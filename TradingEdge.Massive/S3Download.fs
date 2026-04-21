module TradingEdge.S3Download

open System
open System.IO
open System.Net
open System.Threading
open Amazon.S3
open Amazon.S3.Model
open DuckDB.NET.Data

/// S3 configuration for Massive
let private bucketName = "flatfiles"
let private serviceUrl = "https://files.massive.com"
let private maxRetries = 5

/// Create an S3 client configured for Massive
let createS3Client (accessKey: string) (secretKey: string) : AmazonS3Client =
    let config = AmazonS3Config(
        ServiceURL = serviceUrl,
        AuthenticationRegion = "us-east-1",
        ForcePathStyle = true
    )
    new AmazonS3Client(accessKey, secretKey, config)

/// Generate the S3 key for a daily aggregate file
let private getS3Key (date: DateTime) : string =
    let dateStr = date.ToString("yyyy-MM-dd")
    let year = date.Year.ToString()
    let month = date.Month.ToString("00")
    $"us_stocks_sip/day_aggs_v1/{year}/{month}/{dateStr}.csv.gz"

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

/// Download a single day's data from S3
let private downloadSingleDay
    (client: AmazonS3Client)
    (outputDir: string)
    (date: DateTime)
    (ct: CancellationToken)
    : Async<DownloadResult> =
    async {
        let dateStr = date.ToString("yyyy-MM-dd")
        let s3Key = getS3Key date
        let localPath = Path.Combine(outputDir, $"{dateStr}.csv.gz")

        if File.Exists localPath then
            return Skipped date
        else
            let rec retry attempt =
                async {
                    try
                        let request = GetObjectRequest(
                            BucketName = bucketName,
                            Key = s3Key
                        )

                        let! response = client.GetObjectAsync(request, ct) |> Async.AwaitTask

                        use responseStream = response.ResponseStream
                        use fileStream = File.Create(localPath)
                        do! responseStream.CopyToAsync(fileStream, ct) |> Async.AwaitTask

                        return Downloaded date
                    with
                    | :? AmazonS3Exception as ex
                        when (ex.StatusCode = HttpStatusCode.ServiceUnavailable
                              || ex.StatusCode = HttpStatusCode.TooManyRequests
                              || ex.ErrorCode.Contains "TooManyRequests"
                              || ex.ErrorCode.Contains "SlowDown")
                              && attempt < maxRetries ->
                        // Exponential backoff: 2s, 4s, 8s, 16s...
                        let delay = pown 2 attempt * 1000
                        do! Async.Sleep delay
                        return! retry (attempt + 1)

                    | :? AmazonS3Exception as ex when attempt >= maxRetries ->
                        return Failed(date, $"S3 Error (MAX RETRIES): {ex.StatusCode} - {ex.Message}")

                    | :? AmazonS3Exception as ex ->
                        return Failed(date, $"S3 Error: {ex.StatusCode} - {ex.Message}")

                    | ex ->
                        return Failed(date, ex.Message)
                }
            return! retry 1
    }

/// Progress callback type
type ProgressCallback = int -> int -> DownloadResult -> unit

/// Download daily aggregates for a date range
let downloadDailyAggregates
    (client: AmazonS3Client)
    (startDate: DateTime)
    (endDate: DateTime)
    (outputDir: string)
    (maxParallelism: int)
    (progress: ProgressCallback option)
    (ct: CancellationToken)
    : Async<DownloadResult list> =
    async {
        Directory.CreateDirectory(outputDir) |> ignore

        let dates = getTradingDays startDate endDate
        let total = dates.Length
        let completed = ref 0

        let reportProgress result =
            let c = Interlocked.Increment(completed)
            match progress with
            | Some callback -> callback c total result
            | None -> ()

        // Use SemaphoreSlim for parallelism control
        use semaphore = new SemaphoreSlim(maxParallelism, maxParallelism)

        let downloadWithSemaphore date =
            async {
                do! semaphore.WaitAsync(ct) |> Async.AwaitTask
                try
                    let! result = downloadSingleDay client outputDir date ct
                    reportProgress result
                    return result
                finally
                    semaphore.Release() |> ignore
            }

        let! results =
            dates
            |> List.map downloadWithSemaphore
            |> Async.Parallel

        return results |> Array.toList
    }

/// Default progress reporter that prints to console
let consoleProgress (completed: int) (total: int) (result: DownloadResult) : unit =
    let status, dateStr =
        match result with
        | Downloaded date -> "Downloaded", date.ToString("yyyy-MM-dd")
        | Skipped date -> "Skipped", date.ToString("yyyy-MM-dd")
        | Failed (date, error) -> $"Failed ({error})", date.ToString("yyyy-MM-dd")

    printfn "[%d/%d] %s: %s" completed total dateStr status

// ============================================================================
// Minute aggregates (market-wide, flat-file bulk)
// ============================================================================

/// Generate the S3 key for a minute aggregate file. Parallel to `getS3Key`
/// but points at the minute_aggs_v1 prefix.
let private getS3KeyMinute (date: DateTime) : string =
    let dateStr = date.ToString("yyyy-MM-dd")
    let year = date.Year.ToString()
    let month = date.Month.ToString("00")
    $"us_stocks_sip/minute_aggs_v1/{year}/{month}/{dateStr}.csv.gz"

/// Convert a downloaded minute-aggs .csv.gz to zstd-compressed Parquet, then
/// delete the .csv.gz. Uses DuckDB's native gzip CSV reader. Schema is:
/// ticker VARCHAR, volume BIGINT, open DOUBLE, close DOUBLE, high DOUBLE,
/// low DOUBLE, window_start BIGINT, transactions BIGINT.
let private convertCsvGzToParquet (csvGzPath: string) (parquetPath: string) : unit =
    // In-memory DuckDB per call — avoids cross-thread contention when called
    // from parallel workers.
    use conn = new DuckDBConnection("DataSource=:memory:")
    conn.Open()
    use cmd = conn.CreateCommand()
    let csvEscaped = csvGzPath.Replace("'", "''")
    let parquetEscaped = parquetPath.Replace("'", "''")
    cmd.CommandText <-
        sprintf
            "COPY (SELECT * FROM read_csv_auto('%s', compression='gzip')) TO '%s' (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3)"
            csvEscaped parquetEscaped
    cmd.ExecuteNonQuery() |> ignore

/// Download a single day's minute aggregates from S3, convert to Parquet, and
/// delete the intermediate .csv.gz. Skips the day if the Parquet output
/// already exists (so the command is resumable).
let private downloadSingleDayMinute
    (client: AmazonS3Client)
    (outputDir: string)
    (date: DateTime)
    (ct: CancellationToken)
    : Async<DownloadResult> =
    async {
        let dateStr = date.ToString("yyyy-MM-dd")
        let s3Key = getS3KeyMinute date
        let parquetPath = Path.Combine(outputDir, $"{dateStr}.parquet")
        let csvGzPath = Path.Combine(outputDir, $"{dateStr}.csv.gz")

        if File.Exists parquetPath then
            return Skipped date
        else
            let rec retry attempt =
                async {
                    try
                        let request = GetObjectRequest(BucketName = bucketName, Key = s3Key)
                        let! response = client.GetObjectAsync(request, ct) |> Async.AwaitTask
                        use responseStream = response.ResponseStream
                        use fileStream = File.Create(csvGzPath)
                        do! responseStream.CopyToAsync(fileStream, ct) |> Async.AwaitTask
                        fileStream.Close()
                        responseStream.Close()

                        // Convert to Parquet, then clean up the .csv.gz. If
                        // conversion fails, leave the .csv.gz behind so the next
                        // run can re-try without re-downloading.
                        convertCsvGzToParquet csvGzPath parquetPath
                        File.Delete csvGzPath

                        return Downloaded date
                    with
                    | :? AmazonS3Exception as ex
                        when (ex.StatusCode = HttpStatusCode.ServiceUnavailable
                              || ex.StatusCode = HttpStatusCode.TooManyRequests
                              || ex.ErrorCode.Contains "TooManyRequests"
                              || ex.ErrorCode.Contains "SlowDown")
                              && attempt < maxRetries ->
                        let delay = pown 2 attempt * 1000
                        do! Async.Sleep delay
                        return! retry (attempt + 1)

                    | :? AmazonS3Exception as ex when attempt >= maxRetries ->
                        return Failed(date, $"S3 Error (MAX RETRIES): {ex.StatusCode} - {ex.Message}")

                    | :? AmazonS3Exception as ex ->
                        return Failed(date, $"S3 Error: {ex.StatusCode} - {ex.Message}")

                    // Transport-layer errors (TCP reset mid-download, TLS,
                    // socket timeouts, etc.) are usually transient. Retry
                    // with exponential backoff like we do for S3 throttling.
                    | _ when attempt < maxRetries ->
                        let delay = pown 2 attempt * 1000
                        do! Async.Sleep delay
                        return! retry (attempt + 1)

                    | ex ->
                        return Failed(date, $"Transport Error (MAX RETRIES): {ex.Message}")
                }
            return! retry 1
    }

/// Download market-wide minute aggregates for a date range and convert each
/// day's file to zstd-compressed Parquet. Output: one file per trading day at
/// `{outputDir}/{yyyy-MM-dd}.parquet`.
let downloadMinuteAggregates
    (client: AmazonS3Client)
    (startDate: DateTime)
    (endDate: DateTime)
    (outputDir: string)
    (maxParallelism: int)
    (progress: ProgressCallback option)
    (ct: CancellationToken)
    : Async<DownloadResult list> =
    async {
        Directory.CreateDirectory(outputDir) |> ignore

        let dates = getTradingDays startDate endDate
        let total = dates.Length
        let completed = ref 0

        let reportProgress result =
            let c = Interlocked.Increment(completed)
            match progress with
            | Some callback -> callback c total result
            | None -> ()

        use semaphore = new SemaphoreSlim(maxParallelism, maxParallelism)

        let downloadWithSemaphore date =
            async {
                do! semaphore.WaitAsync(ct) |> Async.AwaitTask
                try
                    let! result = downloadSingleDayMinute client outputDir date ct
                    reportProgress result
                    return result
                finally
                    semaphore.Release() |> ignore
            }

        let! results =
            dates
            |> List.map downloadWithSemaphore
            |> Async.Parallel

        return results |> Array.toList
    }

// ============================================================================
// Trades (market-wide, flat-file bulk)
// ============================================================================

/// Generate the S3 key for a trades file. Parallel to `getS3KeyMinute` but
/// points at the trades_v1 prefix.
let private getS3KeyTrades (date: DateTime) : string =
    let dateStr = date.ToString("yyyy-MM-dd")
    let year = date.Year.ToString()
    let month = date.Month.ToString("00")
    $"us_stocks_sip/trades_v1/{year}/{month}/{dateStr}.csv.gz"

/// Convert a downloaded trades .csv.gz to zstd-compressed Parquet, then delete
/// the .csv.gz. CSV schema (from Polygon flat-file docs):
///   ticker, conditions, correction, exchange, id, participant_timestamp,
///   price, sequence_number, sip_timestamp, size, tape, trf_id, trf_timestamp
///
/// `conditions` in the CSV is a comma-separated list of integer codes
/// (e.g. "12,41"). We parse it to UTINYINT[] at conversion time so downstream
/// readers can use `list_has_any` / `list_has_all` directly. All observed
/// Polygon condition codes fit in a byte (max seen: 53; spec max: 87), so
/// UTINYINT enforces that invariant and keeps in-memory representation small.
/// On disk, zstd compresses UTINYINT[] and INTEGER[] to essentially the same
/// size, so the storage win is negligible — this is about query-time memory.
let private convertTradesCsvGzToParquet (csvGzPath: string) (parquetPath: string) : unit =
    use conn = new DuckDBConnection("DataSource=:memory:")
    conn.Open()
    // Cap memory and give DuckDB a spill directory so it can stream large
    // files to disk instead of OOMing. 2026-era trades CSVs hit ~3 GB
    // compressed / ~12 GB decoded; the full result set cannot stay resident
    // on a 16 GB box while N parallel workers also run.
    use setMem = conn.CreateCommand()
    setMem.CommandText <- "SET memory_limit='6GB'; SET preserve_insertion_order=false; SET threads=2;"
    setMem.ExecuteNonQuery() |> ignore

    use cmd = conn.CreateCommand()
    let csvEscaped = csvGzPath.Replace("'", "''")
    let parquetEscaped = parquetPath.Replace("'", "''")
    // ROW_GROUP_SIZE bounds the parquet writer's in-memory buffer; combined
    // with preserve_insertion_order=false, DuckDB emits row groups as they
    // fill instead of staging the whole file.
    cmd.CommandText <-
        sprintf
            """COPY (
                SELECT
                    * EXCLUDE conditions,
                    CASE
                        WHEN conditions IS NULL OR conditions = '' THEN []::UTINYINT[]
                        ELSE CAST(string_split(conditions, ',') AS UTINYINT[])
                    END AS conditions
                FROM read_csv_auto(
                    '%s',
                    compression='gzip',
                    types={'conditions': 'VARCHAR'}
                )
            ) TO '%s' (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3, ROW_GROUP_SIZE 122880)"""
            csvEscaped parquetEscaped
    cmd.ExecuteNonQuery() |> ignore

/// Download a single day's trades from S3, optionally convert to Parquet, and
/// delete the intermediate .csv.gz. Skips if the Parquet output already
/// exists. If a .csv.gz is already present but no .parquet (e.g. previous
/// run crashed mid-conversion), we skip the download and jump straight to
/// conversion. When `skipConvert = true`, we download only and treat an
/// existing .csv.gz as already-done.
let private downloadSingleDayTrades
    (client: AmazonS3Client)
    (outputDir: string)
    (skipConvert: bool)
    (date: DateTime)
    (ct: CancellationToken)
    : Async<DownloadResult> =
    async {
        let dateStr = date.ToString("yyyy-MM-dd")
        let s3Key = getS3KeyTrades date
        let parquetPath = Path.Combine(outputDir, $"{dateStr}.parquet")
        let csvGzPath = Path.Combine(outputDir, $"{dateStr}.csv.gz")

        // Download-only mode: the .csv.gz itself is the final artifact.
        if skipConvert && File.Exists csvGzPath then
            return Skipped date
        // If both csv.gz and parquet coexist, the previous run likely crashed
        // mid-conversion leaving a partial parquet. Treat the csv.gz as
        // authoritative and redo the conversion.
        elif File.Exists csvGzPath && not skipConvert then
            if File.Exists parquetPath then
                try File.Delete parquetPath with _ -> ()
            try
                convertTradesCsvGzToParquet csvGzPath parquetPath
                File.Delete csvGzPath
                return Downloaded date
            with ex ->
                // The csv.gz is probably truncated. Remove it and fall
                // through to a fresh download by returning Failed (the outer
                // retry machinery doesn't re-enter this function; the next
                // run will re-download).
                try File.Delete csvGzPath with _ -> ()
                try File.Delete parquetPath with _ -> ()
                return Failed(date, $"Convert failed on existing csv.gz, deleted for retry: {ex.Message}")
        elif File.Exists parquetPath then
            return Skipped date
        else
            let rec retry attempt =
                async {
                    try
                        let request = GetObjectRequest(BucketName = bucketName, Key = s3Key)
                        let! response = client.GetObjectAsync(request, ct) |> Async.AwaitTask
                        use responseStream = response.ResponseStream
                        use fileStream = File.Create(csvGzPath)
                        do! responseStream.CopyToAsync(fileStream, ct) |> Async.AwaitTask
                        fileStream.Close()
                        responseStream.Close()

                        if not skipConvert then
                            convertTradesCsvGzToParquet csvGzPath parquetPath
                            File.Delete csvGzPath

                        return Downloaded date
                    with
                    | :? AmazonS3Exception as ex
                        when (ex.StatusCode = HttpStatusCode.ServiceUnavailable
                              || ex.StatusCode = HttpStatusCode.TooManyRequests
                              || ex.ErrorCode.Contains "TooManyRequests"
                              || ex.ErrorCode.Contains "SlowDown")
                              && attempt < maxRetries ->
                        let delay = pown 2 attempt * 1000
                        do! Async.Sleep delay
                        return! retry (attempt + 1)

                    | :? AmazonS3Exception as ex when attempt >= maxRetries ->
                        return Failed(date, $"S3 Error (MAX RETRIES): {ex.StatusCode} - {ex.Message}")

                    | :? AmazonS3Exception as ex ->
                        return Failed(date, $"S3 Error: {ex.StatusCode} - {ex.Message}")

                    // Transport-layer errors (TCP reset mid-download, TLS,
                    // socket timeouts, etc.) are usually transient. Retry
                    // with exponential backoff like we do for S3 throttling.
                    | _ when attempt < maxRetries ->
                        let delay = pown 2 attempt * 1000
                        do! Async.Sleep delay
                        return! retry (attempt + 1)

                    | ex ->
                        return Failed(date, $"Transport Error (MAX RETRIES): {ex.Message}")
                }
            return! retry 1
    }

/// Download market-wide trades for a date range and convert each day's file to
/// zstd-compressed Parquet. Output: one file per trading day at
/// `{outputDir}/{yyyy-MM-dd}.parquet`.
///
/// When `skipConvert = true`, the .csv.gz itself is the final artifact and
/// conversion is deferred. Useful when the converter is too memory-hungry to
/// run alongside other workloads.
///
/// Files are large (multi-GB uncompressed). Keep parallelism modest (≤4) to
/// avoid saturating the uplink and triggering SlowDown retries.
let downloadTrades
    (client: AmazonS3Client)
    (startDate: DateTime)
    (endDate: DateTime)
    (outputDir: string)
    (maxParallelism: int)
    (skipConvert: bool)
    (progress: ProgressCallback option)
    (ct: CancellationToken)
    : Async<DownloadResult list> =
    async {
        Directory.CreateDirectory(outputDir) |> ignore

        let dates = getTradingDays startDate endDate
        let total = dates.Length
        let completed = ref 0

        let reportProgress result =
            let c = Interlocked.Increment(completed)
            match progress with
            | Some callback -> callback c total result
            | None -> ()

        use semaphore = new SemaphoreSlim(maxParallelism, maxParallelism)

        let downloadWithSemaphore date =
            async {
                do! semaphore.WaitAsync(ct) |> Async.AwaitTask
                try
                    let! result = downloadSingleDayTrades client outputDir skipConvert date ct
                    reportProgress result
                    return result
                finally
                    semaphore.Release() |> ignore
            }

        let! results =
            dates
            |> List.map downloadWithSemaphore
            |> Async.Parallel

        return results |> Array.toList
    }
