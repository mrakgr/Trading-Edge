"""GapFader GF-44 (user 2026-10-20): inside dv20 $1–5M · calm · held at 10:00 · rvol30 >= 1 (GF-42's best dv20 band, 693 trades),
the breakdown by GAP and PRICE, each with and without ACTIVITY FADING (act 09:45–10:00 / 09:30–09:45 < 0.82), and gap × price.
Bought at 10:00 (and 10:30), out at the official close, net 0.7c / share. GF-38's cache.
Run from research/:  python -u scripts/equity/gapfader_dv15_breakdown.py > data/equity/gapfader/dv15_breakdown.md; echo exit $?
"""
import numpy as np, pandas as pd
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & (C.dv20_prior < 5e6) & ~C.early_close & C.close.notna() & (C.gap >= 0.04)].copy()
C = C.merge(pd.read_parquet("data/equity/gapfader/swing_allpm.parquet"), on=["ticker", "date"])
C = C[(C.px_30 >= C.open) & np.isfinite(C.entry_30) & C.volat_clk.notna() & (C.act_a > 0) & C.rvol_30m.notna()]
C = C[(C.volat_clk < 40) & (C.rvol_30m >= 1)].copy()
C["yr"] = C.date.str[:4]; NY = C.yr.nunique()
for k, e in [("10", "entry_30"), ("1030", "entry_60")]:
    C[f"r_{k}"] = C.close / C[e] - 1; C[f"rn_{k}"] = C[f"r_{k}"] - 0.007 / C[e]
fad = C.act_b / C.act_a < 0.82
def pf(x):
    x = np.asarray(x); x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
H = ("| cell | n | n / yr | median % | win % | PF | PF clip +50 % | **net PF 10:00** | net 2016–20 / 2021–26 | years < 1 | **net PF 10:30** |\n"
     "|---|---|---|---|---|---|---|---|---|---|---|")
def row(nm, m):
    d = C[m & np.isfinite(C.rn_10)]
    if len(d) < 20: return f"| {nm} | {len(d)} | | | | | | | | | |"
    y = d.groupby("yr").rn_10.apply(pf); e = d.yr <= "2020"
    return (f"| {nm} | {len(d):,} | {len(d)/NY:.0f} | {d.r_10.median()*100:+.2f} | {(d.r_10>0).mean()*100:.0f} | {pf(d.r_10):.2f} | "
            f"{pf(d.r_10.clip(upper=0.5)):.2f} | **{pf(d.rn_10):.2f}** | {pf(d.rn_10[e]):.2f} / {pf(d.rn_10[~e]):.2f} | {int((y<1).sum())}/{len(y)} | "
            f"{pf(d.rn_1030):.2f} |")
GAP = [("4–6 %", 0.04, 0.06), ("6–10 %", 0.06, 0.10), ("10–20 %", 0.10, 0.20), ("20–40 %", 0.20, 0.40), ("≥ 40 %", 0.40, 99)]
PX = [("$1–3", 1, 3), ("$3–10", 3, 10), ("$10–30", 10, 30), ("≥ $30", 30, 1e9)]
print("# GF-44 — dv20 $1–5M · calm · held at 10:00 · rvol30 ≥ 1: by gap and price, with activity fading\n")
print(H); print(row("**all**", C.r_10.notna())); print(row("fading", fad)); print(row("not fading", ~fad)); print()
for title, col, B in [("gap", "gap", GAP), ("price (the 10:00 entry)", "entry_30", PX)]:
    print(f"## by {title}\n"); print(H)
    for nm, a, b in B:
        m = (C[col] >= a) & (C[col] < b)
        print(row(f"{nm}", m)); print(row(f"{nm} · fading", m & fad)); print(row(f"{nm} · not fading", m & ~fad))
    print()
def cell(m):
    d = C[m & np.isfinite(C.rn_10)]
    if len(d) < 15: return f"· [{len(d)}]"
    y = d.groupby("yr").rn_10.apply(pf); return f"**{pf(d.rn_10):.2f}** [{len(d)}, {int((y<1).sum())}/{len(y)}]"
for sub, sm in [("all", C.r_10.notna()), ("fading", fad)]:
    print(f"## gap × price, {sub} — net PF 10:00 [n, losing years]\n")
    print("| gap \\ price | " + " | ".join(n for n, _, _ in PX) + " |\n|---|" + "---|" * len(PX))
    for gn, a, b in GAP:
        print(f"| {gn} | " + " | ".join(cell(sm & (C.gap >= a) & (C.gap < b) & (C.entry_30 >= c) & (C.entry_30 < e)) for _, c, e in PX) + " |")
    print()
