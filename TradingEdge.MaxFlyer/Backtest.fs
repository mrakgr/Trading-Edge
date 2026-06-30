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
      // Gate 1 (daily selection) + Gate 2 (day-D premarket) — both live in DailyFilterConfig,
      // which MaxFlyer.Process owns end-to-end.
      Daily: DailyFilterConfig
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
          MinMaxAtrLog = 0.04
          // Gate 2 (premarket) — min 10% gap (a daytrading-grade move; may go higher
          // after research), uncapped on the high side, no absolute premkt-vol floor,
          // but require premarket to have traded >= 20% of a normal full day.
          MinGapPct = 0.10
          MaxGapPct = 2.0
          MinPremktVol = 0L
          MinPremktVolPctOfAvg = 0.20 }
      Intraday =
        { VolWindow = 20
          // Intraday tightness is a CORE filter (with the volume-confirmation gate);
          // start at 4.5 (the daily anchor) and sweep. ATR% gate stays off for now.
          MaxTightness = 4.5
          MaxAtrPct = infinity
          SessionStartMin = 8 * 60 + 30   // 08:30 ET — engine start (SMB 1h opening range)
          EntryStartMin   = 9 * 60 + 35   // 09:35 ET — earliest entry (wall-clock trading floor)
          UseStop = false
          TimeStopMin = 0                 // time-stop off by default
          Downside = false                // upside breakout (new session high) by default
          Short = false                   // long by default
          Target = NoTarget               // no mean-reversion target by default
          MocMin = 16 * 60                // 16:00 ET
          MaxConcurrent = 0 }             // unlimited concurrent breakouts
      Notional = 10_000.0 }

// ===========================================================================
// Pipeline stage 1 — DbEmitter: the ONLY database read on the daily side.
//
// Streams the daily universe (split_adjusted_prices + the day's raw close +
// premarket volume) ordered by (ticker, date) and pushes each row downstream as
// a (ticker, Bar) via `onNext`. It owns nothing but the read — no gates, no
// candidate logic, no per-ticker state. The consumer decides what to do at
// ticker boundaries (e.g. spin up a fresh MaxFlyer). Continuation-passing style
// isolates the DB from the rest of the pipeline.
// ===========================================================================
type DbEmitter(conn: DuckDBConnection, startDate: DateOnly, endDate: DateOnly) =

    // Public accessors for the ctor-captured state: `Process` is `inline`, so an
    // inlined body can only touch members accessible at the call site (not the
    // private constructor fields directly).
    member val Conn = conn
    member val StartDate = startDate
    member val EndDate = endDate

    // Universe = common stock + ADRs only. NOTE the SEMI-JOIN (EXISTS), not an inner
    // JOIN: a few tickers (e.g. ASND) have BOTH a 'CS' and an 'ADRC' row in
    // ticker_reference; an inner join would FAN OUT every price row into two identical
    // consecutive rows. EXISTS filters without multiplying.
    //
    // rth_open is NOT selected: bar.open (adj_open) already IS the day-D adjusted RTH
    // open (adj_open == premarket rth_open * adjRatio, verified), on the adjusted scale.
    // premkt_vol LEFT-JOINs in; a missing row (illiquid premarket) reads as 0 — no
    // premarket trades is a real value, not a missing one.
    // public (not private) because Process is `inline` — an inlined body can only
    // touch members accessible at the call site.
    static member val Sql =
        "SELECT p.ticker, p.date, p.adj_open, p.adj_high, p.adj_low, p.adj_close, p.adj_volume,
                d.close AS raw_close,
                COALESCE(pm.premkt_vol, 0) AS premkt_vol
         FROM split_adjusted_prices p
         JOIN daily_prices d ON d.ticker = p.ticker AND d.date = p.date
         LEFT JOIN premarket pm ON pm.ticker = p.ticker AND pm.date = p.date
         WHERE EXISTS (SELECT 1 FROM ticker_reference r
                       WHERE r.ticker = p.ticker AND r.type IN ('CS','ADRC'))
           AND p.date >= $start AND p.date <= $end
         ORDER BY p.ticker, p.date"

    /// Stream every daily row, pushing (ticker, bar) downstream in (ticker, date)
    /// order. `inline` so the onNext closure fuses into the read loop.
    member inline this.Process(onNext: string * Bar -> unit) =
        use cmd = this.Conn.CreateCommand()
        cmd.CommandText <- DbEmitter.Sql
        let pStart = cmd.CreateParameter() in pStart.ParameterName <- "start"; pStart.Value <- this.StartDate; cmd.Parameters.Add pStart |> ignore
        let pEnd   = cmd.CreateParameter() in pEnd.ParameterName   <- "end";   pEnd.Value   <- this.EndDate;   cmd.Parameters.Add pEnd   |> ignore
        use reader = cmd.ExecuteReader()
        while reader.Read() do
            let ticker = reader.GetString 0
            let bar : Bar =
                { date     = DateOnly.FromDateTime(reader.GetDateTime 1)
                  ``open`` = reader.GetDouble 2
                  high     = reader.GetDouble 3
                  low      = reader.GetDouble 4
                  close    = reader.GetDouble 5
                  volume   = reader.GetInt64 6
                  rawClose = reader.GetDouble 7
                  premktVol = reader.GetInt64 8 }
            onNext (ticker, bar)

// ===========================================================================
// Pipeline 1 (daily) — DbEmitter -> MaxFlyer -> candidate array.
//
// The DbEmitter streams (ticker, bar) in (ticker, date) order. We keep one
// MaxFlyer per ticker (spun up fresh at each ticker boundary, since the rolling
// indicators must not bleed across tickers) and feed each bar into its
// Process(onNext, bar), whose onNext is the candidate sink — just a
// ResizeArray.Add. MaxFlyer owns Gate 1 + Gate 2 + the no-lookahead discipline
// internally, so this function is pure plumbing: no gates, no per-ticker state
// beyond "which MaxFlyer am I feeding". Returns the accumulated candidates.
// ===========================================================================
let collectCandidates (conn: DuckDBConnection) (cfg: Config)
                      (startDate: DateOnly) (endDate: DateOnly) : Candidate[] =
    let emitter = DbEmitter(conn, startDate, endDate)
    let candidates = ResizeArray<Candidate>()

    let mutable ticker_sys = None
    let restart ticker =
        let sys = 
            MaxFlyer(ticker, cfg.HiCloseWindow, cfg.AtrWindow,
                    cfg.TightnessWindow, cfg.VolDays, cfg.Daily)
        ticker_sys <- Some (ticker, sys)
        sys

    emitter.Process(fun (ticker, bar) ->
        // ticker boundary: a fresh MaxFlyer so no indicator state crosses tickers.
        let sys =
            match ticker_sys with
            | Some(pTicker, sys) -> if pTicker <> ticker then restart ticker else sys
            | None -> restart ticker
        sys.Process(candidates.Add, bar))

    candidates.ToArray()

// ---------------------------------------------------------------------------
// Pipeline 2 (intraday) — MinuteEmitter → IntradaySystem → trip array.
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

let private toTrip (c: Candidate) (notional: float) (short: bool) (pos: IntradayPosition) : Trip =
    match pos.State with
    | ExitedAt (exitMin, exitPx, reason) ->
        let qty = notional / pos.EntryPx
        // long P&L = qty*(exit-entry); short fades the breakout, so flip the sign.
        let dirPnl = if short then pos.EntryPx - exitPx else exitPx - pos.EntryPx
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
          RunHighAtEntry = pos.BreakoutRef
          StopLo = pos.StopLevel
          ExitMin = exitMin
          ExitPrice = exitPx
          ExitReason = reason
          Qty = qty
          NetPnL = qty * dirPnl                            // long or short (see dirPnl)
          BarsHeld = exitMin - pos.EntryMin }              // minutes held (1m bars)
    | Holding -> failwith "toTrip called on a still-Holding position (Finalize first)"

// ===========================================================================
// Pipeline stage 1 (intraday) — MinuteEmitter: the ONLY database read on the
// intraday side. One per DATE: it opens that date's minute parquet exactly once
// and streams the candidate tickers' RTH-session bars (08:30..MOC, ET) downstream
// as (ticker, MinuteBar), already split-adjusted to each candidate's daily scale.
// Rows arrive ordered (ticker, et_min), so a consumer sees one ticker's full day
// before the next — the same boundary shape as the daily DbEmitter.
// ===========================================================================
type MinuteEmitter
        ( conn: DuckDBConnection, path: string,
          tickers: string[], adjRatio: IDictionary<string, float>,
          sessionStartMin: int, mocMin: int ) =

    // Public accessors for the ctor-captured state `Process` reads (it's `inline`).
    member val Conn = conn
    member val AdjRatio = adjRatio

    // ET conversion ported from scripts/equity/intraday_checkpoints.py (lines 84-90).
    // public (not private) because Process is `inline` (see DbEmitter.Sql).
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

// ===========================================================================
// Pipeline 2 (intraday) — MinuteEmitter -> IntradaySystem -> trip array.
//
// Candidates are grouped by date; each date's minute parquet is opened ONCE by a
// MinuteEmitter (the per-date load that the ticker-sorted, row-group-pruned
// layout makes cheap). Within a date, bars stream (ticker, et_min) ordered, so we
// run one IntradaySystem per ticker (fresh at each ticker boundary). Unlike
// MaxFlyer (which emits via onNext mid-stream), IntradaySystem accumulates and is
// the TERMINAL stage: at each ticker boundary we Finalize the prior system and
// drain its closed positions into the trip array, then again after the stream ends.
// Returns all trips across all dates.
// ===========================================================================
let collectTrips (conn: DuckDBConnection) (cfg: Config) (minuteDir: string)
                 (candidates: Candidate[]) : Trip[] =
    let trips = ResizeArray<Trip>()

    // Drain a finished ticker's IntradaySystem: flatten at its last bar (held in
    // the system's own state), then convert every closed position to a Trip
    // (carrying the candidate's daily metrics).
    let drain (c: Candidate) (sys: IntradaySystem) =
        sys.Flatten()
        for pos in sys.Positions do
            match pos.State with
            | ExitedAt _ -> trips.Add(toTrip c cfg.Notional cfg.Intraday.Short pos)
            | Holding -> failwith "Flatten closes all; unreachable"

    for date, cands in candidates |> Array.groupBy (fun c -> c.Date) do
        let path = IO.Path.Combine(minuteDir, sprintf "%s.parquet" (date.ToString("yyyy-MM-dd")))
        if IO.File.Exists path then
            let byTicker = cands |> Array.map (fun c -> c.Ticker, c) |> dict
            let adjRatio = cands |> Array.map (fun c -> c.Ticker, c.AdjRatio) |> dict
            let emitter = MinuteEmitter(conn, path, Array.map (fun (c: Candidate) -> c.Ticker) cands,
                                        adjRatio, cfg.Intraday.SessionStartMin, cfg.Intraday.MocMin)

            // Per-ticker state across the (ticker, et_min)-ordered stream. `cur` holds
            // the candidate + its system (the system tracks its own last bar), so a
            // ticker boundary can drain the previous one.
            let mutable cur : (Candidate * IntradaySystem) option = None
            emitter.Process(fun (ticker, bar) ->
                match cur with
                | Some(c, sys) when c.Ticker = ticker ->
                    sys.Process bar
                | _ ->
                    // ticker boundary: drain the previous ticker, start this one.
                    match cur with
                    | Some(pc, psys) -> drain pc psys
                    | None -> ()
                    let c = byTicker.[ticker]
                    let sys = IntradaySystem(cfg.Intraday, ticker, date)
                    sys.Process bar
                    cur <- Some(c, sys))
            // drain the final ticker of the date.
            match cur with
            | Some(c, sys) -> drain c sys
            | None -> ()

    trips.ToArray()

/// Run the whole MaxFlyer backtest: pipeline 1 (daily Gate 1+2 -> candidates)
/// then pipeline 2 (intraday Gate 3, grouped by date so each minute_aggs parquet
/// opens at most once, only for dates with ≥1 fully-qualified candidate).
let run (dbPath: string) (minuteDir: string) (cfg: Config)
        (startDate: DateOnly) (endDate: DateOnly) : Trip[] * int =
    let connStr = $"Data Source={dbPath};ACCESS_MODE=READ_ONLY"
    use conn = new DuckDBConnection(connStr)
    conn.Open()
    do
        use pragma = conn.CreateCommand()
        pragma.CommandText <- "PRAGMA memory_limit='6GB'"
        pragma.ExecuteNonQuery() |> ignore

    let candidates = collectCandidates conn cfg startDate endDate
    let trips = collectTrips conn cfg minuteDir candidates
    trips, candidates.Length

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

// ---------------------------------------------------------------------------
// Candidate cache — round-trip Candidate[] to/from CSV.
//
// Pipeline 1 (the daily scan over split_adjusted_prices) is the slow half and is
// INVARIANT to every intraday/exit/target knob. Caching its output lets the
// intraday experiments (exits, MA/channel targets, side) iterate without re-paying
// the scan. The CSV carries exactly the Candidate fields the intraday side needs.
// ---------------------------------------------------------------------------

let candidateHeader =
    "ticker,date,signal_date,prev_adj_close,adj_ratio,gap_pct,premkt_vol,rth_open,"
    + "daily_atr_pct,daily_tightness,max_log_atr,pct_52w,pct_52w_high,avg_dollar_volume_4w"

let private candidateRow (c: Candidate) : string =
    String.concat "," [
        c.Ticker
        c.Date.ToString("yyyy-MM-dd")
        c.SignalDate.ToString("yyyy-MM-dd")
        fmt c.PrevAdjClose
        fmt c.AdjRatio
        fmt c.GapPct
        string c.PremktVol
        fmt c.RthOpen
        fmt c.DailyAtrPct
        fmt c.DailyTightness
        fmt c.MaxAtrLog
        fmt c.Pct52w
        fmt c.Pct52wHigh
        fmt c.AvgDolVol
    ]

let writeCandidates (path: string) (candidates: Candidate[]) =
    use w = new IO.StreamWriter(path)
    w.WriteLine candidateHeader
    for c in candidates do w.WriteLine(candidateRow c)

let readCandidates (path: string) : Candidate[] =
    let pf (s: string) = Double.Parse(s, inv)
    let pd (s: string) = DateOnly.ParseExact(s, "yyyy-MM-dd")
    IO.File.ReadLines path
    |> Seq.skip 1                              // header
    |> Seq.map (fun line ->
        let f = line.Split(',')
        { Ticker = f.[0]
          Date = pd f.[1]
          SignalDate = pd f.[2]
          PrevAdjClose = pf f.[3]
          AdjRatio = pf f.[4]
          GapPct = pf f.[5]
          PremktVol = Int64.Parse(f.[6], inv)
          RthOpen = pf f.[7]
          DailyAtrPct = pf f.[8]
          DailyTightness = pf f.[9]
          MaxAtrLog = pf f.[10]
          Pct52w = pf f.[11]
          Pct52wHigh = pf f.[12]
          AvgDolVol = pf f.[13] })
    |> Seq.toArray

// ---------------------------------------------------------------------------
// Split run entry points (for the candidate cache).
// ---------------------------------------------------------------------------

let private withConn (dbPath: string) (f: DuckDBConnection -> 'a) : 'a =
    let connStr = $"Data Source={dbPath};ACCESS_MODE=READ_ONLY"
    use conn = new DuckDBConnection(connStr)
    conn.Open()
    do
        use pragma = conn.CreateCommand()
        pragma.CommandText <- "PRAGMA memory_limit='6GB'"
        pragma.ExecuteNonQuery() |> ignore
    f conn

/// Pipeline 1 ONLY: the daily Gate-1+2 scan → candidates (no intraday side).
let runCollectCandidates (dbPath: string) (cfg: Config)
                         (startDate: DateOnly) (endDate: DateOnly) : Candidate[] =
    withConn dbPath (fun conn -> collectCandidates conn cfg startDate endDate)

/// Pipeline 2 ONLY: intraday Gate-3 over a PRE-COLLECTED candidate set (cached CSV).
/// Skips the daily scan entirely. The DB connection is still needed for the per-date
/// minute_aggs reads.
let runFromCandidates (dbPath: string) (minuteDir: string) (cfg: Config)
                      (candidates: Candidate[]) : Trip[] * int =
    withConn dbPath (fun conn -> collectTrips conn cfg minuteDir candidates), candidates.Length
