#r "../TradingEdge.Orb/bin/Release/net10.0/TradingEdge.Orb.dll"
#r "nuget: DuckDB.NET.Data.Full, 1.4.4"

// Market-wide predicted-RVOL scanner.
//
// For every (ticker, date) in the tradable universe (CS/ADRC, avg_dollar_volume_4w >= $25M)
// over 2024-04-01 -> today, computes running predicted RVOL from the minute-aggs cumulative
// session volume and the 1-min cumulative volume profile. Records the earliest bar at
// which predicted RVOL crosses 5x, 6x, and 7x.
//
// RVOL formula matches the ORB gate (Pipeline.fs:268-288, Program.fs:279-297):
//   raw_avg_4w       = avg_volume_4w * (raw_volume / adj_volume)        -- raw-shares units
//   predicted_raw    = observed_session_cum / profile[bucket]
//   predicted_rvol   = predicted_raw / raw_avg_4w
//
// Output: data/predicted_rvol_hits.json — one row per (ticker, date) that crossed 5x at
// some point, recording first-hit bar timestamps for 5/6/7x thresholds.

open System
open System.IO
open System.Text
open System.Globalization
open DuckDB.NET.Data
open TradingEdge.Orb
open TradingEdge.Orb.VolumeProfile

// ----- Config -----
let db = "data/trading.db"
let profilePath = "data/volume_profile_minute.json"
let output = "data/predicted_rvol_hits.json"
let minAdv = 25_000_000.0
let thresholds = [| 5.0; 6.0; 7.0 |]
let lowestThreshold = Array.min thresholds

// RTH-only: first possible hit bucket = 60 (09:30 ET with 1-min buckets from
// 08:30). Pre-market buckets have tiny profile fractions — even a handful of
// shares divided by frac ~= 0 produces absurd predicted RVOLs. The ORB entry
// logic fires only after the opening print anyway, so restricting the scan to
// RTH matches where a trade could actually be taken.
let minHitBucket = 60

// ----- Load profile -----
let profile = load profilePath
printfn "Loaded profile from %s (secondsPerBar=%g, regular=%d, early=%d)"
    profilePath profile.SecondsPerBar profile.RegularClose.BucketCount profile.EarlyClose.BucketCount

let bucketNs = int64 (profile.SecondsPerBar * 1e9)

// ----- DuckDB connection -----
let conn = new DuckDBConnection(sprintf "Data Source=%s;ACCESS_MODE=READ_ONLY" db)
conn.Open()

// ----- Hit record -----
type Hit = {
    Ticker: string
    Date: string
    FirstHit5Bucket: int voption
    FirstHit5Rvol: float voption
    FirstHit6Bucket: int voption
    FirstHit6Rvol: float voption
    FirstHit7Bucket: int voption
    FirstHit7Rvol: float voption
    MaxRvol: float
    MaxRvolBucket: int
}

let allHits = ResizeArray<Hit>()
let mutable processedDays = 0
let mutable totalRowsSeen = 0L

// Counts by threshold.
let hitCounts = [| for _ in thresholds -> 0 |]

// Enumerate date files (one Parquet per trading date).
let dateFiles =
    Directory.GetFiles("data/minute_aggs", "*.parquet")
    |> Array.sort
    |> Array.map (fun p ->
        let d = Path.GetFileNameWithoutExtension p
        d, p)

printfn "Found %d minute-aggs parquet files" dateFiles.Length

let sw = Diagnostics.Stopwatch.StartNew()

for (date, parquet) in dateFiles do
    let isEarly = Timezone.early_closes.Contains(DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture))
    let session = if isEarly then profile.EarlyClose else profile.RegularClose
    let bucketCount = session.BucketCount

    let baseTime = Timezone.baseTimeFromDateString date
    let startUtc = baseTime.AddHours(startHoursFromBase)
    let closeHours = if isEarly then earlyCloseHoursFromBase else regularCloseHoursFromBase
    let closeUtc = baseTime.AddHours(closeHours)
    let toUnixNs (dt: DateTime) = (dt.Ticks - DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).Ticks) * 100L
    let startNs = toUnixNs startUtc
    let closeNs = toUnixNs closeUtc

    // Inline the profile as a CTE so DuckDB can join on bucket.
    // Only emit buckets with profileFrac > 0 (earliest premarket bars can be 0).
    let profileValues =
        let sb = StringBuilder()
        let mutable first = true
        for i = 0 to bucketCount - 1 do
            let frac = session.Profile.[i]
            if frac > 0.0 then
                if not first then sb.Append ", " |> ignore
                first <- false
                sb.AppendFormat(CultureInfo.InvariantCulture, "({0}, {1:R})", i, frac) |> ignore
        sb.ToString()

    if profileValues.Length = 0 then
        () // nothing to scan this day
    else

    // Single query per day: compute running cum_vol per (ticker, bucket), join
    // profile fractions, compute predicted_rvol, emit one row per ticker with
    // first-hit bucket for each threshold and max across the day.
    //
    // raw_avg_4w = avg_volume_4w * (raw_volume / adj_volume) on this specific date.
    let sql = $"""
WITH profile(bucket, frac) AS (
    VALUES {profileValues}
),
universe AS (
    SELECT
        sv.ticker,
        sv.avg_volume_4w,
        sa.raw_volume::DOUBLE AS raw_vol,
        sa.adj_volume::DOUBLE AS adj_vol,
        sv.avg_volume_4w * (sa.raw_volume::DOUBLE / sa.adj_volume::DOUBLE) AS raw_avg_4w
    FROM stock_volume_4w sv
    JOIN ticker_reference tr ON tr.ticker = sv.ticker AND tr.type IN ('CS','ADRC')
    JOIN split_adjusted_prices sa ON sa.ticker = sv.ticker AND sa.date = sv.date
    WHERE sv.date = DATE '{date}'
      AND sv.avg_dollar_volume_4w >= {minAdv}
      AND sv.avg_volume_4w > 0
      -- Require a defined split factor (adj_volume=0 means the reverse-split
      -- rounded down to zero shares, which produces meaningless RVOL math).
      AND sa.adj_volume > 0
      -- Require a non-pathological 4w baseline in raw shares. Reverse-split
      -- tickers (MULN-style) satisfy the $-ADV floor because adj_close is
      -- huge while adj_volume is 0-1 shares; their raw-shares baseline is
      -- effectively meaningless and produces billion-multiple predicted RVOLs.
      AND sv.avg_volume_4w * (sa.raw_volume::DOUBLE / sa.adj_volume::DOUBLE) >= 100000
),
bars AS (
    SELECT
        a.ticker,
        CAST((a.window_start - {startNs}) / {bucketNs} AS INTEGER) AS bucket,
        a.volume::DOUBLE AS vol
    FROM read_parquet('{parquet}') a
    WHERE a.ticker IN (SELECT ticker FROM universe)
      AND a.window_start >= {startNs} AND a.window_start < {closeNs}
),
bucketed AS (
    SELECT ticker, bucket, SUM(vol) AS vol
    FROM bars
    WHERE bucket >= 0 AND bucket < {bucketCount}
    GROUP BY ticker, bucket
),
cum AS (
    SELECT
        b.ticker,
        b.bucket,
        SUM(b.vol) OVER (PARTITION BY b.ticker ORDER BY b.bucket) AS cum_vol
    FROM bucketed b
),
pred AS (
    SELECT
        c.ticker,
        c.bucket,
        c.cum_vol,
        p.frac AS profile_frac,
        (c.cum_vol / p.frac) / u.raw_avg_4w AS predicted_rvol
    FROM cum c
    JOIN profile p ON p.bucket = c.bucket
    JOIN universe u ON u.ticker = c.ticker
    WHERE u.raw_avg_4w > 0
)
SELECT
    ticker,
    MAX(CASE WHEN bucket >= {minHitBucket} THEN predicted_rvol END) AS max_rvol,
    ARG_MAX(
        CASE WHEN bucket >= {minHitBucket} THEN bucket END,
        CASE WHEN bucket >= {minHitBucket} THEN predicted_rvol END
    ) AS max_rvol_bucket,
    MIN(CASE WHEN bucket >= {minHitBucket} AND predicted_rvol >= 5.0 THEN bucket END) AS b5,
    MIN(CASE WHEN bucket >= {minHitBucket} AND predicted_rvol >= 6.0 THEN bucket END) AS b6,
    MIN(CASE WHEN bucket >= {minHitBucket} AND predicted_rvol >= 7.0 THEN bucket END) AS b7
FROM pred
GROUP BY ticker
HAVING MAX(CASE WHEN bucket >= {minHitBucket} THEN predicted_rvol END) >= {lowestThreshold}
ORDER BY max_rvol DESC
"""

    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    use reader = cmd.ExecuteReader()

    while reader.Read() do
        let ticker = reader.GetString 0
        let maxRvol = reader.GetDouble 1
        let maxBucket = reader.GetInt32 2
        let b5 = if reader.IsDBNull 3 then ValueNone else ValueSome (reader.GetInt32 3)
        let b6 = if reader.IsDBNull 4 then ValueNone else ValueSome (reader.GetInt32 4)
        let b7 = if reader.IsDBNull 5 then ValueNone else ValueSome (reader.GetInt32 5)

        // Predicted RVOL at first-hit buckets (they crossed the threshold by construction
        // so the value is >= threshold; we don't need it exact, but keep it for the output).
        // Record just the bucket indices and the max.
        let hit = {
            Ticker = ticker
            Date = date
            FirstHit5Bucket = b5
            FirstHit5Rvol = if b5.IsSome then ValueSome 5.0 else ValueNone
            FirstHit6Bucket = b6
            FirstHit6Rvol = if b6.IsSome then ValueSome 6.0 else ValueNone
            FirstHit7Bucket = b7
            FirstHit7Rvol = if b7.IsSome then ValueSome 7.0 else ValueNone
            MaxRvol = maxRvol
            MaxRvolBucket = maxBucket
        }
        allHits.Add hit
        totalRowsSeen <- totalRowsSeen + 1L
        if b5.IsSome then hitCounts.[0] <- hitCounts.[0] + 1
        if b6.IsSome then hitCounts.[1] <- hitCounts.[1] + 1
        if b7.IsSome then hitCounts.[2] <- hitCounts.[2] + 1

    processedDays <- processedDays + 1
    if processedDays % 50 = 0 then
        printfn "[%d/%d] %s: %d hits so far (5x=%d, 6x=%d, 7x=%d)"
            processedDays dateFiles.Length date allHits.Count
            hitCounts.[0] hitCounts.[1] hitCounts.[2]

sw.Stop()
printfn ""
printfn "Scan complete in %.1fs over %d days" sw.Elapsed.TotalSeconds processedDays
printfn "  Total >=5x ticker-days: %d" hitCounts.[0]
printfn "  Total >=6x ticker-days: %d" hitCounts.[1]
printfn "  Total >=7x ticker-days: %d" hitCounts.[2]
printfn "  Trading days in window: %d" processedDays
printfn "  >=5x hits per day:       %.2f" (float hitCounts.[0] / float processedDays)
printfn "  >=6x hits per day:       %.2f" (float hitCounts.[1] / float processedDays)
printfn "  >=7x hits per day:       %.2f" (float hitCounts.[2] / float processedDays)

// ----- Write JSON -----
// Convert bucket to HH:MM ET for human-readability. The bucket index is relative to
// 08:30 ET on that day, one bucket per minute.
let bucketToEt (bucket: int) =
    let totalMinFrom830 = bucket
    let hh = 8 + (30 + totalMinFrom830) / 60
    let mm = (30 + totalMinFrom830) % 60
    sprintf "%02d:%02d" hh mm

let sb = StringBuilder()
sb.Append "[\n" |> ignore
for i = 0 to allHits.Count - 1 do
    let h = allHits.[i]
    let fmtBucket (b: int voption) =
        match b with
        | ValueSome v -> sprintf "\"%s\"" (bucketToEt v)
        | ValueNone -> "null"
    let fmtInt (b: int voption) =
        match b with
        | ValueSome v -> string v
        | ValueNone -> "null"
    let comma = if i = allHits.Count - 1 then "" else ","
    sb.AppendFormat(CultureInfo.InvariantCulture,
        "  {{\"ticker\": \"{0}\", \"date\": \"{1}\", \"max_rvol\": {2:F3}, \"max_rvol_et\": \"{3}\", \"first_5x_et\": {4}, \"first_6x_et\": {5}, \"first_7x_et\": {6}, \"first_5x_bucket\": {7}, \"first_6x_bucket\": {8}, \"first_7x_bucket\": {9}}}{10}\n",
        h.Ticker, h.Date, h.MaxRvol, bucketToEt h.MaxRvolBucket,
        fmtBucket h.FirstHit5Bucket, fmtBucket h.FirstHit6Bucket, fmtBucket h.FirstHit7Bucket,
        fmtInt h.FirstHit5Bucket, fmtInt h.FirstHit6Bucket, fmtInt h.FirstHit7Bucket,
        comma) |> ignore
sb.Append "]\n" |> ignore
File.WriteAllText(output, sb.ToString())
printfn "Wrote %s" output
