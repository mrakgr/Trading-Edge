module TradingEdge.TideFlyer.Backtest

open System
open System.Globalization
open System.Collections.Generic
open DuckDB.NET.Data
open TradingEdge.TideFlyer.Types

/// Full system configuration. The locked production default is `defaultConfig`.
type Config =
    { StopLowWindow: int        // prior-window low (CSV snapshot reference only)
      HiCloseWindow: int
      AtrWindow: int
      TightnessWindow: int
      VolDays: int              // rvol/ADV baseline window, in BARS (was 28 CALENDAR
                                // days via CalendarMeanMa; now a fixed AvgMa bar count
                                // ~20, approximating the old 28-day trailing mean)
      MaxHoldBars: int          // time-stop: exit at next open after this many Holding bars (0 = off)
      ExitMode: ExitMode        // how the TARGET exit fills (Run 17/18): NextOpenClose (close-cross ->
                                // next open, the original), Moc (close-cross -> that close), Limit f
                                // (resting limit at entry + f*(7d-extreme - entry), fill same-bar intraday),
                                // or Off (pure time-stop). time-stop is always the fallback.
      UsePartialEntry: bool     // true = decide + fill on the partial checkpoint candle (the experiment);
                                // false = the parity path (full daily bar drives the entry)
      PartialTable: string      // which checkpoint table to read (partial_candle_1000 / 1030 / ...)
      BreadthMax: float         // market-breadth CEILING (Run 19): only enter when LAG-1 pct_above_20
                                // (fraction of the universe above its 20d MA, the state going INTO the
                                // day) < this. Default 0.10 — TideFlyer is a CAPITULATION buyer: a deep
                                // washout reverts BEST when the broad tape is ALSO puking (<10% above
                                // 20d MA), i.e. the name got dragged down systemically, not for an
                                // idiosyncratic reason (INVERTS HighFlyer). PF 2.30->5.31. +inf disables.
      Notional: float
      Entry: EntryConfig }

/// The locked production default (mirrors the original HighFlyer defaultConfig
/// after debloat): the entry factor stack, NO price stop, a 5d time-stop, MTM.
let defaultConfig =
    { StopLowWindow = 4
      HiCloseWindow = 252
      AtrWindow = 14
      TightnessWindow = 14
      // VolDays is now a BAR count feeding AvgMa (was 28 calendar days via
      // CalendarMeanMa). 20 approximates the old window's high-bar-count case
      // (a 28-cal-day window holds a median of 19, max 20 trading bars). This
      // SHIFTS the rvol/ADV baseline and thus entry selection — re-validated
      // against live_scan.py (ROWS 20 PRECEDING AND 1 PRECEDING) to 0.0.
      VolDays = 20
      // 5d time-stop: the high-edge breakouts carry ~61% of P&L (PF 1.83) in the
      // first 5 days; the rest is a low-edge grind. NoStop is gone — it was just a
      // time-stop with an infinite cap, so a price stop never exists here.
      MaxHoldBars = 5
      // TideFlyer default exit = the time-stop (fixed N-day hold). --target-exit turns on
      // the round-trip "sell at the opposite 7d extreme" path (time-stop as fallback).
      ExitMode = NextOpenClose 1.0 // ON (Run 17): sell at the 7d HIGH (round-trip the washout->recovery),
                                  // 5d time-stop as fallback. +0.058 PF over pure time-stop (2.237->2.295),
                                  // no capacity cost, distribution-wide & era-robust. Run 1 predicted the exit
                                  // matters once entries are gated. --exit-mode {off|next-open|moc|limit} + --target-frac.
      // Entry basis OFF by default = the parity path (full daily bar). --partial-entry
      // switches the decision + fill to the 10:00 ET partial candle.
      UsePartialEntry = false
      PartialTable = "partial_candle_1000"
      BreadthMax = 0.10           // capitulation gate (Run 19): enter only when lag-1 pct_above_20 < 0.10
      Notional = 10_000.0
      // TideFlyer baseline = the PURE 7d-channel signal. Every momentum-era gate is
      // NEUTRALIZED (inherited from HighFlyerV2) so Run 1 measures the raw dip signal;
      // they become post-hoc tuning levers. Only price>=$1 + ADV kept as liquidity floors.
      Entry =
        { UpThreshold = -0.40         // 1d-return FLOOR: require close/prevClose-1 >= -40% — the falling-knife
                                      // cut (Run 3). Below -40% a 1d collapse is a genuine breakdown that
                                      // keeps falling (PF 0.976), not a reversible dip. (--up-threshold tunes.)
          MaxUpThreshold = -0.05      // 1d-return CEILING: require close/prevClose-1 < -5% — a real DOWN
                                      // day INTO the 7d low (the base prune; --max-up-threshold to tune).
                                      // For mirror mode you'd raise this back to +inf.
          RvolMin = 0.0               // rvol FLOOR OFF (the dead-quiet <0.5 tail is weak but the floor is
                                      // ~absorbed by volfrac; not worth the trips — Run 15)
          RvolMax = 3.0               // rvol CEILING (Run 15): entry_vol / 20d-avg < 3× — cut the CATALYST
                                      // SPIKE. A >3× volume day into a washout = a fundamental catalyst (real
                                      // news that keeps falling), NOT panic/technical selling that reverts.
                                      // rvol (20d avg) and volfrac (7d max) are 0.8-corr'd but rvol's spike-tail
                                      // cut isn't fully captured by volfrac, so this STACKS: PF 2.105 -> 2.237.
          MinPriorDays = 21           // warmup (need prior-7 window + history)
          MinAvgDollarVolume = 100_000.0   // liquidity floor (kept)
          Min52wPct = 0.0             // 52w-high proximity OFF (momentum gate)
          Use52wHigh = false
          MinPrice = 1.0              // price floor (kept)
          MaxTightness = 9.0          // tightness ceiling (Run 14): loose sanity cap on the >9 trending-knife
          MinAtrPct = 0.08            // ATR% FLOOR (Run 14): >=0.08 — washout-MR wants VIOLENT dislocations
          MaxAtrPct = 0.25            // ATR% CEILING (Run 14): <0.25 — cut the falling-knife extreme
          MinIntradayRet = -infinity  // intraday-fade floor OFF
          MinMaxAtrLog = -infinity    // past-runner floor OFF
          // TideFlyer core signal:
          LowWindow = 7               // 7-day close channel
          Mirror = false              // LONG-MR: buy the new 7d LOW (default)
          RequireChannel = true       // gate on the channel
          VolFracMin = 0.5            // volume-fraction band [0.5, 1.5] (Run 4): dip on ORDINARY volume;
          VolFracMax = 1.5            // cut the quiet slow-bleed (<0.5) + the panic-spike knife (>~2.5).
          RequireMonotone = false     // strict monotone-descent gate d3>d2>d1 OFF by default (Run 20): a
                                      // sizing/selection signal (PF 5.78 vs 4.29) but costs ~27% of an
                                      // already-thin book; --require-monotone turns it into a hard gate.
          Max3dReturn = -0.15         // 3d washout CEILING (Run 5): close/close-3d-1 <= -15% (deeper=better,
                                      // no knife). --max-3d-return to tune; large value disables.
          MaxPrior2dReturn = -0.10    // prior-2-day-fall CEILING (Run 9): (3d - 1d) <= -10% — already
                                      // sliding into today's flush (deeper=better, PF 1.61-1.75 for
                                      // already-down 10-20%+). --max-prior2d-return to tune; large disables.
          Max60dReturn = -0.40 } }    // 60d washout CEILING (Run 11): close/close-60d-1 <= -40% — a deep
                                      // longer-term collapse. TideFlyer is a WASHOUT book (deeper=better
                                      // every horizon). 16.7k trips @ PF ~1.96. --max-60d-return; large disables.

/// A finished trip, ready for the CSV. Mirrors the original HighFlyer base
/// columns so the two outputs diff directly.
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
      VolMaxAtEntry: float
      RvolAtEntry: float
      AvgDollarVolumeAtEntry: float
      PctUpAtEntry: float
      AtrPctAtEntry: float
      TightnessAtEntry: float
      Pct52wAtEntry: float
      Pct52wHighAtEntry: float
      Pct52wLowCloseAtEntry: float
      Pct52wLowAtEntry: float
      ExitReason: string
      Open: bool }

/// Convert a closed Position into a Trip. `barsHeld` = exit index − entry index,
/// recovered from the per-ticker date list.
let private toTrip (symbol: string) (notional: float)
                   (barIndex: IReadOnlyDictionary<DateOnly,int>) (p: Position) : Trip =
    match p.State with
    | Exited (exitDate, exitPrice, reason) ->
        let qty = notional / p.EntryPrice
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
          NetPnL = qty * (exitPrice - p.EntryPrice)   // long only
          BarsHeld = barIndex.[exitDate] - barIndex.[p.EntryDate]
          EntryVolume = p.EntryVolume
          VolMaxAtEntry = p.VolMaxAtEntry
          RvolAtEntry = p.RvolAtEntry
          AvgDollarVolumeAtEntry = p.AvgDollarVolumeAtEntry
          PctUpAtEntry = p.PctUpAtEntry
          AtrPctAtEntry = p.AtrPctAtEntry
          TightnessAtEntry = p.TightnessAtEntry
          Pct52wAtEntry = p.Pct52wAtEntry
          Pct52wHighAtEntry = p.Pct52wHighAtEntry
          Pct52wLowCloseAtEntry = p.Pct52wLowCloseAtEntry
          Pct52wLowAtEntry = p.Pct52wLowAtEntry
          ExitReason = reason
          Open = (reason = "mtm") }
    | _ -> failwith "toTrip called on a non-Exited position (Finalize first)"

// ===========================================================================
// Pipeline stage 1 — DbEmitter: the ONLY database read.
//
// Streams the daily universe (split_adjusted_prices, CS/ADRC only) ordered by
// (ticker, date) and pushes each (ticker, Bar) downstream via `onNext`. It owns
// nothing but the read. Continuation-passing style isolates the DB from the rest.
// ===========================================================================
type DbEmitter(conn: DuckDBConnection, startDate: DateOnly, endDate: DateOnly, partialTable: string) =

    // Public accessors for the ctor-captured state: `Process` is `inline`.
    member val Conn = conn
    member val StartDate = startDate
    member val EndDate = endDate

    // Universe = common stock + ADRs only. NOTE the SEMI-JOIN (EXISTS), not an
    // inner JOIN: a few tickers (e.g. ASND) have BOTH a 'CS' and an 'ADRC' row in
    // ticker_reference; an inner join would FAN OUT every price row into two
    // identical consecutive rows. EXISTS filters without multiplying.
    //
    // The partial checkpoint candle LEFT-JOINs in (partial_candle_HHMM, RAW minute
    // prices). d.close is the raw daily close, so adjRatio = adj_close/raw_close
    // puts the partial OHLC on the daily adjusted scale. A missing partial row, or
    // a row with NULL open (no RTH bar before the cutoff — halted/illiquid), reads
    // as ValueNone => not tradeable on the partial basis that day. The table name is
    // a trusted internal config value (partial_candle_1000/1030/...), not user free
    // text, so direct interpolation is safe here.
    member val Sql =
        sprintf
            "SELECT p.ticker, p.date, p.adj_open, p.adj_high, p.adj_low, p.adj_close, p.adj_volume,
                    d.close AS raw_close,
                    pc.open AS pc_open, pc.high AS pc_high, pc.low AS pc_low, pc.close AS pc_close, pc.volume AS pc_vol
             FROM split_adjusted_prices p
             JOIN daily_prices d ON d.ticker = p.ticker AND d.date = p.date
             LEFT JOIN %s pc ON pc.ticker = p.ticker AND pc.date = p.date
             WHERE EXISTS (SELECT 1 FROM ticker_reference r
                           WHERE r.ticker = p.ticker AND r.type IN ('CS','ADRC'))
               AND p.date >= $start AND p.date <= $end
             ORDER BY p.ticker, p.date" partialTable

    /// Stream every daily row, pushing (ticker, dailyBar, partialBar) downstream in
    /// (ticker, date) order. The partial bar is ValueNone when no usable 10:00 ET
    /// candle exists for that (ticker, date). `inline` so onNext fuses into the read loop.
    member inline this.Process(onNext: string * Bar * Bar voption -> unit) =
        use cmd = this.Conn.CreateCommand()
        cmd.CommandText <- this.Sql
        let pStart = cmd.CreateParameter() in pStart.ParameterName <- "start"; pStart.Value <- this.StartDate; cmd.Parameters.Add pStart |> ignore
        let pEnd   = cmd.CreateParameter() in pEnd.ParameterName   <- "end";   pEnd.Value   <- this.EndDate;   cmd.Parameters.Add pEnd   |> ignore
        use reader = cmd.ExecuteReader()
        while reader.Read() do
            let ticker = reader.GetString 0
            let date   = DateOnly.FromDateTime(reader.GetDateTime 1)
            let bar : Bar =
                { date     = date
                  ``open`` = reader.GetDouble 2
                  high     = reader.GetDouble 3
                  low      = reader.GetDouble 4
                  close    = reader.GetDouble 5
                  volume   = reader.GetInt64 6 }
            // Partial candle, split-adjusted to the daily scale. ValueNone if the
            // LEFT JOIN missed OR open is NULL (no RTH bar before 10:00).
            let partial : Bar voption =
                if reader.IsDBNull 8 then ValueNone               // pc.open NULL
                else
                    let rawClose = reader.GetDouble 7
                    if rawClose = 0.0 then ValueNone
                    else
                        let r = bar.close / rawClose               // adj_close / raw_close
                        ValueSome
                            { date     = date
                              ``open`` = reader.GetDouble 8 * r
                              high     = reader.GetDouble 9 * r
                              low      = reader.GetDouble 10 * r
                              close    = reader.GetDouble 11 * r
                              volume   = reader.GetInt64 12 }
            onNext (ticker, bar, partial)

/// Run the whole backtest in a single streaming pass over split_adjusted_prices
/// (ordered by ticker, date), one HighFlyer per ticker. Returns all trips.
/// The CAPITULATION-breadth gate (Run 19) is applied in-engine: an entry fires only
/// when the LAG-1 market breadth (pct_above_20 the PRIOR day) < cfg.BreadthMax.
let run (dbPath: string) (cfg: Config) (startDate: DateOnly) (endDate: DateOnly) : Trip[] =
    let connStr = $"Data Source={dbPath};ACCESS_MODE=READ_ONLY"
    use conn = new DuckDBConnection(connStr)
    conn.Open()
    do
        use pragma = conn.CreateCommand()
        pragma.CommandText <- "PRAGMA memory_limit='6GB'"
        pragma.ExecuteNonQuery() |> ignore

    // Load the LAG-1 breadth series once: for each date, the pct_above_20 of the PRIOR
    // trading day (the market state going INTO the day — no lookahead). +inf BreadthMax
    // disables the gate (every day passes). Missing dates (no breadth row) fail the gate
    // when active, matching the post-hoc join's INNER semantics.
    let breadthLag1 = Dictionary<DateOnly, float>()
    if not (Double.IsInfinity cfg.BreadthMax) then
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT date, LAG(pct_above_20) OVER (ORDER BY date) AS b_lag1 \
             FROM 'data/equity/momentum_v0/breadth.parquet' QUALIFY b_lag1 IS NOT NULL"
        use rdr = cmd.ExecuteReader()
        while rdr.Read() do
            breadthLag1.[DateOnly.FromDateTime(rdr.GetDateTime 0)] <- rdr.GetDouble 1
    let breadthOk (d: DateOnly) =
        Double.IsInfinity cfg.BreadthMax
        || (match breadthLag1.TryGetValue d with
            | true, b -> b < cfg.BreadthMax
            | false, _ -> false)   // no breadth data that day -> no entry when the gate is active

    let emitter = DbEmitter(conn, startDate, endDate, cfg.PartialTable)
    let trips = ResizeArray<Trip>()

    // Per-ticker mutable accumulators, reset at each ticker boundary.
    let mutable curTicker : string = null
    let mutable sys = Unchecked.defaultof<TideFlyer>
    let mutable barIndex = Dictionary<DateOnly,int>()
    let mutable lastBar = Unchecked.defaultof<Bar>
    let mutable barNo = 0

    let newSystem () =
        TideFlyer(cfg.StopLowWindow, cfg.HiCloseWindow, cfg.AtrWindow,
                  cfg.TightnessWindow, cfg.VolDays, cfg.MaxHoldBars, cfg.ExitMode, cfg.Entry)

    // Flush the just-finished ticker: MTM-close open trips, emit all trips.
    let flush () =
        if not (isNull curTicker) then
            sys.Finalize lastBar
            for p in sys.Positions do
                match p.State with
                | Exited _ -> trips.Add(toTrip curTicker cfg.Notional barIndex p)
                | _ -> ()

    emitter.Process(fun (ticker, bar, partial) ->
        if ticker <> curTicker then
            flush ()
            curTicker <- ticker
            sys <- newSystem ()
            barIndex <- Dictionary<DateOnly,int>()
            barNo <- 0
        barIndex.[bar.date] <- barNo
        barNo <- barNo + 1
        lastBar <- bar
        // Parity path: entryBar = the daily bar (default). Experiment: the partial
        // candle drives the entry (ValueNone on days with no usable 10:00 candle ->
        // no entry that day). Exits/indicators always run on the daily bar.
        let entryBar = if cfg.UsePartialEntry then partial else ValueSome bar
        sys.Process(bar, entryBar, breadthOk bar.date))
    flush ()  // last ticker

    trips.ToArray()

// ---------------------------------------------------------------------------
// CSV emission (original-HighFlyer-compatible base columns)
// ---------------------------------------------------------------------------

let private inv = CultureInfo.InvariantCulture
let private fmt (x: float) = if Double.IsNaN x then "nan" else x.ToString("0.################", inv)

let header =
    "symbol,signal_date,entry_date,entry_reason,exit_date,side,entry_price,entry_day_stop_ref,stop_low_at_entry,exit_price,qty,net_pnl,bars_held,"
    + "entry_adj_volume,vol_max_7d_at_entry,rvol_at_entry,avg_dollar_volume_4w_at_entry,pct_up_at_entry,"
    + "atr_pct_14_at_entry,range_pct_14_at_entry,tightness_14_at_entry,pct_52w_at_entry,pct_52w_high_at_entry,pct_52w_low_close_at_entry,pct_52w_low_at_entry,exit_reason,open"

let private row (t: Trip) : string =
    String.concat "," [
        t.Symbol
        t.SignalDate.ToString("yyyy-MM-dd")
        t.EntryDate.ToString("yyyy-MM-dd")
        t.EntryReason
        t.ExitDate.ToString("yyyy-MM-dd")
        "long"                      // long-only engine
        fmt t.EntryPrice
        fmt t.EntryDayStopRef
        fmt t.StopLowAtEntry
        fmt t.ExitPrice
        fmt t.Qty
        fmt t.NetPnL
        string t.BarsHeld
        string t.EntryVolume
        fmt t.VolMaxAtEntry
        fmt t.RvolAtEntry
        fmt t.AvgDollarVolumeAtEntry
        fmt t.PctUpAtEntry
        fmt t.AtrPctAtEntry
        "nan"                       // range_pct_14: not carried (post-hoc only)
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
