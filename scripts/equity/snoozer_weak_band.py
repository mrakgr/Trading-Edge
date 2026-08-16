"""S43cw — can the three features RESCUE the weak volatility band [40, 100)bp?

⭐ USER (2026-08-16): "I want to look at the weak cells where the volat_open30 is in
[40,100) bps. Those had really low PFs. I wonder if the 3 features can rescue it
somehow. Also we should try raising chg60k59 from 6 to 8 and higher for them to see if
that makes a difference."

§S43cs found the volatility relation is BIMODAL with a dead zone in the middle. On
`gaps >= 1000 ∧ chg > +6%`: [40,60) PF 1.208 with FOUR losing years, [60,80) 1.380,
[80,100) 1.857 — against 3.248 below 40bp and 3.165-3.801 above 150bp.

Two ways the dead zone could be dead:

  (a) **DILUTION** — it contains good trades that the three features can find, and the
      band average is low only because nothing has been applied inside it. Then a filter
      should lift it toward the neighbouring bands.
  (b) **GENUINELY DEAD** — this volatility regime has no overnight edge, and filters
      will move PF only as much as random selection does.

The distinguishing test is the bootstrap percentile, not the PF. A filter that takes
1.2 to 2.0 by cutting half the trades has done nothing if a random half does the same.

⚠ 2020 control on every row (§S43ct), and the ex-2020 PF is the number that must hold.
⚠ Each threshold is the median OF THE BAND, not a global quantile (§S43co).

Usage:  python scripts/equity/snoozer_weak_band.py
"""
import argparse

import duckdb
import numpy as np
import pandas as pd

pd.set_option("display.width", 250)
pd.set_option("display.max_columns", 60)

ap = argparse.ArgumentParser()
ap.add_argument("--shape", default="data/equity/flushfader/snoozer_shape.parquet")
ap.add_argument("--volat", default="data/equity/flushfader/snoozer_volat.parquet")
ap.add_argument("--vlo", type=float, default=40.0)
ap.add_argument("--vhi", type=float, default=100.0)
ap.add_argument("--boot", type=int, default=2000)
ap.add_argument("--seed", type=int, default=20260816)
args = ap.parse_args()

con = duckdb.connect(config={"memory_limit": "8GB", "threads": 8})
con.execute("SET enable_progress_bar=false")
con.execute(f"""CREATE OR REPLACE TEMP TABLE S AS
SELECT s.ticker, s.date, year(s.date) AS yr, -(s.ovn_from_lim59) AS r,
       3540 - s.nb60k59 AS gaps, s.chg60k59 AS chg,
       s.dv_over_open30 AS inten, s.bar_over_open30 AS pers, v.volat_open30 AS volat
FROM read_parquet('{args.shape}') s
JOIN read_parquet('{args.volat}') v ON v.ticker = s.ticker AND v.date = s.date
WHERE s.chg60k59 > 0.06 AND s.ovn_from_lim59 IS NOT NULL AND s.dv_lh > 0
  AND s.dv_over_open15 IS NOT NULL""")
A = con.execute("SELECT * FROM S").fetchdf()
r = A.r.values
yrs = sorted(A.yr.unique())
rng = np.random.default_rng(args.seed)
v = A.volat.values * 1e4
y20 = A.yr.values == 2020
BAND = (v >= args.vlo) & (v < args.vhi)


def pf(x):
    g, l = x[x > 0].sum(), -x[x < 0].sum()
    return float("inf") if l == 0 else g / l


def line(m, lbl, base=None):
    x, k = r[m], int(m.sum())
    mx = m & ~y20
    neg = [y for y in yrs
           if (m & (A.yr.values == y)).sum() >= 5
           and pf(r[m & (A.yr.values == y)]) < 1.0]
    d = {"rule": lbl, "n": k, "PF": round(pf(x), 3),
         "mean%": round(x.mean()*100, 2), "med%": round(np.median(x)*100, 2),
         "win%": round((x > 0).mean()*100),
         "p5%": round(np.percentile(x, 5)*100, 1),
         "worst5%": round(np.sort(x)[:max(1, k//20)].mean()*100, 1),
         "WORST%": round(x.min()*100), "loss%": round((x < 0).mean()*100),
         "yrs<1": len(neg),
         "n ex20": int(mx.sum()),
         "PF ex20": round(pf(r[mx]), 3) if mx.sum() >= 15 else np.nan}
    if base is not None:
        rb = r[base]
        nb = len(rb)
        if 20 <= k < nb:
            null = np.array([pf(rb[rng.choice(nb, k, replace=False)])
                             for _ in range(args.boot)])
            null = null[np.isfinite(null)]
            d["pctile"] = round(float((null < pf(x)).mean()*100), 1)
    return d


print(f"THE DEAD ZONE:  volat_open30 in [{args.vlo:g}, {args.vhi:g})bp")
print(f"{'='*225}\n⭐ §1 DOES RAISING THE SIGNAL HELP? (the band alone, no other "
      f"filters)\n{'='*225}")
rows = []
for c in (0.06, 0.08, 0.10, 0.12, 0.15):
    m = BAND & (A.chg.values > c)
    if m.sum() < 25:
        continue
    rows.append(line(m, f"chg > {c*100:g}%"))
# the two neighbouring regions at chg>+8%, for scale
rows.append(line((v < args.vlo) & (A.chg.values > 0.08),
                 f"   (reference) volat < {args.vlo:g}bp, chg > 8%"))
rows.append(line((v >= 150) & (A.chg.values > 0.08),
                 "   (reference) volat >= 150bp, chg > 8%"))
print(pd.DataFrame(rows).fillna("").to_string(index=False))

print(f"\n{'='*225}\n⭐⭐ §2 CAN THE THREE FEATURES RESCUE IT? each at the BAND's own "
      f"median, percentile vs random same-n subsets OF THE BAND\n{'='*225}")
for c in (0.06, 0.08, 0.10):
    base = BAND & (A.chg.values > c)
    if base.sum() < 60:
        continue
    print(f"\n  --- chg > {c*100:g}%   band n = {int(base.sum())}, "
          f"PF {pf(r[base]):.3f}, ex-2020 PF "
          f"{pf(r[base & ~y20]):.3f} ---")
    rows = [line(base, "the band (no filter)")]
    for col, op, lab in (("gaps", ">=", "gaps >= band median"),
                         ("gaps", "<", "gaps <  band median"),
                         ("inten", "<=", "intensity <= band median"),
                         ("inten", ">", "intensity >  band median"),
                         ("pers", "<=", "persistence <= band median"),
                         ("pers", ">", "persistence >  band median")):
        vv = A[col].values.astype(float)
        t = np.nanquantile(vv[base], .5)
        m = base & ((vv >= t) if op == ">=" else (vv < t) if op == "<"
                    else (vv <= t) if op == "<=" else (vv > t))
        rows.append(line(m, f"  {lab}", base=base))
    # the S-tier combination, imported wholesale
    m = base & (A.gaps.values >= 1500)
    rows.append(line(m, "  ⭐ gaps >= 1500 (the S-tier cut)", base=base))
    print(pd.DataFrame(rows).fillna("").to_string(index=False))

print(f"\n{'='*225}\n§3 GAP BANDS INSIDE THE DEAD ZONE — the lever that worked in the "
      f"quiet cell, applied here\n{'='*225}")
for c in (0.06, 0.08):
    base = BAND & (A.chg.values > c)
    print(f"\n  --- chg > {c*100:g}% ---")
    rows = []
    for lo, hi in ((0, 500), (500, 1000), (1000, 1500), (1500, 2000),
                   (2000, 2500), (2500, 3541)):
        m = base & (A.gaps.values >= lo) & (A.gaps.values < hi)
        if m.sum() < 20:
            continue
        rows.append(line(m, f"gaps [{lo}, {hi})", base=base))
    print(pd.DataFrame(rows).fillna("").to_string(index=False))
