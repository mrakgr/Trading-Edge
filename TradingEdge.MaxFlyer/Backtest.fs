module TradingEdge.MaxFlyer.Backtest

open System
open System.Globalization
open System.Collections.Generic
open DuckDB.NET.Data
open TradingEdge.MaxFlyer.Types
open TradingEdge.MaxFlyer.Intraday

/// Full MaxFlyer configuration: daily Gate-1 + premarket Gate-2 + intraday Gate-3.
type Config =
    { // daily indicator windows
      HiCloseWindow: int
      AtrWindow: int
      TightnessWindow: int
      VolDays: int
      // Gate 1 (daily selection)
      Daily: DailyFilterConfig
      // Gate 2 (premarket, day D)
      MinGapPct: float          // gapPct = rth_open / prevAdjClose - 1 (wide defaults; the intraday move)
      MaxGapPct: float
      MinPremktVol: int64
      // Gate 3 (intraday engine)
      Intraday: IntradayConfig
      Notional: float }

/// Production-leaning defaults. Gate 1 mirrors HighFlyer's locked selection
/// thresholds (sans the daily move/rvol gate). Gate 2 gap band is WIDE by
/// default (the intraday sweet-spot is expected much higher than the swing
/// system's). Gate 3 starts permissive — these are the sweep knobs.
let defaultConfig =
    { HiCloseWindow = 252
      AtrWindow = 14
      TightnessWindow = 14
      VolDays = 28
      Daily =
        { MinPriorDays = 21
          MinAvgDollarVolume = 100_000.0
          Min52wPct = 0.95
          Use52wHigh = false
          MinPrice = 1.0
          MaxTightness = 4.5
          MaxAtrPct = 0.10
          MinMaxAtrLog = 0.04 }
      // Gate 2 — wide gap band, no premarket-volume floor by default.
      MinGapPct = 0.0
      MaxGapPct = 2.0
      MinPremktVol = 0L
      Intraday =
        { VolWindow = 20
          MaxTightness = infinity   // intraday gates OFF by default (sweep them in)
          MaxAtrPct = infinity
          MinHighBars = 15
          UseStop = false
          MocMin = 16 * 60          // 16:00 ET
          RthOpenMin = 9 * 60 + 30  // 09:30 ET
          MaxConcurrent = 0 }       // unlimited concurrent breakouts
      Notional = 10_000.0 }

// ---------------------------------------------------------------------------
// Pass 1 — daily selection (Gate 1) + premarket (Gate 2) → candidates
// ---------------------------------------------------------------------------

/// A fully-qualified (ticker, day-D) candidate. Both Gate 1 (on D-1) and Gate 2
/// (D's premarket) have passed. Carries the D-1 daily snapshots, day-D adjRatio
/// (= adj_close_D / raw_close_D) to put the RAW intraday parquet on the daily
/// adjusted scale, the already-decided gap%/premktVol, and a lazy thunk to the
/// day-D RTH minute bars (forced only in Pass 2).
type Candidate =
    { Ticker: string
      Date: DateOnly            // day D (the trading day)
      SignalDate: DateOnly      // D-1 (where Gate 1 fired)
      PrevAdjClose: float       // (D-1).adj_close
      AdjRatio: float           // (D).adj_close / (D).raw_close
      GapPct: float
      PremktVol: int64
      RthOpen: float
      // Gate-1 snapshots (as of D-1)
      DailyAtrPct: float
      DailyTightness: float
      MaxAtrLog: float
      Pct52w: float
      Pct52wHigh: float
      AvgDolVol: float
      mutable Cell: MinuteBar[] }   // filled in Pass 2 (one parquet scan per date)

/// One daily-bar row from Pass 1's stream.
type private DailyRow =
    { Ticker: string
      Date: DateOnly
      AdjClose: float
      RawClose: float
      PremktVol: int64 voption
      RthOpen: float voption }

/// Pass 1: stream split_adjusted_prices ORDER BY ticker, date; one MaxFlyer
/// per ticker. Gate 1 fires on D-1; we then test Gate 2 on D's premarket row.
/// We keep a one-bar lookback per ticker (the prior row + whether it passed
/// Gate 1 + its daily snapshots) so when D arrives we already hold D-1's result
/// and D's premarket columns — Gate 2 never reads any post-open bar.
let private selectCandidates
        (conn: DuckDBConnection) (cfg: Config)
        (startDate: DateOnly) (endDate: DateOnly) : ResizeArray<Candidate> =

    use cmd = conn.CreateCommand()
    // Universe = common stock + ADRs only. NOTE the SEMI-JOIN (EXISTS), not an inner
    // JOIN: a few tickers (e.g. ASND) have BOTH a 'CS' and an 'ADRC' row in
    // ticker_reference, and an inner join on that would FAN OUT every price row into
    // two identical consecutive rows — corrupting the one-bar D-1/D lookback (it would
    // emit a same-day "signal"). EXISTS filters without multiplying.
    cmd.CommandText <-
        "SELECT p.ticker, p.date, p.adj_open, p.adj_high, p.adj_low, p.adj_close, p.adj_volume,
                d.close AS raw_close,
                pm.premkt_vol, pm.rth_open
         FROM split_adjusted_prices p
         JOIN daily_prices d ON d.ticker = p.ticker AND d.date = p.date
         LEFT JOIN premarket pm ON pm.ticker = p.ticker AND pm.date = p.date
         WHERE EXISTS (SELECT 1 FROM ticker_reference r
                       WHERE r.ticker = p.ticker AND r.type IN ('CS','ADRC'))
           AND p.date >= $start AND p.date <= $end
         ORDER BY p.ticker, p.date"
    let pStart = cmd.CreateParameter() in pStart.ParameterName <- "start"; pStart.Value <- startDate; cmd.Parameters.Add pStart |> ignore
    let pEnd   = cmd.CreateParameter() in pEnd.ParameterName   <- "end";   pEnd.Value   <- endDate;   cmd.Parameters.Add pEnd   |> ignore

    let candidates = ResizeArray<Candidate>()

    // per-ticker accumulators
    let mutable curTicker : string = null
    let mutable sys = Unchecked.defaultof<MaxFlyer>
    // the prior bar's carried Gate-1 result + the snapshots we'd need to emit a candidate.
    let mutable prevPassed = false
    let mutable prevRow = Unchecked.defaultof<DailyRow>
    let mutable prevAtrPct = nan
    let mutable prevTight = nan
    let mutable prevMaxAtrLog = nan
    let mutable prevPct52w = nan
    let mutable prevPct52wHigh = nan
    let mutable prevAvgDolVol = nan

    let newSystem () =
        MaxFlyer(cfg.HiCloseWindow, cfg.AtrWindow, cfg.TightnessWindow,
                 cfg.VolDays, cfg.Daily)

    let resetTicker t =
        curTicker <- t
        sys <- newSystem ()
        prevPassed <- false
        prevRow <- Unchecked.defaultof<DailyRow>

    let orNan = function ValueSome v -> v | ValueNone -> nan

    // Try to emit a candidate for day D (`row`) using the carried D-1 Gate-1 pass.
    let tryEmit (row: DailyRow) =
        if prevPassed && not (isNull (box prevRow)) then
            // Gate 2: gap% from D's RTH open vs D-1 adj close, plus premarket-volume floor.
            // Requires the premarket row present (LEFT JOIN may be null on illiquid/halted days).
            match row.RthOpen, row.PremktVol with
            | ValueSome rthOpen, ValueSome pmVol when prevRow.AdjClose <> 0.0 && row.RawClose <> 0.0 ->
                let gapPct = rthOpen / prevRow.AdjClose - 1.0
                if gapPct >= cfg.MinGapPct && gapPct <= cfg.MaxGapPct && pmVol >= cfg.MinPremktVol then
                    candidates.Add
                        { Ticker = curTicker
                          Date = row.Date
                          SignalDate = prevRow.Date
                          PrevAdjClose = prevRow.AdjClose
                          AdjRatio = row.AdjClose / row.RawClose
                          GapPct = gapPct
                          PremktVol = pmVol
                          RthOpen = rthOpen
                          DailyAtrPct = prevAtrPct
                          DailyTightness = prevTight
                          MaxAtrLog = prevMaxAtrLog
                          Pct52w = prevPct52w
                          Pct52wHigh = prevPct52wHigh
                          AvgDolVol = prevAvgDolVol
                          Cell = Array.empty }
            | _ -> ()

    use reader = cmd.ExecuteReader()
    while reader.Read() do
        let ticker = reader.GetString 0
        if ticker <> curTicker then resetTicker ticker
        let bar : Bar =
            { date   = DateOnly.FromDateTime(reader.GetDateTime 1)
              ``open`` = reader.GetDouble 2
              high   = reader.GetDouble 3
              low    = reader.GetDouble 4
              close  = reader.GetDouble 5
              volume = reader.GetInt64 6 }
        let row : DailyRow =
            { Ticker = ticker
              Date = bar.date
              AdjClose = bar.close
              RawClose = reader.GetDouble 7
              PremktVol = (if reader.IsDBNull 8 then ValueNone else ValueSome (reader.GetInt64 8))
              RthOpen   = (if reader.IsDBNull 9 then ValueNone else ValueSome (reader.GetDouble 9)) }

        // fold D-1..D: this row is day D; the carried prevPassed is D-1's Gate-1 result.
        tryEmit row

        // advance the daily engine through this bar, then carry its Gate-1 verdict forward.
        sys.ProcessBar bar
        prevPassed <- sys.PassesDailyFilter bar
        prevRow <- row
        prevAtrPct <- orNan sys.AtrPct
        prevTight <- orNan sys.Tightness
        prevMaxAtrLog <- orNan sys.MaxAtrLog
        prevPct52w <- orNan (sys.Pct52w bar.close)
        prevPct52wHigh <- orNan (sys.Pct52wHigh bar.close)
        prevAvgDolVol <- orNan sys.AvgDollarVolume

    candidates

// ---------------------------------------------------------------------------
// Pass 2 — intraday (Gate 3): per candidate-date, one parquet scan
// ---------------------------------------------------------------------------

/// A finished intraday trip, ready for the CSV.
type Trip =
    { Symbol: string
      SignalDate: DateOnly
      TradeDate: DateOnly
      GapPct: float
      PremktVol: int64
      PrevAdjClose: float
      AdjRatio: float
      // daily Gate-1 snapshots
      DailyAtrPct: float
      DailyTightness: float
      MaxAtrLog: float
      Pct52w: float
      Pct52wHigh: float
      AvgDolVol: float
      // intraday at entry
      EntryMin: int
      EntryPrice: float
      IntradayAtrPctAtEntry: float
      IntradayTightnessAtEntry: float
      RunHighAtEntry: float
      StopLo: float
      // exit
      ExitMin: int
      ExitPrice: float
      ExitReason: string
      Qty: float
      NetPnL: float
      BarsHeld: int }

let private toTrip (c: Candidate) (notional: float) (pos: IntradayPosition) : Trip =
    match pos.State with
    | ExitedAt (exitMin, exitPx, reason) ->
        let qty = notional / pos.EntryPx
        { Symbol = c.Ticker
          SignalDate = c.SignalDate
          TradeDate = c.Date
          GapPct = c.GapPct
          PremktVol = c.PremktVol
          PrevAdjClose = c.PrevAdjClose
          AdjRatio = c.AdjRatio
          DailyAtrPct = c.DailyAtrPct
          DailyTightness = c.DailyTightness
          MaxAtrLog = c.MaxAtrLog
          Pct52w = c.Pct52w
          Pct52wHigh = c.Pct52wHigh
          AvgDolVol = c.AvgDolVol
          EntryMin = pos.EntryMin
          EntryPrice = pos.EntryPx
          IntradayAtrPctAtEntry = pos.AtrPctAtEntry
          IntradayTightnessAtEntry = pos.TightnessAtEntry
          RunHighAtEntry = pos.RunHiAtEntry
          StopLo = pos.StopLo
          ExitMin = exitMin
          ExitPrice = exitPx
          ExitReason = reason
          Qty = qty
          NetPnL = qty * (exitPx - pos.EntryPx)            // long-only
          BarsHeld = exitMin - pos.EntryMin }              // minutes held (1m bars)
    | Holding -> failwith "toTrip called on a still-Holding position (Finalize first)"

/// Load day D's RTH (09:30..16:00) minute bars for the given tickers, in one
/// parquet scan, split-adjusted per the candidate's adjRatio, and run each
/// candidate's IntradaySystem. Returns all trips for the date.
let private runDate
        (conn: DuckDBConnection) (cfg: Config) (minuteDir: string)
        (date: DateOnly) (cands: Candidate[]) : Trip[] =

    let path = IO.Path.Combine(minuteDir, sprintf "%s.parquet" (date.ToString("yyyy-MM-dd")))
    if not (IO.File.Exists path) then [||]
    else

    let byTicker = cands |> Array.map (fun c -> c.Ticker, c) |> dict
    let tickerList = cands |> Array.map (fun c -> "'" + c.Ticker.Replace("'", "''") + "'") |> String.concat ","

    // ET conversion ported from scripts/equity/intraday_checkpoints.py (lines 84-90).
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        sprintf """
        WITH bars AS (
            SELECT ticker,
                CAST(date_part('hour', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT)*60
                  + CAST(date_part('minute', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT) AS et_min,
                open, high, low, close, volume
            FROM read_parquet('%s') WHERE close > 0 AND ticker IN (%s))
        SELECT ticker, et_min, open, high, low, close, volume
        FROM bars
        WHERE et_min >= %d AND et_min <= %d
        ORDER BY ticker, et_min"""
            (path.Replace("'", "''")) tickerList cfg.Intraday.RthOpenMin cfg.Intraday.MocMin

    // group rows by ticker into the per-candidate cell, applying the split-adjust ratio.
    let cells = Dictionary<string, ResizeArray<MinuteBar>>()
    do
        use reader = cmd.ExecuteReader()
        while reader.Read() do
        let ticker = reader.GetString 0
        match byTicker.TryGetValue ticker with
        | true, c ->
            let r = c.AdjRatio
            let bar : MinuteBar =
                { etMin  = reader.GetInt32 1
                  ``open`` = reader.GetDouble 2 * r
                  high   = reader.GetDouble 3 * r
                  low    = reader.GetDouble 4 * r
                  close  = reader.GetDouble 5 * r
                  volume = reader.GetInt64 6 }
            match cells.TryGetValue ticker with
            | true, lst -> lst.Add bar
            | _ -> let lst = ResizeArray<MinuteBar>() in lst.Add bar; cells.[ticker] <- lst
        | _ -> ()

    let trips = ResizeArray<Trip>()
    for c in cands do
        match cells.TryGetValue c.Ticker with
        | true, bars when bars.Count > 0 ->
            c.Cell <- bars.ToArray()
            let sys = IntradaySystem(cfg.Intraday, c.Ticker, c.Date)
            for b in c.Cell do sys.Process b
            sys.Finalize c.Cell.[c.Cell.Length - 1]
            for pos in sys.Positions do
                match pos.State with
                | ExitedAt _ -> trips.Add(toTrip c cfg.Notional pos)
                | Holding -> ()   // Finalize closes all; unreachable
        | _ -> ()
    trips.ToArray()

/// Run the whole MaxFlyer backtest: Pass 1 (daily selection + premarket Gate 2)
/// then Pass 2 (intraday, grouped by date so each minute_aggs parquet opens at
/// most once, only for dates with ≥1 fully-qualified candidate).
let run (dbPath: string) (minuteDir: string) (cfg: Config)
        (startDate: DateOnly) (endDate: DateOnly) : Trip[] * int =
    let connStr = $"Data Source={dbPath};ACCESS_MODE=READ_ONLY"
    use conn = new DuckDBConnection(connStr)
    conn.Open()
    do
        use pragma = conn.CreateCommand()
        pragma.CommandText <- "PRAGMA memory_limit='6GB'"
        pragma.ExecuteNonQuery() |> ignore

    let candidates = selectCandidates conn cfg startDate endDate

    let trips = ResizeArray<Trip>()
    let byDate = candidates |> Seq.groupBy (fun c -> c.Date)
    for (date, cs) in byDate do
        let arr = Seq.toArray cs
        trips.AddRange(runDate conn cfg minuteDir date arr)

    trips.ToArray(), candidates.Count

// ---------------------------------------------------------------------------
// CSV emission
// ---------------------------------------------------------------------------

let private inv = CultureInfo.InvariantCulture
let private fmt (x: float) = if Double.IsNaN x then "nan" else x.ToString("0.################", inv)

/// HH:MM ET from minutes-since-midnight.
let private hhmm (m: int) = sprintf "%02d:%02d" (m / 60) (m % 60)

let header =
    "symbol,signal_date,trade_date,gap_pct,premkt_vol,prev_adj_close,adj_ratio,"
    + "daily_atr_pct,daily_tightness,max_log_atr,pct_52w,pct_52w_high,avg_dollar_volume_4w,"
    + "entry_time,entry_price,intraday_atr_pct_at_entry,intraday_tightness_at_entry,run_high_at_entry,stop_lo,"
    + "exit_time,exit_price,exit_reason,qty,net_pnl,bars_held_min"

let private row (t: Trip) : string =
    String.concat "," [
        t.Symbol
        t.SignalDate.ToString("yyyy-MM-dd")
        t.TradeDate.ToString("yyyy-MM-dd")
        fmt t.GapPct
        string t.PremktVol
        fmt t.PrevAdjClose
        fmt t.AdjRatio
        fmt t.DailyAtrPct
        fmt t.DailyTightness
        fmt t.MaxAtrLog
        fmt t.Pct52w
        fmt t.Pct52wHigh
        fmt t.AvgDolVol
        hhmm t.EntryMin
        fmt t.EntryPrice
        fmt t.IntradayAtrPctAtEntry
        fmt t.IntradayTightnessAtEntry
        fmt t.RunHighAtEntry
        fmt t.StopLo
        hhmm t.ExitMin
        fmt t.ExitPrice
        t.ExitReason
        fmt t.Qty
        fmt t.NetPnL
        string t.BarsHeld
    ]

let writeCsv (path: string) (trips: Trip[]) =
    use w = new IO.StreamWriter(path)
    w.WriteLine header
    for t in trips do w.WriteLine(row t)
