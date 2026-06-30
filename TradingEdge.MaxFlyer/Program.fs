module TradingEdge.MaxFlyer.Program

open System
open System.Diagnostics
open Argu
open TradingEdge.MaxFlyer
open TradingEdge.MaxFlyer.Backtest

let private defaultDb = "/home/mrakgr/Trading-Edge/data/trading.db"
let private defaultMinuteDir = "/home/mrakgr/Trading-Edge/data/minute_aggs"
let private defaultCsv = "/tmp/maxflyer_trips.csv"

type Args =
    | [<AltCommandLine("-d")>] Db_Path of string
    | [<AltCommandLine("-m")>] Minute_Dir of string
    | Start_Date of string
    | End_Date of string
    | [<AltCommandLine("-o")>] Out of string
    | Candidates_Out of string
    | Candidates_In of string
    // Gate 1 — daily selection (D-1)
    | Min_Price of float
    | Min_52w_Pct of float
    | Use_52w_High
    | Max_Tightness of float
    | Max_Atr_Pct of float
    | Min_Max_Atr_Log of float
    | Min_Avg_Dollar_Volume of float
    | Vol_Window of int
    | Min_Prior_Days of int
    // Gate 2 — premarket (D)
    | Min_Gap_Pct of float
    | Max_Gap_Pct of float
    | Min_Premkt_Vol of int64
    | Min_Premkt_Vol_Pct_Of_Avg of float
    // Gate 3 — intraday engine
    | Intraday_Vol_Window of int
    | Intraday_Max_Tightness of float
    | Intraday_Max_Atr_Pct of float
    | Session_Start_Min of int
    | Entry_Start_Min of int
    | Intraday_Stop
    | Pct_Stop of float
    | Time_Stop_Min of int
    | Downside
    | Wick_Breakout
    | Trail_Entry
    | Short
    | Target of string
    | Target_Window of int
    | Moc_Min of int
    | Notional of float
    | Max_Concurrent of int

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Db_Path _ -> "Path to trading.db (DuckDB). Default: the shared data/trading.db."
            | Minute_Dir _ -> "Directory of minute_aggs/{date}.parquet (1m bars). Default: data/minute_aggs."
            | Start_Date _ -> "Backtest start date (yyyy-MM-dd). Default 2021-06-17 (dense 1m coverage)."
            | End_Date _ -> "Backtest end date (yyyy-MM-dd). Default 2026-06-25 (data max)."
            | Out _ -> "Output trips CSV path. Default /tmp/maxflyer_trips.csv."
            | Candidates_Out _ -> "Run ONLY the daily scan (pipeline 1), write the candidate set to this CSV, and exit (no intraday). Caches the slow half so intraday experiments can reuse it via --candidates-in."
            | Candidates_In _ -> "Skip the daily scan; load candidates from this CSV (from a prior --candidates-out) and run ONLY the intraday side. Gate-1/Gate-2 flags are ignored in this mode."
            // Gate 1
            | Min_Price _ -> "Gate 1: min D-1 close price. Default 1.0."
            | Min_52w_Pct _ -> "Gate 1: 52w-high proximity — require close >= this * prior-252d-high. Default 0.95."
            | Use_52w_High -> "Gate 1: gate the 52w band on the prior-252d INTRADAY high instead of the closing high. Default off."
            | Max_Tightness _ -> "Gate 1: max daily consolidation tightness (linear). Default 4.5."
            | Max_Atr_Pct _ -> "Gate 1: max daily ATR%% (log-space). Default 0.10."
            | Min_Max_Atr_Log _ -> "Gate 1: min 'max log ATR' (126-bar max of 14-bar log-ATR) — past-runner floor. Default 0.04. 0 disables."
            | Min_Avg_Dollar_Volume _ -> "Gate 1: min 4-week avg dollar volume. Default 100000."
            | Vol_Window _ -> "Gate 1: lookback (bars) for BOTH daily ATR%% and tightness. Default 14."
            | Min_Prior_Days _ -> "Gate 1: require this many prior daily bars (warmup). Default 21."
            // Gate 2
            | Min_Gap_Pct _ -> "Gate 2: min day-D gap%% = adj_open/prev_adj_close-1. Default 0.10 (a daytrading-grade move)."
            | Max_Gap_Pct _ -> "Gate 2: max day-D gap%%. Default 2.0 (200%%). Pass a large value to disable the cap."
            | Min_Premkt_Vol _ -> "Gate 2: min ABSOLUTE premarket volume (shares, 04:00-09:29 ET). Default 0."
            | Min_Premkt_Vol_Pct_Of_Avg _ -> "Gate 2: min premarket volume as a FRACTION of the 4w avg daily volume. Default 0.20 (premarket traded >= 20%% of a normal full day)."
            // Gate 3
            | Intraday_Vol_Window _ -> "Gate 3: intraday ATR/tightness lookback in 1m BARS. Default 20."
            | Intraday_Max_Tightness _ -> "Gate 3: max intraday tightness (linear) at entry. Default 4.5. Pass a large value to disable."
            | Intraday_Max_Atr_Pct _ -> "Gate 3: max intraday ATR%% (log-space) at entry. Default +inf (off). Sweep this in."
            | Session_Start_Min _ -> "Gate 3: first ET minute fed to the intraday engine (the running extremes accumulate from here). Default 510 (08:30, the SMB 1h opening range)."
            | Entry_Start_Min _ -> "Gate 3: earliest ET minute an entry may fire (wall-clock trading floor). Default 575 (09:35). Premarket bars are processed but not traded before this."
            | Intraday_Stop -> "Gate 3: arm the protective stop (fills at the bar close). Direct entry: the 2-bar local extreme at the breakout (wrong-sided for a direct --short). Under --trail-entry: the SESSION extreme at fill (correctly sided for the short). Default off (hold to MOC)."
            | Pct_Stop _ -> "Gate 3: wide catastrophe %-stop — a fixed adverse excursion from entry (0 = off; e.g. 0.5 = stop if price moves 50%% against the position). Independent of --intraday-stop. Clips the run-over tail while leaving normal noise untouched. Fills at the level (gap-through at the bar open)."
            | Time_Stop_Min _ -> "Gate 3: time-stop — flatten this many minutes after entry (capped at MOC). Default 0 (off). Side-independent."
            | Downside -> "Gate 3: breakout DIRECTION — fire on a new session LOW (close < running low) instead of a new session high. Default off (upside). Independent of --short (direction vs P&L sign); the protective stop flips to the 2-bar high."
            | Wick_Breakout -> "Gate 3: breakout TRIGGER — fire when the bar's HIGH/LOW WICK pierces the prior session extreme, even if it closes back inside. Default off (require a CLOSE through the extreme). Fires more/earlier; admits weaker pierces."
            | Trail_Entry -> "Gate 3: entry MODEL — the breakout only ARMS; track a trailing 2-bar extreme that ratchets with the move, and enter only when a bar CLOSES back through it (short on the rollover, not into the thrust). Default off (enter on the breakout bar). Stopless unless --intraday-stop is also passed, which adds a protective stop at the SESSION extreme at fill."
            | Short -> "Gate 3: trade the breakout SHORT (fade) instead of long — flips the P&L sign; entry signal unchanged. Default off."
            | Target _ -> "Gate 3 (short only): mean-reversion cover target — vwap | ma | channel. Covers when price reverts to the anchor; doubles as the loss-cut. Default none (hold to MOC/time-stop)."
            | Target_Window _ -> "Gate 3: lookback in 1m BARS for --target ma (SMA of closes) or channel (Donchian low). Ignored for vwap (session-cumulative). Default 20."
            | Moc_Min _ -> "Gate 3: MOC cutoff in ET minutes-since-midnight. Default 960 (16:00). RTH bars are session-start..MOC."
            | Notional _ -> "Per-trip notional ($). Default 10000."
            | Max_Concurrent _ -> "Gate 3: cap on currently-OPEN positions per day (0 = unlimited). Default 0."

let private parseDate (s: string) = DateOnly.ParseExact(s, "yyyy-MM-dd")

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Args>(programName = "maxflyer")
    let parsed = parser.Parse argv

    let dbPath    = parsed.GetResult(Db_Path, defaultValue = defaultDb)
    let minuteDir = parsed.GetResult(Minute_Dir, defaultValue = defaultMinuteDir)

    // --target {vwap|ma|channel} + --target-window N (window ignored for vwap).
    let target =
        match parsed.TryGetResult Target with
        | None -> Intraday.NoTarget
        | Some s ->
            let w = parsed.GetResult(Target_Window, defaultValue = 20)
            match s.ToLowerInvariant() with
            | "vwap" -> Intraday.Vwap
            | "ma" -> Intraday.Ma w
            | "channel" -> Intraday.Channel w
            | other -> failwithf "unknown --target '%s' (expected vwap | ma | channel)" other
    let startDate = parseDate (parsed.GetResult(Start_Date, defaultValue = "2021-06-17"))
    let endDate   = parseDate (parsed.GetResult(End_Date,   defaultValue = "2026-06-25"))
    let outPath   = parsed.GetResult(Out, defaultValue = defaultCsv)

    let dc = defaultConfig
    let cfg =
        { dc with
            AtrWindow       = parsed.GetResult(Vol_Window, defaultValue = dc.AtrWindow)
            TightnessWindow = parsed.GetResult(Vol_Window, defaultValue = dc.TightnessWindow)
            Notional        = parsed.GetResult(Notional,   defaultValue = dc.Notional)
            Daily =
              { dc.Daily with
                  MinPrice           = parsed.GetResult(Min_Price,             defaultValue = dc.Daily.MinPrice)
                  Min52wPct          = parsed.GetResult(Min_52w_Pct,           defaultValue = dc.Daily.Min52wPct)
                  Use52wHigh         = parsed.Contains Use_52w_High
                  MaxTightness       = parsed.GetResult(Max_Tightness,         defaultValue = dc.Daily.MaxTightness)
                  MaxAtrPct          = parsed.GetResult(Max_Atr_Pct,           defaultValue = dc.Daily.MaxAtrPct)
                  MinMaxAtrLog       = parsed.GetResult(Min_Max_Atr_Log,       defaultValue = dc.Daily.MinMaxAtrLog)
                  MinAvgDollarVolume = parsed.GetResult(Min_Avg_Dollar_Volume, defaultValue = dc.Daily.MinAvgDollarVolume)
                  MinPriorDays       = parsed.GetResult(Min_Prior_Days,        defaultValue = dc.Daily.MinPriorDays)
                  // Gate 2 (premarket)
                  MinGapPct            = parsed.GetResult(Min_Gap_Pct,    defaultValue = dc.Daily.MinGapPct)
                  MaxGapPct            = parsed.GetResult(Max_Gap_Pct,    defaultValue = dc.Daily.MaxGapPct)
                  MinPremktVol         = parsed.GetResult(Min_Premkt_Vol, defaultValue = dc.Daily.MinPremktVol)
                  MinPremktVolPctOfAvg = parsed.GetResult(Min_Premkt_Vol_Pct_Of_Avg, defaultValue = dc.Daily.MinPremktVolPctOfAvg) }
            Intraday =
              { dc.Intraday with
                  VolWindow       = parsed.GetResult(Intraday_Vol_Window,    defaultValue = dc.Intraday.VolWindow)
                  MaxTightness    = parsed.GetResult(Intraday_Max_Tightness, defaultValue = dc.Intraday.MaxTightness)
                  MaxAtrPct       = parsed.GetResult(Intraday_Max_Atr_Pct,   defaultValue = dc.Intraday.MaxAtrPct)
                  SessionStartMin = parsed.GetResult(Session_Start_Min,      defaultValue = dc.Intraday.SessionStartMin)
                  EntryStartMin   = parsed.GetResult(Entry_Start_Min,        defaultValue = dc.Intraday.EntryStartMin)
                  UseStop         = parsed.Contains Intraday_Stop
                  PctStop         = parsed.GetResult(Pct_Stop,               defaultValue = dc.Intraday.PctStop)
                  TimeStopMin     = parsed.GetResult(Time_Stop_Min,          defaultValue = dc.Intraday.TimeStopMin)
                  Downside        = parsed.Contains Downside
                  WickBreakout    = parsed.Contains Wick_Breakout
                  TrailEntry      = parsed.Contains Trail_Entry
                  Short           = parsed.Contains Short
                  Target          = target
                  MocMin          = parsed.GetResult(Moc_Min,                defaultValue = dc.Intraday.MocMin)
                  MaxConcurrent   = parsed.GetResult(Max_Concurrent,         defaultValue = dc.Intraday.MaxConcurrent) } }

    let inf (x: float) = if Double.IsInfinity x then "inf" else sprintf "%.3f" x

    printfn "MaxFlyer backtest"
    printfn "  db        = %s" dbPath
    printfn "  minute    = %s" minuteDir
    printfn "  range     = %O .. %O" startDate endDate
    printfn "  Gate1 (daily): price>=%.2f 52w>=%.2f%s tight<%.2f atr%%<%.3f maxlogatr>=%.3f adv>=%.0f volwin=%d warmup>%d"
        cfg.Daily.MinPrice cfg.Daily.Min52wPct (if cfg.Daily.Use52wHigh then "(intraday-high)" else "")
        cfg.Daily.MaxTightness cfg.Daily.MaxAtrPct cfg.Daily.MinMaxAtrLog cfg.Daily.MinAvgDollarVolume
        cfg.AtrWindow cfg.Daily.MinPriorDays
    let hhmm m = sprintf "%02d:%02d" (m / 60) (m % 60)
    printfn "  Gate2 (premarket): gap[%.2f,%.2f] premkt_vol>=%d premkt_vol_pct_of_avg>=%.2f"
        cfg.Daily.MinGapPct cfg.Daily.MaxGapPct cfg.Daily.MinPremktVol cfg.Daily.MinPremktVolPctOfAvg
    let targetStr =
        match cfg.Intraday.Target with
        | Intraday.NoTarget -> "none"
        | Intraday.Vwap -> "vwap"
        | Intraday.Ma w -> sprintf "ma%d" w
        | Intraday.Channel w -> sprintf "channel%d" w
    printfn "  Gate3 (intraday): dir=%s trig=%s entry=%s side=%s target=%s volwin=%d tight<%s atr%%<%s session=%s entry>=%s stop=%b pct_stop=%.2f time_stop=%d moc=%s max_concurrent=%d notional=%.0f"
        (if cfg.Intraday.Downside then "DOWN" else "up")
        (if cfg.Intraday.WickBreakout then "wick" else "close")
        (if cfg.Intraday.TrailEntry then "TRAIL" else "direct")
        (if cfg.Intraday.Short then "SHORT" else "long") targetStr
        cfg.Intraday.VolWindow (inf cfg.Intraday.MaxTightness) (inf cfg.Intraday.MaxAtrPct)
        (hhmm cfg.Intraday.SessionStartMin) (hhmm cfg.Intraday.EntryStartMin) cfg.Intraday.UseStop
        cfg.Intraday.PctStop cfg.Intraday.TimeStopMin
        (hhmm cfg.Intraday.MocMin)
        cfg.Intraday.MaxConcurrent cfg.Notional

    let sw = Stopwatch.StartNew()

    // Three modes:
    //  --candidates-out P : run pipeline 1 only, write the candidate set to P, exit.
    //  --candidates-in  P : skip pipeline 1, load candidates from P, run pipeline 2.
    //  (neither)          : the full run (pipeline 1 -> pipeline 2).
    match parsed.TryGetResult Candidates_Out with
    | Some candPath ->
        let candidates = runCollectCandidates dbPath cfg startDate endDate
        sw.Stop()
        writeCandidates candPath candidates
        printfn ""
        printfn "  candidates= %d  (Gate1 & Gate2 passed, %.1f s)" candidates.Length sw.Elapsed.TotalSeconds
        printfn "  wrote     = %s" candPath
        0
    | None ->

    let trips, nCandidates =
        match parsed.TryGetResult Candidates_In with
        | Some candPath ->
            let candidates = readCandidates candPath
            printfn "  loaded %d cached candidates from %s" candidates.Length candPath
            runFromCandidates dbPath minuteDir cfg candidates
        | None -> run dbPath minuteDir cfg startDate endDate
    sw.Stop()

    writeCsv outPath trips

    let wins = trips |> Array.filter (fun t -> t.NetPnL > 0.0)
    let losses = trips |> Array.filter (fun t -> t.NetPnL < 0.0)
    let sumW = wins |> Array.sumBy (fun t -> t.NetPnL)
    let sumL = losses |> Array.sumBy (fun t -> t.NetPnL)
    let pf = if sumL = 0.0 then nan else sumW / -sumL
    let netPnl = trips |> Array.sumBy (fun t -> t.NetPnL)

    printfn ""
    printfn "  candidates= %d  (Gate1 & Gate2 passed)" nCandidates
    printfn "  trips     = %d  (%.1f s)" trips.Length sw.Elapsed.TotalSeconds
    printfn "  win rate  = %.1f%%  (%d / %d)"
        (100.0 * float wins.Length / float (max 1 trips.Length)) wins.Length trips.Length
    printfn "  net P&L   = %s" (netPnl.ToString("N0"))
    printfn "  PF        = %.3f" pf
    printfn "  wrote     = %s" outPath
    0
