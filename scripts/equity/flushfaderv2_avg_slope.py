"""V2-41: the AVERAGE short-term slope (user 2026-11-08: "Instead of using leg counters it might be worth it to use the average
1m-5m slope over the last 20m or since the first 20m low ... Not the total slope, but a short-term average of it ... We should
try breaking down the average slope on top of them."). For every production book trade, recomputed from the 1s bars (every
present bar from 09:30, the engine's SessionStartSec), AT THE SIGNAL BAR (its own bar folded in):
  s_w[t]  = the OLS slope of ln(vwap) over the w present bars ending at t (w = 60/120/180/300; full window only; ln per bar,
            x 6e5 = bp/min) -- the engine's ols_slope_w: CONTROL against base_v24's ols_slope_60/300
  over two windows -- the last 1,200 present bars (20m) and the leg since its first 20m low (bars_since_first_low, base_v24):
  avg_w   = mean of s_w            ⚠ a LINEAR filter: an OLS slope's weights sum to zero, so the interior cancels and the mean is
                                   the window's end-to-end change with ~w-bar smoothed ends -- compared with ols_slope_1200 /
                                   ols_slope_since_flow below
  aabs_w  = mean of |s_w|          (path speed: does not telescope)
  fneg_w  = share of bars with s_w < 0   (persistence of the decline)
  ratio   = avg_w / aabs_w         (a slope-efficiency in [-1, 1])
Run from research/:  python3 -u scripts/equity/flushfaderv2_avg_slope.py [--report]
"""
import argparse, time, duckdb, numpy as np, pandas as pd
ap = argparse.ArgumentParser()
ap.add_argument('--book', default='data/flushfaderv2/flushfaderv2_reference_book.parquet')
ap.add_argument('--trips', default='data/equity/flushfader/base_v24/trips_*.parquet')
ap.add_argument('--bars', default='data/intraday_1s_slim')
ap.add_argument('--out', default='data/flushfaderv2/avg_slope.parquet')
ap.add_argument('--report', action='store_true')
args = ap.parse_args()
WS = [60, 120, 180, 300]
EXTRA = ['ols_slope_60', 'ols_slope_300', 'ols_slope_1200', 'ols_slope_since_flow', 'bars_since_first_low', 'lows_since_first_low',
         'lows_since_first_low_300', 'lows_since_first_low_600', 'lows_since_first_low_180', 'eff_10m', 'volat_20m', 'consol_5m_lag1m']

def roll_slope(y, w):
    x = np.arange(w) - (w - 1) / 2; k = x / (x @ x)
    s = np.full(len(y), np.nan)
    if len(y) >= w: s[w - 1:] = np.correlate(y, k, 'valid')
    return s

def run():
    con = duckdb.connect(); con.execute("SET memory_limit='3GB'"); con.execute("SET threads=4")
    B = con.execute(f"""
      WITH b AS (SELECT symbol, trade_date::DATE d, signal_sec, ret_exit, entry_px, pf1_fitted FROM '{args.book}'),
           t AS (SELECT DISTINCT ON (symbol, trade_date::DATE, signal_sec) symbol, trade_date::DATE d, signal_sec, {', '.join(EXTRA)}
                 FROM read_parquet('{args.trips}', union_by_name = true) WHERE (symbol, trade_date::DATE) IN (SELECT symbol, d FROM b))
      SELECT b.*, {', '.join('t.' + c for c in EXTRA)} FROM b LEFT JOIN t USING (symbol, d, signal_sec) ORDER BY d, symbol, signal_sec""").df()
    B['td'] = B.d.astype(str).str[:10]
    print(f"{len(B):,} trades, {B.td.nunique():,} days; base_v24 joined {B.ols_slope_60.notna().mean():.2%}", flush=True)
    out = []; T0 = time.time()
    for k, (day, g) in enumerate(B.groupby('td', sort=True)):
        tick = list(g.symbol.unique())
        bars = con.execute(f"SELECT ticker, bucket, vwap FROM '{args.bars}/{day}.parquet' WHERE ticker IN ({','.join('?' * len(tick))}) "
                           "AND volume > 0 AND bucket >= 34200 ORDER BY ticker, bucket", tick).df()
        for sym, gt in g.groupby('symbol'):
            bt = bars[bars.ticker == sym]; sec = bt.bucket.values.astype(np.int64); y = np.log(bt.vwap.values.astype(np.float64)); n = len(sec)
            S = {w: roll_slope(y, w) for w in WS}
            for row in gt.itertuples():
                i = np.searchsorted(sec, row.signal_sec); o = dict(symbol=sym, td=day, signal_sec=row.signal_sec)
                if i < n and sec[i] == row.signal_sec:
                    o['c60'] = S[60][i]; o['c300'] = S[300][i]
                    bsfl = row.bars_since_first_low
                    for tag, lo in (('20', i - 1199), ('fl', i - int(bsfl) if np.isfinite(bsfl) else None)):
                        if lo is None: continue
                        lo = max(lo, 0)
                        for w in WS:
                            v = S[w][lo:i + 1]; v = v[np.isfinite(v)]
                            if len(v) >= 10:
                                o[f'avg{w}_{tag}'] = v.mean(); o[f'aabs{w}_{tag}'] = np.abs(v).mean(); o[f'fneg{w}_{tag}'] = (v < 0).mean()
                out.append(o)
        if k % 100 == 0: print(f"  {day}  {k + 1}/{B.td.nunique()}  {time.time() - T0:.0f}s", flush=True)
    R = B.merge(pd.DataFrame(out), on=['symbol', 'td', 'signal_sec'], how='left'); R.to_parquet(args.out, index=False)
    for c, e in (('c60', 'ols_slope_60'), ('c300', 'ols_slope_300')):
        ok = R[c].notna() & R[e].notna(); d = np.abs(R[c][ok] - R[e][ok]) / np.maximum(np.abs(R[e][ok]), 1e-12)
        print(f"CONTROL {c} vs base_v24 {e}: {ok.sum():,} rows (of {R[e].notna().sum():,}), |rel| < 1e-9 on {(d < 1e-9).mean():.2%}, < 1e-6 on {(d < 1e-6).mean():.2%}, median {d.median():.1e}")
    print(f"done {time.time() - T0:.0f}s")

def pf(r): r = np.asarray(r); l = -r[r < 0].sum(); return np.inf if l == 0 else r[r > 0].sum() / l

def line(lab, rng, h):
    y = h.d.dt.year; rs = np.sort(h.r.values)
    return (f"| {lab} | {rng} | {len(h):,} | {100*h.r.mean():+.2f} % | {100*h.r.median():+.2f} % | {pf(h.r):.2f} | {pf(rs[int(.05*len(rs)):]):.2f} | "
            f"{1000*(h.r <= -.2).mean():.1f} | {h.peq.sum():+.0f} | {100*h.peq.mean():+.1f} | {pf(h[y <= 2022].r):.2f} | {pf(h[(y >= 2023) & (y <= 2024)].r):.2f} | "
            f"{pf(h[y == 2025].r):.2f} | {pf(h[y == 2026].r):.2f} |")
HDR = ("| band | range | trades | mean | median | PF | PF trim 5 % | ≤ −20 %/1,000 | % eq | bp eq / trade | 2020–22 | 2023–24 | 2025 | 2026 |\n"
       "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")
QE = [0, .1, .25, .5, .75, .9, 1]; QL = ['p0–10', 'p10–25', 'p25–50', 'p50–75', 'p75–90', 'p90–100']

def report():
    R = pd.read_parquet(args.out); R['d'] = pd.to_datetime(R.d)
    R['r'] = R.ret_exit + 2 * .001 / R.entry_px; R['peq'] = 15 * R.pf1_fitted.clip(.125, 2) * R.r
    bp = 6e5
    for tag in ('20', 'fl'):
        for w in WS:
            for c in (f'avg{w}_{tag}', f'aabs{w}_{tag}'): R[c] = R[c] * bp
            R[f'ratio{w}_{tag}'] = R[f'avg{w}_{tag}'] / R[f'aabs{w}_{tag}']
    R['tot20'] = R.ols_slope_1200 * bp; R['totfl'] = R.ols_slope_since_flow * bp; R['aeff'] = R.eff_10m.abs()
    print(f"computed: avg60_20 {R.avg60_20.notna().mean():.2%}, avg60_fl {R.avg60_fl.notna().mean():.2%} of {len(R):,}")
    sp = lambda a, b: R[[a, b]].corr('spearman').iloc[0, 1]
    print("\n**Spearman: each average against the TOTAL slope and the existing features**\n")
    print("| feature | ols_slope_1200 | ols_slope_since_flow | lows300 | lows600 | lows180 | lows (20m leg) | volat_20m | |eff10| | coil |\n|---|---|---|---|---|---|---|---|---|---|")
    for tag in ('20', 'fl'):
        for kind in ('avg', 'aabs', 'fneg', 'ratio'):
            for w in WS:
                c = f'{kind}{w}_{tag}'
                print(f"| {c} | {sp(c, 'tot20'):+.3f} | {sp(c, 'totfl'):+.3f} | {sp(c, 'lows_since_first_low_300'):+.2f} | {sp(c, 'lows_since_first_low_600'):+.2f} | "
                      f"{sp(c, 'lows_since_first_low_180'):+.2f} | {sp(c, 'lows_since_first_low'):+.2f} | {sp(c, 'volat_20m'):+.2f} | {sp(c, 'aeff'):+.2f} | {sp(c, 'consol_5m_lag1m'):+.2f} |")
    for c, lab in [('avg60_20', 'mean 1m slope over the last 20m (bp/min)'), ('avg300_20', 'mean 5m slope over the last 20m (bp/min)'),
                   ('avg60_fl', 'mean 1m slope since the first low (bp/min)'), ('avg300_fl', 'mean 5m slope since the first low (bp/min)'),
                   ('aabs60_20', 'mean |1m slope| over the last 20m (bp/min)'), ('aabs60_fl', 'mean |1m slope| since the first low (bp/min)'),
                   ('fneg60_20', 'share of the last 20m with a falling 1m slope'), ('fneg60_fl', 'share of the leg with a falling 1m slope'),
                   ('ratio60_20', 'mean / mean|·| of the 1m slope, last 20m'), ('ratio300_20', 'mean / mean|·| of the 5m slope, last 20m'),
                   ('tot20', 'CONTROL: the 20m OLS slope (bp/min)'), ('totfl', 'CONTROL: the OLS slope since the first low (bp/min)')]:
        h = R[R[c].notna()]; q = h[c].quantile(QE).values; q[0] -= 1e9; q[-1] += 1e9
        print(f"\n**{lab}** (`{c}`)\n\n{HDR}")
        for L, a, z in zip(QL, q[:-1], q[1:]):
            s = h[(h[c] > a) & (h[c] <= z)]; print(line(L, f"{s[c].min():.3g} … {s[c].max():.3g}", s))
    # on top of the leg counters: conditional terciles of each average WITHIN each lows300 band
    bands = [(6, 10), (10, 20), (20, 40), (40, 10**9)]
    for c in ['avg60_20', 'avg300_20', 'avg60_fl', 'aabs60_20', 'fneg60_fl', 'ratio60_20']:
        print(f"\n**lows300 band × `{c}` tercile within the band**\n\n{HDR}")
        for a, z in bands:
            h = R[(R.lows_since_first_low_300 >= a) & (R.lows_since_first_low_300 < z) & R[c].notna()]
            t = h[c].quantile([1 / 3, 2 / 3]).values
            for L, s in (('low', h[h[c] <= t[0]]), ('mid', h[(h[c] > t[0]) & (h[c] <= t[1])]), ('high', h[h[c] > t[1]])):
                print(line(f"lows300 {a}–{z - 1 if z < 1e8 else '∞'} · {L}", f"{s[c].min():.3g} … {s[c].max():.3g}", s))

report() if args.report else run()
