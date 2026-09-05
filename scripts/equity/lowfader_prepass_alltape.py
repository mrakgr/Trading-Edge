"""LowFader EXPANDED-UNIVERSE prepass (2026-09-05, user): the VOLATILITY mirror ONLY, at 20bp, over the WHOLE tape
(lowfader_alltape_cand: no dv_0945/n_bars floors, no barnum). No liquidity screen of any kind (user: a dv_day screen is
lookahead unless its implied engine gate is always applied — and the illiquid tail is the point of this run); no
session-low mirror either (user: volatility part only). The mirror is engine-implied: volat_20m is an EMA (a convex
combination) of the 30-present-bar slot |ln returns| from 09:30, so max|r| < 20bp on a day means the gate can never
open. Same bar-clock slots as the engine. Output table lowfader_alltape_whitelist (candidate schema).
Run: python3 -u scripts/equity/lowfader_prepass_alltape.py > data/lowfader_prepass_alltape.log 2>&1
"""
import duckdb, os, sys, time
SEC="data/intraday_1s_slim"; OUT="data/lowfader_alltape_whitelist.parquet"; CAND="lowfader_alltape_cand"; VBP=0.0020
c=duckdb.connect("data/trading.db", read_only=True)
c.execute("SET memory_limit='8GB'; SET threads=8")
days=[r[0] for r in c.execute(f"SELECT DISTINCT date FROM {CAND} ORDER BY date").fetchall()]
print(f"{len(days)} candidate days, volat mirror {VBP*1e4:.0f}bp, NO liquidity screen, NO session-low mirror", flush=True)
rows=[]; t0=time.time(); missing=0
Q=f"""
WITH cand AS (SELECT ticker FROM {CAND} WHERE date = ?),
tape AS (
  SELECT ticker, bucket, vwap, volume,
         (row_number() OVER (PARTITION BY ticker ORDER BY bucket) - 1) // 30 AS slot
  FROM read_parquet(?)
  WHERE ticker IN (SELECT ticker FROM cand) AND bucket >= 34200 AND bucket < 57600 AND vwap > 0 AND volume > 0),
slots AS (SELECT ticker, slot, sum(vwap*volume)/sum(volume) AS sv, count(*) AS n FROM tape GROUP BY 1,2),
sr AS (SELECT ticker, abs(ln(sv / lag(sv) OVER (PARTITION BY ticker ORDER BY slot))) AS r FROM slots WHERE n = 30),
vol AS (SELECT ticker FROM sr GROUP BY 1 HAVING max(r) >= {VBP} - 1e-6)
SELECT ticker FROM vol"""
for i,d in enumerate(days):
    path=f"{SEC}/{d}.parquet"
    if not os.path.exists(path): missing+=1; continue
    for (t,) in c.execute(Q,[d,path]).fetchall(): rows.append((t,d))
    if (i+1)%20==0 or i+1==len(days):
        el=time.time()-t0; print(f"  {d}  day {i+1}/{len(days)}  flagged {len(rows):,}  {el:.0f}s  ~{el/(i+1)*(len(days)-i-1)/60:.0f}m left", flush=True)
print(f"done: {len(rows):,} tkd flagged, {missing} days without a tape file, {time.time()-t0:.0f}s", flush=True)
# materialize: pandas -> parquet directly (the DuckDB executemany path took >15 min and ~11 GB on 8.1M rows, 2026-09-05)
import pandas as pd
pd.DataFrame(rows, columns=['ticker','date']).to_parquet(OUT, index=False); print(f"wrote {OUT} ({len(rows):,} rows)", flush=True)
c.close(); wc=duckdb.connect("data/trading.db"); wc.execute("SET memory_limit='6GB'; SET threads=8")
wc.execute("DROP TABLE IF EXISTS lowfader_alltape_whitelist")
wc.execute(f"""CREATE TABLE lowfader_alltape_whitelist AS SELECT m.* FROM {CAND} m JOIN (SELECT DISTINCT ticker, CAST(date AS DATE) AS date FROM read_parquet('{OUT}')) w ON w.ticker = m.ticker AND w.date = m.date""")
n=wc.execute("SELECT count(*), count(DISTINCT ticker), min(date), max(date) FROM lowfader_alltape_whitelist").fetchone(); tot=wc.execute(f"SELECT count(*) FROM {CAND}").fetchone()[0]
incand=wc.execute("SELECT count(*) FROM lowfader_alltape_whitelist w JOIN mr_candidate_1s_v2 m ON m.ticker=w.ticker AND m.date=w.date").fetchone()[0]
wc.execute("CHECKPOINT"); wc.close()
print(f"lowfader_alltape_whitelist: {n[0]:,} tkd ({100*n[0]/tot:.1f}% of {tot:,}), {n[1]:,} tickers, {n[2]}..{n[3]}; of which in the OLD candidate table: {incand:,}", flush=True)
