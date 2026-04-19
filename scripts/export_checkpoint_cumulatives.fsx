#r "../TradingEdge.Orb/bin/Release/net10.0/TradingEdge.Orb.dll"
#r "nuget: DuckDB.NET.Data.Full, 1.4.4"

// For every (ticker, date) in the tradable universe (CS/ADRC, avg_dollar_volume_4w >= $25M)
// over the minute-aggs window, compute cumulative volume and cumulative transactions at
// each checkpoint (09:31, 09:35, 09:45, 10:00 ET — bucket indices 61, 65, 75, 90 with
// buckets numbered from 08:30 ET = 0). Join session_volume_4w for the 4w averages and the
// end-of-day session-scoped volume RVOL (the ground truth MiniZinc will gate on).
//
// Output: one flat JSON file per checkpoint under data/minizinc/. Each row contains only
// the fields needed to evaluate a single checkpoint — no nested structure. This keeps the
// per-file size small enough for MiniZinc to ingest one bucket at a time.
//
//   data/minizinc/checkpoint_b61.json   -- 09:31 ET
//   data/minizinc/checkpoint_b65.json   -- 09:35 ET
//   data/minizinc/checkpoint_b75.json   -- 09:45 ET
//   data/minizinc/checkpoint_b90.json   -- 10:00 ET
//
// Row shape:
//   { "ticker": "ORCL", "date": "2025-06-12",
//     "cum_volume": 1050000, "cum_transactions": 8400,
//     "avg_session_adj_volume_4w": 9324811.3,
//     "avg_session_transactions_4w": 92311.4,
//     "session_volume_rvol_final": 7.33 }

open System
open System.IO
open System.Text
open System.Globalization
open DuckDB.NET.Data
open TradingEdge.Orb
open TradingEdge.Orb.VolumeProfile

// ----- Config -----
let db = "data/trading.db"
let outputDir = "data/minizinc"
let minAdv = 25_000_000.0
// Checkpoint bucket indices, relative to 08:30 ET = bucket 0, one bucket per minute.
// 09:31 ET = 61; 09:35 = 65; 09:45 = 75; 10:00 = 90.
let checkpoints = [| 61; 65; 75; 90 |]

let secondsPerBar = 60.0
let bucketNs = int64 (secondsPerBar * 1e9)

Directory.CreateDirectory outputDir |> ignore

let conn = new DuckDBConnection(sprintf "Data Source=%s;ACCESS_MODE=READ_ONLY" db)
conn.Open()

let bucketToEt (bucket: int) =
    let hh = 8 + (30 + bucket) / 60
    let mm = (30 + bucket) % 60
    sprintf "%02d:%02d" hh mm

// One output file per checkpoint. We stream rows straight to disk as we process
// each date so memory stays flat regardless of how many (ticker, date) rows the
// universe yields.
let writers =
    checkpoints
    |> Array.map (fun bkt ->
        let path = Path.Combine(outputDir, sprintf "checkpoint_b%d.json" bkt)
        let sw = new StreamWriter(path)
        sw.Write "[\n"
        bkt, path, sw)

// Track first-row-per-file to get comma placement right.
let firstRow = Array.create checkpoints.Length true
let mutable rowsPerCheckpoint = Array.create checkpoints.Length 0L

let dateFiles =
    Directory.GetFiles("data/minute_aggs", "*.parquet")
    |> Array.sort
    |> Array.map (fun p ->
        let d = Path.GetFileNameWithoutExtension p
        d, p)

printfn "Found %d minute-aggs parquet files" dateFiles.Length
printfn "Checkpoints:"
for (bkt, path, _) in writers do
    printfn "  %s -> %s" (bucketToEt bkt) path

let checkpointValues =
    let b = StringBuilder()
    for i = 0 to checkpoints.Length - 1 do
        if i > 0 then b.Append ", " |> ignore
        b.AppendFormat(CultureInfo.InvariantCulture, "({0})", checkpoints.[i]) |> ignore
    b.ToString()

let maxBucket = Array.max checkpoints

let sw = Diagnostics.Stopwatch.StartNew()

for (date, parquet) in dateFiles do
    let baseTime = Timezone.baseTimeFromDateString date
    let startUtc = baseTime.AddHours(VolumeProfile.startHoursFromBase)
    let toUnixNs (dt: DateTime) = (dt.Ticks - DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).Ticks) * 100L
    let startNs = toUnixNs startUtc
    let endBucketExclusive = maxBucket + 1
    let endNs = startNs + int64 endBucketExclusive * bucketNs

    // Pivoted: one row per ticker, one column per (checkpoint × (cum_vol, cum_txn)).
    // We then fan this out to four files below.
    let sql = $"""
WITH checkpoints(bucket) AS (
    VALUES {checkpointValues}
),
universe AS (
    SELECT sv.ticker
    FROM stock_volume_4w sv
    JOIN ticker_reference tr ON tr.ticker = sv.ticker AND tr.type IN ('CS','ADRC')
    WHERE sv.date = DATE '{date}'
      AND sv.avg_dollar_volume_4w >= {minAdv}
      AND sv.avg_volume_4w > 0
),
bars AS (
    SELECT
        a.ticker,
        CAST((a.window_start - {startNs}) / {bucketNs} AS INTEGER) AS bucket,
        a.volume::BIGINT AS vol,
        a.transactions::BIGINT AS txn
    FROM read_parquet('{parquet}') a
    WHERE a.ticker IN (SELECT ticker FROM universe)
      AND a.window_start >= {startNs} AND a.window_start < {endNs}
),
bucketed AS (
    SELECT ticker, bucket, SUM(vol) AS vol, SUM(txn) AS txn
    FROM bars
    WHERE bucket >= 0 AND bucket <= {maxBucket}
    GROUP BY ticker, bucket
),
cum_at_cp AS (
    SELECT
        u.ticker,
        c.bucket AS cp_bucket,
        COALESCE(SUM(CASE WHEN b.bucket <= c.bucket THEN b.vol END), 0)::BIGINT AS cum_vol,
        COALESCE(SUM(CASE WHEN b.bucket <= c.bucket THEN b.txn END), 0)::BIGINT AS cum_txn
    FROM universe u
    CROSS JOIN checkpoints c
    LEFT JOIN bucketed b ON b.ticker = u.ticker
    GROUP BY u.ticker, c.bucket
),
pivoted AS (
    SELECT
        ticker,
        MAX(CASE WHEN cp_bucket = {checkpoints.[0]} THEN cum_vol END) AS cv0,
        MAX(CASE WHEN cp_bucket = {checkpoints.[0]} THEN cum_txn END) AS ct0,
        MAX(CASE WHEN cp_bucket = {checkpoints.[1]} THEN cum_vol END) AS cv1,
        MAX(CASE WHEN cp_bucket = {checkpoints.[1]} THEN cum_txn END) AS ct1,
        MAX(CASE WHEN cp_bucket = {checkpoints.[2]} THEN cum_vol END) AS cv2,
        MAX(CASE WHEN cp_bucket = {checkpoints.[2]} THEN cum_txn END) AS ct2,
        MAX(CASE WHEN cp_bucket = {checkpoints.[3]} THEN cum_vol END) AS cv3,
        MAX(CASE WHEN cp_bucket = {checkpoints.[3]} THEN cum_txn END) AS ct3
    FROM cum_at_cp
    GROUP BY ticker
)
SELECT
    p.ticker,
    p.cv0, p.ct0, p.cv1, p.ct1, p.cv2, p.ct2, p.cv3, p.ct3,
    sv.avg_session_adj_volume_4w,
    sv.avg_session_transactions_4w,
    sv.session_volume_rvol,
    sv.session_adj_volume::DOUBLE / NULLIF(sv.session_raw_volume, 0) AS split_factor_today
FROM pivoted p
JOIN session_volume_4w sv
    ON sv.ticker = p.ticker AND sv.date = DATE '{date}'
WHERE sv.avg_session_adj_volume_4w IS NOT NULL
  AND sv.avg_session_transactions_4w IS NOT NULL
  AND sv.session_volume_rvol IS NOT NULL
  AND sv.session_raw_volume > 0
ORDER BY p.ticker
"""

    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    use reader = cmd.ExecuteReader()

    while reader.Read() do
        let ticker = reader.GetString 0
        let cv = Array.init 4 (fun i -> reader.GetInt64(1 + i*2))
        let ct = Array.init 4 (fun i -> reader.GetInt64(2 + i*2))
        let avgAdjVol4w = reader.GetDouble 9
        let avgTxn4w = reader.GetDouble 10
        let volRvolFinal = reader.GetDouble 11
        let splitFactorToday = reader.GetDouble 12

        for i = 0 to checkpoints.Length - 1 do
            let _, _, w = writers.[i]
            if not firstRow.[i] then w.Write ",\n" else firstRow.[i] <- false
            w.Write(
                String.Format(
                    CultureInfo.InvariantCulture,
                    "  {{\"ticker\": \"{0}\", \"date\": \"{1}\", \"cum_volume\": {2}, \"cum_transactions\": {3}, \"avg_session_adj_volume_4w\": {4:F3}, \"avg_session_transactions_4w\": {5:F3}, \"session_volume_rvol_final\": {6:F6}, \"split_factor_today\": {7:F6}}}",
                    ticker, date, cv.[i], ct.[i], avgAdjVol4w, avgTxn4w, volRvolFinal, splitFactorToday))
            rowsPerCheckpoint.[i] <- rowsPerCheckpoint.[i] + 1L

    if (Array.findIndex (fun (d,_) -> d = date) dateFiles) % 25 = 0 then
        printfn "[%s] rows/cp=%d elapsed=%.1fs" date rowsPerCheckpoint.[0] sw.Elapsed.TotalSeconds

for (_, _, w) in writers do
    w.Write "\n]\n"
    w.Flush()
    w.Close()

sw.Stop()
printfn ""
for i = 0 to checkpoints.Length - 1 do
    let bkt, path, _ = writers.[i]
    let size = FileInfo(path).Length
    printfn "  %s (%s): %d rows, %.1f MB" (bucketToEt bkt) path rowsPerCheckpoint.[i] (float size / 1024.0 / 1024.0)
printfn "Done in %.1fs" sw.Elapsed.TotalSeconds
