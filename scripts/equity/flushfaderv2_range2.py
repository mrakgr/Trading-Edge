"""V2-34: the TWO-DAY RANGE as a gate, against / beside ATR5 (user 11-07: "A range filter that takes the max of yesterday's
high and current session's and min of yesterday's low and current session's. Maybe that could work better than the ATR 5
filter."). rng2 = max(H[D-1 .. D-k], sess_high) / min(L[D-1 .. D-k], sess_low) (k = --prior-days), sess_* = the trip's own session extremes AT THE SIGNAL
(causal), D-1 = the ticker's previous daily_adjusted row brought to D's raw scale (x n[D-1] / n[D]; a split dated D
included). No previous row = NaN (passes). Built on V2-30's leg-floor script (which follows):
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
ap.add_argument('--daily', default='data/v2/daily_adjusted.parquet')
ap.add_argument('--hist-trips', default='data/equity/flushfader/base_v24/trips_*.parquet')
ap.add_argument('--win-trips', required=True)
ap.add_argument('--prior-days', type=int, default=1, help='how many prior daily rows join the session (1 = V2-34; 2 = V2-36)')
ap.add_argument('--caps', default='10,6,4,3,2.5,2')
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
    near = ~((v('dslo') > 0) & (v('dslo') <= .03))
    return fresh & which & halt & near
def book(D, m):
    k = np.zeros(len(D), bool); greedy(D.tkd.values.astype(np.int64), D.ent.values.astype(np.int64), D.ext.values.astype(np.int64), m, k)
    return D[k]
def pf(r): r = np.asarray(r); l = -r[r < 0].sum(); return np.inf if l == 0 else r[r > 0].sum() / l
def seg(b, a, z): return b[(b.d >= a) & (b.d <= z)]
SEGS = [('2020–24', '2020-01-01', '2024-12-31'), ('2025', '2025-01-01', '2025-12-31'), ('2026 Jan–May', '2026-01-01', '2026-05-31'),
        ('Jun–08-21', '2026-06-01', '2026-08-21')]
def ctl(n):   # same-n random subsample of the production book: PF p5..p95
    x = E_h.ret.values; p = [pf(x[rng.choice(len(x), n, replace=False)]) for _ in range(300)]; return np.percentile(p, [5, 95])

con = duckdb.connect(); con.execute("SET memory_limit='3GB'"); con.execute("SET threads=4")
def sess(D, glob):
    k = D[['symbol', 'trade_date', 'signal_sec']].drop_duplicates().copy(); k['trade_date'] = pd.to_datetime(k.trade_date).dt.date
    con.register('k', k)
    s = con.execute(f"""SELECT DISTINCT ON (t.symbol, t.trade_date::DATE, t.signal_sec) t.symbol, t.trade_date::DATE AS td, t.signal_sec, t.sess_high, t.sess_low
        FROM read_parquet('{glob}', union_by_name = true) t SEMI JOIN k ON k.symbol = t.symbol AND k.trade_date = t.trade_date::DATE AND k.signal_sec = t.signal_sec""").df()
    con.unregister('k'); s['d'] = pd.to_datetime(s.td)
    x = D.merge(s.drop(columns='td'), on=['symbol', 'd', 'signal_sec'], how='left'); assert len(x) == len(D); return x
H = sess(H, args.hist_trips); W = sess(W, args.win_trips)
tick = sorted(set(H.symbol) | set(W.symbol)); con.register('tk', pd.DataFrame({'t': tick}))
Dd = con.execute(f"""SELECT ticker, date, high, low, n FROM '{args.daily}' WHERE ticker IN (SELECT t FROM tk) AND date >= DATE '2019-06-01' ORDER BY ticker, date""").df()
g = Dd.groupby('ticker', sort=False)
Dd['ph'] = pd.concat([g.high.shift(k) * g.n.shift(k) / Dd.n for k in range(1, args.prior_days + 1)], axis=1).max(axis=1, skipna=False)
Dd['pl'] = pd.concat([g.low.shift(k) * g.n.shift(k) / Dd.n for k in range(1, args.prior_days + 1)], axis=1).min(axis=1, skipna=False)
Dd = Dd[['ticker', 'date', 'ph', 'pl']].copy(); Dd['date'] = pd.to_datetime(Dd.date)
def attach(D):
    x = D.merge(Dd, left_on=['symbol', 'd'], right_on=['ticker', 'date'], how='left'); assert len(x) == len(D)
    x['rng2'] = np.maximum(x.ph, x.sess_high) / np.minimum(x.pl, x.sess_low); return x.drop(columns=['ticker', 'date'])
H = attach(H); W = attach(W)
print(f"sess_* joined: hist {H.sess_high.notna().mean():.2%}, window {W.sess_high.notna().mean():.2%}; rng2 present {H.rng2.notna().mean():.2%} / {W.rng2.notna().mean():.2%}")
BH, BW = base(H), base(W)                       # production WITHOUT ATR5
AH, AW = ~(np.nan_to_num(H.atr5.values, nan=0) >= 4), ~(np.nan_to_num(W.atr5.values, nan=0) >= 4)
E_h = book(H, BH & AH); E_w = book(W, BW & AW); rng = np.random.default_rng(7)
def stats(b): return f"{len(b)} · {pf(b.ret):.2f} · {100*b.ret.sum():+.0f} % · {(b.ret <= -.2).sum()}"
ED = [1, 1.25, 1.5, 2, 2.5, 3, 4, 6, 10, 1e9]
print("\n**rng2 bands (production book)**\n\n| two-day range | 2020–26 trades | mean | PF | ≤ −20 % per 1,000 | 2020–24 PF | 2025 PF | 2026 Jan–Aug n · PF | window before live n · PF · Σret · big | live n · PF · Σret · big |")
print("|---|---|---|---|---|---|---|---|---|---|")
for a, z in zip(ED[:-1], ED[1:]):
    h = E_h[(E_h.rng2 >= a) & (E_h.rng2 < z)]; w = E_w[(E_w.rng2 >= a) & (E_w.rng2 < z)]; h6 = h[h.d >= '2026-01-01']
    print(f"| {a:g} … {z:g} | {len(h):,} | {100*h.ret.mean():+.2f} % | {pf(h.ret):.2f} | {1000*(h.ret <= -.2).mean():.1f} | {pf(seg(h, '2020-01-01', '2024-12-31').ret):.2f} | "
          f"{pf(seg(h, '2025-01-01', '2025-12-31').ret):.2f} | {len(h6)} · {pf(h6.ret):.2f} | {stats(w[w.d < args.live_from])} | {stats(w[w.d >= args.live_from])} |")
print("\n| variant | trades | mean | PF | random same-n PF (of production) | ≤ −20 % per 1,000 | Σret (units) | " + " | ".join(f"{s[0]} PF" for s in SEGS) + " | window before live n · PF · Σret · big | live n · PF · Σret · big |")
print("|---|---|---|---|---|---|---|" + "---|" * len(SEGS) + "---|---|")
def ctl(n): x = E_h.ret.values; p = [pf(x[rng.choice(len(x), n, replace=False)]) for _ in range(300)]; return np.percentile(p, [5, 95])
V = [('production (with ATR5)', BH & AH, BW & AW), ('production WITHOUT ATR5', BH, BW)]
for cap in [float(c) for c in args.caps.split(',')]:
    V.append((f'no ATR5, rng2 < {cap:g}', BH & ~(H.rng2.values >= cap), BW & ~(W.rng2.values >= cap)))
for cap in [float(c) for c in args.caps.split(',')][:2]:
    V.append((f'ATR5 + rng2 < {cap:g}', BH & AH & ~(H.rng2.values >= cap), BW & AW & ~(W.rng2.values >= cap)))
for lab, mh, mw in V:
    h, w = book(H, mh), book(W, mw); c = ctl(len(h)) if len(h) < len(E_h) else (np.nan, np.nan)
    print(f"| {lab} | {len(h):,} | {100*h.ret.mean():+.2f} % | {pf(h.ret):.3f} | {c[0]:.3f}–{c[1]:.3f} | {1000*(h.ret <= -.2).mean():.1f} | {h.ret.sum():+.0f} | "
          + " | ".join(f"{pf(seg(h, s[1], s[2]).ret):.2f}" for s in SEGS) + f" | {stats(w[w.d < args.live_from])} | {stats(w[w.d >= args.live_from])} |")
print(f"\nCONTROL: production on 2020-01..2026-08-21 = {len(E_h):,} (production book 35,767)")
X = book(W, BW); X = X[X.d >= '2026-08-24']
print("\nthe window's worst trades without ATR5 (rng2, atr5, the pieces):")
print(X.nsmallest(14, 'ret')[['symbol', 'trade_date', 'ret', 'rng2', 'atr5', 'ph', 'pl', 'sess_high', 'sess_low']].round(3).to_string(index=False))
