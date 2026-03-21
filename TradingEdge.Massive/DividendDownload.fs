module TradingEdge.DividendDownload

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading

let private jsonOptions =
    JsonFSharpOptions.Default()
        .WithSkippableOptionFields()
        .ToJsonSerializerOptions()

/// JSON response types for the Polygon dividends API
[<CLIMutable>]
type private DividendResult = {
    [<JsonPropertyName("ticker")>]
    Ticker: string

    [<JsonPropertyName("ex_dividend_date")>]
    ExDividendDate: string

    [<JsonPropertyName("cash_amount")>]
    CashAmount: float

    [<JsonPropertyName("declaration_date")>]
    DeclarationDate: string option

    [<JsonPropertyName("pay_date")>]
    PayDate: string option

    [<JsonPropertyName("frequency")>]
    Frequency: int

    [<JsonPropertyName("dividend_type")>]
    DividendType: string
}

[<CLIMutable>]
type private DividendsResponse = {
    [<JsonPropertyName("results")>]
    Results: DividendResult[] option

    [<JsonPropertyName("next_url")>]
    NextUrl: string option
}

let private baseUrl = "https://api.polygon.io/v3/reference/dividends"
let private maxRetries = 5
let private baseDelayMs = 200

let private tryParseDate (s: string) =
    if String.IsNullOrWhiteSpace s then None
    else
        match DateTime.TryParse s with
        | true, d -> Some d
        | false, _ -> None

let private parseDividendResult (r: DividendResult) : Dividend option =
    if r.CashAmount > 0.0 && not (String.IsNullOrWhiteSpace r.Ticker) then
        Some {
            Ticker = r.Ticker
            ExDividendDate = DateTime.Parse r.ExDividendDate
            CashAmount = r.CashAmount
            DeclarationDate = r.DeclarationDate |> Option.bind tryParseDate
            PayDate = r.PayDate |> Option.bind tryParseDate
            Frequency = r.Frequency
            DividendType = if String.IsNullOrWhiteSpace r.DividendType then "CD" else r.DividendType
        }
    else None

let private csvHeader = "ticker,ex_dividend_date,cash_amount,declaration_date,pay_date,frequency,dividend_type"

let private dividendToCsvLine (d: Dividend) =
    let exDateStr = d.ExDividendDate.ToString("yyyy-MM-dd")
    let declDateStr = d.DeclarationDate |> Option.map (fun dt -> dt.ToString("yyyy-MM-dd")) |> Option.defaultValue ""
    let payDateStr = d.PayDate |> Option.map (fun dt -> dt.ToString("yyyy-MM-dd")) |> Option.defaultValue ""
    $"{d.Ticker},{exDateStr},{d.CashAmount},{declDateStr},{payDateStr},{d.Frequency},{d.DividendType}"

/// Download all dividends for a single date range (sequential pagination)
let private downloadDateRange
    (httpClient: HttpClient)
    (apiKey: string)
    (startDateStr: string)
    (endDateStr: string)
    (ct: CancellationToken)
    : Async<Result<Dividend list, string>> =
    async {
        let results = ResizeArray<Dividend>()

        let buildUrl (nextUrl: string option) =
            match nextUrl with
            | Some n when n.Contains "apiKey=" -> n
            | Some n -> $"{n}&apiKey={apiKey}"
            | None -> $"{baseUrl}?ex_dividend_date.gte={startDateStr}&ex_dividend_date.lte={endDateStr}&limit=1000&apiKey={apiKey}"

        let rec fetchPage (nextUrl: string option) : Async<Result<unit, string>> =
            async {
                if ct.IsCancellationRequested then return Ok ()
                else
                    let url = buildUrl nextUrl

                    let rec retry attempt delay =
                        async {
                            try
                                let! response = httpClient.GetAsync(url, ct) |> Async.AwaitTask
                                if response.StatusCode = HttpStatusCode.TooManyRequests then
                                    if attempt >= maxRetries then
                                        return Error "Rate limited: max retries exceeded"
                                    else
                                        let jitter = Random.Shared.Next(10, 100)
                                        do! Async.Sleep (int (float delay) + jitter)
                                        return! retry (attempt + 1) (int (float delay * 1.5))
                                else
                                    response.EnsureSuccessStatusCode() |> ignore
                                    let! content = response.Content.ReadAsStringAsync ct |> Async.AwaitTask
                                    return Ok (JsonSerializer.Deserialize<DividendsResponse>(content, jsonOptions))
                            with ex when attempt < maxRetries ->
                                do! Async.Sleep 500
                                return! retry (attempt + 1) delay
                        }

                    match! retry 0 baseDelayMs with
                    | Error msg -> return Error msg
                    | Ok response ->
                        match response.Results with
                        | Some rs ->
                            for r in rs do
                                match parseDividendResult r with
                                | Some d -> results.Add d
                                | None -> ()
                        | None -> ()

                        match response.NextUrl with
                        | Some n when not (String.IsNullOrWhiteSpace n) ->
                            return! fetchPage (Some n)
                        | _ -> return Ok ()
            }

        match! fetchPage None with
        | Error msg -> return Error msg
        | Ok () -> return Ok (results |> Seq.toList)
    }

/// Split a date range into monthly chunks as (year, month) pairs
let private splitIntoMonths (startDate: DateTime) (endDate: DateTime) : (int * int) list =
    let rec loop (y, m) acc =
        if y > endDate.Year || (y = endDate.Year && m > endDate.Month) then
            List.rev acc
        else
            let nextM = if m = 12 then 1 else m + 1
            let nextY = if m = 12 then y + 1 else y
            loop (nextY, nextM) ((y, m) :: acc)
    loop (startDate.Year, startDate.Month) []

/// Cache file path for a month
let private monthCachePath (cacheDir: string) (year: int) (month: int) =
    Path.Combine(cacheDir, $"{year}-{month:D2}.csv")

/// Write dividends to a CSV cache file
let private writeCacheCsv (path: string) (dividends: Dividend list) =
    use writer = new StreamWriter(path)
    writer.WriteLine csvHeader
    for d in dividends do
        writer.WriteLine(dividendToCsvLine d)

/// Read dividends from a cached CSV file
let private readCacheCsv (path: string) : Dividend list =
    let lines = File.ReadAllLines path
    lines
    |> Array.skip 1 // header
    |> Array.choose (fun line ->
        let parts = line.Split(',')
        if parts.Length >= 7 then
            Some ({
                Ticker = parts.[0]
                ExDividendDate = DateTime.Parse parts.[1]
                CashAmount = float parts.[2]
                DeclarationDate = if String.IsNullOrWhiteSpace parts.[3] then None else Some (DateTime.Parse parts.[3])
                PayDate = if String.IsNullOrWhiteSpace parts.[4] then None else Some (DateTime.Parse parts.[4])
                Frequency = int parts.[5]
                DividendType = parts.[6]
            } : Dividend)
        else None)
    |> Array.toList

/// Download dividends with monthly chunking, caching, and max parallelism.
/// Months before the current month are cached and not re-downloaded.
/// The current month is always re-downloaded.
/// Results are merged into outputCsvPath.
let downloadDividendsWithConsoleProgress
    (httpClient: HttpClient)
    (apiKey: string)
    (startDate: DateTime)
    (endDate: DateTime option)
    (ct: CancellationToken)
    : Async<Result<Dividend list, string>> =
    async {
        let effectiveEnd = endDate |> Option.defaultValue DateTime.Now
        let months = splitIntoMonths startDate effectiveEnd
        let now = DateTime.Now
        let currentYear, currentMonth = now.Year, now.Month

        let cacheDir = "data/dividends_cache"
        Directory.CreateDirectory cacheDir |> ignore

        let cached, toDownload =
            months
            |> List.partition (fun (y, m) ->
                // A month is cached if it's strictly before the current month AND the cache file exists
                (y < currentYear || (y = currentYear && m < currentMonth))
                && File.Exists(monthCachePath cacheDir y m))

        printfn "Dividend months: %d total, %d cached, %d to download" months.Length cached.Length toDownload.Length

        // Load cached months
        let cachedDividends =
            cached
            |> List.collect (fun (y, m) -> readCacheCsv (monthCachePath cacheDir y m))

        // Download remaining months in parallel (all at once)
        let allResults = Collections.Concurrent.ConcurrentBag<int * int * Result<Dividend list, string>>()
        let completed = ref 0

        let tasks =
            toDownload
            |> List.map (fun (y, m) ->
                async {
                    let monthStart = DateTime(y, m, 1)
                    let monthEnd = monthStart.AddMonths(1).AddDays(-1)
                    // Clamp to the requested range
                    let s = max monthStart startDate
                    let e = min monthEnd effectiveEnd
                    let sStr = s.ToString "yyyy-MM-dd"
                    let eStr = e.ToString "yyyy-MM-dd"
                    let! result = downloadDateRange httpClient apiKey sStr eStr ct
                    allResults.Add((y, m, result))
                    let n = Interlocked.Increment completed
                    printfn "Downloaded month %d/%d: %04d-%02d" n toDownload.Length y m
                })

        do! tasks |> Async.Parallel |> Async.Ignore

        // Collect and cache results
        let mutable downloadedDividends = []
        let mutable error = None
        for (y, m, result) in allResults do
            match result with
            | Error msg when error.IsNone -> error <- Some msg
            | Ok divs ->
                downloadedDividends <- divs @ downloadedDividends
                // Cache if it's a completed (past) month
                if y < currentYear || (y = currentYear && m < currentMonth) then
                    writeCacheCsv (monthCachePath cacheDir y m) divs
            | _ -> ()

        match error with
        | Some msg -> return Error msg
        | None ->
            let all =
                (cachedDividends @ downloadedDividends)
                |> List.sortBy (fun d -> d.ExDividendDate, d.Ticker)
            return Ok all
    }
