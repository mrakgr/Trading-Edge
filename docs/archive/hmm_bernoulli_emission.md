# HMM Emission for Tick-Level Signed Flow

This document describes the emission model we're switching to for tick-level
HMM inference, why the previous Gaussian emission was wrong, and what the
new one computes.

## The problem with Gaussian emission on (Δlog p, v)

The model we built first was

```
Δlog p_k | state_s, v_k  ~  Normal( μ_s · v_k,  σ² · v_k )
```

This is the volume-clock intuition: variance proportional to traded volume,
drift proportional to traded volume. It's correct at aggregated timescales
(minute bars and above) — see Clark 1973, Ané & Geman 2000.

At **tick level** it fails for two reasons the data made clear on
LW 2025-12-19:

**1. Variance is dominated by bid-ask bounce, not information flow.**
Small prints hop between bid and ask — a 100-share trade at the ask followed
by a 50-share trade at the bid produces a Δlog p that is **large** despite
both being noise. Large block prints execute at the midpoint and produce
Δlog p ≈ 0 regardless of the block's information content. On panel (d) of
the likelihood explorer we saw the empirical |Δlp|/√v is *higher* at small v,
the opposite of what σ²·v predicts.

**2. A large trade's Δlog p is not its directional signal.**
A block cross at the midpoint carries zero Δlog p but is still a meaningful
directional event if it is buyer-aggressive. The *sign* of aggression is the
signal, not the price change induced by the print.

Both issues compound: the zero-Δlp trades (60% of LW's tape) crushed the
directional states because σ²·v → 0 makes Consol's Gaussian a narrow spike
at zero, and any Δlp = 0 trade votes overwhelmingly for Consol.

## What replaces it

Binance's public trade data carries the `isBuyerMaker` flag — a
ground-truth side classification, not a Lee-Ready reconstruction. Every
trade is either buyer-aggressive or seller-aggressive. (There is no "at-mid"
category; continuous markets always have an aggressor.)

We observe, per trade:

- `sign_k ∈ { +1, -1 }` — aggressor direction. +1 = buyer lifted the offer,
  −1 = seller hit the bid.
- `v_k` — trade size, a non-negative real number. Input, not a random
  variable we model.

The emission assigns a state-conditional probability of the observed sign,
weighted by volume so that a 10k-share aggression carries more evidence
than a 10-share one.

### The simplest form

For a single trade with sign `s_k ∈ {+1, -1}` and volume `v_k`:

```
P(sign_k = +1 | state, v_k) = σ( λ · d_state · v_k )
```

where

- `σ(x) = 1 / (1 + e^{-x})` is the logistic
- `d_state ∈ {+1, 0, -1}` is the state's "directional propensity":
  - `d_Up = +1`
  - `d_Consol = 0`
  - `d_Down = -1`
- `λ > 0` is a single global parameter — the strength of the directional
  preference per unit of traded volume.

In the Consol state, `d = 0` so `P(+1) = 0.5` — the state is agnostic about
direction and gains no evidence from any trade. That's the right behavior:
"I don't know" is a valid regime.

For Up and Down, the probability of a buy-aggressive trade is

```
P(+1 | Up,   v) = σ( +λ · v)
P(+1 | Down, v) = σ( -λ · v)
```

At large `v`, these saturate to 1 (Up) and 0 (Down) respectively. At `v → 0`
(or `λ → 0`), they collapse to 0.5 — a small trade carries little evidence,
which matches intuition.

### Log-likelihood

The forward-backward algorithm needs `log P(observation | state)` per
trade per state. For our emission:

```
log P(sign = +1 | Up, v)   = log σ( +λ·v) = -log(1 + e^{-λ·v})
log P(sign = -1 | Up, v)   = log σ( -λ·v) = -log(1 + e^{+λ·v})
log P(sign = +1 | Down, v) = log σ( -λ·v) = -log(1 + e^{+λ·v})
log P(sign = -1 | Down, v) = log σ( +λ·v) = -log(1 + e^{-λ·v})
log P(sign = ±1 | Consol)  = log 0.5 = -log 2
```

In code, the softplus function `softplus(x) = log(1 + e^x)` is numerically
stable for all x (implementations handle both large-positive and
large-negative arguments). `MathNet.Numerics.SpecialFunctions.LogBinomial`
is not what we need; we want the straight softplus.

### Log-likelihood ratios — the thing that shapes the posterior

Consider Up vs Consol:

```
log P(sign | Up, v) - log P(sign | Consol, v)
    = log σ( sign · λ · v) - log 0.5
    = log 2 - softplus( -sign · λ · v)
```

As `v → ∞`, this tends to `+log 2` when the sign matches Up and to `-∞`
(through softplus going to `+λ·v`) when the sign is against Up. So:

- A large buy-aggressive trade: log-LR Up vs Consol ≈ +log 2 ≈ +0.69.
  Bounded, small.
- A large sell-aggressive trade: log-LR Up vs Consol ≈ -λ·v. Unbounded,
  large-negative.

The asymmetry is intentional. Agreement with a state's direction earns a
*bounded* reward because sign is binary and `log σ(·)` is bounded at log 1 = 0
above log 2 at its minimum. Disagreement with a state's direction earns an
*unbounded* penalty that scales with volume. This matches the right
epistemology: a single buy print is consistent with Up, Consol, or anything
else; a single large sell print is strong evidence *against* Up.

The log-LR Down vs Consol is the mirror image.

The log-LR Up vs Down is especially clean:

```
log P(sign | Up, v) - log P(sign | Down, v)
    = log σ( +sign·λ·v) - log σ( -sign·λ·v)
    = sign · λ · v
```

**Exactly `sign · λ · v`.** A purely linear vote, proportional to signed
volume, with `λ` as the per-unit-volume weight.

This is a big simplification. Up vs Down is the pairwise decision the
posterior actually cares about along a trend — signed-volume-weighted
accumulation, nothing more.

### Choosing λ — physical meaning

`λ` is "log-odds per unit of signed volume in the aggressor's favor."

If we want, say, 1 BTC of aggressive volume to produce a 2:1 odds ratio
(log 2 ≈ 0.69) between Up and Down:

```
λ · 1.0 = log 2   →   λ ≈ 0.69 per BTC
```

If we want a 10:1 odds ratio from 1 BTC: `λ ≈ 2.3 per BTC`.

For Binance BTCUSDT where average trade size is ~0.007 BTC, a typical
tick produces `λ · 0.007 = 0.005` of log-evidence at λ ≈ 0.7 — essentially
nothing. That's *correct*: one tiny trade is nothing. What matters is the
accumulation over many ticks, which forward-backward does automatically.

For a hypothetical sequence of 1000 buy-aggressive trades of 0.007 BTC
each: cumulative log-LR = 1000 · 0.69 · 0.007 ≈ 4.8. `e^4.8 ≈ 120:1` odds
favoring Up. A clearly-directional minute of tape produces strong posterior
commitment. Calm, mixed-sign tape produces near-zero accumulated log-LR,
so posterior stays near whatever the transition prior pulls it toward
(initial / smoothed from neighbors).

A principled way to pick `λ` for BTCUSDT:

1. Pick a time window of known trend — e.g. a minute of the 2026-02-05
   selloff where price falls and flow is clearly seller-aggressive.
2. Compute cumulative signed volume in that window.
3. Set `λ` such that the window's cumulative log-LR is a confident number
   like 3 (≈ 20:1 odds). That gives you "how much signed volume is
   convincing" in the model's units.

This is hand-calibration, like before. But now it's a *single* parameter
with a clear physical meaning, not three entangled ones.

## What this model ignores (on purpose)

- **Δlog p**. The price change between trades is not used as evidence. This
  is deliberate — at tick level, Δlog p is dominated by microstructure noise
  and by the trade sign itself (a buy-aggressive trade tends to lift to the
  offer; the Δlog p it produces is redundant with the sign we already
  observe).
- **Inter-trade time (Δt)**. Still enters the *transition* via `exp(Q·Δt)`.
  Clock time still advances during quiet periods, so the transition prior
  pulls the posterior toward the stationary distribution during long gaps.
  But Δt does not enter the emission.
- **Trade prices themselves.** The model doesn't look at absolute prices
  or price *levels*. It's about the direction of aggression, period.
  Absorption (aggression without price movement) is visible *through* the
  log-LR building up strongly in one direction while the tape doesn't move
  — a derivative observation that downstream code can flag, not something
  the emission models directly.

## Implementation sketch

Replace `Emission.fs`:

```fsharp
module TradingEdge.Hmm.Emission

/// Per-state directional propensity on the signed-flow emission.
/// All three states share the global scale λ.
type StateParams = {
    D: float     // +1.0 for Up, 0.0 for Consol, -1.0 for Down
}

/// Numerically stable softplus.
let private softplus x =
    if x > 0.0 then x + log (1.0 + exp (-x))
    else log (1.0 + exp x)

/// log P(sign | state, volume). sign is ±1.
let logEmission (p: StateParams) (lambda: float) (volume: float) (sign: float) =
    let x = sign * p.D * lambda * volume
    -softplus (-x)       // = log σ(x)
```

`LwModel.infer` needs minor changes to supply `sign` and `volume` to the
emission instead of `dlogp` and `volume`. `buildSequence` gets a new
`Sign: float[]` field; the sign for each trade comes straight from
`isBuyerMaker` (Binance) or is unused for LW until we add quote data.

The forward-backward code in `ForwardBackward.fs` does not change. The
Ctmc transition cache does not change. Only the emission.

## Why this is the right resolution

The Bernoulli-on-sign model separates **what the trade tells us** from
**how confident we are in it**:

- **What**: sign — direction of aggression. Binary, clean, no ambiguity.
- **How confident**: volume — a larger trade is harder to forge or fake
  and represents more committed capital behind the direction.

This is exactly the decomposition we couldn't get from the Gaussian model,
which tried to fold information content, volume scaling, and bid-ask
noise into a single variance parameter. Those three things have different
generative stories and need separate handling; the Bernoulli-on-sign
emission just drops the ones that don't belong in a tick-level model.

The price we pay:

- We commit to a binary view of trade sign. Ambiguous fills (block trades
  at mid on equity exchanges, hidden orders, iceberg trades) have to be
  either reported with a sign or excluded. For Binance data this is a
  non-issue; for equities it's the signed-flow reconstruction problem
  that we're deferring.
- We lose the ability to learn per-trade price-move magnitudes from the
  model. That's fine for regime inference but would need adding back for
  a full price-forecasting model.

## Next steps

- Port `LwModel.fs` → `BtcModel.fs` with Binance-schema trade ingestion.
- Calibrate `λ` on a known-trending window of 2026-02-05.
- Run forward-backward over the full day.
- Generate the volume-bar chart with posterior overlay (like we have for
  LW, but now the emission is not fighting us).
- Inspect whether the posterior correctly identifies the sustained
  down-trend segments AND the "absorption holds" the user observed — the
  second of these is the interesting research question.
