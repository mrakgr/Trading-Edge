module TradingEdge.DividendDownload

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open FSharp.Control

let private jsonOptions =
    JsonFSharpOptions.Default()
        .WithSkippableOptionFields()
        .ToJsonSerializerOptions()

/// JSON response types for the Polygon dividends API
[<CLIMutable>]
type private DividendResult = {
    [<JsonPropertyName("id")>]
    Id: string

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

    [<JsonPropertyName("currency")>]
    Currency: string option
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
            // See Split.Id — one ex-date routinely carries several payments.
            Id = VendorId.require $"dividend {r.Ticker} {r.ExDividendDate}" r.Id
            Ticker = r.Ticker
            ExDividendDate = DateTime.Parse r.ExDividendDate
            CashAmount = r.CashAmount
            DeclarationDate = r.DeclarationDate |> Option.bind tryParseDate
            PayDate = r.PayDate |> Option.bind tryParseDate
            Frequency = r.Frequency
            DividendType = if String.IsNullOrWhiteSpace r.DividendType then "CD" else r.DividendType
            Currency = r.Currency |> Option.filter (String.IsNullOrWhiteSpace >> not)
        }
    else None

/// ⚠ `id` FIRST — it is the primary key. PUBLIC so the final data/dividends.csv writer uses the SAME serialization
/// as `readCsv`. They used to be duplicated, and a copy in Program.fs was missed when `id` was added — producing a
/// 7-column file the ingest rejected. `currency` (2026-10-21) LAST.
let csvHeader = "id,ticker,ex_dividend_date,cash_amount,declaration_date,pay_date,frequency,dividend_type,currency"

let dividendToCsvLine (d: Dividend) =
    let exDateStr = d.ExDividendDate.ToString("yyyy-MM-dd")
    let declDateStr = d.DeclarationDate |> Option.map (fun dt -> dt.ToString("yyyy-MM-dd")) |> Option.defaultValue ""
    let payDateStr = d.PayDate |> Option.map (fun dt -> dt.ToString("yyyy-MM-dd")) |> Option.defaultValue ""
    let currencyStr = d.Currency |> Option.defaultValue ""
    $"{d.Id},{d.Ticker},{exDateStr},{d.CashAmount},{declDateStr},{payDateStr},{d.Frequency},{d.DividendType},{currencyStr}"

/// Download all dividends for a single date range (sequential pagination), in one sort order (`desc`, the API's
/// default, or `asc`).
let private downloadDateRangeOrdered
    (httpClient: HttpClient)
    (apiKey: string)
    (startDateStr: string)
    (endDateStr: string)
    (order: string)
    (ct: CancellationToken)
    : Async<Result<Dividend list, string>> =
    async {
        let results = ResizeArray<Dividend>()

        let buildUrl (nextUrl: string option) =
            match nextUrl with
            | Some n when n.Contains "apiKey=" -> n
            | Some n -> $"{n}&apiKey={apiKey}"
            | None -> $"{baseUrl}?ex_dividend_date.gte={startDateStr}&ex_dividend_date.lte={endDateStr}&limit=1000&order={order}&apiKey={apiKey}"

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

/// ⚠ THE API CAN SILENTLY RETURN AN INCOMPLETE CURRENT MONTH (2026-10-21, docs/massive_cli_gotchas.md). For ~15 minutes
/// (08:51–09:04 UTC) the default (desc) range query for October skipped 100 of 446 records dated 2026-10-02 (ROP, RSG,
/// SATA, … — a contiguous block of tickers at a page boundary) while `order=asc` returned all of them; a single-date
/// query dropped others (10-21: 32 of 127). By 09:17 every shape agreed. Pages are OFFSET-based and sorted on a
/// non-unique key, so a result set changing under the reader can skip rows. Past months agree in every order.
/// ⭐ THE TWO ORDERS MUST AGREE (user 2026-10-31): every month is fetched in BOTH orders and accepted only when both
/// return the same ids; a disagreement fetches both again after `agreeRetryMs`, up to `agreeAttempts` times, and then
/// fails the month — and so the download, which leaves the file untouched (on the VPS the wrapper retries the backfill,
/// then starts REDUCING: deploy/vps/oms_day.ps1). Bounded so a vendor fault can never hang the morning's backfill. It
/// replaced the merge of the two orders AND the carry-over of records the download lacked (carryOverDropped, removed:
/// it carried PUTKF's revised dividend back to its old month — the same id twice, 2026-10-29 and 10-31, and the ingest
/// and the v2 build refused). Without either the ingest RETIRES a skipped id (retireAbsentIds) until the next run.
let private agreeAttempts = 10
let private agreeRetryMs = 30_000

let private downloadDateRange
    (httpClient: HttpClient)
    (apiKey: string)
    (startDateStr: string)
    (endDateStr: string)
    (ct: CancellationToken)
    : Async<Result<Dividend list, string>> =
    let rec attempt (n: int) =
        async {
            let! desc = downloadDateRangeOrdered httpClient apiKey startDateStr endDateStr "desc" ct
            let! asc = downloadDateRangeOrdered httpClient apiKey startDateStr endDateStr "asc" ct
            match desc, asc with
            | Error msg, _ | _, Error msg -> return Error msg
            | Ok d, Ok a ->
                let ids (xs: Dividend list) = xs |> List.map (fun x -> x.Id) |> Set.ofList
                let di, ai = ids d, ids a
                if di = ai then return Ok (d |> List.distinctBy (fun x -> x.Id))
                elif n >= agreeAttempts then
                    return Error (sprintf "dividends %s..%s: desc %d and asc %d ids still disagree after %d attempts"
                                      startDateStr endDateStr di.Count ai.Count n)
                else
                    printfn "  ⚠ dividends %s..%s: desc %d, asc %d ids (%d in one order only), attempt %d: both again in %d s"
                        startDateStr endDateStr di.Count ai.Count (Set.count (Set.union (di - ai) (ai - di))) n (agreeRetryMs / 1000)
                    do! Async.Sleep agreeRetryMs
                    return! attempt (n + 1)
        }
    attempt 1

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

/// Parse one line written by `dividendToCsvLine`.
let private parseCsvLine (line: string) : Dividend option =
    let parts = line.Split(',')
    if parts.Length >= 9 then
        Some {
            Id = parts.[0]
            Ticker = parts.[1]
            ExDividendDate = DateTime.Parse parts.[2]
            CashAmount = float parts.[3]
            DeclarationDate = if String.IsNullOrWhiteSpace parts.[4] then None else Some (DateTime.Parse parts.[4])
            PayDate = if String.IsNullOrWhiteSpace parts.[5] then None else Some (DateTime.Parse parts.[5])
            Frequency = int parts.[6]
            DividendType = parts.[7]
            Currency = if String.IsNullOrWhiteSpace parts.[8] then None else Some parts.[8]
        }
    else None

/// Download every month of [startDate, endDate] into `outputPath` (data/dividends.csv), STREAMED (2026-10-21).
///
/// ⚠ NO CACHE (removed 2026-10-21). A monthly cache froze each past month at its first download, but the vendor keeps
/// revising history: against a full re-download the cache had hidden 250 new ids, 66 deleted ids and 407 changed
/// amounts/dates in months before 2026-09, back to 2003 — and the ingest's mirror rule cannot work on a file that is
/// partly an old snapshot. So every month is fetched every run, in both orders, which must agree (see downloadDateRange).
///
/// ⭐ STREAMED: the months go through a channel to `workers` workers; each downloads its month (both orders, agreeing)
/// and writes it to `<outputPath>.parts/yyyy-MM.csv` at once; a reporter (the only reader
/// of the results channel) counts and prints; the file is then assembled from the parts in month order and swapped in.
/// So only `workers` months are in memory at a time. The old shape — all 298 months in parallel, every record held
/// until the end — peaked at 3.7 GB RSS on a 7 GB VPS.
/// Any failed month fails the whole download and leaves `outputPath` untouched. Returns the rows written.
///
/// ⭐ `recent` (production, user 2026-10-29): the range is downloaded ON TOP OF the existing file — its rows dated
/// before `startDate` are kept as they are and every row from `startDate` on comes from the download (2026-10-29,
/// after PUTKF's duplicate stopped the Oms). The live system needs the recent events only; research keeps the full
/// rewrite. No existing file = an error.
let downloadDividendsToCsv
    (httpClient: HttpClient)
    (apiKey: string)
    (startDate: DateTime)
    (endDate: DateTime option)
    (outputPath: string)
    (recent: bool)
    (workers: int)
    (ct: CancellationToken)
    : Task<Result<int, string>> =
    task {
      if recent && not (File.Exists outputPath) then
        return Error $"{outputPath} is missing: a recent-only download needs the existing file (run the full range once)"
      else
        let effectiveEnd = endDate |> Option.defaultValue DateTime.Now
        let months = splitIntoMonths startDate effectiveEnd
        let partsDir = outputPath + ".parts"
        if Directory.Exists partsDir then Directory.Delete(partsDir, true)
        Directory.CreateDirectory partsDir |> ignore
        let partPath (y: int, m: int) = Path.Combine(partsDir, $"{y}-{m:D2}.csv")
        printfn "Dividend months: %d to download, %d at a time" months.Length workers

        let todo = Channel.CreateUnbounded<int * int>()
        for ym in months do
            do! todo.Writer.WriteAsync(ym, ct)
        todo.Writer.Complete()
        let results = Channel.CreateUnbounded<Result<int, string>>()

        let worker () =
            task {
                for ym in todo.Reader.ReadAllAsync(ct) do
                    let y, m = ym
                    let monthStart = DateTime(y, m, 1)
                    let s = max monthStart startDate
                    let e = min (monthStart.AddMonths(1).AddDays(-1)) effectiveEnd
                    let! r =
                        Async.StartAsTask(
                            downloadDateRange httpClient apiKey (s.ToString "yyyy-MM-dd") (e.ToString "yyyy-MM-dd") ct,
                            cancellationToken = ct)
                    match r with
                    | Error msg -> do! results.Writer.WriteAsync(Error $"{y}-{m:D2}: {msg}", ct)
                    | Ok fresh ->
                        let lines =
                            fresh |> List.sortBy (fun d -> d.ExDividendDate, d.Ticker, d.Id) |> List.map dividendToCsvLine
                        do! File.WriteAllLinesAsync(partPath (y, m), lines, ct)
                        do! results.Writer.WriteAsync(Ok fresh.Length, ct)
            }

        // the reporter: the only reader of `results`, so the only owner of the totals
        let reporter =
            task {
                let mutable finished = 0
                let mutable rows = 0
                let mutable error = None
                for r in results.Reader.ReadAllAsync(ct) do
                    finished <- finished + 1
                    match r with
                    | Ok n -> rows <- rows + n
                    | Error msg -> if error.IsNone then error <- Some msg
                    if finished % 50 = 0 || finished = months.Length then
                        printfn "Downloaded month %d/%d" finished months.Length
                return (match error with Some msg -> Error msg | None -> Ok rows)
            }

        let! _ = Task.WhenAll [| for _ in 1 .. workers -> worker () |]
        results.Writer.Complete()
        let! outcome = reporter

        match outcome with
        | Error msg ->
            Directory.Delete(partsDir, true)
            return Error msg
        | Ok rows ->
            let tmp = outputPath + ".tmp"
            let mutable kept = 0
            do
                use w = new StreamWriter(tmp)
                w.WriteLine csvHeader
                if recent then
                    // the existing file's rows before the range, as they are (the ex-date is the third field, yyyy-MM-dd)
                    let before = startDate.ToString "yyyy-MM-dd"
                    for line in File.ReadLines outputPath |> Seq.skip 1 do
                        let parts = line.Split(',', 4)
                        if parts.Length >= 3 && String.CompareOrdinal(parts.[2], before) < 0 then
                            w.WriteLine line
                            kept <- kept + 1
                for ym in months do
                    for line in File.ReadLines(partPath ym) do
                        w.WriteLine line
            File.Move(tmp, outputPath, true)
            Directory.Delete(partsDir, true)
            if recent then printfn "Kept %d dividends dated before %s from the existing file" kept (startDate.ToString "yyyy-MM-dd")
            return Ok (rows + kept)
    }
