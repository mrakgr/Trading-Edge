"""Batch-render hold-detector charts for the latest gap-up plays, sorted by rvol.

Reads `data/gap_up_top200.json`, filters out rows whose trade shard is
missing, and runs each through the stock_bars -> apply-hold-cdf ->
predict_hold -> btc_hold_pred chart pipeline. Output charts are named
`{rank:03d}_rvol{rvol}_{ticker}_{date}.html` so a directory listing is
already sorted by rvol descending.

Intermediate parquets land in /tmp and are reused only within a single run.

Usage:
    python scripts/visualization/gen_gap_up_charts.py \\
        --input data/gap_up_top200.json \\
        --output-dir data/charts/gap_up_top200 \\
        --checkpoint data/hold_gmlp.pt
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))


def run(cmd, log_prefix=''):
    """Run a subprocess, raise on failure, return captured stdout/stderr."""
    res = subprocess.run(cmd, capture_output=True, text=True)
    if res.returncode != 0:
        sys.stderr.write(f'{log_prefix}FAILED: {" ".join(cmd)}\n')
        sys.stderr.write(res.stdout + '\n' + res.stderr + '\n')
        raise RuntimeError(res.stderr or res.stdout)
    return res.stdout


def chart_one(play, rank, output_dir, checkpoint, tmp_dir):
    ticker = play['ticker']
    date = play['date']
    rvol = play.get('rvol')
    rvol_str = f'{rvol:05.2f}' if rvol is not None else 'NA'

    trade_path = os.path.join(ROOT, 'data', 'trades', ticker, f'{date}.parquet')
    if not os.path.exists(trade_path):
        return False, 'missing shard'

    bars = os.path.join(tmp_dir, 'bars.parquet')
    cdf = os.path.join(tmp_dir, 'cdf.parquet')
    pred = os.path.join(tmp_dir, 'pred.parquet')

    out_html = os.path.join(output_dir, f'{rank:03d}_rvol{rvol_str}_{ticker}_{date}.html')
    if os.path.exists(out_html):
        return True, 'cached'

    # Stage 1: trades -> bars (Python, regular hours only)
    run(['python', os.path.join(ROOT, 'scripts/visualization/stock_bars.py'),
         '--input', trade_path, '--output', bars,
         '--bars-per-day', '3000', '--regular-hours'],
        log_prefix=f'[{rank}/{ticker}] ')

    # Stage 2: per-day CDF transform (F#)
    if os.path.exists(cdf):
        os.remove(cdf)
    run(['dotnet', 'run', '--project', os.path.join(ROOT, 'TradingEdge.Simulation'),
         '-c', 'Release', '--', 'apply-hold-cdf',
         '--input', bars, '--output', cdf],
        log_prefix=f'[{rank}/{ticker}] ')

    # Stage 3: model inference
    run(['python', os.path.join(ROOT, 'scripts/training/predict_hold.py'),
         '--input', cdf, '--checkpoint', checkpoint, '--output', pred],
        log_prefix=f'[{rank}/{ticker}] ')

    # Stage 4: chart
    rvol_label = f'{rvol:.2f}' if rvol is not None else 'NA'
    title = f'{ticker} {date} — rvol {rvol_label}, gap {play.get("gap_pct", 0)*100:.1f}%'
    run(['python', os.path.join(ROOT, 'scripts/visualization/btc_hold_pred.py'),
         '--bars', bars, '--pred', pred, '--output', out_html,
         '--title', title],
        log_prefix=f'[{rank}/{ticker}] ')

    return True, 'rendered'


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--input', default='data/gap_up_top200.json')
    ap.add_argument('--output-dir', default='data/charts/gap_up_top200')
    ap.add_argument('--checkpoint', default='data/hold_gmlp.pt')
    args = ap.parse_args()

    plays = json.load(open(args.input))
    os.makedirs(args.output_dir, exist_ok=True)

    # Per-process scratch directory; reused across plays so the F# JIT/parquet
    # readers cache stays warm and we don't fragment /tmp.
    tmp_dir = tempfile.mkdtemp(prefix='gap_up_charts_')

    sw = time.time()
    rendered = skipped = failed = 0
    for i, play in enumerate(plays):
        rank = i + 1
        try:
            ok, status = chart_one(play, rank, args.output_dir, args.checkpoint, tmp_dir)
        except Exception as e:
            ok, status = False, f'error: {e}'
        ticker = play['ticker']; date = play['date']
        rvol = play.get('rvol')
        rvol_str = f'{rvol:.2f}' if rvol is not None else 'NA'
        elapsed = time.time() - sw
        rate = rank / elapsed if elapsed > 0 else 0
        eta = (len(plays) - rank) / rate if rate > 0 else 0
        if ok:
            print(f'[{rank}/{len(plays)}] {ticker} {date} rvol={rvol_str} → {status}  ({rate:.1f}/s, ETA {eta/60:.1f}m)')
            rendered += 1
        else:
            if status == 'missing shard':
                print(f'[{rank}/{len(plays)}] {ticker} {date} rvol={rvol_str} → SKIP {status}')
                skipped += 1
            else:
                print(f'[{rank}/{len(plays)}] {ticker} {date} rvol={rvol_str} → FAIL {status}')
                failed += 1
        sys.stdout.flush()

    shutil.rmtree(tmp_dir, ignore_errors=True)
    print(f'\nDone. rendered={rendered} skipped={skipped} failed={failed}  total time {time.time()-sw:.0f}s')
    print(f'Charts in: {args.output_dir}')


if __name__ == '__main__':
    main()
