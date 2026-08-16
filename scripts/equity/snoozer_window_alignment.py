"""S43ck — ONE opening window for the whole system: 15m, 30m or 60m?

⭐ USER (2026-08-16): "Just like we're evaluating volatility during set windows, we
should do the same for relative gaps and relative dollar volume. So far we have 4 main
features for this system: volatility, absolute gaps, relative dv (intensity) and
relative gaps (persistence)... I feel that for whatever system we pick, we should use
the same opening window for the features in the system, either 30m or 60m."

## Why this is worth doing rather than assuming

`shape` is `dv_over_open15` — last-hour dollars over the FIRST 15 MINUTES — and that
window was never chosen. It is what `dv_0945_tape` happens to cover, because that
column is the FlushFader UNIVERSE GATE. S43cb swept the reference length but only on
the SHORT side, and found a SHALLOW optimum there (15m and 30m share 89-92% of picks).
The long side has never had the sweep at all. Meanwhile volatility arrived with its own
independent ladder. So the system currently mixes a 15-minute denominator with a
60-minute volatility window for no reason anyone chose.

## The four families, and what "the window" means for each

| family | measure | window role |
|---|---|---|
| **volatility** | `volat_open{W}` = mean abs slot log-return over [09:30, 09:30+W) | ABSOLUTE: where it is measured |
| **intensity** (relative dv) | `dv_over_open{W}` = lastHour$ / firstW$ | RELATIVE: the denominator |
| **persistence** (relative gaps) | `bar_over_open{W}` = (nbLh/3600) / (nbOpen{W}/W) | RELATIVE: the denominator |
| **absolute gaps** | `gaps` = 3540 − nb60k59, seconds of (15:00,15:59] that did not trade | ⚠ NO WINDOW — last hour only |

⚠ `gaps` has no opening window by construction, so it is carried through every variant
unchanged and cannot vote on W. The user's plan is to keep ABSOLUTE persistence and
drop the relative twin; §3 tests that directly rather than assuming it.

⚠ `bar_over_*` is RATE-NORMALISED (bar counts are capped by window length) while
`dv_over_*` is a plain ratio (dollars have no cap). That asymmetry is deliberate and
predates this script — see §S43cd.

## Method

Everything is a ONE-SIDED MEDIAN SPLIT (§S43cj: the lower band was worth ~0.02-0.16 PF
and is the most fittable piece, so it is gone). That makes every single-feature cell
exactly n/2 — matched by construction, no per-feature threshold search, nothing chosen
in-sample.

For the COMBINED spec the three filters are applied at a COMMON quantile q, swept, so
the three windows can be compared at matched trade counts rather than at matched q.

Directions on the LONG side: volatility LOW, intensity HIGH, persistence HIGH,
gaps LOW. (Every one of these flips on the short side — §S43cb.)

Usage:  python scripts/equity/snoozer_window_alignment.py [--side long]
"""
import argparse

import duckdb
import numpy as np
import pandas as pd

pd.set_option("display.width", 260)
pd.set_option("display.max_columns", 60)

ap = argparse.ArgumentParser()
ap.add_argument("--shape", default="data/equity/flushfader/snoozer_shape.parquet")
ap.add_argument("--volat", default="data/equity/flushfader/snoozer_volat.parquet")
ap.add_argument("--side", choices=["long", "short"], default="long")
ap.add_argument("--chg", type=float, default=0.06)
ap.add_argument("--boot", type=int, default=1500)
ap.add_argument("--seed", type=int, default=20260816)
args = ap.parse_args()

sign = "-" if args.side == "short" else ""
cond = f"chg60k59 > {args.chg}" if args.side == "short" else f"chg60k59 < {-args.chg}"
# ⭐ every direction flips between the two systems (§S43cb)
D_VOL = "<=" if args.side == "long" else ">="
D_INT = ">=" if args.side == "long" else "<="
D_PER = ">=" if args.side == "long" else "<="
D_GAP = "<=" if args.side == "long" else ">="

con = duckdb.connect(config={"memory_limit": "8GB", "threads": 8})
con.execute("SET enable_progress_bar=false")
con.execute(f"""CREATE OR REPLACE TEMP TABLE S AS
SELECT s.ticker, s.date, year(s.date) AS yr, {sign}(s.ovn_from_lim59) AS r,
       3540 - s.nb60k59 AS gaps,
       s.dv_over_open15, s.dv_over_open30, s.dv_over_open60,
       s.bar_over_open15, s.bar_over_open30, s.bar_over_open60,
       v.volat_open15, v.volat_open30, v.volat_open60
FROM read_parquet('{args.shape}') s
JOIN read_parquet('{args.volat}') v ON v.ticker = s.ticker AND v.date = s.date
WHERE {cond} AND s.ovn_from_lim59 IS NOT NULL AND s.dv_lh > 0
  AND s.dv_over_open15 IS NOT NULL""")
A = con.execute("SELECT * FROM S").fetchdf()
r = A.r.values
n = len(A)
yrs = sorted(A.yr.unique())
rng = np.random.default_rng(args.seed)


def pf(x):
    g, l = x[x > 0].sum(), -x[x < 0].sum()
    return float("inf") if l == 0 else g / l


def row(m, lbl, extra=None):
    x, k = r[m], int(m.sum())
    neg = [y for y in yrs
           if (m & (A.yr.values == y)).sum() >= 10
           and pf(r[m & (A.yr.values == y)]) < 1.0]
    d = {"rule": lbl, "n": k, "PF": round(pf(x), 3),
         "lift": f"{pf(x)/pf(r):.2f}x",
         "mean%": f"{x.mean()*100:+.2f}", "med%": f"{np.median(x)*100:+.2f}",
         "win%": f"{(x>0).mean()*100:.0f}",
         "p5%": f"{np.percentile(x, 5)*100:.1f}",
         "worst5%": f"{np.sort(x)[:max(1, k//20)].mean()*100:.1f}",
         "loss%": f"{(x<0).mean()*100:.0f}", "yrs<1": len(neg),
         "which": ",".join(str(y) for y in neg) or "-"}
    if extra:
        d.update(extra)
    return d


print(f"side={args.side}   population n = {n:,}   base PF {pf(r):.3f}   "
      f"mean {r.mean()*100:+.2f}%   worst5% {np.sort(r)[:n//20].mean()*100:.1f}%   "
      f"loss {(r<0).mean()*100:.0f}%")

FAM = [("volatility  volat_open{W}", "volat_open", D_VOL),
       ("intensity   dv_over_open{W}", "dv_over_open", D_INT),
       ("persistence bar_over_open{W}", "bar_over_open", D_PER)]

print(f"\n{'='*205}\n⭐ §1 EVERY FAMILY AT EVERY WINDOW — one-sided MEDIAN split, "
      f"so all cells are exactly n/2 (matched by construction)\n{'='*205}")
rows = []
for lbl, pre, op in FAM:
    for W in (15, 30, 60):
        f = f"{pre}{W}"
        v = A[f].values.astype(float)
        med = np.nanmedian(v)
        m = ((v <= med) if op == "<=" else (v >= med)) & ~np.isnan(v)
        k = int(m.sum())
        null = np.array([pf(r[rng.choice(n, k, replace=False)])
                         for _ in range(args.boot)])
        null = null[np.isfinite(null)]
        sc = 1e4 if pre == "volat_open" else 1.0
        u = "bp" if sc > 1 else ""
        rows.append(row(m, lbl.replace("{W}", str(W)),
                        {"W": f"{W}m", "thresh": f"{op} {med*sc:.2f}{u}",
                         "pctile": f"{(null < pf(r[m])).mean()*100:.1f}"}))
# gaps has no window; shown once as the reference
v = A.gaps.values.astype(float)
med = np.nanmedian(v)
m = (v <= med) if D_GAP == "<=" else (v >= med)
rows.append(row(m, "⚠ gaps (ABSOLUTE, no window)",
                {"W": "—", "thresh": f"{D_GAP} {med:.0f}s", "pctile": ""}))
d = pd.DataFrame(rows)
print(d[["rule", "W", "thresh", "n", "PF", "lift", "pctile", "mean%", "med%",
         "win%", "p5%", "worst5%", "loss%", "yrs<1", "which"]].to_string(index=False))

print(f"\n{'='*205}\n§2 THE SAME TABLE, PIVOTED — which W wins per family?\n{'='*205}")
for metric in ("PF", "worst5%", "loss%", "yrs<1"):
    piv = []
    for lbl, pre, op in FAM:
        rr = {"family": lbl.replace("{W}", "*")}
        for W in (15, 30, 60):
            sub = d[d.rule == lbl.replace("{W}", str(W))]
            rr[f"{W}m"] = sub[metric].values[0]
        piv.append(rr)
    print(f"\n  --- {metric} ---")
    print(pd.DataFrame(piv).to_string(index=False))

print(f"\n{'='*205}\n⭐⭐ §3 THE COMBINED SPEC AT EACH WINDOW — three filters at a "
      f"COMMON quantile q, swept so the windows meet at matched n\n{'='*205}")
rows = []
for W in (15, 30, 60):
    for q in (0.70, 0.60, 0.50, 0.40):
        vv = A[f"volat_open{W}"].values.astype(float)
        ii = A[f"dv_over_open{W}"].values.astype(float)
        pp = A[f"bar_over_open{W}"].values.astype(float)
        tv = np.nanquantile(vv, q if D_VOL == "<=" else 1 - q)
        ti = np.nanquantile(ii, q if D_INT == "<=" else 1 - q)
        mv = (vv <= tv) if D_VOL == "<=" else (vv >= tv)
        mi = (ii <= ti) if D_INT == "<=" else (ii >= ti)
        for tag, m in (("vol x int", mv & mi),
                       ("vol x int x pers",
                        mv & mi & ((pp >= np.nanquantile(pp, 1 - q)) if D_PER == ">="
                                   else (pp <= np.nanquantile(pp, q))))):
            m = m & ~np.isnan(vv) & ~np.isnan(ii)
            if m.sum() < 60:
                continue
            rows.append(row(m, f"W={W}m  q={q:.0%}  {tag}", {"W": f"{W}m",
                                                             "combo": tag}))
d3 = pd.DataFrame(rows)
for tag in ("vol x int", "vol x int x pers"):
    print(f"\n  --- {tag} ---")
    print(d3[d3.combo == tag][["rule", "n", "PF", "lift", "mean%", "med%", "win%",
                               "p5%", "worst5%", "loss%", "yrs<1",
                               "which"]].to_string(index=False))

print(f"\n{'='*205}\n⭐ §4 DOES ABSOLUTE `gaps` BEAT RELATIVE PERSISTENCE? "
      f"(the user's proposed omission, tested)\n{'='*205}")
rows = []
for W in (15, 30, 60):
    vv = A[f"volat_open{W}"].values.astype(float)
    ii = A[f"dv_over_open{W}"].values.astype(float)
    base = (((vv <= np.nanquantile(vv, .5)) if D_VOL == "<=" else
             (vv >= np.nanquantile(vv, .5))) &
            ((ii >= np.nanquantile(ii, .5)) if D_INT == ">=" else
             (ii <= np.nanquantile(ii, .5))))
    base &= ~np.isnan(vv) & ~np.isnan(ii)
    nb = int(base.sum())
    rows.append(row(base, f"W={W}m  vol x int (no persistence)"))
    for tag, col, op in (("+ ABSOLUTE gaps", "gaps", D_GAP),
                         (f"+ RELATIVE bar_over_open{W}", f"bar_over_open{W}", D_PER)):
        v2 = A[col].values.astype(float)
        t2 = np.nanquantile(v2[base], .5)
        m2 = base & ((v2 <= t2) if op == "<=" else (v2 >= t2))
        k = int(m2.sum())
        null = np.array([pf(r[base][rng.choice(nb, k, replace=False)])
                         for _ in range(args.boot)])
        null = null[np.isfinite(null)]
        rows.append(row(m2, f"    {tag}",
                        {"pctile vs base": f"{(null < pf(r[m2])).mean()*100:.1f}"}))
d4 = pd.DataFrame(rows)
print(d4[["rule", "n", "PF", "lift", "mean%", "win%", "p5%", "worst5%", "loss%",
          "yrs<1", "pctile vs base"]].fillna("").to_string(index=False))
print("  pctile is against random same-n subsets OF THE vol x int CELL, not the")
print("  whole population — the only question here is whether persistence ADDS.")
