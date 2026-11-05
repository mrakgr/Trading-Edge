"""Run 33 — HighFlyerV2's REAL liquidity around the trade (user 2026-11-05: "The volume is usually significantly higher on
the breakout day so that plays in our favor ... these setups are accompanied by news which increases the liquidity around
the event ... compare the differences between 20d mean and 60d median estimates. We found the former to be better for
detecting high rvol breakouts, but the latter might be a better general estimate of liquidity").

The causal 10:00 book (Run 31), entries 2016-08+ (the 1s corpus). The dollar volume actually traded, from the 1s bars
(data/intraday_1s_slim: lit + TRF trades, markers dropped; vwap · volume per second):
  D 09:30–10:00     the first half hour of the entry day (the 10:00 candle's RTH part)
  D 10:00–10:15     the window a 10:00 entry is worked in
  D full day        the entry day's daily dollar volume (daily_adjusted close · volume)
  X 09:30–09:45     the exit morning (the 5-day hold ends at the open of bar i + 6)
against the two estimates known before the entry: dv60_med (median of the 60 prior sessions' close · volume) and adv20
(their 20-bar MEAN, the engine's). A better liquidity estimate = a tighter actual / estimate ratio (smaller sd of the log).
Cohorts: the whole book; the proxy book (dv60_med < $2M, CS); the proxy A+ (+ the red 10:00 dip −1..−6 % from the open).
    cd research && python3 -u scripts/equity/highflyer_v2_liquidity.py > data/equity/highflyer_repro/v2_liquidity.md 2> logs/highflyer_v2_liquidity.log
"""
import sys, time
import duckdb, numpy as np, pandas as pd
T0 = time.time()
def log(*x): print(f"[{time.time()-T0:7.1f}s]", *x, file=sys.stderr, flush=True)
con = duckdb.connect(); con.execute("SET memory_limit='2GB'; SET threads=4")
con.execute("ATTACH 'data/trading.db' AS db (READ_ONLY)")
T = pd.read_parquet("data/equity/highflyer_repro/v2port_partial_causal.parquet")
T["entry_date"] = pd.to_datetime(T.entry_date); T["exit_date"] = pd.to_datetime(T.exit_date)
T = T[(T.entry_date >= "2016-08-08") & (T.open == 0)].reset_index(drop=True)
con.register("t", T[["symbol", "entry_date", "exit_date"]])
con.execute("""CREATE TEMP TABLE s AS SELECT ticker, date, close * volume dv, row_number() OVER (PARTITION BY ticker ORDER BY date) i
               FROM db.daily_adjusted WHERE ticker IN (SELECT DISTINCT symbol FROM t)""")
M = con.execute("""SELECT t.symbol, t.entry_date, median(p.dv) dv60_med, any_value(x.dv) dv_day, any_value(z.dv) dv_exit_day
    FROM t JOIN s x ON x.ticker = t.symbol AND x.date = t.entry_date
    LEFT JOIN s z ON z.ticker = t.symbol AND z.date = t.exit_date
    JOIN s p ON p.ticker = t.symbol AND p.i BETWEEN x.i - 60 AND x.i - 1 GROUP BY 1, 2""").df()
T = T.merge(M, on=["symbol", "entry_date"], how="left")
ty = con.execute("SELECT ticker symbol, string_agg(DISTINCT type, '/') typ FROM db.ticker_reference WHERE type IN ('CS','ADRC') GROUP BY 1").df()
T = T.merge(ty, on="symbol", how="left")
log(f"{len(T):,} closed trips 2016-08+")

# the 1s windows: entry day [09:30, 10:00) and [10:00, 10:15); exit day [09:30, 09:45)
W = []
jobs = [(d, g.symbol.unique(), "entry") for d, g in T.groupby("entry_date")] + [(d, g.symbol.unique(), "exit") for d, g in T.groupby("exit_date")]
for k, (d, syms, kind) in enumerate(jobs):
    f = f"data/intraday_1s_slim/{d:%Y-%m-%d}.parquet"
    con.register("g", pd.DataFrame({"ticker": syms}))
    if kind == "entry":
        q = f"""SELECT ticker, sum(vwap * volume) FILTER (WHERE bucket >= 34200 AND bucket < 36000) a, sum(vwap * volume) FILTER (WHERE bucket >= 36000 AND bucket < 36900) b
                FROM read_parquet('{f}') WHERE ticker IN (SELECT ticker FROM g) AND bucket >= 34200 AND bucket < 36900 GROUP BY 1"""
    else:
        q = f"""SELECT ticker, sum(vwap * volume) a, NULL::DOUBLE b FROM read_parquet('{f}')
                WHERE ticker IN (SELECT ticker FROM g) AND bucket >= 34200 AND bucket < 35100 GROUP BY 1"""
    try: X = con.execute(q).df()
    except duckdb.IOException: X = pd.DataFrame(columns=["ticker", "a", "b"])
    con.unregister("g")
    X["date"] = d; X["kind"] = kind; W.append(X)
    if k % 500 == 0: log(f"  {k}/{len(jobs)} day-reads")
W = pd.concat(W, ignore_index=True)
E = W[W.kind == "entry"].rename(columns={"ticker": "symbol", "date": "entry_date", "a": "dv_0930_1000", "b": "dv_1000_1015"})
X = W[W.kind == "exit"].rename(columns={"ticker": "symbol", "date": "exit_date", "a": "dv_exit_0930_0945"})[["symbol", "exit_date", "dv_exit_0930_0945"]]
T = T.merge(E[["symbol", "entry_date", "dv_0930_1000", "dv_1000_1015"]], on=["symbol", "entry_date"], how="left")
T = T.merge(X, on=["symbol", "exit_date"], how="left")
T["dv_pm_to_1000"] = T.raw_vol_1000 * T.raw_close_1000                     # the 10:00 candle's volume (04:00–10:00) at its close
T.to_parquet("data/equity/highflyer_repro/v2_liquidity.parquet")

proxy = (T.dv60_med < 2e6) & (T.typ == "CS")
red = (T.raw_close_1000 / T.raw_open_1000 - 1).between(-0.06, -0.01)
COH = (("all", T.index == T.index), ("proxy book (dv60_med < \\$2M, CS)", proxy), ("proxy A+ (+ red dip)", proxy & red))
ACT = (("dv_pm_to_1000", "D 04:00–10:00 (the candle)"), ("dv_0930_1000", "D 09:30–10:00"), ("dv_1000_1015", "D 10:00–10:15"),
       ("dv_day", "D full day"), ("dv_exit_0930_0945", "exit day 09:30–09:45"), ("dv_exit_day", "exit day full"))
def m(x): return f"\\${np.nanmedian(x) / 1e6:.2f}M" if np.nanmedian(x) >= 1e5 else f"\\${np.nanmedian(x) / 1e3:.0f}k"

print("# Run 33 — HighFlyerV2's real liquidity around the trade (scripts/equity/highflyer_v2_liquidity.py)\n")
print(f"The causal 10:00 book, closed trips 2016-08 .. 2026-05: {len(T):,}; the proxy book {int(proxy.sum()):,}; proxy A+ {int((proxy & red).sum()):,}. "
      "Dollar volume actually traded (1s bars: lit + TRF, markers dropped) vs the estimates known before the entry.\n")
print("## 1. Medians\n")
print("| cohort | dv60_med | adv20 | " + " | ".join(l for _, l in ACT) + " |\n|---|---|---|" + "---|" * len(ACT))
for lab, sel in COH:
    S = T[sel]
    print(f"| {lab} | {m(S.dv60_med)} | {m(S.adv)} | " + " | ".join(m(S[c]) for c, _ in ACT) + " |")
print("\n## 2. Actual / estimate: median ratio [p10 – p90] · sd of ln(ratio) — the smaller sd, the better the estimate\n")
for lab, sel in COH:
    S = T[sel]
    print(f"### {lab} ({len(S):,})\n")
    print("| actual window | ÷ dv60_med | ÷ adv20 |\n|---|---|---|")
    for c, l in ACT:
        cells = []
        for est in ("dv60_med", "adv"):
            r = (S[c] / S[est]).replace([np.inf, -np.inf], np.nan).dropna(); r = r[r > 0]
            cells.append(f"{np.median(r):.2f}× [{np.quantile(r, .1):.2f} – {np.quantile(r, .9):.2f}] · sd {np.log(r).std():.2f}" if len(r) else "—")
        print(f"| {l} | " + " | ".join(cells) + " |")
    print()
print("## 3. A \\$10k position as a share of the window's dollar volume (median · p90 · share of trips over 5 %)\n")
print("| cohort | D 10:00–10:15 | exit day 09:30–09:45 | D full day |\n|---|---|---|---|")
for lab, sel in COH:
    S = T[sel]; cells = []
    for c in ("dv_1000_1015", "dv_exit_0930_0945", "dv_day"):
        p = (1e4 / S[c]).replace([np.inf, -np.inf], np.nan).dropna()
        cells.append(f"{100 * p.median():.1f} % · {100 * p.quantile(.9):.1f} % · {100 * (p > 0.05).mean():.0f} %")
    print(f"| {lab} | " + " | ".join(cells) + " |")
