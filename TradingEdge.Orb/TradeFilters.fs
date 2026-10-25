module TradingEdge.Orb.TradeFilters

// =============================================================================
// Canonical lit-only trade filter — single source of truth for F# and SQL
// =============================================================================
//
// Used by TradeLoader (in-memory row filter), the 1m-bar builder (DuckDB SQL
// fragment), and any future filter-using code. The Python module
// scripts/visualization/trade_filters.py mirrors the same constants and SQL
// strings — if any of these change, that file must change too.
//
// Filter semantics: keep a trade iff
//   size > 0
//   AND trf_id = 0      (lit exchange; TRFs/ADF carry nonzero trf_id)
//   AND (conditions intersects opening/closing prints
//        OR conditions does NOT intersect the exclude set)
//
// Note: there is no SIP-delta filter. With lit-only prints, the late-print
// pathology that motivated the original 50ms SIP-delta drop doesn't occur —
// lit venues report via the SIP synchronously, the off-tape latency was the
// TRF/dark-pool tail we now exclude wholesale via trf_id = 0.

/// CTA/UTP trade condition codes that disqualify a print from price discovery.
/// Codes 12 (Form T) and 37 (Odd Lot) are intentionally NOT here: 99%+ of
/// premarket trades carry 12, and odd lots make up a large share of modern
/// volume. Late prints in those groups were previously caught by the SIP-delta
/// filter — which we now drop in favor of the lit-only (trf_id = 0) filter.
let excludeConditions : int Set = Set.ofList [
    2   // Average Price Trade
    7   // Cash Sale (special settlement)
    10  // Derivatively Priced
    13  // Extended Hours (Sold Out Of Sequence)
    20  // Next Day (special settlement)
    21  // Price Variation Trade
    22  // Prior Reference Price
    29  // Seller (special settlement)
    32  // Sold (Out of Sequence)
    52  // Contingent Trade
    53  // Qualified Contingent Trade (QCT)
]

let openingPrintConditions : int Set = Set.ofList [
    17  // Market Center Opening Trade
    25  // Opening Prints
]

let closingPrintConditions : int Set = Set.ofList [
    19  // Market Center Closing Trade
    8   // Closing Prints
]

let openingAndClosingPrintConditions =
    openingPrintConditions + closingPrintConditions

/// ⭐ MARKERS, NOT TRADES (2026-10-24, docs/gapfader_results.md GF-47 / GF-49): 15 Market Center Official Close and
/// 16 Market Center Official Open restate the price and size of a print that is already on the tape (the condition
/// table: Hi/Lo N, Op/Cl N, Volume N). Every venue publishes its own: on the listing venue a copy of its 17 / 8 cross,
/// elsewhere a copy of that venue's first / last trade, and NYSE Arca restates Nasdaq's closing cross a second time.
/// Measured on 5 days 2017-2026: 79-99.5 % of the markers copy a real print of the same ticker, size and price; the
/// rest carry <= 0.11 % of the markers' volume. Kept, they were 9.5-18 % of a day's 1s-bar volume.
/// ⚠ Only the 1s convention drops them (Bars.fs: the 1s corpus and the Oms's bars), BEFORE the auction override (a
/// marker never carries 17/25/19/8, measured 0, but must not be rescued by one). This module's lit-only lineage
/// (`whereClauseSql`: the 1m bars, TradeLoader, trade_filters.py) still counts them.
let markerConditions : int Set = Set.ofList [
    15  // Market Center Official Close
    16  // Market Center Official Open
]

/// True iff the conditions array means we should drop this trade.
/// Opening/closing prints override the exclude set — we always keep auction
/// prints even when they coincide with an excluded condition code.
let shouldExclude (conditions: int Set) : bool =
    if not (Set.intersect conditions openingAndClosingPrintConditions).IsEmpty
    then false
    else not (Set.intersect conditions excludeConditions).IsEmpty

/// Full composite predicate: size > 0 AND lit (trf_id = 0) AND conditions OK.
/// trf_id = 0 means the print came from a lit exchange. TRFs (FINRA TRF
/// IDs 201/202/203) and ADF (exchange = 4) carry nonzero trf_id and are
/// dropped — those are dark/off-exchange prints that don't move price
/// discovery on lit venues.
let shouldKeepTrade (trfId: int64) (size: float) (conditions: int Set) : bool =
    size > 0.0 && trfId = 0L && not (shouldExclude conditions)

// -----------------------------------------------------------------------------
// SQL fragments — DuckDB-flavored.
// -----------------------------------------------------------------------------
// These strings encode the same predicate as shouldKeepTrade above, for use
// inside a COPY (SELECT ... FROM read_parquet(...)) WHERE clause. The integer
// lists here MUST match excludeConditions / openingAndClosingPrintConditions
// above. The schema target is the bulk-trades parquet layout produced by
// TradingEdge.Massive.S3Download / TradesDownload, which has columns
// `trf_id BIGINT`, `size DOUBLE`, `conditions UTINYINT[]`.

let excludeSetSql =
    "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53]::UTINYINT[]"

let openCloseSetSql = "[17, 25, 19, 8]::UTINYINT[]"

/// `markerConditions` (the 1s convention only: Bars.fs)
let markerSetSql = "[15, 16]::UTINYINT[]"

let conditionsSqlClause =
    sprintf "(list_has_any(conditions, %s) OR NOT list_has_any(conditions, %s))"
        openCloseSetSql excludeSetSql

/// Full WHERE predicate for queries reading raw bulk parquets.
let whereClauseSql =
    sprintf "size > 0 AND trf_id = 0 AND %s" conditionsSqlClause
