module TradingEdge.Breakout.VolEngine

open System
open System.Collections.Generic
open Nito.Collections
open TradingEdge.PitFader.Types
open TradingEdge.PitFader.Features
open TradingEdge.Breakout.Engine

// ===========================================================================
// BO-3, THE FIXED-VOLUME WINDOW (user 2026-11-02: "Instead of trying to find the window at which the sigma is below a
// certain threshold we should instead be using total volume as the threshold ... the past 1k-10k contracts ... and then
// calculate the sigma of that volume. That would be easier to deal with").
//
// THE WINDOW at bar i = the most recent bars whose volume sums to EXACTLY V contracts: bars k+1..i whole, and bar k
// weighted by the remainder (the volume still missing), so every window holds V. It is WARM once the session has traded
// V. V = volMin × vref_min (the root's mean RTH volume per minute over its previous 20 sessions — causal; user ruling:
// minutes of average volume rather than a hand-picked count per instrument). Its volume-weighted mean and σ of the bar
// vwaps (prefix sums of v, v·x, v·x², x = ticks from the session's first bar) and its high / low (monotonic deques over
// bars k..i, the partial bar included).
// THE TIGHTNESS = σ ÷ the root's reference σ at the same V: the mean, over its previous 20 sessions, of each session's
// MEDIAN warm-bar σ (causal). < 1 = tighter than usual. ⭐ The engine's pre-filter is ratio ≤ maxRatio (default 1.0, the
// tighter half); finer cuts post hoc.
// THE SYSTEM is BO-2's stop-and-reverse (Engine.fs header), per family = volMin:
//   START (flat, entries open): the window of the PREVIOUS bars is warm, its ratio ≤ maxRatio, it is a new consolidation
//         (its left bar k is past the last episode's consolidation), and this bar's vwap is strictly above its high →
//         LONG, under its low → SHORT. The high, low and last bar are frozen.
//   FLIP before the release at the frozen other end; RELEASE = V more contracts have traded since the consolidation's
//   last bar (the volume clock's version of "every consolidation bar has left the window"); then TRAIL the 1m low/high.
// ===========================================================================

/// The fixed-volume window over one session's bars.
[<Sealed>]
type VolWindow(bars: Bar[], tick: float, vol: float) =
    let n = bars.Length
    let ref0 = bars.[0].vwap
    let x i = (bars.[i].vwap - ref0) / tick
    let C, P1, P2 = Array.zeroCreate (n + 1), Array.zeroCreate (n + 1), Array.zeroCreate (n + 1)   // prefix sums, [0] = 0
    do
        for i in 0 .. n - 1 do
            let v, xi = bars.[i].volume, x i
            C.[i + 1] <- C.[i] + v; P1.[i + 1] <- P1.[i] + v * xi; P2.[i + 1] <- P2.[i] + v * xi * xi
    let lo, hi = Deque<int>(), Deque<int>()
    let mutable r = -1
    let mutable k = 0
    let mutable mean = nan
    let mutable sigma = nan
    member _.R = r
    member _.K = k                                   // the window's oldest (partial) bar
    member _.Warm = r >= 0 && C.[r + 1] >= vol
    member _.Mean = mean
    member _.SigmaTicks = sigma
    member _.Hi = bars.[hi.[0]].vwap
    member _.Lo = bars.[lo.[0]].vwap
    /// the session's cumulative volume through bar i
    member _.Cum (i: int) = C.[i + 1]
    member _.Push () =
        r <- r + 1
        let v = bars.[r].vwap
        while lo.Count > 0 && bars.[lo.[lo.Count - 1]].vwap >= v do lo.RemoveFromBack() |> ignore
        lo.AddToBack r
        while hi.Count > 0 && bars.[hi.[hi.Count - 1]].vwap <= v do hi.RemoveFromBack() |> ignore
        hi.AddToBack r
        if C.[r + 1] >= vol then
            // k = the largest bar index with bars k..r holding at least vol (bars k+1..r hold less)
            while C.[r + 1] - C.[k + 1] >= vol do k <- k + 1
            let w = vol - (C.[r + 1] - C.[k + 1])             // bar k's part, in (0, v_k]
            let xk = x k
            let s1 = P1.[r + 1] - P1.[k + 1] + w * xk
            let s2 = P2.[r + 1] - P2.[k + 1] + w * xk * xk
            let m = s1 / vol
            mean <- ref0 + tick * m
            sigma <- sqrt (max 0.0 (s2 / vol - m * m))
            while lo.[0] < k do lo.RemoveFromFront() |> ignore
            while hi.[0] < k do hi.RemoveFromFront() |> ignore

[<Struct>]
type VCons = { FirstT: int; LastI: int; LastCum: float; Bars: int; SigmaTicks: float; Ratio: float; Mean: float; Hi: float; Lo: float }

/// One leg of an episode (⭐ mutable).
type VLeg =
    { VolMin: float
      Vol: float                   // V in contracts
      EpT: int
      LegNo: int
      Side: int
      SigT: int
      Cons: VCons
      mutable EntryT: int
      mutable EntryPx: float
      mutable ReleasedT: int
      mutable ExitSigT: int
      mutable ExitT: int
      mutable ExitPx: float
      mutable Reason: ExitReason }

type private Pending = NoPend = 0 | Enter = 1 | Flip = 2 | Exit = 3

type private VFamily(volMin: float, vol: float) =
    member val Leg : VLeg option = None with get, set
    member val Pend = Pending.NoPend with get, set
    member val Released = false with get, set
    member val LastConsI = -1 with get, set
    member _.VolMin = volMin
    member _.Vol = vol

/// One session → its legs, the feature snapshot at every episode's start bar, and per volMin the session's MEDIAN warm-bar
/// σ (ticks; nan if never warm) for the next sessions' reference. refSigma = per volMin the reference σ (nan = not yet
/// known: the session is run for its medians only, no signal can pass the ratio).
let runSession (volMins: float[]) (refSigma: float[]) (maxRatio: float) (tick: float) (vrefMin: float) (sessionStartSec: int)
               (entryEndSec: int) (bars: Bar[]) : ResizeArray<VLeg> * Dictionary<int, float[]> * float[] =
    let legs = ResizeArray<VLeg>()
    let snaps = Dictionary<int, float[]>()
    let n = bars.Length
    let medians = Array.create volMins.Length nan
    if n >= 3 then
        let lo60, hi60 = Array.create n nan, Array.create n nan
        do
            let qlo, qhi = Deque<int>(), Deque<int>()
            for i in 0 .. n - 1 do
                let t0 = bars.[i].t - 60
                while qlo.Count > 0 && bars.[qlo.[0]].t < t0 do qlo.RemoveFromFront() |> ignore
                while qhi.Count > 0 && bars.[qhi.[0]].t < t0 do qhi.RemoveFromFront() |> ignore
                if qlo.Count > 0 then
                    lo60.[i] <- bars.[qlo.[0]].vwap
                    hi60.[i] <- bars.[qhi.[0]].vwap
                let v = bars.[i].vwap
                while qlo.Count > 0 && bars.[qlo.[qlo.Count - 1]].vwap >= v do qlo.RemoveFromBack() |> ignore
                qlo.AddToBack i
                while qhi.Count > 0 && bars.[qhi.[qhi.Count - 1]].vwap <= v do qhi.RemoveFromBack() |> ignore
                qhi.AddToBack i
        let wins = volMins |> Array.map (fun m -> VolWindow(bars, tick, m * vrefMin))
        let fams = volMins |> Array.map (fun m -> VFamily(m, m * vrefMin))
        let sigs = volMins |> Array.map (fun _ -> ResizeArray<float>())
        let feats, barF = VolBlock(), BarBlock sessionStartSec
        let openLeg (f: VFamily) epT legNo side sigT cons =
            f.Leg <- Some { VolMin = f.VolMin; Vol = f.Vol; EpT = epT; LegNo = legNo; Side = side; SigT = sigT; Cons = cons
                            EntryT = -1; EntryPx = nan; ReleasedT = -1; ExitSigT = -1; ExitT = -1; ExitPx = nan
                            Reason = ExitReason.Eod }
        for i in 0 .. n - 1 do
            let b = bars.[i]
            feats.Push b; barF.Push b
            let canStart = b.t <= entryEndSec && i < n - 1
            let mutable started = false
            for vi in 0 .. volMins.Length - 1 do
                let win, f = wins.[vi], fams.[vi]
                // 1. the fills of the previous bar's signals
                match f.Pend, f.Leg with
                | Pending.Enter, Some l ->
                    l.EntryT <- b.t; l.EntryPx <- b.vwap; f.Pend <- Pending.NoPend
                | Pending.Flip, Some l ->
                    l.ExitT <- b.t; l.ExitPx <- b.vwap; l.Reason <- ExitReason.Flip; legs.Add l
                    f.Pend <- Pending.NoPend
                    if i < n - 1 then
                        openLeg f l.EpT (l.LegNo + 1) (-l.Side) l.ExitSigT l.Cons
                        f.Leg.Value.EntryT <- b.t; f.Leg.Value.EntryPx <- b.vwap
                    else f.Leg <- None
                | Pending.Exit, Some l ->
                    l.ExitT <- b.t; l.ExitPx <- b.vwap; l.Reason <- ExitReason.Trail; legs.Add l
                    f.Leg <- None; f.Pend <- Pending.NoPend; f.Released <- false
                | _ -> ()
                // 2. a flat family: the start, on the window of the previous bars
                if f.Leg.IsNone && canStart && win.Warm && win.K > f.LastConsI then
                    let ratio = win.SigmaTicks / refSigma.[vi]
                    if ratio <= maxRatio then                                  // nan (no reference yet) never passes
                        let side = if b.vwap > win.Hi then 1 elif b.vwap < win.Lo then -1 else 0
                        if side <> 0 then
                            let c = { FirstT = bars.[win.K].t; LastI = win.R; LastCum = win.Cum win.R; Bars = win.R - win.K + 1
                                      SigmaTicks = win.SigmaTicks; Ratio = ratio; Mean = win.Mean; Hi = win.Hi; Lo = win.Lo }
                            openLeg f b.t 0 side b.t c
                            f.Pend <- Pending.Enter; f.Released <- false; f.LastConsI <- c.LastI
                            started <- true
                win.Push ()
                if win.Warm then sigs.[vi].Add win.SigmaTicks
                // 3. the open leg (filled, nothing pending): the release, then the flip or the trail
                if i < n - 1 then
                    match f.Leg with
                    | Some l when f.Pend = Pending.NoPend && l.EntryT >= 0 ->
                        if not f.Released && win.Cum i - l.Cons.LastCum >= f.Vol then
                            f.Released <- true; l.ReleasedT <- b.t
                        let s = float l.Side
                        if not f.Released then
                            let stop = if l.Side > 0 then l.Cons.Lo else l.Cons.Hi
                            if s * (b.vwap - stop) < 0.0 then
                                l.ExitSigT <- b.t; f.Pend <- Pending.Flip
                        else
                            let trail = if l.Side > 0 then lo60.[i] else hi60.[i]
                            if s * (b.vwap - trail) < 0.0 then
                                l.ExitSigT <- b.t; f.Pend <- Pending.Exit
                    | _ -> ()
            if started then snaps.[b.t] <- Array.append (feats.Snapshot ()) (barF.Snapshot b)
        let last = bars.[n - 1]
        for f in fams do
            match f.Leg with
            | Some l when l.EntryT >= 0 ->
                l.ExitT <- last.t; l.ExitPx <- last.vwap; l.Reason <- ExitReason.Eod; legs.Add l
            | _ -> ()
        for vi in 0 .. volMins.Length - 1 do
            let s = sigs.[vi]
            if s.Count > 0 then
                let a = s.ToArray()
                Array.sortInPlace a
                medians.[vi] <- if a.Length % 2 = 1 then a.[a.Length / 2] else 0.5 * (a.[a.Length / 2 - 1] + a.[a.Length / 2])
    legs, snaps, medians
