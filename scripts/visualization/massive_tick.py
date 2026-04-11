import sys
import os
import math
import plotly.graph_objects as go
from plotly.subplots import make_subplots
from datetime import datetime, timezone
from trade_filters import filter_trades
from market_hours import get_market_hours_bounds
from trade_io import load_trades as _load_parquet_trades

def load_trades(trades_path):
    trades = _load_parquet_trades(trades_path)
    for t in trades:
        if t['participant_timestamp'] == 0:
            t['participant_timestamp'] = t['sip_timestamp']
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

def plot_trades(trades, output_html, all_trades, show_extended_hours=True):
    hours = get_market_hours_bounds(all_trades)

    if not show_extended_hours and hours:
        open_ts, close_ts = hours
        trades = [t for t in trades if open_ts <= t['participant_timestamp'] <= close_ts]
        print(f'Filtered to regular hours: {len(trades)} trades')

    if not trades:
        print('No trades to plot')
        return

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

    # Add background shading for market hours zones
    if hours and show_extended_hours:
        open_ts, close_ts = hours
        open_dt = datetime.fromtimestamp(open_ts / 1e9, timezone.utc)
        close_dt = datetime.fromtimestamp(close_ts / 1e9, timezone.utc)

        first_ts = min(t['participant_timestamp'] for t in all_trades)
        last_ts = max(t['participant_timestamp'] for t in all_trades)
        first_dt = datetime.fromtimestamp(first_ts / 1e9, timezone.utc)
        last_dt = datetime.fromtimestamp(last_ts / 1e9, timezone.utc)

        shapes = []
        if first_dt < open_dt:
            shapes.extend([
                dict(type="rect", xref="x", yref="paper", x0=first_dt, x1=open_dt, y0=0, y1=0.75,
                     fillcolor="lightblue", opacity=0.3, layer="below", line_width=0),
                dict(type="rect", xref="x2", yref="paper", x0=first_dt, x1=open_dt, y0=0.75, y1=1,
                     fillcolor="lightblue", opacity=0.3, layer="below", line_width=0)
            ])
        shapes.extend([
            dict(type="rect", xref="x", yref="paper", x0=open_dt, x1=close_dt, y0=0, y1=0.75,
                 fillcolor="lightgreen", opacity=0.3, layer="below", line_width=0),
            dict(type="rect", xref="x2", yref="paper", x0=open_dt, x1=close_dt, y0=0.75, y1=1,
                 fillcolor="lightgreen", opacity=0.3, layer="below", line_width=0)
        ])
        if last_dt > close_dt:
            shapes.extend([
                dict(type="rect", xref="x", yref="paper", x0=close_dt, x1=last_dt, y0=0, y1=0.75,
                     fillcolor="lightyellow", opacity=0.3, layer="below", line_width=0),
                dict(type="rect", xref="x2", yref="paper", x0=close_dt, x1=last_dt, y0=0.75, y1=1,
                     fillcolor="lightyellow", opacity=0.3, layer="below", line_width=0)
            ])

        fig.update_layout(shapes=shapes)

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
    input_json = sys.argv[1] if len(sys.argv) > 1 else 'data/trades/LW/2025-12-19.parquet'
    if len(sys.argv) > 2 and sys.argv[2]:
        output_html = sys.argv[2]
    else:
        ticker = os.path.basename(os.path.dirname(input_json))
        date = os.path.splitext(os.path.basename(input_json))[0]
        output_dir = f'data/charts/massive/{ticker}_{date}'
        os.makedirs(output_dir, exist_ok=True)
        output_html = f'{output_dir}/tick.html'
    show_extended_hours = sys.argv[3].lower() == 'true' if len(sys.argv) > 3 else True

    print(f'Loading trades from {input_json}...')
    all_trades = load_trades(input_json)
    print(f'Loaded {len(all_trades)} trades')

    # Filter out special trade types
    all_trades = filter_trades(all_trades, exclude_odd_lots=False, exclude_extended_hours=False)
    print(f'After filtering: {len(all_trades)} trades')

    print(f'Merging trades within 100 microseconds...')
    trades = merge_trades(all_trades)
    print(f'After merging: {len(trades)} trades')

    print(f'Plotting...')
    plot_trades(trades, output_html, all_trades, show_extended_hours)
