import pandas as pd
import plotly.graph_objects as go
from plotly.subplots import make_subplots

df = pd.read_csv('data/naive_vs_online.csv')
t = df['time'] / 60.0

fig = make_subplots(rows=4, cols=1, shared_xaxes=True,
    subplot_titles=['VWAP 1m', 'StdDev 1m', 'VWAP 5m', 'StdDev 5m'],
    vertical_spacing=0.05)

for col, row, label in [
    ('vwap_1m', 1, 'VWAP 1m'),
    ('stddev_1m', 2, 'StdDev 1m'),
    ('vwap_5m', 3, 'VWAP 5m'),
    ('stddev_5m', 4, 'StdDev 5m'),
]:
    fig.add_trace(go.Scatter(x=t, y=df[f'online_{col}'], mode='lines',
        name=f'Online {label}', line=dict(color='blue', width=1)), row=row, col=1)
    fig.add_trace(go.Scatter(x=t, y=df[f'naive_{col}'], mode='lines',
        name=f'Naive {label}', line=dict(color='red', width=1, dash='dot')), row=row, col=1)

fig.update_layout(title='Online vs Naive Aggregation', height=1200,
    xaxis4=dict(title='Time (minutes)'))
fig.write_html('data/naive_vs_online.html')
print("Saved to data/naive_vs_online.html")
