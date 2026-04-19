"""
Predicted-RVOL diagnostic chart for the market-wide scanner.

For a given (ticker, date), draws a four-panel chart to diagnose where the
scanner's predicted RVOL diverges from the actual daily RVOL:

    Row 1 — OHLC candles from the minute aggs (price).
    Row 2 — Predicted RVOL curve with horizontal thresholds at 3x/5x/7x and
             the actual end-of-day RVOL marked.
    Row 3 — Per-bar volume.
    Row 4 — Observed cumulative volume fraction (cum_vol / daily_raw_volume)
             vs. the session profile curve.

Pulls minute bars from the Parquet file for that date, the profile from
data/volume_profile_minute.json, and daily aggregates from data/trading.db.

Usage:
    python3 scripts/visualization/predicted_rvol_chart.py MPLX 2026-04-10
    python3 scripts/visualization/predicted_rvol_chart.py MPLX 2026-04-10 -o some/path.html
"""

import argparse
import json
import os
from datetime import datetime, timezone, timedelta

import duckdb
import plotly.graph_objects as go
from plotly.subplots import make_subplots


DB = 'data/trading.db'
PROFILE_JSON = 'data/volume_profile_minute.json'
MINUTE_AGGS_DIR = 'data/minute_aggs'
NYSE_EARLY_CLOSES = {
    '2023-07-03', '2023-11-24',
    '2024-07-03', '2024-11-29', '2024-12-24',
    '2025-07-03', '2025-11-28', '2025-12-24',
    '2026-11-27', '2026-12-24',
}


def load_profile(date, is_early_close):
    with open(PROFILE_JSON) as f:
        p = json.load(f)
    session = p['early_close'] if is_early_close else p['regular_close']
    return p['seconds_per_bar'], session['profile']


def et_session_bounds_utc(date_str, is_early_close):
    # 08:30 ET start, 16:00 ET (or 13:00 ET if early-close) end.
    # DST-aware: need a proper tz library or the trading.db timezone. We'll
    # just approximate by using a UTC offset since the date is known — the
    # minute-aggs window_start is in ns UTC.
    # Eastern time: EDT = UTC-4 (DST in effect Mar~Nov), EST = UTC-5.
    from zoneinfo import ZoneInfo
    y, m, d = [int(x) for x in date_str.split('-')]
    et = ZoneInfo('America/New_York')
    start_et = datetime(y, m, d, 8, 30, tzinfo=et)
    close_et = datetime(y, m, d, 13 if is_early_close else 16, 0, tzinfo=et)
    start_utc = start_et.astimezone(timezone.utc)
    close_utc = close_et.astimezone(timezone.utc)
    return start_utc, close_utc


def build_chart(ticker, date, output_html):
    is_early = date in NYSE_EARLY_CLOSES
    seconds_per_bar, profile = load_profile(date, is_early)
    bucket_seconds = int(seconds_per_bar)  # 60
    bucket_count = len(profile)

    start_utc, close_utc = et_session_bounds_utc(date, is_early)
    start_ns = int(start_utc.timestamp() * 1e9)
    close_ns = int(close_utc.timestamp() * 1e9)
    bucket_ns = bucket_seconds * 1_000_000_000

    parquet_path = os.path.join(MINUTE_AGGS_DIR, f'{date}.parquet')
    if not os.path.exists(parquet_path):
        raise FileNotFoundError(f'no minute aggs parquet for {date}: {parquet_path}')

    con = duckdb.connect(DB, read_only=True)

    # Pull minute bars for the ticker, restricted to session window.
    bars = con.execute(f"""
        SELECT window_start, open, high, low, close, volume
        FROM read_parquet('{parquet_path}')
        WHERE ticker = ?
          AND window_start >= {start_ns}
          AND window_start < {close_ns}
        ORDER BY window_start
    """, [ticker]).fetchall()
    if not bars:
        raise RuntimeError(f'no minute bars found for {ticker} on {date}')

    # Pull daily aggregates for this (ticker, date) for denominators and actual RVOL.
    row = con.execute(f"""
        SELECT dp.volume AS raw_volume,
               sa.adj_volume,
               sv.avg_volume_4w,
               sv.avg_dollar_volume_4w,
               sv.avg_volume_4w * (dp.volume::DOUBLE / NULLIF(sa.adj_volume::DOUBLE, 0)) AS raw_avg_4w
        FROM daily_prices dp
        JOIN split_adjusted_prices sa ON sa.ticker = dp.ticker AND sa.date = dp.date
        JOIN stock_volume_4w sv ON sv.ticker = dp.ticker AND sv.date = dp.date
        WHERE dp.ticker = ? AND dp.date = ?
    """, [ticker, date]).fetchone()
    if not row:
        raise RuntimeError(f'no daily aggregate row for {ticker} on {date}')
    daily_raw, adj_volume, avg_4w, avg_dollar_4w, raw_avg_4w = row
    actual_rvol = daily_raw / raw_avg_4w if raw_avg_4w else float('nan')

    # Compute per-bar predicted RVOL.
    # Bucket index = (window_start - start_ns) / bucket_ns, 0-indexed from 08:30.
    ts = []
    opens = []
    highs = []
    lows = []
    closes = []
    volumes = []
    cum_vols = []
    pred_rvols = []
    observed_fracs = []
    profile_fracs_at_bar = []

    cum = 0.0
    for ws, o, h, l, c, v in bars:
        bucket = (ws - start_ns) // bucket_ns
        cum += v
        if 0 <= bucket < bucket_count:
            pf = profile[int(bucket)]
        else:
            pf = None
        if pf and pf > 0 and raw_avg_4w and raw_avg_4w > 0:
            pr = (cum / pf) / raw_avg_4w
        else:
            pr = None
        obs = cum / daily_raw if daily_raw else 0.0

        ts.append(datetime.fromtimestamp(ws / 1e9, timezone.utc))
        opens.append(o); highs.append(h); lows.append(l); closes.append(c)
        volumes.append(v)
        cum_vols.append(cum)
        pred_rvols.append(pr)
        observed_fracs.append(obs)
        profile_fracs_at_bar.append(pf)

    # Profile curve plotted on the same time axis (at bucket start times).
    profile_ts = [
        datetime.fromtimestamp((start_ns + i * bucket_ns) / 1e9, timezone.utc)
        for i in range(bucket_count)
    ]

    # ---------- Figure ----------
    fig = make_subplots(
        rows=4, cols=1,
        shared_xaxes=True,
        vertical_spacing=0.03,
        row_heights=[0.35, 0.25, 0.15, 0.25],
        subplot_titles=[
            f'{ticker} {date} — Price (minute OHLC)',
            f'Predicted RVOL (scan max: {max((p for p in pred_rvols if p is not None), default=0):.2f}x, actual daily RVOL: {actual_rvol:.2f}x)',
            'Per-minute volume',
            'Cumulative fraction: observed (blue, cum / daily_raw_volume) vs. profile (orange)',
        ],
    )

    # Row 1: OHLC candlesticks.
    fig.add_trace(go.Candlestick(
        x=ts, open=opens, high=highs, low=lows, close=closes,
        name='OHLC', showlegend=False,
    ), row=1, col=1)

    # Row 2: Predicted RVOL curve.
    fig.add_trace(go.Scatter(
        x=ts, y=pred_rvols, mode='lines',
        line=dict(color='purple', width=1.5),
        name='predicted RVOL',
        hovertemplate='%{x}<br>predicted RVOL %{y:.2f}x<extra></extra>',
    ), row=2, col=1)
    # Threshold lines.
    if ts:
        for thresh, colr in [(3.0, 'gray'), (5.0, 'red'), (7.0, 'darkred')]:
            fig.add_trace(go.Scatter(
                x=[ts[0], ts[-1]], y=[thresh, thresh],
                mode='lines', line=dict(color=colr, width=1, dash='dash'),
                name=f'{thresh}x', hoverinfo='skip', showlegend=True,
            ), row=2, col=1)
        # Actual end-of-day RVOL as a solid horizontal line.
        fig.add_trace(go.Scatter(
            x=[ts[0], ts[-1]], y=[actual_rvol, actual_rvol],
            mode='lines', line=dict(color='green', width=1.5),
            name=f'actual daily RVOL {actual_rvol:.2f}x', hoverinfo='skip',
        ), row=2, col=1)

    # Row 3: volume bars.
    bar_width_ms = bucket_seconds * 1000 * 0.8
    fig.add_trace(go.Bar(
        x=ts, y=volumes,
        marker_color='steelblue', marker_line_width=0,
        width=bar_width_ms, opacity=0.7,
        name='volume', showlegend=False,
        hovertemplate='%{x}<br>vol %{y:,.0f}<extra></extra>',
    ), row=3, col=1)

    # Row 4: observed vs profile cumulative fraction.
    fig.add_trace(go.Scatter(
        x=ts, y=observed_fracs, mode='lines',
        line=dict(color='blue', width=1.5),
        name='observed (cum / daily_raw)',
        hovertemplate='%{x}<br>observed %{y:.4f}<extra></extra>',
    ), row=4, col=1)
    fig.add_trace(go.Scatter(
        x=profile_ts, y=profile, mode='lines',
        line=dict(color='orange', width=1.5),
        name='session profile',
        hovertemplate='%{x}<br>profile %{y:.4f}<extra></extra>',
    ), row=4, col=1)

    fig.update_layout(
        height=1100, width=1600,
        hovermode='x unified', hoverdistance=100,
        showlegend=True,
        legend=dict(orientation='h', y=1.02, x=0),
        title=dict(text=f'{ticker} {date} — scanner diagnostic  (raw_avg_4w={raw_avg_4w:,.0f}, daily_raw_volume={daily_raw:,.0f})'),
        xaxis_rangeslider_visible=False,
    )
    fig.update_xaxes(rangeslider_visible=False)
    fig.update_yaxes(title_text='Price', row=1, col=1)
    fig.update_yaxes(title_text='pred. RVOL', row=2, col=1)
    fig.update_yaxes(title_text='volume', row=3, col=1)
    fig.update_yaxes(title_text='cum. frac.', row=4, col=1)

    os.makedirs(os.path.dirname(output_html) or '.', exist_ok=True)
    script_dir = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(script_dir, 'chart_controls.js')) as f:
        post_script = f.read()
    config = {'scrollZoom': True, 'displayModeBar': True}
    fig.write_html(output_html, include_plotlyjs='cdn', config=config, post_script=post_script)
    print(f'Saved {output_html}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('ticker')
    parser.add_argument('date', help='yyyy-MM-dd')
    parser.add_argument('-o', '--output', help='Output HTML path')
    args = parser.parse_args()

    output = args.output or f'data/charts/predicted_rvol/{args.ticker}/{args.date}.html'
    build_chart(args.ticker, args.date, output)
