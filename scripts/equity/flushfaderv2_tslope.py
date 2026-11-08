"""FlushFaderV2 — the CANONICAL time-indexed 20m price slope `ts20` for the production reference (user 2026-11-08: "We'll
aim to replace the bar indexed price slope over the last 1,200 bars with a timestamp indexed one", research
docs/flushfaderv2_results.md V2-45 `span 1`).

    tslope = the OLS slope of ln(bar vwap) on x = (the bar's second − 34200) over the last 1,200 PRESENT bars ending at
             the signal bar (its own bar included), per second; ts20 = tslope × 6e5 (bp/min, s20's unit)

computed ONLINE exactly as the Oms does it (RollingMa `TimeOlsRoll` on the engine's 1,200-bar WindowRoller): five running
sums over every present bar from 09:30, REMOVE BEFORE ADD, in the order sx, sy, sxx, sxy, syy;
slope = (n·Σxy − Σx·Σy) / (n·Σx² − (Σx)²), only with a full window (NaN otherwise).
⚠ THE OPERATION ORDER IS THE SPEC. Python floats are IEEE doubles and math.log is glibc's, as .NET's Math.Log, so this
replay and the Oms agree BIT FOR BIT (the Parity seal checks it). x is an integer count: Σx, Σx² are exact.
Output: one row per reference trip (symbol, trade_date, signal_sec, tslope). Control: against V2-45's two-pass `p_span1`
(data/flushfaderv2/slot_slopes.parquet; the same definition, a different summation) on the book's trades.
Run from research/:  python3 -u scripts/equity/flushfaderv2_tslope.py > logs/flushfaderv2_tslope.log 2>&1; echo exit $?
"""
import argparse, math, time, duckdb, numpy as np, pandas as pd
ap = argparse.ArgumentParser()
ap.add_argument('--trips', default='data/flushfaderv2/flushfaderv2_reference_trips.parquet', help='the reference trips (every V2 engine-level trip)')
ap.add_argument('--bars', default='data/intraday_1s_slim')
ap.add_argument('--out', default='data/flushfaderv2/tslope/tslope_trips.parquet')
ap.add_argument('--check', default='data/flushfaderv2/slot_slopes.parquet', help="V2-45's two-pass p_span1 ('' = no control)")
args = ap.parse_args()
W, ANCHOR = 1200, 34200

def replay(sec, vwap, wanted):
    """sec / vwap = the ticker-day's present bars from 09:30 (python lists); wanted = {bar index: signal_sec}. -> {signal_sec: slope}"""
    out = {}; last = max(wanted)
    sx = sy = sxx = sxy = syy = 0.0; n = 0
    xs = [0.0] * len(sec); ys = [0.0] * len(sec)
    for i in range(last + 1):
        x = float(sec[i] - ANCHOR); y = math.log(vwap[i]); xs[i] = x; ys[i] = y
        if n == W:   # Roll: RemovePoint(old) then AddPoint(new)
            ox = xs[i - W]; oy = ys[i - W]
            sx = sx - ox; sy = sy - oy
            sxx = sxx - ox * ox; sxy = sxy - ox * oy; syy = syy - oy * oy
        else:
            n += 1
        sx = sx + x; sy = sy + y
        sxx = sxx + x * x; sxy = sxy + x * y; syy = syy + y * y
        if i in wanted:
            fn = float(n); dxx = fn * sxx - sx * sx
            out[wanted[i]] = (fn * sxy - sx * sy) / dxx if (n == W and dxx > 0.0) else math.nan
    return out

def main():
    import os; os.makedirs(os.path.dirname(args.out), exist_ok=True)
    con = duckdb.connect(); con.execute("SET memory_limit='3GB'"); con.execute("SET threads=4")
    T = con.execute(f"SELECT symbol, trade_date::VARCHAR AS trade_date, signal_sec FROM '{args.trips}' ORDER BY trade_date, symbol, signal_sec").df()
    print(f"{len(T):,} trips, {T.trade_date.nunique():,} days, {len(T.groupby(['symbol', 'trade_date'])):,} ticker-days", flush=True)
    out = []; T0 = time.time(); miss = 0
    for k, (day, g) in enumerate(T.groupby('trade_date', sort=True)):
        tick = list(g.symbol.unique())
        bars = con.execute(f"SELECT ticker, bucket, vwap FROM '{args.bars}/{day}.parquet' WHERE ticker IN ({','.join('?' * len(tick))}) "
                           "AND volume > 0 AND bucket >= 34200 ORDER BY ticker, bucket", tick).df()
        for sym, gt in g.groupby('symbol'):
            bt = bars[bars.ticker == sym]; sec = bt.bucket.values.astype(np.int64)
            idx = np.searchsorted(sec, gt.signal_sec.values)
            ok = (idx < len(sec)) & (sec[np.minimum(idx, len(sec) - 1)] == gt.signal_sec.values)
            miss += int((~ok).sum())
            wanted = {int(i): int(s) for i, s, o in zip(idx, gt.signal_sec.values, ok) if o}
            res = replay(sec.tolist(), bt.vwap.values.astype(np.float64).tolist(), wanted) if wanted else {}
            for s in gt.signal_sec.values: out.append((sym, day, int(s), res.get(int(s), math.nan)))
        if k % 200 == 0: print(f"  {day}  {k + 1}/{T.trade_date.nunique()}  {time.time() - T0:.0f}s", flush=True)
    R = pd.DataFrame(out, columns=['symbol', 'trade_date', 'signal_sec', 'tslope'])
    R.to_parquet(args.out, index=False)
    print(f"wrote {args.out}: {len(R):,} rows; signal bar not found {miss}; NaN tslope {R.tslope.isna().sum():,} (window not full)")
    if args.check:
        Q = pd.read_parquet(args.check, columns=['symbol', 'td', 'signal_sec', 'p_span1']).rename(columns={'td': 'trade_date'})
        M = R.merge(Q, on=['symbol', 'trade_date', 'signal_sec'], how='inner'); ok = M.tslope.notna() & M.p_span1.notna()
        d = np.abs(M.tslope[ok] * 6e5 - M.p_span1[ok])
        print(f"CONTROL ts20 vs V2-45's two-pass p_span1 on {len(M):,} book trades ({ok.sum():,} both finite): max |Δ| {d.max():.2e} bp/min, median {d.median():.2e}")
    print(f"done {time.time() - T0:.0f}s")

main()
