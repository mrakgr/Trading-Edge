module TradingEdge.Optimize.Program

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Suave
open Suave.Filters
open Suave.Operators
open Suave.Successful
open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.VwapSystem
open TradingEdge.Optimize.Config

// =============================================================================
// 1. Parse canonical (ticker, date) list from the charts PowerShell script
// =============================================================================
//
// docs/generate_stocks_in_play_charts.ps1 holds the canonical list of
// ticker/date pairs we optimize against (via a $Files array of hashtables).
// The regex picks out pairs on a single physical line -- commented-out
// lines in the PS1 are skipped because PowerShell's `#` starts a line
// comment that breaks the match.

let private entryRegex =
    Regex("""Ticker\s*=\s*['"]([^'"]+)['"][^}]*Date\s*=\s*['"]([^'"]+)['"]""")

let private loadEntries (ps1Path: string) =
    File.ReadAllText ps1Path
    |> entryRegex.Matches
    |> Seq.map (fun m -> m.Groups.[1].Value, m.Groups.[2].Value)
    |> Seq.distinct
    |> List.ofSeq

// =============================================================================
// 2. Preload trade data (one pass over all Parquet files at startup)
// =============================================================================

type DayData = {
    Ticker: string
    Date: string
    Trades: Trade[]
    Window: MarketHours
}

let private preloadDayData (entries: (string * string) list) : DayData[] =
    // Filter to the subset that actually has Parquet on disk up front so
    // the progress counter matches the work actually being done (some
    // entries in docs/generate_stocks_in_play_charts.ps1 may be commented
    // out or have no downloaded data).
    let onDisk =
        entries
        |> List.filter (fun (t, d) ->
            File.Exists (sprintf "data/trades/%s/%s.parquet" t d))
    let total = onDisk.Length
    printfn "  %d of %d entries have Parquet files on disk" total entries.Length

    // Straight-line loop so we can print per-file progress; the original
    // list-comprehension version was terser but gave no feedback during
    // the ~44s load.
    let result = ResizeArray<DayData>(total)
    let mutable index = 0
    for (ticker, date) in onDisk do
        index <- index + 1
        let path = sprintf "data/trades/%s/%s.parquet" ticker date
        let trades =
            try Some (loadTrades path)
            with ex ->
                printfn "  [%d/%d] FAIL  %s/%s  %s" index total ticker date ex.Message
                None
        match trades with
        | None -> ()
        | Some trades ->
            let op = trades |> Array.tryFind (fun tr -> tr.Session = OpeningPrint)
            let cp = trades |> Array.tryFind (fun tr -> tr.Session = ClosingPrint)
            match op, cp with
            | Some o, Some c ->
                result.Add {
                    Ticker = ticker
                    Date = date
                    Trades = trades
                    Window = { openTime = o.Timestamp; closeTime = c.Timestamp }
                }
            | _ ->
                printfn "  [%d/%d] SKIP  %s/%s  (missing OpeningPrint or ClosingPrint)" index total ticker date

        // Every 10 files or on the last file, print a progress line.
        if index % 10 = 0 || index = total then
            printfn "  [%d/%d] loaded %d days so far" index total result.Count

    result.ToArray()

// =============================================================================
// 3. Round-trip extraction from a fill stream
// =============================================================================
//
// Walks fills chronologically, tracking a weighted-average cost basis.
// When position flips direction the closing leg is netted against the
// open cost and its slice of pro-rated entry commission, plus the
// closing-leg commission, producing one PnL per round trip.

let private extractRoundTrips (fills: Fill array) (commPerShare: float) =
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

// =============================================================================
// 4. Evaluation functions (closed over preloaded dayData)
// =============================================================================

let private profitFactorFromDecisions (dayData: DayData[]) (pcts: float[]) (bandVol: float) =
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

let private profitFactorFromFills (dayData: DayData[]) (pcts: float[]) (bandVol: float) (percentile: float) =
    let fp =
        { Percentile = percentile
          DelayMs = delayMs
          CommissionPerShare = commissionPerShare
          RejectionRate = rejectionRate
          Rng = None }
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

// =============================================================================
// 5. HTTP request helpers
// =============================================================================

let private readBody (req: HttpRequest) =
    req.rawForm |> System.Text.Encoding.UTF8.GetString

let private jsonResponse obj =
    let json = JsonSerializer.Serialize(obj, JsonSerializerOptions(WriteIndented = false))
    OK json >=> Writers.setMimeType "application/json; charset=utf-8"

/// Safe wrapper for JsonElement.TryGetProperty. The .NET API takes an
/// out-parameter and returns bool; F# wraps that as a tuple, and the
/// original script read it with a confusing `|> fun (ok, v) ->` idiom.
/// This helper makes the intent obvious.
let private tryGetProperty (name: string) (elem: JsonElement) : JsonElement option =
    match elem.TryGetProperty name with
    | true, v -> Some v
    | false, _ -> None

let private doublesOfArray (elem: JsonElement) =
    elem.EnumerateArray()
    |> Seq.map (fun e -> e.GetDouble())
    |> Array.ofSeq

let private intsOfArray (elem: JsonElement) =
    elem.EnumerateArray()
    |> Seq.map (fun e -> e.GetInt32())
    |> Array.ofSeq

// =============================================================================
// 6. Route handlers (closed over preloaded dayData)
// =============================================================================

let private buildRoutes (dayData: DayData[]) =
    let evalDecisions (req: HttpRequest) =
        let body = readBody req |> JsonDocument.Parse
        let root = body.RootElement
        let pcts = root.GetProperty("pcts") |> doublesOfArray
        let bandVol = root.GetProperty("bandVol").GetDouble()
        let pf = profitFactorFromDecisions dayData pcts bandVol
        jsonResponse {| profitFactor = pf |}

    let evalFills (req: HttpRequest) =
        let body = readBody req |> JsonDocument.Parse
        let root = body.RootElement
        let pcts = root.GetProperty("pcts") |> doublesOfArray
        let bandVol = root.GetProperty("bandVol").GetDouble()
        let percentile = root.GetProperty("percentile").GetDouble()
        let pf = profitFactorFromFills dayData pcts bandVol percentile
        jsonResponse {| profitFactor = pf |}

    let evalExponents (req: HttpRequest) =
        let body = readBody req |> JsonDocument.Parse
        let root = body.RootElement
        let exponents = root.GetProperty("exponents") |> intsOfArray
        let bandVol = root.GetProperty("bandVol").GetDouble()
        let pcts = exponents |> Array.map (fun i -> basePct * (decay ** float i))
        let pf =
            match tryGetProperty "percentile" root with
            | Some p -> profitFactorFromFills dayData pcts bandVol (p.GetDouble())
            | None -> profitFactorFromDecisions dayData pcts bandVol
        jsonResponse {| profitFactor = pf |}

    // Batch: parallel fitness evaluation for a whole CMA-ES population.
    //
    // IMPORTANT: every item's parameters are extracted into a plain F#
    // record BEFORE the parallel map. If we left the JsonElement structs
    // in place and called .GetProperty inside the parallel worker, each
    // worker would walk the same JsonDocument backing buffer
    // concurrently. JsonDocument is thread-safe for read but that just
    // means "doesn't crash" -- empirically the read path serializes
    // under contention (~4 cores effective instead of 14 on a 14-item
    // batch). Extracting to records first avoids the shared-document
    // bottleneck entirely.
    let evalBatch (req: HttpRequest) =
        let body = readBody req |> JsonDocument.Parse
        let root = body.RootElement
        let items =
            root.GetProperty("items").EnumerateArray()
            |> Seq.map (fun item ->
                let pcts = item.GetProperty("pcts") |> doublesOfArray
                let bandVol = item.GetProperty("bandVol").GetDouble()
                let percentile =
                    match tryGetProperty "percentile" item with
                    | Some p -> Some (p.GetDouble())
                    | None -> None
                struct (pcts, bandVol, percentile))
            |> Array.ofSeq
        let results =
            items
            |> Array.Parallel.map (fun struct (pcts, bandVol, percentile) ->
                match percentile with
                | Some pctile -> profitFactorFromFills dayData pcts bandVol pctile
                | None -> profitFactorFromDecisions dayData pcts bandVol)
        jsonResponse {| profitFactors = results |}

    let configResponse =
        jsonResponse {|
            basePct = basePct
            decay = decay
            positionSize = positionSize
            referenceVol = referenceVol
            lossLimitPct = lossLimitPct
            commissionPerShare = commissionPerShare
            delayMs = delayMs
            numDays = dayData.Length
        |}

    choose [
        POST >=> path "/eval/decisions" >=> request evalDecisions
        POST >=> path "/eval/fills"     >=> request evalFills
        POST >=> path "/eval/exponents" >=> request evalExponents
        POST >=> path "/eval/batch"     >=> request evalBatch
        GET  >=> path "/health"         >=> OK "ok"
        GET  >=> path "/config"         >=> request (fun _ -> configResponse)
    ]

// =============================================================================
// 7. Entry point
// =============================================================================

[<EntryPoint>]
let main _argv =
    let ps1Path = "docs/generate_stocks_in_play_charts.ps1"
    let entries = loadEntries ps1Path
    printfn "Found %d canonical (ticker, date) entries in %s" entries.Length ps1Path

    printfn "Preloading trade files..."
    let swLoad = System.Diagnostics.Stopwatch.StartNew()
    let dayData = preloadDayData entries
    printfn "Loaded %d days in %.1fs" dayData.Length swLoad.Elapsed.TotalSeconds

    // Total trade count across all loaded days — used to size a custom
    // uncompressed (price, size, time) format. 16 bytes/trade × N tells us
    // the memory ceiling before we commit to streaming.
    let totalTrades =
        dayData |> Array.sumBy (fun d -> int64 d.Trades.Length)
    let bytesPerTrade = 16L  // float64 price + int32 size + int64 ns timestamp
    let estBytes = totalTrades * bytesPerTrade
    printfn "Total trades across %d days: %s (%.2f GB at %d bytes/trade)"
        dayData.Length
        (totalTrades.ToString("N0"))
        (float estBytes / 1e9)
        (int bytesPerTrade)

    let port = 8085us
    let config =
        { defaultConfig with
            bindings = [ HttpBinding.createSimple HTTP "127.0.0.1" (int port) ] }

    printfn "Optimization server ready on http://127.0.0.1:%d" port
    printfn "Endpoints:"
    printfn "  POST /eval/decisions  - {pcts: float[], bandVol: float}"
    printfn "  POST /eval/fills      - {pcts: float[], bandVol: float, percentile: float}"
    printfn "  POST /eval/exponents  - {exponents: int[], bandVol: float, percentile?: float}"
    printfn "  POST /eval/batch      - {items: [{pcts, bandVol, percentile?}, ...]} -> parallel eval"
    printfn "  GET  /health"
    printfn "  GET  /config"

    startWebServer config (buildRoutes dayData)
    0
