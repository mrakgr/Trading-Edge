"""GH-2 — the TAPE FEATURES of the high-volume variants' events, at each variant's DECISION time (user 2026-11-21: "For the
big winners, what is their relative volume, intensity and so on? Let's borrow some features from the Snoozer systems and do
a reverse analysis"). Reads data/equity/gaphv/events.parquet (gaphv_events.py) for the event list; one more pass over the 1s
bars (data/intraday_1s_slim, RAW, extended hours from 04:00).

PREMARKET — the 09:25 decision (the collector's screen time), bars in [04:00, 09:25):
  pm_nb        traded seconds                      pm_rate   traded seconds in [08:55, 09:25) / 1800 (LowFader rate)
  pm_inten     $ [08:55, 09:25) / $ [04:00, 08:55) (the Snoozers' inten: recent $ over earlier $)
  pm_volat     the 30 s slot volat over [08:55, 09:25), bp (the Snoozers' volat_open30, premarket)
  pm_trend     the last vwap before 09:25 / the premarket vwap − 1    pm_pos  (last − pm low) / (pm high − pm low)
  pm_tsize     shares per trade (volume / trade_count)                 pm_retail  Σ retail_volume / Σ total_volume (BJZZ)
FIRST 30 MINUTES — the 10:00 decision, bars in [09:30, 10:00) (volat30, range30, ret30, rv30 are in events.parquet):
  nb30         traded seconds / 1800 (density)     inten30   $ [09:45, 10:00) / $ [09:30, 09:45) (GF-30's persistence)
  pos30        (the last vwap − the 30 m low) / (high − low)           er30   |ln(last / first)| / Σ |slot moves| (efficiency)
  hi30_min     minutes from 09:30 to the 30 m high                     tsize30, retail30   as premarket
  volat5, ret5 the first 5 minutes (09:30–09:35): how early a wild open shows (for a decision at 09:35)
    cd research && python3 scripts/equity/gaphv_features.py > logs/gaphv_features.log 2>&1
Output: data/equity/gaphv/features.parquet (ticker, d + the above).
"""
import duckdb, numpy as np, pandas as pd, os, time
from multiprocessing import Pool

BARS = 'data/intraday_1s_slim'
PM0, PM1, PM2, O, O5, O15, T10 = 14400, 32100, 33900, 34200, 34500, 35100, 36000

def slots(sec, px, vol, lo, hi):
    w = (sec >= lo) & (sec < hi)
    if w.sum() == 0: return np.array([])
    s, p, v = (sec[w] - lo) // 30, px[w], vol[w]
    u, inv = np.unique(s, return_inverse=True)
    sv = np.bincount(inv, p * v) / np.bincount(inv, v)
    return sv[np.isfinite(sv) & (sv > 0)]

def volat(sv): return 1e4 * np.mean(np.abs(np.diff(np.log(sv)))) if len(sv) >= 2 else np.nan

def feats(sec, px, vol, tc, tot, ret):
    r = {}
    dv = px * vol
    def W(lo, hi): return (sec >= lo) & (sec < hi)
    pm, pa, pb = W(PM0, PM2), W(PM0, PM1), W(PM1, PM2)
    r['pm_nb'] = int(pm.sum()); r['pm_rate'] = pb.sum() / 1800
    r['pm_inten'] = dv[pb].sum() / dv[pa].sum() if dv[pa].sum() > 0 else np.nan
    r['pm_volat'] = volat(slots(sec, px, vol, PM1, PM2))
    if pm.any():
        pv = dv[pm].sum() / vol[pm].sum(); last = px[pm][-1]; hi, lo = px[pm].max(), px[pm].min()
        r['pm_trend'] = last / pv - 1; r['pm_pos'] = (last - lo) / (hi - lo) if hi > lo else np.nan
        r['pm_tsize'] = vol[pm].sum() / max(tc[pm].sum(), 1)
        r['pm_retail'] = ret[pm].sum() / tot[pm].sum() if tot[pm].sum() > 0 else np.nan
    else: r['pm_trend'] = r['pm_pos'] = r['pm_tsize'] = r['pm_retail'] = np.nan
    a, b, f = W(O, O15), W(O15, T10), W(O, T10)
    r['nb30'] = f.sum() / 1800
    r['inten30'] = dv[b].sum() / dv[a].sum() if dv[a].sum() > 0 else np.nan
    if f.any():
        p = px[f]; hi, lo = p.max(), p.min()
        r['pos30'] = (p[-1] - lo) / (hi - lo) if hi > lo else np.nan
        r['hi30_min'] = (sec[f][np.argmax(p)] - O) / 60
        sv = slots(sec, px, vol, O, T10)
        r['er30'] = abs(np.log(sv[-1] / sv[0])) / np.abs(np.diff(np.log(sv))).sum() if len(sv) >= 2 and np.abs(np.diff(np.log(sv))).sum() > 0 else np.nan
        r['tsize30'] = vol[f].sum() / max(tc[f].sum(), 1)
        r['retail30'] = ret[f].sum() / tot[f].sum() if tot[f].sum() > 0 else np.nan
    else: r['pos30'] = r['hi30_min'] = r['er30'] = r['tsize30'] = r['retail30'] = np.nan
    f5 = W(O, O5)
    r['volat5'] = volat(slots(sec, px, vol, O, O5))
    r['ret5'] = px[f5][-1] / px[f5][0] - 1 if f5.any() else np.nan
    return r

def day(args):
    d, tk = args
    f = f'{BARS}/{d}.parquet'
    if not os.path.exists(f): return []
    con = duckdb.connect(); con.execute("SET memory_limit='1GB'"); con.execute("SET threads=2")
    B = con.execute(f"""SELECT ticker, bucket, vwap, volume, trade_count, total_volume, retail_volume FROM '{f}'
                        WHERE ticker IN (SELECT unnest(?)) AND bucket >= {PM0} AND bucket < {T10} AND volume > 0 AND vwap > 0
                        ORDER BY ticker, bucket""", [tk]).df()
    out = []
    for t, g in B.groupby('ticker'):
        r = feats(g.bucket.values, g.vwap.values.astype(float), g.volume.values.astype(float), g.trade_count.values.astype(float),
                  g.total_volume.values.astype(float), g.retail_volume.values.astype(float))
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
    R.to_parquet('data/equity/gaphv/features.parquet', index=False)
    print(f'wrote {len(R):,} of {len(E):,}, {time.time() - t0:.0f}s', flush=True)
