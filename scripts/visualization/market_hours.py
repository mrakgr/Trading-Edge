"""Market hours detection from trade conditions."""
import json
from typing import List, Dict, Tuple, Optional

# Opening conditions: 16 (Market Center Official Open)
# Closing conditions: 15 (Market Center Official Close)
OPENING_CONDITIONS = {16}
CLOSING_CONDITIONS = {15}
EXTENDED_HOURS_CONDITION = 12


def load_trades(filepath: str) -> List[Dict]:
    """Load trades from JSON file."""
    with open(filepath) as f:
        return json.load(f)


def detect_market_hours(trades: List[Dict]) -> Optional[Tuple[int, int]]:
    """
    Detect market open/close times from opening/closing print conditions.
    Returns (open_timestamp, close_timestamp) in nanoseconds, or None if not found.
    """
    open_ts = None
    close_ts = None

    for trade in trades:
        conditions = trade.get('conditions')
        if not conditions:
            continue

        ts = trade['participant_timestamp'] if trade['participant_timestamp'] != 0 else trade['sip_timestamp']

        if any(c in OPENING_CONDITIONS for c in conditions):
            if open_ts is None or ts < open_ts:
                open_ts = ts

        if any(c in CLOSING_CONDITIONS for c in conditions):
            # Take the earliest close marker (not the latest)
            if close_ts is None or ts < close_ts:
                close_ts = ts

    if open_ts and close_ts:
        return (open_ts, close_ts)
    return None


def split_by_market_hours(trades: List[Dict]) -> Tuple[List[Dict], List[Dict], List[Dict]]:
    """
    Split trades into pre-market, regular, and post-market.
    Returns (pre_market, regular, post_market).
    """
    hours = detect_market_hours(trades)

    if hours:
        open_ts, close_ts = hours
        pre = [t for t in trades if (t['participant_timestamp'] if t['participant_timestamp'] != 0 else t['sip_timestamp']) < open_ts]
        regular = [t for t in trades if open_ts <= (t['participant_timestamp'] if t['participant_timestamp'] != 0 else t['sip_timestamp']) <= close_ts]
        post = [t for t in trades if (t['participant_timestamp'] if t['participant_timestamp'] != 0 else t['sip_timestamp']) > close_ts]
        return (pre, regular, post)

    # Fallback: use condition 12 to split regular vs extended hours
    regular = []
    extended = []

    for trade in trades:
        conditions = trade.get('conditions')
        if conditions and EXTENDED_HOURS_CONDITION in conditions:
            extended.append(trade)
        else:
            regular.append(trade)

    if not regular:
        return ([], [], extended)

    # Find regular hours boundaries from trades without condition 12
    regular_start = min(t['participant_timestamp'] if t['participant_timestamp'] != 0 else t['sip_timestamp'] for t in regular)
    regular_end = max(t['participant_timestamp'] if t['participant_timestamp'] != 0 else t['sip_timestamp'] for t in regular)

    # Split extended hours into pre and post based on regular hours boundaries
    pre = [t for t in extended if (t['participant_timestamp'] if t['participant_timestamp'] != 0 else t['sip_timestamp']) < regular_start]
    post = [t for t in extended if (t['participant_timestamp'] if t['participant_timestamp'] != 0 else t['sip_timestamp']) > regular_end]

    return (pre, regular, post)


def get_market_hours_bounds(trades: List[Dict]) -> Optional[Tuple[float, float]]:
    """
    Get market hours as (open_timestamp, close_timestamp) in nanoseconds.
    Returns (open_ts, close_ts) or None.
    """
    hours = detect_market_hours(trades)
    if hours:
        return hours

    # Fallback: use condition 12 to find regular hours boundaries
    regular = [t for t in trades if not t.get('conditions') or EXTENDED_HOURS_CONDITION not in t.get('conditions', [])]

    if regular:
        regular_start = min(t['participant_timestamp'] if t['participant_timestamp'] != 0 else t['sip_timestamp'] for t in regular)
        regular_end = max(t['participant_timestamp'] if t['participant_timestamp'] != 0 else t['sip_timestamp'] for t in regular)
        return (regular_start, regular_end)

    return None
