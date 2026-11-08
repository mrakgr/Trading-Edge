"""V2-49: (a) is B (V2-47/48: the 60s grid time price slope + the 1s time volume rate) on top of vlog? — the chain from the
pre-vlog design, and B + vlog; (b) PARTIAL candles in the 60s grid price slope: the newest slot (runs only to the signal
second) and the oldest (holds only the window's bars) dropped or kept (user 2026-11-08: "So, we'll actually allow partial
candles for this feature? The slots we're using for volatility and coil features aren't like that and it should be worth
studying whether they should be.").
Run from research/:  python3 -u scripts/equity/flushfaderv2_partial.py --compute > logs/partial_compute.log 2>&1
                     python3 -u scripts/equity/flushfaderv2_partial.py > logs/partial.log 2>&1
"""
import argparse, json, time, duckdb, numpy as np, pandas as pd
ap = argparse.ArgumentParser()
ap.add_argument('--book', default='data/flushfaderv2/flushfaderv2_reference_book.parquet')
ap.add_argument('--bars', default='data/intraday_1s_slim')
ap.add_argument('--tslot', default='data/flushfaderv2/tslot_years.parquet')
ap.add_argument('--coef', default='models/flushfaderv2/v2_coefficients.json')
ap.add_argument('--out', default='data/flushfaderv2/partial.parquet')
ap.add_argument('--compute', action='store_true')
args = ap.parse_args()
CREDIT, YOUNG_SCALE, W, K = 0.001, 0.8, 1200, 60

def ols(x, y):
    x = x - x.mean(); return (x @ (y - y.mean())) / (x @ x)

def compute():
    con = duckdb.connect(); con.execute("SET memory_limit='3GB'"); con.execute("SET threads=4")
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
                    ss, sv1, sp1 = sec[i - W + 1:i + 1], v[i - W + 1:i + 1], pv[i - W + 1:i + 1]
                    gi = (ss - 34200) // K; j = gi - gi.min(); n = int(j.max()) + 1
                    sv = np.bincount(j, weights=sv1, minlength=n); sp = np.bincount(j, weights=sp1, minlength=n); m = sv > 0
                    newest_full = (S - 34200) % K == K - 1                 # the signal second ends its minute
                    oldest_full = (ss[0] - 34200) % K == 0                 # the window starts on a minute
                    t = np.arange(n) * K
                    for lab, keep in (('both', m), ('nonew', m & (np.arange(n) < n - 1 if not newest_full else True)),
                                      ('noold', m & (np.arange(n) > 0 if not oldest_full else True)),
                                      ('none', m & (np.arange(n) < n - 1 if not newest_full else True) & (np.arange(n) > 0 if not oldest_full else True))):
                        keep = np.asarray(keep, bool)
                        o['p_' + lab] = ols(t[keep].astype(float), np.log(sp[keep] / sv[keep])) * 6e5 if keep.sum() >= 3 else np.nan
                    o['new_share'] = sv[-1] / sv.sum(); o['n_slots'] = int(m.sum())
                out.append(o)
        if n_day % 200 == 0: print(f"  {day}  {n_day + 1}/{B.td.nunique()}  {time.time() - T0:.0f}s", flush=True)
    R = B.merge(pd.DataFrame(out), on=['symbol', 'td', 'signal_sec'], how='left')
    T = pd.read_parquet(args.tslot, columns=['symbol', 'td', 'signal_sec', 'p_g60']).merge(R, on=['symbol', 'td', 'signal_sec'])
    print(f"CONTROL p_both vs V2-47 p_g60: max |Δ| {np.abs(T.p_both - T.p_g60).max():.2e}")
    print("coverage: " + ", ".join(f"{c} {R[c].notna().mean():.2%}" for c in R.columns[3:]))
    print(f"slots per window p10/50/90 {R.n_slots.quantile(.1):.0f} / {R.n_slots.median():.0f} / {R.n_slots.quantile(.9):.0f}; "
          f"the newest (partial) slot's volume share p10/50/90 {R.new_share.quantile(.1):.3f} / {R.new_share.median():.3f} / {R.new_share.quantile(.9):.3f}")
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
    B = con.execute(f"""SELECT b.*, v.* EXCLUDE (symbol, td, signal_sec), t.p_g60, t.tvlog FROM '{args.book}' b
      LEFT JOIN '{args.out}' v ON v.symbol = b.symbol AND v.td = b.trade_date::DATE::VARCHAR AND v.signal_sec = b.signal_sec
      LEFT JOIN '{args.tslot}' t ON t.symbol = b.symbol AND t.td = b.trade_date::DATE::VARCHAR AND t.signal_sec = b.signal_sec""").df()
    B = B.sort_values(['trade_date', 'symbol', 'signal_sec']).reset_index(drop=True)
    R = (B.ret_exit.values + 2 * CREDIT / B.entry_px.values) * 100; WIN = R > 0
    YR = B.trade_date.astype(str).str[:4].astype(int).values; young = B.young.values.astype(bool)
    Z = {'s20': zs(B.s20.values), 'vlog': zs(B.vlog.values.astype(float)), 'cus': zs(B.cusr_dn_k025.values.astype(float)),
         'p60': zs(B.p_g60.values), 'tvlog': zs(B.tvlog.values)}
    for lab in ('both', 'nonew', 'noold', 'none'): Z['p_' + lab] = zs(B['p_' + lab].values.astype(float))
    print("Spearman with the kept-both slope: " + ", ".join(f"{c} {B[c].corr(B.p_both, method='spearman'):.4f}" for c in ('p_nonew', 'p_noold', 'p_none')) + "\n")
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
    MODELS = {'V2-25 design (index s20 + CUSUM, NO volume term)': ['s20', 'cus'],
              'A production (+ index vlog, V2-43)': ['s20', 'cus', 'vlog'],
              'B (60s grid price + 1s time volume rate)': ['p60', 'cus', 'tvlog'],
              'B + index vlog (both volume terms)': ['p60', 'cus', 'tvlog', 'vlog'],
              'B, price slot newest partial DROPPED': ['p_nonew', 'cus', 'tvlog'],
              'B, price slot oldest partial DROPPED': ['p_noold', 'cus', 'tvlog'],
              'B, both partial slots DROPPED': ['p_none', 'cus', 'tvlog']}
    X = {n: run(t) for n, t in MODELS.items()}
    years = sorted(set(YR)); base = X['V2-25 design (index s20 + CUSUM, NO volume term)']; A = X['A production (+ index vlog, V2-43)']; Bm = X['B (60s grid price + 1s time volume rate)']
    ud, inv = np.unique(B.trade_date.values, return_inverse=True); rng = np.random.default_rng(20261108)
    picks = np.stack([np.bincount(rng.integers(0, len(ud), len(ud)), minlength=len(ud))[inv] for _ in range(2000)]).astype(float)
    def pfw(x): g = picks @ np.where(x > 0, x, 0); l = -(picks @ np.where(x < 0, x, 0)); return g / l - 1
    p0, pA, pB = pfw(base), pfw(A), pfw(Bm)
    print("| model | OOS sized PF−1 | " + " | ".join(map(str, years)) + " | Δ vs no-volume [90 %] | Δ vs A [90 %] | Δ vs B [90 %] · share > 0 | net (units·%) |\n|---|---|" + "---|" * (len(years) + 4))
    for n, x in X.items():
        d0, dA, dB = pfw(x) - p0, pfw(x) - pA, pfw(x) - pB
        f = lambda d, ref: f"{pf1(x) - pf1(ref):+.4f} [{np.quantile(d, .05):+.4f}, {np.quantile(d, .95):+.4f}]"
        print(f"| {n} | {pf1(x):.4f} | " + " | ".join(f"{pf1(x[YR == y]):.3f}" for y in years) + f" | {f(d0, base)} | {f(dA, A)} | {f(dB, Bm)} · {100 * (dB > 0).mean():.0f} % | {x.sum():+,.0f} |", flush=True)

compute() if args.compute else report()
