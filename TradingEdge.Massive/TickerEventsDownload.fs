module TradingEdge.TickerEventsDownload

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

// Polygon /vX/reference/tickers/{ticker}/events returns the ticker-change
// chain for a given security keyed by composite_figi. Querying from the
// *originating* ticker returns the full chain; querying from a later ticker
// often returns nothing — so we fetch for every ticker we know about and
// merge by composite_figi later.
//
// Raw responses are written to data/tickers/events/{ticker}.json so we
// retain the API payload on disk (the DB is transient). A 404 response is
// also persisted (as `{"status": "NOT_FOUND", ...}`) so re-runs skip it
// instead of re-hitting the API.

let private baseUrl = "https://api.polygon.io/vX/reference/tickers"
let private maxRetries = 5

/// Sanitize a ticker for filesystem use. Tickers like BRK.B and BF.A contain
/// dots; we replace them with underscores to avoid surprises on cross-FS use.
/// Reversed by `unsanitize`.
let sanitize (ticker: string) : string =
    ticker.Replace('.', '_').Replace('/', '-')

let unsanitize (filename: string) : string =
    filename.Replace('_', '.').Replace('-', '/')

/// Fetch one ticker's events as a raw string. Returns:
///   Ok json   on 200 or 404 (both are persisted; the 404 body has status=NOT_FOUND)
///   Error msg on other failures after retries
let private fetchOne
    (httpClient: HttpClient)
    (apiKey: string)
    (ticker: string)
    (ct: CancellationToken)
    : Task<Result<string, string>> =
    task {
        let url = $"{baseUrl}/{Uri.EscapeDataString(ticker)}/events?apiKey={apiKey}"

        let rec attempt n delayMs =
            task {
                try
                    let! resp = httpClient.GetAsync(url, ct)
                    let code = int resp.StatusCode

                    if resp.StatusCode = HttpStatusCode.TooManyRequests then
                        if n >= maxRetries then
                            return Error $"429 after {maxRetries} retries"
                        else
                            let jitter = Random.Shared.Next(20, 120)
                            do! Task.Delay(delayMs + jitter, ct)
                            return! attempt (n + 1) (delayMs * 2 |> min 30_000)
                    elif resp.StatusCode = HttpStatusCode.OK
                         || resp.StatusCode = HttpStatusCode.NotFound then
                        let! body = resp.Content.ReadAsStringAsync(ct)
                        return Ok body
                    else
                        let! body = resp.Content.ReadAsStringAsync(ct)
                        let snippet = if body.Length > 200 then body.Substring(0, 200) + "…" else body
                        return Error $"HTTP {code}: {snippet}"
                with
                | :? OperationCanceledException -> return Error "cancelled"
                | ex when n < maxRetries ->
                    do! Task.Delay(500, ct)
                    return! attempt (n + 1) delayMs
                | ex -> return! Task.FromResult(Error ex.Message)
            }

        return! attempt 0 200
    }

/// Download events for every ticker in `tickers`, writing one JSON per
/// ticker to `outputDir`. Skips files that already exist (idempotent).
/// Returns (succeeded, missing, failed) counts.
let downloadAll
    (httpClient: HttpClient)
    (apiKey: string)
    (tickers: string seq)
    (outputDir: string)
    (parallelism: int)
    (ct: CancellationToken)
    : Task<int * int * int> =
    task {
        Directory.CreateDirectory outputDir |> ignore

        let work = Channel.CreateUnbounded<string>()
        let mutable produced = 0
        let mutable ok = 0
        let mutable notFound = 0
        let mutable failed = 0
        let started = DateTime.UtcNow
        let mutable lastReport = DateTime.UtcNow

        // Producer: enqueue tickers that don't already have a file.
        for t in tickers do
            let path = Path.Combine(outputDir, sanitize t + ".json")
            if not (File.Exists path) then
                work.Writer.TryWrite t |> ignore
                produced <- produced + 1
        work.Writer.Complete()

        let totalQueued = produced
        printfn "  queued %d tickers (skipping %d already-downloaded)"
            totalQueued (Seq.length tickers - totalQueued)

        // Worker tasks: parallelism workers, each draining the channel.
        let worker () =
            task {
                let reader = work.Reader
                let mutable keepGoing = true
                while keepGoing do
                    let! more = reader.WaitToReadAsync(ct).AsTask()
                    if not more then
                        keepGoing <- false
                    else
                        let mutable next = ""
                        while reader.TryRead(&next) do
                            let t = next
                            let path = Path.Combine(outputDir, sanitize t + ".json")
                            match! fetchOne httpClient apiKey t ct with
                            | Ok body ->
                                let tmp = path + ".tmp"
                                File.WriteAllText(tmp, body)
                                File.Move(tmp, path, overwrite = true)
                                if body.Contains("\"NOT_FOUND\"") then
                                    Interlocked.Increment(&notFound) |> ignore
                                else
                                    Interlocked.Increment(&ok) |> ignore
                            | Error msg ->
                                Interlocked.Increment(&failed) |> ignore
                                if failed <= 20 then
                                    eprintfn "  [fail] %s: %s" t msg

                            // Periodic progress
                            let now = DateTime.UtcNow
                            if (now - lastReport).TotalSeconds >= 5.0 then
                                lock work (fun () ->
                                    let done_ = ok + notFound + failed
                                    let elapsed = (now - started).TotalSeconds
                                    let rate = float done_ / max 1.0 elapsed
                                    let eta = float (totalQueued - done_) / max 0.001 rate
                                    printfn "  [%d/%d] ok=%d 404=%d fail=%d  %.1f/s eta=%.0fs"
                                        done_ totalQueued ok notFound failed rate eta
                                    lastReport <- now)
            }

        let workers : Task<unit>[] = [| for _ in 1 .. parallelism -> worker () |]
        let! _ = Task.WhenAll(workers)
        return (ok, notFound, failed)
    }
