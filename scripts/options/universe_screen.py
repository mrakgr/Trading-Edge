#!/usr/bin/env python3
"""StraddleHarvester SH-5 — the universe screen from Massive's whole-market options day aggregates (2022-11-09 →).

Screen (user 2026-11-10, after Volatility Vibes): the underlying's option volume — calls + puts, every expiry, every
root of the underlying — averages >= 25,000 contracts a day over the 20 trading days BEFORE day D (day D excluded:
`20 PRECEDING AND 1 PRECEDING`). A trading day with no contracts counts as 0. The first 20 days are not evaluable.
Trading days = the days Massive has a file for.

Root -> underlying: STANDARD roots only (the tradable product), BRKB -> BRK.B, BFB -> BF.B. ADJUSTED roots (a corporate
action appends a digit: CMCS1, WOLF1 — non-standard deliverables, e.g. shares + cash) are DROPPED from the screen and the
book: their prices are not straddles on the stock (WOLF1 averaged 86k contracts a day after the restructuring). Single stocks only (user): ticker_reference type CS or ADRC
(ADRs are single companies); ETFs / ETNs / indices are out. A root with no type is reported, not silently kept.

    cd research && python3 scripts/options/universe_screen.py > logs/options_universe_screen.log 2>&1; echo "exit $?"
writes data/options/universe_daily.parquet (date, underlying, type, volume, avg20_prior, in_universe)
"""
import argparse
import duckdb, pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument('--aggs-dir', default='data/options/massive_day_aggs')
ap.add_argument('--min-contracts', type=float, default=25_000)
ap.add_argument('--window', type=int, default=20)
ap.add_argument('--out', default='data/options/universe_daily.parquet')
a = ap.parse_args()

con = duckdb.connect()
con.execute("SET memory_limit='4GB'; SET threads=4")
rv = con.sql(f"""
    select regexp_extract(ticker, '^O:([A-Z0-9.]+?)\\d{{6}}[CP]\\d{{8}}$', 1) as root,
           strptime(regexp_extract(filename, '(\\d{{4}}-\\d{{2}}-\\d{{2}})\\.csv\\.gz$', 1), '%Y-%m-%d')::DATE as date,
           sum(volume)::BIGINT as volume
    from read_csv('{a.aggs_dir}/*.csv.gz', filename=true, columns={{'ticker':'VARCHAR','volume':'DOUBLE','open':'DOUBLE',
         'close':'DOUBLE','high':'DOUBLE','low':'DOUBLE','window_start':'BIGINT','transactions':'BIGINT'}}, header=true)
    group by all""").df()
bad = rv[rv.root == '']
print(f"{rv.date.nunique()} days {rv.date.min()} → {rv.date.max()}; {rv.root.nunique():,} roots; "
      f"unparsed tickers' volume: {int(bad.volume.sum()):,}")
rv = rv[rv.root != '']
adj = rv.root.str.contains(r'\d')
print(f"adjusted roots dropped: {rv[adj].root.nunique()} ({int(rv[adj].volume.sum()):,} contracts, "
      f"{rv[adj].volume.sum() / rv.volume.sum():.2%} of all)")
rv = rv[~adj]

ref = duckdb.connect('data/trading.db', read_only=True).sql("select ticker, type from ticker_reference").df()
typ = dict(zip(ref.ticker, ref.type))
SPECIAL = {'BRKB': 'BRK.B', 'BFB': 'BF.B'}


def underlying(root):
    if root in SPECIAL:
        return SPECIAL[root]
    return root


rv['underlying'] = rv.root.map({r: underlying(r) for r in rv.root.unique()})
uv = rv.groupby(['underlying', 'date'], as_index=False).volume.sum()
days = sorted(uv.date.unique())
grid = uv.pivot(index='date', columns='underlying', values='volume').reindex(days).fillna(0)
avg = grid.shift(1).rolling(a.window, min_periods=a.window).mean()
long = avg.stack().rename('avg20_prior').reset_index()
long = long[long.avg20_prior >= a.min_contracts * 0.5]                    # keep the near-misses for the record
long = long.merge(uv, on=['underlying', 'date'], how='left').fillna({'volume': 0})
long['type'] = long.underlying.map(typ).fillna('?')
long['in_universe'] = (long.avg20_prior >= a.min_contracts) & long.type.isin(['CS', 'ADRC'])
long.to_parquet(a.out, index=False)

p = long[long.avg20_prior >= a.min_contracts]
print(f"\npassing the volume bar on any day, by type (underlyings): {p.drop_duplicates('underlying').type.value_counts().to_dict()}")
unk = p[p.type == '?'].groupby('underlying').avg20_prior.max().sort_values(ascending=False)
print(f"no ticker_reference type ({len(unk)}), by max avg20: {' '.join(f'{u}:{v / 1e3:.0f}k' for u, v in unk.head(60).items())}")
u = long[long.in_universe]
per_day = u.groupby('date').size()
print(f"\nUNIVERSE (single stocks): {u.underlying.nunique()} names ever; per evaluable day: min {per_day.min()}, "
      f"median {per_day.median():.0f}, max {per_day.max()}")
print("per quarter (median names per day · distinct names):")
q = u.assign(q=pd.PeriodIndex(u.date, freq='Q')).groupby('q').agg(names=('underlying', 'nunique'))
q['per_day'] = u.assign(q=pd.PeriodIndex(u.date, freq='Q')).groupby(['q', 'date']).size().groupby('q').median()
print(q.to_string())
days_in = u.groupby('underlying').size().sort_values()
print(f"\ndays in the universe per name: median {days_in.median():.0f}, names with < 20 days {(days_in < 20).sum()}")
print(f"wrote {len(long):,} rows -> {a.out}")
