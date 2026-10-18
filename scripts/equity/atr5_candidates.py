"""The 5-DAY ATR % of every ticker-day of the research universe (mr_candidate_1s_v2) — FlushFader's ATR5 gate input
(user 2026-10-18: no FlushFader entry at ATR5 >= 400 %, after WFF 10-14; docs/flushfaderv2_results.md V2-12/V2-13).

    TR_t  = max(H_t, C_{t-1}) - min(L_t, C_{t-1})       in the causal P·n scale (docs/price_adjustment.md)
    ATR5% = mean(TR_t over the 5 daily rows BEFORE D) / C of the last row before D     (NaN with < 6 prior rows)
The rows are the ticker's daily_adjusted rows, whatever their gaps (no episode split). Knowable before D's open.
The live twin is TradingEdge.Oms Reference.atr5s (pinned to this file by ReferenceTests).
Run from research/:  python -u scripts/equity/atr5_candidates.py > data/equity/atr5/candidates_atr5.log 2>&1; echo exit $?
"""
import os, time
import duckdb
T0 = time.time()
OUT = "data/equity/atr5/candidates_atr5.parquet"
os.makedirs(os.path.dirname(OUT), exist_ok=True)
con = duckdb.connect(); con.execute("SET memory_limit='16GB'"); con.execute("SET threads=8"); con.execute("SET preserve_insertion_order=false")
con.execute(f"""
COPY (
  WITH k AS (SELECT DISTINCT ticker, date FROM read_parquet('data/v2_candidates/mr_candidate_1s_v2/*/*.parquet', hive_partitioning = true)),
       a AS (SELECT ticker, date, high * n AS h, low * n AS l, close * n AS c FROM 'data/v2/daily_adjusted.parquet'
             WHERE ticker IN (SELECT DISTINCT ticker FROM k)),
       -- ⚠ DuckDB's greatest / least SKIP a NULL: a row without a previous close has NO true range (not H - L)
       t AS (SELECT ticker, date, c,
                    CASE WHEN lag(c) OVER w IS NOT NULL THEN greatest(h, lag(c) OVER w) - least(l, lag(c) OVER w) END AS tr
             FROM a WINDOW w AS (PARTITION BY ticker ORDER BY date)),
       p AS (SELECT ticker, date, avg(tr) OVER w5 AS atr, count(tr) OVER w5 AS n_tr, lag(c) OVER w1 AS c_prior
             FROM t WINDOW w5 AS (PARTITION BY ticker ORDER BY date ROWS BETWEEN 5 PRECEDING AND 1 PRECEDING),
                           w1 AS (PARTITION BY ticker ORDER BY date))
  SELECT k.ticker, k.date, CASE WHEN p.n_tr = 5 THEN p.atr / p.c_prior END AS atr5
  FROM k LEFT JOIN p USING (ticker, date) ORDER BY k.ticker, k.date
) TO '{OUT}' (FORMAT PARQUET)""")
n, nn, big = con.execute(f"SELECT count(*), count(*) FILTER (WHERE atr5 IS NULL), count(*) FILTER (WHERE atr5 >= 4) FROM '{OUT}'").fetchone()
print(f"[{time.time()-T0:5.1f}s] wrote {OUT}: {n:,} ticker-days, {nn:,} without 5 prior sessions, {big:,} at ATR5 >= 400 %")
