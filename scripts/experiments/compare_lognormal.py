import json
import numpy as np
from tdigest import TDigest

# Load actual trade data
with open('data/trades/LW/2025-12-19.json') as f:
    trades = json.load(f)
actual_sizes = [t['size'] for t in trades if t['size'] > 0]

# Log-normal parameters from observed data
mu = 3.7062
sigma = 1.5675

# Generate samples
np.random.seed(42)
n_samples = len(actual_sizes)
samples = np.random.lognormal(mu, sigma, n_samples)

# Stochastic rounding
def stochastic_round(x):
    floor = int(x)
    frac = x - floor
    return floor + 1 if np.random.random() < frac else floor

rounded = np.array([stochastic_round(s) for s in samples])

# Resample zeros
while np.any(rounded == 0):
    zero_mask = rounded == 0
    n_zeros = np.sum(zero_mask)
    new_samples = np.random.lognormal(mu, sigma, n_zeros)
    rounded[zero_mask] = [stochastic_round(s) for s in new_samples]

# Build t-digests
actual_digest = TDigest(delta=0.00022, K=1024)
for s in actual_sizes:
    actual_digest.update(s)

sampled_digest = TDigest(delta=0.00022, K=1024)
for s in rounded:
    sampled_digest.update(s)

# Compare statistics
print("Actual vs Log-Normal Sampled:")
print(f"Count: {len(actual_sizes)} vs {len(rounded)}")
print(f"Mean: {np.mean(actual_sizes):.2f} vs {np.mean(rounded):.2f}")
print(f"Median: {actual_digest.percentile(50):.2f} vs {sampled_digest.percentile(50):.2f}")
print(f"P25: {actual_digest.percentile(25):.2f} vs {sampled_digest.percentile(25):.2f}")
print(f"P75: {actual_digest.percentile(75):.2f} vs {sampled_digest.percentile(75):.2f}")
print(f"P95: {actual_digest.percentile(95):.2f} vs {sampled_digest.percentile(95):.2f}")
print(f"P99: {actual_digest.percentile(99):.2f} vs {sampled_digest.percentile(99):.2f}")
print(f"Min: {min(actual_sizes)} vs {min(rounded)}")
print(f"Max: {max(actual_sizes)} vs {max(rounded)}")
