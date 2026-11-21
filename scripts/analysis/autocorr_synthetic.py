"""Synthetic price paths with different return autocorrelation — a visual primer.

Three figures on one page:
  1. AR(1) 30s returns, phi in {-0.5 .. +0.75}, the SAME random shocks and the SAME 1-bar
     volatility in every panel — so the only thing that differs is how a bar's return leans on
     the previous one.
  2. A regime switch: phi = +0.5 for the first hour (120 bars), 0 afterwards — the shape we are
     about to look for on ANT — with the rolling 1h lag-1 autocorrelation under it.
  3. The Working (1960) averaging effect: a pure 1s random walk (phi = 0) aggregated to 30s bars.
     Close-to-close returns have lag-1 ACF ~ 0, VWAP-to-VWAP returns ~ +0.25 — a measurement
     artifact, the baseline any VWAP-bar autocorrelation must be read against.

Usage:  python scripts/analysis/autocorr_synthetic.py [--seed 7] [-o data/autocorr/synthetic.html]
"""
import argparse
import os

import numpy as np
import plotly.graph_objects as go
from plotly.subplots import make_subplots

BARS = 780            # 6.5 h of 30 s bars
SIGMA = 0.002         # 1-bar log-return volatility (0.2 %)
P0 = 10.0


def ar1(eps: np.ndarray, phi: float) -> np.ndarray:
    """r_t = phi r_{t-1} + e_t, innovations scaled so Var(r) = SIGMA^2 for every phi."""
    e = eps * SIGMA * np.sqrt(1 - phi * phi)
    r = np.empty_like(e)
    r[0] = eps[0] * SIGMA
    for t in range(1, len(e)):
        r[t] = phi * r[t - 1] + e[t]
    return r


def acf1(r: np.ndarray) -> float:
    r = r - r.mean()
    return float((r[1:] * r[:-1]).sum() / (r * r).sum())


def variance_ratio(r: np.ndarray, n: int) -> float:
    """Var(n-bar return) / (n Var(1-bar return)); 1 = random walk, >1 trending, <1 mean-reverting."""
    rn = np.convolve(r, np.ones(n), mode='valid')        # overlapping n-bar sums
    return float(rn.var() / (n * r.var()))


def efficiency(r: np.ndarray) -> float:
    """|net move| / path length — Kaufman's efficiency ratio over the whole day."""
    return float(abs(r.sum()) / np.abs(r).sum())


def fig_phi_grid(seed: int) -> go.Figure:
    phis = [-0.5, -0.25, 0.0, 0.25, 0.5, 0.75]
    eps = np.random.default_rng(seed).standard_normal(BARS)
    minutes = np.arange(BARS) * 0.5
    titles, paths = [], []
    for phi in phis:
        r = ar1(eps, phi)
        paths.append(P0 * np.exp(np.concatenate([[0.0], np.cumsum(r)]))[1:])
        titles.append(f"φ = {phi:+.2f}  ·  measured ACF1 {acf1(r):+.2f}  ·  VR(10) {variance_ratio(r, 10):.2f}"
                      f"  ·  ER {efficiency(r):.2f}")
    fig = make_subplots(rows=3, cols=2, subplot_titles=titles, vertical_spacing=0.08, horizontal_spacing=0.05)
    for i, (phi, p) in enumerate(zip(phis, paths)):
        color = '#c0392b' if phi < 0 else ('#555555' if phi == 0 else '#1f6fb2')
        fig.add_trace(go.Scatter(x=minutes, y=p, mode='lines', line=dict(width=1.2, color=color),
                                 name=f"φ={phi:+.2f}", hovertemplate='min %{x}<br>%{y:.3f}<extra></extra>'),
                      row=i // 2 + 1, col=i % 2 + 1)
    fig.update_layout(height=1050, width=1400, showlegend=False,
                      title=dict(text=f"AR(1) 30 s returns — identical shocks (seed {seed}), identical 1-bar volatility "
                                      f"{SIGMA:.1%}; only φ differs<br><sup>VR(10) = Var(5-min move) / (10 × Var(30 s move)): "
                                      f"1 = random walk, &gt;1 trend, &lt;1 chop.  ER = |open→close| / total distance travelled.</sup>"))
    fig.update_xaxes(title_text='minutes from open', row=3)
    return fig


def fig_regime(seed: int) -> go.Figure:
    hour = 120
    eps = np.random.default_rng(seed + 1).standard_normal(BARS)
    r = np.concatenate([ar1(eps[:hour], 0.5), ar1(eps[hour:], 0.0)])
    p = P0 * np.exp(np.cumsum(r))
    minutes = np.arange(BARS) * 0.5
    roll = np.full(BARS, np.nan)
    for t in range(hour, BARS + 1):
        roll[t - 1] = acf1(r[t - hour:t])
    fig = make_subplots(rows=2, cols=1, shared_xaxes=True, row_heights=[0.7, 0.3], vertical_spacing=0.04)
    fig.add_trace(go.Scatter(x=minutes, y=p, mode='lines', line=dict(width=1.2, color='#1f6fb2')), row=1, col=1)
    fig.add_trace(go.Scatter(x=minutes, y=roll, mode='lines', line=dict(width=1.2, color='#333333')), row=2, col=1)
    fig.add_vrect(x0=0, x1=60, fillcolor='#1f6fb2', opacity=0.08, line_width=0, row=1, col=1)
    fig.add_hline(y=0, line=dict(color='#999999', dash='dot'), row=2, col=1)
    fig.update_yaxes(title_text='price', row=1, col=1)
    fig.update_yaxes(title_text='ACF1, trailing 1 h', row=2, col=1)
    fig.update_xaxes(title_text='minutes from open', row=2, col=1)
    fig.update_layout(height=750, width=1400, showlegend=False,
                      title=dict(text=f"Regime switch: φ = +0.5 in the first hour (shaded), 0 afterwards — "
                                      f"first hour ACF1 {acf1(r[:hour]):+.2f}, rest {acf1(r[hour:]):+.2f}<br>"
                                      f"<sup>The trailing-window estimate needs a full hour to forget the trend; "
                                      f"with 120 bars its noise is about ±{2 / np.sqrt(hour):.2f} (2 s.e.).</sup>"))
    return fig


def fig_working(seed: int) -> tuple[go.Figure, str]:
    """1 s random walk -> 30 s bars, close-to-close vs VWAP-to-VWAP, over many simulated days."""
    rng = np.random.default_rng(seed + 2)
    days, sec = 200, 30
    c_acf, v_acf, v_acf_w = [], [], []
    for _ in range(days):
        x = np.cumsum(rng.standard_normal(BARS * sec)) * (SIGMA / np.sqrt(sec))
        bars = x.reshape(BARS, sec)
        close = bars[:, -1]
        vwap_eq = bars.mean(axis=1)                                          # equal volume each second
        w = rng.lognormal(0, 1.5, size=bars.shape)                           # lumpy volume, like a real tape
        vwap_w = (bars * w).sum(axis=1) / w.sum(axis=1)
        c_acf.append(acf1(np.diff(close)))
        v_acf.append(acf1(np.diff(vwap_eq)))
        v_acf_w.append(acf1(np.diff(vwap_w)))
    fig = go.Figure()
    for name, vals, color in [('close → close', c_acf, '#555555'),
                              ('VWAP → VWAP, even volume', v_acf, '#1f6fb2'),
                              ('VWAP → VWAP, lumpy volume', v_acf_w, '#8e44ad')]:
        fig.add_trace(go.Histogram(x=vals, name=f"{name}  (mean {np.mean(vals):+.3f})", opacity=0.6,
                                   marker_color=color, xbins=dict(size=0.02)))
    m = sec
    theory = (m * m - 1) / (2 * (2 * m * m + 1))
    fig.add_vline(x=theory, line=dict(color='#1f6fb2', dash='dash'),
                  annotation_text=f"Working's formula {theory:.3f}")
    fig.update_layout(barmode='overlay', height=500, width=1400,
                      xaxis_title='lag-1 autocorrelation of 30 s returns (one simulated day each)',
                      title=dict(text=f"No trend at all (φ = 0), {days} simulated days — averaging prices inside a bar "
                                      f"MANUFACTURES positive autocorrelation<br><sup>Any ACF measured on VWAP bars must be "
                                      f"compared with ~+0.25, not with 0. Close-to-close has the opposite bias on a real "
                                      f"tape (bid-ask bounce pushes it negative).</sup>"))
    summary = (f"close {np.mean(c_acf):+.3f}, vwap(even) {np.mean(v_acf):+.3f}, "
               f"vwap(lumpy) {np.mean(v_acf_w):+.3f}, theory {theory:.3f}")
    return fig, summary


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--seed', type=int, default=7)
    ap.add_argument('-o', '--output', default='data/autocorr/synthetic.html')
    a = ap.parse_args()

    os.makedirs(os.path.dirname(a.output), exist_ok=True)
    fw, summary = fig_working(a.seed)
    figs = [fig_phi_grid(a.seed), fig_regime(a.seed), fw]
    viz = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'visualization', 'chart_controls.js')
    with open(viz) as f:
        post_script = f.read()
    config = {'scrollZoom': True, 'displayModeBar': True}
    parts = [fig.to_html(full_html=False, include_plotlyjs='cdn' if i == 0 else False, config=config,
                         post_script=post_script)
             for i, fig in enumerate(figs)]
    with open(a.output, 'w') as f:
        f.write('<html><head><meta charset="utf-8"><title>Autocorrelation primer</title></head><body>\n')
        f.write('\n<hr>\n'.join(parts))
        f.write('\n</body></html>\n')
    print(f"Working check: {summary}")
    print(f"Saved to {a.output}")


if __name__ == '__main__':
    main()
