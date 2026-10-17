"""GapFader GF-19 (user 2026-10-17): do the premarket QUOTES tell where a stock will OPEN, before the 09:25 cutoff? Reads
gapfader_preopen_quotes.py's days. Per ticker-day: the NBBO standing at each checkpoint (the last update before it), the
prev close in the open's terms (pc_total, as the census) and the official open (data v2 daily). Population: the census's
universe (the downloaded tickers) with prev close >= $1 and dv20_prior >= $1M (both known before the open; the open itself is never a filter).
  1. how often a two-sided quote exists, and the error of bid / mid / ask vs the open, by checkpoint and spread
  2. the gap screen: names whose 09:25 quote says <= -x % (a LOO each) vs the names that OPENED >= 4 % down
Run from research/:  python -u scripts/equity/gapfader_preopen_vs_open.py > data/equity/gapfader/preopen_vs_open.md 2> data/equity/gapfader/preopen_vs_open.log; echo exit $?
"""
import glob, os, sys, time, datetime as dt
from zoneinfo import ZoneInfo
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
ET = ZoneInfo("America/New_York")
QDIR = "data/equity/gapfader/preopen_quotes"
CHECKS = ["09:25:00", "09:28:00", "09:29:59"]
files = sorted(glob.glob(f"{QDIR}/*.parquet"))
dates = [os.path.basename(f)[:10] for f in files]
log(f"{len(dates)} days: {dates[0]} … {dates[-1]}")

con = duckdb.connect()
D = con.execute(f"""
    WITH d AS (SELECT ticker, date, open, close, n, cum_div,
                      lag(date) OVER w AS prev_date, lag(close) OVER w AS prev_close_raw, lag(n) OVER w AS n_prev, lag(cum_div) OVER w AS cd_prev,
                      avg(volume * close) OVER (w ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS dv20_prior
               FROM 'data/v2/daily_adjusted.parquet' WHERE date >= DATE '{dates[0]}' - INTERVAL 45 DAY
               WINDOW w AS (PARTITION BY ticker ORDER BY date))
    SELECT ticker, strftime(date, '%Y-%m-%d') AS date, open, prev_close_raw * n_prev / n - (cum_div - cd_prev) / n AS pc_total, dv20_prior
    FROM d WHERE date >= DATE '{dates[0]}' AND prev_date IS NOT NULL AND date - prev_date <= 4""").df()

def ns(d, hms):
    h, m, s = map(int, hms.split(":"))
    return int(dt.datetime(*map(int, d.split("-")), h, m, s, tzinfo=ET).timestamp()) * 1_000_000_000

parts = []
for f, d in zip(files, dates):
    Q = pd.read_parquet(f).sort_values(["ticker", "ts"])
    out = pd.DataFrame({"ticker": Q.ticker.unique()}); out["date"] = d
    for c in CHECKS:
        S = Q[Q.ts < ns(d, c)].groupby("ticker").tail(1).set_index("ticker")
        k = c[:5].replace(":", "")
        out[f"bid_{k}"] = out.ticker.map(S.bid); out[f"ask_{k}"] = out.ticker.map(S.ask)
    out["n_upd"] = out.ticker.map(Q[~Q.standing].groupby("ticker").size()).fillna(0)
    parts.append(out)
Q = pd.concat(parts, ignore_index=True)
X = D.merge(Q, on=["ticker", "date"], how="inner")      # the downloaded universe (every one of its tickers has a standing quote)
X = X[X.date.isin(dates) & (X.pc_total >= 1) & (X.dv20_prior >= 1e6) & X.open.notna() & (X.open > 0)].copy()
X["gap"] = X.open / X.pc_total - 1
log(f"population: {len(X):,} ticker-days ({len(X) / len(dates):,.0f} a day)")

def pct(v, q): return np.nanpercentile(v, q) if np.isfinite(v).any() else np.nan
print(f"# GF-19 — the premarket QUOTE vs the official OPEN ({dates[0]} … {dates[-1]}, {len(dates)} days)\n")
print(f"Population: prev close ≥ $1, dv20_prior ≥ $1M: {len(X):,} ticker-days, {len(X) / len(dates):,.0f} a day.\n")

print("## 1. the quote's error vs the open (log points, |quote / open − 1|)\n")
print("| checkpoint | two-sided quote | crossed / locked | median spread | mid: median abs err | mid: p90 | bid: median signed | ask: median signed |")
print("|---|---|---|---|---|---|---|---|")
for c in CHECKS:
    k = c[:5].replace(":", "")
    b, a = X[f"bid_{k}"], X[f"ask_{k}"]
    two = (b > 0) & (a > 0)
    cl = two & (a <= b)
    ok = two & (a > b)
    mid = (b + a) / 2
    e = np.log(mid[ok] / X.open[ok])
    sp = ((a - b) / mid)[ok]
    print(f"| {c} | {two.mean():.1%} | {cl.mean():.1%} | {sp.median():.2%} | {e.abs().median():.2%} | {pct(e.abs(), 90):.2%} | "
          f"{np.log(b[ok] / X.open[ok]).median():+.2%} | {np.log(a[ok] / X.open[ok]).median():+.2%} |")

k = "0925"
b, a = X[f"bid_{k}"], X[f"ask_{k}"]
ok = (b > 0) & (a > b)
X["mid"] = np.where(ok, (b + a) / 2, np.nan)
X["spread"] = np.where(ok, (a - b) / X.mid, np.nan)
X["err"] = np.log(X.mid / X.open)
print("\n### at 09:25 by the quoted spread\n")
print("| spread | n | share | median abs err | p90 abs err | open inside the 09:25 bid–ask |")
print("|---|---|---|---|---|---|")
for lo, hi in [(0, .005), (.005, .01), (.01, .02), (.02, .05), (.05, .10), (.10, 9)]:
    m = (X.spread >= lo) & (X.spread < hi)
    inside = ((X.open >= b) & (X.open <= a))[m]
    print(f"| {lo:.1%}–{hi:.1%} | {m.sum():,} | {m.mean():.1%} | {X.err[m].abs().median():.2%} | {pct(X.err[m].abs(), 90):.2%} | {inside.mean():.1%} |")
print(f"| no two-sided quote | {(~ok).sum():,} | {(~ok).mean():.1%} | | | |")

print("\n## 2. the gap screen at 09:25 vs the names that OPENED ≥ 4 % down\n")
T = X.gap <= -0.04
print(f"Opened ≥ 4 % down: {T.sum():,} ({T.sum() / len(dates):.1f} a day); of them with a two-sided 09:25 quote: {(T & ok).mean() / T.mean():.1%}.\n")
X["pq"] = X.mid / X.pc_total - 1                 # the quote's gap
X["pb"] = b / X.pc_total - 1                     # the bid's
X["pa"] = a / X.pc_total - 1                     # the ask's
print("| screen (09:25, two-sided) | caught | LOOs a day | that open ≥ 4 % down (fill) |")
print("|---|---|---|---|")
for col, name in [("pq", "mid"), ("pa", "ask"), ("pb", "bid")]:
    for x in [0.0, -0.01, -0.02, -0.03, -0.04]:
        s = X[col] <= x
        print(f"| {name} ≤ {x:+.0%} | {(s & T).sum() / T.sum():.1%} | {s.sum() / len(dates):.1f} | {(s & T).sum() / max(1, s.sum()):.1%} |")
print("\n### the ≥ 4 % openers the 09:25 mid screen (≤ −2 %) misses\n")
miss = T & ~(X.pq <= -0.02)
print(f"{miss.sum():,} missed: no two-sided quote {(miss & ~ok).sum():,}; quote above −2 % {(miss & ok).sum():,} "
      f"(their 09:25 mid gap: median {X.pq[miss & ok].median():+.1%}; open gap median {X.gap[miss & ok].median():+.1%})")
X.to_parquet("data/equity/gapfader/preopen_vs_open.parquet", index=False)
log("done")
