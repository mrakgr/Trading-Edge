import csv
import sys
import os
from datetime import datetime
import numpy as np
import plotly.graph_objects as go
from plotly.subplots import make_subplots

def load_bars(csv_path):
    bars = []
    with open(csv_path) as f:
        for row in csv.DictReader(f):
            bars.append({
                'cumulative_volume': float(row['cumulative_volume']),
                'vwap': float(row['vwap']),
                'stddev': float(row['stddev']),
                'vwma': float(row['vwma']),
                'volume': float(row['volume']),
                'start_time': datetime.fromisoformat(row['start_time']),
                'end_time': datetime.fromisoformat(row['end_time']),
                'num_trades': int(row['num_trades']),
            })
    return bars

def load_fills(csv_path):
    fills = []
    with open(csv_path) as f:
        for row in csv.DictReader(f):
            fills.append({
                'timestamp': datetime.fromisoformat(row['timestamp']),
                'price': float(row['price']),
                'quantity': int(row['quantity']),
                'side': row['side'],
            })
    return fills

def load_decisions(csv_path):
    decisions = []
    with open(csv_path) as f:
        for row in csv.DictReader(f):
            decisions.append({
                'timestamp': datetime.fromisoformat(row['timestamp']),
                'price': float(row['price']),
                'shares': int(row['shares']),
            })
    return decisions

def find_cumulative_volume_at(bars, timestamp):
    """Find the cumulative volume at a given timestamp by interpolating between bars."""
    for b in bars:
        if b['end_time'] >= timestamp:
            return b['cumulative_volume']
    return bars[-1]['cumulative_volume'] if bars else 0

def plot_fill_chart(bars, fills, decisions, output_html, title):
    fig = make_subplots(
        rows=2, cols=1,
        shared_xaxes=True,
        vertical_spacing=0.05,
        row_heights=[0.7, 0.3],
        subplot_titles=['VWAP ±2σ with Fills', 'Time Duration (seconds)']
    )

    x_vals = [b['cumulative_volume'] for b in bars]
    vwap_vals = [b['vwap'] for b in bars]
    vwma_vals = [b['vwma'] for b in bars]
    upper_2sigma = [b['vwap'] + 2 * b['stddev'] for b in bars]
    lower_2sigma = [b['vwap'] - 2 * b['stddev'] for b in bars]

    # Time durations in seconds
    time_durations = [(b['end_time'] - b['start_time']).total_seconds() for b in bars]

    # Session VWAP for minimum height scaling
    total_volume = sum(b['volume'] for b in bars)
    session_vwap = sum(b['vwap'] * b['volume'] for b in bars) / total_volume if total_volume > 0 else 1.0
    min_height = min(0.01, 0.001 * session_vwap)

    bar_heights = []
    bar_bases = []
    for upper, lower, vwap in zip(upper_2sigma, lower_2sigma, vwap_vals):
        height = upper - lower
        actual_height = max(height, min_height)
        bar_heights.append(actual_height)
        bar_bases.append(vwap - actual_height / 2)

    colors = ['green' if upper >= lower else 'red'
              for upper, lower in zip(upper_2sigma, lower_2sigma)]

    customdata = [[
        b['cumulative_volume'],
        b['vwap'],
        b['stddev'],
        b['vwap'] + 2 * b['stddev'],
        b['vwap'] - 2 * b['stddev'],
        b['start_time'].strftime('%H:%M:%S.%f')[:-3],
        b['end_time'].strftime('%H:%M:%S.%f')[:-3],
        (b['end_time'] - b['start_time']).total_seconds(),
        b['num_trades'],
        b['vwma'],
    ] for b in bars]

    # Volume bars
    fig.add_trace(go.Bar(
        x=x_vals,
        y=bar_heights,
        base=bar_bases,
        name='VWAP ±2σ',
        marker_color=colors,
        marker_line_width=0,
        width=[b['volume'] * 0.8 for b in bars],
        customdata=customdata,
        hovertemplate='<b>Volume:</b> %{customdata[0]:,.0f}<br>' +
                      '<b>VWAP:</b> %{customdata[1]:.4f}<br>' +
                      '<b>StdDev:</b> %{customdata[2]:.6f}<br>' +
                      '<b>+2σ:</b> %{customdata[3]:.4f}<br>' +
                      '<b>-2σ:</b> %{customdata[4]:.4f}<br>' +
                      '<b>Start:</b> %{customdata[5]}<br>' +
                      '<b>End:</b> %{customdata[6]}<br>' +
                      '<b>Duration:</b> %{customdata[7]:.3f}s<br>' +
                      '<b>Trades:</b> %{customdata[8]}<br>' +
                      '<b>VWMA:</b> %{customdata[9]:.4f}<extra></extra>'
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

    # VWMA line (only where non-zero)
    vwma_x = [x for x, v in zip(x_vals, vwma_vals) if v > 0]
    vwma_y = [v for v in vwma_vals if v > 0]
    fig.add_trace(go.Scatter(
        x=vwma_x,
        y=vwma_y,
        mode='lines',
        name='VWMA',
        line=dict(color='orange', width=2),
        hoverinfo='skip'
    ), row=1, col=1)

    # Fill markers
    if fills:
        buy_fills = [f for f in fills if f['side'] == 'buy']
        sell_fills = [f for f in fills if f['side'] == 'sell']

        if buy_fills:
            buy_x = [find_cumulative_volume_at(bars, f['timestamp']) for f in buy_fills]
            buy_y = [f['price'] for f in buy_fills]
            buy_text = [f"BUY {f['quantity']}@{f['price']:.4f}<br>{f['timestamp'].strftime('%H:%M:%S.%f')[:-3]}"
                        for f in buy_fills]
            fig.add_trace(go.Scatter(
                x=buy_x, y=buy_y,
                mode='markers',
                name='Buy Fill',
                marker=dict(symbol='triangle-up', size=12, color='lime',
                            line=dict(width=1, color='darkgreen')),
                text=buy_text,
                hoverinfo='text'
            ), row=1, col=1)

        if sell_fills:
            sell_x = [find_cumulative_volume_at(bars, f['timestamp']) for f in sell_fills]
            sell_y = [f['price'] for f in sell_fills]
            sell_text = [f"SELL {f['quantity']}@{f['price']:.4f}<br>{f['timestamp'].strftime('%H:%M:%S.%f')[:-3]}"
                         for f in sell_fills]
            fig.add_trace(go.Scatter(
                x=sell_x, y=sell_y,
                mode='markers',
                name='Sell Fill',
                marker=dict(symbol='triangle-down', size=12, color='red',
                            line=dict(width=1, color='darkred')),
                text=sell_text,
                hoverinfo='text'
            ), row=1, col=1)

    # Decision markers (diamonds, smaller)
    if decisions:
        dec_x = [find_cumulative_volume_at(bars, d['timestamp']) for d in decisions]
        dec_y = [d['price'] for d in decisions]
        dec_text = [f"{'LONG' if d['shares'] > 0 else 'SHORT' if d['shares'] < 0 else 'FLAT'} {d['shares']}@{d['price']:.4f}<br>{d['timestamp'].strftime('%H:%M:%S.%f')[:-3]}"
                    for d in decisions]
        dec_colors = ['lime' if d['shares'] > 0 else 'red' if d['shares'] < 0 else 'gray' for d in decisions]
        fig.add_trace(go.Scatter(
            x=dec_x, y=dec_y,
            mode='markers',
            name='Decision',
            marker=dict(symbol='diamond', size=8, color=dec_colors,
                        line=dict(width=1, color='black')),
            text=dec_text,
            hoverinfo='text'
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
        title=title,
        height=800,
        width=1400,
        hovermode='closest',
        hoverdistance=100,
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

def process_dir(data_dir):
    """Generate chart for a single data directory if needed."""
    bars_path = os.path.join(data_dir, 'bars.csv')
    fills_path = os.path.join(data_dir, 'fills.csv')
    decisions_path = os.path.join(data_dir, 'decisions.csv')
    output_html = os.path.join(data_dir, 'fill_chart.html')

    if not all(os.path.exists(p) for p in [bars_path, fills_path, decisions_path]):
        return False

    # Skip if chart is newer than all source CSVs
    if os.path.exists(output_html):
        chart_mtime = os.path.getmtime(output_html)
        source_mtime = max(os.path.getmtime(p) for p in [bars_path, fills_path, decisions_path])
        if chart_mtime >= source_mtime:
            return False

    bars = load_bars(bars_path)
    fills = load_fills(fills_path)
    decisions = load_decisions(decisions_path)
    title = os.path.basename(data_dir).replace('_', ' ')

    print(f'{title}: {len(bars)} bars, {len(fills)} fills, {len(decisions)} decisions')
    plot_fill_chart(bars, fills, decisions, output_html, title)
    return True

if __name__ == '__main__':
    if len(sys.argv) < 2:
        # Scan all subdirectories under data/charts/fills/
        base_dir = 'data/charts/fills'
        if not os.path.isdir(base_dir):
            print(f"No directory found at {base_dir}")
            sys.exit(1)
        generated = 0
        skipped = 0
        for entry in sorted(os.listdir(base_dir)):
            data_dir = os.path.join(base_dir, entry)
            if os.path.isdir(data_dir):
                if process_dir(data_dir):
                    generated += 1
                else:
                    skipped += 1
        print(f'Done. Generated: {generated}, Skipped (up to date): {skipped}')
    else:
        data_dir = sys.argv[1]
        if not process_dir(data_dir):
            print(f'Chart already up to date for {data_dir}')
