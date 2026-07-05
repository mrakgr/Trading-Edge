# MaxFlyerV2 — Extended-Pop Fade Research Log (the intraday SHORT book)

**`TradingEdge.MaxFlyerV2`** — the intraday **short pop-fade**, forked out of LowFlyer so the short
and the long (LowFlyer's flush-fade) are separate systems with independent defaults (they had diverged
and were colliding through one shared `defaultConfig` — e.g. the long's `vol-high-frac 0.90` bleeding
into the short). Named MaxFlyerV2 as it takes the slot of the deleted parked MaxFlyer.

The **mirrored** short book: fade the high-volume 1m breakout to a new session **HIGH**
(`Downside=false, Short=true` — the true mirror of the long's new-low flush-fade). Same intraday
engine, P&L sign flipped in `toTrip`, breakout ref / flush gate / 2-bar stop all flip with `Downside`.
(Research below was conducted as `TradingEdge.LowFlyer --short` on branch `highflyer_v2`; the findings
carry over verbatim — the fork changes only the project/defaults, not the engine or signals.)

**This is the OPPOSITE philosophy to the long.** The long is a *pullback* fade — buy a
flush in a name that was flat-to-strong going in (floors on negative returns). The short
is an *extension* fade — short a pop in a name that is **up big over multiple days AND up
big today**, i.e. an overextended parabolic blow-off. So the short selection **RAMPS UP
positive thresholds** (3d, 1d, 20m all `>` large positive numbers) instead of flooring
negatives. `chg_1d/chg_3d/chg_20m` are entry vs 1d/3d/20m-ago close — POSITIVE = up into
the fade.

**METRIC = RAW PF (unclipped).** Unlike the long, a short's per-trade return is bounded
at +100% (price can only fall to zero), so there is no unbounded winner-skew to clip
against — raw PF is the correct, honest metric. (Raw ≈ clipped anyway here: the short's
edge is broad-based across ~70% win rates, not one-monster-reversal-driven.)

**Gates DISABLED for now:** the 1m-flush gate and the intraday-ATR% gate are OFF (input
CSV = `--short` only, ungated). We're dialing the multi-day selection first, then will
revisit the intraday gates.

Population: whole ungated short book = 128,806 trips, PF 1.208, 55% win, +0.5% avg.

---

## Selection ramp — each of 3d / 1d / 20m is a positive FLOOR (raw PF)

Ramping each threshold up (holding the others at the working baseline). The whole-book
PF 1.20 lifts sharply once multi-day + single-day extension is required.

**(1) ramp 3d (holding 1d > +30%):** helps, but flattish until the extreme.

| 3d floor | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| > +0% | 13,710 | 68.9 | 1.57 | +4.0 |
| > +50% | 8,858 | 69.8 | 1.63 | +4.9 |
| > +100% | 3,855 | 69.5 | 1.67 | +5.9 |
| > +300% | 500 | 73.6 | 2.05 | +9.6 |

**(2) ramp 1d (holding 3d > +50%): 1d is the DOMINANT lever** — the more it's up *today*,
the harder it fades.

| 1d floor | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| > +0% | 12,922 | 67.2 | 1.54 | +3.7 |
| > +30% | 8,858 | 69.8 | 1.63 | +4.9 |
| > +50% | 6,065 | 71.5 | 1.66 | +5.8 |
| > +75% | 3,319 | 71.7 | 1.75 | +7.3 |
| > +100% | 1,884 | 70.8 | 1.82 | +8.4 |
| > +150% | 789 | 71.1 | **2.22** | +11.7 |

**(3) ramp 20m (holding 3d > +50%, 1d > +30%):** adds cleanly at the top (velocity — a
name still ripping into the pop fades best).

| 20m floor | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| any | 8,858 | 69.8 | 1.63 | +4.9 |
| > +10% | 7,590 | 70.9 | 1.67 | +5.5 |
| > +20% | 4,630 | 73.9 | 1.87 | +7.5 |

Win rate is ~70% everywhere and climbs to ~74% at the extremes — a higher-quality
profile than the long (68% win / +3.3% avg). Fading extended pops is a cleaner setup
than fading flushes.

---

## bar_rvol_15m — the exhaustion-spike feature (side-dependent, HUGE on the short)

`bar_rvol_15m = breakout_bar_vol / mean(1m vol over [9:30,9:45) ET)` — the breakout-bar
volume spike relative to the name's own opening-15m 1m tempo (baseline = `vol_0945 /
nbar_0945`, both already in `mr_candidate`; recorded first-class on the trip this
session). **Discriminates OPPOSITELY by side:**
- **LONG (fade flush):** inverted-U — moderate 3–8× best, extreme ≥40× COLLAPSES (PF
  1.15, they're falling-knives). Best long floor is a mild ~3×.
- **SHORT (fade pop):** the extreme tail is where the edge lives — a ≥40× spike into a
  new-session-high pop is an **exhaustion blow-off** that fades hard.

Stacked on the extended-fade (3d>100 & 1d>75 & 20m>10):

| stack | n | win% | raw PF | avg% |
|---|---:|---:|---:|---:|
| base (3d>100 1d>75 20m>10) | 2,132 | 72.6 | 2.03 | +9.5 |
| + bar_rvol ≥ 10 | 657 | 79.3 | **3.91** | +16.1 |
| + bar_rvol ≥ 40 | 159 | 84.9 | **8.44** | +22.8 |

The volume spike does enormous independent work on the short — 85% win, PF 8.4, +22.8%
avg at ≥40×. (To be swept finely as its own parameter.)

---

## 1m-return breakdown (entry-bar move) — baseline 3d>50 & 1d>50 & 20m>10

The entry-bar 1m move = `entry_price / prev_bar_close − 1` (the breakout-candle's own
%-move; POSITIVE up-spike since we short a pop to a new high). Baseline (5,474 trips) is
already strong: **PF 1.69, 72% win, +6.2% avg.** 1m distribution: median +5.7%, p90
+12.6%, max +99.8%.

| 1m return | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| 0–1% | 258 | 67.4 | 1.09 | +1.1 |
| 1–2% | 381 | 66.7 | 1.24 | +2.4 |
| 2–4% | 1,085 | 71.8 | 1.96 | +6.3 |
| 4–7% | 1,683 | 72.2 | 1.49 | +4.8 |
| 7–12% | 1,430 | 73.7 | 1.88 | +7.9 |
| 12–20% | 545 | 73.9 | 1.85 | +8.8 |
| **≥20%** | 92 | 78.3 | **3.87** | +18.6 |

FLOOR sweep: any 1.69 → ≥2% 1.77 → ≥7% 1.93 → ≥12% 2.04 → **≥20% 3.87**.

**Reading:** the 0–2% "up-spikes" (639 trips, PF 1.09–1.24) are soft — a pop to a new
high on a *tiny* 1m candle isn't a real spike; cut them cheaply with a ≥2% floor
(1.69→1.77, keeps 88%). The edge concentrates in the big candles (≥7% climbs monotone),
and the **≥20% blow-off bucket is exceptional** (PF 3.87) — same exhaustion signal as
bar_rvol, measured as a raw 1m %-move. (One non-monotone dip at 4–7% vs 2–4%, smoothed by
the floor sweep — likely sampling noise.)

---

## ATR% breakdown — it's a FLOOR on the short (mirror of the long's ceiling)

Intraday log-ATR at entry (`intraday_atr_pct_at_entry`), broken down on the baseline
3d>50 & 1d>50 & 20m>10 & 1m≥2% & bar_rvol≥4 (3,268 trips; ATR distribution: p25 0.032,
p50 0.043, p90 0.072, max 0.16). **The relationship is the EXACT OPPOSITE of the long.**
On the long, ATR% is a CEILING (< 0.02, reject chaos = falling-knife). On the short, it's
a **FLOOR — the jumpier the name, the harder the pop fades.**

| ATR% | n | win% | raw PF | avg% |
|---|---:|---:|---:|---:|
| 0.01–0.015 | 43 | 65.1 | 0.92 | −0.5 |
| 0.015–0.02 | 111 | 65.8 | 1.04 | +0.3 |
| 0.02–0.03 | 537 | 69.3 | 1.06 | +0.5 |
| 0.03–0.05 | 1,413 | 72.2 | 1.83 | +6.3 |
| **≥0.05** | 1,157 | **81.0** | **2.74** | +13.9 |

FLOOR sweep (monotone — everything improves as the floor rises): any 1.97 → ≥0.03 **2.25**
→ ≥0.05 **2.74** (81% win, +13.9% avg). CEILING sweep confirms the inversion — capping ATR%
(the long-side gate) DESTROYS the short: ≤0.02 → PF 1.03, +0.2% avg.

**Reading:** low-ATR/quiet names (< 0.03, PF ~1.0, ~0% avg) are dead weight — a pop to a
new high in a CALM name isn't an exhaustion blow-off, it's a legitimate grind-up that
keeps going, no fade edge. The edge lives in the CHAOTIC names (≥0.05 → PF 2.74): a
parabolic name whipping violently into a new high is the blow-off that reverses. Same
intuition as the 1m-return and bar_rvol findings — **the more violent/extreme the setup,
the better the short fade** — and the mirror of the long (long: high chaos = falling-knife
AVOID; short: high chaos = exhaustion SEEK). **The ATR gate must be FLIPPED for the short.**

---

## Float — the short does NOT need a float filter (unlike the long)

Float breakdown on the baseline 3d>50 & 1d>50 & 20m>10 & 1m≥2% & bar_rvol≥4 & ATR%≥0.03
(2,570 trips; ASOF float, re-anchored to entry price; 57% float coverage).

| float @ entry | n | win% | raw PF | avg% |
|---|---:|---:|---:|---:|
| NO DATA | 1,096 | 75.7 | 2.14 | +9.7 |
| <$300M | 1,402 | 76.6 | 2.35 | +9.9 |
| ≥$300M | 72 | 73.6 | 2.37 | +8.7 |

**No float edge.** On the long, `<$300M` was decisive (PF 2.20→2.90, large caps ~1.3 — the
flush-fade is a low-float overreaction). On the short, the ≥$300M cohort is JUST AS GOOD
(2.37) as <$300M (2.35) — the extended-pop fade works across the float spectrum. The
selection self-selects for low float anyway (57% coverage skews small-cap, ≥$300M is only
72 trips), so an explicit float filter is redundant AND costly: 43% of trips have no float
data and are PF 2.14, so a `<$300M` filter would DISCARD ~40% of a good book (the no-data +
large-cap cohorts) for no PF gain. **Leave float OUT of the short spec.** (One real soft
spot in the detail: the $50–150M sub-bucket, PF 0.92 on 183 trips.)

---

## 3d & 7d — NOT floor signals; the edge is RECENCY/COMPRESSION of the run-up, driven by 1d

**3d, 1d held at >50% (baseline):** the current 3d>50 floor barely works — floor sweep any
2.17 → >50% 2.25 → ≥100% 2.58 → ≥150% 2.65. The one hole is the **20–50% bucket (PF 1.59)**;
below/above it everything is PF ~1.9–2.8.

**3d with 1d DISABLED** (the decoupling test — reported `med_1d` per bucket exposes what's
really driving it):

| 3d | n | win% | raw PF | avg% | med 1d |
|---|---:|---:|---:|---:|---:|
| <0% (down 3d) | 301 | 75.7 | 2.56 | +8.9 | +43% |
| 0–20% | 250 | 75.6 | 2.55 | +7.1 | +40% |
| **20–50%** | 860 | 72.7 | **1.61** | +4.5 | +44% |
| 50–75% | 952 | 77.5 | 2.13 | +7.7 | +59% |
| 100–150% | 793 | 74.3 | 2.23 | +9.4 | +92% |
| 150–300% | 676 | 75.7 | **2.98** | +11.9 | +105% |

**Key finding: 3d is NOT the lever — 1d is, and 3d's apparent effect was mostly 1d leaking
through** (higher-3d buckets contain higher-1d names). Decoupled: even negative-3d names fade
well (PF 2.56) — a fresh 1-day explosion with no multi-day build-up is a fine setup. The only
genuinely weak cohort is **20–50% 3d (PF 1.61)**: moderate multi-day AND the lowest med_1d
(+44%) — extension without a real spike. A 3d floor is a BLUNT tool: 3d>50 throws away the
strong <0%/0–20% fresh-spike cells (PF 2.5+) just to also cut the 20–50% dead zone.

**7d, full baseline (2,532 trips, base PF 2.25):** also NOT a floor — sweep is flat (any 2.25
→ ≥100% 2.36 → ≥400% 2.96, but samples thin at the top).

| 7d | n | win% | raw PF | avg% | med 1d | med 3d |
|---|---:|---:|---:|---:|---:|---:|
| <0% (down 7d) | 63 | 74.6 | **1.57** | +7.1 | +79% | +76% |
| **0–50%** | 265 | 77.4 | **3.36** | +12.0 | +68% | +64% |
| 50–100% | 865 | 77.1 | 1.97 | +8.1 | +74% | +79% |
| 100–200% | 872 | 74.9 | 2.35 | +10.3 | +100% | +127% |
| ≥400% | 92 | 72.8 | 2.96 | +12.0 | +135% | +371% |

**The standout is the `0–50%` 7d cell: PF 3.36 — the best in the table** — with med_3d only
+64%: names way up in the last 1–3 days but only modestly up over 7 = a **fresh, compressed
1–3 day explosion, not a slow week-long grind.** Recency/compression fades best. The `<0%`
down-7d pocket (PF 1.57) is the weak mirror, same as down-3d.

**7d with 3d AND 1d DISABLED** (the clean decoupling — only 20m>10, 1m≥2, bar_rvol≥4,
ATR≥0.03; 4,654 trips, base PF 2.15):

| 7d | n | win% | raw PF | avg% | med 1d | med 3d |
|---|---:|---:|---:|---:|---:|---:|
| <−20% | 270 | 75.6 | **1.71** | +6.4 | +52% | −3% |
| −20..0% | 195 | 73.3 | 2.24 | +8.1 | +52% | +18% |
| 0–25% | 359 | 74.1 | **1.79** | +5.5 | +45% | +35% |
| 25–50% | 619 | 76.9 | **2.64** | +8.6 | +49% | +47% |
| 50–100% | 1,338 | 76.2 | 2.05 | +7.7 | +62% | +73% |
| 200–400% | 529 | 77.1 | 2.44 | +9.9 | +82% | +209% |
| ≥400% | 164 | 73.2 | 2.51 | +10.0 | +69% | +227% |

**The "fresh/compressed 0–50%" story does NOT survive decoupling.** With 1d/3d stripped and
the bucket split finer, `0–25%` is actually WEAK (PF 1.79) and only `25–50%` is strong (2.64)
— the earlier full-baseline 0–50% cell at PF 3.36 was INFLATED by the still-on 1d>50 & 3d>50
floors, not a clean 7d effect. Floor sweep flat (2.15 → 2.19 @≥0 → 2.25 @≥100), ceiling sweep
flat-to-down: **7d carries no usable floor OR ceiling on its own.** Two real weak pockets:
`<−20%` (1.71, down-window-but-spiking, mirror of down-3d) and `0–25%` (1.79, weak spike on a
name that wasn't going anywhere — lowest med_1d +45%).

**FINAL on 3d/7d: neither is a lever.** Fully decoupled, both are flat/noisy, and every cell
that looked strong was a 1d artifact. **The multi-day windows do NOT independently predict —
1d + the intraday signals (1m / bar_rvol / ATR / 20m) do all the work.** Argues AGAINST 3d/7d
floors; at most carve out weak pockets (20–50% 3d; <−20% & 0–25% 7d), but even that is marginal.
The current 3d>50 & 1d>50 baseline can likely have its 3d floor DROPPED (redundant with 1d)
without loss — to be confirmed by re-examining 1d directly next.

---

## Tightness & intraday-ATR% floors (3d/7d DROPPED — new baseline)

Dropped 3d & 7d (both shown to be non-levers). New baseline = **1d>50 & 20m>10 & 1m≥2% &
bar_rvol≥4 & ATR%≥0.03 → 3,084 trips, PF 2.17, 76% win, +9.3% avg** (essentially identical
to the old 2,570 with 3d>50 — confirming 3d was redundant with 1d). Then tested the HighFlyer
tightness + ATR% measures (intraday 1m versions: `intraday_tightness_at_entry` = range/ATR over
VolWindow; `intraday_atr_pct_at_entry` = 1m log-ATR).

**TIGHTNESS — a HIGH floor helps (opposite of HighFlyer's ceiling usage).** Distribution p50
5.9, p75 7.2, p90 8.7. Per-bucket the mid (5–8) is WEAKEST (PF ~1.9); the edge concentrates in
the ≥8 tail (8–9: 2.83, 9–11: 3.45, ≥11: 3.51).

| tightness > | n | win% | raw PF | avg% |
|---|---:|---:|---:|---:|
| any | 3,084 | 75.7 | 2.17 | +9.3 |
| > 7.0 | 869 | 76.2 | 2.49 | +11.2 |
| > 7.5 | 644 | 76.7 | 2.54 | +11.7 |
| **> 8.0** | 469 | 77.8 | **3.17** | +13.6 |
| > 8.5 | 342 | 77.5 | 3.17 | +13.7 |
| > 9.0 | 252 | 78.2 | 3.46 | +15.5 |

Knee at **> 8.0** (PF 2.17→3.17, keeps ~15%). HighFlyer uses tightness as a CEILING
(tight/coiled = good); here a HIGH tightness (range wide vs its own ATR = a big directional
spike stretched far beyond normal volatility) is what fades — same mirror-logic as ATR:
extreme STRETCH = exhaustion.

**INTRADAY ATR% — pushing the floor above 0.03 keeps helping.** The 0.03–0.04 slice is the
weak link (PF 1.50); raising the floor lifts PF and win% together (win → ~80%+).

| ATR% ≥ | n | win% | raw PF | avg% |
|---|---:|---:|---:|---:|
| ≥0.03 (current) | 3,084 | 75.7 | 2.17 | +9.3 |
| **≥0.04** | 2,151 | 78.8 | 2.47 | +11.5 |
| **≥0.05** | 1,318 | 80.6 | 2.56 | +13.2 |
| ≥0.06 | 768 | 81.8 | 2.51 | +14.2 |
| ≥0.08 | 220 | 83.6 | **3.42** | +19.9 |

Both point the same way (and correlate — high-ATR names tend to be high-tightness, so stacking
overlaps). Clean candidates: **ATR% ≥ 0.04–0.05** (mild tightening) and/or **tightness > 8**
(strong new filter). Confirms the running theme: the more the name is STRETCHED/violent
(1m spike, bar_rvol, ATR, tightness), the better the pop fades. *(Thresholds not yet locked —
user deciding.)*

---

## DAILY tightness & ATR% (the true HighFlyer measures, 14-day, episode-partitioned)

Computed the ACTUAL HighFlyer daily indicators (not the intraday 1m ones): daily ATR% =
`AVG(log_TR)` over 14 days, daily tightness = `(max_high_14 − min_low_14) / AVG(abs_TR_14)`,
on `daily_episodes` (adjusted, episode-partitioned), attached PRE-PUSH (through D−1, no
lookahead for an intraday-on-D trade). Joined 1:1 to the baseline (1d>50 & 20m>10 & 1m≥2% &
bar_rvol≥4 & intraday-ATR≥0.03; 3,061 trips, 99.3% coverage). Sanity: market-wide daily p50
ATR% 0.043 / tight 3.73, matching HighFlyer's MaxAtrPct=0.10 & MaxTightness=4.5 defaults.

**DAILY tightness — mild, weaker than the INTRADAY tightness.** `<3` is the weak cell (PF
1.36, not stretched daily); flat ~2.1–2.4 above; only the extreme helps: > 8.0 → PF 3.12
(210 trips). But **intraday tightness > 8 was PF 3.17 on 469 trips — the intraday version is
stronger and higher-capacity.** Use intraday tightness, not daily.

**DAILY ATR% — a genuine U-SHAPE (both extremes fade; the middle is dead). NOT a floor.**

| daily ATR% | n | win% | raw PF | avg% |
|---|---:|---:|---:|---:|
| <0.05 | 33 | 84.8 | **4.96** | +12.4 |
| 0.05–0.08 | 338 | 81.4 | **3.97** | +14.3 |
| 0.08–0.12 | 754 | 71.8 | **1.55** | +5.5 |
| 0.12–0.18 | 943 | 75.7 | 1.78 | +7.5 |
| 0.18–0.25 | 509 | 77.0 | **2.98** | +12.2 |
| ≥0.25 | 484 | 76.2 | 2.96 | +11.5 |

Two separate winning cohorts, opposite ends: **(a) LOW daily ATR% (<0.08) → PF 4–5** — a
normally-CALM daily name popping intraday to a new high on a huge spike = a rare, out-of-
character blow-off that reverses hardest (the best cells in the whole study, though <0.05 is
thin at 33 trips); **(b) HIGH daily ATR% (≥0.18) → PF ~3** — a chronically-jumpy name at its
extreme. **The middle (0.08–0.18) is DEAD (PF 1.5–1.8).** A floor sweep misses this (it can
only grab the high arm, discarding the even-better low arm). The profitable move is a
**CARVE-OUT: exclude the 0.08–0.18 mid-vol band, keep both tails** — daily ATR% is the first
parameter that is genuinely non-monotone this way. Contrasts with intraday ATR% (a clean
monotone floor); the two are different signals (daily = the name's baseline character; intraday
= today's chaos). *(Not yet locked — a carve-out, not a simple threshold.)*

**Why the LOW-daily-ATR cohort wins — it's DESPITE its intraday setup, not because of it
(daily-ATR is an independent normalizer).** Compared the intraday features across the three
buckets (all on the same baseline):

| daily-ATR bucket | n | PF | med 1d | med 1m | med bar_rvol | med i-ATR | med i-tight |
|---|---:|---:|---:|---:|---:|---:|---:|
| LOW <0.08 | 371 | **4.03** | 84% | 6.67 | **9.7** | 0.049 | 5.62 |
| MID (dead) | 1,697 | 1.67 | 81% | 6.55 | 11.2 | 0.047 | 5.92 |
| HIGH ≥0.18 | 993 | 2.97 | 77% | 6.76 | 11.1 | 0.047 | 6.05 |

**The intraday setup is FLAT across buckets** — 1m return, 20m, intraday-ATR, intraday-
tightness all ~identical — and the LOW bucket's is if anything marginally WEAKER (lowest
bar_rvol 9.7, lowest i-tight 5.62). So PF 4.03 comes with a slightly-below-average intraday
setup: the edge canNOT be a confound from a better 1m/rvol/tightness. **The daily-calm property
IS the signal.** Mechanism: daily ATR% is a NORMALIZER — for a quiet name, the SAME intraday
spike is a far more extreme, out-of-character event relative to that stock's normal behavior
(a genuine anomaly with no structural reason to continue → reverts hardest); for a chronically-
jumpy name a +80% day + 7% pop is business-as-usual (less informative). This is why a carve-out
is the right encoding — daily ATR% adds INDEPENDENT information (how surprising the identical
move is), it is not a proxy for the intraday features.

**AVERAGES (vs the medians above) — same conclusion, two extra wrinkles:**

| daily-ATR bucket | n | PF | avg_ret | avg_1d | avg_1m | avg_bar_rvol | avg_i-ATR | avg_i-tight |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| LOW <0.08 | 371 | **4.03** | +14.1% | 103% | 8.06 | **18.6** | 0.054 | 6.05 |
| MID (dead) | 1,697 | 1.67 | +6.6% | 99% | 7.72 | **22.5** | 0.051 | 6.14 |
| HIGH ≥0.18 | 993 | 2.97 | +11.9% | 93% | 7.81 | 20.4 | 0.051 | 6.32 |

The mean confirms it: LOW bucket's avg bar_rvol is the LOWEST (18.6 vs 22.5) — smaller volume
spikes, best PF — and avg 1m/20m/i-ATR/i-tight are flat. Two things the mean exposes that the
median hid: **(1) a fatter UPPER tail of 1d moves** — LOW-bucket 1d p90 = +181% (vs 158/151
for mid/high), the highest of the three: a calm name doesn't move +80% for no reason, so when
it does it's often a genuinely violent blow-off. **(2) `chg_3d` = nan for the LOW & HIGH
buckets but not MID** — some trips have no D−3 close (name listed <3 sessions before D). So the
LOW/HIGH cohorts carry FRESH/young listings the MID cohort lacks — another hint the low-daily-
ATR winners skew toward recent listings having their first blow-off (a low measured daily-ATR
partly reflects short/quiet history).

---

## Per-daily-ATR-bucket systems: rescuing the MID (dead) bucket with bar_rvol

Plan: treat the daily-ATR buckets as SEPARATE systems (LOW <0.08 is already PF 4.0 — leave it
alone). Focus on the big **MID bucket (daily-ATR 0.08–0.18, 1,697 trips, PF 1.67)** — the dead
zone, but the highest-capacity one. Swept each intraday lever one-at-a-time WITHIN the MID
bucket:

| lever (MID only) | knee | n | PF | vs base 1.67 |
|---|---|---:|---:|---|
| **bar_rvol ≥ 20** | — | 518 | **3.83** | ★ best |
| bar_rvol ≥ 12 | — | 801 | 2.97 | strong |
| 20m > 100% | thin | 50 | 7.11 | extreme only |
| 1m ≥ 15% | thin | 124 | 4.09 | extreme only |
| i-ATR ≥ 0.08 | thin | 118 | 2.60 | weakest, non-monotone |

**bar_rvol is the rescue lever — and essentially sufficient alone.** Stacking test in the MID
bucket: rvol≥12 + 20m>30 + 1m≥7 → PF 3.74 (288 trips), but **rvol≥20 ALONE → PF 3.83 (518
trips)** — beats the triple-stack with ~2× the capacity. The levers overlap so heavily (all
select "more extreme spike") that stacking mostly just cuts trips. i-ATR is the weakest and
non-monotone — skip it.

**This confirms the daily-ATR mechanism.** A mid-vol name CAN be faded profitably — you just
need a much more EXTREME volume spike (≥20× vs the ≥4 baseline) to flag a genuine exhaustion
event, because for a mid-vol name an ordinary spike isn't out-of-character enough. The LOW
(calm) bucket gets PF 4 "for free" (ANY spike is anomalous); the MID bucket reaches PF 3.8 but
only by DEMANDING an extreme spike. → **MID-bucket system = the baseline + bar_rvol ≥ 20.**
(LOW bucket keeps bar_rvol ≥ 4; per-bucket rvol floors are the natural design.)

---

## 20m & 1m — SIZING levers, not thresholds (rvol subsumes them)

Hypothesis (rvol so dominant that stacking 20m/1m on top barely helped → maybe they're better
OFF as gates, used only for sizing). Stripped BOTH from the baseline (kept 1d>50, bar_rvol≥4,
ATR%≥0.03 as the floor):

- **Stripped base (no 20m, no 1m): 3,563 trips, PF 2.01, +8.5% avg**
- **WITH 20m>10 & 1m>2 (prior baseline): 3,084 trips, PF 2.17, +9.3% avg**

The two floors bought only **+0.16 PF for a 13% trip cut** — nearly redundant as thresholds once
rvol≥4 + ATR≥0.03 are on. Confirms the hypothesis. Broken down on the stripped base:

**20m — no dead floor; a pure SIZING gradient (tail is huge):**

| 20m | n | PF | avg% |
|---|---:|---:|---:|
| 5–10% | 106 | 1.44 | +4.0 |
| 10–20% | 626 | 1.73 | +5.3 |
| 20–40% | 1,669 | 2.00 | +7.8 |
| 40–75% | 913 | 1.98 | +10.1 |
| **≥75%** | 190 | **3.27** | +18.7 |

**1m — one genuinely BAD floor (0–2%), then a clean sizing gradient:**

| 1m | n | PF | avg% |
|---|---:|---:|---:|
| 0–1% | 171 | 1.35 | +4.0 |
| **1–2%** | 202 | **1.21** | +2.4 |
| 2–4% | 615 | 2.17 | +7.7 |
| 7–12% | 1,007 | 2.15 | +9.6 |
| 12–20% | 367 | 2.68 | +13.4 |
| **≥20%** | 75 | **3.76** | +20.2 |

**Verdict:** **20m = pure SIZING lever, NO threshold** (no dead floor — even 5–10% is PF 1.44;
size up as it climbs, concentrate on the ≥75% tail at PF 3.27). **1m = SIZING lever + at most a
LIGHT ≥2% floor** (the 0–2% pocket is genuinely bad, PF 1.2–1.35 — a weak non-committal pop;
above 2% it's a pure gradient 2.17→2.68→3.76). Both tails are the violent/fast blow-offs — same
"bigger/faster move = harder fade" theme. Removing them as hard gates recovers ~480 trips at
−0.16 PF, to be more than repaid by sizing up the tails.

**LOCKED the 1m ≥ 2% floor. Re-broke-down 20m WITH it on** (base: 1d>50 & rvol≥4 & ATR≥0.03 &
1m≥2%, no 20m gate → 3,190 trips, PF 2.13):

| 20m | PF (no 1m floor) | PF (1m≥2%) |
|---|---:|---:|
| 5–10% | 1.44 | **1.26** (worse) |
| 10–20% | 1.73 | 1.98 |
| 40–75% | 1.98 | **2.36** (better) |
| ≥75% | 3.27 | 3.14 |

The 1m floor did NOT clean up the weak 5–10% 20m pocket (1.44→1.26) — so the low-1m and low-20m
weaknesses are DIFFERENT names (the 1m floor removes a different bad set). It DID sharpen the
mid-upper range (40–75%: 1.98→2.36) — among names with a real ≥2% 1m spike, a strong 20m run adds
more cleanly (mildly complementary in the mid-range). Tail unchanged (≥75% ≈ 3.1). **20m floor
sweep with 1m≥2% is still flat at the bottom** (any 2.13 → >20% 2.19) and only pays at the top
(>40% 2.50, >75% 3.14). **Conclusion holds: 20m stays a pure SIZING lever, no threshold** — the
1m floor confirmed 20m's value lives entirely in its upper tail.

By-year on 3d>50 & 1d>75 & 20m>10: **2017→2026 uniformly strong** (PF 1.1–2.7, 68–77%
win, positive every year, big samples — 2024: 600 trips, 2025: 622, 2023: 341 @ PF 2.46).
But **2003–2016 mostly dead or negative** (2009 PF 0.54 / −7.5% avg; 2010 0.26 / −14%;
2016 0.63 / −7.3%). Unlike the long (which was only float-DATA-bounded but worked
structurally earlier), **short-the-extended-pop appears to be a genuinely MODERN
phenomenon** — it lives in the low-float/meme era (2020+ especially). Real and large now,
but regime-dependent in a way the long is not. Size accordingly.

---

## 20m × rvol interaction — rvol≥20 largely SUBSUMES 20m

Re-broke-down 20m after raising bar_rvol to ≥20 (base: 1d>50 & rvol≥20 & ATR≥0.03 & 1m≥2% →
925 trips, PF **3.52** — the strong rvol floor alone nearly doubles the base):

| 20m | n | PF (rvol≥4) | PF (rvol≥20) |
|---|---:|---:|---:|
| 10–20% | 100 | 1.98 | **7.47** |
| 20–40% | 406 | 1.96 | **2.34** (dip) |
| 40–75% | 319 | 2.36 | 4.51 |
| ≥75% | 76 | 3.14 | 4.67 |

With rvol≥20, **20m loses its clean gradient** — the biggest bucket (20–40%, 406 trips) is the
WEAKEST (PF 2.34, below the 3.52 base) while 10–20% spikes to 7.47; jagged, non-monotone. Floor
sweep muddy: `>20%` actually LOWERS PF (3.51→3.30, cutting the strong 10–20% cell); only `>40%`
clearly helps (4.54). **rvol≥20 has absorbed most of 20m's edge** — the extreme volume spike
already encodes "violent blow-off," so 20m's increment is largely redundant and noisy at high
rvol. 20m's relative lift over base shrinks (rvol≥4: 20m≥75% ~1.5× base; rvol≥20: ~1.3×).
**Design implication: rvol is the dominant lever; 20m (and 1m) add most where rvol is MODEST.**
(The 20–40% dip is likely sampling noise — biggest bucket, no mechanism to be uniquely bad.)

---

## 1d breakdown — 1d is NOT the dominant lever after all (its floor sits in the DEAD MIDDLE)

Dropped the 1d floor, broke it down on the intraday base (rvol≥4 & ATR≥0.03 & 1m≥2%; 4,960
trips, PF 2.13):

| 1d | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| **<0% (down day)** | 44 | 77.3 | **3.10** | +8.9 |
| 0–10% | 67 | 71.6 | 2.33 | +6.3 |
| 10–25% | 283 | 79.9 | 2.52 | +6.7 |
| 25–50% | 1,370 | 73.4 | 2.04 | +5.8 |
| 50–75% | 1,388 | 75.4 | 1.99 | +7.0 |
| 75–100% | 797 | 76.5 | 1.91 | +8.2 |
| 100–150% | 639 | 74.8 | 2.04 | +9.9 |
| **150–300%** | 320 | 74.4 | **3.42** | +16.4 |
| **≥300%** | 46 | 82.6 | **4.67** | +23.5 |

**1d is a U-SHAPE, and the `≥50%` floor sits in the DEAD MIDDLE.** Floor sweep is FLAT from any
(2.13) to 1d≥75% (2.22) — **the current 1d≥50% gives PF 2.13, identical to NO floor** — and only
pays at the extreme (≥150% 3.57, ≥300% 4.67). The 25–100% band (the cells the ≥50% floor KEEPS)
is the WEAKEST (PF 1.9–2.0); the `<0%` down-day bucket is one of the BEST (PF 3.10).

**This OVERTURNS the earlier "1d is the dominant lever" conclusion** — that was an artifact of
sweeping 1d floors BEFORE the intraday gates (rvol/ATR/1m) existed. Now that the intraday signal
is in place, 1d≥50% adds nothing: a name popping to a new session high on a huge spike is an
exhaustion fade **whether or not it's net-up on the day** (even net-RED days fade, PF 3.10). Same
pattern as 3d/7d — **the multi-day/day windows are NOT floor signals; the INTRADAY signal
(rvol/ATR/1m) carries the edge.** 1d is really another SIZING lever (weight the ≥150% parabolic
tail; low-1d and down-day buckets are fine, not dead). → the working baseline's 1d>50 is
essentially INERT given the intraday gates and can likely be dropped/lightened.

**1d breakdown WITH rvol≥20** (base: rvol≥20 & ATR≥0.03 & 1m≥2%, 1d dropped → 1,193 trips, PF
**3.51**): rvol≥20 lifts EVERY 1d bucket to PF 2.7+ (even the dead 75–100% middle: 1.91→5.18) —
it rescues the whole 1d spectrum. Floor sweep FLAT again (any 3.50 → 1d≥50% 3.51 → 1d≥75% 3.97):
**1d≥50% adds nothing on top of rvol≥20.** The low-1d buckets nearly vanish (down-day = 1 trip,
10–25% = 14) — demanding rvol≥20 mechanically selects big-1d names (a 20× spike ≈ a large daily
move), so 1d has little independent variation left. **⚠ The ≥300% cell shows PF "1812" — a
thin-sample denominator artifact** (17 trips, near-zero summed losses; raw PF has no cap — shorts
are +100%-bounded so not unbounded, just tiny-denominator). Ignore the number; the cell is
"good, tiny." **Consistent with 20m×rvol: rvol≥20 SUBSUMES 1d too** — rvol is the primary gate,
1d/20m/1m are sizing levers whose independent value shrinks as rvol rises.

**1m breakdown WITH rvol≥20 — the 2% floor stops helping (it's rvol-conditional).** Base rvol≥20
& ATR≥0.03, 1m dropped → 1,350 trips, PF 3.52. The sub-2% pocket, DEAD at rvol≥4 (PF 1.2–1.35),
is fully ALIVE at rvol≥20:

| 1m | PF (rvol≥4) | PF (rvol≥20) |
|---|---:|---:|
| 0–1% | 1.35 | 2.68 |
| 1–2% | 1.21 | **5.23** |
| ≥20% | 3.76 | 15.3 |

Direct A/B at rvol≥20: no-1m-floor 3.52 (1,350) vs 1m≥2% 3.51 (1,193) — **the floor is
IDENTICAL, just −157 trips.** Sub-2% group = PF 3.65 (82% win), BETTER than ≥2% group (3.51).
**Why (subsumption again):** at rvol≥4 a sub-2% candle = a weak non-committal pop (low edge); at
rvol≥20 a sub-2% candle with a 20× volume spike = a **massive-volume bar that barely moved price
= heavy ABSORPTION / a stuffed top** (sellers capping the pop) — arguably a CLEANER exhaustion
signal (volume shows the fight, the small candle shows sellers won). **Design: the 1m≥2% floor
is rvol-CONDITIONAL** — keep it in the low-rvol book (LOW/HIGH daily-ATR buckets, rvol≥4), DROP
it in the high-rvol book (MID bucket, rvol≥20). Above 2%, 1m stays a clean sizing gradient in
both regimes.

## Daily-ATR% × rvol — SUBSTITUTES, not complements (the U-shape dies at rvol≥20)

Re-ran the daily-ATR% breakdown WITH rvol≥20 (no 1m floor, no 1d; 1,348 trips, PF 3.51). **The
U-shape does NOT survive — the daily-ATR% signal collapses and the MID (dead) bucket becomes the
BEST:**

| daily-ATR bucket | PF (rvol≥4) | PF (rvol≥20) |
|---|---:|---:|
| LOW <0.08 | **4.03** | 3.73 |
| MID 0.08–0.18 | **1.67** (dead) | **4.11** (now BEST) |
| HIGH ≥0.18 | 2.97 | 2.75 |

At rvol≥4 the story was "LOW & HIGH win, MID dead" (a U). At rvol≥20 it's roughly the OPPOSITE —
MID 4.11 > LOW 3.73 > HIGH 2.75, monotone-ish decreasing; the U is gone. (The <0.05 extreme-calm
cell flips to PF 0.78, but 14 trips = thin-sample noise.)

**daily-ATR% and rvol are SUBSTITUTES, not complements.** daily-ATR% was valuable at rvol≥4 as a
NORMALIZER — telling you how out-of-character an ORDINARY spike was. Once you demand rvol≥20 the
spike is already extreme in absolute terms, so you no longer need daily-ATR% to flag it as
anomalous — it's subsumed. → **These are DISTINCT, mutually-exclusive setups, not stackable
filters:**
- **Setup A (high-rvol):** rvol≥20, IGNORE daily-ATR% → PF ~3.5 across the board (MID best); no
  1m floor needed (absorption re-interpretation).
- **Setup B (calm-name):** daily-ATR% <0.08 with only rvol≥4 → PF ~4 (the ordinary spike is
  anomalous BECAUSE the name is normally calm).

This is the cleanest statement of the emerging structure: rvol is the master feature, and every
other signal (1d, 20m, 1m-floor, daily-ATR%) is either subsumed by high rvol or acts as a
low-rvol substitute for it.

## Inside the extreme-1d (>150%) cohort — rvol still mandatory; 1m floor NOT

Question: for the parabolic >150%-day names, is the volume spike still needed, or does the
extreme extension make ANY breakout fade? Broke the >150% cohort (719 trips, ATR≥0.03 only, no
rvol/1m floor; base PF 2.54, median rvol 4.6) down by each:

**rvol — STILL the dominant lever, NOT redundant:**

| rvol | n | PF | avg% |
|---|---:|---:|---:|
| <4 | 314 | **1.76** | +9.5 |
| 4–8 | 161 | 3.01 | +14.2 |
| 12–20 | 68 | 6.07 | +24.5 |
| ≥40 | 52 | **17.3** | +27.2 |

Floor sweep: any 2.54 → rvol≥4 3.75 → ≥12 6.19 → ≥40 17.3. **The `<4` bucket (44% of the cohort)
is the WEAK one (PF 1.76)** — even a name up >150% on the day is a mediocre fade on a LOW-volume
breakout. **The extension does NOT substitute for rvol** — you still need the volume spike to
confirm exhaustion. rvol is the one UNIVERSAL gate.

**1m floor — NOT needed here (flat):** floor sweep any 2.54 → 1m≥2% 2.53 → ≥7% 2.56. The 0–2%
pocket is PF 2.69 (fine, not dead); the 2–4% cell is the weak one (1.69, non-monotone noise).
Same logic as high-rvol: a name already +150% is by definition in an extreme state, so a small
entry candle isn't "non-committal" — the extension itself did the "real event" filtering the 1m
floor does in the ordinary book.

**Synthesis — the 1m floor is DOUBLY conditional, rvol is universal:** drop the 1m≥2% floor when
rvol≥20 OR when 1d>150% (either extreme makes a small candle a stuffed-top, not a weak pop). Keep
it ONLY in the ordinary/low-rvol, moderate-extension book. **rvol stays mandatory in EVERY regime
— it's the single non-negotiable gate.**

## Multi-day EXTREMES (3d>500, 7d>1000) — NOT A+ like 1d>150; 7d inverts to NEGATIVE

Tested whether the extreme MULTI-DAY windows replicate the 1d>150% A+ tier. They do NOT.

**3d floor sweep (ATR≥0.03 base):** plateaus ~PF 2.2 at 300%, then DIPS — 3d>300 2.22, 3d>500
**1.77**, 3d>800 2.51 (26 trips). No clean escalation; 3d>500% does NOT replicate 1d>150.

**7d floor sweep — INVERTS at the extreme:** 7d>300 2.41 → 7d>500 1.82 → **7d>800 PF 0.85
(−2.0% avg)** → **7d>1200 PF 0.44 (−11.6% avg, 49% win).** The extreme 7-day runners are the
WORST fades in the whole study — shorting a name up +1000%/week is catching a freight train
(a still-running parabolic, not an exhaustion candidate).

**rvol cross on the extremes** (does volume rescue them?): 3d>500 × rvol≥4 → 2.40 (mild, sample-
thin); **7d>1000 × rvol≥12 → PF 3.42 (23 trips)** — even the disaster cohort is salvageable, but
ONLY with a strong spike, tiny sample.

**Synthesis — 1d vs multi-day extremes are FUNDAMENTALLY different:**
- **1d>150% is A+** because a SINGLE-day +150% is almost always a climactic intraday blow-off —
  it exhausts TODAY, and rvol confirms it fades today (our MOC horizon).
- **3d/7d extremes are NOT** because a multi-day +500–1000% run is a SUSTAINED parabolic — the
  exhaustion isn't localized to today; shorting it intraday fights an ongoing trend. 7d>800% is
  outright NEGATIVE (the runaway monsters).
- **This is why rvol matters even MORE at multi-day extremes:** without a huge SAME-DAY spike,
  an extreme multi-day runner is just still running. rvol is what localizes the blow-off to
  "happening NOW, today" vs "been going up all week, not done." 1d's extension implies a today-
  event; 7d's does not. **Confirms: 1d is the right extension window for the A+ tier; the
  multi-day windows are non-levers and DANGEROUS at their extremes without rvol.**

## The rvol ≥ 40 "S bucket" standalone — PF 4.4, robust every modern year

rvol≥40 across the WHOLE book (ATR≥0.03 only, NO 1d/1m/other filter): **533 trips, PF 4.37, 80%
win, +15.2% avg, +$810k @ $10k/trade** (median 1d 75% — 40× spikes naturally coincide with big
days, but no 1d floor applied). The rvol ladder is a clean monotone escalation with NO ceiling:

| rvol ≥ | n | win% | PF | avg% | net@10k |
|---|---:|---:|---:|---:|---:|
| 4 | 5,767 | 74.9 | 2.04 | +7.5 | $4.33M |
| **12 (A+)** | 2,260 | 77.9 | 3.05 | +11.2 | $2.52M |
| 20 | 1,350 | 79.1 | 3.52 | +12.4 | $1.68M |
| **40 (S)** | 533 | 79.9 | 4.37 | +15.2 | $810k |
| 60 | 291 | 85.2 | 6.55 | +18.3 | $533k |
| **100 (SS)** | 98 | 85.7 | 8.78 | +20.8 | $204k |

**By-year: robust, NOT era-concentrated** — every year 2020–2026 is PF ≥ 3.6 (2023 7.58, 2021
5.37, 2026 4.75), no losing year, and the sample is GROWING (2024: 123, 2025: 198). Modern-era-
only like the whole short book, but rock-solid within it.

**Does rvol≥40 still need 1d extension? Adds on top, not required:** 1d 10–50% → PF 2.03 (the
weak cell but still solidly +), 50–100% → 4.04, 100–150% → 5.71, **≥150% → 17.5** (53 trips —
the apex, intersection of the two A+ signals). So rvol≥40 is good on ANY 1d, better with
extension. **rvol≥40 alone is a complete standalone S-tier system** — doesn't require any other
filter. Tiering: rvol≥12 = A+, rvol≥40 = S, rvol≥100 = SS; rvol≥40 × 1d≥150 = the apex cell.

## Lifting the soft cell (rvol≥40 × 1d[10–50%]) — the 1m floor comes BACK

The one soft cell in the rvol≥40 book is low-1d (10–50%): 91 trips, PF 2.03. Conditioning:

**1m return LIFTS it (20m does NOT):**

| 1m | n | PF | avg% |
|---|---:|---:|---:|
| 0–2% | 22 | **1.20** | +2.1 |
| 2–4% | 30 | 1.49 | +3.0 |
| 4–7% | 28 | **3.61** | +9.8 |
| 7–12% | 10 | **6.66** | +13.8 |

Floor sweep: **1m≥4% → PF 4.25** (39 trips) — roughly DOUBLES the cell (2.03→4.25), in line with
the rest of the rvol≥40 book. **20m is useless here** — non-monotone, the median 20–40% bucket is
the WEAKEST (1.43), floors go the wrong way.

**The 1m floor's relevance is conditional on EXTENSION, not just rvol.** General rule was "high
rvol kills the 1m floor" (a sub-2% candle = absorption) — but that held for EXTENDED names. In
the low-1d (10–50%) × high-rvol cell the 1m floor COMES BACK: sub-2% is dead (1.20), need 1m≥4%.
Mechanism: for a name only up 10–50% (not extended), a 40× spike on a TINY candle isn't a
stuffed-top climax — more likely a name just GETTING GOING (early accumulation, not exhaustion).
The big 1m candle is what confirms a violent pop being faded vs the start of a run. So the 1m
floor is doubly-conditional the OTHER way too: drop it when rvol≥20 OR 1d>150 (extreme → any
candle is a stuffed-top), but KEEP it (≥4%) when the name is UN-extended even at high rvol.

## 1m across the rvol≥40 1d sub-buckets — the floor is a TARGETED patch for un-extended only

Ran the 1m≥4% A/B in each 1d sub-bucket of the rvol≥40 book:

| 1d band | n | PF (all) | PF (1m≥4%) | verdict |
|---|---:|---:|---:|---|
| **10–50%** | 91 | 2.02 | **4.24** | 1m FIXES it (2×) |
| 50–100% | 294 | 4.04 | 3.80 | 1m slightly HURTS |
| 100–150% | 92 | 5.71 | 5.87 | flat |
| ≥150% | 53 | 17.5 | 16.2 | flat/slightly hurts |

**1m≥4% only helps the un-extended 10–50% cell; in the extended buckets it's flat-to-negative.**
And the sub-2% pocket in the extended buckets is FINE-to-EXCELLENT: 50–100% 1m<2% → PF 2.66;
**100–150% 1m<2% → PF 10.5** (7 trips — a 40× spike + a tiny candle on a +100–150% name = a
textbook STUFFED TOP, absorption at its purest). (Per-bucket PFs in the extended bands are jumpy
from small n=5–97; the A/B floor sweep is the reliable read.)

**Final 1m rule (fully mapped):** the 1m≥4% floor is a TARGETED patch, conditional on extension —
apply it ONLY to the un-extended (1d 10–50%) cell (lifts 2.0→4.2); DROP it once 1d≥50% (extension
guarantees exhaustion, and the sub-2% absorption candles are among the best). **20m: DITCHED** —
useless in every rvol≥40 cell checked.

## Below 1d<10% — the cell is nearly EMPTY at high rvol (rvol ⟂ 1d are correlated)

Can a 1m floor help the 1d<10% region? **Can't meaningfully test it — the cell barely exists at
high rvol:**

| rvol floor | n (1d<10%) | PF |
|---|---:|---:|
| ≥4 | 134 | 2.73 |
| ≥12 | 13 | (noise) |
| ≥20 | 4 | — |
| ≥40 | 3 | — |

**Structural finding: rvol and 1d are strongly CORRELATED, not independent.** A 12–40× volume
spike essentially NEVER occurs on a name flat/down on the day — the volume IS what drives the
price, so a huge spike ⇒ a big 1d move. The "low-1d × high-rvol" combination is nearly empty
(3–13 trips), so its 1m breakdown is uninformative (2–5 trips/bucket, NULL PFs from zero losses).

At the rvol where low-1d trades DO exist (≥4): **1d<10% → 134 trips, PF 2.73** — solidly
positive (better than the rvol≥4 book's 2.04), so low-1d is NOT a dead zone at moderate rvol and
needs no 1m rescue (unlike the 10–50% cell). **Why 10–50% was special:** it's the one place a big
volume spike coincides with only-MODERATE extension — the ambiguous "getting going vs blowing off"
zone the 1m candle disambiguates. Below 10%, that combination doesn't occur; above 50%, extension
already implies exhaustion. The 1m≥4% patch is thus needed in a narrow band (moderate 1d, high
rvol) and nowhere else.

## What bar_rvol actually MEANS — the opening-15m volume shape (why 4× is weak)

`bar_rvol_15m = breakout_bar_vol / MEAN(1m vol over [9:30,9:45))`. But the MEAN is inflated by
the opening bar, so "4× the mean" is deceptively small. Measured the opening-15m 1m-volume shape
(7,314 sampled name-days from the short book):

- **max/mean ratio:** median **2.75** (p25 2.26, p75 3.41, p90 4.2, mean 2.95) — the 15 bars are
  within ~3× of each other; even at p90 the busiest bar is only 4.2× the mean. A fairly TIGHT
  distribution, not wildly skewed.
- **opening bar (9:30):** is the max only **42%** of the time (most-often but not usually the
  biggest — the tape isn't always front-loaded), and is a median **2.0× the mean.**

**Translating the rvol thresholds into "multiples of the busiest opening bar":**

| rvol (× mean) | × 15m-max | × opening-bar | tier |
|---|---:|---:|---|
| 4 | 1.5 | 2.0 | weak — barely above the open |
| 12 | 4.4 | ~6 | A+ |
| 40 | 14.6 | 19.9 | S |

**A "4× rvol" breakout bar is only ~1.5× the busiest opening bar** — barely out of the opening
flurry, which is exactly why rvol≥4 is only PF ~2.0. The real exhaustion signal doesn't kick in
until the breakout bar DWARFS the whole opening range: 12× mean = ~4.4× the 15m-max (A+), 40×
mean = ~15× the 15m-max = a true out-of-scale CLIMAX bar (S, PF 4.4). **Strengthens the case for
the higher rvol floors** — the weak tier is weak because a 4× bar is just "opening-bar sized," not
a genuine spike.

## Entry-time (30m buckets) — the short is ROBUST ALL DAY (opposite of the long)

Broke the short (rvol≥40, ATR≥0.03, no 1d floor; 533 trips) down by 30m entry bucket:

| entry | n | win% | raw PF | avg% |
|---|---:|---:|---:|---:|
| 09:30 | 25 | 92.0 | 14.7 | +19.6 |
| 10:00 | 114 | 78.1 | 4.40 | +14.2 |
| 10:30 | 93 | 79.6 | 5.02 | +16.3 |
| 11:00–11:30 | 116 | ~78 | 4.7 | +16 |
| **12:00** | 75 | 89.3 | 9.63 | +20.1 |
| 13:00 | 19 | 94.7 | 140* | +16.9 |
| **14:00** | 15 | 66.7 | **0.96** | −0.9 |
| 15:00–15:30 | 22 | ~73 | 2.8–7.8 | +8 |

**Strong across the WHOLE session** — avg return +14–20% almost everywhere, and midday (12:00 →
PF 9.6, +20% avg) is among the BEST. The one soft spot is 14:00 (PF 0.96, the only losing bucket,
15 trips — likely noise). (13:00 PF 140 is a thin-sample artifact, 19 trips near-zero losses.)
**Mechanism / contrast with the long:** an exhaustion blow-off reverses FAST regardless of the
clock, so the short doesn't need the morning — a 12:00 or 15:00 parabolic fades as hard as a 10:00
one. The LONG fade, by contrast, needs TIME for the bounce before MOC, so it's morning-concentrated
(70% of trips 9:30–11:00). **Practical: run the short ALL session (maybe ex-14:00); time-gate the
long to the morning.**

---

## BREADTH — NOT a lever for the short (does NOT mirror the long; regime-independent)

Question (user): does high market breadth improve the short like it does the long MR system?
Thesis: high breadth = bullish tape = more risk-seeking participants = more/better parabolic
blow-offs to fade → expect high-breadth to help. **It does NOT.** Breadth = `pct_above_20`
(fraction of the CS/ADRC liquid universe above its 20d MA), no-lookahead `LAG(pct_above_20) OVER
(ORDER BY date)` — the same series & convention the long production gate uses (breadth≥0.65).
Ran on the ATR%≥0.03 base at each rvol tier. Coverage 100%. (`lowflyer_short_breadth.sql`.)

**rvol≥40 (S) — flat; NO breadth floor helps:**

| breadth | n | win% | raw PF | avg% |
|---|---:|---:|---:|---:|
| 0.00–0.35 (bearish) | 631 | 81.0 | **3.80** | +13.8 |
| 0.35–0.50 | 775 | 78.3 | **2.61** (dip) | +11.0 |
| 0.50–0.65 | 835 | 79.8 | 3.81 | +12.6 |
| 0.65–0.80 | 879 | 78.5 | 3.12 | +12.5 |
| 0.80–1.00 (bullish) | 153 | 76.5 | 3.89 | +12.2 |

Floor sweep FLAT: any 3.26 → ≥0.50 3.45 → ≥0.65 3.21 → ≥0.80 3.89 (only 153 trips). The ≥0.80
cell is nominally best but tiny; no monotone gradient, no usable floor.

**rvol≥12 (A+) and rvol≥4 (whole book) — same W-shape, higher capacity:**

| breadth | rvol≥12 PF | rvol≥4 PF |
|---|---:|---:|
| <0.35 (bearish) | 3.44 | 2.94 |
| **0.35–0.50** | **2.09** | **1.89** ← weak cell |
| 0.50–0.65 | 3.42 | 3.22 |
| 0.65–0.80 | 2.72 | 2.64 |
| >0.80 (bullish) | 3.88 | 3.33 |

**Two findings, both against the thesis:**
1. **The short works in BEARISH markets too** — the <0.35 bucket is one of the BEST (PF 2.9–3.8),
   not the worst. Broadly positive across modern years (2022 PF 1.99, 2024 3.52, 2025 2.72; not a
   crash artifact). An over-extended parabolic blows off regardless of the broad tape.
2. **The only soft regime is the "mushy middle" (0.35–0.50, slightly-below-neutral): PF ~1.9–2.6.**
   By-year (rvol≥12) it's a genuine soft spot — dragged by 2018 (PF 0.43) & 2024 (1.4) among
   otherwise-fine years — noisy, not structural, and NOT worth a gate.

**Why it does NOT mirror the long:** the long (fade the flush → bounce) is a bet on dip-buyers
showing up, which is a risk-appetite / regime-sensitive behaviour → high breadth helps it. The
short (fade the pop → exhaustion) is a bet on a specific overextended-name's blow-off mechanics
(rvol/ATR/1m), which fire the SAME whether the market is greedy or fearful. **The short's edge is
intrinsic to the setup, not the regime.** → **Leave breadth OUT of the short spec.** (If anything,
one could exclude the 0.35–0.50 band, but it's marginal and costs a big chunk of capacity.)

---

## 15m-rvol vs 20d-rvol baseline — they measure DIFFERENT things; brv20d is a false-positive FILTER

Question (user): the 15m baseline (`bar_rvol_15m` = breakout_bar_vol / MEAN(1m vol over
[9:30,9:45))) works great, but is it unstable when the premarket/first-15m volume is abnormal? A
20-DAY baseline might be steadier. Defined **`bar_rvol_20d`** = breakout_bar_vol / (avgvol20 ·
adj_ratio / 390) — the breakout bar vs the average 1m volume implied by the 20-day ADJUSTED daily
avg (volume adjusts inversely to price on splits → raw-equiv = avgvol20·adj_ratio; ÷390 RTH mins).
And **`open15_vs_20d`** = (opening-15m 1m tempo) / (20d per-min baseline) = the "is today's open
abnormal" gauge. (`lowflyer_short_rvol_baseline.sql`.)

**They are nearly UNCORRELATED (corr of logs = 0.14) — genuinely different signals.** Median
`open15_vs_20d` = **9.2×**: these popping names open running ~9× their own 20-day tempo (names
already in play at the bell), so the 15m baseline is itself an elevated, today-specific number
while the 20d baseline is a slow, name-stable one.

**Head-to-head floor ladders — brv15 is the SHARPER knife:**

| floor | brv15: n / PF | brv20d: n / PF |
|---|---|---|
| ≥12 | 2,260 / **3.05** | 8,193 / 1.88 |
| ≥40 | 533 / **4.37** | 6,108 / 2.47 |
| ≥100 | 98 / **8.78** | 2,760 / 6.65 |

brv15 concentrates the edge into a tight high-PF tail; brv20d barely moves PF until its extreme
(≥100) and stays high-capacity/low-PF below it. **As a standalone gate, brv15 wins decisively.**

**The 2×2 is the real finding — they're COMPLEMENTARY, and brv20d removes brv15's false positives:**

| | b20<12 | b20≥12 |
|---|---:|---:|
| **b15<12** | PF 0.98 (410) | PF 1.60 (5,974) |
| **b15≥12** | **PF 1.19 (41)** | **PF 3.11 (2,219)** |

At the 40× tier, sharper: b15≥40 & b20≥40 → **PF 6.16 (454)**; b15<40 & b20<40 → **PF 0.90 (loser,
2,457)**; the single-agree corners weak (1.18 / 2.33).

**The `b15≥12 & b20<12` false-positive cell (PF 1.19, 41 trips) has median open15_vs_20d = 0.44 — a
LIGHT open.** This is EXACTLY the user's worry, confirmed: when the first 15m is unusually quiet the
15m baseline is DEFLATED, so a merely-moderate bar shows a fake-big brv15 (median 19.3×) that against
the 20d baseline is only 8.9× — not a real exhaustion spike. The agreement cell (PF 3.11) has median
open = 2.92 (normal/heavy) and both fire. **brv20d catches the light-open fakes brv15 alone cannot.**

**Actionable A/B — brv20d as a confirmation filter on the S bucket:**

| gate | n | raw PF | avg% |
|---|---:|---:|---:|
| brv15 ≥ 40 (current S) | 533 | 4.37 | +15.2 |
| **brv15 ≥ 40 & brv20d ≥ 40** | 454 | **6.16** | +17.5 |

**+1.8 PF for a 15% capacity cut**, purely by dropping 79 light-open false positives.

**FIRST-PASS verdict (REVISED below):** initially read brv20d as a light-open FILTER on brv15. That
UNDERSOLD it — the deep dive shows brv20d is the stronger PRIMARY lever and should REPLACE brv15≥40 as
the default.

## brv20d IS THE MAIN LEVER — replaces brv15≥40 as the default (5× capacity, ~1.5× PF, new population)

`brv20d ≥ 100` = **2,760 trips, PF 6.65, 88.7% win, +17.3% avg** vs the current default `brv15 ≥ 40` =
533 trips, PF 4.37 — **~5× the capacity AND ~1.5× the PF simultaneously.** (`lowflyer_short_brv20d.sql`.)

**brv20d has a hard KNEE at ~100 — it's threshold-like, not the gentle gradient brv15 is:**

| brv20d band (non-cum) | n | win% | raw PF | avg% |
|---|---:|---:|---:|---:|
| 0–40 | 2,536 | 63.1 | **0.91** (LOSER) | −1.0 |
| 40–100 | 3,348 | 67.6 | 1.35 | +3.2 |
| **100–200** | 2,128 | 86.5 | **5.16** | +15.6 |
| 200–400 | 578 | 96.4 | 28.3* | +23.1 |
| 400–800 | 47 | 95.7 | 188* | +22.2 |

Below ~40 there is NO edge (a net loser); the knee at 100 is a sharp regime boundary — a breakout bar
≥100× the name's average per-minute volume is a genuinely out-of-scale CLIMAX bar. *(The 28/125/188 PFs
above 200 are thin-DENOMINATOR artifacts — 96–100% win = almost no losing trades → near-zero denom;
raw PF has no cap. The HONEST metric up there is win% + avg%, both rock-solid. Treat ≥200 as "excellent,
small.")*

**It's genuinely NEW capacity, not a bigger brv15.** Of brv20d≥100's 2,760 trips only **226 overlap**
with brv15≥40; the other **2,534 are new** and strong standalone (PF 6.12, +16.6% avg). Conversely the
307 trips brv15≥40 catches that brv20d≥100 MISSES are much weaker (+7.4% avg). **brv15≥40 was largely
catching the WRONG thing** (light-open-inflated bars); brv20d≥100 selects a different, better
population — genuine climax bars measured against a stable name-level baseline.

**brv20d SUBSUMES 1d entirely** (brv20d≥100 base): every 1d band is strong — even down-day (PF 18.5, 16
trips) and 0–10% (+21.9% avg). 1d floor sweep DEAD FLAT: any 6.65 → 1d≥50% 5.91 → 1d≥150% 7.17. **1d
adds nothing on top** (cleaner subsumption than rvol showed). Same for the other levers — the single
out-of-scale-volume gate does the work.

**By-year: robust, NOT era-locked, and the setup gets DRAMATICALLY MORE FREQUENT in the last decade** —
the high-PF setup was always +EV but rare pre-2015 (~1–11 trips/yr), then explodes in frequency from
2020 on while STAYING high-PF. This is the key robustness table (brv20d≥100, ATR%≥0.03):

| yr | n | win% | raw PF | avg% |
|---|---:|---:|---:|---:|
| 2005 | 10 | 90.0 | 31.0* | +18.0 |
| 2008 | 5 | 60.0 | 1.18 | +1.3 |
| 2009 | 11 | 90.9 | 3.75 | +9.5 |
| 2010 | 9 | 66.7 | 8.70 | +8.1 |
| 2012 | 11 | 72.7 | 4.86 | +9.4 |
| 2013 | 8 | 62.5 | 2.32 | +7.8 |
| 2014 | 11 | 90.9 | 3.87 | +10.3 |
| 2015 | 34 | 88.2 | 13.6 | +15.6 |
| 2016 | 55 | 83.6 | 5.07 | +14.6 |
| 2017 | 90 | 81.1 | 2.75 | +9.5 |
| 2018 | 84 | 83.3 | 3.72 | +14.6 |
| 2019 | 97 | 80.4 | 2.96 | +11.6 |
| **2020** | **283** | 92.2 | 14.4 | +20.5 |
| 2021 | 231 | 97.4 | 141* | +21.1 |
| 2022 | 202 | 90.6 | 14.4 | +17.8 |
| **2023** | **360** | 88.6 | 7.74 | +17.9 |
| **2024** | **539** | 87.8 | 3.15 | +14.4 |
| **2025** | **523** | 89.5 | 14.2 | +20.2 |
| 2026† | 190 | 86.8 | 9.57 | +18.1 |

**Frequency growth is the headline:** ~1–11 trips/yr through 2014 → 34/55/90/84/97 (2015–19) → **283 → 360
→ 539 → 523 (2020–25)**, a ~50× rise in setup count per year while PF stays ≥2.3 and avg% stays +14–20%.
The low-float/meme era manufactures these out-of-scale-volume pop blow-offs at an accelerating rate — the
book is not just holding up, its CAPACITY is compounding. Positive every year with a real sample; softest
2017 (2.75), 2024 (3.15); best 2020/2022 (14.4), 2025 (14.2). (*2005 PF 31 & 2021 PF 141 are thin-loss-
denominator artifacts — trust the win%/avg%, not the ratio. †2026 partial, through the data cutoff.)

**Liquidity is fine (tradeable):** median entry $10.15, p10 $2.05, only 10 sub-$1 of 2,760; median ADV
$3.9M (p25 $1.5M). Real names, not penny-stock artifacts.

**⚠ The squeeze TAIL is real and is a SIZING/loss-study problem, not a PF one.** ret_moc dist (brv20d≥100):
median +19.4%, p25 already +9.6%, p10 −1.2% — an extraordinary distribution. BUT worst = **−839%** (a
short run over ~9×) and **90 trips (~3%) lost >20%.** The high PF is DESPITE these (winners dominate),
but a single −839% is a portfolio event. This is exactly the deferred big-LOSS study / catastrophe-stop
question — the fat left tail of shorting parabolas. Size for it; do not let one squeeze wipe the book.

**Why brv20d > brv15 mechanistically:** brv15's denominator (the name's own opening-15m tempo) is
itself an elevated, today-specific, noisy number — a light open deflates it into false spikes, a heavy
open masks real ones. brv20d's denominator (the 20-day daily avg ÷ 390) is a STABLE, name-level
baseline, so "100× the normal per-minute volume" means the same thing across names and days. The stable
baseline is what gives the clean knee and the honest, high-capacity edge. **→ NEW DEFAULT SHORT GATE:
brv20d ≥ 100 (+ ATR% ≥ 0.03), PF 6.65 / 2,760 trips.** (brv15 retired to a secondary/confirmation role.)

---

## Working baseline (this session) & next

**Working baseline (evolved this session):** 1d > +50%, **1m return ≥ +2%**, **bar_rvol_15m ≥
4**, **ATR% ≥ 0.03** (3d/7d DROPPED — non-levers; 20m DROPPED as a gate — sizing lever only).
→ **3,190 trips, PF 2.13, 76% win.** Per-daily-ATR-bucket design: LOW (<0.08) rvol≥4 → PF 4.0;
MID (0.08–0.18) rvol≥20 → PF 3.8; HIGH (≥0.18) rvol≥4 → PF ~3.0 (untuned). 20m & 1m-above-2%
are SIZING levers (concentrate weight on the violent/fast tails), not thresholds.

**Params still to examine (user-driven, one at a time):** 1d breakdown (the true driver — next),
finer bar_rvol per bucket, HIGH-bucket rescue, the sizing scheme itself, whether pre-2017 is
salvageable, ADV. Reproducibility: `scripts/equity/lowflyer_short_ramp.sql`.
