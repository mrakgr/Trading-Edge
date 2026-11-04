"""S49am (user 2026-11-04): "does the current FlushFader system have an edge in shorting after the exit signal with a 5m
low as target?" (the older, high-PF variants did). On the reference book's TARGET exits (S49al's 37,200):
  SHORT at the long's exit fill (exit_px at exit_sec: the next bar's vwap after the 5m-high break) — the same event;
  COVER when a later 1s vwap is strictly below the min of the prior 300 PRESENT bars' vwaps (the mirror of the long's
  target), at the NEXT bar's vwap; if it never comes, at the session's last RTH bar's vwap (flagged 'close').
Bars = the 1s slim corpus (raw; its vwaps equal the book's prices exactly). Gross: no borrow / locate, no fees.
    python3 scripts/equity/flushfader_exit_short.py > logs/s49am_exit_short.log 2>&1; echo exit $?
"""
import pandas as pd, numpy as np, pyarrow.parquet as pq, sys
sys.path.insert(0, 'scripts/equity')
d = pd.read_parquet('data/equity/flushfader_exit_sim/sim.parquet', columns=['key', 'symbol', 'trade_date', 'year', 'entry_sec', 'exit_sec', 'entry_px', 'exit_px'])
CLOSE_EARLY = set()       # early-close days: the last bar is whatever the corpus holds before 16:00; RTH = 34200 .. 57599
rows = []
for day, g in d.groupby('trade_date'):
    t = pq.read_table(f'data/intraday_1s_slim/{day}.parquet', columns=['ticker', 'bucket', 'vwap'],
                      filters=[('ticker', 'in', list(g.symbol.unique()))]).to_pandas()
    t = t[(t.bucket >= 34200) & (t.bucket < 57600)]
    by = {k: v.sort_values('bucket') for k, v in t.groupby('ticker')}
    for r in g.itertuples():
        b = by[r.symbol]; s = b.bucket.values; w = b.vwap.values.astype(np.float32).astype(np.float64)
        ie = int(np.searchsorted(s, r.exit_sec))
        if ie >= len(s) or s[ie] != r.exit_sec: continue
        pm = pd.Series(w).rolling(300, min_periods=1).min().shift(1).values      # strictly-prior 300-present-bar min
        hit = np.flatnonzero(w[ie + 1:] < pm[ie + 1:])
        if len(hit) and ie + 1 + hit[0] + 1 < len(s):
            k = ie + 1 + hit[0] + 1; how = 'target'
        else:
            k = len(s) - 1; how = 'close'
        rows.append(dict(key=r.key, year=r.year, entry_px=r.entry_px, long_ret=1e4 * (r.exit_px / r.entry_px - 1),
                         short_px=r.exit_px, short_sec=r.exit_sec, cover_sec=s[k], cover_px=w[k], how=how,
                         lvl=pm[ie + 1] if ie + 1 < len(s) else np.nan))
x = pd.DataFrame(rows)
x['ret'] = 1e4 * (x.short_px - x.cover_px) / x.short_px
x['hold_min'] = (x.cover_sec - x.short_sec) / 60
x['tgt_dist'] = 1e4 * (x.short_px / x.lvl - 1)         # how far above the 5m low the short starts
def pf(r): g = r[r > 0].sum(); l = -r[r < 0].sum(); return g / l if l else np.inf
def line(lab, y): return (f"| {lab} | {len(y):,} | {y.ret.mean():+.1f} | {y.ret.median():+.1f} | {pf(y.ret):.3f} | {100 * (y.ret > 0).mean():.0f} % | "
                          f"{y.hold_min.median():.1f} | {100 * (y.how == 'close').mean():.1f} % |")
H = '| slice | trades | mean bp | median bp | PF | win | median hold min | to the close |\n|---|---|---|---|---|---|---|---|'
print(H); print(line('all', x))
print('\nBy year'); print(H)
for yv, y in x.groupby('year'): print(line(str(yv), y))
print('\nBy entry price'); print(H)
for lab, y in x.groupby(pd.cut(x.entry_px, [0, 1, 2, 5, 10, 20, 1e9], labels=['< $1', '$1-2', '$2-5', '$5-10', '$10-20', '$20+']), observed=True): print(line(lab, y))
print('\nBy the distance from the short entry down to the 5m low (the target), bp'); print(H)
for lab, y in x.groupby(pd.qcut(x.tgt_dist, 5)): print(line(str(lab), y))
print('\nBy the long trip\'s own return (entry -> exit), bp'); print(H)
for lab, y in x.groupby(pd.cut(x.long_ret, [-1e9, 0, 100, 300, 1e9], labels=['long lost', '0-100', '100-300', '> 300']), observed=True): print(line(lab, y))
x.to_parquet('data/equity/flushfader_exit_sim/exit_short.parquet', index=False)
