module TradingEdge.MaxFlyerV3.Backtest

open System
open System.Globalization
open System.Collections.Generic
open DuckDB.NET.Data
open TradingEdge.MaxFlyerV3.Intraday

// ===========================================================================
// MaxFlyerV2 backtest wiring (intraday SHORT pop-fade; forked from LowFlyer).
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

/// MaxFlyerV2 config = the intraday engine knobs + notional. No daily gates.
type Config =
    { Intraday: IntradayConfig
      Notional: float }

/// Production default: the market-wide long mean-reversion setup. Downside
/// breakout (new session low) + long P&L, no stop / no target, hold to MOC,
/// scan from 09:45, RTH warmup from 09:30. The two intraday quality gates
/// (tightness / ATR%) are OFF (+inf) — only new-session-low + volume-confirm +
/// the 09:45 floor survive.
let defaultConfig =
    { Intraday =
        { VolWindow = 20
          MaxTightness = infinity        // OFF
          MaxAtrPct = infinity           // OFF
          SessionStartMin = 8 * 60 + 30  // 08:30 ET — accumulate the running low/vol-high, ATR,
                                         // tightness and the 20-bar LagMa from premarket (like MaxFlyer),
                                         // so every indicator (incl. chg_20m) is warm by the 09:45 entry
                                         // floor regardless of VolWindow. Entries still gated at EntryStartMin.
          EntryStartMin   = 9 * 60 + 45  // 09:45 ET — earliest entry
          UseStop = false
          PctStop = 0.0
          TimeStopMin = 0
          Downside = false               // MaxFlyerV2 = the POP: breakout to a new session HIGH
          WickBreakout = false           // CLOSE through the prior high
          ExtGate = 0.0
          RiseEntry = 0.0
          TrailEntry = false
          Short = true                   // FADE the pop (short). This is the whole point of the fork —
                                         // MaxFlyerV2 defaults to the short so it no longer shares LowFlyer's
                                         // long defaultConfig (which was leaking the long's vol-high-frac 0.90).
          Target = NoTarget
          MocMin = 16 * 60               // 16:00 ET
          MaxConcurrent = 0              // unlimited concurrent entries per day
          MinBarFlush = 0.0              // entry-bar flush gate OFF (--min-bar-flush -0.007 to enable)
          MinBarFlushFloor = 0.0         // entry-bar flush-depth floor OFF (--min-bar-flush-floor -0.12 to enable)
          VolHighFrac = 1.0              // SHORT vol-confirm = STRICT (must EXCEED the vol high). Decoupled from
                                         // the long's 0.90: the short's research (maxflyerv2_results.md) uses the
                                         // strict gate + brv20d>=100 as the main lever. --vol-high-frac to override.
          MinCloseRef = true             // default = min-CLOSE reference (wick-immune; +~29% trips at ~same
                                         // PF — Run 12). --min-low-ref switches back to the min-LOW channel.
          // 9-EMA arm-timer entry + max-EMA stop — all OFF by default (V3 reproduces V2 until flagged on).
          EmaEntry = false               // --ema-entry: arm on breakout, short on the 9-EMA cross-under.
          EmaPeriod = 9                  // the EMA period for the arm-timer entry + max-EMA stop.
          EmaArmBars = 60                // --ema-arm-bars: the cross-under window after a breakout. F6: ≤60 more
                                         // than DOUBLES net vs ≤10 ($4.08M vs $1.84M) for a modest PF give-up
                                         // (5.27 vs 6.61); the edge doesn't need a fast rollover. Knee ~60–90.
          EmaMaxStop = false             // --ema-max-stop: cover when the 9-EMA rises above the session-max 9-EMA.
          EmaMaxStopWindow = 30          // --ema-max-stop-window: 30-bar ROLLING max 9-EMA anchor (F7). Re-anchors
                                         // the stop to the RECENT local EMA high near the fill; the session anchor
                                         // (0) staled → −153% tail. Rolling-30m + buf20 → worst −82%, 0 trades >100%.
          EmaMaxStopBuffer = 0.20        // --ema-max-stop-buffer. F7: with the rolling-30m anchor, 20% = the sweet
                                         // spot — worst −82% / 0 over 100% at PF 5.26 / net $4.07M (= the session
                                         // book's PF/net, HALF the tail). HARD CLIFF: buf25+ → −153% reappears;
                                         // buf<20 cuts winners (PF↓). (Was 0.30 for the session anchor in F5.)
          EmaPctStop = 0.0 }             // --ema-pct-stop: 9-EMA %-stop off the ENTRY 9-EMA (uniform per-trade cap).
                                         // 0 = OFF (default; the max-EMA stop is the primary). e.g. 0.60 caps the tail.
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
      EntryPrice: float          // = breakout bar close (the fill)
      EntryBarOpen: float        // the entry (breakout) 1m bar's OPEN — for the entry-bar %-change (close/open-1)
      PrevBarClose: float        // the strictly-prior 1m bar's CLOSE — for the flush = close/prevClose-1
      Chg20m: float              // 20-minute %-change into entry (engine LagMa(20), no post-hoc rescan)
      RunLowAtEntry: float       // the session low the breakout cleared (pos.BreakoutRef)
      IntradayAtrPct: float      // 1m log-ATR (VolWindow bars) snapshot at the breakout — how jumpy the name is intraday
      IntradayTightness: float   // 1m tightness snapshot at the breakout
      // RECORDED features (post-hoc slicing, NOT gates)
      Rvol: float                // cumVolToEntry / avgVol20
      BreakoutBarVol: int64      // the breakout bar's own volume
      NewVolHigh: bool           // did the breakout bar make a new session 1m-vol high? (always true at
                                 // vol-high-frac>=1; a relaxed run flags which entries clear the strict gate)
      VolVsHigh: float           // breakout-bar volume / running session 1m-vol high (continuous; >=1 = new high)
      BarRvol15m: float          // breakout_bar_vol / mean(1m vol over [9:30,9:45)) — the breakout-bar
                                 // volume spike relative to the name's own opening-15m 1m tempo. Baseline
                                 // = vol_0945 / nbar_0945 (RTH 09:30-09:45 sum / bar count). Discriminates
                                 // OPPOSITELY by side: extreme spikes (>=40x) = exhaustion blow-off that
                                 // fades on the SHORT (pop-fade PF 1.0->2.0), but falling-knife on the LONG.
      CumVolToEntry: int64       // cumulative day volume through the entry bar
      // ----- SIGNAL (arm/breakout) bar — under EmaEntry the breakout HIGH is the signal but the FILL is deferred
      //       to the 9-EMA cross-under; these let post-hoc analysis compare the signal bar to the entry bar. For
      //       a direct entry signal* == entry-bar values. -----
      SignalMin: int             // the signal (arm/breakout) bar's ET minute. entry lag = entry_time - signal_time.
      SignalHigh: float          // the breakout bar's HIGH — the trade SIGNAL price.
      SignalOpen: float          // the signal bar's OPEN.
      SignalLow: float           // the signal bar's LOW.
      SignalClose: float         // the signal bar's CLOSE.
      SignalVolume: int64        // the signal bar's own VOLUME.
      SessVolHighAtSignal: int64 // session 1m-vol high STRICTLY BEFORE the signal bar (excl. it) — vol-confirm ref.
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
          EntryBarOpen = pos.BreakoutBarOpen
          PrevBarClose = pos.PrevBarClose
          Chg20m = pos.Chg20mAtEntry
          RunLowAtEntry = pos.BreakoutRef
          IntradayAtrPct = pos.AtrPctAtEntry
          IntradayTightness = pos.TightnessAtEntry
          Rvol = (if c.AvgVol20 > 0.0 then float pos.CumVolAtEntry / c.AvgVol20 else nan)
          BreakoutBarVol = pos.BreakoutBarVol
          NewVolHigh = pos.NewVolHigh
          VolVsHigh = pos.VolVsHigh
          // spike vs the [9:30,9:45) 1m baseline = vol_0945/nbar_0945 (mean 1m vol over the window).
          BarRvol15m =
              (let meanBarVol15m = if c.NBar0945 > 0 then float c.Vol0945 / float c.NBar0945 else nan
               if meanBarVol15m > 0.0 then float pos.BreakoutBarVol / meanBarVol15m else nan)
          CumVolToEntry = pos.CumVolAtEntry
          SignalMin = pos.SignalMin
          SignalHigh = pos.SignalHigh
          SignalOpen = pos.SignalOpen
          SignalLow = pos.SignalLow
          SignalClose = pos.SignalClose
          SignalVolume = pos.SignalVolume
          SessVolHighAtSignal = pos.SessVolHighAtSignal
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
          RetMoc = (if pos.EntryPx > 0.0 then exitPx / pos.EntryPx - 1.0 else nan)
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
    | Armed _ | Skipped -> failwith "toTrip called on an Armed/Skipped (never-filled) position (filter these out)"

// ===========================================================================
// Pipeline 1 — read qualifying (ticker, day) rows from mr_candidate.
// ===========================================================================
let private readCandidates (conn: DuckDBConnection) (startDate: DateOnly) (endDate: DateOnly) : Candidate[] =
    // Research override: MFV2_CANDIDATE_PARQUET points readCandidates at a PARQUET of the same schema as
    // mr_candidate (e.g. a restricted (ticker,date) subset) instead of the mr_candidate table. Value must be
    // a filesystem path; single-quotes are escaped for the interpolated read_parquet(). Empty/unset = default.
    let source =
        match System.Environment.GetEnvironmentVariable "MFV2_CANDIDATE_PARQUET" with
        | null | "" -> "mr_candidate"
        | p -> sprintf "read_parquet('%s')" (p.Replace("'", "''"))
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        sprintf "SELECT ticker, date, prev_adj_close, close_3d, close_7d, day_close, adj_ratio, avgvol20,
                close_fwd_1d, close_fwd_3d, close_fwd_5d, day_open, med_bar_vol_0945, nbar_0945, vol_0945
         FROM %s
         WHERE date >= $start AND date <= $end
         ORDER BY ticker, date" source
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

let collectTrips (conn: DuckDBConnection) (cfg: Config) (minuteDir: string)
                 (candidates: Candidate[]) : Trip[] =
    let trips = ResizeArray<Trip>()

    let drain (c: Candidate) (sys: IntradaySystem) =
        sys.Flatten()
        for pos in sys.Positions do
            match pos.State with
            | ExitedAt _ -> trips.Add(toTrip c cfg.Notional cfg.Intraday.Short pos)
            | Armed _ | Skipped -> ()   // never filled → no trip (Armed/RiseEntry off here anyway)
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
                    let sys = IntradaySystem(cfg.Intraday, ticker, date, c.PrevAdjClose)
                    sys.Process bar
                    cur <- Some(c, sys))
            match cur with
            | Some(c, sys) -> drain c sys
            | None -> ()

    trips.ToArray()

/// Run the whole MaxFlyerV2 backtest: read mr_candidate (pipeline 1), then the
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
    + "entry_time,entry_price,entry_bar_open,prev_bar_close,chg_20m,run_low_at_entry,intraday_atr_pct_at_entry,intraday_tightness_at_entry,"
    + "rvol,breakout_bar_vol,new_vol_high,vol_vs_high,bar_rvol_15m,cum_vol_to_entry,"
    + "signal_time,signal_high,signal_open,signal_low,signal_close,signal_volume,sess_vol_high_at_signal,"
    + "pct_chg_since_open,close_1d,close_3d,close_7d,chg_1d,chg_3d,chg_7d,"
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
        fmt t.EntryBarOpen
        fmt t.PrevBarClose
        fmt t.Chg20m
        fmt t.RunLowAtEntry
        fmt t.IntradayAtrPct
        fmt t.IntradayTightness
        fmt t.Rvol
        string t.BreakoutBarVol
        (if t.NewVolHigh then "1" else "0")
        fmt t.VolVsHigh
        fmt t.BarRvol15m
        string t.CumVolToEntry
        hhmm t.SignalMin
        fmt t.SignalHigh
        fmt t.SignalOpen
        fmt t.SignalLow
        fmt t.SignalClose
        string t.SignalVolume
        string t.SessVolHighAtSignal
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
