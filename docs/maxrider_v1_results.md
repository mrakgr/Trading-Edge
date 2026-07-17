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
