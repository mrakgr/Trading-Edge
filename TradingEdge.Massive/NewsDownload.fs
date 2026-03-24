module TradingEdge.NewsDownload

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading

[<CLIMutable>]
type NewsPublisher = {
    [<JsonPropertyName("name")>]
    Name: string

    [<JsonPropertyName("homepage_url")>]
    HomepageUrl: string option

    [<JsonPropertyName("logo_url")>]
    LogoUrl: string option
}

[<CLIMutable>]
type NewsInsight = {
    [<JsonPropertyName("ticker")>]
    Ticker: string

    [<JsonPropertyName("sentiment")>]
    Sentiment: string option

    [<JsonPropertyName("sentiment_reasoning")>]
    SentimentReasoning: string option
}

[<CLIMutable>]
type NewsArticle = {
    [<JsonPropertyName("id")>]
    Id: string

    [<JsonPropertyName("title")>]
    Title: string

    [<JsonPropertyName("author")>]
    Author: string option

    [<JsonPropertyName("published_utc")>]
    PublishedUtc: string

    [<JsonPropertyName("article_url")>]
    ArticleUrl: string

    [<JsonPropertyName("description")>]
    Description: string option

    [<JsonPropertyName("tickers")>]
    Tickers: string[] option

    [<JsonPropertyName("publisher")>]
    Publisher: NewsPublisher

    [<JsonPropertyName("insights")>]
    Insights: NewsInsight[] option
}

[<CLIMutable>]
type NewsResponse = {
    [<JsonPropertyName("results")>]
    Results: NewsArticle[] option

    [<JsonPropertyName("status")>]
    Status: string

    [<JsonPropertyName("next_url")>]
    NextUrl: string option

    [<JsonPropertyName("count")>]
    Count: int
}

type NewsDownloadResult =
    | NewsDownloaded of ticker: string * date: DateTime * articleCount: int
    | NewsSkipped of ticker: string * date: DateTime
    | NewsFailed of ticker: string * date: DateTime * error: string

let private baseUrl = "https://api.polygon.io/v2/reference/news"
let private maxRetries = 5
let private baseDelayMs = 200

let private jsonOptions =
    let options = JsonSerializerOptions()
    options.PropertyNameCaseInsensitive <- true
    options.WriteIndented <- true
    options

let private getOutputPath (outputDir: string) (ticker: string) (date: DateTime) : string =
    let dateStr = date.ToString("yyyy-MM-dd")
    let dir = Path.Combine(outputDir, ticker)
    Directory.CreateDirectory(dir) |> ignore
    Path.Combine(dir, $"{dateStr}.json")

let downloadNews
    (httpClient: HttpClient)
    (apiKey: string)
    (ticker: string)
    (date: DateTime)
    (ct: CancellationToken)
    : Async<Result<NewsArticle[], string>> =
    async {
        let dateStr = date.ToString("yyyy-MM-dd")
        let nextDateStr = date.AddDays(1.0).ToString("yyyy-MM-dd")
        let initialUrl = $"{baseUrl}?ticker={ticker}&published_utc.gte={dateStr}&published_utc.lt={nextDateStr}&limit=1000&order=asc&apiKey={apiKey}"

        let rec fetchPage (url: string) (accumulatedArticles: NewsArticle list) attempt delay =
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
                            return! fetchPage url accumulatedArticles (attempt + 1) newDelay
                    else
                        response.EnsureSuccessStatusCode() |> ignore
                        let! content = response.Content.ReadAsStringAsync(ct) |> Async.AwaitTask
                        let parsed = JsonSerializer.Deserialize<NewsResponse>(content, jsonOptions)

                        let newArticles =
                            parsed.Results
                            |> Option.map Array.toList
                            |> Option.defaultValue []

                        let allArticles = accumulatedArticles @ newArticles

                        match parsed.NextUrl with
                        | Some nextUrl when not (String.IsNullOrEmpty nextUrl) ->
                            let nextUrlWithKey =
                                if nextUrl.Contains("apiKey=") then nextUrl
                                else $"{nextUrl}&apiKey={apiKey}"
                            return! fetchPage nextUrlWithKey allArticles 0 baseDelayMs
                        | _ ->
                            return Ok (allArticles |> List.toArray)
                with
                | :? HttpRequestException as ex when attempt < maxRetries ->
                    do! Async.Sleep 500
                    return! fetchPage url accumulatedArticles (attempt + 1) delay
                | ex ->
                    return Error ex.Message
            }

        return! fetchPage initialUrl [] 0 baseDelayMs
    }

let downloadAndSaveNews
    (httpClient: HttpClient)
    (apiKey: string)
    (outputDir: string)
    (ticker: string)
    (date: DateTime)
    (ct: CancellationToken)
    : Async<NewsDownloadResult> =
    async {
        let outputPath = getOutputPath outputDir ticker date

        if File.Exists outputPath then
            return NewsSkipped(ticker, date)
        else
            let! result = downloadNews httpClient apiKey ticker date ct

            match result with
            | Error msg ->
                return NewsFailed(ticker, date, msg)
            | Ok articles ->
                let articleCount = articles.Length
                use fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize = 65536, useAsync = true)
                do! JsonSerializer.SerializeAsync(fileStream, articles, jsonOptions, ct) |> Async.AwaitTask
                return NewsDownloaded(ticker, date, articleCount)
    }

type NewsProgressCallback = int -> int -> NewsDownloadResult -> unit

let downloadNewsBatch
    (httpClient: HttpClient)
    (apiKey: string)
    (outputDir: string)
    (tickerDates: (string * DateTime) list)
    (maxParallelism: int)
    (progress: NewsProgressCallback option)
    (ct: CancellationToken)
    : Async<NewsDownloadResult list> =
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
                    let! result = downloadAndSaveNews httpClient apiKey outputDir ticker date ct
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

let consoleProgress (completed: int) (total: int) (result: NewsDownloadResult) : unit =
    let formatDate (d: DateTime) = d.ToString("yyyy-MM-dd")
    let status =
        match result with
        | NewsDownloaded(ticker, date, count) ->
            $"Downloaded {ticker} {formatDate date} ({count} articles)"
        | NewsSkipped(ticker, date) ->
            $"Skipped {ticker} {formatDate date} (exists)"
        | NewsFailed(ticker, date, error) ->
            $"Failed {ticker} {formatDate date}: {error}"

    printfn "[%d/%d] %s" completed total status

