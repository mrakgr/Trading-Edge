# PlungeRider — the short-side 1s momentum campaign

The SHORT mirror of SurgeRider (see `docs/surgerider_results.md` F1-F28b for the shared machinery,
feature pedigree, and the long-side map). Engine: `TradingEdge.PlungeRider` (forked 2026-07-25,
branch `plunge-rider`) — entry = channel-LOW breakdown, stop = high-side channel, `ret_exit = 1 −
exit/entry` (positive = the short made money), breach counters are LOW-side (`breach_sess = 0` = at
the session low), leg pair resets on new 20m HIGHS, aux marks = first new {2,5,10,20}m lows
(`aux_lo_*`). Direction-neutral features identical; post-hoc SQL ports directly.

⚠ Everything here is mc=0 ATTRIBUTION, pre-cost, and — short-specific — pre-BORROW: sub-$2 in-play
wreckage is the hardest locate in the market. ⚠ long-PF<1 cells do NOT automatically short (the
squeeze tail flips sides); every cell must earn its own confirmation.

Baseline dataset: `data/equity/surgerider/plunge23_e60_x60_noband/` — 2023-01-01 → 2026-07-17,
in-play (`--min-rvol-0945 10`, dv_0945 ≥ $10M), simplest system (60-bar entry / 60-bar stop, z-exits
off), **NO vol band** (user: start from the beginning). 16,349 ticker-days → **3,004,832 trips**,
raw PF 1.104 / 40.7% win.

## S1 — the vol_20m ladder (unbanded): the short edge is in the QUIET band too — the ≥40bp blowoff region is a bust

| vol_20m | n | win% | ret% | PF | tkdays | med hold |
|---|---|---|---|---|---|---|
| <2bp | 5,382 | 42.5 | −0.003 | 0.865 | 70 | 113 |
| 2-7bp | 6,337 | 40.8 | +0.068 | 1.875 | 150 | 123 |
| **7-20bp** | 50,527 | 50.6 | +0.270 | **2.692** | 964 | 127 |
| 20-40bp | 251,791 | 45.9 | +0.226 | 1.790 | 3,964 | 104 |
| 40-80bp | 1,019,628 | 41.4 | +0.134 | 1.238 | 10,670 | 90 |
| ≥80bp | 1,671,167 | 39.2 | +0.030 | 1.027 | 12,655 | 84 |

90% of unbanded in-play breakdowns fire ≥40bp and there the short barely breaks even. **The
F14b/F16 "blowoff = PlungeRider material" flag does NOT transfer to breakdown-riding** — those
findings were about fading long exits at tops; riding breakdowns inside an 80bp/30s tape gets
whipsawed by squeeze violence. The short sweet spot is 7-20bp — QUIETER than the long's [7,40):
an orderly decline continues, a violent one snaps back. Both directions monetize ORDERLY momentum.

## S2 — the tc-ignition on the short side: stacks in every vol bucket; does NOT substitute for the band (user's mechanical question)

Ignition = `tc_60 ≤ 300 AND bar_tc ∈ [10,40)` (the F26b long-side lock, applied unchanged):

| vol_20m | n all | n ign | surv% | ign win% | ign ret% | ign PF | ign tkd |
|---|---|---|---|---|---|---|---|
| <7bp | 11,719 | 421 | 3.6 | 47.7 | +0.202 | 5.201 ⚠ thin | 120 |
| 7-20bp | 50,527 | 5,843 | 11.6 | 55.5 | +0.280 | **3.067** | 742 |
| 20-40bp | 251,791 | 29,303 | 11.6 | 49.4 | +0.300 | **2.155** | 3,385 |
| 40-80bp | 1,019,628 | 103,686 | 10.2 | 44.3 | +0.233 | 1.466 | 9,490 |
| ≥80bp | 1,671,167 | 108,225 | 6.5 | 43.7 | +0.243 | 1.267 | 10,424 |

The quiet-tape leg thins ≥80bp only to 6.5% survival (vs 11.6% in the sweet spot) — **price
volatility and print-count busyness are far-from-identical axes in the wreckage universe** (huge
moves on few-but-market-moving prints), so 86% of ignition survivors still sit ≥40bp at PF
1.27-1.47. The vol band stays load-bearing; the ignition is a stacking lever (×1.2-1.9 in every
bucket), not a substitute. **Short cell candidate: ignition × vol ∈ [7,40)bp — ~35k trips, blended
~2.3, the [7,20) core at 3.07.**

## S3 — eff stacked on the short cell: the long side's map reproduces to TWO DECIMAL PLACES

Cell = ignition × [7,40)bp. eff_20m states (short sign convention: eff < 0 = 20m DECLINE = the
short's continuation):

| eff_20m state | n | win% | ret% | PF | tkdays | long-side twin |
|---|---|---|---|---|---|---|
| hi-down (continuation) | 2,267 | 57.0 | +0.520 | **3.646** | 608 | cont 3.607 (F28b) |
| NULL (early session) | 4,785 | 55.1 | +0.621 | 3.434 | 1,391 | NULL×ign 2.868 (F27) |
| hi-up (reversal: rally rolls over) | 927 | 55.4 | +0.365 | 2.559 | 414 | rev 2.589 (F28b) |
| lo (chop) | 27,167 | 48.9 | +0.219 | 1.910 | 2,993 | lo×ign 1.876 (F27) |

Continuation 3.65 vs 3.61, reversal 2.56 vs 2.59, chop floor 1.91 vs 1.88 — **a structure neither
side was fitted to, agreeing across directions**: ignition print + live trend in the trade's
direction, whichever way it points. e20×e10 cross (t-adj hi ≥ 0.566 for 10m): `e20lo × e10dn`
rescues the chop bucket (3.166 / n=1,236 / 523 tkd — young declines the 20m hasn't confirmed);
both-horizons-down adds only a sliver on e20dn (3.702 vs 3.596); ⚠ `e20up × e10up` (shorting into a
rally live at both horizons) = 2.973 / n=359 — a sharp-snap cell, recorded not believed.

**The emerging short map (pre-confirmation):** ignition × [7,40)bp × (eff-down-or-NULL, e10dn
rescue for chop) ≈ 3.2-3.7. Next: the session-LOW tier scan (breach_sess = 0 — the tier-A analog),
year-stability, concentration audits, then the borrow/cost reality check.

## S4 — does eff rescue the high-vol bands? (user) PARTIALLY — real lift, but the vol ceiling stays binding

Within ignition, the ≥40bp bands × eff states:

| vol | eff | n ign | ign ret% | ign PF | tkd |
|---|---|---|---|---|---|
| 40-80bp | hi-down (cont) | 3,713 | +0.443 | 1.902 | 1,154 |
| 40-80bp | hi-up (rev) | 1,386 | +0.415 | 1.875 | 628 |
| 40-80bp | lo | 80,519 | +0.202 | 1.405 | 7,379 |
| ≥80bp | hi-down (cont) | 3,728 | +0.523 | 1.588 | 1,079 |
| ≥80bp | hi-up (rev) | 568 | +0.953 | 2.227 ⚠ | 266 |
| ≥80bp | lo | 68,102 | +0.185 | 1.216 | 6,521 |

eff-hi beats chop by ~+0.5 PF in both bands (the direction is real), but the fully-stacked high-vol
cells top out at 1.6-1.9 vs 3.4-3.7 in-band — whipsaw eats the follow-through regardless of trend
confirmation. The band stays in the short spec. ⚠ recorded-not-believed: **≥80bp × hi-up × ign =
+0.953/trip, PF 2.227, n=568/266 tkd** — the first appearance of the F16 blowoff-REVERSAL shape
(violent tape, rally rolls over); if pursued, the vehicle is a top-fade entry, not breakdown-riding.

## S5 — SESSION-LOW breakdowns: the F24 step MIRRORS (interior 1.6-1.8 → sess-low 2.54) — but unlike the high side, the tier has internal structure

**The breakdown-horizon ladder (banded [7,40)bp; longest channel LOW broken at entry, exclusive):**

| rung | n | win% | ret% | PF | tkdays |
|---|---|---|---|---|---|
| 1m only | 82,071 | 44.9 | +0.173 | 1.723 | 3,655 |
| 2m | 76,453 | 46.7 | +0.197 | 1.802 | 3,500 |
| 5m | 68,789 | 46.5 | +0.209 | 1.779 | 3,243 |
| 20m | 31,645 | 45.7 | +0.185 | 1.608 | 1,922 |
| **session low** | **43,360** | **51.2** | **+0.489** | **2.538** | 1,980 |

Same discontinuity, mirrored mechanism: at a fresh session low there is NO cohort of intraday
buyers below whose break-even bids provide support — support is as binary as F24's resistance.
(Unbanded, the interior rungs go NEGATIVE — 0.78-0.96 at 5m/20m; the band matters more here.)

**Tier structure (banded) — NOT context-free like the long's Cell A:**

| eff_20m | no ignition | + ignition |
|---|---|---|
| NULL (early session) | **3.727 / +0.950 / n=9,832 / 1,028 tkd** | 3.665 / n=1,395 |
| hi-down (decline live) | 2.255 / n=10,485 | **3.533 / n=905 / 298 tkd** |
| lo (chop) | 1.922 / n=19,060 | 2.372 / n=1,682 |
| hi-up | n=1 — mechanically void (a 20m uptrend can't coexist with a fresh session low) | — |

Three reads: (1) eff is NOT flat here — ignition lifts hi-down 2.26→3.53 and chop 1.92→2.37, so the
sess-low tier wants structure AND context; (2) **the early-session bucket is the crown: NULL-eff
sess-low breaks run ~3.7 WITH OR WITHOUT the ignition** — a new session low in the first ~20 min on
an in-play name is prime short territory by itself, and it is the tier's biggest cell; (3) hi-up is
void by construction, which is WHY this tier can't be context-free the way sess-high was.

### S5b — the 30s razor stop + audits: sess-low × decline-live × ignition = PF ~5 EVERY YEAR — the campaign's best-auditing cell, either side

Banded e60/x30 run (`plunge23_e60_x30`: 302,318 trips, whole-sampler PF 2.112 vs the long's 1.713
equivalent). The sess-low tier at 30s vs 60s stops:

| eff × ign | 60s PF | 30s PF | 30s win% |
|---|---|---|---|
| NULL, no ign | 3.727 | 3.763 | 54.7 |
| NULL × ign | 3.665 | 3.342 | 58.9 |
| lo, no ign | 1.922 | 2.444 | 51.6 |
| lo × ign | 2.372 | 3.391 | 58.4 |
| hi-down, no ign | 2.255 | 2.807 | 55.1 |
| **hi-down × ign** | 3.533 | **4.953** | **63.3** |

The razor lifts every cell except ign-NULL. **Audits + year stability of the two headline cells:**

| cell | tkd | syms | top-3% | tkd+ | PF 23/24/25/26 |
|---|---|---|---|---|---|
| sess-low × hi-down × ign × 30s (n=905) | 298 | 98 | 17.3 | 63.1 | **4.78 / 4.78 / 5.40 / 5.00** |
| sess-low × NULL-early × 30s (n=9,832) | 1,042 | 185 | 26.2 | 47.4 | 3.06 / 3.63 / 4.71 / 5.01 |

PF ~5 four years running at 17% top-3 concentration — the best-auditing cell of the entire
two-sided campaign; the early-session cell RISES monotonically across years.

**THE SHORT MAP (pre-cost, pre-borrow, mc=0):**
- **Cell A′ — sess-low @30s stop × (decline-live × ignition | NULL-early): PF ~3.8-5.0**
- **Cell B′ — off-low × ignition × [7,40)bp × eff-down-or-NULL: PF ~3.4-3.5 (stop-insensitive, see S6)**
- The full mirror of the long map, at HIGHER levels. ⚠ borrow on sub-$2 wreckage + costs = the gate.

## S6 — 30s vs 60s stop ACROSS SIDES (user question: is 60s better on the short side?): NO — the razor's edge is LARGER on shorts, in both tiers

Paired on identical trip populations, banded:

| side | tier | 30s PF | 60s PF | razor edge | 30s hold | 60s hold |
|---|---|---|---|---|---|---|
| long | sess-extreme | 2.259 | 2.105 | +0.15 | 69 | 125 |
| **short** | **sess-extreme** | **2.962** | 2.538 | **+0.42** | 96 | 157 |
| long | off-extreme | 1.644 | 1.587 | +0.06 | 69 | 127 |
| short | off-extreme | 1.937 | 1.744 | +0.19 | 104 | 165 |

Mechanism: short-side adverse moves are SNAP-BACK SQUEEZES — fast and violent — so a loose stop
gives back far more on a short; meanwhile declines GRIND (short holds ~40% longer than long at the
same stop). Slow-grinding profit + fast-snapping risk → the tight stop is worth more short.
**Cell B′ specifically is stop-insensitive** (30s 3.503 / +0.419 vs 60s 3.416 / +0.540 on the same
4,752 trips) — 30s slightly better risk-adjusted, 60s better per-trip; A′ strongly prefers 30s.

## S7 — ⭐ mc=1 (both sides): the attribution SURVIVES sequential execution — cells compress 0-12%, books are real

Two layers. (1) ENGINE mc=1 (`--max-concurrent 1`, one position per ticker at a time, hard gates
only — banded in-play e60/x30): **SurgeRider book PF 1.669 / 46.5% / n=45,483 / +$587k @10k;
PlungeRider book PF 1.831 / 47.2% / n=46,332 / +$748k** (mc=0 attribution: 1.713 / 2.112 — modest
compression, REAL sequential PF numbers now). Runs: `surge23_mc1_e60_x30`, `plunge23_mc1_e60_x30`.
(2) CELL mc=1 via greedy non-overlap over the recorded trip times (the engine's exact rule: a
signal is taken only once the prior exit filled; `mc1_cells.py`):

| cell | n mc0 | n mc1 | win% | ret% | mc1 PF | (mc0 PF) | net @10k | tkd |
|---|---|---|---|---|---|---|---|---|
| LONG A sess-high composite | 8,088 | 1,526 | 53.8 | +0.420 | 2.707 | 2.778 | $64k | 796 |
| LONG A+ × ignition | 1,660 | 897 | 57.7 | +0.480 | **3.065** | ~3.05 | $43k | 567 |
| LONG B off-high ign × eff-live | 4,425 | 2,671 | 52.2 | +0.400 | 2.750 | ~3.0 | $107k | 1,653 |
| SHORT A′ sess-low ign×eff-dn\|early | 12,132 | 2,022 | 56.3 | +0.568 | 3.083 | ~3.5-3.7 | $115k | 1,208 |
| SHORT B′ off-low ign × eff-dn\|NULL | 4,752 | 2,765 | 55.6 | +0.395 | **3.200** | 3.50 | $109k | 1,516 |

Trip counts collapse up to 6:1 (the pyramiding removed) yet PF compresses only 0-12% and per-trip
ret holds or IMPROVES — **the first signal of each episode carries the same edge as the pile**; the
sampler's duplicates weren't propping the attribution. Four-cell book combined: ~7,000 sequential
trips, ~$395k @10k notional over 3.5y, pre-cost. Remaining gates: costs (spread on sub-$2 in-play)
and, short side, BORROW.
