#!/usr/bin/env python3
"""Report for fill_study.py (data/options/fills/fills.parquet). Per bucket, in % of contract-days:
  trd1 = the contract trades on t+1 at all; sh / sh> = a short limit at M fills (high1 >= M / > M); lg = long (low1 <= M)
  same-day lookahead: up0 / dn0 = day t printed above / below its own closing mid.
  gapS = % of short fills that came at the OPEN (gapped through M: fill price = the open), impS = their mean price
  improvement over M (% of M, over all short fills).
  markouts (fill price = max(M, open1) short / min(M, open1) long) at the t+1 close, % of M, signed for the side (+ good): moS = filled shorts, moL = filled longs,
  drift = all contract-days (short-signed: what the mid did anyway). hs = half-spread at t (the cost of crossing).
  cross = drift - hs (short, marketable at t's bid, marked at t+1's mid);  rest = sh x moS (per INTENDED short).
    cd research && python3 scripts/options/fill_report.py > logs/options_fill_report.log
"""
import pandas as pd
pd.set_option('display.width', 250); pd.set_option('display.max_columns', 30); pd.set_option('display.max_rows', 200)
D = pd.read_parquet('data/options/fills/fills.parquet')
D = D[D.delta_b != 'na']


def tab(by, d=D):
    g = d.groupby(by)[['n', 'tr1', 'fs', 'fs_x', 'fl', 'tr0', 's0', 'l0', 'mo_all', 'ok1', 'mo_fs', 'n_fs', 'mo_fl', 'n_fl', 'hs', 'gs', 'imp_s']].sum()
    t = pd.DataFrame({'n(M)': g.n / 1e6, 'trd1': 100 * g.tr1 / g.n, 'sh': 100 * g.fs / g.n, 'sh>': 100 * g.fs_x / g.n,
                      'lg': 100 * g.fl / g.n, 'up0': 100 * g.s0 / g.n, 'dn0': 100 * g.l0 / g.n,
                      'moS': 100 * g.mo_fs / g.n_fs, 'moL': 100 * g.mo_fl / g.n_fl, 'drift': 100 * g.mo_all / g.ok1,
                      'hs': 100 * g.hs / g.n, 'gapS': 100 * g.gs / g.fs, 'impS': 100 * g.imp_s / g.fs})
    t['cross'] = t.drift - t.hs; t['rest'] = t.sh / 100 * t.moS
    return t.round(2)


print('== by |delta| (all DTE 1-90, universe days)'); print(tab('delta_b'))
sys_ = D[D.delta_b.isin(['ATM .45-.55', '.30-.45', '.15-.30']) & D.dte_b.isin(['8-14', '15-30', '31-60'])]
print('\n== the systems\' contracts (|delta| .15-.55, DTE 8-60): by DTE'); print(tab('dte_b', sys_))
print('\n== ... by half-spread at t'); print(tab('hs_b', sys_))
print('\n== ... by the contract\'s own volume on t'); print(tab('vol_b', sys_))
print('\n== ... by period'); print(tab('ygrp', sys_))
print('\n== ... calls / puts'); print(tab('r', sys_))
print('\n== ATM only, by DTE x half-spread'); print(tab(['dte_b', 'hs_b'], D[D.delta_b == 'ATM .45-.55']))
