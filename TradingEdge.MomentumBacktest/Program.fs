module TradingEdge.MomentumBacktest.Program

open System
open System.Diagnostics
open Argu
open DuckDB.NET.Data
open TradingEdge.MomentumBacktest.Types
open TradingEdge.MomentumBacktest.Signals
open TradingEdge.MomentumBacktest.StopWalk
open TradingEdge.MomentumBacktest.Reporting

let defaultDbPath = "data/trading.db"
let defaultStart = "2005-01-01"
let defaultTripsCsv = "data/equity/momentum_v0/trips.csv"
let defaultBreakdownLog = "data/equity/momentum_v0/breakdown.log"

// =============================================================================
// Argu DU
// =============================================================================

type Args =
    | Db_Path of string
    | Start_Date of string
    | End_Date of string
    | Notional of float
    | Up_Threshold of float
    | Rvol_Threshold of float
    | Lookback_High of int
    | Stop_Low_Window of int
    | Min_Prior_Days of int
    | Min_Avg_Dollar_Volume of float
    | All_Security_Types
    | Min_Pct_Of_52w_High of float
    | Max_Tightness of float
    | Max_Atr_Pct of float
    | Expansion_Exit of float
    | Atr_Exit of float
    | Time_Stop of int
    | Stall of int
    | Breakeven_After of int
    | No_Price_Stop
    | Initial_Stop_Day_Low
    | Trail_Limit_High of int
    | Trail_Limit_Time_Cap of int
    | No_Structure
    | Trips_Csv of string
    | Breakdown_Log of string
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Db_Path _ -> "DuckDB database path. Default: " + defaultDbPath
            | Start_Date _ -> "Inclusive signal-start date YYYY-MM-DD (pre-start history warms up indicators). Default 2005-01-01."
            | End_Date _ -> "Inclusive end date YYYY-MM-DD. Default: the data's max date."
            | Notional _ -> "Fixed dollar notional per trade. Default 10000."
            | Up_Threshold _ -> "Minimum same-day return to qualify (fraction). Default 0.05 (5%)."
            | Rvol_Threshold _ -> "Minimum RVOL (adj_volume / avg_volume_4w). Default 3.0."
            | Lookback_High _ -> "52-week-high lookback in trading days. Default 252."
            | Stop_Low_Window _ -> "Trailing-stop low window in trading days. Default 15."
            | Min_Prior_Days _ -> "Minimum prior trading days before a ticker is eligible. Default 21."
            | Min_Avg_Dollar_Volume _ -> "Minimum trailing avg dollar volume (liquidity floor). Default 100000 (the small-cap-inclusive floor the study CSV was built on; a 1M floor cuts the low-ADV names that carry the small-cap premium)."
            | All_Security_Types -> "Include ALL security types (default keeps only CS/ADRC common stock + ADRs)."
            | Min_Pct_Of_52w_High _ -> "52-week-high proximity gate: require entry close >= X * prior-252-day-high-close. 1.0 = strict new high (default). 0.85 = within 15%% of the 52w high. Pass 0 (or <=0) to DROP the gate (full breakout range). Replaces the old --no-52w-high."
            | Max_Tightness _ -> "Entry filter: only take entries whose 14-day tightness range/(14*ATR) <= this (e.g. 0.40). Off by default."
            | Max_Atr_Pct _ -> "Entry filter: only take entries whose 14-day ATR%% <= this (e.g. 0.08 = 8%%). Off by default."
            | Expansion_Exit _ -> "Volatility-expansion exit: close a held trip when its rolling 14-day tightness range/(14*ATR) rises ABOVE this threshold (exit next open). Off by default."
            | Atr_Exit _ -> "ATR%%-expansion exit: close a held trip when its rolling 14-day ATR%% rises ABOVE this threshold (e.g. 0.20 = 20%%; exit next open). Absolute-volatility exit, distinct from --expansion-exit. Off by default."
            | Time_Stop _ -> "Time-stop exit: if no other exit has fired within N held bars, exit at bar T+N (next open). Off by default."
            | Stall _ -> "Stall exit: exit if K consecutive held bars pass with no new since-entry-high close. Off by default."
            | Breakeven_After _ -> "Breakeven after N bars: at bar T+N raise the stop floor to entry price IF in profit (stop = max(15-day-low, entry) thereafter); if not in profit at T+N, exit. Off by default."
            | No_Price_Stop -> "Disable the price stop entirely (no 15-day-low). Exits become time/stall/expansion only. Variant 1."
            | Initial_Stop_Day_Low -> "Floor the stop at the entry-day low (Qullamaggie initial stop) until the 15-day-low rises above it. Variant 2."
            | Trail_Limit_High _ -> "Mean-reversion exit: when a stop/time exit fires, instead of selling next open, rest a SELL LIMIT at the N-day high and ratchet it DOWN-only each bar; fill on the first bar whose high reaches it. N = this window (e.g. 5). Off by default."
            | Trail_Limit_Time_Cap _ -> "Bars to keep the trailing limit resting before bailing to market (next open). Only with --trail-limit-high. Default 5."
            | No_Structure -> "Skip the structure_levels JOIN and 66-column marshalling (full run ~12 min -> seconds). Use when the run needs only core indicators (e.g. breadth x RVOL grids), not the 66 structure columns (which are left empty in the CSV)."
            | Trips_Csv _ -> "Output trips CSV path. Default: " + defaultTripsCsv
            | Breakdown_Log _ -> "Output breakdown log path. Default: " + defaultBreakdownLog

/// Open the shared DB READ-ONLY (the backtest never mutates it) with the same
/// 6GB memory cap Database.openConnection uses. ACCESS_MODE=READ_ONLY guarantees
/// no accidental DDL/DML can touch the 8.3GB file.
let openReadOnly (dbPath: string) : DuckDBConnection =
    let conn = new DuckDBConnection($"Data Source={dbPath};ACCESS_MODE=READ_ONLY")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "PRAGMA memory_limit='6GB'"
    cmd.ExecuteNonQuery() |> ignore
    conn

/// Resolve End_Date default to the data's max split-adjusted date.
let private maxDataDate (conn: System.Data.IDbConnection) : DateOnly =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT MAX(date) FROM split_adjusted_prices"
    match cmd.ExecuteScalar() with
    | :? DateOnly as d -> d
    | :? DateTime as d -> DateOnly.FromDateTime d
    | other -> DateOnly.FromDateTime(Convert.ToDateTime other)

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Args>(programName = "TradingEdge.MomentumBacktest")
    let parsed = parser.Parse(argv, raiseOnUsage = true)

    let dbPath = parsed.GetResult(Db_Path, defaultValue = defaultDbPath)
    let parseDate (s: string) = DateOnly.ParseExact(s, "yyyy-MM-dd")

    use conn = openReadOnly dbPath

    let startDate = parseDate (parsed.GetResult(Start_Date, defaultValue = defaultStart))
    let endDate =
        match parsed.TryGetResult End_Date with
        | Some s -> parseDate s
        | None -> maxDataDate conn

    let cfg = {
        DbPath = dbPath
        StartDate = startDate
        EndDate = endDate
        Notional = parsed.GetResult(Notional, defaultValue = 10000.0)
        UpThreshold = parsed.GetResult(Up_Threshold, defaultValue = 0.05)
        RvolThreshold = parsed.GetResult(Rvol_Threshold, defaultValue = 3.0)
        LookbackHigh = parsed.GetResult(Lookback_High, defaultValue = 252)
        StopLowWindow = parsed.GetResult(Stop_Low_Window, defaultValue = 15)
        MinPriorDays = parsed.GetResult(Min_Prior_Days, defaultValue = 21)
        MinAvgDollarVolume = parsed.GetResult(Min_Avg_Dollar_Volume, defaultValue = 100_000.0)
        TradableOnly = not (parsed.Contains All_Security_Types)
        // Default = strict new-high gate (Some 1.0). A value <= 0 drops the gate (None).
        MinPctOf52wHigh =
            match parsed.GetResult(Min_Pct_Of_52w_High, defaultValue = 1.0) with
            | p when p <= 0.0 -> None
            | p -> Some p
        MaxTightnessAtEntry = parsed.TryGetResult Max_Tightness
        MaxAtrPctAtEntry = parsed.TryGetResult Max_Atr_Pct
        ExpansionExitThreshold = parsed.TryGetResult Expansion_Exit
        AtrExitThreshold = parsed.TryGetResult Atr_Exit
        TimeStopBars = parsed.TryGetResult Time_Stop
        StallBars = parsed.TryGetResult Stall
        BreakevenAfter = parsed.TryGetResult Breakeven_After
        NoPriceStop = parsed.Contains No_Price_Stop
        InitialStopDayLow = parsed.Contains Initial_Stop_Day_Low
        TrailLimitHighWindow = parsed.TryGetResult Trail_Limit_High
        TrailLimitTimeCap = parsed.GetResult(Trail_Limit_Time_Cap, defaultValue = 5)
        NoStructure = parsed.Contains No_Structure
        TripsCsv = parsed.GetResult(Trips_Csv, defaultValue = defaultTripsCsv)
        BreakdownLog = parsed.GetResult(Breakdown_Log, defaultValue = defaultBreakdownLog)
    }

    let expStr =
        (match cfg.ExpansionExitThreshold with Some t -> sprintf " exp-exit=%.2f" t | None -> "") +
        (match cfg.AtrExitThreshold with Some t -> sprintf " atr-exit=%.0f%%" (t*100.0) | None -> "")
    let entStr =
        (match cfg.MaxTightnessAtEntry with Some t -> sprintf " tight<=%.2f" t | None -> "") +
        (match cfg.MaxAtrPctAtEntry with Some a -> sprintf " atr<=%.0f%%" (a*100.0) | None -> "")
    let timeStr = match cfg.TimeStopBars with Some n -> sprintf " time-stop=%dd" n | None -> ""
    let stallStr = match cfg.StallBars with Some k -> sprintf " stall=%dd" k | None -> ""
    let beStr = match cfg.BreakevenAfter with Some n -> sprintf " breakeven-after=%dd" n | None -> ""
    let trailStr = match cfg.TrailLimitHighWindow with Some w -> sprintf " trail-limit=%dd-high(cap=%dd)" w cfg.TrailLimitTimeCap | None -> ""
    let stopVarStr = (if cfg.NoPriceStop then " no-price-stop" else "") + (if cfg.InitialStopDayLow then " init-stop=day-low" else "") + trailStr
    printfn "momentum_v0: %s .. %s | up>=%.0f%% rvol>=%.1f %d-day-high%s | stop=%d-day-low%s%s%s | notional=$%.0f | tradable_only=%b min_adv=%.0f"
        (cfg.StartDate.ToString("yyyy-MM-dd")) (cfg.EndDate.ToString("yyyy-MM-dd"))
        (cfg.UpThreshold * 100.0) cfg.RvolThreshold cfg.LookbackHigh entStr cfg.StopLowWindow expStr timeStr (stallStr + beStr + stopVarStr)
        cfg.Notional cfg.TradableOnly cfg.MinAvgDollarVolume

    let sw = Stopwatch.StartNew()

    // Stream ticker-by-ticker so the .NET heap stays bounded; collect trips.
    let tickers = eligibleTickers conn cfg.TradableOnly
    printfn "eligible tickers: %d" tickers.Length

    let allTrips = ResizeArray<Trip>()
    let mutable processed = 0
    for ticker in tickers do
        let rows = loadTicker conn cfg ticker
        if rows.Length > 0 then
            allTrips.AddRange(tripsForTicker cfg rows)
        processed <- processed + 1
        if processed % 1000 = 0 then
            printfn "  ... %d/%d tickers, %d trips so far (%.0fs)"
                processed tickers.Length allTrips.Count sw.Elapsed.TotalSeconds

    let trips = allTrips.ToArray()
    printfn "trips: %d (%.0fs)" trips.Length sw.Elapsed.TotalSeconds

    writeTrips cfg.TripsCsv trips
    printfn "wrote %s" cfg.TripsCsv

    writeBreakdown cfg.BreakdownLog cfg trips
    printfn "wrote %s" cfg.BreakdownLog

    0
