module TradingEdge.MomentumV2.Program

open System
open System.Diagnostics
open Argu
open TradingEdge.MomentumV2.Types
open TradingEdge.MomentumV2.Backtest

let private defaultDb = "/home/mrakgr/Trading-Edge/data/trading.db"
let private defaultCsv = "/tmp/momentum_v2_trips.csv"

type Args =
    | [<AltCommandLine("-d")>] Db_Path of string
    | Start_Date of string
    | End_Date of string
    | [<AltCommandLine("-o")>] Out of string
    | Stop_Low_Window of int
    | Trail_Window of int
    | Exit_Time_Cap of int
    | Entry_Limit
    | Entry_Trail_Window of int
    | Entry_Time_Cap of int
    | Expansion_Thr of float
    | Max_Tightness of float
    | Max_Atr_Pct of float
    | Min_Intraday_Ret of float
    | Vol_Window of int
    | Up_Threshold of float
    | Max_Up_Threshold of float
    | Min_Price of float
    | Min_52w_Pct of float
    | Use_52w_High
    | No_Entry_Day_Stop
    | Atr_Stop of float
    | Fixed_Stop of float
    | Fixed_Stop_Be of float
    | Inv_Atr_Stop of float * float
    | Chandelier_Regime of float * float * float
    | Chandelier_Ladder of string
    | Window_Low
    | No_Stop
    | Max_Hold_Bars of int
    | Profit_Target of float
    | Target_Next_Open
    | Exhaustion_Exit
    | Exhaustion_Tightness of float
    | Exhaustion_Rvol of float
    | Exhaustion_Move_Lo of float
    | Exhaustion_Move_Hi of float
    | Exhaustion_Max_Gain of float
    | Exhaustion_Min_Atr_Pct of float
    | Disaster_Exit
    | Disaster_Atr of float
    | Disaster_Loss of float
    | Side of string
    | Tightness_Mode of string
    | Rvol_Min of float
    | Rvol_Max of float

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Db_Path _ -> "Path to trading.db (DuckDB). Default: the shared data/trading.db."
            | Start_Date _ -> "Backtest start date (yyyy-MM-dd). Default 2005-01-01."
            | End_Date _ -> "Backtest end date (yyyy-MM-dd). Default 2026-05-13 (data max)."
            | Out _ -> "Output trips CSV path. Default /tmp/momentum_v2_trips.csv."
            | Stop_Low_Window _ -> "Trailing-stop low window in bars. Default 4."
            | Trail_Window _ -> "Trailing-limit N-day-high window (the resting sell-limit reference). Default 1 (N=1)."
            | Exit_Time_Cap _ -> "Bars the sell limit may rest before exiting at the next open. Default 5. 0 = exit at next open immediately (N ignored)."
            | Entry_Limit -> "Limit-ENTRY mode: instead of buying the signal-bar close, rest a buy limit at the trailing prior-window low (drags down each bar) and fill only on a pullback; on timeout enter at the next open (tagged open_after_cap). Default off (buy the close)."
            | Entry_Trail_Window _ -> "Prior-window-low window the entry buy limit rests at (drags down). Default 4 (= stop window). Only used with --entry-limit."
            | Entry_Time_Cap _ -> "Bars the entry limit may rest before entering at the next open. Default 5. Only used with --entry-limit."
            | Expansion_Thr _ -> "Expansion-exit threshold on the log-tightness scale (exit when tightness > this). Default +inf (off). Live tightness runs ~1.4–13."
            | Max_Tightness _ -> "Max entry tightness (log scale). Default 5.0. Pass a large value to disable."
            | Max_Atr_Pct _ -> "Max entry ATR%% (log scale). Default 0.11. Pass a large value to disable."
            | Min_Intraday_Ret _ -> "Min entry-day intraday return (close/open-1). Default -0.07 — rejects deep intraday FADES (gap-up-then-sell-off). Pass a large negative value to disable."
            | Vol_Window _ -> "Lookback window (bars) for BOTH the ATR%% and tightness measures. Default 14. Sweeps the volatility-base length."
            | Up_Threshold _ -> "Min entry-day move (close/prevClose-1). Default 0.10. The v0/old-system value was 0.05."
            | Max_Up_Threshold _ -> "MAX entry-day move (close/prevClose-1). Default 0.30 — caps the 30%+ single-day blow-off (exhaustion/squeeze/pump that reverts). Pass a large value to disable."
            | Min_Price _ -> "Min entry close price. Default 5.0. Pass 0 to admit sub-$5 names."
            | Min_52w_Pct _ -> "52-week-high proximity: require close >= this * prior-252d-high-close. Default 0.95. 1.0 = strict new high (the old v0 default); 0 drops the gate."
            | Use_52w_High -> "Gate the 52w-proximity band on the prior-252d INTRADAY HIGH instead of the closing high (stricter 'above true resistance'). Default off (closing-high channel)."
            | No_Entry_Day_Stop -> "Drop the Qulla entry-day-low stop floor; use the trailing prior-window low only (no stop until that window warms)."
            | Atr_Stop _ -> "Use an up-only ATR%%-ratchet trailing stop instead of the window-low rule: stop = max(prev, close - k*ATR%%*close), k = this value. Replaces --stop-low-window / entry-day-low geometry."
            | Fixed_Stop _ -> "Use an up-only FIXED-%% ratchet trailing stop: stop = max(prev, close*(1-p)), p = this fraction (e.g. 0.15). Same trailing machinery as --atr-stop but a constant distance. Mutually exclusive with --atr-stop."
            | Fixed_Stop_Be _ -> "Fixed-%% stop CAPPED at break-even: stop = max(prev, min(close*(1-p), entry)) — starts p below entry, ratchets only up to the entry price, then locks. Pair with --max-hold-bars. Mutually exclusive with --atr-stop/--fixed-stop."
            | Inv_Atr_Stop _ -> "Up-only INVERSE-ATR%% ratchet stop: stop%% = w*(atrRef/ATR%%) — TIGHTENS as ATR%% rises, widens when quiet. Two args: w atrRef (e.g. --inv-atr-stop 0.10 0.04 = 10%% stop at 4%% ATR). Clamped to (0,0.95]. Mutually exclusive with the other stop flags."
            | Chandelier_Regime _ -> "Regime-switched CHANDELIER stop off the running max-close: width = (ATR%% >= atrThr ? tightPct : widePct), stop = maxClose*(1-width). Three args: widePct tightPct atrThr (e.g. --chandelier-regime 0.20 0.10 0.10 = 20%% leash when quiet, 10%% once ATR%% >= 10%%). Mutually exclusive with the other stop flags."
            | Chandelier_Ladder _ -> "N-tier CHANDELIER ladder off the running max-close. Spec string: comma-separated atrThr:width tiers plus a base:width, e.g. --chandelier-ladder \"0.10:0.08,0.08:0.10,0.06:0.12,base:0.15\" = 8%% leash at ATR>=10%%, 10%% at >=8%%, 12%% at >=6%%, 15%% below 6%%. Highest matching threshold wins. Mutually exclusive with the other stop flags."
            | Window_Low -> "Use the legacy Qulla WINDOW-LOW trailing stop (trail the prior-`--stop-low-window` low, floored at the entry-day low). This was the default before 2026-06-19; now selectable explicitly. Mutually exclusive with the other stop flags."
            | No_Stop -> "NO price stop at all — hold until another exit fires (exhaustion / time-stop / target) or MTM at the last bar. Diagnostic. Mutually exclusive with the other stop flags."
            | Max_Hold_Bars _ -> "Time-stop: exit at the next open after this many Holding bars (0 = off, default). E.g. 20."
            | Profit_Target _ -> "Fixed profit target as a fraction above entry (0 = off). Resting sell limit, fills intrabar at max(target, open); wins over a same-bar stop (which exits next open). E.g. 0.20."
            | Target_Next_Open -> "With --profit-target: exit at the NEXT bar's open when the target is hit (a signal), instead of the intrabar limit fill."
            | Exhaustion_Exit -> "Enable the conditional exhaustion exit: sell at next open when a HELD bar is a loose-base blow-off — tightness>T AND ((rvol>R AND move>moveLo) OR move>moveHi). Defaults T=7.5 R=3 moveLo=0.05 moveHi=0.10."
            | Exhaustion_Tightness _ -> "Exhaustion exit: loose-base tightness gate T (default 7.5)."
            | Exhaustion_Rvol _ -> "Exhaustion exit: rule-A rvol gate R (default 3.0)."
            | Exhaustion_Move_Lo _ -> "Exhaustion exit: rule-A move gate (default 0.05); fires with rvol>R."
            | Exhaustion_Move_Hi _ -> "Exhaustion exit: rule-B move gate (default 0.10); fires regardless of rvol."
            | Exhaustion_Max_Gain _ -> "Exhaustion exit: only fire while gain-from-entry < this (e.g. 0.10). Default +inf (no cap). A blow-off near entry reverts; far above entry it continues."
            | Exhaustion_Min_Atr_Pct _ -> "Exhaustion exit: only fire when the bar's ATR%% (log-ATR) > this (e.g. 0.12). Default 0 (no gate). High ATR%% at the blow-off marks the names that crater."
            | Disaster_Exit -> "Enable the conditional DISASTER exit (OFF by default). Closes at next open when a held bar is BOTH volatile (current-bar ATR%% > atr) AND under water (gain < loss). Redundant under a 5d hold; it's really a SHORT setup (fwd-10d PF 0.70)."
            | Disaster_Atr _ -> "Disaster exit: ATR%% threshold — fire only when the bar's log-ATR%% exceeds this (default 0.10; try 0.08)."
            | Disaster_Loss _ -> "Disaster exit: loss threshold — fire only when gain-from-entry is below this (default -0.10)."
            | Side _ -> "Trade direction: 'long' (default) or 'short'. Short trails the stop along the prior-window HIGH and flips the P&L sign."
            | Tightness_Mode _ -> "Tightness measure for the entry filter + expansion exit: 'log' (default) or 'linear'. Thresholds differ between modes."
            | Rvol_Min _ -> "Minimum relative volume at entry. Default 5.0 (production)."
            | Rvol_Max _ -> "Maximum relative volume at entry. Default +inf (uncapped) — the 30%-move cap handles the blow-off tail instead. Pass e.g. 15 to also cap rvol."

let private parseDate (s: string) = DateOnly.ParseExact(s, "yyyy-MM-dd")

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Args>(programName = "momentum-v2")
    let parsed = parser.Parse argv

    let dbPath    = parsed.GetResult(Db_Path, defaultValue = defaultDb)
    let startDate = parseDate (parsed.GetResult(Start_Date, defaultValue = "2005-01-01"))
    let endDate   = parseDate (parsed.GetResult(End_Date,   defaultValue = "2026-05-13"))
    let outPath   = parsed.GetResult(Out, defaultValue = defaultCsv)

    let tightnessMode =
        match parsed.TryGetResult Tightness_Mode with
        | None -> defaultConfig.TightnessMode
        | Some s ->
            match s.Trim().ToLowerInvariant() with
            | "log" -> Log
            | "linear" | "lin" -> Linear
            | other -> failwithf "unknown --tightness-mode '%s' (expected 'log' or 'linear')" other

    let side =
        match parsed.TryGetResult Side with
        | None -> defaultConfig.Side
        | Some s ->
            match s.Trim().ToLowerInvariant() with
            | "long" -> Types.Long
            | "short" -> Types.Short
            | other -> failwithf "unknown --side '%s' (expected 'long' or 'short')" other

    // Parse a chandelier-ladder spec "thr:w,thr:w,...,base:w" into (tiers, baseWidth).
    let parseLadder (spec: string) : Types.StopMode =
        let mutable baseW = nan
        let tiers =
            spec.Split(',')
            |> Array.choose (fun part ->
                match part.Trim().Split(':') with
                | [| k; v |] ->
                    let w = float (v.Trim())
                    if k.Trim().ToLowerInvariant() = "base" then baseW <- w; None
                    else Some (float (k.Trim()), w)
                | _ -> failwithf "bad --chandelier-ladder tier '%s' (expected thr:width or base:width)" part)
            |> Array.toList
        if Double.IsNaN baseW then failwith "--chandelier-ladder must include a base:width tier"
        Types.ChandelierLadder (tiers, baseW)

    let cfg =
        { defaultConfig with
            // --vol-window sets BOTH the ATR%% and tightness lookback to the same value.
            AtrWindow       = parsed.GetResult(Vol_Window, defaultValue = defaultConfig.AtrWindow)
            TightnessWindow = parsed.GetResult(Vol_Window, defaultValue = defaultConfig.TightnessWindow)
            StopLowWindow = parsed.GetResult(Stop_Low_Window, defaultValue = defaultConfig.StopLowWindow)
            TrailWindow   = parsed.GetResult(Trail_Window,    defaultValue = defaultConfig.TrailWindow)
            ExitTimeCap   = parsed.GetResult(Exit_Time_Cap,   defaultValue = defaultConfig.ExitTimeCap)
            EntryLimitMode   = parsed.Contains Entry_Limit
            EntryTrailWindow = parsed.GetResult(Entry_Trail_Window, defaultValue = defaultConfig.EntryTrailWindow)
            EntryTimeCap     = parsed.GetResult(Entry_Time_Cap,     defaultValue = defaultConfig.EntryTimeCap)
            ExpansionThr  = parsed.GetResult(Expansion_Thr,   defaultValue = defaultConfig.ExpansionThr)
            UseEntryDayStop = not (parsed.Contains No_Entry_Day_Stop)
            StopMode =
                (let modes =
                    [ parsed.TryGetResult Atr_Stop      |> Option.map AtrRatchet
                      parsed.TryGetResult Fixed_Stop    |> Option.map FixedPct
                      parsed.TryGetResult Fixed_Stop_Be |> Option.map FixedPctBE
                      parsed.TryGetResult Inv_Atr_Stop  |> Option.map InvAtr
                      parsed.TryGetResult Chandelier_Regime |> Option.map ChandelierRegime
                      parsed.TryGetResult Chandelier_Ladder |> Option.map parseLadder
                      (if parsed.Contains Window_Low then Some WindowLow else None)
                      (if parsed.Contains No_Stop then Some NoStop else None) ]
                    |> List.choose id
                 match modes with
                 | []  -> defaultConfig.StopMode   // no stop flag → the config default (now NoStop)
                 | [m] -> m
                 | _   -> failwith "--atr-stop, --fixed-stop, --fixed-stop-be, --inv-atr-stop, --chandelier-regime, --chandelier-ladder, --window-low, --no-stop are mutually exclusive")
            MaxHoldBars = parsed.GetResult(Max_Hold_Bars, defaultValue = defaultConfig.MaxHoldBars)
            ProfitTarget = parsed.GetResult(Profit_Target, defaultValue = defaultConfig.ProfitTarget)
            TargetNextOpen = parsed.Contains Target_Next_Open
            Exhaustion =
              { defaultConfig.Exhaustion with
                  Enabled   = parsed.Contains Exhaustion_Exit
                  Tightness = parsed.GetResult(Exhaustion_Tightness, defaultValue = defaultConfig.Exhaustion.Tightness)
                  Rvol      = parsed.GetResult(Exhaustion_Rvol,      defaultValue = defaultConfig.Exhaustion.Rvol)
                  MoveLo    = parsed.GetResult(Exhaustion_Move_Lo,   defaultValue = defaultConfig.Exhaustion.MoveLo)
                  MoveHi    = parsed.GetResult(Exhaustion_Move_Hi,   defaultValue = defaultConfig.Exhaustion.MoveHi)
                  MaxGain   = parsed.GetResult(Exhaustion_Max_Gain,  defaultValue = defaultConfig.Exhaustion.MaxGain)
                  MinAtrPct = parsed.GetResult(Exhaustion_Min_Atr_Pct, defaultValue = defaultConfig.Exhaustion.MinAtrPct) }
            Disaster =
              { defaultConfig.Disaster with
                  Enabled = parsed.Contains Disaster_Exit
                  AtrThr  = parsed.GetResult(Disaster_Atr,  defaultValue = defaultConfig.Disaster.AtrThr)
                  LossThr = parsed.GetResult(Disaster_Loss, defaultValue = defaultConfig.Disaster.LossThr) }
            Side = side
            TightnessMode = tightnessMode
            Entry =
              { defaultConfig.Entry with
                  UpThreshold  = parsed.GetResult(Up_Threshold,  defaultValue = defaultConfig.Entry.UpThreshold)
                  MaxUpThreshold = parsed.GetResult(Max_Up_Threshold, defaultValue = defaultConfig.Entry.MaxUpThreshold)
                  MinPrice     = parsed.GetResult(Min_Price,     defaultValue = defaultConfig.Entry.MinPrice)
                  Min52wPct    = parsed.GetResult(Min_52w_Pct,   defaultValue = defaultConfig.Entry.Min52wPct)
                  Use52wHigh   = parsed.Contains Use_52w_High
                  MaxTightness = parsed.GetResult(Max_Tightness, defaultValue = defaultConfig.Entry.MaxTightness)
                  MaxAtrPct    = parsed.GetResult(Max_Atr_Pct,   defaultValue = defaultConfig.Entry.MaxAtrPct)
                  MinIntradayRet = parsed.GetResult(Min_Intraday_Ret, defaultValue = defaultConfig.Entry.MinIntradayRet)
                  RvolMin = parsed.GetResult(Rvol_Min, defaultValue = defaultConfig.Entry.RvolMin)
                  RvolMax = parsed.GetResult(Rvol_Max, defaultValue = defaultConfig.Entry.RvolMax) } }

    printfn "MomentumV2 backtest"
    printfn "  db        = %s" dbPath
    printfn "  range     = %O .. %O" startDate endDate
    printfn "  side = %A   stop win = %d   trail N = %d   exit cap = %d   expansion = %.2f   tightness = %A"
        cfg.Side cfg.StopLowWindow cfg.TrailWindow cfg.ExitTimeCap cfg.ExpansionThr cfg.TightnessMode
    printfn "  entry mode = %s   entry trail win = %d   entry cap = %d"
        (if cfg.EntryLimitMode then "trailing-limit" else "at-close") cfg.EntryTrailWindow cfg.EntryTimeCap
    printfn "  stop mode = %s   entry-day-stop = %b   time-stop = %s"
        (match cfg.StopMode with
         | WindowLow -> sprintf "window-low(%d)" cfg.StopLowWindow
         | AtrRatchet k -> sprintf "atr-ratchet k=%.1f" k
         | FixedPct p -> sprintf "fixed-pct p=%.3f" p
         | FixedPctBE p -> sprintf "fixed-pct-BE p=%.3f" p
         | InvAtr (w, atrRef) -> sprintf "inv-atr w=%.3f atrRef=%.3f" w atrRef
         | ChandelierRegime (wide, tight, thr) -> sprintf "chandelier-regime wide=%.3f tight=%.3f atrThr=%.3f" wide tight thr
         | ChandelierLadder (tiers, baseW) ->
             let ts = tiers |> List.sortByDescending fst |> List.map (fun (t,w) -> sprintf "%.2f:%.3f" t w) |> String.concat " "
             sprintf "chandelier-ladder [%s base=%.3f]" ts baseW
         | NoStop -> "none")
        cfg.UseEntryDayStop
        (if cfg.MaxHoldBars > 0 then sprintf "%dd" cfg.MaxHoldBars else "off")
    printfn "  profit target = %s%s"
        (if cfg.ProfitTarget > 0.0 then sprintf "%.0f%%" (cfg.ProfitTarget * 100.0) else "off")
        (if cfg.ProfitTarget > 0.0 && cfg.TargetNextOpen then " (next-open)" else "")
    printfn "  exhaustion exit = %s"
        (if cfg.Exhaustion.Enabled then
            sprintf "tight>%.1f & ((rvol>%.1f & move>%.0f%%) | move>%.0f%%)%s"
                cfg.Exhaustion.Tightness cfg.Exhaustion.Rvol (cfg.Exhaustion.MoveLo*100.0) (cfg.Exhaustion.MoveHi*100.0)
                (if Double.IsInfinity cfg.Exhaustion.MaxGain then "" else sprintf " & gain<%.0f%%" (cfg.Exhaustion.MaxGain*100.0))
              + (if cfg.Exhaustion.MinAtrPct > 0.0 then sprintf " & atr%%>%.0f%%" (cfg.Exhaustion.MinAtrPct*100.0) else "")
         else "off")
    printfn "  disaster exit = %s"
        (if cfg.Disaster.Enabled then
            sprintf "atr%%>%.0f%% & gain<%.0f%%" (cfg.Disaster.AtrThr*100.0) (cfg.Disaster.LossThr*100.0)
         else "off")
    let rvolHi = if System.Double.IsInfinity cfg.Entry.RvolMax then "inf" else sprintf "%.0f" cfg.Entry.RvolMax
    printfn "  entry     = up[%.2f,%.2f) rvol[%.0f,%s] adv>=%.0f price>=%.0f 52w>=%.2f tight<%.2f atr%%<%.2f intraday>=%.2f"
        cfg.Entry.UpThreshold cfg.Entry.MaxUpThreshold cfg.Entry.RvolMin rvolHi cfg.Entry.MinAvgDollarVolume
        cfg.Entry.MinPrice cfg.Entry.Min52wPct cfg.Entry.MaxTightness cfg.Entry.MaxAtrPct cfg.Entry.MinIntradayRet

    let sw = Stopwatch.StartNew()
    let trips = run dbPath cfg startDate endDate
    sw.Stop()

    writeCsv outPath trips

    let wins = trips |> Array.filter (fun t -> t.NetPnL > 0.0)
    let losses = trips |> Array.filter (fun t -> t.NetPnL < 0.0)
    let sumW = wins |> Array.sumBy (fun t -> t.NetPnL)
    let sumL = losses |> Array.sumBy (fun t -> t.NetPnL)
    let pf = if sumL = 0.0 then nan else sumW / -sumL
    let netPnl = trips |> Array.sumBy (fun t -> t.NetPnL)

    printfn ""
    printfn "  trips     = %d  (%.1f s)" trips.Length sw.Elapsed.TotalSeconds
    printfn "  win rate  = %.1f%%  (%d / %d)"
        (100.0 * float wins.Length / float (max 1 trips.Length)) wins.Length trips.Length
    printfn "  net P&L   = %s" (netPnl.ToString("N0"))
    printfn "  PF        = %.3f" pf
    printfn "  wrote     = %s" outPath
    0
