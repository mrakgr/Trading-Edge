module TradingEdge.FlushFader.Backtest

open System
open System.Collections.Generic
open DuckDB.NET.Data
open TradingEdge.FlushFader.Intraday

// ===========================================================================
// FlushFader backtest wiring (template: DipRiderV6/Backtest.fs via SurgeRider).
//
// Pipeline 1 is the same pure-SQL read of `diprider_v6_candidate` (built by
// scripts/equity/build_diprider_v5_candidate.fsx — the leak-audited universe:
// dv_0945 floor, no avgvol20 anywhere). Pipeline 2 streams each candidate
// day's PRESENT 1-second bars from data/intraday_1s_slim/ into IntradaySystem.
//
// ⭐ OUTPUT IS PARQUET, NOT CSV (user, 2026-07-23): at 1s granularity the
// sampler emits orders of magnitude more trips than the 1m systems — a CSV
// would be multi-GB and every post-hoc query would pay a full text parse.
// Trips STREAM into an in-memory DuckDB staging table via the appender (the
// TradesDownload.fs writeTradesToParquet pattern) and rotate to zstd part
// files every RowsPerPart rows, so neither the CLR heap nor the staging table
// ever holds the full book. Post-hoc: read_parquet('<outDir>/*.parquet').
// NaN features are written as NULL — SQL aggregates then skip them natively.
// ===========================================================================

/// FlushFader config = the intraday engine knobs + notional + the daily floor.
type Config =
    { Intraday: IntradayConfig
      Notional: float
      /// ⭐ The daily in-play floor: minimum 09:30-09:45 dollar volume. Default $3M —
      /// DipRiderV6 F14's MANDATORY floor: below it the apparent PF rise is a
      /// penny-stock artifact ($1.13 median entry at <$250k). The per-bar
      /// DvFloor60/TcFloor60 gates do the second-by-second version of the same job.
      MinDv0945: float
      /// ⭐ Optional in-play universe pre-filter: rvol_0945_honest >= this in the candidate
      /// SELECT. 0 = off (THE SAMPLER DEFAULT — rvol stays recorded-not-gated for breadth).
      /// Knowability: same 09:45 class as dv_0945, legal for EntryStartSec >= 35100. Use for
      /// focused sweeps (e.g. --min-rvol-0945 10 narrows the universe ~50x and a full run
      /// drops to seconds per day).
      MinRvol0945: float
      /// ⭐ Universe gate on the PRIOR day's close in day-D's raw (post-split) scale —
      /// prev_adj_close / adj_ratio >= this. Knowable BEFORE the open (D-1 close),
      /// unlike entry price. 0 = off (record-first). 2.0 = the ">=$2 stocks" universe
      /// (sub-$1 is priced out on every EU-accessible broker route).
      MinPrevClose: float }

/// The sampler defaults (mc = 0). Every gate here is a HARD gate; everything
/// else is recorded and sliced post-hoc over the parquet.
let defaultConfig =
    { Intraday =
        { EntryChannelBars = 1200       // ⭐ the ~20m flush channel: entry on its new LOW, leg reset
                                        // on its new HIGH. {60,120,300,600,1200}.
          ExitChannelBars  = 300        // ⭐ the ~5m reversion target (V6 F16's direction).
                                        // {30,60,120,300,600,1200}.
          DvFloor60        = 100_000.0  // >= $100k traded over the last 60 present bars at the signal
          TcFloor60        = 60.0       // >= 60 trades over the same window (1/sec — kills the
                                        // block-print-only tape)
          MinAbsEff20m     = 0.0        // superseded by the SIGNED Eff20 band below (kept for sweeps)
          MinVolat20m      = 0.004      // ⭐ SPEC v1.2 (S18): the 40bp volatility floor
          MaxVolat20m      = Double.PositiveInfinity
          // ⭐ SPEC v1.2 GATES (S18, baked 2026-07-29). Defaults = the production
          // stack; disable individually for sweeps (see IntradayConfig for the
          // off-conventions). Formulas identical to the recorded columns.
          MaxSpeed1m       = -0.02      // flush speed < -2%/1m
          KBandLo          = 26         // lows_since_first_low ∈ [26, 50] — THE 2022 fix
          KBandHi          = 50
          Eff20Lo          = -0.5       // eff_20m ∈ [-0.5, -0.3) — the exhaustion band
          Eff20Hi          = -0.3
          MinAbsEff10m     = 0.15       // |eff_10m| >= 0.15 — no flat 10m tape
          DistHiLo         = -0.35      // dist from 20m high ∈ (-35%, -10%] — inside the
          DistHiHi         = -0.10      // fadeable zone, past the un-fadeable wall
          MinVol10Rate     = 0.75       // last-10s volume rate >= 0.75x the 1m rate (S17/S18)
          MinLows300       = 6          // ⭐ SPEC v1.4: >= 6 lows since the last 5m-high bounce (S38h)
          MaxRngFront      = 0.8        // ⭐ SPEC v1.5: rng_300/rng_20m < 0.8 — no pure cliffs (S38k)
          MinDv0945Tape    = 3e6        // ⭐ THE universe floor (S35): Σ vwap·vol over OUR 1s
                                        // bars < 09:45, honest dollars — replaces the
                                        // candidate dv_0945 gate (real dollars × adj_ratio,
                                        // future-split-dependent; 20% of universe inflated in)
          // ⭐ price-acceptance stops (user, 2026-07-28): a fresh entry-channel low on
          // >=8x 1m/20m participation, or at <-1%/1m pace, is the market ACCEPTING the
          // lower price — stop out. S2 motivation: >=8x lows = PF 0.59-0.87 at entry.
          VolStopRatio     = infinity   // acceptance stops default OFF — S16 A/B:
          TcStopRatio      = infinity   // speed stop fired on 93.7% of the spec book
          SpeedStopPct     = 0.0        // at median 2s; stopped trades won 70% without it
          MaxConcurrent    = 0          // ⭐ SAMPLER. 1 = a real book.
          SlotBars         = 30
          SessionStartSec  = 34200      // 09:30 — features fold from the RTH open
          EntryStartSec    = 35100      // ⭐ 09:45 — THE knowability floor itself (user 2026-07-29:
                                        // 10:00-13:30 was a VwapReclaim-era throwback; use the full day)
          EntryEndSec      = 54000      // ⭐ 15:00 (S31b/S31c: after it, quality and
                                        // completion-room degrade together; 14:30 rejected)
          MocSec           = 57600 }    // 16:00
      Notional = 10_000.0
      MinDv0945 = 0.0               // 💀 DEPRECATED (S35): the candidate column = real
                                    // dollars × adj_ratio (future-split-dependent).
                                    // The floor now lives in MinDv0945Tape. Column
                                    // still recorded for reference.
      MinRvol0945 = 0.0
      MinPrevClose = 0.0 }          // record-first (user 2026-07-29): a prev-close gate
                                    // would drop names flushing DOWN through $1 — the $1
                                    // book stays a POST-HOC entry_px cut (S7c fee wall)

/// One candidate (ticker, day) from diprider_v6_candidate — the daily context
/// that rides along on every trip for post-hoc slicing. Forward closes are
/// REPORTED only.
type Candidate =
    { Ticker: string
      Date: DateOnly
      PrevAdjClose: float
      Close3d: float             // adjusted close 3 trading days back — chg_3d in SQL
      DayClose: float
      AdjRatio: float
      CloseFwd1d: float
      CloseFwd3d: float
      CloseFwd5d: float
      Dv0945: float
      Rvol0945Honest: float }

let private readCandidates (conn: DuckDBConnection) (startDate: DateOnly) (endDate: DateOnly) (minDv0945: float) (minRvol0945: float) (minPrevClose: float) : Candidate[] =
    // Research override: FF_CANDIDATE_TABLE lets a breakdown run against a different
    // universe without disturbing the production table. Identifier-only (injection-safe).
    let table =
        match Environment.GetEnvironmentVariable "FF_CANDIDATE_TABLE" with
        | null | "" -> "diprider_v6_candidate"
        | t when t |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_') -> t
        | bad -> failwithf "Invalid FF_CANDIDATE_TABLE %A (identifier chars only)" bad
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        $"SELECT ticker, date, prev_adj_close, close_3d, day_close, adj_ratio,
                 close_fwd_1d, close_fwd_3d, close_fwd_5d, dv_0945, rvol_0945_honest
          FROM {table}
          WHERE date >= $start AND date <= $end AND dv_0945 >= $mindv
            AND rvol_0945_honest >= $minrvol
            AND coalesce(prev_adj_close / nullif(adj_ratio, 0), 0) >= $minprevclose
          ORDER BY ticker, date"
    let pStart = cmd.CreateParameter() in pStart.ParameterName <- "start"; pStart.Value <- startDate; cmd.Parameters.Add pStart |> ignore
    let pEnd   = cmd.CreateParameter() in pEnd.ParameterName   <- "end";   pEnd.Value   <- endDate;   cmd.Parameters.Add pEnd   |> ignore
    let pDv    = cmd.CreateParameter() in pDv.ParameterName    <- "mindv"; pDv.Value    <- minDv0945; cmd.Parameters.Add pDv    |> ignore
    let pRv    = cmd.CreateParameter() in pRv.ParameterName    <- "minrvol"; pRv.Value  <- minRvol0945; cmd.Parameters.Add pRv  |> ignore
    let pPc    = cmd.CreateParameter() in pPc.ParameterName    <- "minprevclose"; pPc.Value <- minPrevClose; cmd.Parameters.Add pPc |> ignore
    let out = ResizeArray<Candidate>()
    use reader = cmd.ExecuteReader()
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
              Dv0945 = dbl 9
              Rvol0945Honest = dbl 10 })
    out.ToArray()

// ===========================================================================
// The trip parquet sink — appender into an in-memory staging table, rotated to
// zstd part files (trips_p000.parquet, trips_p001.parquet, ...) so nothing
// holds the full book. Column order in appendTrip MUST match the CREATE TABLE.
// ===========================================================================
[<Literal>]
let private RowsPerPart = 2_000_000

let private tripTableSql = """
CREATE TABLE trips (
    symbol VARCHAR, trade_date VARCHAR, adj_ratio DOUBLE,
    signal_sec INTEGER, signal_vwap DOUBLE, entry_sec INTEGER, entry_px DOUBLE,
    volat_20m DOUBLE, volat_10m DOUBLE, rng_20m DOUBLE, eff_20m DOUBLE, eff_10m DOUBLE, slot_count INTEGER,
    rng_sess DOUBLE, rng_600 DOUBLE, rng_300 DOUBLE, rng_120 DOUBLE, rng_60 DOUBLE, rng_30 DOUBLE,
    breach_sess INTEGER, breach_1200 INTEGER, breach_600 INTEGER, breach_300 INTEGER,
    breach_120 INTEGER, breach_60 INTEGER, breach_30 INTEGER,
    breach_lo_sess INTEGER, breach_lo_1200 INTEGER, breach_lo_600 INTEGER, breach_lo_300 INTEGER,
    breach_lo_120 INTEGER, breach_lo_60 INTEGER, breach_lo_30 INTEGER,
    bars_since_first_low INTEGER, lows_since_first_low INTEGER,
    bars_since_first_low_300 INTEGER, lows_since_first_low_300 INTEGER,
    bars_since_first_low_600 INTEGER, lows_since_first_low_600 INTEGER,
    trade_idx INTEGER, open_at_signal INTEGER,
    vwap_1200 DOUBLE, chan_hi DOUBLE, chan_lo DOUBLE, exit_chan_hi DOUBLE,
    gap_60 INTEGER, gap_30 INTEGER, gap_15 INTEGER,
    sess_vwap DOUBLE, dist_sess_vwap DOUBLE, pct_chg_open DOUBLE,
    bar_vol DOUBLE, bar_tc INTEGER,
    vol_5 DOUBLE, vol_10 DOUBLE, vol_15 DOUBLE, vol_30 DOUBLE, vol_60 DOUBLE, vol_600 DOUBLE, vol_1200 DOUBLE,
    tc_5 DOUBLE, tc_10 DOUBLE, tc_15 DOUBLE, tc_30 DOUBLE, tc_60 DOUBLE, tc_600 DOUBLE, tc_1200 DOUBLE,
    vol_60_prev DOUBLE, tc_60_prev DOUBLE, vwap_60 DOUBLE, vwap_60_prev DOUBLE,
    dollar_vol_60 DOUBLE, cum_vol DOUBLE, cum_tc DOUBLE,
    fwd_vwap_60 DOUBLE, fwd_vwap_300 DOUBLE, fwd_vwap_600 DOUBLE, fwd_vwap_1200 DOUBLE,
    aux_hi_120_px DOUBLE, aux_hi_120_sec INTEGER,
    aux_hi_300_px DOUBLE, aux_hi_300_sec INTEGER,
    aux_hi_600_px DOUBLE, aux_hi_600_sec INTEGER,
    aux_hi_1200_px DOUBLE, aux_hi_1200_sec INTEGER,
    ma_10m_px DOUBLE, ma_10m_sec INTEGER,
    ma_20m_px DOUBLE, ma_20m_sec INTEGER,
    ma_30m_px DOUBLE, ma_30m_sec INTEGER,
    ma_40m_px DOUBLE, ma_40m_sec INTEGER,
    ma_50m_px DOUBLE, ma_50m_sec INTEGER,
    ma_60m_px DOUBLE, ma_60m_sec INTEGER,
    vwma_10m_px DOUBLE, vwma_10m_sec INTEGER,
    vwma_20m_px DOUBLE, vwma_20m_sec INTEGER,
    vwma_30m_px DOUBLE, vwma_30m_sec INTEGER,
    vwma_40m_px DOUBLE, vwma_40m_sec INTEGER,
    vwma_50m_px DOUBLE, vwma_50m_sec INTEGER,
    vwma_60m_px DOUBLE, vwma_60m_sec INTEGER,
    exit_sec INTEGER, exit_px DOUBLE, exit_reason VARCHAR,
    ret_exit DOUBLE, bars_held INTEGER,
    prev_adj_close DOUBLE, close_3d DOUBLE, day_close DOUBLE,
    close_fwd_1d DOUBLE, close_fwd_3d DOUBLE, close_fwd_5d DOUBLE,
    dv_0945 DOUBLE, rvol_0945_honest DOUBLE, dv_0945_tape DOUBLE,
    qty DOUBLE, net_pnl DOUBLE
)"""

// ⭐ right-side-of-V continuation trips (user, 2026-07-29): one row per
// (parent, entry_window), the three trailing-stop outcomes as columns —
// 3 rows x 3 stops = the 9 counterfactuals per reversal. Parent join:
// (symbol, trade_date, parent_signal_sec) = trips.(symbol, trade_date, signal_sec).
let private contTableSql = """
CREATE TABLE cont_trips (
    symbol VARCHAR, trade_date VARCHAR, adj_ratio DOUBLE,
    parent_signal_sec INTEGER, parent_entry_sec INTEGER, parent_entry_px DOUBLE,
    entry_window INTEGER, signal_sec INTEGER, signal_vwap DOUBLE,
    entry_sec INTEGER, entry_px DOUBLE,
    stop60_sec INTEGER, stop60_px DOUBLE, stop60_reason VARCHAR,
    stop120_sec INTEGER, stop120_px DOUBLE, stop120_reason VARCHAR,
    stop300_sec INTEGER, stop300_px DOUBLE, stop300_reason VARCHAR
)"""

type ContSink(outDir: string) =
    let conn = new DuckDBConnection("Data Source=:memory:")
    do
        conn.Open()
        IO.Directory.CreateDirectory outDir |> ignore
        use cmd = conn.CreateCommand()
        cmd.CommandText <- contTableSql
        cmd.ExecuteNonQuery() |> ignore
    let mutable appender = conn.CreateAppender "cont_trips"
    let mutable rowsInPart = 0
    let mutable part = 0
    let mutable total = 0L
    let flushPart () =
        appender.Close()
        if rowsInPart > 0 then
            let path = IO.Path.Combine(outDir, sprintf "cont_trips_p%03d.parquet" part).Replace("\\", "/").Replace("'", "''")
            use cmd = conn.CreateCommand()
            cmd.CommandText <- $"COPY cont_trips TO '{path}' (FORMAT PARQUET, COMPRESSION 'zstd'); DELETE FROM cont_trips;"
            cmd.ExecuteNonQuery() |> ignore
            part <- part + 1
            rowsInPart <- 0
    member _.Total = total
    member _.Add (c: Candidate) (q: ContPosition) =
        if q.Stop60Sec < 0 || q.Stop120Sec < 0 || q.Stop300Sec < 0 then
            failwith "ContSink.Add on an unfinished continuation (Flatten first)"
        let row = appender.CreateRow()
        let inline f (x: float) =
            if Double.IsNaN x then row.AppendNullValue() |> ignore
            else row.AppendValue x |> ignore
        let inline i (x: int) = row.AppendValue x |> ignore
        let inline s (x: string) = row.AppendValue x |> ignore
        s c.Ticker
        s (c.Date.ToString "yyyy-MM-dd")
        f c.AdjRatio
        i q.ParentSignalSec; i q.ParentEntrySec; f q.ParentEntryPx
        i q.EntryWindow; i q.SignalSec; f q.SignalVwap
        i q.EntrySec; f q.EntryPx
        i q.Stop60Sec; f q.Stop60Px; s q.Stop60Reason
        i q.Stop120Sec; f q.Stop120Px; s q.Stop120Reason
        i q.Stop300Sec; f q.Stop300Px; s q.Stop300Reason
        row.EndRow()
        total <- total + 1L
        rowsInPart <- rowsInPart + 1
        if rowsInPart >= RowsPerPart then
            flushPart ()
            appender <- conn.CreateAppender "cont_trips"
    interface IDisposable with
        member _.Dispose () =
            flushPart ()
            conn.Dispose()

type TripSink(outDir: string) =
    let conn = new DuckDBConnection("Data Source=:memory:")
    do
        conn.Open()
        IO.Directory.CreateDirectory outDir |> ignore
        use cmd = conn.CreateCommand()
        cmd.CommandText <- tripTableSql
        cmd.ExecuteNonQuery() |> ignore
    let mutable appender = conn.CreateAppender "trips"
    let mutable rowsInPart = 0
    let mutable part = 0
    let mutable total = 0L
    // quick console attribution (⚠ mc=0 = attribution, not portfolio)
    let mutable grossWin = 0.0
    let mutable grossLoss = 0.0
    let mutable wins = 0L

    let flushPart () =
        appender.Close()
        if rowsInPart > 0 then
            let path = IO.Path.Combine(outDir, sprintf "trips_p%03d.parquet" part).Replace("\\", "/").Replace("'", "''")
            use cmd = conn.CreateCommand()
            cmd.CommandText <- $"COPY trips TO '{path}' (FORMAT PARQUET, COMPRESSION 'zstd'); DELETE FROM trips;"
            cmd.ExecuteNonQuery() |> ignore
            part <- part + 1
            rowsInPart <- 0

    member _.Total = total
    member _.Wins = wins
    member _.GrossWin = grossWin
    member _.GrossLoss = grossLoss

    /// Append one finished trip. NaN floats become NULL.
    member _.Add (c: Candidate) (notional: float) (p: FlushPosition) =
        match p.State with
        | Holding | PendingExit _ -> failwith "TripSink.Add on an unfinished position (Flatten first)"
        | ExitedAt (exitSec, exitPx, reason) ->
            let qty = notional / p.EntryPx
            let pnl = qty * (exitPx - p.EntryPx)
            let row = appender.CreateRow()
            let inline f (x: float) =
                if Double.IsNaN x then row.AppendNullValue() |> ignore
                else row.AppendValue x |> ignore
            let inline i (x: int) = row.AppendValue x |> ignore
            let inline s (x: string) = row.AppendValue x |> ignore
            s c.Ticker
            s (c.Date.ToString "yyyy-MM-dd")
            f c.AdjRatio
            i p.SignalSec; f p.SignalVwap; i p.EntrySec; f p.EntryPx
            f p.Volat20m; f p.Volat10m; f p.Rng20m; f p.Eff20m; f p.Eff10m; i p.SlotCount
            f p.RngSess; f p.Rng600; f p.Rng300; f p.Rng120; f p.Rng60; f p.Rng30
            i p.BreachSess; i p.Breach1200; i p.Breach600; i p.Breach300
            i p.Breach120; i p.Breach60; i p.Breach30
            i p.BreachLoSess; i p.BreachLo1200; i p.BreachLo600; i p.BreachLo300
            i p.BreachLo120; i p.BreachLo60; i p.BreachLo30
            i p.BarsSinceFirstLow; i p.LowsSinceFirstLow
            i p.BarsSinceFirstLow300; i p.LowsSinceFirstLow300
            i p.BarsSinceFirstLow600; i p.LowsSinceFirstLow600
            i p.TradeIdx; i p.OpenAtSignal
            f p.Vwap1200; f p.ChanHi; f p.ChanLo; f p.ExitChanHi
            i p.Gap60; i p.Gap30; i p.Gap15
            f p.SessVwap; f p.DistSessVwap; f p.PctChgOpen
            f p.BarVol; i p.BarTc
            f p.Vol5; f p.Vol10; f p.Vol15; f p.Vol30; f p.Vol60; f p.Vol600; f p.Vol1200
            f p.Tc5; f p.Tc10; f p.Tc15; f p.Tc30; f p.Tc60; f p.Tc600; f p.Tc1200
            f p.Vol60Prev; f p.Tc60Prev; f p.Vwap60; f p.Vwap60Prev
            f p.DollarVol60; f p.CumVol; f p.CumTc
            f p.FwdVwap60; f p.FwdVwap300; f p.FwdVwap600; f p.FwdVwap1200
            let inline auxSec (s: int) =
                if s < 0 then row.AppendNullValue() |> ignore else row.AppendValue s |> ignore
            f p.AuxHi120; auxSec p.AuxSec120
            f p.AuxHi300; auxSec p.AuxSec300
            f p.AuxHi600; auxSec p.AuxSec600
            f p.AuxHi1200; auxSec p.AuxSec1200
            f p.Ma10Px; auxSec p.Ma10Sec
            f p.Ma20Px; auxSec p.Ma20Sec
            f p.Ma30Px; auxSec p.Ma30Sec
            f p.Ma40Px; auxSec p.Ma40Sec
            f p.Ma50Px; auxSec p.Ma50Sec
            f p.Ma60Px; auxSec p.Ma60Sec
            f p.Vwma10Px; auxSec p.Vwma10Sec
            f p.Vwma20Px; auxSec p.Vwma20Sec
            f p.Vwma30Px; auxSec p.Vwma30Sec
            f p.Vwma40Px; auxSec p.Vwma40Sec
            f p.Vwma50Px; auxSec p.Vwma50Sec
            f p.Vwma60Px; auxSec p.Vwma60Sec
            i exitSec; f exitPx; s reason
            f (if p.EntryPx > 0.0 then exitPx / p.EntryPx - 1.0 else nan)
            i p.BarsHeld
            f c.PrevAdjClose; f c.Close3d; f c.DayClose
            f c.CloseFwd1d; f c.CloseFwd3d; f c.CloseFwd5d
            f c.Dv0945; f c.Rvol0945Honest; f p.Dv0945Tape
            f qty; f pnl
            row.EndRow()
            total <- total + 1L
            if pnl > 0.0 then wins <- wins + 1L; grossWin <- grossWin + pnl
            else grossLoss <- grossLoss - pnl
            rowsInPart <- rowsInPart + 1
            if rowsInPart >= RowsPerPart then
                flushPart ()
                appender <- conn.CreateAppender "trips"

    interface IDisposable with
        member _.Dispose () =
            flushPart ()
            conn.Dispose()

// ===========================================================================
// Pipeline 2 — SecEmitter -> IntradaySystem -> TripSink. Same drain-on-ticker-
// boundary shape as DipRiderV6's MinuteEmitter loop; the parquet is opened once
// per date and streamed in (ticker, bucket) order (the files are stored sorted).
// ===========================================================================
type SecEmitter
        ( conn: DuckDBConnection, path: string,
          tickers: string[], adjRatio: IDictionary<string, float>,
          sessionStartSec: int, mocSec: int ) =

    member val Conn = conn
    member val AdjRatio = adjRatio

    member val Sql =
        let tickerList = tickers |> Array.map (fun t -> "'" + t.Replace("'", "''") + "'") |> String.concat ","
        // `bucket` IS seconds-since-00:00-ET (the 1s builder already did the
        // timezone work) — no window_start conversion, unlike the 1m emitters.
        sprintf """
        SELECT ticker, bucket, vwap::DOUBLE, volume::DOUBLE, trade_count
        FROM read_parquet('%s')
        WHERE ticker IN (%s) AND bucket >= %d AND bucket <= %d
          AND vwap > 0 AND volume > 0
        ORDER BY ticker, bucket"""
            (path.Replace("'", "''")) tickerList sessionStartSec mocSec

    /// Stream every candidate-ticker present 1s bar for this date, split-
    /// adjusted, in (ticker, bucket) order. `inline` so onNext fuses into the
    /// read loop — this loop runs ~10^6-10^7 times per day.
    member inline this.Process(onNext: string * SecBar -> unit) =
        use cmd = this.Conn.CreateCommand()
        cmd.CommandText <- this.Sql
        use reader = cmd.ExecuteReader()
        while reader.Read() do
            let ticker = reader.GetString 0
            let r = this.AdjRatio.[ticker]
            let bar : SecBar =
                { etSec      = reader.GetInt32 1
                  vwap       = reader.GetDouble 2 * r
                  // ⭐ volume divided by the SAME ratio (2026-07-29): price and
                  // shares must share one scale or every vwap·volume product is
                  // adj_ratio × real dollars — the DvFloor60 gate was future-
                  // split-dependent (808 trips passed only via inflation, S29).
                  // Ratios (vol_10/vol_60, vwap_60, VWMA) are unaffected.
                  volume     = reader.GetDouble 3 / r
                  tradeCount = reader.GetInt32 4 }
            onNext (ticker, bar)

/// Run pipeline 2 for every candidate day, streaming finished trips into the
/// sink. Returns the number of (ticker, day) candidates whose tape was found.
let collectTrips (conn: DuckDBConnection) (cfg: Config) (secDir: string)
                 (candidates: Candidate[]) (sink: TripSink) (contSink: ContSink)
                 (progress: (DateOnly -> int -> int -> int64 -> unit) option) : int =
    let mutable daysRun = 0
    // ⭐ per-tkd progress (user, 2026-07-27): fires after EVERY drained
    // (ticker, day) — the caller throttles the printing. Skipped days (no 1s
    // tape file) still count as processed so the remaining estimate is honest.
    let mutable processedTkd = 0
    let report (date: DateOnly) =
        progress |> Option.iter (fun p -> p date processedTkd candidates.Length sink.Total)

    let drain (c: Candidate) (sys: IntradaySystem) (lastBar: SecBar) =
        sys.Flatten lastBar
        for pos in sys.Positions do
            match pos.State with
            | ExitedAt _ -> sink.Add c cfg.Notional pos
            | _ -> failwith "Flatten closes all; unreachable"
        for q in sys.ContPositions do contSink.Add c q
        processedTkd <- processedTkd + 1
        report c.Date

    for date, cands in candidates |> Array.groupBy (fun c -> c.Date) do
        let path = IO.Path.Combine(secDir, sprintf "%s.parquet" (date.ToString "yyyy-MM-dd"))
        if not (IO.File.Exists path) then
            processedTkd <- processedTkd + cands.Length
            report date
        else
            daysRun <- daysRun + cands.Length
            let byTicker = cands |> Array.map (fun c -> c.Ticker, c) |> dict
            let adjRatio = cands |> Array.map (fun c -> c.Ticker, c.AdjRatio) |> dict
            let emitter = SecEmitter(conn, path, Array.map (fun (c: Candidate) -> c.Ticker) cands,
                                     adjRatio, cfg.Intraday.SessionStartSec, cfg.Intraday.MocSec)
            let mutable cur : (Candidate * IntradaySystem * SecBar) option = None
            emitter.Process(fun (ticker, bar) ->
                match cur with
                | Some(c, sys, _) when c.Ticker = ticker ->
                    sys.Process bar
                    cur <- Some(c, sys, bar)          // track the LAST bar for Flatten
                | _ ->
                    match cur with
                    | Some(pc, psys, plast) -> drain pc psys plast
                    | None -> ()
                    let c = byTicker.[ticker]
                    let sys = IntradaySystem(cfg.Intraday, ticker, date)
                    sys.Process bar
                    cur <- Some(c, sys, bar))
            match cur with
            | Some(c, sys, lastBar) -> drain c sys lastBar
            | None -> ()

    daysRun

/// Console-summary snapshot of the sink counters (the sink itself is disposed
/// — and its last part flushed — before `run` returns).
type TripSinkStats =
    { Total: int64
      Wins: int64
      GrossWin: float
      GrossLoss: float
      ContTotal: int64 }

/// Run the whole FlushFader sampler: candidates from trading.db (pipeline 1),
/// then the 1s flush engine per candidate day (pipeline 2), trips streamed
/// to parquet part files in `outDir`. Returns (candidate count, days run).
let run (dbPath: string) (secDir: string) (outDir: string) (cfg: Config)
        (startDate: DateOnly) (endDate: DateOnly)
        (progress: (DateOnly -> int -> int -> int64 -> unit) option) : int * int * TripSinkStats =
    let connStr = $"Data Source={dbPath};ACCESS_MODE=READ_ONLY"
    use conn = new DuckDBConnection(connStr)
    conn.Open()
    do
        use pragma = conn.CreateCommand()
        pragma.CommandText <- "PRAGMA memory_limit='6GB'"
        pragma.ExecuteNonQuery() |> ignore

    let candidates = readCandidates conn startDate endDate cfg.MinDv0945 cfg.MinRvol0945 cfg.MinPrevClose
    use sink = new TripSink(outDir)
    use contSink = new ContSink(outDir)
    let daysRun = collectTrips conn cfg secDir candidates sink contSink progress
    // the `use` bindings dispose both sinks on return, flushing the final parts
    // before the caller ever sees the stats
    candidates.Length, daysRun,
    { Total = sink.Total; Wins = sink.Wins; GrossWin = sink.GrossWin; GrossLoss = sink.GrossLoss
      ContTotal = contSink.Total }
