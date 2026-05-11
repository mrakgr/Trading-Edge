#!/usr/bin/env python3
"""Plot MTM cumulative-equity curves for the four cumsum-z short variants.

Reads data/crypto/cumsum_z_no_gate/mtm_monthly.csv (long-form: variant, month, pnl)
and writes a Plotly HTML with one equity curve per variant.
"""

import os
import pandas as pd
import plotly.graph_objects as go
from plotly.subplots import make_subplots

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
IN_CSV = os.path.join(ROOT, "data/crypto/cumsum_z_no_gate/mtm_monthly.csv")
OUT_HTML = os.path.join(ROOT, "data/crypto/cumsum_z_no_gate/equity_curves.html")
CONTROLS_JS = os.path.join(ROOT, "scripts/visualization/chart_controls.js")

LABELS = {
    "z100":   "z100 (baseline, gated, RawZ-100)",
    "ng200":  "no-gate, threshold=200",
    "ng450":  "no-gate, threshold=450",
    "ng4500": "no-gate, threshold=4500",
    "ng6750": "no-gate, threshold=6750",
}
COLORS = {"z100": "#888888", "ng200": "#ff7f0e", "ng450": "#1f77b4", "ng4500": "#2ca02c", "ng6750": "#d62728"}

df = pd.read_csv(IN_CSV, parse_dates=["month"])
df = df.sort_values(["variant", "month"]).reset_index(drop=True)
df["cum_pnl"] = df.groupby("variant")["pnl"].cumsum()
df["peak"] = df.groupby("variant")["cum_pnl"].cummax()
df["drawdown"] = df["cum_pnl"] - df["peak"]

fig = make_subplots(
    rows=3, cols=1,
    shared_xaxes=True,
    row_heights=[0.5, 0.25, 0.25],
    vertical_spacing=0.04,
    subplot_titles=("Cumulative MTM equity ($1k base notional)",
                    "Monthly P&L",
                    "Drawdown from peak"),
)

for variant in ["z100", "ng200", "ng450", "ng4500", "ng6750"]:
    sub = df[df["variant"] == variant]
    fig.add_trace(go.Scatter(
        x=sub["month"], y=sub["cum_pnl"],
        mode="lines+markers", name=LABELS[variant], legendgroup=variant,
        line=dict(color=COLORS[variant], width=2),
        marker=dict(size=4),
    ), row=1, col=1)
    fig.add_trace(go.Bar(
        x=sub["month"], y=sub["pnl"], name=LABELS[variant], legendgroup=variant,
        showlegend=False, marker_color=COLORS[variant], opacity=0.7,
    ), row=2, col=1)
    fig.add_trace(go.Scatter(
        x=sub["month"], y=sub["drawdown"], name=LABELS[variant], legendgroup=variant,
        showlegend=False, mode="lines", line=dict(color=COLORS[variant], width=1.5),
        fill="tozeroy", fillcolor=COLORS[variant], opacity=0.3,
    ), row=3, col=1)

fig.update_layout(
    title="Cumsum-Z short variants — proper monthly MTM (anchored at first 1m bar of each month)",
    template="plotly_white",
    height=900,
    legend=dict(orientation="h", yanchor="bottom", y=1.02, xanchor="left", x=0),
    barmode="group",
    hovermode="x unified",
)
fig.update_yaxes(title_text="Cumulative P&L ($)", row=1, col=1)
fig.update_yaxes(title_text="Monthly P&L ($)", row=2, col=1)
fig.update_yaxes(title_text="Drawdown ($)", row=3, col=1)
fig.update_xaxes(title_text="Month", row=3, col=1)

post_script = ""
if os.path.exists(CONTROLS_JS):
    with open(CONTROLS_JS) as f:
        post_script = f.read()

fig.write_html(OUT_HTML, post_script=post_script if post_script else None,
               config={"scrollZoom": True})
print(f"Wrote {OUT_HTML}")
print(df.groupby("variant").agg(total=("pnl","sum"),
                                max_eq=("cum_pnl","max"),
                                max_dd=("drawdown","min"),
                                months=("month","count")).round(0))
