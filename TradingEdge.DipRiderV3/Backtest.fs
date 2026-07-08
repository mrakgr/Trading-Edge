module TradingEdge.DipRiderV3.Backtest

open System
open System.Globalization
open System.Collections.Generic
open DuckDB.NET.Data
open TradingEdge.DipRiderV3.Intraday

// ===========================================================================
// LowFlyer backtest wiring.
//
// There is NO daily selection engine. Pipeline 1 is a pure-SQL read of the
// `mr_candidate` table (built by scripts/equity/build_mr_candidate.fsx): every
// (ticker, day) that clears the market-wide preconditions — median 09:30-09:45
// 1m-bar vol >= 10k AND >= 10 bars, CS/ADRC, price >= $1 — carrying the daily
// context (prev/3d close, day close, adj ratio, 20-bar avg vol) and the forward
// returns (D+1/3/5 close, reported for post-hoc slicing). Pipeline 2 streams
// each candidate day's minute bars into the reused IntradaySystem (Intraday.fs)
// driven downside/long to buy the high-volume flush to a new session low, held
// to MOC. Same two-pipeline shape as MaxFlyer, minus the daily engine.
// ===========================================================================

/// LowFlyer config = the intraday engine knobs + notional. No daily gates.
type Config =
    { Intraday: IntradayConfig
      Notional: float }

/// DipRiderV3 default: pure trailing-window momentum, long-only, hold-to-MOC.
/// Two-anchor timing (session extremes from 08:30, all other features from
/// 09:30), morning window 10:00-13:30, one concurrent position, geometry stop
/// off the 20m-window low. Only the three core gates are ON at the baseline
/// (vol-slope >= 0.05, price-slope > 0, tightness >= 3); the tunable/cap gates
/// (SumAbove6, slope/ATR, session-max-log-ATR, the long-window caps) start OFF
/// and are enabled after a breakdown.
let defaultConfig =
    { Intraday =
        { VolWindow = 20
          EmaPeriod = 9                  // the 9-EMA (closes-above-EMA reference)
          SessionStartMin = 8 * 60 + 30  // 08:30 ET — session min/max close + session max volume anchor here.
          FeatureStartMin = 9 * 60 + 30  // 09:30 ET (570) — VWAP, both OLS slopes, ATR, tightness, EMA, the
                                         // SumMa above-EMA counts, and 15m-init-vol all fold ONLY from here.
          EntryStartMin   = 10 * 60      // 10:00 ET — morning-window START (09:30-10:00 warms the windows).
          EntryEndMin     = 13 * 60 + 30 // 13:30 ET — morning-window END.
          MocMin          = 16 * 60      // 16:00 ET
          MaxConcurrent   = 1            // ONE position per (ticker,day). --max-concurrent 0 = unlimited.
          // ----- entry gates -----
          MinVolSlope    = 0.05          // 20m OLS log-volume slope >= 0.05 (V2 F14/F15 — rising volume is the PF lever).
          MinPriceSlope  = 0.0           // 20m OLS log-price slope > 0. Sweep for a higher floor.
          MinTightness   = 3.0           // tightness >= 3 (real range, not lethargic; VwapReclaim Finding 6).
          MaxTightness   = infinity      // OFF
          MinAtrPct      = 0.013         // 20m log-ATR >= 0.013 — THE MAIN LEVER (F3: PF scales monotonically
                                         // with ATR; sub-0.013 is flat/dead ~PF 1.07). 0 = off.
          MaxAtrPct      = infinity      // OFF
          MinCloseAbove6 = 5             // F6: require >= 5 of the last 6 bars closed above the 9-EMA. A real,
                                         // clip-robust entry-quality gate (sum6<=4 weak/dead; 5-6 = clipped PF
                                         // 1.32/1.21, 84% of the book). Measures how SUSTAINED the push is
                                         // (orthogonal to ATR's SIZE). 0 = off.
          MinSlopePerAtr = Double.NegativeInfinity  // slope/log-ATR floor OFF (gated after breakdown).
          MinSessMaxLogAtr = 0.0         // session-max-log-ATR floor OFF (gated after breakdown).
          MaxSumAbove40  = 0             // trend-too-long cap OFF (tune after breakdown).
          MaxSumAbove60  = 0
          // ----- stop / exits -----
          GeomStop        = true         // geometry stop: d = entry - stopFloor; stop = entry - d*StopDistFrac.
          StopFloorSessMin = true         // stop floor = the SESSION MIN CLOSE (from 08:30) — F2: the wider floor
                                         // lifts win-rate 24.5%->36.1% & PF 1.244->1.253 vs the 20m-min-close
                                         // (the tight 20m floor chopped winners). false = 20m min close.
          StopDistFrac    = 2.0 / 3.0    // stop distance = d*2/3 (VwapReclaim F14 / V2 F25).
          StopOnClose     = true         // stop on a CLOSE at/below the level (ignore noise wicks).
          PctStop         = 0.0          // catastrophe %-stop OFF.
          TimeStopMin     = 0            // HOLD-TO-MOC (the continuation lesson: any exit caps the winners).
          ExhaustExit     = false        // exhaustion exit OFF (recorded lesson; --exhaust-exit enables).
          ExhaustVolMult  = 10.0
          VwapExitBars    = 0 }          // loss-of-VWAP exit OFF.
      Notional = 10_000.0 }

/// One candidate (ticker, day) from mr_candidate, with the daily context the
/// engine + the post-hoc feature slicing need. Forward closes are REPORTED only.
type Candidate =
    { Ticker: string
      Date: DateOnly
      PrevAdjClose: float       // D-1 adj close (= close_1d), the engine's prevClose
      Close3d: float            // D-3 adj close
      Close7d: float            // D-7 adj close (trailing 7-day return feature)
      DayClose: float           // D adj close (price floor + fwd-return base)
      AdjRatio: float           // adj_close / raw_close (rescale minute bars)
      AvgVol20: float           // 20-bar trailing mean daily volume (rvol denom)
      CloseFwd1d: float
      CloseFwd3d: float
      CloseFwd5d: float
      DayOpen: float            // first 09:30 RTH bar's open (== engine session open)
      MedBarVol0945: int64
      NBar0945: int
      Vol0945: int64 }

/// A finished trip. MaxFlyer's gap/premarket/daily-quality fields are gone;
/// the mean-reversion features + forward returns are added.
type Trip =
    { Symbol: string
      TradeDate: DateOnly
      PrevAdjClose: float
      AdjRatio: float
      // intraday at entry
      EntryMin: int
      EntryPrice: float          // = the entry bar's close (the fill)
      StopDistPct: float         // stop distance as a fraction of entry = (entry - stopLevel)/entry; nan if stopless
      // ----- DipRiderV3 trailing-window momentum features (all strictly-prior; nan/-1 = not-warm) -----
      PriceSlope20: float        // 20m OLS slope of log(close) — trend strength (%/bar). The core price gate.
      VolSlope20: float          // 20m OLS slope of log(volume) — is volume rising into the move (the F14 lever)
      LogAtr20: float            // 20m mean log-true-range — the volatility feature (high-ATR trades momentum better)
      Tightness20: float         // (rangeHigh-rangeLow)/atrLin over the window — real range vs lethargic
      SlopePerAtr: float         // PriceSlope20 / LogAtr20 (trend per unit volatility) — recorded, gated after breakdown
      SumAbove6: int             // # of the last 6 bars that closed >= the 9-EMA (the short push count; SumMa)
      SumAbove40: int            // # of the last 40 closed above the EMA (trend-too-long cap input)
      SumAbove60: int            // # of the last 60 closed above the EMA
      SessMaxLogAtr: float       // session-cumulative MAX of the 20m log-ATR (past volatility explosions)
      SessMinClose: float        // session MIN close (from 08:30) — the geometry-stop floor candidate / context
      SessMaxClose: float        // session MAX close (from 08:30)
      SessMaxVol: int64          // session MAX single-bar volume (from 08:30)
      VwapAtEntry: float         // session VWAP at entry (from 09:30)
      EntryVsVwap: float         // entryPx / VWAP - 1 (>0 = bought ABOVE VWAP, <0 = below)
      InitVol15m: int64          // Σ volume over the first 15 VALID feature-bars (the name's early tempo)
      EntryVsSessHigh: float     // entry / running session high - 1 (<=0; how far below the session high we bought)
      Chg20m: float              // 20-minute %-change into entry (engine LagMa(20), no post-hoc rescan)
      Rvol: float                // cumVolToEntry / avgVol20 (recorded feature)
      MktChgOpen: float          // SPY %-change from session open at entry (broader-market regime)
      MktChgPrev: float          // SPY %-change from prev daily close at entry
      CumVolToEntry: int64       // cumulative day volume through the entry bar
      PctChgSinceOpen: float     // entryPx / dayOpen - 1
      Close1d: float             // close-1-day-ago (adj) = PrevAdjClose
      Close3d: float             // close-3-days-ago (adj)
      Close7d: float             // close-7-days-ago (adj)
      Chg1d: float               // entryPx / close_1d - 1 — day-scale flush DEPTH at entry (selection filter)
      Chg3d: float               // entryPx / close_3d - 1 — 3-day trend (>= -8% = not a multi-day decliner)
      Chg7d: float               // entryPx / close_7d - 1 — 7-day trend into entry (the run-up feature)
      // exit
      ExitMin: int
      ExitPrice: float           // MOC close (adj scale)
      ExitReason: string         // "moc"
      RetMoc: float              // exitPx / entryPx - 1 (intraday held return)
      // forward daily returns (base = D's daily close; recomputed in analysis)
      DayClose: float
      CloseFwd1d: float
      CloseFwd3d: float
      CloseFwd5d: float
      MedBarVol0945: int64
      Qty: float
      NetPnL: float
      BarsHeld: int }

let private toTrip (c: Candidate) (notional: float) (short: bool) (pos: IntradayPosition) : Trip =
    match pos.State with
    | ExitedAt (exitMin, exitPx, reason) ->
        let qty = notional / pos.EntryPx
        { Symbol = c.Ticker
          TradeDate = c.Date
          PrevAdjClose = c.PrevAdjClose
          AdjRatio = c.AdjRatio
          EntryMin = pos.EntryMin
          EntryPrice = pos.EntryPx
          StopDistPct = pos.StopDistPct
          PriceSlope20 = pos.PriceSlope20AtEntry
          VolSlope20 = pos.VolSlope20AtEntry
          LogAtr20 = pos.LogAtr20AtEntry
          Tightness20 = pos.Tightness20AtEntry
          SlopePerAtr = pos.SlopePerAtrAtEntry
          SumAbove6 = pos.SumAbove6AtEntry
          SumAbove40 = pos.SumAbove40AtEntry
          SumAbove60 = pos.SumAbove60AtEntry
          SessMaxLogAtr = pos.SessMaxLogAtrAtEntry
          SessMinClose = pos.SessMinCloseAtEntry
          SessMaxClose = pos.SessMaxCloseAtEntry
          SessMaxVol = pos.SessMaxVolAtEntry
          VwapAtEntry = pos.VwapAtEntry
          EntryVsVwap = (if pos.VwapAtEntry > 0.0 && not (Double.IsNaN pos.VwapAtEntry) then pos.EntryPx / pos.VwapAtEntry - 1.0 else nan)
          InitVol15m = pos.InitVol15mAtEntry
          EntryVsSessHigh = pos.EntryVsSessHighAtEntry
          Chg20m = pos.Chg20mAtEntry
          Rvol = (if c.AvgVol20 > 0.0 then float pos.CumVolAtEntry / c.AvgVol20 else nan)
          MktChgOpen = pos.MktChgOpenAtEntry
          MktChgPrev = pos.MktChgPrevAtEntry
          CumVolToEntry = pos.CumVolAtEntry
          PctChgSinceOpen = (if c.DayOpen > 0.0 then pos.EntryPx / c.DayOpen - 1.0 else nan)
          Close1d = c.PrevAdjClose
          Close3d = c.Close3d
          Close7d = c.Close7d
          Chg1d = (if c.PrevAdjClose > 0.0 then pos.EntryPx / c.PrevAdjClose - 1.0 else nan)
          Chg3d = (if c.Close3d > 0.0 then pos.EntryPx / c.Close3d - 1.0 else nan)
          Chg7d = (if c.Close7d > 0.0 then pos.EntryPx / c.Close7d - 1.0 else nan)
          ExitMin = exitMin
          ExitPrice = exitPx
          ExitReason = reason
          // long: gain when price rises (exit/entry-1); short: gain when price falls (entry/exit-1).
          RetMoc = (if pos.EntryPx > 0.0 then (if short then pos.EntryPx / exitPx - 1.0 else exitPx / pos.EntryPx - 1.0) else nan)
          DayClose = c.DayClose
          CloseFwd1d = c.CloseFwd1d
          CloseFwd3d = c.CloseFwd3d
          CloseFwd5d = c.CloseFwd5d
          MedBarVol0945 = c.MedBarVol0945
          Qty = qty
          // long: profit when price rises (exit - entry); short: profit when price falls.
          NetPnL = (if short then qty * (pos.EntryPx - exitPx) else qty * (exitPx - pos.EntryPx))
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
        $"SELECT ticker, date, prev_adj_close, close_3d, close_7d, day_close, adj_ratio, avgvol20,
                close_fwd_1d, close_fwd_3d, close_fwd_5d, day_open, med_bar_vol_0945, nbar_0945, vol_0945
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
              Close7d = dbl 4
              DayClose = dbl 5
              AdjRatio = dbl 6
              AvgVol20 = dbl 7
              CloseFwd1d = dbl 8
              CloseFwd3d = dbl 9
              CloseFwd5d = dbl 10
              DayOpen = dbl 11
              MedBarVol0945 = reader.GetInt64 12
              NBar0945 = reader.GetInt32 13
              Vol0945 = reader.GetInt64 14 })
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

/// Build the per-date MARKET-CONTEXT lookup (broader-market regime, shared by all tickers). Reads the
/// reference index's (SPY) 1m RTH bars from the same date parquet, plus its prev daily close, and returns
/// et_min -> struct(chg-from-open, chg-from-prev-close) as CUMULATIVE %-changes at that minute. No-lookahead
/// by construction (the value at et_min uses only SPY bars through et_min). Returns the (nan,nan) no-op if
/// SPY is missing that day. `idx` defaults to "SPY".
let private buildMarketCtx (conn: DuckDBConnection) (path: string) (date: DateOnly)
                           (mocMin: int) : int -> struct (float * float) =
    let idx = "SPY"
    // prev daily close from the RAW daily table (avoid the split_adjusted dividend bug). ValueNone if absent.
    let prevClose =
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT close FROM daily_prices WHERE ticker = $t AND date < $d ORDER BY date DESC LIMIT 1"
        let pt = cmd.CreateParameter() in pt.ParameterName <- "t"; pt.Value <- idx; cmd.Parameters.Add pt |> ignore
        let pd = cmd.CreateParameter() in pd.ParameterName <- "d"; pd.Value <- date; cmd.Parameters.Add pd |> ignore
        match cmd.ExecuteScalar() with
        | null -> ValueNone
        | :? System.DBNull -> ValueNone
        | v -> ValueSome (System.Convert.ToDouble v)
    // SPY 1m RTH closes, in et_min order, for this date.
    let bars =
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            sprintf """
            WITH b AS (
                SELECT CAST(date_part('hour', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT)*60
                     + CAST(date_part('minute', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT) AS et_min,
                     open, close
                FROM read_parquet('%s') WHERE ticker = 'SPY' AND close > 0)
            SELECT et_min, open, close FROM b WHERE et_min >= 570 AND et_min <= %d ORDER BY et_min"""
                (path.Replace("'", "''")) mocMin
        use reader = cmd.ExecuteReader()
        let acc = ResizeArray<struct (int * float * float)>()
        while reader.Read() do acc.Add(struct (reader.GetInt32 0, reader.GetDouble 1, reader.GetDouble 2))
        acc
    if bars.Count = 0 then (fun _ -> struct (nan, nan))
    else
        // session open = the first RTH (>=09:30) bar's OPEN.
        let struct (_, sessOpen, _) = bars.[0]
        // map et_min -> cumulative (chgOpen, chgPrev) using SPY's close at/through that minute. We fill a
        // dense map by carrying the last-seen close forward across gaps (a minute with no SPY bar reads the
        // prior minute's value).
        let m = System.Collections.Generic.Dictionary<int, struct (float * float)>()
        let mutable lastClose = sessOpen   // forward-filled across minutes with no SPY bar
        let mutable bi = 0
        for etMin in 570 .. mocMin do
            while bi < bars.Count && (let struct (e,_,_) = bars.[bi] in e) <= etMin do
                let struct (_,_,c) = bars.[bi] in lastClose <- c
                bi <- bi + 1
            let chgOpen = if sessOpen > 0.0 then lastClose / sessOpen - 1.0 else nan
            let chgPrev = match prevClose with ValueSome pc when pc > 0.0 -> lastClose / pc - 1.0 | _ -> nan
            m.[etMin] <- struct (chgOpen, chgPrev)
        (fun etMin -> match m.TryGetValue etMin with | true, v -> v | _ -> struct (nan, nan))

let collectTrips (conn: DuckDBConnection) (cfg: Config) (minuteDir: string)
                 (candidates: Candidate[]) : Trip[] =
    let trips = ResizeArray<Trip>()

    let drain (c: Candidate) (sys: IntradaySystem) =
        sys.Flatten()
        for pos in sys.Positions do
            match pos.State with
            | ExitedAt _ -> trips.Add(toTrip c cfg.Notional false pos)   // V3 is long-only
            | Holding -> failwith "Flatten closes all; unreachable"

    for date, cands in candidates |> Array.groupBy (fun c -> c.Date) do
        let path = IO.Path.Combine(minuteDir, sprintf "%s.parquet" (date.ToString("yyyy-MM-dd")))
        if IO.File.Exists path then
            let byTicker = cands |> Array.map (fun c -> c.Ticker, c) |> dict
            let adjRatio = cands |> Array.map (fun c -> c.Ticker, c.AdjRatio) |> dict
            let emitter = MinuteEmitter(conn, path, Array.map (fun (c: Candidate) -> c.Ticker) cands,
                                        adjRatio, cfg.Intraday.SessionStartMin, cfg.Intraday.MocMin)
            // broader-market (SPY) context for this date — built once, shared by every ticker's engine.
            let mktCtx = buildMarketCtx conn path date cfg.Intraday.MocMin
            let mutable cur : (Candidate * IntradaySystem) option = None
            emitter.Process(fun (ticker, bar) ->
                match cur with
                | Some(c, sys) when c.Ticker = ticker -> sys.Process bar
                | _ ->
                    match cur with
                    | Some(pc, psys) -> drain pc psys
                    | None -> ()
                    let c = byTicker.[ticker]
                    let sys = IntradaySystem(cfg.Intraday, ticker, date, c.PrevAdjClose)
                    sys.SetMarketCtx mktCtx
                    // per-minute volume baselines for the exhaustion exit: 20d-avg/390 and opening-15m avg 1m vol.
                    let perMin20d = if c.AvgVol20 > 0.0 then c.AvgVol20 / 390.0 else 0.0
                    let perMin15m = if c.NBar0945 > 0 then float c.Vol0945 / float c.NBar0945 else 0.0
                    sys.SetVolBaselines(perMin20d, perMin15m)
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
    + "price_slope_20,vol_slope_20,log_atr_20,tightness_20,slope_per_atr,sum_above_6,sum_above_40,sum_above_60,"
    + "sess_max_log_atr,sess_min_close,sess_max_close,sess_max_vol,vwap_at_entry,entry_vs_vwap,init_vol_15m,"
    + "entry_vs_sess_high,chg_20m,rvol,mkt_chg_open,mkt_chg_prev,cum_vol_to_entry,pct_chg_since_open,"
    + "close_1d,close_3d,close_7d,chg_1d,chg_3d,chg_7d,"
    + "exit_time,exit_price,exit_reason,ret_moc,"
    + "day_close,close_fwd_1d,close_fwd_3d,close_fwd_5d,med_bar_vol_0945,"
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
        fmt t.PriceSlope20
        fmt t.VolSlope20
        fmt t.LogAtr20
        fmt t.Tightness20
        fmt t.SlopePerAtr
        string t.SumAbove6
        string t.SumAbove40
        string t.SumAbove60
        fmt t.SessMaxLogAtr
        fmt t.SessMinClose
        fmt t.SessMaxClose
        string t.SessMaxVol
        fmt t.VwapAtEntry
        fmt t.EntryVsVwap
        string t.InitVol15m
        fmt t.EntryVsSessHigh
        fmt t.Chg20m
        fmt t.Rvol
        fmt t.MktChgOpen
        fmt t.MktChgPrev
        string t.CumVolToEntry
        fmt t.PctChgSinceOpen
        fmt t.Close1d
        fmt t.Close3d
        fmt t.Close7d
        fmt t.Chg1d
        fmt t.Chg3d
        fmt t.Chg7d
        hhmm t.ExitMin
        fmt t.ExitPrice
        t.ExitReason
        fmt t.RetMoc
        fmt t.DayClose
        fmt t.CloseFwd1d
        fmt t.CloseFwd3d
        fmt t.CloseFwd5d
        string t.MedBarVol0945
        fmt t.Qty
        fmt t.NetPnL
        string t.BarsHeld
    ]

let writeCsv (path: string) (trips: Trip[]) =
    use w = new IO.StreamWriter(path)
    w.WriteLine header
    for t in trips do w.WriteLine(row t)
