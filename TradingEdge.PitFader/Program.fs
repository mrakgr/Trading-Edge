module TradingEdge.PitFader.Program

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open Argu
open DuckDB.NET.Data
open TradingEdge.PitFader.Types
open TradingEdge.PitFader.Engine

// THE SESSION of day D = its REGULAR TRADING HOURS (user 2026-11-01): the present bars of the date's file with
// SessionStartSec <= bucket < FlatSec (default 09:30–16:00 ET, every root; the grains and CL simply stop printing earlier).
// Every channel folds from the session start; entries before EntryEndSec (default 15:00, FlushFader's); a trip still
// open at the last bar exits there. Nothing is held overnight.
// THE CONTRACT (causal): the root's volume leader of the PREVIOUS Globex / CFE session (17:00 ET to 17:00 ET) the root
// traded; its first session is skipped.
// Output: a DIRECTORY of parquet parts (part_NNN.parquet, RowsPerPart trips each; the in-memory table is flushed so a
// 5M-trip mc = 0 run never holds the book): read_parquet('<dir>/*.parquet'). The study happens post-hoc in SQL / Python.
// Every trip carries Leg.AllNames (stages 1–2 of the SpikeFader / FlushFader port) read at its signal bar.

type Args =
    | Bars_Dir of string
    | Grids of string
    | [<AltCommandLine("-o")>] Out_Dir of string
    | Entry_Bars of int
    | Exit_Bars of int
    | Aux_Exit_Bars of string
    | Session_Start_Sec of int
    | Entry_End_Sec of int
    | Flat_Sec of int
    | Max_Concurrent of int
    | Exclude_Roots of string
    | Limit_Days of int
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Bars_Dir _ -> "The 1s bars, one file per ET date (default data/futures/intraday_1s_ticks)."
            | Grids _ -> "The quote tick per contract (default data/futures/grids.parquet)."
            | Out_Dir _ -> "The trips directory (default data/futures/pitfader/trips_mc<N>; replaced by a run)."
            | Entry_Bars _ -> "The entry channel in present bars (default 1200 = the ~20m low / high)."
            | Exit_Bars _ -> "The exit channel in present bars (default 300 = the ~5m high / low)."
            | Aux_Exit_Bars _ -> "The AUX exit channels, comma-separated present bars (default 540,600,900,1200,1800,2400,3600): each trip records exit_t_N / exit_px_N / eod_N."
            | Session_Start_Sec _ -> "The session's first second, ET (default 34200 = 09:30)."
            | Entry_End_Sec _ -> "No entry signal at or after this second, ET (default 54000 = 15:00)."
            | Flat_Sec _ -> "The session ends before this second, ET (default 57600 = 16:00)."
            | Exclude_Roots _ -> "Roots left out, comma-separated (default YM,MHG — user 2026-11-01: illiquid small contracts; YM = one contract, MHG = micro copper, use HG). '' = none."
            | Max_Concurrent _ -> "Open trips per root and side: 0 = unlimited, the SAMPLER (default; every new extreme is a trip), 1 = a book."
            | Limit_Days _ -> "Test: the first N sessions only."

let BreakSec = 17 * 3600
let RowsPerPart = 250_000

[<EntryPoint>]
let main argv =
    let a = ArgumentParser.Create<Args>(programName = "PitFader").Parse argv
    let barsDir = a.GetResult(Bars_Dir, "data/futures/intraday_1s_ticks")
    let grids = a.GetResult(Grids, "data/futures/grids.parquet")
    let mc = a.GetResult(Max_Concurrent, 0)
    let excluded = a.GetResult(Exclude_Roots, "YM,MHG").Split(',', StringSplitOptions.RemoveEmptyEntries) |> Set.ofArray
    let outDir = a.GetResult(Out_Dir, sprintf "data/futures/pitfader/trips_mc%d" mc)
    let entryBars, exitBars = a.GetResult(Entry_Bars, 1200), a.GetResult(Exit_Bars, 300)
    let auxExits = a.GetResult(Aux_Exit_Bars, "540,600,900,1200,1800,2400,3600").Split(',', StringSplitOptions.RemoveEmptyEntries) |> Array.map int
    let sessStart, entryEnd, flatSec = a.GetResult(Session_Start_Sec, 34200), a.GetResult(Entry_End_Sec, 54000), a.GetResult(Flat_Sec, 57600)
    let sw = Stopwatch.StartNew()
    let log (s: string) = printfn "[%7.1fs] %s" sw.Elapsed.TotalSeconds s
    if Directory.Exists outDir then Directory.Delete(outDir, true)
    Directory.CreateDirectory outDir |> ignore
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

    // ---- the front per (root, session): the previous session's leader (a low-cardinality aggregate, streamed)
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
            if not (r.IsDBNull 2) && not (excluded.Contains (r.GetString 0)) then
                let td = r.GetFieldValue<DateOnly> 1
                match fronts.TryGetValue td with
                | true, l -> l.Add((r.GetString 0, r.GetString 2))
                | _ -> fronts.[td] <- ResizeArray [ (r.GetString 0, r.GetString 2) ])
    let tick = Dictionary<string, float>()
    query (sprintf "SELECT contract, tick FROM read_parquet('%s') WHERE tick IS NOT NULL" grids) (fun r ->
        tick.[r.GetString 0] <- r.GetDouble 1)
    log (sprintf "%d sessions with a front contract (roots left out: %s)" fronts.Count (String.concat "," excluded))

    exec (sprintf """CREATE TABLE trips (root VARCHAR, contract VARCHAR, td DATE, side TINYINT, sig_t INTEGER, entry_t INTEGER,
            entry_px DOUBLE, exit_t INTEGER, exit_px DOUBLE, eod BOOLEAN, tick DOUBLE, sess_first_t INTEGER,
            sess_last_t INTEGER, sess_bars INTEGER, open_at_sig INTEGER, %s, %s)"""
            (auxExits |> Array.map (fun n -> sprintf "exit_t_%d INTEGER, exit_px_%d DOUBLE, eod_%d BOOLEAN" n n n) |> String.concat ", ")
            (Leg.AllNames |> Array.map (fun n -> n + " DOUBLE") |> String.concat ", "))
    let file (d: DateOnly) = sprintf "%s/%s.parquet" barsDir (d.ToString "yyyy-MM-dd")
    let sessions = fronts |> Seq.toArray
    let sessions = match a.TryGetResult Limit_Days with Some k -> Array.truncate k sessions | None -> sessions
    let mutable nTrips = 0
    let mutable inTable = 0
    let mutable part = 0
    let flush () =
        if inTable > 0 then
            let p = Path.Combine(outDir, sprintf "part_%03d.parquet" part)
            exec (sprintf "COPY trips TO '%s.tmp' (FORMAT parquet, COMPRESSION zstd); DELETE FROM trips" p)
            File.Move(p + ".tmp", p)
            part <- part + 1; inTable <- 0
    do
        let mutable app = conn.CreateAppender "trips"
        for si, KeyValue(td, roots) in Array.indexed sessions do
            let rootOf = Dictionary<string, string>()
            for (root, c) in roots do rootOf.[c] <- root
            let inList = roots |> Seq.map (fun (_, c) -> "'" + c + "'") |> String.concat ","
            let c = file td
            let parts =
                if File.Exists c then
                    [ sprintf "SELECT ticker, bucket, vwap::DOUBLE, volume::DOUBLE, trade_count, delta::DOUBLE FROM read_parquet('%s') WHERE bucket >= %d AND bucket < %d AND ticker IN (%s)"
                          c sessStart flatSec inList ]
                else []
            if not parts.IsEmpty then
                let bars = Dictionary<string, ResizeArray<Bar>>()
                query (String.concat " UNION ALL " parts + " ORDER BY ticker, bucket") (fun r ->
                    let c = r.GetString 0
                    let b = { t = r.GetInt32 1; vwap = r.GetDouble 2; volume = r.GetDouble 3; tradeCount = r.GetInt32 4; delta = r.GetDouble 5 }
                    match bars.TryGetValue c with
                    | true, l -> l.Add b
                    | _ -> bars.[c] <- ResizeArray [ b ])
                for KeyValue(c, l) in bars do
                    let tk = match tick.TryGetValue c with | true, x -> x | _ -> failwithf "%s: no tick in %s" c grids
                    let arr = l.ToArray()
                    for tr in runSession entryBars exitBars auxExits sessStart entryEnd mc arr do
                        let row =
                            app.CreateRow()
                                .AppendValue(rootOf.[c]).AppendValue(c).AppendValue(td).AppendValue(sbyte tr.Side)
                                .AppendValue(tr.SigT).AppendValue(tr.EntryT).AppendValue(tr.EntryPx)
                                .AppendValue(tr.ExitT).AppendValue(tr.ExitPx).AppendValue(tr.Eod).AppendValue(tk)
                                .AppendValue(arr.[0].t).AppendValue(arr.[arr.Length - 1].t).AppendValue(arr.Length)
                                .AppendValue(tr.OpenAtSignal)
                        for k in 0 .. auxExits.Length - 1 do
                            row.AppendValue(tr.AuxExitT.[k]).AppendValue(tr.AuxExitPx.[k]).AppendValue(tr.AuxEod.[k]) |> ignore
                        if tr.Features.Length <> Leg.AllNames.Length then
                            failwithf "%d features for %d names" tr.Features.Length Leg.AllNames.Length
                        for x in tr.Features do
                            (if Double.IsNaN x then row.AppendNullValue() else row.AppendValue x) |> ignore
                        row.EndRow()
                        nTrips <- nTrips + 1; inTable <- inTable + 1
            if inTable >= RowsPerPart then
                app.Close(); flush (); app <- conn.CreateAppender "trips"
            if si % 250 = 0 then log (sprintf "  %d/%d %O  %d trips" (si + 1) sessions.Length td nTrips)
        app.Close()
    flush ()
    log (sprintf "wrote %s: %d trips in %d parts" outDir nTrips part)
    0
