import csv
import sys
import os
import plotly.graph_objects as go
from plotly.subplots import make_subplots

input_csv = sys.argv[1] if len(sys.argv) > 1 else 'data/test_hmm.csv'
if len(sys.argv) > 2:
    output_html = sys.argv[2]
else:
    basename = os.path.splitext(os.path.basename(input_csv))[0]
    output_dir = f'data/charts/sim/{basename}'
    os.makedirs(output_dir, exist_ok=True)
    output_html = f'{output_dir}/tick.html'

trades = []
with open(input_csv) as f:
    r = csv.DictReader(f)
    for row in r:
        trades.append({
            'time': float(row['time']),
            'price': float(row['price']),
            'size': int(row['size']),
            'target_mean': float(row['target_mean']),
            'target_sigma': float(row['target_sigma']),
        })


# Plot all trades
times = [t['time'] / 60.0 for t in trades]
prices = [t['price'] for t in trades]

# Precompute marker sizes
import math
marker_sizes = [0.5 * math.sqrt(t['size']) for t in trades]

hover_text = [
    f"Time: {t['time']/60:.2f}m<br>"
    f"Price: ${t['price']:.4f}<br>"
    f"Size: {t['size']:,}"
    for t in trades
]

fig = make_subplots(rows=2, cols=1, shared_xaxes=True,
    row_heights=[3, 1], vertical_spacing=0.05,
    subplot_titles=['Price', 'Trade Size'])

# Price scatter
fig.add_trace(go.Scattergl(
    x=times,
    y=prices,
    mode='markers',
    marker=dict(size=marker_sizes, color='blue', opacity=0.6),
    text=hover_text,
    hoverinfo='text',
    name='Trades'
), row=1, col=1)

# Target mean and stddev bands
target_means = [t['target_mean'] for t in trades]
target_upper = [t['target_mean'] + t['target_sigma'] for t in trades]
target_lower = [t['target_mean'] - t['target_sigma'] for t in trades]

fig.add_trace(go.Scattergl(
    x=times, y=target_means, mode='lines',
    line=dict(color='orange', width=1, dash='dot'),
    name='Target Mean'
), row=1, col=1)
fig.add_trace(go.Scattergl(
    x=times, y=target_upper, mode='lines',
    line=dict(color='orange', width=0.5, dash='dot'),
    name='+1σ', showlegend=False
), row=1, col=1)
fig.add_trace(go.Scattergl(
    x=times, y=target_lower, mode='lines',
    line=dict(color='orange', width=0.5, dash='dot'),
    name='-1σ', showlegend=False
), row=1, col=1)

# Volume
sizes = [t['size'] for t in trades]
fig.add_trace(go.Scattergl(
    x=times, y=sizes, mode='markers',
    marker=dict(size=marker_sizes, color='blue', opacity=0.3),
    text=hover_text,
    hoverinfo='text',
    name='Size', showlegend=False
), row=2, col=1)

fig.update_layout(height=900, width=1400, title=f'Trade Data ({input_csv})',
    xaxis2=dict(title='Time (minutes)'))
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
print(f'Written to {output_html}')
