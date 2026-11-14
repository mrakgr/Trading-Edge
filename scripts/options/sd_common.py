#!/usr/bin/env python3
"""Shared hygiene for the short_dated.py reports (2026-11-14 errata, SH-36).
  drop_bad(T): drops the trades whose contract VANISHED before expiry (hedge_daily.py's flag: a corporate action re-rooted
  it, e.g. WOLF -> WOLF1 2025-09-29, so the spot-intrinsic settle is wrong). STR rows go when either ATM leg vanished.
  Splits are short_dated.py's own filter (fixed 2026-11-14: the split dates were unsorted for searchsorted).
"""
import pandas as pd

HEDGE = 'data/options/short_dated/hedge.parquet'
KEYS = ['root', 'date', 'exp', 'K', 'r', 'inst']


def drop_bad(T, quiet=False):
    H = pd.read_parquet(HEDGE, columns=KEYS + ['vanish'])
    v = H[H.vanish][KEYS].drop_duplicates().assign(_v=True)
    n0 = len(T)
    m = T[KEYS].merge(v, on=KEYS, how='left')._v.eq(True).to_numpy()
    if (T.inst == 'STR').any():
        legs = H[H.vanish & H.inst.isin(['AP', 'AC'])][['root', 'date', 'exp']].drop_duplicates().assign(_s=True)
        ms = T[['root', 'date', 'exp']].merge(legs, on=['root', 'date', 'exp'], how='left')._s.eq(True).to_numpy()
        m = m | ((T.inst == 'STR').to_numpy() & ms)
    T = T[~m]
    if not quiet:
        print(f'[sd_common] dropped {n0 - len(T):,} of {n0:,} trades whose contract vanished before expiry')
    return T
