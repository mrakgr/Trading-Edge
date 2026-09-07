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
