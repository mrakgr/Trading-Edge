import numpy as np
import plotly.graph_objects as go
from plotly.subplots import make_subplots
from scipy import stats

np.random.seed(42)
median = 100.0
means = [110, 150, 200]
n = 100

fig = make_subplots(rows=3, cols=2, shared_xaxes=False,
    subplot_titles=[f'{dist} mean={m}' for m in means for dist in ['LogNormal', 'Pareto']],
    vertical_spacing=0.08)

for i, mean in enumerate(means):
    # LogNormal samples above median
    mu = np.log(median)
    sigma = np.sqrt(2 * np.log(mean / median))
    ln_samples = []
    while len(ln_samples) < n:
        s = stats.lognorm.rvs(s=sigma, scale=median)
        if s >= median:
            ln_samples.append(s)
    ln_samples = sorted(ln_samples)

    # Pareto samples (all >= median by definition)
    alpha = mean / (mean - median)
    pareto_samples = sorted((np.random.pareto(alpha, n) + 1) * median)

    row = i + 1
    fig.add_trace(go.Bar(x=list(range(n)), y=ln_samples, name=f'LN mean={mean}',
        marker_color='steelblue'), row=row, col=1)
    fig.add_trace(go.Bar(x=list(range(n)), y=pareto_samples, name=f'Pareto mean={mean}',
        marker_color='firebrick'), row=row, col=2)

    # Print stats
    print(f'mean={mean}:')
    print(f'  LogNormal  - min={min(ln_samples):.0f} median={np.median(ln_samples):.0f} mean={np.mean(ln_samples):.0f} max={max(ln_samples):.0f}')
    print(f'  Pareto     - min={min(pareto_samples):.0f} median={np.median(pareto_samples):.0f} mean={np.mean(pareto_samples):.0f} max={max(pareto_samples):.0f}')

fig.update_layout(height=900, width=1200, title='100 sorted samples: LogNormal vs Pareto (above median=100)',
    showlegend=False)
fig.write_html('data/pareto_vs_lognormal_samples.html')
print('\nWritten to data/pareto_vs_lognormal_samples.html')
