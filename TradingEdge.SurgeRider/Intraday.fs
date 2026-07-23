module TradingEdge.SurgeRider.Intraday

open System
open TradingEdge.RollingMa

// ===========================================================================
// SurgeRider — 1-SECOND-bar intraday MOMENTUM (long-only), the volume/trade-
// count-acceleration breakout sampler. Template: DipRiderV6 (the MR sampler);
// design + all measurement pedigree in docs/surgerider_results.md F1-F8b and
// the approved plan.
//
// THE SYSTEM (sampler form — record, don't gate):
//   ENTRY SIGNAL: bar vwap > the STRICTLY-PRIOR N-present-bar MAX of vwaps
//                 (N = EntryChannelBars; "the bar just made a new channel high"
//                 = bars-since-breach 0, the plan's C=1), inside the entry
//                 window, with the 60-bar dollar-volume AND trade-count floors
//                 met (a real breakout needs both volume and activity — high
//                 volume + low trade count = one block print, not a breakout).
//   FILL: the NEXT present bar's vwap (the 1s dataset has no close column, and
//         the next-bar vwap is the honest "you traded into the following
//         second" fill). Exits fill the same way.
//   EXIT SIGNAL (any, checked at bar close): 60-bar log-volume z < EZV (the
//         acceleration died), 60-bar log-trade-count z < EZT, vwap under the
//         strictly-prior exit-channel LOW (opposite side hit), or MOC.
//
// ⭐ SAMPLER, NOT A BOOK (mc = 0 default): every qualifying bar becomes an
// independent trip with its full feature vector + forward marks; PF on the raw
// output is ATTRIBUTION, not portfolio. Re-run chosen cells at mc=1.
//
// ⭐ PRESENT-BAR SEMANTICS (D1): the engine steps ONLY on bars that exist in
// data/intraday_1s_slim/ (seconds with >= 1 kept trade). Every window is a
// PRESENT-BAR-COUNT window — "60 bars" spans 60+ wall-clock seconds on any
// name with gaps. The GapCounter features (gap60/30/15 = missing seconds in
// the trailing wall-clock window) measure exactly what this convention hides;
// gap60 = 0 certifies present-bar ~= wall-clock locally.
//
// ⭐ THE VOL BLOCK is the F1-F8 bake-off lock, nothing else:
//   30-present-bar slot vwaps (SlotVwapMa) -> r = ln(V/V_prev) -> EmaHlMa of
//   |r| at hl = 40 slots (vol20m, THE driver) + hl = 20 slots (vol10m, the
//   trajectory twin); rng20m = ln(high/low) of the 1200-bar vwap channel
//   (the F8 complement, partial +0.145 | ew20); eff20m = |ln(V/V_40ago)| /
//   Sum40|r| (the trendiness axis, rho vs ew20 = 0.024; drift-t-stat
//   transform; the exactly-consistent form — both sides span the SAME 40
//   returns, so eff <= 1 unconditionally).
//
// Live-safe: every feature folds from D's own realized bars; entry window
// starts 09:45 (etSec 35100), the knowability floor for the 09:45 universe
// context fields (docs/lookahead_protocol.md).
// ===========================================================================

/// One 1-second present bar from data/intraday_1s_slim/, split-adjusted to the
/// candidate's daily scale. `etSec` = the `bucket` column: seconds since 00:00
/// ET (RTH open = 34200, 09:45 = 35100, 16:00 = 57600). volume is FLOAT — the
/// tape carries genuine fractional shares.
type SecBar =
    { etSec: int
      vwap: float
      volume: float
      tradeCount: int }

/// Missing-second counter over a trailing WALL-CLOCK window (user, 2026-07-23):
/// how many of the last `windowSecs` seconds (inclusive of the current bar's)
/// had NO present bar. The engine's windows are present-bar-count (D1); this is
/// the feature that records what that convention skips. `Push` the bar's etSec,
/// then read `.Gaps`. Session-start clamp: before the window fills with session
/// seconds, the denominator is the elapsed session span, so the first RTH bars
/// don't read as one giant gap.
[<Sealed>]
type GapCounter(windowSecs: int, sessionStartSec: int) =
    let q = System.Collections.Generic.Queue<int>()
    let mutable lastSec = -1
    member _.Push (sec: int) =
        q.Enqueue sec
        while q.Peek() <= sec - windowSecs do
            q.Dequeue() |> ignore
        lastSec <- sec
    /// Missing seconds in the trailing window as of the last Push.
    member _.Gaps =
        if lastSec < 0 then 0
        else
            let span = min windowSecs (lastSec - sessionStartSec + 1)
            max 0 (span - q.Count)
    member _.Reset () =
        q.Clear()
        lastSec <- -1

/// Bars-since-last-channel-breach, one per channel (the plan's generalized
/// NewLowCounters). -1 = this channel's high has not been breached this
/// session; 0 = the CURRENT bar breached it; N = N present bars ago. Step
/// FIRST each bar, then OnBreach if the bar broke the channel high, so the
/// breach bar itself reads 0.
[<Sealed>]
type BreachCounter() =
    let mutable bars = -1
    member _.BarsSinceBreach = bars
    member _.Step () = if bars >= 0 then bars <- bars + 1
    member _.OnBreach () = bars <- 0
    member _.Reset () = bars <- -1

/// Trip life-cycle. Exit SIGNALS are detected at a bar's close but FILL at the
/// next present bar's vwap — PendingExit carries the reason across that bar.
type IntraPosState =
    | Holding
    | PendingExit of reason: string
    | ExitedAt of exitSec: int * exitPx: float * reason: string   // "zvol" | "ztc" | "channel" | "moc"

/// One sampler trip. Features are the state at the SIGNAL bar's close
/// (inclusive of the signal bar — it has closed; not lookahead). The fill is
/// the NEXT present bar's vwap. ⭐ NOTHING here gates (beyond the hard entry
/// gates); it is all recorded for post-hoc SQL.
type SurgePosition =
    { SignalSec: int             // the gate bar (features captured here)
      SignalVwap: float          // its vwap — entry slippage = EntryPx/SignalVwap
      EntrySec: int              // the fill bar
      EntryPx: float             // the fill: next present bar's vwap
      // ----- acceleration z-scores (D6: k-bar log sums vs the 1200-bar baseline) -----
      ZVol1: float               // ln(bar volume) vs its own 1200-bar mean/sigma
      ZVol15: float              // ln(15-bar volume sum) vs its 1200-bar baseline
      ZVol30: float
      ZVol60: float
      ZTc1: float
      ZTc15: float
      ZTc30: float
      ZTc60: float
      // ----- the locked volatility block (F1-F8) -----
      Vol20m: float              // EmaHlMa hl=40 slots of |slot return| — THE driver
      Vol10m: float              // hl=20 twin (vol trajectory: Vol10m << Vol20m = vol collapsing)
      Rng20m: float              // ln(high/low) of the 1200-bar vwap channel (F8 complement)
      Eff20m: float              // |ln(V/V_40slots_ago)| / Sum40|r| — trendiness, <= 1
      SlotCount: int             // slot returns folded so far (vol-feature warmth)
      // ----- channel widths, ln(high/low) per present-bar window -----
      RngSess: float
      Rng300: float
      Rng120: float
      Rng60: float
      Rng30: float
      // ----- bars since each channel's high was last breached (-1 = never) -----
      BreachSess: int
      Breach1200: int
      Breach300: int
      Breach120: int
      Breach60: int
      Breach30: int
      // ----- ⭐ the up-leg reset counters (user, 2026-07-23; the momentum mirror of
      // DipRiderV6's NewLowCounters). A NEW 20m LOW (vwap < the strictly-prior
      // 1200-bar min) ends the up-leg and resets both. Together they separate
      // HOW DEEP INTO THE ENTRY SEQUENCE a trade is from HOW OLD the leg is:
      //   TradeIdx = 0 -> the FIRST trade since the leg began (the early breakout);
      //   TradeIdx = N -> the (N+1)th chase of the same leg.
      //   BarsSinceLow1200 -> the leg's age in present bars (-1 = no 20m low yet). -----
      TradeIdx: int
      BarsSinceLow1200: int
      // ----- the gap counts (what present-bar windows hide) -----
      Gap60: int
      Gap30: int
      Gap15: int
      // ----- location -----
      SessVwap: float
      DistSessVwap: float        // ln(vwap / session vwap)
      PctChgOpen: float          // vwap / first-RTH-bar vwap - 1
      // ----- raw activity levels (log twins = ln() in SQL) -----
      BarVol: float
      BarTc: int
      Vol15: float
      Vol30: float
      Vol60: float
      Tc15: float
      Tc30: float
      Tc60: float
      DollarVol60: float         // Sum60 of vwap*volume — the liquidity-floor value
      CumVol: float
      CumTc: float
      // ----- forward marks (vwap at the first present bar >= entry + horizon; nan if the day ends first) -----
      FwdVwap60: float
      FwdVwap300: float
      FwdVwap1200: float
      // ----- exit -----
      BarsHeld: int              // present bars from the fill bar to the exit-fill bar
      State: IntraPosState }

/// SurgeRider config. Hard gates only — every other lever is a recorded column.
type IntradayConfig =
    { EntryChannelBars: int      // ⭐ ENTRY: vwap > the prior N-present-bar MAX of vwaps. Default 300
                                 // (~5m on a fully-active name). The channel must be WARM (N bars folded)
                                 // — a partial-window "high" is not a breakout. Breach counters for all
                                 // six windows are recorded, so post-hoc can TIGHTEN to 1200/session
                                 // (a longer-window breach implies every shorter one) but not loosen.
      ExitChannelBars: int       // opposite side: vwap < the prior N-bar MIN -> exit signal. Default 300.
      ExitZBars: int             // which k feeds the exit z's (60 = the 1m aggregate). {1,15,30,60}.
      Ezv: float                 // exit when z(ln vol sum, k=ExitZBars) < this. Default 0.0.
      Ezt: float                 // exit when z(ln tc  sum, k=ExitZBars) < this. Default 0.0.
      DvFloor60: float           // hard gate: Sum60(vwap*volume) >= this at the signal bar. $ terms.
      TcFloor60: float           // hard gate: Sum60(tradeCount) >= this.
      MaxConcurrent: int         // 0 = unlimited (THE SAMPLER DEFAULT). 1 = a real book.
      SlotBars: int              // the slot clock: 30 present bars (F5c: 30-40s flat, 30 stands).
      BaselineBars: int          // the z baseline window: 1200 present bars (~20m active).
      SessionStartSec: int       // 34200 = 09:30 — features fold from RTH open.
      EntryStartSec: int         // 35100 = 09:45. ⚠ KNOWABILITY FLOOR for the 09:45 universe context
                                 // (dv_0945 / rvol_0945_honest ride along in the CSV) — lowering this
                                 // below 35100 silently makes those columns lookahead (R4).
      EntryEndSec: int           // 48600 = 13:30.
      MocSec: int }              // 57600 = 16:00. Positions force-exit at the first bar >= this (its
                                 // own vwap — the auction-proximate print), and Flatten catches days
                                 // whose tape ends earlier (early closes).

/// The SurgeRider engine. One instance per (ticker, day).
type IntradaySystem(cfg: IntradayConfig, ticker: string, day: DateOnly) =
    // ----- the entry/exit channels + the recorded channel set -----
    // MaxMa/MinMa pairs over vwap at the five present-bar windows; session
    // extremes via RunMaxMa/RunMinMa. The entry/exit windows must be one of
    // {30,60,120,300,1200} (validated in Program) so they alias these.
    let max30 = MaxMa 30
    let max60 = MaxMa 60
    let max120 = MaxMa 120
    let max300 = MaxMa 300
    let max1200 = MaxMa 1200
    let min30 = MinMa 30
    let min60 = MinMa 60
    let min120 = MinMa 120
    let min300 = MinMa 300
    let min1200 = MinMa 1200
    let sessHigh = RunMaxMa<float>()
    let sessLow = RunMinMa<float>()
    let chanMax n : MaxMa =
        match n with
        | 30 -> max30 | 60 -> max60 | 120 -> max120 | 300 -> max300 | 1200 -> max1200
        | _ -> invalidArg "n" $"no {n}-bar channel"
    let chanMin n : MinMa =
        match n with
        | 30 -> min30 | 60 -> min60 | 120 -> min120 | 300 -> min300 | 1200 -> min1200
        | _ -> invalidArg "n" $"no {n}-bar channel"
    let entryMax = chanMax cfg.EntryChannelBars
    let exitMin = chanMin cfg.ExitChannelBars
    // ----- breach counters (bars since each channel high was last broken) -----
    let brSess = BreachCounter()
    let br30 = BreachCounter()
    let br60 = BreachCounter()
    let br120 = BreachCounter()
    let br300 = BreachCounter()
    let br1200 = BreachCounter()
    // ⭐ the up-leg reset pair: a LOW-side breach counter on the 1200-bar channel
    // (leg age) + the per-leg trade counter (entry-sequence depth). See the
    // SurgePosition comment; reset together on every new 20m low.
    let legLow = BreachCounter()
    let mutable tradeIdx = 0
    // ----- activity sums + the 1200-bar z baselines (D6: one WinStdMa per k) -----
    let volSum15 = SumMa 15
    let volSum30 = SumMa 30
    let volSum60 = SumMa 60
    let tcSum15 = SumMa 15
    let tcSum30 = SumMa 30
    let tcSum60 = SumMa 60
    let dvSum60 = SumMa 60                       // Σ vwap·volume — the liquidity floor
    let zVol1 = WinStdMa cfg.BaselineBars        // fed ln(bar volume)
    let zVol15 = WinStdMa cfg.BaselineBars       // fed ln(15-bar volume sum) — only once the sum is warm
    let zVol30 = WinStdMa cfg.BaselineBars
    let zVol60 = WinStdMa cfg.BaselineBars
    let zTc1 = WinStdMa cfg.BaselineBars
    let zTc15 = WinStdMa cfg.BaselineBars
    let zTc30 = WinStdMa cfg.BaselineBars
    let zTc60 = WinStdMa cfg.BaselineBars
    // ----- the locked vol block -----
    let slots = SlotVwapMa cfg.SlotBars
    let ew40 = EmaHlMa 40.0                      // vol20m — THE driver (F7 lock)
    let ew20 = EmaHlMa 20.0                      // vol10m — the trajectory twin
    let slotLag = LagMa<float> 40                // slot vwap 40 emissions ago (eff numerator)
    let slotAbsSum = SumMa 40                    // Σ|r| over the same 40 returns (eff denominator)
    let mutable prevSlotVwap : float voption = ValueNone
    let mutable slotReturns = 0
    // ----- gaps / location / session -----
    let gap60 = GapCounter(60, cfg.SessionStartSec)
    let gap30 = GapCounter(30, cfg.SessionStartSec)
    let gap15 = GapCounter(15, cfg.SessionStartSec)
    let sessVwap = RatioMa()
    let mutable openVwap : float voption = ValueNone
    let mutable cumVol = 0.0
    let mutable cumTc = 0.0

    // ⭐ ACTIVE/RETIRED SPLIT (user, 2026-07-23). At mc=0 a busy day opens hundreds
    // of trips per ticker; looping ALL of them every bar made runtime scale
    // super-linearly with trip count (2.2x from 300- to 60-bar channels). A trip
    // is INERT once it has exited AND its last forward mark (+1200s) has filled —
    // nothing in the per-bar loop can touch it again — so it retires to `retired`
    // and the hot loop only walks `active`. `.Positions` = retired @ active
    // (⚠ NOT chronological order — sort by signal_sec in SQL if order matters).
    let active = ResizeArray<SurgePosition>()
    let retired = ResizeArray<SurgePosition>()
    let mutable pendingEntry : SurgePosition voption = ValueNone
    // STRICTLY-PRIOR snapshots, captured BEFORE this bar's vwap folds in. ⚠ If
    // the current vwap were inside its own window, "vwap > channel max" would be
    // trivially false on every bar (a value can't exceed a max that contains it).
    let mutable sMax30 : float voption = ValueNone
    let mutable sMax60 : float voption = ValueNone
    let mutable sMax120 : float voption = ValueNone
    let mutable sMax300 : float voption = ValueNone
    let mutable sMax1200 : float voption = ValueNone
    let mutable sExitMin : float voption = ValueNone
    let mutable sMin1200 : float voption = ValueNone
    let mutable sSessHigh : float voption = ValueNone

    let vv (v: float voption) = match v with ValueSome x -> x | ValueNone -> nan
    /// ln(high/low) of a channel pair, nan until both sides carry a value.
    let chanRng (hi: MaxMa) (lo: MinMa) =
        match hi.State, lo.State with
        | ValueSome h, ValueSome l when l > 0.0 -> log (h / l)
        | _ -> nan
    /// z of ln(sum_k) against its 1200-bar baseline — nan until the sum is warm
    /// (partial early-session sums would poison the baseline) and the baseline
    /// has >= 2 values. Inclusive of the current bar (pushed before reading),
    /// same convention as DipRiderV6's volZ.
    let zOf (baseline: WinStdMa) (sum: SumMa) =
        if sum.Count < sum.WindowSize then ValueNone
        else match sum.State with
             | ValueSome s when s > 0.0 -> baseline.Z (log s)
             | _ -> ValueNone

    member _.Ticker = ticker
    member _.Day = day
    member _.Positions = Seq.append retired active
    member _.OpenCount =
        let mutable k = 0
        for p in active do
            (match p.State with Holding | PendingExit _ -> k <- k + 1 | ExitedAt _ -> ())
        k
    member this.HasSlot =
        cfg.MaxConcurrent <= 0
        || this.OpenCount + (if pendingEntry.IsSome then 1 else 0) < cfg.MaxConcurrent

    /// Advance the whole system by one PRESENT 1s bar. Bars arrive in etSec
    /// order, RTH only (the emitter filters to [SessionStartSec, MocSec]).
    member this.Process (bar: SecBar) =
        if bar.etSec < cfg.SessionStartSec then () else

        // ===== 1. capture the STRICTLY-PRIOR channel states =====
        sMax30 <- max30.State
        sMax60 <- max60.State
        sMax120 <- max120.State
        sMax300 <- max300.State
        sMax1200 <- max1200.State
        sExitMin <- exitMin.State
        sMin1200 <- min1200.State
        sSessHigh <- sessHigh.State
        let priorEntryMax =
            match cfg.EntryChannelBars with
            | 30 -> sMax30 | 60 -> sMax60 | 120 -> sMax120 | 300 -> sMax300 | 1200 -> sMax1200
            | _ -> ValueNone

        // ===== 2. fold this bar into every structure =====
        if openVwap.IsNone then openVwap <- ValueSome bar.vwap
        cumVol <- cumVol + bar.volume
        cumTc <- cumTc + float bar.tradeCount
        gap60.Push bar.etSec
        gap30.Push bar.etSec
        gap15.Push bar.etSec
        volSum15.Push bar.volume
        volSum30.Push bar.volume
        volSum60.Push bar.volume
        tcSum15.Push (float bar.tradeCount)
        tcSum30.Push (float bar.tradeCount)
        tcSum60.Push (float bar.tradeCount)
        dvSum60.Push (bar.vwap * bar.volume)
        // baselines: ln(bar value) always; ln(sum_k) only once the sum is warm
        zVol1.Push (log (max bar.volume 1.0))
        zTc1.Push (log (float (max bar.tradeCount 1)))
        let pushWarm (baseline: WinStdMa) (sum: SumMa) =
            if sum.Count = sum.WindowSize then
                match sum.State with
                | ValueSome s when s > 0.0 -> baseline.Push (log s)
                | _ -> ()
        pushWarm zVol15 volSum15
        pushWarm zVol30 volSum30
        pushWarm zVol60 volSum60
        pushWarm zTc15 tcSum15
        pushWarm zTc30 tcSum30
        pushWarm zTc60 tcSum60
        sessVwap.Push(bar.vwap * bar.volume, bar.volume)
        max30.Push bar.vwap
        max60.Push bar.vwap
        max120.Push bar.vwap
        max300.Push bar.vwap
        max1200.Push bar.vwap
        min30.Push bar.vwap
        min60.Push bar.vwap
        min120.Push bar.vwap
        min300.Push bar.vwap
        min1200.Push bar.vwap
        sessHigh.Push bar.vwap
        sessLow.Push bar.vwap
        // the slot chain: one |r| into the vol EWMAs per completed slot
        match slots.Push(bar.vwap, bar.volume) with
        | ValueSome v ->
            (match prevSlotVwap with
             | ValueSome pv when pv > 0.0 && v > 0.0 ->
                 let ar = abs (log (v / pv))
                 ew40.Push ar
                 ew20.Push ar
                 slotAbsSum.Push ar
                 slotReturns <- slotReturns + 1
             | _ -> ())
            slotLag.Push v
            prevSlotVwap <- ValueSome v
        | ValueNone -> ()

        // ===== 3. fill pendings at THIS bar's vwap (signals from the prior bar) =====
        match pendingEntry with
        | ValueSome p ->
            active.Add { p with EntrySec = bar.etSec; EntryPx = bar.vwap }
            pendingEntry <- ValueNone
        | ValueNone -> ()
        for i in 0 .. active.Count - 1 do
            match active.[i].State with
            | PendingExit reason ->
                active.[i] <- { active.[i] with State = ExitedAt (bar.etSec, bar.vwap, reason) }
            | _ -> ()

        // ===== 4. breach counters: step, then mark this bar's breaches =====
        let breached (prior: float voption) = match prior with ValueSome hi -> bar.vwap > hi | ValueNone -> false
        brSess.Step(); br30.Step(); br60.Step(); br120.Step(); br300.Step(); br1200.Step()
        if breached sSessHigh then brSess.OnBreach()
        if breached sMax30 then br30.OnBreach()
        if breached sMax60 then br60.OnBreach()
        if breached sMax120 then br120.OnBreach()
        if breached sMax300 then br300.OnBreach()
        if breached sMax1200 then br1200.OnBreach()
        // ⭐ the up-leg reset: a new 20m LOW (strict, like every breach here) ends
        // the leg — the trade counter restarts and the leg clock rearms. Fires
        // BEFORE this bar's entry check, so an entry on the very reset bar (rare
        // but possible in partial-warm windows) counts as trade 0 of the new leg.
        legLow.Step()
        (match sMin1200 with
         | ValueSome lo when bar.vwap < lo ->
             legLow.OnBreach()
             tradeIdx <- 0
         | _ -> ())

        // ===== 5. advance open positions: forward marks, hold clock, exit signals =====
        let exitZv = zOf (match cfg.ExitZBars with 1 -> zVol1 | 15 -> zVol15 | 30 -> zVol30 | _ -> zVol60)
                         (match cfg.ExitZBars with 15 -> volSum15 | 30 -> volSum30 | _ -> volSum60)
        let exitZt = zOf (match cfg.ExitZBars with 1 -> zTc1 | 15 -> zTc15 | 30 -> zTc30 | _ -> zTc60)
                         (match cfg.ExitZBars with 15 -> tcSum15 | 30 -> tcSum30 | _ -> tcSum60)
        // k=1 aliases: the "sum" is the bar itself — always warm
        let exitZv = if cfg.ExitZBars = 1 then zVol1.Z (log (max bar.volume 1.0)) else exitZv
        let exitZt = if cfg.ExitZBars = 1 then zTc1.Z (log (float (max bar.tradeCount 1))) else exitZt
        let channelBroken = match sExitMin with ValueSome lo -> bar.vwap < lo | ValueNone -> false
        // compacting walk: survivors overwrite in place, inert trips retire
        let mutable w = 0
        for i in 0 .. active.Count - 1 do
            let p = active.[i]
            // forward marks fill for EVERY trip (exited included — the sampler
            // wants the counterfactual path), first present bar past each horizon
            let p =
                { p with
                    FwdVwap60 = if Double.IsNaN p.FwdVwap60 && bar.etSec >= p.EntrySec + 60 then bar.vwap else p.FwdVwap60
                    FwdVwap300 = if Double.IsNaN p.FwdVwap300 && bar.etSec >= p.EntrySec + 300 then bar.vwap else p.FwdVwap300
                    FwdVwap1200 = if Double.IsNaN p.FwdVwap1200 && bar.etSec >= p.EntrySec + 1200 then bar.vwap else p.FwdVwap1200 }
            let p =
                match p.State with
                | Holding | PendingExit _ -> { p with BarsHeld = p.BarsHeld + 1 }
                | ExitedAt _ -> p
            let p =
                match p.State with
                | Holding ->
                    if bar.etSec >= cfg.MocSec then
                        // the 16:00 bar IS the auction-proximate print — fill here, not next bar
                        { p with State = ExitedAt (bar.etSec, bar.vwap, "moc") }
                    elif channelBroken then { p with State = PendingExit "channel" }
                    elif (match exitZv with ValueSome z -> z < cfg.Ezv | ValueNone -> false) then
                        { p with State = PendingExit "zvol" }
                    elif (match exitZt with ValueSome z -> z < cfg.Ezt | ValueNone -> false) then
                        { p with State = PendingExit "ztc" }
                    else p
                | _ -> p
            // retire when exited AND the last (+1200s) mark has filled — a bar
            // that fills the 1200s mark also fills the 60/300 ones, so nothing
            // in this loop can ever touch the trip again
            match p.State with
            | ExitedAt _ when not (Double.IsNaN p.FwdVwap1200) ->
                retired.Add p
            | _ ->
                active.[w] <- p
                w <- w + 1
        if w < active.Count then active.RemoveRange(w, active.Count - w)

        // ===== 6. entry signal (fills next bar) =====
        let inWindow = bar.etSec >= cfg.EntryStartSec && bar.etSec <= cfg.EntryEndSec
        let channelWarm = entryMax.Count = entryMax.WindowSize
        let isBreakout = breached priorEntryMax
        let floorsOk =
            (match dvSum60.State with ValueSome dv -> dv >= cfg.DvFloor60 | ValueNone -> false)
            && (match tcSum60.State with ValueSome tc -> tc >= cfg.TcFloor60 | ValueNone -> false)
        if inWindow && channelWarm && isBreakout && floorsOk && this.HasSlot then
            pendingEntry <-
                ValueSome
                    { SignalSec = bar.etSec
                      SignalVwap = bar.vwap
                      EntrySec = -1                  // filled next bar (step 3)
                      EntryPx = nan
                      ZVol1 = vv (zVol1.Z (log (max bar.volume 1.0)))
                      ZVol15 = vv (zOf zVol15 volSum15)
                      ZVol30 = vv (zOf zVol30 volSum30)
                      ZVol60 = vv (zOf zVol60 volSum60)
                      ZTc1 = vv (zTc1.Z (log (float (max bar.tradeCount 1))))
                      ZTc15 = vv (zOf zTc15 tcSum15)
                      ZTc30 = vv (zOf zTc30 tcSum30)
                      ZTc60 = vv (zOf zTc60 tcSum60)
                      Vol20m = vv ew40.State
                      Vol10m = vv ew20.State
                      Rng20m = chanRng max1200 min1200
                      Eff20m =
                        (match slotLag.Last, slotLag.Lagged, slotAbsSum.State with
                         | ValueSome cur, ValueSome old, ValueSome s
                             when slotAbsSum.Count = slotAbsSum.WindowSize && old > 0.0 && s > 0.0 ->
                             abs (log (cur / old)) / s
                         | _ -> nan)
                      SlotCount = slotReturns
                      RngSess =
                        (match sessHigh.State, sessLow.State with
                         | ValueSome h, ValueSome l when l > 0.0 -> log (h / l)
                         | _ -> nan)
                      Rng300 = chanRng max300 min300
                      Rng120 = chanRng max120 min120
                      Rng60 = chanRng max60 min60
                      Rng30 = chanRng max30 min30
                      BreachSess = brSess.BarsSinceBreach
                      Breach1200 = br1200.BarsSinceBreach
                      Breach300 = br300.BarsSinceBreach
                      Breach120 = br120.BarsSinceBreach
                      Breach60 = br60.BarsSinceBreach
                      Breach30 = br30.BarsSinceBreach
                      TradeIdx = tradeIdx
                      BarsSinceLow1200 = legLow.BarsSinceBreach
                      Gap60 = gap60.Gaps
                      Gap30 = gap30.Gaps
                      Gap15 = gap15.Gaps
                      SessVwap = vv sessVwap.State
                      DistSessVwap =
                        (match sessVwap.State with
                         | ValueSome sv when sv > 0.0 -> log (bar.vwap / sv)
                         | _ -> nan)
                      PctChgOpen =
                        (match openVwap with
                         | ValueSome o when o > 0.0 -> bar.vwap / o - 1.0
                         | _ -> nan)
                      BarVol = bar.volume
                      BarTc = bar.tradeCount
                      Vol15 = vv volSum15.State
                      Vol30 = vv volSum30.State
                      Vol60 = vv volSum60.State
                      Tc15 = vv tcSum15.State
                      Tc30 = vv tcSum30.State
                      Tc60 = vv tcSum60.State
                      DollarVol60 = vv dvSum60.State
                      CumVol = cumVol
                      CumTc = cumTc
                      FwdVwap60 = nan
                      FwdVwap300 = nan
                      FwdVwap1200 = nan
                      BarsHeld = 0
                      State = Holding }
            // ⭐ the trade counter advances on INITIATION (the signal), whether or
            // not the fill materializes — the (rare) end-of-tape dropped pending
            // entry still consumed its place in the leg's sequence.
            tradeIdx <- tradeIdx + 1

    /// Flatten at the tape's last bar: fill any pending exit and force-exit any
    /// holder at the last vwap ("moc" — covers early closes and thin tapes whose
    /// last print lands before MocSec). A pending ENTRY that never filled is
    /// dropped — there was no bar to trade into.
    member _.Flatten (lastBar: SecBar) =
        pendingEntry <- ValueNone
        for i in 0 .. active.Count - 1 do
            match active.[i].State with
            | Holding | PendingExit _ ->
                active.[i] <- { active.[i] with State = ExitedAt (lastBar.etSec, lastBar.vwap, "moc") }
            | ExitedAt _ -> ()
