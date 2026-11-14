#!/usr/bin/env python3
"""Full-period inputs (2012-06 -> 2026-11, market_full) for the system scripts' --full mode (user 2026-11-13: survey
the three VV systems over the whole dataset). Everything is keyed by OPTION ROOT (the historical ticker of the day:
FB until 2022-06, META after), so a script's `t = ROOT2T.get(root, root)` is the root itself.

  universe   data/options/universe_daily_full.parquet (universe_full.py): one underlying per root; the in-universe days
             are applied in the REPORTS (a trade counts when its entry day is in the universe), not here.
  stock      data/trading.db daily_adjusted (raw OHLC + causal n + cum_div) of the root's underlying, CUT AT GAPS > 45
             calendar days (a reused ticker / a relisting = a new series): only the segments that overlap the root's
             in-universe spans are kept, clipped to [first in-universe day - LOOKBACK, last + TAIL].
  splits     trading.db splits of the underlying inside the kept range.
  earnings   data/options/earnings_sched_full.parquet (earnings_full.py + earnings_scheduled.py), the underlying's rows,
             re-tagged ticker = root, inside the kept range (FB and META both carry all 58 Meta releases: each root
             only meets those inside its own option dates; the reports dedupe by (cik, R)).
  yz         data/options/yz_vol_full.parquet (yz_vol.py over the underlyings, 2011 ->), cut to the kept range.
  WIN[root]  the option-read window (ISO dates): [first in-universe day - LOOKBACK, last + TAIL] - the per-root reads
             add `and date between ...` (RAM: a 14-year chain of a big root does not fit 6 workers).
"""
import os, sys
import duckdb, numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__))); from tr_index import add_tr

GAP, LOOKBACK, TAIL = 45, 420, 120
UNIV = 'data/options/universe_daily_full.parquet'
EARN_FULL = 'data/options/earnings_sched_full.parquet'
YZ_FULL = 'data/options/yz_vol_full.parquet'


def load(eod, roots_arg='', da_cols=('close', 'n', 'cum_div'), need_yz=False):
    u = duckdb.sql(f"""select root, any_value(underlying) und, episode, min(date) lo, max(date) hi from '{UNIV}'
                       where in_universe group by root, episode""").df()
    have = {os.path.basename(f).split('__')[0] for f in os.listdir(eod)}
    u = u[u.root.isin(have)]
    if roots_arg: u = u[u.root.isin(roots_arg.split(','))]
    roots = sorted(u.root.unique())
    und = u.groupby('root').und.first().to_dict()
    c = duckdb.connect('data/trading.db', read_only=True)
    tl = ','.join(f"'{t}'" for t in sorted(set(und.values())))
    cols = ', '.join(dict.fromkeys(['close', 'n', 'cum_div', *da_cols]))
    da = c.sql(f"""select ticker, date, {cols} from daily_adjusted where ticker in ({tl}) and date >= '2010-06-01'
                  order by ticker, date""").df()
    da['date'] = pd.to_datetime(da.date)
    da['seg'] = (da.groupby('ticker').date.diff().dt.days > GAP).groupby(da.ticker).cumsum()
    sp = c.sql(f"select ticker, execution_date d from splits where ticker in ({tl})").df()
    sp['d'] = pd.to_datetime(sp.d)
    e = pd.read_parquet(EARN_FULL); e = e[e.ticker.isin(set(und.values()))]
    e['et_date'] = pd.to_datetime(e.et_date)
    yz = pd.read_parquet(YZ_FULL) if need_yz else None
    if yz is not None: yz['date'] = pd.to_datetime(yz.date)
    DAg = dict(tuple(da.groupby('ticker'))); SPg = dict(tuple(sp.groupby('ticker'))); Eg = dict(tuple(e.groupby('ticker')))
    YZg = dict(tuple(yz.groupby('ticker'))) if yz is not None else {}
    parts, eparts, SPL, YZ, WIN = [], [], {}, {}, {}
    for r, g in u.groupby('root'):
        t = und[r]; lo, hi = g.lo.min(), g.hi.max()
        WIN[r] = (str((lo - pd.Timedelta(days=LOOKBACK)).date()), str((hi + pd.Timedelta(days=TAIL)).date()))
        d = DAg.get(t)
        if d is None: continue
        keep = set()
        for _, ep in g.iterrows():   # the segments overlapping each in-universe span
            sg = d.groupby('seg').date.agg(['min', 'max'])
            keep |= set(sg[(sg['min'] <= ep.hi) & (sg['max'] >= ep.lo)].index)
        a0, a1 = lo - pd.Timedelta(days=LOOKBACK), hi + pd.Timedelta(days=TAIL)
        d = d[d.seg.isin(keep) & (d.date >= a0) & (d.date <= a1)]
        if d.empty: continue
        b0, b1 = d.date.min(), d.date.max()
        parts.append(d.assign(ticker=r))
        s = SPg.get(t)
        # SORTED (2026-11-14: unsorted arrays broke short_dated.py's searchsorted split count - TSLA/AAPL/NVDA splits slipped in)
        SPL[r] = np.sort(np.array(s.d[(s.d > b0) & (s.d <= b1)].values, dtype='datetime64[D]')) if s is not None else np.array([], 'datetime64[D]')
        x = Eg.get(t)
        if x is not None: eparts.append(x[(x.et_date >= b0 - pd.Timedelta(days=5)) & (x.et_date <= b1)].assign(ticker=r))
        y = YZg.get(t)
        if y is not None: YZ[r] = y[(y.date >= b0) & (y.date <= b1)]
    da = add_tr(pd.concat(parts, ignore_index=True).sort_values(['ticker', 'date'], kind='stable').reset_index(drop=True))
    E = pd.concat(eparts, ignore_index=True)
    TD = np.array(sorted(c.sql("select distinct date from daily_adjusted where ticker = 'SPY' and date >= '2010-06-01'").df().date),
                  dtype='datetime64[D]')
    return dict(roots=roots, ROOT2T={}, tick=roots, UND=und, WIN=WIN, c=c, da=da, SPL=SPL, E_RAW=E, YZRAW=YZ, TD=TD,
                TDALL=TD, TDSET={str(x) for x in TD},
                SPOT={r: g.set_index('date').close for r, g in da.groupby('ticker')},
                VADJ={r: g.set_index('date').V for r, g in da.groupby('ticker')})


if __name__ == '__main__':      # a self-check: sizes and a few roots
    import time; t = time.time()
    I = load('data/options/market_full')
    print(f"{time.time() - t:.0f}s  roots {len(I['roots'])}  stock rows {len(I['da']):,}  events {len(I['E_RAW']):,}  TD {I['TD'][0]}..{I['TD'][-1]}")
    for r in ['FB', 'META', 'TWTR', 'DELL', 'AAPL']:
        s = I['SPOT'].get(r)
        print(r, I['UND'].get(r), I['WIN'].get(r), None if s is None else (s.index.min().date(), s.index.max().date(), len(s)),
              len(I['E_RAW'][I['E_RAW'].ticker == r]), I['SPL'].get(r))
