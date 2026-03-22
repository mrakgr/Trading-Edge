"""
Trade filtering utilities for removing outliers and special trade types.

Based on CTA/UTP trade condition codes documented in docs/trade_conditions.md
"""

# Condition codes to exclude for price discovery analysis
EXCLUDE_FOR_PRICE_DISCOVERY = {
    2,   # Average Price Trade
    7,   # Cash Sale (special settlement)
    10,  # Derivatively Priced
    12,  # Form T/Extended Hours
    13,  # Extended Hours (Sold Out Of Sequence)
    20,  # Next Day (special settlement)
    21,  # Price Variation Trade
    22,  # Prior Reference Price
    29,  # Seller (special settlement)
    32,  # Sold (Out of Sequence)
    37,  # Odd Lot Trade
    52,  # Contingent Trade
    53,  # Qualified Contingent Trade (QCT)
}

def should_exclude_trade(trade, exclude_odd_lots=True, exclude_extended_hours=True):
    """
    Determine if a trade should be excluded from price analysis.

    Args:
        trade: Trade dict with 'conditions' field
        exclude_odd_lots: If False, don't exclude code 37 (odd lots)
        exclude_extended_hours: If False, don't exclude codes 12, 13 (extended hours)

    Returns:
        True if trade should be excluded, False otherwise
    """
    if trade['conditions'] is None:
        return False

    conditions = set(trade['conditions'])
    exclude_codes = EXCLUDE_FOR_PRICE_DISCOVERY.copy()

    if not exclude_odd_lots:
        exclude_codes.discard(37)

    if not exclude_extended_hours:
        exclude_codes.discard(12)
        exclude_codes.discard(13)

    return bool(conditions & exclude_codes)

def filter_trades(trades, exclude_odd_lots=True, exclude_extended_hours=True):
    """
    Filter out trades with special conditions that don't reflect true market prices.

    Args:
        trades: List of trade dicts
        exclude_odd_lots: Whether to exclude odd lot trades (code 37)
        exclude_extended_hours: Whether to exclude extended hours trades (codes 12, 13)

    Returns:
        Filtered list of trades
    """
    return [t for t in trades if not should_exclude_trade(t, exclude_odd_lots, exclude_extended_hours)]
