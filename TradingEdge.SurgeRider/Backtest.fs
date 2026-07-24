module TradingEdge.SurgeRider.Backtest

open System
open System.Collections.Generic
open DuckDB.NET.Data
open TradingEdge.SurgeRider.Intraday

// ===========================================================================
// SurgeRider backtest wiring (template: DipRiderV6/Backtest.fs).
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

/// SurgeRider config = the intraday engine knobs + notional + the daily floor.
type Config =
    { Intraday: IntradayConfig
      Notional: float
      /// ⭐ The daily in-play floor: minimum 09:30-09:45 dollar volume. SurgeRider
      /// wants a HIGHER floor than the MR books (breakouts need names that trade
      /// every second; measured RTH-active fraction: >=$500M/day names trade 67%
      /// of seconds, $100-500M 33%, $20-100M 18%). Default $10M dv_0945 (~$50-150M
      /// full-day) — sweep post-hoc; the per-bar DvFloor60/TcFloor60 gates do the
      /// second-by-second version of the same job.
      MinDv0945: float
      /// ⭐ Optional in-play universe pre-filter: rvol_0945_honest >= this in the candidate
      /// SELECT. 0 = off (THE SAMPLER DEFAULT — rvol stays recorded-not-gated for breadth).
      /// Knowability: same 09:45 class as dv_0945, legal for EntryStartSec >= 35100. Use for
      /// focused sweeps (e.g. --min-rvol-0945 10 narrows the universe ~50x and a full run
      /// drops to seconds per day).
      MinRvol0945: float }

/// The sampler defaults (mc = 0). Every gate here is a HARD gate; everything
/// else is recorded and sliced post-hoc over the parquet.
let defaultConfig =
    { Intraday =
        { EntryChannelBars = 300        // breakout of the ~5m vwap channel; breach counters for all six
                                        // windows are recorded, so post-hoc can TIGHTEN to 1200/session.
          ExitChannelBars  = 300
          ExitZBars        = 60
          // ⭐ z-EXITS OFF BY DEFAULT (user, 2026-07-23; F11 mechanism study): at EZV/EZT = 0
          // they were (a) instant-ejecting the 56% of entries born with z < 0 (98% exit on
          // the fill bar) and (b) acting as a disguised ~60-bar time stop for the rest (an
          // isolated spike holds the 60-bar-sum z up for exactly the window length). The
          // system exits SOLELY on the channel break — a scalp study of what breakout
          // entries actually earn. Re-enable via --ezv/--ezt.
          Ezv              = Double.NegativeInfinity
          Ezt              = Double.NegativeInfinity
          DvFloor60        = 100_000.0  // >= $100k traded over the last 60 present bars at the signal
          TcFloor60        = 60.0       // >= 60 trades over the same window (1/sec — bars exist anyway;
                                        // kills the block-print-only tape, per the plan's "volume AND
                                        // activity" requirement)
          MinVol20m        = 0.0007     // ⭐ THE VOL BAND [7,40)bp/30s (F10 ceiling + F14b floor): the
                                        // 40bp CEILING is load-bearing (87% of unbanded in-play
                                        // sess-high trips sit >=40bp at PF 0.82); the floor dropped
                                        // 20bp -> 7bp (user, 2026-07-23) — the 7-20bp slice runs PF
                                        // 2.3-3.6 IN-PLAY (F10's p20 was calibrated on the whole
                                        // universe, not in-play).
          MaxVol20m        = 0.0040
          MaxConcurrent    = 0          // ⭐ SAMPLER. 1 = a real book.
          SlotBars         = 30
          BaselineBars     = 1200
          SessionStartSec  = 34200      // 09:30 — features fold from the RTH open
          EntryStartSec    = 35100      // 09:45 — ⚠ the knowability floor (R4), do not lower
          EntryEndSec      = 48600      // 13:30
          MocSec           = 57600 }    // 16:00
      Notional = 10_000.0
      MinDv0945 = 10_000_000.0
      MinRvol0945 = 0.0 }

/// One candidate (ticker, day) from diprider_v6_candidate — the daily context
/// that rides along on every trip for post-hoc slicing. Forward closes are
/// REPORTED only.
type Candidate =
    { Ticker: string
      Date: DateOnly
      PrevAdjClose: float
      DayClose: float
      AdjRatio: float
      CloseFwd1d: float
      CloseFwd3d: float
      CloseFwd5d: float
      Dv0945: float
      Rvol0945Honest: float }

let private readCandidates (conn: DuckDBConnection) (startDate: DateOnly) (endDate: DateOnly) (minDv0945: float) (minRvol0945: float) : Candidate[] =
    // Research override: SR_CANDIDATE_TABLE lets a breakdown run against a different
    // universe without disturbing the production table. Identifier-only (injection-safe).
    let table =
        match Environment.GetEnvironmentVariable "SR_CANDIDATE_TABLE" with
        | null | "" -> "diprider_v6_candidate"
        | t when t |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_') -> t
        | bad -> failwithf "Invalid SR_CANDIDATE_TABLE %A (identifier chars only)" bad
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        $"SELECT ticker, date, prev_adj_close, day_close, adj_ratio,
                 close_fwd_1d, close_fwd_3d, close_fwd_5d, dv_0945, rvol_0945_honest
          FROM {table}
          WHERE date >= $start AND date <= $end AND dv_0945 >= $mindv
            AND rvol_0945_honest >= $minrvol
          ORDER BY ticker, date"
    let pStart = cmd.CreateParameter() in pStart.ParameterName <- "start"; pStart.Value <- startDate; cmd.Parameters.Add pStart |> ignore
    let pEnd   = cmd.CreateParameter() in pEnd.ParameterName   <- "end";   pEnd.Value   <- endDate;   cmd.Parameters.Add pEnd   |> ignore
    let pDv    = cmd.CreateParameter() in pDv.ParameterName    <- "mindv"; pDv.Value    <- minDv0945; cmd.Parameters.Add pDv    |> ignore
    let pRv    = cmd.CreateParameter() in pRv.ParameterName    <- "minrvol"; pRv.Value  <- minRvol0945; cmd.Parameters.Add pRv  |> ignore
    let out = ResizeArray<Candidate>()
    use reader = cmd.ExecuteReader()
    let dbl (i: int) = if reader.IsDBNull i then nan else reader.GetDouble i
    while reader.Read() do
        out.Add(
            { Ticker = reader.GetString 0
              Date   = DateOnly.FromDateTime(reader.GetDateTime 1)
              PrevAdjClose = dbl 2
              DayClose = dbl 3
              AdjRatio = dbl 4
              CloseFwd1d = dbl 5
              CloseFwd3d = dbl 6
              CloseFwd5d = dbl 7
              Dv0945 = dbl 8
              Rvol0945Honest = dbl 9 })
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
    z_vol_1 DOUBLE, z_vol_5 DOUBLE, z_vol_10 DOUBLE, z_vol_15 DOUBLE, z_vol_30 DOUBLE, z_vol_60 DOUBLE,
    z_tc_1 DOUBLE, z_tc_5 DOUBLE, z_tc_10 DOUBLE, z_tc_15 DOUBLE, z_tc_30 DOUBLE, z_tc_60 DOUBLE,
    vol_20m DOUBLE, vol_10m DOUBLE, rng_20m DOUBLE, eff_20m DOUBLE, eff_10m DOUBLE, slot_count INTEGER,
    rng_sess DOUBLE, rng_300 DOUBLE, rng_120 DOUBLE, rng_60 DOUBLE, rng_30 DOUBLE,
    breach_sess INTEGER, breach_1200 INTEGER, breach_300 INTEGER,
    breach_120 INTEGER, breach_60 INTEGER, breach_30 INTEGER,
    trade_idx INTEGER, bars_since_low_1200 INTEGER,
    gap_60 INTEGER, gap_30 INTEGER, gap_15 INTEGER,
    sess_vwap DOUBLE, dist_sess_vwap DOUBLE, pct_chg_open DOUBLE,
    bar_vol DOUBLE, bar_tc INTEGER,
    vol_5 DOUBLE, vol_10 DOUBLE, vol_15 DOUBLE, vol_30 DOUBLE, vol_60 DOUBLE,
    tc_15 DOUBLE, tc_30 DOUBLE, tc_60 DOUBLE,
    dollar_vol_60 DOUBLE, cum_vol DOUBLE, cum_tc DOUBLE,
    fwd_vwap_60 DOUBLE, fwd_vwap_300 DOUBLE, fwd_vwap_1200 DOUBLE,
    aux_hi_120_px DOUBLE, aux_hi_120_sec INTEGER,
    aux_hi_300_px DOUBLE, aux_hi_300_sec INTEGER,
    aux_hi_600_px DOUBLE, aux_hi_600_sec INTEGER,
    aux_hi_1200_px DOUBLE, aux_hi_1200_sec INTEGER,
    exit_sec INTEGER, exit_px DOUBLE, exit_reason VARCHAR,
    ret_exit DOUBLE, bars_held INTEGER,
    prev_adj_close DOUBLE, day_close DOUBLE,
    close_fwd_1d DOUBLE, close_fwd_3d DOUBLE, close_fwd_5d DOUBLE,
    dv_0945 DOUBLE, rvol_0945_honest DOUBLE,
    qty DOUBLE, net_pnl DOUBLE
)"""

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
    member _.Add (c: Candidate) (notional: float) (p: SurgePosition) =
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
            f p.ZVol1; f p.ZVol5; f p.ZVol10; f p.ZVol15; f p.ZVol30; f p.ZVol60
            f p.ZTc1; f p.ZTc5; f p.ZTc10; f p.ZTc15; f p.ZTc30; f p.ZTc60
            f p.Vol20m; f p.Vol10m; f p.Rng20m; f p.Eff20m; f p.Eff10m; i p.SlotCount
            f p.RngSess; f p.Rng300; f p.Rng120; f p.Rng60; f p.Rng30
            i p.BreachSess; i p.Breach1200; i p.Breach300
            i p.Breach120; i p.Breach60; i p.Breach30
            i p.TradeIdx; i p.BarsSinceLow1200
            i p.Gap60; i p.Gap30; i p.Gap15
            f p.SessVwap; f p.DistSessVwap; f p.PctChgOpen
            f p.BarVol; i p.BarTc
            f p.Vol5; f p.Vol10; f p.Vol15; f p.Vol30; f p.Vol60
            f p.Tc15; f p.Tc30; f p.Tc60
            f p.DollarVol60; f p.CumVol; f p.CumTc
            f p.FwdVwap60; f p.FwdVwap300; f p.FwdVwap1200
            let inline auxSec (s: int) =
                if s < 0 then row.AppendNullValue() |> ignore else row.AppendValue s |> ignore
            f p.AuxHi120; auxSec p.AuxSec120
            f p.AuxHi300; auxSec p.AuxSec300
            f p.AuxHi600; auxSec p.AuxSec600
            f p.AuxHi1200; auxSec p.AuxSec1200
            i exitSec; f exitPx; s reason
            f (if p.EntryPx > 0.0 then exitPx / p.EntryPx - 1.0 else nan)
            i p.BarsHeld
            f c.PrevAdjClose; f c.DayClose
            f c.CloseFwd1d; f c.CloseFwd3d; f c.CloseFwd5d
            f c.Dv0945; f c.Rvol0945Honest
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
                  volume     = reader.GetDouble 3
                  tradeCount = reader.GetInt32 4 }
            onNext (ticker, bar)

/// Run pipeline 2 for every candidate day, streaming finished trips into the
/// sink. Returns the number of (ticker, day) candidates whose tape was found.
let collectTrips (conn: DuckDBConnection) (cfg: Config) (secDir: string)
                 (candidates: Candidate[]) (sink: TripSink)
                 (progress: (DateOnly -> int64 -> unit) option) : int =
    let mutable daysRun = 0

    let drain (c: Candidate) (sys: IntradaySystem) (lastBar: SecBar) =
        sys.Flatten lastBar
        for pos in sys.Positions do
            match pos.State with
            | ExitedAt _ -> sink.Add c cfg.Notional pos
            | _ -> failwith "Flatten closes all; unreachable"

    for date, cands in candidates |> Array.groupBy (fun c -> c.Date) do
        let path = IO.Path.Combine(secDir, sprintf "%s.parquet" (date.ToString "yyyy-MM-dd"))
        if IO.File.Exists path then
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
            progress |> Option.iter (fun p -> p date sink.Total)

    daysRun

/// Console-summary snapshot of the sink counters (the sink itself is disposed
/// — and its last part flushed — before `run` returns).
type TripSinkStats =
    { Total: int64
      Wins: int64
      GrossWin: float
      GrossLoss: float }

/// Run the whole SurgeRider sampler: candidates from trading.db (pipeline 1),
/// then the 1s breakout engine per candidate day (pipeline 2), trips streamed
/// to parquet part files in `outDir`. Returns (candidate count, days run).
let run (dbPath: string) (secDir: string) (outDir: string) (cfg: Config)
        (startDate: DateOnly) (endDate: DateOnly)
        (progress: (DateOnly -> int64 -> unit) option) : int * int * TripSinkStats =
    let connStr = $"Data Source={dbPath};ACCESS_MODE=READ_ONLY"
    use conn = new DuckDBConnection(connStr)
    conn.Open()
    do
        use pragma = conn.CreateCommand()
        pragma.CommandText <- "PRAGMA memory_limit='6GB'"
        pragma.ExecuteNonQuery() |> ignore

    let candidates = readCandidates conn startDate endDate cfg.MinDv0945 cfg.MinRvol0945
    use sink = new TripSink(outDir)
    let daysRun = collectTrips conn cfg secDir candidates sink progress
    // the `use` binding disposes the sink on return, flushing the final part
    // before the caller ever sees the stats
    candidates.Length, daysRun,
    { Total = sink.Total; Wins = sink.Wins; GrossWin = sink.GrossWin; GrossLoss = sink.GrossLoss }
