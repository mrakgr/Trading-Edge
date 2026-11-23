"""GH-19 — STOP METHODS on the four gap-short variants, on the new baseline (user 2026-11-23: "we should test out different
stop methods. As the baseline rule, all the variants we test will have the off · 700 · 500 before 9:45 ET and 350 bp volatility
stop active after that. We'll also use the z score rule with the -2 threshold at 10 ET ... Exiting past 10 ET or 10:30 ET if the
stock is higher than the 30m high or the 60m high. The 10:30 ET variant that exits if the 30m high hasn't been breached between
10 and 10:30 ET ... is a genuine long momentum setup. We should keep track of the 9:30 ET bar's and 10 ET bar's highs").

BASELINE (every method rides on it): the variant's GH-6 rule (U0930 LOSER · D0930 T1530 · U1000 VWAP · D1000 T1530) + the
ew · any volatility stop (W = 3): 09:30 entries by the stepped schedule (off before 09:35 · 700 · 500 · 350 from 09:45, GH-13g),
10:00 entries 350 bp from the entry; the 10:00 variants enter only when z > −2 (GH-16). The first exit wins.
THE METHODS (an extra exit; the highs are 1s-vwap highs — the slim bars carry no high): H30 = the 09:30–10:00 high, H1000 =
the 10:00–10:30 high; a cover at the first 1s bar trading above the level, filled at the $5k VWAP from the next second.
  A  above H30, from 10:00
  B  above H30, from 10:30
  C  above the 60-minute high max(H30, H1000), from 10:30
  D  above H30 from 10:30, ONLY when the 10:00 bar did not breach it (H1000 <= H30)  — then the 60m high IS H30: D = C on
     the not-breached days
  E  at each half-hour mark 10:30 .. 15:00: out when the bar just completed has a HIGHER vwap than the bar before it (the
     user's 11-23 idea); the fill = the mark's $5k fill (path.parquet x<hhmm>)
  F  out at the 10:30 mark when the 10:00 bar BREACHED H30 (H1000 > H30; knowable at 10:30), the mark's $5k fill
  D+F  both: breached → out at 10:30; not breached → D's stop above H30
    cd research && python3 scripts/equity/gaphv_stops.py > logs/gaphv_stops.log 2>&1
    cd research && python3 scripts/equity/gaphv_stops.py --show > data/equity/gaphv/stops.md
"""
import duckdb, numpy as np, pandas as pd, os, sys, io, contextlib, time
from multiprocessing import Pool
sys.path.insert(0, 'scripts/equity')
from gaphv_volstop3 import fill, BARS, O, CLOSE

T10, T1030 = 36000, 37800
OUT = 'data/equity/gaphv/stops.parquet'
BARMARKS = [f'{h:02d}{m:02d}' for h in range(10, 16) for m in (0, 30)][:12]     # the bar ENDS 10:00 .. 15:30

def one(sec, px, vol, close):
    r = {}
    w1 = (sec >= O) & (sec < T10); w2 = (sec >= T10) & (sec < T1030)
    h30 = px[w1].max() if w1.any() else np.nan
    h1000 = px[w2].max() if w2.any() else np.nan
    r['h30'], r['h1000'] = h30, h1000
    h60 = np.nanmax([h30, h1000]) if np.isfinite(h30) or np.isfinite(h1000) else np.nan
    for m, frm, lvl in (('A', T10, h30), ('B', T1030, h30), ('C', T1030, h60)):
        hit = np.where((sec >= frm) & (sec < 55800) & (px > lvl))[0] if np.isfinite(lvl) else []
        if len(hit):
            t = int(sec[hit[0]]); r[m + '_t'] = t; r[m + '_x'] = fill(sec, px, vol, t + 1, close)
        else:
            r[m + '_t'] = -1; r[m + '_x'] = np.nan
    # the half-hour bars' vwaps: the bar ending at hhmm
    for e in BARMARKS:
        es = int(e[:2]) * 3600 + int(e[2:]) * 60
        m = (sec >= es - 1800) & (sec < es)
        r['bv' + e] = (px[m] * vol[m]).sum() / vol[m].sum() if m.any() else np.nan
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
        E = pd.read_parquet('data/equity/gaphv/events.parquet', columns=['ticker', 'd', 'close'])
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
        from gaphv_exits import X, simulate, pf, f2, CHK
        from gaphv_volstop import hm2s
    X = (X.merge(pd.read_parquet(OUT), on=['ticker', 'd'], how='left')
          .merge(pd.read_parquet('data/equity/gaphv/volstop6.parquet')[['ticker', 'd', 'pinf_a700_b500_t', 'pinf_a700_b500_x']], on=['ticker', 'd'], how='left')
          .merge(pd.read_parquet('data/equity/gaphv/volstop3.parquet')[['ticker', 'd', 'e_w3_350_t', 'e_w3_350_x']], on=['ticker', 'd'], how='left')
          .merge(pd.read_parquet('data/equity/gaphv/entry10.parquet')[['ticker', 'd', 'z', 'halted10']], on=['ticker', 'd'], how='left'))
    V = [('U0930', 'u0930', 'open', 'LOSER', 34200, 'pinf_a700_b500'), ('D0930', 'd0930', 'open', 'T1530', 34200, 'pinf_a700_b500'),
         ('U1000', 'u1000', 'x1000', 'VWAP', 36000, 'e_w3_350'), ('D1000', 'd1000', 'x1000', 'T1530', 36000, 'e_w3_350')]
    print('# GH-19 — stop methods on the new baseline\n')
    print('BASELINE = the GH-6 rule + the volatility stop (09:30 entries: off · 700 · 500 · 350 by the clock; 10:00 entries: 350 '
          'from the entry) + (10:00 variants) only z > −2. Each method adds one exit; the first exit wins; the $5k fill. '
          'H30 = the 09:30–10:00 high, H1000 = the 10:00–10:30 high (1s vwap). A: above H30 from 10:00 · B: above H30 from 10:30 · '
          'C: above max(H30, H1000) from 10:30 · D: as B, only when H1000 <= H30 (not breached) · E: at a mark 10:30–15:00 the '
          'bar just completed has a higher vwap than the one before. Σ = Σ % (the short, gross) · PF (halves 2016–2020 / '
          '2021–2026) · worst · CVaR 1 % · the method fired on · its covered trades: now / on the baseline.\n')
    summary = []
    for name, flag, ent, rule, e0, vs in V:
        K = X[X[flag] & np.isfinite(X[ent]) & (X[ent] > 0)].copy()
        if e0 == 36000:
            n0 = len(K); K = K[~(K.z < -2.0)]; zcut = n0 - len(K)
        K = K.reset_index(drop=True); K['entry'] = K[ent]
        px, at = simulate(K, rule)
        bsec = np.array([57600 if a == 'close' else hm2s(a) for a in at]).astype(float)
        t = K[vs + '_t'].values.astype(float); x = K[vs + '_x'].values
        v = (t > 0) & np.isfinite(x) & (t < bsec)
        ex_t = np.where(v, t, bsec); ex_x = np.where(v, x, px)
        r0 = 100 * (1 - ex_x / K.entry.values)
        h1 = (K.yr <= 2020).values; k1 = max(1, len(K) // 100)
        breached = (K.h1000 > K.h30).values
        # E: the first mark after the entry where the completed bar's vwap > the previous bar's
        Et = np.full(len(K), -1.0); Ex = np.full(len(K), np.nan)
        for mk in CHK:
            ms = hm2s(mk); prev = f'{(ms - 1800) // 3600:02d}{(ms - 1800) % 3600 // 60:02d}'
            up = (K['bv' + mk] > K['bv' + prev]).fillna(False).values & (ms > e0) & (Et < 0)
            xm = K[f'x{mk}'].values; go = up & np.isfinite(xm)
            Et[go] = ms; Ex[go] = xm[go]
        meth = {'A': (K.A_t.values.astype(float), K.A_x.values), 'B': (K.B_t.values.astype(float), K.B_x.values),
                'C': (K.C_t.values.astype(float), K.C_x.values),
                'D': (np.where(breached, -1.0, K.B_t.values.astype(float)), K.B_x.values), 'E': (Et, Ex),
                'F': (np.where(breached, 37800.0, -1.0), K.x1030.values),
                'D+F': (np.where(breached, 37800.0, K.B_t.values.astype(float)), np.where(breached, K.x1030.values, K.B_x.values))}
        def line(lab, r, fired=None, bold=False):
            s = np.sort(r); b = '**' if bold else ''
            cov = f'{fired.mean():.0%} · {r[fired].mean():+.2f} / {r0[fired].mean():+.2f} %' if fired is not None and fired.any() else ''
            return (f'| {b}{lab}{b} | {r.sum():,.0f} ({100 * (r.sum() / r0.sum() - 1):+.1f} %) | {f2(pf(r))} ({f2(pf(r[h1]))} / {f2(pf(r[~h1]))}) | '
                    f'{r.mean():+.2f} % | {s[0]:+.0f} % | {s[:k1].mean():+.1f} % | {cov} |')
        hdr = f'## {name} ({rule}), n = {len(K):,}' + (f' (z < −2 skipped: {zcut})' if e0 == 36000 else '') + '\n'
        print(hdr)
        print('| book | Σ | PF (halves) | mean | worst | CVaR 1 % | fired · covered: now / baseline |')
        print('|---|---|---|---|---|---|---|')
        print(line('baseline', r0, bold=True))
        res = {}
        for m, (mt, mx) in meth.items():
            fired = (mt > 0) & np.isfinite(mx) & (mt >= e0) & (mt < ex_t)
            r = np.where(fired, 100 * (1 - mx / K.entry.values), r0); res[m] = r
            print(line(f'+ {m}', r, fired))
        if e0 == 36000:
            keep = ~K.halted10.astype('boolean').fillna(False).to_numpy(bool); rk = r0[keep]; sk = np.sort(rk)
            print(f'| baseline, skipping "halted at 10:00" (the 60 s proxy; n {keep.sum():,}) | {rk.sum():,.0f} ({100 * (rk.sum() / r0.sum() - 1):+.1f} %) | '
                  f'{f2(pf(rk))} ({f2(pf(r0[keep & h1]))} / {f2(pf(r0[keep & ~h1]))}) | {rk.mean():+.2f} % | {sk[0]:+.0f} % | {sk[:max(1, len(sk) // 100)].mean():+.1f} % | |')
        print()
        print(f'The 10:00 bar vs the 09:30 bar: breached (H1000 > H30) {breached.mean():.0%} of the stock-days — baseline mean '
              f'{r0[breached].mean():+.2f} % (PF {f2(pf(r0[breached]))}) · not breached {r0[~breached].mean():+.2f} % (PF {f2(pf(r0[~breached]))}). '
              f'D on the not-breached days: fired on {((meth["D"][0] > 0) & ~breached).sum() / max(1, (~breached).sum()):.0%} of them.\n')
        summary.append((name, r0.sum(), {m: res[m].sum() for m in res}, r0.min(), {m: res[m].min() for m in res}))
    print('## Summary — Σ (worst) per variant\n')
    print('| variant | baseline | + A | + B | + C | + D | + E | + F | + D+F |')
    print('|---|---|---|---|---|---|---|---|---|')
    MS = ['A', 'B', 'C', 'D', 'E', 'F', 'D+F']
    tot = [0.0] * (len(MS) + 1)
    for name, b, rs, bw, ws in summary:
        print(f'| {name} | {b:,.0f} ({bw:+.0f} %) | ' + ' | '.join(f'{rs[m]:,.0f} ({ws[m]:+.0f} %)' for m in MS) + ' |')
        tot[0] += b
        for i, m in enumerate(MS): tot[i + 1] += rs[m]
    print(f'| **all four** | {tot[0]:,.0f} | ' + ' | '.join(f'{tt:,.0f} ({100 * (tt / tot[0] - 1):+.1f} %)' for tt in tot[1:]) + ' |')
