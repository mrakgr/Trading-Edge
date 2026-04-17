module TradingEdge.Orb.VolumeProfile

open System
open System.IO
open System.Text
open System.Text.Json
open TradeLoader
open TradeBinary

// ============================================================================
// Input row
// ============================================================================

type PlayRow = {
    Ticker: string
    Date: string
    /// Raw daily volume from `daily_prices` (unadjusted for splits). Matches the raw
    /// share counts reported by the filtered binary trade stream — required for the
    /// cumulative-fraction math to make sense on split-affected tickers.
    RawVolume: float
}

let loadPlaysWithVolume (jsonPath: string) : PlayRow[] =
    let bytes = File.ReadAllBytes jsonPath
    use doc = JsonDocument.Parse(ReadOnlyMemory bytes)
    [| for el in doc.RootElement.EnumerateArray() ->
        { Ticker = el.GetProperty("ticker").GetString()
          Date = el.GetProperty("date").GetString()
          RawVolume = el.GetProperty("raw_volume").GetDouble() } |]

// ============================================================================
// Profile accumulator
// ============================================================================

/// One profile across a single session shape (regular close or early close).
/// Sums per-day cumulative fractions into `CumFracSum`, with `Count` tracking
/// how many days contributed. The emitted profile is CumFracSum / Count per
/// bucket.
type ProfileAccumulator(bucketCount: int) =
    member val BucketCount = bucketCount
    member val CumFracSum = Array.zeroCreate<float> bucketCount with get
    member val Count = 0 with get, set

    member self.AddDay(perDayCumFrac: float[]) =
        for i = 0 to bucketCount - 1 do
            self.CumFracSum.[i] <- self.CumFracSum.[i] + perDayCumFrac.[i]
        self.Count <- self.Count + 1

    member self.Finalize() =
        if self.Count = 0 then Array.zeroCreate bucketCount
        else Array.map (fun s -> s / float self.Count) self.CumFracSum

// ============================================================================
// Per-day bucket walk
// ============================================================================

/// Walk the filtered trade stream, accumulate volume per bucket in
/// [startTime, closeTime). Returns a cumulative-fraction array of length
/// `bucketCount`. Numerator: session-filtered volume (raw shares); denominator:
/// `totalDailyVolume` (raw 24h daily_prices aggregate, NOT split-adjusted — must
/// match the raw shares in the trade stream). Final bucket value is therefore
/// `session_vol / daily_vol`, not 1.0 — by construction, so at inference time
/// `observed_cum / profile[bucket]` recovers the daily aggregate.
let computeDayCumFrac
    (trades: Trade[])
    (baseTicks: int64)
    (startTicks: int64)
    (closeTicks: int64)
    (bucketTicks: int64)
    (bucketCount: int)
    (totalDailyVolume: float) : float[] =
    let volumeInBucket = Array.zeroCreate<float> bucketCount
    for trade in trades do
        let tTicks = baseTicks + trade.TicksFromBase
        if tTicks >= startTicks && tTicks < closeTicks then
            let bucket = int ((tTicks - startTicks) / bucketTicks)
            volumeInBucket.[bucket] <- volumeInBucket.[bucket] + float trade.Volume
    // Cumulative volume, then divide by daily total.
    let cumFrac = Array.zeroCreate<float> bucketCount
    let mutable running = 0.0
    for i = 0 to bucketCount - 1 do
        running <- running + volumeInBucket.[i]
        cumFrac.[i] <- running / totalDailyVolume
    cumFrac

// ============================================================================
// Session boundaries
// ============================================================================

let startHoursFromBase = 8.5
let regularCloseHoursFromBase = 16.0
let earlyCloseHoursFromBase = 13.0

// ============================================================================
// Output JSON
// ============================================================================

let private writeArray (sb: StringBuilder) (xs: float[]) =
    sb.Append '[' |> ignore
    for i = 0 to xs.Length - 1 do
        if i > 0 then sb.Append ", " |> ignore
        sb.Append(xs.[i].ToString("R", Globalization.CultureInfo.InvariantCulture)) |> ignore
    sb.Append ']' |> ignore

let private writeProfileSection
    (sb: StringBuilder)
    (name: string)
    (closeTimeEt: string)
    (acc: ProfileAccumulator) =
    sb.AppendFormat("  \"{0}\": {{\n", name) |> ignore
    sb.AppendFormat("    \"close_time_et\": \"{0}\",\n", closeTimeEt) |> ignore
    sb.AppendFormat("    \"bucket_count\": {0},\n", acc.BucketCount) |> ignore
    sb.AppendFormat("    \"days_used\": {0},\n", acc.Count) |> ignore
    sb.Append "    \"profile\": " |> ignore
    writeArray sb (acc.Finalize())
    sb.Append "\n  }" |> ignore

// ============================================================================
// Driver
// ============================================================================

let run (inputPath: string) (outputPath: string) (secondsPerBar: float) =
    let rows = loadPlaysWithVolume inputPath
    printfn "Loaded %d rows from %s" rows.Length inputPath

    let bucketTicks = int64 (secondsPerBar * float TimeSpan.TicksPerSecond)
    let regularBucketCount =
        int ((regularCloseHoursFromBase - startHoursFromBase) * 3600.0 / secondsPerBar)
    let earlyBucketCount =
        int ((earlyCloseHoursFromBase - startHoursFromBase) * 3600.0 / secondsPerBar)
    let regularAcc = ProfileAccumulator(regularBucketCount)
    let earlyAcc = ProfileAccumulator(earlyBucketCount)

    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable skippedNoVolume = 0
    let mutable skippedMissingBin = 0
    let mutable skippedNoOpeningPrint = 0
    for row in rows do
        if row.RawVolume <= 0.0 then
            skippedNoVolume <- skippedNoVolume + 1
        else
            let info = { Directory = "data/trades_bin"; Ticker = row.Ticker; Date = row.Date }
            let path = infoPath info
            if not (File.Exists path) then
                skippedMissingBin <- skippedMissingBin + 1
            else
                let header, trades = loadDay info
                if header.OpeningPrintIndex = ValueNone then
                    skippedNoOpeningPrint <- skippedNoOpeningPrint + 1
                else
                    let baseTime = Timezone.baseTimeFromDateString row.Date
                    let baseTicks = baseTime.Ticks
                    let startTicks = baseTime.AddHours(startHoursFromBase).Ticks
                    let isEarly = Timezone.early_closes.Contains(DateOnly.FromDateTime baseTime)
                    let closeHours =
                        if isEarly then earlyCloseHoursFromBase else regularCloseHoursFromBase
                    let closeTicks = baseTime.AddHours(closeHours).Ticks
                    let acc = if isEarly then earlyAcc else regularAcc
                    let cumFrac =
                        computeDayCumFrac trades baseTicks startTicks closeTicks
                            bucketTicks acc.BucketCount row.RawVolume
                    acc.AddDay cumFrac
    sw.Stop()

    printfn ""
    printfn "Profile built in %.1fs" sw.Elapsed.TotalSeconds
    printfn "  regular-close days: %d" regularAcc.Count
    printfn "  early-close days:   %d" earlyAcc.Count
    printfn "  skipped (volume=0):        %d" skippedNoVolume
    printfn "  skipped (missing .bin):    %d" skippedMissingBin
    printfn "  skipped (no opening print): %d" skippedNoOpeningPrint

    let sb = StringBuilder()
    sb.Append "{\n" |> ignore
    sb.AppendFormat("  \"seconds_per_bar\": {0},\n",
        secondsPerBar.ToString("R", Globalization.CultureInfo.InvariantCulture)) |> ignore
    sb.Append "  \"start_time_et\": \"08:30:00\",\n" |> ignore
    writeProfileSection sb "regular_close" "16:00:00" regularAcc
    sb.Append ",\n" |> ignore
    writeProfileSection sb "early_close" "13:00:00" earlyAcc
    sb.Append "\n}\n" |> ignore
    File.WriteAllText(outputPath, sb.ToString())
    printfn "Wrote %s" outputPath
