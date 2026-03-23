#!/usr/bin/env python3
"""Fetch stock fundamentals for stocks in play documentation."""

import os
import sys
import json
import requests
from datetime import datetime
import duckdb

# Add visualization scripts to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'visualization'))
from trade_filters import filter_trades
from market_hours import split_by_market_hours

# Load API keys from config files
CONFIG = {}
if os.path.exists('api_key.json'):
    with open('api_key.json') as f:
        CONFIG = json.load(f)

POLYGON_KEY = CONFIG.get('massive_api_key') or os.getenv('POLYGON_API_KEY')
FMP_KEY = CONFIG.get('financialmodelingprep_key') or os.getenv('FMP_API_KEY')

if not POLYGON_KEY:
    print("Error: Polygon API key not found in api_key.json or POLYGON_API_KEY environment variable")
    sys.exit(1)

def get_ticker_details(ticker):
    """Get shares outstanding from Polygon."""
    url = f"https://api.polygon.io/v3/reference/tickers/{ticker}"
    params = {'apiKey': POLYGON_KEY}

    response = requests.get(url, params=params)
    if response.status_code != 200:
        return None

    data = response.json().get('results', {})
    return {
        'float': data.get('share_class_shares_outstanding'),
    }

def get_avg_volume(ticker, date_str=None, db_path='data/trading.db'):
    """Calculate 20-day average volume from DuckDB, excluding target date."""
    conn = duckdb.connect(db_path, read_only=True)

    if date_str:
        query = """
        SELECT AVG(adj_volume) as avg_volume
        FROM (
            SELECT adj_volume
            FROM split_adjusted_prices
            WHERE ticker = ? AND date < ?
            ORDER BY date DESC
            LIMIT 20
        )
        """
        result = conn.execute(query, [ticker, date_str]).fetchone()
    else:
        query = """
        SELECT AVG(adj_volume) as avg_volume
        FROM (
            SELECT adj_volume
            FROM split_adjusted_prices
            WHERE ticker = ?
            ORDER BY date DESC
            LIMIT 20
        )
        """
        result = conn.execute(query, [ticker]).fetchone()

    conn.close()
    return result[0] if result else None

def get_gap_and_premarket(ticker, date_str):
    """Get gap % and premarket volume for a specific date."""
    conn = duckdb.connect('data/trading.db', read_only=True)

    # Get current day's open and previous day's close
    query = """
    SELECT
        curr.adj_open as open_price,
        prev.adj_close as prev_close
    FROM split_adjusted_prices curr
    ASOF LEFT JOIN split_adjusted_prices prev
        ON curr.ticker = prev.ticker
        AND prev.date < curr.date
    WHERE curr.ticker = ? AND curr.date = ?
    """
    result = conn.execute(query, [ticker, date_str]).fetchone()
    conn.close()

    prev_close = result[1] if result and result[1] else None
    open_price = result[0] if result and result[0] else None

    # Calculate gap
    gap_pct = None
    if prev_close and open_price:
        gap_pct = ((open_price - prev_close) / prev_close) * 100

    # Get premarket volume from trades JSON
    trades_path = f'data/trades/{ticker}/{date_str}.json'
    premarket_volume = 0

    if os.path.exists(trades_path):
        with open(trades_path) as f:
            trades = json.load(f)

        # Filter trades first
        trades = filter_trades(trades, exclude_odd_lots=False, exclude_extended_hours=False)

        # Split by market hours
        pre_market, regular, post_market = split_by_market_hours(trades)
        premarket_volume = sum(t['size'] for t in pre_market)

    return {
        'gap_pct': gap_pct,
        'premarket_volume': premarket_volume,
        'prev_close': prev_close,
        'open_price': open_price
    }

def parse_number(s):
    """Parse number with k/m/b suffixes (e.g., '575m' -> 575000000)."""
    s = s.lower().strip()
    multipliers = {'k': 1e3, 'm': 1e6, 'b': 1e9}

    for suffix, mult in multipliers.items():
        if s.endswith(suffix):
            return int(float(s[:-1]) * mult)

    return int(s)

def format_number(num):
    """Format large numbers with k/m/b suffixes."""
    if num is None:
        return "Unknown"
    if num >= 1e9:
        return f"{num/1e9:.1f}b"
    if num >= 1e6:
        return f"{num/1e6:.1f}m"
    if num >= 1e3:
        return f"{num/1e3:.1f}k"
    return f"{num:,.0f}"

if __name__ == '__main__':
    if len(sys.argv) < 2:
        print("Usage: python fetch_stock_fundamentals.py TICKER [DATE] [FLOAT]")
        print("Example: python fetch_stock_fundamentals.py SMCI 2026-03-20")
        print("Example: python fetch_stock_fundamentals.py TME 2026-03-17 575000000")
        sys.exit(1)

    ticker = sys.argv[1].upper()
    date_str = sys.argv[2] if len(sys.argv) > 2 else None
    manual_float = parse_number(sys.argv[3]) if len(sys.argv) > 3 else None

    print(f"Fetching fundamentals for {ticker}...")

    # Get ticker details (use manual float if provided)
    if manual_float:
        print(f"Float: {format_number(manual_float)}")
        details = {'float': manual_float}
    else:
        details = get_ticker_details(ticker)
        if details:
            print(f"Float: {format_number(details['float'])} (shares outstanding)")
        else:
            print("Float: Unable to fetch")
            details = None

    # Get average volume
    avg_vol = get_avg_volume(ticker, date_str)
    print(f"Avg. Volume: {format_number(avg_vol)}")

    # Get gap and premarket if date provided
    if date_str:
        gap_data = get_gap_and_premarket(ticker, date_str)
        if gap_data['gap_pct'] is not None:
            print(f"Gap %: {gap_data['gap_pct']:.2f}%")
        else:
            print("Gap %: Unable to calculate")

        pm_vol = gap_data['premarket_volume']
        pm_str = f"Premarket volume: {format_number(pm_vol)}"

        # Calculate percentages
        if avg_vol and pm_vol:
            pm_avg_pct = (pm_vol / avg_vol) * 100
            pm_str += f" ({pm_avg_pct:.0f}% avg"

            if details and details['float']:
                pm_float_pct = (pm_vol / details['float']) * 100
                pm_str += f", {pm_float_pct:.1f}% float"

            pm_str += ")"

        print(pm_str)
