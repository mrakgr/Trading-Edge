#!/usr/bin/env python
"""Charts of the trapped-seller zones (escvd_zones.py), to read the signals by eye.

  cd research && python scripts/futures/escvd_zone_charts.py --date 2023-03-13 [--N 20 --K 4]
  cd research && python scripts/futures/escvd_zone_charts.py --sample 6 --year 2023       # six days spread over the year's range of day returns

One HTML per session in data/charts/escvd/, 08:30-16:00 ET:
  top     the footprint as a heatmap (delta per price per minute: red = sold at the bid, green = bought at the ask),
          1-minute candles, the 24h and 72h vwap, every zone as a box (from the 3 bars into the trough to the trigger),
          and the trigger as a triangle at the next bar's vwap (the entry). A FILLED box / triangle is a zone in the top
          quintile of zratio (the heavy one-sided zones); hover the triangle for the features and the outcome.
  middle  the session's CVD: all, bursts of 20+ contracts, bursts under 20
  bottom  volume, coloured by the bar's delta

A date after the fit sample is refused: the holdout stays unread.
"""
import sys, os, argparse, datetime as dt, numpy as np, polars as pl
import plotly.graph_objects as go
from plotly.subplots import make_subplots
sys.path.insert(0, 'scripts/futures')
import escvd_lib as L

ap = argparse.ArgumentParser()
ap.add_argument('--date', action='append', default=[]); ap.add_argument('--sample', type=int, default=0); ap.add_argument('--year', default='2023')
ap.add_argument('--N', type=int, default=20); ap.add_argument('--K', type=int, default=4)
ap.add_argument('--m0', type=int, default=870); ap.add_argument('--m1', type=int, default=1320)
a = ap.parse_args()

D = L.load(); M = L.M; ns = D['ns']; sess = [str(s) for s in D['sess']]
Z = pl.read_parquet('data/futures/escvd/zones.parquet').filter((pl.col('N') == a.N) & (pl.col('K') == a.K))
zq = Z.filter(pl.col('fit'))['zratio'].quantile(0.8)
v1 = L.rolling_vwap(D, 1) - D['adj']; v3 = L.rolling_vwap(D, 3) - D['adj']
dates = list(a.date)
if a.sample:
    C2 = D['c'].reshape(ns, M); O2 = D['o'].reshape(ns, M)
    r = (C2[:, L.RTH1 - 1] - O2[:, L.RTH0]) / D['sd_px_s']
    idx = [i for i, s in enumerate(sess) if s.startswith(a.year) and s <= L.FIT_END and np.isfinite(r[i])]
    idx.sort(key=lambda i: r[i])
    dates += [sess[idx[int(round(q * (len(idx) - 1)))]] for q in np.linspace(0, 1, a.sample)]
with open('scripts/visualization/chart_controls.js') as f: post = f.read()
os.makedirs('data/charts/escvd', exist_ok=True)

for date in dates:
    assert date <= L.FIT_END, f'{date} is in the holdout'
    s = sess.index(date); sl = slice(s * M + a.m0, s * M + a.m1); t0 = s * M
    ts = D['ts'][sl]; adj = D['adj'][sl]
    o, c = D['o'][sl] - adj, D['c'][sl] - adj
    sd = dt.date.fromisoformat(date)
    fp = pl.read_parquet('data/futures/escvd/fp_price.parquet').filter((pl.col('session') == sd) & pl.col('minute').is_between(a.m0, a.m1 - 1))
    q = pl.read_parquet('data/futures/escvd/fp_min.parquet').filter(pl.col('session') == sd).sort('minute')
    ext = fp.group_by('minute').agg(lo=pl.col('pt').min(), hi=pl.col('pt').max())
    hi = np.maximum(o, c).copy(); lo = np.minimum(o, c).copy(); mi = ext['minute'].to_numpy() - a.m0
    hi[mi] = ext['hi'].to_numpy() / 4.0; lo[mi] = ext['lo'].to_numpy() / 4.0
    # ---- the footprint heatmap ----
    p0, p1 = int(fp['pt'].min()), int(fp['pt'].max())
    G = np.full((p1 - p0 + 1, a.m1 - a.m0), np.nan)
    G[fp['pt'].to_numpy() - p0, fp['minute'].to_numpy() - a.m0] = (fp['bv'] - fp['sv']).to_numpy()
    cap = np.nanquantile(np.abs(G), 0.97)
    fig = make_subplots(rows=3, cols=1, shared_xaxes=True, row_heights=[0.68, 0.18, 0.14], vertical_spacing=0.02)
    fig.add_trace(go.Heatmap(x=ts, y=(np.arange(p0, p1 + 1)) / 4.0, z=np.clip(G, -cap, cap), zmid=0, colorscale='RdYlGn', opacity=0.75,
                             showscale=False, hovertemplate='%{x|%H:%M} %{y:.2f} delta %{z:.0f}<extra></extra>', name='footprint'), row=1, col=1)
    fig.add_trace(go.Candlestick(x=ts, open=o, high=hi, low=lo, close=c, name='ES', increasing_line_color='#111', decreasing_line_color='#111',
                                 increasing_fillcolor='rgba(255,255,255,0.9)', decreasing_fillcolor='rgba(0,0,0,0.9)', line_width=1), row=1, col=1)
    fig.add_trace(go.Scatter(x=ts, y=v1[sl], name='24h vwap', line=dict(color='#1f77b4', width=1.5)), row=1, col=1)
    fig.add_trace(go.Scatter(x=ts, y=v3[sl], name='72h vwap', line=dict(color='#9467bd', width=1.5, dash='dot')), row=1, col=1)
    # ---- zones and triggers ----
    z = Z.filter(pl.col('session') == sd).sort('t')
    shapes = []; mk = {('L', True): [], ('L', False): [], ('S', True): [], ('S', False): []}
    for r in z.iter_rows(named=True):
        t, tb = r['t'] - t0, r['tb'] - t0
        if t + 1 >= a.m1 or tb - 2 < a.m0: continue
        sg = 1 if r['side'] == 'L' else -1
        ext_px = (lo if sg > 0 else hi)[tb - a.m0]
        y0, y1 = (ext_px, ext_px + a.K / 4.0) if sg > 0 else (ext_px - a.K / 4.0, ext_px)
        heavy = r['zratio'] >= zq; col = '214,39,40' if sg > 0 else '44,160,44'
        shapes.append(dict(type='rect', xref='x', yref='y', x0=D['ts'][t0 + tb - 2], x1=D['ts'][t0 + t], y0=y0, y1=y1,
                           line=dict(color=f'rgba({col},0.9)', width=1.5), fillcolor=f'rgba({col},{0.45 if heavy else 0.0})'))
        txt = (f"{r['side']} {str(D['ts'][t0 + t + 1])[11:16]}  zratio {r['zratio']:.2f}  zone dz {r['zdz']:+.1f}  3-bar dz {r['wdz']:+.1f}<br>"
               f"big-lot share of the hitting side {r['bigr']:.2f}  stack {r['stack']:.0f}  lag {r['lag']}<br>"
               f"d by size: {r['d0']:+.1f} {r['d1']:+.1f} {r['d2']:+.1f} {r['d3']:+.1f}<br>"
               f"f10 {r['f10']:+.1f}  f30 {r['f30']:+.1f}  f60 {r['f60']:+.1f} bps   1R bracket {r['b1_R']:+.2f} (R {r['R_bps']:.1f} bps)")
        mk[(r['side'], heavy)].append((D['ts'][t0 + t + 1], D['vwap'][t0 + t + 1] - D['adj'][t0 + t + 1], txt))
    for (side, heavy), v in mk.items():
        if not v: continue
        col = '#d62728' if side == 'L' else '#2ca02c'
        fig.add_trace(go.Scatter(x=[x[0] for x in v], y=[x[1] for x in v], mode='markers', text=[x[2] for x in v], hovertemplate='%{text}<extra></extra>',
                                 name=f"{'long' if side == 'L' else 'short'} {'heavy zone' if heavy else 'other'}",
                                 marker=dict(symbol=('triangle-up' if side == 'L' else 'triangle-down') + ('' if heavy else '-open'), size=13 if heavy else 9,
                                             color=col, line=dict(color='#000', width=1))), row=1, col=1)
    # ---- CVD by burst size, volume ----
    qm = q['minute'].to_numpy(); keep = (qm >= a.m0) & (qm < a.m1)
    def dens(x):
        y = np.zeros(M); y[qm] = x; return np.cumsum(y)[a.m0:a.m1]
    dsm = (q['bv0'] + q['bv1'] - q['sv0'] - q['sv1']).to_numpy(); dbg = (q['bv2'] + q['bv3'] - q['sv2'] - q['sv3']).to_numpy()
    for y, nm, col in ((dens(dsm + dbg), 'CVD', '#111'), (dens(dbg), 'CVD bursts 20+', '#d62728'), (dens(dsm), 'CVD bursts <20', '#1f77b4')):
        fig.add_trace(go.Scatter(x=ts, y=y - y[max(L.RTH0 - a.m0 - 1, 0)], name=nm, line=dict(color=col, width=1.3)), row=2, col=1)
    dl = D['delta'][sl]
    fig.add_trace(go.Bar(x=ts, y=D['volume'][sl], marker_color=np.where(dl >= 0, '#2ca02c', '#d62728'), name='volume', showlegend=False), row=3, col=1)
    nz = sum(len(v) for v in mk.values()); nh = len(mk[('L', True)]) + len(mk[('S', True)])
    fig.update_layout(title=f'ES {date}  zones N {a.N} K {a.K} ticks: {nz} triggers, {nh} heavy (zratio >= {zq:.2f})  |  filled = heavy one-sided zone; red = long off a low, green = short off a high',
                      shapes=shapes, xaxis_rangeslider_visible=False, height=1000, template='plotly_white', hovermode='closest', dragmode='pan',
                      legend=dict(orientation='h', y=1.02, x=0))
    fig.update_yaxes(title_text='price', row=1, col=1); fig.update_yaxes(title_text='CVD since 09:30', row=2, col=1); fig.update_yaxes(title_text='volume', row=3, col=1)
    out = f'data/charts/escvd/{date}_N{a.N}_K{a.K}.html'
    fig.write_html(out, config={'scrollZoom': True}, post_script=post, include_plotlyjs='cdn')
    print(f'{out}: {nz} triggers, {nh} heavy; day return {(c[-1] - o[L.RTH0 - a.m0]) / o[L.RTH0 - a.m0] * 1e4:+.0f} bps')
