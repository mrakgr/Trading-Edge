"""V2-45: the price and volume slopes (s20, vlog) built from 2s / 5s / 10s / 15s SLOTS instead of 1s bars (user 2026-11-08:
"research if using 2s, 5s, 10s or 15s slots for the price and volume slope works better than using 1s bars ... test
alternative methods of creating them so that the bars are both in bar clock and wall clock time. In bar clock time, the
slots should not contain 1s bars with 0 volume, but in wall clock time they could, but otherwise in both cases we should
filter out the slots that have volume of 0").
For every production book trade (the V2-44 reference, 35,519), from the 1s bars from 09:30 (SessionStartSec), AT THE SIGNAL
BAR (its own bar folded in), k in {1, 2, 5, 10, 15}:
  bar  = BAR CLOCK: slots of k consecutive PRESENT 1s bars counted back from the signal bar; the window = the last 1,200
         present bars (= 1,200 / k slots; full window only, as production). k = 1 IS production (s20, vlog): the control.
  wall = WALL CLOCK: slots of k seconds ending at the signal second, over the last 1,200 wall seconds (20 minutes, not
         before 09:30); a slot may hold silent seconds; slots with zero volume dropped; >= 10 slots needed.
  span = WALL CLOCK over the SAME span the 1,200 present bars cover (the first of them .. the signal second): separates
         the clock from the window length (on a thin name 1,200 present bars are far more than 20 minutes).
Slot price = its volume-weighted vwap, slot volume = its sum. Price slope = OLS of ln(slot vwap) on the slot's index (bar)
or its time (wall/span), per 1s, x 6e5 (bp/min as s20). Volume slope = OLS of ln(slot volume) x the window length (the
fitted ln change across it, as vlog). Unweighted OLS on the kept slots.
Report: V2-42's holdout harness (fit p / W / L on one year block's non-young trades, apply to the other: fwd 2020–23 ->
2024–26, mirror the reverse; young x 0.8; units clip(PF−1, 0.125, 2); paired day bootstrap against production). Every
term (production's included) is standardised the same way (winsorised at the book's 1 % / 99 %, nan -> 0), so `bar 1`
reproduces production exactly. Families: the price slope replaced (s20 -> variant), the volume slope replaced
(vlog -> variant), both replaced by the same construction.
Run from research/:  python3 -u scripts/equity/flushfaderv2_slot_slopes.py --compute > logs/slot_slopes_compute.log 2>&1
                     python3 -u scripts/equity/flushfaderv2_slot_slopes.py > logs/slot_slopes.log 2>&1
"""
import argparse, json, time, duckdb, numpy as np, pandas as pd
ap = argparse.ArgumentParser()
ap.add_argument('--book', default='data/flushfaderv2/flushfaderv2_reference_book.parquet')
ap.add_argument('--bars', default='data/intraday_1s_slim')
ap.add_argument('--coef', default='models/flushfaderv2/v2_coefficients.json')
ap.add_argument('--out', default='data/flushfaderv2/slot_slopes.parquet')
ap.add_argument('--compute', action='store_true')
args = ap.parse_args()
CREDIT, YOUNG_SCALE, W, KS, MIN_SLOTS = 0.001, 0.8, 1200, [1, 2, 5, 10, 15], 10
VARS = [f'{m}{k}' for m in ('bar', 'wall', 'span') for k in KS]

def ols(x, y):
    x = x - x.mean(); return (x @ (y - y.mean())) / (x @ x)

def slopes_from_slots(t, v, pv, length, per):
    """t = slot positions (index or seconds), v / pv = slot volume / vwap x volume; drop empty slots. -> (price, volume)"""
    m = v > 0
    if m.sum() < MIN_SLOTS: return np.nan, np.nan
    t, v, pv = t[m].astype(float), v[m], pv[m]
    return ols(t, np.log(pv / v)) / per * 6e5, ols(t, np.log(v)) * length

def compute():
    con = duckdb.connect(); con.execute("SET memory_limit='3GB'"); con.execute("SET threads=4")
    B = con.execute(f"SELECT symbol, trade_date::DATE::VARCHAR td, signal_sec, s20, vlog FROM '{args.book}' ORDER BY td, symbol, signal_sec").df()
    print(f"{len(B):,} trades, {B.td.nunique():,} days", flush=True)
    out = []; T0 = time.time()
    for n_day, (day, g) in enumerate(B.groupby('td', sort=True)):
        tick = list(g.symbol.unique())
        bars = con.execute(f"SELECT ticker, bucket, volume, vwap FROM '{args.bars}/{day}.parquet' WHERE ticker IN ({','.join('?' * len(tick))}) "
                           "AND volume > 0 AND bucket >= 34200 ORDER BY ticker, bucket", tick).df()
        for sym, gt in g.groupby('symbol'):
            bt = bars[bars.ticker == sym]; sec = bt.bucket.values.astype(np.int64)
            v = bt.volume.values.astype(np.float64); pv = bt.vwap.values.astype(np.float64) * v
            for row in gt.itertuples():
                S = int(row.signal_sec); i = int(np.searchsorted(sec, S)); o = dict(symbol=sym, td=day, signal_sec=S)
                if not (i < len(sec) and sec[i] == S): out.append(o); continue
                if i >= W - 1:   # BAR CLOCK: k present bars per slot, oldest slot first
                    wv, wp = v[i - W + 1:i + 1], pv[i - W + 1:i + 1]
                    for k in KS:
                        n = W // k; sv = wv.reshape(n, k).sum(1); sp = wp.reshape(n, k).sum(1)
                        o[f'p_bar{k}'], o[f'v_bar{k}'] = slopes_from_slots(np.arange(n), sv, sp, n, k)
                for mode, start in (('wall', max(34200, S - W + 1)), ('span', sec[i - W + 1] if i >= W - 1 else None)):
                    if start is None: continue
                    lo = int(np.searchsorted(sec, start)); ss, sv1, sp1 = sec[lo:i + 1], v[lo:i + 1], pv[lo:i + 1]
                    for k in KS:   # WALL CLOCK: slot j covers (S - k(j+1), S - kj], j = 0 the newest
                        n = (S - start) // k + 1; j = (S - ss) // k
                        sv = np.bincount(j, weights=sv1, minlength=n)[:n][::-1]; sp = np.bincount(j, weights=sp1, minlength=n)[:n][::-1]
                        o[f'p_{mode}{k}'], o[f'v_{mode}{k}'] = slopes_from_slots(np.arange(n) * k, sv, sp, n * k, 1)   # t in seconds
                out.append(o)
        if n_day % 200 == 0: print(f"  {day}  {n_day + 1}/{B.td.nunique()}  {time.time() - T0:.0f}s", flush=True)
    R = B.merge(pd.DataFrame(out), on=['symbol', 'td', 'signal_sec'], how='left')
    for c, ref in (('p_bar1', 's20'), ('v_bar1', 'vlog')):
        d = np.abs(R[c] - R[ref]); print(f"CONTROL {c} vs the book's {ref}: max |Δ| {d.max():.2e}, median {d.median():.2e}, NaN {R[c].isna().sum()}")
    print("coverage: " + ", ".join(f"{c} {R['p_' + c].notna().mean():.1%}" for c in VARS))
    R.drop(columns=['s20', 'vlog']).to_parquet(args.out, index=False); print(f"wrote {args.out}; done {time.time() - T0:.0f}s")

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
    B = con.execute(f"SELECT b.*, {', '.join(f'v.p_{c}, v.v_{c}' for c in VARS)} FROM '{args.book}' b LEFT JOIN '{args.out}' v "
                    "ON v.symbol = b.symbol AND v.td = b.trade_date::DATE::VARCHAR AND v.signal_sec = b.signal_sec").df()
    B = B.sort_values(['trade_date', 'symbol', 'signal_sec']).reset_index(drop=True)
    R = (B.ret_exit.values + 2 * CREDIT / B.entry_px.values) * 100; WIN = R > 0
    YR = B.trade_date.astype(str).str[:4].astype(int).values; young = B.young.values.astype(bool)
    print(f"{len(B):,} trades (the V2-44 reference book)\n")
    Z = {'s20': zs(B.s20.values), 'vlog': zs(B.vlog.values.astype(float)), 'cus': zs(B.cusr_dn_k025.values.astype(float))}
    for c in VARS: Z['p_' + c] = zs(B['p_' + c].values.astype(float)); Z['v_' + c] = zs(B['v_' + c].values.astype(float))
    print("| variant | price: coverage · Spearman with s20 · p10 / p50 / p90 (bp/min) | volume: coverage · Spearman with vlog · p10 / p50 / p90 |\n|---|---|---|")
    for c in VARS:
        p, q = B['p_' + c], B['v_' + c]
        print(f"| {c} | {p.notna().mean():.1%} · {p.corr(B.s20, method='spearman'):.3f} · {p.quantile(.1):+.1f} / {p.quantile(.5):+.1f} / {p.quantile(.9):+.1f} | "
              f"{q.notna().mean():.1%} · {q.corr(B.vlog, method='spearman'):.3f} · {q.quantile(.1):+.2f} / {q.quantile(.5):+.2f} / {q.quantile(.9):+.2f} |")
    gb = B.gap_band.values
    common = [np.ones(len(B)), gb == 1, gb == 2, np.log(np.clip(B.volat.values, *C['volat_clip_bp'])), B.rate600_fast.values, B.s_tier.values, B.dslo_flag.values]
    def predict(Xm, f):
        bp = fit_logistic(Xm[f], WIN[f] * 1.0); bw = fit_loglink(Xm[f & WIN], R[f & WIN]); bl = fit_loglink(Xm[f & ~WIN], -R[f & ~WIN])
        p = 1 / (1 + np.exp(-(Xm @ bp))); return p * np.exp(Xm @ bw) / ((1 - p) * np.exp(Xm @ bl)) - 1
    def run(terms):
        Xm = np.column_stack(common + [Z[t] for t in terms]).astype(float); x = np.zeros(len(B)); o = {}
        for h, fm in [('fwd', YR <= 2023), ('mirror', YR > 2023)]:
            app = ~fm; mm = predict(Xm, fm & ~young); mm = np.where(young, YOUNG_SCALE * mm, mm)
            u = np.clip(mm, .125, 2.0)[app]; ru = u * R[app]; x[np.flatnonzero(app)] = ru; o[h] = (pf1(ru), ru.sum(), u.sum())
        return o, x
    oA, base = run(['s20', 'cus', 'vlog'])
    ud, inv = np.unique(B.trade_date.values, return_inverse=True); rng = np.random.default_rng(20261108)
    picks = np.stack([np.bincount(rng.integers(0, len(ud), len(ud)), minlength=len(ud))[inv] for _ in range(2000)]).astype(float)
    def pfw(x):  # PF−1 of x under every bootstrap weight at once
        g = picks @ np.where(x > 0, x, 0); l = -(picks @ np.where(x < 0, x, 0)); return g / l - 1
    pA = pfw(base)
    per = {'2020–21': YR <= 2021, '2022–23': (YR >= 2022) & (YR <= 2023), '2024': YR == 2024, '2025': YR == 2025, '2026': YR == 2026}
    print(f"\nA = production (s20 + CUSUM + vlog), refit the same way: sized PF−1 fwd {oA['fwd'][0]:.4f}, mirror {oA['mirror'][0]:.4f}, "
          f"net {oA['fwd'][1] + oA['mirror'][1]:,.0f} units·% ({(oA['fwd'][1] + oA['mirror'][1]) / (oA['fwd'][2] + oA['mirror'][2]):.4f} per unit-trade); "
          "by period " + " · ".join(f"{k} {base[m].sum():+,.0f}" for k, m in per.items()))
    fams = {'PRICE slope replaced (s20 → variant; CUSUM, vlog kept)': lambda c: ['p_' + c, 'cus', 'vlog'],
            'VOLUME slope replaced (vlog → variant; s20, CUSUM kept)': lambda c: ['s20', 'cus', 'v_' + c],
            'BOTH replaced by the same construction': lambda c: ['p_' + c, 'cus', 'v_' + c]}
    for fam, terms in fams.items():
        print(f"\n**{fam}** — out-of-sample, fwd + mirror; Δ against A; bootstrap = paired day resamples (2,000)\n")
        print("| variant | sized PF−1 fwd | mirror | mean | Δ PF−1 | 90 % interval | share > 0 | Δ net (units·%) | net per unit-trade | Δ net by period: " + " · ".join(per) + " |")
        print("|---|---|---|---|---|---|---|---|---|---|")
        for c in VARS:
            o, x = run(terms(c)); d = pfw(x) - pA; f, mi = o['fwd'], o['mirror']
            print(f"| {c} | {f[0]:.4f} | {mi[0]:.4f} | {(f[0] + mi[0]) / 2:.4f} | {pf1(x) - pf1(base):+.4f} | [{np.quantile(d, .05):+.4f}, {np.quantile(d, .95):+.4f}] | "
                  f"{100 * (d > 0).mean():.0f} % | {(x - base).sum():+,.0f} | {(f[1] + mi[1]) / (f[2] + mi[2]):.4f} | "
                  + " · ".join(f"{(x - base)[m].sum():+,.0f}" for m in per.values()) + " |", flush=True)

compute() if args.compute else report()
