#r "../TradingEdge.Orb/bin/Release/net10.0/TradingEdge.Orb.dll"
#r "nuget: DuckDB.NET.Data.Full, 1.4.4"

// For every (ticker, date) in the tradable universe (CS/ADRC, avg_dollar_volume_4w >= $25M)
// over the minute-aggs window, compute cumulative volume and cumulative transactions at
// the four checkpoints 09:31, 09:35, 09:45, 10:00 ET (bucket indices 61, 65, 75, 90 with
// buckets numbered from 08:30 ET = 0). Join session_volume_4w for the 4w averages and the
// end-of-day session-scoped RVOL. Write one JSON row per (ticker, date).
//
// Output shape (per row):
//   { "ticker": "ORCL", "date": "2025-06-12",
//     "avg_session_adj_volume_4w": 9324811.3, "avg_session_transactions_4w": 92311.4,
//     "session_raw_volume": 68_300_000, "session_adj_volume": 68_300_000,
//     "session_transactions": 441_000,
//     "session_volume_rvol_final": 7.33, "session_transaction_rvol_final": 4.81,
//     "cum_at_checkpoints": [
//       { "bucket": 61, "et": "09:31", "cum_raw_volume": 1050000, "cum_transactions": 8400 },
//       { "bucket": 65, "et": "09:35", "cum_raw_volume": 2200000, "cum_transactions": 16200 },
//       { "bucket": 75, "et": "09:45", "cum_raw_volume": 4100000, "cum_transactions": 27800 },
//       { "bucket": 90, "et": "10:00", "cum_raw_volume": 6800000, "cum_transactions": 41500 } ] }

open System
open System.IO
open System.Text
open System.Globalization
open DuckDB.NET.Data
open TradingEdge.Orb
open TradingEdge.Orb.VolumeProfile

// ----- Config -----
let db = "data/trading.db"
let output = "data/checkpoint_cumulatives.json"
let minAdv = 25_000_000.0
// Checkpoint bucket indices, relative to 08:30 ET = bucket 0, one bucket per minute.
// 09:31 ET = 60 + 1 = 61; 09:35 = 65; 09:45 = 75; 10:00 = 90.
let checkpoints = [| 61; 65; 75; 90 |]

let secondsPerBar = 60.0
let bucketNs = int64 (secondsPerBar * 1e9)

let conn = new DuckDBConnection(sprintf "Data Source=%s;ACCESS_MODE=READ_ONLY" db)
conn.Open()

let bucketToEt (bucket: int) =
    let hh = 8 + (30 + bucket) / 60
    let mm = (30 + bucket) % 60
    sprintf "%02d:%02d" hh mm

let checkpointEt = checkpoints |> Array.map bucketToEt

let dateFiles =
    Directory.GetFiles("data/minute_aggs", "*.parquet")
    |> Array.sort
    |> Array.map (fun p ->
        let d = Path.GetFileNameWithoutExtension p
        d, p)

printfn "Found %d minute-aggs parquet files" dateFiles.Length
printfn "Checkpoints: %s"
    (String.Join(", ", Array.map2 (fun b et -> sprintf "b%d=%s" b et) checkpoints checkpointEt))

let sb = StringBuilder()
sb.Append "[\n" |> ignore
let mutable firstRow = true
let mutable rowsWritten = 0L
let sw = Diagnostics.Stopwatch.StartNew()

let checkpointValues =
    let b = StringBuilder()
    for i = 0 to checkpoints.Length - 1 do
        if i > 0 then b.Append ", " |> ignore
        b.AppendFormat(CultureInfo.InvariantCulture, "({0})", checkpoints.[i]) |> ignore
    b.ToString()

let maxBucket = Array.max checkpoints

for (date, parquet) in dateFiles do
    let isEarly = Timezone.early_closes.Contains(DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture))
    // Skip early-close days only if their close is earlier than our latest checkpoint.
    // Latest checkpoint is 10:00 ET; early-close is 13:00 ET, so it's fine.
    ignore isEarly

    let baseTime = Timezone.baseTimeFromDateString date
    let startUtc = baseTime.AddHours(VolumeProfile.startHoursFromBase)
    let toUnixNs (dt: DateTime) = (dt.Ticks - DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).Ticks) * 100L
    let startNs = toUnixNs startUtc
    // Stop scanning once we're past the last checkpoint bar.
    let endBucketExclusive = maxBucket + 1
    let endNs = startNs + int64 endBucketExclusive * bucketNs

    // For each (ticker, checkpoint), compute cumulative raw volume and cumulative
    // transactions from 08:30 ET (bucket 0) through the checkpoint bucket
    // inclusive. We filter to the tradable universe on this date.
    //
    // Note: endpoint `bucket <= checkpoint` is intentional. Bucket 61 covers the
    // 09:31-09:32 ET minute; "cumulative at 09:31 ET" includes that bar because
    // by the time we evaluate the rule at 09:32 the 09:31 minute has completed.
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
    sv.session_raw_volume,
    sv.session_adj_volume,
    sv.session_transactions,
    sv.avg_session_adj_volume_4w,
    sv.avg_session_transactions_4w,
    sv.session_volume_rvol,
    sv.session_transaction_rvol
FROM pivoted p
JOIN session_volume_4w sv
    ON sv.ticker = p.ticker AND sv.date = DATE '{date}'
WHERE sv.avg_session_adj_volume_4w IS NOT NULL
  AND sv.avg_session_transactions_4w IS NOT NULL
ORDER BY p.ticker
"""

    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    use reader = cmd.ExecuteReader()

    while reader.Read() do
        let ticker = reader.GetString 0
        let cv = Array.init 4 (fun i -> reader.GetInt64(1 + i*2))
        let ct = Array.init 4 (fun i -> reader.GetInt64(2 + i*2))
        let sessionRaw = reader.GetInt64 9
        let sessionAdj = reader.GetInt64 10
        let sessionTxn = reader.GetInt64 11
        let avgAdjVol4w = reader.GetDouble 12
        let avgTxn4w = reader.GetDouble 13
        let volRvolFinal = reader.GetDouble 14
        let txnRvolFinal = reader.GetDouble 15

        // Build cum_at_checkpoints array
        let cpSb = StringBuilder()
        cpSb.Append "[" |> ignore
        for i = 0 to checkpoints.Length - 1 do
            if i > 0 then cpSb.Append ", " |> ignore
            cpSb.AppendFormat(CultureInfo.InvariantCulture,
                "{{\"bucket\": {0}, \"et\": \"{1}\", \"cum_raw_volume\": {2}, \"cum_transactions\": {3}}}",
                checkpoints.[i], checkpointEt.[i], cv.[i], ct.[i]) |> ignore
        cpSb.Append "]" |> ignore

        if not firstRow then sb.Append ",\n" |> ignore
        firstRow <- false
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "  {{\"ticker\": \"{0}\", \"date\": \"{1}\", \"session_raw_volume\": {2}, \"session_adj_volume\": {3}, \"session_transactions\": {4}, \"avg_session_adj_volume_4w\": {5:F3}, \"avg_session_transactions_4w\": {6:F3}, \"session_volume_rvol_final\": {7:F6}, \"session_transaction_rvol_final\": {8:F6}, \"cum_at_checkpoints\": {9}}}",
            ticker, date, sessionRaw, sessionAdj, sessionTxn,
            avgAdjVol4w, avgTxn4w, volRvolFinal, txnRvolFinal, cpSb.ToString()) |> ignore
        rowsWritten <- rowsWritten + 1L

    if (Array.findIndex (fun (d,_) -> d = date) dateFiles) % 25 = 0 then
        printfn "[%s] rows=%d elapsed=%.1fs" date rowsWritten sw.Elapsed.TotalSeconds

sb.Append "\n]\n" |> ignore
File.WriteAllText(output, sb.ToString())
sw.Stop()
printfn ""
printfn "Wrote %d rows to %s in %.1fs (%.1f MB)"
    rowsWritten output sw.Elapsed.TotalSeconds
    (float (FileInfo(output).Length) / 1024.0 / 1024.0)
