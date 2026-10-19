"""GapFader GF-29 (user 2026-10-19, curiosity): (a) can ABSOLUTE DOLLAR VOLUME replace the TRADE COUNT as GF-10b's tape
measure (the user: "using trade count doesn't sit well with me")? (b) the calm-first-30-min continuation (GF-11) by GAP
SIZE x PREMARKET VOLUME, a combination never tested.

The GF-10 / GF-11 trade: gap-up >= 4 % (headline universe: open >= $1, dv20_prior >= $1M, no early close), rvol_pm >= 0.2,
HELD THE OPEN at 10:00 (the last 1s vwap before 10:00 >= the open), bought at the first 1s vwap at/after 10:00, out at the
official close. Tape features over 09:30–10:00 from the cached GF-10 file (tape_persistence.parquet):
  trades30 = trade count, act30 = active 1s bars, dv30 = share volume x the 10:00 price (absolute $, a proxy: one price
  for the window), rvol30 = share volume / avgvol20_prior (GF-10b's flat control). volat_clk from GF-11 (volat30.parquet).
Gross, plus net at 0.7c / share round trip (GF-11's cost). In-sample curiosity: the bands are quintiles / round numbers.
Run from research/:  python -u scripts/equity/gapfader_tape_dv.py > data/equity/gapfader/tape_dv.md; echo exit $?
"""
import numpy as np, pandas as pd
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm >= 0.2) & (C.gap >= 0.04)].copy()
X = pd.read_parquet("data/equity/gapfader/tape_persistence.parquet")
V = pd.read_parquet("data/equity/gapfader/volat30.parquet")[["ticker", "date", "volat_clk"]]
C = C.merge(X, on=["ticker", "date"]).merge(V, on=["ticker", "date"], how="left")
C = C[(C.px_30 >= C.open) & np.isfinite(C.entry_30)].copy()                     # held the open at 10:00
C["r"] = C.close / C.entry_30 - 1
C["rn"] = C.r - 0.007 / C.entry_30
C["trades30"] = C.tc_a + C.tc_b; C["act30"] = C.act_a + C.act_b
C["dv30"] = (C.vol_a + C.vol_b) * C.entry_30; C["rvol30"] = (C.vol_a + C.vol_b) / C.avgvol20_prior
C["yr"] = C.date.str[:4]
def pf(x):
    x = np.asarray(x); x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
def row(nm, m):
    d = C[m]
    if len(d) < 30: return f"| {nm} | {len(d)} | | | | | | |"
    yrs = d.groupby("yr").r.apply(pf); low = int((yrs < 1).sum())
    return (f"| {nm} | {len(d):,} | {d.r.median()*100:+.2f} | {pf(d.r):.2f} | {pf(d.r.clip(upper=0.5)):.2f} | {pf(d.rn):.2f} | "
            f"{low}/{len(yrs)} | {d.dv20_prior.median()/1e6:,.1f} |")
H = "| | n | median % | PF | PF clip +50 % | net PF (0.7c) | years < 1 | median dv20 $M |\n|---|---|---|---|---|---|---|---|"

print(f"# GF-29 — the gap-up continuation: dollar volume vs trade count, and the calm tape by gap × premarket volume\n")
print(f"Held the open at 10:00, 10:00 → close: {len(C):,} trades (rvol_pm ≥ 0.2).\n")
print("Spearman ρ among the tape measures (09:30–10:00):\n")
print(C[["trades30", "act30", "dv30", "rvol30", "volat_clk"]].corr(method="spearman").round(2).to_markdown(), "\n")

for title, mask in [("all held gap-ups (rvol_pm ≥ 0.2)", C.rvol_pm >= 0.2), ("GF-10b's band, rvol_pm 1–5", (C.rvol_pm >= 1) & (C.rvol_pm < 5))]:
    S = C[mask]
    print(f"## (a) the tape measure, by its quintile — {title}\n")
    for f in ["trades30", "act30", "dv30", "rvol30"]:
        q = S[f].quantile([.2, .4, .6, .8]).values
        print(f"**{f}** (quintile edges {', '.join(f'{v:,.3g}' for v in q)})\n\n" + H)
        b = np.digitize(C[f], q)
        for k in range(5): print(row(f"Q{k+1}", mask & (b == k)))
        print()

print("## (a2) the substitution test — rvol_pm ≥ 0.2: trades30 × dv30, each split at its median\n")
mt, md = C.trades30.median(), C.dv30.median()
print(H)
for a, ma in [("few trades", C.trades30 < mt), ("many trades", C.trades30 >= mt)]:
    for b, mb in [("small $", C.dv30 < md), ("large $", C.dv30 >= md)]:
        print(row(f"{a}, {b}", ma & mb))
print(f"\n(medians: {mt:,.0f} trades, ${md/1e6:,.2f}M in 30 min)\n")

print("## (b) the calm tape (volat_clk < 40 bp) by gap size × rvol_pm\n")
calm = C.volat_clk < 40
print("PF (net 0.7c) [n] — rows gap, columns rvol_pm; and the WILD tape (≥ 80 bp) beside it as the control\n")
G = [(0.04, 0.06), (0.06, 0.10), (0.10, 0.20), (0.20, 9)]; R = [(0.2, 0.5), (0.5, 1), (1, 2), (2, 5), (5, 1e9)]
for title, m0 in [("calm, volat_clk < 40", calm), ("wild, volat_clk ≥ 80 (control)", C.volat_clk >= 80)]:
    print(f"**{title}**\n\n| gap \\ rvol_pm | " + " | ".join(f"{a:g}–{b:g}" if b < 1e8 else f"≥ {a:g}" for a, b in R) + " | all |")
    print("|---|" + "---|" * (len(R) + 1))
    for g0, g1 in G:
        cells = []
        for r0, r1 in R + [(0.2, 1e9)]:
            d = C[m0 & (C.gap >= g0) & (C.gap < g1) & (C.rvol_pm >= r0) & (C.rvol_pm < r1)]
            cells.append(f"{pf(d.rn):.2f} [{len(d)}]" if len(d) >= 20 else f"· [{len(d)}]")
        print(f"| {g0*100:g}–{g1*100:g} % |" if g1 < 9 else f"| ≥ {g0*100:g} % |", " | ".join(cells), "|")
    print()

print("## (c) the AVERAGE TRADE SIZE ($ per trade, 09:30–10:00) — what the substitution test points at\n")
C["avg_tsize"] = C.dv30 / C.trades30.replace(0, np.nan)
for title, mask in [("rvol_pm ≥ 0.2", C.rvol_pm >= 0.2), ("rvol_pm 1–5", (C.rvol_pm >= 1) & (C.rvol_pm < 5))]:
    S = C[mask]; q = S.avg_tsize.quantile([.2, .4, .6, .8]).values
    print(f"**{title}** (quintile edges $ {', '.join(f'{v:,.0f}' for v in q)})\n\n" + H)
    b = np.digitize(C.avg_tsize, q)
    for k in range(5): print(row(f"Q{k+1}", mask & (b == k)))
    print()
print("ρ(avg trade size, dv20_prior) = %.2f, ρ(avg trade size, price) = %.2f\n" % (
    C[["avg_tsize", "dv20_prior"]].corr(method="spearman").iloc[0, 1], C[["avg_tsize", "entry_30"]].corr(method="spearman").iloc[0, 1]))
