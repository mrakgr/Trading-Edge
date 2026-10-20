"""GapFader GF-40 (user 2026-10-20): GF-39's 09:30–10:00 relative volume (rvol30 = volume 09:30–10:00 / avgvol20_prior) on
(1) the NON-CALM gap-up tape (GF-38's sample, swing_allpm.parquet), and (2) GAP-DOWNS <= −4 % (GF-32's tape, gapdown_tape.parquet).
The trade: LONG, bought at the first 1s vwap at/after 10:00, out at the official close, net 0.7c / share. held = the last 1s
vwap before 10:00 >= the official open. calm = volat_clk < 40 bp (09:30–10:00); wild = >= 80 bp; fading = act_b / act_a < 0.82.
Headline universe (open >= $1, dv20_prior >= $1M, no early close), every rvol_pm.
Run from research/:  python -u scripts/equity/gapfader_rvol30_more.py > data/equity/gapfader/rvol30_more.md; echo exit $?
"""
import numpy as np, pandas as pd
CEN = pd.read_parquet("data/equity/gapfader/census.parquet")
CEN = CEN[(CEN.open >= 1) & (CEN.dv20_prior >= 1e6) & ~CEN.early_close & CEN.close.notna()]
def load(side):
    if side == "up":
        C = CEN[CEN.gap >= 0.04].merge(pd.read_parquet("data/equity/gapfader/swing_allpm.parquet"), on=["ticker", "date"])
    else:
        C = CEN[CEN.gap <= -0.04].merge(pd.read_parquet("data/equity/gapfader/gapdown_tape.parquet"), on=["ticker", "date"])
    C = C[np.isfinite(C.entry_30) & np.isfinite(C.px_30) & C.volat_clk.notna() & (C.act_a > 0) & C.rvol_30m.notna()].copy()
    C["rn"] = C.close / C.entry_30 - 1 - 0.007 / C.entry_30; C["r"] = C.close / C.entry_30 - 1; C["yr"] = C.date.str[:4]
    return C
def pf(x):
    x = np.asarray(x); x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
RV = [(0, 0.2), (0.2, 0.5), (0.5, 1), (1, 2), (2, 1e9)]
rl = lambda a, b: f"{a:g}–{b:g}" if b < 1e8 else f"≥ {a:g}"
def cell(C, m):
    d = C[m]
    if len(d) < 25: return f"· [{len(d)}]"
    yrs = d.groupby("yr").rn.apply(pf)
    return f"**{pf(d.rn):.2f}** ({pf(d.r.clip(upper=0.5)) / max(pf(d.r), 1e-9) * pf(d.rn):.2f}) [{len(d)}, {int((yrs < 1).sum())}/{len(yrs)}]"
def grid(C, title, rows):
    print(f"**{title}** — net PF (clip-adjusted) [n, losing years], rvol30 bands\n")
    print("| cell | " + " | ".join(f"rvol30 {rl(a, b)}" for a, b in RV) + " |\n|---|" + "---|" * len(RV))
    for nm, m in rows:
        print(f"| {nm} | " + " | ".join(cell(C, m & (C.rvol_30m >= a) & (C.rvol_30m < b)) for a, b in RV) + " |")
    print()
print("# GF-40 — rvol30 on the non-calm gap-up tape, and on gap-downs (long, 10:00 → close)\n")
print("Each cell: net PF; in brackets after it, the net PF scaled by clip/raw (the +50 % clip's effect: a big drop = tail-driven); [n, losing years].\n")
for side, nm in [("up", "GAP-UPS ≥ 4 %"), ("down", "GAP-DOWNS ≤ −4 %")]:
    C = load(side)
    held = C.px_30 >= C.open; calm = C.volat_clk < 40; wild = C.volat_clk >= 80; mid = ~calm & ~wild; fad = C.act_b / C.act_a < 0.82
    pm2 = C.rvol_pm >= 0.02
    print(f"## {nm} ({len(C):,}; held at 10:00 {held.mean()*100:.0f} %)\n")
    grid(C, "held at 10:00", [("calm", held & calm), ("40–80 bp", held & mid), ("wild ≥ 80 bp", held & wild),
                              ("not calm · fading", held & ~calm & fad), ("not calm · not fading", held & ~calm & ~fad)])
    grid(C, "held at 10:00, rvol_pm ≥ 0.02", [("calm", held & calm & pm2), ("not calm", held & ~calm & pm2), ("wild ≥ 80 bp", held & wild & pm2)])
    grid(C, "FAILED at 10:00", [("calm", ~held & calm), ("40–80 bp", ~held & mid), ("wild ≥ 80 bp", ~held & wild)])
