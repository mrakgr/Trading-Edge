module TradingEdge.PitFader.Engine

open TradingEdge.RollingMa
open TradingEdge.PitFader.Types
open TradingEdge.PitFader.Features
open TradingEdge.PitFader.Leg

// ===========================================================================
// PitFader — the BASIC futures mean reversion (user 2026-11-01): "Buy at 20m low, sell at 5m high and vice versa for
// shorts." FlushFader's channel rules (TradingEdge.FlushFader/Intraday.fs), on one root's front contract per day,
// REGULAR TRADING HOURS only (user 2026-11-01: liquidity, "for starters"):
//   LONG  ENTRY SIGNAL: the vwap is strictly UNDER the min of the PRIOR EntryBars present bars' vwaps, once that
//         channel is WARM (EntryBars bars folded since the session start) and before the entry cutoff.
//         FILL: the NEXT present bar's vwap.
//         EXIT SIGNAL: the vwap strictly OVER the max of the prior ExitBars bars' vwaps, checked from the fill bar on.
//         FILL: the next bar's vwap.
//   SHORT the mirror (the 20m high in, the 5m low out).
//   ONE trip at a time per side (mc = 1); the long and short books are independent.
//   A trip still open at the session's last bar (the last bar before the flat time) exits at that bar's vwap (Eod).
// ⭐ BAR CLOCK (user 2026-11-01: "We'll stick to bar clock features for now"): every window counts PRESENT bars, as in
// FlushFader / SpikeFader, whose research features are to be ported here.
// ===========================================================================

type Trip =
    { Side: int              // +1 long, −1 short
      SigT: int              // the entry signal's bar
      EntryT: int; EntryPx: float
      ExitT: int; ExitPx: float
      Eod: bool
      OpenAtSignal: int      // trips of this side already open when the signal fired (0 = the first of its run)
      Features: float[]      // Leg.AllNames, read at the signal bar (inclusive)
      AuxExitT: int[]        // ⭐ the AUX exits (user 2026-11-01: "let's try a longer target"): per aux channel, the exit
      AuxExitPx: float[]     //   at the first new N-bar opposite extreme at or after the fill bar, filled at the next bar;
      AuxEod: bool[] }       //   the session's last bar when none came (eod). Index = the run's aux channel list.

/// One open trip: its exits per channel (0 = the primary), −1 until filled, and the pending flags.
type private Open =
    { SigT: int; EntryT: int; EntryPx: float; Oas: int; Feat: float[]
      ExT: int[]; ExPx: float[]; Pend: bool[] }

/// One side's book. ⭐ mc = 0 (maxConcurrent 0, THE RESEARCH DEFAULT — user 2026-11-01: "more data samples ... to better
/// observe the effects of filters"): every bar that makes a new channel extreme is another trip (averaging down), and
/// every open trip exits at the first exit signal at or after its fill bar (shared exits, as FlushFader's sampler).
/// maxConcurrent k > 0 = a real book: a signal is taken only while fewer than k trips are open on the PRIMARY exit.
/// Greedy mc-replay of the mc = 0 output by signal time reproduces the mc = 1 book (an exit signal and an entry signal
/// never share a bar: a new N-bar opposite extreme is never a new 20m entry extreme).
/// Exit channel 0 is the primary (ExitT / ExitPx; it sets the depth); the others are the aux marks. A trip retires when
/// every channel has filled.
type private Book(side: float, maxConcurrent: int, nExits: int) =
    let opened = ResizeArray<Open>()
    let mutable pendSig = -1                     // the previous bar's entry signal, filled now
    let mutable pendOpen = 0
    let mutable pendFeat : float[] = null
    let emit (o: Open) (eod: bool[]) (trips: ResizeArray<Trip>) =
        trips.Add { Side = int side; SigT = o.SigT; EntryT = o.EntryT; EntryPx = o.EntryPx
                    ExitT = o.ExT.[0]; ExitPx = o.ExPx.[0]; Eod = eod.[0]; OpenAtSignal = o.Oas; Features = o.Feat
                    AuxExitT = o.ExT.[1..]; AuxExitPx = o.ExPx.[1..]; AuxEod = eod.[1..] }
    let openPrimary () =
        let mutable c = 0
        for o in opened do if o.ExT.[0] < 0 then c <- c + 1
        c
    member _.Step (b: Bar, ent: float, exts: float[], canSignal: bool, snap: unit -> float[], trips: ResizeArray<Trip>) =
        let mutable w = 0
        for i in 0 .. opened.Count - 1 do
            let o = opened.[i]
            for k in 0 .. nExits - 1 do
                if o.Pend.[k] then
                    o.ExT.[k] <- b.t; o.ExPx.[k] <- b.vwap; o.Pend.[k] <- false
            if Array.forall (fun t -> t >= 0) o.ExT then emit o (Array.create nExits false) trips
            else
                opened.[w] <- o
                w <- w + 1
        if w < opened.Count then opened.RemoveRange(w, opened.Count - w)
        if pendSig >= 0 then
            opened.Add { SigT = pendSig; EntryT = b.t; EntryPx = b.vwap; Oas = pendOpen; Feat = pendFeat
                         ExT = Array.create nExits -1; ExPx = Array.create nExits nan; Pend = Array.zeroCreate nExits }
            pendSig <- -1; pendFeat <- null
        if canSignal then
            let mutable primaryExit = false
            for k in 0 .. nExits - 1 do
                if side * (b.vwap - exts.[k]) > 0.0 then
                    for o in opened do
                        if o.ExT.[k] < 0 && not o.Pend.[k] then
                            o.Pend.[k] <- true
                            if k = 0 then primaryExit <- true
            let nOpen = openPrimary ()
            if not primaryExit && side * (ent - b.vwap) > 0.0 && (maxConcurrent = 0 || nOpen < maxConcurrent) then
                pendSig <- b.t; pendOpen <- nOpen; pendFeat <- snap ()
    /// the session's last bar: every channel still open exits at its vwap (eod)
    member _.Flatten (last: Bar, trips: ResizeArray<Trip>) =
        for o in opened do
            let eod = Array.init nExits (fun k -> o.ExT.[k] < 0)
            for k in 0 .. nExits - 1 do
                if o.ExT.[k] < 0 then
                    o.ExT.[k] <- last.t
                    o.ExPx.[k] <- last.vwap
            emit o eod trips

/// Both books over one session's bars (sorted by t). Entry signals only at bars with t <= entryEndSec, once the entry
/// channel is warm; no signal of either kind on the last bar (it has no next bar to fill at).
/// ⭐ WARMTH as in FlushFader / SpikeFader (`channelWarm = entryMin.Count = WindowSize`, read AFTER the bar is pushed):
/// the channel counts the signal bar itself, so the first possible signal is the EntryBars-th bar, tested against the
/// EntryBars − 1 bars before it. The cutoff is inclusive, as their `inWindow`.
let runSession (entryBars: int) (exitBars: int) (auxExitBars: int[]) (sessionStartSec: int) (entryEndSec: int) (maxConcurrent: int) (bars: Bar[]) : ResizeArray<Trip> =
    let trips = ResizeArray<Trip>()
    let n = bars.Length
    if n >= 3 then
        let mnE, mxE = MinMa entryBars, MaxMa entryBars
        let xs = Array.append [| exitBars |] auxExitBars
        let mnX, mxX = xs |> Array.map MinMa, xs |> Array.map MaxMa
        let long, short = Book(1.0, maxConcurrent, xs.Length), Book(-1.0, maxConcurrent, xs.Length)
        let feats, barF = VolBlock(), BarBlock sessionStartSec
        let legL, legS = LegBlock(1, exitBars, 30), LegBlock(-1, exitBars, 30)
        let inline get (s: float voption) = match s with ValueSome x -> x | ValueNone -> nan
        for i in 0 .. n - 1 do
            let b = bars.[i]
            let warm = mnE.Count + 1 >= entryBars      // the channel, the signal bar included, holds EntryBars bars
            let entryOk = warm && b.t <= entryEndSec
            // the features are read at the signal bar, inclusive; the legs' resets wait until after the entry
            feats.Push b; barF.Push b
            let ep = feats.EffPairPartial ()
            legL.Push(b, ep); legS.Push(b, ep)
            let last = i = n - 1
            // an entry signal needs the channel warm and the cutoff not passed; an exit signal only a next bar.
            // (Passing ent = nan disables the entry test: nan comparisons are false.)
            long.Step(b, (if entryOk then get mnE.State else nan), mxX |> Array.map (fun m -> get m.State), not last,
                      (fun () -> Array.concat [ feats.Snapshot (); barF.Snapshot b; legL.SnapshotAndCount b ]), trips)
            short.Step(b, (if entryOk then get mxE.State else nan), mnX |> Array.map (fun m -> get m.State), not last,
                       (fun () -> Array.concat [ feats.Snapshot (); barF.Snapshot b; legS.SnapshotAndCount b ]), trips)
            legL.Finish b; legS.Finish b
            mnE.Push b.vwap; mxE.Push b.vwap
            for m in mnX do m.Push b.vwap
            for m in mxX do m.Push b.vwap
        long.Flatten(bars.[n - 1], trips); short.Flatten(bars.[n - 1], trips)
    trips
