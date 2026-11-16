"""MARKET-MAKING OPPORTUNITY SCAN, step 1: the per-(ticker, minute) activity / spread / volatility of the whole market from
the TRADES tape alone (user 2026-11-16: "High volatility, high spreads, high volume and activity. I think those are the
most fertile grounds for a market maker" — find how often such minutes occur, whether they persist, and list them for a
review in TapeSim).

Per RTH minute (m = minutes since 09:30 ET), lit trades only (exchange <> 4; the cap sim's condition exclusions), for every
ticker with at least one minute of >= --min-trades lit trades that day; a minute is SAVED when it has >= --min-trades:
    n        the lit trades (with two prior trades of the ticker: the pairs below)
    dv       the dollar volume
    hi, lo, last_px
    cov      cov(dP_t, dP_t-1) of the trade-to-trade price changes in bp, both inside the minute's trades' order
    var0     var(dP_t)
Roll's spread = 2·sqrt(−cov) reads ≈ 0.27 × the quoted spread on the 40 IPO first days (rank corr 0.85 pooled, 0.67 within
a name; collapsing same-price runs is worse: docs/realized_spread_results.md §the trades-only spread); the bounce-free
volatility per second = sqrt((var0 + 2·cov) · n / 60) (Roll's decomposition var(dP) = σ² + s²/2).

One file per day in --out (resumable: a day with a file is skipped). One day = ~48 s, ~6 GB peak at a 6 GB DuckDB cap.
  cd research && python -u scripts/equity/mm_opportunity_scan.py --start 2026-05-14 > logs/mm_opportunity_scan.log 2>&1; echo exit $?
"""
import argparse, datetime, os, sys, time, zoneinfo
import duckdb

ap = argparse.ArgumentParser()
ap.add_argument('--start', required=True)
ap.add_argument('--end', default='9999-12-31')
ap.add_argument('--every', type=int, default=1, help='every k-th trading day')
ap.add_argument('--trades', default='data/bulk/trades')
ap.add_argument('--out', default='data/equity/mm_opportunity/minutes')
ap.add_argument('--min-trades', type=int, default=100)
ap.add_argument('--mem', default='5GB')
a = ap.parse_args()
NY = zoneinfo.ZoneInfo('America/New_York')
EXCL = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53, 8, 15, 19]::UTINYINT[]"
os.makedirs(a.out, exist_ok=True)
days = sorted(f[:-8] for f in os.listdir(a.trades) if f.endswith('.parquet') and a.start <= f[:-8] <= a.end)[::a.every]
T0 = time.time()
todo = [d for d in days if not os.path.exists(f'{a.out}/{d}.parquet')]
print(f'{len(days)} days, {len(todo)} to do', flush=True)
for k, d in enumerate(todo):
    t = time.time()
    base = int(datetime.datetime.fromisoformat(d).replace(tzinfo=NY).timestamp() * 1e9)
    con = duckdb.connect()
    con.execute(f"SET memory_limit='{a.mem}'; SET threads=8; SET preserve_insertion_order=false")
    df = con.execute(f"""
    WITH x AS (SELECT ticker, sip_timestamp ts, price, size FROM read_parquet('{a.trades}/{d}.parquet')
               WHERE size > 0 AND exchange <> 4 AND NOT list_has_any(conditions, {EXCL})
                 AND sip_timestamp >= {base + int(9.5 * 3600e9)} AND sip_timestamp < {base + int(16 * 3600e9)}),
    hot AS (SELECT DISTINCT ticker FROM (SELECT ticker FROM x GROUP BY ticker, ts // 60000000000 HAVING count(*) >= {a.min_trades})),
    y AS (SELECT x.*, (ts - {base}) // 60000000000 - 570 AS m,
                 1e4 * (price / lag(price) OVER w - 1) d0 FROM x SEMI JOIN hot USING (ticker) WINDOW w AS (PARTITION BY ticker ORDER BY ts)),
    z AS (SELECT *, lag(d0) OVER (PARTITION BY ticker ORDER BY ts) d1 FROM y)
    SELECT ticker, m::SMALLINT m, count(*)::INTEGER n, sum(price * size) dv, max(price) hi, min(price) lo, arg_max(price, ts) last_px,
           avg(d0 * d1) - avg(d0) * avg(d1) cov, var_pop(d0) var0
    FROM z WHERE d1 IS NOT NULL GROUP BY ticker, m HAVING count(*) >= {a.min_trades}""").df()
    con.close()
    df.to_parquet(f'{a.out}/{d}.parquet.tmp', index=False); os.replace(f'{a.out}/{d}.parquet.tmp', f'{a.out}/{d}.parquet')
    el = time.time() - T0
    print(f'{d}: {len(df):,} minutes, {df.ticker.nunique():,} tickers, {time.time() - t:.0f}s | {k + 1}/{len(todo)}, '
          f'ETA {el / (k + 1) * (len(todo) - k - 1) / 60:.0f} min', flush=True)
print(f'done in {(time.time() - T0) / 60:.1f} min', flush=True)
