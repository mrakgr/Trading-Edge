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

## GF-5 — the LONG fade by price × SPY's gap (2026-10-16; user: the focus moves to long systems)

`scripts/equity/gapfader_buckets.py` → `data/equity/gapfader/buckets.md` (also price × gap size). Weak-premarket
(rvol_pm < 0.02) gap-DOWNs ≥ 4 %, bought at the open, out at the official close; headline universe; no costs.
82,318 trades in 10.1 years. SPY's gap as GF-3 ("against" = SPY gapped UP while the stock gapped down).

Trades:

| | SPY up ≥ 1 % (against) | SPY up 0.5–1 % | quiet < 0.5 % | SPY down 0.5–1 % | SPY down ≥ 1 % (with) | all |
|---|---|---|---|---|---|---|
| $1–2 | 201 | 509 | 3,613 | 932 | 1,989 | 7,244 |
| $2–3 | 148 | 343 | 2,412 | 675 | 1,933 | 5,511 |
| $3–5 | 187 | 435 | 3,140 | 926 | 3,235 | 7,923 |
| $5–10 | 288 | 639 | 4,540 | 1,407 | 6,688 | 13,562 |
| $10–20 | 308 | 629 | 4,766 | 1,481 | 8,303 | 15,487 |
| $20–50 | 312 | 712 | 5,482 | 1,691 | 10,395 | 18,592 |
| ≥ $50 | 263 | 567 | 4,071 | 1,198 | 7,900 | 13,999 |
| all | 1,707 | 3,834 | 28,024 | 8,310 | 40,443 | 82,318 |

PF (open → close):

| | SPY up ≥ 1 % (against) | SPY up 0.5–1 % | quiet < 0.5 % | SPY down 0.5–1 % | SPY down ≥ 1 % (with) | all |
|---|---|---|---|---|---|---|
| $1–2 | 2.24 | 1.48 | 1.57 | 1.74 | 1.29 | 1.52 |
| $2–3 | 1.45 | 1.38 | 1.52 | 1.49 | 1.09 | 1.34 |
| $3–5 | 1.44 | 1.15 | 1.23 | 1.51 | 1.03 | 1.18 |
| $5–10 | 1.45 | 1.28 | 1.32 | 1.29 | 0.82 | 1.06 |
| $10–20 | 1.27 | 1.44 | 1.16 | 1.31 | 0.71 | 0.92 |
| $20–50 | 1.24 | 1.00 | 1.24 | 1.20 | 0.76 | 0.94 |
| ≥ $50 | 1.70 | 1.28 | 1.08 | 1.22 | 0.89 | 1.00 |
| all | 1.49 | 1.27 | 1.29 | 1.36 | 0.85 | 1.07 |

- **Price is the second axis: cheaper fades better.** $1–2 PF 1.52 overall (1.57 on quiet days), $2–3 1.34; from $5 up
  the quiet-day PF is 1.08–1.32 and every ≥ $5 row loses on with-market days.
- **The with-market column (SPY down ≥ 1 %) is half the trades (40,443) and loses from $5 up (0.71–0.89)** — GF-3's
  "drop with-market moderate gaps", by price. Without it: 41,875 trades (~4,150 / yr).
- Against-market days are the best cells (1.24–2.24) but thin: 1,707 trades (~170 / yr) from ~110 days (clustered).
- ⚠ No costs: at $1–2 the 0.2c/share commission alone is ~0.13 % a side, and the spread more; the cheap rows' edge must be
  re-measured with costs before it is trusted. The entry is the OPEN — a MOO, the order verified on live 10-15/16
  (NSDQ; NYSE / NYSE American via SMART).

**GF-5 costed (user: "how the gains relate to fees"):** mean % per trade, gross → NET of 0.7c/share round trip (0.2c
commission a side + ~0.15c each auction: the MOO entry and the official-close exit are auction prints, so no spread).
Cost = 0.50 % of the trade at $1–2, 0.29 % at $2–3, 0.18 % at $3–5, 0.10 % at $5–10, ≤ 0.05 % above.

| price | against ≥ 1 % | SPY up 0.5–1 % | quiet | SPY down 0.5–1 % | with ≥ 1 % |
|---|---|---|---|---|---|
| $1–2 | +2.74 → +2.25 | +1.28 → +0.78 | +1.36 → +0.85 | +1.50 → +1.00 | +0.76 → +0.27 |
| $2–3 | +1.19 → +0.90 | +1.06 → +0.76 | +1.35 → +1.06 | +1.09 → +0.80 | +0.25 → −0.04 |
| $3–5 | +1.26 → +1.08 | +0.52 → +0.34 | +0.71 → +0.53 | +1.14 → +0.96 | +0.10 → −0.08 |
| $5–10 | +1.30 → +1.20 | +0.80 → +0.70 | +0.88 → +0.78 | +0.68 → +0.58 | −0.53 → −0.63 |
| $10–20 | +0.69 → +0.64 | +1.07 → +1.02 | +0.43 → +0.38 | +0.63 → +0.58 | −0.86 → −0.91 |
| $20–50 | +0.55 → +0.53 | −0.00 → −0.03 | +0.53 → +0.50 | +0.39 → +0.37 | −0.60 → −0.62 |
| ≥ $50 | +1.05 → +1.05 | +0.52 → +0.52 | +0.16 → +0.15 | +0.37 → +0.36 | −0.22 → −0.23 |

Net PF, quiet days: $1–2 1.33, $2–3 1.39, $3–5 1.17, $5–10 1.27, $10–20 1.14, $20–50 1.23, ≥ $50 1.08. The fees take
~⅓ of the $1–2 edge and ~¼ of the $2–3 edge but leave every non-with-market cell positive; the with-market column is
≈ 0 or negative everywhere from $2 up. (The auction fees are an assumption — Rob has not answered; the statement's fee
lines will settle it.)

## GF-6 — buy the DIP of a heavy-premarket gap-UP? (2026-10-16; user: GF-4's open → close long loses — can a limit below the open fix it?)

`scripts/equity/gapfader_dip_buy.py` → `data/equity/gapfader/dip_buy.md`. Gap-ups ≥ 4 % with rvol_pm > 1 (5,581, headline
universe); a buy limit working all day, filled only if an RTH 1s vwap trades BELOW it (not a touch); out at the official
close (and the next open). No costs.

**A limit k % below the open — all gaps ≥ 4 %:**

| k | fill % | mean % → close | median % | PF → close | PF 2016–20 / 2021–26 | PF → next open |
|---|---|---|---|---|---|---|
| 0 (the open) | 100 | −2.62 | −4.19 | 0.70 | 0.52 / 0.74 | 0.58 |
| 2 % | 85 | −2.81 | −5.34 | 0.69 | 0.51 / 0.73 | 0.56 |
| 6 % | 71 | −2.66 | −4.11 | 0.68 | 0.53 / 0.71 | 0.53 |
| 10 % | 57 | −3.00 | −2.74 | 0.62 | 0.54 / 0.63 | 0.46 |
| 15 % | 40 | −2.54 | −2.04 | 0.67 | 0.65 / 0.67 | 0.47 |
| 20 % | 26 | −2.94 | −2.15 | 0.62 | 0.63 / 0.62 | 0.42 |
| 30 % | 10 | −4.00 | −1.80 | 0.55 | 0.30 / 0.59 | 0.30 |

By gap size (4–10 / 10–20 / 20–40 / ≥ 40 %) and by the GAP retraced (a limit at 25 / 50 / 75 / 100 / 125 % of the gap
— 100 % = the previous close): the same, PF 0.37–0.81, except gap 4–10 % with a limit 25–75 % into the gap (PF 1.14–1.25,
~45 trades / yr) — and that cell fails 2016–20 (0.64–0.80), its median is negative (−1.3 … −2.0 %) and to the next open
it loses (0.79–0.82): a few big winners, not an edge.

**Reading: no.** A dip limit does not rescue the heavy-premarket gap-up long — it is ADVERSE SELECTION: the limit fills
on exactly the days the gap is failing, and the fade keeps going to the close (and overnight: the next-open PF is lower
at every k). The median improves as k grows (−4.2 → −1.8 %) but the losers grow with it. These gaps are distribution,
not demand: their edge is the FADE (GF-1: PF 1.3–2.0), a short. On the long side the opening-gap edge is the
weak-premarket gap-DOWN fade (GF-2 / GF-3 / GF-5).

## GF-7 — heavy-premarket gap-UPs: who fades in the first hour, and buying the ones that hold (2026-10-16)

`scripts/equity/gapfader_hold_after_open.py` → `data/equity/gapfader/hold_after_open.{md,parquet}`. The 5,581 gap-ups of
GF-6; at 09:30 + 15 / 30 / 45 / 60 min from the 1s vwaps: the price, the low and high since the open; bought at the FIRST
1s vwap after the checkpoint (nothing after it decides), out at the official close. No costs.

**The fade: 62–65 % are below their open at every checkpoint** (15 min 62 %, 30–60 min 65 %); only 2–3 % never trade
below it. Buying the faded ones loses (PF 0.73–0.76); buying the ones that HELD (price ≥ the open) is about break-even
overall (0.77 / 0.94 / 0.92 / 0.97).

The promising-looking cells (all gaps ≥ 4 %; "at the high" = price ≥ 99 % of the high since the open, and ≥ the open):

| rule | T | n | mean % | median % | PF raw | PF clip +50 % | PF top 5 % trimmed | top 5 % of trades = gains |
|---|---|---|---|---|---|---|---|---|
| gap 4–20 %, HELD | 30 | 667 | +1.77 | −0.01 | 1.47 | 1.12 | **0.60** | 58 % |
| gap 4–20 %, HELD | 60 | 673 | +0.94 | −0.08 | 1.25 | 1.01 | **0.56** | 55 % |
| AT THE HIGH | 30 | 485 | +1.01 | +0.04 | 1.26 | 0.87 | **0.30** | 75 % |
| AT THE HIGH | 60 | 401 | +1.62 | +0.11 | 1.76 | 1.10 | **0.33** | 80 % |

Year tables jump from 0.4 to 17.6 on 9–126 trades a year (in the md and the check below the table).

**Reading: no long edge — a LOTTERY.** The medians are ~0; the top 5 % of trades make 55–80 % of all the gains; without
them every rule loses (PF 0.30–0.60), and clipped at +50 % it is break-even. The raw PFs (1.25–1.76) are a handful of
runners per year — the same profile that closed the momentum program (2026-07-27: "all magnitude, the median loses").
A heavy-premarket gap-up that holds its open is a coin flip with a fat right tail, not a long system; the edge in these
names stays the FADE (short).

## GF-8 — a BUY STOP above the open on heavy-premarket gap-UPs (2026-10-16)

`scripts/equity/gapfader_stop_entry.py` → `data/equity/gapfader/stop_entry.{md,parquet}`. The GF-6 gaps; a buy stop k %
above the official open, working all day; filled at the FIRST 1s vwap ≥ the stop (the overshoot included); out at the
official close. No costs. All gaps ≥ 4 %:

| stop | triggered | n | mean % | median % | PF raw | PF clip +50 % | PF top 5 % trimmed | top 5 % = gains |
|---|---|---|---|---|---|---|---|---|
| +2 % | 74 % | 4,114 | −2.54 | −5.82 | 0.74 | 0.52 | 0.29 | 61 % |
| +6 % | 55 % | 3,055 | −2.61 | −6.41 | 0.76 | 0.51 | 0.29 | 62 % |
| +10 % | 42 % | 2,340 | −2.43 | −7.10 | 0.78 | 0.52 | 0.30 | 62 % |
| +20 % | 24 % | 1,322 | −2.01 | −8.07 | 0.84 | 0.53 | 0.32 | 61 % |
| +30 % | 15 % | 839 | −1.77 | −9.55 | 0.87 | 0.53 | 0.35 | 59 % |

Gap 4–20 % with a high stop (+10 … +30 %) reaches PF 1.11–1.29 raw — trimmed 0.41–0.42, clipped 0.59–0.75, 2016–20 below
1: the same lottery as GF-7. By the trigger time (stop +4 %, all gaps): before 10:00 mean −2.95 %, 10:00–11:00 −1.39 %,
after 11:00 −0.24 %.

**Reading — why the limit (GF-6) and the stop (GF-8) give the SAME ≈ −2.5 % from any entry level:** these stocks move from
the open like a random walk with a NEGATIVE DRIFT. The level at which you enter carries no information — no reversion
after a dip (limits), no continuation after a rise (stops) — so the expected return from any entry is about the drift ×
the time left to the close: the trigger-time split shows it (−2.95 → −1.39 → −0.24 % as the entry moves later). The
median gets WORSE with a higher stop (−5.8 → −9.6 %): the stop buys exactly the spikes the fade then takes back. The
heavy-premarket gap-up is a short (the drift); there is no long entry in it.

## GF-9 — a buy LIMIT below the open on heavy-premarket gap-DOWNs (2026-10-16; user: "the same results as for the longs?")

`scripts/equity/gapfader_gapdown_dip.py` → `data/equity/gapfader/gapdown_dip.{md,parquet}`. Gap-downs ≤ −4 % with
rvol_pm > 1 (1,488 — a quarter of the gap-ups); a buy limit k % below the open, all day, filled AT the limit on the first
1s vwap below it; out at the official close. No costs.

| gap | buy the open: mean % / PF | limits 2–10 %: PF | limits 15–30 %: PF raw → top 5 % trimmed |
|---|---|---|---|
| all ≤ −4 % | −0.99 / 0.82 | 0.70–0.87 | 1.12–1.39 → 0.38–0.56 |
| −4 … −10 % (404) | +0.05 / 1.02 | 0.79–1.05 (trimmed 0.44–0.68) | thin (8–40 fills) |
| −10 … −20 % (292) | −0.95 / 0.75 | 0.70–0.87 | thin (12–37 fills) |
| ≤ −20 % (792) | −1.54 / 0.79 | 0.68–0.88 | 1.18–1.51 → 0.34–0.48 |

**Reading: yes, the same.** The limit entry is no better than the open; the deep limits' raw PF > 1 is a few rebounds (the
top 5 % = 49–72 % of the gains; trimmed 0.34–0.56). The drift sets the result: none for −4 … −10 % gaps (break-even at
every k), down for the larger ones (−1.0 / −1.5 % open → close) — GF-4's continuation, seen from the long side. Heavy
premarket volume on EITHER side of a gap is a short or nothing; the long gap edge is the WEAK-premarket gap-down (GF-2,
GF-3, GF-5).

## GF-10 — gap-UP CONTINUATION by the 1s TAPE: activity and its persistence (2026-10-16)

`scripts/equity/gapfader_tape_persistence.py` → `data/equity/gapfader/tape_persistence.{md,parquet}`. Gap-ups ≥ 4 % on
moderate+ premarket volume (rvol_pm ≥ 0.2; 15,308, headline universe) that HELD THE OPEN at T (price ≥ the open), bought
at the first 1s vwap after T, out at the official close. Tape features from the raw 1s bars (a row = a second with a
trade), knowable at T: active seconds and trades in the window, and their PERSISTENCE (the later window ÷ the earlier).

- **38–39 % hold the open** at 30 / 60 min. Held: PF 0.97 / 1.00 (median −0.87 / −0.45 %); not held 0.82 / 0.91.
- **The tape matters — the WRONG way for momentum.** The busiest tape is the worst: held at T = 30, trades Q5 (≥ 51.7k in
  30 min) PF 0.75, median −4.19 %; active seconds Q5 (≥ 1,760 of 1,800) 0.76; rising persistence (Q5) 0.84 at T = 60.
  The QUIET tape is the best: trades Q1–Q2 (< 10.1k) PF 1.23 / 1.46; active seconds Q1–Q2 1.25 / 1.27.
- **…but the quiet cells are tail-driven, not an edge.** Held at T = 30, trades < 10.1k: n 2,359, mean +1.11 %, MEDIAN
  −0.14 %, win 48 %; PF raw 1.37 → clip +50 % 1.10 → clip +20 % 0.94; years 0.67–2.45 with 4 of 11 below 1.0. Active
  seconds < 1,200: raw 1.26 → clip +50 % 0.96.

**Reading:** a gap-up that holds its open is break-even to the close; a busy, persistent tape marks DISTRIBUTION (the
heavier it trades, the more it falls — the GF-4 / GF-8 drift), and a quiet tape only removes those losers, leaving a
coin flip with a fat right tail. Holding to the close, there is no gap-up continuation long. (Not tested: an exit that
keeps the right tail — a trailing stop or a time stop — which is where any value in the quiet cells would have to be.)

### GF-10b — by premarket volume (user: "would a higher premarket volume threshold improve performance?")

Held the open at 10:00, PF to the close (clip +50 %), median; and the QUIET-tape half (trades 09:30–10:00 < 10.1k):

| rvol_pm | gaps | held | PF (clip +50 %) | median % | quiet tape: n / median % / PF / clip +50 % |
|---|---|---|---|---|---|
| 0.2–0.5 | 6,914 | 41 % | 1.06 (0.92) | −0.52 | 1,428 / −0.41 / 1.25 / 0.95 |
| 0.5–1 | 2,812 | 39 % | 0.89 (0.76) | −1.20 | 436 / −0.32 / 1.32 / 1.17 |
| 1–2 | 1,948 | 37 % | 1.11 (0.75) | −0.86 | 252 / **+0.27** / 1.57 / **1.56** |
| 2–5 | 1,724 | 34 % | 1.06 (0.82) | −0.89 | 146 / **+0.12** / 2.01 / **1.69** |
| ≥ 5 | 1,908 | 35 % | **0.77 (0.50)** | −3.70 | 97 / +0.00 / 2.05 / 1.07 |

Alone, more premarket volume does NOT help (≥ 5×: the worst). Combined with a quiet tape it gives the first long cell
with a positive median. **rvol_pm 1–5 ∧ held at 10:00 ∧ trades 09:30–10:00 < 10.1k:** n 398 (356 days), mean +1.87 %,
median +0.16 %, win 56 %, PF 1.73, clip +50 % 1.61, clip +20 % 1.36, top 5 % trimmed 0.97, to the next open 1.54; EVERY
year > 1 (1.15–12.3 on 4–73 trades); every gap and price band > 1 (1.11–2.31); dv20_prior ≥ $10M only 1.03. Control, the
same band with a BUSY tape: n 908, median −2.89 %, PF 1.00, clip 0.67. The TRADE COUNT decides, not the volume: by
rvol_30m (volume 09:30–10:00 / avgvol20_prior) the same band is flat (PF 0.80–1.36, medians negative).

⚠ Candidate, NOT a feature yet: the band (1–5) and the cut (10.1k, a population quintile edge) were read off this table
(selection); ~40 trades a year; few prints = thin names — the entry (a 1s vwap at 10:00) ignores the spread, and at $1–3
the 0.29–0.50 % fee alone exceeds the +0.16 % median. Next: threshold robustness (rvol 0.8–6, the cut 5k / 15k / 20k, a
cut relative to the stock's own print rate), the spread at 10:00 from the quotes, and costs.

## GF-11 — gap-UP continuation by the FIRST-30-MIN VOLATILITY (2026-10-16; user: the FlushFader / Snoozer measure)

`scripts/equity/gapfader_volat30.py` → `data/equity/gapfader/volat30.{md,parquet}`. The GF-10 trade (gap-up ≥ 4 %,
rvol_pm ≥ 0.2, held the open at 10:00, bought then, out at the official close). Volatility = the locked measure, slot
vwaps → mean |ln r| in bp, over 09:30–10:00: `volat_clk` (30 s clock slots — the Snoozer study's fixed-window analogue)
and `volat_pres` (30 present 1s bars per slot — the engines'); the two correlate 0.96 and give the same picture.

| volat_clk (bp / 30 s) | n | mean % | median % | win % | PF | PF clip +50 % | years < 1 |
|---|---|---|---|---|---|---|---|
| 0–20 | 657 | +0.21 | +0.09 | 55 | 1.42 | 1.42 | 1 / 11 |
| 20–40 | 941 | +0.60 | +0.23 | 52 | 1.38 | 1.38 | 1 / 11 |
| 40–80 | 2,103 | +0.85 | −0.82 | 46 | 1.25 | 1.17 | 2 / 11 |
| 80–150 | 1,554 | −0.83 | −4.64 | 36 | 0.89 | 0.72 | 7 / 11 |
| ≥ 150 | 667 | −3.10 | −11.36 | 29 | 0.80 | 0.43 | 7 / 11 |

- **Monotone: the calmer the first 30 min, the better the continuation** — the INVERSE of FlushFader (a fade, where
  volat ≥ 40 bp is the edge): a gap-up that held its open on a calm tape keeps going; a wild one is distribution.
- **Not tail-driven:** below 40 bp the PF with gains clipped at +50 % EQUALS the raw PF (1.42 / 1.38), the medians are
  positive and 1 year in 11 is below 1 — unlike every GF-6 … GF-10 cell. Small, steady winners.
- **Costed (0.7c / share round trip; entry at the 1s vwap — the spread not modelled):** volat_clk < 40, all rvol_pm ≥ 0.2:
  n 1,598 (~160 / yr), median price $23.86, net mean +0.37 %, **net PF 1.32 (2016–20 1.61 / 2021–26 1.20)**; net PF by price
  1.16 ($1–3) / 1.30 / 1.42 / 1.30 (≥ $30); by gap 1.35 / 1.31 / 1.22.
- With rvol_pm 1–5: n 258, net PF 2.47 (4.75 / 1.72) — the volatility and the GF-10b trade-count cut overlap (rvol 1–5,
  low volat × few trades: PF 1.93, median +0.13 %, n 266).

**Reading:** the first long gap cell with a steady (not lottery) profile. ⚠ Still in-sample: the bands were chosen on
this data; the per-trade edge is small (+0.4 % net) — sizing and the entry's spread decide whether it is worth trading.
Next: the spread at 10:00 from the quotes, the robustness of the 40 bp cut and the held-the-open rule (does a calm
gap-up that is slightly BELOW its open behave the same?), and an exit study.

## GF-12 — the weak-premarket gap-DOWN long: hold to the close or sell at 10:00? (2026-10-16)

`scripts/equity/gapfader_gapdown_30m.py` → `data/equity/gapfader/gapdown_30m.{md,parquet}`. The GF-5 long (rvol_pm < 0.02,
gap ≤ −4 %, bought at the open; 81,981 trades). At 10:00: the first-30-min volatility (30 s slots), trades and the price vs
the open; the value of HOLDING = the 10:00 → close leg (sold at the first 1s vwap after 10:00 otherwise).

**The edge is spent by 10:00.** Open → 10:00 earns +0.61 % (all days) / +0.75 % (ex with-market) of the whole open →
close +0.18 % / +0.74 %; the 10:00 → close leg is −0.42 % (PF 0.81) / −0.00 % (PF 1.00, 7 of 11 years < 1) / −0.86 % (PF
0.65, with-market days). The features barely move the ex-with-market hold leg (PF 0.93–1.08 in every volatility, trade
and price quintile; best cell low volat ∧ few trades 1.16, +0.16 %); on with-market days they separate the damage (high
volat / few trades −1.4 … −2.0 %) — but the answer there is to be out anyway.

**⇒ SELL AT 10:00.** Open → 10:00, NET of 0.7c / share round trip:

| set | n | → 10:00 net PF (2016–20 / 2021–26) | → close net PF | years < 1 (→ 10:00) |
|---|---|---|---|---|
| all days | 82,285 | **1.38** (1.31 / 1.43) | 1.03 | **0 / 11** |
| ex with-market | 41,842 | 1.40 (1.40 / 1.40) | 1.24 | 0 / 11 |
| with-market only | 40,443 | **1.35** (1.23 / 1.52) | 0.82 | 1 / 9 |
| quiet days | 27,992 | 1.45 (1.42 / 1.47) | 1.23 | 0 / 11 |
| against ≥ 1 % | 1,707 | 1.60 (1.97 / 1.41) | 1.42 | 0 / 11 |

By price (all days), → 10:00 / → close net PF: $1–2 1.59 / 1.29, $2–3 1.57 / 1.22, $3–5 1.39 / 1.11, $5–10 1.29 / 1.02,
$10–20 1.28 / 0.91, $20–50 1.34 / 0.93, ≥ $50 1.46 / 0.99.

- The with-market days — GF-3/GF-5's losing column — are a WINNER with a 10:00 exit: those stocks bounce at the open
  with the rest of the market and then go down with it. No market filter is needed.
- ~8,000 trades a year, every year, every price band.
- ⚠ The exit at 10:00 is not an auction: the 1s vwap ignores the spread, and the fee assumed (0.7c) is the auction
  close's — a taker sale is ~0.85c + half the spread. Next: the exit-time curve (5 … 120 min — is 10:00 the right
  moment?), the spread at the exit from the quotes, and the overlap / capacity (8k a year = ~32 a day).

## GF-13 — the EXIT-TIME CURVE of the weak-premarket gap-DOWN long (2026-10-16)

`scripts/equity/gapfader_exit_curve.py` → `data/equity/gapfader/exit_curve.{md,parquet}`. Bought at the official open,
sold at the first 1s vwap at/after 09:30 + T. Net PF (0.7c / share round trip):

| exit | all days (82,317) | ex with-market (41,874) | with-market (40,443) |
|---|---|---|---|
| 1 min | 1.30 | **1.72** | 0.88 |
| 5 min | 1.28 | 1.49 | 1.01 |
| 15 min | 1.22 | 1.45 | 0.93 |
| 20 min | 1.21 | 1.39 | 0.96 |
| **30 min** | **1.38** | 1.40 | **1.35** |
| **45 min** | **1.38** | 1.37 | **1.40** |
| 60 min | 1.29 | 1.36 | 1.20 |
| 120 min | 1.31 | 1.33 | 1.27 |
| close | 1.02 | 1.24 | 0.82 |

0 losing years (net) from 30 to 120 min on all days. Ex with-market the edge is IMMEDIATE (1 min: +0.59 % gross) and decays;
with-market days lose until ~20 min, then the market's "10 o'clock reversal" (a smooth rise 09:50 → 10:02 — checked
second by second: no step at 10:00:00, not an artifact; ⚠ half the with-market trades are 2020). By price, 30–45 min:
1.27–1.61 in every band ($1–3 best). Bigger gaps want later exits (15–30 %: 2.16 at 30 min → 2.56 at 60 min).
⚠ The 1-min result is an AUCTION effect (a thin name's opening print is low): real for a MOO fill, but the sale crosses
the spread — unmeasured. **⇒ One exit for every day: 10:00–10:15.** In and out before FlushFader's 09:45+ entries matter.

## GF-14 — TARGETS (2026-10-16; user: "we should also research targets")

`scripts/equity/gapfader_targets.py` → `data/equity/gapfader/targets.{md,parquet}`. A sell limit from the open, filled on a
TRADE-THROUGH (a 1s vwap above it) at the target; else sold at the time stop. Net PF (0.7c), all days, stop 10:00:

| exit | hit % | mean net % | median % | win % | PF net | years < 1 |
|---|---|---|---|---|---|---|
| time stop only | – | +0.49 | +0.45 | 56 | 1.38 | 0 / 11 |
| 25 % of the gap | 71 | +0.28 | +1.11 | 76 | 1.33 | 1 / 11 |
| **50 % of the gap** | 46 | +0.54 | +1.75 | 65 | **1.49** | 0 / 11 |
| **75 % of the gap** | 28 | +0.61 | +0.83 | 60 | **1.50** | 0 / 11 |
| 100 % (the fill) | 15 | +0.56 | +0.57 | 58 | 1.43 | 0 / 11 |
| +1 % | 80 | +0.06 | +0.91 | 82 | 1.10 | 5 / 11 |
| +3 % | 47 | +0.44 | +1.76 | 65 | 1.41 | 0 / 11 |

- **A target at 50–75 % of the gap + a 10:00 stop: PF 1.38 → 1.49–1.50**, both halves (1.41 / 1.56–1.58), every day type
  (ex with-market 1.45–1.52, with-market 1.47–1.55), every price band (50 % gap, 10:15 stop: 1.35–1.75).
- Gap-relative targets beat fixed ones; a small fixed target (+1 %) cuts the winners (1.10).
- Execution bonus: the target is a resting SELL LIMIT — a maker exit (a rebate, no spread) on 46–28 % of the trades.

## GF-15 — VOLUME around the open, and the premarket entry (2026-10-16; user: "enter in the premarket with limit orders, get rebates?")

`scripts/equity/gapfader_pm_volume.py` → `data/equity/gapfader/pm_volume.{md,parquet}`. $ volume per gap (median; p25 / p75
in the md): 04:00–07:00 $33, 07:00–09:00 $9.4k, 09:00–09:30 $8.8k, **the opening cross (the 09:30:00 1s bar) $27k (p25 $0)**,
09:30–09:35 $269k, 09:30–10:00 $1.34M (a whole day: dv20_prior $14.9M). 90 % have a premarket trade, 82 % in 09:00–09:30
(median 6 active seconds of 1,800).

- **The premarket is ABOVE the open:** the 09:00–09:30 vwap is above the official open in 74 % of the gaps (median
  +0.76 %, p90 +3.9 %); the last premarket print in 67 % (median +0.34 %). These names slide INTO the open — the open is
  near the low (hence the 1-min bounce, GF-13). Premarket $ traded BELOW the open in 09:00–09:30: median $0 (≥ $5k in
  28 %). ⇒ **No premarket entry**: a premarket buy pays more than the MOO, and a limit low enough to beat the open
  almost never fills.
- **Capacity at the cross is thin:** 10 % of the cross = median $2.7k a name (p75 $30k); a quarter of the gaps show NO
  volume in the 09:30:00 bar (⚠ to check on the trades tape: the official opening print's own time and size). The
  first 5 min trade 10× the cross ($269k): a position larger than the cross allows is filled in the first minutes.

## GF-16 — BUY DOWN: a limit below the open instead of the MOO, by the market (2026-10-16; user: "on big MARKET gap-down days we have more time to buy, could even buy down")

`scripts/equity/gapfader_buy_down.py` → `data/equity/gapfader/buy_down.{md,parquet}`. A buy limit k % below the official
open, 09:30–09:50, filled at the limit on a trade-through (a maker: 0.55c / share round trip vs the MOO's 0.7c); exit 10:15 or
a target at 50 % of the gap from the entry. Net PF (fill %):

| market | MOO | MOO + target | limit −1 % | limit −2 % | limit −3 % | limit −3 % + target |
|---|---|---|---|---|---|---|
| SPY gap DOWN ≥ 1 % (40,443 on 119 days) | 1.40 | 1.50 | 1.45 (62 %) | **1.66 (39 %)** | **1.80 (23 %)** | 1.77 (23 %) |
| SPY down 0.5–1 % (8,309) | 1.25 | 1.35 | 0.92 (63 %) | 1.05 (46 %) | 1.20 (33 %) | 1.25 |
| quiet / up (33,533) | 1.39 | 1.46 | **0.88 (60 %)** | 0.94 (45 %) | 0.98 (33 %) | 1.00 |

- **On quiet days a dip below the open is BAD NEWS** (adverse selection — 9 of 11 years lose at −0.5 / −1 %): buy the open.
- **On market gap-down days the user is right:** the stock keeps sliding with the market until the 10 o'clock reversal,
  and a deeper limit buys better — −2 % PF 1.66, −3 % 1.80, both halves (1.42 / 2.00, 1.60 / 2.12) — on fewer fills, so
  the day's total is below the MOO's (sum per day 108 / 83 % vs 170 %). ⇒ BUY DOWN, i.e. ADD: the MOO plus a second
  tranche at −2 / −3 % on market gap-down days (only), not instead of the MOO. ⚠ 119 days, half of the trades in 2020.

## GF-17 — a buy LIMIT AT THE OPEN PRICE after the cross, for more volume? (2026-10-16; user: "the crosses are thin")

`scripts/equity/gapfader_limit_at_open.py` → `data/equity/gapfader/limit_at_open.{md,parquet}`. (A limit-ON-open is the same
auction — no extra volume.) A buy limit at the official open, 09:30:01 → 09:35 / 09:50, filled at the open on a
trade-through (or a touch); exit 10:15 or a target at 50 % of the gap after the fill; 0.55c / share.

- **The volume is there:** $ traded at or below the open after the cross, median per gap — first 1 min $7.4k, 5 min
  $60k, 20 min $228k (the cross: $27k); 22 % of the gaps never trade back to the open within 5 min.
- **…but it is ADVERSE SELECTION:** the limit fills 77–86 % of the gaps at PF 1.00–1.03 (all days; quiet days 0.89–0.93,
  9 of 11 years lose; market gap-down days 1.15–1.17) against the MOO's 1.47. The 14 % it MISSES — the gaps that never
  come back to the open — are where the MOO's edge lives: on them the MOO makes +2.75 % a trade, win 92 %, PF 14.
- ⇒ **The edge is in the opening auction itself** (the low opening print, then the bounce). The cross is the capacity;
  volume bought after it is a different, break-even trade. Size each position as a share of the cross (median $2.7k at
  10 %); the binding limit comes with account size, not now.

## GF-18 — the entry as a LIMIT-ON-OPEN, and which names get one (2026-10-16; user: "how will we know where they open?")

`scripts/equity/gapfader_loo_coverage.py` → `data/equity/gapfader/loo_coverage.{md,parquet}`. A LOO buy at prev close × 0.96
fills only if the stock OPENS ≥ 4 % down, at the open — the gap rule without knowing the open (a delayed open just waits).
Which names get one before the 09:25 cutoff? A premarket screen (a print before 09:25 at ≤ prev close × (1 − x)):

| screen | trades covered | covered: PF net | MISSED: n / PF net | LOOs per day | that fill |
|---|---|---|---|---|---|
| last print ≤ 0 % | 81 % | 1.32 | 15,654 / **2.33** | 107 | 25 % |
| last print ≤ −2 % | 70 % | 1.25 | 24,425 / **2.22** | 68 | 34 % |
| last print ≤ −4 % | 49 % | 1.23 | 42,010 / **1.80** | 23 | 69 % |
| premarket low ≤ 0 % | 83 % | 1.35 | 13,738 / **2.25** | 114 | 24 % |
| premarket low ≤ −2 % | 77 % | 1.31 | 18,961 / **2.19** | 90 | 28 % |

(LOOs per day count only the census's gaps ≥ 2 %; names opening within 2 % add more.) 15 % of the trades have NO premarket
print before 09:25 at all: PF net **2.19**, +1.12 % a trade, every price band ≥ 2.06.

**Reading — the best trades are the SURPRISES.** A gap the premarket already showed is a weaker trade (PF 1.23–1.35); the
gaps a premarket screen cannot see — no print, or a print still near the prev close — carry PF 1.8–2.3: the opening
auction prints a gap nobody traded into. A premarket screen therefore keeps the weaker 70–80 % and loses the best.
To see the surprises before the cutoff the auction's own data is needed: the exchanges' IMBALANCE feeds (Nasdaq NOII,
NYSE / NYSE American imbalance publications — the indicative opening price) — or a LOO on every name, which is not
feasible (pending orders tie up buying power; ~5,600 names).

### GF-18b — where to get the imbalance (2026-10-16/17; user: "don't data providers sell auction data?")

| source | what | history | real time, non-pro | note |
|---|---|---|---|---|
| Massive | NYSE order imbalances (NOI) only | none | $49 / month | no Nasdaq, Arca or NYSE American |
| Databento | XNAS.ITCH (NOII), XNYS / XASE / ARCX.PILLAR imbalance; no BZX | from 2018-05 | live XNAS.ITCH is for businesses only; the $199 plan has no imbalance | history: full Nasdaq ~$17.5k, the day's candidates only ~$0.23 / day |
| **IBKR** (2026-10-17, its public market-data pricing page) | NYSE Order Imbalances $1, NYSE MKT (American) Order Imbalances $1, NYSE Arca Order Imbalances $1 a month each (pro the same); Nasdaq's NOII comes with **TotalView-OpenView $16.50** (pro $90) | none | ≈ **$19.50 / month** for the four listings; no BZX line | TWS API generic tick **225**: Auction Volume (34), Auction Price (35, the indicative price), Auction Imbalance (36), Regulatory Imbalance (61) |

**⭐ The clock decides it, not the price.** Each listing's imbalance starts at a different time, and the Oms's MOO / LOO
cutoff is **09:25** (Lightspeed, Rob 2026-10-12; `L3Types.MooUntil`):

| listing | the opening imbalance from | before our 09:25 cutoff? |
|---|---|---|
| NYSE | 08:00 (read 2026-10-16) | yes, 85 minutes of it |
| Nasdaq | **09:25**, every second to 09:30 (nasdaqtrader.com; was 09:28 every 5 s); the exchange's own MOO cutoff is 09:28, LOO 09:29:30 | **no — it starts AT the cutoff** |

Most of the book is Nasdaq-listed small caps, so through Lightspeed the Nasdaq imbalance arrives exactly when we can no
longer send a LOO. It is only usable if a later order type reaches the cross: Lightspeed's real cutoff for a NSDQ LOO
(the exchange's is 09:29:30 — never tested by us; Rob's "09:25" has been wrong before), or a regular limit order joining
the cross. Both need a live test on the account (planned for Monday 10-19).

Open before subscribing:
- whether IBKR delivers Nasdaq's NOII through tick 225 at all (documented only per tick, not per exchange: "not all tick
  types are available for all instruments at all times") — one day on the existing IBKR gateway (localhost:4002) settles it;
- market-data LINES: one per streamed symbol, 100 by default (more with commissions / equity, or booster packs); the
  candidate list must fit;
- whether the exchanges' NON-PROFESSIONAL terms allow automated (non-display) use of the feed.

## GF-19 — the premarket QUOTE vs the official OPEN (2026-10-17; user: "can we estimate the open from the quotes?")

`scripts/equity/gapfader_preopen_quotes.py` (Massive REST v3/quotes: the NBBO standing at 09:25:00 + every update to 09:30,
every universe ticker, 10 days 2026-10-02 … 10-15, ~38 s a day) → `data/equity/gapfader/preopen_quotes/<date>.parquet`;
`scripts/equity/gapfader_preopen_vs_open.py` → `data/equity/gapfader/preopen_vs_open.{md,parquet}`. Population: prev close
≥ $1, dv20_prior ≥ $1M — 3,857 names a day, ALL of them (not only the weak-premarket ones: no premarket trades yet for
these days). Every name has a two-sided NBBO at 09:25 (never crossed or locked on the SIP).

| 09:25 spread | share | mid vs open: median abs err | p90 | open inside the 09:25 bid–ask |
|---|---|---|---|---|
| < 0.5 % | 10.9 % | 0.20 % | 0.70 % | 40.9 % |
| 0.5–1 % | 9.4 % | 0.34 % | 1.04 % | 62.7 % |
| 1–2 % | 13.7 % | 0.48 % | 1.23 % | 77.5 % |
| 2–5 % | 23.6 % | 0.78 % | 1.87 % | 90.1 % |
| 5–10 % | 15.3 % | 1.62 % | 3.55 % | 95.4 % |
| ≥ 10 % | 27.0 % | 5.54 % | 23.9 % | 98.4 % |

The mid at 09:25 is as good as the one at 09:28 (median abs err 0.90 / 0.89 %); only the last second sharpens it (0.68 %).

**The gap screen at 09:25** (a LOO for each name it flags) against the 697 names that OPENED ≥ 4 % down (69.7 a day):

| screen | caught | LOOs a day | that open ≥ 4 % down (fill) |
|---|---|---|---|
| mid ≤ −2 % | 92.0 % | 639 | 10.0 % |
| mid ≤ −4 % | 73.0 % | 357 | 14.3 % |
| **ask ≤ −2 %** | **80.9 %** | **130** | **43.5 %** |
| **ask ≤ −3 %** | **72.2 %** | **72** | **70.0 %** |
| ask ≤ −4 % | 57.0 % | 43 | 91.9 % |
| bid ≤ −4 % | 89.4 % | 1,072 | 5.8 % |

**Reading — the ASK is the signal, not the mid.** A wide pre-open book puts the mid anywhere; an offer already below the
prev close is a seller committed there. GF-18's best print screen (last print ≤ −2 %) covered 70 % at 68 LOOs a day and
34 % filling; the ask ≤ −3 % screen covers 72 % at the same count with **70 %** filling. ⚠ Not like for like: GF-18 is the
weak-premarket trades over 11 years, this is every name over 10 days (697 openers). The open question is whether the
SURPRISES (GF-18: no premarket print, PF 2.2) have an ask below the prev close at 09:25 — that needs these days'
premarket trades (rvol_pm and the last print), not yet downloaded.

## GF-20 — the edge by GAP SIZE, and a LOO LADDER to size by it (2026-10-17; user: "larger gaps are more profitable")

> ⚠ **SUPERSEDED by GF-23 (same day): the rise of PF−1 past 15 % is 2020** — March 2020 alone is 2,730 of the 6,545
> trades of 10 % or more. Without 2020, PF−1 is 0.53 / 0.71 / 0.79 / 0.57 / 0.65 / 0.60 from the 4–6 % band to ≥ 20 %:
> flat past 6 %. The avg % still rises with the gap (the moves are bigger), the edge per unit of risk does not.

`scripts/equity/gapfader_gap_size.py`. The weak-premarket gap-down long as GF-14 (open → target 50 % of the gap / 10:15,
net 0.7c), 82,285 trades 2016–2026:

| gap | trades / yr | avg % | median % | PF raw | PF clip ±50 % | PF−1 | losing years |
|---|---|---|---|---|---|---|---|
| 4–6 % | 4,777 | +0.40 % | +1.96 % | 1.40 | 1.40 | 0.40 | 0/11 |
| 6–8 % | 1,501 | +0.54 % | +2.31 % | 1.41 | 1.41 | 0.41 | 0/11 |
| 8–10 % | 607 | +0.81 % | +2.34 % | 1.50 | 1.50 | 0.50 | 1/11 |
| 10–15 % | 438 | +1.00 % | +1.96 % | 1.52 | 1.52 | 0.52 | 1/11 |
| 15–20 % | 103 | +2.65 % | +3.64 % | 2.32 | 2.33 | 1.32 | 1/11 |
| 20–30 % | 40 | +4.04 % | +4.95 % | 2.42 | 2.42 | 1.42 | 2/9 |
| ≥ 30 % | 13 | +8.87 % | +3.45 % | 3.26 | 3.05 | 2.26 | 0/1 |

PF−1 steps rather than climbs: ~0.4 to 8 %, ~0.5 to 15 %, ~1.3–1.4 beyond (thin: 156 trades a year past 15 %). Raw = clip
everywhere: no tail drives it.

**The ladder.** The size must follow a gap that is unknown until the cross. Buy LOOs at several limits — the auction fills
every rung whose limit is at or above the open price — so the position grows with the actual gap by construction:
a rung at prev close × 0.96 (PF−1 0.4), one at × 0.92 (+0.1), one at × 0.85 (+0.9, to 1.3). ⚠ Every rung reserves buying
power until the cross, at its own limit — a cost the screen (GF-19: ~72 names a day) multiplies.

## GF-21 — MAKER exits (2026-10-17; user: "limit orders for exits and the rebates; the target until 10:00, then the usual maker policy")

`scripts/equity/gapfader_maker_exit.py`. Entry = the LOO in the cross: commission 0.2c + Nasdaq's opening-cross fee 0.15c.
Exit = a resting sell limit at f of the gap until 10:00 (GF-14's trade-through fill), else the maker exit from 10:00,
modelled as the first 1s vwap at/after 10:00 (neutral: no half spread earned, no chase). Exit fee 0.2c − the NSDQ rebate
0.15c. **Round trip 0.40c** (GF-14 charged 0.70c). 82,285 trades:

| exit | avg % | PF raw | PF clip ±50 % | PF−1 | target hit | losing years |
|---|---|---|---|---|---|---|
| old: 50 % to 10:15, taker, 0.70c | +0.56 % | 1.47 | 1.47 | 0.47 | 50 % | 0/11 |
| 50 % to 10:00, maker, 0.40c | +0.59 % | 1.54 | 1.54 | 0.54 | 46 % | 0/11 |
| **75 % to 10:00, maker, 0.40c** | **+0.66 %** | **1.55** | 1.55 | **0.55** | 28 % | 0/11 |
| 100 % to 10:00, maker, 0.40c | +0.60 % | 1.48 | 1.48 | 0.48 | 15 % | 0/11 |

PF−1 by gap band (avg % in brackets):

| gap | trades / yr | old | 50 % maker | 75 % maker | 100 % maker |
|---|---|---|---|---|---|
| 4–6 % | 4,777 | 0.40 (+0.40 %) | 0.46 (+0.43 %) | 0.46 (+0.48 %) | 0.38 (+0.42 %) |
| 6–8 % | 1,501 | 0.41 (+0.54 %) | 0.54 (+0.64 %) | 0.54 (+0.71 %) | 0.49 (+0.66 %) |
| 8–10 % | 607 | 0.50 (+0.81 %) | 0.63 (+0.91 %) | 0.66 (+1.01 %) | 0.61 (+0.96 %) |
| 10–15 % | 438 | 0.52 (+1.00 %) | 0.58 (+1.00 %) | 0.61 (+1.09 %) | 0.59 (+1.08 %) |
| 15–20 % | 103 | 1.32 (+2.65 %) | 1.22 (+2.30 %) | 1.32 (+2.51 %) | 1.29 (+2.47 %) |
| 20–30 % | 40 | 1.42 (+4.04 %) | 1.03 (+2.93 %) | 1.09 (+3.16 %) | 1.09 (+3.16 %) |
| ≥ 30 % | 13 | 2.26 (+8.87 %) | 1.76 (+6.26 %) | 1.90 (+6.76 %) | 1.85 (+6.57 %) |

The fee saving (0.3c) lifts the small gaps; the deep gaps (≥ 15 %) LOSE from the 10:00 stop — their move is still going
at 10:00, and the old 10:15 stop caught more of it. ⚠ Not yet tested: a later stop for the deep gaps only; the Nasdaq
cross fee for a LOO (assumed = the MOO's 0.15c) and NYSE's via SMART; the maker exit's real fill (needs the quotes).

## GF-22 — does the 09:25 ASK see the DEEP gaps? (2026-10-17)

GF-19's 10 quote days, every name (not only the weak-premarket ones):

| opened | n (a day) | 09:25 ask ≤ −3 % | ≤ −6 % | ≤ −10 % | ≤ −15 % | median ask gap | median open gap |
|---|---|---|---|---|---|---|---|
| ≤ −4 % | 697 (69.7) | 72 % | 28 % | 14 % | 6 % | −4.3 % | −5.3 % |
| ≤ −8 % | 167 (16.7) | 94 % | 87 % | 57 % | 25 % | −11.2 % | −12.8 % |
| ≤ −15 % | 60 (6.0) | 98 % | 98 % | 93 % | 65 % | −18.7 % | −19.8 % |

| 09:25 ask | names a day | opened ≤ −4 % | ≤ −8 % | ≤ −15 % | median open gap | open above the ask |
|---|---|---|---|---|---|---|
| 3–6 % down | 51.7 | 59 % | 2 % | 0 % | −4.2 % | 30 % |
| 6–10 % down | 10.5 | 97 % | 48 % | 3 % | −8.0 % | 31 % |
| 10–15 % down | 5.5 | 98 % | 96 % | 31 % | −13.5 % | 24 % |
| ≥ 15 % down | 4.2 | 100 % | 100 % | 93 % | −23.8 % | 36 % |

Rank correlation of the ask's gap with the open's: 0.69 among the ≥ 4 % openers. **The deeper the gap, the better the
ask sees it** — 98 % of the ≥ 15 % openers were offered ≥ 6 % down at 09:25 — and the open lands near or BELOW the
09:25 ask (median open gap −1 pt beyond the ask's). So the ladder's deep rungs can go only where the ask is already deep:
rung 1 on every ask ≤ −3 %, rung 2 (× 0.92) from an ask ≤ −6 % (~20 names a day), rung 3 (× 0.85) from ≤ −10 % (~10).
⚠ All names, 10 days: the weak-premarket subset (the trade) needs the new 1s bars.

> ⚠ **The counts above are EVERY name, not the trade (user 2026-10-17).** Gap-downs a day, every name vs weak premarket
> (rvol_pm < 0.02): 2026 to 09-04 — ≥ 4 %: 77.2 vs 39.7; ≥ 8 %: 20.9 vs 5.2; ≥ 15 %: 6.61 vs 0.67 (2016–2026: 52.8 / 32.9,
> 14.2 / 5.3, 4.02 / 0.70; the 10 quote days, every name: 69.7 / 16.7 / 6.0 — in line with 2026). The weak-premarket
> filter keeps ~½ of the ≥ 4 % gaps, ~¼ of the ≥ 8 % and ~1/10 of the ≥ 15 %: deep gaps come with heavy premarket volume
> (news). The real screen is the ask AND rvol_pm to 09:25 (known before the cutoff) — far fewer names than 72 / 20 / 10.
> The ladder's deep rungs are moot anyway (GF-23).

## GF-23 — a LATER STOP for the deep gaps? No: the deep-gap edge is MARCH 2020 (2026-10-17; user: "test whether a longer stop would help")

`scripts/equity/gapfader_late_stops.py` → `data/equity/gapfader/late_stops.{md,parquet}`: GF-21's exit (target f of the gap,
trade-through; else the maker exit at the stop's first 1s vwap; 0.40c) with the stop at 10:00 … 15:55, by gap band, for
f = 50 / 75 / 100 %. Over all 11 years the deep gaps peak at an 11:00 stop (≥ 15 %, target 75 %: PF−1 1.29 at 10:00 →
2.05 at 11:00; 10–15 %: 0.60 → 0.74); the gaps under 10 % are best at 10:00 and decay after. A tiered stop (≥ 10 % → 11:00)
lifts the book 1.55 → 1.60 — **but the year table puts the whole gain in 2020** (the ≥ 10 % gaps' PF, 10:00 → 11:00:
2020 2.31 → 3.39; worse in 7 of the other 10 years). Without 2020 (target 75 %, PF−1):

| gap | 10:00 | 10:15 | 10:30 | 11:00 | 12:00 | trades ex-2020 | 2020's share |
|---|---|---|---|---|---|---|---|
| 4–10 % | **0.60** | 0.56 | 0.52 | 0.48 | 0.57 | 52,552 | 31 % |
| 10–15 % | **0.56** | 0.53 | 0.51 | 0.51 | 0.48 | 2,541 | 47 % |
| ≥ 15 % | **0.63** | 0.56 | 0.59 | 0.55 | 0.49 | 867 | 50 % |

GF-21's exit by gap band, all years vs WITHOUT 2020:

| gap | trades / yr ex-2020 | PF−1 all | PF−1 ex-2020 | avg % all | avg % ex-2020 | median % ex-2020 | PF clip ex-2020 | losing years ex-2020 |
|---|---|---|---|---|---|---|---|---|
| 4–6 % | 3,844 | 0.46 | 0.53 | +0.48 % | +0.56 % | +0.92 % | 1.53 | 0/10 |
| 6–8 % | 1,018 | 0.54 | 0.71 | +0.71 % | +0.95 % | +1.44 % | 1.71 | 0/10 |
| 8–10 % | 395 | 0.66 | 0.79 | +1.01 % | +1.32 % | +2.01 % | 1.79 | 0/10 |
| 10–15 % | 254 | 0.61 | 0.57 | +1.09 % | +1.24 % | +1.70 % | 1.57 | 0/10 |
| 15–20 % | 55 | 1.32 | 0.65 | +2.51 % | +1.83 % | +1.82 % | 1.65 | 1/10 |
| ≥ 20 % | 32 | 1.33 | 0.60 | +4.06 % | +2.35 % | +0.99 % | 1.58 | 3/10 |

The book itself does NOT depend on 2020: all years PF 1.55 (+0.66 %), without 2020 **1.60** (+0.74 %, 55,970 trades);
2020 alone 1.43 (26,315 trades — 32 % of the book), March 2020 alone 1.12 (13,664). 2020 dilutes the book and inflates
only its deep tail.

**Reading.** (1) Keep the 10:00 stop everywhere. (2) The gap size does not grade the edge past ~6 %: PF−1 ~0.53 for
4–6 %, ~0.6–0.8 beyond, with nothing rising after 10 %. Under L2's rule (one unit = one PF−1) the deep gaps get NO more
units — GF-20's ladder for sizing is unsupported. What does rise is the avg % a trade (0.56 → 2.35 %): the fixed per-share
fees matter less on a deep gap, which is a reason to TAKE every gap, not to size it up. ⚠ Lesson (again, cf. the
2026-07-27 momentum close): a band whose edge rises with magnitude must be checked by year — a single crash month can
be half its trades.

## GF-24 — the OPENING CROSS vs the daily dollar volume, and the trade by LIQUIDITY (2026-10-17; user: "do gaps in more liquid stocks make better trades?")

`scripts/equity/gapfader_cross_liquidity.py` → `data/equity/gapfader/cross_liquidity.{md,parquet}` (+ `_trades.parquet`).
The cross from the bulk trades tape (GF-15's proxy was the 09:30:00 1s bar, p25 $0): the largest print with condition 17
(opening trade) 09:28–10:00 — the listing exchange's cross (checked on AAPL / SOFI / WFF = Nasdaq 12, F = NYSE 10, UEC =
NYSE American 1; other venues print tiny openers; the condition-16 copy of the same print is not counted). Found for
98.5 % of the 82,285 trades; ⚠ its price = the official open for only 76.1 % (to check: which print the daily open is);
14.6 % of the crosses print at or after 09:31, 6.7 % after 09:35 (late NYSE opens).

**The cross is ~1 % of the daily dollar volume, at every liquidity**: Spearman ρ(cross $, dv20_prior) 0.78, log-log slope
**1.01**.

| dv20_prior | trades / yr | cross $ p25 | median | p75 | cross / dv20 median (p25–p75) | no cross |
|---|---|---|---|---|---|---|
| $1–2M | 790 | $4.7k | $14.9k | $38k | 1.05 % (0.33–2.64) | 2.7 % |
| $2–5M | 1,228 | $12.4k | $33.8k | $78k | 1.06 % (0.40–2.35) | 1.7 % |
| $5–10M | 1,061 | $28.9k | $70.9k | $150k | 1.00 % (0.41–2.10) | 1.4 % |
| $10–25M | 1,475 | $63.6k | $145k | $289k | 0.93 % (0.42–1.80) | 1.3 % |
| $25–100M | 1,793 | $206k | $432k | $813k | 0.91 % (0.48–1.59) | 1.5 % |
| ≥ $100M | 1,133 | $1.17M | $2.41M | $4.96M | 1.05 % (0.64–1.66) | 0.7 % |

**The edge falls with liquidity** (GF-21's exit; ex-2020 in bold):

| dv20_prior | PF−1 all | **PF−1 ex-2020** | avg % ex-2020 | median % ex-2020 | PF clip ex-2020 | losing yrs ex-2020 |
|---|---|---|---|---|---|---|
| $1–2M | 1.19 | **1.39** | +1.53 % | +3.07 % | 2.39 | 0/10 |
| $2–5M | 0.72 | **0.74** | +0.98 % | +1.96 % | 1.74 | 0/10 |
| $5–10M | 0.55 | **0.57** | +0.76 % | +1.24 % | 1.57 | 0/10 |
| $10–25M | 0.41 | **0.48** | +0.62 % | +0.91 % | 1.48 | 0/10 |
| $25–100M | 0.35 | **0.41** | +0.49 % | +0.61 % | 1.41 | 0/10 |
| ≥ $100M | 0.40 | **0.29** | +0.31 % | +0.34 % | 1.29 | 1/10 |

By the cross's own dollars (known only at the open — capacity, not a gate; ex-2020): none 1.47 · < $5k **3.45** · $5–25k
1.34 · $25–100k 0.44 · $100–500k 0.29 · ≥ $500k 0.28. The thinner the auction, the bigger the dislocation it prints.

**Reading.** (1) dv20_prior is a CAUSAL grade (known before the open) and a monotone one, 0/10 losing years in every band
but the top: under L2's rule the illiquid gaps earn the most units. (2) But the capacity runs the other way: the cross is
~1 % of dv20, so the $1–2M names (PF−1 1.39) cross a median $15k. (3) Capacity-limited estimate (ex-2020; position =
min(units × 15 % × equity, 10 % × 1 % × dv20_prior), units = the band's PF−1; the units cap, other systems, the screen's
coverage and the LOO fills IGNORED):

| equity | avg position | capacity binds | $ / day | % of equity / day | Σ units / day |
|---|---|---|---|---|---|
| $8k | $691 | 8 % | $151 | 1.89 % | 14.3 |
| $30k | $2,086 | 21 % | $386 | 1.29 % | 11.5 |
| $100k | $5,211 | 40 % | $835 | 0.83 % | 8.6 |
| $300k | $10,950 | 61 % | $1,532 | 0.51 % | 6.0 |
| $1M | $21,430 | 80 % | $2,608 | 0.26 % | 3.5 |

⚠ At 8k the day's positions sum to 14.3 units, all opened at 09:30: the 10-unit book cap binds (≈ 1.3 %/day). The system
is a small-account edge: it scales sub-linearly from ~$30k and is worth ~0.3 %/day at $1M.

## GF-25 — the screen on the WEAK-PREMARKET names: the ASK alone is not enough (2026-10-17)

`scripts/equity/gapfader_ask_screen.py` → `data/equity/gapfader/ask_screen.{md,parquet}`. GF-19's 10 quote days with the new
1s bars (09-05 … 10-15 built 10-17): premarket volume to 09:25 (what the screen knows) and to 09:30 (the census's rvol_pm —
they agree on the 0.02 line for 99.3 % of names). THE TRADE = opened ≥ 4 % down with rvol_pm < 0.02: 29.6 a day (296; of the
67.5 a day opening ≥ 4 % down). Every screen below also requires rvol to 09:25 < 0.02; a screened name gets a LOO at
prev close × 0.96 (it fills iff the stock opens ≥ 4 % down).

| screen at 09:25 | trades caught | LOOs a day | fill |
|---|---|---|---|
| ask ≤ −2 % | 64 % | 64.3 | 31 % |
| ask ≤ −3 % | 47 % | 26.2 | 58 % |
| ask ≤ −4 % | 28 % | 10.9 | 85 % |
| last print ≤ −2 % | 86 % | 213.8 | 13 % |
| last print ≤ −3 % | 79 % | 93.5 | 27 % |
| **ask ≤ −3 % OR last print ≤ −3 %** | **80 %** | **93.9** | **27 %** |
| ask ≤ −2 % OR last print ≤ −3 % | 82 % | 118.2 | 22 % |
| mid ≤ −3 % | 75 % | 396.1 | 6 % |

By liquidity (dv20_prior):

| screen | $1–5M (57 trades): caught / LOOs a day / fill | $5–25M (85) | ≥ $25M (154) |
|---|---|---|---|
| ask ≤ −3 % | 26 % / 3.1 / 55 % | 31 % / 5.8 / 50 % | 64 % / 17.3 / 61 % |
| last print ≤ −3 % | 63 % / 22.6 / 17 % | 73 % / 27.0 / 24 % | 88 % / 43.9 / 33 % |
| ask ≤ −3 % OR print ≤ −3 % | 65 % / 22.7 / 17 % | 73 % / 27.1 / 24 % | 89 % / 44.1 / 33 % |

**Reading.** GF-19's "the ask catches 72 %" was every name — the heavy-premarket (news) gaps, whose books follow the news.
On the weak-premarket names the 09:25 ask catches 47 %, and only 26 % in the $1–5M band, the best one (GF-24: PF−1 1.39):
their books are wide (the missed trades' spread median 4.7 %) with stale offers near the prev close (missed ask median
−0.7 % while the open is −4.5 %). The few PRINTS a thin name has are the better signal there: last print ≤ −3 % catches
63–88 %. The OR of both is the best screen — 80 % at ~94 LOOs a day, 27 % filling. The SURPRISES (no print before 09:25)
are only 3 of 296 here (GF-18: 15 % over 2016–2026 — the premarket is busier in 2026). ⚠ 10 days, 296 trades. ⚠ ~94 LOOs a
day with 27 % filling: whether a resting LOO holds BUYING POWER at Lightspeed (and how much) decides whether this fits —
a live check (one LOO, read `pendingOrdersMarginRequirements`).

## GF-26 — premarket ACTIVITY (1s bars) vs rvol_pm, and where they disagree (2026-10-17; user: "bar counts in the hour before the open instead of rvol? block trades with little activity")

`scripts/equity/gapfader_pm_activity.py` → `data/equity/gapfader/pm_activity.{md,parquet}`. EVERY gap-down ≥ 4 % (open ≥ $1,
dv20_prior ≥ $1M; all premarket volumes: 131,359 trades); active 1s bars and trade count 08:30–09:30; GF-21's exit.
Spearman ρ: bars vs rvol_pm 0.82, bars vs trades 0.99 (the same feature), bars vs dv20_prior 0.45, rvol_pm vs dv20 0.08.
⚠ Knowability: the window ends at the open — for a decision before a 09:25 cutoff, use 08:25–09:25.

Each alone (PF−1 ex-2020; losing years ex-2020):

| rvol_pm | trades / yr | PF−1 | avg % | | active bars 08:30–09:30 | trades / yr | PF−1 | avg % | losing yrs |
|---|---|---|---|---|---|---|---|---|---|
| < 0.005 | 4,626 | **0.89** | +0.98 % | | 0 | 1,059 | **1.44** | +1.44 % | 0/10 |
| 0.005–0.02 | 2,855 | 0.33 | +0.45 % | | 1–9 | 2,880 | 0.65 | +0.80 % | 0/10 |
| 0.02–0.05 | 1,974 | 0.10 | +0.17 % | | 10–29 | 2,095 | 0.35 | +0.49 % | 1/10 |
| 0.05–0.2 | 1,749 | −0.15 | −0.34 % | | 30–99 | 2,468 | 0.13 | +0.22 % | 2/10 |
| ≥ 0.2 | 738 | −0.29 | −0.88 % | | 100–299 | 1,803 | −0.05 | −0.10 % | 7/10 |

**The disagreement** — rvol_pm (rows) × active bars (columns), PF−1 ex-2020 (trades):

| rvol_pm \ bars | 0 | 1–9 | 10–29 | 30–99 | 100–299 | 300–999 | ≥ 1000 |
|---|---|---|---|---|---|---|---|
| < 0.005 | **1.45** (5,847) | 0.84 (15,532) | 0.72 (6,002) | 0.52 (2,567) | 0.49 (370) | · | · |
| 0.005–0.02 | 0.73 (112) | 0.31 (4,113) | 0.38 (7,451) | 0.34 (8,648) | 0.29 (3,914) | 0.10 (1,258) | 0.56 (129) |
| 0.02–0.05 | · | −0.01 (857) | 0.03 (3,170) | 0.08 (6,234) | 0.15 (4,973) | 0.17 (2,856) | 0.21 (816) |
| 0.05–0.2 | · | 0.15 (229) | 0.01 (1,220) | −0.14 (4,246) | −0.20 (5,601) | −0.20 (4,207) | 0.07 (1,770) |
| ≥ 0.2 | · | · | · | −0.11 (723) | −0.33 (2,106) | −0.32 (2,835) | −0.28 (1,676) |

- **The block-trade case (high rvol, few bars) is NOT a good trade**: rvol 0.02–0.05 with 1–9 bars −0.01; 0.05–0.2 with
  1–9 bars 0.15. Volume printed in a few blocks still marks the name as one the premarket has priced.
- **Low rvol with a busy tape stays decent** (rvol < 0.005 with 100–299 bars: 0.49). When the two disagree, rvol wins.
- **Inside the weak premarket, the bars refine**: rvol < 0.005 runs 1.45 → 0.84 → 0.72 → 0.52 as the bars rise.
- The 0-bar cell beats the 1–9-bar cell in all 11 years (rvol < 0.005: 2016 2.19 / 0.82 … 2025 1.04 / 0.75, 2026 3.97 / 1.79)
  and still exists in 2026 (10 % of the rvol < 0.005 trades; was 43 % in 2016).

**Control — liquidity** (bars ρ 0.45 with dv20): rvol_pm < 0.02, ex-2020, PF−1 by dv20_prior × bars:

| dv20 \ bars | 0 | 1–9 | 10–29 | 30–99 | ≥ 100 |
|---|---|---|---|---|---|
| $1–2M | 2.86 (1,543) | 1.16 (3,556) | 1.01 (1,182) | 0.49 (133) | · |
| $2–5M | 1.17 (1,681) | 0.72 (4,834) | 0.65 (2,478) | 0.48 (751) | · |
| $5–10M | 0.84 (1,039) | 0.59 (3,508) | 0.48 (2,267) | 0.54 (1,366) | · |
| $10–25M | 0.96 (1,012) | 0.50 (4,097) | 0.43 (3,066) | 0.43 (2,538) | 0.35 (449) |
| $25–100M | 1.52 (630) | 0.54 (3,222) | 0.36 (3,379) | 0.33 (3,858) | 0.24 (1,579) |
| ≥ $100M | · | 0.85 (428) | 0.32 (1,081) | 0.21 (2,569) | 0.28 (3,558) |

and rvol within a liquidity band (< 0.005 vs 0.005–0.02): $1–2M 1.91 / 0.74 · $2–5M 1.04 / 0.39 · $5–10M 0.84 / 0.29 ·
$10–25M 0.75 / 0.23 · $25–100M 0.53 / 0.30 · ≥ $100M 0.40 / 0.24. **Both survive the control in every row**: liquidity,
rvol_pm and the premarket bars are three grades, all known before the open, none a proxy for another. The trade is a
quiet name in a quiet premarket; the quieter on all three, the bigger the dislocation the cross prints.

### GF-18c — the OPENING AUCTION rules, all four listing markets (2026-10-17; user: "official auction timing")

From the exchanges (nyse.com/trade/auctions, the NYSE American / Arca trading-info pages, the XDP Imbalances spec v2.2c,
nasdaqtrader.com OpenClose, Nasdaq's SR-NASDAQ-2025-105 rule text). ET.

| | Nasdaq | NYSE | NYSE American | NYSE Arca |
|---|---|---|---|---|
| order entry from | 04:00 | 06:30 | 06:30 | 02:30 |
| early open auction | — | — | 07:00 (limit orders only; freeze 06:59) | 04:00 (limit orders only; freeze 03:59) |
| opening imbalance published | **09:25**–09:30, every second | **08:00** until the open, every second | 08:00 (core) | 08:00 (core) |
| MOO entry until | 09:28 | the open (DMM) | 09:29:55 | 09:29:55 |
| LOO entry until | 09:29:30 (later = imbalance-only) | the open (DMM) | 09:29:55 | 09:29:55 |
| MOO / LOO cancel until | 09:28 (2025 text: 09:25) | the open: "orders can be entered and cancelled until the security is opened by the DMM, which may occur after 9:30" | 09:29 | 09:29 |
| freeze | — | — | 09:29:55–09:30 | 09:29:55–09:30 |
| regular orders in the cross | yes ("Open Eligible Interest": SDAY / SGTC / SHEX / GTMC) | yes (all electronic interest, + the DMM) | yes (Limit, Market, MOO, LOO, Primary Peg) | yes (same) |
| the open | 09:30:00 | when the DMM opens it (14.6 % of our crosses ≥ 09:31, GF-24) | 09:30, within the auction collars | 09:30, within the collars; core open for Arca-LISTED names only |

During the Arca / American freeze, MOOs and LOOs are rejected; new limit orders (not early-session eligible) and market
orders are accepted but only offset the remaining imbalance. Arca's core open auction is for Arca-listed securities only
(a UTP name gets only the 04:00 early auction there) — an auction order belongs on the LISTING exchange.

**Reading against the live rejects (§L8 of lightspeed_connect_api.md):** every exchange accepts auction orders far later
than Lightspeed's "09:25". AMEX's LOO R110 ("already opened") from 07:00 = its EARLY open auction — consistent with
Lightspeed stamping the early session on the order (the R042 "Invalid TradingSessionID" theory). NYSE's codes switch at
its 06:30 order-entry start: R084 ("closed") at 05:30–06:00, then R138 ("not open") from 06:30 on — NYSE Tape A HAS no
early session, so an early-session order never finds an open one; NYSE American's LOO goes R138 at 06:30 and R110
("already opened") from 07:00, right after its early open auction. The same theory explains all three codes. NYSE's imbalance from 08:00 + entry/cancel until the DMM opens = 90 minutes
of usable imbalance; Nasdaq's 09:25 publication still leaves 3 min for MOOs and 4.5 for LOOs (5 for regular limits).

## GF-27 — the LISTING exchange: NYSE-listed gaps barely trade (2026-10-17; prompted by "does Massive's NYSE imbalance feed cover AMEX and Arca?")

Massive's NOI feed: "Coverage is NYSE-listed tickers only… NASDAQ-listed names will not appear" (its NOI tracker post; the
docs: "Focused on NYSE listed securities"); fields: imbalance qty `o`, paired qty `p`, book clearing price `b`, auction
type / time, exchange id — no history. NYSE American / Arca coverage is not documented. So, how much of the trade is
NYSE-listed? Listings = Massive's reference API, ACTIVE tickers today (`data/equity/gapfader/listing_2026-10-17.parquet`;
⚠ delisted names unmapped, and a name's listing may have moved over the years). GF-21's exit.

2025-01 … 2026-09 (17,420 trades): Nasdaq 58.2 % (PF−1 0.80), NYSE 27.5 % (**0.12**), NYSE American 3.6 % (0.66),
not active today 10.7 % (0.90).

By liquidity, 2016–2026 ex-2020, PF−1 (trades):

| dv20_prior | Nasdaq | NYSE | NYSE American |
|---|---|---|---|
| $1–5M | 1.25 (7,659) | **0.13** (2,174) | 1.13 (545) |
| $5–25M | 0.69 (8,930) | **0.05** (4,361) | 0.90 (449) |
| $25–100M | 0.58 (5,566) | **0.18** (4,210) | −0.06 (225) |
| ≥ $100M | 0.33 (3,424) | **0.15** (3,330) | 0.54 (85) |
| all | 0.78 (25,579) | **0.12** (14,075) | 0.72 (1,304) |

By year, PF−1 Nasdaq / NYSE: 2016 1.54 / 0.06 · 2017 0.53 / 0.55 · 2018 0.72 / 0.12 · 2019 0.70 / −0.17 · 2020 0.82 / 0.11 ·
2021 0.99 / 0.75 · 2022 0.48 / −0.02 · 2023 0.60 / −0.11 · 2024 1.19 / 0.32 · 2025 0.77 / 0.05 · 2026 0.83 / 0.26 —
Nasdaq ahead in 10 of 11 years (2017 a tie). NYSE American runs with Nasdaq (thin: 1,304 trades).

**Reading.** Not liquidity (it holds in every band). The likely mechanism is HOW the open is made: Nasdaq's and NYSE
American's opens are electronic auctions that print wherever the thin auction book clears; NYSE's is set by the DMM, who
offsets imbalances and is held to price continuity (and opens late — GF-24: 14.6 % of the crosses after 09:31). The DMM
removes exactly the dislocation the trade buys. ⇒ (1) The trade is a Nasdaq + NYSE American trade; dropping NYSE-listed
names removes ~28 % of the trades and nearly none of the profit. (2) **Massive's NYSE-only imbalance feed covers the one
listing where the trade barely works** — not worth buying for this. The data that matters is Nasdaq's NOII (TotalView).

GF-27b — the 10.7 % unmapped, looked up POINT-IN-TIME (`/v3/reference/tickers/{t}?date=<trade date>`, 1,840 trades,
513 tickers, all HTTP 200; `listing_pit_unmapped_2025-26.parquet`): Nasdaq 1,335, NYSE 326, NYSE American 115, Cboe BZX 62,
NYSE Arca 2. All trades 2025-01 … 2026-09 with them: Nasdaq 65.8 % (PF−1 0.83), NYSE 29.4 % (0.11), NYSE American 4.3 %
(0.71), Cboe BZX 0.4 % (62 trades, 1.96), Arca 2 trades. Listings today (active CS + ADRC): Nasdaq 3,534, NYSE 1,920, NYSE
American 240, Cboe BZX 1 (CBOE itself), **NYSE Arca 0** — Arca lists ETPs (2,503 ETFs, 45 ETNs, 90 ETVs), no common stocks.

## GF-28 — the gap-down long on plain ETFs (2026-10-17; user's curiosity — the account's owner cannot trade US ETFs)

`scripts/equity/gapfader_etf.py` → `data/equity/gapfader/etf.{md,parquet}`. Type ETF, gap ≤ −4 %, open ≥ $1, dv20_prior ≥ $1M,
2016-08 … 2026-09; **leveraged / inverse funds removed** (user): by name — 2X / 3X / −1X, Ultra, Leveraged, Inverse, Bear,
Bull, Short (not short-duration bond funds), Roundhill WeeklyPay (1.2×), YieldBOOST, Daily Target — 20,752 of 29,713 gaps
(556 funds); 8,961 gaps left in 1,586 plain funds. GF-21's exit.

| | trades / yr | PF−1 | PF−1 ex-2020 | avg % ex-2020 | median % ex-2020 |
|---|---|---|---|---|---|
| all | 815 | 1.01 | 0.70 | +0.42 % | +0.26 % |
| rvol_pm < 0.005 | 471 | 1.61 | 1.67 | +0.69 % | +0.35 % |
| weak premarket (< 0.02) | 621 | 1.33 | 1.09 | +0.53 % | +0.28 % |
| weak, SPY gap ≤ −1 % | 505 | 1.46 | 1.48 | +0.56 % | +0.42 % |
| weak, SPY gap > −1 % | 116 | 0.90 | 0.71 | +0.47 % | +0.03 % |
| weak, 0 bars 08:30–09:30 | 219 | 2.39 | 3.81 | +1.14 % | +0.73 % |
| weak, ≥ 10 bars | 126 | 0.75 | 0.42 | +0.25 % | +0.15 % |

By listing (weak, ex-2020): Arca 0.89, Nasdaq 0.95, Cboe BZX 1.74 (ETFs have no DMM open on Arca: an electronic auction).

**⚠ It is a crash-day basket, not a stream.** SPY itself gapped ≤ −1 % on 72 % of the gaps; the 6,827 weak-premarket trades
fall on 589 days with a median of ONE a day — the top 5 days (2020-03-16 / 18 / 12 / 09, 2024-08-05) hold 48 % of the
trades and 64 % of the P&L, the top 20 days 74 % / 81 %. A crash morning is one bet on the market's bounce (2020-03-12
PF 0.49, 2026-03-03 0.00, 2025-04-07 246). Without the 20 biggest days: 1,768 trades on 569 days, PF−1 0.81, avg +0.50 %,
median +0.15 %. On days with ≤ 3 such ETF gaps — the fund's OWN dislocation: 625 trades on 443 days (~60 a year),
**PF−1 1.75, avg +1.06 %**. ⇒ The auction effect exists on ETFs too (and grades the same way: rvol, the premarket bars), but
outside crash days it is ~60 trades a year — and the account cannot trade them.

### GF-18d — where to get NASDAQ's imbalance (NOII) (2026-10-17; user: "the cheapest Nasdaq imbalance data besides IBKR"; GF-27: NYSE's is useless to us)

NOII is part of Nasdaq TotalView (no standalone NOII product for individuals); TotalView's exchange fee is ~$15 a month
non-professional, so any vendor's price is that plus its own margin and licensing.

| source | Nasdaq NOII | access | price | catch |
|---|---|---|---|---|
| **IBKR** (TWS API, generic tick 225) | via TotalView-OpenView | TWS / IB Gateway (runs headless on Linux with IBC) | **$16.50 / month** | the gateway; tick 225 for Nasdaq names unverified |
| Webull OpenAPI | an NOII snapshot endpoint (Asian sites: 09:28–09:30 open, 5 s) | plain REST, no gateway | unknown | US OpenAPI needs a Webull US brokerage account; the EU OpenAPI lists TotalView "for Non-Display OpenAPI usage" but no NOII endpoint |
| QuoteMedia API | yes, with a TotalView entitlement | REST + streaming | unknown | aimed at businesses |
| dxFeed | native Order Imbalance event (added 06-2026) | API | contact sales | aimed at market makers / execution algos |
| Databento | XNAS.ITCH carries NOII | API | history cheap; live business-only | — |
| Nasdaq direct (Data Link / NCDS) | as part of TotalView | cloud API | distributor agreements + non-display fees | institutional |
| Massive | **none** (its KB: NYSE only) | — | — | — |

Cadence (Nasdaq's TotalView-ITCH spec): every 10 s 09:25–09:28, every second 09:28–09:30. ⇒ The cheapest settled route
is IBKR — one month ($16.50) on the existing account answers whether tick 225 carries Nasdaq's NOII before 09:28.
⚠ Licensing: driving an automated system is NON-DISPLAY use, which exchanges license separately; IBKR's line is priced
for display — to ask IBKR (Webull's EU line is the only one that says non-display explicitly).

**Licensing (Nasdaq Equity 7, SR-NASDAQ-2024-076 Exhibit 5, "Enhanced Display Solution"):** an individual cannot license
TotalView directly (Databento's TotalView-live post: "Nasdaq doesn't permit non-professional or individual users to start a
license" — its live XNAS.ITCH is for business entities). A DISTRIBUTOR pays Nasdaq $4,390 a month (2026) for the right to
offer TotalView "with access to an API or similar solution"; its Professional subscribers then pay $88.00 / month, its
Non-Professional subscribers "the applicable Nasdaq Level 2 or Nasdaq TotalView rate" (~$15). ⇒ The way in is a broker's or
vendor's API product with TotalView. Candidates:

| | API | login | TotalView via the API | NOII through it | an EU resident's account |
|---|---|---|---|---|---|
| IBKR | TWS API | IB Gateway (a GUI; IBC + a virtual display automates it, 2FA ~weekly) | yes ($16.50) | tick 225, unverified for Nasdaq | yes (the user has one) |
| moomoo OpenAPI | OpenD | a command-line build for Linux, account + password in OpenD.xml | the API's US quotes: Nasdaq Basic + TotalView (60 levels) | unconfirmed | **❌ no EU residents** (moomoo's own list 2026: US, CA, SG, AU, JP, MY, NZ; the UK / EU "future expansion") |
| Webull OpenAPI | REST | API keys | yes (EU: "for Non-Display OpenAPI usage") | US / Asia only (snapshot endpoint) | EU API lacks the NOII endpoint |
| TradeStation WebAPI | REST / HTTP streaming | OAuth | yes (Level 2 / TotalView depth) | unconfirmed | unknown |

(Retracted 2026-10-17: a "Lightspeed gateway ITCH spec" row. The file at download.lightspeed.com/GatewaySpecs/ is a 2006
copy of Nasdaq's own TotalView-ITCH 3.0 document — no evidence of a Lightspeed data API; LS Connect offers no data.)

**IBKR, cycling the subscriptions** (user: "iterate through the whole list by resubscribing"): the TWS API's ~50 messages a
second = ~25 symbol swaps a second (cancel + subscribe). Watch lists at 09:25 on GF-19's 10 days (Nasdaq + NYSE American,
the trade there = 23.7 a day):

| watch list | names a day | trades in it | one pass at 25 / s |
|---|---|---|---|
| every name (prev close ≥ $1, dv20 ≥ $1M) | 2,094 | 100 % | ~84 s |
| + weak premarket to 09:25 | 1,914 | 100 % | ~77 s |
| + ask ≤ prev close | 264 | 80 % | ~11 s |
| + ask ≤ −1 % | 97 | 68 % | ~4 s |

At the 100-line allowance the hold per symbol decides it: if a new subscription delivers the CURRENT auction values at
once, the whole list cycles ~2× between 09:25 and 09:28 and every ~80 s after; if it must wait for the next NOII update
(10 s to 09:28, 1 s after), 2,000 names = 20 batches of 100 = ~200 s before 09:28, then 20–40 s a pass to the 09:29:30 LOO
cutoff. The "ask ≤ prev close" list (264, 80 % of the trades) fits 3 batches either way. To verify on a trial month: tick
225 for Nasdaq names; the values on subscription; the pacing.

**IBKR's Web API is not a route** (checked 2026-10-17): (1) its market-data field table (docs/web-api/v1/endpoints/
market-data/market-data-fields.md, 110 fields) has NO auction or imbalance field — last / bid / ask / sizes, volume, open,
prior close, shortable shares, fee rate, EMAs, events …, nothing from tick 225; (2) a retail account authenticates only
through the Client Portal Gateway (IBKR, Aug 2025: "retail clients are currently only approved to access the Web API using
the Client Portal Gateway"; OAuth 1.0a is institutional, OAuth 2.0 for individuals "being considered"). The auction ticks
exist only in the TWS API (IB Gateway / TWS). With the user's watch list (weak premarket, sorted by the 09:25 ask's distance
from the prev close) the top 100 fit the default 100 lines without cycling: ask ≤ −1 % = 97 names a day, 68 % of the trades.

## GF-29 — the gap-up continuation: can DOLLAR VOLUME replace the TRADE COUNT? And the calm tape by gap × premarket volume (2026-10-19; user: "using trade count doesn't sit well with me")

`scripts/equity/gapfader_tape_dv.py` → `data/equity/gapfader/tape_dv.md`. GF-10/GF-11's trade: gap-up ≥ 4 %, rvol_pm ≥ 0.2,
held the open at 10:00, bought then, out at the close (5,922 trades). Tape over 09:30–10:00: trades, active 1s bars, dollar
volume (share volume × the 10:00 price), rvol30 (volume / avgvol20_prior). In-sample curiosity; quintile bands.

Spearman ρ: trades vs dollar volume 0.83, trades vs active bars 0.90, trades vs rvol30 0.43, dollar volume vs volat_clk −0.01.

| quintile Q1 → Q5 (rvol_pm ≥ 0.2) | PF clip +50 % | median % |
|---|---|---|
| **trades30** | **1.13 / 1.06 / 0.91 / 0.73 / 0.51** (monotone) | −0.29 / −0.00 / −0.46 / −1.60 / −4.19 |
| dollar volume | 1.08 / 0.87 / 0.76 / 0.66 / 0.62 | −0.65 / −1.16 / −1.08 / −1.42 / −0.31 |
| active bars | 0.91 / 1.00 / 0.68 / 0.83 / 0.60 | −0.19 / −0.08 / −1.51 / −1.15 / −3.03 |
| rvol30 | 0.80 / 0.86 / 1.13 / 0.75 / 0.55 | −0.97 / −0.58 / +0.04 / −0.87 / −3.30 |

**The substitution test** (median splits: 15.2k trades, $29.4M):

| | n | median % | PF clip +50 % | net PF | years < 1 |
|---|---|---|---|---|---|
| few trades, large $ | 534 | **+0.08** | **1.18** | 1.27 | 5/11 |
| few trades, small $ | 2,425 | −0.56 | 1.03 | 1.21 | 4/11 |
| many trades, small $ | 534 | **−3.91** | 0.71 | 1.07 | 9/11 |
| many trades, large $ | 2,426 | −1.80 | 0.63 | 0.76 | 6/11 |

- **No: the trade count carries the information, the dollar volume does not.** At the same dollar volume, more trades is
  worse; at the same trade count, more dollars is a little better.
- **The average trade size ($ per trade) is not the clean version:** ρ 0.89 with the share price. Its medians rise with
  it (−3.2 % → +0.1 %), but its PF is not monotone, and inside rvol_pm 1–5 it inverts.
- Reading: the count is the number of separate decisions to trade. A held gap-up chased by thousands of small prints is
  being distributed into (GF-10's "busy tape = distribution"); volume measures size, the edge is about the crowd.

**The calm tape (volat_clk < 40 bp) by gap × rvol_pm**, net PF (0.7c) [n]:

| gap \ rvol_pm | 0.2–0.5 | 0.5–1 | 1–2 | 2–5 | ≥ 5 | all |
|---|---|---|---|---|---|---|
| 4–6 % | 1.44 [241] | 1.62 [40] | 2.08 [26] | · [11] | · [7] | 1.47 [325] |
| 6–10 % | 1.18 [333] | 0.99 [59] | 6.70 [33] | 1.80 [20] | · [10] | 1.26 [455] |
| 10–20 % | 1.13 [334] | 1.22 [83] | 2.11 [41] | 11.77 [27] | · [19] | 1.31 [504] |
| ≥ 20 % | 0.75 [76] | 1.83 [63] | 1.31 [45] | 0.81 [55] | 3.44 [75] | 1.22 [314] |

Control, the WILD tape (≥ 80 bp), by gap: 2.62 [71] / 0.84 [189] / 0.62 [597] / 0.84 [1,364].

- **The calm tape works at every gap size** (1.22–1.47 by gap). The wild tape loses everywhere except a 71-trade cell.
- Premarket volume adds no clean structure on top. The big cells (6.70, 11.77) sit on 27–33 trades: noise, not a
  finding. The calm × big gap × light premarket corner (≥ 20 %, rvol 0.2–0.5) is the one calm cell below 1 (0.75, 76 trades).
- ⏭ The user: the early-day volatility finding goes into the swing momentum system later.

## GF-30 — the gap-up INTRADAY SWING by tape COMBINATIONS: calm × few trades × volume persistence (2026-10-19; user: "low vol, low trade count, but high-volume persistence — a big player slowly accumulating?")

`scripts/equity/gapfader_swing_combo.py` → `data/equity/gapfader/swing_combo.md`. GF-10's sample (gap-up ≥ 4 %, rvol_pm
≥ 0.2), held the open at 10:00, bought then, out at the close: 5,922 trades. Cuts FIXED in advance: volat_clk < 40 bp
(GF-11), trades30 terciles 7.7k / 28.6k (median 15.2k), volume persistence = volume 09:45–10:00 / 09:30–09:45, terciles
0.49 / 0.79 (median 0.61: the volume normally decays). Net = 0.7c / share. ρ: volat–trades 0.41, volat–persistence 0.35.

| volatility × trades (net PF) | few (T1) | mid (T2) | many (T3) |
|---|---|---|---|
| calm < 40 bp | 1.33 [751] | 1.39 [548] | 1.20 [299] |
| not calm | 1.12 [1,222] | 1.01 [1,425] | **0.81** [1,674], 9/11 losing years |

| calm · few trades (< median), by volume persistence | n / yr | median % | net PF | 2016–20 / 2021–26 | years < 1 |
|---|---|---|---|---|---|
| all | 95 | +0.07 | 1.33 | 1.63 / 1.21 | 2/11 |
| **decaying (< median)** | 62 | +0.12 | **1.45** | **1.49 / 1.43** | 2/11 |
| PERSISTENT (≥ median) — the user's cell | 33 | +0.02 | 1.12 | 1.88 / **0.85** | 3/11 |
| did not decay at all (≥ 1) | 8 | +0.12 | 1.48 | 3.68 / 0.87 | 5/11 |

- **Calm dominates.** The trade count matters mostly OUTSIDE the calm tape (1.12 → 0.81); inside it, little (1.33 / 1.39 /
  1.20). The two overlap (ρ 0.41) rather than multiply.
- **Volume persistence works the OTHER way:** across all held gap-ups the persistent tercile is the worst (0.87); inside
  calm · few trades the persistent half fades after 2020 (0.85; the user's cell by year 0.51–3.24). Volume that keeps
  coming after the open is distribution more often than accumulation (GF-10's "rising persistence" again).
- **The stable combination: calm · few trades · decaying volume**, net PF 1.45 with both halves ≈ equal (1.49 / 1.43), 2/11
  losing years, ~62 trades a year: modestly above calm alone (1.32). ⚠ Read off the complement of the tested cell: it
  needs a holdout before it is a feature.
- Premarket volume: this sample holds only rvol_pm ≥ 0.2. The weak-premarket gap-ups (the GF-2 fade side) were never
  built for this trade (their tape features need a pass over the 1s bars).
**BAR COUNTS on the calm tape** (user: "try bar counts"; act30 = active 1s bars 09:30–10:00, calm terciles 750 / 1,391):

| calm tape | n / yr | net PF | 2016–20 / 2021–26 | years < 1 |
|---|---|---|---|---|
| few / mid / many bars | 48 each | 1.38 / 1.28 / 1.30 | 1.75 / 1.26 · 1.71 / 1.07 · 1.42 / 1.25 | 2 / 3 / 3 of 11 |
| bars × trades disagree (few bars · many trades / many bars · few trades) | 6 / 6 | 1.34 / 1.03 | | |
| **activity FADING** (active bars 09:45–10:00 / 09:30–09:45, lowest tercile < 0.82) | 48 | **1.53** | **1.58 / 1.51** | 2/11 |
| activity middle (0.82–0.97) / persisting (≥ 0.97) | 48 / 48 | 1.10 / 1.33 | 1.17 / 1.06 · 2.30 / 1.07 | 4 / 4 of 11 |

- On the calm tape the bar count IS the trade count (ρ 0.97); its level barely grades the trade.
- **Its PERSISTENCE repeats the volume result**: activity that FADES after the open is the best (1.53, both halves
  ≈ 1.5). Two measures now agree: a gap-up holding its open on a calm tape while the trading dies down is the best
  intraday swing. The gap was repricing, not a frenzy. Not monotone (the middle tercile is the weakest), in-sample cuts.
- ⏭ Better than more slicing: a FlushFader-style PF−1 model with volat, log(trades) and the volume / activity persistence
  as continuous terms, scored on the year-block holdouts.

## GF-31 — a CONTINUOUS PF−1 model for the gap-up swing: it LOSES to the simple cells (2026-10-19; user: "a continuous model, and throw in the slope")

`scripts/equity/gapfader_swing_model.py` → `data/equity/gapfader/swing_model.md`; the slope from
`scripts/equity/gapfader_slope30.py` (OLS of ln vwap over the 09:30–10:00 1s bars, bp/min; 22 s for 15,302 gap-ups).
FlushFaderV2's machinery (logit p, log W, log L), every term continuous and standardised on the fit set: ln volat, ln(1 +
trades), ln volume / activity persistence, the slope, the open → 10:00 return, ln gap, ln rvol_pm, ln dv20. Net 0.7c. Half
holdouts: fit 2016–20 (1,407 trades) → apply 2021–26 (4,515), and the mirror.

| model | sized PF−1, fwd / mirror (mean) | top third by prediction: net PF fwd / mirror |
|---|---|---|
| flat | −0.075 / 0.017 (−0.029) | 0.92 / 1.02 |
| volatility | −0.003 / 0.344 (**0.170**) | 1.04 / **0.78** |
| + trades | 0.007 / 0.085 (0.046) | 1.08 / 0.93 |
| + volume / activity persistence | 0.022 / 0.014 (0.018) | 1.11 / 1.17 |
| + slope, ret30 | 0.035 / −0.047 (−0.006) | 1.14 / 1.18 |
| + gap, rvol_pm, dv20 (all) | 0.050 / 0.011 (0.031) | 1.37 / 1.00 |
| slope alone | 0.031 / −0.005 (0.013) | 1.04 / **0.77** |
| all but the slope terms | 0.066 / 0.093 (0.080) | 1.21 / 0.89 |

- **No model is stable across both holdouts**; the simple rule (calm + activity fading, GF-30: 1.58 / 1.51) is better.
- **Why: the effects are THRESHOLDS, not log-linear.** The full fit per SD of ln volat: odds ×0.79, W ×2.89, L ×2.17, so
  λ = L/W ×0.75 and the model's PF−1 RISES slightly with volatility. The buckets say the reverse (calm 1.22–1.47, wild
  0.62–0.84): a few huge wins on wild tapes inflate W, and a line in ln volat cannot express "fine below 40 bp,
  collapsing above 80". (FlushFader's volatility was the opposite case: a smooth power law, V2-14.)
- **The slope adds nothing**: alone 0.77 on one holdout; it restates ret30 (ρ 0.69) and the volume persistence (0.61).
- ⏭ The next model: volatility as BANDS or a hinge at ~40 bp; winsorised returns for the W fit; then the persistence
  terms. The trade stays an APPETIZER candidate (user): calm tape + fading activity, net PF ~1.5.

## GF-32 — the swing rule on GAP-DOWNS that held their open: NO edge (2026-10-19; user: "would gap-downs that hold the open be positive as well?")

`scripts/equity/gapfader_gapdown_swing.py` → `data/equity/gapfader/gapdown_swing.md`; the gap-down tape cached in
`data/equity/gapfader/gapdown_tape.parquet` (131,358 gap-downs ≤ −4 %, all premarket volumes; GF-10/11's definitions; 63 s).
Held the open at 10:00 (the last vwap before 10:00 ≥ the open), 10:00 → close, net 0.7c; the gap-up rule's cuts
unchanged (calm < 40 bp, activity fading < 0.82). 70,814 held gap-downs.

| held gap-downs | n / yr | net PF | 2016–20 / 2021–26 | years < 1 |
|---|---|---|---|---|
| all | 6,438 | 0.85 | 0.61 / 1.01 | 7/11 |
| calm | 3,414 | 0.98 | 0.73 / 1.18 | 5/11 |
| **calm · activity fading** | 1,298 | **0.99** | 0.90 / 1.05 | 6/11 |
| wild (≥ 80 bp) | 1,036 | **0.70** | 0.45 / 0.83 | **11/11** |

By rvol_pm (calm · fading): < 0.02 0.97 · 0.02–0.2 1.05 (7/11 losing years) · 0.2–1 0.85 · ≥ 1 0.86. By gap: −4…−6 % 1.05
· −6…−10 % 0.94 · −10…−20 % 0.82 · ≤ −20 % 0.98.

**Like for like with the gap-up sample (rvol_pm ≥ 0.2; user: "were you testing on high premarket volume?")** — worse:
all 3,660 held: net PF 0.81 (9/11 losing years) · calm 0.89 · **calm · fading 0.85** (760; 1.07 / 0.74) · wild 0.72.
Consistent with GF-4: heavy-premarket gap-downs CONTINUE (their short had PF 1.15–1.80), and holding the open until
10:00 does not change that. (59 % of the held gap-downs are weak-premarket, rvol_pm < 0.02.)

- **The rule does not carry over.** Volatility still SORTS (wild tapes lose in all 11 years), but only as a loser filter:
  the calm cells reach break-even, not an edge.
- Reading: the gap-down's edge is spent by 10:00. GF-12/13's trade buys the cross and sells by 10:00 (to the close PF
  1.02), so a gap-down still above its open at 10:00 has already reverted. A gap-up holding quietly is the market
  ACCEPTING the new price, and it continues. **The calm-tape swing is a gap-UP trade only**: the appetizer beside the
  gap-down open trade, not its mirror.

### GF-33 — the gap-up when the open FAILS (2026-10-20; user: "maybe the hold doesn't matter")

The user's hypothesis was that a gap-up fades in the first 30 min and then trends up, so a long from 10:00 (or the open) to
the close might pay whether or not the open held. Tested on GF-10's sample: gap-up ≥ 4 %, rvol_pm ≥ 0.2, headline
universe, 15,298 trades. Net = 0.7c / share. Script: `scripts/equity/gapfader_gapup_failed.py` → `data/equity/gapfader/gapup_failed.md`.

10:00 → close (tradeable):

| cell | n / yr | median % | net PF | net 2016–20 / 2021–26 | years < 1 |
|---|---|---|---|---|---|
| held · calm (GF-11) | 145 | +0.11 | **1.32** | 1.61 / 1.20 | 2/11 |
| held · calm · fading (GF-30) | 47 | +0.09 | **1.56** | 1.54 / 1.57 | 3/11 |
| **failed · calm** | 215 | −0.01 | **1.07** | 1.27 / 0.98 | 5/11 |
| failed · calm · fading | 102 | −0.07 | 1.06 | 1.13 / 1.02 | 6/11 |
| failed · not calm | 637 | −1.99 | 0.76 | 0.52 / 0.84 | 11/11 |
| failed · wild (≥ 80 bp) | 275 | −4.33 | 0.68 | 0.42 / 0.76 | 11/11 |

Failed · calm by year (net PF): 2016 0.39 · 2017 1.23 · 2018 1.31 · 2019 0.98 · 2020 1.60 · 2021 1.05 · 2022 0.85 ·
2023 1.16 · 2024 1.51 · 2025 0.65 · 2026 0.95.

Pockets inside failed · calm, all thin and in-sample:

| pocket | n | net PF | years < 1 |
|---|---|---|---|
| rvol_pm ≥ 5 | 148 | 1.53 | 2/11 |
| gap 6–10 % | 743 | 1.23 | 4/11 |
| 0–2 % below the open at 10:00 | 1,237 | 1.11 | 4/11 |

The pockets that go the other way:

| pocket | net PF |
|---|---|
| gap 4–6 % | 0.77 |
| rvol_pm 1–5 | 0.84 |
| 5–10 % below the open | 0.95 |

From the open (⚠ the split by 10:00's tape is NOT tradeable; it only describes the path):

| cell | net PF |
|---|---|
| all gap-ups, open → close | 0.74 (11/11 losing years) |
| calm days | 0.80 |
| calm · held | 4.99 |
| calm · failed | 0.27 |

**Verdict: the hypothesis fails. The hold matters.**
- A calm tape alone doesn't make the long work: failed · calm is about breakeven, 1.07 with 5/11 losing years, against
  held · calm's 1.32.
- The calm + fading rule doesn't carry over to failed opens (1.06).
- Buying at the open loses even on the days that turn out calm (0.80). The first 30 min's fade is real, and it's not
  "played out".
- A calm day is only a good long once the open has held. The swing stays as GF-30: held · calm · fading.

### GF-34 — the 09:30–10:00 volume profile, and held at 10:00 → failed at 10:30 (2026-10-20, user)

Same sample as GF-33: gap-up ≥ 4 %, rvol_pm ≥ 0.2, out at the close, net 0.7c / share. Script:
`scripts/equity/gapfader_profile30.py` → `data/equity/gapfader/profile30.md`, with the cache `profile30.parquet`.

**(A) Where 10:00 sits in the first 30 min's volume profile.** `prof_pct` = the share of 09:30–10:00 volume traded below
the 10:00 price. The control is the plain range position (ρ 0.93 with it).

| prof_pct band at 10:00 | all | calm | not calm |
|---|---|---|---|
| 0.00–0.10 (a breakdown) | 0.74 | 1.24 | 0.71 |
| 0.10–0.25 | 0.84 | 1.09 | 0.82 |
| 0.25–0.50 | 0.77 | 1.10 | 0.75 |
| 0.50–0.75 | 0.97 | 1.17 | 0.95 |
| 0.75–0.90 | 0.91 | 1.21 | 0.89 |
| 0.90–1.00 (a breakout) | 1.01 | 1.15 | 1.00 |

- **Breakouts are only less bad.** On all and not-calm tapes the top of the profile is breakeven (1.00–1.05) and the
  bottom loses (0.71–0.74).
- On a calm tape the profile is flat (1.09–1.24): it adds nothing beyond "calm".
- The range position tells the same story.
- Inside calm · held, being at the TOP of the profile is the weakest band (1.21). The middle bands are the best: 1.61 at
  0.25–0.50 and 1.41 at 0.50–0.75. That is against the ORB reading.
- Inside calm · failed, only the deepest band holds up (0–0.25: 1.16). Every other band is about 0.95.

**(B) Held at 10:00, then the open checked again at 10:30; bought at 10:30.**

| calm (09:30–10:00) | n / yr | median % | net PF | net 2016–20 / 2021–26 | years < 1 |
|---|---|---|---|---|---|
| held 10:00 · held 10:30 | 119 | +0.18 | **1.38** | 1.51 / 1.32 | 2/11 |
| **held 10:00 · FAILED 10:30** | 27 | +0.03 | **1.67** (clip 1.78 = raw) | 1.82 / 1.62 | **0/11** |
| failed 10:00 · reclaimed 10:30 | 28 | +0.10 | 1.30 | 1.15 / 1.36 | 3/11 |
| failed 10:00 · failed 10:30 | 187 | +0.02 | 1.18 | 1.45 / 1.07 | 5/11 |

- The held-then-failed cell's yearly net PF is 1.04–2.43, every year above 1.
- It is a **dip below the open after a calm hold**: median 0.67 % below the open at 10:30, p90 2.6 %.
- Without the calm filter the same cell is tail-driven: 1.27 raw, 0.96 clipped.
- Bought at 10:00 instead, those days lose: calm · held 10:00 · failed 10:30 is 0.49, losing in 11/11 years. The 10:00 → 10:30
  drop is the loss, and the recovery after 10:30 is the gain.

⇒ **Delaying the calm swing's entry to 10:30 helps:**

| trade | buy at 10:00 | buy at 10:30 |
|---|---|---|
| calm · held | 1.32 (2/11 losing) | **1.44** (1/11 losing) |
| calm · held · fading | 1.56 (3/11 losing) | **1.70** (1/11 losing) |

The calm and fading tests are still measured at 10:00.

The cells were found by splitting the same sample. A holdout and a time control (10:15 / 10:45) are needed before the
10:30 entry is called a feature.

### GF-35 — the VWAP instead of the open, and the volume-weighted z-score (2026-10-20, user)

Same sample as GF-33/34. VWAP = the RTH VWAP from 09:30 (the 1s bars' volume at their vwaps). z = (price − VWAP) / sd, with
sd = sqrt(Σ vol·(v − VWAP)² / Σ vol), where the volume is the count. At 10:00 the window is 09:30–10:00; at 10:30 it is
09:30–10:30. Script: `scripts/equity/gapfader_vwap_z.py` → `data/equity/gapfader/vwap_z.md`, with the cache `vwap_z.parquet`.
At 10:00 the price is above the VWAP for 35 % of trades and above the open for 39 %; the two agree 82 % of the time.
Median z30 is −0.53 (p10 −1.66, p90 +1.24).

**The open is the better reference.** 10:00 → close, calm:

| | above | below |
|---|---|---|
| the open | **1.32** (2/11 losing) | 1.07 (5/11) |
| the VWAP | 1.22 (2/11) | 1.11 (5/11) |
| calm · fading, the open | **1.56** | 1.06 |
| calm · fading, the VWAP | 1.45 | 1.08 |

The two references crossed, calm, 10:00 → close:

| cell | n / yr | net PF | net 2016–20 / 2021–26 | losing years |
|---|---|---|---|---|
| above the open · above the VWAP | 116 | 1.28 | 1.57 / 1.15 | 3/11 |
| **above the open · BELOW the VWAP** | 29 | **1.48** | 1.82 / 1.38 | 2/11 |
| below the open · above the VWAP | 38 | 1.08 | 1.17 / 1.05 | 3/11 |
| below the open · below the VWAP | 178 | 1.06 | 1.29 / 0.97 | 6/11 |

**Held the open, but pulled back under the VWAP** is the best cell. It's the same mechanism as GF-34 (B): a dip inside a day that held.

At 10:30, bought at 10:30, calm: held at 10:00 → below at 10:30 is **1.67** with the open as reference (0/11 losing years),
against 1.45 with the VWAP (2/11). The open stays the better reference.

**The z-score has no clean gradient.** 10:00 → close, net PF by band:

| z band at 10:00 | < −2 | −2…−1 | −1…−0.5 | −0.5…0 | 0…0.5 | 0.5…1 | 1…2 | ≥ 2 |
|---|---|---|---|---|---|---|---|---|
| not calm | 0.73 | 0.70 | 0.80 | 0.86 | 0.86 | 0.95 | 1.02 | 0.80 |
| calm | 1.77 (n 157) | 1.08 | 1.16 | 0.94 | 1.21 | 1.41 | 1.09 | 1.40 (n 125) |

| z band at 10:30, calm, bought 10:30 | < −2 | −2…−1 | −1…−0.5 | −0.5…0 | 0…0.5 | 0.5…1 | 1…2 | ≥ 2 |
|---|---|---|---|---|---|---|---|---|
| net PF | 1.64 | 1.31 | 1.07 | 1.26 | 0.97 | 1.33 | 1.58 | 1.94 (n 119) |

- Not calm: the further below the VWAP, the worse, but no band is positive. Above z 2 it is overextended (0.80).
- Calm: a U-shape. Both extremes are good, but they are thin (n 120–160, 2–4/11 losing years) and the middle is noise. That
  is what split bands look like when the base cell (calm) is positive and the bands are small.
- Inside calm · held at 10:00 the 10:30 bands have n 42–378 and jump around (1.13–2.18): too thin to read.

**Verdict:** keep the open as the reference. The z-score adds no monotone grading on top of calm + held. The one consistent
mechanism across GF-34 and GF-35 is **buying the dip in a calm gap-up that held its open**: below the VWAP at 10:00, or below
the open at 10:30.

### GF-36 — held the open at 09:45 AND 10:00 vs at 10:00 alone (2026-10-20, user)

Same sample. px_15 = the last 1s vwap before 09:45 (cache `px15.parquet`). Script: `scripts/equity/gapfader_hold945.py` →
`data/equity/gapfader/hold945.md`. **80 % of the 10:00 holders also held at 09:45** (calm too), so the extra check can only
move a fifth of the trades.

| 10:00 → close, net PF (losing years) | held 10:00 | held both | failed 09:45 · held 10:00 | held 09:45 · failed 10:00 |
|---|---|---|---|---|
| all | 0.94 (7/11) | 0.86 (8/11) | 1.31 (4/11; clip 0.97, tail-driven) | 0.74 (9/11) |
| calm | 1.32 (2/11) | **1.33** (2/11) | 1.30 (4/11; 2.18 / 0.98 by half) | 1.13 (5/11) |
| calm · fading | 1.56 (3/11) | **1.58** (4/11) | 1.49 (n 89) | 1.03 |

Bought at 10:30 instead, calm: held 10:00 1.44 · held both 1.39 · reclaimed 1.61 · held 09:45 → failed 10:00 1.44.

**Verdict: no gain.** Holding at 09:45 as well changes nothing on a calm tape: 1.33 vs 1.32, and 1.58 vs 1.56 with fading.
The ones that failed at 09:45 and reclaimed the open by 10:00 are as good in 2016–20, but weaker since 2021 (0.98, with 2022–25
all below 1). The single 10:00 check stays.

### GF-37 — held at 10:00, bought at the next 30-minute low (2026-10-20, user)

Same sample, held at 10:00. The trigger is the first 1s bar after 10:00 (before 15:30) whose vwap is below either (a) the
previous 1,800 s' lowest vwap (a rolling 30-min low) or (b) the 09:30–10:00 low (the opening-range low). Entry = the next
1s bar's vwap; out at the close. Script: `scripts/equity/gapfader_dip30.py` → `data/equity/gapfader/dip30.md`, with the cache
`dip30.parquet`.

| calm · held, net PF (losing years) | trades / yr | net PF |
|---|---|---|
| bought at 10:00 | 145 | 1.32 (2/11) |
| bought at 10:30 | 145 | **1.44** (1/11) |
| (a) rolling 30-min low (fires on 100 %, median 10:37) | 145 | 1.37 (1/11) |
| (b) below the opening-range low (fires on 34 %, median 10:50) | 50 | **1.54** (2/11) |
| (b)'s days bought at 10:00 | 50 | 0.23 (11/11) |
| (b)'s non-trigger days bought at 10:00 | 95 | 4.95 (0/11) |

With calm · fading: 10:00 1.56 · 10:30 1.70 · (a) 1.55 · (b) 1.60 (19 / yr, 5/11 losing years).

- **(a) is just a delay.** A rolling 30-min low almost always prints within minutes after 10:00. It does no better than
  buying at 10:30.
- **(b) is a selection, not a better entry.** It buys only the third of the days that break the opening-range low (median
  2.2 % below the 10:00 price; p10 −5.2 %), and those days lose if bought at 10:00 (0.23). It skips the two thirds that never
  look back, which are the big winners (4.95).
- Per trade it's better (mean +0.51 % vs +0.37 %), but the total is smaller. With $1 per trade, the sum of net % is:

  | entry | total |
  |---|---|
  | 10:00 | 597 |
  | 10:30 | 693 |
  | (b) alone | 279 |
  | **10:00 + a second unit at (b)** | **875** |

- Without the calm filter, (b) depends on a few big winners: 1.20 raw, 0.99 clipped.
- (b) by trigger time: 10:00–10:30 1.51 · 10:30–11:30 2.37 (n 157) · 11:30–13:30 0.98 · 13:30–15:30 0.86. Late breaks of the
  low don't recover by the close.

⇒ **The opening-range low works as an ADD, not as a replacement:** buy at 10:00 (or 10:30), and add on a break of the
09:30–10:00 low before ~11:30. That is a scale-in in a calm, held gap-up. It still needs a holdout.

### GF-38 — the calm gap-up swing at every premarket volume (2026-10-20, user)

All gap-ups ≥ 4 % (153,513), headline universe, held at 10:00. One pass for everything, cached in `swing_allpm.parquet`.
Script: `scripts/equity/gapfader_swing_allpm.py` → `data/equity/gapfader/swing_allpm.md`. **Control:** the rvol_pm ≥ 0.2 rows
reproduce GF-30 / 34 / 37 exactly (1.32 / 1.44 / 1.54 / 1.56 / 1.70).

| net PF (losing years) | rvol_pm < 0.02 | 0.02–0.05 | 0.05–0.2 | **≥ 0.2** |
|---|---|---|---|---|
| held gap-ups / yr | 3,104 | 1,003 | 941 | 538 |
| calm share of held | 69 % | 65 % | 51 % | 27 % |
| calm · bought 10:00 | 0.98 (4/11) | 1.09 (3/11) | 1.13 (2/11) | **1.32** (2/11) |
| calm · bought 10:30 | 1.09 (4/11; 2021–26 0.88) | 1.11 (4/11) | 1.14 (5/11) | **1.44** (1/11) |
| calm · at the OR-low break | 1.06 (6/11) | 1.15 (2/11) | 1.06 (5/11) | **1.54** (2/11) |
| calm · fading · bought 10:00 | 0.98 (7/11) | 1.00 (4/11) | 1.05 (7/11) | **1.56** (3/11) |
| calm · fading · bought 10:30 | 1.04 (8/11) | 0.99 (8/11) | 1.07 (5/11) | **1.70** (1/11) |

- **The edge does NOT carry below rvol_pm 0.2.**
  - Below 0.02 it is gone: 0.98 at 10:00. The 1.09 at 10:30 comes from 2016 and 2020, with 2021–26 at 0.88 and 2025 at 0.47.
  - At 0.02–0.2 it is thin and positive: 1.09–1.14, 2021–26 1.04–1.06.
- **The grade is premarket volume:** calm · 10:00 rises with it: 0.98 → 1.09 → 1.13 → 1.32. GF-26 already found the same
  ordering for the open trade.
- The refinements found on rvol_pm ≥ 0.2 do not transfer: activity fading, the 10:30 delay and the OR-low entry each add
  0–0.04 below 0.2. They are features of the heavy-premarket gap-up only.
- ⚠ 2020 has 7,314 calm trades below rvol_pm 0.02 (other years 400–3,400). This is not a data gap: in March–June 2020 there
  were 4.5–8.3k gap-ups ≥ 4 % a month, during the COVID market-wide gaps, when a stock's own premarket volume is small
  relative to the move.

### GF-39 — the calm gap-up swing by 09:30–10:00 relative volume (2026-10-20, user)

rvol30 = volume 09:30–10:00 / avgvol20_prior (the census's `rvol_30m`, checked by recomputation). Sample: GF-38's held
gap-ups, every rvol_pm (65,449). Script: `scripts/equity/gapfader_rvol30.py` → `data/equity/gapfader/rvol30.md`.

Correlations: ρ(rvol30, rvol_pm) = 0.66; ρ(rvol30, activity persistence) = −0.07, so heavy volume and fading activity
coexist freely, as the user suspected; ρ(rvol30, volat_clk) = 0.17.

| calm, 10:00 → close, net PF | rvol30 < 0.1 | 0.1–0.2 | 0.2–0.5 | 0.5–1 | **≥ 1** |
|---|---|---|---|---|---|
| calm | 1.04 | 1.00 | 1.02 | 1.07 | **1.29** (1/11 losing) |
| calm · fading | 1.21 | 0.94 | 0.94 | 1.04 | **1.43** (1/11) |
| calm · not fading | 1.00 | 1.02 | 1.05 | 1.09 | 1.23 (1/11) |

**Only rvol30 ≥ 1 matters.** That's a first half hour that traded a whole average day's volume. Crossed with the premarket
(calm, rvol30 ≥ 1):

| rvol_pm | all: n / yr, 10:00 / 10:30, losing years | fading: n / yr, 10:00 / 10:30, losing years |
|---|---|---|
| < 0.02 | 33 / yr, 1.03 / 1.11, 6/11 | 13 / yr, 0.81 / 0.88 |
| 0.02–0.2 | **132 / yr, 1.24 / 1.22, 1/11** | 42 / yr, 1.38 / 1.32, 3/11 |
| ≥ 0.2 | **102 / yr, 1.53 / 1.55, 2/11** | **31 / yr, 1.91 / 1.97, 2/11** |

- **Inside the heavy-premarket cell, rvol30 is the grade.** Calm · held · rvol_pm ≥ 0.2 (1.32 overall) splits into rvol30 ≥ 1 at
  1.53 (1,120 trades) and rvol30 0.5–1 at 1.02 (328). Below 0.5 there are almost none.
- **rvol30 ≥ 1 opens the 0.02–0.2 premarket band:** 1.24 with 1/11 losing years, 132 / yr. Without it that band is 1.09–1.13.
- **Below rvol_pm 0.02 nothing works,** even at rvol30 ≥ 1.
- Fading adds on top: 1.43 vs 1.23 at rvol30 ≥ 1, and 1.91 inside rvol_pm ≥ 0.2.

⇒ The candidate swing book: **calm · held · rvol30 ≥ 1 · rvol_pm ≥ 0.02**, about 234 / yr. Its grades are rvol_pm, then
fading. Same-sample caveat: the rvol30 ≥ 1 cut and the bands were read off this table; a holdout is needed.

### GF-40 — rvol30 on the non-calm gap-up tape, and on gap-downs (2026-10-20, user)

LONG, 10:00 → close, net 0.7c, every rvol_pm. Script: `scripts/equity/gapfader_rvol30_more.py` → `data/equity/gapfader/rvol30_more.md`
(GF-38's and GF-32's caches). Net PF [losing years]:

| gap-ups, held at 10:00 | rvol30 < 0.2 | 0.2–0.5 | 0.5–1 | 1–2 | ≥ 2 |
|---|---|---|---|---|---|
| calm < 40 bp | 1.01 [3] | 1.02 [6] | 1.07 [5] | 1.21 [3] | **1.61 [0/11]** (867) |
| 40–80 bp | 1.09 [7] | 1.01 [5] | 1.11 [3] | **1.20 [2]** (2,325) | 1.16 [4] |
| wild ≥ 80 bp | 0.94 [8] | 1.10, clip 0.80 [4] | 0.88 [8] | 0.89 [5] | **0.72, clip 0.52 [11/11]** |
| failed · calm | 1.03 [7] | 0.97 [6] | 0.92 [10] | 1.09 [5] | 1.14 [6] |

| gap-downs, long | rvol30 < 0.2 | 0.2–0.5 | 0.5–1 | 1–2 | ≥ 2 |
|---|---|---|---|---|---|
| held · calm | 0.90 [4] | 1.19 [4] | 0.90 [8] | 0.90 [7] | 1.14 [4] |
| held · 40–80 bp | 0.74 [7] | 1.03 [4] | 0.95 [6] | 0.91 [8] | 0.82 [7] |
| held · wild | 0.72 [7] | 0.72 [9] | 0.63 [10] | 0.41 [10] | 0.81 [8] |
| failed · calm | 0.76 [7] | 1.00 [5] | 0.83 [10] | **0.74 [11/11]** | 0.66 [9] |
| failed · 40–80 bp | 0.73 [4] | 1.03 [7] | 0.90 [6] | **0.72 [11/11]** | 0.80 [9] |

**Heavy first-half-hour volume is a long signal only on a calm tape.**
- On a wild gap-up tape it is the worst cell there is: rvol30 ≥ 2 gives 0.72, losing in all 11 years, tail-driven (clip 0.52).
- The middle tape (40–80 bp) gets a small lift at rvol30 1–2: 1.20 with 2/11 losing years.
- The strongest single cell is **calm · held · rvol30 ≥ 2: 1.61, 0/11 losing years, about 79 / yr, at any premarket volume**.
- **Gap-downs: no long swing at any rvol30.** A gap-down that failed its open on heavy volume keeps falling to the close
  (long 0.66–0.74, failed · rvol30 1–2 losing in all 11 years). That would be the SHORT side, which is parked under the
  longs-only ruling (2026-10-16). GF-32 already found the gap-down long edge spent by 10:00.

GF-40 restricted to **rvol_pm ≥ 0.2** (computed inline from the same script's `load` / `grid`). Below rvol30 0.5 the cells
are tiny: a heavy premarket nearly always comes with a heavy first half hour.

| gap-ups, held at 10:00, net PF [n, losing years] | rvol30 0.5–1 | 1–2 | ≥ 2 |
|---|---|---|---|
| calm | 1.02 [328, 5/11] | 1.32 [509, 2/11] | **1.84** [611, 2/11] |
| 40–80 bp | 1.29, clip 1.01 [418] | **1.30** [655, 2/11] | 1.18 [783, 4/11] |
| wild | 0.95 | 0.88 | 0.74, clip 0.52 [1,391, 11/11] |

Gap-downs at rvol_pm ≥ 0.2 show no positive long cell with ≥ 400 trades (0.49–1.18).

### GF-41 — gap-downs that FAILED the open at 10:00 as SHORTS (2026-10-20, user)

SHORT at the first 1s vwap at/after 10:00, cover at the close. Net = 0.7c / share, **no borrow or locate fee**. Every rvol_pm,
58,561 failed gap-downs ≤ −4 %. Script: `scripts/equity/gapfader_gapdown_short.py` → `data/equity/gapfader/gapdown_short.md`.

| short net PF [losing years] | rvol30 < 0.2 | 0.2–0.5 | 0.5–1 | **1–2** | **≥ 2** |
|---|---|---|---|---|---|
| calm < 40 bp | 1.12 [7] | 0.89 [7] | 1.12 [1] | **1.28 [0/11]** (2,114) | **1.41 [2]** (854) |
| 40–80 bp | 1.10 [7] | 0.84 [6] | 1.00 [5] | **1.26 [0/11]** (1,660) | 1.17 [3] |
| wild ≥ 80 bp | 1.04 [6] | 0.93 [4] | 1.06 [3] | **1.35 [1]** (529) | 1.28 [3] |
| rvol_pm ≥ 0.2 · not calm | 0.65 | 0.72 | 0.93 | **1.46 [0/11]** (685) | 1.17 [4] |

**Heavy first-half-hour volume (rvol30 ≥ 1) turns a failed gap-down into a short on every tape.**
- None of these depend on a few big winners: the +50 % clip leaves them unchanged.
- Below rvol30 1 it is noise (0.84–1.12, 5–7 losing years).
- All failed gap-downs together: 1.08, which has decayed (1.48 in 2016–20, 0.91 in 2021–26).

**Failed · not calm · rvol30 ≥ 1:** 3,440 trades, 313 / yr, net **1.27**, 1/11 losing years (2023 0.86), 1.41 / 1.20 by half.
Median short +0.79 %, mean net +0.86 %.

| split | net PF [losing years] |
|---|---|
| rvol_pm < 0.02 | 1.04 |
| rvol_pm 0.02–0.2 | **1.33 [0]** |
| rvol_pm 0.2–1 | 1.23 [1] |
| rvol_pm ≥ 1 | 1.43 [3] |
| gap ≤ −20 % | **1.38 [1]** |
| gap −4 … −20 % | 1.13–1.30 |
| price $1–3 | **1.68** |
| price $3–10 | 1.33 |
| price $10–30 | 0.98 |
| price ≥ $30 | 1.04 |
| dv20 $1–5M | **1.46 [0]** |
| dv20 $5–25M | 1.26 |
| dv20 ≥ $25M | 0.92 |
| ≥ 10 % below the prior close at 10:00 (95 % of the cell) | 1.28 |
| < 10 % below | 0.90 |

⚠ **The edge sits exactly where shorting is hardest.**
- It lives in cheap (under $10), thin (dv20 under $25M), deeply gapped names: the hard-to-borrow end. The locate fee per share
  there can be a large fraction of a +0.86 % mean.
- 95 % are under the **SSR uptick rule** (down ≥ 10 % from the prior close). The short must rest above the NBB, so it has to
  be a maker entry. That is our style anyway, but the fill rate is unknown.
- This sits under the longs-only ruling (2026-10-16, borrow failures). Before it can be revived we need: a locate-cost tally
  on these names (the Oms journals log locate quotes), a maker short-entry fill model under SSR, and a holdout.

### GF-42 — the gap-up long swing (calm · held at 10:00 · rvol30 ≥ 1) by segment (2026-10-20, user)

Base: 2,964 trades, 269 / yr, net **1.29** at 10:00 (1/11 losing years) and 1.31 at 10:30 (0/11). Every rvol_pm. Script:
`scripts/equity/gapfader_swing_segments.py` → `data/equity/gapfader/swing_segments.md`. Net PF at 10:00 [losing years]:

| grade | bands → |
|---|---|
| rvol_pm | < 0.02 1.03 [5] · 0.02–0.2 1.24 [1] · 0.2–1 1.41 [3] · **1–5 2.54** [2] (22 / yr) · ≥ 5 1.43 (4.19 → 0.82 by half) |
| rvol30 | 1–2 1.21 [3] · 2–5 **1.52** [1] · ≥ 5 **2.36** [1] (18 / yr): monotone |
| gap | 4–6 % 1.11 [5] · **6–10 % 1.49** [1] · 10–20 % 1.24 [2] · 20–40 % 1.42 [4] · ≥ 40 % 1.06 (5.54 → 0.49, decayed) |
| price | $1–3 1.62 (n 74) · $3–10 1.27 · $10–30 1.38 · $30–100 1.30 · **≥ $100 1.07** [4] |
| dv20 | **$1–5M 1.64** [1] · $5–25M 1.21 · $25–100M 1.03 [6] · ≥ $100M 1.37: U-shaped |
| fading | 1.43 [1] vs 1.23 [1] |

Two-way with rvol_pm (< 0.2 / ≥ 0.2):

| | rvol_pm < 0.2 | rvol_pm ≥ 0.2 |
|---|---|---|
| dv20 $1–5M | 1.45 | **2.20 [0/11]** (274) |
| dv20 $25–100M | 0.99 | 1.14 |
| gap 6–10 % | 1.39 | **1.84** |
| gap 4–6 % | 0.95 | 1.76 |
| price $3–10 | 1.07 | 1.71 |
| price ≥ $100 | 0.98 | 1.37 |

**Reading:** the clean graders are **rvol30** (monotone), **rvol_pm** (monotone up to 5) and **fading**. dv20 is U-shaped:
thin names are best and $25–100M is dead. Gap and price are noisy, except that ≥ 40 % gaps and ≥ $100 stocks are weak.

Narrowing to the best cells reaches PF 2+ (dv20 $1–5M · rvol_pm ≥ 0.2: 2.20, 0/11), but on 25–30 trades a year, and every cut
was read off this table. ⇒ The next step is a **PF−1 sizing model** (FlushFaderV2's method: logit p, log W, log L) on the base
cell, with ln rvol30, ln rvol_pm, fading, the dv20 shape and a gap term, scored on the two half holdouts. That turns these
graders into position size without hard cuts.

### GF-43 — below the $1M dv20 floor (2026-10-20, user)

The census starts at dv20 $250k, so this band is $0.25–1M: 46,020 gap-ups, with the tape built as in GF-38 (cache
`swing_sub1m.parquet`). Script: `scripts/equity/gapfader_swing_sub1m.py` → `data/equity/gapfader/swing_sub1m.md`.

| held at 10:00, net PF 10:00 [n, losing years] | dv20 $0.25–0.5M | $0.5–1M | $1–5M (GF-42) |
|---|---|---|---|
| calm | 0.97 [649, 6] | 1.19 [1,054, 3] | 1.09 [6,346, 4] |
| not calm | 0.83 [5,510, **11/11**] | 0.87 [5,338, **11/11**] | 1.03 [11,584, 7] |
| **calm · rvol30 ≥ 1** | **1.52** [160, 4] (raw 1.72) | **1.43** [206, 3] | **1.64** [693, 1] |
| calm · rvol30 ≥ 2 | 2.15 [83, 2] | 1.67 [95, 3] | 1.54 [254, 3] |
| calm · rvol30 ≥ 1 · rvol_pm ≥ 0.2 | 1.75 [92, 3] | 1.37 [111, 5] | **2.20** [274, 0] |
| not calm · rvol30 ≥ 1 | **0.70** [1,508, 10/11] | **0.74** [1,486, 10/11] | 0.98 [2,913, 6] |

- **The calm · rvol30 ≥ 1 rule holds below $1M** (1.43–1.52), but there are only ~34 trades / yr, with 3–4 losing years.
  Fees take a visible share: $0.25–0.5M is 1.72 raw vs 1.52 net, with median price $7.69.
- The year table (net PF): 2016 5.43 (8 trades) · 2017 1.11 · 2018 0.72 · 2019 2.94 · 2020 1.13 · 2021 1.11 · 2022 2.94 ·
  2023 1.02 · 2024 2.15 · 2025 1.50 · 2026 1.42.
- Fading and rvol_pm, the graders that worked from $1M up, are noise here (n 59–111).
- **Thin and not calm is the worst place to be long:** 0.70–0.74, losing in 10–11 of 11 years.
- ⚠ Our net PF charges only the 0.7c commission; the spread is not modelled, and it is widest exactly in these names.
  Median 09:30–10:00 dollar volume is $1.3M, so capacity is fine for a small account, but a maker fill model is needed
  before this band is trusted.

⇒ Extend the swing's universe to dv20 ≥ $500k at most, and only under calm · rvol30 ≥ 1. The $1–5M band stays the core.

### GF-44 — dv20 $1–5M · calm · held · rvol30 ≥ 1, by gap and price, with fading (2026-10-20, user)

693 trades, 63 / yr, net 1.64 (1/11 losing years). Fading 1.76 vs not fading 1.52. Script:
`scripts/equity/gapfader_dv15_breakdown.py` → `data/equity/gapfader/dv15_breakdown.md`. Net PF 10:00 [n, losing years]:

| | all | fading | not fading |
|---|---|---|---|
| gap 4–6 % | 1.23 [209, 4] (0.85 in 2016–20) | 1.47 | 1.00 |
| gap 6–10 % | **1.85** [232, 1] | 1.73 | 2.00 |
| gap 10–20 % | **1.70** [159, 2] | 2.34 | 1.23 |
| gap 20–40 % | 5.38 [63, 4] (7.98 → 2.71) | 2.24 [20] | 8.20 [43] |
| price $1–3 | 1.50 [61, 5] | 0.66 | 3.76 |
| price $3–10 | 1.38 [230, 2] (0.88 → 2.01) | **1.93** | **0.99** |
| **price $10–30** | **2.03 [299, 0/11]** | 2.02 | 2.04 |
| price ≥ $30 | 1.58 [103, 5] | 1.77 | 1.34 |

Gap × price, all: the **$10–30 column is positive at every gap** (4–6 % 1.80 · 6–10 % 1.83 · 10–20 % 1.93 · 20–40 % 7.09,
n 31). 4–6 % gaps on stocks under $10 are about breakeven (0.96–1.01).

**Reading:**
- The one segment that holds across the other splits is **price $10–30**: 2.03 with 0/11 losing years, about 27 / yr. It is
  the same at every gap, and it doesn't care about fading (2.02 vs 2.04).
- Inside this band **fading is not a consistent grader**. It helps at $3–10 (1.93 vs 0.99) and on 10–20 % gaps, but goes the
  other way at $1–3 and on 6–10 % / 20–40 % gaps. The cells are 20–160 trades, i.e. noise. In the full base (GF-42) it was 1.43
  vs 1.23. Leave it to the sizing model, not a hard cut.
- 4–6 % gaps are the weak end (1.23, with 2016–20 at 0.85).
- ⚠ Every cell here is 2–27 trades a year, after about 30 splits of one sample. Read it as shape for the model, not as rules.

## GF-45 — THE SIZING MODEL for the weak-premarket gap-down long (2026-10-20, user)

`scripts/equity/gapfader_sizing_model.py` → `data/equity/gapfader/sizing_model.md` (cache `sizing_tape.parquet`).
FlushFaderV2's machinery: logit p, log W, log L by IRLS, PF−1 = p / ((1−p)·L/W) − 1.
- **The trade as designed:** a LOO in the cross; half sold at 50 % of the gap and half at 75 % (resting limits,
  trade-through) until 10:00; the rest at 10:00 (GF-21's maker exit); 0.40c.
- **Gates knowable at the decision:** gap ≤ −4 % · open ≥ $1 · dv20 ≥ $1M · no early close · not NYSE-listed (today's map)
  · **rvol28** < 0.02, where rvol28 = premarket volume 04:00–09:28 / avgvol20_prior. The census's rvol_pm ran to 09:30.
- **The open as the auction price plus noise:** |e| lognormal, median 0.10 %, p90 0.65 % (the 10-19 gap watch). A trade
  needs both the noisy and the true gap ≤ −4 %.
- **The noise is harmless:** the model with the true gap scores the same, 0.860 / 0.518 vs 0.856 / 0.519.
- **Size:** 50,564 trades, 4,597 / yr. Flat net PF 1.64: 2016–19 1.36 · 2020 1.53 · 2021–26 **1.76**, every year ≥ 1.24.

**⚠ dv20's grade FLIPPED between the regimes.** Flat net PF by period:

| | 2016–19 | 2020 | 2021–23 | 2024–26 |
|---|---|---|---|---|
| dv20 $1–5M | 1.24 | 1.63 | 1.88 | **2.63** |
| dv20 ≥ $100M | **2.76** | 1.85 | 1.54 | 1.19 |
| rvol28 < 0.001 | 1.79 | 1.77 | 2.58 | 2.83 |
| rvol28 0.01–0.02 | 1.01 | 1.38 | 1.33 | 1.37 |
| bars 0 | 2.17 | 2.30 | 2.63 | 3.13 |
| bars ≥ 30 | 1.46 | 1.54 | 1.49 | 1.30 |

- **rvol28 grades the same way in every period.** It is the one stable grade.
- **Bars:** 0 bars is best in every period.
- **dv20 went from liquid-is-best (2016–19) to thin-is-best (since 2021):** a smooth trend through the years, not noise.
  GF-24's monotone dv20 table was the recent regime plus NYSE.
- Gap and price grade weakly or not at all.

**Holdouts.** Sized PF−1 = weights clip(PF−1 / fit mean, 0.25, 4), mean 1. Fits exclude 2020.

| model | across regimes: fit 16–19 → 21–26 / the reverse | **inside the current regime: 21–23 → 24–26 / the reverse** | top / bottom third PF (current) |
|---|---|---|---|
| flat | 0.755 / 0.356 | 0.852 / 0.650 (mean 0.751) | — |
| dv20 | 0.585 / 0.305 (worse than flat: the flip) | 0.965 / 0.742 (0.853) | 2.57 / 1.37 |
| **dv20 + rvol (log + < 0.005 flag)** | 0.879 / 0.504 | **1.231 / 0.942 (1.086)** | **2.71–2.24 / 1.31–1.33** |
| + bars | 0.820 / 0.551 | 1.205 / 0.914 (1.059) | 2.42 / 1.45 |
| + bars + gap (log) | 0.856 / 0.519 | 1.206 / 0.855 (1.031) | |
| + bars + gap + price | 0.916 / 0.550 | 1.230 / 0.880 (1.055) | |
| no rvol (dv20 + bars + gap) | 0.737 / 0.395 | 1.059 / 0.748 (0.904) | |
| no dv20 (rvol + bars + gap) | 0.939 / 0.519 | 1.120 / 0.807 (0.964) | |
| gap alone | 0.745 / 0.342 | 0.829 / 0.620 (inverted: bottom third better) | 1.50 / 2.06 |

**Reading:**
- **The model is dv20 + rvol28.** In the current regime it lifts the sized PF−1 by **+45 %** over flat (1.086 vs 0.751), and
  the top third runs at PF 2.2–2.7 vs 1.3 for the bottom third.
- **rvol28 carries most of it.** It also works across regimes: the 2021–26 fit applied to 2016–19 gives 0.504 vs flat 0.356.
- **Bars add nothing once rvol is in.** The zero-bar cell is the rvol28 ≈ 0 cell.
- **The gap adds nothing.** Linear, log and sqrt all score the same; the gap alone is inverted.
- **Price adds nothing beyond dv20.**
- **The fit (2021–26, per SD):** dv20 odds × 0.83, λ × 0.96; rvol28 odds × 0.84, λ × 1.10. Thinner and quieter is better on
  both legs. Predicted PF−1 p10 / p50 / p90 0.22 / 0.74 / 1.71: 5 % fall under the 0.125 floor and 4 % go above 2.
- ⚠ **dv20 is a regime bet.** Watch it in the live journal. If thin names stop leading, refit or drop the term; rvol28
  alone still sizes correctly.

### GF-45b — the cutoff moved to 09:29 (rvol29, bars 08:29–09:29), and the bars as BUCKETS (2026-10-20, user)

Same script; `data/equity/gapfader/sizing_model.md` now holds the 09:29 run (cache `sizing_tape29.parquet`). 49,662 trades;
flat net PF 1.65 (2021–26 1.77). **Nothing changes:** within the current regime dv20 + rvol29 scores 1.105 sized PF−1 vs flat
0.765 (+44 %).

| current-regime holdouts (21–23 ↔ 24–26), sized PF−1 mean | 09:28 | **09:29** |
|---|---|---|
| dv20 + rvol (log + < 0.005 flag) | 1.086 | **1.105** |
| + bars: ln(1 + b) + a b = 0 flag (the GF-45 shape) | 1.059 | 1.079 |
| + bars as BUCKET dummies (1–9 / 10–29 / ≥ 30 vs 0) | — | 1.075 |
| dv20 + bars, no rvol: continuous / buckets | 0.904 | 0.943 / 0.931 |

- **Either shape, bars add nothing once rvol29 is in.** Without rvol they recover only part of its value (0.93–0.94 vs 1.105).
- The < 0.005 flag is worth +0.008. Keep it or drop it; it doesn't matter.
- The last premarket minute is busy. Moving the bars window from 08:28–09:28 to 08:29–09:29 shrinks the 0-bar cell by a
  third (2016–19: 1,013 → 383 trades): many weak-premarket names print for the first time in 09:28–09:29.

### GF-45c — each listing's own indicator cutoff: 09:29:25 Nasdaq, 09:29:50 NYSE American (2026-10-20, user)

**rvolc** = premarket volume 04:00 → the listing's cutoff / avgvol20_prior. The cutoffs sit 5 s inside the measured LOO
cutoffs (Nasdaq 09:29:30; NYSE American's freeze 09:29:55). Unmapped (delisted) names get 09:29:25. Same script; the md now
holds this run (cache `sizing_tape_cut.parquet`). 49,523 trades; flat net PF 1.659 (2021–26 1.776).

| current-regime holdouts, sized PF−1 mean | 09:28 | 09:29 | **listing cutoff** |
|---|---|---|---|
| flat | 0.751 | 0.765 | **0.772** |
| dv20 + rvol (log) | — | 1.097 | 1.097 |
| dv20 + rvol (log + < 0.005 flag) | 1.086 | 1.105 | **1.105** |

The extra 25–50 s help the GATE (flat 0.765 → 0.772: the rvol < 0.02 screen drops a few more names that print late), not the
model's grading. It's free, so it's the spec: **the indicators run to 09:29:25 (Nasdaq) / 09:29:50 (NYSE American)**. The
fitted model on 2021–26 (per SD): dv20 odds × 0.825, λ × 0.962; rvolc odds × 0.836, λ × 1.103; the flag ≈ 1. Predicted PF−1
p10 / p50 / p90 0.24 / 0.76 / 1.74.

### GF-45d — the production coefficients (2026-10-20, user: "roll with 2021–2026")

`data/equity/gapfader/gf_coefficients.json` (tracked, force-added), written by `scripts/equity/gapfader_sizing_model.py`'s last
section. Contents:
- **The fit:** dv20 + rvolc (ln dv20 · ln(rvol + 0.001) · rvol < 0.005), 2021-01 … 2026-09, n = 31,220.
- **The gates** and **the indicator cutoffs** (XNAS 09:29:25, XASE 09:29:50, premarket from 04:00).
- **Standardisation:** per term, the winsor bounds (the fit's 1 / 99 %), mean and sd.
- β_p, β_w, β_l.
- **200 pins** (ticker, date, dv20, rvol → PF−1) for the Oms tests. The script recomputes them from the JSON alone, max |Δ| 1.8e-15.

**Fit vs 2024–26:** fitting on 2024–26 alone (16,697 trades) gives nearly the same coefficients (dv20 odds 0.773 vs 0.825,
rvol 0.866 vs 0.836) and predictions with ρ = 0.982 to the 2021–26 fit. Scored on 2024–26 the two give 1.390 (in-sample)
vs 1.345, so the doubled sample wins.

Fit-mean PF−1 is **0.88** a trade. At one unit = 15 % of equity and ~20 trades a day, that is far more than the book allows:
the 0.25 %-of-ADV cap and the book's unit cap will bind, and L2's sizing needs to handle a burst of entries all at 09:30.

## GF-46 — ⚠ the census's 20-session window reaches back to an EARLIER LISTING (found 2026-10-21, porting dv20 to the Oms)

`gapfader_census.py` computes `avgvol20_prior` / `dv20_prior` with `ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING` over a
ticker's daily rows with NO date bound and no break between listings. When a ticker is recycled, the window mixes the two
companies. ARIA 2026-08-13: 3 rows of the new ARIA (the Aria Opportunities ETF, a few hundred shares a day) plus 17 rows of
ARIAD Pharmaceuticals from early 2017 → avgvol20_prior 6.97M, dv20 $166M for a security that trades ~$1k a day.

| census rows | 20-row window spans > 1,100 days | spans > 60 days | in the model's pool (open ≥ $1, dv20 ≥ $1M, gap ≤ −4 %, rvol_pm < 0.05, no early close) with a > 1,100-day window |
|---|---|---|---|
| 1,278,260 | 4,333 (0.34 %) | 6,221 | **495** (~1.5 % of the pool) |

The Oms's `Reference.volume20s` (1,100-day window) leaves those tickers OUT (no 20 rows): the live system cannot trade
them, so they are trades in the backtest that live would not take, each carrying an unrelated company's dv20. Not refitted;
the fix is an episode break (a > 45-day gap between rows, as `barnum`) in the census window, then a refit of GF-45.

**FIXED + REFITTED the same day (user):** the census's 20-session windows now run inside the episode (`(ticker, episode)`,
the rule of `scripts/equity/build_daily_episodes_causal_view.sql`), as does the Oms's `Reference.volume20s` (so the two agree
exactly: 11,974 census rows over 23 days, worst 8e-16). Census 1,278,260 → 1,272,551 rows (5,796 out, 87 in, 118 values
moved); the gap-down pool (open ≥ $1, dv20 ≥ $1M, gap ≤ −4 %, rvol_pm < 0.02) 73,693 → 73,176 (520 out, 3 in, spread over
every year). GF-45 refitted on it (`gf_coefficients.json`; the previous fit kept as `gf_coefficients_pre_gf46.json`, the
report as `sizing_model_pre_gf46.md`):

| | before | after |
|---|---|---|
| trades (NYSE removed) / fit set 2021–26 | 49,523 / 31,220 | 49,044 / 30,804 |
| flat net PF 2021–26 | 1.776 | 1.764 |
| current-regime holdout, flat sized PF−1 (mean of 21–23→24–26 and back) | 0.772 | 0.760 |
| … dv20 + rvol (log + < 0.005 flag), THE MODEL | **1.105** | **1.087** (+43 % over flat, unchanged) |
| beta_p | 0.7227 / −0.1921 / −0.1794 / −0.0330 | 0.7171 / −0.1805 / −0.1737 / −0.0206 |
| beta_w | 1.0657 / −0.0682 / 0.0266 / −0.0069 | 1.0619 / −0.0709 / 0.0308 / −0.0039 |
| beta_l | 1.2011 / −0.1075 / 0.1249 / 0.0200 | 1.1978 / −0.1088 / 0.1329 / 0.0280 |
| fit-mean PF−1 | 0.8805 | 0.8675 |

The rows that left were mildly profitable trades the live system could never take; the model's lift is unchanged.
Related: ARIA's type. `ticker_reference` lists ARIA as CS (Ariad) AND ETF (the new security), so a CS/ADRC universe keeps the
ETF; the live universe (`Reference.csAdrRows`) does the same.

## GF-47 — the opening print's CONDITION CODES at the listing venue (2026-10-23, user: "do Nasdaq and AMEX always use 17?")

The bulk trades tape, 2026-10-16 / 19 / 20, every Nasdaq (exchange 12) and NYSE American (exchange 1) listing, the listing
venue's own prints 09:29–09:35 carrying 16 (Market Center Official Open), 17 (Market Center Opening Trade), 18 (Reopening
Trade) or 25 (Opening Prints):

| | NYSE American | Nasdaq |
|---|---|---|
| tickers printing at the listing venue | 229 / 232 / 229 | 3,268 / 3,331 / 3,296 |
| with a 17 | 177 / 197 / 176 (77–85 %) | 99.1–99.8 % |
| **a 16 and no 17** | **52 / 35 / 53 (15–23 %)** | 18 / 6 / 28 |
| neither | 0 | 0–2 |
| 18 or 25 from the listing venue | never | never |
| both: the same price and size | 100 % | 100 % |
| both: the 16's timestamp | the same ms | the same ms (93–96 %) or later, never earlier |

16 and 17 never appear on one print; where both come they report the SAME cross. ⇒ **The cross is the first listing-venue
print carrying 16 OR 17.** GF-24's "the largest condition-17 print" missed the cross on ~1 in 5 NYSE American names (its
76.1 % agreement with the official open may partly be this). The live system takes 16 or 17.


**16 is a REPORT, not a trade** (Massive's conditions API, 2026-10-23): 16 Market Center Official Open and 15 Market Center
Official Close have `updates_volume / updates_open_close / updates_high_low` all FALSE (CTA / UTP 'Q' and 'M'); 17, 19, 25,
8, 18, 28 update all three. So the 16 duplicates the 17 print's volume, and is the ONLY record of the official open where
no 17 comes. ⚠ **The 1s bar convention keeps both** (`TradeFilters`: 15 and 16 are neither in the always-kept open/close
set nor in the excluded set): the 09:30:00 bar counts the cross volume TWICE wherever the 17 and the 16 both come (99 % of
Nasdaq names, ~80 % of NYSE American), and the 16:00 bar the close's (15). Research and the live bars share the filter, so
parity holds; the open/close bars' volumes are inflated. Fixing it = 15 + 16 into the excluded set, a rebuild of the 1s
bars and a re-seal of every engine (backlog).

**How much the bars keep** (`keepByMask` on the raw tape, 5 days): 95–100 % of the 16 reports and 97–100 % of the 15
reports pass the filter (they are ordinary prints to it), and the kept 15 + 16 volume is **9.5 % (2017-04-03), 14.7 %
(2021), 15.6 % (2024), 17.8 % / 16.7 % (2026-08-10 / 10-20) of the day's 1s-bar volume** — the close's 15 duplicating the
closing auction (whose own print is condition 8; 19 never appears). Ticker-days with a 17 and a 16 (the open doubled): 73–84 %
in every year sampled (2 days a year, 2016–26). ⚠ So every volume feature touching the 16:00 bar, and dollar-volume
totals over the whole day, carry this inflation — to be fixed and every system revalidated (user 2026-10-23: next).

## GF-48 — CONSISTENCY of the system as built in the Oms (2026-10-23, user: "max drawdown, worst daily / weekly / monthly loss, the worst losing streaks")

`scripts/equity/gapfader_consistency.py` (output `data/equity/gapfader/consistency.md`, not tracked). The GF-45c trades
(same gates, same auction-price noise), the Oms's exit (ONE target at 50 % to 10:00, then the 10:00 vwap; 0.40c) and the
Oms's sizing at a FIXED 8,000 (pf1 from gf_coefficients.json; clip [0.125, 2] × 15 % of equity; the 0.25 %-of-dv20 cap; the
day's signals ranked by pf1 into the book: 10 units, margin 100 % of equity at 50 % / 100 % under $3; minimum notional 200).
15,523 of 49,044 trades taken (~6 a day; the book binds daily). P&L in % of the 8,000, not compounded.

| period | max drawdown | worst day | worst week | worst month | losing streak: days / weeks / months | losing days / weeks / months | net PF |
|---|---|---|---|---|---|---|---|
| all 2016-08 .. 2026-09 | **29.4 %** (2022-11-03 → 11-09) | −11.5 % (2022-11-04) | −17.2 % (2022-11-07 wk) | −7.3 % (2022-11) | 9 / 3 / 2 | 22 / 12 / 6 % | 2.16 |
| 2016–19 (out of sample: the fit is 2021–26) | 13.1 % | −8.2 % | −10.1 % | −2.8 % | 9 / 3 / 2 | 34 / 26 / 15 % | 1.37 |
| 2020 | 6.5 % | −6.5 % | −2.0 % | none | 3 / 1 / 0 | 17 / 4 / 0 % | 2.60 |
| 2021–23 | 29.4 % | −11.5 % | −17.2 % | −7.3 % | 4 / 2 / 1 | 19 / 7 / 3 % | 2.29 |
| 2024–26 | 8.0 % | −7.0 % | −4.7 % | none | 4 / 2 / 0 | 14 / 3 / 0 % | 2.96 |

- The worst stretch is ONE week, November 2022: LUNG −39.9 % on a 2-unit position (−10.1 % of equity), then BVS, RDNT.
- ⚠ Optimistic: the model is in-sample on 2021–26 and the rank puts the top-pf1 names first; every LOO fills at the open
  and the target fills on the trade-through (the Oms works the exit as a maker AFTER the trade-through bar); the P&L per
  year (+97 % in 2016–19, +300–470 % since 2021, at 8k) is not an expectation. The drawdown and streak columns are the
  ones to plan with; 2016–19 the honest guide.

## GF-49 — the DEATH-SPIRAL rule: refuse a stock down ≥ 95 % in a year (2026-10-26, user, after NIVF)

**Status 2026-10-28: REJECTED** (user): superseded by the dv60_med ≥ $500k floor (GF-54), which keeps only 105 of
these 961 trades. See the end of GF-55.

`scripts/equity/gapfader_spiral.py` → `data/equity/gapfader/spiral.{md,parquet}`. User: "we need to avoid trading stocks
that are down more than 95 % in the past year", after NIVF on the first live day.

NIVF had been reverse-split again and again, and its share count `n` went from 1 to 0.000002 since its 2024 listing.
- **The book:** GF-45's (same gates, noise and seed) with the Oms's exit (ONE target at 50 % to 10:00) and production units
  (clip(PF−1, 0.125, 2) from gf_coefficients.json): 49,044 trades.
- **The feature:** chg_1y = close(D−1)·n(D−1) / (close(Y)·n(Y)) − 1, with Y the last session ≤ D−1 − 365 days inside the
  ticker's episode (docs/price_adjustment.md: causal through every reverse split).
- **History:** 14.4 % of trades have less than a year of history (reported apart: the change since the listing).

| chg_1y | n | PF flat | PF units | mean % | Σ units·r |
|---|---|---|---|---|---|
| ≤ −99 % | 340 | 1.22 | 1.71 | +0.35 | +2.1 |
| −99 … −95 % | 621 | 1.45 | 1.57 | +0.62 | +3.7 |
| −95 … −90 % | 712 | 2.33 | 3.08 | +1.30 | +10.3 |
| −90 … −75 % | 2,800 | 1.47 | 1.68 | +0.63 | +21.8 |
| −75 … 0 % | 19,836 | 1.44 | 1.56 | +0.53 | +127.5 |
| > 0 % | 17,659 | 1.69 | 1.86 | +0.73 | +126.9 |
| < 1 year of history | 7,076 | 2.11 | 2.61 | +1.11 | +92.3 |
| ALL | 49,044 | 1.62 | 1.80 | +0.70 | +384.6 |

**The rule at −95 %:**
- It refuses 961 trades (1.96 %; 4.66 % of 2024–26), which made PF 1.36 flat / 1.61 units and +5.7 of the book's +384.6.
- The kept book: PF units 1.80 → **1.81**, Σ −1.5 %.
- By regime, refused vs kept: 2016–19 0.66 [42] vs 1.32 · 2020 1.27 [36] vs 1.55 · 2021–23 **2.03** [115] vs 1.62 ·
  2024–26 1.36 [768] vs 1.86. In the current regime the spirals are the weaker trades, but they are still profitable.
- **Disproportion test:** 2 % of the book moves PF +0.2 %. That is consistent with a quality filter, not a hidden selector.
- The threshold sweep (−80 … −99 %) moves PF units by −0.012 … +0.004. Only −95 % and beyond are not negative.

**NIVF in the book:** 17 trades since 2024; the 9 with chg_1y ≤ −95 % (from 2025-05) sum to −5.8 %.

⚠ **What the backtest cannot see:** the exit. A spiral name sells off for hours, and the Oms's maker exit then rests on
the ask while the price falls through it (NIVF 2026-10-26: the 10:00 exit was still unfilled at 11:38). The book exits
at the 10:00 1s vwap.

**The rule is cheap insurance, not an edge.** Decision: the user's (2026-10-26).

## GF-50 — ACTIVITY floors: the 20-session median of active 1s bars, and the median dollar volume (2026-10-26, user, after NIVF)

`scripts/equity/build_daily_bars.py` → `data/equity/daily_bars/` (new): per ticker-day RTH / premarket active 1s bars, trades
and volume, from the marker-free 1s corpus (2,568 days, 2 min). `scripts/equity/gapfader_bars20.py` →
`data/equity/gapfader/bars20.{md,parquet}`. The book is GF-49's: 49,044 trades, the Oms's exit and units.

User's hypothesis: NIVF's one huge day (144M dollars on 2026-09-30) lifted its MEAN dv20 and made a thin name look safe.
The features, over the 20 prior sessions in the episode:
- `bars20_med`, the median count of active 1s bars;
- `dv20_med` (user: "let's also introduce median 20d volume"), the median dollar volume;
- `spike` = dv20 (mean) / dv20_med;
- `vol_top1`, the biggest session's share of the 20 sessions' volume.

**⚠ The thin and spiky names are GapFader's BEST trades.**

| slice | n | PF flat | PF units |
|---|---|---|---|
| bars20_med < 250 | 3,776 | **2.41** | **3.13** |
| bars20_med 2,000–4,000 | 11,440 | 1.51 | 1.61 |
| bars20_med 8,000–16,000 | 2,418 | 1.33 | 1.23 |
| dv20_med < $0.5M | 5,677 | **2.55** | **3.46** |
| dv20_med ≥ $25M | 13,334 | 1.49 | 1.55 |
| spike ≥ 5 | 8,322 | **2.20** | **3.01** |
| spike < 1.25 | 28,050 | 1.40 | 1.47 |
| ALL | 49,044 | 1.62 | 1.80 |

- **Every floor cuts the edge far beyond chance.** Against the iso-trip control (the same n refused at random, 200 draws):

  | rule | Δ PF units | random p5 … p95 |
  |---|---|---|
  | bars20_med < 250 | −0.091 | −0.014 … +0.014 |
  | dv20_med < $1M | −0.229 | ±0.02 |
  | spike ≥ 5 | −0.170 | ±0.02 |

- **It holds in every regime.** bars20_med < 500, refused vs kept: 2016–19 1.64 vs 1.23 · 2020 1.75 vs 1.52 · 2021–23 2.63 vs
  1.49 · 2024–26 2.68 vs 1.65. This is GF-18 / GF-45 again: the surprises and the thin names carry the edge.
- **NIVF on 2026-10-26 would have passed both floors.** Its 20 prior sessions: dv20 mean $14.9M, median **$2.32M**, spike 6.4,
  bars20_med **3,545** (1,102 … 20,599 a day). By the median it was not thin.
- **Capping the size by the median** (MaxNotional = 0.25 % × dv20_med instead of the mean; 8k equity, $200 floor): $ PF 1.81 →
  1.69, total −20 %, 2024–26 −28 %. NIVF is untouched (its cap would have been $5.8k against a $421 position).
- **What did go wrong on NIVF is the EXIT.** The 10:00 time stop's maker exit rested on the ask while the stock fell, and it
  sold at 11:52–11:53 at 1.18 / 1.17. That is −11.7 % against about −6.5 % at the 10:00 price the backtest assumes (the sell
  went out at 1.25 = the ask), so roughly half the loss is the exit's.

⇒ No activity floor: it removes the edge. The open question is the time-stop exit on a falling name (the user's call).

## GF-51 — the PREVIOUS SESSION's activity: bar count and dollar volume on D−1 (2026-10-26, user)

`scripts/equity/gapfader_prevday.py` → `data/equity/gapfader/prevday.{md,parquet}`. The book is GF-50's.
- `bars_d1`: RTH active 1s bars on D−1.
- `dv_d1`: D−1's dollar volume.
- `rel_d1` = dv_d1 / dv20_med.

**The quieter the previous session, the better the trade: even more strongly than over 20 days.**

| slice | n | PF flat | PF units |
|---|---|---|---|
| bars_d1 < 250 | 1,683 | **4.76** | **5.57** |
| bars_d1 250–500 | 3,229 | 2.67 | 3.06 |
| bars_d1 4,000–8,000 | 8,636 | 1.38 | 1.39 |
| bars_d1 ≥ 16,000 | 581 | 1.19 | 1.11 |
| dv_d1 < $0.25M | 1,618 | **5.90** | **7.58** |
| dv_d1 ≥ $25M | 15,617 | 1.50 | 1.58 |
| rel_d1 < 0.25 (yesterday quiet vs the median) | 682 | 3.65 | 5.38 |
| rel_d1 ≥ 5 (yesterday busy) | 5,050 | 1.81 | 2.16 |

- **Every floor cuts the edge far beyond the iso control.** bars_d1 < 500: Δ PF units −0.178 against random ±0.014; dv_d1 <
  $1M: −0.206 against ±0.016. The quiet-yesterday trades lead in every regime: bars_d1 < 500, 2021–23 4.56 vs 1.49 · 2024–26
  4.63 vs 1.65.
- **A busy yesterday** (rel_d1 ≥ 5) is neutral: Δ −0.032, inside the random band's edge.
- **NIVF on 2026-10-26** would have passed these too: D−1 (10-23) 3,348 bars, $1.59M.

⚠ **This is where the backtest's execution model is weakest.** PF 4.8–5.9 on names that barely traded the day before is too
good to take at face value. In a thin name the target "fills" on any trade-through print, however small, and the 10:00 exit
fills at the first 1s vwap. Neither has to hold for our size. The next study (the user's concern, "whether we'll be able to
tap into those live"): an execution-realistic book. The target needs volume through the level ≥ our shares, the 10:00 exit
pays the spread, and the comparison is made by activity bucket.

## GF-52 — the 60-session MEDIAN dollar volume (2026-10-27, user, after NIVF and GIXI)

`scripts/equity/gapfader_dv60.py` → `data/equity/gapfader/dv60.{md,parquet}`. The book is GF-51's (49,044 trades).
- `dv60_med`: the median of volume × close over the 60 prior sessions in the episode (fewer for a younger listing; 96.5 % have 60).
- `surge` = dv20_med / dv60_med: the last 20 sessions against the last 60.

**Why:** NIVF passed GF-50's 20-session median ($2.32M) because its 20 sessions were the death-spiral frenzy (09-30: $144M,
859M shares at $0.17). Over 60 sessions its median is **$224k** (Aug and Sep ~$150k a day): surge ≈ 10. GIXI (listed
2026-09-02, 37 sessions) is thin on both: $99k over 20, $166k over all 37; one day (10-12, $184M) carried 83 % of its 20
sessions' dollars, so the MEAN gate ($11.0M ≥ $1M) passed it.

**The thinner over 60 sessions, the better the trade — a cleaner gradient than over 20.**

| dv60_med decile | n | PF flat | PF units |
|---|---|---|---|
| D1 < $0.149M | 4,905 | **2.48** | **3.30** |
| D2 $0.149–0.719M | 4,904 | 2.19 | 2.80 |
| D3 $0.72–1.72M | 4,904 | 1.63 | 1.80 |
| D4 $1.72–3.22M | 4,905 | 1.44 | 1.57 |
| D5–D9 $3.2–85M | 24,521 | 1.33–1.48 | 1.43–1.62 |
| D10 > $85M | 4,905 | 1.64 | 1.77 |
| ALL | 49,044 | 1.62 | 1.80 |

| surge | n | PF flat | PF units |
|---|---|---|---|
| < 0.5 | 932 | 2.32 | 2.74 |
| 0.8–1.25 | 24,324 | 1.53 | 1.66 |
| 2–4 | 3,741 | 1.88 | 2.50 |
| 4–8 | 1,616 | 2.11 | 2.76 |
| ≥ 8 | 2,178 | 1.90 | 2.52 |

- **D1 leads in every year** 2016–2026 (flat 1.72–3.21; the book 1.21–2.03). D2 leads in 10 of 11 (2021: 1.45 vs 1.81).
- **Every floor cuts the edge far beyond the iso control.** dv60_med < $0.25M refuses 13.1 %: Δ PF units −0.159 against random
  ±0.019, −107.5 of Σ units·r (−28 %); < $1M: −0.251, −44 %. The refused thin trades lead in every regime: < $0.25M,
  2016–19 2.02 vs 1.27 · 2020 2.08 vs 1.52 · 2021–23 2.71 vs 1.52 · 2024–26 2.41 vs 1.66.
- **NIVF's shape** (dv60_med < $0.25M and surge ≥ 4): 2,297 trades, PF 2.29 / 2.95 — better than the book. A surge is no
  warning sign either: surge ≥ 4 refused = 1.98 / 2.62, Δ −0.050.
- **The sizing already leans this way**: dv60_med ~ pf1 Spearman −0.335 (the model's `ldv` is the MEAN dv20); the units
  PF exceeds the flat one most in the thin deciles (D1 3.30 vs 2.48).

⇒ Ranked on dv60_med, GapFader's edge sits in the thin names — the ones that hurt us live on 10-26 (NIVF, GIXI). Whether
that edge is capturable is GF-51's open question: the execution-realistic book, by this decile. Not yet tested: whether
dv60_med adds to pf1 (dv60_med deciles within pf1 bands).

## GF-53 — an alternative sizing model on the 60-session MEDIAN dollar volume (2026-10-27, user)

`scripts/equity/gapfader_dv60_model.py` → `data/equity/gapfader/dv60_model.{md,parquet}`. GF-45's book verbatim (49,044
trades after GF-46, seed 20261020); the current model refits to `gf_coefficients.json` exactly (max |Δβ| 0) and gives the
live pf1 of 10-26 exactly (NIVF 0.371, GIXI 0.840). Only `ldv = ln dv20_prior` (the 20-session MEAN) is swapped; rvol's two
terms stay, and so do the gates (dv20_prior ≥ $1M). Each model is fitted and scored on the same trade, on two trades: the
DESIGN trade GF-45 was fitted on (½ at 50 % / ½ at 75 %) and the one the OMS RUNS (one target at 50 %).

**The 60-session median beats the 20-session mean on every holdout, on both trades.**

| model | current regime (fit 21–23 ↔ 24–26), sized PF−1 design / Oms trade | units PF−1 design / Oms | across regimes (16–19 ↔ 21–26), sized design / Oms |
|---|---|---|---|
| flat | 0.760 / 0.723 | 0.760 / 0.723 | 0.563 / 0.521 |
| **current: ln dv20 mean + rvol** | **1.087** / 1.027 | 1.064 / 1.011 | 0.636 / 0.577 |
| **dv60_med + rvol** | **1.214** / **1.140** | **1.177** / **1.119** | **0.809** / **0.746** |
| dv20_med + rvol | 1.219 / 1.141 | 1.186 / 1.122 | 0.736 / 0.673 |
| both (dv20 mean + dv60_med) + rvol | 1.165 / 1.088 | 1.119 / 1.057 | 0.751 / 0.688 |
| dv60_med + surge + rvol | 1.209 / 1.131 | 1.174 / 1.111 | 0.749 / 0.686 |

- Over flat, the lift on the current regime grows from +43 % to **+60 %** (design trade); across regimes it doubles, +13 % → +44 %.
- **The statistic matters more than the window.** A 20-session MEDIAN does as well inside the current regime (1.219), but the
  60-session one carries across regimes (0.809 vs 0.736). Adding the mean back to dv60_med makes it WORSE (1.165): the mean
  is noise once the median is in.
- **Σ units·r (production's clip):** A→B +164.4 vs +136.3 (+21 %); B→A +126.8 vs +134.0 (−5 %). The units PF improves both
  ways; the total does not.
- Fitted as production (2021–26, design trade): ln dv60_med per SD odds × 0.780, W × 0.877, L × 0.840 (current's ln dv20:
  0.835 / 0.932 / 0.897) — the same direction, steeper. Mean predicted PF−1 0.853 vs 0.867; Spearman between the two
  models +0.858; at the 0.125 floor 7.7 % vs 4.8 %, at the 2-unit cap 4.8 % both.
- **Where they disagree most** (in-sample): 1,849 trades the new model sizes ≥ 2× the current (median dv20 mean $41.7M,
  dv60_med $0.11M — a quiet name with a spike in its 20 sessions) are only flat PF 1.35, units 1.49 current vs 1.45 new;
  the 1.25–2× band (4,119, dv60_med $0.149M) is PF 2.24, units 2.71 vs 2.63. The model's gain is in the ranking of the bulk
  (0.8–1.25×: 10,982 trades, units 2.98 → 3.10), not in its upsizing of the spike names.

**NIVF and GIXI on 10-26 would have been sized LARGER, not smaller:**

| ticker | dv20 mean | dv60_med | rvol | pf1 current (= live) | pf1 dv60_med | units |
|---|---|---|---|---|---|---|
| NIVF | $14.93M | $0.224M | 0.0140 | 0.371 | 0.735 | 0.37 → 0.73 |
| GIXI | $11.03M | $0.166M | 0.0033 | 0.840 | 1.274 | 0.84 → 1.27 |

Both are the "quiet name, one spike" shape. The system's cap (MaxNotional = 0.25 % × dv20_prior, the mean) is untouched
here; on the median it would bind hard (GIXI: $415). GF-50 measured a median-based cap at −20 % of P&L.

## GF-54 — THE PRODUCTION REFIT on the 60-session MEDIAN: the model, the gates, the cap; the $500k floor; the Oms's trade (2026-10-27, user)

`scripts/equity/gapfader_model_v2.py` → `data/equity/gapfader/model_v2.{md,parquet}` and **`gf_coefficients_gf54.json`**
(production's `gf_coefficients.json` untouched until the Oms switches).

**User rulings:**
- **The volume estimates are 60-session MEDIANS** in the model, the gates and the cap: dv60_med = median(volume × close),
  vol60_med = median(volume / n) × n(D) (shares on D's scale), over the 60 prior sessions in the episode. A younger
  listing uses all its prior sessions, 20 at the least (the census and the Oms's Reference need 20) — "I like that
  approach". rvol = premarket volume / vol60_med, in the terms and in the rvol < 0.02 gate.
- **The floor is dv60_med ≥ $500k**, replacing dv20_prior ≥ $1M: "to trade we actually need counter parties". The
  backtest's PF below it (GF-52) is discounted as not a quantity we can trade.
- **The cap**: MaxNotional = 0.25 % × dv60_med.
- **Fitted on the trade the Oms runs**: one target at 50 % to 10:00, then the 10:00 1s vwap.

The book is GF-45's with the volume swapped; the census pool is pre-filtered on the new rvol (premarket to 09:30 /
vol60_med < 0.05). GF-45's rows keep their exact auction-price noise; a new row draws from a seed of its own (ticker,
date). Their tape is GF-45's cache plus `sizing_tape_cut_ext.parquet` (5,718 rows, the same query).

| book | n | flat PF | 2016–19 | 2020 | 2021–23 | 2024–26 |
|---|---|---|---|---|---|---|
| **dv60_med ≥ $500k, rvol60 < 0.02** | **38,055** | **1.566** | 1.272 | 1.562 | 1.570 | 1.773 |
| … GF-45's gates pass too | 34,620 | 1.520 | 1.255 | 1.549 | 1.515 | 1.672 |
| … not in GF-45's | 3,435 | 2.092 | 1.439 | 1.783 | 2.049 | 2.997 |
| … fewer than 60 sessions behind the medians | 1,163 | 3.350 | 3.111 | 1.370 | 4.107 | 4.121 |

(GF-45's book on the same trade: 49,044 trades, flat PF 1.621.) Sessions behind the medians: 20–29: 380 · 30–39: 308 ·
40–49: 249 · 50–59: 226 · 60: 36,892.

**The models on this book:**

| model | current regime (21–23 ↔ 24–26): sized / units PF−1 · Σ units·r | across regimes (16–19 ↔ 21–26): sized / units |
|---|---|---|
| flat | 0.671 / 0.671 · +45.9 / +54.4 | 0.467 / 0.467 |
| current: ln dv20 mean + rvol20 | 0.829 / 0.827 · +55.1 / +75.8 | 0.502 / 0.499 |
| **ln dv60_med + rvol20** | **0.847 / 0.846** · +55.7 / +76.9 | **0.515 / 0.510** |
| ln dv60_med + rvol60 | 0.845 / 0.843 · +54.7 / +76.6 | 0.492 / 0.491 |

- **dv60_med beats the mean** in every column, by a little: +0.018 in the current regime, +0.013 across regimes; Σ
  units·r +0.6 / +1.1. Most of GF-53's lift came from ranking the thin names, which the floor removes.
- **⚠ rvol on the median does NOT help as a term**: −0.002 in the current regime, −0.023 across regimes against rvol on
  the mean (beside dv60_med). Within noise; the user's ruling decides which goes into production.
- **Young listings (< 60 sessions behind the medians) lead**: 1,163 trades at PF 3.35 (4.1 in 2021–26). Small n.
- **The fit (2021–26, n = 21,910; ln dv60_med + rvol60), per SD:** ln dv60_med odds × 0.771, W × 0.930, L × 0.826;
  ln rvol60 0.865 / 1.019 / 1.101; rvol60 < 0.005 0.933 / 1.002 / 1.036.
- **Predicted PF−1:** p10 / p50 / p90 0.25 / 0.65 / 1.21, mean 0.692 (GF-45's 0.867). 3.8 % sit at the 0.125 floor and
  none reach the 2-unit cap, so GapFader takes less capacity than before (the user: free it for FlushFader).
  In-sample units PF 1.879 vs flat 1.663.
- **Calibration** (in-sample deciles): predicted PF−1 0.14 → 1.42, realised 0.24 → 1.88; deciles 1–5 realise 0.24–0.42.
- **10-26's NIVF (dv60_med $224k) and GIXI ($166k) are below the floor**: neither would have traded.

## GF-55 — CONSISTENCY of the system as deployed since 2026-10-27 (2026-10-28, user: "its % of profitable days, weeks and months")

`scripts/equity/gapfader_consistency_v2.py` (output `data/equity/gapfader/consistency_v2.md`, not tracked). GF-48's method
on GF-54's book (`model_v2.parquet`):
- **The system:** the gates dv60_med ≥ $500k · rvol60 < 0.02; GF-54's model; the cap 0.25 % × dv60_med; ONE target at
  50 % to 10:00.
- **The sizing:** the Oms's at a FIXED 8,000 (no compounding), the day's signals ranked by pf1 into the 10-unit / 100 %-
  margin book.
- **Taken:** 13,991 of 38,055 trades; the rest fell below the 200 minimum notional once the book was full.
- A period with no trade is not counted.

| period | profitable days | profitable weeks | profitable months | net PF | max drawdown | worst day / week / month | losing streak d / w / m |
|---|---|---|---|---|---|---|---|
| all 2016-08 .. 2026-09 | **72 %** | **81 %** | **89 %** (109/122) | 1.71 | 19.0 % | −9.8 / −12.2 / −8.0 % | 6 / 3 / 2 |
| 2016–19 (out of sample) | 64 % | 71 % | 80 % | 1.26 | 13.6 % | −6.7 / −11.1 / −5.8 % | 6 / 3 / 2 |
| 2020 | 80 % | 92 % | 100 % | 2.38 | 4.1 % | −3.3 / −0.7 / +8.5 % | 4 / 2 / 0 |
| 2021–23 | 71 % | 79 % | 86 % | 1.64 | 19.0 % | −8.4 / −12.2 / −8.0 % | 4 / 3 / 2 |
| 2024–26 | 81 % | 94 % | 100 % (33/33) | 2.30 | 17.0 % | −9.8 / −10.5 / +1.5 % | 4 / 2 / 0 |

- **By year:** profitable months were lowest in 2017 (58 %) and 2023 (75 %). Every year was net positive (2016's five
  months +13 % … 2025 +246 % of E).
- **Frequency:** trades on 2,400 of ~2,630 weekdays, a median of 5 a day.
- **Vs GF-48 (the 20-session mean, the $1M floor):**
  - losing months 6 % → 11 % and PF 2.16 → 1.71;
  - the max drawdown 29.4 % → 19.0 %.
  - The $500k floor drops the thinnest names: the best backtest PF (GF-52), but not tradeable at our size.
- ⚠ **The same optimism as GF-48:**
  - the model is in-sample on 2021–26 (2016–19 is the honest guide);
  - every LOO fills at the open and the target on the trade-through;
  - FlushFader does not compete for the book;
  - The P&L per year is not an expectation.

**GF-49's death-spiral rule: REJECTED (user 2026-10-28).** "The new 60d median volume feature supersedes it. The big issue
with these death-spiral stocks is the liquidity and not their overall trend." The check: of GF-49's 961 trades with
chg_1y ≤ −95 %, only 105 (11 %) pass dv60_med ≥ $500k. They are 0.28 % of GF-54's book, with a flat PF of 1.44 there vs
the book's 1.57.

## GF-56 — the gap as the HOLDER'S RETURN; the −50 % split guard (2026-10-31, user)

**User:** "switch both systems [GapFader and LowFader] to the holder's return since that is the correct approach".
The gap was `open / pc_total − 1` — the back-adjusted convention LowFader's `chg_1d` had kept through the causal
migration (S43br) and GapFader reused. It is now the holder's return from D−1's close (docs/price_adjustment.md §2):

    gap = [(P·n(D) − P1·n(D−1)) + (C(D) − C(D−1))] / (P1·n(D−1)) = (P − div_m1) / close_m1 − 1

`gapfader_census.py` keeps `close_m1`, `div_m1` and `pc_total = close_m1 + div_m1` (the price whose holder's return is 0:
the target's other end, unchanged); `gapfader_model_v2.py`'s auction gap and `gapfader_consistency_v2.py`'s LOO limit
(`close_m1 × 0.96 + div_m1`) follow. **The production coefficients are kept** (user: the model has no gap term);
`model_v2.py --coefficients-out` wrote the refit beside them (`gf_coefficients_holder_refit.json`, not used).

**The effect, with the auction noise held fixed** (the previous book, its own noise recovered, under the new gates):
3 of 38,055 candidates drop out, all on an ex-dividend day; at 8k the traded book is 13,992 trades / +$111,112 / PF 1.707
against 13,991 / +$111,147 / 1.707 — every year identical but 2020 (−$35). LowFader: identical on all 10,472 ex-dividend
ticker-days 2020-01 … 2026-06 (the one trade, AOS 2020-07-30, unchanged).

⚠ **The full rebuild reshuffled ~9 % of the book's membership (3,449 rows) for an unrelated reason:** GF-45's rows draw
their auction noise SEQUENTIALLY in the pool's order (seed 20261020), so the 137 fewer gap rows of the rebuilt census
(the daily data revised since 10-21) shifted every later draw across the `gap_est ≤ −4 %` gate. The rebuilt book's
stats are the same in substance (consistency_v2.md: days / weeks / months profitable 72 / 82 / 89 % vs 72 / 81 / 89 %,
PF 1.71 both, +137 % vs +138 %). A per-row seed (as the newer rows already use) would make rebuilds stable.
The previous outputs: data/equity/gapfader/pre_holder_20261031/.

**The −50 % guard** (the Oms's GapVerdict.SplitSuspect): GF-54's book at 8k has 9 trades below a −50 % gap, together
−$42 (such real gaps average −6.0 % a trade); refusing them is free, and it stops a vendor split dated D that the tape
never shows (OIO 2026-04-24: a −67 % "gap") from being bought at the model's deepest size.
