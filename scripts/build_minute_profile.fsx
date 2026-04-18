#r "../TradingEdge.Orb/bin/Release/net10.0/TradingEdge.Orb.dll"
#r "nuget: DuckDB.NET.Data.Full, 1.4.4"

// Build a 1-minute cumulative-volume profile from the minute-aggs Parquet
// dump, averaged across the tradable universe (CS/ADRC, ADV >= $25M) for each
// date. Output matches the shape of VolumeProfile.load so the existing
// LoadedProfile consumer can read it unchanged.
//
// Denominator for the cumulative-fraction math is the *full-day* minute-aggs
// volume (sum of all bars for that ticker on that day), not daily_prices.volume.
// Massive's minute aggs exclude some late/off-market prints that daily_prices
// includes — matching denominator keeps the profile self-consistent with what
// the scanner actually sees at inference.

open System
open System.IO
open System.Text
open System.Globalization
open DuckDB.NET.Data
open TradingEdge.Orb
open TradingEdge.Orb.VolumeProfile

// ----- Config -----
let db = "data/trading.db"
let minuteAggsGlob = "data/minute_aggs/*.parquet"
let output = "data/volume_profile_minute.json"
let minAdv = 25_000_000.0
let secondsPerBar = 60.0

// ----- Bucket layout (matches the tick-based profile) -----
let bucketTicks = int64 (secondsPerBar * float TimeSpan.TicksPerSecond)
let regularBucketCount = int ((regularCloseHoursFromBase - startHoursFromBase) * 3600.0 / secondsPerBar)
let earlyBucketCount = int ((earlyCloseHoursFromBase - startHoursFromBase) * 3600.0 / secondsPerBar)
printfn "bucket layout: regular=%d, early=%d (secondsPerBar=%g)" regularBucketCount earlyBucketCount secondsPerBar

let regularAcc = ProfileAccumulator(regularBucketCount)
let earlyAcc = ProfileAccumulator(earlyBucketCount)

// ----- DuckDB connection (read-only) -----
let conn = new DuckDBConnection(sprintf "Data Source=%s;ACCESS_MODE=READ_ONLY" db)
conn.Open()

// ----- Enumerate the Parquet files (one per trading date) -----
let dateFiles =
    Directory.GetFiles("data/minute_aggs", "*.parquet")
    |> Array.sort
    |> Array.map (fun p ->
        let date = Path.GetFileNameWithoutExtension p
        date, p)

printfn "Found %d minute-aggs parquet files" dateFiles.Length

// ----- Per-day processing -----
// For each date: query DuckDB for a single result set combining the universe
// (CS/ADRC + ADV filter) and per-ticker cumulative-per-bucket numerator/denominator
// for that day's minute aggs. Then walk results in F# to update the profile
// accumulator.

let sw = Diagnostics.Stopwatch.StartNew()
let mutable processedDays = 0
let mutable totalRowsUsed = 0L
let mutable skippedNoUniverse = 0
let mutable skippedNoVolume = 0

for (date, parquet) in dateFiles do
    let isEarly = Timezone.early_closes.Contains(DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture))
    let acc = if isEarly then earlyAcc else regularAcc
    let bucketCount = acc.BucketCount

    // Session window in UTC ns (DST-aware).
    let baseTime = Timezone.baseTimeFromDateString date
    let startUtc = baseTime.AddHours(startHoursFromBase)
    let closeHours = if isEarly then earlyCloseHoursFromBase else regularCloseHoursFromBase
    let closeUtc = baseTime.AddHours(closeHours)
    let toUnixNs (dt: DateTime) = (dt.Ticks - DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).Ticks) * 100L
    let startNs = toUnixNs startUtc
    let closeNs = toUnixNs closeUtc
    let bucketNs = int64 (secondsPerBar * 1e9)

    // Universe for this date: CS/ADRC + avg_dollar_volume_4w >= minAdv.
    // Denominator for cum-fraction is daily_prices.volume (raw, unadjusted
    // daily tape volume), not the sum of the minute-aggs bars themselves.
    // This matches the units that raw_avg_4w predicts against at inference,
    // so predicted_rvol math is consistent with the existing ORB gate.
    let sql = $"""
WITH universe AS (
    SELECT sv.ticker, dp.volume::DOUBLE AS total_vol
    FROM stock_volume_4w sv
    JOIN ticker_reference tr ON tr.ticker = sv.ticker AND tr.type IN ('CS','ADRC')
    JOIN daily_prices dp ON dp.ticker = sv.ticker AND dp.date = sv.date
    WHERE sv.date = DATE '{date}'
      AND sv.avg_dollar_volume_4w >= {minAdv}
      AND dp.volume > 0
),
in_session AS (
    SELECT
        a.ticker,
        CAST((a.window_start - {startNs}) / {bucketNs} AS INTEGER) AS bucket,
        a.volume
    FROM read_parquet('{parquet}') a
    WHERE a.ticker IN (SELECT ticker FROM universe)
      AND a.window_start >= {startNs} AND a.window_start < {closeNs}
),
bucketed AS (
    SELECT ticker, bucket, SUM(volume)::DOUBLE AS vol
    FROM in_session
    WHERE bucket >= 0 AND bucket < {bucketCount}
    GROUP BY ticker, bucket
)
SELECT b.ticker, b.bucket, b.vol, u.total_vol
FROM bucketed b
JOIN universe u ON u.ticker = b.ticker
ORDER BY b.ticker, b.bucket
"""

    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    use reader = cmd.ExecuteReader()

    // Per-ticker temporary buffer. We emit one cum-frac array into the
    // profile accumulator per ticker when its rows are finished.
    let mutable currentTicker = ""
    let mutable currentTotal = 0.0
    let perBucketVol = Array.zeroCreate<float> bucketCount
    let mutable tickersInDay = 0

    let flushTicker () =
        if currentTicker <> "" && currentTotal > 0.0 then
            // Build cumulative fraction (cumulative session vol / total day vol).
            let cumFrac = Array.zeroCreate<float> bucketCount
            let mutable running = 0.0
            for i = 0 to bucketCount - 1 do
                running <- running + perBucketVol.[i]
                cumFrac.[i] <- running / currentTotal
            acc.AddDay cumFrac
            tickersInDay <- tickersInDay + 1
        // Reset buffer either way.
        Array.Clear(perBucketVol, 0, bucketCount)

    while reader.Read() do
        let ticker = reader.GetString 0
        if ticker <> currentTicker then
            flushTicker ()
            currentTicker <- ticker
            currentTotal <- reader.GetDouble 3
        let bucket = reader.GetInt32 1
        let vol = reader.GetDouble 2
        perBucketVol.[bucket] <- perBucketVol.[bucket] + vol
        totalRowsUsed <- totalRowsUsed + 1L
    flushTicker ()

    processedDays <- processedDays + 1
    if processedDays % 25 = 0 then
        printfn "[%d/%d] %s: %d tickers contributed (regular acc count=%d, early acc count=%d)"
            processedDays dateFiles.Length date tickersInDay regularAcc.Count earlyAcc.Count

sw.Stop()
printfn ""
printfn "Profile built in %.1fs over %d days, %d minute rows read" sw.Elapsed.TotalSeconds processedDays totalRowsUsed
printfn "  regular-close ticker-days: %d" regularAcc.Count
printfn "  early-close ticker-days:   %d" earlyAcc.Count

// ----- Write JSON (mirrors VolumeProfile.run layout so the loader works unchanged) -----
let writeArray (sb: StringBuilder) (xs: float[]) =
    sb.Append '[' |> ignore
    for i = 0 to xs.Length - 1 do
        if i > 0 then sb.Append ", " |> ignore
        sb.Append(xs.[i].ToString("R", CultureInfo.InvariantCulture)) |> ignore
    sb.Append ']' |> ignore

let writeSection (sb: StringBuilder) (name: string) (closeTimeEt: string) (a: ProfileAccumulator) =
    sb.AppendFormat("  \"{0}\": {{\n", name) |> ignore
    sb.AppendFormat("    \"close_time_et\": \"{0}\",\n", closeTimeEt) |> ignore
    sb.AppendFormat("    \"bucket_count\": {0},\n", a.BucketCount) |> ignore
    sb.AppendFormat("    \"days_used\": {0},\n", a.Count) |> ignore
    sb.Append "    \"profile\": " |> ignore
    writeArray sb (a.Finalize())
    sb.Append "\n  }" |> ignore

let sb = StringBuilder()
sb.Append "{\n" |> ignore
sb.AppendFormat("  \"seconds_per_bar\": {0},\n",
    secondsPerBar.ToString("R", CultureInfo.InvariantCulture)) |> ignore
sb.Append "  \"start_time_et\": \"08:30:00\",\n" |> ignore
writeSection sb "regular_close" "16:00:00" regularAcc
sb.Append ",\n" |> ignore
writeSection sb "early_close" "13:00:00" earlyAcc
sb.Append "\n}\n" |> ignore
File.WriteAllText(output, sb.ToString())
printfn "Wrote %s" output
