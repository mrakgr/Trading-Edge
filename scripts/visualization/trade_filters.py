"""
Canonical lit-only trade filter — Python mirror of TradingEdge.Orb.TradeFilters.

If either side changes, the other MUST follow. Used by visualization scripts
that load trade dicts from JSON/parquet; the same constants are also exposed
as SQL strings for use inside DuckDB queries.

Filter semantics: keep a trade iff
  size > 0
  AND trf_id = 0      (lit exchange; TRFs/ADF carry nonzero trf_id)
  AND (conditions intersects opening/closing prints
       OR conditions does NOT intersect the exclude set)

There is no SIP-delta filter. With lit-only prints the late-print pathology
that motivated the original 50ms SIP-delta drop doesn't occur — lit venues
report via the SIP synchronously, the off-tape latency was the TRF/dark-pool
tail we now exclude wholesale via trf_id = 0.

Based on CTA/UTP trade condition codes documented in docs/trade_conditions.md.
"""

# CTA/UTP trade condition codes that disqualify a print from price discovery.
# Codes 12 (Form T) and 37 (Odd Lot) are intentionally NOT here: 99%+ of
# premarket trades carry 12, and odd lots make up a large share of modern
# volume. Late prints in those groups were previously caught by the SIP-delta
# filter — which we now drop in favor of the lit-only (trf_id = 0) filter.
EXCLUDE_FOR_PRICE_DISCOVERY = {
    2,   # Average Price Trade
    7,   # Cash Sale (special settlement)
    10,  # Derivatively Priced
    13,  # Extended Hours (Sold Out Of Sequence)
    20,  # Next Day (special settlement)
    21,  # Price Variation Trade
    22,  # Prior Reference Price
    29,  # Seller (special settlement)
    32,  # Sold (Out of Sequence)
    52,  # Contingent Trade
    53,  # Qualified Contingent Trade (QCT)
}

OPENING_PRINTS = {
    17,  # Market Center Opening Trade
    25,  # Opening Prints
}

CLOSING_PRINTS = {
    19,  # Market Center Closing Trade
    8,   # Closing Prints
}

OPEN_CLOSE_PRINTS = OPENING_PRINTS | CLOSING_PRINTS


def should_exclude_trade(trade):
    """
    True iff the trade should be dropped by the conditions filter alone
    (does NOT check size or trf_id — use should_keep_trade for the full check).
    Opening/closing prints override the exclude set.
    """
    if trade['conditions'] is None:
        return False
    conditions = set(trade['conditions'])
    if conditions & OPEN_CLOSE_PRINTS:
        return False
    return bool(conditions & EXCLUDE_FOR_PRICE_DISCOVERY)


def should_keep_trade(trade):
    """
    Full composite predicate: size > 0 AND trf_id = 0 AND conditions OK.

    Raises KeyError if `trf_id` is missing from the trade dict — same posture
    as the F# side: data-integrity issues should fail loudly rather than be
    silently treated as lit. Trades from pre-trf_id parquets need a rebuild.
    """
    if trade.get('size', 0) <= 0:
        return False
    if trade['trf_id'] != 0:
        return False
    return not should_exclude_trade(trade)


def filter_trades(trades):
    """Filter out trades failing the full lit-only predicate."""
    return [t for t in trades if should_keep_trade(t)]


# -----------------------------------------------------------------------------
# SQL fragments — DuckDB-flavored. Must stay in sync with the constants above
# AND with TradingEdge.Orb.TradeFilters in F#.
# -----------------------------------------------------------------------------

EXCLUDE_SET_SQL = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53]::UTINYINT[]"
OPEN_CLOSE_SET_SQL = "[17, 25, 19, 8]::UTINYINT[]"
CONDITIONS_SQL_CLAUSE = (
    f"(list_has_any(conditions, {OPEN_CLOSE_SET_SQL}) "
    f"OR NOT list_has_any(conditions, {EXCLUDE_SET_SQL}))"
)
WHERE_CLAUSE_SQL = f"size > 0 AND trf_id = 0 AND {CONDITIONS_SQL_CLAUSE}"
