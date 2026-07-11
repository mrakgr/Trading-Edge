module TradingEdge.VwapReclaimV2.Backtest

open System
open System.Globalization
open System.Collections.Generic
open DuckDB.NET.Data
open TradingEdge.VwapReclaimV2.Intraday

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
          SessionStartMin = 9 * 60       // 09:30 ET — the SMB session VWAP anchors at the RTH OPEN (not
                                         // premarket). VWAP, the 9-EMA, the below-VWAP counter, and the
                                         // running session low all accumulate from 09:30 for the reclaim.
          EntryStartMin   = 10 * 60      // 10:00 ET — morning-window START (Finding 4: 10:00-13:30 is best;
                                         // 09:30-10:00 warms VWAP/EMA + the weakness run before any entry)
          EntryEndMin     = 13 * 60 + 30 // 13:30 ET — morning-window END (afternoon reclaims fade)
          UseStop = false
          PctStop = 0.0
          TimeStopMin = 0
          Downside = true                // breakout to a new session LOW
          WickBreakout = false           // CLOSE through the prior low
          ExtGate = 0.0
          RiseEntry = 0.0
          TrailEntry = false
          Short = false                  // LONG (buy the flush)
          Target = NoTarget
          MocMin = 16 * 60               // 16:00 ET
          MaxConcurrent = 0              // unlimited concurrent entries per day
          MinBarFlush = 0.0              // entry-bar flush gate OFF (--min-bar-flush -0.007 to enable)
          MinBarFlushFloor = 0.0         // entry-bar flush-depth floor OFF (--min-bar-flush-floor -0.12 to enable)
          VolHighFrac = 0.90             // volume-confirm = breakout vol >= 90% of the running vol high (PRODUCTION:
                                         // within 10% of the high; +239 trips over strict 1.0 at PF 3.38 vs 3.45 —
                                         // "near the high" carries the signal, <0.90 dilutes. --vol-high-frac to override.)
          MinCloseRef = true             // default = min-CLOSE reference (wick-immune; +~29% trips at ~same
                                         // PF — Run 12). --min-low-ref switches back to the min-LOW channel.
          // ----- SMB VWAP x 9-EMA reclaim (long-only) — ON by default for this project -----
          VwapReclaim = true             // this project's whole point: the EMA-crosses-VWAP entry.
          EmaPeriod = 9                  // SMB 9-EMA.
          BelowVwapFrac = 0.0            // OFF — the consecutive-run gate (MinRunBelowVwap) is the weakness
                                         // filter now (rb>=11 consecutive is a stronger signal than a 60%
                                         // cumulative fraction, and they double-gate if both are on).
          MinRunBelowVwap = 11           // require >=11 CONSECUTIVE bars EMA<VWAP into the cross. The rb FLOOR
                                         // (no upper cap — the by-year work showed the old rb<=30 cap threw
                                         // away ~6.5x the trips for a PF premium that doesn't survive the
                                         // trip-count trade: rb>=11 no-cap = ~5.8k trips / PF 1.48 / positive
                                         // EVERY year, vs rb[11,30] = 896 trips / PF 2.07 / 35 trips in 2025).
          StopAnchorVwap = true          // stop = VWAP - d*StopDistFrac (default); false = entry-anchored.
          FixedPctStop = 0.0             // 0 = use the d-geometry stop; >0 = a fixed %-below-entry stop (Finding 17).
          StopDistFrac = 2.0 / 3.0       // stop distance = d*2/3 (Finding 14): the video's d/3 was too tight
                                         // once the target is off & winners run to MOC — a wider stop lets
                                         // the reclaim breathe (stop-rate 55%->38%). d*2/3 is the peak (PF
                                         // 1.478->1.689); d/2 and full-d are slightly worse. --stop-dist-frac.
          MinStopDistPct = 0.0           // OFF (Finding 18). It was load-bearing at d/3 (Finding 7), but at
                                         // d*2/3 even the p5 stop distance is 1.17% > 1%, so NOTHING lands
                                         // under 1% — the filter is fully inert (removing it is byte-identical:
                                         // 868 trips, PF 1.69). Dropped to simplify. --min-stop-dist-pct to re-enable.
          ClampStopDist = true           // (moot while MinStopDistPct=0) clamp-vs-skip for a too-tight stop.
          MinTightness = 0.0             // OFF by default (V2 F4). tightness is REDUNDANT once run_dist_per_atr
                                         // gates the book — `d/atr<3 & no-tightness` reproduces `d/atr<3 &
                                         // tight>=3` to the decimal (PF 4.42 vs 4.38, same n). The speed caps
                                         // (d/atr, slope/atr) already remove the dead-flat chop tightness caught.
                                         // (V1 used tight>=3 on the fat book; --min-tightness re-enables it.)
          StopOnClose = true             // stop triggers only on a CLOSE below the level (ignore noise wicks).
          UseTarget = false              // NO target by default (Finding 13: winners run to MOC). --use-target re-enables VWAP+d.
          ReclaimShort = false }         // LONG reclaim by default; --reclaim-short mirrors to the short side.
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
      VolVsHigh: float           // breakout-bar volume / running session 1m-vol high (continuous; >=1 = new high).
                                 // FOR THE RECLAIM: repurposed to the below-VWAP FRACTION at entry.
      RunBelowVwap: int          // consecutive bars EMA<VWAP right before the reclaim cross (0 = breakout engine)
      StopDistPct: float         // stop distance as a fraction of entry = (entry - stopLevel)/entry
      BarRvol15m: float          // breakout_bar_vol / mean(1m vol over [9:30,9:45)) — the breakout-bar
                                 // volume spike relative to the name's own opening-15m 1m tempo. Baseline
                                 // = vol_0945 / nbar_0945 (RTH 09:30-09:45 sum / bar count). Discriminates
                                 // OPPOSITELY by side: extreme spikes (>=40x) = exhaustion blow-off that
                                 // fades on the SHORT (pop-fade PF 1.0->2.0), but falling-knife on the LONG.
      Rvol20m20d: float          // trailing-20m mean 1m volume / (avgvol20/390) — the last 20 minutes' volume
                                 // vs the name's 20-DAY per-minute baseline. "Is the convergence running hot
                                 // vs normal?" (Jeff's rising-volume-into-the-reclaim cue, 20m window not 1 bar).
      Rvol20m15m: float          // trailing-20m mean 1m volume / (vol_0945/nbar_0945) — the last 20 minutes'
                                 // volume vs the OPENING-15m per-minute average. >1 = volume ACCELERATING since
                                 // the open into the reclaim (the acceleration measure, vs the 20d "hot" one).
      RunMaxDist: float          // max (VWAP-EMA)/VWAP over the pre-cross run = the run's DEPTH (how far the
                                 // 9-EMA fell below VWAP). "Are bigger %-runs better trades?"
      RunAtr: float              // mean per-bar log-TR OVER the run bars (reset at each cross) = the run's own vol
      RunDistPerAtr: float       // RunMaxDist / RunAtr = the run's depth in ATR-units (depth normalized by vol)
      RunUpVol: float            // mean 1m vol of above-9EMA bars since the last VWAP cross (accumulation)
      RunDnVol: float            // mean 1m vol of below-9EMA bars since the last VWAP cross (distribution)
      RunUpDnRatio: float        // RunUpVol / RunDnVol — >1 = volume flowing into the RISING side (convergence
                                 // back toward VWAP); <1 = volume on the FALLING side (divergence/selloff)
      PriceSlope20: float        // 20m OLS log-price slope at entry (%/bar) — trend strength
      VolSlope20: float          // 20m OLS log-volume slope at entry — rising volume into the reclaim
      SlopePerAtr: float         // PriceSlope20 / (log-ATR at entry) — SPEED-normalized momentum (dist/atr successor)
      EmaMin20: float            // trailing-20m MIN of the 9-EMA at entry (later a stop basis)
      EmaClimb: float            // (9-EMA − 20m-min-9-EMA) / 9-EMA = alt depth: how far the EMA climbed off its floor
      EmaClimbPerRunAtr: float   // EmaClimb / run_atr    (alt `d` ÷ PULLBACK atr — matches d/atr's denominator)
      EmaClimbPerLogAtr: float   // EmaClimb / log-ATR20  (alt `d` ÷ ROLLING-20m log-ATR — the other denominator)
      DistPerLogAtr15: float     // run_max_dist / rolling-15m log-ATR (alt d/atr denominator — window sweep)
      DistPerLogAtr30: float     // run_max_dist / rolling-30m log-ATR (alt d/atr denominator — window sweep)
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
          RunBelowVwap = pos.RunBelowVwapAtEntry
          StopDistPct = pos.StopDistPct
          // spike vs the [9:30,9:45) 1m baseline = vol_0945/nbar_0945 (mean 1m vol over the window).
          BarRvol15m =
              (let meanBarVol15m = if c.NBar0945 > 0 then float c.Vol0945 / float c.NBar0945 else nan
               if meanBarVol15m > 0.0 then float pos.BreakoutBarVol / meanBarVol15m else nan)
          // trailing-20m mean 1m volume vs (a) the 20-day per-minute baseline and (b) the opening-15m avg.
          Rvol20m20d =
              (let perMin20d = c.AvgVol20 / 390.0   // 390 RTH minutes/day
               if perMin20d > 0.0 && not (Double.IsNaN pos.Vol20mAvgAtEntry) then pos.Vol20mAvgAtEntry / perMin20d else nan)
          Rvol20m15m =
              (let meanBarVol15m = if c.NBar0945 > 0 then float c.Vol0945 / float c.NBar0945 else nan
               if meanBarVol15m > 0.0 && not (Double.IsNaN pos.Vol20mAvgAtEntry) then pos.Vol20mAvgAtEntry / meanBarVol15m else nan)
          RunMaxDist = pos.RunMaxDistAtEntry
          RunAtr = pos.RunAtrAtEntry
          RunDistPerAtr =
              (if pos.RunAtrAtEntry > 0.0 && not (Double.IsNaN pos.RunAtrAtEntry) then pos.RunMaxDistAtEntry / pos.RunAtrAtEntry else nan)
          RunUpVol = pos.RunUpVolAtEntry
          RunDnVol = pos.RunDnVolAtEntry
          RunUpDnRatio =
              (if pos.RunDnVolAtEntry > 0.0 && not (Double.IsNaN pos.RunUpVolAtEntry) then pos.RunUpVolAtEntry / pos.RunDnVolAtEntry else nan)
          PriceSlope20 = pos.PriceSlope20AtEntry
          VolSlope20 = pos.VolSlope20AtEntry
          SlopePerAtr =
              (if pos.AtrPctAtEntry > 0.0 && not (Double.IsNaN pos.AtrPctAtEntry) && not (Double.IsNaN pos.PriceSlope20AtEntry)
               then pos.PriceSlope20AtEntry / pos.AtrPctAtEntry else nan)
          EmaMin20 = pos.EmaMin20AtEntry
          EmaClimb =
              (if pos.EmaAtEntry > 0.0 && not (Double.IsNaN pos.EmaAtEntry) && not (Double.IsNaN pos.EmaMin20AtEntry)
               then (pos.EmaAtEntry - pos.EmaMin20AtEntry) / pos.EmaAtEntry else nan)
          EmaClimbPerRunAtr =
              (let climb = if pos.EmaAtEntry > 0.0 && not (Double.IsNaN pos.EmaAtEntry) && not (Double.IsNaN pos.EmaMin20AtEntry) then (pos.EmaAtEntry - pos.EmaMin20AtEntry) / pos.EmaAtEntry else nan
               if pos.RunAtrAtEntry > 0.0 && not (Double.IsNaN pos.RunAtrAtEntry) && not (Double.IsNaN climb) then climb / pos.RunAtrAtEntry else nan)
          EmaClimbPerLogAtr =
              (let climb = if pos.EmaAtEntry > 0.0 && not (Double.IsNaN pos.EmaAtEntry) && not (Double.IsNaN pos.EmaMin20AtEntry) then (pos.EmaAtEntry - pos.EmaMin20AtEntry) / pos.EmaAtEntry else nan
               if pos.AtrPctAtEntry > 0.0 && not (Double.IsNaN pos.AtrPctAtEntry) && not (Double.IsNaN climb) then climb / pos.AtrPctAtEntry else nan)
          DistPerLogAtr15 =
              (if pos.AtrLog15AtEntry > 0.0 && not (Double.IsNaN pos.AtrLog15AtEntry) && not (Double.IsNaN pos.RunMaxDistAtEntry) then pos.RunMaxDistAtEntry / pos.AtrLog15AtEntry else nan)
          DistPerLogAtr30 =
              (if pos.AtrLog30AtEntry > 0.0 && not (Double.IsNaN pos.AtrLog30AtEntry) && not (Double.IsNaN pos.RunMaxDistAtEntry) then pos.RunMaxDistAtEntry / pos.AtrLog30AtEntry else nan)
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
    | Armed _ | Skipped -> failwith "toTrip called on an Armed/Skipped (never-filled) position (filter these out)"

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
    + "entry_time,entry_price,entry_bar_open,prev_bar_close,chg_20m,run_low_at_entry,intraday_atr_pct_at_entry,intraday_tightness_at_entry,"
    + "rvol,breakout_bar_vol,new_vol_high,vol_vs_high,run_below_vwap,stop_dist_pct,bar_rvol_15m,rvol20m_20d,rvol20m_15m,run_max_dist,run_atr,run_dist_per_atr,run_up_vol,run_dn_vol,run_updn_ratio,price_slope_20,vol_slope_20,slope_per_atr,ema_min_20,ema_climb,ema_climb_per_run_atr,ema_climb_per_log_atr,dist_per_log_atr15,dist_per_log_atr30,cum_vol_to_entry,pct_chg_since_open,close_1d,close_3d,close_7d,chg_1d,chg_3d,chg_7d,"
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
        string t.RunBelowVwap
        fmt t.StopDistPct
        fmt t.BarRvol15m
        fmt t.Rvol20m20d
        fmt t.Rvol20m15m
        fmt t.RunMaxDist
        fmt t.RunAtr
        fmt t.RunDistPerAtr
        fmt t.RunUpVol
        fmt t.RunDnVol
        fmt t.RunUpDnRatio
        fmt t.PriceSlope20
        fmt t.VolSlope20
        fmt t.SlopePerAtr
        fmt t.EmaMin20
        fmt t.EmaClimb
        fmt t.EmaClimbPerRunAtr
        fmt t.EmaClimbPerLogAtr
        fmt t.DistPerLogAtr15
        fmt t.DistPerLogAtr30
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
