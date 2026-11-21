"""GH-7 — a higher 60-session MEDIAN dollar-volume floor against the untradeable tails (user 2026-11-21, after PGHL / ZJYL:
"Could we filter out such trades using a 60d median volume filter?"). Each variant on its GH-6 production exit (U0930
LOSER, U1000 VWAP, D0930 / D1000 T1530), the short, gross. dv60 = the median of the 60 prior sessions' close × volume
(GapFader's dv60_med; ≥ $500k is every variant's floor already). Also the 60-session median SHARE volume x price at D's
open (a split-robust alternative) is printed for the worst trades.
    cd research && python3 scripts/equity/gaphv_floor.py > data/equity/gaphv/floor.md
"""
import sys, numpy as np, pandas as pd
sys.path.insert(0, 'scripts/equity')
import io, contextlib
with contextlib.redirect_stdout(io.StringIO()):          # gaphv_exits prints its GH-6 report on import
    from gaphv_exits import X, simulate, pf, f2

V = [('U0930', 'u0930', 'open', 'LOSER'), ('U1000', 'u1000', 'x1000', 'VWAP'), ('D0930', 'd0930', 'open', 'T1530'), ('D1000', 'd1000', 'x1000', 'T1530')]
FL = [5e5, 1e6, 2e6, 5e6, 1e7]
print('# GH-7 — the dv60_med floor against the untradeable tails\n')
print('Each variant on its GH-6 exit; the short, gross, one unit a trade. CVaR = the mean of the worst 1 % / 5 %.\n')
ALL = []
for name, flag, ent, rule in V:
    K = X[X[flag] & np.isfinite(X[ent]) & (X[ent] > 0)].copy().reset_index(drop=True)
    K['entry'] = K[ent]
    px, at = simulate(K, rule)
    K['r'] = 100 * (1 - px / K.entry.values); K['v'] = name
    ALL.append(K)
    print(f'## {name} ({rule})\n')
    print('| dv60_med floor | n / yr | PF | mean | sum | worst | CVaR 1 % | CVaR 5 % | PF 2016–20 / 2021–26 |')
    print('|---|---|---|---|---|---|---|---|---|')
    for fl in FL:
        g = K[K.dv60 >= fl]; s = np.sort(g.r.values); k1, k5 = max(1, len(s) // 100), max(1, len(s) // 20)
        print(f'| \\${fl / 1e6:g}M | {len(g) / 10.1:.0f} | {f2(pf(g.r.values))} | {g.r.mean():+.2f} % | {g.r.sum():,.0f} | {s[0]:+.0f} % | '
              f'{s[:k1].mean():+.1f} % | {s[:k5].mean():+.1f} % | {f2(pf(g.r[g.yr <= 2020].values))} / {f2(pf(g.r[g.yr >= 2021].values))} |')
    print()
A = pd.concat(ALL)
print('## The 20 worst trades across the four variants (on their exits)\n')
print('| variant | date | ticker | r | dv60_med | open | rvol_pm | rv30 | gap |')
print('|---|---|---|---|---|---|---|---|---|')
for t in A.sort_values('r').drop_duplicates(['ticker', 'd']).head(20).itertuples():
    print(f'| {t.v} | {t.d} | {t.ticker} | {t.r:+.0f} % | \\${t.dv60 / 1e6:.2f}M | \\${t.open:.2f} | {t.rvpm:.1f} | {t.rv30:.1f} | {100 * t.gap:+.0f} % |')
print('\nThe dv60_med distribution: the worst 1 % of trades vs all (per variant, median / p25):\n')
for name, g in A.groupby('v'):
    w = g[g.r <= np.quantile(g.r, .01)]
    print(f'- {name}: worst 1 % median \\${w.dv60.median() / 1e6:.2f}M (p25 \\${w.dv60.quantile(.25) / 1e6:.2f}M) vs all \\${g.dv60.median() / 1e6:.2f}M (p25 \\${g.dv60.quantile(.25) / 1e6:.2f}M)')
