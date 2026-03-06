import json
import sys
import os
import numpy as np
import plotly.graph_objects as go
from plotly.subplots import make_subplots
from datetime import datetime, timezone

def load_trades(json_path):
    """Load trades from Massive JSON file."""
    with open(json_path) as f:
        trades = json.load(f)

    # Sort by participant_timestamp
    trades.sort(key=lambda t: t['participant_timestamp'])
    return trades

def create_time_bars(trades, seconds_per_bar):
    """
    Group trades into fixed time intervals and compute OHLC.

    Returns list of bars with:
    - timestamp: bar start time
    - open, high, low, close: OHLC prices
    - volume: total volume in bar
    - num_trades: number of trades
    """
    if not trades:
        return []

    bars = []
    start_time = trades[0]['participant_timestamp']
    bar_duration_ns = seconds_per_bar * 1_000_000_000

    current_bar_start = start_time
    current_bar_end = current_bar_start + bar_duration_ns
    bar_trades = []

    for trade in trades:
        timestamp = trade['participant_timestamp']

        # Check if we need to start a new bar
        while timestamp >= current_bar_end:
            # Complete current bar if it has trades
            if bar_trades:
                bar = compute_bar_ohlc(bar_trades, current_bar_start)
                bars.append(bar)
                bar_trades = []

            # Move to next bar
            current_bar_start = current_bar_end
            current_bar_end = current_bar_start + bar_duration_ns

        # Add trade to current bar
        bar_trades.append(trade)

    # Handle remaining trades
    if bar_trades:
        bar = compute_bar_ohlc(bar_trades, current_bar_start)
        bars.append(bar)

    return bars

def compute_bar_ohlc(bar_trades, bar_start_time):
    """Compute OHLC for a time bar."""
    prices = [t['price'] for t in bar_trades]
    volumes = [t['size'] for t in bar_trades]

    return {
        'timestamp': bar_start_time,
        'open': bar_trades[0]['price'],
        'high': max(prices),
        'low': min(prices),
        'close': bar_trades[-1]['price'],
        'volume': sum(volumes),
        'num_trades': len(bar_trades)
    }

def plot_candlesticks(bars, output_html, seconds_per_bar):
    """
    Create traditional time-based candlestick chart.
    X-axis: time
    Y-axis: price
    """
    fig = make_subplots(
        rows=2, cols=1,
        shared_xaxes=True,
        vertical_spacing=0.05,
        row_heights=[0.7, 0.3],
        subplot_titles=['Price', 'Volume']
    )

    # Convert nanosecond timestamps to datetime
    def ns_to_datetime(ns_timestamp):
        return datetime.fromtimestamp(ns_timestamp / 1e9, timezone.utc)

    x_vals = [ns_to_datetime(b['timestamp']) for b in bars]
    open_vals = [b['open'] for b in bars]
    high_vals = [b['high'] for b in bars]
    low_vals = [b['low'] for b in bars]
    close_vals = [b['close'] for b in bars]
    volumes = [b['volume'] for b in bars]

    hover_text = [
        f"Time: {ns_to_datetime(b['timestamp']).strftime('%H:%M:%S')}<br>"
        f"O: {b['open']:.2f} H: {b['high']:.2f}<br>"
        f"L: {b['low']:.2f} C: {b['close']:.2f}<br>"
        f"Volume: {b['volume']:,}<br>"
        f"Trades: {b['num_trades']}"
        for b in bars
    ]

    # Plot candlesticks
    fig.add_trace(go.Candlestick(
        x=x_vals,
        open=open_vals,
        high=high_vals,
        low=low_vals,
        close=close_vals,
        name='Price',
        text=hover_text,
        hoverinfo='text'
    ), row=1, col=1)

    fig.add_trace(go.Bar(
        x=x_vals,
        y=volumes,
        name='Volume',
        marker_color='blue',
        opacity=0.5
    ), row=2, col=1)

    fig.update_layout(
        title=f'Traditional Candlestick Chart ({seconds_per_bar}s bars) - Massive Data',
        height=900,
        width=1400,
        hovermode='x unified',
        showlegend=False,
        xaxis2_title='Time'
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
    input_json = sys.argv[1] if len(sys.argv) > 1 else 'data/trades/LW/2025-12-19.json'
    seconds_per_bar = int(sys.argv[2]) if len(sys.argv) > 2 else 60
    if len(sys.argv) > 3:
        output_html = sys.argv[3]
    else:
        ticker = os.path.basename(os.path.dirname(input_json))
        date = os.path.splitext(os.path.basename(input_json))[0]
        output_dir = f'data/charts/massive/{ticker}_{date}'
        os.makedirs(output_dir, exist_ok=True)
        output_html = f'{output_dir}/candle.html'

    print(f'Loading trades from {input_json}...')
    trades = load_trades(input_json)
    print(f'Loaded {len(trades)} trades')

    print(f'Creating {seconds_per_bar}s time bars...')
    bars = create_time_bars(trades, seconds_per_bar)
    print(f'Created {len(bars)} time bars')

    print(f'Plotting...')
    plot_candlesticks(bars, output_html, seconds_per_bar)
