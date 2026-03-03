import json
import sys
import numpy as np
import plotly.graph_objects as go
from datetime import datetime
from collections import defaultdict

def load_trades(json_path):
    """Load trades from Massive JSON file."""
    with open(json_path) as f:
        trades = json.load(f)
    trades.sort(key=lambda t: t['participant_timestamp'])
    return trades

def create_time_bars_footprint(trades, seconds_per_bar, price_rounding=2):
    """
    Group trades into fixed time intervals.
    Each bar stores volume at each price level (rounded).
    """
    if not trades:
        return []

    bars = []
    bar_duration_ns = seconds_per_bar * 1_000_000_000
    current_bar_start = trades[0]['participant_timestamp']
    current_bar_end = current_bar_start + bar_duration_ns
    volume_at_price = defaultdict(int)
    bar_trades = []

    for trade in trades:
        ts = trade['participant_timestamp']

        while ts >= current_bar_end:
            if bar_trades:
                bars.append(make_bar(volume_at_price, bar_trades, current_bar_start))
            volume_at_price = defaultdict(int)
            bar_trades = []
            current_bar_start = current_bar_end
            current_bar_end = current_bar_start + bar_duration_ns

        rounded_price = round(trade['price'], price_rounding)
        volume_at_price[rounded_price] += trade['size']
        bar_trades.append(trade)

    if bar_trades:
        bars.append(make_bar(volume_at_price, bar_trades, current_bar_start))

    return bars

def make_bar(volume_at_price, bar_trades, bar_start):
    prices = [t['price'] for t in bar_trades]
    return {
        'timestamp': bar_start,
        'open': bar_trades[0]['price'],
        'high': max(prices),
        'low': min(prices),
        'close': bar_trades[-1]['price'],
        'total_volume': sum(t['size'] for t in bar_trades),
        'volume_at_price': dict(volume_at_price),
        'num_trades': len(bar_trades)
    }

def ns_to_datetime(ns_timestamp):
    return datetime.fromtimestamp(ns_timestamp / 1e9)

def plot_footprint(bars, output_html, seconds_per_bar):
    """
    Create footprint chart using a heatmap.
    X-axis: time (bar index)
    Y-axis: price
    Color: volume at that price level
    """
    fig = go.Figure()

    # Collect all price levels
    all_prices = set()
    for bar in bars:
        all_prices.update(bar['volume_at_price'].keys())
    price_levels = sorted(all_prices)

    if not price_levels:
        print('No price data to plot')
        return

    # Build heatmap matrix: rows = price levels, cols = bars
    z = []
    text = []
    for price in price_levels:
        z_row = []
        text_row = []
        for bar in bars:
            vol = bar['volume_at_price'].get(price, 0)
            if vol > 0:
                z_row.append(np.log10(vol))  # Log scale for color
                if vol >= 1000:
                    text_row.append(f'{vol/1000:.1f}k')
                else:
                    text_row.append(str(vol))
            else:
                z_row.append(None)
                text_row.append('')
        z.append(z_row)
        text.append(text_row)

    # X-axis labels as time strings
    x_labels = [
        datetime.fromtimestamp(bar['timestamp'] / 1e9).strftime('%H:%M:%S')
        for bar in bars
    ]

    fig.add_trace(go.Heatmap(
        x=x_labels,
        y=price_levels,
        z=z,
        text=text,
        colorscale='Blues',
        connectgaps=False,
        colorbar=dict(title='log10(vol)'),
        hovertemplate='Time: %{x}<br>Price: %{y:.2f}<br>Volume: %{text}<extra></extra>'
    ))

    fig.update_layout(
        title=f'Footprint Chart ({seconds_per_bar}s bars) - Volume at Price',
        xaxis_title='Time',
        yaxis_title='Price',
        height=900,
        width=1600,
        hovermode='closest'
    )

    fig.write_html(output_html)
    print(f'Saved to {output_html}')

if __name__ == '__main__':
    input_json = sys.argv[1] if len(sys.argv) > 1 else 'data/trades/LW/2025-12-19.json'
    seconds_per_bar = int(sys.argv[2]) if len(sys.argv) > 2 else 30
    output_html = sys.argv[3] if len(sys.argv) > 3 else 'data/footprint_massive.html'
    max_bars = int(sys.argv[4]) if len(sys.argv) > 4 else 0  # 0 = all bars
    start_hour = int(sys.argv[5]) if len(sys.argv) > 5 else 15  # Default to market open (UTC)

    print(f'Loading trades from {input_json}...')
    trades = load_trades(input_json)
    print(f'Loaded {len(trades)} trades')

    # Filter to start at specified hour
    start_cutoff = None
    for t in trades:
        dt = datetime.fromtimestamp(t['participant_timestamp'] / 1e9)
        if dt.hour >= start_hour:
            start_cutoff = t['participant_timestamp']
            break

    if start_cutoff:
        trades = [t for t in trades if t['participant_timestamp'] >= start_cutoff]
        print(f'Filtered to {len(trades)} trades starting at hour {start_hour}:00 UTC')

    print(f'Creating {seconds_per_bar}s footprint bars...')
    bars = create_time_bars_footprint(trades, seconds_per_bar)
    print(f'Created {len(bars)} bars total')
    if max_bars > 0:
        bars = bars[:max_bars]
        print(f'Using first {max_bars} bars')

    # Stats
    total_cells = sum(len(b['volume_at_price']) for b in bars)
    print(f'Total price levels across all bars: {total_cells}')

    print(f'Plotting...')
    plot_footprint(bars, output_html, seconds_per_bar)
