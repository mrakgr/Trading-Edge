"""GH-1 — the high-volume variants' BIGGEST WINNERS AND LOSERS by the first-30-minute volatility (user 2026-11-21).
Reads data/equity/gaphv/events.parquet (scripts/equity/gaphv_events.py). Every trade is the SHORT (the variants' side),
gross, no borrow fee: r = 1 - close / entry; the short's big LOSERS are the long side's big winners.
  09:30 variants: entry = the official open (r_0930 also split into open→10:00 and 10:00→close at the $5k px10)
  10:00 variants: entry = px10 (the $5k VWAP from 10:00)
"Big" = the variant's own top / bottom 10 % of r. volat30 = GapShort.fs's (bp, 30 s slots 09:30–10:00).
⚠ For the 09:30 variants volat30 is NOT known at the entry — it describes the trade's first 30 minutes (an exit / manage
question); for the 10:00 variants it is known at the entry (a gate / sizing question).
    cd research && python3 scripts/equity/gaphv_winners.py > data/equity/gaphv/winners_volat30.md
"""
import numpy as np, pandas as pd

X = pd.read_parquet('data/equity/gaphv/events.parquet')
X['yr'] = pd.to_datetime(X.d).dt.year
NY = (pd.to_datetime(X.d).max() - pd.to_datetime(X.d).min()).days / 365.25
X['r0930'] = 1 - X.close / X.open
X['r1000'] = 1 - X.close / X.px10
X['r_first'] = 1 - X.px10 / X.open            # the 09:30 short's open → 10:00 leg (to the $5k px10)

def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum()
    return np.inf if l == 0 else g / l
def f2(x): return 'inf' if np.isinf(x) else f'{x:.2f}'

V = [('U0930', 'u0930', 'r0930', 'gap-UP ≥ 2 %, rvol_pm ≥ 0.5 — short the open'),
     ('D0930', 'd0930', 'r0930', 'gap-DOWN ≤ −2 %, rvol_pm ≥ 0.25 — short the open'),
     ('U1000', 'u1000', 'r1000', 'gap-UP ≥ 4 %, rvol30 ≥ 1 — short at 10:00 ($5k VWAP)'),
     ('D1000', 'd1000', 'r1000', 'gap-DOWN ≤ −4 %, rvol30 ≥ 1 — short at 10:00 ($5k VWAP)')]

print('# GH-1 — the high-volume variants\' biggest winners and losers by the first-30-minute volatility\n')
print(f'{len(X):,} ticker-days, {NY:.1f} years. Every trade = the SHORT, to the official close, gross. Big winner / loser = '
      'the variant\'s top / bottom 10 % of r.\n')
print('## The variants\n')
print('| variant | n | /yr | PF | mean | median | win % | p10 | p90 | PF 2016–20 / 2021–26 |')
print('|---|---|---|---|---|---|---|---|---|---|')
S = {}
for name, flag, rc, desc in V:
    K = X[X[flag] & np.isfinite(X[rc]) & np.isfinite(X.volat30)].copy()
    K['r'] = K[rc]
    S[name] = K
    r = K.r.values
    print(f'| {name} ({desc}) | {len(K):,} | {len(K) / NY:.0f} | {f2(pf(r))} | {100 * r.mean():+.2f} % | {100 * np.median(r):+.2f} % | '
          f'{100 * (r > 0).mean():.0f} | {100 * np.quantile(r, .1):+.1f} % | {100 * np.quantile(r, .9):+.1f} % | '
          f'{f2(pf(r[K.yr <= 2020]))} / {f2(pf(r[K.yr >= 2021]))} |')
print()

for name, flag, rc, desc in V:
    K = S[name]
    lo, hi = np.quantile(K.r, .1), np.quantile(K.r, .9)
    K['big_w'], K['big_l'] = K.r >= hi, K.r <= lo
    K['q'] = pd.qcut(K.volat30, 10, labels=False)
    print(f'## {name} — {desc}\n')
    print(f'Big winner = r ≥ {100 * hi:+.1f} %, big loser = r ≤ {100 * lo:+.1f} %.\n')
    extra = ' | open→10:00 mean | 10:00→close mean | PF 10:00→close' if rc == 'r0930' else ''
    print('| volat30 decile (bp) | n | median price | PF | mean | win % | big winners % | big losers % | '
          'winner share of the big moves | PF 2016–20 / 2021–26' + extra + ' |')
    print('|---|---|---|---|---|---|---|---|---|---|' + ('---|---|---|' if extra else ''))
    for q, g in K.groupby('q'):
        r = g.r.values
        w, l = g.big_w.sum(), g.big_l.sum()
        row = (f'| {q + 1}: {g.volat30.min():.0f}–{g.volat30.max():.0f} | {len(g):,} | \\${g.open.median():.2f} | {f2(pf(r))} | '
               f'{100 * r.mean():+.2f} % | {100 * (r > 0).mean():.0f} | {100 * w / len(g):.0f} | {100 * l / len(g):.0f} | '
               f'{100 * w / max(w + l, 1):.0f} % | {f2(pf(r[g.yr <= 2020]))} / {f2(pf(r[g.yr >= 2021]))}')
        if extra:
            a, b = g.r_first.values, g.r1000.values
            row += f' | {100 * np.nanmean(a):+.2f} % | {100 * np.nanmean(b):+.2f} % | {f2(pf(b))}'
        print(row + ' |')
    pw, pl, pa = (K.volat30.rank(pct=True)[m].median() for m in (K.big_w, K.big_l, K.r == K.r))
    print(f'\nvolat30 median: big winners {K.volat30[K.big_w].median():.0f} bp, big losers {K.volat30[K.big_l].median():.0f} bp, '
          f'all {K.volat30.median():.0f} bp.\n')
    for lab, m, asc in (('biggest winners', K.big_w, False), ('biggest losers', K.big_l, True)):
        T = K.sort_values('r', ascending=asc).head(15)
        print(f'<details><summary>{name}: the 15 {lab}</summary>\n')
        print('| date | ticker | gap | open | volat30 bp (decile) | ret30 | r |')
        print('|---|---|---|---|---|---|---|')
        for t in T.itertuples():
            print(f'| {t.d} | {t.ticker} | {100 * t.gap:+.1f} % | \\${t.open:.2f} | {t.volat30:.0f} ({t.q + 1}) | {100 * t.ret30:+.1f} % | {100 * t.r:+.1f} % |')
        print('\n</details>\n')
