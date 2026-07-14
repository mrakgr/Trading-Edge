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

**But the tails don't survive per-year:**

| cell | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | PF | n |
|---|---|---|---|---|---|---|---|---|---|
| vs ≤ −0.07 (dc1) | 2.09 | 3.49 | **0.31** | 7.17 | 27.60 | 8.31 | **0.07** | 6.89 | 63 |
| U-edges (≤−0.07 or ≥0.025) | 5.68 | 3.72 | **0.65** | 5.19 | 16.98 | 8.86 | **1.27** | — | 148 |

dc1's 6.89 is carried by 2024 (PF 27.6, avg +82.8%) and 2025; it's **losing in 2022 (0.31) and
2026 (0.07)**, thin (6–12 tr/yr). As a hard GATE, `vs ≥ 0` CUTS the full book from PF 3.52 / $810k
→ 3.21 / $169k (it discards dc1–dc7, most of which are PF > 2.5).

**The decisive test — is dc1's PF real, or a few lottery winners?** (Every cell has a NEGATIVE
median return — all −4.9%, dc1 −5.1%: this is a fat-tailed book where the median trade LOSES and
the mean is winner-driven everywhere. So the question is winner *concentration*.)

| cell | full PF | drop top-1 | drop top-3 | top-1 = % of gross profit | top-3 = % of gross |
|---|---|---|---|---|---|
| **all (702)** | 3.52 | 3.37 | 3.14 | **4%** | **11%** |
| dc1 (vs≤−0.07, 83) | 5.85 | 4.87 | 3.64 | **17%** | **38%** |
| dc10 (vs≥0.025, 65) | 4.83 | 4.22 | 3.24 | 13% | 33% |

**The full book is winner-DIVERSIFIED and robust** — no single trade > 4% of gross profit; dropping
the 3 biggest of 702 only moves PF 3.52 → 3.14. **The vol_slope tail cells are lottery-skewed and
fragile** — in dc1 a SINGLE trade (HOLO 2024-02-07, +363%, a sub-$5 meme runner) is 17% of gross
profit; 3 trades are 38%. Strip those 3 and dc1 falls to 3.64, dc10 to 3.24 — **indistinguishable
from the full book's stripped 3.14.** The "U-shape edge" is 3–4 microcap jackpots deep, not a
repeatable property of volume slope.

**Verdict (raw): vol_slope has no robust RAW edge; keep it OFF by default.** But see F3 — the +50%
clip splits the two tails apart and the picture is more nuanced than "all lottery."

## F3 — under the +50% clip, the U-shape is ASYMMETRIC: rising-vol is real, falling-vol is jackpots

Re-running the deciles with each trade capped at +0.50 (`least(ret_moc, 0.50)`) — the standard
fat-tail-neutralised view:

| decile | vol_slope | n | win | PF raw | **PF clip** |
|---|---|---|---|---|---|
| 1 (steep fall) | ≤ −0.073 | 71 | 37 | 6.89 | **2.79** |
| 2–7 (mild fall) | −0.072…−0.010 | 420 | 30–41 | 2.5–5.0 | 1.4–2.5 |
| 8 (flat, vs≈0) | −0.010…0.002 | 70 | 34 | 1.66 | 1.61 |
| 9 | 0.002…0.023 | 70 | 29 | 2.35 | 1.23 |
| 10 (rising) | ≥ 0.023 | 70 | 46 | 4.58 | **2.81** |

The **U-shape survives the clip** — dc1 (2.79) and dc10 (2.81) are still the two best deciles, both
well above the flat middle (dc8/9 ≈ 1.2–1.6). So the tails do hold more *repeatable* winners, not
just bigger ones. But the threshold cuts reveal the two tails are NOT the same kind of edge:

| cut | n | win | PF raw | **PF clip** |
|---|---|---|---|---|
| all | 702 | 35 | 3.52 | 1.92 |
| vs ≥ 0 | 148 | 36 | 3.21 | 1.89 |
| vs ≥ 0.01 | 107 | 41 | 4.37 | 2.43 |
| **vs ≥ 0.025 (rising, F15 dial)** | 65 | **48** | 4.83 | **2.94** |
| **vs ≤ −0.07 (falling, dc1)** | 83 | **34** | 5.85 | **2.46** |

- **Rising-vol (`vs ≥ 0.025`) has a real per-trade QUALITY lift:** clip PF 2.94 vs the book's 1.92,
  win rate 35% → **48%**. The clip strips its lottery component and the win-rate improvement is
  genuine — rising volume into the drive is a real quality signal, not a fat-tail artifact. BUT
  per-year clip is NOT all-weather: 2022 clip PF **0.82** (14% win) and 2026 **1.20**, and the cell
  is thin (65 tr; 3–9/yr). So it lifts win rate and clip PF on average but does NOT produce a robust
  standalone book — a quality TILT, not a filter.
- **Falling-vol (`vs ≤ −0.07`) is JACKPOTS:** clip PF 2.46 but win rate **34%** = the SAME as the
  book. Its raw 5.85 was almost all the HOLO/ICCT/PHUN jackpots (top-1 = 17% of gross, F2); under
  the clip it's just an average-win-rate cell with fatter-than-average tails. No repeatable edge.

**Corrected verdict:** the U-shape is asymmetric under the clip. **Rising-vol (`vs ≥ 0.025`)** carries
a genuine win-rate lift (35%→48%, clip PF 2.94) but is thin and breaks in 2022/2026 — a quality tilt,
not an all-weather standalone book. **Falling-vol is a lottery mirage** (clip win rate flat at 34%).
Keep vol_slope OFF as the DEFAULT (the full 702-tr book is the all-weather base). `--min-vol-slope
0.025` is a legitimate quality overlay to reach for when a higher win rate matters, but it does not
survive as a hard filter on its own (thin + 2022/2026 clip < 1.2). This PARTIALLY rehabilitates the
F15 dial (the win-rate signal is real) while confirming F2's core point (it is not a robust filter).

## F4 — the 09:45–09:59 book (hand the tail to the other momentum systems)

Restricting the arm window to the first 15 minutes (`--entry-start-min 585 --entry-end-min 599`) so
the 10:00+ entries go to the other momentum systems (DipRiderV4 etc.). Now reporting NET alongside
raw+clip PF:

| yr | n | avg% | win | PF raw | PF clip | net_k |
|---|---|---|---|---|---|---|
| 2020 | 72 | 12.6 | 38 | 3.81 | 2.28 | 91 |
| 2021 | 135 | 4.8 | 33 | 1.95 | 1.44 | 65 |
| 2022 | 54 | 4.3 | 37 | 2.09 | 2.00 | 23 |
| 2023 | 30 | 13.7 | 37 | 3.65 | 2.10 | 41 |
| 2024 | 59 | 32.7 | 37 | 7.19 | 2.44 | 193 |
| 2025 | 106 | 26.8 | 47 | 8.35 | 3.91 | 284 |
| 2026 | 41 | 5.7 | 22 | 2.09 | **0.98** | 23 |
| **all** | **497** | **14.5** | **37** | **4.16** | **2.17** | **719** |

**Cutting the tail is nearly free — and improves quality-per-trade.** vs the full [09:45,10:30] book
(702 tr / raw 3.52 / clip 1.92 / $810k): the early window keeps **89% of the net ($719k) with 71%
of the trips**, and clip PF RISES 1.92 → **2.17** (net/trip $1,154 → $1,447). The 10:00–10:30 entries
were adding trips at lower profit-per-trip (the F24 time-decay) — handing them off costs almost
nothing. All-weather on raw PF (2021 floor 1.95); clip-positive every year except 2026 (clip 0.98 ≈
breakeven, 41 tr, partial year through June).

**vol_slope cuts on the early book (net + clip):**

| cut | n | win | PF raw | PF clip | net_k |
|---|---|---|---|---|---|
| all | 497 | 37 | 4.16 | 2.17 | 719 |
| vs ≥ 0 | 96 | 41 | 4.02 | 2.22 | 157 |
| **vs ≥ 0.025** | 54 | **50** | 5.38 | **3.12** | 104 |
| vs ≤ −0.07 | 77 | 32 | 4.68 | 2.15 | 130 |

Same F3 verdict, now with net: rising-vol (`vs≥0.025`) is the quality cell (win 50%, clip 3.12) but
keeps only **$104k of $719k (14%)** — a real tilt you'd pay 86% of the net for. Falling-vol (clip
2.15 = the book, win 32% < book) stays a jackpot cell. **Trade the full early book (no vol cut):
$719k / clip 2.17 / all-weather.**

## F5 — at the 09:59 bucket, WAIT for the 9-EMA downtick; do NOT enter on a monotone-up stock

Studied on the V1 post-hoc recorder (`od_modern_semalow.csv`, every bar present), restricted to the
**09:59 bucket** (the last bar of the [09:45,10:00) early window) within the day-strength book
(`chg_1d≥0.20 & chg_3d∈[0,1.5] & log_atr≥0.013 & stop_dist≥0.03`). At 09:59 the max `bl`
(bars-since-9-EMA-session-low) = 29: **`bl=29` means the 9-EMA made its low at the first bar (~09:30)
and NEVER made a new low in the 29 bars since** — the "up every 9-EMA tick, get in right away" cohort.
The question: enter immediately on monotone-up, or wait for a downtick (bl<29 / bh≥1)?

| cohort | n | avg% | win | PF raw | PF clip | net_k |
|---|---|---|---|---|---|---|
| ALL (book) | 813 | 9.2 | 43 | 2.70 | 1.74 | 745 |
| **bl=29 monotone-up (enter now)** | 293 | 4.2 | 43 | **1.79** | **1.40** | 124 |
| **bl<29 down-ticked (wait)** | 520 | 11.9 | 43 | **3.20** | **1.93** | 621 |
| bh=0 chasing high | 340 | 7.9 | 43 | 2.27 | 1.42 | 270 |
| bh≥1 pullback | 473 | 10.1 | 44 | 3.09 | 2.04 | 476 |
| bl=29 & bh=0 | 119 | 4.4 | 45 | 1.78 | 1.43 | 53 |
| bl=29 & bh≥1 | 174 | 4.1 | 42 | 1.80 | 1.37 | 71 |

**Waiting for the downtick wins decisively — and it's a MAGNITUDE story, not a win-rate one** (all
cohorts win ~43%). The monotone-up `bl=29` cohort is the WORST cell: clip PF 1.40 & avg +4.2%/tr vs
the down-ticked `bl<29` at clip 1.93 & +11.9%/tr — **~2.8× the profit per trade for waiting** ($1,194
vs $423/tr). A stock that has ticked its 9-EMA up for 30 straight bars into 09:59 has spent its
thrust (extended/exhausted); the average winner is small. Names that pulled back at least once have
room left to run. The `bl=29 & bh` splits confirm the exhaustion is structural: `bl=29 & bh=0` (1.78)
and `bl=29 & bh≥1` (1.80) are BOTH bad — once it's climbed monotonically, a late tiny pullback doesn't
rescue it.

**Per-year (raw/clip PF):**

| cohort | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|
| bl=29 monotone-up (clip) | 1.68 | 1.28 | 1.24 | **0.77** | 1.59 | 1.69 | 1.28 |
| bl<29 down-ticked (clip) | 2.30 | **1.04** | 1.67 | 2.52 | 2.32 | 3.45 | 2.14 |

Monotone-up is weak/mediocre EVERY year (clip ≤ 1.69, losing in 2023). Down-ticked dominates in 6 of
7 (clip 1.67–3.45); the sole exception is **2021** (the meme-chop year, clip 1.04 vs 1.28 — pullbacks
kept failing, 34% win) but both are soft there. **Verdict: the `bl<15` freshness cut (F23/V1) already
encodes half of this — it drops the extended pile — but F5 sharpens WHY: monotone-up = exhausted. Enter
on the pullback, never on the stock that's been green every tick.**

## F6 — `bh ≥ 1` only vs the V2 default (`bl < 15 & bh ≥ 1`): quantity+robustness vs quality/trade

Head-to-head on the early [09:45, 10:00) window: the V2 default's timing gate is `bl<15 & bh≥1`;
this drops the `bl<15` freshness gate and keeps only the pullback requirement (`--bl-max 0`).

| system | n | avg% | win | PF raw | PF clip | net_k |
|---|---|---|---|---|---|---|
| V2 default (bl<15 & bh≥1) | 497 | 14.5 | 37 | 4.16 | 2.17 | 719 |
| **bh ≥ 1 only** | 1088 | 10.6 | 41 | 3.22 | 1.87 | 1154 |

**Per-year (clip PF / net_k):**

| yr | default clip | default net | bh-only clip | bh-only net |
|---|---|---|---|---|
| 2020 | 2.28 | 91 | 2.04 | 111 |
| 2021 | 1.44 | 65 | 1.17 | 85 |
| 2022 | 2.00 | 23 | 2.03 | 98 |
| 2023 | 2.10 | 41 | 1.84 | 69 |
| 2024 | 2.44 | 193 | 2.16 | 289 |
| 2025 | 3.91 | 284 | 2.78 | 383 |
| 2026 | **0.98** | 23 | **1.57** | 119 |

**Neither strictly dominates — a real quantity+robustness vs quality/trade trade-off:**
- **bh≥1 only wins net + robustness:** $1,154k vs $719k (+60% on 2.2× trips), higher win rate
  (41 vs 37), and it is clip-positive EVERY year — floor **1.17 (2021)**, and 2026 clip **1.57**
  vs the default's near-breakeven **0.98**. The default's edge is concentrated in 2024–25.
- **V2 default wins quality/trade:** clip PF 2.17 vs 1.87, avg +14.5% vs +10.6%, net/trip $1,447 vs
  $1,061. The `bl<15` gate concentrates into fewer, better drives (F5: it drops the exhausted
  monotone-up pile).

**Concentration check (both are diversified — the default's quality edge is NOT jackpots):**

| system | top-1 | top-3 | top-10 % of gross | clip PF drop-top-10 |
|---|---|---|---|---|
| default | 5% | 13% | 29% | 2.17 → 1.95 |
| bh≥1 only | 4% | 9% | 20% | 1.87 → 1.77 |

Neither is lottery-driven (cf. F2's 17–38% tail cells). The default's clip 2.17 is a real per-trade
edge (survives dropping the top-10), just slightly more concentrated (fewer trips → each winner
weighs more).

**Verdict:** by the F2 principle (prefer the diversified, all-weather book), **`bh≥1`-only has the
stronger case for the production default** — more net, higher win rate, clip-positive every year
(incl. the soft 2026), maximally diversified. The `bl<15` gate buys genuine quality-per-trade (clip
2.17, +14.5%/tr) with no jackpot risk, but costs $435k of net and a fragile 2026 — keep it as an
optional SELECTIVITY dial, not the default.

## F7 — the day-strength-only book (no bl/bh): the full timing ladder

Completing the timing-gate ladder — day-strength only (`chg_1d≥0.20 & chg_3d∈[0,1.5] & log_atr≥0.013
& stop_dist≥0.03`, both bl AND bh disabled), early [09:45,10:00):

| system | n | avg% | win | PF raw | PF clip | net_k |
|---|---|---|---|---|---|---|
| default (bl<15 & bh≥1) | 497 | 14.5 | 37 | 4.16 | 2.17 | 719 |
| bh≥1 only | 1088 | 10.6 | 41 | 3.22 | 1.87 | 1154 |
| **day-strength only** | 1362 | 8.8 | 40 | 2.77 | **1.67** | **1204** |

**day-strength only, per-year (clip PF / net_k):** 2020 2.03/133 · **2021 1.01/66** · 2022 1.72/101 ·
2023 1.72/70 · 2024 1.97/323 · 2025 2.43/380 · 2026 1.62/131. Diversified (top-10 = 18% of gross).

**The ladder trades net for quality/trade and 2021 robustness at each rung:**
- **day-strength → +bh≥1:** removing 274 marginal trips (1362→1088) COSTS only $50k net ($1204→$1154)
  but lifts clip PF 1.67→1.87 and firms 2021 from **1.01 (breakeven) → 1.17**. Those bh-filtered-out
  trades are ~break-even clipped — `bh≥1` is the best-value gate (near-free quality + 2021 firming).
- **+bh≥1 → +bl<15 (the default):** a further $435k for clip 1.87→2.17 and +14.5%/tr, but 2026 goes
  fragile (F6).

**2021 is the discriminator:** day-strength-alone washes out in the meme-chop year (clip 1.01); each
timing gate rescues it (bh≥1 → 1.17, +bl<15 → 1.44). Raw day-strength thrust is not enough in an
adverse regime — the pullback/freshness structure is what carries 2021. **Production recommendation
stands (F6): `bh≥1` only** — it captures nearly all the net ($1154k of the $1204k ceiling) while
being the gate that most cheaply firms the adverse year.

## F8 — 09:45 premarket-inclusive RVOL (`rvol_0945`) is an INVERTED-U: [2,3)× is the sweet spot, [5,10)× a graveyard

`rvol_0945` = premarket-inclusive cumulative volume 04:00→09:45 ÷ 20d avg DAILY volume (the
candidate-table field, `vol_0945_pm / avgvol20`; `rvol_0945 = 2` means the stock did 2× a normal
full day's volume by 09:45). Joined onto the day-strength early book (no bl/bh; `chg_1d≥0.20 &
chg_3d∈[0,1.5] & log_atr≥0.013 & stop_dist≥0.03`, [09:45,10:00), 2020–26, 1362 tr; median rvol 4.1×).

| rvol_0945 | n | avg% | win | PF raw | PF clip | net_k |
|---|---|---|---|---|---|---|
| [1, 1.5) | 286 | 5.9 | 48 | 2.76 | 2.02 | 170 |
| [1.5, 2) | 135 | 7.7 | 39 | 2.90 | 1.80 | 104 |
| **[2, 3)** | 149 | **14.8** | **54** | **5.23** | **2.83** | 221 |
| [3, 5) | 166 | 9.0 | 41 | 2.86 | 2.17 | 150 |
| **[5, 10)** | 156 | 3.1 | **27** | 1.44 | **0.88** | 48 |
| [10, ∞) | 470 | 10.9 | 36 | 2.78 | 1.48 | 512 |

**NOT monotone — an inverted-U.** More premarket volume helps up to ~3×, then turns TOXIC in the
5–10× band, then the extreme tail (≥10×, the mega-gappers) partially recovers.

- **⭐ [2, 3)× = the sweet spot:** clip PF 2.83, win **54%**, avg +14.8%/tr — the single best RVOL cell.
  A stock that's done 2–3× its full ADV by 09:45 = strong genuine participation, not yet blown off.
  **All-weather** (clip-positive every year, floor 1.04 in 2022 / 1.15 in 2026; strong 2023–25 at
  3.7–5.3; 51% win even in 2021). Real edge (high win rate = not lottery).
- **⚠ [5, 10)× = a graveyard:** clip PF **0.88** (LOSING), win **27%** — the WORST cell. Robustly
  toxic: clip ≤ 1.08 in 6 of 7 years, outright losing in 2021 (0.63) / 2023 (0.00) / 2024 (0.61) /
  2026 (0.97). 5–10× ADV before 09:45 without being a mega-gapper = the drive is already spent by
  entry (exhaustion/blow-off). A genuine AVOID zone.

**A floor ALONE hurts** (it drags in the toxic 5–10× band): `rv≥1.5` clip 1.61, `rv≥2` 1.59, `rv≥3`
1.46, `rv≥5` 1.31 — monotonically WORSE than the full book's 1.67 as the floor rises. **Use a BAND,
not a floor.** The actionable cut is the `[2,3)` sweet spot (a sharp A+ overlay, clip 2.83/win 54%,
$221k) and/or EXCLUDE `[5,10)` (removing a clip-0.88 graveyard). This inverts the naive "more volume
= better" thesis: it's true only up to ~3×, then extreme early volume signals exhaustion.
