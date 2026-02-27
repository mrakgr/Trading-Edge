import numpy as np
import plotly.graph_objects as go
from scipy import stats

x = np.linspace(100, 5000, 2000)
median = 100.0

fig = go.Figure()

for mean, color in [(110, 'blue'), (150, 'green'), (200, 'red')]:
    # Pareto: alpha = mean / (mean - median)
    alpha = mean / (mean - median)
    pareto_sf = (median / x) ** alpha

    # LogNormal (above median only): match median and mean
    # For LogNormal: median = exp(mu), mean = exp(mu + sigma^2/2)
    mu = np.log(median)
    sigma = np.sqrt(2 * np.log(mean / median))
    ln_sf = 1 - stats.lognorm.cdf(x, s=sigma, scale=median)
    # Normalize to survival at median (since we only care about above-median)
    ln_sf = ln_sf / (1 - stats.lognorm.cdf(median, s=sigma, scale=median))

    fig.add_trace(go.Scatter(x=x, y=pareto_sf, mode='lines',
        line=dict(color=color), name=f'Pareto mean={mean} (α={alpha:.1f})'))
    fig.add_trace(go.Scatter(x=x, y=ln_sf, mode='lines',
        line=dict(color=color, dash='dash'), name=f'LogNormal mean={mean}'))

fig.update_layout(
    title='Survival function P(X > x) for x > median=100: Pareto vs LogNormal',
    xaxis_title='x', yaxis_title='P(X > x)',
    yaxis_type='log', height=600, width=900)
fig.write_html('data/pareto_vs_lognormal.html')
print('Written to data/pareto_vs_lognormal.html')
