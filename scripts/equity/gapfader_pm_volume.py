"""GapFader GF-15 (user 2026-10-16): the weak-premarket gap-DOWN long — how much VOLUME is there before and after the open, and
could a PREMARKET limit buy (a maker, a rebate) beat the opening-auction entry? Per gap (raw 1s bars, $ = vwap × volume):
  premarket $ volume 04:00–07:00 / 07:00–09:00 / 09:00–09:30; the opening cross ($ of the 09:30:00 bar); $ 09:30–09:35 / –10:00
  premarket prices vs the official open: the 09:00–09:30 vwap, the last premarket print, the premarket low
  premarket $ that traded BELOW the open (04:00–09:30 and 09:00–09:30) — what a buy limit under the open could have taken
Headline universe. Writes data/equity/gapfader/pm_volume.parquet.
Run from research/:  python -u scripts/equity/gapfader_pm_volume.py > data/equity/gapfader/pm_volume.md 2> data/equity/gapfader/pm_volume.log; echo exit $?
"""
import os, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm.fillna(0) < 0.02) & (C.gap <= -0.04)].copy()
OUT = "data/equity/gapfader/pm_volume.parquet"
if os.path.exists(OUT):
    V = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []
    for i, d in enumerate(sorted(C.date.unique())):
        gd = C[C.date == d][["ticker", "open"]]
        con.register("gd", gd)
        V = con.execute(f"""SELECT s.ticker,
              sum(v * vol) FILTER (bucket < 25200) dv_0400, sum(v * vol) FILTER (bucket >= 25200 AND bucket < 32400) dv_0700,
              sum(v * vol) FILTER (bucket >= 32400 AND bucket < 34200) dv_0900,
              sum(v * vol) FILTER (bucket = 34200) dv_cross, sum(vol) FILTER (bucket = 34200) vol_cross,
              sum(v * vol) FILTER (bucket > 34200 AND bucket < 34500) dv_5m, sum(v * vol) FILTER (bucket > 34200 AND bucket < 36000) dv_30m,
              sum(v * vol) FILTER (bucket >= 32400 AND bucket < 34200) / nullif(sum(vol) FILTER (bucket >= 32400 AND bucket < 34200), 0) vwap_0900,
              arg_max(v, bucket) FILTER (bucket < 34200) last_pm, min(v) FILTER (bucket < 34200) low_pm,
              count(*) FILTER (bucket >= 32400 AND bucket < 34200) act_0900,
              sum(v * vol) FILTER (bucket < 34200 AND v < gd.open) dv_below, sum(v * vol) FILTER (bucket >= 32400 AND bucket < 34200 AND v < gd.open) dv_below_0900,
              sum(v * vol) FILTER (bucket >= 32400 AND bucket < 34200 AND v < gd.open * 0.99) dv_below1_0900
            FROM (SELECT ticker, bucket, vwap::DOUBLE v, volume::DOUBLE vol FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                  WHERE ticker IN (SELECT ticker FROM gd) AND bucket < 36000 AND volume > 0) s
            JOIN gd ON gd.ticker = s.ticker GROUP BY s.ticker""").df()
        con.unregister("gd"); V["date"] = d; parts.append(V)
        if i % 400 == 0: log(f"{i+1} {d}")
    V = pd.concat(parts, ignore_index=True); V.to_parquet(OUT); log(f"wrote {OUT}: {len(V):,}")
C = C.merge(V, on=["ticker", "date"], how="left")
for c in ("dv_0400", "dv_0700", "dv_0900", "dv_cross", "dv_5m", "dv_30m", "dv_below", "dv_below_0900", "dv_below1_0900", "act_0900"):
    C[c] = C[c].fillna(0)
C["dv_pm"] = C.dv_0400 + C.dv_0700 + C.dv_0900
def q(s): return f"{s.quantile(0.25):,.0f} / **{s.median():,.0f}** / {s.quantile(0.75):,.0f}"
print("# GF-15 — VOLUME around the open for the weak-premarket gap-DOWN long, and the premarket entry\n")
print(f"{len(C):,} gaps (rvol_pm < 0.02, gap ≤ −4 %, headline universe). $ volume, p25 / **median** / p75 per gap.\n")
print("## $ volume by window, by price\n")
print("| price | n | 04:00–07:00 | 07:00–09:00 | 09:00–09:30 | **opening cross** | 09:30–09:35 | 09:30–10:00 | dv20_prior (a day) |")
print("|---|---|---|---|---|---|---|---|---|")
for lo, hi in ((1, 3), (3, 10), (10, 30), (30, 1e9), (1, 1e9)):
    P = C[(C.open >= lo) & (C.open < hi)]
    print(f"| ${lo}–{hi:g} | {len(P):,} | {q(P.dv_0400)} | {q(P.dv_0700)} | {q(P.dv_0900)} | {q(P.dv_cross)} | {q(P.dv_5m)} | {q(P.dv_30m)} | {q(P.dv20_prior)} |")
print("\n## Premarket activity: share of gaps with ANY premarket trade, and with trades in 09:00–09:30\n")
print("| price | any premarket trade | trades 09:00–09:30 | active seconds 09:00–09:30, median (of 1,800) |"); print("|---|---|---|---|")
for lo, hi in ((1, 3), (3, 10), (10, 30), (30, 1e9), (1, 1e9)):
    P = C[(C.open >= lo) & (C.open < hi)]
    print(f"| ${lo}–{hi:g} | {(P.dv_pm > 0).mean()*100:.0f} % | {(P.dv_0900 > 0).mean()*100:.0f} % | {P.act_0900.median():.0f} |")
print("\n## Premarket PRICES vs the official open (gaps with premarket trades)\n")
P = C[C.dv_0900 > 0]
for col, lab in (("vwap_0900", "the 09:00–09:30 vwap"), ("last_pm", "the last premarket print")):
    x = (P[col] / P.open - 1) * 100
    print(f"- {lab} vs the open: p10 {x.quantile(0.1):+.2f} %, p25 {x.quantile(0.25):+.2f} %, median {x.median():+.2f} %, p75 {x.quantile(0.75):+.2f} %, p90 {x.quantile(0.9):+.2f} %; below the open in {(x < 0).mean()*100:.0f} %")
x = (C[C.dv_pm > 0].low_pm / C[C.dv_pm > 0].open - 1) * 100
print(f"- the premarket LOW vs the open: median {x.median():+.2f} %, p25 {x.quantile(0.25):+.2f} %; below the open in {(x < 0).mean()*100:.0f} %, ≥ 1 % below in {(x <= -1).mean()*100:.0f} %")
print("\n## Premarket $ that traded BELOW the open — what a buy limit under the open could have filled\n")
print("| price | median $ below the open, 04:00–09:30 | median $ below, 09:00–09:30 | median $ ≥ 1 % below, 09:00–09:30 | share of gaps with ≥ $5k below, 09:00–09:30 |")
print("|---|---|---|---|---|")
for lo, hi in ((1, 3), (3, 10), (10, 30), (30, 1e9), (1, 1e9)):
    P = C[(C.open >= lo) & (C.open < hi)]
    print(f"| ${lo}–{hi:g} | {P.dv_below.median():,.0f} | {P.dv_below_0900.median():,.0f} | {P.dv_below1_0900.median():,.0f} | {(P.dv_below_0900 >= 5000).mean()*100:.0f} % |")
print("\n## Capacity: a position as a share of the opening cross\n")
for share in (0.05, 0.10, 0.20):
    s = C.dv_cross * share
    print(f"- {share*100:.0f} % of the cross: median ${s.median():,.0f}, p25 ${s.quantile(0.25):,.0f}, p75 ${s.quantile(0.75):,.0f}; ≥ $1,000 in {(s >= 1000).mean()*100:.0f} % of gaps, ≥ $5,000 in {(s >= 5000).mean()*100:.0f} %")
