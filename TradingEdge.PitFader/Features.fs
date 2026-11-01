module TradingEdge.PitFader.Features

open System
open TradingEdge.RollingMa
open TradingEdge.PitFader.Types

// ===========================================================================
// STAGE 1 of the SpikeFader / FlushFader feature port (user 2026-11-01: "port all the FlushFader research features ...
// for the short side, we'll want to bring in the features from SpikeFader as well"; in stages, bar clock; volatility
// first — "MR on stocks only became good once we started focusing on highly volatile ones").
// The SIDE-AGNOSTIC block: the volatility family, its slopes, the tightness pair, the efficiency / trend-persistence
// family, the channel ranges, the consolidation ratios, and the user's volume deltas. Every formula, window and warmth
// rule is the research engines' (TradingEdge.SpikeFader/Intraday.fs; the volat_5m/3m, volat_eslope_*, consol_* rungs
// from TradingEdge.FlushFader/Intraday.fs, which SpikeFader lacks); the column names are theirs.
// THE CLOCK: present bars; the slot = 30 present bars (SlotVwapMa); |r| = |ln(V / V_prev)| of consecutive slot vwaps.
// READ TIME: the state at the SIGNAL bar's close, the signal bar INCLUDED (the research engines' convention).
// ⚠ ln needs positive prices: a slot pair with V <= 0 is skipped, as in the research engines (CL printed below 0 in
// 2020-04).
// ===========================================================================

/// the bar-count windows of the volume deltas (user 2026-11-01: "30s, 1m, 2m, 3m, 5m, 10m and 20m")
let DeltaWindows = [| 30; 60; 120; 180; 300; 600; 1200 |]
let ConsolSlots = [| 6; 10; 20; 40 |]
let ConsolNames = [| "3m"; "5m"; "10m"; "20m" |]

let Names : string[] =
    [| yield! [ "volat_20m"; "volat_10m"; "volat_5m"; "volat_3m"; "slot_count"
                "volat_20m_lag1m"; "volat_10m_lag1m"; "volat_20m_sessmax"; "volat_20m_prev" ]
       for w in [ "20m"; "10m"; "5m"; "3m" ] do yield! [ $"volat_slope_{w}"; $"volat_r_{w}" ]
       for w in [ "20m"; "10m"; "5m"; "3m" ] do yield! [ $"volat_eslope_{w}"; $"volat_er_{w}" ]
       yield! [ "std_20m"; "std_20m_lag1m"; "std_10m"; "std_10m_lag1m"
                "eff_20m"; "eff_10m"; "rng_slots_20m"; "rng_slots_10m"; "eff_rng_20m"; "eff_rng_10m"
                "eff_rng_lin_20m"; "eff_rng_lin_10m"; "eff_9ema_20m"; "eff_9ema_10m"; "eff_ewma_20m"; "eff_ewma_10m"
                "n_eff_ret_20m"; "n_eff_ret_10m"; "vr2_ewma"; "vr4_ewma"; "ac1_ewma"; "ac2_ewma"; "ac3_ewma"
                "rng_20m"; "rng_sess"; "rng_600"; "rng_300"; "rng_120"; "rng_60"; "rng_30" ]
       for nm in ConsolNames do yield! [ $"consol_{nm}"; $"consol_{nm}_lag1m" ]
       // bar-clock sums: the research engines call these `vol_N_bar` (their plain `vol_N` is the tradeable-second clock)
       for w in DeltaWindows do yield! [ $"delta_{w}_bar"; $"vol_{w}_bar" ] |]

let inline private vv (v: float voption) = match v with ValueSome x -> x | ValueNone -> nan

/// One instance per (contract, session). Push every present bar in order; Snapshot reads the state after the last push.
[<Sealed>]
type VolBlock() =
    let slotBars = 30
    let slots = SlotVwapMa slotBars
    let mutable prevSlotVwap : float voption = ValueNone
    let mutable slotReturns = 0
    // the volatility EWMAs of |r| (half-lives in slots: 40 ≈ 20m, 20 ≈ 10m, 10 ≈ 5m, 6 ≈ 3m)
    let ew40, ew20, ew10, ew6 = EmaHlMa 40.0, EmaHlMa 20.0, EmaHlMa 10.0, EmaHlMa 6.0
    let volatLag = LagMa<float> 1200                 // volat_20m as of 1200 present bars ago
    let volatLag2s40, volatLag2s20 = LagMa<float> 2, LagMa<float> 2   // ... and 2 slots (≈ 1m) ago
    let volatSessMax = RunMaxMa<float>()
    // the vol-trend: OLS of |r| on its order over the last 40 / 20 / 10 / 6 slot returns (full windows only), and the
    // EWMA twins
    let volatOls20m, volatOls10m, volatOls5m, volatOls3m = FloatOls 40, FloatOls 20, FloatOls 10, FloatOls 6
    let volatRoller =
        WindowRoller<float>(
            [| 40, [| volatOls20m :> IRoll<float> |]
               20, [| volatOls10m :> IRoll<float> |]
               10, [| volatOls5m :> IRoll<float> |]
               6, [| volatOls3m :> IRoll<float> |] |])
    let volatEols20m, volatEols10m, volatEols5m, volatEols3m = EwmaOlsMa 40.0, EwmaOlsMa 20.0, EwmaOlsMa 10.0, EwmaOlsMa 6.0
    // tightness: the EWMA std of ln(slot vwap)
    let stdEw40, stdEw20 = EwmaVarMa 40.0, EwmaVarMa 20.0
    let stdLag40, stdLag20 = LagMa<float> 2, LagMa<float> 2
    // efficiency / trend persistence on the same slot stream
    let slotLag, slotLag20 = LagMa<float> 40, LagMa<float> 20
    let slotAbsSum, slotAbsSum20 = SumMa 40, SumMa 20
    let slotAbsSumLin, slotAbsSumLin20 = SumMa 40, SumMa 20
    let slotEma9 = EmaMa 9
    let slotSgnSum, slotSgnSum20 = SumMa 40, SumMa 20
    let slotMax41, slotMin41, slotMax21, slotMin21 = MaxMa 41, MinMa 41, MaxMa 21, MinMa 21
    let neffRet40 = SlidingAgg<NEffShannon>(NEffShannon.Zero, fun a b -> a.Merge b)
    let neffRet20 = SlidingAgg<NEffShannon>(NEffShannon.Zero, fun a b -> a.Merge b)
    let mutable neffRet40Count = 0
    let mutable neffRet20Count = 0
    let effEwma20m, effEwma10m = EwmaEffMa 40.0, EwmaEffMa 20.0
    let vr2Ewma, vr4Ewma = EwmaVarRatioMa(40.0, 2), EwmaVarRatioMa(40.0, 4)
    let acEwma = EwmaAutoCorrMa(40.0, 3)
    // channel ranges ln(high / low) over present-bar windows, inclusive of the current bar (partial windows count)
    let rngW = [| 1200; 600; 300; 120; 60; 30 |]
    let maxs, mins = rngW |> Array.map MaxMa, rngW |> Array.map MinMa
    let sessHigh, sessLow = RunMaxMa<float>(), RunMinMa<float>()
    let consol = SlotVarRatioMa(slotBars, ConsolSlots.[ConsolSlots.Length - 1] + 2)
    // the volume deltas: bar-count sums of the signed volume and of the volume
    let dSums, vSums = DeltaWindows |> Array.map SumMa, DeltaWindows |> Array.map SumMa
    let olsRead (o: FloatOls) =
        if o.Count = o.WindowSize then
            match o.Slope, o.R2 with
            | ValueSome sl, ValueSome r2 -> struct (sl, (if sl < 0.0 then -1.0 else 1.0) * sqrt r2)
            | ValueSome sl, ValueNone -> struct (sl, nan)
            | _ -> struct (nan, nan)
        else struct (nan, nan)
    let eolsRead (o: EwmaOlsMa) = match o.State with ValueSome (struct (sl, r)) -> struct (sl, r) | ValueNone -> struct (nan, nan)
    let chanRng (hi: MaxMa) (lo: MinMa) =
        match hi.State, lo.State with
        | ValueSome h, ValueSome l when l > 0.0 -> log (h / l)
        | _ -> nan

    member _.Push (bar: Bar) =
        for k in 0 .. rngW.Length - 1 do
            maxs.[k].Push bar.vwap; mins.[k].Push bar.vwap
        sessHigh.Push bar.vwap; sessLow.Push bar.vwap
        for k in 0 .. DeltaWindows.Length - 1 do
            dSums.[k].Push bar.delta; vSums.[k].Push bar.volume
        volatLag.Push (vv ew40.State)
        consol.Push (log bar.vwap)
        match slots.Push(bar.vwap, bar.volume) with
        | ValueSome v ->
            (match prevSlotVwap with
             | ValueSome pv when pv > 0.0 && v > 0.0 ->
                 let r = log (v / pv)
                 let ar = abs r
                 ew40.Push ar; ew20.Push ar; ew10.Push ar; ew6.Push ar
                 slotAbsSum.Push ar; slotAbsSum20.Push ar
                 slotAbsSumLin.Push (abs (v - pv)); slotAbsSumLin20.Push (abs (v - pv))
                 // the trend-signed twin: slotEma9 has absorbed pv but not v, so the sign is fixed before r
                 let s = match slotEma9.State with ValueSome e when pv > e -> 1.0 | _ -> -1.0
                 slotSgnSum.Push (s * r); slotSgnSum20.Push (s * r)
                 neffRet40.Push (NEffShannon.Zero.Add ar)
                 if neffRet40Count = 40 then neffRet40.Pop() else neffRet40Count <- neffRet40Count + 1
                 neffRet20.Push (NEffShannon.Zero.Add ar)
                 if neffRet20Count = 20 then neffRet20.Pop() else neffRet20Count <- neffRet20Count + 1
                 volatRoller.Push ar
                 volatEols20m.Push ar; volatEols10m.Push ar; volatEols5m.Push ar; volatEols3m.Push ar
                 effEwma20m.Push r; effEwma10m.Push r
                 vr2Ewma.Push r; vr4Ewma.Push r; acEwma.Push r
                 slotReturns <- slotReturns + 1
             | _ -> ())
            if v > 0.0 then
                stdEw40.Push (log v); stdEw20.Push (log v)
            stdLag40.Push (vv stdEw40.Std); stdLag20.Push (vv stdEw20.Std)
            volatLag2s40.Push (vv ew40.State); volatLag2s20.Push (vv ew20.State)
            (match ew40.State with ValueSome volNow -> volatSessMax.Push volNow | ValueNone -> ())
            slotLag.Push v; slotLag20.Push v
            slotEma9.Push v
            slotMax41.Push v; slotMin41.Push v; slotMax21.Push v; slotMin21.Push v
            prevSlotVwap <- ValueSome v
        | ValueNone -> ()

    /// The (eff_20m, eff_10m, span20, span10) pair AS IT IS on a short window (`Oldest` spans whatever has accumulated;
    /// the span is recorded alongside) — the payload the leg block stamps on every bar for the arming metadata
    /// (FlushFader / SpikeFader S43ai/aj `effPairNow`). Read after this bar's Push.
    member _.EffPairPartial () : struct (float * float * int * int) =
        let partialEff (lagM: LagMa<float>) (absSum: SumMa) =
            match lagM.Last, lagM.Oldest, absSum.State with
            | ValueSome cur, ValueSome old, ValueSome sm when lagM.Count >= 2 && old > 0.0 && cur > 0.0 && sm > 0.0 ->
                struct (log (cur / old) / sm, lagM.Count - 1)
            | _ -> struct (nan, 0)
        let struct (e20, n20) = partialEff slotLag slotAbsSum
        let struct (e10, n10) = partialEff slotLag20 slotAbsSum20
        struct (e20, e10, n20, n10)

    /// the values in the order of Names
    member _.Snapshot () : float[] =
        let eff (lag: LagMa<float>) (s: SumMa) =
            match lag.Last, lag.Lagged, s.State with
            | ValueSome cur, ValueSome old, ValueSome d when s.Count = s.WindowSize && old > 0.0 && d > 0.0 -> log (cur / old) / d
            | _ -> nan
        let rngSlots (h: MaxMa) (l: MinMa) =
            match h.State, l.State with
            | ValueSome h', ValueSome l' when h.Count = h.WindowSize && l' > 0.0 -> log (h' / l')
            | _ -> nan
        let effRng (h: MaxMa) (l: MinMa) (s: SumMa) =
            match h.State, l.State, s.State with
            | ValueSome h', ValueSome l', ValueSome d when h.Count = h.WindowSize && s.Count = s.WindowSize && l' > 0.0 && d > 0.0 ->
                log (h' / l') / d
            | _ -> nan
        let effRngLin (h: MaxMa) (l: MinMa) (s: SumMa) =
            match h.State, l.State, s.State with
            | ValueSome h', ValueSome l', ValueSome d when h.Count = h.WindowSize && s.Count = s.WindowSize && d > 0.0 -> (h' - l') / d
            | _ -> nan
        let eff9 (n: SumMa) (d: SumMa) =
            match n.State, d.State with
            | ValueSome n', ValueSome d' when d.Count = d.WindowSize && d' > 0.0 -> n' / d'
            | _ -> nan
        let full (s: SumMa) = if s.Count = s.WindowSize then vv s.State else nan
        [| yield! [ vv ew40.State; vv ew20.State; vv ew10.State; vv ew6.State; float slotReturns
                    vv volatLag2s40.Lagged; vv volatLag2s20.Lagged; vv volatSessMax.State; vv volatLag.Lagged ]
           for o in [ volatOls20m; volatOls10m; volatOls5m; volatOls3m ] do
               let struct (s, r) = olsRead o in yield! [ s; r ]
           for o in [ volatEols20m; volatEols10m; volatEols5m; volatEols3m ] do
               let struct (s, r) = eolsRead o in yield! [ s; r ]
           yield! [ vv stdEw40.Std; vv stdLag40.Lagged; vv stdEw20.Std; vv stdLag20.Lagged
                    eff slotLag slotAbsSum; eff slotLag20 slotAbsSum20
                    rngSlots slotMax41 slotMin41; rngSlots slotMax21 slotMin21
                    effRng slotMax41 slotMin41 slotAbsSum; effRng slotMax21 slotMin21 slotAbsSum20
                    effRngLin slotMax41 slotMin41 slotAbsSumLin; effRngLin slotMax21 slotMin21 slotAbsSumLin20
                    eff9 slotSgnSum slotAbsSum; eff9 slotSgnSum20 slotAbsSum20
                    effEwma20m.Value; effEwma10m.Value
                    (if neffRet40Count = 40 then neffRet40.Query.Value else nan)
                    (if neffRet20Count = 20 then neffRet20.Query.Value else nan)
                    vr2Ewma.Value; vr4Ewma.Value; acEwma.Rho 1; acEwma.Rho 2; acEwma.Rho 3
                    chanRng maxs.[0] mins.[0]
                    (match sessHigh.State, sessLow.State with ValueSome h, ValueSome l when l > 0.0 -> log (h / l) | _ -> nan)
                    chanRng maxs.[1] mins.[1]; chanRng maxs.[2] mins.[2]; chanRng maxs.[3] mins.[3]
                    chanRng maxs.[4] mins.[4]; chanRng maxs.[5] mins.[5] ]
           for k in ConsolSlots do yield! [ vv (consol.Ratio(k, 0)); vv (consol.Ratio(k, 2)) ]
           for k in 0 .. DeltaWindows.Length - 1 do yield! [ full dSums.[k]; full vSums.[k] ] |]


// ===========================================================================
// STAGE 2a — the SIDE-AGNOSTIC bar block (user 2026-11-01: "Yes, let's do it" — the stage-2 port, by hand, side-neutral;
// "we should make a clean break from the stock trading systems"). From FlushFader / SpikeFader (identical in both):
// rolling OLS of ln(vwap) at 60..1200 present bars; the rolling vwaps and the previous-window vwaps (the speed
// denominators: (dv_2n − dv_n) / (vol_2n − vol_n)); bar-count volume / trade-count / dollar-volume sums; the price moments
// behind z_20m; the session block (vwap, open, extremes, cums); the wall-clock gap counts; N_eff of the volume.
// Left out by design: the tradeable-second twins and the halt detector (no LULD in futures; bar clock — user), the
// 09:45 tape, the EWP speed cells (a later stage).
// ===========================================================================

let BarNames : string[] =
    [| for w in [ 60; 120; 180; 300; 600; 1200 ] do yield! [ $"ols_slope_{w}"; $"ols_r_{w}" ]
       yield! [ "vwap_1200"; "vwap_60"; "vwap_30"; "vwap_120"; "vwap_180"
                "vwap_60_prev"; "vwap_30_prev"; "vwap_10_prev"; "vwap_5_prev"
                "vol_5_bar"; "vol_10_bar"; "vol_15_bar" ]
       for w in [ 5; 10; 15; 30; 60; 300; 600; 1200 ] do yield $"tc_{w}_bar"
       for w in [ 60; 300; 600; 1200 ] do yield $"dollar_vol_{w}_bar"
       yield! [ "vol_60_prev_bar"; "tc_60_prev_bar"; "cum_vol"; "cum_tc"; "cum_dv"
                "sess_dv2"; "sess_dlv"; "sess_dlv2" ]
       for w in [ 300; 600; 1200 ] do yield! [ $"dv2_{w}"; $"dlv_{w}"; $"dlv2_{w}" ]
       yield! [ "sess_vwap"; "dist_sess_vwap"; "pct_chg_open"; "bar_vol"; "bar_tc"; "bars_above_svwap"; "bars_present"
                "sess_low"; "sess_high" ]
       for w in [ 15; 30; 60; 120; 300; 600; 1200 ] do yield $"gap_{w}"
       yield! [ "max_gap_run_1200"; "max_gap_run_300"; "big_gap_runs_1200" ]
       for w in [ 60; 300; 600; 1200 ] do yield! [ $"n_eff_shannon_{w}"; $"n_eff_hhi_{w}" ] |]

[<Sealed>]
type BarBlock(sessionStartSec: int) =
    let olsW = [| 60; 120; 180; 300; 600; 1200 |]
    let ols = olsW |> Array.map OlsSlopeMa
    // the bar-count sums (the window difference of the 2n and n sums gives the previous window)
    let sumW = [| 5; 10; 15; 20; 30; 60; 120; 180; 300; 600; 1200 |]
    let idx w = Array.IndexOf(sumW, w)
    let volS, dvS, tcS = sumW |> Array.map SumMa, sumW |> Array.map SumMa, sumW |> Array.map SumMa
    let momW = [| 300; 600; 1200 |]
    let dv2S, dlvS, dlv2S = momW |> Array.map SumMa, momW |> Array.map SumMa, momW |> Array.map SumMa
    let sessVwap = RatioMa()
    let mutable openVwap = nan
    let mutable cumVol, cumTc, cumDv, cumDv2, cumDlv, cumDlv2 = 0.0, 0.0, 0.0, 0.0, 0.0, 0.0
    let mutable barsAboveSvwap, barsPresent = 0, 0
    let sessHigh, sessLow = RunMaxMa<float>(), RunMinMa<float>()
    let gapW = [| 15; 30; 60; 120; 300; 600; 1200 |]
    let gaps = gapW |> Array.map (fun w -> GapCounter(w, sessionStartSec))
    let gapRunMax1200, gapRunMax300, bigGapRuns1200 = MaxMa 1200, MaxMa 300, SumMa 1200
    let mutable prevT = -1
    let neffCombine (struct (h1: NEffHhi, s1: NEffShannon)) (struct (h2: NEffHhi, s2: NEffShannon)) = struct (h1.Merge h2, s1.Merge s2)
    let neffZero = struct (NEffHhi.Zero, NEffShannon.Zero)
    let neffW = [| 60; 300; 600; 1200 |]
    let neff = neffW |> Array.map (fun _ -> SlidingAgg<struct (NEffHhi * NEffShannon)>(neffZero, neffCombine))
    let neffN = Array.zeroCreate<int> neffW.Length
    member _.Push (bar: Bar) =
        if Double.IsNaN openVwap then openVwap <- bar.vwap
        let lnp = log bar.vwap
        cumVol <- cumVol + bar.volume; cumTc <- cumTc + float bar.tradeCount; cumDv <- cumDv + bar.vwap * bar.volume
        cumDv2 <- cumDv2 + bar.vwap * bar.vwap * bar.volume
        cumDlv <- cumDlv + lnp * bar.volume; cumDlv2 <- cumDlv2 + lnp * lnp * bar.volume
        barsPresent <- barsPresent + 1
        if cumVol > 0.0 && bar.vwap > cumDv / cumVol then barsAboveSvwap <- barsAboveSvwap + 1
        for g in gaps do g.Push bar.t
        let gapRun = float (max 0 (bar.t - (if prevT < sessionStartSec then sessionStartSec - 1 else prevT) - 1))
        gapRunMax1200.Push gapRun; gapRunMax300.Push gapRun
        bigGapRuns1200.Push (if gapRun >= 60.0 then 1.0 else 0.0)
        for k in 0 .. sumW.Length - 1 do
            volS.[k].Push bar.volume; dvS.[k].Push (bar.vwap * bar.volume); tcS.[k].Push (float bar.tradeCount)
        for k in 0 .. momW.Length - 1 do
            dv2S.[k].Push (bar.vwap * bar.vwap * bar.volume); dlvS.[k].Push (lnp * bar.volume); dlv2S.[k].Push (lnp * lnp * bar.volume)
        sessVwap.Push(bar.vwap * bar.volume, bar.volume)
        sessHigh.Push bar.vwap; sessLow.Push bar.vwap
        for o in ols do o.Push lnp
        for k in 0 .. neffW.Length - 1 do
            neff.[k].Push (struct (NEffHhi.Zero.Add bar.volume, NEffShannon.Zero.Add bar.volume))
            if neffN.[k] = neffW.[k] then neff.[k].Pop() else neffN.[k] <- neffN.[k] + 1
        prevT <- bar.t

    /// the values in the order of BarNames, at the signal bar (inclusive)
    member _.Snapshot (bar: Bar) : float[] =
        let prevWinVwap n =
            let d2, d1, v2, v1 = dvS.[idx (2 * n)], dvS.[idx n], volS.[idx (2 * n)], volS.[idx n]
            if v2.Count = v2.WindowSize then
                match d2.State, d1.State, v2.State, v1.State with
                | ValueSome a, ValueSome b, ValueSome c, ValueSome d when c - d > 0.0 -> (a - b) / (c - d)
                | _ -> nan
            else nan
        let sumRatio n = match dvS.[idx n].State, volS.[idx n].State with ValueSome x, ValueSome y when y > 0.0 -> x / y | _ -> nan
        let fullRatio n =
            let v = volS.[idx n]
            if v.Count = v.WindowSize then sumRatio n else nan
        let prevSum (s: SumMa[]) =
            let a, b = s.[idx 120], s.[idx 60]
            if a.Count = a.WindowSize then match a.State, b.State with ValueSome x, ValueSome y -> x - y | _ -> nan else nan
        [| for o in ols do
               if o.Count = o.WindowSize then
                   match o.State, o.R2 with
                   | ValueSome m, ValueSome r2 -> yield! [ m; (if m < 0.0 then -sqrt r2 else sqrt r2) ]
                   | ValueSome m, ValueNone -> yield! [ m; nan ]
                   | _ -> yield! [ nan; nan ]
               else yield! [ nan; nan ]
           yield! [ fullRatio 1200; fullRatio 60; sumRatio 30; sumRatio 120; sumRatio 180
                    prevWinVwap 60; prevWinVwap 30; prevWinVwap 10; prevWinVwap 5
                    vv volS.[idx 5].State; vv volS.[idx 10].State; vv volS.[idx 15].State ]
           for w in [ 5; 10; 15; 30; 60; 300; 600; 1200 ] do yield vv tcS.[idx w].State
           for w in [ 60; 300; 600; 1200 ] do yield vv dvS.[idx w].State
           yield! [ prevSum volS; prevSum tcS; cumVol; cumTc; cumDv; cumDv2; cumDlv; cumDlv2 ]
           for k in 0 .. momW.Length - 1 do yield! [ vv dv2S.[k].State; vv dlvS.[k].State; vv dlv2S.[k].State ]
           yield! [ vv sessVwap.State
                    (match sessVwap.State with ValueSome sv when sv > 0.0 -> log (bar.vwap / sv) | _ -> nan)
                    (if openVwap > 0.0 then bar.vwap / openVwap - 1.0 else nan)
                    bar.volume; float bar.tradeCount; float barsAboveSvwap; float barsPresent
                    vv sessLow.State; vv sessHigh.State ]
           for g in gaps do yield float g.Gaps
           yield! [ vv gapRunMax1200.State; vv gapRunMax300.State; vv bigGapRuns1200.State ]
           for k in 0 .. neffW.Length - 1 do
               if neffN.[k] = neffW.[k] then
                   let struct (h, s) = neff.[k].Query in yield! [ s.Value; h.Value ]
               else yield! [ nan; nan ] |]
