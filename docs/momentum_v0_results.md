# Mid-Cap Momentum v0 — Regime-Dependent, Prints in Parabolic Bull Innings

**Status: working signal with strong regime dependence — exactly the expected shape.** A naive high-volume breakout-to-52-week-high system, long-only, run over the full US equity daily history **2005-01-01 → 2026-05-13** (CS/ADRC universe, ≥$1M trailing dollar volume). It makes large profits in parabolic bull stretches (2020, 2013, 2009, the 2024→2026 AI run) and bleeds in bears / post-blowoff unwinds (2021, 2022, 2023, 2008). Net P&L **+$1.64M** at a fixed $10k/trade, uncapped, no compounding, across **25,799 trips**.

This is **v0**: no regime filter, no market-cap bucketing, no parameter tuning. The point of this run is to characterize *when* this style works — and to see whether the current environment looks like one of its good innings (it does).

## Thesis

From the latest *Market Wizards*: daily-timeframe momentum reportedly shines in the "7th–9th inning" of a bull market, when things go parabolic (Lukas Fröhlich). The current AI-bubble regime may be such an inning. The minimal test: aggressively buy high-volume breakouts to new 52-week highs and trail a stop — then look at *which months and years* it was most profitable over ~20 years, with no regime gating yet. We expect outperformance in parabolic bull periods and roughly flat-to-negative results in bears and dull stretches.

## Locked v0 spec

Evaluated on each day **T**'s close, using **split- and dividend-adjusted** prices (`split_adjusted_prices`):

- **Entry conditions** (all three required):
  - **Up ≥ 5% on the day**: `adj_close / prev_adj_close - 1 ≥ 0.05`.
  - **RVOL ≥ 3**: `adj_volume / avg_volume_4w ≥ 3`, where `avg_volume_4w` is the trailing 4-week average **excluding the current day** (`stock_volume_4w`, a 28-day RANGE window ending one day prior).
  - **New 52-week high**: `adj_close ≥ max(adj_close)` over the **prior 252 trading days** (excluding today).
- **Entry fill**: day **T's close** (same-day close — see caveat).
- **Sizing**: fixed **$10,000 notional per trade**, **uncapped** concurrent positions, **no compounding**. Each trip is independent; the same ticker may hold many overlapping trips.
- **Trailing stop**: the **15-day low** = `min(adj_low)` over the prior 15 trading days (excluding today). A position stops out the first day its `adj_low ≤` that level; the **exit fills at the NEXT day's open** (`adj_open[D+1]`) — deliberately not into the stop area.
- **Stop-only**: the trailing stop is the only exit. No time stop, no profit target. Trips still open at the end of the data are **marked-to-market at the final available adj_close** and flagged `open=1` (so recent months show unrealized P&L).

**Universe**: tickers in the daily data from 2005-01-01 onward (pre-2005 history is used only to warm up the 252/15/28-day windows). A ticker is eligible only after **≥21 prior trading days**. Default to **CS/ADRC security types only** (common stock + ADRs — drops ETFs/warrants/preferreds) and a **≥$1M trailing avg dollar volume** floor. The dollar-volume floor is the v0 **liquidity proxy for "mid cap"**, since we have no shares-outstanding data yet (a true market-cap breakdown is deferred — see Next steps).

All of the above are CLI flags (`--up-threshold`, `--rvol-threshold`, `--lookback-high`, `--stop-low-window`, `--min-prior-days`, `--min-avg-dollar-volume`, `--all-security-types`, `--notional`, `--start-date`, `--end-date`).

## Engine

New project [TradingEdge.MomentumBacktest](../TradingEdge.MomentumBacktest) (added to `TradingEdge.slnx`). A **hybrid SQL + F#** design mirroring the repo's existing patterns:

- **[Signals.fs](../TradingEdge.MomentumBacktest/Signals.fs)** — one DuckDB query per ticker computes the full per-day adjusted series plus the indicator columns (`prev_adj_close`, `rvol`, `hi_252_prior`, `low_15_prior`, `pct_up`) and the `is_entry` flag. Every "excluding today" window uses the `ROWS BETWEEN N PRECEDING AND 1 PRECEDING` idiom (the `1 PRECEDING` upper bound is what drops the current bar), so point-in-time correctness lives in one place. Streamed ticker-by-ticker to bound the .NET heap.
- **[StopWalk.fs](../TradingEdge.MomentumBacktest/StopWalk.fs)** — the path-dependent exit, an F# forward-walk per entry (the same `buildChain`-style fold the crypto continuation-plays engine uses). From entry bar T, scan T+1… for the first bar whose `adj_low ≤ low_15_prior`; exit at the next bar's open. Entry on a ticker's final bar (no next open) or a never-triggered trip → open trip, MTM at the final close.
- **[Reporting.fs](../TradingEdge.MomentumBacktest/Reporting.fs)** — atomic trips CSV + the by-year / by-month breakdown (net P&L, trade count, win rate, profit factor). Mirrors the crypto backtest's `writeAtomic` and profit-factor aggregation (`gross_wins / gross_losses`).

Reads the shared **`data/trading.db`** (DuckDB, 8.3 GB) **read-only**; the required materialized tables (`split_adjusted_prices`, `stock_volume_4w`, `ticker_reference`) are built by `TradingEdge.Massive`'s `ingest-data`/`materialize` step.

**Run**: `dotnet run --project TradingEdge.MomentumBacktest -c Release --`
**Outputs**: `data/equity/momentum_v0/trips.csv` (one row per trip; 14 columns incl. `rvol_at_entry`, `avg_dollar_volume_4w_at_entry`, `pct_up_at_entry`, `open`) and `data/equity/momentum_v0/breakdown.log`.

## Headline result — by year (entry date)

25,799 trips, 681 still open (MTM'd at the 2026-05-13 close). Net P&L **+$1,644,645**. Bucketed by **entry** date ("when the system fired").

| year | trades | win% | PF | net P&L |
| ---- | -----: | ---: | ---: | -------: |
| 2005 | 1008 | 42.9% | 1.11 | +67,062 |
| 2006 | 1193 | 42.8% | 0.99 | −6,603 |
| 2007 | 1248 | 37.7% | 0.83 | −125,413 |
| **2008** | 380 | 29.2% | **0.37** | **−218,917** |
| **2009** | 608 | 39.5% | **2.01** | **+462,291** |
| 2010 | 1132 | 43.9% | 1.13 | +99,590 |
| 2011 | 949 | 33.2% | 0.72 | −176,526 |
| 2012 | 693 | 43.4% | 1.25 | +89,110 |
| **2013** | 1528 | 51.6% | **2.08** | **+745,410** |
| 2014 | 1184 | 39.9% | 0.98 | −14,315 |
| 2015 | 998 | 40.9% | 0.85 | −90,595 |
| 2016 | 890 | 48.7% | 1.20 | +106,223 |
| 2017 | 1613 | 45.9% | 1.28 | +247,315 |
| 2018 | 1303 | 41.3% | 1.00 | −147 |
| 2019 | 968 | 44.4% | 0.95 | −30,406 |
| **2020** | 1929 | 43.1% | **2.32** | **+2,618,489** |
| **2021** | 2542 | 30.7% | **0.65** | **−1,143,069** |
| **2022** | 516 | 28.9% | **0.31** | **−482,904** |
| **2023** | 1010 | 35.1% | **0.47** | **−544,837** |
| 2024 | 1856 | 39.9% | 0.89 | −170,651 |
| 2025 | 1538 | 37.1% | 0.89 | −204,089 |
| **2026** (YTD) | 713 | 46.0% | **1.80** | **+417,627** |

## What the by-year table says

- **It prints in parabolic bull innings.** The four standout years are textbook melt-ups: **2020** (COVID liquidity surge, +$2.6M, PF 2.32), **2013** (the QE-driven momentum bull, +$745k, PF 2.08), **2009** (post-GFC V-recovery, +$462k, PF 2.01), and **2026 YTD** (the AI run, +$418k, PF 1.80). 2017, 2016, 2012, 2010 are solid second-tier bull years.
- **It bleeds in bears and post-blowoff unwinds.** The worst year is **2021** (−$1.14M, PF 0.65) — the system gave a large chunk of its 2020 gains *back* as the post-COVID momentum top rolled over (note 2021 also has the **most trades of any year, 2542** — the signal fires most heavily right as it stops working, a classic late-cycle failure mode). **2022** (rate-hike bear, PF 0.31) and **2023** (PF 0.47) compound the damage; **2008** (GFC, PF 0.37) is the other clear bear.
- **Win rate is always a minority (29–52%); the edge, when it exists, is in the tail.** This is a momentum/trend signature: most breakouts fail the trailing stop quickly for small losses, and a few run far. Profit factor — not win rate — is the regime tell. In good years PF ≫ 1 on a sub-50% win rate; in bad years even a 40%+ win rate posts PF < 0.5 because the winners don't run.
- **The current environment looks like a good inning.** 2024 and 2025 net negative *on the year*, but that masks a sharp month-level alternation (below) — and the most recent months (2025-08/09, 2026-01, 2026-03/04) are among the strongest in the whole sample. This is consistent with a late-bull "7th–9th inning" character: violent, profitable momentum bursts interleaved with equally violent give-backs.

## Full monthly breakdown (by entry month)

The monthly view shows the regime alternation the annual table smooths over — e.g. the +$1.23M of 2020-11 alone, or the −$759k of 2021-02 immediately after 2021-01's +$433k.

> **The full per-month table (2005-01 … 2026-05) lives in [momentum_v0_monthly_tables.md](momentum_v0_monthly_tables.md#full-monthly-breakdown-all-trips).**

### Reading the monthly table

- **The biggest single months are clustered in melt-ups**: 2020-11 (+$1.23M), 2020-12 (+$958k), 2009-12 (+$542k), 2021-01 (+$433k), 2020-05/07 (+$306k/+$302k). Several of these are dominated by a handful of trips that ran far — concentration to watch (a future pass should check top-trip contribution per month, mirroring the Donchian "one-day" caveat in [donchian_fade_v0_results.md](donchian_fade_v0_results.md)).
- **The biggest losing months immediately *follow* the biggest winners**: 2021-02 (−$759k) right after 2021-01's +$433k; the whole 2021 H1 give-back follows the 2020 H2 surge. The signal does not turn itself off at the top — it fires *hardest* into the reversal (2021-01/02 are the two highest-trade-count months in the sample, 501 and 572). This is the single clearest argument for a **regime filter**.
- **2024–2026 is choppy but tilting up late.** Within two net-negative annual numbers, the monthly view shows strong bursts (2024-08 +$119k, 2025-09 +$184k, 2026-01 +$171k, 2026-04 +$185k) interleaved with sharp give-backs (2024-12 −$236k, 2025-02 −$258k, 2025-10 −$226k). The most recent two months (2026-04 PF 3.22, 2026-05 PF 1.19) are positive. Late-bull alternation, with the recent tape leaning bullish.

## Breakdown by average dollar volume (market-cap proxy)

We have no shares-outstanding data yet, so trailing 4-week **average daily dollar volume at entry** is the v0 stand-in for market cap (carried on every trip as `avg_dollar_volume_4w_at_entry`). This run **lowers the liquidity floor to $100k** (`--min-avg-dollar-volume 100000`) to probe whether the edge extends into smaller names than the original $1M-floor run reached. Lowering the floor only *adds* the new sub-$1M tiers — the $1M+ tiers are byte-identical to the original run.

**$100k-floor run: 37,309 trips, net P&L +$3,441,699** (vs the $1M-floor run's 25,799 trips / +$1.64M). The extra +$1.80M is almost entirely the newly-admitted sub-$1M names.

| ADV tier (at entry) | trades | win% | PF | net P&L |
| ------------------- | -----: | ---: | ---: | -------: |
| **$100-300k** | 4812 | 38.4% | 1.12 | +569,414 |
| **$300k-1M** | 6698 | 39.1% | 1.19 | **+1,227,639** |
| **$1-3M** | 6476 | 39.0% | **1.25** | **+1,471,290** |
| $3-10M | 7197 | 38.9% | 1.04 | +220,551 |
| $10-30M | 5486 | 41.1% | 0.97 | −124,281 |
| $30-100M | 4169 | 42.7% | 0.98 | −42,921 |
| $100M-1B | 2301 | 43.5% | 1.04 | +61,845 |
| $1B+ | 170 | 49.4% | 1.42 | +58,160 |

### Reading the dollar-volume table

- **The edge is concentrated in low-liquidity small-caps.** The three lowest tiers ($100k–$3M) carry essentially all the profit: **+$3.27M of the +$3.44M total**. The sweet spot is **$300k–$3M** (PF 1.19 and 1.25, +$1.23M and +$1.47M) — big enough to be tradable, small enough that the breakout-to-new-high momentum hasn't been arbitraged away.
- **The mid-liquidity tiers are dead-to-negative.** $10–30M (PF 0.97) and $30–100M (PF 0.98) lose money; $3–10M is barely above water (PF 1.04). The naive breakout is essentially efficient-market noise once names are liquid enough for institutions to police them.
- **The very smallest tier ($100–300k) is positive but thinner** (PF 1.12) than $300k–$3M — diminishing returns at the bottom, consistent with rising real-world frictions (spread, impact, borrow, capacity) that this v0 does **not** model. **This is the critical caveat: the lowest tiers are exactly where the unmodeled costs bite hardest**, so their gross edge overstates the net. Capacity at $100–300k ADV is also tiny relative to a six-figure-plus book.
- **Win rate rises monotonically with liquidity (38% → 49%) while profit factor does not.** Bigger names win slightly more often but their winners don't run — the low-tier edge is a fat right tail (a few big momentum runners), not a higher hit rate. Same momentum signature as the time-series view.
- **The $1B+ tier (PF 1.42) is a small-sample curiosity** (170 trips) — mega-cap breakouts on 3× RVOL are rare and these few ran well; not enough to lean on.

**Takeaway:** "mid-cap momentum" is a misnomer for what actually works here — the edge is a **small-cap / low-float** breakout effect, strongest in the $300k–$3M ADV band, and it decays as names get liquid. The next refinement should (a) realistically cost the low tiers to see how much survives, and (b) treat the dollar-volume band as a primary *filter*, not just a reporting cut.

Outputs for this run: `data/equity/momentum_v0/trips_adv100k.csv`, `data/equity/momentum_v0/breakdown_adv100k.log`.

## Dollar-volume sweet-spot sliced by time (within $100k–$3M)

The earlier dollar-volume cut showed the edge living in the $100k–$3M band; the time-series view showed strong regime dependence. This section crosses the two: a **yearly and monthly breakdown for each of the three sub-buckets** in that band, from the $100k-floor run (`trips_adv100k.csv`). As expected, every bucket is regime-dependent — each one prints in the parabolic-bull years (2009, 2013, 2020) and bleeds in the bears (2008, 2022, 2023) — but the *degree* and the recent (2024–2026) behaviour differ by bucket.

**Cross-bucket reading (yearly view):**

- **All three share the bull/bear regime spine** — each posts big positive years in 2009, 2013, 2020 (PFs ~2–4.5) and loses in 2022–2023. The momentum edge is conditional on regime *within every liquidity slice*, so the dollar-volume cut and a future regime filter are complementary, not substitutes.
- **The two upper buckets ($300k–1M, $1–3M) are the engine of the melt-up years**: $1–3M alone made +$564k in 2009, +$441k in 2013, +$618k in 2020; $300k–1M made +$420k in 2012, +$442k in 2013, +$425k in 2020. These are where the fat-tailed winners concentrate.
- **The smallest bucket ($100-300k) marches to a different drum.** It was the *only* bucket that made money in 2008 (PF 3.04, +$134k) and 2018 (the larger buckets lost there), but it has been net-negative every year 2022→2026. Lowest capacity, highest unmodeled-cost exposure, and the most idiosyncratic — least trustworthy of the three despite its positive lifetime total.
- **Recent regime (2024–2026) splits the buckets:** $1–3M has stabilised (2024 −$82k, 2025 −$65k, **2026 +$23k / PF 1.17**), $300k–1M is still bleeding (2025 −$242k, 2026 −$61k), and $100-300k is uniformly weak. If the current AI-bull is a good inning, it is showing up **first and most cleanly in the $1–3M band** — consistent with that being the most tradable, least-frictional slice of the edge.

### Bucket $100-300k

**4,812 trips, net P&L +569,415.**

#### By year (entry)

| period | trades | win% | PF | net P&L |
| --- | --: | --: | --: | --: |
| 2005 | 211 | 36.5% | 0.96 | −7,234 |
| 2006 | 227 | 39.2% | 1.17 | +27,934 |
| 2007 | 199 | 39.2% | 1.21 | +30,328 |
| 2008 | 67 | 35.8% | 3.04 | +134,474 |
| 2009 | 209 | 40.2% | 1.21 | +40,733 |
| 2010 | 260 | 46.2% | 2.21 | +204,183 |
| 2011 | 155 | 32.9% | 1.11 | +15,063 |
| 2012 | 181 | 50.8% | 2.64 | +156,542 |
| 2013 | 335 | 51.9% | 2.36 | +288,520 |
| 2014 | 218 | 38.5% | 0.96 | −6,376 |
| 2015 | 135 | 34.1% | 0.48 | −67,933 |
| 2016 | 243 | 41.6% | 1.25 | +49,269 |
| 2017 | 277 | 39.4% | 1.55 | +131,074 |
| 2018 | 213 | 36.6% | 0.68 | −72,879 |
| 2019 | 202 | 43.1% | 1.23 | +43,719 |
| 2020 | 307 | 37.1% | 1.19 | +81,344 |
| 2021 | 270 | 35.2% | 0.90 | −26,744 |
| 2022 | 128 | 32.8% | 0.46 | −92,228 |
| 2023 | 267 | 28.5% | 0.64 | −149,243 |
| 2024 | 349 | 33.0% | 0.71 | −122,149 |
| 2025 | 292 | 30.8% | 0.84 | −75,375 |
| 2026 | 67 | 35.8% | 0.79 | −13,607 |

#### By month (entry)

> **Monthly table for this bucket: [momentum_v0_monthly_tables.md](momentum_v0_monthly_tables.md#bucket-100-300k-by-month).**

### Bucket $300k-1M

**6,698 trips, net P&L +1,227,637.**

#### By year (entry)

| period | trades | win% | PF | net P&L |
| --- | --: | --: | --: | --: |
| 2005 | 301 | 43.9% | 1.73 | +179,742 |
| 2006 | 280 | 38.6% | 1.23 | +44,754 |
| 2007 | 251 | 36.7% | 0.91 | −17,791 |
| 2008 | 94 | 27.7% | 0.40 | −73,126 |
| 2009 | 238 | 37.4% | 0.73 | −68,324 |
| 2010 | 351 | 41.9% | 1.83 | +186,368 |
| 2011 | 229 | 33.2% | 0.92 | −16,576 |
| 2012 | 213 | 41.3% | 3.96 | +419,558 |
| 2013 | 490 | 49.4% | 2.53 | +441,614 |
| 2014 | 337 | 37.7% | 0.76 | −70,647 |
| 2015 | 186 | 36.6% | 0.83 | −25,940 |
| 2016 | 278 | 48.9% | 1.84 | +190,234 |
| 2017 | 412 | 43.2% | 1.64 | +217,145 |
| 2018 | 293 | 38.6% | 1.04 | +12,736 |
| 2019 | 235 | 40.0% | 1.40 | +93,034 |
| 2020 | 443 | 41.5% | 1.81 | +424,813 |
| 2021 | 574 | 35.5% | 0.82 | −112,271 |
| 2022 | 173 | 30.6% | 0.48 | −119,862 |
| 2023 | 340 | 33.2% | 0.61 | −189,138 |
| 2024 | 456 | 36.8% | 1.03 | +14,383 |
| 2025 | 396 | 33.8% | 0.61 | −241,720 |
| 2026 | 128 | 35.2% | 0.56 | −61,349 |

#### By month (entry)

> **Monthly table for this bucket: [momentum_v0_monthly_tables.md](momentum_v0_monthly_tables.md#bucket-300k-1m-by-month).**

### Bucket $1-3M

**6,476 trips, net P&L +1,471,289.**

#### By year (entry)

| period | trades | win% | PF | net P&L |
| --- | --: | --: | --: | --: |
| 2005 | 277 | 45.5% | 1.57 | +93,990 |
| 2006 | 322 | 47.2% | 1.22 | +44,311 |
| 2007 | 259 | 34.7% | 0.92 | −16,085 |
| 2008 | 88 | 20.5% | 0.28 | −75,623 |
| 2009 | 205 | 36.1% | 4.45 | +564,245 |
| 2010 | 349 | 43.6% | 1.45 | +116,531 |
| 2011 | 265 | 34.3% | 0.90 | −19,056 |
| 2012 | 179 | 46.4% | 1.38 | +38,628 |
| 2013 | 435 | 54.5% | 2.94 | +441,260 |
| 2014 | 294 | 34.0% | 0.97 | −8,129 |
| 2015 | 236 | 39.4% | 1.05 | +9,989 |
| 2016 | 249 | 46.6% | 1.18 | +36,203 |
| 2017 | 397 | 46.1% | 1.60 | +141,371 |
| 2018 | 300 | 40.3% | 1.16 | +42,105 |
| 2019 | 218 | 40.4% | 0.88 | −24,175 |
| 2020 | 453 | 38.4% | 2.16 | +617,973 |
| 2021 | 658 | 30.4% | 0.92 | −64,931 |
| 2022 | 141 | 23.4% | 0.32 | −144,705 |
| 2023 | 254 | 29.5% | 0.42 | −199,328 |
| 2024 | 412 | 35.4% | 0.82 | −81,519 |
| 2025 | 341 | 34.0% | 0.87 | −65,120 |
| 2026 | 144 | 38.2% | 1.17 | +23,354 |

#### By month (entry)

> **Monthly table for this bucket: [momentum_v0_monthly_tables.md](momentum_v0_monthly_tables.md#bucket-1-3m-by-month).**

## Volatility & consolidation structure at entry

Two more entry-context dimensions, both computed over the **14 trading days prior** to the breakout (no lookahead), from the $100k-floor run (`trips_adv100k.csv`):

- **14-day ATR%** — the name's baseline daily volatility going into the breakout, `mean(true_range) / entry_close`. `true_range = max(high−low, |high−prev_close|, |low−prev_close|)` (the repo's standard, mirrored from `gap_play`).
- **Consolidation tightness** — `range / (14 × ATR)`, where `range` is the 14-day span (highest high − lowest low). Near **1.0** the prior window trended cleanly; well **below** 1 price chopped in a tight band relative to its daily travel — a "coiled spring." Bounded and scale-free (median 0.29, max 0.99 across all trips).

### By 14-day ATR% at entry (name volatility)

| ATR% tier | trades | win% | PF | net P&L |
| --------- | -----: | ---: | ---: | -------: |
| <3% | 13169 | 44.6% | 1.25 | +1,490,052 |
| **3-5%** | 12804 | 41.5% | **1.29** | **+2,605,032** |
| 5-8% | 8631 | 34.9% | 1.10 | +1,141,220 |
| **8-12%** | 2304 | 26.5% | **0.72** | **−1,325,275** |
| 12-20% | 367 | 22.1% | 0.63 | −386,412 |
| 20%+ | 34 | 14.7% | 0.29 | −82,919 |

**Low-to-moderate baseline volatility wins; high volatility loses hard.** Everything ≤8% ATR is net-positive (sweet spot **3–5%**, +$2.6M, PF 1.29); everything ≥8% bleeds (−$1.79M combined). Win rate falls monotonically with ATR (45% → 15%). A breakout in a calm name follows through; a breakout in an already-frantic name is exhaustion/noise. This is a clean *exclusion* filter: drop entries whose name is already moving >8%/day.

### By consolidation tightness = range / (14 × ATR) at entry

| tightness tier | trades | win% | PF | net P&L |
| -------------- | -----: | ---: | ---: | -------: |
| **<0.40 (tight)** | 30051 | 41.1% | **1.22** | **+5,008,319** |
| 0.40-0.55 | 5659 | 36.5% | 0.98 | −148,505 |
| 0.55-0.70 | 1242 | 32.2% | 0.65 | −767,824 |
| 0.70-0.85 | 286 | 24.8% | 0.41 | −454,013 |
| 0.85+ (trend) | 71 | 21.1% | 0.21 | −196,278 |

**The entire system's edge lives in the tightest bucket.** Breakouts from a tight prior base (ratio <0.40) made **+$5.0M — more than the whole system's net P&L** — while *every* looser tier is net-negative, degrading monotonically as the prior structure widens (PF 1.22 → 0.21; win rate 41% → 21%).

This is the **"coiled spring" thesis — and it reverses the earlier crypto result.** The same range-vs-ATR compression filter *failed* on 1-minute crypto intraday breakouts ([consol_breakout_v0_results.md](consol_breakout_v0_results.md)), but on **daily equities it is the strongest single filter found so far**. The mechanism is plausible: a daily breakout from a tight multi-week base is a genuine accumulation → markup transition, whereas a breakout from an already-wide/trending base is just chasing extension. At 1m crypto that structure is microstructure noise; at the daily horizon it's a real regime.

Caveat on tier sizes: 30,051 of 37,309 trips are *already* <0.40 (most breakouts-to-new-highs do come from tight bases), so this is a high-value **exclusion** of the loose-base tail rather than a way to mint many more trades. Because that tight bucket holds the bulk of the data, it is worth sub-slicing — done next.

#### Inside the tight bucket (<0.40 sub-sliced)

The <0.40 bucket (+$5.0M, 30,051 trips) broken into finer tightness bands. Reconciles exactly to the parent (sub-tiers sum to 30,051 trips / +$5,008,319).

| tightness (within <0.40) | trades | win% | PF | net P&L | avg/trip |
| ------------------------ | -----: | ---: | ---: | -------: | -------: |
| **<0.15** | 244 | 45.9% | **1.69** | +90,320 | **+$370** |
| 0.15-0.20 | 4024 | 41.9% | 1.31 | +780,489 | +$194 |
| 0.20-0.25 | 8074 | 42.1% | 1.29 | +1,579,498 | +$196 |
| 0.25-0.30 | 7646 | 41.6% | 1.21 | +1,160,951 | +$152 |
| 0.30-0.35 | 5876 | 40.2% | 1.16 | +770,149 | +$131 |
| 0.35-0.40 | 4187 | 38.4% | 1.16 | +626,912 | +$150 |

**The edge is a continuum, not a threshold — and it keeps strengthening to the tightest extreme.** Both profit factor and average P&L per trip rise monotonically as the base tightens (PF 1.16 at 0.35-0.40 → **1.69** at <0.15; avg/trip $131 → **$370**, roughly 2× the bucket mean). There is **no saturation and no tail reversal**: the tightest cell (<0.15, 244 trips — small but not trivial) is the single best sub-tier on both PF and per-trip P&L. Win rate also climbs gently (38% → 46%), but as everywhere in this system the real driver is winner size relative to losers (PF), not hit rate.

The **deployable core is the 0.15–0.30 band**: +$3.5M of the +$5.0M across ~20k trips — tight enough to carry strong edge, populated enough to absorb capital. The sub-0.15 tier is the highest-quality signal but thin (≈11 trips/year). Practically: the tighter the pre-breakout base, the better, with diminishing trade count as the cutoff falls.

Both dimensions are now carried on every trip (`atr_pct_14_at_entry`, `range_pct_14_at_entry`, `tightness_14_at_entry`) for future cross-tabulation.

### ATR% within the tight bucket (the two filters are complementary)

Crossing the two volatility dimensions — ATR% tiers *inside* the tight (<0.40) base universe — confirms they are mostly independent and stack cleanly (sub-tiers reconcile to the +$5.0M / 30,051-trip tight total):

| ATR% (tight <0.40) | trades | win% | PF | net P&L | avg/trip |
| ------------------ | -----: | ---: | ---: | -------: | -------: |
| <3% | 11,729 | 44.7% | 1.29 | +1,484,763 | +$127 |
| **3-5%** | 10,410 | 42.1% | **1.37** | **+2,512,473** | **+$241** |
| 5-8% | 6,149 | 36.2% | 1.20 | +1,437,311 | +$234 |
| 8-12% | 1,523 | 28.9% | 0.88 | −313,808 | −$206 |
| 12-20% | 222 | 27.0% | 0.89 | −64,838 | −$292 |
| 20%+ | 18 | 11.1% | 0.29 | −47,583 | −$2,643 |

- **Applying tightness did not collapse the ATR pattern** — it is still strongly positive across all sub-8% tiers, then cliffs. The whole **<8% range works** (<3% PF 1.29, 3-5% PF 1.37, 5-8% PF 1.20); per-trip edge peaks at 3-5% (+$241) but the calm <3% names are reliably profitable too (PF 1.29, $127/trip), just lower-octane. **3-5% sharpens to PF 1.37 here vs 1.29 standalone** — the tightness filter removed losers and concentrated the edge.
- **The ≥8% tiers are net-negative even inside tight bases** (8-12% PF 0.88). A *tight multi-week base that is still a high-daily-ATR name* is a worse trade — the volatility lives in the daily bar even when the range is compressed. So the refined entry is **tight base AND ATR% < 8%** (keep everything below the cliff), not a narrow band.

**Combined `tight AND ATR<8%`: 28,288 trips, PF 1.29, +$5,434,547, +$192/trip** — vs tight-only's 30,051 / PF 1.22 / +$5.0M / $167. Cutting just the ≥8% tail (~1,763 trips) *adds* +$426k and lifts PF and avg/trip: a pure improvement.

**Why tightness did NOT subsume ATR% (the surprising part):** the two are nearly orthogonal — **correlation only +0.25**, and the ATR distribution barely shifts when you filter to tight bases (median ATR 0.037 → 0.035). The reason is that **ATR appears in both metrics, but as a ratio it cancels**: ATR% = ATR/price (absolute daily jumpiness), while tightness = 14-day range / (14 × ATR) has ATR in the *denominator*. So a stock can be **high-ATR yet tight** (big daily moves that keep cancelling → ping-pongs in a band) or **low-ATR yet loose** (small moves all pointing one way → steady trend). "Contracted" and "calm" are genuinely separate properties, which is exactly why the two filters stack instead of one absorbing the other. This is Minervini's "constructive" profile: contracted *and* not too jumpy.

### RVOL and gap size — only useful *conditioned on* a tight base

The two entry triggers themselves — RVOL (`adj_volume / avg_volume_4w`, floor 3×) and the same-day gap (`pct_up`, floor 5%) — show a striking **interaction with tightness**: on raw entries they have *no* usable edge (the big tiers are catastrophic), but on the refined tight + ATR<8% set the relationship **inverts** and bigger becomes better. Both views below; the all-entries set is the trailing-stop baseline (37,309 trips, +$3.44M), the refined set is tight<0.40 AND ATR<8% + expansion-0.70 exit (28,288 trips, +$6.18M). Each pair of tables reconciles to its set.

**RVOL (all entries, trailing-stop baseline):**

| RVOL | trades | win% | PF | net P&L | avg/trip |
| ---- | -----: | ---: | ---: | -------: | -------: |
| 3-4x | 12,533 | 39.1% | 1.19 | +1,778,536 | +$142 |
| 4-6x | 11,289 | 39.9% | 1.19 | +1,686,140 | +$149 |
| 6-10x | 6,877 | 41.6% | 1.19 | +1,091,014 | +$159 |
| 10-20x | 3,444 | 40.6% | **0.97** | −99,935 | −$29 |
| 20x+ | 3,166 | 39.3% | **0.77** | −1,014,056 | −$320 |

**Gap size (all entries, trailing-stop baseline):**

| gap (pct_up) | trades | win% | PF | net P&L | avg/trip |
| ------------ | -----: | ---: | ---: | -------: | -------: |
| 5-8% | 10,526 | 41.1% | 1.28 | +1,644,864 | +$156 |
| 8-12% | 9,251 | 40.2% | 1.28 | +1,730,149 | +$187 |
| 12-20% | 9,267 | 40.9% | 1.22 | +1,615,076 | +$174 |
| 20-40% | 5,593 | 39.5% | 1.12 | +786,675 | +$141 |
| 40%+ | 2,672 | 32.4% | **0.61** | −2,335,066 | −$874 |

On the raw set the pattern is flat-then-cliff: RVOL is a uniform ~1.19 up to 10x then turns **negative** (>20x PF 0.77, −$1.0M), and gap size is fine to 40% then **collapses** (40%+ PF 0.61, −$2.3M — the classic gap-and-fade). Standalone, neither has a usable monotonic edge, and their extremes are where the system bleeds.

**RVOL (refined set):**

| RVOL | trades | win% | PF | net P&L | avg/trip | (all-entries PF) |
| ---- | -----: | ---: | ---: | -------: | -------: | ---: |
| 3-4x | 9,467 | 40.0% | 1.23 | +1,344,460 | +$142 | 1.19 |
| 4-6x | 8,653 | 41.8% | 1.39 | +2,059,253 | +$238 | 1.19 |
| **6-10x** | 5,208 | 44.1% | **1.55** | +1,771,397 | **+$340** | 1.19 |
| 10-20x | 2,490 | 44.3% | 1.45 | +720,678 | +$289 | **0.97** |
| 20x+ | 2,470 | 43.6% | 1.12 | +288,255 | +$117 | **0.77** |

**Gap size (refined set):**

| gap (pct_up) | trades | win% | PF | net P&L | avg/trip | (all-entries PF) |
| ------------ | -----: | ---: | ---: | -------: | -------: | ---: |
| 5-8% | 8,324 | 42.0% | 1.35 | +1,448,224 | +$174 | 1.28 |
| 8-12% | 7,382 | 40.8% | 1.23 | +999,678 | +$135 | 1.28 |
| 12-20% | 7,148 | 42.6% | 1.43 | +1,974,831 | +$276 | 1.22 |
| **20-40%** | 3,809 | 44.3% | **1.48** | +1,463,650 | **+$384** | 1.12 |
| 40%+ | 1,625 | 39.5% | 1.13 | +297,661 | +$183 | **0.61** |

- **On all entries these signals are dangerous at the extremes** — RVOL >10x is net-negative (PF 0.97 → 0.77), and a 40%+ gap is catastrophic (PF 0.61, −$2.3M, the classic gap-and-fade). Standalone, "more RVOL / bigger gap" is *worse*.
- **Conditioned on a tight base they flip to strongly positive.** RVOL rises to a 6-10x sweet spot (PF 1.55, $340/trip) and even 10-20x is excellent (1.45); the 20-40% gap tier is the single best per-trip cell in the study (+$384, PF 1.48), and 40%+ gaps stay positive (1.13). The contraction filter is what separates an **explosive breakout from a base** (institutional accumulation releasing) from a **blow-off / exhaustion spike** (a loose, jumpy name gapping into a reversal). Once you require the tight base, the magnitude of the volume/gap surge becomes a *quality* signal rather than a danger sign.
- **Practical consequence:** do **not** layer a low-RVOL or small-gap cap on top of the tightness filter — that would delete the best cells (6-10x RVOL, 12-40% gaps). The refined entry already keeps the strong ones and the contraction filter neutralises the fade risk that makes big gaps dangerous on raw entries. The only mildly soft cells are the very floors (3-4x RVOL, 8-12% gap) and the extreme tails (20x+, 40%+), both still net-positive on the refined set.

## SPY regime filter (10MA vs 20MA)

The classic momentum regime rule: **only take longs when SPY's 10-day MA is above its 20-day MA** (a fast trend-confirmation on the index). Computed post-hoc on SPY's split-adjusted close — dividends shift the MA levels in parallel, so the crossover is unaffected. Over 2005→2026, **64.5% of trading days are "bull" (10>20MA)**, 35.5% bear/neutral. Each trip's entry date is tagged with the regime in force that day; `trips_adv100k.csv` ($100k floor) is the dataset.

### By regime (whole system)

| regime | trades | win% | PF | net P&L | avg/trip |
| ------ | -----: | ---: | ---: | -------: | -------: |
| **bull (10>20MA)** | 27712 | 39.7% | **1.13** | **+3,023,200** | +$109 |
| bear (10≤20MA) | 9597 | 40.6% | 1.05 | +418,498 | +$44 |

The regime filter helps, but it is a **weaker lever than the volatility filter**. Bull regime roughly **doubles per-trip P&L** ($109 vs $44) and holds **88% of the system's profit** in 74% of the trips — yet bear regime is **not outright negative** (PF 1.05): the naive breakout still grinds a small positive even against the index. On its own, "buy in a bull tape" is a mild edge; tightness is the strong one.

### Tightness × regime (do they stack?)

| tightness | regime | trades | win% | PF | net P&L | avg/trip |
| --------- | ------ | -----: | ---: | ---: | -------: | -------: |
| **<0.40 tight** | **bull** | 21924 | 40.8% | **1.26** | **+4,322,987** | **+$197** |
| <0.40 tight | bear | 8127 | 41.9% | 1.12 | +685,332 | +$84 |
| 0.40-0.55 | bull | 4544 | 36.8% | 0.97 | −158,738 | −$35 |
| 0.40-0.55 | bear | 1115 | 35.2% | 1.01 | +10,233 | +$9 |
| 0.55-0.70 | bull | 967 | 32.7% | 0.61 | −621,093 | −$642 |
| 0.55-0.70 | bear | 275 | 30.5% | 0.74 | −146,732 | −$534 |
| 0.70-0.85 | bull | 222 | 24.8% | 0.38 | −359,997 | −$1,622 |
| 0.70-0.85 | bear | 64 | 25.0% | 0.48 | −94,017 | −$1,469 |
| 0.85+ trend | bull | 55 | 21.8% | 0.18 | −159,959 | −$2,908 |
| 0.85+ trend | bear | 16 | 18.8% | 0.34 | −36,319 | −$2,270 |

**They stack, and the volatility filter dominates.** Three readings:

- **The optimal cell is tight + bull: PF 1.26, +$4.32M, $197/trip** over 21,924 trips — **86% of the tight-bucket profit and 126% of the whole unfiltered system** (the loose tiers are net drags). The practical rule is "**tight base AND bull regime.**"
- **Regime adds a clean second lift inside the tight bucket** — tight+bull ($197/trip) more than doubles tight+bear ($84/trip). But **tight+bear is still solidly positive** (PF 1.12, +$685k): the contraction edge survives a weak tape, it just pays less.
- **The regime filter cannot rescue a loose base.** Every non-tight tier loses money *even in bull regime* (0.40-0.55 bull −$159k, worse as the base widens). A bull market does not make a wide-base breakout work — confirming volatility contraction is the primary edge, the market backdrop secondary.

### Combined rule (tight <0.40 AND bull) by year

| year | trades | win% | PF | net P&L |
| ---- | -----: | ---: | ---: | -------: |
| 2005 | 788 | 41.4% | 1.37 | +179,583 |
| 2006 | 972 | 42.1% | 1.05 | +30,208 |
| 2007 | 946 | 37.4% | 0.92 | −42,101 |
| 2008 | 187 | 43.3% | 1.06 | +6,768 |
| 2009 | 640 | 40.9% | 2.08 | +534,034 |
| 2010 | 1155 | 43.4% | 1.47 | +336,736 |
| 2011 | 758 | 34.6% | 0.91 | −45,460 |
| 2012 | 633 | 47.9% | 2.88 | +551,477 |
| 2013 | 1455 | 53.5% | 2.45 | +904,093 |
| 2014 | 1015 | 39.7% | 1.15 | +95,959 |
| 2015 | 649 | 44.2% | 1.03 | +8,698 |
| 2016 | 631 | 42.6% | 1.40 | +138,204 |
| 2017 | 1560 | 45.0% | 1.53 | +436,962 |
| 2018 | 1097 | 41.1% | 0.89 | −78,863 |
| 2019 | 908 | 44.8% | 1.29 | +168,024 |
| 2020 | 1623 | 42.0% | 2.39 | +2,226,265 |
| 2021 | 2340 | 32.1% | 0.78 | −568,043 |
| 2022 | 313 | 31.0% | 0.49 | −172,365 |
| 2023 | 873 | 32.9% | 0.50 | −449,598 |
| 2024 | 1464 | 41.3% | 1.06 | +67,281 |
| 2025 | 1443 | 36.7% | 0.91 | −159,726 |
| 2026 | 474 | 43.5% | 1.45 | +154,852 |

**The honest limitation: the combined filter does NOT save the bad years.** 2021 (−$568k), 2022 (−$172k) and 2023 (−$450k) stay deeply negative *with both filters on*. The daily SPY 10/20MA cross is too slow and too index-centric to dodge a momentum-stock bear when the headline index holds up — 2021 is the textbook case (S&P fine, small-cap momentum collapsed). So the regime filter trims the drawdown years modestly but does not flip them; the system remains regime-dependent even after filtering. The melt-up years (2009, 2012, 2013, 2020) still carry the record (+$0.5M–$2.2M each).

### A slower cross (25/50) is *not* the fix

The 10/20 (Qullamaggie's rule) whips around; a natural thought is that a slower **25/50** cross would avoid being chopped in and out. It does not help — and on the metric that matters here it is slightly **worse**. Combined (tight <0.40 AND bull) totals, head to head:

| rule | trades | net P&L | PF |
| ---- | -----: | -------: | ---: |
| tight only (no regime) | 30,051 | +5,008,319 | 1.22 |
| tight + 10/20 | 21,924 | +4,322,987 | **1.26** |
| tight + 25/50 | 23,888 | +4,384,044 | 1.24 |

And the give-back years got **worse**, not better: 2021 −$614k (vs −$568k at 10/20), 2025 −$313k (vs −$160k), 2018 −$146k (vs −$79k). The reason is mechanical: **a slower MA stays "bull" longer into a topping process**, so it keeps you long *deeper* into the decline before flipping. The whip-reduction cuts both ways — fewer false bear flips, but later exits from real tops — and it admits ~2k more trades that are below average (net barely rises, PF falls).

**The deeper lesson: no SPY index MA cross can fix this, because the index was not the problem.** In 2021 the cap-weighted S&P kept making new highs while small-cap/momentum names (what this system actually trades) collapsed — the ARKK-style unwind. Any filter watching SPY stays bullish straight through that carnage; a slower one stays bullish even longer. To filter 2021–2023 we need a signal that senses **momentum-stock health specifically**, not the headline index — pursued next.

## Self-referential regime: gate on the strategy's OWN recent health

Since no *index* MA fixes 2021–2023 (the index held up while momentum names broke), the natural alternative is to gate on the **strategy's own recent realized P&L** — a signal that, by construction, senses momentum-stock regime directly. Rule: only take a new entry when the system's **trailing 3-month (63 trading-day) realized P&L is positive**.

**No-lookahead construction:** realized P&L is summed by each trip's *exit* date (a trip is only "known" once closed), rolled over a 63-day window that **ends on the prior trading day** (`ROWS BETWEEN 63 PRECEDING AND 1 PRECEDING`), so an entry on day T sees only P&L realized strictly before T. Open trips (not yet realized) never contribute. This is a post-hoc tag on the existing `trips_adv100k.csv`.

### By health regime (whole system)

| regime | trades | win% | PF | net P&L | avg/trip |
| ------ | -----: | ---: | ---: | -------: | -------: |
| **healthy (3mo realized > 0)** | 19272 | 41.1% | **1.17** | +2,507,873 | +$130 |
| unhealthy | 18037 | 38.8% | 1.05 | +933,825 | +$52 |

Per-trip, this discriminates *better* than the SPY cross ($130 vs $52, a 2.5× ratio) — but it splits the book roughly in half, and "unhealthy" is still mildly positive.

### Combined (tight <0.40 AND healthy) by year — the real test

| year | trades | win% | PF | net P&L |
| ---- | -----: | ---: | ---: | -------: |
| 2005 | 468 | 39.5% | 1.11 | +34,536 |
| 2006 | 1229 | 41.4% | 0.97 | −20,641 |
| 2007 | 1051 | 40.0% | 1.04 | +21,772 |
| 2008 | 144 | 38.2% | 0.57 | −47,193 |
| 2009 | 42 | 35.7% | 0.70 | −11,397 |
| 2010 | 517 | 50.1% | 2.04 | +299,463 |
| 2011 | 996 | 33.4% | 0.80 | −128,769 |
| 2012 | 649 | 41.4% | 1.52 | +192,528 |
| 2013 | 1894 | 53.1% | 2.66 | +1,362,917 |
| 2014 | 984 | 42.5% | 1.16 | +90,588 |
| 2015 | 826 | 40.9% | 0.77 | −109,701 |
| 2016 | 426 | 56.6% | 2.45 | +273,948 |
| 2017 | 1491 | 46.4% | 1.66 | +542,384 |
| 2018 | 1156 | 40.2% | 0.96 | −33,906 |
| 2019 | 10 | 50.0% | 1.07 | +318 |
| 2020 | 714 | 51.3% | 3.63 | +1,399,518 |
| 2021 | 2387 | 33.0% | 0.76 | −650,354 |
| 2022 | 86 | 37.2% | 0.56 | −29,631 |
| 2023 | 0 | — | — | $0 (no trades) |
| 2024 | 55 | 23.6% | 0.39 | −37,608 |
| 2025 | 254 | 31.9% | 0.54 | −136,502 |
| 2026 | 326 | 48.8% | 1.39 | +84,079 |

**Combined totals: 15,705 trips, PF 1.29, +$3,096,347, +$197/trip — the highest PF of any rule tried** (but the fewest trades; it sits out a lot). The give-back years, head to head with the SPY rule:

| year | tight only | tight + SPY 10/20 | tight + healthy(3mo) |
| ---- | ---------: | ----------------: | -------------------: |
| 2021 | −652,352 | −568,043 | **−650,354** |
| 2022 | −352,266 | −172,365 | **−29,631** |
| 2023 | −494,503 | −449,598 | **$0 (no trades)** |
| 2025 | −329,723 | −159,726 | −136,502 |

**It is the best tool yet for *grinding* bears, and useless against a *violent top.*** Two distinct failure modes, and this filter only catches one:

- **Grinding declines (2022, 2023, 2015) — caught cleanly.** After a sustained losing stretch the trailing health goes negative and stays there, so the system simply stops trading. **2023 is eliminated entirely** (zero entries passed both filters); **2022 shrinks from −$352k to −$30k**; 2008 to −$47k; 2009 admits only 42 trades (it waited until the strategy was working again before re-engaging — exactly the desired behaviour).
- **A violent top right after a melt-up (2021) — missed.** 2021's collapse came *immediately* after the explosive 2020 (+$1.4M) and the Jan-2021 blow-off, so the 3-month trailing health was still strongly positive going into the February top. A backward-looking health metric is structurally **late** to a sharp reversal that follows a strong run — the same lag that hurt the slow MA, now self-referential. 2021 stays −$650k, essentially unfiltered.

So the dream of "make $2M in 2020 and not give it back in 2021" is **not** solved by trailing realized P&L either: the give-back happens too fast, before the health signal can turn. What this filter *does* buy is avoidance of the long post-2021 / 2022–2023 grind — which the SPY cross could not do. The two filters are complementary in *which* bad years they help, and neither catches 2021.

Both regime signals are weak-to-moderate levers next to the volatility-contraction filter, which remains the single dominant edge.

## Volatility-expansion exit (the same VCP metric, used to exit)

The regime filters trimmed the bad years but could not fix the 2021 give-back, because the problem is not *which* trades you take — it is *how long you hold the winners*. The fix is to use the **same volatility metric for exits that we use for entries**: a stock that breaks out of a tight base and then goes **parabolic** sees its tightness `range/(14×ATR)` *expand*. So: hold a position only while it stays reasonably tight, and **exit when the held-day rolling tightness rises above a threshold** (volatility expansion = the move is becoming overextended). Both exits run together — the 15-day-low stop AND the expansion exit — and whichever fires first wins (exit next open, no lookahead). Implemented in [StopWalk.fs](../TradingEdge.MomentumBacktest/StopWalk.fs); CLI flag `--expansion-exit <thr>`.

### Threshold sweep (whole book, $100k floor)

| exit rule | trades | net P&L | PF | avg/trip |
| --------- | -----: | -------: | ---: | -------: |
| stop only (baseline) | 37,309 | +3,441,699 | 1.11 | +$92 |
| stop + expansion 0.55 | 37,309 | +4,713,135 | 1.18 | +$126 |
| **stop + expansion 0.70** | 37,309 | **+5,165,701** | 1.17 | **+$138** |
| stop + expansion 0.85 | 37,309 | +3,906,851 | 1.12 | +$105 |

Trade count is unchanged (every entry still opens exactly one trip — the exit only changes *where* it closes). **0.70 is the sweet spot: +50% net P&L and +50% avg/trip over the stop-only baseline.** 0.55 exits too eagerly (bails on names still running); 0.85 only catches extreme blow-offs and lets too much round-trip back to the stop first.

### Why it works: the expansion exit captures the parabola

Splitting the 0.70 book by *which* exit closed each trip is decisive:

| exit reason | trades | win% | PF | net P&L |
| ----------- | -----: | ---: | ---: | -------: |
| **expansion** | 2079 (5.6%) | 54.1% | **4.62** | **+4,697,747** |
| mtm (still open) | 665 | 58.9% | 5.12 | +975,632 |
| stop (15-day-low) | 34565 (92.6%) | 39.0% | 0.98 | −507,678 |

**Only 5.6% of trips exit on expansion — and that sleeve carries essentially the entire book** (PF 4.62, +$4.7M), while the 92.6% that exit on the trailing stop net *negative*. The expansion exit is selling the parabola near its high — locking in the fat-tailed winner before it gives the move back. The stop is left cleaning up the names that simply faded. (Tighter thresholds concentrate this further: expansion-sleeve PF is 3.54 at 0.55, **4.62 at 0.70, 8.47 at 0.85** — the rarer the trigger, the more extreme the blow-off it catches.)

### The 2021 give-back is (mostly) solved

This is the result the regime filters could not produce. Give-back years, **tight-entry universe (<0.40)**, baseline vs expansion-0.70:

| year | stop only | + expansion 0.70 |
| ---- | --------: | ---------------: |
| **2021** | **−652,352** | **−65,405** (PF 0.98) |
| 2022 | −352,266 | −302,383 |
| 2023 | −494,503 | −441,408 |
| 2025 | −329,723 | −272,217 |

**2021 collapses from −$652k to −$65k — a 90% reduction, essentially breakeven.** This is exactly the "made $2M in 2020, gave it back in 2021" problem. The mechanism is precise: the 2021 momentum top was a *parabolic blow-off* — names went vertical and their volatility expanded, so the expansion exit sold them near the highs **before** they round-tripped to the 15-day-low stop. No index regime could see this (the S&P held up); each stock's own volatility structure could. The grinding bears (2022, 2023, 2025) improve only modestly — those names fade *without* a clean parabolic expansion, so the stop still does the (losing) work. The expansion exit specifically rescues the **post-melt-up blow-off**, which is 2021's signature.

### Full by-year with expansion 0.70 (whole book)

| year | trades | win% | PF | net P&L |
| ---- | -----: | ---: | ---: | -------: |
| 2005 | 1520 | 42.2% | 1.25 | +246,789 |
| 2006 | 1700 | 41.8% | 1.08 | +80,079 |
| 2007 | 1698 | 37.6% | 0.92 | −84,986 |
| 2008 | 541 | 29.9% | 0.74 | −135,092 |
| 2009 | 1055 | 39.7% | 1.42 | +368,782 |
| 2010 | 1743 | 44.1% | 1.53 | +565,978 |
| 2011 | 1333 | 34.0% | 0.85 | −137,552 |
| 2012 | 1087 | 44.1% | 1.90 | +519,395 |
| 2013 | 2353 | 51.4% | 2.25 | +1,432,512 |
| 2014 | 1739 | 39.2% | 0.94 | −74,511 |
| 2015 | 1319 | 40.3% | 0.84 | −134,516 |
| 2016 | 1411 | 47.5% | 1.50 | +445,752 |
| 2017 | 2302 | 44.8% | 1.46 | +636,215 |
| 2018 | 1809 | 40.7% | 1.06 | +76,641 |
| 2019 | 1405 | 44.3% | 1.10 | +102,268 |
| 2020 | 2679 | 42.0% | 2.10 | +3,092,092 |
| 2021 | 3386 | 32.6% | 0.90 | −388,704 |
| 2022 | 817 | 30.4% | 0.45 | −553,377 |
| 2023 | 1617 | 33.6% | 0.61 | −719,728 |
| 2024 | 2661 | 39.0% | 0.96 | −91,242 |
| 2025 | 2226 | 36.0% | 0.85 | −412,584 |
| 2026 | 908 | 43.5% | 1.49 | +331,490 |

Compared to the stop-only headline table, every melt-up year holds (2013 +$1.43M, 2020 +$3.09M, 2026 +$331k) while 2021 at the whole-book level improves from −$1.14M to −$389k. The exit is now carried on every trip as `exit_reason` (`stop` / `expansion` / `mtm`).

## Stacking the best levers: expansion exit + self-referential health

Combining the volatility-expansion exit (0.70) with the self-referential health *entry* filter (3-month trailing realized P&L > 0). Health is recomputed from **this run's** realized P&L (the expansion exit changes exit dates, so the realized series differs from the stop-only run). Whole-book overall:

| config | trades | net P&L | PF | avg/trip |
| ------ | -----: | -------: | ---: | -------: |
| exp 0.70 (all entries) | 37,309 | +5,165,701 | 1.17 | +$138 |
| exp 0.70 + healthy | 22,098 | +3,861,270 | 1.23 | +$175 |
| exp 0.70 + tight (<0.40) | 30,051 | +5,756,019 | 1.27 | +$192 |
| **exp 0.70 + tight + healthy** | 17,942 | +3,849,659 | **1.31** | **+$215** |

**The full stack reaches PF 1.31 / +$215 per trip — the best quality of any configuration**, ~2.3× the raw stop-only baseline's $92/trip. But it is a **quality-vs-volume trade**: health cuts the book to 17,942 trips (under half) and *lowers* net P&L vs exp+tight-only ($3.85M vs $5.76M), because it sits out a lot — including good trades.

### The two filters are complementary — but health is a blunt instrument

Give-back years, exp+tight vs the full stack (tight-entry universe):

| year | exp 0.70 + tight | + healthy |
| ---- | ---------------: | --------: |
| 2021 | −65,405 | −65,405 |
| 2022 | −302,383 | −11,971 |
| 2023 | −441,408 | — |
| 2024 | +193,551 | −149,796 |
| 2025 | −272,217 | −468,536 |

The honest read:

- **They own *different* bad years.** The expansion exit already fixed **2021** (−$65k; unchanged by health — those entries were all made while trailing health was still green, so all pass). Health then fixes the **grinding bears the exit could not**: **2022 −$302k → −$12k, 2023 −$441k → eliminated** (zero entries passed). Blow-off protection (exit) + grind protection (health) cover different failure modes.
- **But health actively *hurts* the whippy years.** **2024 flips +$194k → −$150k and 2025 −$272k → −$469k.** This is the lag: after a drawdown health turns red and you sit out the *recovery*, then turns green just in time to catch the next dip — in chop (2024/2025) that is worse than staying in. Health also discards a lot of good recovery P&L (it sits out most of 2009 and much of 2016/2020).

**Verdict: the expansion exit is the unambiguous win; the health filter is a tradeoff.** It raises headline PF and cleanly removes 2022/2023, but at the cost of new damage in choppy years and a much smaller book. Whether to run it depends on objective — max PF / drawdown-aversion (use it) vs max net P&L / capacity (skip it, run exp+tight). The lag in the trailing-realized health signal is the weakness; a **breadth-based regime signal** (how many stocks are above their 20/50/100-day MAs) may give a less laggy read — investigated next.

## Market-breadth regime filter (% of stocks above their MA)

The SPY MA cross and the self-referential health filter share a weakness: SPY ignores the small-caps we trade, and health lags (it sat out the 2024/2025 recoveries). A **market-internal breadth** signal addresses the first directly — the *fraction of liquid CS/ADRC stocks above their own 20/50/100-day MA*, computed across the whole ~3,000-name universe per day (`breadth.parquet`). It reads the actual health of the tradable universe, not a cap-weighted index. Entry on day T uses breadth **as of T−1's close** (lagged one day) so there is no lookahead. Tested on top of the best long config (expansion-0.70 exit + tight-<0.40 entry).

### Threshold sweep (exp 0.70 + tight entry)

| breadth filter | trades | net P&L | PF |
| -------------- | -----: | -------: | ---: |
| none (exp + tight) | 30,051 | +5,756,019 | 1.27 |
| **% > 50-day MA > 0.50** | 22,886 | +5,729,622 | **1.36** |
| % > 50-day MA > 0.60 | 16,081 | +5,222,079 | 1.45 |
| % > 100-day MA > 0.50 | 24,086 | +5,508,154 | 1.32 |

**This is the best regime filter found** — and unlike the health filter, it raises PF *without sacrificing net P&L*. `breadth50 > 0.50` lifts PF **1.27 → 1.36 while net P&L barely moves** ($5.76M → $5.73M): it removes losing trades surgically rather than sitting out good ones. Tightening to 0.60 pushes PF to 1.45 but starts costing real P&L (it sits out ~half the book).

### By year (exp+tight vs +breadth50 > 0.50)

| year | exp + tight | + breadth50 > 0.50 |
| ---- | ----------: | -----------------: |
| 2005 | +272,988 | +156,804 |
| 2006 | +100,603 | +114,230 |
| 2007 | −54,647 | −23,658 |
| 2008 | −158,813 | +10,775 |
| 2009 | +472,316 | +467,089 |
| 2010 | +440,576 | +337,727 |
| 2011 | −88,029 | −24,963 |
| 2012 | +371,626 | +355,939 |
| 2013 | +1,305,227 | +1,164,658 |
| 2014 | +153,018 | +56,799 |
| 2015 | −58,479 | +78,618 |
| 2016 | +373,556 | +279,834 |
| 2017 | +587,330 | +412,445 |
| 2018 | −4,970 | +57,546 |
| 2019 | +175,919 | +95,407 |
| 2020 | +2,493,687 | +2,372,951 |
| 2021 | −65,405 | −45,450 |
| 2022 | −302,383 | −113,620 |
| 2023 | −441,408 | −457,653 |
| 2024 | +193,551 | +193,666 |
| 2025 | −272,217 | +18,983 |
| 2026 | +261,976 | +221,498 |

**It improves nearly every losing year and flips four of them positive** — 2008 (−$159k → +$11k), 2015 (−$58k → +$79k), 2018 (−$5k → +$58k), and the choppy **2025 (−$272k → +$19k)** — while the melt-up years give up almost nothing (2020 $2.49M → $2.37M, 2013 $1.31M → $1.16M). This is exactly the surgical filtering the trailing-realized **health** filter could *not* do: breadth fixed 2025 instead of making it worse (health flipped 2025 to −$469k via its recovery-lag). Breadth is coincident, not lagging like realized P&L, so it doesn't get trapped sitting out recoveries.

**The two stubborn years remain 2021 and 2023.** 2021 (−$45k) was already mostly solved by the expansion exit; breadth shaves a little more. **2023 (−$458k) is essentially unchanged** — a slow grind where breadth oscillated around 50%, so the filter let trades through. And the honest structural limit persists: **breadth, like every price-based regime signal, *peaks at the top*** — on 2021-02-16 (the exact momentum high) 83% of stocks were above their 50-day MA and 90% above their 100-day. A breadth-high filter is maximally long right into the blow-off. Only the expansion *exit* — which reads each stock's own parabola — caught 2021; breadth (and all aggregate-state filters) cannot.

### Where this leaves the long system

Best configurations to date, whole-book ($100k floor) unless noted:

| config | trades | net P&L | PF | avg/trip |
| ------ | -----: | -------: | ---: | -------: |
| stop only (baseline) | 37,309 | +3,441,699 | 1.11 | +$92 |
| + expansion-0.70 exit | 37,309 | +5,165,701 | 1.17 | +$138 |
| + tight (<0.40) entry | 30,051 | +5,756,019 | 1.27 | +$192 |
| **+ breadth50 > 0.50 entry** | 22,886 | +5,729,622 | **1.36** | **+$250** |
| (alt) + self-ref health entry | 17,942 | +3,849,659 | 1.31 | +$215 |

The compounding picture: **volatility contraction (entry) → expansion (exit) → breadth (regime)** stack to PF 1.36 at +$250/trip — nearly **3× the raw baseline's $92/trip** — while keeping 23k trades and almost all the net P&L. The expansion exit is the single biggest lever (it owns the 2021 fix); breadth is the best *entry* regime filter; the tightness entry filter is the foundation. 2023 (slow grind, no parabolic blow-off, breadth near 50%) is the one regime none of these levers cleanly solves.

## Short-term exits: harvest the drift instead of holding for the runner

Everything above optimizes for **big wins** — hold the breakout via a trailing stop, let the parabola run, exit on volatility expansion. The opposite question: what if we just **hold a fixed 1–3 days and take the immediate post-breakout drift**, never riding for the big move? Tested post-hoc on the same tight-entry universe (`tight<0.40`, $100k floor), no stop and no expansion exit — enter at day-T close, exit at the close N trading days later.

### Fixed-N-day holds (tight entries)

| exit | trades | win% | PF | net P&L | avg/trip |
| ---- | -----: | ---: | ---: | -------: | -------: |
| hold 1d | 30,009 | 48.3% | 1.11 | +714,652 | +$24 |
| hold 2d | 29,981 | 48.3% | 1.12 | +1,034,196 | +$35 |
| hold 3d | 29,954 | 49.2% | 1.16 | +1,537,823 | +$51 |
| hold 5d | 29,910 | 49.4% | 1.21 | +2,433,256 | +$81 |
| hold 10d | 29,793 | 49.6% | 1.19 | +2,897,315 | +$97 |

**The post-breakout drift is real and positive from day 1.** Every short hold profits (PF ≥ 1.11), the edge builds monotonically through ~5–10 days, and there is no "give-back" inside the first 10 days — these breakouts keep drifting up. Win rate is ~48–50% (vs ~40% for the big-win system — short holds win more often but each win is small).

The same holds with the **refined entry filter (tight <0.40 AND ATR% < 8%)** — adding the ATR cut nudges quality up across the board (PF +0.02–0.05 at the longer holds, win rate above 50% at 5–10d) for ~1,750 fewer trips and roughly unchanged net P&L:

| exit | trades | win% | PF | net P&L | avg/trip |
| ---- | -----: | ---: | ---: | -------: | -------: |
| hold 1d | 28,253 | 48.7% | 1.11 | +654,516 | +$23 |
| hold 2d | 28,228 | 48.8% | 1.12 | +899,512 | +$32 |
| hold 3d | 28,204 | 49.7% | 1.18 | +1,497,514 | +$53 |
| hold 5d | 28,163 | 49.9% | 1.24 | +2,355,446 | +$84 |
| hold 10d | 28,051 | 50.3% | 1.23 | +2,953,744 | +$105 |

Same shape, slightly cleaner — consistent with the ATR<8% cut being a pure quality bump at every horizon (it removes the high-daily-ATR names that drift worst). The by-year robustness analysis below uses the tight-only hold for continuity with the earlier comparison; the ATR refinement would shift each year marginally in the same direction.

**On raw P&L the short hold is worse than the big-win system** ($51/trip at 3d vs **$192/trip** for expansion-exit+tight): the parabolic runners captured by the expansion exit are ~4× the per-trade edge of the drift. If the objective is total dollars, hold for the runner.

### But the short hold is far more *robust* — it sidesteps the regime problem entirely

Because you are out in 1–3 days, you cannot round-trip a multi-month decline. By year (entry date):

| year | hold 3d | hold 5d | exp+tight (big-win) |
| ---- | ------: | ------: | ------------------: |
| 2005 | +17,186 | +32,530 | +272,988 |
| 2006 | +74,944 | +92,413 | +100,603 |
| 2007 | +69,470 | +46,745 | −54,647 |
| 2008 | −79,830 | −64,291 | −158,813 |
| 2009 | +200,262 | +467,103 | +472,316 |
| 2010 | +79,425 | +120,737 | +440,576 |
| 2011 | −16,697 | −12,135 | −88,029 |
| 2012 | +23,124 | +46,847 | +371,626 |
| 2013 | +199,848 | +320,834 | +1,305,227 |
| 2014 | +187,341 | +238,542 | +153,018 |
| 2015 | +74,645 | +57,876 | −58,479 |
| 2016 | +107,515 | +124,361 | +373,556 |
| 2017 | +94,676 | +132,296 | +587,330 |
| 2018 | +103,528 | +125,387 | −4,970 |
| 2019 | +60,513 | +221,244 | +175,919 |
| 2020 | +140,386 | +163,120 | +2,493,687 |
| 2021 | +96,294 | +86,183 | −65,405 |
| 2022 | −73,010 | −97,743 | −302,383 |
| 2023 | −35,384 | −33,454 | −441,408 |
| 2024 | +157,731 | +269,571 | +193,551 |
| 2025 | +2,197 | +56,054 | −272,217 |
| 2026 | +53,660 | +39,038 | +261,976 |

This is the most striking comparison in the study:

- **2021 is *positive* (+$96k)** — vs −$65k for the (already-expansion-fixed) big-win system. You take the breakout drift and you are gone before the round-trip.
- **2023 — the one year NO other lever could fix — is nearly flat (−$35k)** vs **−$441k** for the big-win system. The grinding bear that defeated breadth, health, and the expansion exit is almost neutralized just by not holding.
- **2025 is roughly flat-to-positive** (+$2k / +$56k) vs −$272k.
- Only **2008 and 2022** (sharp, fast crashes that gap *through* a 3-day hold) stay meaningfully negative — and even those are a fraction of the big-win losses.

**Positivity / drawdown, head to head:**

| metric | hold 3d | exp+tight (big-win) |
| ------ | ------: | ------------------: |
| years positive | **18 / 22** | 13 / 22 |
| worst year | **−$79,830** | −$441,408 |
| total net P&L | +$1,537,824 | +$5,756,019 |

**The fork is clear: the same signal supports two different systems.** Swing for the fences (hold the runner via the expansion exit) → ~4× the dollars but lumpy, regime-dependent, and 2023 stays a −$441k hole. Harvest the drift (3–5 day hold) → a quarter of the dollars per trade but **18/22 green years, a worst year 5.5× smaller, and the regime dependence largely gone** — a much higher-Sharpe, lower-magnitude book. Which to run is an objective choice (capacity/absolute return vs consistency/drawdown), and they could even be combined (drift-harvest base + a runner sleeve). All post-hoc on the existing entries — no engine change.

### Drift by tightness tier — contraction matters even at 3 days

The fixed-N-day results above used the tight (<0.40) universe. Breaking the **3- and 5-day drift across all tightness tiers** shows the VCP edge is, if anything, *sharper* at the short horizon than for the big-win system:

| tightness | trades | win% (3d) | PF (3d) | net 3d | avg 3d | net 5d | avg 5d |
| --------- | -----: | --------: | ------: | -----: | -----: | -----: | -----: |
| **<0.40 tight** | 29,954 | 49.2% | **1.16** | **+1,537,823** | +$51 | +2,433,256 | +$81 |
| 0.40-0.55 | 5,632 | 42.7% | 0.84 | −502,577 | −$89 | −563,744 | −$100 |
| 0.55-0.70 | 1,226 | 38.7% | 0.73 | −315,943 | −$258 | −481,880 | −$395 |
| 0.70-0.85 | 282 | 22.7% | 0.29 | −315,061 | −$1,117 | −330,223 | −$1,184 |
| 0.85+ trend | 71 | 26.8% | 0.18 | −131,283 | −$1,849 | −150,776 | −$2,124 |

**A loose-base breakout doesn't even give you a 3-day drift** — the 0.85+ tier averages −$1,849/trip over just three days. So volatility contraction is necessary even for the immediate post-breakout pop, not only for the long hold. The punchline: across **all** entries (every tightness, 37,165 trades) the 3-day drift is a negligible **+$273k (~+$7/trip)** — the tight subset (+$51/trip) carries essentially all of it while the loose tiers bleed it back. Without the contraction filter there is barely a *short-horizon* drift edge at all. (This also reinforces the short thesis: the loose/expanded breakouts are bad longs at *every* horizon → good short candidates for the reversal book.)

### Breadth on the drift system — barely needed

Layering the breadth50 > 0.50 regime filter onto the tight-entry drift:

| config | trades | net P&L | PF | avg/trip |
| ------ | -----: | -------: | ---: | -------: |
| hold 3d (tight) | 29,954 | +1,537,823 | 1.16 | +$51 |
| hold 3d + breadth50 > 0.50 | 22,808 | +1,335,638 | 1.18 | +$59 |
| hold 5d (tight) | 29,910 | +2,433,256 | 1.21 | +$81 |
| hold 5d + breadth50 > 0.50 | 22,769 | +2,231,434 | 1.26 | +$98 |

**Breadth helps the drift far less than it helped the big-win system** (5d: 1.21 → 1.26, vs the runner's 1.27 → 1.36). That is expected: the short hold is *already* regime-robust because it never holds through a decline, so a regime filter has little left to fix. By year it is a wash — it flips the two sharp-crash years (2008 −$80k → +$25k, 2022 −$73k → −$15k) but slightly *worsens* 2023 (−$35k → −$67k) and shaves the good years, netting ~−$200k overall for a small PF bump. **The drift system's decisive filter is tightness, not regime** — the opposite weighting from the big-win system, where the regime/exit levers mattered most.

### Why the original system was profitable *before* the tightness filter

The near-zero 3-day drift on raw entries raises a fair question: how did the +$3.44M baseline make money at all? Two reasons, both about *horizon* and *the loose drag*:

| cohort (trailing-stop hold) | trades | avg bars held | avg/trip | net P&L | PF |
| --------------------------- | -----: | ------------: | -------: | ------: | ---: |
| all entries | 37,309 | 28.7 | +$92 | +3,441,699 | 1.11 |
| tight (<0.40) | 30,051 | 28.7 | +$167 | +5,008,319 | 1.22 |
| loose (≥0.40) | 7,258 | 28.5 | −$216 | −1,566,621 | 0.84 |

- **The edge was always in the long hold, not the short drift.** The baseline used the 15-day-low trailing stop, which holds ~**29 bars (weeks)** on average — not 3 days. The money is the handful of breakouts that keep running for weeks (classic fat tail), worth ~$92/trip across all entries vs only ~$7/trip at a 3-day horizon. The first three days are nearly flat; the *runners* pay.
- **The tightness filter removed a drag, it didn't create the edge.** The baseline +$3.44M was already the tight cohort (+$5.0M) *minus* the loose-base cohort (−$1.57M, PF 0.84). The system was net-positive only because the tight winners outweighed the loose losers; filtering to tight bases just deletes the −$1.57M drag. So "the raw signal has no edge" is specifically a statement about the **3-day** horizon — at the hold-for-the-runner horizon the raw signal does earn, but entirely via the tight subset.

## Time-stop and stall exits (the hybrid) — drawdown relief, not more profit

Two ways to add a *third* exit on top of the stop + expansion-0.70 exit, both meant to recycle "dead money" — trades that neither run (no expansion) nor fail (no stop) but chop sideways for weeks:

- **Time stop:** if no other exit has fired within N held bars, exit at bar T+N (next open).
- **Stall exit:** exit if K consecutive held bars pass with no new *since-entry-high* close (momentum persistence broke).

Both implemented in [StopWalk.fs](../TradingEdge.MomentumBacktest/StopWalk.fs) (flags `--time-stop N`, `--stall K`), swept on the refined entry (tight <0.40 AND ATR% < 8%) + expansion-0.70 exit. Baseline (no third exit): **28,288 trips, PF 1.336, +$6,184,044, +$219/trip.**

### Sweep (whole book)

| third exit | net P&L | PF | avg/trip |
| ---------- | -------: | ---: | -------: |
| **none (baseline)** | **+6,184,044** | 1.336 | +$219 |
| time-stop 5d | +2,925,882 | 1.299 | +$103 |
| time-stop 10d | +3,680,598 | 1.294 | +$130 |
| time-stop 15d | +5,051,582 | 1.350 | +$179 |
| time-stop 20d | +5,728,469 | **1.359** | +$203 |
| time-stop 30d | +6,115,449 | 1.350 | +$216 |
| stall 2d | +1,941,639 | 1.228 | +$69 |
| stall 3d | +2,409,888 | 1.249 | +$85 |
| stall 5d | +3,019,595 | 1.259 | +$107 |
| stall 7d | +3,850,830 | 1.295 | +$136 |
| stall 10d | +4,716,787 | 1.317 | +$167 |

**On raw P&L, neither beats just holding for the runner.** Every config nets *less* than baseline; only **time-stop 20d edges PF up** (1.359 vs 1.336) at a −$450k P&L cost. The deeper reason: the expansion exit already does the smart cutting — it sells runners at the parabola and the stop catches failures, so there is little dead money left to recover, and what these exits cut is disproportionately *future winners* (a trade that pauses for a few days mid-run is exactly what the stall exit boots right before it continues). The stall exit is the worse of the two — it never beats baseline on either axis.

### But the time-stop materially cuts the drawdown years

The total hides the real benefit. By year, the remaining bad years (after the ATR filter + expansion exit already near-fixed 2021):

| year | baseline | time-stop 20d | stall 10d |
| ---- | -------: | ------------: | --------: |
| 2021 | −951 | **+230,700** | +152,052 |
| 2022 | −220,507 | **−123,970** | −150,619 |
| 2023 | −280,909 | **−108,848** | −202,713 |
| 2025 | −141,315 | **−71,766** | −90,658 |

**Time-stop 20d roughly halves every remaining drawdown year** — 2022 −44%, **2023 −61%** (the year no other lever could fix), 2025 −49%, and flips 2021 to +$231k. Capping the hold at 20 days stops trades from grinding all the way down to the 15-day-low stop over many weeks in a bad regime. The exit-reason mix confirms the mechanism: in the baseline the *stop* sleeve is the weak link (26,659 trips, PF 1.13); time-stop 20d intercepts those trades at day 20 — the winners exit via `time` (PF 8.0) while only genuine failures fall through to `stop`.

**So the time stop is the same consistency-vs-magnitude fork as the short hold:** baseline maximizes dollars (+$6.18M) but leaves −$200-280k drawdown years; time-stop 20d gives up ~$450k of total for **~halved drawdowns and a positive 2023-adjacent profile**. Which to run is a risk-appetite choice — and a time stop *plus* the expansion exit is a coherent "let winners run, but don't let a dead trade bleed for two months" rule. The stall exit is dominated by the time stop and not worth running.

## Caveats & known limitations

- **Same-day-close entry is mildly optimistic (by design).** The signal is defined by day T's close and we fill at that same close — i.e. we assume we could act on the print that defines the signal. This was the user's explicit v0 choice to maximize captured move; the **exit is kept strictly no-lookahead** (next-day open) so the optimism doesn't compound. A next-day-open *entry* variant is the obvious robustness check.
- **No regime filter.** This is the dominant unrealized improvement — the by-month evidence (firing hardest into reversals) says a SPY 10/20-day MA gate should remove a large share of the 2008/2021/2022/2023 bleed. Deferred to v1.
- **"Mid cap" is approximated by a dollar-volume floor**, not real market cap (no shares-outstanding data yet). A true market-cap breakdown is deferred pending a Polygon shares-outstanding feed wired into `TradingEdge.Massive`.
- **Survivorship**: the price universe is delisting-inclusive (good — no survivorship bias from prices), but `ticker_reference.type` is a *current* snapshot, so the CS/ADRC filter is approximate for delisted names.
- **No costs modeled.** No commissions, no slippage, no borrow. At fixed $10k notional these are second-order vs the regime effect, but a realistic-execution pass should add them.
- **Uncapped, non-compounding sizing** means net P&L is a raw breadth-and-edge measure, not an achievable equity curve — many concurrent positions can be open at once. A capped-book / fractional-equity variant is needed before reading these as portfolio returns.

## Next steps (in order)

1. **SPY 10/20-day MA regime filter** — gate entries on SPY being in an uptrend (10-day MA > 20-day MA, or price > both). The data/columns are structured so this slots in as one additive SQL column. Re-run and compare the bear-year bleed (2008/2021/2022/2023) head-to-head. *This is the immediate next task.*
2. **Top-trip / concentration check** per big month, to see how much of the melt-up P&L is a few names.
3. **Next-day-open entry variant** as a robustness check on the same-day-close optimism.
4. **Real market-cap bucketing** once shares-outstanding is available — the trips CSV already carries `avg_dollar_volume_4w_at_entry` as the interim size proxy.

---

*Generated 2026-06-15. Engine: [TradingEdge.MomentumBacktest](../TradingEdge.MomentumBacktest). Data: `data/trading.db` (split-adjusted daily, 2003-09→2026-05). Trips/log: `data/equity/momentum_v0/`.*
