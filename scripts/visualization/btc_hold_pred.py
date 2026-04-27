"""BTC volume-bar chart with hold-detector posterior overlay.

Reads:
  - BTC bars parquet (raw schema from `dotnet ... btc-bars`):
      day_id, bar_idx, rel_stddev, ret, duration_sec, trade_count, is_hold
    Note: there's no VWAP column in this schema. We approximate per-bar price
    by integrating the log-returns: price_i = exp(cumsum(ret)) * start_price.
    Without a known starting price we just plot the rel-price curve normalized
    to start at 1.0; the y-axis is unitless but the relative shape matches the
    real BTC tape.
  - Predictions parquet (from predict_hold.py):
      day_id, bar_idx, hold_prob

Plots:
  Top:  rel-price line, with bar widths = rel_stddev (visualizing the hold
        signal). Bars colored on a green-to-red gradient by hold_prob, so
        high-prob clusters jump out visually.
  Bot:  hold_prob over the same x-axis (bar index).

Usage:
    python scripts/visualization/btc_hold_pred.py \
        --bars data/btc_bars/2026-02-05.parquet \
        --pred data/btc_hold_pred/2026-02-05.parquet \
        --output data/charts/btc_hold/2026-02-05.html
"""

from __future__ import annotations

import argparse
import os

import numpy as np
import pyarrow.parquet as pq
import plotly.graph_objects as go
from plotly.subplots import make_subplots


def load_bars(path: str):
    t = pq.read_table(path).to_pandas()
    return t


def load_pred(path: str):
    t = pq.read_table(path).to_pandas()
    return t


def viridis_color(p: float) -> str:
    """Map p in [0, 1] to a green-low / red-high RGB string."""
    p = max(0.0, min(1.0, float(p)))
    # Lerp green -> yellow -> red.
    if p < 0.5:
        # green to yellow
        t = p / 0.5
        r, g, b = int(t * 255), 200, 0
    else:
        # yellow to red
        t = (p - 0.5) / 0.5
        r, g, b = 255, int(200 * (1 - t)), 0
    return f"rgb({r},{g},{b})"


def plot(bars, pred, output_html, title: str):
    # Align by bar_idx (bars + pred should already share day_id). Use bar_idx
    # as the x-axis directly — uniform spacing keeps adjacent bars rendering
    # as integer columns.
    df = bars.merge(pred[["bar_idx", "hold_prob"]], on="bar_idx", how="left")
    n = len(df)
    print(f"Bars: {n}, predictions matched: {df['hold_prob'].notna().sum()}")

    # Approximate price curve from log-returns. Start at 1.0 (relative).
    log_rets = df["ret"].to_numpy()
    log_rets[0] = 0.0  # first bar's ret is 0 by convention
    rel_price = np.exp(np.cumsum(log_rets))

    rel_stddev = df["rel_stddev"].to_numpy()
    # Bar height is ±2σ around the price, where σ here is rel_stddev * price.
    bar_half = 2.0 * rel_stddev * rel_price
    bar_height = 2.0 * bar_half
    bar_base = rel_price - bar_half

    probs = df["hold_prob"].fillna(0.0).to_numpy()
    colors = [viridis_color(p) for p in probs]

    x_vals = np.arange(n, dtype=np.float64)

    customdata = np.column_stack([
        df["bar_idx"].to_numpy(),
        rel_price,
        rel_stddev,
        df["ret"].to_numpy(),
        df["duration_sec"].to_numpy(),
        df["trade_count"].to_numpy(),
        probs,
    ])

    fig = make_subplots(
        rows=2, cols=1,
        shared_xaxes=True,
        vertical_spacing=0.05,
        row_heights=[0.7, 0.3],
        subplot_titles=["Price (log-cum) ±2σ — color = hold_prob", "Hold probability"],
    )

    fig.add_trace(go.Bar(
        x=x_vals, y=bar_height, base=bar_base,
        marker_color=colors, marker_line_width=0,
        width=0.9,
        customdata=customdata,
        hovertemplate=(
            "<b>bar:</b> %{customdata[0]}<br>"
            "<b>rel_price:</b> %{customdata[1]:.6f}<br>"
            "<b>rel_stddev:</b> %{customdata[2]:.6f}<br>"
            "<b>log_ret:</b> %{customdata[3]:+.6f}<br>"
            "<b>dur:</b> %{customdata[4]:.2f}s<br>"
            "<b>trades:</b> %{customdata[5]}<br>"
            "<b>hold_prob:</b> %{customdata[6]:.4f}<extra></extra>"
        ),
        name="bars",
    ), row=1, col=1)

    # Price line on top of the bars for orientation.
    fig.add_trace(go.Scatter(
        x=x_vals, y=rel_price, mode="lines",
        line=dict(color="black", width=1),
        name="rel_price", hoverinfo="skip",
    ), row=1, col=1)

    # hold_prob area chart in bottom panel.
    fig.add_trace(go.Scatter(
        x=x_vals, y=probs, mode="lines", fill="tozeroy",
        line=dict(color="firebrick", width=1),
        fillcolor="rgba(178, 34, 34, 0.4)",
        name="hold_prob",
        hovertemplate="bar %{x}: prob=%{y:.4f}<extra></extra>",
    ), row=2, col=1)
    fig.add_hline(y=0.5, line=dict(color="gray", width=1, dash="dash"), row=2, col=1)

    fig.update_layout(
        title=title, height=900, width=1500,
        hovermode="closest", showlegend=False,
    )
    fig.update_xaxes(rangeslider_visible=False, row=1, col=1)
    fig.update_xaxes(title_text="bar_idx", row=2, col=1)
    fig.update_yaxes(title_text="rel_price", row=1, col=1)
    fig.update_yaxes(title_text="P(hold)", range=[0, 1], row=2, col=1)

    config = {"scrollZoom": True, "displayModeBar": True}
    script_dir = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(script_dir, "chart_controls.js")) as f:
        post_script = f.read()

    os.makedirs(os.path.dirname(output_html) or ".", exist_ok=True)
    fig.write_html(output_html, config=config, post_script=post_script)
    print(f"Saved to {output_html}")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--bars", required=True, help="BTC bars parquet (raw schema)")
    ap.add_argument("--pred", required=True, help="Hold predictions parquet")
    ap.add_argument("--output", required=True, help="Output HTML path")
    ap.add_argument("--title", default=None)
    args = ap.parse_args()

    bars = load_bars(args.bars)
    pred = load_pred(args.pred)
    title = args.title or f"BTC hold-detector — {os.path.basename(args.bars)}"
    plot(bars, pred, args.output, title)


if __name__ == "__main__":
    main()
