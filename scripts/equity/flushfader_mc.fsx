#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#r "nuget: Argu, 6.2.5"

// FlushFader post-hoc max-concurrency (mc) selector — S38.
//
// The engine records the FULL attribution book (mc=0). A real single-account
// trader can only hold `mc` positions at once. Instead of re-running the engine
// (which would also distort the continuation counterfactuals), we replay the
// book chronologically and greedily take a trip iff fewer than `mc` previously
// taken positions are still open at its entry time:
//
//   sort trips by (trade_date, entry_sec, symbol)   -- symbol = deterministic tie-break
//   open : min-heap of exit timestamps of taken positions
//   for each trip:
//     pop open exits STRICTLY BEFORE entry_ts       -- exit == entry still blocks
//     if open.Count < mc: take; push exit_ts
//
// All positions are intraday (MOC backstop) so days never interact; the
// absolute timestamp (DayNumber*86400 + sec) makes that automatic.
//
// Output: per-year + total PF table on the console, and the selected trip keys
// (symbol, trade_date, signal_sec) written to a parquet for downstream SQL joins.

open System
open System.Collections.Generic
open Argu
open DuckDB.NET.Data

type Args =
    | Mc of int
    | Trips of string
    | Where of string
    | Out of string
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Mc _ -> "max concurrent positions (default 1)"
            | Trips _ -> "trips parquet glob (default v13_reference)"
            | Where _ -> "extra SQL cut on the book (default: SPEC v1.3 post-hoc $1<=px<$10)"
            | Out _ -> "output parquet path for selected trip keys (default: alongside trips, mc{N}_selected.parquet)"

let parser = ArgumentParser.Create<Args>(programName = "flushfader_mc.fsx")
let args = parser.Parse(fsi.CommandLineArgs |> Array.skip 1)

let mc = args.GetResult(Mc, defaultValue = 1)
let tripsGlob = args.GetResult(Trips, defaultValue = "data/equity/flushfader/v13_reference/trips_p*.parquet")
let cut = args.GetResult(Where, defaultValue = "entry_px/adj_ratio >= 1 AND entry_px/adj_ratio < 10")
let outPath = args.GetResult(Out, defaultValue = $"data/equity/flushfader/v13_reference/mc{mc}_selected.parquet")

printfn "mc=%d  trips=%s" mc tripsGlob
printfn "cut:  %s" cut

use conn = new DuckDBConnection("DataSource=:memory:")
conn.Open()

let trips =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        $"""SELECT symbol, trade_date, signal_sec, entry_sec, exit_sec, ret_exit
            FROM read_parquet('{tripsGlob}')
            WHERE {cut}
            ORDER BY trade_date, entry_sec, symbol"""
    use reader = cmd.ExecuteReader()
    let acc = ResizeArray()
    while reader.Read() do
        let td = reader.GetString 1
        let day = int64 (DateOnly.Parse(td).DayNumber)
        acc.Add
            {| Symbol = reader.GetString 0
               TradeDate = td
               SignalSec = reader.GetInt32 2
               EntryTs = day * 86400L + int64 (reader.GetInt32 3)
               ExitTs = day * 86400L + int64 (reader.GetInt32 4)
               Ret = reader.GetDouble 5 |}
    acc

printfn "book: %d trips" trips.Count

let openExits = PriorityQueue<int64, int64>()
let taken = ResizeArray()
for t in trips do
    while openExits.Count > 0 && openExits.Peek() < t.EntryTs do
        openExits.Dequeue() |> ignore
    if openExits.Count < mc then
        openExits.Enqueue(t.ExitTs, t.ExitTs)
        taken.Add t

printfn "taken: %d trips (%.1f%% of book)" taken.Count (100.0 * float taken.Count / float trips.Count)

// selected keys -> parquet for downstream SQL
do
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "CREATE TABLE sel (symbol VARCHAR, trade_date VARCHAR, signal_sec INTEGER)"
    cmd.ExecuteNonQuery() |> ignore
    use appender = conn.CreateAppender("sel")
    for t in taken do
        let row = appender.CreateRow()
        row.AppendValue(t.Symbol).AppendValue(t.TradeDate).AppendValue(t.SignalSec).EndRow()
    appender.Close()
    use copy = conn.CreateCommand()
    copy.CommandText <- $"COPY sel TO '{outPath}' (FORMAT PARQUET)"
    copy.ExecuteNonQuery() |> ignore
    printfn "selected keys -> %s" outPath

// per-year + total stats
let stats (rs: float seq) =
    let rs = Seq.toArray rs
    let pos = rs |> Array.filter (fun r -> r > 0.0) |> Array.sum
    let neg = rs |> Array.filter (fun r -> r < 0.0) |> Array.sumBy abs
    let pf = if neg > 0.0 then pos / neg else infinity
    let win = 100.0 * float (rs |> Array.filter (fun r -> r > 0.0) |> Array.length) / float rs.Length
    let med = rs |> Array.sort |> fun a -> a.[a.Length / 2]
    pf, win, Array.average rs, med, rs.Length

printfn ""
printfn "| year | n | PF | win%% | avg%% | med%% |"
printfn "|------|---|----|------|------|------|"
taken
|> Seq.groupBy (fun t -> t.TradeDate.Substring(0, 4))
|> Seq.sortBy fst
|> Seq.iter (fun (y, ts) ->
    let pf, win, avg, med, n = stats (ts |> Seq.map (fun t -> t.Ret))
    printfn "| %s | %d | %.3f | %.1f | %+.2f | %+.2f |" y n pf win (100.0 * avg) (100.0 * med))
let pf, win, avg, med, n = stats (taken |> Seq.map (fun t -> t.Ret))
printfn "| **total** | %d | %.3f | %.1f | %+.2f | %+.2f |" n pf win (100.0 * avg) (100.0 * med)
