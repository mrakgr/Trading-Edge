#!/usr/bin/env python
"""ES CVD study, the LEG engine: the anchor is reset by a new N-minute extreme (the Fader leg counters).

  cd research && python -u scripts/futures/escvd_legs.py          -> data/futures/escvd/legs.parquet

Long side (the short side is the same pass on the negated tape):
  ANCHOR   a bar that prints a new N-minute high. Everything below is accumulated from the bar after it.
  ARMED    once a new N-minute low has printed since the anchor (k = how many).
  the leg carries: the delta sum (cumD), its running peak and the trough since it; the volume; the SELLERS' vwap
  (sell volume = (volume - delta) / 2, weighted by the bar vwap); the low; the worst 5-minute delta window (the
  CLIMAX) with its high; OLS slopes of price, CVD and volume against the bar count.
  TRIGGERS, each once per trough, evaluated at a bar close AFTER the trough bar, entry at the NEXT bar's vwap:
    sv   close > the leg's sellers' vwap            (the trapped-seller reclaim: what footprint traders describe)
    cx   close > the high of the climax window
    p25 / p50   price has retraced 25 / 50 % of the leg       (the price-only twins)
    c25 / c50   CVD has retraced 25 / 50 % of its drawdown    (the flip; re-armed by a new CVD trough)
Nothing here reads past the bar: extremes are of the PRIOR N bars, scales of the prior 20 sessions.
"""
import sys, numpy as np, polars as pl
from numba import njit
sys.path.insert(0, 'scripts/futures')
import escvd_lib as L

TRIGS = ('sv', 'cx', 'p25', 'p50', 'c25', 'c50')
COLS = ('t', 'trig', 'anchor', 'flow', 'itr', 'k', 'nfire', 'pk', 'low', 'cumD', 'maxD', 'minD', 'legvol', 'svwap',
        'cumD_tr', 'vol_tr', 'clxD', 'clxV', 'clxH', 'clxT', 'sl_px', 'r_px', 'sl_cvd', 'r_cvd', 'sl_vol', 'c_anchor')


@njit(cache=True)
def prior_ext(h, l, si, N):
    n = len(h); hi = np.full(n, np.nan); lo = np.full(n, np.nan); s0 = 0
    for t in range(n):
        if t == 0 or si[t] != si[t - 1]: s0 = t
        if t - s0 >= N:
            hi[t] = h[t - N:t].max(); lo[t] = l[t - N:t].min()
    return hi, lo


@njit(cache=True)
def legs(h, l, c, vw, vol, dlt, si, hiN, loN):
    n = len(h); out = np.empty((n * 3, 26)); ne = 0
    have = False; anchor = -1
    cumD = maxD = minD = legvol = spv = sv = pk = low = 0.0
    cumD_tr = vol_tr = clxD = clxV = clxH = 0.0; clxT = -1; itr = -1; k = 0; flow = -1; ca = 0.0
    sj = sjj = 0.0; sy = np.zeros(3); sjy = np.zeros(3); syy = np.zeros(3)
    fired = np.zeros(6, np.bool_); nfire = np.zeros(6, np.int64)
    for t in range(n):
        if t == 0 or si[t] != si[t - 1]: have = False
        if np.isnan(hiN[t]): continue
        if h[t] > hiN[t]:                                   # a new N-minute high: the anchor resets
            have = True; anchor = t; cumD = 0.0; maxD = 0.0; minD = 0.0; legvol = 0.0; spv = 0.0; sv = 0.0
            pk = h[t]; low = l[t]; itr = t; k = 0; flow = -1; ca = c[t]
            clxD = 0.0; clxV = 0.0; clxH = h[t]; clxT = -1; cumD_tr = 0.0; vol_tr = 0.0
            sj = 0.0; sjj = 0.0; sy[:] = 0.0; sjy[:] = 0.0; syy[:] = 0.0
            fired[:] = False; nfire[:] = 0
            continue
        if not have: continue
        cumD += dlt[t]; legvol += vol[t]
        s = (vol[t] - dlt[t]) / 2.0; spv += s * vw[t]; sv += s
        j = float(t - anchor); sj += j; sjj += j * j
        y0 = c[t] - ca; y1 = cumD; y2 = vol[t]
        sy[0] += y0; sjy[0] += j * y0; syy[0] += y0 * y0
        sy[1] += y1; sjy[1] += j * y1; syy[1] += y1 * y1
        sy[2] += y2; sjy[2] += j * y2; syy[2] += y2 * y2
        if cumD > maxD:
            maxD = cumD; minD = cumD
        elif cumD < minD:
            minD = cumD; fired[4] = False; fired[5] = False
        a5 = max(t - 4, anchor + 1); d5 = 0.0; v5 = 0.0; h5 = h[a5]
        for u in range(a5, t + 1):
            d5 += dlt[u]; v5 += vol[u]
            if h[u] > h5: h5 = h[u]
        if d5 < clxD:
            clxD = d5; clxV = v5; clxH = h5; clxT = t; fired[1] = False
        if l[t] < loN[t]:
            k += 1
            if k == 1: flow = t
        if l[t] < low:
            low = l[t]; itr = t; cumD_tr = cumD; vol_tr = legvol
            fired[0] = False; fired[1] = False; fired[2] = False; fired[3] = False
        if k < 1 or t <= itr: continue
        m = j
        den = m * sjj - sj * sj
        for q in range(6):
            if fired[q]: continue
            hit = False
            if q == 0: hit = sv > 0 and c[t] > spv / sv
            elif q == 1: hit = clxT >= 0 and t > clxT and c[t] > clxH
            elif q == 2: hit = pk > low and (c[t] - low) / (pk - low) >= 0.25
            elif q == 3: hit = pk > low and (c[t] - low) / (pk - low) >= 0.50
            elif q == 4: hit = maxD > minD and (cumD - minD) / (maxD - minD) >= 0.25
            elif q == 5: hit = maxD > minD and (cumD - minD) / (maxD - minD) >= 0.50
            if not hit: continue
            fired[q] = True; nfire[q] += 1
            o = out[ne]; ne += 1
            o[0] = t; o[1] = q; o[2] = anchor; o[3] = flow; o[4] = itr; o[5] = k; o[6] = nfire[q]; o[7] = pk; o[8] = low
            o[9] = cumD; o[10] = maxD; o[11] = minD; o[12] = legvol; o[13] = spv / sv if sv > 0 else np.nan
            o[14] = cumD_tr; o[15] = vol_tr; o[16] = clxD; o[17] = clxV; o[18] = clxH; o[19] = clxT
            for z in range(3):
                sl = (m * sjy[z] - sj * sy[z]) / den if den > 0 else np.nan
                vy = m * syy[z] - sy[z] * sy[z]
                r = (m * sjy[z] - sj * sy[z]) / np.sqrt(den * vy) if den > 0 and vy > 0 else np.nan
                if z == 0: o[20] = sl; o[21] = r
                elif z == 1: o[22] = sl; o[23] = r
                else: o[24] = sl
            o[25] = ca
    return out[:ne]


@njit(cache=True)
def bracket(ev, stop, h, l, vw, si, H, mult):
    """Entry vwap[t+1]; from bar t+2: the stop first, then entry + mult * R; else out at vwap[t+1+H]. In points."""
    n = len(h); pnl = np.full(len(ev), np.nan); R = np.full(len(ev), np.nan); bars = np.full(len(ev), np.nan)
    for i in range(len(ev)):
        t = ev[i]
        if t + 1 + H >= n or si[t + 1 + H] != si[t]: continue
        e = vw[t + 1]; r = e - stop[i]
        if not r > 0: continue
        R[i] = r; pnl[i] = vw[t + 1 + H] - e; bars[i] = H
        for u in range(t + 2, t + 1 + H):
            if l[u] <= stop[i]:
                pnl[i] = -r; bars[i] = u - t - 1; break
            if h[u] >= e + mult * r:
                pnl[i] = mult * r; bars[i] = u - t - 1; break
    return pnl, R, bars


def main():
    D = L.load(); n = D['n']; ns = D['ns']; M = L.M
    kd, kp = L.vol_scales(D)
    F = L.forward(D)
    v1, v3 = L.rolling_vwap(D, 1), L.rolling_vwap(D, 3)
    # ---- the day-scale context, all from sessions BEFORE the bar's own ----------------------
    C = D['c'].reshape(ns, M)[:, L.RTH1 - 1]                       # the 16:00 close
    def lag_ret(k):
        x = np.full(ns, np.nan); x[k + 1:] = C[k:-1] - C[:-k - 1]
        return x / (D['sd_px_s'] * np.sqrt(k))
    ret5, ret20 = lag_ret(5), lag_ret(20)
    poc = np.full(ns, np.nan); vah = np.full(ns, np.nan); val = np.full(ns, np.nan)
    vwr = D['vwap'].reshape(ns, M)[:, L.RTH0:L.RTH1]; vor = D['volume'].reshape(ns, M)[:, L.RTH0:L.RTH1]
    for s in range(ns):
        b = np.round(vwr[s]).astype(np.int64); b0 = b.min()
        prof = np.bincount(b - b0, weights=vor[s])
        if prof.sum() <= 0: continue
        i0 = prof.argmax(); poc[s] = b0 + i0
        lo = hi = i0; tot = prof.sum(); acc = prof[i0]                # the 70 % value area, grown from the mode
        while acc < 0.7 * tot:
            up = prof[hi + 1] if hi + 1 < len(prof) else -1.0; dn = prof[lo - 1] if lo > 0 else -1.0
            if up >= dn: hi += 1; acc += up
            else: lo -= 1; acc += dn
        vah[s] = b0 + hi; val[s] = b0 + lo
    prev = lambda x: np.concatenate([[np.nan], x[:-1]])
    poc_p, vah_p, val_p = prev(poc), prev(vah), prev(val)
    hi_s = np.maximum.accumulate(D['h'].reshape(ns, M), axis=1).ravel(); lo_s = np.minimum.accumulate(D['l'].reshape(ns, M), axis=1).ravel()

    frames = []
    for N in (20, 60):
        for side, sg in (('long', 1.0), ('short', -1.0)):
            if sg > 0: h, l = D['h'], D['l']
            else: h, l = -D['l'], -D['h']
            c, vw, dl = sg * D['c'], sg * D['vwap'], sg * D['delta']
            hiN, loN = prior_ext(h, l, D['si'], N)
            E = legs(h, l, c, vw, D['volume'], dl, D['si'], hiN, loN)
            e = {k: E[:, i] for i, k in enumerate(COLS)}
            t = e['t'].astype(np.int64)
            sp, sd, raw = D['sd_px'][t], D['sd_delta'][t], D['raw'][t]
            ct = c[t]
            o = dict(N=np.full(len(t), N), side=np.full(len(t), side), trig=np.array(TRIGS)[e['trig'].astype(int)], t=t,
                     session=pl.Series(D['sess'])[D['si'][t]], minute=D['m'][t], fit=D['fit_bar'][t], hold=D['hold_bar'][t],
                     k=e['k'], nfire=e['nfire'], bars_anchor=t - e['anchor'], bars_flow=t - e['flow'], bars_tr=t - e['itr'],
                     depth_z=(e['low'] - e['pk']) / sp, depth_bps=(e['low'] - e['pk']) / raw * 1e4,
                     reb=(ct - e['low']) / (e['pk'] - e['low']),
                     legD_z=e['cumD'] / np.sqrt(kd[t] * np.maximum(e['legvol'], 1)),
                     legDtr_z=e['cumD_tr'] / np.sqrt(kd[t] * np.maximum(e['vol_tr'], 1)),
                     legP_z=(ct - e['c_anchor']) / np.sqrt(kp[t] * np.maximum(e['legvol'], 1)),
                     legD_day=e['cumD'] / sd, cvd_dd_day=(e['minD'] - e['maxD']) / sd,
                     cvd_dd_z=(e['minD'] - e['maxD']) / np.sqrt(kd[t] * np.maximum(e['legvol'], 1)),
                     cvd_reb=np.where(e['maxD'] > e['minD'], (e['cumD'] - e['minD']) / (e['maxD'] - e['minD']), np.nan),
                     clx_z=e['clxD'] / np.sqrt(kd[t] * np.maximum(e['clxV'], 1)), clx_day=e['clxD'] / sd, clx_n=e['clxD'],
                     bars_clx=t - e['clxT'], legvol_adv=e['legvol'] / D['adv20'][t],
                     sv_gap=(ct - e['svwap']) / sp, sv_pos=(e['svwap'] - e['low']) / (e['pk'] - e['low']),
                     sl_px=e['sl_px'] / sp * 60, r_px=e['r_px'], sl_cvd=e['sl_cvd'] / sd * 60, r_cvd=e['r_cvd'],
                     sl_vol=e['sl_vol'] / np.maximum(e['legvol'] / np.maximum(t - e['anchor'], 1), 1),
                     dist1=sg * (D['c'][t] - v1[t]) / sp, dist3=sg * (D['c'][t] - v3[t]) / sp,
                     ret5=sg * ret5[D['si'][t]], ret20=sg * ret20[D['si'][t]],
                     poc_d=sg * (D['c'][t] - poc_p[D['si'][t]]) / sp, vah_d=sg * (D['c'][t] - vah_p[D['si'][t]]) / sp,
                     val_d=sg * (D['c'][t] - val_p[D['si'][t]]) / sp,
                     sess_pos=np.where(hi_s[t] > lo_s[t], (D['c'][t] - lo_s[t]) / (hi_s[t] - lo_s[t]), np.nan),
                     risk_bps=(ct - e['low']) / raw * 1e4)
            if sg < 0: o['sess_pos'] = 1 - o['sess_pos']
            for hh in L.HORIZONS: o[f'f{hh}'] = sg * F[hh][t]
            stop = e['low'] - 0.25
            for mult in (1.0, 2.0):
                pnl, R, bars = bracket(t, stop, h, l, vw, D['si'], 60, mult)
                o[f'b{int(mult)}_bps'] = pnl / raw * 1e4; o[f'b{int(mult)}_R'] = pnl / R; o[f'b{int(mult)}_bars'] = bars
            o['R_bps'] = R / raw * 1e4
            frames.append(pl.DataFrame(o))
            print(f'N={N} {side}: {len(t)} events, ' + ' '.join(f'{q}={int((e["trig"] == i).sum())}' for i, q in enumerate(TRIGS)), flush=True)
    out = pl.concat(frames)
    out.write_parquet('data/futures/escvd/legs.parquet')
    print(out.shape, 'fit', out['fit'].sum(), 'hold', out['hold'].sum())

if __name__ == '__main__':
    main()
