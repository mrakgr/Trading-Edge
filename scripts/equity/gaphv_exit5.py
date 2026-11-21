"""GH-10 — EXIT AT 5 MINUTES when the after-entry volatility is extreme (user 2026-11-21: "It might not be a bad idea to
exit 5m if the after-entry volatility is >= 400 bps"). Pass 1 (one pass over the 1s bars): per event the 5-minute volat
after each entry (v5o = 09:30–09:35, v5e = 10:00–10:05; the 30 s slot measure, bp) and the $5k VWAP from 09:35 / 10:05
(x0935 / x1005, capped at 30 min). Pass 2 (--report): the rule on top of each variant's GH-6 exit (U0930 LOSER, U1000
VWAP, D0930 / D1000 T1530): out at the 5-minute mark when v5 >= the threshold, else the GH-6 exit. The short, gross.
    cd research && python3 scripts/equity/gaphv_exit5.py > logs/gaphv_exit5.log 2>&1
    cd research && python3 scripts/equity/gaphv_exit5.py --report > data/equity/gaphv/exit5.md
"""
import duckdb, numpy as np, pandas as pd, os, sys, io, contextlib, time
from multiprocessing import Pool
BARS = 'data/intraday_1s_slim'

def volat(sec, px, vol, lo, hi):
    w = (sec >= lo) & (sec < hi)
    if w.sum() == 0: return np.nan
    s, p, v = (sec[w] - lo) // 30, px[w], vol[w]
    u, inv = np.unique(s, return_inverse=True)
    sv = np.bincount(inv, p * v) / np.bincount(inv, v); sv = sv[np.isfinite(sv) & (sv > 0)]
    return 1e4 * np.mean(np.abs(np.diff(np.log(sv)))) if len(sv) >= 2 else np.nan

def fill(sec, px, vol, t):
    a, b = np.searchsorted(sec, t), np.searchsorted(sec, t + 1800)
    if a >= b: return np.nan
    dv = np.cumsum(px[a:b] * vol[a:b]); k = min(np.searchsorted(dv, 5000.0), b - a - 1)
    return dv[k] / vol[a:a + k + 1].sum()

def day(args):
    d, tk = args
    f = f'{BARS}/{d}.parquet'
    if not os.path.exists(f): return []
    con = duckdb.connect(); con.execute("SET memory_limit='1GB'"); con.execute("SET threads=2")
    B = con.execute(f"""SELECT ticker, bucket, vwap, volume FROM '{f}' WHERE ticker IN (SELECT unnest(?))
                        AND bucket >= 34200 AND bucket < 57600 AND volume > 0 AND vwap > 0 ORDER BY ticker, bucket""", [tk]).df()
    out = []
    for t, g in B.groupby('ticker'):
        s, p, v = g.bucket.values, g.vwap.values.astype(float), g.volume.values.astype(float)
        out.append({'ticker': t, 'd': d, 'v5o': volat(s, p, v, 34200, 34500), 'x0935': fill(s, p, v, 34500),
                    'v5e': volat(s, p, v, 36000, 36300), 'x1005': fill(s, p, v, 36300)})
    return out

if '--report' not in sys.argv:
    if __name__ == '__main__':
        t0 = time.time()
        E = pd.read_parquet('data/equity/gaphv/events.parquet', columns=['ticker', 'd'])
        jobs = [(str(pd.Timestamp(d).date()), list(g.ticker)) for d, g in E.groupby('d')]
        res = []
        with Pool(8) as p:
            for r in p.imap_unordered(day, jobs, chunksize=4): res.extend(r)
        R = pd.DataFrame(res); R['d'] = pd.to_datetime(R.d).dt.date
        R.to_parquet('data/equity/gaphv/exit5.parquet', index=False); print(f'wrote {len(R):,}, {time.time() - t0:.0f}s')
else:
    sys.path.insert(0, 'scripts/equity')
    with contextlib.redirect_stdout(io.StringIO()):
        from gaphv_exits import X, simulate, pf, f2
    X = X.merge(pd.read_parquet('data/equity/gaphv/exit5.parquet'), on=['ticker', 'd'])
    V = [('U0930', 'u0930', 'open', 'LOSER', 'v5o', 'x0935'), ('D0930', 'd0930', 'open', 'T1530', 'v5o', 'x0935'),
         ('U1000', 'u1000', 'x1000', 'VWAP', 'v5e', 'x1005'), ('D1000', 'd1000', 'x1000', 'T1530', 'v5e', 'x1005')]
    print('# GH-10 — exit at 5 minutes on extreme after-entry volatility\n')
    print('On top of each variant\'s GH-6 exit. v5 = the 30 s slot volat over the first 5 minutes after the entry (bp); the '
          'exit = the $5k VWAP from the 5-minute mark (09:35 / 10:05). The short, gross; sum = Σ %.\n')
    for name, flag, ent, rule, vc, xc in V:
        K = X[X[flag] & np.isfinite(X[ent]) & (X[ent] > 0)].copy().reset_index(drop=True); K['entry'] = K[ent]
        px, at = simulate(K, rule); base = 100 * (1 - px / K.entry.values)
        e5 = 100 * (1 - K[xc].values / K.entry.values)
        print(f'## {name} ({rule}; v5 median {K[vc].median():.0f} bp)\n')
        print('| rule | fired / yr | PF | mean | sum | worst | CVaR 1 % | CVaR 5 % | PF 2016–20 / 2021–26 | the fired trades: 5-min exit / their GH-6 result (mean) |')
        print('|---|---|---|---|---|---|---|---|---|---|')
        for th in (None, 200, 300, 400, 500, 700):
            fire = (K[vc] >= th).values & np.isfinite(e5) if th else np.zeros(len(K), bool)
            r = np.where(fire, e5, base); s = np.sort(r); k1, k5 = max(1, len(s) // 100), max(1, len(s) // 20)
            fx = f'{e5[fire].mean():+.1f} % / {base[fire].mean():+.1f} %' if fire.any() else ''
            print(f'| {"GH-6 exit" if th is None else f"+ out at 5 min if v5 ≥ {th} bp"} | {fire.sum() / 10.1:.0f} | {f2(pf(r))} | {r.mean():+.2f} % | '
                  f'{r.sum():,.0f} | {s[0]:+.0f} % | {s[:k1].mean():+.1f} % | {s[:k5].mean():+.1f} % | '
                  f'{f2(pf(r[(K.yr <= 2020).values]))} / {f2(pf(r[(K.yr >= 2021).values]))} | {fx} |')
        print()
