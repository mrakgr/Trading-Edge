# ORB Session — Findings Report

**Date:** 2026-04-16
**Branch:** `opening_range_breakouts`
**Dataset:** 3,409 RVOL≥3 breakout days (`data/breakouts_rvol3plus.json`)

## Preface

This session started as a follow-up to the VWAP continuation / trend-following system, which had collapsed from a decision-level PF of ~1.9 to a fill-sim PF of ~0.85 — a failure attributed to 4-parameter `volPcts` overfitting via lookahead that wasn't realistically available. The plan was to try an opening-range breakout (ORB) as an elimination test before moving to generative approaches.

The session ended up being productive well beyond that scope. The rest of this document is the chronological trail of what we tried and what we learned.

## System design

The ORB system shares the plumbing of the VWAP project:

- `SegregateTrades` classifies each trade into `EarlyPremarket` / `LatePremarket` (8:30 ET → op+60s) / `AfterOpeningPrint` / `BeforeClosing`.
- `VolumeBarBuilder` feeds a bar every `barSize` shares of volume; `barSize` = total RTH volume / 3000 (uses lookahead).
- `OrbSystemArgsBuilder` exposes a `VwmaAccumulator` (rolling-64 and session-long), a `VolFactor` (pairwise realized variance), and the opening range (high/low of bar VWAP during LatePremarket only).
- `OrbSystem` makes entry/exit decisions from the bar stream.
- `FillSimulator` turns decisions into realistic fills (5th percentile limit within the bar, 100ms latency, 30% rejection, $0.0035/share commission).

## Chronological results (all fill-sim unless noted)

### 1. Initial ORB: long breakouts, 64-bar VWMA stop (live-updating)

First working version. Entry: `price > RangeHigh`. Exit: `price < Vwma64` (evaluated every bar).

- Decision PF **1.19**, 735 trades/day.

This was catastrophic churn. The live-updated VWMA was effectively a trailing stop — positions got knocked out and re-entered on every VWMA crossover.

### 2. Freeze the stop at entry

Capture `Vwma64` at entry, store it in `Active(price, position, stop)`, compare against the frozen value.

- Decision PF **1.51**, fill-sim PF 0.85, 12.4 decisions/day.

Commit: 6b851cc.

The freeze cut churn 60x but introduced new tension: shorts were dragging down the fill-sim PF, and a chunk of trades stopped out immediately because the entry price was already close to the (frozen) VWMA level.

### 3. Drop shorts

Shorts on RVOL≥3 breakouts lose money after borrow / execution costs that aren't fully modeled. Longs only.

- Fill-sim PF **1.37**, NetPnL +$203k, 21.9 round trips/day, 24.4% win rate.

Commit: a8a6f39.

### 4. VWMA-distance filter sweep

Hypothesis: breakouts that happen closer to the 64-bar VWMA should have better R:R because the VWMA is a natural support level.

Sweep of `maxVwmaDist` (entry only if `(price - Vwma64) / (price * VolFactor) ≤ maxDist`):

| maxDist | PF |
|---|---|
| 0.50 | 0.70 |
| 2.32 | 0.91 |
| 6.46 | 1.23 |
| 10.77 | 1.32 |
| 50.00 | 1.37 |

Monotonic — the **closer** the entry to VWMA, the **worse** the PF. The theory was wrong: near-VWMA breakouts fire too early (VWMA hasn't established a real trend yet); far-from-VWMA breakouts are where the real moves happen.

### 5. Flip the filter: minimum distance

Require `vwmaDist ≥ minDist`:

| minDist | PF |
|---|---|
| 0.00 | 1.405 |
| 0.73 | 1.409 |
| 1.41 | 1.413 |
| 5.32 | 1.384 |
| 10.31 | 1.269 |
| 20.00 | 1.102 |

Flat across `[0, ~2.7]`, then decays from loss of sample size. **The distance filter doesn't add meaningful alpha** — the ORB setup captures most of the "away from VWMA" effect inherently, because breakouts of the opening range tend to happen after price has already pulled away from VWMA.

### 6. Signal inversion check (fade near-VWMA breakouts)

Since near-VWMA entries (PF 0.70) were so bad, maybe flipping the signal (short those same setups) would be a profitable fade? Ran symmetric: short at range-high break with stop above at entry + (entry - VWMA).

- PF **0.45–0.63** across all `minDist` thresholds.

No. The losing trades at tight `maxDist` weren't losing because the direction was wrong — they were losing because the **stop was too tight relative to noise**. Tight-stop systems lose money even when the signal is right, and inverting doesn't help when your stop placement is the problem.

### 7. Stop-width sweep (vol-based stop)

Replace `Vwma64` stop with `price - stopVol * price * VolFactor`. 2D sweep over `minDist × stopVol`:

- At `stopVol = 1.0`: PF ~1.15
- At `stopVol = 3.2`: PF ~1.30
- At `stopVol = 10.0`: PF ~1.52
- At `stopVol = 30.0`: PF ~1.72
- At `stopVol = 100.0` (effectively no stop): PF ~1.76 (plateau)

**Stop width dominates; `minDist` filter has negligible effect at wide stops.** The 64-bar VWMA stop was way too tight — it was knocking us out of winners before they developed.

### 8. Range-based stop (`rangeLo`)

Natural idea: the opposite end of the opening range is the thesis-invalidation level. `stop = RangeLow`.

- Fill-sim PF **1.69**, NetPnL +$293k, **4.0** round trips/day, 47.4% win rate.
- Worst day -$1,800 (6% DD on $30k nominal).

Commit: 6e382ce.

This is the system. No tuning parameter — the range is self-calibrated to whatever pre-market volatility the stock happened to have. Matches the PF plateau of very-wide vol stops while keeping a finite, interpretable risk level.

Decision-level on the same setup: PF **1.95**, win rate 49.9%, avg winner $495 vs avg loser -$254 (1.95:1 R:R). Clean 50/50 coin flip with asymmetric payoff — textbook breakout profile.

### 9. Capped-range stop: strictly worse

Tried `stop = price - min(rangeDist, capVol * price * VolFactor)` — use rangeLo unless range is unusually wide. Any cap hurts PF monotonically (1.71 → 1.68 at cap=30 → 1.15 at cap=1).

**Wide ranges are where the best trades live.** Capping them removes days where the stock had a big pre-market range — which is precisely when the post-breakout trend is strongest. Counterintuitive but real: the "worst days" come from narrow-range stocks whipping through tight stops, not from wide-range stocks going against.

Commit: 1f6fab8.

### 10. Baseline: buy-at-open with no stop

To measure dataset directional bias:

- Decision PF **1.63**, fill-sim PF **1.52**, fill-sim NetPnL +$308k.

**Fill-sim NetPnL for naive buy-and-hold ($308k) is *higher* than the ORB system ($293k).** The ORB's PF advantage (1.69 vs 1.52) translates to lower drawdown (worst day -$1,800 vs -$3,544) but not more dollars captured.

Meaning: the RVOL≥3 breakouts dataset has a 57% same-day up-close rate. A huge chunk of the ORB system's edge is piggybacking on that base rate — ORB is "structured exposure to a bullish base rate" more than "directional alpha from timing."

### 11. Short side, full dataset

- Decision PF **1.03** (break-even), fill-sim PF **0.88** (-$59k).

The short side can't survive. This is the direct confirmation of the dataset bias discovered in step 10.

Commit: 44c52da.

### 12. Gap direction split — the punchline

Hypothesis: gap-ups should favor longs; gap-downs should favor shorts. Split the RVOL≥3 breakouts dataset by gap sign (open / prior_close - 1).

- 2,108 gap-up days
- 1,298 gap-down days
- Median gap: +6.8% (confirms the bullish skew)

**The 2x2 fill-sim matrix:**

|  | Gap-Up (n=2,108) | Gap-Down (n=1,298) |
|---|---|---|
| **Long ORB** | PF **1.87**, +$271k | PF 1.19, +$21k |
| **Short ORB** | PF 0.70, -$100k | PF **1.27**, +$42k |

Commit: 0588ac5.

- **Long-on-gap-up** is better than long-on-full-dataset (PF 1.87 vs 1.69). The gap-downs were dragging long performance.
- **Short-on-gap-down** is profitable (PF 1.27, +$42k) — the bearish signal exists; the full-dataset short loss was entirely from fighting the bullish base rate on gap-ups.
- **Short-on-gap-up** is the worst quadrant (PF 0.70) — fighting both the gap continuation and the base rate.
- **Long-on-gap-down** still positive (PF 1.19) — some residual bullish base rate even on gap-downs.

Combined long-gap-up + short-gap-down: PF **1.68**, NetPnL **+$313k**, direction-neutral. Slightly better than long-only full-dataset in dollars, and no longer dependent on the bullish skew.

## Key lessons

1. **Tight stops kill otherwise-valid signals.** The 64-bar VWMA stop turned a PF 1.95 decision-level system into fill-sim PF 1.37. Switching to rangeLo recovered most of the gap (1.69).

2. **The natural risk unit is the opening range itself.** It's self-calibrating (wider on volatile stocks, tighter on quiet ones), requires no tuning, and caps the tail without limiting upside.

3. **Wider ranges are the profit engine, not the risk.** The days where the stock has a big 8:30-to-open range are the days the trend continues longest post-breakout. Capping them reduces edge.

4. **Most of the edge is in stock selection, not intraday cleverness.** PF 1.52 from buy-at-open with no logic at all on this dataset. The "memorable failures" (high-volume stocks going nowhere) are availability bias — the boring grind-ups don't stick in memory.

5. **Gap direction is a free filter.** Splitting by `open / prior_close` sign turns a unidirectional long-only system into a direction-neutral long/short system with better per-trade PF on each side.

## Where this leaves the broader agenda

Before this session, the plan was to move to generative approaches after eliminating ORB. That's now deprioritized. The pre-market feature space — gap %, RVOL, catalyst type, premarket volume curve, float, sector — evidently contains most of the signal. A supervised predictor of "will this stock close above its open" trained on pre-9:30 features should be worth more than any intraday system refinement, because intraday would just be decorating an already-present edge.

The immediate next step is augmenting continuation_plays with same-day close metrics (close %, close % within daily range) so we can begin studying what pre-market configurations predict strong closes — and, downstream, build a stock-selection model for live trading.

## 13. Day-1 continuations after strong closes — all four quadrants losing

With the augmented continuation_plays dataset (gap_pct, close_vs_open_pct, close_in_range_pct), we tested whether the breakout-day bias carries into day 1.

Cohorts (day 1 after a RVOL≥3 breakout):
- **Bullish continuations (n=500):** breakout gapped up (median +9.9%) and closed in the top 20% of its daily range (median CIR 90.4%)
- **Bearish continuations (n=344):** breakout gapped down (median -10.7%) and closed in the bottom 20% of its daily range (median CIR 8.9%)

Fill-sim ORB on day 1:

| | Bullish cont. (n=500) | Bearish cont. (n=344) |
|---|---|---|
| **Long ORB** | PF 0.77, -$20.5k | PF 0.90, -$5.6k |
| **Short ORB** | PF 0.97, -$2.7k | PF 0.89, -$7.7k |

**All four quadrants are losing.** The breakout-day bias does not persist into day 1 in this dataset. The edge was entirely on day 0 — the day of the catalyst, high RVOL, and range expansion. By day 1:

- No fresh catalyst to drive volume
- The move has already happened — the stock is well-extended
- Other participants have had time to position against it
- RVOL is definitionally lower (day 0 was max-RVOL)

The naive "strong breakout → next day continues" thesis is not supported. Short-on-bullish-cont comes closest to break-even (-$5/day), hinting at a tiny mean-reversion tendency, but not tradeable.

This is an important negative result: it narrows the live-tradable universe to **breakout days themselves**, not their continuations, and reinforces the selection-based framing from section 12.

## 14. Day-1 continuations with a volume filter — the bias does persist

Re-running section 13 but requiring the **continuation day itself** to have RVOL ≥ 3:

- Bullish-cont + day-1 RVOL≥3: n=100 (out of 500)
- Bearish-cont + day-1 RVOL≥3: n=43 (out of 344)

| | Bullish cont. + RVOL≥3 (n=100) | Bearish cont. + RVOL≥3 (n=43) |
|---|---|---|
| **Long ORB** | PF **1.99**, +$14.1k | PF 1.07, +$0.5k |
| **Short ORB** | PF 0.70, -$5.7k | PF **1.42**, +$3.4k |

**Volume is the necessary condition.** Without it (section 13), all four quadrants lose. With it, the breakout-day bias reasserts itself cleanly:

- Long-on-bullish-cont PF **1.99** — essentially matching the day-0 decision-level PF 1.95.
- Short-on-bearish-cont PF **1.42** — meaningful edge, asymmetric to the long side (consistent with borrow/execution realities).
- Wrong-direction quadrants are flat-to-losing, confirming the signal is real.

The intuition holds: a breakout's directional signature only matters on day 1 if participants show up to continue the move. No volume → no continuation → no edge. This re-opens the door to multi-day continuation trading, but gated on live volume confirmation (which you can only assess intraday, not from pre-market alone).

Small-sample caveat: n=100 and n=43. The long-bullish result is strong enough to be meaningful; the short-bearish result is consistent with section 12's long-only dominance but warrants more data before building a shippable system.

## 15. Separating initial SIP breakouts from continuation-reset day-0s

The 3,409-day `breakouts_rvol3plus.json` file was a mix of two populations:

- **Initial SIP breakouts** (n=3,324): `date == breakout_date` — the first RVOL≥3 day in a chain.
- **Continuation resets** (n=85): `date != breakout_date` but `days_since_max_rvol == 0` — a *later* day in an existing chain that set a new RVOL high.

The continuation-reset subset is tiny (85 days) but qualitatively distinct: the stock already had a prior RVOL≥3 breakout within ~15 days, and the catalyst is re-igniting with fresh volume.

**Continuation-reset day-0 results:**

- **Long ORB: PF 3.89, +$30.3k, 65.7% day win rate** ($356/day)
- Short ORB: PF 0.83, -$2.5k

**Gap-split on continuation resets:**

| | Gap-up cont-reset (n=45) | Gap-down cont-reset (n=37) |
|---|---|---|
| **Long ORB** | PF **4.03**, +$17.6k | PF **4.02**, +$11.5k |
| **Short ORB** | PF 1.17, +$1.2k | PF 0.61, -$2.3k |

**Long dominates regardless of gap direction** on continuation resets — totally different from initial breakouts where the gap split cleanly flipped long/short favorability. Interpretation: when a stock sets a new RVOL high after already having a prior RVOL≥3 breakout in the chain, the directional signature is overwhelmingly bullish. Gap-downs appear to be pullbacks that get bought back, not genuine reversals.

Practical implication: **continuation resets are the highest-edge subset** we've found. The SIP-screen pre-selection (catalyst quality) plus the re-breakout (catalyst still working) combine multiplicatively. PF ~4 is in a different regime from the ~1.7–1.9 we saw on initial breakouts.

Caveat: n=85 total, n=45 and n=37 per gap bucket. The effect is large enough to be real but the strategy needs more data before live deployment — the universe of "stocks that set a new RVOL high shortly after a prior RVOL≥3 event" is narrow (~25/year).
