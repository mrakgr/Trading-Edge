import json
import numpy as np
from tdigest import TDigest

# Load actual trade data
with open('data/trades/LW/2025-12-19.json') as f:
    trades = json.load(f)
actual_sizes = np.array([t['size'] for t in trades if t['size'] > 0])

target_median = np.median(actual_sizes)
target_mean = np.mean(actual_sizes)

# Single log-normal (from mean and median)
mu_single = np.log(target_median)
sigma_single = np.sqrt(2 * np.log(target_mean / target_median))

print(f"Target: median={target_median:.2f}, mean={target_mean:.2f}")
print(f"\nSingle log-normal: μ={mu_single:.4f}, σ={sigma_single:.4f}")

# Bimodal 80/20 split
prob_small = 0.8
prob_large = 0.2
mean_small = 50
mean_large = (target_mean - prob_small * mean_small) / prob_large

mu_80_20 = mu_single
sigma_small = np.sqrt(2 * np.log(mean_small / target_median))
sigma_large = np.sqrt(2 * np.log(mean_large / target_median))

print(f"\nBimodal (80/20 split):")
print(f"  Small (80%): μ={mu_80_20:.4f}, σ={sigma_small:.4f}, mean={mean_small:.2f}")
print(f"  Large (20%): μ={mu_80_20:.4f}, σ={sigma_large:.4f}, mean={mean_large:.2f}")
print(f"  Weighted mean: {prob_small * mean_small + prob_large * mean_large:.2f}")

# Sample from both models
np.random.seed(42)
n_samples = len(actual_sizes)

def stochastic_round(x):
    floor = int(x)
    frac = x - floor
    return floor + 1 if np.random.random() < frac else floor

# Single log-normal samples
single_samples = []
for _ in range(n_samples):
    s = np.random.lognormal(mu_single, sigma_single)
    rounded = stochastic_round(s)
    if rounded > 0:
        single_samples.append(rounded)

# Bimodal samples (80/20)
bimodal_samples = []
for _ in range(n_samples):
    if np.random.random() < prob_small:
        s = np.random.lognormal(mu_80_20, sigma_small)
    else:
        s = np.random.lognormal(mu_80_20, sigma_large)
    rounded = stochastic_round(s)
    if rounded > 0:
        bimodal_samples.append(rounded)

single_samples = np.array(single_samples)
bimodal_samples = np.array(bimodal_samples)

# Build t-digests
actual_digest = TDigest(delta=0.00022, K=1024)
for s in actual_sizes:
    actual_digest.update(s)

single_digest = TDigest(delta=0.00022, K=1024)
for s in single_samples:
    single_digest.update(s)

bimodal_digest = TDigest(delta=0.00022, K=1024)
for s in bimodal_samples:
    bimodal_digest.update(s)

# Compare
print("\n" + "="*70)
print(f"{'Metric':<10} {'Actual':>12} {'Single':>12} {'Bimodal':>12}")
print("="*70)
print(f"{'Count':<10} {len(actual_sizes):>12} {len(single_samples):>12} {len(bimodal_samples):>12}")
print(f"{'Mean':<10} {np.mean(actual_sizes):>12.2f} {np.mean(single_samples):>12.2f} {np.mean(bimodal_samples):>12.2f}")
print(f"{'Median':<10} {actual_digest.percentile(50):>12.2f} {single_digest.percentile(50):>12.2f} {bimodal_digest.percentile(50):>12.2f}")
print(f"{'P25':<10} {actual_digest.percentile(25):>12.2f} {single_digest.percentile(25):>12.2f} {bimodal_digest.percentile(25):>12.2f}")
print(f"{'P75':<10} {actual_digest.percentile(75):>12.2f} {single_digest.percentile(75):>12.2f} {bimodal_digest.percentile(75):>12.2f}")
print(f"{'P95':<10} {actual_digest.percentile(95):>12.2f} {single_digest.percentile(95):>12.2f} {bimodal_digest.percentile(95):>12.2f}")
print(f"{'P99':<10} {actual_digest.percentile(99):>12.2f} {single_digest.percentile(99):>12.2f} {bimodal_digest.percentile(99):>12.2f}")
print(f"{'Min':<10} {min(actual_sizes):>12} {min(single_samples):>12} {min(bimodal_samples):>12}")
print(f"{'Max':<10} {max(actual_sizes):>12} {max(single_samples):>12} {max(bimodal_samples):>12}")
print("="*70)

