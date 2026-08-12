"""S43br CONTROL — v43_legtick (adjusted tape) vs v44_causal (raw tape).

CLAUDE.md rule 6: a genuine system is INDIFFERENT to removing a lookahead, or
IMPROVES. A large move in either direction is the finding, not a nuisance.

⭐ THE PREDICTION, stated before looking. The trips should be very nearly
IDENTICAL, because nothing in the signal path actually consumed the adjustment:

  * every intraday feature is either a SAME-DAY RATIO (vwap/vwap, eff, slopes,
    channel breaches — any common factor cancels) or a DOLLAR quantity;
  * dollar quantities were already correct in v43. The S29 fix (2026-07-29) had
    divided volume by the same adj_ratio it multiplied price by, so
    vwap*volume = (raw*r)*(rawvol/r) = real dollars either way;
  * ret_exit = (exit_px - entry_px)/entry_px is a same-day ratio;
  * the universe is unchanged — mr_candidate_1s_v2 has EXACT membership parity
    with mr_candidate_1s (1,431,802 rows, identical per-year counts);
  * the $1 book floor was `entry_px/adj_ratio` before and is plain `entry_px`
    now — the same raw number either way.

So the lookahead was REAL but INERT in the signal path. If this control shows a
large divergence, that prediction is wrong and something else changed — chase it
before accepting the migration.

Residual differences that ARE expected: float rounding (v43 multiplied then
divided by adj_ratio), and any ticker-day whose split correction (S43bs) moved
`n`, which changes recorded entry_px/exit_px levels but not their ratio.

Usage:  python scripts/equity/flushfader_causal_control.py
"""
import duckdb, numpy as np, pandas as pd, warnings

warnings.filterwarnings("ignore")
pd.set_option("display.width", 220)

OLD = "data/equity/flushfader/v43_legtick/trips_p*.parquet"
NEW = "data/equity/flushfader/v44_causal/trips_p*.parquet"
con = duckdb.connect()
con.execute("SET enable_progress_bar=false")

print("=== 1. trip counts ===")
d = con.execute(f"""
SELECT (SELECT count(*) FROM read_parquet('{OLD}')) AS v43_trips,
       (SELECT count(*) FROM read_parquet('{NEW}')) AS v44_trips,
       (SELECT count(DISTINCT symbol||trade_date) FROM read_parquet('{OLD}')) AS v43_tkds,
       (SELECT count(DISTINCT symbol||trade_date) FROM read_parquet('{NEW}')) AS v44_tkds
""").fetchdf()
print(d.T.to_string(header=False))
print(f"  trip delta: {int(d.v44_trips[0]) - int(d.v43_trips[0]):+,}")

print("\n=== 2. trip-level identity on the join key (symbol, trade_date, signal_sec) ===")
j = con.execute(f"""
SELECT count(*) AS matched,
  sum(CASE WHEN abs(o.ret_exit - n.ret_exit) < 1e-12 THEN 1 ELSE 0 END) AS ret_identical,
  sum(CASE WHEN abs(o.ret_exit - n.ret_exit) < 1e-9  THEN 1 ELSE 0 END) AS ret_within_1e9,
  max(abs(o.ret_exit - n.ret_exit)) AS max_ret_diff,
  sum(CASE WHEN o.exit_reason <> n.exit_reason THEN 1 ELSE 0 END) AS exit_reason_differs,
  sum(CASE WHEN o.exit_sec <> n.exit_sec THEN 1 ELSE 0 END) AS exit_sec_differs
FROM read_parquet('{OLD}') o
JOIN read_parquet('{NEW}') n USING (symbol, trade_date, signal_sec)
""").fetchdf()
print(j.T.to_string(header=False))

print("\n=== 3. entry_px: v44 raw vs v43 de-adjusted (should agree) ===")
print(con.execute(f"""
SELECT count(*) AS n,
  sum(CASE WHEN abs(n.entry_px - o.entry_px/o.adj_ratio) < 1e-6 * greatest(n.entry_px,1)
           THEN 1 ELSE 0 END) AS agree,
  max(abs(n.entry_px - o.entry_px/o.adj_ratio)) AS max_abs_diff
FROM read_parquet('{OLD}') o
JOIN read_parquet('{NEW}') n USING (symbol, trade_date, signal_sec)
WHERE o.adj_ratio > 0""").fetchdf().T.to_string(header=False))

print("\n=== 4. trips present in only one run ===")
print(con.execute(f"""
SELECT
 (SELECT count(*) FROM (SELECT symbol, trade_date, signal_sec FROM read_parquet('{OLD}')
   EXCEPT SELECT symbol, trade_date, signal_sec FROM read_parquet('{NEW}'))) AS only_v43,
 (SELECT count(*) FROM (SELECT symbol, trade_date, signal_sec FROM read_parquet('{NEW}')
   EXCEPT SELECT symbol, trade_date, signal_sec FROM read_parquet('{OLD}'))) AS only_v44
""").fetchdf().T.to_string(header=False))

print("\n=== 5. ⭐ open_p1 now rides on every trip (the S43bq question, answerable in-place) ===")
print(con.execute(f"""
SELECT count(*) AS trips, sum(CASE WHEN open_p1 IS NOT NULL THEN 1 ELSE 0 END) AS with_open_p1,
  round(median((open_p1 + div_p1)/close_d - 1)*100, 4) AS median_overnight_pct
FROM read_parquet('{NEW}')""").fetchdf().T.to_string(header=False))
print("\nNow run:  python scripts/equity/flushfader_book.py --trips "
      f"'{NEW}'\n  and compare against the reference 1,318 trades @ PF 4.077.")
