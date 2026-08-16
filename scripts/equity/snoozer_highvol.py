"""S43cy — the `volat_open30 >= 100bp` region: huge mean, ruinous tail. Can gaps fix it?

⭐ USER (2026-08-16): "Let's look at the volat >= 100bp region next."

This is the third and last volatility region. §S43cs showed it is the OTHER good half of
the bimodal relation — [150,190) PF 3.165 and [240,∞) 3.801 on `gaps >= 1000 ∧ chg > +6%`
— and §S43cw's reference row showed why it has never been usable:

    volat >= 150bp, chg > +8%:  n=560  PF 1.964  mean +6.44%  MEDIAN +11.02%  win 78%
                                worst-5% -90.0%  WORST -245%

**The highest expectancy in the entire system and the worst tail in the entire system,
in the same population.** A +11% median with a −245% worst trade is the classic short
payoff: right most nights, ruined occasionally.

## The prediction being tested

§S43cw established that **the gap threshold required rises with volatility**:

    volat < 40bp        works from gaps >= 500,  optimum 1500-1750
    volat [40,100)bp    dead below 2000, tradeable from gaps >= 2000

If that is a real structure rather than two coincidences, `volat >= 100bp` should need a
threshold **higher still** — and the payoff would be taming the worst tail in the book.
If instead the high-volatility region is tradeable at any gap level, the "rising
threshold" story is wrong and §S43cw needs revisiting.

⚠ 2020 control on every row. ⚠ Absolute gap bands, not quantiles.
⚠ The region is split into sub-bands because §S43cs showed it is NOT uniform inside
([120,150) 2.012 against [150,190) 3.165 against [190,240) 1.939).

Usage:  python scripts/equity/snoozer_highvol.py [--chg 0.08]
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
ap.add_argument("--chg", type=float, default=0.08)
ap.add_argument("--boot", type=int, default=2000)
ap.add_argument("--seed", type=int, default=20260816)
args = ap.parse_args()

con = duckdb.connect(config={"memory_limit": "8GB", "threads": 8})
con.execute("SET enable_progress_bar=false")
con.execute(f"""CREATE OR REPLACE TEMP TABLE S AS
SELECT s.ticker, s.date, year(s.date) AS yr, -(s.ovn_from_lim59) AS r,
       3540 - s.nb60k59 AS gaps, s.chg60k59 AS chg, s.close_d, s.dv_lh,
       s.dv_over_open30 AS inten, s.bar_over_open30 AS pers, v.volat_open30 AS volat
FROM read_parquet('{args.shape}') s
JOIN read_parquet('{args.volat}') v ON v.ticker = s.ticker AND v.date = s.date
WHERE s.chg60k59 > {args.chg} AND s.ovn_from_lim59 IS NOT NULL AND s.dv_lh > 0
  AND s.dv_over_open15 IS NOT NULL""")
A = con.execute("SELECT * FROM S").fetchdf()
r = A.r.values
yrs = sorted(A.yr.unique())
rng = np.random.default_rng(args.seed)
v = A.volat.values * 1e4
g = A.gaps.values.astype(float)
y20 = A.yr.values == 2020


def pf(x):
    a, b = x[x > 0].sum(), -x[x < 0].sum()
    return float("inf") if b == 0 else a / b


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
         "WORST%": round(x.min()*100),
         ">100% loss": int((x < -1).sum()),
         "loss%": round((x < 0).mean()*100), "yrs<1": len(neg),
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


HI = v >= 100
print(f"THE HIGH-VOLATILITY REGION:  volat_open30 >= 100bp,  chg > {args.chg*100:g}%")
print(pd.DataFrame([line(HI, "the region"),
                    line((v >= 40) & (v < 100), "  (ref) [40,100)bp"),
                    line(v < 40, "  (ref) < 40bp")]).fillna("").to_string(index=False))

print(f"\n{'='*235}\n⭐ §1 GAP BANDS inside volat >= 100bp — does the threshold rise "
      f"again?\n{'='*235}")
rows = []
for lo, hi in ((0, 500), (500, 1000), (1000, 1500), (1500, 2000), (2000, 2500),
               (2500, 3000), (3000, 3541)):
    m = HI & (g >= lo) & (g < hi)
    if m.sum() < 20:
        continue
    rows.append(line(m, f"gaps [{lo}, {hi})", base=HI))
print(pd.DataFrame(rows).fillna("").to_string(index=False))

print(f"\n{'='*235}\n⭐⭐ §2 CUMULATIVE FLOOR — where does the tail become "
      f"survivable?\n{'='*235}")
rows = []
for T in (0, 1000, 1500, 2000, 2500, 2800, 3000, 3200):
    m = HI & (g >= T)
    if m.sum() < 25:
        continue
    rows.append(line(m, f"gaps >= {T}", base=HI))
print(pd.DataFrame(rows).fillna("").to_string(index=False))

print(f"\n{'='*235}\n§3 IS THE REGION UNIFORM? volatility sub-bands, each with its own "
      f"gap sweep\n{'='*235}")
for vlo, vhi in ((100, 150), (150, 240), (240, 5000)):
    sub = (v >= vlo) & (v < vhi)
    print(f"\n  --- volat [{vlo}, {min(vhi,999)})bp   n={int(sub.sum())}  "
          f"PF {pf(r[sub]):.3f}  mean {r[sub].mean()*100:+.2f}%  "
          f"worst {r[sub].min()*100:.0f}% ---")
    rows = []
    for T in (0, 1500, 2000, 2500, 3000):
        m = sub & (g >= T)
        if m.sum() < 20:
            continue
        rows.append(line(m, f"gaps >= {T}", base=sub))
    print(pd.DataFrame(rows).fillna("").to_string(index=False))

print(f"\n{'='*235}\n§4 ANYTHING ELSE? the other features inside the best gap cut\n"
      f"{'='*235}")
BEST = HI & (g >= 2500)
rows = [line(BEST, "volat>=100 x gaps>=2500")]
for col, lab in (("inten", "intensity 30m"), ("pers", "persistence 30m"),
                 ("volat", "volat_open30"), ("close_d", "price"),
                 ("dv_lh", "last-hour dollars")):
    vv = A[col].values.astype(float)
    t = np.nanquantile(vv[BEST], .5)
    for op in ("<=", ">"):
        m = BEST & ((vv <= t) if op == "<=" else (vv > t)) & ~np.isnan(vv)
        rows.append(line(m, f"  {lab} {op} median", base=BEST))
print(pd.DataFrame(rows).fillna("").to_string(index=False))
