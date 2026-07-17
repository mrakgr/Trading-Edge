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

**⭐ The short PF is below the long in EVERY year, by a consistent 0.05–0.31.** That asymmetry is the
well-known structural one: **stocks drift UP over time, so fading pops fights a gentle tailwind that fading
dips does not.** It is not a flaw — it is the market's long bias appearing exactly where it should.

**⚠ It is the SAME SETUP FROM THE OTHER SIDE, NOT A DIVERSIFIER.** Both books are strongest in 2023 (1.80 /
1.88) and weakest in 2026 (1.33 / 1.39) — the **same year-to-year shape**. The two will move TOGETHER, not
hedge each other. What the short side delivers is the user's stated goal — **MORE TRADES**: 2.4M short +
2.6M long on the same infrastructure, ~2× the capacity.

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
