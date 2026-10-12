# GapFader — fading open gaps (mean reversion)

**Started 2026-10-12** (user: "do large gaps on weak volume revert to the mean during the day, and how often?").
Mean reversion only — breakouts (ORB, overnight-range breaks) are out of scope by the user's ruling: "we always have
a lot of success researching mean reversion systems, so we should stick to what we're good at." Branch `gapfader`
of the research repo. Name provisional (the Fader family).

## Definitions (all knowable at the entry time; docs/lookahead_protocol.md)

| | definition | knowable |
|---|---|---|
| gap | open(D) / pc_total − 1; open = the official opening print; pc_total = close(D−1) × n(D−1)/n(D) − (cum_div(D) − cum_div(D−1)) / n(D) — the previous close in D's raw share terms (`daily_adjusted`, the causal scheme; the Oms LowFader's prevCloseTotal) | 09:30 |
| rvol_pm | premarket volume 04:00–09:29:59 (1s bars) / avgvol20_prior (the 20 PRIOR sessions, in D's share units) | 09:30 |
| rvol_5m / 15m / 30m | the first 5 / 15 / 30 RTH minutes' volume / avgvol20_prior (the 09:30:00 bar holds the open cross) | 09:35 / 09:45 / 10:00 |
| dv20_prior | the 20 PRIOR sessions' mean close × volume | before the open |
| outcomes | `filled` (an RTH 1s vwap reaches pc_total), `fill_sec`, `fade_oc` (open → official close, signed for the fade), `fade_on` (open → next open), `retraced` (share of the gap closed by the close), `adverse` (the worst excursion against the fade before the fill / close) | after the fact — never filters |

Universe: `ticker_reference` CS / ADRC (delisted names included) minus the Oms's mislabel exclusions (Universe.fs
mirrored: warrants, rights, SPAC units, preferred issues, the CEF list); the previous session ≤ 4 calendar days back;
20 prior sessions present.

## GF-1 — the gap census, open → close (2026-10-12)

`scripts/equity/gapfader_census.py` → `data/equity/gapfader/census.parquet` (1,277,988 gaps |gap| ≥ 2 %, raw open ≥ $0.50,
dv20_prior ≥ $250k, 2016-08-08 .. 2026-09-04; the official open and the 1s bars' first RTH vwap agree, median Δ 0).
Report `scripts/equity/gapfader_tables.py` → `data/equity/gapfader/tables.md`. Below: raw open ≥ $1, dv20_prior ≥
$1M, no early closes — **946,324 gaps; the FADE at the open to the official close, equal weight, NO costs.**

**Gap DOWN (fade = buy the open):**

| gap | n | filled by close | median fill (min) | fade mean % | fade median % | win % | PF | adverse p90 % |
|---|---|---|---|---|---|---|---|---|
| 2–4 % | 316,222 | 47 % | 22 | +0.19 | +0.14 | 51 | 1.13 | 6.5 |
| 4–6 % | 70,099 | 36 % | 36 | +0.25 | +0.22 | 51 | 1.11 | 9.4 |
| 6–10 % | 39,138 | 27 % | 44 | −0.10 | −0.07 | 49 | 0.97 | 13.2 |
| 10–15 % | 12,531 | 16 % | 62 | −0.32 | −0.47 | 47 | 0.92 | 16.4 |
| 15–20 % | 4,583 | 10 % | 92 | +0.10 | +0.00 | 49 | 1.02 | 18.6 |
| 20–30 % | 3,212 | 5 % | 116 | −0.97 | −0.49 | 48 | 0.83 | 23.1 |
| 30–50 % | 1,377 | 2 % | 121 | −1.25 | −1.39 | 45 | 0.83 | 29.3 |
| ≥ 50 % | 430 | 0 % | — | −0.98 | −3.51 | 40 | 0.90 | 35.1 |

**Gap UP (fade = short the open):**

| gap | n | filled by close | median fill (min) | fade mean % | fade median % | win % | PF | adverse p90 % |
|---|---|---|---|---|---|---|---|---|
| 2–4 % | 345,249 | 48 % | 22 | +0.19 | +0.32 | 54 | 1.13 | 6.4 |
| 4–6 % | 82,351 | 38 % | 33 | +0.23 | +0.53 | 54 | 1.11 | 9.4 |
| 6–10 % | 45,242 | 31 % | 44 | +0.29 | +0.79 | 55 | 1.11 | 12.5 |
| 10–15 % | 13,318 | 24 % | 58 | +0.62 | +1.53 | 58 | 1.18 | 18.1 |
| 15–20 % | 4,993 | 18 % | 79 | +1.24 | +1.90 | 59 | 1.31 | 22.8 |
| 20–30 % | 3,760 | 14 % | 97 | +1.16 | +2.67 | 60 | 1.22 | 29.9 |
| 30–50 % | 2,138 | 11 % | 152 | +2.00 | +4.71 | 64 | 1.30 | 42.2 |
| ≥ 50 % | 1,681 | 5 % | 181 | +4.89 | +10.34 | 68 | 1.55 | 58.1 |

Gap × rvol_pm fade PF (fill rate, n) and the year tables: `data/equity/gapfader/tables.md`. Highlights:
- **Weak premarket volume, large gaps** — rvol_pm < 0.02: gap-down 15–20 % 1.54 (1,138), 20–30 % 1.50 (441), 30–50 %
  2.60 (125); gap-up 15–20 % 1.87 (703), 20–30 % 2.19 (268), 30–50 % 5.24 (44). Thin cells, not yet year-checked.
- **Gap-ups fade with strong premarket volume too** (rvol_pm ≥ 1: 1.29–2.02 at gaps ≥ 6 %); gap-DOWN fades with
  strong premarket volume lose everywhere (0.55–0.89).
- Year table, gaps ≥ 6 %, weak (rvol_pm < 0.10): gap-down 1.54 / 1.04 / 1.07 / 0.97 / **0.60 (2020, n 14,739)** / 1.58 /
  1.47 / 1.04 / 1.54 / 1.04 / 1.24; gap-up 1.21 / 1.35 / 1.45 / 1.07 / 0.90 / 1.25 / 1.10 / 1.26 / 1.15 / 1.50 / 1.15.

Reading:
- **"How often do they revert?"** A full FILL (back to the previous close the same day) gets rarer with size: ~47 %
  at 2–4 %, a quarter at 10–15 %, 5 % at ≥ 50 % (gap-ups) and ~0 % (gap-downs). The median gap retraces only 4–22 %
  by the close. But big gap-UPS close BELOW their open most of the time: 58–68 % at gaps ≥ 10 % — the "gappers
  close down ~70 %" claim, measured: 68 % for gap-ups ≥ 50 %.
- **The two sides are not mirror images** (again): fading gap-ups works and strengthens with the gap size (PF 1.11
  → 1.55); fading gap-downs dies past 6 % (0.83–0.97) except, perhaps, on weak premarket volume.
- **"Weak volume" helps where the user expected**, for the largest gaps on both sides — on thin cells so far.
- ⚠ **The market confound:** 2020's 14,739 weak-volume gap-downs ≥ 6 % (3× any other year) are market-wide gap days,
  not idiosyncratic gaps — the next step has to separate the stock's own gap from the market's.
- ⚠ Entry AT the official open print, no costs, no borrow: the fade-short side needs locates on exactly the names
  that gap most, and the adverse excursion before the fill is large (p90 30–58 % at gaps ≥ 20 %).

## GF-2 — the market adjustment (2026-10-12)

`scripts/equity/gapfader_market.py` → `data/equity/gapfader/market.md`. User: "do we simply ignore weak fades on large
gaps down in the SPY?" Two views, side by side: the IDIOSYNCRATIC gap ((1 + gap) / (1 + SPY gap) − 1) with a
market-adjusted fade (the fade minus SPY's open → close in the same direction), and the CONTROL — QUIET days only
(|SPY gap| < 0.5 %, SPY's gap on the same causal prev-close).

**The market IS the census on big days:**

| SPY gap | days | gaps | gaps per day | same direction as SPY |
|---|---|---|---|---|
| < 0.5 % | 1,797 | 415,270 | 231 | 63 % |
| 0.5–1 % | 484 | 209,395 | 433 | 83 % |
| 1–2 % | 185 | 201,139 | 1,087 | 94 % |
| ≥ 2 % | 47 | 120,520 | 2,564 | 98 % |

56 % of all gaps fall on the 716 days SPY itself gaps ≥ 0.5 %. The idiosyncratic re-bucketing barely moves the
gap-up PFs (1.10–1.53 vs 1.11–1.55) and helps the gap-down side at 4–10 % (1.16–1.21 vs 0.97–1.11); the QUIET-day
control is the sharper tool: it removes the market days outright and leaves the stock's own gap.

**Quiet days ∧ weak premarket volume (rvol_pm < 0.02) — the user's setup — open → close, no costs:**

| side | gap | n | per year | mean % | median % | win % | PF |
|---|---|---|---|---|---|---|---|
| up (short) | 4–6 % | 22,546 | 2,239 | +0.78 | +1.12 | 59 | 1.43 |
| up (short) | 6–10 % | 9,744 | 968 | +1.10 | +1.63 | 61 | 1.49 |
| up (short) | 10–15 % | 1,900 | 189 | +1.03 | +1.88 | 60 | 1.34 |
| up (short) | ≥ 15 % | 493 | 49 | +3.93 | +3.90 | 66 | 2.40 |
| down (long) | 4–6 % | 17,731 | 1,761 | +0.59 | +0.61 | 54 | 1.28 |
| down (long) | 6–10 % | 7,990 | 793 | +0.78 | +0.87 | 55 | 1.28 |
| down (long) | 10–15 % | 1,698 | 169 | +1.33 | +1.10 | 55 | 1.38 |
| down (long) | ≥ 15 % | 597 | 59 | +1.69 | +1.48 | 56 | 1.37 |

The same gaps WITH premarket volume (rvol_pm ≥ 0.02) on quiet days: gap-up 6–15 % PF 1.18 / 1.10 (2016–20 / 2021–26),
gap-down 6–15 % 0.79 / 0.91. **Premarket volume is the axis that decides it**, on both sides.

**Year tables (fade PF raw, n):**

| cell | days | 2016 | 2017 | 2018 | 2019 | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| UP 6–15 %, weak pm | quiet | 1.33 | 1.34 | 1.79 | 1.03 | 1.25 | 2.09 | 1.62 | 1.56 | 1.43 | 1.51 | 1.24 |
| UP 6–15 %, weak pm | all | 1.22 | 1.33 | 1.67 | 1.03 | **0.84 (10,540)** | 1.60 | 1.16 | 1.44 | 1.24 | 1.83 | 1.21 |
| UP ≥ 15 %, weak pm | quiet | 3.18 (31) | 1.25 (50) | 2.84 (55) | 2.13 (42) | 1.27 (67) | 4.24 (31) | 6.25 (28) | 5.65 (47) | 2.11 (45) | 3.51 (62) | 1.92 (45) |
| DOWN 6–15 %, weak pm | quiet | 1.65 | 1.33 | 1.06 | 1.08 | 1.37 | 1.98 | 1.11 | 1.04 | 1.40 | 1.10 | 1.57 |
| DOWN 6–15 %, weak pm | all | 2.03 | 1.38 | 1.28 | 1.10 | **0.44 (11,357)** | 1.96 | 1.54 | 1.22 | 1.86 | 1.11 | 1.56 |
| DOWN ≥ 15 %, weak pm | quiet | 1.26 (32) | 0.90 (75) | 1.47 (67) | 1.17 (60) | 2.09 (34) | 2.93 (28) | 1.22 (42) | 1.08 (56) | 0.95 (70) | 1.97 (60) | 1.97 (73) |

Reading:
- **The user's hypothesis holds: gaps on weak premarket volume revert; gaps with premarket volume do not** (or far
  less: gap-up ≈ 1.1, gap-down < 1). Mechanism (reading): a gap nobody traded before the open is a gap without a
  catalyst — sentiment, not news — which is exactly the MR edge we look for; premarket volume is the news.
- **On quiet days the weak-volume cells are positive in EVERY year on both sides** (gap 6–15 %: up 1.03–2.09, down
  1.04–1.98). On ALL days 2020 breaks both (0.84 / 0.44, ~11k trades each — the COVID gap days): the market
  confound was real, and the quiet-day filter removes it.
- **Not just "ignore SPY gap-downs":** the 2020 damage is on BOTH sides and on market gap-UP days too (same-direction
  share 83–98 % on SPY gaps ≥ 0.5 %). Proposal: the quiet-day rule (|SPY gap| < 0.5 %) as the frame — 1,797 of
  2,513 days (72 %) — and a measured threshold later (0.3 / 0.5 / 0.75 / 1 %).
- Mean per trade +0.6 % … +3.9 %, enough to carry costs; ~5,000 weak-pm quiet-day trades a year at gaps ≥ 4 % (the
  4–10 % cells are most of them) — capacity is not the issue, buying power against the intraday systems is.
- Still open: the entry is the official OPEN PRINT (a MOO; for the short side a locate before 09:28); the exit the
  official CLOSE; no costs; the adverse excursion; the premarket-volume threshold itself (0.02 is GF-1's lowest band).

## GF-3 — the stock gaps AGAINST the market (user 2026-10-12: "SPY gaps up big, the stock gaps down on low volume?")

`scripts/equity/gapfader_opposite.py` → `data/equity/gapfader/opposite.md`. Weak-premarket gaps (rvol_pm < 0.02) by
SPY's gap measured in the stock's direction (against / quiet / with). Open → close fade, no costs.

| SPY's gap vs the stock | stock UP 4–15 %: n / days / mean % / PF (2016–20, 2021–26) | stock DOWN 4–15 %: n / days / mean % / PF (halves) |
|---|---|---|
| **against ≥ 1 %** | 1,230 / 118 / **+1.97 / 2.12** (2.32, 2.01) | 1,652 / 113 / **+1.19 / 1.53** (2.11, 1.26) |
| against 0.5–1 % | 2,744 / 197 / +1.21 / 1.59 (1.07, 2.01) | 3,749 / 286 / +0.66 / 1.25 (1.41, 1.20) |
| quiet < 0.5 % | 34,190 / 1,796 / +0.89 / 1.44 (1.37, 1.48) | 27,419 / 1,791 / +0.69 / 1.29 (1.29, 1.28) |
| with 0.5–1 % | 12,434 / 286 / +0.39 / 1.20 (1.10, 1.24) | 8,188 / 197 / +0.71 / 1.36 (1.66, 1.26) |
| **with ≥ 1 %** | 38,279 / 113 / **−0.28 / 0.87** (0.70, 1.26) | 39,561 / 119 / **−0.49 / 0.81** (0.47, 1.74) |

Gaps ≥ 15 % (thin): against ≥ 1 % 3.51 (29) / 1.10 (55); quiet 2.40 (493) / 1.37 (597); with ≥ 1 % 1.97 (315) /
2.11 (870) — a gap far larger than the market's is the stock's own even on a market day.

Reading:
- **A stock gapping AGAINST the market on weak volume is the BEST fade** — PF 2.12 short / 1.53 long, both halves
  > 1. SPY's own open → close is ~flat for these trades (−0.09 / +0.18 % in the fade's favour), so it is not the
  market reverting: it is the stock's own gap, and against the tide it is the most obviously unsupported.
- **The losses are the gaps that ARE the market's**: SPY with ≥ 1 % and a moderate gap (4–15 %) — 0.87 / 0.81, the
  2020 damage (0.70 / 0.47 in 2016–20). These stocks just moved with the index.
- ⇒ **Refines GF-2's proposal: do not drop market days — drop WITH-market moderate gaps.** The frame becomes: fade a
  weak-premarket gap unless SPY gapped ≥ ~1 % in the SAME direction and the stock's gap is < 15 %. Quiet and
  against-market days are kept (the against-market ones are the best). Threshold to measure (0.5 / 0.75 / 1 %).
- ⚠ Day clustering: the against-market cells come from 113–118 days; trades on one day are not independent.

## GF-4 — gaps on LARGE premarket volume: momentum? (user 2026-10-12)

`scripts/equity/gapfader_momentum.py` → `data/equity/gapfader/momentum.md`. The FOLLOW trade from the official open
(buy a gap-up / short a gap-down), to the close and to the next open; equal weight, no costs. User: "we've never
tested buying the gap directly and never considered the overall market action."

**Follow PF open→close / open→next open, by premarket relative volume (selected rows; full grid in the md):**

| rvol_pm | gap UP 6–15 % (buy) | gap UP 15–30 % | gap UP ≥ 30 % | gap DOWN 6–15 % (short) | gap DOWN 15–30 % | gap DOWN ≥ 30 % |
|---|---|---|---|---|---|---|
| < 0.02 | 0.87 / 0.90 | 0.51 / 0.57 | 0.33 / 0.37 | 1.09 / 0.86 | 0.66 / 0.52 | 0.32 / 0.22 |
| 0.1–0.25 | 0.95 / 1.12 | 0.91 / 0.91 | 0.59 / 0.61 | **1.31 / 1.32** | **1.33 / 1.11** | 1.20 / 1.08 |
| 0.25–0.5 | 0.82 / 0.91 | 0.87 / 0.92 | 1.21 / 0.80 | **1.44 / 1.40** | **1.32 / 1.30** | **1.70 / 1.33** |
| 0.5–1 | 0.75 / 0.83 | 0.78 / 0.78 | 0.69 / 0.72 | **1.42 / 1.45** | **1.43 / 1.33** | **1.75 / 1.83** |
| 1–2 | 0.63 / 0.49 | 0.86 / 0.79 | 0.70 / 0.63 | 1.15 / 1.02 | **1.44 / 1.67** | **1.80 / 1.92** |
| ≥ 2 | 0.49 / 0.44 | 0.70 / 0.52 | 0.67 / 0.57 | 0.93 / 0.97 | 1.28 / 1.14 | 0.91 / 0.99 |

**rvol_pm ≥ 0.5, by SPY's gap relative to the stock (follow open → close):**

| SPY vs the stock | gap UP 2–15 % / ≥ 15 % (buy): PF (n) | gap DOWN 2–15 % / ≥ 15 % (short): PF (n), halves 2016–20 / 2021–26 |
|---|---|---|
| against ≥ 0.5 % | 0.64 (344) / 0.76 (712) | 1.34 (231) / 1.44 (285) — 1.19 / 1.39 and 2.11 / 1.24 |
| quiet | 0.77 (2,386) / 0.72 (3,973) | 1.26 (1,313) / **1.57 (1,375)** — 1.11 / 1.32 and 1.74 / 1.52 |
| with ≥ 0.5 % | 0.89 (529) / 0.64 (1,011) | 1.37 (217) / **0.70 (211)** — 1.29 / 1.40 and 1.24 / 0.59 |

Reading:
- **Gap-UPS on heavy premarket volume have NO momentum** — buying them loses in every cell (0.49–0.95), worst on the
  largest gaps and the heaviest volume, whatever the market does. They FADE (GF-1: the fade PF 1.3–2.0 at rvol_pm ≥ 1).
  "Gap and go" on the long side is a loser here.
- **Gap-DOWNS on heavy premarket volume DO continue** — shorting the open: PF 1.15–1.80 for rvol_pm 0.1–2, both
  halves, every gap size; holding to the next open often adds (1.33–1.92). Bad news with volume keeps going.
- ⇒ With volume, the SHORT side wins both ways: fade the gap-up, follow the gap-down. Without volume, both gaps fade
  (GF-2). The long-side follow trade has nothing.
- **The market:** for the gap-down continuation it barely matters at 2–15 % (1.26–1.37 against / quiet / with); at
  ≥ 15 % it flips on with-market days (0.70; 2021–26 0.59): a stock that crashed WITH a crashing market bounces — the
  same with-market exception as GF-3's fades, mirrored.
- ⚠ Shorting a gap-down at the open: the SSR (Reg SHO 201: a stock −10 % from its prior close is short-restricted —
  shorts only above the national best bid, which a maker offer is) and locates on exactly these names. Not costed.

> **2026-10-12 evening — the universe refined** (mirrors the Oms's Universe.fs 8a77754: abbreviated partnership / trust /
> LLC common units are equity, not SPAC units; 134 excluded instead of 178). Census re-run: 1,278,260 gaps (+272), the
> headline universe 946,573 (+249). Every GF-1 … GF-4 table regenerated; the numbers quoted above are unchanged at their
> printed precision.
