"""Volume-bar chart for crypto perps trades, around a single trade entry.

Reads the raw Binance perps trade tape (parquet, schema:
    price, quantity, timestamp_us, sign
where sign = +1 for buyer-aggressive, -1 for seller-aggressive — the
ground truth our 1m bars derive from).

Builds equal-$volume bars (default $1M / bar), then plots:
  pane 1: VWAP price (line) with entry/exit price lines and
          vertical entry/exit markers.
  pane 2: signed dollar-volume per bar (binance_volume style:
          green if signed>=0, red otherwise).
  pane 3: stacked buy / sell $volume per bar (left axis) +
          1h / 10h trailing imbalance MAs on a secondary axis [-1,+1].
  pane 4: time duration per bar (seconds), reversed axis.

x-axis = cumulative $volume across all bars in the window. This makes
equal-volume periods visually equal — quiet periods compress, busy
periods expand.

The 1h / 10h imbalance MAs are *time-anchored*, not bar-count-anchored:
for each bar we look at the trailing 1h (or 10h) of wall-clock time and
sum buy/sell $vol within it. This preserves the wall-clock semantics
of the MA across volume regimes (a "1h MA" is really 1h of trades, not
some bar-count proxy that drifts with regime).

Use:

    python scripts/crypto/chart_volume_bars.py \\
        --symbol BELUSDT --entry "2024-05-09 10:17" \\
        --hours-before 18 --hours-after 18 \\
        --dollar-per-bar 1000000

The trip metadata (entry/exit price, mfe, mae, etc) is looked up from
the trips_th15_volratio_30d8h.csv file by (symbol, entry_us). If no
match, only the entry vline is drawn.
"""

from __future__ import annotations

import argparse
import os
import sys
from datetime import datetime, timedelta, timezone
from typing import Optional

import numpy as np
import pandas as pd
import plotly.graph_objects as go
from plotly.subplots import make_subplots


SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
CHART_CONTROLS_JS = os.path.join(
    os.path.dirname(SCRIPT_DIR), "visualization", "chart_controls.js"
)
DEFAULT_TAPE_ROOT = "/mnt/d/trading-edge-bulk/crypto/binance/perps"
DEFAULT_TRIPS = "data/crypto/cumsum_z_persistexit/trips_th15_volratio_30d8h.csv"
DEFAULT_OUT_DIR = "logs/charts/individual_trades"

US_PER_HOUR = 3_600_000_000


def load_tape_window(tape_root: str, symbol: str,
                     t_lo_us: int, t_hi_us: int) -> pd.DataFrame:
    """Concatenate per-day trade-tape parquets that overlap the window."""
    sym_dir = os.path.join(tape_root, symbol)
    if not os.path.isdir(sym_dir):
        raise FileNotFoundError(sym_dir)
    lo_dt = datetime.fromtimestamp(t_lo_us / 1e6, timezone.utc)
    hi_dt = datetime.fromtimestamp(t_hi_us / 1e6, timezone.utc)
    days = []
    d = lo_dt.date()
    while d <= hi_dt.date():
        days.append(d)
        d = d + timedelta(days=1)
    parts = []
    for day in days:
        f = os.path.join(sym_dir, f"{symbol}-trades-{day:%Y-%m-%d}.parquet")
        if not os.path.exists(f):
            print(f"  warn: missing tape file {f}", file=sys.stderr)
            continue
        parts.append(pd.read_parquet(f))
    if not parts:
        return pd.DataFrame(columns=["price", "quantity", "timestamp_us", "sign"])
    df = pd.concat(parts, ignore_index=True)
    df = df.loc[(df.timestamp_us >= t_lo_us) & (df.timestamp_us <= t_hi_us)]
    df = df.sort_values("timestamp_us", kind="stable").reset_index(drop=True)
    return df


def build_dollar_bars(df: pd.DataFrame, dollar_per_bar: float) -> list[dict]:
    """Equal-$volume bars from raw trades. Trades that overflow the bar
    threshold split: the partial fills the current bar, the remainder
    rolls into the next bar at the same price/sign/timestamp."""
    if len(df) == 0:
        return []
    p = df.price.to_numpy(dtype=np.float64)
    q = df.quantity.to_numpy(dtype=np.float64)
    t = df.timestamp_us.to_numpy(dtype=np.int64)
    s = df.sign.to_numpy(dtype=np.float64)
    dv = p * q  # dollar volume per trade

    bars = []
    cur = _empty()
    for i in range(len(df)):
        remaining_dv = dv[i]
        price_i = p[i]
        sign_i = s[i]
        ts_i = int(t[i])
        while remaining_dv > 0:
            space = dollar_per_bar - cur["dv"]
            if remaining_dv <= space:
                _add(cur, price_i, remaining_dv, ts_i, sign_i)
                remaining_dv = 0.0
            else:
                if space > 0:
                    _add(cur, price_i, space, ts_i, sign_i)
                    remaining_dv -= space
                bars.append(_finalize(cur))
                cur = _empty()
    if cur["dv"] > 0:
        bars.append(_finalize(cur))
    cum = 0.0
    for b in bars:
        cum += b["dv"]
        b["cum_dv"] = cum
    return bars


def _empty():
    return {"prices": [], "dvs": [], "ts": [], "signs": [], "dv": 0.0}


def _add(b, price, dv, ts, sign):
    b["prices"].append(price)
    b["dvs"].append(dv)
    b["ts"].append(ts)
    b["signs"].append(sign)
    b["dv"] += dv


def _finalize(bar):
    prices = np.asarray(bar["prices"])
    dvs = np.asarray(bar["dvs"])
    signs = np.asarray(bar["signs"])
    total = dvs.sum()
    vwap = float((prices * dvs).sum() / total)
    buy_dv = float(dvs[signs > 0].sum())
    sell_dv = float(dvs[signs < 0].sum())
    return {
        "dv": float(total),
        "vwap": vwap,
        "open": float(prices[0]),
        "high": float(prices.max()),
        "low": float(prices.min()),
        "close": float(prices[-1]),
        "start_us": int(bar["ts"][0]),
        "end_us": int(bar["ts"][-1]),
        "duration_s": (bar["ts"][-1] - bar["ts"][0]) / 1e6,
        "n_trades": len(prices),
        "buy_dv": buy_dv,
        "sell_dv": sell_dv,
        "signed_dv": buy_dv - sell_dv,
    }


def time_anchored_imbalance_ma(bars: list[dict], window_us: int) -> np.ndarray:
    """For each bar, compute (sum_buy - sum_sell)/(sum_buy + sum_sell)
    over all bars whose end_us falls in (current.end_us - window_us,
    current.end_us]. Two-pointer sweep, O(n)."""
    n = len(bars)
    out = np.zeros(n, dtype=np.float64)
    end_us = np.array([b["end_us"] for b in bars], dtype=np.int64)
    buy = np.array([b["buy_dv"] for b in bars], dtype=np.float64)
    sell = np.array([b["sell_dv"] for b in bars], dtype=np.float64)
    cum_buy = np.concatenate([[0.0], np.cumsum(buy)])
    cum_sell = np.concatenate([[0.0], np.cumsum(sell)])

    j = 0
    for i in range(n):
        cutoff = end_us[i] - window_us
        while j < n and end_us[j] <= cutoff:
            j += 1
        sb = cum_buy[i + 1] - cum_buy[j]
        ss = cum_sell[i + 1] - cum_sell[j]
        denom = sb + ss
        out[i] = (sb - ss) / denom if denom > 0 else 0.0
    return out


def lookup_trip(trips_path: str, symbol: str,
                entry_us: int, tol_us: int = 60 * 60 * 1_000_000
                ) -> Optional[pd.Series]:
    """Find the matching row in the trips CSV (within `tol_us` of the
    requested entry_us). Returns None if nothing close."""
    if not os.path.exists(trips_path):
        return None
    trips = pd.read_csv(trips_path)
    sub = trips[trips.symbol == symbol].copy()
    if sub.empty:
        return None
    sub["delta"] = (sub.entry_us - entry_us).abs()
    sub = sub.sort_values("delta")
    best = sub.iloc[0]
    if best.delta > tol_us:
        return None
    return best


def parse_entry_us(entry_str: str) -> int:
    """Accept either a full ISO timestamp or 'YYYY-MM-DD HH:MM[:SS]'
    in UTC. Returns microseconds since epoch."""
    fmt_options = ["%Y-%m-%d %H:%M:%S", "%Y-%m-%d %H:%M", "%Y-%m-%dT%H:%M:%S",
                   "%Y-%m-%dT%H:%M"]
    for fmt in fmt_options:
        try:
            dt = datetime.strptime(entry_str, fmt).replace(tzinfo=timezone.utc)
            return int(dt.timestamp() * 1e6)
        except ValueError:
            continue
    raise ValueError(f"Could not parse entry timestamp: {entry_str!r}")


def plot(bars: list[dict], symbol: str, entry_us: int, trip: Optional[pd.Series],
         dollar_per_bar: float, hours_before: float, hours_after: float,
         out_path: str, post_script: str) -> None:
    if not bars:
        raise RuntimeError("no bars in window")

    cum_dv = np.array([b["cum_dv"] for b in bars])
    vwap = np.array([b["vwap"] for b in bars])
    durations = np.array([b["duration_s"] for b in bars])
    signed_dv = np.array([b["signed_dv"] for b in bars])
    buy_dv = np.array([b["buy_dv"] for b in bars])
    sell_dv = np.array([b["sell_dv"] for b in bars])
    end_us = np.array([b["end_us"] for b in bars], dtype=np.int64)

    imb_1h = time_anchored_imbalance_ma(bars, 1 * US_PER_HOUR)
    imb_10h = time_anchored_imbalance_ma(bars, 10 * US_PER_HOUR)

    # Bar widths for the price/signed/vol bars: 80% of the bar's $ volume.
    widths = np.array([b["dv"] for b in bars]) * 0.8

    # Per-bar tooltip metadata, attached to all primary traces so any
    # pane shows the same rich context.
    def _fmt_dt(us: int) -> str:
        return datetime.fromtimestamp(us / 1e6, timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
    customdata = [
        [
            i,                                  # 0  bar index
            b["cum_dv"],                        # 1  cum $vol
            b["dv"],                            # 2  bar $vol
            b["vwap"],                          # 3  vwap
            b["open"],                          # 4  open
            b["high"],                          # 5  high
            b["low"],                           # 6  low
            b["close"],                         # 7  close
            b["buy_dv"],                        # 8  buy $vol
            b["sell_dv"],                       # 9  sell $vol
            b["signed_dv"],                     # 10 signed $vol
            (100.0 * b["signed_dv"] / b["dv"]) if b["dv"] > 0 else 0.0,
                                                # 11 imbalance %
            b["n_trades"],                      # 12 n trades
            b["duration_s"],                    # 13 duration s
            _fmt_dt(b["start_us"]),             # 14 start dt
            _fmt_dt(b["end_us"]),               # 15 end dt
        ]
        for i, b in enumerate(bars)
    ]
    bar_hover = (
        "<b>bar #%{customdata[0]}</b>  cum_dv $%{customdata[1]:,.0f}<br>"
        "<b>%{customdata[14]}</b> → %{customdata[15]}<br>"
        "duration: %{customdata[13]:.1f}s  trades: %{customdata[12]:,}<br>"
        "<br>"
        "OHLC: %{customdata[4]:,.6g} / %{customdata[5]:,.6g} / "
        "%{customdata[6]:,.6g} / %{customdata[7]:,.6g}<br>"
        "VWAP: %{customdata[3]:,.6g}<br>"
        "<br>"
        "buy $vol:  $%{customdata[8]:,.0f}<br>"
        "sell $vol: $%{customdata[9]:,.0f}<br>"
        "signed:    $%{customdata[10]:+,.0f}  (%{customdata[11]:+.1f}%)<br>"
        "<extra></extra>"
    )

    # Find the cum_dv x-position closest to entry/exit_us (mark line goes
    # at the *start* of the bar that contains the moment, i.e. the one
    # whose end_us > entry_us if any; otherwise the last bar before).
    def cum_at(us: int) -> Optional[float]:
        idx = np.searchsorted(end_us, us, side="left")
        if idx >= len(bars):
            return None
        b = bars[idx]
        # Position the marker at the start of the bar containing the moment.
        return b["cum_dv"] - b["dv"]

    entry_x = cum_at(entry_us)
    if trip is not None:
        exit_x = cum_at(int(trip.exit_us))
    else:
        exit_x = None

    entry_dt = datetime.fromtimestamp(entry_us / 1e6, timezone.utc)
    title_extra = ""
    if trip is not None:
        title_extra = (f"  rvol={trip.ratio:.2f}  net_pnl=${trip.net_pnl:+.0f}  "
                       f"mfe={trip.mfe:+.0f}bp  mae={trip.mae:+.0f}bp  "
                       f"bars_held(1m)={int(trip.bars_held)}")
    title = (f"{symbol} short — entry {entry_dt:%Y-%m-%d %H:%M} UTC  "
             f"(${dollar_per_bar:,.0f}/bar, ±{hours_before:.0f}/{hours_after:.0f}h)"
             + title_extra)

    fig = make_subplots(
        rows=4, cols=1, shared_xaxes=True,
        row_heights=[0.46, 0.18, 0.22, 0.14],
        vertical_spacing=0.035,
        specs=[[{}], [{}], [{"secondary_y": True}], [{}]],
        subplot_titles=(
            title,
            "signed $vol per bar (buy − sell)",
            "buy / sell $vol  +  imbalance MA (1h, 10h)",
            "duration per bar (s)",
        ),
    )

    # --- Pane 1: VWAP line.
    fig.add_trace(
        go.Scatter(x=cum_dv, y=vwap, name="VWAP",
                   line=dict(color="#1f77b4", width=1.4), mode="lines",
                   customdata=customdata, hovertemplate=bar_hover),
        row=1, col=1,
    )
    if entry_x is not None:
        fig.add_vline(x=entry_x, line=dict(color="orange", width=1.5, dash="dash"),
                      row=1, col=1)
    if trip is not None:
        fig.add_hline(y=trip.entry_price,
                      line=dict(color="orange", width=1, dash="dot"), row=1, col=1)
        if exit_x is not None:
            fig.add_vline(x=exit_x, line=dict(color="cyan", width=1.5, dash="dash"),
                          row=1, col=1)
            fig.add_hline(y=trip.exit_price,
                          line=dict(color="cyan", width=1, dash="dot"), row=1, col=1)

    # --- Pane 2: signed flow per bar (binance_volume style colors).
    sf_colors = ["#26a69a" if s >= 0 else "#ef5350" for s in signed_dv]
    fig.add_trace(
        go.Bar(x=cum_dv, y=signed_dv, marker_color=sf_colors,
               marker_line_width=0, width=widths,
               name="signed $vol",
               customdata=customdata, hovertemplate=bar_hover),
        row=2, col=1,
    )
    fig.add_hline(y=0.0, line=dict(color="rgba(128,128,128,0.5)", width=1, dash="dash"),
                  row=2, col=1)
    if entry_x is not None:
        fig.add_vline(x=entry_x, line=dict(color="orange", width=1.5, dash="dash"),
                      row=2, col=1)
    if exit_x is not None:
        fig.add_vline(x=exit_x, line=dict(color="cyan", width=1.5, dash="dash"),
                      row=2, col=1)

    # --- Pane 3: stacked buy/sell + imbalance MAs.
    fig.add_trace(
        go.Bar(x=cum_dv, y=buy_dv, name="buy $vol",
               marker_color="#26a69a", marker_line_width=0,
               width=widths, opacity=0.7,
               customdata=customdata, hovertemplate=bar_hover),
        row=3, col=1, secondary_y=False,
    )
    fig.add_trace(
        go.Bar(x=cum_dv, y=-sell_dv, name="sell $vol",
               marker_color="#ef5350", marker_line_width=0,
               width=widths, opacity=0.7,
               customdata=customdata, hovertemplate=bar_hover),
        row=3, col=1, secondary_y=False,
    )
    fig.add_trace(
        go.Scatter(x=cum_dv, y=imb_1h, name="imb 1h",
                   line=dict(color="#1f77b4", width=1.3), mode="lines",
                   hovertemplate="imb 1h: %{y:+.3f}<extra></extra>"),
        row=3, col=1, secondary_y=True,
    )
    fig.add_trace(
        go.Scatter(x=cum_dv, y=imb_10h, name="imb 10h",
                   line=dict(color="#9467bd", width=1.7), mode="lines",
                   hovertemplate="imb 10h: %{y:+.3f}<extra></extra>"),
        row=3, col=1, secondary_y=True,
    )
    if entry_x is not None:
        fig.add_vline(x=entry_x, line=dict(color="orange", width=1.5, dash="dash"),
                      row=3, col=1)
    if exit_x is not None:
        fig.add_vline(x=exit_x, line=dict(color="cyan", width=1.5, dash="dash"),
                      row=3, col=1)

    # --- Pane 4: bar duration.
    fig.add_trace(
        go.Scatter(x=cum_dv, y=durations, fill="tozeroy", mode="lines",
                   name="bar duration",
                   line=dict(color="#1f77b4", width=1),
                   fillcolor="rgba(31,119,180,0.25)",
                   customdata=customdata, hovertemplate=bar_hover),
        row=4, col=1,
    )

    fig.update_layout(
        height=1100,
        margin=dict(l=70, r=20, t=70, b=40),
        barmode="relative",
        hovermode="closest",
        hoverdistance=50,
        legend=dict(orientation="h", yanchor="bottom", y=1.04, xanchor="right", x=1),
        showlegend=True,
    )
    fig.update_yaxes(title_text="VWAP", row=1, col=1)
    fig.update_yaxes(title_text="signed $vol", row=2, col=1)
    # Left axis on pane 3: force symmetric range so its zero aligns with
    # the imbalance MA's zero on the secondary axis. Without this the
    # buy/sell-stack auto-range can put zero off-center, making the MA
    # lines look offset from the [-1,+1] mid.
    vol_max = max(float(np.abs(buy_dv).max()), float(np.abs(sell_dv).max()))
    if vol_max <= 0:
        vol_max = 1.0
    fig.update_yaxes(title_text="$vol  (sell ↓ / buy ↑)",
                     row=3, col=1, secondary_y=False,
                     range=[-vol_max, vol_max], zeroline=True,
                     zerolinecolor="rgba(128,128,128,0.4)", zerolinewidth=1)
    fig.update_yaxes(title_text="imbalance",
                     row=3, col=1, secondary_y=True,
                     range=[-1, 1], zeroline=False)
    fig.update_yaxes(title_text="seconds", autorange="reversed", row=4, col=1)
    fig.update_xaxes(title_text="cumulative $volume", row=4, col=1)

    config = {"scrollZoom": True, "displayModeBar": True}
    fig.write_html(out_path, config=config, post_script=post_script,
                   include_plotlyjs="cdn")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--symbol", required=True)
    ap.add_argument("--entry", required=True,
                    help="Entry datetime, UTC. e.g. '2024-05-09 10:17'")
    ap.add_argument("--hours-before", type=float, default=18.0)
    ap.add_argument("--hours-after",  type=float, default=18.0)
    ap.add_argument("--dollar-per-bar", type=float, default=1_000_000.0,
                    help="$volume per bar. Default $1M.")
    ap.add_argument("--tape-root", default=DEFAULT_TAPE_ROOT)
    ap.add_argument("--trips", default=DEFAULT_TRIPS)
    ap.add_argument("--out-dir", default=DEFAULT_OUT_DIR)
    args = ap.parse_args()

    repo = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))
    trips_path = args.trips if os.path.isabs(args.trips) else os.path.join(repo, args.trips)
    out_dir = args.out_dir if os.path.isabs(args.out_dir) else os.path.join(repo, args.out_dir)
    os.makedirs(out_dir, exist_ok=True)

    entry_us = parse_entry_us(args.entry)
    t_lo = entry_us - int(args.hours_before * US_PER_HOUR)
    t_hi = entry_us + int(args.hours_after * US_PER_HOUR)
    print(f"Loading {args.symbol} tape over "
          f"{datetime.fromtimestamp(t_lo/1e6, timezone.utc):%Y-%m-%d %H:%M} → "
          f"{datetime.fromtimestamp(t_hi/1e6, timezone.utc):%Y-%m-%d %H:%M} UTC")
    df = load_tape_window(args.tape_root, args.symbol, t_lo, t_hi)
    print(f"  loaded {len(df):,} trades")
    if len(df) == 0:
        print("  no trades; aborting", file=sys.stderr)
        return 1

    bars = build_dollar_bars(df, args.dollar_per_bar)
    total_dv = sum(b["dv"] for b in bars)
    print(f"  built {len(bars):,} bars at ${args.dollar_per_bar:,.0f}/bar  "
          f"(total ${total_dv:,.0f})")

    trip = lookup_trip(trips_path, args.symbol, entry_us)
    if trip is None:
        print(f"  no matching trip in {trips_path}; entry-only chart")
    else:
        print(f"  matched trip: net_pnl={trip.net_pnl:+.0f}  rvol={trip.ratio:.2f}")

    with open(CHART_CONTROLS_JS) as f:
        post_script = f.read()

    entry_dt = datetime.fromtimestamp(entry_us / 1e6, timezone.utc)
    fname = (f"{entry_dt:%Y%m%dT%H%M}_{args.symbol}_volbars.html")
    out = os.path.join(out_dir, fname)
    plot(bars, args.symbol, entry_us, trip,
         args.dollar_per_bar, args.hours_before, args.hours_after,
         out, post_script)
    print(f"Saved to {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
