"""FlushFaderV2 — the CANONICAL vlog for the production reference (user 2026-11-08: "we absolutely need to put this feature
into the production system", research docs/flushfaderv2_results.md V2-42).

    vlog = (S / 143999900.0) * 1200.0,   S = Σ_{j=0..1199} (j − 599.5) · ln(vol_j)   summed SEQUENTIALLY, j = 0 the oldest

over the last 1,200 PRESENT bars ending at the signal bar (its own bar included) — the OLS slope of ln(bar volume) on the
bar index × 1,200, i.e. the fitted ln change of the bar volume across the window. 143,999,900 = Σ (j − 599.5)² =
(1200³ − 1200) / 12, exact in double. NaN when fewer than 1,200 present bars precede (the window is not full) or a bar's
volume is <= 0 (never seen: a present bar has a kept trade). Bars = every present bar from 09:30 (SessionStartSec 34200).
⚠ THE OPERATION ORDER IS THE SPEC: the Oms (Engine/FlushFader.fs, `vlogAt`) runs this exact loop with .NET's Math.Log;
Python's math.log and .NET's both call glibc's log, so the two agree BIT FOR BIT (the Parity seal checks it). numpy's
np.log is NOT used (its SIMD log may differ by an ulp).
Output: one row per reference trip (symbol, trade_date, signal_sec, vlog). Control: against V2-42's vectorised vlog on
the book's trades (data/flushfaderv2/vol_slope.parquet; equal to ~1e-12 — a different summation order).
Run from research/:  python3 -u scripts/equity/flushfaderv2_vlog.py > logs/flushfaderv2_vlog.log 2>&1; echo exit $?
"""
import argparse, math, time, duckdb, numpy as np, pandas as pd
ap = argparse.ArgumentParser()
ap.add_argument('--trips', default='data/flushfaderv2/flushfaderv2_reference_trips.parquet', help='the reference trips (every V2 engine-level trip). ⚠ Since V2-44 (2026-11-08) the reference trips are GATED (bars_since_high >= 300): an ungated reference run needs the vlog of the 196,474 ungated trips — pass pre_vlog_2026-11-08/flushfaderv2_reference_trips.parquet')
ap.add_argument('--bars', default='data/intraday_1s_slim')
ap.add_argument('--out', default='data/flushfaderv2/vlog/vlog_trips.parquet')
ap.add_argument('--check', default='data/flushfaderv2/vol_slope.parquet', help="V2-42's vectorised vlog ('' = no control)")
args = ap.parse_args()
W, C, SXX = 1200, 599.5, 143999900.0
assert SXX == sum((j - C) ** 2 for j in range(W))

def vlog_at(lv, i):
    """lv = the day's ln(volume) list (Python floats, math.log), i = the signal bar's index"""
    if i < W - 1: return math.nan
    s = 0.0; o = i - W + 1
    for j in range(W): s = s + (j - C) * lv[o + j]
    return (s / SXX) * 1200.0

def main():
    import os; os.makedirs(os.path.dirname(args.out), exist_ok=True)
    con = duckdb.connect(); con.execute("SET memory_limit='3GB'"); con.execute("SET threads=4")
    T = con.execute(f"SELECT symbol, trade_date::VARCHAR AS trade_date, signal_sec FROM '{args.trips}' ORDER BY trade_date, symbol, signal_sec").df()
    print(f"{len(T):,} trips, {T.trade_date.nunique():,} days", flush=True)
    out = []; T0 = time.time(); miss = 0; bad = 0
    for k, (day, g) in enumerate(T.groupby('trade_date', sort=True)):
        tick = list(g.symbol.unique())
        bars = con.execute(f"SELECT ticker, bucket, volume FROM '{args.bars}/{day}.parquet' WHERE ticker IN ({','.join('?' * len(tick))}) "
                           "AND volume > 0 AND bucket >= 34200 ORDER BY ticker, bucket", tick).df()
        for sym, gt in g.groupby('symbol'):
            bt = bars[bars.ticker == sym]; sec = bt.bucket.values.astype(np.int64)
            vol = bt.volume.values.astype(np.float64).tolist()
            lv = [math.log(v) if v > 0 else math.nan for v in vol]; bad += sum(v <= 0 for v in vol)
            for s in gt.signal_sec.values:
                i = int(np.searchsorted(sec, s))
                if i < len(sec) and sec[i] == s: out.append((sym, day, int(s), vlog_at(lv, i)))
                else: out.append((sym, day, int(s), math.nan)); miss += 1
        if k % 200 == 0: print(f"  {day}  {k + 1}/{T.trade_date.nunique()}  {time.time() - T0:.0f}s", flush=True)
    R = pd.DataFrame(out, columns=['symbol', 'trade_date', 'signal_sec', 'vlog'])
    R.to_parquet(args.out, index=False)
    print(f"wrote {args.out}: {len(R):,} rows; signal bar not found {miss}; NaN vlog {R.vlog.isna().sum():,} (window not full); volume <= 0 bars {bad}")
    if args.check:
        Q = pd.read_parquet(args.check, columns=['symbol', 'td', 'signal_sec', 'vlog']).rename(columns={'td': 'trade_date', 'vlog': 'v42'})
        M = R.merge(Q, on=['symbol', 'trade_date', 'signal_sec'], how='inner'); ok = M.vlog.notna() & M.v42.notna()
        d = np.abs(M.vlog[ok] - M.v42[ok])
        print(f"CONTROL vs V2-42 on {len(M):,} book trades ({ok.sum():,} both finite; NaN agree {(M.vlog.isna() == M.v42.isna()).mean():.2%}): "
              f"max |Δ| {d.max():.2e}, median {d.median():.2e}")
    print(f"done {time.time() - T0:.0f}s")

main()
