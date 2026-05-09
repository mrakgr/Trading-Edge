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
