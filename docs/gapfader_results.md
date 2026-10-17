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
