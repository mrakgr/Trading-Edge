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

### Stop-tightening to breakeven after N days (gentler, but dominated)

The hard time stop force-sells winners at day N. A gentler variant: at bar T+N, **if the trade is in profit, raise the stop floor to the entry price** (stop = max(15-day-low, entry) thereafter) so it can never give back to a loss but a winner keeps running; **if it is NOT in profit at T+N, exit** (laggard cut). Implemented as `--breakeven-after N` in [StopWalk.fs](../TradingEdge.MomentumBacktest/StopWalk.fs) (exit reasons `breakeven` / `time_be`). Swept on the same refined entry + expansion-0.70 baseline (28,288 trips, +$6.18M, PF 1.336).

| third exit | net P&L | PF | avg/trip |
| ---------- | -------: | ---: | -------: |
| none (baseline) | +6,184,044 | 1.336 | +$219 |
| breakeven-after 5d | +3,386,805 | 1.315 | +$120 |
| breakeven-after 10d | +4,128,264 | 1.308 | +$146 |
| breakeven-after 15d | +5,146,135 | 1.337 | +$182 |
| breakeven-after 20d | +5,752,506 | 1.345 | +$203 |
| breakeven-after 30d | +6,018,553 | 1.337 | +$213 |

On total P&L it is **slightly gentler than the hard time stop** at the same N (be-20 +$5.75M vs hard-time-20 +$5.73M; be-15 +$5.15M vs +$5.05M) — confirming that *not* force-selling the winners, only flooring them, keeps a touch more upside. But it still does not beat the hold-the-runner baseline, and on the metric that motivated a time-based exit — **drawdown-year relief — it is clearly dominated by the hard time stop:**

| year | baseline | breakeven-20 | hard time-20 |
| ---- | -------: | -----------: | -----------: |
| 2021 | −951 | +53,642 | **+230,700** |
| 2022 | −220,507 | −180,241 | **−123,970** |
| 2023 | −280,909 | −255,599 | **−108,848** |
| 2025 | −141,315 | **−211,558** | **−71,766** |

The hard time stop beats breakeven-after in **every** drawdown year, and breakeven-20 even makes **2025 worse** (−$141k → −$212k). The exit-reason mix shows why: the `breakeven` sleeve nets only +$339k (PF 2.15) on 3,241 trips — flooring a mediocre winner at entry recovers little, because by the time it sags back to breakeven the whole open profit is gone — and most trades never arm it (they hit the 15-day-low or expansion exit first, so the floor rarely binds). The deeper lesson: **"let the modest winners keep running" is exactly the wrong instinct in a bear.** A trade only mildly green at day 20 is, in a bad regime, more likely to roll over than continue — the hard time stop cuts those too; breakeven-after keeps them and watches them give it back.

**Verdict on the whole exit investigation:** for **max P&L**, hold the runner (baseline, +$6.18M); for **drawdown relief**, the **hard time stop at ~20 days** is the best tool (halves the bad years, beats every alternative on the drawdown years). Breakeven-after and the stall exit are both dominated — gentler on paper, worse where it counts. The expansion exit remains the single most valuable add (it owns the 2021 fix and harvests the parabolas); everything else is a consistency-vs-magnitude dial on top of it.

## Momentum structure: is the 52-week-high criterion actually the edge?

To test whether buying *strength* (new 52-week highs) beats buying breakouts *from weakness*, the engine now records 66 raw price-level columns per entry — 11 lookback periods (52w/26w/13w/8w/4w/2w and 5/4/3/2/1 day) × 6 levels each (trailing close, MA, high/low channel, high/low **close**-channel) — with distances derived post-hoc in SQL. Run with the **52w-high entry gate dropped** (`--no-52w-high`) on the refined set (tight<0.40 AND ATR<8%, expansion-0.70 exit): **106,522 trips** (vs 28,288 with the gate — so most up-5%/RVOL-3 tight breakouts are *not* at new highs).

### ⚠️ Methodology: the raw result is a low-price artifact

Bucketed naively, the distance-from-52w-high table looked spectacular for *weakness* — the "15-30% below" tier showed PF 3.49 / +$29.7M. **It is an artifact.** 84.6% of that tier's P&L was a *single trade* netting $25M on a $10k notional (a ~2,500× "return" — a split/adjustment glitch), and the deep-discount tiers are dominated by sub-$5 stocks (the >50%-below tier is 54% under $5, median price $4.22). At fixed $10k notional, a low-priced beaten-down name buys a huge share count, so one adjustment error or penny-stock pop produces enormous fake P&L. **Every P&L-weighted breakdown is exposed to this; the deep-discount/low-price cells are where it bites hardest.** The clean test applies an **entry price ≥ $5 floor** and **clips per-trip returns at +500%**.

> **Cross-section comparability caveat (why PF here ≠ PF in the exit tables).** The floor + clip make the structure tiers comparable *to each other*, but they also lower the PF of the legitimate tiers versus the un-clipped exit/time-stop sections. The same "at/above 52w-high" population is **PF 1.336** un-clipped/no-floor (the time-stop *baseline*), **1.229** with the $5 floor, and **1.174** after the +500% clip — the clip caps the real multi-bagger right-tail that higher-priced new-high names genuinely produce, and the floor drops some big sub-$5 winners. So the structure-section PFs (≈1.1-1.3) read lower than the exit-section PFs (≈1.34) **by construction, not because the edge is weaker** — they answer different questions: the exit tables estimate the *deployable book's* P&L (keep the real fat tails), the structure tables *rank tiers like-for-like* (cap tails for comparability). Compare PFs only within a section, never across.

### Cleaned result — proximity to the 52-week high IS the edge

| distance from 52w-high (close) | trades | win% | PF | net P&L | avg/trip |
| ------------------------------ | -----: | ---: | ---: | -------: | -------: |
| at/above (new high) | 24,521 | 42.0% | 1.17 | +2,608,417 | +$106 |
| **within 5%** | 6,770 | 42.9% | **1.36** | +1,237,343 | **+$183** |
| 5-15% below | 11,787 | 40.3% | 1.19 | +1,230,396 | +$104 |
| 15-30% below | 13,415 | 38.2% | 1.12 | +979,027 | +$73 |
| 30-50% below | 11,886 | 35.2% | **0.92** | −691,411 | −$58 |
| >50% below | 9,226 | 29.5% | **0.83** | −1,792,378 | −$194 |

Clean and monotonic: **the closer to the 52-week high, the better, turning net-negative beyond ~30% below.** Win rate falls monotonically with distance (43% → 30%). The nuance: the single best cell is **"within 5% of the high" (PF 1.36, $183/trip), slightly beating at/above (PF 1.17)** — buying *just into / approaching* a new high edges out buying the exact breakout, and anything within ~15% of the high is solidly positive. Breakouts from real weakness (>30% off the high) genuinely lose once the penny-stock lottery effect is stripped.

**Robustness — does the pattern survive removing the return clip (keeping only the $5 floor)?** Mostly yes: the monotonic "closer = better" shape and the within-5% sweet spot (PF 1.43 un-clipped) are unchanged for every tier **within ~30% of the high**, and `30-50% below` stays a reliable dead zone (PF 0.93 either way). The one tier that flips is **`>50% below`: PF 0.83 clipped → 1.21 un-clipped** — but that is *not* a recovered edge, it is a **fat-tail mirage on non-penny names** (median price $14). Its entire positive P&L is ~24 trips that returned >500% (+$5.2M from those alone, vs +$2.2M tier total — the other 9,200 trips net *negative*; trips >200% contribute +$6.3M). You cannot build a strategy on catching two-dozen specific 5-baggers out of 9,226 trades, so the clipped PF (0.83, a loser) is the honest characterization. Net: the clip doesn't change the conclusion — proximity to the high wins, deep-discount does not — it just stops a handful of unrepeatable multi-baggers from flattering the worst tier.

> **⚠️ Short-side warning (for the future reversal book).** The same fat tail that flatters the long deep-discount tier is a *lethal* hazard on the SHORT side. Shorting 52-week highs / extended names blindly means that occasionally you are short one of these 100×-type runners — and a single such position, held with a fixed or slow stop, can wipe out the cumulative profit of hundreds of good shorts (the right-tail is effectively unbounded on the upside, unlike the −100% floor on a long). The 24 trips returning >500% here, and the broader observation that momentum names can run multiples, mean a short-reversal system **must** carry hard, fast, non-negotiable stops and/or strict size caps; it cannot use the "remove stops" approach that helps the long/mean-reversion side. Do not short strength without that protection.

**Verdict: the 52-week-high criterion is validated** — momentum wants strength, near the highs (Minervini/Qullamaggie confirmed) — with the refinement that the productive zone is **within ~15% of the 52w high**, not strictly at it. The headline lesson is also methodological: **the raw, unfiltered P&L-weighting was the *opposite* (and wrong) conclusion** — a reminder to price-floor and outlier-clip every breakdown on this dataset before trusting a P&L-weighted cell. (Remaining structure breakdowns — trailing returns and MA/channel distances across all 11 horizons, testing momentum persistence and the short-term-dip question — are next, with the same price-floor + clip discipline.)

## Momentum structure: MA distance, trailing returns, and the pre-breakout days

With the **52w-high band** (within 15%) and **price floor** ($5) confirmed as the right filters, all three remaining structure breakdowns were run on that cleaned study set (gate-off `trips_structure.csv` filtered to `entry_price ≥ 5 AND entry ≥ 0.85 × hiclose_52w` → **43,081 trips, +$5.08M** clipped; per-trip returns clipped at +500% throughout). Distances derived in SQL from the raw level columns. **Note: the tightness (<0.40) and ATR (<8%) filters are already baked into this study set** (the CSV was generated with `--max-tightness 0.40 --max-atr-pct 0.08`), so every structure pattern below is *already conditioned on the contracted, low-ATR base* — these are not raw-universe results.

### Distance from the moving averages (extension)

Distance = `entry / ma_{period} − 1` (positive = above the MA). The long MAs (52w/26w/13w/8w) tell one clean story; representative cells:

| MA | below MA | 0-5% above | 5-15% above | 15-30% above | >30% above |
| -- | -------: | ---------: | ----------: | -----------: | ---------: |
| 52w | PF 0.85 | 0.99 | **1.28** | **1.30** | 1.17 |
| 26w | 0.96 | 1.11 | **1.27** | **1.26** | 1.16 |
| 13w | 0.93 | 1.21 | **1.27** | 1.23 | 1.15 |
| 8w | 1.02 | 1.14 | **1.26** | 1.24 | 1.14 |

**Below the MA is bad, moderately above (5-30%) is the sweet spot, extreme extension (>30%) fades.** The 52w MA is the cleanest monotonic-then-fade: 0.85 → 0.99 → **1.28 → 1.30** → 1.17. "Above the rising long MA but not over-extended" is the constructive zone — the same picture as the 52w-high finding from a different angle (proximity to highs ⇒ above the long MAs). The short MAs (2w/4w) are noisy and non-monotonic (a 1-day dip below the 2-week average at entry is fine) — too close to price to carry regime info.

### Trailing return (does past momentum persist?)

Trailing move = `entry / trail_{period} − 1`. Across the mid horizons (4w/8w/13w/26w):

| horizon | down >10% | down 0-10% | up 0-15% | up 15-40% | up 40-100% | up >100% |
| ------- | --------: | ---------: | -------: | --------: | ---------: | -------: |
| 13w | 1.07 | 1.18 | **1.28** | 1.25 | 1.26 | **0.93** |
| 8w | 1.11 | 1.10 | **1.26** | 1.23 | 1.25 | **0.96** |
| 4w | 0.96 | 1.10 | **1.23** | 1.24 | 1.21 | **0.81** |
| 26w | 1.36* | 1.07 | 1.21 | **1.27** | 1.26 | 1.12 |

**Moderate prior momentum is best; extreme recent gains exhaust.** The productive band is roughly "up 0-40%" (PF ~1.23-1.31); names already **up >100% over the last few weeks turn net-negative** (13w 0.93, 8w 0.96, 4w 0.81) — the breakout is late. So past momentum *helps* up to a point and *hurts* once the move is already huge — a hump, not a monotonic "more is better." (*26w "down >10%" is a thin 194-trip cell — not load-bearing.)

### The days right before the breakout — does a short-term dip help?

For each of the 4 days *before* the breakout day, that day's own % change, bucketed:

| prior day | down >3% | down 1-3% | flat ±1% | up 1-3% | up >3% |
| --------- | -------: | --------: | -------: | ------: | -----: |
| day −2 | 1.06 | 1.16 | **1.30** | 1.24 | 1.16 |
| day −3 | **0.99** | 1.14 | **1.27** | 1.28 | 1.19 |
| day −4 | 1.13 | 1.21 | **1.24** | 1.21 | 1.17 |
| day −5 | 1.08 | 1.25 | 1.24 | **1.28** | 1.06 |

**A short-term dip into the breakout does NOT help — the opposite.** The strongest bucket on every prior day is **"flat ±1%"** (PF 1.24-1.30): a *quiet* tape right up to ignition. A sharp drop the prior day is the weakest (day −3 "down >3%" PF 0.99), and a sharp *rise* the prior day also fades (the move shouldn't have started yet). This is fully consistent with the tightness/VCP picture: you want **stillness immediately before the explosive up-day** — not a pullback, not a head start. (Worth noting this is the opposite of the *intraday* short-term-reversal effect; at the daily-into-breakout scale, calm-then-ignite beats dip-then-ignite.)

### Synthesis

Every structure lens points to the same archetype, and it is textbook Minervini/VCP: **price near its 52-week high (within ~15%), moderately above its rising long MAs (5-30%, not over-extended), with healthy-but-not-parabolic prior momentum (up, but not already doubled), coiled into a quiet tight base (tightness <0.40, ATR <8%) that goes still in the final days before an explosive high-RVOL up-day.** The earlier "buy from weakness" mirage and these confirmations together are the strongest argument in the study for doing your own filtered research rather than trusting unconditioned single-factor tests.

## Stop variants: remove the price stop, or use Qullamaggie's day-low stop

Mean-reversion systems often improve when you *remove* stops (the trailing stop whipsaws you out of recoveries). Does that carry to momentum? Two variants tested against the baseline 15-day-low trailing stop, all on the refined entry (tight<0.40, ATR<8%, expansion-0.70 exit), price ≥ $5:

- **Baseline** — 15-day-low trailing stop + expansion exit.
- **Variant 1** — *no price stop at all*; exits are the **time stop (20d)** + expansion only.
- **Variant 2 (Qullamaggie)** — initial stop at the **entry-day low**, trailing up to the 15-day-low once that rises above it (tight initial risk, then normal trail) + expansion.

### Results (price ≥ $5; unclipped — the honest read for a stop comparison, since stops are a bet on tails)

| stop config | trades | net P&L | PF | win% | avg/trip | avg hold |
| ----------- | -----: | -------: | ---: | ---: | -------: | -------: |
| baseline (15-day-low) | 24,521 | **+3,447,458** | 1.229 | 42.0% | +$141 | 28d |
| **V1: no stop + time-20** | 24,521 | +3,303,594 | **1.247** | **51.3%** | +$135 | 20d |
| V2: Qullamaggie day-low | 24,521 | +2,873,787 | 1.245 | 35.7% | +$117 | 20d |

**Both variants raise PF (better risk-adjusted return) — the standard 15-day-low stop is not optimal — but neither raises gross P&L.** The two work in opposite ways:

- **V1 (no price stop) — your mean-reversion intuition carries over.** Removing the trailing stop lifts **PF 1.229 → 1.247 and win rate 42% → 51%**: you stop getting shaken out at the 15-day-low, so more trades survive to run (expansion) or get a clean 20-day look (the exit mix is ~all `time` + `expansion`). Cost: net P&L −$144k, because the stop, whipsaws aside, did cut some real losers faster than a 20-day wait. Net = **higher-PF, much-higher-win-rate, smoothest equity, marginally less gross**.
- **V2 (Qullamaggie day-low) — the opposite shape.** The tight entry-day-low stop is hit far more often (it sits much closer than the 15-day-low), so win rate *drops* to 35.7% and gross P&L falls most (−$574k) — but the survivors are high quality (expansion sleeve +$1.66M on 704 trips), so PF still rises to 1.245. Classic Qullamaggie: **tight initial risk, many small stop-outs, fewer-but-bigger winners** — minimizes per-trade loss at the cost of total.

### And — crucially — removing the stop does NOT blow up the bear years

The fear with "no stops" is uncapped bleed in a downtrend. The opposite happens here, because the **20-day time stop caps the hold** instead of letting trades grind to the 15-day-low over many weeks:

| year | baseline | V1 (no stop + time-20) | V2 (day-low) |
| ---- | -------: | ---------------------: | -----------: |
| 2021 | −244,553 | **+4,355** | −62,215 |
| 2022 | −225,378 | **−124,743** | −165,266 |
| 2023 | −287,283 | −214,408 | −208,913 |

**Both variants improve every drawdown year**, and the standard 15-day-low stop is actually the *worst* in bears (it gives back the most). V1 is the standout overall — best win rate, best PF, best bear-year behavior — for only ~$144k less gross than baseline.

**Verdict:** the 15-day-low trailing stop is not the right stop for this momentum system. **Replacing it with no-price-stop + a 20-day time exit (V1) is the best risk-adjusted choice** (higher PF, 51% win rate, smaller drawdowns); the Qullamaggie day-low stop (V2) is the choice if minimizing per-trade loss matters more than total return. This mirrors the mean-reversion finding — stops hurt — but the protection that makes "no stop" safe here is the **time stop**, not a price stop. (NB: this contrasts sharply with the short side, where stops are non-negotiable — see the short-side warning above.)

## Building the composite system (toward a tradeable PF)

Moving from isolated tests to a stacked system. Fixed defaults: **VCP entry (tightness<0.40, ATR<8%) + expansion-0.70 exit**, and the **V1 exit regime as the new baseline (no price stop + 20-day time stop)** — chosen for its higher PF, 51% win rate, and bounded ~20-day hold (capital recycles fast; explicitly avoiding the 3-month time-stop trap). Study set: gate-off `trips_v1_structure.csv` (106,522 trips, V1 exits, all 66 structure columns), breadth lagged 1 day. **P&L unclipped** (the $5 price floor already removes the penny-stock artifact, so the real fat tails are kept for a deployable estimate).

### Filter funnel (the 5 strength filters)

| step | trades | PF | win% | avg/trip | hold |
| ---- | -----: | ---: | ---: | -------: | ---: |
| base (VCP+exp+V1, gate-off) | 106,522 | 1.22 | 46.8% | $164 | 20d |
| + price > $5 | 77,605 | 1.06 | 48.2% | $39 | 21d |
| + within 15% of 52w-high | 43,081 | 1.21 | 50.9% | $109 | 20d |
| + breadth > 0.50 | 30,768 | 1.23 | 50.6% | $122 | 20d |
| + above 13w MA | 30,392 | 1.30 | 50.6% | $154 | 20d |

The five "strength" filters land at **PF ~1.30** — solid but short of the 1.5 bar, and they are **largely redundant with each other** (within-15%-of-high, above-13w-MA, and breadth all select the same "near highs in an uptrend" regime, so each adds little after the first). Note the price>$5 step *lowers* PF on the full range (it strips the artifact gains the penny names were contributing); the within-15% step is what re-establishes the real edge. Hold time holds steady at ~20 days throughout (the V1 time stop).

> **The 13w-MA filter is dropped as pure redundancy.** Tested head-to-head: with the within-15%-of-52wH filter on, adding "above 13w MA" changes the count by only +376 trades (30,768 → 30,392) and PF by 0.004 (1.292 → 1.296); at the selective end the two are *identical* (PF 1.562, the no-MA version giving 39 *more* trades). Anything within 15% of its 52w high is essentially always above its 13w MA, so the condition adds nothing. **Simplified system drops it** — same PF, one fewer parameter, more robust. The remaining strength filters are just **within 15% of 52w-high + breadth>0.5.**

### The selectivity levers that reach 1.5+ (RVOL floor, tighter tightness, tighter band)

The strength filters set the *regime*; pushing PF higher needs filters that *trim bad entries within it*. Within the composite, RVOL rises to a **6-20x sweet spot then collapses at ≥20x** (exhaustion), and tightness keeps paying as it gets tighter. Stacking these (unclipped):

| config | trades | PF | win% | avg/trip | hold |
| ------ | -----: | ---: | ---: | -------: | ---: |
| composite + RVOL 4-20x | 17,420 | 1.43 | 51.4% | $213 | 21d |
| composite + RVOL 6-20x | 7,890 | 1.46 | 52.2% | $231 | 21d |
| + tightness < 0.30 | 5,617 | **1.56** | 52.7% | $264 | 21d |
| + within 5% of 52w-high | 6,133 | 1.52 | 53.5% | $257 | 21d |
| **+ both (tight<0.30 AND within 5%)** | 4,264 | **1.62** | 54.1% | $286 | 21d |

**This clears the 1.5 target.** The decisive additions:
- **An RVOL *floor* (≥6x, capped <20x)** — the weak 3-4x tier was the drag; demanding a *strong but not blow-off* volume surge lifts PF 1.30 → 1.46. (RVOL ≥20x is genuinely bad and dropped; a **gap cap was tried and rejected** — gap≥40% looks bad clipped but unclipped it holds real fat-tail winners, so capping it *lowers* deployable PF.)
- **Tightness < 0.30** (vs <0.40) adds the most single jump (1.46 → 1.56) — the tighter-is-better sub-slice, confirmed.
- **Both together → PF 1.62, 54% win, $286/trip**, and **hold stays ~21 days** — the selectivity comes entirely from entry quality, not from holding longer.

### The cost — and the capital-velocity lens

The 1.6-PF config is **4,264 trips over 21 years ≈ 200/year ≈ <1/day**. High selectivity = few signals: a discretionary single-account trader is fine with that (you can't take many concurrent names anyway), but a capital-deployment view must accept long flat stretches. Crucially, **PF is not the only metric — capital velocity matters too**: the ~21-day hold (V1's 20-day time stop) means capital turns over ~12×/year, so a PF-1.6 / 21-day system compounds far more opportunity than a higher-PF system that ties capital up for months. (This is the explicit lesson from a real 2025 episode where a ~3-month effective hold trapped capital in topped names — the short time stop is a feature, not just a number.) A future evaluation should report an annualized return-on-deployed-capital, not PF alone.

### Converging spec

**Entry:** up≥5% on ≥6× (and <20×) RVOL, into a new-or-near 52-week high (**within 5-15%**), from a **tight base (tightness<0.30, ATR<8%)**, price>$5, **20-day market breadth > 0.5-0.6** (the 20-day breadth beats the 50/100-day — see the breadth-period sweep). (The 13w-MA filter is dropped — redundant with the 52wH band, same PF.) **Exit:** no price stop; **20-day time stop** + volatility-expansion (tightness>0.70). → **PF ~1.6-1.8 with the 20-day breadth, ~54% win, ~21-day hold.** Still to test: the deferred intraday-RVOL entry timing (first 5/15/30/60 min) and the Qullamaggie day-low stop as a *capital-velocity* play (frees capital faster on failures even if gross PF is a touch lower).

### Yearly breakdown of the final system — 21 of 22 years positive

The simplified composite — **VCP tight base (tightness<0.30, ATR<8%) + RVOL 6-20× + within 15% of the 52w-high + breadth>0.5, V1 exits (no price stop, 20-day time stop, expansion), price>$5** — by entry year (unclipped):

| year | trades | win% | PF | net P&L |
| ---- | -----: | ---: | ---: | -------: |
| 2005 | 282 | 58.2% | 1.77 | +76,898 |
| 2006 | 360 | 51.4% | 1.41 | +51,747 |
| 2007 | 324 | 47.8% | 1.27 | +38,823 |
| 2008 | 60 | 58.3% | 1.52 | +13,116 |
| 2009 | 154 | 51.9% | 1.75 | +50,603 |
| 2010 | 268 | 56.7% | 1.68 | +78,623 |
| 2011 | 228 | 48.2% | 1.12 | +12,330 |
| 2012 | 169 | 55.0% | 1.68 | +35,220 |
| 2013 | 415 | 63.1% | 2.77 | +222,803 |
| 2014 | 259 | 47.9% | 0.88 | −15,648 |
| 2015 | 224 | 54.5% | 1.34 | +30,027 |
| 2016 | 237 | 60.8% | 1.95 | +62,730 |
| 2017 | 451 | 55.7% | 1.81 | +123,003 |
| 2018 | 283 | 53.0% | 1.60 | +68,927 |
| 2019 | 278 | 52.5% | 1.37 | +46,784 |
| 2020 | 359 | 56.0% | 2.02 | +258,192 |
| 2021 | 433 | 37.2% | 1.41 | +160,194 |
| 2022 | 67 | 38.8% | 1.07 | +2,689 |
| 2023 | 186 | 52.7% | 1.01 | +1,533 |
| 2024 | 293 | 54.3% | 2.08 | +126,757 |
| 2025 | 215 | 50.2% | 1.25 | +37,224 |
| 2026 | 111 | 49.5% | 1.14 | +8,886 |

**Total +$1,491,461, 21 of 22 years positive, worst year −$15,648 (2014, PF 0.88).** The one down year is a small chop-year loss — about 1/14th of the best year — not a drawdown event. The transformation vs the naive v0 is the headline: the years that *defined* the original system's regime risk are now neutralized —

| year | naive v0 | final composite |
| ---- | -------: | --------------: |
| 2008 | −$218,917 | +$13,116 |
| 2021 | −$1,143,069 | +$160,194 |
| 2022 | −$482,904 | +$2,689 |
| 2023 | −$544,837 | +$1,533 |

The bears that cost the naive system >$2.3M combined now net roughly flat-to-positive — exactly the right behavior for a momentum book in a hostile regime: **don't make money, but don't give it back.** Three mechanisms compound to produce this: the **entry filters** (only contracted bases near highs in a positive-breadth tape), the **20-day time stop** (caps the bleed before a topped name grinds down), and **breadth>0.5** (sits out the worst stretches). Meanwhile the melt-up years are untouched (2013 PF 2.77, 2020 2.02, 2024 2.08, 2017 1.81), and **win rate is a genuine majority in most years (50-63%)** — unusual for momentum, a direct consequence of the no-price-stop regime not shaking you out.

**Caveat — thin bear-year samples.** 2008 (60 trips), 2022 (67), 2023 (186) are small because breadth>0.5 + near-highs correctly *suppresses* entries in bad tapes — which is the point, but it means those years' PFs rest on few trades and shouldn't be over-read. The edge is real and consistent; the precise bear-year P&L is noisier than the trade-rich bull years. The 2014 loss and the barely-positive 2022/2023/2026 also show the system is not magic — it's a *consistent, capital-preserving* momentum edge (PF ~1.5, 21/22 green, fast ~21-day capital turnover), not a holy grail.

### Tightening breadth to >0.60

Raising the breadth gate from 0.50 → 0.60 (still % above the 50-day MA) trades P&L for selectivity, and lands a higher PF:

| | breadth > 0.50 | breadth > 0.60 |
| -- | -------------: | -------------: |
| total PF | 1.56 | **1.79** |
| total net | +$1.49M | +$1.35M |
| trades | 5,656 | 3,855 |
| win% | 52.7% | 54.0% |
| years positive | 21/22 | 21/22 |
| worst year | −$15,648 (2014) | −$25,931 (2014) |

**PF jumps 1.56 → 1.79 for giving up ~$140k of total P&L and ~1,800 trades** — the marginal trades the stricter gate removes are low-quality, so per-trade edge rises sharply. Still 21/22 positive (same lone down year, 2014, which is slightly *worse* at −$26k — its losses were never a breadth problem; it was a whippy year for breakouts specifically). The melt-up years get even sharper (2020 PF **4.13** vs 2.02, 2024 2.53, 2013 2.32), and **2021 improves to PF 1.69 / +$199k** (vs 1.41 / +$160k) as the stricter gate filters more of the 2021-H2 deterioration. Cost: even thinner bear-year samples (2008 = 37 trips at PF 6.32 — not to be over-read; 2022 = 46). Net: **0.60 is the better setting if you want PF margin comfortably above 1.5 and tighter concentration into strong tapes**; 0.50 if you want more trades / fuller capital deployment.

### Breadth-period sweep — the 20-day is the better filter

The breadth gate so far used **% above the 50-day MA** (≈8w, close to the system's own horizons). `breadth.parquet` also stores the 20- and 100-day versions, so sweeping period × threshold is free (no rebuild). On the selective base (VCP tight<0.30, RVOL 6-20×, within 15% of 52wH, V1 exits, price>$5), unclipped:

| breadth filter | trades | net P&L | PF | win% |
| -------------- | -----: | -------: | ---: | ---: |
| (none) | 7,760 | +1,904,186 | 1.525 | 53.1% |
| 100d > 0.5 | 5,973 | +1,552,418 | 1.556 | 52.9% |
| 100d > 0.6 | 4,342 | +1,301,508 | 1.646 | 52.9% |
| 50d > 0.5 | 5,656 | +1,491,461 | 1.562 | 52.7% |
| 50d > 0.6 | 3,855 | +1,350,039 | 1.794 | 54.0% |
| **20d > 0.5** | 4,906 | +1,449,848 | **1.640** | 53.0% |
| **20d > 0.6** | 3,388 | +1,224,990 | **1.816** | 53.8% |

**Shorter breadth period = stronger filter, monotonically.** At every threshold the **20-day breadth beats the 50-day beats the 100-day**: 20d>0.5 (PF 1.64) outperforms 50d>0.5 (1.56) and nearly matches 50d>0.6; 20d>0.6 is the best of the grid at **PF 1.82**. The 100-day is weakest (1.56/1.65) — too slow, it stays "bullish" through market deterioration (the same lag that made the slow SPY MA and the trailing-realized health filter underperform). The 20-day is a *fast* read of whether the broad market is participating *right now*, so it turns defensive quickly exactly when breakouts start failing market-wide.

**Best practical setting: 20-day breadth.** `20d>0.5` (PF 1.64, 4,906 trips) gets most of the lift while keeping ~1,000 more trades than `50d>0.6` — a better PF-per-trade-sacrificed than the 50-day at either threshold; `20d>0.6` (PF 1.82) if you want maximum selectivity. This replaces the 50-day breadth in the converging spec. (Hold time is ~21 days across all — breadth changes *which* trades, not how long they're held.)

### 20-day breadth, yearly — 22 of 22 years positive

Switching the breadth gate from 50-day to 20-day eliminates the lone down year (2014). **Both thresholds are profitable in every one of the 22 years.**

**20-day breadth > 0.5** (total +1,449,849, 4,906 trips, PF 1.64):

| year | trades | win% | PF | net P&L |
| ---- | -----: | ---: | ---: | -------: |
| 2005 | 269 | 60.2% | 1.95 | +84,652 |
| 2006 | 337 | 53.4% | 1.54 | +60,002 |
| 2007 | 254 | 47.6% | 1.23 | +28,138 |
| 2008 | 73 | 58.9% | 1.63 | +18,784 |
| 2009 | 110 | 56.4% | 1.92 | +44,362 |
| 2010 | 260 | 56.9% | 1.86 | +88,529 |
| 2011 | 179 | 51.4% | 1.30 | +22,266 |
| 2012 | 166 | 54.2% | 1.69 | +33,593 |
| 2013 | 342 | 60.5% | 2.46 | +160,143 |
| 2014 | 227 | 50.2% | 1.11 | +10,813 |
| 2015 | 210 | 52.9% | 1.50 | +38,455 |
| 2016 | 216 | 58.3% | 1.85 | +52,643 |
| 2017 | 339 | 54.9% | 1.84 | +103,610 |
| 2018 | 286 | 53.5% | 1.57 | +75,979 |
| 2019 | 247 | 51.4% | 1.43 | +48,835 |
| 2020 | 290 | 55.9% | 2.01 | +213,387 |
| 2021 | 368 | 37.8% | 1.68 | +219,010 |
| 2022 | 71 | 46.5% | 1.39 | +14,426 |
| 2023 | 152 | 52.0% | 1.27 | +18,756 |
| 2024 | 220 | 54.5% | 1.85 | +71,847 |
| 2025 | 207 | 47.8% | 1.07 | +11,895 |
| 2026 | 83 | 55.4% | 1.77 | +29,724 |

**20-day breadth > 0.6** (total +1,224,992, 3,388 trips, PF 1.82):

| year | trades | win% | PF | net P&L |
| ---- | -----: | ---: | ---: | -------: |
| 2005 | 220 | 59.1% | 1.74 | +55,049 |
| 2006 | 244 | 53.7% | 1.56 | +43,606 |
| 2007 | 195 | 47.2% | 1.25 | +24,752 |
| 2008 | 42 | 59.5% | 1.33 | +6,005 |
| 2009 | 82 | 53.7% | 1.67 | +25,903 |
| 2010 | 223 | 56.1% | 1.81 | +72,412 |
| 2011 | 129 | 52.7% | 1.46 | +22,476 |
| 2012 | 116 | 60.3% | 2.21 | +36,011 |
| 2013 | 255 | 60.0% | 2.31 | +111,404 |
| 2014 | 181 | 50.8% | 1.18 | +12,966 |
| 2015 | 134 | 57.5% | 1.81 | +35,061 |
| 2016 | 166 | 59.6% | 1.98 | +44,127 |
| 2017 | 174 | 52.3% | 1.43 | +30,205 |
| 2018 | 157 | 52.2% | 1.40 | +34,232 |
| 2019 | 184 | 51.1% | 1.50 | +40,040 |
| 2020 | 212 | 61.3% | 3.77 | +260,556 |
| 2021 | 248 | 35.5% | 2.05 | +245,399 |
| 2022 | 52 | 50.0% | 1.77 | +18,981 |
| 2023 | 110 | 52.7% | 1.44 | +21,121 |
| 2024 | 111 | 60.4% | 2.05 | +44,424 |
| 2025 | 114 | 50.9% | 1.16 | +14,326 |
| 2026 | 39 | 61.5% | 3.21 | +25,936 |

**The 2014 loss was a breadth-speed artifact.** Under 50d>0.5, 2014 was the one red year (−$16k); under 20d>0.5 it flips to **+$11k (PF 1.11)** and under 20d>0.6 to **+$13k (PF 1.18)**. 2014 had several sharp mid-year breadth dips (Jan, Jul-Aug, Oct) that the fast 20-day gate sidestepped but the slow 50-day rode straight through — the same lag lesson, now costing a whole year. With the 20-day:

- **20d>0.5 — 22/22 green, worst year 2025 (+$12k, PF 1.07)**, 4,906 trips, PF 1.64. More trades / fuller deployment.
- **20d>0.6 — 22/22 green, worst year 2008 (+$6k), no year below PF 1.16**, 3,388 trips, PF 1.82. Tighter and higher-quality, melt-ups sharper (2020 PF 3.77, 2026 3.21, 2021 2.05), but bear-year samples get thin (2008=42, 2026=39 trips — high PFs there not to be over-read).

This is the milestone: a 22-year, **22-of-22-years-positive** momentum system at **PF 1.6-1.8, ~53% win, ~21-day hold** — shines in the parabolic bulls (2013/2020/2021/2024 all PF ≥1.85) and preserves capital in every bear. The threshold is a pure selectivity dial: 0.5 for more trades, 0.6 for more PF margin; both clear the bar with no losing years.

## Position sizing — scale UP on strength, but inverse-ATR backfires

Two sizing ideas tested post-hoc by re-weighting each trip's *return* (`exit/entry−1`) and comparing **return-per-dollar-deployed** and PF, each scheme **normalized to the flat baseline's average notional** (so it measures pure *reallocation*, not just betting more). Base = the final system at 20d-breadth>0.5.

### RVOL within the 6-20× band (the sizing signal)

| RVOL | trades | win% | PF | avg/trip |
| ---- | -----: | ---: | ---: | -------: |
| 6-8x | 2,314 | 52.2% | 1.56 | $253 |
| 8-10x | 1,139 | 51.2% | 1.30 | $161 |
| 10-13x | 795 | 56.9% | 1.82 | $343 |
| **13-16x** | 371 | 52.8% | **2.39** | **$647** |
| **16-20x** | 287 | 56.4% | **2.64** | $583 |

Past ~10×, higher RVOL is monotonically better — the **13-20× cells are PF 2.4-2.6 at ~$580-650/trip** (≈2.5× the 6-8× cell). The bigger the (sub-exhaustion) volume surge, the better the trade — a strong sizing signal.

### Inverse-ATR sizing — REJECTED

Volatility-parity sizing (notional ∝ 1/ATR%, target ~5% risk, clipped [0.25×, 4×]) makes the system **worse**:

| scheme (norm. to same avg notional) | return-per-$ | PF |
| ----------------------------------- | -----------: | ---: |
| flat $10k | **2.96%** | **1.64** |
| inverse-ATR | 2.10% | 1.53 |
| inverse-ATR × breadth>0.6 boost | 2.24% | 1.57 |

Counterintuitive but clear: within this system ATR% barely predicts *win rate* (the entry already caps ATR<8%), but it *does* scale return *magnitude* — a higher-ATR name that works makes a bigger % move. So 1/ATR sizing systematically **underweights the names that produce the fat-tail winners**, shrinking the right tail and cutting return-per-dollar by ~30%. Vol-parity equalizes dollar volatility, but this edge isn't vol-normalized — the big winners live in the higher-ATR names, so flattening them hurts. **Do not size inversely to ATR here.**

### Scale-UP on high-PF conditions — CONFIRMED (~50% lift); RVOL/breadth are INDEPENDENT levers

RVOL≥13 and breadth>0.6 first looked redundant, but the 2×2 contingency shows they are **largely independent and stack multiplicatively** — each lifts PF *within* the other's strata:

| RVOL≥13 | breadth>0.6 | trades | % | PF | avg return |
| :-----: | :---------: | -----: | --: | ---: | ---------: |
| no | no | 1,326 | 27% | 1.22 | 1.15% |
| no | yes | 2,922 | 60% | 1.68 | 3.05% |
| yes | no | 192 | 4% | 1.95 | 3.80% |
| **yes** | **yes** | **466** | **9.5%** | **2.69** | **7.18%** |

The both-true "A-trade" cell is **PF 2.69 / 7.18% average return — ~6× the raw edge of the worst cell** (1.15%). High-RVOL lifts PF 1.68→2.69 within high-breadth; high-breadth lifts 1.95→2.69 within high-RVOL. They are complementary, so they deserve *independent* multipliers (the earlier "1.5×1.5 ≈ redundant" reading was an artifact of under-powering the combined weight, not real overlap).

Independent multipliers, normalized to matched average notional (pure reallocation):

| scheme (norm.) | return-per-$ | PF |
| -------------- | -----------: | ---: |
| flat | 2.96% | 1.64 |
| RVOL 2× × breadth 2× | 3.63% | 1.81 |
| RVOL 5× only | 4.09% | 1.92 |
| tier (A=5× / B=2× / C=1×) | 3.80% | 1.85 |
| **RVOL 5× × breadth 2×** | **4.41%** | **2.00** |

**The A-trades genuinely deserve ~5× size.** Going RVOL 2×→5× (× breadth 2×) lifts return-per-dollar 3.63% → **4.41%** and PF to **2.00** — a **+49% improvement in risk-adjusted return over flat**, pure reallocation. RVOL is the dominant lever (5×-only already gets 4.09%), breadth an independent ~2× on top; the top "A" cell (both) thus carries an effective ~10× weight, justified by its 7.18%-avg-return / PF-2.69 edge.

**Verdict:** take every qualifying trade, but **size ~5× on RVOL≥13 and ~2× on breadth>0.6, independently** (so A-trades ≈ 10× a C-trade) — a large, free ~50% lift in risk-adjusted return. The inverse-ATR idea remains empirically wrong here.

**⚠️ Concentration caveat.** A 5-10× single-trade weight pours capital into ~10-14% of trips, so single-name and gap risk rise sharply even as PF improves — return-per-dollar rewards concentration but does not see ruin risk. A live book needs **hard per-position caps** so one 5×-weighted A-trade gapping down can't be catastrophic (the risk-of-ruin counterpart to the short-side warning). The right *absolute* size-up (deploying more total capital in strong regimes, not just reallocating) is a portfolio-construction / Kelly-fraction question for later, bounded by those caps.

### The 2D PF surface (breadth decile × RVOL) and what Kelly actually says

Building the full empirical PF table — breadth split into deciles (within the >0.5 population), crossed with RVOL buckets — to size each cell by its own edge rather than ad-hoc multipliers:

| breadth decile | RVOL 6-8 | RVOL 8-10 | RVOL 10-13 | RVOL 13-16 | RVOL 16-20 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 (b=0.50-.53) | 0.95 | 0.93 | 1.02 | 2.65 | 1.98 |
| 2 (b=.53-.57) | 1.45 | 1.69 | 1.93 | 3.38 | 1.19 |
| 3 (b=.57-.60) | 0.90 | 1.25 | 2.07 | 1.74 | 2.06 |
| 4 (b=.60-.63) | 1.66 | 0.93 | 2.40 | 1.88 | 2.91 |
| 5 (b=.63-.65) | 1.45 | 1.16 | 2.12 | 0.99 | 2.41 |
| 6 (b=.65-.68) | 1.47 | 1.48 | 1.82 | 1.25 | 3.06 |
| 7 (b=.68-.71) | 1.65 | 1.51 | 1.19 | 0.88 | 1.17 |
| 8 (b=.71-.74) | 3.02 | 1.68 | 1.61 | 2.99 | 4.58 |
| 9 (b=.74-.79) | 1.78 | 1.05 | 1.83 | **10.64** | **9.33** |
| 10 (b=.79-.96) | 1.69 | 1.40 | 2.72 | 2.48 | 1.20 |

The general gradient is right (RVOL columns trend up left→right; higher-breadth rows trend up), **but the individual 50 cells are noisy** — averaging ~98 trips, and the high-RVOL cells only 17-45. The decile-9 / RVOL-13-16 cell shows PF **10.64** on **30 trades**: that is one or two monster winners, not a 10× edge. **Sizing off raw per-cell PF would massively overfit the estimation noise.**

#### Kelly per cell is a trap; Kelly on the robust marginals is the answer

Kelly needs win rate W and the avg-win/avg-loss return ratio R: `f* = W − (1−W)/R`. Computed per cell, the high-PF cells produce absurd fractions on tiny samples — decile-9/13-16 (n=30, R=8.1) → Kelly **0.51**, decile-8/16-20 (n=30, R=3.5) → 0.44. Kelly is notoriously fragile to estimation error (it optimizes a ratio of sample means); betting 44-51% of bankroll on a 30-trade cell is how you blow up. **Do not size off per-cell Kelly.**

The reliable signal is the **marginals** (hundreds-to-thousands of trades each):

**Kelly by RVOL bucket:**

| RVOL | n | win% | R | full-Kelly | half-Kelly |
| ---- | --: | ---: | ---: | ---: | ---: |
| 6-8 | 2,314 | 52% | 1.42 | 0.185 | 9.2% |
| 8-10 | 1,139 | 51% | 1.23 | 0.115 | 5.7% |
| 10-13 | 795 | 57% | 1.38 | 0.255 | 12.8% |
| 13-16 | 371 | 53% | 2.12 | 0.306 | 15.3% |
| 16-20 | 287 | 56% | 1.99 | **0.346** | **17.3%** |

The hand-picked buckets above had one suspicious kink (6-8 PF > 8-10). Re-pooling RVOL into **equal-count quintiles** removes it and locates the breakpoints more honestly:

| RVOL quintile | range | win% | PF | avg ret |
| ------------- | ----- | ---: | ---: | ------: |
| 1 | 6.0-6.65 | 50.3% | 1.27 | 1.29% |
| 2 | 6.65-7.58 | 53.9% | 1.58 | 2.48% |
| 3 | 7.58-8.91 | 51.4% | 1.69 | 3.47% |
| 4 | 8.91-11.46 | 54.2% | 1.46 | 2.21% |
| 5 | 11.47-20 | 55.1% | **2.29** | 5.33% |

The "6-8 > 8-10" inversion was indeed a bucket-edge artifact — it vanishes in quantiles (PF rises 1.27 → 1.58 → 1.69 across the bottom three). The clean reads: **the weakest cell is the 6.0-6.65 floor** (barely clearing the 6× minimum, PF 1.27), and **the top quintile, RVOL > ~11.5×, is decisively the best (PF 2.29)** — robust across quartile/tercile groupings too. One residual wrinkle that **persists across groupings** (so it's likely real, not noise): a **soft spot around 9-11× (PF 1.46)** before the top quintile takes over. Net shape: weak at the 6× floor → strong 6.7-9× → mild 9-11 dip → strongest above ~11.5×. For sizing this sharpens the A-tier breakpoint from "≥13×" to **≈11.5×**, and confirms RVOL is the steeper, more reliable lever (PF span 1.27→2.29 ≈ 1.8×, vs breadth's 1.6×).

**Kelly by breadth decile** looked noisy and non-monotonic at decile resolution (Kelly 0.02 at decile 1, spiking to ~0.33 at deciles 8-9, dropping at 10) — **but that jumpiness was sampling noise.** Pooling the deciles into **quintiles** (~980 trips each) reveals a *clean monotonic* relationship:

| breadth quintile | range | win% | PF | avg ret |
| ---------------- | ----- | ---: | ---: | ------: |
| 1 | 0.50-0.57 | 50.9% | 1.32 | 1.50% |
| 2 | 0.57-0.63 | 52.8% | 1.40 | 1.93% |
| 3 | 0.63-0.68 | 54.1% | 1.49 | 2.30% |
| 4 | 0.68-0.74 | 53.3% | 1.95 | 3.94% |
| 5 | 0.74-0.96 | 53.8% | **2.09** | 5.11% |

PF rises monotonically 1.32 → 1.40 → 1.49 → 1.95 → 2.09 with breadth, no exception (quartiles/terciles agree). **Two earlier decile-level reads were noise and are retracted:** "decile 1 (0.50-0.53) is dead at PF 1.05" — at quintile resolution that bottom bucket is a healthy **1.32**; and "the top decile fades" — the top quintile is the **best** bucket (2.09). So unlike RVOL/gap/ATR (which genuinely fade at extremes), **breadth is monotonic-more-is-better all the way up** — no rolloff. It is still a *smoother, more gradual* lever than RVOL (PF span 1.32→2.09 ≈ 1.6×), but it is clean, not noisy — the earlier "noisier/secondary" label conflated decile sampling error with the underlying signal.

**Full-sample Kelly (all 4,906 trades pooled): W=53%, R=1.44 → f* = 0.20.**

#### What Kelly actually says (the practical answer)

- **Top-level: ~20% full-Kelly → run quarter-to-half Kelly ≈ 5-10% of bankroll per position.** Full Kelly is far too aggressive (and these trades overlap concurrently, which Kelly's sequential-bet math ignores — another reason to fraction down).
- **Scale by RVOL, but ~2-3×, not 5×.** Kelly on the robust RVOL marginals runs **0.12 → 0.35** (≈3× top-to-bottom; ~2× smoothing over the 8-10 dip). So the earlier 5× weighting was directionally right but **too aggressive** — the earlier reallocation test rewarded concentration without charging for variance, whereas Kelly prices the variance and lands near ~3×. **Size roughly: RVOL 8-10 ≈ 0.6×, 6-8 ≈ 1×, 10-13 ≈ 1.4×, 13-16 ≈ 1.7×, 16-20 ≈ 1.9×** (ratios of the marginal Kelly fractions).
- **Breadth: a mild secondary tilt** (~1× at the low deciles up to ~1.7× at deciles 8-9), applied on top — but noisier, so weight it less than RVOL.
- **Never size off the joint cells directly** — use the smooth marginal Kelly fractions (or heavily shrink the cells toward the global 0.20). And cap every position regardless, because half-Kelly on overlapping positions still concentrates risk.

**Bottom line:** the rigorous Kelly sizing is *RVOL-driven, ~2-3× span, fractioned to ~half-Kelly with hard caps* — a more conservative and more defensible version of the 5× idea. The 5× isn't "wrong" directionally; it's beyond what the variance-aware math supports, and rests partly on a few-trade tail it would over-bet.

### The clean 2D surface (breadth quintile × RVOL quintile) — additive, no interaction

With both axes pooled into equal-count quintiles (~196 trips/cell), the joint PF surface:

| breadth Q ↓ / RVOL Q → | Q1 | Q2 | Q3 | Q4 | Q5 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 (low breadth) | 1.05 | 1.31 | 1.00 | 1.77 | 1.61 |
| 2 | 1.16 | 1.49 | 1.19 | 1.41 | 1.91 |
| 3 | 1.31 | 1.34 | 1.77 | 1.47 | 1.54 |
| 4 | 1.61 | 2.10 | 2.95 | 1.21 | 1.86 |
| 5 (high breadth) | 1.36 | 1.75 | 1.74 | 1.46 | **4.95** |

The broad gradient matches both marginals (PF rises down-and-right), but at ~196 trips/cell the **individual cells are still too noisy to trust** — the lone 4.95 and 2.95 are a handful of winners, not real 3-5× edges. To test for genuine *interaction*, collapse to a robust 2×2 (top-2 quintiles = "high", ~770-1,750 trips/cell):

| | low RVOL | high RVOL |
| -- | -------: | --------: |
| **low breadth** | PF 1.28 (avg ret 1.41%) | PF 1.61 (2.65%) |
| **high breadth** | PF 1.93 (3.89%) | PF 2.16 (5.51%) |

**The two factors are independent and additive in average return — no synergy beyond stacking them.** From the 1.41% baseline: high-RVOL adds +1.24%, high-breadth adds +2.48%; both together gives 5.51% vs the 5.13% a purely-additive model predicts — a trivial +0.38% interaction, well within noise. The high/high corner is **PF 2.16**, exactly what the two marginals predict, *not* a hidden 3-4× cell.

**Implication for sizing (reassuring):** because the effects are additive and independent, you do **not** need the noisy 25-cell joint lookup. Size off the two *clean marginal* curves — breadth-quintile and RVOL-quintile Kelly fractions — combined as a product/sum. That captures essentially all the signal while avoiding the cell-level overfit, and the 2×2 confirms combining the two marginals is legitimate. This is the principled basis for the earlier "RVOL multiplier × breadth multiplier" sizing: each multiplier from its own well-sampled marginal, ~2-3× span on RVOL and ~1.6× on breadth, fractioned to half-Kelly with hard caps.

### 3×3 surface (terciles) and the Kelly-sized yearly result

The 2×2 was coarse; **terciles on each axis (~545 trips/cell)** give a clean, trustworthy joint surface:

| breadth ↓ / RVOL → | T1 (6.0-7.2) | T2 (7.2-9.6) | T3 (9.6-20) |
| --- | ---: | ---: | ---: |
| **T1 (b 0.50-0.61)** | 1.22 | 1.14 | 1.81 |
| **T2 (b 0.61-0.70)** | 1.36 | 1.33 | 1.61 |
| **T3 (b 0.70-0.96)** | 1.62 | 2.01 | **2.90** |

PF rises cleanly down each column (breadth) and rightward across each row (RVOL, with the known mild T2 dip). Bottom-left low/low = 1.22; top-right high/high = 2.90 — the additive stack, now smooth at this resolution.

#### Kelly-sized yearly result

Sizing each trade by the **product of its breadth-tercile and RVOL-tercile Kelly fractions** (justified by the additivity/independence finding), normalized to mean weight 1 (pure reallocation — average size = the flat baseline, so this measures *distribution*, not leverage). Tercile Kelly fractions: breadth 0.13 / 0.16 / 0.29, RVOL 0.14 / 0.17 / 0.29.

| year | trades | PF flat | net flat | PF Kelly | net Kelly |
| ---- | -----: | ------: | -------: | -------: | --------: |
| 2005 | 269 | 1.95 | +84,652 | 1.92 | +87,435 |
| 2006 | 337 | 1.54 | +60,002 | 1.64 | +65,340 |
| 2007 | 254 | 1.23 | +28,138 | 1.67 | +83,992 |
| 2008 | 73 | 1.63 | +18,784 | 1.74 | +17,586 |
| 2009 | 110 | 1.92 | +44,362 | 1.69 | +40,926 |
| 2010 | 260 | 1.86 | +88,529 | 1.85 | +105,597 |
| 2011 | 179 | 1.30 | +22,266 | 1.24 | +17,759 |
| 2012 | 166 | 1.69 | +33,593 | 2.15 | +49,075 |
| 2013 | 342 | 2.46 | +160,143 | 2.29 | +151,677 |
| 2014 | 227 | 1.11 | +10,813 | 1.23 | +21,876 |
| 2015 | 210 | 1.50 | +38,455 | 1.91 | +54,403 |
| 2016 | 216 | 1.85 | +52,643 | 1.87 | +51,222 |
| 2017 | 339 | 1.84 | +103,610 | 1.82 | +91,436 |
| 2018 | 286 | 1.57 | +75,979 | 1.51 | +59,374 |
| 2019 | 247 | 1.43 | +48,835 | 1.46 | +53,372 |
| 2020 | 290 | 2.01 | +213,387 | 2.72 | +336,724 |
| 2021 | 368 | 1.68 | +219,010 | 2.35 | +464,688 |
| 2022 | 71 | 1.39 | +14,426 | 1.82 | +31,143 |
| 2023 | 152 | 1.27 | +18,756 | 1.29 | +22,701 |
| 2024 | 220 | 1.85 | +71,847 | 2.13 | +67,083 |
| 2025 | 207 | 1.07 | +11,895 | 1.16 | +23,939 |
| 2026 | 83 | 1.77 | +29,724 | 2.28 | +32,867 |

**Totals: flat PF 1.64 / ++1,449,849 → Kelly-sized PF 1.87 / ++1,930,215** — return-per-dollar 2.96% → **3.93% (+33%)**, at matched average size. Reading it:

- **Still 22/22 positive** — sizing strengthened the weak years rather than risking them (2025 +$12k→+$24k, 2014 +$11k→+$22k).
- **The big bull years amplify hardest** — 2021 +$219k → **+$465k** (PF 1.68→2.35), 2020 +$213k → **+$337k** (2.01→2.72) — exactly right, since those were high-breadth/high-RVOL years where the Kelly weights leaned in and were rewarded.
- A few years give back slightly (2013 −$8k, 2017 −$12k, 2018 −$17k) as weight shifts away from their lower-tercile trades — a fair, expected reallocation cost. Net strongly positive across all years.

**⚠️ In-sample caveat.** The tercile Kelly fractions were fit on the *same* 2005-2026 data they're then applied to, so this +33% is an optimistic, in-sample figure — the sizing edge will be smaller live. Two things mitigate (not eliminate) it: the weights use only the *coarse, well-sampled marginals* (3 terciles × 3 terciles, ~545-2000 trips each — not the noisy 25/50-cell estimates), and the product form is the additivity-justified low-parameter model. Running **half-Kelly** (halve every weight span) and **hard per-position caps** is the deployable version; the live expectation is "a meaningful but smaller sizing lift on top of the PF-1.6 base," not +33%.

### Median & win-rate re-examination — the T2-RVOL "anomaly" is a tail artifact (2026-06-16)

The 3×3 surface above is built on **profit factor** (and the Kelly work on **mean return**). Both are dominated by the right tail — a single KOSS-type +1,000% trade can carry a whole cell — so they are *noisy* estimators of "is this cell good." Re-examining the **same filtered population** (the 5-filter final system: `entry_price≥5, ≥0.85×hiclose_52w, breadth>0.5, RVOL 6-20, tightness<0.30`) through **tail-robust** lenses changes the read.

**Median return per cell (V1 20-day exit):**

| breadth ↓ / RVOL → | T1 | T2 | T3 |
| --- | --- | --- | --- |
| **T1** | 0.02 | −0.15 | 1.37 |
| **T2** | 0.48 | 0.61 | 1.08 |
| **T3** | 0.53 | **1.40** | 0.86 |

vs the same cells' **mean** (T3×T3 = +7.56% — the apparent PF-2.90 "crown jewel"). **Key findings:**

- **RVOL is cleanly monotonic by median** (marginal medians 0.39 → 0.44 → **1.18**). The much-discussed **"T2-RVOL hole" (T2<T1) does NOT exist** on the robust measure — it appears only under mean/PF because the T1 and T3 cells carry fat right tails the middle lacks. **The non-monotonicity was a tail artifact of the mean, not a real entry weakness.** (Earlier "T2-RVOL entry hole" reads are retracted.)
- **The mean's "best cell" (T3×T3) is mediocre by median (+0.86%)** — its edge is "rarely, a moonshot," not "reliably positive." The **genuinely best cell is T3×T2** (high breadth, mid RVOL): median **+1.40%** *and* a healthy mean (+5.33%) — consistent across both lenses. This was hidden because PF spotlighted the tail-heavy T3×T3 instead.
- **One real soft spot survives the median:** T1×T2 (low breadth, mid RVOL) is the only *negative*-median cell (−0.15%) — a genuine entry weakness, not a tail artifact.

**ATR-bracket win-rate grid** (close-based resolution: target = entry + 3·ATR_abs_14, stop = entry − 1.5·ATR_abs_14; a day resolves only if its *close* crosses a bracket; 20-day cap, sign of cap-close = win/loss). Pure tail-immunity — every trade counts ±1. Overall: 33.6% target-hit, 48.7% stop, 12.4% cap-win, 5.2% cap-loss.

| breadth ↓ / RVOL → (win%) | T1 | T2 | T3 |
| --- | --- | --- | --- |
| **T1** | 40.1 | 44.4 | **50.2** |
| **T2** | 47.3 | 46.7 | 46.0 |
| **T3** | 44.4 | 48.7 | 46.2 |

- **RVOL win-rate marginal is monotonic** (43.8 → 46.6 → 47.5; target-hit 31.3 → 34.7 → 34.7) — *third* independent confirmation RVOL is a clean lever.
- **Breadth nearly vanishes as a win-rate lever** (marginal 44.9 → 46.7 → 46.5 — flat after T1). **Conclusion: breadth is a payoff / right-tail lever, NOT a hit-rate lever.** It doesn't make you win *more often*; it makes your winners *bigger*. This is invisible to win-rate by construction and explains why the joint PF grid wobbles even when both marginals are clean: it crosses a *frequency* axis (RVOL) with a *payoff* axis (breadth), and the cells are tail-dominated at ~550 trips each.

**Implication for sizing:** the two factors work through different channels — **RVOL** earns through hit-rate (robust, monotonic) *and* payoff; **breadth** earns purely through the right tail. Lean on RVOL for take/base-size decisions; use breadth for tail-leverage scaling. The earlier additive-marginals sizing stands, but with this mechanistic split now understood.

**Time-stop horizon sweep** (pure post-hoc N-day exits on the same population, fill at open N bars after entry): PF is flat ~1.57 from 5→25 days then dips at 30 (1.52); **median return peaks at 25 days (0.82%)** and win-rate peaks at 25 (53.3%), both turning down at 30. So **~20-25 trading days is the mild optimum** — the current V1 default of 20 is well-placed; 5 days is too short (amputates the RVOL edge — its median is ~⅓ of the 20-day's at every tercile); 30 holds losers too long.

### Collapsing the 2D grid to ONE sizing score — use `min`, not `max` or `avg` (2026-06-16)

The 3×3 joint PF grid is non-monotonic (noisy ~550-trip cells) while both *marginals* are clean and monotonic. The goal: derive a single sizing score per trade from the two marginals, dodging the noisy joint cells. The marginal PFs (filtered V1 system): **breadth** T1=1.37 / T2=1.43 / T3=2.13; **RVOL** T1=1.38 / T2=1.52 / T3=2.07. Three combiners were tested by **grouping trades on the exact derived score (no NTILE — quintiles introduced a tie-binning artifact on these coarse 5-9-value scores) and checking that realized PF rises monotonically with the score:**

| combiner | monotonic? | shape / why |
| --- | --- | --- |
| **`max`(b_pf, r_pf)** | ❌ NO | "bet on the stronger signal." Reintroduces non-monotonicity — realized PF peaks mid-high then *fades* at the top. `max` is an **OR gate**: a trade lands in the top bucket if *either* signal is high, so the top bucket = the elite (b3,r3) corner **plus ~2,200 one-sided trades** (b3,r1 / b1,r3 / etc.) that are genuinely mediocre (joint cells ~1.1-1.6). They dilute the top bucket to PF ~1.97. Overbets one-sided trades. |
| **`avg`(b_pf, r_pf)** | ❌ NO (two real inversions) | A softened `max`. Grouped on exact scores: score 1.444→PF **1.16** (below both lower scores) and score 1.745→PF **1.56** (below the score beneath it). Averaging the two marginals does **not** predict the joint behavior of one-sided trades, so their mid-range scores mis-rank them. |
| **`min`(b_pf, r_pf)** | ✅ YES | "you're only as strong as your weaker signal." Clean monotonic ladder (5 distinct *PF-value* scores): 1.37 → 1.49 → 1.44 → 2.07 → **2.81** (the one wobble, 1.49→1.44 between two near-equal scores, is 0.06 = noise). `min` is an **AND gate**: the top score is reachable *only* by (b3,r3), so its elite bucket is **pure** (533 trades, undiluted) → PF 2.81. One-sided trades correctly sink to the floor (the weak signal pulls them down). *(For sizing we use the coarser `min`-of-tercile-**indices** = 3 buckets; see the 3×3 sizing grid below. This 5-value ladder is monotonicity evidence only.)* |

**Why `min` is the right operator (the principle):** the breadth/RVOL edge is **additive — it needs BOTH factors** (established earlier: the 2×2 showed independence/additivity). The correct combiner therefore rewards *both being present*, not *either*. `max` (OR) is satisfied by one signal and so overbets one-sided trades; `avg` partially does the same and mis-ranks them; **`min` (AND) is the only one that demands both, which is why it alone produces a monotonic sizing score.** Note these are different *groupings* of trades, not different statistics of the same set — `min`'s top bucket out-PFs `max`'s top bucket because `min`'s (AND-selected) elite is a small pure subset while `max`'s (OR-selected) "top" is a bloated superset. (Earlier confusion that "min PF > max PF is impossible" dissolves once you see they bucket *different trades*.)

**`min`-of-marginals half-Kelly sized yearly result.** *Two-step:* (1) the `min`-of-marginal-PFs score is used **only to bucket** each trade into `min(breadth_t, rvol_t)` ∈ {1,2,3} — it is a **ranker/sorter, not the size**; (2) position size = each bucket's **realized half-Kelly** fraction, fit from that bucket's *actual* win-rate `W` and win/loss-ratio `R` (`f* = W − (1−W)/R`). So magnitude comes from realized outcomes, not from the PF-score value (we do **not** bet ∝ the score; PF is not a Kelly fraction). Mean-weight normalized to 1 (pure reallocation, no added leverage): half-Kelly fractions **0.074 / 0.108 / 0.176** (~2.4× span). Result: **flat $1.449M (PF 1.639) → min-Kelly $1.724M (PF 1.766)** = **+19% P&L, +0.13 PF**, positive all 22 years, with the lift concentrated in the high-breadth parabolic years (2020 +$72k, 2021 +$167k). This is **more conservative than the product-of-marginals sizing** (+33% in-sample, earlier section): `min` is a coarse, monotonically-*validated* 3-level ladder that refuses to up-size one-sided trades, trading a little in-sample upside for robustness — the better choice for deployment.

> **⚠️ In-sample caveat.** Both the bucketing (the marginal-PF cutoffs) and the per-bucket Kelly fractions were fit on the *same* 2005-2026 data they're applied to, so +19% is optimistic — the live lift will be smaller. `min` is *less* overfit than the product/fine-cell schemes (only 3 coarse, well-sampled buckets — 2,753 / 1,621 / 533 trades), but it is not out-of-sample. Deploy at half-Kelly (already applied) with hard per-position caps; treat +19% as an upper bound.

**The sizing lookup — `min(breadth_tercile, rvol_tercile)` 3×3 grid** (each cell = realized PF / half-Kelly fraction). This is the *deployable* table: read off a trade's breadth tercile (row) and RVOL tercile (column), take the cell, bet that fraction.

| breadth ↓ / RVOL → | T1 (6.0-7.2) | T2 (7.2-9.6) | T3 (9.6-20) |
| --- | --- | --- | --- |
| **T1 (b 0.50-0.61)** | 1.41 / 0.074 | 1.41 / 0.074 | 1.41 / 0.074 |
| **T2 (b 0.61-0.70)** | 1.41 / 0.074 | 1.68 / 0.108 | 1.68 / 0.108 |
| **T3 (b 0.70-0.96)** | 1.41 / 0.074 | 1.68 / 0.108 | **2.81 / 0.176** |

**Only 3 distinct values, by construction.** Each cell's bucket = `min` of its two tercile *indices*, so the entire bottom-row + left-column **L-shape collapses to bucket 1** ("at least one signal is in its weak tercile"), the diagonal band is bucket 2, and **only the both-high (T3,T3) corner is bucket 3.** This is exactly the AND-gate shape: you only get the elite size (0.176) when *both* breadth and RVOL are top-tercile; any one-sided trade is pulled down to the weaker tercile's size. The bucket sample sizes are 2,753 / 1,621 / 533. *(Note: this `min`-of-tercile-**indices** grid — symmetric, well-sampled, 3 values — is the sizing basis. The 5-value `min`-of-marginal-PF-**values** ladder in the combiner table above is finer monotonicity *evidence* only; its extra granularity comes from sub-noise breadth-vs-RVOL PF gaps like 1.37 vs 1.38 and is not used for sizing.)*

> **This min-bucket half-Kelly grid is the DEFAULT production system from 2026-06-16 onward.** Full per-month P&L (2005-01 … 2026-05, Kelly-weighted) lives in [momentum_v0_monthly_tables.md](momentum_v0_monthly_tables.md#final-production-system--monthly-breakdown-min-bucket-half-kelly-sized).

**Monthly win/loss profile (production, Kelly-sized).** Of **241 months**: **151 winning / 90 losing → 62.7% monthly win rate**, no flat months. The asymmetry compounds the count: **avg winning month +$15,745 vs avg losing month −$7,247 (win/loss ratio 2.17)** — a *monthly* PF of ~3.6, far above the trade-level 1.77 because pooling trades into months diversifies away single-trade variance. The classic momentum profile: lose small ~4 months in 10, win ~2× as large the other 6.

**Losing-month streaks.** 90 losing months fall into **51 streaks** — most are isolated: 31 single months, 8 two-month, 8 three-month, 1 four-month, 3 five-month (39 of 51 are ≤2 months; only 4 ever reached ≥4). **Crucially, the longest streaks are NOT the deepest** — depth and duration diverge:

| streak (months) | occurrences | worst by $ (period → P&L) |
| --- | --- | --- |
| 1 | 31 | — |
| 2 | 8 | — |
| 3 | 8 | **2021-02→04: −$114,787** (post-parabola give-back) |
| 4 | 1 | 2015-06→09: −$6,485 |
| 5 | 3 | 2019-05→10: −$29,578 |

So the **longest drought is 5 months** (3×, all shallow: −$30k / −$18k / −$9k), but the **deepest drawdown is a short, sharp 3-month streak — −$115k across 2021-02→04**, the parabola-peak give-back. Second-deepest is also 3 months (2024-12→2025-02, −$45k). The takeaway for a live operator: duration is mild (you rarely sit in the red more than 2 months, never more than 5), but the *dollar* pain is concentrated in a couple of sharp post-peak 3-month windows, not the long quiet droughts.

**How the cell PF is computed (important — it is NOT `min` of the marginals).** The `min(breadth_t, rvol_t)` index is used *only to group the actual trades* into 3 buckets; each bucket's PF is then computed **freshly from those pooled trades' realized returns** — `sum(winning-trade returns) / |sum(losing-trade returns)|` over the trades in the bucket. It is *not* derived from the two marginal PF numbers. Evidence it's ground-truth and not a marginal lookup: the grid PFs **1.41 / 1.68 / 2.81 do not equal any `min` of the marginals** (which would give min-of {1.37,1.38}=1.37, etc., not 1.41; and min{2.13,2.07}=2.07, not 2.81). The half-Kelly fraction per cell is likewise fit from those same pooled trades' actual win-rate `W` and win/loss-ratio `R`. So both numbers in every cell are realized statistics of the bucket's real trades, with `min` serving only as the bucket-assignment rule.

### Tightness is a THRESHOLD filter, not a sizing lever (2026-06-16)

Does the tightness measure (`range/(14·ATR)`, lower = more contracted) add edge *within* the breadth/RVOL stack? Split tightness into quantiles inside each `min(breadth_t, rvol_t)` bucket, two ways:

**(a) Within the filtered `<0.30` zone (quartiles):** PF is **flat-to-noisy** in every bucket — bucket 1: 1.39 / 1.67 / 1.25 / 1.37; bucket 2: 1.90 / 1.30 / 1.31 / 2.29; bucket 3 (elite, ~133/cell): 2.17 / 4.45 / 2.12 / 2.64 (the 4.45 is a 133-trade tail spike, not real). Median is flat-to-noisy too. **So *how* tight you are within the kept zone carries little extra signal.**

**(b) Across the full range `0.10-0.40` (quintiles), the gradient appears** — and it's a decline from tightest → loosest, concentrated in the *loosest* tail:

| min-bucket / tightness quintile → | Q1 (~0.10-0.21) | Q2 | Q3 | Q4 | Q5 (~0.32-0.40) |
| --- | --- | --- | --- | --- | --- |
| **bucket 1** (PF) | 1.46 | 1.45 | 1.35 | 1.32 | **1.20** |
| **bucket 2** (PF) | 1.81 | 1.38 | 1.94 | 1.31 | **1.09** |
| **bucket 3** (PF, ~167/cell) | 2.29 | 3.28 | 3.58 | 0.90 | 1.32 |

Bucket 1 decays cleanly (1.46→1.20, and Q5 is the only negative-median quintile, −0.8%); bucket 2's loosest quintile (1.09) is clearly worst; bucket 3 is too thin (~167/cell) to read a trend.

**Conclusion: tightness behaves like a threshold (a cliff near ~0.32 above which trades degrade), not a continuous lever.** Unlike RVOL/breadth (genuine continuous gradients you size on), tightness is roughly uninformative *below* its cutoff and only bites at the loose tail. So the right use is exactly the current hard cut, and there is **no value in tightness-based position sizing** within the system. (The real degradation cliff looks closer to ~0.32-0.34 than the current 0.30, so loosening to ~0.33 would add trades at minimal PF cost — a small trade-count knob for future fine-tuning, **left at 0.30 for now**.)

### Dollar-volume (ADV) on the FINAL system — a real, additive small-cap premium (2026-06-16)

Re-running the avg-dollar-volume breakdown (`avg_dollar_volume_4w_at_entry`) on the *final filtered system* (vs the earlier naive pre-filter pass, which was muddied by penny-stock artifacts). The study CSV carries trips down to **$100k ADV** (the engine floor was set to $100k, not the production $1M); 28.4% of the filtered trips are sub-$1M ADV.

**Pooled ADV quintiles (full population, $100k floor):** PF declines as ADV rises — **1.86 → 1.70 → 1.70 → 1.40 → 1.48** (Q1 $0.1-0.5M best). **With the $1M floor applied** (more conservative), the sweet spot is the **$2.5-5.5M band (PF 1.80)** and there's a clear **dead zone at $13-34M ADV (PF 1.11, negative median)** — mid/large breakouts are the weakest trades.

**ADV is NOT redundant with RVOL — it's an additive lever.** Within every `min`-bucket, the low-ADV tercile out-performs and the **high-ADV tercile is distinctly worst** (buckets 1+2, ~90% of trades):

| min-bucket / ADV tercile → | T1 (low, ~$0.1-1.5M) | T2 (mid) | T3 (high, >$10M) |
| --- | --- | --- | --- |
| **bucket 1** (PF) | 1.64 | 1.43 | **1.13** |
| **bucket 2** (PF) | 1.68 | 2.09 | **1.26** |
| **bucket 3** (PF, ~178/cell) | 2.23 | 2.02 | 4.63 (tail spike, thin) |

So the **small-cap momentum premium is real and survives the breadth/RVOL stack** — large-cap breakouts fade (efficiently priced), small-cap breakouts run. This matches the original Market-Wizards "aggressive mid/small-cap breakout" thesis.

**Deployment read (small account):** the low-ADV edge is *accessible*, not a hazard. On a breakout day actual volume is far above the trailing ADV — the **RVOL≥6 filter guarantees ≥6× the average**, so a $600k-ADV name trades $3.6M+ that day, plenty for a small account to fill with manageable slippage. **A small account *should* lean into these names** (the edge is strongest there and large players can't). **No ADV ceiling filter is wanted** — instead, manage the illiquid tail with *sizing*: bet less on the lowest-ADV names (dovetails with the `min`-Kelly sizing) and reserve capital for other opportunities. ADV is thus a continuous, additive dimension usable for sizing (tilt small, down-weight the least liquid), not a gate.

### Stop-variant comparison — time stop vs real price stops (2026-06-16)

The original next-step hypothesis was that an *actual price stop* (especially the tight Qullamaggie entry-day-low) would clean up the T2-RVOL "anomaly" by cutting bleeding trades the 20-day time stop lets linger. Two fresh engine runs on the **identical entry population** (gate/band + VCP filters `tight<0.40, ATR<8%`, expansion-0.70 kept; only the *stop sleeve* changes), filtered to the same 5-filter final system (3,511 trips each):

- **V1** = no price stop + 20-day time stop + expansion 0.70 (the established baseline).
- **Variant A** = 15-day-low trailing stop + expansion 0.70.
- **Variant B** = Qullamaggie entry-day-low stop (floored, rising to the 15-day-low) + expansion 0.70.

| metric | V1 (20d time) | A (15-day-low) | B (Qullamaggie day-low) |
| --- | --- | --- | --- |
| PF | **1.64** | 1.44 | 1.50 |
| win% | **52** | 44.0 | 38.3 |
| median return | **+0.64%** | −1.40% | −1.89% |
| avg return | **+2.95%** | +2.29% | +2.02% |
| avg hold (bars) | ~21 | 29.5 | **20.9** |
| **median hold (bars)** | 20 | 24 | **17** |
| exit mix | 97% time | 96% stop | 97% stop |

**Variant A 3×3 PF grid** (med% in parens):

| breadth ↓ / RVOL → | T1 | T2 | T3 |
| --- | --- | --- | --- |
| **T1** | 1.18 | 1.16 | 1.14 |
| **T2** | 1.24 | 1.19 | 1.16 |
| **T3** | 1.35 | 2.11 | **2.48** |

**Variant B 3×3 PF grid:**

| breadth ↓ / RVOL → | T1 | T2 | T3 |
| --- | --- | --- | --- |
| **T1** | 1.15 | 1.36 | 1.17 |
| **T2** | 0.94 | 1.39 | 1.25 |
| **T3** | 1.15 | **2.42** | **2.57** |

**Findings:**

1. **The price stops did NOT fix the grid — they confirm the tail-artifact read.** In *both* A and B every cell's median is **negative**; the only strong cells are the **top-right corner** (T3×T2, T3×T3). The "edge" under a price stop = "a few high-breadth/high-RVOL trades run far enough to beat the stop-out drag." The T2-RVOL "anomaly" never reappears as a real feature in any regime — **it was always a mean artifact** (consistent with the median/win-rate section above).
2. **The original capital-velocity assumption was BACKWARDS for the trailing low.** Variant A (15-day-low) holds *longer* than V1 (median 24 vs 20 bars), because in an uptrend the trailing low rises slowly and isn't hit until a real reversal — whereas the 20-day time stop is a hard guillotine. So **the time stop is a faster capital recycler than the trailing-low stop.**
3. **Variant B (Qullamaggie) IS the velocity play** — and the only regime that recycles faster than the time stop: median hold **17 bars**, with a structural guarantee against the months-long-bagholding trap (the tight day-low gets hit fast on failed breakouts; 97% exit via stop). This is the structural fix for the user's 2025 European-defense-stocks mistake.
4. **But B costs per-trade economics:** PF 1.50 (vs V1 1.64), win% 38.3 (lowest), median −1.89% (most negative) — the signature of a tight stop (most trades give a small loss back; a minority of runners carry the average).

**The decision is PF/win-rate (V1) vs capital velocity (Qullamaggie B), not quality-vs-quality.** PF undersells B's faster turnover; the metric that actually adjudicates is **annualized return on deployed capital** (deferred next-step). V1 wins per-trade; B may win per-unit-time.

### Does removing the exhaustion (expansion) sell help in aggregate? — No, keep it (2026-06-16)

Given how dominant the time stop is (97% of exits), we tested dropping the expansion exit entirely. Post-hoc counterfactual on the filtered V1 system: trips that exited via `expansion` are re-priced as if held to the 20-day time stop (open 20 bars after entry); all other trips unchanged.

| | V1 (with expansion) | V1 minus expansion |
| --- | --- | --- |
| PF | **1.64** | 1.55 |
| avg return | **2.95%** | 2.53% |
| median | 0.642 | 0.654 |
| win% | 53.0 | 53.1 |

**Keep the expansion sell.** The 95 affected trips (1.9%) averaged **+44.1%** by selling at the parabola vs **+22.3%** if held to day 20 — holding would have *halved* their return. Removing the exit costs ~9 PF points (1.64→1.55) and ~0.4% avg return. The **median is unchanged** (the median trade never touches it), so this is purely a **right-tail improvement**: a rare, *late* (~9-day), net-positive tail-catcher that exits the biggest winners at the top instead of riding them back down. This also closes out the earlier worry that the exhaustion sell might fire *early* on stretched-then-contracted breakouts — it does not (only 18 trips exit it within ≤3 days, 0.4% of all; the mechanism fires late on genuine parabolas).

### Annualized return on deployed capital — V1 wins outright; the capital-velocity thesis fails (2026-06-16)

The stop-variant section left V1 (20d time stop, PF 1.64) vs Qulla (entry-day-low, PF 1.50) as a *PF-vs-capital-velocity* tradeoff: Qulla recycles capital faster (median 17-bar hold), so the hope was that **annualized return on deployed capital** — which PF undersells for fast turnover — would favor Qulla despite the lower PF. Computed post-hoc on the identical 5-filter population via a date-keyed concurrent-capital sweep-line (`scripts/equity/return_on_capital.py`, adapted from `scripts/crypto/notional_at_risk.py`). **Two stages** (annualization base = the ~21.35-year span):

- **Stage A — return on *realized concurrent demand*** (no cap): total P&L / concurrent-notional-base / years. Capital base = peak / p99 / p95 / mean of the daily concurrent-deployed-notional series (headline p95 — robust to a few extreme 2021 cluster-days).
- **Stage B — fixed $100k book, NON-compounding**, min-Kelly sizing (the 3-bucket 0.074/0.108/0.176 weights), hard $20k per-position cap, **drop-the-new** on overflow (no eviction). Flat yield (profits swept, not reinvested) — chosen so early-year differences don't geometrically amplify and muddy the comparison.

| metric | V1 (20d time) | Qulla (day-low) |
| --- | --- | --- |
| filtered trips | 4,907 | 3,511 |
| total P&L | **$1,448,869** | $709,116 |
| PF | **1.64** | 1.50 |
| p95 concurrent capital | $510,000 | **$400,000** |
| peak concurrent capital | $1,110,000 | **$860,000** |
| **ann RoC @ p95** | **13.3%** | 8.3% |
| **ann RoC @ mean** | **31.9%** | 20.1% |
| **Stage B ann return ($100k book)** | **17.9%** | 8.6% |
| Stage B trade capture | 61.0% | 59.5% |

**Verdict: V1 wins decisively on return-on-capital — the capital-velocity thesis is wrong.** Qulla *did* lower capital demand as predicted (p95 $400k vs $510k, ~22% less; peak $860k vs $1.11M) — but its **total P&L fell more than twice as much** ($709k vs $1.45M, −51%). The tight day-low stop doesn't just recycle capital faster; it **cuts winners short** — the same fast exits that free capital also clip trades that would have recovered and run (consistent with Qulla's −1.89% median trade: it bleeds to the tight stop constantly). So the numerator (P&L) collapses faster than the denominator (capital) shrinks, and RoC falls. Stage B confirms it from the achievable-book angle: on the *same* $100k book, V1 returns **17.9% vs Qulla's 8.6%**, with **near-identical capture** (61% vs 59.5%) — Qulla's faster recycling did **not** even let the book accept meaningfully more trades.

**So V1 (20-day time stop) is the production exit:** higher PF, ~2× the P&L, and higher return on capital at every capital base. Qulla's only remaining advantage is *structural* — a hard cap on hold length (anti-bagholding insurance), a risk-management property, not a return one. The original 2025-mistake motivation (capital tied up in topped names) is better addressed by V1's 20-day time stop, which is itself a hard hold cap, than by accepting Qulla's P&L penalty.

> **Reading the Stage A bases:** p95 (~$510k for V1) is the realistic "capital to rarely turn a trade away"; mean (~$213k) the average tied-up capital; peak (~$1.1M) the worst 2021 cluster day. The 13-32% ann-RoC range across bases brackets the strategy's capital efficiency. **In-sample caveat** (Stage B sizing + filters fit on this data) applies as elsewhere.

### Mean-reversion trailing-limit exit — sell the bounce, not the stop (2026-06-16)

Idea: when an exit fires (the price stop for variants A/B; the **time stop** for V1, which has no price stop), instead of dumping at the next open — often a bad print after a down-day-into-stop — rest a **sell LIMIT at the N-day high**, ratchet it DOWN-only each bar (`limit = min(limit, rolling N-day high)`), and fill on the first bar whose high reaches it (selling *into* a bounce). 5-bar time cap to market if no bounce comes. No-lookahead: a resting limit at price P fills when a later bar trades through P. New engine flags `--trail-limit-high N --trail-limit-time-cap M`; the conversion fires only on "get-me-out" exits (stop/breakeven/time), not the discretionary expansion/ATR exits. The whole sweep is on the **full production filter set** (price≥$5, ADV≥$100k, breadth>0.5, rvol∈[6,20], tightness<0.30, **ATR%<8%**); shown at both the 0.85 band and the **0.95 band** (the [proximity optimum](#cleaned-result--proximity-to-the-52-week-high-is-the-edge)). The base column reproduces the documented PF-1.639 baseline exactly (0.85 band) — see the regression note below.

**0.95 band (the production-optimum proximity filter), 3,671 trips each — exit-only difference. ATR%<8% cap applied (the full production filter set):**

| Variant | Exit | PF | Total P&L | Median % | Win% | Fill% |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| V1 (time stop) | base | 1.670 | $1,138,768 | 0.89 | 54.0 | — |
| V1 | **N=1** | **1.988** | **$1,521,917** | 1.58 | 56.8 | 96.9 |
| V1 | N=2 | 1.854 | $1,388,740 | 1.31 | 55.7 | 93.1 |
| V1 | N=3 | 1.798 | $1,336,811 | 1.28 | 55.6 | 82.9 |
| V1 | N=4 | 1.734 | $1,267,894 | 1.19 | 55.0 | 71.9 |
| A (15d-low) | base | 1.709 | $1,150,023 | 0.30 | 51.5 | — |
| A | **N=1** | **2.133** | **$1,583,066** | 1.11 | 54.3 | 97.0 |
| A | N=2 | 1.945 | $1,430,589 | 0.97 | 53.9 | 92.6 |
| A | N=3 | 1.841 | $1,343,647 | 0.88 | 53.8 | 80.3 |
| A | N=4 | 1.758 | $1,260,208 | 0.73 | 53.6 | 67.0 |
| B (Qulla day-low) | base | 1.711 | $986,441 | −1.02 | 44.9 | — |
| B | **N=1** | **2.401** | **$1,505,400** | 0.00 | 49.9 | 97.7 |
| B | N=2 | 2.080 | $1,333,163 | 0.13 | 50.7 | 92.7 |
| B | N=3 | 1.892 | $1,208,291 | 0.19 | 51.2 | 78.7 |
| B | N=4 | 1.800 | $1,140,836 | 0.22 | 51.6 | 65.5 |

**0.85 band (the looser proximity filter), 4,907 trips each — same shape, slightly lower PF in every cell. ATR%<8% cap applied; base = the documented PF-1.639 production baseline, reproduced exactly:**

| Variant | Exit | PF | Total P&L | Median % | Win% | Fill% |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| V1 (time stop) | base | 1.639 | $1,448,869 | 0.64 | 53.0 | — |
| V1 | **N=1** | **1.954** | **$1,957,112** | 1.41 | 56.0 | 97.0 |
| V1 | N=2 | 1.825 | $1,786,302 | 1.16 | 54.8 | 93.3 |
| V1 | N=3 | 1.768 | $1,714,572 | 1.14 | 54.9 | 82.9 |
| V1 | N=4 | 1.710 | $1,633,291 | 1.08 | 54.6 | 72.3 |
| A (15d-low) | base | 1.664 | $1,443,263 | 0.12 | 50.5 | — |
| A | **N=1** | **2.081** | **$2,020,111** | 0.89 | 53.5 | 97.2 |
| A | N=2 | 1.898 | $1,818,528 | 0.73 | 53.0 | 92.8 |
| A | N=3 | 1.808 | $1,715,171 | 0.72 | 53.1 | 80.4 |
| A | N=4 | 1.735 | $1,619,902 | 0.64 | 53.0 | 67.5 |
| B (Qulla day-low) | base | 1.660 | $1,224,294 | −1.20 | 43.8 | — |
| B | **N=1** | **2.333** | **$1,912,600** | −0.02 | 49.4 | 97.8 |
| B | N=2 | 2.018 | $1,678,190 | 0.03 | 50.1 | 92.8 |
| B | N=3 | 1.839 | $1,516,091 | 0.11 | 50.5 | 78.6 |
| B | N=4 | 1.748 | $1,426,780 | 0.16 | 51.1 | 65.3 |

The 0.95 band beats 0.85 on PF in **every cell** (e.g. V1 N=1 1.988 vs 1.954; B N=1 2.401 vs 2.333), reconfirming the [proximity finding](#cleaned-result--proximity-to-the-52-week-high-is-the-edge) — but the trip count is lower (3,671 vs 4,907), so the 0.85 band carries more total P&L at slightly lower PF. The monotone N=1 > N=2 > N=3 > N=4 > base ordering is identical in both bands.

**Findings:**
- **N=1 > N=2 > N=3 > N=4 > base, monotonically, in all 6 variant×band combinations** — no exceptions. The tighter the limit-target high, the bigger the gain, and **N=1 (prior-bar high) is the optimum** — the edge keeps strengthening all the way to the tightest target. This monotone decay is the signature of a **real bounce-capture effect**, not a lucky N: a 1-2-day high sits close to current price so almost any post-stop bounce reaches it (N=1 fills ~97% with **0% timeout**, N=2 ~93%); a 4-day high is further above, so the bounce more often fails to reach it (N=4 fills only ~65-72%) and the trade falls back to the market exit (= baseline). Edge decays smoothly toward baseline as N rises.
- **N=1 did NOT degenerate into "fills too eagerly = sells low" (the worry that motivated testing it).** The ratchet is DOWN-only and the fill requires `high ≥ limit`, so even at N=1 you sell at the *prior bar's high* — after a down-day-into-stop, that's still well above where a market-on-open exit would dump you. Eager filling just means you almost never *miss* the bounce (0% timeout, vs 25-33% at N=4); you are not selling lower, you are selling more *reliably* into the immediate rebound.
- **Large, universal gain.** At the 0.95 band, N=1 lifts PF by +0.32 (V1: 1.670→1.988), +0.42 (A: 1.709→2.133), +0.69 (B: 1.711→2.401) and total P&L by 34-53% — by far the biggest single exit improvement found.
- **It rescues the distressed stop variants — Qulla (B) most.** B's baseline is the worst (median −1.02%, win 45%: it dumps at the panic low). N=1 makes it the **highest-PF cell of all (2.401)** by refusing to sell the bottom and instead selling the rebound. The faster/more-distressed the stop, the more the limit helps — which makes mechanistic sense.
- **The whole return distribution shifts up** (median, not just mean): V1 median 0.74→1.19, B median −1.15→+0.08 at N=2. Tail-robust, so it's not a few outliers.

**Caveat on magnitude — sharpest at N=1:** the limit fills "when a bar's high ≥ the N-day-high limit," assuming a resting limit catches that price intrabar. On thin small-caps the N-day high may be a single untradeable tick. At N=1 essentially the **entire population (~97%, 0% timeout) is credited a "sell at the prior-bar high" fill**, so this is the most optimistic the mechanic can get — **N=1's PF 1.95-2.40 is the upper bound of the upper bound.** The robust takeaways are (a) the **monotone shape** (tighter target = better, no exception across 8 N-values × variants × bands) and (b) that even a *conservative* version (N=3/N=4, which leave 17-33% of exits to the honest market fill) is already a large improvement. A realistic execution model (limit fills only on demonstrated intrabar liquidity, or fill at a haircut to the N-day high) is the right next refinement before banking the N=1 magnitude. The best single config is **B (or A) at N=1, 0.95 band (PF 2.40 / 2.13)**; A-vs-B turns on the capital-velocity re-rank (B recycles faster — the [RoC analysis](#annualized-return-on-deployed-capital--v1-wins-outright-the-capital-velocity-thesis-fails) should be re-run with this exit, since the limit narrowed B's P&L gap to V1 dramatically).

> **⚠️ Two filter regressions found & fixed (2026-06-16) — baseline now reproduces exactly.** While running this sweep, the V1 baseline first came in at PF 1.475 vs the documented 1.639. Two independent causes, both real: **(1) ADV floor** — the engine's `--min-avg-dollar-volume` **default had drifted to 1,000,000** (from the study's 100,000), silently rejecting ~1,400 sub-$1M-ADV breakouts, precisely the low-ADV small-caps that carry the [premium](#dollar-volume-adv-on-the-final-system--a-real-additive-small-cap-premium); default reset to 100,000. **(2) ATR% cap** — the runs omitted `--max-atr-pct 0.08`, admitting 167 high-ATR entries (every one with ATR%>8%, e.g. ZBIO at 9.4%) that blended to **PF 0.855** and dragged the system down. The ATR% and tightness filters are near-orthogonal (a stock can be a tight base *and* a jumpy high-ATR name), so tightness<0.30 does **not** subsume the ATR cap. With **both** filters applied, the baseline reproduces the documented system **to the dollar: 4,907 trips, PF 1.639, +$1,448,869.** The full production entry filter is therefore: price≥$5, ADV≥$100k, breadth>0.5, rvol∈[6,20], tightness<0.30, **ATR%<8%**, proximity band (0.85, optimum 0.95). All trailing-limit tables above already have both filters applied.

### Default system locked: Qulla + N=1, NO time stop (2026-06-17)

The headline Qulla N=1 cell (PF 2.401) is now the **default production system**. One refinement: the documented run carried `--time-stop 20` (inherited from the shared `$COMMON` flag block), but it is **near-inert for Qulla** — the entry-day-low price stop and the 0.70 expansion exit almost always fire before day 20. Dropping it is a small genuine improvement:

| Qulla N=1, 0.95 band | Trips | Win% | PF | Total P&L | Median hold | Fill% |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| **with `--time-stop 20`** (old default) | 3,671 | 49.9 | 2.401 | $1,505,400 | 18 | 97.7 |
| **no time stop** (new default) | 3,671 | 46.1 | **2.426** | **$1,715,972** | 18 | 97.1 |

Same 3,671 trips and same 18-day median hold (the time-stop changes only *which* exit a handful of right-tail trades take, not the population). No-time-stop earns **+$210k (+14%) and +0.025 PF** — the time-stop was clipping a few day-20 trades that would otherwise have run to bigger wins (hence higher P&L, slightly lower win-rate from the added variance). The 18-day median is the **expansion exit + price stop** naturally clustering, *not* the time-stop biting — which is why removing it leaves the median untouched. **New default = Qulla day-low stop + N=1 trailing limit + 0.70 expansion exit, no time stop.**

> **Reproduction recipe (exact — so this never requires archaeology again).** The trail-limit grid is built from a broad base CSV (engine flags), then the production filter is applied **post-hoc** in SQL. The new default (Qulla N=1, no time stop) is:
>
> ```bash
> # base CSV — broad rvol gate; ATR/tightness applied post-hoc (NOT in-engine)
> dotnet run --project TradingEdge.MomentumBacktest -c Release -- \
>   --expansion-exit 0.70 --min-pct-of-52w-high 0.85 --rvol-threshold 3 --no-structure \
>   --initial-stop-day-low --trail-limit-high 1 --trail-limit-time-cap 5 \
>   --trips-csv /tmp/b_n1.csv
> # (add --time-stop 20 to reproduce the OLD default = PF 2.401 / 3,671)
> ```
>
> Post-hoc filter (DuckDB, read-only `data/trading.db`), 0.95 band:
>
> ```sql
> WITH b AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) breadth_lag1
>            FROM read_parquet('data/equity/momentum_v0/breadth.parquet'))
> SELECT count(*), <PF/win/pnl aggregates>
> FROM read_csv_auto('/tmp/b_n1.csv', header=true) r
> JOIN b ON b.date = CAST(r.entry_date AS DATE)
> JOIN structure_levels sl ON sl.ticker=r.symbol AND sl.date=CAST(r.entry_date AS DATE)
> WHERE CAST(r.entry_price AS DOUBLE) >= 5.0
>   AND b.breadth_lag1 > 0.5                                    -- pct_above_20, lagged 1 trading day
>   AND CAST(r.rvol_at_entry AS DOUBLE) BETWEEN 6.0 AND 20.0
>   AND CAST(r.tightness_14_at_entry AS DOUBLE) < 0.30
>   AND CAST(r.atr_pct_14_at_entry AS DOUBLE) < 0.08
>   AND CAST(r.entry_price AS DOUBLE) >= 0.95 * sl.hiclose_52w; -- 0.85 for the looser band
> ```
>
> **Two gotchas that cost a session to rediscover:** (1) ATR%<8% and tightness<0.30 are applied **post-hoc**, not via `--max-atr-pct`/`--max-tightness` — putting them in-engine off a `--rvol-threshold 6` base changes the `is_entry` set and the numbers drift. (2) `breadth_lag1` is **`pct_above_20`** (fraction of stocks above their *20-day* MA), gated `> 0.5`, lagged one trading day — *not* `pct_above_50`. Both are documented above; getting either wrong reproduces a plausible-but-wrong PF (2.12–2.23 instead of 2.40).

## Caveats & known limitations

- **Same-day-close entry is realistic via MOC, not just "optimism."** The signal is defined by day T's close and we fill at that same close — achievable in practice with a **market-on-close (MOC) order**, which gets the official closing print. This is a *deliberate, defensible* choice, not a bias to apologize for: on these breakouts the **overnight gap is on average positive while the next-day intraday action is on average negative**, so entering at the close (vs the next open) is the *better*, not the more-optimistic, fill. The **exit is strictly no-lookahead** (next-day open). A "closer-to-the-open, pro-trader-style" entry is a *future* test, not a correction.
- **Regime filtering is already present — via market breadth.** The earlier "no regime filter / add a SPY 10/20-day MA gate" caveat is **superseded**: the breadth filter (% of liquid stocks above their 20-day MA, lagged 1 day) *is* a market-regime gate, and a better one than a single SPY MA — it's the cross-sectional health of the actual tradable universe. The 22/22-years-positive result with `breadth>0.5` is the evidence it works. A separate SPY-MA gate is no longer a priority.
- **"Mid cap" is approximated by a dollar-volume floor**, not real market cap (no shares-outstanding data yet). A true market-cap breakdown is deferred pending a Polygon shares-outstanding feed wired into `TradingEdge.Massive`.
- **Survivorship**: the price universe is delisting-inclusive (good — no survivorship bias from prices), but `ticker_reference.type` is a *current* snapshot, so the CS/ADRC filter is approximate for delisted names.
- **No costs modeled.** No commissions, no slippage, no borrow. At fixed $10k notional these are second-order vs the regime effect, but a realistic-execution pass should add them.
- **Uncapped, non-compounding sizing** means net P&L is a raw breadth-and-edge measure, not an achievable equity curve — many concurrent positions can be open at once. A capped-book / fractional-equity variant is needed before reading these as portfolio returns.

## Next steps (in order)

**✅ DONE (2026-06-16):** the stop-variant comparison, median/win-rate re-examination, time-stop sweep, exhaustion-removal test, the `min`-of-marginals sizing, AND the annualized-return-on-capital adjudication are all complete (see the sections above). Net conclusions: T2-RVOL "anomaly" = mean tail artifact (RVOL is cleanly monotonic; breadth is a tail/payoff lever); keep the expansion sell (dropping it costs ~9 PF points); size off the `min`-of-tercile-indices 3-bucket half-Kelly grid (+19% in-sample); **and V1 (20d time stop) is the production exit — it beats Qullamaggie on PF (1.64 vs 1.50), total P&L (~2×), AND annualized return on capital (13.3% vs 8.3% @ p95; 17.9% vs 8.6% on a $100k book). The capital-velocity thesis for Qulla failed: faster recycling cut winners short, so P&L fell more than capital demand did.** Engine infra done: `structure_levels` materialized + auto-rebuilt by `ingest-data`; `--no-structure` (~12→6 min); binary `--no-52w-high` replaced by numeric `--min-pct-of-52w-high`.

**Immediate — the big lever: AI-agent news/catalyst analysis (2026-06-16 reprioritization):**
1. **Automated news-catalyst classification with AI agents.** The thesis: these breakouts are catalyst-driven, and **separating *good* catalysts from *mediocre* ones could push this method above the PF-2 range** — a bigger lever than any volatility-measure refinement. Use AI agents to classify each breakout's catalyst (earnings beat, FDA, M&A, guidance raise, sympathy/momentum-only, etc.) and grade its quality, then bucket PF by catalyst class. This is a natural, high-value agent use case. (News-gathering infra already exists — see CLAUDE.md "News Search Strategy": Polygon `download-news` + Google-News date-range supplementation.)

**Deferred (de-prioritized 2026-06-16 — user decided overnight it's not a priority):**
- **v1 volume-weighted / Gaussian volatility** replacing ATR%: per-day VWAP + VW-σ; pairwise daily Gaussians ≈ ATR%, 14-day Gaussian ≈ 14-day range/tightness. Thesis was a less-noisy volatility estimate — but the median/win-rate work already showed the "noise" (T2 anomaly) was a mean tail artifact, not a volatility-measurement problem, which weakens the motivation. Needs intraday data. **Parked.**

**Deferred / opportunistic:**
2. **Recompute `breadth.parquet` through the current date** once the latest daily bars are downloaded (Massive sub) — so we know which breadth tercile (and thus live Kelly size) the market is in *today*.
3. **Intraday-RVOL entry timing** (first 5/15/30/60 min) — earlier entry for tighter risk control; deferred, needs intraday data.
4. **Average-daily-dollar-volume breakdown on the FINAL system** — doable now (every trip carries `avg_dollar_volume_4w_at_entry`); bucket the composite system's PF by liquidity tier to see whether the edge concentrates in a particular dollar-volume band *within* the filtered universe (the earlier ADV breakdown was on the naive v0, pre-filters), and whether ADV is a sizing/selection lever. Apply the usual $5 floor + outlier discipline.
5. **Real market-cap bucketing** once shares-outstanding is available (Massive shares-outstanding endpoint) — pair it with the ADV breakdown above; trips carry `avg_dollar_volume_4w_at_entry` as the interim proxy until then.

---

*Generated 2026-06-15. Engine: [TradingEdge.MomentumBacktest](../TradingEdge.MomentumBacktest). Data: `data/trading.db` (split-adjusted daily, 2003-09→2026-05). Trips/log: `data/equity/momentum_v0/`.*
