module TradingEdge.SplitDownload

open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading

let private jsonOptions =
    JsonFSharpOptions.Default()
        .WithSkippableOptionFields()
        .ToJsonSerializerOptions()

/// JSON response types for the Massive splits API
[<CLIMutable>]
type private SplitResult = {
    [<JsonPropertyName("id")>]
    Id: string

    [<JsonPropertyName("ticker")>]
    Ticker: string

    [<JsonPropertyName("execution_date")>]
    ExecutionDate: string

    [<JsonPropertyName("split_from")>]
    SplitFrom: float

    [<JsonPropertyName("split_to")>]
    SplitTo: float
}

[<CLIMutable>]
type private SplitsResponse = {
    [<JsonPropertyName("results")>]
    Results: SplitResult[] option

    [<JsonPropertyName("next_url")>]
    NextUrl: string option
}

let private baseUrl = "https://api.polygon.io/v3/reference/splits"
let private maxRetries = 5
let private baseDelayMs = 200

/// Download all splits from the Massive API for a date range, in one sort order (`desc` or `asc`)
let private downloadSplitsOrdered
    (httpClient: HttpClient)
    (apiKey: string)
    (startDate: DateTime)
    (endDate: DateTime option)
    (order: string)
    (progress: (int -> int -> unit) option)
    (ct: CancellationToken)
    : Async<Result<Split list, string>> =
    async {
        let startDateStr = startDate.ToString("yyyy-MM-dd")
        let endDateStr = endDate |> Option.map (fun d -> d.ToString("yyyy-MM-dd"))

        let allSplits = ResizeArray<Split>()

        let buildUrl (nextUrl: string option) =
            match nextUrl with
            | Some n when n.Contains("apiKey=") -> n
            | Some n -> $"{n}&apiKey={apiKey}"
            | None ->
                let baseQuery = $"{baseUrl}?execution_date.gte={startDateStr}"
                let withEnd =
                    match endDateStr with
                    | Some e -> $"{baseQuery}&execution_date.lte={e}"
                    | None -> baseQuery
                $"{withEnd}&limit=1000&order={order}&apiKey={apiKey}"

        let rec fetchPage (nextUrl: string option) (pageCount: int) : Async<Result<unit, string>> =
            async {
                if ct.IsCancellationRequested then
                    return Ok ()
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
                                        let currentDelay = int (float delay) + jitter
                                        do! Async.Sleep currentDelay
                                        let newDelay = int (float delay * 1.5)
                                        return! retry (attempt + 1) newDelay
                                else
                                    response.EnsureSuccessStatusCode() |> ignore
                                    let! content = response.Content.ReadAsStringAsync(ct) |> Async.AwaitTask
                                    let parsed = JsonSerializer.Deserialize<SplitsResponse>(content, jsonOptions)
                                    return Ok parsed
                            with ex when attempt < maxRetries ->
                                do! Async.Sleep 500
                                return! retry (attempt + 1) delay
                        }

                    let! result = retry 0 baseDelayMs

                    match result with
                    | Error msg -> return Error msg
                    | Ok response ->
                        match response.Results with
                        | Some results ->
                            for r in results do
                                if r.SplitFrom <> 0.0 && not (String.IsNullOrWhiteSpace r.Ticker) then
                                    allSplits.Add {
                                        Id = VendorId.require $"split {r.Ticker} {r.ExecutionDate}" r.Id
                                        Ticker = r.Ticker
                                        ExecutionDate = DateTime.Parse(r.ExecutionDate)
                                        SplitFrom = r.SplitFrom
                                        SplitTo = r.SplitTo
                                        SplitRatio = r.SplitTo / r.SplitFrom
                                    }
                        | None -> ()

                        let newPageCount = pageCount + 1

                        match progress with
                        | Some report -> report newPageCount allSplits.Count
                        | None -> ()

                        match response.NextUrl with
                        | Some n when not (String.IsNullOrWhiteSpace n) ->
                            return! fetchPage (Some n) newPageCount
                        | _ ->
                            return Ok ()
            }

        let! result = fetchPage None 0

        match result with
        | Error msg -> return Error msg
        | Ok () -> return Ok (allSplits |> Seq.toList)
    }

/// ⭐ THE TWO ORDERS MUST AGREE (user 2026-10-31): the splits endpoint pages like the dividends one — OFFSET pages on a
/// non-unique sort key — so a result set changing under the reader (the vendor's daily load) can skip rows silently
/// (docs/massive_cli_gotchas.md, 2026-10-21: seen on the dividends; the splits were complete that day). The range is
/// fetched in BOTH orders and accepted only when both return the same ids; a disagreement fetches both again after
/// `agreeRetryMs`, up to `agreeAttempts` times, and then fails the download, which leaves splits.csv untouched (on the VPS
/// the wrapper retries the backfill, then starts REDUCING: deploy/vps/oms_day.ps1). A skipped split would be worse than
/// a skipped dividend: n(t) would miss the ratio, and every price comparison across it would carry a ×ratio error.
/// The same rule as DividendDownload.downloadDateRange.
let private agreeAttempts = 10
let private agreeRetryMs = 30_000

let downloadSplits
    (httpClient: HttpClient)
    (apiKey: string)
    (startDate: DateTime)
    (endDate: DateTime option)
    (progress: (int -> int -> unit) option)
    (ct: CancellationToken)
    : Async<Result<Split list, string>> =
    let rec attempt (n: int) =
        async {
            let! desc = downloadSplitsOrdered httpClient apiKey startDate endDate "desc" progress ct
            let! asc = downloadSplitsOrdered httpClient apiKey startDate endDate "asc" progress ct
            match desc, asc with
            | Error msg, _ | _, Error msg -> return Error msg
            | Ok d, Ok a ->
                let ids (xs: Split list) = xs |> List.map (fun x -> x.Id) |> Set.ofList
                let di, ai = ids d, ids a
                if di = ai then return Ok (d |> List.distinctBy (fun x -> x.Id))
                elif n >= agreeAttempts then
                    return Error (sprintf "splits: desc %d and asc %d ids still disagree after %d attempts" di.Count ai.Count n)
                else
                    printfn "  ⚠ splits: desc %d, asc %d ids (%d in one order only), attempt %d: both again in %d s"
                        di.Count ai.Count (Set.count (Set.union (di - ai) (ai - di))) n (agreeRetryMs / 1000)
                    do! Async.Sleep agreeRetryMs
                    return! attempt (n + 1)
        }
    attempt 1

/// Download splits with console progress reporting
let downloadSplitsWithConsoleProgress
    (httpClient: HttpClient)
    (apiKey: string)
    (startDate: DateTime)
    (endDate: DateTime option)
    (ct: CancellationToken)
    : Async<Result<Split list, string>> =
    let progress page total =
        printfn "Page %d: Downloaded splits (Total: %d)" page total

    downloadSplits httpClient apiKey startDate endDate (Some progress) ct
