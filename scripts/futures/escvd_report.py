#!/usr/bin/env python
"""ES CVD event study: the tables. FIT sample only unless --sample hold is asked for.

  cd research && python -u scripts/futures/escvd_report.py head  > logs/escvd_report_head.log 2>&1
  cd research && python -u scripts/futures/escvd_report.py split --anchor rth --lvl 2 --r 0.25

Returns are bps of price from the next bar's vwap; t is clustered by session.
"""
import sys, argparse
import numpy as np, polars as pl
sys.path.insert(0, 'scripts/futures')
import escvd_lib as L

ap = argparse.ArgumentParser()
ap.add_argument('what', choices=['head', 'split'])
ap.add_argument('--sample', default='fit', choices=['fit', 'hold'])
ap.add_argument('--anchor', default='rth'); ap.add_argument('--lvl', type=int, default=1); ap.add_argument('--r', type=float, default=0.25)
a = ap.parse_args()

E = pl.read_parquet('data/futures/escvd/events.parquet').filter(pl.col('fit') == (a.sample == 'fit'))
B = pl.read_parquet('data/futures/escvd/bars_base.parquet').filter(pl.col('fit') == (a.sample == 'fit'))
H = (5, 10, 20, 30, 60, 120)

def cell(x, g):
    mu, t, n = L.cluster_t(np.asarray(x, float), np.asarray(g))
    return f'{mu:+6.2f} ({t:+5.1f})' if n >= 5 else '      -       '

def line(label, df, gcol='session'):
    g = df[gcol].to_numpy()
    return f'{label:<34}{df.height:>7} ' + ' '.join(cell(df[f'f{h}'].to_numpy(), g) for h in H)

hdr = f'{"":<34}{"n":>7} ' + ' '.join(f'{str(h) + "m":^14}' for h in H)

if a.what == 'head':
    print(f'sample: {a.sample}. mean forward return in bps (session-clustered t)\n')
    print(hdr)
    print(line('every bar', B, 'si'))
    print(line('every RTH bar', B.filter(pl.col('minute').is_between(930, 1319)), 'si'))
    for anchor in ('globex', 'rth'):
        print(f'\n== anchor: {anchor} ==')
        for lvl in range(4):
            ev = E.filter((pl.col('anchor') == anchor) & (pl.col('lvl') == lvl))
            ac = ev.filter(pl.col('kind') == 'cvd')['a'][0]; apx = ev.filter(pl.col('kind') == 'px')['a'][0]
            print(f'-- depth: cvd {ac:.1f} sigma / price {apx:.2f} sigma')
            for kind, thr in (('cvd', ac), ('px', apx)):
                arm = B.filter(pl.col(f'ddz_{kind}_{anchor}') <= -thr)
                print(line(f'  {kind} armed bars (no flip)', arm, 'si'))
                for r in (0.25, 0.5):
                    print(line(f'  {kind} flip r={r}', ev.filter((pl.col('kind') == kind) & (pl.col('r') == r))))
