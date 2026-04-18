#r "../TradingEdge.Orb/bin/Release/net10.0/TradingEdge.Orb.dll"

// Export per-bar diagnostics + decisions for a list of (ticker, date) days,
// running the gated ORB pipeline. Output JSON consumed by the false-positive
// chart script.

open System
open System.IO
open System.Text
open System.Globalization
open TradingEdge.Orb
open TradingEdge.Orb.TradeLoader
open TradingEdge.Orb.TradeBinary
open TradingEdge.Orb.Pipeline
open TradingEdge.Orb.Program

// ----- CLI -----
// Usage: dotnet fsi export_false_positive_diagnostics.fsx \
//           <profile.json> <rvol-threshold> <out-dir> <TICKER1:DATE1> [TICKER2:DATE2 ...]
let args = fsi.CommandLineArgs
if args.Length < 5 then
    eprintfn "Usage: export_false_positive_diagnostics.fsx <profile.json> <rvol-threshold> <out-dir> <TICKER:DATE>..."
    exit 1

let profilePath = args.[1]
let rvolThreshold = Double.Parse(args.[2], CultureInfo.InvariantCulture)
let outDir = args.[3]
let keys =
    [| for i in 4 .. args.Length - 1 ->
        let parts = args.[i].Split ':'
        parts.[0], parts.[1] |]

Directory.CreateDirectory outDir |> ignore

let loadedProfile = VolumeProfile.load profilePath
printfn "Loaded profile from %s (bucketSeconds=%g)" profilePath loadedProfile.SecondsPerBar

// ----- Per-day metadata (avg_volume_4w, split factor) from augmented plays JSON -----
// Small local loader rather than calling private Program internals.
type DayMeta = { AvgVolume4w: float; Volume: float; RawVolume: float }
let loadMeta () =
    let path = "data/continuation_plays_augmented.json"
    let bytes = File.ReadAllBytes path
    use doc = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory bytes)
    let d = System.Collections.Generic.Dictionary<struct (string * string), DayMeta>()
    for el in doc.RootElement.EnumerateArray() do
        let ticker = el.GetProperty("ticker").GetString()
        let date = el.GetProperty("date").GetString()
        let avgVol = el.GetProperty("avg_volume_4w").GetDouble()
        let volume = el.GetProperty("volume").GetDouble()
        let rawVolume =
            match el.TryGetProperty("raw_volume") with
            | true, v -> v.GetDouble()
            | _ -> nan
        if not (Double.IsNaN rawVolume) then
            d.[struct (ticker, date)] <- { AvgVolume4w = avgVol; Volume = volume; RawVolume = rawVolume }
    d
let meta = loadMeta ()
printfn "Loaded meta for %d days" meta.Count

// Unix nanoseconds from .NET DateTime (UTC).
let dtToUnixNs (dt: DateTime) =
    let epoch = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    let dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc)
    int64 ((dt - epoch).Ticks * 100L)

let jstr (s: string) =
    let sb = StringBuilder()
    sb.Append '"' |> ignore
    for c in s do
        match c with
        | '"' -> sb.Append "\\\"" |> ignore
        | '\\' -> sb.Append "\\\\" |> ignore
        | '\n' -> sb.Append "\\n" |> ignore
        | '\r' -> sb.Append "\\r" |> ignore
        | '\t' -> sb.Append "\\t" |> ignore
        | c when int c < 0x20 -> sb.AppendFormat("\\u{0:x4}", int c) |> ignore
        | c -> sb.Append c |> ignore
    sb.Append '"' |> ignore
    sb.ToString()

let jnum (x: float) =
    if Double.IsNaN x || Double.IsInfinity x then "null"
    else x.ToString("R", CultureInfo.InvariantCulture)

let exportOne (ticker: string) (date: string) =
    printfn ""
    printfn "==== %s %s ====" ticker date
    let info = { Directory = "data/trades_bin"; Ticker = ticker; Date = date }
    let header, trades = loadDay info
    if header.OpeningPrintIndex.IsNone then
        eprintfn "%s %s: no opening print, skipping" ticker date
    else
        match meta.TryGetValue(struct (ticker, date)) with
        | false, _ ->
            eprintfn "%s %s: no meta entry in continuation_plays_augmented, skipping" ticker date
        | true, m ->
            // Build the gate by hand (mirrors Program.buildGate).
            let baseTime = DateTime header.BaseTicks
            let isEarly = Timezone.early_closes.Contains(DateOnly.FromDateTime baseTime)
            let sessionProfile = if isEarly then loadedProfile.EarlyClose else loadedProfile.RegularClose
            let startTicks = baseTime.AddHours(VolumeProfile.startHoursFromBase).Ticks
            let bucketTicks = int64 (loadedProfile.SecondsPerBar * float TimeSpan.TicksPerSecond)
            let splitFactor = if m.Volume > 0.0 then m.RawVolume / m.Volume else 1.0
            let rawAvg4w = m.AvgVolume4w * splitFactor
            let gate = ValueSome {
                Profile = sessionProfile
                StartTicks = startTicks
                BucketTicks = bucketTicks
                RawAvg4w = rawAvg4w
                EntryThreshold = rvolThreshold
            }

            let seg = SegregateTrades(TimeSpan.FromSeconds loadedProfile.SecondsPerBar, baseTime)
            seg.OpeningPrintIdx <- header.OpeningPrintIndex
            let vs = OrbSystem(positionSize, referenceVol, stopMode, gate)
            let td = TrackDecisions()
            let ell = EnforceLossLimit((fun () -> td.RealizedPnL), infinity)

            // Per-bar diagnostics, logged once per completed bar.
            let barRows = ResizeArray<_>()
            let mutable lastBarVwap = Double.NaN
            let mutable lastBarVolume = Double.NaN
            let logBar (bar: OrbSystemBar voption) (tradeTs: DateTime) =
                match bar with
                | ValueNone -> ()
                | ValueSome b ->
                    // Dedup: ArgsBuilder emits a fresh bar only on bar boundaries,
                    // but the SegregateTrades wrapper clears lastBar to ValueNone
                    // before each trade — so every ValueSome we see here is a new
                    // bar emission. Still guard against identical back-to-backs.
                    if b.Bar.VWAP <> lastBarVwap || b.Bar.Volume <> lastBarVolume then
                        lastBarVwap <- b.Bar.VWAP
                        lastBarVolume <- b.Bar.Volume
                        let bucket = int ((tradeTs.Ticks - gate.Value.StartTicks) / gate.Value.BucketTicks)
                        let inRange = bucket >= 0 && bucket < sessionProfile.BucketCount
                        let profileFrac =
                            if inRange then sessionProfile.Profile.[bucket] else nan
                        let predictedDaily =
                            if profileFrac > 0.0 then b.SessionCumVolume / profileFrac
                            else nan
                        let predictedRvol =
                            if predictedDaily > 0.0 then predictedDaily / rawAvg4w
                            else nan
                        barRows.Add((tradeTs, b, bucket, profileFrac, predictedRvol))
            let onTracked (_: TradingDecision voption, _: OrbSystemBar voption, _: TradeStage, _: Trade) = ()
            for i in 0 .. trades.Length - 1 do
                seg.Process(
                    (fun (bar, stage, trade) ->
                        let tradeTs = seg.Timestamp trade
                        logBar bar tradeTs
                        vs.Process(
                            (fun (decision, bar, stage, trade) ->
                                ell.Process(
                                    (fun (decision, bar, stage, trade) ->
                                        td.Process(onTracked, decision, bar, stage, trade)),
                                    decision, bar, stage, trade)),
                            bar, stage, trade, tradeTs)),
                    trades.[i], i)

            let dayPnL = td.RealizedPnL
            printfn "  bars=%d decisions=%d dayPnL=%.2f"
                barRows.Count td.Decisions.Count dayPnL

            // ----- Write JSON -----
            let sb = StringBuilder()
            sb.Append "{\n" |> ignore
            sb.AppendFormat("  \"ticker\": {0},\n", jstr ticker) |> ignore
            sb.AppendFormat("  \"date\": {0},\n", jstr date) |> ignore
            sb.AppendFormat("  \"bucket_seconds\": {0},\n", jnum loadedProfile.SecondsPerBar) |> ignore
            sb.AppendFormat("  \"is_early_close\": {0},\n", (if isEarly then "true" else "false")) |> ignore
            sb.AppendFormat("  \"avg_volume_4w\": {0},\n", jnum m.AvgVolume4w) |> ignore
            sb.AppendFormat("  \"volume_adj\": {0},\n", jnum m.Volume) |> ignore
            sb.AppendFormat("  \"volume_raw\": {0},\n", jnum m.RawVolume) |> ignore
            sb.AppendFormat("  \"split_factor\": {0},\n", jnum splitFactor) |> ignore
            sb.AppendFormat("  \"raw_avg_4w\": {0},\n", jnum rawAvg4w) |> ignore
            sb.AppendFormat("  \"rvol_threshold\": {0},\n", jnum rvolThreshold) |> ignore
            sb.AppendFormat("  \"day_pnl\": {0},\n", jnum dayPnL) |> ignore
            sb.AppendFormat("  \"session_start_ticks\": {0},\n", startTicks) |> ignore
            sb.AppendFormat("  \"session_start_ns\": {0},\n", dtToUnixNs (DateTime(startTicks, DateTimeKind.Utc))) |> ignore
            sb.AppendFormat("  \"bucket_count\": {0},\n", sessionProfile.BucketCount) |> ignore

            // Profile (full session cumulative fraction curve). Shared x-axis
            // units with bars via "bucket" index.
            sb.Append "  \"session_profile\": [" |> ignore
            for i = 0 to sessionProfile.Profile.Length - 1 do
                if i > 0 then sb.Append ',' |> ignore
                sb.Append(jnum sessionProfile.Profile.[i]) |> ignore
            sb.Append "],\n" |> ignore

            // Per-bar series.
            sb.Append "  \"bars\": [\n" |> ignore
            for i = 0 to barRows.Count - 1 do
                let (ts, b, bucket, profileFrac, predictedRvol) = barRows.[i]
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "    {{\"ts_ns\": {0}, \"bucket\": {1}, \"vwap\": {2}, \"stddev\": {3}, \"volume\": {4}, \"session_cum_volume\": {5}, \"range_high\": {6}, \"range_low\": {7}, \"vol_factor\": {8}, \"profile_frac\": {9}, \"predicted_rvol\": {10}}}",
                    dtToUnixNs ts,
                    bucket,
                    jnum b.Bar.VWAP,
                    jnum b.Bar.StdDev,
                    jnum b.Bar.Volume,
                    jnum b.SessionCumVolume,
                    jnum b.RangeHigh,
                    jnum b.RangeLow,
                    jnum b.VolFactor,
                    jnum profileFrac,
                    jnum predictedRvol) |> ignore
                if i < barRows.Count - 1 then sb.Append ',' |> ignore
                sb.Append '\n' |> ignore
            sb.Append "  ],\n" |> ignore

            // Decisions (entries and exits).
            sb.Append "  \"decisions\": [\n" |> ignore
            for i = 0 to td.Decisions.Count - 1 do
                let d = td.Decisions.[i]
                // Compute predicted RVOL at the decision timestamp (same formula).
                let bucket = int ((d.Timestamp.Ticks - gate.Value.StartTicks) / gate.Value.BucketTicks)
                let prvol =
                    if bucket >= 0 && bucket < sessionProfile.BucketCount then
                        let frac = sessionProfile.Profile.[bucket]
                        // We don't have session_cum_volume directly here; find the
                        // closest prior bar row.
                        let mutable cum = 0.0
                        for r in barRows do
                            let (ts, b, _, _, _) = r
                            if ts <= d.Timestamp then cum <- b.SessionCumVolume
                        if frac > 0.0 && cum > 0.0 then (cum / frac) / rawAvg4w
                        else nan
                    else nan
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "    {{\"ts_ns\": {0}, \"price\": {1}, \"shares\": {2}, \"predicted_rvol\": {3}}}",
                    dtToUnixNs d.Timestamp,
                    jnum d.Price,
                    d.Shares,
                    jnum prvol) |> ignore
                if i < td.Decisions.Count - 1 then sb.Append ',' |> ignore
                sb.Append '\n' |> ignore
            sb.Append "  ]\n" |> ignore

            sb.Append "}\n" |> ignore

            let dir = Path.Combine(outDir, ticker)
            Directory.CreateDirectory dir |> ignore
            let outPath = Path.Combine(dir, $"{date}.json")
            File.WriteAllText(outPath, sb.ToString())
            printfn "  wrote %s" outPath

for (t, d) in keys do
    exportOne t d
