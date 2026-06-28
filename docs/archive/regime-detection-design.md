# Regime Detection via Factorial HMM — Design Notes

## Goal

Detect market regimes to filter the breakout system. Two orthogonal regime dimensions:

- **Trend regime**: {up, down, ranging}
- **Volume regime**: {low, normal, high (RVOL > 3)}

Deploy the breakout system preferentially when P(high volume ∩ uptrend) is elevated, since backtested edge concentrates there (PF 1.4 baseline; hypothesis: regime filtering lifts this).

## Why HMM, not NN

- Generative model is hand-specifiable: we know what RVOL > 3 looks like, what a trending tape looks like.
- Inference is exact dynamic programming (forward-backward). O(T·K²). Microseconds per day per symbol.
- **Model changes = re-run inference, no retraining.** This is the core workflow advantage over the NN-label-generation-training loop.
- Bayesian framing: prior knowledge goes into parameters directly; posterior over regimes falls out of inference.

## Architecture: Factorial HMM (not single joint-state HMM)

Two parallel chains (trend, volume) that couple through the emission, rather than one chain over 9 joint states.

**Why factorial over joint:**

1. **Parameter economy**: 3×3 + 3×3 = 18 transition params vs 9×9 = 81.
2. **Interpretability**: can independently query P(uptrend) and P(high volume); filter trades on conjunction.
3. **Independent dynamics**: volume regimes persist on different timescales than trend regimes. Forcing shared transition matrix couples them artificially.

**Cost**: exact inference intractable (chains couple via observation). Use structured variational inference (Ghahramani & Jordan 1997) or Gibbs sampling. Still fast, still no NN training.

## Observation model — where the edge lives

### Volume chain

Do **not** emit raw volume. Emit:

```
log(volume_t / trailing_20d_median_volume_at_this_minute_of_day)
```

- Bakes in intraday seasonality (the U-shape).
- Cross-sectionally normalized.
- RVOL > 3 becomes a natural threshold in this space.
- Start with Gaussian emission per state; upgrade to Student-t for fat tails if needed.

### Trend chain

Separate directional drift from noise. Options, in increasing order of information content:

1. Log returns only, state-conditional mean and variance (classic Hamilton regime-switching).
2. Log returns + VWAP deviation.
3. Log returns + signed order flow imbalance + VWAP deviation. **This is where the order flow pipeline work pays off — textbook implementations don't have this feature available.**

### Cross-chain coupling

Trend emission variance should depend on volume state. High-volume regimes have different return distributions than low-volume regimes. This is exactly what factorial HMM emission coupling handles.

## Parameter estimation

**Phase 1 (start here): hand-specified parameters.**

- Set emission means/variances from domain knowledge. Example: high-volume state has mean(log_rvol) ≈ log(3), low-volume state has mean(log_rvol) ≈ log(0.5).
- Transition probabilities from expected dwell time. 1-second bars, expected 30-min regime ⇒ self-transition ≈ 1 − 1/1800.
- Validate before any learning: pull a day with an obvious volume breakout, confirm P(high volume) spikes where it should.

**Phase 2 (later): variational EM with priors.**

- Put priors on parameters (this is where the Bayesian background helps — keeps EM from degenerate solutions).
- MAP or full posterior inference, initialized from Phase 1 values.
- Local updates only — do not let it wander.

## Gotchas

**Label switching.** HMM states identified only up to permutation. If learning, constrain μ_up > 0 > μ_down on trend chain. Initialize from hand-specified values.

**Numerical stability.** Forward-backward in probability space underflows on long sequences. Work in log-space (log-sum-exp) or use scaled forward-backward. Non-negotiable for production.

**Geometric dwell-time assumption.** HMM self-transitions ⇒ geometric regime durations. Often wrong — regimes don't decay memorylessly. If duration matters empirically, upgrade to **hidden semi-Markov model** (explicit duration distribution per state). More complex inference but not prohibitive.

**Online vs batch inference.**
- Live trading: **filtered** estimate P(z_t | x_{1:t}) — forward pass only.
- Backtest analysis: **smoothed** P(z_t | x_{1:T}) — full forward-backward.
- **Never train/tune on smoothed and deploy with filtered.** The lookahead will flatter the backtest. Run the backtest filtered-only for honest numbers.

## Epistemic check

The HMM is not alpha. The breakout system is alpha; the HMM is an EV filter.

Failure mode: overfitting regime definitions to the backtest until P(high volume, uptrend) perfectly covers the winners. Mitigation:

- Build from priors about market structure, not from "what labels this set of winners."
- Validate regime labels visually and against known events (earnings gaps, news catalysts, lunch doldrums, halt resumptions) **before** looking at trading performance.
- Only then test whether regime-filtering moves PF on out-of-sample data.

## Path forward

1. **3-state HMM on volume only.** Hand-specified params. Validate labels visually on historical sessions.
2. **3-state HMM on trend only.** Same approach.
3. **Factorial HMM** combining both with coupled emission. Variational inference.
4. **Filter breakout system** on P(high volume) > τ (and later, joint with uptrend). Measure PF change on held-out data.
5. (Optional) Upgrade to HSMM if dwell-time diagnostics show geometric assumption is wrong.
6. (Optional) Variational EM with priors once hand-specified model is validated.

## Implementation notes (F#)

- No mature F# HMM library. Forward-backward is ~200 lines; factorial variational inference adds maybe 300 more. DP structure fits F# well.
- Use log-space throughout. `MathNet.Numerics` for log-sum-exp, matrix ops.
- Tick/bar data already in DuckDB/Parquet pipeline; emit features to a columnar format, load into F# arrays for inference.
- Inference is embarrassingly parallel across symbols — run per-symbol-per-day in parallel.

## References to pull

- Ghahramani & Jordan 1997, "Factorial Hidden Markov Models" — the canonical factorial HMM paper with variational inference derivation.
- Hamilton 1989, "A New Approach to the Economic Analysis of Nonstationary Time Series and the Business Cycle" — original regime-switching model, good for trend chain intuition.
- Murphy, *Machine Learning: A Probabilistic Perspective*, Ch. 17 (HMMs) and Ch. 18 (state-space models) — clean treatment of forward-backward as message passing.
- Rabiner 1989 tutorial — still the best intro to HMM algorithms, though notation is dated.
- Yu 2010, "Hidden Semi-Markov Models" — if duration distributions matter.

## Open questions / decisions deferred

- Bar frequency for inference: 1-second, 1-minute, or adaptive? Second bars are O(23k) per session — trivial. Minute bars may be enough signal and are less noisy.
- Emission distribution: Gaussian vs Student-t. Start Gaussian, revisit if tail diagnostics show poor fit.
- Cross-symbol parameter sharing: learn one model for all low-float momentum names, or per-symbol? Pooled probably better given data sparsity per symbol.
- Whether to add a third chain for *volatility regime* separately from trend+volume. Currently baked into trend emission variance; may want to factor out.