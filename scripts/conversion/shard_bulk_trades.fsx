#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#r "nuget: Argu, 6.2.5"
#r "nuget: FSharp.SystemTextJson, 1.4.36"

// Given a setup list (array of {ticker, date, ...}), shard the bulk trades
// parquets at data/bulk/trades/{date}.parquet into per-(ticker, date) parquets
// at data/trades/{ticker}/{date}.parquet, in the narrow schema that
// TradingEdge.Orb.TradeLoader expects:
//   participant_timestamp BIGINT
//   sip_timestamp         BIGINT
//   price                 DOUBLE
//   size                  DOUBLE
//   conditions            INTEGER[]
//
// For each unique date in the setup list:
//   1. Open data/bulk/trades/{date}.parquet (if present).
//   2. For each needed ticker on that date:
//        COPY (SELECT narrow fields FROM bulk WHERE ticker = ?) TO 'data/trades/{ticker}/{date}.parquet'
//      Skips if the output file already exists (resume-aware) unless --force.
//
// Processes dates in parallel up to --parallelism. DuckDB's own scan of a
// single-day bulk file is itself multithreaded, so keep parallelism small
// to avoid oversubscribing CPUs + disk.

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Collections.Generic
open Argu
open DuckDB.NET.Data

type CliArgs =
    | [<Mandatory; AltCommandLine("-i")>] Input of string
    | [<AltCommandLine("-t")>] Bulk_Dir of string
    | [<AltCommandLine("-o")>] Output_Dir of string
    | [<AltCommandLine("-p")>] Parallelism of int
    | Force

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Input _ -> "Setup list JSON, array of {ticker, date, ...}."
            | Bulk_Dir _ -> "Bulk parquet directory. Default: data/bulk/trades"
            | Output_Dir _ -> "Output per-ticker root. Default: data/trades"
            | Parallelism _ -> "Max parallel dates. Default: 2"
            | Force -> "Rebuild even if output already exists."

let parser = ArgumentParser.Create<CliArgs>(programName = "shard_bulk_trades.fsx")
let parsed =
    try parser.Parse(fsi.CommandLineArgs |> Array.skip 1, raiseOnUsage = true)
    with :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        exit 1

let inPath = parsed.GetResult Input
let bulkDir = parsed.GetResult(Bulk_Dir, defaultValue = "data/bulk/trades")
let outDir = parsed.GetResult(Output_Dir, defaultValue = "data/trades")
let parallelism = parsed.GetResult(Parallelism, defaultValue = 2)
let force = parsed.Contains Force

type Setup = {
    [<JsonPropertyName "ticker">] Ticker: string
    [<JsonPropertyName "date">]   Date: string
}

let setups =
    let bytes = File.ReadAllBytes inPath
    JsonSerializer.Deserialize<Setup[]>(bytes, JsonSerializerOptions())

printfn "Loaded %d setup rows from %s" setups.Length inPath

let byDate =
    setups
    |> Array.groupBy (fun s -> s.Date)
    |> Array.sortBy fst

printfn "Unique dates: %d" byDate.Length

Directory.CreateDirectory outDir |> ignore

let shardOneDate (date: string) (tickers: string[]) : struct (int * int * int * double) =
    // Returns (nNeeded, nWritten, nSkipped, elapsedSeconds)
    let bulkPath = Path.Combine(bulkDir, $"{date}.parquet")
    if not (File.Exists bulkPath) then
        struct (tickers.Length, 0, 0, 0.0)
    else
        let sw = Diagnostics.Stopwatch.StartNew()
        let mutable nWritten = 0
        let mutable nSkipped = 0
        // One DuckDB connection per date — DuckDB scans the bulk file once
        // and answers many per-ticker queries cheaply because the ticker
        // predicate prunes to a small slice.
        use conn = new DuckDBConnection("DataSource=:memory:")
        conn.Open()
        for ticker in tickers do
            let outTickerDir = Path.Combine(outDir, ticker)
            Directory.CreateDirectory outTickerDir |> ignore
            let outPath = Path.Combine(outTickerDir, $"{date}.parquet")
            if (not force) && File.Exists outPath then
                nSkipped <- nSkipped + 1
            else
                let safeTicker = ticker.Replace("'", "''")
                let bulkEsc = bulkPath.Replace("'", "''")
                let outEsc = outPath.Replace("'", "''")
                let sql = $"""
COPY (
    SELECT
        participant_timestamp,
        sip_timestamp,
        price,
        size::DOUBLE AS size,
        CAST(conditions AS INTEGER[]) AS conditions
    FROM read_parquet('{bulkEsc}')
    WHERE ticker = '{safeTicker}'
) TO '{outEsc}' (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3)
"""
                use cmd = conn.CreateCommand()
                cmd.CommandText <- sql
                cmd.CommandTimeout <- 0
                cmd.ExecuteNonQuery() |> ignore
                nWritten <- nWritten + 1
        sw.Stop()
        struct (tickers.Length, nWritten, nSkipped, sw.Elapsed.TotalSeconds)

printfn "Parallelism: %d" parallelism

let overallSw = Diagnostics.Stopwatch.StartNew()
let nDates = byDate.Length
let mutable nDone = 0
let mutable totalNeeded = 0
let mutable totalWritten = 0
let mutable totalSkipped = 0
let logLock = obj()

let opts = System.Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = parallelism)
System.Threading.Tasks.Parallel.ForEach(byDate, opts, fun (date, xs) ->
    let tickers = xs |> Array.map (fun s -> s.Ticker) |> Array.distinct
    let struct (nNeeded, nWritten, nSkipped, elapsed) = shardOneDate date tickers
    lock logLock (fun () ->
        nDone <- nDone + 1
        totalNeeded <- totalNeeded + nNeeded
        totalWritten <- totalWritten + nWritten
        totalSkipped <- totalSkipped + nSkipped
        let wall = overallSw.Elapsed.TotalSeconds
        let eta = wall * float (nDates - nDone) / float (max 1 nDone)
        printfn "[%d/%d] %s  %3d tickers (%3d written, %3d skipped)  %.1fs  | wall=%.0fs eta=%.0fs"
            nDone nDates date nNeeded nWritten nSkipped elapsed wall eta)
) |> ignore

overallSw.Stop()
printfn ""
printfn "Done. needed=%d written=%d skipped=%d in %.1fs"
    totalNeeded totalWritten totalSkipped overallSw.Elapsed.TotalSeconds
