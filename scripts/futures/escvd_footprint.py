#!/usr/bin/env python
"""Footprint charts of ES from the tick tape (escvd_fp.py), to read the auction by eye.

  cd research && python scripts/futures/escvd_footprint.py --date 2023-04-04 [--bar 5] [--png]

One HTML per session in data/charts/escvd/footprint_<date>_<bar>m.html, 08:30-16:00 ET, opening on 09:30-10:30 (drag to pan,
scroll to zoom; the numbers appear once a cell is large enough to hold them).

  main    one column per bar, one cell per price: SELL x BUY = contracts that hit the bid x contracts that lifted the ask at
          that price in that bar. The cell is shaded by its delta (buy - sell): red sold, green bought.
          A bold coloured number is a DIAGONAL IMBALANCE: buys at a price >= ratio x the sells one tick below (green), or
          sells at a price >= ratio x the buys one tick above (red), on at least --minvol contracts. A thick coloured frame
          around consecutive cells is a STACK of --stack or more imbalances in a row (red = sellers, green = buyers).
          The black frame is the bar's POC (its heaviest price); the strip on the column's left edge is the candle body.
          Lines: 24h and 72h vwap, the prior day's POC and value area (RTH, 70%).
  right   the day's volume profile over the charted window, sells and buys stacked
  bottom  per bar: volume, delta, delta as a % of volume, the session's CVD since 09:30

A date after the fit sample is refused: the holdout stays unread.
"""
import sys, os, argparse, datetime as dt, numpy as np, polars as pl
import plotly.graph_objects as go
from plotly.subplots import make_subplots
sys.path.insert(0, 'scripts/futures')
import escvd_lib as L

ap = argparse.ArgumentParser()
ap.add_argument('--date', action='append', default=[]); ap.add_argument('--bar', type=int, default=5)
ap.add_argument('--m0', type=int, default=870); ap.add_argument('--m1', type=int, default=1320)
ap.add_argument('--view0', type=int, default=930); ap.add_argument('--view1', type=int, default=990)
ap.add_argument('--ratio', type=float, default=3.0); ap.add_argument('--minvol', type=int, default=25); ap.add_argument('--stack', type=int, default=3)
ap.add_argument('--N', type=int, default=20); ap.add_argument('--K', type=int, default=4)
ap.add_argument('--png', action='store_true', help='also a static picture of the opening view')
a = ap.parse_args()
B = a.bar
assert (a.m1 - a.m0) % B == 0 and (L.RTH0 - a.m0) % B == 0, 'the window must be whole bars and 09:30 must start one'

D = L.load(); M = L.M; sess = [str(s) for s in D['sess']]
Z = pl.read_parquet('data/futures/escvd/zones.parquet').filter((pl.col('N') == a.N) & (pl.col('K') == a.K))
zq = Z.filter(pl.col('fit'))['zratio'].quantile(0.8)
v1 = L.rolling_vwap(D, 1) - D['adj']; v3 = L.rolling_vwap(D, 3) - D['adj']
FP = pl.scan_parquet('data/futures/escvd/fp_price.parquet')
with open('scripts/visualization/chart_controls.js') as f: post = f.read()
os.makedirs('data/charts/escvd', exist_ok=True)
RED, GRN = '#c00000', '#00701c'
# plotly sizes a heatmap's text from the raw string, markup included, so the numbers come out tiny: size them to the cell here instead
post += """
(function() {
    var gd = document.querySelector('.plotly-graph-div'), BARMS = %d, last = null, busy = false;
    function fit() {
        if (busy) return;
        var ya = gd._fullLayout.yaxis, xa = gd._fullLayout.xaxis;
        var rowPx = ya._length / Math.abs(ya.range[1] - ya.range[0]) * 0.25;
        var colPx = xa._length / Math.abs(xa.r2l(xa.range[1]) - xa.r2l(xa.range[0])) * BARMS;
        var sz = Math.min(Math.floor(Math.min(rowPx * 0.85, colPx / 6.5)), 15);
        if (sz < 6) sz = 0;
        if (sz === last) return;
        last = sz; busy = true;
        Plotly.restyle(gd, sz ? {'textfont.size': sz, texttemplate: '%%{text}'} : {texttemplate: ''}, [0]).then(function() { busy = false; fit(); });
    }
    gd.on('plotly_relayout', fit); window.addEventListener('resize', function() { setTimeout(fit, 300); }); fit();
})();
""" % (B * 60000)


def prior_levels(s):
    """POC and 70% value area of the previous session's RTH, in today's contract prices."""
    if s == 0: return None
    f = (FP.filter((pl.col('session') == dt.date.fromisoformat(sess[s - 1])) & pl.col('minute').is_between(L.RTH0, L.RTH1 - 1))
           .group_by('pt').agg(v=(pl.col('bv') + pl.col('sv')).sum()).sort('pt').collect())
    if f.height == 0: return None
    pt, v = f['pt'].to_numpy(), f['v'].to_numpy(); i = j = int(v.argmax()); tot, acc = v.sum(), v[i]
    while acc < 0.7 * tot and (i > 0 or j < len(v) - 1):
        up = v[j + 1] if j < len(v) - 1 else -1; dn = v[i - 1] if i > 0 else -1
        if up >= dn: j += 1; acc += up
        else: i -= 1; acc += dn
    sh = D['adj'][(s - 1) * M + L.RTH0] - D['adj'][s * M + L.RTH0]          # a roll between the two sessions moves the level
    return dict(POC=pt[int(v.argmax())] / 4 + sh, VAH=pt[j] / 4 + sh, VAL=pt[i] / 4 + sh)


for date in a.date:
    assert date <= L.FIT_END, f'{date} is in the holdout'
    s = sess.index(date); t0 = s * M; sl = slice(t0 + a.m0, t0 + a.m1); sd = dt.date.fromisoformat(date)
    ts = D['ts'][sl]; adj = D['adj'][sl]; o1, c1 = D['o'][sl] - adj, D['c'][sl] - adj
    nb = (a.m1 - a.m0) // B
    fp = (FP.filter((pl.col('session') == sd) & pl.col('minute').is_between(a.m0, a.m1 - 1))
            .with_columns(b=(pl.col('minute') - a.m0) // B).group_by('b', 'pt').agg(pl.col('bv').sum(), pl.col('sv').sum()).collect())
    p0, p1 = int(fp['pt'].min()), int(fp['pt'].max()); nr = p1 - p0 + 1
    BV = np.zeros((nr, nb), np.int64); SV = np.zeros((nr, nb), np.int64)
    r_, b_ = fp['pt'].to_numpy() - p0, fp['b'].to_numpy()
    BV[r_, b_] = fp['bv'].to_numpy(); SV[r_, b_] = fp['sv'].to_numpy()
    T = BV + SV; has = T > 0; dlt = BV - SV
    px = (p0 + np.arange(nr)) / 4.0
    step = np.timedelta64(B * 60, 's'); x0 = ts[::B]; xc = x0 + step // 2; x1 = x0 + step
    O = o1[::B]; C = c1[B - 1::B]
    # ---- diagonal imbalances and their stacks ----
    below = np.vstack([np.zeros((1, nb), np.int64), SV[:-1]]); above = np.vstack([BV[1:], np.zeros((1, nb), np.int64)])
    bi = (BV >= a.minvol) & (BV >= a.ratio * np.maximum(below, 1))            # buyers at p overwhelm the sellers at p-1
    si = (SV >= a.minvol) & (SV >= a.ratio * np.maximum(above, 1))            # sellers at p overwhelm the buyers at p+1
    def runs(mask):
        out = []
        for j in range(nb):
            i = 0
            while i < nr:
                if mask[i, j]:
                    k = i
                    while k + 1 < nr and mask[k + 1, j]: k += 1
                    if k - i + 1 >= a.stack: out.append((j, i, k))
                    i = k + 1
                else: i += 1
        return out
    bst, sst = runs(bi), runs(si)
    # ---- the cells ----
    def num(v, on, col): return f'<span style="color:{col}"><b>{v}</b></span>' if on else str(v)
    txt = np.full((nr, nb), '', dtype=object)
    for i, j in zip(*np.nonzero(has)): txt[i, j] = f'{num(SV[i, j], si[i, j], RED)} x {num(BV[i, j], bi[i, j], GRN)}'
    cap = max(np.quantile(np.abs(dlt[has]), 0.97), 1.0)
    z = np.where(has, np.clip(dlt, -cap, cap), np.nan)
    cd = np.dstack([SV, BV, dlt, T]).tolist(); txt = txt.tolist()             # plain lists: plotly.js does not take a typed 3-D customdata
    pale = [[0, '#f29a9a'], [0.5, '#ffffff'], [1, '#93d893']]
    fig = make_subplots(rows=2, cols=2, shared_xaxes=True, shared_yaxes=True, column_widths=[0.91, 0.09], row_heights=[0.84, 0.16],
                        vertical_spacing=0.015, horizontal_spacing=0.005, specs=[[{}, {}], [{}, None]])
    fig.add_trace(go.Heatmap(x=xc, y=px, z=z, zmin=-cap, zmax=cap, colorscale=pale, showscale=False, xgap=3, ygap=1, text=txt, texttemplate='',
                             textfont=dict(family='Arial', color='#111', size=9), customdata=cd, name='footprint',
                             hovertemplate='%{x|%H:%M} bar  %{y:.2f}<br>sell %{customdata[0]} x buy %{customdata[1]}<br>delta %{customdata[2]:+d}  volume %{customdata[3]}<extra></extra>'),
                  row=1, col=1)
    def segs(items):
        xs, ys = [], []
        for q in items: xs += q[0] + [None]; ys += q[1] + [None]
        return xs, ys
    poc = T.argmax(axis=0); live = has.any(axis=0)
    xs, ys = segs([([x0[j], x1[j], x1[j], x0[j], x0[j]], [px[poc[j]] - .125, px[poc[j]] - .125, px[poc[j]] + .125, px[poc[j]] + .125, px[poc[j]] - .125])
                   for j in range(nb) if live[j]])
    fig.add_trace(go.Scatter(x=xs, y=ys, mode='lines', line=dict(color='#000', width=1.6), name='bar POC', hoverinfo='skip'), row=1, col=1)
    for st, col, nm in ((sst, RED, f'stacked sell imbalance ({a.stack}+)'), (bst, GRN, f'stacked buy imbalance ({a.stack}+)')):
        xs, ys = segs([([x0[j], x1[j], x1[j], x0[j], x0[j]], [px[i] - .125, px[i] - .125, px[k] + .125, px[k] + .125, px[i] - .125]) for j, i, k in st])
        fig.add_trace(go.Scatter(x=xs, y=ys, mode='lines', line=dict(color=col, width=3), name=nm, hoverinfo='skip'), row=1, col=1)
    up = C >= O
    for m, col, nm in ((up, '#1a9641', 'up bar body'), (~up, '#d7191c', 'down bar body')):
        xs, ys = segs([([x0[j] + step * 0.012] * 2, [min(O[j], C[j]) - .125, max(O[j], C[j]) + .125]) for j in range(nb) if m[j] and live[j]])
        fig.add_trace(go.Scatter(x=xs, y=ys, mode='lines', line=dict(color=col, width=4), name=nm, hoverinfo='skip', showlegend=False), row=1, col=1)
    fig.add_trace(go.Scatter(x=ts, y=v1[sl], name='24h vwap', line=dict(color='#1f77b4', width=1.5), hoverinfo='skip'), row=1, col=1)
    fig.add_trace(go.Scatter(x=ts, y=v3[sl], name='72h vwap', line=dict(color='#9467bd', width=1.5, dash='dot'), hoverinfo='skip'), row=1, col=1)
    lv = prior_levels(s)
    if lv:
        for k, (col, dash) in dict(POC=('#ff7f0e', 'solid'), VAH=('#ff7f0e', 'dash'), VAL=('#ff7f0e', 'dash')).items():
            fig.add_trace(go.Scatter(x=[ts[0], ts[-1]], y=[lv[k]] * 2, mode='lines', line=dict(color=col, width=1.3, dash=dash), name=f'prior day {k} {lv[k]:.2f}', hoverinfo='name'), row=1, col=1)
    # ---- the zone engine's triggers, hidden until clicked in the legend ----
    zz = Z.filter(pl.col('session') == sd).sort('t')
    for side, sym, col in (('L', 'triangle-up', '#0050d0'), ('S', 'triangle-down', '#a000a0')):
        v = [(D['ts'][t0 + r['t'] + 1], D['vwap'][t0 + r['t'] + 1] - D['adj'][t0 + r['t'] + 1],
              f"{'long' if side == 'L' else 'short'} {str(D['ts'][t0 + r['t'] + 1])[11:16]}  zratio {r['zratio']:.2f}{' HEAVY' if r['zratio'] >= zq else ''}<br>"
              f"f10 {r['f10']:+.1f}  f30 {r['f30']:+.1f}  f60 {r['f60']:+.1f} bps   1R bracket {r['b1_R']:+.2f}")
             for r in zz.filter(pl.col('side') == side).iter_rows(named=True) if a.m0 <= r['t'] + 1 < a.m1]
        if v: fig.add_trace(go.Scatter(x=[q[0] for q in v], y=[q[1] for q in v], text=[q[2] for q in v], mode='markers', hovertemplate='%{text}<extra></extra>', visible='legendonly',
                                       name=f"zone-exit {'longs' if side == 'L' else 'shorts'} (N{a.N} K{a.K})", marker=dict(symbol=sym, size=12, color=col, line=dict(color='#000', width=1))), row=1, col=1)
    # ---- the profile ----
    fig.add_trace(go.Bar(y=px, x=SV.sum(axis=1), orientation='h', marker_color='#e08080', name='profile sells', width=0.25, showlegend=False,
                         hovertemplate='%{y:.2f} sold %{x}<extra></extra>'), row=1, col=2)
    fig.add_trace(go.Bar(y=px, x=BV.sum(axis=1), orientation='h', marker_color='#80c080', name='profile buys', width=0.25, showlegend=False,
                         hovertemplate='%{y:.2f} bought %{x}<extra></extra>'), row=1, col=2)
    # ---- the per-bar table ----
    vol = T.sum(axis=0); bd = dlt.sum(axis=0); pct = np.where(vol > 0, 100.0 * bd / np.maximum(vol, 1), 0.0)
    k0 = (L.RTH0 - a.m0) // B; cvd = np.cumsum(bd) - (np.cumsum(bd)[k0 - 1] if k0 > 0 else 0)
    def nrm(x): return x / max(np.abs(x).max(), 1e-9)
    rows = ['CVD from 09:30', 'delta %', 'delta', 'volume']                   # one heatmap: a second, single-row one breaks plotly.js
    fig.add_trace(go.Heatmap(x=xc, y=rows, z=np.vstack([nrm(cvd), nrm(pct), nrm(bd), np.zeros(nb)]), zmin=-1, zmax=1, colorscale=pale, showscale=False, xgap=3, ygap=2,
                             text=[[f'{v:+d}' for v in cvd], [f'{v:+.0f}%' for v in pct], [f'{v:+d}' for v in bd], [f'{v}' for v in vol]], texttemplate='%{text}',
                             textfont=dict(family='Arial', color='#111'), hovertemplate='%{x|%H:%M} %{y} %{text}<extra></extra>', name='bar delta'), row=2, col=1)
    # ---- the opening view ----
    j0, j1 = (a.view0 - a.m0) // B, (a.view1 - a.m0) // B; rr = np.nonzero(has[:, j0:j1].any(axis=1))[0]
    ylo, yhi = px[rr[0]] - 0.5, px[rr[-1]] + 0.5
    chk = D['volume'][sl].sum(); chd = D['delta'][sl].sum()
    fig.update_layout(title=dict(text=f'ES {date} footprint, {B}-minute bars: SELL x BUY at each price, shaded by delta  |  bold = diagonal imbalance >= {a.ratio:g}x on {a.minvol}+ lots  |  '
                                      f'coloured frame = {a.stack}+ stacked  |  black frame = bar POC', font=dict(size=14)),
                      barmode='stack', height=1150, template='plotly_white', hovermode='closest', dragmode='pan', legend=dict(orientation='h', y=1.03, x=0),
                      margin=dict(l=60, r=10, t=80, b=30))
    fig.update_xaxes(range=[str(x0[j0]), str(x1[j1 - 1])], col=1); fig.update_yaxes(range=[ylo, yhi], title_text='price', tickformat='.2f', dtick=1, row=1, col=1)
    fig.update_xaxes(showticklabels=False, row=1, col=2); fig.update_yaxes(fixedrange=True, row=2, col=1)
    out = f'data/charts/escvd/footprint_{date}_{B}m.html'
    fig.write_html(out, config={'scrollZoom': True}, post_script=post, include_plotlyjs='cdn')
    print(f'{out}: {int(has.sum()):,} cells, {int(bi.sum())} buy / {int(si.sum())} sell imbalances, {len(bst)} / {len(sst)} stacks; '
          f'tape volume {int(vol.sum()):,} delta {int(bd.sum()):+,} vs 1s bars {int(chk):,} {int(chd):+,}', flush=True)

    if a.png:
        import matplotlib; matplotlib.use('Agg')
        import matplotlib.pyplot as plt
        from matplotlib.colors import LinearSegmentedColormap
        cm = LinearSegmentedColormap.from_list('pale', ['#f29a9a', '#ffffff', '#93d893'])
        r0, r1 = rr[0], rr[-1]; ncol = j1 - j0
        figm, ax = plt.subplots(figsize=(max(2.0 * ncol, 12), max(0.2 * (r1 - r0 + 1), 8)), dpi=110)
        for j in range(j0, j1):
            for i in range(r0, r1 + 1):
                if not has[i, j]: continue
                ax.add_patch(plt.Rectangle((j - j0 + 0.04, i - 0.5), 0.92, 1, color=cm(0.5 + 0.5 * z[i, j] / cap), lw=0))
                ax.text(j - j0 + 0.48, i, f'{SV[i, j]}', ha='right', va='center', fontsize=7.5, color=RED if si[i, j] else '#111', fontweight='bold' if si[i, j] else 'normal')
                ax.text(j - j0 + 0.50, i, 'x', ha='center', va='center', fontsize=6, color='#777')
                ax.text(j - j0 + 0.52, i, f'{BV[i, j]}', ha='left', va='center', fontsize=7.5, color=GRN if bi[i, j] else '#111', fontweight='bold' if bi[i, j] else 'normal')
            if live[j]:
                ax.add_patch(plt.Rectangle((j - j0 + 0.04, poc[j] - 0.5), 0.92, 1, fill=False, ec='k', lw=1.2))
                ax.plot([j - j0 + 0.02] * 2, [(min(O[j], C[j]) - px[0]) * 4 - 0.5, (max(O[j], C[j]) - px[0]) * 4 + 0.5], color='#1a9641' if up[j] else '#d7191c', lw=3)
                ax.text(j - j0 + 0.5, r0 - 1.6, f'd {bd[j]:+d}\nv {vol[j]}', ha='center', va='top', fontsize=8)
        for st, col in ((sst, RED), (bst, GRN)):
            for j, i, k in st:
                if j0 <= j < j1: ax.add_patch(plt.Rectangle((j - j0 + 0.04, i - 0.5), 0.92, k - i + 1, fill=False, ec=col, lw=2.5))
        ax.set_xlim(0, ncol); ax.set_ylim(r0 - 4.5, r1 + 1)
        ax.set_xticks(np.arange(ncol) + 0.5); ax.set_xticklabels([str(x)[11:16] for x in x0[j0:j1]])
        yt = [i for i in range(r0, r1 + 1) if (p0 + i) % 4 == 0]; ax.set_yticks(yt); ax.set_yticklabels([f'{px[i]:.2f}' for i in yt])
        ax.set_title(f'ES {date} footprint, {B}-minute bars, SELL x BUY (bold = diagonal imbalance >= {a.ratio:g}x, coloured frame = stacked, black frame = bar POC)')
        figm.tight_layout(); pn = out.replace('.html', '.png'); figm.savefig(pn); plt.close(figm); print(pn)
