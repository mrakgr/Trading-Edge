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

## S8 — the COST MODEL, part 1: budgets (breakeven 39-57bp) + the price-regime discovery (~90% of trips are SUB-$1 → sub-penny ticks; fees & Reg-T become the real questions)

**Budgets — mc=1 cell PF under a flat round-trip cost:**

| cell (mc=1) | PF@0 | @10bp | @20bp | @30bp | @50bp | breakeven |
|---|---|---|---|---|---|---|
| LONG A+ sess-high × ign | 3.065 | 2.370 | 1.853 | 1.469 | 0.961 | 48bp |
| LONG B off-high | 2.750 | 2.074 | 1.596 | 1.252 | 0.808 | 40bp |
| SHORT A′ sess-low | 3.083 | 2.462 | 1.988 | 1.625 | 1.123 | **57bp** |
| SHORT B′ off-low | 3.200 | 2.298 | 1.692 | 1.278 | 0.778 | 39bp |

Everything survives 20bp; nothing survives 50bp (except A′ barely). THE question: real RT cost vs ~20bp.

**The price-regime table (raw entry px = entry_px/adj_ratio):**

| cell | <$1 subpenny | $1-2 penny-tick | $2-5 | ≥$5 | med px |
|---|---|---|---|---|---|
| A+ long | 88.9 | 3.9 | 3.6 | 3.7 | $0.27 |
| B long | 88.6 | 3.7 | 3.6 | 4.0 | $0.27 |
| A′ short | 90.6 | 4.6 | 2.4 | 2.4 | $0.24 |
| B′ short | 90.7 | 2.9 | 3.0 | 3.3 | $0.26 |

**~90% of the book is sub-$1** — sub-penny quoting (min spread at $0.25 = $0.0001 = 4bp): the
tick-size threat (penny tick at $1.50 = 67bp minimum spread, above every breakeven) applies to only
3-5% of trips. Consequences that REFRAME the cost model:
1. **Fees become %-of-value**: at $0.25, 10k notional = 40k shares; sub-$1 ECN taker fees are
   ~0.1-0.3% OF VALUE per side → possibly 20-60bp RT from fees ALONE. Not measurable from the tape —
   comes from the broker/venue schedules (user input; likely the deciding line).
2. Spread measurement (Level 2, trades tape): within-second price dispersion + Roll estimator on the
   actual signal seconds — "how far above the 4bp floor do sub-$1 in-play spreads sit?"
3. **⚠ Reg-T short landmine: sub-$5 shorts margin at max($2.50/share, 100%)** — shorting a $0.25
   name consumes ~10× notional in margin. A′/B′ per-trip economics survive; CAPITAL EFFICIENCY on
   the deepest wreckage is savaged (on top of borrow). The short book may need a ≥$1 restriction or
   resizing.

⏭ Level 2 (measured spreads from `/mnt/d/trading-edge-bulk/trades` on sampled trip events) +
Level 3 (worst-print replay bound) + the broker fee schedule.

### S8b — CAPACITY + FEES (user questions): the honest reckoning — ~$1-5k/bar fill capacity, IBKR Pro %-caps over budget; the edge is real but SMALL-CLIP

**⚠ adj_ratio bug in the first capacity pass:** `bar_vol × signal_vwap` mixes raw shares with
ADJUSTED price — overstates dollar volume by adj_ratio (large for reverse-splitters). Corrected
(`× signal_vwap/adj_ratio`):

| cell | med signal-bar $vol | p25 | p10 | % bars <$100k | ≥$2 subset med |
|---|---|---|---|---|---|
| A+ long | $5,435 | $2,300 | $1,026 | 98.7 | $21k |
| B long | $4,483 | $1,812 | $794 | 99.1 | $20k |
| A′ short | $977 | $105 | $12 | 98.6 | $8.6k |
| B′ short | $3,894 | $1,704 | $788 | 99.3 | $12k |

**10k notional = 2-10× the ENTIRE fill bar's volume — the vwap fill is fiction at that size.** At
5-10% participation (even spread over a few bars), the realistic clip is **~$200-2,000/trip**. The
$395k @10k four-cell book scales to ~$20-80k over 3.5y at honest sizes: a genuine but SMALL-CLIP
edge — consistent with the small-account frame, not beyond it.

**Fees (researched 2026-07-25):**
1. **IBKR Pro is over budget sub-$1**: Fixed = $0.005/sh, min $1, **max 1% of trade value**/side;
   Tiered **capped at 0.5% of value**/side — at $0.25/share the caps BIND → 100-200bp RT vs 39-57bp
   breakevens. IBKR Lite: sub-$1 orders commission-FREE but only ≤10% of monthly volume — inverted
   for a book that is ~90% sub-$1 (excess charged min($0.005/sh, 1%) = 1% at these prices).
2. **The structural floor is the Reg NMS 610(c) access-fee cap**: sub-$1 taker fees cap at 0.3% of
   quote price/side today (≈60bp RT — marginal-to-dead) but the 2024 SEC amendments REDUCE it to
   **0.1%/side (≤20bp RT — inside every budget) with compliance from Nov 2026** — months away.
   The same amendments bring half-penny quoting to eligible ≥$1 names (halves the $1-2 zone's floor).
3. **No ≥$2 refuge**: long ≥$2 cells 1.4-1.7 (F25 — the edge IS the wreckage); short ≥$2 PFs
   (12.6/3.5) are 73-83% top-3 concentration = collapse-day artifacts, recorded-not-believed.

**Verdict (corrected after user pushback on the maker claim):** the tape-level edge is real and
confirmed, but extraction is constrained to SMALL CLIPS (~$0.5-2k/trip), and the cost lines split:
1. **COMMISSION is order-type-INDEPENDENT — a BROKER-selection problem.** IBKR Pro's %-of-value
   caps (0.5-1%/side) kill sub-$0.50 regardless of limit vs market. ⚠ **EU-RESIDENT reality
   (user is in Croatia; IBKR Lite = US-only)**: the US zero-commission retail routes are OUT —
   Robinhood EU = tokenized large caps only (no real microcaps, no stock API), Webull EU = UK/NL
   only, Schwab International = country-limited. The programmatic candidates: **Alpaca**
   (international + API-first + commission-free, BUT a 2022 report of sub-$1 orders routed
   non-retail at 40 mils/share = 1.6%/side at $0.25 — CURRENT policy must be verified with
   support) and **TradeZero International** (built for non-US residents, small-cap/locate
   specialist — API maturity + sub-$1 schedule need a direct check). IBKR = the ≥$2 fallback.
2. Venue access fee: taker 0.3%→0.1%/side Nov 2026; makers exempt/rebated.
3. Spread: maker flips pay→earn BUT momentum entries suffer textbook ADVERSE SELECTION on resting
   fills (winners run unfilled, losers fill through you) — unmeasurable without quote data; this
   book is taker-natured on both legs. Do NOT credit maker economics without quotes.
**THE deciding number is the effective spread on sub-$1 in-play tapes = Level 2 (trades-tape
measurement).** Everything stays pre-borrow for shorts (+ Reg-T $2.50/sh sub-$5 margin, S8).

### S8c — the broker verdict (2026-07-25) + THE V2 PIVOT

Broker sweep for an EU (Croatia) resident: IBKR Lite = US-only; Alpaca = rejects Croatian
residents (user, first-hand); Clear Street = no EU; Robinhood EU = tokenized large caps; Webull =
UK/NL; Schwab Intl = country-limited. **TradeZero International** (the non-US-resident entity):
sub-$1 = $0.005/sh min $0.99, NO free-limit lane (≈2%/side at $0.25 — the sub-$1 book is priced
out on EVERY EU-accessible route). BUT: **Developer API launched May 2026** — REST+WebSocket
execution, free to enable, paper env, and a **Short Locate API** (programmatic locate quote/
reserve/sell-back) that turns the borrow gate into a QUOTABLE cost-model input. ≥$1: free
non-marketable limits (≥200sh); marketable $0.005/sh = ≤25bp/side at $2+.

**⏭ THE V2 PIVOT (user decision): SurgeRider/PlungeRider V2 — universe ≥$2 raw price, RELAXED
rvol, test whether tape acceleration (ignition) + eff carry an edge on cleaner stock.** ⚠ F25's
"edge dies ≥$2" was measured INSIDE rvol≥10 wreckage — the relaxed-rvol ≥$2 universe is unexplored;
prior findings neither doom nor guarantee it. Execution story if it works: TradeZero API, ≤25bp/side
marketable, locate API for shorts.

## S9 — ⭐⭐ THE V2 VERDICT: comprehensively NEGATIVE — nothing in the V1 stack transfers to ≥$2 stocks; the edge IS the wreckage regime

**Setup:** `--min-prev-close 2` (prior close in day-D raw scale — new engine flag), rvol gate OFF,
no band, e60/x60, 2023→2026: **97,642 tkd → 39.1M (long) / 38.6M (short) trips**, raw PF
1.074/1.072. Runs: `surge23_v2_e60_x60_noband`, `plunge23_v2_e60_x60_noband` (~10GB each; ⚠ next
time scout with the 60d window first — this cost 2×100min).

**Every V1 structure fails on this universe (both sides symmetric):**

| structure | V1 (sub-$1 wreckage, in-play) | V2 (≥$2, all rvol) |
|---|---|---|
| vol band | 7-20bp peak: 2.69 short / 2.12 long-cell | FLAT 1.04-1.12, no peak |
| rvol (in-play) | THE precondition (rvol≥10) | INVERTS: rvol≥10 = 0.92 long / 1.02 short; rvol<1 "best" 1.09-1.11 |
| band × ignition × eff-hi | 3.0-3.3 | 1.12-1.16, ret +0.015-0.02% |
| best cell anywhere | 3.6-5.0 | 1.32-1.45 (no-band ign, n≈41k) at +0.03-0.14%/trip |
| **session-extreme step** | interior 1.6-1.8 → 2.26-2.54 | **GONE: sess 1.04-1.05 ≤ off 1.07; ign-sess 1.11-1.21** |

Even the best V2 cells earn +0.03-0.14%/trip — under the ≥$2 cost line (~20-50bp RT marketable)
everything is deeply negative. **Reading: the V1 edge is a property of the WRECKAGE REGIME, not of
the tape mechanics abstractly.** On a sub-$1 day-trader-rotation name the session is the entire
story — no institutional anchor, no multi-day memory — so session extremes are real supply
boundaries and 1s participation bursts are the marginal buyer. A ≥$2 stock has memory beyond the
session (holders, levels, institutions): its session high is not "no supply above," and its 1s
bursts are noise inside bigger flows. The machinery measured this honestly on 78M trips: there is
no V2 system in this signal class.

**The strategic fork after S9:** (a) the V1 book goes in the DRAWER awaiting extractability (Nov
2026 access-fee cut; half-penny quoting for eligible ≥$1 names; broker landscape shifts); (b) the
$1-2 in-play sliver (TradeZero free-limit eligible, half-penny regime coming) — thin n in V1, worth
a targeted look; (c) a DIFFERENT signal class for real stocks (longer timeframes — the 1s scalp
layer is the wrong microscope for institutional tape); (d) the S9b survivor below.

### S9b — the ONE V2 survivor (user: "what is the best cell?"): the HOT-TAPE SHORT on ~$5 stocks — real, modest, and it clears its cost line

Decomposing the no-band ignition cells: the quiet<7bp side = $60-80 mega-caps at +0.006% (noise);
the HOT side is the survivor. **SHORT × vol≥40bp × ignition × eff-hi: PF 1.579 / +0.326%/trip /
n=5,627 / 1,810 tkdays / median stock $5.42, median rvol 2.8** (+ the eff-NULL early sibling:
1.378 / +0.20% / n=27,781 / 7,754 tkd @ $6.26). By year × sign: continuation (short into a live 20m
decline) 1.67/1.43/1.80/1.30, reversal 1.25/1.78/1.47/1.24 — positive EVERY year, both flavors, no
artifact. This is V1-S4's blowoff-short signature surviving on real stocks. At $5-6 prices the cost
line (~15-25bp RT marketable at TradeZero) leaves net +0.1-0.3%/trip at PF ~1.3-1.5 — THIN but the
only V2 cell where edge and extractability overlap. ⚠ Untuned: this cell inherited V1's knobs
(e60/x60, [10,40) prints, 0.40 eff edges) — an exit sweep / sess-tier / price-floor pass on ITS
universe is the obvious next step if pursued.

**The fine vol ladder inside the survivor (user: target the high bands — confirmed MONOTONE):**

| band | n | win% | ret% | PF | tkd | med px |
|---|---|---|---|---|---|---|
| 40-80bp | 3,933 | 42.3 | +0.152 | 1.368 | 1,401 | $7.21 |
| **80-160bp** | 1,376 | 46.0 | **+0.541** | 1.674 | 430 | $3.51 |
| ≥160bp | 318 | 46.2 | +1.548 | 2.118 | 62 ⚠ audit-first | $2.13 |

Per-trip ret scales ~10× up the ladder; median price slides toward the $2 boundary with it — the
system pulls back toward wreckage from every angle. **Candidate band: [80,160)bp @ ~$3.50 —
+0.54%/trip clears ~20-30bp RT with margin, 430 tkd.** ⚠ Universe-dependent inversion worth
recording: hot-band breakdown-riding was a BUST on V1 wreckage (S1/S4 squeeze violence) but is the
sole V2 survivor — the same tape state is a squeeze crowd at $0.25 and genuine liquidation at $3-7.

**The LONG twin (SurgeRider V2, user question): same family at ~2/3 strength — the hierarchy of V1
REVERSES.** Hot × ign × eff-hi: 40-80bp 1.340/+0.138 (n=3,467); **80-160bp 1.482/+0.357 (n=1,076 /
431 tkd @ $3.75)**; ≥160bp 1.881/+1.177 (n=181 ⚠). The early-session NULL variant is WEAK on the
long (1.14-1.22 vs short 1.37-1.38). Reading: on real stocks in violent tapes, DOWNSIDE
continuation (stop cascades, liquidation) beats upside continuation (breakouts hit profit-taking) —
the MaxRiderV1 asymmetry at 1s scale. If pursued: the SHORT is the flagship, the long the optional
second leg — the reverse of V1. Long clears its ~25bp line at net ~+0.1%/trip only.
