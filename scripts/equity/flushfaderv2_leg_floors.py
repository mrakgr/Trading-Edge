"""Tighter leg-counter floors on production FlushFader (user 11-07: "tighter leg counter gates in the direction that SpikeFader
has" — SpikeFader: highs300 >= 40, highs600 >= 90, highs180 >= 15; FlushFader: lows300 >= 6, lows180 >= 3, no 600 rung).
ladder.py's layer E with the floors replaced, greedy mc=1 re-run inside each mask (a cut trade can be replaced by a later
signal), equal units, raw ret. Control: same-n random subsamples of the production (E) book.
Run from research/ with flushfaderv2_gate_ladder.py's inputs:
    python3 -u scripts/equity/flushfaderv2_leg_floors.py --hist-slice H --win-slice W --win-atr5 A
"""
import argparse, numpy as np, pandas as pd, duckdb
from numba import njit
ap = argparse.ArgumentParser()
ap.add_argument('--hist-slice', required=True); ap.add_argument('--win-slice', required=True)
ap.add_argument('--hist-atr5', default='data/equity/atr5/candidates_atr5.parquet'); ap.add_argument('--win-atr5', required=True)
ap.add_argument('--live-from', default='2026-10-12')
args = ap.parse_args()
COLS = ['tkd','symbol','trade_date','signal_sec','ent','ext','ret','dv0945','volat','px','gap60','l120','l180','l300','l600','eff10a',
        'v10r','z20','crf','consol_5m_lag1m','ht','ssh','dslo']
def load(sl, atr):
    con = duckdb.connect(); con.execute("SET memory_limit='3GB'")
    return con.execute(f"""SELECT {', '.join('s.' + c for c in COLS)}, a.atr5 FROM '{sl}' s
        LEFT JOIN '{atr}' a ON a.ticker = s.symbol AND a.date = s.trade_date::DATE
        WHERE s.volat >= 40 AND s.gap60 < 40 AND s.dv0945 >= 1.6e6 AND s.px >= 1 AND s.signal_sec BETWEEN 35100 AND 54000
          AND s.l300 >= 6 AND s.l180 >= 3
        ORDER BY s.tkd, s.signal_sec""").df()
H = load(args.hist_slice, args.hist_atr5)
W = load(args.win_slice, args.win_atr5)
for D in (H, W): D['d'] = pd.to_datetime(D.trade_date)
@njit(cache=False)
def greedy(tkd, ent, ext, mask, keep):
    lt = -1; le = -1
    for i in range(tkd.shape[0]):
        if tkd[i] != lt: lt = tkd[i]; le = -1
        if mask[i] and ent[i] >= le: keep[i] = True; le = ext[i]
def base(D):
    v = lambda c: D[c].values.astype(float)
    fresh = (v('eff10a') >= .15) & (v('v10r') >= .75) & (np.nan_to_num(v('z20'), nan=0) < -1.5)
    which = (v('crf') <= -.002) & (np.nan_to_num(v('consol_5m_lag1m'), nan=9) <= .22)
    halt = ~((v('volat') >= 140) & (v('gap60') >= 4)) & ~((v('ht') >= 4) & (v('ssh') < 300))
    near = ~((v('dslo') > 0) & (v('dslo') <= .03)); atr = ~(np.nan_to_num(v('atr5'), nan=0) >= 4)
    return fresh & which & halt & near & atr
BH, BW = base(H), base(W)
def book(D, m):
    k = np.zeros(len(D), bool); greedy(D.tkd.values.astype(np.int64), D.ent.values.astype(np.int64), D.ext.values.astype(np.int64), m, k)
    return D[k]
def pf(r): r = np.asarray(r); l = -r[r < 0].sum(); return np.inf if l == 0 else r[r > 0].sum() / l
def seg(b, a, z): return b[(b.d >= a) & (b.d <= z)]
SEGS = [('2020–24', '2020-01-01', '2024-12-31'), ('2025', '2025-01-01', '2025-12-31'), ('2026 Jan–May', '2026-01-01', '2026-05-31'),
        ('Jun–08-21', '2026-06-01', '2026-08-21')]
E_h = book(H, BH); rng = np.random.default_rng(7)
def ctl(n):   # same-n random subsample of the production book: PF p5..p95
    x = E_h.ret.values; p = [pf(x[rng.choice(len(x), n, replace=False)]) for _ in range(300)]; return np.percentile(p, [5, 95])
GRID = [(6,3,0),(10,3,0),(15,3,0),(20,3,0),(30,3,0),(40,3,0),(6,8,0),(6,15,0),(6,3,30),(6,3,60),(6,3,90),(15,8,30),(20,15,60),(40,15,90)]
print("| lows300 · lows180 · lows600 floors | trades | mean | PF | random same-n PF p5–p95 | ≤ −20 % per 1,000 | " + " | ".join(f"{s[0]} PF" for s in SEGS) +
      " | window before live n · PF · Σret | live n · PF · Σret · ≤−20 % |")
print("|---|---|---|---|---|---|" + "---|" * len(SEGS) + "---|---|")
for a3, a1, a6 in GRID:
    mh = BH & (H.l300.values >= a3) & (H.l180.values >= a1) & (H.l600.values >= a6)
    mw = BW & (W.l300.values >= a3) & (W.l180.values >= a1) & (W.l600.values >= a6)
    h, w = book(H, mh), book(W, mw); c = ctl(len(h)) if len(h) < len(E_h) else (np.nan, np.nan)
    p1, p2 = w[w.d < args.live_from], w[w.d >= args.live_from]
    print(f"| {a3} · {a1} · {a6} | {len(h):,} | {100*h.ret.mean():+.2f} % | {pf(h.ret):.3f} | {c[0]:.3f}–{c[1]:.3f} | {1000*(h.ret <= -.2).mean():.1f} | "
          + " | ".join(f"{pf(seg(h, s[1], s[2]).ret):.2f}" for s in SEGS)
          + f" | {len(p1)} · {pf(p1.ret):.2f} · {100*p1.ret.sum():+.0f} % | {len(p2)} · {pf(p2.ret):.2f} · {100*p2.ret.sum():+.0f} % · {(p2.ret <= -.2).sum()} |")
print(f"\nCONTROL: production floors (6 · 3 · 0) on 2020-01..2026-08-21 = {len(E_h):,} (production book 35,767)")
