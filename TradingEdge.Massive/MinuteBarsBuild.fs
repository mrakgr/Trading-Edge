module TradingEdge.MinuteBarsBuild

open System
open System.Globalization
open System.IO
open System.Threading.Tasks
open DuckDB.NET.Data
open TradingEdge.Orb
open TradingEdge.Orb.TradeFilters

// =============================================================================
// Per-day 1m bar builder — pure SQL over bulk-trades parquets
// =============================================================================
//
// One COPY (SELECT ... GROUP BY ticker, bucket) TO PARQUET query per day.
// DuckDB pushes the lit-only WHERE clause into the parquet scan and only the
// surviving rows reach the GROUP BY, so memory is bounded and the bottleneck
// is the parquet scan itself.
//
// Buckets: 960 1-minute slots from 04:00 ET to 20:00 ET (uniform every day —
// no special-casing for early-close half-days; those simply produce empty
// buckets in the 13:00–16:00 ET range).
//
// Output: one zstd-compressed parquet per date under OutputDir, written atomic
// via `.tmp` + `File.Move`.

type BuildOptions = {
    /// Bulk-trades input root. Default: /mnt/d/trading-edge-bulk/trades
    InputDir: string
    /// Per-day output root on the SSD. Default: data/minute_bars_1m
    OutputDir: string
    /// yyyy-MM-dd inclusive. None = earliest available input.
    StartDate: string option
    /// yyyy-MM-dd inclusive. None = latest available input.
    EndDate: string option
    /// Concurrent days. DuckDB is itself multithreaded per query, so don't
    /// oversubscribe. Default 4.
    Parallelism: int
    /// Overwrite existing per-day outputs. Default false (resume-friendly).
    Force: bool
}

let defaultOptions () : BuildOptions =
    {
        InputDir = "/mnt/d/trading-edge-bulk/trades"
        OutputDir = "data/minute_bars_1m"
        StartDate = None
        EndDate = None
        Parallelism = 4
        Force = false
    }

let private unixEpochUtc = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)

/// UTC nanoseconds of 04:00 ET on the given date string. DST-correct.
///
/// We can't just call baseTimeFromDateString and AddHours(4.0): on DST
/// transition days (e.g. 2025-03-09 spring-forward, 2025-11-02 fall-back),
/// midnight ET and 04:00 ET fall on different sides of the UTC offset change,
/// so "midnight UTC + 4 hours" disagrees with "04:00 ET converted to UTC".
/// Instead, build the local 04:00 ET DateTime directly and convert.
let private baseNsForDate (date: string) : int64 =
    let d = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture)
    let local = DateTime(d, TimeOnly(4, 0, 0), DateTimeKind.Unspecified)
    let baseUtc = TimeZoneInfo.ConvertTimeToUtc(local, Timezone.easternTz)
    (baseUtc - unixEpochUtc).Ticks * 100L

let private nsPerMinute = 60_000_000_000L
let private numBuckets = 960  // 16 hours × 60 minutes

let private formatSize (bytes: int64) : string =
    let mb = float bytes / (1024.0 * 1024.0)
    sprintf "%.1f MB" mb

/// Build one day's 1m bars. Returns wallclock seconds.
let buildOne (inputDir: string) (outputDir: string) (date: string) : float =
    let inPath = Path.Combine(inputDir, sprintf "%s.parquet" date)
    let outPath = Path.Combine(outputDir, sprintf "%s.parquet" date)
    let tmpPath = outPath + ".tmp"

    if File.Exists tmpPath then File.Delete tmpPath
    Directory.CreateDirectory outputDir |> ignore

    let baseNs = baseNsForDate date
    let endNsExclusive = baseNs + int64 numBuckets * nsPerMinute

    let inEsc = inPath.Replace("'", "''")
    let outEsc = tmpPath.Replace("'", "''")

    let sql =
        sprintf """
COPY (
  WITH filtered AS (
    SELECT ticker,
           participant_timestamp AS ts,
           sequence_number       AS seq,
           price,
           size
    FROM read_parquet('%s')
    WHERE %s
      AND participant_timestamp >= %d
      AND participant_timestamp <  %d
  ),
  bucketed AS (
    SELECT ticker,
           CAST(((ts - %d) / %d) AS INTEGER) AS bucket,
           ts, seq, price, size
    FROM filtered
  )
  SELECT
    ticker,
    bucket,
    (%d + bucket::BIGINT * %d)                   AS start_ns,
    arg_min(price, [ts, seq])                    AS open,
    arg_max(price, [ts, seq])                    AS close,
    MAX(price)                                   AS high,
    MIN(price)                                   AS low,
    SUM(size)::BIGINT                            AS volume,
    SUM(price * size)                            AS dollar_volume,
    -- SUM(size) > 0 is guaranteed: the filter drops size <= 0, and GROUP BY
    -- only emits rows with COUNT(*) >= 1.
    SUM(price * size) / SUM(size)                AS vwap,
    -- Volume-weighted std in dollars:
    -- sqrt(max(0, E[p²·v]/E[v] − vwap²)). greatest() clamps FP cancellation
    -- to zero when all trades in a bar are at the same price.
    sqrt(greatest(0.0,
        SUM(price * price * size) / SUM(size)
        - (SUM(price * size) / SUM(size))
        * (SUM(price * size) / SUM(size))
    ))                                            AS vwstd,
    COUNT(*)::BIGINT                             AS trade_count
  FROM bucketed
  WHERE bucket >= 0 AND bucket < %d
  GROUP BY ticker, bucket
  ORDER BY ticker, bucket
) TO '%s' (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3)
"""
            inEsc whereClauseSql baseNs endNsExclusive
            baseNs nsPerMinute
            baseNs nsPerMinute
            numBuckets
            outEsc

    let sw = Diagnostics.Stopwatch.StartNew()
    use conn = new DuckDBConnection("DataSource=:memory:")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    cmd.CommandTimeout <- 0
    cmd.ExecuteNonQuery() |> ignore
    sw.Stop()

    if File.Exists outPath then File.Delete outPath
    File.Move(tmpPath, outPath)

    sw.Elapsed.TotalSeconds

/// Build all days in [StartDate, EndDate] that have input parquets.
/// Skips already-built outputs unless Force. Parallel over dates.
let buildAll (opts: BuildOptions) : unit =
    if not (Directory.Exists opts.InputDir) then
        failwithf "Input dir does not exist: %s" opts.InputDir
    Directory.CreateDirectory opts.OutputDir |> ignore

    let parseDate s = DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture)

    let availableDates =
        Directory.GetFiles(opts.InputDir, "*.parquet")
        |> Array.map Path.GetFileNameWithoutExtension
        |> Array.sort

    let inRange (d: string) : bool =
        let dd = parseDate d
        let okStart =
            match opts.StartDate with
            | Some s -> dd >= parseDate s
            | None -> true
        let okEnd =
            match opts.EndDate with
            | Some e -> dd <= parseDate e
            | None -> true
        okStart && okEnd

    let todoDates =
        availableDates
        |> Array.filter inRange
        |> Array.filter (fun d ->
            opts.Force
            || not (File.Exists (Path.Combine(opts.OutputDir, sprintf "%s.parquet" d))))

    printfn "Input  dir: %s" (Path.GetFullPath opts.InputDir)
    printfn "Output dir: %s" (Path.GetFullPath opts.OutputDir)
    printfn "Available days: %d, in range: %d, to build: %d (force=%b)"
        availableDates.Length
        (availableDates |> Array.filter inRange |> Array.length)
        todoDates.Length
        opts.Force
    printfn "Parallelism: %d" opts.Parallelism

    if todoDates.Length = 0 then
        printfn "Nothing to do."
    else

    let overall = Diagnostics.Stopwatch.StartNew()
    let nDates = todoDates.Length
    let mutable nDone = 0
    let logLock = obj()

    let popts = ParallelOptions(MaxDegreeOfParallelism = opts.Parallelism)
    Parallel.ForEach(todoDates, popts, fun date ->
        let elapsed = buildOne opts.InputDir opts.OutputDir date
        let outPath = Path.Combine(opts.OutputDir, sprintf "%s.parquet" date)
        let fi = FileInfo outPath
        lock logLock (fun () ->
            nDone <- nDone + 1
            let wall = overall.Elapsed.TotalSeconds
            let eta = wall * float (nDates - nDone) / float (max 1 nDone)
            printfn "[%d/%d] %s  %5.1fs  out=%s | wall=%.0fs eta=%.0fs"
                nDone nDates date elapsed (formatSize fi.Length) wall eta)
    ) |> ignore
    overall.Stop()

    printfn ""
    printfn "Done. Built %d days in %.1fs." nDates overall.Elapsed.TotalSeconds
