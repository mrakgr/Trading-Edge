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
            'label': row['label'],
            'trend_target': float(row['trend_target']),
            'trend_sigma': float(row['trend_variance']) ** 0.5,
            'session_target': float(row['session_target']),
            'session_sigma': float(row['session_variance']) ** 0.5,
            'day_target': float(row['day_target']),
            'day_sigma': float(row['day_variance']) ** 0.5,
        })


# Plot all trades
times = [t['time'] / 60.0 for t in trades]
prices = [t['price'] for t in trades]

# Extract session labels and assign colors
session_colors = {'Morning': 'red', 'Mid': 'blue', 'Close': 'green'}
colors = []
for t in trades:
    label_parts = t['label'].split('|')
    session = label_parts[1] if len(label_parts) > 1 else 'Unknown'
    colors.append(session_colors.get(session, 'gray'))

# Precompute marker sizes
import math
marker_sizes = [0.5 * math.sqrt(t['size']) for t in trades]

hover_text = [
    f"Time: {t['time']/60:.2f}m<br>"
    f"Price: ${t['price']:.4f}<br>"
    f"Size: {t['size']:,}<br>"
    f"Label: {t['label']}"
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

# Target mean and stddev bands for all three levels
# Day level (outermost)
day_targets = [t['day_target'] for t in trades]
day_upper = [t['day_target'] + t['day_sigma'] for t in trades]
day_lower = [t['day_target'] - t['day_sigma'] for t in trades]

fig.add_trace(go.Scattergl(
    x=times, y=day_targets, mode='lines',
    line=dict(color='purple', width=1, dash='dash'),
    name='Day Target'
), row=1, col=1)
fig.add_trace(go.Scattergl(
    x=times, y=day_upper, mode='lines',
    line=dict(color='purple', width=0.5, dash='dash'),
    name='Day +1σ', showlegend=False
), row=1, col=1)
fig.add_trace(go.Scattergl(
    x=times, y=day_lower, mode='lines',
    line=dict(color='purple', width=0.5, dash='dash'),
    name='Day -1σ', showlegend=False
), row=1, col=1)

# Session level (middle)
session_targets = [t['session_target'] for t in trades]
session_upper = [t['session_target'] + t['session_sigma'] for t in trades]
session_lower = [t['session_target'] - t['session_sigma'] for t in trades]

fig.add_trace(go.Scattergl(
    x=times, y=session_targets, mode='lines',
    line=dict(color='orange', width=1, dash='dot'),
    name='Session Target'
), row=1, col=1)
fig.add_trace(go.Scattergl(
    x=times, y=session_upper, mode='lines',
    line=dict(color='orange', width=0.5, dash='dot'),
    name='Session +1σ', showlegend=False
), row=1, col=1)
fig.add_trace(go.Scattergl(
    x=times, y=session_lower, mode='lines',
    line=dict(color='orange', width=0.5, dash='dot'),
    name='Session -1σ', showlegend=False
), row=1, col=1)

# Trend level (innermost)
trend_targets = [t['trend_target'] for t in trades]
trend_upper = [t['trend_target'] + t['trend_sigma'] for t in trades]
trend_lower = [t['trend_target'] - t['trend_sigma'] for t in trades]

fig.add_trace(go.Scattergl(
    x=times, y=trend_targets, mode='lines',
    line=dict(color='cyan', width=1),
    name='Trend Target'
), row=1, col=1)
fig.add_trace(go.Scattergl(
    x=times, y=trend_upper, mode='lines',
    line=dict(color='cyan', width=0.5),
    name='Trend +1σ', showlegend=False
), row=1, col=1)
fig.add_trace(go.Scattergl(
    x=times, y=trend_lower, mode='lines',
    line=dict(color='cyan', width=0.5),
    name='Trend -1σ', showlegend=False
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
