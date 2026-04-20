#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#r "nuget: Argu, 6.2.5"

// Direct DuckDB -> .dzn exporter, bypassing the per-bucket JSON intermediate.
//
// The old pipeline was: (export_checkpoint_cumulatives_10s.fsx → JSON per bucket)
// then (convert_checkpoint_to_dzn.fsx → DZN per bucket). Both scripts walked
// every bucket with a fresh SQL statement: 2*2323 statements, each with its
// own join cost. Slow and redundant.
//
// This script does it in one pass. The intraday_10s_cum view is physically
// sorted by (bucket, ticker, date) inside each per-day parquet so filtering
// WHERE bucket BETWEEN ... yields a contiguous range scan. We compute the
// RVOL ratios in SQL (vr = cum_volume / raw_avg_4w, tr = cum_trades /
// txn_avg_4w) and stream rows in bucket-major order. One DZN file per bucket
// is produced as we pass each boundary.

open System
open System.IO
open System.Text
open System.Globalization
open System.Diagnostics
open Argu
open DuckDB.NET.Data

type CliArgs =
    | [<AltCommandLine("-d")>] Database of string
    | [<AltCommandLine("-s")>] Start_Bucket of int
    | [<AltCommandLine("-e")>] End_Bucket of int
    | [<AltCommandLine("-k")>] Step of int
    | [<AltCommandLine("-b")>] Bucket of int
    | [<AltCommandLine("-o")>] Output_Dir of string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Database _ -> "DuckDB path. Default: data/trading.db"
            | Start_Bucket _ -> "First bucket (inclusive). Default: 366 (09:31 ET)"
            | End_Bucket _ -> "Last bucket (inclusive). Default: 2688 (15:58:00 ET)"
            | Step _ -> "Bucket step. Default: 1 (every bucket)"
            | Bucket _ -> "Single bucket shortcut (equivalent to -s N -e N -k 1)"
            | Output_Dir _ -> "Output dir. Default: data/minizinc/10s"

let parser = ArgumentParser.Create<CliArgs>(programName = "export_checkpoints_dzn.fsx")
let parsed =
    try parser.Parse(fsi.CommandLineArgs |> Array.skip 1, raiseOnUsage = true)
    with :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        exit 1

let db = parsed.GetResult(Database, defaultValue = "data/trading.db")
let outputDir = parsed.GetResult(Output_Dir, defaultValue = "data/minizinc/10s")
let minAdv = 25_000_000.0

let startB, endB, step =
    match parsed.TryGetResult Bucket with
    | Some b -> b, b, 1
    | None ->
        parsed.GetResult(Start_Bucket, defaultValue = 366),
        parsed.GetResult(End_Bucket, defaultValue = 2688),
        parsed.GetResult(Step, defaultValue = 1)

let bucketToEt (bucket: int) =
    let totalSeconds = 30 * 60 + bucket * 10
    let hh = 8 + totalSeconds / 3600
    let mm = (totalSeconds % 3600) / 60
    let ss = totalSeconds % 60
    sprintf "%02d:%02d:%02d" hh mm ss

// Which buckets to emit — if step > 1, filter the contiguous scan to just
// the target indices.
let targetBuckets =
    System.Collections.Generic.HashSet<int>(
        seq { for b in startB .. step .. endB -> b })

Directory.CreateDirectory outputDir |> ignore

let conn = new DuckDBConnection(sprintf "Data Source=%s;ACCESS_MODE=READ_ONLY" db)
conn.Open()

// One giant scan, ordered by bucket then ticker/date. Selecting pre-computed
// RVOL ratios directly:
//   vr = cum_volume / (avg_session_adj_volume_4w / split_factor_today)
//   tr = cum_trades / avg_session_transactions_4w
// We clamp vr, tr to NaN when their inputs are null/zero and drop those rows,
// same as the old exporter did via the WHERE clause.
let sql = $"""
SELECT
    c.bucket,
    c.cum_volume::DOUBLE
        / NULLIF(sv.avg_session_adj_volume_4w::DOUBLE
                 / NULLIF(sv.session_adj_volume::DOUBLE / NULLIF(sv.session_raw_volume, 0), 0), 0) AS vr,
    c.cum_trade_count::DOUBLE
        / NULLIF(sv.avg_session_transactions_4w, 0) AS tr,
    sv.session_volume_rvol AS rvol_final
FROM intraday_10s_cum c
JOIN session_volume_4w sv ON sv.ticker = c.ticker AND sv.date = c.date
JOIN ticker_reference tr ON tr.ticker = c.ticker AND tr.type IN ('CS','ADRC')
JOIN stock_volume_4w sd ON sd.ticker = c.ticker AND sd.date = c.date
WHERE c.bucket BETWEEN {startB} AND {endB}
  AND sd.avg_dollar_volume_4w >= {minAdv}
  AND sd.avg_volume_4w > 0
  AND sv.avg_session_adj_volume_4w IS NOT NULL
  AND sv.avg_session_transactions_4w IS NOT NULL
  AND sv.session_volume_rvol IS NOT NULL
  AND sv.session_raw_volume > 0
"""

printfn "DB→DZN direct: buckets %d..%d step %d (%d target files)"
    startB endB step targetBuckets.Count
printfn "Output dir: %s" outputDir

let inv = CultureInfo.InvariantCulture
let sw = Stopwatch.StartNew()
let mutable filesWritten = 0

use cmd = conn.CreateCommand()
cmd.CommandText <- sql
cmd.CommandTimeout <- 0
use reader = cmd.ExecuteReader()

// Per-bucket accumulators. We don't ORDER BY bucket in SQL because the sort
// forces DuckDB to materialize the full result set, which is slow; without
// the ORDER BY, rows stream in whatever natural order the join produces. We
// keep one StringBuilder triple per target bucket and look up on each row.
type BucketBufs = {
    Vr : StringBuilder
    Tr : StringBuilder
    Rvol : StringBuilder
    mutable N : int
}
let bufs = System.Collections.Generic.Dictionary<int, BucketBufs>()
for b in targetBuckets do
    bufs.[b] <- { Vr = StringBuilder(); Tr = StringBuilder(); Rvol = StringBuilder(); N = 0 }

while reader.Read() do
    let bucket = reader.GetInt32 0
    match bufs.TryGetValue bucket with
    | false, _ -> ()   // outside the target set (shouldn't happen with BETWEEN + step=1)
    | true, b ->
        if not (reader.IsDBNull 1) && not (reader.IsDBNull 2) && not (reader.IsDBNull 3) then
            let vr = reader.GetDouble 1
            let tr = reader.GetDouble 2
            let rvol = reader.GetDouble 3
            if b.N > 0 then
                b.Vr.Append ',' |> ignore
                b.Tr.Append ',' |> ignore
                b.Rvol.Append ',' |> ignore
            b.Vr.AppendFormat(inv, "{0:F6}", vr) |> ignore
            b.Tr.AppendFormat(inv, "{0:F6}", tr) |> ignore
            b.Rvol.AppendFormat(inv, "{0:F6}", rvol) |> ignore
            b.N <- b.N + 1

for KeyValue(bucket, b) in bufs do
    if b.N > 0 then
        let path = Path.Combine(outputDir, sprintf "checkpoint_b%d.dzn" bucket)
        use w = new StreamWriter(path)
        w.Write (sprintf "n = %d;\nvr = [" b.N)
        w.Write (b.Vr.ToString())
        w.Write "];\ntr = ["
        w.Write (b.Tr.ToString())
        w.Write "];\nsession_volume_rvol_final = ["
        w.Write (b.Rvol.ToString())
        w.Write "];\n"
        filesWritten <- filesWritten + 1

sw.Stop()
printfn ""
printfn "Done. wrote %d DZN files in %.1fs (avg %.1f ms/file)"
    filesWritten sw.Elapsed.TotalSeconds (1000.0 * sw.Elapsed.TotalSeconds / float filesWritten)
