#!/usr/bin/env python
"""Continuous front-month ES 1-minute bars for the CVD study (docs/es_cvd_plan.md).

  cd research && python -u scripts/futures/escvd_bars.py > logs/escvd_bars.log 2>&1

Input : data/futures/intraday_1s_ticks/YYYY-MM-DD.parquet (ET calendar date, every contract).
Output: data/futures/escvd/es_1m.parquet, one row per (session, minute) on a DENSE grid.

Session = the Globex day: 18:00 ET of the previous calendar day to 17:00 ET, named for the day
its RTH falls on. minute = minutes since 18:00 (0..1379); RTH 09:30-16:00 is minute 930..1319.

Front contract (docs/futures_data.md "Rolling"): chosen from the PREVIOUS session's volume, the
next contract takes over the session after its volume first exceeds the old one's, never back.

Prices are FORWARD-adjusted: the first contract keeps its prices, each later one is shifted by
the gap measured in the last RTH hour of the session before its roll (known at the roll).
Raw price = adjusted - adj. Minutes with no trade carry the previous close with volume 0.
"""
import sys, time, glob, os, argparse
import numpy as np, polars as pl

T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, flush=True)

ap = argparse.ArgumentParser()
ap.add_argument('--root', default='ES')
ap.add_argument('--src', default='data/futures/intraday_1s_ticks')
ap.add_argument('--out', default='data/futures/escvd/es_1m.parquet')
a = ap.parse_args()

MONTH = {'H': 3, 'M': 6, 'U': 9, 'Z': 12}
files = sorted(glob.glob(f'{a.src}/*.parquet'))
log(f'{len(files)} day files')

# ---- 1. per-contract 1m bars, a year of files at a time -----------------------------------
parts = []
years = sorted({os.path.basename(f)[:4] for f in files})
for y in years:
    fs = [f for f in files if os.path.basename(f).startswith(y)]
    lf = (pl.scan_parquet(fs, include_file_paths='path')
          .filter(pl.col('ticker').str.contains(f'^{a.root}[HMUZ][0-9][0-9]$'))
          .filter((pl.col('bucket') < 61200) | (pl.col('bucket') >= 64800))
          .with_columns(d=pl.col('path').str.extract(r'(\d{4}-\d{2}-\d{2})\.parquet$').str.to_date())
          .with_columns(
              session=pl.when(pl.col('bucket') >= 64800).then(pl.col('d') + pl.duration(days=1)).otherwise(pl.col('d')),
              minute=(((pl.col('bucket') + 21600) % 86400) // 60).cast(pl.Int32))
          .sort('ticker', 'session', 'minute', 'bucket')
          .group_by('ticker', 'session', 'minute', maintain_order=True)
          .agg(o=pl.col('vwap').first().cast(pl.Float64), h=pl.col('vwap').max().cast(pl.Float64),
               l=pl.col('vwap').min().cast(pl.Float64), c=pl.col('vwap').last().cast(pl.Float64),
               pv=(pl.col('vwap').cast(pl.Float64) * pl.col('volume')).sum(),
               volume=pl.col('volume').cast(pl.Float64).sum(), delta=pl.col('delta').cast(pl.Float64).sum(),
               trades=pl.col('trade_count').sum()))
    df = lf.collect()
    if df.height:
        parts.append(df)
        log(f'{y}: {len(fs)} files, {df.height} contract-minutes, {df["ticker"].n_unique()} contracts')
bars = pl.concat(parts)
# a session straddles two calendar files (and two years): merge the halves
bars = (bars.group_by('ticker', 'session', 'minute')
        .agg(o=pl.col('o').first(), h=pl.col('h').max(), l=pl.col('l').min(), c=pl.col('c').last(),
             pv=pl.col('pv').sum(), volume=pl.col('volume').sum(), delta=pl.col('delta').sum(),
             trades=pl.col('trades').sum())
        .sort('ticker', 'session', 'minute'))
bars = bars.filter(pl.col('session').dt.weekday() <= 5)   # a Friday-evening print would name a Saturday
log(f'{bars.height} contract-minutes, sessions {bars["session"].min()} .. {bars["session"].max()}')

# ---- 2. the front contract per session, from the previous session's volume ----------------
def expiry(t): return (2000 + int(t[-2:])) * 12 + MONTH[t[-3]]
sv = bars.group_by('session', 'ticker').agg(vol=pl.col('volume').sum()).sort('session')
sessions = sv['session'].unique().sort().to_list()
vol = {(r[0], r[1]): r[2] for r in sv.iter_rows()}
by_sess = {}
for (s, t), v in vol.items(): by_sess.setdefault(s, {})[t] = v
front, cur = {}, None
for i, s in enumerate(sessions):
    ref = by_sess[sessions[i - 1]] if i else by_sess[s]     # the first session has no yesterday
    lead = max(ref, key=ref.get)
    if cur is None or expiry(lead) > expiry(cur): cur = lead
    front[s] = cur

# ---- 3. forward adjustment: the gap in the last RTH hour before each roll ------------------
adj, rolls, off = {}, [], 0.0
for i, s in enumerate(sessions):
    if i and front[s] != front[sessions[i - 1]]:
        p, old, new = sessions[i - 1], front[sessions[i - 1]], front[s]
        w = bars.filter((pl.col('session') == p) & pl.col('ticker').is_in([old, new]))
        piv = w.pivot(on='ticker', index='minute', values='c').drop_nulls()
        last = piv.filter(pl.col('minute').is_between(1260, 1319))
        use = last if last.height >= 10 else piv
        gap = float((use[new] - use[old]).median())
        off -= gap
        rolls.append((s, old, new, gap, use.height))
    adj[s] = off
for r in rolls: log(f'roll {r[0]}  {r[1]} -> {r[2]}  gap {r[3]:+8.2f}  ({r[4]} common minutes)')

# ---- 4. the dense grid ---------------------------------------------------------------------
fr = pl.DataFrame({'session': sessions, 'ticker': [front[s] for s in sessions],
                   'adj': [adj[s] for s in sessions]}).with_columns(pl.col('session').cast(pl.Date))
fb = bars.join(fr, on=['session', 'ticker'], how='inner')
grid = (fr.select('session', 'ticker', 'adj')
        .join(pl.DataFrame({'minute': np.arange(1380, dtype=np.int32)}), how='cross')
        .join(fb.drop('adj'), on=['session', 'ticker', 'minute'], how='left')
        .sort('session', 'minute'))
grid = grid.with_columns(filled=pl.col('volume').is_null(),
                         vwap=pl.col('pv') / pl.col('volume'))
for c in ('o', 'h', 'l', 'c', 'vwap'):
    grid = grid.with_columns((pl.col(c) + pl.col('adj')).alias(c))
grid = grid.with_columns(c=pl.col('c').forward_fill())
grid = grid.with_columns(pc=pl.col('c').shift(1))
grid = grid.with_columns(
    [pl.when(pl.col('filled')).then(pl.col('pc')).otherwise(pl.col(c)).alias(c) for c in ('o', 'h', 'l', 'vwap')]
    + [pl.col(c).fill_null(0) for c in ('volume', 'delta', 'trades')])
grid = grid.filter(pl.col('c').is_not_null() & pl.col('o').is_not_null())
grid = grid.with_columns(
    ts=(pl.col('session').cast(pl.Datetime) - pl.duration(hours=6) + pl.duration(minutes=pl.col('minute'))))
grid = grid.select('session', 'minute', 'ts', pl.col('ticker').alias('contract'), 'o', 'h', 'l', 'c', 'vwap',
                   'volume', 'delta', 'trades', 'adj', 'filled')
grid.write_parquet(a.out)

d = grid.group_by('session').agg(vol=pl.col('volume').sum(), rth=pl.col('volume').filter(pl.col('minute').is_between(930, 1319)).sum(),
                                 fill=pl.col('filled').mean()).sort('session')
log(f'wrote {a.out}: {grid.height} rows, {d.height} sessions, {grid["session"].min()} .. {grid["session"].max()}')
log(f'session volume median {d["vol"].median():,.0f}; RTH share {d["rth"].sum() / d["vol"].sum():.3f}; '
    f'sessions with no RTH volume {d.filter(pl.col("rth") == 0).height}; median filled share {d["fill"].median():.4f}')
log(f'last adj {grid["adj"][-1]:+.2f}; raw last close {grid["c"][-1] - grid["adj"][-1]:.2f}')
