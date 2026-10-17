"""GapFader GF-17 (user 2026-10-16: "the crosses are thin — a limit at the open price for more volume?"). A buy LIMIT at the
official open price, placed after the cross (09:30:01) and working until 09:35 / 09:50; filled at the open price on a
trade-through (a 1s vwap BELOW the open — conservative) or a touch (≤ the open — optimistic). Measures the $ that traded at or
below the open after the cross (the volume such a limit competes for) and the fills' quality: exit 10:15, or a target at
50 % of the gap after the fill. Net 0.55c / share (a maker entry). Versus the MOO. By the market (SPY gap vs the stock).
Writes data/equity/gapfader/limit_at_open.parquet.
Run from research/:  python -u scripts/equity/gapfader_limit_at_open.py > data/equity/gapfader/limit_at_open.md 2> data/equity/gapfader/limit_at_open.log; echo exit $?
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
OUT = "data/equity/gapfader/limit_at_open.parquet"
if os.path.exists(OUT):
    V = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []
    for i, d in enumerate(sorted(C.date.unique())):
        gd = C[C.date == d][["ticker", "open", "pc_total"]]
        con.register("gd", gd)
        src = f"""(SELECT ticker, bucket, vwap::DOUBLE v, volume::DOUBLE vol FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                   WHERE ticker IN (SELECT ticker FROM gd) AND bucket > 34200 AND bucket < 57600 AND volume > 0)"""
        V = con.execute(f"""SELECT s.ticker,
              sum(v * vol) FILTER (bucket <= 34260 AND v <= gd.open) dv_le_1m, sum(v * vol) FILTER (bucket <= 34500 AND v <= gd.open) dv_le_5m,
              sum(v * vol) FILTER (bucket <= 35400 AND v <= gd.open) dv_le_20m,
              sum(v * vol) FILTER (bucket <= 34500) dv_5m_all,
              min(bucket) FILTER (bucket < 34500 AND v < gd.open) f_lt_5, min(bucket) FILTER (bucket < 34500 AND v <= gd.open) f_le_5,
              min(bucket) FILTER (bucket < 35400 AND v < gd.open) f_lt_20, min(bucket) FILTER (bucket < 35400 AND v <= gd.open) f_le_20,
              arg_min(v, bucket) FILTER (bucket >= 36900) x_1015
            FROM {src} s JOIN gd ON gd.ticker = s.ticker GROUP BY s.ticker""").df()
        con.register("vf", V)
        for f in ("f_lt_5", "f_le_5", "f_lt_20", "f_le_20"):
            T = con.execute(f"""SELECT s.ticker, min(bucket) AS t_{f[2:]} FROM {src} s JOIN gd ON gd.ticker = s.ticker
                  JOIN vf ON vf.ticker = s.ticker WHERE vf.{f} IS NOT NULL AND s.bucket > vf.{f} AND s.bucket < 36900
                  AND s.v > gd.open + 0.5 * (gd.pc_total - gd.open) GROUP BY s.ticker""").df()
            V = V.merge(T, on="ticker", how="left")
        con.unregister("vf"); con.unregister("gd"); V["date"] = d; parts.append(V)
        if i % 400 == 0: log(f"{i+1} {d}")
    V = pd.concat(parts, ignore_index=True); V.to_parquet(OUT); log(f"wrote {OUT}: {len(V):,}")
C = C.merge(V, on=["ticker", "date"], how="inner")
C = C.merge(pd.read_parquet("data/equity/gapfader/pm_volume.parquet")[["ticker", "date", "dv_cross"]], on=["ticker", "date"], how="left")
C = C.merge(pd.read_parquet("data/equity/gapfader/targets.parquet")[["ticker", "date", "tf_50_36900"]], on=["ticker", "date"], how="left")
C = C[C.x_1015.notna()].copy()
for c in ("dv_le_1m", "dv_le_5m", "dv_le_20m", "dv_cross", "dv_5m_all"): C[c] = C[c].fillna(0)
C["yr"] = C.date.str[:4]; C["rel"] = C.spy_gap * np.sign(C.gap)
def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
print("# GF-17 — a buy LIMIT AT THE OPEN PRICE after the cross: the volume it can reach, and the fills' quality\n")
print(f"{len(C):,} weak-premarket gap-downs.\n")
print("## The $ that traded AT OR BELOW the open after the cross (what the limit competes for), median per gap\n")
print("| price | the cross | ≤ open, first 1 min | ≤ open, first 5 min | ≤ open, first 20 min | all $, first 5 min | gaps with $0 ≤ open in 5 min |")
print("|---|---|---|---|---|---|---|")
for lo, hi in ((1, 3), (3, 10), (10, 30), (30, 1e9), (1, 1e9)):
    P = C[(C.open >= lo) & (C.open < hi)]
    print(f"| ${lo}–{hi:g} | {P.dv_cross.median():,.0f} | {P.dv_le_1m.median():,.0f} | {P.dv_le_5m.median():,.0f} | {P.dv_le_20m.median():,.0f} | {P.dv_5m_all.median():,.0f} | {(P.dv_le_5m == 0).mean()*100:.0f} % |")
print("\n## The fills — exit 10:15, or a target at 50 % of the gap after the fill. Net: MOO 0.7c, the limit 0.55c / share\n")
for lab, P in (("all days", C), ("MARKET GAP-DOWN days (SPY down ≥ 1 %)", C[C.rel >= 0.01]), ("quiet / up days (SPY < 0.5 % down)", C[C.rel < 0.005])):
    print(f"### {lab} — {len(P):,}\n")
    print("| entry | filled | mean net % | median % | win % | **PF net** | 2016–20 / 2021–26 | years < 1 |"); print("|---|---|---|---|---|---|---|---|")
    x = np.where(P.tf_50_36900.notna(), P.open + 0.5 * (P.pc_total - P.open), P.x_1015)
    r = pd.Series(x / P.open - 1 - 0.007 / P.open, index=P.index)
    def line(lab2, r, yr, n):
        yb = sum(pf(r[yr == y].values) < 1 for y in yr.unique())
        return f"| {lab2} | {len(r):,} ({len(r)/n*100:.0f} %) | {r.mean()*100:+.2f} | {r.median()*100:+.2f} | {(r>0).mean()*100:.0f} | **{pf(r.values):.2f}** | {pf(r[yr<='2020'].values):.2f} / {pf(r[yr>='2021'].values):.2f} | {yb}/{yr.nunique()} |"
    print(line("MOO + target", r, P.yr, len(P)))
    for f, fl in (("f_lt_5", "limit at the open, to 09:35, trade-through"), ("f_le_5", "…, to 09:35, touch"), ("f_lt_20", "…, to 09:50, trade-through"), ("f_le_20", "…, to 09:50, touch")):
        F = P[P[f].notna()]
        x = np.where(F[f"t_{f[2:]}"].notna(), F.open + 0.5 * (F.pc_total - F.open), F.x_1015)
        r = pd.Series(x / F.open - 1 - 0.0055 / F.open, index=F.index)
        print(line(fl, r, F.yr, len(P)))
    # those the limit did NOT fill (to 09:50, touch): how did the MOO do on them? (what the limit misses)
    M = P[P.f_le_20.isna()]
    x = np.where(M.tf_50_36900.notna(), M.open + 0.5 * (M.pc_total - M.open), M.x_1015)
    r = pd.Series(x / M.open - 1 - 0.007 / M.open, index=M.index)
    print(line("(the MOO on the gaps the limit MISSED)", r, M.yr, len(P)))
    print()
