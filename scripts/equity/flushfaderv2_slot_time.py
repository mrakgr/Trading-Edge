"""V2-50: WHERE a slot sits on the time axis — the slot's START (V2-47..V2-49, production as built) or its bars'
VOLUME-WEIGHTED MEAN time (Σ sec·vol / Σ vol; a partial newest candle then sits where its trading happened) — for the
price slope (grid 15 / 30 / 60 / 120 s) and the slotted volume rate (grid 5 / 15 / 30 / 60 s) (user 2026-11-08: "Yeah, we
should try it for both the price and volume slopes."). Window and slots as V2-47 (the last 1,200 present bars; fixed
grid floor((sec − 34200) / k); partial newest / oldest kept; zero-volume slots absent). B = the 60s start-time price
slope + the 1s volume rate (the build). V2-47's harness on the V2-44 book.
Run from research/:  python3 -u scripts/equity/flushfaderv2_slot_time.py --compute > logs/slot_time_compute.log 2>&1
                     python3 -u scripts/equity/flushfaderv2_slot_time.py > logs/slot_time.log 2>&1
"""
import argparse, json, time, duckdb, numpy as np, pandas as pd
ap = argparse.ArgumentParser()
ap.add_argument('--book', default='data/flushfaderv2/flushfaderv2_reference_book.parquet')
ap.add_argument('--bars', default='data/intraday_1s_slim')
ap.add_argument('--tslot', default='data/flushfaderv2/tslot_years.parquet')
ap.add_argument('--coef', default='models/flushfaderv2/v2_coefficients.json', help='only volat_clip_bp is read')
ap.add_argument('--out', default='data/flushfaderv2/slot_time.parquet')
ap.add_argument('--compute', action='store_true')
args = ap.parse_args()
CREDIT, YOUNG_SCALE, W = 0.001, 0.8, 1200
KP, KV = [15, 30, 60, 120], [5, 15, 30, 60]

def ols(x, y):
    x = x - x.mean(); return (x @ (y - y.mean())) / (x @ x)

def compute():
    con = duckdb.connect(); con.execute("SET memory_limit='2GB'"); con.execute("SET threads=3")
    B = con.execute(f"SELECT symbol, trade_date::DATE::VARCHAR td, signal_sec FROM '{args.book}' ORDER BY td, symbol, signal_sec").df()
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
                    ss, sv1, sp1 = sec[i - W + 1:i + 1], v[i - W + 1:i + 1], pv[i - W + 1:i + 1]; rel = (ss - 34200).astype(float)
                    for k in sorted(set(KP) | set(KV)):
                        gi = (ss - 34200) // k; j = gi - gi.min(); n = int(j.max()) + 1
                        sv = np.bincount(j, weights=sv1, minlength=n); sp = np.bincount(j, weights=sp1, minlength=n)
                        st = np.bincount(j, weights=rel * sv1, minlength=n); m = sv > 0
                        start = ((gi.min() + np.arange(n)) * k).astype(float)[m]; vwt = st[m] / sv[m]
                        if m.sum() >= 3:
                            if k in KP:
                                o[f'p_s{k}'] = ols(start, np.log(sp[m] / sv[m])) * 6e5; o[f'p_w{k}'] = ols(vwt, np.log(sp[m] / sv[m])) * 6e5
                            if k in KV:
                                o[f'v_s{k}'] = ols(start, np.log(sv[m])) * W; o[f'v_w{k}'] = ols(vwt, np.log(sv[m])) * W
                out.append(o)
        if n_day % 200 == 0: print(f"  {day}  {n_day + 1}/{B.td.nunique()}  {time.time() - T0:.0f}s", flush=True)
    R = B.merge(pd.DataFrame(out), on=['symbol', 'td', 'signal_sec'], how='left')
    T = pd.read_parquet(args.tslot, columns=['symbol', 'td', 'signal_sec', 'p_g60', 'p_g15']).merge(R, on=['symbol', 'td', 'signal_sec'])
    print(f"CONTROL start-time 60s / 15s vs V2-47 p_g60 / p_g15: max |Δ| {np.abs(T.p_s60 - T.p_g60).max():.2e} / {np.abs(T.p_s15 - T.p_g15).max():.2e}")
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
    B = con.execute(f"""SELECT b.*, v.* EXCLUDE (symbol, td, signal_sec), t.tvlog FROM '{args.book}' b
      LEFT JOIN '{args.out}' v ON v.symbol = b.symbol AND v.td = b.trade_date::DATE::VARCHAR AND v.signal_sec = b.signal_sec
      LEFT JOIN '{args.tslot}' t ON t.symbol = b.symbol AND t.td = b.trade_date::DATE::VARCHAR AND t.signal_sec = b.signal_sec""").df()
    B = B.sort_values(['trade_date', 'symbol', 'signal_sec']).reset_index(drop=True)
    R = (B.ret_exit.values + 2 * CREDIT / B.entry_px.values) * 100; WIN = R > 0
    YR = B.trade_date.astype(str).str[:4].astype(int).values; young = B.young.values.astype(bool)
    Z = {'cus': zs(B.cusr_dn_k025.values.astype(float)), 'tvlog': zs(B.tvlog.values)}
    for c in [c for c in B.columns if c[:2] in ('p_', 'v_') and c[2] in 'sw']: Z[c] = zs(B[c].values.astype(float))
    print(f"{len(B):,} trades; Spearman start vs vw-mean time: " + ", ".join(f"{c[0]}{k} {B[f'{c[0]}_s{k}'].corr(B[f'{c[0]}_w{k}'], method='spearman'):.4f}" for c, ks in (('p', KP), ('v', KV)) for k in ks) + "\n")
    gb = B.gap_band.values
    common = [np.ones(len(B)), gb == 1, gb == 2, np.log(np.clip(B.volat.values, *C['volat_clip_bp'])), B.rate600_fast.values, B.s_tier.values, B.dslo_flag.values]
    def predict(Xm, f):
        bp = fit_logistic(Xm[f], WIN[f] * 1.0); bw = fit_loglink(Xm[f & WIN], R[f & WIN]); bl = fit_loglink(Xm[f & ~WIN], -R[f & ~WIN])
        p = 1 / (1 + np.exp(-(Xm @ bp))); return p * np.exp(Xm @ bw) / ((1 - p) * np.exp(Xm @ bl)) - 1, (bp, bw, bl)
    def run(terms):
        Xm = np.column_stack(common + [Z[t] for t in terms]).astype(float); x = np.zeros(len(B))
        for fm in (YR <= 2023, YR > 2023):
            app = ~fm; mm, _ = predict(Xm, fm & ~young); mm = np.where(young, YOUNG_SCALE * mm, mm)
            x[np.flatnonzero(app)] = (np.clip(mm, .125, 2.0) * R)[app]
        return x
    MODELS = {'B  60s START price + 1s volume rate (the build)': ['p_s60', 'cus', 'tvlog']}
    for k in KP:
        if k != 60: MODELS[f'price {k}s START + 1s volume'] = [f'p_s{k}', 'cus', 'tvlog']
        MODELS[f'price {k}s VW-MEAN time + 1s volume'] = [f'p_w{k}', 'cus', 'tvlog']
    for k in KV:
        MODELS[f'60s START price + volume {k}s START'] = ['p_s60', 'cus', f'v_s{k}']
        MODELS[f'60s START price + volume {k}s VW-MEAN time'] = ['p_s60', 'cus', f'v_w{k}']
    MODELS['price 60s VW-MEAN + volume 5s VW-MEAN'] = ['p_w60', 'cus', 'v_w5']
    X = {n: run(t) for n, t in MODELS.items()}
    years = sorted(set(YR)); Bm = X['B  60s START price + 1s volume rate (the build)']
    ud, inv = np.unique(B.trade_date.values, return_inverse=True); rng = np.random.default_rng(20261108)
    picks = np.stack([np.bincount(rng.integers(0, len(ud), len(ud)), minlength=len(ud))[inv] for _ in range(2000)]).astype(float)
    def pfw(x): g = picks @ np.where(x > 0, x, 0); l = -(picks @ np.where(x < 0, x, 0)); return g / l - 1
    pB = pfw(Bm)
    print("| model | OOS sized PF−1 | " + " | ".join(map(str, years)) + " | Δ vs B [90 %] · share > 0 | Δ net vs B |\n|---|---|" + "---|" * (len(years) + 2))
    for n, x in X.items():
        d = pfw(x) - pB
        print(f"| {n} | {pf1(x):.4f} | " + " | ".join(f"{pf1(x[YR == y]):.3f}" for y in years)
              + f" | {pf1(x) - pf1(Bm):+.4f} [{np.quantile(d, .05):+.4f}, {np.quantile(d, .95):+.4f}] · {100 * (d > 0).mean():.0f} % | {(x - Bm).sum():+,.0f} |", flush=True)

compute() if args.compute else report()
