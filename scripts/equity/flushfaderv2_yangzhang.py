"""V2-37: YANG-ZHANG realized volatility as the death-spiral gate, against ATR5 and the 2-prior-day range (user 11-07:
"Instead of using ATR, what if we tried the Yang Zhang realized volatility estimator? Could we also combine it with the
current session's data assuming the close is the current price?").
Daily rows from data/v2/daily_adjusted on the causal P x n scale. Per day i: o = ln(O_i / C_{i-1}), c = ln(C_i / O_i),
rs = ln(H/C) ln(H/O) + ln(L/C) ln(L/O). Over m observations: sigma^2 = var(o) + k var(c) + (1 - k) mean(rs),
k = 0.34 / (1.34 + (m + 1) / (m - 1)), var with ddof 1. yzN = the N sessions BEFORE D (all N required);
yzN_s = those N plus TODAY's partial day: O = D's open, H / L = the session's extremes AT THE SIGNAL, C = the signal vwap,
C_{i-1} = D-1's close on D's raw scale. Thresholds = the production book's own quantiles (the top 0.5 / 1 / 2 / 5 %).
Book-only production-size tally: the V2-25 book joined on (symbol, day, signal_sec), V2-26's % of equity.
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
ap.add_argument('--daily', default='data/v2/daily_adjusted.parquet')
ap.add_argument('--hist-trips', default='data/equity/flushfader/base_v24/trips_*.parquet')
ap.add_argument('--win-trips', required=True)
ap.add_argument('--book', default='data/flushfaderv2/flushfaderv2_reference_book.parquet')
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
    s = con.execute(f"""SELECT DISTINCT ON (t.symbol, t.trade_date::DATE, t.signal_sec) t.symbol, t.trade_date::DATE AS td, t.signal_sec, t.sess_high, t.sess_low, t.signal_vwap
        FROM read_parquet('{glob}', union_by_name = true) t SEMI JOIN k ON k.symbol = t.symbol AND k.trade_date = t.trade_date::DATE AND k.signal_sec = t.signal_sec""").df()
    con.unregister('k'); s['d'] = pd.to_datetime(s.td)
    x = D.merge(s.drop(columns='td'), on=['symbol', 'd', 'signal_sec'], how='left'); assert len(x) == len(D); return x
H = sess(H, args.hist_trips); W = sess(W, args.win_trips)
tick = sorted(set(H.symbol) | set(W.symbol)); con.register('tk', pd.DataFrame({'t': tick}))
Dd = con.execute(f"""SELECT ticker, date, open, high, low, close, n FROM '{args.daily}' WHERE ticker IN (SELECT t FROM tk)
    AND date >= DATE '2019-06-01' AND open > 0 AND high > 0 AND low > 0 AND close > 0 ORDER BY ticker, date""").df()
g = Dd.groupby('ticker', sort=False)
Dd['pc'] = g.close.shift(1) * g.n.shift(1) / Dd.n                         # D-1's close on D's raw scale
Dd['o'] = np.log(Dd.open / Dd.pc); Dd['c'] = np.log(Dd.close / Dd.open)
Dd['rs'] = np.log(Dd.high / Dd.close) * np.log(Dd.high / Dd.open) + np.log(Dd.low / Dd.close) * np.log(Dd.low / Dd.open)
Dd['o2'] = Dd.o ** 2; Dd['c2'] = Dd.c ** 2
NS = [5, 10, 20]
g = Dd.groupby('ticker', sort=False)
for N in NS:
    for col in ['o', 'o2', 'c', 'c2', 'rs']:
        Dd[f'{col}_{N}'] = g[col].transform(lambda s: s.rolling(N, min_periods=N).sum().shift(1))
keep = ['ticker', 'date', 'open', 'pc'] + [f'{c}_{N}' for N in NS for c in ['o', 'o2', 'c', 'c2', 'rs']]
Dd = Dd[keep].copy(); Dd['date'] = pd.to_datetime(Dd.date)
def yz(m, so, so2, sc, sc2, srs):
    vo = (so2 - so ** 2 / m) / (m - 1); vc = (sc2 - sc ** 2 / m) / (m - 1); k = 0.34 / (1.34 + (m + 1) / (m - 1))
    return np.sqrt(np.maximum(vo + k * vc + (1 - k) * srs / m, 0))
def attach(D):
    x = D.merge(Dd, left_on=['symbol', 'd'], right_on=['ticker', 'date'], how='left'); assert len(x) == len(D)
    to = np.log(x.open / x.pc); tc = np.log(x.signal_vwap / x.open)
    trs = np.log(x.sess_high / x.signal_vwap) * np.log(x.sess_high / x.open) + np.log(x.sess_low / x.signal_vwap) * np.log(x.sess_low / x.open)
    for N in NS:
        a = [x[f'{c}_{N}'] for c in ['o', 'o2', 'c', 'c2', 'rs']]
        x[f'yz{N}'] = yz(N, *a)
        x[f'yz{N}_s'] = yz(N + 1, a[0] + to, a[1] + to ** 2, a[2] + tc, a[3] + tc ** 2, a[4] + trs)
    return x.drop(columns=['ticker', 'date'] + [c for c in x.columns if c[:2] in ('o_', 'c_') or c[:3] in ('o2_', 'c2_', 'rs_')])
H = attach(H); W = attach(W)
FEATS = [f'yz{N}{s}' for N in NS for s in ('', '_s')]
BH, BW = base(H), base(W)
AH, AW = ~(np.nan_to_num(H.atr5.values, nan=0) >= 4), ~(np.nan_to_num(W.atr5.values, nan=0) >= 4)
E_h = book(H, BH & AH); E_w = book(W, BW & AW); rng = np.random.default_rng(7)
print("coverage on the production book: " + ", ".join(f"{f} {E_h[f].notna().mean():.1%}" for f in FEATS))
print("sanity — the trips' sess_low <= signal_vwap <= sess_high on " + f"{((E_h.sess_low <= E_h.signal_vwap * (1 + 1e-9)) & (E_h.signal_vwap <= E_h.sess_high * (1 + 1e-9))).mean():.2%}")
print("ρ (Spearman) on the book: " + ", ".join(f"{f}~atr5 {E_h[[f, 'atr5']].corr('spearman').iloc[0, 1]:.2f}" for f in FEATS))
# production-size tally
P = pd.read_parquet(args.book, columns=['symbol', 'trade_date', 'signal_sec', 'ret_exit', 'entry_px', 'pf1_fitted']); P['d'] = pd.to_datetime(P.trade_date)
P = P.merge(E_h[['symbol', 'd', 'signal_sec'] + FEATS], on=['symbol', 'd', 'signal_sec'], how='left')
P['r'] = P.ret_exit + 2 * .001 / P.entry_px; P['peq'] = 15 * P.pf1_fitted.clip(.125, 2) * P.r
print(f"production book joined: {P.yz5.notna().mean():.2%} of {len(P):,}; total {P.peq.sum():+.0f} % eq")
def stats(b): return f"{len(b)} · {pf(b.ret):.2f} · {100*b.ret.sum():+.0f} % · {(b.ret <= -.2).sum()}"
def ctl(n): x = E_h.ret.values; p = [pf(x[rng.choice(len(x), n, replace=False)]) for _ in range(300)]; return np.percentile(p, [5, 95])
print("\n| gate (cut the top q of the book) | threshold | production book cut: trades · PF · ≤ −20 % · % eq · share | harness trades | PF | random same-n | ≤ −20 %/1,000 | Σret | 2026 Jan–May | Jun – 08-21 | window before live n · PF · Σret · big | live n · PF · Σret · big |")
print("|---|---|---|---|---|---|---|---|---|---|---|---|")
for lab, mh, mw in [('production (ATR5)', BH & AH, BW & AW), ('no ATR5', BH, BW)]:
    h, w = book(H, mh), book(W, mw)
    print(f"| {lab} | | | {len(h):,} | {pf(h.ret):.3f} | | {1000*(h.ret <= -.2).mean():.1f} | {h.ret.sum():+.0f} | {pf(seg(h, '2026-01-01', '2026-05-31').ret):.2f} | {pf(seg(h, '2026-06-01', '2026-08-21').ret):.2f} | {stats(w[w.d < args.live_from])} | {stats(w[w.d >= args.live_from])} |")
for f in FEATS:
    for q in [.005, .01, .02, .05]:
        t = E_h[f].quantile(1 - q); x = P[P[f] >= t]
        h, w = book(H, BH & ~(H[f].values >= t)), book(W, BW & ~(W[f].values >= t)); c = ctl(len(h))
        print(f"| {f} top {100*q:g} % | {t:.3f} | {len(x)} · {pf(x.r):.2f} · {(x.r <= -.2).sum()} · {x.peq.sum():+.1f} · {100*x.peq.sum()/P.peq.sum():.1f} % | {len(h):,} | {pf(h.ret):.3f} | {c[0]:.3f}–{c[1]:.3f} | "
              f"{1000*(h.ret <= -.2).mean():.1f} | {h.ret.sum():+.0f} | {pf(seg(h, '2026-01-01', '2026-05-31').ret):.2f} | {pf(seg(h, '2026-06-01', '2026-08-21').ret):.2f} | {stats(w[w.d < args.live_from])} | {stats(w[w.d >= args.live_from])} |")
X = book(W, BW); X = X[X.d >= '2026-08-24']
ranks = {f: E_h[f].dropna().sort_values().values for f in FEATS}
def pct(f, v): return np.nan if np.isnan(v) else 100 * np.searchsorted(ranks[f], v) / len(ranks[f])
L = X.nsmallest(14, 'ret').copy()
for f in ['yz5', 'yz5_s', 'yz20_s']: L[f + '_pctile'] = [pct(f, v) for v in L[f]]
print("\nthe window's worst trades (no ATR5): each feature's percentile in the production book")
print(L[['symbol', 'trade_date', 'ret', 'atr5', 'yz5', 'yz5_pctile', 'yz5_s', 'yz5_s_pctile', 'yz20_s_pctile']].round(3).to_string(index=False))
T = P.merge(E_h[['symbol', 'd', 'signal_sec']], on=['symbol', 'd', 'signal_sec']).nsmallest(10, 'peq')
for f in ['yz5', 'yz5_s']: T[f + '_pctile'] = [pct(f, v) for v in T[f]]
print("\nthe book's 10 worst trades at production size:"); print(T[['symbol', 'd', 'r', 'peq', 'yz5_pctile', 'yz5_s_pctile']].round(2).to_string(index=False))
