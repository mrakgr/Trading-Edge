#!/usr/bin/env python3
"""Report: SHORT WING (skew_vert.py -> wings.parquet). Sell the ~25 / ~15-delta wing, buy a ~5-delta cap farther out, both
held to expiry, delta-hedged in stock (daily at the close at the entry IVs, or static at entry; stock costs ignored).
P&L per share = (mid_w - f hs_w) - (mid_c + f hs_c) - (pay_w - pay_c) + hedge.  Shown as % of the net credit at mid
('% cr') and as % of the max loss of the unhedged position = width - net credit ('% risk').
    cd research && python3 scripts/options/skew_wing_report.py > logs/options_skew_wing_report.log
"""
import sys, numpy as np, pandas as pd
W = pd.read_parquet(sys.argv[1] if len(sys.argv) > 1 else 'data/options/skew_vert/wings.parquet')
W = W[~W.split_in_life & ~W.earn_in_life & (W.mid_w - W.mid_c > 0.02)].copy()
W['own_z'] = np.where(W.side == 'C', W.call_skew_z, W.put_skew_z)
W['cr'] = W.mid_w - W.mid_c
W['risk'] = W.width - W.cr
W = W[W.risk > 0]
for f in [0, .25, .5]:
    tg = int(f * 100); base = W.cr - f * (W.hs_w + W.hs_c) - (W.pay_w - W.pay_c)
    W[f'u{tg}'] = base / W.cr; W[f'd{tg}'] = (base + W.hedge_daily) / W.cr; W[f's{tg}'] = (base + W.hedge_static) / W.cr
    W[f'dr{tg}'] = (base + W.hedge_daily) / W.risk
W['gate'] = (W.iv_atm_long < W.rv20) & (W.iv_w > W.rv20)
W['wing_rv'] = W.iv_w / W.rv20


def t(x, c):
    w = x.groupby('E')[c].mean(); return w.mean() / w.std() * np.sqrt(len(w))


def st(x, lab):
    if len(x) < 20: print(f'  {lab:28s} n={len(x)}'); return
    yrs = ' '.join(f'{100 * g.d25.mean():+.0f}' for y, g in x.groupby(x.t0.dt.year) if y > 2022)
    print(f'  {lab:28s} n {len(x):6d}  % cr: unhedged mid {100 * x.u0.mean():+6.1f} | daily-hedged mid {100 * x.d0.mean():+6.1f} '
          f'f25 {100 * x.d25.mean():+6.1f} (t {t(x, "d25"):5.2f}) f50 {100 * x.d50.mean():+6.1f} | static f25 {100 * x.s25.mean():+6.1f} '
          f'| win {100 * (x.d25 > 0).mean():3.0f}%  worst {100 * x.d25.min():+6.0f}  '
          f'cost/cr {100 * ((x.hs_w + x.hs_c) / x.cr).median():3.0f}%  yrs {yrs}')


print(f'short-wing trades {len(W):,}, roots {W.root.nunique()}, {W.t0.min().date()}..{W.t0.max().date()}; median credit/width '
      f'{(W.cr / W.width).median():.3f}')
for leg in ['p25', 'p15', 'c25', 'c15']:
    x = W[W.leg == leg]; print(f'\n== {leg}')
    st(x, 'all')
    st(x[x.own_z <= -1.5], 'skew z <= -1.5')
    st(x[x.own_z <= -1], 'skew z <= -1')
    st(x[x.own_z >= 1.2], 'skew z >= 1.2 (flat)')
    st(x[x.gate], 'ATM < RV20 < wing')
    st(x[x.gate & (x.own_z <= -1)], 'gate & z <= -1')
    st(x[x.wing_rv >= x.wing_rv.quantile(.9)], f'wing IV/RV20 top 10% (>= {x.wing_rv.quantile(.9):.2f})')
    x2 = x.dropna(subset=['own_z']).copy(); x2['dz'] = pd.qcut(x2.own_z.rank(method='first'), 5, labels=False) + 1
    for q, g in x2.groupby('dz'): st(g, f'z quintile {q} [{g.own_z.min():+.1f},{g.own_z.max():+.1f}]')
    liq = x[(x.hs_w + x.hs_c) / x.cr <= 0.15]
    st(liq[liq.own_z <= -1], 'z <= -1 & cost/cr <= 15%')
    st(liq[liq.gate], 'gate & cost/cr <= 15%')
