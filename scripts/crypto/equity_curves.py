"""Equity-curve overlay for the crypto-perps backtest variants.

Reads multiple trips CSVs (one per system), groups round-trips by exit-day
(UTC), cumulates per-system P&L, and renders two stacked panes:

  1. Cumulative net P&L over time, one line per system.
  2. Peak-to-trough drawdown of the cumulative curve, one line per system.

Use:

    python scripts/crypto/equity_curves.py
    # → logs/equity_curves.html
"""

import os

import duckdb
import pandas as pd
import plotly.graph_objects as go
from plotly.subplots import make_subplots


SYSTEMS = [
    # (label, color, trips-csv path)
    ("v0 1h orderflow-MA",
     "#1f77b4",
     "data/crypto/v0_compare/backtest_results_trips_1h_ma200h_ls.csv"),
    ("Cumsum vol-tuned (1m)",
     "#ff7f0e",
     "data/crypto/cumsum_voltuned/backtest_results_trips_1m_th60_ls.csv"),
    ("Z-cumsum + persist (no stop)",
     "#2ca02c",
     "data/crypto/cumsum_z_persistexit/backtest_results_trips_1m_th15_ls.csv"),
    ("Z-cumsum + vol-stop M=100",
     "#d62728",
     "data/crypto/cumsum_z_volstop100/backtest_results_trips_1m_th15_ls.csv"),
    ("Z-cumsum + VWAP-stop 200h",
     "#9467bd",
     "data/crypto/cumsum_z_vwapstop200/backtest_results_trips_1m_th15_ls.csv"),
]


def daily_pnl(path: str) -> pd.DataFrame:
    """Read a trips CSV and return a per-day DataFrame with columns:
       date (UTC midnight), net_pnl, cum_pnl.

    Trips are bucketed by exit_us (the UTC day the trade was realized)."""
    con = duckdb.connect()
    df = con.execute(f"""
        SELECT
            CAST(make_timestamp(exit_us) AS DATE) AS date,
            SUM(net_pnl)                          AS net_pnl
        FROM read_csv_auto('{path}')
        GROUP BY 1
        ORDER BY 1
    """).fetchdf()
    df["cum_pnl"] = df["net_pnl"].cumsum()
    return df


def drawdown(cum: pd.Series) -> pd.Series:
    """Peak-to-trough drawdown of a cumulative-P&L series, in dollars."""
    running_peak = cum.cummax()
    return cum - running_peak  # 0 or negative


def main():
    series = {}
    for label, color, path in SYSTEMS:
        full = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                            "..", path)
        full = os.path.abspath(full)
        if not os.path.exists(full):
            print(f"[skip] {label}: {full} not found")
            continue
        df = daily_pnl(full)
        df["dd"] = drawdown(df["cum_pnl"])
        series[label] = (color, df)
        print(f"[load] {label:<40s}  {len(df):>4d} days  "
              f"final ${df['cum_pnl'].iloc[-1]:>10,.0f}  "
              f"max-dd ${df['dd'].min():>10,.0f}")

    if not series:
        print("No data loaded — aborting.")
        return

    fig = make_subplots(
        rows=2, cols=1,
        shared_xaxes=True,
        vertical_spacing=0.05,
        row_heights=[0.65, 0.35],
        subplot_titles=[
            "Cumulative net P&L (USDT, $1000 notional/trade)",
            "Drawdown from running peak (USDT)",
        ],
    )

    for label, (color, df) in series.items():
        # Pane 1: cumulative P&L
        fig.add_trace(
            go.Scatter(
                x=df["date"], y=df["cum_pnl"],
                mode="lines",
                name=label,
                line=dict(color=color, width=1.5),
                legendgroup=label,
            ),
            row=1, col=1,
        )
        # Pane 2: drawdown (no separate legend entry)
        fig.add_trace(
            go.Scatter(
                x=df["date"], y=df["dd"],
                mode="lines",
                name=label,
                line=dict(color=color, width=1),
                legendgroup=label,
                showlegend=False,
                hoverinfo="skip",
            ),
            row=2, col=1,
        )

    fig.add_hline(y=0.0, line_width=1, line_dash="dash", line_color="grey",
                  row=1, col=1)
    fig.add_hline(y=0.0, line_width=1, line_dash="dash", line_color="grey",
                  row=2, col=1)

    fig.update_layout(
        title="Equity-curve comparison — orderflow-MA backtest variants",
        height=900, width=1700,
        hovermode="x unified",
        legend=dict(
            orientation="h",
            yanchor="bottom", y=1.02,
            xanchor="left", x=0.0,
        ),
    )
    fig.update_yaxes(title_text="Cumulative P&L ($)", row=1, col=1)
    fig.update_yaxes(title_text="Drawdown ($)", row=2, col=1)
    fig.update_xaxes(title_text="Date (UTC)", row=2, col=1)

    output = os.path.abspath(os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
        "..", "logs", "equity_curves.html"))
    os.makedirs(os.path.dirname(output), exist_ok=True)

    config = {"scrollZoom": True, "displayModeBar": True}
    chart_controls = os.path.abspath(os.path.join(
        os.path.dirname(os.path.abspath(__file__)),
        "..", "visualization", "chart_controls.js"))
    with open(chart_controls) as f:
        post_script = f.read()
    fig.write_html(output, config=config, post_script=post_script)
    print()
    print(f"Saved to {output}")


if __name__ == "__main__":
    main()
