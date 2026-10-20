"""GapFader GF-33 (user 2026-10-20): the gap-UP intraday swing when the open FAILS. GF-10/11/30 bought only gap-ups that HELD
the open at 10:00. The user's hypothesis: the hold may not matter; a gap-up may fade in the first 30 min and then trend up
smoothly, so a long from 10:00 (or even from the open) to the close may have an edge whether or not the open held, on a
CALM tape.

Sample: gap-up >= 4 %, headline universe (open >= $1, dv20_prior >= $1M, no early close), rvol_pm >= 0.2 (user: the GF-10
sample for now). The 09:30–10:00 tape is GF-10's tape_persistence.parquet + GF-11's volat30.parquet (both cover held AND failed):
  volat_clk  30 s clock-slot vwaps, mean |ln r| in bp (GF-11)
  px_30      the last 1s vwap before 10:00        entry_30  the first 1s vwap at/after 10:00
  act_a/act_b, tc_a/tc_b  active 1s bars and trades in 09:30–09:45 / 09:45–10:00
held = px_30 >= the official open; failed = px_30 < open. calm = volat_clk < 40 bp; activity fading = act_b / act_a < 0.82.
Trades, net 0.7c / share:
  10:00 → close   bought at entry_30: knowable, tradeable.
  open → close    bought at the official open. ⚠ Split by 10:00's hold or volatility, this is NOT tradeable (it needs the
                  first 30 min); it is shown only to describe the day's path.
Run from research/:  python -u scripts/equity/gapfader_gapup_failed.py > data/equity/gapfader/gapup_failed.md; echo exit $?
"""
import numpy as np, pandas as pd
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm >= 0.2) & (C.gap >= 0.04)].copy()
X = pd.read_parquet("data/equity/gapfader/tape_persistence.parquet")[["ticker", "date", "act_a", "act_b", "tc_a", "tc_b", "px_30", "entry_30"]]
V = pd.read_parquet("data/equity/gapfader/volat30.parquet")[["ticker", "date", "volat_clk"]]
C = C.merge(X, on=["ticker", "date"]).merge(V, on=["ticker", "date"], how="left")
for c in ["act_a", "act_b", "tc_a", "tc_b"]: C[c] = C[c].fillna(0)
C = C[np.isfinite(C.entry_30) & np.isfinite(C.px_30) & C.volat_clk.notna() & (C.act_a > 0)].copy()
C["r"] = C.close / C.entry_30 - 1; C["rn"] = C.r - 0.007 / C.entry_30          # 10:00 → close
C["ro"] = C.close / C.open - 1; C["ron"] = C.ro - 0.007 / C.open                # open → close (descriptive)
C["r30"] = C.px_30 / C.open - 1                                                 # open → 10:00
C["apers"] = C.act_b / C.act_a; C["yr"] = C.date.str[:4]; early = C.yr <= "2020"
held, calm, fading = C.px_30 >= C.open, C.volat_clk < 40, C.apers < 0.82
NY = C.yr.nunique()
def pf(x):
    x = np.asarray(x); x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
H = ("| cell | n | n / yr | median % | win % | PF | PF clip +50 % | **net PF** | net 2016–20 / 2021–26 | years < 1 (net) |\n"
     "|---|---|---|---|---|---|---|---|---|---|")
def row(nm, m, r="r", rn="rn"):
    d = C[m]
    if len(d) < 25: return f"| {nm} | {len(d)} | | | | | | | | |"
    yrs = d.groupby("yr")[rn].apply(pf)
    return (f"| {nm} | {len(d):,} | {len(d)/NY:.0f} | {d[r].median()*100:+.2f} | {(d[r]>0).mean()*100:.0f} | {pf(d[r]):.2f} | "
            f"{pf(d[r].clip(upper=0.5)):.2f} | **{pf(d[rn]):.2f}** | {pf(d[rn][early[m]]):.2f} / {pf(d[rn][~early[m]]):.2f} | {int((yrs<1).sum())}/{len(yrs)} |")
print(f"# GF-33 — the gap-up when the open FAILS: 10:00 → close, and the day from the open\n")
print(f"{len(C):,} gap-ups ≥ 4 % with a 10:00 entry; held at 10:00 {held.mean()*100:.0f} %, calm (volat_clk < 40 bp) "
      f"{calm.mean()*100:.0f} %, activity fading (< 0.82) {fading.mean()*100:.0f} %.\n")

print("## 1. 10:00 → close: held vs failed × the tape (tradeable)\n"); print(H)
print(row("all gap-ups", C.r.notna()))
for hn, hm in [("held", held), ("**FAILED**", ~held)]:
    print(row(f"{hn}", hm))
    print(row(f"{hn} · calm", hm & calm)); print(row(f"{hn} · not calm", hm & ~calm))
    print(row(f"{hn} · calm · fading", hm & calm & fading)); print(row(f"{hn} · calm · not fading", hm & calm & ~fading))
    print(row(f"{hn} · wild (≥ 80 bp)", hm & (C.volat_clk >= 80)))
print()

print("## 2. FAILED opens, calm: by how far below the open at 10:00 (10:00 → close)\n"); print(H)
for a, b in [(0, 0.02), (0.02, 0.05), (0.05, 0.10), (0.10, 9)]:
    print(row(f"calm · {a*100:g}–{b*100:g} % below the open" if b < 9 else f"calm · ≥ {a*100:g} % below the open",
              ~held & calm & (-C.r30 > a) & (-C.r30 <= b)))
print()

print("## 3. by premarket volume (rvol_pm), 10:00 → close\n"); print(H)
for a, b in [(0.2, 0.5), (0.5, 1), (1, 5), (5, 1e9)]:
    nm = f"{a:g}–{b:g}" if b < 1e8 else f"≥ {a:g}"
    for hn, hm in [("held", held), ("failed", ~held)]:
        print(row(f"rvol_pm {nm} · {hn} · calm", (C.rvol_pm >= a) & (C.rvol_pm < b) & hm & calm))
print()

print("## 4. by gap size, calm, 10:00 → close\n"); print(H)
for a, b in [(0.04, 0.06), (0.06, 0.10), (0.10, 0.20), (0.20, 9)]:
    g = (C.gap >= a) & (C.gap < b); nm = f"gap {a*100:g}–{b*100:g} %" if b < 9 else f"gap ≥ {a*100:g} %"
    print(row(f"{nm} · held · calm", g & held & calm)); print(row(f"{nm} · failed · calm", g & ~held & calm))
print()

print("## 5. ⚠ DESCRIPTIVE (not tradeable): open → close, split by what 10:00 showed\n")
print("Does buying at the open pay on the days that turn out calm? The split needs the first 30 min, so this only describes the path.\n")
print(H)
print(row("all gap-ups, open → close", C.ro.notna(), "ro", "ron"))
for nm, m in [("calm", calm), ("not calm", ~calm), ("calm · held", calm & held), ("calm · failed", calm & ~held)]:
    print(row(f"{nm}, open → close", m, "ro", "ron"))
print(f"\nmedian open → 10:00: calm {C.r30[calm].median()*100:+.2f} %, not calm {C.r30[~calm].median()*100:+.2f} %; "
      f"calm · failed {C.r30[calm & ~held].median()*100:+.2f} %\n")

d = C[~held & calm]
print("## 6. FAILED · calm by year, net PF (10:00 → close)\n\n| year | " + " | ".join(sorted(d.yr.unique())) + " |\n|---|" + "---|" * d.yr.nunique())
print("| n | " + " | ".join(str((d.yr == y).sum()) for y in sorted(d.yr.unique())) + " |")
print("| net PF | " + " | ".join(f"{pf(d.rn[d.yr == y]):.2f}" for y in sorted(d.yr.unique())) + " |")
