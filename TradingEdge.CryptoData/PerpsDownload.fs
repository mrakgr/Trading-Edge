module TradingEdge.CryptoData.PerpsDownload

open System
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open DuckDB.NET.Data
open TradingEdge.CryptoData.Manifest

// =============================================================================
// Manifest-driven downloader (monthly + daily archives)
// =============================================================================
//
// Architecture (2026-05-12 rewrite, two-stage pipeline):
//
//   manifest  -->  [N download workers]  -->  Channel<DownloadedZip>  -->  [M convert workers]  -->  Channel<JobResult>  -->  reporter
//                                               (unbounded)
//
// Why this shape:
//   - The single-stage Async.Parallel implementation interleaved network and
//     DuckDB conversion work on the same thread pool. With moderate convert
//     parallelism this starved I/O completion threads (downloads went idle
//     while conversions ran), and at higher convert parallelism DuckDB's
//     in-memory tables OOMed silently through the P/Invoke boundary.
//   - Splitting the stages decouples the tempos. Downloads sustain at the
//     network rate regardless of how slow the converter is, because the
//     channel between them is unbounded. The converter pool stays small
//     (default 1) so DuckDB's `read_csv` streaming + COPY can use its own
//     internal threads without contention.
//   - All async work uses `task { }` (CLAUDE.md requirement) — no `async { }`.
//
// On-disk layout: per-day parquet at
//   {outputDir}/{SYMBOL}/{SYMBOL}-trades-{YYYY-MM-DD}.parquet
// The monthly path expands a single concatenated CSV into N per-day parquets
// via DuckDB COPY (... PARTITION_BY (date_str)) into a temp dir, then renames
// each partition file into the production flat layout.
//
// Schema: price f64, quantity f64, timestamp_us i64, sign f64
//   - timestamp_us = Binance ms × 1000.
//   - sign = +1.0 if is_buyer_maker=False (buyer aggressed),
//            -1.0 if is_buyer_maker=True  (seller aggressed).
//
// Atomicity: each parquet writes to {final}.tmp then File.Move(overwrite=true).
// .zip.tmp and .csv (extracted) and .part-YYYY-MM/ directories are all
// cleaned up in finally regardless of conversion outcome. sweepOrphanTemps
// removes leftover .zip.tmp / .parquet.tmp at startup.

let private baseUrl = "https://data.binance.vision/"
let private maxRetries = 5

// Per-chunk idle deadline for the HTTP body copy. HttpClient.Timeout is
// ignored once HttpCompletionOption.ResponseHeadersRead returns control, so
// a server that stops sending mid-stream would otherwise hang the read
// forever (observed 5-hour silent stall on PARTIUSDT 2026-04). The deadline
// resets on every successful chunk: while bytes flow the timer keeps
// getting refreshed; only true silence for `idleDeadline` trips it.
let private idleDeadline = TimeSpan.FromSeconds 15.0
let private copyBufferSize = 1 <<< 20  // 1 MiB

type JobResult =
    | DailyDownloaded of symbol: string * date: DateTime * tradeCount: int * fileSize: int64
    | MonthlyDownloaded of symbol: string * year: int * month: int * tradeCount: int * daysWritten: int * totalBytes: int64
    | Skipped of key: string
    | Failed of key: string * error: string

// -----------------------------------------------------------------------------
// Paths and helpers
// -----------------------------------------------------------------------------

let private dailyOutputPath (outputDir: string) (symbol: string) (date: DateTime) : string =
    let dir = Path.Combine(outputDir, symbol)
    Directory.CreateDirectory dir |> ignore
    Path.Combine(dir, sprintf "%s-trades-%s.parquet" symbol (date.ToString("yyyy-MM-dd")))

let private archiveUrl (key: string) : string = baseUrl + key

let private daysInMonth (year: int) (month: int) : int = DateTime.DaysInMonth(year, month)

let private sqlEscape (s: string) : string =
    s.Replace('\\', '/').Replace("'", "''")

let private monthlyCompleteSentinelPath (outputDir: string) (m: MonthlyEntry) : string =
    let dir = Path.Combine(outputDir, m.Symbol)
    Path.Combine(dir, sprintf ".complete-%04d-%02d" m.Year m.Month)

let private writeMonthlySentinel (outputDir: string) (m: MonthlyEntry) : unit =
    let path = monthlyCompleteSentinelPath outputDir m
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    if not (File.Exists path) then
        File.WriteAllBytes(path, [||])

/// True when this monthly should be short-circuited. Two sources of truth:
///   1. Sentinel file exists — definitive, set after a clean finalization.
///   2. All `[YYYY-MM-01, EoM]` per-day parquets present — heuristic legacy
///      check, kept as a fallback for monthlies completed before sentinels
///      existed. When this fallback fires, also write the sentinel.
let private monthlyAllSkipped (outputDir: string) (m: MonthlyEntry) : bool =
    let sentinel = monthlyCompleteSentinelPath outputDir m
    if File.Exists sentinel then true
    else
        let n = daysInMonth m.Year m.Month
        let mutable all = true
        let mutable d = 1
        while all && d <= n do
            let path = dailyOutputPath outputDir m.Symbol (DateTime(m.Year, m.Month, d))
            if not (File.Exists path) then all <- false
            d <- d + 1
        if all then writeMonthlySentinel outputDir m
        all

// -----------------------------------------------------------------------------
// CSV extraction (zip -> disk)
// -----------------------------------------------------------------------------
//
// DuckDB doesn't natively read inside .zip archives (verified DuckDB 1.4.4 —
// it auto-detects gzip/zstd/bz2/snappy but treats .zip as raw bytes), and
// DuckDB.NET can't be handed a managed Stream from `entry.Open()`. So we
// extract to a sibling temp .csv file, then point read_csv at it. The temp
// CSV is ~3-5× the zip size; deleted by the caller in finally.

let private extractCsvToFile (zipPath: string) (csvPath: string) : unit =
    if File.Exists csvPath then File.Delete csvPath
    use fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 <<< 20)
    use archive = new ZipArchive(fs, ZipArchiveMode.Read)
    let entry =
        archive.Entries
        |> Seq.tryFind (fun e -> e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
    match entry with
    | None -> failwith "ZIP contained no .csv entry"
    | Some entry -> entry.ExtractToFile csvPath

/// Binance changed their trade-CSV format around mid-2023: newer archives
/// have a header row (`id,price,qty,quote_qty,time,is_buyer_maker`); older
/// ones go straight to data. We detect by peeking the first byte — a digit
/// means data, a letter means header — and let DuckDB's read_csv handle the
/// rest. Without this, the older archives fail with "Referenced column
/// 'price' not found", DuckDB having sniffed the first data row as a header.
let private csvHasHeader (csvPath: string) : bool =
    use fs = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096)
    let b = fs.ReadByte()
    // Digit = first row is data ('922381201,...' style). Anything else
    // (letter — Binance's column names all start with letters) = header.
    not (b >= int '0' && b <= int '9')

// -----------------------------------------------------------------------------
// CSV -> parquet (daily path: streaming COPY via DuckDB read_csv)
// -----------------------------------------------------------------------------

/// read_csv() argument that handles both header-present and header-absent
/// CSV layouts. Header-absent CSVs are typed by hand; header-present ones
/// are auto-named (the column names match by coincidence — same field
/// order — so the downstream SELECT works on both).
let private readCsvClause (csvPath: string) : string =
    let csvNorm = sqlEscape csvPath
    if csvHasHeader csvPath then
        sprintf "read_csv('%s', header=true)" csvNorm
    else
        // Older Binance trade CSVs have no header. Schema (positional):
        //   id BIGINT, price DOUBLE, qty DOUBLE, quote_qty DOUBLE,
        //   time BIGINT (ms), is_buyer_maker BOOLEAN
        sprintf "read_csv('%s', header=false, columns={'id': 'BIGINT', 'price': 'DOUBLE', 'qty': 'DOUBLE', 'quote_qty': 'DOUBLE', 'time': 'BIGINT', 'is_buyer_maker': 'BOOLEAN'})"
            csvNorm

let private writeSingleDayParquet
    (csvPath: string)
    (outputPath: string)
    : int =
    let tmpPath = outputPath + ".tmp"
    if File.Exists tmpPath then File.Delete tmpPath
    let outNorm = sqlEscape tmpPath
    let readClause = readCsvClause csvPath

    use connection = new DuckDBConnection("Data Source=:memory:")
    connection.Open()

    use copyCmd = connection.CreateCommand()
    copyCmd.CommandText <-
        sprintf "
            COPY (
                SELECT
                    price,
                    qty AS quantity,
                    time * 1000 AS timestamp_us,
                    CASE WHEN is_buyer_maker THEN -1.0 ELSE 1.0 END AS sign
                FROM %s
            ) TO '%s' (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3)"
            readClause outNorm
    copyCmd.ExecuteNonQuery() |> ignore

    use countCmd = connection.CreateCommand()
    countCmd.CommandText <- sprintf "SELECT COUNT(*) FROM read_parquet('%s')" outNorm
    let n =
        use rdr = countCmd.ExecuteReader()
        if rdr.Read() then int (rdr.GetInt64 0) else 0

    File.Move(tmpPath, outputPath, overwrite = true)
    n

// -----------------------------------------------------------------------------
// CSV -> parquet (monthly path: PARTITION_BY + rename)
// -----------------------------------------------------------------------------

let private writeMonthlyParquets
    (csvPath: string)
    (dailyOutputPathOf: DateTime -> string)
    (partitionWorkDir: string)
    : int * int =
    if Directory.Exists partitionWorkDir then
        Directory.Delete(partitionWorkDir, recursive = true)
    Directory.CreateDirectory partitionWorkDir |> ignore
    let partNorm = sqlEscape partitionWorkDir
    let readClause = readCsvClause csvPath

    use connection = new DuckDBConnection("Data Source=:memory:")
    connection.Open()

    use copyCmd = connection.CreateCommand()
    copyCmd.CommandText <-
        sprintf "
            COPY (
                SELECT
                    price,
                    qty AS quantity,
                    time * 1000 AS timestamp_us,
                    CASE WHEN is_buyer_maker THEN -1.0 ELSE 1.0 END AS sign,
                    strftime(make_timestamp(time * 1000), '%%Y-%%m-%%d') AS date_str
                FROM %s
            )
            TO '%s'
            (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3,
             PARTITION_BY (date_str), OVERWRITE_OR_IGNORE)"
            readClause partNorm
    copyCmd.ExecuteNonQuery() |> ignore

    let mutable totalRows = 0
    let mutable daysWritten = 0
    for partDir in Directory.EnumerateDirectories partitionWorkDir do
        let name = Path.GetFileName partDir
        let dateStr =
            if name.StartsWith "date_str=" then name.Substring 9 else name
        let date =
            DateTime.ParseExact(dateStr, "yyyy-MM-dd",
                Globalization.CultureInfo.InvariantCulture)
        let dstPath = dailyOutputPathOf date
        if File.Exists dstPath then
            // already promoted in a prior run
            ()
        else
            let files = Directory.GetFiles(partDir, "*.parquet")
            if files.Length = 1 then
                let src = files.[0]
                Directory.CreateDirectory(Path.GetDirectoryName dstPath) |> ignore
                use cnt = connection.CreateCommand()
                cnt.CommandText <-
                    sprintf "SELECT COUNT(*) FROM read_parquet('%s')" (sqlEscape src)
                use rdr = cnt.ExecuteReader()
                if rdr.Read() then
                    totalRows <- totalRows + int (rdr.GetInt64 0)
                File.Move(src, dstPath, overwrite = true)
                daysWritten <- daysWritten + 1
            elif files.Length > 1 then
                // Defensive: concatenate the partition's chunks into one file.
                let dstTmp = dstPath + ".tmp"
                let srcGlob = sqlEscape (Path.Combine(partDir, "*.parquet"))
                use copy2 = connection.CreateCommand()
                copy2.CommandText <-
                    sprintf "
                        COPY (SELECT * FROM read_parquet('%s')) TO '%s'
                        (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3)"
                        srcGlob (sqlEscape dstTmp)
                copy2.ExecuteNonQuery() |> ignore
                use cnt = connection.CreateCommand()
                cnt.CommandText <-
                    sprintf "SELECT COUNT(*) FROM read_parquet('%s')" (sqlEscape dstTmp)
                use rdr = cnt.ExecuteReader()
                if rdr.Read() then
                    totalRows <- totalRows + int (rdr.GetInt64 0)
                File.Move(dstTmp, dstPath, overwrite = true)
                daysWritten <- daysWritten + 1
    try Directory.Delete(partitionWorkDir, recursive = true) with _ -> ()
    totalRows, daysWritten

// -----------------------------------------------------------------------------
// HTTP fetch (streaming to disk, task-based, retries, idle-deadline)
// -----------------------------------------------------------------------------

/// One HTTP GET with retries on TooManyRequests / 5xx / HttpRequestException.
/// Returns a HttpResponseMessage the caller must dispose, with
/// HttpCompletionOption.ResponseHeadersRead so the body is not yet buffered.
let private fetchWithRetry
    (http: HttpClient)
    (url: string)
    (ct: CancellationToken)
    : Task<Result<HttpResponseMessage, string>> =
    let rec attempt n delayMs : Task<Result<HttpResponseMessage, string>> =
        task {
            try
                let! resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
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
                        do! Task.Delay(delayMs + jitter, ct)
                        return! attempt (n + 1) (delayMs * 2)
                else
                    resp.EnsureSuccessStatusCode() |> ignore
                    return Ok resp
            with
            | :? HttpRequestException when n < maxRetries ->
                do! Task.Delay(delayMs + Random.Shared.Next(50, 250), ct)
                return! attempt (n + 1) (delayMs * 2)
            | ex -> return Error ex.Message
        }
    attempt 0 500

/// Stream a HTTP response body to disk with a per-chunk idle deadline.
let private downloadToFile
    (response: HttpResponseMessage)
    (tempZipPath: string)
    (ct: CancellationToken)
    : Task<Result<unit, string>> =
    task {
        try
            if File.Exists tempZipPath then File.Delete tempZipPath
            Directory.CreateDirectory(Path.GetDirectoryName tempZipPath) |> ignore
            use! body = response.Content.ReadAsStreamAsync(ct)
            use fs = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, copyBufferSize)
            let buf = Array.zeroCreate<byte> copyBufferSize
            use cts = CancellationTokenSource.CreateLinkedTokenSource ct
            let mutable bytesThisChunk = 1
            let mutable stalled = false
            try
                while bytesThisChunk > 0 && not stalled do
                    cts.CancelAfter idleDeadline
                    try
                        let! n = body.ReadAsync(buf, 0, buf.Length, cts.Token)
                        bytesThisChunk <- n
                        if n > 0 then
                            do! fs.WriteAsync(buf, 0, n, ct)
                    with
                    | :? OperationCanceledException when not ct.IsCancellationRequested ->
                        stalled <- true
                if stalled then
                    try File.Delete tempZipPath with _ -> ()
                    return Error (sprintf "download stalled: no bytes received for %.0fs" idleDeadline.TotalSeconds)
                else
                    return Ok ()
            with ex ->
                try File.Delete tempZipPath with _ -> ()
                return Error (sprintf "download: %s" ex.Message)
        with ex ->
            try File.Delete tempZipPath with _ -> ()
            return Error (sprintf "download: %s" ex.Message)
    }

/// Full fetch+body-copy with retries around stalls. Returns Ok when the
/// .zip.tmp is fully on disk; Error otherwise. Caller is responsible for
/// deleting the .zip.tmp on the convert side.
let private downloadZipToDisk
    (http: HttpClient)
    (url: string)
    (tempZipPath: string)
    (ct: CancellationToken)
    : Task<Result<unit, string>> =
    let rec attempt n delayMs : Task<Result<unit, string>> =
        task {
            let! resp = fetchWithRetry http url ct
            match resp with
            | Error msg -> return Error msg
            | Ok response ->
                use response = response
                let! dl = downloadToFile response tempZipPath ct
                match dl with
                | Error msg ->
                    if n >= maxRetries then return Error msg
                    else
                        let jitter = Random.Shared.Next(50, 250)
                        do! Task.Delay(delayMs + jitter, ct)
                        return! attempt (n + 1) (delayMs * 2)
                | Ok () -> return Ok ()
        }
    attempt 0 1000

// -----------------------------------------------------------------------------
// Orphan-temp sweep
// -----------------------------------------------------------------------------

let sweepOrphanTemps (outputDir: string) : int =
    if not (Directory.Exists outputDir) then 0
    else
        let mutable n = 0
        let patterns = [| "*.parquet.tmp"; "*.zip.tmp" |]
        for pat in patterns do
          for tmp in Directory.EnumerateFiles(outputDir, pat, SearchOption.AllDirectories) do
            try
                File.Delete tmp
                n <- n + 1
            with _ -> ()
        // Also sweep half-built .part-YYYY-MM/ partition dirs.
        for partDir in Directory.EnumerateDirectories(outputDir, ".part-*", SearchOption.AllDirectories) do
            try
                Directory.Delete(partDir, recursive = true)
                n <- n + 1
            with _ -> ()
        n

// -----------------------------------------------------------------------------
// Pipeline: skip-filter, download stage, convert stage, reporter
// -----------------------------------------------------------------------------

/// Item handed from the download stage to the convert stage.
type private DownloadedZip = {
    Job: Job
    ZipPath: string
}

let private outputPathsForJob (outputDir: string) (job: Job) : string * string =
    match job with
    | DailyJob d ->
        let outPath = dailyOutputPath outputDir d.Symbol d.Date
        let zipTmp = outPath + ".zip.tmp"
        outPath, zipTmp
    | MonthlyJob m ->
        let symbolDir = Path.Combine(outputDir, m.Symbol)
        Directory.CreateDirectory symbolDir |> ignore
        let zipTmp = Path.Combine(symbolDir,
                        sprintf "%s-trades-%04d-%02d.zip.tmp" m.Symbol m.Year m.Month)
        // outPath isn't well-defined for monthlies (N per-day outputs), so
        // we return zipTmp twice — only the second is used for monthlies.
        zipTmp, zipTmp

/// Per-job decision: should we even attempt this job? Returns Some(Skipped)
/// if the output is already on disk; None otherwise (caller will download).
let private trySkip (outputDir: string) (job: Job) : JobResult option =
    match job with
    | DailyJob d ->
        let outPath = dailyOutputPath outputDir d.Symbol d.Date
        if File.Exists outPath then Some (Skipped job.Key) else None
    | MonthlyJob m ->
        if monthlyAllSkipped outputDir m then Some (Skipped job.Key) else None

/// One convert-stage step. Runs synchronously on the worker's task thread.
let private convertOne (outputDir: string) (item: DownloadedZip) : JobResult =
    let zipPath = item.ZipPath
    let csvTemp = zipPath + ".csv"
    try
        try
            match item.Job with
            | DailyJob d ->
                let outPath = dailyOutputPath outputDir d.Symbol d.Date
                try
                    extractCsvToFile zipPath csvTemp
                    let n = writeSingleDayParquet csvTemp outPath
                    let fi = FileInfo outPath
                    DailyDownloaded(d.Symbol, d.Date, n, fi.Length)
                with ex ->
                    try File.Delete (outPath + ".tmp") with _ -> ()
                    Failed(item.Job.Key, sprintf "convert: %s" ex.Message)
            | MonthlyJob m ->
                let symbolDir = Path.Combine(outputDir, m.Symbol)
                let partWork =
                    Path.Combine(symbolDir,
                        sprintf ".part-%04d-%02d" m.Year m.Month)
                try
                    try
                        extractCsvToFile zipPath csvTemp
                        let pathOf (date: DateTime) = dailyOutputPath outputDir m.Symbol date
                        let totalRows, daysWritten =
                            writeMonthlyParquets csvTemp pathOf partWork
                        writeMonthlySentinel outputDir m
                        MonthlyDownloaded(m.Symbol, m.Year, m.Month, totalRows, daysWritten, m.SizeBytes)
                    with ex ->
                        Failed(item.Job.Key, sprintf "convert: %s" ex.Message)
                finally
                    try
                        if Directory.Exists partWork then
                            Directory.Delete(partWork, recursive = true)
                    with _ -> ()
        finally
            try File.Delete csvTemp with _ -> ()
            try File.Delete zipPath with _ -> ()
    with ex ->
        Failed(item.Job.Key, sprintf "convert: %s" ex.Message)

type ProgressCallback = int -> int -> JobResult -> unit

/// Run the two-stage pipeline. Skip-filter runs synchronously in the driver
/// thread (cheap stat() calls, emits Skipped immediately). Download workers
/// pull from `jobsCh`, push to `downloadedCh`. Convert workers pull from
/// `downloadedCh`, push results to `resultsCh`. A reporter task drains
/// resultsCh and calls `progress`. Returns the full result list.
let runPipeline
    (http: HttpClient)
    (outputDir: string)
    (jobs: Job[])
    (downloadParallelism: int)
    (convertParallelism: int)
    (progress: ProgressCallback)
    (ct: CancellationToken)
    : Task<JobResult[]> =
    task {
        let nSwept = sweepOrphanTemps outputDir
        if nSwept > 0 then
            printfn "Swept %d orphaned .tmp/.part file(s) from prior run." nSwept

        let total = jobs.Length

        // Channels are the only inter-stage communication. No semaphores, no
        // locks, no Interlocked: the reporter is the single owner of the
        // results buffer + completed counter, so it can mutate them without
        // synchronization. Every other stage just writes to a channel.
        //
        //   skip stage  ─┐
        //   download     ├──> resultsCh  ─> reporter ─> ResizeArray + progress
        //   convert      ─┤
        //
        //   skip stage  ─> jobsCh  ─> download workers ─> downloadedCh ─> convert workers
        //                                                  (unbounded; downloads never block on slow converters)
        let jobsCh = Channel.CreateUnbounded<Job>()
        let downloadedCh = Channel.CreateUnbounded<DownloadedZip>()
        let resultsCh = Channel.CreateUnbounded<JobResult>()

        // --- Stage 0: skip-filter + producer ---
        // Runs in a background task so the driver can spin up workers
        // immediately and not be blocked by the synchronous stat() pre-pass.
        let skipStage =
            task {
                for job in jobs do
                    match trySkip outputDir job with
                    | Some skipResult ->
                        do! resultsCh.Writer.WriteAsync(skipResult, ct)
                    | None ->
                        do! jobsCh.Writer.WriteAsync(job, ct)
                jobsCh.Writer.Complete()
            } :> Task

        // --- Stage 1: download workers ---
        let downloadWorker () : Task =
            task {
                let reader = jobsCh.Reader
                let writer = downloadedCh.Writer
                let mutable job = Unchecked.defaultof<Job>
                while! reader.WaitToReadAsync(ct) do
                    while reader.TryRead(&job) do
                        let _, zipTmp = outputPathsForJob outputDir job
                        let url = archiveUrl job.Key
                        let! result = downloadZipToDisk http url zipTmp ct
                        match result with
                        | Ok () ->
                            do! writer.WriteAsync({ Job = job; ZipPath = zipTmp }, ct)
                        | Error msg ->
                            do! resultsCh.Writer.WriteAsync(Failed(job.Key, msg), ct)
            } :> Task

        let downloadWorkers =
            [| for _ in 1 .. downloadParallelism -> downloadWorker () |]

        let downloadDone =
            task {
                do! Task.WhenAll downloadWorkers
                downloadedCh.Writer.Complete()
            } :> Task

        // --- Stage 2: convert workers ---
        let convertWorker () : Task =
            task {
                let reader = downloadedCh.Reader
                let mutable item = Unchecked.defaultof<DownloadedZip>
                while! reader.WaitToReadAsync(ct) do
                    while reader.TryRead(&item) do
                        // Synchronous DuckDB work on a Task.Run worker, not
                        // the channel-reader's continuation thread.
                        let! result = Task.Run((fun () -> convertOne outputDir item), ct)
                        do! resultsCh.Writer.WriteAsync(result, ct)
            } :> Task

        let convertWorkers =
            [| for _ in 1 .. convertParallelism -> convertWorker () |]

        // When skip-producer AND all download workers are done, close
        // downloadedCh so convert workers can drain. When all convert workers
        // are done AND the skip stage is done, close resultsCh so the
        // reporter can drain.
        let downloadStageDone =
            task {
                do! skipStage
                do! downloadDone
            } :> Task

        let pipelineDone =
            task {
                do! Task.WhenAll convertWorkers
                do! skipStage           // ensure all Skipped already enqueued
                resultsCh.Writer.Complete()
            } :> Task

        // --- Reporter: single owner of results buffer + counter ---
        // No lock, no Interlocked — only this task touches `allResults`
        // and `completed`. F# channels deliver items in write order per
        // writer; across writers, order is whatever the scheduler picks.
        let reporter =
            task {
                let allResults = ResizeArray<JobResult>(total)
                let mutable completed = 0
                let reader = resultsCh.Reader
                let mutable r = Unchecked.defaultof<JobResult>
                while! reader.WaitToReadAsync(ct) do
                    while reader.TryRead(&r) do
                        allResults.Add r
                        completed <- completed + 1
                        progress completed total r
                return allResults.ToArray()
            }

        do! downloadStageDone
        do! pipelineDone
        let! results = reporter
        return results
    }

let consoleProgress (completed: int) (total: int) (result: JobResult) : unit =
    let summary =
        match result with
        | DailyDownloaded(s, d, n, fileSize) ->
            sprintf "OK   day  %s %s  %d trades, %.1f MB"
                s (d.ToString("yyyy-MM-dd")) n (float fileSize / 1.0e6)
        | MonthlyDownloaded(s, y, mo, n, days, totalBytes) ->
            let suffix =
                if days = 0 then " (all days already on disk; sentinel written)"
                else ""
            sprintf "OK   mo   %s %04d-%02d  %d trades / %d days, %.1f MB src%s"
                s y mo n days (float totalBytes / 1.0e6) suffix
        | Skipped key ->
            sprintf "skip %s" key
        | Failed(key, err) ->
            sprintf "FAIL %s: %s" key err
    printfn "[%d/%d] %s" completed total summary
