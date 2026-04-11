#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.5.0"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TradingEdge.Parsing.dll"

open System
open System.IO
open System.Text.RegularExpressions
open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.VwapSystem

// ----- 1. Parse the authoritative stocks-in-play list -----
let ps1Path = "docs/generate_stocks_in_play_charts.ps1"
let entryRegex =
    Regex("""Ticker\s*=\s*['"]([^'"]+)['"][^}]*Date\s*=\s*['"]([^'"]+)['"]""")

let entries =
    File.ReadAllText ps1Path
    |> entryRegex.Matches
    |> Seq.map (fun m -> m.Groups.[1].Value, m.Groups.[2].Value)
    |> Seq.distinct
    |> List.ofSeq

printfn "Parsed %d stocks-in-play entries from ps1" entries.Length

let availableEntries =
    entries
    |> List.filter (fun (t, d) ->
        File.Exists (sprintf "data/trades/%s/%s.parquet" t d))
printfn "Trade files present for %d of them" availableEntries.Length

// ----- 2. Candidate definitions -----
let positionSize = 1000.0

type Candidate = {
    Name: string
    MkSystem: unit -> VwapSystemEffectDynamic
}

let staticSizes = [| 1000.0; 2000.0; 4000.0; 8000.0; 16000.0; 32000.0; 64000.0 |]
let staticCandidates =
    staticSizes
    |> Array.map (fun s ->
        { Name = sprintf "static %-6.0f" s
          MkSystem = fun () -> createStaticVwapSystem s })

let dynamicPcts =
    [| for k in 0 .. 15 -> 0.05 * (0.8 ** float k) |]
let dynamicCandidates =
    dynamicPcts
    |> Array.map (fun pct ->
        let arr = Array.create defaultEstimationOffsets.Length pct
        { Name = sprintf "dyn pct=%.5f" pct
          MkSystem = fun () -> createDynamicVwapSystem (defaultEstimationOffsets, arr) })

let candidates = Array.append staticCandidates dynamicCandidates
printfn "Sweep: %d candidates × %d files" candidates.Length availableEntries.Length
printfn ""

// ----- 3. Per-candidate accumulators -----
let N = candidates.Length
let totals = Array.zeroCreate<float> N
let wins   = Array.zeroCreate<int> N
let losses = Array.zeroCreate<int> N
let flats  = Array.zeroCreate<int> N
// Per-file P&L for the best candidate we'll print later — capture all rows
let perFile = Array.init N (fun _ -> ResizeArray<string * string * float>())

// ----- 4. Stream: load one file, run all candidates, release memory -----
let swAll = System.Diagnostics.Stopwatch.StartNew()
let mutable processed = 0
for (ticker, date) in availableEntries do
    let path = sprintf "data/trades/%s/%s.parquet" ticker date
    let trades =
        try Some (loadTrades path)
        with ex ->
            printfn "  skip %s/%s: %s" ticker date ex.Message
            None
    match trades with
    | None -> ()
    | Some trades ->
        let op = trades |> Array.tryFind (fun tr -> tr.Session = OpeningPrint)
        let cp = trades |> Array.tryFind (fun tr -> tr.Session = ClosingPrint)
        match op, cp with
        | Some o, Some c ->
            let window = { openTime = o.Timestamp; closeTime = c.Timestamp }
            for i in 0 .. N - 1 do
                let sys = candidates.[i].MkSystem ()
                let sim = VwapSimulator(window, sys, positionSize)
                for tr in trades do sim.AddTrade tr
                let pnl = sim.Result.RealizedPnL
                totals.[i] <- totals.[i] + pnl
                if pnl > 0.01 then wins.[i] <- wins.[i] + 1
                elif pnl < -0.01 then losses.[i] <- losses.[i] + 1
                else flats.[i] <- flats.[i] + 1
                perFile.[i].Add(ticker, date, pnl)
            processed <- processed + 1
            printfn "[%3d/%d] %-6s %-12s (%d trades, %.0fs elapsed)"
                processed availableEntries.Length ticker date
                trades.Length swAll.Elapsed.TotalSeconds
        | _ ->
            printfn "  skip %s/%s: missing open/close print" ticker date
    // encourage GC so large arrays don't linger
    GC.Collect()
    GC.WaitForPendingFinalizers()

printfn ""
printfn "Sweep finished in %.1fs, processed %d files" swAll.Elapsed.TotalSeconds processed

// ----- 5. Summary -----
printfn ""
printfn "=== Candidate results (sorted by total P&L) ==="
let denom = float (max 1 processed)
let ranking =
    [| for i in 0 .. N - 1 ->
        let w, l = wins.[i], losses.[i]
        let winRate = if w + l > 0 then 100.0 * float w / float (w + l) else 0.0
        i, candidates.[i].Name, totals.[i], totals.[i] / denom, w, l, flats.[i], winRate |]
    |> Array.sortByDescending (fun (_, _, t, _, _, _, _, _) -> t)

for (_, name, total, mean, w, l, f, wr) in ranking do
    printfn "  %-18s  total $%10.2f  mean $%8.2f  W/L/F=%3d/%3d/%3d  winrate=%5.1f%%"
        name total mean w l f wr

// Best candidate per-file breakdown
let (bestIdx, bestName, _, _, _, _, _, _) = ranking.[0]
printfn ""
printfn "=== Best candidate: %s — per-file breakdown (top and bottom) ===" bestName
let sorted =
    perFile.[bestIdx]
    |> Seq.sortByDescending (fun (_, _, p) -> p)
    |> Seq.toList
printfn "Top 15:"
sorted |> List.truncate 15
       |> List.iter (fun (t, d, p) -> printfn "  %-6s %-12s  $%9.2f" t d p)
printfn "Bottom 15:"
sorted |> List.rev |> List.truncate 15
       |> List.iter (fun (t, d, p) -> printfn "  %-6s %-12s  $%9.2f" t d p)
