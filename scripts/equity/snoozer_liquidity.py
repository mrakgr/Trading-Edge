"""Snoozer LIQUIDITY: long vs short (2026-09-30, user: "the dollar volume differences in the last minute and the last hour
between the two systems").

The production book (snoozer_reference_book.parquet: signal at 15:59, a limit fill in the last minute, exit at the next
open) joined to the 1s slim bars of each ticker-day. Per trade:
  dv_lastmin    dollars in [15:59:00, 16:00:00)  — the continuous last minute, where the limit entry fills
  dv_1600       dollars in the 16:00:00 second — ⚠ NOT the closing auction, so kept in the parquet but out of the tables:
                the bars carry the cross (condition 8) AND its official-close reports (condition 15, the same shares again:
                SOAR 2026-08-26 = 3 x $16,071), and a cross stamped after 16:00:00 (DAR 2026-08-31 at 16:00:03, $50.8M) is
                not in it at all. The auction's size needs the raw tape (condition 8 prints).
  dv_lh         dollars in [15:00:00, 16:00:00)  — the last hour (continuous)
  nb_lastmin / nb_lh   present seconds in those windows (tape density)
Tables: quantiles per side (all years, and 2025-26), per cell, the position size at 5% / 10% of the last minute, and the
book's return by within-side last-minute tercile (does the short edge live in the illiquid names?).

Run from research/:  python -u scripts/equity/snoozer_liquidity.py > data/equity/flushfader/snoozer_liquidity.log 2>&1
"""
import argparse, os, time
import numpy as np, pandas as pd, duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--book", default="data/flushfader_gate_review/snoozer_reference_book.parquet")
ap.add_argument("--bars1s", default="data/intraday_1s_slim")
ap.add_argument("--out", default="data/equity/flushfader/snoozer_liquidity.parquet")
ap.add_argument("--batch", type=int, default=40)
args = ap.parse_args()

con = duckdb.connect(config={"memory_limit": "4GB", "threads": 6})
con.execute("SET enable_progress_bar=false")
B = con.execute(f"SELECT symbol AS ticker, trade_date::DATE AS date, side, cell, weight, ret, p1559 FROM read_parquet('{args.book}')").df()
B["d"] = B.date.astype(str)
dates = sorted(B.d.unique())
con.register("book", B[["ticker", "d"]])
rows, t0 = [], time.time()
for i in range(0, len(dates), args.batch):
    chunk = [d for d in dates[i:i + args.batch] if os.path.exists(f"{args.bars1s}/{d}.parquet")]
    if not chunk: continue
    lst = "[" + ",".join(f"'{args.bars1s}/{d}.parquet'" for d in chunk) + "]"
    rows.append(con.execute(f"""
WITH b AS (
  SELECT regexp_extract(filename, '(\\d{{4}}-\\d{{2}}-\\d{{2}})', 1) AS d, ticker, bucket, vwap::DOUBLE * volume::DOUBLE AS dv
  FROM read_parquet({lst}, filename = true) WHERE bucket >= 54000 AND bucket <= 57600)
SELECT b.d, b.ticker,
  sum(dv) FILTER (WHERE bucket >= 57540 AND bucket < 57600) AS dv_lastmin,
  count(*) FILTER (WHERE bucket >= 57540 AND bucket < 57600) AS nb_lastmin,
  coalesce(sum(dv) FILTER (WHERE bucket = 57600), 0) AS dv_1600,
  sum(dv) FILTER (WHERE bucket < 57600) AS dv_lh,
  count(*) FILTER (WHERE bucket < 57600) AS nb_lh
FROM b JOIN book k ON k.ticker = b.ticker AND k.d = b.d GROUP BY ALL""").df())
    print(f"  {min(i + args.batch, len(dates))}/{len(dates)} dates, {time.time() - t0:.0f} s", flush=True)
L = pd.concat(rows, ignore_index=True)
D = B.merge(L, on=["d", "ticker"], how="left")
for c in ["dv_lastmin", "nb_lastmin", "dv_lh", "nb_lh"]: D[c] = D[c].fillna(0)
D["year"] = D.date.astype(str).str[:4].astype(int)
D.drop(columns=["d"]).to_parquet(args.out)
print(f"{len(D):,} trades ({(D.dv_lh > 0).sum():,} with a last hour on the tape) -> {args.out}\n")

def q(x, p): return np.nanpercentile(x, p) if len(x) else np.nan
def k(x): return f"{x / 1e3:,.0f}k" if x < 1e6 else f"{x / 1e6:,.2f}M"
def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l

def quant_table(title, frame, groups):
    print(f"## {title}\n")
    print("| group | n | metric | p10 | p25 | p50 | p75 | p90 | mean |")
    print("|---|---|---|---|---|---|---|---|---|")
    for name, g in groups(frame):
        for m, lab in [("dv_lastmin", "$ last min (15:59)"), ("dv_lh", "$ last hour")]:
            x = g[m].values
            print(f"| {name} | {len(g)} | {lab} | {k(q(x,10))} | {k(q(x,25))} | {k(q(x,50))} | {k(q(x,75))} | {k(q(x,90))} | {k(x.mean())} |")
        for m, lab, den in [("nb_lastmin", "trading seconds, last min (of 60)", 60), ("nb_lh", "trading seconds, last hour (of 3600)", 3600)]:
            x = g[m].values
            print(f"| {name} | {len(g)} | {lab} | {q(x,10):.0f} | {q(x,25):.0f} | {q(x,50):.0f} | {q(x,75):.0f} | {q(x,90):.0f} | {x.mean():.0f} |")
    print()

by_side = lambda f: [(s, f[f.side == s]) for s in ["long", "short"]]
quant_table("Per side, all years (2016-08 .. 2026-08)", D, by_side)
quant_table("Per side, 2025-26 only", D[D.year >= 2025], by_side)

print("## Per side and year: median $ last minute / median $ last hour / median last-minute seconds\n")
print("| year | long n | long last min | long last hour | long secs | short n | short last min | short last hour | short secs | ratio L/S last min | ratio L/S last hour |")
print("|---|---|---|---|---|---|---|---|---|---|---|")
for y in sorted(D.year.unique()):
    a, b = D[(D.year == y) & (D.side == "long")], D[(D.year == y) & (D.side == "short")]
    ma, mb = a.dv_lastmin.median(), b.dv_lastmin.median(); ha, hb = a.dv_lh.median(), b.dv_lh.median()
    print(f"| {y} | {len(a)} | {k(ma)} | {k(ha)} | {a.nb_lastmin.median():.0f} | {len(b)} | {k(mb)} | {k(hb)} | {b.nb_lastmin.median():.0f} | {ma / mb:.2f} | {ha / hb:.2f} |")
print()

print("## Per cell: median $ last minute / $ last hour; share of trades whose last minute is under $100k / $250k\n")
print("| side | cell | w | n | last min p50 | last hour p50 | last min < $100k | < $250k |")
print("|---|---|---|---|---|---|---|---|")
for (s, c), g in D.groupby(["side", "cell"], sort=False):
    print(f"| {s} | {c} | {g.weight.iloc[0]} | {len(g)} | {k(g.dv_lastmin.median())} | {k(g.dv_lh.median())} | {(g.dv_lastmin < 1e5).mean():.0%} | {(g.dv_lastmin < 2.5e5).mean():.0%} |")
print()

print("## Capacity: the position at 5% / 10% of the last minute's dollars (median, p25) — what one entry can be without being the tape\n")
print("| side | 5% p25 | 5% p50 | 10% p25 | 10% p50 | 10% of last hour p50 |")
print("|---|---|---|---|---|---|")
for s, g in by_side(D):
    x = g.dv_lastmin.values
    print(f"| {s} | {k(0.05 * q(x,25))} | {k(0.05 * q(x,50))} | {k(0.1 * q(x,25))} | {k(0.1 * q(x,50))} | {k(0.1 * g.dv_lh.median())} |")
print()

print("## The book's return by WITHIN-SIDE tercile of the last minute's dollars (T1 = thinnest); weighted by the cell weight\n")
print("| side | tercile | n | last min range | mean ret % | PF (weighted) | win % |")
print("|---|---|---|---|---|---|---|")
for s, g in by_side(D):
    g = g.copy(); g["t"] = pd.qcut(g.dv_lastmin.rank(method="first"), 3, labels=["T1", "T2", "T3"])
    for t, h in g.groupby("t", observed=True):
        r = h.ret.values * 100; w = h.weight.values
        print(f"| {s} | {t} | {len(h)} | {k(h.dv_lastmin.min())} .. {k(h.dv_lastmin.max())} | {r.mean():+.2f} | {pf(r * w):.3f} | {(r > 0).mean():.0%} |")
print()
