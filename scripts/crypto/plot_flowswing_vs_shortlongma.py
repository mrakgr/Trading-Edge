#!/usr/bin/env python3
"""Plot MTM equity curves for FlowSwing-long vs ShortFadeMA-mirror-long-MA,
plus the implied combined book.

FlowSwing long: pd=0.14, ma=72h, cvd=240m, rvol=0.75
Short-long-MA:  pr=0.14, ma=4374h (6 months), cvd=240m, rvol=0.75, no 200h CVD
"""

import os
import pandas as pd
import plotly.graph_objects as go
from plotly.subplots import make_subplots

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
LONG_CSV  = os.path.join(ROOT, "data/crypto/long_fade_ma_default_rvol075/mtm_monthly.csv")
SHORT_CSV = os.path.join(ROOT, "data/crypto/short_fade_ma_mirror_xlong/mtm_monthly_pr0.14_ma4374h.csv")
OUT_HTML  = os.path.join(ROOT, "data/crypto/short_fade_ma_mirror_xlong/equity_curves_long_vs_longma.html")
CONTROLS_JS = os.path.join(ROOT, "scripts/visualization/chart_controls.js")

long_df  = pd.read_csv(LONG_CSV,  parse_dates=["month"]).rename(columns={"pnl":"long"}).drop(columns=["variant"])
short_df = pd.read_csv(SHORT_CSV, parse_dates=["month"]).rename(columns={"pnl":"short"}).drop(columns=["variant"])
df = pd.merge(long_df, short_df, on="month", how="outer").sort_values("month").fillna(0)
df["combined"] = df["long"] + df["short"]
for col in ("long","short","combined"):
    df[f"cum_{col}"] = df[col].cumsum()
    df[f"peak_{col}"] = df[f"cum_{col}"].cummax()
    df[f"dd_{col}"] = df[f"cum_{col}"] - df[f"peak_{col}"]

fig = make_subplots(
    rows=3, cols=1,
    shared_xaxes=True,
    row_heights=[0.5, 0.25, 0.25],
    vertical_spacing=0.04,
    subplot_titles=("Cumulative MTM equity ($1k notional/leg)",
                    "Monthly P&L",
                    "Drawdown from peak"),
)

LEGS = [
    ("long",     "FlowSwing long (pd=14% ma=72h)",                  "#2ca02c"),
    ("short",    "ShortFadeMA-mirror short (pr=14% ma=4374h/6mo)",  "#d62728"),
    ("combined", "Combined long+short book",                        "#1f77b4"),
]

for col, label, color in LEGS:
    fig.add_trace(go.Scatter(x=df["month"], y=df[f"cum_{col}"], mode="lines+markers",
                             name=label, legendgroup=col,
                             line=dict(color=color, width=2.5), marker=dict(size=5)),
                  row=1, col=1)
    fig.add_trace(go.Bar(x=df["month"], y=df[col], name=label, legendgroup=col,
                         showlegend=False, marker_color=color, opacity=0.7),
                  row=2, col=1)
    fig.add_trace(go.Scatter(x=df["month"], y=df[f"dd_{col}"], name=label, legendgroup=col,
                             showlegend=False, mode="lines",
                             line=dict(color=color, width=1.5),
                             fill="tozeroy", fillcolor=color, opacity=0.3),
                  row=3, col=1)

fig.update_layout(
    title="FlowSwing long + ShortFadeMA mirror w/ 6-month MA — proper monthly MTM",
    template="plotly_white", height=900,
    legend=dict(orientation="h", yanchor="bottom", y=1.02, xanchor="left", x=0),
    barmode="group", hovermode="x unified",
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
for col, label, _ in LEGS:
    s = df[col]
    sharpe = s.mean()/s.std() * 12**0.5 if s.std() else float("nan")
    print(f"{label:<55s} total=${s.sum():>9,.0f}  max_dd=${df[f'dd_{col}'].min():>9,.0f}  wins={int((s>0).sum()):>2d} losses={int((s<0).sum()):>2d}  ann_sharpe={sharpe:.2f}")
