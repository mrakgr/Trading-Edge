module TradingEdge.MomentumV2.Backtest

open System
open System.Globalization
open System.Collections.Generic
open DuckDB.NET.Data
open TradingEdge.MomentumV2.Types

/// Full system configuration. The locked v0 default is `defaultConfig`.
type Config =
    { StopLowWindow: int
      TrailWindow: int
      HiCloseWindow: int
      AtrWindow: int
      TightnessWindow: int
      VolDays: int
      ExpansionThr: float
      ExitTimeCap: int          // bars the sell limit may rest; 0 = exit next open (N ignored)
      EntryLimitMode: bool      // true = rest a trailing buy/sell limit instead of buying the close
      EntryTrailWindow: int     // prior-window-low window the entry buy limit rests at (drags down)
      EntryTimeCap: int         // bars the entry limit may rest; on timeout enter at the next open
      UseEntryDayStop: bool     // true = stop floored at entry-day low (Qulla); false = trailing low only
      StopMode: StopMode        // WindowLow (default) or AtrRatchet k — the trailing-stop mechanism
      MaxHoldBars: int          // time-stop: exit at next open after this many Holding bars (0 = off)
      ProfitTarget: float       // fixed profit target as a fraction of entry (0 = off); resting limit, fills intrabar
      TargetNextOpen: bool      // true = target hit exits at the NEXT open (signal); false = intrabar limit fill
      Exhaustion: ExhaustionConfig  // conditional exhaustion exit (loose-base blow-off), off by default
      Disaster: DisasterConfig  // conditional disaster exit (high-ATR + under water), on by default
      Side: Side                // Long (default) or Short — flips stop geometry + P&L sign
      TightnessMode: TightnessMode  // Log (default) or Linear — drives entry tightness + expansion
      Notional: float
      Entry: EntryConfig }

/// The locked production default: Qulla day-low stop, stop-window 4, exit at the
/// NEXT OPEN on a stop (ExitTimeCap=0 — no trailing limit), log-space entry filters
/// (ATR% < 0.11, tightness < 4.0), expansion exit at 8.0 (log-tightness scale).
let defaultConfig =
    { StopLowWindow = 4
      TrailWindow = 1
      HiCloseWindow = 252
      AtrWindow = 14
      TightnessWindow = 14
      VolDays = 28
      // Expansion exit threshold, on the tightness scale (now Linear; live range ~1.4–13).
      // OFF (+inf). Under the realistic next-open baseline (ExitTimeCap=0) the expansion
      // exit only ever cuts winners early: PF climbs monotonically as the threshold
      // loosens and converges to off, in BOTH log and linear space, and even with the
      // entry-price-floored position-relative range. It is a tested dead end (see the
      // doc). The old 0.70 fired every bar; a thr=8 "peak" was a trailing-limit fill artifact.
      ExpansionThr = infinity
      // Tightness measure for the entry filter AND the expansion exit. LINEAR is the
      // default: it separates the loose-base losing tail far more cleanly than Log
      // (log compresses the blow-out region, masking the tail — see the v0-reproduction
      // sanity check). The <4.0 cutoff carries over from Log essentially unchanged
      // (linear <4.0 = PF 1.758 / 2,253 trips vs log's 1.734 / 2,260). Log mode stays
      // reachable via --tightness-mode log for comparison, but should not be the default.
      TightnessMode = Linear
      // Baseline exit = sell at the NEXT OPEN on a stop. 0 = no trailing limit
      // (TrailWindow/N ignored). The trailing limit was a ≤+1% refinement, not the
      // edge, and the realistic-fill rewrite retired it as the default.
      ExitTimeCap = 0
      // Limit-entry mode: OFF by default (production buys the signal-bar close).
      // When on, a buy limit rests at the trailing prior-`EntryTrailWindow`-day low
      // (drags down each bar) for up to `EntryTimeCap` bars, then enters at the
      // next open ("open_after_cap"). EntryTrailWindow defaults to the stop window
      // (4) so the entry pullback level is symmetric with the trailing stop.
      EntryLimitMode = false
      EntryTrailWindow = 4
      EntryTimeCap = 5
      // Qulla initial stop: floor the trailing stop at the entry-day low. Default on.
      UseEntryDayStop = true
      // Trailing-stop mechanism. NEW DEFAULT = NoStop. The whole stop-mechanics study
      // (2026-06-19) showed moving stops around doesn't help and too-tight stops hurt;
      // the edge is the breakout pop in the first ~5 days, so a 5d time-stop captures it
      // and recycles capital. Price stops only earn their keep at ATR% > ~8-10% — handled
      // by the conditional Disaster exit, not a trailing stop.
      StopMode = NoStop
      // Time-stop = 5 bars (NEW DEFAULT). Exit at next open after 5 Holding bars: the
      // high-edge breakouts carry ~61% of P&L (PF 1.83) in the first 5 days; the rest is
      // a low-edge grind (PF 1.24).
      MaxHoldBars = 5
      // Profit target OFF by default (0). >0 = fixed fractional target above entry.
      ProfitTarget = 0.0
      // Target fill: intrabar limit by default; true = exit at next open on a target hit.
      TargetNextOpen = false
      // Conditional exhaustion exit OFF by default; thresholds from the loose-base study.
      // (With 5d holds, exhaustion blow-offs aren't a concern — left off.)
      Exhaustion = { Enabled = false; Tightness = 7.5; Rvol = 3.0; MoveLo = 0.05; MoveHi = 0.10; MaxGain = infinity; MinAtrPct = 0.0 }
      // Conditional DISASTER exit OFF by default. The signal is REAL — a held bar that is
      // BOTH volatile (ATR% > 0.10) AND under water (gain < −0.10) has fwd-10d PF 0.70 /
      // median −4.9% (a genuine short setup) — but a 5d time-stop already caps the exposure
      // window before that continuation compounds, so as a LONG exit it is redundant
      // (1.775 → 1.770). Kept as a tested, off-by-default exit (toggle with the CLI flags).
      Disaster = { Enabled = false; AtrThr = 0.10; LossThr = -0.10 }
      // Trade direction. Long is the production system; Short mirrors the stop geometry
      // (trail the prior-window HIGH) and flips the P&L sign — used for the short studies.
      Side = Long
      Notional = 10_000.0
      Entry =
          // Entry-day-move floor. Raised 0.05 → 0.10 by the post-hoc pct_up sweep:
          // the weak band is the MODEST movers (~8–11% breakouts, PF ~1.15), not the
          // big ones (the explosive >20% names are the strongest, PF >2). Lifting the
          // floor improves PF, %-positive months, worst month AND max drawdown together
          // (1.64→1.73 PF, −$41k→−$36k DD); only raw P&L drops (= less exposure).
        { UpThreshold = 0.10
          RvolMin = 6.0
          RvolMax = 20.0
          MinPriorDays = 21
          MinAvgDollarVolume = 100_000.0
          Min52wPct = 0.95
          MinPrice = 5.0
          // Tuned by post-hoc SQL sweep. ATR% is LOG-space (log-true-range; < 0.11 is
          // clear-cut — the high-vol tail is the biggest single drag, and a 0.11 ceiling
          // is a clean interior optimum: total P&L peaks there and falls off both sides —
          // see "Filter-ceiling sweep" 2026-06-20). Tightness is LINEAR (TightnessMode =
          // Linear above).
          // 2026-06-20: RAISED 4.0 → 4.5 (the clean half of the capacity gain). On the
          // PRODUCTION gate (price% ≥ 0.10, rvol [6,20]), tightness bands by era show the
          // 4.0–4.5 increment is clean (post-2015 PF 1.808 ≈ the <4.0 core's 1.848) while
          // the NEXT band 4.5–5.0 is the soft spot (post-2015 PF 1.222). So 4.5 takes the
          // +24% trips (2,233 → 2,780) / +$79k P&L for almost no quality loss (PF 1.859 →
          // 1.795, post 1.848 → 1.808, mean $225 → $209) and STOPS before the drag.
          // Pushing to 5.5 adds more capacity (3,550 trips, +$701k) but pays for it —
          // post-2015 PF 1.678, mean $197 — by carrying the 4.5–5.0 soft band plus the
          // spiky 5.0–5.5 one. <4.0 remains the max-PF / min-DD choice (PF 1.859, post
          // 1.848); <5.5 is the max-capacity choice. Gate-dependence: on the LOOSE gate
          // (price% ≥ 0.05, rvol [3,20]) loosening past 4.0 degrades post-2015 PF (5.0–5.5
          // reverts to 1.23) — the prod move/rvol floor is what keeps these names clean.
          MaxTightness = 4.5
          MaxAtrPct = 0.11 } }

/// A finished trip, ready for the CSV. Mirrors v0's base trip columns so the
/// two outputs diff directly.
type Trip =
    { Symbol: string
      SignalDate: DateOnly
      EntryDate: DateOnly
      EntryReason: string
      ExitDate: DateOnly
      EntryPrice: float
      EntryDayStopRef: float
      StopLowAtEntry: float
      ExitPrice: float
      Qty: float
      NetPnL: float
      BarsHeld: int
      EntryVolume: int64
      RvolAtEntry: float
      AvgDollarVolumeAtEntry: float
      PctUpAtEntry: float
      AtrPctAtEntry: float
      TightnessAtEntry: float
      Pct52wAtEntry: float
      Pct52wHighAtEntry: float
      Pct52wLowCloseAtEntry: float
      Pct52wLowAtEntry: float
      Side: Side
      ExitReason: string
      Open: bool }

/// Convert a closed Position into a Trip. `barsHeld` is the number of trading
/// bars the ticker saw between entry and exit (exit index − entry index),
/// recovered from the per-ticker date list.
let private toTrip (symbol: string) (notional: float) (side: Side)
                   (barIndex: IReadOnlyDictionary<DateOnly,int>) (p: Position) : Trip =
    match p.State with
    | Exited (exitDate, exitPrice, reason) ->
        let qty = notional / p.EntryPrice
        // Long profits when price rises (exit−entry); Short when it falls (entry−exit).
        let dir = match side with Long -> 1.0 | Short -> -1.0
        { Symbol = symbol
          SignalDate = p.SignalDate
          EntryDate = p.EntryDate
          EntryReason = p.EntryReason
          ExitDate = exitDate
          EntryPrice = p.EntryPrice
          EntryDayStopRef = p.EntryDayStopRef
          StopLowAtEntry = p.StopLowAtEntry
          ExitPrice = exitPrice
          Qty = qty
          NetPnL = qty * dir * (exitPrice - p.EntryPrice)
          BarsHeld = barIndex.[exitDate] - barIndex.[p.EntryDate]
          EntryVolume = p.EntryVolume
          RvolAtEntry = p.RvolAtEntry
          AvgDollarVolumeAtEntry = p.AvgDollarVolumeAtEntry
          PctUpAtEntry = p.PctUpAtEntry
          AtrPctAtEntry = p.AtrPctAtEntry
          TightnessAtEntry = p.TightnessAtEntry
          Pct52wAtEntry = p.Pct52wAtEntry
          Pct52wHighAtEntry = p.Pct52wHighAtEntry
          Pct52wLowCloseAtEntry = p.Pct52wLowCloseAtEntry
          Pct52wLowAtEntry = p.Pct52wLowAtEntry
          Side = side
          ExitReason = reason
          Open = (reason = "mtm") }
    | _ -> failwith "toTrip called on a non-Exited position (Finalize first)"

/// Run the whole backtest in a single streaming pass over split_adjusted_prices
/// (ordered by ticker, then date), one QullaSystem per ticker. Returns all
/// trips across all tickers. breadth is applied later (post-hoc); the engine
/// runs with no breadth gate here.
let run (dbPath: string) (cfg: Config) (startDate: DateOnly) (endDate: DateOnly) : Trip[] =
    let connStr = $"Data Source={dbPath};ACCESS_MODE=READ_ONLY"
    use conn = new DuckDBConnection(connStr)
    conn.Open()
    (use pragma = conn.CreateCommand()
     pragma.CommandText <- "PRAGMA memory_limit='6GB'"
     pragma.ExecuteNonQuery() |> ignore)

    use cmd = conn.CreateCommand()
    // Universe = common stock + ADRs only (v0's tradableOnly default:
    // ticker_reference.type IN ('CS','ADRC')). Excludes ETFs, units, warrants,
    // preferreds, etc. — without this v1 trades ~3x more tickers than v0.
    cmd.CommandText <-
        "SELECT p.ticker, p.date, p.adj_open, p.adj_high, p.adj_low, p.adj_close, p.adj_volume
         FROM split_adjusted_prices p
         JOIN ticker_reference r ON r.ticker = p.ticker
         WHERE r.type IN ('CS','ADRC')
           AND p.date >= $start AND p.date <= $end
         ORDER BY p.ticker, p.date"
    let pStart = cmd.CreateParameter() in pStart.ParameterName <- "start"; pStart.Value <- startDate; cmd.Parameters.Add pStart |> ignore
    let pEnd   = cmd.CreateParameter() in pEnd.ParameterName   <- "end";   pEnd.Value   <- endDate;   cmd.Parameters.Add pEnd   |> ignore

    let trips = ResizeArray<Trip>()

    // Per-ticker mutable accumulators, reset at each ticker boundary.
    let mutable curTicker : string = null
    let mutable sys = Unchecked.defaultof<QullaSystem>
    let mutable barIndex = Dictionary<DateOnly,int>()
    let mutable lastBar = Unchecked.defaultof<Bar>
    let mutable barNo = 0

    let newSystem () =
        QullaSystem(cfg.StopLowWindow, cfg.TrailWindow, cfg.HiCloseWindow,
                    cfg.AtrWindow, cfg.TightnessWindow, cfg.VolDays,
                    cfg.ExpansionThr, cfg.ExitTimeCap, cfg.EntryLimitMode,
                    cfg.EntryTrailWindow, cfg.EntryTimeCap, cfg.UseEntryDayStop,
                    cfg.StopMode, cfg.MaxHoldBars, cfg.ProfitTarget, cfg.TargetNextOpen,
                    cfg.Exhaustion, cfg.Disaster, cfg.Side, cfg.TightnessMode, cfg.Entry)

    // Flush the just-finished ticker: MTM-close open trips, emit all trips.
    let flush () =
        if not (isNull curTicker) then
            sys.Finalize lastBar
            for p in sys.Positions do
                // In limit-entry mode some signals never filled (the pullback never
                // came AND the timed-open never resolved before the data ended) —
                // those stay non-Exited after Finalize and are NOT trips. Skip them.
                match p.State with
                | Exited _ -> trips.Add(toTrip curTicker cfg.Notional cfg.Side barIndex p)
                | _ -> ()

    use reader = cmd.ExecuteReader()
    while reader.Read() do
        let ticker = reader.GetString 0
        if ticker <> curTicker then
            flush ()
            curTicker <- ticker
            sys <- newSystem ()
            barIndex <- Dictionary<DateOnly,int>()
            barNo <- 0
        let bar =
            { date   = DateOnly.FromDateTime(reader.GetDateTime 1)
              ``open`` = reader.GetDouble 2
              high   = reader.GetDouble 3
              low    = reader.GetDouble 4
              close  = reader.GetDouble 5
              volume = reader.GetInt64 6 }
        barIndex.[bar.date] <- barNo
        barNo <- barNo + 1
        lastBar <- bar
        sys.Process bar
    flush ()  // last ticker

    trips.ToArray()

// ---------------------------------------------------------------------------
// CSV emission (v0-compatible base columns)
// ---------------------------------------------------------------------------

let private inv = CultureInfo.InvariantCulture
let private fmt (x: float) = if Double.IsNaN x then "nan" else x.ToString("0.################", inv)

let header =
    "symbol,signal_date,entry_date,entry_reason,exit_date,side,entry_price,entry_day_stop_ref,stop_low_at_entry,exit_price,qty,net_pnl,bars_held,"
    + "entry_adj_volume,rvol_at_entry,avg_dollar_volume_4w_at_entry,pct_up_at_entry,"
    + "atr_pct_14_at_entry,range_pct_14_at_entry,tightness_14_at_entry,pct_52w_at_entry,pct_52w_high_at_entry,pct_52w_low_close_at_entry,pct_52w_low_at_entry,exit_reason,open"

let private row (t: Trip) : string =
    String.concat "," [
        t.Symbol
        t.SignalDate.ToString("yyyy-MM-dd")
        t.EntryDate.ToString("yyyy-MM-dd")
        t.EntryReason
        t.ExitDate.ToString("yyyy-MM-dd")
        (match t.Side with Long -> "long" | Short -> "short")
        fmt t.EntryPrice
        fmt t.EntryDayStopRef
        fmt t.StopLowAtEntry
        fmt t.ExitPrice
        fmt t.Qty
        fmt t.NetPnL
        string t.BarsHeld
        string t.EntryVolume
        fmt t.RvolAtEntry
        fmt t.AvgDollarVolumeAtEntry
        fmt t.PctUpAtEntry
        fmt t.AtrPctAtEntry
        "nan"                       // range_pct_14: not carried by v1 (post-hoc only)
        fmt t.TightnessAtEntry
        fmt t.Pct52wAtEntry
        fmt t.Pct52wHighAtEntry
        fmt t.Pct52wLowCloseAtEntry
        fmt t.Pct52wLowAtEntry
        t.ExitReason
        (if t.Open then "1" else "0")
    ]

let writeCsv (path: string) (trips: Trip[]) =
    use w = new IO.StreamWriter(path)
    w.WriteLine header
    for t in trips do w.WriteLine(row t)
