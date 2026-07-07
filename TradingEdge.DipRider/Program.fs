module TradingEdge.DipRider.Program

open System
open System.Diagnostics
open Argu
open TradingEdge.DipRider.Intraday
open TradingEdge.DipRider.Backtest

let private defaultDb = "/home/mrakgr/Trading-Edge/data/trading.db"
let private defaultMinuteDir = "/home/mrakgr/Trading-Edge/data/minute_aggs"
let private defaultCsv = "/tmp/lowflyer_trips.csv"

type Args =
    | [<AltCommandLine("-d")>] Db_Path of string
    | [<AltCommandLine("-m")>] Minute_Dir of string
    | Start_Date of string
    | End_Date of string
    | [<AltCommandLine("-o")>] Out of string
    // intraday engine knobs (the strategy defaults are locked in Backtest.defaultConfig)
    | Entry_Start_Min of int
    | Entry_End_Min of int
    | Vol_Window of int
    | Max_Concurrent of int
    | Min_Low_Ref
    | No_Vol_High
    | Vol_High_Frac of float
    | Min_Bar_Flush of float
    | Min_Bar_Flush_Floor of float
    | Max_Intraday_Atr_Pct of float
    // kept-inert levers (off by default) for later sweeps
    // (the SHORT pop-fade + short-breakdown modes were forked out to TradingEdge.MaxFlyerV2;
    //  LowFlyer is now the LONG flush-fade book only.)
    | Pct_Stop of float
    | Time_Stop_Min of int
    // ----- SMB VWAP x 9-EMA reclaim knobs -----
    | Ema_Period of int
    | Below_Vwap_Frac of float
    | Min_Run_Below_Vwap of int
    | Entry_Stop_Anchor
    | Stop_Dist_Frac of float
    | Min_Stop_Dist_Pct of float
    | Min_Tightness of float
    | Wick_Stop
    | Use_Target
    | Reclaim_Short
    | Skip_Tight_Stop
    | Fixed_Pct_Stop of float
    | Max_Intraday_Tightness of float
    // ----- DipRider knobs -----
    | Reclaim_Mode
    | Dip_Rebreak_Atr of float
    | Dip_Min_Bars_Below_Ema of int
    | Dip_Max_Bars_Below_Ema of int
    | Dip_Min_Trend_Pct of float
    | Dip_Exit_New_High
    | Diprider_V2
    | Run_Reset_Bars_Below of int
    | Dip_V2_Min_Run_Len of int
    | Dip_V2_Reclaim
    | Dip_V2_Min_Bars_Since_Break of int
    | Dip_V2_Max_Bars_Since_Break of int
    | Dip_V2_Pullback_Bar of int
    | Dip_V2_Geom_Stop
    | Dip_V2_Stop_Dist_Frac of float
    | Dip_V2_Buy_Into_Run of int
    | Dip_V2_Exhaust_Exit
    | Dip_V2_Exhaust_Vol_Mult of float
    | Dip_V2_Vwap_Exit_Bars of int
    | Dip_V2_Max_Rvol of float

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Db_Path _ -> "Path to trading.db (DuckDB). Default: the shared data/trading.db."
            | Minute_Dir _ -> "Directory of minute_aggs parquet files. Default: data/minute_aggs."
            | Start_Date _ -> "Backtest start date (yyyy-MM-dd). Default 2003-09-10 (data min)."
            | End_Date _ -> "Backtest end date (yyyy-MM-dd). Default 2026-06-25 (minute-data max)."
            | Out _ -> "Output trips CSV path. Default /tmp/lowflyer_trips.csv."
            | Entry_Start_Min _ -> "Earliest ET minute an entry may fire (600 = 10:00 default). 09:30-EntryStart warms VWAP/EMA + the weakness run."
            | Entry_End_Min _ -> "LATEST ET minute an entry may fire (810 = 13:30 default morning window). 0 or >=MOC = all-day (no upper bound)."
            | Vol_Window _ -> "Intraday ATR/tightness lookback in 1m bars (default 20). Both quality gates are OFF (+inf) by default, so this only affects the recorded *_at_entry snapshots."
            | Max_Concurrent _ -> "Cap on concurrently-open positions per day (0 = unlimited, default)."
            | Min_Low_Ref -> "Switch the breakout reference back to the running min-LOW channel. Default is min-CLOSE (wick-immune, closes-only; +~29%% trips at ~same PF — Run 12)."
            | No_Vol_High -> "DROP the volume-confirmation gate entirely (= --vol-high-frac 0): enter on the FIRST new-session-extreme bar regardless of its 1m volume (default requires the bar to make a new session 1m-volume high). The recorded `new_vol_high` column flags whether each entry bar DID make a strict new vol high."
            | Vol_High_Frac _ -> "RELAX the volume-confirmation gate to a FRACTION of the running session 1m-vol high: require bar volume >= frac * vol_high. Default 1.0 = must EXCEED it (original). e.g. 0.95 / 0.90 = within 5%%/10%% of the high (recovers trips near, but not over, the vol high). 0 = off (same as --no-vol-high)."
            | Min_Bar_Flush _ -> "Entry-bar FLUSH gate: require the breakout bar's 1m move (close/prevClose-1) <= this. Default 0.0 = OFF. e.g. -0.007 rejects bars softer than -0.7% (a real flush candle, not a one-tick poke)."
            | Min_Bar_Flush_Floor _ -> "Entry-bar flush-DEPTH floor (the falling-knife cut, Run 26): reject a flush DEEPER than this. Default 0.0 = OFF. e.g. -0.12 rejects flushes deeper than -12%%. Pairs with --min-bar-flush to band the entry move; PF 3.25->3.45 on the production long."
            | Max_Intraday_Atr_Pct _ -> "Intraday log-ATR CAP at entry: require the 1m log-ATR < this. Default +inf = OFF. e.g. 0.02 rejects names in genuine chaos (per Run 9)."
            | Pct_Stop _ -> "SWEEP LEVER: wide catastrophe %%-stop, a fixed adverse excursion from entry (0 = off, default)."
            | Time_Stop_Min _ -> "SWEEP LEVER: flatten this many minutes after entry, capped at MOC (0 = off, default = hold to MOC)."
            | Ema_Period _ -> "VWAP-reclaim: the fast EMA period that must cross above VWAP (default 9)."
            | Below_Vwap_Frac _ -> "VWAP-reclaim: require the EMA below VWAP for > this fraction of the pre-cross session (default 0.6; 0 = off). Sweep 0.5/0.6/0.75/0.9."
            | Min_Run_Below_Vwap _ -> "VWAP-reclaim: require >= this many CONSECUTIVE bars EMA<VWAP right before the cross (0 = off). The IMMEDIACY of the weakness; an alternative to --below-vwap-frac."
            | Entry_Stop_Anchor -> "VWAP-reclaim: anchor the stop at ENTRY - d*StopDistFrac instead of the default VWAP - d*StopDistFrac (d = VWAP-sessionLow)."
            | Stop_Dist_Frac _ -> "VWAP-reclaim: stop DISTANCE as a fraction of d=(VWAP-sessionLow). Default 0.333 (=d/3, tight). Larger = WIDER stop (0.5, 1.0). The stop-distance sweep lever."
            | Min_Stop_Dist_Pct _ -> "VWAP-reclaim: MIN stop distance as a fraction of entry (Finding 7). Default 0.01 (1%): skip reclaims whose d/3 stop is too tight (chopped inside 1m noise). 0 = off."
            | Min_Tightness _ -> "VWAP-reclaim: MIN intraday tightness at entry (Finding 6). Default 4.5: require a name with real range, not a dead-flat chop. 0 = off."
            | Wick_Stop -> "VWAP-reclaim: revert to the WICK stop (triggers when bar.low touches the stop level) instead of the default CLOSE-based stop (bar must CLOSE below the level, ignoring noise wicks)."
            | Use_Target -> "VWAP-reclaim: RE-ENABLE the VWAP+d profit target (default is NO target — Finding 13: let winners run to MOC; the target caps the momentum-continuation upside)."
            | Reclaim_Short -> "MIRROR the reclaim to the SHORT side: enter when the 9-EMA crosses BELOW VWAP after sustained STRENGTH (EMA above VWAP for the run). d=sessionHigh-VWAP, target below, stop above, P&L short. All other gates apply symmetrically."
            | Skip_Tight_Stop -> "VWAP-reclaim: SKIP a reclaim whose geometric stop is tighter than the min-stop-distance, instead of the default CLAMP (keep the trade, widen the stop to 1%). ~identical at d*2/3."
            | Fixed_Pct_Stop _ -> "VWAP-reclaim: use a FIXED %-below-entry stop (e.g. 0.03 = 3%) instead of the d*2/3 geometry. 0 (default) = geometry. Tests whether the stop's edge is the VWAP-low geometry or just a sensible fixed distance."
            | Max_Intraday_Tightness _ -> "Intraday tightness CAP at entry: require tightness < this. Default +inf = OFF."
            | Reclaim_Mode -> "Switch OFF DipRider and run the VWAP x 9-EMA RECLAIM engine instead (the forked-from behavior)."
            | Dip_Rebreak_Atr _ -> "DipRider: re-break trigger size — close >= prevBar.high * (1 + k*ATR%%). Default 0.5. 0 = any close above the prior high."
            | Dip_Min_Bars_Below_Ema _ -> "DipRider: require >= this many CONSECUTIVE bars closed below the 9-EMA before the re-break (the pullback depth). Default 3. 0 = off."
            | Dip_Max_Bars_Below_Ema _ -> "DipRider: CAP the pullback — reject a re-break with >= this many bars below the 9-EMA (Finding 2: shallow resumes, deep = broken trend). Default 8. 0 = off."
            | Dip_Min_Trend_Pct _ -> "DipRider: UPTREND precondition — the re-break close must be >= this fraction above the session open. Default 0.02 (2%%). 0 = off."
            | Dip_Exit_New_High -> "DipRider: RE-ENABLE the exit-to-new-session-high (Finding 3: OFF by default — the target amputates the continuation runners; hold-to-MOC wins)."
            | Diprider_V2 -> "Switch to DipRider V2 (run-above-9EMA slopes): same re-break entry, gates 2-5 dropped, records the just-ended above-9EMA run's length/log-close slope/log-vol slope/R²s + trailing 20/10/5/2m volume + VWAP for breakdown."
            | Run_Reset_Bars_Below _ -> "V2: how many consecutive below-9EMA closes are EXCUSED before the above-EMA run breaks. Default 1 (a single below-close is forgiven if the next closes back above)."
            | Dip_V2_Min_Run_Len _ -> "V2 GATE: require the just-ended above-9EMA run to have lasted >= this many bars (a real sustained trend, not a 1-bar poke). Default 10. 0 = off."
            | Dip_V2_Reclaim -> "V2 TRIGGER: enter on the 9-EMA RECLAIM (close crosses back above the 9-EMA) instead of the re-break above the prior bar's high. Fires earlier/more often; no prior-high or ATR%% needed."
            | Dip_V2_Min_Bars_Since_Break _ -> "V2 GATE: require >= this many bars since the above-EMA run broke (the true pullback age). Default 0 = off."
            | Dip_V2_Max_Bars_Since_Break _ -> "V2 GATE: require < this many bars since the above-EMA run broke (cap the pullback age). Default 0 = off."
            | Dip_V2_Pullback_Bar _ -> "V2 TRIGGER OVERRIDE: BUY INTO the pullback with NO resumption trigger — enter the Nth consecutive bar below the 9-EMA (bars_below_ema == N), still below the EMA. Overrides reclaim/re-break. 0 = off."
            | Dip_V2_Geom_Stop -> "V2 STOP: use the run-anchored GEOMETRY stop (d = run top - run floor; stop = run floor - d*frac) instead of the 2-bar low. Gives a bought dip room (the 2-bar low trips instantly on a still-falling entry)."
            | Dip_V2_Stop_Dist_Frac _ -> "V2 geometry-stop distance below the run floor as a fraction of the run range (default 0.667 = d*2/3, VwapReclaim F14). Larger = wider. Used by --dip-v2-geom-stop and --dip-v2-buy-into-run."
            | Dip_V2_Buy_Into_Run _ -> "V2 MOMENTUM ENTRY: buy INTO the live above-9EMA run when its length first reaches N bars (fires once/run). NOT a pullback. Uses the live-run geometry stop. Records the live run's len/slope/R2/ATR/%gain. 0 = off."
            | Dip_V2_Exhaust_Exit -> "V2 EXHAUSTION EXIT (MaxFlyer-style): while holding, sell into a NEW-SESSION-HIGH bar on a VOLUME BLOW-OFF (bar vol >= mult × both the 20d-per-min AND opening-15m per-min baselines), filled at that bar's close. Default OFF = hold-to-MOC."
            | Dip_V2_Exhaust_Vol_Mult _ -> "V2 exhaustion blow-off multiplier: require the exit bar's volume >= this × each per-minute baseline. Default 10. Swept (5/10/20). Only with --dip-v2-exhaust-exit."
            | Dip_V2_Vwap_Exit_Bars _ -> "V2 LOSS-OF-VWAP EXIT: once the 9-EMA has been above the session VWAP for >= this many bars during the hold, close the long when the 9-EMA crosses below VWAP. 0 = off (default). e.g. 10."
            | Dip_V2_Max_Rvol _ -> "V2 EXHAUSTION CUT (F22): skip entries where cumulative day volume through entry >= this × the 20d avg DAILY volume (rvol). Default 75 (cuts the blown-out tail). 0 = off."

let private parseDate (s: string) = DateOnly.ParseExact(s, "yyyy-MM-dd")

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Args>(programName = "lowflyer")
    let parsed = parser.Parse argv

    let dbPath    = parsed.GetResult(Db_Path, defaultValue = defaultDb)
    let minuteDir = parsed.GetResult(Minute_Dir, defaultValue = defaultMinuteDir)
    let startDate = parseDate (parsed.GetResult(Start_Date, defaultValue = "2003-09-10"))
    let endDate   = parseDate (parsed.GetResult(End_Date,   defaultValue = "2026-06-25"))
    let outPath   = parsed.GetResult(Out, defaultValue = defaultCsv)

    let cfg =
        { defaultConfig with
            Intraday =
              { defaultConfig.Intraday with
                  EntryStartMin = parsed.GetResult(Entry_Start_Min, defaultValue = defaultConfig.Intraday.EntryStartMin)
                  EntryEndMin   = parsed.GetResult(Entry_End_Min,   defaultValue = defaultConfig.Intraday.EntryEndMin)
                  VolWindow     = parsed.GetResult(Vol_Window,      defaultValue = defaultConfig.Intraday.VolWindow)
                  MaxConcurrent = parsed.GetResult(Max_Concurrent,  defaultValue = defaultConfig.Intraday.MaxConcurrent)
                  MinCloseRef   = not (parsed.Contains Min_Low_Ref)
                  VolHighFrac =
                      if parsed.Contains No_Vol_High then 0.0
                      else parsed.GetResult(Vol_High_Frac, defaultValue = defaultConfig.Intraday.VolHighFrac)
                  MinBarFlush   = parsed.GetResult(Min_Bar_Flush,   defaultValue = defaultConfig.Intraday.MinBarFlush)
                  MinBarFlushFloor = parsed.GetResult(Min_Bar_Flush_Floor, defaultValue = defaultConfig.Intraday.MinBarFlushFloor)
                  MaxAtrPct     = parsed.GetResult(Max_Intraday_Atr_Pct, defaultValue = defaultConfig.Intraday.MaxAtrPct)
                  MaxTightness  = parsed.GetResult(Max_Intraday_Tightness, defaultValue = defaultConfig.Intraday.MaxTightness)
                  // SMB VWAP-reclaim knobs (VwapReclaim/Short come from defaultConfig: long-only reclaim).
                  EmaPeriod      = parsed.GetResult(Ema_Period,       defaultValue = defaultConfig.Intraday.EmaPeriod)
                  BelowVwapFrac  = parsed.GetResult(Below_Vwap_Frac,  defaultValue = defaultConfig.Intraday.BelowVwapFrac)
                  MinRunBelowVwap = parsed.GetResult(Min_Run_Below_Vwap, defaultValue = defaultConfig.Intraday.MinRunBelowVwap)
                  StopAnchorVwap = not (parsed.Contains Entry_Stop_Anchor)
                  StopDistFrac   = parsed.GetResult(Stop_Dist_Frac, defaultValue = defaultConfig.Intraday.StopDistFrac)
                  MinStopDistPct = parsed.GetResult(Min_Stop_Dist_Pct, defaultValue = defaultConfig.Intraday.MinStopDistPct)
                  MinTightness   = parsed.GetResult(Min_Tightness,   defaultValue = defaultConfig.Intraday.MinTightness)
                  StopOnClose    = not (parsed.Contains Wick_Stop)
                  UseTarget      = parsed.Contains Use_Target
                  ClampStopDist  = not (parsed.Contains Skip_Tight_Stop)
                  FixedPctStop   = parsed.GetResult(Fixed_Pct_Stop, defaultValue = defaultConfig.Intraday.FixedPctStop)
                  PctStop       = parsed.GetResult(Pct_Stop,        defaultValue = defaultConfig.Intraday.PctStop)
                  TimeStopMin   = parsed.GetResult(Time_Stop_Min,   defaultValue = defaultConfig.Intraday.TimeStopMin)
                  // --reclaim-short mirrors the whole system to the short side (Short flips P&L, ReclaimShort flips signal).
                  ReclaimShort   = parsed.Contains Reclaim_Short
                  Short          = parsed.Contains Reclaim_Short || defaultConfig.Intraday.Short
                  // Engine select (exclusive): default = DipRider V1; --diprider-v2 = V2; --reclaim-mode = reclaim.
                  DipRiderV2         = parsed.Contains Diprider_V2
                  DipRider           = not (parsed.Contains Reclaim_Mode) && not (parsed.Contains Diprider_V2)
                  VwapReclaim        = parsed.Contains Reclaim_Mode
                  RunResetBarsBelow  = parsed.GetResult(Run_Reset_Bars_Below, defaultValue = defaultConfig.Intraday.RunResetBarsBelow)
                  DipV2MinRunLen     = parsed.GetResult(Dip_V2_Min_Run_Len, defaultValue = defaultConfig.Intraday.DipV2MinRunLen)
                  DipV2Reclaim       = parsed.Contains Dip_V2_Reclaim
                  DipV2MinBarsSinceBreak = parsed.GetResult(Dip_V2_Min_Bars_Since_Break, defaultValue = defaultConfig.Intraday.DipV2MinBarsSinceBreak)
                  DipV2MaxBarsSinceBreak = parsed.GetResult(Dip_V2_Max_Bars_Since_Break, defaultValue = defaultConfig.Intraday.DipV2MaxBarsSinceBreak)
                  DipV2PullbackBar   = parsed.GetResult(Dip_V2_Pullback_Bar, defaultValue = defaultConfig.Intraday.DipV2PullbackBar)
                  DipV2GeomStop      = parsed.Contains Dip_V2_Geom_Stop
                  DipV2StopDistFrac  = parsed.GetResult(Dip_V2_Stop_Dist_Frac, defaultValue = defaultConfig.Intraday.DipV2StopDistFrac)
                  DipV2BuyIntoRun    = parsed.GetResult(Dip_V2_Buy_Into_Run, defaultValue = defaultConfig.Intraday.DipV2BuyIntoRun)
                  DipV2ExhaustExit   = parsed.Contains Dip_V2_Exhaust_Exit
                  DipV2ExhaustVolMult = parsed.GetResult(Dip_V2_Exhaust_Vol_Mult, defaultValue = defaultConfig.Intraday.DipV2ExhaustVolMult)
                  DipV2VwapExitBars  = parsed.GetResult(Dip_V2_Vwap_Exit_Bars, defaultValue = defaultConfig.Intraday.DipV2VwapExitBars)
                  DipV2MaxRvol       = parsed.GetResult(Dip_V2_Max_Rvol, defaultValue = defaultConfig.Intraday.DipV2MaxRvol)
                  DipRebreakAtr      = parsed.GetResult(Dip_Rebreak_Atr,      defaultValue = defaultConfig.Intraday.DipRebreakAtr)
                  DipMinBarsBelowEma = parsed.GetResult(Dip_Min_Bars_Below_Ema, defaultValue = defaultConfig.Intraday.DipMinBarsBelowEma)
                  DipMaxBarsBelowEma = parsed.GetResult(Dip_Max_Bars_Below_Ema, defaultValue = defaultConfig.Intraday.DipMaxBarsBelowEma)
                  DipMinTrendPct     = parsed.GetResult(Dip_Min_Trend_Pct,    defaultValue = defaultConfig.Intraday.DipMinTrendPct)
                  DipExitNewHigh     = parsed.Contains Dip_Exit_New_High } }

    if cfg.Intraday.DipRiderV2 then
        let entryWindow =
            sprintf "%02d:%02d–%02d:%02d ET" (cfg.Intraday.EntryStartMin / 60) (cfg.Intraday.EntryStartMin % 60)
                (cfg.Intraday.EntryEndMin / 60) (cfg.Intraday.EntryEndMin % 60)
        let timeStop = if cfg.Intraday.TimeStopMin > 0 then sprintf "time-stop %dm" cfg.Intraday.TimeStopMin else "no time-stop"
        printfn "DipRider V2 backtest — run-above-9EMA slopes (LONG intraday, research mode)"
        printfn "  db          = %s" dbPath
        printfn "  minute_aggs = %s" minuteDir
        printfn "  range       = %O .. %O" startDate endDate
        printfn "  entry window= %s   %s" entryWindow timeStop
        if cfg.Intraday.DipV2BuyIntoRun > 0 then
            printfn "  entry       = BUY INTO RUN: at live above-9EMA run length = %d bars (momentum; live-run geom stop d*%.2f)" cfg.Intraday.DipV2BuyIntoRun cfg.Intraday.DipV2StopDistFrac
        elif cfg.Intraday.DipV2PullbackBar > 0 then
            printfn "  entry       = BUY INTO PULLBACK: the %d-th bar below the 9-EMA (no resumption trigger)" cfg.Intraday.DipV2PullbackBar
        elif cfg.Intraday.DipV2Reclaim then
            printfn "  entry       = 9-EMA RECLAIM (close crosses back above 9-EMA) after >=1 bar below   (gates 2-5 OFF)"
        else
            printfn "  entry       = re-break close >= prevHigh*(1 + %.2f*ATR%%) after >=1 bar below 9-EMA   (gates 2-5 OFF)" cfg.Intraday.DipRebreakAtr
        printfn "  run gate     = prior above-9EMA run >= %d bars   (tol: excuse %d below-close(s))" cfg.Intraday.DipV2MinRunLen cfg.Intraday.RunResetBarsBelow
        printfn "  recorded    = run_len / run_slope(logC) / run_r2 / run_vol_slope(logV) / run_vol_r2 / run_atr / vol 20-10-5-2m / vwap"
        printfn "  exit        = hold-to-MOC   stop = 2-bar low (%s)" (if cfg.Intraday.StopOnClose then "close-based" else "wick")
    elif cfg.Intraday.DipRider then
        let entryWindow =
            sprintf "%02d:%02d–%02d:%02d ET" (cfg.Intraday.EntryStartMin / 60) (cfg.Intraday.EntryStartMin % 60)
                (cfg.Intraday.EntryEndMin / 60) (cfg.Intraday.EntryEndMin % 60)
        let timeStop = if cfg.Intraday.TimeStopMin > 0 then sprintf "time-stop %dm" cfg.Intraday.TimeStopMin else "no time-stop"
        let newHigh = if cfg.Intraday.DipExitNewHigh then "new-session-high" else "no-new-high-exit"
        printfn "DipRider backtest — pullback-in-uptrend re-break (LONG intraday)"
        printfn "  db          = %s" dbPath
        printfn "  minute_aggs = %s" minuteDir
        printfn "  range       = %O .. %O" startDate endDate
        printfn "  entry window= %s   %s" entryWindow timeStop
        let trendPctDisp = 100.0 * cfg.Intraday.DipMinTrendPct
        let rebreakLine = sprintf "  re-break    = close >= prevHigh*(1 + %.2f*ATR%%)   pullback >= %d bars below 9-EMA   trend >= %.1f%% up" cfg.Intraday.DipRebreakAtr cfg.Intraday.DipMinBarsBelowEma trendPctDisp
        printfn "%s" rebreakLine
        printfn "  exit        = %s + %s + MOC   stop = 2-bar low (%s)" newHigh timeStop
            (if cfg.Intraday.StopOnClose then "close-based" else "wick")
        printfn "  gates       = tightness >= %.1f" cfg.Intraday.MinTightness
    else
        printfn "VwapReclaim backtest — SMB VWAP x %d-EMA reclaim (%s intraday)" cfg.Intraday.EmaPeriod
            (if cfg.Intraday.ReclaimShort then "SHORT / loss-of-VWAP" else "LONG / reclaim")
        printfn "  db          = %s" dbPath
        printfn "  minute_aggs = %s" minuteDir
        printfn "  range       = %O .. %O" startDate endDate
        printfn "  entry window= %02d:%02d–%02d:%02d ET   %s-run >= %d bars   %s"
            (cfg.Intraday.EntryStartMin / 60) (cfg.Intraday.EntryStartMin % 60)
            (cfg.Intraday.EntryEndMin / 60) (cfg.Intraday.EntryEndMin % 60)
            (if cfg.Intraday.ReclaimShort then "above" else "below") cfg.Intraday.MinRunBelowVwap
            (if cfg.Intraday.TimeStopMin > 0 then sprintf "time-stop %dm" cfg.Intraday.TimeStopMin else "hold-to-MOC")
        printfn "  stop anchor = %s   target = %s"
            (if cfg.Intraday.StopAnchorVwap then "VWAP -/+ d*frac" else "entry -/+ d*frac")
            (if cfg.Intraday.UseTarget then "VWAP -/+ (VWAP - sessionExtreme)" else "NO target (run to MOC)")
        printfn "  gates       = tightness >= %.1f   below-frac %s"
            cfg.Intraday.MinTightness
            (if cfg.Intraday.BelowVwapFrac > 0.0 then sprintf "> %.2f" cfg.Intraday.BelowVwapFrac else "off")

    let sw = Stopwatch.StartNew()
    let trips, nCand = run dbPath minuteDir cfg startDate endDate
    sw.Stop()

    writeCsv outPath trips

    let wins   = trips |> Array.filter (fun t -> t.NetPnL > 0.0)
    let losses = trips |> Array.filter (fun t -> t.NetPnL < 0.0)
    let sumW = wins   |> Array.sumBy (fun t -> t.NetPnL)
    let sumL = losses |> Array.sumBy (fun t -> t.NetPnL)
    let pf = if sumL = 0.0 then nan else sumW / -sumL
    let netPnl = trips |> Array.sumBy (fun t -> t.NetPnL)

    printfn ""
    printfn "  candidates = %d  (ticker-days that cleared the preconditions)" nCand
    printfn "  trips      = %d  (%.1f s)" trips.Length sw.Elapsed.TotalSeconds
    printfn "  win rate   = %.1f%%  (%d / %d)"
        (100.0 * float wins.Length / float (max 1 trips.Length)) wins.Length trips.Length
    printfn "  net P&L    = %s" (netPnl.ToString("N0"))
    printfn "  PF (MOC)   = %.3f" pf
    printfn "  wrote      = %s" outPath
    0
