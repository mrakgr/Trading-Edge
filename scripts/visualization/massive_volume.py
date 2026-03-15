import json
import sys
import os
import numpy as np
import plotly.graph_objects as go
from plotly.subplots import make_subplots
from datetime import datetime, timezone
from trade_filters import filter_trades

def load_trades(json_path):
    """Load trades from Massive JSON file."""
    with open(json_path) as f:
        trades = json.load(f)

    # Sort by participant_timestamp
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

def plot_volume_bars_vwap(bars, output_html):
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

    # Plot candlesticks on main panel
    fig.add_trace(go.Candlestick(
        x=x_vals,
        open=lower_2sigma,
        high=upper_2sigma,
        low=lower_2sigma,
        close=upper_2sigma,
        name='VWAP ±2σ',
        increasing_line_color='green',
        decreasing_line_color='red',
        text=hover_text,
        hoverinfo='text'
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
        title='Fixed Volume Bars - VWAP with ±2σ Bands and Time Duration (Massive Data)',
        height=1000,
        width=1400,
        hovermode='closest',
        xaxis2_title='Cumulative Volume',
        showlegend=True
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
    input_json = sys.argv[1] if len(sys.argv) > 1 else 'data/trades/LW/2025-12-19.json'
    volume_per_bar = int(sys.argv[2]) if len(sys.argv) > 2 else 10000
    if len(sys.argv) > 3 and sys.argv[3]:
        output_html = sys.argv[3]
    else:
        ticker = os.path.basename(os.path.dirname(input_json))
        date = os.path.splitext(os.path.basename(input_json))[0]
        output_dir = f'data/charts/massive/{ticker}_{date}'
        os.makedirs(output_dir, exist_ok=True)
        output_html = f'{output_dir}/volume.html'
    market_open = float(sys.argv[4]) if len(sys.argv) > 4 else 13.5   # UTC
    market_close = float(sys.argv[5]) if len(sys.argv) > 5 else 20.0  # UTC

    print(f'Loading trades from {input_json}...')
    trades = load_trades(input_json)
    print(f'Loaded {len(trades)} trades')

    # Filter out special trade types
    trades = filter_trades(trades, exclude_odd_lots=False, exclude_extended_hours=False)
    print(f'After filtering: {len(trades)} trades')

    # Filter to market hours
    open_minutes = market_open * 60
    close_minutes = market_close * 60
    filtered = []
    for t in trades:
        dt = datetime.fromtimestamp(t['participant_timestamp'] / 1e9, timezone.utc)
        ts_minutes = dt.hour * 60 + dt.minute
        if open_minutes <= ts_minutes < close_minutes:
            filtered.append(t)
    print(f'Filtered to {len(filtered)} trades ({market_open}:00-{market_close}:00 UTC)')
    trades = filtered

    print(f'Creating volume bars with {volume_per_bar} volume per bar...')
    bars = create_volume_bars_vwap(trades, volume_per_bar)
    print(f'Created {len(bars)} volume bars')

    print(f'Plotting...')
    plot_volume_bars_vwap(bars, output_html)
