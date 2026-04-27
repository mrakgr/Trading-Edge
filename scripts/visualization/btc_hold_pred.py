"""BTC volume-bar chart with multi-class hold-detector posterior overlay.

Reads:
  - BTC bars parquet (raw schema from `dotnet ... btc-bars`):
      day_id, bar_idx, rel_stddev, ret, duration_sec, trade_count, label
    Note: there's no VWAP column in this schema. We approximate per-bar price
    by integrating the log-returns: price_i = exp(cumsum(ret)) * start_price.
    Without a known starting price we just plot the rel-price curve normalized
    to start at 1.0; the y-axis is unitless but the relative shape matches the
    real BTC tape.
  - Predictions parquet (from predict_hold.py):
      day_id, bar_idx, pred_label, prob_0, prob_1, ..., prob_<n-1>

Composite hold score = P(Hold|Up) + P(Hold|Down) + P(Fakeout|Up) + P(Fakeout|Down)
— "what fraction of mass does the model put on the consolidation zone?". The
top panel colors bars green→yellow→red on this composite, so high-confidence
holds and fakeouts both pop out. The bottom panel splits the composite into
its Hold (firebrick) and Fakeout (orange) components stacked, so you can see
which part is driving the call.

Class indices come from the simulator's labelToInt mapping
(TradingEdge.Simulation.HoldDataset.labelNames):
    5 = Hold|UptrendDay,    6 = Hold|DowntrendDay,
    7 = Fakeout|UptrendDay, 8 = Fakeout|DowntrendDay.

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


HOLD_CLASSES = (5, 6)         # Hold | UpDay, Hold | DownDay
FAKEOUT_CLASSES = (7, 8)      # Fakeout | UpDay, Fakeout | DownDay


def load_bars(path: str):
    return pq.read_table(path).to_pandas()


def load_pred(path: str):
    return pq.read_table(path).to_pandas()


def viridis_color(p: float) -> str:
    """Map p in [0, 1] to a green-low / red-high RGB string."""
    p = max(0.0, min(1.0, float(p)))
    if p < 0.5:
        t = p / 0.5
        r, g, b = int(t * 255), 200, 0
    else:
        t = (p - 0.5) / 0.5
        r, g, b = 255, int(200 * (1 - t)), 0
    return f"rgb({r},{g},{b})"


def plot(bars, pred, output_html, title: str):
    df = bars.merge(pred, on="bar_idx", how="left", suffixes=("", "_pred"))
    n = len(df)

    prob_cols = [c for c in pred.columns if c.startswith("prob_")]
    assert prob_cols, "no prob_<i> columns in predictions parquet"

    def safe(col: str) -> np.ndarray:
        return df[col].fillna(0.0).to_numpy() if col in df else np.zeros(n)

    hold_score = sum(safe(f"prob_{c}") for c in HOLD_CLASSES)
    fakeout_score = sum(safe(f"prob_{c}") for c in FAKEOUT_CLASSES)
    composite = hold_score + fakeout_score

    print(f"Bars: {n}, predictions matched: {df[prob_cols[0]].notna().sum()}")
    print(f"Composite hold+fakeout score: p50={np.median(composite):.4f}, "
          f"p90={np.percentile(composite, 90):.4f}, "
          f">0.5: {(composite > 0.5).sum()}  ({(composite > 0.5).mean()*100:.1f}%)")

    log_rets = df["ret"].to_numpy()
    log_rets[0] = 0.0
    rel_price = np.exp(np.cumsum(log_rets))

    rel_stddev = df["rel_stddev"].to_numpy()
    bar_half = 2.0 * rel_stddev * rel_price
    bar_height = 2.0 * bar_half
    bar_base = rel_price - bar_half

    colors = [viridis_color(s) for s in composite]
    x_vals = np.arange(n, dtype=np.float64)

    pred_label = df["pred_label"].fillna(-1).to_numpy().astype(int)

    customdata = np.column_stack([
        df["bar_idx"].to_numpy(),
        rel_price,
        rel_stddev,
        df["ret"].to_numpy(),
        df["duration_sec"].to_numpy(),
        df["trade_count"].to_numpy(),
        composite,
        hold_score,
        fakeout_score,
        pred_label,
    ])

    fig = make_subplots(
        rows=3, cols=1,
        shared_xaxes=True,
        vertical_spacing=0.04,
        row_heights=[0.55, 0.25, 0.20],
        subplot_titles=[
            "Price (log-cum) ±2σ — color = P(Hold) + P(Fakeout)",
            "Hold (red) + Fakeout (orange) probability",
            "Bar duration (seconds, axis reversed)",
        ],
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
            "<b>composite:</b> %{customdata[6]:.4f} "
            "(hold=%{customdata[7]:.3f}, fakeout=%{customdata[8]:.3f})<br>"
            "<b>argmax:</b> class %{customdata[9]}<extra></extra>"
        ),
        name="bars",
    ), row=1, col=1)

    fig.add_trace(go.Scatter(
        x=x_vals, y=rel_price, mode="lines",
        line=dict(color="black", width=1),
        name="rel_price", hoverinfo="skip",
    ), row=1, col=1)

    # Bottom panel: stacked Hold + Fakeout. Hold gets the firebrick color
    # (matches the previous binary chart) and stacks first; Fakeout sits
    # on top in orange. Stack heights add to the composite score.
    fig.add_trace(go.Scatter(
        x=x_vals, y=hold_score, mode="lines", fill="tozeroy",
        line=dict(color="firebrick", width=1),
        fillcolor="rgba(178, 34, 34, 0.5)",
        name="hold",
        hovertemplate="bar %{x}: hold=%{y:.4f}<extra></extra>",
    ), row=2, col=1)
    fig.add_trace(go.Scatter(
        x=x_vals, y=hold_score + fakeout_score, mode="lines", fill="tonexty",
        line=dict(color="orange", width=1),
        fillcolor="rgba(255, 165, 0, 0.5)",
        name="fakeout",
        hovertemplate="bar %{x}: composite=%{y:.4f}<extra></extra>",
    ), row=2, col=1)
    fig.add_hline(y=0.5, line=dict(color="gray", width=1, dash="dash"), row=2, col=1)

    # Bottom panel: per-bar duration. Y-axis is reversed (matches sim_volume.py)
    # so denser activity reads as taller bars and quiet stretches dip toward
    # the bottom — the visual cue for hold-vs-drift discrimination.
    durations = df["duration_sec"].fillna(0.0).to_numpy()
    fig.add_trace(go.Scatter(
        x=x_vals, y=durations, mode="lines", fill="tozeroy",
        line=dict(color="steelblue", width=1),
        fillcolor="rgba(70, 130, 180, 0.4)",
        name="duration",
        hovertemplate="bar %{x}: duration=%{y:.3f}s<extra></extra>",
    ), row=3, col=1)

    fig.update_layout(
        title=title, height=1100, width=1500,
        hovermode="closest", showlegend=True,
    )
    fig.update_xaxes(rangeslider_visible=False, row=1, col=1)
    fig.update_xaxes(title_text="bar_idx", row=3, col=1)
    fig.update_yaxes(title_text="rel_price", row=1, col=1)
    fig.update_yaxes(title_text="probability", range=[0, 1], row=2, col=1)
    fig.update_yaxes(title_text="seconds", row=3, col=1, autorange="reversed")

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
    ap.add_argument("--pred", required=True, help="Hold predictions parquet (multi-class prob_<i>)")
    ap.add_argument("--output", required=True, help="Output HTML path")
    ap.add_argument("--title", default=None)
    args = ap.parse_args()

    bars = load_bars(args.bars)
    pred = load_pred(args.pred)
    title = args.title or f"BTC hold-detector — {os.path.basename(args.bars)}"
    plot(bars, pred, args.output, title)


if __name__ == "__main__":
    main()
