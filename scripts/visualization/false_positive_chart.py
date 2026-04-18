"""
False-positive inspection chart.

For a given (ticker, date) day that the gated ORB system entered and lost on,
draw a four-panel chart:

    Row 1 — VWAP ±2σ time bars, with entry/exit decision markers.
    Row 2 — Predicted RVOL (gate signal), horizontal at 3.0.
    Row 3 — Per-bar volume.
    Row 4 — Observed cumulative volume fraction vs. the averaged session profile.

Consumes:
  - A JSON diagnostics file produced by scripts/export_false_positive_diagnostics.fsx.
    Contains: per-bar VWAP/stddev/volume/session_cum_volume/range/predicted_rvol,
              decisions (entry/exit with predicted_rvol at decision ts),
              session_profile (cumulative fraction curve).

Output:
  - data/charts/false_positives/<TICKER>/<DATE>.html
"""

import argparse
import json
import os
from datetime import datetime, timezone

import plotly.graph_objects as go
from plotly.subplots import make_subplots


def ns_to_dt(ns):
    return datetime.fromtimestamp(ns / 1e9, timezone.utc)


def build_chart(diag_path, output_html):
    with open(diag_path) as f:
        d = json.load(f)

    ticker = d['ticker']
    date = d['date']
    bucket_seconds = d['bucket_seconds']
    rvol_threshold = d['rvol_threshold']
    day_pnl = d['day_pnl']
    raw_avg_4w = d['raw_avg_4w']
    session_start_ns = d['session_start_ns']
    session_profile = d['session_profile']  # cumulative fraction, length=bucket_count
    bars = d['bars']
    decisions = d['decisions']

    # --- X axis (datetime) ---
    bar_ts = [ns_to_dt(b['ts_ns']) for b in bars]
    vwap = [b['vwap'] for b in bars]
    stddev = [b['stddev'] for b in bars]
    volume = [b['volume'] for b in bars]
    session_cum_vol = [b['session_cum_volume'] for b in bars]
    range_high = [b['range_high'] for b in bars]
    range_low = [b['range_low'] for b in bars]
    pred_rvol = [b['predicted_rvol'] for b in bars]
    buckets = [b['bucket'] for b in bars]

    # --- Session profile on the same time axis ---
    # bucket index i ↔ time t = session_start_ns + i * bucket_seconds * 1e9
    profile_ts = [
        ns_to_dt(session_start_ns + int(i * bucket_seconds * 1e9))
        for i in range(len(session_profile))
    ]
    profile_frac = session_profile

    # --- Observed cumulative fraction ---
    # Need total day volume to normalize. The last bar's session_cum_volume is
    # the total captured during [08:30, close]; divide by it to get observed fraction.
    total_cum = session_cum_vol[-1] if session_cum_vol else 0.0
    if total_cum > 0:
        observed_frac = [v / total_cum for v in session_cum_vol]
    else:
        observed_frac = [0.0] * len(session_cum_vol)

    # --- Figure ---
    fig = make_subplots(
        rows=4, cols=1,
        shared_xaxes=True,
        vertical_spacing=0.03,
        row_heights=[0.40, 0.20, 0.15, 0.25],
        subplot_titles=[
            f'{ticker} {date} — VWAP ±2σ  (day P&L {day_pnl:+.0f}, decisions {len(decisions)})',
            f'Predicted RVOL (gate threshold = {rvol_threshold}x)',
            'Bar Volume',
            'Cumulative volume fraction: observed (blue) vs. session profile (orange)',
        ],
    )

    # --- Row 1: VWAP ±2σ bars ---
    upper = [v + 2 * s for v, s in zip(vwap, stddev)]
    lower = [v - 2 * s for v, s in zip(vwap, stddev)]
    bar_heights, bar_bases = [], []
    for u, lo, v in zip(upper, lower, vwap):
        h = max(u - lo, 0.001 * abs(v) if v else 0.001)
        bar_heights.append(h)
        bar_bases.append(v - h / 2)

    colors = ['green' if i == 0 or vwap[i] >= vwap[i-1] else 'red'
              for i in range(len(vwap))]
    bar_width_ms = bucket_seconds * 1000 * 0.8

    fig.add_trace(go.Bar(
        x=bar_ts, y=bar_heights, base=bar_bases,
        marker_color=colors, marker_line_width=0,
        width=bar_width_ms,
        name='VWAP ±2σ',
        hovertemplate='%{x}<br>VWAP %{customdata[0]:.4f}<br>±2σ [%{customdata[1]:.4f}, %{customdata[2]:.4f}]<extra></extra>',
        customdata=list(zip(vwap, lower, upper)),
    ), row=1, col=1)

    fig.add_trace(go.Scatter(
        x=bar_ts, y=vwap, mode='lines',
        line=dict(color='blue', width=1),
        name='VWAP',
        hoverinfo='skip',
    ), row=1, col=1)

    # Range high/low — the breakout levels the ORB system watches.
    fig.add_trace(go.Scatter(
        x=bar_ts, y=range_high, mode='lines',
        line=dict(color='black', width=1, dash='dot'),
        name='Range High', hoverinfo='skip',
    ), row=1, col=1)
    fig.add_trace(go.Scatter(
        x=bar_ts, y=range_low, mode='lines',
        line=dict(color='black', width=1, dash='dot'),
        name='Range Low', hoverinfo='skip',
    ), row=1, col=1)

    # Entry/exit markers on the price panel.
    entries = [d for d in decisions if d['shares'] != 0]
    exits = [d for d in decisions if d['shares'] == 0]

    def fmt_prvol(x):
        if x is None:
            return 'n/a'
        return f'{x:.2f}x'

    if entries:
        fig.add_trace(go.Scatter(
            x=[ns_to_dt(d['ts_ns']) for d in entries],
            y=[d['price'] for d in entries],
            mode='markers',
            marker=dict(symbol='triangle-up', size=14, color='lime',
                        line=dict(color='black', width=1)),
            name='Entry',
            customdata=[[d['shares'], fmt_prvol(d.get('predicted_rvol'))] for d in entries],
            hovertemplate='ENTRY %{x}<br>price %{y:.4f}<br>shares %{customdata[0]}<br>pred. RVOL %{customdata[1]}<extra></extra>',
        ), row=1, col=1)

    if exits:
        fig.add_trace(go.Scatter(
            x=[ns_to_dt(d['ts_ns']) for d in exits],
            y=[d['price'] for d in exits],
            mode='markers',
            marker=dict(symbol='x', size=14, color='red',
                        line=dict(color='black', width=1)),
            name='Exit',
            customdata=[[fmt_prvol(d.get('predicted_rvol'))] for d in exits],
            hovertemplate='EXIT %{x}<br>price %{y:.4f}<br>pred. RVOL %{customdata[0]}<extra></extra>',
        ), row=1, col=1)

    # --- Row 2: Predicted RVOL ---
    fig.add_trace(go.Scatter(
        x=bar_ts, y=pred_rvol, mode='lines',
        line=dict(color='purple', width=1.5),
        name='Predicted RVOL',
        hovertemplate='%{x}<br>pred. RVOL %{y:.2f}x<extra></extra>',
    ), row=2, col=1)
    # Threshold line.
    if bar_ts:
        fig.add_trace(go.Scatter(
            x=[bar_ts[0], bar_ts[-1]], y=[rvol_threshold, rvol_threshold],
            mode='lines', line=dict(color='red', width=1, dash='dash'),
            name=f'gate {rvol_threshold}x', hoverinfo='skip',
        ), row=2, col=1)

    # --- Row 3: Bar volume ---
    fig.add_trace(go.Bar(
        x=bar_ts, y=volume,
        marker_color='steelblue', marker_line_width=0,
        width=bar_width_ms, opacity=0.7,
        name='Bar volume',
        hovertemplate='%{x}<br>vol %{y:,.0f}<extra></extra>',
    ), row=3, col=1)

    # --- Row 4: Observed vs. profile cumulative fraction ---
    fig.add_trace(go.Scatter(
        x=bar_ts, y=observed_frac, mode='lines',
        line=dict(color='blue', width=1.5),
        name='Observed cum. fraction',
        hovertemplate='%{x}<br>observed %{y:.4f}<extra></extra>',
    ), row=4, col=1)
    fig.add_trace(go.Scatter(
        x=profile_ts, y=profile_frac, mode='lines',
        line=dict(color='orange', width=1.5),
        name='Session profile',
        hovertemplate='%{x}<br>profile %{y:.4f}<extra></extra>',
    ), row=4, col=1)

    # Markers where predicted RVOL crosses the threshold (useful to line up
    # with the cum-fraction divergence visually).
    fig.update_layout(
        height=1100, width=1600,
        hovermode='x unified',
        hoverdistance=100,
        showlegend=True,
        legend=dict(orientation='h', y=1.02, x=0),
        title=dict(text=f'{ticker} {date} — false-positive diagnostics (raw_avg_4w={raw_avg_4w:,.0f})'),
    )
    fig.update_xaxes(rangeslider_visible=False)
    fig.update_yaxes(title_text='Price', row=1, col=1)
    fig.update_yaxes(title_text='Pred. RVOL', row=2, col=1)
    fig.update_yaxes(title_text='Volume', row=3, col=1)
    fig.update_yaxes(title_text='Cum. frac.', row=4, col=1)

    os.makedirs(os.path.dirname(output_html), exist_ok=True)
    script_dir = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(script_dir, 'chart_controls.js')) as f:
        post_script = f.read()
    config = {'scrollZoom': True, 'displayModeBar': True}
    fig.write_html(output_html, include_plotlyjs='cdn', config=config, post_script=post_script)
    print(f'Saved {output_html}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('diag_json', help='Path to diagnostics JSON from export_false_positive_diagnostics.fsx')
    parser.add_argument('-o', '--output', help='Output HTML path')
    args = parser.parse_args()

    if args.output:
        output = args.output
    else:
        with open(args.diag_json) as f:
            meta = json.load(f)
        output = f'data/charts/false_positives/{meta["ticker"]}/{meta["date"]}.html'

    build_chart(args.diag_json, output)
