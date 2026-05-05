"""Breadth-panel visualization with BTC 1h VWAP as the price reference.

Reads the per-hour aggregate parquet emitted by `TradingEdge.CryptoBacktest
build-breadth` and overlays it against BTCUSDT 1h bars. Four panes, top-down:

  1. BTC 1h VWAP line + close-line (price reference).
  2. pct_long (green) and pct_short (red), both in [0, 1] — fraction of the
     active universe in each orderflow regime at that hour.
  3. composite_signed_rank — t-digest CDF in [0, 1] of the composite
     buy-minus-sell dollar-volume across the universe (high = strong
     net taker buying, low = strong net taker selling).
  4. n_symbols — count of symbols with bars in this hour. Diagnostic,
     shows the universe expansion over time.

All four panes share the time axis. Middle-click pan + a/s/d dragmode
toggles via chart_controls.js (matches every other plotly chart in repo).
"""

import argparse
import os
from datetime import datetime, timezone

import duckdb
import pandas as pd
import plotly.graph_objects as go
from plotly.subplots import make_subplots


def load_breadth(path: str) -> pd.DataFrame:
    con = duckdb.connect()
    df = con.execute(f"""
        SELECT
            hour_us, n_symbols, n_long, n_short, n_flat,
            pct_long, pct_short,
            composite_buy_volume, composite_sell_volume,
            composite_signed_volume, composite_signed_rank,
            composite_buy_volume_ma200, composite_sell_volume_ma200,
            composite_signed_volume_ma200, composite_signed_rank_ma200
        FROM read_parquet('{path}')
        ORDER BY hour_us
    """).fetchdf()
    return df


def load_btc_bars(path: str) -> pd.DataFrame:
    con = duckdb.connect()
    df = con.execute(f"""
        SELECT start_us, end_us, open, high, low, close, vwap, volume
        FROM read_parquet('{path}')
        ORDER BY start_us
    """).fetchdf()
    return df


def to_dt(us_array) -> pd.DatetimeIndex:
    return pd.to_datetime(us_array, unit="us", utc=True)


def plot(breadth: pd.DataFrame, btc: pd.DataFrame, output_html: str, title: str):
    bx = to_dt(breadth["hour_us"].to_numpy())
    cx = to_dt(btc["start_us"].to_numpy())

    fig = make_subplots(
        rows=5, cols=1,
        shared_xaxes=True,
        vertical_spacing=0.035,
        row_heights=[0.32, 0.16, 0.20, 0.16, 0.16],
        subplot_titles=[
            "BTC-USDT-PERP 1h VWAP (and close)",
            "Universe orderflow breadth: pct_long (green) vs pct_short (red)",
            "Composite signed-volume MA200 (smoothed, USDT)  —  thin grey: raw 1h",
            "Composite signed-volume rank MA200 (t-digest CDF, [0,1])",
            "n_symbols (universe size at hour)",
        ],
    )

    fig.add_trace(
        go.Scatter(x=cx, y=btc["vwap"], mode="lines", name="BTC VWAP",
                   line=dict(color="black", width=1)),
        row=1, col=1,
    )
    fig.add_trace(
        go.Scatter(x=cx, y=btc["close"], mode="lines", name="BTC close",
                   line=dict(color="grey", width=1, dash="dot"),
                   hoverinfo="skip"),
        row=1, col=1,
    )

    fig.add_trace(
        go.Scatter(x=bx, y=breadth["pct_long"], mode="lines", name="pct_long",
                   line=dict(color="green", width=1)),
        row=2, col=1,
    )
    fig.add_trace(
        go.Scatter(x=bx, y=breadth["pct_short"], mode="lines", name="pct_short",
                   line=dict(color="crimson", width=1)),
        row=2, col=1,
    )
    fig.add_hline(y=0.5, line_width=1, line_dash="dash", line_color="grey", row=2, col=1)

    # Pane 3: raw single-hour signed volume in light grey behind the MA200
    # smoothed signal in solid colour. The user wanted to see both — the raw
    # is too noisy to be a regime signal, but useful as context for what the
    # MA is averaging out.
    fig.add_trace(
        go.Scatter(x=bx, y=breadth["composite_signed_volume"], mode="lines",
                   name="signed vol (raw 1h)",
                   line=dict(color="lightgrey", width=1),
                   hoverinfo="skip"),
        row=3, col=1,
    )
    # Colour the MA200 series by sign: green when positive (net buying),
    # crimson when negative (net selling). Two separate traces masked by NaN.
    ma_signed = breadth["composite_signed_volume_ma200"].to_numpy()
    pos = ma_signed.copy()
    neg = ma_signed.copy()
    pos[ma_signed < 0] = float("nan")
    neg[ma_signed >= 0] = float("nan")
    fig.add_trace(
        go.Scatter(x=bx, y=pos, mode="lines", name="signed vol MA200 (net buy)",
                   line=dict(color="green", width=1.5)),
        row=3, col=1,
    )
    fig.add_trace(
        go.Scatter(x=bx, y=neg, mode="lines", name="signed vol MA200 (net sell)",
                   line=dict(color="crimson", width=1.5)),
        row=3, col=1,
    )
    fig.add_hline(y=0.0, line_width=1, line_dash="dash", line_color="black", row=3, col=1)

    fig.add_trace(
        go.Scatter(x=bx, y=breadth["composite_signed_rank_ma200"], mode="lines",
                   name="composite signed rank MA200",
                   line=dict(color="purple", width=1)),
        row=4, col=1,
    )
    fig.add_hline(y=0.5, line_width=1, line_dash="dash", line_color="grey", row=4, col=1)

    fig.add_trace(
        go.Scatter(x=bx, y=breadth["n_symbols"], mode="lines", name="n_symbols",
                   line=dict(color="blue", width=1),
                   fill="tozeroy", fillcolor="rgba(0, 0, 255, 0.15)"),
        row=5, col=1,
    )

    fig.update_layout(
        title=title,
        height=1300, width=1700,
        hovermode="x unified",
        showlegend=False,
    )
    fig.update_yaxes(title_text="Price (USDT)", row=1, col=1)
    fig.update_yaxes(title_text="Fraction", range=[0, 1], row=2, col=1)
    fig.update_yaxes(title_text="Signed vol (USDT)", row=3, col=1)
    fig.update_yaxes(title_text="Rank [0,1]", range=[0, 1], row=4, col=1)
    fig.update_yaxes(title_text="n_symbols", row=5, col=1)
    fig.update_xaxes(title_text="Time (UTC)", row=5, col=1)

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
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--breadth",
                    default="/mnt/d/trading-edge-bulk/crypto/binance/breadth/per_hour.parquet",
                    help="Per-hour breadth parquet from `build-breadth`.")
    ap.add_argument("--btc-bars",
                    default="/mnt/d/trading-edge-bulk/crypto/binance/perps_bars/1h/BTCUSDT.parquet",
                    help="BTCUSDT 1h bar parquet.")
    ap.add_argument("-o", "--output", default="logs/breadth_overlay.html",
                    help="Output HTML path.")
    args = ap.parse_args()

    print(f"Loading breadth from {args.breadth}...")
    breadth = load_breadth(args.breadth)
    print(f"  {len(breadth):,} hours")

    print(f"Loading BTC bars from {args.btc_bars}...")
    btc = load_btc_bars(args.btc_bars)
    print(f"  {len(btc):,} bars")

    os.makedirs(os.path.dirname(args.output) or ".", exist_ok=True)
    title = "Universe breadth + BTC-USDT-PERP 1h VWAP"
    plot(breadth, btc, args.output, title)


if __name__ == "__main__":
    main()
