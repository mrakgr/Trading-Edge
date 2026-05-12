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
// CSV → parquet (single-day path: streaming COPY via DuckDB read_csv)
// -----------------------------------------------------------------------------
//
// Rewritten 2026-05-12: previous implementation built the entire table in
// memory via a DuckDB appender, then COPYed it. With --parallelism 4 and
// large monthly archives (BONK 2023-12 was 2.4 GB CSV decompressed) the four
// appender buffers competed for RAM and DuckDB's native code deadlocked at
// 0% CPU with 22 .NET threads on futex — likely an OOM that swallowed the
// exception through P/Invoke. The new path hands DuckDB the CSV via
// read_csv(path) so it streams rows from disk into parquet rowgroups
// without materializing the full table. Peak memory becomes O(rowgroup),
// not O(file).

let private sqlEscape (s: string) : string =
    s.Replace('\\', '/').Replace("'", "''")

/// Extract the (single) .csv entry from a Binance trade-archive zip onto
/// disk. Throws on missing entry. Used by both daily and monthly converters
/// because DuckDB has no native zip-archive filesystem (verified DuckDB
/// 1.4.4); feeding it a managed Stream from `entry.Open()` is also not
/// supported by DuckDB.NET. Cost is ~3-5x the zip size in transient disk;
/// deleted by the caller in a finally regardless of conversion outcome.
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

/// Stream a CSV file directly to a single-day parquet via DuckDB read_csv +
/// COPY. The CSV must already be on disk (callers extract from the zip into
/// a temp file first). Returns the row count.
let private writeSingleDayParquet
    (csvPath: string)
    (outputPath: string)
    : int =
    let tmpPath = outputPath + ".tmp"
    if File.Exists tmpPath then File.Delete tmpPath
    let csvNorm = sqlEscape csvPath
    let outNorm = sqlEscape tmpPath

    use connection = new DuckDBConnection("Data Source=:memory:")
    connection.Open()

    // Stream CSV → parquet in one statement. No intermediate table.
    // is_buyer_maker is a bool column from Binance's CSVs.
    use copyCmd = connection.CreateCommand()
    copyCmd.CommandText <-
        sprintf "
            COPY (
                SELECT
                    price,
                    qty AS quantity,
                    time * 1000 AS timestamp_us,
                    CASE WHEN is_buyer_maker THEN -1.0 ELSE 1.0 END AS sign
                FROM read_csv('%s', header=true)
            ) TO '%s' (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3)"
            csvNorm outNorm
    copyCmd.ExecuteNonQuery() |> ignore

    // Row count via a lightweight scan of the freshly written parquet.
    use countCmd = connection.CreateCommand()
    countCmd.CommandText <- sprintf "SELECT COUNT(*) FROM read_parquet('%s')" outNorm
    let n =
        use rdr = countCmd.ExecuteReader()
        if rdr.Read() then int (rdr.GetInt64 0) else 0

    File.Move(tmpPath, outputPath, overwrite = true)
    n

// -----------------------------------------------------------------------------
// CSV → parquet (monthly path: streaming PARTITION_BY + rename)
// -----------------------------------------------------------------------------
//
// Rewritten 2026-05-12. Old design: a per-day DuckDB appender that buffered
// rows in memory and COPYed on day-boundary. With 4 in-flight monthlies the
// peak RAM hit DuckDB's per-:memory:-table ceiling and the native code
// deadlocked silently (likely OOM, exception swallowed through P/Invoke).
//
// New design: one statement —
//   COPY (SELECT ..., strftime(..., '%Y-%m-%d') AS date_str
//         FROM read_csv(path)) TO partitionDir
//   (PARTITION_BY (date_str), ...)
// DuckDB streams rows from disk and writes one parquet per UTC day into a
// directory tree {partitionDir}/date_str=YYYY-MM-DD/data_0.parquet. We then
// rename each partition file into the production flat layout
// {SYMBOL}/{SYMBOL}-trades-YYYY-MM-DD.parquet. Resume-aware: per-day target
// already on disk → that partition's rename is skipped.
//
// Peak memory: O(parquet rowgroup), not O(monthly_csv_size).

/// Stream a monthly CSV (already extracted to disk) into per-day parquets
/// via PARTITION_BY, then rename each day's partition file to the
/// production output path. Returns (totalRows, daysWritten).
let private writeMonthlyParquets
    (csvPath: string)
    (dailyOutputPathOf: DateTime -> string)
    (partitionWorkDir: string)
    : int * int =
    if Directory.Exists partitionWorkDir then
        Directory.Delete(partitionWorkDir, recursive = true)
    Directory.CreateDirectory partitionWorkDir |> ignore
    let csvNorm = sqlEscape csvPath
    let partNorm = sqlEscape partitionWorkDir

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
                FROM read_csv('%s', header=true)
            )
            TO '%s'
            (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3,
             PARTITION_BY (date_str), OVERWRITE_OR_IGNORE)"
            csvNorm partNorm
    copyCmd.ExecuteNonQuery() |> ignore

    // Walk the partition directory; rename each day's file into the prod
    // layout. Skip days already present on disk (resume case).
    let mutable totalRows = 0
    let mutable daysWritten = 0
    for partDir in Directory.EnumerateDirectories partitionWorkDir do
        let name = Path.GetFileName partDir
        // partition dirs are "date_str=YYYY-MM-DD"
        let dateStr =
            if name.StartsWith "date_str=" then name.Substring 9 else name
        let date =
            DateTime.ParseExact(dateStr, "yyyy-MM-dd",
                Globalization.CultureInfo.InvariantCulture)
        let dstPath = dailyOutputPathOf date
        if File.Exists dstPath then
            // already promoted in a prior run; drop the temp partition
            ()
        else
            // partition output is a single file (or several if rowgroup limit
            // hits — DuckDB defaults to one per partition for our row scale).
            let files = Directory.GetFiles(partDir, "*.parquet")
            if files.Length = 1 then
                let src = files.[0]
                Directory.CreateDirectory(Path.GetDirectoryName dstPath) |> ignore
                // Count rows BEFORE the move so we have an accurate telemetry
                // number. Cheap — parquet metadata, not a full scan.
                use cnt = connection.CreateCommand()
                cnt.CommandText <-
                    sprintf "SELECT COUNT(*) FROM read_parquet('%s')" (sqlEscape src)
                use rdr = cnt.ExecuteReader()
                if rdr.Read() then
                    totalRows <- totalRows + int (rdr.GetInt64 0)
                File.Move(src, dstPath, overwrite = true)
                daysWritten <- daysWritten + 1
            elif files.Length > 1 then
                // Defensive: concatenate the partition's chunks into one
                // parquet at the destination. Happens if a partition exceeds
                // DuckDB's per-file limits (very large days).
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
    // Best-effort cleanup of the partition work dir.
    try Directory.Delete(partitionWorkDir, recursive = true) with _ -> ()
    totalRows, daysWritten

// -----------------------------------------------------------------------------
// Job-level dispatch
// -----------------------------------------------------------------------------

/// Path to the per-monthly completion sentinel. Zero-byte file written
/// after a successful monthly finalization. Its presence means: "we already
/// downloaded this monthly archive in full; don't re-fetch."
///
/// Why a sentinel and not just `monthlyAllSkipped` (the old "all expected
/// per-day parquets present" check):
///   - A monthly archive's actual contents can be a strict subset of
///     `[YYYY-MM-01, YYYY-MM-DaysInMonth]`. New listings span only
///     `[listing_day, EoM]`; delistings span `[1, delisting_day]`.
///   - In those cases the per-day check sees the missing days at the edges
///     and reports "not skipped", triggering a redundant re-download. The
///     rotation loop then sees every contained day as already-on-disk,
///     finalizes nothing, and reports `0 trades / 0 days` — wasted bandwidth.
///   - The sentinel records the truth ("we processed this archive end-to-
///     end") rather than guessing it from the per-day file presence.
let private monthlyCompleteSentinelPath (outputDir: string) (m: MonthlyEntry) : string =
    let dir = Path.Combine(outputDir, m.Symbol)
    Path.Combine(dir, sprintf ".complete-%04d-%02d" m.Year m.Month)

let private writeMonthlySentinel (outputDir: string) (m: MonthlyEntry) : unit =
    let path = monthlyCompleteSentinelPath outputDir m
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    if not (File.Exists path) then
        // Empty file; the existence is the signal.
        File.WriteAllBytes(path, [||])

/// True when this monthly should be short-circuited. Two sources of truth:
///   1. Sentinel file exists — definitive, set after a clean finalization.
///   2. All `[YYYY-MM-01, EoM]` per-day parquets present — heuristic legacy
///      check, kept as a fallback for monthlies completed before sentinels
///      existed. When this fallback fires, also write the sentinel so we
///      don't fall through to it again next time.
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

/// Stream the response body to a temp file on disk, then invoke `f` with the
/// path. ZipArchive(stream, Read) requires a seekable stream — handing it
/// HttpClient's non-seekable response stream forces an internal buffer-into-
/// MemoryStream which throws "Stream was too long" past 2 GB. Big monthly
/// archives (ETH, DOGE in volatile months) easily blow that cap when their
/// internal CSV decompresses to 10+ GB. Disk-backed temp avoids the cap and
/// lets us reuse the ZIP in case of a retry. Temp lives next to the eventual
/// parquet so it's on the same volume as the final output.
// Per-chunk idle deadline for the body copy. HttpClient.Timeout is ignored
// once HttpCompletionOption.ResponseHeadersRead returns control, so a server
// that stops sending mid-stream would otherwise hang the read forever (we
// observed a 5-hour silent stall on PARTIUSDT 2026-04). We hand-roll the
// copy so the deadline resets on every successful chunk: while bytes flow
// the timer keeps getting refreshed; only true silence for `idleDeadline`
// trips it. 15s is comfortably past normal TCP/DNS hiccups but converts an
// indefinite hang into a fast retry. Combined with maxRetries/backoff the
// worst-case stall budget per job is small, while legitimate multi-GB
// downloads progressing at any reasonable rate are unaffected.
let private idleDeadline = TimeSpan.FromSeconds 15.0
let private copyBufferSize = 1 <<< 20  // 1 MiB

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
            use fs = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, copyBufferSize)
            let buf = Array.zeroCreate<byte> copyBufferSize
            // One linked CTS for the whole copy; we re-arm its deadline at
            // the start of each chunk by calling CancelAfter again. Once a
            // chunk lands successfully the timer hasn't fired, so the next
            // CancelAfter just refreshes it. If a chunk truly stalls past
            // idleDeadline, the token cancels and ReadAsync throws.
            use cts = CancellationTokenSource.CreateLinkedTokenSource ct
            let mutable bytesThisChunk = 1
            let mutable stalled = false
            try
                while bytesThisChunk > 0 && not stalled do
                    cts.CancelAfter idleDeadline
                    try
                        let! n =
                            body.ReadAsync(buf, 0, buf.Length, cts.Token)
                            |> Async.AwaitTask
                        bytesThisChunk <- n
                        if n > 0 then
                            do! fs.WriteAsync(buf, 0, n, ct) |> Async.AwaitTask
                    with
                    | :? OperationCanceledException when not ct.IsCancellationRequested ->
                        // Our idle deadline fired — no bytes for idleDeadline.
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

let private withDownloadedZip
    (http: HttpClient)
    (url: string)
    (tempZipPath: string)
    (ct: CancellationToken)
    (f: string -> Async<'a>)
    : Async<Result<'a, string>> =
    // Retries cover the whole fetch+body-copy as one unit. fetchWithRetry
    // already retries the GET (header phase) on TooManyRequests / 5xx; what
    // this wrapper adds is retry on body-copy stalls (the 20-min deadline
    // firing) — a 200 OK whose body times out should not fail the job.
    // The conversion `f` is deterministic local work; not retried.
    let rec attempt n delayMs =
        async {
            let! resp = fetchWithRetry http url ct
            match resp with
            | Error msg -> return Error msg
            | Ok response ->
                use response = response
                let! dl = downloadToFile response tempZipPath ct
                match dl with
                | Error msg ->
                    // 404s already short-circuit inside fetchWithRetry; here
                    // we're seeing post-200 failures (stall or network drop).
                    // Retry up to maxRetries with backoff.
                    if n >= maxRetries then return Error msg
                    else
                        let jitter = Random.Shared.Next(50, 250)
                        do! Async.Sleep(delayMs + jitter)
                        return! attempt (n + 1) (delayMs * 2)
                | Ok () ->
                    // Body fully on disk. Run the conversion; success or
                    // failure, drop the .zip.tmp afterward.
                    try
                        let! result = f tempZipPath
                        return Ok result
                    finally
                        try File.Delete tempZipPath with _ -> ()
        }
    attempt 0 1000

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
                        // Synchronous DuckDB + extraction work on a thread-pool
                        // worker. Two-step path: (1) extract the inner CSV to a
                        // sibling temp file, (2) hand the path to DuckDB's
                        // read_csv. DuckDB can't read inside a .zip directly
                        // (verified 2026-05-12 — no native zip filesystem),
                        // and feeding it a managed Stream isn't supported by
                        // DuckDB.NET. The extracted CSV is deleted in the
                        // finally regardless of outcome.
                        Tasks.Task.Run(fun () ->
                            let csvTemp = zipPath + ".csv"
                            try
                                try
                                    extractCsvToFile zipPath csvTemp
                                    let n = writeSingleDayParquet csvTemp outPath
                                    let fi = FileInfo outPath
                                    Ok (n, fi.Length)
                                with ex ->
                                    try File.Delete (outPath + ".tmp") with _ -> ()
                                    Error (sprintf "convert: %s" ex.Message)
                            finally
                                try File.Delete csvTemp with _ -> ())
                        |> Async.AwaitTask)
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
                        Tasks.Task.Run(fun () ->
                            let csvTemp = zipPath + ".csv"
                            let partWork =
                                Path.Combine(symbolDir,
                                    sprintf ".part-%04d-%02d" m.Year m.Month)
                            try
                                try
                                    extractCsvToFile zipPath csvTemp
                                    let pathOf (date: DateTime) = dailyOutputPath outputDir m.Symbol date
                                    let totalRows, daysWritten =
                                        writeMonthlyParquets csvTemp pathOf partWork
                                    Ok (totalRows, daysWritten)
                                with ex ->
                                    Error (sprintf "convert: %s" ex.Message)
                            finally
                                try File.Delete csvTemp with _ -> ()
                                try
                                    if Directory.Exists partWork then
                                        Directory.Delete(partWork, recursive = true)
                                with _ -> ())
                        |> Async.AwaitTask)
                match result with
                | Error msg -> return Failed(job.Key, msg)
                | Ok (Error inner) -> return Failed(job.Key, inner)
                | Ok (Ok (totalRows, daysWritten)) ->
                    // Mark the monthly complete so future runs skip without
                    // re-downloading. We commit the sentinel even if
                    // daysWritten=0 (the all-days-already-on-disk redundancy
                    // case): the source archive has been processed end-to-end,
                    // and that's what the sentinel records.
                    writeMonthlySentinel outputDir m
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
        let patterns = [| "*.parquet.tmp"; "*.zip.tmp" |]
        for pat in patterns do
          for tmp in Directory.EnumerateFiles(outputDir, pat, SearchOption.AllDirectories) do
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
            // daysWritten=0 means every day in the source archive was already
            // on disk from a prior run — the redundant-download case. We still
            // report "OK" because the work succeeded and the sentinel is now
            // written; tag it so the log is honest about what happened.
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
