import csv
import sys
import os
import numpy as np
import plotly.graph_objects as go
from plotly.subplots import make_subplots

def load_trades(csv_path):
    """Load trades from simulation CSV file."""
    trades = []
    with open(csv_path) as f:
        r = csv.DictReader(f)
        for row in r:
            trades.append({
                'time': float(row['time']),
                'price': float(row['price']),
                'size': int(row['size']),
                'target_mean': float(row['target_mean']),
                'target_sigma': float(row['target_sigma']),
                'label': row['label'],
            })
    return trades

def create_volume_bars(trades, volume_per_bar):
    """
    Group trades into fixed volume chunks with trade splitting.
    Computes VWAP with stddev per bar.
    """
    bars = []
    current_volume = 0
    bar_data = []

    for trade in trades:
        price = trade['price']
        size = trade['size']
        time = trade['time']
        target_mean = trade['target_mean']
        target_sigma = trade['target_sigma']
        label = trade['label']

        remaining_size = size

        while remaining_size > 0:
            space_left = volume_per_bar - current_volume

            if remaining_size <= space_left:
                bar_data.append((price, remaining_size, time, target_mean, target_sigma, label))
                current_volume += remaining_size
                remaining_size = 0
            else:
                if space_left > 0:
                    bar_data.append((price, space_left, time, target_mean, target_sigma, label))
                    current_volume += space_left
                    remaining_size -= space_left

                if current_volume >= volume_per_bar:
                    bars.append(compute_bar_stats(bar_data, bars))
                    current_volume = 0
                    bar_data = []

    if bar_data:
        bars.append(compute_bar_stats(bar_data, bars))

    return bars

def compute_bar_stats(bar_data, existing_bars):
    """Compute VWAP, stddev, and time duration for a bar."""
    prices = np.array([d[0] for d in bar_data])
    volumes = np.array([d[1] for d in bar_data])
    times = [d[2] for d in bar_data]

    total_volume = volumes.sum()
    vwap = np.sum(prices * volumes) / total_volume

    variance = np.sum(volumes * (prices - vwap) ** 2) / total_volume
    stddev = np.sqrt(variance)

    cumulative_volume = existing_bars[-1]['cumulative_volume'] + total_volume if existing_bars else total_volume

    time_duration = times[-1] - times[0]

    # Volume-weighted target mean and sigma
    target_means = np.array([d[3] for d in bar_data])
    target_sigmas = np.array([d[4] for d in bar_data])

    return {
        'cumulative_volume': cumulative_volume,
        'vwap': vwap,
        'stddev': stddev,
        'volume': total_volume,
        'start_time': times[0],
        'end_time': times[-1],
        'time_duration': time_duration,
        'target_mean': np.sum(target_means * volumes) / total_volume,
        'target_sigma': np.sum(target_sigmas * volumes) / total_volume,
        'num_trades': len(bar_data),
        'labels': bar_data[0][5] if bar_data else ''
    }

def plot_volume_bars(bars, output_html, input_csv):
    """
    Create equivolume chart for simulation data.
    Top panel: VWAP ±2σ candlesticks with target mean/sigma overlay
    Bottom panel: time duration per volume bar
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
    time_durations = [b['time_duration'] for b in bars]

    hover_text = [
        f"Volume: {b['cumulative_volume']:,.0f}<br>"
        f"VWAP: {b['vwap']:.4f}<br>"
        f"StdDev: {b['stddev']:.6f}<br>"
        f"+2σ: {b['vwap'] + 2 * b['stddev']:.4f}<br>"
        f"-2σ: {b['vwap'] - 2 * b['stddev']:.4f}<br>"
        f"Time: {b['start_time']/60:.2f}-{b['end_time']/60:.2f}m<br>"
        f"Duration: {b['time_duration']:.3f}s<br>"
        f"Trades: {b['num_trades']}<br>"
        f"Labels: {b['labels']}"
        for b in bars
    ]

    # Candlesticks
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

    # VWAP line
    fig.add_trace(go.Scatter(
        x=x_vals,
        y=vwap_vals,
        mode='lines',
        name='VWAP',
        line=dict(color='blue', width=1),
        hoverinfo='skip'
    ), row=1, col=1)

    # Target mean and sigma bands
    target_means = [b['target_mean'] for b in bars]
    target_upper = [b['target_mean'] + b['target_sigma'] for b in bars]
    target_lower = [b['target_mean'] - b['target_sigma'] for b in bars]

    fig.add_trace(go.Scatter(
        x=x_vals, y=target_means, mode='lines',
        line=dict(color='red', width=2),
        name='Target Mean', hoverinfo='skip'
    ), row=1, col=1)
    fig.add_trace(go.Scatter(
        x=x_vals, y=target_upper, mode='lines',
        line=dict(color='red', width=1, dash='dash'),
        name='Target +1σ', showlegend=False, hoverinfo='skip'
    ), row=1, col=1)
    fig.add_trace(go.Scatter(
        x=x_vals, y=target_lower, mode='lines',
        line=dict(color='red', width=1, dash='dash'),
        name='Target -1σ', showlegend=False, hoverinfo='skip'
    ), row=1, col=1)

    # Time duration area chart
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
        title=f'Equivolume Chart - {input_csv}',
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
    input_csv = sys.argv[1] if len(sys.argv) > 1 else 'data/test_hmm.csv'
    volume_per_bar = int(sys.argv[2]) if len(sys.argv) > 2 else 10000
    if len(sys.argv) > 3:
        output_html = sys.argv[3]
    else:
        basename = os.path.splitext(os.path.basename(input_csv))[0]
        output_dir = f'data/charts/sim/{basename}'
        os.makedirs(output_dir, exist_ok=True)
        output_html = f'{output_dir}/volume.html'

    print(f'Loading trades from {input_csv}...')
    trades = load_trades(input_csv)
    print(f'Loaded {len(trades)} trades')

    print(f'Creating volume bars with {volume_per_bar} volume per bar...')
    bars = create_volume_bars(trades, volume_per_bar)
    print(f'Created {len(bars)} volume bars')

    print(f'Plotting...')
    plot_volume_bars(bars, output_html, input_csv)
