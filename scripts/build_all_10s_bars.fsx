#r "../TradingEdge.Orb/bin/Release/net10.0/TradingEdge.Orb.dll"
#r "nuget: DuckDB.NET.Data.Full, 1.4.4"

// Batch runner for build_10s_bars.fsx logic. Walks every
// data/bulk/trades/{date}.parquet and produces the matching 10s bars file
// at data/bulk/intraday_10s/{date}.parquet, skipping days that are already
// done.
//
// Safe to run alongside the trades downloader: any trades file that's still
// being written (no .parquet sibling yet, or smaller than a few hundred MB)
// is simply not picked up this pass. Re-run after more downloads land.

open System
open System.IO
open System.Globalization
open DuckDB.NET.Data
open TradingEdge.Orb

let tradesDir = "data/bulk/trades"
let outDir = "data/bulk/intraday_10s"
Directory.CreateDirectory outDir |> ignore

let excludeSetSql = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53]::UTINYINT[]"
let openCloseSetSql = "[17, 25, 19, 8]::UTINYINT[]"

let buildOne (date: string) : double =
    let inPath = Path.Combine(tradesDir, $"{date}.parquet")
    let outPath = Path.Combine(outDir, $"{date}.parquet")

    let dateOnly = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture)
    let isEarly = Timezone.early_closes.Contains dateOnly
    let maxBucket = if isEarly then 1608 else 2748

    let baseUtc =
        Timezone.baseTimeFromDateString(date).AddHours(VolumeProfile.startHoursFromBase)
    let ticksFromUnixEpoch = baseUtc.Ticks - DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks
    let baseNs = ticksFromUnixEpoch * 100L

    let inEscaped = inPath.Replace("'", "''")
    let outEscaped = outPath.Replace("'", "''")

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
              list_has_any(conditions, {openCloseSetSql})
              OR NOT list_has_any(conditions, {excludeSetSql})
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

let todo = availableDates |> Array.filter (fun d -> not (alreadyDone.Contains d))

printfn "trades parquets available: %d" availableDates.Length
printfn "already built:             %d" alreadyDone.Count
printfn "to process this run:       %d" todo.Length

if todo.Length = 0 then
    printfn "Nothing to do."
else
    let outerSw = Diagnostics.Stopwatch.StartNew()
    let mutable totalSeconds = 0.0
    for i = 0 to todo.Length - 1 do
        let date = todo.[i]
        try
            let elapsed = buildOne date
            totalSeconds <- totalSeconds + elapsed
            let outSize = FileInfo(Path.Combine(outDir, $"{date}.parquet")).Length
            printfn "[%d/%d] %s  %.1fs  out=%.1f MB"
                (i + 1) todo.Length date elapsed (float outSize / 1e6)
        with ex ->
            printfn "[%d/%d] %s  FAILED: %s" (i + 1) todo.Length date ex.Message
    outerSw.Stop()
    printfn ""
    printfn "Processed %d days in %.1fs (avg %.2fs/day)"
        todo.Length totalSeconds (totalSeconds / float todo.Length)
