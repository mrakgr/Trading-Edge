import numpy as np
from scipy import stats

median = 2.0
mean = 3.0
mu = np.log(median)
sigma = np.sqrt(2 * np.log(mean / median))

p10 = np.exp(mu + sigma * stats.norm.ppf(0.1))
p90 = np.exp(mu + sigma * stats.norm.ppf(0.9))

print(f'LogNormal(median={median}, mean={mean}):')
print(f'  mu={mu:.4f}, sigma={sigma:.4f}')
print(f'  P10 = {p10:.3f} min')
print(f'  P50 = {median:.3f} min (median)')
print(f'  P90 = {p90:.3f} min')
print(f'  P90/P10 ratio = {p90/p10:.1f}x')
print()

# Sample 10k and show empirical quantiles
np.random.seed(42)
samples = stats.lognorm.rvs(s=sigma, scale=median, size=10000)
print(f'Empirical (10k samples):')
for q in [0.01, 0.05, 0.10, 0.25, 0.50, 0.75, 0.90, 0.95, 0.99]:
    print(f'  P{int(q*100):02d} = {np.quantile(samples, q):.3f} min')
