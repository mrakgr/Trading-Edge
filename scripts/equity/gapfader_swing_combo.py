"""GapFader GF-30 (user 2026-10-19): the gap-up INTRADAY SWING (held the open at 10:00, bought then, out at the close) by
COMBINATIONS of the 09:30–10:00 tape: low volatility × low trade count × VOLUME PERSISTENCE (the user: "low vol, low trade
count, but high-volume persistence might indicate a big player slowly accumulating").

  volat_clk  GF-11's 30 s clock-slot volatility, bp           (low = calm)
  trades30   the trade count 09:30–10:00                      (low = few separate decisions)
  vpers      volume 09:45–10:00 / volume 09:30–09:45          (≥ 1 = the volume did NOT decay after the open)
Thresholds FIXED in advance (no tuning): volat_clk < 40 bp (GF-11's line) and terciles / medians of the other two.
Every cell: n, median, PF raw / clip +50 %, net PF (0.7c / share), the two halves 2016–20 / 2021–26, losing years.
Sample: GF-10's (gap-up ≥ 4 %, rvol_pm ≥ 0.2, headline universe) — weak-premarket gap-ups are NOT in it.
Run from research/:  python -u scripts/equity/gapfader_swing_combo.py > data/equity/gapfader/swing_combo.md; echo exit $?
"""
import numpy as np, pandas as pd
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm >= 0.2) & (C.gap >= 0.04)].copy()
X = pd.read_parquet("data/equity/gapfader/tape_persistence.parquet")
V = pd.read_parquet("data/equity/gapfader/volat30.parquet")[["ticker", "date", "volat_clk"]]
C = C.merge(X, on=["ticker", "date"]).merge(V, on=["ticker", "date"], how="left")
C = C[(C.px_30 >= C.open) & np.isfinite(C.entry_30) & C.volat_clk.notna()].copy()
C["r"] = C.close / C.entry_30 - 1; C["rn"] = C.r - 0.007 / C.entry_30
C["trades30"] = C.tc_a + C.tc_b
C["vpers"] = C.vol_b / C.vol_a.replace(0, np.nan)
C["yr"] = C.date.str[:4]; early = C.yr <= "2020"
def pf(x):
    x = np.asarray(x); x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
H = "| cell | n | n / yr | median % | win % | PF | PF clip +50 % | **net PF** | net 2016–20 / 2021–26 | years < 1 (net) |\n|---|---|---|---|---|---|---|---|---|---|"
NY = C.yr.nunique()
def row(nm, m):
    d = C[m]
    if len(d) < 25: return f"| {nm} | {len(d)} | | | | | | | | |"
    yrs = d.groupby("yr").rn.apply(pf)
    return (f"| {nm} | {len(d):,} | {len(d)/NY:.0f} | {d.r.median()*100:+.2f} | {(d.r>0).mean()*100:.0f} | {pf(d.r):.2f} | "
            f"{pf(d.r.clip(upper=0.5)):.2f} | **{pf(d.rn):.2f}** | {pf(d.rn[early[m]]):.2f} / {pf(d.rn[~early[m]]):.2f} | {int((yrs<1).sum())}/{len(yrs)} |")

t1, t2 = C.trades30.quantile([1/3, 2/3]); p1, p2 = C.vpers.quantile([1/3, 2/3]); tm, pm_ = C.trades30.median(), C.vpers.median()
calm = C.volat_clk < 40
print(f"# GF-30 — the gap-up intraday swing by tape combinations ({len(C):,} held gap-ups, 10:00 → close)\n")
print(f"Fixed cuts: volat_clk < 40 bp; trades30 terciles {t1:,.0f} / {t2:,.0f} (median {tm:,.0f}); "
      f"volume persistence terciles {p1:.2f} / {p2:.2f} (median {pm_:.2f}; < 1 = the volume decays after the open)\n")
print("ρ (Spearman): " + ", ".join(f"{a}–{b} {C[[a,b]].corr(method='spearman').iloc[0,1]:.2f}" for a, b in
      [("volat_clk", "trades30"), ("volat_clk", "vpers"), ("trades30", "vpers")]) + "\n")

print("## 1. volatility × trade count\n"); print(H)
print(row("all held gap-ups", C.r.notna()))
for vn, vm in [("calm < 40", calm), ("not calm ≥ 40", ~calm)]:
    for tn, tmask in [("few trades (T1)", C.trades30 < t1), ("mid (T2)", (C.trades30 >= t1) & (C.trades30 < t2)), ("many (T3)", C.trades30 >= t2)]:
        print(row(f"{vn} · {tn}", vm & tmask))
print()
print("## 2. volume persistence alone, and inside the calm tape\n"); print(H)
for nm, m0 in [("all", C.r.notna()), ("calm < 40", calm), ("calm · few trades (< median)", calm & (C.trades30 < tm))]:
    for pn, pmask in [("decaying (P1)", C.vpers < p1), ("mid (P2)", (C.vpers >= p1) & (C.vpers < p2)), ("PERSISTENT (P3)", C.vpers >= p2)]:
        print(row(f"{nm} · {pn}", m0 & pmask))
print()
print("## 3. THE USER'S CELL: calm · few trades · persistent volume, and its neighbours\n"); print(H)
base = calm & (C.trades30 < tm)
print(row("calm · few trades (< median)", base))
print(row("… · volume persistence ≥ median", base & (C.vpers >= pm_)))
print(row("… · volume persistence < median", base & (C.vpers < pm_)))
print(row("… · volume persistence ≥ 1 (did not decay)", base & (C.vpers >= 1)))
print(row("calm · MANY trades · persistence ≥ median (control)", calm & (C.trades30 >= tm) & (C.vpers >= pm_)))
print()
print("## 4. by year, the user's cell (calm · few trades · persistence ≥ median), net\n")
d = C[base & (C.vpers >= pm_)]
print("| year | " + " | ".join(sorted(d.yr.unique())) + " |\n|---|" + "---|" * d.yr.nunique())
print("| n | " + " | ".join(str((d.yr == y).sum()) for y in sorted(d.yr.unique())) + " |")
print("| net PF | " + " | ".join(f"{pf(d.rn[d.yr == y]):.2f}" for y in sorted(d.yr.unique())) + " |")
print("\nby price: " + " · ".join(f"{a:g}–{b:g}: {pf(d.rn[(d.entry_30 >= a) & (d.entry_30 < b)]):.2f} [{((d.entry_30 >= a) & (d.entry_30 < b)).sum()}]"
                                 for a, b in [(1, 3), (3, 10), (10, 30), (30, 1e9)]))
print("by gap: " + " · ".join(f"{a*100:g}–{b*100:g} %: {pf(d.rn[(d.gap >= a) & (d.gap < b)]):.2f} [{((d.gap >= a) & (d.gap < b)).sum()}]"
                                for a, b in [(.04, .06), (.06, .10), (.10, .20), (.20, 9)]))
print("by rvol_pm: " + " · ".join(f"{a:g}–{b:g}: {pf(d.rn[(d.rvol_pm >= a) & (d.rvol_pm < b)]):.2f} [{((d.rvol_pm >= a) & (d.rvol_pm < b)).sum()}]"
                                    for a, b in [(.2, .5), (.5, 1), (1, 5), (5, 1e9)]))

# ---- 5. BAR COUNTS on the calm tape (user 2026-10-19): act30 = active 1s bars 09:30–10:00 (max 1,800)
C["act30"] = C.act_a + C.act_b
C["apers"] = C.act_b / C.act_a.replace(0, np.nan)
K = C[calm]
a1, a2 = K.act30.quantile([1/3, 2/3]); am = K.act30.median(); tmc = K.trades30.median(); ap1, ap2 = K.apers.quantile([1/3, 2/3])
print(f"\n## 5. BAR COUNTS on the calm tape (volat_clk < 40 bp, {calm.sum():,} trades)\n")
print(f"act30 terciles (calm) {a1:,.0f} / {a2:,.0f} (median {am:,.0f} of 1,800); trades30 median (calm) {tmc:,.0f}; "
      f"bar persistence terciles {ap1:.2f} / {ap2:.2f}; ρ(act30, trades30) in calm = {K[['act30','trades30']].corr(method='spearman').iloc[0,1]:.2f}\n")
print(H)
for nm, m in [("calm · few bars (A1)", calm & (C.act30 < a1)), ("calm · mid bars (A2)", calm & (C.act30 >= a1) & (C.act30 < a2)),
              ("calm · many bars (A3)", calm & (C.act30 >= a2))]:
    print(row(nm, m))
print()
print("**substitution — bars × trades, each split at its calm median**\n"); print(H)
for bn, bm in [("few bars", C.act30 < am), ("many bars", C.act30 >= am)]:
    for tn, tmk in [("few trades", C.trades30 < tmc), ("many trades", C.trades30 >= tmc)]:
        print(row(f"calm · {bn} · {tn}", calm & bm & tmk))
print()
print("**bar persistence (active bars 09:45–10:00 / 09:30–09:45)**\n"); print(H)
for nm, m in [("calm · bars fading (BP1)", calm & (C.apers < ap1)), ("calm · mid (BP2)", calm & (C.apers >= ap1) & (C.apers < ap2)),
              ("calm · bars persisting (BP3)", calm & (C.apers >= ap2))]:
    print(row(nm, m))
