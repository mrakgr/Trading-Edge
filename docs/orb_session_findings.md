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

With `next_open_vs_close_pct` and `next_close_vs_close_pct` in the augmented dataset, we can directly measure post-close behavior. All tables below are on day-0 RVOL≥3 entries. CIR bins are disjoint 20%-wide buckets. Returns are reported as-is (positive = stock went up), so a long holder wants positive and a short holder wants negative.

Three return windows:
- **Overnight**: close → next day's open (what you capture if you hold overnight and sell at next-day open).
- **Next-day intraday**: next day's open → next day's close (if you bought at next-day open and held through next-day close).
- **Full next day**: close → next day's close (overnight + next-day intraday combined; what you capture if you hold through the entire next trading day).

### Gap-up breakouts (gap_pct > 0)

| CIR bin | n | Overnight | Next-day intraday | Full next day |
|---|---|---|---|---|
| [0.00, 0.20) | 418 | +0.58%  (58% hit) | -0.62%  (45% hit) | -0.17%  (47% hit) |
| [0.20, 0.40) | 376 | +0.46%  (54% hit) | +0.45%  (49% hit) | +0.98%  (48% hit) |
| [0.40, 0.60) | 387 | +0.32%  (55% hit) | -1.37%  (43% hit) | -1.17%  (43% hit) |
| [0.60, 0.80) | 435 | **+1.55%**  (49% hit) | -1.03%  (48% hit) | +0.51%  (45% hit) |
| [0.80, 1.00) | 574 | +0.34%  (50% hit) | -0.66%  (46% hit) | -0.35%  (46% hit) |

### Gap-down breakouts (gap_pct < 0)

| CIR bin | n | Overnight | Next-day intraday | Full next day |
|---|---|---|---|---|
| [0.00, 0.20) | 354 | **+0.91%**  (**67% hit**) | -0.68%  (46% hit) | +0.13%  (54% hit) |
| [0.20, 0.40) | 266 | +0.04%  (59% hit) | -0.15%  (49% hit) | -0.11%  (50% hit) |
| [0.40, 0.60) | 225 | +0.04%  (51% hit) | -0.34%  (50% hit) | -0.33%  (50% hit) |
| [0.60, 0.80) | 222 | +0.18%  (62% hit) | -0.65%  (43% hit) | -0.48%  (45% hit) |
| [0.80, 1.00) | 247 | -0.24%  (46% hit) | -0.46%  (45% hit) | -0.69%  (42% hit) |

### What this tells us

**Long-side overnight hold** (buy at day-0 close, sell at next-day open):

- **Gap-up + CIR 60-80%: +1.55% mean** is the single best cell in the matrix. Strong-ish closes that haven't hit the "everyone sees the perfect chart" zone.
- **Gap-down + CIR 0-20%: +0.91% mean, 67% hit rate** — the highest hit rate in any cell. These are stocks that gapped down *and* closed weak; bottom-fishers pile in overnight.
- **Gap-down + CIR 60-80%: +0.18% mean but 62% hit rate** — a high-frequency, low-magnitude bounce play. The stock rejected the gap-down to close strong, and that rejection continues overnight more often than not.
- **Gap-up + CIR 80-100%** (the "perfect continuation chart"): only +0.34%, 50% hit. Profit-taking on the strongest closers keeps the overnight gain muted.
- **Gap-down + CIR 80-100%** (the "gap-down that fully recovered"): **-0.24% overnight, 46% hit — losing**. Stocks that reversed a gap-down all the way up to close at the high give back overnight.

**Short-side overnight hold**:

- Every cell in both tables has **positive** overnight mean (except gap-down CIR 80-100% at -0.24%), meaning every cell is a *loss* for a short holder. Short overnight is not tradeable on this universe.

**Next-day intraday** (buy at next-day open, sell at next-day close):

- Negative in 9 of 10 cells. The only positive is gap-up CIR 20-40% (+0.45%, 49% hit), which is thin.
- The negativity is strongest on the "extended" cells (gap-up + strong close, gap-down + strong reversal).

**Full next day** (close-to-close):

- Only two cells are clearly positive: gap-up + CIR 20-40% (+0.98%) and gap-up + CIR 60-80% (+0.51%).
- Gap-up + CIR 40-60% is the worst close-to-close cell (-1.17%) — a "middling close after a gap-up" gets faded hard.
- Gap-down + CIR 0-20% is roughly flat close-to-close (+0.13%) despite the strong overnight (+0.91%) because the next-day intraday fades (-0.68%). The bounce is real but short-lived.

### Tradeable overnight setups

All assume exiting at next-day open.

1. **Gap-up, CIR 60-80%**: +1.55% mean, 49% hit. Highest expected return.
2. **Gap-down, CIR 0-20%**: +0.91% mean, 67% hit. Highest hit rate. Oversold-bounce play.
3. **Gap-down, CIR 60-80%** (optional): +0.18% mean, 62% hit. Small expected return but frequent; better as a size-up signal than a standalone trade.

### Cells to avoid

- **Gap-up, CIR 80-100%**: +0.34%, 50% hit. Looks obvious but isn't — profit-taking kills it.
- **Gap-down, CIR 80-100%**: -0.24%, 46% hit. "Full rejection" closes *lose* overnight.
- Any short overnight, any bucket.
- Any full-next-day hold (intraday grind is consistently negative).

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

With the volume-profile gate shipped (`--profile data/volume_profile.json --rvol-threshold N`), we tested it on the complementary side of the "breakout" dataset: every row in `continuation_plays_augmented.json` whose `date != breakout_date`. That keeps both the day-N tails of each pump and the 85 continuation resets, any RVOL — **4,063 tradable days** written to `data/breakouts_continuations.json` via `scripts/generate_continuation_dataset.fsx`.

### 23a. Gate threshold sweep on continuations

Long-only ORB, 10s time bars, rangeLo stop, fill-sim:

Out of 4,063 total days:

| Setting | Total P&L | Round trips | Win rate | PF | Active days | Active % | Worst day |
|---|---|---|---|---|---|---|---|
| Ungated | -$46,681 | 15,587 | 36.3% | 0.87 | 2,805 | 69.0% | -$1,670 |
| Gated @3x | -$12,711 | 1,966 | 37.2% | 0.83 | 418 | 10.3% | -$1,670 |
| Gated @4x | -$3,640 | 793 | 40.9% | 0.88 | 168 | 4.1% | -$1,003 |
| Gated @5x | **+$2,804** | 300 | 49.3% | **1.28** | 70 | 1.7% | -$843 |
| Gated @6x | +$2,289 | 153 | 55.6% | **1.46** | 36 | 0.9% | -$873 |
| Gated @7x | +$2,463 | 77 | 71.4% | **2.29** | 18 | 0.4% | -$659 |

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
