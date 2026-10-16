"""GapFader GF-9 (user 2026-10-16): heavy-premarket gap-DOWNs (rvol_pm > 1, gap ≤ −4 %) — a LONG on a buy LIMIT k % below
the official open, working all day (does the limit-entry result of the gap-ups, GF-6, repeat?). Fill: the FIRST RTH 1s
bar whose vwap is BELOW the limit (a trade-through), at the LIMIT price. Out at the official close (and the next open).
k = 0: the open itself. Headline universe, no costs. Writes data/equity/gapfader/gapdown_dip.parquet.
Run from research/:  python -u scripts/equity/gapfader_gapdown_dip.py > data/equity/gapfader/gapdown_dip.md 2> data/equity/gapfader/gapdown_dip.log; echo exit $?
"""
import os, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm > 1) & (C.gap <= -0.04)].copy()
KS = (0.02, 0.04, 0.06, 0.08, 0.10, 0.15, 0.20, 0.30)
OUT = "data/equity/gapfader/gapdown_dip.parquet"
if os.path.exists(OUT):
    X = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []
    days = sorted(C.date.unique())
    for i, d in enumerate(days):
        gd = C[C.date == d][["ticker", "open"]]
        con.register("gd", gd)
        cols = [f"min(bucket) FILTER (v < gd.open * {1 - k}) AS t_{int(k*100)}" for k in KS]
        X = con.execute(f"""SELECT s.ticker, {', '.join(cols)}
            FROM (SELECT ticker, bucket, vwap::DOUBLE v FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                  WHERE ticker IN (SELECT ticker FROM gd) AND bucket >= 34200 AND bucket < 57600) s
            JOIN gd ON gd.ticker = s.ticker GROUP BY s.ticker""").df()
        con.unregister("gd")
        X["date"] = d; parts.append(X)
        if i % 300 == 0: log(f"{i+1}/{len(days)} {d}")
    X = pd.concat(parts, ignore_index=True); X.to_parquet(OUT); log(f"wrote {OUT}: {len(X):,}")
C = C.merge(X, on=["ticker", "date"], how="inner")
C["yr"] = C.date.str[:4]
def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
def stats(r, yr):
    r = pd.Series(r).reset_index(drop=True); yr = pd.Series(yr).reset_index(drop=True)
    ok = np.isfinite(r); r, yr = r[ok], yr[ok]
    trim = r[r < r.quantile(0.95)]
    top = r.sort_values(ascending=False).head(max(1, len(r) // 20)).sum() / max(r[r > 0].sum(), 1e-12)
    return (f"{len(r):,} | {r.mean()*100:+.2f} | {r.median()*100:+.2f} | {(r>0).mean()*100:.0f} | {pf(r.values):.2f} | "
            f"{pf(r.clip(upper=0.5).values):.2f} | {pf(trim.values):.2f} | {top*100:.0f} % | "
            f"{pf(r[(yr <= '2020').values].values):.2f} / {pf(r[(yr >= '2021').values].values):.2f}")
HDR = "| n | mean % | median % | win % | PF raw | PF clip +50 % | PF top 5 % trimmed | top 5 % = gains | PF 2016–20 / 2021–26 |"
print("# GF-9 — a buy LIMIT k % below the open on heavy-premarket gap-DOWNs (rvol_pm > 1), held to the close\n")
print(f"{len(C):,} gaps, {C.date.min()} … {C.date.max()}. Fill = the first RTH 1s vwap below the limit, at the limit. No costs.\n")
for glo, ghi, gl in ((0.04, 9, "all ≤ −4 %"), (0.04, 0.10, "−4 … −10 %"), (0.10, 0.20, "−10 … −20 %"), (0.20, 9, "≤ −20 %")):
    G = C[(-C.gap >= glo) & (-C.gap < ghi)]
    print(f"## gap {gl} — {len(G):,} gaps\n")
    print("| limit below the open | filled % " + HDR + " PF → next open |"); print("|---" * 12 + "|")
    r = G.close / G.open - 1; rn = G.open_next_total / G.open - 1
    print(f"| 0 (the open) | 100 | {stats(r.values, G.yr.values)} | {pf(rn.values[np.isfinite(rn.values)]):.2f} |")
    for k in KS:
        F = G[G[f"t_{int(k*100)}"].notna()]; L = F.open * (1 - k)
        r = F.close / L - 1; rn = F.open_next_total / L - 1
        print(f"| {k*100:.0f} % | {len(F)/len(G)*100:.0f} | {stats(r.values, F.yr.values)} | {pf(rn.values[np.isfinite(rn.values)]):.2f} |")
    print("\nBy the fill time (limits 4 % and 10 %):\n")
    print("| limit | filled " + HDR); print("|---" * 11 + "|")
    for k in (0.04, 0.10):
        t = G[f"t_{int(k*100)}"]
        for lo, hi, lab in ((34200, 36000, "before 10:00"), (36000, 39600, "10:00–11:00"), (39600, 57600, "after 11:00")):
            F = G[(t >= lo) & (t < hi)]
            if len(F) < 20: continue
            r = F.close / (F.open * (1 - k)) - 1
            print(f"| {k*100:.0f} % | {lab} | {stats(r.values, F.yr.values)} |")
    print()
