#!/usr/bin/env python3
"""StraddleHarvester SH-4 — the universe screen on a pilot month, and the stock-liquidity PREFILTER it must survive.

Screen (user 2026-11-10): a root's option volume (calls + puts, every expiry) averages >= 25,000 contracts a day over
the 20 trading days BEFORE day D (day D excluded). A day the root has no rows counts as 0.
Prefilter (download only, never trading): the stock's 20-prior-day average dollar volume (close x volume, Massive day
aggregates). The question: what stock-dv floor keeps EVERY root that passes the screen, and how many tickers does it keep?

    cd research && python3 scripts/options/universe_pilot.py --eod-dir data/options/eod_pilot_2026-10
"""
import argparse, glob, os
import duckdb, pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument('--eod-dir', required=True)
ap.add_argument('--min-contracts', type=float, default=25_000)
ap.add_argument('--window', type=int, default=20)
a = ap.parse_args()

con = duckdb.connect()
con.execute("SET memory_limit='4GB'")
vol = con.sql(f"""select root, date, sum(volume)::BIGINT as volume, count(*) as n_contracts
                  from read_parquet('{a.eod_dir}/*.parquet') group by all""").df()
vol['date'] = pd.to_datetime(vol.date)
days = sorted(vol.date.unique())
grid = vol.pivot(index='date', columns='root', values='volume').reindex(days).fillna(0)
avg = grid.shift(1).rolling(a.window, min_periods=a.window).mean()          # 20 PRECEDING .. 1 PRECEDING
screen_days = avg.dropna(how='all').index
print(f"{vol.root.nunique():,} roots with rows on {len(days)} days ({days[0]:%Y-%m-%d} → {days[-1]:%Y-%m-%d}); "
      f"screen evaluable on {len(screen_days)} days")

# stock: 20-prior-day average dollar volume on the same days (raw close x volume; the day files are unadjusted)
files = sorted(glob.glob('data/daily_aggregates/*.csv.gz'))
lo = (pd.Timestamp(days[0]) - pd.Timedelta(days=45)).strftime('%Y-%m-%d')
files = [f for f in files if lo <= os.path.basename(f)[:10] <= pd.Timestamp(days[-1]).strftime('%Y-%m-%d')]
st = pd.concat([pd.read_csv(f, usecols=['ticker', 'volume', 'close']).assign(date=pd.Timestamp(os.path.basename(f)[:10]))
                for f in files])
st['dv'] = st.volume * st.close
sg = st.pivot(index='date', columns='ticker', values='dv').fillna(0)
sdv = sg.shift(1).rolling(a.window, min_periods=a.window).mean()

ref = duckdb.connect('data/trading.db', read_only=True).sql("select ticker, type from ticker_reference").df()
typ = dict(zip(ref.ticker, ref.type))

rows = []
for d in screen_days:
    passing = avg.loc[d][avg.loc[d] >= a.min_contracts]
    for root, v in passing.items():
        rows.append(dict(date=d, root=root, avg_contracts=v, type=typ.get(root, '?'),
                         stock_dv20=sdv.loc[d].get(root) if d in sdv.index else None))
p = pd.DataFrame(rows)
print(f"\npassing roots per screen day: {p.groupby('date').size().to_dict()}")
last = p[p.date == p.date.max()].sort_values('avg_contracts', ascending=False)
print(f"\nlast screen day {p.date.max():%Y-%m-%d}: {len(last)} roots pass — by type: {last.type.value_counts().to_dict()}")
u = p.drop_duplicates('root')
print(f"any screen day: {len(u)} roots — by type: {u.type.value_counts().to_dict()}")
cs = p[p.type.isin(['CS', 'ADRC'])]
q = cs.groupby('root').stock_dv20.min()
print(f"\nsingle names (CS + ADRC) passing on any day: {len(q)}; their stock dv20 ($M): "
      f"min {q.min() / 1e6:.1f}, p1 {q.quantile(.01) / 1e6:.1f}, p5 {q.quantile(.05) / 1e6:.1f}, "
      f"median {q.median() / 1e6:.1f}; no stock row: {q.isna().sum()}")
print("lowest stock dv20 among them:"); print((q.sort_values().head(15) / 1e6).round(1).to_string())
d = p.date.max()
for floor in [5e6, 10e6, 20e6, 30e6, 50e6]:
    kept = (sdv.loc[d] >= floor).sum()
    lost = (q < floor).sum()
    print(f"  floor ${floor / 1e6:.0f}M: {kept:,} stock tickers kept on {d:%Y-%m-%d}; passing single names lost: {lost}")
na = [r for r in u.root if r not in typ]
print(f"\nroots without a ticker_reference type ({len(na)}): {' '.join(sorted(na)[:80])}")
print("\ntop 30 by avg contracts on the last day:")
print(last.head(30)[['root', 'type', 'avg_contracts', 'stock_dv20']].assign(
    avg_contracts=lambda x: x.avg_contracts.round(0), stock_dv20=lambda x: (x.stock_dv20 / 1e6).round(0)).to_string(index=False))
os.makedirs('data/options', exist_ok=True)
p.to_parquet('data/options/universe_pilot_2026-10.parquet', index=False)
