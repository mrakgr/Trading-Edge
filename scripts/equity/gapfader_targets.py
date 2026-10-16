"""GapFader GF-14 (user 2026-10-16): TARGETS for the weak-premarket gap-DOWN long (rvol_pm < 0.02, gap ≤ −4 %, bought at the
official open). A sell LIMIT at the target, working from the open; filled only on a TRADE-THROUGH (the first RTH 1s vwap
ABOVE the target), at the target. Not hit by the time stop → sold at the first 1s vwap at/after the stop (10:00 / 10:15).
Targets: a fraction f of the gap (open + f × (prev close − open); f = 1 = the gap filled) and a fixed k % above the open.
Net of 0.7c / share round trip. Headline universe; SPY's gap as GF-3. Writes data/equity/gapfader/targets.parquet.
Run from research/:  python -u scripts/equity/gapfader_targets.py > data/equity/gapfader/targets.md 2> data/equity/gapfader/targets.log; echo exit $?
"""
import os, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
S = duckdb.sql("""WITH d AS (SELECT date, open, close, n, cum_div, lag(close) OVER w pcr, lag(n) OVER w np_, lag(cum_div) OVER w cdp
   FROM 'data/v2/daily_adjusted.parquet' WHERE ticker='SPY' WINDOW w AS (ORDER BY date))
   SELECT date::VARCHAR date, open/(pcr*np_/n-(cum_div-cdp)/n)-1 spy_gap FROM d""").df()
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm.fillna(0) < 0.02) & (C.gap <= -0.04)].merge(S, on="date")
FR = (0.25, 0.5, 0.75, 1.0); KS = (0.01, 0.02, 0.03, 0.05)
STOPS = (36000, 36900)                                  # 10:00, 10:15
OUT = "data/equity/gapfader/targets.parquet"
if os.path.exists(OUT):
    V = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []
    for i, d in enumerate(sorted(C.date.unique())):
        gd = C[C.date == d][["ticker", "open", "pc_total"]]
        con.register("gd", gd)
        cols = []
        for f in FR:
            for st in STOPS:
                cols.append(f"min(bucket) FILTER (v > gd.open + {f} * (gd.pc_total - gd.open) AND bucket < {st}) AS tf_{int(f*100)}_{st}")
        for k in KS:
            for st in STOPS:
                cols.append(f"min(bucket) FILTER (v > gd.open * {1 + k} AND bucket < {st}) AS tk_{int(k*100)}_{st}")
        cols += [f"arg_min(v, bucket) FILTER (bucket >= {st}) AS x_{st}" for st in STOPS]
        V = con.execute(f"""SELECT s.ticker, {', '.join(cols)}
            FROM (SELECT ticker, bucket, vwap::DOUBLE v FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                  WHERE ticker IN (SELECT ticker FROM gd) AND bucket >= 34200 AND bucket < 57600 AND volume > 0) s
            JOIN gd ON gd.ticker = s.ticker GROUP BY s.ticker""").df()
        con.unregister("gd"); V["date"] = d; parts.append(V)
        if i % 400 == 0: log(f"{i+1} {d}")
    V = pd.concat(parts, ignore_index=True); V.to_parquet(OUT); log(f"wrote {OUT}: {len(V):,}")
C = C.merge(V, on=["ticker", "date"], how="inner")
C["yr"] = C.date.str[:4]; C["rel"] = C.spy_gap * np.sign(C.gap)
def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
def run(P, tgt, hitcol, st):
    hit = P[hitcol].notna()
    px = np.where(hit, tgt, P[f"x_{st}"])
    r = px / P.open - 1 - 0.007 / P.open
    return hit, pd.Series(r, index=P.index)
def table(P, lab):
    print(f"### {lab} — {len(P):,} trades\n")
    print("| target | stop | hit % | mean net % | median % | win % | **PF net** | 2016–20 / 2021–26 | years < 1 |")
    print("|---|---|---|---|---|---|---|---|---|")
    for st, sl in ((36000, "10:00"), (36900, "10:15")):
        r = (P[f"x_{st}"] / P.open - 1 - 0.007 / P.open)
        print(f"| none (time exit) | {sl} | – | {r.mean()*100:+.2f} | {r.median()*100:+.2f} | {(r>0).mean()*100:.0f} | **{pf(r.values):.2f}** | "
              f"{pf(r[P.yr<='2020'].values):.2f} / {pf(r[P.yr>='2021'].values):.2f} | {sum(pf(r[P.yr==y].values)<1 for y in P.yr.unique())}/{P.yr.nunique()} |")
        for f in FR:
            hit, r = run(P, P.open + f * (P.pc_total - P.open), f"tf_{int(f*100)}_{st}", st)
            print(f"| {f*100:.0f} % of the gap | {sl} | {hit.mean()*100:.0f} | {r.mean()*100:+.2f} | {r.median()*100:+.2f} | {(r>0).mean()*100:.0f} | **{pf(r.values):.2f}** | "
                  f"{pf(r[P.yr<='2020'].values):.2f} / {pf(r[P.yr>='2021'].values):.2f} | {sum(pf(r[P.yr==y].values)<1 for y in P.yr.unique())}/{P.yr.nunique()} |")
        for k in KS:
            hit, r = run(P, P.open * (1 + k), f"tk_{int(k*100)}_{st}", st)
            print(f"| +{k*100:.0f} % | {sl} | {hit.mean()*100:.0f} | {r.mean()*100:+.2f} | {r.median()*100:+.2f} | {(r>0).mean()*100:.0f} | **{pf(r.values):.2f}** | "
                  f"{pf(r[P.yr<='2020'].values):.2f} / {pf(r[P.yr>='2021'].values):.2f} | {sum(pf(r[P.yr==y].values)<1 for y in P.yr.unique())}/{P.yr.nunique()} |")
    print()
P = C[C.x_36000.notna() & C.x_36900.notna()]
print("# GF-14 — TARGETS on the weak-premarket gap-DOWN long (a sell limit from the open; a trade-through fills at the target; else the time stop)\n")
print(f"{len(P):,} trades, {P.date.min()} … {P.date.max()}. Net of 0.7c / share round trip.\n")
table(P, "all days")
table(P[P.rel < 0.01], "ex with-market")
table(P[P.rel >= 0.01], "with-market (SPY down ≥ 1 %)")
print("### net PF by price (all days, stop 10:15): time exit vs the best targets\n")
print("| price | n | time exit | 25 % gap | 50 % gap | +1 % | +2 % |"); print("|---|---|---|---|---|---|---|")
for lo, hi in ((1, 2), (2, 3), (3, 5), (5, 10), (10, 20), (20, 50), (50, 1e9)):
    Q = P[(P.open >= lo) & (P.open < hi)]
    t0 = Q.x_36900 / Q.open - 1 - 0.007 / Q.open
    vals = [pf(t0.values)]
    for tgt, col in ((Q.open + 0.25 * (Q.pc_total - Q.open), "tf_25_36900"), (Q.open + 0.5 * (Q.pc_total - Q.open), "tf_50_36900"),
                     (Q.open * 1.01, "tk_1_36900"), (Q.open * 1.02, "tk_2_36900")):
        vals.append(pf(run(Q, tgt, col, 36900)[1].values))
    print(f"| ${lo}–{hi:g} | {len(Q):,} | " + " | ".join(f"{v:.2f}" for v in vals) + " |")
