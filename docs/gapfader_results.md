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
