"""GH-6 pass — per HALF HOUR, the share of the clock seconds below the session VWAP, and the last price before each mark
(for the exit rules' DECISIONS: a rule decides at mark t on what is known before t; its fill is path.parquet's x<t>, the
$5k VWAP from t). Reads data/equity/gaphv/events.parquet; one pass over the 1s bars 09:30–16:00 (RAW).
  v30_<hhmm>  the share of [hhmm, hhmm + 30 min) below the session VWAP (from 09:30; the price forward-filled from the first
              RTH trade), hhmm = 0930 .. 1530
  p_<hhmm>    the last 1s vwap before hhmm (strictly), hhmm = 1000 .. 1530
    cd research && python3 scripts/equity/gaphv_vwap30.py > logs/gaphv_vwap30.log 2>&1
Output: data/equity/gaphv/vwap30.parquet
"""
import duckdb, numpy as np, pandas as pd, os, time
from multiprocessing import Pool

BARS = 'data/intraday_1s_slim'
O, CLOSE = 34200, 57600
N = CLOSE - O
HH = list(range(O, CLOSE, 1800))
def hm(s): return f'{s // 3600:02d}{s % 3600 // 60:02d}'

def one(sec, px, vol):
    r = {}
    for t in HH[1:]:
        j = np.searchsorted(sec, t) - 1
        r[f'p_{hm(t)}'] = px[j] if j >= 0 else np.nan
    i = sec - O
    p = np.full(N, np.nan); p[i] = px
    pv = np.zeros(N); v = np.zeros(N); pv[i] = px * vol; v[i] = vol
    idx = np.where(np.isfinite(p), np.arange(N), 0); np.maximum.accumulate(idx, out=idx); p = p[idx]; p[:i[0]] = np.nan
    cv = np.cumsum(v); vw = np.where(cv > 0, np.cumsum(pv) / np.where(cv > 0, cv, 1), np.nan)
    d = p - vw
    for t in HH:
        x = d[t - O:t - O + 1800]; x = x[np.isfinite(x)]
        r[f'v30_{hm(t)}'] = (x < 0).mean() if len(x) else np.nan
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
        r = one(g.bucket.values, g.vwap.values.astype(float), g.volume.values.astype(float)); r['ticker'], r['d'] = t, d
        out.append(r)
    return out

if __name__ == '__main__':
    t0 = time.time()
    E = pd.read_parquet('data/equity/gaphv/events.parquet', columns=['ticker', 'd'])
    jobs = [(str(pd.Timestamp(d).date()), list(g.ticker)) for d, g in E.groupby('d')]
    res = []
    with Pool(8) as p:
        for r in p.imap_unordered(day, jobs, chunksize=4): res.extend(r)
    R = pd.DataFrame(res); R['d'] = pd.to_datetime(R.d).dt.date
    R.to_parquet('data/equity/gaphv/vwap30.parquet', index=False)
    print(f'wrote {len(R):,} of {len(E):,}, {time.time() - t0:.0f}s')
