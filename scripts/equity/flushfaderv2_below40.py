"""V2-35: 6-MONTH MOMENTUM — the share of the 126 sessions before D that closed BELOW their 40-session SMA (user 11-07:
"calculating the daily 40d moving average and calculating how many days the stock has been below it in the last 6
months ... Maybe we could at least filter out low volume < 250k stocks with persistently negative 6-month momentum?").
Causal: data/v2/daily_adjusted, close x n (the causal scale, split-proof); SMA40 over 40 rows (incl. that day); below_t =
close_t < SMA40_t; below6m(D) = mean(below) over the 126 rows BEFORE D (>= 60 needed, else NaN = passes). med60_sh as
V2-33 (60 sessions before D, D's share scale). Built on V2-30's leg-floor script (which follows):
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

tick = sorted(set(H.symbol) | set(W.symbol))
con = duckdb.connect(); con.execute("SET memory_limit='3GB'"); con.register('tk', pd.DataFrame({'t': tick}))
Dd = con.execute(f"""SELECT ticker, date, close, volume, n FROM '{args.daily}' WHERE ticker IN (SELECT t FROM tk)
    AND date >= DATE '2018-06-01' ORDER BY ticker, date""").df()
Dd['c'] = Dd.close * Dd.n; Dd['vn'] = Dd.volume / Dd.n
g = Dd.groupby('ticker', sort=False)
Dd['sma40'] = g.c.transform(lambda s: s.rolling(40, min_periods=40).mean())
Dd['below'] = np.where(Dd.sma40.notna(), (Dd.c < Dd.sma40).astype(float), np.nan)
g = Dd.groupby('ticker', sort=False)
Dd['below6m'] = g.below.transform(lambda s: s.rolling(126, min_periods=60).mean().shift(1))
Dd['med60_sh'] = g.vn.transform(lambda s: s.rolling(60, min_periods=20).median().shift(1)) * Dd.n
Dd = Dd[['ticker', 'date', 'below6m', 'med60_sh']].copy(); Dd['date'] = pd.to_datetime(Dd.date)
def attach(D):
    x = D.merge(Dd, left_on=['symbol', 'd'], right_on=['ticker', 'date'], how='left'); assert len(x) == len(D); return x.drop(columns=['ticker', 'date'])
H = attach(H); W = attach(W)
E_h = book(H, BH); E_w = book(W, BW)
print(f"below6m present: book {E_h.below6m.notna().mean():.2%}, window {E_w.below6m.notna().mean():.2%}; median {E_h.below6m.median():.2f}")
def stats(b): return f"{len(b)} · {pf(b.ret):.2f} · {100*b.ret.sum():+.0f} % · {(b.ret <= -.2).sum()}"
ED = [0, .3, .4, .5, .6, .7, .8, .9, 1.01]
for lab, fh, fw in [('all', E_h, E_w), ('med60 < 250k shares', E_h[E_h.med60_sh < 250e3], E_w[E_w.med60_sh < 250e3]),
                    ('med60 ≥ 250k shares', E_h[E_h.med60_sh >= 250e3], E_w[E_w.med60_sh >= 250e3])]:
    print(f"\n**days below the 40d SMA, last 6 months — {lab}**\n\n| share of days below | 2020–26 trades | mean | PF | ≤ −20 % per 1,000 | 2020–24 PF | 2025 PF | 2026 Jan–Aug n · PF | window before live n · PF · Σret · big | live n · PF · Σret · big |")
    print("|---|---|---|---|---|---|---|---|---|---|")
    for a, z in list(zip(ED[:-1], ED[1:])) + [(None, None)]:
        if a is None: h = fh[fh.below6m.isna()]; w = fw[fw.below6m.isna()]; bl = 'no history'
        else: h = fh[(fh.below6m >= a) & (fh.below6m < z)]; w = fw[(fw.below6m >= a) & (fw.below6m < z)]; bl = f"{a:.0%} … {min(z, 1):.0%}"
        h6 = h[h.d >= '2026-01-01']
        print(f"| {bl} | {len(h):,} | {100*h.ret.mean():+.2f} % | {pf(h.ret):.2f} | {1000*(h.ret <= -.2).mean():.1f} | {pf(seg(h, '2020-01-01', '2024-12-31').ret):.2f} | "
              f"{pf(seg(h, '2025-01-01', '2025-12-31').ret):.2f} | {len(h6)} · {pf(h6.ret):.2f} | {stats(w[w.d < args.live_from])} | {stats(w[w.d >= args.live_from])} |")
print("\n| gate (greedy re-run; NaN passes) | trades | mean | PF | random same-n PF | ≤ −20 % per 1,000 | Σret (units) | " + " | ".join(f"{s[0]} PF" for s in SEGS) + " | window before live n · PF · Σret · big | live n · PF · Σret · big |")
print("|---|---|---|---|---|---|---|" + "---|" * len(SEGS) + "---|---|")
V = [('none (production)', lambda D: np.ones(len(D), bool))]
for x in [.9, .8, .7, .6]:
    V.append((f'below6m < {x:.0%}', lambda D, x=x: ~(D.below6m.values >= x)))
for x in [.9, .8, .7, .6]:
    V.append((f'thin (< 250k) AND below6m ≥ {x:.0%} → off', lambda D, x=x: ~((D.below6m.values >= x) & (D.med60_sh.values < 250e3))))
for lab, mk in V:
    h, w = book(H, BH & mk(H)), book(W, BW & mk(W)); c = ctl(len(h)) if len(h) < len(E_h) else (np.nan, np.nan)
    print(f"| {lab} | {len(h):,} | {100*h.ret.mean():+.2f} % | {pf(h.ret):.3f} | {c[0]:.3f}–{c[1]:.3f} | {1000*(h.ret <= -.2).mean():.1f} | {h.ret.sum():+.0f} | "
          + " | ".join(f"{pf(seg(h, s[1], s[2]).ret):.2f}" for s in SEGS) + f" | {stats(w[w.d < args.live_from])} | {stats(w[w.d >= args.live_from])} |")
print(f"\nCONTROL: production on 2020-01..2026-08-21 = {len(E_h):,} (production book 35,767)")
L = E_w[E_w.d >= '2026-08-24'].nsmallest(14, 'ret').copy(); L['med60_k'] = (L.med60_sh / 1e3).round(0)
print(L[['symbol', 'trade_date', 'ret', 'below6m', 'med60_k']].round(3).to_string(index=False))
