# Market-making opportunity minutes — how often, and do they persist? (2026-11-16)

**The question (user, 2026-11-16).** The fertile ground for a market maker is where the spread is wide AND volume,
activity and volatility are high at the same time. Wide spreads alone are usually a dead, illiquid market. How often do
such periods occur? Once one is detected over a minute, does it persist for the next few minutes? The detected periods
are for a discretionary review in TapeSim. The user's view: the simple realized-spread test of 11-03
(`realized_spread_results.md`) is not a good estimate of what a maker earns, and the user's own discretionary quoting
is closer to what could be expected.

## CBRS, 2026-05-14, minute by minute

The first trade was at 12:59:43 ET. An LULD halt ran 13:00:01–13:05:01, so minutes 1–4 have no trades. All trades are
counted here (lit and TRF); the spread is the time-weighted quoted NBBO.

| minute | trades | \$M | quoted spread (bp) | high–low range (bp) | volatility (bp/√s) |
|---|---|---|---|---|---|
| 0 (the opening cross) | 2,713 | 3,292 | 25 | 1,000 | 25 |
| 5 (the reopen) | 15,372 | 674 | 93 | 1,920 | 129 |
| 6 | 16,304 | 381 | 89 | 1,385 | 113 |
| 7 | 14,083 | 342 | 88 | 853 | 60 |
| 8–12 | 6–10k | 125–194 | 40–59 | 300–570 | 34–44 |
| the day's median minute | 1,638 | 31 | 33 | 131 | 16 |

The three minutes after the reopen ran at 3× the day's spread, 10× its trades and 7–8× its volatility.

## The spread from trades alone

On the 40 IPO first days (their NBBO is cached), per minute, lit trades only, against the median quoted spread at the
trades (7,951 ticker-minutes):

| estimator | rank corr (pooled) | rank corr within a name | ratio to quoted: median (IQR) |
|---|---|---|---|
| **Roll 2·√(−cov(ΔPₜ, ΔPₜ₋₁)), trade by trade** | **0.85** | **0.67** | 0.27 (0.21–0.33) |
| Roll on the series with same-price runs collapsed | 0.75 | 0.63 | 0.48 (0.37–0.66) |
| reversal bounce (median abs ΔP where the price reverses) | 0.74 | 0.58 | 0.27 (0.20–0.36) |
| reversal bounce, runs collapsed | 0.75 | 0.62 | 0.26 (0.20–0.33) |

- **Raw Roll is the estimator.** Divided by 0.27, it is the quoted-scale spread estimate. The 0.27 comes from sweeps:
  a sweep prints many trades at one price, and the zero changes dilute the covariance.
- **Collapsing the runs compresses the scale, both ends:**
  - Tight spreads: every remaining change is at least one tick, so the estimate is floored near the tick (4.5 bp
    estimated at 4.3 bp quoted).
  - Wide spreads: a sweep walking the book is still same-direction, so the bounce stays hidden (16 bp estimated at
    47 bp quoted).
- **The volatility is bounce-free:** σ per second = √((var ΔP + 2·cov) · n / 60), from Roll's decomposition
  var ΔP = σ² + s²/2.
- ⚠ The 0.27 factor is calibrated on IPO days only.

## The scan: 128 days, 2026-05-14 → 2026-11-13

**The scan** (`scripts/equity/mm_opportunity_scan.py`):
- every ticker-minute of RTH with ≥ 100 lit trades;
- 17.3M ticker-minutes in all, one file per day in `data/equity/mm_opportunity/minutes/`;
- about 50 s a day at a 5 GB DuckDB cap, 113 min in total.

The report is `scripts/equity/mm_opportunity_report.py` → `data/equity/mm_opportunity/report.md`, `episodes.csv`.
The window analysis is in `logs/mm_opportunity_window.log`.

**The tiers.** A minute qualifies at a tier when it clears all four thresholds:

| tier | lit trades | \$ volume | spread estimate (bp) | volatility (bp/√s) |
|---|---|---|---|---|
| A, CBRS-like | ≥ 1,000 | ≥ \$5M | ≥ 30 | ≥ 10 |
| B, broad | ≥ 300 | ≥ \$1M | ≥ 30 | ≥ 10 |
| C, wide | ≥ 300 | ≥ \$1M | ≥ 50 | ≥ 15 |
| D, looser | ≥ 300 | ≥ \$1M | ≥ 20 | ≥ 5 |

### Frequency

| tier | minutes / day | in the first 5 min | tickers / day | days with any |
|---|---|---|---|---|
| A, CBRS-like | 22.9 | 48 % | 16.8 | 128/128 |
| B, broad | 259.6 | 31 % | 116.9 | 128/128 |
| C, wide | 97.8 | 26 % | 42.3 | 128/128 |
| D, looser | 550.9 | 30 % | 254.6 | 128/128 |

- **Hot stocks on hot days lead the counts.** OMH, VEEE, WFF, SDOT and ASTC each qualify on only 6–16 days, but for
  30–60 minutes on each of those days.
- **Tier A is mostly the open:** about half of its minutes fall in 09:30–09:35. Outside the open, CBRS-like
  conditions are rare: SPCX 06-12, CBRS 05-14, WDAY 08-13 and a few others.

### Persistence

**A single qualifying minute does not persist.** After a ticker's first tier-B minute of the day (from 09:35):
- The next minute qualifies 8 % of the time.
- The next 30 minutes hold a median of **0** qualifying minutes, and a mean of 0.7.
- 82–92 % of the episodes (runs of qualifying minutes) are one minute long.

**The minutes cluster**: the strict per-minute thresholds break one hot day into many short episodes. The trigger that
works is **K consecutive qualifying minutes**, the first time in a ticker-day (from 09:35). "Half-tier" = the minute
clears half of each of the tier's four thresholds.

| tier, K | triggers / day | days with one | next minute qualifies | of the next 5 (mean) | of the next 30 (mean / share ≥ 10) | half-tier, of the next 30 (mean / share ≥ 15) |
|---|---|---|---|---|---|---|
| B, 1 | 63.9 | 128 | 8 % | 0.24 | 0.7 / 2 % | 3.0 / 6 % |
| B, 2 | 8.1 | 128 | 38 % | 1.27 | 4.2 / 15 % | 11.1 / 34 % |
| **B, 3** | **4.1** | **124** | **49 %** | **1.85** | **6.5 / 27 %** | **14.8 / 49 %** |
| C, 2 | 3.9 | 127 | 35 % | 1.01 | 3.3 / 7 % | 11.3 / 33 % |
| C, 3 | 1.9 | 104 | 36 % | 1.32 | 4.7 / 14 % | 14.5 / 48 % |
| D, 2 | 18.1 | 128 | 33 % | 1.13 | 3.5 / 11 % | 10.5 / 33 % |
| D, 3 | 7.7 | 127 | 48 % | 1.84 | 6.0 / 24 % | 15.0 / 51 % |

## Findings

- **Fertile minutes are common in the market as a whole.** Tier B gives about 260 minutes a day across about 117
  tickers. CBRS-grade minutes (tier A) come at about 23 a day, but half of them are the open.
- **A one-minute detection is mostly noise or a one-off burst.** The next minute qualifies only 8 % of the time.
- **Three consecutive qualifying minutes are a workable trigger.**
  - Tier B, K = 3, fires about 4 times a day and on 124 of 128 days.
  - In the next 30 minutes the ticker stays at least half-tier active for 15 minutes on average.
  - Half of the triggers stay active for half the window or more.
- **The review list** is `data/equity/mm_opportunity/triggers_B3.csv`: 527 triggers (the first per ticker-day). For
  each trigger it gives the 3-minute window's start, the trigger time, the 3-minute trades, \$ volume, spread and
  volatility, and how much of the next 30 minutes qualified. 259 of them stayed half-tier active for ≥ 15 of 30
  minutes.
- ⚠ **This is an opportunity count, not a P&L.** The 11-03 realized-spread test found adverse selection paid for the
  wide spreads on hot IPOs. The user's view is that discretionary quoting does better than that test. The TapeSim
  review of the triggers is the next step.
