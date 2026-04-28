"""BTC volume-bar chart with multi-class hold-detector posterior overlay.

Reads:
  - BTC bars parquet (raw schema from `dotnet ... btc-bars`):
      day_id, bar_idx, rel_stddev, ret, duration_sec, trade_count, buy_count,
      label
  - Predictions parquet (from predict_hold.py):
      day_id, bar_idx, pred_label, prob_0, prob_1, ..., prob_<n-1>

Layout (4 panels):
  1. Price ±2σ bars colored by argmax class with intensity = max-prob:
       gray  = drift (any direction)
       blues = ShortHold / Hold / LongHold (light -> medium -> dark)
       orange = Fakeout
       teal/cyan/blue-green = HoldRelease variants
  2. Stacked area chart of all class probabilities, same colors as panel 1.
  3. Per-bar sidedness: (2 * buy_count / trade_count - 1) ∈ [-1, +1].
     +1 = all buyer-aggressive, -1 = all seller-aggressive, 0 = balanced.
     Useful for sanity-checking weak-hold candidates (low aggression should
     show sidedness near zero, releases/fakeouts should skew strongly).
  4. Bar duration (axis reversed, blue area).

Class indices (matches simulator's labelToInt order):
    1 DriftFlat,  2 DriftUp,  3 DriftDown,
    4 ShortHold,  5 Hold,     6 LongHold,
    7 Fakeout,
    8 ShortHoldRelease,  9 HoldRelease,  10 LongHoldRelease

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


# Color per class. Tuples are (R, G, B). Reused by both the top-panel bar
# colors and the stacked-area panel so the eye links them.
CLASS_COLORS: dict[int, tuple[int, int, int]] = {
    0: (200, 200, 200),    # unlabeled (shouldn't occur on BTC inference)
    1: (180, 180, 180),    # DriftFlat — neutral gray
    2: (170, 170, 170),    # DriftUp   — slightly different gray
    3: (170, 170, 170),    # DriftDown — slightly different gray
    4: (135, 206, 235),    # ShortHold — sky blue (lightest)
    5: ( 70, 130, 180),    # Hold      — steel blue (medium)
    6: ( 25,  50, 120),    # LongHold  — navy (darkest)
    7: (255, 140,   0),    # Fakeout   — dark orange
    8: ( 64, 224, 208),    # ShortHoldRelease — turquoise
    9: ( 32, 178, 170),    # HoldRelease — light sea green
    10: (  0, 128, 128),   # LongHoldRelease — teal
}

CLASS_NAMES_FALLBACK = [
    "", "DriftFlat", "DriftUp", "DriftDown",
    "ShortHold", "Hold", "LongHold",
    "Fakeout",
    "ShortHoldRelease", "HoldRelease", "LongHoldRelease",
]


def load_bars(path: str):
    return pq.read_table(path).to_pandas()


def load_pred(path: str):
    return pq.read_table(path).to_pandas()


def rgb_str(rgb: tuple[int, int, int], alpha: float | None = None) -> str:
    if alpha is None:
        return f"rgb({rgb[0]},{rgb[1]},{rgb[2]})"
    return f"rgba({rgb[0]},{rgb[1]},{rgb[2]},{alpha:.3f})"


def class_color(cls: int, intensity: float = 1.0) -> str:
    """Return rgba string for a class id at the given intensity in [0, 1].
    intensity scales toward the class's full color from a desaturated pale gray
    so low-confidence predictions read as washed-out."""
    base = CLASS_COLORS.get(cls, (180, 180, 180))
    pale = (235, 235, 235)
    intensity = max(0.0, min(1.0, intensity))
    r = int(pale[0] + (base[0] - pale[0]) * intensity)
    g = int(pale[1] + (base[1] - pale[1]) * intensity)
    b = int(pale[2] + (base[2] - pale[2]) * intensity)
    return rgb_str((r, g, b))


def plot(bars, pred, output_html, title: str, label_names: list[str] | None = None):
    df = bars.merge(pred, on="bar_idx", how="left", suffixes=("", "_pred"))
    n = len(df)

    prob_cols = sorted(
        [c for c in pred.columns if c.startswith("prob_")],
        key=lambda c: int(c.split("_", 1)[1]),
    )
    assert prob_cols, "no prob_<i> columns in predictions parquet"
    num_classes = len(prob_cols)
    if label_names is None or len(label_names) < num_classes:
        label_names = CLASS_NAMES_FALLBACK[:num_classes]

    probs = df[prob_cols].fillna(0.0).to_numpy()  # (n, K)
    pred_label = df["pred_label"].fillna(-1).to_numpy().astype(int)
    max_prob = probs.max(axis=1)

    print(f"Bars: {n}, predictions matched: {df[prob_cols[0]].notna().sum()}")
    holdish_idx = [4, 5, 6, 7]   # Short/Mid/Long hold + Fakeout
    if num_classes > max(holdish_idx):
        holdish = probs[:, holdish_idx].sum(axis=1)
        print(f"Hold+Fakeout composite: p50={np.median(holdish):.4f}, "
              f"p90={np.percentile(holdish,90):.4f}, "
              f">0.5: {(holdish > 0.5).sum()}  ({(holdish > 0.5).mean()*100:.1f}%)")

    log_rets = df["ret"].to_numpy()
    log_rets[0] = 0.0
    rel_price = np.exp(np.cumsum(log_rets))

    rel_stddev = df["rel_stddev"].to_numpy()
    bar_half = 2.0 * rel_stddev * rel_price
    bar_height = 2.0 * bar_half
    bar_base = rel_price - bar_half

    # Per-bar fill color: hue from argmax class, intensity from max prob.
    bar_colors = [class_color(int(c), float(p)) for c, p in zip(pred_label, max_prob)]

    x_vals = np.arange(n, dtype=np.float64)

    customdata = np.column_stack([
        df["bar_idx"].to_numpy(),
        rel_price,
        rel_stddev,
        df["ret"].to_numpy(),
        df["duration_sec"].to_numpy(),
        df["trade_count"].to_numpy(),
        max_prob,
        pred_label,
    ])

    # Build hovertemplate with the predicted class name pulled from the
    # checkpoint's label_names so we don't have to keep this script in sync.
    pred_class_name = np.array([
        label_names[c] if 0 <= c < len(label_names) else f"class_{c}"
        for c in pred_label
    ])
    customdata_obj = np.column_stack([customdata, pred_class_name])

    fig = make_subplots(
        rows=4, cols=1,
        shared_xaxes=True,
        vertical_spacing=0.035,
        row_heights=[0.45, 0.22, 0.18, 0.15],
        subplot_titles=[
            "Price (log-cum) ±2σ — color = argmax class, intensity = confidence",
            "Class probabilities (stacked)",
            "Sidedness (2·buy/trade − 1) — green = buy-aggression, red = sell",
            "Bar duration (seconds, axis reversed)",
        ],
    )

    fig.add_trace(go.Bar(
        x=x_vals, y=bar_height, base=bar_base,
        marker_color=bar_colors, marker_line_width=0,
        width=0.9,
        customdata=customdata_obj,
        hovertemplate=(
            "<b>bar:</b> %{customdata[0]}<br>"
            "<b>rel_price:</b> %{customdata[1]:.6f}<br>"
            "<b>rel_stddev:</b> %{customdata[2]:.6f}<br>"
            "<b>log_ret:</b> %{customdata[3]:+.6f}<br>"
            "<b>dur:</b> %{customdata[4]:.2f}s<br>"
            "<b>trades:</b> %{customdata[5]}<br>"
            "<b>argmax prob:</b> %{customdata[6]:.4f}<br>"
            "<b>argmax class:</b> %{customdata[7]} (%{customdata[8]})<extra></extra>"
        ),
        name="bars",
        showlegend=False,
    ), row=1, col=1)

    # Price line on top of the bars for orientation.
    fig.add_trace(go.Scatter(
        x=x_vals, y=rel_price, mode="lines",
        line=dict(color="black", width=1),
        name="rel_price", hoverinfo="skip", showlegend=False,
    ), row=1, col=1)

    # Stacked-area panel: cumulative probabilities. We stack all classes,
    # using each class's color so the eye matches band -> top-panel bar.
    cum = np.zeros(n, dtype=np.float32)
    for ci in range(num_classes):
        if ci == 0:
            continue  # skip the unlabeled bucket — never fired on labelled data
        prev = cum.copy()
        cum = cum + probs[:, ci]
        col_solid = class_color(ci, 1.0)
        # tonexty fills between this trace and the previous one. Make the
        # very first non-zero trace go to zero by setting fill='tozeroy'.
        fig.add_trace(go.Scatter(
            x=x_vals,
            y=cum,
            mode="lines",
            line=dict(color=col_solid, width=0.5),
            fill=("tozeroy" if (prev == 0).all() else "tonexty"),
            fillcolor=class_color(ci, 0.7),
            name=label_names[ci] if ci < len(label_names) else f"class_{ci}",
            hovertemplate=(
                f"bar %{{x}}: cum P({label_names[ci] if ci < len(label_names) else ci})"
                "=%{y:.3f}<extra></extra>"
            ),
        ), row=2, col=1)

    fig.add_hline(y=0.5, line=dict(color="gray", width=1, dash="dash"), row=2, col=1)

    # Panel 3: sidedness. (2 * buy_count / trade_count - 1) ∈ [-1, +1].
    # Bars where trade_count = 0 (rare, only on overflow spillover from a
    # single huge print straddling many bars) get sidedness = 0.
    trade_count = df["trade_count"].fillna(0).to_numpy()
    buy_count = df["buy_count"].fillna(0).to_numpy()
    safe_n = np.where(trade_count > 0, trade_count, 1)
    sidedness = np.where(trade_count > 0, 2.0 * buy_count / safe_n - 1.0, 0.0)
    side_colors = ["rgba(0, 160, 0, 0.7)" if s >= 0 else "rgba(200, 30, 30, 0.7)" for s in sidedness]
    fig.add_trace(go.Bar(
        x=x_vals, y=sidedness,
        marker_color=side_colors, marker_line_width=0,
        width=0.9,
        name="sidedness",
        showlegend=False,
        hovertemplate=(
            "bar %{x}: sidedness=%{y:+.3f}"
            "<br>buys=%{customdata[0]} / trades=%{customdata[1]}<extra></extra>"
        ),
        customdata=np.column_stack([buy_count.astype(int), trade_count.astype(int)]),
    ), row=3, col=1)
    fig.add_hline(y=0.0, line=dict(color="gray", width=1), row=3, col=1)

    # Panel 4: per-bar duration.
    durations = df["duration_sec"].fillna(0.0).to_numpy()
    fig.add_trace(go.Scatter(
        x=x_vals, y=durations, mode="lines", fill="tozeroy",
        line=dict(color="steelblue", width=1),
        fillcolor="rgba(70, 130, 180, 0.4)",
        name="duration",
        hovertemplate="bar %{x}: duration=%{y:.3f}s<extra></extra>",
        showlegend=False,
    ), row=4, col=1)

    fig.update_layout(
        title=title, height=1300, width=1500,
        hovermode="closest", showlegend=True,
        legend=dict(orientation="v", x=1.02, y=1.0, xanchor="left"),
    )
    fig.update_xaxes(rangeslider_visible=False, row=1, col=1)
    fig.update_xaxes(title_text="bar_idx", row=4, col=1)
    fig.update_yaxes(title_text="rel_price", row=1, col=1)
    fig.update_yaxes(title_text="cum prob", range=[0, 1], row=2, col=1)
    fig.update_yaxes(title_text="sidedness", range=[-1, 1], row=3, col=1)
    fig.update_yaxes(title_text="seconds", row=4, col=1, autorange="reversed")

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
    ap.add_argument("--labels", default=None,
                    help="Optional .labels.json (auto-detected next to --bars if not given)")
    args = ap.parse_args()

    bars = load_bars(args.bars)
    pred = load_pred(args.pred)

    label_names = None
    labels_path = args.labels
    if labels_path is None:
        # Look next to the predictions parquet first, then bars.
        for cand in (args.pred, args.bars):
            base, _ = os.path.splitext(cand)
            sidecar = base + ".labels.json"
            if os.path.exists(sidecar):
                labels_path = sidecar
                break
    if labels_path and os.path.exists(labels_path):
        import json
        with open(labels_path) as f:
            m = json.load(f)
        n = max(int(k) for k in m.keys()) + 1
        label_names = [""] * n
        for k, v in m.items():
            label_names[int(k)] = v

    title = args.title or f"BTC hold-detector — {os.path.basename(args.bars)}"
    plot(bars, pred, args.output, title, label_names=label_names)


if __name__ == "__main__":
    main()
