"""GapFader GF-25 (user 2026-10-17): the REAL screen — the 09:25 best ASK on the weak-premarket names only — on GF-19's 10 quote
days (2026-10-02 … 10-15). Premarket volume from the 1s bars: to 09:25 (bucket < 33900: what the screen can know) and to
09:30 (the census's rvol_pm, which defines the trade). The trade = opened ≥ 4 % down with rvol_pm < 0.02 (open ≥ $1,
dv20_prior ≥ $1M, prev close ≥ $1). A screened name gets a LOO at prev close × 0.96: it fills iff the stock opens ≥ 4 % down.
Compared on the same days with GF-18's print screen (the last print before 09:25), and for GF-18's SURPRISES (no print
before 09:25). Writes data/equity/gapfader/ask_screen.parquet.
Run from research/:  python -u scripts/equity/gapfader_ask_screen.py > data/equity/gapfader/ask_screen.md 2> data/equity/gapfader/ask_screen.log; echo exit $?
"""
import sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
X = pd.read_parquet("data/equity/gapfader/preopen_vs_open.parquet")
dates = sorted(X.date.unique())
con = duckdb.connect()
A = con.execute(f"""
    WITH d AS (SELECT ticker, date, n, avg(volume / n) OVER (PARTITION BY ticker ORDER BY date ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) * n AS avgvol20_prior
               FROM 'data/v2/daily_adjusted.parquet' WHERE date >= DATE '{dates[0]}' - INTERVAL 45 DAY)
    SELECT ticker, strftime(date, '%Y-%m-%d') AS date, avgvol20_prior FROM d WHERE date >= DATE '{dates[0]}'""").df()
X = X.merge(A, on=["ticker", "date"], how="left")
parts = []
for d in dates:
    con.register("tk", X[X.date == d][["ticker"]])
    P = con.execute(f"""SELECT ticker, sum(volume::DOUBLE) FILTER (bucket < 34200) AS pm_vol, sum(volume::DOUBLE) FILTER (bucket < 33900) AS pm_vol_0925,
               arg_max(vwap::DOUBLE, bucket) FILTER (bucket < 33900) AS last_0925, count(*) FILTER (bucket < 33900) AS bars_0925
        FROM read_parquet('data/intraday_1s_slim/{d}.parquet') WHERE ticker IN (SELECT ticker FROM tk) AND volume > 0 GROUP BY ticker""").df()
    con.unregister("tk"); P["date"] = d; parts.append(P)
X = X.merge(pd.concat(parts), on=["ticker", "date"], how="left")
for c in ("pm_vol", "pm_vol_0925", "bars_0925"): X[c] = X[c].fillna(0)
X["rvol_pm"] = X.pm_vol / X.avgvol20_prior
X["rvol_0925"] = X.pm_vol_0925 / X.avgvol20_prior
X = X[(X.open >= 1) & X.avgvol20_prior.notna()].copy()
days = len(dates)
OPEN4 = X.gap <= -0.04
TRADE = OPEN4 & (X.rvol_pm < 0.02)
WEAK = X.rvol_0925 < 0.02                                  # knowable at 09:25
SURP = TRADE & (X.bars_0925 == 0)
X["pl"] = X.last_0925 / X.pc_total - 1
print(f"# GF-25 — the 09:25 ASK screen on the WEAK-PREMARKET names ({dates[0]} … {dates[-1]}, {days} days)\n")
print(f"Names a day: {len(X) / days:,.0f}; opened ≥ 4 % down {OPEN4.sum() / days:.1f}; of them THE TRADE (rvol_pm < 0.02) "
      f"{TRADE.sum() / days:.1f} ({TRADE.sum()} in all); surprises (no print before 09:25) {SURP.sum()} ({SURP.sum() / TRADE.sum():.0%} of the trades). "
      f"rvol to 09:25 vs to 09:30 agree on the 0.02 line for {((X.rvol_0925 < 0.02) == (X.rvol_pm < 0.02)).mean():.1%} of names.\n")
print("| screen at 09:25 (and rvol to 09:25 < 0.02) | trades caught | surprises caught | LOOs a day | fill (opens ≥ 4 % down) | fills that are trades |")
print("|---|---|---|---|---|---|")
rows = [(f"ask ≤ {x:+.0%}", WEAK & (X.pa <= x)) for x in (0.0, -0.01, -0.02, -0.03, -0.04)]
rows += [(f"last print ≤ {x:+.0%} (GF-18)", WEAK & (X.pl <= x)) for x in (0.0, -0.02, -0.04)]
rows += [("ask ≤ −3 % OR no print", WEAK & ((X.pa <= -0.03) | (X.bars_0925 == 0)))]
for name, s in rows:
    fills = s & OPEN4
    print(f"| {name} | {(s & TRADE).sum() / TRADE.sum():.0%} | {(s & SURP).sum() / max(1, SURP.sum()):.0%} | {s.sum() / days:.1f} | "
          f"{fills.sum() / max(1, s.sum()):.0%} | {(fills & TRADE).sum() / max(1, fills.sum()):.0%} |")
print("\n### the trades the ask ≤ −3 % screen misses\n")
miss = TRADE & ~(WEAK & (X.pa <= -0.03))
print(f"{miss.sum()} missed of {TRADE.sum()}: rvol to 09:25 ≥ 0.02 {(miss & ~WEAK).sum()}; ask above −3 % {(miss & WEAK).sum()} "
      f"(their 09:25 ask gap: median {X.pa[miss & WEAK].median():+.1%}, spread median {X.spread[miss & WEAK].median():.1%}; open gap median {X.gap[miss & WEAK].median():+.1%})")
print("\n### by liquidity (ask ≤ −3 %)\n")
print("| dv20_prior | trades | caught | LOOs a day | fill |\n|---|---|---|---|---|")
s = WEAK & (X.pa <= -0.03)
for lo, hi in [(1e6, 5e6), (5e6, 2.5e7), (2.5e7, 1e13)]:
    m = (X.dv20_prior >= lo) & (X.dv20_prior < hi)
    print(f"| ${lo / 1e6:g}M–{hi / 1e6:g}M | {(TRADE & m).sum()} | {(s & m & TRADE).sum() / max(1, (TRADE & m).sum()):.0%} | {(s & m).sum() / days:.1f} | {(s & m & OPEN4).sum() / max(1, (s & m).sum()):.0%} |" if hi < 1e13 else
          f"| ≥ ${lo / 1e6:g}M | {(TRADE & m).sum()} | {(s & m & TRADE).sum() / max(1, (TRADE & m).sum()):.0%} | {(s & m).sum() / days:.1f} | {(s & m & OPEN4).sum() / max(1, (s & m).sum()):.0%} |")
X.to_parquet("data/equity/gapfader/ask_screen.parquet", index=False)
log("done")
