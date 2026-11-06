#!/usr/bin/env python3
"""SpringFlyer S12 — is the 60-session median dollar volume a QUALITY proxy for the short? (user 2026-11-06: S11 found the
names WITH listed options are the weaker shorts — PF r10 2.55 vs 4.23; HighFlyerV2 Run 32 found dv60_med tracks the float)

dv60_med = median(volume × close) over the 60 sessions BEFORE D (same episode; raw volume × raw close = dollars, scale-free),
fewer when the episode is younger (n60 recorded). Knowable before D's open. The book = signals_spec_v2.parquet in_book10
(exact-session mc=1, phantom suspects out), short returns.
  1. the book in dv60_med quintiles (and fixed $ bands), r5 / r10, both halves of the history
  2. dv60_med against dv20_prior (the frame's 20-day mean, which carries the run-up's own volume)
  3. the S11 control: inside the same dv60 band, do the names WITH an option chain still short worse?

    cd research && python3 scripts/equity/springflyer_dv60.py
"""
import duckdb, numpy as np, pandas as pd
import data_v2

con = data_v2.attach(duckdb.connect(), 'main'); con.execute("SET memory_limit='5GB'; SET threads=8")
b = pd.read_parquet('data/equity/springflyer/signals_spec_v2.parquet')
b = b[b.in_book10 & b.r10.notna()].copy(); b['date'] = pd.to_datetime(b.date)
con.register('b', b[['ticker', 'date']])
dv = con.execute("""
WITH t AS (SELECT DISTINCT ticker FROM b),
p AS (SELECT x.ticker, x.date,
        MEDIAN(x.volume * x.close) OVER (w ROWS BETWEEN 60 PRECEDING AND 1 PRECEDING) AS dv60_med,
        COUNT(*) OVER (w ROWS BETWEEN 60 PRECEDING AND 1 PRECEDING) AS n60
      FROM daily_episodes_causal x JOIN t USING (ticker) WHERE x.date >= '2004-01-01'
      WINDOW w AS (PARTITION BY x.ticker, x.episode ORDER BY x.date))
SELECT p.* FROM p JOIN b USING (ticker, date)""").df()
dv['date'] = pd.to_datetime(dv.date)
b = b.merge(dv, on=['ticker', 'date'], how='left')
cov = pd.read_parquet('data/equity/springflyer/option_coverage.parquet')[['ticker', 'date', 'has_chain']]
cov['date'] = pd.to_datetime(cov.date)
b = b.merge(cov, on=['ticker', 'date'], how='left')
b['s5'], b['s10'] = -b.r5, -b.r10


def pf(r):
    r = r[~np.isnan(r)]; g, l = r[r > 0].sum(), -r[r < 0].sum()
    return g / l if l > 0 else float('inf')


def row(lab, g):
    return (f"| {lab} | {len(g)} | {(g.s10 > 0).mean() * 100:.0f}% | {g.s10.mean():+.0f} | {g.s10.median():+.0f} | **{pf(g.s10.values):.2f}** | "
            f"{pf(g.s5.values):.2f} | {g.s10.min():+.0f} | {np.percentile(g.mfe5.dropna(), 95):+.0f} | {g.dv60_med.median() / 1e6:.2f} |")


H = "| cell | trades | win% r10 | mean r10 | median r10 | PF r10 | PF r5 | worst r10 | p95 mfe5 | median dv60 $M |\n|---|---|---|---|---|---|---|---|---|---|"
print(f"book: {len(b)} trades {b.date.min().date()} → {b.date.max().date()}; dv60_med missing {b.dv60_med.isna().sum()}; "
      f"episode younger than 60 sessions {int((b.n60 < 60).sum())}")
print(f"rank correlation dv60_med vs dv20_prior: {b.dv60_med.rank().corr(b.dv20_prior.rank()):.2f}; "
      f"median dv20_prior / dv60_med = {(b.dv20_prior / b.dv60_med).median():.1f}x (the run-up's own volume)")

b['q'] = pd.qcut(b.dv60_med, 5, labels=['Q1 lowest', 'Q2', 'Q3', 'Q4', 'Q5 highest'])
print("\n### 1. the book by dv60_med QUINTILE, short bp\n" + H)
for q, g in b.groupby('q', observed=True):
    print(row(f"{q} ({g.dv60_med.min() / 1e6:.2f}-{g.dv60_med.max() / 1e6:.2f} $M)", g))
print(row('**all**', b))

BANDS = [0, 1e6, 2e6, 5e6, 2e7, 1e12]; LAB = ['< $1M', '$1-2M', '$2-5M', '$5-20M', '> $20M']
b['band'] = pd.cut(b.dv60_med, BANDS, labels=LAB)
print("\n### 1b. fixed $ bands (HighFlyerV2 Run 32's cut was $1-2M)\n" + H)
for q, g in b.groupby('band', observed=True):
    print(row(q, g))

print("\n### 1c. both halves of the history, PF r10 (trades)")
print("| band | 2005-2019 | 2020-2026 |\n|---|---|---|")
for q, g in b.groupby('band', observed=True):
    a_, c_ = g[g.date.dt.year < 2020], g[g.date.dt.year >= 2020]
    print(f"| {q} | {pf(a_.s10.values):.2f} ({len(a_)}) | {pf(c_.s10.values):.2f} ({len(c_)}) |")

print("\n### 1d. the year table split at the median dv60 (PF r10, trades)")
med = b.dv60_med.median(); print(f"median dv60_med = ${med / 1e6:.2f}M\n| year | below | above |\n|---|---|---|")
for y, g in b[b.date.dt.year >= 2016].groupby(b.date.dt.year):
    lo, hi = g[g.dv60_med <= med], g[g.dv60_med > med]
    print(f"| {y} | {pf(lo.s10.values):.2f} ({len(lo)}) | {pf(hi.s10.values):.2f} ({len(hi)}) |")

print("\n### 2. dv20_prior quintiles beside it (the frame's 20-day mean: includes the run-up)\n" + H)
for q, g in b.groupby(pd.qcut(b.dv20_prior, 5, labels=['Q1', 'Q2', 'Q3', 'Q4', 'Q5']), observed=True):
    print(row(f"dv20 {q}", g))

c = b[b.has_chain.notna()].copy()                    # the 2016+ book that S11 probed
c['half'] = np.where(c.dv60_med <= c.dv60_med.median(), 'dv60 below median', 'dv60 above median')
print(f"\n### 3. the S11 control (2016+, {len(c)} trades; median dv60 ${c.dv60_med.median() / 1e6:.2f}M): chain vs no chain INSIDE a dv60 half\n" + H)
for (h, ch), g in c.groupby(['half', 'has_chain']):
    print(row(f"{h} · {'chain' if ch else 'no chain'}", g))
print(f"\nchain share: below median {c[c.half == 'dv60 below median'].has_chain.mean() * 100:.0f}%, "
      f"above {c[c.half == 'dv60 above median'].has_chain.mean() * 100:.0f}%")

print("\n### 4. is the chain a disguised YEAR mix or LISTING AGE? (2016+; barnum = sessions since the episode began)")
c['era'] = np.where(c.date.dt.year <= 2021, '2016-2021', '2022-2026')
c['age'] = pd.cut(c.barnum, [0, 250, 1000, 1e9], labels=['< 1 yr listed', '1-4 yrs', '> 4 yrs'])
c['px'] = pd.cut(c.close, [0, 10, 1e9], labels=['< $10', '>= $10'])
print("| stratum | chain: trades / PF r10 / median r10 | no chain: trades / PF r10 / median r10 |\n|---|---|---|")
for col in ['era', 'age', 'px']:
    for k, g in c.groupby(col, observed=True):
        y, n = g[g.has_chain == True], g[g.has_chain == False]
        print(f"| {col} {k} | {len(y)} / {pf(y.s10.values):.2f} / {y.s10.median():+.0f} | {len(n)} / {pf(n.s10.values):.2f} / {n.s10.median():+.0f} |")
# the iso-trip control: a random subset of the no-chain trades of the chain subset's size, same dv60 half mix
rng = np.random.default_rng(11); pfs = []
for _ in range(2000):
    s = pd.concat([c[(c.has_chain == False) & (c.half == h)].sample(int(((c.has_chain == True) & (c.half == h)).sum()), replace=True, random_state=rng.integers(1 << 31))
                   for h in c.half.unique()])
    pfs.append(pf(s.s10.values))
pfs = np.array(pfs); ch = pf(c[c.has_chain == True].s10.values)
print(f"\niso-trip control: {int((c.has_chain == True).sum())} no-chain trades drawn with the chain subset's dv60 mix, 2000 draws: "
      f"PF r10 p5 {np.percentile(pfs, 5):.2f} / median {np.median(pfs):.2f} / p95 {np.percentile(pfs, 95):.2f}; the chain subset {ch:.2f} "
      f"(share of draws at or below it {(pfs <= ch).mean() * 100:.1f}%)")
