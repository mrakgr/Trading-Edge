module TradingEdge.Breakout.Program

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open Argu
open DuckDB.NET.Data
open TradingEdge.PitFader.Types
open TradingEdge.Breakout.Engine

// The session, the contract and the bars are PitFader's (TradingEdge.PitFader/Program.fs): RTH 09:30–16:00 ET, the
// front = the root's volume leader of the PREVIOUS Globex / CFE session (causal), the 1s present bars.
// THE VOLUME REFERENCE (causal): vref_min = the root's mean RTH volume per minute (the session's volume ÷ its present
// span in minutes) over its PREVIOUS VrefSessions sessions; a root's first VrefSessions sessions make no trips.
// Output: two DIRECTORIES of parquet parts —
//   <out>/legs/    one row per LEG (the family = thr × floor; the episode = (contract, td, thr, floor, ep_t)): the
//                  frozen consolidation, the entry, the exit and why (flip / trail / eod), the release;
//   <out>/feats/   one row per (contract, td, ep_t) episode start bar: Features.Names + BarNames read at it (inclusive),
//                  shared by every family that started there — join on (contract, td, sig_t = ep_t).
// --window sigma = BO-2 (Engine.fs: the σ-threshold window, families thr × floor); --window volume = BO-3 (VolEngine.fs:
// the fixed-volume window, families vol_min; the reference σ per root and V = the mean of its previous SigmaSessions
// sessions' median warm-bar σ, so a root trades from its VrefSessions + SigmaSessions + 1-th session); --window absorption
// = BO-6 (AbsEngine.fs: strong delta the price did not follow; the references — volume per present bar and the median
// N-bar move — over the previous VrefSessions sessions, so a root trades from its VrefSessions + 1-th session);
// --window momentum = BO-7 (MomEngine.fs: the 1m breakout with repeat entries, the pre-entry volume / delta windows, the
// 1–60 s marks and PitFader's 236 sealed features stored as REAL; ONE table, no feats/ rows; vref_bar as absorption's);
// --window level = LV-1 (LevelEngine.fs: the KEY-LEVEL TREND, user 2026-11-03 — the decayed session max of a 5 s volume
// EWMA is the level, a 30 s EWMA mean ± 2σ against it the bias, stop-and-reverse; one row per trip with its marks; no
// cross-session reference, so a root trades from its first session).

type Args =
    | Bars_Dir of string
    | Grids of string
    | [<AltCommandLine("-o")>] Out_Dir of string
    | Window of string
    | Thresholds of string
    | Vol_Mins of string
    | Max_Ratio of float
    | Sigma_Sessions of int
    | Abs_Windows of string
    | Min_Imb_Ratio of float
    | Min_Rv of float
    | Max_Move_Ratio of float
    | Level_Sec of int
    | Entry_Bars of int
    | Vol_Half_Life of float
    | Level_Half_Life of float
    | Bias_Half_Life of float
    | K_Sigma of float
    | Warm_Sec of int
    | Level of string
    | Band of string
    | Floors of string
    | Vref_Sessions of int
    | Session_Start_Sec of int
    | Entry_End_Sec of int
    | Flat_Sec of int
    | Exclude_Roots of string
    | Roots of string
    | Limit_Days of int
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Bars_Dir _ -> "The 1s bars, one file per ET date (default data/futures/intraday_1s_ticks)."
            | Grids _ -> "The quote tick per contract (default data/futures/grids.parquet)."
            | Out_Dir _ -> "The output directory (default data/futures/breakout/run_bo2 for sigma, run_bo3 for volume; replaced by a run)."
            | Window _ -> "sigma (BO-2, the σ-threshold window), volume (BO-3, the fixed-volume window), absorption (BO-6), momentum (BO-7) or level (LV-1, the key-level trend). Required."
            | Abs_Windows _ -> "absorption: the detection windows in present bars, comma-separated (default 30,60,120,300)."
            | Min_Imb_Ratio _ -> "absorption: the pre-filter's floor on |Σ delta ÷ Σ volume| ÷ the root's reference at N (default 2.0)."
            | Min_Rv _ -> "absorption: the pre-filter's window volume ÷ (N × the average per bar) floor (default 1.0)."
            | Max_Move_Ratio _ -> "absorption: the pre-filter's ceiling on the move in the aggressors' direction ÷ the reference N-bar move (default 0.5)."
            | Entry_Bars _ -> "momentum: the entry channel in present bars (default 60 = the 1m high / low)."
            | Vol_Half_Life _ -> "level: the priority's volume EWMA half-life, seconds (default 5)."
            | Level_Half_Life _ -> "level: the key level's decay half-life, seconds (default 300)."
            | Bias_Half_Life _ -> "level: the bias's mean / variance EWMA half-life, seconds (default 30)."
            | K_Sigma _ -> "level: the band around the key level in σ (default 2)."
            | Warm_Sec _ -> "level: no signal in the session's first N seconds (default 300)."
            | Level _ -> "level: key (the decayed session max of the 5 s volume priority, default) or vwap (the session VWAP)."
            | Band _ -> "level: the band's unit for --k-sigma — sigma (the bias's σ, default), ticks, or atr (the root's mean 1-minute range over its previous --vref-sessions sessions)."
            | Level_Sec _ -> "absorption: how long a detection's range stays armed for the LEVEL family, seconds (default 900)."
            | Vol_Mins _ -> "volume: the window's V in minutes of vref_min, comma-separated (default 5,10,20,40)."
            | Max_Ratio _ -> "volume: a start needs σ ÷ the reference σ ≤ this (default 1.0 = the tighter half)."
            | Sigma_Sessions _ -> "volume: the sessions in the reference σ (default 20)."
            | Thresholds _ -> "The window's σ thresholds in TICKS, comma-separated (default 1,2,3,4,6,8)."
            | Floors _ -> "The volume floors in minutes of vref_min, comma-separated (default 2,5,10,20)."
            | Vref_Sessions _ -> "The sessions in the volume reference (default 20)."
            | Session_Start_Sec _ -> "The session's first second, ET (default 34200 = 09:30)."
            | Entry_End_Sec _ -> "No entry signal after this second, ET (default 54000 = 15:00)."
            | Flat_Sec _ -> "The session ends before this second, ET (default 57600 = 16:00)."
            | Exclude_Roots _ -> "Roots left out, comma-separated (default YM,MHG, as PitFader). '' = none."
            | Roots _ -> "Only these roots, comma-separated (default: all)."
            | Limit_Days _ -> "Test: the first N sessions only."

let BreakSec = 17 * 3600
let RowsPerPart = 1_000_000

[<EntryPoint>]
let main argv =
    let a = ArgumentParser.Create<Args>(programName = "Breakout").Parse argv
    let barsDir = a.GetResult(Bars_Dir, "data/futures/intraday_1s_ticks")
    let grids = a.GetResult(Grids, "data/futures/grids.parquet")
    let csv (s: string) = s.Split(',', StringSplitOptions.RemoveEmptyEntries)
    let excluded = a.GetResult(Exclude_Roots, "YM,MHG") |> csv |> Set.ofArray
    let only = a.TryGetResult Roots |> Option.map (csv >> Set.ofArray)
    let thrs = a.GetResult(Thresholds, "1,2,3,4,6,8") |> csv |> Array.map float
    let floors = a.GetResult(Floors, "2,5,10,20") |> csv |> Array.map float
    let nVref = a.GetResult(Vref_Sessions, 20)
    let mode = a.GetResult Window
    if not (List.contains mode [ "sigma"; "volume"; "absorption"; "momentum"; "level" ]) then
        failwithf "--window %s: expected sigma, volume, absorption, momentum or level" mode
    let volMode, absMode, momMode, lvMode = (mode = "volume"), (mode = "absorption"), (mode = "momentum"), (mode = "level")
    let lvCfg : LevelEngine.LevelConfig =
        { VolHalfLife = a.GetResult(Vol_Half_Life, 5.0); LevelHalfLife = a.GetResult(Level_Half_Life, 300.0)
          BiasHalfLife = a.GetResult(Bias_Half_Life, 30.0); KSigma = a.GetResult(K_Sigma, 2.0); WarmSec = a.GetResult(Warm_Sec, 300)
          Level = (match a.GetResult(Level, "key") with "key" -> LevelEngine.Key | "vwap" -> LevelEngine.Vwap | x -> failwithf "--level %s: key or vwap" x)
          Band = (match a.GetResult(Band, "sigma") with
                  | "sigma" -> LevelEngine.Sigma | "ticks" -> LevelEngine.Ticks | "atr" -> LevelEngine.Atr
                  | x -> failwithf "--band %s: sigma, ticks or atr" x) }
    let momBars = a.GetResult(Entry_Bars, 60)
    let absNs = a.GetResult(Abs_Windows, "30,60,120,300") |> csv |> Array.map int
    let minImb, minRv, maxMove, levelSec =
        a.GetResult(Min_Imb_Ratio, 2.0), a.GetResult(Min_Rv, 1.0), a.GetResult(Max_Move_Ratio, 0.5), a.GetResult(Level_Sec, 900)
    let volMins = a.GetResult(Vol_Mins, "5,10,20,40") |> csv |> Array.map float
    let maxRatio, nSig = a.GetResult(Max_Ratio, 1.0), a.GetResult(Sigma_Sessions, 20)
    let outDir = a.GetResult(Out_Dir, if lvMode then "data/futures/levels/run_lv1" elif momMode then "data/futures/breakout/run_bo7" elif absMode then "data/futures/breakout/run_bo6" elif volMode then "data/futures/breakout/run_bo3" else "data/futures/breakout/run_bo2")
    let sessStart, entryEnd, flatSec = a.GetResult(Session_Start_Sec, 34200), a.GetResult(Entry_End_Sec, 54000), a.GetResult(Flat_Sec, 57600)
    let sw = Stopwatch.StartNew()
    let log (s: string) = printfn "[%7.1fs] %s" sw.Elapsed.TotalSeconds s
    if Directory.Exists outDir then Directory.Delete(outDir, true)
    for d in [ "legs"; "feats" ] do Directory.CreateDirectory(Path.Combine(outDir, d)) |> ignore
    let tmpDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath outDir), ".duckdb_tmp")
    use conn = new DuckDBConnection("Data Source=:memory:")
    conn.Open()
    let exec (sql: string) = use c = conn.CreateCommand() in c.CommandText <- sql; c.ExecuteNonQuery() |> ignore
    exec (sprintf "set memory_limit = '3GB'; set threads = 4; set enable_progress_bar = false; set temp_directory = '%s'" tmpDir)
    let query (sql: string) (f: DuckDBDataReader -> unit) =
        use c = conn.CreateCommand()
        c.CommandText <- sql
        c.UseStreamingMode <- true   // 🛑 CLAUDE.md: mandatory on every reader (2026-08-27 OOM)
        use r = c.ExecuteReader()
        while r.Read() do f r

    // ---- the front per (root, session): the previous session's leader (PitFader's query)
    let fronts = SortedDictionary<DateOnly, ResizeArray<string * string>>()
    query (sprintf """
        WITH v AS (
          SELECT CAST(regexp_extract(filename, '(\d{4}-\d{2}-\d{2})\.parquet$', 1) AS DATE) d, bucket >= %d nxt,
                 ticker, sum(volume) vol
          FROM read_parquet('%s/*.parquet', filename = true) GROUP BY ALL),
        s AS (SELECT CASE WHEN nxt THEN CAST(d + INTERVAL 1 DAY AS DATE) ELSE d END td, ticker, sum(vol) vol
              FROM v GROUP BY ALL),
        l AS (SELECT td, ticker, left(ticker, length(ticker) - 3) root,
                     row_number() OVER (PARTITION BY left(ticker, length(ticker) - 3), td ORDER BY vol DESC, ticker) rk
              FROM s)
        SELECT root, td, lag(ticker) OVER (PARTITION BY root ORDER BY td) front FROM l WHERE rk = 1 ORDER BY td, root"""
        BreakSec barsDir) (fun r ->
            let root = r.GetString 0
            if not (r.IsDBNull 2) && not (excluded.Contains root) && (only |> Option.forall (fun o -> o.Contains root)) then
                let td = r.GetFieldValue<DateOnly> 1
                match fronts.TryGetValue td with
                | true, l -> l.Add((root, r.GetString 2))
                | _ -> fronts.[td] <- ResizeArray [ (root, r.GetString 2) ])
    let tick = Dictionary<string, float>()
    query (sprintf "SELECT contract, tick FROM read_parquet('%s') WHERE tick IS NOT NULL" grids) (fun r ->
        tick.[r.GetString 0] <- r.GetDouble 1)
    if lvMode then log (sprintf "%d sessions with a front contract; KEY LEVELS %A" fronts.Count lvCfg)
    elif momMode then log (sprintf "%d sessions with a front contract; MOMENTUM: the %d-bar breakout, repeat entries, 60 s exit" fronts.Count momBars)
    elif absMode then log (sprintf "%d sessions with a front contract; ABSORPTION windows %A bars, imb ratio ≥ %g, rv ≥ %g, move ratio ≤ %g, level %d s" fronts.Count absNs minImb minRv maxMove levelSec)
    elif volMode then log (sprintf "%d sessions with a front contract; VOLUME windows %A min of vref, ratio ≤ %g" fronts.Count volMins maxRatio)
    else log (sprintf "%d sessions with a front contract; σ %A ticks × floors %A min" fronts.Count thrs floors)

    let featNames = Array.append TradingEdge.PitFader.Features.Names TradingEdge.PitFader.Features.BarNames
    if lvMode then
        let marks = LevelEngine.Marks |> Array.map (fun m -> sprintf "t_%d INTEGER, px_%d DOUBLE" m m) |> String.concat ", "
        exec (sprintf """CREATE TABLE trips (root VARCHAR, contract VARCHAR, td DATE, side TINYINT, sig_t INTEGER, level DOUBLE,
                level_t INTEGER, level_vol DOUBLE, mean DOUBLE, sigma DOUBLE, vol_now DOUBLE, typ_med DOUBLE, level_now DOUBLE,
                entry_t INTEGER, entry_px DOUBLE,
                exit_sig_t INTEGER, exit_t INTEGER, exit_px DOUBLE, reason VARCHAR, tick DOUBLE, band DOUBLE, atr DOUBLE, %s)""" marks)
    elif momMode then
        let pre = MomEngine.PreBars |> Array.map (fun k -> sprintf "pre_vol_%d DOUBLE, pre_dimb_%d DOUBLE" k k) |> String.concat ", "
        let marks = MomEngine.Marks |> Array.map (fun m -> sprintf "t_%d INTEGER, px_%d DOUBLE, dimb_%d DOUBLE, vol_%d DOUBLE" m m m m) |> String.concat ", "
        exec (sprintf """CREATE TABLE trips (root VARCHAR, contract VARCHAR, td DATE, side TINYINT, sig_t INTEGER, entry_t INTEGER,
                entry_px DOUBLE, eod BOOLEAN, tick DOUBLE, vref_bar DOUBLE, %s, %s, %s)""" pre marks
                (TradingEdge.PitFader.Leg.AllNames |> Array.map (fun n -> n + " REAL") |> String.concat ", "))
    elif absMode then
        let hcols = AbsEngine.Horizons |> Array.map (fun h -> sprintf "exit_t_%d INTEGER, exit_px_%d DOUBLE, eod_%d BOOLEAN" h h h) |> String.concat ", "
        exec (sprintf """CREATE TABLE trips (root VARCHAR, contract VARCHAR, td DATE, fam VARCHAR, n_win SMALLINT, aggr TINYINT,
                side TINYINT, det_t INTEGER, sig_t INTEGER, entry_t INTEGER, entry_px DOUBLE, stop_px DOUBLE, imb DOUBLE, rv DOUBLE,
                imb_ratio DOUBLE, ref_imb DOUBLE, move_ticks DOUBLE, move_ratio DOUBLE, ref_move DOUBLE, win_vwap DOUBLE, win_hi DOUBLE, win_lo DOUBLE, win_vol DOUBLE,
                vref_bar DOUBLE, tick DOUBLE, %s, stop_sig_t INTEGER, stop_t INTEGER, stop_fill_px DOUBLE, d30 DOUBLE, d60 DOUBLE)""" hcols)
    elif volMode then
        exec """CREATE TABLE trips (root VARCHAR, contract VARCHAR, td DATE, vol_min DOUBLE, vol DOUBLE, ep_t INTEGER, leg SMALLINT,
                side TINYINT, sig_t INTEGER, entry_t INTEGER, entry_px DOUBLE, exit_sig_t INTEGER, exit_t INTEGER, exit_px DOUBLE,
                reason VARCHAR, released_t INTEGER, cons_first_t INTEGER, cons_bars INTEGER, cons_sigma DOUBLE, cons_ratio DOUBLE,
                ref_sigma DOUBLE, cons_mean DOUBLE, cons_hi DOUBLE, cons_lo DOUBLE, vref_min DOUBLE, tick DOUBLE)"""
    else
        exec """CREATE TABLE trips (root VARCHAR, contract VARCHAR, td DATE, thr DOUBLE, floor DOUBLE, ep_t INTEGER, leg SMALLINT,
                side TINYINT, sig_t INTEGER, entry_t INTEGER, entry_px DOUBLE, exit_sig_t INTEGER, exit_t INTEGER, exit_px DOUBLE,
                reason VARCHAR, released_t INTEGER, cons_first_t INTEGER, cons_bars INTEGER, cons_vol DOUBLE, cons_sigma DOUBLE,
                cons_mean DOUBLE, cons_hi DOUBLE, cons_lo DOUBLE, vref_min DOUBLE, tick DOUBLE)"""
    exec (sprintf "CREATE TABLE feats (contract VARCHAR, td DATE, sig_t INTEGER, %s)"
            (featNames |> Array.map (fun n -> n + " DOUBLE") |> String.concat ", "))
    let file (d: DateOnly) = sprintf "%s/%s.parquet" barsDir (d.ToString "yyyy-MM-dd")
    let sessions = fronts |> Seq.toArray
    let sessions = match a.TryGetResult Limit_Days with Some k -> Array.truncate k sessions | None -> sessions
    let vhist = Dictionary<string, Queue<float>>()
    let shist = Dictionary<string, Queue<float[]>>()      // volume: per root, the previous sessions' median σ per V
    let bhist = Dictionary<string, Queue<float>>()        // absorption: per root, the previous sessions' volume per present bar
    let mhist = Dictionary<string, Queue<float[]>>()      // absorption: per root, the previous sessions' median |N-bar move|
    let ihist = Dictionary<string, Queue<float[]>>()      // absorption: per root, the previous sessions' median |N-bar imb|
    let mutable nTrips = 0L
    let mutable inTable = 0
    let mutable part = 0
    let flush () =
        if inTable > 0 then
            for tb in [ "trips"; "feats" ] do
                let p = Path.Combine(outDir, (if tb = "trips" then "legs" else tb), sprintf "part_%03d.parquet" part)
                exec (sprintf "COPY %s TO '%s.tmp' (FORMAT parquet, COMPRESSION zstd); DELETE FROM %s" tb p tb)
                File.Move(p + ".tmp", p)
            part <- part + 1; inTable <- 0
    do
        let mutable app = conn.CreateAppender "trips"
        let mutable fapp = conn.CreateAppender "feats"
        for si, KeyValue(td, roots) in Array.indexed sessions do
            let rootOf = Dictionary<string, string>()
            for (root, c) in roots do rootOf.[c] <- root
            let inList = roots |> Seq.map (fun (_, c) -> "'" + c + "'") |> String.concat ","
            let c = file td
            if File.Exists c then
                let bars = Dictionary<string, ResizeArray<Bar>>()
                query (sprintf "SELECT ticker, bucket, vwap::DOUBLE, volume::DOUBLE, trade_count, delta::DOUBLE FROM read_parquet('%s') WHERE bucket >= %d AND bucket < %d AND ticker IN (%s) ORDER BY ticker, bucket"
                           c sessStart flatSec inList) (fun r ->
                    let c = r.GetString 0
                    let b = { t = r.GetInt32 1; vwap = r.GetDouble 2; volume = r.GetDouble 3; tradeCount = r.GetInt32 4; delta = r.GetDouble 5 }
                    match bars.TryGetValue c with
                    | true, l -> l.Add b
                    | _ -> bars.[c] <- ResizeArray [ b ])
                for KeyValue(c, l) in bars do
                    let root = rootOf.[c]
                    let tk = match tick.TryGetValue c with | true, x -> x | _ -> failwithf "%s: no tick in %s" c grids
                    let arr = l.ToArray()
                    let h = match vhist.TryGetValue root with | true, q -> q | _ -> (let q = Queue<float>() in vhist.[root] <- q; q)
                    let writeSnaps (snaps: Dictionary<int, float[]>) =
                        for KeyValue(t, f) in snaps do
                            if f.Length <> featNames.Length then failwithf "%d features for %d names" f.Length featNames.Length
                            let row = fapp.CreateRow().AppendValue(c).AppendValue(td).AppendValue(t)
                            for x in f do (if Double.IsNaN x then row.AppendNullValue() else row.AppendValue x) |> ignore
                            row.EndRow()
                    if lvMode && arr.Length >= 3 then
                        // the ATR reference (causal): the root's mean 1-minute range over its previous nVref sessions
                        let ah = match bhist.TryGetValue root with | true, q -> q | _ -> (let q = Queue<float>() in bhist.[root] <- q; q)
                        let atr = if ah.Count = nVref then Seq.average ah else nan
                        let trade = lvCfg.Band <> LevelEngine.Atr || not (Double.IsNaN atr)
                        let lvTrips = if trade then LevelEngine.runSession lvCfg tk atr sessStart entryEnd arr else ResizeArray()
                        let r1 = LevelEngine.minuteRange arr
                        if not (Double.IsNaN r1) then
                            ah.Enqueue r1
                            if ah.Count > nVref then ah.Dequeue() |> ignore
                        for o in lvTrips do
                            let row =
                                app.CreateRow()
                                    .AppendValue(root).AppendValue(c).AppendValue(td).AppendValue(sbyte o.Side).AppendValue(o.SigT)
                                    .AppendValue(o.Level).AppendValue(o.LevelT).AppendValue(o.LevelVol).AppendValue(o.Mean).AppendValue(o.Sigma)
                                    .AppendValue(o.VolNow).AppendValue(o.TypMed).AppendValue(o.LevelNow).AppendValue(o.EntryT).AppendValue(o.EntryPx).AppendValue(o.ExitSigT)
                                    .AppendValue(o.ExitT).AppendValue(o.ExitPx).AppendValue(o.Reason).AppendValue(tk).AppendValue(o.BandW)
                            (if Double.IsNaN atr then row.AppendNullValue() else row.AppendValue atr) |> ignore
                            for k in 0 .. LevelEngine.Marks.Length - 1 do
                                (if o.MarkT.[k] < 0 then row.AppendNullValue() else row.AppendValue o.MarkT.[k]) |> ignore
                                (if Double.IsNaN o.MarkPx.[k] then row.AppendNullValue() else row.AppendValue o.MarkPx.[k]) |> ignore
                            row.EndRow()
                            nTrips <- nTrips + 1L; inTable <- inTable + 1
                    elif momMode && arr.Length >= 3 then
                        let bh = match bhist.TryGetValue root with | true, q -> q | _ -> (let q = Queue<float>() in bhist.[root] <- q; q)
                        if bh.Count = nVref then
                            let vrefBar = Seq.average bh
                            for o in MomEngine.runSession momBars sessStart entryEnd arr do
                                let row =
                                    app.CreateRow()
                                        .AppendValue(root).AppendValue(c).AppendValue(td).AppendValue(sbyte o.Side).AppendValue(o.SigT)
                                        .AppendValue(o.EntryT).AppendValue(o.EntryPx).AppendValue(o.Eod).AppendValue(tk).AppendValue(vrefBar)
                                let nn (x: float) = (if Double.IsNaN x then row.AppendNullValue() else row.AppendValue x) |> ignore
                                for k in 0 .. MomEngine.PreBars.Length - 1 do
                                    nn o.PreVol.[k]; nn o.PreDimb.[k]
                                for k in 0 .. MomEngine.Marks.Length - 1 do
                                    (if o.MarkT.[k] < 0 then row.AppendNullValue() else row.AppendValue o.MarkT.[k]) |> ignore
                                    nn o.MarkPx.[k]; nn o.MarkDimb.[k]; nn o.MarkVol.[k]
                                if o.Features.Length <> TradingEdge.PitFader.Leg.AllNames.Length then
                                    failwithf "%d features for %d names" o.Features.Length TradingEdge.PitFader.Leg.AllNames.Length
                                for x in o.Features do
                                    (if Double.IsNaN x then row.AppendNullValue() else row.AppendValue(float32 x)) |> ignore
                                row.EndRow()
                                nTrips <- nTrips + 1L; inTable <- inTable + 1
                        bh.Enqueue((arr |> Array.sumBy (fun b -> b.volume)) / float arr.Length)
                        if bh.Count > nVref then bh.Dequeue() |> ignore
                    elif absMode && arr.Length >= 3 then
                        let bh = match bhist.TryGetValue root with | true, q -> q | _ -> (let q = Queue<float>() in bhist.[root] <- q; q)
                        let mh = match mhist.TryGetValue root with | true, q -> q | _ -> (let q = Queue<float[]>() in mhist.[root] <- q; q)
                        let ih = match ihist.TryGetValue root with | true, q -> q | _ -> (let q = Queue<float[]>() in ihist.[root] <- q; q)
                        let warm = bh.Count = nVref && mh.Count = nVref
                        let vrefBar = if warm then Seq.average bh else nan
                        let refOf (q: Queue<float[]>) =
                            Array.init absNs.Length (fun ni ->
                                if not warm then nan
                                else
                                    let xs = q |> Seq.map (fun m -> m.[ni]) |> Seq.filter (Double.IsNaN >> not) |> Seq.toArray
                                    if xs.Length = 0 then nan else Array.average xs)
                        let refMove, refImb = refOf mh, refOf ih
                        let trips, snaps, medMove, medImb = AbsEngine.runSession absNs refMove refImb minImb minRv maxMove levelSec tk vrefBar sessStart entryEnd arr
                        for o in trips do
                            let d = o.Det
                            let row =
                                app.CreateRow()
                                    .AppendValue(root).AppendValue(c).AppendValue(td).AppendValue(if o.Fam = 0 then "fade" else "level")
                                    .AppendValue(int16 d.N).AppendValue(sbyte d.Aggr).AppendValue(sbyte o.Side).AppendValue(d.DetT)
                                    .AppendValue(o.SigT).AppendValue(o.EntryT).AppendValue(o.EntryPx).AppendValue(o.StopPx)
                                    .AppendValue(d.Imb).AppendValue(d.Rv).AppendValue(d.ImbR).AppendValue(refImb.[Array.IndexOf(absNs, d.N)])
                                    .AppendValue(d.Move).AppendValue(d.Ratio)
                                    .AppendValue(refMove.[Array.IndexOf(absNs, d.N)]).AppendValue(d.Vwap).AppendValue(d.Hi).AppendValue(d.Lo)
                                    .AppendValue(d.Vol).AppendValue(vrefBar).AppendValue(tk)
                            for k in 0 .. AbsEngine.Horizons.Length - 1 do
                                row.AppendValue(o.ExitT.[k]).AppendValue(o.ExitPx.[k]).AppendValue(o.Eod.[k]) |> ignore
                            if o.StopSigT < 0 then row.AppendNullValue().AppendNullValue().AppendNullValue() |> ignore
                            else row.AppendValue(o.StopSigT).AppendValue(o.StopT).AppendValue(o.StopFillPx) |> ignore
                            (if Double.IsNaN o.D30 then row.AppendNullValue() else row.AppendValue o.D30) |> ignore
                            (if Double.IsNaN o.D60 then row.AppendNullValue() else row.AppendValue o.D60) |> ignore
                            row.EndRow()
                            nTrips <- nTrips + 1L; inTable <- inTable + 1
                        writeSnaps snaps
                        // this session joins both references only after it was traded (causal)
                        bh.Enqueue((arr |> Array.sumBy (fun b -> b.volume)) / float arr.Length)
                        if bh.Count > nVref then bh.Dequeue() |> ignore
                        mh.Enqueue medMove
                        if mh.Count > nVref then mh.Dequeue() |> ignore
                        ih.Enqueue medImb
                        if ih.Count > nVref then ih.Dequeue() |> ignore
                    elif volMode && h.Count = nVref && arr.Length >= 3 then
                        let vref = Seq.average h
                        let sh = match shist.TryGetValue root with | true, q -> q | _ -> (let q = Queue<float[]>() in shist.[root] <- q; q)
                        let refSigma =
                            Array.init volMins.Length (fun vi ->
                                if sh.Count < nSig then nan
                                else
                                    let xs = sh |> Seq.map (fun m -> m.[vi]) |> Seq.filter (Double.IsNaN >> not) |> Seq.toArray
                                    if xs.Length = 0 then nan else Array.average xs)
                        let legs, snaps, medians = VolEngine.runSession volMins refSigma maxRatio tk vref sessStart entryEnd arr
                        for l in legs do
                            let vi = Array.IndexOf(volMins, l.VolMin)
                            let row =
                                app.CreateRow()
                                    .AppendValue(root).AppendValue(c).AppendValue(td).AppendValue(l.VolMin).AppendValue(l.Vol)
                                    .AppendValue(l.EpT).AppendValue(int16 l.LegNo).AppendValue(sbyte l.Side).AppendValue(l.SigT)
                                    .AppendValue(l.EntryT).AppendValue(l.EntryPx)
                            (if l.ExitSigT < 0 then row.AppendNullValue() else row.AppendValue l.ExitSigT) |> ignore
                            row.AppendValue(l.ExitT).AppendValue(l.ExitPx).AppendValue(l.Reason.ToString().ToLowerInvariant()) |> ignore
                            (if l.ReleasedT < 0 then row.AppendNullValue() else row.AppendValue l.ReleasedT) |> ignore
                            row.AppendValue(l.Cons.FirstT).AppendValue(l.Cons.Bars).AppendValue(l.Cons.SigmaTicks).AppendValue(l.Cons.Ratio)
                               .AppendValue(refSigma.[vi]).AppendValue(l.Cons.Mean).AppendValue(l.Cons.Hi).AppendValue(l.Cons.Lo)
                               .AppendValue(vref).AppendValue(tk) |> ignore
                            row.EndRow()
                            nTrips <- nTrips + 1L; inTable <- inTable + 1
                        writeSnaps snaps
                        // this session joins the σ reference only after it was traded (causal)
                        sh.Enqueue medians
                        if sh.Count > nSig then sh.Dequeue() |> ignore
                    elif not volMode && h.Count = nVref && arr.Length >= 3 then
                        let vref = Seq.average h
                        let legs, snaps = runSession thrs floors tk vref sessStart entryEnd arr
                        for l in legs do
                            let row =
                                app.CreateRow()
                                    .AppendValue(root).AppendValue(c).AppendValue(td).AppendValue(l.ThrTicks).AppendValue(l.FloorMin)
                                    .AppendValue(l.EpT).AppendValue(int16 l.LegNo).AppendValue(sbyte l.Side).AppendValue(l.SigT)
                                    .AppendValue(l.EntryT).AppendValue(l.EntryPx)
                            (if l.ExitSigT < 0 then row.AppendNullValue() else row.AppendValue l.ExitSigT) |> ignore
                            row.AppendValue(l.ExitT).AppendValue(l.ExitPx).AppendValue(l.Reason.ToString().ToLowerInvariant()) |> ignore
                            (if l.ReleasedT < 0 then row.AppendNullValue() else row.AppendValue l.ReleasedT) |> ignore
                            row.AppendValue(l.Cons.FirstT).AppendValue(l.Cons.Bars).AppendValue(l.Cons.Volume).AppendValue(l.Cons.SigmaTicks)
                               .AppendValue(l.Cons.Mean).AppendValue(l.Cons.Hi).AppendValue(l.Cons.Lo).AppendValue(vref).AppendValue(tk) |> ignore
                            row.EndRow()
                            nTrips <- nTrips + 1L; inTable <- inTable + 1
                        writeSnaps snaps
                    // this session joins the reference only after it was traded (causal)
                    if arr.Length > 0 then
                        let mins = float (arr.[arr.Length - 1].t - arr.[0].t + 1) / 60.0
                        h.Enqueue((arr |> Array.sumBy (fun b -> b.volume)) / mins)
                        if h.Count > nVref then h.Dequeue() |> ignore
            if inTable >= (if momMode then RowsPerPart / 4 else RowsPerPart) then      // momentum: ~280 columns a row
                app.Close(); fapp.Close(); flush (); app <- conn.CreateAppender "trips"; fapp <- conn.CreateAppender "feats"
            if si % 250 = 0 then log (sprintf "  %d/%d %O  %d trips" (si + 1) sessions.Length td nTrips)
        app.Close(); fapp.Close()
    flush ()
    log (sprintf "wrote %s: %d legs in %d parts" outDir nTrips part)
    0
