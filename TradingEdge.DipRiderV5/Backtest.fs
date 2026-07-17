module TradingEdge.DipRiderV5.Backtest

open System
open System.Globalization
open System.Collections.Generic
open DuckDB.NET.Data
open TradingEdge.DipRiderV5.Intraday

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
let defaultConfig =
    { Intraday =
        { ReArm = RollingEmaLow         // re-arm reference: RollingEmaLow | SessionEmaLow | LastStopLevel.
          MaxConcurrent = 0             // 0 = unlimited (the pure arm/re-arm book). 1 = V3Backside slot-lifetime.
          VolAsGate = true              // GATE mode (vol ANDed into the trigger) — the A-book default.
          VolWindow = 20
          EmaPeriod = 9                  // the 9-EMA (closes-above-EMA reference)
          SessionStartMin = 8 * 60 + 30  // 08:30 ET — the session anchor (windows warm from here).
          FeatureStartMin = 9 * 60 + 30  // 09:30 ET (570) — VWAP, OLS slope, ATR, EMA, sum6 fold ONLY from here.
          EntryStartMin   = 10 * 60      // 10:00 ET — entry-window START (09:30-10:00 warms the windows).
          EntryEndMin     = 13 * 60 + 30 // 13:30 ET — entry-window END.
          MocMin          = 16 * 60      // 16:00 ET
          // ----- entry gates -----
          MinVolClimb    = 0.0           // A-book: the GLOBAL vol_climb gate is OFF (0); the per-window breakout
                                         // vc floors below (session 0 ∥ 60m ⅓ ∥ 20m ½) do the volume gating instead.
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
          MaxRvol5m15m   = 0.0           // ⛔ EXHAUSTION CUT OFF IN V5. V4 ran this at 100× the *20d* per-min pace,
                                         // but that denominator (avgvol20/390) was a LOOKAHEAD — avgvol20 includes
                                         // D's own full-session volume. The live-safe twin divides by permin15m
                                         // (D's 09:30-09:45 mean 1m vol), a COMPLETELY different scale, so V4's
                                         // 100 does NOT carry over. Off by default; re-tune from scratch (F1).
          Rvol5mUseMax   = false         // false = 5m AVG numerator (V3Backside). true = 5m MAX (the short book's
                                         // spiky 1m-vol signal). --rvol-use-max enables.
          VolStopFrac    = 0.0           // ⭐ THE SCALP EXIT — OFF by default so the baseline is hold-to-MOC
                                         // (V4 behavior) and the A/B is clean. --vol-stop-frac 0.667 enables:
                                         // exit when the volume 9-EMA falls to ⅔ of its value at entry.
                                         // Ported from BreakoutTimer F14; there it turned a hold-to-MOC swing
                                         // into a ~45-min scalp (raw PF 3.12→3.44 but net $302k→$262k — it caps
                                         // the tail). Worth re-testing here because V4's tail was largely the
                                         // LOOKAHEAD's doing; if the tail was fake, capping it costs little.
          VolStopUseAvg20 = false        // false = 9-EMA-of-volume basis (fast). true = 20m AvgMa (smooth,
                                         // closer to BreakoutTimer's original). Both warmup-safe averages.
          // ----- A-book breakout structure (the DEFAULT). All three EMA-high breakout timers ON (fire
          // within 10 bars of a new high), OR-combined, each branch ANDing its OWN per-window vol_climb floor:
          // session@0 ∥ 60m@⅓ ∥ 20m@½ (looser structure ⇒ higher vol bar required). This IS the A book
          // (1860 trips 2020+ / PF 2.77 / clip 1.86 / $1.43M) — differentiated per-window floors DOMINATE. -----
          MaxBarsSinceBreakout    = 10   // session-9EMA-high breakout timer ON (0<=bars<10).
          MaxBarsSince20mBreakout = 10   // 20m-EMA-high breakout timer ON.
          MaxBarsSince60mBreakout = 10   // 60m-EMA-high breakout timer ON.
          BreakoutOr = true              // OR the three windows (any within its countdown AND clearing its vc floor).
          BreakoutVcSession = 0.0        // session-high branch: vol_climb floor 0 (tightest structure, no vol bar).
          BreakoutVc60m = 1.0 / 3.0      // 60m-high branch: vol_climb >= 1/3.
          BreakoutVc20m = 0.5            // 20m-high branch: vol_climb >= 1/2 (loosest structure, highest vol bar).
          DisablePriceSlope = true       // A-book: the breakout STRUCTURE subsumes price-slope (DEAD WEIGHT).
          DisableSum6 = true             // A-book: the breakout STRUCTURE subsumes sum6 (DEAD WEIGHT).
          MinStopDistPct = 0.03          // ⭐ F17/F20: require the entry >= 3% above its 20m-EMA-low (larger stops
                                         // = stronger established moves; sub-3% = weak/choppy). SKIP mode: A book
                                         // 1860→1608 tr, PF 2.77→2.88, net $1.43M→$1.39M (97% kept). 0 = off.
          StopDistAsGate = false }       // SKIP (fire + disarm, open no position) — GATE tested (F20), no clear
                                         // win (+$26k net at LOWER PF 2.82 vs 2.88), so SKIP. --stop-dist-as-gate = GATE.
      Notional = 10_000.0
      MinDv0945 = 5_000_000.0 }   // ⭐ $5M opening dollar volume (user) — the live-safe in-play floor.

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
      Dv0945: float }           // ⭐ opening dollar volume = vol_0945 * avgprice_0945 * adj_ratio — the
                                // in-play selector that REPLACES the leaked `avgvol20 * day_close` ADV floor.

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
      Rvol5m: float              // trailing-5m avg vol / permin15m (LIVE-SAFE exhaustion ratio; V4 used permin20d)
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
        | null | "" -> "diprider_v5_candidate"
        | t when t |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_') -> t
        | bad -> failwithf "Invalid DR5_CANDIDATE_TABLE %A (identifier chars only)" bad
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        $"SELECT ticker, date, prev_adj_close, close_3d, day_close, adj_ratio,
                close_fwd_1d, close_fwd_3d, close_fwd_5d, day_open,
                vol_0945::DOUBLE, nbar_0945, dv_0945   -- vol_0945 is HUGEINT in the table; cast for the reader
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
              Dv0945 = dbl 12 })
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
                    // LIVE-SAFE per-minute volume baseline: D's OWN mean 1m volume over 09:30-09:45.
                    // Replaces V4's `permin20d = avgvol20/390` (a lookahead — avgvol20 is CURRENT-ROW-
                    // inclusive, so it carried D's own full-session volume). Complete at 09:45 < 10:00 entry.
                    let permin15m = if c.NBar0945 > 0 && c.Vol0945 > 0.0 then c.Vol0945 / float c.NBar0945 else 0.0
                    let sys = IntradaySystem(cfg.Intraday, ticker, date, c.PrevAdjClose, c.Close3d, permin15m)
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

    let candidates = readCandidates conn startDate endDate cfg.MinDv0945
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
