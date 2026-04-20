#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#r "nuget: Argu, 6.2.5"

// Per-bucket JSON exporter for the 10s-bar MiniZinc calibration.
//
// For each bucket in the sweep range, query intraday_10s_cum JOIN session_volume_4w
// JOIN ticker_reference JOIN stock_volume_4w (CS/ADRC + $25M ADV universe), and
// write one flat JSON file per bucket under data/minizinc/10s/checkpoint_b{N}.json.
//
// Row shape:
//   { "ticker": "ORCL", "date": "2025-06-12",
//     "cum_volume": 1050000, "cum_transactions": 8400,
//     "avg_session_adj_volume_4w": 9324811.3,
//     "avg_session_transactions_4w": 92311.4,
//     "session_volume_rvol_final": 7.33,
//     "split_factor_today": 1.0 }
//
// Default step is 6 (one minute of 10s buckets) — gives ~388 files over the trading day.
// 366 = 09:31 ET (first minute after the open); 2688 = 15:58 ET (start of the last
// full minute bar before the close).

open System
open System.IO
open System.Text
open System.Globalization
open Argu
open DuckDB.NET.Data

type CliArgs =
    | [<AltCommandLine("-s")>] Start_Bucket of int
    | [<AltCommandLine("-e")>] End_Bucket of int
    | [<AltCommandLine("-k")>] Step of int
    | [<AltCommandLine("-b")>] Bucket of int
    | [<AltCommandLine("-o")>] Output_Dir of string
    | [<AltCommandLine("-d")>] Database of string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Start_Bucket _ -> "First bucket index (inclusive). Default: 366 (09:31 ET)"
            | End_Bucket _ -> "Last bucket index (inclusive). Default: 2688 (15:58:00 ET, start of the last full-minute bar before close)"
            | Step _ -> "Bucket step. Default: 6 (one minute of 10s buckets)"
            | Bucket _ -> "Single bucket shortcut — equivalent to -s N -e N -k 1"
            | Output_Dir _ -> "Output directory. Default: data/minizinc/10s"
            | Database _ -> "DuckDB path. Default: data/trading.db"

let parser = ArgumentParser.Create<CliArgs>(programName = "export_checkpoint_cumulatives_10s.fsx")
let cliArgs = fsi.CommandLineArgs |> Array.skip 1
let parsed =
    try parser.Parse(cliArgs, raiseOnUsage = true)
    with :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        exit 1

// ----- Config -----
let db = parsed.GetResult(Database, defaultValue = "data/trading.db")
let outputDir = parsed.GetResult(Output_Dir, defaultValue = "data/minizinc/10s")
let minAdv = 25_000_000.0

let startB, endB, step =
    match parsed.TryGetResult Bucket with
    | Some b -> b, b, 1
    | None ->
        parsed.GetResult(Start_Bucket, defaultValue = 366),
        parsed.GetResult(End_Bucket, defaultValue = 2688),
        parsed.GetResult(Step, defaultValue = 6)

let buckets = [| for b in startB .. step .. endB -> b |]

let bucketToEt (bucket: int) =
    // bucket 0 = 08:30 ET, 10s increments. 360 buckets/hour.
    let totalSeconds = 30 * 60 + bucket * 10
    let hh = 8 + totalSeconds / 3600
    let mm = (totalSeconds % 3600) / 60
    let ss = totalSeconds % 60
    sprintf "%02d:%02d:%02d" hh mm ss

Directory.CreateDirectory outputDir |> ignore

let conn = new DuckDBConnection(sprintf "Data Source=%s;ACCESS_MODE=READ_ONLY" db)
conn.Open()

printfn "Exporting buckets %d..%d step %d (%d files)" startB endB step buckets.Length
printfn "Output dir: %s" outputDir

let sw = Diagnostics.Stopwatch.StartNew()

for i = 0 to buckets.Length - 1 do
    let bucket = buckets.[i]
    let path = Path.Combine(outputDir, sprintf "checkpoint_b%d.json" bucket)

    let sql = $"""
SELECT
    c.ticker,
    strftime(c.date, '%%Y-%%m-%%d') AS date_str,
    c.cum_volume::BIGINT,
    c.cum_trade_count::BIGINT,
    sv.avg_session_adj_volume_4w,
    sv.avg_session_transactions_4w,
    sv.session_volume_rvol,
    sv.session_adj_volume::DOUBLE / NULLIF(sv.session_raw_volume, 0) AS split_factor_today
FROM intraday_10s_cum c
JOIN session_volume_4w sv ON sv.ticker = c.ticker AND sv.date = c.date
JOIN ticker_reference tr ON tr.ticker = c.ticker AND tr.type IN ('CS','ADRC')
JOIN stock_volume_4w sd ON sd.ticker = c.ticker AND sd.date = c.date
WHERE c.bucket = {bucket}
  AND sd.avg_dollar_volume_4w >= {minAdv}
  AND sd.avg_volume_4w > 0
  AND sv.avg_session_adj_volume_4w IS NOT NULL
  AND sv.avg_session_transactions_4w IS NOT NULL
  AND sv.session_volume_rvol IS NOT NULL
  AND sv.session_raw_volume > 0
ORDER BY c.ticker, c.date
"""

    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    use reader = cmd.ExecuteReader()

    use fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536)
    use w = new StreamWriter(fs)
    w.Write "[\n"
    let mutable first = true
    let mutable rows = 0L
    while reader.Read() do
        let ticker = reader.GetString 0
        let date = reader.GetString 1
        let cv = reader.GetInt64 2
        let ct = reader.GetInt64 3
        let avgAdjVol4w = reader.GetDouble 4
        let avgTxn4w = reader.GetDouble 5
        let volRvolFinal = reader.GetDouble 6
        let splitFactorToday = reader.GetDouble 7
        if not first then w.Write ",\n" else first <- false
        w.Write(
            String.Format(
                CultureInfo.InvariantCulture,
                "  {{\"ticker\": \"{0}\", \"date\": \"{1}\", \"cum_volume\": {2}, \"cum_transactions\": {3}, \"avg_session_adj_volume_4w\": {4:F3}, \"avg_session_transactions_4w\": {5:F3}, \"session_volume_rvol_final\": {6:F6}, \"split_factor_today\": {7:F6}}}",
                ticker, date, cv, ct, avgAdjVol4w, avgTxn4w, volRvolFinal, splitFactorToday))
        rows <- rows + 1L
    w.Write "\n]\n"
    w.Flush()

    if i % 50 = 0 || i = buckets.Length - 1 then
        printfn "  [b%d / %s] %d rows, %.1fs elapsed" bucket (bucketToEt bucket) rows sw.Elapsed.TotalSeconds

sw.Stop()
printfn ""
printfn "Done %d buckets in %.1fs" buckets.Length sw.Elapsed.TotalSeconds
