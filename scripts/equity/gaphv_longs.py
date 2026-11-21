"""GH-4 — the high-volume variants' big LOSERS as LONG trades (user 2026-11-21: "we should profile the losers as they could
be good long trades. We already have some long setups in gap up that hold the open. We should look into what they have in
common"). events + features + path parquets. Every trade here = the LONG, the same entry / exit as the variant's short
(09:30 variants: the official open → the close; 10:00 variants: the $5k VWAP from 10:00 → the close); gross and net of
0.7c / share (GF's swing cost). Big long winner = the variant's top 10 % of the long's r (= the short's big losers).
The existing long setup (GF-42, docs/gapfader_results.md): gap-up >= 4 %, HELD the open at 10:00, CALM (volat30 < 40 bp),
rvol30 >= 1, bought at 10:00, out at the close — net 1.29 there (rvol30 on avgvol20_prior; here on the 60-session median,
so U1000 is its superset).
    cd research && python3 scripts/equity/gaphv_longs.py > data/equity/gaphv/longs.md
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
def cell(K):
    if len(K) < 50: return f'— ({len(K)})'
    a, b = K[K.yr <= 2020], K[K.yr >= 2021]
    return (f'{len(K) / NY:.0f}/yr · {f2(pf(K.r.values))} / net {f2(pf(K.rn.values))} · {100 * K.r.mean():+.2f} % '
            f'({f2(pf(a.rn.values))} / {f2(pf(b.rn.values))})')

PRE = [('calm premarket (pm volat < 30 bp)', lambda K: K.pm_volat < 30), ('light premarket (rvol_pm < 0.5)', lambda K: K.rvpm < 0.5),
       ('heavy premarket (rvol_pm ≥ 1)', lambda K: K.rvpm >= 1), ('rising into the open (pm trend > 0)', lambda K: K.pm_trend > 0),
       ('near the premarket high (pm pos ≥ 0.7)', lambda K: K.pm_pos >= 0.7), ('price ≥ \\$10', lambda K: K.open >= 10),
       ('dv60_med ≥ \\$5M', lambda K: K.dv60 >= 5e6), ('gap 6–20 %', lambda K: K.gap.abs().between(0.06, 0.2))]
AT10 = [('CALM (volat30 < 40 bp)', lambda K: K.volat30 < 40), ('HELD the open at 10:00 (ret30 ≥ 0)', lambda K: K.ret30 >= 0),
        ('near the 30 m high at 10:00 (pos30 ≥ 0.7)', lambda K: K.pos30 >= 0.7),
        ('mostly ABOVE the VWAP 09:30–10:00 (< 50 % below)', lambda K: K.b_vwap_0930 < 0.5),
        ('$ fading (inten30 < 0.82)', lambda K: K.inten30 < 0.82), ('rvol30 ≥ 2', lambda K: K.rv30 >= 2),
        ('the 30 m high late (≥ 20 min)', lambda K: K.hi30_min >= 20), ('retail share 30 m ≥ 0.2', lambda K: K.retail30 >= 0.2)]
POST = [('ABOVE the VWAP 10:00–10:30 (< 50 % below)', lambda K: K.b_vwap_1000 < 0.5)]
V = [('U0930', 'u0930', 'open', PRE, AT10), ('D0930', 'd0930', 'open', PRE, AT10),
     ('U1000', 'u1000', 'x1000', PRE + AT10, POST), ('D1000', 'd1000', 'x1000', PRE + AT10, POST)]

print('# GH-4 — the big LOSERS of the high-volume shorts as LONG trades\n')
print('Every trade = the LONG on the variant\'s own entry and exit (09:30 variants: open → close; 10:00 variants: the \\$5k '
      'VWAP from 10:00 → close). Cells: trades/yr · PF gross / net of 0.7c a share · mean gross (net PF 2016–20 / 2021–26). '
      'Big long winner = the variant\'s top 10 % of the long.\n')
S = {}
for name, flag, ent, known, after in V:
    K = X[X[flag] & np.isfinite(X[ent]) & (X[ent] > 0)].copy()
    K['entry'] = K[ent]; K['r'] = K.close / K.entry - 1; K['rn'] = K.r - 0.007 / K.entry
    K['big'] = K.r >= np.quantile(K.r, .9)
    S[name] = K
    print(f'## {name} — the long: {cell(K)}; big long winner = r ≥ {100 * np.quantile(K.r, .9):+.1f} %\n')
    print('| trait | known | share of the big long winners | share of all | lift | the long WITH the trait | WITHOUT |')
    print('|---|---|---|---|---|---|---|')
    for grp, tag in ((known, 'at the entry'), (after, 'after the entry')):
        for lab, f in grp:
            m = f(K).fillna(False).values
            a, b = m[K.big.values].mean(), m.mean()
            print(f'| {lab} | {tag} | {100 * a:.0f} % | {100 * b:.0f} % | {a / b if b > 0 else np.nan:.2f} | {cell(K[m])} | {cell(K[~m])} |')
    print()

print('## The existing long setup inside U1000 — GF-42\'s cell (calm · held), and what the big long winners outside it look like\n')
K = S['U1000']
calm, held = (K.volat30 < 40).values, (K.ret30 >= 0).values
cellm = calm & held
print(f'GF-42 cell (calm · held · rvol30 ≥ 1): {cell(K[cellm])}; it holds {100 * K.big[cellm].sum() / K.big.sum():.0f} % of '
      f'U1000\'s big long winners ({K.big[cellm].sum()} of {K.big.sum()}).\n')
print('| volat30 band | held at 10:00 | failed at 10:00 |')
print('|---|---|---|')
for lab, lo, hi in (('< 40 bp (calm)', 0, 40), ('40–60', 40, 60), ('60–80', 60, 80), ('80–120', 80, 120), ('≥ 120', 120, 1e9)):
    v = (K.volat30 >= lo) & (K.volat30 < hi)
    print(f'| {lab} | {cell(K[v & (K.ret30 >= 0)])} | {cell(K[v & (K.ret30 < 0)])} |')
print()
print('Calm · held, by the premarket and the 30-minute position (net PF grades):\n')
print('| | all | rvol_pm < 0.5 | rvol_pm ≥ 0.5 | price ≥ \\$10 | price < \\$10 | pos30 ≥ 0.7 | pos30 < 0.7 | above the VWAP 09:30–10 |')
print('|---|---|---|---|---|---|---|---|---|')
C = K[cellm]
print('| calm · held | ' + ' | '.join(cell(C[m]) for m in (C.r == C.r, C.rvpm < 0.5, C.rvpm >= 0.5, C.open >= 10, C.open < 10,
                                                        C.pos30 >= 0.7, C.pos30 < 0.7, C.b_vwap_0930 < 0.5)) + ' |')
print()
print('## When the longs make their money (U1000, calm · held): exit at each mark\n')
MK = ['1030', '1100', '1200', '1300', '1400', '1500', '1530']
print('| | ' + ' | '.join(f'{m[:2]}:{m[2:]}' for m in MK) + ' | close |')
print('|---|' + '---|' * (len(MK) + 1))
for lab, D in (('calm · held', C), ('U1000 all', K)):
    cs = [f'{f2(pf((D[f"x{m}"] / D.entry - 1).values))} · {100 * (D[f"x{m}"] / D.entry - 1).mean():+.2f} %' for m in MK]
    print(f'| {lab} | ' + ' | '.join(cs) + f' | {f2(pf(D.r.values))} · {100 * D.r.mean():+.2f} % |')
print()
