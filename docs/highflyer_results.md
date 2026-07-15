# Momentum / HighFlyer — Consolidated Research Log (v0 → v4)

This document merges the five momentum research notes into one chronological record.
Each version's original content is reproduced **verbatim** below, in order. The arc:

- **v0** — naive high-volume breakout-to-52w-high; established the signal + its regime dependence.
- **v1** — realistic-fill rewrite (caught the top-tick fill bug; PF ~1.7–1.8, not 3.0).
- **v2** — log-space volatility filters (log ATR% / tightness) + the move-cap.
- **v3** — HighFlyer, clipped methodology (the named production system; +50%-clip standard).
- **v4** — HighFlyer, float-feature era (dollar-float < $300M → ~PF 2.47).

The current production state is **v3/v4 (HighFlyer)**; v0–v2 are retained as historical record.

> **Note on internal links.** This doc was merged from five files (`momentum_v0_results.md` …
> `momentum_v4_results.md`). The preserved sections below still reference each other by their old
> filenames (e.g. "*lives in `momentum_v3_results.md`*"); those now mean the corresponding
> **`#v3` section of this same document** (see Contents). The original text is kept verbatim.

## Contents
- [v0 — Regime-Dependent, Parabolic Bull Innings](#v0)
- [v1 — Realistic-Fill Rewrite](#v1)
- [v2 — Log-Space Volatility Filters](#v2)
- [v3 — HighFlyer, Clipped-Methodology Era](#v3)
- [v4 — HighFlyer, Float-Feature Era](#v4)



---

<a id="v0"></a>

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


---

<a id="v1"></a>

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
| **4** | **baseline** ⭐ | 3671 | 45.3% | 1.760 | 805,994 | 56.7% | **−16,788** | 4mo |
| 4 | N=1 cap5 | 3671 | 48.3% | **1.776** | 857,491 | 59.3% | −27,510 | 5mo |
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
   sweet spot** (highest PF, shallowest drawdown); window 15 is the max-P&L / max-risk end. ⭐ marks
   the chosen default: **stop-window 4 + baseline exit** — its −$16.8k drawdown is ~40% shallower
   than N=1's −$27.5k for only −0.02 PF, a deliberate trade of PF for drawdown.

4. **Monthly consistency tops out ~64–68%**, not the 80% v0 claimed. Higher-N exits raise win
   rate and month-positive % (more, smaller winners) but not PF.

## RVOL floor — a quantity/capacity dial, NOT a quality dial

The production rvol floor is 6.0. Lowering it admits more trades. Sweep at the default
(stop-4, baseline, breadth ON, 2005+); `--rvol-min N --rvol-max 20`:

| rvol ≥ | trips | flat PF | flat net | max monthly DD | half-Kelly PF | Kelly net |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 3 | 12,641 | 1.390 | +1,435,044 | −110,590 | 1.594 | +2,170,518 |
| 4 | 7,909 | 1.545 | +1,248,676 | −44,018 | 1.724 | +1,651,796 |
| 5 | 5,307 | 1.738 | +1,128,851 | −24,961 | 1.864 | +1,317,503 |
| **6** | 3,671 | 1.760 | +805,994 | **−16,788** | 1.980 | +1,032,607 |

More trades buy more total P&L (rvol≥3 nearly doubles flat net to $1.44M) but at lower PF and a
**much** deeper drawdown (−$111k vs −$17k, ~6.5×).

**The key finding (rvol≥5 vs ≥6): the extra trades are NOT lower quality — the deeper drawdown is
just more exposure.** Adding ~45% more trades (3,671 → 5,307) leaves the share of profitable months
**unchanged: 56.8% vs 56.7%.** On the 247 shared months, 214 agree on sign (90 both-red, 124
both-green); only 33 near-zero months flip. The same months are good or bad in both — rvol≥5 simply
sizes the bad months bigger because more positions are open into the same downturns. The tiny PF dip
(1.74 vs 1.76) is consistent with that.

This is structurally different from the stop-window dial: tightening the stop changes *per-trade*
behavior (win rate, hold time); lowering the rvol floor just adds *more of the same edge* at higher
capacity, preserving the monthly win/loss *pattern* and only scaling the *magnitudes*. So the
extra-drawdown is a position-sizing/concurrent-exposure problem (cap concurrent positions, or cut
per-trade notional), **not** signal degradation — the rvol 5–6 names carry the same edge. rvol≥5 is
the reasonable "more size, still shallow drawdown" point; rvol≥3 maximizes raw dollars at a drawdown
that contradicts the drawdown-conscious default.

## Working default: stop-window 4, baseline exit (cap=0)

> ⚠️ **SUPERSEDED 2026-06-18 by the log-space entry rewrite** (next section). The exit choice
> below (stop-4, next-open baseline) still stands, but the entry filters here are the OLD
> absolute-scale ones (ATR%<8% on a current-close denominator, tightness<0.30, up≥5%). The new
> default re-tunes all three on a log scale and adds an entry-day-move floor. Numbers in this
> section (PF 1.760 flat / 1.980 Kelly) are the old-entry baseline, kept for comparison.

**Chosen to halve the drawdown at a small PF cost.** vs the N=1 trailing-limit exit, the baseline
(just sell at the next open after a stop) cuts the max monthly drawdown from −$27.5k to **−$16.8k**
(flat sizing) for a negligible PF give-up (1.776 → 1.760). Given the preference to sacrifice some
PF to halve drawdown, baseline is the default. The trailing limit added only ~+1% PF anyway.

Yearly breakdown — flat $10k/trip **and** min-tercile half-Kelly sizing (realistic fill,
post-breadth, 2005+). Half-Kelly fractions recomputed on THIS config's returns: **0.053 / 0.126 /
0.149** for min-buckets 1/2/3 (breadth tiers <0.61 / 0.61–0.70 / ≥0.70; rvol tiers <7.2 / 7.2–9.6
/ ≥9.6; normalized to mean weight 1).

| year | trips | flat win% | flat PF | flat net | Kelly PF | Kelly net |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2005 | 195 | 43% | 0.97 | -1,623 | 0.95 | -3,168 |
| 2006 | 261 | 41% | 1.58 | +31,466 | 1.80 | +42,926 |
| 2007 | 209 | 41% | 1.75 | +35,534 | 2.15 | +55,993 |
| 2008 | 55 | 58% | 1.85 | +13,081 | 1.65 | +8,987 |
| 2009 | 71 | 48% | 2.43 | +22,140 | 2.97 | +29,089 |
| 2010 | 206 | 50% | 2.06 | +50,982 | 2.24 | +61,511 |
| 2011 | 154 | 44% | 1.62 | +22,237 | 1.66 | +23,499 |
| 2012 | 115 | 45% | 2.10 | +24,331 | 2.72 | +36,994 |
| 2013 | 274 | 50% | 1.84 | +57,375 | 1.73 | +50,747 |
| 2014 | 193 | 48% | 1.42 | +20,587 | 1.67 | +30,946 |
| 2015 | 145 | 52% | 2.31 | +41,308 | 2.51 | +37,652 |
| 2016 | 150 | 45% | 1.88 | +21,024 | 1.79 | +18,293 |
| 2017 | 245 | 46% | 1.28 | +18,807 | 1.23 | +14,414 |
| 2018 | 210 | 50% | 1.99 | +49,106 | 2.26 | +56,029 |
| 2019 | 156 | 50% | 1.66 | +28,435 | 2.08 | +41,266 |
| 2020 | 200 | 39% | 1.02 | +1,767 | 1.20 | +18,325 |
| 2021 | 301 | 42% | 3.18 | +314,162 | 3.83 | +449,277 |
| 2022 | 49 | 41% | 1.02 | +501 | 1.11 | +2,491 |
| 2023 | 84 | 50% | 1.29 | +8,125 | 1.13 | +3,934 |
| 2024 | 180 | 42% | 1.31 | +17,976 | 1.28 | +14,366 |
| 2025 | 152 | 40% | 1.01 | +538 | 1.14 | +8,959 |
| 2026 | 66 | 50% | 2.04 | +28,136 | 2.11 | +30,075 |

|  | flat | half-Kelly |
| --- | ---: | ---: |
| PF | 1.760 | **1.980** |
| net P&L | +805,994 | +1,032,607 |
| months positive | 140/247 (57%) | 138/247 (56%) |
| max monthly DD | **−16,788** | −18,369 |
| years positive | 21/22 | 21/22 |

Half-Kelly lifts PF to **1.98** for ~$227k more P&L, with the drawdown essentially unchanged
(−$18.4k vs −$16.8k) — the sizing concentrates into the high-conviction buckets without adding
tail risk. 21/22 years positive (only 2005, the cold-start year, is fractionally red at −$1.6k
flat). **2020 is now POSITIVE even flat (+$1.8k), and +$18.3k Kelly** — the baseline's next-open
exits got out of the COVID-crash names a touch better than the resting limit did.

<details>
<summary>Full monthly breakdown — FLAT sizing (stop-4, baseline, by exit date) — click to expand</summary>

| month | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005-01 | 1 | 100% | inf | +747 |
| 2005-02 | 22 | 32% | 0.60 | -2,631 |
| 2005-03 | 12 | 50% | 2.62 | +3,628 |
| 2005-04 | 1 | 0% | 0.00 | -1,750 |
| 2005-05 | 5 | 20% | 0.76 | -216 |
| 2005-06 | 26 | 42% | 0.38 | -6,759 |
| 2005-07 | 28 | 46% | 0.90 | -732 |
| 2005-08 | 49 | 49% | 1.79 | +8,402 |
| 2005-09 | 15 | 13% | 0.10 | -4,555 |
| 2005-10 | 5 | 40% | 0.67 | -484 |
| 2005-11 | 16 | 62% | 1.18 | +562 |
| 2005-12 | 15 | 40% | 1.61 | +2,164 |
| 2006-01 | 21 | 48% | 1.18 | +951 |
| 2006-02 | 33 | 45% | 0.81 | -1,071 |
| 2006-03 | 27 | 44% | 2.03 | +3,782 |
| 2006-04 | 22 | 50% | 7.42 | +24,199 |
| 2006-05 | 44 | 30% | 0.57 | -5,669 |
| 2006-07 | 9 | 11% | 0.01 | -3,591 |
| 2006-08 | 14 | 43% | 1.17 | +580 |
| 2006-09 | 14 | 36% | 2.00 | +2,296 |
| 2006-10 | 21 | 38% | 1.13 | +537 |
| 2006-11 | 35 | 43% | 1.83 | +5,255 |
| 2006-12 | 21 | 48% | 2.54 | +4,197 |
| 2007-01 | 7 | 57% | 15.89 | +4,182 |
| 2007-02 | 46 | 35% | 1.08 | +777 |
| 2007-03 | 9 | 67% | 2.98 | +3,025 |
| 2007-04 | 23 | 39% | 1.31 | +960 |
| 2007-05 | 53 | 43% | 1.21 | +2,761 |
| 2007-06 | 24 | 33% | 2.16 | +5,897 |
| 2007-07 | 17 | 35% | 0.25 | -3,197 |
| 2007-08 | 1 | 0% | 0.00 | -104 |
| 2007-09 | 4 | 50% | 0.17 | -1,122 |
| 2007-10 | 19 | 53% | 4.31 | +22,731 |
| 2007-11 | 2 | 0% | 0.00 | -1,219 |
| 2007-12 | 4 | 25% | 1.76 | +844 |
| 2008-02 | 4 | 50% | 0.32 | -479 |
| 2008-03 | 5 | 60% | 3.22 | +1,654 |
| 2008-04 | 3 | 67% | 2.08 | +1,104 |
| 2008-05 | 13 | 69% | 3.35 | +3,451 |
| 2008-06 | 12 | 92% | 78.83 | +14,070 |
| 2008-07 | 2 | 0% | 0.00 | -1,668 |
| 2008-08 | 11 | 27% | 0.18 | -6,441 |
| 2008-09 | 3 | 33% | 0.91 | -112 |
| 2008-11 | 1 | 0% | 0.00 | -429 |
| 2008-12 | 1 | 100% | inf | +1,930 |
| 2009-01 | 3 | 67% | 0.61 | -231 |
| 2009-04 | 1 | 0% | 0.00 | -174 |
| 2009-05 | 3 | 100% | inf | +3,266 |
| 2009-06 | 5 | 40% | 0.43 | -1,102 |
| 2009-07 | 2 | 100% | inf | +1,686 |
| 2009-08 | 11 | 18% | 0.26 | -2,778 |
| 2009-09 | 10 | 50% | 8.21 | +16,797 |
| 2009-10 | 16 | 56% | 1.87 | +2,169 |
| 2009-11 | 5 | 80% | 10.91 | +3,105 |
| 2009-12 | 15 | 33% | 0.85 | -599 |
| 2010-01 | 29 | 45% | 1.27 | +1,807 |
| 2010-02 | 6 | 67% | 1.21 | +213 |
| 2010-03 | 18 | 56% | 2.78 | +5,771 |
| 2010-04 | 21 | 52% | 3.52 | +12,383 |
| 2010-05 | 22 | 27% | 0.60 | -4,017 |
| 2010-07 | 1 | 100% | inf | +26 |
| 2010-08 | 10 | 40% | 0.74 | -860 |
| 2010-09 | 9 | 67% | 61.40 | +13,617 |
| 2010-10 | 18 | 56% | 5.45 | +6,952 |
| 2010-11 | 48 | 48% | 0.91 | -1,344 |
| 2010-12 | 24 | 62% | 6.69 | +16,433 |
| 2011-01 | 26 | 38% | 1.04 | +273 |
| 2011-02 | 43 | 56% | 3.63 | +19,102 |
| 2011-03 | 18 | 50% | 1.93 | +2,273 |
| 2011-04 | 9 | 11% | 0.10 | -2,849 |
| 2011-05 | 30 | 27% | 0.91 | -894 |
| 2011-06 | 2 | 100% | inf | +3,983 |
| 2011-07 | 7 | 71% | 7.47 | +2,019 |
| 2011-08 | 2 | 50% | 0.16 | -61 |
| 2011-09 | 2 | 0% | 0.00 | -1,612 |
| 2011-10 | 3 | 33% | 1.36 | +326 |
| 2011-11 | 9 | 44% | 0.53 | -1,323 |
| 2011-12 | 3 | 100% | inf | +1,000 |
| 2012-01 | 4 | 75% | 2.82 | +1,069 |
| 2012-02 | 15 | 53% | 1.36 | +810 |
| 2012-03 | 17 | 59% | 3.00 | +4,640 |
| 2012-04 | 8 | 50% | 3.34 | +3,552 |
| 2012-05 | 12 | 25% | 0.04 | -3,887 |
| 2012-06 | 6 | 0% | 0.00 | -2,381 |
| 2012-07 | 10 | 50% | 11.92 | +14,617 |
| 2012-08 | 15 | 53% | 0.98 | -37 |
| 2012-09 | 16 | 44% | 3.35 | +5,858 |
| 2012-10 | 5 | 40% | 1.94 | +1,314 |
| 2012-11 | 2 | 0% | 0.00 | -379 |
| 2012-12 | 5 | 40% | 0.28 | -845 |
| 2013-01 | 15 | 47% | 0.85 | -573 |
| 2013-02 | 32 | 53% | 1.34 | +3,007 |
| 2013-03 | 23 | 70% | 1.82 | +4,249 |
| 2013-04 | 18 | 67% | 3.38 | +7,189 |
| 2013-05 | 27 | 59% | 3.71 | +10,165 |
| 2013-06 | 12 | 67% | 6.81 | +8,603 |
| 2013-07 | 11 | 55% | 3.46 | +2,383 |
| 2013-08 | 41 | 34% | 1.10 | +1,184 |
| 2013-09 | 13 | 38% | 4.79 | +8,929 |
| 2013-10 | 21 | 33% | 1.82 | +6,126 |
| 2013-11 | 43 | 47% | 0.99 | -178 |
| 2013-12 | 18 | 44% | 2.15 | +6,290 |
| 2014-01 | 43 | 42% | 0.91 | -1,025 |
| 2014-02 | 14 | 21% | 0.29 | -3,977 |
| 2014-03 | 42 | 57% | 1.73 | +8,444 |
| 2014-04 | 4 | 0% | 0.00 | -1,284 |
| 2014-05 | 7 | 71% | 22.08 | +5,335 |
| 2014-06 | 19 | 53% | 4.01 | +9,844 |
| 2014-07 | 18 | 44% | 1.53 | +1,777 |
| 2014-08 | 2 | 50% | 0.06 | -447 |
| 2014-09 | 13 | 31% | 0.48 | -3,313 |
| 2014-10 | 1 | 100% | inf | +9 |
| 2014-11 | 18 | 72% | 13.74 | +7,983 |
| 2014-12 | 12 | 50% | 0.46 | -2,760 |
| 2015-01 | 3 | 33% | 5.65 | +2,920 |
| 2015-02 | 23 | 48% | 1.82 | +3,754 |
| 2015-03 | 30 | 57% | 3.85 | +17,735 |
| 2015-04 | 22 | 55% | 0.94 | -324 |
| 2015-05 | 15 | 40% | 1.68 | +2,030 |
| 2015-06 | 12 | 58% | 4.00 | +5,410 |
| 2015-07 | 8 | 50% | 1.35 | +1,071 |
| 2015-08 | 1 | 0% | 0.00 | -1,220 |
| 2015-09 | 1 | 0% | inf | +0 |
| 2015-10 | 4 | 50% | 5.64 | +1,946 |
| 2015-11 | 21 | 52% | 1.83 | +4,138 |
| 2015-12 | 5 | 80% | 7.62 | +3,847 |
| 2016-01 | 2 | 50% | 0.74 | -187 |
| 2016-02 | 1 | 0% | 0.00 | -36 |
| 2016-03 | 8 | 25% | 0.61 | -1,135 |
| 2016-04 | 6 | 50% | 1.80 | +669 |
| 2016-05 | 10 | 40% | 2.08 | +2,102 |
| 2016-06 | 7 | 29% | 0.80 | -206 |
| 2016-07 | 2 | 50% | 0.76 | -45 |
| 2016-08 | 50 | 42% | 1.99 | +6,496 |
| 2016-09 | 15 | 53% | 3.87 | +5,051 |
| 2016-10 | 15 | 60% | 1.68 | +2,138 |
| 2016-11 | 5 | 60% | 3.66 | +3,022 |
| 2016-12 | 29 | 45% | 1.86 | +3,154 |
| 2017-01 | 17 | 35% | 1.08 | +261 |
| 2017-02 | 29 | 48% | 1.67 | +4,001 |
| 2017-03 | 36 | 58% | 1.27 | +2,482 |
| 2017-04 | 10 | 20% | 0.95 | -184 |
| 2017-05 | 29 | 66% | 4.22 | +16,884 |
| 2017-06 | 18 | 61% | 4.01 | +8,024 |
| 2017-07 | 12 | 42% | 0.91 | -201 |
| 2017-08 | 16 | 44% | 0.90 | -432 |
| 2017-09 | 17 | 29% | 0.16 | -6,436 |
| 2017-10 | 32 | 44% | 1.75 | +4,457 |
| 2017-11 | 10 | 40% | 0.29 | -3,728 |
| 2017-12 | 19 | 26% | 0.38 | -6,322 |
| 2018-01 | 16 | 62% | 2.00 | +3,422 |
| 2018-02 | 14 | 36% | 0.38 | -3,570 |
| 2018-03 | 21 | 38% | 3.81 | +23,551 |
| 2018-04 | 4 | 50% | 0.95 | -22 |
| 2018-05 | 21 | 43% | 0.85 | -1,043 |
| 2018-06 | 41 | 51% | 2.32 | +9,838 |
| 2018-07 | 24 | 42% | 1.05 | +186 |
| 2018-08 | 28 | 61% | 2.90 | +8,158 |
| 2018-09 | 23 | 70% | 6.32 | +11,695 |
| 2018-10 | 3 | 0% | 0.00 | -960 |
| 2018-11 | 10 | 60% | 1.44 | +980 |
| 2018-12 | 5 | 20% | 0.13 | -3,131 |
| 2019-01 | 1 | 0% | inf | +0 |
| 2019-02 | 16 | 81% | 2.83 | +5,102 |
| 2019-03 | 22 | 50% | 2.02 | +9,773 |
| 2019-04 | 6 | 50% | 2.03 | +1,339 |
| 2019-05 | 14 | 57% | 2.56 | +4,575 |
| 2019-06 | 2 | 50% | 3.15 | +1,110 |
| 2019-07 | 21 | 48% | 2.46 | +6,459 |
| 2019-08 | 15 | 13% | 0.12 | -5,517 |
| 2019-09 | 8 | 12% | 0.02 | -4,814 |
| 2019-10 | 4 | 25% | 0.08 | -507 |
| 2019-11 | 21 | 48% | 0.64 | -2,019 |
| 2019-12 | 26 | 69% | 3.98 | +12,935 |
| 2020-01 | 20 | 40% | 1.02 | +113 |
| 2020-02 | 22 | 14% | 0.27 | -14,172 |
| 2020-03 | 3 | 33% | 0.32 | -1,310 |
| 2020-04 | 3 | 0% | 0.00 | -1,306 |
| 2020-05 | 13 | 62% | 3.36 | +10,027 |
| 2020-06 | 14 | 36% | 0.38 | -3,642 |
| 2020-07 | 7 | 71% | 5.24 | +3,977 |
| 2020-08 | 24 | 33% | 1.28 | +3,064 |
| 2020-09 | 8 | 62% | 3.45 | +6,131 |
| 2020-10 | 14 | 36% | 0.36 | -3,451 |
| 2020-11 | 12 | 25% | 0.29 | -3,851 |
| 2020-12 | 60 | 45% | 1.23 | +6,186 |
| 2021-01 | 57 | 56% | 16.08 | +277,704 |
| 2021-02 | 80 | 35% | 1.54 | +33,169 |
| 2021-03 | 28 | 39% | 1.30 | +3,668 |
| 2021-04 | 14 | 29% | 0.18 | -7,343 |
| 2021-05 | 11 | 55% | 1.16 | +304 |
| 2021-06 | 30 | 23% | 0.31 | -8,854 |
| 2021-07 | 9 | 67% | 2.96 | +3,123 |
| 2021-08 | 14 | 50% | 0.50 | -1,886 |
| 2021-09 | 16 | 50% | 4.90 | +15,767 |
| 2021-10 | 3 | 0% | 0.00 | -1,763 |
| 2021-11 | 34 | 35% | 0.67 | -5,271 |
| 2021-12 | 5 | 80% | 6.26 | +5,545 |
| 2022-01 | 3 | 67% | 0.50 | -357 |
| 2022-02 | 2 | 50% | 0.76 | -665 |
| 2022-03 | 3 | 33% | 0.25 | -1,439 |
| 2022-04 | 7 | 43% | 4.59 | +2,834 |
| 2022-06 | 3 | 33% | 0.27 | -1,575 |
| 2022-07 | 1 | 100% | inf | +123 |
| 2022-08 | 14 | 36% | 0.78 | -1,804 |
| 2022-09 | 3 | 33% | 1.19 | +265 |
| 2022-11 | 4 | 25% | 0.03 | -1,542 |
| 2022-12 | 9 | 44% | 3.90 | +4,661 |
| 2023-01 | 4 | 25% | 0.16 | -2,236 |
| 2023-02 | 6 | 33% | 0.64 | -828 |
| 2023-03 | 2 | 50% | 20.13 | +1,358 |
| 2023-04 | 4 | 50% | 0.97 | -40 |
| 2023-05 | 4 | 25% | 0.61 | -630 |
| 2023-06 | 14 | 57% | 2.97 | +6,846 |
| 2023-07 | 10 | 70% | 5.12 | +5,311 |
| 2023-08 | 14 | 36% | 0.51 | -2,533 |
| 2023-09 | 4 | 75% | 0.37 | -629 |
| 2023-11 | 6 | 33% | 0.35 | -2,346 |
| 2023-12 | 16 | 62% | 1.73 | +3,852 |
| 2024-01 | 16 | 69% | 4.30 | +7,212 |
| 2024-02 | 17 | 29% | 0.60 | -3,284 |
| 2024-03 | 42 | 45% | 1.69 | +9,354 |
| 2024-04 | 11 | 73% | 11.96 | +9,041 |
| 2024-05 | 9 | 33% | 0.33 | -1,637 |
| 2024-06 | 2 | 100% | inf | +2,993 |
| 2024-07 | 1 | 100% | inf | +38 |
| 2024-08 | 8 | 38% | 0.46 | -1,532 |
| 2024-09 | 9 | 44% | 1.56 | +1,684 |
| 2024-10 | 16 | 31% | 0.87 | -1,016 |
| 2024-11 | 38 | 21% | 0.72 | -3,582 |
| 2024-12 | 11 | 55% | 0.73 | -1,298 |
| 2025-01 | 3 | 67% | 1.82 | +893 |
| 2025-02 | 23 | 39% | 0.56 | -3,359 |
| 2025-03 | 1 | 0% | 0.00 | -1,008 |
| 2025-05 | 12 | 50% | 1.24 | +1,350 |
| 2025-06 | 20 | 50% | 1.45 | +3,518 |
| 2025-07 | 18 | 44% | 1.15 | +1,210 |
| 2025-08 | 9 | 22% | 0.67 | -1,431 |
| 2025-09 | 32 | 44% | 0.86 | -2,226 |
| 2025-10 | 12 | 33% | 1.95 | +5,963 |
| 2025-11 | 4 | 0% | 0.00 | -2,573 |
| 2025-12 | 18 | 33% | 0.75 | -1,799 |
| 2026-01 | 15 | 67% | 7.72 | +10,424 |
| 2026-02 | 16 | 38% | 0.71 | -1,571 |
| 2026-03 | 9 | 56% | 7.27 | +18,394 |
| 2026-04 | 5 | 40% | 0.74 | -433 |
| 2026-05 | 21 | 48% | 1.09 | +1,322 |

</details>

<details>
<summary>Full monthly breakdown — half-KELLY sizing (stop-4, baseline) — click to expand</summary>

| month | trips | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| 2005-01 | 1 | 100% | inf | +448 |
| 2005-02 | 22 | 32% | 0.66 | -2,788 |
| 2005-03 | 12 | 50% | 3.42 | +5,106 |
| 2005-04 | 1 | 0% | 0.00 | -2,492 |
| 2005-05 | 5 | 20% | 1.35 | +254 |
| 2005-06 | 26 | 42% | 0.29 | -9,946 |
| 2005-07 | 28 | 46% | 0.98 | -206 |
| 2005-08 | 49 | 49% | 1.93 | +10,458 |
| 2005-09 | 15 | 13% | 0.17 | -3,542 |
| 2005-10 | 5 | 40% | 0.67 | -290 |
| 2005-11 | 16 | 62% | 0.68 | -1,362 |
| 2005-12 | 15 | 40% | 1.30 | +1,192 |
| 2006-01 | 21 | 48% | 1.31 | +2,073 |
| 2006-02 | 33 | 45% | 0.87 | -712 |
| 2006-03 | 27 | 44% | 1.31 | +1,293 |
| 2006-04 | 22 | 50% | 8.90 | +33,159 |
| 2006-05 | 44 | 30% | 0.69 | -2,921 |
| 2006-07 | 9 | 11% | 0.01 | -4,619 |
| 2006-08 | 14 | 43% | 0.98 | -53 |
| 2006-09 | 14 | 36% | 1.10 | +246 |
| 2006-10 | 21 | 38% | 1.53 | +2,194 |
| 2006-11 | 35 | 43% | 2.01 | +7,127 |
| 2006-12 | 21 | 48% | 3.06 | +5,138 |
| 2007-01 | 7 | 57% | 15.89 | +2,506 |
| 2007-02 | 46 | 35% | 0.94 | -756 |
| 2007-03 | 9 | 67% | 3.23 | +4,590 |
| 2007-04 | 23 | 39% | 1.38 | +1,510 |
| 2007-05 | 53 | 43% | 1.75 | +8,775 |
| 2007-06 | 24 | 33% | 3.58 | +9,096 |
| 2007-07 | 17 | 35% | 0.35 | -1,860 |
| 2007-08 | 1 | 0% | 0.00 | -62 |
| 2007-09 | 4 | 50% | 0.08 | -1,514 |
| 2007-10 | 19 | 53% | 4.87 | +34,371 |
| 2007-11 | 2 | 0% | 0.00 | -730 |
| 2007-12 | 4 | 25% | 1.06 | +68 |
| 2008-02 | 4 | 50% | 0.26 | -699 |
| 2008-03 | 5 | 60% | 3.22 | +991 |
| 2008-04 | 3 | 67% | 1.17 | +251 |
| 2008-05 | 13 | 69% | 2.16 | +1,594 |
| 2008-06 | 12 | 92% | 119.15 | +12,800 |
| 2008-07 | 2 | 0% | 0.00 | -1,000 |
| 2008-08 | 11 | 27% | 0.24 | -5,191 |
| 2008-09 | 3 | 33% | 0.52 | -660 |
| 2008-11 | 1 | 0% | 0.00 | -257 |
| 2008-12 | 1 | 100% | inf | +1,157 |
| 2009-01 | 3 | 67% | 0.61 | -138 |
| 2009-04 | 1 | 0% | 0.00 | -248 |
| 2009-05 | 3 | 100% | inf | +5,152 |
| 2009-06 | 5 | 40% | 0.34 | -1,605 |
| 2009-07 | 2 | 100% | inf | +1,491 |
| 2009-08 | 11 | 18% | 0.39 | -2,159 |
| 2009-09 | 10 | 50% | 10.58 | +24,430 |
| 2009-10 | 16 | 56% | 1.07 | +218 |
| 2009-11 | 5 | 80% | 11.21 | +1,918 |
| 2009-12 | 15 | 33% | 1.01 | +31 |
| 2010-01 | 29 | 45% | 1.22 | +1,890 |
| 2010-02 | 6 | 67% | 1.29 | +179 |
| 2010-03 | 18 | 56% | 1.88 | +3,797 |
| 2010-04 | 21 | 52% | 5.56 | +17,484 |
| 2010-05 | 22 | 27% | 0.73 | -2,337 |
| 2010-07 | 1 | 100% | inf | +15 |
| 2010-08 | 10 | 40% | 0.33 | -2,919 |
| 2010-09 | 9 | 67% | 53.75 | +14,827 |
| 2010-10 | 18 | 56% | 4.78 | +8,767 |
| 2010-11 | 48 | 48% | 1.04 | +568 |
| 2010-12 | 24 | 62% | 6.68 | +19,238 |
| 2011-01 | 26 | 38% | 1.11 | +978 |
| 2011-02 | 43 | 56% | 2.95 | +14,683 |
| 2011-03 | 18 | 50% | 2.13 | +2,368 |
| 2011-04 | 9 | 11% | 0.19 | -1,975 |
| 2011-05 | 30 | 27% | 1.16 | +1,463 |
| 2011-06 | 2 | 100% | inf | +2,387 |
| 2011-07 | 7 | 71% | 15.10 | +2,771 |
| 2011-08 | 2 | 50% | 0.16 | -36 |
| 2011-09 | 2 | 0% | 0.00 | -2,478 |
| 2011-10 | 3 | 33% | 2.33 | +1,187 |
| 2011-11 | 9 | 44% | 1.38 | +682 |
| 2011-12 | 3 | 100% | inf | +1,470 |
| 2012-01 | 4 | 75% | 3.22 | +1,851 |
| 2012-02 | 15 | 53% | 0.92 | -285 |
| 2012-03 | 17 | 59% | 3.67 | +6,208 |
| 2012-04 | 8 | 50% | 5.24 | +4,213 |
| 2012-05 | 12 | 25% | 0.06 | -3,280 |
| 2012-06 | 6 | 0% | 0.00 | -1,896 |
| 2012-07 | 10 | 50% | 24.16 | +21,897 |
| 2012-08 | 15 | 53% | 0.78 | -626 |
| 2012-09 | 16 | 44% | 4.91 | +9,185 |
| 2012-10 | 5 | 40% | 1.94 | +788 |
| 2012-11 | 2 | 0% | 0.00 | -227 |
| 2012-12 | 5 | 40% | 0.37 | -834 |
| 2013-01 | 15 | 47% | 0.36 | -3,799 |
| 2013-02 | 32 | 53% | 1.29 | +3,137 |
| 2013-03 | 23 | 70% | 1.43 | +3,033 |
| 2013-04 | 18 | 67% | 2.04 | +4,124 |
| 2013-05 | 27 | 59% | 4.24 | +10,571 |
| 2013-06 | 12 | 67% | 10.37 | +12,885 |
| 2013-07 | 11 | 55% | 2.51 | +1,568 |
| 2013-08 | 41 | 34% | 1.08 | +774 |
| 2013-09 | 13 | 38% | 3.50 | +7,537 |
| 2013-10 | 21 | 33% | 1.19 | +1,880 |
| 2013-11 | 43 | 47% | 1.47 | +4,836 |
| 2013-12 | 18 | 44% | 1.98 | +4,202 |
| 2014-01 | 43 | 42% | 0.95 | -469 |
| 2014-02 | 14 | 21% | 0.20 | -3,966 |
| 2014-03 | 42 | 57% | 1.77 | +10,083 |
| 2014-04 | 4 | 0% | 0.00 | -996 |
| 2014-05 | 7 | 71% | 23.32 | +3,386 |
| 2014-06 | 19 | 53% | 5.50 | +15,314 |
| 2014-07 | 18 | 44% | 1.98 | +3,503 |
| 2014-08 | 2 | 50% | 0.02 | -660 |
| 2014-09 | 13 | 31% | 0.71 | -1,695 |
| 2014-10 | 1 | 100% | inf | +5 |
| 2014-11 | 18 | 72% | 15.50 | +6,779 |
| 2014-12 | 12 | 50% | 0.90 | -338 |
| 2015-01 | 3 | 33% | 5.65 | +1,750 |
| 2015-02 | 23 | 48% | 1.85 | +3,499 |
| 2015-03 | 30 | 57% | 5.54 | +18,476 |
| 2015-04 | 22 | 55% | 1.01 | +53 |
| 2015-05 | 15 | 40% | 1.68 | +1,216 |
| 2015-06 | 12 | 58% | 3.41 | +3,057 |
| 2015-07 | 8 | 50% | 1.53 | +961 |
| 2015-08 | 1 | 0% | 0.00 | -731 |
| 2015-09 | 1 | 0% | inf | +0 |
| 2015-10 | 4 | 50% | 3.00 | +1,253 |
| 2015-11 | 21 | 52% | 1.82 | +4,198 |
| 2015-12 | 5 | 80% | 12.26 | +3,919 |
| 2016-01 | 2 | 50% | 0.74 | -266 |
| 2016-02 | 1 | 0% | 0.00 | -21 |
| 2016-03 | 8 | 25% | 0.81 | -602 |
| 2016-04 | 6 | 50% | 1.80 | +401 |
| 2016-05 | 10 | 40% | 2.57 | +1,895 |
| 2016-06 | 7 | 29% | 0.31 | -1,140 |
| 2016-07 | 2 | 50% | 0.32 | -182 |
| 2016-08 | 50 | 42% | 2.44 | +8,249 |
| 2016-09 | 15 | 53% | 2.62 | +3,580 |
| 2016-10 | 15 | 60% | 2.21 | +2,771 |
| 2016-11 | 5 | 60% | 2.04 | +1,980 |
| 2016-12 | 29 | 45% | 1.49 | +1,628 |
| 2017-01 | 17 | 35% | 0.97 | -81 |
| 2017-02 | 29 | 48% | 1.34 | +1,686 |
| 2017-03 | 36 | 58% | 1.39 | +3,731 |
| 2017-04 | 10 | 20% | 0.74 | -911 |
| 2017-05 | 29 | 66% | 3.69 | +14,008 |
| 2017-06 | 18 | 61% | 3.49 | +5,944 |
| 2017-07 | 12 | 42% | 0.47 | -1,724 |
| 2017-08 | 16 | 44% | 0.86 | -504 |
| 2017-09 | 17 | 29% | 0.13 | -5,080 |
| 2017-10 | 32 | 44% | 2.13 | +7,584 |
| 2017-11 | 10 | 40% | 0.30 | -2,228 |
| 2017-12 | 19 | 26% | 0.22 | -8,011 |
| 2018-01 | 16 | 62% | 2.81 | +6,091 |
| 2018-02 | 14 | 36% | 0.48 | -2,821 |
| 2018-03 | 21 | 38% | 6.92 | +35,274 |
| 2018-04 | 4 | 50% | 0.38 | -446 |
| 2018-05 | 21 | 43% | 0.56 | -3,493 |
| 2018-06 | 41 | 51% | 2.31 | +10,987 |
| 2018-07 | 24 | 42% | 1.01 | +30 |
| 2018-08 | 28 | 61% | 2.37 | +4,309 |
| 2018-09 | 23 | 70% | 4.47 | +8,110 |
| 2018-10 | 3 | 0% | 0.00 | -575 |
| 2018-11 | 10 | 60% | 1.22 | +440 |
| 2018-12 | 5 | 20% | 0.13 | -1,876 |
| 2019-01 | 1 | 0% | inf | +0 |
| 2019-02 | 16 | 81% | 2.67 | +4,863 |
| 2019-03 | 22 | 50% | 3.22 | +18,337 |
| 2019-04 | 6 | 50% | 2.43 | +1,452 |
| 2019-05 | 14 | 57% | 5.49 | +7,897 |
| 2019-06 | 2 | 50% | 3.15 | +665 |
| 2019-07 | 21 | 48% | 3.00 | +9,223 |
| 2019-08 | 15 | 13% | 0.23 | -2,898 |
| 2019-09 | 8 | 12% | 0.03 | -4,673 |
| 2019-10 | 4 | 25% | 0.08 | -304 |
| 2019-11 | 21 | 48% | 0.77 | -1,290 |
| 2019-12 | 26 | 69% | 2.68 | +7,995 |
| 2020-01 | 20 | 40% | 1.03 | +117 |
| 2020-02 | 22 | 14% | 0.34 | -7,659 |
| 2020-03 | 3 | 33% | 0.32 | -785 |
| 2020-04 | 3 | 0% | 0.00 | -1,043 |
| 2020-05 | 13 | 62% | 3.19 | +13,740 |
| 2020-06 | 14 | 36% | 0.52 | -3,250 |
| 2020-07 | 7 | 71% | 3.39 | +3,767 |
| 2020-08 | 24 | 33% | 1.01 | +137 |
| 2020-09 | 8 | 62% | 6.52 | +8,278 |
| 2020-10 | 14 | 36% | 0.30 | -4,175 |
| 2020-11 | 12 | 25% | 0.37 | -3,824 |
| 2020-12 | 60 | 45% | 1.40 | +13,022 |
| 2021-01 | 57 | 56% | 22.49 | +403,130 |
| 2021-02 | 80 | 35% | 1.64 | +48,481 |
| 2021-03 | 28 | 39% | 1.19 | +2,526 |
| 2021-04 | 14 | 29% | 0.19 | -4,341 |
| 2021-05 | 11 | 55% | 0.92 | -131 |
| 2021-06 | 30 | 23% | 0.16 | -13,897 |
| 2021-07 | 9 | 67% | 4.22 | +3,078 |
| 2021-08 | 14 | 50% | 0.50 | -1,130 |
| 2021-09 | 16 | 50% | 3.31 | +8,625 |
| 2021-10 | 3 | 0% | 0.00 | -2,076 |
| 2021-11 | 34 | 35% | 0.81 | -3,442 |
| 2021-12 | 5 | 80% | 14.38 | +8,455 |
| 2022-01 | 3 | 67% | 0.44 | -570 |
| 2022-02 | 2 | 50% | 0.76 | -399 |
| 2022-03 | 3 | 33% | 0.11 | -2,452 |
| 2022-04 | 7 | 43% | 7.20 | +4,819 |
| 2022-06 | 3 | 33% | 0.27 | -944 |
| 2022-07 | 1 | 100% | inf | +74 |
| 2022-08 | 14 | 36% | 0.80 | -2,188 |
| 2022-09 | 3 | 33% | 1.19 | +159 |
| 2022-11 | 4 | 25% | 0.03 | -2,279 |
| 2022-12 | 9 | 44% | 5.38 | +6,271 |
| 2023-01 | 4 | 25% | 0.14 | -1,515 |
| 2023-02 | 6 | 33% | 0.85 | -378 |
| 2023-03 | 2 | 50% | 8.47 | +756 |
| 2023-04 | 4 | 50% | 0.97 | -24 |
| 2023-05 | 4 | 25% | 0.61 | -378 |
| 2023-06 | 14 | 57% | 2.56 | +5,802 |
| 2023-07 | 10 | 70% | 4.22 | +4,444 |
| 2023-08 | 14 | 36% | 0.28 | -4,312 |
| 2023-09 | 4 | 75% | 0.13 | -1,451 |
| 2023-11 | 6 | 33% | 0.43 | -1,603 |
| 2023-12 | 16 | 62% | 1.33 | +2,593 |
| 2024-01 | 16 | 69% | 6.46 | +10,883 |
| 2024-02 | 17 | 29% | 0.53 | -3,475 |
| 2024-03 | 42 | 45% | 1.68 | +7,114 |
| 2024-04 | 11 | 73% | 15.51 | +7,172 |
| 2024-05 | 9 | 33% | 0.38 | -1,900 |
| 2024-06 | 2 | 100% | inf | +1,794 |
| 2024-07 | 1 | 100% | inf | +64 |
| 2024-08 | 8 | 38% | 0.64 | -1,028 |
| 2024-09 | 9 | 44% | 1.64 | +1,170 |
| 2024-10 | 16 | 31% | 0.70 | -2,582 |
| 2024-11 | 38 | 21% | 0.69 | -2,715 |
| 2024-12 | 11 | 55% | 0.64 | -2,131 |
| 2025-01 | 3 | 67% | 0.76 | -368 |
| 2025-02 | 23 | 39% | 0.48 | -3,444 |
| 2025-03 | 1 | 0% | 0.00 | -604 |
| 2025-05 | 12 | 50% | 0.96 | -221 |
| 2025-06 | 20 | 50% | 2.07 | +6,305 |
| 2025-07 | 18 | 44% | 0.91 | -978 |
| 2025-08 | 9 | 22% | 1.29 | +764 |
| 2025-09 | 32 | 44% | 0.95 | -944 |
| 2025-10 | 12 | 33% | 3.71 | +10,236 |
| 2025-11 | 4 | 0% | 0.00 | -1,542 |
| 2025-12 | 18 | 33% | 0.97 | -245 |
| 2026-01 | 15 | 67% | 6.33 | +6,629 |
| 2026-02 | 16 | 38% | 0.63 | -1,630 |
| 2026-03 | 9 | 56% | 16.31 | +26,942 |
| 2026-04 | 5 | 40% | 0.78 | -443 |
| 2026-05 | 21 | 48% | 0.92 | -1,422 |

</details>

## Reproduction

```bash
# default (stop-window 4, baseline exit: cap=0 = sell next open), full warmup from dataset start
dotnet run --project TradingEdge.MomentumV1 -c Release -- \
  --start-date 2003-09-10 --end-date 2026-05-13 \
  --stop-low-window 4 --exit-time-cap 0 -o /tmp/v1.csv
# then apply breadth_lag1 > 0.5 and the 2005 entry cutoff post-hoc (LAG(pct_above_20) by 1
# trading day on data/equity/momentum_v0/breadth.parquet), as in the sweep analysis.
```

## Known post-parity TODOs (in code)

- **Hard up-only stop ratchet** — the stop currently recomputes `max(prior-window low, entry-day
  low)` each bar and can tick down if a lower low slides into the window. A true Qulla trailing
  stop never loosens.
- **ATR% denominator** — divides by the current bar's close, which a big breakout deflates. Switch
  to the prior close.


---

<a id="v2"></a>

# Mid-Cap Momentum v2 — Log-Space Volatility Filters

> **📚 ARCHIVE (as of 2026-06-21).** The **current** production state and all research under the clipped /
> cumulative methodology live in [`momentum_v3_results.md`](momentum_v3_results.md). This v2 document is the
> full historical research record — exit-mechanic sweeps, 52w-proximity studies, loose-base shorts,
> regime-switching, chandelier/time-stop derivation — kept intact for reference. Where a v2 finding was
> re-derived under the clip (notably the ATR% ceiling, now **0.10** not 0.11), v3 is authoritative.

**Status: working long-only daily-momentum edge, PF ~1.77 post-breadth, on honest next-open fills.**
This is the current production system. It supersedes both `momentum_v0` (whose mean-reversion
trailing-limit results were inflated by a fill bug) and the v1 *exit*-correction work, by re-deriving
the **entry** filters on a log-volatility scale and dropping the trailing-limit / expansion exits in
favour of a plain next-open exit.

`TradingEdge.HighFlyer` is a ground-up F# rewrite: all indicators computed in a single in-memory
pass (no SQL window functions), one `QullaSystem` per ticker. A full 22-year scan runs in **~15s**,
so the parameter sweeps below are minutes, not hours.

---

## The system in one screen

Long-only daily momentum on US common stocks / ADRs (`ticker_reference.type IN ('CS','ADRC')`),
2005–2026. One position per breakout signal, fixed $10k notional, uncapped concurrency, no
compounding (so net P&L is a raw edge-and-breadth measure, not an achievable equity curve).

**Entry** — on a daily bar, go long at the close when ALL hold (each indicator uses *prior* bars,
no lookahead):

| gate | threshold | meaning |
| --- | --- | --- |
| entry-day move | **≥ 10%** | `close/prevClose − 1` — the breakout has to *announce itself* |
| relative volume | **rvol ≥ 5** (no upper cap) | `volume / 28-day avg volume`. Floor 6→5 on 2026-06-20 (rvol 5 is enough to be significant). The upper cap was *removed* — the 30%-move cap below handles the blow-off tail instead |
| entry-day move | **10% ≤ move < 30%** | `close/prevClose − 1`. The 30% cap (added 2026-06-20) removes the single-day exhaustion blow-off; it makes an rvol cap redundant |
| ATR% (log) | **< 0.11** | mean log-true-range over 14 prior bars (see below) |
| tightness (linear) | **< 4.5** | `(14d range) / ATR` — prior consolidation must be tight (linear; sharper loose-tail cut than log). Raised 4.0 → 4.5 on 2026-06-20 (clean capacity gain; < 4.0 = max-PF, < 5.5 = max-capacity alternatives) |
| 52-week proximity | **close ≥ 0.95 × hi_252** | near the 1-year closing high |
| price floor | **≥ $5** | no sub-$5 names |
| liquidity | **avg dollar volume ≥ $100k** | 28-day average |
| breadth (market-wide) | **`pct_above_20` lagged 1 day > 0.5** | applied post-hoc; risk-on regime only |

**Exit** — a Qullamaggie-style trailing stop: floor = `max(min-low over prior 4 bars, entry-day
low)`. When the bar's low breaches it, **sell at the next bar's open**. No trailing limit, no
time stop, no expansion exit. Open positions at the data's end are marked-to-market at the final
close.

**Headline (filtered: breadth + 2005-start, 21.4 trading-day-years):**

| | value |
| --- | ---: |
| trips | 2,227 |
| win rate | 46.4% |
| profit factor | **1.769** |
| net P&L | +$527,594 |
| % months positive | 58.1% |
| max monthly drawdown | **−$33,772** |
| years positive | **21 / 22** |

*(Headline recomputed 2026-06-18 on the dividend-adjustment-fixed `split_adjusted_prices` — see the
data-fix note below. The change vs the pre-fix numbers was negligible: PF 1.758→1.769, DD −37.6k→
−33.8k, 26 fewer trips — the production filters mostly exclude the low-priced dividend payers that
the bug corrupted.)*

---

## Yearly breakdown — PRE-time-stop default (window-low stop-4), filtered (flat $10k/trip, by entry year)

> **Which system:** this is the production default *as it stood before the 2026-06-19 stop-mechanics
> rework* — **window-low trailing stop (stop-window 4), next-open exit, expansion off, ATR% < 0.11,
> log-tightness < 4.0, entry-move ≥ 10%**, + breadth (lag-1 pct_above_20 > 0.5) + entry ≥ 2005. It is the
> `≥ 10%` headline row above (**2,260 trips, PF 1.734, +$520,641, 58.7% positive months**). It is **not**
> the current default (5-day time-stop, no price stop, rvol [5,15] — see "The system in one screen"); the
> time-stop system's own yearly/monthly breakdown has not yet been generated. Kept here as the historical
> reference for the system's year-by-year robustness.

| year | trips | win% | PF | net |
| ---: | ---: | ---: | ---: | ---: |
| 2005 | 100 | 50% | 1.38 | +9,277 |
| 2006 | 130 | 39% | 1.61 | +17,275 |
| 2007 | 109 | 43% | 1.87 | +24,381 |
| 2008 | 34 | 59% | 1.73 | +8,161 |
| 2009 | 51 | 53% | 3.71 | +27,346 |
| 2010 | 123 | 53% | 2.37 | +41,138 |
| 2011 | 82 | 43% | 1.42 | +9,226 |
| 2012 | 73 | 52% | 3.19 | +29,421 |
| 2013 | 181 | 47% | 1.69 | +34,559 |
| 2014 | 107 | 50% | 1.65 | +18,738 |
| 2015 | 86 | 51% | 2.45 | +27,830 |
| 2016 | 88 | 43% | 1.81 | +11,748 |
| 2017 | 138 | 46% | 1.35 | +13,744 |
| 2018 | 127 | 54% | 1.73 | +23,002 |
| 2019 | 93 | 49% | 4.80 | +103,596 |
| 2020 | 149 | 42% | 1.81 | +57,348 |
| 2021 | 191 | 36% | 1.09 | +9,529 |
| 2022 | 33 | 45% | 1.34 | +5,329 |
| 2023 | 71 | 51% | 1.59 | +12,368 |
| 2024 | 127 | 40% | 1.21 | +9,694 |
| 2025 | 119 | 39% | 0.97 | −1,570 |
| 2026 | 48 | 46% | 2.75 | +28,501 |

**21 of 22 years positive** (only 2025 fractionally red at −$1.6k). Crucially, the edge is now
*spread across years* rather than concentrated: 2021's COVID-bubble names — which dominated the old
system — are largely filtered out by the tight ATR%/tightness gates (2021 is only +$9.5k here),
and 2020 is solidly positive (+$57k) on the next-open exits getting clear of the March crash. The
two biggest years (2019 +$104k, 2020 +$57k) are real momentum regimes, not a single blow-off.

<details>
<summary>Full monthly breakdown — net P&L by year × month ($k), flat sizing, by entry month — click to expand</summary>

| year | Jan | Feb | Mar | Apr | May | Jun | Jul | Aug | Sep | Oct | Nov | Dec | **year** |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2005 | 747 | -2.5k | -876 | · | 796 | 3.2k | 9.3k | 97 | 702 | -378 | -1.1k | -632 | **9.3k** |
| 2006 | 150 | 584 | 6.0k | 2.9k | -6.4k | -1.4k | 96 | 1.5k | 49 | 2.6k | 7.0k | 4.2k | **17.3k** |
| 2007 | 1.4k | 4.8k | 1.7k | 10.5k | -2.1k | -1.3k | -2.9k | 213 | 15.7k | -2.2k | -620 | -896 | **24.4k** |
| 2008 | · | 969 | -1.0k | 3.0k | 10.5k | 1.7k | -2.2k | -4.6k | · | · | -429 | 246 | **8.2k** |
| 2009 | -593 | · | · | · | 4.2k | 297 | -346 | 16.9k | 691 | 475 | 4.8k | 882 | **27.3k** |
| 2010 | 418 | 4.9k | 13.7k | -2.4k | -1.1k | · | -94 | 2.8k | 5.5k | 145 | 13.7k | 3.6k | **41.1k** |
| 2011 | -5 | 7.6k | -1.0k | 3.9k | -371 | · | 320 | · | -706 | · | -1.1k | 631 | **9.2k** |
| 2012 | 6.9k | 1.8k | 3 | -3.2k | -711 | 13.7k | 266 | 6.7k | 2.5k | -616 | -105 | 2.2k | **29.4k** |
| 2013 | -864 | 3.5k | -390 | 3.5k | 13.7k | · | 5.5k | 632 | 681 | 8.3k | -177 | 204 | **34.6k** |
| 2014 | -1.2k | 4.3k | -1.3k | 956 | 6.4k | 7.7k | 63 | -1.4k | -1.7k | 5.1k | 2.5k | -2.7k | **18.7k** |
| 2015 | 15.4k | 7.6k | -899 | 481 | 980 | 536 | · | -1.2k | · | 4.8k | 1.4k | -1.3k | **27.8k** |
| 2016 | · | -36 | 15 | -559 | 837 | -594 | 4.0k | 9.4k | 474 | -1.0k | 42 | -803 | **11.7k** |
| 2017 | -495 | 2.5k | 3.6k | 7.4k | 3.9k | 3.5k | -45 | -802 | -3.2k | 3.8k | -3.3k | -3.3k | **13.7k** |
| 2018 | 359 | · | 277 | 4.2k | 9.2k | -3.7k | 7.0k | 7.0k | 428 | · | 1.8k | -3.6k | **23.0k** |
| 2019 | 2.7k | 99.6k | -2.9k | 6.2k | -35 | 954 | -2.2k | · | -1.4k | -1.1k | 7.1k | -5.4k | **103.6k** |
| 2020 | 2.7k | -12.9k | · | 1.5k | 8.6k | 4.8k | 2.3k | -9.5k | · | -280 | -354 | 60.5k | **57.3k** |
| 2021 | 32.3k | -24.6k | -3.0k | -4.1k | 3.8k | -7.5k | -357 | 15.5k | 1.2k | -95 | -3.1k | -431 | **9.5k** |
| 2022 | · | -665 | 2.2k | · | -1.3k | 596 | 1.0k | -338 | · | -276 | 2.6k | 1.5k | **5.3k** |
| 2023 | -1.1k | -583 | · | 170 | 1.3k | 7.1k | -4.0k | -1.1k | 364 | · | 2.4k | 7.8k | **12.4k** |
| 2024 | 5.1k | 3.1k | -1.4k | 3.9k | 272 | · | -744 | 51 | -1.2k | 6.0k | -3.7k | -1.6k | **9.7k** |
| 2025 | -1.1k | -2.5k | · | 26 | 380 | 4.8k | -5.6k | -714 | 12.8k | -7.3k | -171 | -2.0k | **-1.6k** |
| 2026 | 28.8k | -2.1k | -1.0k | 1.5k | 1.3k | · | · | · | · | · | · | · | **28.5k** |

`·` = no trades that month. 135 / 230 months positive (58.7%); worst month −$24.6k (Feb 2021,
the post-blow-off unwind); best month +$99.6k (Feb 2019). Outlier months are upside (Feb 2019,
Dec 2020 +$60k, Jan 2021/2026 ~+$30k), not catastrophic downside — the tight entry gate keeps the
left tail shallow.

</details>

---

## Why log-space ATR / tightness (the v2 change)

The v0/v1 ATR% was the prior-14-day average true range divided by the **current** bar's close.
That denominator is wrong for a momentum entry: on a big breakout day the close jumps, which
**deflates** the ATR% — so a genuinely volatile name can slip *under* an `ATR% < 0.08` filter
purely because its trigger bar ran up. The filter was meant to reject jumpy names and was being
defeated by exactly the bars it should catch.

**The fix:** compute true range in **log space**. Each leg becomes a log-price difference (a
log-return magnitude):

```
logTR = max( log(high)−log(low),
             |log(high)−log(prevClose)|,
             |log(low) −log(prevClose)| )
```

The 14-bar average of `logTR` *is* an ATR% — intrinsically a per-bar percentage-of-price — with no
division by any close. A breakout day no longer distorts it; it measures the bar's volatility on
its own scale regardless of where the close landed. Tightness follows the same logic:
`tightness = log(maxHigh/minLow) / logATR` (no `× window` factor — ATR is already a per-bar
average and the threshold is set by sweep, so the span constant is redundant).

This moved both filters onto a new numeric scale (live ATR% ≈ 0.003–0.5, median ~0.04; live
tightness ≈ 1.4–13, median ~3.9), so every old cutoff (`0.08`, `0.30`) was meaningless and had to
be re-swept from scratch.

---

## Tuning — post-hoc sweeps on the realistic (next-open) baseline

All sweeps below run the engine with the relevant filter **off**, then carve thresholds in SQL off
the trips CSV (breadth_lag1 > 0.5, entries ≥ 2005). Because the entry filters are entry-time gates,
post-hoc cutting is exact; the exit choice (next-open) was fixed first so nothing is tuned against
an unrealistic fill.

### ATR% (log) — the single biggest lever

The high-volatility tail is the drag, exactly as the broken denominator was hiding. Cutting it
lifts PF from 1.13 → ~1.47:

| ATR% cut | trips | PF | net |
| ---: | ---: | ---: | ---: |
| none | 6922 | 1.146 | 451,908 |
| < 0.09 | 6033 | 1.470 | 868,426 |
| **< 0.11** | 6331 | **1.467** | **970,707** |
| < 0.12 | 6424 | 1.429 | 936,091 |
| < 0.15 | — | lower | — |

Flat plateau across 0.09–0.11, falling above 0.12. **0.11** sits at the top of the plateau and
maxes P&L. Clear-cut.

### Tightness (log) — monotonic; tighter = better on every axis

Unlike the old absolute formula (where the relationship was muddied), log-tightness is cleanly
monotonic. Tighter caps raise PF *and* shrink drawdown *and* lift monthly consistency — only raw
P&L falls (fewer, better trades = less exposure):

| tightness cut | trips | PF | net | % months + | worst mo | max DD |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| < 3.5 | 2342 | 1.701 | 486,592 | 59.9% | −19.0k | −32.4k |
| **< 4.0** ⭐ | 3357 | 1.636 | 627,079 | 57.2% | −23.9k | **−40.8k** |
| < 4.5 | 4187 | 1.575 | 705,646 | 59.0% | −25.5k | −43.8k |
| < 5.0 | 4836 | 1.549 | 792,685 | 59.1% | −33.2k | −63.9k |
| none | 6331 | 1.467 | 970,707 | 57.2% | −42.8k | −95.4k |

**4.0** is the drawdown/PF sweet spot — it halves the max drawdown vs the loosest sane setting
while keeping PF at 1.64 and most of the P&L. (Numbers here are at the pre-pct_up-floor stage; the
final default adds the 10% entry-move floor on top, lifting filtered PF to 1.73.)

### Entry-day move — the surprise: bigger movers are *better*

Hypothesis going in was that stocks "up too much" on the day are bad. The data says the **opposite**
— the weak band is the *modest* movers (~8–11%, PF ~1.15); the explosive ≥20% names are the
strongest (decile 9, up 22–29%, PF **2.33**). Capping the top does nothing for PF and only discards
P&L. **Raising the floor**, by contrast, improves everything:

| move floor | trips | PF | net | % months + | worst mo | max DD |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| ≥ 5% (old) | 3357 | 1.636 | 627,079 | 57.2% | −23.9k | −40.8k |
| ≥ 8% | 2683 | 1.660 | 553,963 | 56.7% | −24.8k | −38.9k |
| **≥ 10%** ⭐ | 2260 | 1.734 | 520,641 | 58.7% | −24.6k | −35.8k |
| ≥ 12% | 1897 | 1.776 | 479,246 | 58.4% | −20.8k | −28.3k |
| ≥ 15% | 1401 | 1.858 | 420,585 | 60.1% | −18.0k | −26.4k |

PF, consistency, worst month and drawdown all improve as the floor rises — a rare dial that pays on
quality *and* risk at once. **10%** is the chosen balance: PF 1.73 at meaningful trade count;
higher floors keep paying if you want fewer/stronger trades.

#### …but most of the modest-move edge is really 52w-high proximity (2026-06-18)

The entry-day move and the 52-week-high gate are correlated (a big up-day is often *what makes* a new
high), so is "up≥10% > up≥5%" really a move effect, or a proxy for proximity? Test: require a **strict
new closing high** (52w = 1.0) and drop the move floor (up≥0), then bucket by the day's move *within*
that new-high population (15-day stop, tightness<4, rvol[6,20], breadth):

| pct_up band (all at a new high) | n | PF |
| --- | ---: | ---: |
| <3% | 937 | 1.576 |
| 3–5% | 330 | 1.322 |
| 5–8% | 448 | 1.537 |
| 8–10% | 322 | 1.158 |
| 10–15% | 710 | 1.311 |
| **15–20%** | 501 | **2.085** |
| **20%+** | 729 | **1.708** |

**Verdict: partly a proxy, but not entirely.** In the 3–15% range the move size barely matters once a
new high is required (flat, choppy PF 1.16–1.58) — so much of the basic up≥10%-vs-up≥5% effect *was*
the new-high edge. But the **explosive ≥15% moves carry real, independent signal** (PF 2.0+) even
among new-high names. Move floor and 52w gate are complementary, not redundant: the floor earns its
keep specifically by isolating the big-mover tail. (And with strict new-high, raising the move floor
still lifts PF 1.58→1.83 at ≥15%, for the same reason.)

#### Distance above the 52-week high — bimodal, not "closer = better" (2026-06-18)

Bucketing by **how far the close sits above the prior-252d closing high** (`pct_52w_at_entry =
close / hi_252_prior − 1`; 52w gate OFF so it spans both sides; up≥10, tightness<4, rvol[6,20],
15-day stop, breadth):

| distance vs 52w high | n | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| < −15% (well below) | 2333 | 28.5% | 1.214 | +384k |
| −15..−5% | 635 | 36.1% | 1.286 | +96k |
| −5..0% (just under) | 313 | 42.8% | 1.706 | +86k |
| **0..2% (fresh new high)** | 158 | 46.2% | **1.939** | +61k |
| 2..5% (dead zone) | 266 | 38.7% | **1.184** | +22k |
| 5..10% | 495 | 37.6% | 1.333 | +75k |
| **10..20% (extended run)** | 756 | 43.7% | **2.014** | +330k |
| 20%+ | 265 | 38.9% | 1.695 | +130k |

The signal is **bimodal**, with two distinct sweet spots and a trough between them:

1. **Right at the high (0–2%): PF 1.94** — the clean fresh breakout, highest win rate (46%).
2. **Extended 10–20% above: PF 2.01** — names already in a confirmed strong run; biggest P&L
   contributor ($330k).
3. A **dead zone at 2–5% (PF 1.18)** — past the clean breakout but not yet a confirmed run.

Names *well below* the high (< −15%, the early-stage breakouts) are the weakest on PF (1.21) but carry
a lot of P&L on volume. This refines the proximity story: it is not "closer to the high is better" —
it is "at the high OR clearly extended, but not the awkward 2–5% just-past zone."

#### The move × 52w-position interaction — "bigger is better" is conditional (2026-06-18, pure gainers)

Re-run on **pure gainers** (up≥0, no move floor — the up≥10 default had hidden the small-mover cells)
the bimodal 52w shape holds (peak at the new high, dead 2–10% trough, strong 10–20% extended). But
slicing the **dead 2–10% zone by entry-day move** overturns the earlier "not rescuable by move"
read — there IS a pattern, and it **inverts** the big-mover edge:

| pct_up (within 2–10% above high) | n | PF |
| --- | ---: | ---: |
| 2–4% | 151 | **2.03** |
| 4–6% | 216 | 1.372 |
| 6–8% | 224 | 1.275 |
| 8–10% | 271 | **1.094** |
| 14–20% | 235 | 1.528 |

The **smallest movers (2–4%) are the best** in this zone (PF 2.0), declining to a trough at 8–14%.
Mechanism: a stock 2–10% above its high on a *2–4% day* got there by **grinding** (quiet, steady
higher highs) — a clean continuation. A stock only 2–10% above the high on a *10%+ day* just **gapped
up to barely clear it** — overextended intraday relative to its structure, and underperforms.

So the move×position interaction is the real story (PF, cells with n≥40, pure gainers):

| 52w zone \ move | <5% | 5–10% | 10–15% | 15–20% | 20%+ |
| --- | ---: | ---: | ---: | ---: | ---: |
| below high | 1.53 | 1.23 | 0.81 | 1.06 | 1.64 |
| at-high 0–2% | 1.34 | **2.05** | 1.23 | **3.20** | — |
| dead 2–10% | **1.74** | 1.21 | 1.10 | 1.80 | 1.20 |
| ext 10–20% | — | — | 1.83 | **2.15** | 2.00 |
| ext 20%+ | — | — | — | — | 1.70 |

**"Bigger movers are better" is conditional on position:** explosive at a *fresh* high or in a
*confirmed run* is premium (PF 2–3); the *same* big move in the dead zone is mediocre (~1.1–1.2),
where instead the quiet small-mover grinder wins. (Grid is noisy per-cell at these sample sizes —
read it for the pattern, not the decimals.)

#### Is the dead zone a resistance artifact? (max-CLOSE vs max-HIGH 52w reference)

The 52w channel is `max(close)`, but the resistance a breakout must clear is the prior **intraday
high** (`max(high)`), which sits above the max-close. So a name "+2–10% above its 52w *close* high"
may still be **at/under its prior intraday high** — banging into real overhead. Added a parallel
252d high-channel and `pct_52w_high_at_entry = close / hi_252_high − 1`. Bucketing on it (pure
gainers, breadth, 15-day stop):

| band (vs 52w INTRADAY high) | n | PF | net |
| --- | ---: | ---: | ---: |
| < −15% (well below) | 7253 | 1.307 | +1.06M |
| −15..−5% | 3618 | 1.513 | +439k |
| **−5..0% (pressing into resistance)** | 3445 | **1.646** | +371k |
| 0..2% (clears high) | 870 | 1.403 | +97k |
| 2..5% | 660 | 1.351 | +86k |
| 5..10% | 683 | 1.357 | +108k |
| **10..20% (extended)** | 568 | **1.757** | +176k |
| 20%+ | 182 | 1.473 | +65k |

**Partly confirmed, with a sharper read.** The median intraday-high distance is ~1.3% below the
close-high distance, and **5.9% of trades are a new *closing* high yet still under the prior intraday
high.** On the intraday-high reference the dead zone is milder (the close-based +2–10% trough was
partly a resistance artifact) — but the standout flips to **−5..0%, i.e. *just below* the intraday
high (PF 1.65).** The best fresh-breakout setup is price *pressing into* intraday resistance from
below, not after it clears; once it poking through (0–10% above the intraday high) it enters the mild
dead zone, and only the well-extended 10–20% names are strong again. The close-based "fresh high"
sweet spot was conflating these two; the intraday-high reference cleanly separates them.

#### Rescuing the 0–10% dead zone with CLOSE-IN-RANGE — weak signal; the real effect is "don't pin the high" (2026-06-18)

Can the 0–10%-above-intraday-high dead zone be salvaged by *how the entry bar closes*? Close-in-range
`= (close − low)/(high − low)` (1.0 = closed at the day's high, 0 = at the low; from raw
`daily_prices` — a within-bar ratio, so adjustment cancels). **PURE GAINERS (up≥0)**, production
quality filters (tight<4, ATR%<0.11, rvol[6,20]), breadth, 2005+, 2,244 trips (total PF 1.29):

| close-in-range | n | PF | | era split | 2005–14 | 2015–26 |
| --- | ---: | ---: | --- | --- | ---: | ---: |
| 0.0–0.2 | 123 | 1.27 | | weak/mid <0.6 | 1.35 | 1.32 |
| 0.2–0.4 | 194 | 1.51 | | strong 0.6–0.95 | 1.54 | 1.24 |
| 0.4–0.6 (mid) | 328 | 1.26 | | **at-high 0.95+** | 1.23 | **0.65** |
| 0.6–0.8 | 598 | 1.34 | | | | |
| 0.8–0.95 | 676 | 1.39 | | | | |
| **0.95–1.0 (at high)** | 325 | **0.88** | | | | |

⚠️ **Close-in-range is a WEAK signal once the move floor is removed.** An earlier cut with up≥10%
showed strong-close at PF 1.84 — but that was **the move floor leaking back in**, not the close
location. On *all* gainers the strong-close bands (0.6–0.95) are only ~1.34–1.39, barely above the
weak/mid closers (1.26–1.51), and there's no clean monotonic "stronger close = better" (the 0.2–0.4
bucket is actually the best small one). The strong-close group also fades across eras (1.54 → 1.24).

**The one robust, actionable effect is the NEGATIVE one: closing at the literal high (0.95+) is bad**
— PF 0.88 overall, and clearly era-damning (1.23 → **0.65**, net-losing recently). Pinning the close
to the absolute high is an exhaustion tell (mirrors the bimodal distance-above-high "pinned = no
buyers left above" pattern). So close-in-range does NOT meaningfully rescue the dead zone — the
takeaway is to **avoid the pinned-at-the-high closes**, not to chase strong closes.

#### …but the INTRADAY RECLAIM does rescue it — open below resistance, close through it (2026-06-18)

The real dead-zone refinement: did the entry bar **open below** the 52w intraday high (resistance)
and push **up through it** to close above (a live intraday reclaim), or did it **gap over** and just
hold (already-extended, you missed the break)? Reconstruct resistance = `hi_252_high =
entry_price/(1+pct_52w_high_at_entry)` and compare the (raw) open to it. Pure gainers, dead zone, 2,244 trips:

| dead-zone entry | n | win% | PF | net | | era | 2005–14 | 2015–26 |
| --- | ---: | ---: | ---: | ---: | --- | --- | ---: | ---: |
| **reclaim (open below → close above)** | 1142 | 44.6% | **1.43** | +137k | | reclaim | **1.69** | **1.26** |
| gap-over (open at/above) | 1102 | 39.4% | 1.09 | +19k | | gap-over | 1.15 | 1.04 |

**The intraday reclaim is far better and era-robust** — PF 1.43 (1.69 → 1.26 across eras), higher win
rate, and it carries essentially ALL the dead zone's P&L ($137k of $156k). The gap-over half is the
genuinely dead part (PF 1.09, near break-even both eras). Mechanism: a name that opens below its 52w
high and reclaims it through the session is a *live* breakout you're participating in; a gap-over is
one that broke before the open — you're buying it already extended, after the fact. Unlike
close-in-range (which evaporated on pure gainers), the reclaim-vs-gap split survives the era test
cleanly. **This is the dead zone's actual rescue: require an intraday reclaim of the prior high, not
a gap over it.**

Not a stop artifact: a gap-over's entry-day-low stop sits relatively tighter (open far above the
low), so the split could be stop-geometry. Re-ran with `--no-entry-day-stop` — the edge holds:
reclaim 1.435 (vs 1.43), gap-over 1.16 (vs 1.09; the stop *was* penalizing gap-overs slightly more,
a second-order effect), reclaim still wins both eras (1.65 / 1.29 vs 1.07 / 1.16). The reclaim
advantage is genuine, not a consequence of the initial stop.

#### ⭐ The reclaim edge GENERALIZES to the whole production system (2026-06-18)

> **Resistance = the prior 252-day max of INTRADAY HIGHS** (`hi_252_high`, the `hiHigh` MaxMa over
> `bar.high`, pre-push), NOT the max of closes. So **reclaim** = entry bar's `open < hi_252_high`
> **and** `close ≥ hi_252_high` (opened below the prior intraday high, pushed up through it during the
> session); **gap-over** = `open ≥ hi_252_high` (opened already above it). The intraday high is the
> real overhead level sellers defended, so a reclaim means price broke through actual supply intraday
> — the stronger of the two possible definitions. (Distinct from the production *entry gate*
> `Min52wPct`, which tests `close ≥ 0.95 × hi_252_CLOSE` — the close-channel. Two references, each
> correct for its role.)

Splitting the **full production default** (not just the dead zone) by reclaim vs gap-over — open
below the 52w intraday high and close through it, vs open already at/above it:

| whole system | n | win% | PF | net | | era | 2005–14 | 2015–26 |
| --- | ---: | ---: | ---: | ---: | --- | --- | ---: | ---: |
| **reclaim** | 1226 | 45.8% | **1.917** | +411k | | reclaim | 1.98 | **1.89** |
| gap-over | 1001 | 47.2% | 1.49 | +116k | | gap-over | 1.76 | **1.31** |

**"Open below the prior high, close through it" is a broadly better entry than buying any close** —
PF 1.92 vs 1.49, carrying **78% of the system's P&L** ($411k of $528k), steady across eras
(1.98 → 1.89). The gap-over half is the weaker one and **decaying** (1.76 → 1.31 — buying breakouts
that gapped over the high before the open has gotten worse over time, gap-and-fade / front-run).
Note the gap-over has a slightly *higher* win rate (47.2 vs 45.8) but lower PF — its winners are
smaller: the reclaim's edge is payoff size, consistent with entering at a better price than chasing
an extended gap.

**As a system filter (reclaim-only) it dominates the default:**

| | full default | reclaim-only |
| --- | ---: | ---: |
| trips | 2,227 | 1,226 |
| PF | 1.769 | **1.917** |
| net | $528k | $411k |
| max monthly DD | −$33.8k | **−$23.6k** |
| % months + | 58.1% | 53.9% |

+0.15 PF and **−30% max drawdown** for 78% of the P&L on 55% of the trips (only cost: fewer trades →
a few more flat months). Strongest single entry refinement found this session, era-robust, with a
sound mechanism (participate in a live intraday breakout vs chase an already-gapped one). **Candidate
production change** — would need an intraday/open data feed at entry time to act on live, but the
daily `open` + reconstructed `hi_252_high` is enough to backtest it cleanly.

#### ⭐ Trailing LIMIT entries rescue the dead zone — buy the pullback, not the close (2026-06-18)

The natural follow-on to the reclaim finding: instead of buying the signal-bar **close**, rest a
**buy limit at the trailing prior-window low** (the same `MinMa` the stop trails), drag it down each
bar, and fill only on a pullback to it; if it doesn't fill within a time cap, enter at the next open
(tagged `open_after_cap` so the forced-open fills are analyzable separately). This is a real resting
order that can miss — the mirror of the retired trailing-limit *exit*. Implemented in the engine as a
`PendingLimit` lifecycle (CLI: `--entry-limit --entry-trail-window N --entry-time-cap K`); the
`*_at_entry` metrics stay fixed at the **signal** bar, `entry_date`/`entry_price`/`entry_reason`
record the actual fill. A buy limit at `lvl` fills at `min(lvl, open)` (a gap-down opening below the
limit fills at the open, no top-tick credit). The CSV now carries `signal_date`, `entry_date`,
`entry_reason` (`close` | `limit` | `open_after_cap`).

**On the WHOLE production system limit entries LOSE** (breadth-filtered, default up≥10%, tw=4/cap=5):
PF **1.758 → 1.286**, and only **22%** of signals ever pulled back to the 4-day low within 5 bars —
momentum breakouts run, they don't retrace. The 78% `open_after_cap` fills (PF 1.215) bought ~5 bars
higher into a worse base; even the 456 genuine `limit` fills were PF 1.585, *below* the at-close
baseline. So for **strong, near/at-high breakouts, buying the close is correct** — chasing a pullback
either misses or buys weakness.

**But in the DEAD ZONE the opposite is true.** Re-run on pure gainers (`--up-threshold 0`) and
isolate the 0–10%-above-intraday-high band. The dead-zone **at-close** baseline is PF **1.275 /
$149.6k** (vs 1.757 for the rest of the system). Sweeping the limit params (dead-zone PF only):

| trail / cap | n | PF | net |
| --- | ---: | ---: | ---: |
| **at-close (up=0)** | **2284** | **1.275** | **$149.6k** |
| tw=1 cap=2 | 2260 | 1.199 | $74k |
| tw=1 cap=5 | 2292 | 1.326 | $109k |
| tw=1 cap=10 | 2290 | 1.39 | $128k |
| tw=2 cap=5 | 2232 | 1.258 | $86k |
| **tw=2 cap=10** | 2234 | **1.457** | $139k |
| tw=4 cap=10 | 2123 | 1.455 | $140k |

A **long cap is essential** (cap=2 is strictly worse on both axes — it forces the open too fast); with
cap=10 the dead-zone PF climbs to ~**1.45**. Unlike strong breakouts, **94.6% of dead-zone signals DO
pull back** to the 4-day low within 10 bars (only 5.4% time out) — a 0–10%-above-resistance name with
no big-move requirement is *exactly* the choppy, retracing kind that gives you the fill. Splitting the
best variant (tw=2/cap=10) by fill type:

| dead-zone fill | n | % | PF | net |
| --- | ---: | ---: | ---: | ---: |
| **limit** | 2113 | 94.6% | **1.502** | +$143k |
| open_after_cap | 121 | 5.4% | 0.759 | −$4k |

The limit fills are **PF 1.502 on the same trades** the at-close run booked at 1.275 — a genuine
+0.23 PF from entering a few % lower on the pullback. The small `open_after_cap` tail (the runaways
that never retrace) is the only loser, and it's tiny.

**Era split — robust in the right direction (the load-bearing check):**

| dead zone (0–10% above high) | 2005–14 | 2015–26 |
| --- | ---: | ---: |
| at-close PF | 1.402 | **1.175** |
| limit (tw=2, cap=10) PF | 1.422 | **1.493** |

The dead zone's weakness was concentrated in the **modern era** (2015–26 at-close PF 1.175). The
limit entry **lifts the modern era to 1.493** while leaving the early era ~unchanged (1.402→1.422) —
it repairs the exact regime where the dead zone was broken, the opposite of a regime artifact. (Early-
era P&L drops $96k→$65k because buying the close outright was already fine pre-2015; modern-era P&L is
flat-to-up, $53k→$74k, at a far higher PF.)

**Takeaway:** limit-entry is **position-dependent, mirroring everything else in this system.** For
strong near-high breakouts, buy the close (they run). For the **0–10%-above-resistance dead zone**, a
trailing buy limit with a *generous* cap (tw≈2, cap≈10) lifts PF from 1.275 to ~1.50 — because these
names reliably retrace, and the dead zone's weakness was a chase-the-close problem. This is the second
dead-zone rescue found this session (alongside the intraday reclaim); the two are complementary
(reclaim = *which* dead-zone bars to take; limit = *how* to enter them). **Candidate refinement for
dead-zone entries specifically — not the whole system.**

**The two refinements STACK — and the limit's benefit lives entirely in the reclaim half.** Splitting
the dead-zone limit run (tw=2/cap=10) by reclaim (signal-bar `adj_open < hi_252_high`) vs gap-over,
against the at-close reference:

| dead zone | at-close PF | limit PF |
| --- | ---: | ---: |
| **reclaim** (open below high) | 1.331 | **1.603** |
| gap-over (open ≥ high) | 1.14 | 1.15 |

On **reclaim** trades the limit adds a real **+0.27 PF** (1.331→1.603) at ~flat P&L — buying the
pullback on a name that opened below resistance and reclaimed it intraday is a clean improvement. On
**gap-over** trades the limit does nothing (1.14→1.15): a name that already gapped through resistance
either doesn't retrace to the 4-day low or the retrace means the gap is failing. The gap-over half is
dead weight under *both* entry methods (~$15–22k on ~870 trades). So the best dead-zone cell is
**reclaim + limit**, and gap-overs should be dropped regardless of entry style.

**DROP the timed-out (`open_after_cap`) trades — strictly dominant.** Splitting by fill type × reclaim:

| dead-zone fill | n | PF | net |
| --- | ---: | ---: | ---: |
| limit fill — reclaim | 1284 | **1.669** | +$128.3k |
| limit fill — gap-over | 829 | 1.158 | +$14.8k |
| **open_after_cap — reclaim** | 78 | **0.687** | **−$4.3k** |
| open_after_cap — gap-over | 43 | 0.987 | −$0.1k |

Both timed-out cells are sub-1.0; the reclaim ones are *especially* bad (PF 0.687) — they are exactly
the reclaim names that **failed to retrace within 10 bars** (ran away), so the cap forces you in at the
open chasing a faded breakout. Dropping all timeouts lifts the dead-zone limit run on **both axes at
once** — PF **1.457→1.502** AND net **$138.8k→$143.1k** (a rare no-tradeoff cut, because the dropped
trades are net-negative). So the correct policy is **expire-on-timeout, not enter-at-the-open**: if
the pullback doesn't come within the cap, the signal has invalidated itself.

**Best dead-zone configuration found:** `--up-threshold 0` + 0–10%-above-intraday-high + **reclaim
only** + **trailing buy limit (tw=2, cap=10) + drop timeouts** → **PF 1.669, +$128k on 1,284 trips**,
the cleanest dead-zone cell of the session. (Engine currently enters at the open on timeout, tagged
`open_after_cap`, so "drop timeouts" is a post-hoc filter today; an `--entry-expire-on-cap` flag would
make it native.)

**The limit entry is a dead-zone-ONLY tool — it damages the rest of the universe.** Splitting the
same up=0 runs into dead zone vs everything else (below the high, fresh 0% new highs, well-extended
10–20%+):

| up=0, breadth-filtered | at-close PF | limit PF (tw=2/cap=10) |
| --- | ---: | ---: |
| **dead zone (0–10% above high)** | 1.275 | **1.457** |
| **rest of universe** | **1.757** | 1.411 |

On the rest, the limit **drops PF 1.757→1.411 and net P&L $671k→$254k** — a big loss. Win rate actually
*rises* (44%→53%) while PF *falls*: the classic "many small pullback wins, but you miss the few huge
runners that carry the system" — the biggest winners are precisely the names that never retrace. It is
not a timeout problem here (limit fills alone are 1.438, ~95% of rest-of-universe names do retrace);
buying the pullback on a strong breakout is **just worse**, because the at-close entry catches the
continuation while the pullback entry sits through a dip that, on the winners, costs the entry into
the best part of the move.

So the limit entry helps *exactly* the band where chasing the close was the problem (the dead zone)
and hurts everywhere else — the same position-dependence that runs through this whole system. **If
shipped it must be a CONDITIONAL entry rule: trailing limit in the dead zone, buy-the-close
everywhere else** — never a global default. (Aside, untested: gap-overs might instead want an
EARLY-in-the-day open entry — gaps come from premarket news, so waiting for the close may be wrong —
but that needs premarket-volume data we don't have yet to model open entries.)

#### Gap-over baseline in the intraday-data window (mid-2021+) — setting up the open-entry test (2026-06-18 PM)

To prep the open-entry idea above we pulled the **bulk 1-minute aggregate universe** (Massive
`download-bulk-minute`). The subscription only reaches **5 years back**, so the data starts
**2021-06-17** (the pre-2021-06-17 dates 404/403 as out-of-window — expected, not retriable). That
fixes the analysis window: **mid-2021 onward**. Here is the at-close baseline the open-entry test must
beat, on the near-high universe (`pct_52w_high_at_entry ≥ 0`, pure gainers `up=0`, breadth-filtered):

| mid-2021+ (near-high, at close) | n | PF | net | win% |
| --- | ---: | ---: | ---: | ---: |
| **gap-over** (signal-bar `adj_open ≥ hi_252_high`) | 277 | **1.09** | +$5.3k | 42.2 |
| reclaim (open below → close above) | 336 | 1.22 | +$24.9k | 42.6 |

Two takeaways for the open-entry work:
- **Gap-overs are barely break-even at the close (PF 1.09)** in this window — a *low bar*, which is the
  point: the thesis is that a gap's move happens premarket/at-open and fades into the close, so the
  close is the wrong entry. Lots of headroom if entering at/near the open helps; the 277 gap-over
  trips are the test set.
- **Reclaims still beat gap-overs even here (1.22 vs 1.09)** — consistent with every prior split;
  reclaim is the genuinely better near-high entry, gap-over is the suspect class the intraday data is
  meant to rescue or kill.

⚠️ **Per-year gap-over PF is pure noise** — 26–81 trades/year, PF swinging 0.66 (2025) → 2.00 (2022) →
0.66 → 2.03 (2026) with no trend. **Pool the whole mid-2021+ window; do not slice gap-overs by year.**
The decision rule for tomorrow: if an open / early-morning entry materially beats the 1.09 close
baseline on these 277, gap-overs become tradeable and it's worth re-subscribing for older data;
otherwise gap-overs stay a skip and **reclaim + dead-zone limit** remains the answer. (Minute corpus
now on disk: `data/minute_aggs/{date}.parquet`, 2021-06-17 → 2026-04-17, 1,214 daily files.)

#### Breakouts FAR below the high (< −15%) — positive but weaker, and the structure inverts (2026-06-18)

With the quality filters MET (tightness<4, ATR%<0.11, rvol≥3) but the 52w gate OFF, what happens to
breakouts in stocks **far below their 52w intraday high** (`pct_52w_high_at_entry < −15%`)? They are
**still net positive but markedly weaker** than near-high breakouts (PF ~1.1–1.45 vs 1.5–1.8), and
the move/volume structure **inverts** vs the near-high regime.

⚠️ **Penny-stock caveat (load-bearing):** without a price floor this bucket is dominated by a sub-$1
artifact — the <$1 band alone was PF **14.6 / +$26.5M** (≈75% of the bucket's nominal P&L), because
fixed-$10k notional on a $0.30→$3 name books absurd P&L you could never actually deploy. **All
numbers below apply the $5 price floor**; the far-from-high analysis is meaningless without it.

| pct_up ($5+, <−15% below high) | n | PF | | rvol | n | PF |
| --- | ---: | ---: | --- | --- | ---: | ---: |
| <2% | 11297 | 1.365 | | 3–5 | 23453 | 1.351 |
| **2–5%** | 8415 | **1.458** | | 5–8 | 7102 | 1.127 |
| 5–10% | 7548 | 1.161 | | 8–15 | 3136 | 1.399 |
| 10–20% | 5941 | 1.105 | | 15–30 | 1143 | 1.042 |
| 20–35% | 1861 | 1.20 | | **30+** | 860 | **0.397** |
| 35%+ | 632 | 1.13 | | | | |

**The inversion:** near/at the high, explosive moves are premium; *far below* the high, the **modest
movers (2–5%) are best** (PF 1.46) and bigger moves fade to ~1.1 — a big up-day far below the high is
just a dead-cat bounce in a downtrend, no edge. By volume the edge is flat-positive 3–15 then the
**extreme 30+ rvol bucket collapses to PF 0.40 (−$551k)** — the same "huge volume spike = blow-off
loser" pattern as near the high, just at a higher rvol threshold (30+ here vs 8+ near the high).
Position relative to the 52w high genuinely changes the breakout's character.

**rvol band [5,20] does NOT help far below the high** (PF 1.228 vs ~1.27 at rvol≥3): it discards the
**rvol 3–5 band, which is the best volume bucket here** (PF 1.35) — the opposite of the near-high
production system where [6,20] is right. Within [5,20] the move profile is the same (2–5% best at
1.54; the 5–20% bands go slightly negative). The rvol band is itself position-dependent.

**By PRICE LEVEL the far-below-high edge is monotonic — lower is better** ($5+, rvol[5,20], <−15%):

| price | n | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| $5–10 | 3481 | 30.2% | **1.379** | +630k |
| $10–20 | 3381 | 35.1% | 1.268 | +356k |
| $20–50 | 2542 | 30.3% | 1.305 | +335k |
| $50–100 | 713 | 28.8% | **0.882** | −44k |
| $100+ | 739 | 23.3% | **0.72** | −144k |

The $5–50 range carries the whole edge (PF 1.27–1.38); **>$50 flips negative** (a $100+ large-cap 15%
off its high doing a one-day volume breakout is mostly noise that fades, vs a $5–20 name where an
oversold-bounce/turnaround can run hard in % terms). So the far-below regime's profile is the
**opposite of near-high production**: lower price ($5–50), modest move (2–5%), lower-to-moderate
volume (rvol 3–5) — vs near-high's higher-priced, big-move, high-volume winners.

### Loose-base breakouts are a strong negative edge — and LINEAR tightness separates the tail better

Sanity check (2026-06-18): does buying breakouts from *loose* bases (high tightness) lose money, as
the old `momentum_v0` study found? **Yes — strongly, and v2 reproduces the old table trade-for-trade
once the gates are matched.** v2's linear `Tightness` = `range / ATR` (drops v0's `÷14`), so
v0_tier × 14 ≈ v2 scale.

| tier (old scale) | v2 n | v2 PF | old n | old PF |
| --- | ---: | ---: | ---: | ---: |
| <0.40 (tight) | 30,048 | **1.218** | 30,051 | 1.22 |
| 0.40–0.55 | 5,662 | 0.904 | 5,659 | 0.98 |
| 0.55–0.70 | 1,241 | **0.589** | 1,242 | 0.65 |
| 0.70–0.85 | 283 | **0.304** | 286 | 0.41 |
| **0.85+ (trend)** | 74 | **0.252** | 71 | **0.21** |

N matches to a handful of trades per tier; the loose tail collapses to PF ~0.25 (matching v0's 0.21).
**Loose-base breakouts are a confirmed money-loser, and the v2 engine is faithful to v0.**

**What had to match to reproduce it** (my first attempt diverged ~3× on N — these were the culprits):

1. **rvol ≥ 3.0** — the v0 study's floor. (v2 production uses the [6,20] band; a *fully*-unbanded
   run floods in low-rvol breakouts and ~triples N.)
2. **52-week proximity = 1.0** — v0 required a *strict new closing high*; v2's default is 0.95
   (within 5%), which is far more permissive. This is the biggest N lever.
3. **No breadth filter** — the v0 tightness study was raw (no `breadth_lag1 > 0.5`). Applying
   breadth drops N ~30% and is NOT how the old table was built.

Exact reproduction command (linear tightness, 15-day stop, old gates, no breadth, 2005+ post-hoc):

```bash
dotnet run --project TradingEdge.HighFlyer -c Release -- \
  --start-date 2003-09-10 --end-date 2026-05-13 \
  --stop-low-window 15 --tightness-mode linear \
  --min-price 0 --rvol-min 3 --rvol-max 999999 --min-52w-pct 1.0 \
  --up-threshold 0.05 --max-tightness 999999 --max-atr-pct 999999 \
  -o /tmp/v2_oldgates.csv
# then tier on tightness_14_at_entry with entry_date >= 2005-01-01 and NO breadth join.
```

**Linear vs log matters for the tail.** This collapse only shows up in LINEAR tightness. In LOG
space the same trend tier reads PF ~0.97 — log *compresses* the extreme blow-out region where the
worst losers live, masking the tail. Log and linear are functionally identical as an entry filter
*at the `<4.0` cutoff* (where both agree) but **diverge in the loose tail; linear is the sharper
discriminator there.** (The earlier "ATR%-denominator artifact" theory for the tail was wrong — the
v0 study predates the ATR filter and used a plain linear ATR.)

**ADOPTED (2026-06-18): linear tightness is now the v2 default** (`TightnessMode = Linear`). The
`< 4.0` cutoff carries over essentially unchanged — linear `< 4.0` = PF 1.758 / 2,253 trips vs the
old log default's 1.734 / 2,260, a marginal improvement with the bonus of the cleaner loose-tail
cut. Log mode stays reachable via `--tightness-mode log` for comparison only. The yearly/monthly
detail tables below were generated on the *log* default and differ by ~7 trades; the headline above
is the current (linear) default.

#### The loose-base negative edge is conditional on a VOLUME / MOVE spike (2026-06-18)

The loose-base loss isn't unconditional — it's overextension **combined with a same-day volume or
move spike**. Take *all* loose-base new-high breakouts (tightness > 7.5, new 52w close-high, every
other filter OFF — no rvol/price/ATR/move gates, ADV≥$100k liquidity floor only; 15-day stop, **no
entry-day stop**, breadth, 2005+) and break down by rvol and by the day's move:

| rvol at entry | n | PF | net | | pct_up | n | win% | PF | net |
| --- | ---: | ---: | ---: | --- | --- | ---: | ---: | ---: | ---: |
| <1 | 11212 | 1.157 | +780k | | <2% | 14718 | 50.0% | **1.342** | +1.62M |
| 1–2 | 6608 | 1.164 | +548k | | 2–5% | 4461 | 39.4% | 1.004 | +11k |
| 2–3 | 2196 | 1.007 | +10k | | 5–10% | 1933 | 39.1% | 1.071 | +128k |
| 3–5 | 1432 | 0.857 | −176k | | 10–20% | 980 | 33.6% | 0.808 | −272k |
| 5–8 | 619 | 0.898 | −66k | | 20–35% | 331 | 26.6% | 0.75 | −199k |
| 8–15 | 410 | **0.606** | −242k | | **35%+** | 343 | **19.2%** | **0.419** | −695k |
| 15+ | 289 | **0.51** | −260k | | | | | | |

**Both rvol and the daily move are monotonic killers, and the move is even cleaner.** A loose-base
name drifting to a new high on quiet volume / a small move (<2% day, PF 1.34, 50% win) is **fine**;
the *same* loose base ripping +35% on huge volume (rvol 15+, PF ~0.5) is the disaster — 19% win
rate, −$695k. This is the exhaustion signature in its purest form: a loose base is only dangerous
when it's *also* a climax spike that day. (The high-rvol/high-move negative edge holds with the
entry-day stop dropped too, so it is a real signal, not a stop artifact — though dropping the
entry-day stop did lift the whole population's PF 1.028 → 1.079, since the wide-range loose breakouts
trip the tight initial stop a lot.)

**Implication for exhaustion exits:** the v0 exhaustion exit fired on overextension alone, with no
volume/move condition — so it was a blunt instrument. Adding an rvol/move condition to the exhaustion
trigger should make it far more targeted (fade only the spike-blow-offs, not quiet drifters). This is
the next thing to test on the exit side. *(Reproduction: the 998k-trip `--no-entry-day-stop` run
above, tiered on `tightness_14_at_entry > 7.5` with `rvol_at_entry` / `pct_up_at_entry`.)*

##### …and the conditional exhaustion exit was BUILT and TESTED — it does not help (2026-06-19)

Implemented exactly that targeted trigger (`ExhaustionConfig`, CLI `--exhaustion-exit`): on each HELD
bar, exit at next open when **tightness > 7.5 AND ((rvol > 3 AND move > 5%) OR move > 10%)** — the
move-primary / rvol-sharpener rule the study above prescribed. Tested on three bases (loosened set,
up≥5% rvol≥3):

| base | exhaustion OFF | exhaustion ON |
| --- | ---: | ---: |
| window-low(4) | 1.352 | 1.332 |
| BE 10% + 20d | 1.40 | 1.393 |
| ATR k=4 | 1.421 | 1.412 |

**It slightly HURTS on every base.** The diagnosis (exit-reason mix on the BE+20d run) is decisive and
explains why: the exhaustion exit fired only **194 times (1.6%)**, on trades that were **93% winners,
median +41% return (best +2368%), median hold 3 bars**. I.e. on a *held* position a "loose-base
blow-off bar" only occurs **after the name has already exploded up** — so the trigger sells the
right-tail rockets a momentum system lives on, banking +41% where letting them ride to the BE/time-stop
earned more. **The asymmetry is the lesson:** the loose-base negative edge is about *entering* a base
that is *already* blown-off; once you're holding, a blow-off bar means *you are winning big*. The exact
same signal flips sign between entry-filter and held-exit *when applied unconditionally*. (This first
pass joined the position-relative expansion exit, fixed targets, and the gap-over time-stop as
upside-capping exits — but see the gain-gated refinement below, which rescues it to flat-positive.)
The signal's primary use remains an **entry** filter (which production already applies via
`MaxTightness`/rvol/move gates); the held-exit version works only when gated by extension (next).

###### Resolving the paradox: the loose-base ENTRY really is −EV, the held-exit just never sees those names (2026-06-19)

The objection was reasonable: if entering a loose-base spike is strongly −EV, exiting a held position on
the same condition "should" help. So we tested the entry directly — admit loose bases (tightness & ATR
gates off, 52w-high kept, up≥5% rvol≥3, window-low stop, long, breadth, 2005+) and tier:

| loose-base ENTRY | n | win% | PF | net | avg ret |
| --- | ---: | ---: | ---: | ---: | ---: |
| all (loose-admitting) | 25477 | 40.0 | 1.052 | +$552k | +0.2% |
| **loose (tight>7.5)** | 1121 | 29.6 | **0.536** | −$571k | **−5.1%** |
| loose + rvol>3 + up≥5% | 1121 | 29.6 | 0.536 | −$571k | −5.1% |
| **loose + up≥10% (rule B)** | 710 | 26.2 | **0.419** | −$603k | **−8.5%** |

**The premise is confirmed: buying the loose-base spike is a strong −EV pattern (PF 0.54 / −5%; the
up≥10% subset PF 0.42 / −8.5%).** (Rows 2 and 3 are identical — every tight>7.5 name in this run
already had rvol>3 & up≥5%, since the run required them to enter.) **So why doesn't the held-exit on
the same condition help?** Because they are **disjoint populations.** The −EV entry names are loose *at
the moment of entry* — you bought the top of someone else's run. A *held* position that trips the same
condition was entered from a TIGHT base (production entry) and only *became* loose later — i.e. it
already ran in your favor. Bucketing the 194 held-exit triggers by their gain when the blow-off fired:
only **13 were at a loss**; 181 were winners (avg +6% / +18% / +56% / +500% across the 0-10 / 10-30 /
30-100 / 100%+ buckets). The held-exit mostly fires on names that already paid you.

**…but the EXTENSION at the trigger is the discriminator, and gating on it salvages the exit
(2026-06-19).** Measuring the **20-day-forward return FROM the exhaustion-exit price** settles which
way it cuts — and it depends entirely on how extended the position is:

| post-exhaustion, by gain-at-trigger | n | avg fwd-20d | % up |
| --- | ---: | ---: | ---: |
| near entry (<5% gain) | 27 | **−4.58%** | 63% |
| 5–30% gain | 111 | −0.10% | 48% |
| >30% gain | 54 | **+11.91%** | 52% |

A blow-off **near entry reverts** (−4.6% fwd, the toppy chase — the same −EV pattern as the loose-base
entry); the same blow-off **after a 30%+ run keeps going** (+11.9%). The unconditional exit lumps both
and the rocket group dominates → net wash-to-drag. **Gating the exit on gain-from-entry** (new
`ExhaustionConfig.MaxGain`, CLI `--exhaustion-max-gain`) — fire only while not-yet-extended — flips it
from a drag to flat-positive on the BE 10% + 20d base:

| BE 10% + 20d | PF | net |
| --- | ---: | ---: |
| exhaustion OFF | 1.400 | $1.889M |
| + exhaustion, no gain cap | 1.393 | $1.845M |
| + exhaustion, gain cap 5% | **1.405** | $1.906M |
| + exhaustion, gain cap 10% | 1.404 | $1.897M |
| + exhaustion, gain cap 20% | 1.405 | $1.900M |

**Corrected conclusion** (supersedes the earlier "cannot be repurposed as a held exit"): the
exhaustion signal *can* be a held exit, but **only gated by extension** — it must fire on the
not-yet-extended subset (the part that overlaps the −EV entry), never on the rockets. With the cap it
beats the no-exit base on both PF and P&L. **But the effect is tiny and NOT era-robust** (pooled
+0.005 PF; era split 2005-14 1.489→1.480 *down*, 2015-26 1.340→1.351 *up* — a near-wash both ways),
because the near-entry blow-offs only revert ~−4.6% over 20d and there are few of them. So: the
held-exit is real and correctly-signed once gain-gated, but it is **not a meaningful PF lever** — the
edge in this signal still lives overwhelmingly on the **entry** side (buy the tight base, refuse the
loose one; production already does this via `MaxTightness`/rvol/move). `--exhaustion-max-gain` kept for
completeness.

###### Pushing the criteria HARD (tight>8, move>20% / move>10%+rvol) on the fixed-20% base — still no PF gain, and now we know WHY (2026-06-19)

Sharpened the trigger as far as the thesis suggests — tightness > 8, rule B move > 20%, rule A move >
10% with rvol > 3 — on the **fixed-20% ratchet** base (loosened set), the system that rides winners
hardest. Result: **still a wash.** No-cap 1.460 vs base 1.464; gain-capped 1.465 (cap 5/10/30% all
+0.001). The unconditional sharp exit lifts **win% 40.9→41.0** while **PF 1.464→1.460 and P&L
$5.14M→$5.07M** fall — the tell-tale mean-vs-median signature. The forward-20d-from-trigger study
explains it definitively, split by gain-at-trigger:

| sharp trigger, fwd-20d | n | avg | median | % up |
| --- | ---: | ---: | ---: | ---: |
| <5% gain | 5 | −20.1% | −10.3% | 40% |
| 5–30% gain | 30 | −2.4% | −4.3% | 37% |
| 30–60% gain | 21 | +0.6% | +3.1% | 62% |
| 60–150% gain | 18 | **+13.1%** | **−10.6%** | 44% |
| 150%+ gain | 6 | **+4.9%** | **−5.2%** | 33% |

**The user's claim — "PF goes negative even for significant winners" — is half-right, and the half
that's wrong is decisive.** For the big winners (60%+ gain) the *median* forward return DOES go
negative (−10.6%, −5.2%) and most decline (33–44% up) — the typical extended blow-off reverts, exactly
as hypothesized. **But the *mean* stays positive** (+13.1%, +4.9%) because a few monster continuations
dominate the average. **PF and P&L are mean-driven, not median-driven** — so exiting the big-winner
blow-offs improves the median trade and the win rate but *loses* on the mean, because momentum's whole
edge is the fat right tail. There is no tightness/move/rvol/gain configuration that escapes this: the
near-entry triggers are genuinely −EV but too few (5–35 trades) to move a 12k-trade book, and the
extended triggers can't be cut without sacrificing the tail. **Definitive close of the exhaustion-exit
line: it is structurally incapable of raising PF on this system — not a tuning problem.** The signal is
an entry filter; on the exit side, manage downside + holding period only and let the tail run.

###### The condition is STRONGLY −EV universe-wide — it's an ENTRY signal we're already screening (2026-06-19)

The held-exit only ever saw ~80 of these bars. How many are there in the *entire tradeable universe*,
and what's their forward return? Scanned every (ticker, day) bar 2005+ (CS/ADRC, price≥$5, ADV≥$100k,
breadth lag1>0.5), reconstructing the same per-bar tightness / rvol / pct_up, and measured the 20-day
forward return on bars meeting **tightness>8 AND ((rvol>3 AND move>10%) OR move>20%)**:

| universe exhaustion bars | n | avg fwd-20d | median | % up |
| --- | ---: | ---: | ---: | ---: |
| all (ADV≥$100k) | 1127 | **−14.5%** | −19.1% | 28.3% |
| ADV≥$1M, $5–10 | 230 | −15.7% | −21.3% | 23.9% |
| ADV≥$1M, $10–20 | 210 | −14.7% | −18.8% | 30.0% |
| ADV≥$1M, $20–50 | 206 | −13.3% | −13.7% | 31.1% |
| ADV≥$1M, $50+ | 270 | −21.1% | −29.5% | 22.2% |

**Universe-wide this is a powerful, robust negative-EV signal** — −14.5% avg / −19% median over 20
days, only 28% higher, and negative across *every* price bucket (no penny-stock artifact; $50+ is the
worst at −21%). So the user's instinct that the pattern is toxic is **completely correct** — on the
*mean*, not just the median, when measured over the whole universe.

**Reconciliation with the failed held-exit (the key insight):** the ~1,127 toxic bars and our ~80
held-exit triggers **barely overlap.** The toxic universe bars are overwhelmingly names that were
loose/extended for a long time — exactly the names the **tight entry gate already refuses to buy.** The
handful that surface on a *held* position are a biased subsample: names that passed a tight entry, ran
up into us, then spiked — skewed toward already-big-winners whose *mean* forward return stays positive
on the fat tail (≠ the −14.5% universe mean). So there's no contradiction: **the condition is toxic in
the wild, production already screens ~all of it on entry, and the residue reaching a held position is
the non-representative right tail.** The actionable edge is on the **entry side** (a hard avoid — which
`MaxTightness`/rvol/move largely encode — or, untested, a candidate SHORT entry: −19% median / 28% up
on 1,127 liquid occurrences is a far larger, cleaner signal than the held-exit could ever capture).

###### ATR% at the exit bar IS a clean discriminator — but the held sample is still too small (2026-06-19)

Can ATR% at the moment of the blow-off separate the held-exit winners from losers? Reconstructed the
log-ATR% at each of the ~80 exhaustion-exit bars and crossed it with the 20-day-forward return:

| ATR% at exit | n | avg fwd-20d | median | % up |
| --- | ---: | ---: | ---: | ---: |
| <5% | 12 | +5.8% | +2.3% | 67% |
| 5–8% | 16 | +7.8% | +3.6% | 56% |
| 8–12% | 26 | +8.2% | −4.6% | 42% |
| **12%+** | 26 | **−11.6%** | **−25.9%** | **31%** |

**ATR% is the cleanest discriminator found** — the >12%-ATR% bucket craters on *both* mean and median
(−11.6% / −25.9%, 31% up) while low-ATR% blow-offs keep grinding up (+6–8%). And it is **orthogonal to
gain-from-entry** (the 26 high-ATR% losers split 16 low-gain / 10 high-gain), so it catches reverting
big-winners the gain-cap can't. Added `ExhaustionConfig.MinAtrPct` (CLI `--exhaustion-min-atr-pct`):
firing the exit only when ATR% exceeds the floor removes the drag and finally edges past the base on
*both* PF and P&L, **in both eras** (the only gate that does so without hurting either):

| fixed 20% base | PF | net | 2005–14 PF | 2015–26 PF |
| --- | ---: | ---: | ---: | ---: |
| exhaustion OFF | 1.464 | $5.136M | 1.536 | 1.400 |
| + sharp exh, no ATR gate | 1.460 | $5.071M | — | — |
| + sharp exh, ATR%>0.15 | **1.465** | **$5.150M** | **1.538** | **1.402** |

**But it is still only +0.001 PF / +$14k on $5.1M** — a rounding-error gain. ATR% *correctly* isolates
the cratering subset (genuinely useful), but on the **held** population there are simply too few of
these bars (the entry gate already excludes nearly all of them — see the 1,127-bar universe scan
above). **So the conclusion stands and is now triple-confirmed (gain-cap, ATR%-floor, universe scan):
the exhaustion signal cannot be a meaningful HELD-EXIT PF lever; its payoff is on the ENTRY side.** The
ATR%-discriminator finding is most valuable *there* — a candidate sharper entry-avoid / short-entry
filter (high ATR% + loose + spike = the −26%-median cohort). `--exhaustion-min-atr-pct` retained.

###### ⭐ ATR% sorts the UNIVERSE exhaustion signal monotonically — a real short/avoid edge (2026-06-19)

Applying the ATR% breakdown to the **entire universe** of exhaustion bars (the 1,127), not just the
held exits, gives the cleanest gradient of the session — fwd-20d falls monotonically with ATR% on every
metric:

| ATR% at bar | n | avg fwd-20d | median | % up |
| --- | ---: | ---: | ---: | ---: |
| <5% | 115 | +2.7% | +0.2% | 52% |
| 5–8% | 112 | −1.7% | −3.7% | 39% |
| 8–12% | 171 | −6.4% | −11.6% | 33% |
| 12–18% | 218 | −10.7% | −19.5% | 29% |
| **18%+** | 511 | **−25.6%** | **−34.1%** | **18%** |

**ATR% is an independent monotonic sort within the exhaustion signal** (the <5% bucket is even mildly
*positive* — so it isn't a proxy for the condition itself). The toxic tail is large and clean: **511
bars at ATR%>18% → −25.6% avg / −34% median / only 18% higher over 20 days.** Era-robust on the high-
ATR% (≥12%) cohort: 2005–14 −18% median / 27% up (n=63), 2015–26 −30% median / 21% up (n=666) — both
strongly negative (modern era has far more high-ATR small/mid-caps and is worse, but the early era
confirms it). **This is the genuine edge the whole exhaustion thread was circling, and it is an ENTRY
signal:** high ATR% + loose base (tight>8) + spike (move>20%, or move>10% with rvol>3) is (a) a hard
long-AVOID, and (b) a candidate **short entry** — ~82% lower in 20 days, −34% median, large era-spanning
sample. Categorically different from the held-exit (which only ever saw ~80 of these, biased to the
big-winner tail). **Next: test it as a standalone short book** (`--side short` + these gates as an
entry filter — needs a `MinTightness` entry floor + ATR% floor, currently only present as exit knobs).
*(Decision 2026-06-19: shorting these is NOT being pursued — overnight borrow costs are significant for
these names and spike risk is severe; they'd have to be day-traded. Stay long-only; use this only to
sharpen the long exit/avoid.)*

###### Isolating the exact "holding is −EV" condition: loose base AND high ATR% (2026-06-19)

Rather than tune exit thresholds blind, scan the cost of *holding*: for every qualifying bar (>5%
move, near-high liquid universe, 2005+) take the 20-day-forward return — the EV of holding past that
bar — across the tightness × ATR% grid. The **mean** (the EV that drives PF):

| mean fwd-20d | ATR<8% | ATR 8–12% | ATR>12% |
| --- | ---: | ---: | ---: |
| tight ≤4 | +0.65% | +2.81% | +4.76% |
| 4–8 | +0.48% | +1.05% | +0.51% |
| **loose >8** | +1.37% | **−1.67%** | **−6.93%** |

**The negative-EV-to-hold region is sharply and uniquely the loose+high-ATR corner.** Both conditions
are necessary: a loose base on *low* ATR holds fine (+1.37%), and high ATR on a *tight* base is a
healthy breakout (+4.76%) — only the **conjunction** is toxic. (This also explains every failed exit:
the *median* is negative almost everywhere, but the *mean* is positive except here — exits chasing the
median fought the tail; this is the one cell where the mean itself is negative.) Finer ATR within the
loose bucket: the mean crosses zero at **ATR ≈ 6%** and is robustly negative on mean+median at **ATR ≥
~16%** (16–22%: −8.7% / −10.5%; 22%+: −11.9% / −16.4%; the lone 12–16% cell pops +8.9% mean on a
fat-tail outlier, median still −5.7%).

**So the condition is: `tightness > 8 AND ATR% ≳ 16%` (volatility blown up AND base gone loose) — the
user's exact framing, now pinned to numbers.** But on the LONG book the payoff is still negligible
(fixed-20% base 1.464 → best 1.465 at atr>16%/move>20%): the negative-EV region holds ~2,000 bars over
20 years of the *universe*, yet our long book — gated to TIGHT entries — almost never holds a position
into that state, and when it does it's the biased winner-tail. **Final word on exhaustion exits: the
−EV-to-hold condition is now exactly characterized (loose + high-ATR), the exit no longer hurts, but it
cannot add meaningful long PF because the entry gate keeps us out of that state-space. It is an
entry/avoid signal.** Production already avoids it via `MaxTightness 4.0` (well below the 8 threshold)
and `MaxAtrPct 0.11` — i.e. the long system structurally never enters the loose+high-ATR cell, which is
why holding-EV there is moot for it. The exhaustion-exit knobs are retained as the characterized
negative-result substrate.

###### NO-STOP system: exhaustion finally EARNS its keep — but a pure-ATR exit destroys the edge (2026-06-19)

The reason exhaustion never helped the stopped systems is that the **stop already exits most blow-offs
before exhaustion can fire** (they overlap). So strip the stop entirely (`StopMode.NoStop`, CLI
`--no-stop`; equivalently `--fixed-stop 1` → stop at 0, never reached — confirmed identical) and let
exhaustion be the *sole* exit. (Caveat: no-stop is not a usable strategy — these are 20-year holds at
fixed $10k notional, PF ~5.5 is the degenerate buy-and-hold-survivors scale. Only the *relative* effect
matters.) Loosened set:

| no-stop (loosened) | win% | PF | net | avg hold |
| --- | ---: | ---: | ---: | ---: |
| no exit (MTM only) | 53.6 | 5.553 | $146.1M | 1881 |
| **+ exhaustion (t8, atr16, mv5+rv3 / mv20)** | 53.5 | **5.642** | $147.0M | 1833 |
| + exhaustion (t8, atr12, mv5+rv3 / mv10) | 53.1 | 5.511 | $142.0M | 1766 |

**With no stop underneath, the SHARP exhaustion exit (loose + ATR>16 + spike) beats pure hold on both
PF and P&L** (5.642 vs 5.553) while exiting ~48 bars earlier — the first time exhaustion *adds* value,
because it's now the only thing catching the toxic loose+high-ATR bars. The looser ATR-12 version
hurts (5.511). Confirms the whole thread: exhaustion's value is real but was masked by the stop.

**But a PURE ATR%-threshold exit (hold until ATR% > x, no tightness/move condition) is a BAD exit** —
it monotonically destroys the edge:

| no-stop, exit when ATR% > x | win% | PF | net | avg hold |
| --- | ---: | ---: | ---: | ---: |
| 0.08 | 44.8 | 1.567 | $12.7M | 515 |
| 0.12 | 44.8 | 2.738 | $55.9M | 1027 |
| 0.16 | 48.7 | 4.221 | $109.7M | 1442 |
| 0.20 | 51.3 | 5.031 | $134.3M | 1649 |
| 0.30 | 53.3 | 5.577 | $144.5M | 1817 |
| (no exit) | 53.6 | 5.553 | $146.1M | 1881 |

The tighter the ATR threshold, the worse — exiting on ATR%>8% (PF 1.57) guts the system; it only
converges back to pure-hold near ATR%>0.30 (where it barely exits). **High ATR% ALONE is not toxic — a
volatile momentum name is exactly what you want to hold.** The toxicity requires the **conjunction**
with a loose base: the sharp exhaustion exit adds value *because* it demands both, while the pure-ATR
exit subtracts value because ATR% by itself just means "lively name." This is the cleanest proof yet
that the −EV signal is *loose AND high-ATR*, not either alone — and that a naive volatility-stop is a
mistake on a momentum book.

**rvol vs move: only moderately correlated, and the MOVE dominates.** Within the loose-base
population, rvol and pct_up have a **Spearman rank correlation of 0.38** (raw Pearson 0.04 is
outlier-scrambled and misleading; log–log 0.24). So they share information but are far from
redundant. The 2×2 shows the move is the primary signal and rvol a secondary confirmer:

| (loose base) | rvol < 5 | rvol ≥ 5 |
| --- | ---: | ---: |
| **move < 10%** | PF 1.18 (n=20392) | PF 1.35 (n=720) |
| **move ≥ 10%** | **0.76** (n=1056) | **0.51** (n=598) |

A big move kills the edge regardless of volume (0.76 / 0.51); small-move names are fine either way —
and *high rvol on a small move is the best cell* (1.35; high volume on a quiet day is just liquidity).
But both together is the worst (0.51): rvol adds incremental damage *conditional on* a big move
(0.76 → 0.51). So an exhaustion trigger should make the **daily move the primary condition with rvol
as a sharpener**, not weight them equally.

#### The inverse — buying loose-base capitulation at new 52w LOWS does NOT work (2026-06-18)

Is there a long-side mirror: buy the *down*-blowoff (loose base making a new 52w low) the way we
*avoid* the up-blowoff? Extended the engine with 252d **low** channels (min-close and min-intraday-low,
mirroring the high channels) and emitted `pct_52w_low_close_at_entry` / `pct_52w_low_at_entry`. Took
loose-base (tightness>7.5) names at/below the prior 252d closing low, **down days admitted**
(`--up-threshold -1`), rvol≥3, $5 floor, long-only, 15-day stop:

**Total: 1,797 trips, PF 0.833, −$154k — a net loser.** Breakdown:

| pct_up (capitulation severity) | n | PF | | rvol | n | PF |
| --- | ---: | ---: | --- | --- | ---: | ---: |
| < −15% | 305 | **0.69** | | 3–5 | 1195 | 0.92 |
| −15..−8% | 324 | 0.91 | | 5–8 | 351 | 0.97 |
| −8..−4% | 412 | 0.78 | | 8–15 | 152 | 0.74 |
| −4..0% | 708 | 1.02 | | **15+** | 99 | **0.31** |

**Capitulation is NOT the clean mirror of exhaustion.** Fading euphoria works (avoid loose-base
up-blowoffs), but *buying* loose-base down-blowoffs loses — the deeper the down-day the worse
(< −15% → PF 0.69), only the mild −4..0% drift is ~breakeven, and falling knives keep falling on a
15-day hold. Breadth doesn't rescue it; it's actually **worse in risk-on regimes** (PF 0.65 vs 0.93
risk-off) — a fresh 52w low while the market is healthy is strong relative weakness, a broken laggard.

**Unifying insight:** the high-rvol cell is the worst in *both* directions (up-blowoff rvol 8+ → ~0.5;
down-blowoff rvol 15+ → 0.31). It is not "highs good / lows bad" — it is that **a violent,
high-participation move on an unstructured (loose) base is toxic to be long of, up OR down.** For the
up case you avoid/fade it; for the down case you simply don't buy it. There is no symmetric "buy the
panic" long edge here.

#### …so SHORT them instead? Only the high-volume capitulation (2026-06-18)

If buying the new-low loses, does *shorting* it win (trail the stop along the prior-window HIGH,
cover on a break up — the mirror system)? Added a `Side = Long | Short` mode (`--side short`): Short
trails the 15-day high and flips the P&L sign. On the same loose-base new-low population:

**SHORT total: 1,797 trips, PF 0.914, −$115k — also a loser.** So it's *not* a simple stop-trip
artifact (if it were, the short would profit). Both sides losing means these names **chop / mean-
revert** — you get stopped out long on the bounces AND stopped out short on them. A no-trend regime.

BUT the short edge concentrates exactly where the long was worst — **high volume** (2×2, loose-base
new-low, short):

| SHORT | rvol < 8 | rvol ≥ 8 |
| --- | ---: | ---: |
| big down (≤ −8%) | 0.67 (n=484) | **1.47** (n=145) |
| mild (> −8%) | 0.99 (n=1062) | **1.51** (n=106) |

**Shorting works only on a volume spike (rvol ≥ 8): PF ~1.5 in both move rows** (~250 trades, ~48%
win) — and loses/chops on normal volume; the big-down/low-volume cell is the worst (0.67, those
bounce and stop you out). It's **volume-gated, not move-gated**, and a modest low-frequency setup.
⚠️ **This full-sample short edge is a post-2015 REGIME artifact** — it lost badly pre-2015 (it's the
short half of a regime that flipped). See the regime-switching subsection below before trusting it.

**The symmetry (rvol is the key, both sides):** high-volume blow-offs *continue* — short the
down ones (PF ~1.5), fade/avoid the up ones (long PF ~0.5) — while **low-volume drifts mean-revert /
chop** and have no directional edge either way. The genuine inverse-exhaustion short edge exists but
is narrow: loose base + new 52w low + **rvol ≥ 8**.

**Deep dive: the rvol 3–5 segment by move size (2026-06-18, fixed prices).** The big losing bucket
(rvol 3–5, PF 0.74, 1,184 trades) split by the down-day magnitude looks U-shaped — profitable at the
extremes, deeply negative in the middle:

| pct_up (rvol 3–5 short) | n | PF | | era split | 2005–14 | 2015–26 |
| --- | ---: | ---: | --- | --- | ---: | ---: |
| < −20% (crash) | 63 | 1.36 | | crash < −12% | 0.23 | 0.79 |
| **−20..−12%** | 111 | **0.31** | | mid −12..−1% | 0.42 | 1.06 |
| −12..−4% | 442 | ~0.72 | | **flat −1..0%** | **1.25** | **1.51** |
| −4..−1% | 390 | 0.80 | | | | |
| **−1..0% (flat)** | 167 | **1.46** | | | | |

⚠️ **But the era split kills most of the U-shape.** Only the **flat −1..0% quiet-drift-to-a-new-low
is robust** (PF 1.25 then 1.51 — stable across both halves): a name barely setting a new low on
moderate volume has no bounce energy and bleeds lower. The "crash < −20% short works" cell is an
**era artifact** (PF 0.23 in 2005–14, only positive recently — don't trust it). The moderate-down
middle (−20..−12% = PF 0.31, spread across many names not one outlier) is reliably bad in both eras —
moderate down-days at a new low snap back violently. The whole rvol-3-5 segment was much worse pre-2015
(mid-band 0.42 vs 1.06), so its negativity is partly an old-era effect. **Takeaway: within this
segment the one trustworthy short is the near-flat quiet new low; the rest is mean-reversion or noise.**

#### ⚠️ The whole new-low setup is REGIME-SWITCHING — long↔short flipped at ~2015 (2026-06-18)

Era-splitting the loose-base new-low population by rvol exposes that this setup has **no stable
directional edge** — the two sides are near-perfect mirror images that swapped regimes mid-decade:

| rvol | LONG 2005–14 | LONG 2015–26 | SHORT 2005–14 | SHORT 2015–26 |
| --- | ---: | ---: | ---: | ---: |
| 3–5 | **1.31** | 0.70 | 0.43 | 0.94 |
| 5–8 | 1.15 | 0.92 | 0.59 | 1.20 |
| 8–15 | 0.92 | 0.81 | 0.69 | 1.33 |
| 15+ | **2.43** | 0.12 | 0.23 | **2.23** |

- **2005–2014 = mean-reversion regime:** new lows BOUNCED. Buying them worked (rvol 3–5 PF 1.31,
  rvol 15+ PF 2.43); shorting them lost (every band < 1).
- **2015–2026 = continuation regime:** new lows kept FALLING. Shorting worked (rvol 15+ PF 2.23);
  buying got crushed (rvol 15+ PF 0.12).

So the earlier "**high-rvol capitulation short works (PF 1.5–1.7)**" conclusion was a **single-regime
(post-2015) artifact** — full-sample PF hid that it lost badly pre-2015. Likewise the long
mean-reversion edge exists but **only in 2005–14**, and inverted after. Neither side is tradeable
forward on its own: the new-low setup is a regime bet, not a structural edge. (Lesson reinforced:
era-split any "edge" found by pooling 20 years — full-sample PF can be the average of two opposite
regimes.)

#### Shorting TIGHT-consolidation breakdowns to new lows — does NOT work (2026-06-18)

The above was all *loose*-base. Mirror thesis: a **tight** consolidation (tightness<4, ATR%<0.11 —
the production quality filters) breaking DOWN to a new 52w low on volume should be a clean
distribution short, the mirror of the winning long breakout. **It isn't.** Short, quality filters ON,
new 52w close-low, down days, rvol≥3, $5, 15-day stop — every rvol×era cell is < 1.0:

| rvol | SHORT 2005–14 | SHORT 2015–26 |
| --- | ---: | ---: |
| 3–5 | 0.97 | 0.71 |
| 5–8 | 0.50 | 0.86 |
| 8–15 | 0.98 | 0.65 |
| 15+ | 0.60 | 0.96 |

Total PF 0.81; **no volume band wins in either decade** (and unlike the loose-base setup it isn't
even regime-saved — it loses both halves). Tight bases breaking to new lows **bounce, they don't
cascade** (win rates 30–40% but net-losing — shorting into support that holds / failed breakdowns).

**Tightness is a LONG-ONLY signal — the asymmetry is the point:**

| base | breakout UP (long) | breakdown DOWN (short) |
| --- | ---: | ---: |
| **tight** | PF ~1.77 (the production edge) | **0.81 (loses both eras)** |
| **loose** | PF ~0.4 (avoid) | regime-switching (no stable edge) |

A tight consolidation is coiled-spring/accumulation structure biased to resolve UP (the whole
momentum premise); when it breaks down instead it's often a false breakdown that snaps back. So the
tight-base short direction is closed off — definitively (both eras), more firmly than the loose-base
new-low setup.

### Exits that *didn't* survive the realistic baseline

- **Trailing limit** (sell at the prior N-day high) — a ≤+1% PF refinement under honest fills;
  retired. The "PF 3.0 / 80% winning months" of the v0 era was a top-tick fill artifact (the limit
  filled at the recent high *every* bar). See the v0 doc's warning banner.
- **Expansion exit** — sell when the name "blows off". Tested three ways (2026-06-18), all dead;
  the trailing stop is the right exit. See the dedicated section below.

### Expansion exit — a thoroughly-tested dead end (2026-06-18)

The intuition is sound (take profits when a momentum name goes parabolic), but **no variant beats
just holding to the trailing stop.** Three attempts, each with a diagnostic that explains the
failure:

1. **Tightness blow-out, LOG space** (`tightness > thr`). Under the next-open baseline PF climbs
   monotonically as the threshold loosens and converges to "off" — every firing threshold is worse
   than off. (A `thr=8` "peak" seen earlier was an artifact of the old trailing-limit fills; it
   vanished at cap=0.)
2. **Tightness blow-out, LINEAR space.** Same shape, same conclusion. The diagnostic shows *why
   it can't work in any space*: tightness = `range₁₄ / ATR₁₄`, and a climax bar inflates the
   14-day range AND the 14-day ATR **together**, so a +30% day reads the *same* tightness (~3.8
   median) as a +10% day. Tightness is a **consolidation** detector, not a **spike** detector.
   (Bonus finding: as an *entry* filter, linear vs log tightness are functionally identical —
   PF 1.758 vs 1.734, same drawdown, same monotonic cutoff curve. The `range/ATR` ratio is nearly
   scale-invariant because the price normalization cancels top and bottom.)
3. **Position-relative range** (floor the range-low at the entry price:
   `range = rangeHigh − max(rangeLow, entryPrice)`). This *does* fire on multi-bar run-ups (273
   exits at thr=4), fixing attempt 2's blind spot. But it **only ever truncates winners**: the
   floored range can only be large when the stock is far ABOVE entry, so the exit never touches a
   loser. Counterfactual on the 273 fired trades: booked +$744k, would have made **+$811k** if
   held — it left **$67k on the table**. By construction it fights the edge ("let winners run").

**Conclusion:** this momentum edge is "hold to the trailing stop." Anything that exits *because* a
name is up a lot is selling the right tail, where these names keep running more often than they
revert. Expansion exit stays **off**. The `--tightness-mode log|linear` flag and the
position-relative `ExpansionTightness` member remain in the engine as the substrate for the tested
negative result (and any future single-bar exit work).

### Initial-stop distance vs risk:reward — tighter stops are NOT better (2026-06-19)

Question: does the **distance from entry to the initial trailing stop** predict a trade's
risk:reward, i.e. are trades with *tight* stops better? Tested on a deliberately **loosened** entry
set (more samples) with both the Qulla day-low stop and the trailing prior-window low, at window 15
**and** 4.

**Test parameters (printed for the record — both runs identical except `--stop-low-window`):**

| param | value |
| --- | --- |
| side | Long, at-close entry |
| tightness mode | Linear |
| up-threshold | **0.05** (loosened from prod 0.10) |
| rvol band | **[3, ∞)** (loosened from prod [6,20]) |
| ADV / price / 52w-prox | ≥ $100k / ≥ $5 / ≥ 0.95 |
| tightness / ATR% | < 4.0 / < 0.11 |
| stop-low-window | **15** and **4** (two runs) |
| trail N / exit cap / expansion | 1 / 0 (next-open exit) / off |
| date range | 2005-01-01 → 2026-05-13 |
| breadth | post-hoc lag1 > 0.5 |

19,701 entries per run (entry signal is identical; only the stop trail differs). Metrics:
**R-multiple** = `(exit − entry)/(entry − stop)` (realized return in units of initial risk);
**stop distance** = `(entry − stop)/entry`. Note the production stop-window is **4**, not 15 — the
15-day low is the older/looser "regular" trailing stop; both are tested here.

**Qulla day-low stop** (`entry_day_stop_ref`):

| stop dist | n | win% | avg R | med R | PF |
| --- | ---: | ---: | ---: | ---: | ---: |
| <2% | 1048 | 39.2 | 0.83 | −0.50 | 1.089 |
| 2–4% | 1579 | 34.8 | 0.31 | −0.57 | 1.39 |
| 4–6% | 2586 | 36.5 | 0.24 | −0.51 | 1.39 |
| 6–9% | 3409 | 35.4 | 0.18 | −0.45 | 1.32 |
| **9–13%** | 2151 | 37.7 | 0.35 | −0.38 | **1.68** |
| 13–20% | 1077 | 39.8 | 0.19 | −0.28 | 1.41 |
| 20%+ | 391 | 36.6 | 0.16 | −0.30 | 1.18 |

**15-day-low stop** (structurally wide — a 15-day low sits far under a breakout):

| stop dist | n | win% | avg R | med R | PF |
| --- | ---: | ---: | ---: | ---: | ---: |
| 4–6% | 43 | 30.2 | −0.28 | −0.43 | 0.45 |
| 6–9% | 816 | 37.3 | 0.09 | −0.19 | 1.24 |
| 9–13% | 2685 | 36.6 | 0.07 | −0.16 | 1.28 |
| **13–20%** | 4578 | 35.8 | 0.10 | −0.15 | **1.45** |
| 20%+ | 4144 | 37.9 | 0.10 | −0.11 | 1.44 |

**4-day-low stop** (tighter; higher win rate ~42–44%):

| stop dist | n | win% | avg R | med R | PF |
| --- | ---: | ---: | ---: | ---: | ---: |
| 4–6% | 154 | 46.8 | 0.07 | −0.04 | 1.24 |
| 6–9% | 1995 | 41.7 | 0.06 | −0.10 | 1.26 |
| 9–13% | 3634 | 41.2 | 0.03 | −0.09 | 1.12 |
| **13–20%** | 3947 | 42.7 | 0.09 | −0.06 | **1.45** |
| 20%+ | 2536 | 44.3 | 0.09 | −0.03 | 1.46 |

**Finding: tighter stops do NOT give better risk:reward — the relationship is flat-to-inverted.**
- The Qulla <2% bucket has the highest *avg* R (0.83) but it's a **trap**: PF only 1.09 and median R
  −0.50, i.e. a few big-winner tails over many whipsaw losers (low win rate). High avg-R there is
  fragile, not an edge.
- Across all three stop definitions the genuinely best (highest-PF) cells are the **WIDE** stops
  (9–20%, PF 1.45–1.68), not the tight ones. The 15- and 4-day stops both improve monotonically out
  to 13–20%.
- The 4-day stop trades a **higher win rate** (~42–44% vs ~36%) for **lower avg-R per trade**; the
  15-day stop is wider, lower win rate, but its wide buckets carry the best PF. Neither makes tight
  stops pay.
- Practical read: a tight initial stop mostly buys whipsaw. The momentum edge wants **room** — the
  best R:R lives 9–20% below entry. Don't tighten the initial stop to chase R:R.

### Tight-stop trades — a 20-day TIME-STOP rescues them (the whipsaw, confirmed) (2026-06-19)

Follow-on to the above: for the **tight-stop bucket** (Qulla day-low < 2% below entry, where the
trailing stop whipsaws), what if we'd **replaced the trailing stop with a 20-day time-stop** — hold
exactly 20 trading bars and exit at that bar's open (MTM at last close if the ticker runs out of
bars)? Post-hoc over the same loosened run; forward prices from `split_adjusted_prices` (same
CS/ADRC universe + date range), per-trade fixed-$10k notional to match the engine.

**Parameters:** same run as the stop-distance analysis above (Long, at-close, Linear, up≥0.05,
rvol[3,∞), ADV≥$100k, price≥$5, 52w≥0.95, tight<4.0, ATR%<0.11, 2005-01-01→2026-05-13, breadth
lag1>0.5, **stop-window 15** run — the Qulla day-low ref is window-independent). Bucket = Qulla
`(entry−day_low)/entry < 0.02`. Time-stop exit = open of the 20th bar after entry.

| tight-stop bucket (Qulla <2%) | n | win% | net | PF | avg/trade |
| --- | ---: | ---: | ---: | ---: | ---: |
| actual trailing stop | 1048 | 39.2 | $15.8k | 1.089 | $15 |
| **20-day time stop** | 1048 | **60.2** | **$163.9k** | **1.684** | **$156** |

**A ~10× improvement, era-robust:** the trailing stop sits <2% away, so any normal pullback triggers
it and turns would-be winners into small losers. Holding 20 days instead: win rate 39%→**60%**, P&L
**$16k→$164k**, PF 1.09→**1.68**. Era split (only 14/1048 trades truncated <20 bars, so MTM isn't
driving it):

| era | n | trailing-stop net / PF | 20d time-stop net / PF |
| --- | ---: | ---: | ---: |
| 2005–14 | 489 | $4.3k / 1.045 | $84.3k / **1.726** |
| 2015–26 | 559 | $11.5k / 1.14 | $79.6k / **1.645** |

**Control — it is SPECIFIC to tight stops, not "time-stops beat trailing everywhere".** The same
comparison on the **wide** bucket (13–20% stop distance) is a dead heat: trailing PF 1.405 / $320k vs
time-stop 1.407 / $298k (trailing slightly ahead on P&L). So the time-stop rescue fires *only* where
the trailing stop is too close to survive normal noise. Read: a tight initial stop is not merely
"no better" — it is **actively harmful**, and the fix is to **stop trailing close and just hold**
(time-stop) for those names, not to tighten further. (Trade construction lever for a future variant:
route tight-initial-stop entries to a time-stop exit, wide ones to the trailing stop.)

### ATR%-ratchet trailing stop beats the window-low rule (2026-06-19)

Given that the Qulla window-low / entry-day-low stops keep underperforming under the microscope, test
a different mechanism: an **up-only ATR%-chandelier off the latest close**. Each bar the candidate
stop = `close − k·ATR%·close` (ATR% = the log-ATR, a per-bar fractional volatility); the carried stop
**only ever rises** (`stop = max(prev_stop, candidate)`) — never loosens even as ATR expands or price
dips. New engine `StopMode = AtrRatchet k` (CLI `--atr-stop k`); it **fully replaces** the window-low
and entry-day-low geometry (no day-low floor — the ratchet starts from the entry bar's own candidate).
Immutable Position carry of the ratchet level, no lookahead (the level checked on bar B is set from
bars ≤ B-1, then updated from B's close for B+1).

**Parameters:** loosened set — Long, at-close, Linear, up≥0.05, rvol[3,∞), ADV≥$100k, price≥$5,
52w≥0.95, tight<4.0, ATR%<0.11, 2005-01-01→2026-05-13, breadth lag1>0.5. Stop = ATR ratchet **only**
(entry-day-low floor OFF, window-low OFF). Sweep k=1..5 vs the window-low(4) baseline:

| stop | n | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| window-low(4) baseline | 12266 | 42.5 | 1.352 | $1.29M |
| atr-ratchet k=1 | 12266 | 45.5 | 1.309 | $1.10M |
| atr-ratchet k=2 | 12266 | 43.2 | 1.349 | $1.87M |
| atr-ratchet k=3 | 12266 | 42.3 | 1.387 | $2.81M |
| **atr-ratchet k=4** | 12266 | 41.6 | **1.421** | $3.82M |
| atr-ratchet k=5 | 12266 | 40.6 | 1.387 | $4.24M |

**The ATR ratchet beats the window-low stop at k≥3 on PF and massively on P&L** (a looser stop rides
winners far longer; P&L grows monotonically with k while PF peaks at **k=4**, PF 1.421 vs 1.352
baseline). The pattern is the same lesson again: **tight = whipsaw** — k=1 has the *highest* win rate
(45.5%) but the *lowest* P&L and a sub-baseline PF (1.31), exactly the tight-stop trap; momentum wants
room. Era-robust (k=4 wins both halves):

| era | window-low(4) PF / net | atr k=4 PF / net |
| --- | ---: | ---: |
| 2005–14 | 1.489 / $0.66M | **1.568 / $2.03M** |
| 2015–26 | 1.272 / $0.63M | **1.325 / $1.79M** |

So the ATR%-ratchet is a strictly better trailing mechanism than the legacy window-low here — higher
PF, ~3× P&L, both eras. (Not yet swept on the *production* entry set; k=4 is the loosened-set sweet
spot. Candidate to test as the production stop next.) Production default remains window-low(4) until
that confirms.

**R:R by INITIAL ATR-stop distance (k=4) — tight stops hurt here too, inverted-U.** Same breakdown as
the window-low stop-distance analysis, now for the ATR ratchet. Initial stop distance = `k·ATR%` (the
first-bar candidate is `close·(1 − k·ATR%)`, so the distance is exactly `4 × atr_pct_14_at_entry` of
entry); it varies trade-to-trade because ATR% does, even at fixed k. R = `(exit−entry)/(k·ATR·entry)`.

| initial stop dist | n | win% | avg R | med R | PF | net |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| <4% | 47 | 44.7 | 0.95 | −0.09 | 1.62 | $13k |
| 4–7% | 849 | 44.5 | 0.07 | −0.14 | **1.11** | $39k |
| 7–10% | 2287 | 42.4 | 0.09 | −0.19 | **1.16** | $173k |
| **10–14%** | 3313 | 42.0 | 0.27 | −0.20 | **1.57** | $1.04M |
| **14–20%** | 2960 | 42.6 | 0.26 | −0.20 | **1.60** | $1.28M |
| 20–30% | 2002 | 39.8 | 0.21 | −0.28 | 1.46 | $1.02M |
| 30%+ | 808 | 35.4 | 0.09 | −0.37 | 1.18 | $0.26M |

**Tight stops hurt — inverted-U, damage on both tails:** the tight **4–10%** buckets are the *worst*
(PF 1.11–1.16) — low-ATR names where even 4×ATR lands close to entry and whipsaws out, the same trap
as the window-low <2% bucket. The **10–20%** zone is the sweet spot (PF 1.57–1.60) and carries the
bulk of the P&L ($2.3M of $3.8M). The wide **30%+** tail also fades (PF 1.18, median R −0.37 — too
loose to protect, gives a lot back). The `<4%` cell (n=47) is the same misleading high-avg-R /
negative-median tail-artifact seen before — ignore it. So across *both* stop mechanisms the verdict is
identical: **tight initial stops hurt.** The ATR ratchet's edge is that at k=4 it *naturally* parks
most trades in the healthy 10–20% band; where ATR is low enough to still produce a tight stop, it
degrades exactly as the whipsaw thesis predicts.

#### It's the DISTANCE, not the mechanism — the case for a fixed-% stop (2026-06-19)

Running the *initial-stop-distance* R:R breakdown for **every** stop system on the loosened set (Qulla
day-low, 4-day-low, 15-day-low, ATR-ratchet k=2/3/4/5) gives the same shape each time: low PF at tight
distances, best PF in a 10–20%+ middle, fade at the very wide tail. The per-system tables differ in
*where* their trades fall on the distance axis (k=1 is almost all <7%; the 4-day-low spans 4–30%; the
15-day-low is mostly 10%+) but not in the relationship. Pooling **all six systems** and reading PF
purely as a function of the **absolute initial stop distance** (independent of how the stop was
derived) makes it explicit:

| initial stop dist | n | win% | PF |
| --- | ---: | ---: | ---: |
| <4% | 4402 | 40.0 | **1.183** |
| 4–7% | 12936 | 41.1 | 1.306 |
| 7–10% | 15173 | 40.8 | 1.386 |
| 10–14% | 16145 | 40.6 | 1.429 |
| 14–20% | 13675 | 40.5 | 1.448 |
| **20–30%** | 8234 | 40.1 | **1.472** |
| 30%+ | 3006 | 37.9 | 1.219 |

**Absolute distance predicts PF monotonically up to ~20–30%, then fades — and the mechanism washes
out.** A trade whose stop sits ~4% away performs like every other ~4%-stop trade (PF ~1.18) whether
that 4% came from a day-low, a window-low, or an ATR ratchet; a ~20% stop performs like every other
~20% stop (PF ~1.47). The elaborate mechanisms are just different ways of *sampling* a distance
distribution — the ones that look best (ATR k=4, 4-day-low's wide buckets) are simply the ones that
tend to land stops in the 10–30% band. **This is the argument for a fixed-% stop**: if distance is
what matters, set it directly (e.g. a ~12–20% initial stop) rather than inferring it from price
structure and getting whipsawed whenever the structure happens to be tight.

Two honest caveats: (1) this is *initial* distance vs realized P&L; post-entry trailing still differs
by mechanism (the ATR ratchet's *up-only* trail is why it booked far more total P&L — it rides
winners), so "fixed-% beats ATR" is **not** proven here — what's proven is "initial distance
dominates, tight is bad." The thing to test next is a **fixed-% initial stop** (or a min-distance
floor on the existing stops) that then trails or time-stops. (2) The 30%+ fade is partly formula
survivorship (a 30%+ ATR stop only arises on very high-ATR, intrinsically wilder names).

#### Fixed-% ratchet stop — competitive with ATR, but PF-vs-width is a degenerate objective (2026-06-19)

Built the test the previous section called for: `StopMode = FixedPct p` (CLI `--fixed-stop p`) — the
**same up-only ratchet machinery as the ATR stop** but with a *constant* fractional distance:
`stop = max(prev_stop, close·(1−p))`. This isolates distance from mechanism — same trailing, distance
set directly. Same loosened set (Long, at-close, Linear, up≥0.05, rvol[3,∞), ADV≥$100k, price≥$5,
52w≥0.95, tight<4.0, ATR%<0.11, 2005-01-01→2026-05-13, breadth lag1>0.5):

| stop | win% | PF | net | avg hold | % exit via stop |
| --- | ---: | ---: | ---: | ---: | ---: |
| window-low(4) | 42.5 | 1.352 | $1.29M | — | — |
| atr k=4 | 41.6 | 1.421 | $3.82M | — | — |
| fixed p=0.08 | 42.7 | 1.233 | $1.26M | — | — |
| fixed p=0.10 | 42.2 | 1.329 | $2.06M | — | — |
| fixed p=0.12 | 41.8 | 1.325 | $2.36M | — | — |
| fixed p=0.15 | 41.4 | 1.394 | $3.43M | 66 | 94.6% |
| **fixed p=0.20** | 40.9 | **1.464** | $5.14M | — | — |
| fixed p=0.25 | 40.4 | 1.525 | $7.12M | 161 | 91.0% |
| fixed p=0.30 | 39.8 | 1.561 | $8.96M | — | — |
| fixed p=0.35 | 39.5 | 1.689 | $12.6M | — | — |
| fixed p=0.40 | 39.0 | 1.861 | $17.9M | — | — |
| fixed p=0.50 | 38.4 | 2.084 | $27.8M | 512 | 76.4% |

**Two findings:**
1. **In the tradeable range, fixed-% matches/beats the best ATR stop.** At p=0.20 PF 1.464 > ATR k=4's
   1.421 with more P&L; p=0.15 (1.394) is comparable. Confirms the previous section: you can set the
   distance *directly* and do at least as well as inferring it from ATR — distance is the lever.
2. **But PF rises monotonically with width all the way to p=0.50 (PF 2.08) — which is a DEGENERATE
   objective, not a result.** This is the same trap as the expansion-exit dead-end: *any* exit rule
   that fires fights the momentum edge, so looser always scores higher PF. The wide end isn't a better
   stop, it's the **absence** of one: at p=0.50 the avg hold is **512 bars (~2 yrs)**, only 76% of
   trades exit via the stop (24% MTM), and the worst single trade is −91% (vs −99% theoretical) — i.e.
   buy-and-hold-the-survivors with a catastrophe stop, untradeable on per-trade drawdown / capital at
   risk. At p=0.15 it's still a real system (94.6% stop exits, ~3-month avg hold).

So **PF alone cannot pick the stop width** (it just walks you to no-stop). The realistic sweet spot is
where the stop still does its job — roughly **p=0.15–0.25** — and there fixed-% is competitive with or
slightly better than ATR k=4. Net takeaway of the whole stop investigation: **the distance is the
edge-relevant lever, tight stops whipsaw, and a simple fixed-% ratchet in the 15–25% band is as good
as the fancier mechanisms** — pick the width by drawdown/hold tolerance, not by maximizing PF.

#### Is the dead-zone gap-over underperformance a stop-distance artifact? NO (2026-06-19)

Hypothesis: yesterday's finding that **gap-over** dead-zone trades (signal-bar `adj_open ≥ hi_252_high`)
badly underperform **reclaim** ones (`open < hi_252_high`, close above) might be entirely a *stop*
effect — gap-overs gap up, so a structure-based stop sits at a different distance and whipsaws them.
If so, a **uniform fixed 10% stop** on both should erase the gap. Tested on the up=0 dead zone
(matching yesterday), window-low(4) vs fixed p=0.10:

| up=0 dead zone | window-low(4) PF / net | fixed 10% PF / net |
| --- | ---: | ---: |
| gap-over | 1.159 / $100k | 1.096 / $180k |
| reclaim | 1.318 / $565k | **1.478 / $1.64M** |
| reclaim − gap-over (PF) | +0.16 | **+0.38** |

**The hypothesis is refuted — the gap WIDENS under a fixed stop.** The fixed 10% helps *reclaims* a lot
(1.318→1.478, P&L ~3×) but leaves *gap-overs* flat-to-worse (1.159→1.096). So equalizing the stop
distance does the opposite of closing the gap. Two reasons it isn't a stop artifact:
- Under the structure stop actually used (4-day-low), gap-overs and reclaims **already had near-
  identical stop distances** (median 12.2% vs 13.0%) — there was no distance difference for a fixed
  stop to neutralize. (The mechanism the hypothesis imagined IS real but for the *day-low* stop:
  gap-overs' median day-low distance is 3.83% vs reclaims' 7.78% — gap-overs open high so the day low
  is close. But the day-low stop wasn't the one in play here.)
- A reclaim is a live intraday breakout that **keeps running if given room**, so a wider/up-only stop
  captures more of the move; a gap-over has **front-loaded** its move on overnight news, so room
  doesn't help — there's less continuation left and it often fades.

So the gap-over deficit is a property of the **setup's continuation profile, not the stop.** It is
exactly what an open / early-morning *entry* (not a different stop) would need to address — reinforcing
the deferred intraday-entry test. Reclaims, separately, are the standout beneficiary of the wide
fixed/ratchet stop (the single best dead-zone cell: up=0 reclaim + fixed 10% = PF 1.478 / $1.64M).

**Follow-up: does a 20-day TIME-STOP rescue gap-overs (like it did the tight-stop bucket)? No.**
Applied the same time-stop method (hold 20 bars, exit at the 20th bar's open, MTM if the ticker runs
out) to the up=0 dead-zone **gap-over** universe (n=3665):

| up=0 dead-zone gap-overs (n=3665) | win% | PF | net |
| --- | ---: | ---: | ---: |
| window-low(4) stop | 41.4 | **1.159** | $100k |
| fixed 10% stop | 42.8 | 1.096 | $180k |
| 20-day time stop | 53.8 | 1.125 | $156k |

The time-stop lifts win rate hard (41%→54%, holding through noise instead of stopping out) and beats
window-low on P&L, but **PF stays stuck ~1.12** — no better than the price stops, *below* window-low's
1.159. Era-robust at that mediocre level (2005–14 1.058, 2015–26 1.198), so it's not a hidden regime.
Contrast with yesterday's tight-stop bucket where the time-stop was a ~10× rescue (PF 1.09→1.68): there
the trades were genuine winners being *whipsawed out*; here holding longer just converts more gap-overs
to *small* wins — there's no large continuation to capture (the move was front-loaded overnight). So
across window-low, fixed-%, ATR, AND time-stop the gap-over PF clusters ~1.1: **the exit/stop is not
the lever for gap-overs.** Only an earlier (open / pre-market) *entry* could plausibly help — you'd
have to participate before the overnight move is already priced in.

#### Break-even-capped fixed stop + 20-day time-stop — and it helps gap-overs (2026-06-19)

A combination stop: fixed-% initial stop that ratchets up **only to break-even** (entry), then locks,
paired with a 20-day time-stop to harvest the winner. `StopMode = FixedPctBE p` (CLI `--fixed-stop-be`)
= `stop = max(prev, min(close·(1−p), entry))`; `--max-hold-bars 20`. Rationale: don't give back open
profit beyond BE (unlike the full ratchet), and don't hold past the point where momentum decays
(time-stops >25d are known to fade). Loosened set, vs the full-ratchet comparators:

| loosened set | win% | PF | net |
| --- | ---: | ---: | ---: |
| window-low(4) | 42.5 | 1.352 | $1.29M |
| atr k=4 (full ratchet) | 41.6 | 1.421 | $3.82M |
| fixed p=0.10 (full ratchet) | 42.2 | 1.329 | $2.06M |
| **BE p=0.10 + 20d time-stop** | 47.2 | **1.40** | $1.89M |
| BE p=0.15 + 20d time-stop | 49.7 | 1.39 | $2.06M |

The combo reaches **PF ~1.40 at p=0.10** — beating the equivalent full-ratchet fixed-% (1.329) and
window-low (1.352), and ≈ the full-ratchet ATR k=4 (1.421) — but with a much **higher win rate**
(47–50% vs 42%) and a **modest, honest P&L** ($1.9M, not the wide-ratchet $3.8M). The lower P&L is the
point: it's banking winners at 20 days, not riding the degenerate fat tail, so this PF doesn't depend
on the "hold-forever" artifact that inflated the wide fixed-% sweep.

**Dead-zone split — this is the FIRST treatment that helps gap-overs:**

| up=0 dead zone | gap-over PF | reclaim PF |
| --- | ---: | ---: |
| full-ratchet fixed 10% | 1.096 | **1.478** |
| **BE 10% + 20d time-stop** | **1.224** | 1.385 |
| BE 15% + 20d time-stop | 1.189 | 1.432 |

Capping at break-even + harvesting at 20 days lifts **gap-overs 1.096 → 1.224** (win rate 43%→52%) —
the first thing to move them off ~1.1, because the BE cap stops them round-tripping into losers and the
time-stop banks the front-loaded move before it fades. The trade-off is symmetric: it slightly **lowers
reclaims** (1.478 → 1.385), which *want* room to keep running and get cut short by the BE cap + 20d
harvest. So the two setups have **opposite stop preferences**: reclaims → wide full-ratchet (run);
gap-overs → BE + time-stop (bank it). ⚠️ **Caveat (era split):** the gap-over lift is **modern-era
concentrated** — 2015–26 PF 1.381 vs 2005–14 1.091 — so it's a regime-tilted improvement, not a stable
cross-era edge. Better than the ~1.1 every other stop gave, but not robust; the open-entry idea remains
the cleaner potential fix for gap-overs.

#### Fixed profit TARGETS — do not help (truncate the right tail) (2026-06-19)

Added a fixed profit-target exit (`StopMode` independent; CLI `--profit-target t`): a resting sell
limit at `entry·(1+t)`, fills **intrabar** at `max(target, open)` (gap-up through the limit fills at
the better open, else at the limit). It wins over a same-bar stop (the stop only acts next-open).
Swept 15/20/30/50% on the BE 10% + 20d base (loosened set):

| BE 10% + 20d base | win% | PF | net |
| --- | ---: | ---: | ---: |
| no target | 47.2 | **1.40** | $1.89M |
| + target 15% | 50.6 | 1.352 | $1.57M |
| + target 20% | 49.2 | 1.380 | $1.73M |
| + target 30% | 48.0 | 1.374 | $1.74M |
| + target 50% | 47.5 | 1.390 | $1.83M |

**Every target level lowers both PF and P&L**, monotonically worse the tighter the target (15% →
1.352 / $1.57M); as the target widens toward "off" (50%) it converges back up to the no-target
baseline. Win rate rises (capping books more small wins) but that's the losing trade-off — **the
target truncates the right tail that carries a momentum system.** Same lesson as the expansion exit,
the gap-over time-stop, and the wide-stop PF degeneracy: any rule that caps the *upside* fights the
edge. Fixed targets are a confirmed non-starter for this system — let winners run; manage only the
downside (stop) and the holding period (time-stop).

**Fill convention doesn't matter.** Re-ran the sweep with `--target-next-open` (target hit → exit at
the NEXT bar's open, a signal rather than an intrabar limit): PF 1.350 / 1.379 / 1.363 / 1.375 at
15/20/30/50% — within ≤0.015 of the intrabar-limit version (1.352 / 1.380 / 1.374 / 1.390), and
next-open marginally *worse* (you give a little back on the open). So the negative result is robust to
how the target fills — it's the *capping*, not the fill, that costs. Both conventions stay below the
1.40 no-target base at every level. Engine retains `--profit-target` and `--target-next-open` as the
substrate for this tested negative result.

---

#### Post-exit path study — forward return by EXIT bucket, and ATR%-at-exit is the discriminator (2026-06-19)

The question: after a trade closes, does the *state it closed in* tell us anything about what the
stock does next — i.e. is the −10% stop throwing away a bounce (mean-reversion), and do big winners
keep winning? Measured as the **20-bar forward return from the EXIT price** (the recycle-vs-hold
decision), bucketed by realized gain-at-exit, then cross-cut by ATR%-at-exit.

**System parameters (this study):**
```
side = long              stop mode = fixed-pct ratchet p=0.100   entry-day-stop = true
entry = at-close         time-stop = off    profit target = off   exhaustion = off
entry gates: up>=0.05  rvol[3,20]  adv>=100000  price>=5  52w>=0.95  tight<4.00  atr%<0.11
trips = 18,310   win% 41.7   PF 1.377   net $3.41M     (LOOSENED set: rvol≥3, move≥5%)
```
Forward return = `close[exit_rn+20] / exit_price − 1`, joined per-ticker on `split_adjusted_prices`;
ATR%/tightness at exit reconstructed (log-ATR% and linear tightness over the prior 14 bars).

**By exit bucket** (realized gain at exit):

| exit bucket | n | avg gain@exit | fwd20 mean | fwd20 med | fwd20 PF | win% | tight@exit | ATR%@exit |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| < −10% | 3,055 | −16.6% | −0.39% | −0.24% | **0.936** | 48.7 | 4.76 | 5.79 |
| −10..0% | 7,471 | −5.3% | +0.76% | +0.45% | 1.159 | 51.4 | 4.24 | 4.39 |
| 0..+10% | 3,839 | +4.3% | +1.20% | +1.11% | 1.252 | 53.9 | 4.38 | 4.62 |
| +10..+30% | 2,518 | +17.6% | +1.17% | +0.69% | 1.226 | 51.9 | 4.54 | 5.06 |
| +30%+ | 953 | +57.6% | +1.00% | **−0.68%** | 1.149 | 47.1 | 4.91 | 6.17 |

Two answers fall out: **(1) the −10% stop is neutral going forward** (PF 0.936, mean −0.39%, win
48.7%) — a name that has reverted ≥10% from entry has *no* forward edge, so recycling the capital is
correct and the stop is not sacrificing a bounce. **(2) Winners do NOT keep winning** — forward PF
*peaks in the middle* (0..+10%: 1.252) and decays with the gain; the +30%+ bucket has a **negative
forward median (−0.68%)** and sub-50% win rate. The extended names are the ones that give it back.
Note the loosest base (tight 4.76/4.91) and highest ATR% (5.79/6.17) sit in exactly the two
worst-forward buckets — the loose-base + high-vol exhaustion signature again.

**…but the gain bucket is mostly a proxy. ATR%-at-exit is the real axis** (standalone, ignoring gain):

| ATR%@exit | n | avg gain@exit | fwd20 mean | fwd20 med | fwd20 PF | win% |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| <2% | 731 | −0.2% | +1.38% | +1.70% | **1.551** | 59.1 |
| 2-3% | 3,910 | +1.4% | +1.12% | +1.15% | 1.343 | 55.9 |
| 3-4% | 3,888 | +1.9% | +0.77% | +0.83% | 1.196 | 53.7 |
| 4-5% | 2,672 | +1.5% | +0.66% | +0.53% | 1.136 | 52.0 |
| 5-6% | 2,002 | +0.1% | +0.43% | 0.00% | 1.077 | 50.0 |
| 6-8% | 2,408 | −1.1% | +0.85% | −0.88% | 1.125 | 47.3 |
| 8-10% | 1,176 | +1.2% | +0.66% | −1.65% | 1.084 | 46.3 |
| 10-12% | 562 | +1.9% | −1.34% | −4.03% | **0.879** | 42.9 |
| 12-16% | 312 | +13.0% | +0.40% | −8.04% | 1.032 | 36.5 |
| 16-20% | 42 | +18.0% | −6.66% | −11.79% | **0.622** | 33.3 |
| 20%+ | 23 | +176.5% | −2.76% | −10.95% | 0.871 | 34.8 |

**Forward edge decays monotonically with ATR%-at-exit and dies above ~10%.** Median and win% fall
in lockstep: the *typical* name stops advancing right at **5-6% ATR** (median 0.0, win 50.0) and is
outright negative-EV by **10%+** (PF 0.88, median −4%). The 12%+ cells carry a few fat-tail survivors
that prop up the mean/PF while the median bleeds −8% to −12% — read the median there, not the PF. The
mass is in the healthy 2-5% region (~10.5k of ~18.2k exits); the toxic 10%+ region is thin (~940).

**Cross-cut (gain bucket × ATR%) confirms ATR% dominates:** within *every* gain bucket the <4%-ATR row
is a healthy ~1.25–1.35 forward PF with a positive median, and the 12%+-ATR row collapses to a
negative median (−3% to −13%). The two genuinely toxic cells (PF<1, negative median, real n): **< −10%
& 12%+ ATR** (PF 0.422, median −11.95%, n=108 — a name down ≥10% *and* blown-out keeps falling) and
**+30%+ & 12%+ ATR** (PF 0.964, median −13.18%, n=94 — the extended-and-violent winner reverts hard).

**Takeaways.** The −10% stop is vindicated (neutral forward EV — recycle, don't hold for a bounce).
"Let winners run" is true on average but the *extended-and-high-vol* tail reverts — consistent with
every prior exhaustion finding. And the single most predictive post-exit variable is **ATR%, not the
gain**: low vol → continuation, high vol → reversion, the same volatility axis the entry gate
(atr% < 0.11) already screens on. This is a forward/diagnostic measurement, not a new exit rule.

---

#### ⭐ Regime-switched CHANDELIER stop — raises PF *and* passes the forward-EV test (2026-06-19)

**The principle (from the post-exit study).** Be long while forward-EV is positive; flat when it
turns. Forward-EV is a *decreasing function of ATR%* (≈ +1.7% median at 2% ATR → −4% at 10%+ ATR), so
the EV-zero exit point is a roughly fixed *ATR-dollar* move, not a fixed %. The acceptance test for any
exit: **a forward-PF breakdown of the stop exits should show no bucket above ~1.1** — if it does, we
sold something still trending.

**Two dead-ends first.** (a) A *proportional* ATR-multiple stop (`stop% = k·ATR%`, `--atr-stop`) widens
the leash as vol rises — backwards — already known to only modestly beat window-low. (b) An *inverse*-ATR
ratchet (`stop% = w·atrRef/ATR%`, new `--inv-atr-stop`) tightens continuously as vol rises, but made the
stop-exit forward-PF *worse* (1.21 vs flat-10%'s 1.14): tightening on *every* high-vol name shakes out
the noisy-pullback names that then bounce (the median reverts but there's a fat bounce tail). **A
price-pullback stop of any shape selects for mean-reverters** — it can't get below 1.1.

**The fix — regime-switched chandelier off the running MAX CLOSE** (new `--chandelier-regime wide tight
atrThr`, `StopMode.ChandelierRegime`). One high-water-mark `maxClose` per position; each bar
`width = (ATR% ≥ atrThr ? tight : wide)`, `stop = maxClose·(1−width)`. Quiet names get a *wide* leash
(noise can't shake them); the *instant* ATR% crosses the negative-EV threshold the width snaps tight off
the **peak** (above the old wide line → bites immediately). Path-independent — pure `f(maxClose, regime)`,
no ratchet bookkeeping, no lookahead (mark through B−1, ATR% = the pre-push log-ATR snapshot).

**System parameters:**
```
--chandelier-regime 0.20 0.10 0.10   (20% leash when quiet; 10% off the peak once ATR% ≥ 10%)
side = long   entry-day-stop = true   time-stop = off   profit/exhaustion = off
```

**Forward-PF acceptance test passes** (loosened set, rvol≥3 move≥5%, fwd-20d-from-exit):

| stop | book PF | book net | avg gain@exit | **stop-exit fwd-PF** | fwd-med |
| --- | ---: | ---: | ---: | ---: | ---: |
| flat 10% | 1.377 | $3.41M | +1.4% | 1.141 | +0.47% |
| **chandelier 20/10@10%** | **1.516** | **$8.18M** | **+3.3%** | **1.089** | +0.40% |

The chandelier pushes the stop-exit forward-PF **below the 1.1 neutral line** (1.089) — we stop selling
things that keep going — *while* lifting book PF and letting winners run further (+3.3% vs +1.4% avg gain
at exit). The wide leash holds quiet trenders through noise; the tight-off-peak arm cuts the violent
negative-EV names. First mechanism in this workstream to improve book PF and pass the forward-EV test
together.

**Production gate (rvol[6,20], move≥10%) + breadth (lag-1 pct_above_20 > 0.5), era-split:**

| stop | ALL PF | pre-2015 | post-2015 | ALL net |
| --- | ---: | ---: | ---: | ---: |
| flat 10% | 1.335 | 1.215 | 1.444 | $367k |
| **chandelier 20/10@10%** | **1.419** | **1.391** | 1.445 | **$774k** |

**+0.084 blended PF, 2.1× the P&L, and it RESCUES the weak pre-2015 era** (1.215 → 1.391), pulling the
two eras to near-parity (1.391 / 1.445) vs the flat stop's lopsided split. Robustifies across regime
rather than relying on one — the opposite of an artifact. (Without breadth: prod-gate ALL PF 1.554 vs
1.419; pre/post 1.428/1.440.) Confirmed the default and all pre-existing stop modes are byte-unchanged
(new code only adds match arms). **This is the live candidate to replace the flat/window-low stop.**

---

#### Chandelier LADDER (N-tier) — the high-ATR tiers calibrate; the quiet tier won't (2026-06-19)

Generalized the 2-tier chandelier to an N-tier ATR% ladder (new `StopMode.ChandelierLadder`, CLI
`--chandelier-ladder "thr:w,…,base:w"` — highest matching threshold wins; max-close anchored as before).
Tested the user's 4-tier ladder **8% @ ATR≥10%, 10% @ ≥8%, 12% @ ≥6%, 15% @ <6%**, plus the 2-tier
**12%/8% @ 10%** for reference.

**System parameters / book results:**
```
--chandelier-ladder "0.10:0.08,0.08:0.10,0.06:0.12,base:0.15"
side = long   entry-day-stop = true   time/profit/exhaustion = off
```

| stop | loose PF | loose net | prod PF | prod net |
| --- | ---: | ---: | ---: | ---: |
| chandelier 20/10 @10% | 1.516 | $8.18M | 1.554 | $1.60M |
| chandelier 12/8 @10% | 1.394 | $3.96M | 1.526 | $0.98M |
| ladder 8/10/12/15 | 1.421 | $4.95M | 1.479 | $1.02M |

The narrower-base ladders (15% / 12%) sit below the 20%-base version on book PF — same "wider base leash →
higher book PF" pattern (the wide leash lets winners run).

**Breakdown by WHICH stop fired** (loosened set, fwd-20d from exit; regime reconstructed from
ATR%-at-exit against the ladder thresholds):

| which stop fired | n | avg gain@exit | avg held | ATR%@exit | fwd med | **fwd PF** |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8% leash (ATR≥10%) | 1,001 | +9.7% | 17 | 12.2% | −4.37% | **0.946** |
| 10% leash (ATR 8-10%) | 1,355 | +2.1% | 12 | 8.9% | −1.50% | **1.083** |
| 12% leash (ATR 6-8%) | 2,819 | −0.5% | 25 | 6.9% | −0.76% | **1.090** |
| 15% leash (ATR<6%) | 12,473 | +2.1% | 82 | 3.7% | +0.94% | **1.219** |

(2-tier 12/8 ran the same way: TIGHT 8% → fwd-PF 0.911 / med −5.2%, n=1039; WIDE 12% → fwd-PF 1.193 /
med +0.52%, n=16732 — same split, coarser.)

**The decisive finding:** the **three high-ATR tiers are all calibrated** — fwd-PF 0.946 → 1.083 → 1.090,
each with a *negative* forward median, i.e. they sell names that then fall (the 8% tier catches violent
names at +9.7% avg gain right as they go negative-EV). The laddered tight side works exactly as designed,
monotone in ATR%. **The entire residual edge-leakage AND the entire drawdown live in the low-ATR 15%
tier**: fwd-PF **1.219**, positive median, **71% of all exits (12,473)** held a punishing **82 bars
(~4 months)** — we're stopping quiet trenders that keep going.

**This tier cannot be fixed by the price-stop width** — seen three ways now: 20% base → fwd-PF 1.19, 15%
base → 1.219, 12% → 1.193. The quiet leash trades book-PF against this number but never gets it under
1.1, because *any* price-pullback stop on a quiet trender sells into the bounce (the median reverts but
the bounce tail props the PF). The 82-bar hold confirms these are sideways/up grinders that occasionally
dip into the stop.

**Architecture conclusion:** high-ATR names want a *price* stop (the ladder works); the low-ATR majority
wants a *time/gain*-gated recycle, not a price stop. The right design is a **hybrid** — ladder price-stop
for the volatile tiers + the ATR/gain-gated time-stop (hold while ATR% < ~8% AND gain < +60%; recycle
otherwise) for the quiet names. That is the next build.

---

#### Time-stops with NO price stop — the "hold another 5 days?" map, gated on ATR% (2026-06-19)

The chandelier-stop geometry got complicated and its quiet-name holds ran **9 months** (the wide leash
lets a sideways drifter sit for ~189 bars). Cleaner idea: drop the price stop entirely, use a pure
**time-stop**, and decide *when* to recycle from the forward EV — at the exit, given ATR%, is holding
another 5 days still +EV? If yes, hold; if no, recycle the capital.

**System parameters:**
```
--no-stop --max-hold-bars N   (N ∈ {5,10,15,20,25,30}; the time-stop is the ONLY exit)
side = long   entry-day-stop = false (no-stop)   profit/exhaustion = off
entry gates: up>=0.05  rvol[3,20]  adv>=100000  price>=5  52w>=0.95  tight<4.00  atr%<0.11
LOOSENED set: 18,310 trips
```

**Time-stop sweep (no price stop):**

| hold | PF | net | win% |
| --- | ---: | ---: | ---: |
| 5d | **1.346** | $1.68M | 51.0 |
| 10d | 1.287 | $1.86M | 50.6 |
| 15d | 1.310 | $2.37M | 51.8 |
| 20d | 1.361 | $3.14M | 52.1 |
| 25d | 1.353 | $3.42M | 52.6 |
| 30d | 1.328 | $3.51M | 51.8 |

PF peaks at the short (5d, 1.346) and the 20d hold (1.361); net P&L climbs monotonically with hold
length (more trend captured per trade). No stop needed — the stop was mostly forcing rotation, which the
time-stop does more cleanly. Holds are now *bounded* by construction (the drawdown / 9-month-hold
complaint goes away).

**The "hold another 5 days?" map** — forward-5d return measured FROM each time-stop exit, bucketed by
ATR%-at-exit, one column per hold length. Read the **median** in the high-ATR cells (PF/mean there are
fat-tail-inflated by a few survivors).

Forward-5d **PF** (>1.1 → keep holding; ≤1.0 → recycle):

| ATR%@exit | 5d | 10d | 20d | 30d |
| --- | ---: | ---: | ---: | ---: |
| <4% | 1.073 | 1.231 | 1.148 | 1.120 |
| 4-6% | 1.062 | 1.168 | 1.050 | 0.919 |
| 6-8% | 1.021 | 1.174 | 0.940 | 1.072 |
| 8-10% | 1.141 | 1.038 | 1.100 | 1.045 |
| 10-14% | 0.877 | 0.974 | 0.847 | 1.077 |
| 14%+ | 1.141 | 0.994 | 1.008 | 1.406 |

Forward-5d **MEDIAN %** (the honest read):

| ATR%@exit | 5d | 10d | 20d | 30d |
| --- | ---: | ---: | ---: | ---: |
| <4% | +0.05 | +0.32 | +0.18 | +0.19 |
| 4-6% | +0.09 | +0.28 | 0.00 | −0.17 |
| 6-8% | −0.28 | +0.11 | −0.45 | −0.11 |
| 8-10% | −0.33 | −0.66 | −0.99 | −0.51 |
| 10-14% | −2.14 | −1.15 | −2.14 | −0.41 |
| 14%+ | −5.81 | −0.77 | −3.10 | −0.06 |

Forward-5d **MEAN %** (right-tail visible — diverges from median exactly where the cell is fat-tailed):

| ATR%@exit | 5d (n) | 10d (n) | 20d (n) | 30d (n) |
| --- | ---: | ---: | ---: | ---: |
| <4% | +0.11 (7457) | +0.31 (7104) | +0.24 (10285) | +0.20 (10232) |
| 4-6% | +0.15 (6014) | +0.38 (5864) | +0.14 (4341) | −0.24 (4459) |
| 6-8% | +0.08 (2615) | +0.58 (2750) | −0.24 (1946) | +0.28 (1920) |
| 8-10% | +0.60 (1147) | +0.17 (1276) | +0.51 (799) | +0.21 (766) |
| 10-14% | −0.78 (803) | −0.15 (885) | −1.02 (540) | +0.41 (488) |
| 14%+ | +1.38 (131) | −0.05 (261) | +0.07 (187) | +2.54 (179) |

**Findings:**
1. **Only the quiet <4% bucket is reliably worth holding at every horizon** — forward PF 1.07→1.23→1.15
   →1.12, positive median throughout. Quiet trenders keep grinding up; this is the core hold.
2. **Everything ≥10% ATR is negative-EV by median at essentially every hold length** (10-14% medians
   −2.14/−1.15/−2.14/−0.41; 14%+ medians −5.81/−0.77/−3.10). The occasional high PF/mean is one name
   ripping while the typical one bleeds. **Recycle high-ATR names regardless of the clock.**
3. **The mid buckets (4-10%) decay with hold length** — fine at 5-10d (PF 1.04-1.17), but the 4-6% and
   6-8% rows roll under 1.0 by 20-30d. The longer you've held, the lower the ATR% at which holding stops
   paying.

The clean separator across all horizons is **~8-10% ATR**: below it the median stays non-negative at
short holds and the <4% band stays positive everywhere; above it the median is negative everywhere. This
is the substrate for a **conditional / ATR-gated time-stop** — a base time-stop that extends only while
the name is quiet, and cuts early (independent of the clock) once ATR% is high. The second axis
(gain-from-entry, treated separately) is the next breakdown before designing that rule.

**The second marginal — gain-from-entry — is a MUCH weaker discriminator than ATR%** (same exits, same
forward-5d measure):

Forward-5d **PF** by gain-from-entry:

| gain@exit | 5d | 10d | 20d | 30d |
| --- | ---: | ---: | ---: | ---: |
| < −20% | 0.772 | 1.054 | 0.989 | 1.065 |
| −20..−10% | 1.019 | 1.091 | 0.918 | 1.073 |
| −10..0% | 1.120 | 1.129 | 1.144 | 1.059 |
| 0..+10% | 1.020 | 1.202 | 1.145 | 1.077 |
| +10..+30% | 0.972 | 1.084 | 0.950 | 1.014 |
| **+30..+60%** | **1.304** | **1.236** | **1.165** | 1.032 |
| +60%+ | 0.531 | 0.907 | 0.848 | 0.926 |

Forward-5d **MEDIAN %** by gain-from-entry:

| gain@exit | 5d | 10d | 20d | 30d |
| --- | ---: | ---: | ---: | ---: |
| < −20% | −1.02 | −0.66 | −0.68 | +0.13 |
| −20..−10% | −0.24 | +0.17 | −0.14 | +0.17 |
| −10..0% | +0.15 | +0.28 | +0.27 | +0.16 |
| 0..+10% | −0.01 | +0.26 | +0.13 | +0.16 |
| +10..+30% | −0.62 | −0.06 | −0.14 | −0.08 |
| +30..+60% | −1.18 | −0.02 | −0.35 | 0.00 |
| +60%+ | −4.42 | +0.52 | −1.56 | −1.04 |

**Findings on the gain axis:**
1. **The middle is flat & mildly positive** — the −10..+10% buckets hold ~80% of exits (≈15k of 18.3k)
   at PF 1.02-1.20, small positive medians; no actionable signal.
2. **The tails bite opposite to a naïve "cut losers / ride winners" rule.** The big-winner tail
   (**+60%+**) is *toxic* — PF 0.53/0.91/0.85/0.93, median −4.42% at 5d (extended names revert). The
   big-loser tail (**< −20%**) is *neutral*, even mildly positive by 30d (median +0.13) — no
   continued-bleed edge from cutting losers fast (echoes the "−10% stop is neutral" finding).
3. **The +30..+60% band is the one winner zone worth holding longer** — PF 1.304/1.236/1.165 with
   ~flat medians; a strong, durable continuation cohort distinct from the +60%+ blow-offs.

**Net:** gain-from-entry is a blunt knife — it mostly re-expresses ATR% (a stock can't reach +60% without
being volatile), plus a real "+30-60% = keep holding" pocket. **ATR% stays the primary gate;** the gain
axis adds a "cut if +60%+ / hold if +30-60%" nuance. Whether the +60% toxicity is independent of ATR% or
just ATR% in disguise is settled by the 2D gain×ATR cross (next).

**2D gain×ATR cross** (all four hold lengths pooled for cell counts; forward-5d-from-exit):

PF — rows gain-from-entry, cols ATR%@exit:

| gain ↓ / ATR → | <4% | 4-6% | 6-8% | 8-10% | 10-14% | 14%+ |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| < −10% | 1.25 | 0.94 | 1.02 | 1.00 | 0.89 | 1.26* |
| −10..0% | 1.15 | 1.14 | 1.07 | 1.02 | 0.96 | 0.63 |
| 0..+10% | 1.13 | 1.08 | 1.03 | 1.29 | 0.99 | 1.49* |
| +10..+30% | 1.03 | 0.94 | 1.16 | 1.04 | 0.88 | 0.70 |
| **+30..+60%** | **1.42** | 1.15 | 0.88 | 1.30 | 1.05 | 1.53* |
| +60%+ | 0.08* | 0.41 | 0.96 | 0.89 | 0.93 | 0.81 |

MEDIAN % — same axes:

| gain ↓ / ATR → | <4% | 4-6% | 6-8% | 8-10% | 10-14% | 14%+ |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| < −10% | +0.44 | 0.00 | −0.20 | −0.58 | −1.96 | 0.00 |
| −10..0% | +0.23 | +0.31 | −0.09 | −0.78 | −0.72 | −5.92 |
| 0..+10% | +0.15 | +0.11 | 0.00 | −0.13 | −1.37 | +0.29 |
| +10..+30% | +0.03 | −0.36 | 0.00 | −1.28 | −1.33 | −6.65 |
| +30..+60% | +0.30 | +0.04 | −0.86 | −0.12 | −1.35 | −1.28 |
| +60%+ | +0.12 | −0.62 | −2.17 | −1.04 | −0.60 | −2.79 |

n — same axes (`*` cells above are the small-n ones):

| gain ↓ / ATR → | <4% | 4-6% | 6-8% | 8-10% | 10-14% | 14%+ |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| < −10% | 2300 | 3224 | 2348 | 1254 | 950 | 312 |
| −10..0% | 14119 | 6822 | 2382 | 815 | 417 | 51 |
| 0..+10% | 15084 | 6784 | 2167 | 769 | 385 | 68 |
| +10..+30% | 3396 | 3374 | 1832 | 739 | 501 | 90 |
| +30..+60% | 173 | 448 | 423 | 316 | 294 | 111 |
| +60%+ | 6 | 26 | 79 | 95 | 169 | 126 |

**The cross resolves it:**
1. **ATR% is a genuine, independent discriminator** — the PF gradient falls left→right within almost
   every gain row (cleanest in −10..0%: 1.15→1.14→1.07→1.02→0.96→0.63). Not a gain proxy.
2. **+30..+60% is the strongest hold cohort** and confirmed separable — PF 1.42 at <4% ATR, staying
   ≥1.05 out to 10-14%. A stock up 30-60% that is *still quiet* is the best thing to keep in the table.
3. **+60%+ toxicity is its OWN signal, not ATR% in disguise** — that row is sub-1 across *every* ATR
   column (0.41/0.96/0.89/0.93/0.81), so "up +60%" carries reversion beyond volatility.

**Combined map → the rule:** **hold while ATR% < ~8-10% AND gain < +60%; recycle otherwise** (ATR% ≥ 10%
or gain ≥ +60%). The +30-60% band rides *because* it is usually still quiet (it lives in the low-ATR
cells), not as a separate clause. This is the spec for the conditional time-stop to build next.

---

#### ⭐ On HIGH-EDGE breakouts the edge is concentrated in the first ~5 days (2026-06-19)

Hypothesis: the ATR%<6% quiet names are just low-edge grinders, and the real edge of this system is the
breakout *pop* in the first week. Test it on the high-edge population where the signal is strongest —
**production gate (rvol[6,20], move≥10%) + breadth (lag-1 pct_above_20 > 0.5)**, no price stop.

**System parameters:**
```
--no-stop --max-hold-bars N   (N ∈ {5,10,15,20,25,30})
entry gates: up>=0.10  rvol[6,20]  adv>=100000  price>=5  52w>=0.95  tight<4.00  atr%<0.11
+ breadth lag-1 pct_above_20 > 0.5 (post-hoc)   ≈ 2,230 trips
```

**Breadth-filtered time-stop sweep:**

| hold | n | PF | net | win% |
| --- | ---: | ---: | ---: | ---: |
| **5d** | 2,233 | **1.859** | $501k | 53.2 |
| 10d | 2,227 | 1.847 | $658k | 53.5 |
| 15d | 2,224 | 1.668 | $626k | 53.9 |
| 20d | 2,222 | 1.654 | $700k | 54.6 |
| 25d | 2,216 | 1.596 | $713k | 53.7 |
| 30d | 2,208 | 1.606 | $787k | 52.7 |

**Segment decomposition** — split each 30d-hold trade's P&L into its first-5-bar segment (entry → close
at +5) vs the remainder (+5 → exit), PF each:

| segment | n | net $ | PF | share of P&L |
| --- | ---: | ---: | ---: | ---: |
| **first 5 bars** | 2,208 | $477k | **1.827** | **61%** |
| rest (+5 → exit) | 2,208 | $309k | 1.235 | 39% |
| whole trade | 2,208 | $787k | 1.606 | 100% |

**Confirmed — and the contrast with the loosened set is the whole point:**

| population | first-5 PF | rest PF | spread | first-5 share |
| --- | ---: | ---: | ---: | ---: |
| **high-edge** (rvol≥6, move≥10%, breadth) | **1.827** | 1.235 | **0.59** | 61% |
| loosened (rvol≥3, move≥5%) | 1.319 | 1.169 | 0.15 | 48% |

On the loosened set the first-5 and the rest are barely separable (1.32 vs 1.17) — which is why that
time-stop sweep was flat (1.35→1.36). On the **high-edge** breakouts the first 5 days are PF **1.83** and
the remainder collapses to **1.235** — a 0.59 spread, with the first week carrying **61% of the P&L on
~17% of the holding time**. The edge is the breakout *pop*; everything after is the low-edge grind (the
ATR%<6% quiet names held ~82 bars are exactly that). The "rest" isn't negative — just dilutive (drags the
blend from 1.86 → 1.61). So a **5-day time-stop captures the concentrated edge (PF 1.86) and recycles
capital ~6× faster** than a 30-day hold; holding longer earns more total dollars at a worse rate (the
capture-vs-efficiency tradeoff, now quantified on the population that matters).

---

#### ⭐ NEW DEFAULT — 5-day time-stop, NO price stop; the disaster exit is a SHORT setup (2026-06-19)

> **Superseded ceiling (2026-06-20):** the PF figures in this section (1.775 no-breadth; 1.859/1.881/1.848
> filtered, 2,233 trips) are the **tight < 4.0** numbers, which was the default *when this was written*. The
> tightness ceiling was later raised to **4.5** (the clean half of the capacity gain — see "The gate
> matters" in the filter-ceiling sweep below): the current default is **2,780 trips, PF 1.795, +$580k**
> (pre 1.769 / post 1.808). The 5d-time-stop / no-price-stop / ATR% < 0.11 facts in this section all still
> hold; only the tightness ceiling and the headline trip/PF numbers changed.

Acting on the whole stop-mechanics arc: **the new production default is a 5-bar time-stop with no
trailing price stop** (`defaultConfig`: `StopMode = NoStop`, `MaxHoldBars = 5`). Rationale, all
established above: moving stops around doesn't help; too-tight stops hurt; the edge is the breakout pop in
the first ~5 days; price stops only earn their keep at ATR% > ~8-10%. Simple, legible, and the best PF of
anything tested.

**New default (no flags), production gate:** stop = none, time-stop = 5d, **PF 1.775**, $738k, win 53.7%.
**+ breadth (lag-1 pct_above_20 > 0.5), era-split:**

| era | n | PF | net |
| --- | ---: | ---: | ---: |
| ALL | 2,233 | **1.859** | $501k |
| pre-2015 | 991 | 1.881 | $172k |
| post-2015 | 1,242 | 1.848 | $329k |

Near-identical across eras — no regime dependence, and well above every stop-based variant.

**The conditional DISASTER exit — tested, OFF by default, but a real SHORT signal.** Added
`StopMode`-independent `DisasterConfig` (CLI `--disaster-exit --disaster-atr --disaster-loss`): close at
next open when a held bar is BOTH volatile (**current-bar** log-ATR% > 0.10) AND under water (gain <
−0.10) — the one outright-negative-EV corner from the 2D gain×ATR grid. (Current-bar ATR% is fair: it's a
close-of-bar decision filling at the next open, no lookahead.)

As a *long exit it is redundant* under the 5d hold (PF 1.775 → 1.770 at ATR>10%, 1.765 at ATR>8% — all
slightly worse). It fires 128× and every one is a loss *by construction* (it only triggers on losers). The
real test is the **forward return of the names it cut**:

| disaster exits | n | fwd mean | fwd med | fwd PF |
| --- | ---: | ---: | ---: | ---: |
| +5d | 127 | −0.16% | −0.63% | 0.974 |
| +10d | 127 | −2.51% | −4.91% | **0.703** |
| +20d | 127 | +0.70% | −4.13% | 1.072 |

So the disaster names **do keep falling** — fwd-10d PF **0.703**, median **−4.9%**. The signal is correct;
it's just redundant as a *long* exit because the 5d time-stop already caps the exposure window before the
−4.9% continuation compounds (at +5d it's only ~neutral, 0.974 — the bar or two we'd save isn't worth it).
**But fwd-10d PF 0.70 / −4.9% median is a genuine SHORT setup** — a blown-out, under-water ex-momentum
name. Caveat (per the short-side policy): borrow cost + spike risk make these a daytrade/short-horizon
idea, not an overnight hold. The disaster-exit code is kept off-by-default as the substrate for that study.

**Engine note:** flipping `defaultConfig.StopMode → NoStop` also required the CLI no-stop-flag fallback to
read `defaultConfig.StopMode` (was hardcoded `WindowLow`); confirmed the no-flag run now prints
`stop mode = none` and matches the pure-timestop-5 number (PF 1.775).

---

#### ⭐ ATR% × tightness grid under the TIME-STOP — the quiet/tight corner is the book, not a dead zone (2026-06-20)

> **Why this matters.** Under the old trailing-stop systems (window-low, ATR%-ratchet) we concluded that
> very-low-ATR% / very-low-tightness names "had no edge beyond a certain point." This re-test shows that
> conclusion was an **artifact of the stop being too tight on quiet names** — the stop sat *inside* their
> own noise band and chopped them out before the move played. Swap the trailing stop for the 5-day
> time-stop (current default) and those same quiet/tight names become the **core profit engine**. The
> entry filters should cut the *opposite* corner (high-ATR% + loose base), not the quiet one.

**Population:** default exit (5d time-stop, NO price stop), entry filters *loosened to populate the grid*
(`--rvol-min 3 --rvol-max 20 --up-threshold 0.05 --max-atr-pct 100 --max-tightness 100`), breadth applied
post-hoc (`LAG(pct_above_20) > 0.5`). 35,858 raw trips → ~26.5k after breadth. ATR%/tightness are the
engine's **log-space** values (ATR% median 0.038, tightness median 3.9). PF = Σwin/|Σloss| over net P&L.

**ATR% marginal** — edge is *highest* at the bottom and decays monotonically; only the `14%+` tail is negative:

| ATR% (log) | n | win% | mean $ | med $ | PF | total $ |
|---|--:|--:|--:|--:|--:|--:|
| <4%    | 12,631 | 50.2 | 73 | 4 | **1.371** | +928k |
| 4–6%   | 5,448 | 50.9 | 117 | 18 | **1.375** | +640k |
| 6–8%   | 2,374 | 46.6 | 106 | −87 | 1.223 | +253k |
| 8–10%  | 1,262 | 47.3 | 172 | −91 | 1.259 | +216k |
| 10–14% | 1,114 | 41.6 | 47 | −368 | 1.05 | +52k |
| **14%+** | 784 | 30.9 | −694 | −1273 | **0.615** | **−544k** |

> This **directly reverses** the trailing-stop-era read. The quiet `<6%` ATR% names (73% of all trips) are
> the strongest cells, not the weakest. They were never edge-less — they were stop-victims.

**Tightness marginal** — tighter is better, monotonically; edge dies above ~5.5 (looser = worse):

| tightness (log) | n | win% | mean $ | med $ | PF | total $ |
|---|--:|--:|--:|--:|--:|--:|
| <2.5    | 1,123 | 46.9 | 22 | −31 | 1.087 | +24k |
| 2.5–3.5 | 6,824 | 50.9 | 133 | 12 | **1.481** | +910k |
| 3.5–4.5 | 6,655 | 50.8 | 102 | 13 | 1.353 | +679k |
| 4.5–5.5 | 4,386 | 48.9 | 105 | −10 | 1.282 | +459k |
| 5.5–7   | 3,117 | 45.1 | −15 | −83 | 0.969 | −47k |
| **7+**  | 1,508 | 39.7 | −320 | −228 | **0.654** | **−482k** |

> Peak is the **tight** `2.5–3.5` cell (PF 1.481). The ultra-tight `<2.5` column is weak/noisy (small n,
> barely-moved names) — don't over-read it. The real loser is the loose `7+` tail.

**2D PF grid — ATR% (rows) × tightness (cols):**

| ATR% ↓ \ tight → | <2.5 | 2.5–3.5 | 3.5–4.5 | 4.5–5.5 | 5.5–7 | 7+ |
|---|--:|--:|--:|--:|--:|--:|
| **<4**    | 1.01 | **1.47** | 1.18 | **1.83** | 1.17 | 1.15 |
| **4–6**   | 1.18 | 1.38 | **1.58** | 1.40 | 1.26 | 0.97 |
| **6–8**   | 1.29 | 1.38 | **1.68** | 1.03 | 0.88 | 0.89 |
| **8–10**  | 2.37* | 2.18* | 1.36 | 1.12 | 0.96 | 0.65 |
| **10–14** | 0.52* | 1.19 | 1.34 | 0.92 | 0.92 | 1.03 |
| **14+**   | 0.24* | 1.70* | 0.93 | 0.93 | 0.63 | **0.38** |

`*` = thin cell (n < 50); ignore. Cell **n** and **total $** below.

**n grid:**

| ATR% ↓ \ tight → | <2.5 | 2.5–3.5 | 3.5–4.5 | 4.5–5.5 | 5.5–7 | 7+ |
|---|--:|--:|--:|--:|--:|--:|
| **<4**    | 769 | 4308 | 3893 | 2149 | 1174 | 338 |
| **4–6**   | 235 | 1530 | 1527 | 1061 | 790 | 305 |
| **6–8**   | 82 | 560 | 624 | 520 | 392 | 196 |
| **8–10**  | 20 | 243 | 294 | 287 | 293 | 125 |
| **10–14** | 12 | 143 | 235 | 231 | 283 | 210 |
| **14+**   | 5 | 40 | 82 | 138 | 185 | 334 |

**total $ grid:**

| ATR% ↓ \ tight → | <2.5 | 2.5–3.5 | 3.5–4.5 | 4.5–5.5 | 5.5–7 | 7+ |
|---|--:|--:|--:|--:|--:|--:|
| **<4**    | 2k | **+395k** | +133k | **+338k** | +48k | +12k |
| **4–6**   | +13k | +183k | **+252k** | +125k | +70k | −4k |
| **6–8**   | +8k | +95k | **+178k** | +8k | −24k | −12k |
| **8–10**  | +10k | +176k | +59k | +23k | −8k | −42k |
| **10–14** | −4k | +21k | +66k | −20k | −18k | +6k |
| **14+**   | −5k | +40k | −9k | −14k | −114k | **−442k** |

**Verdict:**
- **The mass and the money live in the quiet/tight top-left quadrant** (ATR% < 6%, tightness 2.5–4.5):
  `<4% × 2.5–3.5` (n=4308, PF 1.47, +$395k) and `<4% × 4.5–5.5` (PF 1.83, +$338k) are the two biggest
  P&L cells; `4–6% × 3.5–4.5` (+$252k) and `6–8% × 3.5–4.5` (PF 1.68) round it out. This is precisely the
  region the trailing stops killed.
- **The loss engine is the high-ATR% + loose-base corner.** The single `14%+ × 7+` cell is **−$442k / PF
  0.38** — essentially the entire system loss. The whole bottom row (ATR% 14+) and right column
  (tightness 7+) are where the cut belongs.
- **Implication for the filters:** the current `MaxAtrPct = 0.11` cap is roughly right (it trims the
  worst of the 14%+ row); a `tightness < ~7` cap would shed the loose-base loser without touching the
  core. There is **no case for an ATR%-floor or tightness-floor** — the quiet/tight cells are the edge.
- **Why it reverses the old read:** a trailing stop on a 3% -ATR name triggers on a single average down-day;
  the time-stop simply holds the 5-day window and lets the breakout resolve. Quiet names were never
  edge-less — the exit was eating them.

**Era split (pre/post 2015-01-01) — the pattern is era-robust, not a single-regime artifact:**

| ATR% | n pre | PF pre | n post | PF post |   | tightness | n pre | PF pre | n post | PF post |
|---|--:|--:|--:|--:|---|---|--:|--:|--:|--:|
| <6%    | 9011 | **1.528** | 9068 | 1.241 |   | <3.5    | 3565 | **1.613** | 4382 | 1.323 |
| 6–10%  | 1048 | 1.307 | 2588 | 1.215 |   | 3.5–5.5 | 4940 | 1.528 | 6101 | 1.218 |
| 10–14% | 220 | 1.10 | 894 | 1.04 |   | 5.5–7   | 1318 | 1.224 | 1799 | 0.866 |
| 14%+   | 107 | **0.485** | 677 | **0.63** |   | 7+      | 563 | **0.714** | 945 | **0.635** |

Both eras: monotone decay with ATR%, monotone decay with looseness, the quiet/tight cells strongest and
the high-ATR%/loose tail the only sub-1.0 cell. The reversal of the old trailing-stop conclusion is not a
regime fluke.

---

#### Filter-ceiling sweep — the current ATR% < 0.11 / tight < 4.0 ceilings are already on the post-2015 optimum (2026-06-20)

> Follow-up to the grid above: given that the quiet/tight corner is the book, *should we move the entry
> ceilings?* Tested candidate ATR% and tightness ceilings on the **loose gate** (price% ≥ 0.05, rvol [3,20],
> breadth on — same population as the grid). The answer is **no — the current ceilings are the optimum.**
> Both axes were swept; mean-$/trade is the quality tell (PF can be lifted by a few fat-tail survivors).

**ATR% ceiling sweep (holding tight < 5.5)** — total P&L *peaks at 0.11* and declines on both sides:

| ATR% ceiling | n | PF | total $ | mean $ |
|---|--:|--:|--:|--:|
| < 0.08 | 17,258 | 1.406 | +1.73M | 100 |
| < 0.10 | 18,102 | 1.418 | +2.00M | 110 |
| **< 0.11** | 18,349 | **1.423** | **+2.09M** | **114** |
| < 0.12 | 18,539 | 1.403 | +2.07M | 112 |
| < 0.13 | 18,648 | 1.394 | +2.06M | 110 |
| < 0.14 | 18,723 | 1.386 | +2.06M | 110 |

Below 0.11 you cut the productive 8–10% ATR band (P&L drops); above 0.11 you add net-negative 12–14% names
(PF, total $ AND mean-$ all drop — going 0.11→0.12 adds 190 trips but *loses* $22k). **0.11 is a clean
interior optimum on both sides.** Consistent with the grid: 10–14% is break-even, 14%+ is the −$442k loss
engine.

**Tightness ceiling sweep (holding ATR% < 0.11)** — *non-monotonic*, and that's the trap:

| tight ceiling | n | PF | total $ | mean $ |
|---|--:|--:|--:|--:|
| **< 4.0 (current)** | 11,375 | **1.441** | +1.29M | 114 |
| < 4.5 | 14,245 | 1.432 | +1.59M | 111 |
| < 5.0 | 16,573 | 1.399 | +1.75M | 105 |
| < 5.5 | 18,349 | 1.423 | +2.09M | 114 |
| < 6.0 | 19,602 | 1.400 | +2.16M | 110 |

The < 5.5 ceiling *looks* like a free +62% P&L at flat mean-$ (1.423 vs 1.441). But it's non-monotone — PF
dips at 5.0 then recovers at 5.5 — which means a sub-band is carrying it. The **non-cumulative tightness
bands** (ATR% < 0.11) expose the lump:

| tightness band | n | PF | mean $ |
|---|--:|--:|--:|
| < 4.0 | 11,375 | 1.441 | 114 |
| 4.0–4.5 | 2,870 | 1.395 | 102 |
| **4.5–5.0** | 2,328 | **1.231** | **70** ← weak |
| **5.0–5.5** | 1,776 | **1.608** | **194** ← spike |
| 5.5–6.0 | 1,253 | 1.141 | 49 |
| 6.0+ | 2,547 | 0.978 | −9 |

The entire < 5.5 case rests on the **5.0–5.5 spike (PF 1.608, mean $194)** — sandwiched next to the *worst*
qualifying band (4.5–5.0). To reach the spike you must swallow the dip.

**⛔ Era split kills the spike — it's a pre-2015 artifact:**

| tightness band | n pre | PF pre | mean pre | n post | PF post | mean post |
|---|--:|--:|--:|--:|--:|--:|
| < 4.0 | 5228 | 1.564 | 123 | 6147 | **1.363** | 105 |
| 4.0–4.5 | 1342 | 1.386 | 83 | 1528 | 1.401 | 119 |
| 4.5–5.0 | 1087 | 1.273 | 70 | 1241 | 1.203 | 70 |
| **5.0–5.5** | 784 | **2.205** | **336** | 992 | **1.232** | **82** |
| 5.5–6.0 | 554 | 1.363 | 106 | 699 | 1.012 | 5 |
| 6.0+ | 1169 | 1.10 | 35 | 1378 | 0.908 | −47 |

The 5.0–5.5 spike is **PF 2.205 / mean $336 pre-2015** but reverts to **PF 1.232 / mean $82 post-2015** —
*below* the core < 4.0 band's post-2015 PF of 1.363. Cumulatively, loosening 4.0 → 5.5 leaves pre-2015 PF
flat (1.564 → 1.568) but **degrades post-2015 PF (1.363 → 1.331)** — it makes the system worse in the only
era that matters for live trading. Every loose band (4.5–5.0, 5.5–6.0, 6.0+) is sub-1.4 or negative
post-2015; there is **no robust loose-tightness edge in the modern era.**

> **⚠️ This loose-gate verdict does NOT carry to the production gate — see below.** On the loose gate the
> 5.0–5.5 spike reverts to junk post-2015, so loosening *there* is a mirage. But the production move/rvol
> floor cleans up exactly those names, and on the gate that actually ships the loosening holds.

**The gate matters — on the PRODUCTION gate, tight < 5.5 holds out-of-sample (2026-06-20).** Re-ran the
tightness decision on the real default gate (price% ≥ 0.10, rvol [6,20], ATR% < 0.11, breadth, ≥2005):

| tightness | n | PF | total $ | mean $ | PF pre | PF post |
|---|--:|--:|--:|--:|--:|--:|
| **< 4.0** (old default) | 2,233 | **1.859** | +501k | 225 | 1.881 | **1.848** |
| **< 4.5** (NEW default) | 2,780 | 1.795 | +580k | 209 | 1.769 | **1.808** |
| < 5.5 (max-capacity) | 3,550 | 1.711 | +701k | 197 | 1.775 | 1.678 |

Non-cumulative bands, **production gate**, with era split — the 5.0–5.5 band that reverted to junk on the
loose gate stays *healthy* here:

| tightness band | n | PF | mean $ | PF pre | PF post |
|---|--:|--:|--:|--:|--:|
| < 4.0 | 2,233 | 1.859 | 225 | 1.881 | 1.848 |
| 4.0–4.5 | 547 | 1.538 | 143 | 1.355 | 1.643 |
| **4.5–5.0** | 446 | 1.343 | 117 | 1.589 | 1.222 ← soft spot |
| **5.0–5.5** | 324 | 1.660 | 212 | 2.106 | **1.454** ← still good post-2015 |
| 5.5+ | 681 | 1.121 | 58 | 1.206 | 1.080 |

Compare the 5.0–5.5 band across gates: **loose gate** PF 2.205 pre → **1.232** post (reverts); **production
gate** PF 2.106 pre → **1.454** post (holds). The move/rvol floor is what keeps the loose-tightness names
clean. The 4.5–5.0 band is the soft spot (post 1.222) we swallow to reach the good 5.0–5.5 band, but the
*aggregate* < 5.5 survives out-of-sample (post-2015 PF 1.678, mean $197).

**✅ DECISION (2026-06-20): RAISE the default tightness ceiling 4.0 → 4.5 — the clean half of the capacity
gain.** The bands above show the increments aren't uniform: the **4.0–4.5 band is clean** (post-2015 PF
1.808 ≈ the < 4.0 core's 1.848) but the **next band 4.5–5.0 is the soft spot** (post-2015 PF 1.222). So 4.5
takes **+24% trips (2,233 → 2,780) and +$79k P&L for almost no quality loss** (PF 1.859 → 1.795, post-2015
1.848 → **1.808**, mean $225 → $209) — and *stops before the drag*. `MaxTightness = 4.5` in `defaultConfig`
(engine verified: 2,780 filtered trips / PF 1.795 / +$579,714).
**Why not 5.5:** it adds more capacity (3,550 trips, +$701k) but pays for it — post-2015 PF 1.678, mean $197
— by carrying the 4.5–5.0 soft band *plus* the spiky 5.0–5.5 one (PF 1.45 post, but pre 2.11 = mostly an
early-era artifact even on the prod gate). 5.5 stays documented as the **max-capacity** alternative; **< 4.0**
as the **max-PF / min-drawdown** alternative (PF 1.859, post 1.848).
**ATR% < 0.11 stays put** — clean interior optimum; tightening it (toward 0.06–0.08) is strictly worse
(cuts the time-stop-rescued 8–10% band).

> **⏭️ MOVED TO v3.** The clipped re-derivation of the tightness & ATR% ceilings (2026-06-21) — the
> tightness *hump* peaking at 4.5, the ATR% tightening 0.11 → **0.10**, and the 2D joint-ceiling grid —
> now lives in [`momentum_v3_results.md`](momentum_v3_results.md) § *Entry-filter geometry*, computed under
> the +50% winner-clip / cumulative standard. The v2 tables above are the raw-`net_pnl` originals; the
> clipped versions superseded the ATR% decision (default is now **0.10**, not 0.11).

---

#### ⭐ Past-runner personality — a 6-month volatility/momentum HISTORY predicts the next breakout (2026-06-20)

> ⚠️ **READ THIS FIRST (caveat added 2026-06-21).** The PF figures below are **RAW** (no winner-clip), and the **monotone
> "higher is better, top bucket PF ~3" reads as much cleaner than it trades.** A later investigation
> ([`momentum_v3_results.md` § past-runner](momentum_v3_results.md)) reproduced this section *exactly* and then dissected
> it. Two things every first-time reader should know:
> 1. **The TOP buckets are lottery-driven, NOT well-distributed.** In the 15%+ ADR / 130%+ ret buckets **one trade carries
>    ~46% / ~64% of the bucket's entire winning profit**; ex-top-1 PF falls 3.0 → 1.6 / 3.6 → 1.3, and ex-top-5 the
>    ret-top bucket is a *net loser* (0.82). The monotone PF ladder is really a monotone **concentration** ladder — the
>    *base* (ex-top-5) PF is flat-to-declining across buckets. High past-vol means **a fatter right tail**, not a better
>    typical trade. So the headline PF ~3 is **not a sizing-into-able expectation** — it's a convexity/tail bet that needs
>    many-tiny-bets sizing (bubble upside).
> 2. **The clean monotonicity is fragile to the population.** It only holds on *this* run (rvol [6,20], no heat filter).
>    Lowering the rvol floor below 6 re-admits extreme lottery winners that spike individual buckets (one +2,368% trade),
>    and the v3 production **heat** filter over-cuts the profitable 11-15% band — so the ladder looks non-monotone under
>    v3 production. **The durable, tradeable part is the FLOOR** (dead-quiet-base names are uniformly bad and *that* is
>    well-distributed) — now shipped as the v3 `max log ATR ≥ 0.04` filter. The "higher is always better" framing is not.
>
> **Thesis (Tim Sykes / chart-reading lore):** momentum stocks keep being momentum stocks; boring stocks
> stay boring. Test it directly — for each entry, measure the stock's volatility/momentum *personality*
> over the **trailing 6 months (126 trading days)**, then break the default-system trips down by it. The
> measures are the trailing-126d **max of a 14-day window stat**, sampled **as-of the signal date and
> lagged 1 bar** (no lookahead — the signal bar's own range is excluded). Three measures:
> - **max ADR 6mo** = max over 126d of `mean₁₄(high/low − 1)` (range-based, the literal Sykes ADR)
> - **max ATR% 6mo** = max over 126d of `mean₁₄(true_range/close)` (gap-aware cousin of ADR)
> - **max ret 6mo** = max over 126d of `close_t/close_{t−14} − 1` (directional 14d burst — the "slope")
>
> Population: default trips (5d time-stop, tight < 4.5, ATR% < 0.11), breadth on, ≥2005 (~2,580 trips).

**All three sort the book monotonically — strongly. The verdict: yes, past runners run again.**

| max ADR 6mo | n | win% | mean $ | PF |   | max ret 6mo | n | win% | mean $ | PF |
|---|--:|--:|--:|--:|---|---|--:|--:|--:|--:|
| <4%    | 498 | 49.8 | −8 | **0.955** |   | <15%    | 417 | 52.3 | 77 | 1.428 |
| 4–6%   | 796 | 52.6 | 76 | 1.345 |   | 15–30%  | 1100 | 53.4 | 106 | 1.481 |
| 6–8%   | 569 | 54.7 | 187 | 1.821 |   | 30–50%  | 705 | 54.8 | 194 | 1.671 |
| 8–11%  | 427 | 55.3 | 221 | 1.703 |   | 50–80%  | 354 | 52.0 | 276 | 1.832 |
| 11–15% | 237 | 53.6 | 382 | 1.978 |   | 80–130% | 118 | 55.1 | 520 | 2.548 |
| **15%+** | 253 | 53.4 | **918** | **3.126** |   | **130%+** | 86 | 41.9 | **1,579** | **3.599** |

(max ATR% 6mo is near-identical to ADR: PF 1.11 → 1.26 → 1.66 → 1.75 → 2.19 → **2.91**.)

- The **bottom ADR/ATR bucket is dead** (PF 0.955 / mean −$8) — stocks with no volatile fortnight in the
  prior 6 months are untradeable under our system. The top bucket is **PF 3.13 / $918 a trade**.
- **max ret (the slope) is the most interesting measure** — even its *bottom* bucket is profitable
  (PF 1.428: a clean directional run leaves edge even when the range was tame), and its top bucket is the
  strongest of all (**PF 3.60 / $1,579**). It captures directional momentum personality specifically.
- **Fat-tail caveat:** the top buckets win on magnitude, not frequency — max-ret `130%+` has win% **41.9**
  but PF 3.60 (few winners, huge). Real edge, but lumpy/higher-variance to trade. n is also thin up top
  (86–253) — directional, not precise.

**Era split — holds in both eras (not a regime artifact):**

| max ret 6mo | PF pre | PF post |   | max ADR 6mo | PF pre | PF post |
|---|--:|--:|---|---|--:|--:|
| <15%    | — | 1.28 |   | <4%    | 1.197 | **0.721** |
| 15–30%  | 1.813 | 1.277 |   | 4–6%   | 1.829 | 1.033 |
| 30–50%  | 1.768 | 1.619 |   | 6–8%   | 1.742 | 1.880 |
| 50–80%  | 1.278 | 2.056 |   | 8–11%  | 1.441 | 1.835 |
| 80–130% | 2.749 | 2.489 |   | 11–15% | 1.529 | 2.131 |
| 130%+   | **4.676** | **3.467** |   | 15%+   | **5.247** | **2.856** |

Monotone in both eras; the ADR `<4%` bottom is actually *worse* post-2015 (PF 0.72 — boring names have
gotten even less tradeable).

**⭐ It's INDEPENDENT of the entry-ATR% filter — the two are complementary, not redundant.** The obvious
worry is that "past runner" just re-discovers "volatile entry." It does not — max-ret sorts *within* fixed
entry-ATR% strata:

| within entry-ATR% < 0.06 (quiet entries, n=2,277) | n | PF |   | within entry-ATR% ≥ 0.08 (volatile entries, n=186) | n | PF |
|---|--:|--:|---|---|--:|--:|
| max-ret < 30% | 1453 | 1.501 |   | max-ret < 30% | 21 | **0.983** |
| max-ret 30–80% | 764 | 1.376 |   | max-ret 30–80% | 87 | 2.451 |
| max-ret > 80% | 60 | **1.701** |   | max-ret > 80% | 78 | **4.875** |

The volatile-entry row is the punchline: **a volatile entry with NO prior run is break-even junk (PF 0.98,
mean −$12); a volatile entry WITH a prior run is PF 4.88 ($2,025/trade).** Today's volatility only pays
when the stock has a *history* of running — a momentary spike on an otherwise-boring name is a fakeout. The
entry-ATR% filter and the past-runner signal stack.

**Implication / next step:** a **max-ret-6mo (or max-ADR) entry floor or sizing input** is a strong
candidate — it adds orthogonal signal to the current ATR%/tightness gates, especially as a *partner* to
entry-ATR% (cut volatile-but-no-history entries, which are pure fakeouts). max-ret looks like the better measure.
**It is a genuine edge under the full default** — monotone and era-robust, strongest at the top, on both
the production AND the loose gate once the entry ATR%/tightness caps are applied (see the gate-amplified
result below; the earlier "loose-gate inversion" was a caps-off artifact). Not yet wired into the engine —
to be designed and swept next; mind the thin top-decile n and the win%-vs-PF fat tail.

> **Resolution (2026-06-21):** shipped as a **FLOOR**, not a "strongest-at-the-top" signal. The top-bucket strength is
> lottery-tail (see the caveat at the top of this section), so the engine got a `max log ATR ≥ 0.04` *floor* (cut the
> dead-quiet base — the well-distributed bottom) rather than a top-chasing gate or a PF-3-sizing input. Details in
> [`momentum_v3_results.md` § max-log-ATR floor](momentum_v3_results.md).

#### The past-runner edge is GATE-AMPLIFIED, monotone on BOTH gates — with the ATR%/tightness caps ON (2026-06-20)

> ⚠️ **Corrected.** The first version of this section read the loose-gate deciles off the loose CSV with
> the entry **ATR%/tightness caps OFF** (disabled to populate the grid), and wrongly concluded the loose
> top decile "inverts to a net loser." That was an artifact: the `ATR% < 0.11` cap removes the violent-
> history blow-off cohort, and *with the cap on* the inversion vanishes. Re-ran with even-sized **deciles**
> (NTILE 10) and the **real entry caps applied on both gates** (ATR% < 0.11, tight < 4.5). Keep the two
> stronger measures — **max ATR% 6mo** (true-range) and **max ret/slope** — dropping range-based max ADR.

**PRODUCTION gate (caps on) — clean monotone rise, top decile strongest:**

| decile | max ATR% 6mo: PF | post | | max ret/slope: PF | post |
|---|--:|--:|---|--:|--:|
| D1 (lowest) | 1.155 | 0.949 | | 1.48 | 1.554 |
| D5 | 1.825 | 1.651 | | 1.825 | 1.668 |
| D6 | 1.797 | 1.756 | | 2.047 | 1.995 |
| D9 | 1.965 | 2.100 | | 1.989 | 2.328 |
| **D10 (highest)** | **2.834** (mean $822) | **2.647** | | **2.697** (mean $749) | **2.676** |

**LOOSE gate (caps on) — also monotone-up; top decile is the STRONGEST, NOT a loser:**

| decile | max ATR% 6mo: PF | post | | max ret/slope: PF | post |
|---|--:|--:|---|--:|--:|
| D1 (lowest) | 1.112 | 1.274 | | 1.297 | 1.239 |
| D5 | 1.219 | 0.995 | | 1.264 | 1.217 |
| D6 | 2.111 | 1.261 | | 1.232 | 1.192 |
| D9 | 1.538 | 1.497 | | 1.563 | 1.703 |
| **D10 (highest)** | **1.874** (mean $380) | **1.777** | | **1.706** (mean $314) | **1.692** |

**It's gate-AMPLIFIED, not gate-dependent.** With the caps on, the top decile is the best cell on *both*
gates and never a loser — the production gate just sharpens it (D10 ~2.7–2.8 vs loose ~1.7–1.9). The earlier
"loose-gate inversion" was entirely the missing ATR% cap letting the violent-history fakeout tail back in.
The `ATR% < 0.11` entry cap and the past-runner signal point the same way: both want a stock with a real
volatility history but *not* the uncapped blow-off extreme.

**Upshot:** max-ATR%/max-ret is a genuine **edge under the full default** (caps + gate), monotone and
era-robust, strongest at the top. The production gate amplifies it but the loose gate doesn't kill it — so a
max-ret floor is a sound signal to build *on top of* the existing entry. (Note a stray **D6 spike** in a
couple of cells, PF ~2 driven *pre-2015* — a concentration; don't over-read single deciles.)

#### Which half of the gate matters? → both floors help and STACK — with the ATR%/tightness caps ON (2026-06-20)

> ⚠️ **Correction.** An earlier version of this section ran the move/rvol decomposition on the loose CSV
> with the entry **ATR%/tightness caps OFF** (they'd been disabled to populate the volatility grid). That
> was wrong — the `ATR% < 0.11` cap already removes the violent-history fakeout tail, so the caps-off run
> mismeasured both the level *and the shape* of the gate effect (it falsely showed the move floor doing
> nothing, the top quintile losing, and production *worse* than rvol-only). **Redone here with the real
> caps applied throughout** (ATR% < 0.11, tight < 4.5); only move/rvol are varied. Quintiles (NTILE 5),
> breadth + ≥2005.

**Whole-system PF by gate (ATR%/tightness caps ON):**

| gate | n | PF |
|---|--:|--:|
| loose: move ≥ 5%, rvol [3,20] | 14,245 | 1.432 |
| move-only: move ≥ 10%, rvol [3,20] | 6,524 | **1.681** |
| rvol-only: move ≥ 5%, rvol [6,20] | 4,194 | **1.643** |
| **PROD**: move ≥ 10%, rvol [6,20] | 2,780 | **1.795** |

**Top-quintile (Q5) PF of the slope measure, across the four gates:**

| gate | Q5 PF | Q5 post | Q5 mean $ |
|---|--:|--:|--:|
| loose: move ≥ 5%, rvol [3,20] | 1.645 | 1.697 | +250 |
| move-only: move ≥ 10%, rvol [3,20] | 1.882 | 1.854 | +373 |
| rvol-only: move ≥ 5%, rvol [6,20] | 2.107 | 2.246 | +413 |
| **PROD**: move ≥ 10%, rvol [6,20] | **2.404** | **2.547** | **+528** |

**Findings (caps on — the real system):**
1. **Both floors help and they STACK cleanly.** Whole-system: 1.432 → move-only 1.681 / rvol-only 1.643 →
   prod 1.795. Slope Q5: 1.645 → 1.882 / 2.107 → **2.404**. Each tightening step raises PF; no inversion.
   The move and rvol floors contribute comparably (rvol a touch more at the top), and combine additively.
2. **The top quintile is NEVER a loser with the caps on** — even on the loose gate it's PF 1.645 (+$250),
   vs the broken caps-off run's −$125. The `ATR% < 0.11` cap is what removes the violent-history blow-off
   cohort that made the top decile invert; once it's gone, "more past-runner is better" holds at the top.
3. **The full PROD slope ladder is monotone and era-robust:** Q1 1.30 → Q3 1.94 → Q5 **2.40** (post-2015
   Q5 **2.55**). Under the real default, the past-runner edge is strongest at the extreme — opposite of the
   caps-off artifact.

**Design implication (revised):** a max-ret/max-ATR signal works *on top of* the full production entry
(both floors + ATR%/tightness caps), and **"higher is better" holds** — a floor on max-ret is sensible, no
need for a band/cap. The rvol and move floors are both pulling their weight; neither is redundant.

#### Full quintile tables — both measures × all three sub-systems (caps + breadth ON, 2026-06-20)

> The full picture behind the Q5-only summary above. Quintiles (NTILE 5) of each measure, **breadth applied
> throughout** (lag-1 pct_above_20 > 0.5), ATR% < 0.11, tight < 4.5, ≥2005. PF / pre-2015 / post-2015.

**LOOSE (move ≥ 5%, rvol [3,20], ~2,849/quintile):**

| Q | max ATR%: PF | post | | max slope: PF | post |
|---|--:|--:|---|--:|--:|
| Q1 | 1.054 | 1.126 | | 1.280 | 1.278 |
| Q2 | 1.017 | 0.963 | | 1.230 | 1.063 |
| Q3 | 1.668 | 1.130 | | 1.246 | 1.203 |
| Q4 | 1.345 | 1.395 | | 1.519 | 1.229 |
| **Q5** | **1.719** | 1.657 | | **1.645** | 1.697 |

**MOVE-ONLY (move ≥ 10%, rvol [3,20], ~1,305/quintile):**

| Q | max ATR%: PF | post | | max slope: PF | post |
|---|--:|--:|---|--:|--:|
| Q1 | 1.009 | 0.843 | | 1.271 | 1.038 |
| Q2 | 1.095 | 0.946 | | 1.248 | 1.114 |
| Q3 | 2.237 | 1.281 | | 2.330 | 1.455 |
| Q4 | 1.569 | 1.635 | | 1.487 | 1.539 |
| **Q5** | **2.052** | 1.950 | | **1.882** | 1.854 |

**RVOL-ONLY (move ≥ 5%, rvol [6,20], ~839/quintile):**

| Q | max ATR%: PF | post | | max slope: PF | post |
|---|--:|--:|---|--:|--:|
| Q1 | 1.067 | 1.122 | | 1.255 | 1.241 |
| Q2 | 1.200 | 1.096 | | 1.486 | 1.142 |
| Q3 | 1.521 | 1.227 | | 1.559 | 1.484 |
| Q4 | 1.617 | 1.691 | | 1.444 | 1.429 |
| **Q5** | **2.182** | **2.108** | | **2.107** | **2.246** |

Q5 is the strongest quintile in all six. **max ATR% edges max slope at Q5** in every gate (mildly, within
noise). **rvol-only is the most durable partner** — its Q5 is strongest *and* holds best post-2015 (ATR%
2.11, slope 2.25), while move-only leans pre-2015 (its Q3 spike is pre-2015 PF ~3.8–4.2). The bottom
quintiles diverge: move-only Q1–Q2 go *negative* post-2015 (0.84/0.95 — modest-history names clearing 10%
on noise); rvol-only keeps them positive (1.12/1.10). rvol confirmation cleans the low end better.

#### ⭐ Within-Q5 breakdown — the edge KEEPS rising into the top 4%: use a FLOOR, not a band (2026-06-20)

> Re-split *just the top quintile* of each measure into 5 sub-quantiles (so sub-Q5 ≈ the top 4% of the whole
> population) — to decide floor-vs-band. If the edge saturates/reverts inside Q5, a band/cap is right; if it
> keeps climbing, a floor is right.

Each table = the top quintile (Q5) of that measure, re-split into 5 even sub-quantiles. `range` = the
measure's lo–hi within the sub-bucket. mean $ / PF / pre-2015 / post-2015.

**LOOSE — within-Q5 of max ATR% (n≈570/sub):**

| sub-Q | range | mean $ | PF | pre | post |
|---|---|--:|--:|--:|--:|
| 1 | 0.095–0.105 | 172 | 1.491 | 1.952 | 1.315 |
| 2 | 0.105–0.119 | 153 | 1.379 | 1.270 | 1.434 |
| 3 | 0.119–0.142 | 408 | 2.108 | 2.451 | 2.002 |
| 4 | 0.142–0.188 | 229 | 1.586 | 2.454 | 1.386 |
| **5** | 0.188–2.74 | **500** | **1.964** | 1.900 | **1.979** |

**LOOSE — within-Q5 of max slope (n≈570/sub):**

| sub-Q | range | mean $ | PF | pre | post |
|---|---|--:|--:|--:|--:|
| 1 | 0.442–0.497 | 264 | 1.925 | 1.538 | 2.196 |
| 2 | 0.497–0.575 | 73 | 1.193 | 0.892 | 1.363 |
| 3 | 0.575–0.700 | 242 | 1.676 | 1.624 | 1.697 |
| 4 | 0.701–0.985 | 170 | 1.376 | 1.462 | 1.341 |
| **5** | 0.986–1654 | **503** | **2.077** | 2.453 | 1.990 |

**MOVE-ONLY — within-Q5 of max ATR% (n≈261/sub):**

| sub-Q | range | mean $ | PF | pre | post |
|---|---|--:|--:|--:|--:|
| 1 | 0.111–0.123 | 236 | 1.521 | 1.292 | 1.610 |
| 2 | 0.123–0.140 | 391 | 2.028 | 2.412 | 1.930 |
| 3 | 0.140–0.165 | 433 | 2.199 | 5.145 | 1.839 |
| 4 | 0.165–0.214 | 352 | 1.835 | 2.302 | 1.723 |
| **5** | 0.215–2.74 | **872** | **2.579** | 3.689 | **2.425** |

**MOVE-ONLY — within-Q5 of max slope (n≈261/sub):**

| sub-Q | range | mean $ | PF | pre | post |
|---|---|--:|--:|--:|--:|
| 1 | 0.538–0.604 | 189 | 1.580 | 1.687 | 1.540 |
| 2 | 0.604–0.706 | 337 | 1.872 | 1.607 | 1.959 |
| 3 | 0.707–0.857 | 151 | 1.346 | 1.633 | 1.266 |
| 4 | 0.857–1.293 | 375 | 1.934 | 2.893 | 1.727 |
| **5** | 1.297–656 | **816** | **2.441** | 2.373 | **2.453** |

**RVOL-ONLY — within-Q5 of max ATR% (n≈168/sub):**

| sub-Q | range | mean $ | PF | pre | post |
|---|---|--:|--:|--:|--:|
| 1 | 0.097–0.107 | 165 | 1.458 | 1.442 | 1.465 |
| 2 | 0.107–0.122 | 293 | 1.769 | 0.945 | 2.266 |
| 3 | 0.123–0.145 | 496 | 2.513 | 3.599 | 2.180 |
| 4 | 0.146–0.192 | 445 | 2.288 | 4.025 | 2.069 |
| **5** | 0.192–2.00 | **975** | **2.645** | 6.741 | **2.326** |

**RVOL-ONLY — within-Q5 of max slope (n≈168/sub):**

| sub-Q | range | mean $ | PF | pre | post |
|---|---|--:|--:|--:|--:|
| 1 | 0.449–0.500 | 191 | 1.581 | 1.250 | 1.920 |
| 2 | 0.501–0.575 | 179 | 1.557 | 0.870 | 1.960 |
| 3 | 0.576–0.700 | 351 | 2.185 | 1.843 | 2.286 |
| 4 | 0.701–0.960 | 298 | 1.752 | 1.219 | 1.923 |
| **5** | 0.962–656 | **1,051** | **3.003** | 4.720 | **2.693** |

In all six, **sub-Q5 is the highest-PF and highest-mean-$ sub-bucket** — no saturation, no extreme
reversion. Mean P&L roughly *doubles* sub-Q1 → sub-Q5 in the conviction-gated systems ($165 → $975 ATR%
rvol-only; $191 → $1,051 slope rvol-only). **"More is better" holds into the top 4% → a FLOOR is the right
structure, not a band/cap; the higher the floor, the better the per-trade quality (trading count for PF).**
Caveats: the within-Q5 middle is lumpy (sub-Q2 sags in a few — sample noise at n~170–570); rvol-only sub-Q5
is thin (n=167, ~100+ post-2015) so a real floor must be re-validated on a fresh full-history run, not this
grid CSV; read the post-2015 column (pre-2015 sub-Q5 PFs of 4–7 are small-n).

**Net design call:** pair a **max-ATR%-6mo (or max-slope-6mo) FLOOR** with **rvol ≥ 6** — rvol-only Q5 is
both the strongest and the most post-2015-durable, and the within-Q5 edge is monotone there. max ATR% is
the marginally better sort variable; max slope is close and more intuitive. Next: wire one measure into the
engine as an entry floor and sweep the threshold (same count-vs-PF trade as the tightness ceiling).

#### ⭐ Entry-day move analysis — a SWEET-SPOT NOTCH: 25–30% is the best band, 30–40% the worst (2026-06-20)

> Does raising the entry-day move threshold (default 10%) help, and where does it stop? Swept the floor
> post-hoc on the loose CSV holding everything else at default (ATR% < 0.11, tight < 4.5, breadth, ≥2005),
> for **both rvol gates** (≥6 production, ≥3 loose). PF / mean-$ / pre / post-2015; watch n thinning.

**Production rvol [6,20]:**

| move floor | n | PF | mean $ | total $ | PF post |
|---|--:|--:|--:|--:|--:|
| **0.10 (current)** | 2,780 | 1.795 | 209 | 580k | 1.808 |
| 0.125 | 2,202 | 1.847 | 235 | 517k | 1.916 |
| 0.15 | 1,698 | 1.887 | 260 | 442k | 1.977 |
| 0.175 | 1,301 | 1.930 | 290 | 378k | 2.034 |
| 0.20 | 979 | 1.978 | 331 | 324k | 2.109 |
| 0.25 | 593 | 2.156 | 422 | 250k | 2.274 |
| 0.275 | 470 | **2.306** | **479** | 225k | **2.458** |
| 0.30 | 364 | **1.536** | 209 | 76k | **1.460** |
| 0.40 | 142 | 1.942 | 404 | 57k | 1.639 (pre 4.03) |

**Loose rvol [3,20]:**

| move floor | n | PF | mean $ | total $ | PF post |
|---|--:|--:|--:|--:|--:|
| 0.10 | 6,524 | 1.681 | 195 | 1.27M | 1.508 |
| 0.15 | 3,061 | 1.676 | 218 | 666k | 1.625 |
| 0.20 | 1,427 | 1.832 | 306 | 436k | 1.870 |
| 0.25 | 771 | 1.904 | 371 | 286k | 1.985 |
| 0.275 | 582 | **2.037** | **415** | 242k | **2.217** |
| 0.30 | 436 | **1.493** | 209 | 91k | **1.488** |
| 0.40 | 163 | 1.770 | 354 | 58k | 1.600 (pre 2.62) |

The cumulative floor (above) shows where to set a *threshold*, but it can't locate the actual cliff —
a 0.30 floor pools *all* ≥30% names, so its low PF is dominated by the many 30–40% trades and hides what
happens *at* each move level. **Non-cumulative bands (below) are the honest read** of where the edge lives.

**Non-cumulative move bands (production gate, move ≥ 10% applied, bucketed WITHIN):**

| band | n | PF | mean $ | pre | post |
|---|--:|--:|--:|--:|--:|
| 10–15% | 1,082 | 1.595 | 127 | 1.904 | 1.387 |
| 15–20% | 719 | 1.708 | 165 | 1.779 | 1.661 |
| 20–25% | 386 | 1.642 | 190 | 1.438 | 1.745 |
| **25–30%** | 229 | **3.342** | **761** | 1.626 | **4.206** |
| **30–40%** | 222 | **1.230** | 84 | **0.929** | 1.311 |
| 40–55% | 98 | 2.514 | 550 | 4.562 | 2.128 |
| 55%+ | 44 | 1.140 | 81 | 2.587 | 1.011 |

**Same bands on the loose rvol [3,20] gate — the notch is NOT an artifact of the rvol ≥ 6 filter:**

| band | n | PF | mean $ | pre | post |
|---|--:|--:|--:|--:|--:|
| 10–15% | 3,463 | 1.687 | 175 | 2.287 | 1.358 |
| 15–20% | 1,634 | 1.499 | 141 | 1.939 | 1.329 |
| 20–25% | 656 | 1.723 | 229 | 1.836 | 1.683 |
| **25–30%** | 335 | **2.481** | **581** | 1.700 | **2.751** |
| **30–40%** | 273 | **1.304** | 122 | **0.874** | 1.409 |
| 40–55% | 115 | 2.174 | 476 | 3.709 | 1.884 |
| 55%+ | 48 | 1.108 | 64 | 1.046 | 1.122 |

Identical shape: 25–30% is the peak (PF 2.481, post 2.751), 30–40% is the worst non-tail band (1.304, pre-
2015 losing 0.874), the far tail is pre-2015-driven. rvol ≥ 6 *sharpens* the peak (3.34 vs 2.48) but the
structure is a property of the move distribution itself, present on both gates.

**Findings (corrected — the cliff is a NOTCH at 30%, not a smooth ramp):**
1. **The 25–30% band is the single best cell in the whole move distribution — PF 3.342, mean $761,
   post-2015 4.206.** These are the highest-conviction clean breakouts. The cumulative sweep buried this
   (adding 30%+ junk on top dragged the running average down to 1.54 at the 0.30 floor).
2. **The real cliff is a NOTCH at 30–40%: PF 1.230, mean $84, pre-2015 outright losing (0.929)** — the
   worst non-trivial band in the sweep, sitting *directly above* the best one. The mean winner collapses
   $761 → $84 across the 30% line: 30–40% single-day moves are exhaustion / blow-off gaps that revert.
3. **It is NOT monotone up to the notch.** 20–25% (1.642) is actually *weaker* than 15–20% (1.708); the
   edge is lumpy with a sharp spike specifically at **25–30%**. So "bigger is better" was wrong — there's a
   discrete sweet-spot band, not a ramp.
4. **The far tail (40–55%, 55%+) is thin and pre-2015-driven** — 40–55% PF 2.514 is pre 4.562 / post 2.128
   on n=98; 55%+ is dead post-2015 (1.011, n=44). Don't lean on it.
5. **rvol ≥ 6 stays ahead at every cumulative move floor** (move doesn't substitute for volume — rvol
   cleans the low end, move sharpens the high end), but rvol≥3 + move≥0.25 still beats the rvol≥6 + move≥0.10
   default — a different frontier point.

**Practical takeaway (revised):** this is a **notch, not a ceiling.** Don't cap at 30% in the naïve sense —
instead **size up the 25–30% band** (the best clean-breakout cohort) and **de-weight / avoid 30–40%** (the
exhaustion zone). Keep the move *default* floor near 0.10 for capacity (each band still has positive edge up
to 30%); the ≥30% blow-off is the one region to actively exclude.

**✅ ADOPTED (2026-06-20): MaxUpThreshold = 0.30 cap, and the rvol upper cap REMOVED (move cap supersedes
it).** The 30%+ blow-off and the rvol >15 toxicity are the *same trades* from two angles (the >15 cohort
averages a +47% move). So a 30%-move cap **mends the rvol >15 bucket directly** — rvol >15 goes from PF 0.73
(mean −1.8%) to **PF 1.54** (mean +1.2%) once the ≥30% moves are removed. Head-to-head (rvol ≥ 5, breadth,
≥2005):

| gate | n | PF | total $ | PF post |
|---|--:|--:|--:|--:|
| A: rvol [5,15], move uncapped (old) | 3,437 | 2.004 | 918k | 1.748 |
| **B: rvol ≥ 5 uncapped, move < 30% (NEW DEFAULT)** | 3,713 | 1.991 | 917k | 1.716 |
| C: both caps | 3,132 | 2.068 | 849k | 1.780 |

A and B are **interchangeable** (PF ~identical, same $917k) — they remove the same blow-offs. B is chosen:
it keeps **+276 well-behaved high-rvol trades** the volume cap discarded (a 3% PF gain wasn't worth 20%
fewer trades — option C), and it's the *more principled* rule: a 30% single-day move is what a blow-off
**is**; high rvol merely correlates. Rationale also includes that the surviving high-rvol names are
manually triageable (skip the deal-locked/pump ones on a news check), which a blind rvol cap can't do.
The move filter is now a **band [10%, 30%)** and rvol is **[5, ∞)**.

#### ⭐ rvol sweep (1→15, move held at 10%) — rvol ALSO has a toxic blow-off tail; cap it ~15 (2026-06-20)

> **Superseded conclusion:** this section concluded "add an upper rvol cap ~15." That cap was briefly the
> default, then **removed** — the 30%-move cap supersedes it (see the move-notch section above: the >15
> toxicity and the 30%+ moves are the same blow-off trades; capping the move mends the rvol bucket and
> keeps +276 well-behaved high-rvol trades). The rvol *analysis* below still stands; only the cap was dropped.

> Symmetric question to the move analysis: hold move ≥ 10%, vary rvol. Regenerated trips with a wide rvol
> gate (`--rvol-min 1 --rvol-max 1000`) since the standard CSV is rvol ∈ [3,20]; caps + breadth + ≥2005.
> Non-cumulative bands first (where the edge lives), then the cumulative floor.

**rvol DECILES — median return added (move ≥ 10%, caps on, ~1,100/decile).** PF is mean-driven, so it's
tail-sensitive; the **median return** shows what the *typical* trade does:

| decile | rvol range | median ret | mean ret | win% | PF | post med |
|---|---|--:|--:|--:|--:|--:|
| 1 | 1.0–1.7 | +0.19% | 1.29% | 50.5 | 1.286 | +0.38% |
| 2 | 1.7–2.3 | +0.73% | 1.77% | 53.3 | 1.489 | +0.59% |
| 3 | 2.3–2.8 | **−0.49%** | 0.81% | 46.2 | 1.215 | −0.51% |
| 4 | 2.8–3.5 | −0.11% | 1.31% | 49.2 | 1.374 | −0.10% |
| 5 | 3.5–4.2 | −0.07% | 1.18% | 49.2 | 1.374 | −0.24% |
| 6 | 4.2–5.2 | 0.00% | 0.55% | 49.7 | 1.188 | −0.12% |
| 7 | 5.2–6.5 | +0.03% | **3.57%** | 50.0 | 2.263 | −0.41% |
| 8 | 6.5–8.8 | +0.24% | 2.64% | 51.9 | 1.979 | +0.12% |
| **9** | 8.8–15.6 | **+0.67%** | 2.01% | **55.2** | 1.802 | **+0.91%** |
| 10 | 15.6+ | 0.00% | **−0.34%** | 49.9 | 0.923 | −0.08% |

**The median reframes the rvol story — the edge is almost ALL right-tail, not the typical trade:**
1. **Low rvol (D1–D6, rvol ~1–5) is a fragile, tail-carried edge.** The *median* trade there is slightly
   negative-to-flat (−0.49% to 0.00%); the positive PF comes entirely from a few big winners. High variance,
   mean-dependent — confirms the "1–3 rvol breakouts are barely distinguishable" read. Not a reliable edge.
2. **Median EXPOSES decile 7 (rvol 5.2–6.5) as a mirage.** Median +0.03% but mean +3.57% — its PF 2.26 is
   one or two monster trades, not a broadly good cohort (and post-2015 median is −0.41%). PF flattered it.
3. **⭐ Decile 9 (rvol ~9–15.6) is the genuine sweet spot — the ONLY decile where all three agree:** highest
   win rate (55.2%), highest median (+0.67%), best post-2015 median (+0.91%). Broad-based edge, not tail-
   driven. By median this is *the* rvol cohort to want — clearer than the PF view (which spread the edge
   across D7–D9).
4. **Decile 10 (15.6+) confirmed toxic from a new angle:** median 0.00%, mean *negative* (−0.34%) — the
   typical extreme-volume trade goes nowhere and the average loses (reverting blow-offs dominate).

So a cap ~15 severs the dead top decile, and the real conviction lives in **rvol ~9–15** (D9), where the
*typical* trade — not just the average — is positive and wins >55%.

**Cumulative rvol floor (move ≥ 10%):**

| floor | n | PF | total $ | post |
|---|--:|--:|--:|--:|
| ≥1 | 11,000 | 1.436 | 1.62M | 1.341 |
| ≥3 | 7,397 | 1.516 | 1.19M | 1.349 |
| **≥5 (cum. peak)** | 4,587 | **1.622** | 885k | 1.382 |
| ≥6 (current) | 3,653 | 1.426 | 496k | 1.364 |
| ≥8 | 2,496 | 1.260 | 221k | 1.138 |
| ≥10 | 1,843 | 1.204 | 135k | 1.086 |
| ≥15 | 1,150 | **0.934** | −33k | **0.846** |

**rvol BAND gates (cap the tail):**

| gate | n | total $ | PF | pre | post |
|---|--:|--:|--:|--:|--:|
| **rvol [6,20] (CURRENT)** | 2,779 | 580k | 1.796 | 1.769 | 1.810 |
| rvol [5,15] | 3,437 | 918k | 2.004 | 2.492 | 1.748 |
| **rvol [6,15]** | 2,503 | 529k | 1.807 | 1.705 | **1.862** |
| rvol [3,15] | 6,247 | 1.22M | 1.681 | 2.066 | 1.513 |

**Findings — rvol is NOT a monotone floor; it mirrors the move%-notch (healthy middle, toxic blow-off tail):**
1. **The top decile (rvol 15.6+) is an outright LOSER: PF 0.923, mean −$34, post-2015 0.844** — the only
   losing decile. Extreme volume = climax/exhaustion, exactly like 30%+ single-day moves. This is the
   cleanest, most robust signal in the sweep.
2. **The cumulative floor peaks at ~5 then DECLINES:** ≥5 (1.622) > ≥6 (1.426) > ≥8 (1.260) > ≥15 (0.934).
   Raising the floor past ~5 actively hurts because it loads up on that bad top decile. **The current rvol ≥ 6
   floor is past the cumulative peak** — but most of the ≥5 advantage is *pre-2015* (decile 7, rvol 5.2–6.5,
   is pre 4.52 / post 1.28), so this is era-fragile; post-2015 the floor barely matters between 5 and 6.
3. **The one durable, actionable change is an UPPER CAP ~15.** `rvol [6,15]` vs current `[6,20]`: **post-2015
   PF rises 1.810 → 1.862** with almost no trade loss (2,779 → 2,503) — cutting the 15+ losers helps the
   modern era for free. (Lowering the floor to 5, `[5,15]`, is the pre-2015 mirage: overall PF 2.004 but
   post-2015 1.748 < current 1.810.)
4. **By median (see decile table above), the edge is even more concentrated than PF suggests** — low rvol
   (D1–D6) has a near-zero/negative *median* (tail-carried, fragile), decile 7's PF is a fat-tail mirage, and
   the genuine broad-based sweet spot is **decile 9 (rvol ~9–15.6)**: win 55%, median +0.67%, post +0.91%.

**rvol [1,15) QUARTILES (less noisy than deciles, ~2,463/quartile) — the typical trade only wins in Q4:**

| quartile | rvol range | n | median ret | mean ret | win% | PF | med pre | med post |
|---|---|--:|--:|--:|--:|--:|--:|--:|
| 1 | 1.0–2.4 | 2,463 | +0.24% | 1.36% | 51.0 | 1.335 | +0.26% | +0.24% |
| 2 | 2.4–3.8 | 2,463 | **−0.23%** | 1.17% | 48.2 | 1.329 | −0.21% | −0.25% |
| 3 | 3.8–6.0 | 2,462 | +0.03% | 2.09% | 50.1 | 1.731 | +0.52% | **−0.19%** |
| **4** | 6.0–15.0 | 2,462 | **+0.37%** | 2.12% | **52.8** | 1.807 | +0.45% | **+0.30%** |

The quartile view (cleaner than the deciles) shows the median is **flat-to-weak across Q1–Q3 then steps up
in Q4**: +0.24 → −0.23 → +0.03 → +0.37. No smooth ramp — Q1 (rvol 1–2.4) is as good as Q3 (3.8–6) on the
median. **Only Q4 (rvol 6–15) stands out, and it's the only era-robust quartile** (post-2015 median +0.30%,
win 52.8%). Q3's PF 1.731 was flattered — its post-2015 median is *negative* (−0.19%, a pre-2015 edge). Q2
(rvol 2.4–3.8) is the weak spot (negative median both eras). **This vindicates the rvol ≥ 6 floor on a
median basis** — Q4 (≥6) is exactly where the typical trade turns durably positive, contradicting the
cumulative-PF "peak at 5" read (which was tail/pre-2015-driven). The conviction is in rvol ~6–15.

**SUB-1 rvol — the floor DOES matter, at ~1 (regenerated with `--rvol-min 0`):** the "1–5 is indistinct"
read was a *within-≥1* observation; below average volume the median turns sharply negative.

| rvol band | n | median ret | mean ret | win% | PF |
|---|--:|--:|--:|--:|--:|
| <0.5 | 170 | **−1.38%** | −0.93% | 43.5 | 0.859 |
| 0.5–0.75 | 129 | −1.06% | 1.06% | 47.3 | 1.239 |
| 0.75–1 | 198 | **−1.88%** | 0.88% | 43.4 | 1.175 |
| 1–1.5 | 767 | −0.17% | 0.92% | 49.4 | 1.187 |
| 1.5–2 | 931 | +0.64% | 1.64% | 52.8 | 1.443 |

**Sub-1 vs ≥1:** median **−1.30% vs +0.10%**, win **44.5% vs 50.5%** (n=497 vs 11,000). A breakout on
*below-average* volume is a fakeout — the typical one loses ~1.5% and wins <45%. By PF the sub-1 bands look
almost respectable (0.86–1.24, an occasional winner drags the mean up); **only the median exposes them.**

**So rvol has THREE regimes, and the median is the only metric that draws all three:**
- **rvol < 1 = fakeout / fail** (median ≈ −1.5%, win <45%) — no real demand confirming the breakout.
- **rvol ~1.5–15 = the edge** — a mild ramp (median +0.6% at 1.5–2, +0.57% at 9–15), peaking broadly at
  decile 9 (rvol ~9–15.6); the 1–5 sub-range is positive-but-indistinct, the conviction is ~9–15.
- **rvol > 15 = toxic blow-off** (median 0%, mean negative) — climax/exhaustion.

**Practical takeaway:** rvol is **not** a "more is better" dial. It's a **gate at ~1** (below = fakeout) +
a mild edge ramp to ~15 + a **toxic tail above 15.** Keep the rvol ≥ 6 floor (it clears the sub-1 junk with
margin; the ≥5 "improvement" is pre-2015 only) and **add an upper cap ~15.** Mirrors move%: healthy middle,
bad on both extremes — but for rvol the *lower* bad zone is sub-1, not merely low.

> ### 📐 Winner-clip convention (PROJECT STANDARD as of 2026-06-21)
> **All PF / mean-return figures are now computed on each trade's RETURN clipped at +50% (`LEAST(ret, 0.50)`);
> the loss side is left untouched.** PF is mean-driven and therefore hostage to lottery winners — a single
> buyout pop or +800% runner can hand a bucket a gaudy PF that has nothing to do with its *reliable* edge, and
> that contamination is exactly what makes bucketed/decile views go non-monotonic. Clipping the upside to a
> sensible ceiling (+50% is above p99 of qualifying trades — generous) makes PF reflect the *typical* trade in
> a bucket, which is what a floor/ceiling filter decision actually rests on. **Total P&L is understated by
> design** (the conservative read); when raw total-$ matters it is reported separately as `PF raw` / `tot`.
> Corollary discipline (re-confirmed 2026-06-21): **decide on CUMULATIVE views** (`X ≥ thr` / `X < thr`), which
> are low-variance and monotone; use **non-cumulative bands only diagnostically** to locate where edge changes
> — their edge-of-range cells are noisy and must not drive a threshold choice on their own.

#### Cumulative rvol floor with RETURN CLIPPING — the high-floor edge is NOT a tail artifact (2026-06-20)

> Cumulative `rvol ≥ X` floor (capped at 15 to drop the toxic tail, move ≥ 10%, caps on). Raw PF is
> tail-sensitive — at rvol ≥ 6 the max single-trade return is **+1,202%** (p95 +19%, p99 +41%; 12 trades
> >+50%, 2 >+100%). To test whether the floor's PF gains are real or carried by a few monsters, recomputed
> PF with each trade's **upside clipped at +50%** (generous — above p99; loss side untouched).

| floor | n | PF raw | PF clipped | clip pre | clip post |
|---|--:|--:|--:|--:|--:|
| ≥1 | 9,850 | 1.515 | 1.353 | 1.453 | 1.318 |
| ≥3 | 6,247 | 1.681 | 1.425 | 1.589 | 1.354 |
| ≥5 | 3,437 | 2.004 | **1.581** | 1.702 | 1.517 |
| ≥6 | 2,503 | 1.807 | 1.604 | 1.683 | 1.561 |
| ≥7 | 1,825 | 1.971 | 1.689 | 1.689 | 1.688 |
| ≥8 | 1,346 | 1.749 | 1.721 | 1.872 | 1.644 |
| ≥9 | 968 | 1.964 | 1.950 | 1.984 | 1.933 |
| ≥10 | 693 | 2.101 | 2.079 | 2.152 | 2.042 |
| ≥12 | 319 | 2.721 | 2.670 | 2.079 | 3.037 |

**Findings:**
1. **The monster winners inflated the LOW floors, not the high ones.** The clip haircut shrinks as the floor
   rises: ≥1 loses 0.16 PF, ≥5 loses **0.42** (the biggest mirage — that gaudy raw 2.004 was a couple of
   monsters), but ≥9 loses only 0.014 and ≥12 only 0.05. The high-rvol PFs are tail-robust; their edge is
   broad, not carried by outliers.
2. **The monotone trend SURVIVES clipping and is cleaner than raw.** Clipped PF still rises 1.35 → 1.60 (≥6)
   → 1.95 (≥9) → 2.67 (≥12), and **post-2015 clipped is the cleanest monotone of all** (1.318 → 1.561 → 1.688
   → 1.933 → 2.042) — no spikes, no reversals. The raw-table lumpiness (the ≥5 and ≥12 bumps) was tail noise.
3. **Conclusion (tail-robust):** higher rvol genuinely buys better trades, monotonically, all the way to 15
   — and it is NOT a tail artifact at the high end. This agrees with the median view; raw PF obscured it. The
   earlier raw "floor peaks at ≥5" was a monster-winner mirage (clipped, ≥5 is 1.58, *below* ≥9–12).

#### Why rvol >15 is "neutral" — it's the 2020-21 pump cohort, NOT buyouts (2026-06-20)

> Investigated the toxic rvol >15 tail (decile 10, median 0% / mean −$34 / PF 0.92). Hypothesis: deal-locked
> buyout/merger arbs (huge volume, price pinned → flat). **The data rejects buyouts and points to catalyst
> blow-offs concentrated in the 2020-21 pump mania.**

- **Not deal-flat.** The >15 cohort has lower stddev (16% vs 26% for 6–15) and more flat trades (39% within
  ±2% vs 26%), *but* a fatter LEFT tail (p10 −14.7% vs −8.3%). A buyout pins price near a fixed offer; these
  don't — they make violent two-sided moves that net to ~0.
- **The names are explosions, not deals.** Avg entry-day move **+47%**, avg rvol 70 (max 986), across 1,019
  distinct symbols. Top names: SCKT +538% → −50%, GLSI +998% → −18%, OBLN +414% → −44%, IINN +308% → −61%,
  YGMZ +333% → −33% — small-cap squeezes, biotech binary readouts, SPAC/meme pumps. They spike then crater.
- **⭐ It's a 2020-21 regime artifact, not a structural dead zone:**

  | rvol >15 cohort | n | % | median ret | mean ret | PF |
  |---|--:|--:|--:|--:|--:|
  | 2020H2–2021 (pump era) | 238 | 20.7% | **−2.27%** | **−4.75%** | **0.477** |
  | all other years | 912 | 79.3% | +0.18% | +0.87% | 1.273 |

  The toxicity is ~entirely the 21% of the cohort from the 2020-21 meme/SPAC/micro-cap mania (PF 0.48). The
  other 79%, across normal years, is an ordinary **PF 1.273**. Averaged together → the deceptive ~0 neutral.

**Implication for the [5,15] cap:** the upper cap is mainly **regime insurance against a repeat of 2021's
pump blow-offs**, not the pruning of a permanently-bad cohort — in normal regimes rvol >15 is fine (PF 1.27).
A reasonable robustness measure (don't be long the next meme-stock climax), but characterize it honestly as
tail-regime defense. (Breadth lag-1 > 0.5 should have caught much of 2021's churn but let these through on
the up-days — the blow-offs happen *into* strength.)

#### Breadth (pct_above_20) cumulative floor — higher breadth is better up to ~0.70, then rolls over (2026-06-20)

> We *gate* on breadth (lag-1 `pct_above_20 > 0.5`) but had never swept the level. Breadth = the fraction of
> liquid CS/ADRC stocks above their own **20-day** MA across the ~3,000-name universe (`breadth.parquet`,
> stores pct_above_20/50/100), lagged 1 day. (Note: v0 once concluded the **50-day** breadth was best — that
> is **stale**; v1/v2 settled on the **20-day** and that is the decided measure.) Cumulative `≥ X` floor
> (default trips, ≥2005), the clean view (deciles are too noisy):
>
> **Reverse-engineered universe (the builder wasn't committed; recovered 2026-06-20 by matching the parquet's
> `n` across dates):** `type IN ('CS','ADRC')` (common stock + ADRs — **NOT** ETF/ETN/funds; those are ~43%
> of the raw price table, the main gap) **AND 30-calendar-day average dollar volume ≥ $1,000,000** (the
> project-standard `avg_dollar_volume_4w` liquidity convention; **not** same-day, **not** $100k). Matched the
> parquet `n` to ~2-3% on 2010/2015/2020/2026 (2,593 vs 2,531; 3,112 vs 3,028; 3,364 vs 3,289; 3,934 vs
> 3,859 — consistently a hair over, likely a point-in-time ticker-reference nuance). $100k overshoots 25-35%.
> `pct_above_N` = fraction of that daily universe with `close > N-day SMA of close`.

| floor | n | median | win% | PF | total $ | PF post |
|---|--:|--:|--:|--:|--:|--:|
| ≥0.0 (no gate) | 5,858 | +0.34% | 52.8 | 1.781 | 1.20M | 1.644 |
| ≥0.4 | 4,677 | +0.32% | 52.8 | 1.899 | 1.06M | 1.690 |
| **≥0.5 (CURRENT)** | 3,717 | +0.28% | 52.3 | 1.991 | 917k | 1.717 |
| ≥0.6 | 2,520 | +0.30% | 52.9 | 2.249 | 776k | 1.909 |
| ≥0.65 | 1,919 | +0.34% | 53.1 | 2.385 | 663k | 1.936 |
| **≥0.70** | 1,272 | +0.38% | 53.5 | **2.822** | 583k | **2.150** |
| ≥0.75 | 662 | +0.89% | 57.1 | 1.942 | 153k | 1.776 |
| ≥0.80 | 288 | +0.98% | 58.3 | 2.124 | 80k | 1.967 |

**Findings:**
1. **PF rises monotonically with breadth up to 0.70, then rolls over.** Peak at ≥0.70: PF **2.822**,
   post-2015 **2.150** — vs the current ≥0.5 (1.991 / 1.717). The ≥0.75/≥0.80 rollback (~1.9–2.1) is the
   familiar froth signature (extreme breadth = late-cycle euphoria) but n is thin (288–662) — don't over-read.
2. **Unlike the noisy deciles, the floor view shows median AND win-rate also rise** (median +0.28% → +0.38%,
   win 52.3% → 53.5% from ≥0.5 to ≥0.70) — so higher breadth improves the *typical* trade, not just the
   tail. A trustworthy lever.
3. **Faster breadth (10/15-day MA) does NOT beat the 20-day**: the 10-day was *worse* at every floor,
   especially post-2015; 15-day ≈ 20-day. The current 20-day window is confirmed; a shorter, more-reactive
   breadth just adds noise. (Caveat: this test built pct_above_10/15/20 on an *approximate* universe — before
   the universe was reverse-engineered, it omitted the CS/ADRC filter so ~43% ETFs leaked in. The relative
   window ranking should hold, but re-confirm on the correct CS/ADRC + $1M-dv universe if it ever matters.)
4. **Available upgrade:** raising the breadth gate 0.5 → 0.70 lifts PF 1.991 → 2.822 (post-2015 → 2.150) at
   the usual capacity cost (3,717 → 1,272 trips, −66%). A steep quality-vs-capacity dial, same family as the
   tightness/move levers; the *direction* (higher breadth = better, to 0.70) is clean and era-robust. Not
   adopted as default yet — the −66% capacity is a big ask; candidate for a sizing tilt rather than a hard gate.

> **Universe:** the table above is the **standard $1M universe** (CS/ADRC + 30-cal-day ADV ≥ $1M, decided
> 2026-06-20 for both filters); the build below reproduces the production `breadth.parquet` to ~1-2%.

**Alternative — same sweep on a looser $100k universe** (CS/ADRC + 30-cal-ADV ≥ $100k), for reference only —
the optimum shifts down and the rollover is earlier/sharper (the looser universe is noisier at the extreme):

| floor | n | PF | PF post | (vs $1M-standard PF) |
|---|--:|--:|--:|--:|
| ≥0.5 | 3,542 | 2.044 | 1.759 | (1.991) |
| ≥0.6 | 2,278 | 2.294 | 1.941 | (2.249) |
| **≥0.65** | 1,630 | **2.484** | 2.002 | (2.385) |
| ≥0.70 | 972 | 1.776 | 1.574 | (2.822) |

On $100k the peak is ≥0.65 (then a sharp thin-n rollover at ≥0.70); on the standard **$1M the peak is ≥0.70
(PF 2.822)**. The optimum is universe-dependent, but "higher breadth helps to a mid-high optimum then froths
over" is robust to the definition. **We use $1M.**

> **Build script:** both the breadth and heat parquets are built by
> **`scripts/equity/build_breadth_and_heat.sql`** (runnable DuckDB; writes `breadth_1m.parquet` —
> reproducing the production `breadth.parquet` — and `heat.parquet`). It encodes the shared universe
> (30-cal-day ADV ≥ **$1M**; CS/ADRC for breadth only) and the load-bearing +1000% heat clip — the canonical
> reference for how both regime filters are computed.

#### ⭐ "Top-gainer HEAT" — froth timing measure; CHOSEN: skip entries when heat-10d ≥ 25% (Sykes-inspired) (2026-06-20)

> A new market-timing measure, orthogonal to the %-above-MA breadth we already gate on. It measures the
> *speculative temperature* of the tape — how hot the day's hottest names are running.
>
> **How the heat filter is calculated (exact, reproducible — canonical builder:
> `scripts/equity/build_breadth_and_heat.sql`; this section's breakdown SQL: `scripts/equity/heat_breakdown.sql`):**
> 1. **Per-stock daily return** = `adj_close / prev_adj_close − 1`, from `split_adjusted_prices`.
> 2. **Qualifying universe each day:** **30-calendar-day average dollar volume ≥ $1M** (the project-
>    standard `avg_dollar_volume_4w` window, $1M bar — NOT same-day dollar volume) AND a non-null return.
> 3. **⚠️ CLIP each per-stock return at +1000% (×10) BEFORE aggregating.** `split_adjusted_prices` contains
>    rare corrupted split/price rows that produce absurd returns; because heat is a mean of the *top* tail,
>    even one such row destroys that day's value. **The $1M + CS/ADRC constraints do NOT remove these** —
>    verified 2026-06-20: on the $1M universe, un-clipped, **2,390 rows still exceed +1000%** (max
>    **199,999,989,900%** = `ZXZZT` on 2014-10-22, price 0.0001→199,999.99). The culprits are mostly **NASDAQ
>    test tickers** (`ZXZZT`, `ZWZZT`, `AAZST`, `TESTA`, `ZVV`, `CGZST`, a bare `Z`, …). **CORRECTION (2026-06-21):
>    these have NO `ticker_reference` row at all** (verified — the ref-table query returns 0 rows for every test
>    symbol; an earlier note here wrongly said they were "tagged `CS`"). They reach the HEAT universe only because
>    heat uses a **LEFT** join and is intentionally not CS/ADRC-restricted, so a ref-less ticker survives with a
>    NULL type and its fake $1M+ volume clears the liquidity bar. The remainder are real micro-caps with genuine-but-extreme reverse-split moves that
>    *should* still be capped for a top-tail mean. **A 5-letter-ticker exclusion does NOT work either** (tested):
>    only 1,747 of the 2,390 corrupt rows are 5-letter; **642 are shorter** — both more synthetic symbols
>    (`ZVV`, a bare `Z`) *and* legitimate volatile micro-caps with real blowups (`TOPS`, `INPX`, `GBSN`,
>    `SDRL +180%`, `MULN +177%`, `XELA`). So a 5-letter rule simultaneously *misses* short-ticker junk **and**
>    would wrongly drop the many real 5-letter names (foreign ADRs etc.) — leaky and over-broad. Any name-based
>    filter is the wrong tool; the clip is **return-based** (caps the symptom regardless of ticker) and remains
>    the robust catch-all — even with all synthetic names gone, the real `SDRL`/`MULN`-type extremes still need
>    capping for a top-tail mean. It doesn't touch genuine gainers (a real one-day move tops out well under
>    1000%). Without it the whole measure is garbage. Do NOT drop it when porting to a live precompute.
>    **Belt-and-suspenders (2026-06-20):** the test tickers are also excluded by a **hardcoded blocklist** in
>    `build_breadth_and_heat.sql` (`is_test_ticker` = a literal list: NASDAQ `Z?ZZT`/`ZYxxx` test series, NYSE
>    `NTEST.*`, `AAZST`/`CGZST`/`YJZST`/`ZVV`), applied to BOTH universes. *Why a blocklist and not just the type
>    filter (clarified 2026-06-21):* for **breadth** the `type IN ('CS','ADRC')` inner join ALREADY removes them
>    for free (they have no ref row — verified 0 survive the join), exactly as in the engine; the blocklist there
>    is redundant. But **heat deliberately reads the whole liquid tape, NOT just CS/ADRC** (top-gainer froth lives
>    in warrants/units/recent-IPOs/foreign listings that legitimately lack a clean CS/ADRC row) — there are
>    **16,400 ref-less tickers** in the price table, so switching heat to a CS/ADRC inner join would silently drop
>    ~16k real-ish names to kill ~16 synthetic ones. The blocklist removes *only* the known-bad synthetic symbols
>    while keeping the broad tape — which is why it, not the type filter, is the right tool for heat. *Why a
>    hardcoded list and not a pattern/API:* the test symbols are a fixed, finite, published set that doesn't grow,
>    and every alternative proved leaky —
>    **Polygon doesn't carry them in its reference master at all** (`type=OTHER` and a direct `?ticker=ZXZZT`
>    query both return 0 rows, verified), a high-price rule false-positives on real reverse-split micro-caps,
>    and a bare `name~"test"` hits Whitestone/inTEST. The list catches all 16 synthetic tickers found in the
>    data; the real ticker `Z` (Zillow, one corrupt $200k row) is deliberately *kept* and handled by the clip.
>    This cleans synthetic names out of breadth too (where they'd silently count as "stocks"). The clip stays
>    as the backstop for residual real-ticker ratio-glitches (LU, EPIX).
> 4. **Daily heat** = mean of the clipped returns of the **top 1% of the qualifying universe by return**
>    (per-day `PERCENT_RANK() ≥ 0.99`).
> 5. **Smooth:** trailing mean over the chosen window — **`h10` = mean of daily heat over the prior 10
>    days**, `ROWS BETWEEN 10 PRECEDING AND 1 PRECEDING` (the `1 PRECEDING` **lags it one day** → as-of the
>    prior close, no lookahead). (5/10/15/20 were swept; h10 chosen.)
> 6. **Gate:** at entry, exclude (or downsize) when **`h10 ≥ 0.25`** (the Q4/Q5 boundary = 80th percentile,
>    on the 30-cal-ADV ≥ $1M universe).
>
> Series ~5,700 days (2003-09→2026-05); daily heat median ~18% after clipping. (Robustness: the froth-cut
> result barely moves across universe definitions — same-day-$100k gave threshold 27% / kept-PF 2.245;
> 30-cal-$100k gave 24% / 2.243; **30-cal-$1M (chosen) gives 25% / kept-PF 2.268 / post-2015 1.955** — so the
> signal does not depend on the liquidity-filter choice; we standardize on 30-cal-day ADV ≥ $1M.)

**Heat quintiles — high heat is BAD for our breakouts (median return, every window):**

| quintile | h5 | **h10 (chosen)** | h15 | h20 |
|---|--:|--:|--:|--:|
| Q1 (coolest) | +0.39% | **+0.67%** | +0.70% | +0.72% |
| Q2 | +0.58% | **+0.45%** | +0.44% | +0.38% |
| Q3 | +0.37% | **+0.39%** | +0.34% | +0.56% |
| Q4 | 0.00% | **+0.23%** | +0.11% | −0.08% |
| **Q5 (hottest)** | −0.29% | **−0.47%** | −0.41% | −0.32% |

*(This window-comparison table is on the original same-day-$100k universe; the chosen-window numbers were
re-confirmed on the corrected 30-cal-ADV ≥ $100k universe — the conclusion and h10 choice are unchanged, the
exact medians shift ~0.1pt. The h10 ladder/threshold below are the corrected, authoritative ones.)*

**Findings:**
1. **The froth hypothesis wins over risk-on.** The hottest-heat quintile is the *only* one with a negative
   median return — in **all four windows**. When the top 1% of gainers have run hot over the prior 1–4
   weeks, our momentum breakouts buy into a crowded, late-stage tape and the typical trade loses. Cool tape
   (Q1–Q2) → breakouts run. This generalizes the 2021-pump finding to a continuous regime measure.
2. **Froth cut across all windows (exclude top quintile, keep Q1–4) — h10/h15 best on the kept book, h20
   best post-2015, h5 worst:**

   | window | n keep | PF keep | total $ | PF keep post | Q5 median (cut) | Q5 PF |
   |---|--:|--:|--:|--:|--:|--:|
   | h5 | 2,971 | 2.167 | 780k | 1.776 | −0.29% | 1.532 |
   | **h10 (CHOSEN)** | 2,971 | **2.245** | 810k | 1.885 | **−0.47%** | **1.388** |
   | h15 | 2,971 | 2.244 | 808k | 1.902 | −0.41% | 1.394 |
   | h20 | 2,971 | 2.233 | 803k | **1.934** | −0.32% | 1.414 |

   h5 is too noisy (cuts the *least* toxic Q5). h10/h15 tie for best kept-PF and sharpest froth separation
   (cut the most toxic cohort); h20 wins post-2015 by ~0.05 PF. **Chose h10** — fastest regime response (a
   timing signal should step aside sooner) and the sharpest bad-cohort separation; the window choice is a
   minor optimization (window-insensitivity 10→20 is itself evidence the signal is real, not fitted).
3. **⭐ The h10 filter — exclude entries when trailing-10d heat ≥ ~24%** (Q4/Q5 boundary = 80th percentile,
   on the 30-cal-ADV ≥ $1M universe). Concrete ladder (quintile boundaries): Q1 9.6–14.2% (calm) · Q2
   14.2–16.6% · Q3 16.6–19.2% (typical, daily-heat median ≈18%) · Q4 19.2–25.6% (warming) · **Q5 25.6–49.2%
   (frothy — the cut)**. A 10-day *average* ≥25% means sustained two-week froth, not a single hot day.
   Filter effect:

   | | n | PF | total $ | PF post |
   |---|--:|--:|--:|--:|
   | baseline (all heat) | 3,713 | 1.991 | 917k | — |
   | keep heat-10d < 25% | 2,971 | **2.268** | 825k | **1.955** |
   | excluded (heat-10d ≥ 25%) | 742 | 1.334 | 92k | 1.403 |

   Cutting the frothy tape lifts PF **1.991 → 2.268** for −20% trips / −10% P&L — a better quality-per-
   capacity trade than most filters tested.
4. **The excluded Q5 is a low-win-rate coin-flip, not outright poison.** Its **median is −0.57%** but its
   **mean is +1.24%** (win rate the lowest) — froth tape produces enough occasional monsters to keep
   the mean barely positive even as the *typical* trade loses. So it's a **downsize/skip** candidate, not a
   hard "never trade" exclusion like the rvol-15+ pump cohort (which had a negative mean). **Orthogonal** to
   breadth/trend (speculative temperature, not direction). Not yet wired into the engine; the heat series is
   a post-hoc DuckDB build (`scripts/equity/heat_breakdown.sql`) — to go live it needs precomputing into a
   parquet like `breadth.parquet` (then gate `heat10 < 0.25` as-of the prior close).
5. **The mirror measure "COLD" (bottom-1% losers) is the SAME regime axis as heat — redundant, don't wire
   it.** Built cold identically (mean return of the *bottom* 1% by `PERCENT_RANK ≤ 0.01`, same $1M universe,
   −100%/+1000% clip, trailing-10d lagged `c10`). Standalone quintile breakdown: the **coldest** quintile
   (c10 −24% to −15%, a tape whose laggards are in freefall) is the *worst* for our breakouts — median
   **−0.34%**, win 46.7%, PF 1.24 — i.e. **same direction as heat** (extremes are bad / risk-off), **not** a
   contrarian capitulation-bounce signal. But it's a weaker tool: **non-monotone** (only the extreme-cold
   tail matters; Q2–Q5 are PF 1.5–2.0 with no ordering, plus a fat-tail Q3 spike), and **`corr(c10, h10) =
   −0.646`** — strongly anti-correlated with heat (frothy tape ⇒ shallow losers; fearful tape ⇒ deep losers
   + tame gainers), so it's largely the *same* signal from the other tail. Decisive test — *within* the
   heat-kept book (h10 < 0.25), the coldest cohort's badness mostly vanishes (median flips to **+0.59%**, PF
   1.67 vs 2.49 for the rest): heat already removes the overlapping bad regime. **Keep heat; cold adds
   little.** (Useful confirmation that the froth/extreme axis is real from both tails — momentum-long systems
   just read the *froth* tail more cleanly than the *fear* tail.)

#### 52w-proximity gate: intraday-HIGH channel is WORSE than the closing-high channel (2026-06-20)

> The 52w-proximity gate (`close ≥ 0.95 × prior-252d high`) used the **closing-high** channel. Tested
> gating on the **intraday-high** channel instead (`Use52wHigh = true`, CLI `--use-52w-high`) — a stricter
> "above *true* resistance" condition (price must clear the prior year's intraday spike, not just its closing
> high). Hypothesis: cleaner resistance detection → better entries. **Result: it's worse on every metric.**

| gate | n | PF | total $ | mean $ | PF pre | PF post |
|---|--:|--:|--:|--:|--:|--:|
| **closing-high (DEFAULT)** | 2,780 | **1.795** | 580k | 209 | 1.769 | **1.808** |
| intraday-high (`--use-52w-high`) | 2,555 | 1.511 | 343k | 134 | 1.752 | **1.384** |

The intraday-high gate lowers PF (1.795 → 1.511), cuts P&L 41%, drops mean-$ (209 → 134), and **degrades
post-2015 specifically** (1.808 → 1.384; pre-2015 is ~flat). The ~225 trips it removes were *better than
average* (P&L falls more than proportionally). **Why:** the intraday-high condition is satisfied *later* in
a breakout — by the time price closes above the prior year's intraday wick, the high-edge early part of the
move is gone. The closing-high gate enters *earlier* (above the prior closing high but still under the old
intraday wick — the 0–10% "dead zone above the high"), which is where the edge is. Cleaner-resistance ≠
better-entry. **Decision: keep the closing-high default;** `--use-52w-high` stays as an opt-in research flag.

## Reproduction

```bash
# v2 default (2026-06-20): NO price stop, 5-day time-stop, next-open exit (cap=0, no
# trailing limit, expansion off); entry filters ATR% < 0.11 (log), tightness < 4.5
# (linear), entry-move band [10%, 30%), rvol >= 5 (no upper cap).
# Run from dataset start for the 252-day warmup; filter entries to >=2005 post-hoc.
dotnet run --project TradingEdge.HighFlyer -c Release -- \
  --start-date 2003-09-10 --end-date 2026-05-13 -o /tmp/v2.csv

# then apply, post-hoc:
#   breadth_lag1 > 0.5   — LAG(pct_above_20) by 1 trading day on
#                          data/equity/momentum_v0/breadth.parquet, joined on entry_date
#   entry_date >= 2005-01-01
```

All defaults live in `TradingEdge.HighFlyer/Backtest.fs` (`defaultConfig`). Threshold sweeps use
the CLI overrides `--max-atr-pct`, `--max-tightness`, `--expansion-thr`, `--exit-time-cap`,
`--stop-low-window`, `--trail-window`, `--rvol-min/--rvol-max`.

---

## Engine notes

- **One in-memory pass.** Per ticker, a `QullaSystem` folds each bar into rolling structures
  (monotonic-deque sliding max/min for the stop/trail/52w channels, invertible sums for the ATR and
  volume means, a calendar-day mean for the 28-day volume window). All reads use the prior-bars
  snapshot taken *before* the current bar is folded in — no lookahead.
- **Indicators are `voption`** throughout; a gate whose metric is `ValueNone` (insufficient history)
  fails the entry, matching v0's `COALESCE(..., FALSE)`.
- **Universe** = `split_adjusted_prices` JOIN `ticker_reference` WHERE `type IN ('CS','ADRC')`,
  streamed `ORDER BY ticker, date`, flushed at ticker boundaries.

## Data fix — split_adjusted_prices dividend adjustment (2026-06-18)

While drilling the new-52w-low study, found stocks with impossible `pct_52w_low = −162%`, traced to a
**negative adjusted close** (DOMH 2024-12-30: raw $0.8975 → adj −$0.0745). Root cause: the
`split_adjusted_prices` materialization adjusted dividends **subtractively** (`adj = price − Σ
dividend_dollars`), which on low-priced dividend payers drives the price ≤ 0. Confirmed it was
dividends not splits (DOMH has zero splits; raw prices are clean; `raw − adj` was a constant dollar
step per ex-date). Affected **209,701 rows across 563 tickers** (all dividend payers), and silently
mis-scaled *every* payer's history. Fixed to the correct **multiplicative** back-adjustment
(`f = 1 − div/close_on_exdate` per ex-date, reverse cumulative product); rebuilt → 0 non-positive
prices. **Impact on this system was negligible** (PF 1.758→1.769) because the production filters
exclude the low-priced names where the bug lived; the headline above is on the corrected data.

## Known TODO (in code)

- **Hard up-only stop ratchet** — the stop recomputes `max(prior-window low, entry-day low)` each
  bar and can tick *down* if a lower low slides into the window. A true Qulla trailing stop never
  loosens. Deferred; test as a strategy change on top of this baseline.

*(The old "ATR% denominator uses the current close" TODO is RESOLVED — that's exactly what the
log-space rewrite fixed.)*

---

## Next session — INTRADAY entries near the open (planned 2026-06-21)

The whole system so far enters **at the close**. Next: test entering the same high-conviction setups
**near the open** and holding a few days, to capture the part of the move that happens *during* the
signal day instead of waiting for the close.

**Why retry this — it failed before, but under different conditions.** Months ago we tested opening-range
breakouts on high-rvol stocks and got nowhere. **But** that was: (a) essentially *every* rvol stock, with
(b) **no tightness gate, no ATR% gate, no move band, no breadth/heat regime filter** — i.e. none of the
edge-concentrating conditions discovered since. Those filters might have been the difference; an intraday
entry on the *filtered* population is a genuinely different experiment. The hypothesis: enter the
production setup (move [10%,30%), rvol [5,∞), ATR% < 0.11, tight < 4.5, 52w ≥ 0.95, breadth, heat < 25%)
**near the open** and hold ~5 days.

**Open questions to design around:**
- **Entry timing:** at the open? after an opening-range (first 5/15/30 min)? The old ORB framing is one
  option but not the only one — "buy near the open if the setup is intact" may beat waiting for a range break.
- **Which signal is known pre-close?** rvol, the entry-day move, and tightness/ATR% are partly intraday —
  need to define them as-of the entry *moment* (e.g. rvol projected from morning volume), not the full-day
  close, to avoid lookahead. This is the main correctness risk.
- **Data:** needs intraday bars (we have the equity bulk; the `intraday_10s` dataset exists). The current
  engine is daily-bar only — an intraday entry path is a real engine change, not a post-hoc SQL study.
- **Special case to revisit:** the **very-high-rvol names** (the 15+ / 30%+-move blow-offs that the
  current close-entry system *excludes* as exhaustion) might behave differently entered *early in the day* —
  noted as a deferred idea. Worth a separate look once the intraday harness exists.

The bar is high: the close-entry system is now PF ~2.0 (filtered, post-2015 ~1.8) with multiple
independent edges stacked. Intraday only earns its complexity if it adds materially on top.


---

<a id="v3"></a>

# HighFlyer — Mid-Cap Momentum v3 (Clipped-Methodology Era)

**System name: HighFlyer** (named 2026-06-21) — long-only daily swing momentum on US common stocks / ADRs:
the tight-consolidation breakout to new 52-week highs on a volume + move spike. Engine project
`TradingEdge.HighFlyer` (renamed 2026-06-28 from its legacy working name `TradingEdge.MomentumV2`).

**Status: working long-only daily-momentum edge. Filtered (breadth + ≥2005): 4,245 trips, PF raw
1.960 / clip 1.600, +$1.08M. Honest next-open fills, 5-day time-stop.**

v3 is not a new engine or a new system — it is the **same `TradingEdge.HighFlyer` production system**,
carried forward into a research methodology that computes every PF/return figure on **clipped per-trade
returns** and decides on **cumulative** views. That shift (2026-06-21) was significant enough — it
re-derived the tightness and ATR% optima and exposed several prior "edges" as lottery-winner mirages —
to warrant a fresh document. The full historical research record (exit-mechanic sweeps, 52w-proximity
studies, loose-base shorts, regime-switching, chandelier/time-stop derivation) lives in
[`momentum_v2_results.md`](momentum_v2_results.md) and is **not** repeated here; v3 carries only the
*current production state* and the work done under the clipped methodology.

`TradingEdge.HighFlyer` is a ground-up F# rewrite: all indicators computed in a single in-memory pass
(no SQL window functions), one `QullaSystem` per ticker. A full 22-year scan runs in **~16s**, so the
parameter sweeps below are minutes, not hours.

---

## 📐 Methodology — the clipped, cumulative standard (PROJECT STANDARD as of 2026-06-21)

Two disciplines now govern every sweep in this document:

**1. Winner-clip: PF / mean-return are computed on each trade's RETURN clipped at +50% (`LEAST(ret, 0.50)`);
the loss side is left untouched.** PF is mean-driven and therefore hostage to lottery winners — a single
buyout pop or +800% runner can hand a bucket a gaudy PF that has nothing to do with its *reliable* edge, and
that contamination is exactly what makes bucketed/decile views go non-monotonic. Clipping the upside to a
sensible ceiling (+50% is above p99 of qualifying trades — generous) makes PF reflect the *typical* trade in
a bucket, which is what a floor/ceiling filter decision actually rests on. **Total P&L is understated by
design** (the conservative read); when raw total-$ matters it is reported separately as `PF raw` / `tot`.

**2. Decide on CUMULATIVE views** (`X ≥ thr` / `X < thr`), which are low-variance and monotone; use
**non-cumulative bands only diagnostically** to locate where edge changes — their edge-of-range cells are
noisy and must not drive a threshold choice on their own.

*Why this matters (evidence):* under the clip, `tight < 3.0` showed **PF raw 2.67 vs PF clip 1.388** — its
apparent edge was almost entirely a handful of monster winners. The clipped column is the honest read; the
raw column is seductive and non-monotone. Several v2-era "findings" that rested on raw PF have been re-checked
under this standard (and where they changed, the v3 entry says so).

---

## The system in one screen (current production state)

Long-only daily momentum on US common stocks / ADRs (`ticker_reference.type IN ('CS','ADRC')`),
2005–2026. One position per breakout signal, fixed $10k notional, uncapped concurrency, no
compounding (so net P&L is a raw edge-and-breadth measure, not an achievable equity curve).

**Entry** — on a daily bar, go long at the close when ALL hold (each indicator uses *prior* bars,
no lookahead):

| gate | threshold | meaning |
| --- | --- | --- |
| entry-day move | **10% ≤ move < 30%** | `close/prevClose − 1`. The breakout must announce itself; the 30% cap removes the single-day exhaustion blow-off (and makes an rvol upper cap redundant) |
| relative volume | **rvol ≥ 5** (no upper cap) | `volume / 28-day avg volume`. 5 is enough to be significant; the move cap handles the toxic high-rvol blow-off tail |
| ATR% (log) | **< 0.10** | mean log-true-range over 14 prior bars. Tightened 0.11 → 0.10 on 2026-06-21 (the 0.10–0.11 band was dead) |
| intraday return | **`close/open − 1 ≥ −0.07`** | reject deep intraday FADES (gap-up-then-sell-off — the toxic tail of the red-candle band). Added 2026-06-21. (Raising to 0 tested: trims ~9% of trips for no clip-PF gain — kept at −0.07.) |
| **max log ATR** | **≥ 0.04** | the past-runner volatility-history FLOOR — 126-bar max of the 14-bar log-ATR (`MinMaxAtrLog`). Cuts the dead-quiet-base names (the robust, well-distributed *bottom* of the past-runner ladder; the *top* is lottery-tail, NOT floored). Wired into the engine 2026-06-21. Unfiltered-engine PF 1.851 → 1.922, −15% trips |
| tightness (linear) | **< 4.5** | `(14d range) / ATR` — prior consolidation must be tight (linear scale; sharper loose-tail cut than log) |
| 52-week proximity | **close ≥ 0.95 × hi_252** | near the 1-year closing high (closing-high channel beats the intraday-high channel) |
| price floor | **≥ $1** | lowered 5 → 1 on 2026-06-21 (sub-$5 is real edge under the clip, not a lottery; $1–3 kept for the future past-runner floor to rescue) |
| liquidity | **avg dollar volume ≥ $100k** | 28-day average |
| breadth (market-wide) | **`pct_above_20` lagged 1 day > 0.5** | applied post-hoc; risk-on regime only |
| heat (market-wide) | **`h10 < 0.25`** | skip entries when the top-1%-gainer froth measure is hot (Sykes-inspired); applied post-hoc |

**Exit** — **5-day time-stop, NO price stop** (the current default; the v2 doc's header still describes the
older Qullamaggie trailing stop — superseded). Sell at the next bar's open on the 5th day. The "disaster
exit" tested in v2 turned out to be a SHORT signal, not a long exit, and is off. Open positions at the data's
end are marked-to-market at the final close.

**Headline (filtered: breadth + 2005-start, 21.4 trading-day-years; ATR% 0.10):**

| | value |
| --- | ---: |
| trips | 4,245 |
| win rate | 52.7% |
| profit factor (raw) | **1.960** |
| profit factor (clip +50%) | **1.600** |
| net P&L | +$1,078,992 |

*(Unfiltered engine run at production defaults: 6,647 trips / PF 1.851 / +$1.56M. With heat<0.25 added:
3,157 trips / PF raw 2.191 / clip 1.678 / post-2015 1.648 / +$879k.)*

---

## Entry-filter geometry — the clipped re-derivation (2026-06-21)

All three sweeps below use the **wide dump** (both volatility gates opened so the full range is visible):

```bash
dotnet run -c Release --project TradingEdge.HighFlyer -- \
  --out /tmp/v2_wide_tight_atr.csv --max-tightness 1000 --max-atr-pct 1000   # ~9,090 trips
```

then sub-filter in SQL with breadth lag1 > 0.5, ≥2005, closed trips, clip +50%.

### Tightness ceiling — a HUMP peaking exactly at 4.5

The naive intuition ("tighter = safer = better") is **wrong**: clipped PF climbs off the ultra-tight floor,
peaks in the 3.5–4.5 zone, and falls off a cliff above 4.5. Script:
[`scripts/equity/tightness_atr_cum_sweep.sql`](../scripts/equity/tightness_atr_cum_sweep.sql).

| tight ceiling | n | PF clip | clip post | | tight ceiling | n | PF clip | clip post |
|---|--:|--:|--:|---|---|--:|--:|--:|
| < 2.5 | 298 | 1.219 | 1.499 | | **< 4.5** | 3,713 | **1.571** ← peak | 1.478 |
| < 3.0 | 1,082 | 1.388 | 1.247 | | < 5.0 | 4,301 | 1.495 | 1.386 |
| < 3.5 | 2,047 | 1.447 | 1.338 | | < 5.5 | 4,727 | 1.475 | 1.389 |
| < 4.0 | 3,003 | 1.568 | 1.447 | | < 7.0 | 5,365 | 1.390 | 1.291 |

Non-cumulative bands locate the source — best band is **3.5–4.0 (clip 1.864)**; **< 2.0 is a loser (0.836)**;
edge collapses above 4.5 (**5.5–7.0 = 1.015, post 0.886**). `tight<3.0` raw PF 2.67 → clip 1.388 is the clearest
lottery-mirage in the data. `MaxTightness = 4.5` is the optimum of the entire 2.5–7 range.

### ATR% ceiling — peaks at 0.10; the edge lives in the 0.06–0.10 band, NOT the quiet bulk

Same lesson on the other axis. Cumulative PF peaks at **< 0.10**; the non-cumulative bands show the edge is
concentrated in **0.06–0.10 (PF ~1.9–2.5)**, not the quiet `< 0.04` bulk (1.42):

| atr% ceiling | n | PF clip | clip post | | atr% band | n | PF clip | clip post |
|---|--:|--:|--:|---|---|--:|--:|--:|
| < 0.04 | 2,254 | 1.420 | 1.236 | | < 0.04 | 2,254 | 1.420 | 1.236 |
| < 0.06 | 3,158 | 1.438 | 1.355 | | 0.06–0.07 | 222 | 1.925 | 1.867 |
| < 0.08 | 3,524 | 1.529 | 1.470 | | 0.07–0.08 | 144 | 2.013 | 1.853 |
| < 0.09 | 3,615 | 1.569 | 1.501 | | **0.08–0.09** | 91 | **2.460** | **2.016** |
| **< 0.10** | 3,678 | **1.590** ← peak | **1.520** | | 0.09–0.10 | 63 | 2.208 | 1.903 |
| < 0.11 (old) | 3,713 | 1.571 | 1.478 | | 0.10–0.11 | 35 | 0.984 | 0.633 ← dead |

**✅ DECISION (2026-06-21): `MaxAtrPct` 0.11 → 0.10.** Engine-verified: raw PF **1.774 → 1.802**, P&L flat at
**+$1.19M**, trips 5,883 → 5,827 (−56). The 0.10–0.11 band was net-neutral on $ but PF-dilutive.

### 2D joint ceiling — the production corner IS the joint optimum

The joint cumulative grid (each cell = `tight < T` AND `atr% < A`, clip +50%) rules out a hidden off-diagonal
sweet-spot. Script: [`scripts/equity/tightness_atr_2d_sweep.sql`](../scripts/equity/tightness_atr_2d_sweep.sql).

**PF post-2015 (the era that matters); production corner `(4.5, 0.10)` is the grid max:**

| tight \ atr% | < .06 | < .07 | < .08 | < .09 | < .10 | < .11 |
|---|--:|--:|--:|--:|--:|--:|
| < 4.0 | 1.371 | 1.384 | 1.412 | 1.453 | 1.483 | 1.447 |
| **< 4.5** | 1.355 | 1.425 | 1.470 | 1.501 | **1.520** | 1.478 |
| < 5.0 | 1.314 | 1.387 | 1.398 | 1.429 | 1.427 | 1.386 |
| < 5.5 | 1.375 | 1.433 | 1.433 | 1.438 | 1.434 | 1.389 |
| < 7.0 | 1.276 | 1.304 | 1.322 | 1.305 | 1.318 | 1.291 |

The axes **reinforce** (no trade-off — moving toward the corner along either axis helps); both cliffs (quiet
`< 0.05` ATR%, loose `tight < 7.0`) hold across the other axis. **`(MaxTightness 4.5, MaxAtrPct 0.10)` is the
joint optimum — nothing to change.** *(Full-sample grid and trip-count grid: see the script output / v2 doc
equivalents.)*

### Vol-window length (ATR% + tightness lookback) — 14 is well-chosen; 13–18 is a plateau (2026-06-21)

The ATR% and tightness measures share a single lookback, fixed at **14 bars** since inception and never swept.
Added a `--vol-window` flag (sets BOTH lookbacks) and ran the engine for every window **10→25** (each is a full
run — the window redefines the measures, so it can't be sub-filtered in SQL; production caps atr%<0.10 / tight<4.5
re-apply inside each run). Clip +50%, breadth on, ≥2005. Script:
[`scripts/equity/vol_window_sweep.sql`](../scripts/equity/vol_window_sweep.sql).

| window | n | PF clip | PF raw | clip post | net P&L | | window | n | PF clip | clip post | net P&L |
|---|--:|--:|--:|--:|--:|---|---|--:|--:|--:|--:|
| 10 | 5,356 | 1.428 | 1.695 | 1.381 | $1.07M | | 18 | 3,442 | 1.612 | 1.480 | $0.93M |
| 11 | 5,079 | 1.481 | 1.772 | 1.440 | $1.09M | | 19 | 3,252 | 1.592 | 1.445 | $0.89M |
| 12 | 4,793 | 1.497 | 1.805 | 1.477 | $1.07M | | 20 | 3,045 | 1.581 | 1.440 | $0.84M |
| **13** | 4,540 | 1.569 | 1.906 | **1.521** | **$1.10M** | | 21 | 2,845 | 1.577 | 1.440 | $0.82M |
| **14** | 4,314 | 1.575 | 1.923 | 1.509 | $1.07M | | 22 | 2,649 | 1.582 | 1.464 | $0.78M |
| 15 | 4,084 | 1.560 | 1.933 | 1.501 | $1.01M | | 23 | 2,483 | 1.629 | 1.505 | $0.79M |
| 16 | 3,855 | 1.564 | 1.943 | 1.486 | $0.97M | | 24 | 2,324 | 1.641 | 1.516 | $0.65M |
| 17 | 3,622 | 1.581 | 1.983 | 1.496 | $0.95M | | 25 | 2,160 | 1.672 | 1.519 | $0.64M |

**Findings:**
1. **Short windows (10–12) are clearly worse** (clip 1.43–1.50) — a 10-bar base is too noisy to capture a real
   consolidation. Rules out anything below 13.
2. **13–14 is a local optimum AND the post-2015 peak.** Clip PF jumps 1.497 (w12) → 1.569 (w13) → 1.575 (w14), and
   **post-2015 PF tops the entire range at w13 (1.521) / w14 (1.509)** while capacity is still healthy (~4,300–4,500
   trips) and total P&L is at its max ($1.10M / $1.07M).
3. **The long-end full-sample PF rise (23–25 → 1.63–1.67) is a thinning artifact, not a real edge.** Trips more than
   halve (4,540 @ w13 → 2,160 @ w25) and **net P&L falls steadily** ($1.10M → $0.64M) even as PF climbs — the classic
   "stricter filter keeps only the easy trades" pattern. Post-2015 PF also *dips* through 19–21 (1.44) before the
   thin recovery, so it's not a clean monotone improvement.

**Verdict: 14 is well-chosen — it sits at the front of the 13–18 plateau where post-2015 PF is best and capacity is
full.** w=13 marginally edges it (post-2015 1.521 vs 1.509, +226 trips, +$33k), but the gap is within noise.
**✅ DECISION (2026-06-21): KEEP the window at 14** — the long-standing, well-understood default on the plateau;
not worth re-tuning to a coin-flip edge. `AtrWindow = TightnessWindow = 14` unchanged. The `--vol-window` flag
stays available for future sweeps.

### 2D joint FLOOR — entry-day move% × rvol: both floors reinforce; the move floor may be too LOW (2026-06-21)

The volatility *ceilings* (tightness, ATR%) define which bases qualify; the move% and rvol *floors* define how
hard the breakout must announce itself. Joint cumulative grid, each cell = `move ≥ M` **AND** `move < 0.30`
(the blow-off cap always on) **AND** `rvol ≥ R`, clip +50%. Wide dump with both gates opened
(`--up-threshold 0 --max-up-threshold 1000 --rvol-min 0 --rvol-max 100000`); rest production. Script:
[`scripts/equity/move_rvol_2d_sweep.sql`](../scripts/equity/move_rvol_2d_sweep.sql). Production corner
`(move ≥ 10%, rvol ≥ 5)` is **bolded**.

**PF (clipped +50%), rows = move ≥ M, cols = rvol ≥ R:**

| move \ rvol | ≥1 | ≥3 | ≥5 | ≥7 | ≥10 | ≥15 |
|---|--:|--:|--:|--:|--:|--:|
| ≥ 0% | 1.048 | 1.192 | 1.269 | 1.361 | 1.506 | 1.483 |
| ≥ 5% | 1.199 | 1.288 | 1.435 | 1.576 | 1.790 | 1.638 |
| ≥ 10% | 1.364 | 1.425 | **1.590** | 1.641 | 1.777 | 1.552 |
| ≥ 15% | 1.414 | 1.493 | 1.650 | 1.621 | 1.740 | 1.463 |
| ≥ 20% | 1.487 | 1.593 | 1.728 | 1.727 | **2.054** | 1.779 |
| ≥ 25% | 1.430 | 1.490 | 1.768 | 1.617 | 1.981 | 1.240 |

**PF post-2015 (the decision surface):**

| move \ rvol | ≥1 | ≥3 | ≥5 | ≥7 | ≥10 | ≥15 |
|---|--:|--:|--:|--:|--:|--:|
| ≥ 0% | 1.060 | 1.169 | 1.280 | 1.411 | 1.574 | 1.454 |
| ≥ 5% | 1.205 | 1.262 | 1.428 | 1.593 | 1.728 | 1.558 |
| ≥ 10% | 1.317 | 1.343 | **1.520** | 1.604 | 1.628 | 1.405 |
| ≥ 15% | 1.312 | 1.386 | 1.622 | 1.646 | 1.512 | 1.219 |
| ≥ 20% | 1.455 | 1.560 | 1.754 | 1.758 | 1.755 | 1.473 |
| ≥ 25% | 1.394 | 1.471 | **1.807** | 1.647 | 1.610 | 1.142 |

**Trip counts (n):**

| move \ rvol | ≥1 | ≥3 | ≥5 | ≥7 | ≥10 | ≥15 |
|---|--:|--:|--:|--:|--:|--:|
| ≥ 0% | 493,741 | 39,872 | 14,464 | 7,758 | 4,038 | 2,078 |
| ≥ 5% | 40,622 | 14,181 | 6,076 | 3,187 | 1,550 | 755 |
| ≥ 10% | 9,724 | 6,384 | **3,678** | 2,149 | 1,125 | 578 |
| ≥ 15% | 3,468 | 2,850 | 1,994 | 1,327 | 758 | 418 |
| ≥ 20% | 1,271 | 1,150 | 926 | 679 | 442 | 267 |
| ≥ 25% | 438 | 410 | 359 | 269 | 184 | 121 |

**Findings:**
1. **Both floors monotonically reinforce up the diagonal.** Along rvol (any move row) PF climbs `≥1 → ≥10` then
   dips at `≥15` (the toxic pump tail — bad in *every* row). Along move (any rvol col) PF climbs `≥0 → ≥20–25`.
   The two floors stack: the rich cells are the high-move × high-rvol diagonal, the poor cells the low-low corner
   (`move≥0 × rv≥1 = 1.048` — essentially no edge).
2. **The production move floor of 10% looks too LOW.** The shipped corner `(move≥10, rv≥5)` is post-2015 **1.520**,
   but raising the move floor to **15–20%** at the same rvol lifts it to **1.62 → 1.75** with still-usable size
   (`move≥15 × rv≥5` = 1.622 / n 1,994; `move≥20 × rv≥5` = 1.754 / n 926; `move≥20 × rv≥7` = 1.758 / n 679). The
   `move≥25 × rv≥5` cell is the post-2015 PF max of the whole grid at usable size (**1.807**, n 359).
3. **The `rv≥15` column is a wall** — PF falls off in every move row (toxic blow-off), confirming the high-rvol
   tail is genuinely bad, not merely thin. The deep corner (`move≥25 × rv≥15`, n 121) is noise.

**✅ DECISION (2026-06-21): KEEP the move floor at 10% (`UpThreshold = 0.10`, unchanged).** Raising it would lift
PF (`move≥15` → post-2015 1.622, `move≥20` → 1.754) but at a steep capacity cost — `move≥15` roughly halves the
book (3,678 → 1,994 at rv≥5) and `move≥20` quarters it (→ 926). The 10% floor is the **capacity anchor**; the PF
gain isn't worth that much lost size while sizing/capacity is unmodeled (the backtest is uncapped, non-compounding,
so PF-per-trip is the relevant axis only once a real book constrains concurrency). The finding stands as documented
upside to revisit *after* capital efficiency is modeled — at that point a higher move floor is the first lever to
pull. The prior notch work (25–30% best, 30–40% worst, *which is why the 30% cap exists*) is consistent: within
the [0,30] window the edge keeps rising toward the cap.

### Sub-$5 stocks under the clip — the $5 floor was a raw-PF over-correction (2026-06-21)

The $5 price floor was set in the raw-PF era because the sub-$5 bucket showed a gaudy PF driven by a few lottery
winners. With the clip we can finally see the region honestly. Wide dump with the price floor dropped
(`--min-price 0`, rest production); script [`scripts/equity/sub5_price_sweep.sql`](../scripts/equity/sub5_price_sweep.sql).

**The whole sub-$5 region is NOT a lottery mirage** — its raw PF (1.592) is barely above its clipped PF (1.526),
i.e. *under the production filters there is no fat tail to clip*.

**Where did the original "PF ~6" sub-$5 lottery go? It was never in THIS population — and the cause was NOT the
return clip.** Tracing it back to `momentum_v0_results.md` (the deep-discount structure study), the old inflation
had two compounding causes, both orthogonal to the +50% clip:
1. **It was dollar-P&L-weighted, not return-weighted.** Old PF was computed on dollar `net_pnl`. At fixed $10k
   notional a sub-$5 name buys a huge share count, so a split/adjustment glitch became a giant *dollar* number —
   the v0 doc records **a single trade at +$25M (a ~2,500× return) that was 84.6% of its tier**. The v3 sweeps
   compute PF on per-trade **return** (`exit/entry−1`), which is structurally immune to the share-count explosion
   (a 2,500× glitch is `ret=2500`, not `+$25M`). *This* — return-weighting, not the +50% price-gain clip — is the
   real reason "PF 6" became "PF 1.59".
2. **The lottery lived in a universe we now exclude.** Those glitch trades were **deep-discount, >50%-below-52w-high
   weakness breakouts** (54% sub-$5, median $4.22 per the v0 doc). Production now requires `52w ≥ 0.95` — *near
   highs* — so we never enter the beaten-down penny names where the corrupted rows were.

**Proof (verified 2026-06-21):** the single largest sub-$5 trade in the production population is **+161%** (a clean
$3.34 → $8.71 move) — no monster glitch exists; and `pf_dollar == pf_return == 1.592` *identically* (they only
diverge when a tail glitch is present). The v0 clean test even used a **+500% clip** (10× looser than ours) and
*still* found deep-discount was a loser. So the 1.526 clipped sub-$5 PF is honest and propped up by nothing.

| region | n | PF clip | PF raw | clip post |
|---|--:|--:|--:|--:|
| sub-$5 [0,5) | 667 | 1.526 | 1.592 | 1.402 |
| ≥ $5 [5,∞) | 3,678 | 1.590 | 2.021 | 1.520 |

Sub-$5 is only **modestly** worse than ≥$5 — not the disaster the floor implied. The price BANDS show the edge is
strongly **non-monotone in price**, and the floor is cutting good names:

| price band | n | PF clip | PF raw | clip post |
|---|--:|--:|--:|--:|
| < $1 | 31 | 1.582 | 1.582 | 0.551 ← broken post-2015, thin |
| $1–2 | 121 | 1.442 | 1.460 | 1.422 |
| $2–3 | 173 | 1.296 | 1.316 | 1.452 |
| **$3–5** | 342 | **1.698** | 1.820 | **1.501** ← better than $20+ |
| **$5–10** | 677 | **2.246** | 2.316 | **2.488** ← best band in the book |
| $10–20 | 920 | 1.780 | 2.854 | 1.608 |
| $20+ | 2,081 | 1.236 | 1.491 | 1.151 ← *worst* band |

Cumulative price FLOOR — **peaks at ~$3, not $5:**

| floor | n | PF clip | clip post |
|---|--:|--:|--:|
| ≥ $0 | 4,345 | 1.575 | 1.492 |
| ≥ $1 | 4,314 | 1.575 | 1.509 |
| ≥ $2 | 4,193 | 1.581 | 1.513 |
| **≥ $3** | 4,020 | **1.604** ← peak | 1.517 |
| ≥ $5 | 3,678 | 1.590 | 1.520 |
| ≥ $10 | 3,001 | 1.417 | 1.297 |

**Findings:**
1. **The edge is low-priced-but-not-penny.** `$5–10` is the single best band in the entire system (clip 2.246,
   **post-2015 2.488**); `$3–5` (clip 1.698, post 1.501) is *better than the `$20+` band the system currently
   trades* (clip 1.236, post 1.151). High-priced large-caps are the WORST band — same "energetic middle beats the
   safe extreme" theme as tightness/ATR%.
2. **The $5 floor is slightly too high.** The cumulative floor peaks at **≥$3** (clip 1.604 / post 1.517), which
   recovers the strong `$3–5` band while still excluding the weak `$1–3` zone and the broken-post-2015 penny band
   (`<$1` post 0.551). Below $3 the $2–3 + penny bands drag PF down.
3. **The aggregate gain is small but it is FREE trips in a GOOD band** (≥$3 vs ≥$5: clip 1.590 → 1.604, post 1.520
   → 1.517 ≈ flat, +342 trips in the 1.70-PF $3–5 band). Not a PF play — a capacity play that doesn't cost quality.

**✅ DECISION (2026-06-21): drop `MinPrice` 5 → 1** (not 3). The cumulative-floor *optimum* is $3, but we
deliberately keep the weak `$1–3` zone IN rather than floor at $3, because those low-priced names are the prime
candidates to be **rescued by the past-runner max-ADR% / max-slope FLOORS** (deferred, not yet in engine — see
below). A flat $3 floor would throw them away before that filter gets a chance; a $1 floor keeps them while still
excluding only the broken-post-2015 sub-$1 penny band (clip_post 0.551). Engine-verified end-to-end at $1:

| filter | trips | PF raw | PF clip | net P&L |
|---|--:|--:|--:|--:|
| breadth, ≥2005 ($5 floor, prior) | 3,678 | 2.021 | 1.590 | +$915k |
| **breadth, ≥2005 ($1 floor, NEW)** | **4,314** | 1.923 | **1.575** | **+$1.07M** |
| breadth + heat<0.25 ($1 floor) | 3,195 | 2.164 | 1.662 | +$879k |

Adding `$1–5` is **+636 breadth-filtered trips (+17%) / +$156k** at **flat clip PF** (1.590 → 1.575 — the honest
measure barely moves; raw dips 2.021 → 1.923 only because the `$1–3` names are weaker, exactly the band the
past-runner floor should lift). Unfiltered engine run: 6,749 trips / PF 1.820 / +$1.55M. `MinPrice = 1.0` in
`defaultConfig`.

### Entry-day candle BODY shape — the "fat green candle" hypothesis is WRONG; it's a middle-body hump (2026-06-21)

From the gap-over < reclaim finding, the hypothesis was: a **fat green body** (opens low, closes high — conviction
built through the day) should beat a **doji / top-heavy** candle. Tested on the production trips by joining each
entry day's adjusted OHLC and computing body shape. Script:
[`scripts/equity/candle_body_breakdown.sql`](../scripts/equity/candle_body_breakdown.sql). Clip +50%, breadth, ≥2005.

**Body fraction `(close−open)/range` — the FATTEST green bodies are the WORST (after red):**

| body band | n | PF clip | clip post |
|---|--:|--:|--:|
| < 0 (red close) | 518 | 1.345 | 1.077 |
| 0.0–0.2 (doji-ish) | 249 | **1.888** | **2.514** |
| 0.2–0.4 | 420 | 1.629 | 1.588 |
| 0.4–0.6 | 752 | 1.837 | 1.714 |
| 0.6–0.8 | 1,168 | 1.602 | 1.491 |
| **0.8–1.0 (fat green)** | 1,207 | **1.426** | **1.405** ← worst non-red |

The hypothesis is **inverted**: the fattest green candle (body 0.8–1.0) is the *worst* non-red band, *below*
baseline (1.575). The best are the **doji-ish and mid-body** bands. A mild "don't close red" floor (`body ≥ 0`)
helps (clip 1.603 / post 1.572), but tightening to `body ≥ 0.8` *hurts* (1.426).

**Open position `(open−low)/range` — the cleanest signal, and it CONFIRMS the gap-over intuition:**

| open position | n | PF clip | clip post |
|---|--:|--:|--:|
| 0.0–0.2 (opened at LOW) | 3,001 | 1.538 | 1.488 |
| **0.2–0.4** | 604 | **1.895** | **1.805** |
| 0.4–0.6 | 313 | 1.619 | 1.694 |
| 0.6–0.8 | 192 | 1.794 | 1.380 |
| **0.8–1.0 (opened at HIGH)** | 204 | **1.047** | **0.899** ← a loser |

**Opening at the high is a near-loser (clip 1.047, post-2015 0.899)** — exactly the gap-over story: a stock that
opens at its high already did the move premarket, nothing left. The sweet spot is opening **lower-middle (0.2–0.4)**.

**The key test — among CLOSE-HIGH trades, does opening low (fat body) beat opening high (doji)?**

| group (close_pos ≥ 0.8) | n | PF clip | clip post |
|---|--:|--:|--:|
| A: opened LOW (full fat body) | 1,569 | 1.463 | 1.420 |
| **B: opened MID** | 310 | **1.977** | **1.968** |
| C: opened HIGH (doji-at-top) | 90 | 1.489 | 1.880 |

**It's a hump, not a ramp — the MIDDLE body wins (B: clip 1.977 / post 1.968).** Neither extreme is best: the
full fat body (open-at-low → close-at-high, body_frac ≈ 1) is a **climactic full-range bar — over-extended and
exhausted**, the *worst* of the three; the doji-at-top is thin/noisy. The winner is a **controlled intraday
reclaim**: opened slightly down, closed near the high, moderate body.

**Conclusion — same lesson as everywhere this session: MODERATE energy beats EXTREME energy.** The fattest candle
is the over-extended one, just like the fattest move (30%+ blow-off) and the highest rvol (>15 pump). The gap-over
instinct was right (opening at the high is genuinely bad), but the "fat body = better" framing had the curve
backwards. **Not adopted as a filter** (the effect is real but the bands are smaller than the core filters, and a
"middle body" gate is fiddly to specify cleanly) — documented as a strong characterization of *what a good
breakout day looks like*: opens slightly down, closes near the high, with a controlled rather than vertical body.

#### The RED-CLOSE band drilled — it's "gap up huge then fade," and the damage is ALL in the 20%+ gap (2026-06-21)

The body band `< 0` is the oddest cell: trades that close **up ≥ 10% on the day** yet print a **RED candle**
(close < open). The only way both hold is a stock that **gapped up huge at the open and then sold off into the
close** — it finished green purely on the size of the gap. Drill
([`scripts/equity/candle_redbody_detail.sql`](../scripts/equity/candle_redbody_detail.sql)):

| | n | avg overnight gap | open→close | open_pos | PF clip | clip post |
|---|--:|--:|--:|--:|--:|--:|
| GREEN (body ≥ 0) | 3,796 | +6.5% | +9.9% | 0.12 (opens low) | 1.603 | 1.572 |
| **RED (gap-up then fade)** | 518 | **+21.5%** | −3.5% | 0.68 (opens high) | 1.345 | **1.077** |

The red band gaps **+21.5% overnight** (vs +6.5% green), opens near the top of its range (open_pos 0.68 vs 0.12),
and gives back ~3.5% intraday — the most extreme gap-over there is (the whole move happened pre-open; live you
can't capture it). **But it is NOT uniformly bad — the damage is entirely in the 20%+ gap subset:**

| red band × overnight gap | n | PF clip |
|---|--:|--:|
| gap 10–20% | 270 | **1.889** ← fine |
| gap 20%+ | 248 | **1.014** ← the loser |

A red candle on a *moderate* (10–20%) gap is fine (1.889); the loser is the **20%+ gap** (1.014, ≈ breakeven), and
the deepest intraday fades are worst of all (open→close < −15%: clip 0.516, n 14). The most extreme examples are
+40–175% overnight pops on speculative micro-caps (JG +176%, ZKIN +76%, DOGZ, MICT, TTNP) that opened at the top
and bled all day — unrepeatable premarket squeezes. **So the red band is not a new bad signal — it's the candle-shape
silhouette of the same 30%+ over-extension cohort we already cap with the day-move ceiling.** Same theme, fourth axis.

#### Intraday-return floor `close/open − 1 ≥ N` — swept; peaks at N=0, but NOT adopted (2026-06-21)

Tested the cleanest possible version of the finding: a single floor that rejects the trade unless `close/open − 1 ≥ N`
([`scripts/equity/intraday_ret_floor_sweep.sql`](../scripts/equity/intraday_ret_floor_sweep.sql)). The floor peaks
**exactly at N = 0 ("no red candle")** and over-cuts for any positive N:

| floor N | n | PF clip | clip post | | excluded tail | n | PF clip of CUT |
|---|--:|--:|--:|---|---|--:|--:|
| −∞ (all) | 4,314 | 1.575 | 1.509 | | — | — | — |
| −0.05 | 4,192 | 1.590 | 1.541 | | cut < 0.00 | 518 | **1.345** ← bad, drop |
| **0.00 (no red)** | 3,796 | **1.603** | **1.572** | | cut < 0.02 | 910 | 1.496 |
| 0.02 | 3,404 | 1.590 | 1.541 | | cut < 0.05 | 1,461 | **1.612** ← *above* baseline! |
| 0.05 | 2,853 | 1.562 | 1.490 | |  |  |  |
| 0.10 | 1,777 | 1.541 | 1.470 | |  |  |  |

Clip PF rises monotonically to N=0 then falls — N=0 is the optimum on both eras (post-2015 1.509 → 1.572). The
excluded-tail table shows *why* 0 is the line: the `close/open < 0` reds are genuinely bad (clip 1.345, worth
dropping), but pushing past 0 starts cutting trades that are **at/above baseline** (the `< 0.05` cut population is
1.612 > the 1.575 baseline) — a positive floor throws away good trades.

**Deep-fade-only variant — cut ONLY the worst reds, keep the mild-red band (2026-06-21).** A follow-up: rather than
the full no-red rule, keep everything *except* deep intraday fades. The negative region is strongly graded — the
damage is in the deep fades, the mild reds are near-baseline:

| intraday_ret band | n | PF clip | clip post |  | keep ≥ N (deep-fade cut) | trips | PF clip | clip post |
|---|--:|--:|--:|---|---|--:|--:|--:|
| < −20% | 8 | 0.736 | 0.736 | | all | 4,314 | 1.575 | 1.509 |
| −20..−15% | 6 | 0.295 | 0.383 | | keep ≥ −0.10 | 4,287 | 1.587 | 1.524 |
| −15..−10% | 13 | 1.187 | 1.218 | | **keep ≥ −0.07** | 4,245 | **1.600** | 1.541 |
| −10..−5% | 95 | 1.484 | 1.038 | | keep ≥ −0.05 | 4,192 | 1.590 | 1.541 |
| −5..0% (mild red) | 396 | 1.420 | 1.179 | | keep ≥ 0.00 | 3,796 | 1.603 | 1.572 |
| ≥ 0 (green) | 3,796 | 1.603 | 1.572 |  |  |  |  |  |

The deep-fade tail is genuinely toxic (the cut population `< −10%` is clip ~0.72, `< −15%` is 0.52) — but it is
**tiny: only ~27 trades over 21 years below −10%, 69 below −7%.** So **`N = −0.07` is the capacity-efficient sweet
spot**: it captures most of the no-red rule's PF gain (clip 1.575 → **1.600**, post 1.509 → 1.541) for a cost of just
**69 trips (1.6%)** vs the full rule's 518 — because the mild −5..0% band it keeps is near-baseline (1.42), not worth
dropping.

**✅ DECISION (2026-06-21): ADOPTED at `N = −0.07` (deep-fade-only).** Wired into the engine as a new entry gate
`close/open − 1 ≥ −0.07` (`MinIntradayRet` in `defaultConfig.Entry`; CLI `--min-intraday-ret`). It targets *only* the
toxic deep-fade tail of the red-candle band while keeping the near-baseline mild reds — the capacity-efficient choice
over the full no-red rule (−69 trips vs −518 for nearly the same PF). **Engine-verified end-to-end** (matches the
post-hoc projection exactly): breadth-filtered **4,314 → 4,245 trips, clip PF 1.575 → 1.600, post-2015 1.509 → 1.541,
P&L +$1.07M → +$1.08M** (raw engine PF 1.820 → 1.851). With heat<0.25: clip 1.662 → **1.678**, post 1.572 → **1.648**.
The earlier overlap concern (the 30% move cap already catches much of this cohort) is real but the deep-fade tail is
the residual it *doesn't* catch — and it's nearly free (1.6% of trips), so it earns its place. `N = 0` (full no-red)
remains available via the flag for a slightly higher PF at a much larger capacity cost.

#### Full intraday-return breakdown — the hump strikes again: 0–5% is the SWEET SPOT (2026-06-21)

With the deep-fade floor live, mapped the *whole* intraday-return range to see what else it carries. Script:
[`scripts/equity/intraday_ret_breakdown.sql`](../scripts/equity/intraday_ret_breakdown.sql). Clip +50%, breadth, ≥2005.

| intraday_ret band | n | PF clip | clip post | | quintile (data-driven) | range | PF clip | clip post |
|---|--:|--:|--:|---|---|---|--:|--:|
| < −7% (cut by gate) | 69 | 0.791 | 0.766 | | Q1 | −55%..+1.8% | 1.445 | 1.264 |
| −7..0% (mild red, kept) | 449 | 1.566 | 1.226 | | **Q2** | +1.8%..+6.3% | **1.912** | **2.092** |
| **0–2%** | 392 | **1.827** | **2.186** | | Q3 | +6.3%..+10.2% | 1.437 | 1.352 |
| **2–5%** | 551 | **1.810** | 1.986 | | Q4 | +10.2%..+14.2% | 1.650 | 1.568 |
| 5–10% | 1,076 | 1.610 | 1.546 | | Q5 | +14.3%..+34.5% | 1.511 | 1.451 |
| 10–15% | 1,029 | 1.561 | 1.512 | |  |  |  |  |
| 15–25% | 677 | 1.601 | 1.437 | |  |  |  |  |
| 25%+ | 71 | 1.043 | 1.401 | |  |  |  |  |

**Findings:**
1. **The best intraday band is 0–5% (clip ~1.8, post-2015 ~2.0–2.2) — a LOW push, not a big one.** A stock that
   closes +10% on the *day* but only moved **0–5% from its open** got most of its gain **overnight (the gap)** and then
   **drifted up calmly** intraday — a controlled, non-vertical session. The quintiles agree: **Q2 (+1.8–6.3% intraday)
   is the standout at clip 1.912 / post 2.092.**
2. **The 25%+ intraday band rolls over (1.043)** — a 25%+ open→close climb is the *vertical intraday blow-off*, the
   worst green band. **Same hump as every axis this session:** moderate beats both extremes (deep fade AND vertical push).
3. **Intraday return is a GENUINELY NEW signal, not a move% proxy** — corr with the day-move (pct_up) is only **0.29**
   (and −0.14 with the overnight gap). It measures *where in the session* the move happened, which the move% gate doesn't.
4. **A green push with NO overnight gap is excellent** (gap <2%: clip 1.839 / post 1.732) — the move is happening
   cleanly *during* tradeable hours. (Most production trips DO gap, by construction of the ≥10% day-move + rvol filters.)

**No new gate taken** — the 0–5% peak is a soft preference, not a cliff, and the existing gate already removes the bad
tail; a 25%+ *intraday* cap would only catch 71 trades and overlaps the 30% day-move cap. Documented as another face of
the **moderate-energy-beats-extreme** theme: the ideal entry day gaps up, then drifts calmly higher — it does **not**
go vertical intraday.

### Path dependency — day-1 direction is MECHANICAL, not predictive; no early-exit rule (2026-06-21)

Entry = close of day 0; hold days 1–5; exit at the **open of the 6th trading day** (`MaxHoldBars = 5`). Asked whether
day 1 (first held bar) telegraphs the trade. The raw split is enormous — and entirely a trap:

| day 1 | n | full-trade PF clip | clip post |
|---|--:|--:|--:|
| **UP** | 2,160 | **5.739** | 6.061 |
| **DOWN** | 2,033 | **0.460** | 0.390 |

Day-1-up trades show PF 5.7, day-1-down are a money-loser at 0.46, perfectly monotone across bands (day1 < −10% →
0.075; day1 10%+ → 29.4). **But this is almost entirely MECHANICAL, not predictive:** day-1 return is a *component*
of the 5-day trade return — an up day-1 has already booked part of the win, a down day-1 has dug a hole. The actionable
question is whether day-1 weakness predicts the **rest** of the trade going badly (which would justify an early exit).
It does **not** — the **forward** return (day-1 close → exit) is the same regardless of day-1 direction:

| day 1 | n | FORWARD PF clip | forward mean | forward median |
|---|--:|--:|--:|--:|
| UP | 2,160 | 1.428 | +2.25% | +0.03% |
| DOWN | 2,033 | **1.474** | +1.13% | **+0.25%** |

**Day-1-down trades have the SAME forward edge as day-1-up (PF 1.47 vs 1.43), and a *higher* forward median.** Graded
forward bands are flat-to-noisy (even day1 < −10% is forward PF 1.175; the −10..−5% band is the *best* at 1.674) — no
"weakness begets weakness" continuation. **Conclusion: NO early-exit-on-day-1-weakness rule.** It feels right ("cut the
losers early") but day-1-down trades have positive forward EV (mean +1.1%, median +0.25%) — exiting on day 1 would
realize the dip and forfeit the bounce. Same lesson as the v2 tight-stop whipsaw: these breakouts need room to shake
out; a quick stop on early weakness destroys the edge. The 5-day time-stop (hold through) stays.

**Extended to 2- and 3-day STREAKS (2026-06-21)** — same mechanical trap, even more extreme, and a NEW twist on the
up side. Script: [`scripts/equity/streak_path_breakdown.sql`](../scripts/equity/streak_path_breakdown.sql). A "down
day" = close below the prior day's close; streaks measured from entry.

| streak | n | full-trade PF (mechanical) | FORWARD PF (streak-end → exit) | fwd mean | fwd median |
|---|--:|--:|--:|--:|--:|
| DOWN-DOWN | 991 | 0.151 | **1.393** | +0.75% | +0.13% |
| UP-UP | 1,038 | 21.065 | 1.217 | +2.67% | 0.00% |
| DOWN-DOWN-DOWN | 472 | 0.035 | **1.281** | +0.44% | 0.00% |
| UP-UP-UP | 480 | 107.9 | **1.026** | +4.41% | −0.05% |

The full-trade PF spread is now absurd (UP-UP-UP = 108, DOWN-DOWN-DOWN = 0.035) — by day 3 almost the entire trade is
already booked; pure mechanics. The **forward** PF is the real signal and it splits the two sides:
- **Down streaks KEEP their forward edge** — DD forward PF 1.393, DDD 1.281, both solidly > 1 with positive forward
  mean/median. A stock down 2–3 days straight after entry still has positive expected return over the rest of the hold.
  No capitulation. This **re-confirms: do NOT cut on weakness**, even a multi-day run of it.
- **Up streaks DECAY toward no edge** — UU forward PF 1.217, and **UUU drops to 1.026 with a *negative* forward median
  (−0.05%).** After a 3-day winning streak the forward edge is nearly exhausted: the move is front-loaded and the
  remaining hold is ~a coin flip.

**The actionable asymmetry is the OPPOSITE of intuition.** The reflex ("cut down-streaks early") is wrong — down
streaks mean-revert. If anything, the signal is on the UP side: **a 3-day up streak is where forward edge dies**, hinting
at an early *profit*-take (sell into a 3-day run rather than holding to day 6). ⏳ *Open: test an early-profit exit on
the 3-day up streak — but it's modest (PF 1.026 ≠ a loss, just no edge), so it would free capital, not avoid losses.*

**4-day streaks complete the progression (2026-06-21)** — the trend is monotone and the up-side finally crosses below 1:

| streak | n | full-trade PF | FORWARD PF (streak-end → exit) | fwd mean | fwd median |
|---|--:|--:|--:|--:|--:|
| DOWN ×4 | 220 | 0.001 | **1.212** | +0.26% | +0.15% |
| UP ×4 | 229 | 343.9 | **0.916** | **−0.14%** | **−0.04%** |

Forward PF down the streak ladder: **up** = 1.217 (×2) → 1.026 (×3) → **0.916 (×4)** — a clean monotone decay that goes
*negative* by 4 days (the breakout has fully spent its move; the last held day is a slight drag). **Down** = 1.393 → 1.281
→ **1.212** — still solidly positive after 4 straight down days; the mean-reversion never breaks. **Caveats keep it
non-actionable:** the samples are tiny (~220 each, ~10/yr), the forward window at a 4-day streak is only ~1 day (day 5 →
day-6 open, so "UP×4 negative" just means *the last day of a 4-day winner gives a hair back*), and the eras disagree
(post-2015 DOWN×4 0.898 / UP×4 0.982 — both ≈ noise). So the **shape is real and consistent** but the 4-day effect is a
micro-drag, not a regime — it reinforces the day-3 up-streak-exhaustion read rather than adding a separate signal.

### Dead-zone reclaim vs gap-over, revisited through intraday return — reproduced, and only PARTLY a proxy (2026-06-21)

The v2 dead-zone refinement was *"require an intraday reclaim of the prior 52w high, not a gap over it"* — in the
0–10%-above-the-high dead zone, names that **opened below** the prior 52w intraday high and **closed above** it
(reclaim) beat names that **gapped over** it pre-open: PF 1.43 vs 1.09, ~2,244 trips, reclaim carrying nearly all the
P&L. With the intraday-return (`close/open − 1`) signal now established (§ *Full intraday-return breakdown*), the
question was whether that geometry was ever real or just a proxy for the intraday push.

**First, the reproduction — which initially failed and is the key lesson.** The original split ONLY reproduces on the
population it was actually run on: **"pure gainers" (`--up-threshold 0`, NO move floor)** on the v2-era defaults
(rvol [6,20], tight < 4.0, ATR% < 0.11, price ≥ $5) + breadth. The small movers the up = 0 floor admits are most of the
2,244 trips and are *where the reclaim edge lives*. Restricting to the v3 production move band [10,30]% (or even
[5,10]%) carves a different, reclaim-heavy population on which the split **inverts** — so an earlier write-up of this
section that used the move-restricted tiers was reproducing the wrong population and is **superseded by this one**.
Repro script: [`scripts/equity/deadzone_repro_v2era.sql`](../scripts/equity/deadzone_repro_v2era.sql) (regen CSV with
`--up-threshold 0 --rvol-min 6 --rvol-max 20 --min-price 5 --max-tightness 4.0 --max-atr-pct 0.11`). Raw PF (no clip),
breadth lag-1 > 0.5, ≥ 2005. The v3 5-day exit shifts the PF *levels* vs the original 20-day-era numbers, but the
direction and ~2,256-trip count reproduce:

| dead-zone split (up=0 pure gainers, v2-era filters, 5d stop) | n | win% | PF raw | net | pre-2015 | post-2015 |
|---|---|---|---|---|---|---|
| **reclaim (open < high → close > high)** | 1408 | 51.3 | **1.295** | +$97.5k | 1.426 | 1.207 |
| gap-over (open ≥ high) | 848 | 49.8 | 1.172 | +$27.8k | 1.062 | 1.298 |

Reclaim wins (1.295 vs 1.172) and carries ~78% of the dead-zone P&L — the original finding holds on its own population.

**The 20-day time-stop (the v2-era exit) does NOT recover the exact original 1.43/1.09.** Re-running with
`--max-hold-bars 20`: reclaim 1.303 / gap-over 1.348 raw PF (gap-over actually edges *ahead* on raw PF), but reclaim
still carries **~2× the P&L** ($189.8k vs $91.5k on 1395/835 trips) and still wins the early era decisively (pre-2015
**1.68** vs 1.22). So the time-stop is not the lever; the residual gap to the original 1.09 is most likely the v2-era
**entry-day stop** geometry (a gap-over's day-low stop sat tighter, penalizing it — the original doc flagged this as a
"second-order effect" worth ~0.07 PF) plus minor 52w-window drift. The **direction and P&L-dominance reproduce under
both exits**; the precise 1.09 was specific to a now-superseded stop config and isn't worth chasing.

**Is it just the intraday signal? Partly — but NOT fully.** A reclaim is **intraday-up by construction** (open below the
high, close above it ⇒ close > open): the cross-tab leaks nothing, **100% of reclaims are intraday-up** (1408/1408),
while gap-overs are mixed (557 up / 291 down). So the two variables are mechanically entangled. The controlling test —
hold the intraday sign fixed and compare:

| intraday sign | split | n | PF raw | net |
|---|---|---|---|---|
| intraday UP | reclaim | 1408 | **1.295** | +$97.5k |
| intraday UP | gap-over | 557 | 1.171 | +$18.7k |
| intraday DOWN/flat | gap-over | 291 | 1.174 | +$9.1k |

Among **intraday-up** names, reclaim (1.295) still beats gap-over-up (1.171) by **+0.12 PF** — a *residual* edge that
does **not** vanish when you control for intraday return. So the reclaim rule is **not merely** the intraday signal in
disguise: opening below resistance and reclaiming it intraday is genuinely better than gapping through it, even at the
same intraday-up sign. (Note too that for a **gap-over**, the intraday sign barely matters — 1.171 up vs 1.174 down —
so intraday return is informative for reclaim-type entries but not gap-over-type ones.) The push-size band on this
population is **rising-then-flat**, not the hump seen on the strong v3 tier: `−2..0%` 1.22 → `0..5%` 1.24 → `5..10%`
**1.33** → `10%+` 1.25.

**Verdict:** the original reclaim > gap-over result is **real and reproduced** (on up=0 pure gainers). Intraday return
is **correlated with but does not fully explain** it — reclaim keeps a ~+0.12 PF residual after controlling for the
intraday sign. So reclaim and intraday-return are two related-but-distinct reads of the same entry bar; the reclaim
geometry is the stronger one in the dead zone, and intraday return is the more general signal across the rest of the
system. **Both are worth carrying into intraday entries.** Net effect on production: none — the engine never wired in the
reclaim rule (it stayed a candidate); this corrects the record and keeps both signals on the table.

> ⚠️ **Methodology lesson:** always reproduce a prior finding **on its original population** before re-interpreting it.
> The reclaim result was run on `--up-threshold 0` pure gainers; reading it through the v3 production move band silently
> swapped the population and flipped the sign. Match the filters first, *then* test the new hypothesis.

**Lower-tier aside [5,10]% / rvol ≥ 3 — intraday return INVERTS vs the production tier.** On weak breakouts (PF 1.21
overall, vs the production tier's 1.60) an intraday **fade is the GOOD version** — intraday-down 1.455 vs intraday-up
1.193, decaying monotonically as the push grows (`−2..0%` 1.62 → `5%+` 1.13 / post-2015 1.06). So the production −0.07
deep-fade reject gate is a **strong-tier phenomenon that does not generalize down**: on a small 5–10% mover, a name that
opened roughly flat and *ground out* its close is live; one that gapped and *drifted* is exhausted. Script:
[`scripts/equity/deadzone_intraday_explains.sql`](../scripts/equity/deadzone_intraday_explains.sql).

### Distance from the 52w MAX CLOSE on the [5,10]% / rvol > 3 tier — spike at the fresh high, decay when extended (2026-06-21)

On the lower-tier system (move ∈ [5,10]%, rvol > 3) with **full production settings intact incl. heat + breadth**, how
does PF depend on `pct_52w_at_entry` = close / 52w-max-close − 1 (the close-vs-close-high reference)? Negative = still
below the prior max close; ~0 = fresh new closing high; positive = extended above it. Script:
[`scripts/equity/dist_52w_close_510_rvol3.sql`](../scripts/equity/dist_52w_close_510_rvol3.sql). Baseline 7,069 trips,
PF clip **1.217** / post-2015 1.161. Clip +50%, breadth lag-1 > 0.5, heat (CS/ADRC) h10 < 0.25, ≥ 2005.

| distance from 52w max close | n | PF clip | post-2015 |
|---|---|---|---|
| < −3% (below max close) | 760 | 1.353 | 0.852 |
| −3..−1% | 755 | 1.328 | 1.713 |
| −1..0% (just under) | 417 | 1.158 | 0.889 |
| **0..1% (fresh close-high)** | 487 | **1.735** | **1.909** |
| 1..3% | 1003 | 1.359 | 1.171 |
| 3..5% | 1269 | 1.133 | 1.125 |
| **5%+ (extended)** | 2378 | **1.065** | 1.066 |

**A sharp spike right at the fresh closing high (0–1%, PF 1.735 / post-2015 1.909) then monotone decay the further the
name extends above it.** The `5%+` bucket is the biggest (2,378 trips, a third of the band) and is **dead** at 1.065 —
the same moderate>extreme shape as every other axis, here on the 52w-close distance: the best entry is *exactly at* the
breakout to a new closing high; chasing it once it's run 5%+ past the high is no edge. The deep-below-high `<−3%` bucket
looks decent (1.353) but is **era-fragile** (post-2015 0.852) — a recovering-laggard artifact, not the signal; the
fresh-high spike is the era-robust one.

**Cumulative — the CEILING is the refinement, the floor isn't.** Requiring *more* extension (`d52 ≥ N`) flat-to-hurts
(d52 ≥ 0.03 → 1.086). Requiring *less* (`d52 < N`) concentrates the edge: **d52 < 0.05 → 1.305** (keeps 4,691 of 7,069),
**d52 < 0.03 → 1.368**, d52 < 0.01 → 1.372. So capping the extension — *not pinning a floor above the high* — lifts the
lower tier from 1.217 to ~1.31–1.37 by dropping the dead extended bucket. **Takeaway for the lower tier: buy the fresh
close-high, cap the extension at ≤ ~3–5% above the prior max close.** (Diagnostic for now — the lower tier is not in
production; logged for when intraday entries pull in weaker breakouts.)

**Can the reclaim/gap-over split rescue the dead extended (`d52 ≥ 3%`) bucket? No — reclaim only works in the breakout
zone, not the extended zone.** Splitting `d52 ≥ 3%` (above the max *close*) into reclaim vs gap-over (vs the *intraday*
high, the v2 definition):

| extended dead zone (d52 ≥ 3%) | n | PF clip | post-2015 |
|---|---|---|---|
| reclaim (open < intraday-high) | 2276 | 1.104 | 1.058 |
| gap-over (open ≥ high) | 1371 | 1.049 | 1.148 |
| └ `3..5%` gap-over | 328 | 1.211 | **1.886** |
| └ `3..5%` reclaim | 941 | 1.116 | 1.004 |
| └ `5%+` gap-over | 1043 | 1.013 | 1.021 |
| └ `5%+` reclaim | 1335 | 1.097 | 1.090 |

The reclaim edge is **gone** here: reclaim 1.104 vs gap-over 1.049 (a trivial 0.06, and it *reverses* post-2015), and in
`3..5%` the **gap-over is the better half** post-2015 (1.886 vs 1.004). This is the crucial contrast with the original v2
dead zone: that edge was in the **0–10%-above-the-52w-INTRADAY-HIGH** zone (names right *at the breakout level*), whereas
`d52 ≥ 3% above the max CLOSE` is the **already-extended/run** territory. Once a name is well past its breakout, opening
below vs above the intraday high no longer matters — it's extended and dead either way. **So the reclaim signal is a
property of the breakout zone, not the extended zone; the only tool for the `d52 ≥ 3%` bucket is the extension ceiling
(cap d52), not a reclaim filter.** Script:
[`scripts/equity/dist_52w_close_510_rvol3.sql`](../scripts/equity/dist_52w_close_510_rvol3.sql).

**Does intraday return rescue it instead? No — same dead result, even flatter.** Splitting the same `d52 ≥ 3%` bucket by
intraday return: by sign, intraday-up 1.087 vs intraday-down 1.028 (n=3376/262) — no edge; by band, **non-monotone and
hovering ~1.0** (`0..2%` 1.39 is the one bright cell on n=369, but `2..5%` is **0.985** on n=984, and `5%+` 1.103 on
n=2032) — scatter, not a gradient. So **neither microstructure lever (reclaim geometry NOR intraday push) discriminates
in the extended zone** — both are *breakout-zone* signals that work only at the fresh high (the 0–1% close-distance cell,
where intraday push and reclaim both mattered) and go silent once a name has run 3%+ past its max close. **Confirmed: you
can't rescue over-extended names — you can only avoid them (cap `d52`).**

**Are dead-zone trades just names up several days in a row already (a stale, late breakout)? Partly — but extension, not
streak, is the real axis.** Counting the **pre-entry up-streak** (consecutive up-closes ending at the entry bar) on the
same population. Script:
[`scripts/equity/preentry_upstreak_deadzone.sql`](../scripts/equity/preentry_upstreak_deadzone.sql).

| distance from max close | n | mean up-streak (incl. breakout day) | % streak ≥3 (≥2 prior up-days) |
|---|---|---|---|
| < 0 (below high) | 1932 | 2.07 | 30.0 |
| 0..1% (fresh high) | 487 | 2.01 | 27.7 |
| 1..3% | 1003 | 2.04 | 27.6 |
| 3..5% | 1269 | 2.09 | 29.8 |
| **5%+ (extended)** | 2378 | **2.72** | **49.7** |

The `5%+` extended bucket **is** streak-heavy — half its names break out on a ≥3-day up-run (the breakout day plus ≥2
prior up-days, vs ~28–30% everywhere else), confirming many are late continuations where the run started earlier and the
5–10% entry-day move is a late leg of a grind. **But the 2D grid shows extension is the dominant axis, not streak** (PF
clip by d52 × up-streak):

| d52 band ↓ / up-streak (incl. breakout day) → | 0–1 (fresh) | 2 | 3 | 4+ |
|---|---|---|---|---|
| < 1% (at high) | 1.443 | 1.297 | 1.195 | **1.533** |
| 1..3% | 1.284 | 1.313 | **1.716** | 1.233 |
| 3..5% | 1.145 | 0.950 | 1.225 | 1.447 |
| **5%+ extended** | **1.120** | **1.000** | **1.014** | **1.160** |

Read the `5%+` row: it's dead at **every** streak length, *including a fresh 1-day move* (0–1 streak → 1.12). So
extension kills it regardless of how it got there — it's not "we bought after a multi-day run." Conversely the `<1%`
at-the-high row is healthy at every streak length (1.44 / 1.30 / 1.20 / 1.53), **long streaks included**. So a long
pre-entry streak *at a fresh high* is fine; a *short* streak when *already extended* is the dead profile.

**A long up-streak is GOOD, not stale — and a streak cap would be exactly backwards.** *Definition: the streak length
INCLUDES the breakout/entry day itself* — `streak = 1` means only the breakout day was an up-close (0 prior up-days);
`streak = 5` means the breakout day capped a run of ≥5 consecutive up-closes (≥4 up-days of build-up *into* it). Every
entry is a gainer day, so the minimum is 1. Pooled across the band, PF clip is mildly **non-monotone in the GOOD
direction**:

| up-streak (incl. breakout day) | n | PF clip | post-2015 |
|---|---|---|---|
| 1 (breakout day only) | 2411 | 1.290 | 1.142 |
| 2 | 2108 | 1.118 | 1.112 |
| 3 | 1276 | 1.181 | 1.013 |
| 4 | 680 | 1.115 | 1.099 |
| **5 (≥5 in a row)** | 594 | **1.513** | **1.856** |

The **longest streak is the best**, not the worst: a breakout that caps ≥5 consecutive up-closes is a name in a tight,
persistent multi-day advance that keeps running (post-2015 1.856). This is the opposite of the "stale = exhausted"
intuition. So a **streak cap would cut the strongest trend names** — exactly the wrong move. (These streak-5 names also
cluster in the extended zone, which is why the `5%+` bucket isn't *uniformly* dead — the trend-persistence partly offsets
the extension drag.)

**Conclusion — the verification passes: we are NOT simply buying multi-day runners.** The dead zone is a genuine
**extension** effect (d52), not a pre-entry-streak artifact; a long prior streak alone is benign-to-good. So the correct
discriminator stays the **extension ceiling** (`d52 < ~3%`), *not* a streak cap — a streak filter would be redundant at
best and would wrongly cut the strong streak-5 trend names. The one genuinely weak combination is **moderately
extended + short streak** (`3..5%`/`5%+` at streak 2: 0.95 / 1.00) — gapped into extended territory with no trend behind
it.

### Trailing-6mo MAX ATR% rescues part of the dead zone — slope does not (2026-06-21)

After reclaim / intraday / streak all failed to discriminate the extended dead zone, two trailing-6-month measures
(measured as-of entry, no lookahead): **max ATR% 6mo** = max over the trailing 126 days of the 14-bar log-ATR (the most
violent volatility episode in the last 6 months); **slope 6mo** = OLS slope of ln(close) over 126 bars (6mo log-drift).
Quintiled within the population. Script:
[`scripts/equity/sixmo_atr_slope_deadzone.sql`](../scripts/equity/sixmo_atr_slope_deadzone.sql). Baseline 6,730 trips
(full-6mo-window required), PF clip 1.201.

**Max ATR% 6mo is the FIRST real dead-zone lever — and it's monotone.** Overall, PF rises with the quintile (Q1 calmest
0.956 → Q5 0.94–0.07 range, 1.411); inside the dead zone it's cleaner:

| max-ATR% 6mo quintile | dead-zone n | PF clip | post-2015 |
|---|---|---|---|
| Q1 (calmest 6mo) | 687 | **0.762** | 0.791 |
| Q2 | 677 | 0.985 | 1.118 |
| Q3 | 698 | 1.127 | 1.141 |
| Q4 | 709 | **1.229** | 1.059 |
| Q5 (most violent 6mo) | 699 | 1.104 | 1.13 |

The calmest 6mo quintile is a clear **loser** (PF 0.762) and PF climbs to Q4 (1.229): a name extended above its high
**that has been dead-quiet for 6 months** is the genuinely bad dead-zone trade — an extended drift with no underlying
energy, the stale-continuation profile. **Not just the entry-day ATR% filter in disguise:** the two correlate 0.738, but
the dead-zone gradient holds *within both* entry-day-ATR% halves (entry-ATR%-LOW: Q1 0.77 → Q4 1.18; entry-ATR%-HIGH:
Q2 1.02 → Q4 1.26), so the *historical* energy is distinct from today's bar. The two are complementary: the production
filter wants the **entry bar calm** (ATR% < 0.10, don't buy a jumpy print), this wants the **6mo base to have shown it
can move** — energy in the base, calm on the trigger. Cutting the bottom ~1–2 quintiles of 6mo-max-ATR% lifts the dead
zone from ~1.05 to ~1.15–1.23.

**Slope 6mo is a dud.** Overall the quintiles are flat-noisy (1.14 / 1.36 / 1.13 / 1.29 / 1.13, no gradient), and in the
dead zone the steepest-slope quintiles are if anything slightly *worse* (Q1/Q2 ~1.26 → Q3–Q5 ~0.96–1.08). A steep 6mo
uptrend does **not** save a dead-zone trade — consistent with the streak result: a steep base just means *more*
extension, and extension is the problem. So of the two: **max-6mo-ATR% is a genuine dead-zone refinement (drop the
calmest base), slope is not.**

### How much rvol does the dead zone need to turn positive? rvol ≥ ~8–10 — a damning verdict on naive new-high buying (2026-06-21)

The dead-zone CSVs so far were gated at rvol ≥ 3; to find where the dead zone *turns*, regenerate at rvol ≥ 1 (the
O'Neil "buy the new high" crowd lives mostly at rvol 1–3) and sweep. Population: [5,10]% move, **rvol ≥ 1**, full
production + breadth + heat; dead zone = d52 ≥ 3% above the 52w max close. Script:
[`scripts/equity/deadzone_rvol_sweep.sql`](../scripts/equity/deadzone_rvol_sweep.sql).

**Baseline dead zone, all rvol ≥ 1: PF clip 1.076, post-2015 1.001 — dead flat.** A [5,10]% breakout to a new high,
extended a little above the prior max close, *at any volume*, has essentially **no edge in the modern era**. The
cumulative rvol floor shows it only turns at an extreme level:

| dead zone (d52 ≥ 3%), keep rvol ≥ | n | PF clip | post-2015 |
|---|---|---|---|
| ≥ 1 (all) | 10452 | 1.076 | **1.001** |
| ≥ 2 | 6552 | 1.111 | 1.054 |
| ≥ 3 | 3647 | 1.086 | 1.084 |
| ≥ 5 | 1161 | 1.161 | 1.183 |
| ≥ 8 | 315 | 1.193 | 1.334 |
| **≥ 10** | 171 | **1.498** | **1.838** |
| ≥ 15 | 72 | 1.577 | 1.39 |

The dead zone **does not meaningfully turn until rvol ≥ 8–10.** Below that it crawls 1.08 → 1.16 — barely above churn;
only at **rvol ≥ 10** does it become a real edge (1.498 / post-2015 1.838), but that is ~1% of the band (171 trips). The
contrast with the **fresh-high** zone (d52 < 1%) is the whole point — there, rvol ≥ 2–3 already reaches PF ~1.34–1.37:

| keep rvol ≥ | fresh high (d52 < 1%) | dead zone (d52 ≥ 3%) |
|---|---|---|
| ≥ 2 | 1.342 | 1.111 |
| ≥ 3 | **1.372** | 1.086 |
| ≥ 5 | 1.339 | 1.161 |
| ≥ 8 | 1.817 | 1.193 |

At the fresh high, modest volume (rvol 2–3) is enough; in the dead zone you need rvol ≥ ~8–10 to reach what the fresh
high gives you at rvol 2 — the extension penalty is so steep that only near-pump volume overcomes it.

**The O'Neil warning, made precise.** Most significant [5,10]% breakouts to new highs do *not* come with rvol > 3, and
buying them naively — extended a few % above the prior max close, on ordinary volume — is **PF ≈ 1.0 post-2015 (pure
churn), and outright negative when 6mo-max-ATR% is also low** (the calmest-base quintile, PF 0.762). It is not just a
weak edge; it bleeds via costs/slippage while feeling like "textbook discipline." The actionable takeaways stack: **(1)
buy the fresh high, not the extension** (d52 < ~3%); **(2)** if forced into the dead zone, demand **rvol ≥ ~8–10**, not
the rvol ≥ 2–3 that suffices at the fresh high; **(3)** drop the dead-quiet-base names (low 6mo-max-ATR%) entirely.

**Caveat — do the rvol ≥ 8 and 6mo-max-ATR% filters STACK? Mostly no; at rvol ≥ 8 the ATR% structure is noise.** Within
the dead zone restricted to rvol ≥ 8 (only 299 trips), the 6mo-max-ATR% breakdown is unreliable: full-sample the
calm-base penalty still shows (calm-half PF 0.986 vs energetic-half 1.352; calmest tercile 0.674), but the gradient is no
longer monotone (a **hump** — terciles 0.674 / 1.862 / 1.199) and **post-2015 the sign flips** (calm-half 2.418 vs
energetic 1.138) — classic small-sample instability (~5 trips/yr/cell). The clean monotone version only exists at the
larger rvol ≥ 3 sample (terciles 0.894 / 1.093 / 1.145, n≈1157 each). So the two filters are **mostly redundant**, and
rvol ≥ 8 is the stronger, more reliable one: once you demand pump-level volume you've already selected for energetic
names, and the ATR% filter has little left to do. The 6mo-max-ATR% lever **earns its keep at LOW rvol** (where it
salvages the churny majority), not on top of an rvol ≥ 8 gate.

### The dead zone is a WEAK-breakout phenomenon — strong [10,30]% breakouts wave through it (2026-06-21)

Repeat the distance-from-max-close breakdown on the **strong [10,30]% breakout band** (vs the [5,10]% band above), full
production + breadth + heat. Script: [`scripts/equity/dist_52w_close_1030.sql`](../scripts/equity/dist_52w_close_1030.sql).

| distance from max close ([10,30]%, rvol ≥ 5) | n | PF clip | post-2015 |
|---|---|---|---|
| 0..1% (fresh high) | 109 | 2.388 | 2.497 |
| 1..3% | 225 | 2.133 | 1.954 |
| 3..5% | 285 | 1.853 | 1.261 |
| **5..10% (dead zone)** | 745 | **1.694** | **1.800** |
| 10%+ (far-extended) | 1258 | 1.513 | 1.509 |

The open-ended `5%+` is split here into the **5–10% dead zone** and the **10%+ far-extended/parabolic** tail — the same
bound applied below, so the dead zone is isolated from the parabolic regime. The fresh-high spike still exists (2.39 at
0–1%), and extension still *costs* (decaying 2.39 → 1.69), **but the dead zone never crosses into no-edge** — the 5–10%
band is PF **1.694 / post-2015 1.800** (the strongest post-2015 cell after the fresh high), and even the 10%+ parabolic
tail holds 1.513. Contrast the two tiers head-to-head:

| zone (rvol ≥ 5) | weak [5,10]% | strong [10,30]% |
|---|---|---|
| fresh high (d52 < 1%) | ~1.37 (at rvol ≥ 3) | **~1.79** |
| dead zone (d52 ∈ [3,10]%) | **1.06** (churn) | **1.74** |

**And the rvol rescue requirement vanishes for the strong band** — its dead zone (bounded [3,10]%) is already PF 1.43 at
*any* volume and 1.74 at rvol ≥ 5 (vs the weak band needing rvol ≥ 8–10 to reach ~1.2):

| dead zone (d52 ∈ [3,10]%), keep rvol ≥ | weak [5,10]% | strong [10,30]% |
|---|---|---|
| ≥ 1 (all) | 1.076 | **1.426** |
| ≥ 3 | 1.086 | 1.609 |
| ≥ 5 | 1.161 | **1.741** |

**Mechanism: a 10–30% move IS the conviction signal**, so even a few % above the old high the demand is real; a 5–10%
move into extended territory has *neither* freshness *nor* magnitude — the weakest possible combination, and the only one
where extension turns fatal. **So: there is no waving 5–10% breakouts into the dead zone (PF ≈ 1.0, churn), but 10–30%
breakouts wave through fine (dead-zone PF ≈ 1.7).** The extension cap (`d52 < 3%`) is a real refinement for the *weak* tier and
only a minor optimization for the *strong* tier — which is why production (move ≥ 10%) was never badly exposed to the
dead zone in the first place.

> **Live-trading watch item (2026-06-21):** even though strong 10–30% breakouts are *solidly profitable* across the 3–10%
> dead zone (~1.7), the band still has an **internal valley** (the 3–5% / mid sub-bands soften vs the fresh high and the
> well-extended ends). So *where exactly a breakout lands relative to its prior 52w high matters even inside a profitable
> band* — worth tracking by eye in live trading, not just trusting the band-level average.

#### Dead-zone REFERENCE — max 52w CLOSE vs max 52w intraday HIGH (decision deferred, 2026-06-21)

Should the dead zone be measured from the max 52w **close** (`d52c` = `pct_52w_at_entry`, the current `[3,10]%`
definition) or the max 52w intraday **high** (`d52h` = `pct_52w_high_at_entry`, candidate `[0,10]%`)? The intraday high
sits *above* the close high, so `d52h` is on average **−2.2%** vs `d52c` (a name is "closer to / below" the intraday
high) — "0% above the intraday high" ≈ "a couple % above the close high", a stricter *"cleared actual resistance"*
semantic. The two correlate **0.747** — related but not interchangeable (~25% of names bucket differently). On the
[5,10]% / rvol ≥ 2 system (full production gates + breadth + heat, clip PF), bucketed:

| band above ref | CLOSE (d52c) PF / post | intraday-HIGH (d52h) PF / post |
|---|---|---|
| < 0 (below) | 1.321 / 1.191 | 1.336 / 1.224 |
| 0–3 (fresh) | **1.323 / 1.317** | 1.228 / 1.155 |
| 3–5 | 1.098 / 1.000 | 1.029 / 1.016 |
| 5–7 | 1.128 / 1.178 | 1.074 / 1.150 |
| **7–10** | 1.099 / 0.962 | **0.976 / 0.755** |

**Cumulative ceiling (the decision lens — keep below N):**

| ceiling | CLOSE: PF / post / %kept | intraday-HIGH: PF / post / %kept |
|---|---|---|
| < 0.03 | **1.322 / 1.240 / 51%** | 1.300 / 1.203 / 67% |
| < 0.05 | 1.263 / 1.178 / 69% | 1.244 / 1.169 / 85% |
| < 0.07 | 1.231 / 1.178 / 89% | 1.225 / 1.167 / 96% |

**Findings (both refs ~equivalent in aggregate; pick by what you want to express):**
- **CLOSE ref** gives the single **best filtered cell** (`d52c < 0.03` → 1.322 at 51% kept) and the cleanest *fresh-high*
  signal (0–3 band 1.323 vs the high-ref's 1.228) — because reclaiming the *closing* high is the meaningful level for a
  daily-bar system. Its dead valley is central (3–5, post 1.0).
- **intraday-HIGH ref** flags the **dead extreme more sharply**: its 7–10 band is a genuine *net loser* (post-2015 0.755)
  where the close-ref 7–10 is merely mediocre (0.962). So the dead zone under the high ref is **`[0,10]`** (the 7–10 sub-band
  is the worst part, *inside* the zone, not excluded) — `[0,7]` would wrongly cut at the boundary of the deadest names.
- There is **no sharp knee at 7 or 10 under either ref** — the band softens smoothly from ~3% on, so the ceiling is a
  capacity dial, not a data-given break.

**Decision: DEFERRED.** The two are close enough that switching isn't compelling on aggregate PF — lock the choice when
intraday entries put real fills behind the distinction (the intraday-high "cleared actual resistance" semantic may matter
more once entering mid-session). Current production keeps the **close ref**. Script:
[`scripts/equity/deadzone_ref_compare.sql`](../scripts/equity/deadzone_ref_compare.sql).

#### …but 6mo-max-ATR% (calm base) IS universal — the calmest quintile is a net loser on the STRONG band too (2026-06-21)

The extension penalty was weak-band-only — but the **6mo-max-ATR% calm-base penalty is not.** Running the quintile
breakdown on the strong [10,30]% band (where the dead zone itself is alive), the calmest-6mo quintile is a **net loser
even here**, and it survives the extension control.

> **Dead-zone bound:** the dead zone here is `d52 ∈ [3%, 10%]` — *bounded above*, not the open-ended `d52 ≥ 3%` used in
> some earlier tables. On the strong band the uncapped `d52 ≥ 3%` set is mostly far-extended names (1187 of 2180 trips
> sit at d52 > 10% — a different, parabolic regime). Capping at 10% isolates the actual "just past the breakout" dead
> zone. The calm-base result is identical either way (it is not an artifact of the far-extended tail).

| 6mo-max-ATR% quintile, [10,30]% rvol ≥ 5 | OVERALL n=600/q | OVERALL post-2015 | DEAD-ZONE [3,10]% n≈199/q | DEAD-ZONE post-2015 |
|---|---|---|---|---|
| Q1 (calmest 6mo) | **0.987** | **0.804** | **0.927** | **0.780** |
| Q2 | 1.371 | 1.341 | 1.764 | 1.081 |
| Q3 | **2.101** | 1.781 | 1.810 | 1.774 |
| Q4 | 1.923 | 2.350 | **2.132** | 3.294 |
| Q5 (most violent) | 1.702 | 1.722 | 1.679 | 1.445 |

Unlike the noisy rvol ≥ 8 stacking, this is solid (600 trips/quintile overall, ~199 in the bounded dead zone): the
calmest quintile is **PF ~0.93–0.99 / post-2015 0.78–0.80** — a *net loser* while every other cut of the strong band is
healthy (PF 1.4–2.4). The shape is a **hump** (calmest loses, middle/Q4 best, most-violent Q5 gives some back — the
pump-and-revert names), so the rule is **cut the bottom quintile**, not chase the top. It holds at rvol ≥ 1 too
(monotone-ish Q1→Q5: 1.126 / 1.363 / 1.474 / 1.468 / 1.533). And the bounded dead-zone column ≈ the overall column, so it
is **orthogonal to extension** — not the dead zone in disguise. (The weak [5,10]% band, dead zone bounded [3,10]%, is the
same shape and even sharper at the bottom: Q1 0.758 / post-2015 0.750 → Q4 1.248.)

**Synthesis — the two dead-zone levers split by generality:** *extension (d52)* is a **weak-breakout-only** effect
(strong breakouts wave through); *6mo-max-ATR% calm-base* is **universal** — the calmest-base quintile is a net loser on
*both* tiers (weak 0.76, strong 0.99 / post-2015 0.80), independent of extension and move size. **So calm-base is the
most generalizable signal found in this whole dead-zone investigation — and a genuine candidate for the production daily
system (move ≥ 10%):** it cuts a net-losing quintile the current filters don't touch. (Deferred to wiring: needs a
trailing-126-day rolling-max-of-14-bar-log-ATR computed in-engine; logged here as the next filter to test when that
plumbing exists, alongside the past-runner ADR%/slope floor.)

### 6mo-max-ATR% × 6mo-slope — they do NOT combine; max-ATR% is the whole signal, slope is dead (2026-06-21)

The two trailing-6mo measures had only been tested in isolation. Do they combine? Population: **production system, rvol
lowered to 2** — move ∈ [10,30]%, rvol ≥ 2, full production + breadth + heat; 5,862 trips, PF clip 1.476. Script:
[`scripts/equity/atr_slope_combine.sql`](../scripts/equity/atr_slope_combine.sql).

**Correlation is only moderate (0.543)** — meaningful but far from collinear, so there *was* room for them to carry
independent information. They don't, because **slope has no standalone edge to begin with.** In isolation:

| quintile | max-ATR% PF / post-2015 | slope PF / post-2015 |
|---|---|---|
| Q1 | 1.094 / **0.983** | 1.496 / 1.256 |
| Q2 | 1.389 / 1.126 | 1.546 / 1.383 |
| Q3 | 1.629 / 1.836 | 1.491 / 1.511 |
| Q4 | 1.480 / 1.559 | 1.517 / 1.644 |
| Q5 | 1.579 / 1.486 | 1.387 / 1.373 |

Max-ATR% shows the familiar calm-base penalty (Q1 net-loser-ish 1.09 / post-2015 0.98, rising to ~1.6); **slope is flat
across all five quintiles** (1.39–1.55, no gradient; the steepest quintile is if anything the *worst*). The 2D grid (PF
by ATR% quintile rows × slope quintile cols) confirms slope adds nothing within an ATR% stratum:

| ATR%-q ↓ / slope-q → | s1 | s2 | s3 | s4 | s5 |
|---|---|---|---|---|---|
| aq1 | 0.93 | 1.29 | 1.05 | 1.26 | 1.07 |
| aq2 | 1.78 | 1.31 | 1.19 | 1.44 | 0.98 |
| aq3 | 2.23 | 1.79 | 1.36 | 1.68 | 1.23 |
| aq4 | 1.27 | 1.70 | 1.83 | 1.52 | 1.26 |
| aq5 | 1.80 | 1.88 | 2.01 | 1.47 | 1.48 |

Row averages climb cleanly aq1→aq5 (the ATR% gradient); across-row (slope) it's **non-monotone scatter** — the grid is
the ATR% signal running vertically with slope-noise horizontally. The combined-gate test seals it:

| gate | n | PF | post-2015 |
|---|---|---|---|
| baseline | 5862 | 1.476 | 1.433 |
| drop ATR Q1 | 4689 | **1.530** | 1.495 |
| drop slope Q1 | 4689 | 1.472 | 1.471 |
| drop BOTH Q1 | 3931 | 1.503 | 1.507 |

Dropping the calm-ATR quintile does all the work (1.476 → 1.530); dropping the slope quintile does **nothing** (→ 1.472,
just discards ~20% of trips); dropping both is *worse* in-sample than ATR-alone (1.503 < 1.530). **Verdict: they don't
combine — not because of collinearity but because slope carries no edge. Keep max-ATR% (drop the calmest quintile); drop
slope from consideration entirely.**

> ⚠️ **SUPERSEDED (2026-06-21, later same day).** This subsection used **two wrong inputs**: (1) the "slope" was an OLS
> log-price regression slope, NOT the v2 measure (max 14-day return), and (2) PF was **clipped**. Re-run with the *correct*
> measures (engine log-ATR + max-14d-return) on **raw PF**, **slope is NOT dead** — it is a real, monotone signal, and so
> is max-ATR%. The "do they combine" answer (they're largely redundant substitutes) still holds, but "slope carries no
> edge" was an artifact of the wrong measure + clip. See **§ Why the past-runner monotonicity breaks under v3 production**
> below for the full corrected story.

#### ⭐ SANITY CHECK — the v2 06-20 "Past-runner personality" section, faithfully reproduced (2026-06-21)

Before concluding the clip "kills" the past-runner signal, reproduce the original
[`momentum_v2_results.md` § Past-runner personality](momentum_v2_results.md) **exactly** — same three measures, same
buckets, same population, **raw PF** (its convention). Script:
[`scripts/equity/pastrunner_repro.sql`](../scripts/equity/pastrunner_repro.sql).

**How the three measures were calculated** (trailing **126 trading days** = 6 months, **lagged 1 bar** so the signal
bar's own range is excluded — no lookahead):
- **max ADR 6mo** = max over 126d of `mean₁₄( adj_high / adj_low − 1 )` — range-based; the literal Sykes ADR.
- **max ATR% 6mo** = max over 126d of `mean₁₄( true_range / adj_close )` — gap-aware; **TR divided by the close**
  (NOT a log measure; this is the formula detail that surprised us — it is `TR/close`, not `ln(high/low)`).
- **max ret 6mo (the "slope")** = max over 126d of `adj_close_t / adj_close_{t−14} − 1` — a 14-day directional burst
  (NOT an OLS regression slope; "slope" here is the best trailing 14-day return).

Population: v2-era production (move ≥ 10%, **rvol [6,20]**, ATR% < 0.11, tight < 4.5, price ≥ $5, 5d stop), breadth,
≥ 2005. **The reproduction matches the original to within rounding** (n = 2,619 vs the documented ~2,580):

| bucket | max ADR 6mo (orig → repro) | max ret/slope (orig → repro) | max ATR% (orig → repro) |
|---|---|---|---|
| bottom | 0.955 → **0.998** | 1.428 → 1.177 | 1.11 → 1.19 |
| top | **3.126 → 3.165** | **3.599 → 3.589** | **2.91 → 2.86** |

So **the 06-20 work was correct and reproduces** — it was a real, monotone, strong signal *on raw PF*. Now add the clip
column on the **identical buckets** — the bottom is unchanged (no winners to clip) but **the top bucket collapses**, which
is the whole story:

| bucket | ADR raw → clip | ret/slope raw → clip |
|---|---|---|
| bottom (<4% / <15%) | 0.998 → 0.998 | 1.177 → 1.177 |
| mid | 1.84 → 1.84 | 1.71 → 1.70 |
| **top (15%+ / 130%+)** | **3.165 → 1.712** | **3.589 → 1.297** |

The lower/mid buckets are **identical raw vs clip**; only the top buckets fall — ADR 3.165 → 1.712, and the slope's top
**3.589 → 1.297** (from strongest bucket to *below the middle*). The top bucket's win% is only **41.7%** — it wins on a
handful of huge moves, the textbook lottery signature. **Verdict: the 06-20 measurement was sound; the "monotone PF 3.6
top" was real raw but lottery-driven at the top. Under the clip the durable part is a bottom-only floor — boring
(no-volatile-fortnight) names are dead (PF ~1.0, survives the clip) — while "past runners run to huge winners" is true
but uncliptably lumpy.**

> **Formula invariance (checked 2026-06-21, answering "what if ATR% used log space instead of TR/close?"):** swapping the
> original `mean₁₄(TR/close)` for the **engine's log-ATR** — `mean₁₄` of the **log true range**
> `max( ln(high)−ln(low), |ln(high)−ln(prevClose)|, |ln(low)−ln(prevClose)| )` (the proper TR, including the gap-to-prior-close
> legs — NOT just `ln(high/low)`, which would be the log-space *ADR*) — changes nothing: the two correlate **0.959**, both
> top deciles raw ~2.69 → clip ~1.66/1.68, same shape. (Range-ADR vs log-ADR likewise correlate **0.992**.) So the measure
> *formula* — linear-TR/close vs log-true-range vs range-based — is a wash; the clip-vs-raw distinction is the entire
> difference, not the choice of volatility proxy.

#### Why this looked stronger yesterday than today — it's the CLIP, not the rvol (2026-06-21)

The v2 06-20 session recorded the past-runner (max-6mo-14d-ATR% / slope) as a **strong, monotone, top-decile-PF-~2.7,
era-robust** signal with "higher is better → use a FLOOR." Today both look weak (ATR%) or dead (slope). The cause is
that the 06-20 result was **raw PF, measured before clip-everywhere became the standard** — and raw PF is exactly the
lottery-winner mirage the clip was adopted to kill. Decile breakdown, [10,30]% rvol ≥ 5, **raw vs clip side by side**:

| max-6mo-ATR% decile | raw PF | clip PF | | slope decile | raw PF | clip PF |
|---|---|---|---|---|---|---|
| D6 | **6.08** | 2.17 | | D6 | **6.34** | 2.22 |
| D10 (top) | **2.65** | 1.71 | | D7 | **3.58** | 1.71 |

The top ATR% decile reproduces the remembered **raw 2.65 ≈ "~2.7"** — and **clips to 1.71.** Slope's mid deciles are
raw **6.34 / 3.58** → clip **2.22 / 1.71**: slope's apparent monotone edge was a handful of moonshots clustered in its
mid-positive deciles, and the clip removes them, leaving it flat. So **slope was never a directional signal — it was a
lottery-winner concentrator**, and the clip correctly kills it. For ATR% the *penalty* side (calmest quintile = net
loser) survives the clip cleanly and is the durable, actionable part; the high-ATR% *bonus* was mostly right-tail the
clip strips. **(Lowering rvol 5 → 2 dilutes the ATR% gradient somewhat but is NOT the main cause — the raw-vs-clip gap
above is the whole story.)** Net: this **retroactively corrects the deferred "past-runner floor" candidate** — under the
clip there is no strong monotone floor, only "drop the calmest-base quintile." A clean vindication of the clip
methodology: it caught a tail-contaminated signal before it became a production filter.

**Why it looked "monotonically rising" yesterday — the cumulative-FLOOR + raw combo.** The 06-20 view was a *cumulative
floor* (keep max-ATR% ≥ the Nth decile boundary — the natural way to read "use a FLOOR"), which smooths per-bucket noise
into a clean line, AND it was *raw* PF. Both effects compounded. The cumulative floor, raw vs clip ([10,30] rvol ≥ 5):

| keep max-ATR% ≥ | n | raw PF | clip PF |
|---|---|---|---|
| d0 (all) | 3000 | 2.20 | 1.66 |
| d2 | 2400 | 2.40 | 1.77 |
| d4 | 1800 | 2.63 | 1.86 |
| **d5** | 1500 | **2.70** | 1.84 |
| d6 | 1200 | 2.16 | 1.78 |
| d9 (top decile) | 300 | 2.65 | 1.71 |

Raw, it rises smoothly to the remembered **2.70 peak at d5** → "monotone, higher is better." Clipped, it barely moves
(1.66 → 1.86 peak → back to 1.71) and is **non-monotone** — raising the floor past ~the 40th percentile *hurts*. So three
things stacked to make it look clear-cut yesterday and they were all removed/reframed today: **(1) cumulative-floor view**
(smooths noise → line) vs per-bucket quintiles (show the hump); **(2) raw PF** (lottery tail rides the high-ATR% cells)
vs clip; **(3) rvol ≥ 5** (sharper) vs rvol ≥ 2 (diluted). The durable truth under the clip is a **bottom-only floor**:
the calmest quintile is a net loser, cutting *just that* helps; above it, more historical vol plateaus then fades into
pump-and-revert. It was clear-cut — clear-cut *wrong*, exactly the failure mode the clip exists to catch.

#### ⭐ Why the past-runner monotonicity BREAKS under v3 production — a one-step ablation (2026-06-21)

The above used clip + an OLS slope. Redone with the **correct measures** (engine log-ATR; max-14d-return slope) on **raw
PF**, **both signals are real and monotone** on the v2-exact population — and the puzzle becomes: *why does that clean
monotone ladder fall apart on v3 production?* Resolved by ablating v2-exact → v3-production **one filter at a time** and
watching the max-log-ATR buckets (v2 fixed edges: `<4 / 4-6 / 6-8 / 8-11 / 11-15 / 15+`%). Script:
[`scripts/equity/pastrunner_ablation.sql`](../scripts/equity/pastrunner_ablation.sql). Raw PF, breadth, ≥ 2005.

| step | change applied | n | 6-8% | 11-15% | 15%+ | monotone? |
|---|---|---|---|---|---|---|
| S0 | **v2-exact** (rvol [6,20], no move cap, $5, ATR%<0.11, breadth-only) | 2619 | 1.60 | 2.08 | 2.99 | ✅ |
| S1 | + 30% move cap | 2277 | 1.78 | 2.30 | 3.56 | ✅ |
| **S2** | **rvol [6,20] → ≥ 5** | 3498 | **3.26** | 1.91 | 2.65 | ❌ |
| S6 | + ATR%<0.10, price ≥ $1, intraday gate, **heat** (= full prod) | 2999 | 3.66 | **1.44** | 3.33 | ❌ |

**Two independent culprits, and the move cap is innocent.** S1 (the 30% move cap) keeps it monotone — *sharpens* it, even.
S2 is where it breaks, and isolating the two halves of the rvol change shows it's the **floor**, not the cap: removing the
rvol-20 upper cap alone stays monotone (the rvol > 20 tail is a tame 63 trips, PF 2.08); **lowering the floor 6 → 5 alone
breaks it** (6-8% bucket → 3.36).

**Culprit #1 — the rvol 5-6 cohort spikes a bucket via ONE trade.** Splitting the 6-8%-ATR bucket by rvol:

| 6-8%-ATR cohort | n | win% | max ret | PF | net |
|---|---|---|---|---|---|
| rvol ≥ 6 (v2 had these) | 562 | 56.2 | +40% | 1.81 | $96k |
| **rvol 5-6 (NEW)** | 183 | 53.0 | **+2,368%** | **7.6** | $263k |

The new cohort's PF 7.6 is **one trade** — `SNS`, 2009-12-15, rvol 5.5, **+2,368%** (a post-crash recovery moonshot), worth
$237k of the cohort's $263k. **Ex that single trade the cohort is PF 1.66** (ex top-3: 1.47) — fully in line with rvol ≥ 6.
So the "6-8% spike" that breaks the ladder is a **raw-PF concentration artifact** the rvol ≥ 6 floor happened to exclude —
exactly the contamination the clip was built to catch.

**Culprit #2 — heat hollows out the 11-15% band by removing its WINNERS.** Heat removal rises monotonically with
ATR-history, but at 11-15% it cuts the *good* trades:

| ATR bucket | % cut by heat | PF of the cut trades |
|---|---|---|
| <4% | 11.1 | 1.04 |
| 8-11% | 33.2 | 1.69 |
| **11-15%** | **43.4** | **2.94** ← winners removed |
| 15%+ | 53.7 | 0.98 |

Heat cuts 43% of the 11-15% band, and those cuts are **PF 2.94** — so the band *collapses to 1.44* in production (the good
trades got filtered out). At 15%+ heat cuts junk (PF 0.98), so it helps there. Restoring rvol ≥ 6 to full production does
**not** fully fix the dip *because heat is the one still gutting 11-15%* — they are two separate distortions.

**Resolution of the whole confusing thread.** The past-runner signal is genuine and monotone; it "stops working" under v3
production for **two non-signal reasons**: (1) the lower rvol floor re-admits extreme lottery winners that spike raw-PF
buckets (raw-PF artifact), and (2) heat removes the genuinely-good winners from the upper-middle volatility band (real
filter interaction). **Heat and the past-runner signal overlap on the same frothy names** — which is *why* a past-runner
floor "doesn't combine" with production: heat is already doing part of the same job (removing froth), just imperfectly
(it over-cuts the profitable 11-15% band). **Strategic note (per the lottery-tail decision):** these top buckets are
carried by rare huge winners — kept on purpose; on the right side of a bubble that is where the money is, and the +50%
clip would discard them. The signal lives in the **raw-PF, rvol ≥ 6, un-heat-filtered** population.

#### ⭐ Is the TOP bucket genuinely well-distributed, or also lottery-driven? — it's the MOST concentrated of all (2026-06-21)

A natural worry after the SNS finding: is the strong top bucket (15%+ ATR / 130%+ slope, PF ~3) a real broad edge, or a
single-trade illusion like the spurious 6-8% spike? Concentration test on the v2-exact population (raw PF) — each
bucket's PF after removing its top-N winners, and the share of total **winning** profit carried by the single biggest
trade:

| max-log-ATR bucket | n | PF all | ex top-1 | ex top-3 | ex top-5 | top-1 % of win-profit | top-3 % |
|---|---|---|---|---|---|---|---|
| 6-8% | 583 | 1.60 | 1.57 | 1.52 | 1.47 | 1.8 | 5.1 |
| 8-11% | 446 | 1.77 | 1.72 | 1.64 | 1.58 | 2.5 | 7.1 |
| 11-15% | 205 | 2.08 | 1.95 | 1.82 | 1.70 | 6.2 | 12.3 |
| **15%+ (top)** | 173 | **2.99** | **1.60** | **1.42** | **1.28** | **46.3** | **52.6** |

| max-slope bucket | n | PF all | ex top-1 | ex top-5 | top-1 % of win-profit | top-3 % |
|---|---|---|---|---|---|---|
| 30-50% | 668 | 1.64 | 1.58 | 1.48 | 3.2 | 6.8 |
| 80-130% | 109 | 2.37 | 2.12 | 1.71 | 10.5 | 19.9 |
| **130%+ (top)** | 84 | **3.59** | **1.28** | **0.82** | **64.4** | **71.9** |

**The top bucket is NOT better distributed — it is the MOST lottery-driven bucket of all.** One trade carries **46%**
(ATR) / **64%** (slope) of the bucket's entire winning profit; the top *three* carry 53% / 72%. Strip the top winner and
PF falls 2.99 → 1.60 (ATR) and 3.59 → **1.28** (slope); strip the top 5 and the slope top bucket is **0.82 — a net
loser**. This is the same shape as the spurious SNS spike — except here it is *inherent* to the cohort, not a filter
artifact.

**The decisive reframe: the past-runner "monotone PF ladder" is also a monotone CONCENTRATION ladder — they are the same
gradient.** The top-1-share climbs 1.8% → 6.2% → **46.3%** (ATR) and 3.2% → 10.5% → **64.4%** (slope) in lockstep with PF.
The *base* (ex-top-5) PF does **not** rise across buckets — it is flat-to-declining (ATR 6-8% 1.47 / 11-15% 1.70 / 15%+
1.28; slope 30-50% 1.48 / 130%+ **0.82**). So high past-volatility/momentum does **not** mean a better *typical* trade —
it means **a fatter right tail**. The signal is real but it is a **pure convexity / tail bet**: *"high-past-vol names are
where the moonshots happen,"* not *"high-past-vol names win more often."*

**Sizing consequence:** this cohort must be played as **many tiny lottery-ticket bets to CATCH the rare 5×**, never
concentrated — in any single trade you almost certainly will not be holding the winner (top bucket win% is only ~48-52%,
and one name is half the profit). That is exactly how to be "on the right side of a bubble." It also means the headline
PF ~3 is **not** a sizing-into-able expectation; the realistic per-trade base is the ex-top-5 figure (~1.3 ATR, <1 slope).
Script: [`scripts/equity/pastrunner_concentration.sql`](../scripts/equity/pastrunner_concentration.sql).

#### ✅ WIRED IN — the max-log-ATR FLOOR at ≥ 0.04 (production default, 2026-06-21)

The **bottom** of the past-runner ladder is the robust, well-distributed part (unlike the lottery-tail top): dead-quiet
base names are uniformly bad and removing them lifts the *whole distribution*, not just the tail. So a **floor** (not the
abandoned monotone "higher is better" framing) goes into production. Cumulative floor sweep on full production
(rvol ≥ 5, raw PF + ex-top-5 base + post-2015):

| keep max log ATR ≥ | trips kept | raw PF | ex-top-5 base | post-2015 |
|---|---|---|---|---|
| all | 100% | 2.205 | 1.639 | 2.066 |
| 0.035 | 89.5% | 2.292 | 1.684 | 2.153 |
| **0.04 (chosen)** | **82.4%** | **2.346** | **1.705** | **2.216** |
| 0.05 | 64.2% | 2.569 | 1.799 | 2.426 |

It is a **smooth capacity-vs-edge slope, no sharp knee** — every metric rises monotonically as the floor rises, and
crucially the **ex-top-5 base lifts alongside raw PF** (1.64 → 1.71 at 0.04), confirming a *broad* gain, not a tail
artifact. Chose **0.04** (~p20): cuts the dead bottom while keeping 82% of capacity; 0.05+ keeps lifting PF but costs a
third of the trips (a worse trade than the first cut, and capacity matters for a real book). **Wired into the engine**
(`EntryConfig.MinMaxAtrLog`, default 0.04; CLI `--min-max-atr-log`): the 126-bar rolling max of the 14-bar log-ATR,
snapshotted pre-push (no lookahead). Unfiltered-engine headline **PF 1.851 → 1.922, 6,647 → 5,617 trips**. Script:
[`scripts/equity/maxlogatr_floor_sweep.sql`](../scripts/equity/maxlogatr_floor_sweep.sql).

> **Engine cleanup (same commit):** the log/linear true range now uses the identity `TR = max(high,prevClose) −
> min(low,prevClose)` (log: `ln(max(hi,pc)/min(lo,pc))`) instead of the explicit 3-leg `max(hi−lo, |hi−pc|, |lo−pc|)` —
> since `high ≥ low`, the max leg is always highest-endpoint minus lowest-endpoint. **Verified identical to 1e-15** over
> 173k bars and the full backtest is bit-identical (PF 1.922 / 5,617 trips unchanged). Per user's simplification note.

---

#### ⭐ FLOAT breakdown — small dollar-float (< $300M at entry) is a clean +0.5 PF edge (2026-06-22)

New structural feature: **public float**, from SEC `dei:EntityPublicFloat` (the 10-K cover-page USD
market value of non-affiliate shares). Downloaded via SEC's bulk XBRL **frames API** (one CIK-keyed
call per period; 2009→2026 = 70 frames / 46s) into `data/equity/float/float.db` (`float_sec`). The
ticker→CIK bridge for delisted names — where momentum runners end up — was the hard part: SEC/edgar
ticker maps cover only ~46% of our cumulative universe, so the rest were recovered via Polygon
ticker-details queried with a `?date=` inside each ticker's listing window (96.9% of CS+ADRC resolved
to a CIK; see `scripts/equity/resolve_cik.py`, `download_float.py`).

**The conversion matters (per user).** `EntityPublicFloat` is a *dollar* value anchored to the close on
the issuer's 2nd-fiscal-quarter-end (`period_end`). A raw dollar float conflates company *size* with
float-*tightness* and is anchored to a stale price. So we convert to a **share count** and re-anchor to
the entry-day price: `float_usd_at_entry = float_usd × adj_close[entry] / adj_close[period_end]`. (A
1M-share float is $1M behind a $1 stock but $100M behind a $100 stock — bucket on the entry-anchored
dollar value.) This is **split-safe in adjusted space** — the split adjustment factor cancels in the
ratio (verified on SMCI across its 2024 10:1 split: raw-consistent and adj-consistent both give the
same $54.2B entry float). No-lookahead via ASOF join on `known_date = period_end + 90d` (the 10-K
filing deadline) ≤ `entry_date`. Script: `scripts/equity/float_breakdown.sql`.

Population: v3 production trips (PF 1.922) + breadth + heat + ≥2005, **+50% clip**. Coverage is 47.5%
overall but that's a **pre-2010 artifact** (XBRL float reporting didn't exist): pre-2010 is 5/659
covered, **2013+ is 1101/1465 = 75% covered**. The edge below is shown on both windows.

Per-bucket PF by `float_usd_at_entry` (full sample, diagnostic):

| float$ at entry | n | win% | PF (clip) | post-2015 |
| --------------- | --- | ---- | --------- | --------- |
| < $50M          | 80  | 60.0 | 2.50      | 2.13      |
| $50–150M        | 224 | 59.4 | 2.29      | 1.98      |
| $150–300M       | 191 | 59.7 | 2.72      | 2.99      |
| $300–750M       | 247 | 54.3 | 1.35      | 1.49      |
| $750M–2B        | 263 | 52.1 | 1.27      | 1.09      |
| > $2B           | 241 | 54.8 | 1.49      | 1.58      |

Cumulative CEILING (the decision lens) — keep float below N:

| keep float < | trips | % covered | PF (clip) | post-2015 | 2013+ era |
| ------------ | ----- | --------- | --------- | --------- | --------- |
| < $150M      | 304   | 24.4      | 2.36      | 2.03      | 2.05      |
| < $300M      | 495   | 39.7      | **2.47**  | 2.26      | **2.32**  |
| < $750M      | 742   | 59.6      | 2.13      | 2.07      | 2.04      |
| (all covered)| 1246  | 100       | 1.87      | —         | 1.81      |
| float ≥ $750M| 504   | 40.4      | 1.37      | 1.30      | —         |

**Read:** the edge is **monotone and sharp** — `float < $300M at entry → PF ~2.4–2.5` vs ~1.8 covered
baseline, and **big float (≥ $750M) is the drag at PF ~1.37**. This is NOT the lottery-tail problem: it
survives the +50% clip, **win rate RISES** in the low buckets (57–60% vs 52% for big float), and it
holds in the modern 2013+ window (PF 2.32 at < $300M). The `no-data` bucket is benign — 2013+ no-data
is PF 1.88 / win 57%, i.e. average, so a float filter wouldn't be dumping a profitable cohort silently.

**Status: NOT yet wired into the engine.** Float lives in a separate DB and needs an engine join +
the >75%-only-modern coverage caveat thought through (how to treat pre-2013 / no-data trips in a live
filter). Strong candidate for a production filter (`float_usd_at_entry < ~$300M`) or a sizing tilt.
Consistent with the recurring theme — small, tight, controlled-conviction names are HighFlyer's edge.

---

## Active production-defining findings (carried from v2, still live)

These define the current entry/exit and remain in force. Full derivation and era-split tables are in
[`momentum_v2_results.md`](momentum_v2_results.md) at the linked sections; summarized here as the live spec.

- **5-day time-stop, NO price stop** — on high-edge breakouts the move is concentrated in the first ~5 days;
  moving stops around doesn't help. The "disaster exit" is a SHORT setup, not a long exit (off).
  *(v2: "5-day time-stop, NO price stop"; "edge concentrated in first ~5 days".)*
- **Entry-day move is a NOTCH, not a ramp** — 25–30% is the best band, 30–40% the worst (exhaustion). Cap at 30%.
  ⏳ *Re-confirm under the clip — see "next" below.* *(v2: "Entry-day move — SWEET-SPOT NOTCH".)*
- **rvol three regimes** — <1 fakeout, ~6–15 durable edge, >15 toxic blow-off (the 2020-21 pump cohort, not
  buyouts). Floor at 5; the 30%-move cap supersedes an rvol upper cap. *(v2: "rvol sweep"; "why >15 is neutral".)*
- **Past-runner floor** (deferred, not in engine) — max-over-6mo of 14d ATR%/slope; top decile PF ~2.7, monotone,
  era-robust, gate-amplified. "Higher is better" → use a FLOOR. *(v2: "Past-runner personality".)* **Now also the
  designated rescue for the weak `$1–3` price band** admitted by the 2026-06-21 floor drop (5 → 1): the hypothesis
  is that a low-priced name that has *recently been a runner* (high max-ADR%/slope) carries the breakout edge that
  a generic $1–3 name lacks. First thing to test when this floor goes into the engine.

### Regime filters (post-hoc, not yet in engine)

- **HEAT** (Sykes-inspired, ⭐) — daily mean return of the **top 1% gainers** (**CS/ADRC** + 30-cal-day ADV ≥ $1M
  universe; **+1000% per-stock return clip is LOAD-BEARING**), trailing-10d lagged = `h10`. **Skip entries when
  h10 ≥ 0.25** (80th pctile). Froth is bad for breakouts. *(Heat universe unified to CS/ADRC on 2026-06-21 — see
  next subsection; under the clip the gate gives clip-PF 1.702 / post-2015 1.686 at h10<0.25 vs 1.590/1.520 ungated.)*
- **BREADTH** — `pct_above_20` on CS/ADRC + 30-cal-day ADV ≥ $1M (reproduces the production `breadth.parquet`).
  PF rises with breadth to a peak at ≥0.70, then rolls over; current gate 0.5.
- **COLD** (bottom-1% losers) — same axis as heat (corr −0.65), redundant; not wired.
- Both built by **`scripts/equity/build_breadth_and_heat.sql`**.

#### Heat universe unified to CS/ADRC — drops non-frothy CEFs/preferreds, gates STRONGER, no blocklist (2026-06-21)

Originally the heat universe read the **whole liquid tape** (any ticker ≥ $1M ADV) + a hardcoded `is_test_ticker`
blocklist, on the rationale that top-gainer froth lives in names that lack a clean CS/ADRC ref row. **That
rationale was wrong** (caught by inspection): a recent IPO *is* CS and a foreign listing *is* ADRC — both get ref
rows. The ~16,400 ref-less tickers in the price table break down as ~5,600 genuine non-equity (warrants `…W`,
units `…U`, rights `…R`, dotted class/`.WS`) and the rest mostly **closed-end funds** (MHD, NCV, NRK, DHY, …) and
**preferreds** (lowercase-`p` tickers). CEFs and preferreds **structurally cannot be top-1% gainers** — a muni CEF
doesn't pop 50% — so including them only **dilutes** the froth mean.

Empirically the two universes are **0.815-correlated** but materially different (mean h10 0.172 whole-tape vs
0.179 CS/ADRC — the CS/ADRC version runs *hotter*, confirming the dilution). And the CS/ADRC heat **gates
strictly better** under the clip (production trips, breadth on, ≥2005; script
[`scripts/equity/heat_csadrc_gate_sweep.sql`](../scripts/equity/heat_csadrc_gate_sweep.sql)):

| gate | n | PF clip | clip post | (old whole-tape clip post) |
|---|--:|--:|--:|--:|
| no heat gate | 3,678 | 1.590 | 1.520 | — |
| h10 < 0.20 | 2,476 | 1.778 | **1.819** | 1.722 |
| h10 < 0.25 | 2,772 | 1.702 | **1.686** | 1.620 |
| h10 < 0.30 | 3,051 | 1.689 | 1.666 | 1.527 |

**✅ DECISION (2026-06-21): heat is now built on the CS/ADRC inner join** (unified with breadth + the engine).
The new 80th pctile of h10 is **0.251 ≈ the old 0.25**, so the **gate threshold is unchanged**; it just gates a
cleaner series (post-2015 1.686 vs 1.620 at h10<0.25). `build_breadth_and_heat.sql` heat block switched to
`JOIN ticker_reference ... WHERE type IN ('CS','ADRC')`. The `is_test_ticker` blocklist is now **removed entirely**
(2026-06-21): with the CS/ADRC filter on BOTH universes it never fired — *proven* no-op, 0 test-ticker rows survive
CS/ADRC + $1M ADV on breadth — and a filter that can't fire only misleads a reader into thinking test tickers are a
live threat. The **+1000% return clip stays** (still needed for residual real-CS/ADRC glitches like LU/EPIX).

- **TEST-TICKER mechanics (verified 2026-06-21).** NASDAQ test symbols (ZXZZT etc.) have corrupt 0.0001→$200k
  prices but **NO `ticker_reference` row at all** (an earlier note wrongly called them "tagged CS" — corrected).
  Every CS/ADRC **inner join** therefore drops them for free: the engine (`Backtest.fs`), breadth, and now heat.
  Confirmed 0 test-ticker trips in every dump this session (the 3 `ZY*` names — ZYBT/ZYME/ZYXI — are *real* CS).
  Do **not** remove the engine's CS/ADRC inner join thinking a blocklist covers it — no blocklist runs in the engine.

---

## Reproduction

```bash
# v3 default (2026-06-21): NO price stop, 5-day time-stop, next-open exit; entry filters
# ATR% < 0.10 (log), tightness < 4.5 (linear), entry-move band [10%, 30%), rvol >= 5 (no upper cap),
# price >= $1, ADV >= $100k, 52w-close >= 0.95, intraday close/open-1 >= -0.07 (reject deep fades).
# Run from dataset start for the 252-day warmup; filter entries to >=2005 post-hoc.
dotnet run --project TradingEdge.HighFlyer -c Release -- \
  --start-date 2003-09-10 --end-date 2026-05-13 -o /tmp/v3.csv

# then apply, post-hoc:
#   breadth_lag1 > 0.5   — LAG(pct_above_20) by 1 trading day on
#                          data/equity/momentum_v0/breadth.parquet, joined on entry_date
#   heat h10 < 0.25      — join data/equity/momentum_v0/heat.parquet on entry_date
#   entry_date >= 2005-01-01
#
# PF/return figures: clip per-trade return at +50% (LEAST(ret,0.50)); decide on cumulative views.
```

All defaults live in `TradingEdge.HighFlyer/Backtest.fs` (`defaultConfig`). Threshold sweeps use the CLI
overrides `--max-atr-pct`, `--max-tightness`, `--rvol-min/--rvol-max`, `--up-threshold/--max-up-threshold`,
`--exit-time-cap`, `--stop-low-window`, `--trail-window`.

For engine internals, the dividend-adjustment data fix, and the in-code TODO, see
[`momentum_v2_results.md`](momentum_v2_results.md) (§ Engine notes / Data fix / Known TODO) — unchanged in v3.

---

## Next session — INTRADAY entries near the open (planned 2026-06-21)

The whole system so far enters **at the close**. Next: test entering the same high-conviction setups **near the
open** and holding a few days, to capture the part of the move that happens *during* the signal day.

**Why retry — it failed before, but under different conditions.** The old opening-range-breakout test used
essentially *every* rvol stock with **no tightness/ATR%/move/breadth/heat filter** — none of the edge-concentrating
conditions discovered since. An intraday entry on the *filtered* population is a genuinely different experiment.

**Open questions to design around:**
- **Entry timing:** at the open? after an opening range (first 5/15/30 min)? "Buy near the open if the setup is
  intact" may beat waiting for a range break.
- **Which signal is known pre-close?** rvol / move / tightness / ATR% are partly intraday — define them as-of the
  entry *moment* (e.g. rvol projected from morning volume), not the full-day close. **Main correctness risk.**
- **Data:** needs intraday bars (`intraday_10s` exists). The engine is daily-bar only — an intraday entry path is
  a real engine change, not a post-hoc SQL study.
- **Special case:** the very-high-rvol blow-offs the close-entry system *excludes* might behave differently entered
  early in the day — deferred, worth a separate look once the harness exists.

The bar is high: the close-entry system is PF ~2.0 raw (filtered, post-2015 clip ~1.5) with multiple independent
edges stacked. Intraday only earns its complexity if it adds materially on top.


---

<a id="v4"></a>

# HighFlyer — Mid-Cap Momentum v4 (Float-Feature Era)

**System name: HighFlyer** (named 2026-06-21) — long-only daily swing momentum on US common stocks / ADRs:
the tight-consolidation breakout to new 52-week highs on a volume + move spike. Engine project
`TradingEdge.HighFlyer` (renamed 2026-06-28 from its legacy working name `TradingEdge.MomentumV2`).

v4 is **not a new engine or a new system** — it is the same `TradingEdge.HighFlyer` production system,
carried forward into a new research thread centered on a structural feature that v3 lacked: **public
float**. The v4 line opens because float turned out to be a clean, first-order edge axis (small
dollar-float < $300M at entry → PF ~2.5), worth its own document and its own filter/sizing decisions.

The full prior research record lives in [`momentum_v3_results.md`](momentum_v3_results.md) (clipped
methodology, current production spec, past-runner studies, regime filters) and
[`momentum_v2_results.md`](momentum_v2_results.md) (exit mechanics, 52w-proximity, the original
derivations). v4 carries only the **float work** and anything it forces us to re-derive.

---

## 📐 Methodology — unchanged from v3 (the clipped, cumulative standard)

Every PF / return figure is computed on each trade's **return clipped at +50% (`LEAST(ret, 0.50)`)**, loss
side untouched — PF then reflects the *typical* trade, not a lottery tail. Decisions are made on
**cumulative** views (`X < thr` / `X ≥ thr`); non-cumulative bands are diagnostic only. Era split
pre/post-2015. Production population = v3 engine defaults + breadth (lag-1 `pct_above_20` > 0.5) + heat
(`h10` < 0.25) + ≥2005, unless a section explicitly relaxes a filter (and says so).

**Current production spec (carried from v3, the engine `defaultConfig`):** entry move ∈ [10,30)%,
rvol ≥ 5 (no cap), log-ATR < 0.10, tightness < 4.5, 52w-close ≥ 0.95, price ≥ $1, ADV ≥ $100k,
close/open−1 ≥ −0.07, **max log ATR ≥ 0.04**; exit = 5-day time-stop, no price stop. Engine PF 1.922 /
5,617 trips (pre breadth+heat); filtered ~2,625 trips.

---

## The float feature

### Source & pipeline

Public float from SEC `dei:EntityPublicFloat` — the 10-K cover-page **USD market value of non-affiliate
shares** (true free float, annual, back to ~2009). Downloaded via SEC's bulk XBRL **frames API**
(`https://data.sec.gov/api/xbrl/frames/dei/EntityPublicFloat/USD/CY{yyyy}Q{n}I.json` — every company's
value for one period in a single CIK-keyed call; a 2009→2026 sweep is 70 frames in ~46s) into
`data/equity/float/float.db` (`float_sec`). Scripts: `scripts/equity/download_float.py`.

The hard part was the **ticker→CIK bridge for delisted names** (where momentum runners end up). SEC and
edgartools public ticker maps contain only currently-listed tickers (~46% of our cumulative 2005-2026
CS+ADRC universe). The rest were recovered via **Polygon ticker-details queried with a `?date=` inside
each ticker's listing window** (delisted tickers 404 on the bare endpoint, resolve with an active-day
date). Result: **12,002 / 12,380 (96.9%)** CIKs resolved into the `ticker_cik` table
(`scripts/equity/resolve_cik.py`). Float coverage: 100,164 datapoints 2009-2026, **9,124 / 12,380
(73.7%)** of the universe with ≥1 float observation.

### Conversion (the crux) — dollar float re-anchored to the entry price

`EntityPublicFloat` is a **dollar** value anchored to the close on the issuer's 2nd-fiscal-quarter-end
(`period_end`). A raw dollar float conflates company *size* with float *tightness* and is anchored to a
stale price. So we convert to a share count and re-anchor to the **entry-day price**:

```
float_usd_at_entry = float_usd × adj_close[entry] / adj_close[period_end]
```

A 1M-share float is $1M behind a $1 stock but $100M behind a $100 stock — so we bucket on
`float_usd_at_entry`, not the reported dollar value. This is **split-SAFE in adjusted space**: the split
adjustment factor cancels in the ratio (verified on SMCI across its 2024 10:1 split — raw-consistent and
adj-consistent prices both yield the same $54.2B entry float). No-lookahead via an ASOF join on
`known_date = period_end + 90d` (the 10-K filing deadline) ≤ `entry_date`.

> **Caveat for a real share count:** if you ever want the literal non-affiliate share *count* (for
> display), divide the dollar float by the **RAW** (`daily_prices.close`) period_end price, not
> `adj_close` — adjusted prices inflate the count by the cumulative split factor (SMCI 2011 reads 296M
> adj vs the true ~30M raw). The dollar-at-entry bucketing avoids this because the factor cancels.

Script: `scripts/equity/float_breakdown.sql`.

---

## ⭐ FINDING — small dollar-float (< $300M at entry) is a clean +0.5 PF edge (2026-06-22)

Population: v3 production trips (PF 1.922) + breadth + heat + ≥2005, **+50% clip**, **rvol ≥ 5** (the
production floor), **max-log-ATR floor ≥ 0.04 ON** (it is baked into the engine defaults, so these trips
already passed it). Coverage 47.5% overall is a **pre-2010 XBRL artifact** (pre-2010 = 5/659 covered;
**2013+ = 75% covered**).

Per-bucket PF by `float_usd_at_entry` (full sample, diagnostic):

| float$ at entry | n | win% | PF (clip) | post-2015 |
| --------------- | --- | ---- | --------- | --------- |
| < $50M          | 80  | 60.0 | 2.50      | 2.13      |
| $50–150M        | 224 | 59.4 | 2.29      | 1.98      |
| $150–300M       | 191 | 59.7 | 2.72      | 2.99      |
| $300–750M       | 247 | 54.3 | 1.35      | 1.49      |
| $750M–2B        | 263 | 52.1 | 1.27      | 1.09      |
| > $2B           | 241 | 54.8 | 1.49      | 1.58      |

Cumulative CEILING (the decision lens) — keep float below N:

| keep float < | trips | % covered | PF (clip) | post-2015 | 2013+ era |
| ------------ | ----- | --------- | --------- | --------- | --------- |
| < $150M      | 304   | 24.4      | 2.36      | 2.03      | 2.05      |
| < $300M      | 495   | 39.7      | **2.47**  | 2.26      | **2.32**  |
| < $750M      | 742   | 59.6      | 2.13      | 2.07      | 2.04      |
| (all covered)| 1246  | 100       | 1.87      | —         | 1.81      |
| float ≥ $750M| 504   | 40.4      | 1.37      | 1.30      | —         |

**Read:** monotone and sharp — `float < $300M at entry → PF ~2.4–2.5` vs ~1.8 covered baseline; **big
float (≥ $750M) is the drag at PF ~1.37**. NOT the lottery-tail problem: it survives the +50% clip, the
**win rate RISES** in the low buckets (57–60% vs 52% for big float), and it holds in the modern 2013+
window (PF 2.32 at < $300M). The `no-data` bucket is benign — 2013+ no-data is PF 1.88 / win 57%
(average), so a float filter wouldn't silently dump a profitable cohort.

**Status: NOT yet wired into the engine.** Strong candidate for a production filter (`float_usd_at_entry
< ~$300M`) or a sizing tilt. Open questions: (a) how a LIVE filter treats pre-2013 / no-data names;
(b) whether float subsumes the max-log-ATR floor (both proxy "small frothy name"); (c) float × rvol and
float × move interactions.

---

## ⭐ FINDING — the float edge OVERLAPS breadth/heat/rvol; it is NOT independent (2026-06-22)

To test whether float's lift is *independent* of the regime gates or partly captured by them, we
**removed breadth + heat and lowered rvol 5 → 3** (more samples; looser entries). Trips regenerated at
`--rvol-min 3` (max-log-ATR floor ≥ 0.04 still ON): **10,254 trips, engine PF 1.625**. Only ≥2005 kept;
no breadth/heat. Coverage 58.9% (no pre-2010 cliff because the population is much larger and later-skewed).

Per-bucket PF by `float_usd_at_entry`:

| float$ at entry | n | win% | PF (clip) | post-2015 |
| --------------- | ---- | ---- | --------- | --------- |
| NO DATA         | 4217 | 51.9 | 1.47      | 1.58      |
| < $50M          | 435  | 54.3 | 1.61      | 1.48      |
| $50–150M        | 954  | 54.7 | 1.59      | 1.42      |
| $150–300M       | 870  | 52.2 | 1.62      | 1.61      |
| $300–750M       | 1234 | 48.5 | 1.20      | 1.15      |
| $750M–2B        | 1118 | 49.0 | 1.17      | 1.15      |
| > $2B           | 1426 | 52.2 | 1.21      | 1.23      |

Cumulative keep float < N:

| keep float < | trips | PF (clip) | post-2015 | 2013+ |
| ------------ | ----- | --------- | --------- | ----- |
| < $150M      | 1389  | 1.59      | 1.44      | 1.55  |
| < $300M      | 2259  | **1.60**  | 1.49      | 1.59  |
| < $750M      | 3493  | 1.48      | 1.39      | 1.48  |
| (all covered)| 6037  | 1.38      | —         | —     |

**Read — float is complementary/redundant, not orthogonal.** Side-by-side with the production run:

| | rvol≥5 + breadth + heat (prod) | rvol≥3, NO breadth/heat |
| --- | --- | --- |
| float < $300M | PF **2.47** | PF **1.60** |
| covered baseline | 1.81 | 1.38 |
| **float-edge uplift** (< $300M − baseline) | **+0.66** | **+0.22** |
| float ≥ $750M (drag) | 1.37 | ~1.19 |

Two separate things happened. (1) Absolute PF fell everywhere — *expected, not about float*: rvol 5→3
plus dropping breadth/heat admits weaker entries (baseline 1.81 → 1.38). (2) The float **uplift shrank
from +0.66 to +0.22** — roughly a **third** of its production magnitude. The low-float ordering *survives*
(< $300M at 1.60 still beats ≥ $750M at ~1.19), so float is a genuine signal — but most of its standalone
power in the production run was breadth + heat + rvol acting on the **same small-frothy-breakout names**.
Strip those gates and float points the right way with far less force; the < $300M bucket (1.60) barely
clears the *full-production covered baseline* (1.81).

**Implication:** float is **not a free +0.6 PF bolt-on** for a stripped-down system — it earns its keep
largely *in conjunction with* the regime filters (they select overlapping populations). As a production
filter on top of the existing gates it is still worthwhile (the ordering is real and the production-run
uplift is large), but its incremental value over breadth+heat+rvol is modest, not additive. This also
flags that the next overlap test (float × max-log-ATR) is likely to show similar redundancy — they all
proxy "small, frothy, in-a-healthy-tape."

---

## ⭐ FINDING — low-float names do NOT earn a higher move cap; 30% is correct for them too (2026-06-22)

Hypothesis (user): a 30% day on a $100M-float name is a normal day, not a blow-off, so maybe low-float
stocks reward (or at least tolerate) the 30%+ moves that the general population can't — i.e. the move cap
should be float-dependent. Tested by lifting the engine's upper move cap (`--max-up-threshold 100`,
band → [10%, ∞), 7,137 trips, engine PF 1.589) and crossing move band × `float_usd_at_entry`, full
production population (breadth + heat + rvol≥5), +50% clip.

The general population confirms the cap: PF falls off a cliff above 30% — 10–30% 1.75 → 30–50% 1.15 →
50–80% 0.92 → 80%+ 0.45. The blow-off reverts.

Move band × float (covered only):

| move band | LOW < $300M | HIGH ≥ $300M |
| --------- | ----------- | ------------ |
| 10–30%    | PF **2.47** (win 60%) | 1.36 |
| 30–50%    | PF 1.54 (win 54%)     | 0.66 (net loser) |
| 50–80%    | 0.81        | 2.35¹        |
| 80%+      | 0.73        | 0.73         |

¹ n=32, noise — ignore. The 30–50% low-float cell (1.54) looked like a shelf, so we swept the cap
**cumulatively** for low-float only — and it is **NOT** real:

| keep low-float move < | n | PF clip |
| --------------------- | --- | ------- |
| 30% (current cap)     | 495 | **2.47** |
| 40%                   | 536 | 2.25    |
| 50%                   | 558 | 2.30    |
| 60%                   | 573 | 2.15    |
| no cap                | 615 | 1.93    |

**Extending the cap monotonically DEGRADES PF even for low-float.** 30% is the best; every extension
lowers it. The fine marginal bands show the 30–50% "shelf" was two opposing tiny cells: 30–40% is a
*loser* (PF 0.85, n=41), 40–50% a flukey winner (PF 3.07, n=**22**, win 73% — one or two trades, not a
shelf); 50–70% 0.95, 70%+ 0.69. All dead, all thin.

**Conclusion: NO — low-float stocks do not earn a higher move ceiling. The 30% cap is correct for them
too.** Their advantage is entirely *within* the [10,30%] band (PF 2.47 vs 1.36 high-float), not in
tolerating bigger moves; a 35%+ single-day move is an exhaustion blow-off **regardless of float**. This
is the *useful* outcome: float and the move-cap are **independent levers that do not interact** — keep
the 30% cap, stack the low-float tilt inside it. (Contrast with the breadth/heat overlap finding above,
where float DID overlap the regime gates.) Script `scripts/equity/move_x_float.sql`.

---

## ❌ NEGATIVE — smoothing the breadth gate (MA5/MA10) does NOT beat raw lag-1 (2026-06-22)

The raw daily breadth series (`pct_above_20`) whips across the 0.5 gate ~40% of adjacent days in chop
zones (visible eyeballing 5 months of it), so the hypothesis was that a moving-average gate would be a
more stable, better-backtesting regime filter. **Tested and rejected.** All MAs end at lag-1 (window
`[t-w, t-1]`) — no lookahead. Population: refreshed production trips (PF 1.828 post data-refresh) +
heat + ≥2005; +50% clip. Gate is the only variable.

| gate | trips | PF clip | post-2015 |
| ---- | ----- | ------- | --------- |
| **raw lag-1 > 0.50 (CURRENT)** | 2689 | **1.763** | 1.74 |
| no gate (heat only)            | 4165 | 1.652 | 1.692 |
| ma5 > 0.50                     | 2699 | 1.724 | 1.662 |
| ma10 > 0.50                    | 2812 | 1.729 | 1.669 |
| raw lag-1 > 0.55               | 2285 | 1.708 | 1.691 |
| ma10 > 0.55                    | 2343 | 1.813 | 1.858 |
| raw lag-1 > 0.60               | 1871 | 1.730 | 1.719 |
| ma10 > 0.60                    | 1796 | 1.739 | 1.889 |

**Read:** at the matched 0.50 threshold raw (1.763) *beats* both MAs (1.724/1.729) — smoothing slightly
LOWERS PF, opposite of the hypothesis. The only cell that beats baseline is `ma10 > 0.55` (1.813), and
head-to-head it does beat `raw > 0.55` (1.708) at the same threshold — but it's a single non-monotone
cell out of ~10 tried (raw wins at 0.50, tie at 0.60, ma10 only at 0.55), the classic shape of a
multiple-comparison artifact. **Decision: keep the production gate as raw lag-1 > 0.50; do NOT smooth.**

What the test DID confirm (both useful): (1) the breadth gate earns its keep — no-gate 1.652 → gated
~1.76, a real ~+0.1 PF filter. (2) the edge is **robust to the gate's exact form** (raw vs smoothed,
0.50–0.60 all land ~1.71–1.81) — reassuring for live trading: no need to agonize over whether breadth
prints 0.49 or 0.52 on a given day. Script `scripts/equity/breadth_gate_smoothing.sql`.

---

## Open experiments (v4 queue)

- Float × max-log-ATR overlap (regenerate trips at `--min-max-atr-log 0`) — user doubts they overlap.
- Float × rvol interaction grid.
- Engine wiring of the low-float tilt / filter (decide modest incremental value vs data dependency).
- Float × rvol and float × move interaction grids.
- Engine wiring of the float filter / sizing tilt.
