# Donchian-fade engine — v0 research notes

## Setup

Lance-style breakout-failure fade on 1-minute crypto-perps bars. The setup is pure price-structure:

- Maintain a rolling `DonchianBars` (= 3) high/low channel.
- On every bar, increment `bars_since_up_violation` if `bar.High <= prior_donchian_high`, else reset to 0. Symmetric for `bars_since_down_violation`.
- **Short entry**: `bars_since_down_violation >= 30` (sustained 30-bar uptrend run with no down-violations) AND the current bar's Low pierces the prior Donchian low.
- **Long entry**: symmetric (sustained downtrend then break-up).
- **Stop**: ratcheted Donchian-extreme trailing stop. For shorts, snapshot the prior 3-bar high at entry; on each subsequent bar, ratchet down only (`min(stop, donHighMa.State)`). Symmetric for longs.
- **Cover**: opposite-channel re-violation. Short covers when `bar.High > trailingStop`; long covers when `bar.Low < trailingStop`. Fill at `bar.Close`.

Standard cross-engine flags applied: `--reference-vol-pct 0.1019 --max-bar-price-ratio 3 --min-short-adv 5000000 --min-long-adv 5000000`. 1m bars, 2024-05-01 → 2026-04-30, 643 perps universe.

The trip CSV records five engine-specific post-hoc fields at entry:

| Field | Definition |
|---|---|
| `bars_since_up_violation` | bars since last upper-channel break |
| `bars_since_down_violation` | bars since last lower-channel break |
| `pct_1h_change` | `bar.Close / closeShortMa.mean - 1` (1h MA-relative) |
| `pct_72h_change` | `bar.Close / closeLongMa.mean - 1` (72h MA-relative) |
| `vol_ratio_1h_over_72h` | `(volShortMa.mean) / (volLongMa.mean)` |

## Aggregate result

| Metric | Value |
|---|---|
| Symbols | 639 |
| Total trips | 316,413 |
| Long trips | 161,154 |
| Short trips | 155,259 |
| Net P&L | **-$193,728** |
| Aggregate PF | **0.446** |
| Win rate | 24.9% |
| Mean bars held | 14.0 |

| Side | Trips | PF | Net P&L | Win rate | Mean bars |
|---|---:|---:|---:|---:|---:|
| Long | 161,154 | 0.465 | -$94,901 | 25.1% | 13.8 |
| Short | 155,259 | 0.426 | -$98,828 | 24.6% | 14.1 |

The fade hypothesis loses both ways. Both sides bleed at roughly equal rates — the symmetry suggests the loss is structural, not a directional artefact.

## Post-hoc breakdowns

Each post-hoc field is binned into 10 equal-population deciles per side, then PF / net P&L / win rate are computed within each decile. The breakdowns *use* the trip CSV from the running engine — they do not rerun the engine with different gates.

### `pct_72h_change` deciles

Position vs the 72h trailing MA at entry, signed.

**Short side** — strongest at the extremes, dead-zone in the middle:

| Decile | Range | Trips | PF | Net P&L | Win rate |
|---:|---|---:|---:|---:|---:|
| 0 | [-99.9%, -5.30%] | 15,526 | 0.464 | -$10,094 | 25.9% |
| 1 | [-5.30%, -3.18%] | 15,526 | 0.402 | -$10,109 | 25.2% |
| 2 | [-3.18%, -1.87%] | 15,526 | 0.385 | -$9,987 | 24.6% |
| 3 | [-1.87%, -0.82%] | 15,526 | 0.366 | -$10,268 | 23.7% |
| 4 | [-0.82%, +0.01%] | 15,526 | 0.317 | -$10,670 | 20.3% |
| 5 | [+0.01%, +0.89%] | 15,525 | 0.350 | -$10,055 | 21.8% |
| 6 | [+0.89%, +1.94%] | 15,526 | 0.362 | -$10,304 | 23.8% |
| 7 | [+1.94%, +3.26%] | 15,526 | 0.396 | -$9,955 | 25.1% |
| 8 | [+3.26%, +5.39%] | 15,526 | 0.455 | -$9,508 | 26.0% |
| **9** | **[+5.39%, +284%]** | **15,526** | **0.655** | **-$7,879** | **29.8%** |

**Long side** — mirror image, extreme downtrend (D0) is best:

| Decile | Range | Trips | PF | Net P&L | Win rate |
|---:|---|---:|---:|---:|---:|
| **0** | **[-99.9%, -7.09%]** | **16,116** | **0.816** | **-$4,067** | **31.3%** |
| 1 | [-7.09%, -4.63%] | 16,115 | 0.570 | -$7,675 | 29.2% |
| 2 | [-4.63%, -3.19%] | 16,115 | 0.466 | -$9,156 | 26.5% |
| 3 | [-3.19%, -2.12%] | 16,116 | 0.404 | -$10,184 | 25.1% |
| 4 | [-2.12%, -1.17%] | 16,115 | 0.397 | -$10,231 | 24.1% |
| 5 | [-1.17%, -0.26%] | 16,115 | 0.362 | -$10,822 | 23.5% |
| 6 | [-0.26%, +0.53%] | 16,116 | 0.298 | -$11,287 | 18.5% |
| 7 | [+0.53%, +1.70%] | 16,115 | 0.357 | -$11,043 | 23.1% |
| 8 | [+1.70%, +3.49%] | 16,115 | 0.384 | -$10,659 | 23.7% |
| 9 | [+3.49%, +154%] | 16,116 | 0.480 | -$9,777 | 25.8% |

Both sides peak at the extreme of their *own* trend direction — short fades work best when price is most stretched up; long fades work best when price is most stretched down. Mid-buckets are a graveyard.

### `pct_1h_change` deciles

Position vs the 1h trailing MA at entry. Same shape as 72h but cleaner monotonicity:

**Short side** — clean monotone, low → high PF:

| Decile | Range | Trips | PF | Net P&L | Win rate |
|---:|---|---:|---:|---:|---:|
| 0 | [-99.9%, -0.16%] | 15,526 | 0.213 | -$19,438 | 14.1% |
| 1 | [-0.16%, +0.02%] | 15,526 | 0.253 | -$12,590 | 16.4% |
| 2 | [+0.02%, +0.17%] | 15,526 | 0.342 | -$10,106 | 22.1% |
| 3 | [+0.17%, +0.30%] | 15,526 | 0.392 | -$8,751 | 24.3% |
| 4 | [+0.30%, +0.43%] | 15,526 | 0.413 | -$8,296 | 25.7% |
| 5 | [+0.43%, +0.58%] | 15,525 | 0.463 | -$7,485 | 26.9% |
| 6 | [+0.58%, +0.77%] | 15,526 | 0.481 | -$7,422 | 27.8% |
| 7 | [+0.77%, +1.04%] | 15,526 | 0.489 | -$7,880 | 28.1% |
| 8 | [+1.04%, +1.57%] | 15,526 | 0.549 | -$7,666 | 30.2% |
| **9** | **[+1.57%, +84.1%]** | **15,526** | **0.647** | **-$9,195** | **30.8%** |

**Long side** — clean reverse monotone, high → low:

| Decile | Range | Trips | PF | Net P&L | Win rate |
|---:|---|---:|---:|---:|---:|
| **0** | **[-99.8%, -1.50%]** | **16,116** | **0.802** | **-$4,985** | **33.3%** |
| 1 | [-1.50%, -1.02%] | 16,115 | 0.605 | -$6,846 | 31.0% |
| 2 | [-1.02%, -0.76%] | 16,115 | 0.577 | -$6,659 | 29.9% |
| 3 | [-0.76%, -0.57%] | 16,116 | 0.536 | -$6,778 | 28.9% |
| 4 | [-0.57%, -0.42%] | 16,115 | 0.474 | -$7,721 | 26.7% |
| 5 | [-0.42%, -0.29%] | 16,115 | 0.426 | -$8,607 | 24.9% |
| 6 | [-0.29%, -0.17%] | 16,116 | 0.407 | -$9,055 | 23.9% |
| 7 | [-0.17%, -0.02%] | 16,115 | 0.337 | -$10,770 | 21.9% |
| 8 | [-0.02%, +0.16%] | 16,115 | 0.258 | -$13,141 | 16.4% |
| 9 | [+0.16%, +11.4%] | 16,116 | 0.209 | -$20,337 | 14.0% |

The strongest signal in the dataset. Short PF rises monotonically with `pct_1h_change`; long PF falls monotonically. Both sides perform worst when entered against their own thesis (short with negative 1h change → long was trending down; long with positive 1h change → short was trending up). The fade is *least* profitable when it's most consistent with the immediate-prior 1h direction.

### `vol_ratio_1h_over_72h` deciles

Trailing 1h vol divided by trailing 72h vol — measures whether the entry bar comes during a recent volume burst or a quiet patch.

**Short side** — clean monotone, low vol → high vol:

| Decile | Range | Trips | PF | Net P&L | Win rate |
|---:|---|---:|---:|---:|---:|
| 0 | [0.01, 0.31] | 15,526 | 0.335 | -$10,147 | 21.5% |
| 1 | [0.31, 0.41] | 15,526 | 0.353 | -$9,944 | 22.9% |
| 2 | [0.41, 0.51] | 15,526 | 0.361 | -$9,859 | 23.8% |
| 3 | [0.51, 0.60] | 15,526 | 0.375 | -$9,749 | 24.2% |
| 4 | [0.60, 0.71] | 15,526 | 0.387 | -$9,758 | 24.1% |
| 5 | [0.71, 0.84] | 15,525 | 0.388 | -$10,006 | 24.6% |
| 6 | [0.84, 1.01] | 15,526 | 0.449 | -$9,065 | 25.7% |
| 7 | [1.01, 1.28] | 15,526 | 0.434 | -$9,854 | 25.7% |
| 8 | [1.28, 1.82] | 15,526 | 0.460 | -$10,049 | 26.5% |
| **9** | **[1.82, 63.0]** | **15,526** | **0.597** | **-$10,395** | **27.4%** |

**Long side** — same clean monotone:

| Decile | Range | Trips | PF | Net P&L | Win rate |
|---:|---|---:|---:|---:|---:|
| 0 | [0.02, 0.31] | 16,116 | 0.374 | -$10,053 | 21.3% |
| 1 | [0.31, 0.42] | 16,115 | 0.356 | -$10,327 | 22.8% |
| 2 | [0.42, 0.51] | 16,115 | 0.373 | -$10,067 | 23.4% |
| 3 | [0.51, 0.61] | 16,116 | 0.401 | -$9,874 | 24.1% |
| 4 | [0.61, 0.72] | 16,115 | 0.404 | -$9,862 | 24.4% |
| 5 | [0.72, 0.85] | 16,115 | 0.418 | -$9,896 | 24.9% |
| 6 | [0.85, 1.03] | 16,116 | 0.449 | -$9,556 | 26.1% |
| 7 | [1.03, 1.31] | 16,115 | 0.484 | -$9,290 | 26.2% |
| 8 | [1.31, 1.90] | 16,115 | 0.531 | -$8,985 | 27.0% |
| **9** | **[1.90, 68.0]** | **16,116** | **0.718** | **-$6,990** | **30.5%** |

Both sides prefer high recent vol. Symmetric, monotone, no surprises.

### `bars_held` quintiles — the override signal

Not requested, but checked for sanity. Hold duration dominates everything:

**Short side**:

| Quintile | Held range | Trips | PF | Net P&L | Win rate |
|---:|---|---:|---:|---:|---:|
| Q0 | [2, 4] | 34,997 | **0.013** | -$59,879 | **3.3%** |
| Q1 | [5, 6] | 28,662 | 0.040 | -$38,447 | 7.9% |
| Q2 | [7, 10] | 34,751 | 0.353 | -$21,071 | 26.5% |
| Q3 | [11, 17] | 27,901 | **1.259** | +$4,907 | **44.8%** |
| Q4 | [18, 2769] | 28,948 | **1.787** | +$15,662 | **45.3%** |

**Long side**:

| Quintile | Held range | Trips | PF | Net P&L | Win rate |
|---:|---|---:|---:|---:|---:|
| Q0 | [2, 4] | 35,885 | **0.011** | -$61,386 | **3.0%** |
| Q1 | [5, 6] | 30,612 | 0.045 | -$39,834 | 8.4% |
| Q2 | [7, 10] | 35,948 | 0.339 | -$22,388 | 26.8% |
| Q3 | [11, 16] | 26,997 | **1.342** | +$6,143 | **46.0%** |
| Q4 | [17, 2942] | 31,712 | **2.031** | +$22,564 | **46.5%** |

35% of trades exit in 2-4 bars at PF 0.01 / win rate 3%. Reading: the breaking bar's own wick is wide enough that the next bar's high (for shorts) or low (for longs) re-violates the just-set trailing stop, immediately covering. Trades that survive the first 10 bars are profitable in aggregate (PF 1.3-2.0).

The 3-bar Donchian cover is too tight. The cover Donchian needs to be wider than the entry Donchian, or there needs to be a min-hold-bars floor before the cover can fire.

## Reverse interpretation — momentum continuation, not fade

The decile breakdowns sketch a coherent picture that is the *opposite* of what was hypothesised:

- `pct_1h_change` short PF rises monotonically with the value at entry. The strongest short trades happen when price is most up-stretched at the 1h horizon.
- `pct_1h_change` long PF falls monotonically. The strongest long trades happen when price is most down-stretched at the 1h horizon.
- Same shape on `pct_72h_change`, slightly noisier.
- Both sides prefer high recent vol.

Read literally, the system does not benefit from fading the trend; it benefits from being *aligned with* the trend. The Donchian-break entry trigger fires in two regimes:
1. **Exhaustion-breaks** (the original thesis): a long trend ends and the first opposite-side wick is the local pivot. Fade.
2. **Continuation-breaks**: a long uptrend pulls back briefly, prints a low that pierces the rolling 3-bar low, and the trend resumes. Fade is wrong here; momentum is right.

The PF distribution suggests the continuation regime dominates — entries at deep `pct_1h_change` are *with* the underlying trend, not against it, and they're the ones that work. Inverting the engine (short on downtrend break-up, long on uptrend break-down) is the natural test.

## Open questions

1. **Continuation engine.** Run the entry logic with sides flipped. The Donchian-break is the same; only the side picked is reversed. If the decile shape still holds, the engine works directionally. If it inverts, the signal was an artefact of the trip-set, not the directional hypothesis.
2. **Cover-Donchian width.** Independent of the directional question, no version of this engine works with a 3-bar cover. Q0+Q1 (held ≤6 bars) bleeds at PF 0.03. Either widen the cover Donchian (e.g. 10-30 bars) or add a `MinHoldBars` floor.
3. **Entry-trend-length.** `bars_since_violation` quintile breakdowns showed shorter trend runs (D0-D3) outperform longer ones at default thresholds — opposite to the original "longer run = stronger fade" intuition. Worth a sweep over `--min-trend-bars`.

# v1 — reverse direction with trailing target

## Setup

Same entry detection as v0 (3-bar Donchian, 30-bar trend qualifier, break trigger). Two flags added:

- `--reverse-direction true`: uptrend break-down opens **LONG** (was Short), downtrend break-up opens **SHORT** (was Long). Tests the continuation-pullback hypothesis the v0 deciles pointed at.
- `--cover-mode entry-channel-target`: cover at the prior 3-bar Donchian extreme on the with-trend side. **Trailing**, not fixed: as the trade moves adverse, the target ratchets toward entry (long target = current `donHighMa.State` if it's lower than the last cached target; short target = `donLowMa.State` if higher). No stops.

Why trailing-target rather than fixed-target: a fixed take-profit with no stop is a backtest illusion. Trades that don't hit the target sit open as unrealized drawdown, invisible to the trip-level metrics. End-of-stream Flush eventually marks them at the symbol's last price, which often happens to be near entry — making the fixed-target version look like PF 17 when in reality it was hiding the losers off the books. The trailing target forces the engine to realize losses honestly: when price drifts against the position, the target collapses to current price and the trade closes near entry minus fees.

This is the *honest* version of the system. It's the one that survives contact with real capital.

## Aggregate

| Metric | Fade (v0) | Reverse + trailing (v1) |
|---|---:|---:|
| Total trips | 316,413 | **323,937** |
| Long / Short | 161,154 / 155,259 | 159,125 / 164,812 |
| Net P&L | -$193,728 | -$96,032 |
| Aggregate PF | 0.446 | **0.584** |
| Win rate | 24.9% | **39.9%** |
| Median bars held | — | 4 |
| Mean fees per trade | — | $0.43 |
| Total fees | — | $137,635 |

| Side | Trips | PF | Net P&L | Win rate |
|---|---:|---:|---:|---:|
| Long | 159,125 | 0.601 | -$44,148 | 39.7% |
| Short | 164,812 | 0.567 | -$51,884 | 40.1% |

The aggregate still loses to fees on average. Trades resolve fast (median 4 bars) and most close for tiny losses as the trailing target pursues adverse drift. **But the headline aggregate is misleading** — the deciles tell a different story.

## Decile breakdowns — top deciles cross PF 1.0

The honest finding: the entries that come during sharp counter-direction wicks (extreme `pct_1h_change` on the side opposite to the trade) are profitable on a fees-paid basis.

### `pct_1h_change` deciles

**Short side** — D9 (positive 1h change, i.e. shorting *into* an immediate-prior 1h rise) is the only profitable cell:

| Decile | Range | Trips | PF | Net P&L | Win rate | Bars held |
|---:|---|---:|---:|---:|---:|---:|
| 0 | [-99.8%, -1.49%] | 16,482 | 0.675 | -$7,828 | 52.0% | 6.1 |
| 1 | [-1.49%, -1.00%] | 16,481 | 0.524 | -$7,126 | 45.5% | 5.7 |
| 2 | [-1.00%, -0.74%] | 16,481 | 0.427 | -$7,795 | 41.8% | 5.4 |
| 3 | [-0.74%, -0.56%] | 16,481 | 0.365 | -$7,835 | 36.9% | 5.2 |
| 4 | [-0.56%, -0.41%] | 16,481 | 0.357 | -$7,362 | 35.6% | 5.0 |
| 5 | [-0.41%, -0.28%] | 16,481 | 0.353 | -$6,968 | 34.4% | 4.7 |
| 6 | [-0.28%, -0.15%] | 16,481 | 0.395 | -$5,851 | 34.1% | 4.4 |
| 7 | [-0.15%, -0.00%] | 16,481 | 0.477 | -$4,770 | 34.7% | 4.3 |
| 8 | [-0.00%, +0.19%] | 16,481 | 0.612 | -$3,340 | 34.3% | 4.2 |
| **9** | **[+0.19%, +995230%]** | **16,482** | **2.328** | **+$6,992** | **52.1%** | 3.9 |

**Long side** — mirror image, D0 (negative 1h change, i.e. longing *into* a 1h decline) is the profitable cell:

| Decile | Range | Trips | PF | Net P&L | Win rate | Bars held |
|---:|---|---:|---:|---:|---:|---:|
| **0** | **[-99.99%, -0.20%]** | **15,913** | **2.464** | **+$7,119** | **51.5%** | 3.9 |
| 1 | [-0.20%, +0.00%] | 15,912 | 0.617 | -$3,154 | 34.2% | 4.2 |
| 2 | [+0.00%, +0.15%] | 15,913 | 0.430 | -$5,111 | 31.9% | 4.4 |
| 3 | [+0.15%, +0.28%] | 15,912 | 0.387 | -$5,748 | 33.3% | 4.5 |
| 4 | [+0.28%, +0.41%] | 15,913 | 0.345 | -$6,700 | 33.4% | 4.7 |
| 5 | [+0.41%, +0.56%] | 15,912 | 0.350 | -$7,058 | 35.2% | 5.0 |
| 6 | [+0.56%, +0.75%] | 15,912 | 0.383 | -$7,025 | 37.3% | 5.1 |
| 7 | [+0.75%, +1.03%] | 15,913 | 0.448 | -$6,749 | 40.7% | 5.4 |
| 8 | [+1.03%, +1.55%] | 15,912 | 0.531 | -$6,553 | 45.1% | 5.7 |
| 9 | [+1.55%, +646%] | 15,913 | 0.847 | -$3,170 | 54.7% | 5.8 |

**Both top deciles cross PF 2.3+, with 51-52% win rate, and resolve in <4 bars**. ~16k trades each, $7k net per decile. This is the only honest profitability we've found in the system. The shape is symmetric and physically interpretable: when the system enters a trade and the immediate-prior 1h has moved sharply *against* the new position, the trade is being placed at a real counter-move/wick — not a continuation pullback. The decile boundaries are tiny (0.2%) but the PF separation is dramatic.

### `pct_72h_change` deciles

**Short side**: weakest in mid-buckets (D6 PF 0.39); strongest at the extremes but still all PF < 0.7. No profitable decile.

| Decile | Range | Trips | PF | Net P&L | Win rate |
|---:|---|---:|---:|---:|---:|
| 0 | [-99.99%, -7.04%] | 16,482 | 0.598 | -$8,303 | 47.0% |
| 1 | [-7.04%, -4.59%] | 16,481 | 0.570 | -$5,751 | 42.4% |
| 2 | [-4.59%, -3.15%] | 16,481 | 0.550 | -$5,284 | 40.8% |
| 3 | [-3.15%, -2.08%] | 16,481 | 0.556 | -$4,795 | 40.3% |
| 4 | [-2.08%, -1.13%] | 16,481 | 0.546 | -$4,770 | 39.7% |
| 5 | [-1.13%, -0.21%] | 16,481 | 0.546 | -$4,603 | 38.3% |
| 6 | [-0.21%, +0.52%] | 16,481 | 0.390 | -$6,508 | 28.9% |
| 7 | [+0.52%, +1.70%] | 16,481 | 0.580 | -$4,133 | 39.7% |
| 8 | [+1.70%, +3.49%] | 16,481 | 0.615 | -$3,852 | 40.9% |
| 9 | [+3.49%, +154%] | 16,482 | 0.681 | -$3,884 | 43.5% |

**Long side**: similar shape, D0 strongest at PF 0.79.

| Decile | Range | Trips | PF | Net P&L | Win rate |
|---:|---|---:|---:|---:|---:|
| 0 | [-99.99%, -5.29%] | 15,913 | 0.789 | -$2,329 | 45.1% |
| 9 | [+5.33%, +284%] | 15,913 | 0.715 | -$5,283 | 47.2% |

The 72h horizon doesn't isolate the edge — only the 1h does. The 1h captures the immediate-prior wick that the engine is buying/selling into; 72h is too coarse for this very-short-term reversion play.

### `vol_ratio_1h_over_72h` deciles

Clean monotone but never crosses PF 1.0. D9 (highest recent vol vs baseline) is the strongest cell on both sides — short PF 0.63, long PF 0.79 — but still loses.

| Decile | Range | Short PF | Short net | Long PF | Long net |
|---:|---|---:|---:|---:|---:|
| 0 | [0.02, 0.31] | 0.560 | -$4,071 | 0.609 | -$3,249 |
| 5 | [~0.71, ~0.85] | 0.553 | -$4,828 | 0.546 | -$4,664 |
| 9 | [≥1.88] | 0.630 | -$8,337 | 0.790 | -$4,100 |

### `bars_held` quintiles

The fast-resolving trades are where the edge sits:

| Side | Q | Held range | Trips | PF | Net P&L | Win rate |
|---:|---:|---|---:|---:|---:|---:|
| short | **Q0** | **[2, 4]** | **89,021** | **2.105** | **+$27,010** | 54.2% |
| short | Q1 | [5, 5] | 31,867 | 0.801 | -$2,773 | 37.4% |
| short | Q2 | [6, 6] | 12,028 | 0.268 | -$6,996 | 23.8% |
| short | Q3 | [7, 39] | 31,896 | 0.039 | -$69,125 | 9.8% |
| long | **Q0** | **[2, 4]** | **86,185** | **2.041** | **+$25,413** | 53.2% |
| long | Q1 | [5, 5] | 30,832 | 0.814 | -$2,567 | 36.5% |
| long | Q2 | [6, 6] | 11,725 | 0.301 | -$6,240 | 25.4% |
| long | Q3 | [7, 52] | 30,383 | 0.044 | -$60,755 | 10.2% |

**The bottom 50% of trades by hold duration is profitable.** Q0 (held 2-4 bars) on both sides crosses PF 2.0 with 53-54% win rate and ~$25k net per side — the trade hit the with-trend extreme target on the second or third bar after entry. Q3 (held 7+ bars) is catastrophic at PF 0.04 — the trailing target has ratcheted way down by then and the trade is closing for nearly a full notional loss. These two regimes are doing different things: Q0 is the genuine continuation-snapback trade firing as designed; Q3 is the trailing target slowly bleeding out as price drifts further and further from the original take-profit level.

## Read of the system

The trailing target enforces honesty. Under it the headline still loses, but the structure of the breakdowns shows real signal in two places:

1. **`pct_1h_change` extreme deciles**: when the entry bar is at the tail of an immediate-prior 1h move *opposite* to the trade direction, the trade is profitable (PF 2.3-2.5, 52% win, +$7k per decile).
2. **`bars_held` Q0**: trades that hit the target within 2-4 bars are profitable (PF 2.0+, 53% win, +$25k per quintile).

These two are likely the same trades — fast-resolving entries during sharp counter-direction wicks. About 15-25% of total trips, depending on which slice you pick.

Whether this is enough to build a tradable system is a separate question. The PF 2.3 cells have $7-25k net P&L over 16-89k trades on $1k notional — fees of $0.43/trade dominate the per-trade economics, and a stricter pre-trade filter would have to keep enough volume to amortize them. Worth a pre-trade gate sweep on `pct_1h_change` thresholds; that's the next step.
