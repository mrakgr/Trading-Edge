# Donchian-fade engine — v0 research notes

## Terminology

Two channel-break events fire entries; each can be paired with either side of trade:

- **Uptrend-breakdown**: a sustained 30-bar uptrend run (no lower-channel violations for ≥30 bars), then a bar's Low pierces the prior 3-bar Donchian low. Fade interpretation: the breakdown bar caps the uptrend.
- **Downtrend-breakup**: a sustained 30-bar downtrend run (no upper-channel violations for ≥30 bars), then a bar's High pierces the prior 3-bar Donchian high. Fade interpretation: the breakup bar caps the downtrend.

The engine has two configuration switches that determine what we do once a break event fires:

- `--reverse-direction`: which side we take on each break.
  - **v0 (fade the prior trend, `reverse=false`)**: uptrend-breakdown → SHORT, downtrend-breakup → LONG. We fade the multi-bar trend that was just broken.
  - **v1 (fade the break itself, `reverse=true`)**: uptrend-breakdown → LONG, downtrend-breakup → SHORT. We bet the just-broken move is itself a fakeout and price snaps back into the prior channel.
- `--cover-mode`: how we exit.
  - **OppositeChannel** (v0): ratcheted trailing stop on the just-broken side. Cover when price re-violates that side.
  - **EntryChannelTarget** (v1): trailing target on the with-trade side, snapshot at entry as the prior 3-bar Donchian extreme. Target ratchets toward entry on adverse moves so unrealized losers can't hide off the books. No stops.

Throughout this doc, when describing a trade we'll name both the trigger and the side: e.g. "uptrend-breakdown short" (v0) versus "uptrend-breakdown long" (v1). The trip CSV `side` column records the actual fill side; the trigger event is implicit in the side once the mode is fixed (v0 short ↔ uptrend breakdown; v1 long ↔ uptrend breakdown).

## Setup

Lance-style breakout-failure 1m engine on the crypto-perps universe:

- Maintain a rolling `DonchianBars` (= 3) high/low channel.
- On every bar, increment `bars_since_up_violation` if `bar.High <= prior_donchian_high`, else reset to 0. Symmetric for `bars_since_down_violation`.
- v0 entry: an uptrend-breakdown opens SHORT; a downtrend-breakup opens LONG.
- v0 stop: ratcheted Donchian-extreme trailing stop. Short stop = prior 3-bar high; ratchet down only (`min(stop, donHighMa.State)`). Long mirror.
- v0 cover: opposite-channel re-violation. Short covers when `bar.High > stop`; long covers when `bar.Low < stop`. Fill at `bar.Close`.

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

**Uptrend-breakdown shorts** — strongest at the extremes, dead-zone in the middle:

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

**Downtrend-breakup longs** — mirror image, extreme downtrend (D0) is best:

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

**Uptrend-breakdown shorts** — clean monotone, low → high PF:

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

**Downtrend-breakup longs** — clean reverse monotone, high → low:

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

**Uptrend-breakdown shorts** — clean monotone, low vol → high vol:

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

**Downtrend-breakup longs** — same clean monotone:

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

**Uptrend-breakdown shorts**:

| Quintile | Held range | Trips | PF | Net P&L | Win rate |
|---:|---|---:|---:|---:|---:|
| Q0 | [2, 4] | 34,997 | **0.013** | -$59,879 | **3.3%** |
| Q1 | [5, 6] | 28,662 | 0.040 | -$38,447 | 7.9% |
| Q2 | [7, 10] | 34,751 | 0.353 | -$21,071 | 26.5% |
| Q3 | [11, 17] | 27,901 | **1.259** | +$4,907 | **44.8%** |
| Q4 | [18, 2769] | 28,948 | **1.787** | +$15,662 | **45.3%** |

**Downtrend-breakup longs**:

| Quintile | Held range | Trips | PF | Net P&L | Win rate |
|---:|---|---:|---:|---:|---:|
| Q0 | [2, 4] | 35,885 | **0.011** | -$61,386 | **3.0%** |
| Q1 | [5, 6] | 30,612 | 0.045 | -$39,834 | 8.4% |
| Q2 | [7, 10] | 35,948 | 0.339 | -$22,388 | 26.8% |
| Q3 | [11, 16] | 26,997 | **1.342** | +$6,143 | **46.0%** |
| Q4 | [17, 2942] | 31,712 | **2.031** | +$22,564 | **46.5%** |

35% of trades exit in 2-4 bars at PF 0.01 / win rate 3%. Reading: the breaking bar's own wick is wide enough that the next bar's high (for shorts) or low (for longs) re-violates the just-set trailing stop, immediately covering. Trades that survive the first 10 bars are profitable in aggregate (PF 1.3-2.0).

The 3-bar Donchian cover is too tight. The cover Donchian needs to be wider than the entry Donchian, or there needs to be a min-hold-bars floor before the cover can fire.

## Read of v0

Both fade configurations lose at the headline (uptrend-breakdown shorts PF 0.43; downtrend-breakup longs PF 0.46). But the deciles show the structure of *where* the edge actually sits:

- `pct_1h_change` for uptrend-breakdown shorts: PF rises monotonically with the value. The least-bad shorts are placed when price is most up-stretched at the 1h horizon.
- `pct_1h_change` for downtrend-breakup longs: mirror image — PF falls monotonically; the least-bad longs are placed when price is most down-stretched.
- Both reactions favour high recent vol (`vol_ratio_1h_over_72h` D9).

The clean monotone in opposite directions per side, and the bars_held quintile pattern (Q4 PF 1.8-2.0 — long-held trades win), suggest the v0 cover (opposite-channel re-violation on a 3-bar Donchian) is the structural problem. The breaking bar's own wick is wide enough that the next bar often re-violates the just-set trailing stop. Q0 (held 2-4 bars) PF is 0.01 with 3% win rate.

Two natural follow-ups: (1) flip the trade direction so each break is faded by its *own* break rather than the prior trend, and (2) replace the opposite-channel cover with a take-profit target. v1 does both.

## Open questions

1. **Continuation engine.** Run the entry logic with sides flipped. The Donchian-break is the same; only the side picked is reversed. If the decile shape still holds, the engine works directionally. If it inverts, the signal was an artefact of the trip-set, not the directional hypothesis.
2. **Cover-Donchian width.** Independent of the directional question, no version of this engine works with a 3-bar cover. Q0+Q1 (held ≤6 bars) bleeds at PF 0.03. Either widen the cover Donchian (e.g. 10-30 bars) or add a `MinHoldBars` floor.
3. **Entry-trend-length.** `bars_since_violation` quintile breakdowns showed shorter trend runs (D0-D3) outperform longer ones at default thresholds — opposite to the original "longer run = stronger fade" intuition. Worth a sweep over `--min-trend-bars`.

# v1 — reverse direction with trailing target

## Setup

Same entry detection as v0 (3-bar Donchian, 30-bar trend qualifier, break trigger). Two flags differ:

- `--reverse-direction true`: uptrend-breakdown opens **LONG** (was Short), downtrend-breakup opens **SHORT** (was Long). The trade fades the *break* itself rather than the prior trend — it bets the just-broken move is a fakeout and price snaps back into the channel.
- `--cover-mode entry-channel-target`: cover at the prior 3-bar Donchian extreme on the *with-trade* side, snapshot at entry as the take-profit target. **Trailing**, not fixed: as the trade moves adverse, the target ratchets toward entry (long target = current `donHighMa.State` if it's lower than the last cached target; short target = `donLowMa.State` if higher). No stops.

Trade-direction summary:

| Trigger | v0 side | v1 side | What v1 fades |
|---|---|---|---|
| Uptrend-breakdown | Short | **Long** | the breakdown (price snaps back up into the channel) |
| Downtrend-breakup | Long | **Short** | the breakup (price snaps back down into the channel) |

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

| Trigger | Side | Trips | PF | Net P&L | Win rate |
|---|---|---:|---:|---:|---:|
| Uptrend-breakdown | Long | 159,125 | 0.601 | -$44,148 | 39.7% |
| Downtrend-breakup | Short | 164,812 | 0.567 | -$51,884 | 40.1% |

The aggregate still loses to fees on average. Trades resolve fast (median 4 bars) and most close for tiny losses as the trailing target pursues adverse drift. **But the headline aggregate is misleading** — the deciles tell a different story.

## Decile breakdowns — top deciles cross PF 1.0

The honest finding: the entries placed at the deepest `pct_1h_change` extremes on the side that fades the break are profitable on a fees-paid basis.

### `pct_1h_change` deciles

**Downtrend-breakup shorts** (`side=short`) — only D9 profits. These shorts are taken right when bar.High pierces the upper channel after a 30-bar downtrend; deeper-positive `pct_1h_change` means that bar's close was further above the 1h MA — i.e. a sharper upward wick within the underlying downtrend, which is exactly when the short fade pays off:

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

**Uptrend-breakdown longs** (`side=long`) — mirror image, only D0 profits. These longs are taken when bar.Low pierces the lower channel after a 30-bar uptrend; deeper-negative `pct_1h_change` means that bar closed further below the 1h MA — a sharper downward wick within the underlying uptrend, where the long fade pays:

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

**Both extreme deciles cross PF 2.3+, with 51-52% win rate, and resolve in <4 bars**. ~16k trades each, $7k net per decile. The shape is symmetric and physically interpretable: the deeper the wick that pierced the channel (in the direction *opposite* to the trade), the better the snapback works. Tiny decile boundaries (0.2%), dramatic PF separation.

### `pct_72h_change` deciles

**Downtrend-breakup shorts**: weakest in mid-buckets (D6 PF 0.39); strongest at the extremes but still all PF < 0.7. No profitable decile.

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

**Uptrend-breakdown longs**: similar shape, D0 strongest at PF 0.79.

| Decile | Range | Trips | PF | Net P&L | Win rate |
|---:|---|---:|---:|---:|---:|
| 0 | [-99.99%, -5.29%] | 15,913 | 0.789 | -$2,329 | 45.1% |
| 9 | [+5.33%, +284%] | 15,913 | 0.715 | -$5,283 | 47.2% |

The 72h horizon doesn't isolate the edge — only the 1h does. The 1h captures the immediate-prior wick that the engine is buying/selling into; 72h is too coarse for this very-short-term reversion play.

### `vol_ratio_1h_over_72h` deciles

Clean monotone but never crosses PF 1.0. D9 (highest recent vol vs baseline) is the strongest cell on both triggers — downtrend-breakup short PF 0.63, uptrend-breakdown long PF 0.79 — but still loses.

| Decile | Range | Dn-breakup short PF | Net | Up-breakdown long PF | Net |
|---:|---|---:|---:|---:|---:|
| 0 | [0.02, 0.31] | 0.560 | -$4,071 | 0.609 | -$3,249 |
| 5 | [~0.71, ~0.85] | 0.553 | -$4,828 | 0.546 | -$4,664 |
| 9 | [≥1.88] | 0.630 | -$8,337 | 0.790 | -$4,100 |

### `bars_held` quintiles

The fast-resolving trades are where the edge sits:

| Trigger | Side | Q | Held range | Trips | PF | Net P&L | Win rate |
|---|---|---:|---|---:|---:|---:|---:|
| Downtrend-breakup | Short | **Q0** | **[2, 4]** | **89,021** | **2.105** | **+$27,010** | 54.2% |
| Downtrend-breakup | Short | Q1 | [5, 5] | 31,867 | 0.801 | -$2,773 | 37.4% |
| Downtrend-breakup | Short | Q2 | [6, 6] | 12,028 | 0.268 | -$6,996 | 23.8% |
| Downtrend-breakup | Short | Q3 | [7, 39] | 31,896 | 0.039 | -$69,125 | 9.8% |
| Uptrend-breakdown | Long | **Q0** | **[2, 4]** | **86,185** | **2.041** | **+$25,413** | 53.2% |
| Uptrend-breakdown | Long | Q1 | [5, 5] | 30,832 | 0.814 | -$2,567 | 36.5% |
| Uptrend-breakdown | Long | Q2 | [6, 6] | 11,725 | 0.301 | -$6,240 | 25.4% |
| Uptrend-breakdown | Long | Q3 | [7, 52] | 30,383 | 0.044 | -$60,755 | 10.2% |

**The bottom 50% of trades by hold duration is profitable.** Q0 (held 2-4 bars) on both sides crosses PF 2.0 with 53-54% win rate and ~$25k net per side — the trade hit the with-trend extreme target on the second or third bar after entry. Q3 (held 7+ bars) is catastrophic at PF 0.04 — the trailing target has ratcheted way down by then and the trade is closing for nearly a full notional loss. These two regimes are doing different things: Q0 is the genuine continuation-snapback trade firing as designed; Q3 is the trailing target slowly bleeding out as price drifts further and further from the original take-profit level.

## Read of the system

The trailing target enforces honesty. Under it the headline still loses, but the structure of the breakdowns shows real signal in two places:

1. **`pct_1h_change` extreme deciles**: when the entry bar is at the tail of an immediate-prior 1h move *opposite* to the trade direction, the trade is profitable (PF 2.3-2.5, 52% win, +$7k per decile).
2. **`bars_held` Q0**: trades that hit the target within 2-4 bars are profitable (PF 2.0+, 53% win, +$25k per quintile).

These two are likely the same trades — fast-resolving entries during sharp counter-direction wicks. About 15-25% of total trips, depending on which slice you pick.

Whether this is enough to build a tradable system is a separate question. The PF 2.3 cells have $7-25k net P&L over 16-89k trades on $1k notional — fees of $0.43/trade dominate the per-trade economics, and a stricter pre-trade filter would have to keep enough volume to amortize them. Worth a pre-trade gate sweep on `pct_1h_change` thresholds; that's the next step.

# Sub-decile breakdown of fade-mode (v0) top deciles

The v0 fade-mode breakdowns showed monotone improvement of PF as `pct_1h_change` moved toward the extremes (D9 for shorts, D0 for longs). Both top deciles still lost (short D9 PF 0.65, long D0 PF 0.80), but the trajectory suggested the *very* top of the distribution might cross PF 1.0. Slicing those top deciles into 10 sub-deciles each:

## v0 uptrend-breakdown shorts: D9 of pct_1h_change (price most stretched up at 1h horizon)

D9 spans [+1.57%, +84.1%]. n = 15,526 trips. Sub-deciles:

| Sub | Range | Trips | PF | Net P&L | Win rate | Bars |
|---:|---|---:|---:|---:|---:|---:|
| D9.0 | [+1.57%, +1.66%] | 1,553 | 0.613 | -$728 | 30.5% | 7.6 |
| D9.1 | [+1.66%, +1.77%] | 1,553 | 0.556 | -$878 | 30.3% | 7.0 |
| D9.2 | [+1.77%, +1.89%] | 1,552 | 0.527 | -$992 | 29.8% | 7.4 |
| D9.3 | [+1.89%, +2.04%] | 1,553 | 0.510 | -$1,035 | 30.5% | 6.9 |
| D9.4 | [+2.04%, +2.23%] | 1,552 | 0.621 | -$803 | 30.4% | 7.3 |
| D9.5 | [+2.23%, +2.46%] | 1,553 | 0.625 | -$883 | 30.2% | 6.8 |
| D9.6 | [+2.46%, +2.78%] | 1,552 | 0.627 | -$919 | 30.8% | 6.6 |
| D9.7 | [+2.78%, +3.32%] | 1,553 | 0.583 | -$1,122 | 29.9% | 6.8 |
| D9.8 | [+3.32%, +4.46%] | 1,552 | 0.549 | -$1,474 | 30.9% | 6.2 |
| **D9.9** | **[+4.46%, +84.1%]** | **1,553** | **0.929** | **-$360** | **34.4%** | 6.7 |

Uptrend-breakdown shorts have nothing tradable here. D9.9 is the strongest sub-decile but still PF 0.93 — converges toward break-even but doesn't cross it. The PF curve is flat-to-noisy across the rest of D9.

## v0 downtrend-breakup longs: D0 of pct_1h_change (price most stretched down at 1h horizon)

D0 spans [-99.8%, -1.50%]. n = 16,116 trips. Sub-deciles:

| Sub | Range | Trips | PF | Net P&L | Win rate | Bars |
|---:|---|---:|---:|---:|---:|---:|
| **D0.0** | **[-99.8%, -3.86%]** | **1,612** | **1.536** | **+$2,178** | **40.6%** | 7.9 |
| D0.1 | [-3.86%, -2.97%] | 1,612 | 0.979 | -$56 | 39.8% | 7.9 |
| D0.2 | [-2.97%, -2.52%] | 1,611 | 0.671 | -$901 | 33.8% | 7.5 |
| D0.3 | [-2.52%, -2.25%] | 1,612 | 0.619 | -$1,012 | 32.0% | 7.4 |
| D0.4 | [-2.25%, -2.05%] | 1,611 | 0.588 | -$990 | 30.9% | 7.1 |
| D0.5 | [-2.05%, -1.89%] | 1,612 | 0.547 | -$1,069 | 29.9% | 7.4 |
| D0.6 | [-1.89%, -1.77%] | 1,611 | 0.584 | -$885 | 32.5% | 7.2 |
| D0.7 | [-1.77%, -1.67%] | 1,612 | 0.713 | -$637 | 30.5% | 7.5 |
| D0.8 | [-1.67%, -1.58%] | 1,611 | 0.572 | -$905 | 30.4% | 7.6 |
| D0.9 | [-1.58%, -1.50%] | 1,612 | 0.625 | -$709 | 32.7% | 7.6 |

**The only profitable cell in the entire v0 trip set is sub-decile D0.0** — the deepest 1% of downtrend-breakup long entries (where price is ≥ 3.86% below the 1h MA when the breakup bar fires). PF 1.54, +$2,178 over 1,612 trades, 40.6% win rate. D0.1 (next 1%) sits at PF 0.98, essentially break-even. Everything else is structurally underwater.

The asymmetry is consistent with the broader workstream finding: long mean-reversion has been the more reliable book on this universe (LongFadeMA PF 3.50 vs ShortFadeMA PF 1.98 with dual-CVD overlay). The downtrend-breakup-long trigger fires at exactly the right moment for mean-reversion: a sustained 30-bar downtrend just put in a fresh 3-bar high, the close is 4-10% below the 1h MA, and the system is buying the snapback.

## Sub-sub-deciles — v0 downtrend-breakup long D0.0

D0.0 itself is PF 1.54 over 1,612 trades. Slicing it into 10 sub-sub-deciles:

| Sub-sub | Range (pct_1h_change) | Trips | PF | Net P&L | Win rate | Bars |
|---:|---|---:|---:|---:|---:|---:|
| **D0.0.0** | **[-99.8%, -11.10%]** | **162** | **3.530** | **+$1,712** | **52.5%** | 8.7 |
| **D0.0.1** | **[-11.10%, -8.01%]** | **161** | **3.159** | **+$935** | **53.4%** | 9.1 |
| D0.0.2 | [-8.01%, -6.65%] | 161 | 1.198 | +$81 | 43.5% | 8.3 |
| D0.0.3 | [-6.64%, -5.83%] | 161 | 0.598 | -$202 | 29.2% | 6.8 |
| D0.0.4 | [-5.82%, -5.21%] | 161 | 0.499 | -$222 | 31.1% | 6.8 |
| D0.0.5 | [-5.21%, -4.79%] | 161 | 0.757 | -$85 | 34.2% | 7.6 |
| D0.0.6 | [-4.78%, -4.51%] | 161 | 0.845 | -$49 | 33.5% | 7.6 |
| D0.0.7 | [-4.51%, -4.22%] | 161 | 0.887 | -$40 | 39.1% | 7.5 |
| D0.0.8 | [-4.21%, -4.02%] | 161 | 1.299 | +$74 | 44.7% | 8.8 |
| D0.0.9 | [-4.02%, -3.86%] | 162 | 0.919 | -$27 | 45.1% | 7.5 |

**The edge concentrates in the deepest two cells: D0.0.0 and D0.0.1, both PF >3.0 with 52-53% win rate.** Combined: 323 trips, PF ~3.30, +$2,650, 53% win rate — and these are the deepest 0.2% of downtrend-breakup long entries (`pct_1h_change ≤ -8.01%`, i.e. price more than 8% below the 1h MA when the breakup bar fires).

D0.0.2 (-8% to -6.65%) is barely positive (PF 1.20). Cells D0.0.3 through D0.0.9 are mostly underwater (PF 0.5-0.9), though D0.0.8 oddly pops back to PF 1.30. That's not enough to call a regime — looks like noise inside an otherwise losing band.

The shape is **bimodal-ish but dominated by a single deep regime**: extreme capitulation declines (≥ 8% below the 1h MA at entry) mean-revert reliably; less extreme declines bleed.

## Sub-sub-deciles — v0 uptrend-breakdown short D9.9

D9.9 itself was PF 0.93 (already barely-not-profitable). Slicing it the same way:

| Sub-sub | Range (pct_1h_change) | Trips | PF | Net P&L | Win rate | Bars |
|---:|---|---:|---:|---:|---:|---:|
| D9.9.0 | [+4.46%, +4.66%] | 156 | 0.556 | -$149 | 30.8% | 6.5 |
| D9.9.1 | [+4.66%, +4.92%] | 155 | 0.841 | -$61 | 34.8% | 6.5 |
| D9.9.2 | [+4.93%, +5.24%] | 155 | 0.523 | -$208 | 27.1% | 6.4 |
| D9.9.3 | [+5.24%, +5.60%] | 155 | 0.633 | -$156 | 30.3% | 7.0 |
| D9.9.4 | [+5.60%, +6.04%] | 156 | 0.864 | -$58 | 35.9% | 6.6 |
| **D9.9.5** | **[+6.04%, +6.71%]** | **155** | **1.116** | **+$48** | **36.8%** | 7.0 |
| D9.9.6 | [+6.72%, +7.50%] | 155 | 0.866 | -$79 | 34.8% | 6.7 |
| D9.9.7 | [+7.51%, +9.11%] | 155 | 0.893 | -$58 | 34.2% | 6.8 |
| **D9.9.8** | **[+9.11%, +12.42%]** | **155** | **1.290** | **+$180** | **36.8%** | 6.7 |
| **D9.9.9** | **[+12.43%, +84.1%]** | **156** | **1.201** | **+$181** | **42.3%** | 6.8 |

**Uptrend-breakdown shorts do have an edge in the deepest cells, but it's much smaller than the downtrend-breakup longs.** D9.9.8 + D9.9.9 combined: 311 trades, PF ~1.24, +$361, 39.5% win rate. That's the deepest 0.2% of short entries (`pct_1h_change ≥ +9.11%`, i.e. price more than 9% above the 1h MA when the breakdown bar fires).

D9.9.5 is a one-cell standout (PF 1.12, +$48) bracketed by losing cells D9.9.4 and D9.9.6 — looks like noise within an otherwise losing band, not a separate regime.

## Combined extreme-tail summary

The deepest 0.2% on each side:

| Trigger | Side | Threshold (`pct_1h_change`) | Trips | PF | Net P&L | Win rate | Per-trade |
|---|---|---|---:|---:|---:|---:|---:|
| **Downtrend-breakup** | **Long** | **≤ -8.01%** | **323** | **3.30** | **+$2,650** | **53.0%** | **+$8.20** |
| Uptrend-breakdown | Short | ≥ +9.11% | 311 | 1.24 | +$361 | 39.5% | +$1.16 |

Both extreme tails are profitable post-fees. The asymmetry is enormous: per-trade, downtrend-breakup longs deliver ~7× the edge of uptrend-breakdown shorts. This re-confirms the workstream's broader pattern — long mean-reversion is the more reliable book on this universe. Capitulation breakups reliably mean-revert when they're already deep below the 1h MA; blow-off breakdowns from extreme above-MA stretches mean-revert weakly.

The downtrend-breakup long D0.0.0+D0.0.1 (PF 3.30, 53% win rate, 8%+ below the 1h MA) is the strongest pre-trade-gateable signal we've found in the Donchian engine on either trigger, in either v0 or v1. It's a small slice — 323 trades over 2 years across 643 symbols, ~0.5 trades/day across the whole universe — but per-trade economics of $8.20 on $1k notional with $0.80 fees is a clean 0.8% net per round-trip.

# Sub-decile breakdown of v1 (reverse + trailing-target) top deciles

The v1 deciles showed a much cleaner monotone in `pct_1h_change` than v0 did, with both extreme deciles cleanly profitable:

- **Uptrend-breakdown longs** (`side=long`, fading the breakdown): D0 PF 2.46 / +$7,119 / 51.5% win
- **Downtrend-breakup shorts** (`side=short`, fading the breakup): D9 PF 2.33 / +$6,992 / 52.1% win

Reminder of the trigger ↔ side mapping in v1: an uptrend-breakdown opens LONG (we fade the breakdown back up into the channel); a downtrend-breakup opens SHORT (we fade the breakup back down into the channel). The `pct_1h_change` at entry measures how stretched the closing price was relative to the 1h MA at the moment the break fired:

- For uptrend-breakdown longs, deeper-negative `pct_1h_change` = the breaking-down bar's close was further below the 1h MA = a sharper downward wick within an underlying uptrend = where the snapback-up plays out best.
- For downtrend-breakup shorts, deeper-positive `pct_1h_change` = the breaking-up bar's close was further above the 1h MA = a sharper upward wick within an underlying downtrend = where the snapback-down plays out best.

Slicing each extreme decile into 10 sub-deciles:

## v1 uptrend-breakdown longs: D0 of pct_1h_change

D0 spans [-99.99%, -0.20%]. Aggregate PF 2.46. Sub-deciles:

| Sub | Range (`pct_1h_change`) | Trips | PF | Net P&L | Win rate | Bars |
|---:|---|---:|---:|---:|---:|---:|
| D0.0 | [-99.99%, -0.98%] | 1,592 | 4.096 | +$1,493 | 57.0% | 4.1 |
| **D0.1** | **[-0.98%, -0.74%]** | **1,591** | **5.868** | **+$1,283** | **56.5%** | 3.9 |
| D0.2 | [-0.74%, -0.60%] | 1,591 | 3.691 | +$973 | 53.0% | 3.9 |
| D0.3 | [-0.60%, -0.50%] | 1,591 | 2.826 | +$821 | 53.0% | 3.8 |
| D0.4 | [-0.50%, -0.42%] | 1,592 | 2.405 | +$688 | 52.5% | 3.8 |
| D0.5 | [-0.42%, -0.36%] | 1,591 | 2.153 | +$568 | 50.5% | 3.8 |
| D0.6 | [-0.36%, -0.32%] | 1,591 | 1.824 | +$456 | 48.8% | 3.8 |
| D0.7 | [-0.32%, -0.27%] | 1,591 | 1.614 | +$354 | 49.6% | 3.9 |
| D0.8 | [-0.27%, -0.23%] | 1,591 | 1.489 | +$281 | 48.1% | 3.9 |
| D0.9 | [-0.23%, -0.20%] | 1,592 | 1.326 | +$202 | 45.5% | 3.9 |

**Every sub-decile is profitable.** Peak is D0.1 at PF 5.87, in the band [-0.98%, -0.74%]. D0.0 (the deepest 10% of D0 — including the absurd >-10% outliers) sits at PF 4.10 — strong, but the band just above it is stronger. After D0.1 there's a clean monotone decay from PF 5.87 → 1.33 as the entry wick gets shallower.

## v1 downtrend-breakup shorts: D9 of pct_1h_change

D9 spans [+0.19%, +9952%]. Aggregate PF 2.33. Sub-deciles:

| Sub | Range (`pct_1h_change`) | Trips | PF | Net P&L | Win rate | Bars |
|---:|---|---:|---:|---:|---:|---:|
| D9.0 | [+0.19%, +0.22%] | 1,649 | 1.418 | +$259 | 48.8% | 3.9 |
| D9.1 | [+0.22%, +0.26%] | 1,648 | 1.396 | +$247 | 47.3% | 3.9 |
| D9.2 | [+0.26%, +0.30%] | 1,648 | 1.361 | +$245 | 46.2% | 3.9 |
| D9.3 | [+0.30%, +0.35%] | 1,648 | 1.783 | +$442 | 49.6% | 3.9 |
| D9.4 | [+0.35%, +0.41%] | 1,648 | 2.009 | +$542 | 50.0% | 3.9 |
| D9.5 | [+0.41%, +0.48%] | 1,648 | 2.456 | +$707 | 52.9% | 3.9 |
| D9.6 | [+0.48%, +0.57%] | 1,648 | 2.720 | +$832 | 53.8% | 3.8 |
| D9.7 | [+0.57%, +0.70%] | 1,648 | 3.312 | +$983 | 55.3% | 3.9 |
| **D9.8** | **[+0.70%, +0.93%]** | **1,648** | **5.541** | **+$1,349** | **59.6%** | 3.9 |
| D9.9 | [+0.93%, +9952%] | 1,649 | 3.517 | +$1,386 | 57.6% | 4.2 |

**Every sub-decile is profitable here too.** Peak is D9.8 at PF 5.54 in the band [+0.70%, +0.93%]. D9.9 (the deepest 10%, including extreme outliers up to +995230% — almost certainly post-redenom dust trades that the `--max-bar-price-ratio 3` filter didn't catch) drops back to PF 3.52. Monotone increase from PF 1.42 (D9.0) up to D9.8, then a slight pullback at the very tail.

## Combined v1 picture

| Trigger | Side | Best sub-decile | Range (`pct_1h_change`) | Trips | PF | Net P&L | Win rate |
|---|---|---|---|---:|---:|---:|---:|
| **Uptrend-breakdown** | **Long** | **D0.1** | **[-0.98%, -0.74%]** | **1,591** | **5.87** | **+$1,283** | **56.5%** |
| Uptrend-breakdown | Long | D0.0 | [-99.99%, -0.98%] | 1,592 | 4.10 | +$1,493 | 57.0% |
| **Downtrend-breakup** | **Short** | **D9.8** | **[+0.70%, +0.93%]** | **1,648** | **5.54** | **+$1,349** | **59.6%** |
| Downtrend-breakup | Short | D9.9 | [+0.93%, +9952%] | 1,649 | 3.52 | +$1,386 | 57.6% |

**The v1 system has signal across the entire extreme decile on both triggers** — every sub-decile is profitable, with peaks in the second-deepest band rather than the deepest. Per-trade economics: D0.1 uptrend-breakdown long delivers ~$0.81/trade and D9.8 downtrend-breakup short ~$0.82/trade — only ~1× fees, so per-trade net is around 0.4% on $1k notional after the $0.43-0.80 round-trip cost.

The story is qualitatively different from v0: v0 isolated edge in the deepest 0.2% on one trigger only (downtrend-breakup long D0.0.0+1); v1 has profitable signal across the top 10% on both triggers. Smaller per-trade economics in v1 (~$0.81 vs $8.20 in v0's flagship cell), but **ten times as many trades** in the profitable cells. Total profitable-cell P&L: v1 uptrend-breakdown longs D0 alone deliver +$7,119 across 15,913 trades, vs +$2,650 across 323 trades for v0's flagship cell. Higher gross P&L, smaller per-trade margin — a different operating regime, but a real one.

# v2 — pure trend-following (TrendOnly entry, OppositeChannel cover)

## Setup

Same Donchian channel, same 30-bar trend qualifier, same OppositeChannel ratcheted-Donchian trailing stop as v0. The only change is the entry trigger:

- `--entry-mode trend-only`: fire on the first bar where the 30-bar qualifier is satisfied (no break required). With-trend direction: a 30-bar uptrend qualifier opens **LONG**, a 30-bar downtrend qualifier opens **SHORT**. Re-arms after each violation that resets the corresponding counter.
- `--cover-mode opposite-channel`: same trailing stop as v0 (long stop = `donLowMa.State` ratcheted up, short stop = `donHighMa.State` ratcheted down). No fixed take-profit.
- `--reverse-direction false`: ignored in TrendOnly mode (which always goes with-trend).

Trade-direction summary:

| Trigger | Side | Thesis |
|---|---|---|
| Uptrend-qualifier (no down-violation for ≥30 bars) | Long | Ride the established uptrend |
| Downtrend-qualifier (no up-violation for ≥30 bars) | Short | Ride the established downtrend |

## Aggregate

| Metric | v0 fade | v1 reverse-target | v2 trend-only |
|---|---:|---:|---:|
| Total trips | 316,413 | 323,937 | **269,360** |
| Net P&L | -$193,728 | -$96,032 | **-$158,145** |
| Aggregate PF | 0.446 | 0.584 | **0.443** |
| Win rate | 24.9% | 39.9% | **22.2%** |
| Median bars held | — | 4 | **7** |
| Mean bars held | 14.0 | 4.9 | **13.4** |

| Trigger | Side | Trips | PF | Net P&L | Win rate | Mean bars |
|---|---|---:|---:|---:|---:|---:|
| Uptrend-qualifier | Long | 131,777 | 0.449 | -$76,477 | 22.2% | 13.4 |
| Downtrend-qualifier | Short | 137,582 | 0.438 | -$81,668 | 22.2% | 13.5 |

**v2 trend-only has the same headline as v0 fade** (PF 0.44 vs 0.45) despite opposite entry theses. Both rely on the OppositeChannel ratcheted-Donchian trailing stop, and both bleed at the same rate. The Q0 disaster reproduces:

| Side | Q | Held range | Trips | PF | Net | Win |
|---|---|---|---:|---:|---:|---:|
| Long | **Q0** | **[2, 3]** | 33,010 | **0.018** | -$45,185 | **4.16%** |
| Long | Q1 | [4, 5] | 23,799 | 0.019 | -$34,058 | 4.18% |
| Long | Q2 | [6, 8] | 22,962 | 0.209 | -$18,514 | 19.7% |
| Long | Q3 | [9, 15] | 25,789 | **1.120** | +$2,176 | 40.5% |
| Long | Q4 | [16, 2942] | 26,217 | **2.153** | +$19,104 | 45.5% |
| Short | **Q0** | **[2, 3]** | 34,318 | **0.015** | -$47,314 | **3.65%** |
| Short | Q1 | [4, 5] | 24,415 | 0.014 | -$36,123 | 3.87% |
| Short | Q2 | [6, 8] | 24,041 | 0.212 | -$19,433 | 20.2% |
| Short | Q3 | [9, 16] | 29,369 | **1.166** | +$3,340 | 41.4% |
| Short | Q4 | [17, 2769] | 25,439 | **2.126** | +$17,862 | 44.7% |

Q0 (held 2-3 bars, ~25% of trades on each side) bleeds at PF 0.02 with 4% win rate. The breaking bar's own follow-through wick re-violates the just-set trailing stop next bar. Q4 (held 17+ bars) reaches PF 2.1 — the trades that survive the noise window run profitably.

**This confirms the cover is the structural problem, not the entry direction.** v0 fade and v2 trend-only have opposite entry theses but the same headline PF and the same Q0 disaster.

## Decile breakdowns

`pct_1h_change` deciles — flat-U on both sides, no profitable decile:

**Uptrend-qualifier longs**:

| Decile | Range | Trips | PF | Net | Win | Bars |
|---:|---|---:|---:|---:|---:|---:|
| 0 | [-99.7%, +0.01%] | 13,178 | 0.643 | -$3,379 | 23.9% | 39.2 |
| 1 | [+0.01%, +0.18%] | 13,178 | 0.360 | -$7,611 | 19.9% | 16.1 |
| 2 | [+0.18%, +0.30%] | 13,177 | 0.321 | -$8,532 | 19.7% | 12.8 |
| 3 | [+0.30%, +0.43%] | 13,178 | 0.313 | -$8,965 | 19.4% | 11.8 |
| 4 | [+0.43%, +0.56%] | 13,178 | 0.354 | -$8,482 | 20.3% | 11.1 |
| 5 | [+0.56%, +0.71%] | 13,177 | 0.359 | -$8,465 | 20.6% | 10.3 |
| 6 | [+0.71%, +0.91%] | 13,178 | 0.394 | -$8,156 | 21.7% | 9.6 |
| 7 | [+0.91%, +1.20%] | 13,177 | 0.454 | -$7,701 | 23.3% | 8.8 |
| 8 | [+1.20%, +1.78%] | 13,178 | 0.475 | -$8,151 | 24.5% | 7.6 |
| 9 | [+1.78%, +696%] | 13,178 | 0.686 | -$7,036 | 28.7% | 6.4 |

**Downtrend-qualifier shorts** (mirror image):

| Decile | Range | Trips | PF | Net | Win | Bars |
|---:|---|---:|---:|---:|---:|---:|
| 0 | [-99.9%, -1.70%] | 13,759 | 0.608 | -$9,626 | 27.3% | 6.5 |
| 1 | [-1.70%, -1.18%] | 13,758 | 0.450 | -$9,076 | 23.8% | 7.9 |
| 2-7 | mid-buckets | ~13.7k each | 0.35-0.43 | -$8,400 to -$8,900 | 20-23% | 8-13 |
| 8 | [-0.17%, -0.01%] | 13,758 | 0.383 | -$7,435 | 20.4% | 16.0 |
| 9 | [-0.01%, +4.10%] | 13,759 | 0.624 | -$3,713 | 24.0% | 39.8 |

Both sides have a flat-U: D0 and D9 outperform mid-buckets but no decile crosses PF 1.0. `pct_72h_change` and `vol_ratio_1h_over_72h` show similar shapes — best at extremes, never profitable.

## Sub-decile breakdown of D0 and D9 per side

The D0/D9 outperformance was strong enough to drill down. Three sub-deciles cross PF 1.0:

### Uptrend-qualifier longs, D0 of pct_1h_change (deepest 1h decline at entry — buying *into* a 1h down-move while the 30-bar trend qualifier still holds up)

D0 spans [-99.7%, +0.0001%]. Aggregate PF 0.64. Sub-deciles:

| Sub | Range (`pct_1h_change`) | Trips | PF | Net P&L | Win rate | Bars |
|---:|---|---:|---:|---:|---:|---:|
| **L-D0.0** | **[-99.7%, -0.41%]** | **1,318** | **1.43** | **+$342** | **34.6%** | 39.3 |
| **L-D0.1** | **[-0.41%, -0.26%]** | **1,318** | **1.08** | **+$65** | **32.2%** | 30.2 |
| L-D0.2 | [-0.26%, -0.18%] | 1,318 | 0.85 | -$134 | 29.5% | 25.9 |
| L-D0.3 | [-0.18%, -0.13%] | 1,317 | 0.76 | -$228 | 27.9% | 23.6 |
| L-D0.4 | [-0.13%, -0.08%] | 1,318 | 0.74 | -$233 | 29.1% | 21.7 |
| L-D0.5 | [-0.08%, -0.05%] | 1,318 | 0.62 | -$382 | 26.5% | 20.5 |
| L-D0.6 | [-0.05%, -0.02%] | 1,317 | 0.56 | -$452 | 25.8% | 20.0 |
| L-D0.7 | [-0.02%, ~0] | 1,318 | 0.38 | -$620 | 17.7% | 43.4 |
| L-D0.8 | [~0, ~0] | 1,318 | **0.12** | -$883 | **6.0%** | **110.2** |
| L-D0.9 | [~0, +0.0001%] | 1,318 | 0.18 | -$853 | 9.9% | 57.1 |

L-D0.8/9 are the degenerate near-zero `pct_1h_change` cluster — entries where price sat almost exactly at the 1h MA. PF 0.12-0.18, 6-10% win rate, 57-110 mean bars held. These trades enter into low-volatility consolidation and bleed slowly to the trailing stop.

### Uptrend-qualifier longs, D9 of pct_1h_change (sharpest 1h rise at entry — buying *with* a 1h up-stretch)

D9 spans [+1.78%, +696%]. Aggregate PF 0.69. Sub-deciles:

| Sub | Range (`pct_1h_change`) | Trips | PF | Net P&L | Win rate | Bars |
|---:|---|---:|---:|---:|---:|---:|
| L-D9.0 | [+1.78%, +1.88%] | 1,318 | 0.53 | -$794 | 25.0% | 6.8 |
| L-D9.1 | [+1.88%, +2.00%] | 1,318 | 0.54 | -$846 | 25.6% | 6.3 |
| L-D9.2 | [+2.00%, +2.14%] | 1,318 | 0.55 | -$792 | 27.2% | 6.5 |
| L-D9.3 | [+2.14%, +2.30%] | 1,317 | 0.66 | -$598 | 28.6% | 6.7 |
| L-D9.4 | [+2.30%, +2.51%] | 1,318 | 0.62 | -$708 | 27.7% | 6.5 |
| L-D9.5 | [+2.51%, +2.78%] | 1,318 | 0.74 | -$502 | 28.8% | 6.5 |
| L-D9.6 | [+2.78%, +3.17%] | 1,317 | 0.69 | -$604 | 28.0% | 6.2 |
| L-D9.7 | [+3.17%, +3.76%] | 1,318 | 0.67 | -$747 | 30.3% | 6.3 |
| L-D9.8 | [+3.76%, +5.02%] | 1,318 | 0.75 | -$645 | 32.4% | 6.2 |
| L-D9.9 | [+5.02%, +696%] | 1,318 | 0.83 | -$798 | 33.4% | 6.3 |

Mild monotone improvement with deeper stretches — but the entire D9 stays sub-1.0. "Buy when stretched up" doesn't beat fees in any band. Mean held drops to 6 bars (vs 39 in L-D0.0) — these trades resolve quickly through the trailing stop because the breaking-up wick gives plenty of room for the next bar to retrace and trigger the cover.

### Downtrend-qualifier shorts, D0 of pct_1h_change (deepest 1h decline at entry — shorting *with* a 1h down-stretch)

D0 spans [-99.9%, -1.70%]. Aggregate PF 0.61. Mirror of L-D9: monotone PF degradation as you go deeper, every sub-decile loses, mean held ~6-7 bars throughout. No profitable cell.

### Downtrend-qualifier shorts, D9 of pct_1h_change (deepest 1h rise at entry — shorting *into* a 1h up-stretch while the 30-bar downtrend qualifier still holds)

D9 spans [-0.0001%, +4.10%]. Aggregate PF 0.62. Sub-deciles:

| Sub | Range (`pct_1h_change`) | Trips | PF | Net P&L | Win rate | Bars |
|---:|---|---:|---:|---:|---:|---:|
| S-D9.0 | [~0, ~0] | 1,376 | 0.22 | -$833 | **10.6%** | 64.5 |
| S-D9.1 | [~0, ~0] | 1,376 | **0.12** | -$944 | **6.4%** | **115.9** |
| S-D9.2 | [~0, +0.02%] | 1,376 | 0.37 | -$649 | 17.4% | 37.4 |
| S-D9.3 | [+0.02%, +0.05%] | 1,376 | 0.50 | -$548 | 26.7% | 19.5 |
| S-D9.4 | [+0.05%, +0.08%] | 1,376 | 0.62 | -$396 | 28.3% | 19.7 |
| S-D9.5 | [+0.08%, +0.13%] | 1,375 | 0.64 | -$368 | 28.6% | 22.8 |
| S-D9.6 | [+0.13%, +0.18%] | 1,376 | 0.78 | -$214 | 29.0% | 23.9 |
| S-D9.7 | [+0.18%, +0.25%] | 1,376 | 0.76 | -$242 | 29.2% | 25.6 |
| **S-D9.8** | **[+0.25%, +0.39%]** | **1,376** | **1.11** | **+$88** | **30.2%** | 29.4 |
| **S-D9.9** | **[+0.39%, +4.10%]** | **1,376** | **1.54** | **+$392** | **33.5%** | 39.6 |

S-D9.0/1 are again the near-zero degenerate cluster (PF 0.12-0.22, 6-11% win rate, 64-116 mean bars held). The actual edge sits in S-D9.8/9 — shorting when the entry bar's close is ≥ +0.25% above the 1h MA *during* a confirmed 30-bar downtrend.

## Combined v2 profitable cells

| Trigger | Side | Sub-decile | Threshold (`pct_1h_change`) | Trips | PF | Net P&L | Win | Bars |
|---|---|---|---|---:|---:|---:|---:|---:|
| Uptrend-qualifier | Long | L-D0.0 | ≤ -0.41% | 1,318 | 1.43 | +$342 | 34.6% | 39.3 |
| Uptrend-qualifier | Long | L-D0.1 | [-0.41%, -0.26%] | 1,318 | 1.08 | +$65 | 32.2% | 30.2 |
| Downtrend-qualifier | Short | S-D9.8 | [+0.25%, +0.39%] | 1,376 | 1.11 | +$88 | 30.2% | 29.4 |
| Downtrend-qualifier | Short | S-D9.9 | ≥ +0.39% | 1,376 | 1.54 | +$392 | 33.5% | 39.6 |

Total: 5,388 trades, +$887 net, ~$0.16/trade after $0.42 fees.

**Counter-intuitive shape**: in TrendOnly mode the profitable longs are entered when `pct_1h_change` is *negative* (entry bar closed below the 1h MA, i.e. the immediate 1h has been falling), and the profitable shorts are entered when `pct_1h_change` is *positive* (entry bar above the 1h MA, immediate 1h rising). In other words, even within a confirmed 30-bar trend, the system works best when entered against the *short-term* 1h direction — buying the dip within the uptrend, selling the bounce within the downtrend.

The bars-held jumps from ~6 in the unprofitable D9 cells (longs) / D0 cells (shorts) to ~30-40 in the profitable cells — entering against the 1h drift gives the trailing stop a wide enough cushion that the trade has room to run into the underlying multi-bar trend before getting whipsawed out.

Per-trade economics are tiny (~$0.05-0.28 on $1k notional) — real edge but barely above the fee line. Compared to v1's profitable cells (~$0.81/trade across thousands of trades) v2 is the weakest of the three modes for tradable per-trade margin.

## Read across all three modes

| Mode | Aggregate PF | Profitable cells | Q0 (held ≤4 bars) | Hold pattern of profitable cells |
|---|---:|---|---:|---|
| v0 fade (BreakTrigger, OppositeChannel) | 0.45 | Downtrend-breakup long deepest 0.2%: PF 3.30, 323 trips | PF 0.013 | Q4 (≥18 bars) |
| v1 reverse + trailing target (BreakTrigger, EntryChannelTarget) | 0.58 | Every D0/D9 sub-decile: peak PF 5.87 | (median 4.9, no whipsaw mode) | Whole D0/D9 distribution |
| v2 trend-only (TrendOnly, OppositeChannel) | 0.44 | L-D0.0/1, S-D9.8/9: peak PF 1.54 | PF 0.018 | ~30-40 bars held |

v0 and v2 share the OppositeChannel cover and share its Q0 disaster (PF 0.01-0.02). v1 swapped the cover for a trailing take-profit and avoided the whipsaw entirely. The cover, not the entry direction, is the load-bearing variable in this engine family.

# v3 — TrendOnly entry + EntryChannelTargetOnBreak (delayed trailing target)

## Setup

Combines v2's TrendOnly entry with a deferred version of v1's trailing take-profit. The position runs unhedged after entry; the cover is armed only when the with-trend Donchian channel finally cracks.

- `--entry-mode trend-only`: same as v2. Uptrend qualifier → LONG, downtrend qualifier → SHORT.
- `--cover-mode entry-channel-target-on-break`: position runs with `trailingStop = 0` (unarmed) immediately after entry. When the with-trend channel pierces (long: `bar.Low < prevDonLow`; short: `bar.High > prevDonHigh`), set `trailingStop = prevDonHigh` (long) or `prevDonLow` (short) — i.e. the *opposite* end of the channel from the one that cracked. From there, the trailing-target ratchet behaves identically to v1's EntryChannelTarget: target moves toward entry on adverse drift, cover triggers on the next bar's `bar.High >= target` (long) or `bar.Low <= target` (short). Same-bar arming applies (the bar that arms cannot also cover; cover-check first runs on the following bar).

Mental model: while the trend continues running cleanly, ride it. The moment it cracks, transition to "exit at the channel's other extreme" with a trailing-take-profit that pursues current price if it keeps slipping further from the target.

## Aggregate

| Metric | v0 fade | v1 reverse-target | v2 trend-only | v3 trend-target-on-break |
|---|---:|---:|---:|---:|
| Total trips | 316,413 | 323,937 | 269,360 | **268,656** |
| Net P&L | -$193,728 | -$96,032 | -$158,145 | **-$119,540** |
| Aggregate PF | 0.446 | 0.584 | 0.443 | **0.590** |
| Win rate | 24.9% | 39.9% | 22.2% | **32.1%** |
| Median bars held | — | 4 | 7 | **11** |

| Trigger | Side | Trips | PF | Net P&L | Win rate |
|---|---|---:|---:|---:|---:|
| Uptrend-qualifier | Long | 131,439 | 0.606 | -$55,421 | 32.2% |
| Downtrend-qualifier | Short | 137,216 | 0.576 | -$64,118 | 32.1% |

v3 has the same entry as v2 (identical trigger counts) but a fundamentally different cover. Q0 (held 3-6 bars) PF 0.39 vs v2's PF 0.018 — the unhedged-then-armed cover **eliminates the breaking-bar-wick whipsaw entirely**. The position simply can't be exited on the same bar that fires.

## Decile and quintile breakdowns (unfiltered, default 30-bar qualifier)

`pct_1h_change` deciles — clean monotone, both sides have one cell crossing PF 1.0 *at the parent level*:

| Side | Decile | Range | Trips | PF | Net |
|---|---:|---|---:|---:|---:|
| Long | D0 | [-99.7%, +0.0001%] | 13,144 | **0.999** | -$4 (effectively flat) |
| Long | D9 | [+1.78%, +696%] | 13,144 | 0.824 | -$4,719 |
| Short | D0 | [-99.9%, -1.70%] | 13,722 | 0.679 | -$9,957 |
| Short | D9 | [-0.0001%, +4.10%] | 13,722 | **1.020** | +$170 |

**`bars_held` quintiles — Q4 cleanly profitable on both sides**:

| Side | Q | Held range | Trips | PF | Net | Win |
|---|---:|---|---:|---:|---:|---:|
| Long | Q0 | [3, 6] | 26,561 | 0.39 | -$10,135 | 28.4% |
| Long | Q1 | [7, 9] | 27,917 | 0.22 | -$24,297 | 21.2% |
| Long | Q2 | [10, 13] | 27,549 | 0.37 | -$22,698 | 28.2% |
| Long | Q3 | [14, 20] | 24,676 | 0.65 | -$12,056 | 38.1% |
| Long | **Q4** | **[21, 2942]** | **24,676** | **1.62** | **+$13,765** | **47.1%** |
| Short | Q0 | [2, 7] | 37,486 | 0.29 | -$20,146 | 25.7% |
| Short | Q1 | [8, 9] | 18,620 | 0.23 | -$17,014 | 21.7% |
| Short | Q2 | [10, 13] | 28,497 | 0.35 | -$24,572 | 28.3% |
| Short | Q3 | [14, 20] | 26,369 | 0.62 | -$14,231 | 38.1% |
| Short | **Q4** | **[21, 2766]** | **26,244** | **1.45** | **+$11,844** | **46.6%** |

~50,000 profitable trades via Q4 alone — the largest profitable cell-population of any mode tested so far.

## Sub-decile breakdown of D0 long / D9 short

**Long D0 — 5 of 10 sub-deciles profitable**:

| Sub | Range | Trips | PF | Net | Win | Bars |
|---:|---|---:|---:|---:|---:|---:|
| **L-D0.0** | [-99.7%, -0.43%] | 1,315 | **1.97** | +$709 | 44.9% | 43.0 |
| **L-D0.1** | [-0.43%, -0.28%] | 1,314 | **1.75** | +$528 | 43.5% | 33.9 |
| **L-D0.2** | [-0.28%, -0.20%] | 1,314 | **1.46** | +$328 | 42.3% | 28.9 |
| **L-D0.3** | [-0.19%, -0.14%] | 1,315 | **1.18** | +$136 | 38.6% | 27.1 |
| **L-D0.4** | [-0.14%, -0.09%] | 1,314 | **1.13** | +$105 | 39.1% | 24.6 |
| L-D0.5-7 | mid | 3,943 | 0.76-0.99 | -$272 | 30-37% | 24-30 |
| L-D0.8/9 | near-zero | 2,629 | **0.21** | -$1,537 | **10%** | 80-98 |

**Short D9 — 6 of 10 sub-deciles profitable**:

| Sub | Range | Trips | PF | Net | Win |
|---:|---|---:|---:|---:|---:|
| S-D9.0/1 | near-zero | 2,745 | **0.21** | -$1,629 | **11%** |
| S-D9.2-3 | mid | 2,744 | 0.70-0.90 | -$376 | 31-36% |
| **S-D9.4** | [+0.06%, +0.09%] | 1,372 | **1.02** | +$14 | 38.3% |
| **S-D9.5** | [+0.09%, +0.14%] | 1,372 | **1.17** | +$147 | 37.2% |
| **S-D9.6** | [+0.14%, +0.19%] | 1,372 | **1.22** | +$179 | 40.9% |
| **S-D9.7** | [+0.19%, +0.27%] | 1,372 | **1.35** | +$280 | 41.0% |
| **S-D9.8** | [+0.27%, +0.41%] | 1,372 | **1.95** | +$634 | 44.9% |
| **S-D9.9** | [+0.41%, +4.10%] | 1,373 | **2.38** | +$921 | 46.3% |

**Diagnostic note**: the near-zero `pct_1h_change` cluster (long D0.8/9, short D9.0/1) consistently bleeds across modes. Mean held jumps to 80-110 bars; entry sits almost exactly at the 1h MA so the trailing target has no room to ratchet productively. These are the "consolidation" entries that bleed slowly to fees.

## 4-mode summary

| Mode | PF | Net | Best cell | # profitable trades |
|---|---:|---:|---|---:|
| v0 fade (BreakTrigger, OppositeChannel) | 0.45 | -$194k | Downtrend-breakup long deepest 0.2%: PF 3.30 | 323 |
| v1 reverse-target (BreakTrigger, ReverseDirection, EntryChannelTarget) | 0.58 | -$96k | Every D0/D9 sub-decile profitable; peak PF 5.87 | ~32k |
| v2 trend-only (TrendOnly, OppositeChannel) | 0.44 | -$158k | L-D0.0/1, S-D9.8/9: peak PF 1.54 | ~5,400 |
| **v3 trend-target-on-break (TrendOnly, EntryChannelTargetOnBreak)** | **0.59** | **-$120k** | L-D0.0 PF 1.97 / S-D9.9 PF 2.38; **Q4 PF 1.6 across both sides** | **~50k via Q4** |

v3 is structurally the cleanest of the four: it has v2's trend-following spirit (ride the established trend) but avoids v0/v2's Q0 whipsaw (cover only kicks in once the trend has demonstrably cracked). Aggregate is below 1.0 but the profitable cell-population is the largest of any mode.

# MA-side entry filter + min_trend_bars sweep

## Setup

Two CLI flags added to gate entries by 1h MA-position at the bar of entry:

- `--max-pct-1h-for-long` (default **0.0**): a LONG entry only fires if `pct_1h_change <= this`. The default requires the entry bar's close to be **at or below** the 1h MA — there's room to revert *up* toward it.
- `--min-pct-1h-for-short` (default **0.0**): a SHORT entry only fires if `pct_1h_change >= this`. Default requires the entry bar at-or-above the 1h MA — room to revert *down*.

Pass `+1e9` / `-1e9` to disable. Default-on follows from the v0/v1/v2/v3 deciles all telling the same story: the profitable trades sit on the mean-reverting side of the 1h MA, the unprofitable trades sit on the with-immediate-1h-trend side. The filter cleanly bisects the universe and removes ~50% of trips wholesale, while keeping the profitable population.

In TrendOnly mode the filter does *not* consume the side's "armed" flag — a filtered candidate leaves the side armed for the next satisfying bar within the same trend run. This means the engine waits patiently for an MA-side-correct entry rather than skipping the run entirely.

## Sweep — min_trend_bars 20..30, both v1 and v3

Run with `--min-trend-bars "20,22,24,26,28,30"` and the MA-side filter active.

### v1-filtered (BreakTrigger + ReverseDirection + EntryChannelTarget + filter)

| min_trend_bars | Trips | Agg PF | Net | Win | Med bars | Long PF | Short PF |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 20 | 273,586 | 0.762 | -$35,917 | 44.0% | 4 | 0.766 | 0.757 |
| 22 | 181,769 | 0.885 | -$10,610 | 44.2% | 4 | 0.896 | 0.874 |
| **24** | **128,585** | **1.010** | **+$595** | 44.1% | 4 | 1.019 | 1.001 |
| **26** | **97,164** | **1.154** | **+$6,581** | 44.3% | 4 | 1.175 | 1.134 |
| **28** | **77,276** | **1.250** | **+$8,187** | 44.2% | 4 | 1.260 | 1.241 |
| **30** | **63,143** | **1.334** | **+$8,607** | 43.9% | 4 | 1.343 | 1.325 |

### v3-filtered (TrendOnly + EntryChannelTargetOnBreak + filter)

| min_trend_bars | Trips | Agg PF | Net | Win | Med bars | Long PF | Short PF |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 20 | 177,261 | 0.871 | -$16,362 | 37.7% | 11 | 0.875 | 0.868 |
| 22 | 119,536 | 0.978 | -$1,745 | 38.1% | 13 | 0.961 | 0.997 |
| **24** | **86,918** | **1.081** | **+$4,462** | 38.5% | 14 | 1.042 | 1.125 |
| **26** | **67,301** | **1.191** | **+$7,713** | 38.6% | 16 | 1.147 | 1.241 |
| **28** | **54,392** | **1.300** | **+$9,315** | 39.0% | 18 | 1.218 | 1.393 |
| **30** | **45,496** | **1.406** | **+$10,180** | 39.5% | 19 | 1.303 | **1.526** |

**Key findings:**

1. **The filter alone flips both modes aggregate-profitable.** Unfiltered v1 PF 0.58, v3 PF 0.59; filtered at trend=24+ both cross PF 1.0. The MA-side condition removes ~50% of trips and they're disproportionately the loss-making half.

2. **PF rises monotonically with trend bars.** From 20 → 30, v1 lifts 0.76 → 1.33 and v3 lifts 0.87 → 1.41. The trend qualifier is genuinely informative — longer runs select for stronger setups, not just rarer ones.

3. **30 bars is the cleanest single threshold but not the only profitable one.** Everything ≥ 24 is profitable, with PF and per-trade economics improving as you go stricter. Trade volume drops 4× from trend=20 to trend=30, while net P&L *rises*. Trip-count cost is real but the gain in PF more than compensates.

4. **v3 narrowly beats v1 at strict thresholds**, almost entirely via short-side dominance: at trend=30, v3 short PF is **1.53** vs v1 short PF 1.33. The unfiltered v1 dominance over v3 (PF 17 vs 0.59) was masking unrealised losers via the fixed-target-with-no-stop pathology that the trailing target now properly realises.

## `pct_1h_change` decile shape per cell

Per-decile PF, both sides, all trend-bar thresholds. Cells profitable at PF ≥ 1.0 in **bold**.

### v1-filtered

| trend | side | D0 | D1 | D2 | D3 | D4 | D5 | D6 | D7 | D8 | D9 |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 20 | long | **1.88** | **1.36** | **1.01** | 0.81 | 0.67 | 0.59 | 0.54 | 0.47 | 0.42 | 0.30 |
| 20 | short | 0.32 | 0.44 | 0.47 | 0.56 | 0.59 | 0.65 | 0.81 | 0.97 | **1.28** | **1.86** |
| 22 | long | **2.52** | **1.78** | **1.25** | 0.99 | 0.80 | 0.68 | 0.63 | 0.52 | 0.45 | 0.28 |
| 22 | short | 0.29 | 0.47 | 0.53 | 0.63 | 0.67 | 0.79 | 0.95 | **1.21** | **1.65** | **2.32** |
| 24 | long | **3.27** | **2.12** | **1.53** | **1.20** | 0.90 | 0.80 | 0.69 | 0.57 | 0.48 | 0.24 |
| 24 | short | 0.26 | 0.51 | 0.59 | 0.70 | 0.77 | 0.97 | **1.10** | **1.52** | **2.08** | **2.75** |
| 26 | long | **4.24** | **2.65** | **1.84** | **1.41** | **1.08** | 0.96 | 0.81 | 0.66 | 0.53 | 0.20 |
| 26 | short | 0.23 | 0.53 | 0.67 | 0.75 | 0.98 | **1.15** | **1.33** | **1.80** | **2.36** | **3.39** |
| 28 | long | **4.47** | **2.98** | **2.10** | **1.56** | **1.27** | **1.04** | 0.93 | 0.72 | 0.55 | 0.17 |
| 28 | short | 0.20 | 0.58 | 0.74 | 0.83 | **1.14** | **1.31** | **1.51** | **2.06** | **2.75** | **3.68** |
| 30 | long | **4.75** | **3.27** | **2.26** | **1.75** | **1.45** | **1.19** | **1.02** | 0.77 | 0.56 | 0.13 |
| 30 | short | 0.16 | 0.63 | 0.75 | 0.95 | **1.21** | **1.41** | **1.64** | **2.24** | **3.07** | **4.21** |

### v3-filtered

| trend | side | D0 | D1 | D2 | D3 | D4 | D5 | D6 | D7 | D8 | D9 |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 20 | long | **1.61** | **1.08** | 0.98 | 0.84 | 0.82 | 0.78 | 0.71 | 0.65 | 0.70 | 0.55 |
| 20 | short | 0.62 | 0.70 | 0.70 | 0.74 | 0.78 | 0.83 | 0.90 | 0.96 | **1.11** | **1.35** |
| 22 | long | **1.70** | **1.31** | **1.10** | 0.92 | 0.92 | 0.87 | 0.82 | 0.74 | 0.76 | 0.51 |
| 22 | short | 0.67 | 0.78 | 0.79 | 0.84 | 0.91 | 0.97 | **1.04** | **1.14** | **1.28** | **1.63** |
| 24 | long | **1.87** | **1.54** | **1.19** | **1.02** | **1.02** | 0.94 | 0.89 | 0.85 | 0.86 | 0.42 |
| 24 | short | 0.70 | 0.93 | 0.84 | 0.95 | 0.97 | **1.15** | **1.13** | **1.31** | **1.53** | **1.94** |
| 26 | long | **2.08** | **1.73** | **1.44** | **1.15** | **1.08** | **1.09** | **1.00** | 0.95 | 0.95 | 0.35 |
| 26 | short | 0.67 | 0.97 | **1.03** | **1.05** | **1.13** | **1.16** | **1.21** | **1.41** | **1.86** | **2.29** |
| 28 | long | **2.14** | **1.76** | **1.58** | **1.22** | **1.23** | **1.16** | **1.10** | **1.09** | 0.99 | 0.30 |
| 28 | short | 0.73 | **1.03** | **1.15** | **1.16** | **1.39** | **1.26** | **1.39** | **1.63** | **2.12** | **2.53** |
| 30 | long | **2.18** | **2.01** | **1.73** | **1.34** | **1.37** | **1.30** | **1.24** | **1.15** | **1.01** | 0.24 |
| 30 | short | 0.78 | **1.11** | **1.34** | **1.36** | **1.31** | **1.42** | **1.49** | **1.77** | **2.43** | **2.80** |

## Read of the per-cell shapes

**v1**: monotone diagonal — PF rises from D0 → D9 on shorts (deeper 1h rises = better short fade) and falls on longs (deeper 1h declines = better long fade). At weaker trend qualifiers, the profitable region is concentrated in the 2-3 extreme deciles per side; at trend=30 the profitable band reaches 6-7 deciles per side. The shape stays monotone throughout — there's no inversion, just a steepening gradient as trend bars increase.

**v3**: shallower gradient, broader profitable band. At trend=20 only D0/D1 (long) or D8/D9 (short) cross PF 1.0; at trend=30 the profitable band reaches 9 of 10 deciles per side (only the degenerate near-zero `pct_1h_change` cluster — long D9, short D0 — stays underwater). The peak PF per band is lower than v1 (long D0 PF 2.18 vs v1's 4.75; short D9 PF 2.80 vs v1's 4.21), but the spread of the profitable population is much wider.

**Practical implication**: v1 isolates a small set of high-edge trades; v3 captures a much larger volume of moderate-edge trades. Choice depends on operating constraints — capital efficiency favors v1 (fewer trades, more concentrated edge per round-trip); diversification favors v3 (larger profitable population, less per-symbol concentration risk).

**Trend-bar takeaway**: 30 is a clean default but **24-28 are also viable**, especially if trade volume matters. v1 trend=24 has +$595 / 128k trips at PF 1.01 — barely positive but ten thousand more profitable trades than trend=30. Below 24 the aggregate is loss-making for both modes, even with the filter on. The 30-bar threshold is the cleanest *single* sweet spot, but the engine has a useful operating band from 24 → 30 with smooth tradeoffs.

# v4 / v5 — 1h-MA cover

## Setup

The decile breakdowns of every prior mode pointed at the same underlying signal: profitable trades sit on the mean-reverting side of the 1h MA at entry, and price tends to revisit the 1h MA on a sub-hour horizon. v4/v5 make the 1h MA the *cover target* directly.

- `--cover-mode ma-cross-cover`: cover when bar.Close crosses the 1h MA from the favorable side. Long covers when `bar.Close >= closeShortMa.mean`; short covers when `bar.Close <= closeShortMa.mean`. No stops, no time cap. Trade runs until the cross fires (or end-of-stream Flush, but that's <1% in practice — price reliably revisits the 1h MA).

The two variants pair MaCrossCover with each entry mode:

| Mode | Entry | Cover | What's faded |
|---|---|---|---|
| v4 | BreakTrigger + ReverseDirection (= v1's entry) | MaCrossCover | the just-broken bar; target is the 1h MA |
| v5 | TrendOnly (= v3's entry) | MaCrossCover | the trend's stretch above/below the 1h MA |

**Strict-inequality MA-side filter** (engine-wide change): `MaxPct1hChangeForLong` / `MinPct1hChangeForShort` defaults stay at 0.0 but the comparison is now strict (`<` for longs, `>` for shorts) instead of non-strict (`<=` / `>=`). Required because under MaCrossCover, an entry placed exactly on the MA would cover instantly on the next bar's MA-cross check, guaranteed loser to fees. The strict change is global (also applies to v1/v3-filtered) but produces no measurable difference on those modes since `pct_1h_change` exactly equal to 0.0 is measure-zero in continuous data.

## Universe sweep — both modes across min_trend_bars 20..30

### v4 (BreakTrigger + ReverseDirection + MaCrossCover + strict filter)

| min_trend_bars | Trips | Agg PF | Net P&L | Win rate | Med bars | Flush% | Long PF | Short PF |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 20 | 267,074 | 1.292 | +$43,942 | 68.4% | 7 | 0.24% | 1.242 | 1.348 |
| 22 | 177,617 | 1.634 | **+$54,680** | 70.9% | 6 | 0.36% | 1.571 | 1.703 |
| 24 | 125,719 | 2.056 | +$55,597 | 73.0% | 6 | 0.50% | 2.005 | 2.111 |
| 26 | 95,136 | 2.510 | +$53,021 | 74.6% | 6 | 0.65% | 2.458 | 2.565 |
| 28 | 75,766 | 2.856 | +$47,974 | 75.5% | 6 | 0.77% | 2.792 | 2.923 |
| **30** | **62,003** | **3.190** | **+$43,273** | **75.9%** | 6 | 0.82% | **3.196** | **3.184** |

### v5 (TrendOnly + MaCrossCover + strict filter)

| min_trend_bars | Trips | Agg PF | Net P&L | Win rate | Med bars | Flush% | Long PF | Short PF |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 20 | 197,638 | 1.739 | +$58,688 | 69.5% | 4 | 0.32% | 1.664 | 1.826 |
| 22 | 137,611 | 2.367 | **+$60,607** | 72.6% | 4 | 0.46% | 2.259 | 2.489 |
| 24 | 103,001 | 3.107 | +$57,226 | 74.9% | 3 | 0.60% | 2.956 | 3.276 |
| 26 | 81,619 | 3.864 | +$52,650 | 76.4% | 3 | 0.70% | 3.767 | 3.966 |
| 28 | 67,253 | 4.558 | +$47,710 | 77.5% | 3 | 0.71% | 4.389 | 4.739 |
| **30** | **57,037** | **5.090** | **+$43,181** | **78.3%** | 3 | 0.64% | **4.963** | **5.221** |

**Key findings:**

1. **Both modes profitable at every trend-bar threshold from 20 onward.** v1/v3-filtered required trend≥24 to cross PF 1.0; v4/v5 already cross PF 1.3 (v4) / 1.7 (v5) at trend=20.
2. **Net P&L peaks at trend=22** (~$55-60k) for both modes; PF keeps rising monotonically to trend=30. Trade-volume drop outweighs PF gain past 22 bars on the *net* axis. PF gains are still useful for the per-trade economics.
3. **Flush artefact is negligible** (<1%). Concerns about unrealised losers don't bite — price revisits the 1h MA on a sub-hour horizon, so the cover triggers for >99% of trades.
4. **v5 dominates v4** at every threshold: PF, win rate, and per-trade economics. v5 trend=30 PF 5.09 vs v4 trend=30 PF 3.19. Median 3-4 bars (v5) vs 6-7 (v4). Both deliver near-identical net P&L (~$43k) at trend=30 because v5's higher PF offsets v4's slightly larger trip count.

## 4-mode comparison at trend=30 (filter on)

| Mode | Trips | Agg PF | Net | Win | Per-trade | Median bars |
|---|---:|---:|---:|---:|---:|---:|
| v1-filtered (BreakTrigger + EntryChannelTarget + reverse) | 63,143 | 1.33 | +$8,607 | 43.9% | +$0.14 | 4 |
| v3-filtered (TrendOnly + EntryChannelTargetOnBreak) | 45,496 | 1.41 | +$10,180 | 39.5% | +$0.22 | 19 |
| **v4 (BreakTrigger + MaCrossCover + reverse)** | **62,003** | **3.19** | **+$43,273** | **75.9%** | **+$0.70** | 6 |
| **v5 (TrendOnly + MaCrossCover)** | **57,037** | **5.09** | **+$43,181** | **78.3%** | **+$0.76** | 3 |

**The 1h MA is the right reversion target for this engine family.** At the same trip volume:
- ~3.5-5× the PF
- ~5× the net P&L
- ~2× the win rate
- 3× the per-trade economics (after fees)

The intuition: v1/v3 used the *prior 3-bar Donchian extreme* as the target — too tight on short-stretch entries (target is just above/below entry by a few bars' range). v4/v5 use the *trailing 1h mean* — wide enough that there's room for price to actually revert toward it, narrow enough that the cross fires quickly when it does.

## Decile shapes — pct_1h_change PFs per side, per trend-bar cell

### v4

| trend | side | D0 | D1 | D2 | D3 | D4 | D5 | D6 | D7 | D8 | D9 |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 20 | long | **2.18** | **1.96** | **1.53** | **1.31** | **1.13** | 0.96 | 0.80 | 0.71 | 0.60 | 0.35 |
| 20 | short | 0.39 | 0.62 | 0.71 | 0.92 | **1.05** | **1.25** | **1.36** | **1.69** | **2.05** | **2.65** |
| 22 | long | **3.05** | **2.69** | **2.07** | **1.66** | **1.47** | **1.13** | **1.03** | 0.88 | 0.72 | 0.37 |
| 22 | short | 0.39 | 0.73 | 0.91 | **1.22** | **1.31** | **1.62** | **1.82** | **2.23** | **2.79** | **3.66** |
| 24 | long | **4.43** | **3.82** | **2.98** | **2.08** | **1.92** | **1.57** | **1.27** | **1.15** | 0.86 | 0.35 |
| 24 | short | 0.39 | 0.97 | **1.18** | **1.44** | **1.58** | **2.27** | **2.21** | **3.05** | **3.54** | **4.82** |
| 26 | long | **5.55** | **5.32** | **3.62** | **2.77** | **2.47** | **2.16** | **1.44** | **1.43** | **1.12** | 0.34 |
| 26 | short | 0.37 | **1.20** | **1.49** | **1.86** | **2.30** | **2.82** | **2.79** | **3.81** | **4.26** | **5.96** |
| 28 | long | **5.68** | **6.31** | **4.36** | **2.96** | **3.32** | **2.76** | **1.99** | **1.61** | **1.29** | 0.29 |
| 28 | short | 0.33 | **1.41** | **1.70** | **2.25** | **3.10** | **2.86** | **3.60** | **4.58** | **4.94** | **7.22** |
| 30 | long | **6.30** | **6.50** | **6.22** | **3.78** | **3.32** | **3.53** | **2.49** | **2.21** | **1.46** | 0.24 |
| 30 | short | 0.29 | **1.38** | **2.22** | **2.52** | **3.78** | **3.25** | **4.14** | **4.79** | **5.20** | **8.55** |

### v5

| trend | side | D0 | D1 | D2 | D3 | D4 | D5 | D6 | D7 | D8 | D9 |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 20 | long | **2.88** | **2.28** | **2.10** | **1.86** | **1.41** | **1.33** | **1.28** | **1.02** | **1.12** | 0.57 |
| 20 | short | 0.73 | **1.01** | **1.20** | **1.28** | **1.57** | **1.65** | **2.06** | **2.31** | **2.80** | **3.50** |
| 22 | long | **4.76** | **4.14** | **2.98** | **2.40** | **2.14** | **1.88** | **1.62** | **1.41** | **1.48** | 0.50 |
| 22 | short | 0.76 | **1.40** | **1.62** | **1.91** | **2.30** | **2.23** | **2.92** | **3.28** | **4.08** | **5.76** |
| 24 | long | **8.94** | **6.52** | **4.18** | **3.07** | **3.19** | **2.67** | **1.93** | **2.23** | **1.81** | 0.42 |
| 24 | short | 0.72 | **1.92** | **2.01** | **2.48** | **2.75** | **3.55** | **4.00** | **4.82** | **7.21** | **9.20** |
| 26 | long | **16.33** | **7.46** | **7.08** | **5.05** | **4.35** | **3.58** | **2.56** | **2.83** | **2.26** | 0.33 |
| 26 | short | 0.65 | **1.78** | **2.90** | **3.18** | **3.96** | **4.66** | **4.83** | **5.46** | **15.24** | **13.67** |
| 28 | long | **15.74** | **9.60** | **8.84** | **6.75** | **6.16** | **4.92** | **3.48** | **4.02** | **2.52** | 0.25 |
| 28 | short | 0.68 | **1.55** | **3.82** | **4.26** | **5.60** | **5.38** | **6.67** | **9.94** | **13.93** | **25.19** |
| 30 | long | **21.46** | **14.31** | **12.73** | **6.58** | **8.41** | **6.69** | **5.49** | **4.56** | **2.36** | 0.17 |
| 30 | short | 0.73 | **1.14** | **4.59** | **4.73** | **6.25** | **6.53** | **9.08** | **16.31** | **18.25** | **34.40** |

## Read of v4/v5

**v4** has the same monotone-diagonal shape as v1-filtered but with PFs lifted ~3-4× across the board. Long D0 → D9 declines from PF 6.30 (deepest 1h decline) to 0.24 (degenerate near-zero). Short D9 PF 8.55 at trend=30. 9 of 10 long deciles profitable, 9 of 10 short deciles profitable.

**v5** has an even steeper shape with extraordinary extreme-tail PFs. At trend=30, long D0 PF **21.5** and short D9 PF **34.4** — one to two orders of magnitude higher than the v3-filtered equivalents (PF 2.18 / 2.80 at the same threshold). The profitable population is essentially the entire decile space minus the degenerate near-zero cluster: 9 of 10 long deciles profitable, 9 of 10 short deciles profitable, with no decile underperforming PF 1.0 except the long-D9 / short-D0 cluster where price sat almost exactly at the 1h MA at entry.

The v5 trend=20 cell is already cleaner than the v3-filtered trend=30 cell: v5 trend=20 has 18 of 20 deciles profitable; v3-filtered trend=30 has 18 of 20 deciles profitable. v5 produces this performance with a *looser* trend qualifier — meaning v5 trend=20 generates more trades AND has a higher PF than v3-filtered trend=30.

## 5-mode summary across the workstream

| Mode | Aggregate PF | Best cell | Production-ready? |
|---|---:|---|---|
| v0 fade (BreakTrigger + OppositeChannel) | 0.45 | Downtrend-breakup long deepest 0.2%: PF 3.30 (323 trips) | No — Q0 whipsaw |
| v1 reverse-target (BreakTrigger + reverse + EntryChannelTarget) | 0.58 | D0/D9 sub-deciles all profitable; peak PF 5.87 | Honest mode but PF still <1.0 aggregate |
| v2 trend-only (TrendOnly + OppositeChannel) | 0.44 | L-D0.0/1, S-D9.8/9: peak PF 1.54 | No — same Q0 whipsaw as v0 |
| v3 trend-target-on-break (TrendOnly + EntryChannelTargetOnBreak) | 0.59 | Q4 PF 1.6 across both sides (~50k trips) | Aggregate PF still <1.0 |
| v1-filtered, trend=30 | **1.33** | Long D0 PF 4.75 / Short D9 PF 4.21 | Yes — 63k trips +$8.6k |
| v3-filtered, trend=30 | **1.41** | 9 of 10 long + 9 of 10 short deciles profitable | Yes — 45k trips +$10.2k |
| **v4 (BreakTrigger + reverse + MaCrossCover + filter), trend=30** | **3.19** | **Long D0 PF 6.30 / Short D9 PF 8.55** | **Yes — 62k trips +$43.3k** |
| **v5 (TrendOnly + MaCrossCover + filter), trend=30** | **5.09** | **Long D0 PF 21.5 / Short D9 PF 34.4** | **Yes — 57k trips +$43.2k** |

v5 with the strict filter and 1h-MA cover is the strongest version found in the workstream by every measure: highest aggregate PF, highest per-trade economics, broadest profitable population, fastest resolving trades (median 3 bars), no Flush artefact. The strict-MA-side filter combined with the 1h-MA cover make it a clean mean-reversion scalp engine — enter on the wrong side of the MA after a confirmed trend run, exit when price closes back through the MA.

## v5 — sweep over `--short-ref-minutes` (cover MA length)

The 1h MA is fixed in v4/v5 by the `ShortRefMinutes` config field (default 60). Sweeping this from 60m → 300m tests whether a different reversion target produces more edge. Both the strict-inequality entry filter and the cover share this MA length, so the experiment is "wider/narrower reversion target" rather than "decoupled filter and cover."

Trend-bars held at 30, MaCrossCover, TrendOnly entry:

| MA (min) | Trips | Agg PF | Net P&L | Win | Med bars | Mean bars | Flush% | Long PF | Short PF | Per-trade |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| **60** | 57,037 | **5.090** | **+$43,181** | **78.3%** | **3** | 7.9 | 0.64% | **4.96** | **5.22** | **+$0.76** |
| 90 | 59,348 | 3.071 | +$36,607 | 75.8% | 4 | 13.4 | 0.90% | 2.89 | 3.27 | +$0.62 |
| 120 | 62,472 | 2.358 | +$33,051 | 74.8% | 5 | 19.5 | 0.95% | 2.26 | 2.47 | +$0.53 |
| 150 | 65,098 | 1.931 | +$29,937 | 74.7% | 6 | 26.1 | 0.94% | 1.87 | 1.99 | +$0.46 |
| 180 | 67,607 | 1.692 | +$27,615 | 74.4% | 7 | 32.8 | 0.92% | 1.67 | 1.72 | +$0.41 |
| 240 | 70,704 | 1.380 | +$21,575 | 74.3% | 12 | 47.6 | 0.89% | 1.37 | 1.39 | +$0.31 |
| 300 | 72,414 | 1.243 | +$17,241 | 74.3% | 17 | 62.3 | 0.87% | 1.22 | 1.27 | +$0.24 |

**60m wins every metric. PF, net P&L, win rate, and per-trade economics all peak at the shortest MA tested and decay monotonically as the MA lengthens.**

Going from MA=60 to MA=300:
- PF collapses **5.09 → 1.24** (4.1× degradation)
- Net P&L drops **$43k → $17k** (60% drop)
- Per-trade falls **+$0.76 → +$0.24** (3× drop)
- Median hold goes **3 bars → 17 bars** (5.7× longer); mean **7.9 → 62.3 bars**
- Trip count *rises* slightly (57k → 72k) because the loosened filter (longer MA = more "below MA" eligible bars) lets more borderline candidates through, but they bleed PF.

Flush stays negligible at every length. Longer MAs don't trap unrealised losers — they just produce slower-resolving lower-quality trades.

### Per-MA `pct_1h_change` decile shapes

Where the per-cell deciles really tell the story — longer MAs collapse the extreme-tail PFs that drove v5's edge:

| MA | Long D0 PF | Long D9 PF | Short D0 PF | Short D9 PF |
|---:|---:|---:|---:|---:|
| **60m** | **21.46** | 0.17 | 0.73 | **34.40** |
| 90m | 2.98 | 0.41 | 0.55 | 6.87 |
| 120m | 2.33 | 0.64 | 0.57 | 3.59 |
| 180m | 1.80 | 0.70 | 0.67 | 1.72 |
| 300m | 1.29 | 0.82 | 0.81 | 1.13 |

The deep-extreme-tail trades (long D0, short D9) lose PF by **16-30×** going from MA=60 to MA=300. The middle of the distribution stays ~PF 1.2-1.4 throughout — but the extreme-tail trades that deliver most of v5's economics rely on the MA being a **tight, fast-moving target** that price genuinely whipsaws around on a sub-hourly horizon.

### Read

A longer MA is a different system. With MA=300, "5% below the 5h MA" describes a sustained multi-hour trend extreme — a regime, not a wick. Those don't snap back cleanly. With MA=60, the same percentage describes a momentary deviation from the very recent mean — a wick that frequently reverts within minutes. The strict filter + 60m cover catches exactly that wick-reversion regime, and the deep tails of the `pct_1h_change` distribution are *predominantly* such wicks.

**60m is the right MA length.** No need to experiment further on this axis.

## v5 — 2D MA × min_trend_bars grid (cover-MA-length 30..60m, trend 20..30, MA-side filter at 0/0)

The MA-length sweep above used the v5 default `--min-trend-bars 30`. To check whether shorter MAs interact with shorter trend qualifiers, a 2D grid sweeps cover-MA-length 30..60m (in 5m steps) × min_trend_bars 20..30 (in 2-bar steps), subject to a "MA window length must be at least 1.5× the trend qualifier" constraint to keep the strict-inequality MA-side filter from being trivially satisfied (a bar that just hit the 30-bar uptrend qualifier is almost always above its own 30m MA).

Constraint table — `.` cells excluded:

### Aggregate PF — rows = MA, cols = min_trend_bars

| MA | 20 | 22 | 24 | 26 | 28 | 30 |
|---:|---:|---:|---:|---:|---:|---:|
| 30 | 4.488 | . | . | . | . | . |
| 35 | 3.801 | 4.776 | . | . | . | . |
| 40 | 3.169 | 4.190 | 5.200 | . | . | . |
| 45 | 2.658 | 3.602 | 4.595 | 5.491 | 6.142 | **6.446** |
| 50 | 2.242 | 3.080 | 3.968 | 4.810 | 5.471 | 5.897 |
| 55 | 1.938 | 2.677 | 3.465 | 4.270 | 4.928 | 5.470 |
| 60 | . | . | . | . | . | 5.090 |

### Net P&L

| MA | 20 | 22 | 24 | 26 | 28 | 30 |
|---:|---:|---:|---:|---:|---:|---:|
| **30** | **+$90,677** | . | . | . | . | . |
| 35 | +$84,949 | +$76,817 | . | . | . | . |
| 40 | +$80,092 | +$73,418 | +$66,484 | . | . | . |
| 45 | +$75,269 | +$70,349 | +$63,984 | +$57,820 | +$52,105 | +$46,881 |
| 50 | +$69,567 | +$67,142 | +$61,393 | +$55,815 | +$50,252 | +$45,257 |
| 55 | +$63,620 | +$63,893 | +$59,120 | +$54,067 | +$48,733 | +$44,166 |
| 60 | . | . | . | . | . | +$43,181 |

### Win rate

| MA | 20 | 22 | 24 | 26 | 28 | 30 |
|---:|---:|---:|---:|---:|---:|---:|
| 30 | 79.20% | . | . | . | . | . |
| 35 | 76.70% | 79.45% | . | . | . | . |
| 40 | 74.44% | 77.51% | 79.41% | . | . | . |
| 45 | 72.74% | 75.98% | 78.03% | 79.26% | 80.25% | **80.87%** |
| 50 | 71.30% | 74.64% | 76.82% | 78.18% | 79.06% | 79.75% |
| 55 | 70.23% | 73.49% | 75.73% | 77.14% | 78.16% | 78.94% |
| 60 | . | . | . | . | . | 78.28% |

### Trip count

| MA | 20 | 22 | 24 | 26 | 28 | 30 |
|---:|---:|---:|---:|---:|---:|---:|
| 30 | 146,873 | . | . | . | . | . |
| 35 | 149,872 | 116,156 | . | . | . | . |
| 40 | 156,889 | 118,385 | 94,488 | . | . | . |
| 45 | 165,798 | 122,063 | 95,712 | 78,498 | 66,320 | 57,209 |
| 50 | 175,890 | 126,861 | 97,782 | 79,183 | 66,358 | 56,900 |
| 55 | 186,631 | 132,074 | 100,348 | 80,322 | 66,725 | 56,909 |
| 60 | . | . | . | . | . | 57,037 |

Median bars held = **3 across the entire grid** — the cover MA shrinking does not make trades resolve faster (already optimal at MA=60). What changes is that shorter MAs catch tighter wicks at smaller magnitudes, producing more trips with slightly worse per-trade economics.

### Two distinct champions

Two cells dominate, optimising different axes:

| Champion | MA | trend | PF | Net | Win | Trips | $/trade |
|---|---:|---:|---:|---:|---:|---:|---:|
| **PF leader** | **45** | **30** | **6.45** | +$46,881 | **80.87%** | 57,209 | **+$0.819** |
| **Net P&L leader** | **30** | **20** | 4.49 | **+$90,677** | 79.20% | **146,873** | +$0.617 |
| Prior baseline | 60 | 30 | 5.09 | +$43,181 | 78.28% | 57,037 | +$0.757 |

**Both cells beat the prior MA=60, trend=30 baseline cleanly** on every metric they optimise. MA=45/trend=30 lifts PF by 27% and beats the baseline on every metric simultaneously (PF, net, win, $/trade). MA=30/trend=20 trades PF for ~2.6× the trade volume and >2× the net P&L; per-trade economics still healthy at +$0.62.

### Read

The 1.5× constraint binds along a diagonal in the grid. The cells closest to that diagonal (MA = 1.5×trend exactly) consistently produce the highest PF *for that MA* — the strict filter has the most headroom there. Cells well above the diagonal (MA much larger than 1.5×trend) lose PF as the MA becomes too smoothed relative to the qualifier window.

Two coherent operating points emerge:
- **MA=45 / trend=30** — best risk-adjusted return per trade. Use when capital is the binding constraint or when execution slippage is a worry (fewer trades = less slippage exposure).
- **MA=30 / trend=20** — best absolute return. Use when capital can absorb 2.5× the trade volume and the goal is total profit dollars rather than per-trade efficiency.

Both are strictly preferable to the prior MA=60/trend=30 baseline. **MA=45/trend=30 is the new v5 default.**

## v5 — 40 bp MA-side filter, expanded grid (MA 30..60 × trend 14..30)

Round-trip taker fees on a $1k notional are $0.80 = 8 bp. At the 0 bp filter default, an entry placed `pct_1h_change = -0.05%` (i.e. 5 bp below the MA) needs to recover by 8 bp just to break even on fees, and the realised reversion to the MA mean is approximately `|pct_1h_change|` — so the entire fee budget is consumed before any profit registers. The 0 bp default lets in trades that have effectively zero room to be profitable.

Setting the filter defaults to **+/- 40 bp** (`MaxPct1hChangeForLong = -0.004`, `MinPct1hChangeForShort = 0.004`) reserves an 8 bp fee budget plus ~32 bp of net mean-revert margin per round-trip. The strict-inequality filter then becomes "trades must be ≥ 40 bp on the mean-reverting side of the 1h MA at entry."

The trend qualifier was extended down to 14 bars (the prior grid stopped at 20) since the filter now rejects most of the marginal entries that benefited from the longer qualifier. The 1.5× constraint excludes cells where MA < 1.5 × trend.

### Aggregate PF — 40 bp filter — rows = MA, cols = min_trend_bars

| MA | 14 | 16 | 18 | 20 | 22 | 24 | 26 | 28 | 30 |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 30 | 4.841 | 10.563 | 27.466 | 50.357 | . | . | . | . | . |
| 35 | 3.062 | 5.559 | 14.327 | 28.182 | 38.915 | . | . | . | . |
| 40 | 2.271 | 3.622 | 8.056 | 15.624 | 24.292 | 48.324 | **67.166** | . | . |
| 45 | 1.885 | 2.741 | 5.362 | 9.727 | 16.901 | 31.859 | 45.494 | 59.552 | 61.690 |
| 50 | 1.584 | 2.195 | 3.727 | 6.407 | 10.336 | 18.821 | 27.105 | 37.811 | 40.821 |
| 55 | 1.402 | 1.827 | 2.824 | 4.331 | 7.111 | 11.573 | 17.863 | 25.485 | 28.496 |
| 60 | 1.267 | 1.560 | 2.235 | 3.284 | 5.436 | 8.321 | 11.751 | 14.825 | 19.330 |

### Net P&L — 40 bp filter

| MA | 14 | 16 | 18 | 20 | 22 | 24 | 26 | 28 | 30 |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 30 | +$43,810 | +$35,328 | +$29,175 | +$24,429 | . | . | . | . | . |
| 35 | +$51,086 | +$39,932 | +$32,622 | +$26,603 | +$22,536 | . | . | . | . |
| 40 | +$56,352 | +$43,250 | +$35,699 | +$29,006 | +$24,004 | +$20,966 | +$18,567 | . | . |
| **45** | **+$60,959** | +$46,871 | +$38,654 | +$31,247 | +$25,847 | +$22,130 | +$19,453 | +$17,207 | +$15,481 |
| 50 | +$59,969 | +$48,735 | +$40,386 | +$33,117 | +$27,367 | +$23,401 | +$20,449 | +$17,955 | +$15,959 |
| 55 | +$57,158 | +$48,600 | +$41,251 | +$33,764 | +$28,550 | +$24,256 | +$21,217 | +$18,532 | +$16,414 |
| 60 | +$50,118 | +$45,311 | +$40,223 | +$33,725 | +$29,313 | +$24,933 | +$21,817 | +$18,884 | +$16,805 |

### Win rate — 40 bp filter

| MA | 14 | 16 | 18 | 20 | 22 | 24 | 26 | 28 | 30 |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 30 | 87.92% | 93.01% | 96.00% | 96.99% | . | . | . | . | . |
| 35 | 82.86% | 89.07% | 93.92% | 95.85% | 96.74% | . | . | . | . |
| 40 | 78.67% | 84.80% | 90.83% | 94.08% | 95.76% | 96.93% | **97.22%** | . | . |
| 45 | 75.78% | 81.23% | 87.64% | 91.64% | 94.20% | 95.69% | 96.40% | 97.13% | **97.64%** |
| 50 | 73.74% | 78.61% | 84.43% | 89.03% | 92.17% | 94.47% | 95.43% | 96.44% | 97.18% |
| 55 | 72.11% | 76.33% | 81.86% | 86.15% | 89.95% | 92.66% | 94.27% | 95.60% | 96.35% |
| 60 | 70.93% | 74.38% | 79.43% | 83.92% | 87.94% | 91.10% | 93.05% | 94.48% | 95.53% |

### Trip count — 40 bp filter

| MA | 14 | 16 | 18 | 20 | 22 | 24 | 26 | 28 | 30 |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 30 | 35,383 | 24,078 | 18,421 | 15,061 | . | . | . | . | . |
| 35 | 48,098 | 29,639 | 21,310 | 16,850 | 14,077 | . | . | . | . |
| 40 | 64,369 | 36,730 | 24,935 | 18,909 | 15,427 | 13,017 | 11,292 | . | . |
| 45 | 84,134 | 46,069 | 29,273 | 21,261 | 16,930 | 14,101 | 12,097 | 10,494 | 9,306 |
| 50 | 106,190 | 56,699 | 34,501 | 24,071 | 18,638 | 15,235 | 12,942 | 11,168 | 9,814 |
| 55 | 130,518 | 68,810 | 40,463 | 27,322 | 20,520 | 16,420 | 13,717 | 11,764 | 10,304 |
| 60 | 156,267 | 81,762 | 46,938 | 30,779 | 22,471 | 17,640 | 14,606 | 12,432 | 10,840 |

### Median bars held — 40 bp filter

| MA | 14 | 16 | 18 | 20 | 22 | 24 | 26 | 28 | 30 |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 30 | 4 | 3 | 3 | 2 | . | . | . | . | . |
| 40 | 8 | 6 | 4 | 3 | 3 | 3 | **2** | . | . |
| 45 | 10 | 8 | 6 | 4 | 3 | 3 | 3 | 3 | **2** |
| 50 | 12 | 10 | 7 | 5 | 4 | 3 | 3 | 3 | 3 |
| 60 | 16 | 14 | 11 | 8 | 6 | 5 | 4 | 3 | 3 |

### Per-trade net ($) after fees — 40 bp filter

| MA | 14 | 16 | 18 | 20 | 22 | 24 | 26 | 28 | 30 |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 30 | +1.238 | +1.467 | +1.584 | +1.622 | . | . | . | . | . |
| 35 | +1.062 | +1.347 | +1.531 | +1.579 | +1.601 | . | . | . | . |
| 40 | +0.875 | +1.178 | +1.432 | +1.534 | +1.556 | +1.611 | +1.644 | . | . |
| **45** | +0.725 | +1.017 | +1.320 | +1.470 | +1.527 | +1.569 | +1.608 | +1.640 | **+1.664** |
| 50 | +0.565 | +0.860 | +1.171 | +1.376 | +1.468 | +1.536 | +1.580 | +1.608 | +1.626 |
| 55 | +0.438 | +0.706 | +1.019 | +1.236 | +1.391 | +1.477 | +1.547 | +1.575 | +1.593 |
| 60 | +0.321 | +0.554 | +0.857 | +1.096 | +1.304 | +1.413 | +1.494 | +1.519 | +1.550 |

## 0 bp vs 40 bp comparison at the prior champions

| Cell | 0 bp PF | **40 bp PF** | 0 bp Net | 40 bp Net | 0 bp Trips | 40 bp Trips | 0 bp $/trade | **40 bp $/trade** |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| MA=45, trend=30 | 6.45 | **61.69** | +$46,881 | +$15,481 | 57,209 | 9,306 | +$0.82 | **+$1.66** |
| MA=30, trend=20 | 4.49 | **50.36** | +$90,677 | +$24,429 | 146,873 | 15,061 | +$0.62 | **+$1.62** |
| MA=60, trend=30 | 5.09 | 19.33 | +$43,181 | +$16,805 | 57,037 | 10,840 | +$0.76 | +$1.55 |

The 40 bp filter strips out the marginally-profitable noise where realised reversion was barely covering fees, leaving entries where the realised mean-reversion comfortably exceeds the fee budget. PF jumps **3-10×**, per-trade economics **roughly double**, win rate climbs to **96-97%** in the high-PF cells. Trip volume falls ~75% as expected — the 40 bp threshold sits well into the body of the `pct_1h_change` distribution at qualifier-satisfying bars.

## Two Pareto frontiers

The 40 bp grid splits into two operating regimes that optimise different metrics:

### High-PF / low-volume frontier (top-right of grid)

| Cell | PF | Net | Win | Trips | $/trade | Med bars |
|---|---:|---:|---:|---:|---:|---:|
| MA=40, trend=26 | **67.17** | +$18,567 | 97.22% | 11,292 | +$1.64 | 2 |
| **MA=45, trend=30** | 61.69 | +$15,481 | **97.64%** | 9,306 | **+$1.66** | 2 |
| MA=45, trend=28 | 59.55 | +$17,207 | 97.13% | 10,494 | +$1.64 | 3 |
| MA=30, trend=20 | 50.36 | +$24,429 | 96.99% | 15,061 | +$1.62 | 2 |
| MA=40, trend=24 | 48.32 | +$20,966 | 96.93% | 13,017 | +$1.61 | 3 |

These cells deliver near-perfect win rates with $1.6+ per trade. The natural-fit when capital is the binding constraint or when execution slippage is a worry — fewer trades = less slippage exposure, and the high PF means equity-curve drawdowns should be small.

### Net-P&L / high-volume frontier (top-left of grid)

| Cell | PF | Net | Win | Trips | $/trade | Med bars |
|---|---:|---:|---:|---:|---:|---:|
| **MA=45, trend=14** | 1.89 | **+$60,959** | 75.78% | 84,134 | +$0.73 | 10 |
| MA=50, trend=14 | 1.58 | +$59,969 | 73.74% | 106,190 | +$0.57 | 12 |
| MA=55, trend=14 | 1.40 | +$57,158 | 72.11% | 130,518 | +$0.44 | 14 |
| MA=40, trend=14 | 2.27 | +$56,352 | 78.67% | 64,369 | +$0.88 | 8 |
| MA=35, trend=14 | 3.06 | +$51,086 | 82.86% | 48,098 | +$1.06 | 6 |

These cells trade per-trade margin for trip volume — short trend qualifiers fire much more often, but more entries land in the marginal "barely beat fees" zone. The natural-fit when capital can absorb 5-10× the trade volume and the goal is total dollars rather than per-trade efficiency. Median hold extends to 8-14 bars (vs 2-3 at the high-PF frontier).

## Read of the 40 bp grid

The 40 bp filter encodes a **fee-aware mean-reversion margin**: only fire when the realised reversion has clear room to exceed the round-trip fee. Combined with the strict trend qualifier and the 1h-MA cover, this is now a near-perfect win-rate engine in its high-PF cells.

The two-frontier shape is real and sharp:
- **The high-PF cells at MA=40-45 / trend=24-30** are the cleanest signal in the workstream: 9-15k trips at PF 48-67 with 96-97% win rate. ~20-30 trades/day universe-wide. Per-trade $1.6+.
- **The net-P&L leaders at MA=45-55 / trend=14** chase volume: 60-130k trips at PF 1.4-2.3 with 72-83% win. ~120-260 trades/day. Per-trade $0.4-1.0.

**Default change**: engine and CLI defaults updated from 0 bp to 40 bp on both `MaxPct1hChangeForLong` (default `-0.004`) and `MinPct1hChangeForShort` (default `+0.004`). Pass `--max-pct-1h-for-long 1e9 --min-pct-1h-for-short -1e9` to revert to the legacy 0 bp behaviour.

**MA=45, trend=30 with 40 bp filter is the new v5 default**: PF 61.69, win 97.64%, +$1.66/trade. Cleanest signal in the entire workstream.

---

# Robustness audit — 2026-05-10

The v5 PF 61.69 headline begged for the standard cautionary checks: month-by-month stability, per-symbol concentration, and pre-entry liquidity. Each was performed on the trip CSVs from `donchian_v5_grid40bp/` (and a re-run with new fields, `donchian_v5_liq/`, after engine instrumentation). The findings reframe what "production cell" means.

## 1. Symbol concentration — the trend=30 problem

**MA=45 / trend=30 (the v5 champion):**

| metric | value |
|---:|---:|
| trips | 9,306 |
| **active symbols** | **30** (out of 643 universe symbols) |
| top 1 sym share of P&L | 21.4% |
| top 5 share | **81.8%** |
| top 10 share | **96.5%** |
| profitable / losing symbols | 24 / 6 |

The headline PF is essentially the **CELOUSDT + DYDXUSDT + 1000SATSUSDT + ALICEUSDT + FLOWUSDT** show — five symbols delivering 82% of P&L. The v5 champion is a niche scalp on a small mid-cap-perp core, not a universe-wide engine.

**MA=45 / trend=14 (loosened qualifier):**

| metric | value |
|---:|---:|
| trips | 84,134 |
| active symbols | **633** |
| top 5 share | 47.8% |
| top 10 share | 69.8% |
| top 50 share | 89.1% |
| profitable / losing symbols | 408 / 225 |

Loosening the trend qualifier from 30 to 14 bars takes the engine universe-wide. PF drops 61.7 → 1.89 but trips grow 9× and 633 of 643 symbols fire. The trend=20 variant sits between (PF 9.73, 21k trips, 470 active symbols, top-5 64%).

**Verdict on concentration**: trend=30's 5-symbol concentration is structural — it is what trend=30 detects, not an artifact of any other parameter. Liquidity floors don't fix it (next section).

## 2. Pre-entry liquidity — engine instrumentation

`OrderflowDonchianFade.fs` now records two new per-trip fields surfaced via [Reporting.fs](../TradingEdge.CryptoBacktest/Reporting.fs):

- `dollar_volume_1h_at_entry` — sum of `(BuyDollarVolume + SellDollarVolume)` over the trailing `nShortRef` bars ending the bar BEFORE entry. USDT-denominated.
- `trade_count_1h_at_entry` — sum of `bar.TradeCount` over the same window.

Both are pre-push reads (do not include the entry bar itself). The 1h here is the `--short-ref-minutes` setting, defaulting to 60 (production runs use 45 for the v5 MA).

### Distribution at entry (trend=20)

| stat | dollar_volume_1h | trade_count_1h |
|---:|---:|---:|
| p5 | $21,214 | 281 |
| p25 | $91,430 | 617 |
| p50 | $211,060 | 1,083 |
| p75 | $514,271 | 2,005 |
| p95 | $2,232,639 | 7,096 |

**Median dv_1h × 24 / adv_at_entry = 0.29.** The system fires into hours that are roughly **30% of typical-hour volume** — i.e. **lull windows, not spike windows.** This is the central liquidity finding: a Donchian channel break in a quiet hour means somebody actually decided to move price (no random-noise crossings), and the rebound to MA is reliable.

### Decile breakdown (trend=20, by dollar_volume_1h_at_entry)

| decile | dv_1h range (USDT) | win % | PF | net | avg/trade |
|---:|---:|---:|---:|---:|---:|
| 1 | $3 – $40k | 97.5 | **48.6** | $3,343 | $1.57 |
| 2 | $40k – $74k | 96.0 | 44.9 | $3,250 | $1.53 |
| 5 | $154k – $211k | 93.4 | 16.9 | $3,122 | $1.47 |
| 7 | $291k – $419k | 91.1 | 9.5 | $2,929 | $1.38 |
| 9 | $645k – $1.26M | 87.6 | 5.4 | $2,623 | $1.23 |
| 10 | $1.26M – $392M | 80.1 | **3.8** | $3,771 | **$1.77** |

Monotonic PF decline from quiet → busy 1h. Decile 1 has the strongest PF (clean reverts), decile 10 has the **highest avg/trade** (busier markets accept larger edges). **All deciles profitable.** The per-trade payoff stays remarkably stable around $1.4-1.8 across all 10 deciles — only the win-rate (and therefore PF) erodes with volume.

### Liquidity-floor PF/concentration tradeoff

**trend=20 floor cost:**

| floor | trips | PF | net | top5 share |
|---:|---:|---:|---:|---:|
| no floor | 21,261 | 9.73 | $31,247 | 64.1% |
| ≥ $250k | 9,540 | 5.89 | $13,653 | 63.9% |
| ≥ $500k | 5,447 | 4.66 | $7,970 | 63.1% |
| ≥ $1M | 2,713 | **3.81** | $4,427 | **51.6%** |

The same 5 symbols dominate at every liquidity threshold. Filtering on dv_1h doesn't change which symbols carry the strategy — those symbols just have more trips. The concentration is the strategy.

**trend=14 floor cost:**

| floor | trips | PF | net | avg | top5 share |
|---:|---:|---:|---:|---:|---:|
| no floor | 84,134 | 1.89 | $60,959 | $0.72 | 47.8% |
| ≥ $250k | 54,257 | 1.62 | $34,970 | $0.64 | 45.0% |
| ≥ $500k | 38,974 | 1.56 | $25,896 | $0.66 | 38.5% |
| **≥ $1M** | **25,887** | **1.57** | **$20,227** | **$0.78** | **25.5%** |
| ≥ $2M | 16,291 | 1.69 | $17,529 | $1.08 | – |

trend=14 ≥ $1M shows the textbook desired behaviour: top-5 collapses from 48% → 25.5%, top-10 from 70% → 32%, **431 of 624 active symbols are profitable** (69%). PF holds at 1.57 with avg/trade $0.78 — meaningful after fees on $1k notional.

**Verdict on liquidity**: trend=14 ≥ $1M is the deployable production cell. Lower per-trade payoff than trend=20 but with a genuinely diversified symbol distribution and sufficient liquidity to size up.

## 3. Sizing-signal study (trend=20)

Decile-stratified by three independent variables on the same trip set; PF spread between bottom and top decile listed:

| signal | bottom-decile PF | top-decile PF | spread |
|---|---:|---:|---:|
| `bars_since_violation` (run length) | 6.6 | **137** | **20×** |
| `dollar_volume_1h_at_entry` (inverse) | 3.8 | 48.6 | 13× |
| `abs(pct_1h_change)` (distance to MA) | 9.6 | 14.0 | 1.5× |
| `trade_count_1h_at_entry` (inverse) | 2.8 | 182 | 65× |

`bars_since_violation` and `trade_count_1h_at_entry` carry the strongest single-axis edge. All four are roughly monotonic and largely independent (verified via 5×5 joint cubes — bottom-right `tc_q=5 × pct_q=5` cell is PF 6.7, avg $3.49, the highest-payoff corner despite being the busiest-traffic quintile).

**Production sizing implication**: a multiplicative scaler in `openPos` of the form

```
notional × f(bars_since_violation) × g(abs(pct_1h_change)) × h(dv_1h_at_entry)
```

would up-size the cleanest setups (long trend run, far from MA, in a quiet hour) and down-size the marginal ones, lifting the realised per-trade payoff materially. Order of priority: `bars_since_violation` first (20× spread), then `dv_1h_at_entry` and `trade_count_1h_at_entry` (capacity tradeoff), then `abs(pct_1h_change)` as a secondary multiplier.

## 4. Month-by-month stability

**trend=14:** every month from 2024-05 through 2026-04 fires 1.4k–7.5k trips across 200+ symbols. Only one losing month: **2025-06 (PF 0.81, -$710)**. Recovers immediately.

**trend=20:** same shape, fewer trips per month (300–3,400). Only losing months: 2025-01 (PF 0.84, -$5) and 2025-06 (PF 0.44, -$287).

**The recent ramp is real.** Trip counts at trend=20 climb 319 (2025-09) → 755 → 1,196 → 2,411 → 2,559 → 2,992 → 3,403 (2026-03). Roughly 10× growth in 6 months. PF holds high through the ramp (14.3, 24.2, 23.8). The setups are getting **more frequent AND staying high-PF** — consistent with a market regime change on Binance perps, not a parameter overfit.

**The 2025-06 universal down month** appears at every trend variant (14/20/30) and warrants a focused breakdown before sizing capital — it's the one regime moment the strategy genuinely misfired. Not yet investigated.

## Production recommendation revised

The v5 champion (MA=45 / trend=30 / 40 bp filter, PF 61.69) **stays in the doc as the high-PF Pareto endpoint** but is no longer the recommended production cell. It works, but only on 30 symbols with 5 of them carrying 82% of P&L — that's a niche scalp, not a portfolio strategy.

**Production candidate: MA=45 / trend=14 / 40 bp filter / dv_1h_at_entry ≥ $1M**:
- 25,887 trips over 2 years (35/day across 624 active symbols)
- PF 1.57 — modest but credible
- avg/trade $0.78
- top-5 share 25.5%, top-10 32% — actually diversified
- 431 profitable / 193 losing of 624 active symbols (69% profitable)
- liquidity at entry ≥ $1M USDT/hr — sufficient for meaningful notional
- the three sizing signals (`bars_since_violation`, `abs(pct_1h_change)`, `dv_1h_at_entry`) are all available for dynamic notional scaling

The trend=20 variant (PF 9.73, 21k trips, 470 active symbols) is the **middle Pareto cell** — accept it as a research benchmark but not a production target without explicit acceptance of the symbol concentration.

## Engine changes

[OrderflowDonchianFade.fs](../TradingEdge.CryptoBacktest/OrderflowDonchianFade.fs):
- New `DonchianRoundTrip` fields: `DollarVolume1hAtEntry`, `TradeCount1hAtEntry`.
- New rolling aggregate: `tradeCountShortMa = SumMa(nShortRef)`.
- `volShortMa` and `volLongMa` now sum `(BuyDollarVolume + SellDollarVolume)` per bar instead of `bar.Volume` — recorded values are USDT, comparable across symbols.

[Reporting.fs](../TradingEdge.CryptoBacktest/Reporting.fs):
- `donchianTripsHeader` extended with `dollar_volume_1h_at_entry,trade_count_1h_at_entry`.

Trip CSVs from prior sweeps lack these fields. To regenerate, re-run the sweep with the same flags — engine logic is unchanged so trip counts match (`MA=45 / trend=20`: 21,261 trips both before and after instrumentation).

---

# Limit-fill simulator audit + 2025-10 attribution — 2026-05-10 PM

The square-root market-impact estimate said DonchianScalp would not survive market-order execution at any meaningful notional. The natural response was to test limit-order execution: **post a maker limit at the trip's recorded EntryPrice, trail per a 1-second-bar rule, replay against the trade tape**. New module: [LimitFillSim.fs](../TradingEdge.CryptoBacktest/LimitFillSim.fs). New CLI: `limit-fill-sim`. Architectural reference is the Orb [FillSimulator](../TradingEdge.Orb/Pipeline.fs) (lines 536-732) — three-phase per-trip state machine (entry-trail / passive-hold / exit-trail), latency-deferred price/qty updates via a Nito Deque, partial-fill accumulation, queue-position rejection, maker fees.

## Configuration used

| dial | value |
|---|---|
| trail mode | RePeg (limit follows each closed 1s bar's Low for buy-limits / High for sell-limits; can move adverse) |
| maker fee | 0.0002 (= 2 bp = Binance USDT-perps base tier, per side) |
| rejection rate | 0.30 (queue-position proxy: ~30% of crossing trades go to makers ahead of us) |
| latency | 100 ms per placement / repricing |

## Run target

The trend=14 / dv≥$1M cell from the robustness audit above — the cell that looked like the deployable production target. 25,887 trips, 624 active symbols. Sim wall: 41 min @ -p 8.

## Fill quality

| metric | value |
|---|---|
| trips | 25,887 |
| no-fill | 8 (0.03%) |
| partial fill | 153 (0.6%) |
| full fill | 25,726 (99.4%) |
| avg entry fraction | 99.66% |
| avg held seconds | 977 (~16 min) |
| trips with residual | 2 |

The dv≥$1M floor is liquid enough that fills are not the binding constraint. Almost every trip filled fully on both sides.

## Headline result — engine market-fill vs limit-fill

| metric | engine | limit-fill |
|---|---:|---:|
| trips | 25,887 | 25,887 |
| net P&L | $20,227 | $9,611 |
| avg / trade | $0.78 | $0.37 |
| PF | 1.57 | 1.25 |

**Limit-fill captures 47.5% of engine P&L while keeping PF at 1.25.** That's much better than the square-root market-impact estimate predicted — the limit-fill strategy is genuinely a maker edge, not a fee-and-impact-eating market chase.

## Per-side asymmetry — the first warning sign

| side | trips | engine net | limit net | PF (limit) | retention |
|---|---:|---:|---:|---:|---:|
| long | 14,578 | $18,383 | $12,074 | 1.55 | 65.7% |
| short | 11,309 | $1,844 | -$2,464 | 0.85 | -134% |

**The short side destroys $4,308 of P&L vs engine baseline.** The pattern is adverse selection on the maker side: when our short-side limit at the engine's EntryPrice gets filled, price tends to keep running up afterward. The engine's market-fill avoided this because it filled at bar.Close before price could run; the limit waits, RePeg moves the limit upward chasing the bar's High, and we end up entering at progressively worse prices precisely when the move is genuinely against us.

The natural remedy is **`--trail-mode ratchet`** (limit only moves favorable). Tested separately; even so, the short side stays structurally weak under maker execution.

## Long-only at higher dv floors

| slice | trips | engine | limit | retention | lim_avg | lim_pf |
|---|---:|---:|---:|---:|---:|---:|
| all sides, no floor | 25,887 | $20,227 | $9,611 | 47.5% | $0.37 | 1.25 |
| long, dv≥$1M | 14,578 | $18,383 | $12,074 | 65.7% | $0.83 | 1.55 |
| long, dv≥$2M | 9,196 | $16,967 | $14,129 | 83.3% | $1.54 | 1.94 |
| **long, dv≥$5M** | **4,580** | **$12,510** | **$11,753** | **94.0%** | **$2.57** | **2.39** |
| long, dv≥$10M | 2,469 | $6,621 | $6,406 | 96.8% | $2.60 | 2.23 |

At dv≥$5M long-only, limit-fill captures 94% of engine P&L with PF 2.39 / $2.57 per trade. By the surface metrics this is a deployable cell.

## The 2025-10 single-month attribution — the deal-breaker

October 2025 contributed:

| slice | total | October | rest |
|---|---:|---:|---:|
| **engine net** | $20,227 | $16,597 (82.1%) | $3,630 (PF 1.11) |
| **limit net** | $9,611 | $15,786 (164.3%) | -$6,176 (PF 0.83) |
| **long, dv≥$2M, limit** | $14,129 | $16,412 (116%) | -$2,283 (PF 0.84) |

**The strategy's reported P&L is overwhelmingly a single calendar month.** Even at the engine's market-fill version, ex-October PF is 1.11 — barely above break-even and well below any execution-drag survival threshold. Under limit-fill, ex-October P&L is **negative**.

Per user knowledge: October 2025 had a market-wide liquidation event where most altcoins flushed ~50% in half an hour and recovered almost immediately. **DonchianScalp is structurally a vol-flush fade engine** — it fades extreme channel breaks and catches the rebound to MA. Of course it printed money during a universal liquidation event; the rest of the time those setups don't fire as cleanly because the moves don't follow the same path.

## Reclassification

DonchianScalp is **not** a daily-bread mean-reversion strategy. It is a **rare-vol-event fade engine** of the same family as `OrderflowExtremeRvol`. The 2-year backtest looked smooth because the universe-wide trip aggregation hid the regime concentration; the month-by-month and per-symbol audits make it visible.

This means:
- The earlier "production candidate: trend=14 / dv≥$1M / 40 bp filter" recommendation **is withdrawn**. Headline metrics on that cell are 80%+ October 2025.
- The strategy stays in the codebase as an event-fader, alongside ExtremeRvol. Rule of engagement: only fire when universe-wide vol expansion + cross-symbol drawdown signal indicates a flush is in progress. Such a regime detector does not yet exist.
- The mean-reversion thesis remains correct — channel breaks during vol flushes do revert to MA. The thesis just doesn't apply to ordinary days.

## Ratchet trail mode — conclusively worse than RePeg

For completeness, the same 25,887 trips were re-run with `--trail-mode ratchet` (limit only moves favorable). The hypothesis was that ratchet would mitigate the adverse-selection drag visible on shorts under RePeg. **It did the opposite.**

| variant | total P&L | per-trade | PF |
|---|---:|---:|---:|
| engine | $20,227 | $0.78 | 1.574 |
| limit RePeg | $9,611 | $0.37 | 1.251 |
| **limit Ratchet** | **$5,431** | **$0.21** | **1.135** |

| ex-October | total P&L | per-trade | PF |
|---|---:|---:|---:|
| engine | $3,630 | $0.15 | 1.112 |
| limit RePeg | -$6,176 | -$0.25 | 0.826 |
| **limit Ratchet** | **-$10,018** | **-$0.41** | **0.732** |

Ratchet loses an extra $4.2k overall vs RePeg, and another $3.8k ex-October. Both sides:

| side | RePeg | Ratchet |
|---|---:|---:|
| long | PF 1.55 / +$12,074 | PF 1.42 / +$9,569 |
| short | PF 0.85 / -$2,464 | PF 0.76 / -$4,138 |

**Mechanism**: this is a mean-reversion fade. When long, we want price to come back up — the bar.Low rising means the rebound is starting. RePeg follows the rebound; Ratchet refuses to lift the limit and waits for an even lower price that may never come. Net result: ratchet captures less of the rebound and exits later at worse prices on average. RePeg is the correct rule for this strategy.

The ratchet experiment is closed: **don't use ratchet for fade-style mean-reversion**. RePeg is the right default.

## What this audit does NOT close

- **FlowSwing (LongFadeMA)** audit run separately on the same day; results in [docs/donchian_fade_v0_results.md](docs/donchian_fade_v0_results.md) under the FlowSwing section (or a separate doc). Headline: 11.4% October attribution (vs DonchianScalp's 82%), PF 3.33 ex-October — the production strategy.
- **Regime detector** for DonchianScalp. If we can build a pre-event signal — e.g. cross-universe simultaneous-drop + funding-rate dislocation — the strategy becomes "fire only when regime is on" and may yet be deployable. Not pursued in this audit. The user's plan is forward-collection of L2 data on cloud VMs, replaying once we have a month of book history; book imbalance is the natural feature for that signal.

---

# 2D PF surface — bars_since × abs(pct_1h_change), MA=60 / dv≥$1M

For dynamic-sizing in a live system, we need PF as a function of the entry's signal characteristics — primarily `bars_since_violation` (the trend-run length) and `abs(pct_1h_change)` (distance from the 1h MA). The live system would observe both at signal time, look up the PF cell, and size proportionally — skip cells with PF < 1, size up cells with PF >> 1.

## Sweep design

To populate every (bars_since, abs_pct_bp) cell with non-overlapping data, we ran [donchian-fade-sweep](../TradingEdge.CryptoBacktest/Program.fs) at `MA=60 / trend ∈ {10..30} step 1` and pooled the trips by extracting only rows where `bars_since == trend_floor` from each trend-N CSV. This isolates the population that fired at exactly bars_since = N (rather than the rare leftovers from filtered-out lower-trend triggers), giving a unique-per-event distribution across the full bs range without double-counting.

Filter: `dollar_volume_1h_at_entry >= $1M` (production liquidity floor). Pool: 21 trend values × ~600 symbols × 2 years.

Source CSVs: `data/crypto/donchian_v5_liq_ma60_trend10to40/backtest_results_trips_1m_don3_trend{10..40}.csv`. Pooled output: `data/crypto/donchian_v5_liq_ma60_trend10to40/2d_pf_pooled_dv1m.csv`.

## Trip count matrix

```
                   abs(pct_1h_change) [bp]
bars_since   40-50  50-70  70-100 100-150 150-250  250+
10-11        92727 120022   91534   61915   35780  19776
12-13        44590  55054   40794   27401   15302   8846
14-16        25689  31463   22024   13940    7981   4765
17-20        10067  11351    7403    4391    2407   1528
21-25         3555   3683    2085    1109     468    405
26-30         1328   1199     553     285      92     94
```

## PF matrix (engine market-fill)

```
                   abs(pct_1h_change) [bp]
bars_since   40-50  50-70  70-100 100-150 150-250  250+
10-11         0.78   0.81   0.85    0.82    0.89   1.60   ← floor row mostly negative
12-13         0.79   0.82   0.81    0.79    0.92   2.58
14-16         0.89   0.91   0.85    0.79    0.96   4.38
17-20         1.03   1.04   0.94    1.07    1.25   6.26
21-25         2.24   1.76   1.56    1.87    1.49   7.63
26-30         4.59   4.26   5.62    7.60    3.30   2.68   ← top corner unstable due to small N
```

## Avg P&L per trade matrix ($, on $1k notional)

```
                   abs(pct_1h_change) [bp]
bars_since   40-50  50-70  70-100 100-150 150-250  250+
10-11        -0.31  -0.31  -0.29   -0.45   -0.31   2.96
12-13        -0.29  -0.29  -0.37   -0.52   -0.21   6.58
14-16        -0.13  -0.13  -0.28   -0.51   -0.10  12.00
17-20         0.03   0.05  -0.09    0.11    0.48  15.94
21-25         0.65   0.56   0.54    0.90    0.81  16.71
26-30         1.02   1.12   1.46    2.00    2.08   8.11
```

## Reading the surface

**Two separate edges combine:**

1. **Distance from MA carries an edge even at the trigger floor**. The bs=10-11 / 250+bp cell is **PF 1.60** with avg +$2.96/trade on **19,776 trips**. A live system firing only when both conditions are met (bars_since at minimum AND distance from MA > 250bp) would have a real edge from the lowest possible trigger.
2. **Bars_since dominates at moderate distances**. At 50-70bp, going from bs=10-11 (PF 0.81) to bs=21-25 (PF 1.76) is a 2× PF improvement. The strategy effectively wants either a very-extended trend OR a far-from-MA entry; both at once is the strongest signal.

**The losing cells are concentrated in the bottom-left**: bs=10-16 with abs_pct < 150bp. These represent ~80% of all signal events under the trend=10 floor and are where the engine's broad-aggregate PF drag comes from. A live system that **skips this region entirely** and only fires the positive-PF cells would dramatically improve the apparent strategy quality — at the cost of trip volume.

**The top-right corner (bs=21-30 / abs_pct > 100bp) is the goldmine**. Cells like bs=21-25 / 250+bp at PF 7.63 (405 trips, +$16.71/trade) and bs=26-30 / 100-150bp at PF 7.60 (285 trips, +$2.00/trade) are the cells worth size-multiplying in a live system.

**The top-row 250+bp cell (PF 2.68, only 94 trips) is unstable** — too few losers to compute a stable PF. Don't read too much into the very-high-bars / very-far-from-MA corner without more data.

## How to apply for live sizing

Production rule: at signal time, observe `bars_since` and `abs(pct_1h_change)` for the candidate trade, look up the PF cell:

- **PF < 1.0**: skip. Per-trade EV is negative; not worth the spread cost.
- **PF 1.0-1.5**: minimum size. Edge exists but small.
- **PF 1.5-3.0**: standard size.
- **PF > 3.0**: scale up. These are the cells where the strategy historically delivered the bulk of per-trade payoff.

The 3rd dimension (`dollar_volume_1h_at_entry` decile) was deferred from this 2D table for readability but exists in the raw trip data for the eventual live-system 3D sizing function. The pooled CSV at `data/crypto/donchian_v5_liq_ma60_trend10to40/2d_pf_pooled_dv1m.csv` contains the cube data.

## Caveats

- **bs=26-30 / 250+bp is small (94 trips)** — PF 2.68 is unstable. Either need more data or accept the cell will down-size in production until it has more samples.
- **The 2D table is built on engine market-fill PF**, not limit-fill. Per the limit-fill audit above, DonchianScalp loses substantially under maker execution (PF 1.25 overall vs 1.57 engine, with shorts losing money). The PF cells in this table need to be re-derived under the limit-fill simulator before the sizing function is final. The 2D structure (bottom-left bad, top-right good) is expected to survive the transformation, but the absolute PF values will compress.
- **MA=60 here**, not the v5-champion MA=45. The v5 champion was tuned for the high-PF / low-volume Pareto endpoint, which this 2D surface dilutes by including the much larger bs=10 floor population. A separate 2D table at MA=45 would be slightly different but the qualitative shape should hold.

---

## v6 — Taker-fill simulator on the v5 cell

Run date: 2026-05-11. The earlier limit-fill audit (DonchianScalp section above) showed limit-order execution **destroys** the strategy's edge — trailing a limit through a fast-moving bar isn't how a live system would actually execute these scalps. The trade-set is structurally **taker-side**: when a 1m bar signal fires, real execution lifts the offer (or hits the bid) within the next few seconds.

The taker-fill simulator (per [docs/taker_fill_simulator.md](./taker_fill_simulator.md)) models that honestly: for each entry and exit signal, gather same-side aggressive trades in `[signal + 200ms, signal + 3s]`, **scratch** the signal if fewer than 10 same-side trades land in that window (the liquidity gate), otherwise fill at a size-and-exponential-time-weighted VWAP with a 5 bp taker fee on each side. The DonchianScalp engine emits an `exit_reason` column (`normal` / `redenomination` / `endofstream`); the simulator skips degenerate exits.

The simulator runs in the cloud-on-WSL setup at ~35 (symbol, date) pairs/sec at parallelism=8. 84,134 trips at MA=45/trend=14 generated 168,260 side-jobs across 41,927 unique (symbol, date) pairs and completed in 18.5 min.

### Headline result — MA=45, trend=14 (v5 production cell)

| Metric | Engine (market fill) | **Taker-fill sim** | Note |
|---|---:|---:|---|
| Total trips | 84,134 | 84,130 | (4 redenom/eos excluded) |
| Both-filled | n/a | **5,846 (6.95%)** | scratched by liquidity gate otherwise |
| PF | 1.89 | **1.77** | -6% from taker spread |
| Per-trade net | $0.73 | **+$1.63** | filtered to liquid signals |
| Total net P&L | +$60,959 | **+$9,503** | 15.6% of headline |
| Wins / Losses | n/a | 3,950 / 1,896 | 67.6% win rate |

The strategy's edge **survives realistic taker execution**. Three points worth highlighting:

1. **PF holds.** A drop from 1.89 → 1.77 with 5 bp × 2 fees applied confirms the win/loss asymmetry is genuine, not a fill-modelling artifact. Profit factor of 1.77 on a 1-minute mean-reversion scalp is a tradable edge.

2. **Per-trade payoff more than doubles.** The taker-fill set delivers **+$1.63/trade vs the engine's $0.73**, because the `n_min ≥ 10` liquidity gate naturally selects the more-liquid signals where mean reversion is also more reliable. The unfilled 93% of signals were predominantly the marginal "barely beat fees" entries that drag the engine's per-trade number down.

3. **Total dollars: 15.6% of headline.** This is the honest read on the strategy's economic capacity. The strategy fires often on illiquid signals at the bar-close moment; only ~7% of those produce enough same-side flow in the next 3 seconds to be executable. At $1k notional that's **+$9,503 over 2 years** on the production cell — modest, but real, and scales linearly with notional until impact starts mattering.

### Why the fill rate is only 7%

The strategy fires on the **bar close** of every minute that satisfies the trend qualifier. At the 1-minute granularity, most bar-closes don't coincide with a burst of taker flow on the relevant side. A bar where price dipped 40 bp below the 1h MA (long-entry filter) might have had aggressive sellers two seconds before the close, but the close itself often sits in a quiet pocket.

The 7% fill rate isn't a bug — it's the simulator surfacing what would actually happen live. Live execution would either (a) fill on these 7% and skip the rest (the simulator's read), or (b) accept worse spread to chase the unfilled 93%, which would degrade per-trade economics into the engine's $0.73 territory. Option (a) is strictly better: smaller account requirement, fewer trades, higher PF.

### Tradeable interpretation

At PF 1.77 and ~6 fills/day universe-wide (5,846 over 2 years), this is a **trickle-flow scalp**: not a primary income system, but a clean +EV stream that compounds with other strategies in a multi-engine book. The natural production setup:

- Capital required: low (per-trade notional $1k, max ~10 concurrent positions = $10k).
- Execution complexity: trivial — single taker market order, no inventory management.
- Expected daily P&L at $1k notional: ~$13/day (small, but it's the floor — scale notional by 10× while still under any impact concerns).

The next step is **live paper-trading on Binance testnet** to confirm the simulator's predictions hold against real exchange execution. The simulator's `n_min` gate is a key parameter to validate — live fills can fail for reasons the trade tape can't see (queue jumps, last-look rejections), and a real-money paper-test will reveal whether 7% fill rate is too optimistic or pessimistic.

### Data + reproducibility

- Trip CSV: `/tmp/tk_v5/results_trips_1m_don3_trend14.csv` (84,134 rows including 4 non-normal exit_reason). Generated via `donchian-fade-sweep --entry-mode trend-only --cover-mode ma-cross-cover --short-ref-minutes 45 --min-trend-bars 14` with all production defaults (ADV gates, vol-target, gap detector).
- Fill CSV: `/tmp/tk_v5/taker_fills_v2.csv` (84,130 rows after exit_reason filter, each with 12 taker-fill columns appended).
- Run command: `taker-fill-sim --trips-csv ... --output-csv ... -p 8` with all defaults (t_skip=200ms, w_max=3s, n_min=10, tau=500ms, taker_fee=0.0005).

---

## v7 — Cum-volume fill mode + 5m liquidity stratification

Run date: 2026-05-11 (same session as v6).

The v6 time-EWMA mode has a structural flaw: the `n_min=10` gate uses trade COUNT, not VOLUME. At $1k notional on a typical mid-cap perp, our order's base-qty is a few hundred to a few thousand coins — most signals are easily covered by the first 1-3 same-side aggressive trades, but `n_min=10` forces us to wait until 10 trades have crossed, biasing the simulator toward periods of high trading activity (which aren't necessarily the periods when our order would clear). EWMA's exponential time-weighting compounds this — it averages trades that happened seconds after the moment our order would have actually filled.

v7 introduces **cumulative-volume fill mode** (`--fill-mode cum-volume`, now the default). For each signal:

1. Walk same-side aggressive trades chronologically.
2. Accumulate base-asset quantity (`trade.Quantity`) — NOT dollar volume. Entry and exit on a round-trip must clear the same base qty (the engine's `effective_notional / entry_price`); using dollar volume would mismatch sides as price drifts.
3. Stop when cumulative qty reaches `cv_multiplier × target_qty` (default `cv=3`, a 3× safety margin over the literal order size).
4. Fill price = VWAP of those trades (`Σ price·qty / Σ qty`).
5. SCRATCH if the threshold isn't reached within `w_max` (default extended from 3s → 10s — more headroom for thinner symbols).

The window default was bumped to 10s to give cum-volume mode time to accumulate flow on slow-tape symbols where the strategy's better setups live.

### v6 vs v7 universe-wide (MA=45, trend=14)

| Mode | Both-filled | Avg n_trades (entry/exit) | PF | Avg/trade | Total |
|---|---:|---:|---:|---:|---:|
| **v6 EWMA** (3s, n_min=10, τ=500ms) | 5,846 / 6.95% | 70 / 88 | 1.77 | +$1.63 | +$9,503 |
| **v7 CumVolume** (10s, cv=3) | 11,243 / 13.4% | 13 / 13 | 1.51 | +$0.92 | +$10,295 |

CumVolume nearly doubles the fill rate (most fills clear the 3× qty threshold within 1-5 trades — the average is 13 in the wider 10s window, but the median fill happens much sooner). Headline P&L is **flat** at ~$10k, but PF compresses from 1.77 → 1.51 because the additional 5,400 filled trips are predominantly the lower-edge tail. Per-trade economics halve from $1.63 → $0.92.

### Cum-volume math correctness (audit)

A representative CRVUSDT short trip was traced by hand against the raw trade tape to confirm the simulator math:

- Entry signal at $0.322, target_qty = $441.37 / $0.322 = 1370.7 base, cv=3 threshold = 4112 base.
- Same-side seller-aggressive trades in the 10s window: qty 87.7 + 1799.1 + 2324.0 = **4210.8** (threshold hit on trade 3 at $0.321).
- VWAP = $0.321. Reported entry_fill_price = $0.321. ✓
- Symmetric exit calculation lands at $0.323 (2 trades). ✓
- P&L with 5bp fees and `target_qty = effective_notional / entry_eff` matches reported value to 0.001. ✓

The math is correct; the lower PF is a real selection-shift effect, not a bug.

### 5m pre-entry liquidity stratification (the key finding)

The engine was extended with two new instrumentation fields: `dollar_volume_5m_at_entry` and `trade_count_5m_at_entry` — sums over the trailing 5 bars ending the bar BEFORE entry (independent of the configurable cover-MA window).

Stratifying the v7 cum-volume fill outcomes by `trade_count_5m_at_entry` quintile (universe-wide, MA=45/trend=14):

| Quintile | tc5m range | Total trips | Filled | Fill % | PF | Avg/trade | Total |
|---:|---|---:|---:|---:|---:|---:|---:|
| 1 | 5 - 67 | 16,826 | 56 | 0.3% | 0.02 | -$0.76 | -$42 |
| 2 | 67 - 153 | 16,826 | 109 | 0.6% | 0.23 | -$0.86 | -$94 |
| 3 | 153 - 315 | 16,826 | 344 | 2.0% | 0.32 | -$0.77 | -$265 |
| 4 | 316 - 856 | 16,826 | 1,140 | 6.8% | 0.54 | -$0.63 | -$719 |
| **5** | **857 - 348,661** | **16,826** | **9,594** | **57.0%** | **1.64** | **+$1.19** | **+$11,416** |

**This is a clean monotone result.** Every quintile below Q5 has PF < 1.0 — they bleed money. **Q5 alone (trade_count_5m ≥ 857) delivers 111% of cv-mode total P&L** ($11,416 vs cv-mode universe $10,295), at higher PF (1.64 vs 1.51) on 85% of cv-mode's fills.

The dollar-volume stratification is similar but slightly weaker (dv5m ≥ $140k → PF 1.63 / $10,771 / 9,223 fills). Trade count is the cleaner signal.

### Inside Q5: PF holds across the high-liquidity range

Splitting Q5 (tc5m ≥ 857) into its own quintiles:

| Q5 sub-q | Trips | Fill % | PF | Avg/trade | Total |
|---:|---:|---:|---:|---:|---:|
| 1 (lowest tc5m of Q5) | 3,365 | 24% | 2.30 | +$1.74 | +$1,417 |
| 2 | 3,365 | 34% | 1.67 | +$1.01 | +$1,164 |
| 3 | 3,365 | 49% | 1.94 | +$1.59 | +$2,626 |
| 4 | 3,365 | 71% | 1.45 | +$0.83 | +$1,986 |
| 5 (highest tc5m) | 3,365 | 95% | 1.51 | +$1.12 | +$3,575 |

The strongest per-trade edge is **at the threshold itself** (sub-q1: PF 2.30, +$1.74/trade) — meaning trades that *just barely* qualify on liquidity behave best. PF compresses modestly toward higher liquidity. Fill rate climbs monotonically from 24% → 95%.

The interpretation: once a signal passes the liquidity gate at all, more liquidity adds *fills* but doesn't add *edge per fill*. The strategy doesn't need busy tape to work — it just needs enough tape to execute.

### EWMA vs CV under the 5m gate

| Mode | Filter | Fills | PF | Avg/trade | Total |
|---|---|---:|---:|---:|---:|
| EWMA (v6) | none | 5,846 | 1.77 | +$1.63 | +$9,503 |
| EWMA | tc5m ≥ 857 | 5,714 | 1.78 | +$1.67 | +$9,525 |
| CV (v7) | none | 11,243 | 1.51 | +$0.92 | +$10,295 |
| **CV** | **tc5m ≥ 857** | **9,594** | **1.64** | **+$1.19** | **+$11,416** |

EWMA's own `n_min=10` requirement is already strongly correlated with high tc5m — adding the explicit filter barely changes EWMA's result (-132 fills, +$22). EWMA-mode is implicitly filtering by tc5m already, just opaquely.

CV-mode + explicit tc5m filter is the cleanest production setup:
- **All EWMA's PF discipline**, plus
- **~68% more filled trips** (9,594 vs 5,714), and
- **~$1,900 more total P&L** over 2 years.

### Interpretation

The 5m pre-entry trade count is a **fill-quality predictor** that's also computable in real time without the trade tape — it's a bar-aggregate statistic the engine has natively. A live-trading filter `tc5m ≥ 850` would:
1. Reject ~80% of v5's headline trip count as "won't fill cleanly" before any order is placed.
2. Among the surviving 20%, achieve **57% fill rate** and **PF 1.6+**.
3. Capture **all the strategy's net P&L** — and then some, by avoiding the negative-edge drag from the bottom 4 quintiles.

This finding survives both fill modes (EWMA + CV) and both stratification fields (dv5m, tc5m). The pattern is real.

### Data + reproducibility

- New sweep trip CSV (with 5m fields): `/tmp/tk_v5_5m/results_trips_1m_don3_trend14.csv`.
- v7 fill CSV: `/tmp/tk_v5/taker_fills_cv.csv` (84,130 trips, 12 taker-fill columns appended).
- v7 run: `taker-fill-sim --trips-csv ... --output-csv ... --fill-mode cum-volume -p 8` with all other defaults (t_skip=200ms, w_max=10s, cv=3.0, taker_fee=0.0005).
- Stratification queries: DuckDB joins on `(symbol, entry_us)` between fill CSV and new sweep CSV.

---

## v8 — Lookahead-free taker fill, regime-dependence reconfirmed

Run date: 2026-05-11 (same session as v6/v7).

v7's `cum-volume` fill mode still scratched any signal that didn't accumulate enough same-side flow within the window — that's a **lookahead** decision. A live trader cannot un-send an order at second 4 because the next-60-second flow was insufficient. The realistic model is: place the order, take whatever VWAP the next window's same-side flow gives you, regardless of how thin that flow is.

v8 changes the cum-volume mode to **always fill**: if cumulative qty hits `cv_multiplier × target_qty`, stop accumulating and fill at that point (the realistic execution); otherwise fill at the VWAP of ALL same-side trades in the window. The only Scratched outcome is now when there are literally **zero** same-side trades in the entire window — no order could have filled at all.

The window default was extended from 10s → **60s** to give thin symbols enough room that "all available same-side flow" lands on a meaningful average rather than a single degenerate print. The cross-midnight bug discovered in this rewrite (a 60s window from a bar closing seconds before midnight straddles into the next-day parquet) is fixed by bucketing on the bar's **clock-end** (= signal_us ÷ bucketUs + 1, rounded up) rather than `signal_us` itself — since `bar.EndUs` is the bar's last-trade timestamp, and the [last-trade, clock-end) sub-interval has zero trades by construction, anchoring on the next-minute boundary correctly assigns the entire fill window to the day that holds the post-bar tape.

### Universe-wide v8 result (MA=45, trend=14)

| Version | Fills | PF | Avg/trade | Total | Note |
|---|---:|---:|---:|---:|---|
| Engine bar-close (v5) | 84,134 | 1.89 | $0.73 | +$60,959 | unrealistic — assumes free bar-close fills |
| v6 EWMA (lookahead) | 5,846 | 1.77 | $1.63 | +$9,503 | `n_min=10` is a hidden lookahead gate |
| v7 CV with scratch (lookahead) | 11,243 | 1.51 | $0.92 | +$10,295 | scratch decision uses future window flow |
| **v8 CV, no-lookahead, 60s** | **75,148** | **0.59** | **-$0.60** | **-$45,343** | every signal must take whatever flow appears |

**Unconditional v8 is a $45k LOSS over 2 years.** The previous "tradable" headlines (PF 1.5-1.8) were all selection effects from the scratch/n_min gates filtering out trips whose 60-second post-signal flow happened to be unfavorable — precisely the cases that would have been losing live trades.

### Regime-dependence: the entire edge is one day

Partitioning the universe-wide v8 fills by date reveals that **>100% of the strategy's total P&L (under any filter we tried) comes from a single trading day**, **2025-10-10** — the universal-liquidation flush event already documented in [project_donchianscalp_regime_dependence_2026-05-10.md](../memory/project_donchianscalp_regime_dependence_2026-05-10.md).

| Filter | Trips | Total | PF | Note |
|---|---:|---:|---:|---|
| tc5m ≥ 1700 (whole period) | 10,090 | +$13,434 | 1.71 | apparent edge |
| **tc5m ≥ 1700, 2025-10-10 only** | **207** | **+$14,837** | **138.7** | 2% of trips, 110% of P&L |
| **tc5m ≥ 1700, all other days** | **9,864** | **-$1,562** | **0.92** | breakeven-to-loser ex-Oct-10 |

The single-day P&L of +$14,837 on 207 trips is more than the entire 2-year P&L under any non-cherry-picked filter. The next-biggest single day is 2024-12-09 at +$873 on 166 trips — an order of magnitude smaller. **No second flush of comparable impact exists in the 2-year history.**

### Inside Oct-10: the universal long-recovery cluster

The Oct-10 outliers are concentrated in a 10-minute window (≈14:55–15:05 UTC) and span the entire universe simultaneously:

- 1000LUNCUSDT long entered $0.0216, exited $0.0311 in 11 bars (+44%, +$268)
- AIUSDT long $0.0477 → $0.0673 (+41%)
- LUNA2USDT long $0.0575 → $0.0778 (+35%)
- BANANAS31USDT long $0.0019 → $0.0028 (+50%)
- IOUSDT long $0.1902 → $0.2843 (+50%)
- HEIUSDT long $0.1018 → $0.1775 (+74%)
- ... and 200+ similar trips on the same day

All `exit_reason = normal` — the engine's gap-detector correctly let these through. They're real long entries fired into a universal flush, covered as price snapped back. The 99.5% win rate (216W / 1L) reflects the synchronized cross-asset recovery, not a repeatable scalp edge.

### Sub-regime hunt: deeper-trend cell shows the same shape

To check whether a stricter trend qualifier might reveal a sub-edge outside Oct-10, the v8 simulator was re-run on **MA=45 / trend=20** (the v5 doc's high-PF cell at engine PF 9.73):

| Cell | Trips | Total | PF |
|---|---:|---:|---:|
| trend=20 unfiltered | 17,101 | -$25,254 | 0.13 |
| trend=20, tc5m≥1700 | 322 | +$1,459 | 3.86 |
| trend=20, tc5m≥1700, ex-Oct-10 | **282** | **-$115** | **0.77** |
| trend=20, tc5m≥1700, on Oct-10 | 40 | +$1,574 | (all winners) |

Same pattern, sharper: the unfiltered cell bleeds more (the deeper-trend signals are further from MA, so the engine-bar vs realistic-VWAP gap is wider), and the entire surviving edge is on Oct-10. Deeper trend doesn't unlock a new edge.

### Pre-entry deviation breakdown (ex-Oct-10, tc5m ≥ 1700)

Stratifying ex-Oct-10 trips by `|pct_1h_change|` (deviation from the 1h MA at entry — known at decision time):

| Pre-entry deviation | Trips | Total | PF |
|---|---:|---:|---:|
| < 0.5% | 2,334 | -$693 | 0.78 |
| 0.5-0.75% | 2,979 | -$1,257 | 0.76 |
| 0.75-1% | 1,476 | -$138 | 0.95 |
| 1-1.5% | 1,378 | +$107 | 1.04 |
| 1.5-2% | 674 | -$2 | 1.00 |
| 2-3% | 574 | +$404 | 1.29 |
| 3-5% | 320 | +$177 | 1.17 |
| ≥ 5% | 136 | -$152 | 0.86 |

A faint edge appears in the 2-5% deviation band (PF 1.17-1.29) but the absolute dollars are small (+$581 on 894 trips) and don't pay for the bleed in shallower bands. The 40bp entry filter already cuts most shallow-deviation trips; pushing it deeper than 2% would cost most of the trip count without buying meaningfully more PF.

### Conclusion

**DonchianScalp is not a tradable daily strategy under realistic taker execution.** The taker-fill simulator independently confirms the conclusion previously drawn from the limit-fill audit (saved in memory): the engine's bar-close fills overstate edge by approximately the round-trip spread, which is significant on a 1m mean-reversion scalp. The supposed edge:

1. Vanishes (PF 0.59, -$45k) when applied unconditionally with no lookahead.
2. Reappears (PF 1.7) only after a tc5m-based filter that itself is heavily Oct-10-loaded.
3. Vanishes again (PF ~0.9, slight loss) when the Oct-10 universal-liquidation regime is excluded.

The strategy works as a **vol-flush fader** for once-or-twice-a-year systemic events — not as a continuous scalp. Live trading of this engine would require either a regime-detector that activates only during flushes (a separate research project) or acceptance that the strategy will produce small bleed in normal regimes and large positive returns in flush events. The two-year history doesn't establish a reliable cadence for the latter.

### Data + reproducibility

- v5 sweep trip CSV (with 5m fields): `/tmp/tk_v5_5m/results_trips_1m_don3_trend14.csv` (84,134 rows).
- v8 fill CSV: `/tmp/tk_v5_5m/taker_fills_v8b.csv` (84,130 rows with bucketEnd date fix; v8 at `taker_fills_v8.csv` is 13 rows different due to cross-midnight artifacts, conclusions identical).
- trend=20 fill CSV: `/tmp/tk_v5_t20/taker_fills.csv`.
- v8 run: `taker-fill-sim --trips-csv ... --output-csv ... --fill-mode cum-volume -p 8` (defaults: w_max=60000ms, cv=3.0, taker_fee=0.0005). 60s window, no-lookahead.
- The cum-volume mode now always fills (when ≥1 same-side trade exists in the window); only literal zero-flow Scratched.
