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
    seed = basename.split('_')[-1] if '_' in basename else 'default'
    output_html = f'data/charts/sim_{seed}_tick.html'

trades = []
with open(input_csv) as f:
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

trend_colors = {
    'StrongUp': 'darkgreen', 'MidUp': 'green', 'WeakUp': 'lightgreen',
    'Consol': 'gray',
    'WeakDown': 'lightsalmon', 'MidDown': 'orange', 'StrongDown': 'darkred'
}

# Assign colors to hold trends dynamically
hold_palette = ['red', 'magenta', 'salmon', 'deeppink', 'crimson', 'hotpink',
                'indianred', 'mediumvioletred', 'palevioletred', 'tomato']
hold_trends = sorted(set(t['trend'] for t in trades if t['trend'].startswith('Hold')))
for i, ht in enumerate(hold_trends):
    trend_colors[ht] = hold_palette[i % len(hold_palette)]

def is_hold(trend):
    return trend.startswith('Hold')

# Find hold segments for highlighting
hold_starts = []
hold_ends = []
in_hold = False
for t in trades:
    if is_hold(t['trend']) and not in_hold:
        hold_starts.append(t['time'] / 60.0)
        in_hold = True
    elif not is_hold(t['trend']) and in_hold:
        hold_ends.append(t['time'] / 60.0)
        in_hold = False
if in_hold:
    hold_ends.append(trades[-1]['time'] / 60.0)

# Plot all trades
times = [t['time'] / 60.0 for t in trades]
prices = [t['price'] for t in trades]
colors = [trend_colors.get(t['trend'], 'black') for t in trades]

fig = make_subplots(rows=2, cols=1, shared_xaxes=True,
    row_heights=[3, 1], vertical_spacing=0.05,
    subplot_titles=['Price (colored by trend)', 'Trade Size'])

# Precompute marker sizes
import math
marker_sizes = [0.5 * math.sqrt(t['size']) for t in trades]

# Price scatter colored by trend
for trend, color in trend_colors.items():
    idx = [i for i, t in enumerate(trades) if t['trend'] == trend]
    if idx:
        fig.add_trace(go.Scattergl(
            x=[times[i] for i in idx],
            y=[prices[i] for i in idx],
            mode='markers', marker=dict(size=[marker_sizes[i] for i in idx], color=color),
            name=trend
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

# Effective hold stddev band for hold regions (HoldSigmaFraction = 0.05)
# Insert None breaks between separate hold segments to avoid connecting lines
hold_upper_x, hold_upper_y = [], []
hold_lower_x, hold_lower_y = [], []
prev_was_hold = False
for t in trades:
    if is_hold(t['trend']):
        if not prev_was_hold:
            hold_upper_x.append(None); hold_upper_y.append(None)
            hold_lower_x.append(None); hold_lower_y.append(None)
        x = t['time'] / 60.0
        eff_sigma = t['target_sigma'] * 0.05
        hold_upper_x.append(x); hold_upper_y.append(t['target_mean'] + eff_sigma)
        hold_lower_x.append(x); hold_lower_y.append(t['target_mean'] - eff_sigma)
        prev_was_hold = True
    else:
        prev_was_hold = False

if hold_upper_x:
    fig.add_trace(go.Scattergl(
        x=hold_upper_x, y=hold_upper_y, mode='lines',
        line=dict(color='red', width=1, dash='dot'),
        name='+1σ hold', showlegend=False
    ), row=1, col=1)
    fig.add_trace(go.Scattergl(
        x=hold_lower_x, y=hold_lower_y, mode='lines',
        line=dict(color='red', width=1, dash='dot'),
        name='-1σ hold', showlegend=False
    ), row=1, col=1)

# Hold region shading
for s, e in zip(hold_starts, hold_ends):
    fig.add_vrect(x0=s, x1=e, fillcolor='red', opacity=0.08, line_width=0, row=1, col=1)

# Volume
sizes = [t['size'] for t in trades]
fig.add_trace(go.Scattergl(
    x=times, y=sizes, mode='markers',
    marker=dict(size=marker_sizes, color='blue', opacity=0.3),
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
