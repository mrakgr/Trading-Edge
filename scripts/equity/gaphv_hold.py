"""GH-3 — the COUNTERFACTUAL HOLD and the time below the moving averages (user 2026-11-21: "instead of thinking in terms of
stops we should find the area that has the most profit"; "the expectancy of holding winners from 10 to 10:30 ET and every
half hour going forward"; "what % do they spend below their various moving averages"; SMB: "80 % of the time the stock
puts in its high or low in the first hour").
events.parquet + path.parquet (gaphv_path.py). Every trade = the SHORT, gross; every intraday fill = the $5k VWAP from the
mark (x1000 .. x1530); the 09:30 variants enter at the official open, the 10:00 variants at x1000; the last exit = the
official close. Nothing conditions on the final outcome except the descriptive big-winner / big-loser columns.
    cd research && python3 scripts/equity/gaphv_hold.py > data/equity/gaphv/hold.md
"""
import numpy as np, pandas as pd

X = pd.read_parquet('data/equity/gaphv/events.parquet').merge(pd.read_parquet('data/equity/gaphv/path.parquet'), on=['ticker', 'd'])
X['yr'] = pd.to_datetime(X.d).dt.year
MK = ['1000', '1030', '1100', '1130', '1200', '1230', '1300', '1330', '1400', '1430', '1500', '1530']
X['xclose'] = X.close
def lab(m): return 'close' if m == 'close' else f'{m[:2]}:{m[2:]}'

def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum()
    return np.inf if l == 0 else g / l
def f2(x): return 'inf' if np.isinf(x) else f'{x:.2f}'
def cell(r): r = r[np.isfinite(r)]; return f'{f2(pf(r))} · {100 * r.mean():+.2f} %' if len(r) >= 100 else f'— ({len(r)})'

V = [('U0930', 'u0930', 'open', MK), ('D0930', 'd0930', 'open', MK),
     ('U1000', 'u1000', 'x1000', MK[1:]), ('D1000', 'd1000', 'x1000', MK[1:])]
S = {}
for name, flag, ent, mk in V:
    K = X[X[flag] & np.isfinite(X[ent]) & (X[ent] > 0)].copy()
    K['entry'] = K[ent]
    K['r'] = 1 - K.close / K.entry
    lo, hi = np.quantile(K.r, .1), np.quantile(K.r, .9)
    K['big_w'], K['big_l'] = K.r >= hi, K.r <= lo
    S[name] = (K, mk)

print('# GH-3 — the counterfactual hold, the session extremes, and the time below the moving averages\n')
print('Every trade = the SHORT, gross. Intraday fills = the \\$5k VWAP from the mark; the last exit = the official close. '
      'Cells: PF · mean.\n')

print('## 1. Exit at each mark (entry → the mark)\n')
cols = ['1000', '1030', '1100', '1130', '1200', '1300', '1400', '1500', '1530', 'close']
print('| variant | n | ' + ' | '.join(lab(c) for c in cols) + ' |')
print('|---|---|' + '---|' * len(cols))
for name, (K, mk) in S.items():
    row = []
    for c in cols:
        if c != 'close' and c not in mk: row.append(''); continue
        x = K.close if c == 'close' else K[f'x{c}']
        row.append(cell((1 - x / K.entry).values))
    print(f'| {name} | {len(K):,} | ' + ' | '.join(row) + ' |')
print()

print('## 2. The next half hour, by the position at the mark (the short from the mark to the next mark)\n')
print('in profit = the short\'s return from the entry to the mark > 0 (knowable at the mark). Cells: PF · mean of the '
      'half hour [n in profit / losing].\n')
for name, (K, mk) in S.items():
    print(f'### {name}\n')
    print('| half hour | all | in profit at the mark | losing at the mark | in profit by ≥ 5 % | the rest of the day from the mark: in profit / losing |')
    print('|---|---|---|---|---|---|')
    seq = mk + ['close']
    for a, b in zip(seq[:-1], seq[1:]):
        xa = K[f'x{a}']; xb = K.close if b == 'close' else K[f'x{b}']
        seg = (1 - xb / xa).values; rest = (1 - K.close / xa).values
        sofar = (1 - xa / K.entry).values
        w, l = sofar > 0, sofar <= 0
        print(f'| {lab(a)} → {lab(b)} | {cell(seg)} | {cell(seg[w])} [{w.sum():,}] | {cell(seg[l])} [{l.sum():,}] | '
              f'{cell(seg[sofar >= 0.05])} | {cell(rest[w])} / {cell(rest[l])} |')
    print()

print('## 3. When the session high and low are set (SMB: "80 % of the time the high or low is in the first hour")\n')
print('The 1s-vwap session high / low from 09:30 (hi_o_sec / lo_o_sec). "by 10:30" = set before 10:30:00.\n')
print('| variant | high by 10:00 | high by 10:30 | low by 10:00 | low by 10:30 | either by 10:30 | both by 10:30 | the short\'s '
      'best exit (the session low after the entry) by 10:30 / 11:00 / 12:00 / 14:00 / later |')
print('|---|---|---|---|---|---|---|---|')
for name, (K, mk) in S.items():
    h, l = K.hi_o_sec, K.lo_o_sec
    le = K.lo_o_sec if name.endswith('0930') else K.lo_e_sec
    br = [(le < t).mean() for t in (37800, 39600, 43200, 50400)]
    print(f'| {name} | {(h < 36000).mean():.0%} | {(h < 37800).mean():.0%} | {(l < 36000).mean():.0%} | {(l < 37800).mean():.0%} | '
          f'{((h < 37800) | (l < 37800)).mean():.0%} | {((h < 37800) & (l < 37800)).mean():.0%} | '
          + ' / '.join(f'{x:.0%}' for x in br) + f' / {1 - br[-1]:.0%} |')
print()

PER = ['0930', '1000', '1030', '1100', '1200', '1300', '1400', '1500']
PL = ['09:30–10', '10–10:30', '10:30–11', '11–12', '12–13', '13–14', '14–15', '15–16']
print('## 4. The share of each period spent BELOW the moving average (big winners / all / big losers)\n')
print('vwap = the session VWAP; s5 / s30 = the 5- / 30-minute simple average of the 1s price. Clock seconds.\n')
for name, (K, mk) in S.items():
    print(f'### {name}\n')
    print('| average | group | ' + ' | '.join(PL) + ' |')
    print('|---|---|' + '---|' * len(PER))
    for ma in ('vwap', 's5', 's30'):
        for g, m in (('big winners', K.big_w), ('all', K.r == K.r), ('big losers', K.big_l)):
            print(f'| {ma} | {g} | ' + ' | '.join(f'{100 * K[f"b_{ma}_{p}"][m].mean():.0f} %' for p in PER) + ' |')
    print()

print('## 5. Range-bound as a filter — the rest of the day by the previous half hour\'s tape (knowable at the mark)\n')
print('At 10:30 (from the 10:00–10:30 tape) and at 11:00 (from 10:30–11:00): the short\'s return from the mark to the close '
      'by quintile of the share below the VWAP, and of the s15 crossings (more crossings = more range-bound). '
      'Cells: PF · mean.\n')
for name, (K, mk) in S.items():
    print(f'### {name}\n')
    print('| at | feature | Q1 | Q2 | Q3 | Q4 | Q5 |')
    print('|---|---|---|---|---|---|---|')
    for at, p in (('1030', '1000'), ('1100', '1030')):
        rest = (1 - K.close / K[f'x{at}']).values
        for f, fl in ((f'b_vwap_{p}', 'share below VWAP'), (f'b_s30_{p}', 'share below s30'), (f'c_s15_{p}', 's15 crossings')):
            s = K[f]; ok = np.isfinite(s.values) & np.isfinite(rest)
            q = pd.qcut(s[ok].rank(method='first'), 5, labels=False).values
            cells = []
            for i in range(5):
                m = q == i
                cells.append(f'{cell(rest[ok][m])} ({s[ok][m].median():.2f})' if 'share' in fl else f'{cell(rest[ok][m])} ({s[ok][m].median():.0f})')
            print(f'| {lab(at)} | {fl} | ' + ' | '.join(cells) + ' |')
    print()
