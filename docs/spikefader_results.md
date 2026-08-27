# SpikeFader — results

**The 1s SHORT mean-reversion fader** — `TradingEdge.SpikeFader`, forked 2026-08-26 from
`TradingEdge.FlushFader` (direction-flipped) per `docs/maxrider_1s_plan.md` and the SpikeFader
feature plan. Fades the POP: entry = new 20m HIGH (strict, prior-max), leg reset = new 20m LOW,
target = 5m-LOW cover, MOC backstop, 🛑 NO overnight holds (an overnight short gap is unbounded —
the ShortSnoozer asymmetry; `open_p1` recorded for post-hoc study only).

**Universe**: `mr_candidate_1s_v2` (causal, raw tape). ⭐ This engine IS the clean-universe rerun
MaxRiderV1's INVALID banner demands — its 1m PF 2.599 (quiet-vol × ADX≥40 × not-sess-high) is the
number to confirm or bury, with the S39d directional prediction (user): two lookahead channels
biased shorts conservative, one (future-reverse-split admission) inflated them.

## S0 — the fork (2026-08-26), verified

1. **Step 0**: FlushFader's volat-slope path replaced with the production `WindowRoller + OlsRoll`
   (full-window-only) BEFORE forking; old-vs-new diff on 2026-07: trip keys identical, all other
   columns zero-diff, full-window slopes within 1.5e-16, exactly the 285 partial-window base-run
   rows going valued→NULL.
2. **Step 1**: the direction flip (~420 count-asserted edits). Every one of the 15 directional
   gates sign-flipped with renamed config fields + mirrored off-sentinels; `NewHighCounters`
   (K = highs_since_first_high); arming-LOW family (`d_lo_flow`, `arm_lo_eff_*` via the new
   RollingMa `MinMaMeta`); tick runs swapped (upticks primary); aux_lo marks on the low-side
   breach counters; MA/VWMA marks cross BELOW; pnl/ret in SHORT convention
   (`qty·(entry−exit)`, `(entry−exit)/entry`).
   - ⭐ `eff_9ema` deliberately NOT flipped: its sign convention is already direction-symmetric
     (coherent trend reads positive both ways); the ≥ −0.10 whipsaw gate transfers unchanged.
   - 🔄 cascade/reopen gates **RECORD-ONLY by default** (user): limit-UP LULD economics are
     unmeasured short-side; `halts_today`/`secs_since_halt`/`halts_*` still recorded.
   - ⚠ the banner formatters lied for six gates after the flip (long off-conventions) — fixed;
     gates verified EMPIRICALLY: 0 violations across all 15 on every spec trip of the smoke month.
   - Sign populations as designed: speed/d_lo_flow/extension 100% positive, eff_20m 93%+ positive,
     **37-39% mass point at exactly the session high** (the dslo mass-point mirror, S43g/S41p).
3. **Step 2**: LongHiker grafts + exit re-sweep. `std_{20m,10m}` (+`_lag1m` twins, BOTH legs
   lagged 2 slots), `volat_20m_sessmax`, `eff_ewma_{20m,10m}`, `vr2/vr4_ewma`, `ac1-3_ewma`,
   `hi_rate_hl*`/`lo_rate_hl*` (strictly-prior — pushed at the END of Process; verified
   min(hi_rate_hl30)=0, impossible under an inclusive read), and aux_lo rungs at
   {3,4,6,7,8,9}m beside the inherited {1,2,5,10,20}m — the cover re-sweep is pure post-hoc SQL.
   - Grafts verified purely additive: smoke P&L byte-identical pre/post; zero nulls; zero aux
     nesting violations; ⭐ the free self-check holds — `aux_lo_300` equals the engine's own
     target fill on 99.83% of trips (= the target rate exactly).

**Smoke month (2026-07, mc=0 attribution — NOT a result)**: base 22,486 trips PF 2.085;
the transplanted FlushFader spec (untuned) 454 trips PF 3.569 / 79.7% win / target fires 100%.

⏭ Full pass 2020 → present next; session-1 tables per the plan: speed bands (expect the S36b
mirror above ~1-2%/min), the F18 reproduction (quiet-vol × smoothness-analog × not-session-high),
K band, year table 2026-FIRST, mc=1 + eqw + trim throughout; spreads early.

---

# Session 1 — the full 2020 → 2026-08-25 pass (autonomous run, 2026-08-26)

**Corpora**: `data/spikefader_base_v1` (2,397,710 trips, 1.9 GB, --base-run) and
`data/spikefader_spec_v1` (32,030 trips — the transplanted FlushFader spec, UNTUNED).
mc=0 attribution: base PF 1.334 / 66.9% win; spec PF 1.711 / 72.7% win.
⚠⚠ NOTHING here is cost-modeled: borrow, locates, SSR/uptick, spreads, and short-fill realism
(filling a short at next-bar vwap INTO a spike) are all unmodeled. Spreads remain the standing
unmeasured item for the whole program.

## S1 — the year table, 2026 FIRST (spec book, mc=1 with actual exit times)

**eqw +88.2 bp/day · median day +198.6 bp · 71.1% up-days · 7/7 years · trim +86.0 · 672 tkd/yr.**

| yr | tkd | eqw bp | med bp | up% | mc=0 PF |
|---|---|---|---|---|---|
| **2026** | 514 | **+90.7** | +207.2 | 68.5 | 1.284 |
| 2025 | 868 | +80.4 | +203.6 | 72.4 | 1.743 |
| 2024 | 632 | +129.4 | +247.2 | 75.5 | 2.234 |
| 2023 | 399 | +90.6 | +211.0 | 68.9 | 1.497 |
| 2022 | 467 | +87.2 | +175.6 | 73.4 | 2.003 |
| 2021 | 841 | +64.0 | +165.9 | 69.6 | 1.699 |
| 2020 | 750 | +87.3 | +197.4 | 69.2 | 1.679 |

⭐⭐ **2026 is POSITIVE and mid-pack at mc=1** — the first system in the 1s program whose worst
recent year still reads ~+0.9%/day equal-weight. ⭐ The magnitude is FlushFader-class (its long
spec ran median ~+2%/trip; the mirror reads the same scale), i.e. ~30× the LongHiker momentum
cells — consistent with [[the strategy memory]]: the fade side is where the edge lives.
⚠ mc=0 PF is much weaker than mc=1 eqw in 2026 (1.284) — averaging-up into pops hurts the
sampler book; the greedy single-position replay does not carry that cost.

## S2 — the MaxRiderV1 F18 reproduction: ⚠ the QUIET-VOLUME leg does NOT port

Base corpus, px>1, mc=1, single-lever slices:

| lever (1s analog) | tkd/yr | eqw bp | yrs | trim |
|---|---|---|---|---|
| all | 7,645 | +10.4 | 6/7 | +10.1 |
| **quiet** `(vol_60/60)/(vol_1200/1200) < 0.7` | 3,592 | **+0.2** | 4/7 | −0.3 |
| **loud** `> 1.3` | 5,810 | **+21.1** | **7/7** | +20.7 |
| **smooth** `|ols_r_1200| >= 0.85` (the ADX analog) | 3,735 | **+29.0** | **7/7** | +28.4 |
| not-session-high | 5,466 | +6.4 | 6/7 | +6.0 |
| AT session high | 4,440 | +3.8 | 5/7 | +3.2 |
| **F18 stack** quiet × smooth × not-high | 542 | **+1.7** | 3/7 | **0.0** |

💀 **The 1m PF 2.599 stack is DEAD on the 1s tape**: its quiet-volume leg INVERTS (loud pops fade
better than quiet ones, +21.1 vs +0.2), and the full stack reads trim-zero, 3/7 years. The
smoothness leg (ADX→|OLS r|) DOES port and is the strongest single lever; not-session-high points
the right way but is small. ⚠ Caveat: the 1s "quiet" analog (1m/20m volume rate) may not match
MaxRider's 1m-bar measure — but at this size the burden of proof flips to the old finding.
**S39d verdict: neither confirmed nor buried — SUPERSEDED.** The clean-universe short edge exists
(S1) and is bigger than PF 2.6's implied economics, but it does not live in MaxRider's cells.

## S3 — speed bands: ⭐⭐ the S36b boundary MIRRORED, exactly as predicted

Base px>1, pinned def `signal_vwap/vwap_60_prev − 1`, mc=1:

| speed | tkd/yr | eqw bp | med bp | up% | yrs | trim |
|---|---|---|---|---|---|---|
| 0-0.5% | 1,791 | −1.4 | +58.7 | 63.9 | 5/7 | −1.8 |
| 0.5-1% | 4,939 | +5.5 | +59.3 | 63.3 | 5/7 | +5.3 |
| 1-2% | 6,627 | +9.9 | +54.4 | 62.0 | 6/7 | +9.7 |
| 2-4% | 5,094 | +22.1 | +66.5 | 62.5 | **7/7** | +21.9 |
| 4-8% | 2,010 | **+71.0** | +140.4 | 67.0 | **7/7** | +70.3 |
| **8%+** | 628 | **+138.8** | +268.4 | 69.0 | **7/7** | **+134.9** |

⭐⭐ **Monotone RISING in speed, 7/7 years from 2%/min up — the exact complement of LongHiker's
S36b table.** The 1-2%/min boundary measured from the long side reappears from the short side:
below it the fade is dead-to-marginal (where the rider lived), above it the fade compounds without
limit in the measured range. The two systems partition the speed axis, each 7/7 in its own half.

## S4 — the cover re-sweep: the 5m target was NOT the optimum — longer is monotonically better

Spec book, mc=1, each rung's mark (fallback = real exit on the <1.5% unmarked):

| rung | fired | med hold | eqw bp | med bp | up% | yrs | trim |
|---|---|---|---|---|---|---|---|
| 1m | 100% | 89s | +5.8 | +64.7 | 65.0 | 4/7 | +4.2 |
| 2m | 100% | 184s | +33.2 | +105.0 | 67.7 | 7/7 | +31.3 |
| 3m | 100% | 275s | +52.9 | +138.1 | 69.2 | 7/7 | +50.8 |
| 4m | 99.9% | 357s | +71.5 | +168.6 | 70.4 | 7/7 | +69.3 |
| **5m (prod)** | 99.9% | 448s | +88.2 | +198.6 | 71.1 | 7/7 | +86.0 |
| 6m | 99.8% | 551s | +98.0 | +217.1 | 71.7 | 7/7 | +95.5 |
| **7m (V6-F16)** | 99.6% | 662s | +107.1 | +232.0 | 71.6 | 7/7 | +104.5 |
| 8m | 99.4% | 758s | +119.0 | +244.3 | 71.9 | 7/7 | +116.4 |
| 9m | 99.0% | 870s | +126.0 | +260.0 | 72.0 | 7/7 | +123.3 |
| 10m | 98.5% | 971s | +129.5 | +273.1 | 71.7 | 7/7 | +126.6 |
| 20m | 75.4% | 1922s | **+146.9** | +295.5 | 72.6 | 7/7 | +143.7 |

⭐ Monotone through the whole ladder — the pop keeps bleeding well past the 5m cover, and MaxRider's
1m-era 7m choice was directionally right but not the top either. ⚠ The gradient buys hold TIME
(median 7.5 → 32 min) — inventory, borrow-hours, and squeeze exposure scale with it; the risk-
adjusted pick is NOT simply the bottom row. User's call after costs.

## S5 — tails (spec book, trip level)

Worst −112.7% · p1 −25.7% · 0.022% of trips lose >100% · median +2.41%. The worst SIX rows are
one day (NRSN 2022-03-21, the sampler averaging up into a doubling squeeze) — mc=1 dedupes it but
the exposure class is real: **this is ShortSnoozer's tail wearing an intraday face. A real book
needs the stop design the V1 doc demanded, and locates on exactly these names are the open cost.**

## ⏭ Held for the user (no tuning done autonomously)

1. The spec is FlushFader's transplant — every gate deserves the S38-style re-derivation short-side
   (the K band, |eff20| band, z, dlv, ssf were all fitted on flushes).
2. Cover choice vs costs/borrow-hours; stop design (mandatory before any tradable claim).
3. SPREADS — still never measured; at +88bp/day the edge would survive costs the momentum cells
   could not, but that is an argument, not a measurement.
4. The loud-beats-quiet inversion (S2) deserves its own study — it contradicts the 1m lore and
   agrees with LongHiker S34's mirror (volume bursts poison RIDING and feed FADING).

## S6 — session close (user, 2026-08-26)

> *"Pretty wild that it works so well without any tuning at all... The fact that shorting into low
> volume breakouts worked so well in MaxRider was one of its main edges — investigating the
> difference will be quite important."*

⏭ Next session opens on the **quiet-volume inversion** (S2): why does the lever that carried the
1m system invert on 1s? First step: rebuild the closest possible 1s replica of MaxRider's EXACT
volume measure and run the substitution test — different universes, measures, and event
granularity are all in play. ⭐ User read of S4: **the cover optimum is 7-9m** — 10m→20m adds no
win rate and little profit ("just variance"). ⚠ Add PF columns (raw + clipped) to the sweep
tables when revisiting.

# 🛑⭐⭐ S7 (2026-08-27) — THE CLOCK REVIEW: bar-clock vs time-clock, and the volume family was never a rate

**User diagnosis, confirmed by full engine audit.** The engine's present-bar semantics (the D1
invariant, `Intraday.fs:50-55` — "every window is a PRESENT-BAR-COUNT window") was built to
discount halts. Side effect: **every volume feature lost its time denominator.** Relative volume
is supposed to measure ACCELERATION — rate now vs rate baseline — but on the bar clock the
numerator and denominator skip empty seconds identically, so the arrival rate **cancels
algebraically** and the ratio degenerates to trade-size intensity (dollars per BAR). A quiet
stretch where true $/s collapses reads relvol ≈ 1; a burst of many small prints reads as no
change. Quiet periods stretch their windows over more wall time until the same bar count
accumulates — the halt-discounting doing the wrong job on ordinary sparsity.

## S7a — the audit verdict

Of ~180 stateful accumulators, exactly **three families are genuinely wall-clock**:

| honest family | mechanism |
|---|---|
| `gap_15..1200`, `gap_adj_*` | `GapCounter` — true `etSec` eviction (the ONLY time-evicted window) |
| `secs_since_*`, `fwd_vwap_N`, time gates | `etSec` arithmetic (fwd marks: EntrySec + N SECONDS — correct) |
| halt classifier | gap-run trigger + interval arithmetic (its `prevRng300` qualifier is bar-clock) |

Everything else is bar- or slot-clock. Notable classes:

- **Unit bugs under ANY convention**: `rate60vs1200` and `vol10Ok` divided N-BAR sums by
  N-SECOND literals (a per-bar rate mislabelled per-second).
- **Slot chain worse than bar-clock**: `SlotVwapMa` counts pushes — a "30s slot" is 30 present
  bars of arbitrary wall span, boundaries not wall-aligned, empty wall intervals invisible.
  volat/tightness/eff/eff9/VR/AC/volat_slope/lag-"1m" twins all inherit this.
- **Hybrid mislabels**: `speed` "%/min" is %-per-60-present-bars; the OLS `×6e5 ≈ bp/min`
  conversion assumes 1 bar = 1 s and is baked into five transplanted gate thresholds; `aux_lo_N`
  (bar marks) sit beside `fwd_vwap_N` (time marks) — the S4 cover sweep's "7-9m" is a
  bar-count optimum.
- **Deliberate bar-clock that STAYS**: `halts_1200/600` (a wall window shrinks exactly when a
  cascade runs — the in-file rationale is sound), event counters (K counts are event counts),
  session cums.
- **FlushFader irony**: its load-bearing levers — the gap counts, persistence — are the ONE
  time-correct family. The production system was accidentally built on the honest features.
  FlushFader stays untouched (user decision); LongHiker's S33/S34 relvol results are rate-blind
  (banner added there); MaxRider-vs-SpikeFader quiet-volume (S2) was comparing two different
  quantities wearing the same name.

## S7b — the fix (user decisions)

**Scope: the VOLUME family only**, on a **halt-adjusted tradeable clock**: every push advances
an internal clock by `1 + gapCount`, where `gapCount` = missing NON-HALT seconds since the
previous bar (a classified halt owns its entire run → gap 0 across it; ordinary sparsity counts
in full). Canonical columns are now TIME windows (N tradeable seconds); the old bar-count sums
are kept as `*_bar` twins for the substitution test. Price channels, slot chain, OLS, speed,
entry/exit events: unchanged bar-clock.

- New primitives (`RollingMa.fs`, additive): `TimeSumMa(windowSecs)` (eviction folded into
  `Push(v, gapCount)`; `Count` = free bar-density), `TimeLagMa(lagSecs)`, and the gap-aware
  `EmaHlMa.Push(x, gapCount)` — closed form `d = (1−α)^(1+g); num ← d·num + αx;
  den ← d·den + (1−d)` (⚠ den adds `1−d`, NOT α — the zeros are observations; adding only α
  reads volume-per-bar at steady state, the bug itself). Oracle `TimeSumMa_Test.fsx`: eviction
  exact to 1e-12 on halt-sized jumps, dense-tape equality with SumMa/LagMa exact, gap-EWMA vs
  explicit zero-loop 1e-13, steady-state V=900-every-10s reads 90.2/s not 900.
- Engine: `vol/tc_{5..1200}`, `dollar_vol_{60..1200}`, `vol/tc_60_prev` → time twins;
  `rate60vs1200`, `vol10Ok`, `DvFloor60`/`TcFloor60` read the TIME sums (the /60, /1200
  literals are now correct); `halt_secs_cum` recorded (signal_sec − halt_secs_cum = the
  tradeable clock). Schema +23 columns.

Consequences: the corpus needs a full re-run (`spikefader_base_v2`); the S1-S5 tables'
volume-derived columns are bar-clock (labels wrong, internally consistent); the F18
quiet-volume question re-opens on the honest rate via the built-in substitution pair
(`vol_1200` vs `vol_1200_bar` on the same trips).
