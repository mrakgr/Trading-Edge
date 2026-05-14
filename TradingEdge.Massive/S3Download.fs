module TradingEdge.S3Download

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Amazon.S3
open Amazon.S3.Model

// =============================================================================
// Bulk Massive S3 downloader — two-stage channel pipeline
// =============================================================================
//
// Architecture (2026-05-14 rewrite, mirrors TradingEdge.CryptoData.PerpsDownload):
//
//   dates  -->  skip-stage  ──┐
//                             ├──> resultsCh ──> reporter
//   download workers          │
//   convert workers ──────────┘
//
//   dates  -->  jobsCh  -->  [N download workers]  -->  Channel<DownloadedCsvGz>
//                                                      (bounded 8 — SSD backpressure)
//                                                       -->  [M convert workers]  -->  resultsCh
//
// Why this shape:
//   - The HDD is the chokepoint. The old Async.Parallel design landed every
//     download AND every conversion on the same HDD volume, so 4 parallel
//     downloads pegged HDD write throughput at ~100% and saturated the disk
//     queue — making any other I/O (e.g. video editing) impossible. By
//     staging downloads on the SSD (random/burst writes, cheap) and only
//     committing the final parquet to the HDD (one sequential write per
//     file), the HDD is left mostly idle.
//   - Splitting download and convert into separate stages also lets us run
//     download=4 / convert=1 for trades: the converter writes 1-2 GB
//     sequential parquets to the HDD, and one writer in flight keeps the HDD
//     happy without contention with neighbouring writes.
//   - The bounded channel (8 slots) caps the SSD high-water at ~8 × ~2 GB
//     = ~16 GB of staged .csv.gz, well within the 600+ GB SSD headroom.
//
// On-disk staging convention:
//   tempDir/{date}.{kind}.csv.gz.tmp   -- in-flight download
//   tempDir/{date}.{kind}.csv.gz       -- complete download, ready to convert
//   outputDir/{date}.parquet.tmp       -- in-flight conversion (or csv.gz.tmp for daily)
//   outputDir/{date}.parquet           -- final artifact (or .csv.gz for daily)
//
// {kind} disambiguates daily/minute/trades so a single tempDir can host all
// three concurrently without collision.
//
// Atomicity: each parquet writes to {final}.tmp then File.Move(overwrite=true)
// — same-volume move is atomic on POSIX. The SSD csv.gz is deleted only AFTER
// the parquet rename succeeds, so an interrupted run leaves either the
// SSD csv.gz alone (re-converts on restart) or nothing (already done). The
// orphan-sweep at startup cleans .tmp residue from prior crashes.

let private bucketName = "flatfiles"
let private serviceUrl = "https://files.massive.com"
let private maxRetries = 5

/// Walk an exception's inner chain looking for an AmazonS3Exception with a
/// 404 status. Async.AwaitTask wraps faulted-task exceptions inside an
/// AggregateException, so a plain `:? AmazonS3Exception` match misses the
/// 404 we actually care about.
let private isS3NotFound (ex: exn) : bool =
    let rec walk (e: exn) =
        match e with
        | null -> false
        | :? AmazonS3Exception as s3 when s3.StatusCode = HttpStatusCode.NotFound -> true
        | :? AggregateException as agg ->
            agg.InnerExceptions |> Seq.exists walk
        | _ -> walk e.InnerException
    walk ex

/// Create an S3 client configured for Massive
let createS3Client (accessKey: string) (secretKey: string) : AmazonS3Client =
    let config = AmazonS3Config(
        ServiceURL = serviceUrl,
        AuthenticationRegion = "us-east-1",
        ForcePathStyle = true
    )
    new AmazonS3Client(accessKey, secretKey, config)

/// Generate trading days (excluding weekends) between two dates
let getTradingDays (startDate: DateTime) (endDate: DateTime) : DateTime list =
    let rec loop (current: DateTime) acc =
        if current > endDate then
            List.rev acc
        else
            let next = current.AddDays(1.0)
            if current.DayOfWeek = DayOfWeek.Saturday || current.DayOfWeek = DayOfWeek.Sunday then
                loop next acc
            else
                loop next (current :: acc)
    loop startDate []

// -----------------------------------------------------------------------------
// S3 key generators (one per bulk dataset)
// -----------------------------------------------------------------------------

let private getS3KeyDaily (date: DateTime) : string =
    let y = date.Year.ToString()
    let m = date.Month.ToString("00")
    let d = date.ToString("yyyy-MM-dd")
    $"us_stocks_sip/day_aggs_v1/{y}/{m}/{d}.csv.gz"

let private getS3KeyMinute (date: DateTime) : string =
    let y = date.Year.ToString()
    let m = date.Month.ToString("00")
    let d = date.ToString("yyyy-MM-dd")
    $"us_stocks_sip/minute_aggs_v1/{y}/{m}/{d}.csv.gz"

let private getS3KeyTrades (date: DateTime) : string =
    let y = date.Year.ToString()
    let m = date.Month.ToString("00")
    let d = date.ToString("yyyy-MM-dd")
    $"us_stocks_sip/trades_v1/{y}/{m}/{d}.csv.gz"

// -----------------------------------------------------------------------------
// Converters (csv.gz -> parquet)
// -----------------------------------------------------------------------------
//
// Minute aggs: trivial schema (ticker, ohlcv, window_start, transactions).
// We use the DuckDB CLI piped from zcat for consistency with the trades path,
// even though minute files are small enough for the in-process reader. Keeping
// both converters on the same external-process model means the converter
// thread doesn't carry a DuckDB.NET native-allocator footprint.

/// Run `zcat <csv.gz> | duckdb -c <sql>` synchronously. The SQL must read
/// from /dev/stdin and write its parquet to a path the caller chose. Returns
/// on success; throws on non-zero exit of either process.
let private runZcatDuckdb (csvGzPath: string) (sql: string) : unit =
    let zcatPsi = ProcessStartInfo("zcat", sprintf "\"%s\"" csvGzPath)
    zcatPsi.RedirectStandardOutput <- true
    zcatPsi.UseShellExecute <- false
    let duckPsi = ProcessStartInfo("duckdb", sprintf "-c \"%s\"" (sql.Replace("\"", "\\\"")))
    duckPsi.RedirectStandardInput <- true
    duckPsi.RedirectStandardError <- true
    duckPsi.UseShellExecute <- false

    use zcat = Process.Start zcatPsi
    use duck = Process.Start duckPsi

    let pumpTask =
        Task.Run(fun () ->
            try
                zcat.StandardOutput.BaseStream.CopyTo(duck.StandardInput.BaseStream)
            finally
                duck.StandardInput.Close())

    let stderrTask = duck.StandardError.ReadToEndAsync()

    zcat.WaitForExit()
    pumpTask.Wait()
    duck.WaitForExit()
    let err = stderrTask.Result

    if zcat.ExitCode <> 0 then
        failwithf "zcat failed (exit %d) on %s" zcat.ExitCode csvGzPath
    if duck.ExitCode <> 0 then
        failwithf "duckdb failed (exit %d) on %s: %s" duck.ExitCode csvGzPath err

let private convertMinuteCsvGzToParquet (csvGzPath: string) (parquetPath: string) : unit =
    let parquetEscaped = parquetPath.Replace("'", "''")
    let sql =
        sprintf
            "COPY (SELECT * FROM read_csv('/dev/stdin')) TO '%s' (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3)"
            parquetEscaped
    runZcatDuckdb csvGzPath sql

/// CSV schema (from Polygon flat-file docs):
///   ticker, conditions, correction, exchange, id, participant_timestamp,
///   price, sequence_number, sip_timestamp, size, tape, trf_id, trf_timestamp
///
/// `conditions` in the CSV is a comma-separated list of integer codes
/// (e.g. "12,41"). We parse it to UTINYINT[] at conversion time so downstream
/// readers can use `list_has_any` / `list_has_all` directly. All observed
/// Polygon condition codes fit in a byte (max seen: 53; spec max: 87), so
/// UTINYINT enforces that invariant and keeps in-memory representation small.
let private convertTradesCsvGzToParquet (csvGzPath: string) (parquetPath: string) : unit =
    let parquetEscaped = parquetPath.Replace("'", "''")
    let sql =
        sprintf
            """COPY (
                SELECT
                    * EXCLUDE conditions,
                    CASE
                        WHEN conditions IS NULL OR conditions = '' THEN []::UTINYINT[]
                        ELSE CAST(string_split(conditions, ',') AS UTINYINT[])
                    END AS conditions
                FROM read_csv('/dev/stdin', types={'conditions': 'VARCHAR'})
            ) TO '%s' (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3, ROW_GROUP_SIZE 122880)"""
            parquetEscaped
    runZcatDuckdb csvGzPath sql

// -----------------------------------------------------------------------------
// Dataset kind (one shape, three flavours)
// -----------------------------------------------------------------------------

type private DatasetKind =
    | Daily         // no conversion; csv.gz IS the final artifact
    | Minute
    | Trades of skipConvert: bool   // skipConvert=true leaves csv.gz as final

/// Suffix used to disambiguate temp files from the three datasets if they
/// share a temp dir.
let private kindSuffix (k: DatasetKind) : string =
    match k with
    | Daily -> "daily"
    | Minute -> "minute"
    | Trades _ -> "trades"

let private getS3Key (k: DatasetKind) (date: DateTime) : string =
    match k with
    | Daily -> getS3KeyDaily date
    | Minute -> getS3KeyMinute date
    | Trades _ -> getS3KeyTrades date

/// Path of the final on-disk artifact for this (kind, date) on the HDD.
let private finalArtifactPath (k: DatasetKind) (outputDir: string) (date: DateTime) : string =
    let dateStr = date.ToString("yyyy-MM-dd")
    match k with
    | Daily -> Path.Combine(outputDir, $"{dateStr}.csv.gz")
    | Minute -> Path.Combine(outputDir, $"{dateStr}.parquet")
    | Trades skipConvert ->
        if skipConvert then Path.Combine(outputDir, $"{dateStr}.csv.gz")
        else Path.Combine(outputDir, $"{dateStr}.parquet")

/// SSD-staged csv.gz path. Always lives under tempDir so all random I/O
/// (gzip body write, sequential read for conversion) stays off the HDD.
let private stagedCsvGzPath (k: DatasetKind) (tempDir: string) (date: DateTime) : string =
    let dateStr = date.ToString("yyyy-MM-dd")
    Path.Combine(tempDir, $"{dateStr}.{kindSuffix k}.csv.gz")

// -----------------------------------------------------------------------------
// Stage 1: download csv.gz from S3 to SSD
// -----------------------------------------------------------------------------

type private DownloadOutcome =
    | DownloadOk
    | DownloadSkippedHoliday    // 404 — US market holiday
    | DownloadFailed of string

let private downloadOneCsvGz
    (client: AmazonS3Client)
    (k: DatasetKind)
    (tempDir: string)
    (date: DateTime)
    (ct: CancellationToken)
    : Task<DownloadOutcome> =
    task {
        let s3Key = getS3Key k date
        let stagedPath = stagedCsvGzPath k tempDir date
        let tmpPath = stagedPath + ".tmp"
        let rec attempt n =
            task {
                try
                    if File.Exists tmpPath then File.Delete tmpPath
                    let request = GetObjectRequest(BucketName = bucketName, Key = s3Key)
                    use! response = client.GetObjectAsync(request, ct)
                    use responseStream = response.ResponseStream
                    use fileStream = File.Create(tmpPath)
                    do! responseStream.CopyToAsync(fileStream, ct)
                    fileStream.Close()
                    responseStream.Close()
                    // Atomic rename within tempDir (same volume).
                    File.Move(tmpPath, stagedPath, overwrite = true)
                    return DownloadOk
                with
                | ex when isS3NotFound ex ->
                    try File.Delete tmpPath with _ -> ()
                    return DownloadSkippedHoliday
                | :? AmazonS3Exception as ex
                    when (ex.StatusCode = HttpStatusCode.ServiceUnavailable
                          || ex.StatusCode = HttpStatusCode.TooManyRequests
                          || ex.ErrorCode.Contains "TooManyRequests"
                          || ex.ErrorCode.Contains "SlowDown")
                          && n < maxRetries ->
                    let delay = pown 2 n * 1000
                    do! Task.Delay(delay, ct)
                    return! attempt (n + 1)
                | :? AmazonS3Exception as ex when n >= maxRetries ->
                    try File.Delete tmpPath with _ -> ()
                    return DownloadFailed (sprintf "S3 Error (MAX RETRIES): %A - %s" ex.StatusCode ex.Message)
                | :? AmazonS3Exception as ex ->
                    try File.Delete tmpPath with _ -> ()
                    return DownloadFailed (sprintf "S3 Error: %A - %s" ex.StatusCode ex.Message)
                | _ when n < maxRetries ->
                    let delay = pown 2 n * 1000
                    do! Task.Delay(delay, ct)
                    return! attempt (n + 1)
                | ex ->
                    try File.Delete tmpPath with _ -> ()
                    return DownloadFailed (sprintf "Transport Error (MAX RETRIES): %s" ex.Message)
            }
        return! attempt 1
    }

// -----------------------------------------------------------------------------
// Stage 2: convert SSD csv.gz to HDD parquet (atomic via .tmp + rename)
// -----------------------------------------------------------------------------
//
// `Daily` has no actual conversion: the csv.gz IS the artifact. We just move
// it from the SSD to the HDD (cross-volume copy on WSL, so File.Move falls
// back to copy+delete). `Trades(skipConvert=true)` is the same: csv.gz is the
// artifact, move SSD->HDD.

type private ConvertOutcome =
    | ConvertOk
    | ConvertFailed of string

let private convertOne
    (k: DatasetKind)
    (outputDir: string)
    (tempDir: string)
    (date: DateTime)
    : ConvertOutcome =
    let stagedCsvGz = stagedCsvGzPath k tempDir date
    let finalPath = finalArtifactPath k outputDir date
    try
        match k with
        | Daily ->
            // csv.gz is the artifact; just move SSD -> HDD via .tmp rename.
            let tmpPath = finalPath + ".tmp"
            if File.Exists tmpPath then File.Delete tmpPath
            File.Move(stagedCsvGz, tmpPath)
            File.Move(tmpPath, finalPath, overwrite = true)
            ConvertOk
        | Trades true ->
            // skipConvert mode: same as Daily, csv.gz is the artifact.
            let tmpPath = finalPath + ".tmp"
            if File.Exists tmpPath then File.Delete tmpPath
            File.Move(stagedCsvGz, tmpPath)
            File.Move(tmpPath, finalPath, overwrite = true)
            ConvertOk
        | Minute ->
            let tmpParquet = finalPath + ".tmp"
            if File.Exists tmpParquet then File.Delete tmpParquet
            convertMinuteCsvGzToParquet stagedCsvGz tmpParquet
            File.Move(tmpParquet, finalPath, overwrite = true)
            try File.Delete stagedCsvGz with _ -> ()
            ConvertOk
        | Trades false ->
            let tmpParquet = finalPath + ".tmp"
            if File.Exists tmpParquet then File.Delete tmpParquet
            convertTradesCsvGzToParquet stagedCsvGz tmpParquet
            File.Move(tmpParquet, finalPath, overwrite = true)
            try File.Delete stagedCsvGz with _ -> ()
            ConvertOk
    with ex ->
        // Conversion failed (most likely a truncated csv.gz). Clean up the
        // SSD staging file and any .parquet.tmp so the next run re-downloads.
        try File.Delete stagedCsvGz with _ -> ()
        try File.Delete (finalPath + ".tmp") with _ -> ()
        ConvertFailed (sprintf "convert: %s" ex.Message)

// -----------------------------------------------------------------------------
// Orphan-temp sweep
// -----------------------------------------------------------------------------

let private sweepDir (root: string) : int =
    if not (Directory.Exists root) then 0
    else
        let mutable n = 0
        let patterns = [| "*.csv.gz.tmp"; "*.parquet.tmp" |]
        for pat in patterns do
            for tmp in Directory.EnumerateFiles(root, pat, SearchOption.TopDirectoryOnly) do
                try
                    File.Delete tmp
                    n <- n + 1
                with _ -> ()
        n

/// Sweep .csv.gz.tmp and .parquet.tmp residue from prior interrupted runs.
/// Returns total count swept.
let sweepOrphanTemps (outputDir: string) (tempDir: string) : int =
    sweepDir outputDir + (if tempDir = outputDir then 0 else sweepDir tempDir)

// -----------------------------------------------------------------------------
// Public types (back-compat with handlers in Program.fs)
// -----------------------------------------------------------------------------

type ProgressCallback = int -> int -> DownloadResult -> unit

let consoleProgress (completed: int) (total: int) (result: DownloadResult) : unit =
    let status, dateStr =
        match result with
        | Downloaded date -> "Downloaded", date.ToString("yyyy-MM-dd")
        | Skipped date -> "Skipped", date.ToString("yyyy-MM-dd")
        | Failed (date, error) -> $"Failed ({error})", date.ToString("yyyy-MM-dd")
    printfn "[%d/%d] %s: %s" completed total dateStr status

// -----------------------------------------------------------------------------
// Channel pipeline (shared by all three datasets)
// -----------------------------------------------------------------------------

/// Inter-stage record carrying the date whose staged csv.gz is ready to convert.
type private StagedJob = { Date: DateTime }

let private runPipeline
    (client: AmazonS3Client)
    (k: DatasetKind)
    (outputDir: string)
    (tempDir: string)
    (startDate: DateTime)
    (endDate: DateTime)
    (downloadParallelism: int)
    (convertParallelism: int)
    (progress: ProgressCallback option)
    (ct: CancellationToken)
    : Task<DownloadResult list> =
    task {
        Directory.CreateDirectory(outputDir) |> ignore
        Directory.CreateDirectory(tempDir) |> ignore
        let nSwept = sweepOrphanTemps outputDir tempDir
        if nSwept > 0 then
            printfn "Swept %d orphaned .tmp file(s) from prior run." nSwept

        let dates = getTradingDays startDate endDate
        let total = dates.Length

        let jobsCh = Channel.CreateUnbounded<DateTime>()
        // Bounded at 8: each staged csv.gz is up to ~2 GB; 8 × 2 GB = ~16 GB
        // SSD high-water, well within the SSD's headroom. When the converter
        // falls behind, download workers block on WriteAsync instead of
        // unbounded-growing the queue.
        let stagedCh =
            Channel.CreateBounded<StagedJob>(
                BoundedChannelOptions(8,
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = false))
        let resultsCh = Channel.CreateUnbounded<DownloadResult>()

        let writeResult (r: DownloadResult) : Task =
            task {
                do! resultsCh.Writer.WriteAsync(r, ct)
            } :> Task

        // --- Stage 0: skip-filter + producer ---
        // Each date: if the final artifact is on the HDD, emit Skipped.
        // Else if a staged csv.gz is already on the SSD (e.g. a prior run
        // got interrupted after download but before conversion), push it
        // straight to the convert stage. Else hand to the download stage.
        let skipStage =
            task {
                for date in dates do
                    let finalPath = finalArtifactPath k outputDir date
                    let stagedPath = stagedCsvGzPath k tempDir date
                    if File.Exists finalPath then
                        do! writeResult (Skipped date)
                    elif File.Exists stagedPath then
                        // Hand to convert stage directly — but stagedCh is
                        // bounded; if it's full the writer waits.
                        do! stagedCh.Writer.WriteAsync({ Date = date }, ct)
                    else
                        do! jobsCh.Writer.WriteAsync(date, ct)
                jobsCh.Writer.Complete()
            } :> Task

        // --- Stage 1: download workers (SSD-bound writes) ---
        let downloadWorker () : Task =
            task {
                let reader = jobsCh.Reader
                let writer = stagedCh.Writer
                let mutable date = Unchecked.defaultof<DateTime>
                while! reader.WaitToReadAsync(ct) do
                    while reader.TryRead(&date) do
                        let! outcome = downloadOneCsvGz client k tempDir date ct
                        match outcome with
                        | DownloadOk ->
                            do! writer.WriteAsync({ Date = date }, ct)
                        | DownloadSkippedHoliday ->
                            // 404 = market holiday; emit Skipped result.
                            do! writeResult (Skipped date)
                        | DownloadFailed msg ->
                            do! writeResult (Failed (date, msg))
            } :> Task

        let downloadWorkers =
            [| for _ in 1 .. downloadParallelism -> downloadWorker () |]

        let downloadDone =
            task {
                do! Task.WhenAll downloadWorkers
                stagedCh.Writer.Complete()
            } :> Task

        // --- Stage 2: convert workers (HDD-bound sequential writes) ---
        //
        // Each convert worker runs on a DEDICATED long-running thread, not
        // the .NET thread pool. convertOne does blocking I/O (zcat pipe to
        // duckdb child process, writes 1-2 GB sequential parquet) that can
        // run for minutes. Putting it on the regular thread pool contends
        // with HTTP completion callbacks (Stage 1's downloads) and risks the
        // same I/O-completion starvation deadlock that bit the Binance
        // pipeline. TaskCreationOptions.LongRunning hands the runtime
        // permission to spin up a dedicated thread outside the pool.
        let convertWorkerBody () =
            let reader = stagedCh.Reader
            let mutable job = Unchecked.defaultof<StagedJob>
            let mutable keepGoing = true
            while keepGoing do
                let ready =
                    try reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult()
                    with :? OperationCanceledException -> false
                if not ready then
                    keepGoing <- false
                else
                    while reader.TryRead(&job) do
                        let outcome = convertOne k outputDir tempDir job.Date
                        let result =
                            match outcome with
                            | ConvertOk -> Downloaded job.Date
                            | ConvertFailed msg -> Failed (job.Date, msg)
                        resultsCh.Writer.WriteAsync(result, ct).AsTask().GetAwaiter().GetResult()

        let startConvertWorker () : Task =
            Task.Factory.StartNew(
                Action(convertWorkerBody),
                ct,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)

        let convertWorkers =
            [| for _ in 1 .. convertParallelism -> startConvertWorker () |]

        let pipelineDone =
            task {
                do! skipStage
                do! downloadDone
                do! Task.WhenAll convertWorkers
                resultsCh.Writer.Complete()
            } :> Task

        // --- Reporter: single owner of the result list ---
        // No locks, no Interlocked — only this task touches `allResults` and
        // `completed`. Per-drain flush of stdout so progress streams to a
        // redirected log file in real time.
        let reporter =
            task {
                let allResults = ResizeArray<DownloadResult>(total)
                let mutable completed = 0
                let reader = resultsCh.Reader
                let mutable r = Unchecked.defaultof<DownloadResult>
                while! reader.WaitToReadAsync(ct) do
                    while reader.TryRead(&r) do
                        allResults.Add r
                        completed <- completed + 1
                        match progress with
                        | Some cb -> cb completed total r
                        | None -> ()
                    Console.Out.Flush()
                return allResults.ToArray()
            }

        do! pipelineDone
        let! results = reporter
        return results |> Array.toList
    }

// -----------------------------------------------------------------------------
// Public API — one entry point per dataset
// -----------------------------------------------------------------------------

/// Download daily aggregates for a date range. csv.gz IS the final artifact;
/// no conversion is done — staged on SSD via tempDir, then moved to outputDir.
let downloadDailyAggregates
    (client: AmazonS3Client)
    (startDate: DateTime)
    (endDate: DateTime)
    (outputDir: string)
    (tempDir: string)
    (downloadParallelism: int)
    (convertParallelism: int)
    (progress: ProgressCallback option)
    (ct: CancellationToken)
    : Async<DownloadResult list> =
    runPipeline client Daily outputDir tempDir startDate endDate
        downloadParallelism convertParallelism progress ct
    |> Async.AwaitTask

/// Download market-wide minute aggregates for a date range and convert each
/// day's file to zstd-compressed Parquet. Output: one file per trading day at
/// `{outputDir}/{yyyy-MM-dd}.parquet`. Downloads stage on SSD via tempDir.
let downloadMinuteAggregates
    (client: AmazonS3Client)
    (startDate: DateTime)
    (endDate: DateTime)
    (outputDir: string)
    (tempDir: string)
    (downloadParallelism: int)
    (convertParallelism: int)
    (progress: ProgressCallback option)
    (ct: CancellationToken)
    : Async<DownloadResult list> =
    runPipeline client Minute outputDir tempDir startDate endDate
        downloadParallelism convertParallelism progress ct
    |> Async.AwaitTask

/// Download market-wide trades for a date range and convert each day's file
/// to zstd-compressed Parquet. Output: one file per trading day at
/// `{outputDir}/{yyyy-MM-dd}.parquet`.
///
/// When `skipConvert = true`, the .csv.gz itself is the final artifact and
/// conversion is deferred. Downloads stage on SSD via tempDir.
let downloadTrades
    (client: AmazonS3Client)
    (startDate: DateTime)
    (endDate: DateTime)
    (outputDir: string)
    (tempDir: string)
    (downloadParallelism: int)
    (convertParallelism: int)
    (skipConvert: bool)
    (progress: ProgressCallback option)
    (ct: CancellationToken)
    : Async<DownloadResult list> =
    runPipeline client (Trades skipConvert) outputDir tempDir startDate endDate
        downloadParallelism convertParallelism progress ct
    |> Async.AwaitTask
