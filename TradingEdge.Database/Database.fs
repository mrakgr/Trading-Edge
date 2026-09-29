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
    volume: float
    transactions: int64
}

[<CLIMutable>]
type SplitRow = {
    id: string
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

/// Create and open a DuckDB connection.
/// Caps memory at 6GB so the engine spills to disk rather than letting the OS OOM-kill
/// the process on heavy materializations (e.g. split_adjusted_prices).
let openConnection (dbPath: string) : DuckDBConnection =
    let connectionString = $"Data Source={dbPath}"
    let connection = new DuckDBConnection(connectionString)
    connection.Open()
    use cmd = connection.CreateCommand()
    cmd.CommandText <- "PRAGMA memory_limit='6GB'"
    cmd.ExecuteNonQuery() |> ignore
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

/// Execute a single embedded SQL resource by its filename suffix
/// (e.g. "07_premarket_volume_daily.sql"). Used by subcommands that
/// rebuild just one materialized table without touching the rest.
let executeNamedResource (connection: IDbConnection) (suffix: string) : unit =
    let sql = loadEmbeddedSql suffix
    connection.Execute(sql) |> ignore

/// Refresh views only (fast, call when view definitions change)
let refreshViews (connection: IDbConnection) : unit =
    executeSqlFromFolder connection "sql.schema.views"

/// Materialize all derived tables and views (call after data ingestion)
let materializeAll (connection: IDbConnection) : unit =
    materializeTables connection
    refreshViews connection

// Note: DuckDB is columnar and optimized for bulk loads by default.
// No PRAGMA statements or index manipulation needed.

// ---------------------------------------------------------------------------
// THE RAW-FILE SOURCES — one definition of how each downloaded file becomes rows,
// shared by the v1 ingest (INSERT … ON CONFLICT into trading.db) and the v2 build
// (V2Build.fs: parquet). Research and production must build their data the same way
// (user, 2026-09-29): change a source here and both change together.
// ---------------------------------------------------------------------------

/// Day aggregates (`data/daily_aggregates/*.csv.gz`, one S3 day_aggs file per session). `withFile` adds the
/// source file's session date as `file_date` (v2 uses it to settle a (ticker, date) that two files carry).
let dailyPricesSourceWith (withFile: bool) (fileOrGlob: string) = $"""
        SELECT
            ticker,
            (epoch_ms(0) + to_milliseconds(window_start / 1000000))::DATE as date,
            open, high, low, close, volume, transactions{if withFile then ",\n            strptime(regexp_extract(filename, '(\\d{4}-\\d{2}-\\d{2})\\.csv\\.gz$', 1), '%Y-%m-%d')::DATE AS file_date" else ""}
        FROM read_csv('{fileOrGlob}',{if withFile then " filename = true," else ""}
            columns = {{
                'ticker': 'VARCHAR',
                'volume': 'DOUBLE',
                'open': 'DOUBLE',
                'close': 'DOUBLE',
                'high': 'DOUBLE',
                'low': 'DOUBLE',
                'window_start': 'BIGINT',
                'transactions': 'BIGINT'
            }},
            header = true
        )"""
let dailyPricesSource (fileOrGlob: string) = dailyPricesSourceWith false fileOrGlob

/// The splits mirror (`data/splits.csv`, always the FULL range).
let splitsSource (filePath: string) = $"""
        SELECT
            id,
            ticker,
            execution_date::DATE AS execution_date,
            split_from,
            split_to,
            split_ratio
        FROM read_csv('{filePath}',
            columns = {{
                'id': 'VARCHAR',
                'ticker': 'VARCHAR',
                'execution_date': 'VARCHAR',
                'split_from': 'DOUBLE',
                'split_to': 'DOUBLE',
                'split_ratio': 'DOUBLE'
            }},
            header = true
        )"""

/// The dividends mirror (`data/dividends.csv`, always the FULL range).
let dividendsSource (filePath: string) = $"""
        SELECT
            id,
            ticker,
            ex_dividend_date::DATE AS ex_dividend_date,
            cash_amount,
            CASE WHEN declaration_date = '' THEN NULL ELSE declaration_date::DATE END AS declaration_date,
            CASE WHEN pay_date = '' THEN NULL ELSE pay_date::DATE END AS pay_date,
            frequency,
            dividend_type
        FROM read_csv('{filePath}',
            columns = {{
                'id': 'VARCHAR',
                'ticker': 'VARCHAR',
                'ex_dividend_date': 'VARCHAR',
                'cash_amount': 'DOUBLE',
                'declaration_date': 'VARCHAR',
                'pay_date': 'VARCHAR',
                'frequency': 'INTEGER',
                'dividend_type': 'VARCHAR'
            }},
            header = true
        )"""

/// The reference tickers snapshot (`data/tickers.csv`, RFC-4180-quoted; active AND delisted).
let tickersSource (filePath: string) = $"""
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
        )"""

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
        {dailyPricesSource filePath}
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
        {dailyPricesSource globPattern}
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
    p.Add("id", split.Id)
    p.Add("ticker", split.Ticker)
    p.Add("execution_date", split.ExecutionDate.ToString("yyyy-MM-dd"))
    p.Add("split_from", split.SplitFrom)
    p.Add("split_to", split.SplitTo)
    p.Add("split_ratio", split.SplitRatio)
    p

/// ⚠ Conflict target is `id`, NOT (ticker, execution_date) — see splits.sql.
/// Keying on the date collapsed reverse/forward pairs to one leg.
let private splitUpsertSql = """
    INSERT INTO splits (id, ticker, execution_date, split_from, split_to, split_ratio)
    VALUES ($id, $ticker, $execution_date, $split_from, $split_to, $split_ratio)
    ON CONFLICT(id) DO UPDATE SET
        ticker = excluded.ticker,
        execution_date = excluded.execution_date,
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

/// ⭐ THE FULL-RANGE FILE IS A MIRROR (2026-09-22). Polygon re-keys split and
/// dividend records over time: the same event comes back under a new `id` and
/// the old one is never published again. An append-only upsert on `id` then keeps
/// BOTH — the event stacks (AAPL 2020-08-31 became a 16x two-leg split, which the
/// tape corroboration REJECTED, so the 4:1 was not applied at all; 753 retired
/// split ids, 903 duplicate (ticker, date) pairs against 185 in Polygon's own
/// file). So before the upsert, every id the file no longer carries is deleted.
///
/// ⚠ GUARD: the files are rewritten WHOLE by their download verbs, so a
/// narrow-range download would make this delete the table's history
/// (docs/massive_cli_gotchas.md). Retiring more than `maxRetireFraction` of the
/// table is refused — `backfill-daily` always fetches the full range.
let private maxRetireFraction = 0.10

let private retireAbsentIds (connection: IDbConnection) (table: string) (filePath: string) : int64 =
    let total = connection.ExecuteScalar<int64>($"SELECT COUNT(*) FROM {table}")
    let absent =
        connection.ExecuteScalar<int64>(
            $"SELECT COUNT(*) FROM {table} WHERE id NOT IN (SELECT id FROM read_csv('{filePath}', header = true))")
    if total > 0L && float absent > maxRetireFraction * float total then
        failwithf "%s: the file %s would retire %d of %d ids (> %.0f%%) — a narrow-range download? The splits/dividends files must be FULL RANGE (docs/massive_cli_gotchas.md)."
            table filePath absent total (maxRetireFraction * 100.0)
    connection.Execute($"DELETE FROM {table} WHERE id NOT IN (SELECT id FROM read_csv('{filePath}', header = true))") |> ignore
    absent

/// Bulk ingest splits directly from a CSV file using DuckDB's native CSV reader.
/// The file is a MIRROR: ids it no longer carries are retired first (see
/// `retireAbsentIds`). Returns the number of retired ids.
let ingestSplitsFromCsv (connection: IDbConnection) (filePath: string) : int64 =
    let retired = retireAbsentIds connection "splits" filePath
    let sql = $"""
        INSERT INTO splits (id, ticker, execution_date, split_from, split_to, split_ratio)
        {splitsSource filePath}
        ON CONFLICT(id) DO UPDATE SET
            ticker = excluded.ticker,
            execution_date = excluded.execution_date,
            split_from = excluded.split_from,
            split_to = excluded.split_to,
            split_ratio = excluded.split_ratio
    """
    connection.Execute(sql) |> ignore
    retired

/// Get count of daily prices in database
let getDailyPriceCount (connection: IDbConnection) : int64 =
    connection.ExecuteScalar<int64>("SELECT COUNT(*) FROM daily_prices")

/// Get count of splits in database
let getSplitCount (connection: IDbConnection) : int64 =
    connection.ExecuteScalar<int64>("SELECT COUNT(*) FROM splits")

// --- Dividends ---

/// Bulk ingest dividends directly from a CSV file using DuckDB's native CSV reader.
/// The file is a MIRROR: ids it no longer carries are retired first (see
/// `retireAbsentIds`). Returns the number of retired ids.
let ingestDividendsFromCsv (connection: IDbConnection) (filePath: string) : int64 =
    let retired = retireAbsentIds connection "dividends" filePath
    let sql = $"""
        INSERT INTO dividends (id, ticker, ex_dividend_date, cash_amount, declaration_date, pay_date, frequency, dividend_type)
        {dividendsSource filePath}
        ON CONFLICT(id) DO UPDATE SET
            ticker = excluded.ticker,
            ex_dividend_date = excluded.ex_dividend_date,
            cash_amount = excluded.cash_amount,
            declaration_date = excluded.declaration_date,
            pay_date = excluded.pay_date,
            frequency = excluded.frequency,
            dividend_type = excluded.dividend_type
    """
    connection.Execute(sql) |> ignore
    retired

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
        {tickersSource filePath}
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
        "SELECT id, ticker, execution_date, split_from, split_to, split_ratio FROM splits WHERE ticker = $ticker ORDER BY execution_date DESC",
        {| ticker = ticker |})
    |> Seq.map (fun row -> {
        Id = row.id
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
