import json
import sys
import os
import math
import plotly.graph_objects as go
from plotly.subplots import make_subplots
from datetime import datetime, timezone
from trade_filters import filter_trades

def load_trades(json_path):
    with open(json_path) as f:
        trades = json.load(f)

    # OTC stocks have participant_timestamp=0; fall back to sip_timestamp
    for t in trades:
        if t['participant_timestamp'] == 0:
            t['participant_timestamp'] = t['sip_timestamp']

    trades.sort(key=lambda t: t['participant_timestamp'])
    return trades

def merge_trades(trades, threshold_ns=100000):
    """Merge trades within threshold_ns nanoseconds to reduce HFT noise."""
    if not trades:
        return []

    merged = []
    current_group = [trades[0]]

    for trade in trades[1:]:
        if trade['participant_timestamp'] - current_group[0]['participant_timestamp'] < threshold_ns:
            current_group.append(trade)
        else:
            merged.append(merge_group(current_group))
            current_group = [trade]

    merged.append(merge_group(current_group))
    return merged

def merge_group(group):
    """Merge a group of trades into one with VWAP."""
    if len(group) == 1:
        return group[0]

    total_size = sum(t['size'] for t in group)
    vwap = sum(t['price'] * t['size'] for t in group) / total_size

    return {
        'participant_timestamp': group[0]['participant_timestamp'],
        'price': vwap,
        'size': total_size
    }

def plot_trades(trades, output_html, market_open=14.5, market_close=21.0):
    # Filter to market hours (UTC)
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

    # Plot all trades (Scattergl handles large datasets efficiently)
    times = [datetime.fromtimestamp(t['participant_timestamp'] / 1e9, timezone.utc) for t in trades]
    prices = [t['price'] for t in trades]
    sizes = [t['size'] for t in trades]
    marker_sizes = [0.5 * math.sqrt(s) for s in sizes]

    hover_text = [
        f"Time: {times[i].strftime('%H:%M:%S.%f')[:-3]}<br>"
        f"Price: ${prices[i]:.2f}<br>"
        f"Size: {sizes[i]:,}"
        for i in range(len(trades))
    ]

    fig = make_subplots(rows=2, cols=1, shared_xaxes=True,
        row_heights=[3, 1], vertical_spacing=0.05,
        subplot_titles=['Price', 'Trade Size'])

    fig.add_trace(go.Scattergl(
        x=times, y=prices, mode='markers',
        marker=dict(size=marker_sizes, color='blue', opacity=0.3),
        text=hover_text,
        hoverinfo='text',
        name='Price'
    ), row=1, col=1)

    fig.add_trace(go.Scattergl(
        x=times, y=sizes, mode='markers',
        marker=dict(size=marker_sizes, color='blue', opacity=0.3),
        text=hover_text,
        hoverinfo='text',
        name='Size', showlegend=False
    ), row=2, col=1)

    fig.update_layout(height=900, width=1400, title=f'Trade Data (Massive)',
        xaxis2=dict(title='Time'))
    fig.update_yaxes(title_text='Price', row=1, col=1)
    fig.update_yaxes(title_text='Size', row=2, col=1)

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
    if len(sys.argv) > 2 and sys.argv[2]:
        output_html = sys.argv[2]
    else:
        ticker = os.path.basename(os.path.dirname(input_json))
        date = os.path.splitext(os.path.basename(input_json))[0]
        output_dir = f'data/charts/massive/{ticker}_{date}'
        os.makedirs(output_dir, exist_ok=True)
        output_html = f'{output_dir}/tick.html'
    market_open = float(sys.argv[3]) if len(sys.argv) > 3 else 13.5
    market_close = float(sys.argv[4]) if len(sys.argv) > 4 else 20.0

    print(f'Loading trades from {input_json}...')
    trades = load_trades(input_json)
    print(f'Loaded {len(trades)} trades')

    # Filter out special trade types
    trades = filter_trades(trades, exclude_odd_lots=False, exclude_extended_hours=False)
    print(f'After filtering: {len(trades)} trades')

    print(f'Merging trades within 100 microseconds...')
    trades = merge_trades(trades)
    print(f'After merging: {len(trades)} trades')

    print(f'Plotting...')
    plot_trades(trades, output_html, market_open, market_close)
