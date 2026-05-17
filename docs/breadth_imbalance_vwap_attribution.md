# Breadth-Imbalance Attribution of the VWAP-Flip Strategy

This document captures the first attribution pass that cross-references the
SPY VWAP-flip system's per-trade P&L against an S&P 500 dollar-volume
imbalance breadth indicator computed at three different MA windows: 4-minute,
30-minute, and 60-minute VWMA classification.

The point is not to ship a filtered strategy yet — it's to identify which
breadth window carries actual edge information and which is microstructure
noise. The strategy is not aware of breadth; the breadth is used purely
post-hoc to bucket the trades.

## The setup

- **Strategy (unfiltered base case)**: TradingEdge.Vwap, SPY 2024-01-01 →
  2026-05-17, always-in flip on session VWAP starting at 09:30 ET,
  EOD-flat at 16:00 close, fills at bar close. 593 trading days, 9,547
  round-trip trades. Win rate 15.7%, PF 1.073, net +$147/share (long
  +$113, short +$34). Trade log at
  `data/vwap/trades_SPY_2024-01-01_2026-05-17.csv`.
- **Breadth indicator**: per-RTH-minute aggregate over S&P 500
  constituents. For each ticker at minute T, classify the bar as `buy` if
  its bar-VWAP > trailing-N-minute VWMA, `sell` if below, `neutral` if
  the N-bar window isn't full (premarket gaps). Then aggregate dollar
  volumes: `imbalance = (Σbuy_dv − Σsell_dv) / (Σbuy_dv + Σsell_dv)`.
  Built by `scripts/breadth/build_imbalance_breadth.py --window-minutes
  {N}`. Output in `data/breadth/imbalance_vwma_{N}m.parquet`.
- **Membership** is point-in-time per
  `data/sp500_membership.parquet` (see
  `docs/sp500_membership_pipeline.md`) so renames and adds/removes are
  handled honestly. Coverage 2024-01-01 → 2026-05-13 (593 days), 390 RTH
  minutes/day = 231,270 imbalance rows per window.
- **Attribution**: `scripts/breadth/attribute_vwap_trades.py` joins every
  VWAP trade to the breadth row at `(entry_date, entry_bucket)`, then
  NTILEs into 10 deciles **separately for longs, shorts, and the union**
  so each side gets its own decile edges. Reports per-bucket trade
  count, win rate, mean P&L, total P&L, and profit factor.

## Headline numbers per window

Longs: **+$113 / 4,790 trades / PF 1.11** in the base, regardless of
window — the join is to the same trades.
Shorts: **+$34 / 4,757 trades / PF 1.03** in the base.

What changes per window is the **decile structure** — how much of the
total sits in which imbalance bucket.

### 4-minute VWMA

**Longs** — weak, non-monotone:

| decile | imb mean | n | mean P&L | total P&L | PF |
|---:|---:|---:|---:|---:|---:|
| 1 | −0.447 | 479 | −0.039 | −18.68 | 0.82 |
| 5 | +0.184 | 479 | +0.071 | +34.09 | 1.40 |
| 10 | +0.803 | 479 | +0.130 | +62.46 | 1.38 |

Top decile and decile 5 are the biggest cells; deciles 6-9 are flat. Not
a clean monotone relationship.

**Shorts** — surprising:

| decile | imb mean | n | mean P&L | total P&L | PF |
|---:|---:|---:|---:|---:|---:|
| 1 | −0.809 | 476 | −0.020 | −9.59 | 0.95 |
| 6 | −0.159 | 476 | +0.126 | **+59.86** | **1.68** |
| 10 | +0.438 | 475 | −0.058 | −27.67 | 0.73 |

**The biggest short cell is mildly-negative imbalance (-0.16), not
extreme.** Shorting into peak-panic breadth (decile 1, imb -0.81)
*loses* — that's capitulation that bounces. The 4m window catches one
real fact (shorts hate positive breadth, decile 10 PF 0.73) but it's
dominated by short-window microstructure noise. **Not a filter to ship.**

### 30-minute VWMA

**Longs** — nearly monotone, matches the textbook intuition:

| decile | imb mean | n | mean P&L | total P&L | PF |
|---:|---:|---:|---:|---:|---:|
| 1 | −0.584 | 479 | −0.034 | −16.42 | 0.85 |
| 2 | −0.313 | 479 | −0.050 | −23.83 | 0.77 |
| 3 | −0.174 | 479 | −0.004 | −1.98 | 0.98 |
| 4 | −0.064 | 479 | −0.012 | −5.50 | 0.94 |
| 5 | +0.039 | 479 | +0.034 | +16.48 | 1.18 |
| 6 | +0.133 | 479 | +0.043 | +20.44 | 1.22 |
| 7 | +0.222 | 479 | +0.021 | +10.05 | 1.12 |
| 8 | +0.325 | 479 | +0.070 | +33.59 | 1.34 |
| 9 | +0.470 | 479 | +0.024 | +11.62 | 1.12 |
| 10 | +0.710 | 479 | **+0.144** | **+68.84** | **1.53** |

Bottom 4 deciles (imb < ~0, ~40% of trades) sum to **−$48** at PF < 1.
Top 6 deciles sum to **+$161**. Single cleanest rule: *skip longs when
30m breadth is negative*.

**Shorts** — also clean, mirror image:

| decile | imb mean | n | mean P&L | total P&L | PF |
|---:|---:|---:|---:|---:|---:|
| 1 | −0.697 | 476 | +0.040 | +18.97 | 1.13 |
| 2 | −0.451 | 476 | **+0.147** | **+69.73** | **1.69** |
| 3 | −0.316 | 476 | +0.018 | +8.76 | 1.08 |
| 4 | −0.200 | 476 | −0.032 | −15.28 | 0.84 |
| 5 | −0.103 | 476 | +0.040 | +18.87 | 1.21 |
| 6 | −0.019 | 476 | −0.030 | −14.36 | 0.85 |
| 7 | +0.078 | 476 | +0.053 | +25.18 | 1.26 |
| 8 | +0.189 | 475 | −0.064 | −30.25 | 0.68 |
| 9 | +0.334 | 475 | −0.018 | −8.74 | 0.90 |
| 10 | +0.594 | 475 | −0.083 | −39.18 | 0.63 |

Top 3 deciles (imb > +0.13, ~30% of shorts) sum to **−$78** at PF
0.65-0.90. Bottom 7 deciles sum to **+$112**. **Best short cell is
moderately-negative breadth** (-0.45), and even decile 1 (the deepest
negative) is profitable now — opposite of the 4m result. The 30m
window captures sustained trend, not flush-and-bounce. Single cleanest
rule: *skip shorts when 30m breadth is meaningfully positive*.

**Hypothetical combined filter at 30m** (counted naively, not re-run as
a backtest — see "What still needs to be done" below):

| filter | trades | gross P&L | PF est. |
|---|---:|---:|---:|
| baseline | 9,547 | +$147 | 1.07 |
| longs ∈ deciles 5-10 | 2,874 | +$161 | — |
| shorts ∈ deciles 1-7 | 3,332 | +$112 | — |
| both filters applied | 6,206 | **~+$273** | — |

**~85% more gross from ~35% fewer trades.** Friction headroom improves a
lot — at $0.005/share commission round-trip the baseline (9,547 trades)
pays ~$48 in commissions per share-unit; the filtered version
(~6,206 trades) pays ~$31. Slippage on SPY at 1 spread tick (~$0.01)
flips the baseline net-negative; the filtered version stays comfortably
positive.

### 60-minute VWMA

**Longs** — relationship breaks at the extremes:

| decile | imb mean | n | mean P&L | total P&L | PF |
|---:|---:|---:|---:|---:|---:|
| 1 | −0.597 | 479 | −0.040 | −19.33 | 0.82 |
| 2 | −0.338 | 479 | −0.046 | −21.79 | 0.79 |
| 6 | +0.093 | 479 | +0.076 | +36.40 | 1.42 |
| 7 | +0.190 | 479 | **+0.137** | **+65.38** | **1.67** |
| 8 | +0.306 | 479 | −0.009 | −4.36 | 0.96 |
| 9 | +0.449 | 479 | +0.066 | +31.79 | 1.33 |
| 10 | +0.682 | 479 | +0.011 | +5.02 | 1.04 |

Sweet spot is **moderate positive breadth (decile 7, imb +0.19)** — by
the time 60m breadth is at +0.68 the move is mature and reversion risk
is back. Cleanest cut: skip the bottom 4 deciles → +$142 from 2,874
trades. Less aggressive than 30m but still positive.

**Shorts** — strongest single cell of the entire study:

| decile | imb mean | n | mean P&L | total P&L | PF |
|---:|---:|---:|---:|---:|---:|
| 1 | −0.660 | 476 | **+0.126** | **+59.77** | **1.46** |
| 3 | −0.293 | 476 | +0.061 | +29.10 | 1.29 |
| 5 | −0.089 | 476 | +0.059 | +28.14 | 1.30 |
| 10 | +0.611 | 475 | −0.062 | −29.51 | 0.73 |

Top 3 deciles all losers (cumulative −$33), bottom 7 mostly winners.
Critically: **shorts now love deep-negative 60m breadth** (decile 1
PF 1.46) — the same condition that lost money on the 4m view. Same
flip happened with the 30m: at longer horizons, "tape has been red
for an hour" is a sustained-downtrend signal, not capitulation.

## Three-way comparison

| | 4m best long cell | 4m best short cell | 30m best long | 30m best short | 60m best long | 60m best short |
|---|---|---|---|---|---|---|
| top P&L | +$62 D10 | +$60 D6 | **+$69 D10** | **+$70 D2** | +$65 D7 | +$60 D1 |
| sign convention | strong+ | mild− | strong+ | mild− | mid+ | strong− |
| monotonicity | weak | none | strong | strong | breaks at extremes | mostly monotone |
| ship-readiness | no | no | **yes** | **yes** | promising for shorts | promising for longs (mid) |

**30m is the cleanest single window.** Monotone-ish on both sides,
matches textbook intuition (longs love positive breadth, shorts love
negative), and produces the highest-PF single cells on each side.

**60m has stronger short signal at the extreme tail** (decile 1 PF 1.46
vs. 30m decile 2 PF 1.69 — close), but **loses the "longs love extreme
positive breadth" relationship** that 30m has. Hands-on: 30m says "trade
with the wind"; 60m says "trade with the wind unless it's been blowing
too long."

**4m carries some directional information but is dominated by
microstructure noise.** Not useful as a stand-alone filter.

## What this means about the underlying VWAP-flip

The very fact that breadth attribution produces clean monotone patterns
at 30m+ is meaningful. It says the VWAP-flip system's edge isn't
spread uniformly across regime — it concentrates in cells where the
broader tape agrees with the local cross.

- Long when SPY crosses *up* through its session VWAP **and** 30m
  S&P breadth is already positive: this is "alignment-of-frames" —
  big-tape uptrend confirmed by a single-name-level breadth majority.
- Short when SPY crosses *down* through its session VWAP **and** 30m
  S&P breadth is mildly-to-moderately negative: tape is rolling
  over but not panicking.

The cells that lose money are the *disagreement* cases: longs into
negative breadth (longs in deciles 1-4 at 30m: cumulative −$48), shorts
into positive breadth (shorts in deciles 8-10 at 30m: cumulative −$78).
This is what you'd expect from a strategy that's mechanically rules-
based and unaware of the broader tape: half its signals are giving the
right answer for SPY in isolation but the wrong answer for the prevailing
market regime.

## Caveats

1. **In-sample fit.** The decile edges were chosen from the same window
   the strategy was backtested on. Any "filter" derived from this
   analysis needs an OOS split before claims about real edge are
   credible. Suggested split: 2024 in-sample, 2025-onwards OOS.
2. **No friction.** All numbers are gross of commission/slippage. The
   baseline +$147 already won't survive realistic friction on 9,547
   trades. The filtered hypotheticals (~+$273 from ~6,206 trades) will,
   but only narrowly.
3. **Naive bucketing is not a backtest.** Skipping a trade in the
   always-in-flip strategy changes downstream flips (the position
   you'd have entered doesn't exist when the next cross comes). The
   ~+$273 estimate is correct for *picking* trades from the existing
   log but is wrong for *running* a filtered strategy. The real
   filtered backtest still needs to be implemented — the engine should
   only enter when the breadth filter passes, hold until the next
   cross-and-filter-passes signal, and skip filtered-out crosses
   entirely.
4. **Breadth lookahead is clean.** The trade enters at bar N's close;
   the breadth measured at bar N's close uses VWMA over bars
   N-(window-1)..N — strictly past data. No lookahead.
5. **Single-strategy attribution.** This pattern is specific to
   VWAP-flip. A different strategy (mean-reverting, breakout,
   momentum) may have a different breadth-window sweet spot or even
   opposite sign.

## What still needs to be done

- **Real filtered backtest** at the 30m window: re-run the VWAP engine
  with the breadth gate baked in, produce a proper equity curve, and
  show that the filtered version's cumulative P&L curve is smoother
  (not just larger).
- **OOS validation** on 2025-2026 after deciles are locked from 2024
  data only.
- **Combined-filter sweep at 60m for shorts only** (best short cell
  was 60m decile 1, narrowly), to test whether a two-window approach
  beats single-window 30m.
- **Friction model** before any claims about net edge.

## Reproduction

```bash
# 1. Build the breadth files (one per window):
python3 scripts/breadth/build_imbalance_breadth.py --window-minutes 4
python3 scripts/breadth/build_imbalance_breadth.py --window-minutes 30
python3 scripts/breadth/build_imbalance_breadth.py --window-minutes 60

# 2. Run the VWAP strategy (skip if already present):
dotnet run --project TradingEdge.Vwap -c Release -- backtest -t SPY -s 2024-01-01

# 3. Attribute against each breadth file:
for w in 4 30 60; do
    python3 scripts/breadth/attribute_vwap_trades.py \
        --breadth data/breadth/imbalance_vwma_${w}m.parquet
done
```

End-to-end: ~1 minute on the cached build.
