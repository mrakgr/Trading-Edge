#!/usr/bin/env python3
"""Plot MTM equity curve for FlowSwing (OrderflowLongFadeMA production cell).

Reads data/crypto/long_fade_ma_default_rvol075/mtm_monthly.csv and writes
a Plotly HTML with cumulative equity, monthly bars, and drawdown.
"""

import os
import pandas as pd
import plotly.graph_objects as go
from plotly.subplots import make_subplots

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
IN_CSV = os.path.join(ROOT, "data/crypto/long_fade_ma_default_rvol075/mtm_monthly.csv")
OUT_HTML = os.path.join(ROOT, "data/crypto/long_fade_ma_default_rvol075/equity_curve.html")
CONTROLS_JS = os.path.join(ROOT, "scripts/visualization/chart_controls.js")

df = pd.read_csv(IN_CSV, parse_dates=["month"]).sort_values("month").reset_index(drop=True)
df["cum_pnl"] = df["pnl"].cumsum()
df["peak"] = df["cum_pnl"].cummax()
df["drawdown"] = df["cum_pnl"] - df["peak"]

fig = make_subplots(
    rows=3, cols=1,
    shared_xaxes=True,
    row_heights=[0.5, 0.25, 0.25],
    vertical_spacing=0.04,
    subplot_titles=("Cumulative MTM equity — FlowSwing ($1k base notional)",
                    "Monthly P&L",
                    "Drawdown from peak"),
)

fig.add_trace(go.Scatter(
    x=df["month"], y=df["cum_pnl"],
    mode="lines+markers", name="FlowSwing",
    line=dict(color="#2ca02c", width=2.5),
    marker=dict(size=6),
), row=1, col=1)
fig.add_trace(go.Bar(
    x=df["month"], y=df["pnl"], name="FlowSwing", showlegend=False,
    marker_color="#2ca02c", opacity=0.7,
), row=2, col=1)
fig.add_trace(go.Scatter(
    x=df["month"], y=df["drawdown"], name="FlowSwing", showlegend=False,
    mode="lines", line=dict(color="#d62728", width=1.5),
    fill="tozeroy", fillcolor="rgba(214,39,40,0.3)",
), row=3, col=1)

fig.update_layout(
    title="FlowSwing (OrderflowLongFadeMA) — proper monthly MTM",
    template="plotly_white",
    height=900,
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
print(df.agg(total=("pnl","sum"), max_eq=("cum_pnl","max"),
             max_dd=("drawdown","min")).round(0))
