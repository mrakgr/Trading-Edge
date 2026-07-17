# MaxRiderV1 — results

`TradingEdge.MaxRiderV1` (branch `diprider-v6-mean-reversion`). **Intraday MEAN REVERSION, the SHORT side.**
A direction-flipped mirror of DipRiderV6, forked 2026-07-17.

> **User:** *"Make a new project by forking DipRiderV6. We're going to do more research on mean reversion,
> but this time from the short side. That will be one way to get more trades."*

**Naming** follows the house convention — LowFlyer (long) / MaxFlyer (short) ⇒ DipRider (long) /
**MaxRider** (short). **Read `docs/diprider_v6_results.md` first**; this document only records where the short
side DIFFERS from the long.

**Standing conventions:** SHORT only. **2020-01-01 → 2026-06-30.** Raw MOC PF. $10k notional/trip.
`dv_0945 >= $3M`, ATR floor 0.004 (uncapped ceiling), 20m-high entry → 5m-low cover (V6 F16/F17 defaults).

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
