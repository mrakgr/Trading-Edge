"""FlushFader with its gates PEELED OFF (user 2026-11-07: "keep the >= 40bps volatility, and the gap count ... remove ...
and then see if the number of big losers really is higher than usual" — the gates could be overfit; a simpler system is a
more honest view of the market). Gates-off base slices (gate review tool --build) of base_v24 (2020-01..2026-08-21) and the
08-24..11-06 base run; each layer = a mask, then greedy mc=1 per ticker-day inside it, every trade at ONE equal unit.
Run from research/ (slices = flushfader_gate_review.py --build --trips <base> --slice <out>):
    python3 -u scripts/equity/flushfaderv2_gate_ladder.py --hist-slice H --win-slice W --win-atr5 A
"""
import argparse, numpy as np, pandas as pd, duckdb
from numba import njit
ap = argparse.ArgumentParser()
ap.add_argument('--hist-slice', required=True, help='the gates-off slice of base_v24 (2020-01..2026-08-21)')
ap.add_argument('--win-slice', required=True, help='the gates-off slice of the later window')
ap.add_argument('--hist-atr5', default='data/equity/atr5/candidates_atr5.parquet')
ap.add_argument('--win-atr5', required=True)
ap.add_argument('--live-from', default='2026-10-12')
args = ap.parse_args()
COLS = ['tkd', 'symbol', 'trade_date', 'signal_sec', 'ent', 'ext', 'ret', 'dv0945', 'volat', 'px', 'gap60', 'l180', 'l300', 'eff10a',
        'v10r', 'z20', 'crf', 'consol_5m_lag1m', 'ht', 'ssh', 'dslo']
def load(sl, atr):
    con = duckdb.connect(); con.execute("SET memory_limit='3GB'")
    return con.execute(f"""SELECT {', '.join('s.' + c for c in COLS)}, a.atr5 FROM '{sl}' s
        LEFT JOIN '{atr}' a ON a.ticker = s.symbol AND a.date = s.trade_date::DATE
        WHERE s.volat >= 40 AND s.gap60 < 40 AND s.dv0945 >= 1.6e6 AND s.px >= 1 AND s.signal_sec BETWEEN 35100 AND 54000
        ORDER BY s.tkd, s.signal_sec""").df()
H = load(args.hist_slice, args.hist_atr5)
W = load(args.win_slice, args.win_atr5)
@njit(cache=False)
def greedy(tkd, ent, ext, mask, keep):
    lt = -1; le = -1
    for i in range(tkd.shape[0]):
        if tkd[i] != lt: lt = tkd[i]; le = -1
        if mask[i] and ent[i] >= le: keep[i] = True; le = ext[i]
def nz(x): return np.nan_to_num(x, nan=-np.inf)
def masks(D):
    v = lambda c: D[c].values.astype(float)
    fresh = (v('l300') >= 6) & (v('eff10a') >= .15) & (v('v10r') >= .75) & (np.nan_to_num(v('z20'), nan=0) < -1.5) & (v('l180') >= 3)
    which = (v('crf') <= -.002) & (np.nan_to_num(v('consol_5m_lag1m'), nan=9) <= .22)
    halt = ~((v('volat') >= 140) & (v('gap60') >= 4)) & ~((v('ht') >= 4) & (v('ssh') < 300))
    near = ~((v('dslo') > 0) & (v('dslo') <= .03)); atr = ~(np.nan_to_num(v('atr5'), nan=0) >= 4)
    one = np.ones(len(D), bool)
    return [('A  volat ≥ 40 + gap_60 < 40 (+ the frame)', one),
            ('B  A + the freshness gates (lows300, eff10, v10r, z20, lows180)', fresh),
            ('C  B + crf + coil', fresh & which),
            ('D  C + rule140 + the halt wait', fresh & which & halt),
            ('E  D + near-low + ATR5 = production', fresh & which & halt & near & atr)]
def book(D, m):
    k = np.zeros(len(D), bool); greedy(D.tkd.values.astype(np.int64), D.ent.values.astype(np.int64), D.ext.values.astype(np.int64), m, k)
    b = D[k].copy(); b['d'] = pd.to_datetime(b.trade_date); return b
def pf(r): l = -r[r < 0].sum(); return np.inf if l == 0 else r[r > 0].sum() / l
def cpf(r): l = -r[r < 0].sum(); return np.inf if l == 0 else np.minimum(r[r > 0], .5).sum() / l
HW = pd.date_range('2020-01-03', '2026-08-21', freq='W-FRI')
print("| layer | 2020–26 trades/wk | mean | PF | ≤ −20 % per 1,000 | ≤ −20 % per 4 wk | live 4 wk: trades | mean | PF | ≤ −20 % (count) | its percentile among 4-week stretches: big losers · Σret |")
print("|---|---|---|---|---|---|---|---|---|---|---|")
for (lab, mh), (_, mw) in zip(masks(H), masks(W)):
    h, w = book(H, mh), book(W, mw)
    hb = (h.ret <= -.2).groupby(h.d.dt.to_period('W-FRI')).sum().reindex(HW.to_period('W-FRI'), fill_value=0)
    hs = h.ret.groupby(h.d.dt.to_period('W-FRI')).sum().reindex(HW.to_period('W-FRI'), fill_value=0)
    hb4, hs4 = hb.rolling(4).sum().dropna(), hs.rolling(4).sum().dropna()
    l = w[w.d >= args.live_from]; nb = int((l.ret <= -.2).sum())
    print(f"| {lab} | {len(h)/len(HW):.0f} | {100*h.ret.mean():+.2f} % | {pf(h.ret.values):.3f} | {1000*(h.ret <= -.2).mean():.1f} | {hb4.mean():.2f} | "
          f"{len(l)} | {100*l.ret.mean():+.2f} % | {pf(l.ret.values):.3f} | {nb} | p{100*(hb4 < nb).mean():.0f} (≥ in {100*(hb4 >= nb).mean():.1f} % of stretches) · "
          f"p{100*(hs4 < l.ret.sum()).mean():.0f} |")
    if lab.startswith('E'): E_h, E_w = h, w
    if lab.startswith('A'): A_h, A_w = h, w
print(f"\nCONTROL: layer E on 2020-01..2026-08-21 = {len(E_h):,} trades (production book 35,767)")
for lab, h, w in (('A', A_h, A_w), ('E', E_h, E_w)):
    print(f"\nlayer {lab}, by year (trades · mean · PF · ≤ −20 % per 1,000) and the window by month:")
    for y, g in h.groupby(h.d.dt.year): print(f"  {y}: {len(g):,} · {100*g.ret.mean():+.2f} % · {pf(g.ret.values):.2f} · {1000*(g.ret <= -.2).mean():.1f}")
    for mo, g in w.groupby(w.d.dt.to_period('M')): print(f"  {mo}: {len(g):,} · {100*g.ret.mean():+.2f} % · {pf(g.ret.values):.2f} · {1000*(g.ret <= -.2).mean():.1f}")
