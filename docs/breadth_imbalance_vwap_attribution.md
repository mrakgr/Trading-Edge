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

| filter | trades | gross P&L | PF |
|---|---:|---:|---:|
| baseline | 9,547 | +$147 | 1.07 |
| longs ∈ deciles 5-10 | 2,874 | +$161 | — |
| shorts ∈ deciles 1-7 | 3,332 | +$112 | — |
| **both filters applied** | **6,206** | **+$273** | **1.21** |

**~85% more gross from ~35% fewer trades.** The friction picture is in
the next section.

## Friction sensitivity

All numbers above are **gross**. For an active SPY scalper, the realistic
per-round-trip friction is:

- **Commission** (IBKR-tiered or modern zero-commission retail like
  Schwab/Fidelity on SPY): ~$0.0005/share × 2 sides = **$0.001 r/t**.
- **Spread cost**: SPY trades penny-wide all RTH. **Crossing the full
  spread on both sides = $0.02 r/t.** Sitting on the inside both sides
  = $0.00. Realistic mixed execution = **~$0.01 r/t** assumed below.

Applying a flat friction reduction to each trade's P&L:

| Scenario | Trades | Net $/share | PF |
|---|---:|---:|---:|
| **Baseline gross** | 9,547 | +$147 | 1.073 |
| Baseline + commission only | 9,547 | +$137 | 1.068 |
| Baseline + commission + 1¢ spread r/t | 9,547 | **+$42** | 1.020 |
| Baseline + commission + 2¢ spread r/t | 9,547 | **−$53** | 0.976 |
| **30m-filtered gross** | 6,206 | +$273 | 1.206 |
| 30m-filtered + commission only | 6,206 | +$267 | 1.201 |
| 30m-filtered + commission + 1¢ spread r/t | 6,206 | **+$205** | 1.148 |
| 30m-filtered + commission + 2¢ spread r/t | 6,206 | **+$143** | 1.099 |

**Two findings:**

1. **The baseline is fragile.** A single penny of average round-trip
   spread takes 71% of the gross edge ($147 → $42). At full-tick crossed
   on every side, it goes negative ($−53). Whether the unfiltered
   strategy makes money in production depends almost entirely on
   execution quality.
2. **The filtered version is friction-resistant.** Even at the
   worst-case 2¢ assumption, the 30m-filtered strategy still nets
   **+$143/share** with PF 1.10. At realistic 1¢, it nets **+$205** —
   roughly 5× the baseline at the same friction level.

**The percentage of gross captured at 1¢ r/t spread**:
- baseline: $42 / $147 = **29%**
- 30m-filtered: $205 / $273 = **75%**

The filter doesn't just save commission by trading less. It concentrates
the edge into bigger-magnitude trades (mean +$0.044/trade gross vs.
+$0.015/trade for the baseline), so the per-trade friction takes a
smaller relative bite. Trading 35% fewer times saves ~$36/share in
total friction at 1¢ — but the filtered version nets $163 *more* than
the baseline at the same friction, so most of the improvement is from
the deciles themselves.

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

## Proper filtered backtest

The naive bucketing above answers *"what's the P&L of trades in
favorable deciles?"* — but skipping a trade in an always-in-flip
strategy has knock-on effects on subsequent trades. To validate, the
gate was baked into the strategy itself (TradingEdge.Vwap now supports
`--breadth` + `--long-min-imb` + `--short-max-imb` + `--filter-mode`).

Two filter modes were implemented:

- **`entry`** (recommended): the gate is checked **only at VWAP-cross
  events**. If the gate refuses the new side, the cross is skipped
  (engine goes flat or stays flat until the next cross-and-gate-passes
  event). The gate is **not** re-checked while a position is open. This
  faithfully implements "filter the entries" semantics.
- **`always`** (rejected): the gate is checked every bar; positions
  flatten when the gate goes red and re-enter when it goes green. This
  generated ~19k trades vs. ~6k from `entry`, with avg hold dropping
  from 25 bars to 10. Net P&L collapsed to +$50 — the gate-induced
  flat episodes break long holds into small ones and cut off trend
  P&L.

### entry-mode results at 30m, decile-5/decile-7 gate

Gate: longs allowed when `imbalance ≥ -0.012`, shorts allowed when
`imbalance ≤ +0.078`. Run via:

```bash
dotnet run --project TradingEdge.Vwap -c Release -- backtest \
  -t SPY -s 2024-01-01 \
  --breadth data/breadth/imbalance_vwma_30m.parquet \
  --long-min-imb -0.012 --short-max-imb 0.078 \
  --filter-mode entry
```

| Metric | Baseline | Naive estimate | Entry-filtered (real) |
|---|---:|---:|---:|
| Trades | 9,547 | 6,206 | **5,971** |
| Win rate | 15.7% | — | 16.0% |
| Net $/share | +$147 | +$273 | **+$272** |
| PF | 1.073 | — | **1.213** |
| Max drawdown | -$63 | — | **-$43** (better) |
| Avg bars held | 25.2 | — | 26.5 |

The naive bucketing's +$273 estimate landed within $1 of the real
backtest's +$272 — they're functionally identical, which makes sense
because the entry-filtered strategy is exactly "skip the bad-decile
entries, hold the rest." Drawdown is *better* than baseline (-$43 vs
-$63), which is a bonus: the trades being skipped were not just
low-edge but also high-volatility.

### Friction sensitivity, real numbers

| Strategy | Trades | Gross | + commission | + 1¢ spread | + 2¢ spread |
|---|---:|---:|---:|---:|---:|
| Baseline | 9,547 | +$147 | +$137 | **+$42** | **−$53** |
| Entry-filtered 30m | 5,971 | +$272 | +$266 | **+$206** | **+$147** |

At realistic 1¢ r/t spread the filtered strategy nets **+$206 vs
baseline +$42** — about **5× the realistic-friction P&L**. Even at
worst-case 2¢ it stays comfortably positive (+$147), which is larger
than the baseline's *gross* P&L.

## Month-by-month breakdown (entry-filtered, 30m)

Each row is the calendar month containing the trade's `exit_date`. `Net @ 1¢`
applies the 1¢ round-trip spread + IBKR-tiered commission ($0.011/share).

| Month | Trades | L | S | Win% | Gross | Long P&L | Short P&L | PF | Net @ 1¢ |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 2024-01 | 220 | 118 | 102 | 17.3 | +0.81 | +3.61 | −2.80 | 1.02 | −1.61 |
| 2024-02 | 236 | 122 | 114 | 16.5 | +6.01 | +9.30 | −3.30 | 1.23 | +3.41 |
| 2024-03 | 252 | 108 | 144 | 11.5 | +4.72 | +6.09 | −1.37 | 1.15 | +1.95 |
| 2024-04 | 221 | 109 | 112 | 16.3 | **+21.69** | −5.73 | +27.42 | 1.52 | +19.26 |
| 2024-05 | 243 | 112 | 131 | 13.6 | +1.60 | +3.83 | −2.23 | 1.05 | −1.07 |
| 2024-06 | 221 | 107 | 114 | 10.0 | **−6.48** | −0.85 | −5.63 | 0.79 | −8.91 |
| 2024-07 | 194 | 87 | 107 | 16.0 | +9.61 | +0.45 | +9.16 | 1.28 | +7.47 |
| 2024-08 | 196 | 87 | 109 | 16.8 | +17.44 | +2.93 | +14.51 | 1.35 | +15.28 |
| 2024-09 | 223 | 117 | 106 | 17.9 | +16.86 | +6.49 | +10.37 | 1.39 | +14.40 |
| 2024-10 | 226 | 103 | 123 | 15.0 | +5.03 | −3.39 | +8.42 | 1.13 | +2.54 |
| 2024-11 | 160 | 79 | 81 | 17.5 | +13.16 | +12.12 | +1.04 | 1.46 | +11.40 |
| 2024-12 | 188 | 99 | 89 | 17.6 | **+23.04** | +7.16 | +15.88 | 1.71 | +20.97 |
| 2025-01 | 198 | 92 | 106 | 12.6 | **−6.04** | −4.68 | −1.36 | 0.88 | −8.22 |
| 2025-02 | 195 | 93 | 102 | 14.4 | **+27.51** | +9.14 | +18.37 | 1.73 | +25.36 |
| 2025-03 | 207 | 104 | 103 | 16.9 | +9.52 | +4.01 | +5.51 | 1.14 | +7.25 |
| 2025-04 | 222 | 111 | 111 | 16.7 | **+36.79** | +51.83 | −15.04 | 1.30 | +34.35 |
| 2025-05 | 228 | 108 | 120 | 18.0 | −0.38 | +10.51 | −10.90 | 0.99 | −2.89 |
| 2025-06 | 217 | 101 | 116 | 17.1 | +10.01 | +8.94 | +1.07 | 1.26 | +7.62 |
| 2025-07 | 188 | 89 | 99 | 20.7 | +14.88 | +7.50 | +7.38 | 1.58 | +12.81 |
| 2025-08 | 196 | 97 | 99 | 14.8 | +2.66 | −0.24 | +2.90 | 1.08 | +0.50 |
| 2025-09 | 235 | 117 | 118 | 14.0 | −2.19 | +6.19 | −8.38 | 0.94 | −4.77 |
| 2025-10 | 204 | 95 | 109 | 18.6 | +15.54 | −5.28 | +20.81 | 1.32 | +13.29 |
| 2025-11 | 164 | 66 | 98 | 18.9 | **+30.47** | +9.26 | +21.20 | 1.61 | +28.66 |
| 2025-12 | 234 | 117 | 117 | 16.7 | **−14.19** | −10.39 | −3.80 | 0.70 | −16.76 |
| 2026-01 | 204 | 94 | 110 | 12.7 | −5.69 | −4.04 | −1.65 | 0.87 | −7.93 |
| 2026-02 | 185 | 76 | 109 | 15.7 | +1.23 | −1.40 | +2.63 | 1.02 | −0.80 |
| 2026-03 | 226 | 125 | 101 | 17.3 | +13.32 | +3.91 | +9.41 | 1.18 | +10.84 |
| 2026-04 | 227 | 109 | 118 | 16.7 | +16.81 | +25.10 | −8.30 | 1.39 | +14.31 |
| 2026-05† | 61 | 29 | 32 | 21.3 | +8.24 | +9.23 | −0.99 | 1.71 | +7.57 |
| **Total** | **5,971** | **2,871** | **3,100** | **16.0** | **+271.95** | **+161.60** | **+110.35** | **1.21** | **+206.27** |

† 2026-05 partial (through 2026-05-13, 9 trading days).

**Aggregate over 29 months**: 23 positive months gross (79%), 20 positive
at 1¢ friction (69%). Mean +$9.38/month gross (σ $11.86) → monthly
Sharpe ~0.79, ≈2.7 annualized before adjusting for autocorrelation.

**Concentration risk**: top 5 months (2025-04, 2025-11, 2025-02, 2024-12,
2024-04) sum to **$139 — 51% of all gross**. Each of those is a known
vol-event month (April 2025 tariff shock, Feb 2025 Trump-tariff threats,
Dec 2024 election aftermath / Fed-pivot positioning, April 2024 mid-cycle
correction). The strategy needs a tape that commits direction for 30+
minutes; chop months underperform.

**Worst stretch**: Dec 2025 → Feb 2026 was −$14, −$6, +$1 = **−$19 over
3 months**, the deepest sustained underperformance in the window. This
period coincided with the late-2025 / early-2026 chop regime — short
ranges, low realized vol, lots of mid-day reversals.

**No persistent side bias**: 13 months are long-dominant, 12 are
short-dominant, 4 are mixed. Whichever side has the larger gain swaps
month to month — consistent with the gate doing its job (only allowing
each side when breadth agrees).

## Out-of-sample test on 2023

The 2024-2026 results above are in-sample (gate thresholds were chosen
from the same window). The cleanest OOS test we can run with our data
is to apply the **same gate values** (long ≥ −0.012, short ≤ +0.078)
**blindly** to 2023, where 1m bars + bulk trades are available but
were not consulted when picking the gate.

Setup: rebuilt `data/sp500_membership.parquet` and the 30m breadth
parquet to span 2022-01-03 → 2026-05-13 (added WTW and EG manual
overrides for two NULL-FIGI Polygon `/events` chains that affect the
2022-2023 window). Membership validation: 100% match against bulk
parquets on all 16 sampled dates from 2022-01-03 through 2026-05-07.

Then ran two backtests for SPY 2023-01-03 → 2023-12-29 (248 days):

### Headline numbers

| Metric | 2023 baseline | 2023 entry-filtered (same gate) | 2024-2026 entry-filtered (reference) |
|---|---:|---:|---:|
| Trades | 4,261 | 2,588 | 5,971 |
| Win rate | 16.1% | 15.7% | 16.0% |
| Gross $/share | **+$16** | **+$8** | +$272 |
| PF | 1.025 | 1.019 | 1.213 |
| Max DD | −$19 | −$34 | −$43 |
| Net @ 1¢ spread | **−$31** | **−$21** | +$206 |

**The strategy does not generalize OOS.** Three things to read out:

1. **2023 gross is essentially zero.** +$16/share over 248 days at the
   baseline is ~$0.004/trade. Filtered, +$8 is $0.003/trade. Either is
   indistinguishable from noise. At realistic 1¢ r/t spread, both are
   meaningful losers.
2. **The filter HURTS in 2023.** Trade count drops 40% (4,261 → 2,588)
   but gross drops 53% ($16 → $8). The filter is identifying
   *worse-than-baseline* trades to keep — opposite of its 2024-2026
   behavior. Max drawdown also got worse (-$34 vs -$19 baseline).
3. **2023 decile structure is non-monotone.** The longs top decile
   (imb +0.76) was a **−$7 loser**, not a winner; the best cell was
   the middle (decile 7, imb +0.25). The bottom decile (imb -0.72)
   was the WORST at -$16 — same direction as 2024-2026, so that part
   replicates, but the top-decile effect inverts.

### What the OOS structure looks like

Below is the 2023 baseline decile table, for comparison with the
30m table further up:

**2023 LONGS by imbalance at entry**:

| decile | imb mean | n | mean P&L | total P&L | PF |
|---:|---:|---:|---:|---:|---:|
| 1 | −0.683 | 215 | −0.034 | −7.21 | 0.84 |
| 4 | −0.069 | 215 | +0.058 | +12.40 | 1.41 |
| 5 | +0.026 | 215 | +0.010 | +2.12 | 1.06 |
| 7 | +0.249 | 215 | **+0.135** | **+29.10** | **2.01** |
| 10 | +0.759 | 214 | −0.035 | **−7.48** | 0.80 |

**2023 SHORTS by imbalance at entry**:

| decile | imb mean | n | mean P&L | total P&L | PF |
|---:|---:|---:|---:|---:|---:|
| 1 | −0.767 | 212 | −0.031 | **−6.50** | 0.84 |
| 5 | −0.127 | 211 | +0.043 | +9.15 | 1.30 |
| 9 | +0.413 | 211 | +0.034 | +7.07 | 1.25 |
| 10 | +0.681 | 211 | −0.000 | −0.07 | 1.00 |

For shorts, the bottom 4 deciles **all lose money** in 2023, with
cumulative −$16. The decile-2 cell that was the **biggest winner**
in 2024-2026 (+$70 at imb −0.45) is a **−$6 loser** in 2023. This
inversion is what kills the filter.

### 2023 monthly breakdown (filtered)

| Month | Trades | Gross | PF | Net @ 1¢ |
|---|---:|---:|---:|---:|
| 2023-01 | 175 | +10.40 | 1.27 | +8.47 |
| 2023-02 | 218 | **−12.29** | 0.72 | −14.69 |
| 2023-03 | 249 | −5.84 | 0.89 | −8.58 |
| 2023-04 | 181 | +1.03 | 1.04 | −0.96 |
| 2023-05 | 214 | **−11.32** | 0.67 | −13.67 |
| 2023-06 | 203 | +5.22 | 1.19 | +2.99 |
| 2023-07 | 225 | +1.19 | 1.05 | −1.29 |
| 2023-08 | 229 | **+11.37** | 1.33 | +8.85 |
| 2023-09 | 187 | −2.53 | 0.91 | −4.59 |
| 2023-10 | 226 | **+11.87** | 1.29 | +9.38 |
| 2023-11 | 260 | −7.14 | 0.77 | −10.00 |
| 2023-12 | 221 | +5.66 | 1.22 | +3.23 |

**6 positive months, 6 negative.** No vol-event months in 2023 (the
banking crisis in March wasn't the kind of sustained directional
breadth move the strategy needs — it was a 2-day shock then mean
reversion). The grinding-uptrend / low-vol regime of late 2023 was
exactly the worst case for a flip strategy.

### Honest interpretation

This is the OOS test working as designed. The 2024-2026 in-sample edge
appears to be **regime-specific** to a window with multiple high-vol
trend-confirming events (April 2025 tariff shock, Nov 2025 spike, Feb
2025 Trump-tariff threats, Dec 2024 election aftermath, April 2024
mid-cycle correction). 2023 had no such events: it was a slow grind
higher punctuated by quick 2-3 day shocks (SVB, debt ceiling, October
correction), none of which produced sustained breadth-confirmed
direction.

**Three plausible interpretations:**

a) **The signal works only during high-vol trend regimes.** If true,
   the strategy should be paired with a regime filter (e.g., trade
   only when realized vol over the past 20 days exceeds some
   threshold). Worth testing but adds another in-sample knob.

b) **The signal is genuine but the threshold is wrong for low-vol
   regimes.** At lower imbalance variance, the same decile boundaries
   are more permissive in absolute terms. Maybe quantile-based gates
   (e.g., "longs in top 60% of recent imbalance distribution")
   generalize better than fixed thresholds.

c) **The original edge was largely in-sample fit.** With 5 months
   carrying 51% of gross P&L, removing those 5 months would have left
   only +$133 over 24 months = ~$5.5/month — a 30m-imbalance filter
   could plausibly extract this from chance alignment with vol events.

The honest read of (c) is that the +$272 is mostly explained by a few
vol-event months where breadth-confirmed VWAP-flips happened to align
with sustained directional moves. The OOS suggests that's not a
generalizable edge but a regime-conditional phenomenon.

### What this means for next steps

- **Do not ship this strategy as-is.** The OOS performance is
  net-negative.
- **Either drop the approach OR add a regime filter.** A simple test:
  apply the gate only when realized vol over the past 20 trading
  days exceeds, say, the median over our 2024-2026 window. This is
  *another* in-sample decision but it's a hypothesis that could be
  separately OOS-validated by withholding part of 2024-2026 alongside
  2023.
- **Don't add more parameters at once.** Each additional knob
  multiplies the risk that 2024-2026 is an in-sample fit. We've
  already used the 30m window choice, two gate thresholds, and
  effectively a regime assumption — too many degrees of freedom for
  the data.

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
