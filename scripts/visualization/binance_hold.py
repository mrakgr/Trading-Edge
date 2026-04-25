"""Binance hold-detector visualization.

Reads the bar+posterior CSV emitted by `TradingEdge.Hmm inferhold` and
plots:
  1. VWAP ±2σ per bar
  2. Signed flow (buy − sell volume), derived from k_buys / n_trades
  3. Stacked posterior P(Hold/Fakeout/Trend)
  4. Time per bar (seconds)

Unlike `binance_volume.py`, this script does NOT rebuild bars from the raw
trade CSV — it reads the F#-emitted CSV directly so bar boundaries stay
aligned with the model's view.
"""

import argparse
import os
from datetime import datetime, timezone

import numpy as np
import pandas as pd
import plotly.graph_objects as go
from plotly.subplots import make_subplots


def us_to_datetime(us):
    return datetime.fromtimestamp(us / 1e6, timezone.utc)


def fmt(us):
    return us_to_datetime(us).strftime("%H:%M:%S.%f")[:-3]


def plot(df, output_html, mode, title):
    bar_size = float(df["volume"].median())
    p_hold = df[f"p_hold{'_filt' if mode == 'filtered' else ''}"].to_numpy()
    p_fake = df[f"p_fakeout{'_filt' if mode == 'filtered' else ''}"].to_numpy()
    p_trend = df[f"p_trend{'_filt' if mode == 'filtered' else ''}"].to_numpy()

    vwap = df["vwap"].to_numpy()
    stddev = df["stddev"].to_numpy()
    upper = vwap + 2.0 * stddev
    lower = vwap - 2.0 * stddev
    durations = (df["end_us"].to_numpy() - df["start_us"].to_numpy()) / 1e6
    n_trades = df["n_trades"].to_numpy()
    k_buys = df["k_buys"].to_numpy()
    # Approximate signed flow: assume buy/sell volume splits in proportion to
    # buy/sell trade count. Exact would require carrying buy_volume from F#,
    # but trade-count proxy is what the Binomial emission consumes anyway.
    buy_frac = np.where(n_trades > 0, k_buys / np.maximum(n_trades, 1), 0.5)
    signed_vol = (2.0 * buy_frac - 1.0) * df["volume"].to_numpy()

    volumes = df["volume"].to_numpy()
    cum_volume = np.cumsum(volumes)
    # Use bar index as the x-axis. All bars are uniform volume (18 BTC by
    # default), so index is equivalent to cumulative volume / bar size — but
    # it lets Plotly render adjacent bars as integer-spaced columns of width
    # 1.0, which avoids the pixel-rounding gaps/overlaps that show up with
    # fractional widths on a continuous axis.
    x_vals = np.arange(len(df), dtype=np.float64)

    fig = make_subplots(
        rows=4, cols=1, shared_xaxes=True, vertical_spacing=0.04,
        row_heights=[0.40, 0.18, 0.22, 0.20],
        subplot_titles=[
            f"VWAP ±2σ ({bar_size:g} BTC/bar)",
            "Signed flow per bar (buy − sell, BTC)",
            f"Hold detector posterior ({mode})",
            "Time per bar (seconds)",
        ],
    )

    session_vwap = float((vwap * df["volume"]).sum() / df["volume"].sum())
    min_height = min(0.01, 0.001 * session_vwap)
    heights = np.maximum(upper - lower, min_height)
    bases = vwap - heights / 2

    colors = []
    for i in range(len(df)):
        if i == 0 or vwap[i] >= vwap[i - 1]:
            colors.append("green")
        else:
            colors.append("red")

    customdata = np.column_stack([
        cum_volume, vwap, stddev, upper, lower,
        [fmt(t) for t in df["start_us"]],
        [fmt(t) for t in df["end_us"]],
        durations, n_trades, k_buys, buy_frac,
        100.0 * p_hold, 100.0 * p_fake, 100.0 * p_trend,
    ])

    fig.add_trace(
        go.Bar(
            x=x_vals, y=heights, base=bases, name="VWAP ±2σ",
            marker_color=colors, marker_line_width=0,
            width=1.0,
            customdata=customdata,
            hovertemplate=(
                "<b>Cum Vol:</b> %{customdata[0]:,.2f}<br>"
                "<b>VWAP:</b> %{customdata[1]:,.2f}<br>"
                "<b>StdDev:</b> %{customdata[2]:.4f}<br>"
                "<b>+2σ:</b> %{customdata[3]:,.2f}<br>"
                "<b>-2σ:</b> %{customdata[4]:,.2f}<br>"
                "<b>Start:</b> %{customdata[5]}<br>"
                "<b>End:</b> %{customdata[6]}<br>"
                "<b>Duration:</b> %{customdata[7]:.3f}s<br>"
                "<b>Trades:</b> %{customdata[8]}<br>"
                "<b>Buy trades:</b> %{customdata[9]}<br>"
                "<b>Buy frac:</b> %{customdata[10]:.3f}<br>"
                "<b>P(Hold):</b> %{customdata[11]:.2f}%<br>"
                "<b>P(Fakeout):</b> %{customdata[12]:.2f}%<br>"
                "<b>P(Trend):</b> %{customdata[13]:.2f}%<br>"
                "<extra></extra>"
            ),
        ),
        row=1, col=1,
    )
    fig.add_trace(
        go.Scatter(
            x=x_vals, y=vwap, mode="lines", name="VWAP",
            line=dict(color="blue", width=1), hoverinfo="skip",
        ),
        row=1, col=1,
    )

    sf_colors = ["green" if s >= 0 else "crimson" for s in signed_vol]
    fig.add_trace(
        go.Bar(
            x=x_vals, y=signed_vol, name="Signed flow",
            marker_color=sf_colors, marker_line_width=0,
            width=1.0,
            hovertemplate="Signed vol: %{y:+.3f}<extra></extra>",
        ),
        row=2, col=1,
    )
    fig.add_hline(y=0.0, line_width=1, line_dash="dash", line_color="black", row=2, col=1)

    # Stacked bars per bar — bottom-to-top is Trend, Fakeout, Hold so Hold
    # sits at the top where single-bar spikes are easiest to spot. We can't
    # use barmode="stack" globally because the VWAP / signed-flow rows are
    # also Bars and aren't supposed to stack — so we stack manually via base.
    bar_widths = 1.0
    base_trend = np.zeros_like(p_trend)
    base_fake = p_trend
    base_hold = p_trend + p_fake
    fig.add_trace(
        go.Bar(
            x=x_vals, y=p_trend, base=base_trend, name="P(Trend)",
            marker_color="rgba(60,160,60,0.85)", marker_line_width=0,
            width=bar_widths,
            hovertemplate="P(Trend): %{y:.3f}<extra></extra>",
        ),
        row=3, col=1,
    )
    fig.add_trace(
        go.Bar(
            x=x_vals, y=p_fake, base=base_fake, name="P(Fakeout)",
            marker_color="rgba(160,160,160,0.75)", marker_line_width=0,
            width=bar_widths,
            hovertemplate="P(Fakeout): %{y:.3f}<extra></extra>",
        ),
        row=3, col=1,
    )
    fig.add_trace(
        go.Bar(
            x=x_vals, y=p_hold, base=base_hold, name="P(Hold)",
            marker_color="rgba(220,60,60,0.95)", marker_line_width=0,
            width=bar_widths,
            hovertemplate="P(Hold): %{y:.3f}<extra></extra>",
        ),
        row=3, col=1,
    )

    fig.add_trace(
        go.Scatter(
            x=x_vals, y=durations, fill="tozeroy", mode="lines",
            name="Time Duration", line=dict(color="blue", width=1),
            fillcolor="rgba(0, 0, 255, 0.3)",
            hovertemplate="Cum Vol: %{x:,.2f}<br>Duration: %{y:.3f}s<extra></extra>",
        ),
        row=4, col=1,
    )

    fig.update_layout(
        title=title, height=1000, width=1600,
        hovermode="closest", hoverdistance=50, showlegend=False,
        # "overlay" tells Plotly to draw each Bar at its literal x. Without
        # this, default "group" mode shifts same-x bars sideways within the
        # slot, which breaks the manual stacking on the posterior panel.
        barmode="overlay",
    )
    fig.update_xaxes(rangeslider_visible=False, row=1, col=1)
    fig.update_xaxes(title_text=f"Bar index ({bar_size:g} BTC each)", row=4, col=1)
    fig.update_yaxes(title_text="Price", row=1, col=1)
    fig.update_yaxes(title_text="Signed vol", row=2, col=1)
    fig.update_yaxes(title_text="P(state)", range=[0, 1], row=3, col=1)
    fig.update_yaxes(title_text="Seconds", autorange="reversed", row=4, col=1)

    config = {"scrollZoom": True, "displayModeBar": True}
    script_dir = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(script_dir, "chart_controls.js")) as f:
        post_script = f.read()
    fig.write_html(output_html, config=config, post_script=post_script)
    print(f"Saved to {output_html}")


def main():
    ap = argparse.ArgumentParser(description="Binance hold-detector chart")
    ap.add_argument("hold_csv", help="Output of `inferhold` (bar+posterior CSV)")
    ap.add_argument("-o", "--output", help="Output HTML path")
    ap.add_argument("--mode", choices=["filtered", "smoothed"], default="filtered",
                    help="Which posterior to render (default: filtered)")
    args = ap.parse_args()

    base = os.path.splitext(os.path.basename(args.hold_csv))[0]
    out = args.output or f"logs/{base}_{args.mode}.html"
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)

    print(f"Loading {args.hold_csv} ...")
    df = pd.read_csv(args.hold_csv)
    print(f"Loaded {len(df):,} bars")
    title = f"BTCUSDT hold detector — {base} — {args.mode}"
    plot(df, out, args.mode, title)


if __name__ == "__main__":
    main()
