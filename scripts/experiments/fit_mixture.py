import json
import numpy as np
from tdigest import TDigest

# Load actual trade data
with open('data/trades/LW/2025-12-19.json') as f:
    trades = json.load(f)
actual_sizes = np.array([t['size'] for t in trades if t['size'] > 0])

# Split at 90th percentile instead
p90 = np.percentile(actual_sizes, 90)
print(f"P90 threshold: {p90:.2f}")

body_trades = actual_sizes[actual_sizes <= p90]
tail_trades = actual_sizes[actual_sizes > p90]

print(f"Body trades (≤P90): {len(body_trades)} ({len(body_trades)/len(actual_sizes)*100:.1f}%)")
print(f"Tail trades (>P90): {len(tail_trades)} ({len(tail_trades)/len(actual_sizes)*100:.1f}%)")

# Use overall log-normal (from mean and median)
overall_median = np.median(actual_sizes)
overall_mean = np.mean(actual_sizes)
mu_overall = np.log(overall_median)
sigma_overall = np.sqrt(2 * np.log(overall_mean / overall_median))
print(f"\nLog-normal (overall): μ={mu_overall:.4f}, σ={sigma_overall:.4f}")

# Fit Pareto to tail using method of moments
x_m = p90
tail_mean = np.mean(tail_trades)
alpha = tail_mean / (tail_mean - x_m)
print(f"Tail mean: {tail_mean:.2f}")
print(f"Pareto (tail): x_m={x_m:.2f}, α={alpha:.4f}")

# Sample from mixture model
np.random.seed(42)
n_samples = len(actual_sizes)
body_prob = len(body_trades) / len(actual_sizes)

def stochastic_round(x):
    floor = int(x)
    frac = x - floor
    return floor + 1 if np.random.random() < frac else floor

samples = []
for _ in range(n_samples):
    # Sample from overall log-normal
    s = np.random.lognormal(mu_overall, sigma_overall)

    # If in tail, replace with Pareto sample
    if s > p90:
        u = np.random.random()
        s = x_m * (1 - u) ** (-1/alpha)

    rounded = stochastic_round(s)
    if rounded > 0:
        samples.append(rounded)

samples = np.array(samples)

# Build t-digests and compare
actual_digest = TDigest(delta=0.00022, K=1024)
for s in actual_sizes:
    actual_digest.update(s)

mixture_digest = TDigest(delta=0.00022, K=1024)
for s in samples:
    mixture_digest.update(s)

print("\nActual vs Mixture Model:")
print(f"Count: {len(actual_sizes)} vs {len(samples)}")
print(f"Mean: {np.mean(actual_sizes):.2f} vs {np.mean(samples):.2f}")
print(f"Median: {actual_digest.percentile(50):.2f} vs {mixture_digest.percentile(50):.2f}")
print(f"P25: {actual_digest.percentile(25):.2f} vs {mixture_digest.percentile(25):.2f}")
print(f"P75: {actual_digest.percentile(75):.2f} vs {mixture_digest.percentile(75):.2f}")
print(f"P95: {actual_digest.percentile(95):.2f} vs {mixture_digest.percentile(95):.2f}")
print(f"P99: {actual_digest.percentile(99):.2f} vs {mixture_digest.percentile(99):.2f}")
print(f"Min: {min(actual_sizes)} vs {min(samples)}")
print(f"Max: {max(actual_sizes)} vs {max(samples)}")

