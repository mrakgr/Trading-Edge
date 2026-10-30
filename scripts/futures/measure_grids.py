"""Per contract: the QUOTE tick and the TRADE grid, inferred from the tick tape (user 2026-10-30: one off-book filter for
every root instead of cleaning each by hand). find_blocks.py hard-codes VX (quote tick 0.05, trade grid 0.01); a generic
filter needs both for every root, and this measures them from the data before anything is dropped.

  quote tick  the smallest positive spread (ask − bid) seen on >= 0.5 % of the fills with a positive spread
  trade grid  the coarsest step g = tick / k (k in 1, 2, 4, 5, 8, 10, 16, 20, 25, 32, 50, 100) that holds >= 99.99 % of the
              fills; the share OFF the tick itself and off the grid are reported (VX: trades on cents under a 0.05 quote)
Streams each file in batches (ESZ22 has 93M fills): ~0.5 GB. Output data/futures/grids.parquet + a per-root table.

  cd research && python -u scripts/futures/measure_grids.py > logs/futures_measure_grids.log 2>&1; echo exit $?
"""
import argparse, collections, glob, os
import numpy as np, pandas as pd, pyarrow.parquet as pq

ap = argparse.ArgumentParser()
ap.add_argument('--src', default='/mnt/d/trading-edge-bulk/futures/ticks')
ap.add_argument('--out', default='data/futures/grids.parquet')
a = ap.parse_args()
KS = (1, 2, 4, 5, 8, 10, 16, 20, 25, 32, 50, 100)
# exchange ticks are NICE numbers: 1 / 2 / 2.5 / 5 x a power of ten, or a binary fraction (1/32, 1/64, 1/256 …). A
# spread measured on float32 prices is noisy (CL 0.00999, ZN 0.0156), so it is SNAPPED to the nearest nice tick within
# 2 % (else nan)
NICE = np.array(sorted({m * 10.0 ** e for m in (1, 2, 2.5, 5) for e in range(-8, 2)} | {0.5 ** n for n in range(1, 11)}))
def snap(v):
    i = np.argmin(np.abs(np.log(NICE / v)))
    return float(NICE[i]) if abs(NICE[i] / v - 1) <= 0.02 else np.nan

def off(px, g):  # on the grid g within float32 precision
    x = px / g
    return np.abs(x - np.round(x)) > np.maximum(0.02, np.abs(x) * 2e-7)

rows = []
for root in sorted(os.listdir(a.src)):
    for f in sorted(glob.glob(os.path.join(a.src, root, '*.parquet'))):
        pf = pq.ParquetFile(f)
        # pass 1: the spreads -> the quote tick
        spr = collections.Counter(); n = 0
        for b in pf.iter_batches(batch_size=5_000_000, columns=['price', 'bid', 'ask']):
            p = b.column('price').to_numpy(zero_copy_only=False).astype(np.float64)
            s = (b.column('ask').to_numpy(zero_copy_only=False).astype(np.float64) - b.column('bid').to_numpy(zero_copy_only=False))
            s = s[np.isfinite(s) & (s > 0)]
            if len(s):
                # spreads rounded to the price's float32 precision (~7 significant digits of the PRICE), then counted
                dec = 6 - int(np.floor(np.log10(np.median(np.abs(p[p != 0])))))
                u, c = np.unique(np.round(s, dec), return_counts=True)
                spr.update(dict(zip(u.tolist(), c.tolist())))
            n += len(p)
        if not n: continue
        tot = sum(spr.values())
        cands = sorted(v for v, c in spr.items() if c >= 0.005 * tot)
        raw = cands[0] if cands else np.nan
        tick = snap(raw) if np.isfinite(raw) else np.nan
        r = dict(root=root, contract=os.path.basename(f)[5:-8], fills=n, tick=tick, raw_spread=raw, quoted=tot / n)
        if np.isfinite(tick):
            # pass 2: the prints off the tick and off each finer grid, counted batch by batch (never the whole contract)
            offs = np.zeros(len(KS), np.int64)
            for b in pf.iter_batches(batch_size=5_000_000, columns=['price']):
                p = b.column('price').to_numpy(zero_copy_only=False).astype(np.float64)
                for i, k in enumerate(KS): offs[i] += int(off(p, tick / k).sum())
            r['off_tick'] = offs[0] / n
            i = next((i for i in range(len(KS)) if offs[i] <= 1e-4 * n), None)
            if i is None: r['grid'] = np.nan
            else: r['grid'] = tick / KS[i]; r['off_grid'] = offs[i] / n; r['off_grid_n'] = int(offs[i])
        rows.append(r)
    print(f'{root}: {sum(1 for r in rows if r["root"] == root)} contracts', flush=True)
G = pd.DataFrame(rows); G.to_parquet(a.out, index=False)

print('\n| root | contracts | fills | quote tick (contracts) | trade grid (contracts) | off the tick, median | off the grid: fills |')
print('|---|---|---|---|---|---|---|')
for root, g in G.groupby('root'):
    tk = ', '.join(f'{v:g} ({c})' for v, c in g.tick.round(9).value_counts().items())
    gr = ', '.join(f'{v:g} ({c})' for v, c in g.grid.round(9).value_counts(dropna=False).items())
    print(f"| {root} | {len(g)} | {g.fills.sum():,} | {tk} | {gr} | {100 * g.off_tick.median():.4f} % | {int(g.off_grid_n.sum()) if 'off_grid_n' in g else 0:,} |")
