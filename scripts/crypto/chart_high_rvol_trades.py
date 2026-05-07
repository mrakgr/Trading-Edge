"""Chart per-trade price + buy/sell volume action around entry.

For each trip in the input CSV (defaults to >=10x rvol shorts on the
30d/8h volume-momentum stratification), plot:
  - top pane:    1m close price, with vertical lines at entry and exit
                 and horizontal lines at entry price + exit price.
  - bottom pane: stacked buy/sell dollar volume per minute (green/red).

Window is +/- `--hours-before` to `--hours-after` of entry. Default 12h
each side. Files are written to a per-symbol subdir of `--out-dir`.

Reuses the standard `scripts/visualization/chart_controls.js` post-script
so middle-click pan/zoom and a/s/d dragmode shortcuts work.
"""
from __future__ import annotations

import argparse
import os
import sys

import numpy as np
import pandas as pd
import plotly.graph_objects as go
from plotly.subplots import make_subplots


SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
CHART_CONTROLS_JS = os.path.join(
    os.path.dirname(SCRIPT_DIR), "visualization", "chart_controls.js"
)
DEFAULT_TRIPS = "data/crypto/inspect_rvol/highrvol_shorts_to_chart.csv"
DEFAULT_BARS_ROOT = "data/crypto/perps_bars/1m"
DEFAULT_OUT_DIR = "logs/charts/high_rvol_shorts"

US_PER_HOUR = 3_600_000_000


def load_window(bars_path: str, t_lo_us: int, t_hi_us: int) -> pd.DataFrame:
    """Load bars in [t_lo_us, t_hi_us]. Caller is responsible for
    asking for any pre-roll needed by trailing indicators."""
    df = pd.read_parquet(
        bars_path,
        columns=["start_us", "open", "high", "low", "close",
                 "volume", "buy_dollar_volume", "sell_dollar_volume"],
    )
    mask = (df.start_us >= t_lo_us) & (df.start_us <= t_hi_us)
    return df.loc[mask].sort_values("start_us").reset_index(drop=True)


def imbalance_ma(bars: pd.DataFrame, n_bars: int) -> pd.Series:
    """Trailing dollar-volume imbalance over the last `n_bars` bars,
    computed as (sum(buy) - sum(sell)) / (sum(buy) + sum(sell)) — i.e.
    the ratio of the SUMS, not the mean of per-bar ratios. This matches
    the imbalance_stratify.py convention."""
    buy = bars.buy_dollar_volume
    sell = bars.sell_dollar_volume
    sb = buy.rolling(window=n_bars, min_periods=1).sum()
    ss = sell.rolling(window=n_bars, min_periods=1).sum()
    denom = sb + ss
    out = (sb - ss) / denom.where(denom > 0, np.nan)
    return out.fillna(0.0)


def resample_to(bars: pd.DataFrame, bar_minutes: int) -> pd.DataFrame:
    """Aggregate 1m bars into `bar_minutes`-minute bars, anchored on
    UTC-epoch boundaries (so e.g. 5m bars start at :00, :05, :10 ...).
    Returns a frame with the same columns as the input."""
    if bar_minutes == 1:
        return bars.reset_index(drop=True)
    us_per_bar = bar_minutes * 60_000_000
    g = (bars.start_us // us_per_bar) * us_per_bar
    out = bars.groupby(g, as_index=False, sort=True).agg(
        start_us=("start_us", "first"),
        open=("open", "first"),
        high=("high", "max"),
        low=("low", "min"),
        close=("close", "last"),
        volume=("volume", "sum"),
        buy_dollar_volume=("buy_dollar_volume", "sum"),
        sell_dollar_volume=("sell_dollar_volume", "sum"),
    )
    # The grouping key is what we want for the bucket start, not the
    # first 1m bar's start_us (which could be off by up to bar_minutes-1).
    out["start_us"] = (out.start_us // us_per_bar) * us_per_bar
    return out.reset_index(drop=True)


def chart_one(row: pd.Series, bars_root: str, out_dir: str,
              hours_before: float, hours_after: float,
              bar_minutes: int,
              post_script: str) -> str | None:
    sym = row.symbol
    bars_path = os.path.join(bars_root, f"{sym}.parquet")
    if not os.path.exists(bars_path):
        return None

    entry_us = int(row.entry_us)
    exit_us = int(row.exit_us)
    display_lo = entry_us - int(hours_before * US_PER_HOUR)
    t_hi = entry_us + int(hours_after * US_PER_HOUR)
    # Load 10h of pre-roll so the 10h imbalance MA is fully warmed up
    # by the left edge of the display window.
    fetch_lo = display_lo - 10 * US_PER_HOUR

    bars_1m = load_window(bars_path, fetch_lo, t_hi)
    if bars_1m.empty:
        return None
    bars_full = resample_to(bars_1m, bar_minutes)
    # Trailing imbalance MAs over the full fetched range, slice to
    # display window afterwards. Window length is converted from
    # minutes to bar counts so it stays at 1h / 10h regardless of
    # bar_minutes.
    n_1h  = max(1, 60  // bar_minutes)
    n_10h = max(1, 600 // bar_minutes)
    bars_full["imb_1h"]  = imbalance_ma(bars_full, n_1h)
    bars_full["imb_10h"] = imbalance_ma(bars_full, n_10h)
    bars = bars_full.loc[bars_full.start_us >= display_lo].reset_index(drop=True)
    if bars.empty:
        return None

    t = pd.to_datetime(bars.start_us, unit="us", utc=True)
    entry_dt = pd.to_datetime(entry_us, unit="us", utc=True)
    exit_dt = pd.to_datetime(exit_us, unit="us", utc=True)
    in_window_exit = exit_dt <= t.iloc[-1]

    fig = make_subplots(
        rows=2, cols=1, shared_xaxes=True,
        row_heights=[0.65, 0.35], vertical_spacing=0.05,
        specs=[[{}], [{"secondary_y": True}]],
        subplot_titles=(
            f"{sym} short — entry {entry_dt:%Y-%m-%d %H:%M}  "
            f"({bar_minutes}m bars, ±{hours_before:.0f}/{hours_after:.0f}h)  "
            f"rvol={row.ratio:.2f}  "
            f"net_pnl=${row.net_pnl:+.0f}  "
            f"mfe={row.mfe:+.0f}bp  mae={row.mae:+.0f}bp  "
            f"bars_held(1m)={int(row.bars_held)} "
            f"({row.bucket}, {row.reason})",
            "buy / sell $vol  +  imbalance MA (1h, 10h)",
        ),
    )

    # OHLC candles in top pane (we have open/high/low/close).
    fig.add_trace(
        go.Candlestick(
            x=t, open=bars.open, high=bars.high, low=bars.low, close=bars.close,
            name="price", increasing_line_color="#26a69a",
            decreasing_line_color="#ef5350",
            showlegend=False,
        ),
        row=1, col=1,
    )

    # Entry / exit markers — vertical lines + horizontal price lines.
    fig.add_vline(x=entry_dt, line=dict(color="orange", width=1.5, dash="dash"),
                  row=1, col=1)
    fig.add_hline(y=row.entry_price, line=dict(color="orange", width=1, dash="dot"),
                  row=1, col=1)
    if in_window_exit:
        fig.add_vline(x=exit_dt, line=dict(color="cyan", width=1.5, dash="dash"),
                      row=1, col=1)
        fig.add_hline(y=row.exit_price, line=dict(color="cyan", width=1, dash="dot"),
                      row=1, col=1)
    # Same vlines on lower pane (only entry — exit is often outside window).
    fig.add_vline(x=entry_dt, line=dict(color="orange", width=1.5, dash="dash"),
                  row=2, col=1)
    if in_window_exit:
        fig.add_vline(x=exit_dt, line=dict(color="cyan", width=1.5, dash="dash"),
                      row=2, col=1)

    # Buy / sell dollar volume bars (stacked) on primary y-axis.
    fig.add_trace(
        go.Bar(x=t, y=bars.buy_dollar_volume, name="buy $vol",
               marker_color="#26a69a", opacity=0.7),
        row=2, col=1, secondary_y=False,
    )
    fig.add_trace(
        go.Bar(x=t, y=-bars.sell_dollar_volume, name="sell $vol",
               marker_color="#ef5350", opacity=0.7),
        row=2, col=1, secondary_y=False,
    )
    # Imbalance moving averages on secondary y-axis ([-1, +1]).
    fig.add_trace(
        go.Scatter(x=t, y=bars.imb_1h, name="imb 1h",
                   line=dict(color="#1f77b4", width=1.4),
                   mode="lines"),
        row=2, col=1, secondary_y=True,
    )
    fig.add_trace(
        go.Scatter(x=t, y=bars.imb_10h, name="imb 10h",
                   line=dict(color="#9467bd", width=1.8),
                   mode="lines"),
        row=2, col=1, secondary_y=True,
    )

    fig.update_layout(
        height=720,
        margin=dict(l=60, r=20, t=70, b=40),
        showlegend=True,
        legend=dict(orientation="h", yanchor="bottom", y=1.06, xanchor="right", x=1),
        xaxis_rangeslider_visible=False,
        barmode="relative",
        hovermode="x unified",
    )
    fig.update_yaxes(title_text="price", row=1, col=1)
    # Symmetric left axis on pane 2 so its zero aligns with the
    # imbalance MA's zero on the secondary [-1, +1] axis.
    vol_max = max(float(bars.buy_dollar_volume.max()),
                  float(bars.sell_dollar_volume.max()))
    if vol_max <= 0:
        vol_max = 1.0
    fig.update_yaxes(title_text="$vol  (sell ↓ / buy ↑)",
                     row=2, col=1, secondary_y=False,
                     range=[-vol_max, vol_max], zeroline=True,
                     zerolinecolor="rgba(128,128,128,0.4)", zerolinewidth=1)
    fig.update_yaxes(title_text="imbalance",
                     row=2, col=1, secondary_y=True,
                     range=[-1, 1], zeroline=False)
    fig.update_xaxes(title_text="time (UTC)", row=2, col=1)

    # Filename: keep the order of trips so they're sortable in finder.
    safe_dt = entry_dt.strftime("%Y%m%dT%H%M")
    fname = f"{safe_dt}_{sym}_pnl{int(row.net_pnl):+d}.html"
    out = os.path.join(out_dir, fname)
    os.makedirs(out_dir, exist_ok=True)
    config = {"scrollZoom": True, "displayModeBar": True}
    fig.write_html(out, config=config, post_script=post_script,
                   include_plotlyjs="cdn")
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--trips", default=DEFAULT_TRIPS,
                    help=f"CSV with trips to chart. Default: {DEFAULT_TRIPS}")
    ap.add_argument("--bars-root", default=DEFAULT_BARS_ROOT)
    ap.add_argument("--out-dir", default=DEFAULT_OUT_DIR)
    ap.add_argument("--hours-before", type=float, default=60.0)
    ap.add_argument("--hours-after",  type=float, default=60.0)
    ap.add_argument("--bar-minutes",  type=int, default=5,
                    help="Aggregate 1m bars into this size for charting. Default 5.")
    ap.add_argument("--filter-bucket", default=None,
                    help="If set (e.g. '>=10x'), keep only rows with that bucket.")
    ap.add_argument("--filter-side", default=None,
                    help="If set (e.g. 'short'), keep only that side.")
    ap.add_argument("--limit", type=int, default=None,
                    help="Optional cap on number of charts (debugging).")
    args = ap.parse_args()

    repo = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))
    trips_path = args.trips if os.path.isabs(args.trips) else os.path.join(repo, args.trips)
    bars_root = args.bars_root if os.path.isabs(args.bars_root) else os.path.join(repo, args.bars_root)
    out_dir = args.out_dir if os.path.isabs(args.out_dir) else os.path.join(repo, args.out_dir)

    trips = pd.read_csv(trips_path)
    if args.filter_bucket and "bucket" in trips.columns:
        trips = trips[trips.bucket == args.filter_bucket]
    if args.filter_side and "side" in trips.columns:
        trips = trips[trips.side == args.filter_side]
    if "side" not in trips.columns:
        # Heuristic: synthetic trips file from inspect_high_rvol_shorts is
        # already shorts; nothing to do.
        pass
    if args.limit is not None:
        trips = trips.head(args.limit)
    print(f"Charting {len(trips)} trips from {trips_path}", file=sys.stderr)

    with open(CHART_CONTROLS_JS) as f:
        post_script = f.read()

    n_done, n_missing = 0, 0
    for i, row in enumerate(trips.itertuples(index=False), start=1):
        out = chart_one(pd.Series(row._asdict()), bars_root, out_dir,
                        args.hours_before, args.hours_after,
                        args.bar_minutes, post_script)
        if out is None:
            n_missing += 1
        else:
            n_done += 1
        if i % 20 == 0 or i == len(trips):
            print(f"  [{i}/{len(trips)}] done={n_done} missing={n_missing}", file=sys.stderr)

    print(f"Wrote {n_done} charts to {out_dir}")
    if n_missing:
        print(f"  ({n_missing} skipped — no parquet or empty window)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
