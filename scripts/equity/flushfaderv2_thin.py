"""V2-48: THINNESS (gap_1200) as a sizing term, and grid slots for the volume RATE (user 2026-11-08: "The old FlushFader had a
gap_1200 feature which is probably the thinness check you're looking for. We should consider adding it as a sizing
feature, in the old system we used it as a gate, and it was a major one." + "Do the coarser slots help the volume slope
after the fixes or not?").
On the V2-44 reference book, at the signal bar, over the span of the last 1,200 PRESENT 1s bars (V2-47's window):
  gap_1200  = the engine's GapCounter(1200): missing seconds among the last 1,200 WALL seconds (base_v24's column; 0..1200)
  spangap   = the 1,200 bars' span in seconds − 1,200 (the missing seconds INSIDE the bar window)
  vr_g{k}   = the volume RATE on the fixed session grid floor((sec − 34200) / k): OLS of ln(slot volume) on the slot's
              time (grid index × k), per second × 1,200 (k = 1 is V2-47's tvlog)
  vr_n{k}   = the same with slot volume / its COVERED seconds (the newest slot runs only to the signal second, the oldest
              only from the window's first bar: their raw sums are short by construction)
  p_g60     = V2-47's 60s grid price slope (the price term of every model here)
Report: V2-42's holdout harness (each year out of sample once), on the V2-44 book; Δ against B = P60g + time vlog
(V2-47's best), the paired day bootstrap. Thinness enters as z(ln(1 + x)).
Run from research/:  python3 -u scripts/equity/flushfaderv2_thin.py --compute > logs/thin_compute.log 2>&1
                     python3 -u scripts/equity/flushfaderv2_thin.py > logs/thin.log 2>&1
"""
import argparse, json, time, duckdb, numpy as np, pandas as pd
ap = argparse.ArgumentParser()
ap.add_argument('--book', default='data/flushfaderv2/flushfaderv2_reference_book.parquet')
ap.add_argument('--bars', default='data/intraday_1s_slim')
ap.add_argument('--base', default='data/equity/flushfader/base_v24/trips_*.parquet')
ap.add_argument('--tslot', default='data/flushfaderv2/tslot_years.parquet')
ap.add_argument('--coef', default='models/flushfaderv2/v2_coefficients.json')
ap.add_argument('--out', default='data/flushfaderv2/thin.parquet')
ap.add_argument('--compute', action='store_true')
args = ap.parse_args()
CREDIT, YOUNG_SCALE, W = 0.001, 0.8, 1200
KV = [1, 5, 15, 30, 60, 120]

def ols(x, y):
    x = x - x.mean(); return (x @ (y - y.mean())) / (x @ x)

def compute():
    con = duckdb.connect(); con.execute("SET memory_limit='3GB'"); con.execute("SET threads=4")
    B = con.execute(f"SELECT symbol, trade_date::DATE::VARCHAR td, signal_sec FROM '{args.book}' ORDER BY td, symbol, signal_sec").df()
    out = []; T0 = time.time()
    for n_day, (day, g) in enumerate(B.groupby('td', sort=True)):
        tick = list(g.symbol.unique())
        bars = con.execute(f"SELECT ticker, bucket, volume FROM '{args.bars}/{day}.parquet' WHERE ticker IN ({','.join('?' * len(tick))}) "
                           "AND volume > 0 AND bucket >= 34200 ORDER BY ticker, bucket", tick).df()
        for sym, gt in g.groupby('symbol'):
            bt = bars[bars.ticker == sym]; sec = bt.bucket.values.astype(np.int64); v = bt.volume.values.astype(np.float64)
            for S in gt.signal_sec.values:
                S = int(S); i = int(np.searchsorted(sec, S)); o = dict(symbol=sym, td=day, signal_sec=S)
                if i < len(sec) and sec[i] == S and i >= W - 1:
                    ss, sv1 = sec[i - W + 1:i + 1], v[i - W + 1:i + 1]; first = int(ss[0])
                    o['spangap'] = (S - first + 1) - W
                    for k in KV:
                        gi = (ss - 34200) // k; j = gi - gi.min(); n = int(j.max()) + 1
                        sv = np.bincount(j, weights=sv1, minlength=n); m = sv > 0
                        # covered seconds of each grid slot within [first, S]
                        lo = np.maximum(34200 + (gi.min() + np.arange(n)) * k, first); hi = np.minimum(34200 + (gi.min() + np.arange(n) + 1) * k - 1, S)
                        cov = (hi - lo + 1).astype(float)
                        t = (np.arange(n)[m] * k).astype(float)
                        if m.sum() >= 3:
                            o[f'vr_g{k}'] = ols(t, np.log(sv[m])) * W
                            o[f'vr_n{k}'] = ols(t, np.log(sv[m] / cov[m])) * W
                out.append(o)
        if n_day % 200 == 0: print(f"  {day}  {n_day + 1}/{B.td.nunique()}  {time.time() - T0:.0f}s", flush=True)
    R = B.merge(pd.DataFrame(out), on=['symbol', 'td', 'signal_sec'], how='left')
    T = pd.read_parquet(args.tslot, columns=['symbol', 'td', 'signal_sec', 'tvlog']).merge(R, on=['symbol', 'td', 'signal_sec'])
    d = np.abs(T.vr_g1 - T.tvlog); print(f"CONTROL vr_g1 vs V2-47 tvlog: max |Δ| {d.max():.2e}; vr_n1 = vr_g1: {np.abs(T.vr_n1 - T.vr_g1).max():.2e}")
    print("coverage: " + ", ".join(f"{c} {R[c].notna().mean():.2%}" for c in R.columns[3:]))
    R.to_parquet(args.out, index=False); print(f"wrote {args.out}; done {time.time() - T0:.0f}s")

def fit_logistic(X, y, it=60):
    b = np.zeros(X.shape[1])
    for _ in range(it):
        eta = X @ b; mu = 1 / (1 + np.exp(-eta)); w = mu * (1 - mu) + 1e-12
        bn = np.linalg.solve((X * w[:, None]).T @ X, (X * w[:, None]).T @ (eta + (y - mu) / w))
        if np.max(np.abs(bn - b)) < 1e-10: return bn
        b = bn
    return b
def fit_loglink(X, y, it=60):
    b = np.zeros(X.shape[1]); b[0] = np.log(y.mean())
    for _ in range(it):
        eta = X @ b; mu = np.exp(eta)
        bn = np.linalg.solve((X * mu[:, None]).T @ X, (X * mu[:, None]).T @ (eta + (y - mu) / mu))
        if np.max(np.abs(bn - b)) < 1e-10: return bn
        b = bn
    return b
def pf1(r): g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l - 1
def zs(x):
    lo, hi = np.nanquantile(x, [0.01, 0.99]); c = np.clip(x, lo, hi); return np.nan_to_num((c - np.nanmean(c)) / np.nanstd(c))

def report():
    con = duckdb.connect(); C = json.load(open(args.coef))
    B = con.execute(f"""
      WITH h AS (SELECT DISTINCT ON (symbol, trade_date::VARCHAR, signal_sec) symbol, trade_date::VARCHAR AS td, signal_sec, gap_1200
                 FROM read_parquet('{args.base}', union_by_name = true)
                 WHERE (symbol, trade_date::VARCHAR) IN (SELECT symbol, trade_date::DATE::VARCHAR FROM '{args.book}'))
      SELECT b.*, v.* EXCLUDE (symbol, td, signal_sec), t.p_g60, t.tvlog, h.gap_1200
      FROM '{args.book}' b
      LEFT JOIN '{args.out}' v ON v.symbol = b.symbol AND v.td = b.trade_date::DATE::VARCHAR AND v.signal_sec = b.signal_sec
      LEFT JOIN '{args.tslot}' t ON t.symbol = b.symbol AND t.td = b.trade_date::DATE::VARCHAR AND t.signal_sec = b.signal_sec
      LEFT JOIN h ON h.symbol = b.symbol AND h.td = b.trade_date::DATE::VARCHAR AND h.signal_sec = b.signal_sec""").df()
    B = B.sort_values(['trade_date', 'symbol', 'signal_sec']).reset_index(drop=True)
    R = (B.ret_exit.values + 2 * CREDIT / B.entry_px.values) * 100; WIN = R > 0
    YR = B.trade_date.astype(str).str[:4].astype(int).values; young = B.young.values.astype(bool)
    print(f"{len(B):,} trades; gap_1200 joined {B.gap_1200.notna().mean():.2%}: p10/50/90 {B.gap_1200.quantile(.1):.0f} / {B.gap_1200.median():.0f} / "
          f"{B.gap_1200.quantile(.9):.0f}; spangap p10/50/90 {B.spangap.quantile(.1):.0f} / {B.spangap.median():.0f} / {B.spangap.quantile(.9):.0f}")
    print("Spearman: " + ", ".join(f"{a}~{b} {B[a].corr(B[b], method='spearman'):+.3f}" for a, b in
          [('gap_1200', 'spangap'), ('gap_1200', 'tvlog'), ('gap_1200', 'vlog'), ('spangap', 'tvlog'), ('gap_1200', 'volat'), ('gap_1200', 'p_g60'), ('tvlog', 'vlog')]) + "\n")
    Z = {'s20': zs(B.s20.values), 'vlog': zs(B.vlog.values.astype(float)), 'cus': zs(B.cusr_dn_k025.values.astype(float)),
         'p60': zs(B.p_g60.values), 'tvlog': zs(B.tvlog.values), 'gap': zs(np.log1p(B.gap_1200.values.astype(float))),
         'span': zs(np.log1p(B.spangap.values.astype(float)))}
    for k in KV:
        for m in 'gn': Z[f'vr_{m}{k}'] = zs(B[f'vr_{m}{k}'].values.astype(float))
    gb = B.gap_band.values
    common = [np.ones(len(B)), gb == 1, gb == 2, np.log(np.clip(B.volat.values, *C['volat_clip_bp'])), B.rate600_fast.values, B.s_tier.values, B.dslo_flag.values]
    def predict(Xm, f):
        bp = fit_logistic(Xm[f], WIN[f] * 1.0); bw = fit_loglink(Xm[f & WIN], R[f & WIN]); bl = fit_loglink(Xm[f & ~WIN], -R[f & ~WIN])
        p = 1 / (1 + np.exp(-(Xm @ bp))); return p * np.exp(Xm @ bw) / ((1 - p) * np.exp(Xm @ bl)) - 1, (bp, bw, bl)
    def run(terms):
        Xm = np.column_stack(common + [Z[t] for t in terms]).astype(float); x = np.zeros(len(B)); bb = []
        for fm in (YR <= 2023, YR > 2023):
            app = ~fm; mm, b = predict(Xm, fm & ~young); mm = np.where(young, YOUNG_SCALE * mm, mm); bb.append(b)
            x[np.flatnonzero(app)] = (np.clip(mm, .125, 2.0) * R)[app]
        return x, bb
    MODELS = {'A  production (index s20 + index vlog)': ['s20', 'cus', 'vlog'],
              'A + gap_1200': ['s20', 'cus', 'vlog', 'gap'],
              'A + spangap': ['s20', 'cus', 'vlog', 'span'],
              'B  P60g + time vlog (V2-47)': ['p60', 'cus', 'tvlog'],
              'P60g + index vlog': ['p60', 'cus', 'vlog'],
              'P60g + index vlog + gap_1200': ['p60', 'cus', 'vlog', 'gap'],
              'P60g + index vlog + spangap': ['p60', 'cus', 'vlog', 'span'],
              'B + gap_1200': ['p60', 'cus', 'tvlog', 'gap'],
              'B + spangap': ['p60', 'cus', 'tvlog', 'span']}
    for k in KV[1:]:
        MODELS[f'P60g + vol rate {k}s grid (raw sums)'] = ['p60', 'cus', f'vr_g{k}']
        MODELS[f'P60g + vol rate {k}s grid (per covered s)'] = ['p60', 'cus', f'vr_n{k}']
    X = {}; BB = {}
    for n, t in MODELS.items(): X[n], BB[n] = run(t)
    years = sorted(set(YR)); A = X['A  production (index s20 + index vlog)']; Bm = X['B  P60g + time vlog (V2-47)']
    ud, inv = np.unique(B.trade_date.values, return_inverse=True); rng = np.random.default_rng(20261108)
    picks = np.stack([np.bincount(rng.integers(0, len(ud), len(ud)), minlength=len(ud))[inv] for _ in range(2000)]).astype(float)
    def pfw(x): g = picks @ np.where(x > 0, x, 0); l = -(picks @ np.where(x < 0, x, 0)); return g / l - 1
    pA, pB = pfw(A), pfw(Bm)
    print("**Out-of-sample sized PF−1** (each year predicted once), Δ vs A and vs B with the paired day bootstrap; the LAST term's "
          "p / W / L × per SD (fwd; mirror)\n")
    print("| model | all | " + " | ".join(map(str, years)) + " | Δ vs A [90 %] | Δ vs B [90 %] · share > 0 | Δ net vs B | last term p / W / L |\n|---|---|" + "---|" * (len(years) + 4))
    for n, x in X.items():
        dA = pfw(x) - pA; dB = pfw(x) - pB
        bt = '; '.join(' / '.join(f"{np.exp(b[-1]):.3f}" for b in bb) for bb in BB[n])
        print(f"| {n} | {pf1(x):.4f} | " + " | ".join(f"{pf1(x[YR == y]):.3f}" for y in years)
              + f" | {pf1(x) - pf1(A):+.4f} [{np.quantile(dA, .05):+.4f}, {np.quantile(dA, .95):+.4f}] | {pf1(x) - pf1(Bm):+.4f} "
              f"[{np.quantile(dB, .05):+.4f}, {np.quantile(dB, .95):+.4f}] · {100 * (dB > 0).mean():.0f} % | {(x - Bm).sum():+,.0f} | {bt} |", flush=True)

compute() if args.compute else report()
