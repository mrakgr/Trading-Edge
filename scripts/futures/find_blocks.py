"""The OFF-BOOK prints in the futures tick tape (block trades, ECRP), which the 1s bars drop (user 2026-10-29, rules 11-01).

Sierra Chart records CFE's off-book Trade messages (blocks, ECRP: "match events that occur off the CFE book") as
ordinary fills with an aggressor side, and its 40-byte record has no condition field. So they are found by two rules;
a fill either rule flags is listed. ⭐ Both are CAUSAL (user 2026-11-01: "any rules we use to filter trades should be
strictly causal"): each reads the fill, its pre-trade quote and the fills BEFORE it, never after.
  1. GRID: the price is off the contract's trade grid. VX book trades print on whole cents (spread legs under a 0.05
     quote), while CFE prices VX blocks and ECRP in 0.005 steps, so a half cent (21.335) can only be off-book. Certain.
  2. QUOTE: a fill that is NOT part of a sweep prints >= 3 quote ticks AND >= 25 bp outside a NORMAL quote (0 < ask −
     bid <= 4 quote ticks; a blown-out book is excluded). The quote is the exchange's, just before the fill: on VX 99.98 % of such
     fills print at or inside it, on ES 99.998 % (2026-11-01), so a print 3 ticks out did not trade on the book.
     Not part of a sweep = the FIRST fill of its millisecond and, where Sierra marks sweeps (fill_pos, CME only; every
     VX fill is 0), not marked middle / last. A sweep's later fills all carry the quote from BEFORE the sweep, so they
     look 3–20+ ticks outside (VX 4,866 of them, ES 11M) while they traded on the book. The millisecond test needs
     Sierra's real-ms clock (from 2020-08-23), so an earlier fill gets rule 1 only and the count is printed.
     THE 25 bp FLOOR (user 2026-11-01): 3 ticks is ~75 bp on VX but 0.2–4 bp on most CME roots, where the 3-tick rule
     alone listed 34,581 fills (RB 14,736 · NG 7,180 · CL 7,022 · MGC 1,681 · NQ 1,244 ...): 1–2-lot fast-market prints
     at the 18:00 reopen, the 10:30 EIA reports, the 14:25–14:30 energy settlement, 08:30 — a quote a few ms old, a
     median 1–15 bp from it, harmless to the bars. The floor keeps only the prints big enough to distort a bar
     (VX: all of its quote-rule prints, a median 94 bp out). bp are of |price| (CL printed below 0 in 2020-04).
  History: the first version (10-29) also required the print to be far from the median of the 10 fills before AND the
  10 after it (the market back) and alone in its ms — a print was dropped because the market reverted, which a
  mean-reversion backtest must not know. A 3σ test against the last 100 fills was tried and dropped (user 11-01).
VX, 2020-09..2026-10, 69.6M fills: rule 2 lists 86 fills (of the first 64, the next quote is back where it was for
59), rule 1 37, 15 of them by both: 108. Left in, the 49 on whole cents would make 33 1s bars out of a block alone and move their 30s bars
by a median 0.95 % (VXN24 2024-07-12, 2,610 lots at 11.65 against a 12.65 bid: −8.0 %). On ES rule 2 lists 75 fills
of 1–5 lots (90 contracts of 340.7M non-sweep fills): fast-market prints against a quote a few ms old; harmless.

The tick and the grid per contract: data/futures/grids.parquet (scripts/futures/measure_grids.py) — the exchange's
fixed price increments, inferred from the tape (VX 0.05 / 0.01; every CME root trades only on its quote tick).

Output: data/futures/blocks.parquet, one row per listed fill: ticker, timestamp (µs UTC, as in ticks/), price (the
tick file's float32), size, side, bid, ask, rule ('grid' / 'quote' / 'both'), out_t, spr_t (in quote ticks), out_bp.
build_1s_bars.py drops these fills on (ticker, timestamp, price, size); every key is checked to match exactly one fill.
Numpy, ONE contract at a time, each column read alone and the rules in 8M-fill chunks (ESZ22, 93M fills, is the peak).

  cd research && python -u scripts/futures/find_blocks.py --roots $(ls /mnt/d/trading-edge-bulk/futures/ticks) > logs/futures_find_blocks.log 2>&1; echo exit $?
"""
import argparse
import glob
import os

import numpy as np
import pandas as pd
import pyarrow.compute as pc
import pyarrow.parquet as pq

FROM_US = 1_420_070_400_000_000          # 2015-01-01 UTC, as build_1s_bars.py
CLOCK_FROM_US = 1_598_140_800_000_000    # 2020-08-23 UTC: Sierra's real-ms clock (rule 2's millisecond test)
FAR, MAX_SPREAD = 3.0, 4.0               # ticks outside the quote; a normal quote's widest spread
FAR_BP = 25.0                            # ... and at least this far in basis points of |price| (user 2026-11-01)
EPS = 1e-4                               # float32 prices: compare tick counts with a margin

ap = argparse.ArgumentParser()
ap.add_argument('--src', default='/mnt/d/trading-edge-bulk/futures/ticks')
ap.add_argument('--roots', nargs='+', default=['VX'])
ap.add_argument('--grids', default='data/futures/grids.parquet')
ap.add_argument('--out', default='data/futures/blocks.parquet')
a = ap.parse_args()
GRIDS = pd.read_parquet(a.grids).set_index('contract')[['tick', 'grid']]


CHUNK = 8_000_000                        # fills per arithmetic chunk (bounds the float64 temporaries)


def scan(path: str, tick: float, grid: float):
    # ⚠ memory (2026-11-01: the first all-roots run was OOM-killed at 14.6 GB on ES, ~150 B a fill): each column is read
    # on its own and sorted, the rules run in chunks, and the key check looks only at the listed fills' timestamps
    ts = pq.read_table(path, columns=['timestamp'])['timestamp'].to_numpy()
    keep = np.flatnonzero(ts >= FROM_US)
    keep = keep[np.argsort(ts[keep], kind='stable')]          # ticks/ is in FILE order, not time order
    ts = ts[keep]

    def col(c):
        a = pq.read_table(path, columns=[c])[c]
        if c == 'fill_pos':
            a = pc.fill_null(a, 0)                           # NULL (no codes) counts as unmarked
        return a.to_numpy()[keep]
    p32, sz, bid, ask, fp, side = col('price'), col('size'), col('bid'), col('ask'), col('fill_pos'), col('side')
    del keep
    n = len(ts)
    off = np.zeros(n, bool); flag = np.zeros(n, bool)
    for a0 in range(0, n, CHUNK):
        s = slice(a0, min(n, a0 + CHUNK))
        px = p32[s].astype(np.float64)
        off[s] = np.abs(px / grid - np.round(px / grid)) > 0.05                              # rule 1
        ms = ts[s] // 1000
        first = np.ones(len(ms), bool); first[1:] = ms[1:] != ms[:-1]                        # no earlier fill in its ms
        if a0 > 0:
            first[0] = ms[0] != ts[a0 - 1] // 1000
        b, k_ = bid[s].astype(np.float64), ask[s].astype(np.float64)
        spr = (k_ - b) / tick
        out_q = np.maximum(np.maximum(b - px, px - k_), 0) / tick
        out_bp = 1e4 * out_q * tick / np.maximum(np.abs(px), 1e-9)
        flag[s] = ((spr > 0) & (spr <= MAX_SPREAD + EPS) & (out_q >= FAR - EPS) & (out_bp >= FAR_BP - EPS)   # rule 2;
                   & first & (fp[s] < 2) & (ts[s] >= CLOCK_FROM_US))                                    # fp 2/3 = middle / last
    k = np.flatnonzero(off | flag)
    # the bars drop on (timestamp, price, size): each listed key must name exactly ONE fill of the contract
    lo, hi = np.searchsorted(ts, ts[k], 'left'), np.searchsorted(ts, ts[k], 'right')
    shared = sum(int(((p32[a:b] == p32[i]) & (sz[a:b] == sz[i])).sum() != 1) for i, a, b in zip(k, lo, hi))
    if shared:
        raise SystemExit(f'{path}: {shared} listed fills share their key with another fill')
    kb, ka, kp = bid[k].astype(np.float64), ask[k].astype(np.float64), p32[k].astype(np.float64)
    rows = pd.DataFrame({'ticker': pq.read_table(path, columns=['ticker'])['ticker'][0].as_py(),
                         'timestamp': ts[k], 'price': p32[k], 'size': sz[k], 'side': side[k], 'bid': bid[k], 'ask': ask[k],
                         'rule': np.where(off[k] & flag[k], 'both', np.where(off[k], 'grid', 'quote')),
                         'out_t': (np.maximum(np.maximum(kb - kp, kp - ka), 0) / tick).astype(np.float32),
                         'spr_t': ((ka - kb) / tick).astype(np.float32),
                         'out_bp': (1e4 * np.maximum(np.maximum(kb - kp, kp - ka), 0) / np.maximum(np.abs(kp), 1e-9)).astype(np.float32)})
    return rows, n, int(sz.sum(dtype=np.int64)), int((ts < CLOCK_FROM_US).sum())


parts, fills, vol = [], 0, 0
for root in a.roots:
    files = sorted(glob.glob(os.path.join(a.src, root, '*.parquet')))
    for f in files:
        contract = os.path.basename(f).split('_', 1)[1][:-8]
        if contract not in GRIDS.index:
            raise SystemExit(f'{contract}: not in {a.grids} (rerun measure_grids.py)')
        g = GRIDS.loc[contract]
        rows, n, v, early = scan(f, float(g.tick), float(g.grid))
        parts.append(rows); fills += n; vol += v
        if len(rows) or early:
            print(f'{os.path.basename(f)}: {len(rows)} of {n:,} fills ({", ".join(f"{r} {c}" for r, c in rows.rule.value_counts().items())})'
                  + (f'; {early:,} fills before the real-ms clock get rule 1 only' if early else ''), flush=True)
    print(f'{root}: {len(files)} contracts', flush=True)
blocks = pd.concat(parts, ignore_index=True)
tmp = a.out + '.tmp'
blocks.to_parquet(tmp, index=False)
os.replace(tmp, a.out)
print(f'done: {len(blocks)} fills listed of {fills:,} ({100 * blocks["size"].sum() / vol:.4f} % of the volume): '
      f'{blocks.rule.value_counts().to_dict()} -> {a.out}', flush=True)
