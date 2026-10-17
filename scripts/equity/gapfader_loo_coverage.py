"""GapFader GF-18 (user 2026-10-16: "how will we even know where these stocks open? the premarket is thin"). The entry as a
LIMIT-ON-OPEN at prev close × 0.96: the auction fills it only if the stock OPENS ≥ 4 % down, at the open price — the gap
condition without knowing the open. The question becomes which names get a LOO before the 09:25 cutoff. Candidates = a
premarket print before 09:25 at ≤ prev close × (1 − x). Measured on every weak-premarket (rvol_pm < 0.02) gap-down ≥ 2 %
of the census (the ones opening −2 … −4 % are the LOOs that do NOT fill): coverage of the ≥ 4 % trades, LOOs sent per day,
and the edge of the trades a premarket screen MISSES. Exit as GF-14 (target 50 % of the gap / 10:15), net 0.7c.
Writes data/equity/gapfader/loo_coverage.parquet.
Run from research/:  python -u scripts/equity/gapfader_loo_coverage.py > data/equity/gapfader/loo_coverage.md 2> data/equity/gapfader/loo_coverage.log; echo exit $?
"""
import os, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm.fillna(0) < 0.02) & (C.gap <= -0.02)].copy()
OUT = "data/equity/gapfader/loo_coverage.parquet"
if os.path.exists(OUT):
    V = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []
    for i, d in enumerate(sorted(C.date.unique())):
        tl = ",".join("'" + t.replace("'", "''") + "'" for t in C[C.date == d].ticker)
        V = con.execute(f"""SELECT ticker, arg_max(vwap::DOUBLE, bucket) FILTER (bucket < 33900) last_0925,
              min(vwap::DOUBLE) FILTER (bucket < 33900) low_0925, max(bucket) FILTER (bucket < 33900) t_last,
              sum(volume::DOUBLE) FILTER (bucket < 33900) vol_0925
            FROM read_parquet('data/intraday_1s_slim/{d}.parquet') WHERE ticker IN ({tl}) AND bucket < 33900 AND volume > 0 GROUP BY ticker""").df()
        V["date"] = d; parts.append(V)
        if i % 400 == 0: log(f"{i+1} {d}")
    V = pd.concat(parts, ignore_index=True); V.to_parquet(OUT); log(f"wrote {OUT}: {len(V):,}")
C = C.merge(V, on=["ticker", "date"], how="left")
C = C.merge(pd.read_parquet("data/equity/gapfader/targets.parquet")[["ticker", "date", "tf_50_36900", "x_36900"]], on=["ticker", "date"], how="left")
C["yr"] = C.date.str[:4]
C["pmgap"] = C.last_0925 / C.pc_total - 1                       # the last premarket print before 09:25 vs the prev close
C["pmlow"] = C.low_0925 / C.pc_total - 1
T = C[(C.gap <= -0.04) & C.x_36900.notna()].copy()               # the trades (opened ≥ 4 % down)
x = np.where(T.tf_50_36900.notna(), T.open + 0.5 * (T.pc_total - T.open), T.x_36900)
T["r"] = x / T.open - 1 - 0.007 / T.open
def pf(v):
    v = v[np.isfinite(v)]; g, l = v[v > 0].sum(), -v[v < 0].sum(); return np.inf if l == 0 else g / l
days = C.date.nunique()
print("# GF-18 — the entry as a LIMIT-ON-OPEN at prev close × 0.96; which names get one before 09:25?\n")
print(f"{len(T):,} trades (opened ≥ 4 % down, rvol_pm < 0.02) of {len(C):,} weak-premarket gap-downs ≥ 2 % on {days:,} days.\n")
print(f"- a premarket print before 09:25: {T.last_0925.notna().mean()*100:.0f} % of the trades; the last one at a median "
      f"{pd.to_datetime(T.t_last.dropna().median(), unit='s').strftime('%H:%M')} ET (p25 {pd.to_datetime(T.t_last.dropna().quantile(0.25), unit='s').strftime('%H:%M')})")
q = T.pmgap.dropna() * 100
print(f"- the last print before 09:25 vs the prev close, on the trades: p10 {q.quantile(0.1):+.1f} %, p25 {q.quantile(0.25):+.1f} %, median {q.median():+.1f} %, p75 {q.quantile(0.75):+.1f} %, p90 {q.quantile(0.9):+.1f} %")
print(f"- vs the actual open gap: the premarket print is ABOVE the open in {(T.last_0925 > T.open).mean()*100:.0f} % (the slide into the open)\n")
print("## A premarket screen: a LOO for every name whose last print before 09:25 is ≤ −x % (or whose premarket LOW is)\n")
print("| screen | trades covered | covered: PF net | MISSED: n / PF net | LOOs sent per day | of which fill (open ≥ 4 % down) |")
print("|---|---|---|---|---|---|")
for col, lab in (("pmgap", "last print"), ("pmlow", "premarket low")):
    for xx in (0.0, 0.01, 0.02, 0.03, 0.04):
        cov = T[col] <= -xx
        sent = (C[col] <= -xx)
        print(f"| {lab} ≤ −{xx*100:.0f} % | {cov.mean()*100:.0f} % | {pf(T[cov].r.values):.2f} | {(~cov).sum():,} / {pf(T[~cov].r.values):.2f} | "
              f"{sent.sum()/days:.1f} (+ gaps < 2 % not in the census) | {(sent & (C.gap <= -0.04)).sum() / max(1, sent.sum())*100:.0f} % |")
print("\n## The trades with NO premarket print before 09:25 (invisible to any screen)\n")
N = T[T.last_0925.isna()]
print(f"n {len(N):,} ({len(N)/len(T)*100:.0f} %), PF net {pf(N.r.values):.2f}, mean {N.r.mean()*100:+.2f} %; by price: " +
      "  ".join(f"${lo}–{hi:g} {pf(N[(N.open>=lo)&(N.open<hi)].r.values):.2f}({((N.open>=lo)&(N.open<hi)).sum()})" for lo, hi in ((1,3),(3,10),(10,30),(30,1e9))))
