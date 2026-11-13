#!/usr/bin/env python3
"""Report for activity.py: where the universe's option volume trades (share of contracts, of premium $, trade size).
    cd research && python3 scripts/options/activity_report.py > logs/options_activity_report.log
"""
import pandas as pd, numpy as np
pd.set_option('display.width', 250); pd.set_option('display.max_columns', 40)
P = pd.read_parquet('data/options/activity/profile.parquet')
P['per'] = pd.cut(P.year, [2011, 2015, 2019, 2022, 2026], labels=['2012-15', '2016-19', '2020-22', '2023-26']).astype(str)
DO = ['<.05', '.05-.15', '.15-.30', '.30-.45', 'ATM .45-.55', '.55-.70', '.70-.85', '>.85', 'na']
TO = ['0', '1-2', '3-7', '8-30', '31-90', '91-365', '>365']


def share(by, order):
    g = P.groupby(['per', by])[['volume', 'prem', 'trades']].sum()
    out = {}
    for per, x in g.groupby(level=0):
        x = x.droplevel(0).reindex(order)
        out[(per, 'vol%')] = 100 * x.volume / x.volume.sum()
        out[(per, 'prem%')] = 100 * x.prem / x.prem.sum()
        out[(per, 'size')] = x.volume / x.trades
    print(pd.DataFrame(out).round(1).to_string())


t = P.groupby('per')[['volume', 'prem', 'trades']].sum()
print('totals per period (universe names, in-universe days):')
print((t.assign(volume=t.volume / 1e9, prem=t.prem / 1e9, trades=t.trades / 1e9)
       .rename(columns={'volume': 'contracts bn', 'prem': 'premium $bn', 'trades': 'trades bn'})).round(2).to_string())
print('\n== by |delta| (vol% = share of contracts, prem% = share of premium $, size = contracts per trade) ==')
share('delta_b', DO)
print('\n== by calendar DTE ==')
share('dte_b', TO)
print('\n== puts vs calls ==')
share('r', ['C', 'P'])
for per in ['2012-15', '2023-26']:
    x = P[P.per == per]
    for col, lab in [('volume', 'contracts'), ('prem', 'premium $')]:
        ct = x.pivot_table(index='dte_b', columns='delta_b', values=col, aggfunc='sum').reindex(index=TO, columns=DO)
        print(f'\n== {per}: % of all {lab}, DTE x |delta| ==')
        print((100 * ct / ct.values[np.isfinite(ct.values)].sum()).round(1).to_string())
