"""GapFader GF-39 (user 2026-10-20): the calm gap-up swing graded by the RELATIVE VOLUME OF 09:30–10:00 instead of the premarket.
rvol30 = volume 09:30–10:00 / avgvol20_prior (the census's rvol_30m, recomputed here as a check). The user: heavy relative
volume can come with FADING activity. Sample: GF-38's (all gap-ups >= 4 %, every rvol_pm, held at 10:00), from its cache
swing_allpm.parquet; calm = volat_clk < 40 bp; fading = act_b / act_a < 0.82. Out at the close, net 0.7c / share.
Run from research/:  python -u scripts/equity/gapfader_rvol30.py > data/equity/gapfader/rvol30.md; echo exit $?
"""
import numpy as np, pandas as pd
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.gap >= 0.04)].copy()
C = C.merge(pd.read_parquet("data/equity/gapfader/swing_allpm.parquet"), on=["ticker", "date"])
C = C[(C.px_30 >= C.open) & np.isfinite(C.entry_30) & C.volat_clk.notna() & (C.act_a > 0)].copy()
chk = (C.vol_30m / C.avgvol20_prior - C.rvol_30m).abs().max()
assert chk < 1e-9, chk
C["rvol30"] = C.rvol_30m
C["yr"] = C.date.str[:4]; early = C.yr <= "2020"; NY = C.yr.nunique()
for k, e in [("10", "entry_30"), ("1030", "entry_60")]:
    C[f"r_{k}"] = C.close / C[e] - 1; C[f"rn_{k}"] = C[f"r_{k}"] - 0.007 / C[e]
calm = C.volat_clk < 40; fading = C.act_b / C.act_a < 0.82
def pf(x):
    x = np.asarray(x); x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
H = ("| cell | n | n / yr | median % | win % | PF | PF clip +50 % | **net PF** | net 2016–20 / 2021–26 | years < 1 (net) |\n"
     "|---|---|---|---|---|---|---|---|---|---|")
def row(nm, m, k="10"):
    r, rn = f"r_{k}", f"rn_{k}"; m = m & np.isfinite(C[rn]); d = C[m]
    if len(d) < 25: return f"| {nm} | {len(d)} | | | | | | | | |"
    yrs = d.groupby("yr")[rn].apply(pf)
    return (f"| {nm} | {len(d):,} | {len(d)/NY:.0f} | {d[r].median()*100:+.2f} | {(d[r]>0).mean()*100:.0f} | {pf(d[r]):.2f} | "
            f"{pf(d[r].clip(upper=0.5)):.2f} | **{pf(d[rn]):.2f}** | {pf(d[rn][early[m]]):.2f} / {pf(d[rn][~early[m]]):.2f} | {int((yrs<1).sum())}/{len(yrs)} |")
RV = [(0, 0.1), (0.1, 0.2), (0.2, 0.5), (0.5, 1), (1, 1e9)]
rl = lambda a, b: f"rvol30 {a:g}–{b:g}" if b < 1e8 else f"rvol30 ≥ {a:g}"
PM = [("rvol_pm < 0.02", C.rvol_pm < 0.02), ("rvol_pm 0.02–0.2", (C.rvol_pm >= 0.02) & (C.rvol_pm < 0.2)), ("rvol_pm ≥ 0.2", C.rvol_pm >= 0.2)]
print("# GF-39 — the calm gap-up swing by 09:30–10:00 relative volume (held at 10:00)\n")
print(f"{len(C):,} held gap-ups; ρ(rvol30, rvol_pm) = {C[['rvol30', 'rvol_pm']].corr(method='spearman').iloc[0, 1]:.2f}; "
      f"ρ(rvol30, act_b/act_a) = {pd.Series(C.rvol30).corr(C.act_b / C.act_a, method='spearman'):.2f}; "
      f"ρ(rvol30, volat_clk) = {C[['rvol30', 'volat_clk']].corr(method='spearman').iloc[0, 1]:.2f}\n")
print("## 1. rvol30 alone, 10:00 → close\n"); print(H)
for sub, sm in [("all held", C.r_10.notna()), ("calm", calm), ("calm · fading", calm & fading), ("calm · not fading", calm & ~fading)]:
    for a, b in RV: print(row(f"{sub} · {rl(a, b)}", sm & (C.rvol30 >= a) & (C.rvol30 < b)))
print()
print("## 2. calm: rvol30 × rvol_pm, net PF [n] (10:00 → close)\n")
print("| rvol30 \\ premarket | " + " | ".join(n for n, _ in PM) + " |\n|---|---|---|---|")
for a, b in RV:
    cells = []
    for _, pm in PM:
        d = C[calm & pm & (C.rvol30 >= a) & (C.rvol30 < b)]
        cells.append(f"{pf(d.rn_10):.2f} [{len(d)}]" if len(d) >= 25 else f"· [{len(d)}]")
    print(f"| {rl(a, b)} | " + " | ".join(cells) + " |")
print()
print("## 3. the user's cell: HEAVY rvol30 but FADING activity, calm\n"); print(H)
for a, b in [(0.5, 1e9), (1, 1e9)]:
    for k, kn in [("10", "10:00"), ("1030", "10:30")]:
        print(row(f"calm · fading · {rl(a, b)} · bought {kn}", calm & fading & (C.rvol30 >= a), k))
        print(row(f"calm · NOT fading · {rl(a, b)} · bought {kn}", calm & ~fading & (C.rvol30 >= a), k))
for nm, pm in PM:
    print(row(f"calm · fading · rvol30 ≥ 0.5 · {nm}", calm & fading & (C.rvol30 >= 0.5) & pm))
d = C[calm & fading & (C.rvol30 >= 0.5)]
print(f"\ncalm · fading · rvol30 ≥ 0.5 by year (net PF, 10:00): " + " · ".join(f"{y} {pf(d.rn_10[d.yr == y]):.2f} [{(d.yr == y).sum()}]" for y in sorted(C.yr.unique())))
print(f"\nmedian rvol_pm in that cell {d.rvol_pm.median():.2f}; {(d.rvol_pm < 0.2).mean()*100:.0f} % of it has rvol_pm < 0.2")
