"""SWING MR — an independent spot check of swingmr_build.py: random rows (plus every row in the 15 sessions around a
split) recomputed from daily_adjusted directly, with plain Python loops — the windows, the universe gate, z / er / rsi2
and the outcomes (both entries, both rule exits).

  cd research && python -u scripts/equity/swingmr_check.py --rows data/equity/swingmr/test > logs/swingmr_check.log 2>&1; echo exit $?
"""
import argparse, math, os, sys
import duckdb, numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import data_v2

ap = argparse.ArgumentParser(); ap.add_argument('--rows', required=True); ap.add_argument('--n', type=int, default=200)
ap.add_argument('--breadth', default=None, help='the test build\'s breadth.parquet: recomputed exactly for the test tickers')
a = ap.parse_args()
con = data_v2.attach(duckdb.connect(), 'main')
R = pd.read_parquet(a.rows)
pick = pd.concat([R.sample(min(a.n, len(R)), random_state=3),
                  R[(R.ticker == 'AAPL') & R.date.between('2020-08-20', '2020-09-10')],
                  R[(R.ticker == 'NVDA') & R.date.between('2024-06-01', '2024-06-21')]]).drop_duplicates(['ticker', 'date'])
bad, checked = 0, 0
for tk, rows in pick.groupby('ticker'):
    g = con.execute(f"SELECT episode, date, open o, high h, low l, close c, volume v, n, cum_div cd FROM daily_episodes_causal WHERE ticker = '{tk}' ORDER BY episode, date").df()
    for _, r in rows.iterrows():
        e = g[g.date == r.date].episode.iat[0]
        G = g[g.episode == e].reset_index(drop=True)
        i = int(G.index[G.date == r.date][0])
        cn = lambda k: G.c[k] * G.n[k]
        exp = {}
        exp['price'] = G.c[i]
        ma200 = sum(cn(k) for k in range(i - 199, i + 1)) / 200
        exp['dist200'] = cn(i) / ma200 - 1
        for w in [3, 5, 10, 20, 50, 100, 200]:
            exp[f'ma_{w}'] = cn(i) / (sum(cn(k) for k in range(i - w + 1, i + 1)) / w) - 1
        dvs = sorted(G.c[k] * G.v[k] for k in range(i - 60, i)); exp['med60'] = (dvs[29] + dvs[30]) / 2
        for w in [3, 5, 10, 20, 50]:
            xs = [math.log(cn(k)) for k in range(i - w + 1, i + 1)]; m = sum(xs) / w
            sd = math.sqrt(sum((x - m) ** 2 for x in xs) / (w - 1)); exp[f'z_{w}'] = (xs[-1] - m) / sd
        for w in [2, 3, 5, 10, 20]:
            exp[f'er_{w}'] = (cn(i) - cn(i - w)) / sum(abs(cn(k) - cn(k - 1)) for k in range(i - w + 1, i + 1))
        tr = lambda i0, p0, j: 1e4 * ((cn(j) - p0 * G.n[i0]) + (G.cd[j] - G.cd[i0])) / (p0 * G.n[i0])
        for k in [1, 2, 3, 5, 7, 10]:
            if i + k < len(G):
                exp[f'rc_{k}'] = tr(i, G.c[i], i + k); exp[f'ro_{k}'] = tr(i + 1, G.o[i + 1], i + k)
        exp['ro_0'] = 1e4 * ((G.o[i + 1] * G.n[i + 1] - cn(i)) + (G.cd[i + 1] - G.cd[i])) / cn(i)
        for nm in ('x1', 'x2'):
            k = None
            for q in range(1, 11):
                j = i + q
                if j >= len(G):
                    break
                hit = cn(j) > sum(cn(t) for t in range(j - 4, j + 1)) / 5 if nm == 'x1' else cn(j) > G.h[j - 1] * G.n[j - 1]
                if hit:
                    k = q; break
            if k is None:
                k = min(10, len(G) - 1 - i)
            exp[f'{nm}_k'] = k; exp[f'{nm}_rc'] = tr(i, G.c[i], i + k); exp[f'{nm}_ro'] = tr(i + 1, G.o[i + 1], i + k)
            if r[f'{nm}_date'] != G.date[i + k]:
                bad += 1; print(f'MISMATCH {tk} {r.date.date()} {nm}_date: build {r[f"{nm}_date"]} vs check {G.date[i + k]}')
        for col, x in exp.items():
            y = r[col]; checked += 1
            if not (abs(x - y) <= 1e-6 * max(1, abs(x))):          # pandas' rolling std is online: ~1e-8 noise
                bad += 1; print(f'MISMATCH {tk} {r.date.date()} {col}: build {y} vs check {x}')
# the universe SET per ticker: every gate-passing day present, no other (a plain loop over each episode)
ubad = 0
for tk in R.ticker.unique():
    g = con.execute(f"SELECT episode, date, close c, volume v, n FROM daily_episodes_causal WHERE ticker = '{tk}' ORDER BY episode, date").df()
    want = set()
    for _, G in g.groupby('episode'):
        G = G.reset_index(drop=True); cn = (G.c * G.n).tolist(); dv = (G.c * G.v).tolist()
        for i in range(199, len(G) - 1):           # the 200th bar (index 199) is the first with 200 closes
            if str(G.date[i].date()) < '2005-01-01':
                continue
            if cn[i] > sum(cn[i - 199:i + 1]) / 200 and G.c[i] >= 5 and np.median(dv[i - 60:i]) >= 1e6:
                want.add(G.date[i])
    have = set(R[R.ticker == tk].date)
    if want != have:
        ubad += 1; print(f'UNIVERSE {tk}: {len(want - have)} missing, {len(have - want)} extra')
print(f'universe sets: {R.ticker.nunique()} tickers, {ubad} differ')
bad += ubad
if a.breadth:                                          # the breadth counts of the test tickers, by plain loops
    from collections import defaultdict
    cnt = defaultdict(lambda: [0] * 7)
    for tk in R.ticker.unique():
        g = con.execute(f"SELECT episode, date, close c, volume v, n FROM daily_episodes_causal WHERE ticker = '{tk}' ORDER BY episode, date").df()
        for _, G in g.groupby('episode'):
            G = G.reset_index(drop=True); cn = (G.c * G.n).tolist(); dv = (G.c * G.v).tolist()
            for i in range(60, len(G)):
                if G.c[i] >= 5 and np.median(dv[i - 60:i]) >= 1e6:
                    x = cnt[G.date[i]]; x[0] += 1
                    for q, w in enumerate((20, 50, 200)):
                        if i >= w - 1:
                            x[1 + 2 * q] += 1; x[2 + 2 * q] += cn[i] > sum(cn[i - w + 1:i + 1]) / w
    B = pd.read_parquet(a.breadth).set_index('date')
    bb = 0
    for d0, x in cnt.items():
        y = B.loc[d0, ['liq', 'has20', 'up20', 'has50', 'up50', 'has200', 'up200']].tolist()
        if list(map(int, y)) != x:
            bb += 1
            if bb <= 5: print(f'BREADTH {d0.date()}: build {y} vs check {x}')
    if len(cnt) != len(B):
        bb += 1; print(f'BREADTH dates: build {len(B)} vs check {len(cnt)}')
    print(f'breadth: {len(cnt):,} dates checked, {bb} differ')
    bad += bb
print(f'{len(pick)} rows, {checked} values checked, {bad} mismatches')
print('PASS' if bad == 0 else 'FAIL')
