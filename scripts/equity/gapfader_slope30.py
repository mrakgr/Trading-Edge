"""GapFader GF-31 input (user 2026-10-19): the 09:30–10:00 SLOPE of the gap-up swing sample (GF-10's: gap-up >= 4 %,
rvol_pm >= 0.2, headline universe) — the OLS slope of ln(vwap) on the second, over the PRESENT 1s bars (FlushFader's s20
analogue for the first 30 min), in bp per minute, + the bar count it used. Cached: data/equity/gapfader/slope30.parquet.
Run from research/:  python -u scripts/equity/gapfader_slope30.py > data/equity/gapfader/slope30.log 2>&1; echo exit $?
"""
import time, duckdb, pandas as pd
T0 = time.time()
C = pd.read_parquet("data/equity/gapfader/census.parquet", columns=["ticker", "date", "open", "dv20_prior", "early_close", "close", "rvol_pm", "gap"])
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm >= 0.2) & (C.gap >= 0.04)]
con = duckdb.connect(); parts = []; days = sorted(C.date.unique())
for i, d in enumerate(days):
    tl = ",".join("'" + t.replace("'", "''") + "'" for t in C[C.date == d].ticker)
    parts.append(con.execute(f"""SELECT ticker, '{d}' AS date, regr_slope(ln(vwap::DOUBLE), bucket) * 60 * 1e4 AS slope30, count(*) AS n_slope
        FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
        WHERE ticker IN ({tl}) AND bucket >= 34200 AND bucket < 36000 AND vwap > 0 GROUP BY ticker""").df())
    if i % 250 == 0: print(f"[{time.time()-T0:6.1f}s] {i+1}/{len(days)} {d}", flush=True)
X = pd.concat(parts, ignore_index=True); X.to_parquet("data/equity/gapfader/slope30.parquet")
print(f"[{time.time()-T0:6.1f}s] wrote {len(X):,} rows", flush=True)
