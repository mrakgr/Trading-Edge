"""GH-17 — the 10:00-eligible stocks shorted at the FIRST NEW SESSION HIGH after 10:00 (user 2026-11-23: "how a system that
shorts 10 ET eligible stocks at the session highs would do?"; the user's choices: the first new high after 10:00, one trade
per stock-day; the RTH high since 09:30, the premarket ignored).

The universe: U1000 / D1000 (gaphv_events). The high = the highest 1s vwap of [09:30, 10:00) (the slim bars carry a 1s vwap,
no high); the entry = the first 1s bar at or after 10:00 whose vwap exceeds it, until 15:00; the fill = the $5k VWAP from the
second after (GH-13's fill, no time cap). The exits: the variant's GH-6 rule (U1000 VWAP, D1000 T1530) at the half-hour marks
AFTER the entry, out at 15:30 otherwise; plus the ew · any stop from the entry (W = 3, 350 bp — the 10:00 schedule) when it
fires before the rule's exit. The control: today's 10:00 entry on the same stock-days.
    cd research && python3 scripts/equity/gaphv_newhigh.py > logs/gaphv_newhigh.log 2>&1
    cd research && python3 scripts/equity/gaphv_newhigh.py --show > data/equity/gaphv/newhigh.md
"""
import duckdb, numpy as np, pandas as pd, os, sys, io, contextlib, time
from multiprocessing import Pool
sys.path.insert(0, 'scripts/equity')
from gaphv_volstop3 import fill, ew_series, BARS, O, CLOSE, LAST

T10, LAST_ENTRY = 36000, 54000
W, TH = 3, 350
OUT = 'data/equity/gaphv/newhigh.parquet'

def one(sec, px, vol, close):
    pre = (sec >= O) & (sec < T10)
    if not pre.any(): return dict(te=-1)
    hi = px[pre].max()
    post = np.where((sec >= T10) & (sec < LAST_ENTRY) & (px > hi))[0]
    if not len(post): return dict(te=-1, hi30=hi)
    te = int(sec[post[0]])
    xe = fill(sec, px, vol, te + 1, close)
    t, ew, k = ew_series(sec, px, vol, te + 1)
    hit = (ew >= TH) & (k >= W) & (t < LAST) if len(t) else np.array([], bool)
    if len(hit) and hit.any():
        i = np.argmax(hit); ts = int(t[i]) + 1; xs = fill(sec, px, vol, ts, close)
    else:
        ts, xs = -1, np.nan
    return dict(te=te, hi30=hi, xe=xe, ts=ts, xs=xs, px_hi=float(px[post[0]]))

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
        from gaphv_exits import X, simulate, pf, f2, CHK, PREV
        from gaphv_volstop import hm2s
    X = X.merge(pd.read_parquet(OUT), on=['ticker', 'd'], how='left').merge(
        pd.read_parquet('data/equity/gaphv/volstop3.parquet')[['ticker', 'd', 'e_w3_350_t', 'e_w3_350_x']], on=['ticker', 'd'], how='left')

    def exits_after(K, rule):
        """the GH-6 rule at the marks AFTER the entry (K.te); 15:30 otherwise — gaphv_exits.simulate with the marks gated"""
        n = len(K); px = np.full(n, np.nan); at = np.full(n, 57600.0); done = np.zeros(n, bool)
        for t in CHK:
            ms = hm2s(t); after = (K.te.values < ms)
            fire = np.zeros(n, bool)
            if rule.startswith('VWAP') or rule.startswith('BOTH'):
                fire |= (K[f'v30_{PREV[t]}'] < 0.5).fillna(False).values & (K.te.values < ms - 1800)   # the whole previous half hour after the entry
            if (rule == 'LOSER' or rule.startswith('BOTH')) and t >= '1100':
                fire |= (K[f'p_{t}'] > K.entry).fillna(False).values
            x = K[f'x{t}'].values
            go = fire & after & ~done & np.isfinite(x)
            px[go] = x[go]; at[go] = ms; done |= go
        last = K.x1530.values; rest = ~done
        px[rest] = np.where(np.isfinite(last[rest]), last[rest], K.close.values[rest]); at[rest] = 55800
        return px, at

    def stats(r, K):
        s = np.sort(r); k1 = max(1, len(s) // 100); h1 = (K.yr <= 2020).values
        return (f'{len(r):,} | {f2(pf(r))} | {r.mean():+.2f} % | {r.sum():,.0f} | {(r > 0).mean():.0%} | {s[0]:+.0f} % | {s[:k1].mean():+.1f} % | '
                f'{f2(pf(r[h1]))} / {f2(pf(r[~h1]))}')

    print('# GH-17 — the 10:00-eligible stocks shorted at the first new session high after 10:00\n')
    print('The high = the 1s-vwap high of 09:30–10:00; the entry = the first 1s bar after 10:00 above it (to 15:00), filled at the $5k '
          'VWAP from the next second. Exits: the GH-6 rule at the marks after the entry + the 350 bp stop from the entry. The '
          'control: the 10:00 entry on the same stock-days. The short, gross; Σ = Σ %.\n')
    for name, flag, rule in (('U1000', 'u1000', 'VWAP'), ('D1000', 'd1000', 'T1530')):
        A = X[X[flag] & np.isfinite(X.x1000) & (X.x1000 > 0)].copy().reset_index(drop=True)
        # the control: today's 10:00 entry (the full GH-6 rule + today's stop)
        C = A.copy(); C['entry'] = C.x1000
        px, at = simulate(C, rule); base = 1 - px / C.entry.values
        bsec = np.array([57600 if a == 'close' else hm2s(a) for a in at])
        t = C.e_w3_350_t.values; x = C.e_w3_350_x.values; fire = (t > 0) & np.isfinite(x) & (t < bsec)
        C['r'] = 100 * np.where(fire, 1 - x / C.entry.values, base)
        trig = (A.te.fillna(-1) > 0).values & np.isfinite(A.xe.values)
        K = A[trig].copy().reset_index(drop=True); K['entry'] = K.xe
        px, at = exits_after(K, rule)
        stop = (K['ts'].values > 0) & np.isfinite(K['xs'].values) & (K['ts'].values < at)
        K['r'] = 100 * np.where(stop, 1 - K['xs'].values / K.entry.values, 1 - px / K.entry.values)
        Ct = C[trig]
        print(f'## {name} ({rule}): {len(A):,} stock-days; {trig.sum():,} ({trig.mean():.0%}) make a new high after 10:00\n')
        print('| book | n | PF | mean | Σ | win % | worst | CVaR 1 % | PF halves |')
        print('|---|---|---|---|---|---|---|---|---|')
        print(f'| the 10:00 entry, all stock-days (today) | {stats(C.r.values, C)} |')
        print(f'| the 10:00 entry, the stock-days that later make a new high | {stats(Ct.r.values, Ct)} |')
        print(f'| the 10:00 entry, the others | {stats(C.r.values[~trig], C[~trig])} |')
        print(f'| **the first new high after 10:00** | {stats(K.r.values, K)} |')
        print()
        K['hh'] = (K.te // 1800) * 1800
        print('By the entry\'s half hour:\n')
        print('| entry from | n | PF | mean | Σ | worst |')
        print('|---|---|---|---|---|---|')
        for h, g in K.groupby('hh'):
            print(f'| {int(h) // 3600:02d}:{int(h) % 3600 // 60:02d} | {len(g):,} | {f2(pf(g.r.values))} | {g.r.mean():+.2f} % | {g.r.sum():,.0f} | {g.r.min():+.0f} % |')
        print(f'\nThe entry vs the 10:00 price: median {100 * (K.entry / K.x1000 - 1).median():+.1f} % · the stop fired on {stop.mean():.0%} · '
              f'the worst five: ' + '; '.join(f'{w.ticker} {w.d} {w.r:+.0f} %' for w in K.nsmallest(5, 'r').itertuples()) + '\n')
