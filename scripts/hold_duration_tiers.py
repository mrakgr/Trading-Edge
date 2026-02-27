import numpy as np
from scipy import stats
import plotly.graph_objects as go
from plotly.subplots import make_subplots

np.random.seed(42)

tiers = [
    ('Short',  0.2, 0.3),
    ('Medium', 2.0, 3.0),
    ('Long',   20.0, 30.0),
]

print('Analytical quantiles (minutes):')
print(f'{"Tier":<8} {"Median":>7} {"Mean":>7} {"P10":>7} {"P90":>7} {"P90/P10":>8}')
print('-' * 48)

for name, median, mean in tiers:
    mu = np.log(median)
    sigma = np.sqrt(2 * np.log(mean / median))
    p10 = np.exp(mu + sigma * stats.norm.ppf(0.1))
    p90 = np.exp(mu + sigma * stats.norm.ppf(0.9))
    print(f'{name:<8} {median:>7.2f} {mean:>7.2f} {p10:>7.3f} {p90:>7.3f} {p90/p10:>8.1f}x')

print()
print('In seconds:')
print(f'{"Tier":<8} {"Median":>7} {"P10":>7} {"P90":>7}')
print('-' * 35)
for name, median, mean in tiers:
    mu = np.log(median)
    sigma = np.sqrt(2 * np.log(mean / median))
    p10 = np.exp(mu + sigma * stats.norm.ppf(0.1)) * 60
    p90 = np.exp(mu + sigma * stats.norm.ppf(0.9)) * 60
    print(f'{name:<8} {median*60:>7.1f} {p10:>7.1f} {p90:>7.1f}')
