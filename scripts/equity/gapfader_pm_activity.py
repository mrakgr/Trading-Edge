"""GapFader GF-26 (user 2026-10-17: "instead of rvol, bar counts in the hour before the open? a stock can have block trades but
little activity" + "a breakdown of how the trades behave when the features disagree"). Every gap-down >= 4 % (open >= $1,
dv20_prior >= $1M, no early close; ALL premarket volumes, not only rvol_pm < 0.02). Features from the 1s bars, 08:30–09:30
(bucket 30600–34199): active 1s bars (volume > 0) and the trade count; rvol_pm (census: 04:00–09:30 volume / avgvol20_prior).
The outcome = GF-21's exit (bought at the open; 75 % of the gap until 10:00 on a trade-through, else the first 1s vwap at or
after 10:00; 0.40c a share). Cached in data/equity/gapfader/pm_activity.parquet.
Run from research/:  python -u scripts/equity/gapfader_pm_activity.py > data/equity/gapfader/pm_activity.md 2> data/equity/gapfader/pm_activity.log; echo exit $?
"""
import os, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.gap <= -0.04)].copy()
OUT = "data/equity/gapfader/pm_activity.parquet"
if os.path.exists(OUT):
    V = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []
    for i, d in enumerate(sorted(C.date.unique())):
        gd = C[C.date == d][["ticker", "open", "pc_total"]]
        con.register("gd", gd)
        V = con.execute(f"""SELECT s.ticker,
                   count(*) FILTER (bucket >= 30600 AND bucket < 34200) AS bars_60,
                   sum(trade_count) FILTER (bucket >= 30600 AND bucket < 34200) AS trades_60,
                   sum(volume::DOUBLE) FILTER (bucket >= 30600 AND bucket < 34200) AS vol_60,
                   min(bucket) FILTER (bucket >= 34200 AND v > gd.open + 0.75 * (gd.pc_total - gd.open)) AS tf_75,
                   arg_min(v, bucket) FILTER (bucket >= 36000) AS x_36000
            FROM (SELECT ticker, bucket, vwap::DOUBLE v, volume, trade_count FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                  WHERE ticker IN (SELECT ticker FROM gd) AND bucket >= 30600 AND bucket < 57600 AND volume > 0) s
            JOIN gd ON gd.ticker = s.ticker GROUP BY s.ticker""").df()
        con.unregister("gd"); V["date"] = d; parts.append(V)
        if i % 400 == 0: log(f"{i + 1} {d}")
    V = pd.concat(parts, ignore_index=True); V.to_parquet(OUT); log(f"wrote {OUT}: {len(V):,}")
C = C.merge(V, on=["ticker", "date"], how="left")
C = C[C.x_36000.notna()].copy()
for c in ("bars_60", "trades_60", "vol_60"): C[c] = C[c].fillna(0)
C["tf_75"] = C.tf_75.astype("float64").fillna(np.inf)
C["r"] = np.where(C.tf_75 < 36000, C.open + 0.75 * (C.pc_total - C.open), C.x_36000) / C.open - 1 - 0.004 / C.open
C["rvol_pm"] = C.rvol_pm.fillna(0)
C["yr"] = C.date.str[:4]
EX = C.yr != "2020"
def pf(v):
    v = v[np.isfinite(v)]; g, l = v[v > 0].sum(), -v[v < 0].sum(); return np.inf if l == 0 else g / l
RB = [(0, 0.005, "< 0.005"), (0.005, 0.02, "0.005–0.02"), (0.02, 0.05, "0.02–0.05"), (0.05, 0.2, "0.05–0.2"), (0.2, 1e9, "≥ 0.2")]
BB = [(0, 1, "0"), (1, 10, "1–9"), (10, 30, "10–29"), (30, 100, "30–99"), (100, 300, "100–299"), (300, 1000, "300–999"), (1000, 1e9, "≥ 1000")]
yrs = C.yr.nunique()
print(f"# GF-26 — premarket ACTIVITY (1s bars 08:30–09:30) vs rvol_pm, gap-downs ≥ 4 % ({len(C):,} trades, {yrs} years)\n")
print(f"Spearman ρ: bars_60 vs rvol_pm {C.bars_60.corr(C.rvol_pm, method='spearman'):.2f}; bars_60 vs trades_60 {C.bars_60.corr(C.trades_60, method='spearman'):.2f}; "
      f"bars_60 vs dv20_prior {C.bars_60.corr(C.dv20_prior, method='spearman'):.2f}; rvol_pm vs dv20_prior {C.rvol_pm.corr(C.dv20_prior, method='spearman'):.2f}\n")
def one(col, bands, title):
    print(f"## by {title} — all years | WITHOUT 2020\n")
    print("| band | trades / yr | PF−1 | avg % | PF−1 ex-2020 | avg % ex-2020 | median % ex-2020 | PF clip ex-2020 | losing yrs ex-2020 |")
    print("|---|---|---|---|---|---|---|---|---|")
    for lo, hi, nm in bands:
        m = (C[col] >= lo) & (C[col] < hi); x = m & EX
        ny = [y for y in C.yr.unique() if y != "2020" and (x & (C.yr == y)).sum() >= 20]
        ly = sum(pf(C.r[x & (C.yr == y)]) < 1 for y in ny)
        print(f"| {nm} | {m.sum() / yrs:,.0f} | {pf(C.r[m]) - 1:.2f} | {C.r[m].mean():+.2%} | **{pf(C.r[x]) - 1:.2f}** | {C.r[x].mean():+.2%} | "
              f"{C.r[x].median():+.2%} | {pf(C.r[x].clip(-.5, .5)):.2f} | {ly}/{len(ny)} |")
    print()
one("rvol_pm", RB, "rvol_pm")
one("bars_60", BB, "active 1s bars 08:30–09:30")
print("## THE DISAGREEMENT: rvol_pm (rows) × active bars 08:30–09:30 (columns) — PF−1 ex-2020 (trades ex-2020)\n")
print("| rvol_pm \\ bars | " + " | ".join(b[2] for b in BB) + " |\n|---|" + "---|" * len(BB))
for rlo, rhi, rn in RB:
    cells = []
    for blo, bhi, bn in BB:
        x = EX & (C.rvol_pm >= rlo) & (C.rvol_pm < rhi) & (C.bars_60 >= blo) & (C.bars_60 < bhi)
        cells.append(f"{pf(C.r[x]) - 1:.2f} ({x.sum():,})" if x.sum() >= 100 else (f"· ({x.sum()})" if x.sum() else ""))
    print(f"| {rn} | " + " | ".join(cells) + " |")
print("\n## the same, avg % ex-2020\n")
print("| rvol_pm \\ bars | " + " | ".join(b[2] for b in BB) + " |\n|---|" + "---|" * len(BB))
for rlo, rhi, rn in RB:
    cells = []
    for blo, bhi, bn in BB:
        x = EX & (C.rvol_pm >= rlo) & (C.rvol_pm < rhi) & (C.bars_60 >= blo) & (C.bars_60 < bhi)
        cells.append(f"{C.r[x].mean():+.2%}" if x.sum() >= 100 else "")
    print(f"| {rn} | " + " | ".join(cells) + " |")
log("done")
