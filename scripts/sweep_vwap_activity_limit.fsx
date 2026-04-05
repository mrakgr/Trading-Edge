#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.1.3"
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
        File.Exists (sprintf "data/trades/%s/%s.json" t d))
printfn "Trade files present for %d of them" availableEntries.Length

// ----- 2. Candidate definitions -----
let positionSize = 1000.0

type Candidate = {
    Name: string
    MkSystem: unit -> VwapSystemEffectDynamic
    MaxTrades: int option
}

// Same base systems as the loss-limit sweep
let baseSystems : (string * (unit -> VwapSystemEffectDynamic))[] =
    [|
        "static 32k    ", (fun () -> createStaticVwapSystem 32000.0)
        "static 64k    ", (fun () -> createStaticVwapSystem 64000.0)
        "dyn pct=1.00% ", (fun () ->
            createDynamicVwapSystem (defaultEstimationOffsets, Array.create defaultEstimationOffsets.Length 0.01))
        "dyn pct=0.75% ", (fun () ->
            createDynamicVwapSystem (defaultEstimationOffsets, Array.create defaultEstimationOffsets.Length 0.0075))
        "dyn pct=0.50% ", (fun () ->
            createDynamicVwapSystem (defaultEstimationOffsets, Array.create defaultEstimationOffsets.Length 0.005))
        "dyn pct=0.25% ", (fun () ->
            createDynamicVwapSystem (defaultEstimationOffsets, Array.create defaultEstimationOffsets.Length 0.0025))
    |]

// Activity cap ladder: 5-40 trades/day, plus no limit as a control.
// The previous sweep saw best systems making ~15-28 discretionary decisions
// per day, so 5 is aggressive pruning and 40 is effectively no cap.
let maxTradesGrid : int option[] =
    [| None
       Some 5
       Some 8
       Some 12
       Some 16
       Some 20
       Some 25
       Some 30
       Some 40 |]

let candidates =
    [| for (baseName, mkSys) in baseSystems do
         for mt in maxTradesGrid do
            let tag =
                match mt with
                | None -> "no limit"
                | Some n -> sprintf "cap %3d " n
            { Name = sprintf "%s %s" baseName tag
              MkSystem = mkSys
              MaxTrades = mt } |]

printfn "Sweep: %d candidates × %d files (loss limit disabled)" candidates.Length availableEntries.Length
printfn ""

// ----- 3. Per-candidate accumulators -----
let N = candidates.Length
let totals = Array.zeroCreate<float> N
let wins   = Array.zeroCreate<int> N
let losses = Array.zeroCreate<int> N
let flats  = Array.zeroCreate<int> N
let totalDecisions = Array.zeroCreate<int> N
let perFile = Array.init N (fun _ -> ResizeArray<string * string * float>())

// ----- 4. Stream over files -----
let swAll = System.Diagnostics.Stopwatch.StartNew()
let mutable processed = 0
for (ticker, date) in availableEntries do
    let path = sprintf "data/trades/%s/%s.json" ticker date
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
                let c = candidates.[i]
                let sys = c.MkSystem ()
                let sim =
                    match c.MaxTrades with
                    | Some n -> VwapSimulator(window, sys, positionSize, maxTrades = n)
                    | None -> VwapSimulator(window, sys, positionSize)
                for tr in trades do sim.AddTrade tr
                let r = sim.Result
                let pnl = r.RealizedPnL
                totals.[i] <- totals.[i] + pnl
                totalDecisions.[i] <- totalDecisions.[i] + r.Decisions.Count
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
    GC.Collect()
    GC.WaitForPendingFinalizers()

printfn ""
printfn "Sweep finished in %.1fs, processed %d files" swAll.Elapsed.TotalSeconds processed

// ----- 5. Summary: sorted by total P&L -----
printfn ""
printfn "=== Candidate results (sorted by total P&L) ==="
let denom = float (max 1 processed)
let ranking =
    [| for i in 0 .. N - 1 ->
        let w, l = wins.[i], losses.[i]
        let winRate = if w + l > 0 then 100.0 * float w / float (w + l) else 0.0
        let meanDecisions = float totalDecisions.[i] / denom
        i, candidates.[i].Name, totals.[i], totals.[i] / denom,
        w, l, flats.[i], winRate, totalDecisions.[i], meanDecisions |]
    |> Array.sortByDescending (fun (_, _, t, _, _, _, _, _, _, _) -> t)

for (_, name, total, mean, w, l, f, wr, dTot, dMean) in ranking do
    printfn "  %-28s  total $%9.2f  mean $%7.2f  W/L/F=%3d/%3d/%3d  wr=%5.1f%%  dec=%5d (%.1f/day)"
        name total mean w l f wr dTot dMean

// ----- 6. Grid view: systems × activity caps -----
printfn ""
printfn "=== Grid: total P&L by system × activity cap ==="
printf "%-18s" "system"
for mt in maxTradesGrid do
    match mt with
    | None -> printf " %10s" "no limit"
    | Some n -> printf " %10s" (sprintf "%d tr" n)
printfn ""
for (baseName, _) in baseSystems do
    printf "%-18s" (baseName.Trim())
    for mt in maxTradesGrid do
        let idx =
            candidates
            |> Array.findIndex (fun c ->
                c.Name.StartsWith baseName
                && (match c.MaxTrades, mt with
                    | None, None -> true
                    | Some a, Some b -> a = b
                    | _ -> false))
        printf " %10.0f" totals.[idx]
    printfn ""

// ----- 7. Best candidate breakdown -----
let (bestIdx, bestName, _, _, _, _, _, _, _, _) = ranking.[0]
printfn ""
printfn "=== Best candidate: %s — per-file breakdown (top / bottom 10) ===" bestName
let sorted =
    perFile.[bestIdx]
    |> Seq.sortByDescending (fun (_, _, p) -> p)
    |> Seq.toList
printfn "Top 10:"
sorted |> List.truncate 10
       |> List.iter (fun (t, d, p) -> printfn "  %-6s %-12s  $%9.2f" t d p)
printfn "Bottom 10:"
sorted |> List.rev |> List.truncate 10
       |> List.iter (fun (t, d, p) -> printfn "  %-6s %-12s  $%9.2f" t d p)
