module TradingEdge.Breakout.LevelEngine

open System.Collections.Generic
open TradingEdge.PitFader.Types

/// An exact running median: a max-heap of the lower half, a min-heap of the upper half (sizes equal, or the lower one
/// larger by one); the median of an even count is the mean of the two middles.
type RunningMedian() =
    let lo = PriorityQueue<float, float>()        // the lower half, keyed −x: a max-heap
    let hi = PriorityQueue<float, float>()        // the upper half, keyed x: a min-heap
    member _.Count = lo.Count + hi.Count
    member _.Add (x: float) =
        if lo.Count = 0 || x <= lo.Peek() then lo.Enqueue(x, -x) else hi.Enqueue(x, x)
        if lo.Count > hi.Count + 1 then (let y = lo.Dequeue() in hi.Enqueue(y, y))
        elif hi.Count > lo.Count then (let y = hi.Dequeue() in lo.Enqueue(y, -y))
    member _.Median = if lo.Count = 0 then nan elif lo.Count > hi.Count then lo.Peek() else 0.5 * (lo.Peek() + hi.Peek())

// ===========================================================================
// LV-1, THE KEY-LEVEL TREND (user 2026-11-03: a trend-following system that decides by LEVELS — "large orders as key
// levels ... this was always the principle"; memory project_levels_trend_2026-11-03). Every decay is in CLOCK time
// (a missing second decays like any other), every EWMA by its HALF-LIFE.
//   THE PRIORITY: a 5 s half-life EWMA of the 1s bars' VOLUME, V(t) = Σ v_j·e^(−λv·(t − t_j)) (user: "we don't even need
//         overlapping 5s windows ... just make it an EWMA with a 5s half-life"); its price = the same EWMA of the vwap,
//         P(t) = Σ v_j·p_j·e^(−λv·(t − t_j)) / V(t).
//   THE KEY LEVEL: the session's max of the priorities DECAYED with a 5 min half-life — a bar's V(t_i) is worth
//         V(t_i)·e^(−λL·(now − t_i)) now. Every priority decays alike, so the order between past bars never changes:
//         the max is the bar with the largest ln V(t_i) + λL·t_i, and a new bar only has to beat the current champion
//         (exact, no queue). TIES: the newest bar wins (user). The level = the champion's P (its 5 s EWMA vwap).
//   THE BIAS: a 30 s half-life, VOLUME-weighted EWMA of the 1s vwaps — the mean m and the variance (σ²); LONG when
//         m > level + K·σ, SHORT when m < level − K·σ (K = 2), else the position is kept: stop-and-reverse, always in
//         after the first signal.
//   EXECUTION: a signal at bar i fills at the NEXT present bar's vwap (BO-7's rule); a reversal exits and enters on that
//         bar. No signal before the session start + WarmSec, nor on the session's last bar. After the entry cutoff a
//         reversal signal only EXITS (reason "close"); a position still held at the end exits at the last bar (eod).
//   THE LEVEL'S SIGNIFICANCE (LV-3, user 2026-11-03: "trade only when the champion's volume is large compared with the
//         session's typical priority" — recorded, bucketed in SQL): TypMed = the session-to-date RUNNING MEDIAN of every
//         bar's priority V (two heaps, exact; the signal bar included), LevelNow = the champion's priority decayed to the
//         signal, V(t_c)·e^(−λL·(t − t_c)). The ratios level_vol / typ_med and level_now / typ_med are the SQL's.
//   LV-4 (user 2026-11-03): THE BAND may be absolute — K ticks (`Ticks`) or K × the root's causal 1-minute ATR (`Atr`:
//         the mean 1-minute range of the 1s vwaps over its previous sessions, Program.fs) — instead of K × σ (`Sigma`);
//         and THE LEVEL may be the session VWAP (`Vwap`: the cumulative RTH Σv·p / Σv, "worked very well in one paper on
//         Nasdaq" — Zarattini & Aziz's QQQ study: long above the VWAP, short below) instead of the key level (`Key`).
//   THE BASELINE (user 2026-11-03: "just buying at the open and staying long if the open holds, otherwise going
//         short"): `Open` = the level is the session's first bar's vwap; `OpenLong` = a LONG signal on the first bar (it
//         fills on the second), the warm-up then only delays the first reversal.
//   THE MARKS: m in Marks seconds after the entry — the first bar at or after entry_t + m, its t and vwap, whatever the
//         exit (an exit study reads them); past the session's end nan.
// RTH, the front contract and the 1s present bars as PitFader (Program.fs). The level and the EWMAs restart each session.
// ===========================================================================

let Marks = [| 5; 10; 30; 60; 120; 300; 600; 1200; 1800 |]

type LevelKind =
    | Key                      // the decayed session max of the 5 s volume priority
    | Vwap                     // the session VWAP
    | Open                     // the session's opening price (the first bar's vwap): the baseline (user 2026-11-03)

type BandKind =
    | Sigma                    // K × the bias's σ
    | Ticks                    // K ticks
    | Atr                      // K × the root's 1-minute ATR (causal: previous sessions)

/// the mean 1-minute range of the 1s vwaps over a session's clock minutes (the ATR reference's per-session value)
let minuteRange (bars: Bar[]) =
    let mutable sum, n = 0.0, 0
    let mutable i = 0
    while i < bars.Length do
        let m = bars.[i].t / 60
        let mutable hi, lo = bars.[i].vwap, bars.[i].vwap
        while i < bars.Length && bars.[i].t / 60 = m do
            hi <- max hi bars.[i].vwap
            lo <- min lo bars.[i].vwap
            i <- i + 1
        sum <- sum + (hi - lo)
        n <- n + 1
    if n = 0 then nan else sum / float n

type LevelConfig =
    { VolHalfLife: float       // the priority's EWMA (5 s)
      LevelHalfLife: float     // the key level's decay (300 s)
      BiasHalfLife: float      // the bias's mean and variance (30 s)
      KSigma: float            // the band around the level: K in the band's unit (σ, ticks or ATRs; 2.0)
      WarmSec: int             // no signal in the session's first WarmSec seconds
      Level: LevelKind         // Key (LV-1..3), Vwap (LV-4) or Open (the baseline)
      Band: BandKind           // Sigma (LV-1..3), Ticks or Atr (LV-4)
      OpenLong: bool }         // a long from the first bar (the baseline's literal rule)

let defaultLevelConfig = { VolHalfLife = 5.0; LevelHalfLife = 300.0; BiasHalfLife = 30.0; KSigma = 2.0; WarmSec = 300; Level = Key; Band = Sigma; OpenLong = false }

type LTrip =
    { Side: int
      SigI: int
      SigT: int
      Level: float             // the key level at the signal: the champion's 5 s EWMA vwap
      LevelT: int              // the champion bar's second
      LevelVol: float          // the champion's priority V(t_i), undecayed
      Mean: float              // the bias's mean and σ at the signal
      Sigma: float
      VolNow: float            // the signal bar's own V(t)
      TypMed: float            // the session-to-date running median of every bar's V (the signal bar included)
      LevelNow: float          // the champion's priority decayed to the signal
      BandW: float             // the band's half-width at the signal, in price
      mutable EntryI: int
      mutable EntryT: int
      mutable EntryPx: float
      mutable ExitSigT: int
      mutable ExitT: int
      mutable ExitPx: float
      mutable Reason: string   // flip | close | eod
      MarkT: int[]
      MarkPx: float[] }

let runSession (cfg: LevelConfig) (tick: float) (atr: float) (sessionStartSec: int) (entryEndSec: int) (bars: Bar[]) : ResizeArray<LTrip> =
    let trips = ResizeArray<LTrip>()
    let n = bars.Length
    if n >= 3 then
        let ln2 = log 2.0
        let lv, lL, lb = ln2 / cfg.VolHalfLife, ln2 / cfg.LevelHalfLife, ln2 / cfg.BiasHalfLife
        // the priority's EWMA (V, P·V), the bias's (Σw, Σw·p, Σw·p²), the champion
        let mutable vSum, pvSum = 0.0, 0.0
        let mutable w, wp, wp2 = 0.0, 0.0, 0.0
        let mutable champ = -infinity
        let mutable level, levelT, levelVol = nan, -1, nan
        let mutable prevT = -1
        let med = RunningMedian()
        let mutable cumV, cumPV = 0.0, 0.0          // the session VWAP (Level = Vwap)
        // the position: the trip held (entered, or waiting for its fill), and a trip waiting for its exit fill
        let mutable held : LTrip option = None
        let mutable exiting : LTrip option = None
        let warmT = sessionStartSec + cfg.WarmSec
        for i in 0 .. n - 1 do
            let b = bars.[i]
            // 1. the fills of the previous bar's decisions, at this bar's vwap
            match exiting with
            | Some x ->
                x.ExitT <- b.t
                x.ExitPx <- b.vwap
                trips.Add x
                exiting <- None
            | None -> ()
            match held with
            | Some h when h.EntryT < 0 ->
                h.EntryI <- i
                h.EntryT <- b.t
                h.EntryPx <- b.vwap
            | _ -> ()
            // 2. the EWMAs, decayed in clock time since the previous present bar
            if prevT >= 0 then
                let dt = float (b.t - prevT)
                let av, ab = exp (-lv * dt), exp (-lb * dt)
                vSum <- vSum * av; pvSum <- pvSum * av
                w <- w * ab; wp <- wp * ab; wp2 <- wp2 * ab
            vSum <- vSum + b.volume
            pvSum <- pvSum + b.volume * b.vwap
            w <- w + b.volume
            wp <- wp + b.volume * b.vwap
            wp2 <- wp2 + b.volume * b.vwap * b.vwap
            prevT <- b.t
            med.Add vSum
            cumV <- cumV + b.volume
            cumPV <- cumPV + b.volume * b.vwap
            // 3. the champion: ln V + λL·t, the newest on a tie
            match cfg.Level with
            | Key ->
                if vSum > 0.0 then
                    let score = log vSum + lL * float b.t
                    if score >= champ then
                        champ <- score
                        level <- pvSum / vSum
                        levelT <- b.t
                        levelVol <- vSum
            | Vwap ->
                if cumV > 0.0 then
                    level <- cumPV / cumV
                    levelT <- b.t
                    levelVol <- cumV
            | Open ->
                if i = 0 then
                    level <- b.vwap
                    levelT <- b.t
                    levelVol <- b.volume
            // 4. the bias and the signal
            if cfg.OpenLong && i = 0 && n > 1 then
                held <-
                    Some { Side = 1; SigI = 0; SigT = b.t; Level = level; LevelT = levelT; LevelVol = levelVol
                           Mean = b.vwap; Sigma = 0.0; VolNow = vSum; TypMed = med.Median; LevelNow = levelVol; BandW = 0.0
                           EntryI = -1; EntryT = -1; EntryPx = nan; ExitSigT = -1; ExitT = -1; ExitPx = nan; Reason = ""
                           MarkT = Array.create Marks.Length -1; MarkPx = Array.create Marks.Length nan }
            elif w > 0.0 && b.t >= warmT && i < n - 1 && not (System.Double.IsNaN level) then
                let mean = wp / w
                let sigma = sqrt (max 0.0 (wp2 / w - mean * mean))
                let band = cfg.KSigma * (match cfg.Band with Sigma -> sigma | Ticks -> tick | Atr -> atr)
                let want =
                    if mean > level + band then 1
                    elif mean < level - band then -1
                    else 0
                let pos = match held with Some h -> h.Side | None -> 0
                if want <> 0 && want <> pos then
                    match held with
                    | Some h when h.EntryT >= 0 ->
                        h.ExitSigT <- b.t
                        h.Reason <- (if b.t <= entryEndSec then "flip" else "close")
                        exiting <- Some h
                        held <- None
                    | Some _ -> held <- None          // unreachable: a held trip is filled on the bar after its signal
                    | None -> ()
                    if b.t <= entryEndSec then
                        held <-
                            Some { Side = want; SigI = i; SigT = b.t; Level = level; LevelT = levelT; LevelVol = levelVol
                                   Mean = mean; Sigma = sigma; VolNow = vSum; TypMed = med.Median
                                   LevelNow = levelVol * exp (-lL * float (b.t - levelT)); BandW = band
                                   EntryI = -1; EntryT = -1; EntryPx = nan; ExitSigT = -1; ExitT = -1; ExitPx = nan; Reason = ""
                                   MarkT = Array.create Marks.Length -1; MarkPx = Array.create Marks.Length nan }
        // the session's end: a trip still held exits at the last bar
        match held with
        | Some h when h.EntryT >= 0 ->
            let last = bars.[n - 1]
            h.ExitSigT <- last.t
            h.ExitT <- last.t
            h.ExitPx <- last.vwap
            h.Reason <- "eod"
            trips.Add h
        | _ -> ()
        // the marks: from each entry, the first bar at or after entry_t + m
        for tr in trips do
            let mutable j = tr.EntryI
            for k in 0 .. Marks.Length - 1 do
                while j < n && bars.[j].t < tr.EntryT + Marks.[k] do j <- j + 1
                if j < n then
                    tr.MarkT.[k] <- bars.[j].t
                    tr.MarkPx.[k] <- bars.[j].vwap
    trips
