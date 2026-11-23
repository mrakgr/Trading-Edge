"""GH-20 — the break of the 10 ET high after 10:30, from the LONG side (user 2026-11-23: "Let's consider this from the long
side ... filter down to eligible candidates at 10 ET. That means that the z score > -2 and that the volatility stop hasn't
been activated ... look for the first break above the 10 ET high past the 10:30 ET period and look back whether that level
was also breached during 10-10:30 ET").

The universe: the 10:00-eligible stock-days (U1000 / D1000), kept only when z > −2 (GH-16) AND the volatility stop computed
from 09:30 (GH-13g's off · 700 · 500 · 350) has NOT fired before 10:00. H30 = the 10 ET high = the highest 1s vwap of
09:30–10:00. The groups, by what the price did after 10:00:
  fresh       not above H30 during 10:00–10:30; its first crossing above H30 comes after 10:30 (the entry = that crossing)
  re-break    above H30 during 10:00–10:30, back at or below it, and crossing above again after 10:30 (the entry = that)
  still above above H30 during 10:00–10:30 and still above at 10:30 (no new crossing: the entry = 10:30)
  never       not above H30 after 10:30 (no trade)
The trade: LONG from the $5k VWAP of the second after the entry (to 15:00 for the entry), out at 15:30 ($5k fill); with and
without the ew · any 350 bp stop from the entry (W = 3, both sides). No VWAP rule.
    cd research && python3 scripts/equity/gaphv_break30.py > logs/gaphv_break30.log 2>&1
    cd research && python3 scripts/equity/gaphv_break30.py --show > data/equity/gaphv/break30.md
"""
import duckdb, numpy as np, pandas as pd, os, sys, io, contextlib, time
from multiprocessing import Pool
sys.path.insert(0, 'scripts/equity')
from gaphv_volstop3 import fill, ew_series, BARS, O, CLOSE

T10, T1030, LAST_ENTRY, OUT_T = 36000, 37800, 54000, 55800
OUT = 'data/equity/gaphv/break30.parquet'

def one(sec, px, vol, close):
    w1 = (sec >= O) & (sec < T10)
    if not w1.any(): return dict(grp='none')
    h30 = px[w1].max()
    w2 = (sec >= T10) & (sec < T1030)
    above2 = bool((px[w2] > h30).any())
    before = np.where(sec < T1030)[0]
    at1030_above = bool(before.size and px[before[-1]] > h30)
    r = dict(h30=h30, above1000=above2, at1030_above=at1030_above)
    if above2 and at1030_above:
        grp, te = 'still above', T1030 - 1                      # the entry from 10:30:00
    else:
        prev = np.concatenate([[np.inf], px[:-1]])
        hit = np.where((sec >= T1030) & (sec < LAST_ENTRY) & (px > h30) & (prev <= h30))[0]
        if not len(hit): return dict(r, grp='never')
        grp, te = ('re-break' if above2 else 'fresh'), int(sec[hit[0]])
    xe = fill(sec, px, vol, te + 1, close)
    xo = fill(sec, px, vol, OUT_T, close)
    t, ew, k = ew_series(sec, px, vol, te + 1)
    hit = (ew >= 350) & (k >= 3) & (t < OUT_T) if len(t) else np.array([], bool)
    if len(hit) and hit.any():
        ts = int(t[np.argmax(hit)]) + 1; xs = fill(sec, px, vol, ts, close)
    else:
        ts, xs = -1, np.nan
    return dict(r, grp=grp, te=te, xe=xe, xo=xo, ts=ts, xs=xs)

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
        r = one(g.bucket.values, g.vwap.values.astype(float), g.volume.values.astype(float), row.close)
        r['ticker'], r['d'] = row.ticker, d
        out.append(r)
    return out

if '--show' not in sys.argv:
    if __name__ == '__main__':
        t0 = time.time()
        E = pd.read_parquet('data/equity/gaphv/events.parquet', columns=['ticker', 'd', 'u1000', 'd1000', 'close'])
        E = E[E.u1000 | E.d1000]
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
        from gaphv_exits import X, pf, f2
    X = (X.merge(pd.read_parquet(OUT), on=['ticker', 'd'])
          .merge(pd.read_parquet('data/equity/gaphv/entry10.parquet')[['ticker', 'd', 'z']], on=['ticker', 'd'], how='left')
          .merge(pd.read_parquet('data/equity/gaphv/volstop6.parquet')[['ticker', 'd', 'pinf_a700_b500_t']], on=['ticker', 'd'], how='left'))
    print('# GH-20 — the first break of the 10 ET high after 10:30, from the LONG side\n')
    print('Eligible at 10:00: z > −2 and the 09:30 volatility stop (off · 700 · 500 · 350) not fired before 10:00. H30 = the '
          '09:30–10:00 high (1s vwap). The trade: LONG at the $5k VWAP after the entry, out at 15:30; "+ stop" = the 350 bp ew · '
          'any stop from the entry. The long, gross; Σ = Σ %; PF halves = 2016–2020 / 2021–2026.\n')
    GR = ['fresh', 're-break', 'still above']
    for name, flag in (('U1000', 'u1000'), ('D1000', 'd1000')):
        A = X[X[flag]].copy()
        n0 = len(A)
        zc = (A.z < -2.0).values
        vs = ((A.pinf_a700_b500_t > 0) & (A.pinf_a700_b500_t < 36000)).values
        A = A[~zc & ~vs].reset_index(drop=True)
        print(f'## {name}: {n0:,} stock-days; skipped z < −2: {zc.sum():,}; the stop fired before 10:00: {vs.sum():,}; eligible: {len(A):,}\n')
        cnt = A.grp.value_counts()
        print('| group | stock-days | share |')
        print('|---|---|---|')
        for g in GR + ['never']:
            print(f'| {g} | {cnt.get(g, 0):,} | {cnt.get(g, 0) / len(A):.0%} |')
        print()
        print('| group | n | hold to 15:30: PF · mean · Σ · win · worst · PF halves | + the stop: PF · mean · Σ · worst · PF halves | entry median |')
        print('|---|---|---|---|---|')
        for g in GR:
            K = A[(A.grp == g) & np.isfinite(A.xe) & (A.xe > 0)]
            if not len(K): continue
            ro = 100 * (K.xo.values / K.xe.values - 1)
            st = (K.ts.values > 0) & np.isfinite(K['xs'].values)
            rs = np.where(st, 100 * (K['xs'].values / K.xe.values - 1), ro)
            h1 = (K.yr <= 2020).values
            em = pd.Series(K.te.values).median()
            print(f'| **{g}** | {len(K):,} | {f2(pf(ro))} · {ro.mean():+.2f} % · {ro.sum():,.0f} · {(ro > 0).mean():.0%} · {ro.min():+.0f} % · '
                  f'{f2(pf(ro[h1]))} / {f2(pf(ro[~h1]))} | {f2(pf(rs))} · {rs.mean():+.2f} % · {rs.sum():,.0f} · {rs.min():+.0f} % · '
                  f'{f2(pf(rs[h1]))} / {f2(pf(rs[~h1]))} | {int(em) // 3600:02d}:{int(em) % 3600 // 60:02d} |')
        print()
