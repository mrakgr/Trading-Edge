"""S43ce — widen the A/B tier door: `gap_adj_1200 < 15  OR  inten_1200 >= T`.

⭐ USER (2026-08-14): "What I find really impressive is the inten_1200 Q5. In the A++
book, it might be worth it to OR it with the gap_1200 < 15 feature... since the trades
are getting sparse, let's just OR it for now."

`gap_adj_1200 < 15` is NOT a book gate — it is the A/B-vs-C/D discriminator in the
SIZING tier:

    A  gap_adj_1200 < 15  AND  ols_slope_60*6e5 <= -350
    B  gap_adj_1200 < 15
    C                          ols_slope_60*6e5 <= -350
    D  neither

So OR-ing widens which trades earn the larger multipliers (A 2.44 / B 1.80 vs C 1.14 /
D 1.00). It changes SIZE, not membership — no trade is added to or removed from the
book, which is why an OR is safe here where a third AND would thin an already sparse
book.

    inten_1200 = (dollar_vol_1200/1200) / (dv_0945_tape/n_bars_1s)

dollars per PRESENT bar over the last 1200 present bars, against the same measure over
the opening 15 minutes. Both sides on the present-bar clock (S43cd — dividing the open
by 900 wall-clock seconds instead measured worse at every window).

⚠ KNOWABILITY: `dollar_vol_1200` is a trailing sum at the signal bar; `dv_0945_tape`
and `n_bars_1s` are fixed at 09:45 and every entry is at or after 09:45. No lookahead.

⚠ THE THRESHOLD MUST BE ABSOLUTE. The Q5 that motivated this was the 80th percentile
of the gated population — a quantile is not a tradeable rule (and a per-day
cross-sectional rank would be outright lookahead). The absolute value is printed and
used.

⚠ Multipliers are RE-DERIVED for each variant, not carried over. Moving trades between
tiers changes what each tier contains, so reusing the incumbent ladder would measure
the old tiers on the new membership.

Usage:  python scripts/equity/flushfader_tier_inten.py [--q 0.80]
"""
import argparse
import os
import sys

import duckdb
import numpy as np
import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from flushfader_common import raw_px_expr

pd.set_option("display.width", 220)
pd.set_option("display.max_columns", 40)

ap = argparse.ArgumentParser()
ap.add_argument("--trips", default="data/equity/flushfader/v47_spec20/trips_p*.parquet")
ap.add_argument("--db", default="data/trading.db")
ap.add_argument("--q", type=float, default=0.80, help="quantile of inten_1200 -> absolute T")
ap.add_argument("--esf", type=int, default=450)
ap.add_argument("--base", type=float, default=0.10)
ap.add_argument("--trim", type=float, default=0.05)
args = ap.parse_args()

con = duckdb.connect(config={"memory_limit": "3GB", "threads": 4})
con.execute("SET enable_progress_bar=false")
con.execute(f"ATTACH '{args.db}' AS db (READ_ONLY)")
RAWPX, SCHEMA = raw_px_expr(con, args.trips)
VOICES = ["volat_20m*1e4 >= 140",
          "(signal_vwap/first_low_vwap)*(1+d_hi_flow) - 1 < -0.28",
          "signal_vwap/sess_low - 1 >= 0.08",
          "(volat_slope_10m - volat_slope_20m)*2e4 > 12",
          "volat_slope_5m*2e4 <= -24",
          f"secs_since_first_low >= 0 AND secs_since_first_low <= {args.esf}",
          "downticks_since_uptick >= 8",
          "secs_since_halt >= 1200 AND secs_since_halt < 4800",
          "halts_today >= 1 AND secs_since_halt >= 120 AND secs_since_halt < 1200"]
voice = " OR ".join(f"COALESCE({e}, false)" for e in VOICES)

con.execute(f"""CREATE OR REPLACE TEMP TABLE T AS
SELECT t.symbol, t.trade_date, t.signal_sec, t.entry_sec, t.exit_sec,
       t.ret_exit AS r, year(t.trade_date::DATE) AS yr, t.volat_20m,
       t.ret_exit*sqrt(99.0/(t.volat_20m*1e4)) AS rn,
       t.gap_adj_1200, t.ols_slope_60,
       (t.dollar_vol_1200/1200.0) / nullif(u.dv_0945_tape/u.n_bars_1s, 0) AS inten_1200
FROM read_parquet('{args.trips}') t
JOIN db.mr_candidate_1s_v2 u ON u.ticker = t.symbol AND u.date = t.trade_date::DATE
WHERE {RAWPX} >= 1 AND gap_60 < 4 AND volat_20m >= 0.004 AND signal_sec <= 54000 AND ({voice})
  AND u.n_bars_1s > 0 AND u.dv_0945_tape > 0 AND t.volat_20m > 0
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

T_ABS = float(np.nanquantile(F.inten_1200.values, args.q))
print(f"A++ book: {len(F):,} trades   ({SCHEMA})")
print(f"⭐ inten_1200 q{args.q:.0%} -> ABSOLUTE THRESHOLD T = {T_ABS:.4f}")
print(f"   (the last 20m moves {T_ABS:.2f}x the dollars per traded second that the "
      f"opening 15m did)\n")

steep = (F.ols_slope_60.values * 6e5) <= -350
tight = F.gap_adj_1200.values < 15
hot = F.inten_1200.values >= T_ABS


def tiers(gapok):
    return np.select([gapok & steep, gapok, steep], ["A", "B", "C"], "D")


def pf(x):
    g, l = x[x > 0].sum(), -x[x < 0].sum()
    return float("inf") if l == 0 else g / l


def tpf1(x, q):
    if len(x) < 20:
        return float("nan")
    return pf(x[x >= np.quantile(x, q)]) - 1


VARIANTS = [("baseline  gap_adj_1200 < 15", tight),
            ("⭐ OR      gap<15 OR inten>=T", tight | hot),
            ("(control) inten>=T ALONE", hot)]

print("=" * 150)
print("§1 TIER MEMBERSHIP AND RE-DERIVED MULTIPLIERS")
print("=" * 150)
rows = []
for lbl, gapok in VARIANTS:
    tv = tiers(gapok)
    m = {t: tpf1(F.rn.values[tv == t], args.trim) for t in "ABCD"}
    row = {"variant": lbl}
    for t in "ABCD":
        row[f"{t} n"] = int((tv == t).sum())
    for t in "ABCD":
        row[f"{t} mult"] = (f"{m[t]/m['D']:.2f}" if m["D"] == m["D"] and m[t] == m[t]
                            else ".")
    rows.append(row)
print(pd.DataFrame(rows).to_string(index=False))
print(f"  mult = trimmed PF-1 on vol-normalised returns, scaled to D = 1.00 "
      f"(bottom {args.trim:.0%} trimmed)")
print(f"  trades moved C/D -> A/B by the OR: {int((hot & ~tight).sum())}")

print(f"\n{'='*150}\n§2 PER-TIER STATS under each variant\n{'='*150}")
rows = []
for lbl, gapok in VARIANTS:
    tv = tiers(gapok)
    for t in "ABCD":
        x = F.r.values[tv == t]
        if len(x) < 20:
            continue
        rows.append({"variant": lbl, "tier": t, "n": len(x), "PF": round(pf(x), 3),
                     "mean%": round(x.mean()*100, 2), "worst%": round(x.min()*100, 1)})
print(pd.DataFrame(rows).to_string(index=False))

print(f"\n{'='*150}\n⭐ §3 ACCOUNT ECONOMICS at base {args.base:.0%} "
      f"(multipliers RE-DERIVED per variant)\n{'='*150}")
rows = []
for lbl, gapok in VARIANTS:
    tv = tiers(gapok)
    m = {t: tpf1(F.rn.values[tv == t], args.trim) for t in "ABCD"}
    if m["D"] != m["D"]:
        continue
    mult = np.array([m[t]/m["D"] if m[t] == m[t] else 1.0 for t in tv])
    size = args.base * mult * np.sqrt(99.0/(F.volat_20m.values*1e4))
    contrib = size * F.r.values
    eq = np.cumprod(1 + contrib)
    ny = F.yr.nunique()
    dd = (eq/np.maximum.accumulate(eq) - 1).min()*100
    rows.append({"variant": lbl, "trades": len(F), "PF": round(pf(F.r.values), 3),
                 "acct/yr %": round((eq[-1]**(1/ny)-1)*100, 1),
                 "maxDD %": round(dd, 1),
                 "worst trade on acct %": round(contrib.min()*100, 2),
                 "max size %": round(size.max()*100, 1)})
# ⚠ same trades in every row — only the SIZING differs, so PF is identical by
# construction and the comparison lives entirely in acct/yr and drawdown.
print(pd.DataFrame(rows).to_string(index=False))
print("  ⚠ the book is IDENTICAL across variants; only tier assignment (=size) moves.")
