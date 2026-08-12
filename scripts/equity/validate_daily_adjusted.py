"""Acceptance tests for `daily_adjusted` (causal forward adjustment, S43br).

Every check compares the table's implied return against an INDEPENDENT raw
calculation, so a bug in the builder cannot hide behind its own arithmetic.

    ret(t1 -> t2) = [ (P2*n2 - P1*n1) + (C2 - C1) ] / (P1*n1)

⚠ Do NOT test `P*n + C` ratios. That fused level is continuous across events
(checked below) but its percentage change is the wealth change of a DAY-ONE
holder, not the return of a trade opened at t1 — it divides by `P1n1 + C1`.
AAPL's 2020-08-31 split reads +3.27% fused vs the true +3.39%.

Usage:  python scripts/equity/validate_daily_adjusted.py
Exit code 0 = all pass.
"""
import sys
import duckdb
import pandas as pd

pd.set_option("display.width", 240)
con = duckdb.connect("data/trading.db", read_only=True)
con.execute("SET enable_progress_bar=false")
fails = []


def check(name, ok, detail=""):
    print(f"  {'PASS' if ok else 'FAIL'}  {name}{('   ' + detail) if detail else ''}")
    if not ok:
        fails.append(name)


def ret(ticker, d1, d2):
    """The table's implied return of a position held from d1's close to d2's close."""
    r = con.execute(f"""
      WITH a AS (SELECT close p, n, cum_div c FROM daily_adjusted
                 WHERE ticker='{ticker}' AND date=DATE '{d1}'),
           b AS (SELECT close p, n, cum_div c FROM daily_adjusted
                 WHERE ticker='{ticker}' AND date=DATE '{d2}')
      SELECT ((b.p*b.n - a.p*a.n) + (b.c - a.c)) / (a.p*a.n) FROM a, b""").fetchone()
    return None if r is None else r[0]


print("=== 1. structural invariants ===")
r = con.execute("""SELECT
  (SELECT count(*) FROM daily_adjusted) n_adj,
  (SELECT count(*) FROM daily_prices)   n_raw,
  sum(CASE WHEN n IS NULL OR n<=0 THEN 1 ELSE 0 END) bad_n,
  sum(CASE WHEN cum_div IS NULL OR cum_div<0 THEN 1 ELSE 0 END) bad_div,
  sum(CASE WHEN NOT isfinite(n) OR NOT isfinite(cum_div) THEN 1 ELSE 0 END) nonfinite
  FROM daily_adjusted""").fetchdf()
check("row-count parity with daily_prices", int(r.n_adj[0]) == int(r.n_raw[0]),
      f"{int(r.n_adj[0]):,}")
check("no n <= 0 / NULL", int(r.bad_n[0]) == 0)
check("no cum_div < 0 / NULL", int(r.bad_div[0]) == 0)
check("no inf / nan", int(r.nonfinite[0]) == 0)
# ⚠ An event date is NOT always a trading day for that ticker (holiday, halt, or a
# multi-week gap in the series), so the step lands on the NEXT available bar. The
# assertion is therefore that every step is explained by an event in (prev_date, date],
# NOT by one exactly on `date` — 533 splits and 8,628 ex-dates fall in such gaps.
check("every n step is explained by a split in (prev_date, date]", con.execute("""
  WITH t AS (SELECT ticker, date, n,
                    lag(n)    OVER w pn, lag(date) OVER w pdate
             FROM daily_adjusted WINDOW w AS (PARTITION BY ticker ORDER BY date))
  SELECT count(*) FROM t WHERE pn IS NOT NULL AND n <> pn
    AND NOT EXISTS (SELECT 1 FROM splits s WHERE s.ticker=t.ticker
                    AND s.execution_date > t.pdate AND s.execution_date <= t.date)
  """).fetchone()[0] == 0)
check("every cum_div step is explained by an ex-date in (prev_date, date]", con.execute("""
  WITH t AS (SELECT ticker, date, cum_div cd,
                    lag(cum_div) OVER w pcd, lag(date) OVER w pdate
             FROM daily_adjusted WINDOW w AS (PARTITION BY ticker ORDER BY date))
  SELECT count(*) FROM t WHERE pcd IS NOT NULL AND cd <> pcd
    AND NOT EXISTS (SELECT 1 FROM dividends d WHERE d.ticker=t.ticker
                    AND d.ex_dividend_date > t.pdate AND d.ex_dividend_date <= t.date)
  """).fetchone()[0] == 0)

print("\n=== 2. known events: table return vs independent raw calculation ===")
# (ticker, d1, d2, expected return, how it is derived independently)
CASES = [
    ("AAPL", "2020-08-28", "2020-08-31", (129.04 * 4) / 499.23 - 1, "4:1 split: close*4 / prior close"),
    ("LU",   "2023-12-14", "2023-12-15", (3.55 / 4) / 0.9527 - 1,   "1:4 reverse: close/4 / prior close"),
    ("VISN", "2026-04-27", "2026-04-28", (9.90 + 10.00) / 19.53 - 1, "$10.00 special: (close+div)/prior"),
    ("LU",   "2024-06-03", "2024-06-04", (2.03 + 2.37) / 4.38 - 1,   "$2.37 dividend: (close+div)/prior"),
]
for tkr, d1, d2, want, how in CASES:
    got = ret(tkr, d1, d2)
    ok = got is not None and abs(got - want) < 1e-9
    check(f"{tkr} {d1}->{d2}", ok, f"table {got*100:+.4f}%  vs  {want*100:+.4f}%   [{how}]")

print("\n=== 3. the n(ex-1) rule on same-day split + ex-dividend collisions ===")
coll = con.execute("""
  WITH sc AS (SELECT ticker, execution_date,
     EXP(SUM(LN(split_ratio)) OVER (PARTITION BY ticker ORDER BY execution_date
         ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)) n_at
     FROM splits WHERE split_ratio>0)
  SELECT count(*) n FROM dividends d
  JOIN sc ON sc.ticker=d.ticker AND sc.execution_date=d.ex_dividend_date""").fetchone()[0]
print(f"  {coll:,} collisions across the splits/dividends tables")
# On a collision date the cum_div STEP must equal cash_amount * n(ex-1), i.e. the
# PREVIOUS row's n -- never the post-split n on that same row.
bad = con.execute("""
  WITH t AS (SELECT ticker, date, n, cum_div,
                    lag(n)       OVER (PARTITION BY ticker ORDER BY date) pn,
                    lag(cum_div) OVER (PARTITION BY ticker ORDER BY date) pcd
             FROM daily_adjusted),
       d AS (SELECT ticker, ex_dividend_date, sum(cash_amount) cash
             FROM dividends GROUP BY 1,2)
  SELECT count(*) FROM t
  JOIN d ON d.ticker=t.ticker AND d.ex_dividend_date=t.date
  JOIN splits s ON s.ticker=t.ticker AND s.execution_date=t.date
  WHERE t.pn IS NOT NULL AND abs((t.cum_div - t.pcd) - d.cash * t.pn) > 1e-6 * greatest(abs(t.cum_div),1)
""").fetchone()[0]
check("every in-range collision scales cash by n(ex-1)", bad == 0,
      f"{bad} rows used the post-split count")

print("\n=== 4. SOURCE-DATA INVENTORY: splits contradicted by the price tape ===")
# A split ratio makes a testable prediction: total value P*n + C should be
# CONTINUOUS across the execution date. Where it jumps, `splits` and `daily_prices`
# disagree -- the ratio is recorded but the tape never split.
# ⚠ This is a defect in the SOURCE tables, NOT in this builder, and it corrupts
# `split_adjusted_prices` at least as badly (TTSH's bogus 3000:1 on 2025-12-16
# divides its ENTIRE prior history down to $0.002147 there). Reported as an
# inventory with a regression baseline rather than a builder pass/fail.
BASELINE_BOGUS = 178   # post-ingest-fix (was 188 when splits/dividends were keyed on the date)
inv = con.execute("""
  WITH t AS (SELECT ticker, date, close*n+cum_div tv,
                    lag(close*n+cum_div) OVER w ptv
             FROM daily_adjusted WINDOW w AS (PARTITION BY ticker ORDER BY date)),
  bad AS (SELECT t.ticker, t.date FROM t
          JOIN splits s ON s.ticker=t.ticker AND s.execution_date=t.date
          WHERE t.ptv > 0 AND abs(t.tv/t.ptv - 1) > 0.5)
  SELECT (SELECT count(*) FROM bad) n_bad,
         (SELECT count(DISTINCT ticker) FROM bad) n_tk,
         (SELECT count(*) FROM splits) n_splits,
         (SELECT count(*) FROM mr_candidate_1s u JOIN bad
            ON bad.ticker=u.ticker AND u.date >= bad.date) universe_tkds_after
  """).fetchdf()
nb, ntk = int(inv.n_bad[0]), int(inv.n_tk[0])
print(f"  {nb} of {int(inv.n_splits[0]):,} splits ({100*nb/int(inv.n_splits[0]):.2f}%) across {ntk} tickers")
print(f"  {int(inv.universe_tkds_after[0]):,} universe ticker-days fall AFTER one of them")
print("  ⚠ pre-existing source defect, present in split_adjusted_prices too — see the doc")
check(f"bogus-split count has not regressed past the {BASELINE_BOGUS} baseline",
      nb <= BASELINE_BOGUS, f"now {nb}")

print("\n=== 5. the S43bq overnight leg reproduces from the table alone ===")
r = con.execute("""
  WITH n AS (SELECT ticker, date, close, n, cum_div,
                    lead(open)    OVER w o1, lead(n) OVER w n1, lead(cum_div) OVER w c1
             FROM daily_adjusted WHERE date >= DATE '2016-01-01'
             WINDOW w AS (PARTITION BY ticker ORDER BY date))
  SELECT count(*) n, median(((o1*n1 - close*n) + (c1 - cum_div)) / (close*n))*100 med
  FROM n WHERE o1 IS NOT NULL AND close > 0""").fetchdf()
print(f"  all close->next-open legs: n {int(r.n[0]):,}   median {r.med[0]:+.4f}%")
v = ret("VISN", "2026-04-27", "2026-04-28")
check("VISN overnight is ~+1.9%, not the -49% the old table implied",
      v is not None and 0.015 < v < 0.025, f"{v*100:+.2f}%")

print("\n" + ("ALL CHECKS PASSED" if not fails else f"FAILED: {fails}"))
sys.exit(1 if fails else 0)
