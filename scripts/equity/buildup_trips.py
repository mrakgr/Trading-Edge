r"""BUILDUP STUDY, step 4 (user 2026-11-17: "calculate the signed order flow for 1200 bars prior to every signal for both FlushFader
and SpikeFader"). For every trip of the two reference books:
  FlushFader  data/flushfader_gate_review/broad_reference_book.parquet      (LONG fades of flushes; ret_exit long-signed)
  SpikeFader  data/flushfader_gate_review/spikefader_reference_book.parquet (SHORT fades of spikes; ret_exit SHORT-signed)
the ticker's last 1,200 one-second bars (data/intraday_1s_slim, bars = seconds with prints) up to and including the
SIGNAL second, each signed by the tick rule on its vwap (300 more bars before them as the sign's warmup; validated
against the MBO aggressor side in buildup_validate.py). Per window N in {60, 300, 600, 1200} bars:
  imb_N = signed volume / volume (−1 … +1; < 0 = sellers aggressive)
  secs_N = the clock span of the N bars (s)
Written to data/equity/buildup/trips_{ff,sf}.parquet with the book's columns.

  cd research && python -u scripts/equity/buildup_trips.py > logs/buildup_trips.log 2>&1; echo exit $?
"""
import os, time
from multiprocessing import Pool
import duckdb, numpy as np, pandas as pd

BARS = 'data/intraday_1s_slim'
NS = (60, 300, 600, 1200)
WARM = 300
BOOKS = {'ff': 'data/flushfader_gate_review/broad_reference_book.parquet',
         'sf': 'data/flushfader_gate_review/spikefader_reference_book.parquet'}


def day(args):
    d, trips = args
    syms = sorted(set(t[0] for t in trips))
    con = duckdb.connect(); con.execute("SET threads=2; SET memory_limit='2GB'")
    lst = ', '.join(f"'{s}'" for s in syms)
    X = con.execute(f"SELECT ticker, bucket, vwap, volume FROM read_parquet('{BARS}/{d}.parquet') WHERE ticker IN ({lst}) "
                    f"AND volume > 0 ORDER BY ticker, bucket").df()
    con.close()
    out = []
    G = {s: g for s, g in X.groupby('ticker')}
    for sym, sec, key in trips:
        r = dict(key=key)
        g = G.get(sym)
        if g is None: out.append(r); continue
        b = g.bucket.to_numpy(); j = np.searchsorted(b, sec, 'right')      # bars up to and including the signal second
        i0 = max(0, j - max(NS) - WARM)
        v = g.vwap.to_numpy()[i0:j].astype(float); q = g.volume.to_numpy()[i0:j].astype(float); bb = b[i0:j]
        if len(v) < 2: out.append(r); continue
        dv = np.diff(v, prepend=np.nan); s = np.sign(dv); s[~np.isfinite(s)] = 0
        s = pd.Series(np.where(s == 0, np.nan, s)).ffill().fillna(0).to_numpy()
        for N in NS:
            k = max(0, len(v) - N)
            vol = q[k:].sum()
            r[f'imb_{N}'] = (s[k:] * q[k:]).sum() / vol if vol > 0 else np.nan
            r[f'secs_{N}'] = float(bb[-1] - bb[k]) if len(bb) else np.nan
            r[f'nbars_{N}'] = len(v) - k
        out.append(r)
    return out


if __name__ == '__main__':
    T0 = time.time()
    for tag, path in BOOKS.items():
        B = pd.read_parquet(path).reset_index(drop=True)
        B['key'] = np.arange(len(B)); B['trade_date'] = B.trade_date.astype(str).str[:10]
        jobs = [(d, list(zip(g.symbol, g.signal_sec.astype(int), g.key))) for d, g in B.groupby('trade_date')
                if os.path.exists(f'{BARS}/{d}.parquet')]
        rows = []
        with Pool(8) as pool:
            for k, r in enumerate(pool.imap_unordered(day, jobs, chunksize=4)):
                rows += r
                if (k + 1) % 200 == 0: print(f'{tag}: {k + 1}/{len(jobs)} days, {time.time() - T0:.0f}s', flush=True)
        F = pd.DataFrame(rows)
        out = B.merge(F, on='key', how='left')
        out.to_parquet(f'data/equity/buildup/trips_{tag}.parquet', index=False)
        print(f'{tag}: {len(out):,} trips, imb_1200 missing {out.imb_1200.isna().sum()}, {time.time() - T0:.0f}s', flush=True)
