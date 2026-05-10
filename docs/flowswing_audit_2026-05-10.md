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
