"""Explore the volume-scaled Gaussian emission used by TradingEdge.Hmm.

Given `sigma` and `drift_frac` (so drift mu = drift_frac * sigma), plot:
  (a) log-density of Up/Consol/Down at a small volume (v_small)
  (b) log-density of Up/Consol/Down at a large volume (v_large)
  (c) log-likelihood ratio Up/Consol and Down/Consol vs dlogp at v_small
  (d) empirical (dlogp, volume) scatter from an HMM CSV with the
      Consol/Up and Consol/Down decision boundaries overlaid.

The goal is to see, before running forward-backward, how strongly the
emission commits to each state across the (dlogp, v) plane — and where the
real LW data falls in that plane.
"""

import argparse
import csv
import os
from dataclasses import dataclass

import numpy as np
import plotly.graph_objects as go
from plotly.subplots import make_subplots


@dataclass
class Params:
    sigma: float
    drift_frac: float

    @property
    def mu(self) -> float:
        return self.drift_frac * self.sigma


def log_normal(x, mean, var):
    """log N(x; mean, var), elementwise over x."""
    return -0.5 * (np.log(2.0 * np.pi * var) + (x - mean) ** 2 / var)


def state_log_density(dlogp, v, mu_s, sigma):
    """log p(Δlp | state with drift mu_s, shared sigma, volume v)."""
    mean = mu_s * v
    var = sigma * sigma * v
    return log_normal(dlogp, mean, var)


def log_ratio_dir_vs_consol(dlogp, v, mu, sigma, direction):
    """log P(Δlp | direction) - log P(Δlp | Consol). Closed form with shared σ:
         log P_dir - log P_con = direction * μ * Δlp / σ² - μ² v / (2 σ²)
    where direction = +1 for Up, -1 for Down. Positive means direction wins."""
    s2 = sigma * sigma
    return direction * mu * dlogp / s2 - mu * mu * v / (2.0 * s2)


def crossover_dlogp(v, mu, direction):
    """dlogp where log P_dir == log P_consol. Solving the closed form above:
       Δlp_cross = direction * μ v / 2. Positive direction → Up wins for
       Δlp > crossover; negative direction → Down wins for Δlp < crossover."""
    return direction * mu * v / 2.0


def load_empirical(path):
    """Read dlogp, volume from an HMM inference CSV."""
    dlp = []
    vol = []
    with open(path) as f:
        reader = csv.DictReader(f)
        for r in reader:
            dlp.append(float(r["dlogp"]))
            vol.append(float(r["volume"]))
    return np.asarray(dlp), np.asarray(vol)


def panel_density(fig, row, col, params, v, dlogp_range, title_suffix):
    """log-density of each state's emission at a fixed volume."""
    mu = params.mu
    dlogps = np.linspace(-dlogp_range, dlogp_range, 1001)
    up = state_log_density(dlogps, v, mu, params.sigma)
    con = state_log_density(dlogps, v, 0.0, params.sigma)
    dn = state_log_density(dlogps, v, -mu, params.sigma)
    fig.add_trace(
        go.Scatter(x=dlogps, y=up, mode="lines", name=f"Up (v={v:g})",
                   line=dict(color="green"), legendgroup=f"v{v}"),
        row=row, col=col,
    )
    fig.add_trace(
        go.Scatter(x=dlogps, y=con, mode="lines", name=f"Consol (v={v:g})",
                   line=dict(color="gray"), legendgroup=f"v{v}"),
        row=row, col=col,
    )
    fig.add_trace(
        go.Scatter(x=dlogps, y=dn, mode="lines", name=f"Down (v={v:g})",
                   line=dict(color="crimson"), legendgroup=f"v{v}"),
        row=row, col=col,
    )
    # Mark the two crossover points.
    for dir_ in (+1, -1):
        x_cross = crossover_dlogp(v, mu, dir_)
        fig.add_vline(x=x_cross, line_width=1, line_dash="dot",
                      line_color="black", row=row, col=col)


def panel_log_ratio(fig, row, col, params, v, dlogp_range):
    """log-LR of Up/Down vs Consol at a fixed volume."""
    dlogps = np.linspace(-dlogp_range, dlogp_range, 1001)
    up_lr = log_ratio_dir_vs_consol(dlogps, v, params.mu, params.sigma, +1)
    dn_lr = log_ratio_dir_vs_consol(dlogps, v, params.mu, params.sigma, -1)
    fig.add_trace(
        go.Scatter(x=dlogps, y=up_lr, mode="lines", name="log(Up / Consol)",
                   line=dict(color="green")),
        row=row, col=col,
    )
    fig.add_trace(
        go.Scatter(x=dlogps, y=dn_lr, mode="lines", name="log(Down / Consol)",
                   line=dict(color="crimson")),
        row=row, col=col,
    )
    fig.add_hline(y=0.0, line_width=1, line_dash="dash", line_color="black",
                  row=row, col=col)


def panel_empirical(fig, row, col, params, dlogp, volume, dlogp_range):
    """Empirical (dlogp, volume) points with decision boundaries overlaid."""
    # Cap volume for plotting — a few huge prints blow up the y-axis scale.
    vol_cap = float(np.quantile(volume, 0.995))
    mask = (np.abs(dlogp) < dlogp_range) & (volume < vol_cap)
    # Subsample to keep the HTML light.
    idx = np.where(mask)[0]
    if len(idx) > 20000:
        rng = np.random.default_rng(0)
        idx = rng.choice(idx, size=20000, replace=False)
    fig.add_trace(
        go.Scattergl(
            x=dlogp[idx],
            y=volume[idx],
            mode="markers",
            marker=dict(size=2, color="steelblue", opacity=0.4),
            name="empirical trades",
            showlegend=False,
        ),
        row=row, col=col,
    )
    # Decision boundaries: Consol vs Up crossover is dlogp = +μv/2.
    # Consol vs Down crossover is dlogp = -μv/2.
    # Plotted over the visible volume range so you can see the wedge.
    v_grid = np.linspace(1.0, vol_cap, 200)
    x_up = crossover_dlogp(v_grid, params.mu, +1)
    x_dn = crossover_dlogp(v_grid, params.mu, -1)
    fig.add_trace(
        go.Scatter(x=x_up, y=v_grid, mode="lines", name="Up vs Consol boundary",
                   line=dict(color="green", width=2)),
        row=row, col=col,
    )
    fig.add_trace(
        go.Scatter(x=x_dn, y=v_grid, mode="lines", name="Down vs Consol boundary",
                   line=dict(color="crimson", width=2)),
        row=row, col=col,
    )


def summarize(params, v_small, v_large):
    """Text summary of derived quantities, printed to stdout."""
    sigma = params.sigma
    mu = params.mu
    print("")
    print(f"sigma      = {sigma:.3e}")
    print(f"drift_frac = {params.drift_frac}")
    print(f"mu         = {mu:.3e}")
    print("")
    for v in (v_small, v_large):
        sig_trade = sigma * np.sqrt(v)
        mu_trade = mu * v
        cross = mu * v / 2.0
        penalty_at_zero = -params.drift_frac ** 2 * v / 2.0
        fight_at_mean = params.drift_frac ** 2 * v / 2.0
        print(f"at v = {v:g}:")
        print(f"  σ√v                 = {sig_trade:.3e}   (Consol std)")
        print(f"  μv                  = {mu_trade:.3e}   (directional mean)")
        print(f"  μv / (σ√v)          = {mu_trade / sig_trade:.3f}σ  (how far the state mean is)")
        print(f"  crossover |Δlp|     = {cross:.3e}")
        print(f"  log-LR at Δlp=0     = {penalty_at_zero:.2f}  (how strongly Consol wins at x=0)")
        print(f"  log-LR at Δlp=μv    = {fight_at_mean:.2f}  (how strongly Up wins at its mean)")
        print("")


def main():
    ap = argparse.ArgumentParser(
        description="Explore the volume-scaled Gaussian emission of the HMM"
    )
    ap.add_argument("-s", "--sigma", type=float, default=1.0e-4)
    ap.add_argument("-a", "--drift-frac", type=float, default=1.0,
                    help="μ = drift_frac * sigma")
    ap.add_argument("-v", "--v-small", type=float, default=100.0,
                    help="Volume for the 'small trade' panels (default 100 shares)")
    ap.add_argument("-V", "--v-large", type=float, default=5000.0,
                    help="Volume for the 'large trade' panel (default 5000 shares)")
    ap.add_argument("-r", "--dlogp-range", type=float, default=5.0e-3,
                    help="x-range for density/log-ratio panels (default ±5e-3)")
    ap.add_argument("--csv", help="Optional HMM inference CSV for empirical overlay")
    ap.add_argument("-o", "--output", default="logs/hmm_likelihood_explorer.html")
    args = ap.parse_args()

    params = Params(sigma=args.sigma, drift_frac=args.drift_frac)
    summarize(params, args.v_small, args.v_large)

    fig = make_subplots(
        rows=2, cols=2,
        shared_xaxes=False,
        subplot_titles=[
            f"log-density at v={args.v_small:g}",
            f"log-density at v={args.v_large:g}",
            f"log-LR (Up/Down vs Consol) at v={args.v_small:g}",
            "Empirical (Δlp, v) with decision boundaries",
        ],
        vertical_spacing=0.08,
        horizontal_spacing=0.08,
    )

    panel_density(fig, 1, 1, params, args.v_small, args.dlogp_range, "small v")
    panel_density(fig, 1, 2, params, args.v_large, args.dlogp_range, "large v")
    panel_log_ratio(fig, 2, 1, params, args.v_small, args.dlogp_range)

    if args.csv:
        dlogp, volume = load_empirical(args.csv)
        panel_empirical(fig, 2, 2, params, dlogp, volume, args.dlogp_range)
    else:
        fig.add_annotation(
            text="(pass --csv PATH for empirical overlay)",
            xref="x4", yref="y4", x=0, y=0.5, showarrow=False,
            row=2, col=2,
        )

    fig.update_layout(
        title=(f"HMM emission: σ={params.sigma:.3e}, driftFrac={params.drift_frac:g}, "
               f"μ={params.mu:.3e}"),
        height=900, width=1600,
        showlegend=True,
        legend=dict(orientation="h", yanchor="bottom", y=1.04),
        hovermode="closest",
    )
    for r, c in ((1, 1), (1, 2), (2, 1)):
        fig.update_xaxes(title_text="Δlog p", row=r, col=c)
    fig.update_yaxes(title_text="log-density", row=1, col=1)
    fig.update_yaxes(title_text="log-density", row=1, col=2)
    fig.update_yaxes(title_text="log-LR (nats)", row=2, col=1)
    fig.update_xaxes(title_text="Δlog p", row=2, col=2)
    fig.update_yaxes(title_text="volume", row=2, col=2)

    os.makedirs(os.path.dirname(args.output) or ".", exist_ok=True)
    fig.write_html(args.output, config={"scrollZoom": True, "displayModeBar": True})
    print(f"Saved to {args.output}")


if __name__ == "__main__":
    main()
