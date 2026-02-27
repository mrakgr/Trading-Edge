import numpy as np
import plotly.graph_objects as go
from plotly.subplots import make_subplots
from scipy import stats, optimize

np.random.seed(42)
median = 100.0
target_means = [110, 150, 200]
n = 1000

# For each target mean, find LogNormal sigma such that E[X | X >= median] = target_mean
def conditional_mean_above_median(sigma, median):
    return stats.lognorm.expect(lambda x: x, args=(sigma,), scale=median, lb=median) / 0.5

fig = make_subplots(rows=3, cols=2, shared_xaxes=False,
    subplot_titles=[f'{dist} mean={m}' for m in target_means for dist in ['LogNormal (matched)', 'Pareto']],
    vertical_spacing=0.08)

for i, target_mean in enumerate(target_means):
    # Find sigma so conditional mean matches target
    res = optimize.brentq(lambda s: conditional_mean_above_median(s, median) - target_mean, 0.001, 5.0)
    sigma = res
    print(f'target_mean={target_mean}: matched LN sigma={sigma:.4f}')

    # LogNormal samples above median
    ln_samples = []
    while len(ln_samples) < n:
        s = stats.lognorm.rvs(s=sigma, scale=median)
        if s >= median:
            ln_samples.append(s)
    ln_samples = sorted(ln_samples)

    # Pareto samples
    alpha = target_mean / (target_mean - median)
    pareto_samples = sorted((np.random.pareto(alpha, n) + 1) * median)

    row = i + 1
    fig.add_trace(go.Scattergl(x=list(range(n)), y=ln_samples, mode='markers',
        marker=dict(size=2, color='steelblue'), name=f'LN mean={target_mean}'), row=row, col=1)
    fig.add_trace(go.Scattergl(x=list(range(n)), y=pareto_samples, mode='markers',
        marker=dict(size=2, color='firebrick'), name=f'Pareto mean={target_mean}'), row=row, col=2)

    print(f'  LogNormal  - min={min(ln_samples):.0f} median={np.median(ln_samples):.0f} mean={np.mean(ln_samples):.0f} max={max(ln_samples):.0f}')
    print(f'  Pareto     - min={min(pareto_samples):.0f} median={np.median(pareto_samples):.0f} mean={np.mean(pareto_samples):.0f} max={max(pareto_samples):.0f}')

fig.update_layout(height=900, width=1200,
    title='1000 sorted samples: LogNormal (conditional mean matched) vs Pareto',
    showlegend=False)
fig.write_html('data/pareto_vs_lognormal_matched.html')
print('\nWritten to data/pareto_vs_lognormal_matched.html')
