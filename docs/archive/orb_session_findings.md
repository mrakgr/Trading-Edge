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

## 16. RVOL threshold sweep (long ORB, all day-0 breakouts)

Tested the long ORB across progressively higher RVOL minimums on all day-0 entries (initial + continuation resets combined):

| RVOL min | n | PF | NetPnL | Win rate | $/day |
|---|---|---|---|---|---|
| 3 (baseline) | 3,409 | 1.69 | $293k | 47.4% | $86 |
| 4 | 2,513 | **1.97** | $297k | 49.7% | $118 |
| 5 | 1,710 | **2.47** | $281k | 55.4% | $165 |
| 6 | 1,158 | **2.82** | $239k | 58.5% | $206 |
| 7 | 779 | 3.11 | $193k | 58.2% | $248 |
| 8 | 547 | 3.11 | $146k | 57.3% | $267 |
| 10 | 309 | **3.47** | $104k | 57.6% | $337 |

**PF scales almost linearly with RVOL minimum** up to ~7, then plateaus around 3.1–3.5.

Practical takeaways:
- **RVOL≥5 is an excellent operating point**: PF 2.47, $281k NetPnL (essentially matching the RVOL≥3 baseline in total dollars), win rate jumps from 47.4% → 55.4%, $/day improves 2x.
- **Total NetPnL starts declining above RVOL≥5** because the selectivity trade-off kicks in (fewer trades, higher PF per trade, but eventually you give up too many days).
- **Plateau around RVOL 7–10** suggests there's a regime boundary at ~5-7x RVOL — below that you're picking up "medium-interest" stocks that dilute the edge; above it you're in the "real catalyst" zone where most trades work.

The win-rate shift from 47% to 55-58% as RVOL rises is the cleanest signal that higher-RVOL days are qualitatively different — not just more of the same stuff, but a distinct population where the directional signal gets cleaner.

## 17. Full 2x2 matrix (long/short × gap-up/gap-down) at every RVOL threshold

Extension of section 16 — same RVOL thresholds, split by gap direction, long and short:

**Long ORB — PF:**

| RVOL min | Gap-Up | Gap-Down |
|---|---|---|
| 4 | 2.21 (n=1566, +$270k) | 1.32 (n=944, +$26k) |
| 5 | 2.71 (n=1118, +$251k) | 1.65 (n=591, +$29k) |
| 6 | 3.01 (n=795, +$210k) | 2.04 (n=362, +$27k) |
| 7 | 3.30 (n=567, +$171k) | 2.18 (n=211, +$21k) |
| 8 | 3.29 (n=418, +$133k) | 2.18 (n=129, +$14k) |
| 10 | 3.66 (n=259, +$98k) | 2.19 (n=50, +$7k) |

**Short ORB — PF:**

| RVOL min | Gap-Up | Gap-Down |
|---|---|---|
| 4 | 0.60 | 1.33 |
| 5 | 0.58 | 1.52 |
| 6 | 0.52 | 1.58 |
| 7 | 0.50 | 1.62 |
| 8 | 0.46 | 1.73 |
| 10 | 0.40 | **2.85** (n=50) |

**Key observations:**

1. **Long gap-up PF climbs 2.21 → 3.66 monotonically** as RVOL rises. Cleanest edge in the matrix.
2. **Long gap-down is profitable at every threshold** (1.32 → 2.19). At RVOL ≥ 4, even gap-downs lean bullish once price breaks the opening range — the prior insight that "gap-downs favor shorts" breaks down at high RVOL. Shift the rule: **once RVOL ≥ 3-4, treat both gap-ups and gap-downs as long opportunities.**
3. **Short gap-down PF climbs** 1.33 → 1.73, peaks at **2.85 at RVOL≥10** (n=50, small sample). The short edge exists but narrows: as RVOL rises, gap-downs get rarer (50/259 = 16% at RVOL≥10 vs 38% at RVOL≥4).
4. **Short gap-up gets *worse* with RVOL** (0.60 → 0.40). Never fight a high-volume gap-up.

**Practical implication: scale position size with RVOL rather than filtering.** A simple `size ∝ min(RVOL / 3, cap)` captures the edge progression without cliffs. Below RVOL 3, probably just sit out. Above RVOL 5, full size on gap-up longs and (for gap-down days) split long and short based on the 2x2 per-cell PF.

## 18. Overnight and next-day returns by gap direction × CIR bucket

> Moved to its own document: [`overnight_returns_by_gap_cir.md`](overnight_returns_by_gap_cir.md). It holds the full gap-up / gap-down × CIR tables, the per-cell interpretation, and the tradeable-overnight-setup shortlist.

## 22. Two structural fixes, then time bars — PF 2.07 baseline

After the overnight analysis, we revisited the core bar construction and found two more things that had been silently biasing results.

### 22a. Session range should track through RTH, not just pre-market

`CalculateFlags` had `includeInRange = true` only during `LatePremarket`. This meant the "opening range" was frozen at op+60s and never updated — so `price > RangeHigh` could fire multiple times as price whipped around any level above the pre-market range. Extending `includeInRange` through `AfterOpeningPrint` + `BeforeClosing` (so the range tracks running session highs/lows) and emitting each bar with the range *before* its own contribution (so a bar that sets a new high can itself trigger an entry):

- PF: 1.72 → **1.79** (volume bars, RVOL≥3, fill-sim).
- Worst day cut in half: -$10.3k → -$4.9k.
- Round trips down 14%, win rate up to 51.4%.

Late re-entries into already-mature moves got gated out. The "trailing session range" is now our canonical entry signal.

### 22b. Time bars instead of volume bars

Volume bars were contaminating position sizing. `VolFactor` is a per-bar realized-vol estimate, but with volume bars one bar represents wildly different amounts of wall-clock time depending on how fast volume is flowing. Sizing scales inversely with `VolFactor`, so on high-RVOL days (dense bars, small per-bar variance) position size exploded; on quiet stocks (sparse bars, big per-bar variance) it collapsed.

Replaced the `VolumeBarBuilder` path with a `TimeBarBuilder` that buckets trades into fixed-length time windows (aligned to midnight ET). Empty buckets are skipped — no bar is emitted for a time window with zero trades. `VolFactor` now measures per-N-seconds realized variance, which is consistent across stocks and regimes.

**Time-bar sweep (1s → 60s on RVOL≥3 breakouts, long ORB with rangeLo stop):**

| bucket | PF | NetPnL |
|---|---|---|
| 1s | 1.94 | $373k |
| 3s | 2.01 | $349k |
| 6s | 2.03 | $315k |
| 10s | 2.07 | $290k |
| 12s | 2.08 | $278k |
| 15s | **2.11** | $266k |
| 20s | 2.09 | $240k |
| 30s | 2.07 | $211k |
| 60s | 2.06 | $166k |

PF rises monotonically from 1s to ~15s (noise reduction), plateaus 15-30s, and loses consistency above 30s. NetPnL declines monotonically because smaller bars give smaller per-bar VolFactor → larger EffectiveSize → bigger gross PnL on both sides (ratio is the honest metric).

**10s chosen as default** — PF 2.07 with meaningful NetPnL retention, well away from the 30s+ noise region.

### 22c. Full RVOL × gap matrix on time bars

Re-ran section 17's matrix with time bars (10s, rangeLo):

**Long PF:**

| RVOL min | Gap-Up | Gap-Down |
|---|---|---|
| 3 | 2.27 ($258k, n=2108) | 1.45 ($30k, n=1298) |
| 4 | 2.63 ($249k, n=1566) | 1.73 ($35k, n=944) |
| 5 | 3.13 ($226k, n=1118) | 1.95 ($29k, n=591) |
| 6 | 3.01 ($201k, n=795) | 2.39 ($27k, n=362) |
| 7 | 3.82 ($165k, n=567) | 3.01 ($25k, n=211) |
| 8 | 3.85 ($129k, n=418) | 2.67 ($17k, n=129) |
| 10 | **4.92** ($107k, n=259) | **3.60** ($12k, n=50) |

**Short PF:**

| RVOL min | Gap-Up | Gap-Down |
|---|---|---|
| 3 | 0.63 (-$81k) | 1.30 ($30k) |
| 4 | 0.53 (-$80k) | 1.39 ($29k) |
| 5 | 0.48 (-$68k) | 1.54 ($25k) |
| 6 | 0.42 (-$57k) | 1.64 ($18k) |
| 7 | 0.41 (-$46k) | 1.62 ($11k) |
| 8 | 0.37 (-$39k) | 1.64 ($8k) |
| 10 | 0.32 (-$28k) | **2.83** ($6k) |

**Every long cell improved vs volume bars; short side is roughly unchanged.** The RVOL≥10 gap-up long hits PF 4.92 at 72% win rate (n=259). The monotonic climb 2.27 → 2.63 → 3.13 → 3.01 → 3.82 → 3.85 → 4.92 for long gap-ups strongly supports the "size-scale-with-RVOL" approach.

Volume-bar vs time-bar deltas at key cells:

| | Volume bars | Time bars |
|---|---|---|
| Long gap-up RVOL≥5 | 2.71 | **3.13** |
| Long gap-up RVOL≥10 | 3.66 | **4.92** |
| Long gap-down RVOL≥7 | 2.18 | **3.01** |
| Long gap-down RVOL≥10 | 2.19 | **3.60** |
| Short gap-down RVOL≥10 | 2.85 | 2.83 |

### 22d. Continuation resets and day-1 continuations on time bars

Reran sections 14 and 15 with time bars (10s, rangeLo).

**Day-0 continuation resets** (`days_since_max_rvol_day = 0` AND `date != breakout_date` — same-chain later day that set a new RVOL high):

| | Gap-Up (n=45) | Gap-Down (n=37) |
|---|---|---|
| **Long** | PF **5.65**, $11.9k | PF **7.99**, $13.9k |
| **Short** | PF 1.48, $1.7k | PF 0.62, -$1.3k |

Combined long (n=85): PF **6.65**, $27.9k, 58% win rate. ~2x the PF the volume-bar pipeline produced (3.89). Gap direction remains irrelevant on continuation resets — both favor long. PF 7.99 on gap-downs is the highest long cell we've ever measured.

**Day-1 continuations with day-1 RVOL≥3** (section 14's cohort):

| | Bullish-cont (n=100) | Bearish-cont (n=43) |
|---|---|---|
| **Long** | PF **2.24**, +$9.0k | PF 1.24, +$0.6k |
| **Short** | PF 0.72, -$2.9k | PF **3.10**, +$5.3k |

Both "correct-direction" cells are now clearly tradeable:
- Long-bullish-cont PF 2.24 (vs 1.99 with volume bars).
- **Short-bearish-cont PF 3.10** (vs 1.42 with volume bars — more than doubled).

Time bars make the short-on-bearish-continuation setup meaningfully viable, which volume bars had left borderline.

Size caveat persists: n=43 for bearish-cont. But the PF jump on both cells is consistent with everything else in the time-bar results — the signal was always there; volume bars were diluting it.

## Summary

Combining everything:

1. **Intraday ORB long** on high-RVOL breakouts, time bars, rangeLo stop:
   - RVOL≥3 baseline: PF **2.07** ($290k).
   - RVOL≥5 gap-up: PF **3.13** ($226k, n=1118) — prime sizing target.
   - RVOL≥10 gap-up: PF **4.92** ($107k, n=259) — scale size aggressively here.
   - Long gap-down now also works at every RVOL threshold (PF 1.45 → 3.60).
2. **Intraday ORB short** only viable on gap-downs. RVOL≥10: PF 2.83 (n=50).
3. **Continuation-reset day-0** (same-chain new RVOL high): long-only, PF **6.65** (n=85), gap-direction-agnostic. Highest-edge cohort we've found.
4. **Day-1 continuations with RVOL≥3**: long-bullish-cont PF 2.24, short-bearish-cont PF 3.10. Both sides tradeable if day-1 volume confirms.
5. **Overnight long hold** (exit at next-day open): gap-up CIR 60-80% (+1.55%), gap-down CIR 0-20% (+0.91%).
6. **Overnight short hold**: never.
7. **Next-day hold**: don't, unless fresh day-1 volume.

The core system: **day-0 ORB with 10s time bars, trailing session range, rangeLo stop, flatten at close.** Size-scale by RVOL; weight toward long gap-ups but don't drop long gap-downs. Continuation resets deserve aggressive sizing. Day-1 trades are gated on intraday volume confirmation. Overnight long book on the same universe for two CIR cells.

## 23. Predicted-RVOL gate on the full continuation universe (2026-04-18)

With the volume-profile gate shipped (`--profile data/volume_profile.json --rvol-threshold N`), we tested it on the complementary side of the "breakout" dataset: every row in `continuation_plays_augmented.json` whose `date != breakout_date`. That keeps both the day-N tails of each pump and the 85 continuation resets, any RVOL — **4,063 tradable days** written to `data/breakouts_continuations.json` via `scripts/dataset_generation/generate_continuation_dataset.fsx`.

### 23a. Gate threshold sweep on continuations

Long-only ORB, 10s time bars, rangeLo stop, fill-sim:

Out of 4,063 total days:

| Setting | Total P&L | Entries | Win rate | PF | Active days | Active % | Worst day |
|---|---|---|---|---|---|---|---|
| Ungated | -$46,681 | 3,188 | 36.3% | 0.87 | 2,805 | 69.0% | -$1,670 |
| Gated @3x | -$12,711 | 431 | 37.2% | 0.83 | 418 | 10.3% | -$1,670 |
| Gated @4x | -$3,640 | 172 | 40.9% | 0.88 | 168 | 4.1% | -$1,003 |
| Gated @5x | **+$2,804** | 70 | 49.3% | **1.28** | 70 | 1.7% | -$843 |
| Gated @6x | +$2,289 | 36 | 55.6% | **1.46** | 36 | 0.9% | -$873 |
| Gated @7x | +$2,463 | 18 | 71.4% | **2.29** | 18 | 0.4% | -$659 |

Entry counts come from the decision stream (one per trade taken), not round trips — the latter inflates by partial fills (e.g. 300 trips vs 70 entries at 5x). From 5x upward, entries and active days match exactly — the gate lets at most one trade fire per day in that regime.

**PF climbs monotonically with the threshold**, win rate monotonically, and the dataset becomes profitable at the 5x rung. At 7x the PF matches the day-0 RVOL≥3 breakout baseline (PF 2.07) but on only ~2% of days — roughly one trade every two weeks over a 2-year window.

### 23b. False-positive inspection (gated @3x)

Picked the 5 biggest single-day losers from the gated @3x run and exported per-bar diagnostics (`scripts/export_false_positive_diagnostics.fsx` → `scripts/visualization/false_positive_chart.py`):

1. SERV 2025-10-10 (-$1,670)
2. BIVI 2024-10-23 (-$1,073)
3. SRFM 2025-06-27 (-$1,003)
4. USAR 2026-01-27 (-$884)
5. DXST 2026-03-11 (-$876)

The user's read of the charts: none of these failures are attributable to the profiler itself. The predicted-RVOL curves behave as designed on those days — they simply identified genuinely high-RVOL sessions that happened to whipsaw through the opening range. The profile gate is doing volume forecasting well; the losses are about intraday behavior the ORB entry logic can't distinguish from real continuations.

### 23c. Takeaways

- **The profile gate's edge on continuations lives above 5x predicted RVOL** — below that it's flat-to-negative, and @3x (the default on breakouts) actively underperforms ungated. Threshold matters a lot more here than on breakouts.
- **The breakout universe and the continuation universe respond differently to the same gate.** At 3x the gate trims PF slightly on breakouts (2.06 → 1.49) and roughly preserves PnL; on continuations it barely changes PF but shrinks exposure ~87%. The continuations need a higher bar because their baseline is PF < 1 and the gate has to clear that floor before any volume-proxy signal shows up.
- **Monotonicity across 5 rungs is the encouraging signal for the HMM regime work.** There's a real predicted-RVOL gradient on continuations, just shifted higher than on breakouts. "High RVOL day" evidently means something different on a continuation — it has to be very high before the intraday behavior rhymes with a fresh-breakout day.
- **The false-positive chart tool is a reusable investigation harness.** Per-bar VWAP ±2σ, predicted RVOL with gate threshold, bar volume, and observed-vs-profile cumulative fraction. Reused for any (ticker, date) list we want to inspect.

## 24. MiniZinc-calibrated (Tv, Ta) threshold gate on the gap-up universe (2026-04-22)

Per-bucket (volume-ratio, transaction-ratio) thresholds calibrated via MiniZinc CP-SAT. Target: "end-of-day session RVOL ≥ target_rvol" at ≥80% precision with ≥30 firings per bucket. 2323 buckets (09:31 → 16:00 ET at 10s resolution) solved OPTIMAL; every bucket achieved precision_actual ≥ 80.00 in-sample.

Backtest run on `data/gap_up_universe_4w.json` (4,296 plays, every gap-up ≥5% over 2024-04-01 → 2025-04-17 with valid `session_volume_4w` 4w averages). ORB long, 10s time bars, rangeLo stop, fill-sim, $0.005/share commission.

All numbers below are on the **fail-closed gate**: out-of-range bucket (pre-09:31 or post-15:58 ET, where the sweep has no thresholds) now blocks entries instead of passing them through. The old pass-through behavior was letting late-day trades fire unconstrained, dragging win rate and PF down by a material amount on every gated configuration.

All trade-level metrics (PF, win_rate, avg_win/avg_loss) are computed on **net PnL** (after commissions), matching the convention in `TradingEdge.Orb.Program.fs`'s `breakdown` command. Under this convention `PF = win_rate × avg_win / ((1 − win_rate) × |avg_loss|)` holds exactly. Historical rows from the initial runs used a gross PF with net avg_win/avg_loss which didn't tie out — those numbers are superseded by the tables below.

**Full-day window (09:31 → 15:58 ET):**

| | Baseline | rv=3.0 p80 | rv=4.0 p80 | rv=4.0 p90 | rv=6.0 p90 | rv=8.0 p90 | mean rv=4.0 bp=10 |
|---|---|---|---|---|---|---|---|
| decisions | 6,330 | 2,264 | 1,460 | 1,138 | 456 | 194 | 262 |
| round trips | 15,192 | 4,389 | 2,768 | 2,183 | 832 | 343 | 463 |
| net PnL | -$67,464 | -$8,329 | -$3,352 | -$1,701 | **+$3,435** | +$2,175 | -$538 |
| profit factor | 0.900 | 0.984 | 0.998 | 1.009 | 1.129 | **1.187** | 1.002 |
| win rate | 34.7% | 40.1% | 39.3% | 39.4% | 39.1% | **44.0%** | 41.9% |
| avg win / avg loss | — | $87.79 / -$59.69 | $99.16 / -$64.36 | $96.99 / -$62.39 | $116.23 / -$65.99 | $108.32 / -$71.77 | $113.80 / -$81.94 |
| max drawdown | $69,333 | $20,845 | $14,823 | $12,778 | $8,484 | **$5,222** | $7,661 |
| daily Sharpe | -1.53 | -0.48 | -0.26 | -0.17 | +0.52 | +0.47 | -0.10 |

**Window capped at 10:30 ET (09:31 → 10:29):**

| | rv=3.0 p80 | rv=4.0 p80 | rv=4.0 p90 | rv=6.0 p90 | rv=8.0 p90 | mean rv=4.0 bp=10 |
|---|---|---|---|---|---|---|
| decisions | 1,758 | 1,102 | 772 | 312 | 120 | 222 |
| net PnL | -$11,553 | -$12,075 | -$8,890 | -$3,186 | -$1,078 | -$4,279 |
| profit factor | 0.940 | 0.894 | 0.887 | 0.904 | 0.910 | 0.812 |
| win rate | 38.8% | 35.7% | 35.9% | 35.1% | 33.2% | 34.3% |
| avg win / avg loss | $101.89 / -$68.75 | $118.34 / -$73.59 | $121.20 / -$76.39 | $135.19 / -$80.90 | $139.73 / -$76.16 | $131.57 / -$84.72 |
| max drawdown | $20,934 | $19,170 | $15,401 | $9,500 | $4,528 | $8,468 |
| daily Sharpe | -0.80 | -1.00 | -0.95 | -0.55 | -0.26 | -0.83 |

**Window capped at 10:00 ET (09:31 → 09:59):**

| | rv=3.0 p80 | rv=4.0 p80 | rv=4.0 p90 | rv=6.0 p90 | rv=8.0 p90 | mean rv=4.0 bp=10 |
|---|---|---|---|---|---|---|
| decisions | 1,520 | 932 | 652 | 270 | 78 | 178 |
| net PnL | -$11,237 | -$9,953 | -$10,956 | -$1,638 | +$206 | -$3,921 |
| profit factor | 0.931 | 0.898 | 0.834 | 0.949 | 1.049 | 0.773 |
| win rate | 38.6% | 36.3% | 35.1% | 37.3% | 36.8% | 32.3% |
| avg win / avg loss | $105.44 / -$71.08 | $120.78 / -$76.55 | $124.62 / -$80.90 | $139.14 / -$87.03 | $159.39 / -$88.31 | $145.52 / -$89.69 |
| max drawdown | $18,654 | $18,568 | $15,135 | $7,661 | $2,858 | $5,483 |
| daily Sharpe | -0.84 | -0.91 | -1.27 | -0.30 | +0.06 | -0.85 |

(Decisions count every entry/exit emitted by the system before the fill simulator chops them into partial fills. One intended round trip = 2 decisions. Round-trip count inflates by partial fills. Decisions are the correct activity proxy.)

### 24a. Tightening progression

Monotonic improvement on every metric as the calibration target tightens:

1. **rv=3.0, p80** (`int_scale=1024`, sweep wall ~6h, old max-Ta objective): median Tv 1.71, n_fired 5496/bucket. PF **0.984**.
2. **rv=4.0, p80** (`int_scale=256`, sweep wall 93 min, old max-Ta objective): median Tv 2.344 (+37%), n_fired 2701 (−51%). PF **0.998**.
3. **rv=4.0, p90** (`int_scale=256`, sweep wall 92 min, old max-Ta objective): median Tv 2.562 (+9% over p80), n_fired 2205 (−18%). PF **1.009** — first time above break-even on the trade edge.
4. **rv=6.0, p90** (`int_scale=256`, sweep wall 90 min, new min-Ta objective): median Tv **3.969** (+55%), n_fired 856 (−61%). PF **1.129**, net +$3,435. **First net-profitable run.**
5. **rv=8.0, p90** (`int_scale=256`, sweep wall 87 min, max-Ta objective restored): median Tv **5.477** (+38%), n_fired 405 (−53%). PF **1.187**, net +$2,175. Max drawdown down to **$5,222** (17% of $30k nominal).

Between steps 3 and 4 the MiniZinc model's lex tie-break was flipped from *maximize Ta* to *minimize Ta* as an experiment. Adjacent-bucket |Ta step| metrics on the resulting CSVs:

- **max-Ta (rv=4.0 p90)**: median 0.06, p95 0.56, max 0.78.
- **min-Ta (rv=6.0 p90)**: median 0.14, p95 1.08, max 1.93.
- **max-Ta (rv=8.0 p90)**: median **0.008**, p95 1.14, max 1.67.

The min-Ta variant looks worse because the permissive corner discretely toggles between `Ta=0` and `Ta>0` across adjacent buckets (510 / 2323 buckets landed at Ta=0 under min-Ta). The max-Ta variant produces a far smoother Ta series — at rv=8.0 p=90 the median adjacent-step is basically zero. For out-of-sample generalization, smoothness in the calibrated parameters is valued more than finding the "true" permissive corner: a gate whose Ta wobbles between 0 and 1 across 10s buckets is unlikely to hold up. Reverted to max-Ta for step 5.

The `int_scale` coarsening from 1024 → 256 gave a 2.8× per-bucket solve speedup with identical Tv/Ta output at the integer-quantization level. Verified on b1140: both configs produce `Tv=1.723, Ta=0.430, n_fired=3502, n_hit=2802` — bit-for-bit identical output. RSS peaks drop from 1.36 GB to 481 MB per solver process, letting 6 parallel jobs fit in 2.9 GB total.

Per-bucket solver throughput: **6 jobs × 2 CP-SAT threads** beat **1 job × 14 CP-SAT threads** by ~2× on aggregate wall time. Per-bucket wall drops from 14s → 5s at 14 threads, but the loss of 6-way parallelism across buckets more than offsets it. CP-SAT's internal parallelism is strongly sub-linear past ~4 workers.

### 24b. Crossing into profit at rv=6.0 p=90; still climbing at rv=8.0

rv=6.0 p=90 is the first net-profitable rung: PF 1.129, **+$3,435**, Sharpe +0.52, max DD $8.5k. rv=8.0 p=90 pushes PF further to **1.187** but net PnL falls to +$2,175 — fewer trades × higher PF-per-trade, the classic diminishing-returns signature. Max DD shrinks again to $5,222.

Win rate now *rises* at the far end of the ladder (39.4% → 39.1% → 44.0%). That's different from the initial pre-gate-fix reading which showed win rate falling monotonically. Under the corrected gate the tightest rung is also the most decisive: fewer but cleaner entries.

Activity level drops from ~219 entries/year at rv=6.0 to **~93 entries/year** at rv=8.0 (~2/week, ~0.4/day). At this rate one more rung (rv=10) would push us below 1/week.

**PF gradient** on the ladder: +0.014 → +0.011 → +0.120 → **+0.058**. The big step was 4→6; the 6→8 step has roughly halved the gain but is still meaningful. Whether rv=10 adds another rung before flattening is the open question.

### 24b.5. Gate fail-closed fix

Originally the gate passed on out-of-range buckets and NaN thresholds, on the theory that "no data" meant "no constraint." That was wrong — trades post-15:58 ET (bucket ≥ 2323 with the sweep's 09:31-anchored schedule) were firing unconstrained, and those trades were worse than average: the late-afternoon window has poor ORB signal with neither fresh catalyst nor a full session of price discovery ahead. Switching the fallback to "block on out-of-range / NaN" removes 31-39% of decisions (depending on config) and lifts every metric on every gated run. Re-running the ladder under the corrected gate produced the numbers in the tables above.

### 24b.6. The "first hour is where the edge lives" hypothesis is false

The expectation going in was that ORB edge concentrates in the opening hour — whip volatility, fresh catalyst reaction, retail order flow. Capping the trading window at 10:30 or 10:00 ET should have preserved most of the edge at a fraction of the exposure. **Every config got worse under both cutoffs.** For the two profitable rungs:

- **rv=6.0 p=90**: PF 1.129 → 0.904 (10:30) → 0.949 (10:00); net +$3,435 → −$3,186 → −$1,638.
- **rv=8.0 p=90**: PF 1.187 → 0.910 (10:30) → 1.049 (10:00); net +$2,175 → −$1,078 → +$206.

The 74 rv=8.0 decisions between 10:30 and 15:58 contributed **+$3,253** net on their own (the full-day result minus the 10:30 result). They were not just carrying weight — they were carrying the *system*.

Mechanism hypothesis (to verify later): on gap-up days the first hour is dominated by two competing flows — algorithmic VWAP reversion against the gap, and retail catalyst-chase buying. Those forces cancel and the range is whippy. By 10:30 the VWAP pressure exhausts (institutional positions set for the day) and whichever side has catalyst backing gets room to run. High-rvol gates, by construction, pick days with real catalyst backing — so their post-10:30 firings catch the clean leg.

This contradicts common retail wisdom ("trade the open") but is consistent with sections 22 and 23 (post-open continuation systems with volume confirmation have strong edges). The calibration universe for this section is gap-ups ≥5%, which is a directionally-biased population to begin with — the gate is finding the subset where bias wins, and that win plays out *after* the whipsaw.

Practical implication: **do not constrain the active window for this gate**. The whole session matters. If anything, a 10:30 *start* might be worth testing (block the whipsaw, keep the continuation) — left as a future experiment.

### 24c. Custom solver — deferred

A hand-rolled iterated local search solver at [scripts/sweep_thresholds_local.fsx](scripts/sweep_thresholds_local.fsx) was prototyped to replace MiniZinc. Quantizes rows into a 2D suffix-sum grid for O(1) candidate evaluation; deterministic move-set enumeration with visited-set caching; struct-record score with field order `{nFired; negativeTv; negativeTa}` so default F# comparison does the lex sort.

Spot check on b1140: matched MiniZinc's n_fired exactly (2038) and found a *better* (smaller) Ta than MZ's old max-Ta output (Ta=0 vs Ta=0.207). Full sweep at default `moveScale=Tmax/16=64` missed optima on 472 / 2323 buckets (typical miss 5 rows = 0.24% of n_fired) — local minima at the Ta=0 axis because the better corner was `(Tv-7, Ta+110)` and +110 exceeds the 64-radius neighbourhood. `moveScale=256 (Tmax/4)` fixes it at ~6s per bucket (vs 0.22s at radius 64, 14s for MiniZinc) — still ~2.5× faster than MiniZinc. Deferred for now; ready to use when we want more iteration velocity on the gate experiments.

## 25. Drop MiniZinc — compute (Tv, Ta) by upper-tail quantiles of (vr, tr) directly (2026-04-23)

The MiniZinc calibration pipeline is solving a picking problem — given a precision target, find the loosest thresholds that hit it. But the precision target is a proxy: we want thresholds that are tight enough that the remaining rows are atypical (real conviction), and loose enough to fire on enough days to matter. That's a *quantile* question, not an *optimization* question. Writing it as a CP-SAT problem makes the grid sweep cost hours to explore a handful of cells; a direct quantile computation covers 2,323 buckets × 36 cells in 48 seconds.

Implementation at [scripts/sweep_thresholds_quantile.fsx](scripts/sweep_thresholds_quantile.fsx). For each bucket's DZN file, sort vr and tr descending, pick `Tv = vr[floor(X · n)]` and `Ta = tr[floor(Y · n)]`, then count fires and hits exactly the way the MiniZinc model does. Grid: `X ∈ {0.01%, 0.02%, 0.05%, 0.1%, 0.2%, 0.5%, 1%, 2%, 5%}` (9 cells, ~2× steps) × `Y ∈ {1%, 2.5%, 5%, 10%}` (4 cells). `precision_pct` in the output CSV encodes the cell as `Xbp·1000 + Ybp` so the backtest script reads it unchanged.

### 25a. Full grid on the gap-up universe (training, 2024-04-01 → 2025-04-17)

| X (vol tail) | Y=1% | Y=2.5% | Y=5% | Y=10% |
|---|---|---|---|---|
| 5%   (500bp) | PF 0.927 / −$13,521 / 2180 dec | PF 0.964 / −$8,809 / 3302 dec | PF 0.957 / −$11,448 / 3726 dec | PF 0.966 / −$9,125 / 3858 dec |
| 2%   (200bp) | PF 0.946 / −$8,522 / 1946 dec | PF 0.943 / −$10,495 / 2536 dec | PF 0.941 / −$10,995 / 2604 dec | PF 0.939 / −$11,371 / 2608 dec |
| 1%   (100bp) | PF 0.992 / −$958 / 1534 dec | PF 1.007 / +$884 / 1698 dec | PF 1.006 / +$700 / 1704 dec | PF 1.007 / +$851 / 1704 dec |
| 0.5% (50bp)  | PF 0.951 / −$3,799 / 992 dec | PF 0.946 / −$4,210 / 1004 dec | PF 0.941 / −$4,644 / 1008 dec | PF 0.941 / −$4,635 / 1008 dec |
| 0.2% (20bp)  | **PF 1.156 / +$5,175 / 438 dec** | **PF 1.157 / +$5,214 / 440 dec** | **PF 1.157 / +$5,214 / 440 dec** | **PF 1.157 / +$5,214 / 440 dec** |
| 0.1% (10bp)  | PF 0.935 / −$1,253 / 232 dec | PF 0.935 / −$1,253 / 232 dec | PF 0.935 / −$1,253 / 232 dec | PF 0.935 / −$1,253 / 232 dec |
| 0.05% (5bp)  | PF 1.062 / +$582 / 106 dec | PF 1.062 / +$582 / 106 dec | PF 1.062 / +$582 / 106 dec | PF 1.062 / +$582 / 106 dec |
| 0.02% (2bp)  | PF 1.393 / +$1,070 / 42 dec | PF 1.393 / +$1,070 / 42 dec | PF 1.393 / +$1,070 / 42 dec | PF 1.393 / +$1,070 / 42 dec |
| 0.01% (1bp)  | PF 2.054 / +$1,258 / 20 dec | PF 2.054 / +$1,258 / 20 dec | PF 2.054 / +$1,258 / 20 dec | PF 2.054 / +$1,258 / 20 dec |

### 25b. The Y dimension collapses at X ≤ 0.1%

Below X=0.1% the Y dimension is inert — all four Y values produce identical decision counts. At those tight volume cuts the surviving rows already satisfy any reasonable transaction threshold, so Ta isn't binding. The volume tail *is* the signal.

### 25c. Only one cell has real, non-tiny edge

The sweep produces exactly one operating point with both a material PF and a non-tiny sample: **X=0.2% (20bp)** — PF 1.157, +$5,214 net, 440 decisions, win rate 39.7%. For comparison, the best MiniZinc calibration (`rv=8.0 p=90` full-day) was PF 1.187 on 194 decisions for +$2,175 net. The 20bp quantile cell has **comparable PF on 2.3× the decisions and 2.4× the net dollars** — a better operating point.

The very-tight cells (X ≤ 0.05%) show higher PF (1.4–2.1), but on 20–106 decisions over a three-year training window — roughly ten trades per year at X=0.01%. PF is sample-size-sensitive at that count and the dollar profit doesn't justify the infrastructure. These are not tradeable edges, they're curiosities.

### 25d. The X=0.1% inversion is suspicious

Between X=0.05% (PF 1.062, 106 dec) and X=0.2% (PF 1.157, 440 dec), X=0.1% comes in at PF 0.935 on 232 decisions. That's a sample-size-driven dip, not a structural property — doubling from 106 to 232 adds 126 decisions that happened to be net losers during this window. Noise, not signal. Called out so we don't over-read the pattern.

### 25e. Does gap-up trading have edge?

The full grid's answer is: marginally, in a narrow pocket. X=0.2% fires ~150 times/year and produces ~$1,700/year net at $30K per position. That's real edge but shallow — roughly 5.7% annualized return on capital deployed (ignoring idle cash), not enough to run a system on its own. The tight cells have better PF but ~10 trades/year, which isn't statistically testable and isn't economically meaningful.

Before drawing harder conclusions, the 20bp cell needs to be validated on out-of-sample data. If PF 1.16 survives validation, it's a real (if small) edge. If it collapses, gap-up-only is dead and the path forward is continuations.

### 25f. Next: validation

Both the surviving 20bp quantile cell and the best MiniZinc calibrations (`rv=6.0 p=90`, `rv=8.0 p=90`) need to be tested on held-out data (2025-04-18 → 2026-04-17). That requires building the validation set — see [docs/bulk_to_binary_pipeline.md](bulk_to_binary_pipeline.md) for the conversion chain.

## 26. Validation run — the gap-up edge does not survive out of sample (2026-04-23)

Validation universe: 6128 gap-up plays over 2025-04-18 → 2026-04-17 (250 trading days, ~25/day median — roughly 45% more setups per day than training, partly from three broad-market shock days of 500/422/237 gap-ups and partly from a genuine baseline regime shift). Same gate on entry, same fill sim, same $0.005/share commission. Pipeline build documented in [docs/bulk_to_binary_pipeline.md](bulk_to_binary_pipeline.md).

### 26a. Comparison table (training ← → validation)

Quantile configs run at Y=2.5% (the Y dimension collapses at tight X, so Y choice is inert). MiniZinc configs use the existing tightening ladder.

| Config | Train PF | Train net | Train dec | **Val PF** | **Val net** | **Val dec** | Verdict |
|---|---|---|---|---|---|---|---|
| q20bp / Y=2.5% | 1.157 | +$5,214 | 440 | **1.015** | **+$562** | 484 | Edge gone |
| q10bp / Y=2.5% | 0.935 | −$1,253 | 232 | 0.859 | −$3,000 | 248 | Still bad |
| q5bp / Y=2.5% | 1.062 | +$582 | 106 | 1.138 | +$1,200 | 110 | Held, but 110 dec/yr is underpowered |
| q2bp / Y=2.5% | 1.393 | +$1,070 | 42 | 0.882 | −$341 | 52 | Reverted to noise |
| q1bp / Y=2.5% | 2.054 | +$1,258 | 20 | 0.521 | −$1,580 | 32 | Reverted to noise |
| MZ rv=6.0 p=90 | 1.129 | +$3,435 | 378 | 1.010 | +$378 | 512 | Edge gone |
| MZ rv=8.0 p=90 | **1.187** | +$2,175 | 194 | **1.094** | **+$1,415** | 222 | Partial hold |

### 26b. The flagship 20bp cell collapsed

Training's best "real" operating point (PF 1.157, +$5,214 net on 440 decisions) came back at PF 1.015, +$562 net on 484 decisions. That's not "slight degradation" — that's commissions eating the entire edge. The sample size even increased (validation is more gap-up-dense), so this is not a statistical artifact. The 20bp cut-off was overfit to training — tight enough to look profitable on 440 specific days, not structural enough to generalize.

### 26c. MiniZinc rv=8.0 p=90 held up best

Of the seven configs tested, only `mz_rv80_p90` posted a validation PF above 1.05 on a sample size that isn't tiny. Drop from training PF 1.187 → 1.094 is real (≈8% PF compression) but it survived. At 222 decisions/year and +$1,415 net, that's ~$1,400/year on $30K position — call it 4–5% annualized on capital-at-work, ignoring idle.

Hypothesis for why MiniZinc generalized slightly better than quantiles: the precision target acts as a regularizer. The 90th-percentile constraint forces the thresholds to be loose enough that many independent days contribute to the "hit" count, which is a mild smoothness penalty. A pure quantile cut has no such constraint — the 20bp picks whatever the single tightest cut is in each bucket, which is easier to overfit to local noise.

### 26d. The X=5bp quantile was the surprise

It went from PF 1.062 (training) to PF 1.138 (validation), *improving* out of sample. That's the opposite pattern of every other cell and should be treated with suspicion. Most likely explanation: 106 → 110 decisions is so few trades that PF is dominated by 2–3 big days. Not a trustworthy signal — treat it as noise unless a much larger sample ever agrees.

### 26e. Conclusion: gap-up ORB as a standalone system is dead

The honest read of the validation table:

- No config with a meaningful decision count (> 200/year) posted a validation PF above 1.10.
- The best survivor (`rv=8.0 p=90`) nets $1,400/year — a hobby, not a system.
- "Going tighter" (higher rvol, stricter quantile) doesn't rescue this — the tight-tail cells (2bp, 1bp, plus rv=8.0 at 222 dec) show the same PF-fade-with-sample-growth pattern that the training set hinted at: the edge is a mirage that thins as you average over more data.

What does this tell us? Gap-up ≥5% is not, on its own, a structural edge under ORB. The population has too much noise and too little systematic bias once you get past the survivorship filter of a manual chart review.

### 26f. Next: move to HMM / regime detection

Rather than hunt for a narrower setup population under the same ORB trigger (e.g. continuations — close-up ≥5% on rvol ≥3 seeds with the 80% volume-chain rule), the conclusion from this line of work is that the ORB trigger itself is underpowered as a standalone edge source on any breakout-family population we've tested. Tightening the setup funnel does not rescue a weak trigger. Future work moves to regime/state-based approaches (HMMs) — a structurally different model of when the market offers edge, not another variation on the same threshold-calibration theme.
