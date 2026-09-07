# SpringFlyer — results

Branch `spring-flyer`. The system: a swing long entered AT THE CLOSE of a day that made a large
intraday decline from a reference (previous close / open), ideally below an obvious support (the
7-day low), and then closed back above the reference — Wyckoff's *spring*. Hold k days.

## S1 — first pass on DAILY bars (2026-09-07)

Script `scripts/equity/springflyer_daily.py`, features `data/springflyer_daily.parquet`
(25.5M ticker-days, CS/ADRC via `daily_episodes_causal`, 2005→2026-09-04), log
`data/springflyer_daily.log`. Prices per `docs/price_adjustment.md` §2 (prior days in D's raw
scale, dividends as cash, floors on RAW). Every gate is knowable at D's close; entry at D's close
(the S43bv 15:59 convention). Returns bp from D's close to D+k's close (`r1..r10`), and to D+1's
open (`r_open1`). `mae5` = the worst low over the next 5 days. Cells are per-signal (one row per
ticker-day; overlapping holds NOT de-duplicated — a mean/PF attribution, not a book).

FRAME = `dv20_prior >= $5M ∧ prev_close_raw >= $2` (543,925 ticker-days/yr).

### T0 — the frame, and the naive references
| cell | n/yr | r_open1: mean bp / PF | r1: mean bp / PF | r3: mean bp / PF | r5: mean bp / PF | r10: mean bp / PF | win5 | yrs5 | med mae5 |
|---|---|---|---|---|---|---|---|---|---|
| every day (the frame) | 543,925 | +4 / 1.11 | +4 / 1.04 | +12 / 1.07 | +19 / 1.09 | +37 / 1.13 | 52% | 17/22 | -270 |
| close > prev close (any) | 272,731 | +4 / 1.10 | +3 / 1.03 | +8 / 1.05 | +12 / 1.06 | +31 / 1.11 | 51% | 16/22 | -265 |
| close < prev close (any) | 264,991 | +5 / 1.12 | +6 / 1.06 | +15 / 1.09 | +27 / 1.13 | +44 / 1.15 | 53% | 18/22 | -277 |

### T1 — DECLINE from the previous close (low vs prev close) x the SPRING close (close > prev close), with the failed-reversal control
| cell | n/yr | r_open1: mean bp / PF | r1: mean bp / PF | r3: mean bp / PF | r5: mean bp / PF | r10: mean bp / PF | win5 | yrs5 | med mae5 |
|---|---|---|---|---|---|---|---|---|---|
| spring: decl -3..-5%, close > prev | 7,631 | +7 / 1.10 | -8 / 0.96 | +3 / 1.01 | -0 / 1.00 | +27 / 1.05 | 49% | 14/22 | -557 |
| spring: decl -5..-7%, close > prev | 1,722 | +8 / 1.07 | -12 / 0.95 | +20 / 1.05 | -9 / 0.98 | +21 / 1.03 | 48% | 11/22 | -726 |
| spring: decl -7..-10%, close > prev | 663 | +8 / 1.06 | -21 / 0.93 | -9 / 0.98 | +2 / 1.00 | -20 / 0.97 | 47% | 12/22 | -880 |
| spring: decl -10..-15%, close > prev | 224 | +10 / 1.06 | -40 / 0.90 | -140 / 0.79 | -133 / 0.83 | -226 / 0.78 | 44% | 4/22 | -1138 |
| spring: decl -15..-25%, close > prev | 51 | -24 / 0.91 | -67 / 0.88 | -256 / 0.73 | -303 / 0.73 | -592 / 0.60 | 40% | 7/22 | -1592 |
| spring: decl <-25%, close > prev | 8 | -303 / 0.37 | -501 / 0.34 | -638 / 0.45 | -804 / 0.45 | -286 / 0.85 | 31% | 4/18 | -1277 |
| CONTROL: decl -3..-5%, close <= prev | 50,638 | +4 / 1.08 | +4 / 1.03 | +17 / 1.09 | +29 / 1.11 | +49 / 1.14 | 52% | 18/22 | -376 |
| CONTROL: decl -5..-7%, close <= prev | 18,722 | +2 / 1.03 | -3 / 0.98 | +12 / 1.04 | +28 / 1.08 | +45 / 1.10 | 51% | 16/22 | -501 |
| CONTROL: decl -7..-10%, close <= prev | 10,058 | -1 / 0.99 | -5 / 0.97 | +14 / 1.04 | +36 / 1.09 | +45 / 1.08 | 51% | 18/22 | -619 |
| CONTROL: decl -10..-15%, close <= prev | 4,612 | +14 / 1.13 | +8 / 1.03 | -7 / 0.98 | +16 / 1.03 | +13 / 1.02 | 49% | 12/22 | -792 |
| CONTROL: decl -15..-25%, close <= prev | 1,707 | +41 / 1.30 | +41 / 1.13 | -29 / 0.95 | +8 / 1.01 | -9 / 0.99 | 47% | 10/22 | -958 |
| CONTROL: decl <-25%, close <= prev | 479 | +308 / 2.36 | +427 / 1.89 | +425 / 1.56 | +671 / 1.75 | +476 / 1.42 | 46% | 11/22 | -1166 |

### T2 — the same, decline measured from the OPEN and the close judged vs the OPEN
| cell | n/yr | r_open1: mean bp / PF | r1: mean bp / PF | r3: mean bp / PF | r5: mean bp / PF | r10: mean bp / PF | win5 | yrs5 | med mae5 |
|---|---|---|---|---|---|---|---|---|---|
| spring: decl_open -3..-5%, close > open | 7,286 | +9 / 1.11 | -8 / 0.96 | -7 / 0.98 | -12 / 0.97 | +3 / 1.01 | 48% | 8/22 | -606 |
| spring: decl_open -5..-7%, close > open | 1,564 | -3 / 0.97 | -24 / 0.91 | -33 / 0.93 | -68 / 0.88 | -69 / 0.91 | 46% | 8/22 | -822 |
| spring: decl_open -7..-10%, close > open | 600 | -16 / 0.91 | -30 / 0.92 | -74 / 0.88 | -120 / 0.84 | -108 / 0.88 | 45% | 7/22 | -1024 |
| spring: decl_open -10..-15%, close > open | 194 | -22 / 0.90 | -56 / 0.88 | -192 / 0.76 | -220 / 0.77 | -260 / 0.77 | 41% | 6/22 | -1286 |
| spring: decl_open -15..-25%, close > open | 43 | -40 / 0.89 | -190 / 0.72 | -328 / 0.68 | -245 / 0.79 | -415 / 0.72 | 43% | 5/22 | -1634 |
| spring: decl_open <-25%, close > open | 9 | -145 / 0.74 | -267 / 0.65 | -381 / 0.65 | -541 / 0.62 | -89 / 0.95 | 40% | 3/16 | -1258 |
| CONTROL: decl_open -3..-5%, close <= open | 46,685 | +4 / 1.08 | +4 / 1.03 | +14 / 1.07 | +24 / 1.09 | +44 / 1.12 | 51% | 17/22 | -400 |
| CONTROL: decl_open -5..-7%, close <= open | 16,018 | +2 / 1.03 | +1 / 1.01 | +12 / 1.04 | +24 / 1.07 | +47 / 1.10 | 50% | 17/22 | -538 |
| CONTROL: decl_open -7..-10%, close <= open | 8,225 | -0 / 1.00 | -4 / 0.98 | +6 / 1.02 | +25 / 1.06 | +32 / 1.05 | 50% | 16/22 | -681 |
| CONTROL: decl_open -10..-15%, close <= open | 3,342 | +2 / 1.01 | -9 / 0.97 | -2 / 1.00 | +20 / 1.03 | -2 / 1.00 | 48% | 11/22 | -887 |
| CONTROL: decl_open -15..-25%, close <= open | 1,046 | +7 / 1.04 | +3 / 1.01 | +12 / 1.02 | +84 / 1.11 | -31 / 0.97 | 46% | 10/22 | -1171 |
| CONTROL: decl_open <-25%, close <= open | 209 | +567 / 2.62 | +822 / 2.19 | +957 / 1.98 | +1427 / 2.20 | +1059 / 1.68 | 45% | 9/22 | -1672 |

### T3 — SUPPORT: did the low break the 7-day / 20-day low? (spring days with decline <= -5% vs prev close)
| cell | n/yr | r_open1: mean bp / PF | r1: mean bp / PF | r3: mean bp / PF | r5: mean bp / PF | r10: mean bp / PF | win5 | yrs5 | med mae5 |
|---|---|---|---|---|---|---|---|---|---|
| spring, low ABOVE 7d low (no break) | 1,483 | +5 / 1.04 | -13 / 0.96 | -14 / 0.97 | -45 / 0.93 | -36 / 0.96 | 45% | 8/22 | -930 |
| spring, broke 7d low by 0..-2% | 214 | +26 / 1.28 | -29 / 0.88 | +19 / 1.06 | +20 / 1.05 | -23 / 0.96 | 50% | 15/22 | -606 |
| spring, broke 7d low by -2..-5% | 510 | +6 / 1.06 | -22 / 0.90 | +42 / 1.13 | +36 / 1.09 | +39 / 1.07 | 51% | 10/22 | -617 |
| spring, broke 7d low by <-5% | 461 | +1 / 1.00 | -32 / 0.89 | -56 / 0.87 | -49 / 0.91 | -45 / 0.93 | 49% | 13/22 | -776 |
| spring, broke 20d low | 805 | -2 / 0.99 | -36 / 0.87 | -8 / 0.98 | +17 / 1.04 | -18 / 0.97 | 51% | 11/22 | -686 |
| spring, broke 20d low AND closed back above the 7d low | 805 | -2 / 0.99 | -36 / 0.87 | -8 / 0.98 | +17 / 1.04 | -18 / 0.97 | 51% | 11/22 | -686 |
| CONTROL: broke 7d low, decl<=-5%, close <= prev | 19,225 | +12 / 1.14 | +15 / 1.08 | +31 / 1.10 | +63 / 1.17 | +60 / 1.12 | 52% | 21/22 | -529 |

### T4 — the close's position in the day's range (spring days, decline <= -5%, broke the 7d low)
| cell | n/yr | r_open1: mean bp / PF | r1: mean bp / PF | r3: mean bp / PF | r5: mean bp / PF | r10: mean bp / PF | win5 | yrs5 | med mae5 |
|---|---|---|---|---|---|---|---|---|---|
| clpos 0.5-0.7 | 129 | +58 / 1.43 | -47 / 0.86 | +64 / 1.15 | +129 / 1.25 | +39 / 1.06 | 53% | 16/22 | -818 |
| clpos 0.7-0.85 | 303 | +27 / 1.24 | +6 / 1.02 | +54 / 1.15 | +77 / 1.18 | -3 / 1.00 | 51% | 13/22 | -680 |
| clpos 0.85-0.95 | 410 | +6 / 1.06 | -19 / 0.91 | -11 / 0.97 | +20 / 1.05 | +20 / 1.04 | 50% | 12/22 | -629 |
| clpos 0.95-1 (closed at the high) | 327 | -29 / 0.74 | -63 / 0.74 | -73 / 0.81 | -152 / 0.72 | -56 / 0.90 | 48% | 10/22 | -639 |

### T5 — relative volume on the spring day (decline <= -5%, broke 7d low, close > prev)
| cell | n/yr | r_open1: mean bp / PF | r1: mean bp / PF | r3: mean bp / PF | r5: mean bp / PF | r10: mean bp / PF | win5 | yrs5 | med mae5 |
|---|---|---|---|---|---|---|---|---|---|
| rvol <1 | 352 | +27 / 1.29 | -16 / 0.93 | +33 / 1.09 | +24 / 1.05 | +25 / 1.04 | 50% | 13/22 | -675 |
| rvol 1-2 | 581 | -3 / 0.98 | -45 / 0.84 | -14 / 0.96 | -9 / 0.98 | +6 / 1.01 | 51% | 12/22 | -698 |
| rvol 2-4 | 211 | +1 / 1.01 | -9 / 0.96 | -20 / 0.94 | -12 / 0.97 | -68 / 0.88 | 50% | 11/22 | -598 |
| rvol 4-8 | 35 | +12 / 1.10 | +17 / 1.07 | +34 / 1.10 | +28 / 1.07 | -36 / 0.93 | 48% | 9/22 | -533 |
| rvol 8+ | 6 | +113 / 1.90 | +96 / 1.35 | -81 / 0.83 | -226 / 0.64 | -366 / 0.55 | 39% | 7/22 | -731 |

### T6 — the 20-day context (spring, decline <= -5%, broke 7d low): was it already down?
| cell | n/yr | r_open1: mean bp / PF | r1: mean bp / PF | r3: mean bp / PF | r5: mean bp / PF | r10: mean bp / PF | win5 | yrs5 | med mae5 |
|---|---|---|---|---|---|---|---|---|---|
| chg20 <-30% | 180 | -27 / 0.87 | -34 / 0.92 | +56 / 1.10 | +187 / 1.29 | +68 / 1.09 | 53% | 14/22 | -1090 |
| chg20 -30..-15% | 277 | +4 / 1.03 | -41 / 0.85 | -57 / 0.86 | -52 / 0.89 | -84 / 0.87 | 50% | 11/22 | -799 |
| chg20 -15..-5% | 239 | +10 / 1.10 | -42 / 0.81 | -3 / 0.99 | -34 / 0.92 | -26 / 0.95 | 51% | 13/22 | -620 |
| chg20 -5..+5% | 217 | +6 / 1.09 | -23 / 0.87 | +12 / 1.04 | -34 / 0.91 | +22 / 1.05 | 49% | 9/22 | -501 |
| chg20 +5%+ | 271 | +34 / 1.40 | +2 / 1.01 | +14 / 1.04 | -13 / 0.97 | +25 / 1.04 | 48% | 7/22 | -581 |

### T7 — liquidity tiers (spring, decline <= -5%, broke 7d low)
| cell | n/yr | r_open1: mean bp / PF | r1: mean bp / PF | r3: mean bp / PF | r5: mean bp / PF | r10: mean bp / PF | win5 | yrs5 | med mae5 |
|---|---|---|---|---|---|---|---|---|---|
| dv20 $5-20M | 560 | +3 / 1.03 | -13 / 0.95 | -5 / 0.99 | -2 / 1.00 | +1 / 1.00 | 49% | 12/22 | -655 |
| dv20 $20-100M | 427 | -1 / 0.99 | -40 / 0.85 | -10 / 0.97 | -2 / 1.00 | +8 / 1.01 | 51% | 12/22 | -665 |
| dv20 $100M-1B | 184 | +35 / 1.30 | -42 / 0.84 | +29 / 1.08 | +8 / 1.02 | -48 / 0.92 | 52% | 14/22 | -697 |
| dv20 $1B+ | 14 | +78 / 1.79 | -10 / 0.96 | +135 / 1.47 | +39 / 1.10 | -55 / 0.90 | 53% | 10/18 | -653 |

### T8 — year table, headline cell = spring (decl<=-5%, close>prev) x broke the 7d low
decl_prev < -0.05 AND rev_prev > 0 AND below7 < 0
| year | n | r_open1 mean/PF | r1 | r3 | r5 | r10 |
|---|---|---|---|---|---|---|
| 2005 | 235 | -7 / 0.87 | -34 / 0.78 | -25 / 0.87 | -34 / 0.86 | -152 / 0.65 |
| 2006 | 286 | +12 / 1.24 | -19 / 0.87 | -139 / 0.53 | -19 / 0.93 | -54 / 0.86 |
| 2007 | 727 | +70 / 2.11 | +80 / 1.67 | +40 / 1.18 | +54 / 1.21 | -26 / 0.93 |
| 2008 | 4,670 | +65 / 1.52 | +85 / 1.29 | +53 / 1.12 | -200 / 0.72 | -190 / 0.75 |
| 2009 | 1,148 | +59 / 1.75 | -29 / 0.88 | +108 / 1.34 | +18 / 1.04 | +96 / 1.14 |
| 2010 | 468 | +63 / 2.91 | -28 / 0.82 | +155 / 1.98 | +229 / 2.27 | -142 / 0.70 |
| 2011 | 688 | -50 / 0.57 | -35 / 0.85 | +22 / 1.08 | +219 / 1.69 | +305 / 1.75 |
| 2012 | 245 | -26 / 0.60 | -48 / 0.69 | -51 / 0.77 | -69 / 0.77 | -58 / 0.86 |
| 2013 | 287 | -0 / 0.99 | -23 / 0.84 | +31 / 1.16 | +45 / 1.20 | +111 / 1.36 |
| 2014 | 548 | -18 / 0.78 | +29 / 1.16 | +8 / 1.03 | -24 / 0.94 | +67 / 1.14 |
| 2015 | 708 | +9 / 1.13 | -37 / 0.81 | +14 / 1.05 | +33 / 1.10 | -83 / 0.85 |
| 2016 | 978 | -29 / 0.69 | +8 / 1.05 | +20 / 1.07 | +73 / 1.22 | +218 / 1.53 |
| 2017 | 409 | -9 / 0.87 | -31 / 0.83 | -39 / 0.84 | +2 / 1.01 | +66 / 1.19 |
| 2018 | 872 | +25 / 1.58 | -26 / 0.85 | -11 / 0.96 | +59 / 1.22 | +103 / 1.27 |
| 2019 | 561 | +2 / 1.03 | +35 / 1.23 | +6 / 1.02 | -19 / 0.95 | -17 / 0.97 |
| 2020 | 2,990 | -121 / 0.52 | -221 / 0.49 | -87 / 0.84 | +205 / 1.37 | +170 / 1.25 |
| 2021 | 2,584 | +19 / 1.22 | -70 / 0.72 | -20 / 0.94 | +121 / 1.35 | +20 / 1.04 |
| 2022 | 3,049 | -19 / 0.83 | +1 / 1.00 | -111 / 0.75 | -85 / 0.82 | -182 / 0.74 |
| 2023 | 723 | -14 / 0.86 | -10 / 0.95 | +2 / 1.01 | -88 / 0.80 | -74 / 0.87 |
| 2024 | 763 | +5 / 1.04 | +67 / 1.32 | +89 / 1.24 | +59 / 1.12 | +38 / 1.06 |
| 2025 | 1,987 | +76 / 1.71 | -140 / 0.54 | +31 / 1.10 | -62 / 0.87 | +96 / 1.16 |
| 2026 | 1,149 | +20 / 1.23 | +26 / 1.12 | +61 / 1.15 | -3 / 0.99 | +84 / 1.13 |

### Verdict — ⚠⚠ THE SPRING INVERTS ON DAILY BARS: the reversal close is the FADE, the failed reversal is the buy

- **T1**: every "decline then close above the previous close" band is flat-to-negative at every
  horizon, and MORE negative the deeper the decline (−10..−15%: r5 −133 bp / PF 0.83, 4/22 years;
  −15..−25%: −303 / 0.73). The **CONTROL — same decline, closed BELOW the previous close — is
  positive** and the deepest control cell (<−25%, close ≤ prev) reads **r5 +671 bp / PF 1.75**
  (r_open1 +308 / 2.36). That is the LowFlyer / overnight-reversal capitulation cell
  (`project_overnight_reversal`), re-derived: **long buys WEAKNESS**; a day that already bounced
  into the close has spent the bounce.
- **T2**: measured from the open the picture is the same, only worse for the spring (every band
  negative, 5-8/22 years) and better for the control (<−25% from the open, closed ≤ open:
  r5 +1,427 / PF 2.20).
- **T3**: the 7-day-low break does not rescue it — the spring with a break is ~flat (r5 +20..+36
  in the 0..−5% break bands, 10-15/22 years, r1 negative in all of them); the spring WITHOUT a
  break is −45 / 0.93. The control with a break: +63 / 1.17, **21/22 years**.
- **T4 — the cleanest gradient on the page and it points the same way**: the spring day that
  **closed AT its high** (clpos 0.95-1) is the worst cell, r5 −152 / PF 0.72; the PARTIAL reclaim
  (clpos 0.5-0.7) is the best, +129 / 1.25, 16/22. The stronger the close, the worse the trade.
- T5-T7: rvol, the 20-day context and the liquidity tier do not produce a cell above PF 1.3 at
  r5 with a majority of years, except `chg20 < −30%` (+187 / 1.29, 14/22 — again the already-
  crushed name, the MR family). T8: the headline cell's year table is streaky (2008 −200, 2010
  +229, 2020 +205, 2022 −85) — regime-driven, no sign.

**What the daily bars say**: on the daily close, the intraday reversal is PRICED. The edge in
"large decline → close" is the same edge every MR system here already trades — buy the flush,
sell the recovery — and a close-above-reference filter selects the days where the recovery has
already happened. The Wyckoff spring as a CLOSE-of-day entry does not exist in this universe at
$5M+/day; if it exists at all it is an intraday entry (buy the reclaim as it happens, sell the
strong close), which is the LongHiker S39 shape with a longer hold, or a next-day pattern (the
spring's follow-through after a weak open — untested).

## S2 — the SHORT side, OPEN as the reference (user, 2026-09-07)

> USER: *"It seems we found a good short setup rather than a reversal long. What if we used the open
> as a reference point?"*

Script `scripts/equity/springflyer_short.py`, log `data/springflyer_short.log`. SHORT at D's close
after a decline from the OPEN that closed back ABOVE the open; cover at D+k's close. Returns are
SHORT returns (bp, + = profit). `mfe5` = the highest high over the next 5 days vs the entry (the
short's adverse excursion); its median and p95 are on every row. Per-signal attribution, same
FRAME as S1.

### T1 — SHORT the reversal: decline from the OPEN (low vs open) x closed ABOVE the open; and the prev-close reference beside it
| cell | n/yr | r_open1: short mean bp / PF | r1: short mean bp / PF | r3: short mean bp / PF | r5: short mean bp / PF | r10: short mean bp / PF | win5 | yrs5 | med mfe5 | p95 mfe5 |
|---|---|---|---|---|---|---|---|---|---|---|
| open ref: decl_open -3..-5%, close > open | 7,286 | -9 / 0.90 | +8 / 1.04 | +7 / 1.02 | +12 / 1.03 | -3 / 0.99 | 51% | 14/22 | +574 | +2615 |
| open ref: decl_open -5..-7%, close > open | 1,564 | +3 / 1.03 | +24 / 1.09 | +33 / 1.08 | +68 / 1.13 | +69 / 1.10 | 54% | 14/22 | +704 | +3526 |
| open ref: decl_open -7..-10%, close > open | 600 | +16 / 1.09 | +30 / 1.09 | +74 / 1.14 | +120 / 1.20 | +108 / 1.14 | 55% | 15/22 | +811 | +4521 |
| open ref: decl_open -10..-15%, close > open | 194 | +22 / 1.11 | +56 / 1.13 | +192 / 1.32 | +220 / 1.30 | +260 / 1.29 | 58% | 16/22 | +969 | +6074 |
| open ref: decl_open -15..-25%, close > open | 43 | +40 / 1.13 | +190 / 1.40 | +328 / 1.48 | +245 / 1.26 | +415 / 1.39 | 57% | 17/22 | +1236 | +8277 |
| open ref: decl_open <-25%, close > open | 9 | +145 / 1.35 | +267 / 1.54 | +381 / 1.54 | +541 / 1.60 | +89 / 1.05 | 60% | 13/16 | +648 | +10328 |
| prev ref: decl_prev -3..-5%, close > prev | 7,631 | -7 / 0.91 | +8 / 1.04 | -3 / 0.99 | +0 / 1.00 | -27 / 0.95 | 50% | 8/22 | +543 | +2387 |
| prev ref: decl_prev -5..-7%, close > prev | 1,722 | -8 / 0.93 | +12 / 1.05 | -20 / 0.95 | +9 / 1.02 | -21 / 0.97 | 52% | 11/22 | +690 | +3202 |
| prev ref: decl_prev -7..-10%, close > prev | 663 | -8 / 0.95 | +21 / 1.07 | +9 / 1.02 | -2 / 1.00 | +20 / 1.03 | 52% | 10/22 | +833 | +4046 |
| prev ref: decl_prev -10..-15%, close > prev | 224 | -10 / 0.95 | +40 / 1.11 | +140 / 1.27 | +133 / 1.20 | +226 / 1.29 | 56% | 18/22 | +927 | +5201 |
| prev ref: decl_prev -15..-25%, close > prev | 51 | +24 / 1.10 | +67 / 1.14 | +256 / 1.37 | +303 / 1.36 | +592 / 1.65 | 60% | 15/22 | +1173 | +7746 |
| prev ref: decl_prev <-25%, close > prev | 8 | +303 / 2.68 | +501 / 2.97 | +638 / 2.25 | +804 / 2.25 | +286 / 1.17 | 69% | 14/18 | +593 | +9206 |
| BOTH: decl_open -3..-5%, close > open AND > prev | 5,706 | -8 / 0.91 | +9 / 1.05 | +9 / 1.03 | +23 / 1.06 | +8 / 1.01 | 52% | 14/22 | +579 | +2682 |
| BOTH: decl_open -5..-7%, close > open AND > prev | 1,194 | +10 / 1.08 | +33 / 1.12 | +33 / 1.07 | +92 / 1.18 | +73 / 1.11 | 55% | 17/22 | +711 | +3610 |
| BOTH: decl_open -7..-10%, close > open AND > prev | 440 | +29 / 1.17 | +42 / 1.12 | +86 / 1.16 | +156 / 1.25 | +101 / 1.12 | 56% | 15/22 | +838 | +4698 |
| BOTH: decl_open -10..-15%, close > open AND > prev | 135 | +38 / 1.17 | +101 / 1.24 | +215 / 1.33 | +326 / 1.44 | +306 / 1.33 | 61% | 19/22 | +989 | +6303 |
| BOTH: decl_open -15..-25%, close > open AND > prev | 26 | +129 / 1.41 | +332 / 1.69 | +528 / 1.79 | +586 / 1.72 | +743 / 1.81 | 59% | 19/20 | +1264 | +8271 |
| BOTH: decl_open <-25%, close > open AND > prev | 6 | +415 / 2.70 | +527 / 2.43 | +724 / 2.31 | +829 / 2.10 | -328 / 0.86 | 70% | 13/15 | +400 | +10328 |

### T2 — how far ABOVE the open did it close? (decline from open <= -7%)
| cell | n/yr | r_open1: short mean bp / PF | r1: short mean bp / PF | r3: short mean bp / PF | r5: short mean bp / PF | r10: short mean bp / PF | win5 | yrs5 | med mfe5 | p95 mfe5 |
|---|---|---|---|---|---|---|---|---|---|---|
| rev_open 0..+1% | 180 | +26 / 1.20 | +33 / 1.12 | +58 / 1.13 | +45 / 1.08 | +48 / 1.06 | 53% | 13/22 | +698 | +3988 |
| rev_open +1..+3% | 253 | +33 / 1.23 | +43 / 1.14 | +73 / 1.15 | +75 / 1.12 | +95 / 1.13 | 54% | 12/22 | +800 | +4326 |
| rev_open +3..+6% | 191 | +30 / 1.17 | +51 / 1.15 | +155 / 1.30 | +169 / 1.27 | +119 / 1.15 | 56% | 18/22 | +865 | +4683 |
| rev_open +6..+10% | 113 | +18 / 1.09 | +35 / 1.09 | +108 / 1.18 | +180 / 1.25 | +95 / 1.10 | 57% | 14/22 | +992 | +5531 |
| rev_open +10%+ | 109 | -37 / 0.91 | +83 / 1.13 | +262 / 1.31 | +466 / 1.51 | +623 / 1.57 | 63% | 19/22 | +1273 | +8954 |

### T3 — the close's position in the range (decline from open <= -7%, close > open)
| cell | n/yr | r_open1: short mean bp / PF | r1: short mean bp / PF | r3: short mean bp / PF | r5: short mean bp / PF | r10: short mean bp / PF | win5 | yrs5 | med mfe5 | p95 mfe5 |
|---|---|---|---|---|---|---|---|---|---|---|
| clpos 0.5-0.7 | 119 | -88 / 0.71 | -89 / 0.84 | +7 / 1.01 | +98 / 1.12 | +197 / 1.19 | 57% | 14/22 | +1228 | +7202 |
| clpos 0.7-0.85 | 226 | -22 / 0.89 | -14 / 0.97 | +27 / 1.05 | +68 / 1.10 | +156 / 1.18 | 54% | 14/22 | +945 | +5302 |
| clpos 0.85-0.95 | 259 | +22 / 1.15 | +42 / 1.14 | +102 / 1.22 | +81 / 1.14 | +69 / 1.09 | 53% | 13/22 | +797 | +4240 |
| clpos 0.95-1 | 210 | +140 / 2.16 | +200 / 1.79 | +311 / 1.74 | +379 / 1.75 | +200 / 1.29 | 59% | 17/22 | +620 | +3600 |

### T4 — the gap that set it up (decline from open <= -7%, close > open)
| cell | n/yr | r_open1: short mean bp / PF | r1: short mean bp / PF | r3: short mean bp / PF | r5: short mean bp / PF | r10: short mean bp / PF | win5 | yrs5 | med mfe5 | p95 mfe5 |
|---|---|---|---|---|---|---|---|---|---|---|
| gap <-5% | 150 | -29 / 0.87 | -5 / 0.99 | +187 / 1.35 | +100 / 1.14 | +321 / 1.42 | 55% | 14/22 | +919 | +5972 |
| gap -5..-1% | 197 | -26 / 0.85 | -30 / 0.91 | +21 / 1.04 | -32 / 0.95 | +120 / 1.15 | 52% | 13/22 | +871 | +4718 |
| gap -1..+1% | 194 | +22 / 1.20 | +61 / 1.23 | +92 / 1.22 | +175 / 1.32 | +14 / 1.02 | 56% | 16/22 | +704 | +4072 |
| gap +1..+5% | 176 | +44 / 1.25 | +49 / 1.14 | +24 / 1.04 | +123 / 1.19 | -83 / 0.91 | 55% | 15/22 | +935 | +4619 |
| gap +5..+15% | 94 | +161 / 1.69 | +208 / 1.49 | +360 / 1.54 | +508 / 1.78 | +509 / 1.66 | 64% | 17/22 | +876 | +5640 |
| gap +15%+ | 35 | -26 / 0.95 | +185 / 1.27 | +330 / 1.35 | +526 / 1.54 | +742 / 1.63 | 65% | 15/21 | +975 | +10911 |

### T5 — 7d-low break and the 20-day context (decline from open <= -7%, close > open)
| cell | n/yr | r_open1: short mean bp / PF | r1: short mean bp / PF | r3: short mean bp / PF | r5: short mean bp / PF | r10: short mean bp / PF | win5 | yrs5 | med mfe5 | p95 mfe5 |
|---|---|---|---|---|---|---|---|---|---|---|
| broke the 7d low | 401 | +5 / 1.03 | +35 / 1.12 | +106 / 1.25 | +67 / 1.12 | +77 / 1.12 | 51% | 15/22 | +736 | +3717 |
| did NOT break the 7d low | 446 | +33 / 1.15 | +58 / 1.14 | +128 / 1.19 | +232 / 1.31 | +231 / 1.23 | 60% | 19/22 | +979 | +6389 |
| chg20 < -15% | 324 | +23 / 1.11 | +4 / 1.01 | +37 / 1.06 | -24 / 0.97 | -26 / 0.97 | 51% | 10/22 | +1075 | +4926 |
| chg20 -15..+15% | 243 | +7 / 1.06 | +67 / 1.31 | +123 / 1.37 | +202 / 1.51 | +158 / 1.28 | 56% | 15/22 | +557 | +2888 |
| chg20 +15..+50% | 115 | +11 / 1.08 | +25 / 1.08 | +36 / 1.07 | +126 / 1.22 | +132 / 1.16 | 57% | 14/22 | +785 | +4530 |
| chg20 +50%+ | 164 | +40 / 1.14 | +118 / 1.23 | +327 / 1.44 | +456 / 1.52 | +548 / 1.45 | 65% | 19/22 | +1194 | +8833 |

### T6 — relative volume and liquidity (decline from open <= -7%, close > open)
| cell | n/yr | r_open1: short mean bp / PF | r1: short mean bp / PF | r3: short mean bp / PF | r5: short mean bp / PF | r10: short mean bp / PF | win5 | yrs5 | med mfe5 | p95 mfe5 |
|---|---|---|---|---|---|---|---|---|---|---|
| rvol <1 | 264 | +32 / 1.20 | +16 / 1.04 | +101 / 1.17 | +141 / 1.19 | +84 / 1.08 | 58% | 13/22 | +937 | +5515 |
| rvol 1-2 | 344 | +35 / 1.20 | +63 / 1.19 | +151 / 1.30 | +163 / 1.27 | +138 / 1.19 | 54% | 17/22 | +858 | +4365 |
| rvol 2-4 | 152 | -27 / 0.87 | +7 / 1.02 | -1 / 1.00 | +58 / 1.09 | +129 / 1.17 | 54% | 11/22 | +796 | +5323 |
| rvol 4+ | 86 | +9 / 1.03 | +148 / 1.32 | +246 / 1.40 | +331 / 1.47 | +517 / 1.67 | 60% | 17/22 | +700 | +6289 |
| dv20 $5-20M | 428 | +26 / 1.17 | +41 / 1.12 | +98 / 1.19 | +140 / 1.23 | +145 / 1.18 | 56% | 18/22 | +805 | +4840 |
| dv20 $20-100M | 294 | +23 / 1.11 | +50 / 1.13 | +125 / 1.22 | +149 / 1.21 | +139 / 1.16 | 56% | 18/22 | +891 | +5421 |
| dv20 $100M+ | 124 | -8 / 0.97 | +58 / 1.15 | +169 / 1.30 | +213 / 1.33 | +250 / 1.32 | 56% | 15/22 | +946 | +5082 |

### T7 — year table, SHORT, cell = decline from open <= -10% AND close > open
decl_open < -0.10 AND rev_open > 0
| year | n | r_open1 | r1 | r3 | r5 | r10 | p95 mfe5 |
|---|---|---|---|---|---|---|---|
| 2005 | 21 | +18 / 1.24 | -171 / 0.42 | -102 / 0.60 | -107 / 0.75 | -155 / 0.73 | +2028 |
| 2006 | 26 | -85 / 0.38 | +165 / 2.99 | +222 / 1.97 | +155 / 1.63 | +25 / 1.06 | +1796 |
| 2007 | 136 | -45 / 0.74 | -28 / 0.87 | +9 / 1.03 | +205 / 1.62 | +48 / 1.13 | +3066 |
| 2008 | 1,252 | -161 / 0.49 | -141 / 0.73 | +16 / 1.03 | +226 / 1.33 | +331 / 1.43 | +5209 |
| 2009 | 171 | +6 / 1.04 | +118 / 1.45 | -15 / 0.97 | -94 / 0.85 | -271 / 0.73 | +4231 |
| 2010 | 41 | +12 / 1.25 | +184 / 4.75 | +162 / 2.35 | +43 / 1.15 | +509 / 3.65 | +1444 |
| 2011 | 42 | +138 / 2.59 | +160 / 2.32 | +20 / 1.08 | -19 / 0.95 | -18 / 0.97 | +1935 |
| 2012 | 19 | +68 / 2.13 | +238 / 2.83 | +15 / 1.04 | +240 / 1.77 | +44 / 1.11 | +2137 |
| 2013 | 26 | -9 / 0.89 | +47 / 1.35 | +136 / 1.39 | +200 / 1.66 | -232 / 0.67 | +3594 |
| 2014 | 53 | -38 / 0.77 | +50 / 1.19 | +274 / 1.82 | +341 / 1.83 | +497 / 2.13 | +3368 |
| 2015 | 183 | -190 / 0.26 | +15 / 1.08 | -229 / 0.55 | -185 / 0.65 | -11 / 0.98 | +2364 |
| 2016 | 113 | +81 / 1.78 | +5 / 1.02 | +270 / 1.71 | +332 / 1.71 | +294 / 1.46 | +3747 |
| 2017 | 78 | -88 / 0.52 | -62 / 0.85 | +56 / 1.10 | +235 / 1.43 | +276 / 1.45 | +6255 |
| 2018 | 118 | -21 / 0.90 | +146 / 1.48 | +249 / 1.54 | +287 / 1.52 | +233 / 1.30 | +6235 |
| 2019 | 78 | +213 / 3.99 | +96 / 1.32 | +327 / 1.89 | +238 / 1.51 | +324 / 1.55 | +5260 |
| 2020 | 915 | +189 / 1.79 | +333 / 1.86 | +467 / 1.73 | +126 / 1.14 | +61 / 1.07 | +6550 |
| 2021 | 557 | +38 / 1.20 | +149 / 1.45 | +243 / 1.52 | +6 / 1.01 | +140 / 1.14 | +5894 |
| 2022 | 258 | +198 / 2.33 | +219 / 1.62 | +503 / 1.96 | +640 / 2.06 | +879 / 2.27 | +4871 |
| 2023 | 214 | +122 / 1.56 | +312 / 1.86 | +623 / 2.39 | +699 / 1.99 | +821 / 1.95 | +6819 |
| 2024 | 335 | +91 / 1.34 | +135 / 1.23 | +423 / 1.57 | +618 / 1.77 | +777 / 1.67 | +10470 |
| 2025 | 463 | +144 / 1.70 | +110 / 1.20 | +384 / 1.50 | +365 / 1.35 | +48 / 1.03 | +10385 |
| 2026 | 328 | +51 / 1.14 | -63 / 0.92 | -95 / 0.93 | +223 / 1.17 | +578 / 1.38 | +13510 |

### Verdict — the open reference is the better short, monotone in the decline, and the STRONGEST close is the best fade

- **T1**: from the open the short is monotone in the decline depth (r5 +12 / +68 / +120 / +220 /
  +245 / +541 bp, PF 1.03 → 1.60, win 51 → 60%, years 14 → 17/22); the prev-close reference is
  weaker at every band and mixed below −10%. Requiring BOTH (closed above the open AND above the
  previous close) is the best row: **−10..−15% from the open: r5 +326 / PF 1.44, 61% win,
  19/22 years, 135/yr; −15..−25%: +586 / 1.72, 19/20 years, 26/yr.**
- **T3 — the S1 gradient again, from the short side**: the day that closed AT its high (clpos
  0.95-1) is the best cell on the page, **r5 +379 / PF 1.75, 17/22**, and — unexpectedly — the
  one with the SMALLEST adverse tail (p95 mfe5 +36% vs +72% for the partial reclaim). The partial
  reclaim (0.5-0.7) is a LOSER at 1 day (−89 / 0.84) and only turns at 5-10 days.
- **T4/T5/T6**: the setup is a RUNNER's fade — gap +5..15% (+508 / 1.78, 17/22), `chg20 +50%+`
  (+456 / 1.52, 19/22), rvol 4+ (+331 / 1.47), and the 7d-low break is NOT the thing (no break
  +232 / 1.31 vs break +67 / 1.12). The names that fell 7%+ from the open and still closed up are
  mostly names that had already run.
- **T7 — era**: 2005-2015 mixed (2008 r_open1 −161, 2015 r5 −185); **2016-2026 positive at r5 in
  10/11 years**, 2022-2024 at PF 1.8-2.1.
- ⚠⚠ **THE TAIL**: p95 `mfe5` is +60-83% in the deep bands and +100%+ in the deepest (a 5-day
  high MORE THAN DOUBLE the entry, in 1 short of 20). This is the ShortSnoozer / overnight-
  reversal "ruinous short" class (`project_shortsnoozer_tail_broken`, `project_overnight_reversal`):
  the mean survives only because the winners are many and the squeezes are few, and no stop is
  modelled. Borrow on these names (runners, rvol 4+) is unmodelled and will be the scarce one.
  **Not a spec until the tail is disqualified** — the S1 clpos 0.95-1 cell's smaller tail is the
  first lead.

### S2 addendum — the `< −25%, close ≤ prev` long cell (+671 / 1.75): CRASH-CLUSTERED and ONE-TRADE-DRIVEN. Not a cell.

> USER: *"Are these trips clustered during the crash periods or are they well distributed? Also is
> the mean pushed by a few big trades?"* — both, badly. Log `data/springflyer_flush25.log`.

- **Clustering**: 10,542 trips on 3,266 days; the top four days are all March 2020 (430 / 279 /
  126 / 118 trips); 52% of trips sit on days with ≥ 5 trips. **2008 alone is 92% of the cell's
  Σr5** (959 trips, r5 mean +6,811 / PF 10.5); the 2008-09..11 + 2020-03..04 crash months read
  r5 +4,231 / PF 5.81 and **everything else reads −60 / 0.93 (win 43%)**; excluding 2008 and
  2020 entirely: r5 −96 / PF 0.89, 9/20 years, r10 −306 / 0.75.
- **Outliers**: median r5 is **−157 bp**. The #1 trade is a DATA ARTIFACT — `WB 2008-09-29`
  (Wachovia) carries a raw close of **$0.01** (low 0.01, next open 2.30): r5 = +5,770,000 bp on
  one row. Drop that ONE trade: mean +671 → +120 / PF 1.13; drop the top 5: +90 / 1.10; top 20:
  +47 / 1.05; top 100 of 10,479: **−66 / 0.93**. The top 20 trades are 40% of all positive P&L.
- ⚠ **Daily-tape hazard, new**: a `$0.01` close on a $10 stock passed every gate here (the raw
  floor is on D−1). Any daily-bar study on this table needs a bad-print guard on the SIGNAL day
  (e.g. `close / prev_close > 0.05` or a cross-check against the next open) — noted for the
  price-adjustment doc, not yet applied anywhere.

**Verdict**: the deep-flush-no-reversal long is a crash-rebound sample plus one bad print. The S1
reading "the failed reversal is the buy" stands only in its shallower bands (−5..−10%, PF 1.08-1.09,
16-18/22 years, +28..+36 bp — the ordinary MR drift), not as a deep-flush cell.

> **The WB 2008-09-29 row, verified against the minute bars** (`data/minute_aggs/2008-09-29.parquet`,
> 442 WB bars): the crash was real — the stock printed ~$1 in the pre-market from 08:34 (the
> Citigroup rescue) and the regular session closed $1.84 — but the tape's minimum print is $0.15
> (one 14:33 bar, 39.6M shares, range 0.15-5.00, the delayed open) and the last post-market bars
> are $2.04-2.05. **No $0.01 trade exists in the minute data.** The `daily_adjusted` row's
> low = close = 0.01 is a defect of the vendor's DAILY flat file, not of the tape — the daily and
> minute files disagree. A bad-print guard on the signal day (or a daily-vs-minute close
> cross-check) is the fix; the minute files exist back to 2003-09 so the check is cheap.

## S3 — the MIRROR: a big intraday RISE that closes below the open — is it a LONG? (user, 2026-09-07)

> USER: *"if this pattern is a good short, would the opposite of it be a good long? For example, a
> stock that goes up a lot during the day and then closes below the open and/or the close."*

Script `scripts/equity/springflyer_mirror.py`, log `data/springflyer_mirror.log`. LONG at the close
after a rise from the open (high vs open) that closed BELOW the open / the previous close. Same
FRAME plus a minimal signal-day guard `close > 0.1 × prev_close` (the WB class).

### T1 — LONG the failed rally: RISE from the OPEN (high vs open) x closed BELOW the open; prev-close reference; BOTH; and the S2 short's own cells for reference (negated = what the mirror must beat)
| cell | n/yr | r_open1: long mean bp / PF | r1: long mean bp / PF | r3: long mean bp / PF | r5: long mean bp / PF | r10: long mean bp / PF | win5 | yrs5 | med r5 | med mae5 |
|---|---|---|---|---|---|---|---|---|---|---|
| open ref: rise_open +3..+5%, close < open | 7,185 | +16 / 1.20 | +24 / 1.12 | +39 / 1.12 | +44 / 1.11 | +48 / 1.08 | 50% | 18/22 | +7 | -601 |
| open ref: rise_open +5..+7%, close < open | 1,556 | +27 / 1.26 | +33 / 1.13 | +49 / 1.12 | +49 / 1.09 | +31 / 1.04 | 48% | 14/22 | -38 | -784 |
| open ref: rise_open +7..+10%, close < open | 616 | +39 / 1.31 | +27 / 1.08 | +55 / 1.10 | +19 / 1.03 | -48 / 0.95 | 46% | 10/22 | -134 | -977 |
| open ref: rise_open +10..+15%, close < open | 230 | +46 / 1.26 | -17 / 0.96 | -24 / 0.97 | -37 / 0.96 | -160 / 0.86 | 44% | 11/22 | -225 | -1268 |
| open ref: rise_open +15..+25%, close < open | 79 | +54 / 1.20 | -81 / 0.87 | -202 / 0.80 | -433 / 0.66 | -727 / 0.56 | 35% | 7/22 | -739 | -1756 |
| open ref: rise_open +25%+, close < open | 24 | +127 / 1.38 | -311 / 0.62 | -477 / 0.64 | -822 / 0.52 | -1395 / 0.36 | 28% | 2/20 | -1195 | -2308 |
| prev ref: rise_prev +3..+5%, close < prev | 7,758 | +9 / 1.12 | +18 / 1.10 | +42 / 1.14 | +52 / 1.14 | +60 / 1.11 | 51% | 17/22 | +20 | -561 |
| prev ref: rise_prev +5..+7%, close < prev | 1,753 | +12 / 1.13 | +25 / 1.10 | +69 / 1.18 | +62 / 1.12 | +52 / 1.08 | 50% | 12/22 | +0 | -725 |
| prev ref: rise_prev +7..+10%, close < prev | 708 | +11 / 1.09 | -4 / 0.99 | +85 / 1.18 | +67 / 1.11 | +15 / 1.02 | 47% | 12/22 | -64 | -876 |
| prev ref: rise_prev +10..+15%, close < prev | 252 | -5 / 0.97 | -73 / 0.82 | -19 / 0.97 | -37 / 0.95 | -180 / 0.83 | 44% | 7/22 | -195 | -1115 |
| prev ref: rise_prev +15..+25%, close < prev | 84 | +9 / 1.04 | -98 / 0.81 | -127 / 0.84 | -284 / 0.74 | -447 / 0.69 | 38% | 5/22 | -499 | -1518 |
| prev ref: rise_prev +25%+, close < prev | 27 | +87 / 1.24 | -289 / 0.66 | -655 / 0.52 | -805 / 0.52 | -1446 / 0.35 | 30% | 7/21 | -1197 | -2226 |
| BOTH: rise_open +3..+5%, close < open AND < prev | 5,709 | +16 / 1.19 | +26 / 1.14 | +44 / 1.13 | +51 / 1.12 | +52 / 1.09 | 50% | 19/22 | +12 | -617 |
| BOTH: rise_open +5..+7%, close < open AND < prev | 1,205 | +29 / 1.29 | +42 / 1.16 | +66 / 1.16 | +68 / 1.12 | +37 / 1.05 | 49% | 14/22 | -31 | -811 |
| BOTH: rise_open +7..+10%, close < open AND < prev | 462 | +45 / 1.35 | +42 / 1.13 | +74 / 1.14 | +35 / 1.05 | -30 / 0.97 | 46% | 11/22 | -125 | -1006 |
| BOTH: rise_open +10..+15%, close < open AND < prev | 168 | +55 / 1.32 | -3 / 0.99 | -12 / 0.98 | -1 / 1.00 | -127 / 0.89 | 45% | 11/22 | -174 | -1291 |
| BOTH: rise_open +15..+25%, close < open AND < prev | 55 | +88 / 1.37 | -79 / 0.87 | -176 / 0.82 | -356 / 0.72 | -620 / 0.62 | 36% | 6/22 | -684 | -1759 |
| BOTH: rise_open +25%+, close < open AND < prev | 15 | +237 / 1.82 | -270 / 0.68 | -744 / 0.49 | -1170 / 0.37 | -1625 / 0.30 | 28% | 3/20 | -1278 | -2463 |
| CONTROL: rise_open +3..+5%, close >= open (rally held) | 42,666 | +6 / 1.13 | +3 / 1.03 | +8 / 1.04 | +13 / 1.05 | +35 / 1.09 | 51% | 16/22 | +10 | -379 |
| CONTROL: rise_open +5..+7%, close >= open (rally held) | 14,479 | +11 / 1.17 | +3 / 1.02 | +13 / 1.05 | +11 / 1.03 | +36 / 1.08 | 50% | 14/22 | -4 | -488 |
| CONTROL: rise_open +7..+10%, close >= open (rally held) | 7,603 | +18 / 1.23 | +7 / 1.04 | +18 / 1.06 | +7 / 1.02 | +42 / 1.08 | 49% | 13/22 | -27 | -601 |
| CONTROL: rise_open +10..+15%, close >= open (rally held) | 3,490 | +25 / 1.25 | +8 / 1.03 | +14 / 1.03 | -19 / 0.96 | +9 / 1.01 | 46% | 9/22 | -91 | -758 |
| CONTROL: rise_open +15..+25%, close >= open (rally held) | 1,284 | +48 / 1.35 | +8 / 1.02 | -2 / 1.00 | -59 / 0.92 | -67 / 0.93 | 44% | 4/22 | -207 | -1011 |
| CONTROL: rise_open +25%+, close >= open (rally held) | 394 | +99 / 1.34 | -4 / 0.99 | -118 / 0.88 | -208 / 0.82 | -514 / 0.67 | 38% | 5/22 | -616 | -1616 |

### T2 — how far BELOW the open did it close? (rise from open >= +7%)
| cell | n/yr | r_open1: long mean bp / PF | r1: long mean bp / PF | r3: long mean bp / PF | r5: long mean bp / PF | r10: long mean bp / PF | win5 | yrs5 | med r5 | med mae5 |
|---|---|---|---|---|---|---|---|---|---|---|
| rev_open 0..-1% | 198 | +49 / 1.47 | +24 / 1.08 | +21 / 1.04 | -15 / 0.98 | -30 / 0.96 | 46% | 9/22 | -122 | -876 |
| rev_open -1..-3% | 278 | +47 / 1.38 | -3 / 0.99 | +5 / 1.01 | -27 / 0.96 | -36 / 0.96 | 45% | 13/22 | -136 | -996 |
| rev_open -3..-6% | 221 | +52 / 1.37 | +12 / 1.03 | +57 / 1.09 | +16 / 1.02 | -118 / 0.89 | 45% | 10/22 | -175 | -1098 |
| rev_open -6..-10% | 130 | +32 / 1.18 | -31 / 0.93 | -25 / 0.97 | -68 / 0.93 | -248 / 0.80 | 43% | 8/22 | -300 | -1355 |
| rev_open -10%+ | 121 | +32 / 1.10 | -29 / 0.96 | -117 / 0.89 | -285 / 0.79 | -677 / 0.61 | 38% | 7/22 | -739 | -1877 |

### T3 — the close's position in the range (rise from open >= +7%, close < open)
| cell | n/yr | r_open1: long mean bp / PF | r1: long mean bp / PF | r3: long mean bp / PF | r5: long mean bp / PF | r10: long mean bp / PF | win5 | yrs5 | med r5 | med mae5 |
|---|---|---|---|---|---|---|---|---|---|---|
| clpos 0-0.05 (closed at the low) | 194 | +94 / 1.77 | +47 / 1.14 | +56 / 1.10 | -15 / 0.98 | -65 / 0.93 | 46% | 11/22 | -120 | -971 |
| clpos 0.05-0.15 | 339 | +46 / 1.33 | -2 / 0.99 | +27 / 1.05 | -62 / 0.92 | -146 / 0.86 | 44% | 8/22 | -193 | -1054 |
| clpos 0.15-0.3 | 283 | +34 / 1.20 | -12 / 0.97 | -4 / 0.99 | -41 / 0.95 | -181 / 0.83 | 44% | 9/22 | -221 | -1143 |
| clpos 0.3-0.5 | 116 | -10 / 0.96 | -42 / 0.92 | -130 / 0.83 | -88 / 0.91 | -275 / 0.79 | 42% | 8/22 | -337 | -1344 |

### T4 — the gap that set it up (rise from open >= +7%, close < open)
| cell | n/yr | r_open1: long mean bp / PF | r1: long mean bp / PF | r3: long mean bp / PF | r5: long mean bp / PF | r10: long mean bp / PF | win5 | yrs5 | med r5 | med mae5 |
|---|---|---|---|---|---|---|---|---|---|---|
| gap <-15% | 49 | +97 / 1.33 | +73 / 1.13 | -128 / 0.86 | -331 / 0.72 | -408 / 0.71 | 38% | 7/22 | -505 | -1549 |
| gap -15..-5% | 126 | +142 / 1.97 | +178 / 1.49 | +22 / 1.03 | +18 / 1.02 | -79 / 0.93 | 46% | 13/22 | -158 | -1269 |
| gap -5..-1% | 195 | +60 / 1.49 | +32 / 1.09 | +89 / 1.15 | +49 / 1.06 | -51 / 0.95 | 45% | 9/22 | -157 | -1102 |
| gap -1..+1% | 208 | +27 / 1.25 | -27 / 0.92 | +30 / 1.06 | +8 / 1.01 | -81 / 0.91 | 46% | 11/22 | -92 | -909 |
| gap +1..+5% | 198 | +3 / 1.02 | -75 / 0.79 | +15 / 1.03 | -24 / 0.97 | -82 / 0.91 | 45% | 9/22 | -149 | -1025 |
| gap +5%+ | 172 | +9 / 1.04 | -75 / 0.85 | -129 / 0.84 | -250 / 0.75 | -480 / 0.64 | 39% | 5/22 | -432 | -1289 |

### T5 — 20-day context, 20d-high break, rvol, liquidity (rise from open >= +7%, close < open)
| cell | n/yr | r_open1: long mean bp / PF | r1: long mean bp / PF | r3: long mean bp / PF | r5: long mean bp / PF | r10: long mean bp / PF | win5 | yrs5 | med r5 | med mae5 |
|---|---|---|---|---|---|---|---|---|---|---|
| broke the 20d HIGH intraday | 220 | +16 / 1.10 | -29 / 0.92 | -52 / 0.91 | -77 / 0.89 | -202 / 0.79 | 43% | 7/22 | -177 | -918 |
| did NOT break the 20d high | 728 | +53 / 1.34 | +7 / 1.02 | +17 / 1.03 | -46 / 0.94 | -152 / 0.86 | 44% | 10/22 | -221 | -1169 |
| chg20 < -30% | 200 | +87 / 1.54 | +121 / 1.30 | +280 / 1.44 | +278 / 1.35 | +189 / 1.20 | 51% | 13/22 | +57 | -1240 |
| chg20 -30..-10% | 166 | +51 / 1.47 | +17 / 1.06 | +22 / 1.04 | -19 / 0.97 | -29 / 0.97 | 47% | 11/22 | -94 | -922 |
| chg20 -10..+10% | 159 | +26 / 1.26 | -13 / 0.95 | +0 / 1.00 | -37 / 0.93 | -133 / 0.83 | 45% | 11/22 | -105 | -761 |
| chg20 +10..+50% | 189 | +50 / 1.38 | -11 / 0.97 | -43 / 0.92 | -101 / 0.85 | -155 / 0.84 | 43% | 6/22 | -224 | -985 |
| chg20 +50%+ | 234 | +12 / 1.05 | -102 / 0.82 | -216 / 0.77 | -332 / 0.73 | -593 / 0.64 | 36% | 6/22 | -712 | -1695 |
| rvol <1 | 368 | +35 / 1.24 | -29 / 0.93 | -85 / 0.88 | -150 / 0.84 | -311 / 0.75 | 41% | 10/22 | -345 | -1280 |
| rvol 1-2 | 313 | +66 / 1.50 | +59 / 1.17 | +122 / 1.22 | +91 / 1.13 | +67 / 1.08 | 48% | 10/22 | -43 | -1010 |
| rvol 2-4 | 165 | +72 / 1.47 | +14 / 1.04 | +35 / 1.06 | +6 / 1.01 | -157 / 0.84 | 45% | 9/22 | -157 | -937 |
| rvol 4+ | 103 | -30 / 0.89 | -107 / 0.80 | -114 / 0.85 | -242 / 0.74 | -357 / 0.69 | 41% | 6/22 | -313 | -1123 |
| dv20 $5-20M | 476 | +32 / 1.23 | -7 / 0.98 | +6 / 1.01 | -36 / 0.95 | -121 / 0.88 | 45% | 10/22 | -159 | -1030 |
| dv20 $20-100M | 325 | +43 / 1.26 | -11 / 0.97 | -34 / 0.95 | -89 / 0.90 | -211 / 0.81 | 43% | 8/22 | -280 | -1190 |
| dv20 $100M+ | 147 | +89 / 1.47 | +41 / 1.10 | +64 / 1.09 | -29 / 0.97 | -200 / 0.83 | 44% | 12/22 | -231 | -1213 |

### T6 — year table, LONG, cell = rise from open >= +10% AND close < open
(high/open - 1) >= 0.10 AND rev_open < 0
| year | n | r_open1 | r1 | r3 | r5 | r10 | med mae5 |
|---|---|---|---|---|---|---|---|
| 2005 | 21 | +481 / 11.62 | +249 / 1.94 | +19 / 1.04 | +405 / 1.73 | +511 / 1.73 | -1011 |
| 2006 | 29 | +231 / 5.66 | +209 / 2.11 | +436 / 2.45 | +154 / 1.28 | +130 / 1.22 | -366 |
| 2007 | 125 | +54 / 1.80 | -32 / 0.86 | -9 / 0.98 | +84 / 1.19 | -29 / 0.94 | -597 |
| 2008 | 786 | +88 / 1.49 | +46 / 1.11 | +155 / 1.22 | +169 / 1.21 | -232 / 0.81 | -1405 |
| 2009 | 168 | +60 / 1.44 | +14 / 1.04 | +4 / 1.01 | +79 / 1.12 | +656 / 2.02 | -1067 |
| 2010 | 35 | +169 / 6.68 | -43 / 0.83 | +157 / 1.76 | +397 / 3.00 | -107 / 0.81 | -389 |
| 2011 | 50 | +97 / 2.04 | +183 / 1.86 | +405 / 2.19 | +386 / 1.92 | -36 / 0.95 | -356 |
| 2012 | 37 | +125 / 2.27 | -45 / 0.88 | +142 / 1.47 | +82 / 1.19 | -188 / 0.77 | -605 |
| 2013 | 45 | +26 / 1.18 | -184 / 0.59 | -184 / 0.72 | -503 / 0.41 | -539 / 0.53 | -1193 |
| 2014 | 86 | +55 / 1.27 | -174 / 0.66 | -126 / 0.84 | -681 / 0.41 | -828 / 0.36 | -1470 |
| 2015 | 113 | +95 / 1.91 | +26 / 1.08 | -6 / 0.99 | -90 / 0.89 | +89 / 1.09 | -1107 |
| 2016 | 129 | +113 / 1.89 | -133 / 0.73 | -339 / 0.53 | -349 / 0.60 | -395 / 0.65 | -1206 |
| 2017 | 126 | +32 / 1.10 | -102 / 0.81 | -526 / 0.54 | -601 / 0.54 | -874 / 0.46 | -1687 |
| 2018 | 150 | -71 / 0.74 | -266 / 0.54 | -403 / 0.57 | -528 / 0.53 | -896 / 0.40 | -1391 |
| 2019 | 158 | -9 / 0.95 | -145 / 0.68 | +173 / 1.27 | -121 / 0.87 | -523 / 0.59 | -1236 |
| 2020 | 1,110 | +127 / 1.73 | +62 / 1.15 | +157 / 1.23 | +265 / 1.32 | +420 / 1.54 | -1271 |
| 2021 | 972 | +109 / 1.53 | +44 / 1.09 | +11 / 1.01 | -127 / 0.87 | -233 / 0.81 | -1418 |
| 2022 | 534 | -107 / 0.60 | -196 / 0.67 | -303 / 0.68 | -430 / 0.64 | -930 / 0.43 | -1557 |
| 2023 | 430 | -4 / 0.98 | -117 / 0.80 | -196 / 0.77 | -255 / 0.76 | -799 / 0.50 | -1599 |
| 2024 | 677 | +7 / 1.03 | -202 / 0.67 | -280 / 0.72 | -368 / 0.71 | -541 / 0.69 | -1656 |
| 2025 | 916 | +17 / 1.07 | -129 / 0.80 | -347 / 0.66 | -539 / 0.60 | -645 / 0.64 | -1654 |
| 2026 | 622 | +70 / 1.27 | -51 / 0.92 | -253 / 0.76 | -566 / 0.62 | -1144 / 0.45 | -1896 |

### T7 — the mirror read as a SHORT (spike from the open, closed below the open), short returns, with the S2 short beside it; all years and 2016+
| cell | era | n/yr | r1 short / PF | r5 short / PF | r10 short / PF | win5 | yrs5 | p95 mfe5 (5d high) |
|---|---|---|---|---|---|---|---|---|
| SPIKE-FAIL: rise_open +10..+15%, close < open | all | 230 | +17 / 1.04 | +37 / 1.04 | +160 / 1.16 | 55% | 11/22 | +6978 |
| SPIKE-FAIL: rise_open +10..+15%, close < open | 2016+ | 358 | +34 / 1.08 | +94 / 1.11 | +212 / 1.20 | 56% | 8/11 | +7299 |
| SPIKE-FAIL: rise_open +15..+25%, close < open | all | 79 | +81 / 1.15 | +433 / 1.52 | +727 / 1.77 | 65% | 15/22 | +9278 |
| SPIKE-FAIL: rise_open +15..+25%, close < open | 2016+ | 132 | +102 / 1.19 | +493 / 1.59 | +782 / 1.81 | 67% | 9/11 | +9716 |
| SPIKE-FAIL: rise_open +25%+, close < open | all | 24 | +314 / 1.61 | +821 / 1.93 | +1398 / 2.77 | 72% | 18/20 | +13024 |
| SPIKE-FAIL: rise_open +25%+, close < open | 2016+ | 39 | +323 / 1.58 | +921 / 1.95 | +1573 / 2.89 | 73% | 11/11 | +13705 |
| SPIKE-HELD: rise_open +15%+, close >= open (control) | all | 1,678 | -5 / 0.99 | +94 / 1.13 | +172 / 1.19 | 57% | 19/22 | +5909 |
| SPIKE-HELD: rise_open +15%+, close >= open (control) | 2016+ | 2,496 | +5 / 1.01 | +109 / 1.14 | +183 / 1.19 | 58% | 10/11 | +6518 |
| S2 SPRING: decl_open -10..-15%, close > open | all | 194 | +56 / 1.13 | +220 / 1.30 | +260 / 1.29 | 58% | 16/22 | +6074 |
| S2 SPRING: decl_open -10..-15%, close > open | 2016+ | 251 | +119 / 1.27 | +224 / 1.27 | +297 / 1.30 | 60% | 9/11 | +7004 |
| S2 SPRING: decl_open <-15%, close > open | all | 52 | +204 / 1.42 | +297 / 1.32 | +358 / 1.31 | 57% | 17/22 | +8564 |
| S2 SPRING: decl_open <-15%, close > open | 2016+ | 64 | +398 / 1.80 | +535 / 1.51 | +423 / 1.28 | 62% | 10/11 | +10673 |

### Verdict — NO. The failed spike is a SHORT too, and at depth the STRONGER one

- **T1**: as a long the mirror is negative at every hold past the next open once the rise exceeds
  +10% from the open (r5 −37 / −433 / −822 as the rise deepens; win 44 → 28%; +25%+: 2/20 years).
  The only positive column is the OVERNIGHT (`r_open1` +46..+127, PF 1.2-1.4) — a one-night bounce,
  then down. The "rally held" control is negative at the same depths (−59 / −208), so a large
  intraday spike is a sell whichever way the day closed.
- **T2/T3**: the further below the open it closed, the worse (−10%+: r5 −285, r10 −677); closing AT
  the low is the least bad (the overnight bounce is biggest there, +94 / 1.77) — the S1/S2 gradient
  mirrored: the extreme close mean-reverts one night, then the day's direction resumes.
- **T5**: the runner again — `chg20 +50%+` r5 −332 / 0.73 (36% win); `rvol 4+` −242 / 0.74. The one
  positive long cell is the already-crushed name (`chg20 < −30%`: +278 / 1.35, 13/22) — MR, not this.
- **T6 — era**: 2005-2012 positive as a long, **2013-2026 negative in 12 of 14 years**, 2022-2026
  at PF 0.6-0.76 (r10 PF 0.43-0.69). The failed spike has been a reliable short for a decade.
- **T7 — read as a SHORT, beside the S2 spring short**: rise +15..25% from the open and closed
  below it: **r5 +433 / 1.52, r10 +727 / 1.77, 65% win, 15/22 (2016+: 9/11)**; +25%+: r5 +821 /
  1.93, r10 +1,398 / 2.77, 72% win, **18/20 (2016+: 11/11)** at 24-39/yr. Deeper and more
  consistent than the S2 spring (−15%+ from the open: +297 / 1.32). ⚠ The same tail: p95 of the
  5-day high +70-137% against the entry. Borrow unmodelled.

**What the two sides say together**: the common factor is not the reversal, it is the RANGE. A
name that travels 10-25%+ inside one session — whether it fell and reclaimed (S2) or spiked and
failed (S3) — is lower 5-10 days later, and the failed spike is the cleaner signal (the spring's
close-above-open is a weaker distribution print than the spike's close-below-open). Both are the
small-cap intraday-range fade, and both carry the squeeze tail. A single "big-range day, short at
the close" spec with the close's position as the grade is the natural next rung; the tail
disqualifier (ShortSnoozer's gap × volatility) and borrow are the two things standing between it
and a book.

## S4 — BIG-RANGE DAYS as a short, and the efficiency ratio (user, 2026-09-07)

> USER: *"the common denominator in these patterns is the range. Let's do some studying on big range
> days."* / *"We could also add the efficiency ratio into the mix. On the dailies we'll have to
> calculate it off the closing prices."* / *"What we do know is that breakouts fail when the stock
> has high volatility or a high linear tightness score of above 7.5."*

Script `scripts/equity/springflyer_range.py`, log `data/springflyer_range.log`. New features on
`springflyer_daily.parquet`: `rng = high/low − 1`; `rng_atr = rng / atr20_prior` (the day's range
in units of the stock's own PRIOR 20-day mean range); `er10/er20` = Kaufman efficiency ratio on
share-consistent closes INCLUDING day D, `|cn(D) − cn(D−N)| / Σ|Δcn|`, plus `_signed` twins.
Short at D's close, short returns, same FRAME; `tail>50%` = share of trades whose 5-day high is
more than 50% above the entry.

### T1 — the day's RANGE (high/low − 1), short at the close, no other condition
| cell | n/yr | r_open1: short mean / PF | r1: short mean / PF | r3: short mean / PF | r5: short mean / PF | r10: short mean / PF | win5 | yrs5 | med r5 | p95 mfe5 | tail>50% |
|---|---|---|---|---|---|---|---|---|---|---|---|
| rng 5-7% | 50,166 | -7 / 0.90 | -3 / 0.98 | -11 / 0.96 | -19 / 0.94 | -41 / 0.91 | 49% | 7/22 | -9 | +1877 | 0.3% |
| rng 7-10% | 27,728 | -7 / 0.91 | -3 / 0.98 | -12 / 0.96 | -16 / 0.96 | -40 / 0.93 | 50% | 7/22 | +5 | +2396 | 0.6% |
| rng 10-15% | 13,028 | -11 / 0.90 | -2 / 0.99 | -13 / 0.97 | -8 / 0.98 | -25 / 0.96 | 52% | 10/22 | +43 | +3141 | 1.5% |
| rng 15-25% | 4,801 | -21 / 0.87 | -3 / 0.99 | -13 / 0.98 | -6 / 0.99 | +6 / 1.01 | 53% | 13/22 | +122 | +4506 | 3.9% |
| rng 25-40% | 1,020 | -37 / 0.85 | +3 / 1.01 | +36 / 1.05 | +28 / 1.03 | +221 / 1.22 | 57% | 14/22 | +321 | +7089 | 9.6% |
| rng 40%+ | 376 | -93 / 0.81 | -3 / 1.00 | +73 / 1.07 | +128 / 1.10 | +558 / 1.43 | 61% | 15/22 | +740 | +11294 | 18.7% |

### T1b — the same, 2016+ only
| cell | n/yr | r_open1: short mean / PF | r1: short mean / PF | r3: short mean / PF | r5: short mean / PF | r10: short mean / PF | win5 | yrs5 | med r5 | p95 mfe5 | tail>50% |
|---|---|---|---|---|---|---|---|---|---|---|---|
| rng 5-7% | 63,486 | -7 / 0.90 | -6 / 0.96 | -17 / 0.93 | -29 / 0.92 | -61 / 0.88 | 49% | 3/11 | -11 | +1981 | 0.3% |
| rng 7-10% | 36,388 | -8 / 0.91 | -6 / 0.97 | -18 / 0.95 | -26 / 0.94 | -65 / 0.89 | 50% | 3/11 | +8 | +2524 | 0.8% |
| rng 10-15% | 17,444 | -11 / 0.91 | -2 / 0.99 | -11 / 0.97 | -11 / 0.98 | -57 / 0.92 | 52% | 5/11 | +57 | +3291 | 1.8% |
| rng 15-25% | 6,583 | -15 / 0.91 | +8 / 1.03 | +8 / 1.01 | +1 / 1.00 | -18 / 0.98 | 54% | 7/11 | +154 | +4804 | 4.6% |
| rng 25-40% | 1,506 | -16 / 0.93 | +29 / 1.06 | +84 / 1.11 | +74 / 1.08 | +260 / 1.25 | 58% | 8/11 | +423 | +7757 | 10.5% |
| rng 40%+ | 606 | -49 / 0.90 | +42 / 1.05 | +172 / 1.16 | +239 / 1.18 | +688 / 1.52 | 64% | 10/11 | +962 | +12295 | 19.8% |

### T2 — the range in units of the stock's OWN prior 20-day average range (rng_atr)
| cell | n/yr | r_open1: short mean / PF | r1: short mean / PF | r3: short mean / PF | r5: short mean / PF | r10: short mean / PF | win5 | yrs5 | med r5 | p95 mfe5 | tail>50% |
|---|---|---|---|---|---|---|---|---|---|---|---|
| rng_atr 2-3x | 19,098 | -2 / 0.97 | +0 / 1.00 | -5 / 0.98 | -9 / 0.97 | -10 / 0.97 | 48% | 5/22 | -17 | +1730 | 0.5% |
| rng_atr 3-4x | 3,650 | -4 / 0.95 | +5 / 1.03 | +12 / 1.05 | +1 / 1.00 | +18 / 1.05 | 49% | 7/22 | -9 | +2022 | 0.9% |
| rng_atr 4-6x | 1,573 | -10 / 0.90 | +12 / 1.06 | +18 / 1.07 | -4 / 0.99 | +44 / 1.11 | 50% | 12/22 | +0 | +2321 | 1.5% |
| rng_atr 6-10x | 381 | -28 / 0.81 | +9 / 1.04 | +32 / 1.09 | +15 / 1.04 | +88 / 1.18 | 51% | 14/22 | +15 | +3328 | 2.7% |
| rng_atr 10x+ | 96 | -111 / 0.71 | +53 / 1.12 | +133 / 1.24 | +212 / 1.37 | +352 / 1.55 | 52% | 13/22 | +27 | +5957 | 6.6% |

### T2b — rng x rng_atr cross, r5 short mean / PF (n/yr)
| rng \ rng_atr | <3x | 3-6x | 6-10x | 10x+ |
|---|---|---|---|---|
| 10-15% | -11 / 0.98 (11,619) | +20 / 1.07 (1,318) | -43 / 0.79 (84) | +18 / 1.09 (6) |
| 15-25% | -20 / 0.97 (3,752) | +44 / 1.10 (907) | +52 / 1.20 (125) | -45 / 0.83 (17) |
| 25-40% | +95 / 1.09 (632) | -122 / 0.85 (305) | +93 / 1.21 (66) | +15 / 1.04 (17) |
| 40%+ | +312 / 1.24 (122) | -43 / 0.97 (147) | -42 / 0.97 (61) | +420 / 1.48 (46) |

### T3 — inside the big-range days (rng >= 15%): where did it close, and which way?
| cell | n/yr | r_open1: short mean / PF | r1: short mean / PF | r3: short mean / PF | r5: short mean / PF | r10: short mean / PF | win5 | yrs5 | med r5 | p95 mfe5 | tail>50% |
|---|---|---|---|---|---|---|---|---|---|---|---|
| clpos 0-0.2 (near the low) | 1,824 | -54 / 0.74 | -47 / 0.88 | -80 / 0.88 | -68 / 0.92 | +18 / 1.02 | 53% | 10/22 | +103 | +5450 | 6.0% |
| clpos 0.2-0.4 | 1,065 | -11 / 0.94 | +20 / 1.05 | +7 / 1.01 | -29 / 0.96 | +86 / 1.09 | 55% | 14/22 | +200 | +6055 | 6.9% |
| clpos 0.4-0.6 | 841 | -33 / 0.84 | +5 / 1.01 | +51 / 1.08 | +35 / 1.05 | +162 / 1.18 | 55% | 11/22 | +205 | +5999 | 7.0% |
| clpos 0.6-0.8 | 979 | -55 / 0.74 | -24 / 0.94 | -19 / 0.97 | +16 / 1.02 | +94 / 1.11 | 54% | 13/22 | +153 | +5432 | 6.0% |
| clpos 0.8-1 (near the high) | 1,490 | +11 / 1.07 | +50 / 1.16 | +79 / 1.16 | +105 / 1.17 | +72 / 1.09 | 56% | 18/22 | +199 | +4431 | 3.8% |
| close < open AND < prev (down day) | 2,934 | -24 / 0.87 | -16 / 0.96 | -45 / 0.93 | -88 / 0.89 | +3 / 1.00 | 52% | 12/22 | +84 | +5423 | 5.9% |
| close > open AND > prev (up day) | 2,605 | -38 / 0.81 | +6 / 1.02 | +30 / 1.05 | +96 / 1.14 | +115 / 1.14 | 57% | 17/22 | +231 | +5188 | 5.4% |
| S2 shape: low far below open, closed above open | 511 | -0 / 1.00 | +32 / 1.07 | +119 / 1.18 | +181 / 1.23 | +213 / 1.23 | 57% | 18/22 | +294 | +6339 | 7.8% |
| S3 shape: high far above open, closed below open | 562 | -39 / 0.84 | +29 / 1.06 | +46 / 1.06 | +99 / 1.11 | +293 / 1.29 | 58% | 12/22 | +343 | +7342 | 9.7% |
| closed above the prev close, below the open (gap-up faded) | 297 | +62 / 1.31 | +123 / 1.32 | +171 / 1.28 | +187 / 1.24 | +347 / 1.40 | 60% | 19/22 | +427 | +6243 | 7.3% |
| closed below the prev close, above the open (gap-down bought) | 332 | -71 / 0.69 | -52 / 0.87 | +14 / 1.02 | -3 / 1.00 | +123 / 1.15 | 54% | 16/22 | +148 | +5496 | 6.0% |

### T4 — context on big-range days (rng >= 15%): the runner, the gap, volume, price, liquidity
| cell | n/yr | r_open1: short mean / PF | r1: short mean / PF | r3: short mean / PF | r5: short mean / PF | r10: short mean / PF | win5 | yrs5 | med r5 | p95 mfe5 | tail>50% |
|---|---|---|---|---|---|---|---|---|---|---|---|
| chg20 <-30% | 1,374 | -59 / 0.76 | -107 / 0.79 | -222 / 0.74 | -365 / 0.67 | -267 / 0.76 | 47% | 5/22 | -169 | +6264 | 8.4% |
| chg20 -30..-10% | 1,238 | -17 / 0.89 | +10 / 1.03 | +11 / 1.02 | +33 / 1.06 | +61 / 1.09 | 52% | 14/22 | +71 | +3515 | 2.0% |
| chg20 -10..+10% | 1,018 | -12 / 0.91 | +35 / 1.14 | +29 / 1.07 | +83 / 1.16 | +90 / 1.13 | 56% | 13/22 | +150 | +3469 | 2.3% |
| chg20 +10..+50% | 1,216 | -20 / 0.87 | +14 / 1.04 | +27 / 1.05 | +67 / 1.11 | +81 / 1.10 | 56% | 16/22 | +195 | +4504 | 4.1% |
| chg20 +50..+150% | 916 | -30 / 0.87 | +25 / 1.06 | +80 / 1.12 | +150 / 1.19 | +174 / 1.16 | 60% | 16/22 | +452 | +6860 | 8.5% |
| chg20 +150%+ | 435 | -18 / 0.95 | +113 / 1.19 | +366 / 1.44 | +469 / 1.44 | +932 / 1.82 | 67% | 21/22 | +1039 | +10138 | 15.2% |
| gap <-10% | 432 | -84 / 0.71 | -52 / 0.89 | +63 / 1.10 | +96 / 1.12 | +194 / 1.22 | 57% | 16/22 | +278 | +6910 | 8.2% |
| gap -10..0% | 2,658 | -46 / 0.75 | -43 / 0.89 | -31 / 0.95 | -71 / 0.91 | +30 / 1.03 | 53% | 11/22 | +104 | +5241 | 5.5% |
| gap 0..+10% | 2,704 | -6 / 0.96 | +31 / 1.09 | -10 / 0.98 | +22 / 1.03 | +28 / 1.03 | 55% | 16/22 | +166 | +5080 | 5.2% |
| gap +10..+30% | 338 | -25 / 0.91 | +89 / 1.21 | +187 / 1.29 | +274 / 1.37 | +438 / 1.50 | 62% | 21/22 | +489 | +6815 | 8.4% |
| gap +30%+ | 65 | +160 / 1.41 | +201 / 1.31 | +367 / 1.45 | +642 / 1.74 | +1147 / 2.41 | 67% | 19/21 | +969 | +9056 | 11.6% |
| rvol <2 | 4,000 | -25 / 0.87 | -11 / 0.97 | -12 / 0.98 | -9 / 0.99 | +60 / 1.06 | 55% | 12/22 | +187 | +5584 | 6.3% |
| rvol 2-5 | 1,570 | -43 / 0.78 | -2 / 1.00 | -9 / 0.98 | -5 / 0.99 | +54 / 1.07 | 54% | 10/22 | +114 | +4807 | 4.6% |
| rvol 5-10 | 434 | -10 / 0.95 | +46 / 1.14 | +82 / 1.17 | +117 / 1.21 | +176 / 1.26 | 56% | 17/22 | +167 | +4681 | 4.5% |
| rvol 10-25 | 161 | -13 / 0.95 | +82 / 1.20 | +184 / 1.35 | +227 / 1.37 | +306 / 1.42 | 57% | 18/22 | +228 | +5223 | 5.5% |
| rvol 25+ | 32 | -43 / 0.90 | +58 / 1.09 | +38 / 1.05 | +79 / 1.09 | +313 / 1.34 | 56% | 12/22 | +319 | +8590 | 10.5% |
| prev close $2-5 | 1,562 | -48 / 0.80 | +5 / 1.01 | +3 / 1.00 | +36 / 1.04 | +98 / 1.09 | 58% | 11/22 | +385 | +7257 | 9.4% |
| prev close $5-10 | 1,417 | -25 / 0.87 | -7 / 0.98 | -14 / 0.98 | -11 / 0.99 | +57 / 1.06 | 55% | 14/22 | +214 | +5965 | 7.1% |
| prev close $10-25 | 1,825 | -23 / 0.87 | -6 / 0.98 | -2 / 1.00 | +9 / 1.01 | +98 / 1.12 | 53% | 15/22 | +96 | +4674 | 4.2% |
| prev close $25+ | 1,394 | -15 / 0.90 | +3 / 1.01 | +16 / 1.03 | -8 / 0.99 | +33 / 1.05 | 52% | 14/22 | +51 | +3671 | 2.3% |
| dv20 $5-20M | 3,103 | -20 / 0.88 | -1 / 1.00 | +5 / 1.01 | +13 / 1.02 | +76 / 1.09 | 54% | 15/22 | +158 | +5241 | 5.5% |
| dv20 $20-100M | 2,162 | -28 / 0.86 | +2 / 1.01 | +1 / 1.00 | -0 / 1.00 | +53 / 1.06 | 55% | 12/22 | +178 | +5500 | 6.0% |
| dv20 $100M-1B | 854 | -50 / 0.80 | -13 / 0.97 | -9 / 0.99 | +15 / 1.02 | +127 / 1.14 | 55% | 13/22 | +181 | +5471 | 6.0% |
| dv20 $1B+ | 78 | -78 / 0.74 | -42 / 0.90 | -90 / 0.88 | -70 / 0.92 | +13 / 1.01 | 51% | 7/20 | +67 | +5355 | 6.0% |

### T5 — year table, SHORT, rng >= 15%

| year | n | r_open1 | r1 | r3 | r5 | r10 | win5 | p95 mfe5 |
|---|---|---|---|---|---|---|---|---|
| 2005 | 739 | -28 / 0.78 | +87 / 1.39 | +106 / 1.33 | +100 / 1.25 | +93 / 1.16 | 56% | +2896 |
| 2006 | 654 | -21 / 0.81 | +25 / 1.11 | +3 / 1.01 | +35 / 1.09 | +11 / 1.02 | 51% | +2512 |
| 2007 | 2,259 | -103 / 0.55 | -50 / 0.85 | +22 / 1.05 | +11 / 1.02 | +61 / 1.11 | 54% | +3676 |
| 2008 | 20,479 | -55 / 0.74 | -75 / 0.82 | -100 / 0.85 | -38 / 0.95 | +166 / 1.20 | 53% | +4863 |
| 2009 | 6,097 | -65 / 0.65 | -50 / 0.86 | -120 / 0.79 | -131 / 0.82 | -406 / 0.64 | 50% | +4492 |
| 2010 | 1,254 | -49 / 0.59 | +119 / 1.78 | -136 / 0.64 | -214 / 0.57 | +361 / 2.09 | 36% | +2492 |
| 2011 | 1,779 | -33 / 0.79 | -23 / 0.92 | -53 / 0.88 | -154 / 0.75 | +36 / 1.06 | 47% | +3096 |
| 2012 | 924 | -63 / 0.61 | -10 / 0.96 | -11 / 0.97 | -2 / 1.00 | -57 / 0.92 | 53% | +3325 |
| 2013 | 1,133 | -43 / 0.69 | +3 / 1.01 | -12 / 0.97 | +27 / 1.06 | +5 / 1.01 | 52% | +3812 |
| 2014 | 2,172 | -21 / 0.86 | +29 / 1.10 | +17 / 1.04 | +108 / 1.21 | +268 / 1.49 | 57% | +3622 |
| 2015 | 3,214 | -34 / 0.77 | +32 / 1.13 | -37 / 0.92 | -31 / 0.94 | +123 / 1.19 | 51% | +3489 |
| 2016 | 3,349 | -29 / 0.81 | -15 / 0.95 | +16 / 1.04 | -26 / 0.96 | -100 / 0.88 | 53% | +3998 |
| 2017 | 2,157 | -37 / 0.79 | +50 / 1.17 | +75 / 1.15 | +80 / 1.13 | +216 / 1.33 | 58% | +4888 |
| 2018 | 3,365 | -28 / 0.81 | +43 / 1.16 | +109 / 1.26 | +144 / 1.28 | +237 / 1.37 | 55% | +4005 |
| 2019 | 2,823 | -14 / 0.90 | +10 / 1.04 | +20 / 1.04 | +75 / 1.14 | +213 / 1.33 | 56% | +3958 |
| 2020 | 20,353 | -42 / 0.83 | -32 / 0.93 | -68 / 0.91 | -244 / 0.75 | -410 / 0.63 | 49% | +6211 |
| 2021 | 14,752 | -45 / 0.79 | +12 / 1.03 | -2 / 1.00 | -31 / 0.96 | +56 / 1.06 | 56% | +6041 |
| 2022 | 9,973 | +36 / 1.26 | +32 / 1.09 | +60 / 1.10 | +126 / 1.19 | +295 / 1.39 | 56% | +4806 |
| 2023 | 5,944 | +7 / 1.04 | +17 / 1.04 | +91 / 1.16 | +223 / 1.35 | +430 / 1.56 | 57% | +5422 |
| 2024 | 8,891 | -33 / 0.84 | +21 / 1.05 | +52 / 1.08 | +103 / 1.13 | +232 / 1.22 | 58% | +7629 |
| 2025 | 14,496 | +7 / 1.04 | +61 / 1.15 | +125 / 1.21 | +200 / 1.27 | +182 / 1.17 | 60% | +5985 |
| 2026 | 9,542 | -6 / 0.97 | +12 / 1.03 | +50 / 1.07 | +120 / 1.15 | +357 / 1.38 | 60% | +6527 |

### T6 — EFFICIENCY RATIO on closes, 10-day, inside big-range days (rng >= 15%): |net| / path
| cell | n/yr | r_open1: short mean / PF | r1: short mean / PF | r3: short mean / PF | r5: short mean / PF | r10: short mean / PF | win5 | yrs5 | med r5 | p95 mfe5 | tail>50% |
|---|---|---|---|---|---|---|---|---|---|---|---|
| er10 <0.2 | 1,605 | -21 / 0.88 | +16 / 1.05 | +55 / 1.10 | +125 / 1.20 | +146 / 1.18 | 57% | 17/22 | +249 | +4749 | 4.5% |
| er10 0.2-0.4 | 1,637 | -17 / 0.90 | +17 / 1.05 | +48 / 1.09 | +92 / 1.14 | +141 / 1.17 | 56% | 17/22 | +220 | +4934 | 4.9% |
| er10 0.4-0.6 | 1,478 | -30 / 0.85 | +1 / 1.00 | -49 / 0.92 | -69 / 0.91 | +30 / 1.03 | 53% | 11/22 | +102 | +5395 | 5.8% |
| er10 0.6-0.8 | 1,030 | -36 / 0.83 | -42 / 0.91 | -95 / 0.87 | -189 / 0.79 | -83 / 0.92 | 51% | 11/22 | +48 | +6173 | 7.4% |
| er10 0.8+ | 448 | -69 / 0.76 | -53 / 0.90 | +16 / 1.02 | -18 / 0.98 | +78 / 1.07 | 53% | 15/22 | +127 | +7362 | 9.4% |

### T6b — er10 SIGNED (direction of the 10-day net move), rng >= 15%
| cell | n/yr | r_open1: short mean / PF | r1: short mean / PF | r3: short mean / PF | r5: short mean / PF | r10: short mean / PF | win5 | yrs5 | med r5 | p95 mfe5 | tail>50% |
|---|---|---|---|---|---|---|---|---|---|---|---|
| er10_signed <-0.6 (efficient DOWN) | 821 | -59 / 0.74 | -114 / 0.77 | -226 / 0.71 | -386 / 0.62 | -265 / 0.75 | 45% | 5/22 | -182 | +6085 | 7.4% |
| er10_signed -0.6..-0.3 | 1,198 | -30 / 0.83 | -10 / 0.97 | -96 / 0.85 | -156 / 0.80 | -92 / 0.89 | 49% | 8/22 | -29 | +4692 | 4.2% |
| er10_signed -0.3..0 | 1,177 | -21 / 0.88 | +9 / 1.03 | +37 / 1.07 | +89 / 1.14 | +67 / 1.08 | 56% | 15/22 | +189 | +4327 | 3.6% |
| er10_signed 0..0.3 | 1,255 | -22 / 0.89 | +22 / 1.06 | +74 / 1.14 | +152 / 1.24 | +221 / 1.26 | 58% | 16/22 | +295 | +5276 | 5.5% |
| er10_signed 0.3..0.6 | 1,091 | -16 / 0.91 | +28 / 1.07 | +67 / 1.11 | +131 / 1.18 | +244 / 1.27 | 59% | 17/22 | +322 | +6011 | 7.0% |
| er10_signed 0.6+ (efficient UP) | 656 | -30 / 0.88 | +40 / 1.09 | +144 / 1.22 | +173 / 1.21 | +254 / 1.26 | 60% | 21/22 | +385 | +7318 | 8.8% |

### T6c — er20 SIGNED, rng >= 15%
| cell | n/yr | r_open1: short mean / PF | r1: short mean / PF | r3: short mean / PF | r5: short mean / PF | r10: short mean / PF | win5 | yrs5 | med r5 | p95 mfe5 | tail>50% |
|---|---|---|---|---|---|---|---|---|---|---|---|
| er20_signed <-0.6 | 307 | -78 / 0.72 | -150 / 0.75 | -311 / 0.68 | -656 / 0.53 | -520 / 0.59 | 44% | 2/22 | -298 | +7941 | 12.5% |
| er20_signed -0.6..-0.3 | 1,265 | -34 / 0.82 | -65 / 0.84 | -131 / 0.80 | -224 / 0.73 | -159 / 0.82 | 47% | 6/22 | -111 | +4871 | 4.6% |
| er20_signed -0.3..0 | 1,596 | -25 / 0.85 | +10 / 1.03 | -7 / 0.99 | +48 / 1.08 | +77 / 1.10 | 54% | 16/22 | +134 | +4221 | 3.4% |
| er20_signed 0..0.3 | 1,681 | -22 / 0.88 | +31 / 1.09 | +69 / 1.13 | +129 / 1.20 | +180 / 1.21 | 58% | 17/22 | +297 | +5431 | 5.8% |
| er20_signed 0.3..0.6 | 1,074 | -18 / 0.91 | +29 / 1.07 | +84 / 1.14 | +154 / 1.21 | +275 / 1.30 | 60% | 18/22 | +350 | +6332 | 7.4% |
| er20_signed 0.6+ | 275 | -40 / 0.87 | +68 / 1.13 | +252 / 1.36 | +266 / 1.29 | +369 / 1.32 | 61% | 20/22 | +499 | +8016 | 10.7% |

### T6d — er10 x the day's direction (rng >= 15%)
| cell | n/yr | r_open1: short mean / PF | r1: short mean / PF | r3: short mean / PF | r5: short mean / PF | r10: short mean / PF | win5 | yrs5 | med r5 | p95 mfe5 | tail>50% |
|---|---|---|---|---|---|---|---|---|---|---|---|
| efficient UP (er10_s >= 0.5) AND closed DOWN on the day | 231 | +69 / 1.35 | +122 / 1.29 | +189 / 1.28 | +190 / 1.21 | +414 / 1.42 | 62% | 19/22 | +539 | +7258 | 8.9% |
| efficient UP (er10_s >= 0.5) AND closed UP on the day | 733 | -52 / 0.78 | +15 / 1.04 | +107 / 1.17 | +154 / 1.21 | +200 / 1.21 | 59% | 19/22 | +326 | +6821 | 8.2% |
| efficient DOWN (er10_s <= -0.5) AND closed UP on the day | 302 | -140 / 0.49 | -141 / 0.71 | -222 / 0.69 | -335 / 0.64 | -104 / 0.89 | 46% | 8/22 | -138 | +5532 | 6.5% |
| efficient DOWN (er10_s <= -0.5) AND closed DOWN on the day | 900 | -24 / 0.88 | -72 / 0.84 | -210 / 0.72 | -353 / 0.64 | -289 / 0.72 | 46% | 4/22 | -155 | +5752 | 6.7% |
| choppy (|er10_s| < 0.3) | 2,431 | -21 / 0.88 | +16 / 1.05 | +56 / 1.11 | +121 / 1.19 | +146 / 1.18 | 57% | 16/22 | +244 | +4787 | 4.6% |

### Verdict — the range alone is NOTHING; range × the PRIOR TREND is everything. The short is the RUNNER's big day; the crushed name's big day is the bounce.

- **T1**: the raw range by itself is a losing short below 25% (r5 −19..−6 bp, 7-13/22 years) and
  only turns at 25-40% (+28 / 1.03, r10 +221 / 1.22) and 40%+ (r5 +128 / 1.10, r10 +558 / 1.43,
  15/22) — where **one trade in five sees a 5-day high 50%+ above the entry** (tail>50% 19%).
  T2: relative range (`rng_atr` 10×+) is the better single dial, r5 +212 / 1.37, r10 +352 / 1.55,
  but 96/yr.
- **T3**: closing near the HIGH is the best position (+105 / 1.17, 18/22) and near the low the worst
  at 1-5 days (the bounce); the best SHAPE is the gap-up that faded but held above yesterday
  (`close > prev ∧ close < open`): r5 +187 / 1.24, r10 +347 / 1.40, 60% win, **19/22**, 297/yr.
- **T4 — the runner**: `chg20 +150%+`: r5 +469 / 1.44, **r10 +932 / 1.82, 67% win, 21/22**, 435/yr
  (tail 15%); `gap +10..30%`: +274 / 1.37, 21/22; `gap +30%+`: +642 / 1.74; `rvol 10-25`: +227 /
  1.37, 18/22. **The crushed name (`chg20 < −30%`) is a LOSING short: −365 / 0.67, 5/22** — its
  big-range day is the capitulation bounce, the MR cell. Price: $2-5 carries it, $25+ has none;
  $1B+/day liquidity has none.
- **T5 — era**: 2005-2016 mixed to negative (2008-2011 all negative at r5 — crash bounces);
  **2017-2026 positive at r5 in 9 of 10 years** (2020 the exception, −244), 2022-2023 at PF 1.19-1.35
  ungated.
- **T6 — the efficiency ratio (user)**: unsigned, LOW efficiency (<0.4, choppy) is the better short
  (+92..+125 / 1.14-1.20, 17/22) and 0.6-0.8 is negative (−189 / 0.79) — but the unsigned ratio
  hides the real variable. **Signed, it is monotone**: efficient DOWN (`er10_s < −0.6`) is the
  worst cell on the page (r5 −386 / 0.62, 5/22; `er20_s < −0.6`: −656 / 0.53, **2/22**) and
  efficient UP (`er10_s ≥ 0.6`) is +173 / 1.21, **21/22**. T6d: **efficient UP × the day closed
  DOWN** = r5 +190 / 1.21, r10 +414 / 1.42, 62%, 19/22 at 231/yr; efficient DOWN × closed UP =
  −335 / 0.64 (the bounce again).

**What this says, and how it sits with HighFlyer/MaxFlyer**: a big-range day is a SELL only when it
prints on a name that had been going UP efficiently (the runner) — the MaxFlyerV2 pop-fade and the
HighFlyer "breakouts fail on high volatility / high tightness score" ruling, now on daily bars with
a close-of-day entry. On a name that had been going DOWN efficiently the same day is the flush and
the long. The range measures the energy; the prior trend's SIGN decides who is trapped. The tail is
the runner's tail (15% of `chg20 +150%+` trades see +50% against them within 5 days). ⏭ The
HighFlyer daily tightness score (>7.5 = breakouts fail) is the obvious next feature to port here;
the ShortSnoozer gap × volatility disqualifier is the tail tool.

### S4 addendum — clustering: the efficient-DOWN bounce is a PANIC artifact (TideFlyer's disease); the runner short is NOT

> USER: *"Is there a large amount of clustering in this bucket as before? We failed at creating the
> TideFlyer system because all the good trades were during market panics or the 2008 crash."*
> Log `data/springflyer_effdown.log`.

**The efficient-down cell (`rng ≥ 15% ∧ er10_s < −0.6`, read as the LONG it is)**: 18,064 trips on
3,126 days, but **62% of them on days with ≥ 10 trips**, the top ten days all March 2020 / October
2008; 2008 + 2020 = 63% of Σr5. Crash months r5 +649 / 1.83 vs other days +242 / 1.44 — and on the
QUIET days (< 5 trips, ex-crash) the long is **−40 / 0.94, 44% win; 2016+ quiet: −84 / 0.89,
r10 −170 / 0.83**. Not outlier-driven (drop the top 100 of 18k: PF 1.59 → 1.45) — it is
regime-driven: the bounce exists when EVERYTHING is bouncing. The "do not short efficient-down"
ruling is therefore a crash-day ruling; on an ordinary day the efficient-down big-range name is a
weak but positive short.

**The short's own cells, same test** (short returns):

| cell | slice | n | r5 / PF | r10 / PF | win5 | trips on ≥10-trip days |
|---|---|---|---|---|---|---|
| runner `chg20 ≥ 150%` × rng ≥ 15% | all | 9,589 | +471 / 1.45 | +935 / 1.82 | 67% | 26% |
| | crash months | 195 | +763 / 2.15 | +837 / 2.14 | 66% | |
| | other, days with < 5 trips | 3,818 | **+500 / 1.53** | **+1,004 / 2.07** | 68% | 0% |
| | other, 2016+, days < 5 trips | 3,145 | +524 / 1.53 | +1,088 / 2.14 | 68% | 0% |
| efficient UP `er10_s ≥ 0.6` × rng ≥ 15% | all | 14,428 | +173 / 1.21 | +254 / 1.26 | 60% | 39% |
| | other, days with < 5 trips | 4,707 | **+247 / 1.38** | **+398 / 1.51** | 60% | 0% |
| eff UP × closed DOWN on the day | all | 5,092 | +190 / 1.21 | +414 / 1.42 | 62% | 16% |
| | other, 2016+, days < 5 trips | 2,480 | +162 / 1.16 | +458 / 1.43 | 61% | 0% |

⭐ **The short's edge is the OPPOSITE of TideFlyer's**: it is idiosyncratic — one name at a time —
and it is BETTER on the quiet days than in the panics (runner: quiet-day r10 PF 2.07 vs 1.82 all-in;
efficient-up: 1.51 vs 1.26). Crash months only add ~2% of the trips. That is what a name-specific
mechanism (the trapped-runner unwind) looks like, versus a market-beta mechanism (the panic bounce).

## S5 — the efficiency ratio as a TIMING mechanism (user, 2026-09-07): `er < −0.9`, and the same-day COUNT

> USER: *"It very much sounds like we should be buying when there are a lot of these types of
> patterns on the same day. We did a lot of testing about that in the TideFlyer system. The stocks
> which had a clean selloff worked much better than those with poor ones. I wonder if eff < −0.9
> makes for even better longs?"* Log `data/springflyer_er_timing.log`. LONG returns throughout.

### T1 — LONG, fine bands of er10_signed at the efficient-DOWN end, rng >= 15%
| cell | n/yr | days | yrs w/ trips | r_open1 | r1 | r3 | r5 | r10 | win5 | yrs5 | med r5 | med mae5 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| er10_s -1..-0.9 | 86 | 810 | 22 | +168 / 2.10 | +270 / 1.71 | +309 / 1.49 | +397 / 1.52 | +185 / 1.19 | 53% | 17/22 | +125 | -1100 |
| er10_s -0.9..-0.8 | 154 | 1,236 | 22 | +70 / 1.38 | +154 / 1.40 | +215 / 1.39 | +374 / 1.58 | +300 / 1.38 | 55% | 15/22 | +221 | -987 |
| er10_s -0.8..-0.7 | 257 | 1,719 | 22 | +40 / 1.23 | +102 / 1.27 | +242 / 1.45 | +449 / 1.74 | +287 / 1.38 | 56% | 19/22 | +222 | -1022 |
| er10_s -0.7..-0.6 | 325 | 2,028 | 22 | +40 / 1.25 | +63 / 1.17 | +196 / 1.36 | +338 / 1.55 | +251 / 1.33 | 53% | 14/22 | +136 | -1014 |
| er10_s -0.6..-0.4 | 793 | 3,203 | 22 | +37 / 1.25 | +23 / 1.07 | +142 / 1.27 | +222 / 1.37 | +135 / 1.18 | 52% | 14/22 | +78 | -988 |

### T1 — LONG, fine bands of er10_signed at the efficient-DOWN end, rng >= 7%
| cell | n/yr | days | yrs w/ trips | r_open1 | r1 | r3 | r5 | r10 | win5 | yrs5 | med r5 | med mae5 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| er10_s -1..-0.9 | 474 | 2,410 | 22 | +41 / 1.39 | +72 / 1.29 | +103 / 1.26 | +120 / 1.24 | -5 / 0.99 | 53% | 17/22 | +74 | -741 |
| er10_s -0.9..-0.8 | 876 | 3,371 | 22 | +25 / 1.24 | +46 / 1.19 | +66 / 1.17 | +94 / 1.20 | +59 / 1.09 | 53% | 16/22 | +78 | -701 |
| er10_s -0.8..-0.7 | 1,456 | 4,096 | 22 | +15 / 1.14 | +36 / 1.15 | +75 / 1.20 | +106 / 1.23 | +67 / 1.11 | 53% | 17/22 | +68 | -697 |
| er10_s -0.7..-0.6 | 2,044 | 4,560 | 22 | +11 / 1.11 | +20 / 1.09 | +57 / 1.15 | +71 / 1.15 | +68 / 1.11 | 52% | 18/22 | +43 | -696 |
| er10_s -0.6..-0.4 | 5,740 | 5,251 | 22 | +9 / 1.09 | +3 / 1.01 | +44 / 1.12 | +52 / 1.11 | +61 / 1.11 | 51% | 19/22 | +25 | -679 |

### T1 — LONG, fine bands of er10_signed at the efficient-DOWN end, any range
| cell | n/yr | days | yrs w/ trips | r_open1 | r1 | r3 | r5 | r10 | win5 | yrs5 | med r5 | med mae5 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| er10_s -1..-0.9 | 2,575 | 4,750 | 22 | +15 / 1.31 | +20 / 1.16 | +28 / 1.13 | +36 / 1.14 | +6 / 1.01 | 53% | 15/22 | +36 | -339 |
| er10_s -0.9..-0.8 | 5,307 | 5,231 | 22 | +11 / 1.22 | +13 / 1.11 | +22 / 1.11 | +31 / 1.12 | +28 / 1.08 | 53% | 15/22 | +33 | -325 |
| er10_s -0.8..-0.7 | 9,629 | 5,380 | 22 | +8 / 1.17 | +10 / 1.09 | +23 / 1.12 | +34 / 1.14 | +38 / 1.11 | 53% | 19/22 | +33 | -316 |
| er10_s -0.7..-0.6 | 15,121 | 5,437 | 22 | +7 / 1.15 | +9 / 1.08 | +20 / 1.11 | +30 / 1.13 | +47 / 1.14 | 53% | 19/22 | +32 | -306 |
| er10_s -0.6..-0.4 | 49,946 | 5,451 | 22 | +5 / 1.12 | +5 / 1.05 | +18 / 1.10 | +27 / 1.12 | +47 / 1.15 | 53% | 18/22 | +29 | -294 |

### T1b — LONG, er20_signed fine bands, rng >= 7%
| cell | n/yr | days | yrs w/ trips | r_open1 | r1 | r3 | r5 | r10 | win5 | yrs5 | med r5 | med mae5 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| er20_s -1..-0.9 | 17 | 293 | 22 | +7 / 1.05 | -3 / 0.99 | -146 / 0.77 | -110 / 0.86 | -8 / 0.99 | 46% | 14/22 | -181 | -1092 |
| er20_s -0.9..-0.8 | 104 | 1,083 | 22 | +57 / 1.42 | +92 / 1.28 | +120 / 1.22 | +297 / 1.47 | +341 / 1.52 | 53% | 18/22 | +103 | -802 |
| er20_s -0.8..-0.7 | 360 | 2,221 | 22 | +43 / 1.34 | +80 / 1.28 | +151 / 1.32 | +241 / 1.42 | +212 / 1.32 | 55% | 20/22 | +141 | -756 |
| er20_s -0.7..-0.6 | 889 | 3,388 | 22 | +17 / 1.14 | +43 / 1.16 | +84 / 1.19 | +145 / 1.27 | +149 / 1.23 | 53% | 20/22 | +83 | -738 |

### T2 — TIMING: how many of these patterns printed the SAME DAY? cell = rng >= 7% AND er10_s < -0.8 (LONG), banded by the day's count
| cell | n/yr | days | yrs w/ trips | r_open1 | r1 | r3 | r5 | r10 | win5 | yrs5 | med r5 | med mae5 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| count 1 (alone) | 51 | 1,123 | 22 | +7 / 1.09 | -8 / 0.97 | +46 / 1.14 | +97 / 1.23 | +78 / 1.13 | 52% | 13/22 | +43 | -583 |
| count 2-4 | 185 | 1,504 | 22 | +29 / 1.38 | +14 / 1.06 | +5 / 1.01 | -13 / 0.97 | -49 / 0.93 | 48% | 11/22 | -46 | -640 |
| count 5-9 | 177 | 599 | 22 | +16 / 1.18 | -5 / 0.98 | -17 / 0.96 | -39 / 0.92 | -12 / 0.98 | 44% | 11/22 | -120 | -680 |
| count 10-29 | 296 | 412 | 22 | +37 / 1.40 | +32 / 1.14 | +61 / 1.16 | +95 / 1.21 | +128 / 1.22 | 52% | 12/22 | +43 | -693 |
| count 30-99 | 341 | 145 | 19 | +10 / 1.09 | +11 / 1.04 | +152 / 1.41 | +172 / 1.37 | +277 / 1.52 | 55% | 14/19 | +136 | -732 |
| count 100+ | 298 | 30 | 10 | +61 / 1.43 | +202 / 1.74 | +123 / 1.29 | +189 / 1.35 | -254 / 0.72 | 60% | 6/10 | +302 | -839 |

### T2b — the same by count band, with 2008 and 2020 EXCLUDED
| cell | n/yr | days | yrs w/ trips | r_open1 | r1 | r3 | r5 | r10 | win5 | yrs5 | med r5 | med mae5 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| count 1 (alone) | 46 | 1,021 | 20 | +7 / 1.09 | -2 / 0.99 | +42 / 1.12 | +91 / 1.22 | +29 / 1.05 | 52% | 12/20 | +54 | -569 |
| count 2-4 | 168 | 1,369 | 20 | +28 / 1.36 | +8 / 1.04 | -11 / 0.97 | -38 / 0.93 | -86 / 0.88 | 47% | 9/20 | -55 | -641 |
| count 5-9 | 158 | 536 | 20 | +18 / 1.20 | -11 / 0.95 | +6 / 1.02 | -20 / 0.96 | -5 / 0.99 | 45% | 10/20 | -113 | -661 |
| count 10-29 | 253 | 355 | 20 | +42 / 1.49 | +36 / 1.16 | +51 / 1.14 | +94 / 1.22 | +143 / 1.26 | 52% | 11/20 | +44 | -652 |
| count 30-99 | 273 | 119 | 17 | +17 / 1.18 | +40 / 1.19 | +153 / 1.51 | +196 / 1.54 | +301 / 1.71 | 56% | 13/17 | +153 | -680 |
| count 100+ | 172 | 15 | 8 | +90 / 2.16 | +221 / 2.16 | +401 / 3.17 | +538 / 4.26 | +323 / 1.89 | 74% | 6/8 | +527 | -474 |

### T2c — the days with count >= 30: which are they? (date, count, mean r5 of the day's names)

### T3 — TIMING inside a burst: days with count >= 30, by position in the burst (0 = the first such day, runs joined when <= 5 sessions apart), LONG
| position | n trips | days | r1 | r3 | r5 | r10 | win5 | share of days with mean r5 > 0 |
|---|---|---|---|---|---|---|---|---|
| first day (0) | 2926 | 51 | +49 / 1.23 | +86 / 1.24 | +154 / 1.37 | +60 / 1.10 | 54% | 47% |
| 2nd (1) | 2378 | 34 | -145 / 0.59 | +69 / 1.19 | +152 / 1.39 | -218 / 0.70 | 53% | 53% |
| 3rd-4th (2-3) | 4073 | 43 | +258 / 2.52 | +293 / 2.13 | +355 / 2.06 | +351 / 1.78 | 63% | 49% |
| 5th+ (4+) | 4684 | 47 | +118 / 1.36 | +72 / 1.13 | +58 / 1.08 | -146 / 0.85 | 57% | 60% |

### T3b — today's count vs the trailing 5-day sum of counts (was the selloff already running?), count >= 30 today, LONG
| trailing-5d count sum | n trips | days | r1 | r5 | r10 | win5 | days w/ mean r5 > 0 |
|---|---|---|---|---|---|---|---|
| < 30 (fresh) | 1098 | 16 | +28 / 1.15 | +24 / 1.06 | +107 / 1.21 | 57% | 44% |
| 30-99 | 3048 | 56 | +112 / 1.57 | +191 / 1.48 | -138 / 0.81 | 52% | 50% |
| 100-299 | 5149 | 69 | -72 / 0.75 | +250 / 1.71 | +391 / 1.85 | 58% | 57% |
| 300+ (deep in it) | 4766 | 34 | +294 / 1.99 | +134 / 1.18 | -273 / 0.73 | 60% | 50% |

### Verdict

- **`er < −0.9` is a better ONE-DAY bounce, not a better 5-10-day long.** At rng ≥ 15% the
  −1..−0.9 band front-loads: next open +168 / 2.10, r1 +270 / 1.71 (the best r1 on the page), but
  r5 +397 / 1.52 sits below the −0.8..−0.7 band (+449 / 1.74, 19/22) and r10 fades to +185 / 1.19,
  at 86/yr. On any range the extreme band is +36 / 1.14 at r5 and +6 at r10. The 20-day ratio
  below −0.9 is NEGATIVE (−110 / 0.86): the 20-day capitulation keeps falling. The cleanest
  selloff bounces hardest on day one and gives it back.
- **The same-day count is a REGIME signal with a U, and it works ex-crash**: count 1 (alone)
  r5 +97 / 1.23; 2-9 NEGATIVE (−13..−39); 10-29 +95 / 1.21; **30-99 +172 / 1.37, r10 +277 / 1.52,
  14/19 years**; 100+ r5 +189 but r10 −254 (March 2020). With 2008 and 2020 removed: **30-99:
  r5 +196 / 1.54, r10 +301 / 1.71, 13/17 years, 119 days**; 100+: +538 / 4.26 on 15 days. The
  breadth days are the ordinary corrections (Aug 2011, Aug 2015, Jan 2016, Dec 2018, the 2021
  rotations, Jan 2022, Apr 2025) — the TideFlyer sample, but with a positive quiet-day baseline
  under it (count 1: 13/22) that TideFlyer never had.
- ⚠ **Timing INSIDE a burst is unresolved — TideFlyer's actual problem, restated.** Of the 175
  days with ≥ 30 patterns, the share whose names' mean r5 is positive is 47-60% in EVERY position
  band (first day 47%, 3rd-4th 49%, 5th+ 60%) and in every trailing-count band (44-57%). The
  trip-weighted means (3rd-4th day +355 / 2.06) are carried by a few big days; the day-level sign
  is a coin flip. The first days of a cascade lose (2020-02-26 → 03-13: every day −500 to −3,600;
  2016-01-08 → 01-15; 2022-01-13 → 01-21) and the count cannot tell the third day of a cascade
  from the last. Whatever resolves it is not in this feature set (it needs the market's own
  state — index drawdown / VIX / the count's DERIVATIVE — and the TideFlyer file already says the
  same).

**Where this leaves the long**: the clean-selloff bounce is real as a regime cell (30-99 patterns
in a day, ex-crash r5 PF 1.54, 13/17 years) and as a one-day bounce (`er < −0.9`, r1 PF 1.7 at
rng ≥ 15%), and it is not a single-name system: it is a market-timing system wearing single-name
clothes. It belongs with TideFlyer, not with SpringFlyer's short.

## S6 — the FIRST RED DAY after a CLIMAX (user, 2026-09-07; the Tim Sykes short)

> USER: *"there is large move in the past few weeks and the price goes up sharply over a span of few
> days each closing up on big green candles. Then the momentum stalls and there is a red candle. How
> well would shorting on the first red candle work assuming that the first down day isn't bad enough
> to the point of retracing most of the upmove."*

Script `scripts/equity/springflyer_climax.py`, log `data/springflyer_climax.log`. New features:
`prev_streak` (consecutive up closes ending D−1), `prev_run_gain` (D−1's close over the run's base
close, the last non-up close), `retrace` = (close(D−1) − close(D)) / (close(D−1) − base), 1 = gave
the whole run back; `prev_run_maxday`; `up_streak_d`. SHORT at D's close, AND at D+1's open
(`@next open` columns). Short returns; `trips on 10+ days` = the clustering check.

### T1 — the first RED day: streak x run gain, retrace < 50% (short mean bp / PF; entries at D's close, and at D+1's open)
| cell | n/yr | r_open1 | r1 | r3 | r5 | r10 | r5 @next open | r10 @next open | win5 | yrs5 | p95 mfe5 | trips on 10+ days |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| streak >= 2, run 20-50%, red, retrace < 0.5 | 2,077 | -5 / 0.94 | +4 / 1.02 | +30 / 1.10 | +25 / 1.06 | -19 / 0.97 | +30 / 1.08 | -14 / 0.98 | 53% | 12/22 | +2650 | 71% |
| streak >= 2, run 50-100%, red, retrace < 0.5 | 235 | +18 / 1.12 | +36 / 1.11 | +67 / 1.12 | +151 / 1.24 | +158 / 1.19 | +137 / 1.22 | +142 / 1.17 | 58% | 13/22 | +5106 | 21% |
| streak >= 2, run 100%+, red, retrace < 0.5 | 101 | +50 / 1.18 | +45 / 1.07 | +184 / 1.22 | +366 / 1.39 | +641 / 1.60 | +285 / 1.30 | +572 / 1.53 | 65% | 17/22 | +9898 | 4% |
| streak >= 3, run 20-50%, red, retrace < 0.5 | 1,744 | -3 / 0.97 | +7 / 1.04 | +38 / 1.13 | +26 / 1.07 | -27 / 0.95 | +29 / 1.08 | -24 / 0.96 | 53% | 11/22 | +2460 | 68% |
| streak >= 3, run 50-100%, red, retrace < 0.5 | 195 | +25 / 1.19 | +52 / 1.18 | +106 / 1.22 | +175 / 1.30 | +168 / 1.21 | +153 / 1.26 | +146 / 1.18 | 58% | 15/22 | +4671 | 22% |
| streak >= 3, run 100%+, red, retrace < 0.5 | 77 | +38 / 1.14 | +73 / 1.13 | +223 / 1.28 | +353 / 1.38 | +570 / 1.51 | +284 / 1.31 | +512 / 1.46 | 64% | 14/22 | +9476 | 4% |
| streak >= 4, run 20-50%, red, retrace < 0.5 | 1,329 | -4 / 0.95 | +9 / 1.06 | +38 / 1.14 | +17 / 1.05 | -15 / 0.97 | +20 / 1.06 | -12 / 0.98 | 52% | 13/22 | +2293 | 62% |
| streak >= 4, run 50-100%, red, retrace < 0.5 | 151 | +12 / 1.10 | +36 / 1.13 | +74 / 1.16 | +140 / 1.25 | +146 / 1.19 | +131 / 1.23 | +137 / 1.18 | 57% | 13/22 | +4363 | 21% |
| streak >= 4, run 100%+, red, retrace < 0.5 | 55 | +33 / 1.13 | +104 / 1.21 | +206 / 1.27 | +342 / 1.39 | +501 / 1.46 | +287 / 1.33 | +452 / 1.41 | 64% | 14/22 | +9153 | 4% |

### T2 — how much of the run did the red day give back? (streak >= 3, run >= 50%)
| cell | n/yr | r_open1 | r1 | r3 | r5 | r10 | r5 @next open | r10 @next open | win5 | yrs5 | p95 mfe5 | trips on 10+ days |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| retrace 0-0.15 (barely red) | 140 | +64 / 1.58 | +106 / 1.38 | +168 / 1.35 | +255 / 1.47 | +217 / 1.28 | +200 / 1.36 | +162 / 1.20 | 58% | 15/22 | +4761 | 10% |
| retrace 0.15-0.3 | 85 | +41 / 1.22 | +88 / 1.23 | +210 / 1.36 | +267 / 1.37 | +366 / 1.40 | +216 / 1.30 | +317 / 1.35 | 62% | 17/22 | +6578 | 12% |
| retrace 0.3-0.5 | 47 | -98 / 0.72 | -134 / 0.78 | -71 / 0.92 | +59 / 1.06 | +317 / 1.27 | +112 / 1.12 | +380 / 1.34 | 62% | 9/22 | +9289 | 13% |
| retrace 0.5-0.75 | 16 | -238 / 0.55 | -228 / 0.70 | -75 / 0.93 | -102 / 0.92 | +314 / 1.27 | +110 / 1.11 | +460 / 1.43 | 59% | 11/18 | +10136 | 8% |
| retrace 0.75-1 | 2 | +83 / 1.33 | +110 / 1.16 | +705 / 2.59 | +181 / 1.12 | -102 / 0.94 | +18 / 1.01 | -275 / 0.86 | 70% | 9/15 | +14519 | 0% |
| retrace >1 (closed below the base) | 1 | +577 / 2.07 | +1541 / 6.30 | +1773 / 3.39 | +3144 / 8.84 | +3911 / 21.17 | +2722 / 15.39 | +3507 / 71.49 | 88% | 8/9 | +6303 | 0% |

### T3 — CONTROLS: short the GREEN climax day itself (no red yet), and the 2nd red day
| cell | n/yr | r_open1 | r1 | r3 | r5 | r10 | r5 @next open | r10 @next open | win5 | yrs5 | p95 mfe5 | trips on 10+ days |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| green day, streak_d >= 3, run gain (incl. today) >= 50% | 425 | -7 / 0.97 | +67 / 1.15 | +216 / 1.35 | +313 / 1.44 | +339 / 1.36 | +307 / 1.44 | +334 / 1.37 | 61% | 19/22 | +6727 | 32% |
| green day, streak_d >= 4, run gain >= 100% | 77 | +25 / 1.07 | +149 / 1.22 | +574 / 1.77 | +691 / 1.76 | +884 / 1.81 | +660 / 1.76 | +867 / 1.83 | 67% | 21/22 | +9997 | 5% |
| the FIRST red (streak>=3, run>=50%, retrace<0.5) | 272 | +29 / 1.16 | +59 / 1.16 | +140 / 1.24 | +225 / 1.33 | +280 / 1.32 | +190 / 1.28 | +248 / 1.28 | 60% | 18/22 | +6046 | 22% |
| a red day with NO climax (streak <= 1, |chg20| < 10%) | 135,958 | -4 / 0.89 | -5 / 0.94 | -15 / 0.90 | -25 / 0.88 | -40 / 0.86 | -21 / 0.89 | -36 / 0.87 | 46% | 4/22 | +1112 | 100% |

### T4 — inside the first-red cell (streak >= 3, run >= 50%, retrace < 0.5): D's own shape and context
| cell | n/yr | r_open1 | r1 | r3 | r5 | r10 | r5 @next open | r10 @next open | win5 | yrs5 | p95 mfe5 | trips on 10+ days |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| D's range <7% | 41 | +28 / 1.47 | +66 / 1.43 | +80 / 1.33 | +91 / 1.30 | +84 / 1.19 | +71 / 1.23 | +62 / 1.14 | 50% | 10/22 | +2441 | 6% |
| D's range 7-15% | 105 | +37 / 1.32 | +47 / 1.16 | +105 / 1.22 | +141 / 1.24 | +72 / 1.09 | +110 / 1.19 | +40 / 1.05 | 58% | 12/22 | +4519 | 17% |
| D's range 15-30% | 93 | +34 / 1.17 | +66 / 1.15 | +167 / 1.24 | +298 / 1.39 | +431 / 1.44 | +256 / 1.33 | +394 / 1.40 | 63% | 17/22 | +7220 | 10% |
| D's range 30%+ | 34 | -13 / 0.97 | +66 / 1.09 | +245 / 1.26 | +449 / 1.39 | +752 / 1.55 | +400 / 1.35 | +718 / 1.54 | 68% | 13/21 | +12460 | 0% |
| D closed below the open (red body) | 228 | +24 / 1.13 | +43 / 1.11 | +109 / 1.19 | +191 / 1.27 | +254 / 1.28 | +160 / 1.23 | +227 / 1.25 | 59% | 18/22 | +6021 | 22% |
| D closed above the open (gap-down, bought) | 42 | +55 / 1.33 | +151 / 1.45 | +316 / 1.58 | +397 / 1.64 | +408 / 1.46 | +337 / 1.53 | +344 / 1.38 | 64% | 13/22 | +6577 | 7% |
| clpos 0-0.25 | 123 | -8 / 0.96 | +4 / 1.01 | +59 / 1.10 | +170 / 1.24 | +220 / 1.24 | +174 / 1.26 | +232 / 1.26 | 58% | 16/22 | +6104 | 15% |
| clpos 0.25-0.5 | 86 | +35 / 1.21 | +74 / 1.20 | +175 / 1.30 | +247 / 1.36 | +365 / 1.44 | +196 / 1.28 | +307 / 1.36 | 61% | 16/22 | +6008 | 8% |
| clpos 0.5-0.75 | 47 | +92 / 1.69 | +167 / 1.57 | +261 / 1.52 | +330 / 1.55 | +284 / 1.31 | +246 / 1.40 | +208 / 1.22 | 61% | 15/22 | +5624 | 7% |
| clpos 0.75-1 | 16 | +95 / 1.70 | +78 / 1.22 | +218 / 1.41 | +226 / 1.32 | +277 / 1.33 | +117 / 1.16 | +168 / 1.19 | 63% | 12/21 | +6385 | 3% |
| rvol on D <1 | 117 | +53 / 1.36 | +64 / 1.17 | +207 / 1.38 | +294 / 1.44 | +348 / 1.39 | +241 / 1.36 | +297 / 1.33 | 61% | 13/22 | +6388 | 16% |
| rvol on D 1-2 | 101 | +11 / 1.07 | +35 / 1.10 | +96 / 1.17 | +185 / 1.29 | +242 / 1.30 | +169 / 1.27 | +224 / 1.28 | 59% | 17/22 | +5379 | 13% |
| rvol on D 2-5 | 46 | -4 / 0.98 | +65 / 1.17 | +47 / 1.07 | +94 / 1.12 | +267 / 1.29 | +74 / 1.09 | +258 / 1.28 | 58% | 13/22 | +6180 | 1% |
| rvol on D 5+ | 8 | +78 / 1.29 | +247 / 1.61 | +254 / 1.33 | +506 / 1.67 | -158 / 0.91 | +403 / 1.52 | -235 / 0.86 | 62% | 12/22 | +9233 | 0% |
| biggest day in the run <15% | 27 | +38 / 1.40 | +82 / 1.39 | +146 / 1.42 | +193 / 1.44 | +210 / 1.33 | +172 / 1.40 | +189 / 1.29 | 55% | 19/22 | +2998 | 9% |
| biggest day in the run 15-30% | 110 | +8 / 1.06 | +28 / 1.09 | +77 / 1.14 | +174 / 1.28 | +59 / 1.06 | +168 / 1.27 | +57 / 1.06 | 59% | 10/22 | +4881 | 21% |
| biggest day in the run 30-60% | 84 | +37 / 1.23 | +121 / 1.38 | +171 / 1.30 | +205 / 1.29 | +252 / 1.28 | +165 / 1.24 | +209 / 1.23 | 60% | 13/22 | +6114 | 9% |
| biggest day in the run 60%+ | 51 | +51 / 1.18 | +1 / 1.00 | +223 / 1.29 | +384 / 1.43 | +850 / 1.99 | +289 / 1.32 | +764 / 1.87 | 63% | 16/22 | +10436 | 0% |
| chg20 <50% | 113 | +16 / 1.12 | +73 / 1.25 | +203 / 1.48 | +289 / 1.55 | +155 / 1.20 | +274 / 1.52 | +134 / 1.17 | 60% | 13/22 | +4547 | 29% |
| chg20 50-150% | 114 | +17 / 1.10 | +2 / 1.01 | +30 / 1.05 | +56 / 1.08 | +161 / 1.18 | +34 / 1.05 | +146 / 1.16 | 58% | 15/22 | +6058 | 6% |
| chg20 150%+ | 46 | +87 / 1.31 | +162 / 1.28 | +250 / 1.29 | +483 / 1.52 | +886 / 1.80 | +365 / 1.38 | +781 / 1.70 | 65% | 15/22 | +10442 | 1% |
| prev close $2-5 | 70 | +15 / 1.06 | +59 / 1.13 | +167 / 1.25 | +270 / 1.34 | +396 / 1.38 | +244 / 1.31 | +376 / 1.36 | 64% | 17/22 | +8004 | 6% |
| prev close $5-15 | 107 | +42 / 1.25 | +69 / 1.19 | +178 / 1.31 | +277 / 1.41 | +343 / 1.39 | +227 / 1.33 | +294 / 1.33 | 61% | 19/22 | +6002 | 14% |
| prev close $15+ | 95 | +24 / 1.18 | +47 / 1.15 | +76 / 1.15 | +134 / 1.23 | +125 / 1.16 | +108 / 1.18 | +102 / 1.13 | 56% | 14/22 | +4699 | 13% |

### T5 — year table, SHORT, first red: prev_streak >= 3 AND prev_run_gain >= 0.5 AND rev_prev < 0 AND retrace < 0.5
| year | n | r1 | r5 | r10 | r5 @open | win5 | p95 mfe5 |
|---|---|---|---|---|---|---|---|
| 2005 | 27 | +82 / 1.33 | -252 / 0.68 | -138 / 0.82 | -178 / 0.75 | 56% | +4778 |
| 2006 | 32 | +231 / 4.72 | +403 / 3.27 | +635 / 3.73 | +364 / 3.09 | 62% | +1624 |
| 2007 | 44 | +123 / 1.59 | +22 / 1.04 | +322 / 1.76 | +28 / 1.06 | 57% | +4813 |
| 2008 | 417 | +205 / 1.59 | +386 / 1.57 | +518 / 1.63 | +378 / 1.56 | 62% | +4676 |
| 2009 | 290 | +6 / 1.02 | +8 / 1.01 | -111 / 0.88 | +80 / 1.12 | 51% | +4583 |
| 2010 | 50 | +128 / 1.87 | +154 / 1.50 | +329 / 2.01 | +117 / 1.41 | 56% | +2857 |
| 2011 | 42 | +21 / 1.08 | +114 / 1.31 | +311 / 1.77 | +164 / 1.56 | 60% | +4439 |
| 2012 | 47 | -12 / 0.95 | +103 / 1.39 | +301 / 1.85 | +141 / 1.57 | 54% | +2804 |
| 2013 | 72 | +51 / 1.18 | -209 / 0.68 | -269 / 0.72 | -268 / 0.59 | 46% | +5929 |
| 2014 | 80 | +130 / 1.52 | +186 / 1.35 | +768 / 2.81 | +256 / 1.55 | 57% | +4666 |
| 2015 | 115 | +10 / 1.03 | +60 / 1.09 | +212 / 1.31 | +111 / 1.20 | 62% | +2961 |
| 2016 | 189 | -13 / 0.96 | -18 / 0.97 | -53 / 0.94 | +22 / 1.04 | 56% | +3719 |
| 2017 | 133 | +138 / 1.39 | +216 / 1.28 | +152 / 1.15 | +166 / 1.23 | 57% | +7924 |
| 2018 | 128 | +253 / 2.03 | +42 / 1.05 | +199 / 1.23 | -18 / 0.98 | 54% | +6044 |
| 2019 | 161 | -76 / 0.77 | -36 / 0.94 | +199 / 1.33 | -120 / 0.82 | 48% | +3864 |
| 2020 | 1062 | +167 / 1.62 | +510 / 2.07 | +35 / 1.04 | +420 / 1.83 | 67% | +4674 |
| 2021 | 621 | -118 / 0.76 | +90 / 1.11 | +309 / 1.30 | +87 / 1.11 | 64% | +7271 |
| 2022 | 416 | +84 / 1.24 | +309 / 1.47 | +700 / 2.20 | +229 / 1.34 | 60% | +6119 |
| 2023 | 364 | +168 / 1.59 | +200 / 1.33 | +363 / 1.46 | +91 / 1.14 | 56% | +5455 |
| 2024 | 520 | +21 / 1.04 | +112 / 1.13 | +432 / 1.44 | +89 / 1.10 | 58% | +9369 |
| 2025 | 693 | +40 / 1.08 | +270 / 1.36 | +172 / 1.14 | +246 / 1.34 | 60% | +7474 |
| 2026 | 487 | -96 / 0.80 | +177 / 1.21 | +605 / 1.64 | +135 / 1.16 | 58% | +7152 |

### Verdict — it works (18/22 years), and the control says the RED DAY IS NOT NEEDED: the green climax day itself is the better short

- **T1**: streak ≥ 3, run ≥ 50%, first red, retrace < 0.5: **r5 +225 / 1.33, r10 +280 / 1.32, 60%
  win, 18/22 years, 272/yr**; run ≥ 100%: r10 +570 / 1.51 (77/yr, 14/22). The 20-50% run is
  nothing (r10 negative, 68% of its trips on 10+-signal days — it is the market). Entry at the next
  open costs ~30-40 bp of the 5-day (+190 / 1.28) but keeps the shape.
- **T2 — the user's retrace condition is right, and tighter is better**: barely red (retrace
  0-0.15) r5 +255 / 1.47, 0.15-0.3 +267 / 1.37 (17/22); **0.3-0.75 is NEGATIVE at 1-5 days**
  (−134 / 0.78 r1; the deep red day bounces) and only recovers at 10 days. The cut belongs at 0.3.
- ⭐⭐ **T3 — the CONTROL wins**: shorting the GREEN climax day with no red yet (`up_streak_d ≥ 3`,
  run incl. today ≥ 50%): r5 +313 / 1.44, r10 +339 / 1.36, **19/22**, 425/yr; `streak_d ≥ 4` and
  run ≥ 100%: **r5 +691 / 1.76, r10 +884 / 1.81, 67% win, 21/22 years**, 77/yr. Waiting for the
  red candle costs ~90 bp of the 5-day and one year of consistency: the first red day has already
  taken the first leg of the unwind, and the barely-red cell (T2) is exactly the one that kept most
  of it. A red day with NO climax is a losing short (−25 / 0.88, 4/22) — the climax is the whole
  signal; the red candle is a late confirmation of it.
- **T4**: inside the first-red cell, D's range 15%+ (r10 +431..+752), the gap-down that was bought
  back above its open (+397 / 1.64), QUIET volume on D (rvol < 1: +294 / 1.44 — the buyers are
  gone, not fighting), the run's biggest day ≥ 60% (r10 +850 / 1.99) and `chg20 ≥ 150%` (r10 +886
  / 1.80) all lift it; $15+ names carry the least.
- **T5 / clustering**: 2005, 2013, 2016, 2019 negative at r5; 2020 is 1,062 of ~6,000 trips (the
  mania year, +510 / 2.07) but the cell is 22% on 10+-signal days and the run ≥ 100% cells are 4% —
  idiosyncratic, like S4's runner. Tail p95 mfe5 +47..+100% as everywhere on this side.

**Where this sits**: the climax short is the S4 runner cell with a cleaner definition — `N green
closes in a row × run gain` replaces `chg20 × range`. The confirmation the folklore waits for is a
cost, not a filter. The best cell on the whole daily short side so far is **4+ green closes with a
100%+ run, shorted at the 4th close: r10 +884 / 1.81, 21/22 years, ~77/yr** — and its tail
(p95 +100%) and borrow are, as before, the two unsolved things.

## S7 — the user's climax spec: 3-day move > 50% × 52w CLOSING high × extreme volume × er10 > 0.8/0.9 (2026-09-07)

> USER: *"How about if the last 3 days are up more than 50% and the previous day closed at a 52w high
> based on bar closes instead of highs. By itself 3 green days doesn't mean much, it should be a
> climax move on extreme volume, and prior to the red day, the eff score should be > 0.8 maybe even
> 0.9."*

Script `scripts/equity/springflyer_climax2.py`, log `data/springflyer_climax2.log`. New features:
`chg3_prev` (close(D−1)/close(D−4) − 1), `at52_prev` (D−1's close ≥ the max CLOSE of the prior 252
sessions), `run_rvol_max3` (the loudest rvol of D−1..D−3), `prev_er10s` (the signed 10-day ratio at
D−1's close), `retrace3` (D's give-back of the 3-day move); `chg3_d / at52_d / rvol_d` for the
green-day control. Short at D's close; short returns.

### T1 — THE LADDER (first red day, retrace of the 3-day move < 0.3): each condition added in turn
| cell | n/yr | yrs w/ trips | r_open1 | r1 | r3 | r5 | r10 | win5 | yrs5 | med r5 | p95 mfe5 | trips on 10+ days |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 3-day move >= 50%, red, retrace3 < 0.3 | 237 | 22 | +63 / 1.36 | +96 / 1.23 | +199 / 1.31 | +245 / 1.30 | +481 / 1.55 | 62% | 15/22 | +445 | +7733 | 7% |
| + D-1 at a 52w CLOSING high | 72 | 22 | +80 / 1.51 | +99 / 1.24 | +109 / 1.16 | +199 / 1.27 | +370 / 1.42 | 58% | 13/22 | +237 | +7340 | 1% |
| + loudest climax day rvol >= 5 | 58 | 22 | +75 / 1.45 | +85 / 1.21 | +108 / 1.17 | +213 / 1.31 | +311 / 1.37 | 57% | 14/22 | +196 | +7173 | 0% |
| + loudest climax day rvol >= 10 | 48 | 22 | +64 / 1.37 | +77 / 1.19 | +98 / 1.16 | +207 / 1.30 | +372 / 1.48 | 56% | 14/22 | +137 | +7459 | 0% |
| + er10 at D-1 >= 0.8 (with rvol >= 5) | 34 | 22 | +100 / 1.58 | +141 / 1.34 | +179 / 1.28 | +311 / 1.45 | +418 / 1.50 | 58% | 18/22 | +265 | +7899 | 0% |
| + er10 at D-1 >= 0.9 (with rvol >= 5) | 17 | 21 | +124 / 1.64 | +221 / 1.51 | +326 / 1.51 | +599 / 1.93 | +730 / 1.92 | 62% | 17/21 | +467 | +7201 | 0% |
| er10 >= 0.9, rvol >= 10 | 14 | 21 | +121 / 1.59 | +224 / 1.49 | +294 / 1.44 | +583 / 1.88 | +804 / 2.10 | 61% | 15/21 | +423 | +7591 | 0% |
| CONTROL: same but NOT at a 52w high | 24 | 21 | +89 / 1.54 | +157 / 1.40 | +164 / 1.23 | +70 / 1.07 | +428 / 1.46 | 60% | 13/21 | +206 | +9000 | 0% |
| CONTROL: same but er10 < 0.5 | 2 | 9 | +49 / 1.47 | +48 / 1.13 | -136 / 0.82 | -140 / 0.85 | +194 / 1.18 | 60% | 5/9 | +194 | +6608 | 0% |
| CONTROL: same but quiet (rvol max < 2) | 4 | 21 | +33 / 1.34 | +46 / 1.12 | +136 / 1.21 | +32 / 1.04 | +203 / 1.17 | 66% | 14/21 | +339 | +8009 | 0% |

### T2 — bands inside (3-day >= 50% x 52w-high x red): efficiency at D-1, loudest rvol, the move's size
| cell | n/yr | yrs w/ trips | r_open1 | r1 | r3 | r5 | r10 | win5 | yrs5 | med r5 | p95 mfe5 | trips on 10+ days |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| er10 at D-1 <0.5 | 3 | 10 | +65 / 1.60 | +170 / 1.55 | -18 / 0.97 | +201 / 1.24 | +518 / 1.52 | 61% | 9/10 | +521 | +6425 | 0% |
| er10 at D-1 0.5-0.7 | 13 | 21 | +24 / 1.15 | +6 / 1.01 | -92 / 0.88 | +64 / 1.08 | +504 / 1.63 | 59% | 11/21 | +233 | +5963 | 0% |
| er10 at D-1 0.7-0.8 | 14 | 22 | +86 / 1.52 | +65 / 1.16 | +76 / 1.12 | -12 / 0.99 | -51 / 0.95 | 56% | 13/22 | +114 | +7435 | 0% |
| er10 at D-1 0.8-0.9 | 20 | 22 | +74 / 1.49 | +29 / 1.07 | +8 / 1.01 | +58 / 1.08 | +218 / 1.26 | 54% | 11/22 | +68 | +8251 | 0% |
| er10 at D-1 0.9+ | 21 | 22 | +120 / 1.72 | +234 / 1.57 | +372 / 1.61 | +552 / 1.82 | +680 / 1.81 | 64% | 18/22 | +468 | +7198 | 0% |
| loudest rvol <2 | 7 | 21 | +113 / 2.12 | +210 / 1.57 | +328 / 1.52 | +273 / 1.35 | +679 / 1.67 | 65% | 17/21 | +360 | +7693 | 0% |
| loudest rvol 2-5 | 8 | 19 | +89 / 1.60 | +109 / 1.23 | -70 / 0.93 | +34 / 1.03 | +547 / 1.53 | 63% | 13/19 | +526 | +7906 | 0% |
| loudest rvol 5-10 | 9 | 20 | +134 / 2.10 | +127 / 1.38 | +163 / 1.27 | +240 / 1.33 | -11 / 0.99 | 60% | 15/20 | +485 | +6411 | 0% |
| loudest rvol 10-25 | 14 | 22 | +70 / 1.41 | +115 / 1.34 | +78 / 1.14 | +116 / 1.17 | +185 / 1.20 | 56% | 13/22 | +154 | +6449 | 0% |
| loudest rvol 25+ | 34 | 22 | +61 / 1.35 | +62 / 1.14 | +106 / 1.16 | +245 / 1.35 | +449 / 1.62 | 56% | 13/22 | +124 | +7842 | 0% |
| 3-day move 50-80% | 34 | 22 | +61 / 1.49 | +82 / 1.27 | -18 / 0.97 | +20 / 1.03 | +206 / 1.25 | 56% | 13/22 | +93 | +5702 | 0% |
| 3-day move 80-150% | 21 | 22 | +72 / 1.47 | +15 / 1.04 | +66 / 1.11 | +123 / 1.17 | +208 / 1.22 | 55% | 12/22 | +157 | +6714 | 0% |
| 3-day move 150%+ | 17 | 22 | +128 / 1.56 | +239 / 1.41 | +424 / 1.56 | +658 / 1.79 | +917 / 1.96 | 67% | 17/22 | +914 | +10848 | 0% |
| retrace3 <0.1 | 33 | 22 | +61 / 1.63 | -24 / 0.93 | -66 / 0.89 | -42 / 0.94 | -4 / 0.99 | 51% | 13/22 | +8 | +5188 | 0% |
| retrace3 0.1-0.3 | 39 | 22 | +96 / 1.46 | +203 / 1.43 | +256 / 1.35 | +400 / 1.49 | +684 / 1.74 | 65% | 16/22 | +649 | +8243 | 1% |
| retrace3 0.3-0.6 | 28 | 22 | -23 / 0.94 | -100 / 0.86 | -114 / 0.90 | -49 / 0.96 | +360 / 1.27 | 64% | 12/22 | +704 | +11226 | 2% |
| retrace3 0.6+ | 9 | 20 | -96 / 0.79 | -111 / 0.85 | +100 / 1.10 | +652 / 1.87 | +1057 / 2.35 | 67% | 15/20 | +960 | +12933 | 12% |

### T3 — the GREEN-DAY CONTROL: short at the climax close itself (3-day move incl. today >= 50%, closed at a 52w closing high, green)
| cell | n/yr | yrs w/ trips | r_open1 | r1 | r3 | r5 | r10 | win5 | yrs5 | med r5 | p95 mfe5 | trips on 10+ days |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| green climax, 52w high | 129 | 22 | -37 / 0.90 | +93 / 1.16 | +166 / 1.20 | +311 / 1.35 | +592 / 1.63 | 60% | 18/22 | +387 | +9585 | 6% |
| + rvol today >= 5 | 49 | 22 | -70 / 0.86 | +175 / 1.29 | +237 / 1.28 | +400 / 1.44 | +586 / 1.61 | 60% | 19/22 | +570 | +10773 | 1% |
| + rvol today >= 5 AND er10 today >= 0.8 | 27 | 22 | -166 / 0.74 | +270 / 1.41 | +485 / 1.60 | +736 / 1.88 | +1069 / 2.36 | 65% | 19/22 | +865 | +11099 | 0% |
| + rvol today >= 5 AND er10 today >= 0.9 | 13 | 21 | -139 / 0.79 | +481 / 1.92 | +877 / 2.61 | +1142 / 2.94 | +1353 / 2.99 | 68% | 20/21 | +1089 | +9485 | 0% |
| + rvol today >= 10 AND er10 today >= 0.9 | 7 | 18 | -32 / 0.94 | +462 / 1.84 | +673 / 2.14 | +826 / 2.16 | +1156 / 2.76 | 62% | 14/18 | +744 | +9707 | 0% |

### T4 — year table, SHORT, the stacked first-red cell: chg3_prev >= 0.5 AND rev_prev < 0 AND retrace3 < 0.3 AND at52_prev = 1 AND run_rvol_max3 >= 5 AND prev_er10s >= 0.8
| year | n | r1 | r5 | r10 | win5 | p95 mfe5 |
|---|---|---|---|---|---|---|
| 2005 | 6 | +337 / 5.51 | +249 / 1.94 | +151 / 1.21 | 50% | +1172 |
| 2006 | 11 | +4 / 1.02 | -968 / 0.27 | -767 / 0.42 | 45% | +7562 |
| 2007 | 7 | +266 / 1.66 | +71 / 1.17 | +930 / 19.72 | 71% | +3982 |
| 2008 | 2 | +13 / inf | +147 / 26.84 | +415 / inf | 50% | +270 |
| 2009 | 13 | +440 / 2.25 | +404 / 1.90 | +497 / 1.75 | 46% | +4191 |
| 2010 | 12 | +185 / 2.39 | +132 / 1.63 | +849 / 7.33 | 50% | +2503 |
| 2011 | 7 | +124 / 2.83 | +399 / 38.32 | +287 / 5.61 | 57% | +890 |
| 2012 | 11 | +36 / 1.32 | +334 / 4.50 | -405 / 0.53 | 64% | +1118 |
| 2013 | 18 | +384 / 4.28 | +106 / 1.28 | +358 / 1.54 | 61% | +3119 |
| 2014 | 15 | +549 / 4.30 | +356 / 2.04 | +1349 / 8.07 | 60% | +5204 |
| 2015 | 16 | +26 / 1.08 | -73 / 0.92 | -1899 / 0.26 | 62% | +5200 |
| 2016 | 20 | +107 / 1.49 | -199 / 0.68 | -206 / 0.76 | 40% | +4976 |
| 2017 | 39 | +60 / 1.14 | +281 / 1.47 | +157 / 1.21 | 51% | +4323 |
| 2018 | 27 | -94 / 0.72 | -267 / 0.69 | +139 / 1.18 | 48% | +5742 |
| 2019 | 33 | +192 / 1.68 | +178 / 1.28 | +469 / 1.79 | 52% | +3012 |
| 2020 | 110 | +285 / 1.66 | +651 / 1.99 | +579 / 1.63 | 65% | +6101 |
| 2021 | 95 | -44 / 0.92 | +186 / 1.22 | +422 / 1.42 | 65% | +11728 |
| 2022 | 24 | +189 / 2.17 | +1037 / 5.70 | +1041 / 5.20 | 54% | +2163 |
| 2023 | 53 | -54 / 0.83 | +242 / 1.58 | +278 / 1.55 | 47% | +4093 |
| 2024 | 64 | +294 / 1.57 | +28 / 1.02 | +160 / 1.12 | 61% | +14679 |
| 2025 | 98 | +247 / 1.40 | +795 / 2.09 | +1072 / 2.68 | 58% | +9125 |
| 2026 | 59 | -72 / 0.87 | +122 / 1.14 | +329 / 1.31 | 58% | +7348 |

### Verdict — the ladder works and the ratio is the lever that matters; the 52w high is not; and the green climax day beats the red day AGAIN, by more

- **T1**: the base (3-day ≥ 50%, red, retrace3 < 0.3) is r10 +481 / 1.55, 15/22, 237/yr.
  ⚠ CORRECTED (user caught it, 2026-09-07): the 52w closing-high condition is NOT "removing edge" —
  that read compared rungs of different size. Split AT EACH RUNG (`data/springflyer_52w_split.log`):
  at the base and rvol rungs the not-at-52w complement is BETTER at 10 days (r10 +530 / 1.61 vs
  +370 / 1.42; +656 / 1.82 vs +311 / 1.37) and at the er ≥ 0.8 rung the 52w side is better at 5
  days (+311 / 1.45, 18/22 vs +70 / 1.07, 13/21) and equal at 10 (1.50 vs 1.46); at er ≥ 0.9 the
  two sides are 1.93 / 1.34 at r5 and 1.92 / 1.87 at r10 on 17 vs 7 per year. On the GREEN day the
  not-at-52w side wins every rung (base r5 1.64 vs 1.35; rvol ≥ 5: 1.86 vs 1.44; er ≥ 0.9: 4/yr at
  PF 10, too few to quote). Net: **the 52w closing high is a horizon-dependent, non-monotone
  condition — never a floor; the move and the ratio are what carry the cell, and the 52w high
  mostly trims the sample.** Volume alone does little (rvol ≥ 5 / 10:
  1.37 / 1.48). **The efficiency ratio is the lever**: er10 ≥ 0.8 → r5 +311 / 1.45, **18/22**;
  **er10 ≥ 0.9 → r5 +599 / 1.93, r10 +730 / 1.92, 17/21**, 17/yr; with rvol ≥ 10 r10 +804 / 2.10.
  T2 confirms it band by band: er 0.7-0.9 is FLAT (−12 / +58 at r5), **0.9+ is the whole cell
  (+552 / 1.82, 18/22)** — not a floor at 0.8, a knife at 0.9. The 3-day move size grades the same
  way (150%+: r10 +917 / 1.96). The retrace: **0.1-0.3 is the cell** (+400 / 1.49, 16/22); < 0.1
  (barely red) is NOTHING here (−42 / 0.94) — unlike S6's run-based version, the 3-day-move
  version wants a real red day; 0.3-0.6 bounces at 1-5 days (the same shape as S6).
- ⭐⭐ **T3 — the green-day control, stacked the same way**: 3-day ≥ 50% incl. today × 52w closing
  high × green: r10 +592 / 1.63, 18/22, 129/yr; **+ rvol today ≥ 5 × er10 today ≥ 0.9: r1 +481 /
  1.92, r3 +877 / 2.61, r5 +1,142 / 2.94, r10 +1,353 / 2.99, 68% win, 20/21 years, median r5
  +1,089**, 13/yr. The one cost: the NEXT OPEN goes against it (r_open1 −139 / 0.79 — the climax
  gaps up once more), so this is a short you must hold through a bad first morning. Every rung of
  the green ladder beats the corresponding red rung by 2-3× at r5.
- **T4**: the stacked red cell's year table is noisy at 6-20 trades/yr (2006 −968, 2015 −1,899 at
  r10, 2016 −199, 2018 −267), 2020-2025 all positive; the tail is p95 +50-147%.

**What the spec says, and what it changes**: the user's three additions rank as (1) the
efficiency ratio at 0.9 — a knife, the single strongest gate found on the daily short side; (2)
the volume — additive only at 10×+; (3) the 52w closing high — non-monotone, not a floor (see the corrected T1 note). And for the
second time (S6, S7) the red-candle confirmation is a cost: the green climax day with er10 ≥ 0.9
and rvol ≥ 5 is **PF ~3 at 5-10 days in 20/21 years** against the red day's ~1.9. The mechanism
reads as: the ratio says the move was ONE-WAY (nobody sold on the way up — no supply overhead,
every holder is in profit and untested); the first red day is when they start testing; shorting
the day BEFORE catches the gap-up-and-reverse. At 13-17/yr it is a low-frequency A+ cell, the same
frequency class as LowFader's grade A.

### S7 addendum — the retrace condition tightened to < 0.1 (user, 2026-09-07): it INVERTS the horizon

Log `data/springflyer_retrace.log`; fine bands under the stacked spec, both retrace definitions.

| spec | band | n/yr | r1 / PF | r5 / PF | r10 / PF | win5 | yrs5 | med r5 |
|---|---|---|---|---|---|---|---|---|
| 3-day ≥ 50% × rvol ≥ 5 × er10 ≥ 0.8 | **< 0.1** | 25 | +127 / 1.49 | +139 / 1.25 | +103 / 1.14 | 51% | 15/22 | +13 |
| | < 0.3 (current) | 58 | +148 / 1.36 | +210 / 1.26 | +422 / 1.48 | 59% | 16/22 | +227 |
| | **0.1-0.3** | 33 | +163 / 1.31 | +262 / 1.26 | **+665 / 1.67** | 64% | 16/22 | +668 |
| same, er10 ≥ 0.9 | < 0.1 | 10 | +182 / 1.71 | +291 / 1.59 | +258 / 1.37 | 50% | 12/21 | +6 |
| | 0.1-0.3 | 15 | +320 / 1.65 | +661 / 1.74 | **+1,059 / 2.18** | 67% | 15/22 | +1,048 |
| S6 run-based, streak ≥ 3 × run ≥ 50% × er10 ≥ 0.8 | < 0.1 | 46 | +68 / 1.26 | +153 / 1.29 | +43 / 1.05 | 54% | 13/22 | +62 |
| | 0.1-0.3 | 51 | +140 / 1.37 | +310 / 1.45 | +546 / 1.65 | 62% | 18/22 | +411 |

**Read**: the barely-red day (< 0.1) is the best ONE-DAY short and the worst TEN-DAY one — its
median 5-day return is ~0 in every spec (+13 / +6 / +62) and its r10 PF is 1.05-1.37 against
1.65-2.18 for 0.1-0.3. Tightening to < 0.1 keeps the first-day pop and loses the unwind: a
day that closed down 1-2% after a 50-150% run has not yet told the holders anything; 0.1-0.3 is
the day that did. The 0.3-0.5 band bounces for 1-3 days (negative r1/r3) and catches up by 10;
0.5+ (the deep red) is the best 10-day cell of all (+1,011 / 2.12 base) but through a −100 bp
first day. **Ruling: the cut stays at 0.3; the cell is 0.1-0.3; < 0.1 is a different (1-day) trade.**
The same holds under the S6 run-based retrace (< 0.1: r10 1.05; 0.1-0.3: 1.65).

## S8 — the SIMPLIFIED spec (user, 2026-09-07): er10 > 0.9 ∧ er20 > 0.9 ∧ new 52w CLOSING high ∧ 20-session range > 200%

> USER: *"Ok, let's simplify things. We'll look at patterns where both er20 and er10 are > 0.9, the
> previous day made a new 52w closing high, and the 20d range is >200%. How well does that do?"*

Log `data/springflyer_simple.log`. New features: `prev_er20s`, `prev_rng20c` / `rng20c_d` (the
20-session CLOSE range, max close / min close − 1, as of D−1 / including D), `prev_rng20` (the
high/low twin). All four conditions are values of ONE close; T1 shorts AT that close, T2 at the
next close (the user's "previous day" phrasing), split by what the next day did.

### T1 — SHORT AT THE SETUP CLOSE ITSELF (conditions on D: er10 > 0.9, er20 > 0.9, D a new 52w closing high, 20-session range)
| cell | n/yr | yrs | r_open1 | r1 | r3 | r5 | r10 | win5 | yrs5 | med r5 | p95 mfe5 | tail>50% | trips on 5+ days |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| no range condition | 57 | 22 | +1 / 1.00 | +100 / 1.59 | +217 / 2.00 | +224 / 1.80 | +218 / 1.55 | 53% | 20/22 | +26 | +3139 | 3% | 4% |
| + 20-session CLOSE range > 100% | 14 | 21 | +51 / 1.13 | +415 / 1.99 | +813 / 2.85 | +909 / 2.61 | +973 / 2.34 | 65% | 19/21 | +371 | +8628 | 9% | 0% |
| + 20-session CLOSE range > 200% | 10 | 21 | +77 / 1.15 | +581 / 2.15 | +1122 / 3.39 | +1298 / 3.23 | +1477 / 3.01 | 71% | 18/21 | +702 | +9803 | 11% | 0% |
| + 20-session CLOSE range > 300% | 7 | 21 | +102 / 1.16 | +691 / 2.20 | +1314 / 3.62 | +1608 / 3.90 | +1903 / 4.00 | 74% | 18/21 | +1049 | +9887 | 13% | 0% |
| + 20-session HIGH/LOW range > 200% | 11 | 21 | +63 / 1.13 | +518 / 2.04 | +1030 / 3.17 | +1177 / 2.95 | +1369 / 2.93 | 70% | 18/21 | +624 | +9771 | 11% | 0% |
| CONTROL: er10,er20 > 0.9, 52w high, close range 30-100% | 18 | 21 | -41 / 0.51 | -13 / 0.90 | +69 / 1.35 | +25 / 1.09 | +17 / 1.04 | 50% | 13/21 | +0 | +1851 | 1% | 0% |
| CONTROL: close range > 200%, 52w high, but er20 < 0.7 | 47 | 22 | -61 / 0.83 | -77 / 0.90 | +15 / 1.01 | +112 / 1.09 | +668 / 1.58 | 60% | 15/22 | +713 | +12034 | 19% | 10% |

### T2 — SHORT AT THE NEXT CLOSE (conditions as of D-1; D = the day after the setup)
| cell | n/yr | yrs | r_open1 | r1 | r3 | r5 | r10 | win5 | yrs5 | med r5 | p95 mfe5 | tail>50% | trips on 5+ days |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| D any, close range > 200% | 14 | 21 | +122 / 1.44 | +295 / 1.64 | +636 / 2.09 | +831 / 2.26 | +979 / 2.22 | 67% | 16/21 | +522 | +11392 | 12% | 2% |
| D RED (close < prev) | 9 | 21 | +22 / 1.07 | +149 / 1.26 | +434 / 1.61 | +617 / 1.74 | +882 / 2.00 | 65% | 16/21 | +538 | +13829 | 16% | 3% |
| D RED, retrace of the 20d close range < 0.3 | 6 | 21 | +102 / 1.63 | +385 / 2.16 | +447 / 1.78 | +448 / 1.62 | +575 / 1.61 | 61% | 15/21 | +282 | +6589 | 9% | 0% |
| D GREEN (close > prev; the climax continues) | 5 | 21 | +320 / 2.75 | +582 / 3.27 | +1027 / 3.83 | +1241 / 4.53 | +1173 / 2.80 | 72% | 15/21 | +483 | +4060 | 5% | 0% |
| D any, HIGH/LOW range > 200% | 15 | 21 | +111 / 1.41 | +299 / 1.68 | +613 / 2.05 | +813 / 2.28 | +962 / 2.25 | 67% | 16/21 | +482 | +10984 | 12% | 2% |

### T3 — year table, SHORT at the setup close: er10_signed > 0.9 AND er20_signed > 0.9 AND at52_d = 1 AND rng20c_d > 2.0
| year | n | r_open1 | r1 | r5 | r10 | win5 | p95 mfe5 |
|---|---|---|---|---|---|---|---|
| 2005 | 4 | +66 / 4.81 | -54 / 0.68 | +240 / 3.33 | -1328 / 0.00 | 75% | +748 |
| 2006 | 3 | -36 / 0.07 | +45 / 2.01 | -143 / 0.00 | -149 / 0.15 | 0% | +348 |
| 2007 | 11 | +1368 / 8.95 | +1484 / 13.60 | +1238 / 4.00 | +1064 / 3.56 | 73% | +3774 |
| 2008 | 4 | +86 / 2.45 | -166 / 0.17 | -355 / 0.07 | +452 / 5.45 | 25% | +1205 |
| 2009 | 20 | -200 / 0.29 | -484 / 0.30 | -1133 / 0.24 | -1579 / 0.23 | 40% | +17454 |
| 2010 | 1 | -18 / 0.00 | +345 / inf | +203 / inf | +1754 / inf | 100% | +966 |
| 2011 | 4 | -69 / 0.39 | -172 / 0.46 | +234 / 1.48 | +1478 / inf | 50% | +1787 |
| 2013 | 6 | -2 / 0.99 | +660 / 14.15 | +1540 / inf | +1902 / inf | 100% | +2456 |
| 2014 | 9 | -115 / 0.71 | -512 / 0.29 | +330 / 4.61 | +150 / 1.58 | 44% | +4969 |
| 2015 | 8 | -565 / 0.21 | +302 / 2.57 | +814 / 7.75 | +1123 / 71.66 | 88% | +5033 |
| 2016 | 8 | -285 / 0.27 | +517 / 3.22 | +939 / 87.35 | +937 / 10.95 | 88% | +3447 |
| 2017 | 18 | +106 / 1.29 | +805 / 3.20 | +2116 / 14.78 | +2254 / 7.65 | 89% | +4365 |
| 2018 | 7 | -1176 / 0.25 | +1977 / 13.05 | +2605 / 9.40 | +2102 / 5.31 | 86% | +7840 |
| 2019 | 13 | +128 / 6.15 | +216 / 2.48 | +481 / 3.49 | -118 / 0.87 | 54% | +2025 |
| 2020 | 15 | -231 / 0.75 | +636 / 1.67 | +2067 / 6.17 | +1606 / 2.30 | 87% | +10995 |
| 2021 | 18 | -114 / 0.89 | +1259 / 2.30 | +1800 / 2.39 | +2837 / 5.29 | 89% | +14130 |
| 2022 | 5 | +496 / 12.23 | +40 / 1.05 | +583 / 2.26 | +99 / 1.11 | 60% | +4297 |
| 2023 | 3 | +920 / 4.96 | +1986 / 405.65 | +4960 / inf | +8952 / inf | 100% | +1548 |
| 2024 | 22 | +600 / 2.12 | +1023 / 4.11 | +1688 / 2.46 | +2770 / 4.06 | 68% | +13494 |
| 2025 | 18 | +863 / 3.33 | +1155 / 3.63 | +3132 / 13.93 | +2408 / 4.06 | 78% | +5792 |
| 2026 | 15 | -727 / 0.46 | -67 / 0.95 | +1361 / 2.43 | +2723 / 4.89 | 64% | +18708 |

### Verdict — this is the cell. PF 3.2 at 5 days, 3.0 at 10, 18/21 years, ~10/yr, and it is the range that does it on top of the ratios

- **T1, at the setup close**: the two ratios × 52w closing high alone are a modest short
  (r5 +224 / 1.80, 20/22 years but median +26 at 57/yr). The 20-session close range is the
  multiplier: > 100% → r5 +909 / 2.61; **> 200% → r1 +581 / 2.15, r3 +1,122 / 3.39, r5 +1,298 /
  3.23, r10 +1,477 / 3.01, 71% win, median r5 +702, 18/21 years, 10/yr**; > 300% → r5 +1,608 / 3.90,
  r10 +1,903 / 4.00 at 7/yr. The high/low range reads the same (2.95). Both controls fail the way
  they should: the same ratios and 52w high with a 30-100% range are nothing (+25 / 1.09, median 0)
  and a 200% range at a 52w high with er20 < 0.7 is a 1-day LOSER (−77 / 0.90) that only pays at
  10 days (1.58) with a 19% tail — the ratios are what make it a next-day short.
- **T2, at the next close**: unconditional next-day entry r5 +831 / 2.26 (14/yr); if the next day
  was GREEN (the climax ran one more day) **r5 +1,241 / 4.53, 72% win, 15/21, p95 tail only +41%**
  — the extra day of climax is the best entry on the page; if RED, +617 / 1.74 with the fattest tail
  (p95 +138%). The red-day confirmation costs edge here too (S6, S7, S8 — three for three).
- **T3 — years**: 2009 is the one real loser (20 trades, r5 −1,133 — the March-2009 rebound names
  reaching 52w closing highs off crash lows; the only year where "a 200% 20-day range at a new high"
  was the MARKET and not a name); 2005/2006/2008 are 3-4 trades each. 2013-2026 positive at r5 in
  every year, most of them at PF > 2.3. Not clustered (0% of trips on 5+-signal days).
- ⚠ The tail: p95 mfe5 +98%, 11% of trades see a 5-day high 50%+ above the entry; the next-open
  gap is only +77 / 1.15 (it does not gap against you on average, but the 2018/2026 rows show
  −1,176 / −727 first-morning years). Borrow on these names is the practical question.

**Where this leaves the short side**: four studies converged on one object — a name that has gone
up in a straight line (both ratios > 0.9) far enough (a 3× range in 20 sessions) to a new high, is
a 5-10 day short at PF ~3 with a 70% win rate, at ~10 signals a year over 2005-2026 and ~15/yr
since 2017. This is SpringFlyer's spec candidate. Next: (1) the mc=1 book with one position per
name and the tail rule (ShortSnoozer's gap × volatility disqualifier), (2) borrow / locate reality
on the 10-20 names a year, (3) the intraday version on the 1s tape when Massive returns.

### S8 addendum — the 52w closing-high condition REMOVED (user, 2026-09-07): keep it

Log `data/springflyer_simple_no52.log`.

| cell | n/yr | r1 / PF | r5 / PF | r10 / PF | win5 | yrs5 | med r5 |
|---|---|---|---|---|---|---|---|
| S8 (at a new 52w closing high) | 10 | +581 / 2.15 | +1,298 / 3.23 | +1,477 / 3.01 | 71% | 18/21 | +702 |
| NOT at a 52w closing high | 4 | +655 / 2.94 | +1,106 / 2.53 | +1,293 / 2.28 | **49%** | **10/18** | **−18** |
| condition removed (both) | 13 | +601 / 2.30 | +1,246 / 3.01 | +1,428 / 2.77 | 65% | 17/21 | +526 |

The 4/yr the condition excludes are a different population: a 49% win rate, a NEGATIVE median,
10/18 years — a mean carried by the crushed names that tripled off a low (more than 60% below
their 52w high: +3,248 / 7.26 on ~1/yr) while the ones just under the high (0 to −10%) are a
LOSER (r5 −710 / 0.27, 6/15). Removing the condition adds 3 signals a year and costs 6 points of
win rate, 176 bp of median and a year; the year table without it has four negative years at r5
(2006, 2009, 2016, 2019) against S8's one. Here the 52w closing high IS doing work — as the
statement that the straight line went to a place nobody who bought in the last year is under
water, which is the trapped-holder condition the ratios describe locally. **Ruling: keep it.**

### S8 addendum 2 — the 52w closing high REPLACED by a 20d closing high (user, 2026-09-07): ⭐ better

Log `data/springflyer_simple_20d.log`. Ratios > 0.9 × 20-session close range > 200%, the new-high
condition at three horizons (289 spec rows; 86% are at a 20d closing high, 85% at a 63d, 73% at a 52w):

| new-high condition | n/yr | r1 / PF | r3 / PF | r5 / PF | r10 / PF | win5 | yrs5 | med r5 |
|---|---|---|---|---|---|---|---|---|
| none | 13 | +601 / 2.30 | +1,139 / 3.68 | +1,246 / 3.01 | +1,428 / 2.77 | 65% | 17/21 | +526 |
| **20d closing high** | 11 | +647 / 2.24 | +1,257 / 3.69 | **+1,492 / 3.63** | **+1,750 / 3.46** | 70% | 18/21 | **+852** |
| 63d closing high | 11 | +666 / 2.31 | +1,286 / 3.87 | +1,515 / 3.73 | +1,745 / 3.44 | 70% | 18/21 | +852 |
| 52w closing high (S8) | 10 | +581 / 2.15 | +1,122 / 3.39 | +1,298 / 3.23 | +1,477 / 3.01 | 71% | 18/21 | +702 |
| 20d high but NOT 52w | 2 | +1,022 / 2.68 | +2,027 / 5.45 | +2,602 / 6.37 | +3,303 / 6.84 | 68% | 8/11 | +3,185 |
| NOT a 20d closing high | 2 | +315 / 4.70 | +410 / 3.43 | **−279 / 0.70** | −566 / 0.60 | 32% | 7/15 | −136 |

**Read**: the population the new-high condition needs to exclude is the 2/yr that are NOT even at
a 20-day closing high (a straight-line tripling that has already rolled over: r5 −279 / 0.70, 32%
win) — and the 20d condition excludes exactly those while KEEPING the 2/yr the 52w condition threw
away (20d-high-but-not-52w: r5 +2,602 / 6.37, median +3,185 — the crushed name that tripled to a
one-month high). 63d ≡ 20d. Year table: 2006 (3), 2008 (4), 2009 (25) negative, 2019 flat, the
rest positive, 2016-2026 at PF 2.2-18 at five days.

**SPEC v1 (SpringFlyer short): `er10 > 0.9 ∧ er20 > 0.9 ∧ close ≥ max close of the prior 20
sessions ∧ 20-session close range > 200%`, short at that close, cover in 5-10 sessions: r5 +1,492 /
3.63, r10 +1,750 / 3.46, 70% win, 18/21 years, median +852 bp, ~11 signals/yr (~20/yr since 2017).**

### S8 addendum 3 — the er20 requirement REMOVED (user, 2026-09-07): 3.6× the signals at nearly the same PF; er10 is the load-bearing ratio

Log `data/springflyer_simple_er20.log`. Base = er10 > 0.9 × 20d closing high × 20-session close
range > 200%; er20 varied:

| er20 condition | n/yr | r1 / PF | r3 / PF | r5 / PF | r10 / PF | win5 | yrs5 | med r5 | p95 mfe5 |
|---|---|---|---|---|---|---|---|---|---|
| > 0.9 (SPEC v1) | 11 | +647 / 2.24 | +1,257 / 3.69 | +1,492 / 3.63 | +1,750 / 3.46 | 70% | 18/21 | +852 | +97% |
| **removed** | **40** | +397 / 1.70 | +938 / 2.62 | **+1,291 / 3.16** | **+1,656 / 3.26** | 70% | 18/21 | **+1,071** | +88% |
| ≤ 0.9 (what the spec excludes) | 28 | +298 / 1.51 | +812 / 2.30 | +1,211 / 2.99 | +1,619 / 3.19 | 70% | 17/20 | +1,188 | +83% |
| 0.8-0.9 | 18 | +446 / 1.96 | +897 / 2.60 | +1,267 / 3.06 | +1,580 / 3.00 | 70% | 13/19 | +1,116 | +80% |
| 0.6-0.8 | 8 | −30 / 0.96 | +673 / 1.88 | +968 / 2.47 | +1,535 / 2.93 | 70% | 16/18 | +1,064 | +108% |
| ≤ 0.3 | ~0 | −358 / 0.49 | −103 / 0.86 | −328 / 0.66 | +142 / 1.33 | 44% | 1/4 | −51 | +56% |
| > 0.8 | 29 | +524 / 2.07 | +1,036 / 2.98 | +1,354 / 3.27 | +1,646 / 3.16 | 70% | 18/21 | +1,008 | +86% |

And the er10 threshold: with er20 > 0.9 held, every er10 band below 0.9 is EMPTY (< 1/yr — a 20-day
ratio above 0.9 forces the 10-day one up); with er20 removed, `er10 0.5-0.9` is a much weaker cell
(r5 +333 / 1.28, 102/yr, next-open and r1 negative) and `er10 > 0.95` (er20 > 0.9) is r5 +1,599 /
3.98, r10 +1,932 / 4.06 at 9/yr.

**Read**: er10 is the load-bearing ratio; er20 is a fine-grader that costs 3.6× the signals for
+0.5 of PF at 5 days and +0.2 at 10, and the population it excludes is 70% win, 17/20 years, with
a HIGHER median (+1,188). Only the er20 ≤ 0.3 stub (a handful over 22 years) is bad, and 0.6-0.8
loses the first day (r1 −30) before paying. **Removing er20 gives 40 signals/yr at r5 PF 3.16 /
r10 3.26, median +1,071, 18/21 — the same consistency at 3.6× the frequency.** A floor at 0.6
(≈ 38/yr) removes the stub at no cost. Decision: the user's.

## ⭐⭐ S9 — SPEC v2 RATIFIED (user, 2026-09-07) and the first BOOK

> USER: *"A floor at 0.6 seems fine. At 1 trade every 1.5 weeks, this system would be quite good."*

**SPEC v2 (SpringFlyer, SHORT, daily bars)**: at day D's close, `er10 > 0.9 ∧ er20 > 0.6 ∧
close ≥ max close of the prior 20 sessions ∧ (max close / min close over the last 20 sessions incl.
D) − 1 > 2.0`, universe CS/ADRC with prior-20-day dollar volume ≥ $5M and a raw $2 floor on D−1's
close. Short at D's close; cover at the close 5-10 sessions later. Every input is a value of D's
close (the 15:59 convention); the efficiency ratios are Kaufman's on share-consistent closes.

Log `data/springflyer_spec_v2.log`. 827 signals / 439 names over 2005→2026-09-04 (37.6/yr;
66/yr since 2017); **42% of signals repeat a name within 14 days** — the climax prints the setup
on consecutive closes, so the book takes ONE position per name and drops the repeats inside the
hold (mc=1 per name; hold approximated as k × 1.45 calendar days).

### the mc=1 BOOK (one position per name; repeats inside the hold dropped), short returns
| hold | trades | trades/yr | win% | mean bp | median bp | PF | PF ex worst 5% | PF ex best 5% | worst trade | p95 5-day high | yrs PF>1 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 1d | 597 | 27 | 64% | +409 | +385 | **1.66** | 4.02 | 1.23 | -44105 | +9476 | 16/21 |
| 3d | 516 | 23 | 68% | +952 | +1000 | **2.45** | 5.23 | 1.89 | -16505 | +9719 | 18/21 |
| 5d | 491 | 22 | 70% | +1281 | +1217 | **2.95** | 8.22 | 2.36 | -22187 | +9837 | 17/21 |
| 10d | 480 | 22 | 74% | +1731 | +2107 | **3.17** | 7.33 | 2.64 | -19094 | +9963 | 17/21 |

### year table, mc=1 book, 5-day hold
| year | trades | win% | mean | median | PF | worst | sum bp |
|---|---|---|---|---|---|---|---|
| 2005 | 2 | 50% | -32 | -32 | 0.84 | -411 | -64 |
| 2006 | 2 | 50% | +1872 | +1872 | 36.84 | -104 | +3745 |
| 2007 | 10 | 50% | +941 | +367 | 1.90 | -4736 | +9412 |
| 2008 | 3 | 33% | +256 | -715 | 1.50 | -820 | +767 |
| 2009 | 19 | 37% | -16 | -56 | 0.98 | -8992 | -299 |
| 2010 | 5 | 60% | +1209 | +203 | 23.09 | -141 | +6046 |
| 2011 | 5 | 0% | -452 | -118 | 0.00 | -1302 | -2262 |
| 2013 | 8 | 88% | -1110 | +1165 | 0.55 | -19582 | -8882 |
| 2014 | 8 | 75% | +1370 | +694 | 34.65 | -262 | +10956 |
| 2015 | 8 | 88% | +1313 | +527 | 11.90 | -964 | +10504 |
| 2016 | 15 | 60% | +803 | +349 | 2.59 | -5761 | +12041 |
| 2017 | 25 | 88% | +1908 | +2018 | 19.23 | -1586 | +47689 |
| 2018 | 11 | 91% | +2829 | +1707 | 11.63 | -2927 | +31116 |
| 2019 | 19 | 53% | +571 | +603 | 1.94 | -5792 | +10852 |
| 2020 | 63 | 78% | +1467 | +2098 | 3.44 | -14428 | +92406 |
| 2021 | 44 | 82% | +1462 | +1721 | 3.18 | -22187 | +64322 |
| 2022 | 23 | 57% | +1099 | +80 | 3.63 | -2218 | +25272 |
| 2023 | 26 | 65% | +1583 | +1238 | 4.99 | -4658 | +41150 |
| 2024 | 68 | 68% | +682 | +1196 | 1.51 | -18121 | +46379 |
| 2025 | 71 | 82% | +2327 | +2293 | 12.95 | -2781 | +165194 |
| 2026 | 56 | 68% | +1122 | +1311 | 2.13 | -11650 | +62840 |

### what the names look like (5d book)
       prev_close_raw    dv20_prior  rvol_d  chg20      mfe5        r
count           491.0  4.910000e+02   491.0  491.0     491.0    491.0
mean             18.9  1.199169e+08     7.6    6.2    2541.0   1281.4
std              51.6  5.044691e+08    13.3   15.5    6620.9   3471.7
min               2.0  5.000715e+06     0.0    1.3   -8849.3 -22186.7
10%               2.5  6.976070e+06     0.4    1.9    -628.8  -1592.7
25%               3.9  1.235711e+07     1.2    2.2      76.7   -112.4
50%               7.9  2.841190e+07     3.6    2.9    1054.4   1216.7
75%              18.2  7.882857e+07     8.6    5.2    3443.5   3084.6
90%              33.4  1.723794e+08    17.1    9.7    6270.7   4976.6
max             908.4  7.338326e+09   128.3  252.9  118220.0   9743.6

### the 10 worst 5-day trades
ticker       date  prev_close_raw  chg20  rvol_d  r_open1      r1      r5    mfe5
  MRIN 2021-06-28             4.0    4.0    21.0   2440.0 13067.0 22187.0 26347.0
   NBG 2013-05-17             2.0    3.0     3.0   -962.0 -3389.0 19582.0 22218.0
  AGFY 2024-11-19            19.0    5.0     0.0   1211.0  7381.0 18121.0 21161.0
  WKHS 2020-06-25             9.0    2.0     7.0    631.0  1565.0 14428.0 16752.0
  SERV 2024-07-22             8.0    3.0    11.0   -741.0   718.0 12109.0 12366.0
  MGRT 2026-04-09            44.0    5.0     1.0   -461.0   140.0 11650.0 24965.0
  LUMN 2024-07-31             3.0    2.0     4.0    381.0   540.0 11048.0 14857.0
   GGC 2009-07-30             7.0   26.0     2.0    226.0  1176.0  8992.0 20504.0
  LAES 2024-12-17             2.0    5.0     2.0   -604.0 -2148.0  8356.0 11040.0
   CAR 2026-04-14           371.0    3.0     3.0   -416.0  -384.0  7348.0  8611.0

### Verdict

- **The book**: 5-day hold **491 trades / 22 per year, 70% win, mean +1,281 bp, median +1,217,
  PF 2.95, 17/21 years**; 10-day hold **PF 3.17, 74% win, median +2,107**. ⭐ Unlike every
  LongHiker cell, it survives the tail check in BOTH directions: PF 2.36 / 2.64 without its best
  5% of trades, 8.2 / 7.3 without its worst 5%. The edge is in the bulk, not the tail.
- **Years**: 2009 (19 trades, PF 0.98 — the crash-rebound names again), 2011 (5 trades, 0/5) and
  2013 (8 trades; ONE −196% trade, NBG 2013-05-17, a Greek-bank recapitalisation ADR — to be
  tape-checked) are the negative years; 2014-2026 all PF > 1.5 except 2024 (1.51 with an AGFY
  −181%); 2017-2026 at 22-71 trades/yr.
- ⚠⚠ **THE TAIL IS THE SYSTEM'S RISK, AND IT IS THE SQUEEZE**: p95 of the 5-day high is +98%
  against the entry; the ten worst trades are −70% to −222% in 5 days (MRIN +222% / +130% on day
  one, AGFY +181%, NBG +196%, WKHS +144%). These are not data errors — they are the mania names
  going parabolic after the spec fires. A −200% trade at full size is ruin; the book's PF exists
  only because there are 490 others. **Sizing and a hard stop are not optional here**: the S2/S3
  reading (the extreme close mean-reverts one night) says the first-morning gap is where the
  squeeze declares itself — `r_open1` on the ten worst is +2,440 / +1,211 / +631 for the three
  biggest. Borrow on these names is the other half of the question.
- **What the names are**: median prior close $7.9 (p10 $2.5), median dollar volume $28M/day
  (p10 $7M), median rvol 3.6 on the signal day, median 20-day change +290% (p10 +190%): the
  low-priced mania runner, MaxFlyerV2's population, at a 20-day frequency instead of intraday.

⏭ **Open before it is tradeable**: (1) a tail rule — hard stop at the next open / +X% / the
ShortSnoozer gap × volatility disqualifier, measured on this book; (2) the NBG/MRIN/AGFY worst
trades tape-checked; (3) borrow/locate reality on 20-70 names a year at $2-10 (the
`lowflyer_short_productionization_research.md` question); (4) the exact-session mc=1 (this one
approximates the hold in calendar days); (5) the intraday version when Massive is back.

### S9 addendum — cover at the first 10-day CLOSING LOW instead of the timestop (user, 2026-09-07)

Log `data/springflyer_exit_10dlow.log`. Forward path walked on `daily_episodes_causal` (keyed on
ticker × episode × row; the first attempt joined on ticker only and pulled other episodes' rows
for recycled symbols — caught by a mean of −943,501 bp; the fixed walk reproduces the feature
table's `r5` to 0.0 bp). Exit = the first close BELOW the lowest close of the trailing N sessions,
with a session cap; mc=1 per name as in S9.

| exit rule | trades | win% | mean bp | median | PF | PF ex best 5% | worst | mean hold (sessions) | exits by rule % | yrs PF>1 |
|---|---|---|---|---|---|---|---|---|---|---|
| 5-day timestop | 491 | 70% | +1281 | +1217 | **2.95** | 2.36 | -22187 | 5.0 | 0% | 17/21 |
| 10-day timestop | 480 | 74% | +1736 | +2107 | **3.17** | 2.64 | -19094 | 10.0 | 0% | 17/21 |
| 20-day timestop | 467 | 73% | +2100 | +2804 | **3.30** | 2.80 | -32202 | 20.0 | 0% | 19/21 |
| first 10-day CLOSING LOW, cap 20 | 481 | 74% | +1925 | +2584 | **3.23** | 2.74 | -32202 | 13.5 | 73% | 18/21 |
| first 10-day closing low, cap 30 | 481 | 74% | +1913 | +2644 | **3.10** | 2.63 | -40552 | 15.1 | 87% | 17/21 |
| first 10-day closing low, cap 40 | 479 | 75% | +1909 | +2651 | **3.01** | 2.57 | -36512 | 15.8 | 94% | 17/21 |
| first 5-day closing low, cap 20 | 481 | 77% | +1548 | +1894 | **3.17** | 2.61 | -32202 | 7.8 | 94% | 18/21 |
| first 20-day closing low, cap 40 | 478 | 74% | +2196 | +3255 | **3.03** | 2.63 | -50040 | 27.0 | 74% | 19/21 |
| first 10-day closing low after >= 5 sessions, cap 30 | 481 | 74% | +1919 | +2644 | **3.10** | 2.63 | -40552 | 15.4 | 87% | 17/21 |

### the 10-day-closing-low exit (cap 30): when it fires, and what each bucket returns
| exit session | trades | win% | mean | median | PF |
|---|---|---|---|---|---|
| 1-2 | 5 | 60% | +2917 | +7508 | 2.33 |
| 3-5 | 14 | 100% | +5755 | +6118 | inf |
| 6-10 | 167 | 100% | +4188 | +4260 | inf |
| 11-20 | 174 | 76% | +2029 | +2258 | 5.27 |
| 21-30 (incl. the cap) | 121 | 34% | -1880 | -1078 | 0.34 |

**Read**: the 10-day-closing-low exit with a 20-session cap is **PF 3.23, mean +1,925, median
+2,584, 74% win, 18/21 years, 13.5 sessions average** — a hair above the 10-day timestop (3.17 /
+1,736 / +2,107) and a hair below the 20-day timestop (3.30 / +2,100 / +2,804, 19/21) at a shorter
hold. The 5-day-low version is the same thing faster (3.17 at 7.8 sessions). The bucket table says
what it is: **a take-profit.** Trades where the new 10-day low prints in sessions 3-10 are 100%
winners at +4,188 bp (by construction — undercutting the pre-run closes within two weeks of a
climax IS the unwind); the 25% that never print it by the cap are the losers (34% win, −1,880) and
they carry the same worst trade as the timestops (−322%; −405% at cap 30). **It lets the winners
run and does nothing about the losers** — the squeeze tail is in exactly the trades that never make
a new low. So: a reasonable exit (take it over the 10-day timestop if you like the higher median
and the earlier average exit), but NOT the risk control. The tail rule is still the open item and
it has to be on the ADVERSE side (a stop), not on the profit side.

### S9 decisions (user, 2026-09-07 close of session)

- **Exit = the 10-day timestop** (PF 3.17, 74% win, median +2,107, 17/21). The 10-day-low exit is
  a take-profit variant, not adopted.
- **Borrow is the known blocker**: *"These are the exact patterns that I couldn't get borrows for at
  IBKR back in 2013-2014."* Short availability for this population ($2-10, mania runners) to be
  evaluated at **TradeZero, Lightspeed and IBKR** before anything goes live
  (`docs/lowflyer_short_productionization_research.md` is the prior work on locates).
- The tail rule (an adverse-side stop) remains open. User: *"I like this system a lot."*
- Next up: LongHiker revisited with a consolidation feature (user's idea, to be explained).
