module TradingEdge.LongHiker.Backtest

open System
open System.Threading.Channels
open System.Threading.Tasks
open FSharp.Control
open DuckDB.NET.Data
open TradingEdge.LongHiker.Intraday

// ===========================================================================
// LongHiker backtest wiring (template: TradingEdge.FlushFader/Backtest.fs).
//
// Pipeline 1 = the shared MR candidate universe (`mr_candidate_1s_v2` — the
// causal, 1s-tape-native table; the user's call is to reuse it rather than
// reinvent a momentum universe). Pipeline 2 streams each candidate day's PRESENT
// 1-second bars from data/intraday_1s_slim/ into IntradaySystem.
//
// ⭐ OUTPUT IS PARQUET. LongHiker emits far MORE trips than the Faders by
// design — it fires on a STATE, not an event, so a trending ticker-day can
// contribute thousands of rows. Trips stream into an in-memory DuckDB staging
// table via the appender and rotate to zstd part files every RowsPerPart rows,
// so neither the CLR heap nor the staging table ever holds the full book.
// Post-hoc: read_parquet('<outDir>/*.parquet'). NaN features are written as
// NULL so SQL aggregates skip them natively.
// ===========================================================================

type Config =
    { Intraday: IntradayConfig
      Notional: float
      /// Universe pre-filters, applied in the candidate SELECT. All are 09:45-class
      /// quantities — see the knowability note in Intraday.fs's header for what
      /// that means for a 09:40 entry window.
      MinDv0945: float
      MinRvol0945: float
      MinPrevClose: float
      /// Episode warmup: candidate `barnum` (prior-only ROW_NUMBER, live-knowable)
      /// >= this. Column-guarded — legacy tables predate it.
      MinBarnum: int
      /// Day-worker parallelism. Days are the natural isolation unit (fresh
      /// IntradaySystems, no cross-day state); the trip SET is identical at any
      /// worker count, only parquet row ORDER varies.
      Workers: int }

/// ⭐ THE SAMPLER DEFAULTS. The only live gates are the ER threshold and the two
/// trailing liquidity floors; everything else is recorded and sliced post-hoc.
let defaultConfig =
    { Intraday =
        { MinEffOpen      = 0.3        // ⭐ the user's level: efficiency-since-the-open >= 0.3
          MinEffOpenSlots = 4          // 3 slot returns ~= 90s of dense tape
          HoldBars        = 30         // ⭐ the timestop, in present bars
          SignalOnExtremesOnly = true  // ⭐ user, 2026-08-24: only new-extreme bars.
                                       // The intermediate bars are ~88% of the book and are
                                       // no longer of interest; dropping them also cuts the
                                       // corpus and the run time by roughly that much.
          SignalStride    = 1          // every qualifying bar (the design)
          DvFloor60       = 100_000.0  // >= $100k over the trailing 60 present bars
          TcFloor60       = 60.0       // >= 60 trades over the same window (1/sec — kills
                                       // the block-print-only tape)
          MinVolat20m     = 0.0        // record-first: band volat post-hoc
          MaxVolat20m     = Double.PositiveInfinity
          MaxConcurrent   = 0          // ⭐ SAMPLER
          SlotBars        = 30
          SessionStartSec = 34200      // 09:30 — features fold from the RTH open
          EntryStartSec   = 35100      // ⭐ 09:45 — THE KNOWABILITY FLOOR (user, 2026-08-24).
                                       // The candidate universe gates on tape over
                                       // [09:30,09:45), so an earlier entry makes the
                                       // UNIVERSE ITSELF a lookahead. Nothing about
                                       // feature warmth forces this second — volat_20m is
                                       // an EWMA and is live from slot 1, and vr4_roll
                                       // needs only 7 slot returns (91% available at
                                       // 09:45, 100% by 09:48). It is the universe, not
                                       // the features, that sets the start.
          EntryEndSec     = 57000      // 15:50: a 30-bar hold needs almost no room, and the
                                       // afternoon is exactly where a momentum study must be
                                       // able to look. `signal_sec` is recorded — narrow it
                                       // post-hoc, never by re-running.
          EntryEndSecShort = 46200     // 12:50 on NYSE early-close days (the same 10 minutes)
          MocSec          = 57600      // 16:00
          MocSecShort     = 46800 }    // 13:00
      Notional = 10_000.0
      MinDv0945 = 0.0
      MinRvol0945 = 0.0
      MinPrevClose = 0.0
      MinBarnum = 22
      Workers = max 1 (Environment.ProcessorCount - 2) }

/// One candidate (ticker, day): the daily context that rides along on every trip
/// for post-hoc slicing. ⭐ Every price is in day D's RAW scale, and each
/// reference day carries BOTH a price and a dividend increment —
/// `ret(D -> x) = (CloseX + DivX) / CloseD - 1`. Div* are NEGATIVE for past days.
/// See docs/price_adjustment.md.
type Candidate =
    { Ticker: string
      Date: DateOnly
      CloseD: float
      N: float
      CloseM1: float
      DivM1: float
      CloseM3: float
      DivM3: float
      CloseP1: float
      DivP1: float
      CloseP3: float
      DivP3: float
      CloseP5: float
      DivP5: float
      OpenP1: float
      Dv0945: float
      Rvol0945Honest: float }

/// The candidate table, overridable via LH_CANDIDATE_TABLE. Identifier-only
/// (injection-safe); fails fast on a bad value.
let candidateTable =
    match Environment.GetEnvironmentVariable "LH_CANDIDATE_TABLE" with
    | null | "" -> "mr_candidate_1s_v2"
    | t when t |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_') -> t
    | bad -> failwithf "Invalid LH_CANDIDATE_TABLE %A (identifier chars only)" bad

let readCandidates (conn: DuckDBConnection) (startDate: DateOnly) (endDate: DateOnly)
                   (minDv0945: float) (minRvol0945: float) (minPrevClose: float)
                   (minBarnum: int) : Candidate[] =
    let table = candidateTable
    let hasBarnumCol =
        use c = conn.CreateCommand()
        c.CommandText <- $"SELECT count(*) FROM pragma_table_info('{table}') WHERE name = 'barnum'"
        Convert.ToInt64(c.ExecuteScalar()) > 0L
    let barnumClause =
        if minBarnum > 0 && hasBarnumCol then
            eprintfn "  warmup      = barnum >= %d (early-episode slice cut)" minBarnum
            sprintf "AND barnum >= %d" minBarnum
        else ""
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        $"SELECT ticker, date, close_d, n, close_m1, div_m1, close_m3, div_m3,
                 close_p1, div_p1, close_p3, div_p3, close_p5, div_p5, open_p1,
                 dv_0945, rvol_0945_honest
          FROM {table}
          WHERE date >= $start AND date <= $end AND dv_0945 >= $mindv
            AND rvol_0945_honest >= $minrvol
            AND coalesce(close_m1, 0) >= $minprevclose
            {barnumClause}
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
              CloseD = dbl 2; N = dbl 3
              CloseM1 = dbl 4; DivM1 = dbl 5
              CloseM3 = dbl 6; DivM3 = dbl 7
              CloseP1 = dbl 8; DivP1 = dbl 9
              CloseP3 = dbl 10; DivP3 = dbl 11
              CloseP5 = dbl 12; DivP5 = dbl 13
              OpenP1 = dbl 14
              Dv0945 = dbl 15
              Rvol0945Honest = dbl 16 })
    out.ToArray()

// ===========================================================================
// The trip parquet sink.
// ⚠⚠ COLUMN ORDER IN `appendTrip` MUST MATCH THE CREATE TABLE, line for line.
// The two blocks below are laid out in the same groups with the same comments so
// a mismatch is visible rather than inferred; DuckDB's appender throws on
// EndRow if the counts disagree, so a drift fails on the FIRST trip, not silently.
// ===========================================================================
[<Literal>]
let private RowsPerPart = 250_000

let private tripTableSql = """
CREATE TABLE trips (
    symbol VARCHAR, trade_date VARCHAR, n DOUBLE,
    signal_sec INTEGER, signal_vwap DOUBLE, entry_sec INTEGER, entry_px DOUBLE,
    volat_20m DOUBLE, volat_10m DOUBLE, volat_open DOUBLE, slot_count INTEGER,
    eff_20m DOUBLE, eff_10m DOUBLE, eff_open DOUBLE, eff_open_slots INTEGER,
    eff_ewma_20m DOUBLE, eff_ewma_10m DOUBLE,
    vr2_ewma DOUBLE, vr4_ewma DOUBLE,
    ac1_ewma DOUBLE, ac2_ewma DOUBLE, ac3_ewma DOUBLE,
    ac1_open DOUBLE, ac2_open DOUBLE, ac3_open DOUBLE,
    ac1_roll DOUBLE, ac2_roll DOUBLE, ac3_roll DOUBLE,
    vr2_open DOUBLE, vr4_open DOUBLE, vr2_roll DOUBLE, vr4_roll DOUBLE,
    sign_pers_open DOUBLE, sign_pers_roll DOUBLE, sign_run INTEGER,
    dd_20m DOUBLE, dd_20m_w20 DOUBLE, dd_20m_w10 DOUBLE, dd_10m DOUBLE,
    dd_now_20m DOUBLE, dd_now_10m DOUBLE,
    open_px DOUBLE, sess_hi DOUBLE, sess_lo DOUBLE, sess_vwap DOUBLE,
    hi_60 DOUBLE, hi_120 DOUBLE, hi_300 DOUBLE, hi_600 DOUBLE, hi_1200 DOUBLE,
    lo_60 DOUBLE, lo_120 DOUBLE, lo_300 DOUBLE, lo_600 DOUBLE, lo_1200 DOUBLE,
    vwap_60 DOUBLE, vwap_60_prev DOUBLE, vwap_300 DOUBLE, vwap_1200 DOUBLE,
    secs_since_hi_60 INTEGER, secs_since_hi_120 INTEGER, secs_since_hi_300 INTEGER,
    secs_since_hi_600 INTEGER, secs_since_hi_1200 INTEGER,
    secs_since_lo_60 INTEGER, secs_since_lo_120 INTEGER, secs_since_lo_300 INTEGER,
    secs_since_lo_600 INTEGER, secs_since_lo_1200 INTEGER,
    highs_20m_since_lo_60 INTEGER, highs_20m_since_lo_120 INTEGER,
    highs_20m_since_lo_300 INTEGER, highs_20m_since_lo_600 INTEGER,
    highs_20m_since_lo_1200 INTEGER,
    gap_open INTEGER, gap_10 INTEGER, gap_30 INTEGER, gap_60 INTEGER,
    gap_120 INTEGER, gap_300 INTEGER, gap_600 INTEGER, gap_1200 INTEGER,
    dv_sess DOUBLE, dv_10 DOUBLE, dv_30 DOUBLE, dv_60 DOUBLE,
    dv_120 DOUBLE, dv_300 DOUBLE, dv_600 DOUBLE, dv_1200 DOUBLE,
    tc_sess DOUBLE, tc_10 DOUBLE, tc_30 DOUBLE, tc_60 DOUBLE,
    tc_120 DOUBLE, tc_300 DOUBLE, tc_600 DOUBLE, tc_1200 DOUBLE,
    bar_vol DOUBLE, bar_tc INTEGER, bars_present INTEGER, dv_0945_tape DOUBLE,
    ols_slope_open DOUBLE, ols_r_open DOUBLE,
    ols_slope_60 DOUBLE, ols_r_60 DOUBLE, ols_slope_300 DOUBLE, ols_r_300 DOUBLE,
    ols_slope_600 DOUBLE, ols_r_600 DOUBLE, ols_slope_1200 DOUBLE, ols_r_1200 DOUBLE,
    vol_slope_open DOUBLE, vol_r_open DOUBLE,
    vol_slope_60 DOUBLE, vol_r_60 DOUBLE, vol_slope_300 DOUBLE, vol_r_300 DOUBLE,
    vol_slope_600 DOUBLE, vol_r_600 DOUBLE, vol_slope_1200 DOUBLE, vol_r_1200 DOUBLE,
    open_at_signal INTEGER,
    fwd_vwap_30 DOUBLE, fwd_vwap_60 DOUBLE, fwd_vwap_120 DOUBLE,
    fwd_vwap_300 DOUBLE, fwd_vwap_600 DOUBLE, fwd_vwap_1200 DOUBLE,
    ex_lo_px DOUBLE, ex_lo_sec INTEGER,
    ex_nohi60_30_px DOUBLE, ex_nohi60_30_sec INTEGER,
    ex_nohi60_60_px DOUBLE, ex_nohi60_60_sec INTEGER,
    ex_nohi1200_30_px DOUBLE, ex_nohi1200_30_sec INTEGER,
    ex_nohi1200_60_px DOUBLE, ex_nohi1200_60_sec INTEGER,
    exit_sec INTEGER, exit_px DOUBLE, exit_reason VARCHAR,
    ret_exit DOUBLE, bars_held INTEGER,
    close_m1 DOUBLE, div_m1 DOUBLE, close_m3 DOUBLE, div_m3 DOUBLE, close_d DOUBLE,
    close_p1 DOUBLE, div_p1 DOUBLE, close_p3 DOUBLE, div_p3 DOUBLE,
    close_p5 DOUBLE, div_p5 DOUBLE, open_p1 DOUBLE,
    dv_0945 DOUBLE, rvol_0945_honest DOUBLE,
    qty DOUBLE, net_pnl DOUBLE
)"""

type TripSink(outDir: string) =
    let conn = new DuckDBConnection("Data Source=:memory:")
    do
        conn.Open()
        IO.Directory.CreateDirectory outDir |> ignore
        use cmd = conn.CreateCommand()
        let tmpDir = IO.Path.Combine(IO.Path.GetTempPath(), $"lh_duck_sink_{Guid.NewGuid():N}")
        IO.Directory.CreateDirectory tmpDir |> ignore
        cmd.CommandText <- $"PRAGMA memory_limit='2GB'; PRAGMA temp_directory='{tmpDir}'; " + tripTableSql
        cmd.ExecuteNonQuery() |> ignore
    let mutable appender = conn.CreateAppender "trips"
    let mutable rowsInPart = 0
    let mutable part = 0
    let mutable total = 0L
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
    member _.Add (c: Candidate) (notional: float) (p: LhPosition) =
        match p.State with
        | Holding -> failwith "TripSink.Add on an unfinished position (Flatten first)"
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
            f c.N
            i p.SignalSec; f p.SignalVwap; i p.EntrySec; f p.EntryPx
            f p.Volat20m; f p.Volat10m; f p.VolatOpen; i p.SlotCount
            f p.Eff20m; f p.Eff10m; f p.EffOpen; i p.EffOpenSlots
            f p.EffEwma20m; f p.EffEwma10m
            f p.Vr2Ewma; f p.Vr4Ewma
            f p.Ac1Ewma; f p.Ac2Ewma; f p.Ac3Ewma
            f p.Ac1Open; f p.Ac2Open; f p.Ac3Open
            f p.Ac1Roll; f p.Ac2Roll; f p.Ac3Roll
            f p.Vr2Open; f p.Vr4Open; f p.Vr2Roll; f p.Vr4Roll
            f p.SignPersOpen; f p.SignPersRoll; i p.SignRun
            f p.Dd20m; f p.Dd20mW20; f p.Dd20mW10; f p.Dd10m
            f p.DdNow20m; f p.DdNow10m
            f p.OpenPx; f p.SessHi; f p.SessLo; f p.SessVwap
            f p.Hi60; f p.Hi120; f p.Hi300; f p.Hi600; f p.Hi1200
            f p.Lo60; f p.Lo120; f p.Lo300; f p.Lo600; f p.Lo1200
            f p.Vwap60; f p.Vwap60Prev; f p.Vwap300; f p.Vwap1200
            i p.SecsSinceHi60; i p.SecsSinceHi120; i p.SecsSinceHi300
            i p.SecsSinceHi600; i p.SecsSinceHi1200
            i p.SecsSinceLo60; i p.SecsSinceLo120; i p.SecsSinceLo300
            i p.SecsSinceLo600; i p.SecsSinceLo1200
            i p.Highs20mSinceLo60; i p.Highs20mSinceLo120
            i p.Highs20mSinceLo300; i p.Highs20mSinceLo600
            i p.Highs20mSinceLo1200
            i p.GapOpen; i p.Gap10; i p.Gap30; i p.Gap60
            i p.Gap120; i p.Gap300; i p.Gap600; i p.Gap1200
            f p.DvSess; f p.Dv10; f p.Dv30; f p.Dv60
            f p.Dv120; f p.Dv300; f p.Dv600; f p.Dv1200
            f p.TcSess; f p.Tc10; f p.Tc30; f p.Tc60
            f p.Tc120; f p.Tc300; f p.Tc600; f p.Tc1200
            f p.BarVol; i p.BarTc; i p.BarsPresent; f p.Dv0945Tape
            f p.OlsSlopeOpen; f p.OlsROpen
            f p.OlsSlope60; f p.OlsR60; f p.OlsSlope300; f p.OlsR300
            f p.OlsSlope600; f p.OlsR600; f p.OlsSlope1200; f p.OlsR1200
            f p.VolSlopeOpen; f p.VolROpen
            f p.VolSlope60; f p.VolR60; f p.VolSlope300; f p.VolR300
            f p.VolSlope600; f p.VolR600; f p.VolSlope1200; f p.VolR1200
            i p.OpenAtSignal
            f p.Fwd30; f p.Fwd60; f p.Fwd120
            f p.Fwd300; f p.Fwd600; f p.Fwd1200
            // ⚠ an unfired mark writes NULL for the sec too, not -1 — a sentinel
            // integer would silently average into any SQL that forgot to exclude it
            let inline exSec (px: float) (sec: int) =
                if Double.IsNaN px then row.AppendNullValue() |> ignore else row.AppendValue sec |> ignore
            f p.ExLoPx; exSec p.ExLoPx p.ExLoSec
            f p.ExNoHi60_30Px; exSec p.ExNoHi60_30Px p.ExNoHi60_30Sec
            f p.ExNoHi60_60Px; exSec p.ExNoHi60_60Px p.ExNoHi60_60Sec
            f p.ExNoHi1200_30Px; exSec p.ExNoHi1200_30Px p.ExNoHi1200_30Sec
            f p.ExNoHi1200_60Px; exSec p.ExNoHi1200_60Px p.ExNoHi1200_60Sec
            i exitSec; f exitPx; s reason
            f (if p.EntryPx > 0.0 then exitPx / p.EntryPx - 1.0 else nan)
            i p.BarsHeld
            f c.CloseM1; f c.DivM1; f c.CloseM3; f c.DivM3; f c.CloseD
            f c.CloseP1; f c.DivP1; f c.CloseP3; f c.DivP3
            f c.CloseP5; f c.DivP5; f c.OpenP1
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
// Pipeline 2 — SecEmitter -> IntradaySystem -> TripSink.
// ===========================================================================
type SecEmitter
        ( conn: DuckDBConnection, path: string,
          tickers: string[], sessionStartSec: int, mocSec: int ) =

    member val Conn = conn

    member val Sql =
        let tickerList = tickers |> Array.map (fun t -> "'" + t.Replace("'", "''") + "'") |> String.concat ","
        // `bucket` IS seconds-since-00:00-ET (the 1s builder already did the
        // timezone work). The tape is RAW — see docs/price_adjustment.md.
        sprintf """
        SELECT ticker, bucket, vwap::DOUBLE, volume::DOUBLE, trade_count
        FROM read_parquet('%s')
        WHERE ticker IN (%s) AND bucket >= %d AND bucket <= %d
          AND vwap > 0 AND volume > 0
        ORDER BY ticker, bucket"""
            (path.Replace("'", "''")) tickerList sessionStartSec mocSec

    /// `inline` so onNext fuses into the read loop — it runs ~10^6-10^7 times/day.
    member inline this.Process(onNext: string * SecBar -> unit) =
        use cmd = this.Conn.CreateCommand()
        cmd.CommandText <- this.Sql
        use reader = cmd.ExecuteReader()
        while reader.Read() do
            let ticker = reader.GetString 0
            let bar : SecBar =
                { etSec      = reader.GetInt32 1
                  vwap       = reader.GetDouble 2
                  volume     = reader.GetDouble 3
                  tradeCount = reader.GetInt32 4 }
            onNext (ticker, bar)

type private DayRequest =
    { Date: DateOnly
      Cands: Candidate[]
      Reply: ChannelWriter<struct (Candidate * SecBar[])> }

/// Run pipeline 2 for every candidate day, streaming finished trips into the
/// sink. Returns the number of (ticker, day) candidates whose tape was found.
///
/// ⭐ DAYS RUN IN PARALLEL BEHIND A SINGLE READER. One reader task owns the
/// process's only parquet-reading DuckDB connection; cfg.Workers day-workers are
/// pure folders. A worker claims a day, asks the reader for it, and folds the
/// per-tkd bar arrays that come back on its private reply channel — so in-flight
/// tape is structurally bounded at ~one day per worker. Trip ORDER in the
/// parquet is nondeterministic across runs; the trip SET is not.
let collectTrips (cfg: Config) (secDir: string)
                 (candidates: Candidate[]) (sink: TripSink)
                 (progress: (DateOnly -> int -> int -> int64 -> unit) option) : int =
    let work = Channel.CreateUnbounded<DateOnly * Candidate[]>()
    let producer =
        task {
            try
                for item in candidates |> Array.groupBy (fun c -> c.Date) |> Array.sortBy fst do
                    do! work.Writer.WriteAsync item
            finally work.Writer.Complete()
        }

    // ⚠⚠ BOUNDED TIGHT, AND MUCH TIGHTER THAN THE FADERS'. A LongHiker ticker-day
    // can carry tens of thousands of trips (it fires on a state, every bar), so a
    // single in-flight message is orders of magnitude larger than a FlushFader
    // one. 4096 messages of backpressure would be tens of GB here; 64 gives the
    // sink consumer the same protection at a bounded cost.
    let results = Channel.CreateBounded<struct (Candidate * LhPosition[] * bool)>(BoundedChannelOptions 64)

    let requests = Channel.CreateUnbounded<DayRequest>()
    let readerTask =
        task {
            do! Task.Yield()
            use conn = new DuckDBConnection("Data Source=:memory:")
            conn.Open()
            do  use pragma = conn.CreateCommand()
                let tmpDir = IO.Path.Combine(IO.Path.GetTempPath(), $"lh_duck_reader_{Guid.NewGuid():N}")
                IO.Directory.CreateDirectory tmpDir |> ignore
                pragma.CommandText <- $"PRAGMA threads=6; PRAGMA memory_limit='4GB'; PRAGMA temp_directory='{tmpDir}'"
                pragma.ExecuteNonQuery() |> ignore
            let tkds = ResizeArray<struct (Candidate * SecBar[])>()
            let buf = ResizeArray<SecBar>()
            for req in requests.Reader.ReadAllAsync() do
                try
                    tkds.Clear()
                    let path = IO.Path.Combine(secDir, sprintf "%s.parquet" (req.Date.ToString "yyyy-MM-dd"))
                    let byTicker = req.Cands |> Array.map (fun c -> c.Ticker, c) |> dict
                    let dayMocSec =
                        if TradingEdge.Orb.Timezone.early_closes.Contains req.Date
                        then cfg.Intraday.MocSecShort else cfg.Intraday.MocSec
                    let emitter = SecEmitter(conn, path, Array.map (fun (c: Candidate) -> c.Ticker) req.Cands,
                                             cfg.Intraday.SessionStartSec, dayMocSec)
                    let mutable curTicker : string = null
                    let flush () =
                        if not (isNull curTicker) then
                            tkds.Add(struct (byTicker.[curTicker], buf.ToArray()))
                            buf.Clear()
                    emitter.Process(fun (ticker, bar) ->
                        if ticker <> curTicker then
                            flush ()
                            curTicker <- ticker
                        buf.Add bar)
                    flush ()
                    curTicker <- null
                    for t in tkds do
                        do! req.Reply.WriteAsync t
                    req.Reply.Complete()
                with ex ->
                    // fail THIS request loudly (the worker's fold loop rethrows)
                    // and keep serving the rest — a poisoned day must not hang
                    // the pipeline.
                    req.Reply.TryComplete ex |> ignore
        }

    let worker () : Task =
        task {
            // hop off the caller's thread FIRST: the work channel is already
            // complete, so ReadAllAsync yields synchronously — without this,
            // worker #1 would drain the whole channel before #2 is constructed.
            do! Task.Yield()
            // ⚠ EACH FINISHED TICKER-DAY IS HANDED OFF IMMEDIATELY, not buffered
            // into a per-day list first (which is what the Fader workers do). A
            // day holds ~570 candidates; at LongHiker trip densities buffering a
            // whole day's positions before writing any of them would hold GBs per
            // worker. Nothing deadlocks: the reply channel is unbounded and the
            // reader has already finished writing into it, so blocking on the
            // bounded `results` write here stalls only this worker.
            for date, cands in work.Reader.ReadAllAsync() do
                let path = IO.Path.Combine(secDir, sprintf "%s.parquet" (date.ToString "yyyy-MM-dd"))
                if not (IO.File.Exists path) then
                    for c in cands do do! results.Writer.WriteAsync (struct (c, Array.empty, false))
                else
                    let reply = Channel.CreateUnbounded<struct (Candidate * SecBar[])>()
                    do! requests.Writer.WriteAsync { Date = date; Cands = cands; Reply = reply.Writer }
                    for struct (c, bars) in reply.Reader.ReadAllAsync() do
                        if bars.Length > 0 then
                            let sys = IntradaySystem(cfg.Intraday, c.Ticker, date)
                            for b in bars do sys.Process b
                            sys.Flatten bars.[bars.Length - 1]
                            do! results.Writer.WriteAsync (struct (c, Seq.toArray sys.Positions, true))
                        else
                            // file present but zero usable bars: counts as NO tape, so
                            // `daysRun` stays honest — but it still reaches the consumer,
                            // so the progress denominator does too.
                            do! results.Writer.WriteAsync (struct (c, Array.empty, false))
        }

    let workerTasks = Array.init (max 1 cfg.Workers) (fun _ -> worker ())
    let allWorkers =
        task {
            try do! Task.WhenAll workerTasks
            finally
                requests.Writer.Complete()
                results.Writer.Complete()
        }

    let mutable daysRun = 0
    let mutable processedTkd = 0
    let consumer =
        task {
            for struct (c, positions, tapeFound) in results.Reader.ReadAllAsync() do
                if tapeFound then daysRun <- daysRun + 1
                for pos in positions do
                    match pos.State with
                    | ExitedAt _ -> sink.Add c cfg.Notional pos
                    | Holding -> failwith "Flatten closes all; unreachable"
                processedTkd <- processedTkd + 1
                progress |> Option.iter (fun p -> p c.Date processedTkd candidates.Length sink.Total)
        }
    Task.WaitAll [| producer :> Task; readerTask :> Task; allWorkers :> Task; consumer :> Task |]
    daysRun

type TripSinkStats =
    { Total: int64
      Wins: int64
      GrossWin: float
      GrossLoss: float }

/// Run the whole LongHiker sampler: candidates from trading.db (pipeline 1),
/// then the 1s momentum engine per candidate day (pipeline 2), trips streamed to
/// parquet part files in `outDir`. Returns (candidate count, days run, stats).
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

    let candidates =
        readCandidates conn startDate endDate cfg.MinDv0945 cfg.MinRvol0945 cfg.MinPrevClose cfg.MinBarnum
    use sink = new TripSink(outDir)
    let daysRun = collectTrips cfg secDir candidates sink progress
    candidates.Length, daysRun,
    { Total = sink.Total; Wins = sink.Wins; GrossWin = sink.GrossWin; GrossLoss = sink.GrossLoss }
