"""V2-42: a 20m OLS slope of VOLUME as a sizing term on top of production (user 2026-11-08: "Would a 20m OLS slope of volume
rather than price make any difference in sizing on top of what we currently have?"). The production sizing model has no
volume term (rate600 is lows per bar). For every production book trade (35,796, ATR5 off), from the 1s bars from 09:30
(the engine's SessionStartSec), AT THE SIGNAL BAR (its own bar folded in):
  vlog  = OLS slope of ln(bar volume) over the last 1,200 present bars x 1,200 (the fitted ln change across the window;
          full window only)
  vrel  = OLS slope of bar volume over the same window x 1,200 / its mean (the fitted fractional change)
  vwall = 20 WALL-CLOCK minute bins ending at the signal second (silent seconds count as zero volume; bins before 09:30
          dropped, >= 10 needed): OLS slope of bin volume x 20 / the bins' mean
  v5_20 = ln(vol_300_bar / vol_1200_bar) from base_v24 (the last 5m's share of the 20m volume: the cheap control)
Each enters as production's s20 does: winsorised at the book's 1 % / 99 %, standardised, nan -> 0.
V2-20/V2-25's harness (flushfaderv2_cusum_holdout.py): fit p / W / L on one year block's non-young trades, apply to the
other (fwd 2020–23 -> 2024–26, mirror the reverse); young x 0.8; production units clip(PF−1, 0.125, 2); paired day
bootstrap against production. CONTROL: production refitted on the full non-young book = the book's pf1_model.
Run from research/:  python3 -u scripts/equity/flushfaderv2_vol_slope.py --compute;  ... (no flag) = the report
"""
import argparse, time, duckdb, numpy as np, pandas as pd
ap = argparse.ArgumentParser()
ap.add_argument('--book', default='data/flushfaderv2/flushfaderv2_reference_book.parquet')
ap.add_argument('--trips', default='data/equity/flushfader/base_v24/trips_*.parquet')
ap.add_argument('--bars', default='data/intraday_1s_slim')
ap.add_argument('--out', default='data/flushfaderv2/vol_slope.parquet')
ap.add_argument('--compute', action='store_true')
args = ap.parse_args()
CREDIT, YOUNG_SCALE, W = 0.001, 0.8, 1200
KW = (np.arange(W) - (W - 1) / 2); KW = KW / (KW @ KW)
K20 = (np.arange(20) - 9.5); K20 = K20 / (K20 @ K20)

def compute():
    con = duckdb.connect(); con.execute("SET memory_limit='3GB'"); con.execute("SET threads=4")
    B = con.execute(f"""
      WITH b AS (SELECT symbol, trade_date::DATE d, signal_sec FROM '{args.book}'),
           t AS (SELECT DISTINCT ON (symbol, trade_date::DATE, signal_sec) symbol, trade_date::DATE d, signal_sec, vol_300_bar, vol_1200_bar
                 FROM read_parquet('{args.trips}', union_by_name = true) WHERE (symbol, trade_date::DATE) IN (SELECT symbol, d FROM b))
      SELECT b.*, t.vol_300_bar, t.vol_1200_bar FROM b LEFT JOIN t USING (symbol, d, signal_sec) ORDER BY d, symbol, signal_sec""").df()
    B['td'] = B.d.astype(str).str[:10]
    print(f"{len(B):,} trades, {B.td.nunique():,} days; base_v24 joined {B.vol_1200_bar.notna().mean():.2%}", flush=True)
    out = []; T0 = time.time()
    for k, (day, g) in enumerate(B.groupby('td', sort=True)):
        tick = list(g.symbol.unique())
        bars = con.execute(f"SELECT ticker, bucket, volume FROM '{args.bars}/{day}.parquet' WHERE ticker IN ({','.join('?' * len(tick))}) "
                           "AND volume > 0 AND bucket >= 34200 ORDER BY ticker, bucket", tick).df()
        for sym, gt in g.groupby('symbol'):
            bt = bars[bars.ticker == sym]; sec = bt.bucket.values.astype(np.int64); v = bt.volume.values.astype(np.float64); n = len(sec)
            for row in gt.itertuples():
                i = np.searchsorted(sec, row.signal_sec); o = dict(symbol=sym, td=day, signal_sec=row.signal_sec)
                if i < n and sec[i] == row.signal_sec:
                    o['c1200'] = v[max(0, i - W + 1):i + 1].sum()
                    if i >= W - 1:
                        win = v[i - W + 1:i + 1]
                        o['vlog'] = (KW @ np.log(win)) * W; o['vrel'] = (KW @ win) * W / win.mean()
                    S = row.signal_sec; m = (sec > S - 1200) & (sec <= S)
                    b = np.bincount(((S - sec[m]) // 60).astype(int), weights=v[m], minlength=20)[:20][::-1]   # oldest -> newest
                    ok = np.array([S - 60 * (19 - j) - 59 >= 34200 for j in range(20)])
                    if ok.sum() >= 10 and b[ok].mean() > 0:
                        x = np.arange(20)[ok] - np.arange(20)[ok].mean(); o['vwall'] = (x @ b[ok]) / (x @ x) * 20 / b[ok].mean()
                out.append(o)
        if k % 200 == 0: print(f"  {day}  {k + 1}/{B.td.nunique()}  {time.time() - T0:.0f}s", flush=True)
    R = B.merge(pd.DataFrame(out), on=['symbol', 'td', 'signal_sec'], how='left')
    ok = R.c1200.notna() & R.vol_1200_bar.notna()
    print(f"CONTROL 1,200-bar volume vs base_v24 vol_1200_bar: equal on {(np.abs(R.c1200[ok] - R.vol_1200_bar[ok]) < 1e-6).mean():.2%} of {ok.sum():,}")
    R.drop(columns=['d']).to_parquet(args.out, index=False); print(f"done {time.time() - T0:.0f}s")

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
def maxdd(dates, r): d = pd.Series(r).groupby(dates).sum().sort_index().cumsum(); return (d.cummax() - d).max()
def zs(x):
    lo, hi = np.nanquantile(x, [0.01, 0.99]); c = np.clip(x, lo, hi); return np.nan_to_num((c - np.nanmean(c)) / np.nanstd(c))

def report():
    con = duckdb.connect()
    B = con.execute(f"SELECT b.*, v.vlog, v.vrel, v.vwall, v.vol_300_bar, v.vol_1200_bar FROM '{args.book}' b LEFT JOIN '{args.out}' v "
                    "ON v.symbol = b.symbol AND v.td = b.trade_date::DATE::VARCHAR AND v.signal_sec = b.signal_sec").df()
    B = B.sort_values(['trade_date', 'symbol', 'signal_sec']).reset_index(drop=True)
    R = (B.ret_exit.values + 2 * CREDIT / B.entry_px.values) * 100; WIN = R > 0
    YR = B.trade_date.astype(str).str[:4].astype(int).values; young = B.young.values.astype(bool)
    B['v5_20'] = np.log(B.vol_300_bar / B.vol_1200_bar)
    print(f"{len(B):,} trades; vlog {B.vlog.notna().mean():.2%}, vwall {B.vwall.notna().mean():.2%}, v5_20 {B.v5_20.notna().mean():.2%}")
    Z = {'s20': B.s20_z.values, 'cus': B.cus_z.values}
    for c in ['vlog', 'vrel', 'vwall', 'v5_20']: Z[c] = zs(B[c].values.astype(float))
    print("quantiles: " + "; ".join(f"{c} p10 {B[c].quantile(.1):+.2f} p50 {B[c].quantile(.5):+.2f} p90 {B[c].quantile(.9):+.2f}" for c in ['vlog', 'vrel', 'vwall', 'v5_20']))
    sp = pd.DataFrame({k: Z[k] for k in Z}).assign(volat=np.log(B.volat), rate600=B.rate600, dslo=B.dslo).corr('spearman')
    print("\nSpearman:\n" + sp.loc[['vlog', 'vrel', 'vwall', 'v5_20'], ['vlog', 'vrel', 'vwall', 'v5_20', 's20', 'cus', 'volat', 'rate600', 'dslo']].round(2).to_markdown())
    gb = B.gap_band.values
    common = [np.ones(len(B)), gb == 1, gb == 2, np.log(np.clip(B.volat.values, 40.0, 250.0)), B.rate600_fast.values, B.s_tier.values, B.dslo_flag.values]
    def X(terms): return np.column_stack(common + [Z[t] for t in terms]).astype(float)
    def predict(Xm, f):
        bp = fit_logistic(Xm[f], WIN[f] * 1.0); bw = fit_loglink(Xm[f & WIN], R[f & WIN]); bl = fit_loglink(Xm[f & ~WIN], -R[f & ~WIN])
        p = 1 / (1 + np.exp(-(Xm @ bp))); Wf = np.exp(Xm @ bw); Lf = np.exp(Xm @ bl)
        return p, Wf, Lf, p * Wf / ((1 - p) * Lf) - 1, (bp, bw, bl)
    # CONTROL: production refitted on the full non-young book reproduces its pf1_model
    p, Wf, Lf, m, _ = predict(X(['s20', 'cus']), ~young)
    d = np.abs(m - B.pf1_model.values)
    print(f"\nCONTROL production refit vs the book's pf1_model: max |Δ| {d.max():.2e}, median {np.median(d):.2e}")
    # calibration by band: the production model's predictions vs what happened
    for c in ['vlog', 'vwall', 'v5_20']:
        h = B[c]; q = h.quantile([0, .1, .25, .5, .75, .9, 1]).values; q[0] -= 1e9; q[-1] += 1e9
        print(f"\n**production's predictions vs realised, by {c} band** (non-young)\n\n| band | range | trades | win % real / model | mean win real / model | mean loss real / model | PF−1 real | model mean PF−1 | sized net (units·%) | sized net / trade |\n|---|---|---|---|---|---|---|---|---|---|")
        for L, a, z in zip(['p0–10', 'p10–25', 'p25–50', 'p50–75', 'p75–90', 'p90–100'], q[:-1], q[1:]):
            s = ((h > a) & (h <= z)).values & ~young; r = R[s]; u = np.clip(m[s], .125, 2)
            print(f"| {L} | {h[s].min():+.2f} … {h[s].max():+.2f} | {s.sum():,} | {100*WIN[s].mean():.1f} / {100*p[s].mean():.1f} | {r[r>0].mean():.2f} / {Wf[s & WIN].mean():.2f} | "
                  f"{-r[r<0].mean():.2f} / {Lf[s & ~WIN].mean():.2f} | {pf1(r):.3f} | {m[s].mean():.3f} | {(u*r).sum():+,.0f} | {(u*r).mean():+.3f} |")
    MODELS = {'A  production (s20 + cus)': ['s20', 'cus'], '+ vlog': ['s20', 'cus', 'vlog'], '+ vrel': ['s20', 'cus', 'vrel'],
              '+ vwall': ['s20', 'cus', 'vwall'], '+ v5_20 (5m share)': ['s20', 'cus', 'v5_20']}
    def run(terms):
        Xm = X(terms); out = {}; x = np.zeros(len(B)); betas = {}
        for h, fm in [('fwd', YR <= 2023), ('mirror', YR > 2023)]:
            app = ~fm; _, _, _, mm, bb = predict(Xm, fm & ~young); mm = np.where(young, YOUNG_SCALE * mm, mm)
            u = np.clip(mm, .125, 2.0)[app]; ru = u * R[app]; x[np.flatnonzero(app)] = ru
            out[h] = (pf1(ru), ru.sum(), maxdd(B.trade_date.values[app], ru), u.sum()); betas[h] = [b[-1] for b in bb]
        return out, x, betas
    print("\nfwd = fit 2020–23 → apply 2024–26; mirror = the reverse. Production units: weight = clip(PF−1, 0.125, 2); net, max DD in % of one unit.\n")
    print("| model | sized PF−1 fwd | mirror | **mean** | net fwd | mirror | **sum** | unit-trades | **net per unit-trade** | max DD fwd | mirror | new term p / W / L × per SD (fwd; mirror) |")
    print("|---|---|---|---|---|---|---|---|---|---|---|---|")
    res = {}
    for name, terms in MODELS.items():
        o, x, b = run(terms); res[name] = x; f, mi = o['fwd'], o['mirror']
        bt = '—' if name.startswith('A') else '; '.join(' / '.join(f"{np.exp(v):.3f}" for v in b[h]) for h in ('fwd', 'mirror'))
        print(f"| {name} | {f[0]:.3f} | {mi[0]:.3f} | **{(f[0]+mi[0])/2:.3f}** | {f[1]:,.0f} | {mi[1]:,.0f} | **{f[1]+mi[1]:,.0f}** | {f[3]+mi[3]:,.0f} | "
              f"**{(f[1]+mi[1])/(f[3]+mi[3]):.3f}** | {f[2]:,.0f} | {mi[2]:,.0f} | {bt} |")
    base = res['A  production (s20 + cus)']; ud, inv = np.unique(B.trade_date.values, return_inverse=True); rng = np.random.default_rng(20261108)
    picks = [np.bincount(rng.integers(0, len(ud), len(ud)), minlength=len(ud))[inv] for _ in range(2000)]
    print("\nPaired day-bootstrap against A (2,000 resamples of the trading days, out-of-sample trades):\n")
    print("| model | Δ PF−1 | 90 % interval | share > 0 | Δ net | 90 % interval |\n|---|---|---|---|---|---|")
    def pfw(x, w): g = (x * w)[x > 0].sum(); l = -(x * w)[x < 0].sum(); return g / l - 1
    for name in list(MODELS)[1:]:
        x = res[name]; dpf = np.array([pfw(x, w) - pfw(base, w) for w in picks]); dn = np.array([((x - base) * w).sum() for w in picks])
        print(f"| {name} | {pf1(x) - pf1(base):+.4f} | [{np.quantile(dpf, .05):+.4f}, {np.quantile(dpf, .95):+.4f}] | {100*(dpf > 0).mean():.0f} % | "
              f"{(x - base).sum():+,.0f} | [{np.quantile(dn, .05):+,.0f}, {np.quantile(dn, .95):+,.0f}] |")

    print("\nΔ sized net against A by period (out-of-sample trades; units·%) and A's own net:\n")
    per = {'2020–21': (YR <= 2021), '2022–23': (YR >= 2022) & (YR <= 2023), '2024': YR == 2024, '2025': YR == 2025, '2026': YR == 2026}
    print("| model | " + " | ".join(per) + " |\n|---|" + "---|" * len(per))
    print("| A (its net) | " + " | ".join(f"{base[m].sum():+,.0f}" for m in per.values()) + " |")
    for name in list(MODELS)[1:]:
        print(f"| {name} | " + " | ".join(f"{(res[name] - base)[m].sum():+,.0f}" for m in per.values()) + " |")

compute() if args.compute else report()
