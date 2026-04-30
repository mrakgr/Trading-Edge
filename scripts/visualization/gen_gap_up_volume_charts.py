"""Batch-render full-day volume-bar VWAP charts (no model overlay) for the
gap-up plays in a given setup-list JSON. Sister script to gen_gap_up_charts.py
but uses massive_volume.py — extended hours included so premarket / postmarket
ranges are visible.

Usage:
    python scripts/visualization/gen_gap_up_volume_charts.py \\
        --input data/gap_up_breakout52w_top200.json \\
        --output-dir data/charts/gap_up_breakout52w_top200_volume
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))


def chart_one(play, rank, output_dir):
    ticker = play['ticker']
    date = play['date']
    rvol = play.get('rvol')
    rvol_str = f'{rvol:05.2f}' if rvol is not None else 'NA'

    trade_path = os.path.join(ROOT, 'data', 'trades', ticker, f'{date}.parquet')
    if not os.path.exists(trade_path):
        return False, 'missing shard'

    out_html = os.path.join(output_dir, f'{rank:03d}_rvol{rvol_str}_{ticker}_{date}.html')
    if os.path.exists(out_html):
        return True, 'cached'

    res = subprocess.run(
        ['python', os.path.join(ROOT, 'scripts/visualization/massive_volume.py'),
         trade_path, '-o', out_html],
        capture_output=True, text=True)
    if res.returncode != 0:
        sys.stderr.write(res.stdout + '\n' + res.stderr + '\n')
        raise RuntimeError(res.stderr or res.stdout)
    return True, 'rendered'


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--input', default='data/gap_up_breakout52w_top200.json')
    ap.add_argument('--output-dir', default='data/charts/gap_up_breakout52w_top200_volume')
    args = ap.parse_args()

    plays = json.load(open(args.input))
    os.makedirs(args.output_dir, exist_ok=True)

    sw = time.time()
    rendered = skipped = failed = 0
    for i, play in enumerate(plays):
        rank = i + 1
        try:
            ok, status = chart_one(play, rank, args.output_dir)
        except Exception as e:
            ok, status = False, f'error: {e}'
        ticker = play['ticker']
        date = play['date']
        rvol = play.get('rvol')
        rvol_str = f'{rvol:.2f}' if rvol is not None else 'NA'
        elapsed = time.time() - sw
        rate = rank / elapsed if elapsed > 0 else 0
        eta = (len(plays) - rank) / rate if rate > 0 else 0
        if ok:
            print(f'[{rank}/{len(plays)}] {ticker} {date} rvol={rvol_str} → {status}  ({rate:.1f}/s, ETA {eta/60:.1f}m)')
            rendered += 1
        elif status == 'missing shard':
            print(f'[{rank}/{len(plays)}] {ticker} {date} rvol={rvol_str} → SKIP {status}')
            skipped += 1
        else:
            print(f'[{rank}/{len(plays)}] {ticker} {date} rvol={rvol_str} → FAIL {status}')
            failed += 1
        sys.stdout.flush()

    print(f'\nDone. rendered={rendered} skipped={skipped} failed={failed}  total time {time.time()-sw:.0f}s')
    print(f'Charts in: {args.output_dir}')


if __name__ == '__main__':
    main()
