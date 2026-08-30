"""S43ce — is `inten_60` a SIZING lever on the A++ book?

⭐ USER (2026-08-14): "I am quite curious to see if the inten_60 could act as a sizing
lever on the A++ book in general."

Context. `inten_60` failed twice as a MEMBERSHIP rule:
  * OR-ed into the A/B tier door it was neutral-to-noise (+74.8% -> +74.7%/+77.0%/
    +69.4%/+67.7% across thresholds q80-q95 — non-monotone, straddling baseline);
  * as a 9th voice it FAILED leave-one-out (dPF +0.137, the worst seat in the roster
    bar `dslo`), and its admitted trades are A+ segment trades that the widening
    decision already takes anyway.

Neither of those tested it as a CONTINUOUS SIZE signal, which is a different question:
not "is this trade in the book" but "given it is in, how much of it should we hold".

    inten_60 = (dollar_vol_60/60) / (dv_0945_tape/n_bars_1s)

dollars per PRESENT bar over the last 60 present bars vs the same over the opening 15
minutes. Both sides on the present-bar clock (S43cd).

## ⚠⚠ THE TEST MUST BE EXPOSURE-NEUTRAL

A size ladder with a larger average multiplier earns more simply by holding more, which
says nothing about ALLOCATION. Every variant below is therefore rescaled so its MEAN
POSITION SIZE equals the baseline's. Any difference in account return is then
attributable to putting the size in better places, not to using more of it.

⚠ Multipliers are the adopted metric: trimmed PF-1 on VOL-NORMALISED returns (`rn`),
never empirical Kelly (which pins at 1/|worst kept loss| at any trim depth).

⚠ Cell counts get thin fast — 1,325 trades over 4 tiers x 3 bands is ~110 a cell. A
ladder fitted on 110 trades and then evaluated on the same 110 is IN-SAMPLE by
construction; §4 reports a per-year breakdown so a ladder that only works in one
regime is visible.

Usage:  python scripts/equity/flushfader_inten_sizing.py
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
ap.add_argument("--trips", default="data/equity/flushfader/v49_spec20/trips_p*.parquet")
ap.add_argument("--esf", type=int, default=450)
ap.add_argument("--base", type=float, default=0.10)
ap.add_argument("--trim", type=float, default=0.05)
ap.add_argument("--bands", type=int, default=3)
args = ap.parse_args()

con = duckdb.connect(config={"memory_limit": "3GB", "threads": 4})
con.execute("SET enable_progress_bar=false")
RAWPX, SCHEMA = raw_px_expr(con, args.trips)
VOICES = ["volat_20m*1e4 >= 140",
          "(signal_vwap/first_low_vwap)*(1+d_hi_flow) - 1 < -0.28",
          "signal_vwap/sess_low - 1 >= 0.08",
          "(volat_slope_10m - volat_slope_20m)*2e4 > 12",
          "volat_slope_5m*2e4 <= -24",
          "ac1_ewma < -0.1",
          f"secs_since_first_low >= 0 AND secs_since_first_low <= {args.esf}",
          "downticks_since_uptick >= 8",
          "secs_since_halt >= 1200 AND secs_since_halt < 4800",
          "halts_today >= 1 AND secs_since_halt >= 120 AND secs_since_halt < 1200"]
voice = " OR ".join(f"COALESCE({e}, false)" for e in VOICES)

F = con.execute(f"""
SELECT symbol, trade_date, signal_sec, entry_sec, exit_sec, ret_exit AS r,
       ret_exit*sqrt(99.0/(volat_20m*1e4)) AS rn, volat_20m,
       year(trade_date::DATE) AS yr, inten_60,
       CASE WHEN gap_adj_1200<15 AND ols_slope_60*6e5<=-350 THEN 'A'
            WHEN gap_adj_1200<15 THEN 'B'
            WHEN ols_slope_60*6e5<=-350 THEN 'C' ELSE 'D' END AS tier
FROM read_parquet('{args.trips}')
WHERE {RAWPX} >= 1 AND gap_60 < 4 AND volat_20m >= 0.004 AND signal_sec <= 54000 AND lows_since_first_low_180 >= 3 AND ({voice})
  AND volat_20m > 0 AND inten_60 IS NOT NULL
ORDER BY symbol, trade_date, signal_sec""").fetchdf()
keep, last, prev = np.zeros(len(F), bool), -1, None
key = (F.symbol + "_" + F.trade_date.astype(str)).values
for i in range(len(F)):
    if key[i] != prev:
        prev, last = key[i], -1
    if F.entry_sec.values[i] >= last:
        keep[i] = True
        last = F.exit_sec.values[i]
F = F[keep].reset_index(drop=True)
print(f"A++ book: {len(F):,} trades   ({SCHEMA})\n")


def pf(x):
    g, l = x[x > 0].sum(), -x[x < 0].sum()
    return float("inf") if l == 0 else g / l


def tpf1(x):
    if len(x) < 20:
        return float("nan")
    return pf(x[x >= np.quantile(x, args.trim)]) - 1


MU = dict(zip("ABCD", [2.44, 1.80, 1.14, 1.00]))
volf = np.sqrt(99.0 / (F.volat_20m.values * 1e4))
tierv = F.tier.values
edges = np.quantile(F.inten_60.values, np.linspace(0, 1, args.bands + 1))
band = np.clip(np.searchsorted(edges, F.inten_60.values, side="right") - 1,
               0, args.bands - 1)

print("=" * 130)
print(f"§1 inten_60 ALONE as a ladder ({args.bands} bands)")
print("=" * 130)
rows = []
im = {}
for b in range(args.bands):
    m = band == b
    im[b] = tpf1(F.rn.values[m])
    rows.append({"band": f"I{b+1}", "range": f"[{edges[b]:.2f}, {edges[b+1]:.2f})",
                 "n": int(m.sum()), "PF": round(pf(F.r.values[m]), 3),
                 "mean%": round(F.r.values[m].mean()*100, 2),
                 "trimPF-1": round(im[b], 3)})
lo = im[0]
for i, b in enumerate(range(args.bands)):
    rows[i]["mult (I1=1)"] = round(im[b]/lo, 2) if lo == lo and lo != 0 else float("nan")
print(pd.DataFrame(rows).to_string(index=False))

print(f"\n{'='*130}\n§2 DOES IT ADD INSIDE THE TIERS? trimmed PF-1 per (tier x inten band)\n{'='*130}")
rows = []
cell = {}
for t in "ABCD":
    row = {"tier": t}
    for b in range(args.bands):
        m = (tierv == t) & (band == b)
        cell[(t, b)] = tpf1(F.rn.values[m])
        row[f"I{b+1}"] = (f"{cell[(t,b)]:.2f} ({m.sum()})" if m.sum() >= 20
                          else f". ({m.sum()})")
    rows.append(row)
print(pd.DataFrame(rows).to_string(index=False))
print("  ⚠ '.' = fewer than 20 trades; a multiplier from such a cell is not estimable.")

print(f"\n{'='*130}\n⭐ §3 ACCOUNT ECONOMICS — ALL VARIANTS RESCALED TO THE SAME MEAN SIZE\n{'='*130}")
base_mult = np.array([MU[t] for t in tierv])
VARIANTS = [("tier only (incumbent)", base_mult),
            ("inten only", np.array([im[b]/lo if lo else 1.0 for b in band])),
            ("tier x inten (multiplicative)",
             base_mult * np.array([im[b]/lo if lo else 1.0 for b in band])),
            ("tier x inten (2D cells)",
             np.array([cell[(t, b)] if cell[(t, b)] == cell[(t, b)] else np.nan
                       for t, b in zip(tierv, band)]))]
rows = []
base_size = args.base * base_mult * volf
for lbl, mult in VARIANTS:
    mult = np.where(np.isnan(mult), np.nanmedian(mult), mult)
    size = args.base * mult * volf
    # ⭐ EXPOSURE-NEUTRAL: rescale so mean size matches the incumbent exactly.
    size = size * (base_size.mean() / size.mean())
    contrib = size * F.r.values
    eq = np.cumprod(1 + contrib)
    ny = F.yr.nunique()
    dd = (eq/np.maximum.accumulate(eq) - 1).min()*100
    rows.append({"variant": lbl, "acct/yr %": round((eq[-1]**(1/ny)-1)*100, 1),
                 "maxDD %": round(dd, 2),
                 "worst trade %": round(contrib.min()*100, 2),
                 "mean size %": round(size.mean()*100, 2),
                 "max size %": round(size.max()*100, 1)})
print(pd.DataFrame(rows).to_string(index=False))
print("  mean size is EQUAL by construction — any difference is ALLOCATION, not leverage.")

print(f"\n{'='*130}\n§4 PER-YEAR account return, tier-only vs tier x inten\n{'='*130}")
rows = []
for lbl, mult in VARIANTS[:3]:
    mult = np.where(np.isnan(mult), np.nanmedian(mult), mult)
    size = args.base * mult * volf
    size = size * (base_size.mean() / size.mean())
    contrib = size * F.r.values
    row = {"variant": lbl}
    for y in sorted(F.yr.unique()):
        m = F.yr.values == y
        row[str(y)] = f"{(np.prod(1+contrib[m])-1)*100:+.1f}"
    rows.append(row)
print(pd.DataFrame(rows).to_string(index=False))
