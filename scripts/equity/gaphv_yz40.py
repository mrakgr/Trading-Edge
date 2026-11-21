"""GH-11 — the 40-session YANG-ZHANG volatility before the event (user 2026-11-21: "both stocks had in common the extreme
volatility on the daily charts. What would the 40d Yang Zhang look like for these stocks?"). From daily_adjusted (raw P x
the causal share factor n: share-consistent OHLC, so a split is not a jump), the 40 sessions D−40 .. D−1 (known before D):
  o = ln(O_t / C_{t−1}),  c = ln(C_t / O_t),  rs = ln(H/C) ln(H/O) + ln(L/C) ln(L/O)
  σ²_YZ = var(o) + k var(c) + (1 − k) mean(rs),  k = 0.34 / (1.34 + (N + 1) / (N − 1)),  N = 40;  yz40 = σ_YZ, DAILY %
≥ 30 of the 40 sessions required. Then each variant on its GH-6 + GH-10 exit (U0930 LOSER + out at 09:35 if v5 ≥ 400 bp;
U1000 VWAP; D0930 / D1000 T1530), the short, gross, by yz40 decile.
    cd research && python3 scripts/equity/gaphv_yz40.py > data/equity/gaphv/yz40.md
"""
import sys, io, contextlib, duckdb, numpy as np, pandas as pd
sys.path.insert(0, 'scripts/equity')
with contextlib.redirect_stdout(io.StringIO()):
    from gaphv_exits import X, simulate, pf, f2
E5 = pd.read_parquet('data/equity/gaphv/exit5.parquet')
con = duckdb.connect(); con.execute("SET memory_limit='4GB'"); con.execute("SET threads=8")
con.register('ev', X[['ticker', 'd']].drop_duplicates())
N = 40; k = 0.34 / (1.34 + (N + 1) / (N - 1))
Y = con.execute(f"""
WITH s AS (SELECT ticker, date d, open * n o_, high * n h_, low * n l_, close * n c_ FROM 'data/v2/daily_adjusted.parquet'
           WHERE ticker IN (SELECT DISTINCT ticker FROM ev) AND open > 0 AND high > 0 AND low > 0 AND close > 0),
r AS (SELECT ticker, d, ln(o_ / lag(c_) OVER w) o, ln(c_ / o_) c,
             ln(h_ / c_) * ln(h_ / o_) + ln(l_ / c_) * ln(l_ / o_) rs FROM s WINDOW w AS (PARTITION BY ticker ORDER BY d)),
v AS (SELECT ticker, d, var_samp(o) OVER p vo, var_samp(c) OVER p vc, avg(rs) OVER p mrs, count(o) OVER p n
      FROM r WINDOW p AS (PARTITION BY ticker ORDER BY d ROWS BETWEEN {N} PRECEDING AND 1 PRECEDING))
SELECT v.ticker, v.d, 100 * sqrt(vo + {k} * vc + {1 - k} * mrs) yz40, n FROM v JOIN ev ON ev.ticker = v.ticker AND ev.d = v.d
""").df()
Y = Y[Y.n >= 30]; Y['d'] = pd.to_datetime(Y.d).dt.date
X = X.merge(Y[['ticker', 'd', 'yz40']], on=['ticker', 'd']).merge(E5, on=['ticker', 'd'])

print('# GH-11 — the 40-session Yang-Zhang volatility (daily %, D−40 .. D−1)\n')
V = [('U0930', 'u0930', 'open', 'LOSER', 'v5o', 'x0935'), ('D0930', 'd0930', 'open', 'T1530', None, None),
     ('U1000', 'u1000', 'x1000', 'VWAP', None, None), ('D1000', 'd1000', 'x1000', 'T1530', None, None)]
for name, flag, ent, rule, vc, xc in V:
    K = X[X[flag] & np.isfinite(X[ent]) & (X[ent] > 0)].copy().reset_index(drop=True); K['entry'] = K[ent]
    px, at = simulate(K, rule); r = 100 * (1 - px / K.entry.values)
    if vc:
        e5 = 100 * (1 - K[xc].values / K.entry.values); fire = (K[vc] >= 400).values & np.isfinite(e5); r = np.where(fire, e5, r)
    K['r'] = r
    if name == 'U0930':
        print('## PGHL / ZJYL\n')
        for t, d in (('PGHL', '2024-11-26'), ('ZJYL', '2023-12-18')):
            e = K[(K.ticker == t) & (K.d.astype(str) == d)]
            if len(e): print(f'- {t} {d}: yz40 {e.yz40.iloc[0]:.1f} % a day — the {(K.yz40 < e.yz40.iloc[0]).mean():.1%} percentile of U0930 (median {K.yz40.median():.1f} %)')
        print()
    lab = f'{rule}' + (' + 09:35 v5 ≥ 400' if vc else '')
    print(f'## {name} ({lab}; n {len(K):,}, PF {f2(pf(K.r.values))})\n')
    print('| yz40 decile (daily %) | n | median price | PF | mean | sum | worst | CVaR 1 % | PF 2016–20 / 2021–26 |')
    print('|---|---|---|---|---|---|---|---|---|')
    K['q'] = pd.qcut(K.yz40, 10, labels=False)
    for q, g in K.groupby('q'):
        s = np.sort(g.r.values); k1 = max(1, len(s) // 100)
        print(f'| {q + 1}: {g.yz40.min():.1f}–{g.yz40.max():.1f} | {len(g):,} | \\${g.open.median():.2f} | {f2(pf(g.r.values))} | {g.r.mean():+.2f} % | '
              f'{g.r.sum():,.0f} | {s[0]:+.0f} % | {s[:k1].mean():+.1f} % | {f2(pf(g.r[g.yr <= 2020].values))} / {f2(pf(g.r[g.yr >= 2021].values))} |')
    print()
