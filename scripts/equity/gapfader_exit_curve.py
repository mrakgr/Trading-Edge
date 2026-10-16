"""GapFader GF-13 (user 2026-10-16): the EXIT-TIME CURVE of the weak-premarket gap-DOWN long (rvol_pm < 0.02, gap ≤ −4 %,
bought at the official open — a MOO). Sold at the FIRST 1s vwap at/after 09:30 + T (T = 1 … 120 min), or at the official
close. Gross and net (0.7c / share round trip; 0.85c + 0.05 % — a taker sale and a half-spread guess). By the market (SPY
gap vs the stock, GF-3), price and gap size. Headline universe. Writes data/equity/gapfader/exit_curve.parquet.
Run from research/:  python -u scripts/equity/gapfader_exit_curve.py > data/equity/gapfader/exit_curve.md 2> data/equity/gapfader/exit_curve.log; echo exit $?
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
TS = (1, 2, 3, 5, 10, 15, 20, 30, 45, 60, 90, 120)
OUT = "data/equity/gapfader/exit_curve.parquet"
if os.path.exists(OUT):
    V = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []
    days = sorted(C.date.unique())
    cols = ", ".join(f"arg_min(vwap::DOUBLE, bucket) FILTER (bucket >= {34200 + T * 60}) AS x_{T}" for T in TS)
    for i, d in enumerate(days):
        tl = ",".join("'" + t.replace("'", "''") + "'" for t in C[C.date == d].ticker)
        V = con.execute(f"""SELECT ticker, {cols} FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
            WHERE ticker IN ({tl}) AND bucket >= 34200 AND bucket < 57600 AND volume > 0 GROUP BY ticker""").df()
        V["date"] = d; parts.append(V)
        if i % 400 == 0: log(f"{i+1}/{len(days)} {d}")
    V = pd.concat(parts, ignore_index=True); V.to_parquet(OUT); log(f"wrote {OUT}: {len(V):,}")
C = C.merge(V, on=["ticker", "date"], how="inner")
C["yr"] = C.date.str[:4]; C["rel"] = C.spy_gap * np.sign(C.gap)
def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
EXITS = [(f"x_{T}", f"{T} min") for T in TS] + [("close", "close")]
def curve(P, lab):
    print(f"### {lab} — {len(P):,} trades\n")
    print("| exit | gross mean % | median % | win % | PF gross | **PF net 0.7c** | PF net 0.85c + 0.05 % | net 0.7c 2016–20 / 2021–26 | years < 1 (net 0.7c) |")
    print("|---|---|---|---|---|---|---|---|---|")
    for col, el in EXITS:
        F = P[P[col].notna()]; r = F[col] / F.open - 1
        n1 = r - 0.007 / F.open; n2 = r - 0.0085 / F.open - 0.0005
        yb = sum(pf(n1[F.yr == y].values) < 1 for y in F.yr.unique())
        print(f"| {el} | {r.mean()*100:+.2f} | {r.median()*100:+.2f} | {(r>0).mean()*100:.0f} | {pf(r.values):.2f} | **{pf(n1.values):.2f}** | {pf(n2.values):.2f} | "
              f"{pf(n1[F.yr <= '2020'].values):.2f} / {pf(n1[F.yr >= '2021'].values):.2f} | {yb}/{F.yr.nunique()} |")
    print()
print("# GF-13 — the EXIT-TIME CURVE: weak-premarket gap-DOWN longs, bought at the official open\n")
print(f"{len(C):,} trades, {C.date.min()} … {C.date.max()}. Sold at the first 1s vwap at/after 09:30 + T.\n")
curve(C, "all days")
for lab, m in (("ex with-market", C.rel < 0.01), ("with-market (SPY down ≥ 1 %)", C.rel >= 0.01)):
    curve(C[m], lab)
# net 0.7c PF by price and gap size, a few exits
print("### net PF (0.7c) by price — all days\n")
sel = [("x_5", "5"), ("x_10", "10"), ("x_15", "15"), ("x_20", "20"), ("x_30", "30"), ("x_45", "45"), ("x_60", "60"), ("close", "close")]
print("| price | n | " + " | ".join(f"{e} min" if e != "close" else "close" for _, e in sel) + " |"); print("|---" * (len(sel) + 2) + "|")
for lo, hi in ((1, 2), (2, 3), (3, 5), (5, 10), (10, 20), (20, 50), (50, 1e9)):
    P = C[(C.open >= lo) & (C.open < hi)]
    print(f"| ${lo}–{hi:g} | {len(P):,} | " + " | ".join(f"{pf((P[c] / P.open - 1 - 0.007 / P.open).dropna().values):.2f}" for c, _ in sel) + " |")
print("\n### net PF (0.7c) by gap size — all days\n")
print("| gap | n | " + " | ".join(f"{e} min" if e != "close" else "close" for _, e in sel) + " |"); print("|---" * (len(sel) + 2) + "|")
for lo, hi in ((0.04, 0.06), (0.06, 0.10), (0.10, 0.15), (0.15, 0.30), (0.30, 9)):
    P = C[(-C.gap >= lo) & (-C.gap < hi)]
    print(f"| {lo*100:.0f}–{hi*100:.0f} % | {len(P):,} | " + " | ".join(f"{pf((P[c] / P.open - 1 - 0.007 / P.open).dropna().values):.2f}" for c, _ in sel) + " |")
