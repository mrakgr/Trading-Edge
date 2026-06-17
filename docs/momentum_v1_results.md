# Mid-Cap Momentum v1 — Realistic-Fill Rewrite

**Status: working long-only momentum edge, PF ~1.7–1.8 post-breadth, with the unrealistic
top-tick fill removed.** This supersedes the back half of `docs/momentum_v0_results.md`, whose
mean-reversion trailing-limit results were inflated by a fill bug (see the warning banner there).

`TradingEdge.MomentumV1` is a ground-up F# rewrite: all indicators computed in a single in-memory
pass (no SQL window functions), one `QullaSystem` per ticker. A full 22-year scan runs in **~14s**
vs v0's ~6 min, so parameter sweeps are minutes, not hours.

## What was wrong in v0

The N-day trailing-limit exit had a top-tick fill artifact. At N=1 the resting sell-limit was
compared against the **current bar's own high** and seeded at `min(seed, bar.high)`, so the fill
test `bar.high >= limit` was **trivially true on the very next bar, every time** — the model sold
at (essentially) the recent high on every exit, ~97% of the time. A real resting limit order can
only fill if price actually trades up to the level you set; it cannot guarantee a top-tick.

That single artifact inflated profit factor by roughly **+0.7** and was the source of every
headline claim downstream of it: "PF 2.33", "tighter stop → PF 3.0", "80% of months positive".

**The fix:** the limit rests at the **prior bar's** N-day high (`TrailHigh`, excludes the current
bar — symmetric with how the stop reads `low_15_prior`). It fills only when a later bar's high
actually reaches that level, and can genuinely miss. An `exitTimeCap` bounds how long it rests:
after `cap` bars unfilled, the trade exits at the next open. `cap=0` = no limit at all (exit at
the next open immediately).

## v1 reproduces v0 exactly (before the fix)

On the old top-tick fill, v1 reproduced v0 bit-for-bit on the locked default: **5,760 trips,
v0-only 0, v1-only 0, exit-date 100.00%, exit-price 100.00%** (within 0.5¢). So the divergence
below is purely the fill correction, not an engine difference.

## Stop-window × exit-config sweep (realistic fills)

Full production filter (price≥$5, ADV≥$100k, rvol∈[6,20], tightness<0.30, ATR%<8%, 0.95
proximity band), **breadth_lag1 > 0.5 applied post-hoc**, entries 2005-01-01 → 2026-05-13, fixed
$10k/trade, uncapped, no compounding. 3,671 trips in every cell (entries + breadth identical;
only the exit fill moves). "mo+%" = share of calendar months with positive realized P&L;
"maxDD" = deepest monthly-equity drawdown.

| stop | exit | trips | win% | PF | net P&L | mo+% | maxDD | worst streak |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | baseline (next open) | 3671 | 47.4% | 1.601 | 467,897 | 64.7% | −20,585 | 4mo |
| 1 | N=1 cap5 | 3671 | 52.0% | 1.553 | 472,881 | 66.2% | −34,802 | 4mo |
| 1 | N=3 cap5 | 3671 | 56.6% | 1.526 | 517,780 | 68.0% | −56,744 | 4mo |
| 2 | baseline | 3671 | 46.5% | 1.669 | 601,786 | 62.5% | −22,610 | 4mo |
| 2 | N=1 cap5 | 3671 | 50.0% | 1.656 | 629,798 | 64.0% | −29,859 | 5mo |
| 2 | N=3 cap5 | 3671 | 54.4% | 1.666 | 711,360 | 67.1% | −36,918 | 4mo |
| 3 | baseline | 3671 | 45.7% | 1.649 | 636,751 | 62.9% | −19,636 | 5mo |
| 3 | N=1 cap5 | 3671 | 49.0% | 1.672 | 691,258 | 61.4% | −19,958 | 4mo |
| 3 | N=3 cap5 | 3671 | 53.0% | 1.662 | 750,581 | 63.9% | −31,037 | 5mo |
| **4** | baseline | 3671 | 45.3% | 1.760 | 805,994 | 56.7% | **−16,788** | 4mo |
| **4** | **N=1 cap5** ⭐ | 3671 | 48.3% | **1.776** | 857,491 | 59.3% | −27,510 | 5mo |
| 4 | N=3 cap5 | 3671 | 51.5% | 1.740 | 903,212 | 64.9% | −26,910 | 5mo |
| 5 | baseline | 3671 | 43.6% | 1.730 | 828,269 | 55.5% | −18,365 | 4mo |
| 5 | N=1 cap5 | 3671 | 47.0% | 1.722 | 863,976 | 57.1% | −34,409 | 5mo |
| 5 | N=3 cap5 | 3671 | 50.4% | 1.688 | 896,415 | 63.7% | −37,830 | 8mo |
| 8 | baseline | 3671 | 42.4% | 1.710 | 914,201 | 57.5% | −32,842 | 5mo |
| 8 | N=1 cap5 | 3671 | 45.4% | 1.702 | 943,545 | 59.2% | −50,228 | 8mo |
| 8 | N=3 cap5 | 3671 | 48.3% | 1.632 | 934,317 | 64.5% | −44,837 | 4mo |
| 10 | baseline | 3671 | 41.2% | 1.641 | 888,788 | 57.3% | −62,824 | 5mo |
| 10 | N=1 cap5 | 3671 | 44.0% | 1.680 | 962,575 | 58.3% | −60,372 | 7mo |
| 10 | N=3 cap5 | 3671 | 47.3% | 1.628 | 972,666 | 61.7% | −48,457 | 5mo |
| 15 | baseline | 3671 | 40.0% | 1.694 | 1,079,255 | 56.9% | −71,260 | 6mo |
| 15 | N=1 cap5 | 3671 | 42.0% | 1.704 | 1,121,307 | 61.0% | −78,955 | 6mo |
| 15 | N=3 cap5 | 3671 | 45.7% | 1.690 | 1,165,489 | 58.2% | −67,183 | 5mo |

### Findings (these REPLACE the v0 trailing-limit conclusions)

1. **PF peaks at stop-window 4 (~1.76–1.78)** — and falls toward both tighter (w=1 ≈ 1.55) and
   looser (w=15 ≈ 1.70) ends. The v0 claim that PF rises monotonically as the stop tightens
   (→3.0 at w=1) was entirely the fill artifact; the ordering is now **reversed at the tight end**:
   window 1 has the *lowest* PF in the sweep.

2. **The trailing limit is a ~+1% refinement, not the edge.** Baseline (just sell at the next
   open) vs N=1/N=3 differ by ~0.02–0.05 PF. v0's "sell the bounce" (1.66 → 2.33) was the fill
   bug. A real resting limit catches *some* bounces and edges baseline slightly, no more.

3. **The PF–P&L trade-off across the stop window.** Looser stops (w=15) carry the most net P&L
   (~$1.1M) by letting winners run, but at lower win rate (40%) and far deeper drawdown (−$71k+).
   Tighter stops cut drawdown (w=4: −$17k) at lower total P&L. **Window 4 is the risk-adjusted
   sweet spot** (highest PF, shallowest drawdown); window 15 is the max-P&L / max-risk end.

4. **Monthly consistency tops out ~64–68%**, not the 80% v0 claimed. Higher-N exits raise win
   rate and month-positive % (more, smaller winners) but not PF.

## Working default: stop-window 4, N=1, exit-cap 5

Chosen for the best PF with the shallowest drawdown. Yearly breakdown (realistic fill,
post-breadth, 2005+; 19/22 years positive):

| year | trips | win% | PF | net |
| ---: | ---: | ---: | ---: | ---: |
| 2005 | 193 | 48% | 1.02 | +962 |
| 2006 | 262 | 46% | 1.51 | +31,063 |
| 2007 | 209 | 45% | 1.77 | +37,811 |
| 2008 | 56 | 61% | 2.05 | +14,993 |
| 2009 | 70 | 50% | 2.30 | +20,207 |
| 2010 | 205 | 48% | 1.88 | +46,160 |
| 2011 | 156 | 49% | 1.50 | +21,455 |
| 2012 | 114 | 49% | 2.28 | +26,938 |
| 2013 | 274 | 53% | 2.25 | +74,781 |
| 2014 | 194 | 52% | 1.54 | +24,580 |
| 2015 | 145 | 54% | 2.54 | +50,363 |
| 2016 | 148 | 56% | 2.25 | +25,120 |
| 2017 | 247 | 50% | 1.32 | +21,717 |
| 2018 | 210 | 51% | 1.95 | +52,929 |
| 2019 | 153 | 53% | 1.94 | +37,936 |
| 2020 | 199 | 39% | 0.94 | −6,595 |
| 2021 | 305 | 43% | 2.97 | +317,402 |
| 2022 | 49 | 39% | 0.99 | −221 |
| 2023 | 83 | 48% | 1.34 | +9,025 |
| 2024 | 180 | 46% | 1.33 | +20,798 |
| 2025 | 150 | 41% | 0.99 | −820 |
| 2026 | 69 | 55% | 2.14 | +30,888 |

The regime dependence v0 documented is intact and honest: 2021 alone carries a third of the total
P&L; 2020, 2022, 2025 are flat-to-slightly-negative. The edge is real and positive across 19 of 22
years, but it is a **~1.8-PF momentum system, not a 3.0-PF one.**

## Reproduction

```bash
# default (stop-window 4, N=1, exit-cap 5), full warmup from dataset start
dotnet run --project TradingEdge.MomentumV1 -c Release -- \
  --start-date 2003-09-10 --end-date 2026-05-13 \
  --stop-low-window 4 --trail-window 1 --exit-time-cap 5 -o /tmp/v1.csv
# then apply breadth_lag1 > 0.5 and the 2005 entry cutoff post-hoc (LAG(pct_above_20) by 1
# trading day on data/equity/momentum_v0/breadth.parquet), as in the sweep analysis.
```

## Known post-parity TODOs (in code)

- **Hard up-only stop ratchet** — the stop currently recomputes `max(prior-window low, entry-day
  low)` each bar and can tick down if a lower low slides into the window. A true Qulla trailing
  stop never loosens.
- **ATR% denominator** — divides by the current bar's close, which a big breakout deflates. Switch
  to the prior close.
