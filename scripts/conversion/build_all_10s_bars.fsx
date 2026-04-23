#r "../../TradingEdge.Orb/bin/Release/net10.0/TradingEdge.Orb.dll"
#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#r "nuget: Argu, 6.2.5"

// Walks every data/bulk/trades/{date}.parquet, applies the ORB live-system
// trade filter (see TradeLoader.fs:52-86 + :114), buckets the surviving
// trades into 10-second windows over the session, and writes
// data/bulk/intraday_10s/{date}.parquet with columns
//   (date: DATE, ticker: VARCHAR, bucket: INTEGER, volume: BIGINT, trade_count: BIGINT).
// Skips days that are already built.
//
// Filter:
//   * size > 0
//   * sip_timestamp - participant_timestamp <= 50 ms (when both nonzero)
//   * Conditions: if intersects opening/closing prints {17, 25, 19, 8} -> keep
//                 else if intersects exclude set {2, 7, 10, 13, 20, 21, 22,
//                                                 29, 32, 52, 53} -> drop
// Odd lot (37) and Form T (12) are intentionally kept.
//
// Safe to run alongside the trades downloader: any trades file that's still
// being written is simply not picked up this pass. Re-run after more
// downloads land.

open System
open System.IO
open System.Globalization
open Argu
open DuckDB.NET.Data
open TradingEdge.Orb

type CliArgs =
    | [<AltCommandLine("-s")>] Start_Date of string
    | [<AltCommandLine("-e")>] End_Date of string
    | [<AltCommandLine("-n")>] Limit of int
    | [<AltCommandLine("-j")>] Parallelism of int

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Start_Date _ -> "First date to build (yyyy-MM-dd, inclusive). Default: earliest available trades file."
            | End_Date _ -> "Last date to build (yyyy-MM-dd, inclusive). Default: latest available trades file."
            | Limit _ -> "Cap on the number of days built this run (applied after date filter). Default: no cap."
            | Parallelism _ -> "Number of days to process in parallel. Each inner DuckDB scan is multithreaded too — oversubscribing slows total throughput on /mnt/d. Default: 2."

let parser = ArgumentParser.Create<CliArgs>(programName = "build_all_10s_bars.fsx")
let cliArgs = fsi.CommandLineArgs |> Array.skip 1
let parsed =
    try parser.Parse(cliArgs, raiseOnUsage = true)
    with :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        exit 1

let startDateOpt = parsed.TryGetResult Start_Date
let endDateOpt = parsed.TryGetResult End_Date
let limitOpt = parsed.TryGetResult Limit
let parallelism = parsed.GetResult(Parallelism, defaultValue = 2)

let tradesDir = "data/bulk/trades"
let outDir = "data/bulk/intraday_10s"
Directory.CreateDirectory outDir |> ignore

let excludeSetSql = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53]::UTINYINT[]"
let openCloseSetSql = "[17, 25, 19, 8]::UTINYINT[]"

// Session window: bucket N starts at `sessionStart + N*bucketDuration`. We
// keep buckets whose start is in [sessionStart, sessionEnd) — matching
// session_daily_totals' convention of excluding the closing auction minute,
// since the 16:00 auction prints sometimes leak into the 15:59 minute via
// SIP ordering on Polygon's feed, which distorts downstream RVOLs.
let bucketDuration = TimeSpan.FromSeconds 10.0
let bucketNs = int64 bucketDuration.TotalNanoseconds
let sessionStart = TimeSpan(8, 30, 0)
let regularEnd = TimeSpan(15, 59, 0)
let earlyEnd = TimeSpan(12, 59, 0)
let maxSipDeltaNs = int64 (TimeSpan.FromMilliseconds 50.0).TotalNanoseconds

let maxBucketFor (close: TimeSpan) =
    int ((close - sessionStart - bucketDuration).TotalSeconds / bucketDuration.TotalSeconds)

let buildOne (date: string) : double =
    let inPath = Path.Combine(tradesDir, $"{date}.parquet")
    let outPath = Path.Combine(outDir, $"{date}.parquet")

    let dateOnly = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture)
    let isEarly = Timezone.early_closes.Contains dateOnly
    let maxBucket = maxBucketFor (if isEarly then earlyEnd else regularEnd)

    let baseUtc =
        Timezone.baseTimeFromDateString(date).AddHours(Timezone.startHoursFromBase)
    let baseNs = (baseUtc - DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks * 100L
    let endNsExclusive = baseNs + int64 (maxBucket + 1) * bucketNs

    let inEscaped = inPath.Replace("'", "''")
    let outEscaped = outPath.Replace("'", "''")

    // The live ORB system buckets by sip_timestamp (the consolidated-tape
    // publish time — what the system actually sees), falling back to
    // participant_timestamp only when sip_timestamp is zero/unset. See
    // TradingEdge.Orb.TradeLoader.RawTrade.Timestamp at
    // TradingEdge.Orb/TradeLoader.fs:28. We mirror that here so offline
    // calibration matches live behavior.
    let sql =
        $"""
COPY (
    WITH filtered AS (
        SELECT
            ticker,
            COALESCE(NULLIF(sip_timestamp, 0), participant_timestamp) AS ts,
            size
        FROM read_parquet('{inEscaped}')
        WHERE size > 0
          AND (
              sip_timestamp = 0
              OR participant_timestamp = 0
              OR (sip_timestamp - participant_timestamp) <= {maxSipDeltaNs}
          )
          AND (
              list_has_any(conditions, {openCloseSetSql})
              OR NOT list_has_any(conditions, {excludeSetSql})
          )
    ),
    bucketed AS (
        SELECT
            ticker,
            CAST(FLOOR((ts - {baseNs})::DOUBLE / {bucketNs}) AS INTEGER) AS bucket,
            size
        FROM filtered
        WHERE ts >= {baseNs}
          AND ts <  {endNsExclusive}
    )
    SELECT
        DATE '{date}'     AS date,
        ticker,
        bucket,
        SUM(size)::BIGINT AS volume,
        COUNT(*)::BIGINT  AS trade_count
    FROM bucketed
    WHERE bucket >= 0 AND bucket <= {maxBucket}
    GROUP BY ticker, bucket
    ORDER BY ticker, bucket
) TO '{outEscaped}' (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3)
"""

    let sw = Diagnostics.Stopwatch.StartNew()
    use conn = new DuckDBConnection("DataSource=:memory:")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    cmd.CommandTimeout <- 0
    cmd.ExecuteNonQuery() |> ignore
    sw.Stop()
    sw.Elapsed.TotalSeconds

let availableDates =
    Directory.GetFiles(tradesDir, "*.parquet")
    |> Array.map Path.GetFileNameWithoutExtension
    |> Array.sort

let alreadyDone =
    Directory.GetFiles(outDir, "*.parquet")
    |> Array.map Path.GetFileNameWithoutExtension
    |> Set.ofArray

let inRange (d: string) =
    (match startDateOpt with Some s -> d >= s | None -> true)
    && (match endDateOpt with Some e -> d <= e | None -> true)

let todo =
    availableDates
    |> Array.filter (fun d -> not (alreadyDone.Contains d))
    |> Array.filter inRange
    |> fun arr ->
        match limitOpt with
        | Some n -> arr |> Array.truncate n
        | None -> arr

printfn "trades parquets available: %d" availableDates.Length
printfn "already built:             %d" alreadyDone.Count
printfn "to process this run:       %d" todo.Length

if todo.Length = 0 then
    printfn "Nothing to do."
else
    printfn "parallelism:               %d" parallelism
    let outerSw = Diagnostics.Stopwatch.StartNew()
    let mutable totalSeconds = 0.0
    let mutable doneCount = 0
    let logLock = obj()
    let opts = System.Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = parallelism)
    System.Threading.Tasks.Parallel.ForEach(todo, opts, fun (date: string) ->
        try
            let elapsed = buildOne date
            let outSize = FileInfo(Path.Combine(outDir, $"{date}.parquet")).Length
            lock logLock (fun () ->
                doneCount <- doneCount + 1
                totalSeconds <- totalSeconds + elapsed
                printfn "[%d/%d] %s  %.1fs  out=%.1f MB"
                    doneCount todo.Length date elapsed (float outSize / 1e6))
        with ex ->
            lock logLock (fun () ->
                doneCount <- doneCount + 1
                printfn "[%d/%d] %s  FAILED: %s" doneCount todo.Length date ex.Message)
    ) |> ignore
    outerSw.Stop()
    printfn ""
    printfn "Processed %d days in %.1fs wall (%.1fs CPU, avg %.2fs/day CPU)"
        todo.Length outerSw.Elapsed.TotalSeconds totalSeconds (totalSeconds / float todo.Length)
