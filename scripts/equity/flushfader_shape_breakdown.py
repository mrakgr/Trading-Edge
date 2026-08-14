"""S43cd — FlushFader PERSISTENCE and INTENSITY breakdowns, with and without the g60 door.

⭐ USER (2026-08-14): "I'd like to see a breakdown for both inten and pers features,
with and without gap_60 < 4."

The two features, both measured against the SAME DAY's opening 15 minutes:

    pers_N  = ((N - gap_N)/N) / (n_bars_1s/900)
              continuity now vs at the open. gap_N is WALL-CLOCK (GapCounter keeps
              present seconds in (t-N, t] and reports span - count), so N - gap_N is
              exactly the bars in the last N seconds. 1.0 = as continuous as the open.

    inten_N = (dollar_vol_N/N) / (dv_0945_tape/n_bars_1s)
              dollars per PRESENT bar now vs at the open. dollar_vol_N is a SumMa over
              N PRESENT bars, so dividing the open by its own bar count keeps both
              sides on one clock. 1.0 = each active second moves the same dollars.

⚠ THE TWO CLOCKS DIFFER and must not be blended. Dividing the open by 900 wall-clock
seconds instead gives `inten x (n_bars_1s/900)` — intensity times the open's own
density — and it measured WORSE at every window (2.502/2.242/2.287 vs 2.764/2.567/
2.683). Kept apart, the two features are near-orthogonal and stack.

⚠ BANDS ARE QUANTILES OF THE POPULATION SHOWN, so the two populations' bands are NOT
the same cut. Each table prints the band's actual value range, because a live spec
needs an ABSOLUTE threshold — a per-day cross-sectional rank would be lookahead.

⚠ `dv_0945_tape` is also the universe gate (>= $2M), so `inten` shares a term with it.
`pers` does not. Worth remembering before either is called independent of the universe.

Usage:  python scripts/equity/flushfader_shape_breakdown.py [--bands 10]
"""
import argparse
import os
import sys

import duckdb
import numpy as np
import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from flushfader_common import raw_px_expr

pd.set_option("display.width", 250)
pd.set_option("display.max_columns", 60)

ap = argparse.ArgumentParser()
ap.add_argument("--trips", default="data/equity/flushfader/v45_nextopen/trips_p*.parquet")
ap.add_argument("--db", default="data/trading.db")
ap.add_argument("--bands", type=int, default=10)
args = ap.parse_args()

con = duckdb.connect(config={"memory_limit": "3GB", "threads": 4})
con.execute("SET enable_progress_bar=false")
con.execute(f"ATTACH '{args.db}' AS db (READ_ONLY)")
RAWPX, SCHEMA = raw_px_expr(con, args.trips)

con.execute(f"""CREATE OR REPLACE TEMP TABLE T AS
SELECT t.symbol, t.trade_date, t.signal_sec, t.entry_sec, t.exit_sec,
       t.ret_exit AS r, year(t.trade_date::DATE) AS yr, t.gap_60,
       ((60.0   - t.gap_60)  /   60.0) / nullif(u.n_bars_1s/900.0, 0) AS pers_60,
       ((300.0  - t.gap_300) /  300.0) / nullif(u.n_bars_1s/900.0, 0) AS pers_300,
       ((1200.0 - t.gap_1200)/ 1200.0) / nullif(u.n_bars_1s/900.0, 0) AS pers_1200,
       (t.dollar_vol_60  /  60.0) / nullif(u.dv_0945_tape/u.n_bars_1s, 0) AS inten_60,
       (t.dollar_vol_300 / 300.0) / nullif(u.dv_0945_tape/u.n_bars_1s, 0) AS inten_300,
       (t.dollar_vol_1200/1200.0) / nullif(u.dv_0945_tape/u.n_bars_1s, 0) AS inten_1200
FROM read_parquet('{args.trips}') t
JOIN db.mr_candidate_1s_v2 u ON u.ticker = t.symbol AND u.date = t.trade_date::DATE
WHERE {RAWPX} >= 1 AND u.n_bars_1s > 0 AND u.dv_0945_tape > 0
ORDER BY t.symbol, t.trade_date, t.signal_sec""")

F = con.execute("SELECT * FROM T").fetchdf()
keep, last, prev = np.zeros(len(F), bool), -1, None
key = (F.symbol + "_" + F.trade_date.astype(str)).values
for i in range(len(F)):
    if key[i] != prev:
        prev, last = key[i], -1
    if F.entry_sec.values[i] >= last:
        keep[i] = True
        last = F.exit_sec.values[i]
F = F[keep].reset_index(drop=True)
print(f"trades after mc=1: {len(F):,}   ({SCHEMA})")

POPS = [("ALL four segments", F),
        ("g60 door: gap_60 < 4", F[F.gap_60 < 4].reset_index(drop=True))]
FEATS = ["pers_60", "pers_300", "pers_1200", "inten_60", "inten_300", "inten_1200"]
YRS = sorted(F.yr.unique())


def pf(x):
    g, l = x[x > 0].sum(), -x[x < 0].sum()
    return float("inf") if l == 0 else g / l


print(f"\n{'='*200}")
print(f"§1 QUINTILE SUMMARY — PF by feature quintile, both populations")
print(f"{'='*200}")
rows = []
for pname, P in POPS:
    for f in FEATS:
        v, r = P[f].values, P.r.values
        ok = ~np.isnan(v)
        v, r = v[ok], r[ok]
        qs = np.quantile(v, [0, .2, .4, .6, .8, 1.0])
        row = {"population": pname, "feature": f, "n": f"{len(v):,}"}
        for i in range(5):
            m = (v >= qs[i]) & (v <= qs[i + 1] if i == 4 else v < qs[i + 1])
            row[f"Q{i+1}"] = f"{pf(r[m]):.2f} ({m.sum():,})" if m.sum() >= 30 else "."
        row["Q5/Q1"] = ""
        rows.append(row)
print(pd.DataFrame(rows).to_string(index=False))
print("  Q1 = lowest quintile of the feature, Q5 = highest. Monotone Q1->Q5 = a real gradient.")

for pname, P in POPS:
    for f in ("inten_60", "pers_1200"):
        v, r, y = P[f].values, P.r.values, P.yr.values
        ok = ~np.isnan(v)
        v, r, y = v[ok], r[ok], y[ok]
        qs = np.quantile(v, np.linspace(0, 1, args.bands + 1))
        print(f"\n{'='*200}\n⭐ {f}   |   {pname}   |   n = {len(v):,}\n{'='*200}")
        rows = []
        for i in range(args.bands):
            lo, hi = qs[i], qs[i + 1]
            m = (v >= lo) & (v <= hi if i == args.bands - 1 else v < hi)
            if m.sum() < 20:
                continue
            row = {"band": f"D{i+1}", "range": f"[{lo:.2f}, {hi:.2f})",
                   "n": f"{m.sum():,}", "PF": f"{pf(r[m]):.2f}",
                   "mean%": f"{r[m].mean()*100:+.2f}",
                   "win%": f"{(r[m]>0).mean()*100:.0f}",
                   "worst%": f"{r[m].min()*100:.0f}"}
            for yy in YRS:
                mm = m & (y == yy)
                row[str(yy)] = f"{pf(r[mm]):.2f}" if mm.sum() >= 10 else "."
            rows.append(row)
        print(pd.DataFrame(rows).to_string(index=False))
        print("  ('.' = fewer than 10 trades in that year-band cell)")
