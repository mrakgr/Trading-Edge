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
//   counts the 15/16 open/close MARKERS           drops them, auction code or not (2026-10-24)
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

/// `[15, 16]::UTINYINT[]` — the official open / close MARKERS (TradeFilters.markerConditions): not trades, dropped first
let markerSetSql = TradeFilters.markerSetSql

/// The bucketing clock (DECISION 1): SIP publish time, falling back to the
/// venue clock only when SIP is 0. Live-parity — the live feed comes via a SIP.
let tsExprSql = "COALESCE(NULLIF(sip_timestamp, 0), participant_timestamp)"

/// ⭐⭐ TIMESTAMP PRECISION OF THE ROW FILTER.
///
/// The bulk parquet carries NANOSECOND sip/participant timestamps. The LIVE
/// Massive WebSocket carries MILLISECONDS (measured 2026-08-21: `t` is 13
/// digits). Bucketing is unaffected either way — floor(ns/1e9) = floor(ms/1e3)
/// identically — but the delta cap compares a DIFFERENCE, so truncating both
/// sides to ms widens `<= 50ms` to an effective `<= 51ms`.
///
/// Measured on 2026-06-09: 38,360 of 181,709,268 trades are admitted that the ns
/// rule rejects and ZERO the other way, every one with a true delta in
/// (50, 51] ms. The direction is structural, not incidental: only a trade whose
/// true delta lies just ABOVE the cap can survive truncation.
///
/// ⭐ Building the corpus at `Millisecond` makes backtest and live agree BY
/// CONSTRUCTION rather than by repeated measurement — which is the same argument
/// that motivated forking the engine and sharing the bar library.
type TimestampPrecision =
    /// The tape's native resolution. What the existing corpus was built at.
    | Nanosecond
    /// The live feed's resolution, imposed on the tape.
    | Millisecond

/// ⚠⚠ `//`, NOT `/`. In DuckDB `/` on integers is FLOAT division, so
/// `(sip_timestamp / 1000000) * 1000000` round-trips a 19-digit integer through
/// a double — which cannot hold it — and comes back approximately unchanged.
/// The first version of this shipped with `/` and produced a corpus that was the
/// NANOSECOND one plus float noise: identical bar count, identical ticker count,
/// and exactly 3 corrupted bars out of 31,127,627. A no-op is what near-identity
/// looks like; the giveaway was that the trade count moved the WRONG WAY
/// (ms must admit MORE trades, never fewer).
let private tsAtSql (p: TimestampPrecision) (col: string) =
    match p with
    | Nanosecond -> col
    | Millisecond -> sprintf "((%s // 1000000) * 1000000)" col

/// DECISION 7 — auction prints bypass BOTH the delta cap and the exclude set.
/// ⭐ (2026-10-24) An official open / close MARKER (15, 16) is not a trade: it never enters a bar, auction code or not.
let conditionsAndDeltaSqlAt (p: TimestampPrecision) =
    let sip = tsAtSql p "sip_timestamp"
    let participant = tsAtSql p "participant_timestamp"
    $"""(
              NOT list_has_any(conditions, {markerSetSql})
              AND (
                  list_has_any(conditions, {openCloseSetSql})
                  OR (
                      ( sip_timestamp = 0
                        OR participant_timestamp = 0
                        OR ({sip} - {participant}) <= {MaxSipDeltaNs} )
                      AND NOT list_has_any(conditions, {excludeSetSql})
                  )
              )
          )"""

let conditionsAndDeltaSql = conditionsAndDeltaSqlAt Nanosecond

// -----------------------------------------------------------------------------
// ⭐ THE SHARE COLUMNS (2026-11-18, user): total_volume / retail_volume / mid_volume
// -----------------------------------------------------------------------------
// The FlushFader retail-share gate (docs/buildup_results.md §6) needs, per bar, the volume of ALL prints — delayed
// ones too — and of the BJZZ retail prints among them. A print COUNTS toward the share columns iff
//   size > 0 AND price > 0 AND no marker (15, 16) AND no auction code (17, 25, 19, 8) AND no excluded code
// — i.e. the bar rule WITHOUT the 50 ms delay cap, and WITHOUT the auction override (an auction print says nothing
// about who trades: the user, 2026-11-18). Among the counted prints, an off-exchange one (FINRA TRF / ADF = Massive
// exchange 4) is classified by its SUB-TICK FRACTION f — the price's position inside its minimum tick ($0.01 at
// >= $1, $0.0001 below: Reg NMS Rule 612 is per price, so the tick changes the moment the price crosses $1):
//   f in (0, 0.4) or (0.6, 1)  RETAIL (BJZZ: a wholesaler's price-improved fill; Boehmer, Jones, Zhang & Zhang 2021)
//   f in [0.4, 0.6]            MID (a midpoint cross — mostly dark-pool institutional; recorded for testing only)
//   f = 0                      neither (an on-tick print)
// f is computed EXACTLY in integer micro-dollars (the tape's prices carry <= 6 decimals: measured 2026-11-18).
// ⚠ Below $1 the classification is UNVALIDATED (17.6 % of sub-$1 TRF prints are finer than $0.0001 vs 0.9 % of lit
// ones on 2025-06-02 — the same signature, one level down — but no study has used it). FlushFader trades >= $1 only.
// ⚠ A bar holds only seconds with an IN-BAR print. The share volume of a second without one is CARRIED to the ticker's
// next bar (the user's rule): a delayed print never creates a bar, so every bar-count window keeps its meaning. Share
// volume after the day's last bar is dropped.

/// The FINRA TRF / ADF venue id on the Massive tape and feed.
[<Literal>]
let TrfExchange = 4

/// A print in the bar (vwap / volume / trade_count): the 1s bar rule.
[<Literal>]
let FlagInBar = 1uy
/// A print in total_volume (the share rule above).
[<Literal>]
let FlagCounts = 2uy
/// A counted off-exchange print with a retail (BJZZ) fraction.
[<Literal>]
let FlagRetail = 4uy
/// A counted off-exchange print with a midpoint fraction.
[<Literal>]
let FlagMid = 8uy

/// The share rule's conditions, in SQL (size / price guarded by the row filter).
let countsSql =
    $"(NOT list_has_any(conditions, {markerSetSql}) AND NOT list_has_any(conditions, {openCloseSetSql}) AND NOT list_has_any(conditions, {excludeSetSql}))"

/// The sub-tick fraction, in SQL. Mirrors `subTickFrac`.
let subTickFracSql =
    "(CASE WHEN price >= 1 THEN (CAST(round(price * 1e6) AS BIGINT) % 10000) / 10000.0 ELSE (CAST(round(price * 1e6) AS BIGINT) % 100) / 100.0 END)"

/// A counted print's class, in SQL: 'R' retail, 'M' mid, NULL neither. Mirrors `shareFlags`.
let shareClassSql =
    $"(CASE WHEN exchange <> {TrfExchange} THEN NULL
            WHEN {subTickFracSql} >= 0.4 AND {subTickFracSql} <= 0.6 THEN 'M'
            WHEN {subTickFracSql} > 0 THEN 'R' END)"

// -----------------------------------------------------------------------------
// Row filter
// -----------------------------------------------------------------------------

/// Full row filter for the 1s corpus. NOTE: no trf_id clause — by design.
let whereClauseSqlAt (p: TimestampPrecision) =
    sprintf "size > 0\n          AND price > 0\n          AND %s" (conditionsAndDeltaSqlAt p)

/// The historical default. ⚠ Unchanged, so every existing caller keeps the
/// nanosecond corpus it was built and audited against.
let whereClauseSql = whereClauseSqlAt Nanosecond

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
let markerMask = maskOfSet TradeFilters.markerConditions

let maskOf (conditions: int[]) : uint64 =
    let mutable m = 0UL
    for c in conditions do
        if c < 0 || c > 63 then
            failwithf "Bars.maskOf: condition code %d is outside the 0..63 mask range" c
        m <- m ||| (1UL <<< c)
    m

/// `maskOf` for a DuckDB `UTINYINT[]` column, which DuckDB.NET materialises as
/// `List<byte>`. Indexed rather than enumerated so no enumerator is allocated —
/// this runs ~10^8 times per trading day.
/// ⚠ Guards the 0..63 range. .NET MASKS the shift count (`1UL <<< 64` = 1UL), so
/// an out-of-range code would silently alias onto code 0 — i.e. quietly change
/// the filter. Measured max code is 53 on 2016-08-08 / 2020-03-16 / 2025-06-16 /
/// 2026-06-09 (21-24 distinct codes, min 2), so this never fires today; it exists
/// so a future feed change fails loudly instead.
let maskOfBytes (conditions: Collections.Generic.IReadOnlyList<byte>) : uint64 =
    let mutable m = 0UL
    for i in 0 .. conditions.Count - 1 do
        let c = conditions.[i]
        if c > 63uy then
            failwithf "Bars.maskOfBytes: condition code %d is outside the 0..63 mask range" c
        m <- m ||| (1UL <<< int c)
    m

/// True iff this trade belongs in a 1s bar. `mask` from `maskOf`.
let inline keepByMask (mask: uint64) (sipTs: int64) (participantTs: int64)
                      (price: float) (size: float) : bool =
    size > 0.0 && price > 0.0
    && mask &&& markerMask = 0UL // never an official open / close marker (15, 16): not a trade (2026-10-24)
    && (mask &&& openCloseMask <> 0UL // keep if the trade is an open or a close trade
        || (sipTs = 0L || participantTs = 0L || sipTs - participantTs <= MaxSipDeltaNs) // or if the delta between sip and participant timestamps is small enough
           && mask &&& excludeMask = 0UL) // and there are no exclude conditions

/// The sub-tick fraction of a price: its position inside its tick ($0.01 at >= $1, $0.0001 below), in [0, 1).
/// Exact: the price in integer micro-dollars. Mirrors `subTickFracSql`.
/// ⚠ NOT `price % 0.01` (tried 2026-11-18): 0.01 and 0.0001 have no exact binary form, so the remainder of an ON-TICK
/// price is float noise — 10.01 % 0.01 = 0.00999999999999958 (0.99999 of a tick: a retail BUY), 1.03 % 0.01 = 5e-18 (a
/// retail SELL) — and the band edges (10.004 must be exactly 0.4: mid) land on the wrong side. The tape's prices carry
/// <= 6 decimals, so the price in micro-dollars is an exact integer and everything after it is exact integer arithmetic.
let inline subTickFrac (price: float) : float =
    let m = int64 (Math.Round(price * 1e6))
    if price >= 1.0 then float (m % 10000L) / 10000.0 else float (m % 100L) / 100.0

/// The share flags of a print: FlagCounts (+ FlagRetail or FlagMid for an off-exchange one). Mirrors `countsSql` +
/// `shareClassSql`.
let inline shareFlags (mask: uint64) (exchange: int) (price: float) (size: float) : byte =
    if size > 0.0 && price > 0.0 && mask &&& (markerMask ||| openCloseMask ||| excludeMask) = 0UL then
        if exchange <> TrfExchange then FlagCounts
        else
            let f = subTickFrac price
            if f >= 0.4 && f <= 0.6 then FlagCounts ||| FlagMid
            elif f > 0.0 then FlagCounts ||| FlagRetail
            else FlagCounts
    else 0uy

/// All of a print's flags (0 = the bars ignore it entirely). THE one classification the live path, the parity tool
/// and (in SQL) the corpus builder share.
let inline tradeFlags (mask: uint64) (sipTs: int64) (participantTs: int64) (exchange: int) (price: float) (size: float) : byte =
    (if keepByMask mask sipTs participantTs price size then FlagInBar else 0uy) ||| shareFlags mask exchange price size

// -----------------------------------------------------------------------------
// Bar
// -----------------------------------------------------------------------------

/// One present second. `Vwap`/`Volume` are float32 BY CONTRACT — the corpus
/// stores them as parquet FLOAT and consumers cast up to double, so a bar
/// computed and kept in float64 will not match. Sums accumulate in float64;
/// only the RESULT narrows.
/// ⚠ Volume is float, never int: `size` is fractional and int truncation zeroes
/// ~2% of bars (those summing to <1 share) despite them holding real trades.
/// ⭐ (2026-11-18) TotalVolume / RetailVolume / MidVolume: the SHARE columns (see "THE SHARE COLUMNS" above) — this
/// second's counted prints plus those carried from the seconds since the previous bar that had no in-bar print.
[<Struct>]
type Bar1s =
    { Bucket: int
      Vwap: float32
      Volume: float32
      TradeCount: int
      TotalVolume: float32
      RetailVolume: float32
      MidVolume: float32 }

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
/// ⭐ (2026-11-18) The share columns: a counted print in the open bar's second goes into it; one in a LATER second
/// (necessarily without an in-bar print so far — the tape is time-ordered) is CARRIED and lands in the next bar to open.
type BarAccumulator() =
    let mutable bucket = -1
    let mutable pv = 0.0
    let mutable v = 0.0
    let mutable n = 0
    let mutable tot = 0.0           // the open bar's share volumes (carried ones included)
    let mutable ret = 0.0
    let mutable mid = 0.0
    let mutable cTot = 0.0          // counted prints after the open bar's second: the next bar's
    let mutable cRet = 0.0
    let mutable cMid = 0.0

    let emit () =
        { Bucket = bucket
          Vwap = float32 (pv / v)
          Volume = float32 v
          TradeCount = n
          TotalVolume = float32 tot
          RetailVolume = float32 ret
          MidVolume = float32 mid }

    /// Push one print with its `tradeFlags` (0 = ignored). Returns the completed prior bar when an in-bar print opens
    /// a later second.
    member _.Push(b: int, price: float, size: float, flags: byte) : Bar1s voption =
        let counts = flags &&& FlagCounts <> 0uy
        let r = if flags &&& FlagRetail <> 0uy then size else 0.0
        let m = if flags &&& FlagMid <> 0uy then size else 0.0
        if flags &&& FlagInBar = 0uy then
            if counts then
                if n > 0 && b <= bucket then
                    tot <- tot + size; ret <- ret + r; mid <- mid + m
                else
                    cTot <- cTot + size; cRet <- cRet + r; cMid <- cMid + m
            ValueNone
        elif b = bucket && n > 0 then
            pv <- pv + price * size
            v <- v + size
            n <- n + 1
            if counts then (tot <- tot + size; ret <- ret + r; mid <- mid + m)
            ValueNone
        else
            let out = if n > 0 then ValueSome(emit ()) else ValueNone
            bucket <- b
            pv <- price * size
            v <- size
            n <- 1
            tot <- cTot + (if counts then size else 0.0)
            ret <- cRet + r
            mid <- cMid + m
            cTot <- 0.0; cRet <- 0.0; cMid <- 0.0
            out

    /// Push one IN-BAR print with no share flags (the pre-2026-11-18 call: its share columns stay 0).
    member this.Push(b: int, price: float, size: float) : Bar1s voption = this.Push(b, price, size, FlagInBar)

    /// The final partial bar, if any. Share volume carried past it is dropped.
    member _.Flush() : Bar1s voption =
        if n > 0 then
            let out = ValueSome(emit ())
            n <- 0
            cTot <- 0.0; cRet <- 0.0; cMid <- 0.0
            out
        else ValueNone
