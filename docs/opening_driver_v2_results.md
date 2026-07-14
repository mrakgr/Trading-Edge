# OpeningDriverV2 — the tradable opening-drive engine

Fork of OpeningDriver (the post-hoc recorder, `docs/opening_driver_results.md` F1–F24).
V2 restores an **arm/disarm state machine**: per (ticker, day) it scans the entry window
[09:45, 10:30] and fires the **first** bar that passes ALL settled gates, opens **one**
position, then **disarms** for the rest of the day (no re-arm yet). This collapses the V1
one-trip-per-window-bar overcount (the same drive counted at every minute it stayed in the
window) to **one trip per drive-day** = the real book.

**The settled arm gates (the F1–F24 headline book), all this-bar-inclusive:**
`chg_1d ≥ 0.20 & chg_3d ∈ [0,1.5] & log_atr_20 ≥ 0.013 & stop_dist ≥ 0.03 & bl < 15 & bh ≥ 1`.
Stop = 9-EMA below the frozen 9-EMA session-min (`sess-ema-low`, the V1 winner). `vol_slope` is
a **skip filter** (default) or a **gate** (`--vol-slope-as-gate`), and is **OFF by default**.

## F1 — parity with the V1 post-hoc book, then the real (de-overcounted) numbers

**Parity check (09:45-only, `--entry-end-min 585`):** V2 reproduces the V1 post-hoc A+ cell
to the trip:

| book | trips | PF | win |
|---|---|---|---|
| V1 post-hoc A+ cell @09:45 | 146 | 3.65 | 34% |
| **V2 @09:45** | **145** | **3.69** | **34%** |

The 1-trip gap is `WHLR 2024-09-10`, `chg_1d = 0.20` exactly — a `≥`-boundary float artifact
(the CSV stores 0.2; the live double is fractionally below). V2 armed ZERO days the post-hoc
filter rejected (a clean subset). Byte-faithful.

⚠ **The vol_slope trap (why the first V2 run showed only 29 trips):** the initial default
`MinVolSlope = 0.0` silently imposed a `vol_slope ≥ 0` skip that the F24 book NEVER had — it
burned every falling-volume drive (e.g. NVAX 2020-02-27, vs = −0.03, all real gates passing).
Fixed: `vol_slope` is now OFF by default (`MinVolSlope = -infinity`); `--min-vol-slope 0.025`
opts into the F15 A+ dial.

**The real book (full window [09:45, 10:30], vol_slope off, 2020–26):**

| yr | n | avg% | win | PF | net_k |
|---|---|---|---|---|---|
| 2020 | 100 | 9.4 | 38 | 3.20 | 94 |
| 2021 | 209 | 3.4 | 29 | 1.69 | 72 |
| 2022 | 71 | 5.6 | 37 | 2.46 | 40 |
| 2023 | 47 | 13.5 | 45 | 4.26 | 63 |
| 2024 | 82 | 23.8 | 37 | 5.49 | 196 |
| 2025 | 131 | 23.9 | 42 | 6.96 | 313 |
| 2026 | 62 | 5.2 | 23 | 2.03 | 32 |
| **all** | **702** | **11.5** | **35** | **3.52** | **810** |

**PF 3.52 / 702 tr / $810k, positive EVERY year (floor 1.69 in 2021).** This reconciles exactly
with the V1 "first-qualifying-bar-per-day" count (703 distinct days → PF 3.51) — V2 captures 702
of them. The F24 headline "PF 4.96 / 2,670 tr / $4.65M" was the SAME 702 drives counted at ~4
window-minutes each; **the honest book is PF 3.52 / 702 tr.** 2021 (the adverse meme-chop year)
is the most-populated year (209 tr) and the floor, as everywhere in this study.

## F2 — vol_slope is a U-shape, NOT a monotone edge; slicing it kills the all-weather property

Post-hoc vol_slope deciles on the real 702-trip book:

| decile | vol_slope range | n | avg% | win | PF |
|---|---|---|---|---|---|
| 1 (steepest fall) | [−0.14, −0.07] | 71 | 25.4 | 37 | **6.89** |
| 2 | [−0.072, −0.058] | 71 | 8.0 | 32 | 2.65 |
| 3 | [−0.057, −0.045] | 70 | 14.2 | 41 | 4.53 |
| 4 | [−0.045, −0.035] | 70 | 18.2 | 34 | 5.02 |
| 5 | [−0.035, −0.028] | 70 | 8.1 | 30 | 2.71 |
| 6 | [−0.028, −0.019] | 70 | 8.6 | 33 | 2.99 |
| 7 | [−0.019, −0.010] | 70 | 6.3 | 33 | 2.52 |
| 8 (flat, vs≈0) | [−0.010, 0.002] | 70 | 3.1 | 34 | **1.66** |
| 9 | [0.002, 0.023] | 70 | 8.1 | 29 | 2.35 |
| 10 (rising) | [0.023, 0.18] | 70 | 15.5 | 46 | 4.58 |

**U-shape:** both the steep-falling (dc1, PF 6.89) and rising (dc10, PF 4.58) tails beat the
flat-volume middle (dc8, PF 1.66). Falling volume on a pullback = healthy consolidation; rising
= fresh thrust; flat = the dead zone.

**But the tails don't survive per-year — the PF-6.89 cell is a fat-tail mirage:**

| cell | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | PF | n |
|---|---|---|---|---|---|---|---|---|---|
| vs ≤ −0.07 (dc1) | 2.09 | 3.49 | **0.31** | 7.17 | 27.60 | 8.31 | **0.07** | 6.89 | 63 |
| U-edges (≤−0.07 or ≥0.025) | 5.68 | 3.72 | **0.65** | 5.19 | 16.98 | 8.86 | **1.27** | — | 148 |

dc1's 6.89 is carried by 2024 (PF 27.6, avg +82.8% on 10 tr) and 2025; it's **losing in 2022
(0.31) and 2026 (0.07)**, thin (6–12 tr/yr). As a hard GATE, `vs ≥ 0` CUTS the full book from
PF 3.52 / $810k → 3.21 / $169k (it discards dc1–dc7, most of which are PF > 2.5).

**Verdict: keep vol_slope OFF by default.** The full 702-trip book is the trustworthy, all-weather
book; every vol_slope slice buys aggregate PF at the cost of the per-year robustness. (This
qualifies the V1 F15 "vol_slope ≥ 0.025 A+ dial": that cell has a genuinely high win rate (48%),
but it is a high-quality SUB-slice, not a filter that improves the book on net or per-year.)
