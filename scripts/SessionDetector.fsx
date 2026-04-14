// Rolling-volume detector primitives.
//
// RollingVolumeWindow      — sliding-window sum of shares over a fixed TimeSpan.
// SessionStartDetector     — uses two RollingVolumeWindows (short + long)
//                            to detect the transition from premarket to
//                            regular-hours trading via a volume-rate spike.
//
// Designed to be #load'ed from calibration scripts and later lifted into
// the new VWAP trading project.

open System
open System.Collections.Generic

/// Sliding-window sum of (timestamp, volume) pairs. Input timestamps must
/// be non-decreasing. On each Add, evicts any queued entries whose
/// timestamp has fallen out of the window (ts < now - WindowSpan).
type RollingVolumeWindow(windowSpan: TimeSpan) =
    let queue = Queue<struct (DateTime * float)>()
    let mutable currentSum = 0.0

    member _.WindowSpan = windowSpan
    member _.Volume = currentSum
    member _.Count = queue.Count

    member _.Add(ts: DateTime, vol: float) =
        queue.Enqueue(struct (ts, vol))
        currentSum <- currentSum + vol
        let cutoff = ts - windowSpan
        while queue.Count > 0 && (let struct (t, _) = queue.Peek() in t < cutoff) do
            let struct (_, v) = queue.Dequeue()
            currentSum <- currentSum - v

    member _.Clear() =
        queue.Clear()
        currentSum <- 0.0

/// State of the session-start detector.
type SessionStartState =
    | Armed
    | Triggered of DateTime

/// Detects the transition from premarket to regular trading by comparing a
/// short rolling volume window against a long rolling baseline. Triggers when
/// the short-window per-second rate is at least K times the long-window rate
/// AND the short-window volume clears an absolute floor. The date-specific
/// 09:30 ET cutoff gates triggering — the detector silently feeds windows
/// during premarket but will not fire until the wall clock has reached the
/// regular-hours start for the trading date in question.
type SessionStartDetector(
        tradingDate: DateOnly,
        ?shortSpan: TimeSpan,
        ?longSpan: TimeSpan,
        ?rateRatioK: float,
        ?volumeFloor: float) =

    let shortSpan   = defaultArg shortSpan   (TimeSpan.FromSeconds 30.0)
    let longSpan    = defaultArg longSpan    (TimeSpan.FromMinutes 5.0)
    let rateRatioK  = defaultArg rateRatioK  10.0
    let volumeFloor = defaultArg volumeFloor 1000.0

    // Date-specific 09:30 ET cutoff in UTC. DST-aware.
    let easternTz =
        try TimeZoneInfo.FindSystemTimeZoneById("America/New_York")
        with _ -> TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")
    let cutoffUtc =
        let local = DateTime(
            tradingDate.Year, tradingDate.Month, tradingDate.Day,
            9, 30, 0, DateTimeKind.Unspecified)
        TimeZoneInfo.ConvertTimeToUtc(local, easternTz)

    let shortW = RollingVolumeWindow(shortSpan)
    let longW  = RollingVolumeWindow(longSpan)
    let mutable state = Armed

    member _.ShortWindow = shortW
    member _.LongWindow = longW
    member _.RateRatioK = rateRatioK
    member _.VolumeFloor = volumeFloor
    member _.SessionStartCutoff = cutoffUtc
    member _.State = state

    /// Feed one trade. `ts` is UTC. `size` is shares (float to match the
    /// downstream VolumeBar pipeline; int/i32 values convert cleanly). If the
    /// detector triggers on this trade, onNext is invoked once with the
    /// trigger timestamp.
    ///
    /// Trigger rule: the 30s "short" rate must exceed K times the baseline
    /// rate, where baseline = (long_sum - short_sum) over (long_span -
    /// short_span). The short-window contribution is subtracted from the
    /// long window so the baseline reflects only historical activity, not
    /// the current burst. Without this, a single large print lands in both
    /// windows at once and the ratio caps at long_span / short_span.
    member inline self.Process(onNext, ts: DateTime, size: float) =
        self.ShortWindow.Add(ts, size)
        self.LongWindow.Add(ts, size)
        match self.State with
        | Triggered _ -> ()
        | Armed when ts >= self.SessionStartCutoff ->
            let shortSpan = self.ShortWindow.WindowSpan.TotalSeconds
            let longSpan  = self.LongWindow.WindowSpan.TotalSeconds
            let baselineSum  = self.LongWindow.Volume - self.ShortWindow.Volume
            let baselineSpan = longSpan - shortSpan
            let shortRate = self.ShortWindow.Volume / shortSpan
            let baselineRate =
                if baselineSpan > 0.0 then baselineSum / baselineSpan else 0.0
            let ratioOk = baselineRate > 0.0 && shortRate >= self.RateRatioK * baselineRate
            let floorOk = self.ShortWindow.Volume >= self.VolumeFloor
            if ratioOk && floorOk then
                self.SetTriggered ts
                onNext ts
        | Armed -> ()

    // Backing setter so `Process` can stay `inline` while the field is private.
    member _.SetTriggered(ts: DateTime) = state <- Triggered ts
