#r "../../TradingEdge.Orb/bin/Release/net10.0/TradingEdge.Orb.dll"
#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#r "nuget: Argu, 6.2.5"

// Walks every data/bulk/trades/{date}.parquet, applies the trade filter, buckets
// the surviving trades into 1-SECOND windows over the session, and writes
// data/intraday_1s_slim/{date}.parquet (on the SSD) with the SLIM 5-column schema
//   (ticker VARCHAR, bucket INT32, vwap FLOAT, volume FLOAT, trade_count INT32).
// ⭐ SLIM SCHEMA (2026-07-21, user decision after the SurgeRider vol bake-off
// F1-F7 in docs/surgerider_results.md): the locked vol driver is an EWMA of
// |slot-vwap log-returns| and the momentum engine's channels/fills are all
// vwap-based, so the old 11-col schema's extras are dead weight:
//   * open/high/low — unused (channels are vwap MaxMa/MinMa; F5 showed the OHLC
//     range-estimator family is bid-ask-bounce poison on this data anyway);
//   * close — dropped because SurgeRider fills at the NEXT present bar's vwap
//     (a more honest fill than the signal bar's own last print);
//   * vwstd/log_vwstd — the decomposed-vwstd vol measure lost the bake-off (F1);
//   * log_vwap — obsoleted before ever being built (F7 needs only slot vwaps).
// Measured ~62% smaller than the 11-col schema (2026-07-10: 401→154 MB;
// 2023-02-15: 243→81 MB) → the full 2020→2026 period fits in ~240 GB, less than
// the old 3.5-year set took, and backtests decompress ~2.6x less. The trades
// tape remains the source of truth if any dropped field is ever needed again.
// bucket is SECONDS since 00:00 ET (DECISION 9): RTH open 09:30 ET = bucket 34200,
// 10:00 ET = 36000; roll-up to minute = bucket/60, to 10s = bucket/10. 00:00-04:00
// is empty (no trades) so costs nothing; future-proof for extended/24-7 hours.
// NOTE: volume is FLOAT, not INT — the source `size` is DOUBLE with millions of
// fractional-share trades/day; int-truncation would zero out ~2% of bars (those
// summing to <1 share) despite them holding real trades. minute_aggs volume is
// DOUBLE for the same reason. trade_count stays INT32 (a true integer count).
// Skips days that are already built. See the approved plan
//   ~/.claude/plans/let-s-go-with-this-velvety-milner.md
// for the design rationale (timestamp/venue study, schema, sizing).
//
// Filter (DECISION 2 + 7 — ALL venues; open/close prints override everything):
//   * size > 0  AND  price > 0   (garbage guard; also keeps ln(vwap) safe downstream)
//   * KEEP if:  opening/closing print {17,25,19,8}          -- unconditional (D7)
//       OR ( sip-participant <= 50 ms (when both nonzero)   -- delta cap
//            AND NOT in exclude set {2,7,10,13,20,21,22,29,32,52,53} )  -- conditions
//   NOTE: NO trf_id filter — TRFs are included; the 50 ms cap drops the
//   seconds-late off-tape prints that would forward-smear under the sip clock.
//   Auction crosses disseminate late (open/close delta p99 ~388 ms) and are the
//   session's anchor prices, so DECISION 7 exempts them from the cap.
//
// Clock (DECISION 1): bucket by sip_timestamp (the consolidated-tape publish
// time — live-parity: the live feed comes through a SIP), falling back to
// participant_timestamp only when sip is 0.
//
// Schema (DECISIONS 3/4/5/6, slimmed 2026-07-21): no date column (it's the
// filename); FLOAT vwap/volume (compute in f64, cast to f32 on write); INT32
// trade_count/bucket. Written zstd level 9, ORDER BY ticker,bucket (measured
// ~21% smaller + clusters by ticker).
//
// Safe to run alongside the trades downloader: a file still being written is
// simply not picked up this pass. Re-run after more downloads land.
//
// -----------------------------------------------------------------------------
// PERFORMANCE — how to make this faster (proposal, NOT implemented 2026-07-22)
// -----------------------------------------------------------------------------
// The 2023→2026 set is already built and 2020-2022 is a one-off, so this was
// left as a note rather than code. If you ever re-run a large slice, this is the
// lever. Profiled on a heavy 3.0 GB day (2026-06-09, ~182M raw trades, 6 threads):
//
//   raw read+decompress (warm) ............... ~1.4-1.8s
//   + full condition filter (52M survive) .... ~12s wall  (~58s CPU / 6 threads)
//   + GROUP BY (ticker,bucket) + aggs ........ ~13s   (grouping/sort ≈ free)
//   + ORDER BY + zstd-9 write ................ ~13-14s (zstd-9 vs -3: same wall,
//                                                        17% smaller file)
//   SAME query, first COLD read off the HDD .. ~40-82s
//
// So per-day wall time is dominated by TWO things, in this order:
//   1. The COLD HDD read. data/bulk/trades lives on /mnt/d (spinning disk); `dd`
//      streams it at ~139 MB/s, so a cold 3 GB day is ~22s of pure read before
//      any compute. This is the true ceiling — nothing in the query touches it.
//   2. The two `list_has_any` condition filters over ~180M rows/day: ~58s of CPU
//      (≈12s wall across 6 threads). This is the bulk of the compute.
//
// Things that DO NOT help (measured/reasoned 2026-07-22):
//   * Reordering the input. The Polygon flat files are ALREADY sorted by
//     (ticker, sip_timestamp) — checked 2026-06-09: 12,453 ticker transitions,
//     zero out of order. The GROUP BY and ORDER BY are already cheap on
//     pre-clustered data. Adding an ORDER BY to the downloader is pure waste.
//   * Copy-first (cp the day HDD->SSD, then query the SSD copy). Measured:
//     19s copy + 12s SSD query = ~31s, vs ~22s to just read cold in place. The
//     copy does not remove the slow HDD read — it IS that read, into a temp file,
//     after which DuckDB reads a SECOND time from SSD. You pay the ~22s platter
//     read either way; copy-first only adds a redundant SSD write + reread. It
//     wins ONLY if the same file is queried more than once, which the builder
//     never does.
//   * Staging the whole trades set on SSD. The 2020-2022 slice alone is 775 GB
//     across 755 days; SSD headroom was ~489 GB. Doesn't fit. (Full archive 2.1 TB.)
//
// The ONE real win — a 2-stage OVERLAPPED read-ahead pipeline (hide the HDD read
// behind the compute). Current loop is serial: read day N off the HDD, THEN
// filter/group/write it, THEN move to N+1 — so the HDD sits idle during the ~12s
// of CPU work, and the CPU sits idle during the ~22s cold read. Overlap them:
//   * Stage 1 (copier task): `cp` day N+1 from the HDD to an SSD scratch file
//     while stage 2 is still crunching day N.
//   * Stage 2 (builder, the existing per-day loop): read from the SSD scratch
//     file, run the query, write the slim output, delete the scratch file.
//   * One bounded System.Threading.Channels channel between them (capacity 1-2 so
//     the copier stays at most a day or two ahead and SSD scratch never balloons).
//   This is the exact PerpsDownload.fs shape (manifest -> download workers ->
//   unbounded channel -> convert workers -> reporter) that CLAUDE.md points to;
//   here it collapses to copier -> bounded channel -> builder. Expected per-day
//   wall drops from serial ~34s toward max(read ~22s, compute ~12s) ≈ ~22s — i.e.
//   HDD-bound, the compute hidden entirely. ~35% off a full re-run.
//   Bonus lever if still HDD-bound after that: widen the copier to 2-3 parallel
//   `cp`s (WSL2 /mnt/d random-read is poor; a couple of concurrent sequential
//   reads can beat one) and raise the channel capacity to match.
//
// Not worth chasing: the condition-filter CPU is already ~12s wall on 6 threads;
// bumping SET threads only helps if the box has spare cores AND the read is no
// longer the gate (i.e. only after the pipeline above). zstd-9 is already free on
// wall time, so don't drop to a lower level — you'd only lose the 17% size win.
// -----------------------------------------------------------------------------

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

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Start_Date _ -> "First date to build (yyyy-MM-dd, inclusive). Default: earliest available trades file."
            | End_Date _ -> "Last date to build (yyyy-MM-dd, inclusive). Default: latest available trades file."
            | Limit _ -> "Cap on the number of days built this run (applied after date filter). Default: no cap."

let parser = ArgumentParser.Create<CliArgs>(programName = "build_all_1s_bars.fsx")
let cliArgs = fsi.CommandLineArgs |> Array.skip 1
let parsed =
    try parser.Parse(cliArgs, raiseOnUsage = true)
    with :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        exit 1

let startDateOpt = parsed.TryGetResult Start_Date
let endDateOpt = parsed.TryGetResult End_Date
let limitOpt = parsed.TryGetResult Limit

let tradesDir = "data/bulk/trades"       // HDD source (symlink -> /mnt/d)
let outDir = "data/intraday_1s_slim"     // SSD (repo root /dev/sde) — NOT data/bulk (that's the HDD)
Directory.CreateDirectory outDir |> ignore

// SSD spill dir for the per-day sort (keep any DuckDB spill off the HDD).
let spillDir = Path.Combine(outDir, ".duckdb_tmp")
Directory.CreateDirectory spillDir |> ignore

// Reuse the two canonical condition sets from TradeFilters. We do NOT use
// whereClauseSql (bakes in trf_id = 0, DECISION 2 drops it) NOR conditionsSqlClause
// (it folds open/close into the conditions only) — DECISION 7 needs open/close to
// override BOTH the exclude set AND the 50 ms delta cap, so we assemble the
// predicate from the raw sets.
let openCloseSetSql = TradeFilters.openCloseSetSql   // [17,25,19,8] — opening/closing auction prints
let excludeSetSql = TradeFilters.excludeSetSql       // [2,7,10,13,20,21,22,29,32,52,53]

// Session window: bucket N starts at sessionStart + N*bucketDuration; keep
// buckets in [sessionStart, sessionEnd) — excludes the closing auction minute
// (16:00 prints leak into 15:59 via SIP ordering and distort downstream RVOLs).
//
// ⭐ Bucket 0 = 00:00 ET (midnight Eastern), NOT 08:30 ET (user, DECISION 9).
// The 10s builder starts at 08:30 for simplicity/perf; here we start at midnight
// so 00:00-04:00 (empty for now — no trades → no bars) is future-proof for
// extended/24-7 hours. RTH open = bucket 34,200 (9.5h * 3600). We therefore use a
// LOCAL startHoursFromBase = 0.0 rather than Timezone.startHoursFromBase (8.5),
// which is shared with the 10s builder and must not change.
let startHoursFromBase = 0.0
let bucketDuration = TimeSpan.FromSeconds 1.0
let bucketNs = int64 bucketDuration.TotalNanoseconds       // 1_000_000_000
let sessionStart = TimeSpan(0, 0, 0)                       // 00:00 ET
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
        Timezone.baseTimeFromDateString(date).AddHours(startHoursFromBase)
    // TimeSpan.TotalNanoseconds (.NET 8+) — exact for our range (verified 0 error
    // vs Ticks*100 across 2003-2028; the values are 100 ns multiples so they land
    // on representable doubles). int64 cast: TotalNanoseconds is a double.
    let baseNs = int64 (baseUtc - DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalNanoseconds
    let endNsExclusive = baseNs + int64 (maxBucket + 1) * bucketNs

    let inEscaped = inPath.Replace("'", "''")
    let outEscaped = outPath.Replace("'", "''")
    let spillEscaped = spillDir.Replace("'", "''")

    // All sums accumulate in DOUBLE (DuckDB default); only the final per-bar
    // RESULT is cast to FLOAT. volume is sum(size)::FLOAT — size is fractional
    // DOUBLE, so int would truncate sub-share bars to a spurious 0 (Stage 0
    // found ~2% of bars affected).
    let sql =
        $"""
SET memory_limit='8GB';
SET threads=6;
SET preserve_insertion_order=false;
SET temp_directory='{spillEscaped}';
COPY (
    WITH filtered AS (
        SELECT
            ticker,
            COALESCE(NULLIF(sip_timestamp, 0), participant_timestamp) AS ts,
            price,
            size
        FROM read_parquet('{inEscaped}')
        WHERE size > 0
          AND price > 0
          AND (
              -- DECISION 7: opening/closing auction prints are kept UNCONDITIONALLY
              -- (bypass both the 50 ms delta cap and the exclude-conditions test) —
              -- auction crosses disseminate late (p99 ~388 ms) and are anchor prices.
              list_has_any(conditions, {openCloseSetSql})
              OR (
                  ( sip_timestamp = 0
                    OR participant_timestamp = 0
                    OR (sip_timestamp - participant_timestamp) <= {maxSipDeltaNs} )
                  AND NOT list_has_any(conditions, {excludeSetSql})
              )
          )
    ),
    bucketed AS (
        SELECT
            ticker,
            CAST(FLOOR((ts - {baseNs})::DOUBLE / {bucketNs}) AS INTEGER) AS bucket,
            ts,
            price,
            size
        FROM filtered
        WHERE ts >= {baseNs}
          AND ts <  {endNsExclusive}
    )
    SELECT
        ticker,
        bucket,
        (sum(price * size) / sum(size))::FLOAT AS vwap,
        sum(size)::FLOAT          AS volume,
        count(*)::INTEGER         AS trade_count
    FROM bucketed
    WHERE bucket >= 0 AND bucket <= {maxBucket}
    GROUP BY ticker, bucket
    ORDER BY ticker, bucket
) TO '{outEscaped}' (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 9)
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
