r"""BUILDUP STUDY, step 1: the per-(ticker, RTH 5-minute bar) signed volume from the 1 s bars (the tick rule on the bar vwap; validated
against the MBO aggressor side by buildup_validate.py: median corr 0.66 per minute, ~0.8 over 60 min).

UNIVERSE (causal — known before the open of day D): tickers whose RTH dollar volume on the PREVIOUS trading day was
>= --min-prev-dv and whose previous-day RTH vwap was >= --min-px. Both read from the previous day's 1 s file (RAW prices:
the 1 s files are the raw tape aggregated; a split between D-1 and D does not touch a same-day signal).
Per 5-minute bar m (0 = 09:30-09:35; 5 min, not 1: 35 MB/day at 1 min = 90 GB for 10 years): vol, sv (signed volume), dv, n (trades), p0 / p1 (the first / last 1 s vwap), hi / lo (of the
1 s vwaps). Per ticker-day also prev_vol / prev_dv (the universe's own fields, for normalising the buildup).
One file per day in --out (resumable).

  cd research && python -u scripts/equity/buildup_scan.py --start 2016-08-09 > logs/buildup_scan.log 2>&1; echo exit $?
"""
import argparse, os, time
import duckdb

ap = argparse.ArgumentParser()
ap.add_argument('--start', required=True)
ap.add_argument('--end', default='9999-12-31')
ap.add_argument('--every', type=int, default=1)
ap.add_argument('--bars', default='data/intraday_1s_slim')
ap.add_argument('--out', default='/mnt/d/trading-edge-bulk/buildup_5m')
ap.add_argument('--bar', type=int, default=300, help='bar length, s')
ap.add_argument('--min-prev-dv', type=float, default=10e6)
ap.add_argument('--min-px', type=float, default=1.0)
ap.add_argument('--mem', default='4GB')
a = ap.parse_args()
os.makedirs(a.out, exist_ok=True)
alld = sorted(f[:-8] for f in os.listdir(a.bars) if f.endswith('.parquet'))
pairs = [(alld[i - 1], alld[i]) for i in range(1, len(alld)) if a.start <= alld[i] <= a.end][::a.every]
todo = [(p, d) for p, d in pairs if not os.path.exists(f'{a.out}/{d}.parquet')]
print(f'{len(pairs)} days, {len(todo)} to do', flush=True)
T0 = time.time()
for k, (p, d) in enumerate(todo):
    t = time.time()
    con = duckdb.connect()
    con.execute(f"SET memory_limit='{a.mem}'; SET threads=8; SET preserve_insertion_order=false")
    con.execute(f"""COPY (
    WITH u AS (SELECT ticker, sum(volume) prev_vol, sum(vwap * volume) prev_dv FROM read_parquet('{a.bars}/{p}.parquet')
               WHERE bucket >= 34200 AND bucket < 57600 GROUP BY ticker
               HAVING sum(vwap * volume) >= {a.min_prev_dv} AND sum(vwap * volume) / sum(volume) >= {a.min_px}),
    x AS (SELECT b.ticker, bucket, vwap, volume, trade_count FROM read_parquet('{a.bars}/{d}.parquet') b SEMI JOIN u USING (ticker)
          WHERE bucket >= 34200 AND bucket < 57600 AND volume > 0),
    y AS (SELECT *, vwap - lag(vwap) OVER (PARTITION BY ticker ORDER BY bucket) AS dvw FROM x),
    z AS (SELECT *, last_value(CASE WHEN dvw <> 0 THEN sign(dvw) END IGNORE NULLS)
                     OVER (PARTITION BY ticker ORDER BY bucket ROWS UNBOUNDED PRECEDING) AS s FROM y)
    SELECT z.ticker, ((bucket - 34200) // {a.bar})::SMALLINT AS m, sum(volume)::FLOAT vol, sum(coalesce(s, 0) * volume)::FLOAT sv,
           sum(vwap * volume)::FLOAT dv, sum(trade_count)::INTEGER n, arg_min(vwap, bucket) p0, arg_max(vwap, bucket) p1,
           max(vwap) hi, min(vwap) lo, any_value(u.prev_vol)::FLOAT prev_vol, any_value(u.prev_dv)::FLOAT prev_dv
    FROM z JOIN u USING (ticker) GROUP BY z.ticker, m ORDER BY z.ticker, m
    ) TO '{a.out}/{d}.parquet.tmp' (FORMAT parquet)""")
    con.close()
    os.replace(f'{a.out}/{d}.parquet.tmp', f'{a.out}/{d}.parquet')
    el = time.time() - T0
    print(f'{d}: {time.time() - t:.1f}s | {k + 1}/{len(todo)}, ETA {el / (k + 1) * (len(todo) - k - 1) / 60:.0f} min', flush=True)
print(f'done in {(time.time() - T0) / 60:.1f} min', flush=True)
