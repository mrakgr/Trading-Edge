#!/usr/bin/env python3
"""SpringFlyer SPEC v2 — the signal list, for the options study (user 2026-11-06: "gather a list of SpringFlyer signals
since 2020, and then we'll look into selling calls on them").

SPEC v2 (docs/springflyer_results.md S9, ratified 2026-09-07), every value one of day D's close:
  er10_signed > 0.9 AND er20_signed > 0.6 AND close >= the max close of the PRIOR 20 sessions AND rng20c_d > 2.0
  frame: dv20_prior >= $5M, raw $2 floor on D-1's close, the WB bad-print stub (close > 0.1 * prev_close), atr20_prior > 0
Short at D's close; the user's exit = the 10-day timestop (S9 decisions).

S9's log was produced inline and never committed; this script is its reproduction, and the CONTROL is S9's own numbers:
827 signals through 2026-09-04 and the 10-day mc=1 book of 480 trades at PF 3.17 (S9 drops the repeats with an approximate
hold of k * 1.45 calendar days). The list carries the EXACT-session book as well (S9 open item 4): a repeat is admitted
once the previous position's 10th session has closed (same episode, barnum > the last entry's + 10).

The forward columns (r_open1 .. r10, mae5, mfe5: short-side NOT applied, bp of D's close, dividends as cash) are OUTCOMES
ONLY. close = D's RAW close — the price an option strike is set against on D. exit_date_5 / exit_date_10 = the dates of the
5th / 10th session after D in the same episode (the options study picks expiries around them).

    cd research && python3 scripts/equity/springflyer_signals.py [--feat data/springflyer_daily_2026-10-30.parquet]
writes data/equity/springflyer/signals_spec_v2.parquet + .csv (every signal, 2005 ->; the 2020+ view is a filter); in_book10 =
the exact-session 10-day book WITHOUT the phantom suspects (see below); in_book10_s9 = S9's approximation on every signal
"""
import argparse, os
import numpy as np, pandas as pd, duckdb
import data_v2

ap = argparse.ArgumentParser()
ap.add_argument('--feat', default='data/springflyer_daily_2026-10-30.parquet')
ap.add_argument('--out', default='data/equity/springflyer/signals_spec_v2')
ap.add_argument('--since', default='2020-01-01', help='the summary tables (the file keeps every year)')
a = ap.parse_args()

con = data_v2.attach(duckdb.connect(), 'main')
con.execute("SET memory_limit='6GB'; SET threads=8")
FRAME = "f.dv20_prior >= 5e6 AND f.prev_close_raw >= 2 AND f.close > 0.1 * f.prev_close AND f.atr20_prior > 0"
SPEC = "f.er10_signed > 0.9 AND f.er20_signed > 0.6 AND f.rng20c_d > 2.0 AND p.cn >= p.hi20c"
d = con.execute(f"""
WITH p AS (
  SELECT ticker, date, close * n AS cn,
    MAX(close * n) OVER (w ROWS BETWEEN 19 PRECEDING AND 1 PRECEDING) AS hi20c,
    LEAD(date, 5) OVER w AS exit_date_5, LEAD(date, 10) OVER w AS exit_date_10
  FROM daily_episodes_causal WHERE date >= '2004-06-01'
  WINDOW w AS (PARTITION BY ticker, episode ORDER BY date))
SELECT f.ticker, f.date, f.yr, f.barnum, f.close, f.prev_close_raw, f.open, f.high, f.low, f.volume, f.dv20_prior, f.rvol_d,
  f.chg20, f.rng20c_d, f.er10_signed, f.er20_signed, f.r_open1, f.r1, f.r3, f.r5, f.r10, f.mae5, f.mfe5,
  p.exit_date_5, p.exit_date_10
FROM read_parquet('{a.feat}') f JOIN p ON p.ticker = f.ticker AND p.date = f.date
WHERE {FRAME} AND {SPEC}
ORDER BY f.ticker, f.date""").df()
d['date'] = pd.to_datetime(d.date)

# ⚠ PHANTOM SUSPECTS (2026-11-06): an UNRECORDED split / ticker reuse is a one-day close jump >= 3x (or <= 1/3) on ORDINARY
# volume — UBXG 2026-05-22 0.129 -> 3.12 on 0.01x its median volume (a ~1:25 reverse split missing from `splits`); WLL
# 2020-09-02 34x (the post-bankruptcy shares). The real climaxes jump on 20-10,000x volume. A jump inside the 20-session
# window contaminates rng20c / er20 / chg20, so the signal is flagged (kept in the file, excluded from the tables).
con.register('sig', d[['ticker', 'date']])
ph = con.execute("""
WITH t AS (SELECT DISTINCT ticker FROM sig),
p AS (SELECT x.ticker, x.date, x.close * x.n / LAG(x.close * x.n) OVER w AS jump,
        x.volume / MEDIAN(x.volume) OVER (w ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS vol_rel,
        ROW_NUMBER() OVER w AS rn, x.episode
      FROM daily_episodes_causal x JOIN t USING (ticker) WHERE x.date >= '2004-06-01'
      WINDOW w AS (PARTITION BY x.ticker, x.episode ORDER BY x.date)),
s AS (SELECT sig.ticker, sig.date, p.rn, p.episode FROM sig JOIN p ON p.ticker = sig.ticker AND p.date = sig.date)
SELECT s.ticker, s.date, MAX(CASE WHEN (p.jump >= 3 OR p.jump <= 1.0 / 3) AND p.vol_rel < 3 THEN 1 ELSE 0 END) AS phantom_suspect
FROM s JOIN p ON p.ticker = s.ticker AND p.episode = s.episode AND p.rn BETWEEN s.rn - 19 AND s.rn
GROUP BY 1, 2""").df()
ph['date'] = pd.to_datetime(ph.date)
d = d.merge(ph, on=['ticker', 'date'], how='left')
d['phantom_suspect'] = d.phantom_suspect.fillna(0).astype(bool)


def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum()
    return g / l if l > 0 else float('inf')


def book_approx(s, k, col):          # S9's: repeats dropped for k * 1.45 calendar days
    keep = []
    for _, g in s.groupby('ticker'):
        last = pd.Timestamp('1900-01-01')
        for i, r in g.iterrows():
            if r.date > last and not np.isnan(r[col]):
                keep.append(i); last = r.date + pd.Timedelta(days=int(k * 1.45) + 1)
    return s.loc[keep]


def book_exact(s, k):                # a repeat admitted once the last entry's k-th session has closed
    keep = []
    for _, g in s.groupby('ticker'):
        lb, ld = None, None
        for i, r in g.iterrows():
            same_ep = lb is not None and r.barnum > lb and (r.date - ld).days < 120
            if not same_ep or r.barnum >= lb + k:
                keep.append(i); lb, ld = r.barnum, r.date
    return s.loc[keep]


# ---- the control: S9 through 2026-09-04 (S9's frame also required r1 IS NOT NULL)
c = d[(d.date <= '2026-09-04') & d.r1.notna()]
b10 = book_approx(c, 10, 'r10'); r = -b10.r10.values
print(f"CONTROL vs S9 (through 2026-09-04): signals {len(c)} (S9 827), names {c.ticker.nunique()} (S9 439); "
      f"10d mc=1 book {len(b10)} (S9 480), PF {pf(r):.2f} (S9 3.17), mean {r.mean():+.0f} (S9 +1,731), median {np.median(r):+.0f} (S9 +2,107)")

print(f"phantom suspects: {d.phantom_suspect.sum()} of {len(d)} signals ({d[d.date >= a.since].phantom_suspect.sum()} since {a.since}): "
      + ", ".join(f"{t} {x.date()}" for t, x in d[d.phantom_suspect & (d.date >= a.since)][['ticker', 'date']].drop_duplicates('ticker').values))
d['in_book10'] = d.index.isin(book_exact(d[~d.phantom_suspect], 10).index)        # the book is built WITHOUT the suspects
d['in_book10_s9'] = d.index.isin(book_approx(d[d.r10.notna()], 10, 'r10').index)
os.makedirs(os.path.dirname(a.out), exist_ok=True)
out = d.copy(); out['date'] = out.date.dt.date
out.to_parquet(a.out + '.parquet', index=False); out.to_csv(a.out + '.csv', index=False)
print(f"wrote {a.out}.parquet / .csv: {len(d)} signals {d.date.min().date()} -> {d.date.max().date()}, "
      f"{d.in_book10.sum()} in the exact-session 10d book ({d.in_book10_s9.sum()} by S9's approximation)")

# ---- the summary since --since, short returns (bp)
s = d[d.date >= a.since]
bk = s[s.in_book10]
print(f"\n## SPEC v2 since {a.since}: {len(s)} signals on {s.ticker.nunique()} names; the 10-day book (exact sessions) {len(bk)} trades, "
      f"{bk.r10.isna().sum()} still open (no 10th session yet)\n")
print("| year | signals | book | win% (r10) | mean r10 | median r10 | PF r10 | PF r5 | worst r10 | p95 mfe5 (adverse) | median close $ | median dv20 $M |")
print("|---|---|---|---|---|---|---|---|---|---|---|---|")
for y, g in s.groupby('yr'):
    k = g[g.in_book10]; r10 = -k.r10.dropna().values; r5 = -k.r5.dropna().values
    print(f"| {y} | {len(g)} | {len(k)} | {(r10 > 0).mean() * 100:.0f}% | {r10.mean():+.0f} | {np.median(r10):+.0f} | {pf(r10):.2f} | {pf(r5):.2f} | "
          f"{r10.min():+.0f} | {np.percentile(k.mfe5.dropna(), 95):+.0f} | {k.close.median():.2f} | {k.dv20_prior.median() / 1e6:.1f} |")
r10 = -bk.r10.dropna().values; r5 = -bk.r5.dropna().values
print(f"| **all** | {len(s)} | {len(bk)} | {(r10 > 0).mean() * 100:.0f}% | {r10.mean():+.0f} | {np.median(r10):+.0f} | **{pf(r10):.2f}** | {pf(r5):.2f} | "
      f"{r10.min():+.0f} | {np.percentile(bk.mfe5.dropna(), 95):+.0f} | {bk.close.median():.2f} | {bk.dv20_prior.median() / 1e6:.1f} |")

print("\n### the D close in raw dollars (book) — what an option chain must exist on")
print(pd.cut(bk.close, [2, 5, 10, 20, 50, 1e9], labels=['$2-5', '$5-10', '$10-20', '$20-50', '$50+']).value_counts(sort=False).to_string())
rec = bk.sort_values('date').tail(15).assign(date=lambda x: x.date.dt.date, short_r1=lambda x: -x.r1, short_r5=lambda x: -x.r5,
                                             short_r10=lambda x: -x.r10, chg20_pct=lambda x: 100 * x.chg20)
print(f"\n### the most recent 15 book entries (short bp)\n{rec[['ticker', 'date', 'close', 'chg20_pct', 'rvol_d', 'short_r1', 'short_r5', 'short_r10', 'exit_date_10']].round(1).to_string(index=False)}")
