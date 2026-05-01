module TradingEdge.CryptoData.Manifest

open System
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading

// =============================================================================
// Listing-driven download manifest
// =============================================================================
//
// Build the exact set of S3 archives we need by listing both
//   data/futures/um/monthly/trades/{SYMBOL}/  -> {SYMBOL}-trades-{YYYY-MM}.zip
//   data/futures/um/daily/trades/{SYMBOL}/    -> {SYMBOL}-trades-{YYYY-MM-DD}.zip
//
// then for each (symbol, month) inside the requested window:
//   - prefer monthly when:
//       * a monthly archive exists for that month, AND
//       * every day of that month is inside the [startDate, endDate] window
//         (i.e. neither the first nor last partial month at the window edges)
//   - else use individual dailies
//
// Output: a Job[] of MonthlyJob | DailyJob with exact key + size, so the
// downloader does no listing of its own and never sees a 404. The user-
// facing size estimate is exactly the bytes that will transit.
//
// Why we list both unconditionally: the monthly archive is a single
// concatenated CSV, not a tar of per-day CSVs (verified empirically). To
// emit per-day parquets we stream-rotate inside the converter; partial-month
// edges can't use the monthly because we'd be downloading days outside the
// window.

let private s3ListUrl = "https://s3-ap-northeast-1.amazonaws.com/data.binance.vision/"
let private monthlyPrefix = "data/futures/um/monthly/trades/"
let private dailyPrefix = "data/futures/um/daily/trades/"

let private contentsRe =
    Regex(@"<Contents>\s*<Key>([^<]+)</Key>(?:[^<]|<(?!Size))*<Size>(\d+)</Size>",
        RegexOptions.Compiled ||| RegexOptions.Singleline)
let private isTruncatedRe = Regex("<IsTruncated>([^<]+)</IsTruncated>", RegexOptions.Compiled)
let private nextMarkerRe = Regex("<NextMarker>([^<]+)</NextMarker>", RegexOptions.Compiled)

// Patterns must allow {SYMBOL} chars verbatim (Binance includes digits, e.g.
// 1000PEPEUSDT). Anchor and use the directory's symbol as the back-reference
// so we can't match a sibling-bucket entry.
let private monthlyKeyRe (symbol: string) =
    Regex(sprintf @"^data/futures/um/monthly/trades/%s/%s-trades-(\d{4})-(\d{2})\.zip$"
              (Regex.Escape symbol) (Regex.Escape symbol),
          RegexOptions.Compiled)
let private dailyKeyRe (symbol: string) =
    Regex(sprintf @"^data/futures/um/daily/trades/%s/%s-trades-(\d{4}-\d{2}-\d{2})\.zip$"
              (Regex.Escape symbol) (Regex.Escape symbol),
          RegexOptions.Compiled)

type MonthlyEntry = {
    Symbol: string
    Year: int
    Month: int   // 1-12
    SizeBytes: int64
}

type DailyEntry = {
    Symbol: string
    Date: DateTime
    SizeBytes: int64
}

type Job =
    | MonthlyJob of MonthlyEntry
    | DailyJob of DailyEntry
    member this.Symbol =
        match this with
        | MonthlyJob m -> m.Symbol
        | DailyJob d -> d.Symbol
    member this.SizeBytes =
        match this with
        | MonthlyJob m -> m.SizeBytes
        | DailyJob d -> d.SizeBytes
    member this.Key =
        match this with
        | MonthlyJob m ->
            sprintf "%s%s/%s-trades-%04d-%02d.zip"
                monthlyPrefix m.Symbol m.Symbol m.Year m.Month
        | DailyJob d ->
            sprintf "%s%s/%s-trades-%s.zip"
                dailyPrefix d.Symbol d.Symbol (d.Date.ToString("yyyy-MM-dd"))

// Iterate over an S3 listing, paginating until !IsTruncated. The Contents
// regex is a streamy match across the whole body — we don't need to maintain
// a parser state since each <Contents> block is self-contained.
let private listPrefix
    (http: HttpClient)
    (prefix: string)
    : Async<(string * int64)[]> =
    async {
        let acc = ResizeArray<string * int64>()
        let mutable marker : string option = None
        let mutable keepGoing = true
        while keepGoing do
            let url =
                match marker with
                | Some m -> sprintf "%s?prefix=%s&marker=%s" s3ListUrl prefix (Uri.EscapeDataString m)
                | None -> sprintf "%s?prefix=%s" s3ListUrl prefix
            let! body = http.GetStringAsync(url) |> Async.AwaitTask
            let mutable lastKey = ""
            for m in contentsRe.Matches body do
                let key = m.Groups.[1].Value
                let size = Int64.Parse(m.Groups.[2].Value)
                acc.Add(key, size)
                lastKey <- key
            let truncated =
                let m = isTruncatedRe.Match body
                m.Success && m.Groups.[1].Value = "true"
            if truncated then
                let nm = nextMarkerRe.Match body
                marker <-
                    if nm.Success then Some nm.Groups.[1].Value
                    elif lastKey <> "" then Some lastKey
                    else keepGoing <- false; None
            else
                keepGoing <- false
        return acc.ToArray()
    }

let private daysInMonth (year: int) (month: int) : int =
    DateTime.DaysInMonth(year, month)

/// Lists monthly + daily archives for one symbol and produces a deduped Job[]
/// inside [startDate, endDate]. Monthly is preferred when it strictly tiles
/// the window for that month; otherwise dailies stand in.
let private buildJobsForSymbol
    (http: HttpClient)
    (symbol: string)
    (startDate: DateTime)
    (endDate: DateTime)
    : Async<Job[]> =
    async {
        let mPrefix = sprintf "%s%s/" monthlyPrefix symbol
        let dPrefix = sprintf "%s%s/" dailyPrefix symbol
        // Both listings in parallel — they hit the same bucket but different
        // prefixes, so the server processes them independently.
        let! monthlyListing = listPrefix http mPrefix |> Async.StartChild
        let! dailyListing = listPrefix http dPrefix |> Async.StartChild
        let! monthlyResults = monthlyListing
        let! dailyResults = dailyListing

        let mRe = monthlyKeyRe symbol
        let dRe = dailyKeyRe symbol

        let monthlies =
            monthlyResults
            |> Array.choose (fun (key, size) ->
                let m = mRe.Match key
                if not m.Success then None
                else
                    let y = Int32.Parse m.Groups.[1].Value
                    let mo = Int32.Parse m.Groups.[2].Value
                    Some { Symbol = symbol; Year = y; Month = mo; SizeBytes = size })

        let dailies =
            dailyResults
            |> Array.choose (fun (key, size) ->
                let m = dRe.Match key
                if not m.Success then None
                else
                    let d = DateTime.ParseExact(m.Groups.[1].Value, "yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture)
                    Some { Symbol = symbol; Date = d; SizeBytes = size })

        // For each monthly: the month tile is [first, last] of that month.
        // We use the monthly archive only when both edges of the tile fall
        // inside the window; that way the monthly's content matches what
        // dailies would have given us.
        let monthlyByMonth =
            monthlies
            |> Array.map (fun m -> (m.Year, m.Month), m)
            |> Map.ofArray

        // Indexed daily lookup so we can later say "is this date covered by a
        // daily entry?".
        let dailyByDate =
            dailies
            |> Array.map (fun d -> d.Date, d)
            |> Map.ofArray

        // Walk months in the window, decide per-month strategy.
        let firstMonth = DateTime(startDate.Year, startDate.Month, 1)
        let lastMonth = DateTime(endDate.Year, endDate.Month, 1)

        let jobs = ResizeArray<Job>()
        let mutable cursor = firstMonth
        while cursor <= lastMonth do
            let y, mo = cursor.Year, cursor.Month
            let monthFirst = DateTime(y, mo, 1)
            let monthLast = DateTime(y, mo, daysInMonth y mo)
            let tileFullyInside = monthFirst >= startDate && monthLast <= endDate
            match Map.tryFind (y, mo) monthlyByMonth with
            | Some m when tileFullyInside ->
                jobs.Add(MonthlyJob m)
            | _ ->
                // Use dailies for this month, clipped to the window. A daily
                // entry's presence in the listing already implies the file
                // exists, so no 404s.
                let monthStart = max startDate monthFirst
                let monthEnd = min endDate monthLast
                let mutable d = monthStart
                while d <= monthEnd do
                    match Map.tryFind d dailyByDate with
                    | Some entry -> jobs.Add(DailyJob entry)
                    | None -> ()  // Symbol wasn't trading that day — skip silently.
                    d <- d.AddDays 1.0
            cursor <- cursor.AddMonths 1

        return jobs.ToArray()
    }

/// Build the manifest across many symbols in parallel.
let buildManifest
    (http: HttpClient)
    (symbols: string[])
    (startDate: DateTime)
    (endDate: DateTime)
    (parallelism: int)
    : Async<Job[]> =
    async {
        use sem = new SemaphoreSlim(parallelism, parallelism)
        let completed = ref 0
        let oneSymbol s =
            async {
                do! sem.WaitAsync() |> Async.AwaitTask
                try
                    try
                        let! jobs = buildJobsForSymbol http s startDate endDate
                        let c = Interlocked.Increment completed
                        if c % 25 = 0 || c = symbols.Length then
                            printfn "  manifested %d/%d symbols" c symbols.Length
                        return jobs
                    with ex ->
                        eprintfn "  manifest %s failed: %s" s ex.Message
                        return [||]
                finally
                    sem.Release() |> ignore
            }
        let! results = symbols |> Array.map oneSymbol |> Async.Parallel
        return Array.concat results
    }

/// Summary stats for telemetry.
type ManifestStats = {
    Symbols: int
    SymbolsWithData: int
    MonthlyJobs: int
    DailyJobs: int
    TotalBytes: int64
    PerSymbol: (string * int * int * int64)[]  // symbol, monthly, daily, bytes
}

let summarize (universeSize: int) (jobs: Job[]) : ManifestStats =
    let monthly = jobs |> Array.filter (function MonthlyJob _ -> true | _ -> false)
    let daily = jobs |> Array.filter (function DailyJob _ -> true | _ -> false)
    let totalBytes = jobs |> Array.sumBy (fun j -> j.SizeBytes)
    let perSymbol =
        jobs
        |> Array.groupBy (fun j -> j.Symbol)
        |> Array.map (fun (sym, js) ->
            let nm = js |> Array.sumBy (function MonthlyJob _ -> 1 | _ -> 0)
            let nd = js |> Array.sumBy (function DailyJob _ -> 1 | _ -> 0)
            let b = js |> Array.sumBy (fun j -> j.SizeBytes)
            sym, nm, nd, b)
        |> Array.sortByDescending (fun (_, _, _, b) -> b)
    {
        Symbols = universeSize
        SymbolsWithData = perSymbol.Length
        MonthlyJobs = monthly.Length
        DailyJobs = daily.Length
        TotalBytes = totalBytes
        PerSymbol = perSymbol
    }
