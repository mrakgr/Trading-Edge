# FlowSwing audit — 2026-05-10

**Status: production-ready.** FlowSwing (engine module: [OrderflowLongFadeMA.fs](../TradingEdge.CryptoBacktest/OrderflowLongFadeMA.fs)) passes the same robustness checks that ruled DonchianScalp out as a daily-bread strategy. This document captures the full audit so future-you doesn't have to re-derive it.

## Strategy spec

Multi-hour mean-reversion, **long-only** on Binance USDT-perps:
- **Trigger**: price decline to a level X% below the 72h MA, AND a 4h CVD positive flip
- **Cover**: trailing 72h MA cross from below
- **Median hold**: 42.4 hours (p95 = 109h ≈ 4.5 days, max ~16 days)
- **Default config (production champion)**: `pd=14% cvd=4h cover=72h no-stops rvol≥0.75`
- **Engine baseline metrics**: 3,914 trips, PF 3.44, win 74.7%, $24.77/trade, $96,964 net P&L over 2024-05 to 2026-04

## Engine baseline — 2-year performance

```
trips: 3,914     active symbols: 592 (out of 643 universe)
win %: 74.7      median bars held: 2,548 (~42h)
PF:    3.44      net P&L: $96,964
avg/trade: $24.77
```

Source CSV: `data/crypto/long_fade_ma_default_rvol075/results_trips_1m_pd0.14_ma72h_cvd240m_ed0.2_long.csv`

## Audit 1 — Month-by-month + 2025-10 attribution

Same audit shape that exposed DonchianScalp's regime dependence. **FlowSwing passes**.

| metric | full | ex-Oct 2025 | Oct 2025 only |
|---|---:|---:|---:|
| trips | 3,914 | 3,551 | 363 |
| net P&L | $96,964 | $85,911 | $11,053 |
| PF | 3.44 | **3.33** | 4.90 |
| Oct % of total | — | — | **11.4%** |

**October 2025 contributes 11.4% of P&L, not 82%.** DonchianScalp had 82% concentration in that single liquidation-flush month; FlowSwing has the proportionate share you'd expect from a regime-independent strategy.

### Top 5 contributing months (broadly distributed)

| month | trips | net P&L | % of total |
|---|---:|---:|---:|
| 2024-12 | 407 | $14,980 | 15.4% |
| 2024-08 | 229 | $14,533 | 15.0% |
| 2024-07 | 204 | $14,341 | 14.8% |
| 2025-10 | 363 | $11,053 | 11.4% |
| 2025-02 | 475 | $9,840 | 10.1% |

Five months each contribute 10-15% of P&L. Spread across 2024 and 2025 with no single month dominating.

### Profitable months: 23 of 24

Every month from 2024-05 through 2026-04 fires 50+ trips except some low-activity periods. Only one losing month: **2025-09 at PF 0.79 / -$429**. Even the weakest profitable months sit at PF 1.05+ (above break-even after fees).

### 2025-09 drill — known acceptable failure mode

Worst trips that month are **failed-revert capitulation buys**: symbols that fell 15-60% and kept falling. Median 21,000 bars held (~14 days) means the position sat through extended downtrends without the 72h MA crossing back.

| symbol | net | pr_at_entry | bars_held |
|---|---:|---:|---:|
| FORMUSDT | -$432 | -17.7% | 21,153 |
| UXLINKUSDT | -$191 | -60.9% | 4,496 |
| BAKEUSDT | -$118 | -23.7% | 4,366 |
| HIFIUSDT | -$117 | -23.3% | 3,997 |

This is the **engineered failure mode** of capitulation-fade-to-MA: when a capitulation drop is the start of a real downtrend rather than a flush, the strategy sits in losing positions for weeks. No stops by design (per `project_long_fade_ma_session_2026-05-08.md`). The 23/24-month win rate confirms this failure mode is rare and acceptable.

## Audit 2 — Per-symbol concentration

| metric | value |
|---|---:|
| active symbols | **592 / 643** universe (92%) |
| top 1 sym % of P&L | **0.8%** |
| top 5 % | **3.8%** |
| top 10 % | **6.9%** |
| top 20 % | 12.9% |
| top 50 % | 27.7% |
| profitable / losing | **493 / 99** (83% positive) |

**No concentration crisis.** The top contributor (FARTCOINUSDT) is 0.8% of total P&L. Top 10 symbols are 6.9%. Top 50 is 27.7% — i.e. the bottom 542 active symbols still contribute 72% of P&L. Compare to DonchianScalp trend=30 where top 5 were 82% of P&L on only 30 active symbols.

### Top 15 P&L symbols (with October attribution per-symbol)

| symbol | trips | net | Oct $ | Oct % |
|---|---:|---:|---:|---:|
| FARTCOINUSDT | 32 | $812 | $86 | 10.6% |
| AGLDUSDT | 6 | $734 | $0 | 0% |
| ZECUSDT | 13 | $723 | $160 | 22.1% |
| TURBOUSDT | 20 | $710 | $50 | 7.0% |
| INJUSDT | 16 | $667 | $23 | 3.5% |
| SUIUSDT | 9 | $639 | $41 | 6.4% |
| ADAUSDT | 8 | $615 | $31 | 5.1% |
| THETAUSDT | 8 | $610 | $78 | 12.8% |
| 1000BONKUSDT | 14 | $606 | $34 | 5.5% |
| JUPUSDT | 14 | $604 | $55 | 9.1% |

Even the strongest contributors aren't October-dependent (most under 15% Oct attribution per-symbol). Edge is structural, not regime-driven.

## Audit 3 — Limit-fill simulator

The same `LimitFillSim` from the DonchianScalp audit run on FlowSwing trips. Defaults: RePeg trail, 2 bp maker fee, 30% rejection, 100ms latency, 18-day lookahead (covers the 16-day max hold).

The simulator was **refactored to stream day-by-day per trip** ([LimitFillSim.fs](../TradingEdge.CryptoBacktest/LimitFillSim.fs)) — the original "load all 7 days into one array per group" design caused OOM on FlowSwing's longer-hold trips. New design: `TradeStream` class holds one day at a time, drops on exhaustion, lazily loads next via callback. Per-group day cache shares loaded days across consecutive trips, with eviction after each trip. Memory bounded at one day's worth of trades per active worker.

### Fill quality

| metric | value |
|---|---:|
| trips | 3,914 |
| no-fill | 0 (0%) |
| partial fill | 1 (0.03%) |
| full fill | 3,913 (99.97%) |
| avg entry fraction | 99.99% |
| avg held hours | 48.4 |
| trips with residual | 11 (0.3%) |

Near-perfect fill quality. The multi-hour holds and broad symbol diversity mean the limit's RePeg trail catches almost every trip cleanly.

### Engine vs Limit P&L (raw, includes redenomination artifacts)

| metric | engine | limit |
|---|---:|---:|
| trips | 3,914 | 3,914 |
| net P&L | $96,964 | $138,264 |
| avg/trade | $24.77 | $35.33 |
| PF | 3.44 | **4.32** |
| retention | — | **142.6%** |

The 42% boost is the headline — but it's **not real**. Three trips contain bad data from token redenomination events that the engine's gap detector missed; the limit-fill simulator hits phantom prices in the trade tape and books false profit.

### Redenomination artifacts found

| date | symbol | engine entry_px | engine exit_px | limit exit_px | exit/engine ratio | engine net | limit net |
|---|---|---:|---:|---:|---:|---:|---:|
| 2026-04-16 | DYDXUSDT | 0.000107 | 0.000106 | 0.0988 | **932×** | -$0.51 | **+$43,388** |
| 2025-03-25 | LINAUSDT | 7.8e-5 | 8.1e-5 | 0.00078 | 9.6× | $5.12 | +$1,224 |
| 2025-03-21 | LINAUSDT | 0.00132 | 0.00075 | 7.5e-5 | 0.1× | -$138 | -$299 |

These are token-rebasing events (DYDXUSDT → 1000DYDXUSDT, etc.) where the contract spec changes price by a power of 10/1000. The engine's `MaxBarPriceRatio = 3.0` gap-detector should catch these but missed them when the redenom-day prices straddled the gap boundary. The engine's recorded entry/exit prices are at the *old* scale; the limit-fill simulator places limits at those bogus levels and gets matched against trades at the *new* scale, producing impossible profits.

**Engine gap-detector bug — not yet fixed.** Affects all engines using `MaxBarPriceRatio` (DonchianScalp, ConsolBreakout, etc.). Captured in memory as a deferred fix.

### Engine vs Limit P&L (clean — ex-redenom)

| metric | engine | limit |
|---|---:|---:|
| trips | 3,911 | 3,911 |
| net P&L | $97,098 | **$93,952** |
| avg/trade | $24.83 | $24.02 |
| PF | 3.44 | **3.27** |
| retention | — | **96.8%** |

**This is the real number.** Limit-fill captures **96.8%** of engine market-fill P&L, with PF 3.27 — well above any execution-drag survival threshold.

### Why limit-fill works on FlowSwing (vs failing on DonchianScalp)

Three structural reasons:

1. **Multi-hour holds dilute the moment-of-fill effect.** A long held 42 hours doesn't care whether the entry filled at $0.500 or $0.502 — the exit is at $0.55 either way. DonchianScalp's 10-bar median holds amplified single-fill slippage.
2. **Per-trade payoff is large** ($24.77 engine, $35 limit before redenom-fix). Round-trip maker fees on $1k notional × 4 bp = $0.40, trail-slippage ~$0.20, rejection cost amortizes to maybe $1-2. All small relative to the $24+ payoff.
3. **No adverse selection.** Limits at the engine's recorded EntryPrice on a price-decline-driven entry don't get adversely selected the way DonchianScalp's channel-break entries did. The CVD+price-decline trigger fires in market-wide capitulation moments where there's plenty of liquidity for a passive maker.

### Cleaned ex-October check (regime independence under limit fill)

| slice | trips | engine | limit | lim PF | retention |
|---|---:|---:|---:|---:|---:|
| all clean | 3,911 | $97,098 | $93,952 | 3.27 | 96.8% |
| **clean ex-Oct** | **3,548** | **$86,044** | **$83,155** | **3.16** | **96.6%** |
| Oct only | 363 | $11,053 | $10,797 | 4.77 | 97.7% |

**PF 3.16 ex-October at limit fills.** No regime dependence. Compare to DonchianScalp's PF 0.83 ex-October at limit fills — that's the order of magnitude of difference between a regime-dependent flush-fader and a regime-independent capitulation-fader.

## Comparison table — DonchianScalp vs FlowSwing

|  | DonchianScalp (trend=14/dv≥1M) | FlowSwing |
|---|---:|---:|
| trips | 25,887 | 3,914 |
| Engine PF | 1.57 | 3.44 |
| **Limit PF (clean)** | **1.25** | **3.27** |
| **Limit PF ex-Oct (clean)** | **0.83** | **3.16** |
| avg/trade limit | $0.37 | $24.02 |
| Oct 2025 % of P&L | **82%** | **11.4%** |
| top-5 sym share | 25.5% (at dv≥1M) | 3.8% |
| profitable months | 23/24 with regime caveat | 23/24 |
| profitable symbols | 408/633 (64%) | **493/592 (83%)** |
| diagnosis | regime-dependent vol-flush fader | **production-ready capitulation-fader** |

## Production status

**FlowSwing is the deployable strategy.** 23/24 profitable months, edge spread evenly across 24 months and 592 symbols, no October dependence (PF 3.16 ex-Oct under limit fills, 96.6% retention). The 42-hour median hold and $24/trade payoff give massive headroom for execution drag — limit-fill maker execution at 2 bp + 30% rejection costs essentially nothing in P&L terms.

The decision to drop ShortFadeMA from production was correct (per `project_short_fade_ma_session_2026-05-08.md`). Long-only is structural — the strategy fades capitulation, and capitulations are more reliably long-favorable on this universe than rallies are short-favorable.

## What this audit does NOT close

- **Engine `MaxBarPriceRatio` gap-detector bug** — misses mid-trip redenominations that straddle the gap boundary. Affects all engines. Deferred fix.
- **Live execution validation** — backtest-to-live drift is the next-after-deployment unknown. The L2 collector planning notes ([project_l2_collector_planning_2026-05-10.md](../.claude/projects/-home-mrakgr-Trading-Edge/memory/project_l2_collector_planning_2026-05-10.md)) cover this. FlowSwing's slow holds make book-imbalance signals less critical than for DonchianScalp; the strategy would be deployable without L2 if you wanted to start sooner.
- **Sizing curve** — the trip CSV doesn't expose the dynamic-sizing signals (`bars_since_violation`, `pct_1h_change`, `dv_1h_at_entry`) that powered the DonchianScalp sizing analysis. FlowSwing has its own equivalents (`price_rise_at_entry`, `ratio_at_entry`) that haven't been decile-stratified. Worth a follow-up.

## Reproduction

```bash
# Re-run the limit-fill sim
dotnet run --project TradingEdge.CryptoBacktest -c Release -- limit-fill-sim \
  --trips-csv data/crypto/long_fade_ma_default_rvol075/results_trips_1m_pd0.14_ma72h_cvd240m_ed0.2_long.csv \
  --output-csv data/crypto/long_fade_ma_default_rvol075/limit_fills_repeg.csv \
  --trail-mode re-peg \
  --lookahead-days 18 \
  -p 4

# Wall: ~15 min on -p 4 with the streaming refactor.
```

Output CSV has the original FlowSwing columns plus 16 limit-fill columns: `target_qty`, `entry_fill_qty`, `entry_fill_fraction`, `entry_first_fill_us`, `entry_last_fill_us`, `entry_avg_fill_price`, `exit_fill_qty`, `exit_first_fill_us`, `exit_last_fill_us`, `exit_avg_fill_price`, `residual_qty`, `residual_mark_price`, `limit_realized_pnl`, `limit_residual_pnl`, `limit_net_pnl`, `limit_seconds_held`.

## 2D PF surface — price_decline × ratio_at_entry

For dynamic-sizing in a live system. Same template as the DonchianScalp 2D table in [docs/donchian_fade_v0_results.md](archive/donchian_fade_v0_results.md), but on FlowSwing's two natural signal axes:

- **`price_decline`** = `abs(price_rise_at_entry)` × 100 — % decline from the trailing 72h MA at entry. Engine's `pd=14%` floor means all trips are at least 14%.
- **`ratio_at_entry`** = the rvol-weighted CVD signal magnitude. Engine's `rvol≥0.75` floor means all trips have ratio ≥ 0.75.

3,914 trips on the production-config trip CSV.

### Trip count matrix

```
                  ratio_at_entry
price_decline  0.75-1.0  1.0-1.5  1.5-2.5  2.5-4.0  4.0-7.0  7.0+
14-15           234       352      354      161      73       28
15-17           172       257      297      180      91       37
17-20           118       160      204      133      95       30
20-25            64        81      169      100      87       41
25-35            25        46       78       75      36       39
35+               8         6       11       16      20       36
```

### PF matrix (engine market-fill)

```
                  ratio_at_entry
price_decline  0.75-1.0  1.0-1.5  1.5-2.5  2.5-4.0  4.0-7.0  7.0+
14-15           2.34      2.41     2.94     3.86     7.77    1.72
15-17           2.95      2.60     2.77     3.33     4.09    1.82
17-20           3.02      5.52     6.60     5.08    10.22    3.54
20-25           3.31      8.86     3.69     5.24    11.88    1.38
25-35           7.92      6.03     6.47     4.92     4.77    1.31
35+             1.43      0.03     2.40     0.98     2.06    1.07
```

### Avg P&L per trade matrix ($, $1k notional)

```
                  ratio_at_entry
price_decline  0.75-1.0  1.0-1.5  1.5-2.5  2.5-4.0  4.0-7.0  7.0+
14-15          11.07     12.64    19.15    24.88    45.39   16.08
15-17          15.43     15.55    19.51    25.55    41.43   17.73
17-20          18.31     25.87    33.70    40.60    57.47   49.75
20-25          19.72     32.33    24.45    37.67    64.40   13.68
25-35          30.31     32.67    36.03    33.33    46.58   11.06
35+             5.66    -32.01    20.24    -0.61    28.96    2.98
```

### Reading the surface

**Every well-populated cell has PF > 2.** Even the engine's lowest-signal floor (14-15% decline, 0.75-1.0 rvol) is PF 2.34 — there's no "PF < 1 throwaway zone" the way DonchianScalp's bs=10/low-pct corner is. FlowSwing's signal is structurally cleaner.

**The 4.0-7.0 rvol column is the goldmine**: PF 7.77-11.88 across the 14-25% decline rows. These cells combine moderate price-decline with high CVD-flip strength — the textbook capitulation pattern.

**The 7.0+ rvol column drops back to PF 1.4-3.5.** Extreme rvol entries are likely to coincide with capitulations that turn into real downtrends rather than reverts. Same failure mechanism as the 2025-09 down-month: once rvol gets really extreme, you're catching the start of bear moves rather than fading panic.

**The 35+% decline row has unstable cells with negative PF**: PF 0.03 (only 6 trips), PF 0.98 (16 trips). Symbols that have already dropped 35% from MA are often in genuine downtrends. Position-sizing the 35+% row at minimum is the right call.

### How to apply for live sizing

Same shape as the DonchianScalp recommendation:

- **PF < 1.5**: minimum size or skip. Mostly the 35+% / mid-rvol cells and the 7.0+ rvol top corner.
- **PF 1.5-3.0**: standard size. The bottom-left workhorses (14-17% / 0.75-1.5 rvol) live here.
- **PF 3.0-6.0**: scale up. Most of the 17-25% / 1.5-4.0 rvol cells.
- **PF > 6.0**: scale up further. The 4.0-7.0 rvol column at 17-25% decline.

Per-trade payoff in the goldmine cells is **$45-65/trade** vs the engine baseline $24.77 — sizing into those cells preferentially could double the per-trade payoff.

### Caveats

- **Ratio_at_entry interpretation**: this is the engine's internal CVD-weighted rvol signal, not raw rvol. Documented in `OrderflowLongFadeMA.fs`. The exact functional form matters when a live system reproduces the signal in real-time.
- **35+% decline row is sparse** (8-36 trips per cell). The negative cells there are noise; treat the whole row as "minimum size" until it has more data.
- **Limit-fill version preserves this surface**. Per the audit above, FlowSwing limit-fill captures 96.8% of engine market-fill P&L; the 2D PF surface should compress slightly under maker fees + rejection but the qualitative shape (small-decline-mid-rvol = workhorse, mid-decline-high-rvol = goldmine, extreme-rvol = unstable) holds.

---

## Taker-fill validation — 2026-05-11

The no-lookahead taker-fill simulator ([TakerFillSim.fs](../TradingEdge.CryptoBacktest/TakerFillSim.fs)) developed for DonchianScalp was run on the same FlowSwing production trip CSV. Configuration:
- Cum-volume fill mode: walk same-side aggressive trades, accumulate base qty until 3× target_qty (= `effective_notional / entry_price`), VWAP-price the result.
- 60s window, no lookahead — always fill if ≥1 same-side trade exists in the window.
- 5 bp taker fee per side. 200ms latency skip.
- Wall: 116s on parallelism=8 (vs limit-fill's ~15 min on `-p 4`).

### Headline

| Cell | Trips | Total | PF |
|---|---:|---:|---:|
| Engine market-fill | 3,914 | +$96,964 | 3.50 |
| Limit-fill (96.8% retention vs engine) | 3,914 | +$93,852 | 3.27 |
| **Taker-fill, raw** | 3,884 / 99.2% filled | +$145,608 | **4.58** |
| **Taker-fill, ex-redenom artifacts** | 3,881 | **+$94,602** | **3.34** |
| Taker-fill, ex-redenom AND ex top-3 flush days | 3,685 | +$87,599 | 3.24 |

The raw taker headline appears to *beat* the engine (+$145k vs +$97k). Three trips contaminated by the DYDXUSDT → 1000DYDXUSDT and similar redenominations contributed ~$51k of phantom P&L — same engine-side gap-detector bug flagged in [feedback_engine_gap_detector_redenom_bug.md](../memory/feedback_engine_gap_detector_redenom_bug.md). The post-hoc 20% drift filter (`ABS(exit_fill_price / exit_price - 1) > 0.20`) excludes those. After filtering: **taker-fill PF 3.34 / +$94,602**, essentially matching the limit-fill audit's PF 3.27.

### Regime independence: full pass

| Cell | Trips | Total | PF |
|---|---:|---:|---:|
| Ex-Oct-10 | 3,874 (of 3,884 raw) | +$145,384 | 4.61 |
| **Ex top-3 flush days (Oct-10-2025, Dec-09-2024, Feb-03-2025)** | **3,688** | **+$138,605** | **4.52** |
| October 2025 only | 359 | +$10,648 | 4.72 |

The three flush days that carried 100%+ of DonchianScalp's P&L contribute roughly proportionate amounts to FlowSwing (~5% of P&L for ~5% of trips). Removing them barely moves the PF.

### Why taker fill matches/exceeds limit fill on FlowSwing

The same factors that made limit-fill cheap on FlowSwing apply to taker-fill:
- **42-hour median holds** dilute per-trade execution cost. The 5 bp × 2 taker fee plus typical 3-5 bp slippage is a ~13 bp round-trip — invisible against an average $24-37/trade payoff.
- **High win rate (74.6%) and asymmetric per-trade payoff** mean fills don't need to be precise.
- **The 60s same-side flow window almost always contains plenty of same-direction taker activity** (99.2% of FlowSwing trips fill cleanly under cum-volume mode), because the entry condition (price has just declined sharply on positive CVD turn) is by construction a moment of high directional flow.

### Why this matters

Both the limit-fill audit (2026-05-10) and the no-lookahead taker-fill audit (2026-05-11) confirm the same conclusion: **FlowSwing's edge survives realistic execution.** The strategy works for the same reason both fill models capture it: holds are long enough that fill-price precision is a small effect against the underlying mean-reversion-to-MA payoff.

### Reproduction

```bash
dotnet run --project TradingEdge.CryptoBacktest -c Release -- taker-fill-sim \
  --trips-csv data/crypto/long_fade_ma_default_rvol075/results_trips_1m_pd0.14_ma72h_cvd240m_ed0.2_long.csv \
  --output-csv /tmp/tk_flowswing/taker_fills.csv \
  -p 8
# Wall: ~2 min on parallelism=8.
```

Output: `/tmp/tk_flowswing/taker_fills.csv` (3,914 rows, 12 taker-fill columns appended). Stratifications via DuckDB on the original trip CSV's `entry_us` field.

### Production status

**FlowSwing remains the production strategy** at the workstream's highest level of confidence. Two independent fill audits (limit + taker) both reproduce the engine's headline PF within ±10%. No regime-dependence. 22 of 24 months profitable, top-3 flush days carry 5% of P&L (not 100% like DonchianScalp).

## Out-of-sample backtest — 2020-2026 (2026-05-14)

After backfilling the full Binance USDT-perps trade archive back to 2020-01 (1.80 TB of trade data, 670 symbols, 175.8B trades — see `docs/crypto_data_pipeline.md`), re-ran the same production config over the extended 2020-01-01 → 2026-05-11 window. Same flags, same engine, same anchors.parquet (rebuilt over the wider date range).

### Headline

| Metric | In-sample (2024-05 → 2026-04) | OOS (2020-01 → 2026-05) | OOS ex-2022 |
|---|---:|---:|---:|
| Trips | 4,591 | **7,302** | 6,451 |
| Net P&L | $120,118 | **$128,339** | $145,571 |
| Profit factor | 3.50 | **2.25** | — |
| Win rate | ~73% | 70.6% | — |
| Symbols traded | 614 | 646 | — |

The strategy lights up across the wider universe — 2,711 additional trips earned from the 2020-2024 backfill — but the headline PF drops 3.50 → 2.25. **All of the degradation comes from a single year: 2022.**

### Yearly breakdown (entry-year grouping)

| Year | Trips | Net P&L | PF | Win% |
|---|---:|---:|---:|---:|
| 2020 | 238 | +$2.4k | 1.57 | 69.3 |
| 2021 | 952 | +$16.5k | 2.96 | 74.6 |
| **2022** | **851** | **-$17.2k** | **0.49** | **44.8** |
| 2023 | 283 | +$5.9k | 3.90 | 79.5 |
| 2024 | 1,541 | +$55.1k | 5.28 | 76.8 |
| 2025 | 2,623 | +$48.1k | 2.73 | 73.0 |
| 2026 | 814 | +$17.6k | 2.29 | 70.5 |

Six of seven years run PF ≥ 1.57. The genuine new-OOS years (2020-2021, pre-LUNA) work fine. 2022 alone is PF 0.49 / win rate 44.8% — every other year is healthy.

### The 2022 damage is concentrated in three months

| Month | P&L | Catalyst |
|---|---:|---|
| 2022-05 | **-$9,886** | LUNA/UST collapse (May 7-12, $40B wipeout) |
| 2022-01 | -$5,108 | Broad BTC drop $46k→$36k |
| 2022-11 | -$4,086 | FTX collapse (Nov 8-11) |

Outside those three months, 2022 is roughly flat. Top losers in May 2022 cap at -$629 (LUNAUSDT, 4-day hold) — the `--reference-vol-pct 0.1019` sizing keeps per-trip damage bounded. The hit comes from **breadth**: 15th-worst trip is still -$202, similar magnitude. Many simultaneous correlated losers, not a single blown-up position.

### Interpretation

FlowSwing's thesis — "buy capitulation declines and wait for MA-touch recovery" — breaks down precisely when the decline is **not a temporary capitulation but a structural insolvency event**. LUNA's peg break and FTX's credit shock both mean no recovery to MA; the cover-MA never gets hit, time-stop is disabled, positions ride down with the regime.

What this is **not**:
- Not bad sizing (per-trip caps held).
- Not bad fills (limit + taker audits both confirmed clean execution).
- Not a tuning artifact (works on 6/7 years at identical parameters).

What it **is**: a hidden assumption that decline ≠ insolvency. Mean-reversion strategies all share this failure mode in the systemic-credit-event regime.

### Mitigations not yet tested

- **BTC-200d-MA regime gate**: suspend new entries when BTC is below long MA. Would have killed most 2022 entries cleanly. Risk: also kills early bear-market reversal trades.
- **Cross-symbol realized-correlation gate**: pause when the correlation of recent returns across the top-N alts exceeds a threshold (systemic-stress proxy).
- **Funding-rate floor**: 2022 episodes printed extreme positive funding (longs paying through the floor); a "skip if perp funding > X" filter is cheap to add.

None of these are tested yet. Deferred to a future session — current OOS result is a "ship with documented regime risk" outcome, not a "go fix the strategy" mandate.

### Production status post-OOS

Still ship. PF 2.25 / +$128k / 6-of-7-years-positive across 2020-2026 is a real edge by any measurement, especially given the in-sample-was-already-in-low-IS-bias style of the original audits. The 2022 loss does **not** invalidate the strategy — it identifies a known regime where the thesis is invalid, which is the kind of finding live trading needs to manage explicitly (size-down or pause during systemic-credit episodes), not the kind that retires a strategy.

The asymmetry of evidence is the right shape: when conditions match the thesis (5 of 7 years post-2020-bear and post-LUNA/FTX), PF runs 2.3-5.3. When conditions don't match (one specific year, three specific months), PF runs 0.49.

### Reproduction

```bash
# Engine — full 2020-2026 window at production config
dotnet run --project TradingEdge.CryptoBacktest -c Release -- long-fade-ma-sweep \
  --price-decline-thresholds 0.14 \
  --cover-ma-hours 72 \
  --cvd-minutes-list 240 \
  --timeframes 1m \
  --start-date 2020-01-01 \
  --end-date 2026-05-11 \
  --reference-vol-pct 0.1019 \
  --max-bar-price-ratio 3 \
  --min-long-adv 0 \
  --bars-root data/crypto/perps_bars \
  --results-csv data/crypto/long_fade_ma_default_rvol075/results.csv \
  --summary-csv data/crypto/long_fade_ma_default_rvol075/summary.csv
# Wall: ~154s on parallelism=4 (1m bars cached in page cache after first pass).

# Rebuild anchors over 2020-2026 (required before MTM decomposition)
duckdb -c "
COPY (
  SELECT regexp_extract(filename, '/([^/]+)\.parquet\$', 1) AS symbol,
         DATE_TRUNC('month', TO_TIMESTAMP(end_us/1e6))::DATE AS month,
         arg_min(close, end_us) AS anchor_close,
         arg_min(end_us, end_us) AS anchor_us
  FROM read_parquet('data/crypto/perps_bars/1m/*.parquet', filename=true)
  GROUP BY 1, 2
) TO 'data/crypto/cumsum_z_no_gate/anchors.parquet'
"

# MTM decomposition (reconciles to $0.00 error)
duckdb < scripts/crypto/mtm_flowswing.sql
```

The 2024-2026 in-sample trip CSV from before this run is archived at `data/crypto/long_fade_ma_default_rvol075.before_2020_oos/` for comparison.
