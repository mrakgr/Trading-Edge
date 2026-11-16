"""MARKET-MAKING OPPORTUNITY SCAN, step 2: from mm_opportunity_scan.py's per-minute files — how OFTEN do the fertile minutes
(high activity × wide spread × high volatility) occur, do they PERSIST after one is detected (user 2026-11-16: "after we
detect a high opportunity period for 1m whether it persists for a few minutes after that"), and the EPISODES for a review
in TapeSim.

A minute QUALIFIES at a tier when all of: lit trades >= n, dollar volume >= dv, the spread estimate (Roll / 0.27, bp) >= spr,
the bounce-free volatility (bp / sqrt s) >= vol. Persistence is CAUSAL: the minute is known at its end, the question is
the next minutes m+1..m+5 of the same ticker (a minute not saved = it had < 100 lit trades = not qualifying). An EPISODE =
consecutive qualifying minutes (a one-minute gap bridged); its onset is the first.

  cd research && python -u scripts/equity/mm_opportunity_report.py > data/equity/mm_opportunity/report.md 2> logs/mm_opportunity_report.log; echo exit $?
"""
import argparse, glob, os
import numpy as np, pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument('--minutes', default='data/equity/mm_opportunity/minutes')
ap.add_argument('--out', default='data/equity/mm_opportunity')
ap.add_argument('--roll-ratio', type=float, default=0.27)
a = ap.parse_args()
TIERS = {  # name: (lit trades, $ volume, spread bp, vol bp/sqrt s) per minute
    'A  CBRS-like': (1000, 5e6, 30, 10),
    'B  broad':     (300, 1e6, 30, 10),
    'C  wide':      (300, 1e6, 50, 15),
    'D  looser':    (300, 1e6, 20, 5),
}
files = sorted(glob.glob(f'{a.minutes}/*.parquet'))
D = pd.concat([pd.read_parquet(f).assign(date=os.path.basename(f)[:-8]) for f in files], ignore_index=True)
D = D[D.m.between(0, 389)].reset_index(drop=True)
D['spr'] = 2 * np.sqrt((-D['cov']).clip(lower=0)) / a.roll_ratio
D['vol'] = np.sqrt((D.var0 + 2 * D['cov']).clip(lower=0) * D.n / 60)
D['rng'] = 1e4 * (D.hi / D.lo - 1)
D = D.sort_values(['date', 'ticker', 'm']).reset_index(drop=True)
days = D.date.nunique()
et = lambda m: f'{9 + (30 + int(m)) // 60:02d}:{(30 + int(m)) % 60:02d}'
P = lambda x: f'{x:.0%}' if x >= 0.01 or x == 0 else f'{x:.1%}'

print(f'# Market-making opportunity minutes — {days} days, {os.path.basename(files[0])[:-8]} … {os.path.basename(files[-1])[:-8]}\n')
print(f'{len(D):,} ticker-minutes with ≥ 100 lit trades. Spread = Roll / {a.roll_ratio} (quoted-scale estimate); vol = bounce-free bp/√s.\n')
print('## Frequency\n')
print('| tier (per minute: trades, \\$, spread bp, vol) | minutes / day | in the first 5 min | tickers / day | days with any | ticker-days | top tickers |')
print('|---|---|---|---|---|---|---|')
Q = {}
for name, (n, dv, s, v) in TIERS.items():
    q = (D.n >= n) & (D.dv >= dv) & (D.spr >= s) & (D.vol >= v); Q[name] = q.to_numpy()
    X = D[q]
    td = X.groupby('date').ticker.nunique()
    top = ', '.join(f'{t} {c}' for t, c in X.groupby(['ticker']).size().sort_values(ascending=False).head(5).items())
    print(f'| {name} ({n}, {dv / 1e6:.0f}M, {s}, {v}) | {len(X) / days:.1f} | {P((X.m < 5).mean()) if len(X) else "-"} | '
          f'{td.sum() / days:.1f} | {X.date.nunique()}/{days} | {X.groupby(["date", "ticker"]).ngroups} | {top} |')

print('\n## Persistence — after a qualifying minute m, the same ticker\'s next minutes\n')
print('"next" = m+1 qualifies; "≥3 of 5" = at least 3 of m+1..m+5; "base" = the share of all saved minutes that qualify. '
      'Onset = the first qualifying minute after one that did not; excl. open = m ≥ 5 (09:35 on). "½ tier" = the minute clears HALF '
      'the tier\'s four thresholds (a hysteresis: the estimates are noisy minute to minute).\n')
print('| tier | condition | minutes | next | m+2 | m+5 | ≥3 of 5 | mean of 5 | next: ≥ ½ own trades & spread | ½ tier: next | ½ tier: ≥3 of 5 | ½ tier: m+5 | base |')
print('|---|---|---|---|---|---|---|---|---|---|---|---|')
key = (D.date + '|' + D.ticker).to_numpy(); m = D.m.to_numpy()
idx = pd.Series(np.arange(len(D)), index=pd.MultiIndex.from_arrays([key, m]))
def look(i_rows, k):
    """the row index of the same ticker-day at minute m+k, -1 when not saved"""
    j = idx.reindex(pd.MultiIndex.from_arrays([key[i_rows], m[i_rows] + k]))
    return j.fillna(-1).astype(int).to_numpy()
rows_ep = []
for name, q in Q.items():
    n_, dv_, s_, v_ = TIERS[name]
    h = ((D.n >= n_ / 2) & (D.dv >= dv_ / 2) & (D.spr >= s_ / 2) & (D.vol >= v_ / 2)).to_numpy()
    prev = look(np.arange(len(D)), -1)
    onset = q & ~np.where(prev >= 0, q[np.clip(prev, 0, None)], False)
    for cname, c in [('every', q), ('every, excl. open', q & (m >= 5)), ('onset', onset), ('onset, excl. open', onset & (m >= 5))]:
        i = np.nonzero(c)[0]
        if len(i) == 0: continue
        nxt = np.stack([np.where((j := look(i, k)) >= 0, q[np.clip(j, 0, None)], False) for k in range(1, 6)], 1)
        j1 = look(i, 1); ok1 = j1 >= 0
        half = np.zeros(len(i), bool)
        half[ok1] = (D.n.to_numpy()[j1[ok1]] >= 0.5 * D.n.to_numpy()[i[ok1]]) & (D.spr.to_numpy()[j1[ok1]] >= 0.5 * D.spr.to_numpy()[i[ok1]])
        hn = np.stack([np.where((j := look(i, k)) >= 0, h[np.clip(j, 0, None)], False) for k in range(1, 6)], 1)
        print(f'| {name} | {cname} | {len(i):,} | {P(nxt[:, 0].mean())} | {P(nxt[:, 1].mean())} | {P(nxt[:, 4].mean())} | '
              f'{P((nxt.sum(1) >= 3).mean())} | {nxt.sum(1).mean():.2f} | {P(half.mean())} | {P(hn[:, 0].mean())} | '
              f'{P((hn.sum(1) >= 3).mean())} | {P(hn[:, 4].mean())} | {P(q.mean())} |')
    # the episodes (one-minute gaps bridged)
    X = D[q][['date', 'ticker', 'm', 'n', 'dv', 'spr', 'vol', 'rng', 'last_px']].copy()
    X['g'] = ((X.ticker != X.ticker.shift()) | (X.date != X.date.shift()) | (X.m - X.m.shift() > 2)).cumsum()
    E = X.groupby('g').agg(date=('date', 'first'), ticker=('ticker', 'first'), m0=('m', 'first'), m1=('m', 'last'), minutes=('m', 'size'),
                           trades=('n', 'sum'), dv=('dv', 'sum'), spr=('spr', 'median'), vol=('vol', 'median'), price=('last_px', 'median'))
    E['tier'] = name.split()[0]
    rows_ep.append(E)

print('\n## Episodes (consecutive qualifying minutes, a one-minute gap bridged)\n')
print('| tier | episodes | per day | 1 min | 2–4 min | 5–14 min | 15 min + | median length | in the first 5 min |')
print('|---|---|---|---|---|---|---|---|---|')
for E in rows_ep:
    L = E.m1 - E.m0 + 1
    print(f'| {E.tier.iloc[0]} | {len(E):,} | {len(E) / days:.1f} | {P((L == 1).mean())} | {P(L.between(2, 4).mean())} | '
          f'{P(L.between(5, 14).mean())} | {P((L >= 15).mean())} | {L.median():.0f} | {P((E.m0 < 5).mean())} |')
EP = pd.concat(rows_ep, ignore_index=True)
EP['start_et'] = EP.m0.map(et); EP['end_et'] = (EP.m1 + 1).map(et)
EP['dv_M'] = (EP.dv / 1e6).round(1)
EP = EP[['tier', 'date', 'ticker', 'start_et', 'end_et', 'minutes', 'trades', 'dv_M', 'spr', 'vol', 'price']].round(1)
EP.sort_values(['tier', 'date', 'start_et']).to_csv(f'{a.out}/episodes.csv', index=False)
B = EP[(EP.tier == 'B') & (EP.minutes >= 3)].sort_values('dv_M', ascending=False)
print(f'\n## The longest-lived tier-B episodes (≥ 3 qualifying minutes), by dollar volume — for a TapeSim review\n')
print(f'All episodes: `{a.out}/episodes.csv`.\n')
print(B.head(40).to_markdown(index=False))
