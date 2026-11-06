#!/usr/bin/env python3
"""SpringFlyer S15 — the ADVERSE EXCURSION of the names with options vs without (user 2026-11-06: "We should also look at
the MAE for those with options vs without. If those stocks that have options really have more upside, we should see that
reflected in the drawdown figures for the two books. If that's true, it might be a good idea to tighten up the rules for
the with-option names.")

The 2016+ book of S11 (option_coverage.parquet: has_chain on D). The path over the 10-session hold from
daily_episodes_causal, every price in D's scale (P * n / n(D)), entry = D's close:
  mae   = max(high over sessions 1..10) / entry - 1     the short's worst point (adverse)
  mfe   = 1 - min(low over sessions 1..10) / entry      its best point
  mae_day  the session of the max high;  mae_k1  = session 1's high alone (the first-morning squeeze)
Then, inside the chain names only: the spec's own features in bands (er10, er20, rng20c, chg20, rvol_d, dv60_med, close),
PF r10 and the MAE p75/p90 — candidate tightenings, to be read against the no-chain names in the same band.

    cd research && python3 scripts/equity/springflyer_chain_mae.py
"""
import duckdb, numpy as np, pandas as pd
import data_v2

con = data_v2.attach(duckdb.connect(), 'main'); con.execute("SET memory_limit='5GB'")
cov = pd.read_parquet('data/equity/springflyer/option_coverage.parquet')[['ticker', 'date', 'has_chain']]
sig = pd.read_parquet('data/equity/springflyer/signals_spec_v2.parquet')
for x in (cov, sig):
    x['date'] = pd.to_datetime(x.date)
b = sig.merge(cov, on=['ticker', 'date'])
b = b[b.r10.notna()].copy()
con.register('b', b[['ticker', 'date']])
path = con.execute("""
WITH t AS (SELECT DISTINCT ticker FROM b),
p AS (SELECT x.ticker, x.date, x.episode, x.high * x.n AS hn, x.low * x.n AS ln, x.close * x.n AS cn,
        ROW_NUMBER() OVER (PARTITION BY x.ticker, x.episode ORDER BY x.date) AS rn
      FROM daily_episodes_causal x JOIN t USING (ticker) WHERE x.date >= '2015-06-01'),
s AS (SELECT b.ticker, b.date, p.episode, p.rn, p.cn FROM b JOIN p ON p.ticker = b.ticker AND p.date = b.date)
SELECT s.ticker, s.date, q.rn - s.rn AS k, q.hn / s.cn - 1 AS hi, q.ln / s.cn - 1 AS lo
FROM s JOIN p q ON q.ticker = s.ticker AND q.episode = s.episode AND q.rn BETWEEN s.rn + 1 AND s.rn + 10""").df()
path['date'] = pd.to_datetime(path.date)
g = path.groupby(['ticker', 'date'])
agg = pd.DataFrame({'mae': g.hi.max(), 'mfe': -g.lo.min(), 'n_sess': g.k.max(),
                    'mae_day': path.loc[g.hi.idxmax(), ['ticker', 'date', 'k']].set_index(['ticker', 'date']).k})
k1 = path[path.k == 1].set_index(['ticker', 'date']).hi.rename('mae_k1')
b = b.merge(agg.join(k1).reset_index(), on=['ticker', 'date'], how='left')
b['s10'] = -b.r10
dv = con.execute("""
WITH t AS (SELECT DISTINCT ticker FROM b),
p AS (SELECT x.ticker, x.date, MEDIAN(x.volume * x.close) OVER (PARTITION BY x.ticker, x.episode ORDER BY x.date
        ROWS BETWEEN 60 PRECEDING AND 1 PRECEDING) AS dv60_med FROM daily_episodes_causal x JOIN t USING (ticker) WHERE x.date >= '2015-01-01')
SELECT p.* FROM p JOIN b USING (ticker, date)""").df()
dv['date'] = pd.to_datetime(dv.date); b = b.merge(dv, on=['ticker', 'date'], how='left')


def pf(r):
    r = np.asarray(r, float); r = r[~np.isnan(r)]; g_, l = r[r > 0].sum(), -r[r < 0].sum()
    return g_ / l if l > 0 else float('inf')


print(f"book 2016+ with closed 10-day trades: {len(b)} ({int(b.has_chain.sum())} with a chain); full 10-session path for "
      f"{int((b.n_sess == 10).sum())}\n")
print("## 1. the adverse excursion (max high over the hold vs the entry close; % of the entry)\n")
print("| group | n | MAE p25 | p50 | p75 | p90 | p95 | max | MAE > 20% | > 50% | > 100% | session-1 high p50 / p90 | MFE p50 | PF r10 |")
print("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")
for lab, x in (('chain', b[b.has_chain]), ('no chain', b[~b.has_chain])):
    m = x.mae * 100; q = np.percentile(m, [25, 50, 75, 90, 95])
    print(f"| {lab} | {len(x)} | {q[0]:.1f} | {q[1]:.1f} | {q[2]:.1f} | {q[3]:.1f} | {q[4]:.1f} | {m.max():.0f} | {(m > 20).mean() * 100:.0f}% | "
          f"{(m > 50).mean() * 100:.0f}% | {(m > 100).mean() * 100:.0f}% | {x.mae_k1.median() * 100:.1f} / {np.percentile(x.mae_k1, 90) * 100:.1f} | "
          f"{x.mfe.median() * 100:.1f} | {pf(x.s10.values):.2f} |")
print("\n### where the MAE lands (session of the max high), share of trades")
print(pd.crosstab(b.has_chain.map({True: 'chain', False: 'no chain'}), pd.cut(b.mae_day, [0, 1, 2, 5, 10], labels=['1', '2', '3-5', '6-10']),
                  normalize='index').mul(100).round(0).to_string())
print("\n### MAE vs the outcome: trades whose MAE passed X, and how they ended (mean r10 short bp; share that still won)")
print("| MAE passed | chain: n / still won / mean | no chain: n / still won / mean |\n|---|---|---|")
for th in (0.1, 0.2, 0.3, 0.5):
    cells = []
    for hc in (True, False):
        x = b[(b.has_chain == hc) & (b.mae > th)]
        cells.append(f"{len(x)} / {(x.s10 > 0).mean() * 100:.0f}% / {x.s10.mean():+.0f}" if len(x) else "—")
    print(f"| {th * 100:.0f}% | {cells[0]} | {cells[1]} |")

print("\n## 2. candidate tightenings for the CHAIN names: the spec's features in bands (n · PF r10 · MAE p75 / p90 %), no-chain beside")
F = {'er10_signed': [0.9, 0.95, 0.98, 1.01], 'er20_signed': [0.6, 0.75, 0.9, 1.01], 'rng20c_d': [2, 3, 5, 1e9],
     'chg20': [1, 2, 3, 5, 1e9], 'rvol_d': [0, 1, 3, 10, 1e9], 'dv60_med': [0, 1e6, 5e6, 2e7, 1e13], 'close': [2, 5, 10, 20, 50, 1e9]}
for f, edges in F.items():
    print(f"\n| {f} | chain | no chain |\n|---|---|---|")
    for lo, hi in zip(edges[:-1], edges[1:]):
        cells = []
        for hc in (True, False):
            x = b[(b.has_chain == hc) & (b[f] > lo) & (b[f] <= hi)]
            cells.append(f"{len(x)} · {pf(x.s10.values):.2f} · {np.percentile(x.mae, 75) * 100:.0f} / {np.percentile(x.mae, 90) * 100:.0f}" if len(x) >= 5
                         else f"{len(x)}")
        print(f"| ({lo:g}, {hi:g}] | {cells[0]} | {cells[1]} |")

print("\n## 3. a STOP at +X% over the entry close, each group (fills: a session OPENING above the stop exits at that open — the gap;")
print("else a high through the stop exits at the stop; otherwise the 10-day close). Short bp.\n")
op = con.execute("""
WITH t AS (SELECT DISTINCT ticker FROM b),
p AS (SELECT x.ticker, x.date, x.episode, x.open * x.n AS on_, x.high * x.n AS hn, x.close * x.n AS cn,
        ROW_NUMBER() OVER (PARTITION BY x.ticker, x.episode ORDER BY x.date) AS rn
      FROM daily_episodes_causal x JOIN t USING (ticker) WHERE x.date >= '2015-06-01'),
s AS (SELECT b.ticker, b.date, p.episode, p.rn, p.cn FROM b JOIN p ON p.ticker = b.ticker AND p.date = b.date)
SELECT s.ticker, s.date, q.rn - s.rn AS k, q.on_ / s.cn - 1 AS o, q.hn / s.cn - 1 AS h
FROM s JOIN p q ON q.ticker = s.ticker AND q.episode = s.episode AND q.rn BETWEEN s.rn + 1 AND s.rn + 10 ORDER BY 1, 2, 3""").df()
op['date'] = pd.to_datetime(op.date)
P = {key: (g_.o.values, g_.h.values) for key, g_ in op.groupby(['ticker', 'date'])}


def stopped(row, x):
    o, h = P[(row.ticker, row.date)]
    for oo, hh in zip(o, h):
        if oo >= x:
            return -oo * 1e4
        if hh >= x:
            return -x * 1e4
    return row.s10


print("| stop | chain: PF / mean / worst / stopped % | no chain: PF / mean / worst / stopped % |\n|---|---|---|")
for x in (None, 0.2, 0.3, 0.5, 1.0):
    cells = []
    for hc in (True, False):
        g_ = b[b.has_chain == hc]
        r = g_.s10.values if x is None else np.array([stopped(rw, x) for rw in g_.itertuples()])
        st = 0 if x is None else (g_.mae >= x).mean() * 100
        cells.append(f"{pf(r):.2f} / {r.mean():+.0f} / {r.min():+.0f} / {st:.0f}%")
    print(f"| {'none (10-day close)' if x is None else f'+{x * 100:.0f}%'} | {cells[0]} | {cells[1]} |")
for era, sel in (('2016-2021', b.date.dt.year <= 2021), ('2022-2026', b.date.dt.year >= 2022)):
    cells = []
    for x in (None, 0.2, 0.3):
        g_ = b[(b.has_chain) & sel]
        r = g_.s10.values if x is None else np.array([stopped(rw, x) for rw in g_.itertuples()])
        cells.append(f"{pf(r):.2f}")
    print(f"| chain {era} ({int((b.has_chain & sel).sum())}): none / +20% / +30% | {' / '.join(cells)} | |")

print("\n## 4. the range rule for the chain names: rng20c_d > 3 (vs the spec's > 2), by era (n · PF r10 · mean)\n")
print("| era | chain, range 2-3 | chain, range > 3 | no chain, range 2-3 | no chain, range > 3 |\n|---|---|---|---|---|")
for era, sel in (('2016-2021', b.date.dt.year <= 2021), ('2022-2026', b.date.dt.year >= 2022), ('all', b.date.dt.year > 0)):
    cells = []
    for hc in (True, False):
        for lo, hi in ((2, 3), (3, 1e9)):
            x = b[sel & (b.has_chain == hc) & (b.rng20c_d > lo) & (b.rng20c_d <= hi)]
            cells.append(f"{len(x)} · {pf(x.s10.values):.2f} · {x.s10.mean():+.0f}")
    print(f"| {era} | " + " | ".join(cells) + " |")
