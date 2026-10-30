"""The OFF-BOOK prints in the futures tick tape (block trades, ECRP), which the 1s bars drop (user 2026-10-29).

Sierra Chart records CFE's off-book Trade messages (blocks, ECRP: "match events that occur off the CFE book") as
ordinary fills with an aggressor side, and its 40-byte record has no condition field. So they are found by two rules;
a fill either rule flags is listed. Both read only the fill and its neighbours:
  1. GRID: the price is off the root's trade grid. VX book trades print on whole cents, while CFE prices VX blocks
     and ECRP in 0.005 steps, so a half cent (21.335) can only be off-book. Certain.
  2. OUTLIER: a lone print far from the market, ALL of:
       a. a NORMAL quote: 0 < ask − bid <= 4 quote ticks (a blown-out crash book is excluded);
       b. >= 3 quote ticks outside that quote (below the bid or above the ask);
       c. >= 3 quote ticks from the median of the 10 fills BEFORE it and of the 10 AFTER it, on the same side of
          both (a real move shifts the market; a block prints at its own price and the next trades are back. ⚠ It
          reads the next 10 fills: fine for cleaning the research corpus, not a live rule);
       d. ALONE in its millisecond (an aggressive order sweeping several resting orders fills several times in the
          same ms; a block is one print). It needs Sierra's real-ms clock (from 2020-08-23), so an earlier fill gets
          rule 1 only (VXU20's first days) and the count is printed.
VX, 2020-09..2026-10, 69.6M fills: rule 1 lists 37 fills, rule 2 lists 54, 15 of them by both rules, 76 in all,
0.0099 % of the volume. That includes VXN24 2024-07-12 2,610 contracts at 11.65, a point below a 12.65 bid, which no
grid test can see. The crash sweeps (2024-08-05, the 2025-04-06 reopen) are excluded by rule d. The CME roots are
not covered: whether CME blocks reach Sierra's tape at all is unknown, and before 2020-08 the ms test cannot be made.

Output: data/futures/blocks.parquet, one row per listed fill: ticker, timestamp (µs UTC, as in ticks/), price (the
tick file's float32), size, side, bid, ask, rule ('grid' / 'outlier' / 'both'), dev_t, out_t, spr_t (in quote ticks).
build_1s_bars.py drops these fills on (ticker, timestamp, price, size); every key is checked to match exactly one fill.
Numpy, ONE contract at a time: ~0.6 GB, VX in ~10 s. (DuckDB's window quantile ignores memory_limit: three runs of it
hit 13.5 GB and crashed the editor, 2026-10-29.)

  cd research && python -u scripts/futures/find_blocks.py > logs/futures_find_blocks.log 2>&1; echo exit $?
"""
import argparse
import glob
import os

import numpy as np
import pandas as pd
import pyarrow.parquet as pq
from numpy.lib.stride_tricks import sliding_window_view

# per root: the QUOTE step (the distances in rule 2) and the TRADE grid (rule 1). VX quotes in 0.05; its trades print
# on cents (spread legs).
ROOTS = {'VX': dict(tick=0.05, grid=0.01)}
FROM_US = 1_420_070_400_000_000          # 2015-01-01 UTC, as build_1s_bars.py
CLOCK_FROM_US = 1_598_140_800_000_000    # 2020-08-23 UTC: Sierra's real-ms clock (rule d)
W, FAR, MAX_SPREAD = 10, 3.0, 4.0        # neighbours each side; ticks outside the quote and from both medians; normal quote

ap = argparse.ArgumentParser()
ap.add_argument('--src', default='/mnt/d/trading-edge-bulk/futures/ticks')
ap.add_argument('--roots', nargs='+', default=list(ROOTS))
ap.add_argument('--out', default='data/futures/blocks.parquet')
a = ap.parse_args()


def scan(path: str, tick: float, grid: float):
    t = pq.read_table(path, columns=['timestamp', 'ticker', 'price', 'size', 'side', 'bid', 'ask'])
    ts = t['timestamp'].to_numpy()
    keep = np.flatnonzero(ts >= FROM_US)
    keep = keep[np.argsort(ts[keep], kind='stable')]          # ticks/ is in FILE order, not time order
    ts, ticker = ts[keep], t['ticker'][0].as_py()
    p32 = t['price'].to_numpy()[keep]
    sz, side = t['size'].to_numpy()[keep], t['side'].to_numpy()[keep]
    bid, ask = t['bid'].to_numpy()[keep], t['ask'].to_numpy()[keep]
    px, n = p32.astype(np.float64), len(ts)
    off = np.abs(px / grid - np.round(px / grid)) > 0.05                                    # rule 1
    flag = np.zeros(n, bool)
    if n > 2 * W:
        m = np.median(sliding_window_view(px, W), axis=1)    # m[j] = the median of px[j:j+W]
        before, after = np.full(n, np.nan), np.full(n, np.nan)
        before[W:], after[:n - W] = m[:n - W], m[1:]
        db, da = (px - before) / tick, (px - after) / tick
        dev = np.minimum(np.abs(db), np.abs(da))
        spr = (ask - bid) / tick
        out_q = np.maximum(np.maximum(bid - px, px - ask), 0) / tick
        ms = ts // 1000
        same_ms = ms[1:] == ms[:-1]
        lone = np.ones(n, bool); lone[1:] &= ~same_ms; lone[:-1] &= ~same_ms
        flag = ((spr > 0) & (spr <= MAX_SPREAD) & (out_q >= FAR) & (dev >= FAR) & (np.sign(db) * np.sign(da) > 0) & lone
                & (ts >= CLOCK_FROM_US))
    else:
        dev = spr = out_q = np.full(n, np.nan)
    k = np.flatnonzero(off | flag)
    # the bars drop on (timestamp, price, size): each listed key must name exactly ONE fill of the contract
    keys = pd.DataFrame({'timestamp': ts, 'price': p32, 'size': sz})
    dup = keys.iloc[k].merge(keys, on=['timestamp', 'price', 'size']).groupby(['timestamp', 'price', 'size']).size()
    if (dup != 1).any():
        raise SystemExit(f'{path}: {int((dup != 1).sum())} listed fills share their key with another fill')
    rows = pd.DataFrame({'ticker': ticker, 'timestamp': ts[k], 'price': p32[k], 'size': sz[k], 'side': side[k],
                         'bid': bid[k], 'ask': ask[k],
                         'rule': np.where(off[k] & flag[k], 'both', np.where(off[k], 'grid', 'outlier')),
                         'dev_t': dev[k].astype(np.float32), 'out_t': out_q[k].astype(np.float32),
                         'spr_t': spr[k].astype(np.float32)})
    return rows, n, int(sz.sum()), int((ts < CLOCK_FROM_US).sum())


parts, fills, vol = [], 0, 0
for root in a.roots:
    if root not in ROOTS:
        raise SystemExit(f'{root}: no rules for this root (only {list(ROOTS)})')
    files = sorted(glob.glob(os.path.join(a.src, root, '*.parquet')))
    for f in files:
        rows, n, v, early = scan(f, **ROOTS[root])
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
