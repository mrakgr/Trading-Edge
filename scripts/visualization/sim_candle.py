import csv
import sys
import os
import plotly.graph_objects as go
from plotly.subplots import make_subplots

def load_trades(csv_path):
    trades = []
    with open(csv_path) as f:
        r = csv.DictReader(f)
        for row in r:
            trades.append({
                'time': float(row['time']),
                'price': float(row['price']),
                'size': int(row['size']),
                'day_target': float(row['day_target']),
                'day_sigma': float(row['day_variance']) ** 0.5,
                'session_target': float(row['session_target']),
                'session_sigma': float(row['session_variance']) ** 0.5,
                'trend_target': float(row['trend_target']),
                'trend_sigma': float(row['trend_variance']) ** 0.5,
            })
    return trades

def create_time_bars(trades, seconds_per_bar):
    if not trades:
        return []

    # Convert seconds to minutes since trade times are in minutes
    minutes_per_bar = seconds_per_bar / 60.0

    bars = []
    start_time = trades[0]['time']
    current_bar_start = start_time
    current_bar_end = current_bar_start + minutes_per_bar
    bar_trades = []

    for trade in trades:
        timestamp = trade['time']

        while timestamp >= current_bar_end:
            if bar_trades:
                bars.append(compute_bar_ohlc(bar_trades, current_bar_start))
                bar_trades = []
            current_bar_start = current_bar_end
            current_bar_end = current_bar_start + minutes_per_bar

        bar_trades.append(trade)

    if bar_trades:
        bars.append(compute_bar_ohlc(bar_trades, current_bar_start))

    return bars

def compute_bar_ohlc(bar_trades, bar_start_time):
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
    fig = make_subplots(
        rows=2, cols=1,
        shared_xaxes=True,
        vertical_spacing=0.05,
        row_heights=[0.7, 0.3],
        subplot_titles=['Price', 'Volume']
    )

    x_vals = [b['timestamp'] for b in bars]  # Already in minutes
    open_vals = [b['open'] for b in bars]
    high_vals = [b['high'] for b in bars]
    low_vals = [b['low'] for b in bars]
    close_vals = [b['close'] for b in bars]
    volumes = [b['volume'] for b in bars]

    hover_text = [
        f"Time: {b['timestamp']:.2f}m<br>"
        f"O: {b['open']:.4f} H: {b['high']:.4f}<br>"
        f"L: {b['low']:.4f} C: {b['close']:.4f}<br>"
        f"Volume: {b['volume']:,}<br>"
        f"Trades: {b['num_trades']}"
        for b in bars
    ]

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

    # Add target lines for all three levels
    # Get target values from first trade in each bar
    bar_times = [b['timestamp'] for b in bars]

    # Find corresponding trades for each bar to get targets
    bar_targets = []
    trade_idx = 0
    for bar in bars:
        bar_start = bar['timestamp']
        # Find first trade in this bar
        while trade_idx < len(trades) and trades[trade_idx]['time'] < bar_start:
            trade_idx += 1
        if trade_idx < len(trades):
            bar_targets.append(trades[trade_idx])
        else:
            bar_targets.append(trades[-1])

    x_minutes = bar_times  # Already in minutes

    # Day level
    fig.add_trace(go.Scatter(
        x=x_minutes,
        y=[t['day_target'] for t in bar_targets],
        mode='lines',
        line=dict(color='purple', width=2, dash='dash'),
        name='Day Target'
    ), row=1, col=1)
    fig.add_trace(go.Scatter(
        x=x_minutes,
        y=[t['day_target'] + t['day_sigma'] for t in bar_targets],
        mode='lines',
        line=dict(color='purple', width=1, dash='dash'),
        name='Day +1σ',
        showlegend=False
    ), row=1, col=1)
    fig.add_trace(go.Scatter(
        x=x_minutes,
        y=[t['day_target'] - t['day_sigma'] for t in bar_targets],
        mode='lines',
        line=dict(color='purple', width=1, dash='dash'),
        name='Day -1σ',
        showlegend=False
    ), row=1, col=1)

    # Session level
    fig.add_trace(go.Scatter(
        x=x_minutes,
        y=[t['session_target'] for t in bar_targets],
        mode='lines',
        line=dict(color='orange', width=2, dash='dot'),
        name='Session Target'
    ), row=1, col=1)
    fig.add_trace(go.Scatter(
        x=x_minutes,
        y=[t['session_target'] + t['session_sigma'] for t in bar_targets],
        mode='lines',
        line=dict(color='orange', width=1, dash='dot'),
        name='Session +1σ',
        showlegend=False
    ), row=1, col=1)
    fig.add_trace(go.Scatter(
        x=x_minutes,
        y=[t['session_target'] - t['session_sigma'] for t in bar_targets],
        mode='lines',
        line=dict(color='orange', width=1, dash='dot'),
        name='Session -1σ',
        showlegend=False
    ), row=1, col=1)

    # Trend level
    fig.add_trace(go.Scatter(
        x=x_minutes,
        y=[t['trend_target'] for t in bar_targets],
        mode='lines',
        line=dict(color='cyan', width=2),
        name='Trend Target'
    ), row=1, col=1)
    fig.add_trace(go.Scatter(
        x=x_minutes,
        y=[t['trend_target'] + t['trend_sigma'] for t in bar_targets],
        mode='lines',
        line=dict(color='cyan', width=1),
        name='Trend +1σ',
        showlegend=False
    ), row=1, col=1)
    fig.add_trace(go.Scatter(
        x=x_minutes,
        y=[t['trend_target'] - t['trend_sigma'] for t in bar_targets],
        mode='lines',
        line=dict(color='cyan', width=1),
        name='Trend -1σ',
        showlegend=False
    ), row=1, col=1)

    fig.add_trace(go.Bar(
        x=x_vals,
        y=volumes,
        name='Volume',
        marker_color='blue',
        opacity=0.5
    ), row=2, col=1)

    fig.update_layout(
        title=f'Candlestick Chart ({seconds_per_bar}s bars) - Simulation Data',
        height=900,
        width=1400,
        hovermode='x unified',
        showlegend=True,
        xaxis2_title='Time (minutes)'
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
    input_csv = sys.argv[1] if len(sys.argv) > 1 else 'data/test_hmm.csv'
    seconds_per_bar = int(sys.argv[2]) if len(sys.argv) > 2 else 60
    if len(sys.argv) > 3:
        output_html = sys.argv[3]
    else:
        basename = os.path.splitext(os.path.basename(input_csv))[0]
        output_dir = f'data/charts/sim/{basename}'
        os.makedirs(output_dir, exist_ok=True)
        output_html = f'{output_dir}/candle.html'

    print(f'Loading trades from {input_csv}...')
    trades = load_trades(input_csv)
    print(f'Loaded {len(trades)} trades')

    print(f'Creating {seconds_per_bar}s time bars...')
    bars = create_time_bars(trades, seconds_per_bar)
    print(f'Created {len(bars)} time bars')

    print(f'Plotting...')
    plot_candlesticks(bars, output_html, seconds_per_bar)
