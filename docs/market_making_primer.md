# Market making — a primer and the simulator plan (2026-11-17)

**Why (user, 2026-11-17).** Reviewing the 527 opportunity triggers (`mm_opportunity_results.md`) by hand in TapeSim is too
much labour. Instead, build a real market-making system, starting from the academic models, and test it on the cheap
data first: trades plus NBBO. For CBRS there is also Databento MBO (full order-by-order book); more of it can be bought
later if it is cheap. **Assumption for now: borrows are free**, so the maker quotes both sides from flat.

## 1. What a maker earns

Every fill is a trade against someone who crossed the spread. Per share:

```
maker P&L = spread captured − adverse selection − inventory P&L − fees + rebates
```

- **Spread captured.** The distance from the fill price to the mid at the fill (the effective half-spread).
- **Adverse selection.** How far the mid moves against the maker after the fill. Informed or fast traders hit the quote
  just before the price moves. The 11-03 realized-spread study (`realized_spread_results.md`) measured this on hot IPOs:
  it ate most of the wide spread for a maker filled pro rata.
- **Inventory P&L.** Between fills the maker holds a position and the price moves. This has no drift for a maker
  without an edge, but it is the risk the models price.
- **Fees.** Lightspeed charges 0.2c/share commission, and ARCA pays an add credit of about +0.20c/share. At \$5 that
  nets out; on a 50 bp spread at \$5 (2.5c) the spread is ten times the fees.

## 2. The academic line

| paper | the idea it adds |
|---|---|
| Ho & Stoll (1981) | a dealer with inventory shifts both quotes to unload it; the spread pays for inventory risk |
| Glosten & Milgrom (1985) | some traders are informed; the spread exists to pay for losses to them (adverse selection) |
| Avellaneda & Stoikov (2008) | a solvable model: the inventory-skewed reservation price and the optimal spread |
| Guéant, Lehalle & Fernandez-Tapia (2013) | the same model with a hard inventory bound; closed-form approximate quotes; calibration |
| Cartea, Jaimungal & Penalva (2015, book) | short-term price signals ("alpha") and adverse selection put into the quoting |
| Stoikov (2018), the micro-price | a fair value from the order-book imbalance, better than the mid |

### 2.1 Avellaneda–Stoikov (A-S)

**The assumptions:**
- The mid `s` is a Brownian motion with volatility `σ`, with no drift.
- The maker posts a bid at `s − δᵇ` and an ask at `s + δᵃ`.
- Market orders reach a quote at distance `δ` at the Poisson rate `λ(δ) = A·e^(−kδ)`. Quoting further out fills
  exponentially less often.
- The maker is risk-averse with coefficient `γ` and trades until a horizon `T`.

**The answer:**

```
reservation price    r = s − q·γ·σ²·(T − t)              q = inventory (shares, or lots)
total spread         δᵃ + δᵇ = γ·σ²·(T − t) + (2/γ)·ln(1 + γ/k)
quotes               bid = r − spread/2,   ask = r + spread/2
```

- **Long inventory** pushes `r` below the mid. Both quotes move down: the maker sells more readily and buys less.
- **The spread has two parts.** The `ln` term is the monopoly markup: how far out it pays to quote, given how fast
  fills fall off (`k`). The `γσ²τ` term is the inventory-risk premium.
- **What it leaves out** is the part that matters most on our tape: the price has no drift and order flow carries no
  information. Every fill is assumed to be noise. A-S makers lose in trending tape: they buy every bid on the way
  down and sell every ask on the way up.

### 2.2 Guéant–Lehalle–Fernandez-Tapia (GLFT)

This is the same model with `|q| ≤ Q` enforced and a long horizon. Its closed-form approximation, with `q` in lots:

```
c  = sqrt( σ²·γ / (2·k·A) · (1 + γ/k)^(1 + k/γ) )
δᵇ(q) ≈ (1/γ)·ln(1 + γ/k) + (2q + 1)/2 · c
δᵃ(q) ≈ (1/γ)·ln(1 + γ/k) − (2q − 1)/2 · c
```

- At `q = Q` the bid is pulled, and at `q = −Q` the ask.
- It needs no horizon `T`, so it suits a market that may go on for the whole session.
- It needs `A` as well as `k`. Both can be estimated from the tape (§4).

### 2.3 Cartea–Jaimungal: putting adverse selection in

- The mid gets a short-term drift `α`, a predictable component such as order-flow imbalance, a burst of buys, or the
  micro-price above the mid.
- Each market order also moves the mid: impact.
- Each quote shifts by roughly `α` times the expected holding time.
  - When the price is expected to rise, the ask moves up, or is pulled, and the bid joins tighter.
  - The maker stops selling to the buyers who know something and buys alongside them.
- This is where the edge lives, if there is one. The quoting formula only turns a forecast into prices.

### 2.4 The micro-price

```
I = bid_size / (bid_size + ask_size)
micro ≈ mid + spread·(I − ½)        (the simplest weighted mid; Stoikov 2018 refines it)
```

A heavy bid means the next mid move is more likely up. ⚠ In the SIP NBBO, the size is the size at the best venue, not
the whole price level. It is a weaker signal here than in a full-depth book (MBO).

## 3. Where our tape breaks the models

| model assumption | the hot small-cap tape |
|---|---|
| Brownian mid, no drift | momentum bursts, flushes, LULD halts that strand inventory for 5 minutes |
| continuous prices | a one-cent tick; at \$2 a tick is 50 bp, so `δ*` snaps to the grid |
| a quote fills at rate λ(δ) | price-time priority: behind the queue, a print at your price does not fill you |
| instant re-quotes | about 130 ms from our order to the venue (live, `accidental_crossing.md`); a quote moving faster than that is picked off |
| one book | 16 lit venues plus dark pools (TRF); the NBBO size is one venue's |

The simulator has to model the queue, the latency and the halts. Otherwise it measures the formula, not the maker.

## 4. Calibration from the tape

- **σ:** the bounce-free volatility of the scan, `√((var ΔP + 2·cov)·n/60)` per second, or a short EWMA of mid
  returns.
- **A and k:** for each lit trade, its distance `δ` from the mid of the prevailing NBBO. Count the trades per second
  at or beyond each `δ`, then fit `ln λ(δ) = ln A − k·δ`. This is the standard calibration (Guéant 2016, *The Financial
  Mathematics of Market Liquidity*).
- **γ:** not estimated, chosen. Pick it so the skew at the inventory cap is about one spread, then sweep it.
- **Q and the size:** small, at our scale. For example 100 shares a quote, Q = 3–5 lots.

## 5. The simulator plan

**Data:**
- The 527 tier-B triggers (`triggers_B3.csv`).
- Each window runs from 15 minutes before its 3-minute detection window to 35 minutes after the trigger.
- The NBBO comes from Massive (`scripts/equity/mm_quotes_fetch.py`, cache `data/equity/mm_sim/quotes/`). The trades come
  from the bulk tape.

**The engine:** event-driven on the merged quote and trade stream.
- **Orders** go live after a latency (0.13–0.2 s). A cancel also takes the latency, so a quote can fill during it.
- **Queue fills:**
  - A joined price level puts us behind the NBBO size displayed when we arrive.
  - Lit prints at our price eat that queue first, then fill us.
  - This is the fill model that agreed with live 5–0 on fill-or-no-fill calls (`flushfader_results.md` §S49ap-3).
- **Improving quotes:** a quote that improves the NBBO is first in line.
- **Fills through:** a print through our price fills us in full.
- **Quotes never cross.** A desired bid at or above the ask is capped one tick under the ask.
- **Fees:** 0.2c/share commission, +0.20c add credit; no credit under \$1.
- **Flat at the end:** at the end of the trading window the inventory is closed at the touch, taking liquidity.
- **Halts** (LULD) freeze the book; the inventory rides through.

**The P&L decomposition, per window:** spread captured, adverse selection at 1 s / 5 s / 30 s, inventory P&L, fees,
and fills per side.

**The ladder of rules, simplest first:**
1. **Join the touch** on both sides, with the inventory cap. Inventory is skewed only by pulling the side at the cap.
   This is the baseline every model must beat.
2. **A-S / GLFT** with `σ, A, k` estimated from the warmup, with γ swept.
3. **The same plus an alpha skew:** order-flow imbalance over the last N seconds, the micro-price, the trade-direction
   run. Cartea–Jaimungal style.

**The controls:**
- The same rules on the same tickers at **non-trigger** times, to check that the trigger matters.
- No year split is possible: all 128 days are in 2026.
- Look at results by day and by name, since a few names (OMH, VEEE, …) dominate the counts.
