"""BTC perps order-book imbalance overlay — snapshot vs delta-replay.

Two-pane plotly chart:
  - top: BTC-USDT-PERP mid-price track (from book snapshots)
  - bottom: four OBI lines, all on a [-1, +1] y-axis
      solid blue   = obi_top5_book
      dashed blue  = obi_top10_book
      solid orange = obi_top5_delta
      dashed orange= obi_top10_delta

Eyeball test: do the orange lines (delta-replay) track the blue lines
(snapshot-direct), or do they reveal signal that 100 ms snapshots miss?

Reads:
  /mnt/d/trading-edge-bulk/crypto/lake/book/BINANCE_FUTURES/BTC-USDT-PERP/{date}.parquet
  /mnt/d/trading-edge-bulk/crypto/lake/book_delta_v2/BINANCE_FUTURES/BTC-USDT-PERP/{date}.parquet
"""
from __future__ import annotations

import argparse
import os
import sys
from datetime import datetime, timezone

import numpy as np
import pandas as pd
import plotly.graph_objects as go
from plotly.subplots import make_subplots

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from obi import obi_from_book, obi_from_delta  # noqa: E402

LAKE_ROOT = "/mnt/d/trading-edge-bulk/crypto/lake"
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
CHART_CONTROLS_JS = os.path.join(
    os.path.dirname(SCRIPT_DIR), "visualization", "chart_controls.js"
)


def _us_to_dt(us: np.ndarray) -> np.ndarray:
    return pd.to_datetime(us, unit="us", utc=True)


def _load_book(date: str, symbol: str, exchange: str) -> pd.DataFrame:
    path = f"{LAKE_ROOT}/book/{exchange}/{symbol}/{date}.parquet"
    if not os.path.exists(path):
        raise FileNotFoundError(path)
    return pd.read_parquet(path)


def _load_delta(date: str, symbol: str, exchange: str) -> pd.DataFrame:
    path = f"{LAKE_ROOT}/book_delta_v2/{exchange}/{symbol}/{date}.parquet"
    if not os.path.exists(path):
        raise FileNotFoundError(path)
    # Only the columns we need.
    return pd.read_parquet(
        path, columns=["timestamp_us", "side_is_bid", "price", "size"]
    )


def _decimate(times_us: np.ndarray, vals: np.ndarray, max_pts: int):
    """Stride-decimate to keep plotly responsive. ~50k points is the practical
    ceiling for line rendering before browsers start to choke."""
    n = len(times_us)
    if n <= max_pts:
        return times_us, vals
    step = max(1, n // max_pts)
    return times_us[::step], vals[::step]


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--date", required=True, help="YYYY-MM-DD")
    ap.add_argument("--symbol", default="BTC-USDT-PERP")
    ap.add_argument("--exchange", default="BINANCE_FUTURES")
    ap.add_argument("--lambda-decay", type=float, default=0.58,
                    help="OBI exponential decay per level index (default 0.58: "
                         "level 4 carries ~10%% of level 0).")
    ap.add_argument("--sample-cadence-ms", type=int, default=100,
                    help="Resample cadence for delta-replay OBI (ms). Default 100.")
    ap.add_argument("--start-hour", type=int, default=None,
                    help="Optional UTC hour-of-day window start (inclusive).")
    ap.add_argument("--end-hour", type=int, default=None,
                    help="Optional UTC hour-of-day window end (exclusive).")
    ap.add_argument("--max-points", type=int, default=50_000,
                    help="Per-line decimation ceiling. Default 50000.")
    ap.add_argument("-o", "--output", help="Output HTML path.")
    args = ap.parse_args()

    print(f"Loading book snapshots for {args.symbol} {args.date}...")
    book_df = _load_book(args.date, args.symbol, args.exchange)
    print(f"  {len(book_df):,} snapshots")

    if args.start_hour is not None or args.end_hour is not None:
        # Compute UTC midnight as us, then add hour offsets.
        midnight_us = int(
            datetime.fromisoformat(args.date).replace(tzinfo=timezone.utc).timestamp() * 1_000_000
        )
        sh = args.start_hour or 0
        eh = args.end_hour or 24
        t_lo = midnight_us + sh * 3600 * 1_000_000
        t_hi = midnight_us + eh * 3600 * 1_000_000
        n_before = len(book_df)
        book_df = book_df[(book_df["timestamp_us"] >= t_lo) & (book_df["timestamp_us"] < t_hi)]
        print(f"  filtered to [{sh}, {eh}) UTC: {n_before:,} -> {len(book_df):,}")
    else:
        t_lo = int(book_df["timestamp_us"].iloc[0])
        t_hi = int(book_df["timestamp_us"].iloc[-1])

    if len(book_df) == 0:
        print("ERROR: no snapshots in window", file=sys.stderr)
        return 1

    print(f"Loading book_delta_v2 for {args.symbol} {args.date}...")
    delta_df = _load_delta(args.date, args.symbol, args.exchange)
    print(f"  {len(delta_df):,} delta events")
    # Filter deltas to the same window.
    delta_df = delta_df[
        (delta_df["timestamp_us"] >= int(book_df["timestamp_us"].iloc[0]))
        & (delta_df["timestamp_us"] <= int(book_df["timestamp_us"].iloc[-1]))
    ].reset_index(drop=True)
    print(f"  {len(delta_df):,} delta events in window")

    # --- OBI from snapshots (cheap, vectorised) ---
    print("Computing OBI from snapshots...")
    ts_book, obi5_book = obi_from_book(book_df, depth=5,  lambda_decay=args.lambda_decay)
    _,       obi10_book = obi_from_book(book_df, depth=10, lambda_decay=args.lambda_decay)

    # --- OBI from delta replay (slow, Python loop). Seed from first book row. ---
    seed_row = book_df.iloc[0]
    cadence_us = args.sample_cadence_ms * 1000
    t_end_us = int(book_df["timestamp_us"].iloc[-1])
    print(f"Replaying deltas from t_seed={seed_row['timestamp_us']} for OBI top-5...")
    ts_delta, obi5_delta = obi_from_delta(
        seed_row, delta_df, depth=5, lambda_decay=args.lambda_decay,
        sample_cadence_us=cadence_us, t_end_us=t_end_us,
    )
    print(f"  {len(ts_delta):,} samples")
    print(f"Replaying deltas for OBI top-10...")
    ts_delta10, obi10_delta = obi_from_delta(
        seed_row, delta_df, depth=10, lambda_decay=args.lambda_decay,
        sample_cadence_us=cadence_us, t_end_us=t_end_us,
    )

    # --- Mid-price track from snapshots ---
    mid = (book_df["bid_0_price"].to_numpy() + book_df["ask_0_price"].to_numpy()) / 2.0

    # --- Decimate for rendering ---
    ts_book_d, mid_d = _decimate(ts_book, mid, args.max_points)
    _, obi5_book_d = _decimate(ts_book, obi5_book, args.max_points)
    _, obi10_book_d = _decimate(ts_book, obi10_book, args.max_points)
    ts_delta_d, obi5_delta_d = _decimate(ts_delta, obi5_delta, args.max_points)
    _, obi10_delta_d = _decimate(ts_delta10, obi10_delta, args.max_points)

    # --- Plot ---
    fig = make_subplots(
        rows=2, cols=1, shared_xaxes=True, vertical_spacing=0.04,
        row_heights=[0.55, 0.45],
        subplot_titles=[
            f"{args.symbol} mid-price ({args.date})",
            f"OBI (lambda={args.lambda_decay}): blue=book snapshots, orange=delta replay; solid=top5, dashed=top10",
        ],
    )
    fig.add_trace(
        go.Scatter(x=_us_to_dt(ts_book_d), y=mid_d, mode="lines",
                   name="mid", line=dict(color="black", width=1)),
        row=1, col=1,
    )
    fig.add_trace(
        go.Scatter(x=_us_to_dt(ts_book_d), y=obi5_book_d, mode="lines",
                   name="OBI top-5 (book)", line=dict(color="blue", width=1)),
        row=2, col=1,
    )
    fig.add_trace(
        go.Scatter(x=_us_to_dt(ts_book_d), y=obi10_book_d, mode="lines",
                   name="OBI top-10 (book)", line=dict(color="blue", width=1, dash="dash")),
        row=2, col=1,
    )
    fig.add_trace(
        go.Scatter(x=_us_to_dt(ts_delta_d), y=obi5_delta_d, mode="lines",
                   name="OBI top-5 (delta)", line=dict(color="orange", width=1)),
        row=2, col=1,
    )
    fig.add_trace(
        go.Scatter(x=_us_to_dt(ts_delta_d), y=obi10_delta_d, mode="lines",
                   name="OBI top-10 (delta)", line=dict(color="orange", width=1, dash="dash")),
        row=2, col=1,
    )
    fig.add_hline(y=0.0, line_width=1, line_dash="dot", line_color="grey", row=2, col=1)

    fig.update_layout(
        title=f"BTC-USDT-PERP order-book imbalance — {args.date}",
        height=900, width=1600, hovermode="x unified",
    )
    fig.update_yaxes(title_text="Price", row=1, col=1)
    fig.update_yaxes(title_text="OBI", range=[-1, 1], row=2, col=1)
    fig.update_xaxes(title_text="Time (UTC)", row=2, col=1)

    out = args.output or f"logs/btc_perps_obi_{args.date}.html"
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)
    with open(CHART_CONTROLS_JS) as f:
        post_script = f.read()
    config = {"scrollZoom": True, "displayModeBar": True}
    fig.write_html(out, config=config, post_script=post_script)
    print(f"Saved to {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
