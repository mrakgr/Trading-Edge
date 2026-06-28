# Consolidation-Breakout v0 — Negative Result

**Status: dead end. No exploitable edge after fees on the Binance USDT-perps universe (643 symbols, 1m bars, 2024-05-01 to 2026-04-30).**

This document records the research path so the negative result is preserved and a future iteration knows what's been ruled out. Companion to [donchian_fade_v0_results.md](donchian_fade_v0_results.md), which covers the structurally opposite mean-reversion engine that *did* work.

## Thesis

A "coiled spring" pattern: price compresses into a tight 3h window while volume stays elevated, then resolves with a directional move. The hypothesis is that consolidation + elevated volume signals accumulation/distribution that ultimately resolves in a tradable direction.

Operational definition (locked v0 spec):

- **Consolidation signal — single-bar check at the moment of break** (no history counter):
  - **Vol-compression ratio**: `std(log close) over trailing 3h` ÷ `std(log returns) over trailing 3h` < threshold. The ratio is small when prices keep coming back to roughly the same level despite normal bar-to-bar noise.
  - **Volume rvol**: 3h-window mean volume ÷ 30d-window mean volume ≥ threshold.
- **Entry trigger** (same bar):
  - Long: `bar.High ≥ rolling 3h max-high` AND `bar.Volume ≥ 2× trailing 3h mean`.
  - Short: symmetric on min-low.
  - Direction: with the break.
- **Cover** — two modes tested:
  - `donchian-stop`: ratcheted 3-bar trailing Donchian stop on the with-trade side.
  - `cvd-flip`: cover when 1h CVD crosses to the unfavourable sign.
- No stops, no time-stop, no MA-side filter. Cover mode is the only protection.

Expected scale of the compression ratio: a pure random walk of length N gives ratio ≈ √(N/3) ≈ **7.75 at N=180 bars**. Pure mean-reverting (level pinned) prices approach 0; pure trending prices give ratios > 7.75. Genuine consolidation should sit in the 1..5 range. Ratios > 7.75 indicate the prior 3h was already trending hard.

## Engine

[OrderflowConsolBreakout.fs](../TradingEdge.CryptoBacktest/OrderflowConsolBreakout.fs) — standalone module following the same conventions as `OrderflowDonchianFade.fs`: standalone trip record (`ConsolBreakoutRoundTrip`), read-before-write rolling state, gap detector, vol-targeting, ADV gate.

Trip CSV columns recorded for post-hoc analysis: `vol_compression_ratio_at_entry`, `vol_rvol_at_entry`, `bar_vol_multiple_at_entry`, `range_width_pct_at_entry`, `pct_1h_change_at_entry`, `pct_24h_change_at_entry`, `cover_mode`.

CLI: `consol-breakout-sweep` in [Program.fs](../TradingEdge.CryptoBacktest/Program.fs).

## Sweep results

### v0 default thresholds (compression < 1.0, rvol ≥ 3, bar-vol ≥ 2×)

Universe sweep, both cover modes:

| cover         | trips |
| ------------- | ----- |
| donchian-stop | 34    |
| cvd-flip      | 33    |

**The signal almost never fires at the locked-spec thresholds.** 33-34 trips across 643 symbols × 2 years is unusable for analysis. The compression-ratio threshold of 1.0 is well below the random-walk baseline of 7.75 — only pathologically pinned prices satisfy it.

### Gates wide open (compression < 100, rvol ≥ 0, bar-vol ≥ 0)

To enable post-hoc decile analysis, the sweep was re-run with all signal gates disabled — every range break becomes a trip:

| cover         | trips     | win % | PF    | net P&L   | avg/trade |
| ------------- | --------- | ----- | ----- | --------- | --------- |
| donchian-stop | 7,120,984 | 26.3  | 0.558 | -$3.67M   | -$0.515   |
| cvd-flip      | 7,708,160 | 27.2  | 0.597 | -$3.85M   | -$0.499   |

Long/short balance is roughly symmetric (PF 0.55-0.64 per side). Wide-open is strongly negative — most range breaks are noise that gets faded.

## Where the edge concentrates (decile analysis)

### Long, pct_24h_change ≥ 10%, by vol_compression decile

Hypothesis: the original "long the up-break in a strong context" cell.

| cover | best decile | best PF | net P&L  | sign of any decile |
| ----- | ----------- | ------- | -------- | ------------------ |
| donchian-stop | dec 9 (vc 8.6..10.1) | 0.91 | -$2,266 | **all 10 negative** |
| cvd-flip | dec 9 (vc 8.6..10.2) | 0.90 | -$1,908 | **all 10 negative** |

The **most-compressed deciles are the worst** (PF 0.66-0.81). Long-after-24h-up-10% loses across the entire VC range with no decile reaching PF 1.0.

### Short, pct_24h_change ≤ -10%, by vol_compression decile (donchian-stop)

This is where the only structural finding appears:

| decile | trips | vc range | win %  | PF    | net P&L | avg     |
| ------ | ----- | -------- | ------ | ----- | ------- | ------- |
| 1 | 6,295 | 0.58..3.08 | 33.0 | 1.067 | +$977 | +$0.16 |
| 2 | 6,294 | 3.08..3.76 | 32.2 | 1.035 | +$515 | +$0.08 |
| 3 | 6,294 | 3.76..4.34 | 30.8 | 0.974 | -$444 | -$0.07 |
| **4** | **6,294** | **4.34..4.92** | **32.7** | **1.339** | **+$5,987** | **+$0.95** |
| **5** | **6,294** | **4.92..5.54** | **32.1** | **1.686** | **+$12,855** | **+$2.04** |
| **6** | **6,294** | **5.54..6.23** | **33.4** | **2.723** | **+$28,324** | **+$4.50** |
| **7** | **6,294** | **6.23..7.07** | **30.9** | **2.349** | **+$21,660** | **+$3.44** |
| 8 | 6,294 | 7.07..8.19 | 29.3 | 0.941 | -$908 | -$0.14 |
| 9 | 6,294 | 8.19..9.80 | 30.6 | 0.601 | -$6,023 | -$0.96 |
| 10 | 6,294 | 9.80..21.65 | 33.2 | 0.695 | -$4,398 | -$0.70 |

Deciles 4-7 (VC 4.34..7.07) are jointly profitable: ~25k trips, PF ≈ 2.0, ~+$69k net. **The "winning region" is mid-VC random-walk territory, NOT compressed prices.** This contradicts the original consolidation thesis — the cells that look most like a "coiled spring" (deciles 1-2, VC < 3.8) only barely scrape PF 1.0.

The cvd-flip cover does not produce the same pattern (every decile has PF ≤ 1.0 except dec 1).

## Drilling into the donchian-stop short / 24h-down / VC 4-7.5 slice

31,469 trips, baseline PF **1.82**, net **+$69,955**, +$2.22/trade. Looks like the real edge.

5×5×5 quintile cube on `vol_rvol × bar_vol_multiple × range_width_pct` (VC already constrained):

| 1D dim | best quintile range | PF | net P&L |
| ------ | ------------------- | -- | ------- |
| vol_rvol | q5: ≥ 3.65 | 2.81 | +$58,427 |
| bar_vol_multiple | q5: ≥ 5.46 | **3.30** | **+$79,133** |
| range_width_pct | q5: ≥ 0.116 | 2.89 | +$61,919 |

All three primary signals concentrate sharply in the top quintile. **bar_vol q5 alone** captures more than the entire slice net P&L (other quintiles are negative).

3D corner cell `rv_q=5, bv_q=5, rw_q=5`: **865 trips, PF 5.41, win 37.1%, +$51,421 net, +$59/trade.** ~3% of the slice yields ~74% of the slice's edge.

Sounds promising. Then:

### Year-by-year stability check — the killer

Whole slice (donchian-stop short / 24h ≤ -10% / VC 4..7.5):

| year | trips  | win % | PF    | net P&L  | avg/trade |
| ---- | ------ | ----- | ----- | -------- | --------- |
| 2024 | 7,002  | 36.3  | 0.926 | -$1,519  | -$0.22    |
| **2025** | **18,283** | **31.1** | **2.82**  | **+$82,487** | **+$4.51** |
| 2026 | 6,184  | 28.8  | **0.44**  | -$11,013 | **-$1.78** |

bar_vol q5 only:

| year | trips | win % | PF    | net P&L  |
| ---- | ----- | ----- | ----- | -------- |
| 2024 | 1,037 | 44.8  | 1.637 | +$3,908  |
| **2025** | **3,659** | **31.5** | **5.82**  | **+$83,694** |
| 2026 | 1,597 | 25.9  | **0.22**  | **-$8,469** |

3D corner cell:

| year | trips | win % | PF    | net P&L  |
| ---- | ----- | ----- | ----- | -------- |
| 2024 | 97    | 10.3  | **0.06**  | -$1,955  |
| **2025** | **548** | **46.9** | **8.42**  | **+$54,868** |
| 2026 | 220   | 24.5  | **0.32**  | -$1,492  |

**The entire edge is a single 2025 calendar year.** 2024 is barely break-even or losing. **2026 is significantly worse than the unfiltered baseline** at every aggregation level — the slice is *anti-edge* in the most recent five months. The slice baseline PF of 1.82 is a 2025-regime artifact; out-of-sample the strategy is a stop-loss.

Possible interpretations (all unverified):
- 2025 had a sustained altcoin bear-continuation regime that paid short-after-24h-down breakouts.
- 2026 is the bounce-back / regime shift where shorts get squeezed.
- Survivorship of low-quality 2024 listings going through one terminal drawdown.

None of these would produce a strategy you'd ship.

## Conclusion

The visual-chart intuition that consolidation+volume = tradable breakout does not survive contact with quant analysis on this universe:

1. **Strict-spec gates almost never fire** — 30-odd trips over 2 years across 643 symbols.
2. **Wide-open gates are strongly negative** — PF 0.56-0.60 across 7M+ trips per cover mode.
3. **The only positive cell** (donchian-stop short / 24h-down / VC 4-7.5) **is regime-dependent** — entire +$70k net P&L lives in 2025, with 2026 reversing into a worse-than-baseline loser.
4. **The "winning compression region" contradicts the original thesis** — it's mid-VC random-walk territory, not compressed prices. The compression signal itself (vol_compression_ratio < 3) is *anti-correlated* with edge.
5. **The three primary signals (rvol, bar_vol_mult, range_width_pct) all concentrate in their top quintiles**, which is consistent with "elevated activity helps short-after-down" but not with "coiled spring."

## What was tested vs. not

**Tested:**
- Both cover modes (`donchian-stop`, `cvd-flip`).
- Full 5×5 vol-compression × signal grid; 5×5×5 cube on rvol × bar_vol × range_width within the conditioned slice.
- Long and short separately, conditioned on `pct_24h_change_at_entry` direction (≥ 10%, ≤ -10%).
- Per-year stability (2024 / 2025 / 2026).

**Not tested (deferred):**
- Reverse-direction trades (fade the breakout instead of going with it). The decile-9 "long after 24h-up >10%" loses ~30bp/trade — fading would gain that, but only in the same regime-dependent way. Not pursued because the structural pattern across cover modes suggests fading wouldn't escape the regime issue.
- Per-symbol stability of the 2025-only edge. The fact that 2026 reverses across all aggregation levels suggests it's a market-wide regime, not a few symbol whales — but this wasn't directly verified.
- Multi-bar consolidation duration (counter-style "consolidating for at least N bars"). The single-bar check was the user-locked spec; a counter could plausibly recover some signal but would only matter if the underlying compression signal worked, which it doesn't.
- Tighter compression bands and finer rvol thresholds in v0-style locked-gate mode. The wide-open analysis already showed monotonic structure — re-running with tighter gates would only resample the same surface.

## Verdict

**Shelved.** Consolidation-breakout v0 produces neither a robust edge nor a clean signal map that suggests a v1 redesign. The mean-reversion family ([donchian_fade_v0_results.md](donchian_fade_v0_results.md)) remains the workstream. Move to next research direction.

## Reproduction

```bash
# Locked-spec sweep (33-34 trips, no edge signal)
dotnet run --project TradingEdge.CryptoBacktest -c Release -- consol-breakout-sweep \
  --consol-minutes 180 --vol-compression-max-ratio 1 --vol-rvol-min 3 \
  --bar-vol-min-multiple 2 --cover-mode donchian-stop \
  --reference-vol-pct 0.1019 --max-bar-price-ratio 3 \
  --min-short-adv 5000000 --min-long-adv 5000000 \
  --results-csv data/crypto/consol_breakout_donchian/backtest_results.csv \
  --summary-csv data/crypto/consol_breakout_donchian/backtest_summary.csv -p 8

# Gates-open sweep (every range break -> 7M+ trips per cover mode)
dotnet run --project TradingEdge.CryptoBacktest -c Release -- consol-breakout-sweep \
  --consol-minutes 180 --vol-compression-max-ratio 100 --vol-rvol-min 0 \
  --bar-vol-min-multiple 0 --cover-mode donchian-stop \
  --reference-vol-pct 0.1019 --max-bar-price-ratio 3 \
  --min-short-adv 5000000 --min-long-adv 5000000 \
  --results-csv data/crypto/consol_breakout_donchian_open/backtest_results.csv \
  --summary-csv data/crypto/consol_breakout_donchian_open/backtest_summary.csv -p 8
```

Trip CSVs produced:
- `data/crypto/consol_breakout_donchian_open/backtest_results_trips_1m_consol180_vc100_rv0_bv0.csv` (7.1M rows)
- `data/crypto/consol_breakout_cvd_open/backtest_results_trips_1m_consol180_vc100_rv0_bv0.csv` (7.7M rows)

Narrow slice for the 2025-only finding:
- `data/crypto/consol_breakout_donchian_open/narrow_short_24hdown_vc4to75.csv` (31,469 rows)
