# PEAD on upside earnings breakouts: stock vs long call vs short put (user 2026-11-11)

Script `scripts/options/pead.py` (`--build` then report), log `logs/options_pead.log`, data
`data/options/pead/{events,options,options_pnl}.parquet`.

## Setup
- Events: EDGAR 8-K 2.02 earnings (`data/options/earnings_8k202.parquet`), 439 tickers, 2015-06 -> 2026-10, 15,114
  reaction days R (BMO/RTH: that session; AMC: the next).
- Features at R's close: `xs_r` = R's total return minus SPY's; `vol_ratio` = R's volume / mean of the 30 sessions
  before R (split-consistent shares); `hi52` = new 52-week closing high; `gap_r`.
- Stock: buy at R's close, exit at the close h = 5/10/15/20 sessions later; total return from `daily_adjusted`
  (V = close*n + cum_div), also minus SPY's (`xs_h`).
- Options (2022-12 ->, ThetaData EOD NBBO): ATM strike at R's close; `tight` = first expiry >= exit date (settles at
  intrinsic if it expires that day), `buf` = first expiry >= exit + 14 days. Long call / short put, out at the exit
  close. Fill f = fraction of the half-spread paid per trade (0 = mid, 1 = natural). P&L per share in % of S_R (one
  contract = the same 100-share notional as the stock).

## Stock: SPY-excess return after the breakout
| definition | n (11.4 y) | xs_5 | xs_10 | xs_20 | med xs_20 | win 20 | t 20 |
|---|---|---|---|---|---|---|---|
| all events (baseline) | 14,940 | +0.03 | +0.07 | +0.46 | -0.70 | 46% | 3.4 |
| up5_v2.5: xs_r >= 5%, vol >= 2.5x | 1,931 | +0.17 | +0.75 | +1.46 | +0.22 | 51% | 3.6 |
| up10_v4 | 727 | +0.24 | +0.45 | +1.33 | +0.10 | 50% | 1.8 |
| **up5_v2.5 + new 52w closing high** | 639 | +0.79 | +1.74 | **+2.34** | **+1.02** | 55% | 3.6 |
| gap >= 5%, close > open, vol >= 2.5x | 917 | +0.08 | +0.83 | +1.92 | +0.80 | 53% | 3.3 |
| mirror: xs_r <= -5%, vol >= 2.5x | 2,089 | -0.27 | -0.42 | +0.27 | -1.27 | 45% | 0.6 |

- The drift builds over 10-20 sessions (little in the first 5). The mean beats the median: a few big winners.
- ⚠ **Survivorship**: the tickers are TODAY's universe, so even the all-events baseline drifts +0.46% / 20d (t 3.4).
  Net of the baseline, up5_v2.5 is ~+1.0% and the 52w-high version ~+1.9%.
- Unstable by year (up5_v2.5, xs_20): 2016-18 +1.8..+3.7, 2019-20 ~0, **2021 -3.4**, 2022 0, 2023 +2.0, 2024 +4.7,
  2025 +0.3, 2026 +3.0.
- Grid (R-day excess move x volume ratio, xs_20): moves > 20% on >= 1.5x volume +8.6..+9.9% (n 74), but > 20% on > 4x
  volume only +3.3% (n 305); moves 5-20% +1..+2%.
- Frequency: up5_v2.5 ~14/month (11.5/month in the options window), with the 52w-high filter ~4.7/month.

## Options vs stock on the same events (up5_v2.5, 2022-12 ->, n ~530, % of S_R per share)
| h | expiry | stock | call mid | call f=.5 | call nat | put mid | put f=.5 | put nat | sr stock | sr call .5 | sr put .5 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 10 | tight (15 DTE) | +1.01 | +0.46 | +0.02 | -0.42 | +0.49 | 0.00 | -0.49 | 0.07 | 0.00 | 0.00 |
| 20 | tight (30 DTE) | +2.55 | +1.21 | +0.62 | +0.02 | +1.27 | +0.52 | -0.24 | 0.14 | 0.04 | 0.06 |
| 20 | buf (44 DTE) | +2.59 | +1.17 | +0.26 | -0.65 | +1.26 | +0.37 | -0.53 | 0.14 | 0.02 | 0.04 |

(sr = mean / sd per trade.) Call at f=.5 as % of premium: +12% at 20 sessions tight, win rate 37%; the short put wins 70%.

**Read**:
- An ATM call and a short ATM put each carry ~0.5 delta, so at MID each captures about half the stock's move
  (+1.2-1.3 vs +2.55). Neither adds anything: post-earnings IV leaves no extra premium to harvest on the upside
  breakouts (short put at mid ~= half the stock move, same as the call).
- The spread then takes about half of what is left (two crossings, ~0.6% of S at f=.5). Risk-adjusted the stock is
  2-3x better (sr 0.14 vs 0.04-0.06).
- Options only buy leverage (call +12% of premium per trade) or a capped loss (the call); they are not a better
  expression of PEAD here. If used: the tight expiry beats the buffered one (less spread and vega per share).

## Expanded universe, scheduled releases only (2026-11-11)

EDGAR Item 2.02 for 13.7k tickers with md60 >= $500k (point-in-time CIKs, reused tickers split by date), releases only
via `earnings_scheduled.py --bulk` (straddleharvester SH-21): 171,658 events 2015-06 -> 2026-10, 6.5k tickers
(`data/options/pead_md500k_sched`, logs/options_pead_md500k_sched.log; the all-2.02 version: logs/options_pead_md500k.log).
`pead.py --build` is chunked by ticker now (the whole universe at once was OOM-killed on 15 GB; chunked it takes 21 s).

xs_20 (SPY-excess, 20 sessions, entry at the R close): up5_v2.5 **+0.32%** (n 15,054, t 2.6; the 439 liquid option
names gave +1.46), up10_v4 +0.55, up5_v2.5 + 52w high +0.57 (t 3.1), gap_hold +0.74 (t 3.6), down5_v2.5 -0.32,
all -0.10. By year (all-2.02 run) up5_v2.5 swings from +2.8 (2016) to -1.6 (2021). By liquidity (md60): 0.5-5M +0.89
(t 3.1), 5-50M -0.09, 50-200M +0.25, > 200M +0.59 - not monotone. The drift exists but is small and unstable in the
broad market.
