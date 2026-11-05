"""FlushFaderV2 — V2-20: the raw downside CUSUM as a SIZING term, against and beside the 20m slope (user 2026-11-04:
"the faster the move against the trend, the better it is to fade it ... we're already using the 20m slope for sizing
in the production system, so it will be directly competing with it").

The production V2 book (data/flushfaderv2/dslo3/flushfaderv2_reference_book_g3.parquet, 35,185 trades) joined to the
S49an columns (base_v21 + the young splice base_v21_young, by symbol / trade_date / signal_sec):
    cus  = cusr_dn_k435   the raw downside CUSUM at k = 4.35 bp per bar: the bp of selling in excess of the median bar
                          move since it last reset (research flushfader_results.md §S49an)
    vel  = cus / max(cusr_dn_age_k435, 1)   its VELOCITY: bp per present bar since that reset
Each enters like s20: winsorised at the book's 1 % / 99 % quantiles and standardised by its mean / SD.
Models (every other term = production: gap bands, ln volat, rate600 fast, S tier, dslo >= 4 %):
    A s20 (production) · B cus instead of s20 · C s20 + cus · D vel instead of s20 · E s20 + vel
V2-16's method exactly (flushfaderv2_refit_holdout.py): fit p / W / L on one year block's barnum >= 22 trades, apply to
the other (fwd = fit 2020–23 → 2024–26; mirror = the reverse); young trades × 0.8; scored in PRODUCTION units
(weight = clip(PF−1, 0.125, 2), Sizer.fs), net and max DD in % of one unit.
Run from research/:  python -u scripts/equity/flushfaderv2_cusum_holdout.py > data/flushfaderv2/cusum_holdout.md; echo exit $?
S49ap / V2-21 (smaller k):  ... --base data/equity/flushfader/base_v22 --young data/equity/flushfader/young/base_v22_young --ks 025,05,1,2,435
V2-23 (the CUSUM age, rate600 continuous):  ... --base data/equity/flushfader/base_v23 --young data/equity/flushfader/young/base_v23_young --ks 025,05,1,2,435 --age
S49aq / V2-22 (the anchored slope):  ... --base data/equity/flushfader/base_v23 --young data/equity/flushfader/young/base_v23_young --ks 025,05,1,2 --aslope
"""
import duckdb, numpy as np, pandas as pd
CREDIT, YOUNG_SCALE = 0.001, 0.8

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
def pf1(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l - 1
def maxdd(dates, r):
    d = pd.Series(r).groupby(dates).sum().sort_index().cumsum(); return (d.cummax() - d).max()
def zs(x):
    lo, hi = np.nanquantile(x, [0.01, 0.99]); c = np.clip(x, lo, hi); return np.nan_to_num((c - np.nanmean(c)) / np.nanstd(c))

import argparse
ap = argparse.ArgumentParser()
ap.add_argument('--base', default='data/equity/flushfader/base_v21', help='the base with the CUSUM columns')
ap.add_argument('--book', default='data/flushfaderv2/dslo3/flushfaderv2_reference_book_g3.parquet', help='the V2 book (V2-25: the rebuilt one)')
ap.add_argument('--young', default='data/equity/flushfader/young/base_v21_young', help="'' = none (a base that holds the young trades too: base_v24)")
ap.add_argument('--ks', default='435', help="the CUSUM k's to test, comma-separated column suffixes (S49ap: 025,05,1,2,435)")
ap.add_argument('--age', action='store_true', help="V2-23 (user 2026-11-04): the bars since each k's down-CUSUM reset as a sizing "
                "term, ln(1 + age) standardised, beside s20 and beside s20 + the k level; and the rate600 term continuous "
                "(standardised) instead of / beside its binary 'fast' flag")
ap.add_argument('--aslope', action='store_true', help="V2-22 (user 2026-11-04): the OLS slope since each k's down-CUSUM reset "
                "(aslope_k*, base_v23): instead of s20, beside it, with the level, and all three")
a = ap.parse_args()
KS = a.ks.split(',')
cols = ', '.join(f'cusr_dn_k{k}, cusr_dn_age_k{k}' + (f', aslope_k{k}' if a.aslope else '') for k in KS)
con = duckdb.connect(); con.execute("SET memory_limit='6GB'")
# the rebuilt reference book (V2-25) already carries cusr_dn_k025: the book's own CUSUM columns are dropped, the --base's joined
bcols = con.execute(f"SELECT column_name FROM (DESCRIBE SELECT * FROM read_parquet('{a.book}'))").df().column_name.tolist()
own = [c for c in bcols if c.startswith(('cusr_', 'aslope_'))]
con.execute(f"CREATE TABLE b AS SELECT * {'EXCLUDE (' + ', '.join(own) + ')' if own else ''} FROM read_parquet('{a.book}')")
young_src = f" UNION ALL SELECT symbol, trade_date, signal_sec, {cols} FROM read_parquet('{a.young}/*.parquet')" if a.young else ""
B = con.execute(f"""
    SELECT b.*, {cols} FROM b JOIN (
        SELECT symbol, trade_date, signal_sec, {cols} FROM read_parquet('{a.base}/*.parquet'){young_src}
    ) t USING (symbol, trade_date, signal_sec)""").df()
assert len(B) == con.execute("SELECT count(*) FROM b").fetchone()[0], f"{len(B)} joined rows: the book's trades must join exactly once"
B = B.sort_values(["trade_date", "symbol", "signal_sec"]).reset_index(drop=True)
R = (B.ret_exit.values + 2 * CREDIT / B.entry_px.values) * 100; WIN = R > 0
YR = B.trade_date.str[:4].astype(int).values; young = B.young.values
Z = {'s20': B.s20_z.values}
for k in KS:
    cus = B[f'cusr_dn_k{k}'].values.astype(float)
    Z[f'cus{k}'] = zs(cus)
    Z[f'vel{k}'] = zs(cus / np.maximum(B[f'cusr_dn_age_k{k}'].values.astype(float), 1.0))
    if a.aslope: Z[f'as{k}'] = zs(B[f'aslope_k{k}'].values.astype(float))
    Z[f'age{k}'] = zs(np.log1p(B[f'cusr_dn_age_k{k}'].values.astype(float)))
Z['rate'] = zs(B.rate600.values.astype(float))
gb = B.gap_band.values
common = [np.ones(len(B)), gb == 1, gb == 2, np.log(np.clip(B.volat.values, 40.0, 250.0)), B.rate600_fast.values, B.s_tier.values, B.dslo_flag.values]
MODELS = {'A  s20 (production)': ['s20']}
for k in KS:
    MODELS[f's20 + cus k{k}'] = ['s20', f'cus{k}']
    if a.age:
        MODELS[f's20 + age k{k}'] = ['s20', f'age{k}']
        MODELS[f's20 + cus k{k} + age k{k}'] = ['s20', f'cus{k}', f'age{k}']
    elif a.aslope:
        MODELS[f'aslope k{k} instead of s20'] = [f'as{k}']
        MODELS[f's20 + aslope k{k}'] = ['s20', f'as{k}']
        MODELS[f'cus k{k} + aslope k{k} (no s20)'] = [f'cus{k}', f'as{k}']
        MODELS[f's20 + cus k{k} + aslope k{k}'] = ['s20', f'cus{k}', f'as{k}']
    else:
        MODELS[f's20 + vel k{k}'] = ['s20', f'vel{k}']
if a.age:
    MODELS['s20, rate600 continuous INSTEAD of the fast flag'] = ['s20', 'rate', '-fast']
    MODELS['s20 + rate600 continuous beside the fast flag'] = ['s20', 'rate']
def run(terms):
    base = [c for i, c in enumerate(common) if not ('-fast' in terms and i == 4)]     # common[4] = rate600 fast
    terms = [t for t in terms if t != '-fast']
    Xm = np.column_stack(base + [Z[t] for t in terms]).astype(float)
    out, betas = {}, {}
    for h, fit_m in [("fwd", YR <= 2023), ("mirror", YR > 2023)]:
        app = ~fit_m; f = fit_m & ~young
        bp = fit_logistic(Xm[f], WIN[f] * 1.0); bw = fit_loglink(Xm[f & WIN], R[f & WIN]); bl = fit_loglink(Xm[f & ~WIN], -R[f & ~WIN])
        p = 1 / (1 + np.exp(-(Xm @ bp))); lam = np.exp(Xm @ bl) / np.exp(Xm @ bw); m = p / ((1 - p) * lam) - 1
        m = np.where(young, YOUNG_SCALE * m, m)
        u = np.clip(m, 0.125, 2.0)[app]; ru = u * R[app]
        out[h] = (pf1(ru), ru.sum(), maxdd(B.trade_date.values[app], ru), u.sum())
        out[h + '_trades'] = (np.flatnonzero(app), ru)
        betas[h] = (bp[-len(terms):], bw[-len(terms):], bl[-len(terms):])
    return out, betas

print("# V2-20 — the raw downside CUSUM as a sizing term, against and beside the 20m slope\n")
print(f"The V2 book, {len(B):,} trades joined (of 35,185). fwd = fit 2020–23 → apply 2024–26; mirror = the reverse. "
      "PRODUCTION units: weight = clip(PF−1, 0.125, 2); net and max DD in % of one unit.\n")
print("Spearman with s20 on the book: " + ', '.join(
    f"{c} {pd.Series(Z['s20']).corr(pd.Series(Z[c]), method='spearman'):+.2f}" for c in Z if c != 's20') + "\n")
print("| model | sized PF−1 fwd | mirror | **mean** | net fwd | mirror | **sum** | unit-trades | **net per unit-trade** | max DD fwd | mirror |")
print("|---|---|---|---|---|---|---|---|---|---|---|")
allb = {}
for name, terms in MODELS.items():
    o, b = run(terms); allb[name] = ([t for t in terms if t != '-fast'], b)
    f, m = o['fwd'], o['mirror']
    print(f"| {name} | {f[0]:.3f} | {m[0]:.3f} | **{(f[0] + m[0]) / 2:.3f}** | {f[1]:,.0f} | {m[1]:,.0f} | **{f[1] + m[1]:,.0f}** | "
          f"{f[3] + m[3]:,.0f} | **{(f[1] + m[1]) / (f[3] + m[3]):.3f}** | {f[2]:,.0f} | {m[2]:,.0f} |")
# ---- is the difference from production more than noise? Each trade's sized return from its OUT-of-sample fit (fwd or
# mirror), the two models paired on the same trades; 2,000 resamples of the trading DAYS
def pertrade(terms):
    o, _ = run(terms); x = np.zeros(len(B))
    for h in ('fwd', 'mirror'):
        idx, ru = o[h + '_trades']; x[idx] = ru
    return x
base = pertrade(MODELS['A  s20 (production)'])
days = B.trade_date.values; ud, inv = np.unique(days, return_inverse=True)
rng = np.random.default_rng(20261104)
print("\nPaired day-bootstrap against A (2,000 resamples of the trading days; Δ = model − A on the out-of-sample trades):\n")
print("| model | Δ PF−1 (pooled) | 90 % interval | share of resamples > 0 | Δ net | 90 % interval |\n|---|---|---|---|---|---|")
for name, terms in list(MODELS.items())[1:]:
    x = pertrade(terms)
    dpf, dnet = [], []
    for _ in range(2000):
        pick = rng.integers(0, len(ud), len(ud)); w = np.bincount(pick, minlength=len(ud))[inv]
        dpf.append(pf1(np.repeat(x, w)) - pf1(np.repeat(base, w))); dnet.append(((x - base) * w).sum())
    dpf, dnet = np.array(dpf), np.array(dnet)
    print(f"| {name} | {pf1(x) - pf1(base):+.4f} | [{np.quantile(dpf, .05):+.4f}, {np.quantile(dpf, .95):+.4f}] | {100 * (dpf > 0).mean():.0f} % | "
          f"{(x - base).sum():+,.0f} | [{np.quantile(dnet, .05):+,.0f}, {np.quantile(dnet, .95):+,.0f}] |")
print("\nThe candidates' coefficients per fit (per SD; odds × for p, × for W and L):\n")
print("| model | term | fit | p (odds ×) | W (×) | L (×) |\n|---|---|---|---|---|---|")
for name, (terms, b) in allb.items():
    for h in ('fwd', 'mirror'):
        bp, bw, bl = b[h]
        for i, t in enumerate(terms):
            print(f"| {name} | {t} | {h} | {np.exp(bp[i]):.3f} | {np.exp(bw[i]):.3f} | {np.exp(bl[i]):.3f} |")
