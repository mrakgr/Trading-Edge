"""Run 35 — HighFlyerV2: exit at an EARLIER open (user 2026-11-05: "maybe we could consider getting out on day 3 or 4's open.
Most of the edge is there during the first 3 days anyway"), and which past 10-month windows were as bad as 2026.
Book: the causal 10:00 port through 2026-10-30 (Run 34). Exit k = the open of the k-th session after the entry day (k = 6 is
production's 5-day time stop), the holder's return from the 10:00 fill (daily_adjusted; a session missing = the trip is
dropped for that k). The exit morning's liquidity: the 1s bars' dollar volume 09:30–09:45 on that day (2016-08+).
    cd research && python3 -u scripts/equity/highflyer_v2_exit_day.py > data/equity/highflyer_repro/v2_exit_day.md 2> logs/highflyer_v2_exit_day.log
"""
import sys, duckdb, numpy as np, pandas as pd
con = duckdb.connect(); con.execute("SET memory_limit='2GB'; SET threads=4"); con.execute("ATTACH 'data/trading.db' AS db (READ_ONLY)")
T = pd.read_parquet("data/equity/highflyer_repro/v2port_partial_causal_to1030.parquet")
T["entry_date"] = pd.to_datetime(T.entry_date); T = T.reset_index(drop=True); T["tid"] = T.index
con.register("t", T[["tid", "symbol", "entry_date", "raw_close_1000", "n"]])
X = con.execute("""
  WITH s AS (SELECT ticker, date, open, close, volume, n, cum_div cd, row_number() OVER (PARTITION BY ticker ORDER BY date) i
             FROM db.daily_adjusted WHERE ticker IN (SELECT DISTINCT symbol FROM t))
  SELECT t.tid, y.i - x.i k, y.date xdate, ((y.open * y.n - t.raw_close_1000 * x.n) + (y.cd - x.cd)) / (t.raw_close_1000 * x.n) ret
  FROM t JOIN s x ON x.ticker = t.symbol AND x.date = t.entry_date
  JOIN s y ON y.ticker = t.symbol AND y.i BETWEEN x.i + 1 AND x.i + 6""").df()
M = con.execute("""
  WITH s AS (SELECT ticker, date, close * volume dv, row_number() OVER (PARTITION BY ticker ORDER BY date) i FROM db.daily_adjusted
             WHERE ticker IN (SELECT DISTINCT symbol FROM t))
  SELECT t.tid, median(p.dv) dv60_med FROM t JOIN s x ON x.ticker = t.symbol AND x.date = t.entry_date
  JOIN s p ON p.ticker = t.symbol AND p.i BETWEEN x.i - 60 AND x.i - 1 GROUP BY 1""").df()
ty = con.execute("SELECT ticker symbol, string_agg(DISTINCT type, '/') typ FROM db.ticker_reference WHERE type IN ('CS','ADRC') GROUP BY 1").df()
T = T.merge(M, on="tid", how="left").merge(ty, on="symbol", how="left")
T["proxy"] = (T.dv60_med < 2e6) & (T.typ == "CS")
T["aplus"] = T.proxy & (T.raw_close_1000 / T.raw_open_1000 - 1).between(-0.06, -0.01)
X = X.merge(T[["tid", "symbol", "entry_date", "proxy", "aplus"]], on="tid")
X["xdate"] = pd.to_datetime(X.xdate)
# exit-morning liquidity, 2016-08+
L = []
for d, g in X[X.entry_date >= "2016-08-08"].groupby("xdate"):
    con.register("g", pd.DataFrame({"ticker": g.symbol.unique()}))
    try:
        Y = con.execute(f"""SELECT ticker symbol, sum(vwap * volume) dv15 FROM read_parquet('data/intraday_1s_slim/{d:%Y-%m-%d}.parquet')
                            WHERE ticker IN (SELECT ticker FROM g) AND bucket >= 34200 AND bucket < 35100 GROUP BY 1""").df()
        Y["xdate"] = d; L.append(Y)
    except duckdb.IOException: pass
    con.unregister("g")
L = pd.concat(L, ignore_index=True); X = X.merge(L, on=["symbol", "xdate"], how="left")
def pf(r):
    r = np.asarray(r); r = r[np.isfinite(r)]; l = -r[r < 0].sum()
    return np.inf if l == 0 else np.minimum(r, .5)[r > 0].sum() / l
def f2(x): return "inf" if np.isinf(x) else f"{x:.2f}"
yr = X.entry_date.dt.year
print("# Run 35 — the exit day, and the past windows as bad as 2026 (scripts/equity/highflyer_v2_exit_day.py)\n")
print("## 1. Exit at the open of session k after the entry (k = 6 is production's)\n")
print("Cells: PF clip · mean. Trips 2005-01 .. 2026-10; the same trips in every column (those with all six sessions).\n")
full = X.groupby("tid").k.nunique(); keep = full[full == 6].index; Z = X[X.tid.isin(keep)]
print("| book | trips | " + " | ".join(f"k = {k}" for k in range(1, 7)) + " |\n|---|---|" + "---|" * 6)
for lab, sel in (("all", Z.k == Z.k), ("proxy book", Z.proxy), ("proxy A+", Z.aplus)):
    S = Z[sel]
    print(f"| {lab} | {S.tid.nunique():,} | " + " | ".join(f"{f2(pf(S[S.k == k].ret))} · {100 * S[S.k == k].ret.mean():+.2f} %" for k in range(1, 7)) + " |")
for lab, sel in (("proxy book, 2005–14", Z.proxy & (Z.entry_date.dt.year <= 2014)), ("proxy book, 2015–20", Z.proxy & Z.entry_date.dt.year.between(2015, 2020)),
                 ("proxy book, 2021–26", Z.proxy & (Z.entry_date.dt.year >= 2021)), ("proxy book, 2026", Z.proxy & (Z.entry_date.dt.year == 2026))):
    S = Z[sel]
    print(f"| {lab} | {S.tid.nunique():,} | " + " | ".join(f"{f2(pf(S[S.k == k].ret))} · {100 * S[S.k == k].ret.mean():+.2f} %" for k in range(1, 7)) + " |")
print("\n## 2. The exit morning's liquidity (09:30–09:45 dollar volume, 1s bars, 2016-08+): median · a \\$10k sell's median share\n")
print("| book | " + " | ".join(f"k = {k}" for k in range(1, 7)) + " |\n|---|" + "---|" * 6)
for lab, sel in (("all", Z.k == Z.k), ("proxy book", Z.proxy), ("proxy A+", Z.aplus)):
    S = Z[sel & Z.dv15.notna()]
    print(f"| {lab} | " + " | ".join(f"\\${S[S.k == k].dv15.median() / 1e3:,.0f}k · {100 * (1e4 / S[S.k == k].dv15).median():.1f} %" for k in range(1, 7)) + " |")

print("\n## 3. The rolling 10-month windows (by entry month, 2005-01 .. 2025-12) as bad as 2026 Jan–Oct, production exit (k = 6)\n")
C = T[T.open == 0].copy(); C["ym"] = C.entry_date.dt.to_period("M")
for lab, B in (("all", C), ("proxy book", C[C.proxy])):
    now = pf(B[B.entry_date.dt.year == 2026].ret)
    rows = []
    for m0 in pd.period_range("2005-01", "2025-03", freq="M"):
        W = B[(B.ym >= m0) & (B.ym <= m0 + 9)]
        p = pf(W.ret)
        if p <= now: rows.append((m0, m0 + 9, len(W), p, W.ret.mean()))
    print(f"### {lab}: 2026 Jan–Oct = {f2(now)}\n")
    print("| window | trips | PF clip | mean |\n|---|---|---|---|")
    for a, b, n, p, mu in rows: print(f"| {a} .. {b} | {n} | {f2(p)} | {100 * mu:+.2f} % |")
    print()
