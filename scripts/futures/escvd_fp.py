"""ES footprint from the tick tape (docs/es_cvd_plan.md, third pass).

For every session the continuous series holds a contract as the front (data/futures/escvd/es_1m.parquet), read that
contract's fills and write two tables:

  fp_price.parquet   (session, minute, pt, bv, sv)   volume bought at the ask / sold at the bid per PRICE, pt = price * 4
  fp_min.parquet     (session, minute, ...)          per minute: volume by aggressor-burst size, sweeps, burst counts

A BURST is a run of consecutive fills (file order) with one side inside one millisecond: Sierra's sweep markers
(fill_pos) join the fills of one order, and the same-millisecond rule joins the single fills Sierra leaves apart
(docs/futures_data.md: 22 % of ESH21's single fills). It is the size the aggressor showed, not the resting sizes.

  cd research && python -u scripts/futures/escvd_fp.py > logs/escvd_fp.log 2>&1
"""
import sys, time, datetime as dt
from zoneinfo import ZoneInfo
import numpy as np, polars as pl, pyarrow.parquet as pq, pyarrow.dataset as pds, pyarrow.compute as pc
from numba import njit

TICKS = '/mnt/d/trading-edge-bulk/futures/ticks/ES'
BARS = 'data/futures/escvd/es_1m.parquet'
OUT = 'data/futures/escvd'
M = 1380
NY = ZoneInfo('America/New_York'); UTC = dt.timezone.utc
EDGES = np.array([5, 20, 100], np.int64)            # burst size buckets: <5, 5-19, 20-99, 100+


@njit(cache=True)
def bursts(ts, pt, size, side, fp, bar):
    """One row per burst: bar of its first fill, side, size, ticks spanned, fills."""
    n = len(ts)
    ob = np.empty(n, np.int64); os_ = np.empty(n, np.int8); oz = np.empty(n, np.int64)
    osp = np.empty(n, np.int32); of = np.empty(n, np.int32)
    k = -1; open_ = False; lo = 0; hi = 0
    for i in range(n):
        new = True
        if k >= 0:
            same = side[i] == os_[k] and ts[i] // 1000 == ts[i - 1] // 1000
            # inside a marked order (2 = middle, 3 = last) or the same side in the same millisecond
            if (open_ and (fp[i] == 2 or fp[i] == 3) and side[i] == os_[k]) or same:
                new = False
        if new:
            k += 1; ob[k] = bar[i]; os_[k] = side[i]; oz[k] = 0; of[k] = 0; lo = pt[i]; hi = pt[i]
        oz[k] += size[i]; of[k] += 1
        if pt[i] < lo: lo = pt[i]
        if pt[i] > hi: hi = pt[i]
        osp[k] = hi - lo
        open_ = fp[i] == 1 or fp[i] == 2
    k += 1
    return ob[:k], os_[:k], oz[:k], osp[:k], of[:k]


def main():
    g = pl.read_parquet(BARS, columns=['session', 'minute', 'contract']).filter(pl.col('minute') == 0).sort('session')
    sess = g['session'].to_list(); con = g['contract'].to_list()
    start = np.array([int(dt.datetime.combine(s - dt.timedelta(days=1), dt.time(18), NY).astimezone(UTC).timestamp() * 1e6)
                      for s in sess], np.int64)
    con = np.array(con)
    P, Q = [], []
    for c in sorted(set(con), key=lambda x: np.where(con == x)[0][0]):
        idx = np.where(con == c)[0]
        yr = 2000 + int(c[-2:]) - (1 if False else 0)
        f = f'{TICKS}/{yr}_{c}.parquet'
        t0 = time.time()
        try:
            ds = pds.dataset(f)
        except Exception as e:
            print(f'{c}: no tick file ({len(idx)} sessions skipped)', flush=True); continue
        nf = nk = nb = nv = 0
        # a session opens after the 17:00 halt, so no burst straddles a chunk; 6 sessions at a time keeps this under 2 GB
        for c0 in range(0, len(idx), 6):
            ch = idx[c0:c0 + 6]
            lo, hi = int(start[ch[0]]), int(start[ch[-1]]) + M * 60_000_000
            tb = ds.to_table(columns=['timestamp', 'price', 'size', 'side', 'fill_pos'],
                             filter=(pc.field('timestamp') >= lo) & (pc.field('timestamp') < hi))
            ts = tb['timestamp'].to_numpy(); price = tb['price'].to_numpy(); size = tb['size'].to_numpy().astype(np.int64)
            side = tb['side'].to_numpy(); fp = tb['fill_pos'].fill_null(0).to_numpy()
            del tb
            if len(ts) == 0: continue
            pt = np.rint(price.astype(np.float64) * 4).astype(np.int32)
            assert np.abs(price * 4 - pt).max() < 1e-3, 'a price off the quarter grid'
            s = np.searchsorted(start, ts, 'right') - 1
            mn = (ts - start[s]) // 60_000_000
            ok = (mn < M) & (con[s] == c)
            bar = np.where(ok, s * M + mn, -1).astype(np.int64)
            # ---- per price ----
            d = pl.DataFrame({'bar': bar[ok], 'pt': pt[ok], 'size': size[ok], 'side': side[ok]})
            d = d.group_by('bar', 'pt').agg(bv=pl.col('size').filter(pl.col('side') > 0).sum(),
                                            sv=pl.col('size').filter(pl.col('side') < 0).sum())
            P.append(d)
            # ---- per burst ----
            ob, os_, oz, osp, of = bursts(ts, pt, size, side, fp, bar)
            o = pl.DataFrame({'bar': ob, 'side': os_, 'z': oz, 'span': osp, 'fills': of}).filter(pl.col('bar') >= 0)
            b = pl.col('side') > 0
            aggs = []
            for nm, m in (('b', b), ('s', ~b)):
                for j in range(4):
                    lo_ = 0 if j == 0 else EDGES[j - 1]; hi_ = 10 ** 9 if j == 3 else EDGES[j]
                    aggs.append(pl.col('z').filter(m & (pl.col('z') >= lo_) & (pl.col('z') < hi_)).sum().alias(f'{nm}v{j}'))
                aggs += [pl.col('z').filter(m & (pl.col('span') >= 2)).sum().alias(f'{nm}sweep'),
                         m.sum().alias(f'{nm}n'), pl.col('z').filter(m).max().fill_null(0).alias(f'{nm}max')]
            Q.append(o.group_by('bar').agg(aggs))
            nf += len(ts); nk += int(ok.sum()); nb += len(ob); nv += int(size[ok].sum())
            del ts, price, size, side, fp, pt, s, mn, ok, bar, d, o, ob, os_, oz, osp, of
        print(f'{c}: {len(idx)} sessions {sess[idx[0]]}..{sess[idx[-1]]}  fills {nf:,} kept {nk:,} '
              f'bursts {nb:,}  vol {nv:,}  {time.time() - t0:.0f}s', flush=True)
    sd = pl.DataFrame({'s': np.arange(len(sess)), 'session': sess})
    def fin(L, name):
        d = pl.concat(L).with_columns(s=pl.col('bar') // M, minute=(pl.col('bar') % M).cast(pl.Int32))
        d = d.join(sd.with_columns(pl.col('s').cast(pl.Int64)), on='s').drop('s', 'bar')
        keys = ['session', 'minute'] + (['pt'] if 'pt' in d.columns else [])
        d = d.select(keys + [c for c in d.columns if c not in keys]).sort(keys)
        d.write_parquet(f'{OUT}/{name}.parquet'); print(name, d.shape, flush=True)
        return d
    p = fin(P, 'fp_price'); q = fin(Q, 'fp_min')
    # ---- the footprint must add up to the bars ----
    bars = pl.read_parquet(BARS, columns=['session', 'minute', 'volume', 'delta'])
    a = p.group_by('session', 'minute').agg(v=(pl.col('bv') + pl.col('sv')).sum(), d=(pl.col('bv') - pl.col('sv')).sum())
    j = bars.join(a, on=['session', 'minute'], how='inner')
    print('bars with a footprint', j.height, ' volume equal', (j['volume'] == j['v']).mean(), ' delta equal', (j['delta'] == j['d']).mean(),
          ' vol sums', j['volume'].sum(), j['v'].sum(), ' |delta diff| sum', (j['delta'] - j['d']).abs().sum())
    a2 = q.select('session', 'minute', v=pl.sum_horizontal([f'{x}v{k}' for x in 'bs' for k in range(4)]))
    j2 = a.join(a2, on=['session', 'minute'])
    print('burst table adds up to the footprint', (j2['v'] == j2['v_right']).mean())


if __name__ == '__main__':
    main()
