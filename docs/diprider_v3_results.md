# DipRiderV3 — pure trailing-window momentum

**Project:** `TradingEdge.DipRiderV3` (forked from `TradingEdge.DipRider` on branch `dip-rider`, 2026-07-08).
Long-only intraday momentum. The V2 run-tracking machinery (above-9EMA run reset, `savedRun*` stats,
buy-into-run) is **deleted**: choosing the run length N was the same overfitting trap as choosing an EMA
length. V3 replaces "consecutive run length" with reset-free **trailing-window features** over a fixed
20-bar window.

## Design

**Features** (all strictly-prior, no-lookahead snapshots at entry):

| Feature | Object | Baseline gate |
|---|---|---|
| 20m OLS log-price slope | `OlsSlopeMa(20)` on log-close | `> 0` |
| 20m OLS log-volume slope | `OlsSlopeMa(20)` on log-volume | `>= 0.05` (V2 F14/F15, the PF lever) |
| 20m log-ATR | `AvgMa(20)` of log-TR (`atrLog`) | recorded |
| 20m tightness | `(rangeHigh−rangeLow) / atrLin` (**linear** ATR denom) | `>= 3.0` |
| slope / log-ATR ratio | price-slope ÷ log-ATR | recorded → gate after breakdown |
| closes-above-9EMA, 6m | `SumMa(6)` of `(close ≥ EMA ? 1 : 0)` | `>= 5` but **DISABLED** at baseline |
| closes-above-9EMA, 40/60m | `SumMa(40)`, `SumMa(60)` | cap (trend-too-long) — off, tune later |
| session-max 20m log-ATR | running max of `atrLog.State` | recorded → gate after breakdown |
| session min/max close | running extremes (from 08:30) | recorded |
| session max volume | running max (from 08:30) | recorded |
| session VWAP | Σtp·v/Σv (from 09:30) | recorded |
| 15m initial volume | Σ of the first 15 **valid** feature-bars | recorded |

**Two-anchor timing** (fixes V2's accidental 09:00 VWAP anchor — `SessionStartMin` was `9*60=540`=09:00,
comment wrongly said 09:30):
- `SessionStartMin = 510` (08:30 ET) — the emitter delivers bars from here; **session min/max close +
  session max volume** fold from the first valid bar.
- `FeatureStartMin = 570` (09:30 ET) — **every other feature** (VWAP, both OLS slopes, ATR, tightness,
  EMA, the SumMa above-EMA counts, 15m-init-vol) folds only once `bar.etMin >= FeatureStartMin`.

**Universal valid-bar gate:** a bar enters a rolling window ONLY if `close > 0 && volume > 0`.
Illiquid/halt/print-gap bars are skipped for **every** feature. The 15m-initial-volume counts the first 15
**valid** feature-bars (not the clock 09:30–09:45 window) — a direct consequence.

**Stop:** geometry stop, `d = entry − floor`; `stop = entry − d·(2/3)`, close-based. (Same geometry as
VwapReclaim F14 / V2 F25.) The floor is the **session min close (from 08:30)** by default (F2 — beats the
tighter 20m-min-close floor on win-rate and PF). `--stop-floor-20m` reverts to the 20m-min-close floor.
Hold-to-MOC (the continuation lesson: any exit caps the winners — V1 F3, VwapReclaim F13, V2 F4/F23/F26).

**Defaults:** morning window 10:00–13:30, `MaxConcurrent = 1` (V2 F29 — later same-day adds have worse EV),
9-EMA, 20-bar window. Candidate universe = `vwap_reclaim_candidate` (ADV ≥ $1M, rvol_0945 > 1, CS/ADRC,
price ≥ $1).

**Verification (2026-07-08):** two-anchor split confirmed — on a synthetic day, `sess_max_vol` /
`sess_min_close` pick up the 08:30 premarket bars while VWAP reflects only 09:30+ RTH bars; a 0-volume halt
bar at 09:30 is excluded from every window (init_vol_15m = 15×1000, not counting the halt bar).

---

## Finding 1 — clean-sheet baseline: PF 1.244 / 67,293 trips / +$1.45M, positive 18/24 years

Full 22-year run (2003-09-10 → 2026-06-25), all optional/cap gates OFF, only the three core gates on
(vol-slope20 ≥ 0.05, price-slope20 > 0, tightness20 ≥ 3), geometry stop, hold-to-MOC, max-concurrent 1.

| metric | value |
|---|---|
| candidates | 52,630 ticker-days |
| trips | 67,293 |
| win rate | 24.5% |
| PF (MOC) | **1.244** |
| net P&L | **+$1,445,473** (notional $10k/trip) |

**Per-year** (positive **18 of 24 years**; all 6 negatives are pre-2018 and shallow):

| yr | n | win% | PF | net | avg_ret% |
|---|---|---|---|---|---|
| 2003 | 481 | 26.0 | 1.215 | 4,934 | 0.10 |
| 2004 | 1,290 | 24.4 | 1.174 | 9,578 | 0.07 |
| 2005 | 1,696 | 23.7 | 1.076 | 4,652 | 0.03 |
| 2006 | 2,395 | 23.9 | 1.016 | 1,419 | 0.01 |
| 2007 | 2,536 | 23.7 | 1.066 | 7,112 | 0.03 |
| 2008 | 2,604 | 21.9 | 0.924 | −14,140 | −0.05 |
| 2009 | 3,145 | 25.4 | 1.198 | 39,923 | 0.13 |
| 2010 | 3,315 | 24.8 | 0.871 | −18,033 | −0.05 |
| 2011 | 2,535 | 24.1 | 0.999 | −149 | −0.00 |
| 2012 | 2,540 | 24.7 | 1.076 | 8,479 | 0.03 |
| 2013 | 3,078 | 25.8 | 1.122 | 15,294 | 0.05 |
| 2014 | 3,586 | 23.9 | 0.995 | −786 | −0.00 |
| 2015 | 3,647 | 24.8 | 1.175 | 33,890 | 0.09 |
| 2016 | 2,352 | 26.1 | 1.298 | 41,139 | 0.17 |
| 2017 | 2,191 | 23.3 | 0.935 | −9,780 | −0.04 |
| 2018 | 1,938 | 25.4 | 1.175 | 24,669 | 0.13 |
| 2019 | 2,047 | 26.9 | 1.058 | 8,639 | 0.04 |
| 2020 | 3,753 | 25.2 | 1.372 | 185,798 | 0.50 |
| 2021 | 10,200 | 23.2 | 1.061 | 85,115 | 0.08 |
| 2022 | 4,902 | 24.5 | 1.183 | 111,174 | 0.23 |
| 2023 | 2,522 | 26.4 | 1.592 | 173,662 | 0.69 |
| 2024 | 2,067 | 24.7 | 1.708 | 275,343 | 1.33 |
| 2025 | 1,871 | 24.9 | 1.671 | 289,391 | 1.55 |
| 2026 | 602 | 25.2 | 2.072 | 168,150 | 2.79 |

**Reading:** a healthy, unoptimized baseline. The edge is **right-tailed** (24.5% win rate, positive PF ⇒ a
few large winners carry it — the momentum-continuation signature). It **concentrates in the modern regime**:
2020-2026 runs PF 1.37 → 2.07, climbing monotonically in the last four years, while the negative years are
all pre-2018 and shallow (worst = 2010 PF 0.87). 2021 (the two-sided-chop year that dogged V2) is the
highest-trip year (10,200) at a still-positive PF 1.061 — the loosest cell, ripe for the caps to prune.

This is the launch point for the feature breakdowns.

> **Test range note (from 2026-07-08):** the full 22y run takes ~8 min; going forward the working range is
> **2020-01-01 → 2026-06-25** (the modern era, where the edge lives — ~2.5 min). F1's table stays the 22y
> reference; subsequent findings quote 2020+ unless noted.

---

## Finding 2 — stop floor = SESSION min close (08:30) beats the 20m-min-close (+12pts win-rate)

The F1 baseline anchored the geometry stop at the **20m-min-close** (the trailing analogue of V2's run
floor). That floor is tight — it sits just under the recent 20-bar low, so a normal pullback trips it and
the trade is stopped before the continuation resumes (the low 24.5% win rate was the tell). Widening the
floor to the **session min close (from 08:30)** — `d = entry − session-min-close`, same `stop = entry −
d·2/3` — gives the trade room to breathe.

**22y (both stop floors, all else = F1 baseline):**

| stop floor | trips | win% | PF | net |
|---|---|---|---|---|
| 20m-min-close (F1) | 67,293 | 24.5% | 1.244 | +$1.45M |
| **session-min-close (08:30)** | 54,355 | **36.1%** | **1.253** | **+$1.60M** |

**2020+ (both):**

| stop floor | trips | win% | PF | net |
|---|---|---|---|---|
| 20m-min-close | 25,917 | 24.3% | 1.342 | +$1.29M |
| **session-min-close** | 21,007 | **36.1%** | **1.347** | **+$1.39M** |

**Reading:** the session floor is strictly better on every axis — **win-rate +12pts** (24→36%), PF up, net
up, and **~5k fewer trips** (the wider stop means fewer stop-outs → fewer re-entries, since max-concurrent
=1). The tight 20m floor was chopping winners exactly as suspected. **Per-year 2020+** (session floor) is
positive every year: 2020 PF 1.41, 2021 1.07, 2022 1.19, 2023 1.54, 2024 1.67, 2025 1.64, 2026 2.49.
**Session-min-close is now the default** (`StopFloorSessMin = true`); `--stop-floor-20m` reverts.

(Wiring note: the CLI boolean was initially a `parsed.Contains` flag that forced `false` and silently
overrode the new default — fixed by inverting to a `--stop-floor-20m` opt-out so the default is honored.)

**NEXT:** bucket-breakdown the recorded features to set thresholds.

---

## Finding 3 — log-ATR is THE main lever (step-function at ~0.013); price-slope subsumed

Breakdowns on the F2 baseline sample (2020+, gates = vol-slope20 ≥ 0.05, price-slope20 > 0, tightness20 ≥ 3).

**price_slope_20** climbs with slope but the sub-0.001 bucket (65% of the book) is the drag (PF 1.09);
≥0.001 is meaningfully better (PF 1.25–2.50). Monotone-ish.

**log_atr_20** is a **step function**: everything < 0.013 (88% of the book, ~18.4k trips) is flat/dead at
PF **1.07–1.11** regardless of exact ATR; above 0.013 it climbs hard — PF 1.32 → 1.60 → 1.67 → **2.35**
(≥0.030 alone = $660k net / 547 trips / 12% avg). This is the family thesis (high-ATR trades momentum).

**Cross-tab (price-slope × log-ATR)** — NOT redundant, but ATR dominates. The whole low-ATR column is dead
at every slope; slope only sorts the *high*-ATR cells. Best corner = high-ATR + mid-slope (PF 2.2–2.9).

**2D floor sweep — the verdict:** for maximizing trips at any PF target, you spend the entire selectivity
budget on ATR and keep slope ~0. Trip-maximizing floors per PF target:

| PF target | slope ≥ | ATR ≥ | trips | PF | net |
|---|---|---|---|---|---|
| 1.8 | ~0 | **0.013** | 2,538 | 1.80 | $1.19M |
| 1.9 | ~0 | 0.016 | 1,732 | 1.90 | $1.06M |
| 2.0 | ~0 | 0.018 | 1,489 | 2.00 | $1.06M |
| 2.1 | ~0 | 0.019 | 1,309 | 2.10 | $1.06M |
| 2.2 | ~0 | 0.027 | 683 | 2.22 | $0.72M |

Net is ~flat ($1.06M) across PF 1.8–2.1; **ATR≥0.013 has the highest net ($1.19M)**. Once ATR is set,
raising the slope floor only sheds trips. **Decision: `log_atr_20 >= 0.013` is the main gate** (wired to the
engine as `MinAtrPct`, default 0.013 — see the wiring note below); slope stays at the `> 0` downtrend floor.

> **Wiring (important):** the ATR floor MUST be an engine gate, not a post-hoc CSV filter. With
> `MaxConcurrent = 1` the engine fires the FIRST qualifying bar and holds it; a post-hoc ATR filter would
> drop a low-ATR trip WITHOUT surfacing the later high-ATR bar the concurrency cap suppressed on that day —
> a biased subsample. Added `MinAtrPct` gate + `--min-intraday-atr-pct` (default 0.013).

## Finding 4 — tightness is redundant with ATR (below-3 is empty); the 4–5 dead zone is real but not worth carving

Re-ran 2020+ with `MinAtrPct = 0.013` gated in-engine and **tightness OFF** (`--min-tightness 0`):
**3,641 trips / PF 1.666 / +$1.25M** (vs the post-hoc ATR≥0.013 & tightness≥3 cell ~2,573 trips / PF 1.79 —
the extra ~1,070 trips are low-tightness entries the old gate suppressed).

Tightness breakdown on this clean sample is **not a floor — it's a W**: below tightness 3 is essentially
EMPTY (32 trips total — high-ATR names have real range by construction, so the two gates are highly
correlated); a strong very-tight band 3–4 (273 trips, PF ~2.3); a **dead 4–5 zone** (840 trips = 23% of the
book, PF **1.27**); then the high-tight workhorse ≥5 (2,496 trips, PF 1.65→1.82, most of the P&L).

**Decision: leave tightness OFF.** It gains almost nothing over ATR (below-3 is empty), and band-excluding
4–5 "doesn't make sense" (user) as a mechanical gate. ATR already does tightness's job.

## Finding 5 — price-slope is pure LOTTERY EXPOSURE (dies under +50% clip); keep only as the `>0` floor

Re-ran the price-slope breakdown on the ATR-gated, tightness-off sample, then **clipped every trade's return
at +50%** (winners capped, net recomputed). The raw breakdown looked jagged-but-positive (a 1.84 spike at
0.001–0.002, a steep tail PF 2.0–2.4 at ≥0.006). **Clipping collapses ALL of it:**

| slope bucket | PF raw → **clip** | avg_ret% raw → clip |
|---|---|---|
| <0.001 | 1.15 → **0.98** | 0.56 → −0.08 |
| 0.001–0.002 | 1.84 → **1.29** | 3.36 → 1.17 |
| 0.002–0.004 | 1.3–1.5 → **1.10–1.24** | 1.5–2.1 → 0.5–1.0 |
| 0.004–0.006 | 1.67 → **1.25** | 3.38 → 1.27 |
| 0.006–0.008 | **2.38 → 1.29** | 9.95 → 2.07 |
| ≥0.008 | 1.9–2.0 → **1.14–1.18** | 9–11 → 1.6 |

Under clipping price-slope is **flat at ~1.1–1.3 across the board** — no monotone structure, no knee. The
steep-tail "edge" was entirely a few names that ran 100%+ (avg return 10% → 1.6% when capped). **price-slope
is not a lever — it's a lottery-exposure sorter.** Keep it ONLY as the downtrend floor (`> 0`, user: don't
buy a downtrend; revisit negative-slope buys later). ⚠️ **Clip every lever from here on** — and it's worth
re-checking that ATR / vol-slope themselves survive clipping (a high-ATR name is intrinsically more likely
to print a +100% day).

## Finding 6 — the 9-EMA run count (SumAbove6) IS a real, clip-robust entry-quality gate → `>= 5`

Breakdown of `sum_above_6` (# of the last 6 bars that closed above the 9-EMA) on the ATR-gated, tightness-off
sample, **raw + clipped at +50%**:

| sum6 | n | win% | PF raw | **PF clip** |
|---|---|---|---|---|
| 0 | 15 | 20.0 | 0.33 | **0.33** |
| 1 | 35 | 28.6 | 1.40 | 1.40 |
| 2 | 60 | 20.0 | 1.27 | **1.04** |
| 3 | 134 | 33.6 | 1.62 | **1.07** |
| 4 | 340 | 27.9 | 1.19 | **0.90** |
| **5** | 792 | 35.1 | 1.86 | **1.32** |
| **6** | 2,265 | 36.3 | 1.69 | **1.21** |

Unlike price-slope, this **survives clipping**: sum6 ≤4 is weak-to-dead (clipped PF 0.33–1.40, and sum6=4 —
340 trips — is NEGATIVE at 0.90), while sum6 = 5 and 6 hold at clipped PF **1.32 / 1.21** and carry 84% of
the book (3,057 / 3,641). The cut is clean at **≥5**: it distinguishes "price genuinely riding above the EMA"
from "loosely wandering around it," and the distinction is real after capping winners. This is the
"principled entries" lever (measures how *sustained* the push is — orthogonal to ATR's *size*).
`MinCloseAbove6` gate already wired (default 0). **Candidate default: `>= 5`.**

## Finding 7 — sum6≥5 stacked cell confirmed (6/7 years clip-positive); 2021 is the hole

`MinCloseAbove6 = 5` set as the default. Stacked cell = ATR≥0.013 + vol-slope≥0.05 + price-slope>0 +
**sum6≥5**, tightness off: **3,378 trips / PF 1.702 / +$1.26M** (2020+). Adding sum6≥5 in-engine lifted PF
1.67→1.70 at ~no dollar cost ($1.25M→$1.26M) — a free quality gate. (In-engine sum6≥5 gives 3,378 trips vs
the ~3,057 the post-hoc filter implied — concurrency reshuffles which bar takes the single daily slot.)

**Per-year, raw vs +50%-clipped** — robust in **6 of 7 years**, but **2021 is a hole**:

| yr | n | PF raw | PF clip | net clip |
|---|---|---|---|---|
| 2020 | 407 | 1.74 | 1.35 | $70k |
| **2021** | 1,144 | 1.11 | **0.90** | **−$57k** |
| 2022 | 460 | 1.31 | 1.05 | $12k |
| 2023 | 226 | 2.39 | 1.57 | $65k |
| 2024 | 437 | 2.19 | 1.43 | $106k |
| 2025 | 509 | 1.99 | 1.37 | $109k |
| 2026 | 195 | 3.13 | 1.80 | $83k |

2021 raw looked merely soft (PF 1.11); **clipped it's NEGATIVE (0.90 / −$57k)** — its entire positive year
was lottery winners; the typical 2021 trade lost. This is V2's two-sided-chop regime (V2 F24/F29), and it's
the loosest/highest-trip year here (1,144 trips, 31.7% win). Every other year is genuinely clip-positive
(1.05–1.80). **The core system is sound; 2021 is the explicit target** for the remaining levers — judge the
trend-too-long caps / session-max-ATR / entry-vs-VWAP partly on whether they prune 2021's bad trips.

## Finding 8 — tightness re-confirmed OFF post-9-EMA-gate (no clean floor; only a band-exclude would help)

Re-checked tightness on the sum6≥5 stacked cell (raw + clipped) — the 9-EMA gate did NOT change the shape:

| tightness | n | PF raw | **PF clip** | net clip |
|---|---|---|---|---|
| <3 | 18 | — | 1.24 | ~$1k (empty) |
| 3.0–3.5 | 60 | 1.69 | 1.09 | $2k |
| 3.5–4.0 | 148 | 2.84 | **1.57** | $43k |
| **4.0–5.0** | 717 | 1.26 | **0.97** | **−$12k** |
| 5.0–6.0 | 895 | 1.66 | 1.30 | $136k |
| 6.0–8.0 | 1,207 | 1.83 | 1.27 | $172k |
| ≥8.0 | 333 | 1.82 | 1.26 | $47k |

Same **W** as F4, sharpened by clipping: below-3 empty; a good very-tight band 3.5–4.0 (clip PF 1.57);
a **losing 4–5 dead zone** (717 trips, clip PF **0.97 / −$12k** — the only net-negative bucket); clean
≥5 workhorse. A floor at ≥5 would remove the 4–5 loser (−$12k) BUT also the good 3.5–4.0 band (+$43k) —
**net-negative** (~−$31k). Only a band-exclude of 4–5 helps, which "doesn't make sense" mechanically (user).
**Tightness stays OFF** — no simple floor helps; ATR already does its job. NOTE: the 4–5 dead zone likely
overlaps 2021's losers — check if a 2021-targeted lever prunes it for free rather than gating tightness.

## Finding 9 — sum_above_40/60 is a FRESH-PUSH preference (low = good), NOT a too-long cap — and it inverts in 2021

Breakdown of `sum_above_40` / `sum_above_60` (# of last 40/60 bars closed above the 9-EMA = how extended the
trend is) on the sum6≥5 stacked cell, raw + clipped. The spec framed these as a "trend-too-long CAP" (reject
HIGH counts). **The data says the opposite** — the edge is in the LOW buckets:

**sum_above_40 (clipped):** <10 = 0.59 (bad, 30 trips) | **10–15 = 1.79** | **15–20 = 1.36** | 20–25 = 1.12
| 25–30 = 1.13 | 30–35 = 1.14 | 35–40 = 1.14. Monotone DECAY as the count rises — a *fresh* push (10–20 of
40 bars above EMA) is the best trade; a *mature/extended* trend (≥25) is merely mediocre (~1.12), not a loser.
sum_above_60 same shape (<25 clips 1.28–1.96; mid 25–35 sags to 1.03).

So it's a **low-side preference / band** (`sum40 ∈ ~10–20` = 909 trips, clip PF ~1.5, $236k), not a cap. The
only genuinely bad bucket is `<10` (too few above-EMA bars = not yet a real push). ⚠️ **But it INVERTS in
2021** (see F10) — fragile; NOT gating on it as a "too-long cap."

## Finding 10 — 2021 needs a REGIME detector, not a feature gate; the worst cell is 2021×tightness-4–5

Two checks aimed at the 2021 hole (F7):

**(a) 2021-only by sum_above_40 (clipped):** 2021 loses across the ENTIRE sum40 range, and its ONLY positive
bucket is the EXTENDED one (≥30 = PF 1.21 / +$18k); <15 through 25–30 all clip 0.74–0.90 / negative. So a
trend-too-long cap would do the **exact wrong thing in 2021** — cut its one good bucket, keep the loser mass.
The aggregate sum40 signal (low=good) **inverts inside 2021**.

**(b) is the 4–5 tightness dead zone (F8) the same trips as 2021's clip-losers?** Partial overlap — the single
worst cell in the book is **2021 × tightness-4–5**: 230 trips, clip PF **0.64 / −$44k** (more negative than all
of 2021 combined). But neither problem contains the other:

| | n | PF clip | net clip |
|---|---|---|---|
| 2021 × tight-4–5 | 230 | **0.64** | **−$44k** |
| 2021 × other | 914 | 0.97 | −$13k |
| non-2021 × tight-4–5 | 487 | 1.12 | +$32k |
| non-2021 × other | 1,747 | 1.45 | +$414k |

The 4–5 dead zone is fine OUTSIDE 2021 (PF 1.12, +$32k); 2021 is bad EVERYWHERE but worst in the 4–5 band.

**Conclusion: 2021 cannot be gated away with entry-quality features** — every feature threshold either misfires
in 2021 (the cap) or discards good non-2021 trades (a tightness gate). This is V2's two-sided-chop lesson
(V2 F24/F29): the chop poisons ALL setups that year. **2021 needs a 2021-specific REGIME detector** (broader-
market breadth/chop signal — `mkt_chg_open`/`mkt_chg_prev` are recorded; SPY-chop, VIX, or a same-day
two-sided-fade detector), not another per-trade feature floor.

## Finding 11 — exhaustion cut `rvol_5m_20d < 100` — the biggest lever since ATR; FIXES 2021

Added a trailing **5-bar volume** window (`SumMa(5)`) to the engine + two recorded ratios: `rvol_5m_15m`
(5m-avg vs the name's OWN opening-15m avg) and **`rvol_5m_20d`** (5m-avg vs the 20d per-minute pace =
`(trailVol5m/5)/(avgvol20/390)`). Breakdown (clipped):

- **rvol_5m_20d is the clean exhaustion signal.** The ≥80–100 tail (~half the book) is blown-out/late — clip
  PF ~1.06; everything below clips 1.17–1.50. **Cap sweep 30–150 is a broad, flat knee** (clip PF 1.37–1.44
  for any cap 50–120 — NOT threshold-sensitive, so not overfit). Peak net at **< 100** (round, defensible:
  "5m volume ≥ 100× normal per-minute pace = a blow-off, skip"). Wired as `MaxRvol5m20d` gate (default 100).
- **rvol_5m_15m is NOT a lever** — jagged/non-monotone (a `<0.5` loser, good 1–2.5, a 2.5–4 dip, a 4–7 spike).
  Kept recorded, not gated.

**Gated `< 100` (vs post-hoc — the gate≠post-hoc point):** post-hoc `<100` predicted 1,755 trips / $945k;
the GATE gives **1,943 trips / $1.02M** — MORE trips + net, because with max-concurrent=1 skipping an
exhausted bar frees the day's slot for a later clean bar the post-hoc filter can't surface. (The cumulative
`cumVol/avgvol20` filter, by contrast, IS monotone-in-time so post-hoc = gated — a reason to prefer it later.)

**Stacked book now = ATR≥0.013 + vol-slope≥0.05 + slope>0 + sum6≥5 + rvol5m20d<100, tightness off:**
**1,943 trips / raw PF 2.06 / clip PF 1.42 / +$1.02M / 37.8% win.** Raw PF jumped 1.70→2.06.

**Per-year (clipped) — 2021 FIXED, all-weather now:**

| yr | n | PF raw | PF clip | net clip |
|---|---|---|---|---|
| 2020 | 208 | 2.21 | 1.54 | $53k |
| **2021** | 553 | 1.33 | **1.00** | $0.5k |
| 2022 | 220 | 1.65 | 1.31 | $32k |
| 2023 | 152 | 2.62 | 1.67 | $48k |
| 2024 | 256 | 2.35 | 1.66 | $82k |
| 2025 | 372 | 2.27 | 1.54 | $107k |
| 2026 | 182 | 3.20 | 1.86 | $80k |

**2021 went clip-NEGATIVE (−$57k, F7) → break-even (clip PF 1.00).** The cut removed 591 of 2021's 1,144
trips — the blown-out losers. So F10's "2021 needs a regime detector" resolves partly HERE: 2021's two-sided
chop was largely an **over-trading-the-blow-off** problem (panic/FOMO spikes into exhausted moves); the
exhaustion cut doubles as a regime filter. Every other year is solidly clip-positive (1.31–1.86). This is the
most robust state so far — **no negative years even under +50% clip.**

## Finding 12 — `sum_above_40 < 20` gate REJECTED — re-breaks 2021, redundant with the exhaustion cut

Wired F9's fresh-push preference as a GATE on top of the F11 book (`--max-sum-above-40 20`):
**681 trips / raw PF 2.56 / clip PF 1.58 / +$566k.** Tempting on PF — but two problems kill it:

| | trips | PF clip | net clip |
|---|---|---|---|
| exhaustion book (F11) | 1,943 | 1.42 | $403k |
| +sum40<20 | 681 | 1.58 | $211k |

1. **It UN-FIXES 2021.** F11 had 2021 at break-even (clip 1.00); +sum40<20 pushes it back to **clip 0.76 /
   −$22k** — negative again. Exactly F9's warning that sum40 **inverts in 2021**: 2021's only-good bucket was
   the EXTENDED (high-sum40) trades, so a low-sum40 "fresh-push" gate preferentially keeps 2021's losers.
2. **Halves the net** ($403k→$211k, −65% of trips) for a modest PF bump, on 56–143 trips/yr (small-sample).

**Deeper reason — REDUNDANCY:** the fresh-push cut and the exhaustion cut both encode "don't enter late." The
exhaustion cut (volume-based) does it in a way that FIXES 2021; the sum40 cut (bar-count-based) does it in a
way that HURTS 2021. When two levers target the same thing, keep the one that generalizes. **Keep the
exhaustion cut, reject sum40<20.** (Good years 2022–2026 ARE genuinely stronger under it — clip 1.48–2.46 —
so sum40 is real signal, just not robust; `MaxSumAbove40` stays available, default 0/off.)

**Current locked book: ATR≥0.013 + vol-slope≥0.05 + slope>0 + sum6≥5 + rvol5m20d<100, tightness off, sum40
off** → 1,943 trips / raw PF 2.06 / clip PF 1.42 / all-weather (per-year clip 1.00–1.86).

## Finding 13 — session-max-log-ATR floor REJECTED — redundant with the entry-ATR gate, and wrong tense

Breakdown of `sess_max_log_atr` (session-cumulative MAX of the 20m log-ATR) on the locked F11 book, raw+clipped:
flat/non-monotone (clip PF 1.17–1.50, no clean threshold; a dip at 0.03–0.04, fine elsewhere).

**Relationship to the entry `log_atr_20`:** sess_max is **median 3.0× (mean 3.5×) the entry ATR and exceeds
it 98.8% of the time.** So the F3 entry-ATR floor (≥0.013) ALREADY forces sess_max well above 0.015 (only 6
trips below) — a session-max floor carries almost no independent information; it's **subsumed** by the entry gate.

**And it's the wrong TENSE for a continuation entry:** sess_max asks "did this name EVER explode today?"
(persistent, backward-looking); entry-log-ATR asks "is it volatile RIGHT NOW at the entry bar?" A name that
spiked at the open and went quiet has HIGH sess_max but LOW entry-ATR — the "explosion already over" case we
correctly DON'T want. The entry-ATR gate excludes those; a sess_max floor would ADMIT them (mildly wrong-signed).
(Contrast HighFlyerV2, where the 126-bar-max-ATR floor IS useful — but that's a multi-day swing selecting
"names that CAN move," a different question than "is this intraday move alive now.")

**Conclusion: don't gate on sess_max-log-ATR.** Entry `log_atr_20` is the correct volatility gate — confirmed.

## Finding 14 — VWAP-location floor `entry_vs_vwap >= -3%` — free PF+net, and a 2nd (orthogonal) 2021 fix

Breakdown of `entry_vs_vwap` (entryPx/VWAP − 1) on the locked F11 book (clipped): the **`< −3%` below-VWAP
bucket (383 trips, 20%) is the ONLY loser — clip PF 0.93 / −$15k.** A momentum name trading >3% BELOW its
session VWAP has been sold off hard intraday = a falling knife, not a continuation. Everything ≥ −3% is
positive (clip 1.17–2.05); above-VWAP is strong (1–3% = clip 2.05; ≥6% = best win rates 46–48%).

**Floor sweep (clipped):** `≥ −3%` = 1,560 trips / clip PF **1.55** / net **$418k** (UP from 1.42/$403k —
improves PF AND net, free). `≥ −1%` gains nothing (1.54/$378k). **−3% is the knee** (round, non-overfit).

**2021 (orthogonal 2nd fix):** the `< −3%` bucket in 2021 is catastrophic (79 trips, clip PF **0.33 / −$29k**)
— cutting it takes 2021 from break-even (F11 clip 1.00) to genuinely positive. Distinct signal from the
exhaustion cut: exhaustion removes high-VOLUME blow-offs, the VWAP floor removes sold-off-below-VWAP PRICE
locations — both are "bad entry location," orthogonal, and 2021's chop produced BOTH flavors.

Wired `MinEntryVsVwap` gate (default −0.03) + `--min-entry-vs-vwap`. **Gated `≥ −3%`:** 1,661 trips (vs
post-hoc 1,560 — slot-freeing again) / raw PF **2.18** / clip PF **1.50** / +$979k / 39.6% win.

**Per-year (clipped) — every year now clip-positive with margin:** 2020 1.50 | 2021 **1.05** | 2022 1.39 |
2023 2.24 | 2024 1.88 | 2025 1.62 | 2026 1.87.

**Current locked book: ATR≥0.013 + vol-slope≥0.05 + slope>0 + sum6≥5 + rvol5m20d<100 + entry-vs-vwap≥−3%,
tightness off** → 1,661 trips / raw PF 2.18 / clip PF 1.50 / +$979k / 39.6% win / all-weather.

## Finding 15 — 9-EMA-above-VWAP persistence (SumMa 30/60) is U-shaped, NOT a lever — but confirms V3 ≠ VwapReclaim

Added `ema_vwap_30` / `ema_vwap_60`: SumMa of the "was the 9-EMA above the session VWAP this bar?" 0/1
indicator over 30/60 feature-bars (windowed COUNT, distinct from VwapReclaim's cross event). Recorded, then
broken down on the locked book (clipped):

**Both ends strong, middle sags (U-shape).** ema_vwap_30: `0–4` (fresh reclaim, clip **1.53**) ≈ `30`
(persistent all-window, clip **1.63**); the `10–14` middle is the one loser (clip **0.95**). ema_vwap_60 same:
`0–9` (1.52) ≈ `60` (1.94), `10–19` dip (1.24). No floor helps — a `≥20` floor would keep the good high end
but discard the EQUALLY-good `0–4` low end (674 trips).

**Why — and the point:** the `0–4` bucket is a name that JUST reclaimed VWAP (fresh cross); the `30/60` bucket
is a persistent above-VWAP uptrend. **Both work.** The dead middle is the EMA chopping across VWAP — neither a
clean reclaim nor a clean trend. **This confirms V3 is NOT a VwapReclaim system in disguise:** if it were, the
fresh-cross bucket would dominate and persistence would hurt; instead persistence is NEUTRAL. V3's edge is
ATR/volume/9-EMA-run; the VWAP relationship matters only as the F14 LOCATION floor (don't buy >3% below),
not as a trend-persistence signal. **Record `ema_vwap_30/60`, don't gate** (the `10–14` dip is an interior
band — the tightness-4–5 overfitting trap).

## Finding 16 — vol_slope_20 is a REAL clip-robust lever (unlike price-slope); `≥0.05` floor justified + a blow-off CEILING

`vol_slope_20 ≥ 0.05` was a V2-inherited gate never clip-verified in V3. Studied it the RIGHT way — with
**max-concurrent = 0 (unlimited)** so dropping the gate doesn't reshuffle the single daily slot (a stable,
non-competing 57k-trip set; other gates on). Breakdown (clipped):

| vol_slope_20 | n | PF clip | avg_ret clip |
|---|---|---|---|
| <−0.05 | 4,426 | 1.38 | 1.46 |
| −0.05..0 | 16,965 | 1.28 | 1.46 |
| 0..0.025 | 11,793 | 1.18 | 1.16 |
| 0.025..0.05 | 9,730 | 1.29 | 1.84 |
| **0.05..0.10** | 10,550 | 1.41 | 2.51 |
| **0.10..0.15** | 2,864 | **2.00** | 5.20 |
| 0.15..0.25 | 1,005 | 1.59 | 3.27 |
| **≥0.25** | 74 | **0.58** | **−4.94** |

**Unlike price-slope (F5, pure lottery — died under clipping), vol-slope SURVIVES clipping.** Clipped PF rises
past ~0.05, peaking at `0.10–0.15` (clip **2.00**, avg return 5.2%). So the V2 direction holds: rising volume
into the entry is a real, clip-robust edge. Nuances: (1) the `≥0.05` floor cuts MEDIOCRE trips (sub-0.05 clips
1.18–1.29 — positive, not dead), so it's a quality gate not a survival gate — but justified (selects 1.4–2.0
over 1.2 mush); keep it. (2) The `≥0.25` bucket is a genuine LOSER (clip **0.58 / −4.94%**) — an extreme
volume-slope-up = a blow-off into entry, the same exhaustion signature F11 targets, but on the HIGH side the
floor can't catch. **Add a vol-slope CEILING** to cut it (likely partial overlap with the F11 exhaustion cut).
[NOTE: unlimited-concurrency lens for SEEING the feature; ceiling re-verified gated below.]

**Ceiling wired & gated (MaxVolSlope, default 0.25):** 1,660 trips / PF 2.181 / +$979k — vs 1,661 without.
**Essentially identical (1 trip differs)** → CONFIRMED redundant with the F11 exhaustion cut (both detect
volume blow-offs; on the max-conc-1 book the ≥0.25 entries were already excluded by rvol5m20d<100). Kept as
cheap insurance (matters only if the exhaustion cut is ever loosened). Locked book PF now 2.18.

**NEXT:** 1d-return-to-entry breakdown (chg_1d — a significant lever in LowFlyer/prior systems) →
entry-vs-session-high → cumulative cumVol/avgvol20. Clip every lever.
