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
                'trend': row['trend'],
                'target_mean': float(row['target_mean']),
                'target_sigma': float(row['target_sigma']),
            })
    return trades

def create_volume_bars(trades, volume_per_bar):
    """
    Group trades into fixed volume chunks with trade splitting.
    Computes VWAP with stddev per bar.
    """
    bars = []
    current_volume = 0
    bar_data = []  # List of (price, volume, time, trend, target_mean, target_sigma) tuples

    for trade in trades:
        price = trade['price']
        size = trade['size']
        time = trade['time']
        trend = trade['trend']
        target_mean = trade['target_mean']
        target_sigma = trade['target_sigma']

        remaining_size = size

        while remaining_size > 0:
            space_left = volume_per_bar - current_volume

            if remaining_size <= space_left:
                bar_data.append((price, remaining_size, time, trend, target_mean, target_sigma))
                current_volume += remaining_size
                remaining_size = 0
            else:
                if space_left > 0:
                    bar_data.append((price, space_left, time, trend, target_mean, target_sigma))
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
    """Compute VWAP, stddev, time duration, and dominant trend for a bar."""
    prices = np.array([p for p, v, t, tr, tm, ts in bar_data])
    volumes = np.array([v for p, v, t, tr, tm, ts in bar_data])
    times = [t for p, v, t, tr, tm, ts in bar_data]
    trends = [tr for p, v, t, tr, tm, ts in bar_data]

    total_volume = volumes.sum()
    vwap = np.sum(prices * volumes) / total_volume

    variance = np.sum(volumes * (prices - vwap) ** 2) / total_volume
    stddev = np.sqrt(variance)

    cumulative_volume = existing_bars[-1]['cumulative_volume'] + total_volume if existing_bars else total_volume

    time_duration = times[-1] - times[0]

    # Dominant trend (volume-weighted)
    trend_vol = {}
    for (_, v, _, tr, _, _) in bar_data:
        trend_vol[tr] = trend_vol.get(tr, 0) + v
    dominant_trend = max(trend_vol, key=trend_vol.get)

    # Volume-weighted target mean and sigma
    target_means = np.array([tm for p, v, t, tr, tm, ts in bar_data])
    target_sigmas = np.array([ts for p, v, t, tr, tm, ts in bar_data])
    avg_target_mean = np.sum(target_means * volumes) / total_volume
    avg_target_sigma = np.sum(target_sigmas * volumes) / total_volume

    return {
        'cumulative_volume': cumulative_volume,
        'vwap': vwap,
        'stddev': stddev,
        'volume': total_volume,
        'start_time': times[0],
        'end_time': times[-1],
        'time_duration': time_duration,
        'dominant_trend': dominant_trend,
        'target_mean': avg_target_mean,
        'target_sigma': avg_target_sigma,
        'is_hold': dominant_trend.startswith('Hold'),
        'num_trades': len(bar_data)
    }

trend_colors = {
    'StrongUp': 'darkgreen', 'MidUp': 'green', 'WeakUp': 'lightgreen',
    'Consol': 'black',
    'WeakDown': 'lightsalmon', 'MidDown': 'orange', 'StrongDown': 'darkred'
}

hold_palette = ['red', 'magenta', 'salmon', 'deeppink', 'crimson', 'hotpink',
                'indianred', 'mediumvioletred', 'palevioletred', 'tomato']

def get_trend_color(trend, hold_color_map):
    if trend in trend_colors:
        return trend_colors[trend]
    return hold_color_map.get(trend, 'magenta')

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

    # Assign colors to hold trends
    hold_trends = sorted(set(b['dominant_trend'] for b in bars if b['dominant_trend'].startswith('Hold')))
    hold_color_map = {}
    for i, ht in enumerate(hold_trends):
        hold_color_map[ht] = hold_palette[i % len(hold_palette)]

    hover_text = [
        f"Volume: {b['cumulative_volume']:,.0f}<br>"
        f"VWAP: {b['vwap']:.4f}<br>"
        f"StdDev: {b['stddev']:.6f}<br>"
        f"+2σ: {b['vwap'] + 2 * b['stddev']:.4f}<br>"
        f"-2σ: {b['vwap'] - 2 * b['stddev']:.4f}<br>"
        f"Time: {b['start_time']/60:.2f}-{b['end_time']/60:.2f}m<br>"
        f"Duration: {b['time_duration']:.3f}s<br>"
        f"Trend: {b['dominant_trend']}<br>"
        f"Trades: {b['num_trades']}"
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

    # Target mean line
    target_means = [b['target_mean'] for b in bars]
    target_upper = [b['target_mean'] + b['target_sigma'] for b in bars]
    target_lower = [b['target_mean'] - b['target_sigma'] for b in bars]

    fig.add_trace(go.Scatter(
        x=x_vals, y=target_means, mode='lines',
        line=dict(color='orange', width=1, dash='dot'),
        name='Target Mean'
    ), row=1, col=1)
    fig.add_trace(go.Scatter(
        x=x_vals, y=target_upper, mode='lines',
        line=dict(color='orange', width=0.5, dash='dot'),
        name='+1σ target', showlegend=False
    ), row=1, col=1)
    fig.add_trace(go.Scatter(
        x=x_vals, y=target_lower, mode='lines',
        line=dict(color='orange', width=0.5, dash='dot'),
        name='-1σ target', showlegend=False
    ), row=1, col=1)

    # Hold region shading
    in_hold = False
    hold_start = 0
    for b in bars:
        if b['is_hold'] and not in_hold:
            hold_start = b['cumulative_volume'] - b['volume']
            in_hold = True
        elif not b['is_hold'] and in_hold:
            fig.add_vrect(x0=hold_start, x1=b['cumulative_volume'] - b['volume'],
                         fillcolor='red', opacity=0.08, line_width=0, row=1, col=1)
            in_hold = False
    if in_hold:
        fig.add_vrect(x0=hold_start, x1=bars[-1]['cumulative_volume'],
                     fillcolor='red', opacity=0.08, line_width=0, row=1, col=1)

    # Time duration bars colored by trend
    bar_colors = [get_trend_color(b['dominant_trend'], hold_color_map) for b in bars]
    fig.add_trace(go.Bar(
        x=x_vals,
        y=time_durations,
        name='Time Duration',
        marker_color=bar_colors,
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
