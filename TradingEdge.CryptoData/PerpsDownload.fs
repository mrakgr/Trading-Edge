module TradingEdge.CryptoData.PerpsDownload

open System
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Http
open System.Threading
open DuckDB.NET.Data
open Sylvan.Data.Csv
open TradingEdge.CryptoData.Manifest

// =============================================================================
// Manifest-driven downloader (monthly + daily archives)
// =============================================================================
//
// Source: https://data.binance.vision/{key}, where {key} comes from the
// pre-built Manifest. The manifest guarantees the file exists, so we never
// 404 and never need to walk pre-listing dates.
//
// On-disk layout: per-day parquet at
//   {outputDir}/{SYMBOL}/{SYMBOL}-trades-{YYYY-MM-DD}.parquet
// The monthly path expands a single concatenated CSV into N per-day parquets
// by rotating the DuckDB appender on UTC day boundaries.
//
// Schema: price f64, quantity f64, timestamp_us i64, sign f64
//   - timestamp_us = Binance ms × 1000 (matches the spot loader convention).
//   - sign = +1.0 if is_buyer_maker=False (buyer aggressed),
//            -1.0 if is_buyer_maker=True  (seller aggressed).
//
// Atomicity: each parquet writes to {final}.tmp then File.Move(overwrite=true).
// Power loss leaves .tmp orphans (swept on next run) and never produces a
// half-final file. For a monthly that crosses a power loss, the days that
// already promoted are kept; the days still in flight rebuild from scratch
// when the next run downloads the same monthly archive.
//
// Streaming: Content is streamed via ReadAsStreamAsync so we never hold a
// 1.5 GB monthly ZIP in memory. The CSV reader feeds rows directly into
// the appender as they're decoded.

let private baseUrl = "https://data.binance.vision/"
let private maxRetries = 5

type JobResult =
    | DailyDownloaded of symbol: string * date: DateTime * tradeCount: int * fileSize: int64
    | MonthlyDownloaded of symbol: string * year: int * month: int * tradeCount: int * daysWritten: int * totalBytes: int64
    | Skipped of key: string
    | Failed of key: string * error: string

let private dailyOutputPath (outputDir: string) (symbol: string) (date: DateTime) : string =
    let dir = Path.Combine(outputDir, symbol)
    Directory.CreateDirectory dir |> ignore
    Path.Combine(dir, sprintf "%s-trades-%s.parquet" symbol (date.ToString("yyyy-MM-dd")))

let private archiveUrl (key: string) : string = baseUrl + key

let private daysInMonth (year: int) (month: int) : int = DateTime.DaysInMonth(year, month)

// -----------------------------------------------------------------------------
// HTTP fetch (streaming)
// -----------------------------------------------------------------------------

/// Execute the GET with retries; returns a HttpResponseMessage that the caller
/// must dispose. The response body is NOT buffered — callers must consume it
/// via ReadAsStreamAsync. 404 propagates as Error so the manifest mismatch
/// case (file deleted between listing and fetch) surfaces explicitly.
let private fetchWithRetry
    (http: HttpClient)
    (url: string)
    (ct: CancellationToken)
    : Async<Result<HttpResponseMessage, string>> =
    let rec attempt n delayMs =
        async {
            try
                let! resp =
                    http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    |> Async.AwaitTask
                if resp.StatusCode = HttpStatusCode.NotFound then
                    resp.Dispose()
                    return Error (sprintf "404 %s" url)
                elif resp.StatusCode = HttpStatusCode.TooManyRequests
                     || int resp.StatusCode >= 500 then
                    resp.Dispose()
                    if n >= maxRetries then
                        return Error (sprintf "HTTP %d after %d retries" (int resp.StatusCode) n)
                    else
                        let jitter = Random.Shared.Next(50, 250)
                        do! Async.Sleep(delayMs + jitter)
                        return! attempt (n + 1) (delayMs * 2)
                else
                    resp.EnsureSuccessStatusCode() |> ignore
                    return Ok resp
            with
            | :? HttpRequestException when n < maxRetries ->
                do! Async.Sleep(delayMs + Random.Shared.Next(50, 250))
                return! attempt (n + 1) (delayMs * 2)
            | ex -> return Error ex.Message
        }
    attempt 0 500

// -----------------------------------------------------------------------------
// CSV → parquet (single-day path: one DuckDB appender, one COPY)
// -----------------------------------------------------------------------------

/// Drain a CSV reader into a single-day parquet. Used by the daily path.
let private writeSingleDayParquet
    (sr: StreamReader)
    (outputPath: string)
    : int =
    let tmpPath = outputPath + ".tmp"
    if File.Exists tmpPath then File.Delete tmpPath
    let normalized = tmpPath.Replace('\\', '/').Replace("'", "''")
    let mutable rowCount = 0

    use connection = new DuckDBConnection("Data Source=:memory:")
    connection.Open()
    use createCmd = connection.CreateCommand()
    createCmd.CommandText <-
        "CREATE TABLE trades (
            price DOUBLE,
            quantity DOUBLE,
            timestamp_us BIGINT,
            sign DOUBLE
         )"
    createCmd.ExecuteNonQuery() |> ignore

    use appender = connection.CreateAppender("trades")
    let opts = CsvDataReaderOptions(HasHeaders = true)
    use reader = CsvDataReader.Create(sr, opts)
    while reader.Read() do
        let price = reader.GetDouble 1
        let qty = reader.GetDouble 2
        let timeMs = reader.GetInt64 4
        let bm = reader.GetFieldSpan 5
        let isBuyerMaker = bm.Length > 0 && (bm.[0] = 't' || bm.[0] = 'T')
        let sign = if isBuyerMaker then -1.0 else 1.0
        let row = appender.CreateRow()
        row.AppendValue(Nullable price) |> ignore
        row.AppendValue(Nullable qty) |> ignore
        row.AppendValue(Nullable (timeMs * 1000L)) |> ignore
        row.AppendValue(Nullable sign) |> ignore
        row.EndRow()
        rowCount <- rowCount + 1
    appender.Close()

    use copyCmd = connection.CreateCommand()
    copyCmd.CommandText <-
        sprintf "COPY trades TO '%s' (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3)" normalized
    copyCmd.ExecuteNonQuery() |> ignore
    File.Move(tmpPath, outputPath, overwrite = true)
    rowCount

// -----------------------------------------------------------------------------
// CSV → parquet (monthly path: rotate appender on UTC day boundary)
// -----------------------------------------------------------------------------

/// Resources for a single per-day parquet under construction.
type private DayWriter = {
    OutputPath: string
    TmpPath: string
    Connection: DuckDBConnection
    mutable Appender: DuckDBAppender
    mutable RowCount: int
}

/// Open a fresh DuckDB connection + appender for one day's parquet. Each day
/// gets its own connection — DuckDB doesn't support multiple appenders on the
/// same table efficiently, and one-connection-per-day means we can close +
/// COPY one day's data to disk without disturbing whatever a sibling day is
/// doing. With the row-time-sorted invariant, only one day is ever active.
let private openDayWriter (outputPath: string) : DayWriter =
    let tmpPath = outputPath + ".tmp"
    if File.Exists tmpPath then File.Delete tmpPath
    let conn = new DuckDBConnection("Data Source=:memory:")
    conn.Open()
    use createCmd = conn.CreateCommand()
    createCmd.CommandText <-
        "CREATE TABLE trades (
            price DOUBLE,
            quantity DOUBLE,
            timestamp_us BIGINT,
            sign DOUBLE
         )"
    createCmd.ExecuteNonQuery() |> ignore
    let appender = conn.CreateAppender("trades")
    {
        OutputPath = outputPath
        TmpPath = tmpPath
        Connection = conn
        Appender = appender
        RowCount = 0
    }

/// Flush, COPY to parquet, atomic-rename. Returns the row count for telemetry.
let private finalizeDayWriter (w: DayWriter) : int =
    w.Appender.Close()
    w.Appender.Dispose()
    let normalized = w.TmpPath.Replace('\\', '/').Replace("'", "''")
    use copyCmd = w.Connection.CreateCommand()
    copyCmd.CommandText <-
        sprintf "COPY trades TO '%s' (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3)" normalized
    copyCmd.ExecuteNonQuery() |> ignore
    w.Connection.Dispose()
    File.Move(w.TmpPath, w.OutputPath, overwrite = true)
    w.RowCount

/// Best-effort cleanup if a writer is abandoned mid-stream (exception path).
let private discardDayWriter (w: DayWriter) : unit =
    try w.Appender.Dispose() with _ -> ()
    try w.Connection.Dispose() with _ -> ()
    try if File.Exists w.TmpPath then File.Delete w.TmpPath with _ -> ()

/// Convert ms timestamp -> UTC date (yyyy-MM-dd). Branchless math beats
/// allocating a DateTimeOffset per row.
let private msToUtcDate (timeMs: int64) : DateTime =
    DateTimeOffset.FromUnixTimeMilliseconds(timeMs).UtcDateTime.Date

/// Stream a monthly CSV into per-day parquets. Returns (totalRows, daysWritten).
/// `dailyOutputPathOf` produces the on-disk parquet path for a given UTC date.
/// Days that already have parquets on disk are skipped — their rows are read
/// past without being appended (a power-loss-then-resume scenario where the
/// monthly archive contains some already-promoted days).
let private writeMonthlyParquets
    (sr: StreamReader)
    (dailyOutputPathOf: DateTime -> string)
    : int * int =
    let opts = CsvDataReaderOptions(HasHeaders = true)
    use reader = CsvDataReader.Create(sr, opts)

    let mutable totalRows = 0
    let mutable daysWritten = 0
    let mutable currentWriter : DayWriter option = None
    let mutable currentDate = DateTime.MinValue
    // For days already on disk we set a "skip" marker so the rotation cost
    // still happens once per day boundary but we don't construct a writer.
    let mutable currentSkipDate = DateTime.MinValue

    try
        while reader.Read() do
            let timeMs = reader.GetInt64 4
            let date = msToUtcDate timeMs
            if date <> currentDate then
                // Day boundary. Flush the previous writer (if any) and decide
                // whether to open a new one.
                match currentWriter with
                | Some w ->
                    let n = finalizeDayWriter w
                    totalRows <- totalRows + n
                    daysWritten <- daysWritten + 1
                    currentWriter <- None
                | None -> ()
                currentDate <- date
                let outPath = dailyOutputPathOf date
                if File.Exists outPath then
                    // Already produced for this date in a prior run; skip the
                    // rows but keep the date as the active "skip" date so we
                    // don't reopen.
                    currentSkipDate <- date
                else
                    currentSkipDate <- DateTime.MinValue
                    currentWriter <- Some (openDayWriter outPath)
            // Append the row to the active writer if there is one.
            match currentWriter with
            | Some w when currentSkipDate = DateTime.MinValue ->
                let price = reader.GetDouble 1
                let qty = reader.GetDouble 2
                let bm = reader.GetFieldSpan 5
                let isBuyerMaker = bm.Length > 0 && (bm.[0] = 't' || bm.[0] = 'T')
                let sign = if isBuyerMaker then -1.0 else 1.0
                let row = w.Appender.CreateRow()
                row.AppendValue(Nullable price) |> ignore
                row.AppendValue(Nullable qty) |> ignore
                row.AppendValue(Nullable (timeMs * 1000L)) |> ignore
                row.AppendValue(Nullable sign) |> ignore
                row.EndRow()
                w.RowCount <- w.RowCount + 1
            | _ -> ()
        // Flush the final day.
        match currentWriter with
        | Some w ->
            let n = finalizeDayWriter w
            totalRows <- totalRows + n
            daysWritten <- daysWritten + 1
        | None -> ()
        totalRows, daysWritten
    with ex ->
        // Abandon the in-flight writer; previously promoted days survive.
        match currentWriter with
        | Some w -> discardDayWriter w
        | None -> ()
        reraise ()

// -----------------------------------------------------------------------------
// Job-level dispatch
// -----------------------------------------------------------------------------

/// True when a monthly's per-day parquets are all already on disk — short-
/// circuit the download entirely.
let private monthlyAllSkipped (outputDir: string) (m: MonthlyEntry) : bool =
    let n = daysInMonth m.Year m.Month
    let mutable all = true
    let mutable d = 1
    while all && d <= n do
        let path = dailyOutputPath outputDir m.Symbol (DateTime(m.Year, m.Month, d))
        if not (File.Exists path) then all <- false
        d <- d + 1
    all

/// Stream the response body to a temp file on disk, then invoke `f` with the
/// path. ZipArchive(stream, Read) requires a seekable stream — handing it
/// HttpClient's non-seekable response stream forces an internal buffer-into-
/// MemoryStream which throws "Stream was too long" past 2 GB. Big monthly
/// archives (ETH, DOGE in volatile months) easily blow that cap when their
/// internal CSV decompresses to 10+ GB. Disk-backed temp avoids the cap and
/// lets us reuse the ZIP in case of a retry. Temp lives next to the eventual
/// parquet so it's on the same volume as the final output.
let private downloadToFile
    (response: HttpResponseMessage)
    (tempZipPath: string)
    (ct: CancellationToken)
    : Async<Result<unit, string>> =
    async {
        try
            if File.Exists tempZipPath then File.Delete tempZipPath
            Directory.CreateDirectory(Path.GetDirectoryName tempZipPath) |> ignore
            use! body = response.Content.ReadAsStreamAsync(ct) |> Async.AwaitTask
            use fs = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 <<< 20)
            do! body.CopyToAsync(fs, ct) |> Async.AwaitTask
            return Ok ()
        with ex ->
            try File.Delete tempZipPath with _ -> ()
            return Error (sprintf "download: %s" ex.Message)
    }

let private withDownloadedZip
    (http: HttpClient)
    (url: string)
    (tempZipPath: string)
    (ct: CancellationToken)
    (f: string -> Async<'a>)
    : Async<Result<'a, string>> =
    async {
        let! resp = fetchWithRetry http url ct
        match resp with
        | Error msg -> return Error msg
        | Ok response ->
            use response = response
            let! dl = downloadToFile response tempZipPath ct
            match dl with
            | Error msg -> return Error msg
            | Ok () ->
                // The ZIP is the throwaway artifact; the parquet is durable.
                // finally{} guarantees cleanup even on f's exception path.
                try
                    let! result = f tempZipPath
                    return Ok result
                finally
                    try File.Delete tempZipPath with _ -> ()
    }

let runJob
    (http: HttpClient)
    (outputDir: string)
    (job: Job)
    (ct: CancellationToken)
    : Async<JobResult> =
    async {
        match job with
        | DailyJob d ->
            let outPath = dailyOutputPath outputDir d.Symbol d.Date
            if File.Exists outPath then return Skipped job.Key
            else
                let url = archiveUrl job.Key
                let tempZip = outPath + ".zip.tmp"
                let! result =
                    withDownloadedZip http url tempZip ct (fun zipPath ->
                        async {
                            try
                                use fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 <<< 20)
                                use archive = new ZipArchive(fs, ZipArchiveMode.Read)
                                let entry =
                                    archive.Entries
                                    |> Seq.tryFind (fun e -> e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                                match entry with
                                | None -> return Error "ZIP contained no .csv entry"
                                | Some entry ->
                                    use stream = entry.Open()
                                    use sr = new StreamReader(stream)
                                    let n = writeSingleDayParquet sr outPath
                                    let fi = FileInfo outPath
                                    return Ok (n, fi.Length)
                            with ex ->
                                try File.Delete (outPath + ".tmp") with _ -> ()
                                return Error (sprintf "convert: %s" ex.Message)
                        })
                match result with
                | Error msg -> return Failed(job.Key, msg)
                | Ok (Error inner) -> return Failed(job.Key, inner)
                | Ok (Ok (n, sz)) -> return DailyDownloaded(d.Symbol, d.Date, n, sz)

        | MonthlyJob m ->
            if monthlyAllSkipped outputDir m then return Skipped job.Key
            else
                let url = archiveUrl job.Key
                // Park the temp under the symbol directory; on the same volume
                // as the final parquets so we don't cross filesystems.
                let symbolDir = Path.Combine(outputDir, m.Symbol)
                let tempZip = Path.Combine(symbolDir, sprintf "%s-trades-%04d-%02d.zip.tmp" m.Symbol m.Year m.Month)
                let! result =
                    withDownloadedZip http url tempZip ct (fun zipPath ->
                        async {
                            try
                                use fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 <<< 20)
                                use archive = new ZipArchive(fs, ZipArchiveMode.Read)
                                let entry =
                                    archive.Entries
                                    |> Seq.tryFind (fun e -> e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                                match entry with
                                | None -> return Error "ZIP contained no .csv entry"
                                | Some entry ->
                                    use stream = entry.Open()
                                    use sr = new StreamReader(stream)
                                    let pathOf (date: DateTime) = dailyOutputPath outputDir m.Symbol date
                                    let totalRows, daysWritten = writeMonthlyParquets sr pathOf
                                    return Ok (totalRows, daysWritten)
                            with ex ->
                                return Error (sprintf "convert: %s" ex.Message)
                        })
                match result with
                | Error msg -> return Failed(job.Key, msg)
                | Ok (Error inner) -> return Failed(job.Key, inner)
                | Ok (Ok (totalRows, daysWritten)) ->
                    return MonthlyDownloaded(m.Symbol, m.Year, m.Month, totalRows, daysWritten, m.SizeBytes)
    }

// -----------------------------------------------------------------------------
// Batch driver
// -----------------------------------------------------------------------------

type ProgressCallback = int -> int -> JobResult -> unit

/// Sweep orphaned .tmp files left by interrupted runs (e.g. power loss,
/// SIGKILL). Safe to call before any download batch.
let sweepOrphanTemps (outputDir: string) : int =
    if not (Directory.Exists outputDir) then 0
    else
        let mutable n = 0
        for tmp in Directory.EnumerateFiles(outputDir, "*.parquet.tmp", SearchOption.AllDirectories) do
            try
                File.Delete tmp
                n <- n + 1
            with _ -> ()
        n

let downloadBatch
    (http: HttpClient)
    (outputDir: string)
    (jobs: Job[])
    (maxParallelism: int)
    (progress: ProgressCallback)
    (ct: CancellationToken)
    : Async<JobResult[]> =
    async {
        let nSwept = sweepOrphanTemps outputDir
        if nSwept > 0 then
            printfn "Swept %d orphaned .tmp file(s) from prior interrupted run." nSwept
        let total = jobs.Length
        let completed = ref 0
        use semaphore = new SemaphoreSlim(maxParallelism, maxParallelism)
        let runOne (job: Job) =
            async {
                do! semaphore.WaitAsync(ct) |> Async.AwaitTask
                try
                    let! r = runJob http outputDir job ct
                    let c = Interlocked.Increment completed
                    progress c total r
                    return r
                finally
                    semaphore.Release() |> ignore
            }
        let! results = jobs |> Array.map runOne |> Async.Parallel
        return results
    }

let consoleProgress (completed: int) (total: int) (result: JobResult) : unit =
    let summary =
        match result with
        | DailyDownloaded(s, d, n, fileSize) ->
            sprintf "OK   day  %s %s  %d trades, %.1f MB"
                s (d.ToString("yyyy-MM-dd")) n (float fileSize / 1.0e6)
        | MonthlyDownloaded(s, y, mo, n, days, totalBytes) ->
            sprintf "OK   mo   %s %04d-%02d  %d trades / %d days, %.1f MB src"
                s y mo n days (float totalBytes / 1.0e6)
        | Skipped key ->
            sprintf "skip %s" key
        | Failed(key, err) ->
            sprintf "FAIL %s: %s" key err
    printfn "[%d/%d] %s" completed total summary
