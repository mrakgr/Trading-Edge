module TradingEdge.MaxFlyerV3.Program

open System
open System.Diagnostics
open Argu
open TradingEdge.MaxFlyerV3.Intraday
open TradingEdge.MaxFlyerV3.Backtest

let private defaultDb = "/home/mrakgr/Trading-Edge/data/trading.db"
let private defaultMinuteDir = "/home/mrakgr/Trading-Edge/data/minute_aggs"
let private defaultCsv = "/tmp/maxflyerv2_trips.csv"

type Args =
    | [<AltCommandLine("-d")>] Db_Path of string
    | [<AltCommandLine("-m")>] Minute_Dir of string
    | Start_Date of string
    | End_Date of string
    | [<AltCommandLine("-o")>] Out of string
    // intraday engine knobs (the strategy defaults are locked in Backtest.defaultConfig)
    | Entry_Start_Min of int
    | Vol_Window of int
    | Max_Concurrent of int
    | Min_Low_Ref
    | No_Vol_High
    | Vol_High_Frac of float
    | Min_Bar_Flush of float
    | Min_Bar_Flush_Floor of float
    | Max_Intraday_Atr_Pct of float
    | Min_Intraday_Atr_Pct of float
    | Min_Brv20d of float
    // mode overrides. MaxFlyerV2 DEFAULTS to the short pop-fade (Downside=false, Short=true).
    | Long                 // escape hatch: trade the LONG new-session-LOW flush (= the LowFlyer book) for parity
    | Short_Breakdown      // alternate: SHORT the new-session-LOW breakdown (Downside=true, Short=true)
    | Long_Breakout        // INVERSION: BUY the new-session-HIGH pop, SELL on the 9-EMA down-tick (Short=false, direct entry)
    | Ema_Down_Tick_Exit
    | Short_High_Entry     // apples-to-apples: short the HIGH immediately, arm the stop on the first 9-EMA down-tick
    // kept-inert levers (off by default) for later sweeps
    | Pct_Stop of float
    | Time_Stop_Min of int
    // 9-EMA arm-timer entry + max-EMA stop (the V3 addition)
    | Ema_Entry
    | Ema_Period of int
    | Ema_Arm_Bars of int
    | Ema_Max_Stop
    | Ema_Max_Stop_Buffer of float
    | Ema_Max_Stop_Window of int
    | Ema_Pct_Stop of float
    | Ema_Bars_Since_High_Entry
    | Ema_Bars_Since_High of int
    | Ema_Down_Tick_Entry
    | Ema_Reentries of int
    | Max_Close_Stop
    | Max_Close_Stop_Window of int
    | Max_Close_Stop_Buffer of float

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Db_Path _ -> "Path to trading.db (DuckDB). Default: the shared data/trading.db."
            | Minute_Dir _ -> "Directory of minute_aggs parquet files. Default: data/minute_aggs."
            | Start_Date _ -> "Backtest start date (yyyy-MM-dd). Default 2003-09-10 (data min)."
            | End_Date _ -> "Backtest end date (yyyy-MM-dd). Default 2026-06-25 (minute-data max)."
            | Out _ -> "Output trips CSV path. Default /tmp/maxflyerv2_trips.csv."
            | Entry_Start_Min _ -> "Earliest ET minute an entry may fire (585 = 09:45 default). 09:30-EntryStart warms the running low/vol-high."
            | Vol_Window _ -> "Intraday ATR/tightness lookback in 1m bars (default 20). Both quality gates are OFF (+inf) by default, so this only affects the recorded *_at_entry snapshots."
            | Max_Concurrent _ -> "Cap on concurrently-open positions per day (0 = unlimited, default)."
            | Min_Low_Ref -> "Switch the breakout reference back to the running min-LOW channel. Default is min-CLOSE (wick-immune, closes-only; +~29%% trips at ~same PF — Run 12)."
            | No_Vol_High -> "DROP the volume-confirmation gate entirely (= --vol-high-frac 0): enter on the FIRST new-session-extreme bar regardless of its 1m volume (default requires the bar to make a new session 1m-volume high). The recorded `new_vol_high` column flags whether each entry bar DID make a strict new vol high."
            | Vol_High_Frac _ -> "RELAX the volume-confirmation gate to a FRACTION of the running session 1m-vol high: require bar volume >= frac * vol_high. Default 1.0 = must EXCEED it (original). e.g. 0.95 / 0.90 = within 5%%/10%% of the high (recovers trips near, but not over, the vol high). 0 = off (same as --no-vol-high)."
            | Min_Bar_Flush _ -> "Entry-bar FLUSH gate: require the breakout bar's 1m move (close/prevClose-1) <= this. Default 0.0 = OFF. e.g. -0.007 rejects bars softer than -0.7% (a real flush candle, not a one-tick poke)."
            | Min_Bar_Flush_Floor _ -> "Entry-bar flush-DEPTH floor (the falling-knife cut, Run 26): reject a flush DEEPER than this. Default 0.0 = OFF. e.g. -0.12 rejects flushes deeper than -12%%. Pairs with --min-bar-flush to band the entry move; PF 3.25->3.45 on the production long."
            | Max_Intraday_Atr_Pct _ -> "Intraday log-ATR CAP at entry: require the 1m log-ATR < this. Default +inf = OFF. e.g. 0.02 rejects names in genuine chaos (per Run 9)."
            | Min_Intraday_Atr_Pct _ -> "A-BOOK ATR%% FLOOR (baked in): require the arm-bar trailing-20m log-ATR >= this. Default 0.03. 0 = off."
            | Min_Brv20d _ -> "A-BOOK MAIN LEVER (baked in): require brv20d >= this at the arm bar. brv20d = breakout_bar_vol / (avgvol20*adj_ratio/390). Default 100. 0 = off. Engine emits only A-book trips."
            | Long -> "Escape hatch: trade the LONG new-session-LOW flush-fade (Downside=true, Short=false) — i.e. the LowFlyer book — instead of the default short pop-fade. For cross-system parity checks. Mutually exclusive with --short-breakdown."
            | Short_Breakdown -> "Alternate short: SHORT the new-session-LOW breakdown (Downside=true, Short=true) — momentum continuation of the flush (the 4th quadrant), rather than the default new-HIGH pop-fade. Mutually exclusive with --long."
            | Long_Breakout -> "INVERSION of the short pop-fade: BUY the new-session-HIGH pop on the breakout bar (Short=false, Downside=false, direct entry) and SELL on the first 9-EMA down-tick. Turns off all the short-only EMA machinery. A momentum/continuation test of the same signal the short fades."
            | Ema_Down_Tick_Exit -> "EXIT a LONG when the 9-EMA ticks DOWN vs the prior bar (ema < prevEma). The inversion of --ema-down-tick-entry. Implied by --long-breakout."
            | Short_High_Entry -> "APPLES-TO-APPLES entry-timing test (down-tick mode): short the SIGNAL/breakout bar IMMEDIATELY (the high) instead of waiting for the 9-EMA down-tick, and leave the ema-max stop DORMANT until the first down-tick (when it arms with its roll30-max base frozen at that bar). Re-entries still enter at the next down-tick. Isolates short-the-high vs short-the-down-tick. Requires --ema-entry --ema-down-tick-entry."
            | Pct_Stop _ -> "SWEEP LEVER: wide catastrophe %%-stop, a fixed adverse excursion from entry (0 = off, default)."
            | Time_Stop_Min _ -> "SWEEP LEVER: flatten this many minutes after entry, capped at MOC (0 = off, default = hold to MOC)."
            | Ema_Entry -> "9-EMA ARM-TIMER ENTRY: instead of shorting directly on the breakout, ARM a countdown at the breakout bar (recording its 9-EMA) and SHORT (at close) on the first bar within the window whose 9-EMA closes BELOW that armed level. Re-arms on each new session high. Default OFF (= direct V2 entry)."
            | Ema_Period _ -> "EMA period for the arm-timer entry + max-EMA stop. Default 9."
            | Ema_Arm_Bars _ -> "The cross-under window length (bars) after a breakout during which the 9-EMA-cross short may fire. Default 10."
            | Ema_Max_Stop -> "MAX-EMA STOP: while short, cover (at close) when the live 9-EMA rises ABOVE the running session-max 9-EMA (x (1+buffer)) — a new EMA session high = the pop re-took the high = we're wrong. Default OFF."
            | Ema_Max_Stop_Buffer _ -> "Fractional buffer RAISING the max-EMA stop above the session max (0.0 = at the max, default; 0.05 = 5%% above it). Only used with --ema-max-stop."
            | Ema_Max_Stop_Window _ -> "Max-EMA-stop ANCHOR window in bars: 0 (default) = session-cumulative max 9-EMA; N = a ROLLING N-bar local max 9-EMA (e.g. 30 = 30m EMA high). Re-anchors the stop to the RECENT high near the fill (the session anchor stales when a stock popped early then we short a later, lower pop)."
            | Ema_Pct_Stop _ -> "9-EMA %%-STOP: cover when the live 9-EMA rises to ema_at_entry*(1+x) — a UNIFORM per-trade cap off the ENTRY 9-EMA (vs the session-max anchor). 0 = off (default). e.g. 0.60. Composable with --ema-max-stop (first to fire wins)."
            | Ema_Bars_Since_High_Entry -> "ENTRY TRIGGER: fire on the FIRST weakness — enter when barsSinceEmaHigh reaches --ema-bars-since-high — instead of the 9-EMA cross-under (which can lag ~1h after the pop). Arms on each new 9-EMA session high; once per episode. Requires --ema-entry."
            | Ema_Bars_Since_High _ -> "The bars-since-9EMA-high threshold to enter (default 2). Only with --ema-bars-since-high-entry. Swept."
            | Ema_Down_Tick_Entry -> "ENTRY TRIGGER: fire when the 9-EMA TICKS DOWN vs the prior bar (ema < prevEma) — a pure 'EMA turned down' weakness, NO session-high requirement. Requires --ema-entry; takes precedence over the bars-since-high / cross-under triggers."
            | Ema_Reentries _ -> "RE-ENTRIES after an EMA-stop-out: re-short on the next 9-EMA down-tick up to this many times (0 = off, default). Each re-entry gets a fresh 30m-max-9EMA×(1+buffer) stop. Pair with a TIGHT --ema-max-stop-buffer (e.g. 0.05). Chain ends at the cap or MOC."
            | Max_Close_Stop -> "MAX-CLOSE STOP: while short, cover (at close) when the RAW bar close rises above the rolling-N-bar max close x (1+buffer), frozen at entry. Raw close reacts a bar sooner than the 9-EMA — cuts the worst runners faster. Composable with the EMA stops (first to fire wins). Default OFF."
            | Max_Close_Stop_Window _ -> "Max-close-stop anchor window in bars (rolling max of the raw close). Default 20 (a 20m local price high)."
            | Max_Close_Stop_Buffer _ -> "Fractional buffer RAISING the max-close stop above the rolling max close (0.10 = 10%% above the local high). Only used with --max-close-stop. Default 0.20."

let private parseDate (s: string) = DateOnly.ParseExact(s, "yyyy-MM-dd")

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Args>(programName = "maxflyerv2")
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
                  VolWindow     = parsed.GetResult(Vol_Window,      defaultValue = defaultConfig.Intraday.VolWindow)
                  MaxConcurrent = parsed.GetResult(Max_Concurrent,  defaultValue = defaultConfig.Intraday.MaxConcurrent)
                  MinCloseRef   = not (parsed.Contains Min_Low_Ref)
                  VolHighFrac =
                      if parsed.Contains No_Vol_High then 0.0
                      else parsed.GetResult(Vol_High_Frac, defaultValue = defaultConfig.Intraday.VolHighFrac)
                  MinBarFlush   = parsed.GetResult(Min_Bar_Flush,   defaultValue = defaultConfig.Intraday.MinBarFlush)
                  MinBarFlushFloor = parsed.GetResult(Min_Bar_Flush_Floor, defaultValue = defaultConfig.Intraday.MinBarFlushFloor)
                  MaxAtrPct     = parsed.GetResult(Max_Intraday_Atr_Pct, defaultValue = defaultConfig.Intraday.MaxAtrPct)
                  MinAtrPct     = parsed.GetResult(Min_Intraday_Atr_Pct, defaultValue = defaultConfig.Intraday.MinAtrPct)
                  MinBrv20d     = parsed.GetResult(Min_Brv20d,           defaultValue = defaultConfig.Intraday.MinBrv20d)
                  // Modes (MaxFlyerV2 DEFAULTS to the short pop-fade):
                  //   default            — SHORT the new-session-HIGH pop (Downside=false, Short=true)
                  //   --long             — LONG the new-session-LOW flush (Downside=true, Short=false) [LowFlyer parity]
                  //   --short-breakdown  — SHORT the new-session-LOW breakdown (Downside=true, Short=true)
                  //   --long-breakout    — LONG the new-session-HIGH pop (Downside=false, Short=false), exit on down-tick
                  Downside      = parsed.Contains Long || parsed.Contains Short_Breakdown
                  Short         = not (parsed.Contains Long || parsed.Contains Long_Breakout)
                  PctStop       = parsed.GetResult(Pct_Stop,        defaultValue = defaultConfig.Intraday.PctStop)
                  TimeStopMin   = parsed.GetResult(Time_Stop_Min,   defaultValue = defaultConfig.Intraday.TimeStopMin)
                  // --long-breakout forces the short's EMA machinery OFF (direct entry + down-tick EXIT instead).
                  EmaEntry      = (not (parsed.Contains Long_Breakout)) && (parsed.Contains Ema_Entry || defaultConfig.Intraday.EmaEntry)
                  EmaPeriod     = parsed.GetResult(Ema_Period,   defaultValue = defaultConfig.Intraday.EmaPeriod)
                  EmaArmBars    = parsed.GetResult(Ema_Arm_Bars, defaultValue = defaultConfig.Intraday.EmaArmBars)
                  EmaMaxStop    = (not (parsed.Contains Long_Breakout)) && (parsed.Contains Ema_Max_Stop || defaultConfig.Intraday.EmaMaxStop)
                  EmaMaxStopBuffer = parsed.GetResult(Ema_Max_Stop_Buffer, defaultValue = defaultConfig.Intraday.EmaMaxStopBuffer)
                  EmaMaxStopWindow = parsed.GetResult(Ema_Max_Stop_Window, defaultValue = defaultConfig.Intraday.EmaMaxStopWindow)
                  EmaPctStop = parsed.GetResult(Ema_Pct_Stop, defaultValue = defaultConfig.Intraday.EmaPctStop)
                  EmaDownTickEntry = (parsed.Contains Ema_Down_Tick_Entry || defaultConfig.Intraday.EmaDownTickEntry)
                  EmaReentries = parsed.GetResult(Ema_Reentries, defaultValue = defaultConfig.Intraday.EmaReentries)
                  EmaBarsSinceHighEntry = (parsed.Contains Ema_Bars_Since_High_Entry || defaultConfig.Intraday.EmaBarsSinceHighEntry)
                  EmaBarsSinceHigh = parsed.GetResult(Ema_Bars_Since_High, defaultValue = defaultConfig.Intraday.EmaBarsSinceHigh)
                  MaxCloseStop = (parsed.Contains Max_Close_Stop || defaultConfig.Intraday.MaxCloseStop)
                  MaxCloseStopWindow = parsed.GetResult(Max_Close_Stop_Window, defaultValue = defaultConfig.Intraday.MaxCloseStopWindow)
                  MaxCloseStopBuffer = parsed.GetResult(Max_Close_Stop_Buffer, defaultValue = defaultConfig.Intraday.MaxCloseStopBuffer)
                  EmaDownTickExit = (parsed.Contains Long_Breakout || parsed.Contains Ema_Down_Tick_Exit || defaultConfig.Intraday.EmaDownTickExit)
                  ShortHighEntry = (parsed.Contains Short_High_Entry || defaultConfig.Intraday.ShortHighEntry) } }

    printfn "MaxFlyerV3 backtest — intraday SHORT pop-fade (%s)"
        (match cfg.Intraday.Downside, cfg.Intraday.Short with
         | true,  false -> "LONG the new-session-low flush (mean-reversion)"
         | false, true  -> "SHORT the new-session-HIGH pop (mean-reversion)"
         | true,  true  -> "SHORT the new-session-LOW breakdown (momentum continuation)"
         | false, false -> "LONG the new-session-high pop")
    printfn "  db          = %s" dbPath
    printfn "  minute_aggs = %s" minuteDir
    printfn "  range       = %O .. %O" startDate endDate
    printfn "  entry from  = %02d:%02d ET   vol window = %d   side = %s   %s"
        (cfg.Intraday.EntryStartMin / 60) (cfg.Intraday.EntryStartMin % 60) cfg.Intraday.VolWindow
        (if cfg.Intraday.Short then "SHORT" else "long")
        (if cfg.Intraday.TimeStopMin > 0 then sprintf "time-stop %dm" cfg.Intraday.TimeStopMin
         elif cfg.Intraday.PctStop > 0.0 then sprintf "pct-stop %.0f%%" (cfg.Intraday.PctStop * 100.0)
         else "hold-to-MOC")
    printfn "  breakout    = %s (%s ref)"
        (if cfg.Intraday.Downside then "new session LOW" else "new session HIGH")
        (if cfg.Intraday.MinCloseRef then "CLOSE, wick-immune" else "low/high")
    printfn "  gates       = flush %s   flush-floor %s   log-ATR %s"
        (if cfg.Intraday.MinBarFlush = 0.0 then "off"
         elif cfg.Intraday.Downside then sprintf "<=%.3f" cfg.Intraday.MinBarFlush
         else sprintf ">=%.3f" (-cfg.Intraday.MinBarFlush))
        (if cfg.Intraday.MinBarFlushFloor = 0.0 then "off"
         elif cfg.Intraday.Downside then sprintf ">=%.3f" cfg.Intraday.MinBarFlushFloor
         else sprintf "<=%.3f" (-cfg.Intraday.MinBarFlushFloor))
        (if Double.IsInfinity cfg.Intraday.MaxAtrPct then "off" else sprintf "<%.3f" cfg.Intraday.MaxAtrPct)
    printfn "  A-book      = ATR%% >= %s   brv20d >= %s"
        (if cfg.Intraday.MinAtrPct <= 0.0 then "off" else sprintf "%.3f" cfg.Intraday.MinAtrPct)
        (if cfg.Intraday.MinBrv20d <= 0.0 then "off" else sprintf "%.0f" cfg.Intraday.MinBrv20d)

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
