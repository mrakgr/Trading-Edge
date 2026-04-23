#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#r "nuget: Argu, 6.2.5"

// For each data/bulk/intraday_10s/{date}.parquet, add cumulative-volume and
// cumulative-trade-count columns (running sum within each ticker), and write
// the result to data/bulk/intraday_10s_cum/{date}.parquet sorted by
// (bucket, ticker). DuckDB's row-group zone maps then let `WHERE bucket = N`
// queries skip most row groups at read time — the same access pattern the
// MiniZinc checkpoint exporter uses.
//
// We compute the cumulative in DuckDB itself (single-day window function is
// cheap — at most ~2700 rows × ~5000 tickers = 13M rows per day, fits easily
// in a few hundred MB). The output file is small (~25 MB/day), so downstream
// queries can glob across data/bulk/intraday_10s_cum/*.parquet.
//
// Running this 70 times instead of once avoids the 500M-row global sort that
// was OOM-killing the all-days approach.

open System
open System.IO
open Argu
open DuckDB.NET.Data

type CliArgs =
    | [<AltCommandLine("-s")>] Start_Date of string
    | [<AltCommandLine("-e")>] End_Date of string
    | [<AltCommandLine("-n")>] Limit of int
    | Force

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Start_Date _ -> "First date (yyyy-MM-dd, inclusive). Default: earliest available."
            | End_Date _ -> "Last date (yyyy-MM-dd, inclusive). Default: latest available."
            | Limit _ -> "Max days to process this run. Default: no cap."
            | Force -> "Rebuild even if output already exists."

let parser = ArgumentParser.Create<CliArgs>(programName = "build_10s_cum.fsx")
let parsed =
    try parser.Parse(fsi.CommandLineArgs |> Array.skip 1, raiseOnUsage = true)
    with :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        exit 1

let startDateOpt = parsed.TryGetResult Start_Date
let endDateOpt = parsed.TryGetResult End_Date
let limitOpt = parsed.TryGetResult Limit
let force = parsed.Contains Force

let inDir = "data/bulk/intraday_10s"
let outDir = "data/bulk/intraday_10s_cum"
Directory.CreateDirectory outDir |> ignore

let buildOne (date: string) : double =
    let inPath = Path.Combine(inDir, $"{date}.parquet")
    let outPath = Path.Combine(outDir, $"{date}.parquet")
    let inEsc = inPath.Replace("'", "''")
    let outEsc = outPath.Replace("'", "''")

    let sql = $"""
COPY (
    SELECT
        date, ticker, bucket, volume, trade_count,
        CAST(SUM(volume)      OVER w AS BIGINT) AS cum_volume,
        CAST(SUM(trade_count) OVER w AS BIGINT) AS cum_trade_count
    FROM read_parquet('{inEsc}')
    WINDOW w AS (
        PARTITION BY ticker ORDER BY bucket
        ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
    )
    ORDER BY bucket, ticker
) TO '{outEsc}' (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3)
"""
    let sw = Diagnostics.Stopwatch.StartNew()
    use conn = new DuckDBConnection("DataSource=:memory:")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    cmd.CommandTimeout <- 0
    cmd.ExecuteNonQuery() |> ignore
    sw.Stop()
    sw.Elapsed.TotalSeconds

let availableDates =
    Directory.GetFiles(inDir, "*.parquet")
    |> Array.map Path.GetFileNameWithoutExtension
    |> Array.sort

let alreadyDone =
    if force then Set.empty
    else
        Directory.GetFiles(outDir, "*.parquet")
        |> Array.map Path.GetFileNameWithoutExtension
        |> Set.ofArray

let inRange (d: string) =
    (match startDateOpt with Some s -> d >= s | None -> true)
    && (match endDateOpt with Some e -> d <= e | None -> true)

let todo =
    availableDates
    |> Array.filter (fun d -> not (alreadyDone.Contains d))
    |> Array.filter inRange
    |> fun arr ->
        match limitOpt with
        | Some n -> arr |> Array.truncate n
        | None -> arr

printfn "input parquets available: %d" availableDates.Length
printfn "already built:            %d" alreadyDone.Count
printfn "to process this run:      %d" todo.Length

if todo.Length = 0 then
    printfn "Nothing to do."
else
    let outerSw = Diagnostics.Stopwatch.StartNew()
    let mutable totalSeconds = 0.0
    for i = 0 to todo.Length - 1 do
        let date = todo.[i]
        try
            let elapsed = buildOne date
            totalSeconds <- totalSeconds + elapsed
            let outSize = FileInfo(Path.Combine(outDir, $"{date}.parquet")).Length
            printfn "[%d/%d] %s  %.1fs  out=%.1f MB"
                (i + 1) todo.Length date elapsed (float outSize / 1e6)
        with ex ->
            printfn "[%d/%d] %s  FAILED: %s" (i + 1) todo.Length date ex.Message
    outerSw.Stop()
    printfn ""
    printfn "Processed %d days in %.1fs (avg %.2fs/day)"
        todo.Length totalSeconds (totalSeconds / float todo.Length)
