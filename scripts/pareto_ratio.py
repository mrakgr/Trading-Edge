import numpy as np
import plotly.graph_objects as go

alpha = np.linspace(1.01, 15, 500)
ratio = alpha / (alpha - 1)

fig = go.Figure()
fig.add_trace(go.Scatter(x=alpha, y=ratio, mode='lines', name='α/(α-1)'))
fig.add_hline(y=1, line_dash='dot', line_color='gray')

# Mark the specific points
for a, label in [(2.0, 'mean=200'), (3.0, 'mean=150'), (11.0, 'mean=110')]:
    fig.add_trace(go.Scatter(x=[a], y=[a/(a-1)], mode='markers+text',
        text=[f'α={a} ({label})'], textposition='top right',
        marker=dict(size=8), showlegend=False))

fig.update_layout(title='Pareto mean/x_m ratio: α/(α-1)',
    xaxis_title='α', yaxis_title='mean / x_m', height=500, width=800)
fig.write_html('data/pareto_ratio.html')
print('Written to data/pareto_ratio.html')
