"""Run 42 — a LINEAR SIZING MODEL for HighFlyerV2's late entry (user 2026-11-06: "creating a linear sizing model that is a
function of price change above 8% and rvol above 2 would be a good idea ... ideally, we'd enter an hour before the close. I
don't trust the 15:30 ET results in 2026, most likely it's overfit. We'll also have to figure out how to include 60d median
volume into our sizing").
The book: highflyer_v2_port.py --entry partial at 15:00 (the candle [09:30, 15:00), the fill its close), move >= 8 %, rvol >= 2
CLOSE-EQUIVALENT (2 x the close book's .819 share of the day's volume in by 15:00 = 1.64); the trips cut by a > 45-day gap
(delisting / long halt) KEPT at their last close (the Run 41 audit), only the ones still open at the data's end dropped.
The model: GF-54's form (gapfader_model_v2.py) — logit p (win), log-link W (the mean win, clipped at +50 % as the clip PF) and
log-link L (the mean loss), IRLS; PF−1 = p / ((1 − p) · L / W) − 1. Terms, standardised on the fit set, the continuous ones
winsorised 1/99 %: the move (pct_up), ln(rvol, close-equivalent), ln(dv60_med). Sizing as GapFader: PF−1 <= 0 refused, else
units = clip(PF−1, 0.125, 2). FIT 2005–2020, TEST 2021–2026 (never seen); then the shapes, the controls and a 2005–2025 refit.
    cd research && python3 scripts/equity/highflyer_v2_sizing.py > data/equity/highflyer_repro/v2_sizing.md
"""
import duckdb, numpy as np, pandas as pd
import data_v2

R = "data/equity/highflyer_repro/"
FRAC = 0.819                                             # the close book's share of the day's volume in by 15:00
con = data_v2.attach(duckdb.connect(), 'main'); con.execute("SET memory_limit='3GB'")
K = ['symbol', 'entry_date']
T = pd.read_parquet(R + "v2port_partial_causal_c1500_up0.08_rv1.64.parquet")
T['entry_date'] = pd.to_datetime(T.entry_date)
T = T[(T.open == 0) | (T.entry_date < '2026-10-20')].copy()
con.register('t', T[K].drop_duplicates())
M = con.execute("""WITH s AS (SELECT ticker, date, close * volume dv, row_number() OVER (PARTITION BY ticker ORDER BY date) i
                              FROM daily_adjusted WHERE ticker IN (SELECT DISTINCT symbol FROM t))
  SELECT t.symbol, t.entry_date, median(p.dv) dv60_med, count(*) n60 FROM t JOIN s x ON x.ticker = t.symbol AND x.date = t.entry_date
  JOIN s p ON p.ticker = t.symbol AND p.i BETWEEN x.i - 60 AND x.i - 1 GROUP BY 1, 2""").df()
M['entry_date'] = pd.to_datetime(M.entry_date)
T = T.merge(M, on=K, how='left')
T = T[T.dv60_med > 0].copy()
T['yr'] = T.entry_date.dt.year
T['rv_ce'] = T.rvol / FRAC                                 # close-equivalent rvol
T['r'] = T.ret.values
WIN = T.r > 0
FIT = T.yr <= 2020; TEST = T.yr >= 2021
RAW = {'move': T.pct_up.values, 'lrv': np.log(T.rv_ce.values), 'ldv': np.log(T.dv60_med.values),
       'lowdv': (T.dv60_med.values < 2e6) * 1.0}          # a FLAG (not winsorised): Run 32's proxy line
FLAGS = {'lowdv'}
TERMS = ['move', 'lrv', 'ldv']


def design(fitmask):
    cols, std = [np.ones(len(T))], {}
    for t in TERMS:
        x = RAW[t]
        if t in FLAGS: lo = hi = np.nan
        else: lo, hi = np.quantile(x[fitmask], [0.01, 0.99]); x = np.clip(x, lo, hi)
        mu, sd = x[fitmask].mean(), x[fitmask].std(); std[t] = (lo, hi, mu, sd)
        cols.append((x - mu) / sd)
    return np.column_stack(cols), std


def irls(X, y, link, it=100):
    b = np.zeros(X.shape[1])
    if link == "log": b[0] = np.log(y.mean())
    for _ in range(it):
        eta = X @ b
        if link == "logit": mu = 1 / (1 + np.exp(-eta)); w = mu * (1 - mu) + 1e-12; z = eta + (y - mu) / w
        else: mu = np.exp(eta); w = mu; z = eta + (y - mu) / mu
        bn = np.linalg.solve((X * w[:, None]).T @ X + 1e-9 * np.eye(X.shape[1]), (X * w[:, None]).T @ z)
        if np.max(np.abs(bn - b)) < 1e-10: return bn
        b = bn
    return b


def fit(fitmask):
    X, std = design(fitmask)
    if fitmask.dtype != bool: fitmask = fitmask.astype(bool)
    y = WIN.values * 1.0; rc = np.minimum(T.r.values, 0.5)
    bp = irls(X[fitmask], y[fitmask], 'logit')
    bw = irls(X[fitmask & WIN.values], rc[fitmask & WIN.values], 'log')
    bl = irls(X[fitmask & ~WIN.values], -T.r.values[fitmask & ~WIN.values], 'log')
    p = 1 / (1 + np.exp(-(X @ bp)))
    return p / ((1 - p) * np.exp(X @ bl) / np.exp(X @ bw)) - 1, (bp, bw, bl), std


def pf(r, w=None):
    r = np.asarray(r, float); w = np.ones_like(r) if w is None else np.asarray(w, float)
    k = np.isfinite(r) & (w > 0); r, w = r[k], w[k]
    g = (w * np.minimum(r, .5))[r > 0].sum(); l = -(w * r)[r < 0].sum()
    return np.inf if l == 0 else g / l


def units(pf1): return np.where(pf1 <= 0, 0.0, np.clip(pf1, 0.125, 2.0))


def f2(x): return "inf" if np.isinf(x) else f"{x:.2f}"


pf1, (bp, bw, bl), std = fit(FIT.values)
T['pf1'] = pf1; T['u'] = units(pf1)
print("# Run 42 — a linear sizing model for HighFlyerV2's 15:00 entry (scripts/equity/highflyer_v2_sizing.py)\n")
print(f"The book: 15:00, move >= 8 %, rvol >= 2 close-equivalent: {len(T):,} trips 2005–2026-10 (cut-off trips kept at their last close); "
      f"fit 2005–2020 {int(FIT.sum()):,}, test 2021–2026 {int(TEST.sum()):,}.\n")
print("## the coefficients (fit 2005–2020; per 1 sd of the term)\n\n| term | winsor / mean / sd | odds × (p) | W × | L × |\n|---|---|---|---|---|")
print(f"| intercept | | {np.exp(bp[0]):.3f} | {np.exp(bw[0]):.4f} | {np.exp(bl[0]):.4f} |")
for j, t in enumerate(TERMS, 1):
    lo, hi, mu, sd = std[t]
    print(f"| {t} | [{lo:.3g}, {hi:.3g}] / {mu:.3g} / {sd:.3g} | {np.exp(bp[j]):.3f} | {np.exp(bw[j]):.3f} | {np.exp(bl[j]):.3f} |")
print("\n(move = the holder's return to 15:00; lrv = ln(rvol close-equivalent); ldv = ln(dv60_med $). odds > 1 = more wins, "
      "W > 1 = bigger wins, L > 1 = bigger losses.)\n")

print("## OUT OF SAMPLE: the sized book against the equal-weight one (clip PF; sized = units-weighted)\n")
print("| period | trips | equal-weight PF · mean | refused (PF−1 <= 0) | sized PF | sized mean per unit | units / trip |\n|---|---|---|---|---|---|---|")
for lab, sel in (("fit 2005–2020 (in sample)", FIT), ("TEST 2021–2026", TEST), ("2021–2025", T.yr.between(2021, 2025)), ("2026", T.yr == 2026)):
    S = T[sel]; w = S.u.values
    print(f"| {lab} | {len(S):,} | {f2(pf(S.r))} · {S.r.mean() * 100:+.2f} % | {(w == 0).mean() * 100:.0f} % | **{f2(pf(S.r, w))}** | "
          f"{(w * S.r).sum() / w.sum() * 100:+.2f} % | {w.mean():.2f} |")
print("\n### the test set by predicted PF−1 quintile (does the model RANK?)\n\n| quintile | predicted PF−1 (median) | trips | realised clip PF−1 | mean |\n|---|---|---|---|---|")
S = T[TEST].copy(); S['q'] = pd.qcut(S.pf1, 5, labels=False)
for q, g in S.groupby('q'):
    print(f"| Q{q + 1} | {g.pf1.median():+.2f} | {len(g)} | {pf(g.r) - 1:+.2f} | {g.r.mean() * 100:+.2f} % |")
S26 = T[T.yr == 2026].copy(); S26['q'] = pd.qcut(S26.pf1, 3, labels=False, duplicates='drop')
print("\n2026 by predicted tercile: " + "; ".join(f"T{q + 1} pred {g.pf1.median():+.2f} → realised PF {f2(pf(g.r))} ({len(g)})" for q, g in S26.groupby('q')))

print("\n## the shapes (all years; trips · clip PF · mean) — fit / test\n")
for lab, x, edges, fmt in (("move", T.pct_up, [0.08, 0.10, 0.15, 0.20, 0.30], "{:.0%}"),
                           ("rvol close-eq.", T.rv_ce, [2, 3, 5, 10, 1e9], "{:g}"),
                           ("dv60_med", T.dv60_med, [0, 5e5, 2e6, 1e7, 1e13], "${:,.0f}")):
    print(f"| {lab} | fit 2005–2020 | test 2021–2026 |\n|---|---|---|")
    for lo, hi in zip(edges[:-1], edges[1:]):
        m = (x >= lo) & (x < hi)
        cells = [f"{int((m & s).sum())} · {f2(pf(T.r[m & s]))} · {T.r[m & s].mean() * 100:+.1f} %" for s in (FIT, TEST)]
        print(f"| {fmt.format(lo)} – {fmt.format(hi) if hi < 1e8 else 'up'} | {cells[0]} | {cells[1]} |")
    print()

print("## controls (test 2021–2026)\n")
rng = np.random.default_rng(42); S = T[TEST]; real = pf(S.r, S.u)
sh = np.array([pf(S.r.values, rng.permutation(S.u.values)) for _ in range(1000)])
print(f"- the units SHUFFLED across the test trips, 1000×: sized PF p5 {np.quantile(sh, .05):.2f} / median {np.median(sh):.2f} / p95 "
      f"{np.quantile(sh, .95):.2f}; the model's {real:.2f} (share of shuffles >= it {(sh >= real).mean() * 100:.1f} %)")
for drop in TERMS:
    keep = [t for t in TERMS if t != drop]
    globals()['TERMS'] = keep
    p1, _, _ = fit(FIT.values); u1 = units(p1)
    globals()['TERMS'] = ['move', 'lrv', 'ldv']
    print(f"- without {drop}: test sized PF {pf(S.r, u1[TEST.values]):.2f} (2026 {pf(T.r[T.yr == 2026], u1[(T.yr == 2026).values]):.2f})")
pp = T[TEST & (T.dv60_med < 2e6)]
print(f"- the plain proxy FILTER instead (dv60_med < $2M, equal weight): test {len(pp)} trips PF {pf(pp.r):.2f}; 2026 "
      f"{pf(pp.r[pp.yr == 2026]):.2f} ({int((pp.yr == 2026).sum())})")

print("\n## variants (fit 2005–2020, test 2021–2026; sized clip PF — 2026 beside it)\n")
print("| variant | test trips | equal-weight PF | sized PF | 2026 equal / sized | units on the < $2M trips / the rest (test, mean) |\n|---|---|---|---|---|---|")
for lab, terms, sub in (("move, lrv, ldv (the model above)", ['move', 'lrv', 'ldv'], None), ("move, lrv, lowdv flag", ['move', 'lrv', 'lowdv'], None),
                        ("move, lrv, ldv + lowdv flag", ['move', 'lrv', 'ldv', 'lowdv'], None),
                        ("FILTER dv60_med < $2M, then size on move, lrv", ['move', 'lrv'], T.dv60_med.values < 2e6)):
    globals()['TERMS'] = terms
    m = np.ones(len(T), bool) if sub is None else sub
    p1, _, _ = fit(FIT.values & m); u1 = np.where(m, units(p1), 0.0)
    te, t26 = TEST.values & m, (T.yr == 2026).values & m
    lowm = TEST.values & (T.dv60_med.values < 2e6)
    print(f"| {lab} | {int(te.sum()):,} | {pf(T.r[te]):.2f} | **{pf(T.r[te], u1[te]):.2f}** | {pf(T.r[t26]):.2f} / {pf(T.r[t26], u1[t26]):.2f} | "
          f"{u1[lowm].mean():.2f} / {u1[TEST.values & ~(T.dv60_med.values < 2e6)].mean():.2f} |")
globals()['TERMS'] = ['move', 'lrv', 'ldv']
pf1_all, (cp, cw, cl), std_all = fit((T.yr <= 2025).values)
print("\n## the 2005–2025 refit (for production, not deployed; per 1 sd)\n\n| term | winsor | mean / sd | odds × | W × | L × |\n|---|---|---|---|---|---|")
for j, t in enumerate(['intercept'] + TERMS):
    if t == 'intercept':
        print(f"| intercept | | | {np.exp(cp[0]):.3f} | {np.exp(cw[0]):.4f} | {np.exp(cl[0]):.4f} |"); continue
    lo, hi, mu, sd = std_all[t]
    print(f"| {t} | [{lo:.4g}, {hi:.4g}] | {mu:.4g} / {sd:.4g} | {np.exp(cp[j]):.3f} | {np.exp(cw[j]):.3f} | {np.exp(cl[j]):.3f} |")
u_all = units(pf1_all)
print(f"\nin-sample 2005–2025 sized PF {pf(T.r[T.yr <= 2025], u_all[(T.yr <= 2025).values]):.2f} vs equal {pf(T.r[T.yr <= 2025]):.2f}; 2026 (out of "
      f"sample) {pf(T.r[T.yr == 2026], u_all[(T.yr == 2026).values]):.2f} vs {pf(T.r[T.yr == 2026]):.2f}")

# ---- THE CHOSEN FORM: the proxy filter (dv60_med < $2M, Run 32) then the PF−1 model on move and ln rvol
print("\n## the chosen form: the proxy FILTER (dv60_med < $2M) + the PF−1 model on move and ln(rvol)\n")
LOW = T.dv60_med.values < 2e6
TERMS = ['move', 'lrv']
p1, (b1p, b1w, b1l), s1 = fit(FIT.values & LOW); u1 = units(p1)
S = T[TEST.values & LOW].assign(pf1=p1[TEST.values & LOW], u=u1[TEST.values & LOW])
S['q'] = pd.qcut(S.pf1, 5, labels=False)
print("test 2021–2026 by predicted quintile: " + "; ".join(f"Q{q + 1} pred {g.pf1.median():+.2f} → realised PF−1 {pf(g.r) - 1:+.2f} ({len(g)})" for q, g in S.groupby('q')))
for lab, sel in (("2021–2025", S.yr <= 2025), ("2026", S.yr == 2026)):
    g = S[sel]; print(f"- {lab}: {len(g)} trips, equal {pf(g.r):.2f}, sized {pf(g.r, g.u):.2f}")
pa, (ap, aw, al), sa = fit((T.yr <= 2025).values & LOW)
print("\nthe 2005–2025 refit of the chosen form (production candidate, not deployed; per 1 sd):\n\n| term | winsor | mean / sd | odds × | W × | L × |\n|---|---|---|---|---|---|")
print(f"| intercept | | | {np.exp(ap[0]):.4f} | {np.exp(aw[0]):.5f} | {np.exp(al[0]):.5f} |")
for j, t in enumerate(TERMS, 1):
    lo, hi, mu, sd = sa[t]
    print(f"| {t} | [{lo:.4g}, {hi:.4g}] | {mu:.4g} / {sd:.4g} | {np.exp(ap[j]):.4f} | {np.exp(aw[j]):.4f} | {np.exp(al[j]):.4f} |")
print(f"\n(raw β, for the record — p: {np.round(ap, 6).tolist()}, W: {np.round(aw, 6).tolist()}, L: {np.round(al, 6).tolist()})")
