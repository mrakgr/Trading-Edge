"""GapFader GF-42 (user 2026-10-20): the gap-up LONG swing candidate from GF-39/40 — calm (volat_clk < 40 bp) · held the open at
10:00 · rvol30 >= 1 — split by dv20, price, gap and rvol_pm (as GF-41 did for the gap-down short), to find the segment where the
PF is decent. Bought at 10:00 (entry_30) and 10:30 (entry_60), out at the close, net 0.7c / share. GF-38's cache (every rvol_pm).
Run from research/:  python -u scripts/equity/gapfader_swing_segments.py > data/equity/gapfader/swing_segments.md; echo exit $?
"""
import numpy as np, pandas as pd
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.gap >= 0.04)].copy()
C = C.merge(pd.read_parquet("data/equity/gapfader/swing_allpm.parquet"), on=["ticker", "date"])
C = C[(C.px_30 >= C.open) & np.isfinite(C.entry_30) & C.volat_clk.notna() & (C.act_a > 0) & C.rvol_30m.notna()].copy()
C["yr"] = C.date.str[:4]; early = C.yr <= "2020"; NY = C.yr.nunique()
for k, e in [("10", "entry_30"), ("1030", "entry_60")]:
    C[f"r_{k}"] = C.close / C[e] - 1; C[f"rn_{k}"] = C[f"r_{k}"] - 0.007 / C[e]
calm = C.volat_clk < 40; fad = C.act_b / C.act_a < 0.82
def pf(x):
    x = np.asarray(x); x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
H = ("| cell | n | n / yr | median % | win % | PF | PF clip +50 % | **net PF 10:00** | net 2016–20 / 2021–26 | years < 1 | **net PF 10:30** | years < 1 |\n"
     "|---|---|---|---|---|---|---|---|---|---|---|---|")
def row(nm, m):
    d = C[m & np.isfinite(C.rn_10) & np.isfinite(C.rn_1030)]
    if len(d) < 25: return f"| {nm} | {len(d)} | | | | | | | | | | |"
    y10 = d.groupby("yr").rn_10.apply(pf); y30 = d.groupby("yr").rn_1030.apply(pf); e = d.yr <= "2020"
    return (f"| {nm} | {len(d):,} | {len(d)/NY:.0f} | {d.r_10.median()*100:+.2f} | {(d.r_10>0).mean()*100:.0f} | {pf(d.r_10):.2f} | "
            f"{pf(d.r_10.clip(upper=0.5)):.2f} | **{pf(d.rn_10):.2f}** | {pf(d.rn_10[e]):.2f} / {pf(d.rn_10[~e]):.2f} | {int((y10<1).sum())}/{len(y10)} | "
            f"**{pf(d.rn_1030):.2f}** | {int((y30<1).sum())}/{len(y30)} |")
SPLITS = {
    "rvol_pm": [("< 0.02", lambda: C.rvol_pm < 0.02), ("0.02–0.2", lambda: (C.rvol_pm >= 0.02) & (C.rvol_pm < 0.2)),
                ("0.2–1", lambda: (C.rvol_pm >= 0.2) & (C.rvol_pm < 1)), ("1–5", lambda: (C.rvol_pm >= 1) & (C.rvol_pm < 5)), ("≥ 5", lambda: C.rvol_pm >= 5)],
    "rvol30": [("1–2", lambda: C.rvol_30m < 2), ("2–5", lambda: (C.rvol_30m >= 2) & (C.rvol_30m < 5)), ("≥ 5", lambda: C.rvol_30m >= 5)],
    "gap": [("4–6 %", lambda: C.gap < 0.06), ("6–10 %", lambda: (C.gap >= 0.06) & (C.gap < 0.10)),
            ("10–20 %", lambda: (C.gap >= 0.10) & (C.gap < 0.20)), ("20–40 %", lambda: (C.gap >= 0.20) & (C.gap < 0.40)), ("≥ 40 %", lambda: C.gap >= 0.40)],
    "price": [("$1–3", lambda: C.entry_30 < 3), ("$3–10", lambda: (C.entry_30 >= 3) & (C.entry_30 < 10)),
              ("$10–30", lambda: (C.entry_30 >= 10) & (C.entry_30 < 30)), ("$30–100", lambda: (C.entry_30 >= 30) & (C.entry_30 < 100)), ("≥ $100", lambda: C.entry_30 >= 100)],
    "dv20": [("$1–5M", lambda: C.dv20_prior < 5e6), ("$5–25M", lambda: (C.dv20_prior >= 5e6) & (C.dv20_prior < 25e6)),
             ("$25–100M", lambda: (C.dv20_prior >= 25e6) & (C.dv20_prior < 100e6)), ("≥ $100M", lambda: C.dv20_prior >= 100e6)],
    "fading": [("fading", lambda: fad), ("not fading", lambda: ~fad)],
}
base = calm & (C.rvol_30m >= 1)
print("# GF-42 — the gap-up long swing (calm · held at 10:00 · rvol30 ≥ 1) by segment\n")
print(H); print(row("calm · held (any rvol30)", calm)); print(row("**calm · held · rvol30 ≥ 1**", base)); print()
for k, segs in SPLITS.items():
    print(f"## by {k}\n"); print(H)
    for nm, f in segs: print(row(f"base · {k} {nm}", base & f()))
    print()
# two-way: the two strongest graders crossed
print("## two-way: price × rvol_pm (base), net PF 10:00 [n, losing years]\n")
PM = [("< 0.2", C.rvol_pm < 0.2), ("≥ 0.2", C.rvol_pm >= 0.2)]
def cell(m):
    d = C[m & np.isfinite(C.rn_10)]
    if len(d) < 25: return f"· [{len(d)}]"
    y = d.groupby("yr").rn_10.apply(pf); return f"**{pf(d.rn_10):.2f}** [{len(d)}, {int((y<1).sum())}/{len(y)}]"
for k in ["price", "dv20", "gap"]:
    print(f"| {k} \\ rvol_pm | " + " | ".join(n for n, _ in PM) + " |\n|---|---|---|")
    for nm, f in SPLITS[k]: print(f"| {nm} | " + " | ".join(cell(base & f() & pm) for _, pm in PM) + " |")
    print()
