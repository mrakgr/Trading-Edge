module TradingEdge.Breakout.AbsEngine

open System.Collections.Generic
open Nito.Collections
open TradingEdge.PitFader.Types
open TradingEdge.PitFader.Features

// ===========================================================================
// BO-6, ABSORPTION (user 2026-11-02: "Maybe instead of looking at consolidations, we should be looking for strong delta
// that isn't confirmed by price movement?" — the user's 2026-04 thesis: persistent one-sided flow while price does not
// move marks a level where a passive order absorbs the aggression; "Short below those key levels, long above them").
//
// DETECTION at bar i, per window N present bars (the bars (i − N, i]; needs i ≥ N):
//   imb   = Σ delta ÷ Σ volume (the sign = the AGGRESSORS: + buyers lifting asks, − sellers hitting bids)
//   imbr  = |imb| ÷ the root's reference |imb| at N: the mean over its previous 20 sessions of each session's median
//           |imb| (causal) — the raw scale differs by root (ZN's median 30-bar |imb| 0.30, ES's 0.095) and shrinks with N
//   rv    = Σ volume ÷ (N × the root's mean volume per present bar over its previous 20 sessions — causal)
//   move  = sign(imb) × (vwap_i − vwap_{i−N}) / tick: the move IN THE AGGRESSORS' DIRECTION (≤ 0 = against them)
//   ratio = move ÷ the root's reference N-bar move: the mean over its previous 20 sessions of each session's median
//           |vwap_i − vwap_{i−N}| in ticks (causal). A small ratio = the flow did not move the price = ABSORPTION.
//   The LOOSE pre-filter: imbr ≥ minImbR, rv ≥ minRv, ratio ≤ maxRatio; finer cuts post hoc. After a detection, that N
//   and aggressor side cannot detect again for N bars (no overlapping windows).
// TWO FAMILIES (user ruling 2026-11-02: both):
//   FADE  — at the detection bar, AGAINST the aggressors (sellers absorbed → long). The stop: strictly beyond the window's
//           extreme on the aggressors' side (a long's under the window low, a short's over the window high).
//   LEVEL — the window's range [lo, hi] is ARMED from the next bar for up to LevelSec seconds: the first vwap strictly
//           above hi → LONG, under lo → SHORT (the user's "long above, short below"). The stop: the range's other end.
//           `side × aggr < 0` = the break went AGAINST the aggressors (the absorber won); > 0 = the aggressors broke it.
// FILL: the next present bar's vwap. EXITS at the first bar at or after entry_t + H for H in Horizons (eod if the session
// ends first); the stop's signal = the first bar from the entry bar on strictly through it, filled at the next bar; it
// applies to horizon H when stop_sig_t < exit_t_H (post hoc).
// ⭐ THE CONFIRMATION (user 2026-11-02: delta "as confirmation to enter into trades or to cut them quickly ... we could even
// flip the trade"): every trip records d30 / d60 = side × Σ delta ÷ Σ volume over the bars entry_t ≤ t ≤ entry_t + 30 / 60
// (known at that time), so a cut or a flip at +30 / +60 s is scored post hoc against exit_px_30 / exit_px_60.
// Entries (both families) at signal bars with t ≤ entryEndSec, none on the last bar.
// ===========================================================================

let Horizons = [| 30; 60; 300; 900 |]

/// A fixed-length sliding min / max over bar vwaps.
[<Sealed>]
type private Extremes(bars: Bar[], n: int) =
    let lo, hi = Deque<int>(), Deque<int>()
    member _.Push (i: int) =
        let v = bars.[i].vwap
        while lo.Count > 0 && bars.[lo.[lo.Count - 1]].vwap >= v do lo.RemoveFromBack() |> ignore
        lo.AddToBack i
        while hi.Count > 0 && bars.[hi.[hi.Count - 1]].vwap <= v do hi.RemoveFromBack() |> ignore
        hi.AddToBack i
        while lo.[0] <= i - n do lo.RemoveFromFront() |> ignore
        while hi.[0] <= i - n do hi.RemoveFromFront() |> ignore
    member _.Lo = bars.[lo.[0]].vwap
    member _.Hi = bars.[hi.[0]].vwap

[<Struct>]
type Detection = { N: int; Aggr: int; DetT: int; Imb: float; ImbR: float; Rv: float; Move: float; Ratio: float; Vwap: float; Hi: float; Lo: float; Vol: float }

/// ⭐ mutable (feedback 2026-09-05)
type ATrip =
    { Fam: int                     // 0 = FADE, 1 = LEVEL
      Det: Detection
      Side: int
      SigT: int
      StopPx: float
      mutable EntryI: int
      mutable EntryT: int
      mutable EntryPx: float
      ExitT: int[]
      ExitPx: float[]
      Eod: bool[]
      mutable StopSigT: int
      mutable StopT: int
      mutable StopFillPx: float
      mutable StopPend: bool
      mutable D30: float
      mutable D60: float }

let private newTrip fam det side sigT stop =
    { Fam = fam; Det = det; Side = side; SigT = sigT; StopPx = stop; EntryI = -1; EntryT = -1; EntryPx = nan
      ExitT = Array.create Horizons.Length -1; ExitPx = Array.create Horizons.Length nan; Eod = Array.zeroCreate Horizons.Length
      StopSigT = -1; StopT = -1; StopFillPx = nan; StopPend = false; D30 = nan; D60 = nan }

/// One session → its trips, the feature snapshot at every signal bar (keyed by t), and per N the session's median
/// |N-bar move| in ticks and median |imb| (nan if the session is shorter) for the next sessions' references. refMove /
/// refImb = per N the references (nan = not known yet: no detection passes). vrefBar = the root's mean volume per bar.
let runSession (ns: int[]) (refMove: float[]) (refImb: float[]) (minImbR: float) (minRv: float) (maxRatio: float) (levelSec: int) (tick: float)
               (vrefBar: float) (sessionStartSec: int) (entryEndSec: int) (bars: Bar[])
               : ResizeArray<ATrip> * Dictionary<int, float[]> * float[] * float[] =
    let trips = ResizeArray<ATrip>()
    let snaps = Dictionary<int, float[]>()
    let n = bars.Length
    let medMove, medImb = Array.create ns.Length nan, Array.create ns.Length nan
    if n >= 3 then
        let C, D, PV = Array.zeroCreate (n + 1), Array.zeroCreate (n + 1), Array.zeroCreate (n + 1)
        for i in 0 .. n - 1 do
            C.[i + 1] <- C.[i] + bars.[i].volume; D.[i + 1] <- D.[i] + bars.[i].delta
            PV.[i + 1] <- PV.[i] + bars.[i].volume * bars.[i].vwap
        let ext = ns |> Array.map (fun w -> Extremes(bars, w))
        let nextOk = Array2D.create ns.Length 2 0                    // [N, aggr: 0 = sellers / 1 = buyers]: next allowed bar
        let feats, barF = VolBlock(), BarBlock sessionStartSec
        let opened = ResizeArray<ATrip>()
        let armed = ResizeArray<Detection>()
        let moves, imbs = ns |> Array.map (fun _ -> ResizeArray<float>()), ns |> Array.map (fun _ -> ResizeArray<float>())
        for i in 0 .. n - 1 do
            let b = bars.[i]
            feats.Push b; barF.Push b
            for e in ext do e.Push i
            // 1. the open trips: fills first, then the horizons, then the stop's signal
            let mutable w = 0
            for k in 0 .. opened.Count - 1 do
                let o = opened.[k]
                if o.EntryT < 0 then
                    o.EntryI <- i
                    o.EntryT <- b.t
                    o.EntryPx <- b.vwap
                elif o.StopPend then
                    o.StopT <- b.t
                    o.StopFillPx <- b.vwap
                    o.StopPend <- false
                let mutable allDone = true
                for h in 0 .. Horizons.Length - 1 do
                    if o.ExitT.[h] < 0 then
                        if b.t >= o.EntryT + Horizons.[h] then
                            o.ExitT.[h] <- b.t
                            o.ExitPx.[h] <- b.vwap
                        else allDone <- false
                if not allDone && o.StopSigT < 0 && float o.Side * (b.vwap - o.StopPx) < 0.0 then
                    o.StopSigT <- b.t
                    o.StopPend <- true
                if not (allDone && not o.StopPend) then
                    opened.[w] <- o
                    w <- w + 1
                else trips.Add o
            if w < opened.Count then opened.RemoveRange(w, opened.Count - w)
            let canSignal = b.t <= entryEndSec && i < n - 1
            let mutable signalled = false
            // 2. the armed levels: the first break, or expiry
            let mutable wa = 0
            for k in 0 .. armed.Count - 1 do
                let d = armed.[k]
                if b.t <= d.DetT + levelSec then
                    let side = if b.vwap > d.Hi then 1 elif b.vwap < d.Lo then -1 else 0
                    if side <> 0 then
                        if canSignal then
                            opened.Add(newTrip 1 d side b.t (if side > 0 then d.Lo else d.Hi))
                            signalled <- true
                    else
                        armed.[wa] <- d
                        wa <- wa + 1
            if wa < armed.Count then armed.RemoveRange(wa, armed.Count - wa)
            // 3. the detections on the windows ending at this bar
            for ni in 0 .. ns.Length - 1 do
                let nb = ns.[ni]
                if i >= nb then
                    let mv = (b.vwap - bars.[i - nb].vwap) / tick
                    moves.[ni].Add(abs mv)
                    let vol = C.[i + 1] - C.[i + 1 - nb]
                    if vol > 0.0 then
                        let imb = (D.[i + 1] - D.[i + 1 - nb]) / vol
                        imbs.[ni].Add(abs imb)
                        let imbr = abs imb / refImb.[ni]
                        let aggr = if imb > 0.0 then 1 elif imb < 0.0 then -1 else 0
                        let ai = if aggr > 0 then 1 else 0
                        if aggr <> 0 && i >= nextOk.[ni, ai] && imbr >= minImbR then          // nan never passes
                            let rv = vol / (float nb * vrefBar)
                            let move = float aggr * mv
                            let ratio = move / refMove.[ni]
                            if rv >= minRv && ratio <= maxRatio then                // nan (no reference yet) never passes
                                let d = { N = nb; Aggr = aggr; DetT = b.t; Imb = imb; ImbR = imbr; Rv = rv; Move = move; Ratio = ratio
                                          Vwap = (PV.[i + 1] - PV.[i + 1 - nb]) / vol; Hi = ext.[ni].Hi; Lo = ext.[ni].Lo; Vol = vol }
                                nextOk.[ni, ai] <- i + nb
                                if canSignal then
                                    let side = -aggr
                                    opened.Add(newTrip 0 d side b.t (if side > 0 then d.Lo else d.Hi))
                                    armed.Add d
                                    signalled <- true
            if signalled then snaps.[b.t] <- Array.append (feats.Snapshot ()) (barF.Snapshot b)
        let last = bars.[n - 1]
        for o in opened do
            if o.EntryT >= 0 then
                for h in 0 .. Horizons.Length - 1 do
                    if o.ExitT.[h] < 0 then
                        o.ExitT.[h] <- last.t
                        o.ExitPx.[h] <- last.vwap
                        o.Eod.[h] <- true
                if o.StopPend then
                    o.StopT <- last.t
                    o.StopFillPx <- last.vwap
                    o.StopPend <- false
                trips.Add o
        // the confirmation: the delta over [entry_t, entry_t + 30 / 60]
        let ts = bars |> Array.map (fun b -> b.t)
        for o in trips do
            let i0 = o.EntryI
            let upto s =
                let mutable j = System.Array.BinarySearch(ts, o.EntryT + s)
                if j < 0 then j <- ~~~j - 1                                  // the last bar with t ≤ entry_t + s
                let v = C.[j + 1] - C.[i0]
                if v > 0.0 then float o.Side * (D.[j + 1] - D.[i0]) / v else nan
            o.D30 <- upto 30
            o.D60 <- upto 60
        let median (l: ResizeArray<float>) =
            let a = l.ToArray()
            if a.Length = 0 then nan
            else
                Array.sortInPlace a
                if a.Length % 2 = 1 then a.[a.Length / 2] else 0.5 * (a.[a.Length / 2 - 1] + a.[a.Length / 2])
        for ni in 0 .. ns.Length - 1 do
            medMove.[ni] <- median moves.[ni]
            medImb.[ni] <- median imbs.[ni]
    trips, snaps, medMove, medImb
