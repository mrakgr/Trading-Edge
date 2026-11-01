"""HighFlyer v5 + the FIRST 30 MINUTES' VOLATILITY (user 2026-11-01): "see if using the volatility of first 30m as a
feature makes a difference to it. Back then I had no idea that 30m volatility had such a strong effect".

The book: the causal port (highflyer_port.py --scale causal → data/equity/highflyer_repro/port_causal.parquet),
breadth lag1 > 0.5, entries from 2016-08 (the 1s corpus). The feature, as GapFader's GF-11 / GF-38 (volat_clk): the
mean |ln| change between consecutive 30-second slot vwaps (volume-weighted 1s vwaps) over 09:30–10:00 of the ENTRY
day, in bp — known at 10:00, six hours before the close entry. Cache: data/equity/highflyer_repro/volat30.parquet.

  cd research && python -u scripts/equity/highflyer_volat30.py > data/equity/highflyer_repro/volat30.md 2> logs/highflyer_volat30.log; echo exit $?
"""
import os, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
T = pd.read_parquet("data/equity/highflyer_repro/port_causal.parquet")
br = duckdb.sql("SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b1 FROM 'data/equity/momentum_v0/breadth.parquet'").df()
br["date"] = pd.to_datetime(br.date)
T = T.merge(br, left_on="entry_date", right_on="date", how="left").drop(columns="date")
T["d"] = T.entry_date.dt.strftime("%Y-%m-%d")
T = T[T.d >= "2016-08-01"].copy()
days = sorted(set(T.d) & {f[:10] for f in os.listdir("data/intraday_1s_slim")})
CACHE = "data/equity/highflyer_repro/volat30.parquet"
if os.path.exists(CACHE):
    V = pd.read_parquet(CACHE)
else:
    con = duckdb.connect(); con.execute("set memory_limit='1GB'; set threads=3; set enable_progress_bar=false"); parts = []
    for i, d in enumerate(days):
        con.register("tk", T[T.d == d][["symbol"]].drop_duplicates())
        X = con.execute(f"""
            WITH b AS (SELECT ticker, bucket, vwap::DOUBLE v, volume::DOUBLE vol FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                       WHERE ticker IN (SELECT symbol FROM tk) AND bucket >= 34200 AND bucket < 36000 AND volume > 0),
            c AS (SELECT ticker, (bucket - 34200) // 30 cslot, sum(v * vol) / sum(vol) sv FROM b GROUP BY ticker, cslot),
            cr AS (SELECT ticker, abs(ln(sv / lag(sv) OVER (PARTITION BY ticker ORDER BY cslot))) r FROM c)
            SELECT ticker symbol, avg(r) * 1e4 volat30, count(r) slots FROM cr GROUP BY ticker""").df()
        con.unregister("tk"); X["d"] = d; parts.append(X)
        if i % 200 == 0: log(f"  {i + 1}/{len(days)} {d}")
    V = pd.concat(parts, ignore_index=True); V.to_parquet(CACHE)
T = T.merge(V, on=["symbol", "d"], how="left")
log(f"{len(T):,} trips from 2016-08; volat30 for {T.volat30.notna().sum():,}")

def pf(r):
    r = r[np.isfinite(r)]; g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
B = T[(T.b1 > 0.5) & T.volat30.notna()].copy()
B["yr"] = B.d.str[:4]; EARLY = B.yr <= "2020"
def row(nm, s):
    d = B[s]
    if len(d) < 20: return f"| {nm} | {len(d)} | | | | | | | |"
    r = d.ret.values; yrs = d.groupby("yr").ret.apply(lambda x: pf(x.values))
    return (f"| {nm} | {len(d):,} | {100 * (r > 0).mean():.0f} | {pf(r):.2f} | **{pf(np.minimum(r, 0.5)):.2f}** | {100 * r.mean():+.2f} | "
            f"{100 * np.median(r):+.2f} | {pf(np.minimum(d.ret.values[EARLY[s].values], 0.5)):.2f} / {pf(np.minimum(d.ret.values[~EARLY[s].values], 0.5)):.2f} | "
            f"{int((yrs < 1).sum())}/{len(yrs)} |")
H = ("| cell | n | win % | PF raw | PF clip | mean % | median % | clip 2016–20 / 2021–26 | years < 1 (raw) |\n|---|---|---|---|---|---|---|---|---|")
v = B.volat30
print("# HighFlyer v5 × the first 30 minutes' volatility (volat30, bp)\n")
print(f"The causal book, breadth > 0.5, entries 2016-08..{B.d.max()}: {len(B):,} trips with the feature "
      f"({T[(T.b1 > 0.5)].volat30.isna().sum()} without 09:30–10:00 trades). volat30 median {v.median():.0f} bp "
      f"(p10 {v.quantile(.1):.0f} … p90 {v.quantile(.9):.0f}). ρ (Spearman) with the daily log ATR "
      f"{B[['volat30', 'atr_log']].corr(method='spearman').iloc[0, 1]:.2f}, with the move {B[['volat30', 'pct_up']].corr(method='spearman').iloc[0, 1]:.2f}, "
      f"with rvol {B[['volat30', 'rvol']].corr(method='spearman').iloc[0, 1]:.2f}.\n")
print("## Bands\n"); print(H)
print(row("all", v.notna()))
for a_, b_ in ((0, 20), (20, 30), (30, 40), (40, 60), (60, 100), (100, 1e9)):
    print(row(f"volat30 {a_}–{b_:g} bp" if b_ < 1e8 else f"volat30 ≥ {a_} bp", (v >= a_) & (v < b_)))
print("\n## Cumulative\n"); print(H)
for t in (20, 30, 40, 50, 60, 80):
    print(row(f"volat30 < {t} bp", v < t))
for t in (20, 30, 40, 60):
    print(row(f"volat30 ≥ {t} bp", v >= t))
print(row("volat30 < 100 bp (the wildest 7 % out)", v < 100))
print("\n## Quintiles of volat30 / the daily ATR (the morning's volatility RELATIVE to the stock's own)\n"); print(H)
B["rel"] = (B.volat30 / 1e4) / B.atr_log
q = B.rel.quantile([0.2, 0.4, 0.6, 0.8]).values
edges = [-np.inf, *q, np.inf]
for k in range(5):
    print(row(f"Q{k + 1}: rel {edges[k]:.2f}–{edges[k + 1]:.2f}", (B.rel >= edges[k]) & (B.rel < edges[k + 1])))
print(); print(H)
for t in (0.10, 0.14):
    print(row(f"rel < {t:g}", B.rel < t))
print(row("rel < 0.14 and volat30 < 100 bp", (B.rel < 0.14) & (v < 100)))
log("done")
