# Mid-Cap Momentum v0 — Regime-Dependent, Prints in Parabolic Bull Innings

> ## ⚠️ INVALID RESULTS WARNING (2026-06-17)
> **Everything from the [Mean-reversion trailing-limit exit](#mean-reversion-trailing-limit-exit--sell-the-bounce-not-the-stop-2026-06-16) section onward is INFLATED and must not be trusted.**
> The N-day trailing-limit exit had a fill bug: at N=1 it filled at `min(seed, bar.high)` on the very next bar **every time** — a guaranteed sale at the recent high, which a real resting limit order cannot achieve. This top-tick credit inflated profit factor by roughly **+0.7** (e.g. the "PF 2.33" / "PF 3.0 at tight stops" / "80% of months positive" headlines).
> The rewritten **v1 engine** (`TradingEdge.MomentumV1`) fills the limit only when price actually trades up to the *prior* bar's high. On realistic fills the true PF is **~1.7–1.8** (post-breadth), the trailing limit is a ~+1% refinement (not a major edge), and **stop-window 4 is the sweet spot, not window 1** (the "tighter is better" ordering was the artifact). See `docs/momentum_v1_results.md` for the corrected numbers.
> The sections *before* that one (entry filters, proximity-band finding, ADV buckets) are unaffected — they don't depend on the trailing-limit exit.

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

> **Note:** P&L below is bucketed by **entry date** (when the system fired), Kelly/flat as stated in the section it came from.

<details>
<summary>Full per-month table, all trips, by entry date (2005-01 … 2026-05) — click to expand</summary>

| month | trades | win% | PF | net P&L |
| ----- | -----: | ---: | ---: | -------: |
| 2005-01 | 72 | 59.7% | 1.09 | +2,988 |
| 2005-02 | 109 | 28.4% | 0.73 | −18,921 |
| 2005-03 | 73 | 30.1% | 0.55 | −26,842 |
| 2005-04 | 49 | 49.0% | 2.56 | +42,860 |
| 2005-05 | 47 | 61.7% | 1.93 | +21,570 |
| 2005-06 | 76 | 51.3% | 0.94 | −2,798 |
| 2005-07 | 134 | 40.3% | 0.93 | −5,273 |
| 2005-08 | 135 | 32.6% | 0.81 | −12,131 |
| 2005-09 | 86 | 25.6% | 0.71 | −23,394 |
| 2005-10 | 73 | 58.9% | 1.62 | +24,297 |
| 2005-11 | 84 | 57.1% | 2.44 | +53,610 |
| 2005-12 | 70 | 47.1% | 1.27 | +11,095 |
| 2006-01 | 205 | 57.1% | 1.86 | +100,539 |
| 2006-02 | 135 | 51.1% | 1.36 | +23,656 |
| 2006-03 | 146 | 45.9% | 1.33 | +18,491 |
| 2006-04 | 161 | 19.9% | 0.21 | −102,525 |
| 2006-05 | 113 | 6.2% | 0.04 | −133,108 |
| 2006-06 | 28 | 28.6% | 0.38 | −10,281 |
| 2006-07 | 35 | 57.1% | 0.79 | −4,369 |
| 2006-08 | 48 | 54.2% | 2.10 | +19,576 |
| 2006-09 | 43 | 60.5% | 1.40 | +9,826 |
| 2006-10 | 91 | 50.5% | 2.02 | +37,402 |
| 2006-11 | 111 | 53.2% | 1.89 | +38,134 |
| 2006-12 | 77 | 44.2% | 0.91 | −3,945 |
| 2007-01 | 95 | 52.6% | 0.98 | −1,453 |
| 2007-02 | 168 | 35.7% | 1.14 | +11,088 |
| 2007-03 | 64 | 53.1% | 2.44 | +33,277 |
| 2007-04 | 150 | 42.7% | 1.04 | +2,730 |
| 2007-05 | 161 | 45.3% | 1.59 | +36,975 |
| 2007-06 | 93 | 39.8% | 0.63 | −13,601 |
| 2007-07 | 136 | 16.2% | 0.18 | −117,998 |
| 2007-08 | 69 | 42.0% | 1.13 | +5,772 |
| 2007-09 | 62 | 62.9% | 3.36 | +68,142 |
| 2007-10 | 158 | 24.1% | 0.28 | −96,138 |
| 2007-11 | 52 | 28.8% | 0.28 | −29,425 |
| 2007-12 | 40 | 22.5% | 0.26 | −24,783 |
| 2008-01 | 34 | 23.5% | 0.18 | −24,109 |
| 2008-02 | 22 | 27.3% | 0.94 | −953 |
| 2008-03 | 20 | 35.0% | 0.46 | −6,401 |
| 2008-04 | 49 | 49.0% | 2.04 | +22,033 |
| 2008-05 | 63 | 47.6% | 1.05 | +1,906 |
| 2008-06 | 68 | 22.1% | 0.13 | −53,310 |
| 2008-07 | 38 | 34.2% | 0.14 | −31,522 |
| 2008-08 | 19 | 31.6% | 0.25 | −11,692 |
| 2008-09 | 62 | 1.6% | 0.01 | −111,383 |
| 2008-10 | 2 | 50.0% | 7.58 | +4,838 |
| 2008-11 | 3 | 0.0% | 0.00 | −8,323 |
| 2009-01 | 9 | 22.2% | 0.06 | −7,561 |
| 2009-02 | 7 | 14.3% | 0.17 | −5,714 |
| 2009-03 | 5 | 80.0% | 0.51 | −2,395 |
| 2009-04 | 10 | 60.0% | 1.85 | +4,489 |
| 2009-05 | 41 | 51.2% | 1.94 | +20,265 |
| 2009-06 | 43 | 34.9% | 0.66 | −15,146 |
| 2009-07 | 47 | 46.8% | 1.21 | +7,005 |
| 2009-08 | 63 | 38.1% | 0.89 | −5,503 |
| 2009-09 | 99 | 32.3% | 0.28 | −59,716 |
| 2009-10 | 119 | 34.5% | 0.75 | −26,318 |
| 2009-11 | 73 | 50.7% | 1.29 | +10,671 |
| 2009-12 | 92 | 38.0% | 9.69 | +542,213 |
| 2010-01 | 153 | 27.5% | 0.70 | −41,530 |
| 2010-02 | 75 | 69.3% | 4.09 | +41,484 |
| 2010-03 | 122 | 57.4% | 2.32 | +74,853 |
| 2010-04 | 205 | 8.8% | 0.08 | −207,336 |
| 2010-05 | 40 | 25.0% | 0.43 | −24,634 |
| 2010-06 | 25 | 32.0% | 0.84 | −4,271 |
| 2010-07 | 28 | 42.9% | 0.70 | −5,336 |
| 2010-08 | 58 | 55.2% | 1.78 | +40,076 |
| 2010-09 | 73 | 69.9% | 3.07 | +69,800 |
| 2010-10 | 100 | 66.0% | 2.06 | +43,629 |
| 2010-11 | 138 | 63.8% | 2.90 | +103,770 |
| 2010-12 | 115 | 41.7% | 1.14 | +9,086 |
| 2011-01 | 164 | 39.0% | 1.02 | +2,184 |
| 2011-02 | 195 | 38.5% | 1.02 | +2,320 |
| 2011-03 | 103 | 53.4% | 1.09 | +4,620 |
| 2011-04 | 163 | 19.6% | 0.21 | −99,916 |
| 2011-05 | 107 | 29.9% | 0.76 | −19,331 |
| 2011-06 | 39 | 43.6% | 0.88 | −2,741 |
| 2011-07 | 80 | 2.5% | 0.01 | −99,965 |
| 2011-08 | 16 | 18.8% | 0.09 | −11,290 |
| 2011-09 | 12 | 25.0% | 0.10 | −14,614 |
| 2011-10 | 29 | 37.9% | 0.82 | −2,415 |
| 2011-11 | 28 | 42.9% | 0.73 | −5,300 |
| 2011-12 | 13 | 69.2% | 23.11 | +69,919 |
| 2012-01 | 43 | 58.1% | 4.13 | +35,744 |
| 2012-02 | 93 | 50.5% | 1.75 | +25,837 |
| 2012-03 | 64 | 20.3% | 0.18 | −48,054 |
| 2012-04 | 63 | 7.9% | 0.10 | −43,893 |
| 2012-05 | 56 | 28.6% | 1.17 | +7,722 |
| 2012-06 | 25 | 52.0% | 1.64 | +4,782 |
| 2012-07 | 57 | 57.9% | 1.62 | +13,499 |
| 2012-08 | 67 | 70.1% | 4.64 | +57,573 |
| 2012-09 | 59 | 44.1% | 0.64 | −13,044 |
| 2012-10 | 76 | 34.2% | 0.59 | −17,783 |
| 2012-11 | 54 | 48.1% | 1.73 | +21,374 |
| 2012-12 | 36 | 66.7% | 5.87 | +45,353 |
| 2013-01 | 115 | 57.4% | 2.30 | +39,946 |
| 2013-02 | 128 | 54.7% | 1.18 | +8,814 |
| 2013-03 | 82 | 43.9% | 2.58 | +51,057 |
| 2013-04 | 100 | 71.0% | 8.61 | +148,220 |
| 2013-05 | 141 | 56.0% | 4.04 | +138,251 |
| 2013-06 | 67 | 47.8% | 4.28 | +138,620 |
| 2013-07 | 156 | 47.4% | 1.44 | +27,306 |
| 2013-08 | 169 | 51.5% | 1.39 | +38,832 |
| 2013-09 | 122 | 45.9% | 1.52 | +37,494 |
| 2013-10 | 196 | 48.5% | 0.98 | −2,677 |
| 2013-11 | 128 | 53.1% | 2.16 | +63,902 |
| 2013-12 | 124 | 44.4% | 1.83 | +55,645 |
| 2014-01 | 214 | 41.1% | 1.87 | +128,897 |
| 2014-02 | 177 | 48.0% | 0.87 | −9,089 |
| 2014-03 | 145 | 16.6% | 0.10 | −191,970 |
| 2014-04 | 58 | 51.7% | 2.17 | +25,501 |
| 2014-05 | 59 | 61.0% | 3.87 | +27,194 |
| 2014-06 | 101 | 31.7% | 0.59 | −21,476 |
| 2014-07 | 82 | 42.7% | 0.65 | −15,228 |
| 2014-08 | 56 | 33.9% | 0.59 | −13,094 |
| 2014-09 | 77 | 22.1% | 0.60 | −35,120 |
| 2014-10 | 74 | 54.1% | 1.54 | +36,421 |
| 2014-11 | 73 | 52.1% | 2.88 | +40,526 |
| 2014-12 | 68 | 41.2% | 1.30 | +13,122 |
| 2015-01 | 78 | 62.8% | 3.53 | +58,229 |
| 2015-02 | 134 | 54.5% | 3.46 | +91,538 |
| 2015-03 | 111 | 38.7% | 0.60 | −30,689 |
| 2015-04 | 128 | 56.2% | 1.54 | +37,622 |
| 2015-05 | 97 | 51.5% | 0.90 | −5,489 |
| 2015-06 | 85 | 38.8% | 0.65 | −17,800 |
| 2015-07 | 103 | 19.4% | 0.18 | −55,136 |
| 2015-08 | 72 | 12.5% | 0.11 | −58,934 |
| 2015-09 | 31 | 29.0% | 0.33 | −18,882 |
| 2015-10 | 68 | 47.1% | 0.54 | −15,273 |
| 2015-11 | 58 | 25.9% | 0.20 | −26,205 |
| 2015-12 | 33 | 9.1% | 0.03 | −49,576 |
| 2016-01 | 20 | 15.0% | 0.09 | −25,961 |
| 2016-02 | 21 | 66.7% | 1.13 | +1,183 |
| 2016-03 | 33 | 42.4% | 0.22 | −17,155 |
| 2016-04 | 42 | 38.1% | 0.32 | −30,937 |
| 2016-05 | 55 | 60.0% | 1.62 | +19,083 |
| 2016-06 | 45 | 42.2% | 1.09 | +3,114 |
| 2016-07 | 93 | 45.2% | 1.66 | +29,312 |
| 2016-08 | 168 | 43.5% | 0.98 | −1,272 |
| 2016-09 | 75 | 38.7% | 1.27 | +8,302 |
| 2016-10 | 83 | 48.2% | 1.18 | +8,674 |
| 2016-11 | 195 | 65.6% | 1.66 | +77,778 |
| 2016-12 | 60 | 36.7% | 2.22 | +34,101 |
| 2017-01 | 114 | 39.5% | 0.97 | −1,563 |
| 2017-02 | 185 | 45.9% | 1.15 | +11,203 |
| 2017-03 | 110 | 40.0% | 0.97 | −2,095 |
| 2017-04 | 134 | 46.3% | 1.45 | +27,297 |
| 2017-05 | 168 | 54.2% | 1.62 | +38,713 |
| 2017-06 | 91 | 27.5% | 0.54 | −27,477 |
| 2017-07 | 96 | 31.2% | 0.37 | −39,079 |
| 2017-08 | 144 | 68.1% | 4.93 | +155,098 |
| 2017-09 | 111 | 55.9% | 2.09 | +60,342 |
| 2017-10 | 174 | 37.4% | 1.06 | +8,201 |
| 2017-11 | 192 | 51.0% | 1.68 | +66,843 |
| 2017-12 | 94 | 37.2% | 0.44 | −50,166 |
| 2018-01 | 160 | 33.8% | 0.65 | −39,824 |
| 2018-02 | 119 | 42.9% | 1.19 | +10,329 |
| 2018-03 | 94 | 36.2% | 0.53 | −47,938 |
| 2018-04 | 66 | 59.1% | 5.32 | +188,472 |
| 2018-05 | 178 | 61.8% | 3.30 | +173,249 |
| 2018-06 | 154 | 39.0% | 0.71 | −38,122 |
| 2018-07 | 113 | 45.1% | 0.57 | −33,773 |
| 2018-08 | 185 | 51.9% | 1.81 | +61,246 |
| 2018-09 | 93 | 19.4% | 0.16 | −91,915 |
| 2018-10 | 61 | 18.0% | 0.16 | −94,190 |
| 2018-11 | 64 | 15.6% | 0.05 | −70,143 |
| 2018-12 | 16 | 25.0% | 0.09 | −17,536 |
| 2019-01 | 29 | 62.1% | 1.57 | +5,595 |
| 2019-02 | 107 | 50.5% | 2.17 | +41,825 |
| 2019-03 | 82 | 43.9% | 0.68 | −21,145 |
| 2019-04 | 64 | 37.5% | 0.41 | −41,393 |
| 2019-05 | 89 | 48.3% | 0.86 | −10,355 |
| 2019-06 | 62 | 51.6% | 0.84 | −5,523 |
| 2019-07 | 95 | 27.4% | 0.16 | −64,105 |
| 2019-08 | 86 | 40.7% | 0.59 | −19,605 |
| 2019-09 | 40 | 27.5% | 0.34 | −20,994 |
| 2019-10 | 97 | 55.7% | 2.17 | +38,549 |
| 2019-11 | 102 | 52.9% | 1.74 | +35,519 |
| 2019-12 | 115 | 37.4% | 1.32 | +31,227 |
| 2020-01 | 126 | 26.2% | 0.78 | −28,760 |
| 2020-02 | 158 | 5.1% | 0.11 | −276,323 |
| 2020-03 | 41 | 14.6% | 0.31 | −66,944 |
| 2020-04 | 42 | 33.3% | 0.38 | −40,480 |
| 2020-05 | 118 | 61.9% | 4.87 | +305,707 |
| 2020-06 | 163 | 39.9% | 1.75 | +160,718 |
| 2020-07 | 193 | 36.8% | 2.19 | +301,741 |
| 2020-08 | 190 | 29.5% | 0.66 | −81,411 |
| 2020-09 | 121 | 39.7% | 1.61 | +60,591 |
| 2020-10 | 128 | 50.0% | 1.83 | +100,428 |
| 2020-11 | 266 | 67.7% | 9.74 | +1,225,590 |
| 2020-12 | 383 | 55.9% | 5.02 | +957,632 |
| 2021-01 | 501 | 42.9% | 1.91 | +432,872 |
| 2021-02 | 572 | 19.2% | 0.30 | −759,497 |
| 2021-03 | 221 | 28.1% | 0.51 | −146,852 |
| 2021-04 | 126 | 34.1% | 0.48 | −71,018 |
| 2021-05 | 155 | 40.6% | 1.28 | +25,589 |
| 2021-06 | 183 | 19.7% | 0.24 | −183,410 |
| 2021-07 | 117 | 40.2% | 0.75 | −41,388 |
| 2021-08 | 163 | 42.9% | 1.13 | +19,159 |
| 2021-09 | 146 | 31.5% | 0.56 | −87,565 |
| 2021-10 | 109 | 39.4% | 0.58 | −33,300 |
| 2021-11 | 201 | 18.4% | 0.11 | −238,273 |
| 2021-12 | 48 | 18.8% | 0.04 | −59,386 |
| 2022-01 | 31 | 19.4% | 0.15 | −26,788 |
| 2022-02 | 51 | 43.1% | 0.68 | −14,345 |
| 2022-03 | 79 | 26.6% | 0.24 | −108,789 |
| 2022-04 | 52 | 3.8% | 0.04 | −79,987 |
| 2022-05 | 33 | 27.3% | 0.35 | −17,969 |
| 2022-06 | 28 | 17.9% | 0.19 | −27,248 |
| 2022-07 | 23 | 34.8% | 0.97 | −483 |
| 2022-08 | 72 | 27.8% | 0.22 | −80,232 |
| 2022-09 | 23 | 26.1% | 0.17 | −47,734 |
| 2022-10 | 39 | 41.0% | 0.47 | −27,728 |
| 2022-11 | 37 | 37.8% | 0.40 | −27,791 |
| 2022-12 | 48 | 41.7% | 0.57 | −23,812 |
| 2023-01 | 60 | 38.3% | 0.49 | −36,238 |
| 2023-02 | 103 | 20.4% | 0.18 | −84,611 |
| 2023-03 | 73 | 37.0% | 0.51 | −39,048 |
| 2023-04 | 55 | 34.5% | 0.40 | −41,149 |
| 2023-05 | 139 | 44.6% | 0.73 | −33,336 |
| 2023-06 | 71 | 43.7% | 0.77 | −11,460 |
| 2023-07 | 106 | 18.9% | 0.17 | −107,561 |
| 2023-08 | 119 | 24.4% | 0.16 | −107,532 |
| 2023-09 | 59 | 20.3% | 0.11 | −62,235 |
| 2023-10 | 32 | 50.0% | 1.04 | +923 |
| 2023-11 | 85 | 58.8% | 0.82 | −15,936 |
| 2023-12 | 108 | 41.7% | 0.93 | −6,654 |
| 2024-01 | 105 | 57.1% | 2.27 | +85,269 |
| 2024-02 | 223 | 46.2% | 1.13 | +16,985 |
| 2024-03 | 151 | 30.5% | 0.45 | −80,916 |
| 2024-04 | 87 | 39.1% | 0.47 | −57,928 |
| 2024-05 | 141 | 42.6% | 0.83 | −16,517 |
| 2024-06 | 67 | 40.3% | 0.66 | −12,358 |
| 2024-07 | 91 | 25.3% | 0.55 | −45,535 |
| 2024-08 | 117 | 56.4% | 2.23 | +119,108 |
| 2024-09 | 111 | 49.5% | 1.50 | +57,568 |
| 2024-10 | 190 | 45.8% | 1.05 | +7,174 |
| 2024-11 | 429 | 35.7% | 0.97 | −7,224 |
| 2024-12 | 144 | 18.1% | 0.22 | −236,276 |
| 2025-01 | 110 | 20.9% | 0.13 | −118,230 |
| 2025-02 | 156 | 9.0% | 0.07 | −258,189 |
| 2025-03 | 56 | 19.6% | 0.90 | −11,038 |
| 2025-04 | 35 | 48.6% | 3.90 | +121,831 |
| 2025-05 | 119 | 47.1% | 1.47 | +49,886 |
| 2025-06 | 101 | 34.7% | 0.66 | −61,184 |
| 2025-07 | 140 | 40.7% | 1.29 | +50,876 |
| 2025-08 | 149 | 61.1% | 1.76 | +84,854 |
| 2025-09 | 191 | 47.6% | 2.31 | +183,528 |
| 2025-10 | 228 | 25.9% | 0.43 | −226,154 |
| 2025-11 | 143 | 51.0% | 1.27 | +30,330 |
| 2025-12 | 110 | 39.1% | 0.50 | −50,598 |
| 2026-01 | 165 | 40.0% | 2.01 | +170,866 |
| 2026-02 | 147 | 38.8% | 0.80 | −22,087 |
| 2026-03 | 122 | 48.4% | 1.70 | +73,544 |
| 2026-04 | 147 | 58.5% | 3.22 | +185,250 |
| 2026-05 | 132 | 45.5% | 1.19 | +10,054 |

</details>

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

> **Note:** bucketed by **entry date**.

<details>
<summary>Monthly table — $100-300k bucket, by entry date — click to expand</summary>

| period | trades | win% | PF | net P&L |
| --- | --: | --: | --: | --: |
| 2005-01 | 15 | 40.0% | 0.72 | −4,440 |
| 2005-02 | 33 | 42.4% | 0.63 | −6,487 |
| 2005-03 | 15 | 20.0% | 0.53 | −9,632 |
| 2005-04 | 4 | 50.0% | 6.84 | +8,640 |
| 2005-05 | 14 | 42.9% | 2.26 | +11,296 |
| 2005-06 | 19 | 31.6% | 1.54 | +7,248 |
| 2005-07 | 17 | 41.2% | 1.94 | +12,856 |
| 2005-08 | 21 | 47.6% | 1.35 | +5,181 |
| 2005-09 | 19 | 21.1% | 0.34 | −14,169 |
| 2005-10 | 11 | 27.3% | 0.99 | −148 |
| 2005-11 | 17 | 47.1% | 0.53 | −9,892 |
| 2005-12 | 26 | 30.8% | 0.69 | −7,686 |
| 2006-01 | 37 | 35.1% | 1.40 | +8,287 |
| 2006-02 | 14 | 42.9% | 0.65 | −4,748 |
| 2006-03 | 31 | 45.2% | 1.70 | +10,046 |
| 2006-04 | 28 | 25.0% | 0.37 | −17,416 |
| 2006-05 | 15 | 20.0% | 0.20 | −17,363 |
| 2006-06 | 17 | 47.1% | 1.04 | +560 |
| 2006-07 | 7 | 57.1% | 1.12 | +523 |
| 2006-08 | 11 | 54.5% | 2.51 | +6,689 |
| 2006-09 | 16 | 50.0% | 3.52 | +29,395 |
| 2006-10 | 13 | 38.5% | 1.74 | +5,155 |
| 2006-11 | 19 | 36.8% | 1.61 | +6,584 |
| 2006-12 | 19 | 42.1% | 1.01 | +222 |
| 2007-01 | 20 | 45.0% | 0.74 | −5,287 |
| 2007-02 | 23 | 43.5% | 3.32 | +23,404 |
| 2007-03 | 15 | 33.3% | 0.27 | −8,460 |
| 2007-04 | 23 | 39.1% | 1.36 | +4,128 |
| 2007-05 | 29 | 44.8% | 1.75 | +10,497 |
| 2007-06 | 15 | 73.3% | 10.65 | +24,346 |
| 2007-07 | 26 | 11.5% | 0.09 | −30,895 |
| 2007-08 | 6 | 83.3% | 191.88 | +10,207 |
| 2007-09 | 6 | 66.7% | 4.33 | +9,274 |
| 2007-10 | 20 | 25.0% | 0.26 | −16,079 |
| 2007-11 | 6 | 33.3% | 0.28 | −2,099 |
| 2007-12 | 10 | 20.0% | 1.84 | +11,292 |
| 2008-01 | 7 | 57.1% | 2.40 | +6,314 |
| 2008-02 | 9 | 88.9% | 213.46 | +142,794 |
| 2008-03 | 3 | 33.3% | 0.20 | −1,497 |
| 2008-04 | 2 | 50.0% | 14.67 | +25,883 |
| 2008-05 | 20 | 35.0% | 0.92 | −1,357 |
| 2008-06 | 5 | 20.0% | 0.24 | −1,931 |
| 2008-07 | 3 | 33.3% | 0.39 | −604 |
| 2008-08 | 6 | 16.7% | 0.10 | −8,074 |
| 2008-09 | 10 | 0.0% | 0.00 | −24,558 |
| 2008-12 | 2 | 0.0% | 0.00 | −2,497 |
| 2009-01 | 1 | 100.0% | inf | +723 |
| 2009-02 | 2 | 50.0% | 0.30 | −1,013 |
| 2009-03 | 3 | 100.0% | inf | +10,823 |
| 2009-04 | 4 | 25.0% | 0.45 | −4,021 |
| 2009-05 | 12 | 50.0% | 1.29 | +2,325 |
| 2009-06 | 35 | 48.6% | 1.50 | +12,877 |
| 2009-07 | 11 | 72.7% | 3.29 | +9,585 |
| 2009-08 | 21 | 42.9% | 1.55 | +11,626 |
| 2009-09 | 34 | 32.4% | 0.43 | −24,210 |
| 2009-10 | 35 | 37.1% | 1.20 | +7,566 |
| 2009-11 | 20 | 20.0% | 0.44 | −10,173 |
| 2009-12 | 31 | 32.3% | 2.07 | +24,625 |
| 2010-01 | 26 | 34.6% | 0.91 | −1,688 |
| 2010-02 | 18 | 55.6% | 1.12 | +925 |
| 2010-03 | 35 | 54.3% | 2.48 | +31,114 |
| 2010-04 | 41 | 39.0% | 0.80 | −5,797 |
| 2010-05 | 12 | 8.3% | 0.03 | −14,870 |
| 2010-06 | 18 | 16.7% | 0.12 | −20,191 |
| 2010-07 | 4 | 100.0% | inf | +2,598 |
| 2010-08 | 11 | 72.7% | 5.99 | +11,434 |
| 2010-09 | 13 | 84.6% | 48.21 | +52,479 |
| 2010-10 | 19 | 36.8% | 1.19 | +2,544 |
| 2010-11 | 33 | 51.5% | 8.90 | +142,519 |
| 2010-12 | 30 | 50.0% | 1.18 | +3,117 |
| 2011-01 | 34 | 47.1% | 1.00 | −100 |
| 2011-02 | 32 | 25.0% | 0.41 | −18,121 |
| 2011-03 | 18 | 50.0% | 3.43 | +33,614 |
| 2011-04 | 19 | 47.4% | 1.48 | +5,624 |
| 2011-05 | 16 | 6.3% | 0.01 | −11,762 |
| 2011-06 | 12 | 8.3% | 0.02 | −15,776 |
| 2011-07 | 5 | 40.0% | 0.33 | −4,598 |
| 2011-08 | 1 | 0.0% | 0.00 | −1,192 |
| 2011-09 | 2 | 50.0% | 0.25 | −1,035 |
| 2011-10 | 4 | 25.0% | 0.34 | −2,949 |
| 2011-11 | 7 | 28.6% | 0.61 | −2,699 |
| 2011-12 | 5 | 20.0% | 8.02 | +34,057 |
| 2012-01 | 10 | 40.0% | 2.09 | +5,424 |
| 2012-02 | 25 | 56.0% | 2.70 | +20,878 |
| 2012-03 | 21 | 71.4% | 5.00 | +35,702 |
| 2012-04 | 13 | 38.5% | 1.22 | +2,146 |
| 2012-05 | 16 | 37.5% | 2.40 | +12,137 |
| 2012-06 | 20 | 65.0% | 13.57 | +38,111 |
| 2012-07 | 5 | 80.0% | 29.67 | +5,468 |
| 2012-08 | 13 | 69.2% | 10.38 | +27,094 |
| 2012-09 | 14 | 35.7% | 0.76 | −1,704 |
| 2012-10 | 14 | 21.4% | 0.56 | −7,657 |
| 2012-11 | 17 | 58.8% | 3.82 | +20,736 |
| 2012-12 | 13 | 30.8% | 0.86 | −1,794 |
| 2013-01 | 26 | 57.7% | 10.46 | +70,672 |
| 2013-02 | 11 | 72.7% | 5.34 | +19,137 |
| 2013-03 | 26 | 50.0% | 2.08 | +18,123 |
| 2013-04 | 17 | 64.7% | 2.46 | +16,225 |
| 2013-05 | 30 | 50.0% | 2.18 | +24,070 |
| 2013-06 | 23 | 56.5% | 3.55 | +19,290 |
| 2013-07 | 30 | 50.0% | 3.51 | +44,616 |
| 2013-08 | 41 | 46.3% | 1.38 | +10,667 |
| 2013-09 | 31 | 58.1% | 3.35 | +33,330 |
| 2013-10 | 38 | 42.1% | 1.29 | +8,761 |
| 2013-11 | 38 | 52.6% | 0.95 | −1,520 |
| 2013-12 | 24 | 45.8% | 2.03 | +25,149 |
| 2014-01 | 48 | 31.3% | 1.46 | +14,318 |
| 2014-02 | 32 | 34.4% | 1.17 | +3,693 |
| 2014-03 | 35 | 42.9% | 0.90 | −2,606 |
| 2014-04 | 8 | 37.5% | 0.62 | −2,776 |
| 2014-05 | 17 | 47.1% | 1.19 | +2,015 |
| 2014-06 | 13 | 23.1% | 0.33 | −8,721 |
| 2014-07 | 8 | 50.0% | 1.74 | +2,932 |
| 2014-08 | 17 | 64.7% | 1.40 | +3,510 |
| 2014-09 | 11 | 27.3% | 0.37 | −8,009 |
| 2014-10 | 8 | 12.5% | 0.03 | −12,615 |
| 2014-11 | 5 | 100.0% | inf | +7,395 |
| 2014-12 | 16 | 31.3% | 0.45 | −5,513 |
| 2015-01 | 9 | 33.3% | 0.89 | −1,573 |
| 2015-02 | 11 | 27.3% | 0.50 | −3,698 |
| 2015-03 | 18 | 22.2% | 0.35 | −11,238 |
| 2015-04 | 9 | 22.2% | 0.83 | −902 |
| 2015-05 | 20 | 50.0% | 0.39 | −19,988 |
| 2015-06 | 12 | 16.7% | 0.05 | −8,166 |
| 2015-07 | 7 | 42.9% | 0.43 | −3,775 |
| 2015-08 | 10 | 30.0% | 0.42 | −4,819 |
| 2015-09 | 13 | 30.8% | 0.15 | −13,717 |
| 2015-10 | 8 | 62.5% | 4.00 | +7,181 |
| 2015-11 | 8 | 37.5% | 0.70 | −1,039 |
| 2015-12 | 10 | 40.0% | 0.34 | −6,199 |
| 2016-01 | 7 | 28.6% | 0.93 | −645 |
| 2016-02 | 2 | 0.0% | 0.00 | −1,802 |
| 2016-03 | 20 | 45.0% | 0.81 | −2,487 |
| 2016-04 | 14 | 57.1% | 2.31 | +9,425 |
| 2016-05 | 13 | 46.2% | 2.52 | +22,432 |
| 2016-06 | 25 | 32.0% | 0.84 | −4,543 |
| 2016-07 | 20 | 50.0% | 1.88 | +12,571 |
| 2016-08 | 35 | 31.4% | 0.66 | −7,790 |
| 2016-09 | 24 | 29.2% | 0.67 | −4,959 |
| 2016-10 | 18 | 44.4% | 1.26 | +4,091 |
| 2016-11 | 44 | 52.3% | 1.09 | +3,193 |
| 2016-12 | 21 | 42.9% | 2.25 | +19,783 |
| 2017-01 | 43 | 44.2% | 3.94 | +95,338 |
| 2017-02 | 28 | 35.7% | 1.31 | +5,270 |
| 2017-03 | 22 | 22.7% | 0.56 | −9,070 |
| 2017-04 | 19 | 26.3% | 0.16 | −20,497 |
| 2017-05 | 23 | 43.5% | 1.02 | +302 |
| 2017-06 | 23 | 47.8% | 1.62 | +10,743 |
| 2017-07 | 12 | 50.0% | 1.75 | +6,451 |
| 2017-08 | 11 | 72.7% | 8.63 | +29,346 |
| 2017-09 | 27 | 25.9% | 0.16 | −25,534 |
| 2017-10 | 24 | 25.0% | 2.30 | +26,172 |
| 2017-11 | 26 | 57.7% | 2.06 | +22,583 |
| 2017-12 | 19 | 36.8% | 0.63 | −10,030 |
| 2018-01 | 20 | 25.0% | 0.37 | −17,608 |
| 2018-02 | 11 | 18.2% | 0.39 | −5,593 |
| 2018-03 | 26 | 57.7% | 1.34 | +5,637 |
| 2018-04 | 11 | 36.4% | 1.96 | +10,470 |
| 2018-05 | 34 | 35.3% | 0.78 | −9,876 |
| 2018-06 | 32 | 40.6% | 0.53 | −11,425 |
| 2018-07 | 16 | 31.3% | 0.16 | −17,740 |
| 2018-08 | 27 | 48.1% | 1.39 | +8,880 |
| 2018-09 | 23 | 30.4% | 0.20 | −30,051 |
| 2018-10 | 8 | 25.0% | 0.78 | −2,270 |
| 2018-11 | 3 | 0.0% | 0.00 | −578 |
| 2018-12 | 2 | 0.0% | 0.00 | −2,726 |
| 2019-01 | 9 | 22.2% | 0.14 | −10,316 |
| 2019-02 | 14 | 50.0% | 3.35 | +22,045 |
| 2019-03 | 31 | 25.8% | 0.49 | −23,891 |
| 2019-04 | 15 | 33.3% | 0.18 | −10,189 |
| 2019-05 | 18 | 33.3% | 0.48 | −9,967 |
| 2019-06 | 17 | 41.2% | 1.40 | +5,462 |
| 2019-07 | 11 | 27.3% | 0.11 | −9,614 |
| 2019-08 | 13 | 53.8% | 3.23 | +14,456 |
| 2019-09 | 12 | 50.0% | 1.45 | +4,646 |
| 2019-10 | 16 | 37.5% | 1.80 | +8,334 |
| 2019-11 | 23 | 73.9% | 2.55 | +19,193 |
| 2019-12 | 23 | 56.5% | 2.31 | +33,559 |
| 2020-01 | 36 | 16.7% | 0.36 | −35,806 |
| 2020-02 | 24 | 4.2% | 0.02 | −41,659 |
| 2020-03 | 8 | 37.5% | 0.31 | −14,642 |
| 2020-04 | 13 | 61.5% | 3.84 | +48,262 |
| 2020-05 | 11 | 54.5% | 1.67 | +8,032 |
| 2020-06 | 35 | 25.7% | 0.47 | −34,699 |
| 2020-07 | 32 | 43.8% | 0.45 | −29,767 |
| 2020-08 | 31 | 29.0% | 0.79 | −10,265 |
| 2020-09 | 16 | 31.3% | 0.89 | −2,188 |
| 2020-10 | 20 | 35.0% | 1.45 | +12,260 |
| 2020-11 | 24 | 70.8% | 5.63 | +66,034 |
| 2020-12 | 57 | 50.9% | 3.42 | +115,783 |
| 2021-01 | 38 | 47.4% | 2.19 | +32,768 |
| 2021-02 | 57 | 17.5% | 0.30 | −58,518 |
| 2021-03 | 38 | 34.2% | 1.04 | +1,359 |
| 2021-04 | 20 | 60.0% | 6.59 | +36,395 |
| 2021-05 | 23 | 60.9% | 2.83 | +11,795 |
| 2021-06 | 14 | 7.1% | 0.07 | −22,791 |
| 2021-07 | 23 | 21.7% | 0.65 | −10,394 |
| 2021-08 | 12 | 50.0% | 2.36 | +8,380 |
| 2021-09 | 17 | 23.5% | 0.27 | −18,864 |
| 2021-10 | 8 | 50.0% | 1.65 | +3,901 |
| 2021-11 | 13 | 46.2% | 0.96 | −227 |
| 2021-12 | 7 | 28.6% | 0.08 | −10,548 |
| 2022-01 | 6 | 50.0% | 0.27 | −5,776 |
| 2022-02 | 4 | 50.0% | 1.06 | +139 |
| 2022-03 | 14 | 35.7% | 0.38 | −7,039 |
| 2022-04 | 10 | 20.0% | 0.37 | −6,535 |
| 2022-05 | 13 | 38.5% | 0.91 | −870 |
| 2022-06 | 4 | 50.0% | 0.30 | −3,228 |
| 2022-07 | 5 | 40.0% | 0.08 | −6,875 |
| 2022-08 | 22 | 13.6% | 0.02 | −49,440 |
| 2022-09 | 8 | 37.5% | 0.80 | −2,895 |
| 2022-10 | 6 | 50.0% | 0.46 | −2,548 |
| 2022-11 | 15 | 26.7% | 0.77 | −3,622 |
| 2022-12 | 21 | 38.1% | 0.89 | −3,538 |
| 2023-01 | 15 | 33.3% | 2.16 | +26,228 |
| 2023-02 | 14 | 28.6% | 0.17 | −17,206 |
| 2023-03 | 19 | 31.6% | 1.11 | +2,502 |
| 2023-04 | 12 | 16.7% | 0.06 | −23,565 |
| 2023-05 | 26 | 34.6% | 0.32 | −31,267 |
| 2023-06 | 23 | 17.4% | 0.22 | −22,796 |
| 2023-07 | 26 | 15.4% | 0.04 | −43,036 |
| 2023-08 | 27 | 22.2% | 0.17 | −30,108 |
| 2023-09 | 20 | 20.0% | 0.83 | −6,736 |
| 2023-10 | 11 | 54.5% | 1.07 | +904 |
| 2023-11 | 35 | 48.6% | 1.22 | +12,172 |
| 2023-12 | 39 | 23.1% | 0.74 | −16,335 |
| 2024-01 | 26 | 46.2% | 0.95 | −1,226 |
| 2024-02 | 33 | 30.3% | 0.40 | −26,515 |
| 2024-03 | 36 | 33.3% | 0.54 | −20,392 |
| 2024-04 | 35 | 34.3% | 0.40 | −20,740 |
| 2024-05 | 24 | 16.7% | 0.15 | −29,926 |
| 2024-06 | 25 | 20.0% | 0.19 | −38,349 |
| 2024-07 | 20 | 35.0% | 2.96 | +47,731 |
| 2024-08 | 22 | 36.4% | 1.12 | +4,335 |
| 2024-09 | 17 | 23.5% | 0.29 | −8,421 |
| 2024-10 | 39 | 38.5% | 0.83 | −8,022 |
| 2024-11 | 40 | 37.5% | 1.42 | +8,955 |
| 2024-12 | 32 | 34.4% | 0.45 | −29,580 |
| 2025-01 | 26 | 19.2% | 0.36 | −18,409 |
| 2025-02 | 16 | 12.5% | 0.03 | −34,112 |
| 2025-03 | 15 | 6.7% | 0.26 | −34,628 |
| 2025-04 | 9 | 44.4% | 0.75 | −3,396 |
| 2025-05 | 25 | 52.0% | 3.92 | +42,901 |
| 2025-06 | 31 | 35.5% | 1.09 | +6,127 |
| 2025-07 | 39 | 30.8% | 1.40 | +31,380 |
| 2025-08 | 21 | 38.1% | 0.52 | −17,711 |
| 2025-09 | 36 | 30.6% | 0.55 | −19,055 |
| 2025-10 | 39 | 33.3% | 0.86 | −7,122 |
| 2025-11 | 18 | 27.8% | 0.62 | −11,638 |
| 2025-12 | 17 | 29.4% | 0.58 | −9,713 |
| 2026-01 | 17 | 35.3% | 0.32 | −16,767 |
| 2026-02 | 13 | 61.5% | 1.10 | +690 |
| 2026-03 | 18 | 16.7% | 0.91 | −1,122 |
| 2026-04 | 10 | 40.0% | 0.86 | −2,305 |
| 2026-05 | 9 | 33.3% | 2.06 | +5,897 |

</details>

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

> **Note:** bucketed by **entry date**.

<details>
<summary>Monthly table — $300k-1M bucket, by entry date — click to expand</summary>

| period | trades | win% | PF | net P&L |
| --- | --: | --: | --: | --: |
| 2005-01 | 22 | 63.6% | 3.53 | +19,865 |
| 2005-02 | 45 | 28.9% | 0.66 | −13,050 |
| 2005-03 | 21 | 23.8% | 0.61 | −13,137 |
| 2005-04 | 13 | 46.2% | 2.06 | +16,810 |
| 2005-05 | 14 | 71.4% | 6.96 | +31,202 |
| 2005-06 | 25 | 56.0% | 5.97 | +52,443 |
| 2005-07 | 30 | 50.0% | 3.06 | +37,344 |
| 2005-08 | 29 | 48.3% | 4.03 | +45,806 |
| 2005-09 | 39 | 20.5% | 0.32 | −35,315 |
| 2005-10 | 13 | 30.8% | 0.23 | −14,093 |
| 2005-11 | 23 | 56.5% | 4.10 | +38,062 |
| 2005-12 | 27 | 59.3% | 1.69 | +13,805 |
| 2006-01 | 36 | 44.4% | 1.68 | +17,331 |
| 2006-02 | 17 | 41.2% | 6.25 | +32,600 |
| 2006-03 | 36 | 50.0% | 2.62 | +24,678 |
| 2006-04 | 33 | 21.2% | 0.32 | −16,858 |
| 2006-05 | 30 | 3.3% | 0.11 | −38,188 |
| 2006-06 | 20 | 40.0% | 0.56 | −5,407 |
| 2006-07 | 9 | 44.4% | 2.19 | +8,939 |
| 2006-08 | 10 | 40.0% | 3.86 | +12,989 |
| 2006-09 | 17 | 41.2% | 1.07 | +694 |
| 2006-10 | 17 | 41.2% | 1.39 | +3,711 |
| 2006-11 | 26 | 61.5% | 1.53 | +6,598 |
| 2006-12 | 29 | 44.8% | 0.91 | −2,332 |
| 2007-01 | 20 | 45.0% | 1.02 | +273 |
| 2007-02 | 31 | 29.0% | 1.23 | +4,053 |
| 2007-03 | 21 | 71.4% | 2.90 | +23,420 |
| 2007-04 | 29 | 44.8% | 1.70 | +12,296 |
| 2007-05 | 40 | 52.5% | 1.85 | +17,293 |
| 2007-06 | 18 | 5.6% | 0.02 | −21,773 |
| 2007-07 | 21 | 9.5% | 0.07 | −24,399 |
| 2007-08 | 11 | 45.5% | 1.25 | +4,222 |
| 2007-09 | 17 | 35.3% | 1.48 | +4,619 |
| 2007-10 | 21 | 38.1% | 0.52 | −9,730 |
| 2007-11 | 13 | 7.7% | 0.04 | −15,590 |
| 2007-12 | 9 | 22.2% | 0.04 | −12,475 |
| 2008-01 | 6 | 50.0% | 1.96 | +3,263 |
| 2008-02 | 5 | 60.0% | 2.80 | +1,853 |
| 2008-03 | 3 | 33.3% | 0.77 | −388 |
| 2008-04 | 12 | 58.3% | 1.84 | +3,820 |
| 2008-05 | 15 | 40.0% | 1.76 | +10,389 |
| 2008-06 | 17 | 17.6% | 0.12 | −23,508 |
| 2008-07 | 1 | 0.0% | 0.00 | −656 |
| 2008-08 | 8 | 37.5% | 0.35 | −4,210 |
| 2008-09 | 23 | 0.0% | 0.00 | −52,080 |
| 2008-10 | 1 | 0.0% | 0.00 | −2,829 |
| 2008-12 | 3 | 0.0% | 0.00 | −8,779 |
| 2009-01 | 2 | 0.0% | 0.00 | −10,400 |
| 2009-02 | 3 | 0.0% | 0.00 | −7,258 |
| 2009-03 | 1 | 100.0% | inf | +3,882 |
| 2009-04 | 6 | 50.0% | 1.19 | +712 |
| 2009-05 | 18 | 61.1% | 7.09 | +42,365 |
| 2009-06 | 26 | 50.0% | 1.62 | +11,745 |
| 2009-07 | 8 | 25.0% | 0.76 | −3,241 |
| 2009-08 | 36 | 33.3% | 0.60 | −15,923 |
| 2009-09 | 42 | 26.2% | 0.18 | −50,379 |
| 2009-10 | 43 | 23.3% | 0.32 | −39,677 |
| 2009-11 | 19 | 47.4% | 1.91 | +9,738 |
| 2009-12 | 34 | 50.0% | 0.62 | −9,889 |
| 2010-01 | 26 | 11.5% | 1.22 | +7,639 |
| 2010-02 | 21 | 47.6% | 1.48 | +3,530 |
| 2010-03 | 61 | 50.8% | 1.88 | +25,642 |
| 2010-04 | 64 | 26.6% | 0.43 | −33,117 |
| 2010-05 | 13 | 15.4% | 0.08 | −14,527 |
| 2010-06 | 15 | 26.7% | 0.85 | −2,726 |
| 2010-07 | 9 | 44.4% | 6.70 | +16,853 |
| 2010-08 | 12 | 66.7% | 9.70 | +41,984 |
| 2010-09 | 18 | 50.0% | 5.49 | +34,525 |
| 2010-10 | 36 | 55.6% | 3.92 | +34,027 |
| 2010-11 | 38 | 52.6% | 4.22 | +55,128 |
| 2010-12 | 38 | 50.0% | 1.99 | +17,411 |
| 2011-01 | 51 | 47.1% | 2.67 | +49,493 |
| 2011-02 | 39 | 35.9% | 1.11 | +3,093 |
| 2011-03 | 30 | 20.0% | 0.45 | −24,466 |
| 2011-04 | 23 | 39.1% | 1.20 | +2,782 |
| 2011-05 | 29 | 44.8% | 0.75 | −5,328 |
| 2011-06 | 16 | 25.0% | 0.54 | −3,998 |
| 2011-07 | 17 | 5.9% | 0.00 | −22,675 |
| 2011-08 | 4 | 25.0% | 0.83 | −385 |
| 2011-09 | 2 | 0.0% | 0.00 | −2,491 |
| 2011-10 | 5 | 40.0% | 0.80 | −1,229 |
| 2011-11 | 7 | 28.6% | 0.55 | −2,549 |
| 2011-12 | 6 | 0.0% | 0.00 | −8,825 |
| 2012-01 | 11 | 45.5% | 2.26 | +10,676 |
| 2012-02 | 26 | 65.4% | 9.06 | +41,725 |
| 2012-03 | 27 | 40.7% | 14.80 | +228,249 |
| 2012-04 | 24 | 25.0% | 0.35 | −17,875 |
| 2012-05 | 16 | 25.0% | 0.18 | −13,650 |
| 2012-06 | 18 | 44.4% | 0.52 | −4,819 |
| 2012-07 | 9 | 22.2% | 0.17 | −6,236 |
| 2012-08 | 17 | 64.7% | 3.38 | +17,196 |
| 2012-09 | 20 | 30.0% | 18.85 | +152,265 |
| 2012-10 | 19 | 42.1% | 1.98 | +14,539 |
| 2012-11 | 14 | 50.0% | 1.01 | +107 |
| 2012-12 | 12 | 25.0% | 0.78 | −2,621 |
| 2013-01 | 32 | 59.4% | 4.49 | +35,867 |
| 2013-02 | 30 | 63.3% | 4.59 | +36,423 |
| 2013-03 | 25 | 40.0% | 1.25 | +5,429 |
| 2013-04 | 24 | 50.0% | 7.00 | +45,667 |
| 2013-05 | 39 | 56.4% | 3.12 | +36,373 |
| 2013-06 | 33 | 48.5% | 9.50 | +99,569 |
| 2013-07 | 29 | 41.4% | 1.09 | +1,445 |
| 2013-08 | 53 | 50.9% | 2.22 | +50,584 |
| 2013-09 | 56 | 50.0% | 1.89 | +31,125 |
| 2013-10 | 60 | 41.7% | 1.56 | +29,187 |
| 2013-11 | 52 | 46.2% | 1.78 | +31,835 |
| 2013-12 | 57 | 49.1% | 2.51 | +38,111 |
| 2014-01 | 60 | 45.0% | 1.01 | +589 |
| 2014-02 | 45 | 42.2% | 1.29 | +7,761 |
| 2014-03 | 53 | 22.6% | 0.42 | −35,240 |
| 2014-04 | 9 | 33.3% | 0.17 | −8,380 |
| 2014-05 | 20 | 55.0% | 0.36 | −10,295 |
| 2014-06 | 25 | 32.0% | 0.51 | −13,116 |
| 2014-07 | 16 | 50.0% | 0.40 | −9,769 |
| 2014-08 | 28 | 28.6% | 0.21 | −15,602 |
| 2014-09 | 20 | 45.0% | 5.62 | +42,789 |
| 2014-10 | 17 | 23.5% | 0.26 | −17,993 |
| 2014-11 | 11 | 45.5% | 0.53 | −2,413 |
| 2014-12 | 33 | 39.4% | 0.71 | −8,978 |
| 2015-01 | 12 | 58.3% | 21.67 | +54,012 |
| 2015-02 | 12 | 41.7% | 1.41 | +2,679 |
| 2015-03 | 23 | 52.2% | 2.52 | +11,192 |
| 2015-04 | 35 | 37.1% | 0.53 | −11,968 |
| 2015-05 | 15 | 80.0% | 10.21 | +10,662 |
| 2015-06 | 19 | 15.8% | 0.06 | −29,227 |
| 2015-07 | 18 | 27.8% | 0.12 | −19,123 |
| 2015-08 | 8 | 12.5% | 0.04 | −6,506 |
| 2015-09 | 15 | 13.3% | 0.02 | −21,016 |
| 2015-10 | 11 | 54.5% | 1.32 | +1,606 |
| 2015-11 | 8 | 12.5% | 0.22 | −5,395 |
| 2015-12 | 10 | 10.0% | 0.01 | −12,856 |
| 2016-01 | 7 | 14.3% | 0.00 | −17,844 |
| 2016-02 | 10 | 80.0% | 8.62 | +15,436 |
| 2016-03 | 14 | 57.1% | 0.95 | −502 |
| 2016-04 | 16 | 31.3% | 0.52 | −6,160 |
| 2016-05 | 19 | 47.4% | 1.52 | +6,686 |
| 2016-06 | 11 | 45.5% | 1.40 | +3,571 |
| 2016-07 | 25 | 36.0% | 2.76 | +57,962 |
| 2016-08 | 33 | 36.4% | 1.31 | +11,137 |
| 2016-09 | 33 | 39.4% | 1.23 | +4,850 |
| 2016-10 | 24 | 33.3% | 0.68 | −7,347 |
| 2016-11 | 54 | 75.9% | 3.32 | +73,483 |
| 2016-12 | 32 | 53.1% | 3.83 | +48,962 |
| 2017-01 | 49 | 38.8% | 1.19 | +7,396 |
| 2017-02 | 28 | 53.6% | 1.20 | +4,152 |
| 2017-03 | 53 | 35.8% | 0.89 | −5,071 |
| 2017-04 | 26 | 38.5% | 0.72 | −5,941 |
| 2017-05 | 41 | 51.2% | 2.38 | +28,183 |
| 2017-06 | 26 | 34.6% | 2.12 | +16,705 |
| 2017-07 | 21 | 23.8% | 0.42 | −18,327 |
| 2017-08 | 29 | 58.6% | 5.07 | +88,502 |
| 2017-09 | 46 | 52.2% | 2.47 | +58,148 |
| 2017-10 | 39 | 35.9% | 1.61 | +24,399 |
| 2017-11 | 35 | 57.1% | 2.71 | +36,504 |
| 2017-12 | 19 | 26.3% | 0.23 | −17,504 |
| 2018-01 | 46 | 34.8% | 0.47 | −26,647 |
| 2018-02 | 10 | 60.0% | 1.58 | +5,068 |
| 2018-03 | 32 | 40.6% | 1.08 | +1,876 |
| 2018-04 | 19 | 42.1% | 4.79 | +73,619 |
| 2018-05 | 37 | 48.6% | 1.49 | +13,226 |
| 2018-06 | 39 | 33.3% | 0.44 | −27,975 |
| 2018-07 | 22 | 36.4% | 0.42 | −13,270 |
| 2018-08 | 27 | 44.4% | 2.48 | +25,596 |
| 2018-09 | 35 | 28.6% | 0.24 | −34,765 |
| 2018-10 | 9 | 22.2% | 0.75 | −3,196 |
| 2018-11 | 12 | 41.7% | 0.25 | −7,939 |
| 2018-12 | 5 | 40.0% | 2.17 | +7,141 |
| 2019-01 | 4 | 25.0% | 0.45 | −1,572 |
| 2019-02 | 22 | 50.0% | 4.06 | +49,817 |
| 2019-03 | 23 | 34.8% | 2.27 | +38,049 |
| 2019-04 | 13 | 23.1% | 0.22 | −12,018 |
| 2019-05 | 37 | 29.7% | 0.67 | −15,737 |
| 2019-06 | 15 | 33.3% | 0.22 | −13,003 |
| 2019-07 | 23 | 47.8% | 0.93 | −2,112 |
| 2019-08 | 12 | 58.3% | 1.20 | +1,445 |
| 2019-09 | 15 | 13.3% | 0.29 | −16,800 |
| 2019-10 | 13 | 46.2% | 2.26 | +12,758 |
| 2019-11 | 27 | 48.1% | 2.77 | +20,949 |
| 2019-12 | 31 | 51.6% | 2.56 | +31,259 |
| 2020-01 | 32 | 15.6% | 0.20 | −35,229 |
| 2020-02 | 33 | 21.2% | 0.15 | −43,587 |
| 2020-03 | 2 | 0.0% | 0.00 | −3,582 |
| 2020-04 | 16 | 43.8% | 4.13 | +91,062 |
| 2020-05 | 30 | 50.0% | 2.69 | +64,968 |
| 2020-06 | 54 | 44.4% | 1.03 | +2,251 |
| 2020-07 | 40 | 32.5% | 0.39 | −42,306 |
| 2020-08 | 50 | 24.0% | 0.36 | −48,597 |
| 2020-09 | 25 | 32.0% | 0.94 | −1,967 |
| 2020-10 | 37 | 40.5% | 2.86 | +76,901 |
| 2020-11 | 53 | 62.3% | 9.13 | +188,949 |
| 2020-12 | 71 | 63.4% | 5.38 | +175,950 |
| 2021-01 | 91 | 52.7% | 2.44 | +96,290 |
| 2021-02 | 120 | 25.0% | 0.38 | −117,951 |
| 2021-03 | 65 | 32.3% | 1.09 | +6,228 |
| 2021-04 | 40 | 60.0% | 1.36 | +9,203 |
| 2021-05 | 39 | 38.5% | 1.37 | +11,450 |
| 2021-06 | 46 | 26.1% | 0.21 | −42,881 |
| 2021-07 | 24 | 4.2% | 0.00 | −29,320 |
| 2021-08 | 27 | 40.7% | 1.95 | +17,869 |
| 2021-09 | 31 | 48.4% | 0.65 | −10,089 |
| 2021-10 | 35 | 42.9% | 0.55 | −15,658 |
| 2021-11 | 41 | 24.4% | 0.37 | −26,513 |
| 2021-12 | 15 | 13.3% | 0.06 | −10,900 |
| 2022-01 | 12 | 25.0% | 0.16 | −12,085 |
| 2022-02 | 9 | 11.1% | 0.03 | −9,982 |
| 2022-03 | 24 | 33.3% | 1.24 | +6,683 |
| 2022-04 | 16 | 50.0% | 2.96 | +17,857 |
| 2022-05 | 8 | 12.5% | 0.13 | −8,735 |
| 2022-06 | 13 | 23.1% | 0.21 | −16,209 |
| 2022-07 | 5 | 40.0% | 0.42 | −2,300 |
| 2022-08 | 20 | 0.0% | 0.00 | −35,119 |
| 2022-09 | 6 | 16.7% | 0.00 | −10,071 |
| 2022-10 | 13 | 46.2% | 0.12 | −21,073 |
| 2022-11 | 20 | 55.0% | 1.48 | +6,301 |
| 2022-12 | 27 | 33.3% | 0.30 | −35,129 |
| 2023-01 | 32 | 25.0% | 0.11 | −55,553 |
| 2023-02 | 24 | 45.8% | 1.63 | +15,684 |
| 2023-03 | 25 | 48.0% | 0.94 | −1,668 |
| 2023-04 | 23 | 21.7% | 0.45 | −24,042 |
| 2023-05 | 21 | 57.1% | 1.30 | +5,833 |
| 2023-06 | 38 | 31.6% | 0.33 | −33,993 |
| 2023-07 | 28 | 3.6% | 0.06 | −56,243 |
| 2023-08 | 35 | 34.3% | 0.30 | −29,989 |
| 2023-09 | 29 | 24.1% | 0.65 | −13,415 |
| 2023-10 | 8 | 12.5% | 0.01 | −22,901 |
| 2023-11 | 34 | 55.9% | 1.41 | +13,161 |
| 2023-12 | 43 | 30.2% | 1.25 | +13,988 |
| 2024-01 | 29 | 51.7% | 3.24 | +62,234 |
| 2024-02 | 24 | 37.5% | 1.82 | +19,399 |
| 2024-03 | 59 | 40.7% | 0.87 | −8,412 |
| 2024-04 | 27 | 14.8% | 0.07 | −47,240 |
| 2024-05 | 28 | 42.9% | 0.42 | −21,760 |
| 2024-06 | 27 | 29.6% | 0.12 | −31,439 |
| 2024-07 | 21 | 23.8% | 0.82 | −5,053 |
| 2024-08 | 27 | 66.7% | 2.52 | +55,530 |
| 2024-09 | 23 | 21.7% | 1.11 | +2,719 |
| 2024-10 | 48 | 25.0% | 0.28 | −58,947 |
| 2024-11 | 94 | 45.7% | 4.12 | +92,892 |
| 2024-12 | 49 | 26.5% | 0.49 | −45,539 |
| 2025-01 | 39 | 23.1% | 0.19 | −54,842 |
| 2025-02 | 38 | 15.8% | 0.20 | −50,530 |
| 2025-03 | 14 | 21.4% | 0.03 | −42,530 |
| 2025-04 | 16 | 56.3% | 1.72 | +10,828 |
| 2025-05 | 35 | 42.9% | 2.37 | +53,832 |
| 2025-06 | 41 | 41.5% | 1.07 | +5,041 |
| 2025-07 | 35 | 31.4% | 0.12 | −55,812 |
| 2025-08 | 33 | 36.4% | 0.50 | −19,665 |
| 2025-09 | 48 | 33.3% | 0.54 | −34,634 |
| 2025-10 | 37 | 13.5% | 0.22 | −69,574 |
| 2025-11 | 35 | 60.0% | 2.09 | +26,229 |
| 2025-12 | 25 | 40.0% | 0.65 | −10,061 |
| 2026-01 | 38 | 28.9% | 0.42 | −26,051 |
| 2026-02 | 19 | 47.4% | 0.56 | −6,870 |
| 2026-03 | 30 | 30.0% | 0.66 | −14,185 |
| 2026-04 | 23 | 52.2% | 0.68 | −8,071 |
| 2026-05 | 18 | 22.2% | 0.43 | −6,172 |

</details>

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

> **Note:** bucketed by **entry date**.

<details>
<summary>Monthly table — $1-3M bucket, by entry date — click to expand</summary>

| period | trades | win% | PF | net P&L |
| --- | --: | --: | --: | --: |
| 2005-01 | 19 | 57.9% | 0.66 | −4,800 |
| 2005-02 | 30 | 33.3% | 1.67 | +10,627 |
| 2005-03 | 19 | 15.8% | 0.06 | −19,577 |
| 2005-04 | 9 | 44.4% | 1.19 | +819 |
| 2005-05 | 11 | 72.7% | 3.76 | +17,362 |
| 2005-06 | 25 | 64.0% | 2.16 | +13,896 |
| 2005-07 | 33 | 45.5% | 1.95 | +18,712 |
| 2005-08 | 39 | 28.2% | 0.79 | −4,781 |
| 2005-09 | 29 | 34.5% | 1.24 | +6,066 |
| 2005-10 | 20 | 65.0% | 7.11 | +23,486 |
| 2005-11 | 25 | 68.0% | 3.34 | +21,820 |
| 2005-12 | 18 | 44.4% | 2.04 | +10,361 |
| 2006-01 | 53 | 58.5% | 2.13 | +34,590 |
| 2006-02 | 33 | 45.5% | 1.58 | +12,517 |
| 2006-03 | 44 | 50.0% | 1.67 | +12,570 |
| 2006-04 | 37 | 29.7% | 0.35 | −21,512 |
| 2006-05 | 30 | 6.7% | 0.06 | −31,781 |
| 2006-06 | 14 | 14.3% | 0.29 | −6,822 |
| 2006-07 | 11 | 45.5% | 0.35 | −3,744 |
| 2006-08 | 18 | 61.1% | 2.56 | +7,914 |
| 2006-09 | 14 | 78.6% | 4.56 | +10,415 |
| 2006-10 | 15 | 66.7% | 2.73 | +8,258 |
| 2006-11 | 27 | 66.7% | 1.53 | +10,038 |
| 2006-12 | 26 | 53.8% | 1.74 | +11,869 |
| 2007-01 | 24 | 54.2% | 1.60 | +9,208 |
| 2007-02 | 40 | 35.0% | 0.84 | −3,380 |
| 2007-03 | 15 | 53.3% | 1.77 | +6,968 |
| 2007-04 | 33 | 42.4% | 0.85 | −3,773 |
| 2007-05 | 29 | 41.4% | 2.31 | +16,944 |
| 2007-06 | 21 | 33.3% | 0.22 | −7,729 |
| 2007-07 | 26 | 11.5% | 0.06 | −33,771 |
| 2007-08 | 8 | 12.5% | 0.39 | −5,519 |
| 2007-09 | 13 | 53.8% | 5.76 | +35,501 |
| 2007-10 | 38 | 21.1% | 0.44 | −22,491 |
| 2007-11 | 3 | 0.0% | 0.00 | −2,886 |
| 2007-12 | 9 | 33.3% | 0.37 | −5,158 |
| 2008-01 | 9 | 33.3% | 0.40 | −5,169 |
| 2008-02 | 5 | 20.0% | 1.26 | +1,542 |
| 2008-03 | 6 | 33.3% | 1.25 | +528 |
| 2008-04 | 6 | 50.0% | 1.67 | +3,791 |
| 2008-05 | 12 | 50.0% | 0.58 | −4,270 |
| 2008-06 | 20 | 15.0% | 0.03 | −19,833 |
| 2008-07 | 3 | 0.0% | 0.00 | −5,958 |
| 2008-08 | 3 | 0.0% | 0.00 | −4,818 |
| 2008-09 | 23 | 0.0% | 0.00 | −40,557 |
| 2008-11 | 1 | 0.0% | 0.00 | −878 |
| 2009-02 | 3 | 0.0% | 0.00 | −3,519 |
| 2009-04 | 4 | 25.0% | 0.09 | −4,331 |
| 2009-05 | 24 | 50.0% | 2.62 | +17,572 |
| 2009-06 | 23 | 21.7% | 0.28 | −17,448 |
| 2009-07 | 14 | 35.7% | 1.47 | +7,054 |
| 2009-08 | 14 | 35.7% | 1.78 | +10,435 |
| 2009-09 | 34 | 26.5% | 0.23 | −23,444 |
| 2009-10 | 27 | 29.6% | 1.10 | +2,395 |
| 2009-11 | 24 | 58.3% | 1.45 | +5,732 |
| 2009-12 | 38 | 39.5% | 24.24 | +569,798 |
| 2010-01 | 41 | 22.0% | 0.20 | −34,454 |
| 2010-02 | 25 | 60.0% | 2.56 | +10,386 |
| 2010-03 | 41 | 53.7% | 2.00 | +36,117 |
| 2010-04 | 47 | 17.0% | 0.25 | −39,577 |
| 2010-05 | 11 | 18.2% | 0.38 | −5,986 |
| 2010-06 | 9 | 11.1% | 0.57 | −7,681 |
| 2010-07 | 10 | 30.0% | 0.50 | −4,390 |
| 2010-08 | 28 | 57.1% | 4.05 | +46,071 |
| 2010-09 | 28 | 67.9% | 4.69 | +37,001 |
| 2010-10 | 27 | 59.3% | 2.34 | +17,357 |
| 2010-11 | 45 | 60.0% | 3.00 | +48,569 |
| 2010-12 | 37 | 37.8% | 1.62 | +13,119 |
| 2011-01 | 45 | 33.3% | 1.12 | +3,860 |
| 2011-02 | 51 | 51.0% | 2.54 | +28,451 |
| 2011-03 | 35 | 37.1% | 0.51 | −13,287 |
| 2011-04 | 39 | 25.6% | 0.50 | −15,104 |
| 2011-05 | 32 | 28.1% | 0.41 | −15,941 |
| 2011-06 | 14 | 50.0% | 1.11 | +1,088 |
| 2011-07 | 23 | 0.0% | 0.00 | −33,077 |
| 2011-08 | 1 | 0.0% | 0.00 | −1,964 |
| 2011-09 | 3 | 0.0% | 0.00 | −8,163 |
| 2011-10 | 8 | 37.5% | 1.14 | +554 |
| 2011-11 | 9 | 55.6% | 0.48 | −3,844 |
| 2011-12 | 5 | 60.0% | 29.68 | +38,372 |
| 2012-01 | 11 | 45.5% | 6.09 | +14,810 |
| 2012-02 | 23 | 47.8% | 1.86 | +8,893 |
| 2012-03 | 21 | 28.6% | 0.25 | −17,552 |
| 2012-04 | 13 | 7.7% | 0.14 | −11,896 |
| 2012-05 | 9 | 44.4% | 1.73 | +4,650 |
| 2012-06 | 8 | 62.5% | 1.62 | +1,416 |
| 2012-07 | 15 | 60.0% | 2.45 | +8,409 |
| 2012-08 | 16 | 81.3% | 9.36 | +30,353 |
| 2012-09 | 21 | 42.9% | 0.40 | −9,563 |
| 2012-10 | 18 | 44.4% | 1.38 | +2,158 |
| 2012-11 | 14 | 57.1% | 1.87 | +6,595 |
| 2012-12 | 10 | 40.0% | 1.07 | +353 |
| 2013-01 | 35 | 62.9% | 1.88 | +10,991 |
| 2013-02 | 36 | 58.3% | 1.48 | +6,657 |
| 2013-03 | 25 | 48.0% | 2.20 | +12,227 |
| 2013-04 | 29 | 65.5% | 8.31 | +66,819 |
| 2013-05 | 33 | 69.7% | 8.64 | +65,287 |
| 2013-06 | 27 | 44.4% | 9.77 | +141,930 |
| 2013-07 | 28 | 57.1% | 1.66 | +11,019 |
| 2013-08 | 51 | 58.8% | 2.58 | +46,067 |
| 2013-09 | 47 | 38.3% | 1.41 | +15,629 |
| 2013-10 | 50 | 44.0% | 1.05 | +1,994 |
| 2013-11 | 36 | 58.3% | 2.15 | +16,336 |
| 2013-12 | 38 | 55.3% | 3.20 | +46,304 |
| 2014-01 | 46 | 30.4% | 0.84 | −6,604 |
| 2014-02 | 47 | 40.4% | 0.90 | −2,192 |
| 2014-03 | 48 | 22.9% | 0.11 | −54,996 |
| 2014-04 | 15 | 53.3% | 3.54 | +16,270 |
| 2014-05 | 16 | 50.0% | 2.15 | +5,298 |
| 2014-06 | 22 | 27.3% | 0.97 | −533 |
| 2014-07 | 15 | 20.0% | 0.07 | −14,970 |
| 2014-08 | 8 | 25.0% | 0.50 | −2,314 |
| 2014-09 | 18 | 11.1% | 1.13 | +3,747 |
| 2014-10 | 22 | 54.5% | 3.35 | +45,918 |
| 2014-11 | 17 | 41.2% | 2.02 | +5,258 |
| 2014-12 | 20 | 40.0% | 0.77 | −3,009 |
| 2015-01 | 17 | 47.1% | 2.91 | +22,581 |
| 2015-02 | 28 | 64.3% | 10.76 | +62,299 |
| 2015-03 | 32 | 37.5% | 0.62 | −8,246 |
| 2015-04 | 39 | 51.3% | 1.82 | +23,535 |
| 2015-05 | 24 | 37.5% | 0.39 | −14,983 |
| 2015-06 | 19 | 42.1% | 0.30 | −15,690 |
| 2015-07 | 12 | 16.7% | 0.30 | −6,758 |
| 2015-08 | 13 | 23.1% | 0.34 | −6,855 |
| 2015-09 | 8 | 50.0% | 1.11 | +770 |
| 2015-10 | 14 | 14.3% | 0.02 | −14,372 |
| 2015-11 | 16 | 37.5% | 0.20 | −7,205 |
| 2015-12 | 14 | 7.1% | 0.00 | −25,087 |
| 2016-01 | 7 | 14.3% | 0.00 | −10,724 |
| 2016-02 | 2 | 50.0% | 0.09 | −6,931 |
| 2016-03 | 4 | 25.0% | 0.67 | −585 |
| 2016-04 | 9 | 33.3% | 0.05 | −15,572 |
| 2016-05 | 15 | 46.7% | 1.51 | +7,907 |
| 2016-06 | 14 | 50.0% | 2.07 | +10,027 |
| 2016-07 | 26 | 53.8% | 3.75 | +25,825 |
| 2016-08 | 51 | 39.2% | 0.62 | −13,705 |
| 2016-09 | 24 | 37.5% | 1.80 | +8,281 |
| 2016-10 | 27 | 40.7% | 0.72 | −5,086 |
| 2016-11 | 53 | 69.8% | 1.60 | +30,884 |
| 2016-12 | 17 | 29.4% | 1.49 | +5,882 |
| 2017-01 | 29 | 34.5% | 0.79 | −3,463 |
| 2017-02 | 58 | 46.6% | 0.65 | −9,803 |
| 2017-03 | 37 | 45.9% | 1.35 | +8,386 |
| 2017-04 | 29 | 24.1% | 0.39 | −13,335 |
| 2017-05 | 36 | 69.4% | 2.21 | +17,242 |
| 2017-06 | 24 | 16.7% | 0.26 | −14,171 |
| 2017-07 | 16 | 25.0% | 0.46 | −7,787 |
| 2017-08 | 28 | 78.6% | 12.92 | +84,727 |
| 2017-09 | 31 | 64.5% | 4.46 | +32,601 |
| 2017-10 | 39 | 35.9% | 1.59 | +22,859 |
| 2017-11 | 47 | 48.9% | 1.73 | +20,473 |
| 2017-12 | 23 | 43.5% | 1.28 | +3,641 |
| 2018-01 | 20 | 35.0% | 0.62 | −6,976 |
| 2018-02 | 26 | 53.8% | 1.93 | +9,751 |
| 2018-03 | 23 | 39.1% | 0.87 | −3,719 |
| 2018-04 | 15 | 60.0% | 7.32 | +109,028 |
| 2018-05 | 53 | 56.6% | 2.25 | +40,630 |
| 2018-06 | 45 | 42.2% | 0.73 | −11,566 |
| 2018-07 | 25 | 44.0% | 0.42 | −12,809 |
| 2018-08 | 25 | 36.0% | 0.44 | −10,141 |
| 2018-09 | 29 | 20.7% | 0.13 | −27,357 |
| 2018-10 | 17 | 17.6% | 0.10 | −18,424 |
| 2018-11 | 17 | 17.6% | 0.06 | −21,369 |
| 2018-12 | 5 | 20.0% | 0.10 | −4,943 |
| 2019-01 | 8 | 50.0% | 1.93 | +2,306 |
| 2019-02 | 23 | 39.1% | 1.80 | +5,547 |
| 2019-03 | 25 | 68.0% | 1.26 | +4,010 |
| 2019-04 | 22 | 27.3% | 0.09 | −38,829 |
| 2019-05 | 25 | 28.0% | 0.40 | −16,581 |
| 2019-06 | 15 | 40.0% | 0.20 | −11,038 |
| 2019-07 | 9 | 11.1% | 0.03 | −16,175 |
| 2019-08 | 13 | 53.8% | 1.54 | +3,051 |
| 2019-09 | 9 | 11.1% | 0.29 | −8,211 |
| 2019-10 | 13 | 46.2% | 2.81 | +15,214 |
| 2019-11 | 24 | 41.7% | 0.84 | −3,587 |
| 2019-12 | 32 | 43.8% | 2.57 | +40,117 |
| 2020-01 | 26 | 23.1% | 0.75 | −9,197 |
| 2020-02 | 36 | 0.0% | 0.00 | −75,606 |
| 2020-03 | 10 | 0.0% | 0.00 | −28,069 |
| 2020-04 | 6 | 33.3% | 0.75 | −1,693 |
| 2020-05 | 25 | 56.0% | 7.23 | +106,691 |
| 2020-06 | 42 | 31.0% | 1.06 | +3,473 |
| 2020-07 | 45 | 31.1% | 0.41 | −39,277 |
| 2020-08 | 47 | 14.9% | 0.41 | −40,157 |
| 2020-09 | 24 | 58.3% | 2.05 | +18,209 |
| 2020-10 | 26 | 42.3% | 2.71 | +53,384 |
| 2020-11 | 55 | 67.3% | 13.19 | +431,527 |
| 2020-12 | 111 | 50.5% | 3.13 | +198,690 |
| 2021-01 | 140 | 47.9% | 2.79 | +211,251 |
| 2021-02 | 155 | 24.5% | 0.71 | −78,595 |
| 2021-03 | 70 | 17.1% | 0.40 | −60,149 |
| 2021-04 | 28 | 10.7% | 0.05 | −41,012 |
| 2021-05 | 44 | 31.8% | 0.67 | −8,317 |
| 2021-06 | 43 | 18.6% | 0.14 | −48,618 |
| 2021-07 | 30 | 33.3% | 1.70 | +24,534 |
| 2021-08 | 30 | 53.3% | 2.91 | +49,745 |
| 2021-09 | 28 | 25.0% | 0.37 | −28,885 |
| 2021-10 | 32 | 40.6% | 0.93 | −1,604 |
| 2021-11 | 44 | 18.2% | 0.04 | −73,606 |
| 2021-12 | 14 | 28.6% | 0.14 | −9,674 |
| 2022-01 | 14 | 14.3% | 0.08 | −13,187 |
| 2022-02 | 6 | 66.7% | 2.49 | +2,909 |
| 2022-03 | 26 | 23.1% | 0.38 | −27,307 |
| 2022-04 | 14 | 7.1% | 0.17 | −13,579 |
| 2022-05 | 6 | 16.7% | 0.03 | −6,314 |
| 2022-06 | 11 | 9.1% | 0.02 | −14,146 |
| 2022-07 | 3 | 33.3% | 1.68 | +2,588 |
| 2022-08 | 16 | 31.3% | 0.24 | −24,003 |
| 2022-09 | 7 | 14.3% | 0.15 | −21,778 |
| 2022-10 | 12 | 41.7% | 0.98 | −233 |
| 2022-11 | 10 | 20.0% | 0.02 | −18,806 |
| 2022-12 | 16 | 25.0% | 0.58 | −10,849 |
| 2023-01 | 17 | 23.5% | 0.16 | −26,164 |
| 2023-02 | 21 | 23.8% | 0.23 | −20,139 |
| 2023-03 | 20 | 30.0% | 0.66 | −4,968 |
| 2023-04 | 13 | 15.4% | 0.26 | −19,237 |
| 2023-05 | 32 | 43.8% | 0.51 | −18,644 |
| 2023-06 | 20 | 40.0% | 1.11 | +1,542 |
| 2023-07 | 15 | 13.3% | 0.02 | −26,845 |
| 2023-08 | 33 | 9.1% | 0.05 | −56,580 |
| 2023-09 | 19 | 15.8% | 0.04 | −20,912 |
| 2023-10 | 5 | 20.0% | 0.00 | −7,655 |
| 2023-11 | 26 | 57.7% | 0.91 | −2,606 |
| 2023-12 | 33 | 36.4% | 1.06 | +2,881 |
| 2024-01 | 24 | 37.5% | 1.94 | +18,566 |
| 2024-02 | 41 | 48.8% | 0.92 | −2,225 |
| 2024-03 | 48 | 27.1% | 0.50 | −26,397 |
| 2024-04 | 24 | 33.3% | 0.86 | −4,292 |
| 2024-05 | 22 | 22.7% | 0.10 | −18,726 |
| 2024-06 | 12 | 41.7% | 0.42 | −5,980 |
| 2024-07 | 18 | 16.7% | 0.10 | −26,281 |
| 2024-08 | 23 | 65.2% | 1.59 | +14,471 |
| 2024-09 | 27 | 48.1% | 1.66 | +22,914 |
| 2024-10 | 31 | 38.7% | 1.82 | +25,229 |
| 2024-11 | 102 | 33.3% | 0.56 | −34,160 |
| 2024-12 | 40 | 22.5% | 0.49 | −44,637 |
| 2025-01 | 28 | 17.9% | 0.05 | −34,453 |
| 2025-02 | 32 | 12.5% | 0.13 | −47,382 |
| 2025-03 | 20 | 5.0% | 0.01 | −45,448 |
| 2025-04 | 9 | 55.6% | 9.95 | +100,387 |
| 2025-05 | 29 | 48.3% | 0.82 | −6,439 |
| 2025-06 | 31 | 51.6% | 1.95 | +26,310 |
| 2025-07 | 26 | 38.5% | 0.43 | −30,503 |
| 2025-08 | 32 | 59.4% | 1.73 | +19,057 |
| 2025-09 | 37 | 40.5% | 1.85 | +39,319 |
| 2025-10 | 42 | 14.3% | 0.39 | −49,356 |
| 2025-11 | 30 | 43.3% | 0.47 | −24,466 |
| 2025-12 | 25 | 32.0% | 0.50 | −12,147 |
| 2026-01 | 36 | 36.1% | 0.65 | −15,259 |
| 2026-02 | 22 | 18.2% | 0.23 | −21,715 |
| 2026-03 | 29 | 41.4% | 0.97 | −1,015 |
| 2026-04 | 30 | 50.0% | 3.49 | +64,087 |
| 2026-05 | 27 | 40.7% | 0.77 | −2,745 |

</details>

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

> **Note:** bucketed by **entry date**; this is the *old* V1-exit default (superseded 2026-06-17 — see below).

<details>
<summary>Full per-month table — old V1-exit default, by entry date — click to expand</summary>

| month | trades | win% | PF | net P&L |
| ----- | -----: | ---: | ---: | -------: |
| 2005-01 | 2 | 50.0% | 22.68 | +742 |
| 2005-02 | 38 | 52.6% | 1.52 | +9,320 |
| 2005-03 | 11 | 36.4% | 0.74 | −1,585 |
| 2005-05 | 19 | 78.9% | 1.52 | +4,778 |
| 2005-06 | 45 | 77.8% | 6.03 | +25,194 |
| 2005-07 | 53 | 52.8% | 1.29 | +6,736 |
| 2005-08 | 23 | 39.1% | 0.29 | −8,018 |
| 2005-09 | 22 | 45.5% | 0.94 | −574 |
| 2005-10 | 4 | 75.0% | 1.14 | +341 |
| 2005-11 | 31 | 71.0% | 5.14 | +31,348 |
| 2005-12 | 21 | 71.4% | 5.93 | +12,487 |
| 2006-01 | 50 | 58.0% | 1.95 | +16,965 |
| 2006-02 | 29 | 62.1% | 3.43 | +8,799 |
| 2006-03 | 30 | 56.7% | 2.21 | +9,901 |
| 2006-04 | 37 | 29.7% | 0.28 | −12,922 |
| 2006-05 | 27 | 29.6% | 0.30 | −13,252 |
| 2006-06 | 13 | 23.1% | 0.16 | −5,891 |
| 2006-07 | 9 | 88.9% | 9.33 | +6,842 |
| 2006-08 | 17 | 58.8% | 1.18 | +977 |
| 2006-09 | 25 | 64.0% | 2.57 | +7,462 |
| 2006-10 | 44 | 59.1% | 3.16 | +20,432 |
| 2006-11 | 34 | 70.6% | 5.64 | +23,402 |
| 2006-12 | 22 | 45.5% | 1.09 | +677 |
| 2007-01 | 16 | 56.2% | 1.88 | +2,937 |
| 2007-02 | 60 | 50.0% | 2.42 | +35,328 |
| 2007-03 | 10 | 70.0% | 2.96 | +3,485 |
| 2007-04 | 51 | 52.9% | 1.54 | +10,553 |
| 2007-05 | 44 | 50.0% | 1.43 | +6,284 |
| 2007-06 | 11 | 36.4% | 0.89 | −253 |
| 2007-07 | 19 | 5.3% | 0.02 | −26,973 |
| 2007-08 | 2 | 100.0% | inf | +777 |
| 2007-09 | 16 | 75.0% | 8.04 | +52,140 |
| 2007-10 | 17 | 23.5% | 0.31 | −11,373 |
| 2007-11 | 2 | 0.0% | 0.00 | −902 |
| 2007-12 | 6 | 50.0% | 0.43 | −3,322 |
| 2008-02 | 11 | 54.5% | 0.83 | −1,497 |
| 2008-03 | 1 | 0.0% | 0.00 | −880 |
| 2008-04 | 8 | 87.5% | 36.50 | +8,438 |
| 2008-05 | 27 | 70.4% | 5.79 | +16,518 |
| 2008-06 | 3 | 66.7% | 0.29 | −701 |
| 2008-07 | 5 | 40.0% | 0.38 | −1,208 |
| 2008-08 | 12 | 41.7% | 0.50 | −3,697 |
| 2008-09 | 2 | 0.0% | 0.00 | −3,028 |
| 2008-11 | 1 | 0.0% | 0.00 | −220 |
| 2008-12 | 3 | 66.7% | 2.82 | +743 |
| 2009-01 | 1 | 0.0% | 0.00 | −101 |
| 2009-04 | 3 | 66.7% | 1.77 | +480 |
| 2009-05 | 7 | 85.7% | 22.25 | +9,735 |
| 2009-06 | 5 | 20.0% | 0.24 | −4,040 |
| 2009-07 | 5 | 40.0% | 0.99 | −187 |
| 2009-08 | 18 | 44.4% | 3.07 | +16,340 |
| 2009-09 | 14 | 57.1% | 1.43 | +2,537 |
| 2009-10 | 18 | 50.0% | 0.68 | −3,174 |
| 2009-11 | 11 | 72.7% | 3.91 | +9,230 |
| 2009-12 | 28 | 64.3% | 2.60 | +9,872 |
| 2010-01 | 36 | 38.9% | 0.32 | −18,344 |
| 2010-02 | 12 | 75.0% | 7.76 | +8,408 |
| 2010-03 | 24 | 87.5% | 6.66 | +33,524 |
| 2010-04 | 35 | 34.3% | 0.48 | −16,169 |
| 2010-05 | 4 | 25.0% | 0.28 | −2,178 |
| 2010-07 | 7 | 28.6% | 0.68 | −1,799 |
| 2010-08 | 11 | 63.6% | 6.17 | +10,734 |
| 2010-09 | 20 | 85.0% | 18.36 | +27,706 |
| 2010-10 | 43 | 69.8% | 6.29 | +25,536 |
| 2010-11 | 41 | 63.4% | 4.90 | +39,409 |
| 2010-12 | 27 | 33.3% | 0.60 | −8,410 |
| 2011-01 | 32 | 56.2% | 1.83 | +9,645 |
| 2011-02 | 53 | 47.2% | 1.00 | −73 |
| 2011-03 | 10 | 60.0% | 3.38 | +2,326 |
| 2011-04 | 31 | 48.4% | 1.46 | +4,910 |
| 2011-05 | 13 | 46.2% | 1.13 | +946 |
| 2011-06 | 1 | 0.0% | 0.00 | −1,163 |
| 2011-07 | 11 | 18.2% | 0.05 | −7,172 |
| 2011-09 | 4 | 75.0% | 6.23 | +2,307 |
| 2011-10 | 6 | 50.0% | 5.25 | +3,931 |
| 2011-11 | 10 | 80.0% | 2.16 | +3,282 |
| 2011-12 | 8 | 75.0% | 7.87 | +9,379 |
| 2012-01 | 15 | 80.0% | 7.20 | +9,434 |
| 2012-02 | 31 | 58.1% | 1.82 | +6,811 |
| 2012-03 | 14 | 35.7% | 0.85 | −899 |
| 2012-04 | 9 | 11.1% | 0.30 | −3,541 |
| 2012-05 | 8 | 25.0% | 0.11 | −3,466 |
| 2012-06 | 20 | 60.0% | 4.30 | +13,903 |
| 2012-07 | 7 | 71.4% | 2.76 | +1,975 |
| 2012-08 | 24 | 70.8% | 5.62 | +17,928 |
| 2012-09 | 18 | 38.9% | 0.78 | −1,306 |
| 2012-10 | 3 | 66.7% | 3.18 | +752 |
| 2012-11 | 6 | 16.7% | 0.05 | −2,850 |
| 2012-12 | 11 | 72.7% | 3.74 | +4,592 |
| 2013-01 | 26 | 69.2% | 1.96 | +9,577 |
| 2013-02 | 43 | 72.1% | 5.79 | +45,259 |
| 2013-03 | 27 | 44.4% | 1.82 | +8,628 |
| 2013-04 | 22 | 72.7% | 6.83 | +18,176 |
| 2013-05 | 32 | 62.5% | 3.75 | +22,824 |
| 2013-07 | 35 | 54.3% | 2.10 | +12,237 |
| 2013-08 | 26 | 53.8% | 1.35 | +3,492 |
| 2013-09 | 24 | 54.2% | 1.26 | +4,138 |
| 2013-10 | 48 | 60.4% | 1.36 | +8,129 |
| 2013-11 | 30 | 60.0% | 2.55 | +11,740 |
| 2013-12 | 29 | 58.6% | 2.09 | +7,043 |
| 2014-01 | 33 | 45.5% | 0.52 | −8,252 |
| 2014-02 | 34 | 52.9% | 1.54 | +8,882 |
| 2014-03 | 30 | 43.3% | 0.39 | −12,478 |
| 2014-04 | 5 | 60.0% | 1.60 | +843 |
| 2014-05 | 14 | 71.4% | 6.40 | +9,672 |
| 2014-06 | 38 | 42.1% | 0.59 | −8,755 |
| 2014-07 | 5 | 60.0% | 3.90 | +3,795 |
| 2014-08 | 12 | 25.0% | 0.35 | −2,702 |
| 2014-09 | 10 | 40.0% | 1.08 | +487 |
| 2014-10 | 13 | 84.6% | 37.38 | +10,411 |
| 2014-11 | 21 | 57.1% | 2.83 | +8,087 |
| 2014-12 | 12 | 50.0% | 1.70 | +2,632 |
| 2015-01 | 11 | 63.6% | 8.47 | +8,752 |
| 2015-02 | 46 | 56.5% | 2.26 | +17,856 |
| 2015-03 | 18 | 33.3% | 0.30 | −4,516 |
| 2015-04 | 43 | 51.2% | 1.62 | +8,548 |
| 2015-05 | 11 | 72.7% | 1.95 | +3,179 |
| 2015-06 | 26 | 38.5% | 0.61 | −4,570 |
| 2015-07 | 2 | 50.0% | 0.06 | −1,546 |
| 2015-08 | 1 | 0.0% | 0.00 | −18 |
| 2015-09 | 4 | 25.0% | 0.74 | −351 |
| 2015-10 | 20 | 75.0% | 8.40 | +18,866 |
| 2015-11 | 23 | 52.2% | 1.84 | +4,994 |
| 2015-12 | 5 | 60.0% | 0.37 | −3,396 |
| 2016-02 | 5 | 60.0% | 10.94 | +2,622 |
| 2016-03 | 14 | 64.3% | 2.37 | +6,019 |
| 2016-04 | 19 | 68.4% | 7.60 | +8,790 |
| 2016-05 | 8 | 37.5% | 0.61 | −2,484 |
| 2016-06 | 8 | 50.0% | 1.32 | +1,466 |
| 2016-07 | 22 | 72.7% | 5.05 | +11,331 |
| 2016-08 | 58 | 65.5% | 2.29 | +13,569 |
| 2016-09 | 18 | 50.0% | 1.22 | +1,697 |
| 2016-10 | 11 | 18.2% | 0.29 | −4,493 |
| 2016-11 | 21 | 71.4% | 2.18 | +7,195 |
| 2016-12 | 32 | 43.8% | 1.26 | +2,850 |
| 2017-01 | 31 | 41.9% | 1.00 | −64 |
| 2017-02 | 57 | 57.9% | 1.50 | +8,872 |
| 2017-03 | 23 | 52.2% | 1.28 | +2,050 |
| 2017-04 | 27 | 70.4% | 2.97 | +15,283 |
| 2017-05 | 30 | 73.3% | 6.35 | +15,644 |
| 2017-06 | 25 | 60.0% | 2.00 | +5,599 |
| 2017-07 | 27 | 29.6% | 0.48 | −7,657 |
| 2017-08 | 9 | 77.8% | 2.17 | +1,962 |
| 2017-09 | 31 | 61.3% | 3.49 | +24,916 |
| 2017-10 | 35 | 37.1% | 0.98 | −519 |
| 2017-11 | 17 | 58.8% | 1.98 | +3,917 |
| 2017-12 | 27 | 55.6% | 3.65 | +13,184 |
| 2018-01 | 30 | 33.3% | 0.56 | −11,718 |
| 2018-02 | 1 | 100.0% | inf | +2,021 |
| 2018-03 | 38 | 52.6% | 1.82 | +22,242 |
| 2018-04 | 18 | 61.1% | 6.73 | +12,391 |
| 2018-05 | 48 | 66.7% | 2.50 | +20,438 |
| 2018-06 | 48 | 50.0% | 1.19 | +4,239 |
| 2018-07 | 29 | 58.6% | 2.71 | +9,267 |
| 2018-08 | 42 | 61.9% | 2.51 | +16,599 |
| 2018-09 | 10 | 30.0% | 0.39 | −3,569 |
| 2018-11 | 18 | 50.0% | 0.99 | −100 |
| 2018-12 | 5 | 0.0% | 0.00 | −6,057 |
| 2019-01 | 8 | 75.0% | 12.80 | +7,516 |
| 2019-02 | 38 | 57.9% | 3.35 | +32,226 |
| 2019-03 | 7 | 42.9% | 0.55 | −2,377 |
| 2019-04 | 23 | 60.9% | 1.46 | +5,393 |
| 2019-05 | 9 | 33.3% | 0.45 | −2,396 |
| 2019-06 | 33 | 33.3% | 0.72 | −4,914 |
| 2019-07 | 26 | 46.2% | 0.57 | −4,752 |
| 2019-09 | 20 | 30.0% | 0.29 | −15,202 |
| 2019-10 | 16 | 43.8% | 0.59 | −2,314 |
| 2019-11 | 37 | 73.0% | 6.47 | +31,702 |
| 2019-12 | 30 | 53.3% | 1.35 | +5,088 |
| 2020-01 | 19 | 42.1% | 2.38 | +6,494 |
| 2020-02 | 26 | 7.7% | 0.03 | −72,860 |
| 2020-04 | 8 | 62.5% | 7.92 | +27,014 |
| 2020-05 | 20 | 75.0% | 7.41 | +18,004 |
| 2020-06 | 12 | 50.0% | 2.04 | +9,242 |
| 2020-07 | 21 | 61.9% | 3.08 | +18,937 |
| 2020-08 | 30 | 53.3% | 0.95 | −1,086 |
| 2020-10 | 24 | 50.0% | 0.93 | −1,064 |
| 2020-11 | 36 | 66.7% | 4.42 | +73,800 |
| 2020-12 | 94 | 64.9% | 6.19 | +206,825 |
| 2021-01 | 73 | 53.4% | 12.61 | +544,870 |
| 2021-02 | 84 | 28.6% | 0.30 | −96,374 |
| 2021-03 | 24 | 33.3% | 0.60 | −8,026 |
| 2021-04 | 30 | 36.7% | 0.43 | −10,387 |
| 2021-05 | 14 | 71.4% | 6.83 | +9,267 |
| 2021-06 | 40 | 25.0% | 0.41 | −19,287 |
| 2021-07 | 3 | 33.3% | 2.63 | +511 |
| 2021-08 | 28 | 57.1% | 3.00 | +14,349 |
| 2021-09 | 13 | 30.8% | 0.17 | −8,613 |
| 2021-10 | 18 | 50.0% | 1.43 | +5,184 |
| 2021-11 | 39 | 17.9% | 0.16 | −38,228 |
| 2021-12 | 2 | 0.0% | 0.00 | −6,209 |
| 2022-01 | 1 | 100.0% | inf | +1,181 |
| 2022-02 | 4 | 75.0% | 1.90 | +1,571 |
| 2022-03 | 8 | 50.0% | 3.47 | +12,209 |
| 2022-04 | 7 | 14.3% | 0.04 | −2,812 |
| 2022-05 | 1 | 0.0% | 0.00 | −1,262 |
| 2022-06 | 3 | 0.0% | 0.00 | −3,239 |
| 2022-07 | 5 | 60.0% | 4.26 | +1,789 |
| 2022-08 | 20 | 55.0% | 1.89 | +11,270 |
| 2022-09 | 1 | 0.0% | 0.00 | −887 |
| 2022-10 | 4 | 75.0% | 114.32 | +1,423 |
| 2022-11 | 11 | 45.5% | 4.34 | +10,178 |
| 2022-12 | 6 | 33.3% | 0.27 | −2,928 |
| 2023-01 | 9 | 44.4% | 1.28 | +1,113 |
| 2023-02 | 13 | 46.2% | 0.53 | −5,421 |
| 2023-03 | 1 | 0.0% | 0.00 | −991 |
| 2023-04 | 10 | 50.0% | 0.37 | −4,181 |
| 2023-05 | 10 | 40.0% | 1.11 | +421 |
| 2023-06 | 24 | 79.2% | 3.33 | +16,526 |
| 2023-07 | 21 | 23.8% | 0.47 | −7,112 |
| 2023-08 | 8 | 62.5% | 4.49 | +2,875 |
| 2023-09 | 4 | 25.0% | 0.05 | −1,138 |
| 2023-11 | 19 | 68.4% | 3.51 | +10,222 |
| 2023-12 | 33 | 51.5% | 1.24 | +4,836 |
| 2024-01 | 10 | 50.0% | 1.95 | +3,994 |
| 2024-02 | 40 | 52.5% | 0.87 | −2,518 |
| 2024-03 | 37 | 59.5% | 1.79 | +12,803 |
| 2024-04 | 6 | 66.7% | 18.50 | +8,724 |
| 2024-05 | 18 | 77.8% | 4.16 | +10,898 |
| 2024-07 | 12 | 58.3% | 5.42 | +18,624 |
| 2024-08 | 9 | 55.6% | 5.09 | +4,498 |
| 2024-09 | 16 | 50.0% | 2.68 | +6,209 |
| 2024-10 | 17 | 47.1% | 1.04 | +208 |
| 2024-11 | 41 | 48.8% | 1.59 | +4,742 |
| 2024-12 | 14 | 42.9% | 0.72 | −2,412 |
| 2025-01 | 14 | 28.6% | 0.23 | −11,061 |
| 2025-02 | 18 | 5.6% | 0.01 | −31,253 |
| 2025-04 | 3 | 100.0% | inf | +5,197 |
| 2025-05 | 29 | 48.3% | 1.92 | +13,998 |
| 2025-06 | 28 | 46.4% | 1.03 | +557 |
| 2025-07 | 17 | 47.1% | 0.46 | −9,680 |
| 2025-08 | 22 | 81.8% | 3.53 | +23,529 |
| 2025-09 | 36 | 55.6% | 1.97 | +24,076 |
| 2025-10 | 13 | 30.8% | 0.24 | −8,719 |
| 2025-11 | 3 | 33.3% | 0.56 | −1,059 |
| 2025-12 | 24 | 54.2% | 2.03 | +10,440 |
| 2026-01 | 25 | 60.0% | 3.61 | +23,038 |
| 2026-02 | 20 | 45.0% | 0.54 | −5,683 |
| 2026-03 | 2 | 0.0% | 0.00 | −942 |
| 2026-04 | 16 | 75.0% | 3.51 | +12,318 |
| 2026-05 | 20 | 50.0% | 0.93 | −428 |

</details>
>
> **⚠️ SUPERSEDED 2026-06-17 — the default exit is now Qulla day-low stop + N=1 trailing limit + 0.70 expansion, NO time stop** (see [Default system locked](#default-system-locked-qulla--n1-no-time-stop-2026-06-17) below). The min-bucket half-Kelly *sizing* is unchanged in spirit, but the half-Kelly fractions were **recomputed on the new exit's realized returns** to **0.113 / 0.153 / 0.166** (bucket PFs 2.07 / 2.75 / 3.18 — higher than the old 0.074/0.108/0.176 because the better exit lifts every bucket). The new system's full yearly + monthly Kelly-sized breakdown — **68.1% of months profitable (169/248), 21/22 years positive, total +$1,799,216** — is in the [Qulla N=1 monthly breakdown](#qulla--n1-trailing-limit-no-time-stop--monthly-breakdown-15-day-stop-min-bucket-half-kelly--2026-06-17) below.

### Qulla + N=1 trailing-limit, NO time stop — monthly breakdown (15-day stop, min-bucket half-Kelly) — 2026-06-17

The original write-up of the no-time-stop default, on the **15-day** trailing-stop window (before the [window sweep](#trailing-stop-window-sweep--tighten-to-4-day-low-2026-06-17) showed w=4 is better). Same 5-filter system, Qulla day-low stop + N=1 trailing limit + 0.70 expansion, no time stop. **P&L bucketed by EXIT date** (realized P&L lands when the trade closes) — note this differs from the older entry-date sections above.

half-Kelly **0.114 / 0.151 / 0.169** (recomputed on this system's realized returns; the better exit lifts every bucket's PF vs the old 0.074/0.108/0.176). Total **+$1,703,397** Kelly-sized (flat +$1,630,805), 3671 trips. **Months profitable (by exit date): 171/257 = 66.5%** of the 257-month calendar span. Years positive: 22/22. Avg up +$11,247 vs down −$2,972 (3.8:1). Worst streak 5mo; max DD $-45,488.

> **⚠️ Small-n monthly-PF caveat:** many months have 1–8 trades, so the monthly PF column is noisy (`inf` on no-loss months). Trust net$/win% at month level; PF is meaningful only at year/bucket level.

Yearly (Kelly-sized, **by exit date**):
| year | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005 | 182 | 46.2% | 1.74 | +38,711 |
| 2006 | 258 | 46.5% | 2.17 | +80,598 |
| 2007 | 222 | 46.8% | 3.00 | +114,062 |
| 2008 | 55 | 54.5% | 2.42 | +19,083 |
| 2009 | 63 | 41.3% | 2.20 | +23,148 |
| 2010 | 185 | 48.1% | 2.69 | +74,537 |
| 2011 | 178 | 50.6% | 2.25 | +65,724 |
| 2012 | 117 | 46.2% | 2.42 | +44,839 |
| 2013 | 259 | 52.5% | 2.63 | +112,853 |
| 2014 | 203 | 49.3% | 1.92 | +52,858 |
| 2015 | 147 | 50.3% | 4.01 | +87,588 |
| 2016 | 137 | 52.6% | 2.32 | +33,371 |
| 2017 | 256 | 46.1% | 2.20 | +82,666 |
| 2018 | 218 | 48.2% | 2.89 | +100,252 |
| 2019 | 142 | 47.2% | 2.26 | +53,133 |
| 2020 | 184 | 40.8% | 1.97 | +85,683 |
| 2021 | 331 | 43.5% | 4.11 | +507,466 |
| 2022 | 49 | 38.8% | 1.55 | +12,991 |
| 2023 | 77 | 41.6% | 1.37 | +10,463 |
| 2024 | 184 | 43.5% | 2.06 | +57,123 |
| 2025 | 150 | 34.7% | 1.09 | +8,794 |
| 2026 | 74 | 55.4% | 2.33 | +37,454 |

<details>
<summary>Full per-month table (15d window, by exit date) — click to expand</summary>

| month | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005-02 | 10 | 10.0% | 0.18 | −3,962 |
| 2005-03 | 20 | 55.0% | 1.76 | +3,167 |
| 2005-04 | 5 | 80.0% | 3.04 | +3,728 |
| 2005-05 | 3 | 33.3% | 1.16 | +25 |
| 2005-06 | 16 | 37.5% | 0.16 | −8,031 |
| 2005-07 | 23 | 43.5% | 0.51 | −4,098 |
| 2005-08 | 49 | 38.8% | 1.15 | +2,305 |
| 2005-09 | 22 | 59.1% | 4.71 | +13,033 |
| 2005-10 | 11 | 54.5% | 23.05 | +26,804 |
| 2005-11 | 8 | 87.5% | 10.26 | +4,827 |
| 2005-12 | 15 | 40.0% | 1.28 | +914 |
| 2006-01 | 21 | 47.6% | 4.63 | +12,147 |
| 2006-02 | 28 | 46.4% | 1.13 | +1,902 |
| 2006-03 | 25 | 52.0% | 7.51 | +16,785 |
| 2006-04 | 32 | 62.5% | 3.95 | +23,624 |
| 2006-05 | 49 | 32.7% | 0.65 | −5,442 |
| 2006-06 | 4 | 25.0% | 7.56 | +9,354 |
| 2006-07 | 6 | 0.0% | 0.00 | −4,797 |
| 2006-08 | 7 | 57.1% | 0.41 | −506 |
| 2006-09 | 12 | 66.7% | 14.38 | +12,875 |
| 2006-10 | 17 | 23.5% | 0.28 | −3,302 |
| 2006-11 | 28 | 32.1% | 1.14 | +1,200 |
| 2006-12 | 29 | 75.9% | 5.47 | +16,758 |
| 2007-01 | 15 | 60.0% | 5.92 | +9,608 |
| 2007-02 | 28 | 35.7% | 1.21 | +1,261 |
| 2007-03 | 24 | 58.3% | 3.64 | +10,970 |
| 2007-04 | 18 | 38.9% | 3.27 | +7,298 |
| 2007-05 | 42 | 38.1% | 2.33 | +20,897 |
| 2007-06 | 34 | 58.8% | 1.24 | +2,332 |
| 2007-07 | 27 | 55.6% | 5.99 | +21,003 |
| 2007-08 | 6 | 50.0% | 8.12 | +15,473 |
| 2007-09 | 4 | 50.0% | 0.34 | −681 |
| 2007-10 | 11 | 45.5% | 9.47 | +28,925 |
| 2007-11 | 10 | 30.0% | 0.52 | −2,430 |
| 2007-12 | 3 | 0.0% | 0.00 | −595 |
| 2008-01 | 1 | 100.0% | inf | +2,101 |
| 2008-02 | 4 | 50.0% | 0.32 | −458 |
| 2008-03 | 2 | 50.0% | 1.20 | +52 |
| 2008-04 | 3 | 66.7% | 1.57 | +739 |
| 2008-05 | 6 | 50.0% | 3.39 | +3,030 |
| 2008-06 | 14 | 57.1% | 3.80 | +6,417 |
| 2008-07 | 9 | 66.7% | 7.54 | +8,438 |
| 2008-08 | 9 | 33.3% | 0.38 | −3,076 |
| 2008-09 | 6 | 50.0% | 2.25 | +1,821 |
| 2008-11 | 1 | 100.0% | inf | +20 |
| 2009-01 | 3 | 33.3% | 1.13 | +84 |
| 2009-02 | 1 | 100.0% | inf | +853 |
| 2009-05 | 3 | 100.0% | inf | +1,124 |
| 2009-06 | 3 | 33.3% | 7.30 | +2,486 |
| 2009-07 | 4 | 75.0% | 1.13 | +323 |
| 2009-08 | 9 | 11.1% | 0.51 | −1,603 |
| 2009-09 | 6 | 16.7% | 0.12 | −1,813 |
| 2009-10 | 18 | 27.8% | 1.96 | +7,848 |
| 2009-11 | 5 | 80.0% | 15.38 | +4,942 |
| 2009-12 | 11 | 54.5% | 5.44 | +8,904 |
| 2010-01 | 28 | 28.6% | 0.86 | −1,161 |
| 2010-02 | 13 | 46.2% | 2.25 | +2,085 |
| 2010-03 | 9 | 66.7% | 4.58 | +3,634 |
| 2010-04 | 17 | 35.3% | 3.25 | +9,953 |
| 2010-05 | 35 | 54.3% | 2.08 | +10,748 |
| 2010-06 | 2 | 50.0% | 21.60 | +1,844 |
| 2010-07 | 1 | 0.0% | 0.00 | −773 |
| 2010-08 | 8 | 12.5% | 0.04 | −3,343 |
| 2010-09 | 4 | 75.0% | 40.13 | +913 |
| 2010-10 | 6 | 66.7% | 22.36 | +8,713 |
| 2010-11 | 32 | 53.1% | 1.56 | +5,021 |
| 2010-12 | 30 | 60.0% | 8.42 | +36,904 |
| 2011-01 | 42 | 61.9% | 3.34 | +27,543 |
| 2011-02 | 29 | 44.8% | 1.83 | +5,941 |
| 2011-03 | 33 | 57.6% | 4.99 | +19,341 |
| 2011-04 | 13 | 69.2% | 7.67 | +12,245 |
| 2011-05 | 28 | 25.0% | 0.45 | −9,451 |
| 2011-06 | 11 | 54.5% | 4.84 | +8,562 |
| 2011-07 | 5 | 40.0% | 1.22 | +106 |
| 2011-08 | 6 | 66.7% | 4.15 | +3,274 |
| 2011-10 | 3 | 0.0% | 0.00 | −2,351 |
| 2011-11 | 4 | 25.0% | 0.03 | −2,359 |
| 2011-12 | 4 | 75.0% | 3.45 | +2,873 |
| 2012-01 | 5 | 80.0% | 16.09 | +8,690 |
| 2012-02 | 8 | 50.0% | 0.39 | −1,603 |
| 2012-03 | 17 | 23.5% | 0.40 | −5,330 |
| 2012-04 | 13 | 61.5% | 3.63 | +7,055 |
| 2012-05 | 16 | 37.5% | 3.72 | +11,977 |
| 2012-06 | 9 | 33.3% | 1.27 | +643 |
| 2012-07 | 8 | 50.0% | 5.34 | +6,431 |
| 2012-08 | 8 | 62.5% | 5.05 | +4,883 |
| 2012-09 | 13 | 46.2% | 1.95 | +2,488 |
| 2012-10 | 14 | 57.1% | 4.83 | +10,467 |
| 2012-11 | 3 | 33.3% | 1.05 | +54 |
| 2012-12 | 3 | 33.3% | 0.07 | −918 |
| 2013-01 | 8 | 12.5% | 0.03 | −3,264 |
| 2013-02 | 22 | 50.0% | 1.11 | +768 |
| 2013-03 | 19 | 52.6% | 1.81 | +4,136 |
| 2013-04 | 32 | 75.0% | 4.58 | +23,757 |
| 2013-05 | 18 | 38.9% | 3.23 | +8,005 |
| 2013-06 | 24 | 75.0% | 5.59 | +24,524 |
| 2013-07 | 7 | 71.4% | 18.24 | +7,863 |
| 2013-08 | 40 | 45.0% | 2.66 | +15,700 |
| 2013-09 | 8 | 62.5% | 4.87 | +4,831 |
| 2013-10 | 27 | 48.1% | 2.14 | +13,968 |
| 2013-11 | 24 | 45.8% | 2.18 | +8,308 |
| 2013-12 | 30 | 43.3% | 1.57 | +4,256 |
| 2014-01 | 38 | 52.6% | 1.61 | +9,551 |
| 2014-02 | 25 | 48.0% | 3.28 | +10,836 |
| 2014-03 | 37 | 43.2% | 1.30 | +4,432 |
| 2014-04 | 18 | 55.6% | 3.51 | +5,711 |
| 2014-05 | 7 | 57.1% | 126.63 | +5,165 |
| 2014-06 | 17 | 47.1% | 3.13 | +7,747 |
| 2014-07 | 17 | 41.2% | 1.03 | +174 |
| 2014-08 | 6 | 50.0% | 7.24 | +3,092 |
| 2014-09 | 11 | 9.1% | 0.35 | −4,010 |
| 2014-10 | 4 | 100.0% | inf | +2,182 |
| 2014-11 | 6 | 83.3% | 25.64 | +1,219 |
| 2014-12 | 17 | 58.8% | 2.92 | +6,760 |
| 2015-01 | 7 | 71.4% | 19.39 | +6,616 |
| 2015-02 | 12 | 41.7% | 2.71 | +5,010 |
| 2015-03 | 34 | 44.1% | 2.32 | +8,792 |
| 2015-04 | 23 | 47.8% | 2.21 | +7,784 |
| 2015-05 | 14 | 50.0% | 3.49 | +4,123 |
| 2015-06 | 13 | 53.8% | 4.24 | +8,822 |
| 2015-07 | 17 | 52.9% | 9.94 | +33,731 |
| 2015-08 | 3 | 66.7% | 2.92 | +1,414 |
| 2015-09 | 1 | 0.0% | inf | +0 |
| 2015-10 | 2 | 50.0% | 8.29 | +751 |
| 2015-11 | 6 | 33.3% | 0.43 | −1,251 |
| 2015-12 | 15 | 66.7% | 8.54 | +11,796 |
| 2016-01 | 8 | 50.0% | 2.41 | +2,836 |
| 2016-02 | 2 | 50.0% | 9.59 | +231 |
| 2016-03 | 5 | 0.0% | 0.00 | −1,864 |
| 2016-04 | 6 | 50.0% | 2.51 | +1,221 |
| 2016-05 | 4 | 25.0% | 0.43 | −1,030 |
| 2016-06 | 10 | 50.0% | 4.16 | +5,122 |
| 2016-07 | 3 | 100.0% | inf | +8,388 |
| 2016-08 | 26 | 53.8% | 2.18 | +3,925 |
| 2016-09 | 32 | 53.1% | 2.45 | +6,855 |
| 2016-10 | 23 | 60.9% | 2.72 | +6,503 |
| 2016-11 | 5 | 40.0% | 0.62 | −949 |
| 2016-12 | 13 | 61.5% | 1.78 | +2,133 |
| 2017-01 | 24 | 29.2% | 0.93 | −387 |
| 2017-02 | 18 | 33.3% | 1.19 | +1,054 |
| 2017-03 | 40 | 45.0% | 1.35 | +3,244 |
| 2017-04 | 21 | 47.6% | 2.99 | +13,720 |
| 2017-05 | 19 | 52.6% | 2.14 | +5,104 |
| 2017-06 | 20 | 65.0% | 19.40 | +30,205 |
| 2017-07 | 20 | 65.0% | 4.07 | +11,300 |
| 2017-08 | 17 | 41.2% | 1.12 | +761 |
| 2017-09 | 15 | 40.0% | 0.77 | −1,162 |
| 2017-10 | 28 | 39.3% | 0.72 | −2,944 |
| 2017-11 | 14 | 71.4% | 8.71 | +14,591 |
| 2017-12 | 20 | 35.0% | 1.88 | +7,179 |
| 2018-01 | 16 | 43.8% | 0.92 | −422 |
| 2018-02 | 20 | 35.0% | 1.01 | +88 |
| 2018-03 | 15 | 40.0% | 5.49 | +27,260 |
| 2018-04 | 9 | 66.7% | 8.81 | +10,369 |
| 2018-05 | 13 | 38.5% | 0.43 | −3,011 |
| 2018-06 | 38 | 52.6% | 2.40 | +9,411 |
| 2018-07 | 30 | 50.0% | 5.38 | +18,546 |
| 2018-08 | 16 | 31.2% | 1.08 | +311 |
| 2018-09 | 28 | 53.6% | 6.74 | +26,297 |
| 2018-10 | 19 | 73.7% | 7.71 | +14,335 |
| 2018-11 | 3 | 33.3% | 0.36 | −424 |
| 2018-12 | 11 | 36.4% | 0.55 | −2,508 |
| 2019-01 | 1 | 0.0% | inf | +0 |
| 2019-02 | 5 | 40.0% | 0.49 | −1,382 |
| 2019-03 | 22 | 54.5% | 3.41 | +15,353 |
| 2019-04 | 9 | 77.8% | 22.29 | +7,365 |
| 2019-05 | 17 | 41.2% | 4.30 | +18,688 |
| 2019-06 | 4 | 50.0% | 15.35 | +5,055 |
| 2019-07 | 17 | 29.4% | 0.87 | −1,110 |
| 2019-08 | 17 | 23.5% | 0.45 | −2,870 |
| 2019-09 | 14 | 50.0% | 2.87 | +9,116 |
| 2019-10 | 3 | 66.7% | 5.05 | +273 |
| 2019-11 | 12 | 33.3% | 0.87 | −603 |
| 2019-12 | 21 | 71.4% | 1.88 | +3,247 |
| 2020-01 | 27 | 63.0% | 4.76 | +17,691 |
| 2020-02 | 25 | 28.0% | 0.53 | −7,592 |
| 2020-03 | 9 | 33.3% | 1.72 | +4,808 |
| 2020-04 | 1 | 0.0% | 0.00 | −309 |
| 2020-05 | 5 | 20.0% | 0.07 | −4,235 |
| 2020-06 | 15 | 40.0% | 1.73 | +5,158 |
| 2020-07 | 6 | 33.3% | 0.56 | −852 |
| 2020-08 | 22 | 40.9% | 6.16 | +50,290 |
| 2020-09 | 16 | 75.0% | 5.45 | +22,528 |
| 2020-10 | 11 | 27.3% | 0.87 | −703 |
| 2020-11 | 11 | 27.3% | 0.99 | −96 |
| 2020-12 | 36 | 33.3% | 0.95 | −1,004 |
| 2021-01 | 53 | 58.5% | 20.79 | +313,244 |
| 2021-02 | 83 | 32.5% | 2.17 | +77,742 |
| 2021-03 | 46 | 54.3% | 5.62 | +98,815 |
| 2021-04 | 22 | 36.4% | 2.42 | +11,523 |
| 2021-05 | 13 | 53.8% | 1.52 | +1,327 |
| 2021-06 | 23 | 34.8% | 2.56 | +8,505 |
| 2021-07 | 15 | 60.0% | 1.40 | +2,838 |
| 2021-08 | 15 | 53.3% | 1.07 | +381 |
| 2021-09 | 13 | 38.5% | 0.44 | −2,109 |
| 2021-10 | 7 | 57.1% | 5.68 | +6,125 |
| 2021-11 | 32 | 28.1% | 0.52 | −10,923 |
| 2021-12 | 9 | 33.3% | 1.00 | −1 |
| 2022-01 | 5 | 60.0% | 4.94 | +7,186 |
| 2022-02 | 2 | 50.0% | 0.52 | −997 |
| 2022-03 | 2 | 50.0% | 3.86 | +1,171 |
| 2022-04 | 8 | 50.0% | 0.92 | −148 |
| 2022-06 | 4 | 50.0% | 10.74 | +14,474 |
| 2022-08 | 12 | 33.3% | 0.66 | −2,959 |
| 2022-09 | 6 | 33.3% | 0.27 | −1,596 |
| 2022-11 | 2 | 0.0% | 0.00 | −1,911 |
| 2022-12 | 8 | 25.0% | 0.32 | −2,228 |
| 2023-01 | 5 | 20.0% | 0.02 | −3,299 |
| 2023-02 | 7 | 42.9% | 6.19 | +9,060 |
| 2023-03 | 2 | 50.0% | 0.41 | −204 |
| 2023-04 | 3 | 33.3% | 3.32 | +1,750 |
| 2023-05 | 5 | 20.0% | 0.83 | −151 |
| 2023-06 | 7 | 42.9% | 1.54 | +1,196 |
| 2023-07 | 10 | 50.0% | 2.82 | +5,046 |
| 2023-08 | 16 | 43.8% | 0.77 | −1,236 |
| 2023-09 | 9 | 55.6% | 2.33 | +3,200 |
| 2023-10 | 1 | 0.0% | 0.00 | −1,212 |
| 2023-11 | 3 | 0.0% | 0.00 | −2,586 |
| 2023-12 | 9 | 55.6% | 0.76 | −1,102 |
| 2024-01 | 23 | 56.5% | 2.58 | +8,403 |
| 2024-02 | 12 | 25.0% | 1.78 | +2,587 |
| 2024-03 | 30 | 36.7% | 1.18 | +2,204 |
| 2024-04 | 27 | 59.3% | 7.38 | +13,663 |
| 2024-05 | 6 | 16.7% | 0.21 | −1,448 |
| 2024-06 | 7 | 100.0% | inf | +16,990 |
| 2024-07 | 2 | 100.0% | inf | +3,327 |
| 2024-08 | 7 | 28.6% | 0.32 | −2,414 |
| 2024-09 | 3 | 66.7% | 17.23 | +2,091 |
| 2024-10 | 19 | 31.6% | 0.59 | −3,330 |
| 2024-11 | 27 | 22.2% | 0.38 | −6,954 |
| 2024-12 | 21 | 52.4% | 4.79 | +22,005 |
| 2025-01 | 5 | 20.0% | 0.29 | −1,492 |
| 2025-02 | 17 | 23.5% | 0.27 | −8,918 |
| 2025-03 | 11 | 36.4% | 0.49 | −3,668 |
| 2025-05 | 6 | 33.3% | 0.02 | −7,585 |
| 2025-06 | 15 | 40.0% | 1.13 | +919 |
| 2025-07 | 23 | 21.7% | 0.24 | −22,878 |
| 2025-08 | 11 | 45.5% | 3.35 | +9,084 |
| 2025-09 | 17 | 11.8% | 0.11 | −10,950 |
| 2025-10 | 25 | 56.0% | 5.96 | +40,885 |
| 2025-11 | 9 | 66.7% | 10.20 | +18,945 |
| 2025-12 | 11 | 27.3% | 0.19 | −5,548 |
| 2026-01 | 14 | 57.1% | 3.35 | +7,699 |
| 2026-02 | 17 | 35.3% | 1.49 | +2,745 |
| 2026-03 | 14 | 78.6% | 6.80 | +10,736 |
| 2026-04 | 6 | 50.0% | 9.66 | +9,666 |
| 2026-05 | 23 | 56.5% | 1.41 | +6,608 |

</details>

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

> ## ⚠️ INVALID FROM HERE DOWN — top-tick fill artifact (see warning at top of doc)
> **Every result in this section and all sections below it is inflated.** The trailing-limit
> exit modelled here fills at the recent high essentially every time (`min(seed, bar.high)` on
> the next bar), which is unrealistic for a resting sell-limit. Real PF on this config is
> **~1.7–1.8**, not the 2.0–3.0 shown below; the trailing limit adds only ~+1% (it is not the
> headline edge), and the stop-window sweep ordering is reversed (window 4 wins, not window 1).
> Corrected, realistic-fill results live in `docs/momentum_v1_results.md`. Kept here only as a
> record of the mistake.

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

### Trailing-stop window sweep — tighten to 4-day-low (2026-06-17)

With the mean-reversion limit recovering the bad stop-fills, the 15-day trailing-low stop is too loose: hitting the stop no longer means dumping at a bad open, so the stop can be tightened to recycle capital faster without the old penalty. Swept `--stop-low-window` from 14→1 on the new default (Qulla day-low + N=1 limit + 0.70 expansion, no time stop, 0.95 band). Same 3,671 entries every row — the window changes only *exits*.

| stop window (d) | Win% | PF | Total P&L | Median hold | Fill% |
| --- | ---: | ---: | ---: | ---: | ---: |
| 14 | 46.6% | 2.395 | +1,630,805 | 17 | 97.1% |
| 13 | 47.1% | 2.439 | +1,643,317 | 16 | 97.1% |
| 12 | 47.8% | 2.448 | +1,610,526 | 16 | 97.1% |
| 11 | 48.2% | 2.475 | +1,581,012 | 15 | 97.4% |
| 10 | 48.7% | 2.446 | +1,513,249 | 14 | 97.6% |
| 9 | 49.2% | 2.508 | +1,517,491 | 13 | 97.7% |
| 8 | 50.0% | 2.586 | +1,522,020 | 12 | 97.8% |
| 7 | 50.8% | 2.592 | +1,455,102 | 11 | 98.0% |
| 6 | 51.6% | 2.659 | +1,437,140 | 10 | 98.1% |
| 5 | 52.6% | 2.741 | +1,435,900 | 9 | 98.3% |
| 4 **(sweet spot)** | 54.2% | 2.811 | +1,387,731 | 8 | 98.5% |
| 3 | 55.1% | 2.738 | +1,204,814 | 6 | 98.6% |
| 2 | 56.9% | 2.899 | +1,164,409 | 5 | 98.9% |
| 1 | 59.7% | 3.028 | +1,022,469 | 4 | 99.2% |

**Findings — sweet spot is the 4-day-low stop:**
- **PF rises monotonically as the stop tightens** (2.40 at 15d → 2.81 at 4d → 3.03 at 1d) and **win rate climbs from 47% to 60%** — the tighter stop, paired with the limit that sells the bounce, cleans up the loss side.
- **But net P&L is a plateau-then-cliff.** It holds ~$1.39–1.52M across windows 8→4, then **falls off below 4**: $1.39M (w=4) → $1.20M (w=3) → $1.02M (w=1, −37% vs the 15d default). The tightest stops amputate the right tail — the multi-week runners that carry the strategy get whipsawed out in 4–6 days.
- **Window 4 is the edge of the plateau:** PF 2.811, win 54.2%, $1,387,731, **8-day median hold** (vs 17 at the 15d default). It buys **+0.42 PF and ~2× faster capital turnover for only −10% P&L** — a clear return-on-capital win (the metric that matters for a real, capital-constrained account). Below 4 you trade real P&L for a higher win-rate vanity metric.
- **New recommendation: `--stop-low-window 4`** on the Qulla N=1 no-time-stop default. The 15d default was tuned in a world where hitting the stop meant a bad market-on-open exit; the mean-reversion limit removed that penalty, so the stop should be tight.

**The window is the swing-vs-position knob.** A loose stop (14d) is position-trading; a 1-day stop is Lance-Breitstein-style swing-trading — and it has the *best* consistency, at the cost of raw P&L. As the stop tightens 14→1: **months-profitable 69%→85%, max monthly drawdown −$45k→−$3.3k (~14× smaller), every window 22/22 years positive from w≤9** — while total P&L falls ~40%. **w=4 ⭐ is the P&L-on-plateau sweet spot; w=1 🛡 is the max-consistency / small-account / swing config.** Full per-window consistency table + yearly + (collapsible) monthly breakdowns for all 14 windows, each with its own recomputed half-Kelly fractions, are in the [all-windows breakdown](#trailing-stop-window-sweep--full-monthlyyearly-breakdowns-all-14-windows-2026-06-17) below.

### Trailing-stop window sweep — full monthly/yearly breakdowns, all 14 windows (2026-06-17)

The [window sweep](#trailing-stop-window-sweep--tighten-to-4-day-low-2026-06-17) is the **swing-vs-position knob**: a loose stop (14d) is position-trading, a 1-day stop is Lance-Breitstein-style swing-trading. Same 3,671 entries every row — the window changes only *exits*. Each window's half-Kelly bucket fractions are **recomputed on that window's own realized returns**. **All P&L bucketed by EXIT date** (this differs from the older entry-date sections above). **Months-profitable uses a fixed 257-month calendar-span denominator** so the windows are directly comparable — a month with zero exits counts as neither win nor loss but stays in the denominator.

**Takeaway:** as the stop tightens, **raw P&L falls but consistency rises** — months-profitable (of 257) 67%→79%, max drawdown −$45k→−$3.3k (~14× smaller), every window 22/22 years positive. **w=4 ⭐ = P&L-on-plateau sweet spot; w=1 🛡 = max-consistency / small-account / swing config.**

#### Consistency across windows (Kelly-sized, by exit date)

| window | trips | total P&L | months + (of 257) | years + | up:dn | worst streak | max DD |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| **14** | 3671 | +1,703,397 | 171 (67%) | 22/22 | 3.8:1 | 5mo | -45,488 |
| **13** | 3671 | +1,719,967 | 172 (67%) | 22/22 | 4.0:1 | 5mo | -43,368 |
| **12** | 3671 | +1,688,064 | 178 (69%) | 20/22 | 3.7:1 | 6mo | -36,495 |
| **11** | 3671 | +1,660,420 | 178 (69%) | 21/22 | 3.7:1 | 6mo | -29,310 |
| **10** | 3671 | +1,595,139 | 174 (68%) | 21/22 | 3.8:1 | 5mo | -31,570 |
| **9** | 3671 | +1,596,323 | 173 (67%) | 22/22 | 4.1:1 | 4mo | -26,281 |
| **8** | 3671 | +1,618,521 | 173 (67%) | 22/22 | 4.7:1 | 4mo | -13,704 |
| **7** | 3671 | +1,542,128 | 178 (69%) | 22/22 | 4.8:1 | 4mo | -7,129 |
| **6** | 3671 | +1,530,200 | 178 (69%) | 22/22 | 5.3:1 | 4mo | -9,649 |
| **5** | 3671 | +1,541,855 | 180 (70%) | 22/22 | 6.2:1 | 3mo | -9,664 |
| **4** ⭐ | 3671 | +1,476,605 | 185 (72%) | 22/22 | 6.7:1 | 3mo | -8,976 |
| **3** | 3671 | +1,288,366 | 190 (74%) | 22/22 | 6.5:1 | 3mo | -4,679 |
| **2** | 3671 | +1,245,589 | 187 (73%) | 22/22 | 7.4:1 | 3mo | -3,826 |
| **1** 🛡 | 3671 | +1,062,486 | 202 (79%) | 22/22 | 6.9:1 | 3mo | -3,299 |

#### Window = 14-day-low stop

half-Kelly 0.114/0.151/0.169 · total **+$1,703,397** Kelly (flat +$1,630,805) · months **171/257 (67%)** · years **22/22** · up:dn **3.8:1** · max DD **-45,488** · worst streak **5mo**

Yearly (Kelly-sized, by exit date):
| year | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005 | 182 | 46.2% | 1.74 | +38,711 |
| 2006 | 258 | 46.5% | 2.17 | +80,598 |
| 2007 | 222 | 46.8% | 3.00 | +114,062 |
| 2008 | 55 | 54.5% | 2.42 | +19,083 |
| 2009 | 63 | 41.3% | 2.20 | +23,148 |
| 2010 | 185 | 48.1% | 2.69 | +74,537 |
| 2011 | 178 | 50.6% | 2.25 | +65,724 |
| 2012 | 117 | 46.2% | 2.42 | +44,839 |
| 2013 | 259 | 52.5% | 2.63 | +112,853 |
| 2014 | 203 | 49.3% | 1.92 | +52,858 |
| 2015 | 147 | 50.3% | 4.01 | +87,588 |
| 2016 | 137 | 52.6% | 2.32 | +33,371 |
| 2017 | 256 | 46.1% | 2.20 | +82,666 |
| 2018 | 218 | 48.2% | 2.89 | +100,252 |
| 2019 | 142 | 47.2% | 2.26 | +53,133 |
| 2020 | 184 | 40.8% | 1.97 | +85,683 |
| 2021 | 331 | 43.5% | 4.11 | +507,466 |
| 2022 | 49 | 38.8% | 1.55 | +12,991 |
| 2023 | 77 | 41.6% | 1.37 | +10,463 |
| 2024 | 184 | 43.5% | 2.06 | +57,123 |
| 2025 | 150 | 34.7% | 1.09 | +8,794 |
| 2026 | 74 | 55.4% | 2.33 | +37,454 |

<details>
<summary>Full monthly table</summary>

| month | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005-02 | 10 | 10.0% | 0.18 | −3,962 |
| 2005-03 | 20 | 55.0% | 1.76 | +3,167 |
| 2005-04 | 5 | 80.0% | 3.04 | +3,728 |
| 2005-05 | 3 | 33.3% | 1.16 | +25 |
| 2005-06 | 16 | 37.5% | 0.16 | −8,031 |
| 2005-07 | 23 | 43.5% | 0.51 | −4,098 |
| 2005-08 | 49 | 38.8% | 1.15 | +2,305 |
| 2005-09 | 22 | 59.1% | 4.71 | +13,033 |
| 2005-10 | 11 | 54.5% | 23.05 | +26,804 |
| 2005-11 | 8 | 87.5% | 10.26 | +4,827 |
| 2005-12 | 15 | 40.0% | 1.28 | +914 |
| 2006-01 | 21 | 47.6% | 4.63 | +12,147 |
| 2006-02 | 28 | 46.4% | 1.13 | +1,902 |
| 2006-03 | 25 | 52.0% | 7.51 | +16,785 |
| 2006-04 | 32 | 62.5% | 3.95 | +23,624 |
| 2006-05 | 49 | 32.7% | 0.65 | −5,442 |
| 2006-06 | 4 | 25.0% | 7.56 | +9,354 |
| 2006-07 | 6 | 0.0% | 0.00 | −4,797 |
| 2006-08 | 7 | 57.1% | 0.41 | −506 |
| 2006-09 | 12 | 66.7% | 14.38 | +12,875 |
| 2006-10 | 17 | 23.5% | 0.28 | −3,302 |
| 2006-11 | 28 | 32.1% | 1.14 | +1,200 |
| 2006-12 | 29 | 75.9% | 5.47 | +16,758 |
| 2007-01 | 15 | 60.0% | 5.92 | +9,608 |
| 2007-02 | 28 | 35.7% | 1.21 | +1,261 |
| 2007-03 | 24 | 58.3% | 3.64 | +10,970 |
| 2007-04 | 18 | 38.9% | 3.27 | +7,298 |
| 2007-05 | 42 | 38.1% | 2.33 | +20,897 |
| 2007-06 | 34 | 58.8% | 1.24 | +2,332 |
| 2007-07 | 27 | 55.6% | 5.99 | +21,003 |
| 2007-08 | 6 | 50.0% | 8.12 | +15,473 |
| 2007-09 | 4 | 50.0% | 0.34 | −681 |
| 2007-10 | 11 | 45.5% | 9.47 | +28,925 |
| 2007-11 | 10 | 30.0% | 0.52 | −2,430 |
| 2007-12 | 3 | 0.0% | 0.00 | −595 |
| 2008-01 | 1 | 100.0% | inf | +2,101 |
| 2008-02 | 4 | 50.0% | 0.32 | −458 |
| 2008-03 | 2 | 50.0% | 1.20 | +52 |
| 2008-04 | 3 | 66.7% | 1.57 | +739 |
| 2008-05 | 6 | 50.0% | 3.39 | +3,030 |
| 2008-06 | 14 | 57.1% | 3.80 | +6,417 |
| 2008-07 | 9 | 66.7% | 7.54 | +8,438 |
| 2008-08 | 9 | 33.3% | 0.38 | −3,076 |
| 2008-09 | 6 | 50.0% | 2.25 | +1,821 |
| 2008-11 | 1 | 100.0% | inf | +20 |
| 2009-01 | 3 | 33.3% | 1.13 | +84 |
| 2009-02 | 1 | 100.0% | inf | +853 |
| 2009-05 | 3 | 100.0% | inf | +1,124 |
| 2009-06 | 3 | 33.3% | 7.30 | +2,486 |
| 2009-07 | 4 | 75.0% | 1.13 | +323 |
| 2009-08 | 9 | 11.1% | 0.51 | −1,603 |
| 2009-09 | 6 | 16.7% | 0.12 | −1,813 |
| 2009-10 | 18 | 27.8% | 1.96 | +7,848 |
| 2009-11 | 5 | 80.0% | 15.38 | +4,942 |
| 2009-12 | 11 | 54.5% | 5.44 | +8,904 |
| 2010-01 | 28 | 28.6% | 0.86 | −1,161 |
| 2010-02 | 13 | 46.2% | 2.25 | +2,085 |
| 2010-03 | 9 | 66.7% | 4.58 | +3,634 |
| 2010-04 | 17 | 35.3% | 3.25 | +9,953 |
| 2010-05 | 35 | 54.3% | 2.08 | +10,748 |
| 2010-06 | 2 | 50.0% | 21.60 | +1,844 |
| 2010-07 | 1 | 0.0% | 0.00 | −773 |
| 2010-08 | 8 | 12.5% | 0.04 | −3,343 |
| 2010-09 | 4 | 75.0% | 40.13 | +913 |
| 2010-10 | 6 | 66.7% | 22.36 | +8,713 |
| 2010-11 | 32 | 53.1% | 1.56 | +5,021 |
| 2010-12 | 30 | 60.0% | 8.42 | +36,904 |
| 2011-01 | 42 | 61.9% | 3.34 | +27,543 |
| 2011-02 | 29 | 44.8% | 1.83 | +5,941 |
| 2011-03 | 33 | 57.6% | 4.99 | +19,341 |
| 2011-04 | 13 | 69.2% | 7.67 | +12,245 |
| 2011-05 | 28 | 25.0% | 0.45 | −9,451 |
| 2011-06 | 11 | 54.5% | 4.84 | +8,562 |
| 2011-07 | 5 | 40.0% | 1.22 | +106 |
| 2011-08 | 6 | 66.7% | 4.15 | +3,274 |
| 2011-10 | 3 | 0.0% | 0.00 | −2,351 |
| 2011-11 | 4 | 25.0% | 0.03 | −2,359 |
| 2011-12 | 4 | 75.0% | 3.45 | +2,873 |
| 2012-01 | 5 | 80.0% | 16.09 | +8,690 |
| 2012-02 | 8 | 50.0% | 0.39 | −1,603 |
| 2012-03 | 17 | 23.5% | 0.40 | −5,330 |
| 2012-04 | 13 | 61.5% | 3.63 | +7,055 |
| 2012-05 | 16 | 37.5% | 3.72 | +11,977 |
| 2012-06 | 9 | 33.3% | 1.27 | +643 |
| 2012-07 | 8 | 50.0% | 5.34 | +6,431 |
| 2012-08 | 8 | 62.5% | 5.05 | +4,883 |
| 2012-09 | 13 | 46.2% | 1.95 | +2,488 |
| 2012-10 | 14 | 57.1% | 4.83 | +10,467 |
| 2012-11 | 3 | 33.3% | 1.05 | +54 |
| 2012-12 | 3 | 33.3% | 0.07 | −918 |
| 2013-01 | 8 | 12.5% | 0.03 | −3,264 |
| 2013-02 | 22 | 50.0% | 1.11 | +768 |
| 2013-03 | 19 | 52.6% | 1.81 | +4,136 |
| 2013-04 | 32 | 75.0% | 4.58 | +23,757 |
| 2013-05 | 18 | 38.9% | 3.23 | +8,005 |
| 2013-06 | 24 | 75.0% | 5.59 | +24,524 |
| 2013-07 | 7 | 71.4% | 18.24 | +7,863 |
| 2013-08 | 40 | 45.0% | 2.66 | +15,700 |
| 2013-09 | 8 | 62.5% | 4.87 | +4,831 |
| 2013-10 | 27 | 48.1% | 2.14 | +13,968 |
| 2013-11 | 24 | 45.8% | 2.18 | +8,308 |
| 2013-12 | 30 | 43.3% | 1.57 | +4,256 |
| 2014-01 | 38 | 52.6% | 1.61 | +9,551 |
| 2014-02 | 25 | 48.0% | 3.28 | +10,836 |
| 2014-03 | 37 | 43.2% | 1.30 | +4,432 |
| 2014-04 | 18 | 55.6% | 3.51 | +5,711 |
| 2014-05 | 7 | 57.1% | 126.63 | +5,165 |
| 2014-06 | 17 | 47.1% | 3.13 | +7,747 |
| 2014-07 | 17 | 41.2% | 1.03 | +174 |
| 2014-08 | 6 | 50.0% | 7.24 | +3,092 |
| 2014-09 | 11 | 9.1% | 0.35 | −4,010 |
| 2014-10 | 4 | 100.0% | inf | +2,182 |
| 2014-11 | 6 | 83.3% | 25.64 | +1,219 |
| 2014-12 | 17 | 58.8% | 2.92 | +6,760 |
| 2015-01 | 7 | 71.4% | 19.39 | +6,616 |
| 2015-02 | 12 | 41.7% | 2.71 | +5,010 |
| 2015-03 | 34 | 44.1% | 2.32 | +8,792 |
| 2015-04 | 23 | 47.8% | 2.21 | +7,784 |
| 2015-05 | 14 | 50.0% | 3.49 | +4,123 |
| 2015-06 | 13 | 53.8% | 4.24 | +8,822 |
| 2015-07 | 17 | 52.9% | 9.94 | +33,731 |
| 2015-08 | 3 | 66.7% | 2.92 | +1,414 |
| 2015-09 | 1 | 0.0% | inf | +0 |
| 2015-10 | 2 | 50.0% | 8.29 | +751 |
| 2015-11 | 6 | 33.3% | 0.43 | −1,251 |
| 2015-12 | 15 | 66.7% | 8.54 | +11,796 |
| 2016-01 | 8 | 50.0% | 2.41 | +2,836 |
| 2016-02 | 2 | 50.0% | 9.59 | +231 |
| 2016-03 | 5 | 0.0% | 0.00 | −1,864 |
| 2016-04 | 6 | 50.0% | 2.51 | +1,221 |
| 2016-05 | 4 | 25.0% | 0.43 | −1,030 |
| 2016-06 | 10 | 50.0% | 4.16 | +5,122 |
| 2016-07 | 3 | 100.0% | inf | +8,388 |
| 2016-08 | 26 | 53.8% | 2.18 | +3,925 |
| 2016-09 | 32 | 53.1% | 2.45 | +6,855 |
| 2016-10 | 23 | 60.9% | 2.72 | +6,503 |
| 2016-11 | 5 | 40.0% | 0.62 | −949 |
| 2016-12 | 13 | 61.5% | 1.78 | +2,133 |
| 2017-01 | 24 | 29.2% | 0.93 | −387 |
| 2017-02 | 18 | 33.3% | 1.19 | +1,054 |
| 2017-03 | 40 | 45.0% | 1.35 | +3,244 |
| 2017-04 | 21 | 47.6% | 2.99 | +13,720 |
| 2017-05 | 19 | 52.6% | 2.14 | +5,104 |
| 2017-06 | 20 | 65.0% | 19.40 | +30,205 |
| 2017-07 | 20 | 65.0% | 4.07 | +11,300 |
| 2017-08 | 17 | 41.2% | 1.12 | +761 |
| 2017-09 | 15 | 40.0% | 0.77 | −1,162 |
| 2017-10 | 28 | 39.3% | 0.72 | −2,944 |
| 2017-11 | 14 | 71.4% | 8.71 | +14,591 |
| 2017-12 | 20 | 35.0% | 1.88 | +7,179 |
| 2018-01 | 16 | 43.8% | 0.92 | −422 |
| 2018-02 | 20 | 35.0% | 1.01 | +88 |
| 2018-03 | 15 | 40.0% | 5.49 | +27,260 |
| 2018-04 | 9 | 66.7% | 8.81 | +10,369 |
| 2018-05 | 13 | 38.5% | 0.43 | −3,011 |
| 2018-06 | 38 | 52.6% | 2.40 | +9,411 |
| 2018-07 | 30 | 50.0% | 5.38 | +18,546 |
| 2018-08 | 16 | 31.2% | 1.08 | +311 |
| 2018-09 | 28 | 53.6% | 6.74 | +26,297 |
| 2018-10 | 19 | 73.7% | 7.71 | +14,335 |
| 2018-11 | 3 | 33.3% | 0.36 | −424 |
| 2018-12 | 11 | 36.4% | 0.55 | −2,508 |
| 2019-01 | 1 | 0.0% | inf | +0 |
| 2019-02 | 5 | 40.0% | 0.49 | −1,382 |
| 2019-03 | 22 | 54.5% | 3.41 | +15,353 |
| 2019-04 | 9 | 77.8% | 22.29 | +7,365 |
| 2019-05 | 17 | 41.2% | 4.30 | +18,688 |
| 2019-06 | 4 | 50.0% | 15.35 | +5,055 |
| 2019-07 | 17 | 29.4% | 0.87 | −1,110 |
| 2019-08 | 17 | 23.5% | 0.45 | −2,870 |
| 2019-09 | 14 | 50.0% | 2.87 | +9,116 |
| 2019-10 | 3 | 66.7% | 5.05 | +273 |
| 2019-11 | 12 | 33.3% | 0.87 | −603 |
| 2019-12 | 21 | 71.4% | 1.88 | +3,247 |
| 2020-01 | 27 | 63.0% | 4.76 | +17,691 |
| 2020-02 | 25 | 28.0% | 0.53 | −7,592 |
| 2020-03 | 9 | 33.3% | 1.72 | +4,808 |
| 2020-04 | 1 | 0.0% | 0.00 | −309 |
| 2020-05 | 5 | 20.0% | 0.07 | −4,235 |
| 2020-06 | 15 | 40.0% | 1.73 | +5,158 |
| 2020-07 | 6 | 33.3% | 0.56 | −852 |
| 2020-08 | 22 | 40.9% | 6.16 | +50,290 |
| 2020-09 | 16 | 75.0% | 5.45 | +22,528 |
| 2020-10 | 11 | 27.3% | 0.87 | −703 |
| 2020-11 | 11 | 27.3% | 0.99 | −96 |
| 2020-12 | 36 | 33.3% | 0.95 | −1,004 |
| 2021-01 | 53 | 58.5% | 20.79 | +313,244 |
| 2021-02 | 83 | 32.5% | 2.17 | +77,742 |
| 2021-03 | 46 | 54.3% | 5.62 | +98,815 |
| 2021-04 | 22 | 36.4% | 2.42 | +11,523 |
| 2021-05 | 13 | 53.8% | 1.52 | +1,327 |
| 2021-06 | 23 | 34.8% | 2.56 | +8,505 |
| 2021-07 | 15 | 60.0% | 1.40 | +2,838 |
| 2021-08 | 15 | 53.3% | 1.07 | +381 |
| 2021-09 | 13 | 38.5% | 0.44 | −2,109 |
| 2021-10 | 7 | 57.1% | 5.68 | +6,125 |
| 2021-11 | 32 | 28.1% | 0.52 | −10,923 |
| 2021-12 | 9 | 33.3% | 1.00 | −1 |
| 2022-01 | 5 | 60.0% | 4.94 | +7,186 |
| 2022-02 | 2 | 50.0% | 0.52 | −997 |
| 2022-03 | 2 | 50.0% | 3.86 | +1,171 |
| 2022-04 | 8 | 50.0% | 0.92 | −148 |
| 2022-06 | 4 | 50.0% | 10.74 | +14,474 |
| 2022-08 | 12 | 33.3% | 0.66 | −2,959 |
| 2022-09 | 6 | 33.3% | 0.27 | −1,596 |
| 2022-11 | 2 | 0.0% | 0.00 | −1,911 |
| 2022-12 | 8 | 25.0% | 0.32 | −2,228 |
| 2023-01 | 5 | 20.0% | 0.02 | −3,299 |
| 2023-02 | 7 | 42.9% | 6.19 | +9,060 |
| 2023-03 | 2 | 50.0% | 0.41 | −204 |
| 2023-04 | 3 | 33.3% | 3.32 | +1,750 |
| 2023-05 | 5 | 20.0% | 0.83 | −151 |
| 2023-06 | 7 | 42.9% | 1.54 | +1,196 |
| 2023-07 | 10 | 50.0% | 2.82 | +5,046 |
| 2023-08 | 16 | 43.8% | 0.77 | −1,236 |
| 2023-09 | 9 | 55.6% | 2.33 | +3,200 |
| 2023-10 | 1 | 0.0% | 0.00 | −1,212 |
| 2023-11 | 3 | 0.0% | 0.00 | −2,586 |
| 2023-12 | 9 | 55.6% | 0.76 | −1,102 |
| 2024-01 | 23 | 56.5% | 2.58 | +8,403 |
| 2024-02 | 12 | 25.0% | 1.78 | +2,587 |
| 2024-03 | 30 | 36.7% | 1.18 | +2,204 |
| 2024-04 | 27 | 59.3% | 7.38 | +13,663 |
| 2024-05 | 6 | 16.7% | 0.21 | −1,448 |
| 2024-06 | 7 | 100.0% | inf | +16,990 |
| 2024-07 | 2 | 100.0% | inf | +3,327 |
| 2024-08 | 7 | 28.6% | 0.32 | −2,414 |
| 2024-09 | 3 | 66.7% | 17.23 | +2,091 |
| 2024-10 | 19 | 31.6% | 0.59 | −3,330 |
| 2024-11 | 27 | 22.2% | 0.38 | −6,954 |
| 2024-12 | 21 | 52.4% | 4.79 | +22,005 |
| 2025-01 | 5 | 20.0% | 0.29 | −1,492 |
| 2025-02 | 17 | 23.5% | 0.27 | −8,918 |
| 2025-03 | 11 | 36.4% | 0.49 | −3,668 |
| 2025-05 | 6 | 33.3% | 0.02 | −7,585 |
| 2025-06 | 15 | 40.0% | 1.13 | +919 |
| 2025-07 | 23 | 21.7% | 0.24 | −22,878 |
| 2025-08 | 11 | 45.5% | 3.35 | +9,084 |
| 2025-09 | 17 | 11.8% | 0.11 | −10,950 |
| 2025-10 | 25 | 56.0% | 5.96 | +40,885 |
| 2025-11 | 9 | 66.7% | 10.20 | +18,945 |
| 2025-12 | 11 | 27.3% | 0.19 | −5,548 |
| 2026-01 | 14 | 57.1% | 3.35 | +7,699 |
| 2026-02 | 17 | 35.3% | 1.49 | +2,745 |
| 2026-03 | 14 | 78.6% | 6.80 | +10,736 |
| 2026-04 | 6 | 50.0% | 9.66 | +9,666 |
| 2026-05 | 23 | 56.5% | 1.41 | +6,608 |

</details>

#### Window = 13-day-low stop

half-Kelly 0.116/0.155/0.174 · total **+$1,719,967** Kelly (flat +$1,643,317) · months **172/257 (67%)** · years **22/22** · up:dn **4.0:1** · max DD **-43,368** · worst streak **5mo**

Yearly (Kelly-sized, by exit date):
| year | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005 | 185 | 46.5% | 1.75 | +38,921 |
| 2006 | 256 | 46.5% | 2.17 | +80,086 |
| 2007 | 221 | 45.2% | 2.84 | +103,623 |
| 2008 | 55 | 54.5% | 2.37 | +18,507 |
| 2009 | 63 | 44.4% | 2.44 | +27,361 |
| 2010 | 191 | 48.7% | 2.98 | +87,455 |
| 2011 | 172 | 50.0% | 1.84 | +43,222 |
| 2012 | 118 | 48.3% | 2.60 | +47,673 |
| 2013 | 262 | 53.4% | 2.87 | +124,849 |
| 2014 | 200 | 49.0% | 1.78 | +43,545 |
| 2015 | 147 | 52.4% | 4.30 | +89,543 |
| 2016 | 140 | 52.1% | 2.41 | +34,232 |
| 2017 | 253 | 46.6% | 2.28 | +84,595 |
| 2018 | 219 | 47.9% | 2.99 | +102,264 |
| 2019 | 142 | 48.6% | 2.35 | +53,694 |
| 2020 | 184 | 41.8% | 2.07 | +92,497 |
| 2021 | 330 | 44.5% | 4.27 | +519,390 |
| 2022 | 48 | 37.5% | 1.56 | +13,158 |
| 2023 | 78 | 42.3% | 1.33 | +9,699 |
| 2024 | 183 | 43.7% | 2.04 | +54,196 |
| 2025 | 150 | 36.0% | 1.14 | +14,070 |
| 2026 | 74 | 55.4% | 2.34 | +37,386 |

<details>
<summary>Full monthly table</summary>

| month | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005-02 | 11 | 18.2% | 0.34 | −3,214 |
| 2005-03 | 20 | 55.0% | 1.76 | +3,096 |
| 2005-04 | 4 | 75.0% | 2.01 | +1,848 |
| 2005-05 | 3 | 33.3% | 1.17 | +25 |
| 2005-06 | 16 | 37.5% | 0.16 | −8,037 |
| 2005-07 | 25 | 48.0% | 0.62 | −3,127 |
| 2005-08 | 51 | 41.2% | 1.92 | +13,549 |
| 2005-09 | 20 | 55.0% | 3.96 | +10,406 |
| 2005-10 | 10 | 50.0% | 15.38 | +17,471 |
| 2005-11 | 8 | 87.5% | 11.05 | +5,240 |
| 2005-12 | 17 | 41.2% | 1.47 | +1,665 |
| 2006-01 | 18 | 44.4% | 3.22 | +6,770 |
| 2006-02 | 29 | 48.3% | 1.37 | +5,397 |
| 2006-03 | 24 | 50.0% | 6.30 | +13,651 |
| 2006-04 | 32 | 62.5% | 3.94 | +23,737 |
| 2006-05 | 49 | 34.7% | 0.73 | −3,982 |
| 2006-06 | 4 | 25.0% | 7.56 | +9,355 |
| 2006-07 | 7 | 0.0% | 0.00 | −5,199 |
| 2006-08 | 8 | 62.5% | 0.44 | −479 |
| 2006-09 | 12 | 66.7% | 16.48 | +14,866 |
| 2006-10 | 16 | 12.5% | 0.23 | −3,701 |
| 2006-11 | 28 | 35.7% | 1.31 | +2,620 |
| 2006-12 | 29 | 75.9% | 5.56 | +17,051 |
| 2007-01 | 15 | 53.3% | 7.79 | +9,442 |
| 2007-02 | 29 | 37.9% | 1.77 | +4,625 |
| 2007-03 | 24 | 62.5% | 4.13 | +12,079 |
| 2007-04 | 19 | 42.1% | 2.62 | +5,236 |
| 2007-05 | 45 | 37.8% | 2.07 | +16,981 |
| 2007-06 | 33 | 54.5% | 1.43 | +4,046 |
| 2007-07 | 23 | 47.8% | 4.30 | +14,137 |
| 2007-08 | 5 | 40.0% | 6.39 | +11,683 |
| 2007-09 | 4 | 50.0% | 0.31 | −713 |
| 2007-10 | 11 | 45.5% | 9.54 | +29,156 |
| 2007-11 | 10 | 30.0% | 0.51 | −2,454 |
| 2007-12 | 3 | 0.0% | 0.00 | −595 |
| 2008-01 | 1 | 100.0% | inf | +2,095 |
| 2008-02 | 4 | 50.0% | 0.32 | −459 |
| 2008-03 | 2 | 50.0% | 1.20 | +51 |
| 2008-04 | 4 | 75.0% | 3.74 | +3,597 |
| 2008-05 | 5 | 40.0% | 0.71 | −370 |
| 2008-06 | 14 | 57.1% | 3.79 | +6,398 |
| 2008-07 | 9 | 66.7% | 7.54 | +8,428 |
| 2008-08 | 9 | 33.3% | 0.38 | −3,073 |
| 2008-09 | 6 | 50.0% | 2.25 | +1,818 |
| 2008-11 | 1 | 100.0% | inf | +20 |
| 2009-01 | 3 | 33.3% | 1.13 | +83 |
| 2009-02 | 1 | 100.0% | inf | +851 |
| 2009-05 | 3 | 100.0% | inf | +1,124 |
| 2009-06 | 3 | 33.3% | 7.36 | +2,509 |
| 2009-07 | 4 | 75.0% | 1.16 | +401 |
| 2009-08 | 9 | 11.1% | 0.51 | −1,613 |
| 2009-09 | 6 | 16.7% | 0.12 | −1,821 |
| 2009-10 | 19 | 36.8% | 2.59 | +12,385 |
| 2009-11 | 5 | 80.0% | 11.47 | +3,599 |
| 2009-12 | 10 | 60.0% | 5.95 | +9,843 |
| 2010-01 | 29 | 31.0% | 0.93 | −577 |
| 2010-02 | 12 | 41.7% | 2.03 | +1,719 |
| 2010-03 | 9 | 66.7% | 5.11 | +4,187 |
| 2010-04 | 17 | 35.3% | 3.34 | +10,359 |
| 2010-05 | 35 | 54.3% | 2.19 | +11,926 |
| 2010-06 | 3 | 33.3% | 4.39 | +1,493 |
| 2010-08 | 8 | 12.5% | 0.04 | −3,355 |
| 2010-09 | 4 | 75.0% | 40.32 | +915 |
| 2010-10 | 12 | 66.7% | 11.83 | +13,696 |
| 2010-11 | 29 | 51.7% | 1.16 | +1,365 |
| 2010-12 | 33 | 60.6% | 10.10 | +45,727 |
| 2011-01 | 37 | 62.2% | 2.45 | +15,271 |
| 2011-02 | 30 | 46.7% | 1.89 | +6,391 |
| 2011-03 | 33 | 60.6% | 4.32 | +15,479 |
| 2011-04 | 12 | 66.7% | 6.87 | +10,752 |
| 2011-05 | 30 | 26.7% | 0.56 | −7,679 |
| 2011-06 | 10 | 50.0% | 3.06 | +4,584 |
| 2011-07 | 4 | 25.0% | 2.23 | +252 |
| 2011-08 | 5 | 60.0% | 1.17 | +177 |
| 2011-09 | 1 | 0.0% | 0.00 | −663 |
| 2011-10 | 2 | 0.0% | 0.00 | −1,880 |
| 2011-11 | 4 | 25.0% | 0.03 | −2,369 |
| 2011-12 | 4 | 75.0% | 3.49 | +2,907 |
| 2012-01 | 5 | 80.0% | 16.18 | +8,745 |
| 2012-02 | 8 | 50.0% | 0.37 | −1,653 |
| 2012-03 | 18 | 33.3% | 0.51 | −4,289 |
| 2012-04 | 13 | 61.5% | 5.14 | +11,066 |
| 2012-05 | 15 | 33.3% | 2.66 | +7,283 |
| 2012-06 | 9 | 33.3% | 1.28 | +647 |
| 2012-07 | 9 | 44.4% | 5.64 | +7,021 |
| 2012-08 | 8 | 62.5% | 5.04 | +4,869 |
| 2012-09 | 15 | 60.0% | 2.86 | +4,414 |
| 2012-10 | 13 | 53.8% | 5.18 | +9,917 |
| 2012-11 | 2 | 50.0% | 9.26 | +571 |
| 2012-12 | 3 | 33.3% | 0.07 | −918 |
| 2013-01 | 8 | 12.5% | 0.03 | −3,268 |
| 2013-02 | 23 | 52.2% | 1.21 | +1,432 |
| 2013-03 | 19 | 52.6% | 2.20 | +5,104 |
| 2013-04 | 31 | 74.2% | 4.56 | +23,642 |
| 2013-05 | 18 | 44.4% | 3.66 | +8,882 |
| 2013-06 | 24 | 75.0% | 5.91 | +25,758 |
| 2013-07 | 8 | 75.0% | 21.86 | +9,538 |
| 2013-08 | 40 | 42.5% | 2.77 | +17,019 |
| 2013-09 | 8 | 62.5% | 4.83 | +4,821 |
| 2013-10 | 27 | 48.1% | 2.15 | +13,997 |
| 2013-11 | 25 | 40.0% | 1.65 | +5,106 |
| 2013-12 | 31 | 54.8% | 3.20 | +12,818 |
| 2014-01 | 39 | 51.3% | 1.36 | +5,856 |
| 2014-02 | 23 | 47.8% | 1.95 | +4,661 |
| 2014-03 | 36 | 41.7% | 1.36 | +4,780 |
| 2014-04 | 17 | 58.8% | 4.19 | +6,232 |
| 2014-05 | 7 | 57.1% | 126.87 | +5,162 |
| 2014-06 | 17 | 47.1% | 3.11 | +7,673 |
| 2014-07 | 17 | 41.2% | 1.03 | +152 |
| 2014-08 | 6 | 50.0% | 7.25 | +3,107 |
| 2014-09 | 12 | 16.7% | 0.36 | −3,964 |
| 2014-10 | 3 | 100.0% | inf | +2,124 |
| 2014-11 | 6 | 83.3% | 20.75 | +975 |
| 2014-12 | 17 | 58.8% | 2.93 | +6,787 |
| 2015-01 | 7 | 71.4% | 19.46 | +6,624 |
| 2015-02 | 12 | 41.7% | 2.83 | +5,123 |
| 2015-03 | 35 | 51.4% | 2.54 | +9,635 |
| 2015-04 | 24 | 50.0% | 2.63 | +9,211 |
| 2015-05 | 12 | 41.7% | 2.45 | +2,391 |
| 2015-06 | 14 | 50.0% | 4.10 | +8,713 |
| 2015-07 | 17 | 64.7% | 12.80 | +35,282 |
| 2015-08 | 2 | 50.0% | 1.86 | +633 |
| 2015-09 | 1 | 0.0% | inf | +0 |
| 2015-10 | 2 | 50.0% | 8.27 | +749 |
| 2015-11 | 6 | 33.3% | 0.43 | −1,248 |
| 2015-12 | 15 | 66.7% | 8.95 | +12,429 |
| 2016-01 | 8 | 50.0% | 2.48 | +2,960 |
| 2016-02 | 2 | 50.0% | 9.61 | +231 |
| 2016-03 | 5 | 0.0% | 0.00 | −1,873 |
| 2016-04 | 6 | 50.0% | 2.52 | +1,228 |
| 2016-05 | 4 | 25.0% | 0.48 | −846 |
| 2016-06 | 10 | 60.0% | 4.69 | +5,315 |
| 2016-07 | 3 | 100.0% | inf | +8,585 |
| 2016-08 | 29 | 51.7% | 2.04 | +3,840 |
| 2016-09 | 31 | 51.6% | 2.57 | +6,917 |
| 2016-10 | 21 | 61.9% | 3.00 | +6,356 |
| 2016-11 | 5 | 40.0% | 0.88 | −257 |
| 2016-12 | 16 | 56.2% | 1.55 | +1,776 |
| 2017-01 | 21 | 28.6% | 1.17 | +750 |
| 2017-02 | 21 | 42.9% | 1.97 | +5,180 |
| 2017-03 | 38 | 44.7% | 1.00 | +43 |
| 2017-04 | 21 | 47.6% | 3.32 | +14,502 |
| 2017-05 | 21 | 57.1% | 4.44 | +15,969 |
| 2017-06 | 20 | 65.0% | 14.66 | +22,378 |
| 2017-07 | 20 | 70.0% | 3.37 | +7,646 |
| 2017-08 | 14 | 28.6% | 0.67 | −2,124 |
| 2017-09 | 16 | 43.8% | 0.85 | −765 |
| 2017-10 | 28 | 39.3% | 0.86 | −1,310 |
| 2017-11 | 14 | 71.4% | 9.51 | +16,108 |
| 2017-12 | 19 | 26.3% | 1.75 | +6,217 |
| 2018-01 | 16 | 43.8% | 1.00 | −9 |
| 2018-02 | 20 | 35.0% | 1.01 | +96 |
| 2018-03 | 16 | 37.5% | 5.28 | +27,019 |
| 2018-04 | 9 | 66.7% | 9.52 | +10,455 |
| 2018-05 | 13 | 38.5% | 0.62 | −2,016 |
| 2018-06 | 38 | 52.6% | 2.43 | +9,658 |
| 2018-07 | 29 | 48.3% | 4.91 | +16,513 |
| 2018-08 | 17 | 29.4% | 1.12 | +459 |
| 2018-09 | 29 | 62.1% | 10.41 | +30,632 |
| 2018-10 | 17 | 70.6% | 7.27 | +12,601 |
| 2018-11 | 3 | 33.3% | 0.36 | −423 |
| 2018-12 | 12 | 33.3% | 0.53 | −2,720 |
| 2019-01 | 1 | 0.0% | inf | +0 |
| 2019-02 | 5 | 40.0% | 0.49 | −1,379 |
| 2019-03 | 22 | 59.1% | 3.48 | +15,552 |
| 2019-04 | 9 | 77.8% | 22.27 | +7,357 |
| 2019-05 | 16 | 37.5% | 3.71 | +14,189 |
| 2019-06 | 5 | 60.0% | 17.55 | +5,817 |
| 2019-07 | 16 | 25.0% | 0.97 | −168 |
| 2019-08 | 17 | 29.4% | 1.01 | +66 |
| 2019-09 | 13 | 46.2% | 2.50 | +7,314 |
| 2019-10 | 3 | 66.7% | 5.05 | +272 |
| 2019-11 | 12 | 33.3% | 0.87 | −613 |
| 2019-12 | 23 | 73.9% | 2.43 | +5,287 |
| 2020-01 | 26 | 61.5% | 5.77 | +18,539 |
| 2020-02 | 25 | 28.0% | 0.57 | −6,979 |
| 2020-03 | 8 | 25.0% | 1.12 | +798 |
| 2020-04 | 1 | 0.0% | 0.00 | −309 |
| 2020-05 | 6 | 33.3% | 0.11 | −4,089 |
| 2020-06 | 14 | 42.9% | 2.13 | +6,502 |
| 2020-07 | 6 | 33.3% | 0.55 | −855 |
| 2020-08 | 23 | 43.5% | 6.68 | +55,376 |
| 2020-09 | 15 | 73.3% | 4.77 | +19,067 |
| 2020-10 | 11 | 27.3% | 0.91 | −470 |
| 2020-11 | 11 | 27.3% | 0.98 | −116 |
| 2020-12 | 38 | 39.5% | 1.25 | +5,033 |
| 2021-01 | 55 | 60.0% | 22.89 | +351,946 |
| 2021-02 | 80 | 31.2% | 1.88 | +55,004 |
| 2021-03 | 48 | 58.3% | 6.05 | +107,476 |
| 2021-04 | 21 | 38.1% | 1.32 | +2,615 |
| 2021-05 | 12 | 50.0% | 1.12 | +304 |
| 2021-06 | 22 | 31.8% | 1.83 | +4,546 |
| 2021-07 | 15 | 60.0% | 1.45 | +3,032 |
| 2021-08 | 15 | 53.3% | 1.07 | +376 |
| 2021-09 | 13 | 46.2% | 0.47 | −1,938 |
| 2021-10 | 8 | 50.0% | 3.82 | +5,475 |
| 2021-11 | 32 | 31.2% | 0.56 | −9,454 |
| 2021-12 | 9 | 33.3% | 1.00 | +9 |
| 2022-01 | 4 | 50.0% | 4.32 | +6,049 |
| 2022-02 | 2 | 50.0% | 0.52 | −995 |
| 2022-03 | 2 | 50.0% | 4.37 | +1,379 |
| 2022-04 | 8 | 50.0% | 0.92 | −147 |
| 2022-06 | 4 | 50.0% | 10.85 | +14,603 |
| 2022-08 | 13 | 38.5% | 0.78 | −1,911 |
| 2022-09 | 5 | 20.0% | 0.14 | −1,885 |
| 2022-11 | 2 | 0.0% | 0.00 | −1,919 |
| 2022-12 | 8 | 25.0% | 0.34 | −2,016 |
| 2023-01 | 5 | 20.0% | 0.02 | −3,296 |
| 2023-02 | 7 | 42.9% | 6.19 | +9,053 |
| 2023-03 | 3 | 66.7% | 4.74 | +1,297 |
| 2023-04 | 2 | 0.0% | 0.00 | −752 |
| 2023-05 | 5 | 20.0% | 0.83 | −150 |
| 2023-06 | 7 | 57.1% | 1.88 | +1,858 |
| 2023-07 | 10 | 50.0% | 3.33 | +5,430 |
| 2023-08 | 16 | 43.8% | 0.91 | −498 |
| 2023-09 | 9 | 55.6% | 2.32 | +3,191 |
| 2023-10 | 1 | 0.0% | 0.00 | −1,222 |
| 2023-11 | 3 | 0.0% | 0.00 | −2,582 |
| 2023-12 | 10 | 50.0% | 0.57 | −2,630 |
| 2024-01 | 23 | 60.9% | 4.14 | +11,898 |
| 2024-02 | 11 | 18.2% | 0.94 | −213 |
| 2024-03 | 31 | 35.5% | 1.17 | +2,049 |
| 2024-04 | 28 | 64.3% | 8.27 | +14,840 |
| 2024-05 | 6 | 16.7% | 0.21 | −1,444 |
| 2024-06 | 5 | 100.0% | inf | +12,547 |
| 2024-07 | 2 | 100.0% | inf | +3,440 |
| 2024-08 | 7 | 28.6% | 0.32 | −2,427 |
| 2024-09 | 4 | 75.0% | 20.08 | +2,452 |
| 2024-10 | 18 | 27.8% | 0.53 | −3,858 |
| 2024-11 | 27 | 22.2% | 0.38 | −6,934 |
| 2024-12 | 21 | 52.4% | 4.77 | +21,847 |
| 2025-01 | 5 | 40.0% | 0.55 | −955 |
| 2025-02 | 17 | 29.4% | 0.30 | −8,436 |
| 2025-03 | 11 | 36.4% | 0.49 | −3,661 |
| 2025-05 | 6 | 33.3% | 0.02 | −7,582 |
| 2025-06 | 15 | 40.0% | 1.13 | +900 |
| 2025-07 | 23 | 21.7% | 0.28 | −21,778 |
| 2025-08 | 11 | 45.5% | 3.36 | +9,097 |
| 2025-09 | 17 | 11.8% | 0.11 | −10,953 |
| 2025-10 | 25 | 56.0% | 5.89 | +41,421 |
| 2025-11 | 9 | 66.7% | 11.36 | +21,286 |
| 2025-12 | 11 | 27.3% | 0.23 | −5,269 |
| 2026-01 | 14 | 57.1% | 3.35 | +7,703 |
| 2026-02 | 18 | 38.9% | 1.87 | +4,710 |
| 2026-03 | 13 | 76.9% | 5.73 | +8,725 |
| 2026-04 | 6 | 50.0% | 9.67 | +9,666 |
| 2026-05 | 23 | 56.5% | 1.40 | +6,582 |

</details>

#### Window = 12-day-low stop

half-Kelly 0.119/0.156/0.180 · total **+$1,688,064** Kelly (flat +$1,610,526) · months **178/257 (69%)** · years **20/22** · up:dn **3.7:1** · max DD **-36,495** · worst streak **6mo**

Yearly (Kelly-sized, by exit date):
| year | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005 | 185 | 47.6% | 1.82 | +40,935 |
| 2006 | 257 | 47.1% | 2.12 | +76,029 |
| 2007 | 220 | 45.9% | 2.95 | +104,458 |
| 2008 | 55 | 56.4% | 2.60 | +20,067 |
| 2009 | 64 | 46.9% | 2.00 | +18,114 |
| 2010 | 194 | 48.5% | 2.93 | +86,675 |
| 2011 | 168 | 51.2% | 1.93 | +45,105 |
| 2012 | 118 | 50.0% | 2.72 | +50,826 |
| 2013 | 263 | 52.9% | 2.78 | +116,997 |
| 2014 | 200 | 49.5% | 1.87 | +46,964 |
| 2015 | 146 | 52.7% | 4.52 | +91,438 |
| 2016 | 140 | 51.4% | 2.26 | +29,482 |
| 2017 | 253 | 47.8% | 2.29 | +82,950 |
| 2018 | 219 | 49.3% | 2.83 | +90,889 |
| 2019 | 144 | 48.6% | 2.40 | +54,985 |
| 2020 | 183 | 42.1% | 2.12 | +94,460 |
| 2021 | 329 | 45.0% | 4.41 | +525,097 |
| 2022 | 49 | 40.8% | 0.98 | −447 |
| 2023 | 77 | 41.6% | 1.37 | +10,635 |
| 2024 | 184 | 44.6% | 2.10 | +57,802 |
| 2025 | 149 | 38.9% | 0.95 | −4,699 |
| 2026 | 74 | 56.8% | 2.80 | +49,303 |

<details>
<summary>Full monthly table</summary>

| month | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005-01 | 1 | 100.0% | inf | +954 |
| 2005-02 | 11 | 18.2% | 0.18 | −3,976 |
| 2005-03 | 20 | 60.0% | 2.31 | +4,803 |
| 2005-04 | 3 | 66.7% | 1.83 | +1,507 |
| 2005-05 | 3 | 33.3% | 1.16 | +24 |
| 2005-06 | 16 | 37.5% | 0.16 | −7,999 |
| 2005-07 | 25 | 48.0% | 0.63 | −3,107 |
| 2005-08 | 53 | 45.3% | 2.11 | +14,590 |
| 2005-09 | 18 | 50.0% | 3.67 | +9,446 |
| 2005-10 | 10 | 50.0% | 15.46 | +17,458 |
| 2005-11 | 9 | 88.9% | 12.11 | +5,741 |
| 2005-12 | 16 | 37.5% | 1.43 | +1,494 |
| 2006-01 | 18 | 50.0% | 3.23 | +6,768 |
| 2006-02 | 30 | 50.0% | 1.77 | +11,206 |
| 2006-03 | 24 | 50.0% | 2.88 | +4,814 |
| 2006-04 | 32 | 62.5% | 4.01 | +24,162 |
| 2006-05 | 48 | 33.3% | 0.69 | −4,655 |
| 2006-06 | 4 | 50.0% | 9.05 | +9,546 |
| 2006-07 | 7 | 0.0% | 0.00 | −5,160 |
| 2006-08 | 9 | 55.6% | 0.43 | −508 |
| 2006-09 | 12 | 66.7% | 14.23 | +14,733 |
| 2006-10 | 16 | 12.5% | 0.24 | −3,397 |
| 2006-11 | 27 | 33.3% | 1.04 | +315 |
| 2006-12 | 30 | 76.7% | 6.30 | +18,205 |
| 2007-01 | 16 | 56.2% | 10.44 | +10,054 |
| 2007-02 | 30 | 36.7% | 1.45 | +2,931 |
| 2007-03 | 21 | 61.9% | 5.05 | +11,284 |
| 2007-04 | 20 | 45.0% | 4.17 | +9,025 |
| 2007-05 | 46 | 37.0% | 1.88 | +14,144 |
| 2007-06 | 32 | 62.5% | 2.19 | +9,376 |
| 2007-07 | 22 | 45.5% | 3.27 | +9,732 |
| 2007-08 | 5 | 40.0% | 6.31 | +11,545 |
| 2007-09 | 4 | 50.0% | 0.31 | −724 |
| 2007-10 | 12 | 50.0% | 10.25 | +31,502 |
| 2007-11 | 9 | 22.2% | 0.21 | −3,819 |
| 2007-12 | 3 | 0.0% | 0.00 | −592 |
| 2008-01 | 1 | 100.0% | inf | +2,101 |
| 2008-02 | 4 | 50.0% | 0.32 | −454 |
| 2008-03 | 3 | 66.7% | 5.18 | +1,074 |
| 2008-04 | 3 | 66.7% | 1.66 | +863 |
| 2008-05 | 5 | 40.0% | 0.70 | −380 |
| 2008-06 | 15 | 60.0% | 4.46 | +7,912 |
| 2008-07 | 8 | 75.0% | 24.42 | +10,122 |
| 2008-08 | 9 | 33.3% | 0.38 | −3,054 |
| 2008-09 | 6 | 50.0% | 2.33 | +1,864 |
| 2008-11 | 1 | 100.0% | inf | +20 |
| 2009-01 | 3 | 33.3% | 1.13 | +84 |
| 2009-02 | 1 | 100.0% | inf | +854 |
| 2009-05 | 3 | 100.0% | inf | +1,117 |
| 2009-06 | 3 | 33.3% | 7.51 | +2,557 |
| 2009-07 | 5 | 80.0% | 1.52 | +1,251 |
| 2009-08 | 9 | 0.0% | 0.00 | −3,302 |
| 2009-09 | 6 | 33.3% | 0.14 | −1,612 |
| 2009-10 | 20 | 45.0% | 2.94 | +13,736 |
| 2009-11 | 4 | 75.0% | 1.93 | +404 |
| 2009-12 | 10 | 60.0% | 2.52 | +3,025 |
| 2010-01 | 30 | 36.7% | 1.12 | +966 |
| 2010-02 | 11 | 27.3% | 1.80 | +1,057 |
| 2010-03 | 9 | 66.7% | 5.08 | +4,170 |
| 2010-04 | 18 | 38.9% | 3.63 | +11,640 |
| 2010-05 | 33 | 51.5% | 2.01 | +10,177 |
| 2010-06 | 3 | 33.3% | 4.33 | +1,474 |
| 2010-08 | 8 | 12.5% | 0.04 | −3,336 |
| 2010-09 | 5 | 80.0% | 100.57 | +2,323 |
| 2010-10 | 11 | 63.6% | 9.63 | +11,774 |
| 2010-11 | 32 | 50.0% | 0.98 | −219 |
| 2010-12 | 34 | 61.8% | 10.16 | +46,649 |
| 2011-01 | 34 | 61.8% | 2.45 | +13,865 |
| 2011-02 | 31 | 51.6% | 2.47 | +9,846 |
| 2011-03 | 32 | 62.5% | 5.10 | +15,857 |
| 2011-04 | 11 | 63.6% | 5.86 | +8,919 |
| 2011-05 | 31 | 29.0% | 0.57 | −7,580 |
| 2011-06 | 9 | 55.6% | 3.97 | +5,211 |
| 2011-07 | 4 | 25.0% | 2.23 | +253 |
| 2011-08 | 5 | 60.0% | 1.23 | +242 |
| 2011-09 | 1 | 0.0% | 0.00 | −673 |
| 2011-10 | 2 | 0.0% | 0.00 | −1,865 |
| 2011-11 | 4 | 25.0% | 0.03 | −2,398 |
| 2011-12 | 4 | 75.0% | 3.92 | +3,428 |
| 2012-01 | 5 | 80.0% | 16.55 | +8,872 |
| 2012-02 | 9 | 66.7% | 1.05 | +96 |
| 2012-03 | 18 | 33.3% | 0.44 | −5,686 |
| 2012-04 | 12 | 58.3% | 5.15 | +10,518 |
| 2012-05 | 16 | 37.5% | 2.58 | +7,210 |
| 2012-06 | 8 | 37.5% | 1.83 | +1,345 |
| 2012-07 | 9 | 44.4% | 6.59 | +8,467 |
| 2012-08 | 8 | 62.5% | 5.11 | +4,914 |
| 2012-09 | 16 | 62.5% | 5.23 | +9,989 |
| 2012-10 | 12 | 50.0% | 3.41 | +5,449 |
| 2012-11 | 2 | 50.0% | 9.15 | +564 |
| 2012-12 | 3 | 33.3% | 0.07 | −912 |
| 2013-01 | 8 | 12.5% | 0.03 | −3,271 |
| 2013-02 | 24 | 50.0% | 1.21 | +1,421 |
| 2013-03 | 21 | 57.1% | 2.71 | +6,817 |
| 2013-04 | 29 | 72.4% | 5.04 | +26,618 |
| 2013-05 | 20 | 45.0% | 4.35 | +11,287 |
| 2013-06 | 23 | 73.9% | 4.05 | +15,882 |
| 2013-07 | 8 | 75.0% | 18.79 | +8,206 |
| 2013-08 | 39 | 38.5% | 2.09 | +10,005 |
| 2013-09 | 9 | 66.7% | 5.25 | +5,431 |
| 2013-10 | 27 | 48.1% | 2.58 | +19,198 |
| 2013-11 | 24 | 37.5% | 1.31 | +2,286 |
| 2013-12 | 31 | 58.1% | 3.32 | +13,117 |
| 2014-01 | 38 | 50.0% | 1.41 | +6,250 |
| 2014-02 | 23 | 47.8% | 2.16 | +5,104 |
| 2014-03 | 36 | 41.7% | 1.36 | +4,781 |
| 2014-04 | 18 | 66.7% | 5.39 | +7,691 |
| 2014-05 | 8 | 62.5% | 128.69 | +5,251 |
| 2014-06 | 16 | 43.8% | 2.92 | +6,884 |
| 2014-07 | 18 | 44.4% | 1.33 | +1,802 |
| 2014-08 | 5 | 40.0% | 6.55 | +2,773 |
| 2014-09 | 13 | 23.1% | 0.47 | −2,984 |
| 2014-10 | 1 | 100.0% | inf | +264 |
| 2014-11 | 7 | 85.7% | 46.91 | +2,271 |
| 2014-12 | 17 | 58.8% | 2.96 | +6,877 |
| 2015-01 | 6 | 66.7% | 15.61 | +5,259 |
| 2015-02 | 13 | 46.2% | 3.21 | +6,167 |
| 2015-03 | 34 | 50.0% | 2.59 | +9,654 |
| 2015-04 | 24 | 50.0% | 2.67 | +9,254 |
| 2015-05 | 13 | 46.2% | 3.02 | +3,171 |
| 2015-06 | 13 | 46.2% | 4.03 | +7,746 |
| 2015-07 | 17 | 64.7% | 12.87 | +35,536 |
| 2015-08 | 2 | 50.0% | 1.86 | +635 |
| 2015-09 | 1 | 0.0% | inf | +0 |
| 2015-10 | 2 | 50.0% | 8.37 | +752 |
| 2015-11 | 7 | 42.9% | 0.87 | −295 |
| 2015-12 | 14 | 71.4% | 14.73 | +13,561 |
| 2016-01 | 9 | 55.6% | 2.60 | +3,201 |
| 2016-02 | 1 | 0.0% | 0.00 | −27 |
| 2016-03 | 5 | 0.0% | 0.00 | −1,893 |
| 2016-04 | 7 | 57.1% | 2.98 | +1,599 |
| 2016-05 | 4 | 25.0% | 0.48 | −848 |
| 2016-06 | 13 | 61.5% | 7.78 | +9,989 |
| 2016-07 | 1 | 100.0% | inf | +1,385 |
| 2016-08 | 29 | 48.3% | 1.14 | +534 |
| 2016-09 | 29 | 51.7% | 2.64 | +6,846 |
| 2016-10 | 21 | 61.9% | 3.37 | +6,750 |
| 2016-11 | 5 | 40.0% | 0.91 | −185 |
| 2016-12 | 16 | 56.2% | 1.73 | +2,131 |
| 2017-01 | 21 | 28.6% | 1.33 | +1,356 |
| 2017-02 | 21 | 42.9% | 1.99 | +5,260 |
| 2017-03 | 38 | 50.0% | 1.17 | +1,539 |
| 2017-04 | 21 | 47.6% | 3.59 | +15,067 |
| 2017-05 | 21 | 57.1% | 4.56 | +16,090 |
| 2017-06 | 21 | 71.4% | 15.64 | +23,766 |
| 2017-07 | 19 | 68.4% | 3.07 | +6,630 |
| 2017-08 | 14 | 28.6% | 0.70 | −1,810 |
| 2017-09 | 17 | 41.2% | 0.83 | −913 |
| 2017-10 | 29 | 41.4% | 1.09 | +876 |
| 2017-11 | 12 | 75.0% | 5.90 | +8,845 |
| 2017-12 | 19 | 26.3% | 1.76 | +6,244 |
| 2018-01 | 16 | 50.0% | 1.06 | +260 |
| 2018-02 | 20 | 35.0% | 1.02 | +157 |
| 2018-03 | 16 | 37.5% | 5.07 | +25,645 |
| 2018-04 | 9 | 66.7% | 9.45 | +10,416 |
| 2018-05 | 13 | 38.5% | 0.62 | −2,003 |
| 2018-06 | 40 | 55.0% | 3.78 | +18,629 |
| 2018-07 | 31 | 45.2% | 4.82 | +16,360 |
| 2018-08 | 17 | 29.4% | 1.17 | +634 |
| 2018-09 | 28 | 75.0% | 7.03 | +13,179 |
| 2018-10 | 14 | 64.3% | 6.14 | +10,357 |
| 2018-11 | 4 | 50.0% | 1.15 | +99 |
| 2018-12 | 11 | 27.3% | 0.48 | −2,842 |
| 2019-01 | 1 | 0.0% | inf | +0 |
| 2019-02 | 5 | 40.0% | 0.49 | −1,394 |
| 2019-03 | 23 | 60.9% | 3.50 | +15,607 |
| 2019-04 | 9 | 77.8% | 22.39 | +7,328 |
| 2019-05 | 15 | 33.3% | 4.04 | +14,496 |
| 2019-06 | 5 | 60.0% | 17.37 | +5,769 |
| 2019-07 | 16 | 25.0% | 0.98 | −110 |
| 2019-08 | 17 | 29.4% | 1.02 | +92 |
| 2019-09 | 13 | 46.2% | 2.49 | +7,313 |
| 2019-10 | 3 | 66.7% | 5.05 | +273 |
| 2019-11 | 13 | 30.8% | 0.88 | −589 |
| 2019-12 | 24 | 75.0% | 2.69 | +6,200 |
| 2020-01 | 25 | 56.0% | 4.29 | +14,852 |
| 2020-02 | 24 | 29.2% | 0.67 | −4,803 |
| 2020-03 | 8 | 25.0% | 1.12 | +800 |
| 2020-04 | 1 | 0.0% | 0.00 | −309 |
| 2020-05 | 6 | 33.3% | 0.11 | −4,089 |
| 2020-06 | 14 | 42.9% | 2.22 | +7,039 |
| 2020-07 | 8 | 50.0% | 8.07 | +13,651 |
| 2020-08 | 21 | 42.9% | 6.03 | +46,727 |
| 2020-09 | 15 | 73.3% | 4.77 | +18,970 |
| 2020-10 | 11 | 27.3% | 0.96 | −228 |
| 2020-11 | 12 | 33.3% | 1.15 | +1,017 |
| 2020-12 | 38 | 39.5% | 1.04 | +834 |
| 2021-01 | 54 | 59.3% | 23.94 | +351,309 |
| 2021-02 | 85 | 35.3% | 2.23 | +76,326 |
| 2021-03 | 47 | 59.6% | 6.00 | +96,557 |
| 2021-04 | 19 | 31.6% | 0.71 | −2,355 |
| 2021-05 | 11 | 54.5% | 1.29 | +649 |
| 2021-06 | 22 | 31.8% | 1.83 | +4,529 |
| 2021-07 | 15 | 60.0% | 1.01 | +89 |
| 2021-08 | 14 | 57.1% | 1.60 | +2,123 |
| 2021-09 | 14 | 42.9% | 0.67 | −1,273 |
| 2021-10 | 8 | 50.0% | 4.04 | +5,870 |
| 2021-11 | 31 | 29.0% | 0.53 | −9,628 |
| 2021-12 | 9 | 33.3% | 1.25 | +901 |
| 2022-01 | 5 | 60.0% | 4.75 | +6,774 |
| 2022-02 | 1 | 0.0% | 0.00 | −2,096 |
| 2022-03 | 2 | 50.0% | 4.43 | +1,388 |
| 2022-04 | 9 | 55.6% | 2.58 | +2,372 |
| 2022-06 | 3 | 33.3% | 0.01 | −1,472 |
| 2022-08 | 14 | 35.7% | 0.74 | −2,414 |
| 2022-09 | 4 | 25.0% | 0.19 | −1,298 |
| 2022-11 | 3 | 0.0% | 0.00 | −2,345 |
| 2022-12 | 8 | 50.0% | 0.47 | −1,356 |
| 2023-01 | 4 | 0.0% | 0.00 | −3,371 |
| 2023-02 | 7 | 42.9% | 6.77 | +9,155 |
| 2023-03 | 3 | 66.7% | 4.79 | +1,303 |
| 2023-04 | 2 | 0.0% | 0.00 | −754 |
| 2023-05 | 5 | 20.0% | 1.00 | −4 |
| 2023-06 | 8 | 62.5% | 2.08 | +2,287 |
| 2023-07 | 9 | 55.6% | 3.64 | +5,629 |
| 2023-08 | 17 | 41.2% | 0.97 | −176 |
| 2023-09 | 8 | 50.0% | 2.26 | +3,005 |
| 2023-10 | 1 | 0.0% | 0.00 | −1,241 |
| 2023-11 | 3 | 0.0% | 0.00 | −2,580 |
| 2023-12 | 10 | 50.0% | 0.58 | −2,619 |
| 2024-01 | 23 | 60.9% | 4.41 | +12,759 |
| 2024-02 | 11 | 18.2% | 1.09 | +303 |
| 2024-03 | 31 | 35.5% | 1.24 | +2,828 |
| 2024-04 | 28 | 67.9% | 9.31 | +16,557 |
| 2024-05 | 6 | 16.7% | 0.22 | −1,424 |
| 2024-06 | 5 | 100.0% | inf | +12,570 |
| 2024-07 | 2 | 100.0% | inf | +3,407 |
| 2024-08 | 7 | 28.6% | 0.32 | −2,456 |
| 2024-09 | 5 | 80.0% | 25.20 | +3,119 |
| 2024-10 | 17 | 29.4% | 0.51 | −4,007 |
| 2024-11 | 27 | 22.2% | 0.39 | −6,677 |
| 2024-12 | 22 | 50.0% | 4.01 | +20,821 |
| 2025-01 | 4 | 50.0% | 1.74 | +757 |
| 2025-02 | 17 | 35.3% | 0.32 | −8,032 |
| 2025-03 | 11 | 36.4% | 0.60 | −2,348 |
| 2025-05 | 6 | 33.3% | 0.02 | −7,625 |
| 2025-06 | 15 | 40.0% | 1.07 | +532 |
| 2025-07 | 24 | 33.3% | 0.42 | −17,416 |
| 2025-08 | 11 | 45.5% | 3.25 | +8,673 |
| 2025-09 | 18 | 22.2% | 0.15 | −10,279 |
| 2025-10 | 24 | 54.2% | 3.68 | +22,658 |
| 2025-11 | 9 | 66.7% | 8.37 | +15,172 |
| 2025-12 | 10 | 20.0% | 0.01 | −6,790 |
| 2026-01 | 14 | 57.1% | 3.55 | +7,865 |
| 2026-02 | 18 | 38.9% | 2.27 | +6,894 |
| 2026-03 | 14 | 78.6% | 16.18 | +28,095 |
| 2026-04 | 5 | 60.0% | 0.76 | −195 |
| 2026-05 | 23 | 56.5% | 1.41 | +6,644 |

</details>

#### Window = 11-day-low stop

half-Kelly 0.120/0.159/0.185 · total **+$1,660,420** Kelly (flat +$1,581,012) · months **178/257 (69%)** · years **21/22** · up:dn **3.7:1** · max DD **-29,310** · worst streak **6mo**

Yearly (Kelly-sized, by exit date):
| year | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005 | 186 | 46.8% | 1.73 | +36,492 |
| 2006 | 258 | 48.4% | 2.09 | +70,109 |
| 2007 | 219 | 45.2% | 2.93 | +101,104 |
| 2008 | 55 | 56.4% | 2.75 | +21,481 |
| 2009 | 64 | 46.9% | 2.16 | +19,943 |
| 2010 | 200 | 49.5% | 2.97 | +89,667 |
| 2011 | 163 | 50.9% | 2.08 | +44,602 |
| 2012 | 117 | 49.6% | 2.72 | +49,264 |
| 2013 | 265 | 52.5% | 2.66 | +112,372 |
| 2014 | 197 | 51.3% | 2.32 | +56,182 |
| 2015 | 147 | 55.8% | 4.96 | +96,737 |
| 2016 | 140 | 52.1% | 2.38 | +29,701 |
| 2017 | 253 | 47.4% | 2.23 | +77,540 |
| 2018 | 218 | 49.1% | 2.91 | +91,869 |
| 2019 | 145 | 51.7% | 2.69 | +63,307 |
| 2020 | 183 | 41.0% | 1.75 | +63,514 |
| 2021 | 329 | 45.0% | 4.42 | +519,116 |
| 2022 | 48 | 39.6% | 0.98 | −399 |
| 2023 | 78 | 43.6% | 1.23 | +6,677 |
| 2024 | 183 | 45.9% | 2.26 | +62,264 |
| 2025 | 150 | 41.3% | 1.04 | +3,944 |
| 2026 | 73 | 54.8% | 2.71 | +44,930 |

<details>
<summary>Full monthly table</summary>

| month | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005-01 | 1 | 100.0% | inf | +947 |
| 2005-02 | 12 | 25.0% | 0.23 | −3,745 |
| 2005-03 | 20 | 55.0% | 2.07 | +4,360 |
| 2005-04 | 3 | 66.7% | 1.48 | +881 |
| 2005-05 | 3 | 33.3% | 1.16 | +25 |
| 2005-06 | 16 | 31.2% | 0.07 | −8,938 |
| 2005-07 | 25 | 48.0% | 0.65 | −2,898 |
| 2005-08 | 54 | 44.4% | 2.10 | +14,206 |
| 2005-09 | 17 | 52.9% | 2.77 | +6,201 |
| 2005-10 | 9 | 55.6% | 45.96 | +18,105 |
| 2005-11 | 9 | 88.9% | 11.98 | +5,696 |
| 2005-12 | 17 | 35.3% | 1.47 | +1,651 |
| 2006-01 | 21 | 52.4% | 5.03 | +13,594 |
| 2006-02 | 30 | 50.0% | 1.30 | +4,309 |
| 2006-03 | 25 | 60.0% | 2.92 | +3,932 |
| 2006-04 | 27 | 55.6% | 3.30 | +18,291 |
| 2006-05 | 48 | 35.4% | 0.72 | −4,117 |
| 2006-06 | 4 | 50.0% | 9.05 | +9,587 |
| 2006-07 | 7 | 0.0% | 0.00 | −5,173 |
| 2006-08 | 9 | 55.6% | 0.42 | −512 |
| 2006-09 | 12 | 66.7% | 13.57 | +14,734 |
| 2006-10 | 18 | 22.2% | 0.40 | −2,621 |
| 2006-11 | 31 | 41.9% | 1.99 | +6,574 |
| 2006-12 | 26 | 76.9% | 5.39 | +11,509 |
| 2007-01 | 15 | 53.3% | 11.06 | +9,208 |
| 2007-02 | 30 | 36.7% | 1.55 | +3,568 |
| 2007-03 | 23 | 65.2% | 6.47 | +14,150 |
| 2007-04 | 20 | 45.0% | 8.95 | +22,754 |
| 2007-05 | 45 | 35.6% | 0.64 | −5,654 |
| 2007-06 | 32 | 59.4% | 2.19 | +9,402 |
| 2007-07 | 21 | 42.9% | 3.00 | +8,512 |
| 2007-08 | 5 | 40.0% | 6.39 | +11,621 |
| 2007-09 | 4 | 50.0% | 0.30 | −733 |
| 2007-10 | 12 | 50.0% | 10.42 | +32,090 |
| 2007-11 | 9 | 22.2% | 0.24 | −3,224 |
| 2007-12 | 3 | 0.0% | 0.00 | −592 |
| 2008-01 | 1 | 100.0% | inf | +2,086 |
| 2008-02 | 4 | 50.0% | 0.32 | −456 |
| 2008-03 | 3 | 66.7% | 5.18 | +1,066 |
| 2008-04 | 3 | 66.7% | 1.64 | +842 |
| 2008-05 | 5 | 40.0% | 0.71 | −369 |
| 2008-06 | 16 | 62.5% | 5.67 | +9,473 |
| 2008-07 | 7 | 71.4% | 24.50 | +10,083 |
| 2008-08 | 9 | 33.3% | 0.38 | −3,055 |
| 2008-09 | 6 | 50.0% | 2.28 | +1,792 |
| 2008-11 | 1 | 100.0% | inf | +20 |
| 2009-01 | 3 | 33.3% | 1.13 | +83 |
| 2009-02 | 1 | 100.0% | inf | +847 |
| 2009-05 | 3 | 100.0% | inf | +1,118 |
| 2009-06 | 3 | 33.3% | 7.61 | +2,593 |
| 2009-07 | 5 | 80.0% | 1.51 | +1,229 |
| 2009-08 | 9 | 0.0% | 0.00 | −3,309 |
| 2009-09 | 8 | 25.0% | 0.15 | −2,188 |
| 2009-10 | 19 | 52.6% | 3.81 | +15,462 |
| 2009-11 | 3 | 66.7% | 1.79 | +345 |
| 2009-12 | 10 | 60.0% | 2.90 | +3,763 |
| 2010-01 | 30 | 36.7% | 1.08 | +688 |
| 2010-02 | 11 | 27.3% | 2.02 | +1,204 |
| 2010-03 | 10 | 70.0% | 6.00 | +5,133 |
| 2010-04 | 21 | 42.9% | 3.54 | +11,960 |
| 2010-05 | 31 | 48.4% | 1.91 | +9,229 |
| 2010-06 | 1 | 100.0% | inf | +1,924 |
| 2010-08 | 8 | 25.0% | 0.09 | −2,816 |
| 2010-09 | 6 | 66.7% | 13.68 | +2,177 |
| 2010-10 | 13 | 61.5% | 5.33 | +10,996 |
| 2010-11 | 35 | 48.6% | 1.56 | +5,583 |
| 2010-12 | 34 | 64.7% | 10.79 | +43,587 |
| 2011-01 | 31 | 58.1% | 2.21 | +11,630 |
| 2011-02 | 32 | 53.1% | 2.34 | +9,020 |
| 2011-03 | 32 | 62.5% | 6.46 | +16,894 |
| 2011-04 | 9 | 55.6% | 2.90 | +3,471 |
| 2011-05 | 31 | 25.8% | 0.75 | −3,393 |
| 2011-06 | 7 | 71.4% | 8.36 | +6,141 |
| 2011-07 | 5 | 40.0% | 4.21 | +654 |
| 2011-08 | 4 | 50.0% | 0.86 | −148 |
| 2011-09 | 1 | 0.0% | 0.00 | −681 |
| 2011-10 | 3 | 33.3% | 0.08 | −1,727 |
| 2011-11 | 4 | 50.0% | 2.34 | +1,117 |
| 2011-12 | 4 | 75.0% | 2.40 | +1,626 |
| 2012-01 | 5 | 80.0% | 16.62 | +8,950 |
| 2012-02 | 9 | 66.7% | 1.01 | +15 |
| 2012-03 | 17 | 29.4% | 0.15 | −8,387 |
| 2012-04 | 13 | 61.5% | 6.32 | +12,702 |
| 2012-05 | 15 | 33.3% | 2.07 | +4,860 |
| 2012-06 | 8 | 37.5% | 1.98 | +1,581 |
| 2012-07 | 9 | 44.4% | 7.78 | +8,832 |
| 2012-08 | 8 | 62.5% | 5.06 | +4,871 |
| 2012-09 | 16 | 62.5% | 5.80 | +10,250 |
| 2012-10 | 13 | 53.8% | 4.21 | +6,572 |
| 2012-11 | 1 | 0.0% | 0.00 | −69 |
| 2012-12 | 3 | 33.3% | 0.07 | −913 |
| 2013-01 | 8 | 12.5% | 0.03 | −3,281 |
| 2013-02 | 26 | 50.0% | 0.93 | −656 |
| 2013-03 | 20 | 60.0% | 2.33 | +4,248 |
| 2013-04 | 28 | 71.4% | 5.05 | +26,767 |
| 2013-05 | 20 | 50.0% | 4.66 | +11,837 |
| 2013-06 | 23 | 73.9% | 5.41 | +18,491 |
| 2013-07 | 8 | 75.0% | 18.66 | +8,158 |
| 2013-08 | 40 | 37.5% | 2.11 | +10,080 |
| 2013-09 | 9 | 66.7% | 5.20 | +5,433 |
| 2013-10 | 26 | 46.2% | 2.41 | +17,234 |
| 2013-11 | 27 | 40.7% | 1.49 | +3,974 |
| 2013-12 | 30 | 53.3% | 2.59 | +10,086 |
| 2014-01 | 39 | 56.4% | 3.12 | +13,802 |
| 2014-02 | 22 | 45.5% | 2.68 | +6,694 |
| 2014-03 | 34 | 41.2% | 1.25 | +3,183 |
| 2014-04 | 17 | 70.6% | 7.03 | +8,164 |
| 2014-05 | 8 | 62.5% | 131.61 | +5,332 |
| 2014-06 | 17 | 47.1% | 3.19 | +7,319 |
| 2014-07 | 18 | 44.4% | 1.39 | +2,059 |
| 2014-08 | 4 | 50.0% | 9.58 | +2,957 |
| 2014-09 | 13 | 23.1% | 0.47 | −2,968 |
| 2014-10 | 1 | 100.0% | inf | +262 |
| 2014-11 | 8 | 87.5% | 59.17 | +2,857 |
| 2014-12 | 16 | 56.2% | 2.86 | +6,522 |
| 2015-01 | 8 | 75.0% | 22.05 | +7,521 |
| 2015-02 | 11 | 45.5% | 2.46 | +3,522 |
| 2015-03 | 34 | 50.0% | 2.69 | +9,937 |
| 2015-04 | 24 | 54.2% | 2.79 | +9,901 |
| 2015-05 | 15 | 53.3% | 4.72 | +5,811 |
| 2015-06 | 13 | 53.8% | 5.52 | +8,577 |
| 2015-07 | 15 | 66.7% | 13.37 | +34,433 |
| 2015-08 | 2 | 50.0% | 1.86 | +630 |
| 2015-09 | 1 | 0.0% | inf | +0 |
| 2015-10 | 2 | 50.0% | 8.28 | +745 |
| 2015-11 | 9 | 55.6% | 4.36 | +7,372 |
| 2015-12 | 13 | 69.2% | 9.37 | +8,288 |
| 2016-01 | 8 | 50.0% | 1.09 | +174 |
| 2016-02 | 1 | 0.0% | 0.00 | −27 |
| 2016-03 | 5 | 0.0% | 0.00 | −1,906 |
| 2016-04 | 7 | 57.1% | 3.01 | +1,613 |
| 2016-05 | 4 | 25.0% | 0.54 | −754 |
| 2016-06 | 13 | 61.5% | 7.86 | +10,209 |
| 2016-07 | 2 | 50.0% | 70.52 | +1,356 |
| 2016-08 | 33 | 51.5% | 1.37 | +1,467 |
| 2016-09 | 27 | 59.3% | 3.34 | +8,193 |
| 2016-10 | 21 | 61.9% | 4.36 | +8,680 |
| 2016-11 | 3 | 33.3% | 0.01 | −1,310 |
| 2016-12 | 16 | 50.0% | 1.87 | +2,006 |
| 2017-01 | 21 | 28.6% | 1.30 | +1,257 |
| 2017-02 | 24 | 41.7% | 1.98 | +5,053 |
| 2017-03 | 40 | 55.0% | 1.88 | +7,634 |
| 2017-04 | 18 | 44.4% | 4.28 | +15,160 |
| 2017-05 | 20 | 55.0% | 4.81 | +16,599 |
| 2017-06 | 21 | 71.4% | 9.38 | +13,511 |
| 2017-07 | 17 | 64.7% | 2.27 | +4,091 |
| 2017-08 | 18 | 38.9% | 1.09 | +581 |
| 2017-09 | 14 | 28.6% | 0.37 | −3,477 |
| 2017-10 | 28 | 42.9% | 1.16 | +1,471 |
| 2017-11 | 14 | 71.4% | 6.99 | +15,094 |
| 2017-12 | 18 | 22.2% | 1.07 | +567 |
| 2018-01 | 16 | 50.0% | 1.05 | +248 |
| 2018-02 | 19 | 31.6% | 0.77 | −1,678 |
| 2018-03 | 17 | 41.2% | 5.16 | +26,132 |
| 2018-04 | 9 | 66.7% | 10.78 | +11,467 |
| 2018-05 | 14 | 42.9% | 0.89 | −556 |
| 2018-06 | 41 | 56.1% | 4.15 | +21,107 |
| 2018-07 | 29 | 37.9% | 4.70 | +13,044 |
| 2018-08 | 17 | 29.4% | 1.21 | +757 |
| 2018-09 | 27 | 74.1% | 7.08 | +12,742 |
| 2018-10 | 14 | 64.3% | 6.99 | +10,670 |
| 2018-11 | 4 | 50.0% | 1.24 | +159 |
| 2018-12 | 11 | 36.4% | 0.57 | −2,224 |
| 2019-01 | 1 | 0.0% | inf | +0 |
| 2019-02 | 5 | 40.0% | 0.58 | −984 |
| 2019-03 | 25 | 64.0% | 4.07 | +19,138 |
| 2019-04 | 9 | 66.7% | 12.22 | +6,319 |
| 2019-05 | 14 | 42.9% | 3.90 | +12,684 |
| 2019-06 | 4 | 75.0% | 24.05 | +7,799 |
| 2019-07 | 17 | 35.3% | 1.70 | +4,148 |
| 2019-08 | 17 | 29.4% | 0.72 | −1,339 |
| 2019-09 | 12 | 41.7% | 2.37 | +6,660 |
| 2019-10 | 3 | 66.7% | 5.05 | +271 |
| 2019-11 | 14 | 35.7% | 0.98 | −76 |
| 2019-12 | 24 | 79.2% | 3.51 | +8,689 |
| 2020-01 | 24 | 54.2% | 4.16 | +13,925 |
| 2020-02 | 24 | 29.2% | 0.73 | −3,833 |
| 2020-03 | 8 | 25.0% | 1.12 | +794 |
| 2020-04 | 1 | 0.0% | 0.00 | −307 |
| 2020-05 | 7 | 28.6% | 0.26 | −4,196 |
| 2020-06 | 15 | 46.7% | 2.63 | +9,489 |
| 2020-07 | 9 | 55.6% | 8.79 | +15,001 |
| 2020-08 | 18 | 33.3% | 1.98 | +9,101 |
| 2020-09 | 17 | 76.5% | 5.90 | +24,644 |
| 2020-10 | 11 | 18.2% | 1.04 | +225 |
| 2020-11 | 11 | 36.4% | 0.71 | −1,401 |
| 2020-12 | 38 | 36.8% | 1.00 | +69 |
| 2021-01 | 55 | 60.0% | 24.28 | +356,783 |
| 2021-02 | 88 | 37.5% | 2.28 | +80,077 |
| 2021-03 | 42 | 54.8% | 4.81 | +73,811 |
| 2021-04 | 20 | 30.0% | 0.71 | −2,324 |
| 2021-05 | 10 | 60.0% | 1.80 | +1,360 |
| 2021-06 | 24 | 29.2% | 1.86 | +4,934 |
| 2021-07 | 15 | 66.7% | 1.09 | +676 |
| 2021-08 | 14 | 50.0% | 1.01 | +17 |
| 2021-09 | 12 | 33.3% | 0.77 | −719 |
| 2021-10 | 9 | 55.6% | 7.63 | +12,806 |
| 2021-11 | 30 | 30.0% | 0.33 | −13,236 |
| 2021-12 | 10 | 50.0% | 2.44 | +4,931 |
| 2022-01 | 4 | 50.0% | 6.02 | +5,579 |
| 2022-02 | 1 | 0.0% | 0.00 | −2,081 |
| 2022-03 | 2 | 50.0% | 4.37 | +1,373 |
| 2022-04 | 9 | 55.6% | 2.59 | +2,402 |
| 2022-06 | 3 | 33.3% | 0.34 | −968 |
| 2022-08 | 14 | 35.7% | 0.74 | −2,425 |
| 2022-09 | 4 | 25.0% | 0.19 | −1,289 |
| 2022-11 | 3 | 0.0% | 0.00 | −1,995 |
| 2022-12 | 8 | 50.0% | 0.55 | −996 |
| 2023-01 | 5 | 20.0% | 0.26 | −2,346 |
| 2023-02 | 6 | 33.3% | 4.33 | +5,274 |
| 2023-03 | 3 | 66.7% | 4.81 | +1,313 |
| 2023-04 | 2 | 0.0% | 0.00 | −748 |
| 2023-05 | 5 | 20.0% | 1.00 | −4 |
| 2023-06 | 9 | 66.7% | 2.20 | +2,537 |
| 2023-07 | 9 | 55.6% | 3.81 | +5,967 |
| 2023-08 | 18 | 38.9% | 0.64 | −2,115 |
| 2023-09 | 6 | 66.7% | 2.79 | +2,907 |
| 2023-10 | 1 | 0.0% | 0.00 | −1,256 |
| 2023-11 | 3 | 0.0% | 0.00 | −2,570 |
| 2023-12 | 11 | 54.5% | 0.63 | −2,282 |
| 2024-01 | 22 | 68.2% | 5.70 | +13,935 |
| 2024-02 | 11 | 18.2% | 1.13 | +418 |
| 2024-03 | 34 | 38.2% | 1.41 | +4,880 |
| 2024-04 | 25 | 72.0% | 14.52 | +15,787 |
| 2024-05 | 8 | 37.5% | 2.36 | +2,470 |
| 2024-06 | 4 | 100.0% | inf | +10,464 |
| 2024-07 | 1 | 100.0% | inf | +1,096 |
| 2024-08 | 7 | 28.6% | 0.38 | −1,870 |
| 2024-09 | 5 | 80.0% | 25.34 | +3,113 |
| 2024-10 | 17 | 29.4% | 0.50 | −4,055 |
| 2024-11 | 27 | 22.2% | 0.52 | −4,986 |
| 2024-12 | 22 | 50.0% | 4.04 | +21,013 |
| 2025-01 | 5 | 60.0% | 2.34 | +1,375 |
| 2025-02 | 18 | 44.4% | 0.44 | −6,099 |
| 2025-03 | 9 | 33.3% | 0.55 | −2,328 |
| 2025-05 | 6 | 33.3% | 0.02 | −7,364 |
| 2025-06 | 17 | 41.2% | 1.23 | +1,631 |
| 2025-07 | 22 | 45.5% | 0.47 | −14,435 |
| 2025-08 | 11 | 45.5% | 3.27 | +8,713 |
| 2025-09 | 20 | 25.0% | 0.24 | −9,429 |
| 2025-10 | 23 | 56.5% | 4.61 | +25,885 |
| 2025-11 | 8 | 50.0% | 6.48 | +13,421 |
| 2025-12 | 11 | 18.2% | 0.01 | −7,427 |
| 2026-01 | 15 | 46.7% | 3.13 | +6,673 |
| 2026-02 | 18 | 44.4% | 4.04 | +13,637 |
| 2026-03 | 13 | 76.9% | 15.70 | +24,257 |
| 2026-04 | 5 | 60.0% | 0.76 | −204 |
| 2026-05 | 22 | 54.5% | 1.03 | +567 |

</details>

#### Window = 10-day-low stop

half-Kelly 0.119/0.160/0.186 · total **+$1,595,139** Kelly (flat +$1,513,249) · months **174/257 (68%)** · years **21/22** · up:dn **3.8:1** · max DD **-31,570** · worst streak **5mo**

Yearly (Kelly-sized, by exit date):
| year | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005 | 187 | 47.1% | 1.63 | +31,416 |
| 2006 | 259 | 48.6% | 2.25 | +79,046 |
| 2007 | 217 | 45.6% | 2.99 | +99,487 |
| 2008 | 55 | 58.2% | 2.73 | +21,157 |
| 2009 | 64 | 48.4% | 2.16 | +19,475 |
| 2010 | 203 | 49.3% | 2.74 | +79,392 |
| 2011 | 160 | 50.6% | 2.09 | +44,313 |
| 2012 | 118 | 48.3% | 2.61 | +45,371 |
| 2013 | 265 | 51.3% | 2.40 | +94,287 |
| 2014 | 196 | 53.6% | 2.38 | +56,091 |
| 2015 | 149 | 55.7% | 4.38 | +82,763 |
| 2016 | 140 | 53.6% | 2.61 | +30,855 |
| 2017 | 253 | 47.8% | 2.23 | +75,640 |
| 2018 | 216 | 50.5% | 2.95 | +89,054 |
| 2019 | 145 | 52.4% | 2.57 | +58,189 |
| 2020 | 186 | 41.9% | 1.83 | +67,740 |
| 2021 | 326 | 46.0% | 4.43 | +512,413 |
| 2022 | 48 | 39.6% | 0.99 | −140 |
| 2023 | 78 | 42.3% | 1.27 | +7,356 |
| 2024 | 183 | 47.0% | 2.06 | +51,210 |
| 2025 | 150 | 41.3% | 1.07 | +5,902 |
| 2026 | 73 | 53.4% | 2.66 | +44,123 |

<details>
<summary>Full monthly table</summary>

| month | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005-01 | 1 | 100.0% | inf | +943 |
| 2005-02 | 12 | 25.0% | 0.23 | −3,757 |
| 2005-03 | 21 | 52.4% | 1.93 | +3,821 |
| 2005-04 | 2 | 50.0% | 1.43 | +786 |
| 2005-05 | 3 | 33.3% | 1.17 | +25 |
| 2005-06 | 17 | 41.2% | 0.11 | −7,989 |
| 2005-07 | 28 | 53.6% | 0.94 | −467 |
| 2005-08 | 53 | 45.3% | 2.31 | +16,328 |
| 2005-09 | 15 | 46.7% | 1.06 | +221 |
| 2005-10 | 9 | 55.6% | 47.07 | +18,475 |
| 2005-11 | 9 | 77.8% | 1.71 | +1,234 |
| 2005-12 | 17 | 35.3% | 1.51 | +1,796 |
| 2006-01 | 21 | 52.4% | 5.20 | +14,175 |
| 2006-02 | 31 | 51.6% | 1.05 | +701 |
| 2006-03 | 24 | 58.3% | 3.30 | +4,721 |
| 2006-04 | 26 | 53.8% | 4.00 | +23,377 |
| 2006-05 | 48 | 37.5% | 0.83 | −2,365 |
| 2006-06 | 4 | 25.0% | 8.45 | +9,495 |
| 2006-07 | 7 | 0.0% | 0.00 | −5,186 |
| 2006-08 | 9 | 55.6% | 0.42 | −515 |
| 2006-09 | 12 | 66.7% | 13.53 | +14,672 |
| 2006-10 | 18 | 22.2% | 0.42 | −2,513 |
| 2006-11 | 32 | 43.8% | 2.08 | +7,211 |
| 2006-12 | 27 | 77.8% | 8.75 | +15,272 |
| 2007-01 | 15 | 53.3% | 11.09 | +9,227 |
| 2007-02 | 32 | 34.4% | 1.16 | +1,035 |
| 2007-03 | 19 | 68.4% | 7.43 | +12,523 |
| 2007-04 | 21 | 42.9% | 7.35 | +22,248 |
| 2007-05 | 44 | 40.9% | 0.71 | −4,127 |
| 2007-06 | 33 | 60.6% | 2.37 | +10,799 |
| 2007-07 | 20 | 40.0% | 2.73 | +7,328 |
| 2007-08 | 5 | 40.0% | 6.68 | +11,763 |
| 2007-09 | 4 | 50.0% | 0.30 | −737 |
| 2007-10 | 12 | 50.0% | 10.46 | +32,270 |
| 2007-11 | 9 | 22.2% | 0.31 | −2,249 |
| 2007-12 | 3 | 0.0% | 0.00 | −592 |
| 2008-01 | 1 | 100.0% | inf | +2,075 |
| 2008-02 | 4 | 50.0% | 0.32 | −458 |
| 2008-03 | 3 | 66.7% | 5.18 | +1,060 |
| 2008-04 | 3 | 66.7% | 1.61 | +796 |
| 2008-05 | 6 | 33.3% | 0.87 | −160 |
| 2008-06 | 15 | 66.7% | 5.86 | +9,842 |
| 2008-07 | 8 | 75.0% | 26.49 | +10,882 |
| 2008-08 | 8 | 25.0% | 0.02 | −4,809 |
| 2008-09 | 6 | 66.7% | 2.39 | +1,908 |
| 2008-11 | 1 | 100.0% | inf | +20 |
| 2009-01 | 3 | 33.3% | 1.13 | +83 |
| 2009-02 | 1 | 100.0% | inf | +843 |
| 2009-05 | 3 | 100.0% | inf | +1,120 |
| 2009-06 | 3 | 33.3% | 7.66 | +2,609 |
| 2009-07 | 5 | 80.0% | 1.50 | +1,209 |
| 2009-08 | 9 | 0.0% | 0.00 | −3,326 |
| 2009-09 | 10 | 30.0% | 0.19 | −2,280 |
| 2009-10 | 17 | 58.8% | 3.80 | +14,553 |
| 2009-11 | 3 | 66.7% | 76.10 | +769 |
| 2009-12 | 10 | 60.0% | 2.98 | +3,895 |
| 2010-01 | 30 | 40.0% | 1.19 | +1,545 |
| 2010-02 | 12 | 25.0% | 1.62 | +917 |
| 2010-03 | 11 | 72.7% | 7.63 | +6,817 |
| 2010-04 | 22 | 45.5% | 3.99 | +14,053 |
| 2010-05 | 29 | 44.8% | 1.66 | +6,654 |
| 2010-08 | 8 | 25.0% | 0.09 | −2,820 |
| 2010-09 | 6 | 66.7% | 16.80 | +2,211 |
| 2010-10 | 13 | 61.5% | 5.31 | +11,010 |
| 2010-11 | 38 | 50.0% | 1.77 | +7,807 |
| 2010-12 | 34 | 61.8% | 7.90 | +31,200 |
| 2011-01 | 30 | 60.0% | 2.38 | +12,921 |
| 2011-02 | 30 | 50.0% | 1.95 | +6,351 |
| 2011-03 | 33 | 60.6% | 6.03 | +16,345 |
| 2011-04 | 8 | 50.0% | 2.88 | +3,421 |
| 2011-05 | 31 | 29.0% | 0.81 | −2,457 |
| 2011-06 | 7 | 71.4% | 8.14 | +5,985 |
| 2011-07 | 5 | 40.0% | 4.66 | +744 |
| 2011-08 | 4 | 50.0% | 1.29 | +200 |
| 2011-09 | 1 | 0.0% | 0.00 | −685 |
| 2011-10 | 3 | 33.3% | 0.18 | −1,541 |
| 2011-11 | 4 | 50.0% | 2.37 | +1,132 |
| 2011-12 | 4 | 75.0% | 2.64 | +1,899 |
| 2012-01 | 6 | 83.3% | 19.38 | +10,577 |
| 2012-02 | 9 | 66.7% | 1.06 | +106 |
| 2012-03 | 19 | 36.8% | 0.29 | −6,730 |
| 2012-04 | 11 | 54.5% | 5.61 | +10,265 |
| 2012-05 | 15 | 26.7% | 1.22 | +1,014 |
| 2012-06 | 7 | 28.6% | 1.40 | +648 |
| 2012-07 | 9 | 44.4% | 7.84 | +8,881 |
| 2012-08 | 8 | 62.5% | 5.03 | +4,841 |
| 2012-09 | 16 | 62.5% | 5.82 | +10,275 |
| 2012-10 | 13 | 53.8% | 4.19 | +6,543 |
| 2012-11 | 1 | 0.0% | 0.00 | −68 |
| 2012-12 | 4 | 25.0% | 0.06 | −980 |
| 2013-01 | 8 | 12.5% | 0.03 | −3,230 |
| 2013-02 | 26 | 50.0% | 0.95 | −461 |
| 2013-03 | 25 | 64.0% | 5.04 | +12,227 |
| 2013-04 | 23 | 65.2% | 3.76 | +18,283 |
| 2013-05 | 23 | 56.5% | 5.25 | +13,740 |
| 2013-06 | 20 | 70.0% | 5.19 | +17,600 |
| 2013-07 | 8 | 75.0% | 17.19 | +7,476 |
| 2013-08 | 42 | 42.9% | 2.22 | +10,624 |
| 2013-09 | 10 | 60.0% | 8.60 | +10,555 |
| 2013-10 | 25 | 40.0% | 0.83 | −2,250 |
| 2013-11 | 28 | 32.1% | 1.03 | +277 |
| 2013-12 | 27 | 55.6% | 2.89 | +9,445 |
| 2014-01 | 40 | 57.5% | 3.09 | +13,680 |
| 2014-02 | 21 | 47.6% | 2.87 | +7,120 |
| 2014-03 | 34 | 44.1% | 1.15 | +1,820 |
| 2014-04 | 16 | 68.8% | 6.48 | +7,298 |
| 2014-05 | 8 | 62.5% | 133.47 | +5,380 |
| 2014-06 | 17 | 47.1% | 3.36 | +7,606 |
| 2014-07 | 20 | 55.0% | 1.56 | +2,962 |
| 2014-08 | 3 | 66.7% | 31.46 | +4,233 |
| 2014-09 | 13 | 23.1% | 0.13 | −4,874 |
| 2014-11 | 8 | 87.5% | 60.81 | +2,922 |
| 2014-12 | 16 | 62.5% | 4.12 | +7,944 |
| 2015-01 | 8 | 75.0% | 27.39 | +9,379 |
| 2015-02 | 11 | 45.5% | 2.45 | +3,496 |
| 2015-03 | 35 | 54.3% | 4.54 | +20,570 |
| 2015-04 | 24 | 54.2% | 2.89 | +10,279 |
| 2015-05 | 17 | 58.8% | 7.55 | +10,175 |
| 2015-06 | 11 | 45.5% | 2.92 | +3,615 |
| 2015-07 | 14 | 64.3% | 3.76 | +7,601 |
| 2015-08 | 2 | 50.0% | 1.86 | +627 |
| 2015-09 | 1 | 0.0% | inf | +0 |
| 2015-10 | 2 | 50.0% | 8.20 | +740 |
| 2015-11 | 12 | 50.0% | 3.65 | +7,797 |
| 2015-12 | 12 | 66.7% | 18.35 | +8,482 |
| 2016-01 | 6 | 66.7% | 2.32 | +1,262 |
| 2016-02 | 1 | 0.0% | 0.00 | −27 |
| 2016-03 | 5 | 0.0% | 0.00 | −1,912 |
| 2016-04 | 7 | 57.1% | 3.05 | +1,632 |
| 2016-05 | 5 | 40.0% | 0.96 | −60 |
| 2016-06 | 13 | 61.5% | 7.33 | +9,472 |
| 2016-07 | 2 | 50.0% | 11.26 | +1,247 |
| 2016-08 | 35 | 54.3% | 1.54 | +2,137 |
| 2016-09 | 25 | 52.0% | 3.36 | +7,007 |
| 2016-10 | 20 | 60.0% | 4.43 | +8,424 |
| 2016-11 | 3 | 33.3% | 0.01 | −1,315 |
| 2016-12 | 18 | 61.1% | 2.87 | +2,989 |
| 2017-01 | 20 | 20.0% | 0.70 | −1,309 |
| 2017-02 | 24 | 45.8% | 2.24 | +5,887 |
| 2017-03 | 41 | 53.7% | 2.21 | +11,704 |
| 2017-04 | 16 | 43.8% | 3.78 | +10,843 |
| 2017-05 | 22 | 59.1% | 5.37 | +19,029 |
| 2017-06 | 21 | 71.4% | 9.05 | +12,900 |
| 2017-07 | 17 | 58.8% | 2.23 | +3,918 |
| 2017-08 | 17 | 41.2% | 0.95 | −334 |
| 2017-09 | 14 | 28.6% | 0.37 | −3,469 |
| 2017-10 | 28 | 46.4% | 1.12 | +905 |
| 2017-11 | 14 | 71.4% | 6.86 | +14,439 |
| 2017-12 | 19 | 26.3% | 1.14 | +1,127 |
| 2018-01 | 15 | 53.3% | 1.65 | +2,235 |
| 2018-02 | 20 | 30.0% | 0.97 | −240 |
| 2018-03 | 18 | 38.9% | 5.43 | +27,679 |
| 2018-04 | 6 | 66.7% | 6.03 | +3,086 |
| 2018-05 | 15 | 40.0% | 0.84 | −927 |
| 2018-06 | 41 | 58.5% | 4.06 | +20,622 |
| 2018-07 | 28 | 42.9% | 6.46 | +14,017 |
| 2018-08 | 18 | 33.3% | 1.64 | +2,281 |
| 2018-09 | 31 | 77.4% | 6.36 | +13,491 |
| 2018-10 | 9 | 66.7% | 7.41 | +8,890 |
| 2018-11 | 4 | 50.0% | 1.19 | +125 |
| 2018-12 | 11 | 36.4% | 0.57 | −2,204 |
| 2019-01 | 1 | 0.0% | inf | +0 |
| 2019-02 | 6 | 50.0% | 1.06 | +144 |
| 2019-03 | 25 | 64.0% | 4.35 | +20,354 |
| 2019-04 | 8 | 75.0% | 26.28 | +6,618 |
| 2019-05 | 15 | 46.7% | 4.07 | +13,386 |
| 2019-06 | 5 | 80.0% | 29.86 | +9,715 |
| 2019-07 | 17 | 35.3% | 1.73 | +4,294 |
| 2019-08 | 17 | 29.4% | 0.79 | −973 |
| 2019-09 | 10 | 30.0% | 0.05 | −4,599 |
| 2019-10 | 3 | 66.7% | 5.05 | +270 |
| 2019-11 | 14 | 35.7% | 1.02 | +118 |
| 2019-12 | 24 | 79.2% | 3.56 | +8,863 |
| 2020-01 | 25 | 56.0% | 4.78 | +16,292 |
| 2020-02 | 23 | 26.1% | 0.46 | −7,254 |
| 2020-03 | 8 | 25.0% | 1.12 | +790 |
| 2020-04 | 1 | 0.0% | 0.00 | −306 |
| 2020-05 | 9 | 44.4% | 0.51 | −2,767 |
| 2020-06 | 13 | 46.2% | 2.52 | +8,698 |
| 2020-07 | 10 | 60.0% | 13.18 | +23,411 |
| 2020-08 | 18 | 33.3% | 2.33 | +10,776 |
| 2020-09 | 16 | 75.0% | 3.92 | +14,688 |
| 2020-10 | 11 | 18.2% | 1.28 | +1,607 |
| 2020-11 | 12 | 41.7% | 0.73 | −1,301 |
| 2020-12 | 40 | 37.5% | 1.15 | +3,105 |
| 2021-01 | 58 | 60.3% | 22.93 | +363,832 |
| 2021-02 | 87 | 41.4% | 2.60 | +97,080 |
| 2021-03 | 37 | 48.6% | 3.21 | +42,789 |
| 2021-04 | 20 | 35.0% | 0.73 | −2,147 |
| 2021-05 | 12 | 58.3% | 5.29 | +7,374 |
| 2021-06 | 24 | 33.3% | 0.99 | −41 |
| 2021-07 | 13 | 61.5% | 0.74 | −1,916 |
| 2021-08 | 14 | 50.0% | 1.12 | +398 |
| 2021-09 | 14 | 42.9% | 1.33 | +1,029 |
| 2021-10 | 8 | 62.5% | 7.78 | +12,821 |
| 2021-11 | 29 | 27.6% | 0.24 | −14,938 |
| 2021-12 | 10 | 50.0% | 3.72 | +6,132 |
| 2022-01 | 4 | 50.0% | 6.99 | +5,763 |
| 2022-02 | 1 | 0.0% | 0.00 | −2,070 |
| 2022-03 | 2 | 50.0% | 4.33 | +1,362 |
| 2022-04 | 9 | 55.6% | 2.60 | +2,417 |
| 2022-06 | 3 | 33.3% | 0.34 | −963 |
| 2022-08 | 14 | 35.7% | 0.74 | −2,423 |
| 2022-09 | 4 | 25.0% | 0.19 | −1,282 |
| 2022-11 | 3 | 0.0% | 0.00 | −2,004 |
| 2022-12 | 8 | 50.0% | 0.56 | −940 |
| 2023-01 | 6 | 33.3% | 2.32 | +4,027 |
| 2023-02 | 5 | 20.0% | 0.59 | −656 |
| 2023-03 | 3 | 66.7% | 4.77 | +1,305 |
| 2023-04 | 3 | 0.0% | 0.00 | −855 |
| 2023-05 | 4 | 25.0% | 1.15 | +116 |
| 2023-06 | 10 | 60.0% | 2.53 | +3,278 |
| 2023-07 | 9 | 55.6% | 3.95 | +6,057 |
| 2023-08 | 17 | 35.3% | 0.50 | −2,593 |
| 2023-09 | 6 | 66.7% | 2.76 | +2,870 |
| 2023-10 | 1 | 0.0% | 0.00 | −1,263 |
| 2023-11 | 3 | 0.0% | 0.00 | −2,564 |
| 2023-12 | 11 | 54.5% | 0.62 | −2,365 |
| 2024-01 | 23 | 69.6% | 5.79 | +14,211 |
| 2024-02 | 10 | 10.0% | 0.54 | −1,510 |
| 2024-03 | 34 | 41.2% | 1.58 | +6,263 |
| 2024-04 | 25 | 76.0% | 18.92 | +17,248 |
| 2024-05 | 8 | 37.5% | 2.34 | +2,452 |
| 2024-06 | 4 | 100.0% | inf | +10,431 |
| 2024-07 | 1 | 100.0% | inf | +1,100 |
| 2024-08 | 7 | 28.6% | 0.38 | −1,875 |
| 2024-09 | 7 | 71.4% | 12.62 | +9,559 |
| 2024-10 | 16 | 31.2% | 0.55 | −3,380 |
| 2024-11 | 27 | 22.2% | 0.52 | −4,841 |
| 2024-12 | 21 | 47.6% | 1.22 | +1,550 |
| 2025-01 | 5 | 60.0% | 2.34 | +1,375 |
| 2025-02 | 21 | 47.6% | 0.74 | −2,793 |
| 2025-03 | 6 | 33.3% | 0.08 | −3,562 |
| 2025-05 | 6 | 33.3% | 0.04 | −4,915 |
| 2025-06 | 19 | 42.1% | 1.41 | +3,009 |
| 2025-07 | 21 | 42.9% | 0.43 | −15,699 |
| 2025-08 | 10 | 40.0% | 3.04 | +7,100 |
| 2025-09 | 20 | 25.0% | 0.24 | −9,415 |
| 2025-10 | 24 | 58.3% | 5.85 | +34,661 |
| 2025-11 | 7 | 42.9% | 2.47 | +3,590 |
| 2025-12 | 11 | 18.2% | 0.01 | −7,450 |
| 2026-01 | 15 | 46.7% | 3.14 | +6,682 |
| 2026-02 | 19 | 42.1% | 4.05 | +14,015 |
| 2026-03 | 12 | 75.0% | 14.09 | +23,103 |
| 2026-04 | 5 | 60.0% | 0.75 | −210 |
| 2026-05 | 22 | 54.5% | 1.03 | +532 |

</details>

#### Window = 9-day-low stop

half-Kelly 0.123/0.163/0.190 · total **+$1,596,323** Kelly (flat +$1,517,491) · months **173/257 (67%)** · years **22/22** · up:dn **4.1:1** · max DD **-26,281** · worst streak **4mo**

Yearly (Kelly-sized, by exit date):
| year | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005 | 188 | 48.9% | 1.51 | +24,668 |
| 2006 | 260 | 48.5% | 2.23 | +75,306 |
| 2007 | 215 | 46.0% | 2.79 | +86,236 |
| 2008 | 55 | 56.4% | 2.79 | +19,884 |
| 2009 | 65 | 49.2% | 2.21 | +20,203 |
| 2010 | 203 | 51.2% | 2.75 | +78,060 |
| 2011 | 160 | 49.4% | 2.04 | +40,235 |
| 2012 | 117 | 48.7% | 2.71 | +45,552 |
| 2013 | 266 | 52.6% | 2.61 | +100,445 |
| 2014 | 197 | 53.8% | 2.43 | +57,892 |
| 2015 | 148 | 54.1% | 4.24 | +80,319 |
| 2016 | 140 | 55.7% | 2.79 | +31,925 |
| 2017 | 253 | 49.0% | 2.33 | +76,836 |
| 2018 | 215 | 50.2% | 3.12 | +94,905 |
| 2019 | 148 | 53.4% | 2.60 | +57,442 |
| 2020 | 185 | 41.6% | 1.92 | +72,541 |
| 2021 | 324 | 46.3% | 4.61 | +520,473 |
| 2022 | 49 | 38.8% | 1.36 | +7,565 |
| 2023 | 80 | 46.2% | 1.23 | +5,984 |
| 2024 | 182 | 48.4% | 2.10 | +51,955 |
| 2025 | 148 | 41.9% | 1.09 | +7,535 |
| 2026 | 73 | 52.1% | 2.52 | +40,365 |

<details>
<summary>Full monthly table</summary>

| month | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005-01 | 1 | 100.0% | inf | +950 |
| 2005-02 | 12 | 25.0% | 0.23 | −3,734 |
| 2005-03 | 21 | 57.1% | 2.08 | +4,385 |
| 2005-04 | 2 | 50.0% | 1.45 | +820 |
| 2005-05 | 4 | 50.0% | 5.13 | +617 |
| 2005-06 | 19 | 42.1% | 0.13 | −7,631 |
| 2005-07 | 28 | 57.1% | 1.45 | +3,568 |
| 2005-08 | 51 | 47.1% | 1.50 | +5,996 |
| 2005-09 | 15 | 46.7% | 1.06 | +221 |
| 2005-10 | 8 | 50.0% | 40.32 | +15,869 |
| 2005-11 | 9 | 77.8% | 1.73 | +1,267 |
| 2005-12 | 18 | 38.9% | 1.68 | +2,340 |
| 2006-01 | 21 | 47.6% | 4.33 | +11,365 |
| 2006-02 | 34 | 52.9% | 1.14 | +1,970 |
| 2006-03 | 21 | 57.1% | 2.94 | +3,487 |
| 2006-04 | 26 | 50.0% | 3.46 | +19,954 |
| 2006-05 | 48 | 37.5% | 0.83 | −2,165 |
| 2006-06 | 3 | 33.3% | 28.10 | +10,311 |
| 2006-07 | 8 | 12.5% | 0.01 | −5,087 |
| 2006-08 | 10 | 50.0% | 1.14 | +148 |
| 2006-09 | 11 | 63.6% | 13.11 | +14,205 |
| 2006-10 | 17 | 23.5% | 0.49 | −1,926 |
| 2006-11 | 34 | 47.1% | 2.25 | +8,297 |
| 2006-12 | 27 | 77.8% | 7.00 | +14,745 |
| 2007-01 | 13 | 61.5% | 20.19 | +6,894 |
| 2007-02 | 35 | 37.1% | 1.26 | +1,793 |
| 2007-03 | 16 | 68.8% | 8.82 | +13,104 |
| 2007-04 | 21 | 42.9% | 7.68 | +22,253 |
| 2007-05 | 48 | 43.8% | 0.92 | −1,165 |
| 2007-06 | 31 | 61.3% | 3.03 | +15,031 |
| 2007-07 | 19 | 36.8% | 1.27 | +1,157 |
| 2007-08 | 4 | 25.0% | 0.29 | −1,476 |
| 2007-09 | 4 | 50.0% | 0.30 | −732 |
| 2007-10 | 12 | 50.0% | 10.45 | +32,200 |
| 2007-11 | 9 | 22.2% | 0.32 | −2,230 |
| 2007-12 | 3 | 0.0% | 0.00 | −591 |
| 2008-01 | 1 | 100.0% | inf | +2,092 |
| 2008-02 | 4 | 50.0% | 0.32 | −455 |
| 2008-03 | 4 | 75.0% | 9.19 | +2,097 |
| 2008-04 | 2 | 50.0% | 0.28 | −923 |
| 2008-05 | 8 | 25.0% | 0.64 | −614 |
| 2008-06 | 17 | 76.5% | 15.85 | +17,057 |
| 2008-07 | 4 | 50.0% | 9.70 | +3,743 |
| 2008-08 | 9 | 33.3% | 0.20 | −3,738 |
| 2008-09 | 5 | 60.0% | 1.67 | +602 |
| 2008-11 | 1 | 100.0% | inf | +20 |
| 2009-01 | 4 | 50.0% | 1.62 | +391 |
| 2009-05 | 3 | 100.0% | inf | +1,117 |
| 2009-06 | 3 | 33.3% | 7.60 | +2,589 |
| 2009-07 | 5 | 80.0% | 1.48 | +1,163 |
| 2009-08 | 9 | 0.0% | 0.00 | −3,304 |
| 2009-09 | 10 | 30.0% | 0.20 | −2,238 |
| 2009-10 | 17 | 58.8% | 4.03 | +15,669 |
| 2009-11 | 3 | 66.7% | 76.94 | +772 |
| 2009-12 | 11 | 63.6% | 3.04 | +4,045 |
| 2010-01 | 31 | 41.9% | 1.18 | +1,368 |
| 2010-02 | 10 | 30.0% | 2.41 | +1,610 |
| 2010-03 | 13 | 69.2% | 8.10 | +8,018 |
| 2010-04 | 20 | 45.0% | 3.59 | +11,704 |
| 2010-05 | 29 | 48.3% | 1.78 | +7,653 |
| 2010-08 | 8 | 37.5% | 0.14 | −2,386 |
| 2010-09 | 6 | 66.7% | 39.63 | +2,289 |
| 2010-10 | 15 | 60.0% | 5.02 | +12,029 |
| 2010-11 | 39 | 53.8% | 1.70 | +6,911 |
| 2010-12 | 32 | 59.4% | 7.27 | +28,864 |
| 2011-01 | 32 | 65.6% | 2.28 | +11,408 |
| 2011-02 | 30 | 50.0% | 2.03 | +6,742 |
| 2011-03 | 30 | 53.3% | 6.71 | +15,115 |
| 2011-04 | 10 | 50.0% | 2.42 | +3,194 |
| 2011-05 | 31 | 29.0% | 0.82 | −2,372 |
| 2011-06 | 5 | 60.0% | 7.48 | +5,395 |
| 2011-07 | 5 | 40.0% | 4.64 | +743 |
| 2011-08 | 4 | 50.0% | 7.04 | +762 |
| 2011-09 | 2 | 0.0% | 0.00 | −1,097 |
| 2011-10 | 2 | 50.0% | 1.35 | +87 |
| 2011-11 | 6 | 50.0% | 1.61 | +1,037 |
| 2011-12 | 3 | 66.7% | 0.33 | −779 |
| 2012-01 | 5 | 80.0% | 15.94 | +8,535 |
| 2012-02 | 10 | 80.0% | 2.80 | +2,223 |
| 2012-03 | 19 | 42.1% | 0.38 | −5,505 |
| 2012-04 | 11 | 54.5% | 6.09 | +11,321 |
| 2012-05 | 14 | 21.4% | 0.32 | −3,097 |
| 2012-06 | 7 | 28.6% | 1.39 | +629 |
| 2012-07 | 9 | 44.4% | 11.52 | +11,098 |
| 2012-08 | 9 | 55.6% | 4.82 | +4,823 |
| 2012-09 | 16 | 62.5% | 5.91 | +10,481 |
| 2012-10 | 12 | 50.0% | 4.27 | +6,104 |
| 2012-11 | 2 | 0.0% | 0.00 | −355 |
| 2012-12 | 3 | 33.3% | 0.09 | −704 |
| 2013-01 | 8 | 12.5% | 0.03 | −3,221 |
| 2013-02 | 29 | 55.2% | 1.01 | +124 |
| 2013-03 | 22 | 63.6% | 5.47 | +10,729 |
| 2013-04 | 24 | 70.8% | 3.93 | +19,187 |
| 2013-05 | 23 | 60.9% | 6.49 | +15,616 |
| 2013-06 | 20 | 70.0% | 6.50 | +18,588 |
| 2013-07 | 8 | 75.0% | 10.74 | +4,503 |
| 2013-08 | 41 | 43.9% | 2.52 | +12,445 |
| 2013-09 | 10 | 60.0% | 8.66 | +10,580 |
| 2013-10 | 26 | 38.5% | 0.99 | −98 |
| 2013-11 | 31 | 38.7% | 1.48 | +4,100 |
| 2013-12 | 24 | 50.0% | 2.67 | +7,892 |
| 2014-01 | 42 | 54.8% | 2.66 | +12,471 |
| 2014-02 | 20 | 45.0% | 2.79 | +6,975 |
| 2014-03 | 32 | 50.0% | 1.32 | +3,520 |
| 2014-04 | 17 | 64.7% | 5.45 | +7,051 |
| 2014-05 | 7 | 71.4% | inf | +5,421 |
| 2014-06 | 17 | 47.1% | 3.35 | +7,575 |
| 2014-07 | 20 | 50.0% | 1.51 | +2,724 |
| 2014-08 | 3 | 66.7% | 31.46 | +4,204 |
| 2014-09 | 13 | 23.1% | 0.16 | −4,431 |
| 2014-10 | 1 | 100.0% | inf | +3,913 |
| 2014-11 | 10 | 90.0% | 66.34 | +3,219 |
| 2014-12 | 15 | 60.0% | 3.05 | +5,250 |
| 2015-01 | 7 | 71.4% | 29.61 | +10,251 |
| 2015-02 | 10 | 40.0% | 0.69 | −748 |
| 2015-03 | 36 | 55.6% | 4.77 | +22,047 |
| 2015-04 | 24 | 54.2% | 2.81 | +9,998 |
| 2015-05 | 19 | 52.6% | 6.66 | +10,420 |
| 2015-06 | 9 | 44.4% | 3.11 | +3,805 |
| 2015-07 | 13 | 61.5% | 3.50 | +6,688 |
| 2015-08 | 2 | 50.0% | 1.86 | +632 |
| 2015-09 | 1 | 0.0% | inf | +0 |
| 2015-10 | 2 | 50.0% | 8.33 | +748 |
| 2015-11 | 12 | 50.0% | 3.64 | +7,759 |
| 2015-12 | 13 | 61.5% | 16.52 | +8,719 |
| 2016-01 | 5 | 80.0% | 2.53 | +1,340 |
| 2016-02 | 1 | 0.0% | 0.00 | −27 |
| 2016-03 | 5 | 0.0% | 0.00 | −1,905 |
| 2016-04 | 7 | 57.1% | 3.42 | +1,949 |
| 2016-05 | 7 | 57.1% | 1.34 | +549 |
| 2016-06 | 12 | 58.3% | 6.51 | +8,190 |
| 2016-07 | 1 | 0.0% | 0.00 | −122 |
| 2016-08 | 37 | 59.5% | 2.71 | +6,253 |
| 2016-09 | 25 | 52.0% | 3.07 | +6,150 |
| 2016-10 | 18 | 61.1% | 4.63 | +6,938 |
| 2016-11 | 3 | 33.3% | 0.01 | −1,309 |
| 2016-12 | 19 | 63.2% | 4.37 | +3,920 |
| 2017-01 | 20 | 25.0% | 1.03 | +108 |
| 2017-02 | 28 | 46.4% | 2.03 | +5,210 |
| 2017-03 | 37 | 56.8% | 1.85 | +7,172 |
| 2017-04 | 16 | 43.8% | 3.86 | +11,057 |
| 2017-05 | 23 | 56.5% | 5.12 | +18,787 |
| 2017-06 | 21 | 71.4% | 10.22 | +14,932 |
| 2017-07 | 15 | 60.0% | 2.38 | +4,214 |
| 2017-08 | 18 | 38.9% | 1.31 | +1,431 |
| 2017-09 | 13 | 23.1% | 0.17 | −4,574 |
| 2017-10 | 28 | 50.0% | 1.27 | +1,852 |
| 2017-11 | 14 | 71.4% | 6.96 | +14,689 |
| 2017-12 | 20 | 35.0% | 1.24 | +1,959 |
| 2018-01 | 14 | 50.0% | 2.65 | +5,279 |
| 2018-02 | 20 | 30.0% | 0.95 | −362 |
| 2018-03 | 19 | 42.1% | 5.40 | +27,680 |
| 2018-04 | 6 | 66.7% | 6.05 | +3,112 |
| 2018-05 | 14 | 35.7% | 0.60 | −2,194 |
| 2018-06 | 41 | 58.5% | 4.38 | +22,290 |
| 2018-07 | 29 | 44.8% | 6.66 | +15,219 |
| 2018-08 | 18 | 33.3% | 1.67 | +2,479 |
| 2018-09 | 31 | 77.4% | 6.91 | +14,179 |
| 2018-10 | 8 | 62.5% | 6.94 | +8,293 |
| 2018-11 | 5 | 60.0% | 3.27 | +1,488 |
| 2018-12 | 10 | 30.0% | 0.46 | −2,560 |
| 2019-01 | 1 | 0.0% | inf | +0 |
| 2019-02 | 8 | 62.5% | 1.55 | +1,278 |
| 2019-03 | 24 | 62.5% | 4.76 | +21,732 |
| 2019-04 | 8 | 75.0% | 18.85 | +4,699 |
| 2019-05 | 15 | 40.0% | 3.89 | +12,778 |
| 2019-06 | 5 | 80.0% | 29.40 | +9,640 |
| 2019-07 | 16 | 31.2% | 1.46 | +2,417 |
| 2019-08 | 19 | 31.6% | 0.72 | −1,398 |
| 2019-09 | 9 | 33.3% | 0.06 | −4,245 |
| 2019-10 | 2 | 50.0% | 0.68 | −21 |
| 2019-11 | 15 | 46.7% | 1.10 | +453 |
| 2019-12 | 26 | 80.8% | 3.92 | +10,109 |
| 2020-01 | 22 | 50.0% | 4.80 | +15,223 |
| 2020-02 | 24 | 29.2% | 0.92 | −1,095 |
| 2020-03 | 7 | 28.6% | 0.53 | −3,006 |
| 2020-04 | 1 | 0.0% | 0.00 | −308 |
| 2020-05 | 9 | 44.4% | 0.63 | −2,053 |
| 2020-06 | 13 | 46.2% | 2.63 | +8,878 |
| 2020-07 | 10 | 60.0% | 13.15 | +23,452 |
| 2020-08 | 19 | 36.8% | 2.40 | +11,021 |
| 2020-09 | 15 | 73.3% | 3.90 | +14,593 |
| 2020-10 | 11 | 18.2% | 1.33 | +1,838 |
| 2020-11 | 12 | 41.7% | 0.72 | −1,326 |
| 2020-12 | 42 | 38.1% | 1.29 | +5,324 |
| 2021-01 | 57 | 61.4% | 25.22 | +365,203 |
| 2021-02 | 92 | 42.4% | 2.60 | +100,048 |
| 2021-03 | 32 | 46.9% | 3.82 | +43,914 |
| 2021-04 | 19 | 36.8% | 0.82 | −1,465 |
| 2021-05 | 12 | 58.3% | 5.41 | +7,576 |
| 2021-06 | 26 | 30.8% | 0.53 | −4,942 |
| 2021-07 | 11 | 72.7% | 6.08 | +4,603 |
| 2021-08 | 14 | 50.0% | 1.14 | +463 |
| 2021-09 | 14 | 42.9% | 1.55 | +1,746 |
| 2021-10 | 8 | 62.5% | 7.86 | +12,943 |
| 2021-11 | 30 | 30.0% | 0.29 | −13,775 |
| 2021-12 | 9 | 44.4% | 2.83 | +4,157 |
| 2022-01 | 4 | 50.0% | 7.01 | +5,734 |
| 2022-02 | 1 | 0.0% | 0.00 | −2,087 |
| 2022-03 | 2 | 50.0% | 4.40 | +1,380 |
| 2022-04 | 9 | 55.6% | 5.09 | +4,868 |
| 2022-06 | 3 | 33.3% | 0.34 | −971 |
| 2022-08 | 14 | 35.7% | 0.74 | −2,409 |
| 2022-09 | 4 | 25.0% | 0.19 | −1,292 |
| 2022-11 | 4 | 0.0% | 0.00 | −2,020 |
| 2022-12 | 8 | 50.0% | 3.45 | +4,362 |
| 2023-01 | 5 | 20.0% | 0.30 | −1,939 |
| 2023-02 | 5 | 20.0% | 0.58 | −662 |
| 2023-03 | 3 | 66.7% | 4.83 | +1,318 |
| 2023-04 | 3 | 0.0% | 0.00 | −862 |
| 2023-05 | 4 | 25.0% | 1.15 | +117 |
| 2023-06 | 11 | 63.6% | 2.68 | +3,593 |
| 2023-07 | 10 | 60.0% | 4.45 | +7,042 |
| 2023-08 | 15 | 40.0% | 0.35 | −2,836 |
| 2023-09 | 6 | 66.7% | 2.93 | +3,106 |
| 2023-10 | 1 | 0.0% | 0.00 | −1,255 |
| 2023-11 | 3 | 0.0% | 0.00 | −2,573 |
| 2023-12 | 14 | 64.3% | 1.15 | +935 |
| 2024-01 | 20 | 70.0% | 5.24 | +11,627 |
| 2024-02 | 10 | 10.0% | 0.55 | −1,503 |
| 2024-03 | 38 | 47.4% | 2.01 | +10,956 |
| 2024-04 | 21 | 71.4% | 12.75 | +11,377 |
| 2024-05 | 8 | 37.5% | 2.37 | +2,482 |
| 2024-06 | 4 | 100.0% | inf | +10,502 |
| 2024-07 | 1 | 100.0% | inf | +1,093 |
| 2024-08 | 7 | 28.6% | 0.38 | −1,869 |
| 2024-09 | 8 | 75.0% | 12.76 | +9,756 |
| 2024-10 | 16 | 37.5% | 0.87 | −886 |
| 2024-11 | 27 | 22.2% | 0.33 | −6,830 |
| 2024-12 | 22 | 54.5% | 1.80 | +5,250 |
| 2025-01 | 3 | 33.3% | 0.60 | −405 |
| 2025-02 | 21 | 52.4% | 0.95 | −368 |
| 2025-03 | 6 | 33.3% | 0.08 | −3,580 |
| 2025-05 | 8 | 37.5% | 0.70 | −1,797 |
| 2025-06 | 18 | 44.4% | 1.22 | +1,281 |
| 2025-07 | 21 | 47.6% | 0.49 | −13,518 |
| 2025-08 | 9 | 33.3% | 2.13 | +3,969 |
| 2025-09 | 21 | 23.8% | 0.24 | −9,397 |
| 2025-10 | 24 | 62.5% | 6.57 | +36,355 |
| 2025-11 | 6 | 33.3% | 1.89 | +2,193 |
| 2025-12 | 11 | 18.2% | 0.01 | −7,198 |
| 2026-01 | 16 | 50.0% | 3.54 | +7,963 |
| 2026-02 | 18 | 38.9% | 2.98 | +9,116 |
| 2026-03 | 13 | 69.2% | 12.08 | +23,697 |
| 2026-04 | 5 | 60.0% | 0.82 | −153 |
| 2026-05 | 21 | 52.4% | 0.98 | −258 |

</details>

#### Window = 8-day-low stop

half-Kelly 0.125/0.174/0.192 · total **+$1,618,521** Kelly (flat +$1,522,020) · months **173/257 (67%)** · years **22/22** · up:dn **4.7:1** · max DD **-13,704** · worst streak **4mo**

Yearly (Kelly-sized, by exit date):
| year | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005 | 189 | 50.3% | 1.42 | +20,103 |
| 2006 | 260 | 48.8% | 2.53 | +83,250 |
| 2007 | 214 | 47.2% | 2.82 | +86,537 |
| 2008 | 56 | 57.1% | 3.00 | +21,941 |
| 2009 | 64 | 51.6% | 2.38 | +21,507 |
| 2010 | 203 | 51.7% | 2.86 | +79,495 |
| 2011 | 161 | 50.3% | 2.17 | +43,072 |
| 2012 | 116 | 46.6% | 2.54 | +41,199 |
| 2013 | 267 | 52.1% | 2.62 | +100,381 |
| 2014 | 198 | 55.1% | 2.76 | +65,953 |
| 2015 | 148 | 54.7% | 4.11 | +77,144 |
| 2016 | 141 | 57.4% | 3.00 | +34,255 |
| 2017 | 250 | 49.2% | 2.09 | +59,551 |
| 2018 | 215 | 53.0% | 3.29 | +97,268 |
| 2019 | 149 | 54.4% | 2.73 | +61,544 |
| 2020 | 187 | 41.7% | 1.80 | +62,958 |
| 2021 | 321 | 47.7% | 4.68 | +520,058 |
| 2022 | 49 | 36.7% | 1.34 | +7,201 |
| 2023 | 81 | 48.1% | 1.31 | +7,974 |
| 2024 | 181 | 50.3% | 2.12 | +51,765 |
| 2025 | 148 | 41.9% | 1.49 | +29,877 |
| 2026 | 73 | 54.8% | 2.76 | +45,487 |

<details>
<summary>Full monthly table</summary>

| month | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005-01 | 1 | 100.0% | inf | +931 |
| 2005-02 | 14 | 35.7% | 0.48 | −2,531 |
| 2005-03 | 19 | 47.4% | 1.74 | +2,886 |
| 2005-04 | 2 | 50.0% | 2.27 | +2,381 |
| 2005-05 | 4 | 50.0% | 5.65 | +699 |
| 2005-06 | 19 | 42.1% | 0.13 | −7,788 |
| 2005-07 | 28 | 57.1% | 1.47 | +3,729 |
| 2005-08 | 53 | 50.9% | 1.99 | +11,722 |
| 2005-09 | 14 | 42.9% | 0.68 | −1,044 |
| 2005-10 | 7 | 71.4% | 7.64 | +1,856 |
| 2005-11 | 9 | 77.8% | 1.71 | +1,216 |
| 2005-12 | 19 | 42.1% | 2.71 | +6,046 |
| 2006-01 | 22 | 50.0% | 4.61 | +12,882 |
| 2006-02 | 32 | 46.9% | 0.95 | −640 |
| 2006-03 | 23 | 60.9% | 3.30 | +4,193 |
| 2006-04 | 25 | 52.0% | 8.80 | +26,993 |
| 2006-05 | 47 | 40.4% | 0.89 | −1,372 |
| 2006-06 | 3 | 33.3% | 36.04 | +13,622 |
| 2006-07 | 9 | 11.1% | 0.01 | −5,706 |
| 2006-08 | 10 | 50.0% | 1.42 | +372 |
| 2006-09 | 12 | 66.7% | 11.88 | +12,518 |
| 2006-10 | 16 | 18.8% | 0.52 | −1,854 |
| 2006-11 | 37 | 48.6% | 2.28 | +8,361 |
| 2006-12 | 24 | 79.2% | 7.44 | +13,881 |
| 2007-01 | 12 | 58.3% | 22.25 | +6,805 |
| 2007-02 | 38 | 42.1% | 1.44 | +3,069 |
| 2007-03 | 14 | 71.4% | 6.24 | +12,013 |
| 2007-04 | 21 | 47.6% | 9.30 | +23,668 |
| 2007-05 | 49 | 46.9% | 1.11 | +1,475 |
| 2007-06 | 31 | 58.1% | 2.98 | +13,620 |
| 2007-07 | 17 | 29.4% | 0.64 | −1,519 |
| 2007-08 | 4 | 25.0% | 0.31 | −1,414 |
| 2007-09 | 4 | 50.0% | 0.30 | −718 |
| 2007-10 | 13 | 53.8% | 10.41 | +32,263 |
| 2007-11 | 8 | 25.0% | 0.32 | −2,128 |
| 2007-12 | 3 | 0.0% | 0.00 | −597 |
| 2008-01 | 1 | 100.0% | inf | +2,050 |
| 2008-02 | 4 | 50.0% | 0.32 | −471 |
| 2008-03 | 4 | 75.0% | 9.19 | +2,055 |
| 2008-04 | 2 | 50.0% | 0.27 | −972 |
| 2008-05 | 9 | 44.4% | 1.17 | +290 |
| 2008-06 | 17 | 76.5% | 26.34 | +20,901 |
| 2008-07 | 3 | 33.3% | 1.85 | +360 |
| 2008-08 | 10 | 30.0% | 0.15 | −4,157 |
| 2008-09 | 4 | 50.0% | 1.52 | +475 |
| 2008-11 | 1 | 100.0% | inf | +20 |
| 2008-12 | 1 | 100.0% | inf | +1,391 |
| 2009-01 | 3 | 66.7% | 2.02 | +180 |
| 2009-05 | 4 | 100.0% | inf | +4,275 |
| 2009-06 | 2 | 0.0% | 0.00 | −394 |
| 2009-07 | 5 | 80.0% | 1.41 | +1,012 |
| 2009-08 | 9 | 0.0% | 0.00 | −3,274 |
| 2009-09 | 10 | 30.0% | 0.21 | −2,193 |
| 2009-10 | 18 | 61.1% | 4.66 | +16,440 |
| 2009-11 | 3 | 100.0% | inf | +1,020 |
| 2009-12 | 10 | 60.0% | 3.28 | +4,442 |
| 2010-01 | 32 | 40.6% | 1.31 | +2,357 |
| 2010-02 | 9 | 44.4% | 8.02 | +2,507 |
| 2010-03 | 16 | 56.2% | 3.54 | +6,667 |
| 2010-04 | 19 | 47.4% | 4.20 | +13,473 |
| 2010-05 | 27 | 44.4% | 1.43 | +4,164 |
| 2010-08 | 9 | 44.4% | 0.51 | −1,348 |
| 2010-09 | 5 | 60.0% | 24.16 | +1,389 |
| 2010-10 | 15 | 60.0% | 6.02 | +13,059 |
| 2010-11 | 40 | 55.0% | 1.95 | +8,668 |
| 2010-12 | 31 | 64.5% | 8.50 | +28,559 |
| 2011-01 | 34 | 61.8% | 2.07 | +10,748 |
| 2011-02 | 31 | 54.8% | 2.95 | +10,167 |
| 2011-03 | 27 | 51.9% | 7.08 | +13,064 |
| 2011-04 | 10 | 50.0% | 2.46 | +3,507 |
| 2011-05 | 31 | 32.3% | 0.88 | −1,457 |
| 2011-06 | 5 | 60.0% | 9.38 | +5,452 |
| 2011-07 | 5 | 40.0% | 4.60 | +727 |
| 2011-08 | 4 | 75.0% | 14.36 | +815 |
| 2011-09 | 2 | 0.0% | 0.00 | −1,098 |
| 2011-10 | 3 | 66.7% | 7.67 | +1,605 |
| 2011-11 | 5 | 40.0% | 0.53 | −787 |
| 2011-12 | 4 | 50.0% | 1.63 | +329 |
| 2012-01 | 5 | 60.0% | 10.43 | +7,062 |
| 2012-02 | 11 | 72.7% | 1.91 | +1,125 |
| 2012-03 | 19 | 42.1% | 0.81 | −1,844 |
| 2012-04 | 10 | 40.0% | 3.23 | +4,931 |
| 2012-05 | 13 | 23.1% | 0.41 | −2,196 |
| 2012-06 | 7 | 28.6% | 1.44 | +700 |
| 2012-07 | 9 | 44.4% | 14.18 | +11,393 |
| 2012-08 | 9 | 55.6% | 4.75 | +4,846 |
| 2012-09 | 18 | 61.1% | 5.52 | +10,949 |
| 2012-10 | 10 | 50.0% | 4.36 | +5,304 |
| 2012-11 | 2 | 0.0% | 0.00 | −348 |
| 2012-12 | 3 | 33.3% | 0.08 | −724 |
| 2013-01 | 11 | 27.3% | 0.11 | −4,444 |
| 2013-02 | 29 | 58.6% | 1.20 | +1,568 |
| 2013-03 | 21 | 61.9% | 7.18 | +14,720 |
| 2013-04 | 23 | 69.6% | 3.24 | +14,106 |
| 2013-05 | 25 | 60.0% | 4.19 | +12,880 |
| 2013-06 | 17 | 70.6% | 10.70 | +20,663 |
| 2013-07 | 10 | 60.0% | 11.10 | +5,433 |
| 2013-08 | 41 | 43.9% | 2.34 | +11,097 |
| 2013-09 | 11 | 45.5% | 5.51 | +9,512 |
| 2013-10 | 23 | 39.1% | 1.11 | +1,114 |
| 2013-11 | 33 | 39.4% | 1.51 | +4,537 |
| 2013-12 | 23 | 52.2% | 3.25 | +9,195 |
| 2014-01 | 44 | 54.5% | 2.57 | +12,162 |
| 2014-02 | 19 | 47.4% | 2.63 | +6,391 |
| 2014-03 | 32 | 53.1% | 1.89 | +8,574 |
| 2014-04 | 15 | 60.0% | 3.77 | +4,399 |
| 2014-05 | 7 | 85.7% | inf | +6,414 |
| 2014-06 | 18 | 50.0% | 4.49 | +11,293 |
| 2014-07 | 19 | 47.4% | 1.95 | +2,888 |
| 2014-08 | 3 | 66.7% | 30.71 | +4,234 |
| 2014-09 | 13 | 30.8% | 0.21 | −4,017 |
| 2014-10 | 1 | 100.0% | inf | +3,836 |
| 2014-11 | 12 | 91.7% | 82.99 | +3,957 |
| 2014-12 | 15 | 53.3% | 2.98 | +5,821 |
| 2015-01 | 6 | 66.7% | 27.12 | +9,170 |
| 2015-02 | 10 | 40.0% | 0.65 | −899 |
| 2015-03 | 37 | 56.8% | 4.80 | +21,903 |
| 2015-04 | 23 | 52.2% | 2.29 | +7,287 |
| 2015-05 | 19 | 47.4% | 5.56 | +8,892 |
| 2015-06 | 9 | 55.6% | 5.00 | +6,295 |
| 2015-07 | 12 | 58.3% | 2.67 | +4,390 |
| 2015-08 | 2 | 50.0% | 1.86 | +619 |
| 2015-09 | 1 | 0.0% | inf | +0 |
| 2015-10 | 2 | 50.0% | 7.90 | +727 |
| 2015-11 | 14 | 57.1% | 3.99 | +8,806 |
| 2015-12 | 13 | 69.2% | 18.53 | +9,952 |
| 2016-01 | 3 | 66.7% | 1.35 | +314 |
| 2016-02 | 1 | 0.0% | 0.00 | −26 |
| 2016-03 | 5 | 0.0% | 0.00 | −1,877 |
| 2016-04 | 7 | 57.1% | 3.49 | +1,963 |
| 2016-05 | 8 | 50.0% | 1.27 | +458 |
| 2016-06 | 11 | 63.6% | 6.40 | +7,581 |
| 2016-07 | 1 | 100.0% | inf | +124 |
| 2016-08 | 40 | 60.0% | 3.17 | +7,975 |
| 2016-09 | 23 | 52.2% | 3.67 | +6,908 |
| 2016-10 | 17 | 64.7% | 4.87 | +6,665 |
| 2016-11 | 4 | 50.0% | 0.58 | −545 |
| 2016-12 | 21 | 66.7% | 5.24 | +4,715 |
| 2017-01 | 23 | 34.8% | 1.58 | +1,844 |
| 2017-02 | 26 | 42.3% | 0.83 | −911 |
| 2017-03 | 36 | 55.6% | 1.63 | +5,235 |
| 2017-04 | 16 | 43.8% | 5.92 | +16,224 |
| 2017-05 | 22 | 59.1% | 3.14 | +9,519 |
| 2017-06 | 21 | 71.4% | 11.44 | +16,557 |
| 2017-07 | 14 | 50.0% | 0.79 | −724 |
| 2017-08 | 17 | 41.2% | 1.50 | +1,995 |
| 2017-09 | 14 | 35.7% | 0.21 | −4,025 |
| 2017-10 | 29 | 55.2% | 2.50 | +8,087 |
| 2017-11 | 14 | 64.3% | 4.56 | +9,935 |
| 2017-12 | 18 | 27.8% | 0.47 | −4,183 |
| 2018-01 | 15 | 53.3% | 2.68 | +5,604 |
| 2018-02 | 19 | 36.8% | 1.07 | +455 |
| 2018-03 | 19 | 42.1% | 5.60 | +28,677 |
| 2018-04 | 6 | 66.7% | 6.05 | +3,051 |
| 2018-05 | 15 | 33.3% | 0.59 | −2,229 |
| 2018-06 | 42 | 59.5% | 5.50 | +25,834 |
| 2018-07 | 28 | 46.4% | 5.95 | +11,801 |
| 2018-08 | 20 | 45.0% | 1.41 | +1,627 |
| 2018-09 | 30 | 83.3% | 11.53 | +17,150 |
| 2018-10 | 6 | 66.7% | 6.14 | +5,762 |
| 2018-11 | 5 | 60.0% | 3.33 | +1,496 |
| 2018-12 | 10 | 30.0% | 0.57 | −1,960 |
| 2019-01 | 1 | 0.0% | inf | +0 |
| 2019-02 | 8 | 62.5% | 1.57 | +1,290 |
| 2019-03 | 27 | 66.7% | 5.55 | +26,053 |
| 2019-04 | 5 | 60.0% | 11.56 | +2,746 |
| 2019-05 | 15 | 46.7% | 3.90 | +12,594 |
| 2019-06 | 5 | 80.0% | 30.46 | +9,797 |
| 2019-07 | 16 | 31.2% | 1.65 | +3,370 |
| 2019-08 | 19 | 31.6% | 0.73 | −1,332 |
| 2019-09 | 9 | 33.3% | 0.06 | −4,185 |
| 2019-10 | 2 | 50.0% | 0.68 | −21 |
| 2019-11 | 16 | 50.0% | 1.09 | +428 |
| 2019-12 | 26 | 80.8% | 4.08 | +10,804 |
| 2020-01 | 22 | 54.5% | 4.29 | +12,171 |
| 2020-02 | 24 | 25.0% | 0.84 | −2,223 |
| 2020-03 | 6 | 33.3% | 0.59 | −2,244 |
| 2020-04 | 1 | 0.0% | 0.00 | −302 |
| 2020-05 | 10 | 40.0% | 0.60 | −2,347 |
| 2020-06 | 13 | 53.8% | 2.94 | +9,858 |
| 2020-07 | 10 | 60.0% | 14.72 | +25,935 |
| 2020-08 | 20 | 35.0% | 2.48 | +10,683 |
| 2020-09 | 14 | 71.4% | 3.74 | +13,129 |
| 2020-10 | 11 | 18.2% | 1.35 | +1,904 |
| 2020-11 | 14 | 42.9% | 0.91 | −476 |
| 2020-12 | 42 | 38.1% | 0.84 | −3,131 |
| 2021-01 | 59 | 64.4% | 25.18 | +368,635 |
| 2021-02 | 90 | 42.2% | 2.79 | +109,831 |
| 2021-03 | 30 | 43.3% | 2.89 | +29,395 |
| 2021-04 | 18 | 33.3% | 0.69 | −2,274 |
| 2021-05 | 12 | 58.3% | 6.40 | +7,340 |
| 2021-06 | 27 | 37.0% | 0.74 | −2,702 |
| 2021-07 | 10 | 80.0% | 7.86 | +5,148 |
| 2021-08 | 16 | 56.2% | 1.38 | +1,139 |
| 2021-09 | 13 | 38.5% | 2.79 | +5,371 |
| 2021-10 | 7 | 57.1% | 5.75 | +6,869 |
| 2021-11 | 32 | 31.2% | 0.31 | −13,704 |
| 2021-12 | 7 | 71.4% | 3.66 | +5,011 |
| 2022-01 | 4 | 50.0% | 7.38 | +5,936 |
| 2022-02 | 1 | 0.0% | 0.00 | −2,045 |
| 2022-03 | 2 | 50.0% | 4.17 | +1,330 |
| 2022-04 | 9 | 55.6% | 4.97 | +4,835 |
| 2022-06 | 3 | 33.3% | 0.34 | −951 |
| 2022-08 | 15 | 33.3% | 0.73 | −2,550 |
| 2022-09 | 3 | 0.0% | 0.00 | −1,564 |
| 2022-11 | 4 | 0.0% | 0.00 | −2,286 |
| 2022-12 | 8 | 50.0% | 3.52 | +4,496 |
| 2023-01 | 5 | 20.0% | 0.30 | −1,900 |
| 2023-02 | 5 | 20.0% | 0.60 | −645 |
| 2023-03 | 3 | 100.0% | inf | +1,921 |
| 2023-04 | 3 | 0.0% | 0.00 | −845 |
| 2023-05 | 4 | 25.0% | 1.15 | +115 |
| 2023-06 | 11 | 63.6% | 2.89 | +3,993 |
| 2023-07 | 10 | 60.0% | 5.15 | +7,470 |
| 2023-08 | 16 | 43.8% | 1.12 | +527 |
| 2023-09 | 5 | 60.0% | 0.81 | −312 |
| 2023-10 | 1 | 0.0% | 0.00 | −1,230 |
| 2023-11 | 3 | 0.0% | 0.00 | −2,561 |
| 2023-12 | 15 | 66.7% | 1.23 | +1,440 |
| 2024-01 | 19 | 68.4% | 5.77 | +13,214 |
| 2024-02 | 10 | 10.0% | 0.53 | −1,539 |
| 2024-03 | 40 | 47.5% | 1.90 | +10,803 |
| 2024-04 | 20 | 85.0% | 28.59 | +13,145 |
| 2024-05 | 8 | 50.0% | 3.17 | +3,098 |
| 2024-06 | 3 | 100.0% | inf | +3,677 |
| 2024-07 | 1 | 100.0% | inf | +1,128 |
| 2024-08 | 7 | 28.6% | 0.39 | −1,839 |
| 2024-09 | 10 | 60.0% | 4.94 | +8,558 |
| 2024-10 | 15 | 40.0% | 1.23 | +1,354 |
| 2024-11 | 29 | 24.1% | 0.35 | −6,464 |
| 2024-12 | 19 | 63.2% | 2.26 | +6,630 |
| 2025-01 | 3 | 33.3% | 0.57 | −451 |
| 2025-02 | 23 | 52.2% | 1.56 | +2,748 |
| 2025-03 | 4 | 25.0% | 0.01 | −2,601 |
| 2025-05 | 9 | 44.4% | 0.75 | −1,460 |
| 2025-06 | 18 | 38.9% | 0.81 | −1,119 |
| 2025-07 | 21 | 57.1% | 2.21 | +9,737 |
| 2025-08 | 9 | 22.2% | 1.63 | +2,307 |
| 2025-09 | 22 | 27.3% | 0.31 | −8,851 |
| 2025-10 | 23 | 60.9% | 6.78 | +37,369 |
| 2025-11 | 5 | 20.0% | 0.77 | −547 |
| 2025-12 | 11 | 18.2% | 0.01 | −7,254 |
| 2026-01 | 16 | 62.5% | 4.86 | +9,548 |
| 2026-02 | 18 | 38.9% | 3.26 | +9,642 |
| 2026-03 | 13 | 69.2% | 13.27 | +25,702 |
| 2026-04 | 5 | 60.0% | 0.78 | −190 |
| 2026-05 | 21 | 52.4% | 1.05 | +784 |

</details>

#### Window = 7-day-low stop

half-Kelly 0.127/0.177/0.186 · total **+$1,542,128** Kelly (flat +$1,455,102) · months **178/257 (69%)** · years **22/22** · up:dn **4.8:1** · max DD **-7,129** · worst streak **4mo**

Yearly (Kelly-sized, by exit date):
| year | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005 | 192 | 52.1% | 1.44 | +21,640 |
| 2006 | 260 | 49.2% | 2.75 | +89,233 |
| 2007 | 212 | 48.1% | 2.88 | +80,009 |
| 2008 | 56 | 58.9% | 3.29 | +22,348 |
| 2009 | 64 | 50.0% | 2.31 | +20,104 |
| 2010 | 205 | 52.7% | 2.80 | +75,926 |
| 2011 | 162 | 53.1% | 2.56 | +51,900 |
| 2012 | 114 | 47.4% | 2.22 | +30,923 |
| 2013 | 268 | 53.4% | 2.69 | +103,332 |
| 2014 | 197 | 55.3% | 2.46 | +55,125 |
| 2015 | 147 | 54.4% | 3.93 | +72,076 |
| 2016 | 143 | 59.4% | 3.13 | +36,029 |
| 2017 | 250 | 51.6% | 2.17 | +61,035 |
| 2018 | 213 | 52.1% | 3.11 | +88,739 |
| 2019 | 151 | 54.3% | 2.56 | +55,842 |
| 2020 | 191 | 43.5% | 1.78 | +56,505 |
| 2021 | 314 | 46.8% | 4.71 | +479,398 |
| 2022 | 49 | 38.8% | 1.39 | +8,116 |
| 2023 | 82 | 48.8% | 1.46 | +10,910 |
| 2024 | 181 | 49.2% | 2.07 | +49,387 |
| 2025 | 148 | 43.9% | 1.52 | +29,569 |
| 2026 | 72 | 54.2% | 2.73 | +43,980 |

<details>
<summary>Full monthly table</summary>

| month | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005-01 | 1 | 100.0% | inf | +937 |
| 2005-02 | 14 | 35.7% | 0.54 | −1,957 |
| 2005-03 | 19 | 47.4% | 1.77 | +2,961 |
| 2005-04 | 2 | 50.0% | 2.27 | +2,394 |
| 2005-05 | 4 | 50.0% | 5.65 | +705 |
| 2005-06 | 21 | 57.1% | 0.41 | −5,191 |
| 2005-07 | 28 | 57.1% | 1.09 | +751 |
| 2005-08 | 52 | 53.8% | 1.91 | +10,652 |
| 2005-09 | 13 | 38.5% | 0.59 | −1,317 |
| 2005-10 | 8 | 62.5% | 2.76 | +1,524 |
| 2005-11 | 10 | 70.0% | 1.48 | +860 |
| 2005-12 | 20 | 45.0% | 3.24 | +9,321 |
| 2006-01 | 20 | 55.0% | 5.60 | +12,794 |
| 2006-02 | 33 | 48.5% | 0.63 | −4,798 |
| 2006-03 | 25 | 64.0% | 4.28 | +6,020 |
| 2006-04 | 24 | 45.8% | 8.95 | +28,172 |
| 2006-05 | 45 | 37.8% | 1.01 | +119 |
| 2006-06 | 3 | 33.3% | 36.05 | +13,730 |
| 2006-07 | 9 | 22.2% | 0.02 | −4,516 |
| 2006-08 | 12 | 58.3% | 4.35 | +2,961 |
| 2006-09 | 12 | 50.0% | 6.69 | +10,254 |
| 2006-10 | 15 | 20.0% | 0.96 | −110 |
| 2006-11 | 37 | 48.6% | 2.60 | +9,172 |
| 2006-12 | 25 | 80.0% | 8.38 | +15,435 |
| 2007-01 | 11 | 54.5% | 12.88 | +4,066 |
| 2007-02 | 39 | 43.6% | 1.46 | +2,971 |
| 2007-03 | 13 | 69.2% | 5.47 | +9,751 |
| 2007-04 | 21 | 42.9% | 7.28 | +17,609 |
| 2007-05 | 49 | 53.1% | 1.88 | +8,004 |
| 2007-06 | 31 | 58.1% | 3.00 | +13,700 |
| 2007-07 | 17 | 29.4% | 0.72 | −1,135 |
| 2007-08 | 3 | 0.0% | 0.00 | −2,058 |
| 2007-09 | 4 | 50.0% | 0.31 | −688 |
| 2007-10 | 14 | 50.0% | 6.31 | +29,011 |
| 2007-11 | 7 | 42.9% | 0.63 | −619 |
| 2007-12 | 3 | 0.0% | 0.00 | −601 |
| 2008-01 | 1 | 100.0% | inf | +2,544 |
| 2008-02 | 4 | 50.0% | 0.32 | −475 |
| 2008-03 | 4 | 75.0% | 9.19 | +2,068 |
| 2008-04 | 3 | 66.7% | 2.36 | +1,608 |
| 2008-05 | 9 | 55.6% | 3.69 | +2,023 |
| 2008-06 | 16 | 75.0% | 28.44 | +17,063 |
| 2008-07 | 3 | 33.3% | 1.85 | +363 |
| 2008-08 | 10 | 30.0% | 0.15 | −4,185 |
| 2008-09 | 4 | 50.0% | 1.52 | +477 |
| 2008-11 | 1 | 100.0% | inf | +20 |
| 2008-12 | 1 | 100.0% | inf | +843 |
| 2009-01 | 3 | 66.7% | 1.62 | +110 |
| 2009-05 | 4 | 100.0% | inf | +4,154 |
| 2009-06 | 3 | 33.3% | 1.11 | +43 |
| 2009-07 | 4 | 75.0% | 0.57 | −1,079 |
| 2009-08 | 9 | 0.0% | 0.00 | −3,239 |
| 2009-09 | 10 | 30.0% | 0.21 | −2,089 |
| 2009-10 | 18 | 55.6% | 4.77 | +16,641 |
| 2009-11 | 3 | 100.0% | inf | +1,027 |
| 2009-12 | 10 | 60.0% | 3.32 | +4,537 |
| 2010-01 | 32 | 40.6% | 1.47 | +3,363 |
| 2010-02 | 9 | 55.6% | 10.85 | +3,378 |
| 2010-03 | 16 | 56.2% | 3.33 | +5,983 |
| 2010-04 | 20 | 50.0% | 4.90 | +14,824 |
| 2010-05 | 26 | 42.3% | 1.41 | +3,724 |
| 2010-08 | 10 | 50.0% | 0.63 | −1,013 |
| 2010-09 | 6 | 66.7% | 30.73 | +1,796 |
| 2010-10 | 15 | 53.3% | 5.38 | +12,009 |
| 2010-11 | 43 | 58.1% | 1.76 | +7,669 |
| 2010-12 | 28 | 64.3% | 7.84 | +24,192 |
| 2011-01 | 32 | 62.5% | 2.19 | +11,381 |
| 2011-02 | 34 | 55.9% | 3.20 | +11,580 |
| 2011-03 | 25 | 52.0% | 7.24 | +12,619 |
| 2011-04 | 10 | 50.0% | 2.44 | +3,483 |
| 2011-05 | 31 | 38.7% | 1.23 | +2,151 |
| 2011-06 | 4 | 75.0% | 16.15 | +5,745 |
| 2011-07 | 6 | 50.0% | 6.83 | +1,185 |
| 2011-08 | 3 | 100.0% | inf | +861 |
| 2011-09 | 2 | 0.0% | 0.00 | −1,074 |
| 2011-10 | 3 | 66.7% | 7.31 | +1,528 |
| 2011-11 | 7 | 42.9% | 0.50 | −982 |
| 2011-12 | 5 | 60.0% | 7.53 | +3,423 |
| 2012-01 | 3 | 33.3% | 0.37 | −470 |
| 2012-02 | 12 | 66.7% | 1.63 | +1,062 |
| 2012-03 | 18 | 44.4% | 0.95 | −469 |
| 2012-04 | 10 | 30.0% | 2.69 | +3,985 |
| 2012-05 | 12 | 33.3% | 0.51 | −1,601 |
| 2012-06 | 7 | 28.6% | 1.44 | +707 |
| 2012-07 | 9 | 44.4% | 19.21 | +11,478 |
| 2012-08 | 12 | 66.7% | 4.30 | +5,862 |
| 2012-09 | 17 | 64.7% | 7.96 | +11,011 |
| 2012-10 | 8 | 37.5% | 1.16 | +256 |
| 2012-11 | 2 | 0.0% | 0.00 | −428 |
| 2012-12 | 4 | 50.0% | 0.41 | −470 |
| 2013-01 | 10 | 20.0% | 0.07 | −4,516 |
| 2013-02 | 31 | 54.8% | 1.21 | +1,744 |
| 2013-03 | 24 | 75.0% | 18.18 | +22,474 |
| 2013-04 | 19 | 57.9% | 2.07 | +6,898 |
| 2013-05 | 25 | 64.0% | 3.67 | +9,222 |
| 2013-06 | 17 | 82.4% | 11.55 | +21,108 |
| 2013-07 | 9 | 55.6% | 8.88 | +4,188 |
| 2013-08 | 44 | 45.5% | 2.19 | +10,924 |
| 2013-09 | 11 | 54.5% | 5.93 | +9,753 |
| 2013-10 | 20 | 30.0% | 1.07 | +669 |
| 2013-11 | 35 | 42.9% | 1.87 | +7,604 |
| 2013-12 | 23 | 56.5% | 4.35 | +13,265 |
| 2014-01 | 43 | 55.8% | 2.28 | +9,867 |
| 2014-02 | 19 | 47.4% | 1.77 | +2,903 |
| 2014-03 | 36 | 61.1% | 2.21 | +11,450 |
| 2014-04 | 11 | 45.5% | 2.49 | +2,310 |
| 2014-05 | 7 | 71.4% | 38.02 | +3,607 |
| 2014-06 | 17 | 47.1% | 4.36 | +10,884 |
| 2014-07 | 20 | 50.0% | 1.60 | +2,298 |
| 2014-08 | 2 | 50.0% | 13.50 | +1,796 |
| 2014-09 | 14 | 35.7% | 0.74 | −1,337 |
| 2014-11 | 13 | 92.3% | 184.45 | +5,568 |
| 2014-12 | 15 | 53.3% | 2.96 | +5,778 |
| 2015-01 | 5 | 60.0% | 16.30 | +5,404 |
| 2015-02 | 10 | 40.0% | 0.69 | −732 |
| 2015-03 | 38 | 57.9% | 5.51 | +26,025 |
| 2015-04 | 22 | 50.0% | 1.60 | +3,397 |
| 2015-05 | 20 | 50.0% | 5.76 | +9,338 |
| 2015-06 | 10 | 60.0% | 5.28 | +6,783 |
| 2015-07 | 11 | 54.5% | 2.24 | +3,102 |
| 2015-08 | 1 | 0.0% | 0.00 | −721 |
| 2015-09 | 1 | 0.0% | inf | +0 |
| 2015-10 | 2 | 50.0% | 11.08 | +1,071 |
| 2015-11 | 17 | 58.8% | 4.40 | +10,158 |
| 2015-12 | 10 | 70.0% | 17.53 | +8,251 |
| 2016-01 | 3 | 66.7% | 1.35 | +315 |
| 2016-02 | 1 | 0.0% | 0.00 | −26 |
| 2016-03 | 7 | 14.3% | 0.67 | −701 |
| 2016-04 | 6 | 66.7% | 4.38 | +1,843 |
| 2016-05 | 7 | 42.9% | 0.87 | −214 |
| 2016-06 | 11 | 63.6% | 7.46 | +7,820 |
| 2016-07 | 1 | 100.0% | inf | +125 |
| 2016-08 | 42 | 59.5% | 3.06 | +7,882 |
| 2016-09 | 22 | 59.1% | 4.51 | +8,575 |
| 2016-10 | 17 | 64.7% | 4.05 | +5,585 |
| 2016-11 | 3 | 66.7% | 0.74 | −266 |
| 2016-12 | 23 | 69.6% | 4.95 | +5,093 |
| 2017-01 | 21 | 42.9% | 2.04 | +2,863 |
| 2017-02 | 27 | 44.4% | 1.35 | +1,668 |
| 2017-03 | 36 | 55.6% | 1.68 | +5,510 |
| 2017-04 | 15 | 40.0% | 4.33 | +11,447 |
| 2017-05 | 26 | 65.4% | 3.62 | +12,745 |
| 2017-06 | 17 | 70.6% | 11.17 | +12,713 |
| 2017-07 | 15 | 53.3% | 1.12 | +338 |
| 2017-08 | 16 | 43.8% | 1.61 | +2,388 |
| 2017-09 | 14 | 35.7% | 0.26 | −3,718 |
| 2017-10 | 33 | 63.6% | 3.46 | +12,248 |
| 2017-11 | 11 | 54.5% | 3.53 | +5,785 |
| 2017-12 | 19 | 31.6% | 0.63 | −2,951 |
| 2018-01 | 14 | 50.0% | 1.44 | +1,339 |
| 2018-02 | 18 | 33.3% | 1.04 | +272 |
| 2018-03 | 19 | 42.1% | 5.61 | +28,907 |
| 2018-04 | 6 | 66.7% | 6.41 | +3,248 |
| 2018-05 | 18 | 44.4% | 1.04 | +209 |
| 2018-06 | 42 | 59.5% | 5.55 | +24,690 |
| 2018-07 | 26 | 38.5% | 2.88 | +4,897 |
| 2018-08 | 20 | 45.0% | 1.60 | +2,226 |
| 2018-09 | 29 | 82.8% | 12.38 | +17,391 |
| 2018-10 | 6 | 66.7% | 6.14 | +5,799 |
| 2018-11 | 7 | 57.1% | 2.24 | +1,658 |
| 2018-12 | 8 | 25.0% | 0.52 | −1,896 |
| 2019-01 | 1 | 0.0% | inf | +0 |
| 2019-02 | 8 | 62.5% | 1.58 | +1,312 |
| 2019-03 | 29 | 69.0% | 7.06 | +34,851 |
| 2019-04 | 5 | 60.0% | 11.55 | +2,764 |
| 2019-05 | 15 | 46.7% | 1.74 | +3,108 |
| 2019-06 | 3 | 33.3% | 7.06 | +2,208 |
| 2019-07 | 20 | 35.0% | 1.80 | +4,594 |
| 2019-08 | 15 | 26.7% | 0.39 | −2,705 |
| 2019-09 | 9 | 33.3% | 0.06 | −4,142 |
| 2019-10 | 2 | 50.0% | 0.68 | −21 |
| 2019-11 | 17 | 52.9% | 1.34 | +1,576 |
| 2019-12 | 27 | 81.5% | 4.48 | +12,297 |
| 2020-01 | 22 | 59.1% | 5.36 | +14,119 |
| 2020-02 | 23 | 21.7% | 0.64 | −4,984 |
| 2020-03 | 5 | 40.0% | 1.27 | +695 |
| 2020-04 | 1 | 0.0% | 0.00 | −304 |
| 2020-05 | 12 | 58.3% | 2.06 | +5,874 |
| 2020-06 | 13 | 53.8% | 1.47 | +2,336 |
| 2020-07 | 9 | 55.6% | 11.48 | +13,874 |
| 2020-08 | 22 | 40.9% | 2.73 | +11,758 |
| 2020-09 | 12 | 58.3% | 2.49 | +7,522 |
| 2020-10 | 11 | 18.2% | 0.14 | −3,644 |
| 2020-11 | 15 | 40.0% | 0.72 | −1,438 |
| 2020-12 | 46 | 43.5% | 1.55 | +10,696 |
| 2021-01 | 58 | 62.1% | 25.50 | +353,669 |
| 2021-02 | 85 | 41.2% | 2.30 | +76,108 |
| 2021-03 | 31 | 45.2% | 3.33 | +33,323 |
| 2021-04 | 18 | 33.3% | 0.78 | −1,438 |
| 2021-05 | 11 | 54.5% | 2.22 | +1,380 |
| 2021-06 | 27 | 33.3% | 0.76 | −2,410 |
| 2021-07 | 11 | 72.7% | 6.09 | +5,720 |
| 2021-08 | 15 | 53.3% | 1.03 | +106 |
| 2021-09 | 13 | 38.5% | 3.01 | +5,627 |
| 2021-10 | 7 | 57.1% | 5.74 | +6,911 |
| 2021-11 | 32 | 37.5% | 0.71 | −4,057 |
| 2021-12 | 6 | 66.7% | 3.36 | +4,459 |
| 2022-01 | 4 | 75.0% | 13.23 | +6,404 |
| 2022-02 | 1 | 0.0% | 0.00 | −2,058 |
| 2022-03 | 3 | 33.3% | 1.69 | +721 |
| 2022-04 | 8 | 62.5% | 10.26 | +5,347 |
| 2022-06 | 3 | 33.3% | 0.34 | −957 |
| 2022-08 | 15 | 33.3% | 0.75 | −2,345 |
| 2022-09 | 3 | 0.0% | 0.00 | −1,574 |
| 2022-11 | 4 | 0.0% | 0.00 | −2,253 |
| 2022-12 | 8 | 50.0% | 3.72 | +4,830 |
| 2023-01 | 5 | 20.0% | 0.34 | −1,584 |
| 2023-02 | 5 | 20.0% | 0.60 | −649 |
| 2023-03 | 3 | 100.0% | inf | +1,934 |
| 2023-04 | 3 | 0.0% | 0.00 | −752 |
| 2023-05 | 4 | 25.0% | 1.15 | +116 |
| 2023-06 | 12 | 58.3% | 2.46 | +3,806 |
| 2023-07 | 11 | 72.7% | 12.30 | +11,965 |
| 2023-08 | 14 | 35.7% | 0.30 | −2,629 |
| 2023-09 | 5 | 60.0% | 1.51 | +468 |
| 2023-10 | 1 | 0.0% | 0.00 | −1,180 |
| 2023-11 | 3 | 0.0% | 0.00 | −2,579 |
| 2023-12 | 16 | 68.8% | 1.32 | +1,993 |
| 2024-01 | 19 | 68.4% | 6.76 | +12,260 |
| 2024-02 | 10 | 10.0% | 0.11 | −2,977 |
| 2024-03 | 42 | 50.0% | 2.06 | +12,677 |
| 2024-04 | 17 | 82.4% | 27.18 | +12,554 |
| 2024-05 | 8 | 37.5% | 2.06 | +2,064 |
| 2024-06 | 4 | 100.0% | inf | +3,997 |
| 2024-08 | 7 | 28.6% | 0.40 | −1,792 |
| 2024-09 | 10 | 60.0% | 4.93 | +8,592 |
| 2024-10 | 15 | 40.0% | 1.18 | +1,120 |
| 2024-11 | 33 | 24.2% | 0.41 | −6,208 |
| 2024-12 | 16 | 68.8% | 2.53 | +7,100 |
| 2025-01 | 2 | 0.0% | 0.00 | −1,063 |
| 2025-02 | 24 | 58.3% | 2.07 | +4,509 |
| 2025-03 | 3 | 0.0% | 0.00 | −2,646 |
| 2025-05 | 10 | 50.0% | 0.94 | −331 |
| 2025-06 | 18 | 38.9% | 1.26 | +1,497 |
| 2025-07 | 20 | 55.0% | 1.65 | +5,132 |
| 2025-08 | 9 | 22.2% | 1.61 | +2,226 |
| 2025-09 | 25 | 40.0% | 0.74 | −3,144 |
| 2025-10 | 20 | 60.0% | 6.66 | +29,939 |
| 2025-11 | 5 | 20.0% | 1.08 | +140 |
| 2025-12 | 12 | 25.0% | 0.07 | −6,689 |
| 2026-01 | 17 | 58.8% | 4.44 | +9,638 |
| 2026-02 | 17 | 35.3% | 2.58 | +6,773 |
| 2026-03 | 12 | 75.0% | 14.93 | +26,098 |
| 2026-04 | 5 | 60.0% | 0.90 | −89 |
| 2026-05 | 21 | 52.4% | 1.10 | +1,560 |

</details>

#### Window = 6-day-low stop

half-Kelly 0.130/0.185/0.190 · total **+$1,530,200** Kelly (flat +$1,437,140) · months **178/257 (69%)** · years **22/22** · up:dn **5.3:1** · max DD **-9,649** · worst streak **4mo**

Yearly (Kelly-sized, by exit date):
| year | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005 | 193 | 50.8% | 1.45 | +21,023 |
| 2006 | 261 | 51.0% | 3.03 | +87,511 |
| 2007 | 210 | 49.5% | 2.73 | +69,698 |
| 2008 | 56 | 62.5% | 3.54 | +24,068 |
| 2009 | 67 | 52.2% | 3.27 | +26,880 |
| 2010 | 205 | 53.7% | 3.18 | +86,987 |
| 2011 | 159 | 53.5% | 2.58 | +49,076 |
| 2012 | 114 | 51.8% | 2.86 | +34,317 |
| 2013 | 269 | 54.6% | 2.70 | +98,508 |
| 2014 | 199 | 56.3% | 2.63 | +57,689 |
| 2015 | 145 | 53.8% | 3.66 | +65,566 |
| 2016 | 145 | 60.0% | 3.24 | +36,667 |
| 2017 | 247 | 51.8% | 2.19 | +60,353 |
| 2018 | 213 | 54.0% | 3.24 | +90,302 |
| 2019 | 153 | 54.9% | 2.86 | +63,869 |
| 2020 | 191 | 43.5% | 1.61 | +43,194 |
| 2021 | 312 | 46.2% | 4.63 | +465,067 |
| 2022 | 49 | 40.8% | 1.67 | +12,059 |
| 2023 | 82 | 52.4% | 1.60 | +13,872 |
| 2024 | 182 | 50.0% | 2.25 | +55,854 |
| 2025 | 147 | 44.9% | 1.44 | +23,566 |
| 2026 | 72 | 54.2% | 2.75 | +44,073 |

<details>
<summary>Full monthly table</summary>

| month | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005-01 | 1 | 100.0% | inf | +930 |
| 2005-02 | 15 | 33.3% | 0.52 | −2,151 |
| 2005-03 | 18 | 50.0% | 2.31 | +4,337 |
| 2005-04 | 2 | 50.0% | 2.59 | +3,040 |
| 2005-05 | 4 | 50.0% | 5.70 | +715 |
| 2005-06 | 24 | 58.3% | 0.59 | −3,660 |
| 2005-07 | 27 | 48.1% | 1.08 | +617 |
| 2005-08 | 51 | 52.9% | 1.82 | +9,605 |
| 2005-09 | 14 | 35.7% | 0.52 | −1,303 |
| 2005-10 | 6 | 50.0% | 1.37 | +320 |
| 2005-11 | 14 | 78.6% | 3.69 | +4,843 |
| 2005-12 | 17 | 41.2% | 2.11 | +3,730 |
| 2006-01 | 20 | 50.0% | 2.22 | +5,451 |
| 2006-02 | 34 | 52.9% | 2.11 | +4,398 |
| 2006-03 | 26 | 65.4% | 4.86 | +7,362 |
| 2006-04 | 22 | 50.0% | 9.99 | +29,150 |
| 2006-05 | 45 | 35.6% | 0.96 | −420 |
| 2006-06 | 2 | 50.0% | 48.46 | +15,070 |
| 2006-07 | 9 | 22.2% | 0.02 | −4,560 |
| 2006-08 | 13 | 53.8% | 1.63 | +1,348 |
| 2006-09 | 12 | 50.0% | 2.51 | +2,554 |
| 2006-10 | 16 | 31.2% | 1.10 | +275 |
| 2006-11 | 38 | 52.6% | 2.89 | +11,394 |
| 2006-12 | 24 | 83.3% | 11.93 | +15,489 |
| 2007-01 | 9 | 66.7% | 15.39 | +3,932 |
| 2007-02 | 41 | 43.9% | 1.68 | +4,730 |
| 2007-03 | 13 | 76.9% | 7.60 | +13,359 |
| 2007-04 | 21 | 52.4% | 2.36 | +2,992 |
| 2007-05 | 50 | 50.0% | 1.77 | +7,271 |
| 2007-06 | 30 | 56.7% | 2.93 | +13,077 |
| 2007-07 | 16 | 31.2% | 0.31 | −2,570 |
| 2007-08 | 2 | 0.0% | 0.00 | −273 |
| 2007-09 | 4 | 50.0% | 0.32 | −682 |
| 2007-10 | 15 | 53.3% | 6.49 | +29,228 |
| 2007-11 | 6 | 33.3% | 0.54 | −764 |
| 2007-12 | 3 | 0.0% | 0.00 | −604 |
| 2008-01 | 1 | 100.0% | inf | +2,525 |
| 2008-02 | 4 | 50.0% | 0.32 | −481 |
| 2008-03 | 5 | 80.0% | 12.41 | +2,857 |
| 2008-04 | 2 | 50.0% | 2.01 | +1,213 |
| 2008-05 | 11 | 54.5% | 4.97 | +3,239 |
| 2008-06 | 14 | 85.7% | 59.31 | +16,714 |
| 2008-07 | 3 | 33.3% | 1.85 | +360 |
| 2008-08 | 11 | 45.5% | 0.33 | −3,272 |
| 2008-09 | 3 | 33.3% | 1.06 | +56 |
| 2008-11 | 1 | 100.0% | inf | +20 |
| 2008-12 | 1 | 100.0% | inf | +836 |
| 2009-01 | 3 | 33.3% | 1.31 | +54 |
| 2009-05 | 4 | 100.0% | inf | +4,888 |
| 2009-06 | 4 | 25.0% | 0.23 | −1,475 |
| 2009-07 | 3 | 100.0% | inf | +1,879 |
| 2009-08 | 10 | 10.0% | 0.12 | −2,643 |
| 2009-09 | 9 | 44.4% | 0.60 | −929 |
| 2009-10 | 18 | 61.1% | 9.56 | +19,490 |
| 2009-11 | 3 | 100.0% | inf | +1,025 |
| 2009-12 | 13 | 53.8% | 3.14 | +4,590 |
| 2010-01 | 30 | 46.7% | 1.86 | +5,707 |
| 2010-02 | 8 | 50.0% | 5.49 | +1,527 |
| 2010-03 | 17 | 58.8% | 3.48 | +6,532 |
| 2010-04 | 21 | 52.4% | 4.87 | +14,708 |
| 2010-05 | 24 | 33.3% | 1.50 | +3,988 |
| 2010-08 | 11 | 45.5% | 0.65 | −857 |
| 2010-09 | 7 | 85.7% | 643.26 | +14,598 |
| 2010-10 | 13 | 46.2% | 3.09 | +5,417 |
| 2010-11 | 48 | 58.3% | 2.58 | +17,510 |
| 2010-12 | 26 | 69.2% | 8.66 | +17,857 |
| 2011-01 | 29 | 62.1% | 1.87 | +7,965 |
| 2011-02 | 38 | 60.5% | 4.06 | +16,184 |
| 2011-03 | 23 | 52.2% | 7.12 | +11,110 |
| 2011-04 | 9 | 33.3% | 0.51 | −1,170 |
| 2011-05 | 31 | 38.7% | 1.31 | +2,821 |
| 2011-06 | 3 | 100.0% | inf | +6,072 |
| 2011-07 | 7 | 57.1% | 10.74 | +1,972 |
| 2011-08 | 2 | 100.0% | inf | +455 |
| 2011-09 | 2 | 0.0% | 0.00 | −1,073 |
| 2011-10 | 3 | 66.7% | 7.30 | +1,513 |
| 2011-11 | 7 | 42.9% | 0.91 | −106 |
| 2011-12 | 5 | 60.0% | 7.39 | +3,333 |
| 2012-01 | 3 | 33.3% | 0.37 | −473 |
| 2012-02 | 13 | 69.2% | 1.62 | +1,023 |
| 2012-03 | 18 | 61.1% | 3.66 | +7,513 |
| 2012-04 | 9 | 33.3% | 2.51 | +2,829 |
| 2012-05 | 12 | 41.7% | 0.53 | −1,502 |
| 2012-06 | 7 | 28.6% | 1.13 | +215 |
| 2012-07 | 9 | 44.4% | 22.10 | +11,593 |
| 2012-08 | 13 | 61.5% | 1.55 | +1,077 |
| 2012-09 | 16 | 68.8% | 10.39 | +11,480 |
| 2012-10 | 8 | 37.5% | 1.85 | +1,353 |
| 2012-11 | 2 | 0.0% | 0.00 | −376 |
| 2012-12 | 4 | 50.0% | 0.48 | −414 |
| 2013-01 | 11 | 27.3% | 0.44 | −2,623 |
| 2013-02 | 33 | 54.5% | 1.32 | +2,688 |
| 2013-03 | 22 | 81.8% | 87.41 | +22,525 |
| 2013-04 | 21 | 66.7% | 2.01 | +6,427 |
| 2013-05 | 23 | 65.2% | 4.34 | +9,013 |
| 2013-06 | 18 | 77.8% | 10.24 | +20,863 |
| 2013-07 | 9 | 44.4% | 3.50 | +2,322 |
| 2013-08 | 42 | 45.2% | 1.60 | +5,401 |
| 2013-09 | 12 | 58.3% | 6.31 | +10,152 |
| 2013-10 | 21 | 33.3% | 1.65 | +6,282 |
| 2013-11 | 38 | 47.4% | 1.91 | +7,353 |
| 2013-12 | 19 | 52.6% | 3.21 | +8,107 |
| 2014-01 | 45 | 57.8% | 2.47 | +10,814 |
| 2014-02 | 17 | 41.2% | 0.86 | −457 |
| 2014-03 | 37 | 56.8% | 2.26 | +11,994 |
| 2014-04 | 9 | 55.6% | 3.64 | +2,847 |
| 2014-05 | 7 | 71.4% | 36.81 | +3,735 |
| 2014-06 | 19 | 52.6% | 5.94 | +14,499 |
| 2014-07 | 18 | 50.0% | 1.90 | +2,629 |
| 2014-08 | 2 | 50.0% | 13.21 | +1,776 |
| 2014-09 | 14 | 35.7% | 0.72 | −1,407 |
| 2014-11 | 15 | 93.3% | 269.90 | +8,100 |
| 2014-12 | 16 | 56.2% | 2.08 | +3,158 |
| 2015-01 | 3 | 33.3% | 7.83 | +3,048 |
| 2015-02 | 11 | 36.4% | 1.18 | +426 |
| 2015-03 | 37 | 62.2% | 5.75 | +24,964 |
| 2015-04 | 23 | 52.2% | 1.87 | +4,520 |
| 2015-05 | 18 | 44.4% | 3.98 | +5,706 |
| 2015-06 | 10 | 60.0% | 5.34 | +6,822 |
| 2015-07 | 11 | 54.5% | 2.32 | +3,163 |
| 2015-08 | 1 | 0.0% | 0.00 | −716 |
| 2015-09 | 1 | 0.0% | inf | +0 |
| 2015-10 | 2 | 50.0% | 10.86 | +1,061 |
| 2015-11 | 21 | 57.1% | 3.70 | +11,326 |
| 2015-12 | 7 | 71.4% | 14.99 | +5,246 |
| 2016-01 | 2 | 50.0% | 0.32 | −629 |
| 2016-02 | 1 | 0.0% | 0.00 | −26 |
| 2016-03 | 7 | 14.3% | 0.70 | −625 |
| 2016-04 | 6 | 66.7% | 5.46 | +2,138 |
| 2016-05 | 7 | 57.1% | 0.93 | −105 |
| 2016-06 | 11 | 63.6% | 7.43 | +7,755 |
| 2016-07 | 1 | 100.0% | inf | +124 |
| 2016-08 | 47 | 59.6% | 3.86 | +12,181 |
| 2016-09 | 19 | 68.4% | 6.19 | +6,907 |
| 2016-10 | 15 | 60.0% | 2.72 | +3,249 |
| 2016-11 | 3 | 66.7% | 0.76 | −247 |
| 2016-12 | 26 | 65.4% | 4.66 | +5,946 |
| 2017-01 | 19 | 47.4% | 1.75 | +1,698 |
| 2017-02 | 28 | 46.4% | 1.49 | +2,312 |
| 2017-03 | 36 | 55.6% | 2.10 | +8,673 |
| 2017-04 | 14 | 35.7% | 3.45 | +8,441 |
| 2017-05 | 25 | 68.0% | 3.63 | +12,639 |
| 2017-06 | 17 | 70.6% | 13.73 | +13,780 |
| 2017-07 | 16 | 56.2% | 1.58 | +1,722 |
| 2017-08 | 17 | 41.2% | 0.95 | −228 |
| 2017-09 | 14 | 35.7% | 0.30 | −3,516 |
| 2017-10 | 32 | 59.4% | 3.61 | +11,679 |
| 2017-11 | 10 | 60.0% | 4.03 | +6,028 |
| 2017-12 | 19 | 31.6% | 0.64 | −2,874 |
| 2018-01 | 17 | 58.8% | 3.02 | +6,165 |
| 2018-02 | 15 | 20.0% | 0.37 | −4,394 |
| 2018-03 | 19 | 42.1% | 5.76 | +29,373 |
| 2018-04 | 6 | 66.7% | 10.99 | +3,626 |
| 2018-05 | 19 | 47.4% | 1.58 | +3,209 |
| 2018-06 | 42 | 61.9% | 5.10 | +21,266 |
| 2018-07 | 25 | 40.0% | 2.49 | +3,652 |
| 2018-08 | 22 | 54.5% | 2.52 | +5,310 |
| 2018-09 | 28 | 82.1% | 11.58 | +16,030 |
| 2018-10 | 5 | 60.0% | 7.04 | +5,295 |
| 2018-11 | 8 | 62.5% | 3.32 | +2,335 |
| 2018-12 | 7 | 28.6% | 0.57 | −1,565 |
| 2019-01 | 1 | 0.0% | inf | +0 |
| 2019-02 | 10 | 70.0% | 2.55 | +3,471 |
| 2019-03 | 27 | 66.7% | 7.06 | +34,741 |
| 2019-04 | 6 | 50.0% | 2.98 | +1,995 |
| 2019-05 | 14 | 50.0% | 2.53 | +4,729 |
| 2019-06 | 3 | 33.3% | 7.06 | +2,191 |
| 2019-07 | 20 | 35.0% | 1.94 | +5,030 |
| 2019-08 | 15 | 26.7% | 0.42 | −2,422 |
| 2019-09 | 9 | 33.3% | 0.06 | −3,955 |
| 2019-10 | 2 | 50.0% | 0.68 | −21 |
| 2019-11 | 20 | 60.0% | 1.67 | +3,138 |
| 2019-12 | 26 | 80.8% | 5.21 | +14,973 |
| 2020-01 | 22 | 59.1% | 4.44 | +10,362 |
| 2020-02 | 22 | 22.7% | 0.46 | −7,213 |
| 2020-03 | 4 | 25.0% | 0.17 | −2,135 |
| 2020-04 | 1 | 0.0% | 0.00 | −302 |
| 2020-05 | 14 | 57.1% | 2.19 | +7,279 |
| 2020-06 | 13 | 46.2% | 0.80 | −1,132 |
| 2020-07 | 8 | 62.5% | 17.15 | +10,158 |
| 2020-08 | 23 | 34.8% | 1.91 | +8,450 |
| 2020-09 | 10 | 70.0% | 6.49 | +10,682 |
| 2020-10 | 12 | 25.0% | 0.24 | −3,134 |
| 2020-11 | 14 | 35.7% | 0.77 | −1,165 |
| 2020-12 | 48 | 45.8% | 1.59 | +11,342 |
| 2021-01 | 60 | 61.7% | 23.24 | +353,572 |
| 2021-02 | 83 | 37.3% | 2.02 | +60,173 |
| 2021-03 | 30 | 46.7% | 3.40 | +33,895 |
| 2021-04 | 17 | 35.3% | 1.09 | +439 |
| 2021-05 | 12 | 58.3% | 2.56 | +1,776 |
| 2021-06 | 29 | 31.0% | 0.68 | −3,208 |
| 2021-07 | 10 | 80.0% | 7.43 | +4,821 |
| 2021-08 | 13 | 46.2% | 0.52 | −1,466 |
| 2021-09 | 13 | 38.5% | 3.30 | +6,448 |
| 2021-10 | 7 | 57.1% | 5.64 | +6,832 |
| 2021-11 | 33 | 39.4% | 0.72 | −3,866 |
| 2021-12 | 5 | 80.0% | 8.31 | +5,650 |
| 2022-01 | 4 | 75.0% | 14.26 | +7,025 |
| 2022-02 | 1 | 0.0% | 0.00 | −2,042 |
| 2022-03 | 3 | 33.3% | 1.66 | +695 |
| 2022-04 | 8 | 62.5% | 14.17 | +5,500 |
| 2022-06 | 3 | 33.3% | 0.73 | −394 |
| 2022-08 | 15 | 33.3% | 0.91 | −707 |
| 2022-09 | 3 | 0.0% | 0.00 | −1,562 |
| 2022-11 | 4 | 25.0% | 0.04 | −1,445 |
| 2022-12 | 8 | 50.0% | 3.98 | +4,989 |
| 2023-01 | 5 | 40.0% | 0.61 | −793 |
| 2023-02 | 5 | 40.0% | 1.04 | +67 |
| 2023-03 | 3 | 100.0% | inf | +1,985 |
| 2023-04 | 3 | 33.3% | 0.09 | −670 |
| 2023-05 | 4 | 25.0% | 1.15 | +115 |
| 2023-06 | 12 | 58.3% | 2.59 | +4,150 |
| 2023-07 | 13 | 61.5% | 8.89 | +12,067 |
| 2023-08 | 12 | 41.7% | 0.36 | −2,060 |
| 2023-09 | 5 | 60.0% | 2.23 | +764 |
| 2023-10 | 1 | 0.0% | 0.00 | −1,168 |
| 2023-11 | 3 | 0.0% | 0.00 | −2,575 |
| 2023-12 | 16 | 68.8% | 1.32 | +1,992 |
| 2024-01 | 19 | 73.7% | 7.35 | +13,545 |
| 2024-02 | 10 | 10.0% | 0.10 | −2,980 |
| 2024-03 | 46 | 52.2% | 2.44 | +16,215 |
| 2024-04 | 13 | 76.9% | 24.19 | +11,039 |
| 2024-05 | 9 | 44.4% | 2.89 | +3,711 |
| 2024-06 | 3 | 100.0% | inf | +3,240 |
| 2024-08 | 8 | 37.5% | 2.96 | +5,799 |
| 2024-09 | 9 | 55.6% | 1.98 | +2,068 |
| 2024-10 | 15 | 40.0% | 1.25 | +1,424 |
| 2024-11 | 34 | 26.5% | 0.43 | −5,756 |
| 2024-12 | 16 | 75.0% | 2.70 | +7,549 |
| 2025-01 | 3 | 33.3% | 1.09 | +100 |
| 2025-02 | 22 | 59.1% | 1.36 | +1,433 |
| 2025-03 | 3 | 0.0% | 0.00 | −2,351 |
| 2025-05 | 11 | 54.5% | 0.87 | −712 |
| 2025-06 | 17 | 41.2% | 1.70 | +3,481 |
| 2025-07 | 20 | 55.0% | 1.70 | +5,384 |
| 2025-08 | 10 | 20.0% | 1.42 | +1,741 |
| 2025-09 | 28 | 42.9% | 1.09 | +1,147 |
| 2025-10 | 16 | 62.5% | 5.70 | +18,864 |
| 2025-11 | 5 | 20.0% | 1.08 | +139 |
| 2025-12 | 12 | 25.0% | 0.08 | −5,660 |
| 2026-01 | 18 | 55.6% | 4.90 | +9,873 |
| 2026-02 | 16 | 37.5% | 2.54 | +6,492 |
| 2026-03 | 12 | 75.0% | 15.15 | +26,313 |
| 2026-04 | 5 | 60.0% | 0.88 | −102 |
| 2026-05 | 21 | 52.4% | 1.10 | +1,497 |

</details>

#### Window = 5-day-low stop

half-Kelly 0.132/0.193/0.201 · total **+$1,541,855** Kelly (flat +$1,435,900) · months **180/257 (70%)** · years **22/22** · up:dn **6.2:1** · max DD **-9,664** · worst streak **3mo**

Yearly (Kelly-sized, by exit date):
| year | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005 | 194 | 50.0% | 1.45 | +20,177 |
| 2006 | 261 | 51.7% | 2.86 | +73,136 |
| 2007 | 209 | 49.8% | 2.78 | +68,679 |
| 2008 | 56 | 64.3% | 3.33 | +22,183 |
| 2009 | 70 | 54.3% | 3.73 | +31,037 |
| 2010 | 205 | 55.6% | 3.17 | +83,729 |
| 2011 | 156 | 53.8% | 2.74 | +47,367 |
| 2012 | 114 | 54.4% | 3.36 | +40,953 |
| 2013 | 273 | 56.0% | 2.78 | +98,918 |
| 2014 | 195 | 54.9% | 2.48 | +50,644 |
| 2015 | 145 | 54.5% | 4.12 | +69,291 |
| 2016 | 147 | 58.5% | 2.91 | +30,924 |
| 2017 | 246 | 54.5% | 2.25 | +61,426 |
| 2018 | 212 | 55.7% | 3.50 | +92,983 |
| 2019 | 153 | 54.2% | 2.98 | +62,216 |
| 2020 | 196 | 44.4% | 1.63 | +44,553 |
| 2021 | 307 | 47.2% | 5.12 | +486,748 |
| 2022 | 49 | 44.9% | 1.94 | +15,448 |
| 2023 | 83 | 55.4% | 1.87 | +20,266 |
| 2024 | 182 | 50.0% | 2.16 | +48,301 |
| 2025 | 150 | 46.7% | 1.57 | +30,283 |
| 2026 | 68 | 57.4% | 2.79 | +42,595 |

<details>
<summary>Full monthly table</summary>

| month | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005-01 | 1 | 100.0% | inf | +605 |
| 2005-02 | 17 | 29.4% | 0.76 | −1,153 |
| 2005-03 | 16 | 56.2% | 3.97 | +6,171 |
| 2005-04 | 2 | 50.0% | 2.52 | +2,933 |
| 2005-05 | 4 | 50.0% | 7.37 | +965 |
| 2005-06 | 27 | 59.3% | 0.69 | −2,875 |
| 2005-07 | 27 | 48.1% | 1.05 | +380 |
| 2005-08 | 50 | 52.0% | 1.81 | +8,625 |
| 2005-09 | 15 | 33.3% | 0.36 | −1,792 |
| 2005-10 | 5 | 40.0% | 1.20 | +171 |
| 2005-11 | 13 | 69.2% | 1.76 | +1,571 |
| 2005-12 | 17 | 47.1% | 2.50 | +4,575 |
| 2006-01 | 21 | 52.4% | 1.60 | +2,702 |
| 2006-02 | 34 | 58.8% | 2.44 | +5,183 |
| 2006-03 | 25 | 60.0% | 3.83 | +5,245 |
| 2006-04 | 23 | 56.5% | 11.11 | +31,337 |
| 2006-05 | 45 | 35.6% | 1.18 | +1,751 |
| 2006-07 | 9 | 22.2% | 0.03 | −3,431 |
| 2006-08 | 14 | 57.1% | 1.79 | +1,632 |
| 2006-09 | 11 | 45.5% | 2.65 | +2,224 |
| 2006-10 | 17 | 41.2% | 1.13 | +310 |
| 2006-11 | 39 | 51.3% | 3.31 | +13,428 |
| 2006-12 | 23 | 78.3% | 9.86 | +12,753 |
| 2007-01 | 8 | 75.0% | 24.12 | +3,962 |
| 2007-02 | 44 | 47.7% | 2.21 | +8,018 |
| 2007-03 | 10 | 60.0% | 6.54 | +7,299 |
| 2007-04 | 23 | 56.5% | 2.72 | +3,704 |
| 2007-05 | 50 | 52.0% | 1.81 | +7,910 |
| 2007-06 | 28 | 53.6% | 2.99 | +12,421 |
| 2007-07 | 16 | 31.2% | 0.32 | −2,384 |
| 2007-08 | 2 | 0.0% | 0.00 | −269 |
| 2007-09 | 4 | 50.0% | 0.31 | −698 |
| 2007-10 | 17 | 47.1% | 5.70 | +29,059 |
| 2007-11 | 4 | 50.0% | 1.41 | +261 |
| 2007-12 | 3 | 0.0% | 0.00 | −603 |
| 2008-01 | 1 | 100.0% | inf | +1,669 |
| 2008-02 | 4 | 50.0% | 0.31 | −487 |
| 2008-03 | 5 | 80.0% | 9.49 | +2,091 |
| 2008-04 | 2 | 50.0% | 2.30 | +1,340 |
| 2008-05 | 12 | 58.3% | 5.20 | +3,425 |
| 2008-06 | 13 | 92.3% | 698.54 | +16,376 |
| 2008-07 | 3 | 33.3% | 1.85 | +354 |
| 2008-08 | 11 | 45.5% | 0.36 | −3,328 |
| 2008-09 | 3 | 33.3% | 0.91 | −97 |
| 2008-11 | 1 | 100.0% | inf | +20 |
| 2008-12 | 1 | 100.0% | inf | +822 |
| 2009-01 | 3 | 33.3% | 1.31 | +54 |
| 2009-04 | 1 | 0.0% | 0.00 | −107 |
| 2009-05 | 3 | 100.0% | inf | +4,522 |
| 2009-06 | 4 | 25.0% | 0.22 | −1,499 |
| 2009-07 | 3 | 100.0% | inf | +1,877 |
| 2009-08 | 11 | 27.3% | 0.69 | −771 |
| 2009-09 | 8 | 37.5% | 0.39 | −1,315 |
| 2009-10 | 18 | 61.1% | 9.43 | +19,373 |
| 2009-11 | 3 | 66.7% | 3.05 | +182 |
| 2009-12 | 16 | 68.8% | 5.14 | +8,722 |
| 2010-01 | 28 | 46.4% | 1.60 | +3,519 |
| 2010-02 | 7 | 57.1% | 6.90 | +1,667 |
| 2010-03 | 18 | 61.1% | 3.84 | +7,569 |
| 2010-04 | 21 | 52.4% | 5.18 | +14,113 |
| 2010-05 | 23 | 39.1% | 1.35 | +2,902 |
| 2010-08 | 11 | 45.5% | 0.78 | −466 |
| 2010-09 | 8 | 87.5% | 679.68 | +15,161 |
| 2010-10 | 17 | 64.7% | 6.33 | +11,685 |
| 2010-11 | 46 | 50.0% | 1.60 | +7,211 |
| 2010-12 | 26 | 76.9% | 11.40 | +20,368 |
| 2011-01 | 28 | 57.1% | 1.79 | +5,536 |
| 2011-02 | 39 | 61.5% | 4.99 | +22,205 |
| 2011-03 | 20 | 50.0% | 8.04 | +7,501 |
| 2011-04 | 10 | 40.0% | 0.83 | −354 |
| 2011-05 | 31 | 41.9% | 1.61 | +4,872 |
| 2011-06 | 2 | 100.0% | inf | +3,301 |
| 2011-07 | 7 | 57.1% | 11.13 | +2,026 |
| 2011-08 | 2 | 100.0% | inf | +765 |
| 2011-09 | 2 | 0.0% | 0.00 | −717 |
| 2011-10 | 3 | 66.7% | 7.60 | +1,559 |
| 2011-11 | 9 | 55.6% | 0.95 | −114 |
| 2011-12 | 3 | 66.7% | 12.40 | +786 |
| 2012-01 | 3 | 66.7% | 1.76 | +461 |
| 2012-02 | 14 | 64.3% | 1.55 | +954 |
| 2012-03 | 17 | 64.7% | 3.83 | +8,108 |
| 2012-04 | 10 | 50.0% | 3.99 | +4,926 |
| 2012-05 | 12 | 50.0% | 0.25 | −2,170 |
| 2012-06 | 6 | 16.7% | 0.14 | −1,366 |
| 2012-07 | 10 | 50.0% | 33.09 | +17,357 |
| 2012-08 | 12 | 58.3% | 0.83 | −344 |
| 2012-09 | 17 | 70.6% | 11.21 | +12,443 |
| 2012-10 | 7 | 28.6% | 2.11 | +1,276 |
| 2012-11 | 2 | 0.0% | 0.00 | −370 |
| 2012-12 | 4 | 50.0% | 0.60 | −322 |
| 2013-01 | 16 | 43.8% | 0.79 | −995 |
| 2013-02 | 30 | 56.7% | 1.31 | +2,436 |
| 2013-03 | 23 | 78.3% | 5.52 | +14,645 |
| 2013-04 | 20 | 65.0% | 2.98 | +8,243 |
| 2013-05 | 23 | 65.2% | 4.93 | +10,076 |
| 2013-06 | 16 | 75.0% | 14.54 | +20,264 |
| 2013-07 | 9 | 55.6% | 3.53 | +2,391 |
| 2013-08 | 43 | 48.8% | 1.89 | +7,796 |
| 2013-09 | 12 | 66.7% | 8.83 | +10,181 |
| 2013-10 | 21 | 33.3% | 1.56 | +4,987 |
| 2013-11 | 41 | 46.3% | 1.93 | +7,878 |
| 2013-12 | 19 | 57.9% | 4.46 | +11,017 |
| 2014-01 | 44 | 50.0% | 1.62 | +4,621 |
| 2014-02 | 15 | 40.0% | 0.60 | −1,153 |
| 2014-03 | 39 | 59.0% | 2.33 | +12,473 |
| 2014-04 | 7 | 28.6% | 1.42 | +523 |
| 2014-05 | 6 | 66.7% | 36.38 | +3,627 |
| 2014-06 | 19 | 52.6% | 5.92 | +14,440 |
| 2014-07 | 19 | 57.9% | 2.65 | +4,646 |
| 2014-08 | 1 | 0.0% | 0.00 | −147 |
| 2014-09 | 14 | 35.7% | 0.80 | −960 |
| 2014-11 | 18 | 94.4% | 322.26 | +9,511 |
| 2014-12 | 13 | 53.8% | 2.17 | +3,062 |
| 2015-01 | 3 | 33.3% | 7.83 | +2,996 |
| 2015-02 | 14 | 50.0% | 1.77 | +1,817 |
| 2015-03 | 37 | 62.2% | 7.49 | +30,840 |
| 2015-04 | 22 | 54.5% | 1.20 | +843 |
| 2015-05 | 17 | 47.1% | 5.79 | +7,581 |
| 2015-06 | 12 | 58.3% | 5.32 | +6,507 |
| 2015-07 | 8 | 50.0% | 1.88 | +1,903 |
| 2015-08 | 1 | 0.0% | 0.00 | −703 |
| 2015-09 | 1 | 0.0% | inf | +0 |
| 2015-10 | 2 | 50.0% | 10.56 | +1,040 |
| 2015-11 | 23 | 52.2% | 3.63 | +11,302 |
| 2015-12 | 5 | 80.0% | 34.88 | +5,166 |
| 2016-01 | 2 | 50.0% | 0.32 | −636 |
| 2016-02 | 1 | 0.0% | 0.00 | −7 |
| 2016-03 | 8 | 25.0% | 0.58 | −837 |
| 2016-04 | 6 | 50.0% | 2.75 | +895 |
| 2016-05 | 8 | 50.0% | 1.06 | +92 |
| 2016-06 | 9 | 55.6% | 4.35 | +3,768 |
| 2016-07 | 1 | 100.0% | inf | +212 |
| 2016-08 | 50 | 62.0% | 4.29 | +12,762 |
| 2016-09 | 16 | 62.5% | 5.10 | +5,480 |
| 2016-10 | 15 | 60.0% | 3.07 | +3,849 |
| 2016-11 | 3 | 66.7% | 0.75 | −263 |
| 2016-12 | 28 | 64.3% | 3.76 | +5,610 |
| 2017-01 | 17 | 41.2% | 1.51 | +1,182 |
| 2017-02 | 31 | 54.8% | 3.16 | +10,080 |
| 2017-03 | 36 | 61.1% | 1.91 | +6,961 |
| 2017-04 | 11 | 27.3% | 1.24 | +783 |
| 2017-05 | 27 | 70.4% | 4.23 | +15,319 |
| 2017-06 | 18 | 83.3% | 16.60 | +14,215 |
| 2017-07 | 14 | 50.0% | 0.86 | −408 |
| 2017-08 | 16 | 37.5% | 1.25 | +827 |
| 2017-09 | 14 | 35.7% | 0.30 | −3,482 |
| 2017-10 | 32 | 62.5% | 3.73 | +12,182 |
| 2017-11 | 10 | 60.0% | 3.90 | +5,671 |
| 2017-12 | 20 | 35.0% | 0.76 | −1,905 |
| 2018-01 | 17 | 64.7% | 3.36 | +6,361 |
| 2018-02 | 15 | 26.7% | 0.52 | −2,906 |
| 2018-03 | 19 | 42.1% | 6.08 | +29,581 |
| 2018-04 | 5 | 60.0% | 10.57 | +3,470 |
| 2018-05 | 20 | 50.0% | 1.83 | +4,589 |
| 2018-06 | 43 | 60.5% | 4.70 | +18,695 |
| 2018-07 | 24 | 45.8% | 3.01 | +3,232 |
| 2018-08 | 23 | 60.9% | 3.67 | +8,130 |
| 2018-09 | 26 | 80.8% | 11.66 | +16,156 |
| 2018-10 | 5 | 60.0% | 7.22 | +5,608 |
| 2018-11 | 9 | 66.7% | 3.93 | +2,955 |
| 2018-12 | 6 | 16.7% | 0.20 | −2,887 |
| 2019-01 | 1 | 0.0% | inf | +0 |
| 2019-02 | 11 | 72.7% | 2.42 | +3,167 |
| 2019-03 | 27 | 63.0% | 7.24 | +33,658 |
| 2019-04 | 6 | 50.0% | 2.59 | +1,575 |
| 2019-05 | 14 | 57.1% | 6.02 | +7,766 |
| 2019-06 | 2 | 50.0% | 7.69 | +2,183 |
| 2019-07 | 20 | 35.0% | 2.24 | +6,438 |
| 2019-08 | 16 | 31.2% | 0.49 | −1,940 |
| 2019-09 | 8 | 25.0% | 0.03 | −4,069 |
| 2019-10 | 4 | 25.0% | 0.19 | −190 |
| 2019-11 | 19 | 57.9% | 1.00 | +13 |
| 2019-12 | 25 | 80.0% | 4.82 | +13,614 |
| 2020-01 | 23 | 52.2% | 2.64 | +5,754 |
| 2020-02 | 22 | 22.7% | 0.42 | −8,073 |
| 2020-03 | 3 | 33.3% | 0.30 | −993 |
| 2020-04 | 2 | 0.0% | 0.00 | −598 |
| 2020-05 | 13 | 61.5% | 2.53 | +8,017 |
| 2020-06 | 14 | 57.1% | 1.45 | +1,886 |
| 2020-07 | 7 | 57.1% | 14.67 | +8,746 |
| 2020-08 | 24 | 37.5% | 1.98 | +8,609 |
| 2020-09 | 9 | 66.7% | 9.17 | +12,169 |
| 2020-10 | 12 | 33.3% | 0.28 | −2,666 |
| 2020-11 | 14 | 35.7% | 0.78 | −1,143 |
| 2020-12 | 53 | 47.2% | 1.59 | +12,846 |
| 2021-01 | 57 | 64.9% | 27.48 | +363,917 |
| 2021-02 | 82 | 41.5% | 2.21 | +65,400 |
| 2021-03 | 31 | 45.2% | 3.64 | +34,448 |
| 2021-04 | 15 | 33.3% | 1.18 | +773 |
| 2021-05 | 12 | 58.3% | 2.96 | +2,221 |
| 2021-06 | 29 | 34.5% | 0.73 | −2,606 |
| 2021-07 | 10 | 80.0% | 8.52 | +5,538 |
| 2021-08 | 13 | 46.2% | 0.72 | −660 |
| 2021-09 | 15 | 40.0% | 4.18 | +8,963 |
| 2021-10 | 5 | 40.0% | 5.14 | +6,149 |
| 2021-11 | 33 | 36.4% | 0.78 | −3,121 |
| 2021-12 | 5 | 80.0% | 8.54 | +5,727 |
| 2022-01 | 4 | 75.0% | 14.21 | +7,083 |
| 2022-02 | 1 | 0.0% | 0.00 | −2,007 |
| 2022-03 | 3 | 33.3% | 1.61 | +653 |
| 2022-04 | 8 | 62.5% | 14.96 | +5,801 |
| 2022-06 | 3 | 33.3% | 0.73 | −387 |
| 2022-07 | 1 | 100.0% | inf | +48 |
| 2022-08 | 14 | 35.7% | 1.18 | +1,155 |
| 2022-09 | 3 | 0.0% | 0.00 | −1,535 |
| 2022-11 | 4 | 25.0% | 0.04 | −1,468 |
| 2022-12 | 8 | 62.5% | 4.72 | +6,106 |
| 2023-01 | 5 | 40.0% | 0.88 | −233 |
| 2023-02 | 5 | 40.0% | 1.18 | +272 |
| 2023-03 | 3 | 100.0% | inf | +1,965 |
| 2023-04 | 4 | 50.0% | 1.31 | +220 |
| 2023-05 | 3 | 0.0% | 0.00 | −741 |
| 2023-06 | 12 | 58.3% | 2.80 | +4,675 |
| 2023-07 | 13 | 61.5% | 12.18 | +14,413 |
| 2023-08 | 13 | 38.5% | 0.30 | −3,370 |
| 2023-09 | 4 | 100.0% | inf | +1,436 |
| 2023-10 | 1 | 0.0% | 0.00 | −1,196 |
| 2023-11 | 3 | 0.0% | 0.00 | −2,554 |
| 2023-12 | 17 | 76.5% | 1.90 | +5,379 |
| 2024-01 | 18 | 72.2% | 7.05 | +12,031 |
| 2024-02 | 13 | 23.1% | 0.18 | −3,621 |
| 2024-03 | 43 | 48.8% | 2.60 | +15,765 |
| 2024-04 | 14 | 85.7% | 51.14 | +14,022 |
| 2024-05 | 8 | 37.5% | 0.80 | −399 |
| 2024-06 | 3 | 100.0% | inf | +3,193 |
| 2024-07 | 1 | 100.0% | inf | +48 |
| 2024-08 | 8 | 37.5% | 1.83 | +1,626 |
| 2024-09 | 8 | 50.0% | 1.77 | +1,585 |
| 2024-10 | 16 | 37.5% | 1.30 | +1,679 |
| 2024-11 | 35 | 31.4% | 0.66 | −3,016 |
| 2024-12 | 15 | 73.3% | 2.21 | +5,388 |
| 2025-01 | 2 | 50.0% | 1.06 | +69 |
| 2025-02 | 24 | 54.2% | 0.98 | −127 |
| 2025-03 | 1 | 0.0% | 0.00 | −655 |
| 2025-05 | 12 | 58.3% | 1.10 | +550 |
| 2025-06 | 18 | 44.4% | 1.77 | +4,073 |
| 2025-07 | 19 | 57.9% | 2.21 | +9,113 |
| 2025-08 | 9 | 11.1% | 0.54 | −1,637 |
| 2025-09 | 31 | 45.2% | 1.28 | +3,789 |
| 2025-10 | 13 | 61.5% | 8.08 | +17,317 |
| 2025-11 | 5 | 20.0% | 1.25 | +361 |
| 2025-12 | 16 | 37.5% | 0.64 | −2,571 |
| 2026-01 | 16 | 68.8% | 9.54 | +9,653 |
| 2026-02 | 17 | 47.1% | 2.60 | +6,680 |
| 2026-03 | 9 | 66.7% | 14.67 | +24,992 |
| 2026-04 | 5 | 60.0% | 1.41 | +366 |
| 2026-05 | 21 | 52.4% | 1.06 | +904 |

</details>

#### Window = 4-day-low stop ⭐ (default)

half-Kelly 0.142/0.196/0.214 · total **+$1,476,605** Kelly (flat +$1,387,731) · months **185/257 (72%)** · years **22/22** · up:dn **6.7:1** · max DD **-8,976** · worst streak **3mo**

Yearly (Kelly-sized, by exit date):
| year | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005 | 195 | 51.8% | 1.53 | +22,404 |
| 2006 | 261 | 51.7% | 2.74 | +64,476 |
| 2007 | 209 | 50.7% | 3.28 | +73,814 |
| 2008 | 55 | 67.3% | 3.23 | +19,897 |
| 2009 | 71 | 56.3% | 3.62 | +30,144 |
| 2010 | 206 | 55.8% | 3.28 | +81,576 |
| 2011 | 154 | 55.2% | 2.75 | +44,921 |
| 2012 | 115 | 54.8% | 4.11 | +45,518 |
| 2013 | 274 | 58.0% | 2.96 | +94,913 |
| 2014 | 193 | 58.0% | 2.77 | +56,922 |
| 2015 | 145 | 56.6% | 3.93 | +61,686 |
| 2016 | 150 | 58.0% | 3.10 | +33,799 |
| 2017 | 245 | 55.5% | 2.06 | +49,863 |
| 2018 | 210 | 57.1% | 3.55 | +83,865 |
| 2019 | 156 | 56.4% | 3.06 | +60,035 |
| 2020 | 200 | 48.0% | 1.84 | +55,342 |
| 2021 | 301 | 49.5% | 4.91 | +446,104 |
| 2022 | 49 | 49.0% | 1.80 | +12,528 |
| 2023 | 84 | 57.1% | 1.75 | +16,959 |
| 2024 | 180 | 51.7% | 2.23 | +48,675 |
| 2025 | 152 | 50.0% | 1.69 | +34,558 |
| 2026 | 66 | 57.6% | 2.70 | +38,605 |

<details>
<summary>Full monthly table</summary>

| month | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005-01 | 1 | 100.0% | inf | +621 |
| 2005-02 | 22 | 40.9% | 0.99 | −41 |
| 2005-03 | 12 | 66.7% | 5.85 | +6,492 |
| 2005-04 | 1 | 0.0% | 0.00 | −1,870 |
| 2005-05 | 5 | 40.0% | 4.17 | +821 |
| 2005-06 | 26 | 57.7% | 0.75 | −2,231 |
| 2005-07 | 28 | 50.0% | 1.42 | +2,749 |
| 2005-08 | 49 | 55.1% | 2.43 | +12,649 |
| 2005-09 | 15 | 40.0% | 0.39 | −1,656 |
| 2005-10 | 5 | 40.0% | 1.20 | +175 |
| 2005-11 | 16 | 68.8% | 1.84 | +1,818 |
| 2005-12 | 15 | 40.0% | 1.96 | +2,878 |
| 2006-01 | 21 | 52.4% | 1.90 | +3,764 |
| 2006-02 | 33 | 57.6% | 1.69 | +2,579 |
| 2006-03 | 27 | 59.3% | 3.79 | +5,580 |
| 2006-04 | 22 | 59.1% | 12.57 | +30,779 |
| 2006-05 | 44 | 38.6% | 1.12 | +920 |
| 2006-07 | 9 | 22.2% | 0.03 | −3,195 |
| 2006-08 | 14 | 57.1% | 1.90 | +1,864 |
| 2006-09 | 14 | 42.9% | 2.47 | +2,551 |
| 2006-10 | 21 | 47.6% | 2.21 | +3,319 |
| 2006-11 | 35 | 51.4% | 2.66 | +8,796 |
| 2006-12 | 21 | 71.4% | 6.23 | +7,520 |
| 2007-01 | 7 | 71.4% | 22.65 | +3,805 |
| 2007-02 | 46 | 45.7% | 2.31 | +8,163 |
| 2007-03 | 9 | 77.8% | 13.09 | +7,932 |
| 2007-04 | 23 | 47.8% | 2.10 | +2,805 |
| 2007-05 | 53 | 54.7% | 2.20 | +11,088 |
| 2007-06 | 24 | 50.0% | 4.30 | +10,922 |
| 2007-07 | 17 | 41.2% | 0.45 | −1,641 |
| 2007-08 | 1 | 100.0% | inf | +121 |
| 2007-09 | 4 | 50.0% | 0.31 | −707 |
| 2007-10 | 19 | 52.6% | 7.31 | +30,867 |
| 2007-11 | 2 | 0.0% | 0.00 | −653 |
| 2007-12 | 4 | 25.0% | 2.86 | +1,114 |
| 2008-02 | 4 | 50.0% | 0.32 | −470 |
| 2008-03 | 5 | 80.0% | 9.49 | +2,144 |
| 2008-04 | 3 | 66.7% | 3.26 | +2,247 |
| 2008-05 | 13 | 76.9% | 12.71 | +5,308 |
| 2008-06 | 12 | 91.7% | 559.61 | +13,447 |
| 2008-07 | 2 | 0.0% | 0.00 | −424 |
| 2008-08 | 11 | 45.5% | 0.37 | −3,230 |
| 2008-09 | 3 | 33.3% | 1.01 | +13 |
| 2008-11 | 1 | 100.0% | inf | +20 |
| 2008-12 | 1 | 100.0% | inf | +843 |
| 2009-01 | 3 | 33.3% | 1.31 | +55 |
| 2009-04 | 1 | 0.0% | 0.00 | −103 |
| 2009-05 | 3 | 100.0% | inf | +4,559 |
| 2009-06 | 5 | 40.0% | 0.48 | −889 |
| 2009-07 | 2 | 100.0% | inf | +1,763 |
| 2009-08 | 11 | 36.4% | 0.66 | −872 |
| 2009-09 | 10 | 50.0% | 9.55 | +18,674 |
| 2009-10 | 16 | 68.8% | 2.30 | +2,950 |
| 2009-11 | 5 | 80.0% | 35.42 | +3,121 |
| 2009-12 | 15 | 53.3% | 1.37 | +885 |
| 2010-01 | 29 | 48.3% | 2.15 | +6,106 |
| 2010-02 | 6 | 66.7% | 4.87 | +919 |
| 2010-03 | 18 | 55.6% | 3.42 | +6,496 |
| 2010-04 | 21 | 52.4% | 5.96 | +16,894 |
| 2010-05 | 22 | 36.4% | 0.88 | −899 |
| 2010-07 | 1 | 100.0% | inf | +186 |
| 2010-08 | 10 | 50.0% | 1.30 | +571 |
| 2010-09 | 9 | 88.9% | 667.47 | +15,266 |
| 2010-10 | 18 | 66.7% | 8.42 | +9,898 |
| 2010-11 | 48 | 52.1% | 1.56 | +6,117 |
| 2010-12 | 24 | 70.8% | 10.23 | +20,022 |
| 2011-01 | 26 | 53.8% | 1.69 | +4,390 |
| 2011-02 | 43 | 62.8% | 5.17 | +23,162 |
| 2011-03 | 18 | 61.1% | 7.01 | +5,649 |
| 2011-04 | 9 | 33.3% | 0.32 | −1,358 |
| 2011-05 | 30 | 36.7% | 1.49 | +3,573 |
| 2011-06 | 2 | 100.0% | inf | +3,536 |
| 2011-07 | 7 | 71.4% | 14.64 | +2,396 |
| 2011-08 | 2 | 100.0% | inf | +784 |
| 2011-09 | 2 | 0.0% | 0.00 | −706 |
| 2011-10 | 3 | 66.7% | 7.83 | +1,654 |
| 2011-11 | 9 | 55.6% | 1.01 | +16 |
| 2011-12 | 3 | 100.0% | inf | +1,825 |
| 2012-01 | 4 | 75.0% | 4.29 | +1,934 |
| 2012-02 | 15 | 66.7% | 2.33 | +1,967 |
| 2012-03 | 17 | 64.7% | 4.93 | +7,592 |
| 2012-04 | 8 | 50.0% | 9.59 | +5,787 |
| 2012-05 | 12 | 50.0% | 0.26 | −2,077 |
| 2012-06 | 6 | 16.7% | 0.14 | −1,375 |
| 2012-07 | 10 | 50.0% | 35.53 | +19,576 |
| 2012-08 | 15 | 66.7% | 1.80 | +1,394 |
| 2012-09 | 16 | 56.2% | 7.30 | +9,446 |
| 2012-10 | 5 | 40.0% | 3.54 | +1,830 |
| 2012-11 | 2 | 0.0% | 0.00 | −150 |
| 2012-12 | 5 | 40.0% | 0.55 | −407 |
| 2013-01 | 15 | 46.7% | 0.82 | −786 |
| 2013-02 | 32 | 62.5% | 2.03 | +7,925 |
| 2013-03 | 23 | 82.6% | 3.64 | +8,214 |
| 2013-04 | 18 | 66.7% | 3.33 | +7,121 |
| 2013-05 | 27 | 74.1% | 7.03 | +13,419 |
| 2013-06 | 12 | 66.7% | 12.06 | +11,985 |
| 2013-07 | 11 | 63.6% | 4.72 | +3,498 |
| 2013-08 | 41 | 46.3% | 1.91 | +6,643 |
| 2013-09 | 13 | 61.5% | 6.44 | +9,207 |
| 2013-10 | 21 | 38.1% | 2.26 | +8,273 |
| 2013-11 | 43 | 51.2% | 2.54 | +10,987 |
| 2013-12 | 18 | 50.0% | 3.60 | +8,427 |
| 2014-01 | 43 | 53.5% | 1.99 | +6,617 |
| 2014-02 | 14 | 35.7% | 0.53 | −1,348 |
| 2014-03 | 42 | 66.7% | 2.93 | +16,590 |
| 2014-04 | 4 | 0.0% | 0.00 | −905 |
| 2014-05 | 7 | 71.4% | 48.08 | +4,949 |
| 2014-06 | 19 | 57.9% | 6.10 | +14,238 |
| 2014-07 | 18 | 61.1% | 2.87 | +4,765 |
| 2014-08 | 2 | 50.0% | 0.28 | −102 |
| 2014-09 | 13 | 30.8% | 0.85 | −733 |
| 2014-10 | 1 | 100.0% | inf | +360 |
| 2014-11 | 18 | 94.4% | 357.48 | +10,822 |
| 2014-12 | 12 | 50.0% | 1.62 | +1,669 |
| 2015-01 | 3 | 33.3% | 9.64 | +3,156 |
| 2015-02 | 23 | 56.5% | 2.92 | +6,411 |
| 2015-03 | 30 | 60.0% | 6.64 | +21,861 |
| 2015-04 | 22 | 63.6% | 1.96 | +3,282 |
| 2015-05 | 15 | 40.0% | 3.78 | +4,414 |
| 2015-06 | 12 | 58.3% | 5.83 | +6,370 |
| 2015-07 | 8 | 62.5% | 1.93 | +2,056 |
| 2015-08 | 1 | 0.0% | 0.00 | −721 |
| 2015-09 | 1 | 0.0% | inf | +0 |
| 2015-10 | 4 | 50.0% | 12.26 | +2,093 |
| 2015-11 | 21 | 57.1% | 3.12 | +8,171 |
| 2015-12 | 5 | 80.0% | 30.37 | +4,592 |
| 2016-01 | 2 | 50.0% | 0.61 | −348 |
| 2016-02 | 1 | 0.0% | 0.00 | −8 |
| 2016-03 | 8 | 25.0% | 0.56 | −879 |
| 2016-04 | 6 | 50.0% | 2.08 | +749 |
| 2016-05 | 10 | 50.0% | 3.02 | +2,853 |
| 2016-06 | 7 | 42.9% | 1.37 | +367 |
| 2016-07 | 2 | 50.0% | 1.28 | +48 |
| 2016-08 | 50 | 60.0% | 4.11 | +11,955 |
| 2016-09 | 15 | 66.7% | 7.09 | +5,866 |
| 2016-10 | 15 | 66.7% | 3.01 | +4,099 |
| 2016-11 | 5 | 80.0% | 4.29 | +3,467 |
| 2016-12 | 29 | 62.1% | 3.75 | +5,631 |
| 2017-01 | 17 | 52.9% | 2.25 | +2,427 |
| 2017-02 | 29 | 58.6% | 2.53 | +6,098 |
| 2017-03 | 36 | 63.9% | 2.00 | +7,581 |
| 2017-04 | 10 | 20.0% | 1.27 | +819 |
| 2017-05 | 29 | 75.9% | 5.43 | +18,505 |
| 2017-06 | 18 | 72.2% | 5.39 | +9,246 |
| 2017-07 | 12 | 50.0% | 1.20 | +419 |
| 2017-08 | 16 | 43.8% | 1.37 | +1,183 |
| 2017-09 | 17 | 41.2% | 0.39 | −3,190 |
| 2017-10 | 32 | 59.4% | 4.02 | +11,613 |
| 2017-11 | 10 | 60.0% | 0.61 | −1,193 |
| 2017-12 | 19 | 26.3% | 0.47 | −3,644 |
| 2018-01 | 16 | 68.8% | 3.84 | +6,628 |
| 2018-02 | 14 | 35.7% | 0.85 | −612 |
| 2018-03 | 21 | 47.6% | 6.15 | +30,361 |
| 2018-04 | 4 | 50.0% | 1.98 | +262 |
| 2018-05 | 21 | 52.4% | 1.20 | +1,052 |
| 2018-06 | 41 | 56.1% | 3.97 | +15,797 |
| 2018-07 | 24 | 50.0% | 5.45 | +4,614 |
| 2018-08 | 28 | 60.7% | 4.37 | +10,673 |
| 2018-09 | 23 | 87.0% | 11.22 | +13,772 |
| 2018-10 | 3 | 33.3% | 0.01 | −435 |
| 2018-11 | 10 | 70.0% | 3.35 | +3,197 |
| 2018-12 | 5 | 20.0% | 0.34 | −1,443 |
| 2019-01 | 1 | 0.0% | inf | +0 |
| 2019-02 | 16 | 81.2% | 4.17 | +7,225 |
| 2019-03 | 22 | 54.5% | 4.81 | +20,333 |
| 2019-04 | 6 | 50.0% | 2.90 | +1,678 |
| 2019-05 | 14 | 64.3% | 6.50 | +8,247 |
| 2019-06 | 2 | 50.0% | 4.70 | +1,238 |
| 2019-07 | 21 | 47.6% | 4.15 | +10,526 |
| 2019-08 | 15 | 26.7% | 0.33 | −2,363 |
| 2019-09 | 8 | 25.0% | 0.03 | −4,077 |
| 2019-10 | 4 | 25.0% | 0.19 | −194 |
| 2019-11 | 21 | 61.9% | 1.50 | +1,944 |
| 2019-12 | 26 | 76.9% | 5.37 | +15,479 |
| 2020-01 | 20 | 60.0% | 2.86 | +4,813 |
| 2020-02 | 22 | 22.7% | 0.43 | −7,716 |
| 2020-03 | 3 | 33.3% | 0.63 | −410 |
| 2020-04 | 3 | 0.0% | 0.00 | −850 |
| 2020-05 | 13 | 76.9% | 4.56 | +12,713 |
| 2020-06 | 14 | 50.0% | 0.92 | −375 |
| 2020-07 | 7 | 57.1% | 7.70 | +4,857 |
| 2020-08 | 24 | 37.5% | 2.14 | +9,164 |
| 2020-09 | 8 | 62.5% | 5.79 | +7,839 |
| 2020-10 | 14 | 50.0% | 0.89 | −354 |
| 2020-11 | 12 | 33.3% | 0.60 | −1,805 |
| 2020-12 | 60 | 53.3% | 2.28 | +27,466 |
| 2021-01 | 57 | 66.7% | 26.77 | +348,222 |
| 2021-02 | 80 | 41.2% | 2.10 | +60,067 |
| 2021-03 | 28 | 50.0% | 1.96 | +9,979 |
| 2021-04 | 14 | 35.7% | 0.41 | −2,652 |
| 2021-05 | 11 | 63.6% | 3.26 | +2,531 |
| 2021-06 | 30 | 36.7% | 0.86 | −1,291 |
| 2021-07 | 9 | 77.8% | 8.54 | +5,727 |
| 2021-08 | 14 | 57.1% | 0.96 | −89 |
| 2021-09 | 16 | 50.0% | 7.16 | +16,512 |
| 2021-10 | 3 | 0.0% | 0.00 | −1,446 |
| 2021-11 | 34 | 41.2% | 1.26 | +3,358 |
| 2021-12 | 5 | 80.0% | 7.66 | +5,187 |
| 2022-01 | 3 | 66.7% | 2.55 | +802 |
| 2022-02 | 2 | 50.0% | 0.89 | −231 |
| 2022-03 | 3 | 33.3% | 0.54 | −478 |
| 2022-04 | 7 | 71.4% | 14.30 | +5,527 |
| 2022-06 | 3 | 33.3% | 0.73 | −397 |
| 2022-07 | 1 | 100.0% | inf | +49 |
| 2022-08 | 14 | 35.7% | 1.13 | +904 |
| 2022-09 | 3 | 33.3% | 1.77 | +749 |
| 2022-11 | 4 | 25.0% | 0.04 | −1,254 |
| 2022-12 | 9 | 66.7% | 7.20 | +6,858 |
| 2023-01 | 4 | 25.0% | 0.21 | −1,622 |
| 2023-02 | 6 | 50.0% | 1.25 | +374 |
| 2023-03 | 2 | 100.0% | inf | +1,761 |
| 2023-04 | 4 | 50.0% | 1.32 | +233 |
| 2023-05 | 4 | 25.0% | 1.18 | +133 |
| 2023-06 | 14 | 57.1% | 3.86 | +8,844 |
| 2023-07 | 10 | 70.0% | 8.62 | +6,458 |
| 2023-08 | 14 | 42.9% | 0.56 | −1,839 |
| 2023-09 | 4 | 75.0% | 0.37 | −796 |
| 2023-11 | 6 | 50.0% | 0.54 | −1,170 |
| 2023-12 | 16 | 75.0% | 1.84 | +4,583 |
| 2024-01 | 16 | 68.8% | 7.12 | +11,719 |
| 2024-02 | 17 | 35.3% | 0.89 | −582 |
| 2024-03 | 42 | 52.4% | 2.80 | +15,547 |
| 2024-04 | 11 | 90.9% | 40.01 | +10,264 |
| 2024-05 | 9 | 44.4% | 1.02 | +30 |
| 2024-06 | 2 | 100.0% | inf | +2,836 |
| 2024-07 | 1 | 100.0% | inf | +49 |
| 2024-08 | 8 | 37.5% | 1.79 | +1,546 |
| 2024-09 | 9 | 55.6% | 3.44 | +4,824 |
| 2024-10 | 16 | 37.5% | 1.21 | +1,103 |
| 2024-11 | 38 | 39.5% | 1.08 | +703 |
| 2024-12 | 11 | 72.7% | 1.17 | +638 |
| 2025-01 | 3 | 66.7% | 2.16 | +1,224 |
| 2025-02 | 23 | 56.5% | 1.05 | +201 |
| 2025-03 | 1 | 0.0% | 0.00 | −672 |
| 2025-05 | 12 | 66.7% | 1.53 | +2,361 |
| 2025-06 | 20 | 50.0% | 2.63 | +8,815 |
| 2025-07 | 18 | 61.1% | 1.61 | +4,383 |
| 2025-08 | 9 | 22.2% | 1.11 | +335 |
| 2025-09 | 32 | 43.8% | 1.40 | +5,257 |
| 2025-10 | 12 | 58.3% | 6.26 | +13,189 |
| 2025-11 | 4 | 0.0% | 0.00 | −1,606 |
| 2025-12 | 18 | 50.0% | 1.17 | +1,072 |
| 2026-01 | 15 | 73.3% | 13.26 | +10,892 |
| 2026-02 | 16 | 43.8% | 1.40 | +1,476 |
| 2026-03 | 9 | 66.7% | 13.93 | +24,230 |
| 2026-04 | 5 | 60.0% | 1.99 | +851 |
| 2026-05 | 21 | 52.4% | 1.08 | +1,156 |

</details>

#### Window = 3-day-low stop

half-Kelly 0.140/0.199/0.209 · total **+$1,288,366** Kelly (flat +$1,204,814) · months **190/257 (74%)** · years **22/22** · up:dn **6.5:1** · max DD **-4,679** · worst streak **3mo**

Yearly (Kelly-sized, by exit date):
| year | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005 | 196 | 50.0% | 1.68 | +22,593 |
| 2006 | 262 | 53.1% | 2.62 | +51,437 |
| 2007 | 207 | 50.7% | 2.08 | +33,769 |
| 2008 | 55 | 67.3% | 2.49 | +13,699 |
| 2009 | 71 | 56.3% | 2.61 | +14,872 |
| 2010 | 206 | 58.7% | 3.56 | +77,410 |
| 2011 | 154 | 57.1% | 2.67 | +41,899 |
| 2012 | 116 | 54.3% | 3.96 | +42,395 |
| 2013 | 277 | 58.5% | 2.80 | +85,016 |
| 2014 | 189 | 58.7% | 2.60 | +48,166 |
| 2015 | 145 | 58.6% | 3.59 | +49,751 |
| 2016 | 151 | 58.3% | 3.14 | +34,104 |
| 2017 | 246 | 59.8% | 2.36 | +54,699 |
| 2018 | 208 | 57.2% | 3.73 | +79,460 |
| 2019 | 157 | 57.3% | 3.08 | +53,474 |
| 2020 | 205 | 51.7% | 2.12 | +67,382 |
| 2021 | 295 | 50.2% | 4.94 | +388,119 |
| 2022 | 49 | 51.0% | 1.96 | +13,997 |
| 2023 | 88 | 59.1% | 2.06 | +22,216 |
| 2024 | 176 | 50.6% | 2.01 | +36,981 |
| 2025 | 152 | 48.0% | 1.55 | +25,775 |
| 2026 | 66 | 57.6% | 2.38 | +31,151 |

<details>
<summary>Full monthly table</summary>

| month | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005-01 | 1 | 100.0% | inf | +736 |
| 2005-02 | 23 | 47.8% | 1.81 | +3,954 |
| 2005-03 | 11 | 54.5% | 5.10 | +4,136 |
| 2005-04 | 1 | 0.0% | 0.00 | −1,903 |
| 2005-05 | 8 | 50.0% | 4.21 | +1,221 |
| 2005-06 | 26 | 42.3% | 0.68 | −2,092 |
| 2005-07 | 31 | 54.8% | 1.41 | +2,421 |
| 2005-08 | 43 | 51.2% | 3.14 | +11,410 |
| 2005-09 | 16 | 43.8% | 0.55 | −909 |
| 2005-10 | 4 | 25.0% | 1.07 | +26 |
| 2005-11 | 16 | 68.8% | 1.86 | +1,789 |
| 2005-12 | 16 | 43.8% | 1.60 | +1,805 |
| 2006-01 | 21 | 52.4% | 1.96 | +3,539 |
| 2006-02 | 34 | 58.8% | 4.12 | +11,238 |
| 2006-03 | 29 | 58.6% | 3.68 | +6,214 |
| 2006-04 | 19 | 52.6% | 3.69 | +6,809 |
| 2006-05 | 43 | 48.8% | 1.92 | +5,431 |
| 2006-07 | 10 | 40.0% | 0.10 | −1,820 |
| 2006-08 | 14 | 57.1% | 3.29 | +3,552 |
| 2006-09 | 14 | 35.7% | 0.79 | −388 |
| 2006-10 | 23 | 56.5% | 3.73 | +5,966 |
| 2006-11 | 39 | 48.7% | 2.51 | +7,200 |
| 2006-12 | 16 | 68.8% | 3.95 | +3,696 |
| 2007-01 | 6 | 66.7% | 5.25 | +647 |
| 2007-02 | 49 | 46.9% | 2.28 | +9,475 |
| 2007-03 | 5 | 100.0% | inf | +5,410 |
| 2007-04 | 28 | 53.6% | 1.75 | +2,365 |
| 2007-05 | 50 | 54.0% | 2.63 | +13,099 |
| 2007-06 | 23 | 43.5% | 2.70 | +5,161 |
| 2007-07 | 16 | 43.8% | 0.49 | −1,447 |
| 2007-08 | 1 | 100.0% | inf | +120 |
| 2007-09 | 5 | 60.0% | 1.33 | +220 |
| 2007-10 | 18 | 44.4% | 0.54 | −2,365 |
| 2007-11 | 2 | 50.0% | 0.96 | −12 |
| 2007-12 | 4 | 25.0% | 2.82 | +1,095 |
| 2008-02 | 4 | 50.0% | 0.32 | −479 |
| 2008-03 | 5 | 80.0% | 13.69 | +3,176 |
| 2008-04 | 3 | 66.7% | 3.19 | +2,223 |
| 2008-05 | 18 | 77.8% | 8.47 | +8,086 |
| 2008-06 | 7 | 100.0% | inf | +3,341 |
| 2008-07 | 2 | 0.0% | 0.00 | −421 |
| 2008-08 | 11 | 45.5% | 0.28 | −3,704 |
| 2008-09 | 3 | 33.3% | 1.60 | +368 |
| 2008-11 | 2 | 100.0% | inf | +1,109 |
| 2009-01 | 3 | 66.7% | 1.87 | +152 |
| 2009-04 | 1 | 0.0% | 0.00 | −85 |
| 2009-05 | 3 | 100.0% | inf | +4,487 |
| 2009-06 | 6 | 66.7% | 1.32 | +358 |
| 2009-07 | 2 | 100.0% | inf | +766 |
| 2009-08 | 11 | 36.4% | 0.65 | −880 |
| 2009-09 | 9 | 55.6% | 2.55 | +2,190 |
| 2009-10 | 17 | 52.9% | 2.89 | +3,365 |
| 2009-11 | 5 | 80.0% | 17.34 | +3,009 |
| 2009-12 | 14 | 50.0% | 1.78 | +1,510 |
| 2010-01 | 30 | 56.7% | 2.71 | +8,187 |
| 2010-02 | 8 | 87.5% | 27.32 | +2,875 |
| 2010-03 | 17 | 64.7% | 4.72 | +7,307 |
| 2010-04 | 23 | 47.8% | 2.68 | +6,628 |
| 2010-05 | 18 | 33.3% | 0.98 | −124 |
| 2010-07 | 3 | 100.0% | inf | +720 |
| 2010-08 | 8 | 37.5% | 1.07 | +135 |
| 2010-09 | 10 | 90.0% | 725.20 | +16,448 |
| 2010-10 | 22 | 63.6% | 8.80 | +10,494 |
| 2010-11 | 45 | 55.6% | 1.74 | +6,526 |
| 2010-12 | 22 | 68.2% | 10.28 | +18,215 |
| 2011-01 | 28 | 53.6% | 2.36 | +8,643 |
| 2011-02 | 44 | 61.4% | 3.95 | +17,777 |
| 2011-03 | 15 | 80.0% | 7.59 | +3,657 |
| 2011-04 | 11 | 36.4% | 0.73 | −683 |
| 2011-05 | 29 | 41.4% | 1.77 | +4,781 |
| 2011-06 | 1 | 100.0% | inf | +673 |
| 2011-07 | 7 | 71.4% | 12.95 | +2,084 |
| 2011-08 | 2 | 100.0% | inf | +1,789 |
| 2011-09 | 3 | 33.3% | 2.23 | +870 |
| 2011-10 | 2 | 50.0% | 2.56 | +374 |
| 2011-11 | 9 | 55.6% | 1.29 | +637 |
| 2011-12 | 3 | 100.0% | inf | +1,296 |
| 2012-01 | 4 | 75.0% | 4.16 | +1,894 |
| 2012-02 | 18 | 72.2% | 3.60 | +3,790 |
| 2012-03 | 15 | 60.0% | 4.99 | +6,824 |
| 2012-04 | 7 | 42.9% | 10.00 | +5,707 |
| 2012-05 | 12 | 58.3% | 0.37 | −1,486 |
| 2012-06 | 7 | 14.3% | 0.13 | −1,521 |
| 2012-07 | 10 | 60.0% | 26.95 | +17,179 |
| 2012-08 | 16 | 68.8% | 2.28 | +1,970 |
| 2012-09 | 16 | 37.5% | 5.78 | +8,298 |
| 2012-10 | 4 | 25.0% | 1.29 | +243 |
| 2012-11 | 1 | 100.0% | inf | +72 |
| 2012-12 | 6 | 33.3% | 0.46 | −576 |
| 2013-01 | 16 | 56.2% | 1.09 | +353 |
| 2013-02 | 30 | 60.0% | 2.23 | +8,594 |
| 2013-03 | 26 | 80.8% | 3.89 | +9,550 |
| 2013-04 | 15 | 66.7% | 2.53 | +4,226 |
| 2013-05 | 30 | 76.7% | 8.57 | +17,804 |
| 2013-06 | 9 | 55.6% | 4.61 | +5,254 |
| 2013-07 | 14 | 57.1% | 3.44 | +3,367 |
| 2013-08 | 39 | 51.3% | 2.76 | +11,010 |
| 2013-09 | 13 | 53.8% | 2.88 | +3,851 |
| 2013-10 | 22 | 45.5% | 1.49 | +3,159 |
| 2013-11 | 43 | 51.2% | 2.71 | +12,445 |
| 2013-12 | 20 | 45.0% | 2.86 | +5,403 |
| 2014-01 | 40 | 55.0% | 1.89 | +5,236 |
| 2014-02 | 17 | 35.3% | 0.36 | −3,106 |
| 2014-03 | 39 | 66.7% | 2.53 | +9,669 |
| 2014-04 | 3 | 0.0% | 0.00 | −741 |
| 2014-05 | 7 | 71.4% | 52.13 | +5,330 |
| 2014-06 | 20 | 65.0% | 7.03 | +16,236 |
| 2014-07 | 17 | 58.8% | 2.59 | +3,748 |
| 2014-08 | 2 | 50.0% | 0.28 | −105 |
| 2014-09 | 13 | 30.8% | 0.93 | −318 |
| 2014-10 | 2 | 50.0% | 69.28 | +352 |
| 2014-11 | 18 | 94.4% | 338.73 | +10,166 |
| 2014-12 | 11 | 54.5% | 1.67 | +1,700 |
| 2015-01 | 3 | 33.3% | 7.07 | +1,545 |
| 2015-02 | 26 | 65.4% | 3.34 | +7,174 |
| 2015-03 | 28 | 53.6% | 4.10 | +12,066 |
| 2015-04 | 26 | 65.4% | 2.36 | +4,732 |
| 2015-05 | 12 | 58.3% | 11.34 | +7,010 |
| 2015-06 | 11 | 54.5% | 4.92 | +4,574 |
| 2015-07 | 7 | 57.1% | 1.00 | +8 |
| 2015-08 | 1 | 0.0% | 0.00 | −715 |
| 2015-09 | 2 | 50.0% | inf | +426 |
| 2015-10 | 3 | 33.3% | 1.51 | +223 |
| 2015-11 | 22 | 59.1% | 3.62 | +8,739 |
| 2015-12 | 4 | 75.0% | 26.60 | +3,969 |
| 2016-01 | 2 | 50.0% | 0.61 | −355 |
| 2016-02 | 1 | 100.0% | inf | +15 |
| 2016-03 | 8 | 25.0% | 0.67 | −647 |
| 2016-04 | 6 | 50.0% | 2.91 | +1,099 |
| 2016-05 | 10 | 50.0% | 2.99 | +2,784 |
| 2016-06 | 7 | 42.9% | 1.28 | +277 |
| 2016-07 | 3 | 66.7% | 6.32 | +560 |
| 2016-08 | 51 | 56.9% | 3.90 | +11,444 |
| 2016-09 | 14 | 57.1% | 4.00 | +4,028 |
| 2016-10 | 14 | 64.3% | 3.32 | +4,252 |
| 2016-11 | 8 | 87.5% | 6.08 | +5,236 |
| 2016-12 | 27 | 66.7% | 3.94 | +5,411 |
| 2017-01 | 17 | 64.7% | 2.98 | +3,288 |
| 2017-02 | 32 | 62.5% | 2.79 | +7,363 |
| 2017-03 | 33 | 69.7% | 2.26 | +8,253 |
| 2017-04 | 12 | 33.3% | 0.24 | −2,409 |
| 2017-05 | 27 | 77.8% | 7.92 | +16,555 |
| 2017-06 | 19 | 68.4% | 6.37 | +10,998 |
| 2017-07 | 11 | 63.6% | 2.48 | +1,712 |
| 2017-08 | 15 | 46.7% | 1.51 | +1,480 |
| 2017-09 | 18 | 50.0% | 0.85 | −528 |
| 2017-10 | 32 | 56.2% | 3.50 | +9,096 |
| 2017-11 | 9 | 66.7% | 0.66 | −700 |
| 2017-12 | 21 | 38.1% | 0.94 | −408 |
| 2018-01 | 14 | 71.4% | 3.21 | +4,048 |
| 2018-02 | 14 | 35.7% | 0.99 | −20 |
| 2018-03 | 21 | 52.4% | 6.85 | +31,631 |
| 2018-04 | 5 | 60.0% | 2.06 | +282 |
| 2018-05 | 26 | 53.8% | 2.14 | +5,041 |
| 2018-06 | 36 | 52.8% | 3.08 | +8,967 |
| 2018-07 | 23 | 56.5% | 5.61 | +4,432 |
| 2018-08 | 34 | 58.8% | 5.27 | +14,574 |
| 2018-09 | 19 | 84.2% | 7.05 | +8,982 |
| 2018-10 | 1 | 100.0% | inf | +3 |
| 2018-11 | 10 | 60.0% | 2.63 | +2,607 |
| 2018-12 | 5 | 20.0% | 0.40 | −1,088 |
| 2019-01 | 1 | 0.0% | inf | +0 |
| 2019-02 | 20 | 80.0% | 5.77 | +11,011 |
| 2019-03 | 18 | 50.0% | 4.61 | +16,888 |
| 2019-04 | 6 | 50.0% | 2.93 | +1,694 |
| 2019-05 | 15 | 66.7% | 3.69 | +3,621 |
| 2019-06 | 2 | 50.0% | 0.76 | −29 |
| 2019-07 | 21 | 42.9% | 3.97 | +9,574 |
| 2019-08 | 14 | 42.9% | 0.73 | −680 |
| 2019-09 | 8 | 25.0% | 0.04 | −3,079 |
| 2019-10 | 5 | 40.0% | 0.59 | −82 |
| 2019-11 | 21 | 66.7% | 1.33 | +1,232 |
| 2019-12 | 26 | 69.2% | 4.82 | +13,325 |
| 2020-01 | 20 | 60.0% | 3.62 | +5,658 |
| 2020-02 | 22 | 22.7% | 0.59 | −4,679 |
| 2020-03 | 2 | 100.0% | inf | +1,217 |
| 2020-04 | 3 | 0.0% | 0.00 | −839 |
| 2020-05 | 13 | 76.9% | 4.42 | +12,284 |
| 2020-06 | 15 | 60.0% | 1.16 | +712 |
| 2020-07 | 8 | 50.0% | 6.38 | +4,757 |
| 2020-08 | 23 | 39.1% | 2.36 | +10,162 |
| 2020-09 | 7 | 57.1% | 2.44 | +1,533 |
| 2020-10 | 14 | 50.0% | 0.97 | −84 |
| 2020-11 | 13 | 46.2% | 0.90 | −421 |
| 2020-12 | 65 | 58.5% | 2.78 | +37,081 |
| 2021-01 | 58 | 62.1% | 23.28 | +327,158 |
| 2021-02 | 76 | 40.8% | 1.10 | +5,260 |
| 2021-03 | 26 | 53.8% | 2.75 | +14,145 |
| 2021-04 | 14 | 35.7% | 0.41 | −2,571 |
| 2021-05 | 11 | 63.6% | 5.45 | +2,985 |
| 2021-06 | 32 | 40.6% | 1.40 | +3,225 |
| 2021-07 | 7 | 71.4% | 3.62 | +1,974 |
| 2021-08 | 14 | 50.0% | 0.86 | −289 |
| 2021-09 | 16 | 62.5% | 11.86 | +18,536 |
| 2021-10 | 4 | 25.0% | 1.34 | +466 |
| 2021-11 | 36 | 50.0% | 3.23 | +13,714 |
| 2021-12 | 1 | 100.0% | inf | +3,515 |
| 2022-01 | 3 | 66.7% | 2.57 | +831 |
| 2022-02 | 2 | 50.0% | 0.89 | −230 |
| 2022-03 | 4 | 25.0% | 0.52 | −513 |
| 2022-04 | 6 | 83.3% | 39.86 | +6,307 |
| 2022-06 | 3 | 33.3% | 0.73 | −393 |
| 2022-07 | 1 | 100.0% | inf | +48 |
| 2022-08 | 15 | 46.7% | 1.26 | +1,671 |
| 2022-09 | 2 | 0.0% | 0.00 | −968 |
| 2022-11 | 4 | 25.0% | 0.15 | −825 |
| 2022-12 | 9 | 66.7% | 9.77 | +8,069 |
| 2023-01 | 4 | 25.0% | 0.22 | −1,486 |
| 2023-02 | 7 | 57.1% | 2.54 | +2,053 |
| 2023-03 | 1 | 100.0% | inf | +950 |
| 2023-04 | 4 | 50.0% | 1.37 | +271 |
| 2023-05 | 5 | 40.0% | 1.29 | +219 |
| 2023-06 | 14 | 64.3% | 6.81 | +12,588 |
| 2023-07 | 10 | 60.0% | 6.79 | +3,701 |
| 2023-08 | 13 | 38.5% | 0.33 | −2,760 |
| 2023-09 | 4 | 75.0% | 0.44 | −691 |
| 2023-11 | 8 | 50.0% | 0.73 | −987 |
| 2023-12 | 18 | 83.3% | 2.91 | +8,358 |
| 2024-01 | 12 | 66.7% | 7.31 | +9,305 |
| 2024-02 | 21 | 47.6% | 1.80 | +4,319 |
| 2024-03 | 40 | 52.5% | 1.87 | +6,988 |
| 2024-04 | 9 | 88.9% | 30.25 | +7,630 |
| 2024-05 | 10 | 50.0% | 1.75 | +1,382 |
| 2024-06 | 1 | 100.0% | inf | +1,267 |
| 2024-07 | 1 | 100.0% | inf | +48 |
| 2024-08 | 9 | 44.4% | 1.82 | +1,614 |
| 2024-09 | 10 | 40.0% | 1.93 | +3,248 |
| 2024-10 | 15 | 33.3% | 1.41 | +1,613 |
| 2024-11 | 37 | 37.8% | 0.57 | −3,135 |
| 2024-12 | 11 | 72.7% | 1.85 | +2,704 |
| 2025-01 | 5 | 40.0% | 1.50 | +749 |
| 2025-02 | 22 | 54.5% | 1.32 | +1,118 |
| 2025-05 | 13 | 69.2% | 2.22 | +4,745 |
| 2025-06 | 20 | 50.0% | 1.84 | +4,422 |
| 2025-07 | 18 | 50.0% | 1.10 | +776 |
| 2025-08 | 9 | 33.3% | 2.09 | +1,768 |
| 2025-09 | 33 | 42.4% | 1.60 | +8,108 |
| 2025-10 | 10 | 50.0% | 3.44 | +6,060 |
| 2025-11 | 4 | 0.0% | 0.00 | −1,466 |
| 2025-12 | 18 | 50.0% | 0.92 | −505 |
| 2026-01 | 16 | 75.0% | 16.87 | +11,104 |
| 2026-02 | 18 | 50.0% | 6.00 | +18,209 |
| 2026-03 | 6 | 50.0% | 0.89 | −206 |
| 2026-04 | 5 | 60.0% | 1.98 | +850 |
| 2026-05 | 21 | 52.4% | 1.08 | +1,194 |

</details>

#### Window = 2-day-low stop

half-Kelly 0.149/0.215/0.208 · total **+$1,245,589** Kelly (flat +$1,164,409) · months **187/257 (73%)** · years **22/22** · up:dn **7.4:1** · max DD **-3,826** · worst streak **3mo**

Yearly (Kelly-sized, by exit date):
| year | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005 | 197 | 51.8% | 1.92 | +26,520 |
| 2006 | 262 | 55.0% | 2.69 | +48,347 |
| 2007 | 208 | 56.2% | 2.45 | +38,166 |
| 2008 | 55 | 67.3% | 2.61 | +12,090 |
| 2009 | 72 | 56.9% | 2.68 | +14,721 |
| 2010 | 210 | 61.0% | 3.60 | +65,778 |
| 2011 | 149 | 51.7% | 2.59 | +35,317 |
| 2012 | 118 | 57.6% | 3.15 | +28,433 |
| 2013 | 278 | 59.7% | 2.72 | +71,948 |
| 2014 | 187 | 61.0% | 2.97 | +50,739 |
| 2015 | 144 | 59.7% | 3.40 | +41,917 |
| 2016 | 151 | 58.3% | 3.27 | +34,496 |
| 2017 | 248 | 61.7% | 2.53 | +56,972 |
| 2018 | 206 | 59.7% | 4.26 | +80,425 |
| 2019 | 162 | 59.3% | 2.93 | +47,362 |
| 2020 | 202 | 55.0% | 2.66 | +81,289 |
| 2021 | 292 | 52.7% | 5.12 | +364,813 |
| 2022 | 50 | 52.0% | 2.53 | +18,045 |
| 2023 | 91 | 59.3% | 2.12 | +23,171 |
| 2024 | 173 | 49.7% | 1.88 | +31,256 |
| 2025 | 151 | 51.0% | 1.77 | +34,004 |
| 2026 | 65 | 60.0% | 4.26 | +39,781 |

<details>
<summary>Full monthly table</summary>

| month | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005-01 | 1 | 100.0% | inf | +737 |
| 2005-02 | 26 | 46.2% | 1.84 | +4,149 |
| 2005-03 | 9 | 66.7% | 11.64 | +6,090 |
| 2005-05 | 9 | 55.6% | 3.82 | +1,077 |
| 2005-06 | 29 | 41.4% | 0.70 | −1,867 |
| 2005-07 | 30 | 63.3% | 1.62 | +3,304 |
| 2005-08 | 40 | 52.5% | 3.35 | +9,780 |
| 2005-09 | 16 | 43.8% | 1.06 | +115 |
| 2005-10 | 4 | 25.0% | 0.29 | −251 |
| 2005-11 | 16 | 62.5% | 1.46 | +1,067 |
| 2005-12 | 17 | 47.1% | 1.85 | +2,320 |
| 2006-01 | 25 | 60.0% | 1.85 | +3,438 |
| 2006-02 | 30 | 60.0% | 3.76 | +8,567 |
| 2006-03 | 29 | 65.5% | 4.74 | +7,276 |
| 2006-04 | 18 | 55.6% | 3.37 | +4,850 |
| 2006-05 | 43 | 53.5% | 2.70 | +7,811 |
| 2006-07 | 10 | 40.0% | 0.16 | −1,620 |
| 2006-08 | 14 | 57.1% | 2.71 | +2,768 |
| 2006-09 | 15 | 40.0% | 0.75 | −435 |
| 2006-10 | 27 | 55.6% | 4.66 | +7,145 |
| 2006-11 | 35 | 42.9% | 2.17 | +5,260 |
| 2006-12 | 16 | 68.8% | 3.73 | +3,286 |
| 2007-01 | 7 | 71.4% | 3.92 | +2,481 |
| 2007-02 | 50 | 54.0% | 3.17 | +11,509 |
| 2007-03 | 5 | 80.0% | 7.72 | +3,503 |
| 2007-04 | 33 | 60.6% | 2.50 | +3,784 |
| 2007-05 | 48 | 58.3% | 2.46 | +10,884 |
| 2007-06 | 19 | 47.4% | 3.25 | +4,534 |
| 2007-07 | 16 | 56.2% | 0.79 | −508 |
| 2007-08 | 1 | 100.0% | inf | +120 |
| 2007-09 | 6 | 50.0% | 0.77 | −271 |
| 2007-10 | 17 | 52.9% | 1.36 | +1,178 |
| 2007-11 | 2 | 50.0% | 0.96 | −12 |
| 2007-12 | 4 | 25.0% | 2.58 | +963 |
| 2008-02 | 4 | 50.0% | 0.31 | −489 |
| 2008-03 | 5 | 80.0% | 42.90 | +1,750 |
| 2008-04 | 4 | 75.0% | 1.86 | +888 |
| 2008-05 | 19 | 78.9% | 8.02 | +8,995 |
| 2008-06 | 5 | 100.0% | inf | +2,538 |
| 2008-07 | 3 | 33.3% | 0.12 | −371 |
| 2008-08 | 11 | 36.4% | 0.38 | −2,276 |
| 2008-09 | 2 | 50.0% | 2.78 | +632 |
| 2008-11 | 2 | 100.0% | inf | +423 |
| 2009-01 | 3 | 66.7% | 2.76 | +309 |
| 2009-04 | 1 | 0.0% | 0.00 | −86 |
| 2009-05 | 3 | 100.0% | inf | +4,280 |
| 2009-06 | 6 | 50.0% | 0.44 | −859 |
| 2009-07 | 2 | 100.0% | inf | +735 |
| 2009-08 | 11 | 45.5% | 1.47 | +887 |
| 2009-09 | 11 | 54.5% | 2.06 | +1,751 |
| 2009-10 | 15 | 46.7% | 2.81 | +3,222 |
| 2009-11 | 6 | 83.3% | 23.52 | +3,478 |
| 2009-12 | 14 | 57.1% | 1.68 | +1,005 |
| 2010-01 | 32 | 68.8% | 4.70 | +11,554 |
| 2010-02 | 7 | 71.4% | 8.29 | +1,856 |
| 2010-03 | 17 | 52.9% | 3.32 | +4,763 |
| 2010-04 | 22 | 40.9% | 1.92 | +2,889 |
| 2010-05 | 17 | 41.2% | 0.97 | −116 |
| 2010-07 | 3 | 100.0% | inf | +783 |
| 2010-08 | 10 | 50.0% | 3.07 | +3,570 |
| 2010-09 | 9 | 88.9% | 132.63 | +2,993 |
| 2010-10 | 22 | 63.6% | 10.46 | +9,748 |
| 2010-11 | 46 | 60.9% | 2.20 | +9,950 |
| 2010-12 | 25 | 72.0% | 12.58 | +17,788 |
| 2011-01 | 26 | 42.3% | 1.97 | +5,001 |
| 2011-02 | 46 | 50.0% | 3.18 | +14,695 |
| 2011-03 | 11 | 72.7% | 4.26 | +1,416 |
| 2011-04 | 13 | 53.8% | 1.67 | +1,121 |
| 2011-05 | 27 | 40.7% | 2.10 | +6,241 |
| 2011-07 | 7 | 71.4% | 42.91 | +2,149 |
| 2011-08 | 2 | 100.0% | inf | +1,791 |
| 2011-09 | 4 | 50.0% | 2.65 | +1,152 |
| 2011-10 | 1 | 0.0% | 0.00 | −240 |
| 2011-11 | 9 | 55.6% | 1.46 | +698 |
| 2011-12 | 3 | 100.0% | inf | +1,293 |
| 2012-01 | 5 | 80.0% | 8.48 | +2,605 |
| 2012-02 | 18 | 72.2% | 5.41 | +6,547 |
| 2012-03 | 16 | 68.8% | 2.35 | +2,327 |
| 2012-04 | 5 | 60.0% | 8.53 | +2,628 |
| 2012-05 | 12 | 58.3% | 0.50 | −1,137 |
| 2012-06 | 9 | 22.2% | 2.19 | +1,809 |
| 2012-07 | 8 | 62.5% | 21.32 | +10,540 |
| 2012-08 | 19 | 68.4% | 2.52 | +2,339 |
| 2012-09 | 13 | 30.8% | 1.30 | +467 |
| 2012-10 | 4 | 25.0% | 1.48 | +394 |
| 2012-11 | 1 | 100.0% | inf | +72 |
| 2012-12 | 8 | 50.0% | 0.86 | −159 |
| 2013-01 | 14 | 57.1% | 0.68 | −1,117 |
| 2013-02 | 37 | 70.3% | 2.74 | +11,471 |
| 2013-03 | 23 | 73.9% | 1.96 | +3,716 |
| 2013-04 | 11 | 63.6% | 2.73 | +2,440 |
| 2013-05 | 30 | 70.0% | 8.96 | +16,275 |
| 2013-06 | 9 | 55.6% | 3.77 | +4,216 |
| 2013-07 | 16 | 62.5% | 4.43 | +5,288 |
| 2013-08 | 37 | 56.8% | 2.19 | +6,049 |
| 2013-09 | 13 | 69.2% | 5.38 | +5,661 |
| 2013-10 | 24 | 45.8% | 2.22 | +5,424 |
| 2013-11 | 44 | 54.5% | 2.99 | +13,296 |
| 2013-12 | 20 | 35.0% | 0.80 | −771 |
| 2014-01 | 37 | 59.5% | 2.61 | +6,926 |
| 2014-02 | 20 | 40.0% | 0.42 | −2,988 |
| 2014-03 | 37 | 73.0% | 3.90 | +13,013 |
| 2014-04 | 2 | 0.0% | 0.00 | −691 |
| 2014-05 | 8 | 75.0% | 16.03 | +5,151 |
| 2014-06 | 21 | 57.1% | 7.34 | +17,649 |
| 2014-07 | 15 | 60.0% | 2.53 | +3,387 |
| 2014-08 | 5 | 20.0% | 0.09 | −866 |
| 2014-09 | 10 | 40.0% | 0.52 | −1,025 |
| 2014-10 | 3 | 66.7% | 132.90 | +693 |
| 2014-11 | 18 | 100.0% | inf | +10,874 |
| 2014-12 | 11 | 45.5% | 0.48 | −1,385 |
| 2015-01 | 4 | 50.0% | 7.35 | +1,598 |
| 2015-02 | 26 | 69.2% | 3.31 | +6,734 |
| 2015-03 | 29 | 48.3% | 2.77 | +7,325 |
| 2015-04 | 25 | 68.0% | 2.93 | +6,194 |
| 2015-05 | 12 | 50.0% | 8.14 | +7,030 |
| 2015-06 | 12 | 58.3% | 2.58 | +1,558 |
| 2015-07 | 4 | 50.0% | 0.59 | −454 |
| 2015-08 | 1 | 0.0% | 0.00 | −716 |
| 2015-09 | 2 | 50.0% | inf | +426 |
| 2015-10 | 6 | 66.7% | 4.66 | +1,548 |
| 2015-11 | 21 | 66.7% | 4.84 | +10,039 |
| 2015-12 | 2 | 50.0% | 5.09 | +635 |
| 2016-01 | 2 | 50.0% | 0.61 | −362 |
| 2016-02 | 1 | 0.0% | inf | +0 |
| 2016-03 | 8 | 25.0% | 0.91 | −137 |
| 2016-04 | 6 | 50.0% | 4.20 | +1,278 |
| 2016-05 | 10 | 50.0% | 2.58 | +2,408 |
| 2016-06 | 7 | 57.1% | 2.73 | +1,203 |
| 2016-07 | 3 | 66.7% | 5.54 | +545 |
| 2016-08 | 51 | 56.9% | 3.66 | +10,910 |
| 2016-09 | 15 | 60.0% | 5.23 | +5,846 |
| 2016-10 | 13 | 61.5% | 2.88 | +3,107 |
| 2016-11 | 11 | 72.7% | 5.43 | +5,535 |
| 2016-12 | 24 | 70.8% | 3.41 | +4,161 |
| 2017-01 | 19 | 63.2% | 4.44 | +6,482 |
| 2017-02 | 31 | 64.5% | 2.23 | +4,713 |
| 2017-03 | 34 | 64.7% | 2.26 | +7,705 |
| 2017-04 | 11 | 54.5% | 0.35 | −1,919 |
| 2017-05 | 28 | 78.6% | 13.03 | +18,723 |
| 2017-06 | 17 | 70.6% | 5.80 | +9,668 |
| 2017-07 | 13 | 69.2% | 5.04 | +4,076 |
| 2017-08 | 13 | 46.2% | 1.06 | +147 |
| 2017-09 | 20 | 55.0% | 1.35 | +1,220 |
| 2017-10 | 32 | 62.5% | 4.40 | +9,985 |
| 2017-11 | 11 | 63.6% | 0.60 | −1,077 |
| 2017-12 | 19 | 31.6% | 0.55 | −2,749 |
| 2018-01 | 17 | 70.6% | 4.22 | +6,241 |
| 2018-02 | 9 | 22.2% | 0.74 | −649 |
| 2018-03 | 21 | 52.4% | 8.30 | +33,183 |
| 2018-04 | 5 | 80.0% | 4.35 | +443 |
| 2018-05 | 28 | 53.6% | 3.15 | +8,106 |
| 2018-06 | 37 | 64.9% | 3.60 | +8,790 |
| 2018-07 | 26 | 53.8% | 3.53 | +4,238 |
| 2018-08 | 30 | 63.3% | 5.38 | +9,982 |
| 2018-09 | 17 | 82.4% | 6.38 | +6,926 |
| 2018-10 | 1 | 100.0% | inf | +155 |
| 2018-11 | 10 | 60.0% | 3.03 | +3,270 |
| 2018-12 | 5 | 20.0% | 0.83 | −260 |
| 2019-01 | 1 | 0.0% | inf | +0 |
| 2019-02 | 24 | 75.0% | 4.14 | +10,832 |
| 2019-03 | 14 | 64.3% | 6.11 | +14,132 |
| 2019-04 | 7 | 57.1% | 3.25 | +1,974 |
| 2019-05 | 14 | 64.3% | 3.59 | +3,380 |
| 2019-06 | 3 | 33.3% | 0.63 | −119 |
| 2019-07 | 20 | 40.0% | 3.06 | +6,978 |
| 2019-08 | 14 | 42.9% | 0.68 | −638 |
| 2019-09 | 9 | 33.3% | 0.07 | −2,996 |
| 2019-10 | 5 | 60.0% | 1.63 | +120 |
| 2019-11 | 25 | 72.0% | 2.02 | +3,446 |
| 2019-12 | 26 | 65.4% | 3.77 | +10,254 |
| 2020-01 | 15 | 60.0% | 3.16 | +3,033 |
| 2020-02 | 24 | 33.3% | 0.81 | −1,801 |
| 2020-04 | 3 | 0.0% | 0.00 | −663 |
| 2020-05 | 14 | 78.6% | 4.31 | +11,843 |
| 2020-06 | 14 | 57.1% | 2.68 | +4,401 |
| 2020-07 | 8 | 50.0% | 7.63 | +4,917 |
| 2020-08 | 24 | 45.8% | 2.89 | +11,798 |
| 2020-09 | 6 | 66.7% | 3.65 | +1,991 |
| 2020-10 | 16 | 56.2% | 2.44 | +3,993 |
| 2020-11 | 14 | 64.3% | 1.58 | +1,806 |
| 2020-12 | 64 | 59.4% | 3.29 | +39,972 |
| 2021-01 | 58 | 63.8% | 25.61 | +315,829 |
| 2021-02 | 76 | 43.4% | 1.16 | +7,375 |
| 2021-03 | 24 | 50.0% | 1.36 | +2,885 |
| 2021-04 | 15 | 33.3% | 0.90 | −370 |
| 2021-05 | 10 | 60.0% | 4.77 | +2,312 |
| 2021-06 | 32 | 46.9% | 1.89 | +4,930 |
| 2021-07 | 7 | 57.1% | 4.57 | +2,013 |
| 2021-08 | 16 | 50.0% | 1.82 | +1,606 |
| 2021-09 | 14 | 64.3% | 9.16 | +12,930 |
| 2021-10 | 4 | 25.0% | 1.35 | +476 |
| 2021-11 | 36 | 66.7% | 3.85 | +14,827 |
| 2022-01 | 3 | 66.7% | 18.40 | +1,452 |
| 2022-02 | 2 | 50.0% | 0.90 | −208 |
| 2022-03 | 4 | 25.0% | 0.51 | −533 |
| 2022-04 | 6 | 83.3% | 233.46 | +6,295 |
| 2022-06 | 3 | 66.7% | 2.02 | +557 |
| 2022-07 | 1 | 100.0% | inf | +48 |
| 2022-08 | 15 | 46.7% | 1.83 | +3,778 |
| 2022-09 | 2 | 0.0% | 0.00 | −632 |
| 2022-10 | 1 | 0.0% | 0.00 | −23 |
| 2022-11 | 4 | 50.0% | 0.99 | −5 |
| 2022-12 | 9 | 55.6% | 4.97 | +7,316 |
| 2023-01 | 4 | 0.0% | 0.00 | −3,207 |
| 2023-02 | 7 | 57.1% | 1.51 | +641 |
| 2023-04 | 4 | 50.0% | 1.37 | +272 |
| 2023-05 | 7 | 42.9% | 2.46 | +1,528 |
| 2023-06 | 13 | 61.5% | 6.50 | +11,305 |
| 2023-07 | 10 | 70.0% | 6.33 | +2,273 |
| 2023-08 | 12 | 41.7% | 0.51 | −1,410 |
| 2023-09 | 4 | 75.0% | 0.66 | −395 |
| 2023-11 | 9 | 55.6% | 1.07 | +227 |
| 2023-12 | 21 | 81.0% | 3.56 | +11,938 |
| 2024-01 | 8 | 62.5% | 4.04 | +3,501 |
| 2024-02 | 23 | 43.5% | 1.73 | +3,898 |
| 2024-03 | 39 | 59.0% | 2.27 | +9,051 |
| 2024-04 | 8 | 87.5% | 64.47 | +7,696 |
| 2024-05 | 11 | 54.5% | 2.31 | +2,083 |
| 2024-07 | 1 | 100.0% | inf | +246 |
| 2024-08 | 9 | 44.4% | 1.84 | +1,655 |
| 2024-09 | 12 | 25.0% | 1.41 | +1,607 |
| 2024-10 | 15 | 46.7% | 2.08 | +3,055 |
| 2024-11 | 37 | 35.1% | 0.57 | −3,472 |
| 2024-12 | 10 | 70.0% | 1.53 | +1,935 |
| 2025-01 | 6 | 66.7% | 2.37 | +2,001 |
| 2025-02 | 20 | 55.0% | 2.00 | +3,780 |
| 2025-05 | 13 | 69.2% | 2.91 | +5,575 |
| 2025-06 | 22 | 59.1% | 3.41 | +11,293 |
| 2025-07 | 17 | 41.2% | 0.65 | −2,631 |
| 2025-08 | 10 | 50.0% | 2.87 | +2,693 |
| 2025-09 | 33 | 45.5% | 1.39 | +5,216 |
| 2025-10 | 9 | 44.4% | 3.49 | +6,216 |
| 2025-11 | 3 | 33.3% | 0.92 | −70 |
| 2025-12 | 18 | 44.4% | 0.99 | −69 |
| 2026-01 | 20 | 80.0% | 24.20 | +24,570 |
| 2026-02 | 14 | 50.0% | 1.40 | +1,149 |
| 2026-03 | 6 | 33.3% | 0.85 | −284 |
| 2026-04 | 7 | 71.4% | 9.90 | +7,903 |
| 2026-05 | 18 | 50.0% | 2.18 | +6,443 |

</details>

#### Window = 1-day-low stop 🛡 (swing / small-account)

half-Kelly 0.174/0.228/0.189 · total **+$1,062,486** Kelly (flat +$1,022,469) · months **202/257 (79%)** · years **22/22** · up:dn **6.9:1** · max DD **-3,299** · worst streak **3mo**

Yearly (Kelly-sized, by exit date):
| year | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005 | 198 | 56.1% | 3.00 | +35,639 |
| 2006 | 261 | 56.7% | 2.50 | +36,010 |
| 2007 | 208 | 54.8% | 2.55 | +31,569 |
| 2008 | 56 | 62.5% | 3.03 | +11,366 |
| 2009 | 75 | 64.0% | 3.13 | +15,685 |
| 2010 | 206 | 62.6% | 3.22 | +52,487 |
| 2011 | 149 | 55.7% | 2.64 | +32,073 |
| 2012 | 118 | 63.6% | 4.58 | +28,718 |
| 2013 | 281 | 64.8% | 3.06 | +70,362 |
| 2014 | 185 | 62.7% | 3.57 | +50,867 |
| 2015 | 143 | 62.9% | 3.40 | +32,714 |
| 2016 | 151 | 62.3% | 3.98 | +35,735 |
| 2017 | 249 | 63.1% | 2.85 | +58,883 |
| 2018 | 205 | 65.4% | 5.57 | +84,676 |
| 2019 | 163 | 63.8% | 3.56 | +46,658 |
| 2020 | 206 | 58.7% | 2.80 | +87,608 |
| 2021 | 287 | 55.4% | 3.97 | +206,764 |
| 2022 | 50 | 54.0% | 2.52 | +16,344 |
| 2023 | 94 | 63.8% | 2.25 | +22,106 |
| 2024 | 170 | 49.4% | 1.80 | +27,170 |
| 2025 | 151 | 53.0% | 2.16 | +41,331 |
| 2026 | 65 | 61.5% | 4.48 | +37,722 |

<details>
<summary>Full monthly table</summary>

| month | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005-01 | 1 | 100.0% | inf | +851 |
| 2005-02 | 26 | 50.0% | 2.74 | +6,174 |
| 2005-03 | 9 | 55.6% | 55.28 | +4,748 |
| 2005-05 | 10 | 60.0% | 5.28 | +1,659 |
| 2005-06 | 30 | 60.0% | 2.12 | +3,822 |
| 2005-07 | 33 | 69.7% | 6.06 | +7,001 |
| 2005-08 | 35 | 48.6% | 2.49 | +5,048 |
| 2005-09 | 16 | 50.0% | 2.35 | +2,028 |
| 2005-10 | 4 | 25.0% | 0.29 | −269 |
| 2005-11 | 17 | 58.8% | 2.09 | +1,729 |
| 2005-12 | 17 | 52.9% | 2.36 | +2,847 |
| 2006-01 | 31 | 61.3% | 2.28 | +4,869 |
| 2006-02 | 26 | 57.7% | 1.74 | +1,436 |
| 2006-03 | 28 | 75.0% | 7.74 | +9,537 |
| 2006-04 | 21 | 57.1% | 4.57 | +5,397 |
| 2006-05 | 38 | 60.5% | 2.05 | +4,034 |
| 2006-07 | 10 | 30.0% | 0.08 | −1,682 |
| 2006-08 | 14 | 64.3% | 5.15 | +3,579 |
| 2006-09 | 17 | 52.9% | 1.73 | +1,075 |
| 2006-10 | 31 | 41.9% | 1.78 | +2,563 |
| 2006-11 | 30 | 50.0% | 2.30 | +3,859 |
| 2006-12 | 15 | 60.0% | 2.29 | +1,344 |
| 2007-01 | 7 | 71.4% | 3.74 | +2,547 |
| 2007-02 | 51 | 56.9% | 3.15 | +8,552 |
| 2007-03 | 5 | 80.0% | 2.72 | +872 |
| 2007-04 | 37 | 51.4% | 3.53 | +6,138 |
| 2007-05 | 44 | 56.8% | 2.34 | +6,355 |
| 2007-06 | 18 | 38.9% | 2.30 | +2,195 |
| 2007-07 | 17 | 52.9% | 0.94 | −150 |
| 2007-09 | 9 | 66.7% | 2.33 | +1,064 |
| 2007-10 | 14 | 57.1% | 2.10 | +2,364 |
| 2007-11 | 2 | 50.0% | 18.36 | +497 |
| 2007-12 | 4 | 25.0% | 2.84 | +1,134 |
| 2008-02 | 5 | 60.0% | 1.13 | +88 |
| 2008-03 | 5 | 60.0% | 1.37 | +373 |
| 2008-04 | 4 | 100.0% | inf | +2,955 |
| 2008-05 | 20 | 75.0% | 6.39 | +7,222 |
| 2008-06 | 3 | 66.7% | 17.04 | +1,679 |
| 2008-07 | 5 | 40.0% | 0.43 | −270 |
| 2008-08 | 10 | 40.0% | 0.33 | −1,185 |
| 2008-09 | 1 | 0.0% | 0.00 | −60 |
| 2008-11 | 2 | 50.0% | 1.93 | +134 |
| 2008-12 | 1 | 100.0% | inf | +430 |
| 2009-01 | 2 | 50.0% | 0.38 | −116 |
| 2009-04 | 1 | 100.0% | inf | +138 |
| 2009-05 | 3 | 100.0% | inf | +3,735 |
| 2009-06 | 6 | 50.0% | 0.49 | −746 |
| 2009-07 | 3 | 66.7% | 2.30 | +363 |
| 2009-08 | 11 | 45.5% | 1.16 | +262 |
| 2009-09 | 10 | 60.0% | 2.17 | +1,728 |
| 2009-10 | 17 | 58.8% | 4.02 | +3,859 |
| 2009-11 | 6 | 83.3% | 29.53 | +4,127 |
| 2009-12 | 16 | 75.0% | 3.69 | +2,336 |
| 2010-01 | 29 | 69.0% | 4.12 | +8,691 |
| 2010-02 | 8 | 87.5% | 6.17 | +2,258 |
| 2010-03 | 16 | 43.8% | 2.74 | +3,350 |
| 2010-04 | 24 | 54.2% | 2.54 | +4,334 |
| 2010-05 | 14 | 35.7% | 0.59 | −1,385 |
| 2010-07 | 3 | 100.0% | inf | +675 |
| 2010-08 | 10 | 50.0% | 1.51 | +997 |
| 2010-09 | 10 | 90.0% | 121.47 | +2,934 |
| 2010-10 | 23 | 69.6% | 7.99 | +8,710 |
| 2010-11 | 46 | 63.0% | 2.13 | +8,577 |
| 2010-12 | 23 | 65.2% | 10.29 | +13,347 |
| 2011-01 | 27 | 51.9% | 3.12 | +8,877 |
| 2011-02 | 47 | 51.1% | 2.86 | +10,073 |
| 2011-03 | 9 | 77.8% | 4.85 | +1,708 |
| 2011-04 | 14 | 50.0% | 0.95 | −78 |
| 2011-05 | 26 | 50.0% | 2.22 | +6,226 |
| 2011-07 | 9 | 77.8% | 46.49 | +3,269 |
| 2011-09 | 4 | 75.0% | 2.09 | +900 |
| 2011-10 | 1 | 0.0% | 0.00 | −257 |
| 2011-11 | 9 | 55.6% | 1.06 | +95 |
| 2011-12 | 3 | 100.0% | inf | +1,262 |
| 2012-01 | 6 | 83.3% | 20.77 | +3,384 |
| 2012-02 | 22 | 77.3% | 5.86 | +4,392 |
| 2012-03 | 11 | 63.6% | 1.80 | +580 |
| 2012-04 | 5 | 60.0% | 16.00 | +3,397 |
| 2012-05 | 12 | 66.7% | 2.20 | +823 |
| 2012-06 | 13 | 30.8% | 8.00 | +9,117 |
| 2012-07 | 4 | 75.0% | 3.55 | +1,097 |
| 2012-08 | 20 | 75.0% | 4.29 | +4,352 |
| 2012-09 | 12 | 41.7% | 1.27 | +308 |
| 2012-10 | 4 | 50.0% | 6.94 | +1,198 |
| 2012-11 | 1 | 100.0% | inf | +77 |
| 2012-12 | 8 | 62.5% | 0.99 | −8 |
| 2013-01 | 16 | 62.5% | 0.93 | −201 |
| 2013-02 | 39 | 69.2% | 1.93 | +5,675 |
| 2013-03 | 24 | 70.8% | 2.26 | +4,520 |
| 2013-04 | 7 | 57.1% | 1.18 | +257 |
| 2013-05 | 33 | 78.8% | 19.76 | +19,714 |
| 2013-06 | 5 | 60.0% | 6.34 | +2,725 |
| 2013-07 | 18 | 66.7% | 5.02 | +6,005 |
| 2013-08 | 35 | 62.9% | 3.16 | +7,680 |
| 2013-09 | 13 | 76.9% | 6.75 | +6,138 |
| 2013-10 | 25 | 56.0% | 2.58 | +5,888 |
| 2013-11 | 45 | 60.0% | 2.68 | +11,179 |
| 2013-12 | 21 | 47.6% | 1.33 | +783 |
| 2014-01 | 35 | 65.7% | 4.05 | +8,025 |
| 2014-02 | 24 | 58.3% | 1.13 | +471 |
| 2014-03 | 32 | 68.8% | 6.01 | +11,946 |
| 2014-04 | 3 | 33.3% | 2.71 | +965 |
| 2014-05 | 7 | 85.7% | 16.41 | +3,932 |
| 2014-06 | 23 | 60.9% | 6.48 | +13,309 |
| 2014-07 | 13 | 61.5% | 3.38 | +3,669 |
| 2014-08 | 6 | 16.7% | 0.07 | −994 |
| 2014-09 | 9 | 33.3% | 0.53 | −792 |
| 2014-10 | 3 | 66.7% | 4.07 | +564 |
| 2014-11 | 19 | 89.5% | 16.11 | +9,619 |
| 2014-12 | 11 | 45.5% | 1.05 | +153 |
| 2015-01 | 6 | 33.3% | 1.22 | +96 |
| 2015-02 | 25 | 68.0% | 2.86 | +3,952 |
| 2015-03 | 27 | 55.6% | 3.16 | +7,503 |
| 2015-04 | 27 | 77.8% | 3.20 | +5,288 |
| 2015-05 | 11 | 54.5% | 13.34 | +8,181 |
| 2015-06 | 12 | 41.7% | 1.58 | +616 |
| 2015-07 | 3 | 66.7% | 25.80 | +687 |
| 2015-08 | 1 | 0.0% | 0.00 | −767 |
| 2015-09 | 2 | 50.0% | inf | +531 |
| 2015-10 | 8 | 75.0% | 8.73 | +2,852 |
| 2015-11 | 19 | 73.7% | 2.46 | +3,095 |
| 2015-12 | 2 | 50.0% | 5.09 | +680 |
| 2016-01 | 2 | 50.0% | 0.91 | −81 |
| 2016-02 | 2 | 50.0% | inf | +127 |
| 2016-03 | 9 | 33.3% | 0.70 | −360 |
| 2016-04 | 5 | 60.0% | 14.38 | +1,732 |
| 2016-05 | 9 | 66.7% | 2.65 | +1,472 |
| 2016-06 | 7 | 57.1% | 4.74 | +1,407 |
| 2016-07 | 5 | 60.0% | 5.15 | +967 |
| 2016-08 | 52 | 65.4% | 6.82 | +14,666 |
| 2016-09 | 13 | 53.8% | 3.41 | +4,152 |
| 2016-10 | 12 | 58.3% | 2.14 | +1,960 |
| 2016-11 | 12 | 75.0% | 7.62 | +5,568 |
| 2016-12 | 23 | 69.6% | 3.86 | +4,125 |
| 2017-01 | 22 | 63.6% | 7.28 | +8,102 |
| 2017-02 | 34 | 64.7% | 2.30 | +5,444 |
| 2017-03 | 28 | 60.7% | 1.62 | +3,416 |
| 2017-04 | 13 | 53.8% | 1.59 | +985 |
| 2017-05 | 26 | 80.8% | 12.40 | +13,773 |
| 2017-06 | 17 | 70.6% | 6.50 | +11,080 |
| 2017-07 | 14 | 78.6% | 13.47 | +4,960 |
| 2017-08 | 12 | 66.7% | 1.73 | +1,536 |
| 2017-09 | 22 | 54.5% | 1.86 | +2,739 |
| 2017-10 | 30 | 63.3% | 4.68 | +9,128 |
| 2017-11 | 12 | 58.3% | 0.46 | −1,930 |
| 2017-12 | 19 | 36.8% | 0.92 | −350 |
| 2018-01 | 17 | 82.4% | 10.97 | +6,008 |
| 2018-02 | 8 | 25.0% | 0.75 | −660 |
| 2018-03 | 22 | 59.1% | 7.65 | +31,141 |
| 2018-04 | 7 | 57.1% | 3.09 | +479 |
| 2018-05 | 27 | 59.3% | 4.38 | +8,292 |
| 2018-06 | 41 | 68.3% | 4.68 | +9,601 |
| 2018-07 | 21 | 57.1% | 6.61 | +4,709 |
| 2018-08 | 31 | 74.2% | 7.84 | +12,066 |
| 2018-09 | 16 | 87.5% | 12.09 | +8,296 |
| 2018-11 | 10 | 60.0% | 4.96 | +4,289 |
| 2018-12 | 5 | 40.0% | 1.55 | +456 |
| 2019-01 | 2 | 50.0% | inf | +99 |
| 2019-02 | 26 | 76.9% | 3.82 | +9,355 |
| 2019-03 | 11 | 72.7% | 18.32 | +14,897 |
| 2019-04 | 10 | 60.0% | 7.97 | +3,819 |
| 2019-05 | 11 | 63.6% | 3.40 | +2,512 |
| 2019-06 | 7 | 85.7% | 30.81 | +6,416 |
| 2019-07 | 20 | 35.0% | 0.81 | −592 |
| 2019-08 | 10 | 50.0% | 1.02 | +23 |
| 2019-09 | 10 | 40.0% | 0.17 | −2,292 |
| 2019-10 | 5 | 80.0% | 8.12 | +501 |
| 2019-11 | 29 | 75.9% | 2.33 | +4,116 |
| 2019-12 | 22 | 63.6% | 4.96 | +7,803 |
| 2020-01 | 15 | 60.0% | 2.01 | +2,446 |
| 2020-02 | 23 | 39.1% | 1.25 | +1,902 |
| 2020-04 | 3 | 33.3% | 0.64 | −201 |
| 2020-05 | 15 | 80.0% | 3.78 | +9,290 |
| 2020-06 | 15 | 60.0% | 3.09 | +4,019 |
| 2020-07 | 8 | 62.5% | 9.63 | +3,178 |
| 2020-08 | 24 | 50.0% | 3.84 | +15,374 |
| 2020-09 | 4 | 100.0% | inf | +4,227 |
| 2020-10 | 16 | 50.0% | 1.98 | +3,572 |
| 2020-11 | 15 | 80.0% | 2.56 | +4,255 |
| 2020-12 | 68 | 58.8% | 2.92 | +39,546 |
| 2021-01 | 62 | 61.3% | 12.01 | +156,356 |
| 2021-02 | 69 | 37.7% | 0.97 | −917 |
| 2021-03 | 23 | 69.6% | 4.84 | +11,761 |
| 2021-04 | 16 | 43.8% | 1.86 | +2,859 |
| 2021-05 | 10 | 70.0% | 4.17 | +1,565 |
| 2021-06 | 31 | 51.6% | 3.02 | +9,722 |
| 2021-07 | 6 | 66.7% | 5.50 | +2,257 |
| 2021-08 | 18 | 55.6% | 3.20 | +4,754 |
| 2021-09 | 12 | 66.7% | 7.33 | +8,661 |
| 2021-10 | 8 | 37.5% | 1.92 | +1,321 |
| 2021-11 | 32 | 75.0% | 3.11 | +8,425 |
| 2022-01 | 3 | 33.3% | 1.01 | +3 |
| 2022-02 | 2 | 50.0% | 0.86 | −297 |
| 2022-03 | 4 | 50.0% | 0.69 | −232 |
| 2022-04 | 6 | 83.3% | 182.99 | +5,279 |
| 2022-06 | 3 | 66.7% | 2.02 | +596 |
| 2022-07 | 2 | 50.0% | 3.41 | +187 |
| 2022-08 | 14 | 50.0% | 2.24 | +4,287 |
| 2022-09 | 2 | 0.0% | 0.00 | −677 |
| 2022-10 | 2 | 50.0% | 32.37 | +138 |
| 2022-11 | 3 | 66.7% | 1.01 | +8 |
| 2022-12 | 9 | 55.6% | 5.53 | +7,051 |
| 2023-01 | 4 | 0.0% | 0.00 | −3,299 |
| 2023-02 | 7 | 57.1% | 2.61 | +779 |
| 2023-04 | 5 | 40.0% | 0.51 | −686 |
| 2023-05 | 7 | 57.1% | 4.08 | +2,484 |
| 2023-06 | 12 | 66.7% | 6.18 | +7,158 |
| 2023-07 | 10 | 80.0% | 17.37 | +3,028 |
| 2023-08 | 13 | 46.2% | 0.58 | −1,101 |
| 2023-09 | 3 | 100.0% | inf | +821 |
| 2023-11 | 10 | 60.0% | 1.57 | +1,829 |
| 2023-12 | 23 | 82.6% | 3.60 | +11,093 |
| 2024-01 | 5 | 60.0% | 3.94 | +1,055 |
| 2024-02 | 24 | 41.7% | 0.85 | −853 |
| 2024-03 | 40 | 60.0% | 2.16 | +7,972 |
| 2024-04 | 6 | 83.3% | 45.52 | +5,783 |
| 2024-05 | 11 | 45.5% | 1.32 | +864 |
| 2024-07 | 2 | 100.0% | inf | +89 |
| 2024-08 | 9 | 44.4% | 1.84 | +1,654 |
| 2024-09 | 11 | 27.3% | 2.08 | +3,254 |
| 2024-10 | 15 | 53.3% | 3.69 | +6,623 |
| 2024-11 | 38 | 36.8% | 0.90 | −761 |
| 2024-12 | 9 | 66.7% | 1.45 | +1,490 |
| 2025-01 | 7 | 57.1% | 3.04 | +2,469 |
| 2025-02 | 19 | 63.2% | 2.96 | +5,123 |
| 2025-05 | 14 | 71.4% | 5.32 | +8,540 |
| 2025-06 | 23 | 56.5% | 3.41 | +10,558 |
| 2025-07 | 15 | 33.3% | 0.82 | −810 |
| 2025-08 | 12 | 58.3% | 5.48 | +5,205 |
| 2025-09 | 32 | 46.9% | 1.64 | +8,197 |
| 2025-10 | 9 | 33.3% | 0.29 | −2,263 |
| 2025-11 | 2 | 100.0% | inf | +1,086 |
| 2025-12 | 18 | 50.0% | 1.80 | +3,225 |
| 2026-01 | 22 | 77.3% | 21.43 | +22,055 |
| 2026-02 | 13 | 46.2% | 2.28 | +2,440 |
| 2026-03 | 5 | 40.0% | 0.51 | −1,002 |
| 2026-04 | 8 | 75.0% | 17.15 | +13,949 |
| 2026-05 | 17 | 52.9% | 1.06 | +279 |

</details>


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
