module TradingEdge.Breakout.MomEngine

open TradingEdge.RollingMa
open TradingEdge.PitFader.Types
open TradingEdge.PitFader.Features
open TradingEdge.PitFader.Leg

// ===========================================================================
// BO-7, THE 1m BREAKOUT (user 2026-11-02: "create a simple 1m breakout system. 1m channels could determine momentum. Then
// on top of that we could have various 1-60s volume and volume delta features. Then after the trade has been entered we
// could have marks at 1-60s to see what the price change and the delta was ... whether entering when the delta is
// lopsided, holding the trade when it continues being strong, and exiting when it gets weak makes sense." — "we'd also
// want the leg counters and the zscore, coil and eff features that the PitFader system has").
//   LONG  ENTRY SIGNAL: the vwap strictly ABOVE the max of the PRIOR EntryBars present bars' vwaps, the channel warm
//         (PitFader's warmth: the channel, the signal bar included, holds EntryBars bars), before the entry cutoff.
//   SHORT the mirror (strictly under the prior min). FILL: the next present bar's vwap.
//   EXIT (user ruling: "Fixed 60 s only"): the first bar at or after entry_t + 60 s, at its vwap (the session's last bar
//         when none: eod). ⭐ REPEAT ENTRIES (user 2026-11-02: "let's allow repeat entries. It will make analysis
//         easier"): every bar that makes a new channel extreme is another trip, each with its own 60 s exit (mc = 0).
// BEFORE THE ENTRY (read at the signal bar, inclusive): for k in PreBars, vol_k = Σ volume of the last k bars and
//   dimb_k = side × Σ delta ÷ Σ volume (> 0 = the aggressors WITH the break); + PitFader's sealed features — VolBlock
//   (Features.Names), BarBlock (BarNames) and the LEG block of the break's direction: a LONG break reads PitFader's SHORT
//   instance (its EXT = new 20m HIGHS, the move being joined), a SHORT break the long instance (LegNames, unchanged).
// AFTER THE ENTRY — THE MARKS, m in Marks seconds: the first bar at or after entry_t + m: its t and vwap, and dimb / volume
//   of the bars from the entry bar to it inclusive (⚠ the mark bar's own delta and its vwap are concurrent; a decision
//   at mark m scored from a LATER mark is causal). The 60 s mark IS the exit. A mark past the session's end = nan.
// RTH, the front contract and the 1s present bars as PitFader.
// ===========================================================================

let Marks = [| 1; 2; 3; 5; 10; 15; 20; 30; 45; 60 |]
let PreBars = [| 1; 2; 5; 10; 15; 20; 30; 45; 60 |]

type MTrip =
    { Side: int
      SigI: int
      SigT: int
      PreVol: float[]
      PreDimb: float[]
      Features: float[]            // Features.Names ++ BarNames ++ LegNames (the break's direction)
      mutable EntryI: int
      mutable EntryT: int
      mutable EntryPx: float
      MarkT: int[]
      MarkPx: float[]
      MarkDimb: float[]
      MarkVol: float[]
      mutable Eod: bool }

let runSession (entryBars: int) (sessionStartSec: int) (entryEndSec: int) (bars: Bar[]) : ResizeArray<MTrip> =
    let trips = ResizeArray<MTrip>()
    let n = bars.Length
    if n >= 3 then
        let C, D = Array.zeroCreate (n + 1), Array.zeroCreate (n + 1)
        for i in 0 .. n - 1 do
            C.[i + 1] <- C.[i] + bars.[i].volume
            D.[i + 1] <- D.[i] + bars.[i].delta
        let mn, mx = MinMa entryBars, MaxMa entryBars
        let feats, barF = VolBlock(), BarBlock sessionStartSec
        let legL, legS = LegBlock(1, 300, 30), LegBlock(-1, 300, 30)      // PitFader's configuration
        let opened = ResizeArray<MTrip>()
        let inline get (s: float voption) = match s with ValueSome x -> x | ValueNone -> nan
        let close (o: MTrip) =
            // the marks from the bars entry..exit
            let mutable j = o.EntryI
            for k in 0 .. Marks.Length - 1 do
                while j < n && bars.[j].t < o.EntryT + Marks.[k] do j <- j + 1
                if j < n then
                    let v = C.[j + 1] - C.[o.EntryI]
                    o.MarkT.[k] <- bars.[j].t
                    o.MarkPx.[k] <- bars.[j].vwap
                    o.MarkVol.[k] <- v
                    o.MarkDimb.[k] <- if v > 0.0 then float o.Side * (D.[j + 1] - D.[o.EntryI]) / v else nan
            trips.Add o
        for i in 0 .. n - 1 do
            let b = bars.[i]
            let warm = mn.Count + 1 >= entryBars
            let entryOk = warm && b.t <= entryEndSec && i < n - 1
            feats.Push b; barF.Push b
            let ep = feats.EffPairPartial ()
            legL.Push(b, ep); legS.Push(b, ep)
            let mutable w = 0
            for k in 0 .. opened.Count - 1 do
                let o = opened.[k]
                if o.EntryT < 0 then
                    o.EntryI <- i
                    o.EntryT <- b.t
                    o.EntryPx <- b.vwap
                if b.t >= o.EntryT + 60 then close o
                else
                    opened.[w] <- o
                    w <- w + 1
            if w < opened.Count then opened.RemoveRange(w, opened.Count - w)
            if entryOk then
                for s in 0 .. 1 do
                    let side = if s = 0 then 1 else -1
                    let lvl = if s = 0 then get mx.State else get mn.State
                    if float side * (b.vwap - lvl) > 0.0 then
                        let leg = if side > 0 then legS else legL              // the leg of the move being joined
                        let pv, pd = Array.zeroCreate PreBars.Length, Array.zeroCreate PreBars.Length
                        for k in 0 .. PreBars.Length - 1 do
                            let lo = max 0 (i + 1 - PreBars.[k])
                            let v = C.[i + 1] - C.[lo]
                            pv.[k] <- v
                            pd.[k] <- if v > 0.0 then float side * (D.[i + 1] - D.[lo]) / v else nan
                        let tr =
                            { Side = side; SigI = i; SigT = b.t; PreVol = pv; PreDimb = pd
                              Features = Array.concat [ feats.Snapshot (); barF.Snapshot b; leg.SnapshotAndCount b ]
                              EntryI = -1; EntryT = -1; EntryPx = nan
                              MarkT = Array.create Marks.Length -1; MarkPx = Array.create Marks.Length nan
                              MarkDimb = Array.create Marks.Length nan; MarkVol = Array.create Marks.Length nan
                              Eod = false }
                        opened.Add tr
            legL.Finish b; legS.Finish b
            mn.Push b.vwap; mx.Push b.vwap
        // the session's end: an open trip's missing marks stay nan; its exit is the last bar (eod)
        for o in opened do
            if o.EntryT >= 0 then
                o.Eod <- true
                close o
                let k = Marks.Length - 1
                if o.MarkT.[k] < 0 then
                    o.MarkT.[k] <- bars.[n - 1].t
                    o.MarkPx.[k] <- bars.[n - 1].vwap
                    let v = C.[n] - C.[o.EntryI]
                    o.MarkVol.[k] <- v
                    o.MarkDimb.[k] <- if v > 0.0 then float o.Side * (D.[n] - D.[o.EntryI]) / v else nan
    trips
