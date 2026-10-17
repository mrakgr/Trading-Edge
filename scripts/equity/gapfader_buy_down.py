"""GapFader GF-16 (user 2026-10-16: "for big gap-down days in the MARKET, we'd have more time to buy and could even buy down").
On weak-premarket gap-DOWNs (rvol_pm < 0.02, gap ≤ −4 %): instead of the MOO, a buy LIMIT k % below the official open, working
from 09:30 to 09:50 (filled on a trade-through — a 1s vwap below it — at the limit; a maker, no fee but the commission);
unfilled = no trade. Exit: the 10:15 time stop, or a target at 50 % of the gap (from the FILL) before it. Versus the MOO on the
same days. By the market (SPY gap vs the stock). Net: MOO 0.7c / share round trip; the limit entry 0.55c (no auction fee).
Writes data/equity/gapfader/buy_down.parquet.
Run from research/:  python -u scripts/equity/gapfader_buy_down.py > data/equity/gapfader/buy_down.md 2> data/equity/gapfader/buy_down.log; echo exit $?
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
KS = (0.005, 0.01, 0.02, 0.03)
END = 35400                                     # 09:50
OUT = "data/equity/gapfader/buy_down.parquet"
if os.path.exists(OUT):
    V = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []
    for i, d in enumerate(sorted(C.date.unique())):
        gd = C[C.date == d][["ticker", "open", "pc_total"]]
        con.register("gd", gd)
        cols = []
        for k in KS:
            kk = int(k * 1000)
            lim = f"gd.open * {1 - k}"
            cols.append(f"min(bucket) FILTER (bucket < {END} AND v < {lim}) AS f_{kk}")
        V = con.execute(f"""SELECT s.ticker, {', '.join(cols)}, arg_min(v, bucket) FILTER (bucket >= 36900) AS x_1015
            FROM (SELECT ticker, bucket, vwap::DOUBLE v FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                  WHERE ticker IN (SELECT ticker FROM gd) AND bucket >= 34200 AND bucket < 57600 AND volume > 0) s
            JOIN gd ON gd.ticker = s.ticker GROUP BY s.ticker""").df()
        # the target after the fill: first vwap > fill + 50 % of (prev close − fill), after the fill and before 10:15
        for k in KS:
            kk = int(k * 1000)
            con.register("vf", V[["ticker", f"f_{kk}"]].rename(columns={f"f_{kk}": "fsec"}))
            T = con.execute(f"""SELECT s.ticker, min(bucket) AS t_{kk} FROM
                  (SELECT ticker, bucket, vwap::DOUBLE v FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                   WHERE ticker IN (SELECT ticker FROM gd) AND bucket >= 34200 AND bucket < 36900 AND volume > 0) s
                  JOIN gd ON gd.ticker = s.ticker JOIN vf ON vf.ticker = s.ticker
                  WHERE vf.fsec IS NOT NULL AND s.bucket > vf.fsec
                    AND s.v > gd.open * {1 - k} + 0.5 * (gd.pc_total - gd.open * {1 - k}) GROUP BY s.ticker""").df()
            con.unregister("vf"); V = V.merge(T, on="ticker", how="left")
        # the MOO's own target (50 % of the gap from the open) before 10:15
        T = con.execute(f"""SELECT s.ticker, min(bucket) AS t_moo FROM
              (SELECT ticker, bucket, vwap::DOUBLE v FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
               WHERE ticker IN (SELECT ticker FROM gd) AND bucket >= 34200 AND bucket < 36900 AND volume > 0) s
              JOIN gd ON gd.ticker = s.ticker WHERE s.v > gd.open + 0.5 * (gd.pc_total - gd.open) GROUP BY s.ticker""").df()
        V = V.merge(T, on="ticker", how="left")
        con.unregister("gd"); V["date"] = d; parts.append(V)
        if i % 400 == 0: log(f"{i+1} {d}")
    V = pd.concat(parts, ignore_index=True); V.to_parquet(OUT); log(f"wrote {OUT}: {len(V):,}")
C = C.merge(V, on=["ticker", "date"], how="inner")
C = C[C.x_1015.notna()].copy()
C["yr"] = C.date.str[:4]; C["rel"] = C.spy_gap * np.sign(C.gap)
def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
def row(lab, r, P, n_all):
    r = pd.Series(r).dropna(); yrs = P.loc[r.index, "yr"]
    yb = sum(pf(r[yrs == y].values) < 1 for y in yrs.unique())
    return (f"| {lab} | {len(r):,} ({len(r)/n_all*100:.0f} %) | {r.mean()*100:+.2f} | {r.median()*100:+.2f} | {(r>0).mean()*100:.0f} | **{pf(r.values):.2f}** | "
            f"{pf(r[yrs <= '2020'].values):.2f} / {pf(r[yrs >= '2021'].values):.2f} | {yb}/{yrs.nunique()} | {r.sum()*100/ max(1, P.date.nunique()):+.2f} |")
print("# GF-16 — BUY DOWN: a buy limit below the open (09:30–09:50) instead of the MOO, by the market\n")
print(f"{len(C):,} weak-premarket gap-downs. Exit: 10:15, or a target at 50 % of the gap from the entry. Net: MOO 0.7c / share, limit entry 0.55c.\n")
for lab, P in (("all days", C), ("MARKET GAP-DOWN days (SPY down ≥ 1 %)", C[C.rel >= 0.01]), ("SPY down 0.5–1 %", C[(C.rel >= 0.005) & (C.rel < 0.01)]),
               ("quiet / up days (SPY < 0.5 % down)", C[C.rel < 0.005])):
    n = len(P)
    print(f"## {lab} — {n:,} gaps on {P.date.nunique():,} days\n")
    print("| entry | filled | mean net % | median % | win % | **PF net** | 2016–20 / 2021–26 | years < 1 | sum % per day |")
    print("|---|---|---|---|---|---|---|---|---|")
    for tgt in (False, True):
        x = np.where(P.t_moo.notna(), P.open + 0.5 * (P.pc_total - P.open), P.x_1015) if tgt else P.x_1015
        r = pd.Series(np.asarray(x) / P.open - 1 - 0.007 / P.open, index=P.index)
        print(row(f"MOO{' + target' if tgt else ''}", r, P, n))
        for k in KS:
            kk = int(k * 1000); F = P[P[f"f_{kk}"].notna()]; L = F.open * (1 - k)
            x = np.where(F[f"t_{kk}"].notna(), L + 0.5 * (F.pc_total - L), F.x_1015) if tgt else F.x_1015
            r = pd.Series(np.asarray(x) / L - 1 - 0.0055 / L, index=F.index)
            print(row(f"limit −{k*100:g} %{' + target' if tgt else ''}", r, P, n))
    print()
