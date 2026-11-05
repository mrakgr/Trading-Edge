"""FlushFader S49an — the trend measures on the reference book (user 2026-11-04): Mann–Kendall tau and Page's CUSUM.

The engine (base_v20, scripts/equity/flushfader_run_base_v20.sh) records at every signal: tau_{60,180,300,600,1200}
(Mann–Kendall tau-a of the last N present-bar vwaps, the signal bar included), and per drift allowance k the two CUSUMs
on every present bar's log return with the present bars since each last reset — normalised (cusz_*: z = r / (volat_20m /
√30), k 0.25 / 0.5 / 1) and raw (cusr_*: z = r in bp, k 0 / 4.35 / 10). (S49ao: --family kalman --base base_v21 for the Kalman grid.) Joined to the reference book
(data/flushfader_gate_review/broad_reference_book.parquet, 37,254 trips 2020-01 .. 2026-08, ret_exit) on
(symbol, trade_date, signal_sec).

Per feature: quintiles cut on the FIT years (2020–22), PF raw per quintile on fit and HOLDOUT (2023–26); then the
filter test — drop the end quintile (Q1 or Q5) that is worse on fit, and the holdout PF after the drop against 2,000
random drops of the same number of holdout trips (the percentile where the real drop sits).
    cd research && python3 scripts/equity/flushfader_trend_features.py > data/equity/flushfader/trend_features.md
"""
import argparse, duckdb, numpy as np, pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument('--base', default='data/equity/flushfader/base_v20', help='the base corpus with the feature columns')
ap.add_argument('--family', choices=['tau_cusum', 'kalman', 'cusum_lo'], default='tau_cusum',
                help="tau_cusum = S49an's columns; kalman = S49ao's kal_{slope,t,gap}_<l>_<h>; cusum_lo = S49ap's raw CUSUM at "
                     "k 0.25 / 0.5 / 1 / 2 (+ k 4.35 for comparison) with the velocity dn / max(dn_age, 1)")
a = ap.parse_args()

book = pd.read_parquet('data/flushfader_gate_review/broad_reference_book.parquet', columns=['symbol', 'trade_date', 'signal_sec', 'ret_exit'])
con = duckdb.connect(); con.execute("SET memory_limit='6GB'"); con.execute('SET threads=4')
TAU = [f'tau_{n}' for n in (60, 180, 300, 600, 1200)]
CZ = [f'cusz_{x}_k{k}' for k in ('025', '05', '1') for x in ('up', 'dn', 'up_age', 'dn_age')]
CR = [f'cusr_{x}_k{k}' for k in ('0', '435', '10') for x in ('up', 'dn', 'up_age', 'dn_age')]
KAL = [f'kal_{x}_{l}_{h}' for x in ('slope', 't', 'gap') for l in ('l0', 'l01', 'l025') for h in ('h60', 'h300', 'h1200')]
LOK = ('025', '05', '1', '2', '435')
LO_COLS = [f'cusr_{x}_k{k}' for k in LOK for x in ('dn', 'dn_age')]
FEATS = {'tau_cusum': TAU + CZ + CR, 'kalman': KAL, 'cusum_lo': LO_COLS}[a.family]
con.register('book', book)
d = con.execute(f"""
    SELECT b.*, {', '.join('t.' + f for f in FEATS)}
    FROM book b LEFT JOIN (SELECT symbol, trade_date, signal_sec, {', '.join(FEATS)}
                           FROM read_parquet('{a.base}/*.parquet')) t
      USING (symbol, trade_date, signal_sec)""").df()
if a.family == 'cusum_lo':                           # the velocity: the excess bp per bar since the reset (S⁻ / A)
    for k in LOK:
        d[f'cusr_vel_k{k}'] = d[f'cusr_dn_k{k}'] / np.maximum(d[f'cusr_dn_age_k{k}'], 1.0)
    FEATS = FEATS + [f'cusr_vel_k{k}' for k in LOK]
yr = pd.to_datetime(d.trade_date).dt.year
FIT, HOLD = (yr <= 2022).values, (yr >= 2023).values
r = d.ret_exit.values

def pf(x):
    x = x[np.isfinite(x)]
    g, l = x[x > 0].sum(), -x[x < 0].sum()
    return np.inf if l == 0 else g / l
def f2(x): return 'inf' if np.isinf(x) else f'{x:.2f}'

joined = d[FEATS].notna().any(axis=1)
print({'tau_cusum': '# S49an — Mann–Kendall tau and Page\'s CUSUM on the FlushFader reference book\n',
       'kalman': '# S49ao — the Kalman local linear trend on the FlushFader reference book\n',
       'cusum_lo': '# S49ap — the raw downside CUSUM at smaller k on the FlushFader reference book\n'}[a.family])
print(f'Book {len(d):,} trips (PF {pf(r):.3f}); joined to the base {int(joined.sum()):,} ({100 * joined.mean():.2f} %). '
      f'Fit 2020–22 {FIT.sum():,} trips (PF {pf(r[FIT]):.3f}), holdout 2023–26 {HOLD.sum():,} (PF {pf(r[HOLD]):.3f}).\n')
print('Cells: PF raw (trips). Quintile edges from the fit years. The filter: drop the end quintile worse on fit; '
      '"null pct" = where the holdout PF after the drop sits among 2,000 random drops of as many holdout trips.\n')
rng = np.random.default_rng(20261104)
print('| feature | fit Q1 | Q2 | Q3 | Q4 | Q5 | holdout Q1 | Q2 | Q3 | Q4 | Q5 | drop (fit-chosen) | holdout PF after | null pct |')
print('|---|---|---|---|---|---|---|---|---|---|---|---|---|---|')
for f in FEATS:
    x = d[f].values.astype(float)
    ok = np.isfinite(x)
    edges = np.nanquantile(x[FIT & ok], [0.2, 0.4, 0.6, 0.8])
    # a mass point (a CUSUM at exactly 0, an integer age) repeats an edge: that quintile is empty, its trips in the next
    q = np.where(ok, np.searchsorted(edges, x, side='right'), -1)
    cells_f = [f'{f2(pf(r[FIT & (q == i)]))} ({int((FIT & (q == i)).sum())})' for i in range(5)]
    cells_h = [f'{f2(pf(r[HOLD & (q == i)]))} ({int((HOLD & (q == i)).sum())})' for i in range(5)]
    nonempty = [i for i in range(5) if (FIT & (q == i)).any()]
    lo_q, hi_q = nonempty[0], nonempty[-1]                       # the end bins that hold trips
    worst = lo_q if pf(r[FIT & (q == lo_q)]) < pf(r[FIT & (q == hi_q)]) else hi_q
    keep = HOLD & ok & (q != worst)
    after = pf(r[keep])
    pool = np.flatnonzero(HOLD & ok)
    ndrop = int((HOLD & (q == worst)).sum())
    null = np.array([pf(np.delete(r[pool], rng.choice(len(pool), ndrop, replace=False))) for _ in range(2000)])
    print(f'| {f} | ' + ' | '.join(cells_f) + ' | ' + ' | '.join(cells_h) +
          f' | Q{worst + 1} ({ndrop} holdout) | {f2(after)} | {100 * (null < after).mean():.1f} |')
print(f'\nHoldout PF of the joined trips with no drop: {pf(r[HOLD & joined.values]):.3f}.')
