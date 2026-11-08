"""V2-47: the TIME-indexed slopes year by year — 1s vs 15s (and 20 / 30 / 60s) slots for the price slope, and the volume
slope time-indexed too (user 2026-11-08: "Since the volume slope works just a tiny bit better when using timestamp indexes
we might as well make use of them to make them symmetric with price slopes. Should we maybe use 15s slots for the price
slope? It does seem like the performance improves monotonically when we use coarser bars. Could we compare 1s with 15s
bars on a yearly basis?").
Window = the span of the last 1,200 PRESENT 1s bars (the first of them .. the signal second), V2-45's `span`. Slots:
  anchored k = k-second slots ENDING AT THE SIGNAL SECOND (V2-45's construction: boundaries depend on the signal second,
               so it is NOT an online quantity — it needs a recompute from the 1s bars at signal time)
  grid k     = k-second slots on the FIXED session grid floor((sec − 34200) / k) (the newest slot partial up to the signal
               bar, the oldest holding only the window's bars): what an ONLINE implementation computes
Slot price = its vwap, zero-volume slots dropped; OLS of ln(slot vwap) on the slot's time (anchored: its index × k; grid:
its grid index × k), per second, × 6e5. The volume slope, time-indexed: OLS of ln(1s bar volume) on the bar's second over
the same 1,200 bars, per second × 1,200 (`tvlog`).
Report: V2-42's holdout harness (fwd 2020–23 -> 2024–26, mirror the reverse; every year is predicted out of sample
exactly once), units clip(PF−1, 0.125, 2), young × 0.8, on the V2-44 reference book. A = production (index s20 + CUSUM +
index vlog). Per-year sized PF−1 and Δ net against A and against the 1s time slope; paired day bootstrap of each
variant against the 1s time slope (P1).
Run from research/:  python3 -u scripts/equity/flushfaderv2_tslot_years.py --compute > logs/tslot_years_compute.log 2>&1
                     python3 -u scripts/equity/flushfaderv2_tslot_years.py > logs/tslot_years.log 2>&1
"""
import argparse, json, time, duckdb, numpy as np, pandas as pd
ap = argparse.ArgumentParser()
ap.add_argument('--book', default='data/flushfaderv2/flushfaderv2_reference_book.parquet')
ap.add_argument('--bars', default='data/intraday_1s_slim')
ap.add_argument('--coef', default='models/flushfaderv2/v2_coefficients.json')
ap.add_argument('--out', default='data/flushfaderv2/tslot_years.parquet')
ap.add_argument('--compute', action='store_true')
args = ap.parse_args()
CREDIT, YOUNG_SCALE, W = 0.001, 0.8, 1200
KS = [1, 5, 15, 20, 30, 60, 90, 120, 180, 300]

def ols(x, y):
    x = x - x.mean(); return (x @ (y - y.mean())) / (x @ x)

def compute():
    con = duckdb.connect(); con.execute("SET memory_limit='3GB'"); con.execute("SET threads=4")
    B = con.execute(f"SELECT symbol, trade_date::DATE::VARCHAR td, signal_sec FROM '{args.book}' ORDER BY td, symbol, signal_sec").df()
    print(f"{len(B):,} trades, {B.td.nunique():,} days", flush=True)
    out = []; T0 = time.time()
    for n_day, (day, g) in enumerate(B.groupby('td', sort=True)):
        tick = list(g.symbol.unique())
        bars = con.execute(f"SELECT ticker, bucket, volume, vwap FROM '{args.bars}/{day}.parquet' WHERE ticker IN ({','.join('?' * len(tick))}) "
                           "AND volume > 0 AND bucket >= 34200 ORDER BY ticker, bucket", tick).df()
        for sym, gt in g.groupby('symbol'):
            bt = bars[bars.ticker == sym]; sec = bt.bucket.values.astype(np.int64)
            v = bt.volume.values.astype(np.float64); pv = bt.vwap.values.astype(np.float64) * v
            for S in gt.signal_sec.values:
                S = int(S); i = int(np.searchsorted(sec, S)); o = dict(symbol=sym, td=day, signal_sec=S)
                if i < len(sec) and sec[i] == S and i >= W - 1:
                    ss, sv1, sp1 = sec[i - W + 1:i + 1], v[i - W + 1:i + 1], pv[i - W + 1:i + 1]
                    o['tvlog'] = ols(ss.astype(float), np.log(sv1)) * W
                    for k in KS:
                        for mode, j in (('a', (S - ss) // k), ('g', (ss - 34200) // k)):
                            if mode == 'a': j = j.max() - j                       # oldest = 0
                            else: j = j - j.min()
                            n = int(j.max()) + 1
                            sv = np.bincount(j, weights=sv1, minlength=n); sp = np.bincount(j, weights=sp1, minlength=n); m = sv > 0
                            o[f'p_{mode}{k}'] = ols((np.arange(n)[m] * k).astype(float), np.log(sp[m] / sv[m])) * 6e5 if m.sum() >= 3 else np.nan
                out.append(o)
        if n_day % 200 == 0: print(f"  {day}  {n_day + 1}/{B.td.nunique()}  {time.time() - T0:.0f}s", flush=True)
    R = B.merge(pd.DataFrame(out), on=['symbol', 'td', 'signal_sec'], how='left')
    S = pd.read_parquet('data/flushfaderv2/slot_slopes.parquet', columns=['symbol', 'td', 'signal_sec', 'p_span1', 'p_span15', 'v_span1'])
    M = R.merge(S, on=['symbol', 'td', 'signal_sec'])
    for a, b in (('p_a1', 'p_span1'), ('p_a15', 'p_span15'), ('tvlog', 'v_span1')):
        d = np.abs(M[a] - M[b]); print(f"CONTROL {a} vs V2-45 {b}: max |Δ| {d.max():.2e}, median {d.median():.2e}")
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
    B = con.execute(f"SELECT b.*, v.* EXCLUDE (symbol, td, signal_sec) FROM '{args.book}' b LEFT JOIN '{args.out}' v "
                    "ON v.symbol = b.symbol AND v.td = b.trade_date::DATE::VARCHAR AND v.signal_sec = b.signal_sec").df()
    B = B.sort_values(['trade_date', 'symbol', 'signal_sec']).reset_index(drop=True)
    R = (B.ret_exit.values + 2 * CREDIT / B.entry_px.values) * 100; WIN = R > 0
    YR = B.trade_date.astype(str).str[:4].astype(int).values; young = B.young.values.astype(bool)
    Z = {'s20': zs(B.s20.values), 'vlog': zs(B.vlog.values.astype(float)), 'cus': zs(B.cusr_dn_k025.values.astype(float)),
         'tvlog': zs(B.tvlog.values.astype(float))}
    for k in KS:
        for m in 'ag': Z[f'p_{m}{k}'] = zs(B[f'p_{m}{k}'].values.astype(float))
    print(f"{len(B):,} trades (the V2-44 reference book); Spearman with the 1s time slope p_a1: "
          + ", ".join(f"{c} {B[c].corr(B.p_a1, method='spearman'):.3f}" for c in [f'p_{m}{k}' for k in KS for m in 'ag'] + ['s20'])
          + f"; tvlog vs vlog {B.tvlog.corr(B.vlog, method='spearman'):.3f}\n")
    gb = B.gap_band.values
    common = [np.ones(len(B)), gb == 1, gb == 2, np.log(np.clip(B.volat.values, *C['volat_clip_bp'])), B.rate600_fast.values, B.s_tier.values, B.dslo_flag.values]
    def predict(Xm, f):
        bp = fit_logistic(Xm[f], WIN[f] * 1.0); bw = fit_loglink(Xm[f & WIN], R[f & WIN]); bl = fit_loglink(Xm[f & ~WIN], -R[f & ~WIN])
        p = 1 / (1 + np.exp(-(Xm @ bp))); return p * np.exp(Xm @ bw) / ((1 - p) * np.exp(Xm @ bl)) - 1
    def run(terms):
        Xm = np.column_stack(common + [Z[t] for t in terms]).astype(float); x = np.zeros(len(B))
        for fm in (YR <= 2023, YR > 2023):
            app = ~fm; mm = predict(Xm, fm & ~young); mm = np.where(young, YOUNG_SCALE * mm, mm)
            x[np.flatnonzero(app)] = (np.clip(mm, .125, 2.0) * R)[app]
        return x
    MODELS = {'A  production (index s20, index vlog)': ['s20', 'cus', 'vlog'],
              'P1  time s20 1s, index vlog': ['p_a1', 'cus', 'vlog'],
              'P1V  time s20 1s, TIME vlog': ['p_a1', 'cus', 'tvlog']}
    for k in KS[1:]:
        MODELS[f'P{k}a V  time s20 {k}s anchored, TIME vlog'] = [f'p_a{k}', 'cus', 'tvlog']
        MODELS[f'P{k}g V  time s20 {k}s GRID, TIME vlog'] = [f'p_g{k}', 'cus', 'tvlog']
    MODELS['P1g V  time s20 1s grid (= 1s), TIME vlog'] = ['p_g1', 'cus', 'tvlog']
    X = {n: run(t) for n, t in MODELS.items()}
    years = sorted(set(YR)); A = X['A  production (index s20, index vlog)']; P = X['P1V  time s20 1s, TIME vlog']
    ud, inv = np.unique(B.trade_date.values, return_inverse=True); rng = np.random.default_rng(20261108)
    picks = np.stack([np.bincount(rng.integers(0, len(ud), len(ud)), minlength=len(ud))[inv] for _ in range(2000)]).astype(float)
    def pfw(x): g = picks @ np.where(x > 0, x, 0); l = -(picks @ np.where(x < 0, x, 0)); return g / l - 1
    pA, pP = pfw(A), pfw(P)
    print("**Out-of-sample sized PF−1 by year** (each year predicted once: 2020–23 by the 2024–26 fit, 2024–26 by the 2020–23 fit)\n")
    print("| model | all | " + " | ".join(map(str, years)) + " | years > A | years > P1V |\n|---|---|" + "---|" * (len(years) + 2))
    for n, x in X.items():
        ya = [pf1(x[YR == y]) for y in years]
        print(f"| {n} | {pf1(x):.4f} | " + " | ".join(f"{v:.3f}" for v in ya) + f" | {sum(pf1(x[YR == y]) > pf1(A[YR == y]) for y in years)} | "
              f"{sum(pf1(x[YR == y]) > pf1(P[YR == y]) for y in years)} |")
    print("\n**Δ sized net by year against P1V (time s20 1s + time vlog), units·%** — and the paired day bootstrap of the whole-period Δ PF−1\n")
    print("| model | Δ net all | " + " | ".join(map(str, years)) + " | Δ PF−1 vs P1V | 90 % | share > 0 | Δ PF−1 vs A | 90 % | share > 0 |\n|---|---|" + "---|" * (len(years) + 6))
    for n, x in X.items():
        dP = pfw(x) - pP; dA = pfw(x) - pA
        print(f"| {n} | {(x - P).sum():+,.0f} | " + " | ".join(f"{(x - P)[YR == y].sum():+,.0f}" for y in years)
              + f" | {pf1(x) - pf1(P):+.4f} | [{np.quantile(dP, .05):+.4f}, {np.quantile(dP, .95):+.4f}] | {100 * (dP > 0).mean():.0f} % "
              f"| {pf1(x) - pf1(A):+.4f} | [{np.quantile(dA, .05):+.4f}, {np.quantile(dA, .95):+.4f}] | {100 * (dA > 0).mean():.0f} % |", flush=True)

compute() if args.compute else report()
