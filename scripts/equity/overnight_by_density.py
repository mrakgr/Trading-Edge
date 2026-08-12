"""S43bt — the overnight reversal, sliced by LAST-HOUR TAPE DENSITY.

S43bq only looked at three slices: the full universe, the top ~3% by density
(nbars_lh >= 3000) and the bottom ~3% (< 600). That showed the LONG side (buy
after a last-hour decline) is a LIQUIDITY effect that INVERTS on thin tape — but
it never located the cutoff. This finds it.

  density = `nbars_lh`, the number of 1s bars PRESENT in 15:00-16:00, i.e. how
  many of the hour's 3,600 seconds actually traded. High = continuous tape.
  It is the same quantity `gap_60` measures, counted as presence not absence.

  overnight = (open_p1 + div_p1) / close_d - 1, straight off mr_candidate_1s_v2.
  ⭐ Reconciled against the independent hand-rolled S43bq construction:
  1,420,585 of 1,420,586 rows agree to 1e-9 (the one exception is GSK
  2022-07-18, where a 5:4 consolidation executed inside a 3-session gap — the
  `n` column gets it right via ASOF, the old script missed it).

MEDIAN is the headline throughout: the raw mean is unusable (a handful of rows
carry corporate actions the splits table does not describe), and the median is
what a per-trade sizing decision actually faces.

Usage:  python scripts/equity/overnight_by_density.py
"""
import duckdb
import numpy as np
import pandas as pd

pd.set_option("display.width", 260)
pd.set_option("display.max_columns", 60)

con = duckdb.connect()
con.execute("SET enable_progress_bar=false")
con.execute("ATTACH 'data/trading.db' AS db (READ_ONLY)")
LH = "read_parquet('data/equity/flushfader/lasthour_cache.parquet')"

con.execute(f"""CREATE OR REPLACE TEMP TABLE X AS
SELECT l.ticker, l.date, l.lh_chg, l.nbars_lh, l.dv_0945_tape, v.close_d,
       (v.open_p1 + v.div_p1)/nullif(v.close_d,0) - 1 AS ovn
FROM {LH} l JOIN db.mr_candidate_1s_v2 v ON v.ticker=l.ticker AND v.date=l.date
WHERE v.open_p1 IS NOT NULL AND v.close_d > 0""")
N = con.execute("SELECT count(*) FROM X").fetchone()[0]
print(f"population: {N:,} universe ticker-days with a next-session open\n")

print("=== the density axis: what `top X%` actually means ===")
q = con.execute("""SELECT
  quantile_cont(nbars_lh, 0.50) p50, quantile_cont(nbars_lh, 0.75) p75,
  quantile_cont(nbars_lh, 0.90) p90, quantile_cont(nbars_lh, 0.95) p95,
  quantile_cont(nbars_lh, 0.97) p97, quantile_cont(nbars_lh, 0.99) p99,
  max(nbars_lh) mx FROM X""").fetchdf()
print("  seconds traded of 3,600 — " +
      "  ".join(f"top {100-int(c[1:])}% >= {int(v)}" for c, v in
                zip(q.columns[:-1], q.iloc[0][:-1])) + f"   max {int(q.mx[0])}")

# ⭐ CUMULATIVE bands: "the top X% most continuously traded", which is the form
# the sizing question actually takes ("only trade names at least this liquid").
CUTS = [1, 3, 5, 10, 25, 50, 100]
DOWN = [("<-6%", "lh_chg < -0.06"), ("-6..-4", "lh_chg >= -0.06 AND lh_chg < -0.04"),
        ("-4..-3", "lh_chg >= -0.04 AND lh_chg < -0.03"),
        ("-3..-2", "lh_chg >= -0.03 AND lh_chg < -0.02"),
        ("-2..-1", "lh_chg >= -0.02 AND lh_chg < -0.01")]
UP = [("+4..+6", "lh_chg >= 0.04 AND lh_chg < 0.06"), (">+6%", "lh_chg >= 0.06")]


def band_table(buckets, title):
    print(f"\n{'='*150}\n{title}\n"
          f"median overnight % (win%), by 'top X% most continuously traded'\n{'='*150}")
    rows = []
    for pct in CUTS:
        thr = con.execute(
            f"SELECT quantile_cont(nbars_lh, {1 - pct/100.0}) FROM X").fetchone()[0]
        r = {"top%": f"top {pct}%", "nbars>=": int(thr)}
        for name, cond in buckets:
            d = con.execute(f"""SELECT count(*) n, median(ovn)*100 med,
                avg(CASE WHEN ovn>0 THEN 1.0 ELSE 0 END)*100 w
                FROM X WHERE nbars_lh >= {thr} AND {cond}""").fetchdf()
            n = int(d.n[0])
            r[name] = (f"{d.med[0]:+.3f} ({d.w[0]:.0f}%) n={n:,}" if n >= 30
                       else f"n={n} (thin)")
        rows.append(r)
    print(pd.DataFrame(rows).to_string(index=False))


band_table(DOWN, "⭐ THE LONG SIDE — buy the close after a last-hour DECLINE")
band_table(UP, "THE SHORT SIDE — sell the close after a last-hour RALLY")

print(f"\n{'='*150}\nDECILES (non-cumulative) — where exactly does the long side flip sign?"
      f"\n{'='*150}")
rows = []
for lo in range(0, 100, 10):
    a = con.execute(f"SELECT quantile_cont(nbars_lh, {lo/100.0}) FROM X").fetchone()[0]
    b = con.execute(f"SELECT quantile_cont(nbars_lh, {(lo+10)/100.0}) FROM X").fetchone()[0]
    r = {"decile": f"{lo}-{lo+10}%", "nbars": f"{int(a)}-{int(b)}"}
    for name, cond in DOWN[:4]:
        d = con.execute(f"""SELECT count(*) n, median(ovn)*100 med
            FROM X WHERE nbars_lh >= {a} AND nbars_lh < {b} AND {cond}""").fetchdf()
        r[name] = f"{d.med[0]:+.3f}" if int(d.n[0]) >= 30 else "."
    d = con.execute(f"""SELECT count(*) n, median(ovn)*100 med
        FROM X WHERE nbars_lh >= {a} AND nbars_lh < {b} AND lh_chg >= 0.06""").fetchdf()
    r[">+6%"] = f"{d.med[0]:+.3f}" if int(d.n[0]) >= 30 else "."
    rows.append(r)
print(pd.DataFrame(rows).to_string(index=False))
print("  (median overnight %; '.' = fewer than 30 observations)")
