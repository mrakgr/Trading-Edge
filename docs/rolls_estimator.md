# Roll's estimator — measuring the effective spread without quote data

Methodology note for the FlushFader slippage study (S43q). Written because
we have raw **trades** (`/mnt/d/trading-edge-bulk/trades/`, one row per
print: price, size, `sip_timestamp`, `sequence_number`, `conditions`,
`trf_id`) but **no quote data at all** — no NBBO, so the bid-ask spread
cannot be observed directly and must be inferred from trade prices alone.

Reference: Roll, R. (1984), "A Simple Implicit Measure of the Effective
Bid-Ask Spread in an Efficient Market", *Journal of Finance* 39(4).

---

## 1. The model

Two assumptions. First, the true ("efficient") price is a driftless random
walk:

```
m_t = m_{t-1} + u_t                     u_t = news, iid, mean 0
```

Second — and this is the point — you never observe `m_t`. You observe a
**trade**, and a trade happens at the bid or at the ask:

```
p_t = m_t + (s/2) * q_t

        +1   buyer-initiated  (hits the ask)
q_t = {
        -1   seller-initiated (hits the bid)
```

where `s` is the spread and `q_t` is a coin flip, independent of the news
`u_t`.

## 2. The derivation

Difference the observed price:

```
Δp_t = p_t - p_{t-1} = u_t + (s/2)(q_t - q_{t-1})
```

Now take the **lag-1 autocovariance**. The `u` terms drop out entirely —
that is what "random walk" buys you, independence across `t`. Only the `q`
part survives:

```
cov(Δp_t, Δp_{t-1})
    = (s²/4) [ cov(q_t, q_{t-1}) - cov(q_t, q_{t-2})
               - Var(q_{t-1})    + cov(q_{t-1}, q_{t-2}) ]

    = (s²/4) [    0    -    0    -      1      +    0    ]

    = -s²/4
```

(using `Var(q) = E[q²] = 1` since `q = ±1`.)

Therefore:

```
s = 2 * sqrt( -cov(Δp_t, Δp_{t-1}) )
```

## 3. Why covariance, and why it must come out negative

The spread leaves a **mechanical footprint** that genuine price movement
does not have. A run of trades with *no news whatsoever* still looks like:

```
2.00, 2.01, 2.00, 2.00, 2.01, 2.01, 2.00, 2.01
```

because once you have printed at the ask, the only place left to go is back
down to the bid. Every up-tick is disproportionately followed by a
down-tick. That is systematic negative autocorrelation and it is pure
artifact — it encodes the *width* of the quote, not any change in value.

News, by contrast, is a random walk: it contributes **zero** autocorrelation
at every lag. So the negative autocovariance is a clean fingerprint of the
spread alone, and since its magnitude scales as `(s/2)²`, taking the square
root and doubling recovers `s`.

### ⭐ Adjacency is the entire signal

The bounce lives **only at lag 1**. The `cov(q_t, q_{t-2})` terms cancel in
the algebra above, so lag 2 and beyond carry nothing. This is why the
estimator reads strictly *consecutive* trades — and why trade ORDERING is
not a detail but the whole measurement.

⚠ **The bug this caught (2026-08-04).** The first version of
`spread.py` ordered trades by `(sec, price)` — the 1-second bar bucket, with
price as a tie-break. The 1s bars are only ~50ms-aligned to the exchange
clock and carry 20-130 trades per second, so sorting by price *within* each
second turns the real tape:

```
2.00, 2.01, 2.00, 2.01     (alternating -> strong negative cov)
```

into:

```
2.00, 2.00, 2.01, 2.01     (monotone runs -> cov near zero or positive)
```

This would have destroyed the bounce and biased the estimate toward zero
(or made it undefined). **Fix: order by `sip_timestamp` (nanoseconds).**
Verified empirically that `sip_timestamp` is unique per trade — AGFY
2024-01-18 (266,760 trades), AAPL and TSLA 2024-01-03 (303,556 and 566,025
trades) all show zero ties, max tie = 1 — so a `sequence_number` tie-break
is inert. Harmless, but it buys nothing.

## 4. ⚠ Why the estimator is fragile on THIS book specifically

Both model assumptions fail during a capitulation flush, which is exactly
where we are measuring.

| assumption | what a flush does to it | effect on the estimate |
|---|---|---|
| `m_t` is driftless | −6%/min drift | mostly handled: `covar_pop` de-means both series, removing a constant drift component |
| `q_t` serially independent | everyone is selling, so `q_t = -1` repeatedly | **`cov(q_t, q_{t-1}) > 0`**, which pushes the total covariance toward zero |

The second is the real problem. One-sided order flow adds a *positive* term
to a quantity that is supposed to be `-s²/4`, so the estimator is **biased
downward** — and when the covariance goes positive outright there is no real
square root and the estimate is simply **undefined**. Order-splitting (one
parent order sliced into many same-side child orders) does the same thing.
A microcap flush gives you both at once.

There is also a milder issue: Roll measures the *effective* spread actually
realized by trades, which can be tighter than the quoted spread thanks to
midpoint executions and price improvement. Filtering to lit prints
(`trf_id = 0`) mitigates this, since dark prints execute at or inside the
midpoint and would bias the measurement downward.

## 5. What we compute instead of one number

Because of §4, `spread.py` reports three quantities per (trip, window), not
one:

| field | definition | role |
|---|---|---|
| `roll` | `2*sqrt(-cov(Δp, Δp_lag))`, **NULL when cov ≥ 0** | the estimator; explicitly undefined rather than silently faked |
| `step` | median `|Δp|` over non-zero changes | drift-robust cross-check. For a 1-tick name with real bounce this **is** the spread |
| `p_rev` | fraction of consecutive non-zero `Δp` pairs with opposite sign | **the bounce-strength check** — see the calibration below |

`p_flat` (fraction of consecutive trades at the identical price) and `n`
(trade count) are recorded alongside for context.

### ⚠ Calibrating `p_rev` — 1.0 is the null, not 0.5

An earlier draft of this note said "~0.5 means the coin-flip assumption
roughly holds". **That is wrong, and backwards.** Work the model through
with no news (`u = 0`): prices live on exactly two levels, so `Δp ∈ {0, +s,
-s}`. If `Δp_t = +s` then `q_t = +1`, and the only way to get `Δp_{t+1} ≠ 0`
is `q_{t+1} = -1`, i.e. `Δp_{t+1} = -s`. **Consecutive non-zero changes
alternate deterministically: `p_rev = 1.0` under pure bounce.**

So the scale is:

| `p_rev` | meaning |
|---|---|
| → 1.0 | pure bid-ask bounce, no news — Roll's ideal case |
| → 0.5 | no bounce at all, pure random walk — Roll has nothing to measure |

News and drift pull the observed value down from 1.0. **Measured on this
book: median 0.77, rising to 0.875 in the $1.00-1.50 bucket and falling to
0.606 for $10+.** Bounce is therefore strong and strongest exactly where
the name is tick-constrained — which is what makes the *cheap* end of the
book the most Roll-friendly, the opposite of the earlier expectation.

### What actually happened (2026-08-04)

`roll` was defined on **1,790 of 1,791** entry windows — the feared outright
breakdown from one-sided flow did not occur. But the **downward bias did**,
and `p_flat` shows the mechanism: **83% of consecutive trades print at the
identical price**, which is exactly the serial correlation in `q` that
§4 warns about (a large order being worked at one level, print after print).
The result is `roll` ≈ 0.19-0.62 ticks on sub-$10 names — implausibly tight
for a $1.24 stock.

**Read the two estimators as a bracket, not as competitors:**

```
roll  <=  true effective spread  <=  step
```

`roll` is biased low by order-splitting; `step` is biased high by news
(any real price move inflates a non-zero `|Δp|` beyond the spread).

---

## 6. Windows and filters actually used

- **Windows:** `entry_sec ± 30s` and `exit_sec ± 30s`, per trip.
- **Trades:** lit prints only (`trf_id = 0`), `price > 0`, `size > 0`.
- **Clock:** `sip_timestamp` → `America/New_York`, seconds since ET
  midnight, matching the 1s-bar convention (RTH open = 34200). Parity
  verified against `data/intraday_1s_slim/` — reconstructed 1s vwaps match
  the stored bars to 4 decimals on every bucket tested.
- **Book:** the mc=3 traded book, 1,791 trips over 506 dates (3,582
  windows).
