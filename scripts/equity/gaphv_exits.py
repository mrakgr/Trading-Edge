"""GH-6 — the EXIT RULES as whole trades (user 2026-11-21: "let's try out the rules for getting out of trades ... I am curious
how those would cut the worst-case losses. If they can do that without reducing net profit or the profit factor much, they
would be well worth it"). The rules are GH-3's (docs/gaphv_results.md), fixed BEFORE this run:
  HOLD       the variant as researched: out at the official close
  T1530      out at 15:30 (the $5k VWAP from 15:30)
  VWAP       at every check 10:30 .. 15:00: out when the PREVIOUS half hour spent < 50 % of its clock seconds below the
             session VWAP (mostly above it: the short's tape turned up); else out at 15:30
  VWAP10     VWAP plus a 10:00 check on 09:30–10:00 (the 09:30 variants only)
  LOSER      at every check 11:00 .. 15:00: out when the short is losing (the last price before the mark > the entry);
             else out at 15:30
  BOTH       VWAP or LOSER (the first check that fires);  BOTH10 = VWAP10 or LOSER
Decisions use only what is known before the mark (vwap30.parquet: v30_* and p_*); the fill is the $5k VWAP from the mark
(path.parquet x<hhmm>; a mark with no fill is skipped — the position stays). The short, gross, one unit per trade.
Worst-case measures: the worst trade, the 1st percentile, CVaR 1 % / 5 % (the mean of the worst 1 % / 5 %), and the max
drawdown of the cumulative sum of the trades' % returns by date (one unit each).
    cd research && python3 scripts/equity/gaphv_exits.py > data/equity/gaphv/exits.md
"""
import numpy as np, pandas as pd

X = (pd.read_parquet('data/equity/gaphv/events.parquet').merge(pd.read_parquet('data/equity/gaphv/path.parquet'), on=['ticker', 'd'])
     .merge(pd.read_parquet('data/equity/gaphv/vwap30.parquet'), on=['ticker', 'd']))
X['yr'] = pd.to_datetime(X.d).dt.year
CHK = ['1030', '1100', '1130', '1200', '1230', '1300', '1330', '1400', '1430', '1500']
PREV = {'1000': '0930', '1030': '1000', '1100': '1030', '1130': '1100', '1200': '1130', '1230': '1200', '1300': '1230',
        '1330': '1300', '1400': '1330', '1430': '1400', '1500': '1430'}

def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum()
    return np.inf if l == 0 else g / l
def f2(x): return 'inf' if np.isinf(x) else f'{x:.2f}'

def simulate(K, rule):
    """the exit price and the mark it left at, per trade"""
    n = len(K); px = np.full(n, np.nan); at = np.array(['close'] * n, dtype=object)
    if rule == 'HOLD':
        return K.close.values, at
    done = np.zeros(n, bool)
    checks = (['1000'] if rule.endswith('10') else []) + CHK
    for t in checks:
        fire = np.zeros(n, bool)
        if rule.startswith('VWAP') or rule.startswith('BOTH'):
            fire |= (K[f'v30_{PREV[t]}'] < 0.5).fillna(False).values
        if (rule == 'LOSER' or rule.startswith('BOTH')) and t >= '1100':
            fire |= (K[f'p_{t}'] > K.entry).fillna(False).values
        x = K[f'x{t}'].values
        go = fire & ~done & np.isfinite(x)
        px[go] = x[go]; at[go] = t; done |= go
    last = K.x1530.values
    rest = ~done
    px[rest] = np.where(np.isfinite(last[rest]), last[rest], K.close.values[rest]); at[rest] = '1530'
    return px, at

def stats(K, r, ref=None):
    r = 100 * r
    s = np.sort(r); k1, k5 = max(1, len(s) // 100), max(1, len(s) // 20)
    cum = pd.Series(r).groupby(K.d.values).sum().sort_index().cumsum()
    dd = (cum.cummax() - cum).max()
    a, b = r[(K.yr <= 2020).values], r[(K.yr >= 2021).values]
    return (f'{f2(pf(r))} | {r.mean():+.2f} % | {r.sum():,.0f} | {(r > 0).mean():.0%} | {s[0]:+.0f} % | {s[k1 - 1]:+.1f} % | '
            f'{s[:k1].mean():+.1f} % | {s[:k5].mean():+.1f} % | {dd:,.0f} | {f2(pf(a))} / {f2(pf(b))}')

V = [('U0930', 'u0930', 'open', ['HOLD', 'T1530', 'VWAP', 'VWAP10', 'LOSER', 'BOTH', 'BOTH10']),
     ('U1000', 'u1000', 'x1000', ['HOLD', 'T1530', 'VWAP', 'LOSER', 'BOTH']),
     ('D0930', 'd0930', 'open', ['HOLD', 'T1530', 'VWAP', 'VWAP10', 'LOSER', 'BOTH', 'BOTH10']),
     ('D1000', 'd1000', 'x1000', ['HOLD', 'T1530', 'VWAP', 'LOSER', 'BOTH'])]
print('# GH-6 — the exit rules as whole trades: the worst-case losses vs the profit\n')
print('The short, gross, one unit per trade (sum = Σ % returns). Rules fixed in GH-3 before this run (see the script). '
      'max DD = the max drawdown of the cumulative Σ % by date. PF halves = 2016–20 / 2021–26.\n')
for name, flag, ent, rules in V:
    K = X[X[flag] & np.isfinite(X[ent]) & (X[ent] > 0)].copy().reset_index(drop=True)
    K['entry'] = K[ent]
    hold = 1 - K.close.values / K.entry.values
    print(f'## {name} (n {len(K):,})\n')
    print('| rule | PF | mean | sum | win % | worst | p1 | CVaR 1 % | CVaR 5 % | max DD | PF halves | exited early | Δ on the early exits (vs HOLD) |')
    print('|---|---|---|---|---|---|---|---|---|---|---|---|---|')
    for rule in rules:
        px, at = simulate(K, rule)
        r = 1 - px / K.entry.values
        early = at != 'close' if rule == 'HOLD' else np.isin(at, ['1000'] + CHK)
        dl = 100 * (r[early] - hold[early]).mean() if early.any() and rule != 'HOLD' else np.nan
        print(f'| {rule} | {stats(K, r)} | {early.mean():.0%} | {dl:+.2f} % |' if rule != 'HOLD' else f'| {rule} | {stats(K, r)} | — | — |')
    print()
    # where BOTH exits, and what those trades did
    px, at = simulate(K, 'BOTH10' if 'BOTH10' in rules else 'BOTH')
    r = 1 - px / K.entry.values
    print(f'{"BOTH10" if "BOTH10" in rules else "BOTH"} — the exits by mark (share of trades · mean rule r · mean HOLD r of the same trades):\n')
    print('| mark | ' + ' | '.join(f'{m[:2]}:{m[2:]}' for m in (['1000'] if 'BOTH10' in rules else []) + CHK + ['1530']) + ' |')
    print('|---|' + '---|' * (len(CHK) + 1 + ('BOTH10' in rules)) )
    cs = []
    for m in (['1000'] if 'BOTH10' in rules else []) + CHK + ['1530']:
        w = at == m
        cs.append(f'{w.mean():.0%} · {100 * r[w].mean():+.1f} / {100 * hold[w].mean():+.1f}' if w.any() else '')
    print('| exits | ' + ' | '.join(cs) + ' |\n')
