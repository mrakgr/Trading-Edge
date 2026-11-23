"""GH-16 — the 10:00 variants' ENTRY: halts and the plunge from 09:30 to 10:00 (user 2026-11-23: "we need to at least avoid
entering stocks like ZJYL that are being halted. In ZJYL's case in particular we need to avoid entering stocks that have
plunged down too much. Maybe we'll introduce a slope and a z score filter from 9:30 to 10 ET"). ZJYL 2023-12-18 missed U1000
only by its gap (3.5 % < 4 %): at 10:00 it was −50 % from the open, volat30 653 bp, halted (09:58:54 → 10:13:28).

The features, all from the 1s bars BEFORE 10:00:00 (knowable at the entry):
  halt_s    the longest silence in [09:30, 10:00) between present bars, the one running INTO 10:00 included (s)
  halted10  the silence running into 10:00 is >= 60 s (the stock is not trading at the entry)
  halts     the silences >= 290 s (a LULD pause is >= 5 min) in the window
  slope     OLS of ln(30 s slot vwap) on time over the traded slots, bp / min
  z         (ln last slot vwap − the window's mean) / its sd — where the entry sits in the 30-minute distribution
  ret30, volat30   (gaphv_events)
The trades: U1000 / D1000 with the GH-6 exit + today's stop from 10:00 (GH-13d W = 3 · 350); the $5k fill.
    cd research && python3 scripts/equity/gaphv_entry10.py > logs/gaphv_entry10.log 2>&1
    cd research && python3 scripts/equity/gaphv_entry10.py --show > data/equity/gaphv/entry10.md
"""
import duckdb, numpy as np, pandas as pd, os, sys, io, contextlib, time
from multiprocessing import Pool

BARS = 'data/intraday_1s_slim'
O, T10 = 34200, 36000
OUT = 'data/equity/gaphv/entry10.parquet'

def feats(sec, px, vol):
    m = (sec >= O) & (sec < T10); s, p, v = sec[m], px[m], vol[m]
    if len(s) < 2: return dict(halt_s=1800.0, halted10=True, halts=1, slope=np.nan, z=np.nan, n_bars=len(s))
    gaps = np.diff(np.concatenate([s, [T10]])).astype(float); gaps[-1] = T10 - s[-1]
    slot = (s - O) // 30
    pv = np.bincount(slot, p * v, 60); vv = np.bincount(slot, v, 60); tr = np.where(vv > 0)[0]
    lv = np.log(pv[tr] / vv[tr]); tm = (tr * 30 + 15) / 60.0
    slope = 1e4 * np.polyfit(tm, lv, 1)[0] if len(tr) >= 3 else np.nan
    sd = lv.std()
    z = (lv[-1] - lv.mean()) / sd if len(tr) >= 3 and sd > 0 else np.nan
    return dict(halt_s=gaps.max(), halted10=gaps[-1] >= 60, halts=int((gaps >= 290).sum()), slope=slope, z=z, n_bars=len(s))

def day(args):
    d, rows = args
    f = f'{BARS}/{d}.parquet'
    if not os.path.exists(f): return []
    con = duckdb.connect(); con.execute("SET memory_limit='1GB'"); con.execute("SET threads=2")
    B = con.execute(f"""SELECT ticker, bucket, vwap, volume FROM '{f}' WHERE ticker IN (SELECT unnest(?))
                        AND bucket >= {O} AND bucket < {T10} AND volume > 0 AND vwap > 0 ORDER BY ticker, bucket""", [list(rows.ticker)]).df()
    G = {t: g for t, g in B.groupby('ticker')}
    out = []
    for row in rows.itertuples():
        g = G.get(row.ticker)
        r = feats(g.bucket.values, g.vwap.values.astype(float), g.volume.values.astype(float)) if g is not None else \
            dict(halt_s=1800.0, halted10=True, halts=1, slope=np.nan, z=np.nan, n_bars=0)
        r['ticker'], r['d'] = row.ticker, d
        out.append(r)
    return out

if '--show' not in sys.argv:
    if __name__ == '__main__':
        t0 = time.time()
        E = pd.read_parquet('data/equity/gaphv/events.parquet', columns=['ticker', 'd', 'u1000', 'd1000'])
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
    sys.path.insert(0, 'scripts/equity')
    with contextlib.redirect_stdout(io.StringIO()):
        from gaphv_exits import X, simulate, pf, f2
        from gaphv_volstop import hm2s
    X = X.merge(pd.read_parquet(OUT), on=['ticker', 'd']).merge(
        pd.read_parquet('data/equity/gaphv/volstop3.parquet')[['ticker', 'd', 'e_w3_350_t', 'e_w3_350_x']], on=['ticker', 'd'])
    print('# GH-16 — the 10:00 entry: halts and the plunge, 09:30 → 10:00\n')
    print('The trades: GH-6 exit + today\'s stop from 10:00; the $5k fill. Per band: n · mean % · PF · Σ % · worst · the bottom 1 % '
          'of the band\'s trades. Features from the bars before 10:00 only.\n')
    FE = [('ret30', 'ret30 (gaphv_events: the 09:30 → 10:00 move)'), ('slope', 'slope, bp / min'), ('z', 'z of the 10:00 price'),
          ('volat30', 'volat30, bp'), ('halt_s', 'the longest silence, s')]
    for name, flag, rule in (('U1000', 'u1000', 'VWAP'), ('D1000', 'd1000', 'T1530')):
        A = X[X[flag]].copy()
        miss = A.x1000.isna() | (A.x1000 <= 0)
        K = A[~miss].copy().reset_index(drop=True); K['entry'] = K.x1000
        px, at = simulate(K, rule); base = 1 - px / K.entry.values
        bsec = np.array([57600 if a == 'close' else hm2s(a) for a in at])
        t = K.e_w3_350_t.values; x = K.e_w3_350_x.values; fire = (t > 0) & np.isfinite(x) & (t < bsec)
        K['r'] = 100 * np.where(fire, 1 - x / K.entry.values, base)
        print(f'## {name} ({rule}): {len(K):,} trades; {miss.sum()} more had NO 10:00 price (no fill to the close: '
              f'dropped by the backtest — halted into the close, e.g. PGHL 2024-11-26)\n')
        print(f'All: mean {K.r.mean():+.2f} % · PF {f2(pf(K.r.values))} · Σ {K.r.sum():,.0f} · worst {K.r.min():+.0f} %\n')
        for col, lab in FE:
            q = K[col].quantile([.0, .01, .05, .1, .25, .5, .75, .9, .95, .99, 1.0]).values
            edges = np.unique(q)
            K['band'] = pd.cut(K[col], edges, include_lowest=True, duplicates='drop')
            print(f'**{lab}**\n')
            print('| band | n | mean | PF | Σ | worst | bottom 1 % |')
            print('|---|---|---|---|---|---|---|')
            for b, g in K.groupby('band', observed=True):
                s = np.sort(g.r.values); k1 = max(1, len(s) // 100)
                print(f'| {b} | {len(g):,} | {g.r.mean():+.2f} % | {f2(pf(g.r.values))} | {g.r.sum():,.0f} | {s[0]:+.0f} % | {s[:k1].mean():+.1f} % |')
            print()
        for lab, m in (('halted at 10:00 (silence into 10:00 >= 60 s)', K.halted10), ('a silence >= 290 s in 09:30–10:00', K.halts > 0)):
            g = K[m]; o = K[~m]
            print(f'**{lab}**: {len(g)} trades · mean {g.r.mean():+.2f} % · PF {f2(pf(g.r.values))} · Σ {g.r.sum():,.0f} · worst '
                  f'{g.r.min():+.0f} % — the rest: mean {o.r.mean():+.2f} % · PF {f2(pf(o.r.values))}\n')
        print('The 15 worst trades and their features:\n')
        print('| trade | result | ret30 | slope | z | volat30 | longest silence | halted at 10:00 |')
        print('|---|---|---|---|---|---|---|---|')
        for w in K.nsmallest(15, 'r').itertuples():
            print(f'| {w.ticker} {w.d} | {w.r:+.0f} % | {100 * w.ret30:+.1f} % | {w.slope:+.0f} | {w.z:+.2f} | {w.volat30:.0f} | {w.halt_s:.0f} s | {w.halted10} |')
        print()
