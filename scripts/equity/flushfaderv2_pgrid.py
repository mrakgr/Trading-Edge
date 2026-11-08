"""FlushFaderV2 — the CANONICAL B features for the production reference (user 2026-11-08: "If so, this is huge. We
definitely want this."; research docs/flushfaderv2_results.md V2-47..V2-49):

    pgrid60 = the OLS slope of ln(60s GRID-SLOT vwap) on the slot's time over the last 1,200 PRESENT bars (per second;
              × 6e5 = s20 in bp/min): slots g = (sec − 34200) // 60 (the chart's minutes), the newest partial, the
              oldest holding only the window's bars, one point per slot (RollingMa GridTimeOlsRoll)
    vrate   = the OLS slope of ln(1s bar volume) on the bar's second (sec − 34200) over the same 1,200 bars (per second;
              × 1,200 = the sizing term) (RollingMa TimeOlsRoll)

both ONLINE exactly as the Oms runs them on the engine's 1,200-bar WindowRoller: every present bar from 09:30, REMOVE
BEFORE ADD (warmup: add only), the five running sums in OlsRoll's order (sx, sy, sxx, sxy, syy); the grid's slot updates
in GridTimeOlsRoll's order (its header). Slopes only with a full window (NaN otherwise).
⚠ THE OPERATION ORDER IS THE SPEC: Python floats are IEEE doubles and math.log is glibc's, as .NET's Math.Log, so this
replay and the Oms agree BIT FOR BIT (the Parity seal checks it).
Output: one row per reference trip (symbol, trade_date, signal_sec, pgrid60, vrate). Control: against V2-47's two-pass
p_g60 / tvlog (data/flushfaderv2/tslot_years.parquet) on the book's trades.
Run from research/:  python3 -u scripts/equity/flushfaderv2_pgrid.py > logs/flushfaderv2_pgrid.log 2>&1; echo exit $?
"""
import argparse, math, time, duckdb, numpy as np, pandas as pd
ap = argparse.ArgumentParser()
ap.add_argument('--trips', default='data/flushfaderv2/flushfaderv2_reference_trips.parquet', help='the reference trips (every V2 engine-level trip)')
ap.add_argument('--bars', default='data/intraday_1s_slim')
ap.add_argument('--out', default='data/flushfaderv2/pgrid/pgrid_trips.parquet')
ap.add_argument('--check', default='data/flushfaderv2/tslot_years.parquet', help="V2-47's two-pass p_g60 / tvlog ('' = no control)")
args = ap.parse_args()
W, ANCHOR, K = 1200, 34200, 60

def replay(sec, vwap, vol, wanted):
    """the ticker-day's present bars from 09:30 (python lists); wanted = {bar index: signal_sec} -> {signal_sec: (pgrid, vrate)}"""
    out = {}; last = max(wanted); n = 0
    # the volume rate: TimeOlsRoll
    ax = ay = axx = axy = ayy = 0.0
    # the grid price slope: GridTimeOlsRoll (slots as parallel lists, oldest first)
    gx = gy = gxx = gxy = gyy = 0.0
    sg, sv, sd, sn, syv = [], [], [], [], []; h = 0          # h = the oldest slot's index (lists only grow)
    X = [0.0] * (last + 1); LV = [0.0] * (last + 1); V = [0.0] * (last + 1); DV = [0.0] * (last + 1); G = [0] * (last + 1)
    for i in range(last + 1):
        x = float(sec[i] - ANCHOR); v = vol[i]; dv = vwap[i] * v; ly = math.log(v)
        X[i] = x; LV[i] = ly; V[i] = v; DV[i] = dv; G[i] = int(x) // K
        if n == W:   # Roll: remove the departing bar first
            j = i - W; ox = X[j]; oy = LV[j]
            ax = ax - ox; ay = ay - oy; axx = axx - ox * ox; axy = axy - ox * oy; ayy = ayy - oy * oy
            px = float(sg[h] * K); py = syv[h]
            gx = gx - px; gy = gy - py; gxx = gxx - px * px; gxy = gxy - px * py; gyy = gyy - py * py
            sn[h] -= 1
            if sn[h] == 0: h += 1
            else:
                sv[h] = sv[h] - V[j]; sd[h] = sd[h] - DV[j]
                y = math.log(sd[h] / sv[h]); syv[h] = y
                gx = gx + px; gy = gy + y; gxx = gxx + px * px; gxy = gxy + px * y; gyy = gyy + y * y
        else:
            n += 1
        ax = ax + x; ay = ay + ly; axx = axx + x * x; axy = axy + x * ly; ayy = ayy + ly * ly
        g = G[i]
        if len(sg) > h and sg[-1] == g:
            px = float(g * K); py = syv[-1]
            gx = gx - px; gy = gy - py; gxx = gxx - px * px; gxy = gxy - px * py; gyy = gyy - py * py
            sv[-1] = sv[-1] + v; sd[-1] = sd[-1] + dv; sn[-1] += 1
            y = math.log(sd[-1] / sv[-1]); syv[-1] = y
        else:
            sg.append(g); sv.append(v); sd.append(dv); sn.append(1)
            y = math.log(sd[-1] / sv[-1]); syv.append(y); px = float(g * K)
        gx = gx + px; gy = gy + y; gxx = gxx + px * px; gxy = gxy + px * y; gyy = gyy + y * y
        if i in wanted:
            fn = float(n); dxx = fn * axx - ax * ax
            vr = (fn * axy - ax * ay) / dxx if (n == W and dxx > 0.0) else math.nan
            ns = len(sg) - h; fs = float(ns); gxxd = fs * gxx - gx * gx
            pg = (fs * gxy - gx * gy) / gxxd if (n == W and ns >= 2 and gxxd > 0.0) else math.nan
            out[wanted[i]] = (pg, vr)
    return out

def main():
    import os; os.makedirs(os.path.dirname(args.out), exist_ok=True)
    con = duckdb.connect(); con.execute("SET memory_limit='3GB'"); con.execute("SET threads=4")
    T = con.execute(f"SELECT symbol, trade_date::VARCHAR AS trade_date, signal_sec FROM '{args.trips}' ORDER BY trade_date, symbol, signal_sec").df()
    print(f"{len(T):,} trips, {T.trade_date.nunique():,} days", flush=True)
    out = []; T0 = time.time(); miss = 0
    for k, (day, g) in enumerate(T.groupby('trade_date', sort=True)):
        tick = list(g.symbol.unique())
        bars = con.execute(f"SELECT ticker, bucket, vwap, volume FROM '{args.bars}/{day}.parquet' WHERE ticker IN ({','.join('?' * len(tick))}) "
                           "AND volume > 0 AND bucket >= 34200 ORDER BY ticker, bucket", tick).df()
        for sym, gt in g.groupby('symbol'):
            bt = bars[bars.ticker == sym]; sec = bt.bucket.values.astype(np.int64)
            idx = np.searchsorted(sec, gt.signal_sec.values)
            ok = (idx < len(sec)) & (sec[np.minimum(idx, len(sec) - 1)] == gt.signal_sec.values)
            miss += int((~ok).sum())
            wanted = {int(i): int(s) for i, s, o in zip(idx, gt.signal_sec.values, ok) if o}
            res = replay(sec.tolist(), bt.vwap.values.astype(np.float64).tolist(), bt.volume.values.astype(np.float64).tolist(), wanted) if wanted else {}
            for s in gt.signal_sec.values:
                pg, vr = res.get(int(s), (math.nan, math.nan)); out.append((sym, day, int(s), pg, vr))
        if k % 200 == 0: print(f"  {day}  {k + 1}/{T.trade_date.nunique()}  {time.time() - T0:.0f}s", flush=True)
    R = pd.DataFrame(out, columns=['symbol', 'trade_date', 'signal_sec', 'pgrid60', 'vrate'])
    R.to_parquet(args.out, index=False)
    print(f"wrote {args.out}: {len(R):,} rows; signal bar not found {miss}; NaN pgrid60 {R.pgrid60.isna().sum():,}, vrate {R.vrate.isna().sum():,}")
    if args.check:
        Q = pd.read_parquet(args.check, columns=['symbol', 'td', 'signal_sec', 'p_g60', 'tvlog']).rename(columns={'td': 'trade_date'})
        M = R.merge(Q, on=['symbol', 'trade_date', 'signal_sec'], how='inner')
        d1 = np.abs(M.pgrid60 * 6e5 - M.p_g60); d2 = np.abs(M.vrate * 1200 - M.tvlog)
        print(f"CONTROL on {len(M):,} book trades: pgrid60 × 6e5 vs two-pass p_g60 max |Δ| {d1.max():.2e} bp/min (median {d1.median():.2e}); "
              f"vrate × 1200 vs tvlog max |Δ| {d2.max():.2e} (median {d2.median():.2e})")
    print(f"done {time.time() - T0:.0f}s")

main()
