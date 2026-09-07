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
