module TradingEdge.CryptoBacktest.WindowLoader

open System
open System.IO
open System.Collections.Generic
open DuckDB.NET.Data

// =============================================================================
// Targeted trade-window loader for the taker-fill simulator.
// =============================================================================
//
// For each trip we only need two short windows (one at EntryUs, one at ExitUs)
// of taker trades — by default 3 seconds each. Streaming whole days through
// the simulator (as LimitFillSim does) is wasted I/O for the taker model.
//
// Per-symbol query pattern:
//   1. Build a temp `windows` table from the F#-supplied (trip_idx, side_kind,
//      start_us, end_us) tuples via the DuckDB appender.
//   2. Range-join against read_parquet('<root>/<symbol>/<symbol>-trades-*.parquet')
//      WHERE timestamp_us BETWEEN windows.start_us AND windows.end_us.
//   3. DuckDB pushes the range predicate into the parquet scan; row-group
//      min/max statistics on timestamp_us skip irrelevant blocks.
//   4. Results are bucketed per (trip_idx, side_kind) into ResizeArray<TradeRow>.

[<Struct>]
type TradeRow = {
    TimestampUs: int64
    Price: float
    Quantity: float
    /// +1.0 = buyer-aggressive (uptick fill; equivalent to isBuyerMaker=false).
    /// -1.0 = seller-aggressive.
    Sign: float
}

/// Key for grouping returned trades back into per-(trip, side) bins.
[<Struct>]
type WindowKey = {
    TripIdx: int
    SideKind: string   // "entry" or "exit"
}

type SymbolWindows = {
    Symbol: string
    /// Trades grouped by (trip_idx, side_kind). Sorted by TimestampUs per window.
    /// Missing keys mean no trades fell in that window — caller should treat as
    /// empty (which the taker simulator will scratch on n_min).
    ByWindow: Dictionary<WindowKey, ResizeArray<TradeRow>>
}

let private parquetPath (rootDir: string) (symbol: string) (date: DateTime) : string =
    Path.Combine(rootDir, symbol, sprintf "%s-trades-%s.parquet" symbol (date.ToString("yyyy-MM-dd")))

let private dateOfUs (us: int64) : DateTime =
    DateTimeOffset.FromUnixTimeMilliseconds(us / 1000L).UtcDateTime.Date

/// Compute the set of UTC dates that the window specs collectively touch.
/// Each window has a start and end; a window that straddles midnight contributes
/// two dates. Returns the deduplicated, sorted set.
let private windowDates (windowSpecs: (int * string * int64 * int64)[]) : DateTime[] =
    let set = HashSet<DateTime>()
    for (_, _, startUs, endUs) in windowSpecs do
        set.Add(dateOfUs startUs) |> ignore
        set.Add(dateOfUs endUs) |> ignore
    set |> Seq.sort |> Seq.toArray

/// One DuckDB query per symbol. `windowSpecs` is the full list of
/// (trip_idx, side_kind, start_us, end_us) tuples for that symbol. Returns
/// trades grouped per (trip_idx, side_kind) window.
///
/// If the symbol directory doesn't exist, returns an empty SymbolWindows.
let loadSymbolWindows
    (rootDir: string)
    (symbol: string)
    (windowSpecs: (int * string * int64 * int64)[])
    : SymbolWindows =
    let result = Dictionary<WindowKey, ResizeArray<TradeRow>>(windowSpecs.Length)
    let symDir = Path.Combine(rootDir, symbol)
    if not (Directory.Exists symDir) || windowSpecs.Length = 0 then
        { Symbol = symbol; ByWindow = result }
    else
        use conn = new DuckDBConnection("DataSource=:memory:")
        conn.Open()

        // Create the temp windows table.
        do
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                "CREATE TEMP TABLE windows (
                    trip_idx   INTEGER,
                    side_kind  VARCHAR,
                    start_us   BIGINT,
                    end_us     BIGINT
                )"
            cmd.ExecuteNonQuery() |> ignore

        // Bulk-insert window specs via the appender.
        do
            use appender = conn.CreateAppender("windows")
            for (tripIdx, sideKind, startUs, endUs) in windowSpecs do
                let row = appender.CreateRow()
                row.AppendValue(Nullable tripIdx) |> ignore
                row.AppendValue(sideKind) |> ignore
                row.AppendValue(Nullable startUs) |> ignore
                row.AppendValue(Nullable endUs) |> ignore
                row.EndRow()
            appender.Close()

        // Narrow the file list to only the parquets whose date matches a
        // window. A wildcard glob (BTCUSDT-trades-*.parquet) forces DuckDB to
        // open metadata for every file in the directory (730+ for a symbol
        // with 2 years of data) — that's >100s of overhead even when each
        // window scans 0.07s of actual data.
        let dates = windowDates windowSpecs
        let existingFiles =
            dates
            |> Array.map (fun d -> parquetPath rootDir symbol d)
            |> Array.filter File.Exists
        if existingFiles.Length = 0 then
            { Symbol = symbol; ByWindow = result }
        else
        let fileListSql =
            existingFiles
            |> Array.map (fun p -> sprintf "'%s'" (p.Replace("'", "''")))
            |> String.concat ", "

        // Range-join. timestamp_us BETWEEN start_us AND end_us is inclusive on
        // both ends — that's intentional, the design doc's W_max bound is a
        // hard cap.
        let sql =
            sprintf
                "SELECT
                    w.trip_idx,
                    w.side_kind,
                    t.timestamp_us,
                    t.price,
                    t.quantity,
                    t.sign
                 FROM read_parquet([%s]) t
                 JOIN windows w
                   ON t.timestamp_us BETWEEN w.start_us AND w.end_us
                 ORDER BY w.trip_idx, w.side_kind, t.timestamp_us"
                fileListSql

        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        use reader = cmd.ExecuteReader()
        while reader.Read() do
            let key = { TripIdx = reader.GetInt32 0; SideKind = reader.GetString 1 }
            let trade = {
                TimestampUs = reader.GetInt64 2
                Price = reader.GetDouble 3
                Quantity = reader.GetDouble 4
                Sign = reader.GetDouble 5
            }
            match result.TryGetValue key with
            | true, lst -> lst.Add trade
            | false, _ ->
                let lst = ResizeArray<TradeRow>(64)
                lst.Add trade
                result.[key] <- lst

        { Symbol = symbol; ByWindow = result }
