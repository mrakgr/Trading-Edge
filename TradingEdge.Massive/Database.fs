module TradingEdge.Database

open System
open System.Data
open System.IO
open System.Reflection
open Dapper
open DuckDB.NET.Data

// Row types for Dapper mapping (matches DuckDB column names)
[<CLIMutable>]
type DailyPriceRow = {
    ticker: string
    date: DateOnly
    ``open``: float
    high: float
    low: float
    close: float
    volume: int64
    transactions: int64
}

[<CLIMutable>]
type SplitRow = {
    ticker: string
    execution_date: DateOnly
    split_from: float
    split_to: float
    split_ratio: float
}

[<CLIMutable>]
type SplitAdjustedPriceRow = {
    ticker: string
    date: DateOnly
    adj_open: float
    adj_high: float
    adj_low: float
    adj_close: float
    adj_volume: int64
}

/// Load embedded SQL resource by name
let private loadEmbeddedSql (resourceName: string) : string =
    let assembly = Assembly.GetExecutingAssembly()
    let fullName =
        assembly.GetManifestResourceNames()
        |> Array.find (fun n -> n.EndsWith(resourceName))

    use stream = assembly.GetManifestResourceStream(fullName)
    use reader = new StreamReader(stream)
    reader.ReadToEnd()

/// Get all embedded SQL resources from a specific folder
let private getEmbeddedSqlFromFolder (folderName: string) : string array =
    let assembly = Assembly.GetExecutingAssembly()
    assembly.GetManifestResourceNames()
    |> Array.filter (fun n -> n.Contains(folderName) && n.EndsWith(".sql"))
    |> Array.sort

/// Create and open a DuckDB connection
let openConnection (dbPath: string) : DuckDBConnection =
    let connectionString = $"Data Source={dbPath}"
    let connection = new DuckDBConnection(connectionString)
    connection.Open()
    connection

/// Initialize the base database schema (tables only)
let initializeSchema (connection: IDbConnection) : unit =
    let assembly = Assembly.GetExecutingAssembly()

    // Executes the sql.
    let executeSql folderName =
        for resourceName in getEmbeddedSqlFromFolder folderName do
            use stream = assembly.GetManifestResourceStream(resourceName)
            use reader = new StreamReader(stream)
            let sql = reader.ReadToEnd()
            connection.Execute(sql) |> ignore

    // Drop deprecated objects that used to store tick/quote data in the DB.
    // Trades now live on disk as Parquet (data/trades/{ticker}/{date}.parquet);
    // quotes are no longer ingested into the DB. These DROPs reclaim space in
    // existing databases that were populated before this migration.
    connection.Execute("DROP VIEW IF EXISTS trades_with_quotes") |> ignore
    connection.Execute("DROP TYPE IF EXISTS trade_side") |> ignore
    connection.Execute("DROP TABLE IF EXISTS trades") |> ignore
    connection.Execute("DROP SEQUENCE IF EXISTS trades_id_seq") |> ignore
    connection.Execute("DROP TABLE IF EXISTS quotes") |> ignore

    // Execute all table schemas (base tables only)
    executeSql "sql.schema.tables"

let private executeSqlFromFolder (connection: IDbConnection) (folderName: string) : unit =
    let assembly = Assembly.GetExecutingAssembly()
    for resourceName in getEmbeddedSqlFromFolder folderName do
        use stream = assembly.GetManifestResourceStream(resourceName)
        use reader = new StreamReader(stream)
        let sql = reader.ReadToEnd()
        connection.Execute(sql) |> ignore

/// Materialize derived tables (slow, call after data ingestion)
let materializeTables (connection: IDbConnection) : unit =
    executeSqlFromFolder connection "sql.schema.materialized"

/// Refresh views only (fast, call when view definitions change)
let refreshViews (connection: IDbConnection) : unit =
    executeSqlFromFolder connection "sql.schema.views"

/// Materialize all derived tables and views (call after data ingestion)
let materializeAll (connection: IDbConnection) : unit =
    materializeTables connection
    refreshViews connection

// Note: DuckDB is columnar and optimized for bulk loads by default.
// No PRAGMA statements or index manipulation needed.

/// Convert DailyPrice to Dapper DynamicParameters
let private toDailyPriceParams (price: DailyPrice) : DynamicParameters =
    let p = DynamicParameters()
    p.Add("ticker", price.Ticker)
    p.Add("date", price.Date.ToString("yyyy-MM-dd"))
    p.Add("open", price.Open)
    p.Add("high", price.High)
    p.Add("low", price.Low)
    p.Add("close", price.Close)
    p.Add("volume", price.Volume)
    p.Add("transactions", price.Transactions)
    p

let private dailyPriceUpsertSql = """
    INSERT INTO daily_prices (ticker, date, open, high, low, close, volume, transactions)
    VALUES ($ticker, $date, $open, $high, $low, $close, $volume, $transactions)
    ON CONFLICT(ticker, date) DO UPDATE SET
        open = excluded.open,
        high = excluded.high,
        low = excluded.low,
        close = excluded.close,
        volume = excluded.volume,
        transactions = excluded.transactions
"""

/// Insert or update a single daily price record
let upsertDailyPrice (connection: IDbConnection) (price: DailyPrice) : int =
    connection.Execute(dailyPriceUpsertSql, toDailyPriceParams price)

/// Insert or update multiple daily price records (legacy row-by-row method)
let upsertDailyPrices (duckDbConn : DuckDBConnection) (prices: DailyPrice array) : int =
   use transaction = duckDbConn.BeginTransaction()
   use cmd = duckDbConn.CreateCommand()
   cmd.Transaction <- transaction
   cmd.CommandText <- dailyPriceUpsertSql
   let pTicker = new DuckDBParameter("ticker", null)
   let pDate = new DuckDBParameter("date", null)
   let pOpen = new DuckDBParameter("open", null)
   let pHigh = new DuckDBParameter("high", null)
   let pLow = new DuckDBParameter("low", null)
   let pClose = new DuckDBParameter("close", null)
   let pVolume = new DuckDBParameter("volume", null)
   let pTransactions = new DuckDBParameter("transactions", null)
   cmd.Parameters.Add(pTicker) |> ignore
   cmd.Parameters.Add(pDate) |> ignore
   cmd.Parameters.Add(pOpen) |> ignore
   cmd.Parameters.Add(pHigh) |> ignore
   cmd.Parameters.Add(pLow) |> ignore
   cmd.Parameters.Add(pClose) |> ignore
   cmd.Parameters.Add(pVolume) |> ignore
   cmd.Parameters.Add(pTransactions) |> ignore

   let mutable count = 0
   for price in prices do
       pTicker.Value <- price.Ticker
       pDate.Value <- price.Date.ToString("yyyy-MM-dd")
       pOpen.Value <- price.Open
       pHigh.Value <- price.High
       pLow.Value <- price.Low
       pClose.Value <- price.Close
       pVolume.Value <- price.Volume
       pTransactions.Value <- price.Transactions
       count <- count + cmd.ExecuteNonQuery()
   transaction.Commit()
   count

/// Bulk ingest daily prices directly from a .csv.gz file using DuckDB's native CSV reader
let ingestDailyPricesFromCsvGz (connection: IDbConnection) (filePath: string) : int64 =
    let sql = $"""
        INSERT INTO daily_prices (ticker, date, open, high, low, close, volume, transactions)
        SELECT 
            ticker,
            (epoch_ms(0) + to_milliseconds(window_start / 1000000))::DATE as date,
            open, high, low, close, volume, transactions
        FROM read_csv('{filePath}',
            columns = {{
                'ticker': 'VARCHAR',
                'volume': 'BIGINT',
                'open': 'DOUBLE',
                'close': 'DOUBLE',
                'high': 'DOUBLE',
                'low': 'DOUBLE',
                'window_start': 'BIGINT',
                'transactions': 'BIGINT'
            }},
            header = true
        )
        ON CONFLICT(ticker, date) DO UPDATE SET
            open = excluded.open,
            high = excluded.high,
            low = excluded.low,
            close = excluded.close,
            volume = excluded.volume,
            transactions = excluded.transactions
    """
    connection.Execute(sql) |> int64

/// Bulk ingest daily prices from multiple .csv.gz files at once using glob pattern
let ingestDailyPricesFromGlob (connection: IDbConnection) (globPattern: string) : int64 =
    let sql = $"""
        INSERT INTO daily_prices (ticker, date, open, high, low, close, volume, transactions)
        SELECT 
            ticker,
            (epoch_ms(0) + to_milliseconds(window_start / 1000000))::DATE as date,
            open, high, low, close, volume, transactions
        FROM read_csv('{globPattern}',
            columns = {{
                'ticker': 'VARCHAR',
                'volume': 'BIGINT',
                'open': 'DOUBLE',
                'close': 'DOUBLE',
                'high': 'DOUBLE',
                'low': 'DOUBLE',
                'window_start': 'BIGINT',
                'transactions': 'BIGINT'
            }},
            header = true
        )
        ON CONFLICT(ticker, date) DO UPDATE SET
            open = excluded.open,
            high = excluded.high,
            low = excluded.low,
            close = excluded.close,
            volume = excluded.volume,
            transactions = excluded.transactions
    """
    connection.Execute(sql) |> int64

/// Convert Split to Dapper DynamicParameters
let private toSplitParams (split: Split) : DynamicParameters =
    let p = DynamicParameters()
    p.Add("ticker", split.Ticker)
    p.Add("execution_date", split.ExecutionDate.ToString("yyyy-MM-dd"))
    p.Add("split_from", split.SplitFrom)
    p.Add("split_to", split.SplitTo)
    p.Add("split_ratio", split.SplitRatio)
    p

let private splitUpsertSql = """
    INSERT INTO splits (ticker, execution_date, split_from, split_to, split_ratio)
    VALUES ($ticker, $execution_date, $split_from, $split_to, $split_ratio)
    ON CONFLICT(ticker, execution_date) DO UPDATE SET
        split_from = excluded.split_from,
        split_to = excluded.split_to,
        split_ratio = excluded.split_ratio
"""

/// Insert or update a single split record
let upsertSplit (connection: IDbConnection) (split: Split) : int =
    connection.Execute(splitUpsertSql, toSplitParams split)

/// Insert or update multiple split records using prepared statement (legacy row-by-row method)
let upsertSplits (connection: IDbConnection) (splits: Split array) : int =
    let duckDbConn = connection :?> DuckDBConnection
    use transaction = duckDbConn.BeginTransaction()
    use cmd = duckDbConn.CreateCommand()
    cmd.Transaction <- transaction
    cmd.CommandText <- splitUpsertSql

    let pTicker = new DuckDBParameter("ticker", null)
    let pExecutionDate = new DuckDBParameter("execution_date", null)
    let pSplitFrom = new DuckDBParameter("split_from", null)
    let pSplitTo = new DuckDBParameter("split_to", null)
    let pSplitRatio = new DuckDBParameter("split_ratio", null)
    cmd.Parameters.Add(pTicker) |> ignore
    cmd.Parameters.Add(pExecutionDate) |> ignore
    cmd.Parameters.Add(pSplitFrom) |> ignore
    cmd.Parameters.Add(pSplitTo) |> ignore
    cmd.Parameters.Add(pSplitRatio) |> ignore

    let mutable count = 0
    for split in splits do
        pTicker.Value <- split.Ticker
        pExecutionDate.Value <- split.ExecutionDate.ToString("yyyy-MM-dd")
        pSplitFrom.Value <- split.SplitFrom
        pSplitTo.Value <- split.SplitTo
        pSplitRatio.Value <- split.SplitRatio
        count <- count + cmd.ExecuteNonQuery()

    transaction.Commit()
    count

/// Bulk ingest splits directly from a CSV file using DuckDB's native CSV reader
let ingestSplitsFromCsv (connection: IDbConnection) (filePath: string) : int64 =
    let sql = $"""
        INSERT INTO splits (ticker, execution_date, split_from, split_to, split_ratio)
        SELECT 
            ticker,
            execution_date::DATE,
            split_from,
            split_to,
            split_ratio
        FROM read_csv('{filePath}',
            columns = {{
                'ticker': 'VARCHAR',
                'execution_date': 'VARCHAR',
                'split_from': 'DOUBLE',
                'split_to': 'DOUBLE',
                'split_ratio': 'DOUBLE'
            }},
            header = true
        )
        ON CONFLICT(ticker, execution_date) DO UPDATE SET
            split_from = excluded.split_from,
            split_to = excluded.split_to,
            split_ratio = excluded.split_ratio
    """
    connection.Execute(sql) |> int64

/// Get count of daily prices in database
let getDailyPriceCount (connection: IDbConnection) : int64 =
    connection.ExecuteScalar<int64>("SELECT COUNT(*) FROM daily_prices")

/// Get count of splits in database
let getSplitCount (connection: IDbConnection) : int64 =
    connection.ExecuteScalar<int64>("SELECT COUNT(*) FROM splits")

// --- Dividends ---

/// Bulk ingest dividends directly from a CSV file using DuckDB's native CSV reader
let ingestDividendsFromCsv (connection: IDbConnection) (filePath: string) : int64 =
    let sql = $"""
        INSERT INTO dividends (ticker, ex_dividend_date, cash_amount, declaration_date, pay_date, frequency, dividend_type)
        SELECT
            ticker,
            ex_dividend_date::DATE,
            cash_amount,
            CASE WHEN declaration_date = '' THEN NULL ELSE declaration_date::DATE END,
            CASE WHEN pay_date = '' THEN NULL ELSE pay_date::DATE END,
            frequency,
            dividend_type
        FROM read_csv('{filePath}',
            columns = {{
                'ticker': 'VARCHAR',
                'ex_dividend_date': 'VARCHAR',
                'cash_amount': 'DOUBLE',
                'declaration_date': 'VARCHAR',
                'pay_date': 'VARCHAR',
                'frequency': 'INTEGER',
                'dividend_type': 'VARCHAR'
            }},
            header = true
        )
        ON CONFLICT(ticker, ex_dividend_date) DO UPDATE SET
            cash_amount = excluded.cash_amount,
            declaration_date = excluded.declaration_date,
            pay_date = excluded.pay_date,
            frequency = excluded.frequency,
            dividend_type = excluded.dividend_type
    """
    connection.Execute(sql) |> int64

/// Get count of dividends in database
let getDividendCount (connection: IDbConnection) : int64 =
    connection.ExecuteScalar<int64>("SELECT COUNT(*) FROM dividends")

// --- Ticker Reference (ETF list) ---

/// Bulk ingest the ETF/ETN reference list from a CSV file using DuckDB's
/// native read_csv. The CSV is RFC-4180-quoted (ETF names can contain commas).
/// Existing rows are upserted on conflict so re-running is idempotent.
let ingestTickersFromCsv (connection: IDbConnection) (filePath: string) : int64 =
    let sql = $"""
        INSERT INTO ticker_reference (ticker, name, type)
        SELECT ticker, name, type
        FROM read_csv('{filePath}',
            columns = {{
                'ticker': 'VARCHAR',
                'name': 'VARCHAR',
                'type': 'VARCHAR'
            }},
            header = true,
            quote = '"',
            escape = '"'
        )
        ON CONFLICT(ticker, type) DO UPDATE SET
            name = excluded.name
    """
    connection.Execute(sql) |> int64

/// Get count of rows in ticker_reference
let getTickerReferenceCount (connection: IDbConnection) : int64 =
    connection.ExecuteScalar<int64>("SELECT COUNT(*) FROM ticker_reference")

/// Get all unique tickers from daily prices
let getTickers (connection: IDbConnection) : string array =
    connection.Query<string>("SELECT DISTINCT ticker FROM daily_prices ORDER BY ticker")
    |> Seq.toArray

/// Get date range for daily prices
let getDateRange (connection: IDbConnection) : (DateTime * DateTime) option =
    let minDate = connection.ExecuteScalar<obj>("SELECT MIN(date) FROM daily_prices WHERE date IS NOT NULL")
    let maxDate = connection.ExecuteScalar<obj>("SELECT MAX(date) FROM daily_prices WHERE date IS NOT NULL")
    if isNull minDate || isNull maxDate then
        None
    else
        let toDateTime (o: obj) =
            match o with
            | :? DateOnly as d -> d.ToDateTime(TimeOnly.MinValue)
            | :? DateTime as d -> d
            | _ -> Convert.ToDateTime(o)
        Some (toDateTime minDate, toDateTime maxDate)

/// Get daily prices for a specific ticker, ordered by date
let getDailyPricesByTicker (connection: IDbConnection) (ticker: string) : DailyPrice array =
    connection.Query<DailyPriceRow>(
        "SELECT ticker, date, open, high, low, close, volume, transactions FROM daily_prices WHERE ticker = $ticker ORDER BY date",
        {| ticker = ticker |})
    |> Seq.map (fun row -> {
        Ticker = row.ticker
        Date = row.date.ToDateTime(TimeOnly.MinValue)
        Open = row.``open``
        High = row.high
        Low = row.low
        Close = row.close
        Volume = row.volume
        Transactions = row.transactions
    })
    |> Seq.toArray

/// Get splits for a specific ticker, ordered by execution date descending
let getSplitsByTicker (connection: IDbConnection) (ticker: string) : Split array =
    connection.Query<SplitRow>(
        "SELECT ticker, execution_date, split_from, split_to, split_ratio FROM splits WHERE ticker = $ticker ORDER BY execution_date DESC",
        {| ticker = ticker |})
    |> Seq.map (fun row -> {
        Ticker = row.ticker
        ExecutionDate = row.execution_date.ToDateTime(TimeOnly.MinValue)
        SplitFrom = row.split_from
        SplitTo = row.split_to
        SplitRatio = row.split_ratio
    })
    |> Seq.toArray

/// Get split-adjusted daily prices for a specific ticker using the split_adjusted_prices view
let getSplitAdjustedPricesByTicker (connection: IDbConnection) (ticker: string) : SplitAdjustedPriceRow array =
    connection.Query<SplitAdjustedPriceRow>(
        "SELECT ticker, date, adj_open, adj_high, adj_low, adj_close, adj_volume FROM split_adjusted_prices WHERE ticker = $ticker ORDER BY date",
        {| ticker = ticker |})
    |> Seq.toArray

/// Get split-adjusted daily prices for a specific ticker within a date range
let getSplitAdjustedPricesByTickerDateRange (connection: IDbConnection) (ticker: string) (startDate: DateOnly) (endDate: DateOnly) : SplitAdjustedPriceRow array =
    connection.Query<SplitAdjustedPriceRow>(
        "SELECT ticker, date, adj_open, adj_high, adj_low, adj_close, adj_volume FROM split_adjusted_prices WHERE ticker = $ticker AND date >= $startDate AND date <= $endDate ORDER BY date",
        {| ticker = ticker; startDate = startDate.ToString("yyyy-MM-dd"); endDate = endDate.ToString("yyyy-MM-dd") |})
    |> Seq.toArray

// --- DOM Indicator ---

[<CLIMutable>]
type DomIndicatorRow = {
    date: DateOnly
    avg_leader_return: float
    avg_laggard_return: float
    n_leaders: int64
    n_laggards: int64
    dom_contribution: float
}

/// Get DOM indicator data
let getDomIndicator (connection: IDbConnection) : DomIndicatorRow array =
    connection.Query<DomIndicatorRow>("SELECT * FROM dom_indicator ORDER BY date")
    |> Seq.toArray

// --- Stocks In Play ---

[<CLIMutable>]
type StockInPlayRow = {
    ticker: string
    date: DateOnly
    adj_open: float
    adj_close: float
    prev_close: float
    gap_pct: float
    range_pct: float
    rvol: float
    avg_dollar_volume_4w: float
    volume: int64
    avg_volume_4w: float
    pre_atr_pct: System.Nullable<float>
    post_atr_pct: System.Nullable<float>
    atr_ratio: System.Nullable<float>
    in_play_score: float
    rank: int64
}

/// Get stocks in play for a date range with customizable filters.
/// `excludeEtfs` filters out tickers present in ticker_reference (ETFs/ETNs).
/// `minAtrRatio` excludes rows where post-event ATR (mean true range / close)
/// collapses to that fraction of pre-event ATR or less (buyout filter).
/// Pass 0.0 to disable.
let getStocksInPlay
    (connection: IDbConnection)
    (startDate: DateTime)
    (endDate: DateTime)
    (minRvol: float)
    (minGapPct: float)
    (minAvgDollarVolume: float)
    (rvolWeight: float)
    (gapWeight: float)
    (excludeEtfs: bool)
    (preWindowDays: int)
    (postWindowDays: int)
    (minAtrRatio: float)
    : StockInPlayRow array =
    connection.Query<StockInPlayRow>(
        "SELECT * FROM stocks_in_play(" +
        "start_date := $startDate::DATE, " +
        "end_date := $endDate::DATE, " +
        "min_rvol := $minRvol, " +
        "min_gap_pct := $minGapPct, " +
        "min_avg_dollar_volume := $minAvgDollarVolume, " +
        "rvol_weight := $rvolWeight, " +
        "gap_weight := $gapWeight, " +
        "exclude_etfs := $excludeEtfs, " +
        "pre_window_days := $preWindowDays, " +
        "post_window_days := $postWindowDays, " +
        "min_atr_ratio := $minAtrRatio" +
        ") ORDER BY date, rank",
        {| startDate = startDate.ToString("yyyy-MM-dd")
           endDate = endDate.ToString("yyyy-MM-dd")
           minRvol = minRvol
           minGapPct = minGapPct
           minAvgDollarVolume = minAvgDollarVolume
           rvolWeight = rvolWeight
           gapWeight = gapWeight
           excludeEtfs = excludeEtfs
           preWindowDays = preWindowDays
           postWindowDays = postWindowDays
           minAtrRatio = minAtrRatio |})
    |> Seq.toArray

// --- Continuation Plays ---

/// One row returned from the continuation_plays SQL macro BEFORE F# chain
/// construction. One row per (SIP breakout, candidate day) pair. All chain
/// logic (rolling max, stop rule, dedup) runs in F# on these raw rows.
[<CLIMutable>]
type private ContinuationPlayRawRow = {
    sip_ticker: string
    sip_breakout_date: DateOnly
    day_date: DateOnly
    day_volume: int64
    day_avg_volume_4w: float
    day_avg_dollar_volume_4w: float
    day_rvol: float
}

[<CLIMutable>]
type ContinuationPlayRow = {
    ticker: string
    breakout_date: DateOnly
    date: DateOnly
    max_rvol_day: DateOnly
    max_rvol: float
    days_since_max_rvol_day: int64
    rvol: float
    volume: int64
    avg_volume_4w: float
    avg_dollar_volume_4w: float
}

/// Walk a single pre-sorted chain (one SIP breakout's 15-day forward window)
/// and emit continuation rows using the rolling-max rule. State = (runningMax,
/// runningMaxDate, daysSince, stopped). A new max resets daysSince to 0; a
/// non-max day increments it. The chain terminates after the first day whose
/// rvol drops below `minRvolFraction * runningMax` (that day is included).
let private buildChain
    (minRvolFraction: float)
    (rawChain: ContinuationPlayRawRow array)
    : ContinuationPlayRow array =
    // Using mapFold with Option results so stopped days are dropped cleanly.
    let mapped, _ =
        rawChain
        |> Array.mapFold
            (fun (runningMax: float, runningMaxDate: DateOnly, daysSince: int, stopped: bool) raw ->
                if stopped then
                    None, (runningMax, runningMaxDate, daysSince, stopped)
                else
                    // First row always acts as a new max (it's the SIP breakout day).
                    let isFirst = runningMax = System.Double.NegativeInfinity
                    let isNewMax = isFirst || raw.day_rvol > runningMax
                    let newMax = if isNewMax then raw.day_rvol else runningMax
                    let newMaxDate = if isNewMax then raw.day_date else runningMaxDate
                    let newDaysSince = if isNewMax then 0 else daysSince + 1
                    let fails = not isNewMax && raw.day_rvol < minRvolFraction * runningMax
                    let row = {
                        ticker = raw.sip_ticker
                        breakout_date = raw.sip_breakout_date
                        date = raw.day_date
                        max_rvol_day = newMaxDate
                        max_rvol = newMax
                        days_since_max_rvol_day = int64 newDaysSince
                        rvol = raw.day_rvol
                        volume = raw.day_volume
                        avg_volume_4w = raw.day_avg_volume_4w
                        avg_dollar_volume_4w = raw.day_avg_dollar_volume_4w
                    }
                    Some row, (newMax, newMaxDate, newDaysSince, fails))
            (System.Double.NegativeInfinity, DateOnly.MinValue, 0, false)
    mapped |> Array.choose id

/// Build per-ticker-per-day continuation rows from raw SQL rows. Steps:
///   1. Group by (sip_ticker, sip_breakout_date) to get per-chain candidate lists.
///   2. Run the rolling-max + stop-rule fold per chain.
///   3. Dedup across chains: for each (ticker, date), keep the row whose source
///      chain has the LATEST breakout_date among chains that reached that day.
///      A later breakout "resets the clock" on its own day, so days inside
///      multiple overlapping chains are labeled relative to the most recent
///      breakout event.
///   4. Sort deterministically by (breakout_date, ticker, date).
let private buildContinuationChains
    (minRvolFraction: float)
    (raws: ContinuationPlayRawRow array)
    : ContinuationPlayRow array =
    raws
    |> Array.groupBy (fun r -> (r.sip_ticker, r.sip_breakout_date))
    |> Array.collect (fun (_, group) ->
        // Group is already sorted by day_date because the SQL ORDER BY matches.
        buildChain minRvolFraction group)
    |> Array.groupBy (fun r -> (r.ticker, r.date))
    |> Array.map (fun (_, rows) ->
        rows |> Array.maxBy (fun r -> r.breakout_date))
    |> Array.sortBy (fun r -> (r.breakout_date, r.ticker, r.date))

/// For each breakout in the SIP query window, return continuation chain rows
/// using the rolling-max rule. Chain walks forward up to `maxHorizonDays`
/// calendar days from the breakout; a new max resets `days_since_max_rvol_day`
/// to 0; the chain stops after the first day whose RVOL drops below
/// `minRvolFraction * running_max`. Duplicate `(ticker, date)` rows across
/// overlapping chains are collapsed to the LATEST-breakout source (later
/// breakouts reset the attention cycle on their own day).
let getContinuationPlays
    (connection: IDbConnection)
    (startDate: DateTime)
    (endDate: DateTime)
    (minRvol: float)
    (minGapPct: float)
    (minAvgDollarVolume: float)
    (rvolWeight: float)
    (gapWeight: float)
    (excludeEtfs: bool)
    (preWindowDays: int)
    (postWindowDays: int)
    (minAtrRatio: float)
    (minRvolFraction: float)
    (maxHorizonDays: int)
    : ContinuationPlayRow array =
    let raws =
        connection.Query<ContinuationPlayRawRow>(
            "SELECT * FROM continuation_plays(" +
            "start_date := $startDate::DATE, " +
            "end_date := $endDate::DATE, " +
            "min_rvol := $minRvol, " +
            "min_gap_pct := $minGapPct, " +
            "min_avg_dollar_volume := $minAvgDollarVolume, " +
            "rvol_weight := $rvolWeight, " +
            "gap_weight := $gapWeight, " +
            "exclude_etfs := $excludeEtfs, " +
            "pre_window_days := $preWindowDays, " +
            "post_window_days := $postWindowDays, " +
            "min_atr_ratio := $minAtrRatio, " +
            "max_horizon_days := $maxHorizonDays" +
            ") ORDER BY sip_ticker, sip_breakout_date, day_date",
            {| startDate = startDate.ToString("yyyy-MM-dd")
               endDate = endDate.ToString("yyyy-MM-dd")
               minRvol = minRvol
               minGapPct = minGapPct
               minAvgDollarVolume = minAvgDollarVolume
               rvolWeight = rvolWeight
               gapWeight = gapWeight
               excludeEtfs = excludeEtfs
               preWindowDays = preWindowDays
               postWindowDays = postWindowDays
               minAtrRatio = minAtrRatio
               maxHorizonDays = maxHorizonDays |})
        |> Seq.toArray
    buildContinuationChains minRvolFraction raws

// --- Intraday Prices ---

/// Bulk ingest minute-level intraday prices from JSON files using glob pattern
let ingestIntradayMinuteFromGlob (connection: IDbConnection) (globPattern: string) : int64 =
    let sql = $"""
        INSERT INTO intraday_prices_minute (ticker, timestamp, open, high, low, close, volume, vwap, transactions)
        SELECT
            r.ticker,
            epoch_ms(bar.t),
            bar.o, bar.h, bar.l, bar.c,
            bar.v,
            bar.vw,
            bar.n
        FROM read_json('{globPattern}') r,
        UNNEST(r.results) AS t(bar)
        WHERE bar.t IS NOT NULL
        ON CONFLICT(ticker, timestamp) DO UPDATE SET
            open = excluded.open,
            high = excluded.high,
            low = excluded.low,
            close = excluded.close,
            volume = excluded.volume,
            vwap = excluded.vwap,
            transactions = excluded.transactions
    """
    connection.Execute(sql) |> int64

/// Bulk ingest second-level intraday prices from JSON files using glob pattern
let ingestIntradaySecondFromGlob (connection: IDbConnection) (globPattern: string) : int64 =
    let sql = $"""
        INSERT INTO intraday_prices_second (ticker, timestamp, open, high, low, close, volume, vwap, transactions)
        SELECT
            r.ticker,
            epoch_ms(bar.t),
            bar.o, bar.h, bar.l, bar.c,
            bar.v,
            bar.vw,
            bar.n
        FROM read_json('{globPattern}') r,
        UNNEST(r.results) AS t(bar)
        WHERE bar.t IS NOT NULL
        ON CONFLICT(ticker, timestamp) DO UPDATE SET
            open = excluded.open,
            high = excluded.high,
            low = excluded.low,
            close = excluded.close,
            volume = excluded.volume,
            vwap = excluded.vwap,
            transactions = excluded.transactions
    """
    connection.Execute(sql) |> int64

/// Get count of minute-level intraday prices in database
let getIntradayMinuteCount (connection: IDbConnection) : int64 =
    connection.ExecuteScalar<int64>("SELECT COUNT(*) FROM intraday_prices_minute")

/// Get count of second-level intraday prices in database
let getIntradaySecondCount (connection: IDbConnection) : int64 =
    connection.ExecuteScalar<int64>("SELECT COUNT(*) FROM intraday_prices_second")

// --- Intraday Price Queries ---

[<CLIMutable>]
type IntradayPriceRow = {
    ticker: string
    timestamp: DateTime
    ``open``: float
    high: float
    low: float
    close: float
    volume: float
    vwap: Nullable<float>
    transactions: Nullable<int>
}

/// Get minute-level intraday prices for a ticker on a specific date
let getIntradayMinuteByTickerDate (connection: IDbConnection) (ticker: string) (date: DateTime) : IntradayPriceRow array =
    connection.Query<IntradayPriceRow>(
        """SELECT ticker, timestamp, open, high, low, close, volume, vwap, transactions 
           FROM intraday_prices_minute 
           WHERE ticker = $ticker AND CAST(timestamp AS DATE) = $date 
           ORDER BY timestamp""",
        {| ticker = ticker; date = date.ToString("yyyy-MM-dd") |})
    |> Seq.toArray

/// Get second-level intraday prices for a ticker on a specific date
let getIntradaySecondByTickerDate (connection: IDbConnection) (ticker: string) (date: DateTime) : IntradayPriceRow array =
    connection.Query<IntradayPriceRow>(
        """SELECT ticker, timestamp, open, high, low, close, volume, vwap, transactions 
           FROM intraday_prices_second 
           WHERE ticker = $ticker AND CAST(timestamp AS DATE) = $date 
           ORDER BY timestamp""",
        {| ticker = ticker; date = date.ToString("yyyy-MM-dd") |})
    |> Seq.toArray

