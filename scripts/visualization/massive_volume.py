import json
import sys
import os
import argparse
import numpy as np
import plotly.graph_objects as go
from plotly.subplots import make_subplots
from datetime import datetime, timezone
from trade_filters import filter_trades
from market_hours import get_market_hours_bounds

def load_trades(json_path):
    """Load trades from Massive JSON file."""
    with open(json_path) as f:
        trades = json.load(f)

    # OTC stocks have participant_timestamp=0; fall back to sip_timestamp
    for t in trades:
        if t['participant_timestamp'] == 0:
            t['participant_timestamp'] = t['sip_timestamp']

    trades.sort(key=lambda t: t['participant_timestamp'])
    return trades

def create_volume_bars_vwap(trades, volume_per_bar):
    """
    Group trades into fixed volume chunks and compute VWAP with stddev.

    Splits trades across bars when they exceed the volume threshold.

    Returns list of bars with:
    - cumulative_volume: x-axis position
    - vwap: volume-weighted average price
    - stddev: standard deviation of prices
    - start_time, end_time: time range (participant_timestamp)
    - num_trades: number of trades (partial trades count fractionally)
    """
    bars = []
    current_volume = 0
    bar_data = []  # List of (price, volume, timestamp) tuples

    for trade in trades:
        price = trade['price']
        size = trade['size']
        timestamp = trade['participant_timestamp']

        remaining_size = size

        while remaining_size > 0:
            space_left = volume_per_bar - current_volume

            if remaining_size <= space_left:
                # Trade fits in current bar
                bar_data.append((price, remaining_size, timestamp))
                current_volume += remaining_size
                remaining_size = 0
            else:
                # Split trade: fill current bar and continue to next
                if space_left > 0:
                    bar_data.append((price, space_left, timestamp))
                    current_volume += space_left
                    remaining_size -= space_left

                # Complete current bar
                if current_volume >= volume_per_bar:
                    bar = compute_bar_stats(bar_data, bars)
                    bars.append(bar)

                    # Reset for next bar
                    current_volume = 0
                    bar_data = []

    # Handle remaining data
    if bar_data:
        bar = compute_bar_stats(bar_data, bars)
        bars.append(bar)

    return bars

def compute_bar_stats(bar_data, existing_bars):
    """Compute VWAP, stddev, and time duration for a bar."""
    prices = np.array([p for p, v, t in bar_data])
    volumes = np.array([v for p, v, t in bar_data])
    timestamps = [t for p, v, t in bar_data]

    total_volume = volumes.sum()
    vwap = np.sum(prices * volumes) / total_volume

    # Weighted standard deviation
    variance = np.sum(volumes * (prices - vwap) ** 2) / total_volume
    stddev = np.sqrt(variance)

    cumulative_volume = existing_bars[-1]['cumulative_volume'] + total_volume if existing_bars else total_volume

    # Calculate time duration in seconds
    time_duration_ns = timestamps[-1] - timestamps[0]
    time_duration_s = time_duration_ns / 1e9

    return {
        'cumulative_volume': cumulative_volume,
        'vwap': vwap,
        'stddev': stddev,
        'volume': total_volume,
        'start_time': timestamps[0],
        'end_time': timestamps[-1],
        'time_duration_s': time_duration_s,
        'num_trades': len(bar_data)
    }

def plot_volume_bars_vwap(bars, output_html, all_trades=None):
    """
    Create fixed volume bar chart with VWAP and stddev bands.
    X-axis: cumulative volume
    Y-axis: price (VWAP ± 2σ)
    Bottom panel: time duration for each volume bar
    Uses candlesticks where:
    - Open = VWAP - 2σ
    - Close = VWAP + 2σ
    - High = VWAP + 2σ
    - Low = VWAP - 2σ
    """
    fig = make_subplots(
        rows=2, cols=1,
        shared_xaxes=True,
        vertical_spacing=0.05,
        row_heights=[0.7, 0.3],
        subplot_titles=['VWAP ±2σ', 'Time Duration (seconds)']
    )

    x_vals = [b['cumulative_volume'] for b in bars]
    vwap_vals = [b['vwap'] for b in bars]
    upper_2sigma = [b['vwap'] + 2 * b['stddev'] for b in bars]
    lower_2sigma = [b['vwap'] - 2 * b['stddev'] for b in bars]
    time_durations = [b['time_duration_s'] for b in bars]

    # Convert nanosecond timestamps to human readable
    def format_time(ns_timestamp):
        dt = datetime.fromtimestamp(ns_timestamp / 1e9, timezone.utc)
        return dt.strftime('%H:%M:%S.%f')[:-3]  # Include milliseconds

    hover_text = [
        f"Volume: {b['cumulative_volume']:,.0f}<br>"
        f"VWAP: {b['vwap']:.2f}<br>"
        f"StdDev: {b['stddev']:.4f}<br>"
        f"+2σ: {b['vwap'] + 2 * b['stddev']:.2f}<br>"
        f"-2σ: {b['vwap'] - 2 * b['stddev']:.2f}<br>"
        f"Start: {format_time(b['start_time'])}<br>"
        f"End: {format_time(b['end_time'])}<br>"
        f"Duration: {b['time_duration_s']:.3f}s<br>"
        f"Trades: {b['num_trades']}"
        for b in bars
    ]

    # Add background shading for market hours zones (based on time, not volume)
    if all_trades and bars and show_extended_hours:
        hours = get_market_hours_bounds(all_trades)
        if hours:
            open_ts, close_ts = hours

            # Find volume positions corresponding to market hours
            open_vol = None
            close_vol = None

            for bar in bars:
                if open_vol is None and bar['end_time'] >= open_ts:
                    open_vol = bar['cumulative_volume'] - bar['volume']
                if close_vol is None and bar['start_time'] >= close_ts:
                    close_vol = bar['cumulative_volume'] - bar['volume']
                    break

            if open_vol is None:
                open_vol = 0
            if close_vol is None:
                close_vol = bars[-1]['cumulative_volume']

            shapes = []
            if open_vol > 0:
                shapes.extend([
                    dict(type="rect", xref="x", yref="paper", x0=0, x1=open_vol, y0=0, y1=0.7,
                         fillcolor="lightblue", opacity=0.3, layer="below", line_width=0),
                    dict(type="rect", xref="x2", yref="paper", x0=0, x1=open_vol, y0=0.7, y1=1,
                         fillcolor="lightblue", opacity=0.3, layer="below", line_width=0)
                ])
            shapes.extend([
                dict(type="rect", xref="x", yref="paper", x0=open_vol, x1=close_vol, y0=0, y1=0.7,
                     fillcolor="lightgreen", opacity=0.3, layer="below", line_width=0),
                dict(type="rect", xref="x2", yref="paper", x0=open_vol, x1=close_vol, y0=0.7, y1=1,
                     fillcolor="lightgreen", opacity=0.3, layer="below", line_width=0)
            ])
            if close_vol < bars[-1]['cumulative_volume']:
                shapes.extend([
                    dict(type="rect", xref="x", yref="paper", x0=close_vol, x1=bars[-1]['cumulative_volume'], y0=0, y1=0.7,
                         fillcolor="lightyellow", opacity=0.3, layer="below", line_width=0),
                    dict(type="rect", xref="x2", yref="paper", x0=close_vol, x1=bars[-1]['cumulative_volume'], y0=0.7, y1=1,
                         fillcolor="lightyellow", opacity=0.3, layer="below", line_width=0)
                ])

            fig.update_layout(shapes=shapes)

    # Prepare custom data for hover template
    customdata = [[
        b['cumulative_volume'],
        b['vwap'],
        b['stddev'],
        b['vwap'] + 2 * b['stddev'],
        b['vwap'] - 2 * b['stddev'],
        format_time(b['start_time']),
        format_time(b['end_time']),
        b['time_duration_s'],
        b['num_trades']
    ] for b in bars]

    # Calculate session VWAP for minimum height scaling
    total_volume = sum(b['volume'] for b in bars)
    session_vwap = sum(b['vwap'] * b['volume'] for b in bars) / total_volume if total_volume > 0 else 1.0

    # Calculate bar heights and bases with minimum height scaled to session VWAP
    bar_heights = []
    bar_bases = []
    min_height = min(0.01, 0.001 * session_vwap)

    for upper, lower, vwap in zip(upper_2sigma, lower_2sigma, vwap_vals):
        height = upper - lower
        actual_height = max(height, min_height)

        # Center the bar on the VWAP line for small bars
        bar_heights.append(actual_height)
        bar_bases.append(vwap - actual_height / 2)

    # Calculate bar colors (green for up, red for down)
    colors = ['green' if upper >= lower else 'red'
              for upper, lower in zip(upper_2sigma, lower_2sigma)]

    # Plot bars on main panel (replacing candlesticks)
    fig.add_trace(go.Bar(
        x=x_vals,
        y=bar_heights,
        base=bar_bases,
        name='VWAP ±2σ',
        marker_color=colors,
        marker_line_width=0,
        width=[vol * 0.8 for vol in [bars[i]['volume'] for i in range(len(bars))]],
        customdata=customdata,
        hovertemplate='<b>Volume:</b> %{customdata[0]:,.0f}<br>' +
                      '<b>VWAP:</b> %{customdata[1]:.2f}<br>' +
                      '<b>StdDev:</b> %{customdata[2]:.4f}<br>' +
                      '<b>+2σ:</b> %{customdata[3]:.2f}<br>' +
                      '<b>-2σ:</b> %{customdata[4]:.2f}<br>' +
                      '<b>Start:</b> %{customdata[5]}<br>' +
                      '<b>End:</b> %{customdata[6]}<br>' +
                      '<b>Duration:</b> %{customdata[7]:.3f}s<br>' +
                      '<b>Trades:</b> %{customdata[8]}<extra></extra>'
    ), row=1, col=1)

    # Add VWAP line overlay
    fig.add_trace(go.Scatter(
        x=x_vals,
        y=vwap_vals,
        mode='lines',
        name='VWAP',
        line=dict(color='blue', width=1),
        hoverinfo='skip'
    ), row=1, col=1)

    # Plot time duration area on bottom panel (inverted)
    fig.add_trace(go.Scatter(
        x=x_vals,
        y=time_durations,
        fill='tozeroy',
        mode='lines',
        name='Time Duration',
        line=dict(color='blue', width=1),
        fillcolor='rgba(0, 0, 255, 0.3)',
        hovertemplate='Volume: %{x:,.0f}<br>Duration: %{y:.3f}s<extra></extra>'
    ), row=2, col=1)

    fig.update_layout(
        height=700,
        width=1400,
        hovermode='closest',
        hoverdistance=100,
        xaxis2_title='Cumulative Volume',
        showlegend=False
    )

    fig.update_xaxes(rangeslider_visible=False, row=1, col=1)
    fig.update_yaxes(title_text='Price', row=1, col=1)
    fig.update_yaxes(title_text='Seconds', row=2, col=1, autorange='reversed')

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
    parser = argparse.ArgumentParser(description='Generate volume-based VWAP charts from trade data')
    parser.add_argument('input_json', help='Path to trades JSON file')
    parser.add_argument('-v', '--volume-per-bar', type=int, default=None, help='Volume per bar (auto-calculated if not specified)')
    parser.add_argument('-o', '--output', help='Output HTML file path')
    parser.add_argument('--show-extended-hours', action='store_true', default=True, help='Show extended hours (default: True)')
    parser.add_argument('--no-extended-hours', dest='show_extended_hours', action='store_false', help='Hide extended hours')

    args = parser.parse_args()

    input_json = args.input_json
    volume_per_bar = args.volume_per_bar
    show_extended_hours = args.show_extended_hours

    if args.output:
        output_html = args.output
    else:
        ticker = os.path.basename(os.path.dirname(input_json))
        date = os.path.splitext(os.path.basename(input_json))[0]
        output_dir = f'data/charts/massive/{ticker}_{date}'
        os.makedirs(output_dir, exist_ok=True)
        output_html = f'{output_dir}/volume.html'

    print(f'Loading trades from {input_json}...')
    all_trades = load_trades(input_json)
    print(f'Loaded {len(all_trades)} trades')

    # Filter out special trade types
    all_trades = filter_trades(all_trades, exclude_odd_lots=False, exclude_extended_hours=False)
    print(f'After filtering: {len(all_trades)} trades')

    # Auto-calculate volume_per_bar if not specified
    if volume_per_bar is None:
        hours = get_market_hours_bounds(all_trades)
        if hours:
            open_ts, close_ts = hours
            regular_hours_trades = [t for t in all_trades if open_ts <= t['participant_timestamp'] <= close_ts]
            regular_hours_volume = sum(t['size'] for t in regular_hours_trades)
            volume_per_bar = int(np.ceil(regular_hours_volume / 3000 / 1000) * 1000)
            print(f'Auto-calculated volume_per_bar: {volume_per_bar} (regular hours volume: {regular_hours_volume:,.0f})')
        else:
            total_volume = sum(t['size'] for t in all_trades)
            volume_per_bar = int(np.ceil(total_volume / 3000 / 1000) * 1000)
            print(f'Auto-calculated volume_per_bar: {volume_per_bar} (total volume: {total_volume:,.0f}, market hours not determined)')

    # Filter to regular hours if requested
    trades = all_trades
    if not show_extended_hours:
        hours = get_market_hours_bounds(all_trades)
        if hours:
            open_ts, close_ts = hours
            trades = [t for t in all_trades if open_ts <= t['participant_timestamp'] <= close_ts]
            print(f'Filtered to regular hours: {len(trades)} trades')

    print(f'Creating volume bars with {volume_per_bar} volume per bar...')
    bars = create_volume_bars_vwap(trades, volume_per_bar)
    print(f'Created {len(bars)} volume bars')

    print(f'Plotting...')
    plot_volume_bars_vwap(bars, output_html, all_trades)
