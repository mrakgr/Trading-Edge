"""GH-12 — the volatility DISASTER STOP at ANY point of the trade (user 2026-11-21: "Besides just the open, what if we exit
at any point in the stock's lifecycle if the 5m volatility reaches this point?"). GH-10's rule (out when the 30 s slot volat
over 5 minutes >= 300 bp) checked every 30 s on the TRAILING 5 minutes (10 clock slots), from the first full 5 minutes
after the entry (09:35 / 10:05) to 15:30.
  slot vwaps: 30 s clock slots from 09:30; the volat of a window = mean |ln(v_j / v_i)| over successive traded slots i < j
  both inside it (GapShort.fs's measure). Decision at the window's end; the fill = the $5k VWAP from that second.
  'any' fires on the volat alone; 'adv' only when the window moved AGAINST the short (its last traded slot vwap > its first).
Pass 1 (the 1s bars) → data/equity/gaphv/volstop.parquet: per event, per entry (o = 09:30, e = 10:00), per threshold
(200 / 300 / 400 / 500) and kind, the first firing second and its fill. Pass 2 (--report): on top of each variant's GH-6
exit (whichever exit comes first), vs GH-6 + GH-10's 5-minute-only stop. The short, gross.
    cd research && python3 scripts/equity/gaphv_volstop.py > logs/gaphv_volstop.log 2>&1
    cd research && python3 scripts/equity/gaphv_volstop.py --report > data/equity/gaphv/volstop.md
"""
import duckdb, numpy as np, pandas as pd, os, sys, io, contextlib, time
from multiprocessing import Pool

BARS = 'data/intraday_1s_slim'
O, CLOSE, LAST = 34200, 57600, 55800          # the stop is checked to 15:30 (the final exit)
NS = (CLOSE - O) // 30
TH = (200, 300, 400, 500)

def fill(sec, px, vol, t):
    a, b = np.searchsorted(sec, t), np.searchsorted(sec, t + 1800)
    if a >= b: return np.nan
    dv = np.cumsum(px[a:b] * vol[a:b]); k = min(np.searchsorted(dv, 5000.0), b - a - 1)
    return dv[k] / vol[a:a + k + 1].sum()

def windows(sec, px, vol):
    """per window start s (10 slots [s, s+10)): volat bp and the move sign (last traded slot vwap vs first)"""
    sl = (sec - O) // 30
    pv = np.bincount(sl, px * vol, NS); v = np.bincount(sl, vol, NS)
    tr = np.where(v > 0)[0]; sv = pv[tr] / v[tr]
    W = NS - 9
    sm, cnt = np.zeros(W + 1), np.zeros(W + 1)
    if len(tr) >= 2:
        i, j = tr[:-1], tr[1:]; a = np.abs(np.diff(np.log(sv)))
        lo, hi = np.maximum(j - 9, 0), np.minimum(i, W - 1)       # the window starts s with i >= s and j <= s + 9
        ok = lo <= hi
        np.add.at(sm, lo[ok], a[ok]); np.add.at(sm, hi[ok] + 1, -a[ok])
        np.add.at(cnt, lo[ok], 1); np.add.at(cnt, hi[ok] + 1, -1)
    sm, cnt = np.cumsum(sm)[:W], np.cumsum(cnt)[:W]
    vol_bp = np.where(cnt > 0, 1e4 * sm / np.where(cnt > 0, cnt, 1), 0.0)
    # the window's first / last traded slot vwap (for the direction)
    full = np.full(NS, np.nan); full[tr] = sv
    idx = np.where(np.isfinite(full), np.arange(NS), -1); last_idx = np.maximum.accumulate(idx)           # last traded <= k
    idx2 = np.where(np.isfinite(full), np.arange(NS), NS); nxt = np.minimum.accumulate(idx2[::-1])[::-1]   # first traded >= k
    s = np.arange(W); fi, la = nxt[s], last_idx[s + 9]
    good = (fi < NS) & (la >= 0) & (fi <= s + 9) & (la >= s)
    up = np.zeros(W, bool); up[good] = full[la[good]] > full[fi[good]]
    return vol_bp, up

def one(sec, px, vol):
    vb, up = windows(sec, px, vol)
    r = {}
    for tag, s0 in (('o', 0), ('e', 60)):
        for th in TH:
            for kind in ('any', 'adv'):
                hit = vb >= th
                if kind == 'adv': hit &= up
                s = np.arange(len(vb)); hit &= (s >= s0) & (O + (s + 10) * 30 < LAST)
                k = np.argmax(hit) if hit.any() else -1
                t = O + (k + 10) * 30 if k >= 0 else -1
                r[f'{tag}{th}{kind}_t'] = t
                r[f'{tag}{th}{kind}_x'] = fill(sec, px, vol, t) if t > 0 else np.nan
    return r

def day(args):
    d, tk = args
    f = f'{BARS}/{d}.parquet'
    if not os.path.exists(f): return []
    con = duckdb.connect(); con.execute("SET memory_limit='1GB'"); con.execute("SET threads=2")
    B = con.execute(f"""SELECT ticker, bucket, vwap, volume FROM '{f}' WHERE ticker IN (SELECT unnest(?))
                        AND bucket >= {O} AND bucket < {CLOSE} AND volume > 0 AND vwap > 0 ORDER BY ticker, bucket""", [tk]).df()
    out = []
    for t, g in B.groupby('ticker'):
        if len(g) < 10: continue
        r = one(g.bucket.values, g.vwap.values.astype(float), g.volume.values.astype(float)); r['ticker'], r['d'] = t, d
        out.append(r)
    return out

def hm2s(m): return int(m[:2]) * 3600 + int(m[2:]) * 60

if '--report' not in sys.argv:
    if __name__ == '__main__':
        t0 = time.time()
        E = pd.read_parquet('data/equity/gaphv/events.parquet', columns=['ticker', 'd'])
        jobs = [(str(pd.Timestamp(d).date()), list(g.ticker)) for d, g in E.groupby('d')]
        res = []
        with Pool(8) as p:
            for i, r in enumerate(p.imap_unordered(day, jobs, chunksize=4)):
                res.extend(r)
                if i % 500 == 0: print(f'{i}/{len(jobs)} {time.time() - t0:.0f}s', flush=True)
        R = pd.DataFrame(res); R['d'] = pd.to_datetime(R.d).dt.date
        R.to_parquet('data/equity/gaphv/volstop.parquet', index=False); print(f'wrote {len(R):,}, {time.time() - t0:.0f}s')
else:
    sys.path.insert(0, 'scripts/equity')
    with contextlib.redirect_stdout(io.StringIO()):
        from gaphv_exits import X, simulate, pf, f2
    X = X.merge(pd.read_parquet('data/equity/gaphv/volstop.parquet'), on=['ticker', 'd'])
    V = [('U0930', 'u0930', 'open', 'LOSER', 'o'), ('D0930', 'd0930', 'open', 'T1530', 'o'),
         ('U1000', 'u1000', 'x1000', 'VWAP', 'e'), ('D1000', 'd1000', 'x1000', 'T1530', 'e')]
    print('# GH-12 — the volatility disaster stop at ANY point of the trade\n')
    print('The trailing 5-minute volat (30 s slots) checked every 30 s from 5 minutes after the entry to 15:30; on top of each '
          'variant\'s GH-6 exit (the first exit wins). "at 5 min only" = GH-10\'s rule (the window ending 5 min after the entry). '
          'any = the volat alone; adverse = only when the window moved against the short. The short, gross; sum = Σ %.\n')
    for name, flag, ent, rule, tag in V:
        K = X[X[flag] & np.isfinite(X[ent]) & (X[ent] > 0)].copy().reset_index(drop=True); K['entry'] = K[ent]
        px, at = simulate(K, rule)
        base = 1 - px / K.entry.values
        bsec = np.array([57600 if a == 'close' else hm2s(a) for a in at])
        first5 = (34500 if tag == 'o' else 36300)
        print(f'## {name} ({rule})\n')
        print('| stop | fired / yr | PF | mean | sum | worst | CVaR 1 % | CVaR 5 % | PF 2016–20 / 2021–26 | fired: stop exit / GH-6 result (mean) | fired after the first 5 min |')
        print('|---|---|---|---|---|---|---|---|---|---|---|')
        rows = [('GH-6 exit', None, None, False)]
        for th in TH:
            rows += [(f'≥ {th} bp at 5 min only', th, 'any', True), (f'≥ {th} bp any time · any', th, 'any', False),
                     (f'≥ {th} bp any time · adverse', th, 'adv', False)]
        for lab, th, kind, only5 in rows:
            if th is None: r = base; fire = np.zeros(len(K), bool)
            else:
                t = K[f'{tag}{th}{kind}_t'].values; x = K[f'{tag}{th}{kind}_x'].values
                fire = (t > 0) & np.isfinite(x) & (t < bsec)
                if only5: fire &= t == first5
                r = np.where(fire, 1 - x / K.entry.values, base)
            r = 100 * r; s = np.sort(r); k1, k5 = max(1, len(s) // 100), max(1, len(s) // 20)
            fx = f'{100 * (1 - K[f"{tag}{th}{kind}_x"].values[fire] / K.entry.values[fire]).mean():+.1f} / {100 * base[fire].mean():+.1f} %' if fire.any() else ''
            late = f'{(fire & (K[f"{tag}{th}{kind}_t"].values > first5)).sum() / 10.1:.0f}/yr' if th else ''
            print(f'| {lab} | {fire.sum() / 10.1:.0f} | {f2(pf(r))} | {r.mean():+.2f} % | {r.sum():,.0f} | {s[0]:+.0f} % | {s[:k1].mean():+.1f} % | '
                  f'{s[:k5].mean():+.1f} % | {f2(pf(r[(K.yr <= 2020).values]))} / {f2(pf(r[(K.yr >= 2021).values]))} | {fx} | {late} |')
        print()
