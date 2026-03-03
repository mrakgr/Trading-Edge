import csv
import sys
import numpy as np
import plotly.graph_objects as go
from plotly.subplots import make_subplots

def load_trades(csv_path):
    """Load trades from CSV."""
    trades = []
    with open(csv_path) as f:
        r = csv.DictReader(f)
        for row in r:
            trades.append({
                'time': float(row['time']),
                'price': float(row['price']),
                'size': int(row['size']),
                'trend': row['trend'],
            })
    return trades

def create_volume_bars(trades, volume_per_bar):
    """
    Group trades into fixed volume chunks and compute OHLC.

    Returns list of bars with:
    - cumulative_volume: x-axis position
    - open, high, low, close: OHLC prices
    - start_time, end_time: time range
    - dominant_trend: most common trend in the bar
    """
    bars = []
    current_volume = 0
    bar_trades = []

    for trade in trades:
        bar_trades.append(trade)
        current_volume += trade['size']

        if current_volume >= volume_per_bar:
            # Compute OHLC
            prices = [t['price'] for t in bar_trades]
            trends = [t['trend'] for t in bar_trades]

            # Find dominant trend
            trend_counts = {}
            for t in trends:
                trend_counts[t] = trend_counts.get(t, 0) + 1
            dominant_trend = max(trend_counts, key=trend_counts.get)

            bar = {
                'cumulative_volume': bars[-1]['cumulative_volume'] + current_volume if bars else current_volume,
                'open': bar_trades[0]['price'],
                'high': max(prices),
                'low': min(prices),
                'close': bar_trades[-1]['price'],
                'volume': current_volume,
                'start_time': bar_trades[0]['time'],
                'end_time': bar_trades[-1]['time'],
                'dominant_trend': dominant_trend,
                'num_trades': len(bar_trades)
            }
            bars.append(bar)

            # Reset for next bar
            current_volume = 0
            bar_trades = []

    # Handle remaining trades
    if bar_trades:
        prices = [t['price'] for t in bar_trades]
        trends = [t['trend'] for t in bar_trades]
        trend_counts = {}
        for t in trends:
            trend_counts[t] = trend_counts.get(t, 0) + 1
        dominant_trend = max(trend_counts, key=trend_counts.get)

        bar = {
            'cumulative_volume': bars[-1]['cumulative_volume'] + current_volume if bars else current_volume,
            'open': bar_trades[0]['price'],
            'high': max(prices),
            'low': min(prices),
            'close': bar_trades[-1]['price'],
            'volume': current_volume,
            'start_time': bar_trades[0]['time'],
            'end_time': bar_trades[-1]['time'],
            'dominant_trend': dominant_trend,
            'num_trades': len(bar_trades)
        }
        bars.append(bar)

    return bars

def get_trend_color(trend):
    """Map trend to color."""
    trend_colors = {
        'StrongUp': '#00AA00', 'MidUp': '#66CC66', 'WeakUp': '#99DD99',
        'Consol': '#888888',
        'WeakDown': '#DD9999', 'MidDown': '#CC6666', 'StrongDown': '#AA0000'
    }
    # Handle hold trends
    if trend.startswith('Hold'):
        return '#FF00FF'  # Magenta for holds
    return trend_colors.get(trend, '#888888')

def find_key_levels(bars, min_touches=3, tolerance=0.001):
    """
    Find key price levels where price repeatedly touches.

    Args:
        bars: list of volume bars
        min_touches: minimum number of touches to consider a level significant
        tolerance: price tolerance as fraction (0.001 = 0.1%)

    Returns:
        list of (price, touch_count) tuples
    """
    all_prices = []
    for bar in bars:
        all_prices.extend([bar['high'], bar['low']])

    if not all_prices:
        return []

    all_prices.sort()

    # Cluster nearby prices
    levels = []
    current_cluster = [all_prices[0]]

    for price in all_prices[1:]:
        if price <= current_cluster[-1] * (1 + tolerance):
            current_cluster.append(price)
        else:
            if len(current_cluster) >= min_touches:
                avg_price = sum(current_cluster) / len(current_cluster)
                levels.append((avg_price, len(current_cluster)))
            current_cluster = [price]

    # Handle last cluster
    if len(current_cluster) >= min_touches:
        avg_price = sum(current_cluster) / len(current_cluster)
        levels.append((avg_price, len(current_cluster)))

    return sorted(levels, key=lambda x: x[1], reverse=True)

def plot_volume_bars(bars, output_html, show_key_levels=True):
    """
    Create fixed volume bar chart.
    X-axis: cumulative volume
    Y-axis: price
    """
    fig = go.Figure()

    # Group bars by trend for legend
    trend_groups = {}
    for bar in bars:
        trend = bar['dominant_trend']
        if trend not in trend_groups:
            trend_groups[trend] = []
        trend_groups[trend].append(bar)

    # Plot each trend group
    for trend, trend_bars in trend_groups.items():
        color = get_trend_color(trend)

        x_vals = [b['cumulative_volume'] for b in trend_bars]
        open_vals = [b['open'] for b in trend_bars]
        high_vals = [b['high'] for b in trend_bars]
        low_vals = [b['low'] for b in trend_bars]
        close_vals = [b['close'] for b in trend_bars]

        hover_text = [
            f"Volume: {b['cumulative_volume']:,.0f}<br>"
            f"O: {b['open']:.2f} H: {b['high']:.2f}<br>"
            f"L: {b['low']:.2f} C: {b['close']:.2f}<br>"
            f"Time: {b['start_time']/60:.1f}-{b['end_time']/60:.1f}m<br>"
            f"Trades: {b['num_trades']}"
            for b in trend_bars
        ]

        # Use OHLC trace
        fig.add_trace(go.Ohlc(
            x=x_vals,
            open=open_vals,
            high=high_vals,
            low=low_vals,
            close=close_vals,
            name=trend,
            increasing_line_color=color,
            decreasing_line_color=color,
            hovertext=hover_text,
            hoverinfo='text'
        ))

    # Add key price levels
    if show_key_levels and bars:
        key_levels = find_key_levels(bars, min_touches=5, tolerance=0.002)
        max_vol = bars[-1]['cumulative_volume']

        for i, (price, touches) in enumerate(key_levels[:10]):  # Top 10 levels
            opacity = min(0.8, 0.3 + (touches / 100))
            fig.add_shape(
                type='line',
                x0=0, x1=max_vol,
                y0=price, y1=price,
                line=dict(color='rgba(255, 165, 0, {})'.format(opacity), width=1, dash='dash'),
            )
            # Add annotation for strongest levels
            if i < 3:
                fig.add_annotation(
                    x=max_vol * 0.95,
                    y=price,
                    text=f'{price:.2f} ({touches})',
                    showarrow=False,
                    xanchor='right',
                    bgcolor='rgba(255, 255, 255, 0.7)',
                    font=dict(size=10)
                )

    fig.update_layout(
        title='Fixed Volume Bars (Volume on X-axis, Price on Y-axis)',
        xaxis_title='Cumulative Volume',
        yaxis_title='Price',
        height=800,
        width=1400,
        hovermode='x unified',
        xaxis_rangeslider_visible=False
    )

    fig.write_html(output_html)
    print(f'Saved to {output_html}')

if __name__ == '__main__':
    input_csv = sys.argv[1] if len(sys.argv) > 1 else 'data/test_hmm.csv'
    volume_per_bar = int(sys.argv[2]) if len(sys.argv) > 2 else 10000
    output_html = sys.argv[3] if len(sys.argv) > 3 else 'data/volume_bars.html'

    print(f'Loading trades from {input_csv}...')
    trades = load_trades(input_csv)
    print(f'Loaded {len(trades)} trades')

    print(f'Creating volume bars with {volume_per_bar} volume per bar...')
    bars = create_volume_bars(trades, volume_per_bar)
    print(f'Created {len(bars)} volume bars')

    print(f'Plotting...')
    plot_volume_bars(bars, output_html)

