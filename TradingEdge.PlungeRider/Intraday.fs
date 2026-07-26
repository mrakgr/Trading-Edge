module TradingEdge.PlungeRider.Intraday

open System
open TradingEdge.RollingMa

// ===========================================================================
// PlungeRider — the SHORT-side mirror of SurgeRider (1-second-bar intraday
// momentum, the volume/trade-count-acceleration BREAKDOWN sampler). Forked
// from TradingEdge.SurgeRider/Intraday.fs 2026-07-25 (the D2 two-engine
// design); measurement pedigree in docs/surgerider_results.md F1-F28b.
//
// THE MIRROR (everything else is byte-identical to SurgeRider):
//   ENTRY SIGNAL: bar vwap < the STRICTLY-PRIOR N-present-bar MIN of vwaps
//                 (the bar just made a new channel LOW — ride the breakdown).
//   STOP/EXIT:    vwap > the strictly-prior exit-channel MAX (or MOC).
//   P&L:          SHORT — NetPnL = qty*(entry - exit); ret_exit = 1 - exit/entry.
//   BREACH COUNTERS: track LOW-side breaches (breach_sess = 0 -> the bar is AT
//                 THE SESSION LOW — the tier-A analog).
//   LEG PAIR:     a new 20m HIGH ends the down-leg (bars_since_high_1200 +
//                 trade_idx reset) — the mirror of SurgeRider's new-20m-low.
//   AUX MARKS:    the first new {120,300,600,1200}-bar LOW strictly after the
//                 entry fill, marked at the FOLLOWING bar's vwap (aux_lo_* —
//                 the cover-into-weakness study).
// Direction-NEUTRAL features (z grid, vol block, SIGNED eff — sign still = the
// 20m net direction, so eff < 0 = downtrend = the short's continuation — gaps,
// VWAP distance, chg-open) are identical; post-hoc SQL ports directly.
//
// ⭐ SAMPLER, NOT A BOOK (mc = 0 default): every qualifying bar becomes an
// independent trip; PF on the raw output is ATTRIBUTION. Re-run cells at mc=1.
// ⭐ PRESENT-BAR SEMANTICS (D1) and the F1-F8 vol-block lock: see SurgeRider.
// Live-safe: entry window starts 09:45 (etSec 35100), the knowability floor.
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

/// Missing-second counter over a trailing WALL-CLOCK window: how many of the
/// last `windowSecs` seconds (inclusive) had NO present bar. Identical to
/// SurgeRider's (direction-neutral).
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

/// Bars-since-last-channel-breach, one per channel. ⭐ MIRROR: in PlungeRider a
/// "breach" is a LOW-side break (vwap < the strictly-prior channel min). -1 =
/// this channel's low has not been broken this session; 0 = the CURRENT bar
/// broke it. Step FIRST each bar, then OnBreach, so the breach bar reads 0.
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

/// One sampler trip (SHORT). Features are the state at the SIGNAL bar's close;
/// the fill is the NEXT present bar's vwap. ⭐ NOTHING here gates (beyond the
/// hard entry gates); it is all recorded for post-hoc SQL.
type SurgePosition =
    { SignalSec: int             // the gate bar (features captured here)
      SignalVwap: float          // its vwap — entry slippage = EntryPx/SignalVwap
      EntrySec: int              // the fill bar
      EntryPx: float             // the fill: next present bar's vwap
      // ----- acceleration z-scores (D6: k-bar log sums vs the 1200-bar baseline) -----
      ZVol1: float               // ln(bar volume) vs its own 1200-bar mean/sigma
      ZVol5: float
      ZVol10: float
      ZVol15: float              // ln(15-bar volume sum) vs its 1200-bar baseline
      ZVol30: float
      ZVol60: float
      ZTc1: float
      ZTc5: float
      ZTc10: float
      ZTc15: float
      ZTc30: float
      ZTc60: float
      // ----- the locked volatility block (F1-F8) -----
      Vol20m: float              // EmaHlMa hl=40 slots of |slot return| — THE driver
      Vol10m: float              // hl=20 twin (vol trajectory)
      Rng20m: float              // ln(high/low) of the 1200-bar vwap channel
      Eff20m: float              // SIGNED ln(V/V_40slots_ago)/Sum40|r| ∈ [-1,1]. |eff| = trendiness;
                                 // sign = the 20m NET DIRECTION (eff < 0 = downtrend = the SHORT's
                                 // continuation flavor — F28b's split reads mirrored here).
      Eff10m: float              // 20-slot twin
      SlotCount: int             // slot returns folded so far (vol-feature warmth)
      // ----- channel widths, ln(high/low) per present-bar window -----
      RngSess: float
      Rng300: float
      Rng120: float
      Rng60: float
      Rng30: float
      // ----- ⭐ bars since each channel's LOW was last broken (-1 = never) —
      // breach_sess = 0 -> AT THE SESSION LOW (the tier-A analog) -----
      BreachSess: int
      Breach1200: int
      Breach300: int
      Breach120: int
      Breach60: int
      Breach30: int
      // ----- ⭐ the DOWN-leg reset counters (mirror of SurgeRider's up-leg pair).
      // A NEW 20m HIGH (vwap > the strictly-prior 1200-bar max) ends the down-leg
      // and resets both:
      //   TradeIdx = 0 -> the FIRST short since the leg began (the early breakdown);
      //   TradeIdx = N -> the (N+1)th chase of the same leg.
      //   BarsSinceHigh1200 -> the leg's age in present bars (-1 = no 20m high yet). -----
      TradeIdx: int
      BarsSinceHigh1200: int
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
      Vol5: float                // raw 5/10-bar volume sums (F26d fresh-wave legs)
      Vol10: float
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
      // ----- ⭐ AUX-LOW marks (the mirror of SurgeRider's aux-high marks): the
      // cover-into-weakness study. The first NEW {120,300,600,1200}-present-bar LOW
      // made STRICTLY AFTER the entry fill bar, MARKED AT THE FOLLOWING BAR's vwap
      // (you cover into the bar after the low prints). Detection: the PREVIOUS
      // bar's low-breach-counter snapshot reads 0 and the mark is still nan ->
      // the mark fills at THIS bar's vwap. px = nan / sec = -1 until hit.
      // Prototype "cover at the N-bar low, else trailing stop" in SQL:
      //   ret = if aux_sec <= exit_sec then 1 - aux_px/entry else ret_exit. -----
      AuxLo120: float
      AuxSec120: int
      AuxLo300: float
      AuxSec300: int
      AuxLo600: float
      AuxSec600: int
      AuxLo1200: float
      AuxSec1200: int
      // ----- exit -----
      BarsHeld: int              // present bars from the fill bar to the exit-fill bar
      State: IntraPosState }

/// PlungeRider config. Hard gates only — every other lever is a recorded column.
type IntradayConfig =
    { EntryChannelBars: int      // ⭐ ENTRY: vwap < the prior N-present-bar MIN of vwaps. The channel
                                 // must be WARM (N bars folded) — a partial-window "low" is not a
                                 // breakdown. Low-breach counters for all six windows are recorded, so
                                 // post-hoc can TIGHTEN to 1200/session but not loosen.
      ExitChannelBars: int       // ⭐ STOP: vwap > the prior N-bar MAX -> exit signal (the short's stop).
      ExitZBars: int             // which k feeds the exit z's (60 = the 1m aggregate). {1,15,30,60}.
      Ezv: float                 // exit when z(ln vol sum, k=ExitZBars) < this. Default -inf = off.
      Ezt: float                 // exit when z(ln tc  sum, k=ExitZBars) < this. Default -inf = off.
      DvFloor60: float           // hard gate: Sum60(vwap*volume) >= this at the signal bar. $ terms.
      TcFloor60: float           // hard gate: Sum60(tradeCount) >= this.
      // ⭐ THE VOL BAND (F10): same [MinVol20m, MaxVol20m) hard gate as SurgeRider.
      // ⚠ The band was calibrated on the LONG side; the short side may want the
      // >= 40bp blowoff region the ceiling excludes (F14b/F16 flagged it as
      // PlungeRider material) — sweep with --max-vol-20m 1e9 to see it.
      MinVol20m: float
      MaxVol20m: float
      MaxConcurrent: int         // 0 = unlimited (THE SAMPLER DEFAULT). 1 = a real book.
      SlotBars: int              // the slot clock: 30 present bars.
      BaselineBars: int          // the z baseline window: 1200 present bars (~20m active).
      SessionStartSec: int       // 34200 = 09:30 — features fold from RTH open.
      EntryStartSec: int         // 35100 = 09:45. ⚠ KNOWABILITY FLOOR (R4) — do not lower.
      EntryEndSec: int           // 48600 = 13:30.
      MocSec: int }              // 57600 = 16:00.

/// The PlungeRider engine. One instance per (ticker, day).
type IntradaySystem(cfg: IntradayConfig, ticker: string, day: DateOnly) =
    // ----- the entry/exit channels + the recorded channel set -----
    let max20 = MaxMa 20
    let max25 = MaxMa 25
    let max30 = MaxMa 30
    let max45 = MaxMa 45
    let max60 = MaxMa 60
    let max120 = MaxMa 120
    let max300 = MaxMa 300
    let max600 = MaxMa 600                       // aux-mark window only
    let max1200 = MaxMa 1200
    let min20 = MinMa 20
    let min25 = MinMa 25
    let min30 = MinMa 30
    let min45 = MinMa 45
    let min60 = MinMa 60
    let min120 = MinMa 120
    let min300 = MinMa 300
    let min600 = MinMa 600                       // aux-mark window only
    let min1200 = MinMa 1200
    let sessHigh = RunMaxMa<float>()
    let sessLow = RunMinMa<float>()
    let chanMax n : MaxMa =
        match n with
        | 20 -> max20 | 25 -> max25 | 30 -> max30 | 45 -> max45 | 60 -> max60 | 120 -> max120 | 300 -> max300 | 1200 -> max1200
        | _ -> invalidArg "n" $"no {n}-bar channel"
    let chanMin n : MinMa =
        match n with
        | 20 -> min20 | 25 -> min25 | 30 -> min30 | 45 -> min45 | 60 -> min60 | 120 -> min120 | 300 -> min300 | 1200 -> min1200
        | _ -> invalidArg "n" $"no {n}-bar channel"
    // ⭐ MIRROR: entry rides the LOW side, the stop sits on the HIGH side.
    let entryMin = chanMin cfg.EntryChannelBars
    let exitMax = chanMax cfg.ExitChannelBars
    // ----- breach counters (bars since each channel LOW was last broken) -----
    let brSess = BreachCounter()
    let br30 = BreachCounter()
    let br60 = BreachCounter()
    let br120 = BreachCounter()
    let br300 = BreachCounter()
    let br1200 = BreachCounter()
    let br600 = BreachCounter()                  // aux-mark window only (no recorded feature)
    // ⭐ the down-leg reset pair: a HIGH-side breach counter on the 1200-bar
    // channel (leg age) + the per-leg trade counter. Reset on every new 20m HIGH.
    let legHigh = BreachCounter()
    let mutable tradeIdx = 0
    // ----- activity sums + the 1200-bar z baselines (identical to SurgeRider) -----
    let volSum5 = SumMa 5
    let volSum10 = SumMa 10
    let volSum15 = SumMa 15
    let volSum30 = SumMa 30
    let volSum60 = SumMa 60
    let tcSum5 = SumMa 5
    let tcSum10 = SumMa 10
    let tcSum15 = SumMa 15
    let tcSum30 = SumMa 30
    let tcSum60 = SumMa 60
    let dvSum60 = SumMa 60                       // Σ vwap·volume — the liquidity floor
    let zVol1 = WinStdMa cfg.BaselineBars        // fed ln(bar volume)
    let zVol5 = WinStdMa cfg.BaselineBars
    let zVol10 = WinStdMa cfg.BaselineBars
    let zVol15 = WinStdMa cfg.BaselineBars       // fed ln(15-bar volume sum) — only once the sum is warm
    let zVol30 = WinStdMa cfg.BaselineBars
    let zVol60 = WinStdMa cfg.BaselineBars
    let zTc1 = WinStdMa cfg.BaselineBars
    let zTc5 = WinStdMa cfg.BaselineBars
    let zTc10 = WinStdMa cfg.BaselineBars
    let zTc15 = WinStdMa cfg.BaselineBars
    let zTc30 = WinStdMa cfg.BaselineBars
    let zTc60 = WinStdMa cfg.BaselineBars
    // ----- the locked vol block (identical) -----
    let slots = SlotVwapMa cfg.SlotBars
    let ew40 = EmaHlMa 40.0                      // vol20m — THE driver (F7 lock)
    let ew20 = EmaHlMa 20.0                      // vol10m — the trajectory twin
    let slotLag = LagMa<float> 40                // slot vwap 40 emissions ago (eff numerator)
    let slotAbsSum = SumMa 40                    // Σ|r| over the same 40 returns (eff denominator)
    let slotLag20 = LagMa<float> 20              // eff10m pair — same stream, half the horizon
    let slotAbsSum20 = SumMa 20
    let mutable prevSlotVwap : float voption = ValueNone
    let mutable prevEtSec = -1                   // the PREVIOUS present bar's etSec (aux-mark lookback)
    let mutable slotReturns = 0
    // ----- gaps / location / session -----
    let gap60 = GapCounter(60, cfg.SessionStartSec)
    let gap30 = GapCounter(30, cfg.SessionStartSec)
    let gap15 = GapCounter(15, cfg.SessionStartSec)
    let sessVwap = RatioMa()
    let mutable openVwap : float voption = ValueNone
    let mutable cumVol = 0.0
    let mutable cumTc = 0.0

    // ⭐ ACTIVE/RETIRED SPLIT (see SurgeRider): inert trips leave the hot loop.
    let active = ResizeArray<SurgePosition>()
    let retired = ResizeArray<SurgePosition>()
    let mutable pendingEntry : SurgePosition voption = ValueNone
    // STRICTLY-PRIOR snapshots, captured BEFORE this bar's vwap folds in. ⚠ If
    // the current vwap were inside its own window, "vwap < channel min" would be
    // trivially false on every bar (a value can't undercut a min that contains it).
    let mutable sMin20 : float voption = ValueNone
    let mutable sMin25 : float voption = ValueNone
    let mutable sMin30 : float voption = ValueNone
    let mutable sMin45 : float voption = ValueNone
    let mutable sMin60 : float voption = ValueNone
    let mutable sMin120 : float voption = ValueNone
    let mutable sMin300 : float voption = ValueNone
    let mutable sMin600 : float voption = ValueNone
    let mutable sMin1200 : float voption = ValueNone
    let mutable sExitMax : float voption = ValueNone
    let mutable sMax1200 : float voption = ValueNone       // the down-leg reset reference
    let mutable sSessLow : float voption = ValueNone

    let vv (v: float voption) = match v with ValueSome x -> x | ValueNone -> nan
    /// ln(high/low) of a channel pair, nan until both sides carry a value.
    let chanRng (hi: MaxMa) (lo: MinMa) =
        match hi.State, lo.State with
        | ValueSome h, ValueSome l when l > 0.0 -> log (h / l)
        | _ -> nan
    /// z of ln(sum_k) against its 1200-bar baseline — nan until the sum is warm
    /// and the baseline has >= 2 values. Inclusive of the current bar.
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
        sMin20 <- min20.State
        sMin25 <- min25.State
        sMin30 <- min30.State
        sMin45 <- min45.State
        sMin60 <- min60.State
        sMin120 <- min120.State
        sMin300 <- min300.State
        sMin600 <- min600.State
        sMin1200 <- min1200.State
        sExitMax <- exitMax.State
        sMax1200 <- max1200.State
        sSessLow <- sessLow.State
        let priorEntryMin =
            match cfg.EntryChannelBars with
            | 20 -> sMin20 | 25 -> sMin25 | 30 -> sMin30 | 45 -> sMin45 | 60 -> sMin60 | 120 -> sMin120 | 300 -> sMin300 | 1200 -> sMin1200
            | _ -> ValueNone

        // ===== 2. fold this bar into every structure =====
        if openVwap.IsNone then openVwap <- ValueSome bar.vwap
        cumVol <- cumVol + bar.volume
        cumTc <- cumTc + float bar.tradeCount
        gap60.Push bar.etSec
        gap30.Push bar.etSec
        gap15.Push bar.etSec
        volSum5.Push bar.volume
        volSum10.Push bar.volume
        volSum15.Push bar.volume
        volSum30.Push bar.volume
        volSum60.Push bar.volume
        tcSum5.Push (float bar.tradeCount)
        tcSum10.Push (float bar.tradeCount)
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
        pushWarm zVol5 volSum5
        pushWarm zVol10 volSum10
        pushWarm zVol15 volSum15
        pushWarm zVol30 volSum30
        pushWarm zVol60 volSum60
        pushWarm zTc5 tcSum5
        pushWarm zTc10 tcSum10
        pushWarm zTc15 tcSum15
        pushWarm zTc30 tcSum30
        pushWarm zTc60 tcSum60
        sessVwap.Push(bar.vwap * bar.volume, bar.volume)
        max20.Push bar.vwap
        max25.Push bar.vwap
        max30.Push bar.vwap
        max45.Push bar.vwap
        max60.Push bar.vwap
        max120.Push bar.vwap
        max300.Push bar.vwap
        max600.Push bar.vwap
        max1200.Push bar.vwap
        min20.Push bar.vwap
        min25.Push bar.vwap
        min30.Push bar.vwap
        min45.Push bar.vwap
        min60.Push bar.vwap
        min120.Push bar.vwap
        min300.Push bar.vwap
        min600.Push bar.vwap
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
                 slotAbsSum20.Push ar
                 slotReturns <- slotReturns + 1
             | _ -> ())
            slotLag.Push v
            slotLag20.Push v
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

        // ===== 4. breach counters: step, then mark this bar's LOW breaches =====
        // The aux-mark logic (step 5) reads the counters AS OF THE PREVIOUS BAR
        // ("previous snapshot's bars-since-low = 0 -> mark on the current bar")
        // — snapshot them before this bar's update.
        let prevBr120 = br120.BarsSinceBreach
        let prevBr300 = br300.BarsSinceBreach
        let prevBr600 = br600.BarsSinceBreach
        let prevBr1200 = br1200.BarsSinceBreach
        // ⭐ MIRROR: a breach = the bar UNDERCUT the strictly-prior channel MIN.
        let breached (prior: float voption) = match prior with ValueSome lo -> bar.vwap < lo | ValueNone -> false
        brSess.Step(); br30.Step(); br60.Step(); br120.Step(); br300.Step(); br600.Step(); br1200.Step()
        if breached sSessLow then brSess.OnBreach()
        if breached sMin30 then br30.OnBreach()
        if breached sMin60 then br60.OnBreach()
        if breached sMin120 then br120.OnBreach()
        if breached sMin300 then br300.OnBreach()
        if breached sMin600 then br600.OnBreach()
        if breached sMin1200 then br1200.OnBreach()
        // ⭐ the down-leg reset: a new 20m HIGH (strict) ends the leg — the trade
        // counter restarts and the leg clock rearms. Fires BEFORE this bar's
        // entry check, so an entry on the very reset bar counts as trade 0.
        legHigh.Step()
        (match sMax1200 with
         | ValueSome hi when bar.vwap > hi ->
             legHigh.OnBreach()
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
        // ⭐ MIRROR: the stop — vwap ABOVE the strictly-prior exit-channel MAX.
        let channelBroken = match sExitMax with ValueSome hi -> bar.vwap > hi | ValueNone -> false
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
            // ⭐ aux-LOW marks (mirror): the PREVIOUS bar's low-breach-counter
            // snapshot reads 0 -> the previous bar printed the new N-bar LOW ->
            // the mark fills at THIS bar's vwap. Only lows printed STRICTLY AFTER
            // the entry fill bar count, and only the FIRST one per window per trip.
            let inline auxStep px sec prevBr =
                if Double.IsNaN px && prevBr = 0 && prevEtSec > p.EntrySec
                then struct (bar.vwap, bar.etSec)
                else struct (px, sec)
            let struct (lo120, sc120) = auxStep p.AuxLo120 p.AuxSec120 prevBr120
            let struct (lo300, sc300) = auxStep p.AuxLo300 p.AuxSec300 prevBr300
            let struct (lo600, sc600) = auxStep p.AuxLo600 p.AuxSec600 prevBr600
            let struct (lo1200, sc1200) = auxStep p.AuxLo1200 p.AuxSec1200 prevBr1200
            let p =
                { p with
                    AuxLo120 = lo120; AuxSec120 = sc120
                    AuxLo300 = lo300; AuxSec300 = sc300
                    AuxLo600 = lo600; AuxSec600 = sc600
                    AuxLo1200 = lo1200; AuxSec1200 = sc1200 }
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
            // retire when exited AND the last (+1200s) mark has filled AND no aux
            // mark is about to fill off THIS bar's low (an unset mark whose
            // counter just hit 0 fills next bar; retiring now would lose it)
            match p.State with
            | ExitedAt _ when not (Double.IsNaN p.FwdVwap1200)
                              && not (Double.IsNaN p.AuxLo120 && br120.BarsSinceBreach = 0)
                              && not (Double.IsNaN p.AuxLo300 && br300.BarsSinceBreach = 0)
                              && not (Double.IsNaN p.AuxLo600 && br600.BarsSinceBreach = 0)
                              && not (Double.IsNaN p.AuxLo1200 && br1200.BarsSinceBreach = 0) ->
                retired.Add p
            | _ ->
                active.[w] <- p
                w <- w + 1
        if w < active.Count then active.RemoveRange(w, active.Count - w)

        // ===== 6. entry signal (fills next bar) =====
        let inWindow = bar.etSec >= cfg.EntryStartSec && bar.etSec <= cfg.EntryEndSec
        let channelWarm = entryMin.Count = entryMin.WindowSize
        // ⭐ MIRROR: the entry is a channel-LOW breakdown.
        let isBreakdown = breached priorEntryMin
        let floorsOk =
            (match dvSum60.State with ValueSome dv -> dv >= cfg.DvFloor60 | ValueNone -> false)
            && (match tcSum60.State with ValueSome tc -> tc >= cfg.TcFloor60 | ValueNone -> false)
        // ⭐ the F10 vol band: floor AND ceiling (long-side calibration — see config)
        let volOk =
            (cfg.MinVol20m <= 0.0
             || (match ew40.State with ValueSome v -> v >= cfg.MinVol20m | ValueNone -> false))
            && (Double.IsPositiveInfinity cfg.MaxVol20m
                || (match ew40.State with ValueSome v -> v < cfg.MaxVol20m | ValueNone -> true))
        if inWindow && channelWarm && isBreakdown && floorsOk && volOk && this.HasSlot then
            pendingEntry <-
                ValueSome
                    { SignalSec = bar.etSec
                      SignalVwap = bar.vwap
                      EntrySec = -1                  // filled next bar (step 3)
                      EntryPx = nan
                      ZVol1 = vv (zVol1.Z (log (max bar.volume 1.0)))
                      ZVol5 = vv (zOf zVol5 volSum5)
                      ZVol10 = vv (zOf zVol10 volSum10)
                      ZVol15 = vv (zOf zVol15 volSum15)
                      ZVol30 = vv (zOf zVol30 volSum30)
                      ZVol60 = vv (zOf zVol60 volSum60)
                      ZTc1 = vv (zTc1.Z (log (float (max bar.tradeCount 1))))
                      ZTc5 = vv (zOf zTc5 tcSum5)
                      ZTc10 = vv (zOf zTc10 tcSum10)
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
                             log (cur / old) / s
                         | _ -> nan)
                      Eff10m =
                        (match slotLag20.Last, slotLag20.Lagged, slotAbsSum20.State with
                         | ValueSome cur, ValueSome old, ValueSome s
                             when slotAbsSum20.Count = slotAbsSum20.WindowSize && old > 0.0 && s > 0.0 ->
                             log (cur / old) / s
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
                      BarsSinceHigh1200 = legHigh.BarsSinceBreach
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
                      Vol5 = vv volSum5.State
                      Vol10 = vv volSum10.State
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
                      AuxLo120 = nan
                      AuxSec120 = -1
                      AuxLo300 = nan
                      AuxSec300 = -1
                      AuxLo600 = nan
                      AuxSec600 = -1
                      AuxLo1200 = nan
                      AuxSec1200 = -1
                      BarsHeld = 0
                      State = Holding }
            // the trade counter advances on INITIATION (the signal), whether or
            // not the fill materializes.
            tradeIdx <- tradeIdx + 1
        // the aux-mark lookback: remember this bar as "the previous bar"
        prevEtSec <- bar.etSec

    /// Flatten at the tape's last bar: fill any pending exit and force-exit any
    /// holder at the last vwap ("moc"). A pending ENTRY that never filled is
    /// dropped — there was no bar to trade into.
    member _.Flatten (lastBar: SecBar) =
        pendingEntry <- ValueNone
        for i in 0 .. active.Count - 1 do
            match active.[i].State with
            | Holding | PendingExit _ ->
                active.[i] <- { active.[i] with State = ExitedAt (lastBar.etSec, lastBar.vwap, "moc") }
            | ExitedAt _ -> ()
