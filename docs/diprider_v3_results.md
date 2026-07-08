# DipRiderV3 — pure trailing-window momentum

**Project:** `TradingEdge.DipRiderV3` (forked from `TradingEdge.DipRider` on branch `dip-rider`, 2026-07-08).
Long-only intraday momentum. The V2 run-tracking machinery (above-9EMA run reset, `savedRun*` stats,
buy-into-run) is **deleted**: choosing the run length N was the same overfitting trap as choosing an EMA
length. V3 replaces "consecutive run length" with reset-free **trailing-window features** over a fixed
20-bar window.

## Design

**Features** (all strictly-prior, no-lookahead snapshots at entry):

| Feature | Object | Baseline gate |
|---|---|---|
| 20m OLS log-price slope | `OlsSlopeMa(20)` on log-close | `> 0` |
| 20m OLS log-volume slope | `OlsSlopeMa(20)` on log-volume | `>= 0.05` (V2 F14/F15, the PF lever) |
| 20m log-ATR | `AvgMa(20)` of log-TR (`atrLog`) | recorded |
| 20m tightness | `(rangeHigh−rangeLow) / atrLin` (**linear** ATR denom) | `>= 3.0` |
| slope / log-ATR ratio | price-slope ÷ log-ATR | recorded → gate after breakdown |
| closes-above-9EMA, 6m | `SumMa(6)` of `(close ≥ EMA ? 1 : 0)` | `>= 5` but **DISABLED** at baseline |
| closes-above-9EMA, 40/60m | `SumMa(40)`, `SumMa(60)` | cap (trend-too-long) — off, tune later |
| session-max 20m log-ATR | running max of `atrLog.State` | recorded → gate after breakdown |
| session min/max close | running extremes (from 08:30) | recorded |
| session max volume | running max (from 08:30) | recorded |
| session VWAP | Σtp·v/Σv (from 09:30) | recorded |
| 15m initial volume | Σ of the first 15 **valid** feature-bars | recorded |

**Two-anchor timing** (fixes V2's accidental 09:00 VWAP anchor — `SessionStartMin` was `9*60=540`=09:00,
comment wrongly said 09:30):
- `SessionStartMin = 510` (08:30 ET) — the emitter delivers bars from here; **session min/max close +
  session max volume** fold from the first valid bar.
- `FeatureStartMin = 570` (09:30 ET) — **every other feature** (VWAP, both OLS slopes, ATR, tightness,
  EMA, the SumMa above-EMA counts, 15m-init-vol) folds only once `bar.etMin >= FeatureStartMin`.

**Universal valid-bar gate:** a bar enters a rolling window ONLY if `close > 0 && volume > 0`.
Illiquid/halt/print-gap bars are skipped for **every** feature. The 15m-initial-volume counts the first 15
**valid** feature-bars (not the clock 09:30–09:45 window) — a direct consequence.

**Stop:** geometry stop, `d = entry − 20m-min-close`; `stop = entry − d·(2/3)`, close-based. (Same geometry
as VwapReclaim F14 / V2 F25; the floor is the 20m **min close** — the trailing analogue of V2's run floor.)
`--stop-floor-sess-min` swaps to the session-min-close floor. Hold-to-MOC (the continuation lesson: any
exit caps the winners — V1 F3, VwapReclaim F13, V2 F4/F23/F26).

**Defaults:** morning window 10:00–13:30, `MaxConcurrent = 1` (V2 F29 — later same-day adds have worse EV),
9-EMA, 20-bar window. Candidate universe = `vwap_reclaim_candidate` (ADV ≥ $1M, rvol_0945 > 1, CS/ADRC,
price ≥ $1).

**Verification (2026-07-08):** two-anchor split confirmed — on a synthetic day, `sess_max_vol` /
`sess_min_close` pick up the 08:30 premarket bars while VWAP reflects only 09:30+ RTH bars; a 0-volume halt
bar at 09:30 is excluded from every window (init_vol_15m = 15×1000, not counting the halt bar).

---

## Finding 1 — clean-sheet baseline: PF 1.244 / 67,293 trips / +$1.45M, positive 18/24 years

Full 22-year run (2003-09-10 → 2026-06-25), all optional/cap gates OFF, only the three core gates on
(vol-slope20 ≥ 0.05, price-slope20 > 0, tightness20 ≥ 3), geometry stop, hold-to-MOC, max-concurrent 1.

| metric | value |
|---|---|
| candidates | 52,630 ticker-days |
| trips | 67,293 |
| win rate | 24.5% |
| PF (MOC) | **1.244** |
| net P&L | **+$1,445,473** (notional $10k/trip) |

**Per-year** (positive **18 of 24 years**; all 6 negatives are pre-2018 and shallow):

| yr | n | win% | PF | net | avg_ret% |
|---|---|---|---|---|---|
| 2003 | 481 | 26.0 | 1.215 | 4,934 | 0.10 |
| 2004 | 1,290 | 24.4 | 1.174 | 9,578 | 0.07 |
| 2005 | 1,696 | 23.7 | 1.076 | 4,652 | 0.03 |
| 2006 | 2,395 | 23.9 | 1.016 | 1,419 | 0.01 |
| 2007 | 2,536 | 23.7 | 1.066 | 7,112 | 0.03 |
| 2008 | 2,604 | 21.9 | 0.924 | −14,140 | −0.05 |
| 2009 | 3,145 | 25.4 | 1.198 | 39,923 | 0.13 |
| 2010 | 3,315 | 24.8 | 0.871 | −18,033 | −0.05 |
| 2011 | 2,535 | 24.1 | 0.999 | −149 | −0.00 |
| 2012 | 2,540 | 24.7 | 1.076 | 8,479 | 0.03 |
| 2013 | 3,078 | 25.8 | 1.122 | 15,294 | 0.05 |
| 2014 | 3,586 | 23.9 | 0.995 | −786 | −0.00 |
| 2015 | 3,647 | 24.8 | 1.175 | 33,890 | 0.09 |
| 2016 | 2,352 | 26.1 | 1.298 | 41,139 | 0.17 |
| 2017 | 2,191 | 23.3 | 0.935 | −9,780 | −0.04 |
| 2018 | 1,938 | 25.4 | 1.175 | 24,669 | 0.13 |
| 2019 | 2,047 | 26.9 | 1.058 | 8,639 | 0.04 |
| 2020 | 3,753 | 25.2 | 1.372 | 185,798 | 0.50 |
| 2021 | 10,200 | 23.2 | 1.061 | 85,115 | 0.08 |
| 2022 | 4,902 | 24.5 | 1.183 | 111,174 | 0.23 |
| 2023 | 2,522 | 26.4 | 1.592 | 173,662 | 0.69 |
| 2024 | 2,067 | 24.7 | 1.708 | 275,343 | 1.33 |
| 2025 | 1,871 | 24.9 | 1.671 | 289,391 | 1.55 |
| 2026 | 602 | 25.2 | 2.072 | 168,150 | 2.79 |

**Reading:** a healthy, unoptimized baseline. The edge is **right-tailed** (24.5% win rate, positive PF ⇒ a
few large winners carry it — the momentum-continuation signature). It **concentrates in the modern regime**:
2020-2026 runs PF 1.37 → 2.07, climbing monotonically in the last four years, while the negative years are
all pre-2018 and shallow (worst = 2010 PF 0.87). 2021 (the two-sided-chop year that dogged V2) is the
highest-trip year (10,200) at a still-positive PF 1.061 — the loosest cell, ripe for the caps to prune.

This is the launch point for the feature breakdowns. **NEXT:** bucket-breakdown the recorded features to set
thresholds → (a) slope/log-ATR ratio, (b) session-max-log-ATR floor, (c) the SumMa(6) ≥ 5 gate on/off,
(d) the long-window SumMa cap (trend-too-long), (e) entry-vs-VWAP / entry-vs-session-high, then stack the
survivors and re-check the 22-year robustness.
