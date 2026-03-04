import json
import sys
import os
import math
import plotly.graph_objects as go
from plotly.subplots import make_subplots
from datetime import datetime

def load_trades(json_path):
    with open(json_path) as f:
        trades = json.load(f)
    trades.sort(key=lambda t: t['participant_timestamp'])
    return trades

def plot_trades(trades, output_html, market_open=15.5, market_close=22.0):
    # Filter to market hours
    open_minutes = market_open * 60
    close_minutes = market_close * 60
    filtered = []
    for t in trades:
        dt = datetime.fromtimestamp(t['participant_timestamp'] / 1e9)
        ts_minutes = dt.hour * 60 + dt.minute
        if open_minutes <= ts_minutes < close_minutes:
            filtered.append(t)

    print(f'Filtered to {len(filtered)} trades ({market_open}:00-{market_close}:00 UTC)')
    trades = filtered

    # Plot all trades (Scattergl handles large datasets efficiently)
    times = [datetime.fromtimestamp(t['participant_timestamp'] / 1e9) for t in trades]
    prices = [t['price'] for t in trades]
    sizes = [t['size'] for t in trades]
    marker_sizes = [0.5 * math.sqrt(s) for s in sizes]

    fig = make_subplots(rows=2, cols=1, shared_xaxes=True,
        row_heights=[3, 1], vertical_spacing=0.05,
        subplot_titles=['Price', 'Trade Size'])

    fig.add_trace(go.Scattergl(
        x=times, y=prices, mode='markers',
        marker=dict(size=marker_sizes, color='blue', opacity=0.3),
        name='Price'
    ), row=1, col=1)

    fig.add_trace(go.Scattergl(
        x=times, y=sizes, mode='markers',
        marker=dict(size=marker_sizes, color='blue', opacity=0.3),
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
    output_html = sys.argv[2] if len(sys.argv) > 2 else 'data/charts/massive_tick.html'
    market_open = float(sys.argv[3]) if len(sys.argv) > 3 else 15.5
    market_close = float(sys.argv[4]) if len(sys.argv) > 4 else 22.0

    print(f'Loading trades from {input_json}...')
    trades = load_trades(input_json)
    print(f'Loaded {len(trades)} trades')

    print(f'Plotting...')
    plot_trades(trades, output_html, market_open, market_close)
