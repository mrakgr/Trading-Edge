"""GH-1 — THE GAPFADER HIGH-VOLUME VARIANTS, event by event (user 2026-11-21: "get a list of biggest winners and losers and
study them ... use the existing high volume setups ... both on the long and the short side. I'd be very curious to know
whether big winners have the highest intraday volatility in the first 30m or whether they are moderate.")

The four variants are GF-64's rules (docs/gapfader_results.md), each at dv60_med >= $500k, open >= $1, a full session:
  U0930  gap-UP   >= +2 %, rvol_pm >= 0.5     short the official open, cover at the official close
  D0930  gap-DOWN <= -2 %, rvol_pm >= 0.25    short the official open (the follow)
  U1000  gap-UP   >= +4 %, rvol30 >= 1        short at 10:00, cover at the close
  D1000  gap-DOWN <= -4 %, rvol30 >= 1        short at 10:00
rvol on the 60-session MEDIAN volume (GF-64's join, not episode-partitioned). The census holds gaps >= 2 % only.
⭐ The 10:00 entry is the $5k VWAP (the standing fill rule, 2026-11-21): the 1s bars from 10:00:00 accumulated until
price x volume >= $5k (short of it by 10:30, the VWAP of what traded). The 09:30 entry is the official open (an auction
fill stays at the print).

Per event, from the 1s bars (research/data/intraday_1s_slim, RAW prices — checked against the census open/close):
  volat30   GapShort.fs's volat30 (research's volat_clk): the 30 s clock slots' vwaps 09:30–10:00, mean |ln(v_i/v_{i-1})|
            over successive traded slots, bp
  range30   (max - min) / open of the 1s vwaps 09:30–10:00;  ret30 = the last 1s vwap before 10:00 / open - 1
  volat60b  the same volat over 10:00–10:30 (after the 10:00 entry)
  px10      the 10:00 $5k VWAP;  m1030 .. m1530: the last 1s vwap at/before each mark;  close_1s
  hi/lo + their seconds: the 1s vwap extremes from 09:30 and from 10:00 to the close (the path, for the exit study)
    cd research && python3 scripts/equity/gaphv_events.py > logs/gaphv_events.log 2>&1
Output: data/equity/gaphv/events.parquet (one row per ticker-day; the variant flags u0930/d0930/u1000/d1000).
"""
import duckdb, numpy as np, pandas as pd, os, sys, time
from multiprocessing import Pool

BARS = 'data/intraday_1s_slim'
OUT = 'data/equity/gaphv'
O, T10, T1030, CLOSE = 34200, 36000, 37800, 57600
MARKS = {'m1030': 37800, 'm1100': 39600, 'm1200': 43200, 'm1300': 46800, 'm1400': 50400, 'm1500': 54000, 'm1530': 55800}
MIN_DV = 5000.0

def events():
    con = duckdb.connect(); con.execute("SET memory_limit='3GB'"); con.execute("SET threads=4")
    con.execute("""CREATE TEMP TABLE c AS SELECT ticker, CAST(date AS DATE) d, gap, open, close, pm_vol, vol_30m
                   FROM 'data/equity/gapfader/census.parquet' WHERE open >= 1 AND NOT early_close AND close IS NOT NULL""")
    con.execute("""CREATE TEMP TABLE s AS SELECT ticker, date d, volume / n AS vn, close * volume AS dv, n,
                   row_number() OVER (PARTITION BY ticker ORDER BY date) i FROM 'data/v2/daily_adjusted.parquet'
                   WHERE ticker IN (SELECT DISTINCT ticker FROM c)""")
    con.execute("""CREATE TEMP TABLE m AS
        SELECT c.ticker, c.d, median(p.vn) * any_value(x.n) AS vol60, median(p.dv) AS dv60, count(*) k
        FROM c JOIN s x ON x.ticker = c.ticker AND x.d = c.d
        JOIN s p ON p.ticker = c.ticker AND p.i BETWEEN x.i - 60 AND x.i - 1
        GROUP BY c.ticker, c.d""")
    E = con.execute("""SELECT c.*, m.vol60, m.dv60, c.pm_vol / m.vol60 AS rvpm, c.vol_30m / m.vol60 AS rv30
                       FROM c JOIN m USING (ticker, d) WHERE m.k >= 20 AND m.vol60 > 0 AND m.dv60 >= 5e5""").df()
    E['u0930'] = (E.gap >= 0.02) & (E.rvpm >= 0.5)
    E['d0930'] = (E.gap <= -0.02) & (E.rvpm >= 0.25)
    E['u1000'] = (E.gap >= 0.04) & (E.rv30 >= 1)
    E['d1000'] = (E.gap <= -0.04) & (E.rv30 >= 1)
    return E[E.u0930 | E.d0930 | E.u1000 | E.d1000].reset_index(drop=True)

def volat(sec, px, vol, lo, hi):
    w = (sec >= lo) & (sec < hi)
    if w.sum() == 0: return np.nan
    s, p, v = (sec[w] - lo) // 30, px[w], vol[w]
    u, inv = np.unique(s, return_inverse=True)
    sv = np.bincount(inv, p * v) / np.bincount(inv, v)
    sv = sv[np.isfinite(sv) & (sv > 0)]
    return 1e4 * np.mean(np.abs(np.diff(np.log(sv)))) if len(sv) >= 2 else np.nan

def one(sec, px, vol, row):
    r = {}
    w30 = (sec >= O) & (sec < T10)
    o = row.open
    r['volat30'] = volat(sec, px, vol, O, T10)
    r['volat60b'] = volat(sec, px, vol, T10, T1030)
    r['range30'] = (px[w30].max() - px[w30].min()) / o if w30.any() else np.nan
    r['ret30'] = px[w30][-1] / o - 1 if w30.any() else np.nan
    r['vol30_1s'] = vol[w30].sum()
    a = np.searchsorted(sec, T10)                        # the $5k VWAP from 10:00
    dv = np.cumsum(px[a:] * vol[a:]); k = np.searchsorted(dv, MIN_DV)
    capk = np.searchsorted(sec[a:], T1030)
    if a < len(sec) and sec[a] < T1030:
        k = min(k, capk - 1, len(dv) - 1)
        r['px10'] = dv[k] / vol[a:a + k + 1].sum(); r['px10_sec'] = int(sec[a + k])
    else: r['px10'], r['px10_sec'] = np.nan, -1
    for m, t in MARKS.items():
        j = np.searchsorted(sec, t, side='right') - 1
        r[m] = px[j] if j >= 0 and sec[j] >= O else np.nan
    r['close_1s'] = px[-1]
    for tag, t0 in (('o', O), ('e', T10)):
        b = np.searchsorted(sec, t0)
        if b < len(sec):
            ih, il = b + np.argmax(px[b:]), b + np.argmin(px[b:])
            r[f'hi_{tag}'], r[f'hi_{tag}_sec'], r[f'lo_{tag}'], r[f'lo_{tag}_sec'] = px[ih], int(sec[ih]), px[il], int(sec[il])
        else: r[f'hi_{tag}'] = r[f'lo_{tag}'] = np.nan; r[f'hi_{tag}_sec'] = r[f'lo_{tag}_sec'] = -1
    return r

def day(args):
    d, rows = args
    f = f'{BARS}/{d}.parquet'
    if not os.path.exists(f): return []
    con = duckdb.connect(); con.execute("SET memory_limit='1GB'"); con.execute("SET threads=2")
    tk = list(rows.ticker.unique())
    B = con.execute(f"""SELECT ticker, bucket, vwap, volume FROM '{f}' WHERE ticker IN (SELECT unnest(?))
                        AND bucket >= {O} AND bucket < {CLOSE} AND volume > 0 AND vwap > 0 ORDER BY ticker, bucket""", [tk]).df()
    out = []
    G = {t: g for t, g in B.groupby('ticker')}
    for row in rows.itertuples():
        g = G.get(row.ticker)
        if g is None or len(g) < 10: continue
        r = one(g.bucket.values, g.vwap.values.astype(float), g.volume.values.astype(float), row)
        r['ticker'], r['d'] = row.ticker, row.d
        out.append(r)
    return out

if __name__ == '__main__':
    t0 = time.time()
    E = events()
    print(f'events {len(E):,}: u0930 {E.u0930.sum():,}  d0930 {E.d0930.sum():,}  u1000 {E.u1000.sum():,}  d1000 {E.d1000.sum():,}', flush=True)
    jobs = [(str(pd.Timestamp(d).date()), g) for d, g in E.groupby('d')]
    res = []
    with Pool(8) as p:
        for i, r in enumerate(p.imap_unordered(day, jobs, chunksize=4)):
            res.extend(r)
            if i % 200 == 0: print(f'{i}/{len(jobs)} days  {len(res):,} events  {time.time() - t0:.0f}s', flush=True)
    R = pd.DataFrame(res)
    R['d'] = pd.to_datetime(R.d).dt.date; E['d'] = pd.to_datetime(E.d).dt.date
    X = E.merge(R, on=['ticker', 'd'])
    os.makedirs(OUT, exist_ok=True)
    X.to_parquet(f'{OUT}/events.parquet', index=False)
    print(f'wrote {len(X):,} of {len(E):,} events, {time.time() - t0:.0f}s', flush=True)
