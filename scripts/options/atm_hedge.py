#!/usr/bin/env python3
"""BUYING ATM OPTIONS vs THE DELTA HEDGE (user 2026-11-14). Same root-day-expiry, entry at the close, held to expiry:
  stock    = short 25d (sold at the bid) + DAILY stock delta hedge - 2bp per share traded (SH-29, the reference)
  ratio    = short 1 x 25d (bid) + long n x ATM same right (bought at the ASK), n = delta25 / deltaATM at entry (~0.5):
             delta-neutral at entry with OPTIONS, nothing rebalanced (a 1x2 ratio spread per 2 shorts)
  ratio+dh = ratio + a daily stock hedge of what delta is left (hd25 - n x hdATM, both legs' turnover charged 2bp)
  long ATM = buy the ATM option at the ask, unhedged, held to expiry
P&L per share in % of the 25d option's MID premium (comparable with SH-28..35) and in bp of spot. Signal = the 25d option's
own skew (IV25p/IV_ATM for puts, IV25c/IV_ATM for calls) >= the prior years' 90th pct (same kind, same right).
hs <= 5% on the 25d leg AND the ATM leg; no earnings in the hold; 2013+.
    cd research && python3 scripts/options/atm_hedge.py > logs/options_atm_hedge.log
"""
import numpy as np, pandas as pd
pd.set_option('display.width', 250)
T = pd.read_parquet('data/options/short_dated/trades.parquet')
from sd_common import drop_bad; T = drop_bad(T)   # SH-36: vanished contracts (corporate actions)
T = T[T.inst.isin(['P25', 'C25', 'AP', 'AC']) & ~T.earn]
H = pd.read_parquet('data/options/short_dated/hedge.parquet')
T = T.merge(H, on=['root', 'date', 'exp', 'K', 'r', 'inst'])
keys = ['root', 'date', 'exp']
cols = ['bid', 'ask', 'settle', 'dl', 'hd', 'turn', 'K']
rows = []
for w, a in [('P25', 'AP'), ('C25', 'AC')]:
    x = T[T.inst == w].merge(T[T.inst == a][keys + cols], on=keys, suffixes=('', '_a'))
    x['side'] = 'put' if w == 'P25' else 'call'
    rows.append(x)
D = pd.concat(rows, ignore_index=True)
D['mid'] = (D.bid + D.ask) / 2; D['mid_a'] = (D.bid_a + D.ask_a) / 2
D = D[(D.mid >= .05) & (D.mid_a >= .05)].copy()
D['hs'] = (D.ask - D.bid) / 2 / D.mid; D['hs_a'] = (D.ask_a - D.bid_a) / 2 / D.mid_a
D = D[(D.hs <= .05) & (D.hs_a <= .05)]
D['year'] = D.date.dt.year
D = D[D.year > 2012].copy()
D['n'] = D.dl / D.dl_a                                         # ~0.5 ATM per 25d
c = 2e-4 * D.S
short = D.bid - D.settle                                        # per share, sold at the bid
D['stock'] = (short + D.hd - D.turn * c)
D['ratio'] = short + D.n * (D.settle_a - D.ask_a)
D['ratio_dh'] = D.ratio + (D.hd - D.n * D.hd_a) - (D.turn + D.n * D.turn_a) * c
D['naked'] = short
D['longATM'] = D.settle_a - D.ask_a
D['skw'] = np.where(D.side == 'put', D.iv25p, D.iv25c) / D.iv_atm
D['sig'] = False
for (k, s), g in D.groupby(['kind', 'side']):
    for y in sorted(g.year.unique()):
        p = g.loc[g.year < y, 'skw'].dropna()
        if len(p) < 2000: continue
        D.loc[g.index[(g.year == y) & (g.skw >= p.quantile(.9))], 'sig'] = True


def st(x, col, denom='mid'):
    v = x[col] / x[denom]; b = 1e4 * x[col] / x.S
    dm = v.groupby(x.date).mean(); w, l = v[v > 0].sum(), -v[v < 0].sum()
    yp = v.groupby(x.year).mean()
    return (f'{col:9s} mean {100 * v.mean():7.1f}%  med {100 * v.median():6.1f}%  PF {w / l:5.2f}  t {dm.mean() / dm.std() * np.sqrt(len(dm)):5.1f}  '
            f'win {100 * (v > 0).mean():4.1f}%  p1 {100 * v.quantile(.01):6.0f}%  | {b.mean():6.1f} bp of spot (sd {b.std():5.0f})  yrs+ {(yp > 0).sum()}/{len(yp)}  worst yr {100 * yp.min():+.0f}%')


for s in ['put', 'call']:
    for k in ['stock', 'etf']:
        x = D[(D.side == s) & (D.kind == k)]
        for lab, m in [('SIGNAL (skew top 10%)', x.sig), ('all', x.sig | ~x.sig)]:
            xx = x[m]
            print(f'\n== {k} {s}s, {lab}: n={len(xx):,}  ATM per 25d n={xx.n.median():.2f}; 25d premium {1e4 * (xx.mid / xx.S).median():.0f} bp of spot, '
                  f'ATM {1e4 * (xx.mid_a / xx.S).median():.0f} bp; the ratio is a net {"DEBIT" if (xx.n * xx.ask_a - xx.bid).median() > 0 else "credit"} '
                  f'of {1e4 * ((xx.n * xx.ask_a - xx.bid) / xx.S).median():.0f} bp (median)')
            for col in ['stock', 'ratio', 'ratio_dh', 'naked']: print('   ' + st(xx, col))
            print('   ' + st(xx, 'longATM', 'mid_a') + '   <- % of the ATM premium paid')
