import numpy as np
import plotly.graph_objects as go
from plotly.subplots import make_subplots
from scipy import stats, optimize

np.random.seed(42)
median = 100.0
target_means = [110, 150, 200]

# For exponential shifted to start at median:
# E[X | X >= median] = median + 1/lambda
# So lambda = 1 / (target_mean - median)

fig = make_subplots(rows=2, cols=1, subplot_titles=[
    'Survival function P(X > x) above median=100',
    '1000 sorted samples above median=100'
], vertical_spacing=0.12)

x = np.linspace(100, 3000, 2000)
colors = ['blue', 'green', 'red']

for mean, color in zip(target_means, colors):
    # Truncated LogNormal: find sigma so conditional mean above median = target_mean
    def cond_mean_ln(sigma):
        return stats.lognorm.expect(lambda x: x, args=(sigma,), scale=median, lb=median) / 0.5
    sigma = optimize.brentq(lambda s: cond_mean_ln(s) - mean, 0.001, 5.0)

    ln_sf = (1 - stats.lognorm.cdf(x, s=sigma, scale=median)) / 0.5

    # Shifted exponential: X = median + Exp(lambda), lambda = 1/(mean - median)
    lam = 1.0 / (mean - median)
    exp_sf = np.exp(-lam * (x - median))

    fig.add_trace(go.Scatter(x=x, y=ln_sf, mode='lines',
        line=dict(color=color, dash='dash'), name=f'LogNormal mean={mean}'), row=1, col=1)
    fig.add_trace(go.Scatter(x=x, y=exp_sf, mode='lines',
        line=dict(color=color), name=f'Exponential mean={mean}'), row=1, col=1)

    # Samples
    n = 1000
    ln_samples = []
    while len(ln_samples) < n:
        s = stats.lognorm.rvs(s=sigma, scale=median)
        if s >= median:
            ln_samples.append(s)
    ln_samples = sorted(ln_samples)

    exp_samples = sorted(median + np.random.exponential(1.0/lam, n))

    fig.add_trace(go.Scattergl(x=list(range(n)), y=ln_samples, mode='markers',
        marker=dict(size=2, color=color, opacity=0.5), name=f'LN samples mean={mean}',
        legendgroup=f'ln{mean}'), row=2, col=1)
    fig.add_trace(go.Scattergl(x=list(range(n)), y=exp_samples, mode='markers',
        marker=dict(size=2, color=color, symbol='x', opacity=0.5), name=f'Exp samples mean={mean}',
        legendgroup=f'exp{mean}'), row=2, col=1)

    print(f'mean={mean}:')
    print(f'  LogNormal    - median={np.median(ln_samples):.0f} mean={np.mean(ln_samples):.0f} max={max(ln_samples):.0f}')
    print(f'  Exponential  - median={np.median(exp_samples):.0f} mean={np.mean(exp_samples):.0f} max={max(exp_samples):.0f}')

fig.update_yaxes(type='log', row=1, col=1)
fig.update_layout(height=800, width=1000, title='Truncated LogNormal vs Shifted Exponential (above median=100)')
fig.write_html('data/lognormal_vs_exponential.html')
print('\nWritten to data/lognormal_vs_exponential.html')
