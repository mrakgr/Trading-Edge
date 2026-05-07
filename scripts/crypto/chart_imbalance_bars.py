"""Imbalance-bar chart for crypto perps trades, around a single entry.

Imbalance bars (López de Prado dollar-imbalance bars):
  θ_T = Σ_{t≤T} sign_t · dv_t       (cumulative signed $-flow within the bar)
A bar closes when |θ_T| ≥ threshold; the trade that crosses the
threshold *splits* — the partial fills the bar exactly to ±T,
the remainder rolls into the next bar at the same price/sign/timestamp.
After close the running counter carries the overshoot:
  θ_new = θ_old - sign(θ_old) · T
(so the residual order-flow imbalance feeds the next bar.)

Each bar therefore has a definite sign — buy-imbalance (θ ended at +T)
or sell-imbalance (θ ended at −T). Useful for spotting absorption
(strong one-sided flow that *doesn't* move price) and accumulation/
distribution patterns.

Panes:
  1. VWAP line + per-bar marker colored by bar sign (green=buy, red=sell).
     Entry/exit price + vline overlays.
  2. total $volume per bar, colored by bar sign.
  3. stacked buy / sell $vol per bar (left axis) + 1h / 10h
     time-anchored imbalance MAs on a secondary axis [-1, +1].
  4. duration per bar (s, axis reversed).

x-axis = cumulative $volume across all bars in the window (same
convention as `chart_volume_bars.py`).

Threshold selection: if `--threshold` is not given we auto-pick
the largest power-of-2 threshold that yields at least `--min-bars`
bars (default 1000). This adapts across symbols / regimes.

Use:

    python scripts/crypto/chart_imbalance_bars.py \\
        --symbol BELUSDT --entry "2024-05-09 10:17" \\
        --hours-before 18 --hours-after 18

The trip metadata (entry/exit price, mfe, mae, ...) is looked up from
the trips_th15_volratio_30d8h.csv file by (symbol, entry_us). Falls
back to entry-only chart if no match.
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


def build_imbalance_bars(df: pd.DataFrame, threshold: float) -> list[dict]:
    """Equal-imbalance bars from raw trades. Trades that would push
    |θ| past `threshold` split: a fractional fills the closing bar
    exactly to ±threshold (with sign matching the running θ at the
    moment of crossing); the remainder of the trade rolls into the
    next bar at the same price/sign/timestamp. After close, the
    residual imbalance carries:  θ_new = θ_old - sign(θ_old)·threshold.
    """
    if len(df) == 0 or threshold <= 0:
        return []
    p = df.price.to_numpy(dtype=np.float64)
    q = df.quantity.to_numpy(dtype=np.float64)
    t = df.timestamp_us.to_numpy(dtype=np.int64)
    s = df.sign.to_numpy(dtype=np.float64)
    dv = p * q

    bars = []
    cur = _empty()
    theta = 0.0  # running signed-imbalance counter (carries between bars)

    for i in range(len(df)):
        remaining_dv = dv[i]
        price_i = p[i]
        sign_i = s[i]
        ts_i = int(t[i])
        while remaining_dv > 0:
            # If theta is already at/past threshold (shouldn't happen with
            # carry), close immediately. Otherwise, find how much of this
            # trade can be added before |theta| would cross threshold.
            theta_after_full = theta + sign_i * remaining_dv
            if abs(theta_after_full) < threshold:
                # Whole trade fits without closing the bar.
                _add(cur, price_i, remaining_dv, ts_i, sign_i)
                theta = theta_after_full
                remaining_dv = 0.0
            else:
                # Partial fill: solve for x such that
                #     |theta + sign_i * x| = threshold,
                # with x ∈ (0, remaining_dv].
                target = threshold if sign_i > 0 else -threshold
                # If the trade direction matches the side that's about to
                # be hit, the equation is theta + sign_i*x = target. If
                # theta is on the opposite side, the bar closes on the
                # OTHER side first only if |theta| was already very large
                # — with carry it's bounded by `threshold`, so the close
                # is always on the side matching sign_i.
                x = (target - theta) / sign_i
                # Numerical safety.
                if x <= 0:
                    # theta already at/past threshold in sign_i direction.
                    x = 0.0
                if x > remaining_dv:
                    x = remaining_dv
                if x > 0:
                    _add(cur, price_i, x, ts_i, sign_i)
                    theta += sign_i * x
                    remaining_dv -= x
                # Close the bar; theta should be ±threshold here.
                bar_sign = 1 if theta > 0 else -1
                cur["bar_sign"] = bar_sign
                bars.append(_finalize(cur))
                cur = _empty()
                # Carry the overshoot. With our split logic, theta is
                # exactly ±threshold so the carry is 0, but keep the
                # general formula for robustness against fp drift.
                theta = theta - bar_sign * threshold
    if cur["dv"] > 0:
        cur["bar_sign"] = 1 if theta > 0 else (-1 if theta < 0 else 1)
        bars.append(_finalize(cur))
    cum = 0.0
    for b in bars:
        cum += b["dv"]
        b["cum_dv"] = cum
    return bars


def _empty():
    return {"prices": [], "dvs": [], "ts": [], "signs": [],
            "dv": 0.0, "bar_sign": 0}


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
        "bar_sign": int(bar["bar_sign"]),
    }


def auto_threshold(df: pd.DataFrame, min_bars: int) -> float:
    """Pick the largest threshold (in $-imbalance) such that the
    resulting bar count is >= min_bars. We use a doubling/halving
    search over a power-of-2 ladder anchored on the median trade
    $vol of the loaded data — fast, deterministic, no hidden tuning.
    """
    if len(df) == 0:
        return 1.0
    dv = (df.price.to_numpy() * df.quantity.to_numpy())
    # Start somewhere reasonable: the upper-quartile signed cumsum
    # range over short rolling windows is a good ballpark, but a
    # simpler heuristic is to start at total_dv / min_bars and then
    # halve until we have >= min_bars bars (since bigger threshold ->
    # fewer bars).
    total = float(dv.sum())
    cand = total / max(1, min_bars)
    # Round to a "nice" power of 2 of $1 to keep the threshold
    # human-readable in titles.
    cand = max(1.0, 2 ** round(np.log2(max(cand, 1.0))))
    # Halve until we exceed min_bars (or we hit a floor).
    for _ in range(20):
        bars = build_imbalance_bars(df, cand)
        if len(bars) >= min_bars:
            return cand
        cand /= 2.0
        if cand < 1.0:
            break
    return cand


def time_anchored_imbalance_ma(bars: list[dict], window_us: int) -> np.ndarray:
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


def lookup_trip(trips_path: str, symbol: str, entry_us: int,
                tol_us: int = 60 * 60 * 1_000_000) -> Optional[pd.Series]:
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
    fmts = ["%Y-%m-%d %H:%M:%S", "%Y-%m-%d %H:%M",
            "%Y-%m-%dT%H:%M:%S", "%Y-%m-%dT%H:%M"]
    for fmt in fmts:
        try:
            dt = datetime.strptime(entry_str, fmt).replace(tzinfo=timezone.utc)
            return int(dt.timestamp() * 1e6)
        except ValueError:
            continue
    raise ValueError(f"Could not parse entry timestamp: {entry_str!r}")


MA_LINE_COLORS = ["#1f77b4", "#9467bd", "#ff7f0e", "#2ca02c", "#d62728"]


def plot(bars_full: list[dict], display_lo_us: int,
         symbol: str, entry_us: int, trip: Optional[pd.Series],
         threshold: float, hours_before: float, hours_after: float,
         ma_windows_h: list[float],
         out_path: str, post_script: str) -> None:
    if not bars_full:
        raise RuntimeError("no bars in window")

    # MAs run over the full fetched range so trailing windows are warmed
    # up by pre-roll bars before the display window starts.
    imb_full: list[tuple[float, np.ndarray]] = []
    for w_h in ma_windows_h:
        imb_full.append(
            (w_h, time_anchored_imbalance_ma(bars_full, int(w_h * US_PER_HOUR))))

    end_us_full = np.array([b["end_us"] for b in bars_full], dtype=np.int64)
    first_idx = int(np.searchsorted(end_us_full, display_lo_us, side="left"))
    bars = bars_full[first_idx:]
    if not bars:
        raise RuntimeError("display window contains no bars")
    imb_arrays = [(w, arr[first_idx:]) for (w, arr) in imb_full]

    # Re-base cum_dv to start at 0 within the display slice.
    cum_dv_raw = np.array([b["cum_dv"] for b in bars])
    base_cum = cum_dv_raw[0] - bars[0]["dv"]
    cum_dv = cum_dv_raw - base_cum
    vwap = np.array([b["vwap"] for b in bars])
    durations = np.array([b["duration_s"] for b in bars])
    buy_dv = np.array([b["buy_dv"] for b in bars])
    sell_dv = np.array([b["sell_dv"] for b in bars])
    total_dv = np.array([b["dv"] for b in bars])
    bar_sign = np.array([b["bar_sign"] for b in bars])
    end_us = np.array([b["end_us"] for b in bars], dtype=np.int64)
    bar_colors = np.where(bar_sign > 0, "#26a69a", "#ef5350")
    widths = total_dv * 0.8

    # Per-bar tooltip metadata, attached to all primary traces so any
    # pane shows the same rich context. Imbalance bars carry an extra
    # bar_sign field (buy / sell).
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
            "buy" if b["bar_sign"] > 0 else "sell",  # 16 bar sign label
        ]
        for i, b in enumerate(bars)
    ]
    bar_hover = (
        "<b>bar #%{customdata[0]} (%{customdata[16]})</b>  "
        "cum_dv $%{customdata[1]:,.0f}<br>"
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

    def cum_at(us: int) -> Optional[float]:
        idx = np.searchsorted(end_us, us, side="left")
        if idx >= len(bars):
            return None
        b = bars[idx]
        return (b["cum_dv"] - b["dv"]) - base_cum

    entry_x = cum_at(entry_us)
    exit_x = cum_at(int(trip.exit_us)) if trip is not None else None

    entry_dt = datetime.fromtimestamp(entry_us / 1e6, timezone.utc)
    title_extra = ""
    if trip is not None:
        title_extra = (f"  rvol={trip.ratio:.2f}  net_pnl=${trip.net_pnl:+.0f}  "
                       f"mfe={trip.mfe:+.0f}bp  mae={trip.mae:+.0f}bp  "
                       f"bars_held(1m)={int(trip.bars_held)}")
    title = (f"{symbol} — entry {entry_dt:%Y-%m-%d %H:%M} UTC  "
             f"(imbalance bars, T=${threshold:,.0f}, "
             f"±{hours_before:.0f}/{hours_after:.0f}h, n={len(bars)})"
             + title_extra)

    fig = make_subplots(
        rows=4, cols=1, shared_xaxes=True,
        row_heights=[0.46, 0.18, 0.22, 0.14],
        vertical_spacing=0.035,
        specs=[[{}], [{}], [{"secondary_y": True}], [{}]],
        subplot_titles=(
            title,
            f"total $vol per bar (colored by bar sign)",
            ("buy / sell $vol  +  imbalance MA ("
             + ", ".join(f"{w:g}h" for w in ma_windows_h) + ")"),
            "duration per bar (s)",
        ),
    )

    # Pane 1: VWAP line + per-bar dot colored by bar sign.
    fig.add_trace(
        go.Scatter(x=cum_dv, y=vwap, name="VWAP",
                   line=dict(color="#888", width=0.8),
                   marker=dict(size=4, color=bar_colors,
                               line=dict(width=0)),
                   mode="lines+markers",
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

    # Pane 2: total $vol per bar, colored by bar sign.
    fig.add_trace(
        go.Bar(x=cum_dv, y=total_dv, marker_color=bar_colors,
               marker_line_width=0, width=widths, name="bar $vol",
               customdata=customdata, hovertemplate=bar_hover),
        row=2, col=1,
    )
    if entry_x is not None:
        fig.add_vline(x=entry_x, line=dict(color="orange", width=1.5, dash="dash"),
                      row=2, col=1)
    if exit_x is not None:
        fig.add_vline(x=exit_x, line=dict(color="cyan", width=1.5, dash="dash"),
                      row=2, col=1)

    # Pane 3: stacked buy/sell + imbalance MAs.
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
    for i, (w_h, imb_arr) in enumerate(imb_arrays):
        color = MA_LINE_COLORS[i % len(MA_LINE_COLORS)]
        width = 1.2 + 0.4 * i
        fig.add_trace(
            go.Scatter(x=cum_dv, y=imb_arr, name=f"imb {w_h:g}h",
                       line=dict(color=color, width=width), mode="lines",
                       hovertemplate=f"imb {w_h:g}h: %{{y:+.3f}}<extra></extra>"),
            row=3, col=1, secondary_y=True,
        )
    if entry_x is not None:
        fig.add_vline(x=entry_x, line=dict(color="orange", width=1.5, dash="dash"),
                      row=3, col=1)
    if exit_x is not None:
        fig.add_vline(x=exit_x, line=dict(color="cyan", width=1.5, dash="dash"),
                      row=3, col=1)

    # Pane 4: bar duration.
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
    fig.update_yaxes(title_text="$vol", row=2, col=1)
    # Symmetric left axis on pane 3 so its zero aligns with the
    # imbalance MA's zero on the secondary axis.
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
    ap.add_argument("--threshold", type=float, default=None,
                    help="Imbalance threshold T in $. If unset, auto-pick "
                         "the largest power-of-2 such that bars >= --min-bars.")
    ap.add_argument("--min-bars", type=int, default=1000,
                    help="Auto-threshold target. Default 1000.")
    ap.add_argument("--ma-windows", default="1,200",
                    help="Comma-separated trailing-imbalance MA windows in HOURS. "
                         "Default '1,200' — the production cumsum-z engine uses 200h.")
    ap.add_argument("--tape-root", default=DEFAULT_TAPE_ROOT)
    ap.add_argument("--trips", default=DEFAULT_TRIPS)
    ap.add_argument("--out-dir", default=DEFAULT_OUT_DIR)
    args = ap.parse_args()

    repo = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))
    trips_path = args.trips if os.path.isabs(args.trips) else os.path.join(repo, args.trips)
    out_dir = args.out_dir if os.path.isabs(args.out_dir) else os.path.join(repo, args.out_dir)
    os.makedirs(out_dir, exist_ok=True)

    ma_windows_h = [float(s.strip()) for s in args.ma_windows.split(",") if s.strip()]
    max_ma_h = max(ma_windows_h) if ma_windows_h else 0.0

    entry_us = parse_entry_us(args.entry)
    display_lo = entry_us - int(args.hours_before * US_PER_HOUR)
    t_hi = entry_us + int(args.hours_after * US_PER_HOUR)
    fetch_lo = display_lo - int(max_ma_h * US_PER_HOUR)
    print(f"Loading {args.symbol} tape over "
          f"{datetime.fromtimestamp(fetch_lo/1e6, timezone.utc):%Y-%m-%d %H:%M} → "
          f"{datetime.fromtimestamp(t_hi/1e6, timezone.utc):%Y-%m-%d %H:%M} UTC "
          f"(includes {max_ma_h:g}h of pre-roll for MA warm-up)")
    df = load_tape_window(args.tape_root, args.symbol, fetch_lo, t_hi)
    print(f"  loaded {len(df):,} trades (full fetch incl. pre-roll)")
    if len(df) == 0:
        print("  no trades; aborting", file=sys.stderr)
        return 1

    if args.threshold is None:
        # Auto-threshold against the display-window trades only — we want
        # ~min_bars bars *in the display*, not in the full pre-rolled set.
        df_display = df.loc[df.timestamp_us >= display_lo]
        threshold = auto_threshold(df_display, args.min_bars)
        print(f"  auto-threshold = ${threshold:,.0f} "
              f"(target ≥ {args.min_bars} bars in display window)")
    else:
        threshold = args.threshold
        print(f"  threshold = ${threshold:,.0f}")

    bars_full = build_imbalance_bars(df, threshold)
    end_us_full = np.array([b["end_us"] for b in bars_full], dtype=np.int64)
    first_idx = int(np.searchsorted(end_us_full, display_lo, side="left"))
    bars_display = bars_full[first_idx:]
    n_buy = sum(1 for b in bars_display if b["bar_sign"] > 0)
    n_sell = len(bars_display) - n_buy
    total_dv = sum(b["dv"] for b in bars_display)
    print(f"  built {len(bars_full):,} bars total, "
          f"{len(bars_display):,} in display "
          f"({n_buy:,} buy / {n_sell:,} sell)  "
          f"total displayed ${total_dv:,.0f}")

    trip = lookup_trip(trips_path, args.symbol, entry_us)
    if trip is None:
        print(f"  no matching trip in {trips_path}; entry-only chart")
    else:
        print(f"  matched trip: net_pnl={trip.net_pnl:+.0f}  rvol={trip.ratio:.2f}")

    with open(CHART_CONTROLS_JS) as f:
        post_script = f.read()
    entry_dt = datetime.fromtimestamp(entry_us / 1e6, timezone.utc)
    fname = f"{entry_dt:%Y%m%dT%H%M}_{args.symbol}_imbalbars.html"
    out = os.path.join(out_dir, fname)
    plot(bars_full, display_lo,
         args.symbol, entry_us, trip, threshold,
         args.hours_before, args.hours_after,
         ma_windows_h, out, post_script)
    print(f"Saved to {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
