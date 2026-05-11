module TradingEdge.CryptoBacktest.WindowLoader

open System
open System.IO
open System.Collections.Generic
open DuckDB.NET.Data

// =============================================================================
// Targeted trade-window loader for the taker-fill simulator.
// =============================================================================
//
// Unit of work: one (symbol, UTC date). The runner walks the trip-set's unique
// (symbol, date) pairs and asks the loader for that pair's matching trades.
//
// Per (symbol, date) query pattern:
//   1. Compute min/max of the day's window bounds in F#.
//   2. Issue ONE DuckDB query against ONE parquet (this symbol's parquet for
//      this date) with a constant-bound BETWEEN filter:
//        WHERE timestamp_us BETWEEN <min_start_us> AND <max_end_us>
//      DuckDB pushes this into the parquet scan; the parquet's per-row-group
//      timestamp_us min/max stats skip the unrelated row groups inside the file.
//   3. Bucket the returned rows into per-window arrays via a Sorted scan
//      in F#: each window contributes a contiguous slice of the result set.
//
// Why constant-bound BETWEEN on a SINGLE file rather than range-join on a glob:
//   - range-join through DuckDB blocks predicate pushdown — DuckDB has to read
//     every row of every selected parquet (~9s/file on /mnt/d at ~10GB/file).
//   - wildcard globs force DuckDB to open every file's footer just to discover
//     which row groups exist (~150ms/file × 730 files = 110s overhead).
//   - explicit single-file + constant predicate uses the row-group stats and
//     skips to the relevant row groups (~50-150ms per day with a few windows).
//
// Memory profile: each loader call holds ONE day's matching trades in memory.
// For DonchianScalp's 3s windows on a busy symbol, that's typically a few KB
// to a few MB per day. With per-symbol-parallelism N, peak live data is
// N × (one day's windows). Bounded.

[<Struct>]
type TradeRow = {
    TimestampUs: int64
    Price: float
    Quantity: float
    /// +1.0 = buyer-aggressive (uptick fill). -1.0 = seller-aggressive.
    Sign: float
}

/// One window: a (trip_idx, side_kind, start_us, end_us) request.
/// SideKind is "entry" or "exit".
[<Struct>]
type WindowSpec = {
    TripIdx: int
    SideKind: string
    StartUs: int64
    EndUs: int64
}

/// Key for grouping returned trades back into per-(trip, side) bins.
[<Struct>]
type WindowKey = {
    TripIdx: int
    SideKind: string
}

let private parquetPath (rootDir: string) (symbol: string) (date: DateTime) : string =
    Path.Combine(rootDir, symbol, sprintf "%s-trades-%s.parquet" symbol (date.ToString("yyyy-MM-dd")))

let dateOfUs (us: int64) : DateTime =
    DateTimeOffset.FromUnixTimeMilliseconds(us / 1000L).UtcDateTime.Date

/// Bucket a list of windows into the dates they touch. A window whose start
/// and end fall on the same UTC date contributes to one date; one that
/// straddles midnight contributes to two (rare with default 3s windows on
/// 1m-bar signals — at most ~1% of trips).
let bucketWindowsByDate
    (windows: WindowSpec[])
    : Dictionary<DateTime, ResizeArray<WindowSpec>> =
    let buckets = Dictionary<DateTime, ResizeArray<WindowSpec>>()
    let put (d: DateTime) (w: WindowSpec) =
        match buckets.TryGetValue d with
        | true, lst -> lst.Add w
        | false, _ ->
            let lst = ResizeArray<WindowSpec>(8)
            lst.Add w
            buckets.[d] <- lst
    for w in windows do
        let dStart = dateOfUs w.StartUs
        let dEnd   = dateOfUs w.EndUs
        put dStart w
        if dStart <> dEnd then put dEnd w
    buckets

/// Load one (symbol, date) → trades bucketed per window key. Each window in
/// `windowsForDay` produces its own ResizeArray<TradeRow> in the result, sorted
/// by timestamp. Empty (missing key) means no matching trades — caller should
/// treat as scratched-by-liquidity at the simulator level.
///
/// Returns an empty Dictionary if the parquet file is missing (the symbol
/// wasn't trading that day, e.g. pre-listing or post-delisting).
let loadDayWindows
    (rootDir: string)
    (symbol: string)
    (date: DateTime)
    (windowsForDay: ResizeArray<WindowSpec>)
    : Dictionary<WindowKey, ResizeArray<TradeRow>> =
    let result = Dictionary<WindowKey, ResizeArray<TradeRow>>(windowsForDay.Count)
    let path = parquetPath rootDir symbol date
    if not (File.Exists path) || windowsForDay.Count = 0 then
        result
    else
        // Constant min/max bounds for the day's union of windows. The parquet
        // scan needs ONE pair of literal bounds to enable pushdown.
        let mutable minStart = Int64.MaxValue
        let mutable maxEnd = Int64.MinValue
        for w in windowsForDay do
            if w.StartUs < minStart then minStart <- w.StartUs
            if w.EndUs > maxEnd then maxEnd <- w.EndUs

        use conn = new DuckDBConnection("DataSource=:memory:")
        conn.Open()
        let sql =
            sprintf
                "SELECT timestamp_us, price, quantity, sign
                 FROM read_parquet('%s')
                 WHERE timestamp_us BETWEEN %d AND %d
                 ORDER BY timestamp_us"
                (path.Replace("'", "''"))
                minStart
                maxEnd
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql

        // Pre-allocate ResizeArrays for every requested window so per-row
        // bucketing is a dictionary lookup + Add (no allocation in the hot loop).
        for w in windowsForDay do
            let key = { TripIdx = w.TripIdx; SideKind = w.SideKind }
            if not (result.ContainsKey key) then
                result.[key] <- ResizeArray<TradeRow>(32)

        use reader = cmd.ExecuteReader()
        // Sort windows by StartUs for a simple linear-scan bucketer: each
        // returned trade gets matched against every "live" window whose
        // [StartUs, EndUs] contains the trade's timestamp. With ~2-10 windows
        // per day per symbol the linear scan is cheaper than maintaining an
        // interval tree.
        let sortedWindows =
            windowsForDay
            |> Seq.sortBy (fun w -> w.StartUs)
            |> Seq.toArray
        while reader.Read() do
            let ts = reader.GetInt64 0
            let trade = {
                TimestampUs = ts
                Price = reader.GetDouble 1
                Quantity = reader.GetDouble 2
                Sign = reader.GetDouble 3
            }
            for w in sortedWindows do
                if ts >= w.StartUs && ts <= w.EndUs then
                    let key = { TripIdx = w.TripIdx; SideKind = w.SideKind }
                    result.[key].Add trade

        result
