"""S49al-5 (user 2026-11-04: "Would this be a good short setup then?"): the FlushFader FAILURE short.
Causal: a FlushFader long's resting 5m-high sell fills (S49al's REST60 fill, fill model queue = a touch of the level)
BEFORE the trigger; then a 1s vwap comes back to the long's entry (<= entry_px) before the trigger bar -> SHORT at the
NEXT bar's vwap; cover at the long's model exit (exit_px at exit_sec: the first vwap break above the decayed 5m high,
filled at the next bar's vwap). Gross, no borrow / locate cost. From sim.parquet + the 1s slim bars (raw).
    python3 scripts/equity/flushfader_failure_short.py > logs/s49al5_failure_short.log 2>&1; echo exit $?
"""
import pandas as pd, numpy as np, pyarrow.parquet as pq
c = ['key', 'symbol', 'trade_date', 'year', 'entry_sec', 'exit_sec', 'entry_px', 'exit_px', 't1_REST60|queue']
d = pd.read_parquet('data/equity/flushfader_exit_sim/sim.parquet', columns=c)
d['touch'] = d.exit_sec + d['t1_REST60|queue']
e = d[d['t1_REST60|queue'] < 0]
rows = []
for day, g in e.groupby('trade_date'):
    t = pq.read_table(f'data/intraday_1s_slim/{day}.parquet', columns=['ticker', 'bucket', 'vwap'],
                      filters=[('ticker', 'in', list(g.symbol.unique()))]).to_pandas()
    by = {k: v.sort_values('bucket') for k, v in t.groupby('ticker')}
    for r in g.itertuples():
        b = by[r.symbol]; s = b.bucket.values; w = b.vwap.values
        i0 = np.searchsorted(s, np.floor(r.touch) + 1)          # bars closing after the touch
        i1 = np.searchsorted(s, r.exit_sec - 1)                 # up to the trigger bar (excl.)
        hit = np.flatnonzero(w[i0:i1] <= r.entry_px)
        if not len(hit): continue
        j = i0 + hit[0] + 1                                     # the next bar's vwap = the short's fill
        if j >= len(s) or s[j] >= r.exit_sec: continue
        rows.append(dict(key=r.key, year=r.year, entry_px=r.entry_px, sig_sec=s[j - 1], short_sec=s[j], short_px=w[j],
                         cover_sec=r.exit_sec, cover_px=r.exit_px, touch_age=s[j - 1] - r.touch))
x = pd.DataFrame(rows)
x['ret'] = 1e4 * (x.short_px - x.cover_px) / x.short_px
x['hold_min'] = (x.cover_sec - x.short_sec) / 60
def pf(r): g = r[r > 0].sum(); l = -r[r < 0].sum(); return g / l if l else np.inf
def line(lab, y): return f"| {lab} | {len(y):,} | {y.ret.mean():+.0f} | {y.ret.median():+.0f} | {pf(y.ret):.2f} | {100 * (y.ret > 0).mean():.0f} % | {y.hold_min.median():.0f} |"
print('| slice | trades | mean bp | median bp | PF | win | median hold min |\n|---|---|---|---|---|---|---|')
print(line('all', x))
for yv, y in x.groupby('year'): print(line(str(yv), y))
for lab, y in x.groupby(pd.cut(x.entry_px, [0, 1, 2, 5, 10, 1e9], labels=['< $1', '$1-2', '$2-5', '$5-10', '$10+']), observed=True): print(line(lab, y))
for lab, y in x.groupby(pd.cut(x.touch_age, [-1, 60, 300, 1800, 1e9], labels=['back to entry < 1 min after the touch', '1-5 min', '5-30 min', '> 30 min']), observed=True): print(line(lab, y))
# the cover is a breakout fill (next-bar vwap after the break): the adverse overshoot for a buyer — cover at the level instead?
x.to_parquet('data/equity/flushfader_exit_sim/failure_short.parquet', index=False)
