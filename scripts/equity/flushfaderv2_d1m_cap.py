"""V2-31: a cap on the 1m DROP (d1m = signal vwap / the 60-bar high − 1; user 11-07: "a gate using the 60-bar range to
avoid buying into some of these more significant collapses", after the 1m-bar finding that > 12 % 1m drops did poorly).
Built on V2-30's leg-floor script (which follows):
Tighter leg-counter floors on production FlushFader (user 11-07: "tighter leg counter gates in the direction that SpikeFader
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
COLS = ['tkd','symbol','trade_date','signal_sec','ent','ext','ret','dv0945','volat','px','gap60','l120','l180','l300','l600','d1m','eff10a',
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
def stats(b): return f"{len(b)} · {pf(b.ret):.2f} · {100*b.ret.sum():+.0f} % · {(b.ret <= -.2).sum()}"
# 1. bands of the production book
E_w = book(W, BW)
BANDS = [(-1, -.25), (-.25, -.15), (-.15, -.12), (-.12, -.08), (-.08, -.05), (-.05, -.03), (-.03, 1)]
print("| 1m drop (vwap / 60-bar high − 1) | 2020–26 trades | mean | PF | ≤ −20 % per 1,000 | 2026 Jan–Aug n · PF | window before live n · PF · Σret · big | live n · PF · Σret · big |")
print("|---|---|---|---|---|---|---|---|")
for a, z in BANDS:
    h = E_h[(E_h.d1m > a) & (E_h.d1m <= z)]; w = E_w[(E_w.d1m > a) & (E_w.d1m <= z)]; h6 = h[h.d >= '2026-01-01']
    print(f"| {100*a:.0f} … {100*z:.0f} % | {len(h):,} | {100*h.ret.mean():+.2f} % | {pf(h.ret):.2f} | {1000*(h.ret <= -.2).mean():.1f} | {len(h6)} · {pf(h6.ret):.2f} | "
          f"{stats(w[w.d < args.live_from])} | {stats(w[w.d >= args.live_from])} |")
# 2. caps, greedy re-run
print("\n| cap: 1m drop no deeper than | trades | mean | PF | random same-n PF | ≤ −20 % per 1,000 | Σret (units) | " + " | ".join(f"{s[0]} PF" for s in SEGS) + " | window before live n · PF · Σret · big | live n · PF · Σret · big |")
print("|---|---|---|---|---|---|---|" + "---|" * len(SEGS) + "---|---|")
for cap in [None, -.25, -.20, -.15, -.12, -.10, -.08]:
    mh = BH & ((H.d1m.values >= cap) if cap else True); mw = BW & ((W.d1m.values >= cap) if cap else True)
    h, w = book(H, mh), book(W, mw); c = ctl(len(h)) if len(h) < len(E_h) else (np.nan, np.nan)
    print(f"| {'none (production)' if cap is None else f'{100*cap:.0f} %'} | {len(h):,} | {100*h.ret.mean():+.2f} % | {pf(h.ret):.3f} | {c[0]:.3f}–{c[1]:.3f} | {1000*(h.ret <= -.2).mean():.1f} | {h.ret.sum():+.0f} | "
          + " | ".join(f"{pf(seg(h, s[1], s[2]).ret):.2f}" for s in SEGS) + f" | {stats(w[w.d < args.live_from])} | {stats(w[w.d >= args.live_from])} |")
print(f"\nCONTROL: production on 2020-01..2026-08-21 = {len(E_h):,} (production book 35,767)")
L = E_w[E_w.d >= args.live_from].nsmallest(12, 'ret')
print(L[['symbol', 'trade_date', 'ret', 'd1m']].round(3).to_string(index=False))
