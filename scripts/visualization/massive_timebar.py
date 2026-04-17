import sys
import os
import argparse
import numpy as np
import plotly.graph_objects as go
from plotly.subplots import make_subplots
from datetime import datetime, timezone
from trade_filters import filter_trades, filter_by_sip_delta
from market_hours import get_market_hours_bounds
from trade_io import load_trades as _load_parquet_trades

def load_trades(trades_path):
    """Load trades from the per-ticker-day Parquet file."""
    trades = _load_parquet_trades(trades_path)
    # Apply SIP-delta filter before zeroing out missing participant timestamps,
    # otherwise we lose the ability to distinguish late prints.
    trades = filter_by_sip_delta(trades)
    for t in trades:
        if t['participant_timestamp'] == 0:
            t['participant_timestamp'] = t['sip_timestamp']
    return trades

def create_time_bars_vwap(trades, seconds_per_bar):
    """
    Group trades into fixed time intervals and compute VWAP + volume-weighted stddev.

    Mirrors the ORB system's TimeBarBuilder: trades are bucketed by
    `floor(timestamp_ns / bucket_ns)`, empty buckets are skipped.

    Returns list of bars with:
    - start_time, end_time: time range (participant_timestamp)
    - vwap: volume-weighted average price
    - stddev: volume-weighted standard deviation
    - volume: total volume in bar
    - num_trades: number of trades in bar
    """
    if not trades:
        return []

    bars = []
    bucket_ns = seconds_per_bar * 1_000_000_000
    current_bucket = None
    bar_trades = []

    for trade in trades:
        ts = trade['participant_timestamp']
        bucket = ts // bucket_ns

        if current_bucket is None:
            current_bucket = bucket
        elif bucket != current_bucket:
            if bar_trades:
                bars.append(compute_bar_stats(bar_trades, current_bucket * bucket_ns, bucket_ns))
            bar_trades = []
            current_bucket = bucket

        bar_trades.append(trade)

    if bar_trades:
        bars.append(compute_bar_stats(bar_trades, current_bucket * bucket_ns, bucket_ns))

    return bars

def compute_bar_stats(bar_trades, bucket_start_ns, bucket_ns):
    """Compute VWAP + volume-weighted stddev for a time bar."""
    prices = np.array([t['price'] for t in bar_trades])
    volumes = np.array([t['size'] for t in bar_trades])

    total_volume = volumes.sum()
    vwap = np.sum(prices * volumes) / total_volume
    variance = np.sum(volumes * (prices - vwap) ** 2) / total_volume
    stddev = np.sqrt(max(0.0, variance))

    return {
        'start_time': bucket_start_ns,
        'end_time': bucket_start_ns + bucket_ns,
        'vwap': vwap,
        'stddev': stddev,
        'volume': total_volume,
        'num_trades': len(bar_trades)
    }

def plot_time_bars_vwap(bars, output_html, seconds_per_bar, all_trades=None):
    """
    Time-based VWAP ±2σ bar chart with volume subpanel.
    X-axis: time
    Y-axis: price (VWAP ± 2σ bands)
    Bottom panel: volume per bar
    """
    fig = make_subplots(
        rows=2, cols=1,
        shared_xaxes=True,
        vertical_spacing=0.05,
        row_heights=[0.7, 0.3],
        subplot_titles=['VWAP ±2σ', 'Volume']
    )

    def ns_to_datetime(ns):
        return datetime.fromtimestamp(ns / 1e9, timezone.utc)

    bucket_ns = seconds_per_bar * 1_000_000_000
    # Centered timestamps so the bar is drawn in the middle of its bucket.
    x_vals = [ns_to_datetime(b['start_time'] + bucket_ns // 2) for b in bars]
    vwap_vals = [b['vwap'] for b in bars]
    upper_2sigma = [b['vwap'] + 2 * b['stddev'] for b in bars]
    lower_2sigma = [b['vwap'] - 2 * b['stddev'] for b in bars]
    volumes = [b['volume'] for b in bars]

    def format_time(ns):
        return ns_to_datetime(ns).strftime('%H:%M:%S')

    # Add background shading for market hours zones
    if all_trades and show_extended_hours:
        hours = get_market_hours_bounds(all_trades)
        if hours:
            open_ts, close_ts = hours
            open_dt = ns_to_datetime(open_ts)
            close_dt = ns_to_datetime(close_ts)

            first_ts = min(t['participant_timestamp'] for t in all_trades)
            last_ts = max(t['participant_timestamp'] for t in all_trades)
            first_dt = ns_to_datetime(first_ts)
            last_dt = ns_to_datetime(last_ts)

            shapes = []
            if first_dt < open_dt:
                shapes.extend([
                    dict(type="rect", xref="x", yref="paper", x0=first_dt, x1=open_dt, y0=0, y1=0.7,
                         fillcolor="lightblue", opacity=0.3, layer="below", line_width=0),
                    dict(type="rect", xref="x2", yref="paper", x0=first_dt, x1=open_dt, y0=0.7, y1=1,
                         fillcolor="lightblue", opacity=0.3, layer="below", line_width=0)
                ])
            shapes.extend([
                dict(type="rect", xref="x", yref="paper", x0=open_dt, x1=close_dt, y0=0, y1=0.7,
                     fillcolor="lightgreen", opacity=0.3, layer="below", line_width=0),
                dict(type="rect", xref="x2", yref="paper", x0=open_dt, x1=close_dt, y0=0.7, y1=1,
                     fillcolor="lightgreen", opacity=0.3, layer="below", line_width=0)
            ])
            if last_dt > close_dt:
                shapes.extend([
                    dict(type="rect", xref="x", yref="paper", x0=close_dt, x1=last_dt, y0=0, y1=0.7,
                         fillcolor="lightyellow", opacity=0.3, layer="below", line_width=0),
                    dict(type="rect", xref="x2", yref="paper", x0=close_dt, x1=last_dt, y0=0.7, y1=1,
                         fillcolor="lightyellow", opacity=0.3, layer="below", line_width=0)
                ])

            fig.update_layout(shapes=shapes)

    # Session VWAP for minimum-height scaling on zero-stddev bars
    total_volume = sum(b['volume'] for b in bars)
    session_vwap = sum(b['vwap'] * b['volume'] for b in bars) / total_volume if total_volume > 0 else 1.0

    min_height = min(0.01, 0.001 * session_vwap)
    bar_heights = []
    bar_bases = []
    for upper, lower, vwap in zip(upper_2sigma, lower_2sigma, vwap_vals):
        height = upper - lower
        actual_height = max(height, min_height)
        bar_heights.append(actual_height)
        bar_bases.append(vwap - actual_height / 2)

    # Up (green) vs down (red) vs previous bar's close-ish reference (VWAP)
    colors = []
    for i, b in enumerate(bars):
        if i == 0:
            colors.append('green')
        else:
            colors.append('green' if b['vwap'] >= bars[i - 1]['vwap'] else 'red')

    customdata = [[
        format_time(b['start_time']),
        format_time(b['end_time']),
        b['vwap'],
        b['stddev'],
        b['vwap'] + 2 * b['stddev'],
        b['vwap'] - 2 * b['stddev'],
        b['volume'],
        b['num_trades']
    ] for b in bars]

    # Bar width in ms (plotly Bar on datetime axis expects width in ms)
    bar_width_ms = seconds_per_bar * 1000 * 0.8

    fig.add_trace(go.Bar(
        x=x_vals,
        y=bar_heights,
        base=bar_bases,
        name='VWAP ±2σ',
        marker_color=colors,
        marker_line_width=0,
        width=bar_width_ms,
        customdata=customdata,
        hovertemplate='<b>Start:</b> %{customdata[0]}<br>' +
                      '<b>End:</b> %{customdata[1]}<br>' +
                      '<b>VWAP:</b> %{customdata[2]:.2f}<br>' +
                      '<b>StdDev:</b> %{customdata[3]:.4f}<br>' +
                      '<b>+2σ:</b> %{customdata[4]:.2f}<br>' +
                      '<b>-2σ:</b> %{customdata[5]:.2f}<br>' +
                      '<b>Volume:</b> %{customdata[6]:,.0f}<br>' +
                      '<b>Trades:</b> %{customdata[7]}<extra></extra>'
    ), row=1, col=1)

    # VWAP line overlay
    fig.add_trace(go.Scatter(
        x=x_vals,
        y=vwap_vals,
        mode='lines',
        name='VWAP',
        line=dict(color='blue', width=1),
        hoverinfo='skip'
    ), row=1, col=1)

    # Volume panel
    fig.add_trace(go.Bar(
        x=x_vals,
        y=volumes,
        name='Volume',
        marker_color='blue',
        marker_line_width=0,
        opacity=0.5,
        width=bar_width_ms,
        hovertemplate='Time: %{x}<br>Volume: %{y:,.0f}<extra></extra>'
    ), row=2, col=1)

    fig.update_layout(
        title=f'Time Bars ({seconds_per_bar}s) — VWAP ±2σ',
        height=700,
        width=1400,
        hovermode='closest',
        hoverdistance=100,
        xaxis2_title='Time',
        showlegend=False
    )

    fig.update_xaxes(rangeslider_visible=False, row=1, col=1)
    fig.update_yaxes(title_text='Price', row=1, col=1)
    fig.update_yaxes(title_text='Volume', row=2, col=1)

    config = {
        'scrollZoom': True,
        'displayModeBar': True
    }

    script_dir = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(script_dir, 'chart_controls.js')) as f:
        post_script = f.read()

    fig.write_html(output_html, config=config, post_script=post_script)
    print(f'Saved to {output_html}')

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description='Generate time-bar VWAP charts from trade data')
    parser.add_argument('input_parquet', help='Path to trades Parquet file (e.g. data/trades/TICKER/DATE.parquet)')
    parser.add_argument('-s', '--seconds-per-bar', type=int, default=10, help='Bucket length in seconds (default: 10)')
    parser.add_argument('-o', '--output', help='Output HTML file path')
    parser.add_argument('--show-extended-hours', action='store_true', default=True, help='Show extended hours (default: True)')
    parser.add_argument('--no-extended-hours', dest='show_extended_hours', action='store_false', help='Hide extended hours')

    args = parser.parse_args()

    input_parquet = args.input_parquet
    seconds_per_bar = args.seconds_per_bar
    show_extended_hours = args.show_extended_hours

    if args.output:
        output_html = args.output
    else:
        ticker = os.path.basename(os.path.dirname(input_parquet))
        date = os.path.splitext(os.path.basename(input_parquet))[0]
        output_dir = f'data/charts/massive/{ticker}_{date}'
        os.makedirs(output_dir, exist_ok=True)
        output_html = f'{output_dir}/timebar.html'

    print(f'Loading trades from {input_parquet}...')
    all_trades = load_trades(input_parquet)
    print(f'Loaded {len(all_trades)} trades')

    # Filter out special trade types (match massive_volume.py defaults)
    all_trades = filter_trades(all_trades, exclude_odd_lots=False, exclude_extended_hours=False)
    print(f'After filtering: {len(all_trades)} trades')

    # Filter to regular hours if requested
    trades = all_trades
    if not show_extended_hours:
        hours = get_market_hours_bounds(all_trades)
        if hours:
            open_ts, close_ts = hours
            trades = [t for t in all_trades if open_ts <= t['participant_timestamp'] <= close_ts]
            print(f'Filtered to regular hours: {len(trades)} trades')

    print(f'Creating {seconds_per_bar}s time bars...')
    bars = create_time_bars_vwap(trades, seconds_per_bar)
    print(f'Created {len(bars)} time bars')

    print(f'Plotting...')
    plot_time_bars_vwap(bars, output_html, seconds_per_bar, all_trades)
