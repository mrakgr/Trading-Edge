module TradingEdge.Orb.Bars

// =============================================================================
// The 1-SECOND BAR CONVENTION — single source of truth for SQL and F#
// =============================================================================
//
// ⚠⚠ THIS IS NOT `TradeFilters.whereClauseSql`. The two filters DIFFER, and
// importing the wrong one silently changes every bar:
//
//   TradeFilters (the ORB / minute-bar lineage)   Bars (this module, the 1s corpus)
//   ------------------------------------------   ---------------------------------
//   trf_id = 0   (LIT ONLY — TRFs dropped)        no trf_id filter — TRFs KEPT
//   no SIP-delta cap                              sip - participant <= 50 ms
//   open/close override the EXCLUDE SET only      open/close override the cap TOO
//   no price guard                                price > 0
//
// The 1s convention is DECISION 2 (all venues) + DECISION 7 (auction prints are
// unconditional). It is the convention `data/intraday_1s_slim/` was built with,
// and therefore the one every 1s study and the live scanner must use.
//
// Extracted 2026-08-18 from scripts/conversion/build_all_1s_bars.fsx so that the
// bulk SQL builder and the streaming (live) builder cannot drift apart. The SQL
// strings here are assembled from TradeFilters' canonical condition sets, so the
// condition codes still have exactly one definition in the codebase.

open System

// -----------------------------------------------------------------------------
// Constants
// -----------------------------------------------------------------------------

/// Nanoseconds per 1s bucket.
[<Literal>]
let BucketNs = 1_000_000_000L

/// Max sip_timestamp - participant_timestamp for a non-auction print (50 ms).
/// Drops the seconds-late off-tape prints that would forward-smear under the
/// SIP clock. Auction crosses are EXEMPT (they disseminate late by design;
/// measured open/close delta p99 ~388 ms).
[<Literal>]
let MaxSipDeltaNs = 50_000_000L

/// Bucket 0 is 00:00 ET (DECISION 9), NOT 08:30 — do not use
/// `Timezone.startHoursFromBase` (8.5), which belongs to the 10s builder.
/// RTH open 09:30 = 34200, 09:45 = 35100, 16:00 = 57600.
[<Literal>]
let StartHoursFromBase = 0.0

/// Session runs to midnight ET (post-market included, 2026-08-12).
/// Early closes use the same bound, so the distinction is currently moot.
[<Literal>]
let SessionEndHours = 24

/// Highest valid bucket index for a session ending `SessionEndHours` after
/// 00:00 ET. 24h => 86399.
let maxBucket = SessionEndHours * 3600 - 1

// -----------------------------------------------------------------------------
// SQL — DuckDB, against the bulk-trades parquet schema
// -----------------------------------------------------------------------------

/// `[17, 25, 19, 8]::UTINYINT[]` — opening/closing auction prints.
let openCloseSetSql = TradeFilters.openCloseSetSql

/// `[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53]::UTINYINT[]`
let excludeSetSql = TradeFilters.excludeSetSql

/// The bucketing clock (DECISION 1): SIP publish time, falling back to the
/// venue clock only when SIP is 0. Live-parity — the live feed comes via a SIP.
let tsExprSql = "COALESCE(NULLIF(sip_timestamp, 0), participant_timestamp)"

/// DECISION 7 — auction prints bypass BOTH the delta cap and the exclude set.
let conditionsAndDeltaSql =
    sprintf
        "(
              list_has_any(conditions, %s)
              OR (
                  ( sip_timestamp = 0
                    OR participant_timestamp = 0
                    OR (sip_timestamp - participant_timestamp) <= %d )
                  AND NOT list_has_any(conditions, %s)
              )
          )"
        openCloseSetSql MaxSipDeltaNs excludeSetSql

/// Full row filter for the 1s corpus. NOTE: no trf_id clause — by design.
let whereClauseSql =
    sprintf "size > 0\n          AND price > 0\n          AND %s" conditionsAndDeltaSql

/// `bucket` expression given the UTC-nanosecond origin of 00:00 ET on the date.
let bucketExprSql (baseNs: int64) =
    sprintf "CAST(FLOOR((ts - %d)::DOUBLE / %d) AS INTEGER)" baseNs BucketNs

// -----------------------------------------------------------------------------
// Clock
// -----------------------------------------------------------------------------

/// UTC nanoseconds at 00:00 ET on `date` (yyyy-MM-dd). DST-correct.
/// ⚠ Never hardcode a UTC-4 offset — that shifts winter dates by an hour and
/// manufactures a phantom "05:00-21:00" session that looks like overnight trading.
let baseNsForDate (date: string) : int64 =
    let baseUtc = Timezone.baseTimeFromDateString(date).AddHours StartHoursFromBase
    int64 (baseUtc - DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalNanoseconds

/// Exclusive upper bound in UTC ns, matching `maxBucket`.
let endNsExclusive (baseNs: int64) = baseNs + int64 (maxBucket + 1) * BucketNs

/// Seconds since 00:00 ET. Mirrors `bucketExprSql` exactly.
let inline bucketOfNs (baseNs: int64) (ts: int64) : int =
    int (floor (float (ts - baseNs) / float BucketNs))

/// The bucketing clock, in F#. Mirrors `tsExprSql`.
let inline tsOf (sipTs: int64) (participantTs: int64) : int64 =
    if sipTs <> 0L then sipTs else participantTs

// -----------------------------------------------------------------------------
// F# row filter — mirrors whereClauseSql for the streaming path
// -----------------------------------------------------------------------------
// Condition codes are represented as a 64-bit mask: every code this feed emits
// is < 64 (the largest in either canonical set is 53). `maskOf` RAISES on an
// out-of-range code rather than silently dropping it, so a future feed change
// fails loudly instead of quietly altering the filter.

let private maskOfSet (s: int Set) =
    s |> Set.fold (fun acc c -> acc ||| (1UL <<< c)) 0UL

let excludeMask = maskOfSet TradeFilters.excludeConditions
let openCloseMask = maskOfSet TradeFilters.openingAndClosingPrintConditions

let maskOf (conditions: int[]) : uint64 =
    let mutable m = 0UL
    for c in conditions do
        if c < 0 || c > 63 then
            failwithf "Bars.maskOf: condition code %d is outside the 0..63 mask range" c
        m <- m ||| (1UL <<< c)
    m

/// True iff this trade belongs in a 1s bar. `mask` from `maskOf`.
let inline keepByMask (mask: uint64) (sipTs: int64) (participantTs: int64)
                      (price: float) (size: float) : bool =
    size > 0.0 && price > 0.0
    && ( mask &&& openCloseMask <> 0UL
         || ( ( sipTs = 0L || participantTs = 0L
                || sipTs - participantTs <= MaxSipDeltaNs )
              && mask &&& excludeMask = 0UL ) )

// -----------------------------------------------------------------------------
// Bar
// -----------------------------------------------------------------------------

/// One present second. `Vwap`/`Volume` are float32 BY CONTRACT — the corpus
/// stores them as parquet FLOAT and consumers cast up to double, so a bar
/// computed and kept in float64 will not match. Sums accumulate in float64;
/// only the RESULT narrows.
/// ⚠ Volume is float, never int: `size` is fractional and int truncation zeroes
/// ~2% of bars (those summing to <1 share) despite them holding real trades.
[<Struct>]
type Bar1s =
    { Bucket: int
      Vwap: float32
      Volume: float32
      TradeCount: int }

/// Incremental 1s bar builder for ONE ticker. Push kept trades in bucket order;
/// a completed bar is returned when the bucket advances. Call `Flush` at the end
/// of the tape.
///
/// ⚠ Absent seconds emit NO bar (present-bar semantics — every window downstream
/// counts present bars, so a spurious empty bar shifts every channel).
/// ⚠ This does NOT apply the engine-side `vwap > 0 AND volume > 0` filter; that
/// lives at the consumer boundary (FlushFader's SecEmitter), and the parquet
/// corpus contains such bars. Do not add it here or the corpus will not match.
/// ⭐ REORDER TOLERANCE IS ZERO — MEASURED, not assumed (2026-08-18). The SQL
/// builder is a GROUP BY and so is order-immune; this roll-on-advance version is
/// not, and would drop any trade arriving for an already-closed second. It never
/// happens on this tape:
///   * the day files are PERFECTLY sorted by (ticker, sip_timestamp): 0 order
///     violations in 27,840,603 rows (2016-08-08) and 104,529,025 (2025-06-16);
///   * `sequence_number` is NOT the sort key — it has 270 / 940 violations under
///     file order — and `participant_timestamp` is 10-14% non-monotonic (a TRF
///     print disseminated late carries an old venue clock). Neither may be used
///     as the bucketing clock;
///   * the COALESCE fallback to participant_timestamp NEVER fires: sip_timestamp
///     is non-zero and non-null on all 553M rows sampled across 2016-2026.
/// So ts == sip_timestamp, monotonic per ticker, and no buffer is required.
/// ⚠ STILL OPEN FOR THE LIVE FEED — a WebSocket is only as ordered as the wire.
/// Re-measure against the live Polygon stream before trusting k = 0 there.
type BarAccumulator() =
    let mutable bucket = -1
    let mutable pv = 0.0
    let mutable v = 0.0
    let mutable n = 0

    let emit () =
        { Bucket = bucket
          Vwap = float32 (pv / v)
          Volume = float32 v
          TradeCount = n }

    /// Push one KEPT trade. Returns the completed prior bar when the bucket advances.
    member _.Push(b: int, price: float, size: float) : Bar1s voption =
        if b = bucket then
            pv <- pv + price * size
            v <- v + size
            n <- n + 1
            ValueNone
        else
            let out = if n > 0 then ValueSome(emit ()) else ValueNone
            bucket <- b
            pv <- price * size
            v <- size
            n <- 1
            out

    /// The final partial bar, if any.
    member _.Flush() : Bar1s voption =
        if n > 0 then
            let out = ValueSome(emit ())
            n <- 0
            out
        else ValueNone
