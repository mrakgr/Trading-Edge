"""GH-13g — a STEPPED threshold schedule for the EWMA disaster stop (user 2026-11-23: "Past 9:45 we use the 350 bp stop. Past
9:40 we'll use a higher threshold and past 9:35 a higher threshold still. It's going to be overfitting to some degree, but all
skill is fitting, so we might as well look into it").

The ew · any stop of GH-13c (W = 3, 4 slots), computed from the entry as ever; the threshold by the clock:
    before 09:35  P   (∞ = off, or 1,000 — GH-13f: an early level rescued PHUN)
    09:35–09:40   T1
    09:40–09:45   T2  (T1 >= T2)
    from 09:45    350
T1 ∈ {400, 450, 500, 600, 700, 800, 1000, ∞}, T2 ∈ {350, 400, 450, 500, 600, 700, ∞}. Only U0930 / D0930 change (the 10:00 entries
are past 09:45). The control: P = T1 = T2 = 350 = today's stop. ⚠ FITTING: every cell is shown with both halves of the
sample (2016–2020 · 2021–2026, Σ %), and the halves' ranks — a schedule that wins in one half only is noise.
    cd research && python3 scripts/equity/gaphv_volstop6.py > logs/gaphv_volstop6.log 2>&1
    cd research && python3 scripts/equity/gaphv_volstop6.py --show > data/equity/gaphv/volstop6.md
"""
import duckdb, numpy as np, pandas as pd, os, sys, io, contextlib, time
from multiprocessing import Pool
sys.path.insert(0, 'scripts/equity')
from gaphv_volstop3 import fill, ew_series, BARS, O, CLOSE, LAST

W, LATE, INF = 3, 350, 10 ** 9
T1S = (400, 450, 500, 600, 700, 800, 1000, INF)
T2S = (350, 400, 450, 500, 600, 700, INF)
PS = (INF, 1000)
CELLS = [(LATE, LATE, LATE)] + [(p, t1, t2) for p in PS for t1 in T1S for t2 in T2S if t1 >= t2]
OUT = 'data/equity/gaphv/volstop6.parquet'
def nm(v): return 'inf' if v >= INF else str(v)
def key(p, t1, t2): return f'p{nm(p)}_a{nm(t1)}_b{nm(t2)}'

def one(sec, px, vol, close, e0):
    r = {}
    t, ew, k = ew_series(sec, px, vol, e0)
    for p, t1, t2 in CELLS:
        thr = np.where(t < 34500, p, np.where(t < 34800, t1, np.where(t < 35100, t2, LATE))) if len(t) else np.array([])
        hit = (k >= W) & (t < LAST) & (ew >= thr) if len(t) else np.array([], bool)
        kk = key(p, t1, t2)
        if len(hit) and hit.any():
            i = np.argmax(hit); tt = int(t[i]) + 1
            r[kk + '_t'] = tt; r[kk + '_x'] = fill(sec, px, vol, tt, close)
        else:
            r[kk + '_t'] = -1; r[kk + '_x'] = np.nan
    return r

def day(args):
    d, rows = args
    f = f'{BARS}/{d}.parquet'
    if not os.path.exists(f): return []
    con = duckdb.connect(); con.execute("SET memory_limit='1GB'"); con.execute("SET threads=2")
    B = con.execute(f"""SELECT ticker, bucket, vwap, volume FROM '{f}' WHERE ticker IN (SELECT unnest(?))
                        AND bucket >= {O} AND bucket < {CLOSE} AND volume > 0 AND vwap > 0 ORDER BY ticker, bucket""", [list(rows.ticker)]).df()
    G = {t: g for t, g in B.groupby('ticker')}
    out = []
    for row in rows.itertuples():
        g = G.get(row.ticker)
        if g is None or len(g) < 10: continue
        r = one(g.bucket.values, g.vwap.values.astype(float), g.volume.values.astype(float), row.close, O)
        r['ticker'], r['d'] = row.ticker, d
        out.append(r)
    return out

if '--show' not in sys.argv:
    if __name__ == '__main__':
        t0 = time.time()
        E = pd.read_parquet('data/equity/gaphv/events.parquet', columns=['ticker', 'd', 'open', 'close'])
        jobs = [(str(pd.Timestamp(d).date()), g) for d, g in E.groupby('d')]
        res = []
        with Pool(6) as p:
            for i, r in enumerate(p.imap_unordered(day, jobs, chunksize=4)):
                res.extend(r)
                if i % 500 == 0: print(f'{i}/{len(jobs)} {time.time() - t0:.0f}s', flush=True)
        R = pd.DataFrame(res); R['d'] = pd.to_datetime(R.d).dt.date
        R.to_parquet(OUT, index=False); print(f'wrote {len(R):,}, {time.time() - t0:.0f}s')
else:
    with contextlib.redirect_stdout(io.StringIO()):
        from gaphv_exits import X, simulate, pf, f2
        from gaphv_volstop import hm2s
    X = X.merge(pd.read_parquet(OUT), on=['ticker', 'd'])
    # the 10:00 variants at today's stop (GH-13d's W = 3 · 350 run), constant across the schedules
    from gaphv_volstop3 import OUT as OUT3
    X3 = pd.read_parquet(OUT3)
    rest, rest_h, rest_w = 0.0, np.zeros(2), 0.0
    for flag, ent, rule in (('u1000', 'x1000', 'VWAP'), ('d1000', 'x1000', 'T1530')):
        K = X[X[flag] & np.isfinite(X[ent]) & (X[ent] > 0)].merge(X3[['ticker', 'd', 'e_w3_350_t', 'e_w3_350_x']], on=['ticker', 'd']).reset_index(drop=True)
        K['entry'] = K[ent]; px, at = simulate(K, rule); base = 1 - px / K.entry.values
        bsec = np.array([57600 if a == 'close' else hm2s(a) for a in at])
        t = K.e_w3_350_t.values; x = K.e_w3_350_x.values; fire = (t > 0) & np.isfinite(x) & (t < bsec)
        r = 100 * np.where(fire, 1 - x / K.entry.values, base)
        rest += r.sum(); rest_h += [r[(K.yr <= 2020).values].sum(), r[(K.yr >= 2021).values].sum()]; rest_w = min(rest_w, r.min())
    books = []
    for name, flag, ent, rule in (('U0930', 'u0930', 'open', 'LOSER'), ('D0930', 'd0930', 'open', 'T1530')):
        K = X[X[flag] & np.isfinite(X[ent]) & (X[ent] > 0)].copy().reset_index(drop=True); K['entry'] = K[ent]
        px, at = simulate(K, rule); base = 1 - px / K.entry.values
        bsec = np.array([57600 if a == 'close' else hm2s(a) for a in at])
        books.append((name, K, base, bsec))
    WATCH = [('CNST', '2019-11-06'), ('PHUN', '2021-10-22'), ('YELL', '2023-08-01'), ('ENTA', '2025-09-29'), ('PGHL', '2024-11-26'), ('ZJYL', '2023-12-18')]
    rows = []
    for p, t1, t2 in CELLS:
        kk = key(p, t1, t2); tot, h, worst, per, watch = rest, rest_h.copy(), rest_w, {}, {}
        for name, K, base, bsec in books:
            t = K[kk + '_t'].values; x = K[kk + '_x'].values; fire = (t > 0) & np.isfinite(x) & (t < bsec)
            r = 100 * np.where(fire, 1 - x / K.entry.values, base)
            tot += r.sum(); h += [r[(K.yr <= 2020).values].sum(), r[(K.yr >= 2021).values].sum()]; worst = min(worst, r.min())
            s = np.sort(r); per[name] = (r.sum(), s[0], s[:max(1, len(s) // 100)].mean(), pf(r))
            for tk, dd in WATCH:
                z = np.where((K.ticker == tk).values & (K.d.astype(str) == dd).values)[0]
                if len(z): watch[tk] = r[z[0]]
        rows.append(dict(p=p, t1=t1, t2=t2, tot=tot, h1=h[0], h2=h[1], worst=worst, per=per, watch=watch))
    R = pd.DataFrame(rows)
    R['r1'] = R.h1.rank(ascending=False).astype(int); R['r2'] = R.h2.rank(ascending=False).astype(int)
    c = R.iloc[0]
    print('# GH-13g — a stepped threshold schedule (P before 09:35 · T1 09:35–09:40 · T2 09:40–09:45 · 350 from 09:45)\n')
    print(f'{len(R)} schedules. All four variants (the 10:00 ones at today\'s stop, constant). Σ % · the halves 2016–2020 / '
          f'2021–2026 with their ranks among the {len(R)} · the worst trade · U0930 / D0930 CVaR 1 % · the watched trades. '
          f'The control (350 everywhere = today\'s stop) first; then the top 25 by the sum.\n')
    def line(q, bold=False):
        b = '**' if bold else ''
        w = ' | '.join(f'{q.watch.get(tk, float("nan")):+.0f}' for tk, _ in WATCH)
        return (f'| {b}{nm(q.p)}{b} | {b}{nm(q.t1)}{b} | {b}{nm(q.t2)}{b} | {q.tot:,.0f} ({100 * (q.tot / c.tot - 1):+.1f} %) | '
                f'{q.h1:,.0f} (#{q.r1}) / {q.h2:,.0f} (#{q.r2}) | {q.worst:+.0f} % | {q.per["U0930"][2]:+.1f} / {q.per["D0930"][2]:+.1f} % | {w} |')
    hdr = ('| P | T1 | T2 | all four | halves (rank) | worst | CVaR 1 % U / D | ' + ' | '.join(f'{t} {d[:4]}' for t, d in WATCH) + ' |\n'
           '|---|---|---|---|---|---|---|' + '---|' * len(WATCH))
    print(hdr); print(line(c, True))
    for q in R.iloc[1:].sort_values('tot', ascending=False).head(25).itertuples(): print(line(q))
    print('\n## The schedules in the top 10 of BOTH halves\n')
    print(hdr)
    both = R[(R.r1 <= 10) & (R.r2 <= 10)]
    for q in both.sort_values('tot', ascending=False).itertuples(): print(line(q))
    print(f'\n({len(both)} schedules.) Rank correlation of the halves over all {len(R)}: {R.h1.corr(R.h2, method="spearman"):+.2f}.')
