"""V2-40: the 20m STANDARD DEVIATION against volat_20m (user 2026-11-08: "Suppose we compared 20m standard deviation to the
20m volatility feature we're using based on 30s slot absolute log returns. What would the correlation between them be. Is
there any signal to be found when those measures diverge significantly?"). For every production book trade, recomputed
from the 1s bars (every present bar from 09:30, the engine's SessionStartSec: features fold from the RTH open), AT THE SIGNAL BAR (its own bar folded in):
  volat   = EmaHlMa(hl 40 slots) of |ln(slot vwap / previous slot vwap)|, 30-present-bar slot vwaps (the engine's
            volat_20m: CONTROL against base_v24's volat_20m)
  sd_slot = the same EWMA's CENTERED std of the signed slot returns (hl 40)  -> ratio k_slot = sd_slot / volat
            (Gaussian: sqrt(pi/2) = 1.2533; above = fat-tailed, jumpy slots)
  sd_1s   = std of the 1s ln-vwap returns over the last 1,200 present bars x sqrt(30) -> k_1s = sd_1s / volat
            (noise inside the slots against the moves between them: a variance ratio)
  sd_px   = std of ln(vwap) over the last 1,200 present bars -> k_px = sd_px / volat (the Bollinger sense: how far the
            price has travelled against its step size)
Run from research/:  python3 -u scripts/equity/flushfaderv2_sd_vs_volat.py [--report]
"""
import argparse, time, duckdb, numpy as np, pandas as pd
ap = argparse.ArgumentParser()
ap.add_argument('--book', default='data/flushfaderv2/flushfaderv2_reference_book.parquet')
ap.add_argument('--trips', default='data/equity/flushfader/base_v24/trips_*.parquet')
ap.add_argument('--bars', default='data/intraday_1s_slim')
ap.add_argument('--out', default='data/flushfaderv2/sd_vs_volat.parquet')
ap.add_argument('--report', action='store_true')
args = ap.parse_args()
ALPHA = 1 - 0.5 ** (1 / 40)

def run():
    con = duckdb.connect(); con.execute("SET memory_limit='3GB'"); con.execute("SET threads=4")
    B = con.execute(f"""
      WITH b AS (SELECT symbol, trade_date::DATE d, signal_sec, ret_exit, entry_px, pf1_fitted FROM '{args.book}'),
           t AS (SELECT DISTINCT ON (symbol, trade_date::DATE, signal_sec) symbol, trade_date::DATE d, signal_sec, volat_20m, consol_5m_lag1m,
                        eff_10m, rng_20m FROM read_parquet('{args.trips}', union_by_name = true) WHERE (symbol, trade_date::DATE) IN (SELECT symbol, d FROM b))
      SELECT b.*, t.volat_20m, t.consol_5m_lag1m, t.eff_10m, t.rng_20m FROM b LEFT JOIN t USING (symbol, d, signal_sec) ORDER BY d, symbol, signal_sec""").df()
    B['td'] = B.d.astype(str).str[:10]
    print(f"{len(B):,} trades, {B.td.nunique():,} days; base_v24 joined {B.volat_20m.notna().mean():.2%}", flush=True)
    out = []; T0 = time.time()
    for k, (day, g) in enumerate(B.groupby('td', sort=True)):
        tick = list(g.symbol.unique())
        bars = con.execute(f"SELECT ticker, bucket, vwap, volume FROM '{args.bars}/{day}.parquet' WHERE ticker IN ({','.join('?' * len(tick))}) "
                           "AND volume > 0 AND bucket >= 34200 ORDER BY ticker, bucket", tick).df()
        for sym, gt in g.groupby('symbol'):
            bt = bars[bars.ticker == sym]; sec = bt.bucket.values.astype(np.int64); vw = bt.vwap.values.astype(np.float64); vo = bt.volume.values.astype(np.float64)
            n = len(sec)
            # slot vwaps, completed at every 30th present bar (index 29, 59, ...)
            ns = n // 30; pv = (vw * vo)[:ns * 30].reshape(ns, 30).sum(1); vv = vo[:ns * 30].reshape(ns, 30).sum(1)
            sv = np.where(vv > 0, pv / np.where(vv > 0, vv, 1), np.nan); r = np.log(sv[1:] / sv[:-1])   # r[j] completes at slot j+1
            # EWMAs over r (bias-corrected), state after each slot return
            m1 = np.empty(len(r)); m_s = np.empty(len(r)); m_s2 = np.empty(len(r))
            a = b = c = d = 0.0
            for j in range(len(r)):
                x = r[j]
                if np.isfinite(x):
                    a = (1 - ALPHA) * a + ALPHA * abs(x); d = (1 - ALPHA) * d + ALPHA
                    b = (1 - ALPHA) * b + ALPHA * x; c = (1 - ALPHA) * c + ALPHA * x * x
                m1[j] = a / d if d > 0 else np.nan; m_s[j] = b / d if d > 0 else np.nan; m_s2[j] = c / d if d > 0 else np.nan
            lv = np.log(vw); r1 = np.diff(lv)
            for row in gt.itertuples():
                i = np.searchsorted(sec, row.signal_sec)
                o = dict(symbol=sym, td=day, signal_sec=row.signal_sec)
                if i < n and sec[i] == row.signal_sec:
                    js = (i + 1) // 30 - 2                                   # the last slot return completed at or before bar i
                    if js >= 0:
                        o['volat'] = m1[js]; o['sd_slot'] = np.sqrt(max(m_s2[js] - m_s[js] ** 2, 0))
                    lo = max(0, i - 1199)
                    o['sd_px'] = lv[lo:i + 1].std(ddof=1) if i - lo >= 10 else np.nan
                    o['sd_1s'] = r1[lo:i].std(ddof=1) * np.sqrt(30) if i - lo >= 10 else np.nan
                out.append(o)
        if k % 100 == 0: print(f"  {day}  {k + 1}/{B.td.nunique()}  {time.time() - T0:.0f}s", flush=True)
    R = B.merge(pd.DataFrame(out), on=['symbol', 'td', 'signal_sec'], how='left'); R.to_parquet(args.out, index=False)
    ok = R.volat.notna() & R.volat_20m.notna()
    rel = np.abs(R.volat[ok] / R.volat_20m[ok] - 1)
    print(f"CONTROL volat recompute vs base_v24 volat_20m: {ok.sum():,} rows, |rel| < 1e-9 on {(rel < 1e-9).mean():.2%}, < 1e-6 on {(rel < 1e-6).mean():.2%}, median {rel.median():.1e}")
    print(f"done {time.time() - T0:.0f}s")

def report():
    R = pd.read_parquet(args.out); R['d'] = pd.to_datetime(R.d)
    R['r'] = R.ret_exit + 2 * .001 / R.entry_px; R['peq'] = 15 * R.pf1_fitted.clip(.125, 2) * R.r
    V = R.volat_20m
    R['k_slot'] = R.sd_slot / V; R['k_1s'] = R.sd_1s / V; R['k_px'] = R.sd_px / V
    def pf(r): r = np.asarray(r); l = -r[r < 0].sum(); return np.inf if l == 0 else r[r > 0].sum() / l
    print("Spearman with volat_20m: " + ", ".join(f"{c} {R[[c, 'volat_20m']].corr('spearman').iloc[0, 1]:.3f}" for c in ['sd_slot', 'sd_1s', 'sd_px']))
    print("Pearson (log) with volat_20m: " + ", ".join(f"{c} {np.corrcoef(np.log(R[c].clip(1e-9)), np.log(V))[0, 1]:.3f}" for c in ['sd_slot', 'sd_1s', 'sd_px']))
    print("the ratios' quantiles: " + "; ".join(f"{c} p5 {R[c].quantile(.05):.2f} p50 {R[c].quantile(.5):.2f} p95 {R[c].quantile(.95):.2f}" for c in ['k_slot', 'k_1s', 'k_px']))
    R['aeff'] = R.eff_10m.abs()
    print("Spearman of the ratios with the existing features: " + "; ".join(
        f"{k}: coil {R[[k, 'consol_5m_lag1m']].corr('spearman').iloc[0, 1]:+.2f}, |eff10| {R[[k, 'aeff']].corr('spearman').iloc[0, 1]:+.2f}, rng20 {R[[k, 'rng_20m']].corr('spearman').iloc[0, 1]:+.2f}, volat {R[[k, 'volat_20m']].corr('spearman').iloc[0, 1]:+.2f}"
        for k in ['k_slot', 'k_1s', 'k_px']))
    for k in ['k_slot', 'k_1s', 'k_px']:
        q = R[k].quantile([0, .05, .1, .25, .5, .75, .9, .95, 1]).values; q[0] -= 1; q[-1] += 1
        print(f"\n**{k} quantile bands (production book)**\n\n| band | range | trades | mean | median | PF | PF trim 5 % | ≤ −20 %/1,000 | % eq | bp eq / trade | 2020–22 | 2023–24 | 2025 | 2026 |")
        print("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")
        labs = ['p0–5', 'p5–10', 'p10–25', 'p25–50', 'p50–75', 'p75–90', 'p90–95', 'p95–100']
        for lab, a, z in zip(labs, q[:-1], q[1:]):
            h = R[(R[k] > a) & (R[k] <= z)]; y = h.d.dt.year; rs = np.sort(h.r.values)
            print(f"| {lab} | {max(a, R[k].min()):.2f}–{min(z, R[k].max()):.2f} | {len(h):,} | {100*h.r.mean():+.2f} % | {100*h.r.median():+.2f} % | {pf(h.r):.2f} | {pf(rs[int(.05*len(rs)):]):.2f} | "
                  f"{1000*(h.r <= -.2).mean():.1f} | {h.peq.sum():+.0f} | {100*h.peq.mean():+.1f} | {pf(h[y <= 2022].r):.2f} | {pf(h[(y >= 2023) & (y <= 2024)].r):.2f} | {pf(h[y == 2025].r):.2f} | {pf(h[y == 2026].r):.2f} |")

report() if args.report else run()
