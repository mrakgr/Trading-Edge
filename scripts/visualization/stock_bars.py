"""Build a hold-dataset bars parquet from a per-(ticker, date) stock trade
parquet, mirroring the F# `btc-bars` command for stocks. The output schema
matches `TradingEdge.Simulation.HoldDataset.datasetSchema` so the existing
`apply-hold-cdf` -> `predict_hold` -> `btc_hold_pred` pipeline accepts it
without changes.

Trade filtering matches the visualization stack (massive_volume.py): SIP-delta
50ms cap, condition-code exclusions for non-price-discovery trades, optional
regular-hours-only clip. Same code paths as the breakout charts so the bars
are constructed from exactly the trades a discretionary trader would consider
on the chart.

For stocks we don't carry buy/sell aggression in the trade tape; buy_count is
set to trade_count (sidedness ≡ +1, panel will read as flat). Label is 0 for
all bars (real data, no ground-truth).

Usage:
    python scripts/visualization/stock_bars.py \\
        --input data/trades/NBIS/2025-09-09.parquet \\
        --output data/stock_bars/NBIS_2025-09-09.parquet \\
        --bars-per-day 3000 \\
        --regular-hours
"""

from __future__ import annotations

import argparse
import os
import sys
import numpy as np
import pyarrow as pa
import pyarrow.parquet as pq

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from trade_filters import filter_trades, filter_by_sip_delta
from market_hours import get_market_hours_bounds
from trade_io import load_trades


def build_bars(trades, bar_volume):
    """Replicate VolumeBarBuilder semantics from VolumeBar.fs.

    - Trade fragments split when a trade overflows the current bar; volumes
      are split exactly so VWAP/StdDev/Volume stay correct.
    - TradeCount counts the whole trade against the bar where its first
      fragment lands; spillover bars get TradeCount=0.
    - Returns dict-of-arrays in HoldDataset parquet column order.
    """
    rel_stddev_arr = []
    ret_arr = []
    duration_sec_arr = []
    trade_count_arr = []
    prev_vwap = None

    cur_prices = []
    cur_vols = []
    cur_volume_sum = 0.0
    cur_trade_count = 0
    cur_start_us = 0
    cur_end_us = 0
    has_started = False

    def emit_bar():
        nonlocal prev_vwap
        prices = np.asarray(cur_prices, dtype=np.float64)
        vols = np.asarray(cur_vols, dtype=np.float64)
        v_sum = vols.sum()
        vwap = float((prices * vols).sum() / v_sum)
        var = float((prices * prices * vols).sum() / v_sum - vwap * vwap)
        std = (max(0.0, var)) ** 0.5
        rel_stddev_arr.append(std / vwap if vwap > 0 else 0.0)
        if prev_vwap is None or prev_vwap <= 0 or vwap <= 0:
            ret_arr.append(0.0)
        else:
            ret_arr.append(float(np.log(vwap / prev_vwap)))
        duration_sec_arr.append((cur_end_us - cur_start_us) / 1e6)
        trade_count_arr.append(cur_trade_count)
        prev_vwap = vwap

    for t in trades:
        price = float(t['price'])
        size = float(t['size'])
        ts_us = int(t['participant_timestamp']) // 1000  # ns -> us
        remaining = size
        counted = False
        while remaining > 0.0:
            if cur_volume_sum == 0.0 and not cur_prices:
                cur_start_us = ts_us
                has_started = True
            if not counted:
                cur_trade_count += 1
                counted = True
            cur_end_us = ts_us
            space_left = bar_volume - cur_volume_sum
            if remaining <= space_left:
                cur_prices.append(price)
                cur_vols.append(remaining)
                cur_volume_sum += remaining
                remaining = 0.0
            else:
                if space_left > 0:
                    cur_prices.append(price)
                    cur_vols.append(space_left)
                    cur_volume_sum += space_left
                    remaining -= space_left
                if cur_volume_sum >= bar_volume:
                    emit_bar()
                    cur_prices.clear()
                    cur_vols.clear()
                    cur_volume_sum = 0.0
                    cur_trade_count = 0

    n = len(rel_stddev_arr)
    return {
        'day_id': np.zeros(n, dtype=np.int32),
        'bar_idx': np.arange(n, dtype=np.int32),
        'rel_stddev': np.asarray(rel_stddev_arr, dtype=np.float64),
        'ret': np.asarray(ret_arr, dtype=np.float64),
        'duration_sec': np.asarray(duration_sec_arr, dtype=np.float64),
        'trade_count': np.asarray(trade_count_arr, dtype=np.int32),
        # Stocks have no aggression flag in the tape; mirror trade_count so
        # the chart's sidedness panel reads as constant +1 instead of NaN.
        'buy_count': np.asarray(trade_count_arr, dtype=np.int32),
        'label': np.zeros(n, dtype=np.int32),
    }


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--input', required=True, help='Per-ticker-day trade parquet (data/trades/TICKER/DATE.parquet)')
    ap.add_argument('--output', required=True, help='Output bars parquet')
    ap.add_argument('--bars-per-day', type=int, default=3000)
    ap.add_argument('--regular-hours', action='store_true', help='Restrict to regular trading hours only')
    args = ap.parse_args()

    print(f'Loading trades from {args.input}')
    trades = load_trades(args.input)
    print(f'  loaded {len(trades):,} trades')

    trades = filter_by_sip_delta(trades)
    for t in trades:
        if t['participant_timestamp'] == 0:
            t['participant_timestamp'] = t['sip_timestamp']

    trades = filter_trades(trades, exclude_odd_lots=False, exclude_extended_hours=False)
    print(f'  after condition filter: {len(trades):,}')

    if args.regular_hours:
        hours = get_market_hours_bounds(trades)
        if hours:
            open_ts, close_ts = hours
            trades = [t for t in trades if open_ts <= t['participant_timestamp'] <= close_ts]
            print(f'  regular hours only: {len(trades):,}')

    if not trades:
        print('  no trades after filtering, skipping')
        return

    total_volume = sum(t['size'] for t in trades)
    bar_volume = total_volume / args.bars_per_day
    print(f'  total volume {total_volume:,.0f}, target bars {args.bars_per_day} -> bar_volume {bar_volume:,.4f}')

    bars = build_bars(trades, bar_volume)
    n = len(bars['bar_idx'])
    print(f'  built {n:,} volume bars')

    # nullable=False so the F# Parquet.NET reader sees these as required
    # columns; otherwise it returns Nullable<T>[] arrays that the F# CDF
    # stage's `:?> int[]` / `:?> float[]` casts can't unbox, and the worker
    # task silently stalls (Parquet.NET 5.4.0 doesn't surface the cast error).
    schema = pa.schema([
        pa.field('day_id', pa.int32(), nullable=False),
        pa.field('bar_idx', pa.int32(), nullable=False),
        pa.field('rel_stddev', pa.float64(), nullable=False),
        pa.field('ret', pa.float64(), nullable=False),
        pa.field('duration_sec', pa.float64(), nullable=False),
        pa.field('trade_count', pa.int32(), nullable=False),
        pa.field('buy_count', pa.int32(), nullable=False),
        pa.field('label', pa.int32(), nullable=False),
    ])
    table = pa.table({k: pa.array(v) for k, v in bars.items()}, schema=schema)
    os.makedirs(os.path.dirname(args.output) or '.', exist_ok=True)
    # One row group per "day" so the F# CDF stage's per-day fit aligns with
    # this single session.
    pq.write_table(table, args.output, row_group_size=n if n > 0 else 1)
    print(f'  wrote {args.output}')


if __name__ == '__main__':
    main()
