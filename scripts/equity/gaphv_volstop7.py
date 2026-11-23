"""GH-13h — the 10:00 variants enter ONLY IF the volatility stop has not fired on the stock since 09:30 (user 2026-11-23: "the
10 ET variant should only make an entry if the volatility stop didn't trigger earlier"). Knowable at 10:00: the stop's
history to 10:00.

The stop "fired earlier" = the 09:30-computed stop (GH-13g's o_ columns: ew · any, W = 3, by the schedule P / T1 / T2 / 350)
fired before 10:00:00 — whether or not a 09:30 trade was on. The 10:00 trades kept are managed as ever (GH-6 exit + today's
stop from the 10:00 entry, GH-13d W = 3 · 350). No new pass: data/equity/gaphv/volstop6.parquet + volstop3.parquet.
    cd research && python3 scripts/equity/gaphv_volstop7.py > data/equity/gaphv/volstop7.md
"""
import numpy as np, pandas as pd, sys, io, contextlib
sys.path.insert(0, 'scripts/equity')
with contextlib.redirect_stdout(io.StringIO()):
    from gaphv_exits import X, simulate, pf, f2
    from gaphv_volstop import hm2s
from gaphv_volstop6 import key, nm, INF

SCHED = [(350, 350, 350), (INF, 700, 500), (1000, 700, 500), (INF, 700, 700), (INF, INF, INF)]
X = X.merge(pd.read_parquet('data/equity/gaphv/volstop6.parquet'), on=['ticker', 'd']).merge(
    pd.read_parquet('data/equity/gaphv/volstop3.parquet')[['ticker', 'd', 'e_w3_350_t', 'e_w3_350_x']], on=['ticker', 'd'])
print('# GH-13h — the 10:00 variants skip a stock whose volatility stop fired before 10:00\n')
print('"fired" = the stop computed from 09:30 by the schedule (P before 09:35 · T1 to 09:40 · T2 to 09:45 · 350 after) fired '
      'before 10:00. The 10:00 trades kept: the GH-6 exit + today\'s stop from 10:00. sum = Σ % · worst · CVaR 1 % · PF (halves '
      '2016–2020 / 2021–2026) · n · skipped / yr and their result had they been taken.\n')
for name, flag, rule in (('U1000', 'u1000', 'VWAP'), ('D1000', 'd1000', 'T1530')):
    K = X[X[flag] & np.isfinite(X.x1000) & (X.x1000 > 0)].copy().reset_index(drop=True); K['entry'] = K.x1000
    px, at = simulate(K, rule); base = 1 - px / K.entry.values
    bsec = np.array([57600 if a == 'close' else hm2s(a) for a in at])
    t = K.e_w3_350_t.values; x = K.e_w3_350_x.values; fire = (t > 0) & np.isfinite(x) & (t < bsec)
    r = 100 * np.where(fire, 1 - x / K.entry.values, base)
    h1, h2 = (K.yr <= 2020).values, (K.yr >= 2021).values
    def row(lab, keep):
        rr = r[keep]; s = np.sort(rr); k1 = max(1, len(s) // 100); sk = ~keep
        skipped = f'{sk.sum() / 10.1:.0f} · {r[sk].mean():+.1f} % (Σ {r[sk].sum():+,.0f}, worst {r[sk].min():+.0f} %)' if sk.any() else ''
        return (f'| {lab} | {rr.sum():,.0f} | {s[0]:+.0f} % | {s[:k1].mean():+.1f} % | {f2(pf(rr))} | {f2(pf(r[keep & h1]))} / {f2(pf(r[keep & h2]))} | '
                f'{keep.sum():,} | {skipped} |')
    print(f'## {name} ({rule}), n = {len(K):,}\n')
    print('| skip when the stop fired before 10:00, by | sum | worst | CVaR 1 % | PF | PF halves | n | skipped / yr · their mean (Σ, worst) |')
    print('|---|---|---|---|---|---|---|---|')
    print(row('— (no filter, today)', np.ones(len(K), bool)))
    for p, t1, t2 in SCHED:
        ft = K[key(p, t1, t2) + '_t'].values
        keep = ~((ft > 0) & (ft < 36000))
        lab = 'today\'s stop (350 from 09:30)' if (p, t1, t2) == (350, 350, 350) else f'P {nm(p)} · T1 {nm(t1)} · T2 {nm(t2)}'
        print(row(lab, keep))
    print()
