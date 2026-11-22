"""GH-13d — the EWMA disaster stop's WARM-UP (user 2026-11-22, after PGHL: the 350 bp stop's EWMA was 742 / 784 bp at the
slots ending 09:31:00 / 09:31:30, but it arms only at the 4th 30-present-bar slot (>= 3 returns) — the 120th traded second
— and PGHL halted at 09:32:44 after 119; armed at the 09:42:02 reopen). "Arming after 3 slots would have fired at
09:31:30": what does a shorter warm-up cost the four variants?

The stop: GH-13c's ew · any (both sides) — FlushFader's slot clock (30 PRESENT 1s bars per slot), the bias-corrected EWMA
of |ln(vwap_k / vwap_k-1)| at a half-life of 10 slots, evaluated at each slot's completing bar, fired at the first value
>= the threshold, from the warm-up on: W = the returns the EWMA needs, W + 1 slots filled (3 = GH-13c, the slot 4; 2 =
slot 3; 1 = slot 2; the run also takes 4, 5, 7, 10, 15, 20 — user 2026-11-22: "What if we went the other way and required
at least 5 slots to be filled? What if we went even higher?").
The fill: GH-13's $5k VWAP from the second after the decision, no time cap (the close fills a shortfall). On top of each
variant's GH-6 exit (the first exit wins). W = 3 must reproduce GH-13c (the control).
    cd research && python3 scripts/equity/gaphv_volstop3.py > logs/gaphv_volstop3.log 2>&1
    cd research && python3 scripts/equity/gaphv_volstop3.py --report > data/equity/gaphv/volstop3.md
"""
import duckdb, numpy as np, pandas as pd, os, sys, io, contextlib, time
from multiprocessing import Pool

BARS = 'data/intraday_1s_slim'
O, CLOSE, LAST = 34200, 57600, 55800
TH = (300, 350, 400)
WARM = (1, 2, 3, 4, 5, 7, 10, 15, 20)
HL = 10.0
OUT = 'data/equity/gaphv/volstop3.parquet'

def fill(sec, px, vol, t, close):
    """the $5k VWAP from second t on; a shortfall at the close fills at the official close (GH-13)"""
    a = np.searchsorted(sec, t)
    if a >= len(sec): return close
    dv = np.cumsum(px[a:] * vol[a:]); k = np.searchsorted(dv, 5000.0)
    if k < len(dv): return dv[k] / vol[a:a + k + 1].sum()
    sh = 5000.0 - dv[-1]
    return 5000.0 / (vol[a:].sum() + sh / close)

def ew_series(sec, px, vol, e0):
    """per completed 30-present-bar slot after e0: the decision second, the EWMA (bp), the returns it holds"""
    a = np.searchsorted(sec, e0); n = (len(sec) - a) // 30
    if n < 2: return np.array([]), np.array([]), np.array([], int)
    p, v = px[a:a + 30 * n].reshape(n, 30), vol[a:a + 30 * n].reshape(n, 30)
    sv = (p * v).sum(1) / v.sum(1)
    tdec = sec[a + 30 * np.arange(n) + 29]
    ar = np.abs(np.diff(np.log(sv)))
    al = 1 - 0.5 ** (1 / HL); num = den = 0.0; ew = np.empty(n - 1)
    for i, x in enumerate(ar):
        num = (1 - al) * num + al * x; den = (1 - al) * den + al; ew[i] = 1e4 * num / den
    k = np.arange(1, n)                                   # slot k (0-based) holds returns 0..k-1: k returns
    return tdec[k], ew, k

def one(sec, px, vol, close, entries):
    r = {}
    for tag, e0, _ in entries:
        t, ew, k = ew_series(sec, px, vol, e0)
        for w in WARM:
            for th in TH:
                hit = (ew >= th) & (k >= w) & (t < LAST) if len(t) else np.array([], bool)
                if len(hit) and hit.any():
                    i = np.argmax(hit); tt = int(t[i]) + 1
                    r[f'{tag}_w{w}_{th}_t'] = tt; r[f'{tag}_w{w}_{th}_x'] = fill(sec, px, vol, tt, close)
                else:
                    r[f'{tag}_w{w}_{th}_t'] = -1; r[f'{tag}_w{w}_{th}_x'] = np.nan
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

def hms(s): s = int(s); return f'{s // 3600:02d}:{s % 3600 // 60:02d}:{s % 60:02d}'

if '--report' not in sys.argv:
    if __name__ == '__main__':
        t0 = time.time()
        E = pd.read_parquet('data/equity/gaphv/events.parquet', columns=['ticker', 'd', 'open', 'close']).merge(
            pd.read_parquet('data/equity/gaphv/path.parquet', columns=['ticker', 'd', 'x1000']), on=['ticker', 'd'])
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
    X = X.merge(pd.read_parquet(OUT), on=['ticker', 'd'])
    V = [('U0930', 'u0930', 'open', 'LOSER', 'o', 34200), ('D0930', 'd0930', 'open', 'T1530', 'o', 34200),
         ('U1000', 'u1000', 'x1000', 'VWAP', 'e', 36000), ('D1000', 'd1000', 'x1000', 'T1530', 'e', 36000)]
    print('# GH-13d — the EWMA stop\'s warm-up (ew · any, both sides)\n')
    print('W = the returns the EWMA needs before it may fire — W + 1 slots filled (3 = GH-13c: the 4th 30-present-bar slot). '
          'On top of each variant\'s GH-6 exit; the $5k fill, no time cap. sum = Σ % (the short, gross) · worst · CVaR 1 % · PF · '
          'fired / yr · early = fired within 5 min of the entry, / yr · their mean result (stop exit / held to the GH-6 exit).\n')
    tot = {}
    for name, flag, ent, rule, tag, e0 in V:
        K = X[X[flag] & np.isfinite(X[ent]) & (X[ent] > 0)].copy().reset_index(drop=True); K['entry'] = K[ent]
        px, at = simulate(K, rule); base = 1 - px / K.entry.values
        bsec = np.array([57600 if a == 'close' else hm2s(a) for a in at])
        print(f'## {name} ({rule}), n = {len(K):,}\n')
        print('| W (slots) | bp | sum | worst | CVaR 1 % | PF | PF halves | fired / yr | early / yr | early: stop / held |')
        print('|---|---|---|---|---|---|---|---|---|---|')
        r0 = 100 * base; s0 = np.sort(r0); k1 = max(1, len(s0) // 100)
        print(f'| — | none | {r0.sum():,.0f} | {s0[0]:+.0f} % | {s0[:k1].mean():+.1f} % | {f2(pf(r0))} | | | | |')
        for w in WARM:
            for th in TH:
                if th != 350 and '--all' not in sys.argv:
                    t = K[f'{tag}_w{w}_{th}_t'].values; x = K[f'{tag}_w{w}_{th}_x'].values
                    fire = (t > 0) & np.isfinite(x) & (t < bsec)
                    r = 100 * np.where(fire, 1 - x / K.entry.values, base); s = np.sort(r)
                    tot.setdefault((w, th), []).append((r.sum(), s[0])); continue
                t = K[f'{tag}_w{w}_{th}_t'].values; x = K[f'{tag}_w{w}_{th}_x'].values
                fire = (t > 0) & np.isfinite(x) & (t < bsec)
                r = 100 * np.where(fire, 1 - x / K.entry.values, base); s = np.sort(r)
                early = fire & (t < e0 + 300)
                ex = (f'{100 * (1 - x[early] / K.entry.values[early]).mean():+.1f} / {100 * base[early].mean():+.1f} %') if early.any() else ''
                tot.setdefault((w, th), []).append((r.sum(), s[0]))
                b = '**' if (w, th) in ((3, 350), (2, 350)) else ''
                print(f'| {b}{w} ({w + 1}){b} | {b}{th}{b} | {r.sum():,.0f} | {s[0]:+.0f} % | {s[:k1].mean():+.1f} % | {f2(pf(r))} | '
                      f'{f2(pf(r[(K.yr <= 2020).values]))} / {f2(pf(r[(K.yr >= 2021).values]))} | {fire.sum() / 10.1:.0f} | {early.sum() / 10.1:.1f} | {ex} |')
        for tk, dd in (('PGHL', '2024-11-26'), ('ZJYL', '2023-12-18')):
            z = K[(K.ticker == tk) & (K.d.astype(str) == dd)]
            if len(z):
                cells = []
                for w in WARM:
                    if w not in (2, 3, 4, 5, 10, 20): continue
                    t = z[f'{tag}_w{w}_350_t'].iloc[0]; x = z[f'{tag}_w{w}_350_x'].iloc[0]
                    cells.append(f'W{w}: ' + (f'{hms(t)} @ \\${x:.2f} ({100 * (1 - x / z.entry.iloc[0]):+.0f} %)' if t > 0 else 'never'))
                print(f'\n{tk} {dd} at 350 bp — ' + ' · '.join(cells))
        print()
    print('## All four (sum · the worst of the four worst)\n')
    print('| W (slots) | 300 bp: sum · worst | 350 bp | 400 bp |')
    print('|---|---|---|---|')
    for w in WARM:
        cells = [f"{sum(a for a, _ in tot[(w, th)]):,.0f} · {min(b for _, b in tot[(w, th)]):+.0f} %" for th in TH]
        print(f'| {w} ({w + 1}) | ' + ' | '.join(cells) + ' |')
