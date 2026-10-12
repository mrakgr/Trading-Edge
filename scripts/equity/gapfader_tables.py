"""GapFader — step 1 report: how open gaps behave from the open to the close, by gap size and premarket volume.

Reads data/equity/gapfader/census.parquet (gapfader_census.py). Gap-ups and gap-downs separately. The FADE trade is
the one the question asks about: short a gap-up / buy a gap-down AT THE OPEN, out at the official close; equal weight,
NO costs (a census, not a book). rvol_pm = premarket volume (04:00–09:29:59) / the 20 prior sessions' average daily
volume — the "weak volume" axis, knowable at 09:30.

Run from research/:
    python -u scripts/equity/gapfader_tables.py --min-open 1 --min-dv20 1e6 > data/equity/gapfader/tables.md; echo exit $?
"""
import argparse
import numpy as np, pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument("--census", default="data/equity/gapfader/census.parquet")
ap.add_argument("--min-open", type=float, default=1.0)
ap.add_argument("--min-dv20", type=float, default=1e6)
args = ap.parse_args()

C = pd.read_parquet(args.census)
C = C[(C.open >= args.min_open) & (C.dv20_prior >= args.min_dv20) & ~C.early_close & C.close.notna()].copy()
C["yr"] = C.date.str[:4]
C["side"] = np.where(C.gap > 0, "gap UP (fade = short)", "gap DOWN (fade = long)")
C["ag"] = C.gap.abs()
GAP = [0.02, 0.04, 0.06, 0.10, 0.15, 0.20, 0.30, 0.50, 99]
RV = [0, 0.02, 0.05, 0.10, 0.25, 0.5, 1.0, 1e9]
def lab(e, i, pct=True):
    a, b = e[i], e[i + 1]
    f = (lambda x: f"{x*100:g}%") if pct else (lambda x: f"{x:g}")
    return f"[{f(a)}, {'∞' if b > 50 else f(b)})"
C["gb"] = pd.cut(C.ag, GAP, right=False, labels=[lab(GAP, i) for i in range(len(GAP) - 1)])
C["rb"] = pd.cut(C.rvol_pm.fillna(0), RV, right=False, labels=[lab(RV, i, False) for i in range(len(RV) - 1)])

def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
def f(x, d=2): return "inf" if np.isinf(x) else ("—" if np.isnan(x) else f"{x:.{d}f}")
def stats(S):
    r = S.fade_oc.values
    return (len(S), S.filled.mean() * 100, np.nanmedian((S.fill_sec[S.filled] - 34200) / 60) if S.filled.any() else np.nan,
            np.nanmean(r) * 100, np.nanmedian(r) * 100, (r > 0).mean() * 100, pf(r), np.nanmedian(S.retraced), np.nanquantile(S.adverse, .9) * 100)

print(f"# GapFader census — open → close (universe: raw open ≥ ${args.min_open:g}, dv20_prior ≥ ${args.min_dv20:,.0f}, no early closes; "
      f"{len(C):,} gaps |gap| ≥ 2 %, {C.date.min()} .. {C.date.max()}; equal weight, NO costs)\n")
for side, S in C.groupby("side"):
    print(f"## {side} — by gap size\n")
    print("| gap | n | filled by close | median fill (min after open) | fade mean % | fade median % | fade win % | fade PF | median retraced | adverse p90 % |")
    print("|---|---|---|---|---|---|---|---|---|---|")
    for g, T in S.groupby("gb", observed=True):
        n, fr, ft, m, md, w, p, rt, adv = stats(T)
        print(f"| {g} | {n:,} | {fr:.0f} % | {f(ft,0)} | {m:+.2f} | {md:+.2f} | {w:.0f} | {f(p)} | {f(rt)} | {f(adv,1)} |")
    print(f"\n### {side} — fade PF (fill rate) by gap × premarket relative volume (rvol_pm)\n")
    rbs = [r for r in S.rb.cat.categories]
    print("| gap \\ rvol_pm | " + " | ".join(rbs) + " |\n|---|" + "---|" * len(rbs))
    for g, T in S.groupby("gb", observed=True):
        cells = []
        for r in rbs:
            U = T[T.rb == r]
            cells.append("·" if len(U) < 30 else f"{f(pf(U.fade_oc.values))} ({U.filled.mean()*100:.0f} %, {len(U):,})")
        print(f"| {g} | " + " | ".join(cells) + " |")
    print(f"\n### {side} — by year, gaps ≥ 6 %: fade PF (n) for weak (rvol_pm < 0.10) vs strong (≥ 0.25) premarket volume\n")
    yrs = sorted(S.yr.unique())
    print("| | " + " | ".join(yrs) + " |\n|---|" + "---|" * len(yrs))
    for name, m in (("weak  < 0.10", S.rvol_pm.fillna(0) < 0.10), ("mid  [0.10, 0.25)", (S.rvol_pm >= 0.10) & (S.rvol_pm < 0.25)), ("strong ≥ 0.25", S.rvol_pm >= 0.25)):
        T = S[m & (S.ag >= 0.06)]
        print(f"| {name} | " + " | ".join(f"{f(pf(T[T.yr == y].fade_oc.values))} ({(T.yr == y).sum()})" for y in yrs) + " |")
    print()
