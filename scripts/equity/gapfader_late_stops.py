"""GapFader GF-23 (user 2026-10-17: "a test to see whether a longer stop would help" — GF-21: the deep gaps lose from the
10:00 stop). The weak-premarket gap-down long as GF-21 (the LOO in the cross; a resting sell limit at f of the gap until
the STOP, filled on a trade-through at the target; else the maker exit at the stop = the first 1s vwap at/after it;
0.40c a share round trip), the stop moved from 10:00 to 15:55, by gap band. One pass over the 1s bars (the target's
first trade-through, no stop filter, and the exit price at every stop); cached in data/equity/gapfader/late_stops.parquet.
Run from research/:  python -u scripts/equity/gapfader_late_stops.py > data/equity/gapfader/late_stops.md 2> data/equity/gapfader/late_stops.log; echo exit $?
"""
import os, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm.fillna(0) < 0.02) & (C.gap <= -0.04)].copy()
FR = (0.5, 0.75, 1.0)
STOPS = {"10:00": 36000, "10:15": 36900, "10:30": 37800, "11:00": 39600, "12:00": 43200, "13:00": 46800, "14:00": 50400, "15:55": 57300}
OUT = "data/equity/gapfader/late_stops.parquet"
if os.path.exists(OUT):
    V = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []
    for i, d in enumerate(sorted(C.date.unique())):
        gd = C[C.date == d][["ticker", "open", "pc_total"]]
        con.register("gd", gd)
        cols = [f"min(bucket) FILTER (v > gd.open + {f} * (gd.pc_total - gd.open)) AS tf_{int(f * 100)}" for f in FR]
        cols += [f"arg_min(v, bucket) FILTER (bucket >= {st}) AS x_{st}" for st in STOPS.values()]
        V = con.execute(f"""SELECT s.ticker, {', '.join(cols)}
            FROM (SELECT ticker, bucket, vwap::DOUBLE v FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                  WHERE ticker IN (SELECT ticker FROM gd) AND bucket >= 34200 AND bucket < 57600 AND volume > 0) s
            JOIN gd ON gd.ticker = s.ticker GROUP BY s.ticker""").df()
        con.unregister("gd"); V["date"] = d; parts.append(V)
        if i % 400 == 0: log(f"{i + 1} {d}")
    V = pd.concat(parts, ignore_index=True); V.to_parquet(OUT); log(f"wrote {OUT}: {len(V):,}")
C = C.merge(V, on=["ticker", "date"], how="inner")
for f in FR: C[f"tf_{int(f * 100)}"] = C[f"tf_{int(f * 100)}"].astype("float64").fillna(np.inf)   # never hit = inf
C = C[np.all([C[f"x_{st}"].notna() for st in STOPS.values()], axis=0)].copy()
C["yr"] = C.date.str[:4]
FEE = 0.004
def pf(v):
    v = v[np.isfinite(v)]; g, l = v[v > 0].sum(), -v[v < 0].sum(); return np.inf if l == 0 else g / l
def ret(f, st):
    tgt = C.open + f * (C.pc_total - C.open)
    x = np.where(C[f"tf_{int(f * 100)}"] < st, tgt, C[f"x_{st}"])
    return pd.Series(x / C.open - 1 - FEE / C.open, index=C.index)
yrs = C.yr.nunique()
bands = [(-0.06, -0.04), (-0.08, -0.06), (-0.10, -0.08), (-0.15, -0.10), (-0.20, -0.15), (-0.30, -0.20), (-9, -0.30), (-9, -0.15), (-9, -0.04)]
print(f"# GF-23 — the stop moved later ({len(C):,} trades, {yrs} years)\n")
for f in FR:
    R = {k: ret(f, st) for k, st in STOPS.items()}
    print(f"\n## target {f:.0%} of the gap — PF−1 (avg %)\n")
    print("| gap | trades / yr | " + " | ".join(STOPS) + " |\n|---|---|" + "---|" * len(STOPS))
    for lo, hi in bands:
        m = (C.gap > lo) & (C.gap <= hi)
        name = "ALL ≥ 4 %" if (lo, hi) == (-9, -0.04) else "≥ 15 %" if (lo, hi) == (-9, -0.15) else f"{-hi:.0%} … {-lo:.0%}" if lo > -9 else f"≥ {-hi:.0%}"
        print(f"| {name} | {m.sum() / yrs:,.0f} | " + " | ".join(f"{pf(r[m]) - 1:.2f} ({r[m].mean():+.2%})" for r in R.values()) + " |")
    m = C.gap <= -0.15
    print(f"\n≥ 15 %, losing years (of {yrs}) and PF clip ±50 % by stop: " + "; ".join(
        f"{k} {sum(pf(r[m & (C.yr == y)]) < 1 for y in C.yr.unique())} / {pf(r[m].clip(-.5, .5)):.2f}" for k, r in R.items()))
    print(f"target hit by the stop (≥ 15 %): " + "; ".join(f"{k} {(C[f'tf_{int(f * 100)}'] < st)[m].mean():.0%}" for k, st in STOPS.items()))
log("done")
