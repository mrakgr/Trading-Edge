module TradingEdge.TradesDownload

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open DuckDB.NET.Data

/// A single trade from the Polygon API
[<CLIMutable>]
type Trade = {
    [<JsonPropertyName("conditions")>]
    Conditions: int[] option

    [<JsonPropertyName("exchange")>]
    Exchange: int

    [<JsonPropertyName("id")>]
    Id: string

    [<JsonPropertyName("participant_timestamp")>]
    ParticipantTimestamp: int64

    [<JsonPropertyName("price")>]
    Price: float

    [<JsonPropertyName("sequence_number")>]
    SequenceNumber: int64

    [<JsonPropertyName("sip_timestamp")>]
    SipTimestamp: int64

    [<JsonPropertyName("size")>]
    Size: float

    [<JsonPropertyName("tape")>]
    Tape: int option
}

/// Response from the Polygon trades API
[<CLIMutable>]
type TradesResponse = {
    [<JsonPropertyName("results")>]
    Results: Trade[] option

    [<JsonPropertyName("status")>]
    Status: string

    [<JsonPropertyName("request_id")>]
    RequestId: string

    [<JsonPropertyName("next_url")>]
    NextUrl: string option
}

/// Result of downloading trades for a ticker/date
type TradesDownloadResult =
    | TradesDownloaded of ticker: string * date: DateTime * tradeCount: int
    | TradesSkipped of ticker: string * date: DateTime
    | TradesFailed of ticker: string * date: DateTime * error: string

let private baseUrl = "https://api.polygon.io/v3/trades"
let private maxRetries = 5
let private baseDelayMs = 200

let private jsonOptions =
    let options = JsonSerializerOptions()
    options.PropertyNameCaseInsensitive <- true
    options

/// Generate output file path for trades data
let private getOutputPath (outputDir: string) (ticker: string) (date: DateTime) : string =
    let dateStr = date.ToString("yyyy-MM-dd")
    let dir = Path.Combine(outputDir, ticker)
    Directory.CreateDirectory(dir) |> ignore
    Path.Combine(dir, $"{dateStr}.parquet")

// =============================================================================
// Parquet writer
// =============================================================================
//
// Schema (drops Polygon fields never read downstream: id, exchange,
// sequence_number, tape):
//
//   participant_timestamp BIGINT   (nanoseconds since Unix epoch; may be 0)
//   sip_timestamp         BIGINT   (nanoseconds since Unix epoch)
//   price                 DOUBLE
//   size                  DOUBLE
//   conditions            INTEGER[]  -- nullable, Polygon omits on some trades
//
// We build an in-memory DuckDB table via the appender API, then COPY ... TO
// Parquet. COPY+appender is the fastest .NET->Parquet path available in
// DuckDB.NET. zstd level 3 is the sweet spot for ratio vs CPU.

let private writeTradesToParquet (outputPath: string) (trades: Trade[]) : unit =
    // DuckDB file paths use forward slashes in the SQL literal; keep it simple
    // by normalizing on the way in.
    let normalized = outputPath.Replace('\\', '/')
    // Quote any single-quotes in the path to avoid breaking the SQL string.
    let escaped = normalized.Replace("'", "''")

    use connection = new DuckDBConnection("Data Source=:memory:")
    connection.Open()

    use createCmd = connection.CreateCommand()
    createCmd.CommandText <-
        "CREATE TABLE trades (
            participant_timestamp BIGINT,
            sip_timestamp BIGINT,
            price DOUBLE,
            size DOUBLE,
            conditions INTEGER[]
         )"
    createCmd.ExecuteNonQuery() |> ignore

    use appender = connection.CreateAppender("trades")
    for t in trades do
        let row = appender.CreateRow()
        row.AppendValue(Nullable t.ParticipantTimestamp) |> ignore
        row.AppendValue(Nullable t.SipTimestamp) |> ignore
        row.AppendValue(Nullable t.Price) |> ignore
        row.AppendValue(Nullable t.Size) |> ignore
        match t.Conditions with
        | Some arr when not (isNull arr) ->
            row.AppendValue<int>(arr :> System.Collections.Generic.IEnumerable<int>) |> ignore
        | _ ->
            row.AppendNullValue() |> ignore
        row.EndRow()
    appender.Close()

    use copyCmd = connection.CreateCommand()
    copyCmd.CommandText <-
        sprintf "COPY trades TO '%s' (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3)" escaped
    copyCmd.ExecuteNonQuery() |> ignore

/// Download all trades for a single ticker and date (handles pagination)
let downloadTrades
    (httpClient: HttpClient)
    (apiKey: string)
    (ticker: string)
    (date: DateTime)
    (ct: CancellationToken)
    : Async<Result<Trade[], string>> =
    async {
        let dateStr = date.ToString("yyyy-MM-dd")
        let nextDateStr = date.AddDays(1.0).ToString("yyyy-MM-dd")

        // Build initial URL with timestamp range for the day
        let initialUrl = $"{baseUrl}/{ticker}?timestamp.gte={dateStr}&timestamp.lt={nextDateStr}&limit=50000&apiKey={apiKey}"

        let rec fetchPage (url: string) (accumulatedTrades: Trade list) attempt delay =
            async {
                try
                    let! response = httpClient.GetAsync(url, ct) |> Async.AwaitTask

                    if response.StatusCode = HttpStatusCode.TooManyRequests then
                        if attempt >= maxRetries then
                            return Error "Rate limited: max retries exceeded"
                        else
                            let jitter = Random.Shared.Next(10, 100)
                            let currentDelay = delay + jitter
                            do! Async.Sleep currentDelay
                            let newDelay = int (float delay * 1.5)
                            return! fetchPage url accumulatedTrades (attempt + 1) newDelay
                    elif response.StatusCode = HttpStatusCode.NotFound then
                        return Error $"No data found for {ticker} on {dateStr}"
                    else
                        response.EnsureSuccessStatusCode() |> ignore
                        let! content = response.Content.ReadAsStringAsync(ct) |> Async.AwaitTask
                        let parsed = JsonSerializer.Deserialize<TradesResponse>(content, jsonOptions)

                        let newTrades =
                            parsed.Results
                            |> Option.map Array.toList
                            |> Option.defaultValue []

                        let allTrades = accumulatedTrades @ newTrades

                        match parsed.NextUrl with
                        | Some nextUrl when not (String.IsNullOrEmpty nextUrl) ->
                            // Add API key to next_url if not present
                            let nextUrlWithKey =
                                if nextUrl.Contains("apiKey=") then nextUrl
                                else $"{nextUrl}&apiKey={apiKey}"
                            return! fetchPage nextUrlWithKey allTrades 0 baseDelayMs
                        | _ ->
                            return Ok (allTrades |> List.toArray)
                with
                | :? HttpRequestException as ex when attempt < maxRetries ->
                    do! Async.Sleep 500
                    return! fetchPage url accumulatedTrades (attempt + 1) delay
                | ex ->
                    return Error ex.Message
            }

        return! fetchPage initialUrl [] 0 baseDelayMs
    }

/// Download and save trades for a single ticker and date
let downloadAndSaveTrades
    (httpClient: HttpClient)
    (apiKey: string)
    (outputDir: string)
    (ticker: string)
    (date: DateTime)
    (ct: CancellationToken)
    : Async<TradesDownloadResult> =
    async {
        let outputPath = getOutputPath outputDir ticker date

        if File.Exists outputPath then
            return TradesSkipped(ticker, date)
        else
            printfn "Downloading trades for %s %s..." ticker (date.ToString("yyyy-MM-dd"))
            let! result = downloadTrades httpClient apiKey ticker date ct

            match result with
            | Error msg ->
                return TradesFailed(ticker, date, msg)
            | Ok trades ->
                let tradeCount = trades.Length
                printfn "Downloaded %d trades, writing Parquet..." tradeCount

                writeTradesToParquet outputPath trades
                printfn "Successfully saved to %s" outputPath

                return TradesDownloaded(ticker, date, tradeCount)
    }

/// Progress callback type for trades downloads
type TradesProgressCallback = int -> int -> TradesDownloadResult -> unit

/// Download trades for multiple ticker/date pairs
let downloadTradesBatch
    (httpClient: HttpClient)
    (apiKey: string)
    (outputDir: string)
    (tickerDates: (string * DateTime) list)
    (maxParallelism: int)
    (progress: TradesProgressCallback option)
    (ct: CancellationToken)
    : Async<TradesDownloadResult list> =
    async {
        let total = tickerDates.Length
        let completed = ref 0

        let reportProgress result =
            let c = Threading.Interlocked.Increment(completed)
            match progress with
            | Some callback -> callback c total result
            | None -> ()

        use semaphore = new SemaphoreSlim(maxParallelism, maxParallelism)

        let downloadWithSemaphore (ticker, date) =
            async {
                do! semaphore.WaitAsync(ct) |> Async.AwaitTask
                try
                    let! result = downloadAndSaveTrades httpClient apiKey outputDir ticker date ct
                    reportProgress result
                    return result
                finally
                    semaphore.Release() |> ignore
            }

        let! results =
            tickerDates
            |> List.map downloadWithSemaphore
            |> Async.Parallel

        return results |> Array.toList
    }

/// Default progress reporter that prints to console
let consoleProgress (completed: int) (total: int) (result: TradesDownloadResult) : unit =
    let formatDate (d: DateTime) = d.ToString("yyyy-MM-dd")
    let status =
        match result with
        | TradesDownloaded(ticker, date, trades) ->
            $"Downloaded {ticker} {formatDate date} ({trades} trades)"
        | TradesSkipped(ticker, date) ->
            $"Skipped {ticker} {formatDate date} (exists)"
        | TradesFailed(ticker, date, error) ->
            $"Failed {ticker} {formatDate date}: {error}"

    printfn "[%d/%d] %s" completed total status
