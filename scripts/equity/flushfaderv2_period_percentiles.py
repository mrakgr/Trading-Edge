"""V2-26 — FlushFader (V2, the production book) WEEK BY WEEK and MONTH BY MONTH: the percentiles of the period results
(user 2026-11-05: "how the new FlushFader system does on a monthly and weekly basis ... look into specific percentiles").

The production reference book (data/flushfaderv2/flushfaderv2_reference_book.parquet = V2-25 with the CUSUM level; the
same 35,767 trades without the CUSUM: data/flushfaderv2/v24_nocus/), every trade at L2's production size: units =
clip(PF−1, 0.125, 2), one unit = 15 % of equity, so a trade's result in % of equity = units × 15 % × r, r = ret_exit +
2 × $0.001 / entry_px (the maker credit, research's "net of credit"; no commission, slippage or borrow). Periods by the
ENTRY date; a week / month with no trade counts as 0. No compounding, no concurrency cap (L2's margin budget is not
modelled) — the book's own edge, period by period.
    cd research && python3 scripts/equity/flushfaderv2_period_percentiles.py > data/flushfaderv2/period_percentiles.md
"""
import numpy as np, pandas as pd

UNIT = 0.15
def load(p):
    b = pd.read_parquet(p, columns=['symbol', 'trade_date', 'signal_sec', 'ret_exit', 'entry_px', 'pf1_fitted'])
    b['r'] = b.ret_exit + 2 * 0.001 / b.entry_px
    b['units'] = b.pf1_fitted.clip(0.125, 2.0)
    b['pnl'] = 100 * UNIT * b.units * b.r                       # % of equity
    b['d'] = pd.to_datetime(b.trade_date)
    return b
new = load('data/flushfaderv2/flushfaderv2_reference_book.parquet')
old = load('data/flushfaderv2/v24_nocus/flushfaderv2_reference_book.parquet')

def periods(b, freq):
    s = b.set_index('d').pnl.resample(freq).sum()                # every period in the span, empty = 0
    return s
Q = [1, 5, 10, 25, 50, 75, 90, 95, 99]
def row(s, label):
    q = np.percentile(s.values, Q)
    return (f"| {label} | {len(s)} | {100 * (s > 0).mean():.0f} % | {s.mean():+.2f} | " + ' | '.join(f'{x:+.2f}' for x in q)
            + f" | {s.min():+.2f} ({s.idxmin():%Y-%m-%d}) | {s.max():+.2f} |")
HDR = '| model | periods | positive | mean | ' + ' | '.join(f'p{q}' for q in Q) + ' | worst | best |\n|---|---|---|---|' + '---|' * len(Q) + '---|---|'

print('# V2-26 — FlushFader week by week and month by month: the percentiles\n')
print(f'The production book, {len(new):,} trades, {new.d.min():%Y-%m-%d} .. {new.d.max():%Y-%m-%d}; % of equity at L2\'s production '
      'size (units = clip(PF−1, 0.125, 2) × 15 %); net of the maker credit, before commission / slippage / borrow; no '
      'compounding, no concurrency cap. Weeks end on Friday, by the entry date.\n')
for name, freq in (('WEEKLY', 'W-FRI'), ('MONTHLY', 'ME')):
    for span, lo in (('2020-01 .. 2026-08', '2020-01-01'), ('2023-01 .. 2026-08', '2023-01-01')):
        print(f'## {name}, {span} (% of equity)\n'); print(HDR)
        for lab, b in (('V2-25 (CUSUM)', new), ('without the CUSUM', old)):
            s = periods(b[b.d >= lo], freq); print(row(s, lab))
        print()
# the losing streaks and the year table of months
s = periods(new, 'ME')
print('## The months, by year (V2-25, % of equity)\n')
t = s.to_frame('pnl'); t['y'] = t.index.year; t['m'] = t.index.month
print('| year | ' + ' | '.join(f'{m:02d}' for m in range(1, 13)) + ' | year | losing months |')
print('|---|' + '---|' * 14)
for y, g in t.groupby('y'):
    cells = {int(m): v for m, v in zip(g.m, g.pnl)}
    print(f'| {y} | ' + ' | '.join(f'{cells[m]:+.1f}' if m in cells else '' for m in range(1, 13)) + f' | {g.pnl.sum():+.1f} | {(g.pnl < 0).sum()} |')
w = periods(new, 'W-FRI')
def streak(x):
    best = cur = 0
    for v in x:
        cur = cur + 1 if v < 0 else 0; best = max(best, cur)
    return best
print(f'\nLongest losing streak: {streak(w.values)} weeks, {streak(s.values)} months. Worst 4-week run: '
      f'{w.rolling(4).sum().min():+.2f} %; worst 3-month run: {s.rolling(3).sum().min():+.2f} %.')
