"""V2-39: the production book by the TIME from the last new 20m high to the signal (bars_since_high: present 1s bars since the
bar after the last vwap above the prior 1,200-present-bar max; the engine resets it on that high) and from the leg's first low
(secs_since_first_low); user 2026-11-08: "I wonder if faster signals have a higher EV?". Joined from base_v24 (the
production base). Run from research/."""
import duckdb, numpy as np, pandas as pd
con = duckdb.connect(); con.execute("SET memory_limit='3GB'"); con.execute("SET threads=4")
def load(book, trips):
    return con.execute(f"""
     WITH b AS (SELECT symbol, trade_date::DATE d, signal_sec, ret_exit, entry_px, pf1_fitted FROM '{book}'),
          t AS (SELECT DISTINCT ON (symbol, trade_date::DATE, signal_sec) symbol, trade_date::DATE d, signal_sec, bars_since_high, secs_since_first_low,
                       signal_vwap FROM read_parquet({trips}, union_by_name = true)
                WHERE (symbol, trade_date::DATE) IN (SELECT symbol, d FROM b))
     SELECT b.*, t.bars_since_high, t.secs_since_first_low FROM b LEFT JOIN t USING (symbol, d, signal_sec)""").df()
H = load('data/flushfaderv2/flushfaderv2_reference_book.parquet', "'data/equity/flushfader/base_v24/trips_*.parquet'")
W = H.iloc[:0].copy()
for B in (H, W):
    B['d'] = pd.to_datetime(B.d); B['r'] = B.ret_exit + 2 * .001 / B.entry_px; B['peq'] = 15 * B.pf1_fitted.clip(.125, 2) * B.r
print(f"joined: {H.bars_since_high.notna().mean():.2%} of {len(H):,}")
print(H.bars_since_high.describe(percentiles=[.1, .25, .5, .75, .9]).round(0).to_dict())
def pf(r): r = np.asarray(r); l = -r[r < 0].sum(); return np.inf if l == 0 else r[r > 0].sum() / l
def table(col, edges, lab):
    print(f"\n**{lab}**\n\n| band | 2020–26 trades | mean | median | PF | PF trim 5 % | ≤ −20 %/1,000 | % eq | % eq per trade (bp) | 2020–22 PF | 2023–24 PF | 2025 PF | 2026 PF |")
    print("|---|---|---|---|---|---|---|---|---|---|---|---|---|")
    for a, z in zip(edges[:-1], edges[1:]):
        h = H[(H[col] >= a) & (H[col] < z)]; w = W[(W[col] >= a) & (W[col] < z)]; y = h.d.dt.year
        rs = np.sort(h.r.values)
        print(f"| {a}–{z - 1 if z < 1e8 else '∞'} | {len(h):,} | {100*h.r.mean():+.2f} % | {100*h.r.median():+.2f} % | {pf(h.r):.2f} | {pf(rs[int(.05*len(rs)):]):.2f} | {1000*(h.r <= -.2).mean():.1f} | {h.peq.sum():+.0f} | {100*h.peq.mean():+.1f} | "
              f"{pf(h[y <= 2022].r):.2f} | {pf(h[(y >= 2023) & (y <= 2024)].r):.2f} | {pf(h[y == 2025].r):.2f} | {pf(h[y == 2026].r):.2f} |")
table('bars_since_high', [0, 30, 60, 120, 180, 300, 450, 600, 900, 1200, 10**9], 'present bars from the last new 20m high to the signal')
table('secs_since_first_low', [0, 30, 60, 120, 180, 300, 600, 1200, 10**9], 'seconds from the leg\'s first low to the signal')
