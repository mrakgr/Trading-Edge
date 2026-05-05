"""Volume-bar chart for crypto perps with VWAP, signed-flow, and OBI overlay.

Reads the CSV produced by `TradingEdge.CryptoLake process-day`:
    bar_idx, start_us, end_us, cumulative_volume, volume, num_trades,
    vwap, stddev, buy_volume, sell_volume, signed_volume,
    obi_top5, obi_top10

Modeled on scripts/visualization/futures_volume.py — same VWAP +/-2 sigma main
pane, same per-bar signed-flow subpanel, same time-duration subpanel — plus a
new OBI subpanel showing top-5 (solid) and top-10 (dashed) lines.

Sign convention (matches Obi.fs):
    OBI = (sum w_i * bid_size_i - sum w_i * ask_size_i) / (sum ...)
    +1 = thick bid -> support below.  -1 = thick ask -> resistance above.
"""

import argparse
import os
from datetime import datetime, timezone

import numpy as np
import pandas as pd
import plotly.graph_objects as go
from plotly.subplots import make_subplots


def load_bars(path):
    df = pd.read_csv(path)
    return df


def fmt(us):
    return datetime.fromtimestamp(us / 1e6, timezone.utc).strftime("%H:%M:%S.%f")[:-3]


def plot(df, output_html, volume_per_bar, title):
    fig = make_subplots(
        rows=4, cols=1,
        shared_xaxes=True,
        vertical_spacing=0.04,
        row_heights=[0.45, 0.20, 0.20, 0.15],
        subplot_titles=[
            f"VWAP +/-2 sigma ({volume_per_bar:g} BTC/bar)",
            "Signed flow per bar (buy - sell, BTC)",
            "OBI: solid=top5, dashed=top10  (high=thick bid, low=thick ask)",
            "Time per bar (seconds)",
        ],
    )

    x_vals = df["cumulative_volume"].to_numpy()
    vwap_vals = df["vwap"].to_numpy()
    stddev_vals = df["stddev"].to_numpy()
    upper = vwap_vals + 2 * stddev_vals
    lower = vwap_vals - 2 * stddev_vals
    durations_s = (df["end_us"].to_numpy() - df["start_us"].to_numpy()) / 1e6
    signed_vol = df["signed_volume"].to_numpy()
    obi5 = df["obi_top5"].to_numpy()
    obi10 = df["obi_top10"].to_numpy()
    volumes = df["volume"].to_numpy()

    raw_heights = upper - lower
    positive = raw_heights[raw_heights > 0]
    median_h = float(np.median(positive)) if len(positive) else 0.0
    min_height = 0.25 * median_h if median_h > 0 else 0.0
    heights = np.maximum(raw_heights, min_height)
    bases = vwap_vals - heights / 2

    colors = []
    for i, v in enumerate(vwap_vals):
        if i == 0 or v >= vwap_vals[i - 1]:
            colors.append("green")
        else:
            colors.append("red")

    customdata = [
        [
            df["cumulative_volume"].iloc[i],
            df["vwap"].iloc[i],
            df["stddev"].iloc[i],
            df["vwap"].iloc[i] + 2 * df["stddev"].iloc[i],
            df["vwap"].iloc[i] - 2 * df["stddev"].iloc[i],
            fmt(int(df["start_us"].iloc[i])),
            fmt(int(df["end_us"].iloc[i])),
            durations_s[i],
            int(df["num_trades"].iloc[i]),
            df["buy_volume"].iloc[i],
            df["sell_volume"].iloc[i],
            df["signed_volume"].iloc[i],
            obi5[i],
            obi10[i],
        ]
        for i in range(len(df))
    ]

    fig.add_trace(
        go.Bar(
            x=x_vals,
            y=heights,
            base=bases,
            name="VWAP +/-2 sigma",
            marker_color=colors,
            marker_line_width=0,
            width=volumes * 0.8,
            customdata=customdata,
            hovertemplate=(
                "<b>Cum Vol:</b> %{customdata[0]:,.4f}<br>"
                "<b>VWAP:</b> %{customdata[1]:,.2f}<br>"
                "<b>StdDev:</b> %{customdata[2]:.4f}<br>"
                "<b>+2 sigma:</b> %{customdata[3]:,.2f}<br>"
                "<b>-2 sigma:</b> %{customdata[4]:,.2f}<br>"
                "<b>Start:</b> %{customdata[5]} UTC<br>"
                "<b>End:</b> %{customdata[6]} UTC<br>"
                "<b>Duration:</b> %{customdata[7]:.3f}s<br>"
                "<b>Trades:</b> %{customdata[8]}<br>"
                "<b>Buy vol:</b> %{customdata[9]:.4f}<br>"
                "<b>Sell vol:</b> %{customdata[10]:.4f}<br>"
                "<b>Signed vol:</b> %{customdata[11]:+.4f}<br>"
                "<b>OBI top5:</b> %{customdata[12]:+.3f}<br>"
                "<b>OBI top10:</b> %{customdata[13]:+.3f}<br>"
                "<extra></extra>"
            ),
        ),
        row=1, col=1,
    )
    fig.add_trace(
        go.Scatter(x=x_vals, y=vwap_vals, mode="lines", name="VWAP",
                   line=dict(color="blue", width=1), hoverinfo="skip"),
        row=1, col=1,
    )

    sf_colors = ["green" if s >= 0 else "crimson" for s in signed_vol]
    fig.add_trace(
        go.Bar(
            x=x_vals, y=signed_vol, name="Signed flow",
            marker_color=sf_colors, marker_line_width=0,
            width=volumes * 0.8,
            hovertemplate="Signed vol: %{y:+.4f}<extra></extra>",
        ),
        row=2, col=1,
    )
    fig.add_hline(y=0.0, line_width=1, line_dash="dash", line_color="black", row=2, col=1)

    fig.add_trace(
        go.Scatter(x=x_vals, y=obi5, mode="lines", name="OBI top5",
                   line=dict(color="purple", width=1),
                   hovertemplate="OBI top5: %{y:+.3f}<extra></extra>"),
        row=3, col=1,
    )
    fig.add_trace(
        go.Scatter(x=x_vals, y=obi10, mode="lines", name="OBI top10",
                   line=dict(color="purple", width=1, dash="dash"),
                   hovertemplate="OBI top10: %{y:+.3f}<extra></extra>"),
        row=3, col=1,
    )
    fig.add_hline(y=0.0, line_width=1, line_dash="dot", line_color="grey", row=3, col=1)

    fig.add_trace(
        go.Scatter(x=x_vals, y=durations_s, fill="tozeroy", mode="lines",
                   name="Time Duration",
                   line=dict(color="blue", width=1),
                   fillcolor="rgba(0, 0, 255, 0.3)",
                   hovertemplate="Cum Vol: %{x:,.4f}<br>Duration: %{y:.3f}s<extra></extra>"),
        row=4, col=1,
    )

    fig.update_layout(
        title=title,
        height=1100, width=1600,
        hovermode="closest", hoverdistance=50,
        showlegend=False,
    )
    fig.update_xaxes(rangeslider_visible=False, row=1, col=1)
    fig.update_xaxes(title_text="Cumulative Volume (BTC)", row=4, col=1)
    fig.update_yaxes(title_text="Price", row=1, col=1)
    fig.update_yaxes(title_text="Signed vol", row=2, col=1)
    fig.update_yaxes(title_text="OBI", range=[-1, 1], row=3, col=1)
    fig.update_yaxes(title_text="Seconds", autorange="reversed", row=4, col=1)

    config = {"scrollZoom": True, "displayModeBar": True}
    script_dir = os.path.dirname(os.path.abspath(__file__))
    chart_controls = os.path.join(
        os.path.dirname(script_dir), "visualization", "chart_controls.js"
    )
    with open(chart_controls) as f:
        post_script = f.read()
    fig.write_html(output_html, config=config, post_script=post_script)
    print(f"Saved to {output_html}")


def main():
    ap = argparse.ArgumentParser(description="Crypto-perps volume-bar chart with OBI overlay")
    ap.add_argument("csv", help="CSV from `TradingEdge.CryptoLake process-day`")
    ap.add_argument("-v", "--volume-per-bar", type=float, required=True,
                    help="Volume per bar (BTC). Used in subplot title only — must match the value passed to process-day.")
    ap.add_argument("-o", "--output", help="Output HTML path")
    args = ap.parse_args()

    base = os.path.splitext(os.path.basename(args.csv))[0]
    out = args.output or f"logs/{base}.html"
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)

    print(f"Loading {args.csv}...")
    df = load_bars(args.csv)
    print(f"Loaded {len(df):,} bars")
    title = f"{base} - {args.volume_per_bar:g} BTC/bar"
    plot(df, out, args.volume_per_bar, title)


if __name__ == "__main__":
    main()
