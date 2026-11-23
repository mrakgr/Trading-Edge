"""GH-22 — the stop's REFERENCE and TRIGGER (user 2026-11-23: "whether using the 30m VWAP price as the reference instead of the
30m high might work better? Also, instead of using 1s breaks as the triggers, what if we used the 30m bar VWAP prices as the
triggers instead?").

Levels: H30 = the 09:30–10:00 high (1s vwap) · V30 = the 09:30–10:00 bar's VWAP. Triggers: BRK = the first 1s bar above the
level (a 1s break; the $5k fill from the next second) · LAST = at a half-hour mark, the last price before it above the level ·
BAR = at a mark, the VWAP of the 30-minute bar just completed above the level (both: the mark's $5k fill, path.parquet x<hhmm>).
Armed from 10:30 or from 11:00.
  Part 1 (as GH-21): every eligible trade, at each mark, LAST / BAR above the level → the forward short return to 15:30.
  Part 2: THE BOOK. The universe: the 10:00-eligible stock-days, z > −2 and the 09:30 stepped stop not fired before 10:00
  (GH-20). The 10:00 short, held to 15:30 (the user: "forget the VWAP rule"), with the 350 bp ew · any stop from 10:00; each
  level × trigger × arming adds a cover; the first exit wins. And GH-19's D (H30, 1s break from 10:30, only when the 10:00 bar
  did not breach H30) for comparison.
    cd research && python3 scripts/equity/gaphv_refs.py > logs/gaphv_refs.log 2>&1
    cd research && python3 scripts/equity/gaphv_refs.py --show > data/equity/gaphv/refs.md
    cd research && python3 scripts/equity/gaphv_refs.py --show --ew0930 > data/equity/gaphv/refs_ew0930.md   (the stop's EWMA from 09:30)
"""
import duckdb, numpy as np, pandas as pd, os, sys, io, contextlib, time
from multiprocessing import Pool
sys.path.insert(0, 'scripts/equity')
from gaphv_volstop3 import fill, BARS, O, CLOSE

T10 = 36000
ARMS = {'1030': 37800, '1100': 39600}
OUT = 'data/equity/gaphv/refs.parquet'

def one(sec, px, vol, close):
    r = {}
    w1 = (sec >= O) & (sec < T10)
    if not w1.any(): return r
    lv = {'H30': px[w1].max(), 'V30': (px[w1] * vol[w1]).sum() / vol[w1].sum()}
    r['v30'] = lv['V30']
    for L, level in lv.items():
        for a, asec in ARMS.items():
            hit = np.where((sec >= asec) & (sec < 55800) & (px > level))[0]
            k = f'brk_{L}_{a}'
            if len(hit):
                t = int(sec[hit[0]]); r[k + '_t'] = t; r[k + '_x'] = fill(sec, px, vol, t + 1, close)
            else:
                r[k + '_t'] = -1; r[k + '_x'] = np.nan
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
        from gaphv_exits import X, pf, f2, CHK
        from gaphv_volstop import hm2s
    X = (X.merge(pd.read_parquet(OUT), on=['ticker', 'd'])
          .merge(pd.read_parquet('data/equity/gaphv/stops.parquet').drop(columns=['close'], errors='ignore'), on=['ticker', 'd'], how='left')
          .merge(pd.read_parquet('data/equity/gaphv/entry10.parquet')[['ticker', 'd', 'z']], on=['ticker', 'd'], how='left')
          .merge(pd.read_parquet('data/equity/gaphv/volstop6.parquet')[['ticker', 'd', 'pinf_a700_b500_t', 'pinf_a700_b500_x']], on=['ticker', 'd'], how='left')
          .merge(pd.read_parquet('data/equity/gaphv/volstop3.parquet')[['ticker', 'd', 'e_w3_350_t', 'e_w3_350_x']], on=['ticker', 'd'], how='left'))
    X['H30'], X['V30'] = X.h30, X.v30
    EW0930 = '--ew0930' in sys.argv
    def lab(m): return f'{m[:2]}:{m[2:]}'
    print('# GH-22 — the stop\'s reference (the 30m high vs the 30m VWAP) and trigger (1s break vs the mark\'s last price vs the 30m bar\'s VWAP)\n')
    print('H30 = the 09:30–10:00 high; V30 = the 09:30–10:00 bar\'s VWAP. LAST = the last price before the mark; BAR = the VWAP of '
          'the 30-minute bar ending at the mark; BRK = a 1s break. The eligible 10:00 universe (z > −2, the 09:30 stepped stop not '
          'fired). Σ = Σ % (the short, gross).\n')
    for name, flag in (('U1000', 'u1000'), ('D1000', 'd1000')):
        A = X[X[flag] & ~(X.z < -2.0) & ~((X.pinf_a700_b500_t > 0) & (X.pinf_a700_b500_t < 36000))
              & np.isfinite(X.x1000) & (X.x1000 > 0)].copy().reset_index(drop=True)
        h1 = (A.yr <= 2020).values
        print(f'## {name}, n = {len(A):,}\n')
        print('**Part 1 — above the level at the mark → the short from the mark to 15:30**: share above · mean · PF (halves\' means)\n')
        print('| mark | H30 · LAST | H30 · BAR | V30 · LAST | V30 · BAR |')
        print('|---|---|---|---|---|')
        for m in CHK:
            x0 = A[f'x{m}'].values; x1 = A.x1530.values; ok = np.isfinite(x0) & np.isfinite(x1) & (x0 > 0)
            r = 100 * (1 - x1 / x0); cells = []
            for L in ('H30', 'V30'):
                for trig, val in (('LAST', A[f'p_{m}'].values), ('BAR', A['bv' + m].values)):
                    ab = ok & np.isfinite(val) & (val > A[L].values)
                    cells.append(f'{ab.sum() / ok.sum():.0%} · {r[ab].mean():+.2f} % · {f2(pf(r[ab]))} ({r[ab & h1].mean():+.2f} / {r[ab & ~h1].mean():+.2f})')
            print(f'| {lab(m)} | ' + ' | '.join(cells) + ' |')
        print()
        # Part 2: the book
        # the volatility stop: by default restarted at the 10:00 entry (W = 3, 350 bp); with --ew0930 the ONE EWMA from 09:30 (the
        # stepped schedule, 350 bp after 09:45; user 2026-11-23: "used the same EWMA of volatility from 9:30 throughout"). The
        # universe already excludes a 09:30 stop fired before 10:00, so its first firing is the cover after the entry.
        if EW0930:
            t = A.pinf_a700_b500_t.values.astype(float); x = A.pinf_a700_b500_x.values
        else:
            t = A.e_w3_350_t.values.astype(float); x = A.e_w3_350_x.values
        vs = (t > 0) & np.isfinite(x) & (t < 55800)
        ex_t = np.where(vs, t, 55800.0); ex_x = np.where(vs, x, A.x1530.values)
        ok0 = np.isfinite(ex_x)
        ex_x = np.where(ok0, ex_x, A.close.values)
        r0 = 100 * (1 - ex_x / A.x1000.values); k1 = max(1, len(A) // 100)
        def line(lab_, r, fired=None, bold=False):
            s = np.sort(r); b = '**' if bold else ''
            cov = f'{fired.mean():.0%} · {r[fired].mean():+.2f} / {r0[fired].mean():+.2f} %' if fired is not None and fired.any() else ''
            return (f'| {b}{lab_}{b} | {r.sum():,.0f} ({100 * (r.sum() / r0.sum() - 1):+.1f} %) | {f2(pf(r))} ({f2(pf(r[h1]))} / {f2(pf(r[~h1]))}) | '
                    f'{s[0]:+.0f} % | {s[:k1].mean():+.1f} % | {cov} |')
        print('**Part 2 — the book** (the 10:00 short to 15:30 + the volatility stop — ' + ('the ONE EWMA from 09:30' if EW0930 else 'restarted at 10:00') + '; each row adds one cover)\n')
        print('| book | Σ | PF (halves) | worst | CVaR 1 % | fired · covered: now / baseline |')
        print('|---|---|---|---|---|---|')
        print(line('baseline (no high / VWAP stop)', r0, bold=True))
        for L in ('H30', 'V30'):
            for a, asec in ARMS.items():
                # BRK
                bt = A[f'brk_{L}_{a}_t'].values.astype(float); bx = A[f'brk_{L}_{a}_x'].values
                fired = (bt > 0) & np.isfinite(bx) & (bt < ex_t)
                print(line(f'{L} · BRK · from {lab(a)}', np.where(fired, 100 * (1 - bx / A.x1000.values), r0), fired))
                for trig in ('LAST', 'BAR'):
                    mt = np.full(len(A), -1.0); mx = np.full(len(A), np.nan)
                    for m in CHK:
                        ms = hm2s(m)
                        if ms < asec: continue
                        val = A[f'p_{m}'].values if trig == 'LAST' else A['bv' + m].values
                        go = (mt < 0) & np.isfinite(val) & (val > A[L].values) & np.isfinite(A[f'x{m}'].values)
                        mt[go] = ms; mx[go] = A[f'x{m}'].values[go]
                    fired = (mt > 0) & (mt < ex_t)
                    print(line(f'{L} · {trig} · from {lab(a)}', np.where(fired, 100 * (1 - mx / A.x1000.values), r0), fired))
        breached = (A.h1000 > A.h30).values
        dt = np.where(breached, -1.0, A.B_t.values.astype(float)); dx = A.B_x.values
        fired = (dt > 0) & np.isfinite(dx) & (dt < ex_t)
        print(line('GH-19 D (H30 · BRK from 10:30, not breached 10:00–10:30)', np.where(fired, 100 * (1 - dx / A.x1000.values), r0), fired))
        print()
