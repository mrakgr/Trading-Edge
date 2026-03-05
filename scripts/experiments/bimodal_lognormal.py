import json
import numpy as np
from tdigest import TDigest

# Load actual trade data
with open('data/trades/LW/2025-12-19.json') as f:
    trades = json.load(f)
actual_sizes = np.array([t['size'] for t in trades if t['size'] > 0])

# Target: median = 40.70, mean = 139.04
target_median = 40.70
target_mean = 139.04

# Both distributions have same median
mu = np.log(target_median)

# Choose means that average to target_mean (spread of 10)
mean1 = target_mean - 5
mean2 = target_mean + 5

# Calculate sigmas
sigma1 = np.sqrt(2 * np.log(mean1 / target_median))
sigma2 = np.sqrt(2 * np.log(mean2 / target_median))

print(f"Distribution 1: μ={mu:.4f}, σ={sigma1:.4f}, mean={mean1:.2f}")
print(f"Distribution 2: μ={mu:.4f}, σ={sigma2:.4f}, mean={mean2:.2f}")
print(f"Mixture mean: {0.5 * mean1 + 0.5 * mean2:.2f}")

# Sample from mixture
np.random.seed(42)
n_samples = len(actual_sizes)

def stochastic_round(x):
    floor = int(x)
    frac = x - floor
    return floor + 1 if np.random.random() < frac else floor

samples = []
for _ in range(n_samples):
    if np.random.random() < 0.5:
        s = np.random.lognormal(mu, sigma1)
    else:
        s = np.random.lognormal(mu, sigma2)

    rounded = stochastic_round(s)
    if rounded > 0:
        samples.append(rounded)

samples = np.array(samples)

# Compare statistics
actual_digest = TDigest(delta=0.00022, K=1024)
for s in actual_sizes:
    actual_digest.update(s)

mixture_digest = TDigest(delta=0.00022, K=1024)
for s in samples:
    mixture_digest.update(s)

print("\nActual vs Bimodal Log-Normal:")
print(f"Count: {len(actual_sizes)} vs {len(samples)}")
print(f"Mean: {np.mean(actual_sizes):.2f} vs {np.mean(samples):.2f}")
print(f"Median: {actual_digest.percentile(50):.2f} vs {mixture_digest.percentile(50):.2f}")
print(f"P25: {actual_digest.percentile(25):.2f} vs {mixture_digest.percentile(25):.2f}")
print(f"P75: {actual_digest.percentile(75):.2f} vs {mixture_digest.percentile(75):.2f}")
print(f"P95: {actual_digest.percentile(95):.2f} vs {mixture_digest.percentile(95):.2f}")
print(f"P99: {actual_digest.percentile(99):.2f} vs {mixture_digest.percentile(99):.2f}")
print(f"Min: {min(actual_sizes)} vs {min(samples)}")
print(f"Max: {max(actual_sizes)} vs {max(samples)}")

