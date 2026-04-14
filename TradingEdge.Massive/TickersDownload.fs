module TradingEdge.TickersDownload

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

[<CLIMutable>]
type private TickerResult = {
    [<JsonPropertyName("ticker")>]
    Ticker: string

    [<JsonPropertyName("name")>]
    Name: string option

    [<JsonPropertyName("market")>]
    Market: string option

    [<JsonPropertyName("type")>]
    Type: string option

    [<JsonPropertyName("active")>]
    Active: bool

    [<JsonPropertyName("primary_exchange")>]
    PrimaryExchange: string option
}

[<CLIMutable>]
type private TickersResponse = {
    [<JsonPropertyName("results")>]
    Results: TickerResult[] option

    [<JsonPropertyName("next_url")>]
    NextUrl: string option

    [<JsonPropertyName("status")>]
    Status: string
}

let private baseUrl = "https://api.polygon.io/v3/reference/tickers"
let private maxRetries = 5
let private baseDelayMs = 200

/// Download all tickers of a given Polygon `type` (e.g. "CS", "ADRC", "ETF").
/// Pass `active=true` for currently-listed, `active=false` for delisted/historical.
/// Returns (ticker, name, type) triples.
let downloadTickersOfType
    (httpClient: HttpClient)
    (apiKey: string)
    (tickerType: string)
    (active: bool)
    (progress: (int -> int -> unit) option)
    (ct: CancellationToken)
    : Async<Result<(string * string * string) list, string>> =
    async {
        let allTickers = ResizeArray<string * string * string>()
        let activeStr = if active then "true" else "false"

        let buildUrl (nextUrl: string option) =
            match nextUrl with
            | Some n when n.Contains("apiKey=") -> n
            | Some n -> $"{n}&apiKey={apiKey}"
            | None ->
                $"{baseUrl}?type={tickerType}&market=stocks&active={activeStr}&limit=1000&apiKey={apiKey}"

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
                                    let parsed = JsonSerializer.Deserialize<TickersResponse>(content, jsonOptions)
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
                                if not (String.IsNullOrWhiteSpace r.Ticker) then
                                    let name = r.Name |> Option.defaultValue ""
                                    let typ = r.Type |> Option.defaultValue tickerType
                                    allTickers.Add((r.Ticker, name, typ))
                        | None -> ()

                        let newPageCount = pageCount + 1

                        match progress with
                        | Some report -> report newPageCount allTickers.Count
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
        | Ok () -> return Ok (allTickers |> Seq.toList)
    }

/// Download all reference-ticker security types we classify on:
///   CS   — common stock
///   ADRC — American Depositary Receipt (Common)
///   ETF, ETN, ETV, ETS — exchange-traded products
/// For each type we fetch both active=true (currently listed) and active=false
/// (delisted/acquired/merged) so that historical backtests can still classify
/// tickers that no longer trade. These populate `ticker_reference(ticker, type)`.
/// Downstream filters (e.g. stocks_in_play) select on `type IN ('CS', 'ADRC')`.
let downloadAllReferenceTickers
    (httpClient: HttpClient)
    (apiKey: string)
    (ct: CancellationToken)
    : Async<Result<(string * string * string) list, string>> =
    async {
        let tickerTypes = [ "CS"; "ADRC"; "ETF"; "ETN"; "ETV"; "ETS" ]
        let all = ResizeArray<string * string * string>()
        let mutable error : string option = None

        for tickerType in tickerTypes do
            for active in [ true; false ] do
                if error.IsNone then
                    let activeLabel = if active then "active" else "inactive"
                    let progress page total =
                        printfn "  [%s %s] page %d, total %d" tickerType activeLabel page total
                    let! result = downloadTickersOfType httpClient apiKey tickerType active (Some progress) ct
                    match result with
                    | Ok rows ->
                        printfn "  [%s %s] downloaded %d tickers" tickerType activeLabel rows.Length
                        all.AddRange(rows)
                    | Error msg ->
                        error <- Some $"Failed to download type {tickerType} ({activeLabel}): {msg}"

        match error with
        | Some msg -> return Error msg
        | None -> return Ok (all |> Seq.toList)
    }
