"""Binance trade-data volume-bar chart.

Reads the raw Binance public-data CSV (no header, columns:
trade_id, price, quantity, quote_quantity, timestamp_us, isBuyerMaker, isBestMatch)
and produces a volume-bucketed VWAP ±2σ chart with a signed-flow subpanel.

The signed flow is ground truth: isBuyerMaker = True means the aggressor was
the seller (market sell), False means the aggressor was the buyer (market buy).
"""

import argparse
import os
from datetime import datetime, timezone

import numpy as np
import pandas as pd
import plotly.graph_objects as go
from plotly.subplots import make_subplots


def load_trades(path):
    cols = [
        "trade_id",
        "price",
        "quantity",
        "quote_quantity",
        "timestamp",
        "is_buyer_maker",
        "is_best_match",
    ]
    df = pd.read_csv(
        path,
        header=None,
        names=cols,
        dtype={
            "trade_id": np.int64,
            "price": np.float64,
            "quantity": np.float64,
            "quote_quantity": np.float64,
            "timestamp": np.int64,
            "is_buyer_maker": str,
            "is_best_match": str,
        },
    )
    df["is_buyer_maker"] = df["is_buyer_maker"].map({"True": True, "False": False})
    # Ensure time-sorted (Binance files are by trade_id, which is also time-ordered
    # after the known 2019 reset; assert to be safe).
    if not (df["timestamp"].values[1:] >= df["timestamp"].values[:-1]).all():
        df = df.sort_values("timestamp", kind="stable").reset_index(drop=True)
    return df


def load_posteriors(path, n_trades, mode):
    """Read the HMM posterior CSV. Modern format has both smoothed and
    filtered columns (p_up, p_consol, p_down, p_up_filt, p_consol_filt,
    p_down_filt). Legacy format had only smoothed (3 columns).

    `mode` ∈ {"smoothed", "filtered"}. Filtered uses only past evidence and
    is the right answer for live-trading visualization; smoothed cheats with
    future evidence and is the right answer for retrospective analysis.

    Returns (p_up, p_consol, p_down) padded so the arrays align index-for-index
    with the trade list (HMM output starts at trade index 1; we duplicate
    row 0 to cover trade 0)."""
    df = pd.read_csv(path)
    cols = list(df.columns)
    smoothed_cols = ["p_up", "p_consol", "p_down"]
    filtered_cols = ["p_up_filt", "p_consol_filt", "p_down_filt"]
    if mode == "smoothed":
        chosen = smoothed_cols
    elif mode == "filtered":
        if not all(c in cols for c in filtered_cols):
            raise ValueError(
                f"posterior CSV is missing filtered columns; got {cols}. "
                "Re-run inference to produce a CSV with filtered columns."
            )
        chosen = filtered_cols
    else:
        raise ValueError(f"unknown posterior mode: {mode}")
    p = df[chosen].to_numpy(dtype=np.float64)
    if len(p) == n_trades - 1:
        p = np.concatenate([p[:1], p], axis=0)
    elif len(p) != n_trades:
        raise ValueError(
            f"posterior rows ({len(p)}) don't align with trade count ({n_trades})"
        )
    return p[:, 0], p[:, 1], p[:, 2]


def build_volume_bars(df, volume_per_bar, posteriors=None):
    """Volume-bucketed bars. Trades overflow into the next bar when they don't
    fit; the overflow slice contributes to both bars, volume-weighted.

    If posteriors is given (a tuple of 3 arrays p_up, p_consol, p_down each of
    length len(df)), each bar gets a volume-weighted posterior mean."""
    prices = df["price"].to_numpy()
    qtys = df["quantity"].to_numpy()
    ts_us = df["timestamp"].to_numpy()
    buyer_maker = df["is_buyer_maker"].to_numpy()
    # Signed aggression: +1 for buyer-aggressive (is_buyer_maker=False), -1 for sell.
    signs = np.where(buyer_maker, -1.0, 1.0)
    has_post = posteriors is not None
    p_up_arr = posteriors[0] if has_post else None
    p_con_arr = posteriors[1] if has_post else None
    p_dn_arr = posteriors[2] if has_post else None

    bars = []
    cur = _empty_bar()
    for i in range(len(df)):
        remaining = float(qtys[i])
        price = float(prices[i])
        ts = int(ts_us[i])
        sign = float(signs[i])
        p_up = float(p_up_arr[i]) if has_post else 0.0
        p_con = float(p_con_arr[i]) if has_post else 0.0
        p_dn = float(p_dn_arr[i]) if has_post else 0.0
        while remaining > 0:
            space = volume_per_bar - cur["vol"]
            if remaining <= space:
                _add(cur, price, remaining, ts, sign, p_up, p_con, p_dn)
                remaining = 0
            else:
                if space > 0:
                    _add(cur, price, space, ts, sign, p_up, p_con, p_dn)
                    remaining -= space
                bars.append(_finalize(cur, has_post))
                cur = _empty_bar()
    if cur["vol"] > 0:
        bars.append(_finalize(cur, has_post))

    cum = 0.0
    for b in bars:
        cum += b["volume"]
        b["cumulative_volume"] = cum
    return bars


def _empty_bar():
    return {
        "prices": [],
        "vols": [],
        "ts": [],
        "signs": [],
        "p_ups": [],
        "p_cons": [],
        "p_dns": [],
        "vol": 0.0,
    }


def _add(bar, price, vol, ts, sign, p_up, p_con, p_dn):
    bar["prices"].append(price)
    bar["vols"].append(vol)
    bar["ts"].append(ts)
    bar["signs"].append(sign)
    bar["p_ups"].append(p_up)
    bar["p_cons"].append(p_con)
    bar["p_dns"].append(p_dn)
    bar["vol"] += vol


def _finalize(bar, has_post):
    prices = np.asarray(bar["prices"])
    vols = np.asarray(bar["vols"])
    signs = np.asarray(bar["signs"])
    total_v = vols.sum()
    vwap = float(np.sum(prices * vols) / total_v)
    var = float(np.sum(vols * (prices - vwap) ** 2) / total_v)
    stddev = float(np.sqrt(max(0.0, var)))
    signed_vol = float(np.sum(signs * vols))
    buy_vol = float(np.sum(vols[signs > 0]))
    sell_vol = float(np.sum(vols[signs < 0]))
    out = {
        "volume": float(total_v),
        "vwap": vwap,
        "stddev": stddev,
        "start_us": int(bar["ts"][0]),
        "end_us": int(bar["ts"][-1]),
        "time_duration_s": (bar["ts"][-1] - bar["ts"][0]) / 1e6,
        "num_trades": len(bar["prices"]),
        "signed_volume": signed_vol,
        "buy_volume": buy_vol,
        "sell_volume": sell_vol,
    }
    if has_post:
        p_ups = np.asarray(bar["p_ups"])
        p_cons = np.asarray(bar["p_cons"])
        p_dns = np.asarray(bar["p_dns"])
        out["p_up"] = float(np.sum(p_ups * vols) / total_v)
        out["p_consol"] = float(np.sum(p_cons * vols) / total_v)
        out["p_down"] = float(np.sum(p_dns * vols) / total_v)
    return out


def us_to_datetime(us):
    return datetime.fromtimestamp(us / 1e6, timezone.utc)


def fmt(us):
    return us_to_datetime(us).strftime("%H:%M:%S.%f")[:-3]


def plot(bars, output_html, volume_per_bar, title):
    has_post = "p_up" in bars[0]
    if has_post:
        rows = 4
        row_heights = [0.45, 0.20, 0.20, 0.15]
        titles = [
            f"VWAP ±2σ ({volume_per_bar:g} BTC/bar)",
            "Signed flow per bar (buy − sell, BTC)",
            "Per-bar posterior P(state)",
            "Time per bar (seconds)",
        ]
        time_row = 4
    else:
        rows = 3
        row_heights = [0.55, 0.25, 0.20]
        titles = [
            f"VWAP ±2σ ({volume_per_bar:g} BTC/bar)",
            "Signed flow per bar (buy − sell, BTC)",
            "Time per bar (seconds)",
        ]
        time_row = 3
    fig = make_subplots(
        rows=rows,
        cols=1,
        shared_xaxes=True,
        vertical_spacing=0.04,
        row_heights=row_heights,
        subplot_titles=titles,
    )

    x_vals = [b["cumulative_volume"] for b in bars]
    vwap_vals = [b["vwap"] for b in bars]
    upper = [b["vwap"] + 2 * b["stddev"] for b in bars]
    lower = [b["vwap"] - 2 * b["stddev"] for b in bars]
    durations = [b["time_duration_s"] for b in bars]
    signed_vol = [b["signed_volume"] for b in bars]

    total_volume = sum(b["volume"] for b in bars)
    session_vwap = (
        sum(b["vwap"] * b["volume"] for b in bars) / total_volume if total_volume > 0 else 1.0
    )
    min_height = min(0.01, 0.001 * session_vwap)
    heights, bases = [], []
    for u, l, v in zip(upper, lower, vwap_vals):
        h = max(u - l, min_height)
        heights.append(h)
        bases.append(v - h / 2)

    colors = []
    for i, b in enumerate(bars):
        if i == 0 or b["vwap"] >= bars[i - 1]["vwap"]:
            colors.append("green")
        else:
            colors.append("red")

    customdata = [
        [
            b["cumulative_volume"],
            b["vwap"],
            b["stddev"],
            b["vwap"] + 2 * b["stddev"],
            b["vwap"] - 2 * b["stddev"],
            fmt(b["start_us"]),
            fmt(b["end_us"]),
            b["time_duration_s"],
            b["num_trades"],
            b["buy_volume"],
            b["sell_volume"],
            b["signed_volume"],
            100.0 * b.get("p_up", 0.0),
            100.0 * b.get("p_consol", 0.0),
            100.0 * b.get("p_down", 0.0),
        ]
        for b in bars
    ]

    posterior_hover = (
        "<b>P(Up):</b> %{customdata[12]:.2f}%<br>"
        "<b>P(Consol):</b> %{customdata[13]:.2f}%<br>"
        "<b>P(Down):</b> %{customdata[14]:.2f}%<br>"
        if has_post
        else ""
    )

    fig.add_trace(
        go.Bar(
            x=x_vals,
            y=heights,
            base=bases,
            name="VWAP ±2σ",
            marker_color=colors,
            marker_line_width=0,
            width=[b["volume"] * 0.8 for b in bars],
            customdata=customdata,
            hovertemplate=(
                "<b>Cum Vol:</b> %{customdata[0]:,.2f}<br>"
                "<b>VWAP:</b> %{customdata[1]:,.2f}<br>"
                "<b>StdDev:</b> %{customdata[2]:.4f}<br>"
                "<b>+2σ:</b> %{customdata[3]:,.2f}<br>"
                "<b>-2σ:</b> %{customdata[4]:,.2f}<br>"
                "<b>Start:</b> %{customdata[5]}<br>"
                "<b>End:</b> %{customdata[6]}<br>"
                "<b>Duration:</b> %{customdata[7]:.3f}s<br>"
                "<b>Trades:</b> %{customdata[8]}<br>"
                "<b>Buy vol:</b> %{customdata[9]:.3f}<br>"
                "<b>Sell vol:</b> %{customdata[10]:.3f}<br>"
                "<b>Signed vol:</b> %{customdata[11]:+.3f}<br>"
                + posterior_hover
                + "<extra></extra>"
            ),
        ),
        row=1,
        col=1,
    )
    fig.add_trace(
        go.Scatter(
            x=x_vals,
            y=vwap_vals,
            mode="lines",
            name="VWAP",
            line=dict(color="blue", width=1),
            hoverinfo="skip",
        ),
        row=1,
        col=1,
    )

    # Signed-flow bars: green for positive (buyer-aggressive), red for negative.
    sf_colors = ["green" if s >= 0 else "crimson" for s in signed_vol]
    fig.add_trace(
        go.Bar(
            x=x_vals,
            y=signed_vol,
            name="Signed flow",
            marker_color=sf_colors,
            marker_line_width=0,
            width=[b["volume"] * 0.8 for b in bars],
            hovertemplate="Signed vol: %{y:+.3f}<extra></extra>",
        ),
        row=2,
        col=1,
    )
    fig.add_hline(y=0.0, line_width=1, line_dash="dash", line_color="black",
                  row=2, col=1)

    if has_post:
        p_up = [b["p_up"] for b in bars]
        p_consol = [b["p_consol"] for b in bars]
        p_down = [b["p_down"] for b in bars]
        # Stack order is bottom-to-top: Down, Consol, Up. Each column sums to 1.
        fig.add_trace(
            go.Scatter(
                x=x_vals, y=p_down, name="P(Down)", mode="lines",
                line=dict(width=0), stackgroup="post",
                fillcolor="rgba(220,60,60,0.75)",
                hovertemplate="P(Down): %{y:.3f}<extra></extra>",
            ),
            row=3, col=1,
        )
        fig.add_trace(
            go.Scatter(
                x=x_vals, y=p_consol, name="P(Consol)", mode="lines",
                line=dict(width=0), stackgroup="post",
                fillcolor="rgba(160,160,160,0.65)",
                hovertemplate="P(Consol): %{y:.3f}<extra></extra>",
            ),
            row=3, col=1,
        )
        fig.add_trace(
            go.Scatter(
                x=x_vals, y=p_up, name="P(Up)", mode="lines",
                line=dict(width=0), stackgroup="post",
                fillcolor="rgba(60,160,60,0.75)",
                hovertemplate="P(Up): %{y:.3f}<extra></extra>",
            ),
            row=3, col=1,
        )

    fig.add_trace(
        go.Scatter(
            x=x_vals,
            y=durations,
            fill="tozeroy",
            mode="lines",
            name="Time Duration",
            line=dict(color="blue", width=1),
            fillcolor="rgba(0, 0, 255, 0.3)",
            hovertemplate="Cum Vol: %{x:,.2f}<br>Duration: %{y:.3f}s<extra></extra>",
        ),
        row=time_row,
        col=1,
    )

    fig.update_layout(
        title=title,
        height=1000 if has_post else 900,
        width=1600,
        hovermode="closest",
        hoverdistance=50,
        showlegend=False,
    )
    fig.update_xaxes(rangeslider_visible=False, row=1, col=1)
    fig.update_xaxes(title_text="Cumulative Volume (BTC)", row=time_row, col=1)
    fig.update_yaxes(title_text="Price", row=1, col=1)
    fig.update_yaxes(title_text="Signed vol", row=2, col=1)
    if has_post:
        fig.update_yaxes(title_text="P(state)", range=[0, 1], row=3, col=1)
    fig.update_yaxes(title_text="Seconds", autorange="reversed", row=time_row, col=1)

    config = {"scrollZoom": True, "displayModeBar": True}
    script_dir = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(script_dir, "chart_controls.js")) as f:
        post_script = f.read()
    fig.write_html(output_html, config=config, post_script=post_script)
    print(f"Saved to {output_html}")


def main():
    ap = argparse.ArgumentParser(
        description="Binance trade-data volume-bar chart with signed flow"
    )
    ap.add_argument("csv", help="Binance trade CSV (raw, no header)")
    ap.add_argument("-v", "--volume-per-bar", type=float, default=18.0,
                    help="Volume per bar in base asset units (default 18 BTC)")
    ap.add_argument("-o", "--output", help="Output HTML path")
    ap.add_argument("--posterior",
                    help="Optional HMM posterior CSV (must contain filtered + smoothed columns)")
    ap.add_argument("--mode", choices=["filtered", "smoothed"], default="filtered",
                    help="Which posterior to overlay (default: filtered, real-time honest)")
    args = ap.parse_args()

    base = os.path.splitext(os.path.basename(args.csv))[0]
    if args.posterior:
        suffix = f"_volume_posterior_{args.mode}"
    else:
        suffix = "_volume"
    out = args.output or f"logs/{base}{suffix}.html"
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)

    print(f"Loading {args.csv}...")
    df = load_trades(args.csv)
    print(f"Loaded {len(df):,} trades")
    posteriors = None
    if args.posterior:
        print(f"Loading posterior {args.posterior} (mode: {args.mode})...")
        posteriors = load_posteriors(args.posterior, len(df), args.mode)
    bars = build_volume_bars(df, args.volume_per_bar, posteriors=posteriors)
    total_vol = sum(b["volume"] for b in bars)
    print(f"Built {len(bars):,} bars of {args.volume_per_bar:g} BTC (total {total_vol:,.2f})")
    title = f"BTCUSDT volume bars — {base}"
    if args.posterior:
        title += f" — posterior: {args.mode}"
    plot(bars, out, args.volume_per_bar, title)


if __name__ == "__main__":
    main()
