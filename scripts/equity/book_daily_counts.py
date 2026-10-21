"""How often do LOW-SIGNAL DAYS happen? (user 2026-10-21, after the live week 10-14..10-20 gave 10 / 9 / 6 / 5 / 1 tickers)
Tickers per day in the four production reference books (FlushFaderV2 + LowFader + SpikeFader + Snoozer), 2020-01-02 .. 2026-07-17
(the span all four books cover), every trading day of the 1s corpus counted (a day with no trade = 0). Then: the distribution by
year, how often a day or a 5-day stretch is as thin as the live week, whether thin days cluster, and what drives the count (the
number of universe names that flushed or spiked that day, from the daily bars).
Run from research/:  python -u scripts/equity/book_daily_counts.py > data/flushfaderv2/daily_counts.md 2> data/flushfaderv2/daily_counts.log; echo exit $?
"""
import os, sys, time, glob
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
G = "data/flushfader_gate_review"
D0, D1 = "2020-01-02", "2026-07-17"
LIVE = [("2026-10-14", 10), ("2026-10-15", 9), ("2026-10-16", 6), ("2026-10-19", 5), ("2026-10-20", 1)]
con = duckdb.connect()
cal = sorted(os.path.basename(f)[:10] for f in glob.glob("data/intraday_1s_slim/*.parquet"))
cal = [d for d in cal if D0 <= d <= D1]
B = con.execute(f"""
    SELECT 'flush' sys, symbol, trade_date d FROM 'data/flushfaderv2/flushfaderv2_reference_book.parquet' UNION ALL
    SELECT 'low', symbol, trade_date FROM '{G}/lowfader_reference_book.parquet' UNION ALL
    SELECT 'spike', symbol, trade_date FROM '{G}/spikefader_reference_book.parquet' UNION ALL
    SELECT 'snooze', symbol, trade_date::VARCHAR FROM '{G}/snoozer_reference_book.parquet'""").df()
B = B[(B.d >= D0) & (B.d <= D1)]
N = pd.DataFrame({"d": cal})
N["n"] = N.d.map(B.groupby("d").symbol.nunique()).fillna(0).astype(int)
for s in ["flush", "low", "spike", "snooze"]:
    N[s] = N.d.map(B[B.sys == s].groupby("d").symbol.nunique()).fillna(0).astype(int)
N["yr"] = N.d.str[:4]
log(f"{len(N)} days, {len(B):,} book trades")

# the driver: universe names that flushed (low <= -10 % vs the previous close) or spiked (high >= +20 %) that day
DRV = "data/flushfaderv2/daily_counts_driver.parquet"
if not os.path.exists(DRV):
    con.execute(f"""COPY (
        SELECT c.date::VARCHAR d, count(*) n_univ,
               count(*) FILTER (p.low / c.close_m1 - 1 <= -0.10) n_flush10,
               count(*) FILTER (p.high / c.close_m1 - 1 >= 0.20) n_spike20,
               median(abs(p.close / c.close_m1 - 1)) med_absret
        FROM read_parquet('data/v2_candidates/mr_candidate_1s_v2/*/*.parquet', hive_partitioning = true) c
        JOIN 'data/v2/daily_prices.parquet' p ON p.ticker = c.ticker AND p.date = c.date
        WHERE c.date BETWEEN '{D0}' AND '{D1}' AND c.close_m1 > 0
        GROUP BY 1) TO '{DRV}' (FORMAT parquet)""")
N = N.merge(pd.read_parquet(DRV), on="d", how="left")
log("driver merged")

def q(x, p): return float(np.quantile(x, p))
print("# How often are signal days thin? (the four production books, tickers per day)\n")
print(f"Span {D0} .. {D1}: {len(N):,} trading days, {len(B):,} book trades. A ticker counts once per day whatever its "
      f"systems. The live week for comparison: " + " · ".join(f"{d[5:]} {n}" for d, n in LIVE) + " (5-day mean 6.2).\n")
print("## 1. Tickers per day, by year\n")
print("| year | days | mean | p5 | p10 | p25 | median | p75 | days ≤ 1 | days ≤ 3 | days ≤ 5 | FlushFader mean | others mean |")
print("|---|---|---|---|---|---|---|---|---|---|---|---|---|")
for y, g in list(N.groupby("yr")) + [("all", N)]:
    print(f"| {y} | {len(g)} | {g.n.mean():.1f} | {q(g.n,.05):.0f} | {q(g.n,.10):.0f} | {q(g.n,.25):.0f} | {g.n.median():.0f} | "
          f"{q(g.n,.75):.0f} | {(g.n<=1).sum()} ({(g.n<=1).mean()*100:.1f} %) | {(g.n<=3).sum()} ({(g.n<=3).mean()*100:.1f} %) | "
          f"{(g.n<=5).sum()} ({(g.n<=5).mean()*100:.1f} %) | {g.flush.mean():.1f} | {(g.low+g.spike+g.snooze).mean():.1f} |")

print("\n## 2. Stretches: the rolling 5-day mean\n")
N["m5"] = N.n.rolling(5).mean(); N["m20"] = N.n.rolling(20).mean()
print("| year | 5-day windows | mean ≤ 6.2 (the live week) | mean ≤ 8 | mean ≤ 10 | lowest 5-day mean | its last day |")
print("|---|---|---|---|---|---|---|")
for y, g in list(N.groupby("yr")) + [("all", N)]:
    g = g[g.m5.notna()]; i = g.m5.idxmin()
    print(f"| {y} | {len(g)} | {(g.m5<=6.2).sum()} ({(g.m5<=6.2).mean()*100:.1f} %) | {(g.m5<=8).mean()*100:.1f} % | "
          f"{(g.m5<=10).mean()*100:.1f} % | {g.m5.min():.1f} | {N.d[i]} |")

print("\n## 3. Do thin days cluster?\n")
ac = {k: N.n.autocorr(k) for k in (1, 5, 20, 60)}
print("Autocorrelation of the daily count: " + " · ".join(f"lag {k}: {v:.2f}" for k, v in ac.items()) + "\n")
thin = N.n <= q(N.n, .10)
print(f"A thin day = the bottom 10 % (≤ {q(N.n,.10):.0f} tickers). P(thin) = {thin.mean()*100:.1f} %; "
      f"P(thin | yesterday thin) = {thin[thin.shift(1, fill_value=False)].mean()*100:.1f} %; "
      f"P(thin | one of the last 5 thin) = {thin[thin.rolling(5).max().shift(1, fill_value=0).astype(bool)].mean()*100:.1f} %.\n")
print("The daily count against the 20-day mean that came before it (is a thin day just a thin month?):\n")
N["m20p"] = N.n.rolling(20).mean().shift(1)
N["band"] = pd.cut(N.m20p, [0, 6, 9, 12, 16, 20, 1e9], labels=["< 6", "6–9", "9–12", "12–16", "16–20", "≥ 20"], right=False)
print("| prior 20-day mean | days | mean today | p10 today | days ≤ 1 | days ≤ 5 |")
print("|---|---|---|---|---|---|")
for b, g in N[N.m20p.notna()].groupby("band", observed=True):
    print(f"| {b} | {len(g)} | {g.n.mean():.1f} | {q(g.n,.10):.0f} | {(g.n<=1).mean()*100:.1f} % | {(g.n<=5).mean()*100:.1f} % |")

print("\n## 4. What drives the count: the day's flushes and spikes in the universe\n")
M = N[N.n_flush10.notna()]
print(f"Correlation with the daily count: names down ≥ 10 % intraday ρ = {M.n.corr(M.n_flush10):.2f}; up ≥ 20 % ρ = "
      f"{M.n.corr(M.n_spike20):.2f}; the universe's median |close-to-close| ρ = {M.n.corr(M.med_absret):.2f}; "
      f"universe size ρ = {M.n.corr(M.n_univ):.2f}. FlushFader alone vs flushes ρ = {M.flush.corr(M.n_flush10):.2f}.\n")
M = M.assign(fb=pd.qcut(M.n_flush10, 10, duplicates="drop"))
print("| names down ≥ 10 % (decile) | days | mean tickers | p10 | days ≤ 1 | days ≤ 5 | FlushFader mean |")
print("|---|---|---|---|---|---|---|")
for b, g in M.groupby("fb", observed=True):
    print(f"| {b} | {len(g)} | {g.n.mean():.1f} | {q(g.n,.10):.0f} | {(g.n<=1).mean()*100:.1f} % | {(g.n<=5).mean()*100:.1f} % | {g.flush.mean():.1f} |")

print("\n## 5. The thinnest days\n")
print("| day | tickers | FlushFader | Low | Spike | Snoozer | names down ≥ 10 % | up ≥ 20 % | prior 20-day mean |")
print("|---|---|---|---|---|---|---|---|---|")
for _, r in N.sort_values(["n", "d"]).head(20).iterrows():
    print(f"| {r.d} | {r.n} | {r.flush} | {r.low} | {r.spike} | {r.snooze} | {r.n_flush10:.0f} | {r.n_spike20:.0f} | {r.m20p:.1f} |")
N.to_parquet("data/flushfaderv2/daily_counts.parquet")
log("done")
