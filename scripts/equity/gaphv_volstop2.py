"""GH-13 — the disaster stop, three volatility clocks × three triggers, with an HONEST fill (user 2026-11-21: "Yes, let's give
them a try. I am curious how a FlushFader style window would do"). After GH-12's ZJYL finding (the 30-min-capped $5k fill
accepted 200 shares at the limit-up band before a 70-minute pause).

The measures, every one from the ENTRY (09:30 / 10:00) on, checked to 15:30:
  clk  GH-12's: 10 grid-aligned 30 s CLOCK slots, the simple mean of |ln(v_j/v_i)| over successive traded slots inside the
       window, evaluated at each :00 / :30 (blind across a pause: a reopening > 5 min after the last print is not counted)
  pb   FlushFader's slot clock: 30 PRESENT 1s bars per slot (SlotVwapMa), the simple mean of |r| over the last 10 slots
       (≥ 3 returns since the entry); evaluated at each slot's completing bar. A pause is one return (the reopen counts).
  ew   the same present-bar slots, FlushFader's bias-corrected EWMA of |r| (EmaHlMa) at a half-life of 10 slots (FlushFader's
       volat_20m is hl = 40) — ≥ 3 returns since the entry.
Triggers: any = the volatility alone (both sides) · win = the window moved AGAINST the short (its last slot vwap > its first;
for pb / ew the last 10 slots) · uw = the position is UNDERWATER (the last slot vwap > the entry price).
The fill (all three): the $5k VWAP from the second after the decision with NO time cap; a shortfall still open at the close
is filled at the official close. ("r5" = the GH-10 ruling — clk, both sides, the 5-minute window only — on the same fill.)
Pass 1 → data/equity/gaphv/volstop2.parquet. Pass 2 (--report): on top of each variant's GH-6 exit (the first exit wins).
    cd research && python3 scripts/equity/gaphv_volstop2.py > logs/gaphv_volstop2.log 2>&1
    cd research && python3 scripts/equity/gaphv_volstop2.py --report > data/equity/gaphv/volstop2.md
"""
import duckdb, numpy as np, pandas as pd, os, sys, io, contextlib, time
from multiprocessing import Pool

BARS = 'data/intraday_1s_slim'
O, CLOSE, LAST = 34200, 57600, 55800
TH = (200, 300, 350, 400, 500, 600)
KINDS = ('any', 'win', 'uw')
HL = 10.0

def fill(sec, px, vol, t, close):
    a = np.searchsorted(sec, t)
    if a >= len(sec): return close
    dv = np.cumsum(px[a:] * vol[a:]); k = np.searchsorted(dv, 5000.0)
    if k < len(dv): return dv[k] / vol[a:a + k + 1].sum()
    sh = 5000.0 - dv[-1]                                   # the shortfall at the official close
    return 5000.0 / (vol[a:].sum() + sh / close)

def clock_series(sec, px, vol, e0):
    """clk: per window end (each 30 s boundary from e0 + 300 s): volat bp, window-up, the last traded slot vwap"""
    ns = (CLOSE - e0) // 30
    sl = (sec - e0) // 30; m = sl >= 0
    pv = np.bincount(sl[m], (px * vol)[m], ns); v = np.bincount(sl[m], vol[m], ns)
    tr = np.where(v > 0)[0]; sv = pv[tr] / v[tr] if len(tr) else np.array([])
    W = ns - 9
    if W <= 0: return np.array([]), np.array([]), np.array([]), np.array([])
    sm, cnt = np.zeros(W + 1), np.zeros(W + 1)
    if len(tr) >= 2:
        i, j = tr[:-1], tr[1:]; a = np.abs(np.diff(np.log(sv)))
        lo, hi = np.maximum(j - 9, 0), np.minimum(i, W - 1); ok = lo <= hi
        np.add.at(sm, lo[ok], a[ok]); np.add.at(sm, hi[ok] + 1, -a[ok]); np.add.at(cnt, lo[ok], 1); np.add.at(cnt, hi[ok] + 1, -1)
    sm, cnt = np.cumsum(sm)[:W], np.cumsum(cnt)[:W]
    vb = np.where(cnt > 0, 1e4 * sm / np.where(cnt > 0, cnt, 1), 0.0)
    full = np.full(ns, np.nan)
    if len(tr): full[tr] = sv
    idx = np.where(np.isfinite(full), np.arange(ns), -1); lastI = np.maximum.accumulate(idx)
    idx2 = np.where(np.isfinite(full), np.arange(ns), ns); nextI = np.minimum.accumulate(idx2[::-1])[::-1]
    s = np.arange(W); fi, la = nextI[s], lastI[s + 9]
    good = (fi < ns) & (la >= 0) & (fi <= s + 9) & (la >= s)
    up = np.zeros(W, bool); lastpx = np.full(W, np.nan)
    up[good] = full[la[good]] > full[fi[good]]; lastpx[good] = full[la[good]]
    tdec = e0 + (s + 10) * 30
    return tdec, vb, up, lastpx

def pb_series(sec, px, vol, e0):
    """pb / ew: per completed 30-present-bar slot after e0 (from the 4th slot: >= 3 returns)"""
    a = np.searchsorted(sec, e0); n = (len(sec) - a) // 30
    if n < 4: return (np.array([]),) * 5
    p, v = px[a:a + 30 * n].reshape(n, 30), vol[a:a + 30 * n].reshape(n, 30)
    sv = (p * v).sum(1) / v.sum(1)
    tdec = sec[a + 30 * np.arange(n) + 29]
    ar = np.abs(np.diff(np.log(sv)))                     # return k ends at slot k+1
    cs = np.concatenate([[0.0], np.cumsum(ar)])
    k = np.arange(1, n)                                   # slot k has returns 0..k-1
    lo = np.maximum(k - 10, 0)
    mb = 1e4 * (cs[k] - cs[lo]) / (k - lo)
    al = 1 - 0.5 ** (1 / HL); num = den = 0.0; ew = np.empty(n - 1)
    for i, x in enumerate(ar):
        num = (1 - al) * num + al * x; den = (1 - al) * den + al; ew[i] = 1e4 * num / den
    first = sv[np.maximum(k - 9, 0)]; up = sv[k] > first
    keep = k >= 3
    return tdec[k][keep], mb[keep], ew[keep], up[keep], sv[k][keep]

def one(sec, px, vol, close, entries):
    r = {}
    for tag, e0, ent in entries:
        series = []
        t, vb, up, lp = clock_series(sec, px, vol, e0); series.append(('clk', t, vb, up, lp))
        t2, mb, ew, up2, lp2 = pb_series(sec, px, vol, e0)
        series += [('pb', t2, mb, up2, lp2), ('ew', t2, ew, up2, lp2)]
        for name, t, val, up, lp in series:
            for kind in KINDS:
                cond = np.ones(len(t), bool) if kind == 'any' else (up if kind == 'win' else (lp > ent))
                for th in TH:
                    hit = (val >= th) & cond & (t < LAST) if len(t) else np.array([], bool)
                    if len(hit) and hit.any():
                        i = np.argmax(hit); tt = int(t[i]) + (0 if name == 'clk' else 1)
                        r[f'{tag}_{name}_{kind}_{th}_t'] = tt; r[f'{tag}_{name}_{kind}_{th}_x'] = fill(sec, px, vol, tt, close)
                    else:
                        r[f'{tag}_{name}_{kind}_{th}_t'] = -1; r[f'{tag}_{name}_{kind}_{th}_x'] = np.nan
        f5 = e0 + 300
        r[f'{tag}_r5_x'] = fill(sec, px, vol, f5, close)
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
        ent = [('o', O, row.open)] + ([('e', 36000, row.x1000)] if np.isfinite(row.x1000) else [])
        r = one(g.bucket.values, g.vwap.values.astype(float), g.volume.values.astype(float), row.close, ent)
        r['ticker'], r['d'] = row.ticker, d
        out.append(r)
    return out

if '--report' not in sys.argv:
    if __name__ == '__main__':
        t0 = time.time()
        E = pd.read_parquet('data/equity/gaphv/events.parquet', columns=['ticker', 'd', 'open', 'close']).merge(
            pd.read_parquet('data/equity/gaphv/path.parquet', columns=['ticker', 'd', 'x1000']), on=['ticker', 'd'])
        jobs = [(str(pd.Timestamp(d).date()), g) for d, g in E.groupby('d')]
        res = []
        with Pool(8) as p:
            for i, r in enumerate(p.imap_unordered(day, jobs, chunksize=4)):
                res.extend(r)
                if i % 500 == 0: print(f'{i}/{len(jobs)} {time.time() - t0:.0f}s', flush=True)
        R = pd.DataFrame(res); R['d'] = pd.to_datetime(R.d).dt.date
        R.to_parquet('data/equity/gaphv/volstop2.parquet', index=False); print(f'wrote {len(R):,}, {time.time() - t0:.0f}s')
else:
    sys.path.insert(0, 'scripts/equity')
    with contextlib.redirect_stdout(io.StringIO()):
        from gaphv_exits import X, simulate, pf, f2
        from gaphv_volstop import hm2s
    X = X.merge(pd.read_parquet('data/equity/gaphv/volstop2.parquet'), on=['ticker', 'd']).merge(
        pd.read_parquet('data/equity/gaphv/exit5.parquet')[['ticker', 'd', 'v5o', 'v5e']], on=['ticker', 'd'])
    V = [('U0930', 'u0930', 'open', 'LOSER', 'o', 'v5o'), ('D0930', 'd0930', 'open', 'T1530', 'o', 'v5o'),
         ('U1000', 'u1000', 'x1000', 'VWAP', 'e', 'v5e'), ('D1000', 'd1000', 'x1000', 'T1530', 'e', 'v5e')]
    print('# GH-13 — the disaster stop: three volatility clocks × three triggers, the uncapped $5k fill\n')
    print('On top of each variant\'s GH-6 exit (the first exit wins). clk = 10 grid-aligned 30 s clock slots, simple mean · '
          'pb = 10 slots of 30 PRESENT 1s bars, simple mean · ew = the same slots, EWMA hl 10 slots. any = both sides · win = '
          'the window moved against the short · uw = the position underwater vs the entry. r5 = the GH-10 ruling (clk, both '
          'sides, the 5-minute window only), on the same fill. The short, gross; sum = Σ %.\n')
    for name, flag, ent, rule, tag, v5 in V:
        K = X[X[flag] & np.isfinite(X[ent]) & (X[ent] > 0)].copy().reset_index(drop=True); K['entry'] = K[ent]
        px, at = simulate(K, rule); base = 1 - px / K.entry.values
        bsec = np.array([57600 if a == 'close' else hm2s(a) for a in at])
        f5 = 34500 if tag == 'o' else 36300
        def res(fire, x):
            r = 100 * np.where(fire, 1 - x / K.entry.values, base); s = np.sort(r); k1, k5 = max(1, len(s) // 100), max(1, len(s) // 20)
            return r, s, k1, k5
        def line(lab, fire, x):
            r, s, k1, k5 = res(fire, x)
            fx = f'{100 * (1 - x[fire] / K.entry.values[fire]).mean():+.1f} / {100 * base[fire].mean():+.1f} %' if fire.any() else ''
            print(f'| {lab} | {fire.sum() / 10.1:.0f} | {f2(pf(r))} | {r.sum():,.0f} | {s[0]:+.0f} % | {s[:k1].mean():+.1f} % | {s[:k5].mean():+.1f} % | '
                  f'{f2(pf(r[(K.yr <= 2020).values]))} / {f2(pf(r[(K.yr >= 2021).values]))} | {fx} |')
        print(f'## {name} ({rule})\n')
        print('| stop | fired / yr | PF | sum | worst | CVaR 1 % | CVaR 5 % | PF halves | fired: stop exit / held |')
        print('|---|---|---|---|---|---|---|---|---|')
        line('GH-6 exit only', np.zeros(len(K), bool), np.full(len(K), np.nan))
        r5x = K[f'{tag}_r5_x'].values
        r5 = (K[v5] >= 300).values & np.isfinite(r5x) & (f5 < bsec)
        line('**r5: clk 300 at 5 min, both sides (the ruling)**', r5, r5x)
        for m in ('clk', 'pb', 'ew'):
            for kind in KINDS:
                for th in (300, 400, 600) if m != 'clk' else (300, 400):
                    t = K[f'{tag}_{m}_{kind}_{th}_t'].values; x = K[f'{tag}_{m}_{kind}_{th}_x'].values
                    fire = (t > 0) & np.isfinite(x) & (t < bsec)
                    line(f'{m} · {kind} · {th} bp any time', fire, x)
            # the ruling + this measure adverse (win / uw) at 400 any time
        for m in ('clk', 'pb', 'ew'):
            for kind in ('win', 'uw'):
                th = 400
                t = K[f'{tag}_{m}_{kind}_{th}_t'].values.astype(float); x = K[f'{tag}_{m}_{kind}_{th}_x'].values
                ok = (t > 0) & np.isfinite(x)
                T = np.where(r5, f5, np.where(ok, t, 1e9)); Xp = np.where(r5, r5x, x)
                first_ok = np.where(r5 & ok & (t < f5), True, False)
                T = np.where(first_ok, t, T); Xp = np.where(first_ok, x, Xp)
                fire = (T < bsec) & np.isfinite(Xp)
                line(f'r5 + {m} · {kind} · {th} bp', fire, Xp)
        print()
        W = K.assign(rr=res(r5, r5x)[0])
        print(f'{name} — the five worst trades under r5 alone: ' + '; '.join(f'{t.ticker} {t.d} {t.rr:+.0f} %' for t in W.nsmallest(5, 'rr').itertuples()) + '\n')
