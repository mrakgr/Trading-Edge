#!/usr/bin/env python
"""ES CVD event study: the event table (docs/es_cvd_plan.md).

  cd research && python -u scripts/futures/escvd_events.py > logs/escvd_events.log 2>&1

An EVENT is the first bar at which a drawdown at least `a` day-sigmas deep (from the running peak
since the anchor) has retraced a fraction `r` of itself. kind 'cvd' runs that on the cumulative
delta, kind 'px' on the price: the price-only twin. The twin's depths are the CVD depths mapped
through the fit-period distribution of session-deepest drawdowns, so the two fire about as often.

Output: data/futures/escvd/events.parquet (fit and holdout both, flagged) and bars_base.parquet
(the every-bar forward returns the baselines are built from).
"""
import sys, time
import numpy as np, polars as pl
sys.path.insert(0, 'scripts/futures')
import escvd_lib as L

T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, flush=True)

A_CVD = (1.0, 1.5, 2.0, 3.0)
R = (0.25, 0.5)
OUT = 'data/futures/escvd'

D = L.load(); n = D['n']
log(f'{n} bars, {D["ns"]} sessions {D["sess"][0]} .. {D["sess"][-1]}')
F = L.forward(D)
V = {k: L.rolling_vwap(D, k) for k in (1, 2, 3)}
dist = {k: (D['c'] - V[k]) / D['sd_px'] for k in V}

def session_min(ddz):
    ok = D['fit_bar'] & np.isfinite(ddz)
    mn = np.zeros(D['ns']); np.minimum.at(mn, D['si'][ok], ddz[ok])
    return -mn[np.unique(D['si'][ok])]

rows = []
for anchor in ('globex', 'rth'):
    S = {k: L.state(D, k, anchor) for k in ('cvd', 'px')}
    qc, qp = np.sort(session_min(S['cvd']['ddz'])), np.sort(session_min(S['px']['ddz']))
    a_px = [float(np.quantile(qp, np.searchsorted(qc, a) / len(qc))) for a in A_CVD]
    log(f'{anchor}: cvd depths {A_CVD} -> price twin depths {np.round(a_px, 2)}')
    for kind, depths in (('cvd', A_CVD), ('px', a_px)):
        st, other = S[kind], S['px' if kind == 'cvd' else 'cvd']
        for lvl, a in enumerate(depths):
            for r in R:
                ev = L.events(st, a, r)
                ev = ev[np.isfinite(D['sd_px'][ev]) & np.isfinite(F['entry'][ev])]
                mfe, mae = L.excursions(D, ev, 60)
                ipk, itr = st['ipk'][ev], st['itr'][ev]
                tgt = (V[1][ev] - F['entry'][ev]) / D['raw'][ev] * 1e4          # the 24h VWAP, in bps above the entry
                e = dict(
                    kind=[kind] * len(ev), anchor=[anchor] * len(ev), lvl=np.full(len(ev), lvl), a=np.full(len(ev), a),
                    r=np.full(len(ev), r), t=ev, session=pl.Series(D['sess']).gather(D['si'][ev]), minute=D['m'][ev], ts=D['ts'][ev],
                    fit=D['fit_bar'][ev], depthz=st['depthz'][ev], reb=st['reb'][ev], ddz=st['ddz'][ev],
                    bars_pk=ev - ipk, bars_tr=ev - itr,
                    o_ddz=other['ddz'][ev], o_depthz=other['depthz'][ev], o_reb=other['reb'][ev],
                    px_leg=(D['c'][itr] - D['c'][ipk]) / D['sd_px'][ev],         # the price move over the drawdown
                    px_off=(D['c'][ev] - D['c'][itr]) / D['sd_px'][ev],          # the price move since the trough bar
                    cvd_leg=(np.cumsum(D['delta'])[itr] - np.cumsum(D['delta'])[ipk]) / D['sd_delta'][ev],
                    dist1=dist[1][ev], dist2=dist[2][ev], dist3=dist[3][ev],
                    sd_bps=D['sd_px'][ev] / D['raw'][ev] * 1e4, tgt_bps=tgt, mfe60=mfe, mae60=mae)
                for h in L.HORIZONS: e[f'f{h}'] = F[h][ev]
                rows.append(pl.DataFrame(e))
        log(f'{anchor} {kind}: done')
E = pl.concat(rows)
E.write_parquet(f'{OUT}/events.parquet')
log(f'wrote {OUT}/events.parquet: {E.height} events')

B = {'t': np.arange(n), 'si': D['si'], 'minute': D['m'], 'fit': D['fit_bar'], 'dist1': dist[1]}
for anchor in ('globex', 'rth'):
    for k in ('cvd', 'px'):
        B[f'ddz_{k}_{anchor}'] = L.state(D, k, anchor)['ddz']
for h in L.HORIZONS: B[f'f{h}'] = F[h]
pl.DataFrame(B).write_parquet(f'{OUT}/bars_base.parquet')
log(f'wrote {OUT}/bars_base.parquet')
print(E.filter(pl.col('fit')).group_by('kind', 'anchor', 'lvl', 'r').agg(pl.len(), pl.col('session').n_unique().alias('sessions')).sort('anchor', 'kind', 'lvl', 'r'))
