module TradingEdge.DipRiderV6.Backtest

open System
open System.Globalization
open System.Collections.Generic
open DuckDB.NET.Data
open TradingEdge.DipRiderV6.Intraday

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
      Notional: float
      /// ⭐ THE V5 IN-PLAY SELECTOR — minimum 09:30-09:45 dollar volume (dv_0945). Replaces V4's
      /// leaked `avgvol20 * day_close >= $30M` ADV floor. Default $5M (user, 2026-07-17): a 15-min
      /// window sits ~an order of magnitude below a full-day ADV, so V4's $30M does NOT carry over
      /// (at $30M dv_0945 keeps only 40% of the universe vs $5M's 75%). Swept in F2. 0 = no floor.
      MinDv0945: float }

/// DipRiderV4 default = THE A BOOK (the settled high-capacity production book).
/// Long-only, hold-to-MOC or the EMA-triggered stop, morning window 10:00-13:30,
/// two-anchor timing (session extremes from 08:30, features from 09:30). The A book:
/// the three EMA-high breakout timers ON (bars<10), OR-combined with DIFFERENTIATED
/// per-window vol_climb floors (session@0 ∥ 60m@⅓ ∥ 20m@½), vol-as-gate; price-slope
/// and sum6 DISABLED (the breakout structure subsumes them). The surviving momentum
/// gates: log-ATR ≥ 0.013, chg1d ≥ +10%, chg3d ≥ 0, ema-vs-vwap ≥ −2%, exhaustion
/// cut rvol5m < 100. → 1860 trips (2020+) / PF 2.77 / clip 1.86 / $1.43M, all-weather.
/// Tighten to A+/A++ by raising the per-window vc floors (--breakout-vc-*).
/// DipRiderV6 DEFAULT = the SAMPLER (see Intraday.fs header). Intraday mean reversion:
/// buy the 20m low of closes while above VWAP, sell into the 20m high. mc=0 by design —
/// it AVERAGES DOWN, which is not tradable, but it removes PATH DEPENDENCY so the whole
/// feature study is post-hoc SQL over one CSV. ⚠ Raw PF here is ATTRIBUTION, not a
/// portfolio number: re-run any selected cell at --max-concurrent 1 before believing it.
let defaultConfig =
    { Intraday =
        { EntryLowWindow  = 20         // buy the 20m low of closes. 60 = the deeper-dip variant (user).
          ExitHighWindow  = 20         // sell into the 20m high of closes.
          RequireAboveVwap = false     // ⭐ OFF BY DEFAULT (user, 2026-07-17). V5 F6 called `close > VWAP`
                                       // load-bearing, but that was measured on the OLD system — baking it in
                                       // made dist_vwap POSITIVE-ONLY and the below-VWAP half was NEVER
                                       // SAMPLED. A sampler must RECORD, not GATE. Slice dist_vwap post-hoc.
          MaxConcurrent   = 0          // ⭐ SAMPLER. 1 = a real (tradable) book.
          VolWindow       = 20
          EmaPeriod       = 9
          AdxPeriod       = 14
          SessionStartMin = 9 * 60 + 30  // 09:30 — V6 has no premarket session anchor (no session extremes).
          FeatureStartMin = 9 * 60 + 30  // 09:30 ET — everything folds from the RTH open.
          EntryStartMin   = 10 * 60      // 10:00 ET (09:30-10:00 warms the windows).
          EntryEndMin     = 13 * 60 + 30 // 13:30 ET
          MocMin          = 16 * 60      // 16:00 ET
          MinLowsIntoLeg  = 0            // ⭐ 0 = the SAMPLER (take every low). Set 5 (+ --max-concurrent 1)
                                         // for the F3 production book: PF 1.968 / +1.48%/tr / 1068 trips.
          MaxAtrPct       = infinity     // ⭐ UNCAPPED by default (user, 2026-07-17). F8/F14 measured a hard
                                         // ceiling (~0.035; >=0.05 -> PF 0.755 at -1.66%/tr) — BUT that was
                                         // under a 20m exit, and F16 showed the exit window CHANGES WHICH
                                         // ENTRY FEATURES MATTER (the 5m target's PF lift GROWS with ATR:
                                         // +17% at 0.004-0.006 -> +30% at 0.027-0.035). The ceiling may be
                                         // largely an ARTIFACT of the slow exit. Keep it uncapped so the tail
                                         // stays SAMPLED and sliceable post-hoc; log_atr_20 is recorded.
          MinAtrPct       = 0.004 }      // ⭐ A CAPACITY floor, not a quality one (user, 2026-07-17): purely to
                                         // cut the sampler to a manageable size. F8 measured the sub-0.004
                                         // band as WORTHLESS — 69% of the book (7.4M of 10.7M trips) at
                                         // PF 1.03-1.13 and +0.006-0.049%/trade, i.e. BELOW transaction costs.
                                         // Nothing of value is discarded. The QUALITY floor is ~0.009 and the
                                         // peak is broad (0.009-0.02 all ~PF 1.58); ⚠ F8 also found a CEILING
                                         // (<0.035) that is NOT yet wired — high ATR INVERTS (>=0.05 -> PF
                                         // 0.755 at -1.66%/tr). Slice both post-hoc; log_atr_20 is recorded.
      Notional = 10_000.0
      MinDv0945 = 3_000_000.0 }   // ⭐ THE LIQUIDITY FLOOR (user, 2026-07-17): >= $3M traded 09:30-09:45.
                                  // NOT optional — F14 measured what happens without it: PF rises MONOTONICALLY
                                  // as liquidity FALLS (>= $30M -> 1.372; $250k-1M -> 1.885; < $250k -> 3.291),
                                  // because the low-dv cells are PENNY STOCKS (median entry price $1.13 at
                                  // < $250k, $1.50 at $250k-1M). The spread alone would eat that "edge", before
                                  // any market impact. $3M leaves a median entry of $8.58 — fillable.

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
      // ----- the LIVE-SAFE 09:45 baseline (V5). Both complete at 09:45; EntryStartMin = 10:00 ⇒ legal. -----
      Vol0945: float            // total volume 09:30-09:45
      NBar0945: int             // # of 1m bars present 09:30-09:45 (the permin15m denominator)
      Rvol0945Honest: float     // ⭐ LIVE-SAFE rvol (premkt-incl vol thru 09:45 / prior-20d avg daily vol).
                                // RECORDED, not gated — the sampler must sample it (slice post-hoc).
      Dv0945: float }           // ⭐ opening dollar volume = vol_0945 * avgprice_0945 * adj_ratio — the
                                // in-play selector that REPLACES the leaked `avgvol20 * day_close` ADV floor.

/// A finished trip — debloated to what the trimmed engine records plus cheap
/// candidate context. Core entry features (the tuned levers) come from the
/// position snapshot; day-scale returns + forward closes come from the candidate.
/// One V6 trip. ⭐ EVERY field beyond (entry, exit) is a RECORDED POST-HOC FEATURE —
/// nothing here gates. The whole point of V6 is that the study is SQL over this CSV.
type Trip =
    { Symbol: string
      TradeDate: DateOnly
      AdjRatio: float
      EntryMin: int
      EntryPrice: float
      // ----- ⭐ THE RESET COUNTERS (user's idea; see NewLowCounters) -----
      BarsSinceFirstLow: int     // bars since this down-leg's FIRST new low
      LowsSinceFirstLow: int     // 0 = the FIRST low of the leg; N = averaged down N times
      // ----- location -----
      VwapAtEntry: float
      DistVwap: float            // entry/vwap - 1
      DistVwapZ: float           // ⭐ z of DistVwap (linear)
      DistVwapZLog: float        // ⭐ z of log(close/vwap)
      VolZLog: float             // ⭐ volume z, log space
      VolZLin: float             // ⭐ volume z, normal space
      IsNewSessLow: bool         // ⭐ entry is also a new SESSION close-low (free-fall)
      PrevSessVolHigh: float     // ⭐ session 1m-vol high as of the PRIOR bar
      IsNewSessVolHigh: bool     // ⭐ this bar made a new session 1m-vol high
      EmaAtEntry: float
      PctChgSinceOpen: float
      Chg1d: float
      Chg3d: float
      // ----- volatility / trend -----
      LogAtr20: float
      Adx14: float               // ⭐ trend STRENGTH (direction-agnostic)
      PlusDi14: float
      MinusDi14: float
      // ----- slopes -----
      PriceSlopeOpen: float
      PriceSlope60: float
      PriceSlope20: float
      VolSlopeOpen: float
      VolSlope60: float
      VolSlope20: float
      // ----- volume -----
      BarVol: float
      Brv15m: float
      Rvol5m: float
      VolClimb: float
      CumVol: float
      Dv0945: float              // the day's opening dollar volume
      Rvol0945Honest: float      // the day's live-safe rvol through 09:45 (RECORDED, not gated)
      // ----- exit -----
      ExitMin: int
      ExitPrice: float
      ExitReason: string         // "target" | "moc"
      RetMoc: float
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
          AdjRatio = c.AdjRatio
          EntryMin = pos.EntryMin
          EntryPrice = pos.EntryPx
          BarsSinceFirstLow = pos.BarsSinceFirstLow
          LowsSinceFirstLow = pos.LowsSinceFirstLow
          VwapAtEntry = pos.VwapAtEntry
          DistVwap = pos.DistVwap
          DistVwapZ = pos.DistVwapZ
          DistVwapZLog = pos.DistVwapZLog
          VolZLog = pos.VolZLog
          VolZLin = pos.VolZLin
          IsNewSessLow = pos.IsNewSessLow
          PrevSessVolHigh = pos.PrevSessVolHigh
          IsNewSessVolHigh = pos.IsNewSessVolHigh
          EmaAtEntry = pos.EmaAtEntry
          PctChgSinceOpen = pos.PctChgSinceOpen
          Chg1d = pos.Chg1d
          Chg3d = pos.Chg3d
          LogAtr20 = pos.LogAtr20
          Adx14 = pos.Adx14
          PlusDi14 = pos.PlusDi14
          MinusDi14 = pos.MinusDi14
          PriceSlopeOpen = pos.PriceSlopeOpen
          PriceSlope60 = pos.PriceSlope60
          PriceSlope20 = pos.PriceSlope20
          VolSlopeOpen = pos.VolSlopeOpen
          VolSlope60 = pos.VolSlope60
          VolSlope20 = pos.VolSlope20
          BarVol = pos.BarVol
          Brv15m = pos.Brv15m
          Rvol5m = pos.Rvol5m
          VolClimb = pos.VolClimb
          CumVol = pos.CumVol
          Dv0945 = c.Dv0945
          Rvol0945Honest = c.Rvol0945Honest
          ExitMin = exitMin
          ExitPrice = exitPx
          ExitReason = reason
          RetMoc = (if pos.EntryPx > 0.0 then exitPx / pos.EntryPx - 1.0 else nan)
          DayClose = c.DayClose
          CloseFwd1d = c.CloseFwd1d
          CloseFwd3d = c.CloseFwd3d
          CloseFwd5d = c.CloseFwd5d
          Qty = qty
          NetPnL = qty * (exitPx - pos.EntryPx)
          BarsHeld = exitMin - pos.EntryMin }
    | Holding -> failwith "toTrip called on a still-Holding position (Flatten first)"

// ===========================================================================
// Pipeline 1 — read qualifying (ticker, day) rows from `diprider_v5_candidate`
// (scripts/equity/build_diprider_v5_candidate.fsx).
//
// ⭐ THE V5 UNIVERSE CHANGE. V4 read `vwap_reclaim_candidate`, whose universe filter
// `avgvol20 * day_close >= $30M` was a LOOKAHEAD that destroyed the system (PF 2.876 ->
// 1.158 once removed; docs/lookahead_protocol.md). Both factors were unknowable at the
// 10:00 entry — worst of all `avgvol20` is CURRENT-ROW-inclusive, so a volume spike on D
// inflated D's OWN 20d average and pushed the name over the floor: a backdoor
// "today is a 12x-volume day" selector.
//
// V5 selects on `dv_0945` = the dollars traded in D's OWN 09:30-09:45 window. The point is
// STRUCTURAL, not a re-tuned threshold: a FIXED, CLOSED window cannot swallow D's outcome
// the way a CURRENT-ROW rolling average can. Verified leak-free — names admitted and
// rejected by this floor have IDENTICAL D-volume ratios (1.32x vs 1.32x), where the old
// ADV floor's admits were a 12.7x-volume day at the margin.
//
// ⚠ LEGAL ONLY BECAUSE EntryStartMin (10:00) >= 09:45. That alignment is load-bearing (R3).
// ===========================================================================
let private readCandidates (conn: DuckDBConnection) (startDate: DateOnly) (endDate: DateOnly) (minDv0945: float) : Candidate[] =
    // Research override: DR5_CANDIDATE_TABLE lets a breakdown run against a different universe
    // without disturbing the production table. Validated identifier-only (injection-safe).
    let table =
        match Environment.GetEnvironmentVariable "DR5_CANDIDATE_TABLE" with
        | null | "" -> "diprider_v6_candidate"   // the UNFILTERED twin: no rvol prune, so the sampler samples
        | t when t |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_') -> t
        | bad -> failwithf "Invalid DR5_CANDIDATE_TABLE %A (identifier chars only)" bad
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        $"SELECT ticker, date, prev_adj_close, close_3d, day_close, adj_ratio,
                close_fwd_1d, close_fwd_3d, close_fwd_5d, day_open,
                vol_0945::DOUBLE, nbar_0945, dv_0945, rvol_0945_honest   -- vol_0945 is HUGEINT; cast for the reader
         FROM {table}
         WHERE date >= $start AND date <= $end AND dv_0945 >= $mindv
         ORDER BY ticker, date"
    let pStart = cmd.CreateParameter() in pStart.ParameterName <- "start"; pStart.Value <- startDate; cmd.Parameters.Add pStart |> ignore
    let pEnd   = cmd.CreateParameter() in pEnd.ParameterName   <- "end";   pEnd.Value   <- endDate;   cmd.Parameters.Add pEnd   |> ignore
    let pDv    = cmd.CreateParameter() in pDv.ParameterName    <- "mindv"; pDv.Value    <- minDv0945; cmd.Parameters.Add pDv    |> ignore
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
              Vol0945 = dbl 10
              NBar0945 = (if reader.IsDBNull 11 then 0 else int (reader.GetInt64 11))
              Dv0945 = dbl 12
              Rvol0945Honest = dbl 13 })
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

    let drain (c: Candidate) (sys: IntradaySystem, lastBar: MinuteBar) =
        sys.Flatten lastBar
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
            let mutable cur : (Candidate * IntradaySystem * MinuteBar) option = None
            emitter.Process(fun (ticker, bar) ->
                match cur with
                | Some(c, sys, _) when c.Ticker = ticker ->
                    sys.Process bar
                    cur <- Some(c, sys, bar)          // track the LAST bar for Flatten
                | _ ->
                    match cur with
                    | Some(pc, psys, plast) -> drain pc (psys, plast)
                    | None -> ()
                    let c = byTicker.[ticker]
                    // 20d per-minute volume pace (avgvol20/390) — the exhaustion-cut denominator. 0 = off.
                    // LIVE-SAFE per-minute volume baseline: D's OWN mean 1m volume over 09:30-09:45.
                    // Replaces V4's `permin20d = avgvol20/390` (a lookahead — avgvol20 is CURRENT-ROW-
                    // inclusive, so it carried D's own full-session volume). Complete at 09:45 < 10:00 entry.
                    let permin15m = if c.NBar0945 > 0 && c.Vol0945 > 0.0 then c.Vol0945 / float c.NBar0945 else 0.0
                    let sys = IntradaySystem(cfg.Intraday, ticker, date, c.PrevAdjClose, c.Close3d, permin15m)
                    sys.Process bar
                    cur <- Some(c, sys, bar))
            match cur with
            | Some(c, sys, lastBar) -> drain c (sys, lastBar)
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

    let candidates = readCandidates conn startDate endDate cfg.MinDv0945
    let trips = collectTrips conn cfg minuteDir candidates
    trips, candidates.Length

// ---------------------------------------------------------------------------
// CSV emission
// ---------------------------------------------------------------------------

let private inv = CultureInfo.InvariantCulture
let private fmt (x: float) = if Double.IsNaN x then "nan" else x.ToString("0.################", inv)
let private hhmm (m: int) = sprintf "%02d:%02d" (m / 60) (m % 60)

/// ⭐ The V6 CSV — the whole study runs as SQL over this. Every column past `exit_*`
/// is a POST-HOC feature; none of them gate. Add a feature here, re-run ONCE, then
/// slice forever without touching the engine.
let header =
    "symbol,trade_date,adj_ratio,entry_time,entry_price,"
    + "bars_since_first_low,lows_since_first_low,"          // ⭐ the reset counters
    + "vwap_at_entry,dist_vwap,dist_vwap_z,dist_vwap_z_log,vol_z_log,vol_z_lin,is_new_sess_low,prev_sess_vol_high,is_new_sess_vol_high,ema_at_entry,pct_chg_since_open,chg_1d,chg_3d,"
    + "log_atr_20,adx_14,plus_di_14,minus_di_14,"
    + "price_slope_open,price_slope_60,price_slope_20,"
    + "vol_slope_open,vol_slope_60,vol_slope_20,"
    + "bar_vol,brv_15m,rvol_5m,vol_climb,cum_vol,dv_0945,rvol_0945_honest,"
    + "exit_time,exit_price,exit_reason,ret_moc,day_close,close_fwd_1d,close_fwd_3d,close_fwd_5d,"
    + "qty,net_pnl,bars_held_min"

let private row (t: Trip) =
    let inline f (x: float) = if Double.IsNaN x then "" else x.ToString("R", CultureInfo.InvariantCulture)
    let hhmm (m: int) = sprintf "%02d:%02d" (m / 60) (m % 60)
    String.Join(",",
        [| t.Symbol
           t.TradeDate.ToString("yyyy-MM-dd")
           f t.AdjRatio
           hhmm t.EntryMin
           f t.EntryPrice
           string t.BarsSinceFirstLow
           string t.LowsSinceFirstLow
           f t.VwapAtEntry; f t.DistVwap; f t.DistVwapZ; f t.DistVwapZLog; f t.VolZLog; f t.VolZLin; (if t.IsNewSessLow then "1" else "0"); f t.PrevSessVolHigh; (if t.IsNewSessVolHigh then "1" else "0"); f t.EmaAtEntry
           f t.PctChgSinceOpen; f t.Chg1d; f t.Chg3d
           f t.LogAtr20; f t.Adx14; f t.PlusDi14; f t.MinusDi14
           f t.PriceSlopeOpen; f t.PriceSlope60; f t.PriceSlope20
           f t.VolSlopeOpen; f t.VolSlope60; f t.VolSlope20
           f t.BarVol; f t.Brv15m; f t.Rvol5m; f t.VolClimb; f t.CumVol; f t.Dv0945; f t.Rvol0945Honest
           hhmm t.ExitMin; f t.ExitPrice; t.ExitReason; f t.RetMoc
           f t.DayClose; f t.CloseFwd1d; f t.CloseFwd3d; f t.CloseFwd5d
           f t.Qty; f t.NetPnL; string t.BarsHeld |])

let writeCsv (path: string) (trips: Trip[]) =
    use w = new IO.StreamWriter(path)
    w.WriteLine header
    for t in trips do w.WriteLine(row t)
