module TradingEdge.Breakout.Engine

open System.Collections.Generic
open Nito.Collections
open TradingEdge.PitFader.Types
open TradingEdge.PitFader.Features

// ===========================================================================
// THE CONSOLIDATION BREAKOUT (user 2026-11-02): trend following out of a TIGHT, HIGH-VOLUME consolidation. "Large
// players have picked a spot to battle it out, and once one side wins, the market should go in a certain direction."
//
// THE INDICATOR: a variable-length window whose RIGHT edge is the current bar and whose LEFT edge floats. Every new bar
// is pushed on the back; then bars are popped off the front while the window's volume-weighted standard deviation of the
// bar vwaps is over the threshold (in TICKS, user ruling). Running sums Σv, Σv·x, Σv·x² (x = the vwap in ticks from the
// session's first bar) make each push/pop O(1); two monotonic deques carry the window's high and low.
// ⚠ σ is not monotone in the window's length, so the greedy left edge is not always the LONGEST suffix under the
// threshold; it is causal and never moves back.
//
// BO-2, THE STOP-AND-REVERSE SYSTEM (user 2026-11-02: "a 1m trend following system ... Inside the consolidation, it
// should be flipping between long and short until the release and then it should trail stop at the 1m low or high"),
// one independent state machine per family (σ threshold × volume floor; the floor = the window's volume in minutes of
// the root's average RTH volume over its previous 20 sessions — causal):
//   START (flat, entries open): the window of the PREVIOUS bars holds the floor's volume and is a new consolidation (its
//         left edge is past the last episode's consolidation), and this bar's vwap is strictly above its high → LONG, or
//         under its low → SHORT. The consolidation's high, low and last bar are FROZEN there.
//   FLIP (before the release): a long whose vwap goes strictly under the frozen low reverses to short; a short strictly
//         over the frozen high reverses to long. One fill does both (the old leg out, the new one in).
//   RELEASE: the window's left edge has passed the consolidation's last bar — every bar of the consolidation has been
//         popped, the price has moved too far for the σ to hold it. From then on the leg TRAILS: a long exits flat when
//         its vwap goes strictly under the 1m low (the min vwap of the bars in the previous 60 s, the bar itself
//         excluded), a short over the 1m high. No reverse; the family is flat and waits for the next consolidation.
//   Every signal fills at the next present bar's vwap; the tests run on every bar from a leg's fill bar on, never while a
//   fill is pending. A leg still open at the session's last bar exits at its vwap (eod); a flip filled on the last bar
//   only closes the old leg.
// Each LEG is one output row; the episode (all legs from one start) is the trade, summed post hoc.
// RTH, the front contract and the 1s present bars as PitFader. Starts at bars with t ≤ entryEndSec, none on the last bar;
// flips and trails run to the session's end.
// ===========================================================================

/// The floating-left-edge window over one session's bars at one σ threshold (ticks).
[<Sealed>]
type Window(bars: Bar[], tick: float, thrTicks: float) =
    let ref0 = bars.[0].vwap
    let thr2 = thrTicks * thrTicks
    let mutable l = 0
    let mutable r = -1
    let mutable sv = 0.0
    let mutable svx = 0.0
    let mutable svxx = 0.0
    let lo, hi = Deque<int>(), Deque<int>()
    let x i = (bars.[i].vwap - ref0) / tick
    let var () = let m = svx / sv in max 0.0 (svxx / sv - m * m)
    member _.L = l
    member _.R = r
    member _.Volume = sv
    member _.Bars = r - l + 1
    member _.MeanPx = ref0 + tick * svx / sv
    member _.SigmaTicks = sqrt (var ())
    member _.Hi = bars.[hi.[0]].vwap
    member _.Lo = bars.[lo.[0]].vwap
    member _.Push () =
        r <- r + 1
        let b = bars.[r]
        let xr = x r
        sv <- sv + b.volume; svx <- svx + b.volume * xr; svxx <- svxx + b.volume * xr * xr
        while lo.Count > 0 && bars.[lo.[lo.Count - 1]].vwap >= b.vwap do lo.RemoveFromBack() |> ignore
        lo.AddToBack r
        while hi.Count > 0 && bars.[hi.[hi.Count - 1]].vwap <= b.vwap do hi.RemoveFromBack() |> ignore
        hi.AddToBack r
        while l < r && var () > thr2 do
            let bl = bars.[l]
            let xl = x l
            sv <- sv - bl.volume; svx <- svx - bl.volume * xl; svxx <- svxx - bl.volume * xl * xl
            l <- l + 1
        if l = r then                                // one bar left: reset the sums exactly (no drift across a session)
            sv <- b.volume; svx <- b.volume * xr; svxx <- b.volume * xr * xr
        while lo.[0] < l do lo.RemoveFromFront() |> ignore
        while hi.[0] < l do hi.RemoveFromFront() |> ignore

/// The consolidation at an episode's start (the window of the bars before the start bar), frozen.
[<Struct>]
type Cons = { FirstT: int; LastI: int; Bars: int; Volume: float; SigmaTicks: float; Mean: float; Hi: float; Lo: float }

/// Why a leg ended.
type ExitReason = Flip = 0 | Trail = 1 | Eod = 2

/// One leg of an episode (⭐ mutable, feedback 2026-09-05).
type Leg =
    { ThrTicks: float
      FloorMin: float
      EpT: int                     // the episode's start bar (its id within the session and family)
      LegNo: int                   // 0 = the breakout leg, 1.. = after each flip
      Side: int
      SigT: int                    // the bar whose signal opened this leg
      Cons: Cons
      mutable EntryT: int
      mutable EntryPx: float
      mutable ReleasedT: int       // the bar the episode was released on, if during this leg; −1 = not released in it
      mutable ExitSigT: int
      mutable ExitT: int
      mutable ExitPx: float
      mutable Reason: ExitReason }

type private Pending = NoPend = 0 | Enter = 1 | Flip = 2 | Exit = 3

/// One family's state machine.
type private Family(thr: float, floor: float) =
    member val Leg : Leg option = None with get, set
    member val Pend = Pending.NoPend with get, set
    member val Released = false with get, set
    member val LastConsI = -1 with get, set       // the last episode's consolidation's last bar
    member _.Thr = thr
    member _.Floor = floor

/// One session's bars (sorted by t) → its legs and the feature snapshot at every episode's start bar (keyed by t; read
/// at that bar, inclusive: Features.Names then BarNames). vrefMin = the root's average RTH volume per minute.
let runSession (thrs: float[]) (floors: float[]) (tick: float) (vrefMin: float) (sessionStartSec: int) (entryEndSec: int)
               (bars: Bar[]) : ResizeArray<Leg> * Dictionary<int, float[]> =
    let legs = ResizeArray<Leg>()
    let snaps = Dictionary<int, float[]>()
    let n = bars.Length
    if n >= 3 then
        // the 1m low / high: the min / max vwap of the bars in [t − 60, t), the bar itself excluded (nan = none)
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
        let wins = thrs |> Array.map (fun th -> Window(bars, tick, th))
        let fams = Array.init thrs.Length (fun ti -> floors |> Array.map (fun fl -> Family(thrs.[ti], fl)))
        let feats, barF = VolBlock(), BarBlock sessionStartSec
        let openLeg (f: Family) epT legNo side sigT cons =
            f.Leg <- Some { ThrTicks = f.Thr; FloorMin = f.Floor; EpT = epT; LegNo = legNo; Side = side; SigT = sigT; Cons = cons
                            EntryT = -1; EntryPx = nan; ReleasedT = -1; ExitSigT = -1; ExitT = -1; ExitPx = nan
                            Reason = ExitReason.Eod }
        for i in 0 .. n - 1 do
            let b = bars.[i]
            feats.Push b; barF.Push b
            let canStart = b.t <= entryEndSec && i < n - 1
            let mutable started = false
            for ti in 0 .. thrs.Length - 1 do
                let win = wins.[ti]
                for f in fams.[ti] do
                    // 1. the fills of the previous bar's signals
                    match f.Pend, f.Leg with
                    | Pending.Enter, Some l ->
                        l.EntryT <- b.t; l.EntryPx <- b.vwap; f.Pend <- Pending.NoPend
                    | Pending.Flip, Some l ->
                        l.ExitT <- b.t; l.ExitPx <- b.vwap; l.Reason <- ExitReason.Flip; legs.Add l
                        f.Pend <- Pending.NoPend
                        if i < n - 1 then                  // on the last bar the reverse is not opened
                            openLeg f l.EpT (l.LegNo + 1) (-l.Side) l.ExitSigT l.Cons
                            f.Leg.Value.EntryT <- b.t; f.Leg.Value.EntryPx <- b.vwap
                        else f.Leg <- None
                    | Pending.Exit, Some l ->
                        l.ExitT <- b.t; l.ExitPx <- b.vwap; l.Reason <- ExitReason.Trail; legs.Add l
                        f.Leg <- None; f.Pend <- Pending.NoPend; f.Released <- false
                    | _ -> ()
                    // 2. a flat family: the start, on the window of the previous bars
                    if f.Leg.IsNone && canStart && win.R >= 0 && win.L > f.LastConsI && win.Volume >= f.Floor * vrefMin then
                        let side = if b.vwap > win.Hi then 1 elif b.vwap < win.Lo then -1 else 0
                        if side <> 0 then
                            let c = { FirstT = bars.[win.L].t; LastI = win.R; Bars = win.Bars; Volume = win.Volume
                                      SigmaTicks = win.SigmaTicks; Mean = win.MeanPx; Hi = win.Hi; Lo = win.Lo }
                            openLeg f b.t 0 side b.t c
                            f.Pend <- Pending.Enter; f.Released <- false; f.LastConsI <- c.LastI
                            started <- true
                win.Push ()
                // 3. the open legs (filled, nothing pending): the release, then the flip or the trail
                if i < n - 1 then
                    for f in fams.[ti] do
                        match f.Leg with
                        | Some l when f.Pend = Pending.NoPend && l.EntryT >= 0 ->
                            if not f.Released && win.L > l.Cons.LastI then
                                f.Released <- true; l.ReleasedT <- b.t
                            let s = float l.Side
                            if not f.Released then
                                let stop = if l.Side > 0 then l.Cons.Lo else l.Cons.Hi
                                if s * (b.vwap - stop) < 0.0 then
                                    l.ExitSigT <- b.t; f.Pend <- Pending.Flip
                            else
                                let trail = if l.Side > 0 then lo60.[i] else hi60.[i]
                                if s * (b.vwap - trail) < 0.0 then         // nan (no bar in the last 60 s) never trips
                                    l.ExitSigT <- b.t; f.Pend <- Pending.Exit
                        | _ -> ()
            if started then snaps.[b.t] <- Array.append (feats.Snapshot ()) (barF.Snapshot b)
        // the session's last bar: every open leg exits there (eod)
        let last = bars.[n - 1]
        for row in fams do
            for f in row do
                match f.Leg with
                | Some l when l.EntryT >= 0 ->
                    l.ExitT <- last.t; l.ExitPx <- last.vwap; l.Reason <- ExitReason.Eod; legs.Add l
                | _ -> ()
    legs, snaps
