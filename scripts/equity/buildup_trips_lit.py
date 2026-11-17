r"""BUILDUP STUDY, step 4b (user 2026-11-17: "Let's do it"): the trips' pre-signal order flow re-measured with the TICK RULE ON LIT
PRINTS, trade by trade (buildup_validate2.py: 0.83 per minute / 0.93 over 60 min against the MBO truth, vs 0.70 / 0.79 for
the 1 s-bar tick rule). The windows are the SAME clock spans as buildup_trips.py's N = 60 / 300 / 600 / 1,200 one-second
bars (secs_N, ending at the signal second inclusive), so only the classification changes. Lit = exchange <> 4, the cap
sim's condition exclusions; 300 lit prints before the window as the sign's warmup.

Writes data/equity/buildup/trips_{ff,sf}_lit.parquet (the book + imb_N + nprints_N).
  cd research && python -u scripts/equity/buildup_trips_lit.py > logs/buildup_trips_lit.log 2>&1; echo exit $?
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
    lo = min(t[1] - t[3] for t in trips) - 3600; hi = max(t[1] for t in trips) + 1
    con = duckdb.connect(); con.execute("SET threads=2; SET memory_limit='2GB'")
    lst = ', '.join(f"'{s}'" for s in syms)
    X = con.execute(f"""SELECT ticker, sip_timestamp ts, price, size FROM read_parquet('{TR}/{d}.parquet')
                        WHERE ticker IN ({lst}) AND exchange <> 4 AND size > 0 AND NOT list_has_any(conditions, {EXCL})
                        AND sip_timestamp >= {base + int(max(lo, 4 * 3600) * 1e9)} AND sip_timestamp < {base + int(hi * 1e9)}
                        ORDER BY ticker, sip_timestamp""").df()
    con.close()
    G = {s: g for s, g in X.groupby('ticker')}
    out = []
    for sym, sec, key, span1200, spans in trips:
        r = dict(key=key); g = G.get(sym)
        if g is None: out.append(r); continue
        ts = g.ts.to_numpy(np.int64); p = g.price.to_numpy(float); q = g['size'].to_numpy(float)
        end = np.searchsorted(ts, base + int((sec + 1) * 1e9), 'left')          # prints up to and including the signal second
        for N, span in zip(NS, spans):
            if not np.isfinite(span): continue
            st = np.searchsorted(ts, base + int((sec - span) * 1e9), 'left')
            w0 = max(0, st - 300)
            pp = p[w0:end]
            if end - st < 1: r[f'imb_{N}'] = np.nan; r[f'nprints_{N}'] = 0; continue
            dv = np.diff(pp, prepend=np.nan); s = np.sign(dv); s[~np.isfinite(s)] = 0
            s = pd.Series(np.where(s == 0, np.nan, s)).ffill().fillna(0).to_numpy()[st - w0:]
            vol = q[st:end].sum()
            r[f'imb_{N}'] = (s * q[st:end]).sum() / vol if vol > 0 else np.nan
            r[f'nprints_{N}'] = int(end - st)
        out.append(r)
    return out


if __name__ == '__main__':
    T0 = time.time()
    for tag in ('sf', 'ff'):
        B = pd.read_parquet(f'data/equity/buildup/trips_{tag}.parquet')
        bar_cols = [c for c in B.columns if c.startswith('imb_') or c.startswith('nbars_')]
        B = B.rename(columns={c: c + '_bar' for c in bar_cols})
        jobs = [(d, [(r.symbol, int(r.signal_sec), r.key, r.secs_1200, [getattr(r, f'secs_{N}') for N in NS]) for r in g.itertuples()])
                for d, g in B.groupby('trade_date') if os.path.exists(f'{TR}/{d}.parquet')]
        rows = []
        with Pool(8) as pool:
            for k, r in enumerate(pool.imap_unordered(day, jobs, chunksize=4)):
                rows += r
                if (k + 1) % 200 == 0: print(f'{tag}: {k + 1}/{len(jobs)} days, {time.time() - T0:.0f}s', flush=True)
        out = B.merge(pd.DataFrame(rows), on='key', how='left')
        out.to_parquet(f'data/equity/buildup/trips_{tag}_lit.parquet', index=False)
        print(f'{tag}: {len(out):,} trips, imb_1200 missing {out.imb_1200.isna().sum()}, '
              f'corr with the bar version: ' + ', '.join(f'{N} {out[f"imb_{N}"].corr(out[f"imb_{N}_bar"]):.2f}' for N in NS) +
              f', {time.time() - T0:.0f}s', flush=True)
