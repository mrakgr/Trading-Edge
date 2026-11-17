r"""BUILDUP STUDY, step 4c (user 2026-11-17): RETAIL order flow before every FlushFader / SpikeFader signal, by the BJZZ rule
(Boehmer, Jones, Zhang & Zhang 2021, "Tracking Retail Investor Activity"): an off-exchange print (exchange 4 = the FINRA
TRF) with a SUB-PENNY price is a wholesaler's fill of a retail order with price improvement. With Z = (100·price) mod 1:
  Z in (0.6, 1)  a retail BUY  (improved just below the ask)
  Z in (0, 0.4)  a retail SELL (improved just above the bid)
round-penny prints and Z in [0.4, 0.6] (midpoint crosses, institutional) are not retail. (Barber et al. 2024 find BJZZ signs
~3/4 of the retail trades it identifies correctly but identifies only about a third of them — a noisy but direct measure.)

The windows are buildup_trips.py's clock spans (secs_N for N = 60 / 300 / 600 / 1,200 one-second bars, ending at the
signal second). Per window: rimb_N = (retail buy − retail sell) / (retail buy + retail sell) volume, rshare_N = retail
volume / all volume (lit + TRF), rvol_N = the retail volume. Writes data/equity/buildup/trips_{ff,sf}_retail.parquet.
  cd research && python -u scripts/equity/buildup_trips_retail.py > logs/buildup_trips_retail.log 2>&1; echo exit $?
"""
import datetime, os, time, zoneinfo
from multiprocessing import Pool
import duckdb, numpy as np, pandas as pd

TR = 'data/bulk/trades'
NS = (60, 300, 600, 1200)
EXCL = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53, 8, 15, 19]::UTINYINT[]"
NY = zoneinfo.ZoneInfo('America/New_York')


def day(args):
    d, trips = args
    y, m, dd = map(int, d.split('-')); base = int(datetime.datetime(y, m, dd, tzinfo=NY).timestamp() * 1e9)
    syms = sorted(set(t[0] for t in trips))
    lo = min(t[1] - max(t[3]) for t in trips) - 5; hi = max(t[1] for t in trips) + 1
    con = duckdb.connect(); con.execute("SET threads=2; SET memory_limit='2GB'")
    lst = ', '.join(f"'{s}'" for s in syms)
    X = con.execute(f"""SELECT ticker, sip_timestamp ts, price, size, exchange = 4 trf FROM read_parquet('{TR}/{d}.parquet')
                        WHERE ticker IN ({lst}) AND size > 0 AND NOT list_has_any(conditions, {EXCL})
                        AND sip_timestamp >= {base + int(max(lo, 4 * 3600) * 1e9)} AND sip_timestamp < {base + int(hi * 1e9)}
                        ORDER BY ticker, sip_timestamp""").df()
    con.close()
    z = np.round(X.price.to_numpy() * 100 % 1, 4)
    X['rs'] = np.where(X.trf & (z > 0.6) & (z < 1.0), 1.0, np.where(X.trf & (z > 0) & (z < 0.4), -1.0, 0.0))
    G = {s: g for s, g in X.groupby('ticker')}
    out = []
    for sym, sec, key, spans in trips:
        r = dict(key=key); g = G.get(sym)
        if g is None: out.append(r); continue
        ts = g.ts.to_numpy(np.int64); q = g['size'].to_numpy(float); rs = g.rs.to_numpy()
        end = np.searchsorted(ts, base + int((sec + 1) * 1e9), 'left')
        for N, span in zip(NS, spans):
            if not np.isfinite(span): continue
            st = np.searchsorted(ts, base + int((sec - span) * 1e9), 'left')
            qq = q[st:end]; ss = rs[st:end]
            rv = (np.abs(ss) * qq).sum(); tot = qq.sum()
            r[f'rimb_{N}'] = (ss * qq).sum() / rv if rv > 0 else np.nan
            r[f'rshare_{N}'] = rv / tot if tot > 0 else np.nan
            r[f'rvol_{N}'] = rv
        out.append(r)
    return out


if __name__ == '__main__':
    T0 = time.time()
    for tag in ('sf', 'ff'):
        B = pd.read_parquet(f'data/equity/buildup/trips_{tag}.parquet')
        jobs = [(d, [(r.symbol, int(r.signal_sec), r.key, [getattr(r, f'secs_{N}') for N in NS]) for r in g.itertuples()])
                for d, g in B.groupby('trade_date') if os.path.exists(f'{TR}/{d}.parquet')]
        rows = []
        with Pool(8) as pool:
            for k, r in enumerate(pool.imap_unordered(day, jobs, chunksize=4)):
                rows += r
                if (k + 1) % 200 == 0: print(f'{tag}: {k + 1}/{len(jobs)} days, {time.time() - T0:.0f}s', flush=True)
        out = B.merge(pd.DataFrame(rows), on='key', how='left')
        out.to_parquet(f'data/equity/buildup/trips_{tag}_retail.parquet', index=False)
        print(f'{tag}: {len(out):,} trips; rimb_1200 missing {out.rimb_1200.isna().sum()}; median retail share (1,200) '
              f'{out.rshare_1200.median():.1%}; {time.time() - T0:.0f}s', flush=True)
