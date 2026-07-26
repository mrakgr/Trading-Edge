module TradingEdge.PlungeRider.Backtest

open System
open System.Collections.Generic
open DuckDB.NET.Data
open TradingEdge.PlungeRider.Intraday

// ===========================================================================
// PlungeRider backtest wiring — identical to SurgeRider/Backtest.fs except:
//   * P&L is SHORT: NetPnL = qty * (entry - exit); ret_exit = 1 - exit/entry
//     (the MaxRiderV1 convention — positive = the short made money).
//   * bars_since_low_1200 -> bars_since_high_1200 (the down-leg age).
//   * aux_hi_* -> aux_lo_* (cover-into-weakness marks at new N-bar lows).
// Everything else (candidate SQL incl. SR_CANDIDATE_TABLE override, parquet
// sink rotation, emitter, drain loop) is byte-identical so post-hoc SQL ports.
// ===========================================================================

/// PlungeRider config = the intraday engine knobs + notional + the daily floor.
type Config =
    { Intraday: IntradayConfig
      Notional: float
      /// The daily in-play floor: minimum 09:30-09:45 dollar volume (see SurgeRider).
      MinDv0945: float
      /// Optional in-play universe pre-filter: rvol_0945_honest >= this. 0 = off.
      MinRvol0945: float }

/// The sampler defaults (mc = 0) — same values as SurgeRider's. ⚠ The vol band
/// is the LONG side's calibration; F14b/F16 flagged the >= 40bp region it
/// excludes as PlungeRider material — sweep --max-vol-20m 1e9 to include it.
let defaultConfig =
    { Intraday =
        { EntryChannelBars = 300
          ExitChannelBars  = 300
          ExitZBars        = 60
          Ezv              = Double.NegativeInfinity   // z-exits OFF (F11)
          Ezt              = Double.NegativeInfinity
          DvFloor60        = 100_000.0  // >= $100k traded over the last 60 present bars at the signal
          TcFloor60        = 60.0       // >= 60 trades over the same window
          MinVol20m        = 0.0007     // the F10 band [7,40)bp/30s — long-side calibration
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

/// One candidate (ticker, day) from diprider_v6_candidate.
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
    // Research override: SR_CANDIDATE_TABLE (shared with SurgeRider — same
    // universe machinery). Identifier-only (injection-safe).
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
// zstd part files. Column order in appendTrip MUST match the CREATE TABLE.
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
    trade_idx INTEGER, bars_since_high_1200 INTEGER,
    gap_60 INTEGER, gap_30 INTEGER, gap_15 INTEGER,
    sess_vwap DOUBLE, dist_sess_vwap DOUBLE, pct_chg_open DOUBLE,
    bar_vol DOUBLE, bar_tc INTEGER,
    vol_5 DOUBLE, vol_10 DOUBLE, vol_15 DOUBLE, vol_30 DOUBLE, vol_60 DOUBLE,
    tc_15 DOUBLE, tc_30 DOUBLE, tc_60 DOUBLE,
    dollar_vol_60 DOUBLE, cum_vol DOUBLE, cum_tc DOUBLE,
    fwd_vwap_60 DOUBLE, fwd_vwap_300 DOUBLE, fwd_vwap_1200 DOUBLE,
    aux_lo_120_px DOUBLE, aux_lo_120_sec INTEGER,
    aux_lo_300_px DOUBLE, aux_lo_300_sec INTEGER,
    aux_lo_600_px DOUBLE, aux_lo_600_sec INTEGER,
    aux_lo_1200_px DOUBLE, aux_lo_1200_sec INTEGER,
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
            // ⭐ SHORT: profit when the exit prints BELOW the entry.
            let pnl = qty * (p.EntryPx - exitPx)
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
            i p.TradeIdx; i p.BarsSinceHigh1200
            i p.Gap60; i p.Gap30; i p.Gap15
            f p.SessVwap; f p.DistSessVwap; f p.PctChgOpen
            f p.BarVol; i p.BarTc
            f p.Vol5; f p.Vol10; f p.Vol15; f p.Vol30; f p.Vol60
            f p.Tc15; f p.Tc30; f p.Tc60
            f p.DollarVol60; f p.CumVol; f p.CumTc
            f p.FwdVwap60; f p.FwdVwap300; f p.FwdVwap1200
            let inline auxSec (s: int) =
                if s < 0 then row.AppendNullValue() |> ignore else row.AppendValue s |> ignore
            f p.AuxLo120; auxSec p.AuxSec120
            f p.AuxLo300; auxSec p.AuxSec300
            f p.AuxLo600; auxSec p.AuxSec600
            f p.AuxLo1200; auxSec p.AuxSec1200
            i exitSec; f exitPx; s reason
            // ⭐ SHORT return: 1 - exit/entry (positive = price fell).
            f (if p.EntryPx > 0.0 then 1.0 - exitPx / p.EntryPx else nan)
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
// Pipeline 2 — SecEmitter -> IntradaySystem -> TripSink (identical shape).
// ===========================================================================
type SecEmitter
        ( conn: DuckDBConnection, path: string,
          tickers: string[], adjRatio: IDictionary<string, float>,
          sessionStartSec: int, mocSec: int ) =

    member val Conn = conn
    member val AdjRatio = adjRatio

    member val Sql =
        let tickerList = tickers |> Array.map (fun t -> "'" + t.Replace("'", "''") + "'") |> String.concat ","
        // `bucket` IS seconds-since-00:00-ET — no window_start conversion.
        sprintf """
        SELECT ticker, bucket, vwap::DOUBLE, volume::DOUBLE, trade_count
        FROM read_parquet('%s')
        WHERE ticker IN (%s) AND bucket >= %d AND bucket <= %d
          AND vwap > 0 AND volume > 0
        ORDER BY ticker, bucket"""
            (path.Replace("'", "''")) tickerList sessionStartSec mocSec

    /// Stream every candidate-ticker present 1s bar for this date, split-
    /// adjusted, in (ticker, bucket) order. `inline` so onNext fuses into the
    /// read loop.
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

/// Console-summary snapshot of the sink counters.
type TripSinkStats =
    { Total: int64
      Wins: int64
      GrossWin: float
      GrossLoss: float }

/// Run the whole PlungeRider sampler. Returns (candidate count, days run, stats).
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
    candidates.Length, daysRun,
    { Total = sink.Total; Wins = sink.Wins; GrossWin = sink.GrossWin; GrossLoss = sink.GrossLoss }
