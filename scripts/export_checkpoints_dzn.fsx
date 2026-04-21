#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#r "nuget: Argu, 6.2.5"

// Direct DuckDB -> .dzn exporter.
//
// Strategy: CHUNKED streaming scans. Opening one writer per bucket for all
// 2323 buckets OOM'd with 16 GB of FileStream buffers. Instead we scan one
// chunk of buckets at a time (default 256), keeping at most ~256 writers
// live. Each chunk is one DuckDB query with WHERE bucket BETWEEN cLo AND cHi;
// the parquet zone maps mean DuckDB only reads the relevant row groups from
// each day file.
//
// Data shape: the MZN model takes `data: array of tuple(float, float, float)`.
// One tuple per row → one writer per bucket, zero buffering. DZN is an
// order-independent assignment list, so we write `data = [...];` first then
// append `n = <count>;` after the row loop closes.

open System.IO
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
    | [<AltCommandLine("-c")>] Chunk_Size of int

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Database _ -> "DuckDB path. Default: data/trading.db"
            | Start_Bucket _ -> "First bucket (inclusive). Default: 366 (09:31 ET)"
            | End_Bucket _ -> "Last bucket (inclusive). Default: 2688 (15:58:00 ET)"
            | Step _ -> "Bucket step. Default: 1 (every bucket)"
            | Bucket _ -> "Single bucket shortcut (equivalent to -s N -e N -k 1)"
            | Output_Dir _ -> "Output dir for .dzn files. Default: data/minizinc/10s"
            | Chunk_Size _ -> "Buckets per streaming scan. Default: 256"

let parser = ArgumentParser.Create<CliArgs>(programName = "export_checkpoints_dzn.fsx")
let parsed =
    try parser.Parse(fsi.CommandLineArgs |> Array.skip 1, raiseOnUsage = true)
    with :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        exit 1

let db = parsed.GetResult(Database, defaultValue = "data/trading.db")
let outputDir = parsed.GetResult(Output_Dir, defaultValue = "data/minizinc/10s")
let chunkSize = parsed.GetResult(Chunk_Size, defaultValue = 256)
let minAdv = 25_000_000.0

let startB, endB, step =
    match parsed.TryGetResult Bucket with
    | Some b -> b, b, 1
    | None ->
        parsed.GetResult(Start_Bucket, defaultValue = 366),
        parsed.GetResult(End_Bucket, defaultValue = 2688),
        parsed.GetResult(Step, defaultValue = 1)

let targetBuckets = [| for b in startB .. step .. endB -> b |]

Directory.CreateDirectory outputDir |> ignore

type DznWriter = {
    W : StreamWriter
    mutable N : int
}

let inv = CultureInfo.InvariantCulture
let conn = new DuckDBConnection(sprintf "Data Source=%s;ACCESS_MODE=READ_ONLY" db)
conn.Open()

printfn "DB->DZN chunked: buckets %d..%d step %d (%d target files), chunk=%d"
    startB endB step targetBuckets.Length chunkSize
printfn "Output dir: %s" outputDir

let totalSw = Stopwatch.StartNew()
let mutable filesWritten = 0
let mutable totalRows = 0L

let processChunk (chunkBuckets: int[]) =
    let cLo = chunkBuckets.[0]
    let cHi = chunkBuckets.[chunkBuckets.Length - 1]

    let writers = System.Collections.Generic.Dictionary<int, DznWriter>(chunkBuckets.Length)
    for b in chunkBuckets do
        let path = Path.Combine(outputDir, sprintf "checkpoint_b%d.dzn" b)
        let fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 <<< 15)
        let w = new StreamWriter(fs)
        w.Write "data = ["
        writers.[b] <- { W = w; N = 0 }

    let sql = sprintf """
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
WHERE c.bucket BETWEEN %d AND %d
  AND sd.avg_dollar_volume_4w >= %f
  AND sd.avg_volume_4w > 0
  AND sv.avg_session_adj_volume_4w IS NOT NULL
  AND sv.avg_session_transactions_4w IS NOT NULL
  AND sv.session_volume_rvol IS NOT NULL
  AND sv.session_raw_volume > 0""" cLo cHi minAdv

    let scanSw = Stopwatch.StartNew()
    let mutable chunkRows = 0L
    do
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        cmd.CommandTimeout <- 0
        use reader = cmd.ExecuteReader()
        while reader.Read() do
            if reader.IsDBNull 1 || reader.IsDBNull 2 || reader.IsDBNull 3 then () else
            let bucket = reader.GetInt32 0
            match writers.TryGetValue bucket with
            | false, _ -> ()
            | true, dw ->
                let vr = reader.GetDouble 1
                let tr = reader.GetDouble 2
                let rvol = reader.GetDouble 3
                if dw.N > 0 then dw.W.Write ','
                dw.W.Write '('
                dw.W.Write (vr.ToString("F6", inv))
                dw.W.Write ','
                dw.W.Write (tr.ToString("F6", inv))
                dw.W.Write ','
                dw.W.Write (rvol.ToString("F6", inv))
                dw.W.Write ')'
                dw.N <- dw.N + 1
                chunkRows <- chunkRows + 1L
    scanSw.Stop()
    totalRows <- totalRows + chunkRows

    let wrapSw = Stopwatch.StartNew()
    let mutable chunkFiles = 0
    for bucket in chunkBuckets do
        let dw = writers.[bucket]
        if dw.N > 0 then
            dw.W.Write "];\n"
            dw.W.Write (sprintf "n = %d;\n" dw.N)
            dw.W.Dispose()
            chunkFiles <- chunkFiles + 1
        else
            dw.W.Dispose()
            let path = Path.Combine(outputDir, sprintf "checkpoint_b%d.dzn" bucket)
            try File.Delete path with _ -> ()
    wrapSw.Stop()
    filesWritten <- filesWritten + chunkFiles

    printfn "  chunk [%d..%d]: %d rows scan=%.1fs, wrap=%d files %.1fs | total elapsed %.1fs"
        cLo cHi chunkRows scanSw.Elapsed.TotalSeconds chunkFiles wrapSw.Elapsed.TotalSeconds
        totalSw.Elapsed.TotalSeconds

let mutable idx = 0
while idx < targetBuckets.Length do
    let take = min chunkSize (targetBuckets.Length - idx)
    let chunk = targetBuckets.[idx .. idx + take - 1]
    processChunk chunk
    idx <- idx + take

totalSw.Stop()
printfn ""
printfn "Done. %d files, %d rows in %.1fs (%.1f ms/file)"
    filesWritten totalRows totalSw.Elapsed.TotalSeconds
    (1000.0 * totalSw.Elapsed.TotalSeconds / float (max filesWritten 1))
