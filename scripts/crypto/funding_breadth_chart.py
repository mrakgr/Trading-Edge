"""Funding-rate visualization: BTC 1h VWAP vs universe-wide funding aggregates
vs BTCUSDT's own funding rate.

Five panes, top-down:

  1. BTC-USDT-PERP 1h VWAP — price reference.
  2. Universe median funding rate per hour (from build-funding-breadth)
     and BTCUSDT's own funding rate (forward-filled to 1h cadence).
     Both on the same axis so you can eyeball BTC vs broad-market divergences.
  3. median_funding_rank (t-digest CDF in [0,1] over universe medians).
  4. pct_positive (fraction of active perps with funding > 0) and
     pct_extreme_positive (fraction > +0.01% per interval) — the latter is
     the post-2024-election ATH sign.
  5. n_active_symbols — universe-size diagnostic.
"""

import argparse
import os
from datetime import datetime, timezone

import duckdb
import pandas as pd
import plotly.graph_objects as go
from plotly.subplots import make_subplots


def load_funding_breadth(path: str) -> pd.DataFrame:
    con = duckdb.connect()
    return con.execute(f"""
        SELECT hour_us, n_active_symbols, avg_funding, median_funding,
               pct_positive, pct_extreme_positive, pct_extreme_negative,
               median_funding_rank
        FROM read_parquet('{path}')
        ORDER BY hour_us
    """).fetchdf()


def load_btc_bars(path: str) -> pd.DataFrame:
    con = duckdb.connect()
    return con.execute(f"""
        SELECT start_us, end_us, open, high, low, close, vwap, volume
        FROM read_parquet('{path}')
        ORDER BY start_us
    """).fetchdf()


def load_btc_funding_aligned(funding_path: str, hour_us_array) -> pd.Series:
    """Forward-fill BTCUSDT funding to the 1h grid via DuckDB ASOF JOIN.

    BTC funding fires every 8 hours; we replicate the same forward-fill
    semantics the universe panel uses (each hour carries the most recent
    funding rate whose calc_time_us ≤ hour_us)."""
    con = duckdb.connect()
    # Build a temp table of the hours we need.
    grid = pd.DataFrame({"hour_us": hour_us_array})
    con.register("hour_grid", grid)
    df = con.execute(f"""
        WITH funding AS (
          SELECT calc_time_us, funding_rate
          FROM read_parquet('{funding_path}')
        )
        SELECT g.hour_us, f.funding_rate
        FROM hour_grid g
        ASOF LEFT JOIN funding f ON g.hour_us >= f.calc_time_us
        ORDER BY g.hour_us
    """).fetchdf()
    return df["funding_rate"]


def to_dt(us_array) -> pd.DatetimeIndex:
    return pd.to_datetime(us_array, unit="us", utc=True)


def plot(funding: pd.DataFrame, btc: pd.DataFrame, btc_funding: pd.Series,
         output_html: str, title: str):
    fx = to_dt(funding["hour_us"].to_numpy())
    cx = to_dt(btc["start_us"].to_numpy())

    fig = make_subplots(
        rows=5, cols=1,
        shared_xaxes=True,
        vertical_spacing=0.035,
        row_heights=[0.32, 0.20, 0.18, 0.18, 0.12],
        subplot_titles=[
            "BTC-USDT-PERP 1h VWAP",
            "Funding rates per interval — universe median (purple) vs BTCUSDT (orange)",
            "Universe median funding rank (t-digest CDF, [0,1])",
            "pct_positive (blue) & pct_extreme_positive (red, >+0.01%/interval)",
            "n_active_symbols (universe size)",
        ],
    )

    # Pane 1: BTC VWAP.
    fig.add_trace(
        go.Scatter(x=cx, y=btc["vwap"], mode="lines", name="BTC VWAP",
                   line=dict(color="black", width=1)),
        row=1, col=1,
    )

    # Pane 2: median + BTC funding on the same axis.
    fig.add_trace(
        go.Scatter(x=fx, y=funding["median_funding"], mode="lines",
                   name="universe median funding",
                   line=dict(color="purple", width=1.2)),
        row=2, col=1,
    )
    fig.add_trace(
        go.Scatter(x=fx, y=btc_funding, mode="lines",
                   name="BTCUSDT funding",
                   line=dict(color="orange", width=1)),
        row=2, col=1,
    )
    fig.add_hline(y=0.0, line_width=1, line_dash="dash", line_color="grey", row=2, col=1)
    # Reference line at +0.01% (the extreme-positive threshold).
    fig.add_hline(y=0.0001, line_width=1, line_dash="dot", line_color="red", row=2, col=1)

    # Pane 3: rank.
    fig.add_trace(
        go.Scatter(x=fx, y=funding["median_funding_rank"], mode="lines",
                   name="median funding rank",
                   line=dict(color="purple", width=1)),
        row=3, col=1,
    )
    fig.add_hline(y=0.5, line_width=1, line_dash="dash", line_color="grey", row=3, col=1)
    # Mark the bucket-0 short-side dead zone.
    fig.add_hline(y=0.10, line_width=1, line_dash="dot", line_color="red", row=3, col=1)

    # Pane 4: pct_positive + pct_extreme_positive.
    fig.add_trace(
        go.Scatter(x=fx, y=funding["pct_positive"], mode="lines",
                   name="pct_positive",
                   line=dict(color="blue", width=1)),
        row=4, col=1,
    )
    fig.add_trace(
        go.Scatter(x=fx, y=funding["pct_extreme_positive"], mode="lines",
                   name="pct_extreme_positive",
                   line=dict(color="red", width=1)),
        row=4, col=1,
    )
    fig.add_hline(y=0.5, line_width=1, line_dash="dash", line_color="grey", row=4, col=1)

    # Pane 5: n_active_symbols.
    fig.add_trace(
        go.Scatter(x=fx, y=funding["n_active_symbols"], mode="lines",
                   name="n_active_symbols",
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
    fig.update_yaxes(title_text="Funding rate", row=2, col=1)
    fig.update_yaxes(title_text="Rank [0,1]", range=[0, 1], row=3, col=1)
    fig.update_yaxes(title_text="Fraction", range=[0, 1], row=4, col=1)
    fig.update_yaxes(title_text="n_active", row=5, col=1)
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
    ap.add_argument("--funding-breadth",
                    default="/mnt/d/trading-edge-bulk/crypto/binance/breadth/funding_per_hour.parquet",
                    help="Per-hour funding breadth parquet from `build-funding-breadth`.")
    ap.add_argument("--btc-bars",
                    default="/mnt/d/trading-edge-bulk/crypto/binance/perps_bars/1h/BTCUSDT.parquet",
                    help="BTCUSDT 1h bar parquet.")
    ap.add_argument("--btc-funding",
                    default="/mnt/d/trading-edge-bulk/crypto/binance/perps_funding/BTCUSDT.parquet",
                    help="BTCUSDT funding parquet.")
    ap.add_argument("-o", "--output", default="logs/funding_breadth_overlay.html",
                    help="Output HTML path.")
    args = ap.parse_args()

    print(f"Loading funding breadth from {args.funding_breadth}...")
    funding = load_funding_breadth(args.funding_breadth)
    print(f"  {len(funding):,} hours")

    print(f"Loading BTC bars from {args.btc_bars}...")
    btc = load_btc_bars(args.btc_bars)
    print(f"  {len(btc):,} bars")

    print(f"Forward-filling BTCUSDT funding to 1h grid...")
    btc_funding = load_btc_funding_aligned(args.btc_funding,
                                            funding["hour_us"].to_numpy())
    print(f"  {btc_funding.notna().sum():,}/{len(btc_funding):,} hours have a funding rate")

    os.makedirs(os.path.dirname(args.output) or ".", exist_ok=True)
    title = "Universe funding breadth + BTCUSDT funding + BTC 1h VWAP"
    plot(funding, btc, btc_funding, args.output, title)


if __name__ == "__main__":
    main()
