"""GapFader GF-24 (user 2026-10-17: "a correlation study of the opening cross with the average daily dollar volume; do gaps in
more liquid stocks make for better trades?"). For every weak-premarket gap-down >= 4 % (the trade): the OPENING CROSS from
the bulk trades tape — the largest print carrying condition 17 (opening trade) between 09:28 and 10:00 ET (the listing
exchange's cross; other venues print small openers; the condition-16 copy of the same print is not counted) — its size,
price and time. Then (1) the cross's dollars vs dv20_prior (the 20 prior sessions' mean dollar volume, known before the
open), and (2) GF-21's trade (the LOO at the open; 75 % of the gap until 10:00, else the maker exit; 0.40c) by liquidity.
The cross dollars are the capacity: what a position at the open can be.
Cached in data/equity/gapfader/cross_liquidity.parquet.
Run from research/:  python -u scripts/equity/gapfader_cross_liquidity.py > data/equity/gapfader/cross_liquidity.md 2> data/equity/gapfader/cross_liquidity.log; echo exit $?
"""
import os, sys, time, datetime as dt
from zoneinfo import ZoneInfo
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
ET = ZoneInfo("America/New_York")
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm.fillna(0) < 0.02) & (C.gap <= -0.04)].copy()
OUT = "data/equity/gapfader/cross_liquidity.parquet"
def ns(d, h, m):
    return int(dt.datetime(*map(int, d.split("-")), h, m, tzinfo=ET).timestamp()) * 1_000_000_000
if os.path.exists(OUT):
    V = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); con.execute("SET threads=4"); parts = []
    for i, d in enumerate(sorted(C.date.unique())):
        tl = ",".join("'" + t.replace("'", "''") + "'" for t in C[C.date == d].ticker)
        V = con.execute(f"""SELECT ticker, max(size) AS cross_sh, arg_max(price, size) AS cross_px, arg_max(sip_timestamp, size) AS cross_ts,
                   count(*) AS n17
            FROM read_parquet('data/bulk/trades/{d}.parquet')
            WHERE ticker IN ({tl}) AND sip_timestamp >= {ns(d, 9, 28)} AND sip_timestamp < {ns(d, 10, 0)} AND list_contains(conditions, 17)
            GROUP BY ticker""").df()
        V["date"] = d; parts.append(V)
        if i % 200 == 0: log(f"{i + 1} {d}")
    V = pd.concat(parts, ignore_index=True); V.to_parquet(OUT); log(f"wrote {OUT}: {len(V):,}")

C = C.merge(V, on=["ticker", "date"], how="left")
C = C.merge(pd.read_parquet("data/equity/gapfader/late_stops.parquet")[["ticker", "date", "tf_75", "x_36000"]], on=["ticker", "date"], how="left")
C = C[C.x_36000.notna()].copy()
C["tf_75"] = C.tf_75.astype("float64").fillna(np.inf)
C["r"] = np.where(C.tf_75 < 36000, C.open + 0.75 * (C.pc_total - C.open), C.x_36000) / C.open - 1 - 0.004 / C.open
C["yr"] = C.date.str[:4]
C["cross_usd"] = (C.cross_sh * C.cross_px).fillna(0)
C["cross_sec"] = (pd.to_datetime(C.cross_ts, unit="ns", utc=True).dt.tz_convert(ET) - pd.to_datetime(C.date).dt.tz_localize(ET)).dt.total_seconds() - 34200
C["ratio"] = C.cross_usd / C.dv20_prior
def pf(v):
    v = v[np.isfinite(v)]; g, l = v[v > 0].sum(), -v[v < 0].sum(); return np.inf if l == 0 else g / l
yrs = C.yr.nunique(); ex = C.yr != "2020"
print(f"# GF-24 — the opening cross vs the daily dollar volume, and the trade by liquidity ({len(C):,} trades, 2016–2026)\n")
print(f"A condition-17 cross found for {C.cross_sh.notna().mean():.1%}; px = the official open for "
      f"{(np.abs(C.cross_px / C.open - 1) < 1e-6).sum() / C.cross_sh.notna().sum():.1%} of those; the cross at or after 09:31: "
      f"{(C.cross_sec >= 60).mean():.1%}, after 09:35: {(C.cross_sec >= 300).mean():.1%}.\n")
print(f"Spearman ρ(cross $, dv20_prior) = {C.cross_usd.corr(C.dv20_prior, method='spearman'):.2f}; "
      f"log-log OLS slope = {np.polyfit(np.log(C.dv20_prior[C.cross_usd > 0]), np.log(C.cross_usd[C.cross_usd > 0]), 1)[0]:.2f}\n")
B = [(1e6, 2e6), (2e6, 5e6), (5e6, 1e7), (1e7, 2.5e7), (2.5e7, 1e8), (1e8, 1e13)]
def nm(lo, hi): return f"${lo / 1e6:g}M–{hi / 1e6:g}M" if hi < 1e13 else f"≥ ${lo / 1e6:g}M"
print("## 1. the cross by dv20_prior\n")
print("| dv20_prior | trades / yr | cross $: p25 | median | p75 | cross / dv20: median | p25 | p75 | no cross |")
print("|---|---|---|---|---|---|---|---|---|")
for lo, hi in B:
    m = (C.dv20_prior >= lo) & (C.dv20_prior < hi); c = C.cross_usd[m]; q = C.ratio[m]
    print(f"| {nm(lo, hi)} | {m.sum() / yrs:,.0f} | ${c.quantile(.25):,.0f} | ${c.median():,.0f} | ${c.quantile(.75):,.0f} | {q.median():.2%} | {q.quantile(.25):.2%} | {q.quantile(.75):.2%} | {(c == 0).mean():.1%} |")
print("\n## 2. the trade by dv20_prior (GF-21's exit) — all years | WITHOUT 2020\n")
print("| dv20_prior | trades / yr | PF−1 | avg % | PF−1 ex-2020 | avg % ex-2020 | median % ex-2020 | PF clip ex-2020 | losing yrs ex-2020 |")
print("|---|---|---|---|---|---|---|---|---|")
for lo, hi in B:
    m = (C.dv20_prior >= lo) & (C.dv20_prior < hi); x = m & ex
    ly = sum(pf(C.r[x & (C.yr == y)]) < 1 for y in C.yr.unique() if y != "2020" and (x & (C.yr == y)).sum() >= 20)
    ny = sum((x & (C.yr == y)).sum() >= 20 for y in C.yr.unique() if y != "2020")
    print(f"| {nm(lo, hi)} | {m.sum() / yrs:,.0f} | {pf(C.r[m]) - 1:.2f} | {C.r[m].mean():+.2%} | **{pf(C.r[x]) - 1:.2f}** | {C.r[x].mean():+.2%} | {C.r[x].median():+.2%} | {pf(C.r[x].clip(-.5, .5)):.2f} | {ly}/{ny} |")
print("\n## 3. the trade by the CROSS's own dollars (known only AT the open: capacity, not a gate) — WITHOUT 2020\n")
print("| cross $ | trades / yr | PF−1 | avg % | median % | median dv20_prior |\n|---|---|---|---|---|---|")
for lo, hi in [(0, 1), (1, 5e3), (5e3, 2.5e4), (2.5e4, 1e5), (1e5, 5e5), (5e5, 1e13)]:
    x = ex & (C.cross_usd >= lo) & (C.cross_usd < hi)
    name = "none" if hi == 1 else (f"${lo / 1e3:g}k–{hi / 1e3:g}k" if hi < 1e13 else f"≥ ${lo / 1e3:g}k")
    print(f"| {name} | {x.sum() / 10:,.0f} | {pf(C.r[x]) - 1:.2f} | {C.r[x].mean():+.2%} | {C.r[x].median():+.2%} | ${C.dv20_prior[x].median() / 1e6:.1f}M |")
C[["ticker", "date", "gap", "open", "dv20_prior", "cross_sh", "cross_px", "cross_sec", "cross_usd", "ratio", "r"]].to_parquet("data/equity/gapfader/cross_liquidity_trades.parquet", index=False)
log("done")
