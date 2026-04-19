#r "../TradingEdge.Orb/bin/Release/net10.0/TradingEdge.Orb.dll"
#r "nuget: DuckDB.NET.Data.Full, 1.4.4"

// Reads a single day's bulk trades Parquet at data/bulk/trades/{date}.parquet,
// applies the same filter as the ORB live system (TradeLoader.fs), buckets
// the surviving trades into 10-second windows over the session, and writes
// data/bulk/intraday_10s/{date}.parquet with columns
//   (ticker: VARCHAR, bucket: INTEGER, volume: BIGINT, trade_count: BIGINT).
//
// Bucket 0 = 08:30 ET on {date}. Last bucket of the session is excluded to
// drop the closing auction print:
//   * Regular-close days: buckets 0..2693 (08:30:00–15:57:50 start),
//     omitting 15:58:00–16:00:00 (buckets 2694–2699).
//     Actually for consistency with session_daily_totals we treat buckets
//     that START in [08:30, 15:59) as in-session. 15:58-bucket starts at
//     15:58:00 = 27480 seconds = bucket 2748. Using 10s buckets: buckets
//     [0, 2748] inclusive means 08:30:00 through 15:58:00 — matches the
//     minute-agg semantics of "exclude the 15:59 minute."
//   * Early-close days: buckets [0, 1608] (08:30:00 through 12:58:00).
//
// Filter (identical to TradeLoader.fs:52-86 + :114):
//   * size > 0
//   * sip_timestamp - participant_timestamp <= 50 ms (when both nonzero)
//   * Conditions: if intersects opening/closing prints {17, 25, 19, 8} -> keep
//                 else if intersects exclude set {2, 7, 10, 13, 20, 21, 22,
//                                                 29, 32, 52, 53} -> drop
// Odd lot (37) and Form T (12) are intentionally kept.

open System
open System.IO
open System.Globalization
open DuckDB.NET.Data
open TradingEdge.Orb

let scriptArgs =
    Environment.GetCommandLineArgs()
    |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx"))
    |> Array.skip 1

let date =
    match scriptArgs with
    | [| d |] -> d
    | _ -> failwith "Usage: dotnet fsi build_10s_bars.fsx <yyyy-MM-dd>"

let inPath = $"data/bulk/trades/{date}.parquet"
let outPath = $"data/bulk/intraday_10s/{date}.parquet"

if not (File.Exists inPath) then
    failwithf "Input trades file not found: %s" inPath

Directory.CreateDirectory "data/bulk/intraday_10s" |> ignore

let dateOnly = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture)
let isEarly = Timezone.early_closes.Contains dateOnly

// Session window (bucket indices, inclusive). 10s bars from 08:30 ET.
// 08:30 -> bucket 0. 15:58 -> bucket 2748. 12:58 -> bucket 1608.
let maxBucket = if isEarly then 1608 else 2748

// 08:30 ET on this date, as a UTC nanosecond offset from epoch.
let baseUtc =
    Timezone.baseTimeFromDateString(date).AddHours(VolumeProfile.startHoursFromBase)
let ticksFromUnixEpoch = baseUtc.Ticks - DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks
let baseNs = ticksFromUnixEpoch * 100L

printfn "Building 10s bars for %s (%s)" date (if isEarly then "early-close" else "regular")
printfn "  baseNs (08:30 ET): %d" baseNs
printfn "  bucket range: [0, %d]" maxBucket

let inEscaped = inPath.Replace("'", "''")
let outEscaped = outPath.Replace("'", "''")

// Filter in SQL. `conditions` is already parsed as UTINYINT[] by the trades
// downloader (see TradingEdge.Massive/S3Download.fs:convertTradesCsvGzToParquet).
// Cast the literal sets to UTINYINT[] so list_has_any type-matches cleanly.
let excludeSet = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53]::UTINYINT[]"
let openCloseSet = "[17, 25, 19, 8]::UTINYINT[]"

let sql =
    $"""
COPY (
    WITH filtered AS (
        SELECT
            ticker,
            participant_timestamp,
            size
        FROM read_parquet('{inEscaped}')
        WHERE size > 0
          AND (
              sip_timestamp = 0
              OR participant_timestamp = 0
              OR (sip_timestamp - participant_timestamp) <= 50000000
          )
          AND (
              list_has_any(conditions, {openCloseSet})
              OR NOT list_has_any(conditions, {excludeSet})
          )
    ),
    bucketed AS (
        SELECT
            ticker,
            CAST((participant_timestamp - {baseNs}) / 10000000000 AS INTEGER) AS bucket,
            size
        FROM filtered
        WHERE participant_timestamp >= {baseNs}
          AND participant_timestamp < {baseNs} + ({maxBucket} + 1) * 10000000000
    )
    SELECT
        DATE '{date}'          AS date,
        ticker,
        bucket,
        SUM(size)::BIGINT      AS volume,
        COUNT(*)::BIGINT       AS trade_count
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

let inSize = FileInfo(inPath).Length
let outSize = FileInfo(outPath).Length
printfn "  done in %.1fs  input=%.2f GB  output=%.2f MB" sw.Elapsed.TotalSeconds (float inSize / 1e9) (float outSize / 1e6)
