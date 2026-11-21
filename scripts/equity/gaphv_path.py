"""GH-3 — the high-volume variants' INTRADAY PATH, for the counterfactual hold and the moving-average study (user
2026-11-21: "find the area that has the most profit ... the expectancy of holding winners from 10 to 10:30 ET and every half
hour going forward"; "what % do they spend below their various moving averages during various periods of the day?").
Reads data/equity/gaphv/events.parquet for the event list; one pass over the 1s bars 09:30–16:00 (RAW).

Per event:
  x1000 .. x1530  the $5k VWAP from each half hour (the standing fill rule: the 1s bars from the mark accumulated until
                  price x volume >= $5k, capped at 30 minutes, the VWAP of what traded) — the exit / entry at that mark
  b_<ma>_<p>      the share of the CLOCK seconds of period p (the price forward-filled from the first RTH trade) the price
                  sits BELOW the moving average ma: vwap = the session VWAP from 09:30; s5 / s15 / s30 / s60 = the simple
                  average of the forward-filled 1s price over the last 5 / 15 / 30 / 60 minutes (from 09:30 only: a partial
                  window early in the session)
  c_<ma>_<p>      the crossings of that average in p (sign changes of price − ma): the range-bound measure
  periods p: 0930 [09:30,10:00) · 1000 [10:00,10:30) · 1030 [10:30,11:00) · 1100 [11,12) · 1200 [12,13) · 1300 [13,14) ·
             1400 [14,15) · 1500 [15,16)
    cd research && python3 scripts/equity/gaphv_path.py > logs/gaphv_path.log 2>&1
Output: data/equity/gaphv/path.parquet
"""
import duckdb, numpy as np, pandas as pd, os, time
from multiprocessing import Pool

BARS = 'data/intraday_1s_slim'
O, CLOSE = 34200, 57600
N = CLOSE - O
MARKS = list(range(36000, 57600, 1800))                     # 10:00 .. 15:30
PER = {'0930': (0, 1800), '1000': (1800, 3600), '1030': (3600, 5400), '1100': (5400, 9000), '1200': (9000, 12600),
       '1300': (12600, 16200), '1400': (16200, 19800), '1500': (19800, 23400)}
MAS = {'s5': 300, 's15': 900, 's30': 1800, 's60': 3600}
MIN_DV = 5000.0

def hm(s): return f'{s // 3600:02d}{s % 3600 // 60:02d}'

def one(sec, px, vol):
    r = {}
    for t in MARKS:                                         # the $5k fills
        a = np.searchsorted(sec, t); b = np.searchsorted(sec, t + 1800)
        if a >= b: r[f'x{hm(t)}'] = np.nan; continue
        dv = np.cumsum(px[a:b] * vol[a:b]); k = min(np.searchsorted(dv, MIN_DV), b - a - 1)
        r[f'x{hm(t)}'] = dv[k] / vol[a:a + k + 1].sum()
    i = sec - O                                             # the clock arrays (forward-filled)
    p = np.full(N, np.nan); p[i] = px
    pv = np.zeros(N); v = np.zeros(N); pv[i] = px * vol; v[i] = vol
    first = i[0]
    idx = np.where(np.isfinite(p), np.arange(N), 0); np.maximum.accumulate(idx, out=idx); p = p[idx]
    p[:first] = np.nan
    cv = np.cumsum(v); vw = np.where(cv > 0, np.cumsum(pv) / np.where(cv > 0, cv, 1), np.nan)
    pp = np.nan_to_num(p); cs = np.concatenate([[0.0], np.cumsum(pp)]); cn = np.concatenate([[0], np.cumsum(np.isfinite(p))])
    ar = np.arange(N)
    M = {'vwap': vw}
    for k, w in MAS.items():
        lo = np.maximum(ar + 1 - w, 0)
        n = cn[ar + 1] - cn[lo]
        M[k] = np.where(n > 0, (cs[ar + 1] - cs[lo]) / np.where(n > 0, n, 1), np.nan)
    for k, m in M.items():
        d = p - m
        for pn, (a, b) in PER.items():
            x = d[a:b]; x = x[np.isfinite(x)]
            if len(x) == 0: r[f'b_{k}_{pn}'] = r[f'c_{k}_{pn}'] = np.nan; continue
            r[f'b_{k}_{pn}'] = (x < 0).mean()
            s = np.sign(x); s = s[s != 0]
            r[f'c_{k}_{pn}'] = int((np.diff(s) != 0).sum())
    return r

def day(args):
    d, tk = args
    f = f'{BARS}/{d}.parquet'
    if not os.path.exists(f): return []
    con = duckdb.connect(); con.execute("SET memory_limit='1GB'"); con.execute("SET threads=2")
    B = con.execute(f"""SELECT ticker, bucket, vwap, volume FROM '{f}' WHERE ticker IN (SELECT unnest(?))
                        AND bucket >= {O} AND bucket < {CLOSE} AND volume > 0 AND vwap > 0 ORDER BY ticker, bucket""", [tk]).df()
    out = []
    for t, g in B.groupby('ticker'):
        if len(g) < 10: continue
        r = one(g.bucket.values, g.vwap.values.astype(float), g.volume.values.astype(float))
        r['ticker'], r['d'] = t, d
        out.append(r)
    return out

if __name__ == '__main__':
    t0 = time.time()
    E = pd.read_parquet('data/equity/gaphv/events.parquet', columns=['ticker', 'd'])
    jobs = [(str(pd.Timestamp(d).date()), list(g.ticker)) for d, g in E.groupby('d')]
    res = []
    with Pool(8) as p:
        for i, r in enumerate(p.imap_unordered(day, jobs, chunksize=4)):
            res.extend(r)
            if i % 400 == 0: print(f'{i}/{len(jobs)} days  {len(res):,}  {time.time() - t0:.0f}s', flush=True)
    R = pd.DataFrame(res); R['d'] = pd.to_datetime(R.d).dt.date
    R.to_parquet('data/equity/gaphv/path.parquet', index=False)
    print(f'wrote {len(R):,} of {len(E):,}, {time.time() - t0:.0f}s', flush=True)
