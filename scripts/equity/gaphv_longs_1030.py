"""GH-4b — the KNOWABLE long entries on the high-volume variants' names (user 2026-11-21, the losers as longs): bought at
10:00 (x1000) on the held / calm names, and at 10:30 (x1030) on the names that spent 10:00–10:30 ABOVE their VWAP — out at
the close. The LONG, gross / net of 0.7c a share. Every variant's names (U0930/D0930 too: their 10:00 tape is known then).
    cd research && python3 scripts/equity/gaphv_longs_1030.py > data/equity/gaphv/longs_1030.md
"""
import numpy as np, pandas as pd
X = (pd.read_parquet('data/equity/gaphv/events.parquet').merge(pd.read_parquet('data/equity/gaphv/features.parquet'), on=['ticker', 'd'])
     .merge(pd.read_parquet('data/equity/gaphv/path.parquet'), on=['ticker', 'd']))
X['yr'] = pd.to_datetime(X.d).dt.year
NY = (pd.to_datetime(X.d).max() - pd.to_datetime(X.d).min()).days / 365.25
def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum()
    return np.inf if l == 0 else g / l
def f2(x): return 'inf' if np.isinf(x) else f'{x:.2f}'
def cell(K, e):
    K = K[np.isfinite(K[e]) & (K[e] > 0)]
    if len(K) < 50: return f'— ({len(K)})'
    r = K.close / K[e] - 1; rn = r - 0.007 / K[e]
    return (f'{len(K) / NY:.0f}/yr · {f2(pf(r.values))} / {f2(pf(rn.values))} · {100 * r.mean():+.2f} % '
            f'({f2(pf(rn[K.yr <= 2020].values))} / {f2(pf(rn[K.yr >= 2021].values))})')
print('# GH-4b — knowable long entries on the high-volume names (the LONG to the close)\n')
print('Cells: trades/yr · PF gross / net of 0.7c · mean gross (net 2016–20 / 2021–26). held = ret30 ≥ 0; calm = volat30 < 40 bp; '
      'above = < 50 % of 10:00–10:30 below the session VWAP.\n')
print('| names | bought 10:00: all | 10:00: held | 10:00: calm · held | 10:30: all | 10:30: above the VWAP 10–10:30 | 10:30: above · held | 10:30: above · calm | 10:30: below |')
print('|---|---|---|---|---|---|---|---|---|')
for name, flag in (('U0930', 'u0930'), ('D0930', 'd0930'), ('U1000', 'u1000'), ('D1000', 'd1000')):
    K = X[X[flag]]
    held, calm, above = K.ret30 >= 0, K.volat30 < 40, K.b_vwap_1000 < 0.5
    print(f'| {name} | ' + ' | '.join([cell(K, 'x1000'), cell(K[held], 'x1000'), cell(K[held & calm], 'x1000'), cell(K, 'x1030'),
                                       cell(K[above], 'x1030'), cell(K[above & held], 'x1030'), cell(K[above & calm], 'x1030'),
                                       cell(K[~above], 'x1030')]) + ' |')
