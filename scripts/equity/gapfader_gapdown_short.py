"""GapFader GF-41 (user 2026-10-20): gap-downs <= −4 % that FAILED their open at 10:00 (the last 1s vwap before 10:00 < the official
open) as SHORT setups, graded by rvol30 (volume 09:30–10:00 / avgvol20_prior) and the 09:30–10:00 tape (volat_clk). SHORT at the
first 1s vwap at/after 10:00, COVER at the official close. Short return s = 1 − close / entry; net = s − 0.7c / entry. NO borrow
fee and no locate / SSR modelling (⚠ a stock down >= 10 % from the prior close is under the SSR uptick rule for the day: shorts
must rest above the NBB — a maker short is allowed). GF-32's tape (gapdown_tape.parquet); headline universe; every rvol_pm.
Run from research/:  python -u scripts/equity/gapfader_gapdown_short.py > data/equity/gapfader/gapdown_short.md; echo exit $?
"""
import numpy as np, pandas as pd
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.gap <= -0.04)]
C = C.merge(pd.read_parquet("data/equity/gapfader/gapdown_tape.parquet"), on=["ticker", "date"])
C = C[np.isfinite(C.entry_30) & np.isfinite(C.px_30) & C.volat_clk.notna() & (C.act_a > 0) & C.rvol_30m.notna()].copy()
C = C[C.px_30 < C.open].copy()                                                      # FAILED at 10:00
C["s"] = 1 - C.close / C.entry_30; C["sn"] = C.s - 0.007 / C.entry_30; C["yr"] = C.date.str[:4]; early = C.yr <= "2020"; NY = C.yr.nunique()
calm = C.volat_clk < 40; wild = C.volat_clk >= 80; mid = ~calm & ~wild; fad = C.act_b / C.act_a < 0.82
def pf(x):
    x = np.asarray(x); x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
RV = [(0, 0.2), (0.2, 0.5), (0.5, 1), (1, 2), (2, 1e9)]
rl = lambda a, b: f"{a:g}–{b:g}" if b < 1e8 else f"≥ {a:g}"
def cell(m):
    d = C[m]
    if len(d) < 25: return f"· [{len(d)}]"
    yrs = d.groupby("yr").sn.apply(pf)
    return f"**{pf(d.sn):.2f}** ({pf(d.s.clip(upper=0.5)) / max(pf(d.s), 1e-9) * pf(d.sn):.2f}) [{len(d)}, {int((yrs < 1).sum())}/{len(yrs)}]"
def grid(title, rows):
    print(f"**{title}** — SHORT net PF (clip-adjusted) [n, losing years], rvol30 bands\n")
    print("| cell | " + " | ".join(f"rvol30 {rl(a, b)}" for a, b in RV) + " |\n|---|" + "---|" * len(RV))
    for nm, m in rows: print(f"| {nm} | " + " | ".join(cell(m & (C.rvol_30m >= a) & (C.rvol_30m < b)) for a, b in RV) + " |")
    print()
H = ("| cell | n | n / yr | median short % | win % | PF | PF clip +50 % | **net PF** | net 2016–20 / 2021–26 | years < 1 (net) |\n"
     "|---|---|---|---|---|---|---|---|---|---|")
def row(nm, m):
    d = C[m]
    if len(d) < 25: return f"| {nm} | {len(d)} | | | | | | | | |"
    yrs = d.groupby("yr").sn.apply(pf)
    return (f"| {nm} | {len(d):,} | {len(d)/NY:.0f} | {d.s.median()*100:+.2f} | {(d.s>0).mean()*100:.0f} | {pf(d.s):.2f} | "
            f"{pf(d.s.clip(upper=0.5)):.2f} | **{pf(d.sn):.2f}** | {pf(d.sn[early[m]]):.2f} / {pf(d.sn[~early[m]]):.2f} | {int((yrs<1).sum())}/{len(yrs)} |")
print("# GF-41 — gap-downs that FAILED the open at 10:00, SHORT 10:00 → close\n")
print(f"{len(C):,} failed gap-downs ≤ −4 %. No borrow fee in the net; ⚠ see the header on SSR.\n")
for pm_nm, pm in [("every rvol_pm", C.rvol_pm >= 0), ("rvol_pm ≥ 0.2", C.rvol_pm >= 0.2)]:
    print(f"## {pm_nm}\n")
    grid("failed at 10:00", [("calm", pm & calm), ("40–80 bp", pm & mid), ("wild ≥ 80 bp", pm & wild),
                             ("not calm", pm & ~calm), ("not calm · fading", pm & ~calm & fad), ("not calm · not fading", pm & ~calm & ~fad)])
best = ~calm & (C.rvol_30m >= 1)
print("## the candidate: failed · not calm (≥ 40 bp) · rvol30 ≥ 1\n"); print(H)
print(row("failed · all", C.s.notna())); print(row("**failed · not calm · rvol30 ≥ 1**", best))
for a, b in [(0, 0.02), (0.02, 0.2), (0.2, 1), (1, 1e9)]:
    print(row(f"… · rvol_pm {a:g}–{b:g}" if b < 1e8 else f"… · rvol_pm ≥ {a:g}", best & (C.rvol_pm >= a) & (C.rvol_pm < b)))
for a, b in [(0.04, 0.06), (0.06, 0.10), (0.10, 0.20), (0.20, 9)]:
    print(row(f"… · gap −{a*100:g} … −{b*100:g} %" if b < 9 else f"… · gap ≤ −{a*100:g} %", best & (-C.gap >= a) & (-C.gap < b)))
for a, b in [(1, 3), (3, 10), (10, 30), (30, 1e9)]:
    print(row(f"… · price ${a:g}–{b:g}" if b < 1e8 else f"… · price ≥ ${a:g}", best & (C.entry_30 >= a) & (C.entry_30 < b)))
for a, b in [(1e6, 5e6), (5e6, 25e6), (25e6, 1e12)]:
    print(row(f"… · dv20 ${a/1e6:g}–{b/1e6:g}M" if b < 1e11 else f"… · dv20 ≥ ${a/1e6:g}M", best & (C.dv20_prior >= a) & (C.dv20_prior < b)))
print(row("… · at 10:00 ≥ 10 % below the prior close (SSR-triggered)", best & (C.px_30 / C.pc_total - 1 <= -0.10)))
print(row("… · at 10:00 < 10 % below the prior close", best & (C.px_30 / C.pc_total - 1 > -0.10)))
d = C[best]
print(f"\nby year (net PF): " + " · ".join(f"{y} {pf(d.sn[d.yr == y]):.2f} [{(d.yr == y).sum()}]" for y in sorted(C.yr.unique())))
print(f"\nmedian price ${d.entry_30.median():.2f}, median dv20 ${d.dv20_prior.median()/1e6:.1f}M, median gap {d.gap.median()*100:.1f} %, "
      f"median mean short net {d.sn.mean()*100:+.2f} %")
