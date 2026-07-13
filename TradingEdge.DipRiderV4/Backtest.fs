module TradingEdge.DipRiderV4.Backtest

open System
open System.Globalization
open System.Collections.Generic
open DuckDB.NET.Data
open TradingEdge.DipRiderV4.Intraday

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
        { ReArm = RollingEmaLow         // re-arm reference: RollingEmaLow | SessionEmaLow | LastStopLevel.
          MaxConcurrent = 0             // 0 = unlimited (the pure arm/re-arm book). 1 = V3Backside slot-lifetime.
          VolAsGate = false             // false = SKIP mode (vol decides real-vs-skip); true = GATE mode (vol ANDed in).
          VolWindow = 20
          EmaPeriod = 9                  // the 9-EMA (closes-above-EMA reference)
          SessionStartMin = 8 * 60 + 30  // 08:30 ET — the session anchor (windows warm from here).
          FeatureStartMin = 9 * 60 + 30  // 09:30 ET (570) — VWAP, OLS slope, ATR, EMA, sum6 fold ONLY from here.
          EntryStartMin   = 10 * 60      // 10:00 ET — entry-window START (09:30-10:00 warms the windows).
          EntryEndMin     = 13 * 60 + 30 // 13:30 ET — entry-window END.
          MocMin          = 16 * 60      // 16:00 ET
          // ----- entry gates -----
          MinVolClimb    = 0.5           // ⭐ BACKSIDE VOLUME CHECK (F32): at each ARMED price pattern, require
                                         // vol_climb >= 0.5 to TAKE; else SKIP + DISARM until the next 9-EMA low
                                         // re-arms. Reproduces F32's post-hoc vol_climb>=0.5 filter LIVE. 0 = always take.
          MinAtrPct      = 0.013         // 20m log-ATR >= 0.013 — THE MAIN LEVER (F3: PF scales monotonically
                                         // with ATR; sub-0.013 is flat/dead ~PF 1.07). 0 = off.
          MaxAtrPct      = infinity      // OFF
          MinCloseAbove6 = 5             // F6: require >= 5 of the last 6 bars closed above the 9-EMA (a sustained
                                         // push; sum6<=4 weak/dead). 0 = off.
          MinChg1d       = 0.10          // F17: day-direction floor — require the stock >= +10% on the day (entry vs
                                         // prev close). An ESSENTIAL entry requirement (user). -inf = off.
          MinChg3d       = 0.0           // F28: 3-day trend floor — require the stock UP over 3 days (entry >= close3d).
                                         // A 3-day decliner is a poor momentum buy in both regimes. -inf = off.
          MinEmaVsVwap   = -0.02         // F27: 9-EMA-vs-VWAP floor — reject if the 9-EMA is >2% below VWAP
                                         // (smoothed trend location; knee at −2%). -inf = off.
          MinTightness   = 0.0           // OFF (ablation: tight≥3 removed only 26/814 trips, PF 2.804→2.800, win
                                         // identical — REDUNDANT with the log-ATR floor, as the V3 notes said). --min-tightness 3 restores.
          MaxRvol5m20d   = 100.0         // F11 exhaustion cut: trailing-5m vol numerator < 100× the 20d per-min
                                         // pace (V3Backside; docs: cuts ~half the book). 0 = off.
          Rvol5mUseMax   = false         // false = 5m AVG numerator (V3Backside). true = 5m MAX (the short book's
                                         // spiky 1m-vol signal). --rvol-use-max enables.
          MaxBarsSinceBreakout = 0       // breakout gate OFF (BreakoutTimer used 10). --max-bars-since-breakout N.
          MaxBarsSince20mBreakout = 0    // 20m-EMA-breakout gate OFF. --max-bars-since-20m-breakout N (sweep [1,10]).
          MaxBarsSince60mBreakout = 0    // 60m-EMA-breakout gate OFF. --max-bars-since-60m-breakout N.
          DisablePriceSlope = false      // --no-price-slope drops the price-slope>0 gate (BreakoutTimer didn't use it).
          DisableSum6 = false }          // --no-sum6 drops the sum6 gate (BreakoutTimer didn't use it).
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

/// A finished trip — debloated to what the trimmed engine records plus cheap
/// candidate context. Core entry features (the tuned levers) come from the
/// position snapshot; day-scale returns + forward closes come from the candidate.
type Trip =
    { Symbol: string
      TradeDate: DateOnly
      PrevAdjClose: float
      AdjRatio: float
      // intraday at entry
      EntryMin: int
      EntryPrice: float          // = the entry bar's close (the fill)
      StopDistPct: float         // (entry - stopLevel)/entry; nan if no finite stop
      // ----- core entry-feature snapshot (this-bar-inclusive; the gates' levers) -----
      PriceSlope20: float        // 20m OLS slope of log(close)
      LogAtr20: float            // 20m mean log-true-range
      Tightness20: float         // (rangeHigh-rangeLow)/atrLin
      Rvol5m: float              // trailing-5m avg vol / permin20d (exhaustion-cut ratio)
      BarsSinceBreakout: int     // -1/0/+N — the session-high countdown at entry
      BarsSince20mBreakout: int  // -1/0/+N — the 20m-EMA-high countdown at entry
      BarsSince60mBreakout: int  // -1/0/+N — the 60m-EMA-high countdown at entry
      SessEmaHigh: float         // session-max 9-EMA at entry
      LaggedSessEmaHigh10m: float // session-EMA-high 10m ago (post-hoc)
      VolClimb: float            // (volEma - volEmaMin)/volEma — the F32 volume lever
      SumAbove6: int             // # of the last 6 bars that closed >= the 9-EMA
      EmaAtEntry: float          // the 9-EMA at entry
      VwapAtEntry: float         // session VWAP at entry
      EntryVsVwap: float         // entryPx / VWAP - 1
      // ----- day-scale context / selection features (from the candidate) -----
      Close1d: float             // close-1-day-ago (adj) = PrevAdjClose
      Close3d: float             // close-3-days-ago (adj)
      Chg1d: float               // entryPx / close_1d - 1 — day-direction floor input
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
        { Symbol = c.Ticker
          TradeDate = c.Date
          PrevAdjClose = c.PrevAdjClose
          AdjRatio = c.AdjRatio
          EntryMin = pos.EntryMin
          EntryPrice = pos.EntryPx
          StopDistPct = pos.StopDistPct
          PriceSlope20 = pos.PriceSlope20
          LogAtr20 = pos.LogAtr20
          Tightness20 = pos.Tightness20
          Rvol5m = pos.Rvol5m
          BarsSinceBreakout = pos.BarsSinceBreakout
          BarsSince20mBreakout = pos.BarsSince20mBreakout
          BarsSince60mBreakout = pos.BarsSince60mBreakout
          SessEmaHigh = pos.SessEmaHigh
          LaggedSessEmaHigh10m = pos.LaggedSessEmaHigh10m
          VolClimb = pos.VolClimb
          SumAbove6 = pos.Sum6
          EmaAtEntry = pos.EmaAtEntry
          VwapAtEntry = pos.VwapAtEntry
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
    + "price_slope_20,log_atr_20,tightness_20,rvol_5m_20d,bars_since_breakout,bars_since_20m_breakout,bars_since_60m_breakout,sess_ema_high,lagged_sess_ema_high_10m,vol_climb,sum_above_6,ema_at_entry,vwap_at_entry,entry_vs_vwap,"
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
        fmt t.PriceSlope20
        fmt t.LogAtr20
        fmt t.Tightness20
        fmt t.Rvol5m
        string t.BarsSinceBreakout
        string t.BarsSince20mBreakout
        string t.BarsSince60mBreakout
        fmt t.SessEmaHigh
        fmt t.LaggedSessEmaHigh10m
        fmt t.VolClimb
        string t.SumAbove6
        fmt t.EmaAtEntry
        fmt t.VwapAtEntry
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
