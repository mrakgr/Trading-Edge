#r "nuget: Suave, 2.7.0"
#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TDigest.dll"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TradingEdge.Parsing.dll"

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Suave
open Suave.Filters
open Suave.Operators
open Suave.Successful
open Suave.RequestErrors
open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.VwapSystem

// ----- 1. Parse stocks-in-play list -----
let ps1Path = "docs/generate_stocks_in_play_charts.ps1"
let entryRegex =
    Regex("""Ticker\s*=\s*['"]([^'"]+)['"][^}]*Date\s*=\s*['"]([^'"]+)['"]""")

let entries =
    File.ReadAllText ps1Path
    |> entryRegex.Matches
    |> Seq.map (fun m -> m.Groups.[1].Value, m.Groups.[2].Value)
    |> Seq.distinct
    |> List.ofSeq

let availableEntries =
    entries
    |> List.filter (fun (t, d) ->
        File.Exists (sprintf "data/trades/%s/%s.parquet" t d))

// ----- 2. Preload trade data -----
printfn "Preloading trade files..."
let swLoad = System.Diagnostics.Stopwatch.StartNew()

type DayData = {
    Ticker: string
    Date: string
    Trades: Trade[]
    Window: MarketHours
}

let dayData =
    [| for (ticker, date) in availableEntries do
        let path = sprintf "data/trades/%s/%s.parquet" ticker date
        let trades =
            try Some (loadTrades path)
            with _ -> None
        match trades with
        | None -> ()
        | Some trades ->
            let op = trades |> Array.tryFind (fun tr -> tr.Session = OpeningPrint)
            let cp = trades |> Array.tryFind (fun tr -> tr.Session = ClosingPrint)
            match op, cp with
            | Some o, Some c ->
                yield {
                    Ticker = ticker
                    Date = date
                    Trades = trades
                    Window = { openTime = o.Timestamp; closeTime = c.Timestamp }
                }
            | _ -> () |]

printfn "Loaded %d days in %.1fs" dayData.Length swLoad.Elapsed.TotalSeconds

// ----- 3. Fixed configuration -----
#load "config.fsx"
open Config

// ----- 4. Round-trip extraction -----
let extractRoundTrips (fills: Fill array) (commPerShare: float) =
    let trips = ResizeArray<float>()
    let mutable position = 0
    let mutable avgCost = 0.0
    let mutable entryCommission = 0.0

    for f in fills do
        let qty = f.Quantity
        let commission = float (abs qty) * commPerShare

        if position = 0 then
            avgCost <- f.Price
            position <- qty
            entryCommission <- commission
        elif sign qty = sign position then
            let totalQty = position + qty
            avgCost <- (avgCost * float (abs position) + f.Price * float (abs qty)) / float (abs totalQty)
            position <- totalQty
            entryCommission <- entryCommission + commission
        else
            let closingQty = min (abs qty) (abs position)
            let pnl = float (sign position) * (f.Price - avgCost) * float closingQty
            let totalCommission = entryCommission * (float closingQty / float (abs position)) + commission
            trips.Add(pnl - totalCommission)
            let remaining = position + qty
            if remaining = 0 then
                position <- 0
                avgCost <- 0.0
                entryCommission <- 0.0
            elif sign remaining <> sign position then
                entryCommission <- float (abs remaining) * commPerShare
                avgCost <- f.Price
                position <- remaining
            else
                entryCommission <- entryCommission * (1.0 - float closingQty / float (abs position))
                position <- remaining

    trips.ToArray()

// ----- 5. Evaluation functions -----
let profitFactorFromDecisions (pcts: float[]) (bandVol: float) =
    let allTradePnLs = ResizeArray<float>()
    for d in dayData do
        let addTrade, getResult, _ =
            createPipeline d.Window pcts positionSize (Some referenceVol) bandVol (Some lossLimit) None None None
        for tr in d.Trades do addTrade tr
        let r = getResult()
        let decs = r.Decisions
        for i in 1 .. decs.Count - 1 do
            let prev = decs.[i - 1]
            let curr = decs.[i]
            allTradePnLs.Add((curr.Price - prev.Price) * float prev.Shares)
    let wins = allTradePnLs |> Seq.filter (fun p -> p > 0.01) |> Seq.sum
    let losses = allTradePnLs |> Seq.filter (fun p -> p < -0.01) |> Seq.sum |> abs
    if losses > 0.0 then wins / losses else infinity

let profitFactorFromFills (pcts: float[]) (bandVol: float) (percentile: float) =
    let fp = { Percentile = percentile; DelayMs = delayMs; CommissionPerShare = commissionPerShare; RejectionRate = rejectionRate; Rng = None }
    let allTripPnLs = ResizeArray<float>()
    for d in dayData do
        let addTrade, _, getFillResult =
            createPipeline d.Window pcts positionSize (Some referenceVol) bandVol (Some lossLimit) None None (Some fp)
        for tr in d.Trades do addTrade tr
        let fr = getFillResult()
        let trips = extractRoundTrips (fr.Fills |> Seq.toArray) commissionPerShare
        allTripPnLs.AddRange trips
    let wins = allTripPnLs |> Seq.filter (fun p -> p > 0.01) |> Seq.sum
    let losses = allTripPnLs |> Seq.filter (fun p -> p < -0.01) |> Seq.sum |> abs
    if losses > 0.0 then wins / losses else infinity

// ----- 6. HTTP request handling -----
let readBody (req: HttpRequest) =
    req.rawForm |> System.Text.Encoding.UTF8.GetString

let jsonResponse obj =
    let json = JsonSerializer.Serialize(obj, JsonSerializerOptions(WriteIndented = false))
    OK json >=> Writers.setMimeType "application/json; charset=utf-8"

let evalDecisions (req: HttpRequest) =
    let body = readBody req |> JsonDocument.Parse
    let root = body.RootElement
    let pcts = root.GetProperty("pcts").EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Array.ofSeq
    let bandVol = root.GetProperty("bandVol").GetDouble()
    let pf = profitFactorFromDecisions pcts bandVol
    jsonResponse {| profitFactor = pf |}

let evalFills (req: HttpRequest) =
    let body = readBody req |> JsonDocument.Parse
    let root = body.RootElement
    let pcts = root.GetProperty("pcts").EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Array.ofSeq
    let bandVol = root.GetProperty("bandVol").GetDouble()
    let percentile = root.GetProperty("percentile").GetDouble()
    let pf = profitFactorFromFills pcts bandVol percentile
    jsonResponse {| profitFactor = pf |}

let evalExponents (req: HttpRequest) =
    let body = readBody req |> JsonDocument.Parse
    let root = body.RootElement
    let exponents = root.GetProperty("exponents").EnumerateArray() |> Seq.map (fun e -> e.GetInt32()) |> Array.ofSeq
    let bandVol = root.GetProperty("bandVol").GetDouble()
    let pcts = exponents |> Array.map (fun i -> basePct * (decay ** float i))
    let useFills = root.TryGetProperty("percentile") |> fun (ok, v) -> if ok then Some (v.GetDouble()) else None
    let pf =
        match useFills with
        | Some percentile -> profitFactorFromFills pcts bandVol percentile
        | None -> profitFactorFromDecisions pcts bandVol
    jsonResponse {| profitFactor = pf |}

// ----- 7. Batch evaluation -----
let evalBatch (req: HttpRequest) =
    let body = readBody req |> JsonDocument.Parse
    let root = body.RootElement
    let items = root.GetProperty("items").EnumerateArray() |> Array.ofSeq
    let results =
        items
        |> Array.Parallel.map (fun item ->
            let pcts = item.GetProperty("pcts").EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Array.ofSeq
            let bandVol = item.GetProperty("bandVol").GetDouble()
            let useFills = item.TryGetProperty("percentile") |> fun (ok, v) -> if ok then Some (v.GetDouble()) else None
            match useFills with
            | Some percentile -> profitFactorFromFills pcts bandVol percentile
            | None -> profitFactorFromDecisions pcts bandVol)
    jsonResponse {| profitFactors = results |}

// ----- 8. Routes -----
let app =
    choose [
        POST >=> path "/eval/decisions" >=> request evalDecisions
        POST >=> path "/eval/fills" >=> request evalFills
        POST >=> path "/eval/exponents" >=> request evalExponents
        POST >=> path "/eval/batch" >=> request evalBatch
        GET >=> path "/health" >=> OK "ok"
        GET >=> path "/config" >=> request (fun _ ->
            jsonResponse {|
                basePct = basePct
                decay = decay
                positionSize = positionSize
                referenceVol = referenceVol
                lossLimitPct = lossLimitPct
                commissionPerShare = commissionPerShare
                delayMs = delayMs
                numDays = dayData.Length
            |})
    ]

// ----- 8. Start server -----
let port = 8085us
let config =
    { defaultConfig with
        bindings = [ HttpBinding.createSimple HTTP "127.0.0.1" (int port) ] }

printfn "Optimization server ready on http://127.0.0.1:%d" port
printfn "Endpoints:"
printfn "  POST /eval/decisions  — {pcts: float[], bandVol: float}"
printfn "  POST /eval/fills      — {pcts: float[], bandVol: float, percentile: float}"
printfn "  POST /eval/exponents  — {exponents: int[], bandVol: float, percentile?: float}"
printfn "  POST /eval/batch      — {items: [{pcts, bandVol, percentile?}, ...]} → parallel eval"
printfn "  GET  /health"
printfn "  GET  /config"

startWebServer config app
