"""S43cq — RELATIVE GAP counts vs RELATIVE BAR counts as the persistence feature.

⭐ USER (2026-08-16): "Instead of using bar counts, it might be better to use relative
gap counts as the persistence feature in the short system since it prefers absolute
high gap counts. Gap counts and bar count ratios don't measure the same thing and it
might be better to use gap count ratios."

## Why these are genuinely different, not a reparameterisation

Let `b` be the fraction of seconds that traded in a window, so the gap fraction is
`1 − b`. Then

    bar_over_openW = b_lh / b_open           <- ratio of PRESENCE rates
    gap_over_openW = (1−b_lh) / (1−b_open)   <- ratio of ABSENCE rates

A ratio of `b`s and a ratio of `1−b`s are NOT monotone transforms of one another, and
they have opposite resolution:

  * where both windows are near-continuous (`b → 1`), the BAR ratio saturates at 1 and
    cannot discriminate, while the GAP ratio has unbounded dynamic range;
  * where both are thin (`b → 0`), the GAP ratio saturates and the BAR ratio resolves.

⭐ **The short system selects THIN tape** (`gaps ≥ 1831s` of 3540, i.e. `b_lh ≈ 0.48`)
and its opening windows are typically far denser (median 486 gaps of 1800, `b ≈ 0.73`).
So the short lives in the region where the gap ratio should have the better resolution
— exactly the user's argument. The long system sits at the other end.

## ⚠ THE DENOMINATOR DEGENERATES AND MUST BE SMOOTHED

**155 of 4,084 short ticker-days (3.8%) have a ZERO-gap opening 30m** (252 of 4,164 on
the long side), so a raw gap ratio is undefined or infinite there. Dropping them would
silently delete the most continuously-traded names — precisely the squeeze candidates
§S43bv warns about. Instead both sides get **+1 Laplace smoothing**:

    gap_over_openW = ((gapsLh + 1)/3541) / ((gapsOpenW + 1)/(W + 1))

With a median of 486 opening gaps the correction is ~0.2% on ordinary rows and only
binds where it must.

⚠ The last-hour window is the **k59** one (15:00–15:59, 3540s, `gaps = 3540 − nb60k59`)
to match the existing absolute `gaps` feature and the k59 limit entry. The opening
windows are wall-clock.

Usage:  python scripts/equity/snoozer_gap_ratio.py [--side short]
"""
import argparse
import itertools

import duckdb
import numpy as np
import pandas as pd

pd.set_option("display.width", 260)
pd.set_option("display.max_columns", 60)

ap = argparse.ArgumentParser()
ap.add_argument("--shape", default="data/equity/flushfader/snoozer_shape.parquet")
ap.add_argument("--volat", default="data/equity/flushfader/snoozer_volat.parquet")
ap.add_argument("--side", choices=["short", "long"], default="short")
ap.add_argument("--chg", type=float, default=0.06)
ap.add_argument("--boot", type=int, default=1500)
ap.add_argument("--seed", type=int, default=20260816)
args = ap.parse_args()

sign = "-" if args.side == "short" else ""
cond = f"chg60k59 > {args.chg}" if args.side == "short" else f"chg60k59 < {-args.chg}"

con = duckdb.connect(config={"memory_limit": "8GB", "threads": 8})
con.execute("SET enable_progress_bar=false")
con.execute(f"""CREATE OR REPLACE TEMP TABLE S AS
SELECT s.ticker, s.date, year(s.date) AS yr, {sign}(s.ovn_from_lim59) AS r,
       3540 - s.nb60k59 AS gaps, s.dv_over_open30 AS inten,
       s.bar_over_open15, s.bar_over_open30, s.bar_over_open60,
       -- ⭐ ratio of ABSENCE rates, +1 smoothed on both sides
       ((3540 - s.nb60k59 + 1)/3541.0) / (((900  - (s.nbO5+s.nbO15)) + 1)/901.0)
           AS gap_over_open15,
       ((3540 - s.nb60k59 + 1)/3541.0) / (((1800 - (s.nbO5+s.nbO15+s.nbO30)) + 1)/1801.0)
           AS gap_over_open30,
       ((3540 - s.nb60k59 + 1)/3541.0) / (((3600 - (s.nbO5+s.nbO15+s.nbO30+s.nbO60)) + 1)/3601.0)
           AS gap_over_open60,
       v.volat_open30 AS volat
FROM read_parquet('{args.shape}') s
JOIN read_parquet('{args.volat}') v ON v.ticker = s.ticker AND v.date = s.date
WHERE {cond} AND s.ovn_from_lim59 IS NOT NULL AND s.dv_lh > 0
  AND s.dv_over_open15 IS NOT NULL""")
A = con.execute("SELECT * FROM S").fetchdf()
r = A.r.values
n = len(A)
yrs = sorted(A.yr.unique())
rng = np.random.default_rng(args.seed)

# ⭐ directions: short wants THIN/GAPPY late tape, long wants DENSE
BAR_OP = "<=" if args.side == "short" else ">="
GAP_OP = ">=" if args.side == "short" else "<="
INT_OP = "<=" if args.side == "short" else ">="
GAPS_OP = ">=" if args.side == "short" else "<="
VOL_OP = ">=" if args.side == "short" else "<="


def pf(x):
    g, l = x[x > 0].sum(), -x[x < 0].sum()
    return float("inf") if l == 0 else g / l


def tpf(x):
    return pf(x[x >= np.quantile(x, 0.05)]) if len(x) >= 20 else float("nan")


def st(m, lbl, base=None):
    x, k = r[m], int(m.sum())
    neg = [y for y in yrs
           if (m & (A.yr.values == y)).sum() >= 8
           and pf(r[m & (A.yr.values == y)]) < 1.0]
    d = {"rule": lbl, "n": k, "PF": round(pf(x), 3),
         "PFtrim": round(tpf(x), 3) if k >= 20 else np.nan,
         "mean%": round(x.mean()*100, 2), "med%": round(np.median(x)*100, 2),
         "win%": round((x > 0).mean()*100),
         "p5%": round(np.percentile(x, 5)*100, 1),
         "worst5%": round(np.sort(x)[:max(1, k//20)].mean()*100, 1),
         "worst%": round(x.min()*100), "loss%": round((x < 0).mean()*100),
         "yrs<1": len(neg)}
    if base is not None and k >= 30:
        rb = r[base]
        nb = len(rb)
        null = np.array([pf(rb[rng.choice(nb, k, replace=False)])
                         for _ in range(args.boot)])
        null = null[np.isfinite(null)]
        d["pctile"] = round(float((null < pf(x)).mean()*100), 1)
    return d


print(f"side={args.side}   n = {n:,}   base PF {pf(r):.3f}")
print("=" * 200)
print("§1 ARE THEY THE SAME FEATURE? correlation and pick overlap at q50")
print("=" * 200)
rows = []
for W in (15, 30, 60):
    b, g = A[f"bar_over_open{W}"].values, A[f"gap_over_open{W}"].values
    ok = ~np.isnan(b) & ~np.isnan(g)
    rb = pd.Series(b[ok]).rank().values
    rg = pd.Series(g[ok]).rank().values
    mb = (b <= np.nanquantile(b, .5)) if BAR_OP == "<=" else (b >= np.nanquantile(b, .5))
    mg = (g >= np.nanquantile(g, .5)) if GAP_OP == ">=" else (g <= np.nanquantile(g, .5))
    rows.append({"W": f"{W}m", "Pearson": round(float(np.corrcoef(b[ok], g[ok])[0, 1]), 3),
                 "Spearman": round(float(np.corrcoef(rb, rg)[0, 1]), 3),
                 "bar picks": int(mb.sum()), "gap picks": int(mg.sum()),
                 "overlap %": f"{100*(mb & mg).sum()/mb.sum():.0f}%"})
print(pd.DataFrame(rows).to_string(index=False))
print("  a monotone reparameterisation would be Spearman ±1.000 and 100% overlap.")

print(f"\n{'='*200}\n⭐ §2 HEAD TO HEAD at q50, on the whole population and on the "
      f"`gaps` gate\n{'='*200}")
GATE = ((A.gaps.values >= 1000) if args.side == "short"
        else (A.gaps.values <= 760))
for ctx_lbl, ctx in (("whole population", np.ones(n, bool)),
                     (f"gaps gate (n={int(GATE.sum())})", GATE)):
    print(f"\n  --- {ctx_lbl} ---")
    rows = [st(ctx, "context (no persistence filter)")]
    for W in (15, 30, 60):
        for col, op, tag in ((f"bar_over_open{W}", BAR_OP, "BAR"),
                             (f"gap_over_open{W}", GAP_OP, "GAP")):
            v = A[col].values
            t = np.nanquantile(v[ctx], .5)
            m = ctx & ((v <= t) if op == "<=" else (v >= t)) & ~np.isnan(v)
            rows.append(st(m, f"  {tag}  {col} {op} q50", base=ctx))
    print(pd.DataFrame(rows).fillna("").to_string(index=False))

print(f"\n{'='*200}\n⭐⭐ §3 IN THE SEQUENTIAL BUILD — each threshold is the median of "
      f"the cell it refines (§S43co)\n{'='*200}")
t = np.nanquantile(A.inten.values[GATE], .5)
S2 = GATE & ((A.inten.values <= t) if INT_OP == "<=" else (A.inten.values >= t))
rows = [st(GATE, "gaps gate"), st(S2, "+ dv_over_open30 (intensity)")]
for W in (15, 30, 60):
    for col, op, tag in ((f"bar_over_open{W}", BAR_OP, "BAR"),
                         (f"gap_over_open{W}", GAP_OP, "GAP")):
        v = A[col].values
        tt = np.nanquantile(v[S2], .5)
        m = S2 & ((v <= tt) if op == "<=" else (v >= tt)) & ~np.isnan(v)
        rows.append(st(m, f"  + {tag}  {col}", base=S2))
print(pd.DataFrame(rows).fillna("").to_string(index=False))

print(f"\n{'='*200}\n⭐⭐ §4 MARGINAL VALUE ACROSS THE LATTICE — the test "
      f"`bar_over_open30` FAILED (2/5 contexts, median ΔPF −0.267)\n{'='*200}")
vv = A.volat.values
iv = A.inten.values
gv = A.gaps.values
good = {"V": (vv >= np.nanquantile(vv, .5)) if VOL_OP == ">=" else (vv <= np.nanquantile(vv, .5)),
        "I": (iv <= np.nanquantile(iv, .5)) if INT_OP == "<=" else (iv >= np.nanquantile(iv, .5)),
        "G": (gv >= np.nanquantile(gv, .5)) if GAPS_OP == ">=" else (gv <= np.nanquantile(gv, .5))}
for col, op, tag in (("bar_over_open30", BAR_OP, "BAR"),
                     ("gap_over_open30", GAP_OP, "GAP")):
    v = A[col].values
    rows = []
    for combo in itertools.product([1, 0], repeat=3):
        base = np.ones(n, bool)
        lab = ""
        for bit, k in zip(combo, "VIG"):
            base &= good[k] if bit else ~good[k]
            lab += f"{k}{'+' if bit else '−'}"
        if base.sum() < 80:
            continue
        # ⚠ threshold is the median OF THIS CONTEXT, not a global one (§S43co)
        tt = np.nanquantile(v[base], .5)
        mp = base & ((v <= tt) if op == "<=" else (v >= tt))
        mm = base & ~mp
        if mp.sum() < 30 or mm.sum() < 30:
            continue
        rows.append({"holding": lab, "ctx n": int(base.sum()),
                     "ctx PF": round(pf(r[base]), 3),
                     "+ n": int(mp.sum()), "+ PF": round(pf(r[mp]), 3),
                     "+ worst5%": round(np.sort(r[mp])[:max(1, mp.sum()//20)].mean()*100, 1),
                     "− PF": round(pf(r[mm]), 3),
                     "ΔPF": round(pf(r[mp]) - pf(r[mm]), 3)})
    D = pd.DataFrame(rows)
    print(f"\n  --- {tag}  {col} {op} context median ---")
    print(D.to_string(index=False))
    print(f"      ΔPF > 0 in {int((D['ΔPF'] > 0).sum())}/{len(D)} contexts, "
          f"median Δ {D['ΔPF'].median():+.3f}")
