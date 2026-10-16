"""GapFader GF-6 (user 2026-10-16): gap-UPs on HEAVY premarket volume (rvol_pm > 1) lose when bought at the open (GF-4).
Do they pay as a LONG bought on a DIP — a limit order k % below the official open, working all day?
Fill: an RTH 1s bar VWAP strictly BELOW the limit (a trade-through, not a touch — rth_lo is the min of the RTH 1s vwaps).
Exit: the official close (and, second view, the next open). Return = exit / limit − 1. Headline universe (open ≥ $1,
dv20_prior ≥ $1M, no early close). No costs (a maker entry). k = 0 is the open itself (GF-4's follow trade).
Run from research/:  python -u scripts/equity/gapfader_dip_buy.py > data/equity/gapfader/dip_buy.md; echo exit $?
"""
import numpy as np, pandas as pd
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm > 1) & (C.gap >= 0.04)].copy()
C["yr"] = C.date.str[:4]
def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
GAPS = [(0.04, 0.10, "4–10 %"), (0.10, 0.20, "10–20 %"), (0.20, 0.40, "20–40 %"), (0.40, 99, "≥ 40 %"), (0.04, 99, "all ≥ 4 %")]
KS = [0, 0.02, 0.04, 0.06, 0.08, 0.10, 0.15, 0.20, 0.30]
print(f"# GF-6 — buy the DIP of a heavy-premarket gap-UP (rvol_pm > 1): a limit k % below the open, held to the close\n")
print(f"{len(C):,} gap-ups ≥ 4 % with rvol_pm > 1, {C.date.min()} … {C.date.max()}. Fill = an RTH 1s vwap below the limit.\n")
for glo, ghi, gl in GAPS:
    G = C[(C.gap >= glo) & (C.gap < ghi)]
    print(f"## gap {gl} — {len(G):,} gaps\n")
    print("| k below the open | filled | fill % | mean % → close | median % | win % | PF → close | PF 2016–20 | PF 2021–26 | PF → next open |")
    print("|---|---|---|---|---|---|---|---|---|---|")
    for k in KS:
        lim = G.open * (1 - k)
        F = G[(G.rth_lo < lim) if k > 0 else np.ones(len(G), bool)]
        if len(F) == 0: continue
        L = (F.open * (1 - k))
        r = (F.close / L - 1).values
        rn = (F.open_next_total / L - 1).values
        h1, h2 = F.yr <= "2020", F.yr >= "2021"
        print(f"| {k*100:.0f} % | {len(F):,} | {len(F)/len(G)*100:.0f} | {np.mean(r)*100:+.2f} | {np.median(r)*100:+.2f} | {(r>0).mean()*100:.0f} | "
              f"{pf(r):.2f} | {pf(r[h1.values]):.2f} | {pf(r[h2.values]):.2f} | {pf(rn):.2f} |")
    print()

# ---- the limit by the GAP: a fraction f of the gap retraced (f = 1: the previous close, the full fill) ----
print("## the limit by the GAP retraced: open − f × (open − prev close); f = 1 is the gap filled\n")
print("| gap | f | filled | fill % | mean % → close | median % | win % | PF → close | PF 2016–20 | PF 2021–26 | PF → next open |")
print("|---|---|---|---|---|---|---|---|---|---|---|")
for glo, ghi, gl in GAPS:
    G = C[(C.gap >= glo) & (C.gap < ghi)]
    for f in (0.25, 0.5, 0.75, 1.0, 1.25):
        lim = G.open - f * (G.open - G.pc_total)
        F = G[G.rth_lo < lim]; L = lim[F.index]
        if len(F) < 20: continue
        r = (F.close / L - 1).values; rn = (F.open_next_total / L - 1).values
        h1, h2 = (F.yr <= "2020").values, (F.yr >= "2021").values
        print(f"| {gl} | {f:.2f} | {len(F):,} | {len(F)/len(G)*100:.0f} | {np.mean(r)*100:+.2f} | {np.median(r)*100:+.2f} | {(r>0).mean()*100:.0f} | "
              f"{pf(r):.2f} | {pf(r[h1]):.2f} | {pf(r[h2]):.2f} | {pf(rn):.2f} |")
print()
