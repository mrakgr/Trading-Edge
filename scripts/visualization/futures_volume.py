"""Volume-bar chart for futures trade data downloaded via databento_download.py.

Modeled on binance_volume.py minus the HMM-posterior path. Reads the
6-column CSV (no header): trade_id, price, quantity, quote_quantity,
timestamp_us, is_buyer_maker. Builds constant-volume bars with VWAP+/-2 sigma,
signed-flow subpanel, and per-bar duration subpanel.
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
        },
    )
    df["is_buyer_maker"] = df["is_buyer_maker"].map({"True": True, "False": False})
    if not (df["timestamp"].values[1:] >= df["timestamp"].values[:-1]).all():
        df = df.sort_values("timestamp", kind="stable").reset_index(drop=True)
    return df


def build_volume_bars(df, volume_per_bar):
    prices = df["price"].to_numpy()
    qtys = df["quantity"].to_numpy()
    ts_us = df["timestamp"].to_numpy()
    buyer_maker = df["is_buyer_maker"].to_numpy()
    signs = np.where(buyer_maker, -1.0, 1.0)

    bars = []
    cur = _empty_bar()
    for i in range(len(df)):
        remaining = float(qtys[i])
        price = float(prices[i])
        ts = int(ts_us[i])
        sign = float(signs[i])
        while remaining > 0:
            space = volume_per_bar - cur["vol"]
            if remaining <= space:
                _add(cur, price, remaining, ts, sign)
                remaining = 0
            else:
                if space > 0:
                    _add(cur, price, space, ts, sign)
                    remaining -= space
                bars.append(_finalize(cur))
                cur = _empty_bar()
    if cur["vol"] > 0:
        bars.append(_finalize(cur))

    cum = 0.0
    for b in bars:
        cum += b["volume"]
        b["cumulative_volume"] = cum
    return bars


def _empty_bar():
    return {"prices": [], "vols": [], "ts": [], "signs": [], "vol": 0.0}


def _add(bar, price, vol, ts, sign):
    bar["prices"].append(price)
    bar["vols"].append(vol)
    bar["ts"].append(ts)
    bar["signs"].append(sign)
    bar["vol"] += vol


def _finalize(bar):
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
    return {
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


def fmt(us):
    return datetime.fromtimestamp(us / 1e6, timezone.utc).strftime("%H:%M:%S.%f")[:-3]


def plot(bars, output_html, volume_per_bar, title):
    fig = make_subplots(
        rows=3,
        cols=1,
        shared_xaxes=True,
        vertical_spacing=0.04,
        row_heights=[0.55, 0.25, 0.20],
        subplot_titles=[
            f"VWAP +/-2 sigma ({volume_per_bar:g} contracts/bar)",
            "Signed flow per bar (buy - sell, contracts)",
            "Time per bar (seconds)",
        ],
    )

    x_vals = [b["cumulative_volume"] for b in bars]
    vwap_vals = [b["vwap"] for b in bars]
    upper = [b["vwap"] + 2 * b["stddev"] for b in bars]
    lower = [b["vwap"] - 2 * b["stddev"] for b in bars]
    durations = [b["time_duration_s"] for b in bars]
    signed_vol = [b["signed_volume"] for b in bars]

    raw_heights = [u - l for u, l in zip(upper, lower)]
    positive = [h for h in raw_heights if h > 0]
    median_h = float(np.median(positive)) if positive else 0.0
    min_height = 0.25 * median_h if median_h > 0 else 0.0
    heights, bases = [], []
    for h, v in zip(raw_heights, vwap_vals):
        h = max(h, min_height)
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
        ]
        for b in bars
    ]

    fig.add_trace(
        go.Bar(
            x=x_vals,
            y=heights,
            base=bases,
            name="VWAP +/-2 sigma",
            marker_color=colors,
            marker_line_width=0,
            width=[b["volume"] * 0.8 for b in bars],
            customdata=customdata,
            hovertemplate=(
                "<b>Cum Vol:</b> %{customdata[0]:,.0f}<br>"
                "<b>VWAP:</b> %{customdata[1]:,.4f}<br>"
                "<b>StdDev:</b> %{customdata[2]:.4f}<br>"
                "<b>+2 sigma:</b> %{customdata[3]:,.4f}<br>"
                "<b>-2 sigma:</b> %{customdata[4]:,.4f}<br>"
                "<b>Start:</b> %{customdata[5]} UTC<br>"
                "<b>End:</b> %{customdata[6]} UTC<br>"
                "<b>Duration:</b> %{customdata[7]:.3f}s<br>"
                "<b>Trades:</b> %{customdata[8]}<br>"
                "<b>Buy vol:</b> %{customdata[9]:.0f}<br>"
                "<b>Sell vol:</b> %{customdata[10]:.0f}<br>"
                "<b>Signed vol:</b> %{customdata[11]:+.0f}<br>"
                "<extra></extra>"
            ),
        ),
        row=1, col=1,
    )
    fig.add_trace(
        go.Scatter(
            x=x_vals, y=vwap_vals, mode="lines", name="VWAP",
            line=dict(color="blue", width=1), hoverinfo="skip",
        ),
        row=1, col=1,
    )

    sf_colors = ["green" if s >= 0 else "crimson" for s in signed_vol]
    fig.add_trace(
        go.Bar(
            x=x_vals, y=signed_vol, name="Signed flow",
            marker_color=sf_colors, marker_line_width=0,
            width=[b["volume"] * 0.8 for b in bars],
            hovertemplate="Signed vol: %{y:+.0f}<extra></extra>",
        ),
        row=2, col=1,
    )
    fig.add_hline(y=0.0, line_width=1, line_dash="dash", line_color="black", row=2, col=1)

    fig.add_trace(
        go.Scatter(
            x=x_vals, y=durations, fill="tozeroy", mode="lines", name="Time Duration",
            line=dict(color="blue", width=1), fillcolor="rgba(0, 0, 255, 0.3)",
            hovertemplate="Cum Vol: %{x:,.0f}<br>Duration: %{y:.3f}s<extra></extra>",
        ),
        row=3, col=1,
    )

    fig.update_layout(
        title=title,
        height=900, width=1600,
        hovermode="closest", hoverdistance=50,
        showlegend=False,
    )
    fig.update_xaxes(rangeslider_visible=False, row=1, col=1)
    fig.update_xaxes(title_text="Cumulative Volume (contracts)", row=3, col=1)
    fig.update_yaxes(title_text="Price", row=1, col=1)
    fig.update_yaxes(title_text="Signed vol", row=2, col=1)
    fig.update_yaxes(title_text="Seconds", autorange="reversed", row=3, col=1)

    config = {"scrollZoom": True, "displayModeBar": True}
    script_dir = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(script_dir, "chart_controls.js")) as f:
        post_script = f.read()
    fig.write_html(output_html, config=config, post_script=post_script)
    print(f"Saved to {output_html}")


def main():
    ap = argparse.ArgumentParser(description="Futures volume-bar chart")
    ap.add_argument("csv", help="6-column CSV from databento_download.py")
    ap.add_argument("-v", "--volume-per-bar", type=float, required=True,
                    help="Volume per bar in contracts (no default; product-specific)")
    ap.add_argument("-o", "--output", help="Output HTML path")
    args = ap.parse_args()

    base = os.path.splitext(os.path.basename(args.csv))[0]
    out = args.output or f"logs/{base}_volume.html"
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)

    print(f"Loading {args.csv}...")
    df = load_trades(args.csv)
    print(f"Loaded {len(df):,} trades")
    bars = build_volume_bars(df, args.volume_per_bar)
    total_vol = sum(b["volume"] for b in bars)
    print(f"Built {len(bars):,} bars of {args.volume_per_bar:g} contracts (total {total_vol:,.0f})")
    title = f"{base} - {args.volume_per_bar:g} contracts/bar"
    plot(bars, out, args.volume_per_bar, title)


if __name__ == "__main__":
    main()
