"""Run 34 — HighFlyerV2 in 2026 through October, and how much the MARKET moves it (user 2026-11-05: "we could backfill the
database and see whether it flipped since we last tested it ... start by checking whether this low profit factor in 2026 was
due to lack of trades"; then: "I thought that this method wasn't affected by the general market ... The period you picked
out is when the market was in a short-term bear phase").
Book: the causal 10:00 port through 2026-10-30 (the 10:00 candles after 06-25 from build_partial_candle_ext.py; minute aggs
backfilled 07-18 .. 10-30). Market over the SAME hold: SPY / IWM holder's return from the entry day's close to the exit
day's open (daily_adjusted) — descriptive, not a gate (it is the future). Breadth (causal, lag 1): the June parquet, ends
2026-06-18.
    cd research && python3 scripts/equity/highflyer_v2_2026.py > data/equity/highflyer_repro/v2_2026.md
"""
import duckdb, numpy as np, pandas as pd
con = duckdb.connect(); con.execute("SET memory_limit='2GB'"); con.execute("ATTACH 'data/trading.db' AS db (READ_ONLY)")
T = pd.read_parquet("data/equity/highflyer_repro/v2port_partial_causal_to1030.parquet")
T["entry_date"] = pd.to_datetime(T.entry_date); T["exit_date"] = pd.to_datetime(T.exit_date)
con.register("t", T[["symbol", "entry_date", "exit_date"]].drop_duplicates())
M = con.execute("""
  WITH s AS (SELECT ticker, date, close * volume dv, row_number() OVER (PARTITION BY ticker ORDER BY date) i FROM db.daily_adjusted
             WHERE ticker IN (SELECT DISTINCT symbol FROM t))
  SELECT t.symbol, t.entry_date, median(p.dv) dv60_med FROM t JOIN s x ON x.ticker = t.symbol AND x.date = t.entry_date
  JOIN s p ON p.ticker = t.symbol AND p.i BETWEEN x.i - 60 AND x.i - 1 GROUP BY 1, 2""").df()
T = T.merge(M, on=["symbol", "entry_date"], how="left")
ty = con.execute("SELECT ticker symbol, string_agg(DISTINCT type, '/') typ FROM db.ticker_reference WHERE type IN ('CS','ADRC') GROUP BY 1").df()
T = T.merge(ty, on="symbol", how="left")
for etf in ("SPY", "IWM"):
    E = con.execute(f"SELECT date, open o, close c, n, cum_div cd FROM db.daily_adjusted WHERE ticker = '{etf}' ORDER BY date").df()
    E["date"] = pd.to_datetime(E.date); E = E.set_index("date")
    c0 = E.reindex(T.entry_date); x1 = E.reindex(T.exit_date)
    T[etf.lower()] = (((x1.o.values * x1.n.values) - (c0.c.values * c0.n.values)) + (x1.cd.values - c0.cd.values)) / (c0.c.values * c0.n.values)
br = con.execute("SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b1 FROM 'data/equity/momentum_v0/breadth.parquet'").df()
br["date"] = pd.to_datetime(br.date); T = T.merge(br, left_on="entry_date", right_on="date", how="left")
T["proxy"] = (T.dv60_med < 2e6) & (T.typ == "CS")
T["yr"] = T.entry_date.dt.year
C = T[T.open == 0]
def pf(r):
    r = np.asarray(r); r = r[np.isfinite(r)]; l = -r[r < 0].sum()
    return np.inf if l == 0 else np.minimum(r, .5)[r > 0].sum() / l
def f2(x): return "inf" if np.isinf(x) else f"{x:.2f}"
def cell(S): return f"{len(S)} · {f2(pf(S.ret))} · {100 * S.ret.mean():+.1f} %" if len(S) else "—"

print("# Run 34 — HighFlyerV2 in 2026 through October, and the market (scripts/equity/highflyer_v2_2026.py)\n")
print(f"The causal 10:00 book through 2026-10-30: {len(T):,} trips ({int((T.open == 1).sum())} still open at the end). "
      "Cells: trips · PF clip · mean.\n")
print("## 1. 2026 by month\n")
print("| month | all | proxy book (dv60_med < \\$2M, CS) | SPY over the holds (mean) | IWM (mean) | breadth lag1 at entry (mean) |\n|---|---|---|---|---|---|")
for mth, S in C[C.yr == 2026].groupby(C.entry_date.dt.month):
    print(f"| 2026-{mth:02d} | {cell(S)} | {cell(S[S.proxy])} | {100 * S.spy.mean():+.2f} % | {100 * S.iwm.mean():+.2f} % | "
          f"{S.b1.mean():.2f} |" if S.b1.notna().any() else
          f"| 2026-{mth:02d} | {cell(S)} | {cell(S[S.proxy])} | {100 * S.spy.mean():+.2f} % | {100 * S.iwm.mean():+.2f} % | — |")
S = C[C.yr == 2026]
print(f"| **2026 Jan–Oct** | {cell(S)} | {cell(S[S.proxy])} | | | |")
for lab, sel in (("Jan–May 13", S.entry_date <= "2026-05-13"), ("May 14 – Oct", S.entry_date > "2026-05-13")):
    X = S[sel]; print(f"| {lab} | {cell(X)} | {cell(X[X.proxy])} | | | |")
print("\n## 2. The market over the same hold, 2005–2026 (closed trips)\n")
print("| over the 5-day hold | all | proxy book |\n|---|---|---|")
for etf in ("spy", "iwm"):
    for lab, lo, hi in (("≤ −3 %", -9, -0.03), ("−3..−1 %", -0.03, -0.01), ("−1..+1 %", -0.01, 0.01), ("+1..+3 %", 0.01, 0.03), ("≥ +3 %", 0.03, 9)):
        X = C[(C[etf] > lo) & (C[etf] <= hi)]
        print(f"| {etf.upper()} {lab} | {cell(X)} | {cell(X[X.proxy])} |")
for etf in ("spy", "iwm"):
    X = C[np.isfinite(C[etf])]
    b = np.polyfit(X[etf], np.clip(X.ret, -1, 0.5), 1)[0]
    print(f"\n{etf.upper()}: Spearman ρ(trip return, {etf.upper()} over the hold) = {X.ret.rank().corr(X[etf].rank()):.2f}; "
          f"slope of the clipped return on it = {b:.2f}; proxy book ρ = {X[X.proxy].ret.rank().corr(X[X.proxy][etf].rank()):.2f}")
print("\n## 3. Would the causal breadth gate (lag-1 > 0.5) have stepped aside? (to 2026-06-18)\n")
print("| period | breadth > 0.5 | breadth ≤ 0.5 |\n|---|---|---|")
for lab, sel in (("2005–2025, all", C.yr <= 2025), ("2005–2025, proxy", (C.yr <= 2025) & C.proxy),
                 ("2026 Jan–Jun 18, all", (C.yr == 2026) & C.b1.notna()), ("2026 Jan–Jun 18, proxy", (C.yr == 2026) & C.b1.notna() & C.proxy),
                 ("2026 Feb–Apr, all", (C.entry_date >= "2026-02-01") & (C.entry_date < "2026-05-01"))):
    X = C[sel]; print(f"| {lab} | {cell(X[X.b1 > 0.5])} | {cell(X[X.b1 <= 0.5])} |")
print("\n## 4. How unusual is 2026 Jan–Oct? Every rolling 10-month window, 2005-01 .. 2025-12 (by entry month)\n")
C = C.assign(ym=C.entry_date.dt.to_period("M"))
months = pd.period_range("2005-01", "2025-12", freq="M")
print("| book | 2026 Jan–Oct | windows | worst | p5 | p25 | median | windows ≤ 2026's PF |\n|---|---|---|---|---|---|---|---|")
for lab, X in (("all", C), ("proxy book", C[C.proxy])):
    w = [pf(X[(X.ym >= m0) & (X.ym <= m0 + 9)].ret) for m0 in months[:-9]]
    w = np.array(w); now = pf(X[(X.yr == 2026)].ret)
    print(f"| {lab} | {f2(now)} | {len(w)} | {w.min():.2f} | {np.quantile(w, .05):.2f} | {np.quantile(w, .25):.2f} | {np.median(w):.2f} | "
          f"{int((w <= now).sum())} ({100 * (w <= now).mean():.1f} %) |")
print("\n## 5. The proxy book by year (closed trips: trips · PF clip · mean)\n")
P = C[C.proxy]
print("| " + " | ".join(str(y) for y in sorted(P.yr.unique())) + " |\n|" + "---|" * P.yr.nunique())
print("| " + " | ".join(cell(P[P.yr == y]) for y in sorted(P.yr.unique())) + " |")
