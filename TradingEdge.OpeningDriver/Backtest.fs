module TradingEdge.OpeningDriver.Backtest

open System
open System.Globalization
open System.Collections.Generic
open DuckDB.NET.Data
open TradingEdge.OpeningDriver.Intraday

// ===========================================================================
// OpeningDriver backtest wiring.
//
// There is NO daily selection engine. Pipeline 1 is a pure-SQL read of the
// `vwap_reclaim_candidate` table (mr_candidate PRE-PRUNED to the in-play universe:
// ADV >= $1M AND rvol_0945 > 1), carrying the daily context (prev/3d close, day
// close, adj ratio, 20-bar avg vol) and the forward returns (D+1/3/5 close,
// reported for post-hoc slicing). Pipeline 2 streams each candidate day's minute
// bars into the IntradaySystem (Intraday.fs), which emits EVERY bar in the entry
// window as a candidate opening-drive trip (held to MOC or a 9-EMA stop). Same
// two-pipeline shape, minus the daily engine.
// ===========================================================================

/// OpeningDriver config = the intraday engine knobs + notional. No daily gates.
type Config =
    { Intraday: IntradayConfig
      Notional: float }

/// Default: emit every bar in [09:45, 10:30] (open+15 .. open+60) as a trip, held
/// to MOC or a 9-EMA stop (BelowVwap by default). Features fold from 09:30. No entry
/// gates — the study filters post-hoc on the emitted CSV.
let defaultConfig =
    { Intraday =
        { VolWindow = 20
          EmaPeriod = 9                  // the 9-EMA (stop reference + slope base)
          SessionStartMin = 9 * 60 + 30  // 09:30 ET — session anchor (no premarket; features warm from here).
          FeatureStartMin = 9 * 60 + 30  // 09:30 ET (570) — VWAP, OLS slopes, ATR, EMA fold from the RTH open.
          EntryStartMin   = 9 * 60 + 45  // 09:45 ET — open+15, the entry-window START.
          EntryEndMin     = 10 * 60 + 30 // 10:30 ET — open+60, the entry-window END.
          MocMin          = 16 * 60      // 16:00 ET
          StopMode        = BelowVwap }  // 9-EMA stop reference: BelowVwap (default) | BelowSessEmaLow.
      Notional = 10_000.0 }

/// One candidate (ticker, day) from mr_candidate, with the daily context the
/// engine + the post-hoc feature slicing need. Forward closes are REPORTED only.
type Candidate =
    { Ticker: string
      Date: DateOnly
      PrevAdjClose: float       // D-1 adj close (= close_1d), the engine's close1d
      Close3d: float            // D-3 adj close (engine's close3d + the 3-day trend feature)
      DayClose: float           // D adj close (fwd-return base)
      AdjRatio: float           // adj_close / raw_close (rescale minute bars)
      CloseFwd1d: float
      CloseFwd3d: float
      CloseFwd5d: float
      DayOpen: float            // first 09:30 RTH bar's open (== engine session open)
      AvgVol20: float }         // 20-bar trailing mean daily volume — /390 = permin20d (exhaustion-cut denom)

/// A finished opening-drive trip. The study's levers (ATR%, price/vol slope, chg_1d/3d,
/// cumulative session volume vs 20d) come from the position snapshot + candidate; all
/// slicing is post-hoc on the CSV.
type Trip =
    { Symbol: string
      TradeDate: DateOnly
      PrevAdjClose: float
      AdjRatio: float
      // intraday at entry
      EntryMin: int
      EntryPrice: float          // = the entry bar's close (the fill)
      StopDistPct: float         // (entry - stopLevel)/entry at entry; nan if no finite stop
      // ----- the study's feature levers (this-bar-inclusive) -----
      LogAtr20: float            // 20m mean log-true-range (ATR%)
      PriceSlope20: float        // 20m OLS slope of log(close)
      VolSlope20: float          // 20m OLS slope of log(volume)
      RvolCum: float             // cumulative session volume / (avgvol20 · bars-elapsed/390): "is the day
                                 // running hot vs its own 20d average pace, through the entry bar?"
      CumVolToEntry: int64       // raw cumulative session volume through the entry bar (rvol_cum numerator)
      EmaAtEntry: float          // the 9-EMA at entry
      VwapAtEntry: float         // session VWAP at entry
      SessEmaLowAtEntry: float   // the 9-EMA session-min at entry (the BelowSessEmaLow stop level)
      BarsSinceEmaHigh: int      // bars since the 9-EMA last made a new session HIGH (0 = entry bar; -1 = none)
      BarsSinceEmaLow: int       // bars since the 9-EMA last made a new session LOW (0 = entry bar; -1 = none)
      EntryVsVwap: float         // entryPx / VWAP - 1 (location vs VWAP at entry)
      // ----- day-scale context / selection features (from the candidate) -----
      Close1d: float             // close-1-day-ago (adj) = PrevAdjClose
      Close3d: float             // close-3-days-ago (adj)
      Chg1d: float               // entryPx / close_1d - 1 — day-direction
      Chg3d: float               // entryPx / close_3d - 1 — 3-day trend
      PctChgSinceOpen: float     // entryPx / dayOpen - 1
      // exit
      ExitMin: int
      ExitPrice: float
      ExitReason: string         // "stop" | "moc"
      RetMoc: float              // exitPx / entryPx - 1 (intraday held return)
      // forward daily returns (base = D's daily close; recomputed in analysis)
      DayClose: float
      CloseFwd1d: float
      CloseFwd3d: float
      CloseFwd5d: float
      Qty: float
      NetPnL: float
      BarsHeld: int }

let private toTrip (c: Candidate) (notional: float) (pos: IntradayPosition) : Trip =
    match pos.State with
    | ExitedAt (exitMin, exitPx, reason) ->
        let qty = notional / pos.EntryPx
        // rvol_cum = cumulative session volume vs the 20d-average pace THROUGH the entry bar. The 20d
        // per-minute pace is avgvol20/390; multiplied by minutes-elapsed-since-open gives the "expected"
        // cumulative volume by now. >1 = the day is running hotter than its 20d norm at this point.
        let minsElapsed = float (max 1 (pos.EntryMin - (9*60+30) + 1))   // bars since the 09:30 open, incl. entry
        let expectedCum = if c.AvgVol20 > 0.0 then (c.AvgVol20 / 390.0) * minsElapsed else nan
        { Symbol = c.Ticker
          TradeDate = c.Date
          PrevAdjClose = c.PrevAdjClose
          AdjRatio = c.AdjRatio
          EntryMin = pos.EntryMin
          EntryPrice = pos.EntryPx
          StopDistPct = pos.StopDistPct
          LogAtr20 = pos.LogAtr20
          PriceSlope20 = pos.PriceSlope20
          VolSlope20 = pos.VolSlope20
          RvolCum = (if expectedCum > 0.0 && not (Double.IsNaN expectedCum) then float pos.CumVolAtEntry / expectedCum else nan)
          CumVolToEntry = pos.CumVolAtEntry
          EmaAtEntry = pos.EmaAtEntry
          VwapAtEntry = pos.VwapAtEntry
          SessEmaLowAtEntry = pos.SessEmaLowAtEntry
          BarsSinceEmaHigh = pos.BarsSinceEmaHigh
          BarsSinceEmaLow = pos.BarsSinceEmaLow
          EntryVsVwap = (if pos.VwapAtEntry > 0.0 && not (Double.IsNaN pos.VwapAtEntry) then pos.EntryPx / pos.VwapAtEntry - 1.0 else nan)
          Close1d = c.PrevAdjClose
          Close3d = c.Close3d
          Chg1d = (if c.PrevAdjClose > 0.0 then pos.EntryPx / c.PrevAdjClose - 1.0 else nan)
          Chg3d = (if c.Close3d > 0.0 then pos.EntryPx / c.Close3d - 1.0 else nan)
          PctChgSinceOpen = (if c.DayOpen > 0.0 then pos.EntryPx / c.DayOpen - 1.0 else nan)
          ExitMin = exitMin
          ExitPrice = exitPx
          ExitReason = reason
          RetMoc = (if pos.EntryPx > 0.0 then exitPx / pos.EntryPx - 1.0 else nan)   // long-only
          DayClose = c.DayClose
          CloseFwd1d = c.CloseFwd1d
          CloseFwd3d = c.CloseFwd3d
          CloseFwd5d = c.CloseFwd5d
          Qty = qty
          NetPnL = qty * (exitPx - pos.EntryPx)   // long-only
          BarsHeld = exitMin - pos.EntryMin }
    | Holding -> failwith "toTrip called on a still-Holding position (Flatten first)"

// ===========================================================================
// Pipeline 1 — read qualifying (ticker, day) rows from vwap_reclaim_candidate:
// mr_candidate PRE-PRUNED to the in-play universe (ADV = avgvol20*day_close >= $1M AND
// rvol_0945 > 1), built by scripts/equity/build_vwap_reclaim_candidate.fsx. Folding these
// two Layer-1 filters in up front (was post-hoc on the trips CSV) streams ~5x fewer
// ticker-days (161,979 / 850,107 = 19% of mr_candidate).
// ===========================================================================
let private readCandidates (conn: DuckDBConnection) (startDate: DateOnly) (endDate: DateOnly) : Candidate[] =
    // Research override: VWR_CANDIDATE_TABLE lets a breakdown run against a WIDER universe (e.g. a
    // table with the $100M ADV floor dropped) without disturbing the production `vwap_reclaim_candidate`.
    // Value is validated against a fixed allow-list (identifier-only) to keep it injection-safe.
    let table =
        match Environment.GetEnvironmentVariable "VWR_CANDIDATE_TABLE" with
        | null | "" -> "vwap_reclaim_candidate"
        | t when t |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_') -> t
        | bad -> failwithf "Invalid VWR_CANDIDATE_TABLE %A (identifier chars only)" bad
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        $"SELECT ticker, date, prev_adj_close, close_3d, day_close, adj_ratio,
                close_fwd_1d, close_fwd_3d, close_fwd_5d, day_open, avgvol20
         FROM {table}
         WHERE date >= $start AND date <= $end
         ORDER BY ticker, date"
    let pStart = cmd.CreateParameter() in pStart.ParameterName <- "start"; pStart.Value <- startDate; cmd.Parameters.Add pStart |> ignore
    let pEnd   = cmd.CreateParameter() in pEnd.ParameterName   <- "end";   pEnd.Value   <- endDate;   cmd.Parameters.Add pEnd   |> ignore
    let out = ResizeArray<Candidate>()
    use reader = cmd.ExecuteReader()
    // helper: a nullable DOUBLE column reads as nan when NULL (e.g. close_fwd_* at the tail).
    let dbl (i: int) = if reader.IsDBNull i then nan else reader.GetDouble i
    while reader.Read() do
        out.Add(
            { Ticker = reader.GetString 0
              Date   = DateOnly.FromDateTime(reader.GetDateTime 1)
              PrevAdjClose = dbl 2
              Close3d = dbl 3
              DayClose = dbl 4
              AdjRatio = dbl 5
              CloseFwd1d = dbl 6
              CloseFwd3d = dbl 7
              CloseFwd5d = dbl 8
              DayOpen = dbl 9
              AvgVol20 = dbl 10 })
    out.ToArray()

// ===========================================================================
// Pipeline 2 (intraday) — MinuteEmitter -> IntradaySystem -> trip array.
// Copied from MaxFlyer: one MinuteEmitter per date (parquet opened once), one
// IntradaySystem per ticker, drain-on-boundary + Flatten. Only the candidate
// source and toTrip differ.
// ===========================================================================
type MinuteEmitter
        ( conn: DuckDBConnection, path: string,
          tickers: string[], adjRatio: IDictionary<string, float>,
          sessionStartMin: int, mocMin: int ) =

    member val Conn = conn
    member val AdjRatio = adjRatio

    member val Sql =
        let tickerList = tickers |> Array.map (fun t -> "'" + t.Replace("'", "''") + "'") |> String.concat ","
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
            (path.Replace("'", "''")) tickerList sessionStartMin mocMin

    /// Stream every candidate-ticker minute bar for this date, split-adjusted, in
    /// (ticker, et_min) order. `inline` so onNext fuses into the read loop.
    member inline this.Process(onNext: string * MinuteBar -> unit) =
        use cmd = this.Conn.CreateCommand()
        cmd.CommandText <- this.Sql
        use reader = cmd.ExecuteReader()
        while reader.Read() do
            let ticker = reader.GetString 0
            let r = this.AdjRatio.[ticker]
            let bar : MinuteBar =
                { etMin    = reader.GetInt32 1
                  ``open`` = reader.GetDouble 2 * r
                  high     = reader.GetDouble 3 * r
                  low      = reader.GetDouble 4 * r
                  close    = reader.GetDouble 5 * r
                  volume   = reader.GetInt64 6 }
            onNext (ticker, bar)

let collectTrips (conn: DuckDBConnection) (cfg: Config) (minuteDir: string)
                 (candidates: Candidate[]) : Trip[] =
    let trips = ResizeArray<Trip>()

    let drain (c: Candidate) (sys: IntradaySystem) =
        sys.Flatten()
        for pos in sys.Positions do
            match pos.State with
            | ExitedAt _ -> trips.Add(toTrip c cfg.Notional pos)   // long-only; vol-failed setups never opened a position
            | Holding -> failwith "Flatten closes all; unreachable"

    for date, cands in candidates |> Array.groupBy (fun c -> c.Date) do
        let path = IO.Path.Combine(minuteDir, sprintf "%s.parquet" (date.ToString("yyyy-MM-dd")))
        if IO.File.Exists path then
            let byTicker = cands |> Array.map (fun c -> c.Ticker, c) |> dict
            let adjRatio = cands |> Array.map (fun c -> c.Ticker, c.AdjRatio) |> dict
            let emitter = MinuteEmitter(conn, path, Array.map (fun (c: Candidate) -> c.Ticker) cands,
                                        adjRatio, cfg.Intraday.SessionStartMin, cfg.Intraday.MocMin)
            let mutable cur : (Candidate * IntradaySystem) option = None
            emitter.Process(fun (ticker, bar) ->
                match cur with
                | Some(c, sys) when c.Ticker = ticker -> sys.Process bar
                | _ ->
                    match cur with
                    | Some(pc, psys) -> drain pc psys
                    | None -> ()
                    let c = byTicker.[ticker]
                    // 20d per-minute volume pace (avgvol20/390) — the exhaustion-cut denominator. 0 = off.
                    let permin20d = if c.AvgVol20 > 0.0 then c.AvgVol20 / 390.0 else 0.0
                    let sys = IntradaySystem(cfg.Intraday, ticker, date, c.PrevAdjClose, c.Close3d, permin20d)
                    sys.Process bar
                    cur <- Some(c, sys))
            match cur with
            | Some(c, sys) -> drain c sys
            | None -> ()

    trips.ToArray()

/// Run the whole LowFlyer backtest: read mr_candidate (pipeline 1), then the
/// intraday breakout engine per candidate day (pipeline 2, grouped by date so
/// each minute_aggs parquet opens at most once). Returns (trips, candidateCount).
let run (dbPath: string) (minuteDir: string) (cfg: Config)
        (startDate: DateOnly) (endDate: DateOnly) : Trip[] * int =
    let connStr = $"Data Source={dbPath};ACCESS_MODE=READ_ONLY"
    use conn = new DuckDBConnection(connStr)
    conn.Open()
    do
        use pragma = conn.CreateCommand()
        pragma.CommandText <- "PRAGMA memory_limit='6GB'"
        pragma.ExecuteNonQuery() |> ignore

    let candidates = readCandidates conn startDate endDate
    let trips = collectTrips conn cfg minuteDir candidates
    trips, candidates.Length

// ---------------------------------------------------------------------------
// CSV emission
// ---------------------------------------------------------------------------

let private inv = CultureInfo.InvariantCulture
let private fmt (x: float) = if Double.IsNaN x then "nan" else x.ToString("0.################", inv)
let private hhmm (m: int) = sprintf "%02d:%02d" (m / 60) (m % 60)

let header =
    "symbol,trade_date,prev_adj_close,adj_ratio,"
    + "entry_time,entry_price,stop_dist_pct,"
    + "log_atr_20,price_slope_20,vol_slope_20,rvol_cum,cum_vol_to_entry,ema_at_entry,vwap_at_entry,sess_ema_low_at_entry,bars_since_ema_high,bars_since_ema_low,entry_vs_vwap,"
    + "close_1d,close_3d,chg_1d,chg_3d,pct_chg_since_open,"
    + "exit_time,exit_price,exit_reason,ret_moc,"
    + "day_close,close_fwd_1d,close_fwd_3d,close_fwd_5d,"
    + "qty,net_pnl,bars_held_min"

let private row (t: Trip) : string =
    String.concat "," [
        t.Symbol
        t.TradeDate.ToString("yyyy-MM-dd")
        fmt t.PrevAdjClose
        fmt t.AdjRatio
        hhmm t.EntryMin
        fmt t.EntryPrice
        fmt t.StopDistPct
        fmt t.LogAtr20
        fmt t.PriceSlope20
        fmt t.VolSlope20
        fmt t.RvolCum
        string t.CumVolToEntry
        fmt t.EmaAtEntry
        fmt t.VwapAtEntry
        fmt t.SessEmaLowAtEntry
        string t.BarsSinceEmaHigh
        string t.BarsSinceEmaLow
        fmt t.EntryVsVwap
        fmt t.Close1d
        fmt t.Close3d
        fmt t.Chg1d
        fmt t.Chg3d
        fmt t.PctChgSinceOpen
        hhmm t.ExitMin
        fmt t.ExitPrice
        t.ExitReason
        fmt t.RetMoc
        fmt t.DayClose
        fmt t.CloseFwd1d
        fmt t.CloseFwd3d
        fmt t.CloseFwd5d
        fmt t.Qty
        fmt t.NetPnL
        string t.BarsHeld
    ]

let writeCsv (path: string) (trips: Trip[]) =
    use w = new IO.StreamWriter(path)
    w.WriteLine header
    for t in trips do w.WriteLine(row t)
