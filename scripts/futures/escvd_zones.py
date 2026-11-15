#!/usr/bin/env python
"""ES CVD study, third pass: the TRAPPED-SELLER ZONE from the per-price footprint (docs/es_cvd_plan.md).

What footprint traders describe: aggressive selling prints at the bottom of a range (89 sold against 0 bought at a price);
mark the price ZONE of that selling, and buy when price leaves the zone upward. Mechanically, long side (the short
side is the mirror: buyers lifting into a new high, sold when price leaves the zone downward):

  trough   a bar whose low is below the lows of the N bars before it; every new one rebuilds the zone
  zone     the K ticks from that low up (L .. L+K-1), with the footprint of the W bars ending at the trough bar
  trigger  the first close at or above L + K within A bars of the trough, once per trough; entry at the next bar's vwap
  stop     L - 2 ticks; brackets at 1R and 2R, out at 60 bars

The GEOMETRY alone (a bounce of K ticks off a fresh N-minute low) is the control: every trigger is recorded, and the
footprint of the zone is a feature of it. The test is whether the footprint sorts the outcomes.

  cd research && python -u scripts/futures/escvd_zones.py > logs/escvd_zones.log 2>&1
"""
import sys, numpy as np, polars as pl
from numba import njit
sys.path.insert(0, 'scripts/futures')
import escvd_lib as L
from escvd_legs import bracket

OUT = 'data/futures/escvd'
W, A, MINS = 3, 30, 20.0


@njit(cache=True)
def engine(e, cl, si, ptr, pt, wv, av, sg, N, W, K, A, minS):
    n = len(e); O = np.full((n, 17), np.nan); cnt = 0
    armed = False; fired = True; tb = 0; Lx = 0.0; s0 = 0
    X = np.zeros(K + 1); Y = np.zeros(K + 1); f = np.zeros(10)
    for t in range(n):
        if t == 0 or si[t] != si[t - 1]:
            s0 = t; armed = False
        if np.isnan(e[t]): continue
        if t - s0 >= N:
            pm = np.inf
            for u in range(t - N, t):
                if e[u] < pm: pm = e[u]
            if e[t] < pm:
                armed = True; fired = False; tb = t; Lx = e[t]
                X[:] = 0.0; Y[:] = 0.0; tw = 0.0; ta = 0.0
                for r in range(ptr[t - W + 1], ptr[t + 1]):
                    j = int(sg * pt[r] - Lx); tw += wv[r]; ta += av[r]
                    if j >= 0 and j <= K:
                        X[j] += wv[r]; Y[j] += av[r]
                nh = 0; nd = 0; stack = 0; run = 0
                for j in range(K):
                    if X[j] >= minS and X[j] >= 3.0 * Y[j]: nh += 1
                    if X[j] >= minS and X[j] >= 3.0 * Y[j + 1]:
                        nd += 1; run += 1
                        if run > stack: stack = run
                    else:
                        run = 0
                f[0] = X[:K].sum(); f[1] = Y[:K].sum(); f[2] = tw; f[3] = ta; f[4] = nh; f[5] = nd; f[6] = stack
                f[7] = X[0]; f[8] = Y[0]; f[9] = pm - Lx
        if armed and (not fired) and t - tb <= A and cl[t] >= Lx + K:
            fired = True
            bzw = 0.0; bza = 0.0; bw = 0.0; ba = 0.0
            for r in range(ptr[tb + 1], ptr[t + 1]):
                j = int(sg * pt[r] - Lx); bw += wv[r]; ba += av[r]
                if j >= 0 and j < K:
                    bzw += wv[r]; bza += av[r]
            O[cnt, 0] = t; O[cnt, 1] = tb; O[cnt, 2] = Lx
            for k in range(10): O[cnt, 3 + k] = f[k]
            O[cnt, 13] = bzw; O[cnt, 14] = bza; O[cnt, 15] = bw; O[cnt, 16] = ba
            cnt += 1
    return O[:cnt]


def main():
    D = L.load(); n, ns, M = D['n'], D['ns'], L.M; si, m = D['si'], D['m']
    kd, kp = L.vol_scales(D); F = L.forward(D); sp = D['sd_px']
    smap = pl.DataFrame({'session': D['sess'], 's': np.arange(ns)})
    fp = (pl.read_parquet(f'{OUT}/fp_price.parquet').join(smap, on='session')
            .with_columns(t=pl.col('s') * M + pl.col('minute')).sort('t', 'pt'))
    tt = fp['t'].to_numpy(); pt = fp['pt'].to_numpy().astype(np.int64)
    bv = fp['bv'].to_numpy().astype(np.float64); sv = fp['sv'].to_numpy().astype(np.float64)
    ptr = np.concatenate([[0], np.cumsum(np.bincount(tt, minlength=n))]).astype(np.int64)
    has = ptr[1:] > ptr[:-1]
    lo = np.full(n, np.nan); hi = np.full(n, np.nan)
    lo[has] = pt[ptr[:-1][has]]; hi[has] = pt[ptr[1:][has] - 1]
    cov = has.reshape(ns, M)[:, L.RTH0:L.RTH1].mean(1)
    print(f'bars {n:,}, footprint rows {len(tt):,}; sessions with an RTH footprint {int((cov > 0.9).sum())} of {ns}')
    adj = D['adj']; cl = D['raw'] * 4.0
    ht = np.where(has, hi / 4.0 + adj, D['c']); lt = np.where(has, lo / 4.0 + adj, D['c'])      # true tick extremes, adjusted
    q = (pl.read_parquet(f'{OUT}/fp_min.parquet').join(smap, on='session').with_columns(t=pl.col('s') * M + pl.col('minute')))
    qt = q['t'].to_numpy()
    def dense(c):
        x = np.zeros(n); x[qt] = q[c].to_numpy(); return np.concatenate([[0.0], np.cumsum(x)])
    big = {'b': dense('bv2') + dense('bv3'), 's': dense('sv2') + dense('sv3')}
    swp = {'b': dense('bsweep'), 's': dense('ssweep')}
    tot = {'b': sum(dense(f'bv{k}') for k in range(4)), 's': sum(dense(f'sv{k}') for k in range(4))}
    bk = {x: [dense(f'{x}v{k}') for k in range(4)] for x in 'bs'}         # by burst size: <5, 5-19, 20-99, 100+
    # context, as of the bar's close
    C2 = D['c'].reshape(ns, M); O2 = D['o'].reshape(ns, M)
    dist1 = (D['c'] - L.rolling_vwap(D, 1)) / sp; dist3 = (D['c'] - L.rolling_vwap(D, 3)) / sp
    day_ret = (D['c'] - np.repeat(O2[:, L.RTH0], M)) / sp
    on_ret = np.repeat(C2[:, L.RTH0 - 1] - O2[:, 0], M) / sp
    Cc = C2[:, L.RTH1 - 1]; ctx = {'dist1': dist1, 'dist3': dist3, 'day_ret': day_ret, 'on_ret': on_ret}
    for k in (1, 5, 20):
        x = np.full(ns, np.nan); x[k + 1:] = Cc[k:-1] - Cc[:-k - 1]
        ctx[f'ret{k}'] = np.repeat(x / (D['sd_px_s'] * np.sqrt(k)), M)
    csv = np.concatenate([[0.0], np.cumsum(D['volume'])])
    rows = []
    for N in (20, 60):
        for K in (2, 4, 6, 8, 12):
            for side, sg in (('L', 1.0), ('S', -1.0)):
                e = lo if sg > 0 else -hi
                wv, av = (sv, bv) if sg > 0 else (bv, sv)
                O = engine(e, sg * cl, si, ptr, pt, wv, av, sg, N, W, K, A, MINS)
                t = O[:, 0].astype(np.int64); tb = O[:, 1].astype(np.int64); Lx = O[:, 2]
                zw, za, tw, ta, nh, nd, stack, x0, y0, brk, bzw, bza, bw, ba = [O[:, k] for k in range(3, 17)]
                w_, a_ = ('s', 'b') if sg > 0 else ('b', 's')
                def win(cs): return cs[tb + 1] - cs[tb + 1 - W]
                d = dict(N=N, K=K, side=side, t=t, tb=tb, session=pl.Series(D['sess']).gather(si[t]), minute=m[t], mtb=m[tb],
                         fit=D['fit_bar'][t], hold=D['hold_bar'][t], lag=t - tb,
                         zvol=(zw + za) / (D['adv20'][t] / 390.0), zratio=zw / np.maximum(zw + za, 1.0),
                         zdz=(za - zw) / np.sqrt(kd[t] * np.maximum(zw + za, 1.0)),
                         share=zw / np.maximum(tw, 1.0), wdz=(ta - tw) / np.sqrt(kd[t] * np.maximum(tw + ta, 1.0)),
                         nh=nh, nd=nd, stack=stack, x0=x0 / np.maximum(zw, 1.0), y0=y0, brk=brk,
                         bigr=win(big[w_]) / np.maximum(win(big[w_]) + win(big[a_]), 1.0),
                         bigshare=win(big[w_]) / np.maximum(win(tot[w_]), 1.0),
                         sweepshare=win(swp[w_]) / np.maximum(win(tot[w_]), 1.0),
                         bdz=(ba - bw) / np.sqrt(kd[t] * np.maximum(ba + bw, 1.0)),
                         bzratio=bza / np.maximum(bza + bzw, 1.0), bzvol=(bza + bzw) / np.maximum(zw + za, 1.0),
                         volr=(csv[t + 1] - csv[t + 1 - N]) / (D['adv20'][t] * N / 390.0))
                wvol = np.maximum(win(tot['b']) + win(tot['s']), 1.0)
                for k in range(4):                                           # the window's delta by burst size, + = toward the trade
                    d[f'd{k}'] = (win(bk[a_][k]) - win(bk[w_][k])) / np.sqrt(kd[t] * wvol)
                    d[f'v{k}'] = (win(bk[a_][k]) + win(bk[w_][k])) / wvol
                for k, v in ctx.items(): d[k] = sg * v[t]                    # signed: + = with the trade
                for h in (5, 10, 20, 30, 60): d[f'f{h}'] = sg * F[h][t]
                h_, l_ = (ht, lt) if sg > 0 else (-lt, -ht)
                stop = (Lx - 2.0) / 4.0 + sg * adj[t]
                for mult in (1, 2):
                    pnl, R, bars = bracket(t, stop, h_, l_, sg * D['vwap'], si, 60, float(mult))
                    d[f'b{mult}_R'] = pnl / R; d[f'b{mult}_bars'] = bars
                    if mult == 1: d['R_bps'] = R / D['raw'][t] * 1e4
                df = pl.DataFrame(d).filter((pl.col('minute') >= L.RTH0) & (pl.col('minute') < 1260))
                rows.append(df)
                print(f'N {N} K {K} {side}: {df.height:,} RTH triggers ({df.filter(pl.col("fit")).height:,} fit)', flush=True)
    out = pl.concat(rows); out.write_parquet(f'{OUT}/zones.parquet'); print('zones.parquet', out.shape)
    # ---- the bracket's own bias: the same bracket on every 5th RTH bar, stop R below the entry, R = the events' median.
    # A bar that touches both the stop and the target counts as the stop, so a small R loses with no signal at all.
    tt_ = np.where((m >= L.RTH0) & (m < 1260) & (m % 5 == 0) & D['fit_bar'])[0]; tt_ = tt_[tt_ + 62 < n]
    for rb in (3.0, 6.0, 8.0, 12.0):
        for side, sg in (('L', 1.0), ('S', -1.0)):
            h_, l_ = (ht, lt) if sg > 0 else (-lt, -ht); vw = sg * D['vwap']
            stop = vw[tt_ + 1] - rb * 1e-4 * D['raw'][tt_]
            pnl, R, bars = bracket(tt_, stop, h_, l_, vw, si, 60, 1.0)
            x = pnl / R; ok = np.isfinite(x)
            print(f'bracket control, random entries, R {rb:.0f} bps {side}: n {ok.sum()}  mean {x[ok].mean():+.3f} R  win {(x[ok] > 0).mean():.3f}')


if __name__ == '__main__':
    main()
