module TradingEdge.PitFader.Leg

open System
open TradingEdge.RollingMa
open TradingEdge.PitFader.Types
open TradingEdge.PitFader.Features

// ===========================================================================
// STAGE 2b — the LEG block, one per side (user 2026-11-01: the stage-2 port by hand, SIDE-NEUTRAL; "We'll also need
// equivalent features for the short system"). The long instance is FlushFader's leg machinery, the short one
// SpikeFader's mirror; one class, `side` = +1 / −1. Neutral words:
//   EXT  = the side's ENTRY extreme (a new 20m LOW for a long, a new 20m HIGH for a short) — the leg's events;
//   OPP  = the opposite extreme (the 20m high for a long) — it ends the leg;
//   FLOW = the leg's FIRST ext event (FlushFader's "first low");
//   WT / CT = a WITH tick (toward the ext: a downtick for a long) / a COUNTER tick.
// The bar order is the research engines' (FlushFader Intraday.fs Process): Push = steps 1–4 (snapshot the strictly-prior
// channels; fold the bar; breach counters; the leg events), then the entry snapshot, then Finish = step 7 (the resets,
// AFTER the entry, so an entry reads the pre-reset counters).
// The K ladder: k = ext events since FLOW, reset by a new 20m OPP; k_N = the same events, reset by a new N-bar OPP
// (k_300 is FlushFader's lows_since_first_low_300 — the averaging-down depth of the gate-free sampler).
// Prices are RAW and unsigned (chg_* = vwap / anchor − 1: negative on a long, positive on a short).
// ===========================================================================

let LegNames : string[] =
    [| yield! [ "k"; "k_600"; "k_300"; "k_180"; "k_120"; "k_60"; "k_30"
                "bars_since_first"; "secs_since_first"; "bars_since_first_300"; "bars_since_first_600"; "trade_idx" ]
       for w in [ "sess"; "1200"; "600"; "300"; "120"; "60"; "30" ] do yield $"breach_opp_{w}"
       for w in [ "sess"; "1200"; "600"; "300"; "120"; "60"; "30" ] do yield $"breach_ext_{w}"
       yield! [ "opp_30"; "opp_60"; "opp_120"; "opp_180"; "chan_ext"; "chan_opp"; "exit_chan"
                "chan_ext_prev_60"; "chan_ext_prev_120"; "chan_ext_prev_180"; "first_ext_vwap"
                "ols_slope_since_opp"; "ols_r_since_opp"; "bars_since_opp"; "ols_slope_since_flow"; "ols_r_since_flow"
                "eff_since_opp"; "eff_since_flow"; "eff9_since_opp"; "eff9_since_flow"; "z_since_opp"; "z_since_flow"
                "slots_since_opp"; "slots_since_flow"
                "d_opp_flow"; "ols_slope_opp_flow"; "ols_r_opp_flow"; "eff_opp_flow"
                "arm_opp_eff_20m"; "arm_opp_eff_10m"; "arm_opp_slots_20m"; "arm_opp_slots_10m"
                "vol_leg"; "tc_leg"; "dv_leg"
                "wt_since_ct"; "run_wt"; "secs_since_ct"; "wt_15"; "wt_30"; "wt_60"; "wt_120"; "ct_15"; "ct_30"; "ct_60"; "ct_120"
                "wt_flow"; "ct_flow"; "ext_since_ct"; "chg_since_ct"; "chg_since_run_pre_ext"; "chg_since_run_first_ext"
                "chg_since_run_first_wt" ] |]

let inline private vv (v: float voption) = match v with ValueSome x -> x | ValueNone -> nan
let private chW = [| 30; 60; 120; 180; 300; 600; 1200 |]
let private ci w = Array.IndexOf(chW, w)

/// One per (contract, session, side). `exitBars` = the exit channel (the trip's target), recorded as exit_chan.
[<Sealed>]
type LegBlock(side: int, exitBars: int, slotBars: int) =
    let isLong = side > 0
    let maxs, mins = chW |> Array.map MaxMa, chW |> Array.map MinMa
    let sessHigh, sessLow = RunMaxMa<float>(), RunMinMa<float>()
    let pMax, pMin = Array.create chW.Length ValueNone, Array.create chW.Length ValueNone
    let mutable pSessHi, pSessLo = ValueNone, ValueNone
    let metaOpp = MaxMaMeta<struct (float * float * int * int)> 1200   // the long's arming HIGH
    let metaOppS = MinMaMeta<struct (float * float * int * int)> 1200  // the short's arming LOW
    let mutable pMeta : struct (float * float * int * int) voption = ValueNone
    // breach counters: HI = a new N-bar high, LO = a new N-bar low (+ session)
    let brHi, brLo = chW |> Array.map (fun _ -> BreachCounter()), chW |> Array.map (fun _ -> BreachCounter())
    let brHiSess, brLoSess = BreachCounter(), BreachCounter()
    // the K ladder: index by rung (1200 = the leg itself)
    let rungW = [| 1200; 600; 300; 180; 120; 60; 30 |]
    let counters = rungW |> Array.map (fun _ -> LegCounters())
    let leg = counters.[0]
    let mutable tradeIdx = 0
    // leg-anchored structures
    let olsSinceOpp, olsSinceFlow = AnchoredOls(), AnchoredOls()
    let effSinceOpp, effSinceFlow = AnchoredEff slotBars, AnchoredEff slotBars
    let extLag60, extLag120, extLag180 = LagMa<float> 60, LagMa<float> 120, LagMa<float> 180
    let mutable cumVol, cumTc, cumDv = 0.0, 0.0, 0.0
    let mutable legVolAnchor, legTcAnchor, legDvAnchor = nan, nan, nan
    let mutable dropToFlow, dropSlope, dropR, dropEff = nan, nan, nan, nan
    let mutable armEff20, armEff10, armSlots20, armSlots10 = nan, nan, 0, 0
    let mutable firstExtVwap = nan
    // tick direction
    let mutable prevV = nan
    let wtS = [| 15; 30; 60; 120 |] |> Array.map SumMa
    let ctS = [| 15; 30; 60; 120 |] |> Array.map SumMa
    let mutable wtSinceCt, wtRun, extSinceCt = 0, 0, 0
    let mutable lastCtSec, lastCtVwap = -1, nan
    let mutable runPreExtVwap, runFirstExtVwap, runFirstWtVwap = nan, nan, nan
    let mutable wtFlow, ctFlow = -1, -1
    // this bar's events, kept for Finish
    let mutable isNewExt, isNewOpp = false, false
    let mutable priorExt, priorOpp, priorExit = nan, nan, nan

    /// steps 1–4. `effPair` = VolBlock.EffPairPartial() after this bar's VolBlock push.
    member _.Push (bar: Bar, effPair: struct (float * float * int * int)) =
        let v = bar.vwap
        // 1. the strictly-prior channel states
        for k in 0 .. chW.Length - 1 do
            pMax.[k] <- maxs.[k].State; pMin.[k] <- mins.[k].State
        pSessHi <- sessHigh.State; pSessLo <- sessLow.State
        pMeta <- (if isLong then metaOpp.StateMeta else metaOppS.StateMeta)
        let p1200max, p1200min = pMax.[ci 1200], pMin.[ci 1200]
        priorExt <- vv (if isLong then p1200min else p1200max)
        priorOpp <- vv (if isLong then p1200max else p1200min)
        priorExit <- vv (if isLong then pMax.[ci exitBars] else pMin.[ci exitBars])
        // 2. fold the bar
        let wt = if isLong then v < prevV else v > prevV
        let ct = if isLong then v > prevV else v < prevV
        for s in wtS do s.Push (if wt then 1.0 else 0.0)
        for s in ctS do s.Push (if ct then 1.0 else 0.0)
        if ct then
            wtSinceCt <- 0; wtRun <- 0; extSinceCt <- 0
            lastCtSec <- bar.t; lastCtVwap <- v
            runPreExtVwap <- nan; runFirstExtVwap <- nan; runFirstWtVwap <- nan
        elif wt then
            if wtSinceCt = 0 then runFirstWtVwap <- v
            wtSinceCt <- wtSinceCt + 1; wtRun <- wtRun + 1
        else
            wtRun <- 0
        cumVol <- cumVol + bar.volume; cumTc <- cumTc + float bar.tradeCount; cumDv <- cumDv + v * bar.volume
        for k in 0 .. chW.Length - 1 do
            maxs.[k].Push v; mins.[k].Push v
        sessHigh.Push v; sessLow.Push v
        olsSinceOpp.Push (log v); effSinceOpp.Push(v, bar.volume)
        if leg.Armed then
            olsSinceFlow.Push (log v); effSinceFlow.Push(v, bar.volume)
        extLag60.Push priorExt; extLag120.Push priorExt; extLag180.Push priorExt
        if isLong then metaOpp.Push(v, effPair) else metaOppS.Push(v, effPair)
        // 4. breach counters: step, then mark this bar's breaches
        let above (p: float voption) = match p with ValueSome x -> v > x | ValueNone -> false
        let below (p: float voption) = match p with ValueSome x -> v < x | ValueNone -> false
        brHiSess.Step(); brLoSess.Step()
        for k in 0 .. chW.Length - 1 do
            brHi.[k].Step(); brLo.[k].Step()
        if above pSessHi then brHiSess.OnBreach()
        if below pSessLo then brLoSess.OnBreach()
        for k in 0 .. chW.Length - 1 do
            if above pMax.[k] then brHi.[k].OnBreach()
            if below pMin.[k] then brLo.[k].OnBreach()
        for c in counters do c.Step()
        if leg.Armed then
            if wt then wtFlow <- wtFlow + 1
            elif ct then ctFlow <- ctFlow + 1
        isNewExt <- (if isLong then below p1200min else above p1200max)
        isNewOpp <- (if isLong then above p1200max else below p1200min)
        if isNewExt then
            if not leg.Armed then
                legVolAnchor <- cumVol - bar.volume
                legTcAnchor <- cumTc - float bar.tradeCount
                legDvAnchor <- cumDv - v * bar.volume
                wtFlow <- (if wt then 1 else 0); ctFlow <- (if ct then 1 else 0)
                olsSinceFlow.Reset(); olsSinceFlow.Push (log v)
                effSinceFlow.Reset(); effSinceFlow.Push(v, bar.volume)
                dropToFlow <- (if not (Double.IsNaN priorOpp) && priorOpp > 0.0 then v / priorOpp - 1.0 else nan)
                dropSlope <- olsSinceOpp.Slope; dropR <- olsSinceOpp.R; dropEff <- effSinceOpp.Eff
                (match pMeta with
                 | ValueSome (struct (e20, e10, n20, n10)) -> armEff20 <- e20; armEff10 <- e10; armSlots20 <- n20; armSlots10 <- n10
                 | ValueNone -> armEff20 <- nan; armEff10 <- nan; armSlots20 <- 0; armSlots10 <- 0)
                firstExtVwap <- v
            if extSinceCt = 0 then
                runPreExtVwap <- prevV; runFirstExtVwap <- v
            extSinceCt <- extSinceCt + 1
            for c in counters do c.OnEvent bar.t

    /// the values in the order of LegNames, read at the signal bar; counts the signal into the leg's trade index
    member _.SnapshotAndCount (bar: Bar) : float[] =
        let v = bar.vwap
        let chg (a: float) = if Double.IsNaN a || a <= 0.0 then nan else v / a - 1.0
        let opp k = if isLong then vv maxs.[ci k].State else vv mins.[ci k].State
        let bOpp, bExt = (if isLong then brHi, brLo else brLo, brHi)
        let bOppS, bExtS = (if isLong then brHiSess, brLoSess else brLoSess, brHiSess)
        let breaches (b: BreachCounter[]) (s: BreachCounter) =
            [ yield float s.BarsSinceBreach
              for w in [ 1200; 600; 300; 120; 60; 30 ] do yield float b.[ci w].BarsSinceBreach ]
        let out =
            [| for c in counters do yield float c.EventsSinceFirst
               yield! [ float leg.BarsSinceFirst; float (leg.SecsSinceFirst bar.t)
                        float counters.[2].BarsSinceFirst; float counters.[1].BarsSinceFirst; float tradeIdx ]
               yield! breaches bOpp bOppS
               yield! breaches bExt bExtS
               yield! [ opp 30; opp 60; opp 120; opp 180; priorExt; priorOpp; priorExit
                        vv extLag60.Lagged; vv extLag120.Lagged; vv extLag180.Lagged; firstExtVwap
                        olsSinceOpp.Slope; olsSinceOpp.R; float olsSinceOpp.Count; olsSinceFlow.Slope; olsSinceFlow.R
                        effSinceOpp.Eff; effSinceFlow.Eff; effSinceOpp.Eff9Ema; effSinceFlow.Eff9Ema
                        effSinceOpp.Z v; effSinceFlow.Z v; float effSinceOpp.Slots; float effSinceFlow.Slots
                        dropToFlow; dropSlope; dropR; dropEff
                        armEff20; armEff10; float armSlots20; float armSlots10
                        cumVol - legVolAnchor; cumTc - legTcAnchor; cumDv - legDvAnchor
                        float wtSinceCt; float wtRun; (if lastCtSec < 0 then -1.0 else float (bar.t - lastCtSec)) ]
               for s in wtS do yield (match s.State with ValueSome x -> x | ValueNone -> 0.0)
               for s in ctS do yield (match s.State with ValueSome x -> x | ValueNone -> 0.0)
               yield! [ float wtFlow; float ctFlow; float extSinceCt; chg lastCtVwap; chg runPreExtVwap
                        chg runFirstExtVwap; chg runFirstWtVwap ] |]
        tradeIdx <- tradeIdx + 1
        out

    /// step 7: the resets fire LAST (after the entry), then this bar becomes the previous one
    member _.Finish (bar: Bar) =
        if isNewOpp then
            leg.Reset()
            tradeIdx <- 0
            legVolAnchor <- nan; legTcAnchor <- nan; legDvAnchor <- nan
            dropToFlow <- nan; dropSlope <- nan; dropR <- nan
            firstExtVwap <- nan
            wtFlow <- -1; ctFlow <- -1
            olsSinceOpp.Reset(); olsSinceFlow.Reset(); effSinceOpp.Reset(); effSinceFlow.Reset()
        let bOpp = if isLong then brHi else brLo
        for r in 1 .. rungW.Length - 1 do
            if bOpp.[ci rungW.[r]].BarsSinceBreach = 0 then counters.[r].Reset()
        prevV <- bar.vwap

/// every recorded feature column, in the order of a trip's Features array
let AllNames = Array.concat [ Features.Names; BarNames; LegNames ]
