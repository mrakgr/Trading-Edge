# MaxRiderV1 — results

`TradingEdge.MaxRiderV1` (branch `diprider-v6-mean-reversion`). **Intraday MEAN REVERSION, the SHORT side.**
A direction-flipped mirror of DipRiderV6, forked 2026-07-17.

> **User:** *"Make a new project by forking DipRiderV6. We're going to do more research on mean reversion,
> but this time from the short side. That will be one way to get more trades."*

**Naming** follows the house convention — LowFlyer (long) / MaxFlyer (short) ⇒ DipRider (long) /
**MaxRider** (short). **Read `docs/diprider_v6_results.md` first**; this document only records where the short
side DIFFERS from the long.

**Standing conventions:** SHORT only. **2020-01-01 → 2026-06-30.** Raw MOC PF. $10k notional/trip.
`dv_0945 >= $3M`, ATR floor 0.004 (uncapped ceiling), 20m-high entry → **7m-low cover** (F5; the long side uses 5m).

---

## The system — the exact mirror of V6

| | V6 (long) | **MaxRiderV1 (short)** |
|---|---|---|
| ENTRY | close ≤ prior 20m MIN of closes | **close ≥ prior 20m MAX of closes** (fade the pop) |
| EXIT | close ≥ prior 5m MAX (cover) | **close ≤ prior 5m MIN** (cover), or MOC |
| counters | arm on new LOW, reset on new HIGH | **arm on new HIGH, reset on new LOW** |
| P&L | `exit − entry` | **`entry − exit`** (profit when price FALLS) |

**⚠ WHY THE SHORT SIDE IS NOT A FREE MIRROR** — the engine banner says *MEASUREMENT ONLY* for these reasons,
none of which is modelled:
- **Borrow / locate.** The thin, high-ATR names where V6 found its best cells (F14: the PF 3.291 cell is
  $1.13 stocks; F17: the 0.06–0.08 ATR tail) are exactly the **hardest to borrow** and most likely on the
  hard-to-borrow list at punitive rates.
- **Unbounded tail.** A long is bounded at −100%; **a short is not.** V6 ran with NO STOP because its p01 of
  −17.8% was survivable. An unstopped short into a squeeze is not. A real short book **needs a stop** the
  long book could skip.
- **SSR / uptick.** A name down ≥10% triggers SSR next session — shorts fill only on an uptick.
- **Prior art.** MaxFlyerV2/V3 already fade pops; MaxFlyerV3 is ⚠ UNCONFIRMED (its `brv20d` lever fails the
  lookahead audit). This is a **clean-sheet** measurement; do not assume that book's findings port.

---

## Finding 1 — ⭐ THE SHORT MIRROR IS A REAL, ALL-WEATHER EDGE — slightly weaker than the long, exactly as theory predicts

**Full sampler (2020-26): 2,422,667 trips / PF 1.540 / +0.233%/tr / 68.5% win.**

**Sign verified** (the single most corrupting thing to get wrong): every WIN has `exit < entry`, every LOSS
has `exit > entry` — **0 violations** on both. Counters mirror cleanly: 0 negatives, 0 invariant violations
(`bars ≥ highs` always), max highs-into-leg 52.

**SHORT vs LONG, per year (both 20m→5m, dv ≥ $3M, ATR band):**

| yr | short n | **SHORT PF** | SHORT avg% | **LONG PF** | LONG avg% |
|---|---|---|---|---|---|
| 2020 | 400,379 | 1.516 | 0.210 | 1.881 | 0.292 |
| 2021 | 512,974 | 1.628 | 0.220 | 1.927 | 0.269 |
| 2022 | 371,576 | 1.451 | 0.175 | 1.633 | 0.207 |
| 2023 | 266,741 | **1.802** | 0.313 | 1.878 | 0.312 |
| 2024 | 324,415 | 1.699 | 0.301 | 1.798 | 0.311 |
| 2025 | 363,626 | 1.418 | 0.202 | 1.467 | 0.203 |
| 2026 | 161,242 | 1.326 | 0.147 | 1.390 | 0.165 |

**⭐ The short PF is below the long in EVERY year, by a consistent 0.05–0.31 — but the CAUSE is NOT
overnight drift.** (An earlier draft blamed "stocks drift up so fading pops fights a tailwind" — **WRONG,
and the user corrected it:** the S&P's entire long-run return has historically accrued OVERNIGHT (close→open);
the intraday session is flat-to-negative, per Brett Steenbarger's work. So there is no intraday up-tailwind
for a short to fight.) The real cause is **OPEN** — candidates: SSR making down-moves stickier (a new-high
fade genuinely can squeeze), or an asymmetry in how the 20m-high/5m-low windows interact with intraday
structure. Do not assert a mechanism until it is measured.

**⚠ It is the SAME SETUP FROM THE OTHER SIDE, NOT A DIVERSIFIER.** Both books are strongest in 2023 (1.80 /
1.88) and weakest in 2026 (1.33 / 1.39) — the **same year-to-year shape**. The two will move TOGETHER, not
hedge each other. What the short side delivers is the user's stated goal — **MORE TRADES**: 2.4M short +
2.6M long on the same infrastructure, ~2× the capacity.

---

## Finding 2 — the FAST cover mirrors the long side (V6 F16), but the lift is MUTED and the optimum is FLATTER

**User:** *"Do 5m exits improve upon the 20m exits similarly for how they do on the long side?"*

**Yes — same shape, smaller magnitude.** Cover-window sweep, identical entries (2,400,953 trips every row,
dv ≥ $3M, ATR band):

| cover = prior N-min MIN of closes | win% | avg %/tr | **PF** | med hold |
|---|---|---|---|---|
| 3m | 67.1 | 0.174 | 1.528 | ~4 min |
| **5m** | 68.5 | 0.225 | **⭐ 1.552** | 6 min |
| 7m | **69.0** | 0.256 | 1.540 | ~8 min |
| 10m | **69.0** | 0.281 | 1.498 | 11 min |
| 20m | 68.1 | 0.308 | 1.387 | 24 min |
| 30m | 67.5 | 0.332 | 1.349 | 36 min |
| 45m | 67.2 | 0.351 | 1.311 | 55 min |
| 60m | 66.9 | 0.345 | 1.268 | 74 min |

Same structure as V6: **PF falls monotonically** with target length (1.552 → 1.268) while **avg/trade
rises** (0.225 → 0.345). Fast wins on PF, slow wins on per-trade return.

### ⭐ BUT the improvement is SMALLER on the short side — and I expected the OPPOSITE

| | 5m PF | 20m PF | **5m/20m ratio** |
|---|---|---|---|
| **SHORT** | 1.552 | 1.387 | **1.119 (+12%)** |
| **LONG** (V6) | 1.723 | 1.417 | **1.216 (+22%)** |

**The fast target lifts the LONG book ~2× as much as the SHORT (+22% vs +12%).** My prior was the reverse —
that squeeze risk would make a fast cover help the short side MORE (get out before the run-over). The data
says no.

**The tell is the win-rate curve.** On the short, win rate RISES from 5m→7m→10m (68.5 → 69.0 → 69.0) and
**3m already rolls over on PF (1.528)** — a very fast cover clips shorts that were still working. On the
long side 5m was already the win-rate peak with no over-speed. **So the short's snap-DOWN completes
marginally SLOWER than the long's snap-UP** — the short's ideal cover is a flatter plateau around 5–7m
rather than a sharp 5m point.

**Practical:** keep the 5m default (it is the PF peak and the fine sweep 3/5/7 confirms it), but the
short-side optimum is a **plateau [5m, 7m]**, not a knife-edge. At mc=1 the marginally-slower peak may matter
for capacity.

⚠ Every number is mc=0 attribution and models NO borrow/SSR/squeeze. The 5m cover's main virtue for a REAL
short book is unchanged: a ~6-min hold gives a squeeze little time to develop — but it is not a substitute
for a stop.


---

## Finding 3 — ATR mirrors the long side in the mid-band, but the TAIL INVERTS: high-ATR shorts want the SLOW cover

**ATR × cover window (dv ≥ $3M, tail uncapped):**

| ATR | **PF 5m** | PF 20m | avg% 5m | win% 5m | n |
|---|---|---|---|---|---|
| 0.004–0.006 | 1.369 | 1.216 | 0.102 | 66.8 | 1,122,300 |
| 0.006–0.009 | 1.568 | 1.365 | 0.208 | 69.0 | 720,678 |
| 0.009–0.013 | 1.632 | 1.489 | 0.345 | 70.4 | 317,313 |
| **0.013–0.020** | **⭐ 1.773** | 1.601 | 0.605 | 72.4 | 168,347 |
| 0.020–0.035 | 1.653 | 1.556 | 0.889 | 73.3 | 72,315 |
| **0.035–0.05** | 1.405 | **1.412** | 1.033 | 71.5 | 14,415 |
| **0.05–0.08** | 1.239 | **1.431** | 0.894 | 69.7 | 6,364 |
| **≥ 0.08** | 1.653 | **⭐ 1.940** | **2.85** | 71.3 | 935 |

- **Same peak, same hump: 0.013–0.020 (PF 1.773)** — matches V6 F17's location exactly. Mid-band the 5m
  cover wins +12–15%. ATR is an independent lever on the short side too.
- **⭐ THE TAIL INVERTS vs the long side.** For the LONG book the fast target dominated MOST at high ATR
  (the snap-back was quickest there). For the SHORT, **at ATR ≥ 0.035 the SLOW (20m) cover WINS**
  (0.05–0.08: 1.431 vs 1.239; ≥0.08: 1.940 vs 1.653). **High-ATR names need MORE time to fall to the
  cover** — a 5m cover on a volatile short clips it before the down-move completes.
- **The ≥0.08 short cell is strong on paper** (PF 1.940 @20m, +2.85%/tr, 71% win) **but it is the MOST
  DANGEROUS cell in the study**: 935 trips over 6.5y, and these are the most volatile, HARDEST-TO-BORROW,
  most squeeze-prone names. **Flag it, do not bank it** — most likely a mirage once borrow/locate/SSR are real.

---

## Finding 4 — the z-score mirrors V6, with a SHORT-SPECIFIC twist: the best fade is a pop that FAILS below VWAP

For a SHORT the pop is ABOVE VWAP, so POSITIVE z is the "deeply extended" tail (the mirror of the long's
negative z). z × cover (dv ≥ $3M, ATR band):

| z | **PF 5m** | PF 20m | avg% 5m | n |
|---|---|---|---|---|
| **z ≥ +3** | **1.670** | 1.551 | 0.521 | 73,122 |
| +2..+3 | 1.494 | 1.390 | 0.246 | 416,208 |
| **+1..+2** | 1.368 | 1.261 | 0.154 | 927,312 |
| 0..+1 | 1.742 | 1.413 | 0.252 | 634,849 |
| **z < 0** | **⭐ 1.869** | 1.686 | 0.279 | 349,462 |

**⭐ The U-shape returns (as V6 F6/F16) — but for the short, the STRONG end is the WRONG-LOOKING one.** The
best cell is **`z < 0` (PF 1.869)**: a name making a new 20m HIGH while its close is BELOW VWAP. The
extended-pop tail (`z ≥ +3`, 1.670) is good but NOT the best.

**This is NOT the clean mirror of the long side** (where the extended dip WAS the strong tail). **The
reading: a pop to a new 20m high that is STILL BELOW VWAP is a weak, exhausted bounce inside a down day —
the textbook short.** A `z ≥ +3` name has ripped FAR above VWAP, closer to genuine strength that can keep
squeezing. **The short's quality signal is not "most extended pop" — it is "pop that FAILS below VWAP."**

**The 5m-dissolves-z effect holds** (as V6 F16): at 20m, z spread 1.261 → 1.686; at 5m it compresses upward
(1.368 → 1.869), and the `0..+1` and `z<0` cells jump most (+23%, +11%). **The fast cover takes the
immediate reversal regardless of z** — so on the short side too, z is more a *slow-exit* lever than a
fast-exit one.

⏭ This reframes the short entry: pair the new-20m-high trigger with a **`close < VWAP` filter** (not the
naive `close > VWAP` mirror of the long). Re-check chg_1d next — the analogue prediction is that the best
short is a name moderately UP (not ripping), fading a failed intraday bounce.


---

## Finding 5 — ⭐ 7m is the SHORT-SIDE default: the fast cover leaves 2–3× the return on the table at high ATR

**User** (flagging the weak `0.05–0.08` band under a 5m cover): *"Let's compare the 7m exits to them."* →
then, seeing the avg%: *"7m default."*

**PF alone understated the case. The avg% column is decisive — at high ATR the SLOW cover captures 2–3× the
per-trade return**, because the move there is large AND slow:

| ATR | PF 5 | PF 7 | PF 20 | **avg5** | **avg7** | **avg20** | n |
|---|---|---|---|---|---|---|---|
| 0.004–0.009 | **1.460** | 1.438 | 1.284 | 0.144 | 0.160 | 0.177 | 1,842,978 |
| 0.009–0.013 | **1.632** | 1.618 | 1.489 | 0.345 | 0.391 | 0.506 | 317,313 |
| 0.013–0.020 | 1.773 | **1.780** | 1.601 | 0.605 | 0.703 | 0.903 | 168,347 |
| 0.020–0.035 | 1.653 | **1.695** | 1.556 | 0.889 | 1.078 | 1.416 | 72,315 |
| 0.035–0.05 | 1.405 | 1.383 | **1.412** | 1.033 | 1.147 | **1.847** | 14,415 |
| **0.05–0.08** | 1.239 | **1.374** | 1.431 | 0.894 | **1.504** | **2.589** | 6,364 |
| **≥ 0.08** | 1.653 | 1.928 | **1.940** | 2.85 | **4.137** | **6.308** | 935 |

**Two regimes:**
- **Low/mid ATR (the bulk):** the reversal is FAST — 5m/7m win on PF, holding longer just bleeds win rate,
  and avg% barely differs (0.14 vs 0.18). 5m is marginally best on PF.
- **High ATR (≥0.035):** the move is LARGE and SLOW — a fast cover throws away return. At ≥0.08, 20m earns
  **+6.3%/trade** vs 5m's +2.85%; at 0.05–0.08, +2.59% vs +0.89%.

**⭐ 7m is the chosen compromise (user).** It barely dents the low/mid bulk (PF within 0.02 of 5m) while
recovering ~half the high-ATR return the 5m cover discards (0.05–0.08 avg 0.89% → 1.50%; ≥0.08 2.85% →
4.14%). A single global window, no exit-vs-entry coupling to validate. It fits F2's finding that the short's
snap-DOWN completes slightly slower than the long's snap-UP — **the short optimum is a plateau [5m,7m], and
7m sits at the return-favouring end of it.** The LONG side stays 5m (they measured differently).

⚠ **Caveats kept loud:** (1) the high-ATR bands 7m rescues are ~21k trips (0.8% of the book) and the
**hardest to borrow** — the +6.3% at ≥0.08 is the most likely to evaporate under real execution. (2) avg%
rising with hold time partly just reflects a longer target having more room to travel — it is carrying more
risk per trade, which is why PF does not rise proportionally. **A future ATR-conditional cover (5m low / 20m
high-ATR) captures the best of both, but couples exit to entry and leans on exactly those borrow-constrained
cells — deferred to mc=1.**

### Engine change
**`ExitLowWindow` default 5 → 7** (user). Long side (`DipRiderV6`) stays at 5.


---

## Finding 6 — ⭐⭐ VOLUME z: fade the QUIET pop, not the loud one — and LOG space beats normal space

**User:** *"do a breakdown on volume. Let's try a volume z score in log space."* → *"We could also do it in
normal space and compare."*

Added two session-cumulative `CumStdMa` accumulators (mirroring `dist_vwap_z`): `vol_z_log` (z of
`log(bar_vol)`) and `vol_z_lin` (z of raw `bar_vol`). `bar_vol` spans 100 → 39M (median 25k) — textbook
heavy-tailed, so the linear z is outlier-dominated by construction. Both recorded so the transform is
**validated, not assumed.**

**LOG-space vol z (dv ≥ $3M, ATR band, 7m cover):**

| vol_z_log | n | win% | avg %/tr | **PF** |
|---|---|---|---|---|
| **< −0.5 (quiet)** | 827,081 | 71.2 | 0.270 | **⭐ 1.769** |
| −0.5..0 | 451,658 | 68.8 | 0.230 | 1.531 |
| 0..0.5 | 426,172 | 68.2 | 0.245 | 1.505 |
| 0.5..1 | 330,493 | 67.5 | 0.246 | 1.441 |
| 1..2 | 313,973 | 66.6 | 0.283 | 1.417 |
| **≥ 2 (spike)** | 51,576 | 64.3 | 0.267 | **1.281** |

**⭐ Strictly monotone: PF 1.769 → 1.281 as volume RISES. FADE THE QUIET POP.** A new-20m-high pop on
BELOW-average volume is a hollow, unsupported drift that fails; a pop on a VOLUME SPIKE is real buying
pressure that keeps going. **A volume climax at a new high is a BREAKOUT, not an exhaustion.** This mirrors
V6's long-side exhaustion logic from the other side.

### ⭐ LOG beats NORMAL — the comparison was worth running

| vol_z_lin (normal space) | n | **PF** |
|---|---|---|
| < −0.5 | 841,449 | 1.674 |
| −0.5..0 | 840,316 | 1.635 |
| 0..0.5 | 301,202 | 1.395 |
| 0.5..1 | 151,109 | **1.337** ← dip |
| 1..2 | 142,845 | 1.368 |
| ≥ 2 | 124,032 | 1.388 ← rises again |

**Log spans PF 1.769 → 1.281 (0.49, MONOTONE). Linear spans 1.674 → 1.337 (0.29) and is NON-MONOTONE** — it
dips at 0.5–1 then rises at ≥2, because a handful of 14σ bars land in the top bucket regardless of the true
shape and blur the signal. **Log space is the correct normalization — confirmed empirically.** (Sanity: log
z is symmetric, p99 2.18, max 4.55; linear z has median −0.37 but max 13.93 — the right-skew tell.)

### ⭐ vol_z × dist_vwap_z STACK — two independent dimensions of exhaustion

| vol_z | z<0 (fails below VWAP) | z 0–2 | z≥2 (extended) |
|---|---|---|---|
| **QUIET (<0)** | **⭐ 1.944** | 1.600 | 1.690 |
| MID (0–1) | 1.798 | 1.412 | 1.530 |
| SPIKE (≥1) | 1.487 | 1.238 | 1.499 |

Quiet volume beats spike at EVERY z level; the "fails below VWAP" pop (F4) beats extended at EVERY volume
level. **The best cell — QUIET volume × BELOW-VWAP pop = PF 1.944** (from a 1.533 book baseline); the worst
is a mid-z SPIKE (1.238). The two measure DIFFERENT things: `dist_vwap_z` = *where price is* (failed vs
extended); `vol_z_log` = *how much conviction the pop has*. A quiet pop failing below VWAP is the purest
exhaustion — no volume, no reclaim of fair value.

⚠ **Non-monotone wrinkle in the SPIKE row:** `z≥2` (1.499) beats `z 0–2` (1.238). A high-volume pop that is
ALSO wildly extended can still be a blow-off top worth fading; a high-volume pop at MODERATE extension is the
danger zone (a genuine breakout with fuel). Remember this before gating on vol_z alone.

### Engine change
Two recorded columns added: `vol_z_log`, `vol_z_lin` (both session-cumulative `CumStdMa`). Neither gates.
⏭ Port both to DipRiderV6 (long side has no volume-z lever yet) and check the long-side sign — the long
mirror predicts fade... i.e. BUY the quiet dip, not the panic-volume flush.


---

## Finding 7 — log-space VWAP z is INDISTINGUISHABLE from linear (unlike volume) — and that contrast IS the lesson

**User:** *"Since calculating the z scores in log space performs well, we should calculate z score of the
distance from VWAP in log space as well for both systems."*

Added `dist_vwap_z_log` = session-cumulative z of `log(close/vwap)` (the true log analogue — symmetric,
where the raw ratio `close/vwap−1` is not) to BOTH engines. **Result: it makes no measurable difference.**

**SHORT (dv ≥ $3M, ATR band, 7m):**

| VWAP z bucket | **linear PF** | **log PF** | Δ |
|---|---|---|---|
| ≥ +3 | 1.653 | 1.613 | −0.04 |
| +2..+3 | 1.515 | 1.537 | +0.02 |
| +1..+2 | 1.356 | 1.357 | ~0 |
| 0..+1 | 1.688 | 1.690 | ~0 |
| **< 0 (below vwap)** | **1.883** | **1.878** | ~0 |

**LONG (DipRiderV6, 5m):**

| VWAP z bucket | linear PF | log PF |
|---|---|---|
| **< −3** | 2.152 | 2.161 |
| −3..−2 | 1.915 | 1.896 |
| −2..−1 | 1.579 | 1.570 |
| −1..0 | 1.785 | 1.792 |
| ≥ 0 | 1.839 | 1.840 |

The two track within ~0.02–0.04 on **both** systems; bucket populations barely shift; every finding (the
U-shape, F4's best-short-below-VWAP, V6 F7's long z-sweep) is unchanged.

### ⭐ WHY — and it is the real lesson, since it CONTRASTS with F6 (volume)

The log transform earned its keep for **volume** (F6: log spanned PF 1.769→1.281 monotone, linear only
1.674→1.337 and non-monotone) but is **inert for VWAP distance** — because of the INPUT SCALE:

| input | range | `log(1+x)` vs `x` | z dominated by outliers? |
|---|---|---|---|
| **bar volume** | 100 → 39M (5+ orders of magnitude) | N/A — raw values | **YES** → log essential |
| **dist_vwap** | ~±1–3% | `log(1+x) ≈ x` for small x | no → transform ~inert |

**A z-score already normalizes scale; the log transform only adds value when the input is HEAVY-TAILED
enough that a few outliers hijack the mean/σ.** Volume is (a handful of 14σ bars). VWAP distance is not
(±3%, near-Gaussian). **Keep `dist_vwap_z_log` as the canonical column (symmetric, correct in principle),
but the choice is immaterial here — do not re-run it.**

**Generalisable rule for this codebase:** *reach for log space when the raw feature spans orders of
magnitude (volume, dollar-volume, float); skip it for features already bounded to a few percent (returns,
VWAP distance, ATR%).*


---

## Finding 8 — ⭐⭐ chg_1d: BOTH books want MODERATELY-DOWN names — and the up-day tail is a CATASTROPHIC squeeze zone

**Prediction (WRONG):** the mirror said the best short would be a name moderately UP, fading a failed bounce.
**The data says the best short is a name moderately DOWN**, same as the long book's best dip-buy.

**chg_1d (dv ≥ $3M, ATR band, 7m cover):**

| chg_1d | n | % | win% | avg% | **PF** |
|---|---|---|---|---|---|
| < −20% | 81,145 | 3.4 | 66.3 | 0.187 | 1.245 |
| −20..−10% | 147,441 | 6.1 | 66.6 | 0.225 | 1.411 |
| −10..−5% | 243,316 | 10.1 | 68.4 | 0.238 | 1.609 |
| **−5..−2%** | 279,042 | 11.6 | 71.0 | 0.281 | **⭐ 1.912** |
| −2..0% | 216,767 | 9.0 | 70.5 | 0.232 | 1.695 |
| 0..+2% | 227,156 | 9.5 | 69.2 | 0.206 | 1.614 |
| +2..5% | 297,774 | 12.4 | 68.6 | 0.200 | 1.547 |
| +5..10% | 357,006 | 14.9 | 68.0 | 0.204 | 1.482 |
| +10..20% | 313,797 | 13.1 | 68.0 | 0.248 | 1.444 |
| +20..50% | 185,917 | 7.7 | 70.5 | **0.477** | 1.580 |
| ≥ +50% | 51,592 | 2.1 | **73.0** | **0.675** | 1.442 |

**⭐ PF peaks at −5..−2% (1.912) and declines monotonically UP the day.** The best short is a name **slightly
RED on the day** that pops to a new 20m high — a failed bounce inside a down day.

**⭐⭐ BOTH BOOKS WANT DOWN-DAY NAMES.** V6 F11's long peak was also moderately down (−10..−2%). **A name red
on the day but oscillating is a two-way MEAN-REVERSION MACHINE — buy its dips AND fade its pops, both work.**
That is the opposite of a clean long/short mirror, and it means the two books share a universe (correlated,
per F1 — not a hedge).

### ⭐ THE UP-DAY TAIL (≥ +20%) IS THE SQUEEZE ZONE — high avg%, fat PF, ACCOUNT-ENDING losses

The +20% cells have the HIGHEST win rate (70.5%, 73.0%) and HIGHEST avg% (0.48%, 0.68% — 2–3× the peak) but
only MIDDLING PF (1.58, 1.44). That combination is the classic fat-tailed-loser signature. The loss
distribution confirms it — this is the squeeze risk, MEASURED:

| chg_1d | avg% | p1 worst | **MAX LOSS** | PF |
|---|---|---|---|---|
| **≥ +50% (ripping)** | 0.675 | **−24.05%** | **−420%** | 1.442 |
| **+20..50% (up big)** | 0.477 | **−12.08%** | **−609%** | 1.580 |
| DOWN day (< −2%) | 0.246 | −6.01% | −77% | 1.571 |
| flat/mild (< +20%) | 0.217 | −5.61% | −166% | 1.526 |

**A single short in the +20..50% cell lost −609% (a ~7× squeeze); another −420% in the ripping cell.** p1
losses are −24% and −12% vs the down-day book's −6%. The up-day tail's fat avg% SURVIVES A HANDFUL OF
ACCOUNT-ENDING LOSSES — in a live book those blow you up before the edge compounds. **Fading a name up 50%
is picking up nickels in front of a steamroller.**

### ⚠ AND THIS PUNCTURES THE "7m COVER = TIME-STOP" CLAIM (partially)

A −609% loss means the name kept climbing past every 7-minute low **for the rest of the session** and only
covered at MOC — the cover target NEVER PRINTED. So "the 7m cover bounds the run-over" holds ONLY while a
new 7m low eventually prints. In a runaway squeeze it does not, and the position runs to close.
**A HARD STOP IS genuinely needed — but specifically for the up-day tail, not the bounded-hold bulk.**

### ⭐ Verdict — the tradable short universe

**`chg_1d < +20%`** (the down-day + mild bands): PF 1.53–1.57, worst-case bounded to −77% to −166%
(survivable with sizing), and it is 90% of the book. **EXCLUDE `chg_1d >= +20%`** (the squeeze zone) OR gate
it behind a hard stop — the backtest avg% there is a mirage that a live squeeze erases. The down-day peak
(−5..−2%, PF 1.912) is the core, and it OVERLAPS the long book's universe.

⏭ Next: the reset counters (is a 5th consecutive new high a fade or a runaway?) — F8's tail suggests the
squeeze answer. Then a hard-stop design targeted at the residual runaway risk.


---

## Finding 9 — ⭐ session high & session-volume high BOTH make the short WORSE — every quality signal says "fade the WEAK pop"

**User:** *"what if we used the session high instead of the 20m high for the short trades? What if we also
went short only on a new high in session volume? Note the session volume high for the previous bar as a
feature."*

Added three recorded features (running `RunMaxMa`, strictly-prior — no lookahead): `is_new_sess_high`
(is this 20m-high entry ALSO a session close-high?), `is_new_sess_vol_high` (did this bar make a new session
1m-volume high?), `prev_sess_vol_high` (the session vol-high AS OF THE PRIOR BAR — the user's feature).

**Frequencies (dv ≥ $3M, ATR band):** 29.7% of short entries are session highs; only **1.4%** are new
session-VOLUME highs (a rare condition); 1.1% are both.

### Both ideas make the short WORSE — and in the SAME direction

**1. SESSION high vs 20m-only high:**

| entry | n | win% | avg% | **PF** |
|---|---|---|---|---|
| **20m-only high (below session)** | 1,686,938 | 69.8 | 0.279 | **⭐ 1.644** |
| SESSION high | 714,015 | 67.0 | 0.202 | **1.353** |

**2. NEW session-VOLUME high vs not:**

| | n | win% | avg% | **PF** |
|---|---|---|---|---|
| NEW sess-vol high | 33,487 | 64.5 | 0.355 | **1.337** |
| not | 2,367,466 | 69.0 | 0.255 | **1.547** |

**Both CUT PF hard (1.64→1.35 and 1.55→1.34).** The mechanism is F6's, in its most extreme form: **a new
SESSION high is the name at its STRONGEST point of the day — the worst thing to fade; a new SESSION-VOLUME
high is a volume climax = real conviction that keeps going.** Both are the intuitive momentum instinct
("short the thing making new highs on big volume") — and the data says the OPPOSITE, consistently.

### ⭐ The session-high effect is INDEPENDENT of chg_1d (not a proxy)

| chg_1d | 20m-only PF | SESSION-high PF |
|---|---|---|
| DOWN (< −2%) | 1.595 | 1.414 |
| flat (−2..+10%) | 1.743 | 1.326 |
| UP (≥ +10%) | 1.603 | 1.365 |

The 20m-only high beats the session high in EVERY chg_1d band (even among down-day names, 1.595 vs 1.414).
**A name at its session peak is genuinely harder to fade regardless of its day-return** — a standalone
quality signal, not a chg_1d artifact.

### ⭐⭐ The unifying picture: EVERY short quality signal says FADE THE WEAK POP

| signal | the GOOD short | the BAD short |
|---|---|---|
| VWAP (F4) | pop that FAILS below VWAP | pop extended far above |
| volume (F6) | QUIET pop | volume-SPIKE pop |
| session high (F9) | 20m high still BELOW session high | new SESSION high |
| session vol (F9) | ordinary volume | new SESSION-VOL high |
| chg_1d (F8) | moderately DOWN name | ripping UP name (squeeze) |

**They all point the same way: the fadeable short is a HOLLOW, EXHAUSTED, unsupported pop — never a strong
breakout.** This is coherent and it is the opposite of what a momentum trader would reach for. The two
`is_new_sess_*` flags are best used as **EXCLUSIONS** (skip session-highs and vol-high bars), not gates to
require.

⏭ Next: the reset counters (V6 F1 mirror) — is a 5th consecutive new high a fade or a runaway? F8's squeeze
tail predicts the latter, and this finding (session highs are worse) reinforces it.


---

## Finding 10 — session-high × vol-high 2×2: they INTERACT (not just "double bad") — a blow-off vs a reversal-up trap

**User:** *"What if we combined both a session high and volume high?"*

| cell | n | % | win% | avg% | p1 worst | **PF** |
|---|---|---|---|---|---|---|
| **not-high × not-vol** | 1,679,917 | 70.0 | 69.8 | 0.281 | −6.2% | **⭐ 1.650** |
| **not-high × volHIGH** | 7,021 | 0.29 | 59.9 | **−0.034** | −11.6% | **0.960** |
| sessHIGH × not-vol | 687,549 | 28.6 | 67.0 | 0.192 | −7.4% | 1.349 |
| **sessHIGH × volHIGH** | 26,466 | 1.1 | 65.7 | **0.459** | **−16.9%** | **1.413** |

**The two flags INTERACT — combining them is NOT simply additive:**

**1. `volHIGH × not-high` is the WORST cell (PF 0.960, negative avg%)** — a volume spike that is NOT a new
session high, i.e. aggressive buying pushing hard into a level BELOW the session high. That is a
**reversal-UP** signal (the surge resolves upward, not exhausting), so it is the one place fading loses.
⚠ **But it is NOT robustly negative** — per-year PF ranges 0.71 → 1.47 (negative in 4 of 7 years, positive
in 3; the aggregate 0.96 is dragged by 2020–21). On 7,021 regime-split trips it is a WEAK/inconsistent cell,
not a durable losing book. Still clearly the worst of the four and not tradable as a fade.

**2. `sessHIGH × volHIGH` RECOVERS to 1.413 — BETTER than session-high alone (1.349).** A new SESSION high
ON a volume spike is a **BLOW-OFF CLIMAX** (the classic exhaustion top) and fades better than a QUIET grind
to a new session high. avg% is the highest in the table (0.459%). **⚠ BUT p1 = −16.9%, the fattest tail
here** — the squeeze-zone signature (good average, catastrophic worst case), tradable only with a stop.

### The reframe: combining SPLITS the vol-high names by price location

The session-high flag partitions the (rare, 1.4%) vol-high bars into two opposite setups:
- **vol-high BELOW session high** → the reversal-up trap (0.96) — **exclude.**
- **vol-high AT session high** → a fadeable blow-off (1.413) with a fat tail — **stop required.**

So F9's "exclude vol-high bars" was too blunt: the vol-high names are not uniformly bad, they are
BIMODAL, and the session-high flag tells the two apart. This does NOT change the core book (the tradable
bulk is `not-high × not-vol`, PF 1.650, 70% of the book) — it refines the tail handling.

⭐ **Net for the production filter:** the clean core is **NOT a session high AND NOT a vol-high** (PF 1.650).
The vol-high tail is a separate, stop-gated, blow-off sub-strategy — not part of the core fade.


---

## Status / next

⏭ **The V6 levers all need re-measuring on the short side — do NOT assume they mirror.** The load-bearing
questions:
1. **The counters (V6 F1).** Long: the edge was in the BLEEDERS (fade the 5th consecutive dip). Short: is a
   name making its 5th consecutive new HIGH a fadeable exhaustion — or a **SQUEEZE that keeps going**? The
   unbounded tail makes this the most important asymmetry to check.
2. **VWAP side (V6 F6).** Long favoured BELOW-VWAP. Does short favour ABOVE-VWAP (the clean mirror), or does
   the "distance from VWAP in either direction" reading hold?
3. **chg_1d (V6 F11).** Long bought names moderately DOWN. Does short fade names moderately UP — and does the
   `≥ +50% RIPPING` cell that had "no rescue" long become the WORST short cell (a real squeeze)?
4. **ATR ceiling (V6 F17).** The long tail 0.035–0.08 was profitable under a 5m exit but is exactly the
   hard-to-borrow zone. Re-measure, then discount for borrow.
5. **The exit window (V6 F16).** 5m dominated long. Re-sweep — the squeeze risk may favour an even faster
   cover.

**A STOP is not optional here** (unlike V6). Design one before any tradable claim: a % stop or a break of
the up-leg's prior structure. Report the PF sacrifice honestly.
