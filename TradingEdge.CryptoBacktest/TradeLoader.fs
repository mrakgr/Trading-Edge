module TradingEdge.CryptoBacktest.TradeLoader

open System
open System.IO
open DuckDB.NET.Data
open TradingEdge.Simulation.BinanceLoader

// =============================================================================
// Parquet trade loader
// =============================================================================
//
// Per-day parquet schema (written by TradingEdge.CryptoData):
//   price        DOUBLE
//   quantity     DOUBLE
//   timestamp_us BIGINT
//   sign         DOUBLE
//
// We pull rows in timestamp order via DuckDB and pack them into the existing
// Trade struct so the rest of the pipeline (bar builders, etc.) stays
// schema-identical to the spot path.

let private dailyPath (rootDir: string) (symbol: string) (date: DateTime) : string =
    Path.Combine(rootDir, symbol, sprintf "%s-trades-%s.parquet" symbol (date.ToString("yyyy-MM-dd")))

/// Load one symbol-day parquet → Trade[]. Returns empty if file is missing
/// (the symbol wasn't trading that day — pre-listing or post-delisting).
let loadDay (rootDir: string) (symbol: string) (date: DateTime) : Trade[] =
    let path = dailyPath rootDir symbol date
    if not (File.Exists path) then [||]
    else
        use conn = new DuckDBConnection("DataSource=:memory:")
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            sprintf
                "SELECT price, quantity, timestamp_us, sign FROM read_parquet('%s') ORDER BY timestamp_us"
                (path.Replace("'", "''"))
        use reader = cmd.ExecuteReader()
        // Capacity grows amortized-doubling. Fixed 4M preallocation was causing
        // OOM in the limit-fill simulator when loading 7d windows in parallel
        // per (symbol, date) group — most days have far fewer than 4M trades,
        // and the upfront 128 MB per call accumulates across workers.
        let result = ResizeArray<Trade>(capacity = 256_000)
        while reader.Read() do
            result.Add {
                Price = reader.GetDouble 0
                Quantity = reader.GetDouble 1
                TimestampUs = reader.GetInt64 2
                Sign = reader.GetDouble 3
            }
        result.ToArray()

/// Load a date range for one symbol → Trade[], in timestamp order. Skips
/// missing days silently. Concatenates all rows in memory; for the largest
/// symbols (BTCUSDT) over the full 2-year window this is on the order of
/// 5 GB, so callers running the full sweep should iterate symbols, not load
/// the whole universe at once.
let loadRange (rootDir: string) (symbol: string) (startDate: DateTime) (endDate: DateTime) : Trade[] =
    let result = ResizeArray<Trade>(capacity = 16_000_000)
    let mutable d = startDate.Date
    while d <= endDate.Date do
        let day = loadDay rootDir symbol d
        if day.Length > 0 then result.AddRange day
        d <- d.AddDays 1.0
    result.ToArray()

/// List all symbols present under rootDir (one subdirectory per symbol).
let listSymbols (rootDir: string) : string[] =
    if not (Directory.Exists rootDir) then [||]
    else
        Directory.EnumerateDirectories rootDir
        |> Seq.map Path.GetFileName
        |> Seq.toArray
        |> Array.sort

/// First and last date with a parquet file for the given symbol, or None.
let coverage (rootDir: string) (symbol: string) : (DateTime * DateTime) option =
    let dir = Path.Combine(rootDir, symbol)
    if not (Directory.Exists dir) then None
    else
        let dates =
            Directory.EnumerateFiles(dir, sprintf "%s-trades-*.parquet" symbol)
            |> Seq.choose (fun p ->
                let name = Path.GetFileNameWithoutExtension p
                let prefix = sprintf "%s-trades-" symbol
                if name.StartsWith prefix then
                    let s = name.Substring prefix.Length
                    match DateTime.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None) with
                    | true, d -> Some d
                    | _ -> None
                else None)
            |> Seq.toArray
        if dates.Length = 0 then None
        else Some (Array.min dates, Array.max dates)
