"""Shared pieces of the ES CVD event study (docs/es_cvd_plan.md).

Everything a bar carries is knowable at that bar's close: the scales come from the 20 sessions
BEFORE the bar's own, peaks and troughs are running, and an event is entered at the NEXT bar's vwap.
"""
import numpy as np, polars as pl
from numba import njit

BARS = 'data/futures/escvd/es_1m.parquet'
M = 1380                                   # minutes in a Globex session
RTH0, RTH1 = 930, 1320                     # 09:30 .. 16:00 as session minutes
FIRST, LAST = '2020-06-12', '2026-03-13'   # ESU20 becomes the real front; ESH26 is the last contract held
FIT_END = '2023-12-31'                     # fit <= this, holdout after
HORIZONS = (5, 10, 20, 30, 45, 60, 90, 120)


def load(path=BARS):
    g = pl.read_parquet(path).filter(pl.col('session').is_between(pl.lit(FIRST).str.to_date(), pl.lit(LAST).str.to_date()))
    rth = g.group_by('session').agg(r=pl.col('volume').filter(pl.col('minute').is_between(RTH0, RTH1 - 1)).sum())
    g = g.join(rth.filter(pl.col('r') > 0).select('session'), on='session', how='semi').sort('session', 'minute')
    sess = g['session'].unique().sort()
    ns = len(sess); n = g.height
    assert n == ns * M, 'the grid is not dense'
    D = {k: g[k].to_numpy().astype(np.float64) for k in ('o', 'h', 'l', 'c', 'vwap', 'volume', 'delta', 'adj')}
    D['n'], D['ns'], D['sess'] = n, ns, sess.to_list()
    D['si'] = np.repeat(np.arange(ns), M)
    D['m'] = np.tile(np.arange(M), ns)
    D['raw'] = D['c'] - D['adj']                                  # the unadjusted price, for bps
    D['ts'] = g['ts'].to_numpy()
    # ---- per-session scales from the PRIOR 20 sessions -------------------------------------
    d2 = (D['delta'] ** 2).reshape(ns, M).sum(1)
    dc = np.diff(D['c'], prepend=D['o'][0])
    p2 = (dc ** 2).reshape(ns, M).sum(1)
    v = D['volume'].reshape(ns, M).sum(1)
    def prior_mean(x, k=20):
        cs = np.concatenate([[0.0], np.cumsum(x)]); out = np.full(len(x), np.nan)
        out[k:] = (cs[k:-1] - cs[:-k - 1]) / k
        return out
    D['sd_delta_s'] = np.sqrt(prior_mean(d2))          # a day's delta sigma, in contracts
    D['sd_px_s'] = np.sqrt(prior_mean(p2))             # a day's price sigma, in points
    D['adv20_s'] = prior_mean(v)
    D['sd_delta'] = np.repeat(D['sd_delta_s'], M)
    D['sd_px'] = np.repeat(D['sd_px_s'], M)
    D['adv20'] = np.repeat(D['adv20_s'], M)
    fit_s = np.array([str(s) <= FIT_END for s in D['sess']])
    D['fit_bar'] = np.repeat(fit_s, M) & np.isfinite(D['sd_px'])
    D['hold_bar'] = np.repeat(~fit_s, M)
    return D


@njit(cache=True)
def _state(x, x0, anchor, scale):
    """Running peak and the trough since it, restarted at every anchor bar."""
    n = len(x)
    ddz = np.full(n, np.nan); depthz = np.full(n, np.nan); reb = np.full(n, np.nan)
    ipk = np.full(n, -1, np.int64); itr = np.full(n, -1, np.int64)
    peak = 0.0; trough = 0.0; pk = -1; tr = -1; live = False
    for t in range(n):
        if anchor[t] == 1:
            peak = x0[t]; trough = peak; pk = t; tr = t; live = True
        elif anchor[t] == 2:
            live = False
        if not live: continue
        if x[t] > peak:
            peak = x[t]; trough = x[t]; pk = t; tr = t
        elif x[t] < trough:
            trough = x[t]; tr = t
        s = scale[t]
        ddz[t] = (x[t] - peak) / s
        depthz[t] = (trough - peak) / s
        reb[t] = (x[t] - trough) / (peak - trough) if peak > trough else 0.0
        ipk[t] = pk; itr[t] = tr
    return ddz, depthz, reb, ipk, itr


@njit(cache=True)
def _events(depthz, reb, itr, a, r):
    """First bar at which a drawdown at least `a` deep has retraced `r` of itself; once per trough."""
    out = []
    last = -1
    for t in range(len(reb)):
        if itr[t] < 0: continue
        if depthz[t] <= -a and reb[t] >= r and itr[t] != last:
            out.append(t); last = itr[t]
    return np.array(out, np.int64)


def anchors(D, anchor):
    """1 = restart here, 2 = stop tracking, 0 = carry on."""
    a = np.zeros(D['n'], np.int8)
    if anchor == 'globex':
        a[D['m'] == 0] = 1
    elif anchor == 'rth':
        a[D['m'] == 0] = 2; a[D['m'] == RTH0] = 1; a[D['m'] == RTH1] = 2
    else:
        raise ValueError(anchor)
    return a


def state(D, kind, anchor):
    """kind 'cvd': x is the delta summed since the anchor. kind 'px': x is the close."""
    a = anchors(D, anchor)
    if kind == 'cvd':
        cs = np.cumsum(D['delta'])
        base = np.zeros(D['n']); idx = np.where(a == 1)[0]
        start = np.zeros(D['n'], np.int64); start[idx] = idx
        start = np.maximum.accumulate(start)
        before = np.concatenate([[0.0], cs])[start]              # the cumsum just before the anchor bar
        x = cs - before; x0 = np.zeros(D['n']); scale = D['sd_delta']
    else:
        x = D['c']; x0 = D['o']; scale = D['sd_px']
    ddz, depthz, reb, ipk, itr = _state(x, x0, a, scale)
    return dict(x=x, ddz=ddz, depthz=depthz, reb=reb, ipk=ipk, itr=itr)


def events(st, a, r):
    return _events(st['depthz'], st['reb'], st['itr'], float(a), float(r))


def vol_scales(D):
    """kd, kp per bar: the prior 20 sessions' sum(delta^2)/sum(volume) and sum(dclose^2)/sum(volume), so that
    x / sqrt(k * volume) is a z-score of a delta sum (or a price change) over any stretch with that much volume."""
    ns = D['ns']
    def prior(x, k=20):
        cs = np.concatenate([[0.0], np.cumsum(x)]); out = np.full(len(x), np.nan); out[k:] = cs[k:-1] - cs[:-k - 1]
        return out
    dc = np.diff(D['c'], prepend=D['o'][0])
    vs = prior(D['volume'].reshape(ns, M).sum(1))
    kd = np.repeat(prior((D['delta'] ** 2).reshape(ns, M).sum(1)) / vs, M)
    kp = np.repeat(prior((dc ** 2).reshape(ns, M).sum(1)) / vs, M)
    return kd, kp


def rolling_vwap(D, sessions):
    """Volume-weighted price over the last `sessions` * 1380 bars, through the current bar."""
    k = sessions * M
    pv = np.concatenate([[0.0], np.cumsum(D['vwap'] * D['volume'])]); vv = np.concatenate([[0.0], np.cumsum(D['volume'])])
    i = np.arange(D['n']) + 1; j = np.maximum(i - k, 0)
    out = (pv[i] - pv[j]) / np.maximum(vv[i] - vv[j], 1.0)
    out[:k] = np.nan
    return out


def forward(D):
    """Per bar t: entry at vwap[t+1], exit at vwap[t+1+h], in bps of the raw price. NaN across a session end."""
    n = D['n']; F = {}
    entry = np.full(n, np.nan); entry[:-1] = D['vwap'][1:]
    same = np.zeros(n, bool); same[:-1] = D['si'][1:] == D['si'][:-1]
    entry[~same] = np.nan
    F['entry'] = entry
    for h in HORIZONS:
        x = np.full(n, np.nan)
        x[:-1 - h] = D['vwap'][1 + h:] - D['vwap'][1:-h]
        ok = np.zeros(n, bool); ok[:-1 - h] = D['si'][1 + h:] == D['si'][:-1 - h]
        x[~ok] = np.nan
        F[h] = x / D['raw'] * 1e4
    return F


def excursions(D, ev, h=60):
    """MFE and MAE in bps over the h bars after entry (bars t+1 .. t+h), by the 1s-vwap highs and lows."""
    mfe = np.full(len(ev), np.nan); mae = np.full(len(ev), np.nan)
    for k, t in enumerate(ev):
        if t + 1 + h >= D['n'] or D['si'][t + h] != D['si'][t]: continue
        e = D['vwap'][t + 1]
        mfe[k] = (D['h'][t + 1:t + 1 + h].max() - e) / D['raw'][t] * 1e4
        mae[k] = (D['l'][t + 1:t + 1 + h].min() - e) / D['raw'][t] * 1e4
    return mfe, mae


def cluster_t(x, g):
    """Mean and its session-clustered t: events in one session share a forward path."""
    ok = np.isfinite(x); x = x[ok]; g = g[ok]
    if len(x) < 5: return np.nan, np.nan, len(x)
    mu = x.mean()
    _, inv = np.unique(g, return_inverse=True)
    s = np.bincount(inv, weights=x - mu)
    se = np.sqrt((s ** 2).sum()) / len(x)
    return mu, (mu / se if se > 0 else np.nan), len(x)
