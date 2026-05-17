"""Per-minute S&P 500 breadth imbalance from 1m bars.

For each RTH minute (buckets 330..719 = 09:30..15:59 ET):
  - For every S&P 500 constituent active on that date, compute the trailing
    4-minute VWMA: SUM(dollar_volume) / SUM(volume) over the last 4 bars
    (current bar + 3 preceding), per ticker.
  - Classify each ticker's contribution:
      buy  if bar_vwap >  trailing_vwma_4m
      sell if bar_vwap <  trailing_vwma_4m
      neutral (tie) if equal (rare; drops out of imbalance)
  - Aggregate across constituents:
      buy_dv   = SUM(dollar_volume) where contribution is buy
      sell_dv  = SUM(dollar_volume) where contribution is sell
      n_above  = count of tickers classified buy
      n_below  = count of tickers classified sell
      imbalance = (buy_dv - sell_dv) / (buy_dv + sell_dv)

Output: data/breadth/imbalance_vwma_4m.parquet, one row per (date, bucket).
"""
from __future__ import annotations

import argparse
import sys
import time
from datetime import date, datetime
from pathlib import Path

import duckdb

DEFAULT_BARS_DIR = '/home/mrakgr/Trading-Edge/data/minute_bars_1m'
DEFAULT_MEMBERSHIP = '/home/mrakgr/Trading-Edge/data/sp500_membership.parquet'
DEFAULT_OUT_TEMPLATE = '/home/mrakgr/Trading-Edge/data/breadth/imbalance_vwma_{minutes}m.parquet'
DEFAULT_WINDOW_MINUTES = 4

RTH_START_BUCKET = 330
RTH_END_BUCKET = 719  # 15:59 close inclusive
# The VWMA window covers `window_minutes` bars including the current one;
# we pull `window_minutes - 1` bars of lookback from premarket (or earlier
# RTH) so the 09:30 open bar always sees a full window. The premarket 1m
# bars are in the same parquet (buckets 0..329) and use the same lit-only
# filter as RTH, so they're a clean lookback source.

# Day-level SQL. Computes trailing N-bar VWMA per ticker with a window
# function, classifies each RTH bar against its ticker's VWMA, and aggregates
# across the per-date S&P 500 set. The window function reads (N-1) bars of
# lookback so the first RTH bar (330) has a full N-bar VWMA. We then drop
# pre-RTH rows in the final SELECT — they were lookback only.
DAY_SQL = """
WITH bars AS (
    SELECT
        b.ticker,
        b.bucket,
        b.vwap,
        b.volume,
        b.dollar_volume
    FROM read_parquet('{bars_path}') b
    INNER JOIN (
        SELECT ticker FROM read_parquet('{membership_path}')
        WHERE date = DATE '{date_str}'
    ) m USING (ticker)
    WHERE b.bucket BETWEEN {lookback_start} AND {rth_end}
      AND b.volume > 0           -- silent tickers don't vote
),
with_vwma AS (
    SELECT
        ticker, bucket, vwap, volume, dollar_volume,
        SUM(dollar_volume) OVER (
            PARTITION BY ticker ORDER BY bucket
            ROWS BETWEEN {lookback} PRECEDING AND CURRENT ROW
        ) / NULLIF(
            SUM(volume) OVER (
                PARTITION BY ticker ORDER BY bucket
                ROWS BETWEEN {lookback} PRECEDING AND CURRENT ROW
            ), 0
        ) AS vwma,
        -- Count of bars actually in the window (after premarket-volume gaps).
        COUNT(*) OVER (
            PARTITION BY ticker ORDER BY bucket
            ROWS BETWEEN {lookback} PRECEDING AND CURRENT ROW
        ) AS window_bars
    FROM bars
),
classified AS (
    SELECT
        bucket,
        dollar_volume,
        -- Require the full N-bar window. If a ticker traded fewer than N
        -- of the last N minutes (e.g., premarket gaps), it doesn't vote.
        CASE
            WHEN window_bars < {window_bars} OR vwma IS NULL THEN 'neutral'
            WHEN vwap > vwma  THEN 'buy'
            WHEN vwap < vwma  THEN 'sell'
            ELSE 'neutral'
        END AS side
    FROM with_vwma
    WHERE bucket BETWEEN {rth_start} AND {rth_end}
)
SELECT
    DATE '{date_str}' AS date,
    bucket,
    COALESCE(SUM(dollar_volume) FILTER (WHERE side = 'buy'),  0.0) AS buy_dv,
    COALESCE(SUM(dollar_volume) FILTER (WHERE side = 'sell'), 0.0) AS sell_dv,
    COUNT(*) FILTER (WHERE side = 'buy')     AS n_above,
    COUNT(*) FILTER (WHERE side = 'sell')    AS n_below,
    COUNT(*) FILTER (WHERE side = 'neutral') AS n_neutral
FROM classified
GROUP BY bucket
ORDER BY bucket;
"""


def build_one_day(con: duckdb.DuckDBPyConnection, bars_dir: str,
                  membership_path: str, d: date, window_minutes: int) -> bool:
    """Insert one day's breadth rows. Returns True if the parquet existed."""
    bars_path = Path(bars_dir) / f'{d.isoformat()}.parquet'
    if not bars_path.exists():
        return False
    lookback = window_minutes - 1
    sql = DAY_SQL.format(
        bars_path=bars_path.as_posix(),
        membership_path=membership_path,
        date_str=d.isoformat(),
        rth_start=RTH_START_BUCKET,
        rth_end=RTH_END_BUCKET,
        lookback_start=RTH_START_BUCKET - lookback,
        lookback=lookback,
        window_bars=window_minutes,
    )
    con.execute(f"INSERT INTO breadth {sql}")
    return True


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument('--start', default='2024-01-01')
    p.add_argument('--end', default=None,
                   help='inclusive (yyyy-mm-dd); default: latest bars parquet')
    p.add_argument('--bars-dir', default=DEFAULT_BARS_DIR)
    p.add_argument('--membership', default=DEFAULT_MEMBERSHIP)
    p.add_argument('--window-minutes', type=int, default=DEFAULT_WINDOW_MINUTES,
                   help='VWMA window length in minutes (default: 4)')
    p.add_argument('--out', default=None,
                   help='output path (default: data/breadth/imbalance_vwma_{N}m.parquet)')
    args = p.parse_args()

    if args.window_minutes < 2:
        print('--window-minutes must be >= 2', file=sys.stderr)
        return 1
    out_path_str = args.out or DEFAULT_OUT_TEMPLATE.format(minutes=args.window_minutes)

    start = datetime.strptime(args.start, '%Y-%m-%d').date()
    bars_files = sorted(Path(args.bars_dir).glob('*.parquet'))
    if not bars_files:
        print(f'No parquets in {args.bars_dir}', file=sys.stderr)
        return 1
    latest = datetime.strptime(bars_files[-1].stem, '%Y-%m-%d').date()
    end = (datetime.strptime(args.end, '%Y-%m-%d').date()
           if args.end else latest)

    print(f'Building {args.window_minutes}m-VWMA imbalance breadth for {start}..{end}',
          file=sys.stderr)

    con = duckdb.connect(':memory:')
    # Stage table; we'll dump it to parquet at the end.
    con.execute("""
        CREATE TABLE breadth (
            date DATE,
            bucket INTEGER,
            buy_dv DOUBLE,
            sell_dv DOUBLE,
            n_above BIGINT,
            n_below BIGINT,
            n_neutral BIGINT
        )
    """)

    # Discover usable dates: parquet exists for that day AND it's within [start, end].
    candidates = sorted([
        datetime.strptime(f.stem, '%Y-%m-%d').date()
        for f in bars_files
        if start <= datetime.strptime(f.stem, '%Y-%m-%d').date() <= end
    ])

    started = time.time()
    for i, d in enumerate(candidates, 1):
        build_one_day(con, args.bars_dir, args.membership, d, args.window_minutes)
        if i % 25 == 0 or i == len(candidates):
            elapsed = time.time() - started
            rate = i / max(elapsed, 1e-6)
            eta = (len(candidates) - i) / max(rate, 1e-6)
            cumulative_rows = con.execute("SELECT COUNT(*) FROM breadth").fetchone()[0]
            print(f'  [{i}/{len(candidates)}] {d}  '
                  f'{cumulative_rows:,} rows  {rate:.1f} days/s  eta={eta:.0f}s',
                  file=sys.stderr)

    out_path = Path(out_path_str)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    # Add the imbalance column at write time so storage stays small.
    con.execute(f"""
        COPY (
            SELECT date, bucket, buy_dv, sell_dv, n_above, n_below, n_neutral,
                   CASE WHEN (buy_dv + sell_dv) > 0
                        THEN (buy_dv - sell_dv) / (buy_dv + sell_dv)
                        ELSE 0.0 END AS imbalance
            FROM breadth
            ORDER BY date, bucket
        ) TO '{out_path.as_posix()}' (FORMAT PARQUET, COMPRESSION 'zstd');
    """)

    n_days = con.execute("SELECT COUNT(DISTINCT date) FROM breadth").fetchone()[0]
    n_rows = con.execute("SELECT COUNT(*) FROM breadth").fetchone()[0]
    print(f'Wrote {out_path}: {n_rows:,} rows across {n_days} days '
          f'(avg {n_rows / max(n_days, 1):.1f} bars/day)', file=sys.stderr)
    return 0


if __name__ == '__main__':
    sys.exit(main())
