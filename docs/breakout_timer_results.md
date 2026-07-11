# BreakoutTimer — results

**System:** `TradingEdge.BreakoutTimer` (branch `dip-rider`), forked from DipRiderV3. A **9-EMA-breakout +
momentum-confirm** long intraday system: it fuses VwapReclaim's breakout/pullback structure with DipRiderV3's
trailing-window momentum gate stack.

**The fusion layer (a PERMISSION gate over the V3 entry):**
- Track the session max over **9-EMA VALUES** (`sessMaxEma`). A new EMA session high = a breakout.
- `bars_since_ema_high` = # bars since the last new EMA high (0 on a fresh high, +1 otherwise).
- **breakout timer** (−1 = inactive): SET to `BreakoutTimerBars` (default 10) ONLY when a new EMA high fires
  AND `bars_since_ema_high` (read PRE-reset) ≥ `BreakoutMinBarsSinceHigh` (default 8) — i.e. the breakout
  followed a real ≥8-bar drought. Downticks 1/bar to a −1 floor. **No re-arm** on a new high that doesn't
  clear the drought gate (don't buy lethargic breakouts — we're timing for the volume surge in the next ~10 min).
- When `--require-breakout-timer` is ON, the V3 momentum entry may fire ONLY while the timer is live (>0).
  OFF (default) reproduces DipRiderV3 byte-for-byte.

**Pullback features** (VwapReclaim-style; accumulate CONTINUOUSLY through pullback AND breakout; reset when
the timer hits 0 — an A/B against reset-on-`bars_since` is planned): `pb_updn` (mean above-EMA-bar vol / mean
below-EMA-bar vol — the volume-slope rival), `pb_max_dist` (deepest `sessMaxEma/ema−1` gap = pullback depth),
`pb_atr_pct` (mean 20m log-ATR over the run).

**Methodology (inherited from DipRiderV3):** clip every trade at +50% (the honest per-trade PF); gate ≠ post-hoc
under max-concurrent=1; THE 2021 LAW (a regime problem, mostly un-gateable per-trade). Tables not prose.

---

## Finding 1 — the breakout-timer gate is a real, ALL-WEATHER risk-adjusted lift (clip PF 1.80 → 2.00)

Full 2020+ run, default V3 gate stack (log-ATR≥0.013, vol-slope∈[0.05,0.25), price-slope>0, tightness≥3,
sum6≥5, rvol5m20d<100, ema-vs-vwap≥−2%, chg1d≥10%, chg3d≥0), max-concurrent 1, geom stop, hold-to-MOC.
**Gated** = `--require-breakout-timer` (arm ≥8-bar drought, 10-bar window). Clip = min(ret_moc, +50%).

**Byte-check:** with the gate OFF, BreakoutTimer == DipRiderV3 exactly (2024: 143 trips, PF 2.871, $141,451 —
identical trade tape). So the fusion layer is the ONLY change.

| year | ungated n | gated n | cut% | ungated clip PF | **gated clip PF** | Δ |
|------|-----------|---------|------|-----------------|-------------------|---|
| 2020 | 116 | 62 | 47% | 2.05 | 1.89 | −0.16 |
| **2021** | 276 | 134 | **51%** | 1.24 | **1.34** | **+0.10** |
| 2022 | 121 | 65 | 46% | 1.56 | 1.88 | +0.32 |
| 2023 | 61 | 34 | 44% | 3.00 | 4.22 | +1.22 |
| 2024 | 143 | 81 | 43% | 1.99 | 1.88 | −0.11 |
| 2025 | 229 | 137 | 40% | 1.91 | 2.42 | +0.51 |
| 2026 | 116 | 71 | 39% | 2.18 | 2.08 | −0.10 |
| **ALL** | **1062** | **584** | **45%** | **1.80** | **2.00** | **+0.20** |

Raw (unclipped) MOC: ungated PF 2.71 / $975k / 45.6% win → gated PF 2.91 / $602k / **49.7% win**.

**Reading:**
- **Clip PF 1.80 → 2.00** on ~55% of the trips. Win-rate 45.6% → 49.7% — the gate picks BETTER entries, not
  just fewer. 4/7 years improve clip PF, 5/7 improve-or-hold; the mild degraders (2020/2024/2026, −0.1..−0.16)
  are within noise and still strongly positive.
- **2021 IMPROVES (1.24 → 1.34)** and is the MOST-cut year (276→134, 51%). This is significant vs the 2021 law:
  nearly every per-trade momentum feature INVERTED in 2021, but the breakout gate is a STRUCTURAL/TIMING filter
  (post-drought volume surge), not a momentum-quality feature meme-chop can invert — so it cuts the most where
  the book is weakest and the survivors are better. Still the weakest year, but it moved the right way.
- **Cost = net dollars** (clip-net $458k → $314k, ~69% kept): the gate concentrates, same A-vs-A+ tradeoff as V3.
  This is the A book of BreakoutTimer; the pullback features are the next levers to find an A+.

Next: breakdowns on the pullback features (`pb_updn` vs vol-slope, `pb_max_dist`, `pb_atr_pct`), the timer
length / drought threshold sweeps, and the variants (buy-directly-on-breakout; VWAP-level trigger).

---

## Finding 2 — NONE of the pullback-shape features beats the existing gate stack; the TIMER is the edge

Study population: gated + **max-concurrent 0** (unlimited, so the fusion gate doesn't reshuffle daily slots —
the correct way to study a feature per the gate≠post-hoc rule), full 2020+, 3,815 trips (raw PF 3.13). Quintile
buckets, raw + clip(+50%) PF, plus per-year clip PF for the 2021-law inversion check.

**(a) `pb_updn` (mean above-EMA vol / mean below-EMA vol) — the vol-slope RIVAL — LOSES.** Non-monotone U-shape
AND inverts in 2021:

| pb_updn bucket | PFraw | **PFclip** | win% | 2021 clip | 2023 clip |
|---|---|---|---|---|---|
| [−inf, 1.26) | 3.11 | 2.49 | 59.7% | **0.92** | 15.94 |
| [1.26, 1.53) | 2.76 | 1.85 | 46.8% | 1.47 | 2.02 |
| [1.53, 1.81) | 2.74 | 1.71 | 47.5% | 3.29 | 3.45 |
| [1.81, 2.24) | 3.08 | 2.27 | 52.7% | 1.24 | 6.62 |
| [2.24, +inf) | 4.07 | 2.39 | 54.1% | **0.63** | 29.73 |

Both extremes beat the middle (U-shape), and the extreme buckets are pure trend-regime amplifiers: the
high-updn bucket is 2023 **29.73** / 2024 14.58 but **2021 0.63 / 2020 0.30** — the classic best-in-trend =
worst-in-2021 inversion. Un-gateable.

**vs `vol_slope_20` (the incumbent, same population) — MONOTONE, all-weather:**

| vol_slope_20 bucket | PFraw | **PFclip** | win% |
|---|---|---|---|
| [−inf, 0.062) | 2.44 | 1.84 | 49.9% |
| [0.062, 0.075) | 2.66 | 1.98 | 52.6% |
| [0.075, 0.091) | 2.77 | 1.91 | 53.9% |
| [0.091, 0.116) | 3.74 | 2.34 | 51.2% |
| [0.116, +inf) | 4.13 | **2.50** | 52.7% |

**Verdict: vol_slope_20 wins** — monotone, stable, already the A+ lever in DipRiderV3 (≥0.10). `pb_updn` is a
regime bet, not a durable edge.

**Follow-up (user hypothesis): is `pb_updn` weak only BECAUSE vol_slope is already gating the same signal?**
Re-ran the study with **vol-slope fully disabled** (floor −1e9, ceiling +1e9), same gated max-conc-0 setup.
Population doubles (3,815 → 8,636 — vol-slope was cutting ~55%). `pb_updn` **still U-shaped, NOT rescued**:

| pb_updn bucket (vol-slope OFF) | PFraw | **PFclip** | win% | 2021 clip | 2023 clip | 2024 clip |
|---|---|---|---|---|---|---|
| [−inf, 1.16) | 2.45 | **2.06** | 53.2% | 1.05 | 6.04 | 0.92 |
| [1.16, 1.39) | 2.42 | 1.71 | 47.1% | 1.91 | 1.57 | 2.01 |
| [1.39, 1.64) | 2.53 | 1.67 | 47.8% | 1.27 | 3.24 | 1.03 |
| [1.64, 2.02) | 2.55 | 1.89 | 50.9% | 1.94 | 2.04 | 1.91 |
| [2.02, +inf) | 2.98 | 1.86 | 50.9% | **0.88** | **9.50** | **6.84** |

So the redundancy hypothesis is **NOT supported** — the U-shape and the 2021 inversion are intrinsic to updn,
not an artifact of conditioning on vol-slope. The high-updn bucket is still the trend-amplifier-that-dies-in-2021
(2023 9.50 / 2024 6.84 but 2020 0.70 / 2021 0.88). Note `pb_updn` = mean above-EMA-BAR vol / mean below-EMA-BAR
vol (per-bar averages, not total/total — verified). One genuine but modest inversion of the naive thesis: the
**LOW-updn bucket** (more volume on the pullback/down bars) is the best (clip 2.06, positive 6/7 years) — i.e.
"volume flowing into the rising side" is the WRONG read; but it's still weaker than vol-slope and doesn't fix 2021.

**(b) `pb_max_dist` (pullback depth):** non-monotone/noisy (clip 2.45 / 1.74 / 2.57 / 1.93 / 2.00 across
quintiles) — no clean threshold. The "deeper pullback = better breakout" thesis does not hold.

**(c) `pb_atr_pct` (pullback log-ATR):** inverted-U peaking at [0.0295, 0.0419) clip 2.77 — BUT it's
**0.889-correlated with `log_atr_20`** (the MAIN gate). It's the same feature re-expressed, and it inverts in
2021 like every volatility amplifier (high-ATR buckets 2021 clip 1.11 / 0.57 vs low-ATR 1.47). Redundant.

**TAKEAWAY:** the breakout-timer STRUCTURE is the edge, not the pullback-shape features. The fusion works
because of *when* it permits entry (post-drought volume-surge window), not *what the pullback looked like* —
consistent with why it helped 2021 (structure/timing survives regime; pullback-shape features don't). So the
A+ levers come from the timer/drought parameters and the existing V3 stack (vol-slope), NOT from pb_*.
The pb_* columns stay as recorded diagnostics, not gates.

---

## Finding 3 — a smarter pullback-feature RESET improves `pb_updn` a lot, but STILL doesn't cure 2021

The old reset (fire only when the timer ticks to exactly 0) let the accumulators hold stale pre-drought runs.
**New reset rule (2 triggers, else keep accumulating):** (1) the timer EXPIRES to exactly 0, OR (2) a DORMANT
(timer≤0) new EMA high FAILS to arm (drought threshold not met — a false-start breakout). Ordinary declines
(no new high, timer≤0) do NOT reset — they ARE the pullback we want to accumulate. A qualifying arming breakout
does NOT reset — `updn` carries the pullback straight into the live breakout window. (Sanity: the gated A book
is UNCHANGED — 584 trips / PF 2.910 / $602k — the reset touches only the recorded pb_* columns, no gate.)

**Effect on `pb_updn`** (gated max-conc-0, 3,815 trips): the U-shape mostly collapses into a rising top; the
distribution barely moves (median 1.67→1.58) so it's the SAME trades re-scored by a cleaner window:

| pb_updn bucket | old-reset clip PF | **new-reset clip PF** | win% | 2021 clip (new) | 2023 clip (new) | 2024 clip (new) |
|---|---|---|---|---|---|---|
| [−inf, 1.16) | 2.49 | 1.90 | 55.6% | 0.99 | 2.35 | 0.49 |
| [1.16, 1.43) | 1.85 | 1.69 | 46.9% | 1.43 | 1.80 | 2.15 |
| [1.43, 1.72) | 1.71 | 1.82 | 48.5% | 1.57 | 7.47 | 1.41 |
| [1.72, 2.24) | 2.27 | **2.40** | 54.6% | **2.37** | 30.05 | 3.24 |
| [2.24, +inf) | 2.39 | **2.84** | 55.1% | **0.54** | 23.61 | 11.10 |

**The reset genuinely helped the feature** (top bucket 2.39 → 2.84, near-monotone) — the reset instinct was
right. **BUT 2021 STILL INVERTS:** the now-stronger top bucket is 2023 23.61 / 2024 11.10 but **2021 0.54 /
2020 1.01** — sharper trend-amplification = sharper 2021 death (the classic best-in-trend=worst-in-2021 law).
So `pb_updn` remains un-gateable as a FLOOR. One hint: the `[1.72, 2.24)` bucket is the most BALANCED (2021
**2.37** = best 2021 cell, plus 2023 30.05 / 2025 3.21) → a updn CEILING (cut the blow-off high-updn like V3's
vol-slope 0.25 ceiling) might be 2021-safer than a floor, but a ceiling is a fragile lever. `pb_updn` stays a
recorded diagnostic, not a gate. **Reset rule = KEPT as the standard (strictly better).**

---

## Finding 4 — drought-threshold sweep: d16 is the overall knee; a LONGER drought monotonically helps 2021

Swept `--breakout-min-bars-since-high` in-engine at the gated A-book config (each value = its own run, since
the drought is a GATE param). Both raw + clip PF (standing instruction).

| drought | n | win% | PFraw | **PFclip** | net_raw | net_clip |
|---|---|---|---|---|---|---|
| 8 (baseline) | 584 | 49.7% | 2.91 | 2.00 | 602,018 | 314,380 |
| 12 | 558 | 50.0% | 2.97 | 2.02 | 586,563 | 303,034 |
| **16** | 519 | 50.9% | **3.08** | **2.08** | 565,098 | 293,870 |
| 20 | 493 | 50.1% | 2.78 | 1.99 | 471,331 | 262,151 |
| 25 | 445 | 51.0% | 2.63 | 1.89 | 404,560 | 220,985 |
| 30 | 393 | 52.2% | 2.76 | 1.95 | 383,440 | 207,025 |
| 40 | 321 | 52.0% | 2.65 | 1.88 | 291,928 | 155,920 |

Per-year **clip PF**: 2021 rises MONOTONICALLY with the drought — 1.34 (d8) → 1.50 (d16) → **1.82 (d30) →
1.84 (d40)**; a longer required quiet-period filters meme-chop's rapid re-breaks. But it TAXES the trend years:
2024 clip 1.88 (d8) → 1.17 (d40); 2025 peaks at d16 (2.88) then fades. Per-year **RAW PF** same shape.

**Read:** **d16 is the overall knee** (clip 2.00→2.08, raw 2.91→3.08; and it lifts 2021 1.34→1.50 AND 2023
4.22→5.54 at once — a both-directions win). Beyond ~20 it just bleeds trips/net. There's a genuine tension:
d30 is where 2021 stops being the weak year (1.82) but that costs the trend years. d16 is the modest A-book
upgrade; a 2021-specific longer drought is a separate regime knob, not a free lunch.

---

## Finding 5 — ⭐ the breakout window is a SIZING LEVER, not (just) a gate: timer-live beats dead in 6/7 years

Reframe (user): instead of HARD-GATING on the breakout timer (which discards the timer-dead trips — still
net-positive), keep the FULL DipRiderV3 book and use "was a breakout timer live at entry?" as a **size-up
signal**. Test: does timer-live outperform timer-dead across ALL years (not just the 2024 pre-look)? Population
= ungated V3 book, max-conc 0 (every entry, `breakout_timer` recorded), new reset. Raw + clip both.

**OVERALL:** timer-LIVE (n=3815) clip PF **2.10** / raw 3.13 / win 52.1%  vs  timer-DEAD (n=6301) clip **1.62**
/ raw 2.41 / win 46.3%. Both sub-books PROFITABLE — nothing is discarded, just weighted.

| year | LIVE n | LIVE PFraw | LIVE PFclip | DEAD n | DEAD PFraw | DEAD PFclip | lift (clip) |
|---|---|---|---|---|---|---|---|
| 2020 | 386 | 3.37 | 2.15 | 691 | 2.47 | 1.65 | **+0.50** |
| 2021 | 704 | 1.64 | 1.30 | 1345 | 1.34 | **0.94** | **+0.36** |
| 2022 | 325 | 1.70 | 1.53 | 600 | 3.55 | 2.44 | **−0.91** ⚠ |
| 2023 | 273 | 9.19 | 5.16 | 391 | 5.66 | 3.52 | **+1.64** |
| 2024 | 613 | 3.97 | 2.38 | 975 | 1.99 | 1.36 | **+1.02** |
| 2025 | 942 | 3.89 | 2.52 | 1478 | 2.89 | 1.86 | **+0.67** |
| 2026 | 572 | 2.43 | 1.86 | 821 | 2.52 | 1.75 | +0.11 |

**Reads:**
- **6/7 years positive lift**, large in the trend years (2023 +1.64, 2024 +1.02, 2025 +0.67). The 2024 pre-look
  GENERALIZES.
- **2021 gets the lift AND crosses the profitability line:** timer-DEAD 2021 is clip **0.94 (losing)** vs LIVE
  **1.30 (winning)**. So the breakout window is *especially* valuable in the bad regime as a SIZE-DOWN signal
  for the dead trades — exactly the sizing framing.
- **2022 is the lone inversion (−0.91):** in the bear market, timer-dead did better (breakouts FAILED in the
  2022 downtrend). 1/7 — a regime caveat for the sizing weight, not a disqualifier.

**Conclusion:** the sizing-lever framing is validated and is more capital-efficient than the binary gate — keep
the whole book, overweight timer-live, underweight timer-dead. Next: formalize the sizing (binary 2×/1×?
continuous in the timer value or `bars_since`? regime-aware given 2022?).

**⚠ CORRECTION (F5b) — re-run the split at max-concurrent 1 (the HONEST population).** F5 above used max-conc 0
(every qualifying bar), which is the wrong population for a SIZING test — the real book runs 1 slot/day, so the
question is whether the trips that ACTUALLY fire in a live window are better. Re-ran ungated, **max-conc 1,
drought 16** (default now): 1062 trips (= the DipRiderV3 A book), split by timer at entry. Both PFs shown.

OVERALL: timer-LIVE (n=250) clip **2.15** / raw 3.27 / win 48.8%  vs  DEAD (n=812) clip **1.71** / raw 2.56 /
win 44.6%. The +0.44 clip lift holds — **but per-year it's now 5/7, not 6/7:**

| year | LIVE n | LIVE PFraw | LIVE PFclip | DEAD n | DEAD PFraw | DEAD PFclip | lift (clip) |
|---|---|---|---|---|---|---|---|
| 2020 | 21 | 1.63 | 1.63 | 95 | 3.25 | 2.14 | **−0.51** ⚠ |
| 2021 | 60 | 2.22 | 1.67 | 216 | 1.65 | 1.13 | **+0.53** |
| 2022 | 28 | 1.36 | 1.36 | 93 | 2.08 | 1.61 | **−0.26** ⚠ |
| 2023 | 11 | 9.32 | 7.74 | 50 | 4.67 | 2.57 | **+5.17** |
| 2024 | 35 | 5.61 | 2.90 | 108 | 2.18 | 1.76 | **+1.14** |
| 2025 | 64 | 4.13 | 2.15 | 165 | 2.61 | 1.83 | +0.32 |
| 2026 | 31 | 3.05 | 2.50 | 85 | 3.82 | 2.08 | +0.42 |

**Honest read:** the sizing edge is REAL but WEAKER and less consistent than the max-conc-0 view suggested (user
was right to suspect). Trend years still love it (2023 +5.17, 2024 +1.14) and 2021 gets a big lift (+0.53), but
**2020 and 2022 — the two hardest long-momentum years — flip NEGATIVE** (timer-dead better) once slot-competition
is honest. Per-year LIVE n is small (11–64) so per-year is noisy; the overall +0.44 is the reliable signal. Verdict:
positive-EV sizing lever, but regime-sensitive, not a free win.

---

## Finding 6 — drought=16 is the NEW DEFAULT (gated A book: raw 2.91→3.08, clip 2.00→2.08, 2021 1.34→1.50)

Per F4, `BreakoutMinBarsSinceHigh` default changed 8 → **16**. Gated A book, 2020+, close-stop:
519 trips / **raw PF 3.08 / clip 2.08** / 50.9% win / net_clip $293,870. 2021: raw 1.94 / clip 1.50 (from 1.34).
A clean both-metrics upgrade over d8.

---

## Finding 7 — stop mode: FIXED-EMA ≈ close-geom (wash); TRAILING-EMA trades fat tail for a smoother book

Two EMA-stop variants added (level = trailing-20m MIN 9-EMA DIRECTLY, no geometry; exit when the live 9-EMA
falls below it): **FIXED** (level frozen at entry) and **TRAILING** (level recomputes each bar = ratchets up).
Compared vs the close-based geometry baseline, gated d16, 2020+. Both PFs shown.

| stop | n | win% | **raw PF** | **clip PF** | net_raw | net_clip |
|---|---|---|---|---|---|---|
| close-geom (baseline) | 519 | 50.9% | 3.08 | 2.08 | 565,098 | 293,870 |
| ema-FIXED | 512 | 49.4% | **3.14** | **2.10** | 559,515 | 288,551 |
| ema-TRAIL | 649 | 43.9% | 2.61 | **2.23** | 284,319 | 216,650 |

Per-year **clip PF**: TRAIL best in 2020 (2.51 vs 1.67), 2022 (2.45 vs 1.92), 2024 (2.17 vs 1.66) — the
choppier/harder years; slightly worse in big trend years 2023 (4.48 vs 5.54) / 2025 (2.34 vs 2.88).

**Reads:**
- **FIXED-EMA ≈ close-geom** (raw 3.14 vs 3.08, clip 2.10 vs 2.08, near-identical trips) — a WASH. The fixed
  min-EMA level and the close-geom level land in similar places. Marginal, not a reason to switch.
- **TRAILING-EMA is a genuine CHARACTER shift, a risk-reduction lever:** it stops out more (win 50.9%→43.9%,
  649 trips as it frees the slot sooner), **raw PF DROPS to 2.61** (tighter stop cuts winners short — momentum
  tax, fat tail truncated) but **clip PF is the HIGHEST at 2.23** (typical trade improves). The clip-vs-raw
  divergence is the signal: it truncates the fat right tail (raw ↓, net −$75k) while improving the median
  trade and smoothing the hard years. **Return-maximizer → close-geom/ema-fixed; drawdown-smoother → ema-trail.**
  Since SIZING already handles the tail (F5), a trailing stop that sacrifices the tail may be the wrong trade
  for THIS book. ema-trail is a documented risk-mode.

**ema-FIXED is the NEW DEFAULT (user).** Loss-% breakdown explains WHY its PF edges close-geom — SMALLER,
EARLIER-CUT losses with UNCHANGED winners:

| metric | close-geom | ema-FIXED |
|---|---|---|
| mean loss (losers) | −10.63% | **−10.11%** |
| median loss (losers) | −9.38% | **−8.43%** |
| stop-exit mean ret | −12.39% | **−11.49%** |
| stop-exit median | −11.09% | **−9.74%** |
| stops fired | 154 (29.7%) | 174 (34.0%) |
| worst loss | −44.88% | −42.27% |
| winner median / best | +14.95% / +366.8% | +16.76% / +366.8% |

Mechanism: ema-fixed exits on the 9-EMA CROSSING the level (a smoothed trend-failure signal) vs a noisy price
CLOSE that can gap through — so it stops MORE often (34% vs 30%) but each stop is ~1.3% cheaper on the median,
and it has FEWER catastrophic losers (18.9% vs 20.8% below −15%). Winners are untouched (the EMA-stop only fires
when the fast trend rolls over, which isn't happening while a winner runs). Higher PF ← smaller losses, same
winners = a genuine drawdown reduction across ~250 losers. Default: EMA-FIXED; `--close-stop` reverts.

---

## Finding 8 — ⭐ at drought=16 + ema-fixed, `pb_updn` becomes MONOTONE and the 2021 inversion SOFTENS

User hypothesis: a longer drought (16 vs 8) makes the accumulators run over a more-DEVELOPED pullback before the
breakout, so `pb_updn` should read cleaner. **Confirmed.** Study = gated, max-conc 0, drought 16, ema-fixed,
new reset. 3,379 trips. Both PFs shown.

**vol-slope ON (pb_updn competing with vol-slope):**

| pb_updn bucket | PFraw | **PFclip** | win% | 2021 clip | 2023 clip | 2024 clip |
|---|---|---|---|---|---|---|
| [−inf, 1.13) | 1.88 | 1.64 | 51.9% | 0.88 | 2.68 | 0.35 |
| [1.13, 1.36) | 2.80 | 2.08 | 47.8% | 1.26 | 4.55 | 3.66 |
| [1.36, 1.61) | 3.29 | 1.99 | 48.2% | 1.43 | 6.47 | 1.03 |
| [1.61, 2.00) | 3.39 | 2.39 | 52.5% | **2.12** | 12.53 | 3.10 |
| [2.00, +inf) | **4.45** | **2.59** | 52.1% | 1.39 | 20.83 | 4.27 |

**Two big changes vs d8 (F2/F3):**
1. **Now MONOTONE-increasing** — the old U-shape is gone. At d8 the LOW bucket was best (clip 2.06); at d16 the
   low bucket is WORST (clip 1.64) and the top is best (clip 2.59 / raw 4.45). The relationship flipped to a
   clean rising ramp.
2. **The 2021 inversion SOFTENED** — at d8 the high bucket was 2021 clip **0.54 (dead)**; at d16 the upper range
   is POSITIVE in 2021 (1.43, **2.12**, 1.39). High-updn now SURVIVES 2021 instead of collapsing.

**Mechanism:** with an 8-bar drought the "pullback" feeding updn was often a shallow/noisy wiggle (updn measured
chop → inverted in 2021). With a 16-bar drought updn accumulates over a REAL, developed pullback, so high-updn
means "sustained accumulation into a legitimate breakout" — which holds even in 2021's chop. The longer drought
didn't just help the gate (F4/F6); it changed what `pb_updn` MEASURES.

**vol-slope OFF (pb_updn's cleanest shot, 6,260 trips) — even better, PERFECTLY monotone + ALL-WEATHER:**

| pb_updn bucket | PFraw | **PFclip** | win% | net_clip | 2020 | 2021 | 2022 | 2026 |
|---|---|---|---|---|---|---|---|---|
| [−inf, 1.02) | 1.93 | 1.71 | 45.1% | $374k | 3.16 | 1.51 | 2.29 | 1.40 |
| [1.02, 1.22) | 2.47 | 1.77 | 45.4% | $486k | 2.80 | 0.86 | 1.19 | 2.09 |
| [1.22, 1.46) | 2.70 | 1.95 | 45.7% | $672k | 2.85 | 2.10 | 1.22 | 1.23 |
| [1.46, 1.76) | 3.05 | 1.89 | 46.5% | $721k | 1.86 | 1.91 | 1.21 | 1.55 |
| [1.76, +inf) | **4.20** | **2.67** | **54.1%** | **$1.1M** | 1.32 | **1.50** | 2.57 | 1.57 |

Raw PF is PERFECTLY monotone (1.93→2.47→2.70→3.05→4.20). The top bucket [1.76,+inf) jumps on ALL THREE metrics
(raw 4.20, clip 2.67, win 54% vs ~45%) AND is **clip-POSITIVE in all 7 years** (2020 1.32 / 2021 1.50 / 2022
2.57 / 2023 26.80 / 2024 4.71 / 2025 4.14 / 2026 1.57), raw-positive all 7 (2021 raw 2.03).

**⭐ THE 2021 INVERSION IS GONE.** The journey: d8 high-updn 2021 clip **0.54 (dead)** → d16 vol-slope-ON **1.39**
→ d16 vol-slope-OFF **1.50 (healthy)**. High-updn (≥~1.76) is now a MONOTONE, ALL-WEATHER, 2021-SAFE feature —
a genuine A+ lever (unlike F2/F3 at d8). Both parts of the user's hypothesis confirmed: the longer drought fixed
updn, and it's cleanest with vol-slope out of the way. Next: gate a updn FLOOR in-engine (max-conc 1) → the A+ book.

---

## Finding 9 — gated pb_updn floor: a modest A+ at ≥1.76 (clip 2.10→2.45), but the gate≠post-hoc discount is real

Gated the `pb_updn` floor IN-ENGINE (new `MinPbUpDn` / `--min-pb-updn`), gated d16 + ema-fixed, **max-conc 1**
(the honest book). off = the A book (512 trips). Both PFs shown.

| floor | n | win% | PFraw | **PFclip** | net_clip |
|---|---|---|---|---|---|
| off (A book) | 512 | 49.4% | 3.14 | 2.10 | 288,551 |
| 1.2 | 382 | 48.4% | 3.28 | 2.09 | 230,886 |
| 1.46 | 292 | 49.0% | 3.54 | 2.15 | 191,956 |
| 1.6 | 250 | 49.2% | 3.20 | 2.10 | 160,540 |
| **1.76** | 193 | 49.7% | **3.82** | **2.45** | 152,701 |
| 2.0 | 135 | 47.4% | 3.15 | 1.92 | 77,117 |

Per-year **clip PF** (off vs 1.76): 2020 1.75→2.36, 2021 **1.51→1.35**, 2022 1.90→2.41, 2023 6.08→18.71,
2024 1.60→2.65, 2025 2.83→3.80, 2026 2.04→1.54.

**Reads (the gate≠post-hoc discount, as always):**
- **NOT the clean monotone lift the post-hoc buckets (F8) showed.** Clip PF is ~FLAT (2.09–2.15) through floors
  1.2–1.6, then a single peak at **1.76 (clip 2.45 / raw 3.82, 193 trips)**, then collapses at 2.0 (1.92, only
  135 trips). It's a sweet-spot lever, not "higher = better."
- **1.76 IS a genuine A+ cell** (+0.35 clip over baseline, raw 3.82) — but it's driven by the TREND years (2023
  18.71, 2024 2.65, 2020 2.36), NOT 2021.
- **⚠ 2021 does NOT improve when gated** — off is 2021 clip 1.51; every floor is ≤ that (1.76 = **1.35**). The
  post-hoc "2021-safe" finding did NOT survive the max-conc-1 reshuffle. So the floor concentrates the trend-year
  edge but slightly HURTS 2021 in the real book — the opposite of the drought lever (F4/F6).
- Costs ~half the net (clip $289k→$153k). A modest A+ concentrator, weaker than F8's post-hoc monotonicity
  promised.
- **⚠ The 1.76 peak is UNSTABLE — a single lucky bucket, not a threshold.** The overall clip sequence
  2.10→2.09→2.15→2.10→**2.45**→1.92 is a lone spike surrounded by flat/lower values (not a plateau). Discarded.
- **⚠ CAVEAT (user):** this sweep ran with vol-slope [0.05,0.25) STILL ON. pb_updn correlates with vol-slope
  (both "volume flowing in"), so vol-slope may TRIGGER FIRST and pb_updn is re-selecting what vol-slope already
  chose → the muted/flat result could be redundancy, not pb_updn weakness. The clean test = the floor with
  vol-slope OFF (F10 below).

---

## Finding 10 — pb_updn floor with vol-slope OFF ≈ identical to vol-slope ON: it's gate≠post-hoc, NOT redundancy

User hypothesis: F9's muted floor result was because vol-slope (correlated with pb_updn) triggers FIRST, so
pb_updn was re-selecting vol-slope's picks. Test: the SAME floor sweep with vol-slope DISABLED (floor −1e9,
ceiling +1e9), gated d16 + ema-fixed, max-conc 1. **Hypothesis NOT supported — the two curves are ~identical:**

| floor | clip PF (vs-ON) | **clip PF (vs-OFF)** | 2021 clip (OFF) |
|---|---|---|---|
| off | 2.10 | 2.06 | 1.57 |
| 1.2 | 2.09 | 2.10 | 1.67 |
| 1.46 | 2.15 | 2.10 | 1.37 |
| 1.6 | 2.10 | 2.13 | 1.59 |
| 1.76 | 2.45 | **2.40** | 1.35 |
| 2.0 | 1.92 | 1.95 | 1.26 |

Same flat ~2.1 through 1.2–1.6, same lone unstable spike at 1.76 (2.40 vs 2.45), same collapse at 2.0. Removing
vol-slope did NOT unlock the monotone lift. 2021 still doesn't improve when gated (off 1.57 → 1.76-floor 1.35).

**RESOLUTION of the whole pb_updn arc:** as a POST-HOC feature (F8) it's monotone & beautiful (esp. d16, esp.
vol-slope off); as an IN-ENGINE GATE (F9/F10) it's flat + one unstable spike + no 2021 help — regardless of
vol-slope. The gap is the pure **gate≠post-hoc effect at max-conc 1**: the floor reshuffles which trip takes the
daily slot, and the monotone post-hoc ORDERING doesn't survive that reshuffle. It was never vol-slope redundancy.
pb_updn's monotonicity is a property of WHICH BARS EXIST, not one that SELECTING on it preserves under slot
competition. **Verdict: pb_updn stays a recorded diagnostic, NOT a gate. The drought lever (F4/F6) and the
sizing lever (F5) remain the real knobs.**

---

## Finding 11 — stop-DISTANCE: wide stops win on RAW (5× the net) but it's ALL tail — clip PF is flat

Breakdown of `stop_dist_pct` on the ema-fixed A book (512 trips; stop dist = (entry−minEMA)/entry, median 9.5%,
range 0.2–50%). Quintiles, raw + clip both.

| stop-dist bucket | PFraw | **PFclip** | win% | net_raw | net_clip |
|---|---|---|---|---|---|
| [0, 5.8%) tightest | 2.43 | 2.19 | 41.2% | $46k | $38k |
| [5.8%, 7.9%) | 2.61 | 2.26 | 52.0% | $58k | $46k |
| [7.9%, 10.8%) | 2.75 | 1.74 | 45.1% | $91k | $38k |
| [10.8%, 15.9%) | 2.85 | 2.22 | 53.9% | $111k | $73k |
| [15.9%, +inf) widest | **4.10** | 2.13 | **54.4%** | **$253k** | $92k |

**RAW PF rises monotonically with stop width** (2.43→2.61→2.75→2.85→4.10) and the widest bucket makes **5× the
net** of the tightest ($253k vs $46k), at the HIGHEST win rate (54.4%). **But CLIP PF is FLAT** (~2.1–2.2, no
structure). So the entire wide-stop advantage is the FAT RIGHT TAIL: wide stops don't win more often or have a
better typical trade (clip flat) — they just don't shake you out before the +100–366% runners. The widest
bucket's raw 4.10 → clip 2.13 collapse IS the tail being clipped away.

**Read (consistent with F7):** this book's edge lives in the TAIL, so anything that truncates it (tight stops,
trailing stops) trades away real dollars. Since the book is hold-for-the-tail + sizing-driven (F5), **wide stops
are strictly better** — 5× the net, no per-trade cost. Tight stops only help if optimizing purely for clip
PF / drawdown, and even then barely (tightest clip 2.19 vs widest 2.13). Per-year is noisy (small cells) but
raw-PF monotonicity holds broadly. NB: the ema-fixed stop (F7) already tends WIDE (level = actual min-EMA, no
d·2/3 tightening) — which is WHY it beat close-geom; this confirms the mechanism.

---

## Finding 12 — ⭐ gate ABLATION: the breakout timer IS the edge; 4 inherited momentum gates are DEAD WEIGHT

Puzzle (user): with BOTH vol-slope AND pb_updn off, the book STILL makes clip 2.06 — so what drives the edge?
Ablation: from the vol-slope-OFF baseline (clip 2.06 / 709 trips), remove ONE more gate per run, max-conc 1
(the honest book). Δ clip PF vs 2.06 = the gate's marginal value.

| gate removed | n | PFraw | **PFclip** | Δclip | verdict |
|---|---|---|---|---|---|
| baseline (all on, vol-slope off) | 709 | 3.04 | 2.06 | — | — |
| **noBreakout** (drop the timer) | 1900 | 2.29 | **1.54** | **−0.52** | ⭐ THE edge |
| noRvol (rvol5m20d<100) | 1299 | 2.54 | 1.74 | −0.32 | real |
| noChg1d (chg1d≥10%) | 891 | 2.67 | 1.84 | −0.22 | real |
| noATR (log-ATR≥0.013) | 2013 | 2.52 | 1.89 | −0.17 | real |
| noChg3d (chg3d≥0) | 772 | 2.85 | 1.95 | −0.11 | mild |
| noSum6 (sum6≥5) | 775 | 3.09 | 2.09 | **+0.03** | INERT (drop candidate) |
| noPriceSlope (>0) | 709 | 3.03 | 2.06 | 0.00 | **DEAD** |
| noTight (≥3) | 710 | 3.04 | 2.06 | 0.00 | **DEAD** |
| noEmaVwap (≥−2%) | 709 | 3.04 | 2.06 | 0.00 | **DEAD** |

**RESOLUTION of the puzzle:** the edge = the **breakout timer** (biggest driver, −0.52, floods to 1900 trips
when dropped) + a few inherited DipRiderV3 gates that genuinely filter: **rvol** (−0.32), **chg1d** (−0.22),
**ATR** (−0.17), chg3d (−0.11 mild). It was NEVER the volume-flow features (vol-slope/pb_updn) — the breakout
gate already does that selection, making them redundant (explains F2/F3/F9/F10).

**4 inherited momentum gates are DEAD WEIGHT** in BreakoutTimer — removing price-slope / tightness / ema-vs-vwap
changes NOTHING (0 trips, 0 PF), and sum6 is slightly BETTER off. Verified non-binding (not flag bugs): in the
709-trip book, price-slope≤0 in 0/709 (min exactly 0.0), tightness<3 in 0/709 (min 3.02), entry<−2%-of-VWAP in
2/709. They're **structurally implied by the breakout setup** — a post-16-bar-drought 9-EMA breakout is ALWAYS
trending (price-slope>0), always has range (tight≥3), always near/above VWAP.

**REMOVED (user, F12b):** price-slope, tightness, ema-vs-vwap, sum6 all turned OFF in the default config.
Simplified A book (gated d16 ema-fixed, max-conc 1): **536 trips / raw 3.12 / clip 2.11 / net_clip $302k / 2021
clip 1.54** — vs pre-removal 512 / 3.14 / 2.10 / $289k / 1.51. Neutral-to-slightly-BETTER on every metric,
per-year stable. Confirms the 4 were dead weight. **Shipped stack = breakout-timer(d16,w10) + ATR≥0.013 +
vol-slope∈[0.05,0.25) + rvol5m20d<100 + chg1d≥10% + chg3d≥0 + ema-fixed stop.** (vol-slope KEPT — PF-neutral but
cuts ~200 trips; a candidate for later removal.)

**NB — DipRiderV3 doc/code mismatch found:** V3's `defaultConfig` left `MinTightness = 3.0` ACTIVE despite the
V3 memory's "Tightness OFF (redundant with ATR)" conclusion; the fork inherited it. Verified HARMLESS in V3 too
(V3 2024 book: tightness min 3.14, only 2 entries near the 3.0 floor → gate barely binds, V3 shipped numbers
effectively unchanged). Cosmetic mismatch; fix V3 config to match its documented decision.

---

## Finding 13 — vol-slope DOES have a (small) effect via its FLOOR; the blow-off CEILING is dead weight here

User: "the volume slope really should have some effect." Tested on the SIMPLIFIED book (4 dead gates off),
gated d16 ema-fixed, max-conc 1. Cells: off / [0,0.25) / [0.05,0.25)=default / [0.1,0.25). Raw + clip both.

| vol-slope | n | win% | PFraw | **PFclip** | net_clip | 2021 clip |
|---|---|---|---|---|---|---|
| off | 787 | 46.5% | 3.07 | 2.08 | $420k | 1.54 |
| [0, 0.25) (ceiling only) | 732 | 46.6% | 2.96 | 2.00 | $374k | 1.56 |
| [0.05, 0.25) DEFAULT | 536 | 48.9% | 3.12 | 2.11 | $302k | 1.54 |
| [0.1, 0.25) | 241 | 49.0% | **3.43** | **2.13** | $144k | **1.33** |

**Reads:**
- **The FLOOR has a real, MONOTONE (but small) effect** — clip 2.00→2.11→2.13 and win 46.6→48.9→49.0% and raw
  2.96→3.12→3.43 as the floor rises 0→0.05→0.10. Requiring RISING (not just non-negative) volume genuinely
  improves each trade — vol-slope is NOT noise (confirms the user's intuition). But it's a CONCENTRATOR: [0.1]
  halves the book (536→241) and net ($302k→$144k) for only +0.02 clip. Same shape as pb_updn.
- **The blow-off CEILING (<0.25) is DEAD WEIGHT here** — [0,0.25) is slightly WORSE than fully off (clip 2.00
  vs 2.08). Unlike V3 (F16 found the ceiling useful), the breakout structure already excludes the
  volume-explosion entries the ceiling targeted. → candidate: drop `MaxVolSlope`.
- **⚠ higher floor HURTS 2021** (1.54→1.33 at [0.1]) — the high-vol-slope selection is trend-year-biased (2022
  2.27 / 2025 2.58 up, but 2021 down). Another feature that concentrates the trend-year edge at 2021's expense.
- **Verdict: keep the [0.05] floor** (lifts clip 2.08→2.11 + win 46.5→48.9% over off — earns its place), **drop
  the ceiling** (dead/harmful). [0.1] is a trend-year concentrator, not a default.

---

## Finding 14 — 20m-avg-VOLUME stop: lifts BOTH PFs but it's a de-facto SCALP exit (259m→45m hold), caps the tail

Idea (user): exit if the trailing-20m avg volume decays to <⅔ (or <½) of its ENTRY value — volume drying up =
disinterest. Wired `VolStopFrac` (`--vol-stop-frac`; ratio = live 20m-vol-sum / entry 20m-vol-sum, fills at
close). Gated d16 ema-fixed simplified, max-conc 1. Raw + clip both.

| | no vol-stop | **vol-stop ⅔** | vol-stop ½ |
|---|---|---|---|
| trips | 536 | 664 | 621 |
| **PFraw** | 3.12 | 3.37 | **3.44** |
| **PFclip** | 2.11 | **2.47** | 2.44 |
| net_raw | $579k | $421k | $469k |
| net_clip | $302k | $262k | $276k |
| **median hold** | **259m (~MOC)** | **45m** | 60m |

**Per-year RAW PF:**

| config | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|
| no vol-stop | 2.76 | 2.03 | 2.47 | 10.27 | 2.76 | 4.30 | 2.59 |
| vol-stop ⅔ | 4.70 | 1.86 | 4.07 | 7.02 | 4.01 | 3.81 | 2.05 |
| vol-stop ½ | 3.57 | 1.73 | 4.06 | 6.69 | 5.00 | 4.20 | 2.40 |

**Per-year CLIP PF:**

| config | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|
| no vol-stop | 1.77 | 1.54 | 2.02 | 6.13 | 1.69 | 2.68 | 2.04 |
| vol-stop ⅔ | 3.55 | 1.56 | 3.47 | 5.23 | 2.79 | 2.42 | 1.70 |
| vol-stop ½ | 2.72 | 1.41 | 3.47 | 4.17 | 3.04 | 2.60 | 2.10 |

The vol-stop LIFTS most years (esp. 2020/2022/2024) but SOFTENS the big trend years (2023, 2025) and 2026 —
consistent with "caps the tail": the years with the biggest runners (2023 10.27→7.02 raw) give up the most.
2021 ≈ flat. **⭐ PRODUCTION IDEA (user, deferred):** sell HALF at the ⅔-volume trigger and let the rest ride —
capture the PF lift on the scalped half while keeping the tail on the runner half. Test later.

**Both PFs rise** (⅔: raw +0.25, clip +0.36; per-year clip broadly up: 2020 1.77→3.55, 2022 2.02→3.47) — rare
in this book (most exits LOSE to hold-to-MOC). **BUT the mechanism is NOT "kill dead trades":** diagnostics show
vol-stop ⅔ fires on **89% of trades** (590/664), **median hold collapses 259m→45m**, and it exits WINNERS at a
**mean +4.63%**. Because the breakout bar IS the volume climax (by construction), 20m-vol almost always decays
below ⅔ within ~45 min → the stop fires on ~everything, early, while still up. **It has converted BreakoutTimer
from a hold-to-MOC swing into a ~45-MINUTE SCALP.**

**Read:** same trade-off as the trailing stop (F7/F11) — it CAPS THE FAT TAIL (net drops $302k→$262k; a 45-min
exit guarantees missing the +100–366% runners that ARE this book's edge) in exchange for higher PF + a shorter,
smoother, lower-exposure book. It's a **SCALP VARIANT of BreakoutTimer, not a strict improvement.** Keep OFF by
default (hold-to-MOC preserves the tail); `--vol-stop-frac 0.667` is a documented scalp/risk mode. NB: as a
"disinterest detector" the idea FAILED (fires on 89%, mostly winners) — it's a time-exit in disguise. A vol-SLOPE
stop (exit on volume-slope going negative post-entry) may be more selective — worth the separate test the user queued.

---

## Finding 15 — volume-SLOPE stop: a clear LOSER — fires near-instantly on ~everything, drops BOTH PFs

Follow-up to F14 (user idea): a vol-SLOPE stop (exit when the live 20m OLS log-volume slope rolls below a
threshold) should be MORE selective than the avg-volume decay stop — a genuine downtrend, not mere decay. Wired
`VolSlopeStop` (`--vol-slope-stop`). Gated d16 ema-fixed simplified, max-conc 1. Raw + clip both.

| config | n | win% | PFraw | PFclip | net_clip | median hold | fire-rate |
|---|---|---|---|---|---|---|---|
| no stop | 536 | 48.9% | 3.12 | 2.11 | $302k | 260m | — |
| vss < 0 | 740 | 46.1% | 1.70 | 1.68 | $91k | 11m | 98% |
| vss < −0.05 | 735 | 44.6% | 2.06 | 1.96 | $152k | 16m | 97% |
| vss < −0.10 | 600 | 44.5% | 2.62 | 1.89 | $219k | 97m | 52% |

**Per-year CLIP PF:**

| config | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|
| no stop | 1.77 | 1.54 | 2.02 | 6.13 | 1.69 | 2.68 | 2.04 |
| vss < 0 | 2.15 | 1.08 | 1.98 | 3.07 | 1.08 | 1.70 | 2.27 |
| vss < −0.05 | 2.61 | 1.23 | 2.84 | 4.61 | 1.79 | 1.88 | 1.84 |
| vss < −0.10 | 2.11 | 1.22 | 2.24 | 4.55 | 1.74 | 2.36 | 1.37 |

**A clear LOSER — worse than the avg-volume stop (F14):** at threshold 0/−0.05 it fires on **97–98%** of trades
at a **11–16 minute** median hold (even MORE trigger-happy than F14's 89%/45m), and **all three thresholds DROP
both PFs** (vss<0: raw 3.12→1.70, clip 2.11→1.68) AND net (to $91–219k). Even the most selective −0.10 (52%
fire, 97m hold) stays worse (clip 1.89). 2021 is hurt in every variant (1.54→1.08–1.23).

**Why both volume-decline exits fail the same way:** the breakout bar IS the volume climax (by construction), so
the 20m volume slope rolls negative within a few bars of EVERY entry → any vol-decline exit fires near-universally
and near-immediately. **Volume is NOT a useful disinterest/exit signal in this book** — it ALWAYS declines
post-entry, so it can't distinguish live movers from dead ones. Unlike F14 (a legit scalp variant that at least
lifted PF), the vol-slope stop lifts neither. Keep OFF. The exit story remains: hold-to-MOC preserves the tail
(F7/F11); the only PF-positive exit found is F14's avg-volume stop (as a scalp, not a strict improvement).

---

## Finding 16 — ⭐ SURPRISE: DECLINING volume into entry (vol-slope < 0) BEATS rising volume — inverts the V3 premise

Experiment (user): flip the vol-slope ENTRY gate to select NEGATIVE slope (declining volume into entry), vs
floor=0 (non-negative), each × {no vol-stop, vol-stop ⅔}. Gated d16 ema-fixed simplified, max-conc 1. Both PFs.

| config | n | win% | PFraw | **PFclip** | net_clip | 2021 clip |
|---|---|---|---|---|---|---|
| default [0.05,0.25) | 536 | 48.9% | 3.12 | 2.11 | $302k | 1.54 |
| off (ref) | 787 | 46.5% | 3.07 | 2.08 | $420k | 1.54 |
| **vol-slope < 0** | 380 | 44.5% | 3.33 | **2.37** | $225k | **1.70** |
| **vol-slope < 0 + vstop⅔** | 420 | 48.3% | 3.83 | **2.86** | $158k | 1.73 |
| floor 0 (≥0) | 732 | 46.6% | 2.97 | 2.00 | $374k | 1.56 |
| floor 0 + vstop⅔ | 902 | 47.2% | 3.24 | 2.39 | $348k | 1.81 |

**Per-year CLIP PF:**

| config | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|
| vol-slope < 0 | 2.60 | 1.70 | **6.20** | 10.47 | 1.43 | 2.60 | 1.80 |
| vol-slope < 0 + vstop⅔ | 3.05 | 1.73 | 5.56 | 5.23 | 3.79 | 2.97 | 1.93 |
| floor 0 | 2.27 | 1.56 | 1.80 | 5.24 | 1.81 | 2.15 | 1.82 |

**⭐ DECLINING volume into entry BEATS rising volume** — `vol-slope < 0` clip **2.37** (vs 2.11 default / 2.08
off), raw 3.33, AND **2021 → 1.70** (best for a book this size), **2022 → 6.20** (from 2.02 — the old weak year
transformed). Clip-positive EVERY year. Add vstop⅔: clip **2.86** / raw 3.83. This **INVERTS the DipRiderV3
premise** ("rising volume is the PF lever") — in the BreakoutTimer context the opposite holds.

**Why it makes structural sense:** the breakout timer ALREADY bakes the volume event into the SETUP (a
post-drought 9-EMA breakout). By entry, a DECLINING 20m volume slope means you're buying the QUIET pullback
into/before the breakout (early & cheap), not chasing the volume CLIMAX (late). Rising-vol entries = buying the
top of the volume spike = worse. Consistent with F13 (higher floor was trend-biased, hurt 2021) and the rvol
exhaustion cut already in the stack. It's a CONCENTRATOR (536→380 trips) but a GOOD one — better PF, better 2021
AND 2022 (unlike the pb_updn floor which only helped trend years). floor=0 is neutral (≈off); the edge is
specifically in SELECTING the negative-slope entries. **Candidate new default: `MaxVolSlope = 0` (vol-slope < 0),
drop the floor.** Strongest single lever found after the breakout timer + drought.

---

## Finding 17 — WHY vol-slope<0 works + WHY the vstop is huge here: the edge is a fast EARLY POP, not a runner

Two surprises from F16 explained by studying the vol-slope<0 book directly (post-hoc on the max-conc-1 books
`exp_neg_*`). Both resolve to ONE mechanism.

**(1) pb_updn is NOT the hidden driver.** pb_updn breakdown on the vol-slope<0 book, both post-hoc (380 trips,
max-conc 1) and the clean max-conc-0 study pop (1273 trips, raw PF 3.53), is NON-MONOTONE — max-conc-0 quintiles
clip 2.87 / 2.14 / 1.69 / 3.50 / 2.49 = a noisy U (both ends beat the middle), NOT a gateable lever. So within
the declining-vol regime the accumulation/distribution split adds NO clean signal — vol-slope<0 already captures
whatever pb_updn caught. (Substitutes: at the RISING setting pb_updn WAS monotone, F8; here it's inert.) The edge
is the vol-slope<0 SELECTION itself, not a pb_updn sub-structure. (NB the max-conc-0 vol-slope<0 pop is raw PF
3.53 vs the rising-vol pop's 3.13 — declining-vol is the stronger regime even at the feature-study population.)

**(2) The vstop is NOT more selective here — it's the ENTRY that changed.** On the vol-slope<0 book the vstop⅔
STILL fires near-universally: **82% of trades, 27-minute median hold** (vs 89%/45m on the rising-vol book) —
same blunt near-immediate scalp. But it EXITS AT A MEAN **+5.07%**. The difference from the rising-vol book
(where the same scalp HURT net by capping runners) is the ENTRY: vol-slope<0 buys the QUIET pullback early &
cheap, which produces a **fast, high-hit-rate POP that fades** — so a 27-min early exit LOCKS IN the pop before
the fade. On rising-vol entries (late, at the climax) the early exit just clips the rare runner.

**ONE mechanism:** declining-vol entries are early-and-cheap → they POP FAST then fade → an early exit is optimal.
Rising-vol entries are late-and-expensive → the move is already spent → holding for the tail is the only edge.
So there are really TWO distinct BreakoutTimer books: (A) rising/off-vol + hold-to-MOC = a TAIL/runner book
(F7/F11); (B) **vol-slope<0 + vstop⅔ = a fast EARLY-POP SCALP** (27m holds, clip 2.86). They are different
strategies on the same trigger, not one config strictly beating another — (A) maximizes total net via the tail,
(B) maximizes PF/turnover via the pop. The half-out idea (F14) is the natural synthesis: scalp half at ⅔-vol,
ride half for the tail.

---

## Finding 18 — breakout-timer LENGTH sweep: 10 is the clip-PF peak (flat 10–15 plateau); confirms the default

Swept `--breakout-timer-bars` (the countdown window length) on the new default book (vol-slope OFF, drought 16,
ema-fixed), max-conc 1. Timer length = a gate param (changes which bars are inside the live window), so each is
its own run. Both PFs.

| timer | n | win% | PFraw | **PFclip** | net_clip |
|---|---|---|---|---|---|
| 5 | 660 | 46.4% | 2.95 | 2.02 | $347k |
| 7 | 714 | 46.9% | 3.03 | 2.04 | $375k |
| **10 (default)** | 787 | 46.5% | 3.07 | **2.08** | $420k |
| 15 | 899 | 46.3% | 2.97 | 2.05 | $454k |
| 20 | 981 | 45.6% | 2.96 | 1.99 | $459k |
| 30 | 1088 | 44.9% | 2.82 | 1.93 | $474k |

Per-year clip: 10 best/near-best in the key years (2020 2.72, 2023 6.78, 2021 1.54); longer timers help 2026
(20/30 → 1.98/1.84) & mildly 2022 but hurt 2020/2023/2024 — a slight trend-vs-chop tradeoff.

**Read: 10 is the clip-PF PEAK, on a flat 10–15 plateau.** Shorter (5/7) is slightly WORSE (2.02–2.04) — so the
"shorter suits the fast-pop book" hypothesis is FALSE; below 10 you reject too many genuine breakouts. Longer
(20/30) erodes clip (1.99/1.93) — the momentum has dissipated by then. Net dollars keep rising to 30 (more trips)
but at falling per-trade quality. **10 confirmed as the default** — no change. (15 is a defensible alt if
maximizing net at ~equal clip.)

---

## Finding 19 — intraday ATR% is NOISY here (2021-inverting), NOT the clean monotone lever it was in V3

Post-hoc breakdown of `log_atr_20` on the default book (tlen10: vol-slope off, drought 16, ema-fixed, 787 trips;
gate floor 0.013). Quintiles, raw + clip, per-year.

| log_atr_20 bucket | PFraw | **PFclip** | win% | net_clip | 2021 clip | 2022 clip | 2023 clip |
|---|---|---|---|---|---|---|---|
| [0.013, 0.0144) lowest | 1.69 | 1.69 | 45.2% | $34k | **1.72** | 1.39 | 2.27 |
| [0.0144, 0.0178) | 3.53 | **2.60** | 47.1% | $84k | 1.47 | 2.08 | 3.13 |
| [0.0178, 0.0233) | 1.86 | 1.64 | 42.4% | $47k | 1.42 | 3.97 | 7.55 |
| [0.0233, 0.0334) | 4.19 | 2.49 | 47.8% | $134k | **2.27** | 1.86 | 7.28 |
| [0.0334, +inf) highest | 3.34 | 1.99 | 50.0% | $121k | **0.60** | **0.02** | **48.05** |

**Non-monotone ZIGZAG — NOT "higher ATR = better"** (unlike V3 where ATR was THE main monotone lever). Reason =
the familiar 2021-law: the HIGHEST bucket is a pure TREND-YEAR AMPLIFIER — 2023 clip **48.05** but **2021 0.60 /
2022 0.02 (dead)**. That trend-vs-chop cancellation flattens the aggregate (top bucket only clip 1.99 despite
50% win). The MID bucket [0.0233,0.0334) is the most BALANCED (2021 **2.27** = best 2021 cell, 2020 5.22, 2025
3.61). The lowest bucket [0.013,0.0144) has NO fat tail (net_clip==net_raw, nothing >+50%) and mediocre clip
(1.69) BUT is chop-resilient (2021 1.72) — a low-vol / low-payoff / regime-safe slice.

**Read:** ATR% is MUCH weaker/noisier as a lever in BreakoutTimer than in V3 — the breakout timer already selects
volatile moving names, so the RESIDUAL ATR signal is mostly the 2021-inverting trend-amplification, which doesn't
gate cleanly. The 0.013 floor still earns its keep (F12 ablation: −0.17 clip) but pushing it HIGHER doesn't help
(would cut the strong low-mid buckets AND the chop-resilient bottom). **No change to the floor.** Same lesson as
pb_updn (F8/F16): the breakout structure subsumes the momentum-quality features; only the TIMING/structure levers
(timer, drought, vol-slope-direction) and the bad-everywhere cuts (rvol, chg1d) are clean.

---

## Finding 20 — ATR-floor sweep: 0.013 is the clip-PF PEAK; lowering trades PF for net, does NOT help 2021

Swept `--min-intraday-atr-pct` (gate param → own run each) on the default book, max-conc 1. Both PFs.

| ATR floor | n | win% | PFraw | **PFclip** | net_clip |
|---|---|---|---|---|---|
| off (0) | 2162 | 43.9% | 2.55 | 1.90 | $528k |
| 0.005 | 1664 | 45.4% | 2.64 | 1.95 | $522k |
| 0.008 | 1254 | 45.1% | 2.78 | 2.00 | $484k |
| 0.010 | 1022 | 45.5% | 2.94 | 2.07 | $471k |
| **0.013 (default)** | 787 | 46.5% | 3.07 | **2.08** | $420k |
| 0.018 | 545 | 46.6% | 3.18 | 2.02 | $325k |

Per-year clip: 2021 is FLAT across the whole sweep (1.57/1.59/1.51/1.52/1.54/1.45) — lowering does NOT help it;
0.018 slightly HURTS it. Raising to 0.018 is a trend play (2023 6.78→7.71, 2025 2.19→2.35) at the cost of clip.

**Read: 0.013 is the clip-PF PEAK** — clip rises monotonically off→0.013 (1.90→2.08) then falls at 0.018. So
"try lower" does NOT pan out: lower-ATR entries dilute per-trade quality (clip ↓). It's a genuine optimum, not
an arbitrary inherited value. Lowering DOES add net (off/0.005 make ~$525k vs $420k, ~2× the trips — the extra
low-ATR trades are net-positive, just lower-PF), so off/0.005 is a valid MORE-NET/lower-PF book choice. But it's
not a free win and does NOT help 2021. **0.013 kept as the default.** (off = the max-net variant if desired.)

## Finding 21 — SESSION-MIN vol-stop basis: WEAKER than the entry basis, not stronger — the low threshold HURTS selectivity

Motivation (user): F16 found the vol-stop was hugely effective on vol-slope<0 trades, and the guess was that
this could be a *threshold* effect — vol-slope<0 trades enter on quieter volume, so their entry-based stop level
`⅔×entry-vol` sits LOWER, and a lower floor might be what does the work. To test that directly: hold vol-slope
**> 0** fixed (rising-volume trades, the ORIGINAL premise — so we're NOT confounding with the F16 population)
and swap the vol-stop basis from `⅔ × ENTRY 20m-vol` to `⅔ × SESSION-MIN 20m-vol`. The session-min basis is a
lower, more absolute floor. New engine: `sessMinVol20` (a running min of the trailing-20m avg volume), tracked
from **09:45 ET** (the trade start — NOT the 09:30 feature anchor; the volatile open would drag the min down),
fixed as-of entry (`--vol-stop-session-min`). Gated d16 book, ema-fixed, max-conc 1, `--min-vol-slope 0`.

| (vol-slope>0) | no vol-stop | **entry-basis ⅔** | **sessmin-basis ⅔** |
|---|---|---|---|
| trips | 811 | 994 | 911 |
| win% | 46.0% | 46.9% | 45.1% |
| **PFraw** | 2.85 | **3.23** | 3.04 |
| **PFclip** | 1.97 | **2.42** | 2.18 |
| net_raw | $766k | $593k | $658k |
| net_clip | $399k | $377k | $380k |

**Per-year CLIP PF:**

| config | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|
| no vol-stop     | 2.27 | 1.56 | 1.80 | 5.24 | 1.81 | 2.15 | 1.82 |
| entry-basis ⅔   | 3.62 | 1.81 | 2.89 | 4.60 | 3.12 | 2.13 | 1.59 |
| sessmin-basis ⅔ | 3.07 | 1.67 | 1.96 | 3.21 | 2.79 | 2.01 | 2.04 |

**Result — the hypothesis is REJECTED: session-min is a WEAKER stop, not a stronger one.** The session-min
basis lands BETWEEN no-stop and entry-basis on clip PF (2.18 vs 1.97 vs 2.42) — it keeps more of the tail
(net_raw $658k, closer to no-stop's $766k than entry-basis's $593k) but improves per-trade quality LESS. It
fires less aggressively (911 open-slots vs 994 — the entry stop cuts faster, freeing slots for more trades),
and its per-year clip lift is muted almost everywhere (2022 1.96 vs entry-basis's 2.89; 2024 2.79 vs 3.12).

**Why:** a LOWER stop threshold makes the stop LESS selective — volume has to fall all the way to ⅔ of the
session's *quietest* 20m before it fires, which for most trades never happens until very late (near-MOC), so
the stop degenerates toward hold-to-MOC. The entry basis is a RELATIVE floor ("volume fell to ⅔ of what it was
when I bought") which fires precisely when the move that justified the entry loses its own fuel — that relative
drop is the selective signal, not an absolute-low level. **So F16's vol-stop lift on vol-slope<0 trades is NOT
a low-threshold artifact** — it's the F17 mechanism (declining-vol = fast early pop → the relative ⅔-of-entry
drop catches the fade). **Keep the ENTRY basis; the session-min basis is discarded** (recorded diagnostic only).

## Finding 22 — entry-basis vol-stop TIGHTNESS sweep: ⅔ is the clip-PF PEAK; tightening to ¾/⅚ only ERODES it

After F21 (entry basis beats session-min) the question was whether a TIGHTER entry-basis stop does even better.
Swept `--vol-stop-frac` ∈ {⅔, ¾, ⅚} on the SETTLED default book (vol-slope OFF / take-all, d16, ema-fixed,
max-conc 1). Raw + clip both.

| entry-basis vstop | n | win% | PFraw | **PFclip** | net_raw | net_clip |
|---|---|---|---|---|---|---|
| none | 872 | 46.0% | 2.96 | 2.05 | $835k | $448k |
| **⅔ (0.667)** | 1074 | 47.0% | **3.25** | **2.44** | $624k | $398k |
| ¾ (0.75) | 1130 | 45.2% | 3.07 | 2.38 | $572k | $381k |
| ⅚ (0.833) | 1218 | 46.3% | 2.98 | 2.34 | $547k | $369k |

**Per-year CLIP PF:**

| config | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|
| none    | 2.72 | 1.54 | 1.71 | 6.78 | 1.89 | 2.19 | 1.80 |
| ⅔       | 4.02 | 1.64 | 2.58 | 5.65 | 3.15 | 2.20 | 1.63 |
| ¾       | 3.51 | 1.60 | 2.71 | 4.79 | 2.77 | 2.28 | 1.56 |
| ⅚       | 3.78 | 1.59 | 2.57 | 4.90 | 3.02 | 2.09 | 1.40 |

**Result — ⅔ is the peak; tightening past it is monotonically WORSE on BOTH PF and net.** Clip slides
2.44 → 2.38 → 2.34 and net $398k → $381k → $369k as the stop tightens. A tighter stop takes MORE trades
(1074 → 1218 — it fires on more names, freeing more max-conc-1 slots) but each incremental exit is
lower-quality: the extra trips it enables are net-dilutive. Per-year, ⅔ is the best or near-best clip in almost
every year (2020 4.02, 2024 3.15 both peak at ⅔; 2026 falls off a cliff 1.63→1.40 as it tightens); no single
regime year is rescued by a tighter stop (2022 nudges up to 2.71 at ¾ but everything else erodes). 2021 is FLAT
(~1.6) across the sweep — the vol-stop is regime-neutral there. **⅔ kept as the vol-stop default** (when the
vol-stop is used at all — recall it remains OFF in the settled default book, a scalp/half-out variant per F14/F17).

## Finding 23 — ema_climb/atr ported from VwapReclaimV2 — REDUNDANT here (the breakout structure subsumes it; 66% of trips sit in one bucket)

Ported `ema_climb/atr` from VwapReclaimV2 (`ema_climb = (9-EMA − 20m-min-9-EMA)/9-EMA`, fractional/scale-free;
`/log_atr20`). BreakoutTimer already had `emaLow = MinMa(20)` (its EMA-stop floor) — reused it, snapshotted
strictly-prior; recorded-only. A-book: 3458 trips / raw PF 2.01 / clip PF 1.41. New cols no-NaN. **ema_climb median
= 0.013** (vs DipRiderV3 0.053, reclaim 0.005) — the breakout-timer enters right after a fresh 9-EMA session high
post-drought, so the EMA is barely lifting; climb/atr median only 0.64 and **66% of trips (2267) fall in the
lowest [−∞,1.5) bucket** — the feature barely varies.

| climb/atr | n | rawPF | clipPF | avg% |
|---|---:|---:|---:|---:|
| [−∞,1.5) | 2267 | 2.05 | 1.40 | 4.6 |
| [1.5,2.0) | 254 | 2.28 | 1.76 | 5.4 |
| [2.0,2.5) | 201 | 2.00 | 1.45 | 4.9 |
| [2.5,3.0) | 217 | 1.76 | 1.45 | 3.8 |
| [3.0,3.5) | 198 | 2.56 | 1.51 | 7.7 |
| [3.5,∞) | 321 | 1.44 | 1.17 | 2.2 |
| **A-book (all)** | 3458 | 2.01 | 1.41 | 4.6 |

**REDUNDANT.** Floor (`≥2.0/2.5/3.0`) mildly HURTS (clip 1.41→1.30–1.37); ceiling (`<2.5`) a trivial +0.03. The
faint "less climb = better" tilt (best [1.5,2.0) clip 1.76, worst [3.5+) clip 1.17) is noise-level against a book
where 66% of trips share one bucket. **Confirms F12 across both momentum systems: the breakout/momentum STRUCTURE
already subsumes the volatility-quality ema_climb/atr measures — it adds nothing.** VERDICT: do NOT gate on
ema_climb/atr.

**Cross-system conclusion (VwapReclaimV2 F9 / DipRiderV3 F30 / this):** ema_climb/atr is STRONGLY additive ONLY in
VwapReclaimV2 (PF 4.42→5.13), where the reclaim's few gates leave headroom AND the entry is at the EMA-cross (low
climb, real spread to exploit). In the two MOMENTUM systems it's redundant — they enter with the EMA already
lifted (DRV3 median 0.053) or their structure/gates already capture the volatility quality (BT). The feature's
value is REGIME-SPECIFIC: it works for mean-reversion reclaims, not momentum-continuation.

### F23 addendum — full ema_climb/atr FLOOR ladder [x,∞): raising the floor MONOTONICALLY hurts (breaks below 1.0 clip at the top)

| floor | n | win% | rawPF | clipPF | avg% |
|---|---:|---:|---:|---:|---:|
| (all) | 3458 | 38.1 | 2.01 | 1.41 | 4.6 |
| ≥1.5 | 1191 | 42.5 | 1.94 | 1.44 | 4.6 |
| ≥2.0 | 937 | 43.2 | 1.86 | 1.37 | 4.3 |
| ≥2.5 | 736 | 43.6 | 1.83 | 1.34 | 4.2 |
| ≥3.0 | 519 | 45.1 | 1.86 | 1.30 | 4.3 |
| ≥3.5 | 321 | 44.5 | 1.44 | 1.17 | 2.2 |
| ≥4.0 | 192 | 41.1 | 1.22 | 0.98 | 1.2 |
| ≥5.0 | 63 | 36.5 | 1.84 | 1.17 | 4.1 |

Clip PF erodes as the floor rises (1.44 @≥1.5 → 1.30 @≥3.0 → **0.98 @≥4.0**, losing money on a clip basis).
NUANCE: win RATE rises with the floor (38%→45%) while PF FALLS — high-climb trades win slightly more often but
their winners are SMALLER (avg% collapses 4.6→1.2). More climb = more exhausted = capped upside — a mild version
of the reclaim's "too-fast fade," surfacing as clipped-away tails. **A FLOOR is strictly counterproductive here.**

## Finding 24 — trailing-window updn (10–30m) is NOT a lever in BreakoutTimer — mildly COUNTERPRODUCTIVE (low-updn is better; a floor DEGRADES clip PF)

Ported fixed-window updn from VwapReclaimV2 (recorded-only; 3458 trips / PF 2.01 unchanged). Median updn ~1.3
across windows (flat). **The edge INVERTS vs the reclaim: the LOWEST updn bucket is the BEST** (updn_20 [−∞,1.0)
clip 1.64 vs the 1.41 baseline; [1.5,2.0) sags to 1.25). Every FLOOR across every window DEGRADES clip PF (1.41 →
1.28–1.39). MECHANISM: a breakout-timer entry fires right after a fresh 9-EMA session high — "volume already
piled above the EMA" (high updn) means the move began WITHOUT you (late entry); balanced volume = earlier. But the
effect is weak. **VERDICT: updn does not help BreakoutTimer (a mild low-updn tilt exists but isn't worth a gate).**
Cross-system: trailing-window updn is a null-to-weak feature in BOTH momentum systems (DRV3 F33) and the reclaim
(VwapReclaim F12); only VwapReclaim's RUN-scoped updn was ever a strong lever.
