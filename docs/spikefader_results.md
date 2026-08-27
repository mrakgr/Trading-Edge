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

## ⭐ S8 (2026-08-27, user) — speed stays BAR-CLOCK; lags retired for WINDOW DIFFERENCES; the EWMA time-speed

**User design.** The bar-clock speed denominator is the RIGHT choice — a time-windowed vwap is
degenerate on very sparse tape (an empty window has no price), while "the 60-bar vwap 60 bars
ago" always exists. Three changes:

1. **Every lag queue retired for a window difference**: the N-bar vwap N bars ago == vwap over
   bars [t−2N+1, t−N] == `(dv2N − dvN)/(vol2N − volN)` — same value, same warmth bar, no queue.
   Applied to `vwap_{5,10,30,60}_prev` (new 20-bar sums feed the 10; the 120-bar sums are now
   un-gated since `vwap_60_prev` feeds MinSpeed1m/SpeedStopPct in LiveSlim too).
2. **`vol/tc_60_prev` (canonical, time) = `t120 − t60`** — the EXACT prior tradeable minute,
   replacing the TimeLagMa snapshot (stale on sparse tape). `_bar` twins = `bar120 − bar60`.
   TimeLagMa keeps its oracle but has no engine users.
3. **The proper time-based speed** = difference of gap-aware UNSCALED decayed sums
   (`DecaySumMa`: `s ← s·0.5^((1+gap)/hl) + x`, hl 60/120 tradeable secs):
   `vwap_ew_60_prev = (Sdv120−Sdv60)/(Svol120−Svol60)`. The difference kernel
   `0.5^(a/120) − 0.5^(a/60) >= 0` is a smooth "prior minute" with no sparse-tape degeneracy
   (the ratio stays anchored to the last trades). ⚠ UNSCALED is load-bearing: with α-scaled
   (EmaHlMa-style) sums the age-0 weight is α120 − α60 < 0 and the kernel goes negative.
   Recorded as `vwap_ew_60` / `vwap_ew_60_prev`; `speed_ew = signal_vwap/vwap_ew_60_prev − 1`.

**Verification (2026-07 smoke, old vs new):** trip keys IDENTICAL (20,231 = 20,231, PF 2.156
both); all four difference-form vwaps match the lag queues to <= 1.5e-12; `vol_60_prev` dense
identity exact; oracle test extended (DecaySumMa 1e-15, kernel positivity 0 violations,
constant-price recovery 1e-14). EWMA-vs-bar speed: median +3.51% vs +2.92%, corr 0.87 —
distinct enough to substitution-test.

| gap_120 band | n | med vol_60_prev / _bar |
|---|---|---|
| 0 | 5,541 | 1.0000 |
| 1-29 | 8,647 | 0.9570 |
| 30-59 | 3,155 | 0.6197 |
| 60+ | 2,888 | 0.3188 |

### S8b — DecaySumMa is THE gap-aware exponential primitive; EmaHlMa frozen (user)

**User insight + design**: the decayed sum and the EWMA are one object — `EmaHlMa.num = α·Sum`
and `den = α·w` exactly, where `w` = the history's total weight. So the sum is the primitive
and the mean is the derived read: `DecaySumMa` now carries both (`Sum`, and bias-corrected
`Mean = Sum/w`, gap zeros counted as observations; the weight increment `(1−d)/(1−λ)` is
exactly 1 at gap 0, so one update serves the per-push slot mean AND the per-tradeable-second
rate). ⚠ The user's first sketch derived Average as `s·λ` — that is the sum decayed one more
step, not a mean; the weight accumulator is unavoidable (it IS the bias correction).
`EmaHlMa`'s never-used gap overload (added this morning) is REMOVED; the class itself is
frozen as the production surface — six engines + the live Scanner read it, so retiring it
rides on each engine's next reference-then-diff pass, not on aesthetics. All new gap-aware
work (LongHiker relvol port included) builds on `DecaySumMa`. For the relvol re-test: the
measure is the RATIO of `Mean`s across two half-lives (user), bounded by (0, hl_slow/hl_fast]
— so the baseline leg must be genuinely slow (hl 1200 ≈ the old vol_60-vs-vol_1200 geometry,
ceiling 20×; a 60/120 pair caps at 2× and is too cramped for a relvol axis). Oracle: `Mean`
== explicit-zero-fed `EmaHlMa` to 1e-13 sparse / 2e-16 dense; all prior checks unchanged.

## S9 (2026-08-27, user) — the volHIGH ladder: per-rung prior-window session maxes

**Motivation**: MaxRider F10/F12 — the 2×2 `session-high × vol-high` interaction, and the one
LONG-momentum cell of that program (F12: volume surge to a new session-volume high while price
is still BELOW the session high → continuation, PF ~1.27 long at 1m covers). `volHIGH` there =
"this 1m bar's volume sets a new session max". LowFlyer also consumes the session 1m max —
baked in ahead of that port.

**Construction**: `TimeLagMaxMa(W)` — session running max of the W-sec time-sum over windows
ending **>= W tradeable secs ago** (fully non-overlapping, the analog of distinct W-sec bars;
an unlagged max would compare a rising window to itself). Recorded at every volume rung
(`vol_{5,10,15,30,60,300,600,1200}_prior_max`), the $ twin at 1m (`dollar_vol_60_prior_max`),
and the EWMA-rate version (`vol_ew_60` = DecaySumMa hl-60 Mean now, `vol_ew_60_prior_max` =
its prior max). `volHIGH_W := vol_W > vol_W_prior_max` in SQL; the price leg is `dshi`/`sess_high`.
Oracle test 8 (brute-force lagged max, 0 mismatches); smoke: additive (20,231 trips, PF 2.156
unchanged), 0 nan/neg at signal, volHIGH_60 incidence 3.0% (MaxRider's was 1.4% — same rarity
class).

⚠ **Population caveat for the F12 replication**: this sampler fires only on new 20m highs, so
`below-sess-high × volHIGH` is 0.1% of trips (~20/month) — the test here is the pre-breakout
surge CONDITIONED on the 20m-pop event. The unconditional F12 (any bar making a new session-
volume high below the price high) would need its own sampler if the conditioned cell shows
signal.

### S9b — vol_z_log: MaxRider's ACTUAL quiet-volume measure, transcribed (user)

⚠ Correction of the S2/S7 framing: MaxRider's quiet-volume was **not** a 1m/20m ratio — it was
`vol_z_log`, a SESSION-CUMULATIVE z of log(bar volume) (`CumStdMa` of log 1m-bar volume,
MaxRiderV1/Intraday.fs:230; quiet := z < −0.5; log beat linear empirically — F5: log monotone
PF 1.769→1.281, linear non-monotone with a 14σ-outlier top bucket). SpikeFader had NO
session-distribution volume z — so S2's quiet test missed the original measure on a third axis
(wrong clock, wrong ratio, wrong reference distribution).

**1s transcription (user decision)**: push log(max bar_vol 1) per PRESENT 1s bar, record
`vol_z_log` = z of the SIGNAL bar's log volume vs that session distribution + `vol_z_n`
(samples). **Deliberately NOT gap-aware** — the z is the SIZE-INTENSITY axis over actual
prints; the "how often does anything trade" information lives in the tradeable-time RATE
features (vol_60/prior-max, vol_ew_60, relvol of Means). Forcing gap zeros into the z's
distribution would make the mixture zero-dominated on sparse tape and degenerate the z into a
muddled rate duplicate; log space also cannot take zeros (the log1p weighted-Welford hybrid was
considered and rejected — two clean orthogonal axes beat one fused one; their 2×2 is SQL).
Normal space rejected on F5's own evidence. Smoke: 0 nan, med n=4,862, z p1/med/p99 =
−1.95/+0.95/+2.54 (positive median expected — signal bars ARE pops), quiet band 8.1%.

### S9c — the vol_z_log ladder: overlapping bar-clock windows (user)

`vol_z_log_{5,10,15,30,60}` — z of log(rolling W-BAR volume) against the session distribution
of that same rolling quantity (CumStdMa per rung, warm windows only; sample counts derive as
vol_z_n − W + 1). Multi-scale size intensity to bracket which aggregation the MaxRider lever
lives at. Smoke: additive, 0 nan; median z falls +1.11 (W=5) → +0.54 (W=60) as the pop's
local surge dilutes across wider windows — the ladder separates as intended.

### S9d — debloat step 1: LiveSlim excised from FlushFader + SpikeFader (user)

The research engines carried the pre-split scanner's LiveSlim mode (~55/~86 sites + recW/maW
window helpers); the production Scanner is long since its own debloated codebase, so research
never runs anything but LiveSlim=false. Removed wholesale from both engines (config field,
helpers, every gate, the live retire clause). **Proven inert by reference-then-diff**: 2026-07
smokes pre/post edit — FlushFader base 23,556 rows, FlushFader spec 416, SpikeFader base
20,231 — key sets identical, EVERY column identical on every row. (Process note: the first
reference attempt was invalidated by rebuilding binaries while reference runs were queued on
the same path — the binary-race lesson recurring; redone via stash → old binaries → refs →
pop → candidates.)

# ⭐⭐ S10/S11 (2026-08-27 evening) — the v2 corpus: honest-clock headline HOLDS, and the F23 rate ratio PORTS the quiet-volume lever

**Corpus**: `spikefader_base_v2` — 2,088,578 trips / 1,164,334 tkd / 2020-01-02..2026-08-21,
completed clean on the lightened engine (2h13m, 21 parts, exit 0). Spec derived POST-HOC in SQL
(engine spec runs retired — the transplant was never ratified): every gate transcribed from
Intraday.fs and VALIDATED against a July engine oracle, 443 = 443 keys exact. ⚠ Transcription
subtleties for the record: DuckDB orders NaN ABOVE numbers (every gate needs an isnan guard),
`vol_1200` in the z-gate must be the `_bar` twin (the engine's moments are bar-clock), and
`rngfront`'s denominator is the POST-push channel = `ln(signal_vwap/chan_lo)` (`chan_hi` records
the strictly-prior snapshot).

## S11a — the mc=1 headline on the honest clock (greedy replay in SQL)

30,732 spec trips mc=0 (PF 1.707) → 4,997 mc=1. Per ticker-day mean → per day mean:

| yr | days | eqw bp | med bp | up% | tkd | trim bp |
|---|---|---|---|---|---|---|
| 2020 | 215 | 119.4 | 165.5 | 74.9 | 710 | 115.6 |
| 2021 | 233 | 59.3 | 99.7 | 67.0 | 814 | 56.7 |
| 2022 | 200 | 80.7 | 131.6 | 71.5 | 450 | 76.6 |
| 2023 | 207 | 64.6 | 149.7 | 67.6 | 393 | 59.7 |
| 2024 | 227 | 139.0 | 218.4 | 77.1 | 605 | 134.3 |
| 2025 | 242 | 102.0 | 158.3 | 73.6 | 834 | 97.5 |
| 2026 | 150 | 86.5 | 150.6 | 68.0 | 504 | 80.2 |
| **all** | 1,474 | **93.8** | 155.7 | 71.6 | ~615/yr | — |

**+93.8bp/day eqw, 7/7 years, trim-positive every year — the clock fix IMPROVED the v1 read
(+88.2 → +93.8)**: the honest floors drop sparse-tape trips that were being admitted on
stretched windows. Same caveats as session 1: costs/borrow/stops unmodeled, gates untuned.

## S11b — ⭐⭐ THE QUIET-VOLUME RESOLUTION: baseline length was the whole story

The S2 inversion survives every 20m-baseline measure but REVERSES under the session-baseline
rate ratio (`rr_sf = vol_60 / (cum_vol·60 / tradeable-secs-elapsed)`, the F23 measure, all
recorded columns):

- **mc=1 spec book by rr_sf**: <0.5 → PF 2.314 (n 134), <1.0 → 2.071 (n 950), ≥2 → 1.38-1.55.
  QUIET FADES BEST, monotone.
- **base speed>2% pops by rr_sf (mc=0)**: 0.2-0.3 → 1.989, <0.5 → 1.886, declining to 1.32-1.41
  loud. Same lever, ~2.0-vs-1.4 spread (MaxRider's was 3.6-vs-1.6 on its contaminated table).
- The same frames by `vol_z_log`: FLAT (1.36-1.49 hump) — the z does not separate on 1s.
- S10 full-corpus C1/C2 (vol_60/vol_1200, 20m baseline): hump at 2-4×, quiet bands weak — the
  20m baseline is what S2 was implicitly using, and it genuinely doesn't carry on 1s.

**Conclusion: MaxRider's quiet-volume lever EXISTS on the 1s short fader — it was never the
clock alone, and never absent; it lives at the SESSION baseline.** F23's ordering (session >>
15m >> 5m baselines) predicted exactly this. The morning's "quiet does not transcribe" (S10
preliminary) was true of the measures tested then and is superseded for the session-baseline
ratio. ⏭ The rr_sf lever is a candidate SPEC gate — needs the iso-trip control + re-derivation
with the rest of the short-side gate pass (user-in-the-loop).

## S11c — S10 full-corpus confirmations (2.089M trips)

F10 2×2 confirmed: at-high×volHIGH 1.651 (best), below-high×volHIGH **0.805, the only losing
cell**. F12 probe: 2-3m covers PF 0.715-0.749 (→ long ≈ 1.34-1.40) on n 4,455, but per-year
negative only 4/7 and **2026 flipped positive (1.632)** with the new months — as fragile as
MaxRider's original. Bar-clock relvol's ≥8 band confirmed as a noise bucket (n 1,400, yearly
PFs 0.19-178) vs the honest clock's coherent 26k-trip band.

### S11d — ⚠ THE CLOCK CONTROL (user): at matched selectivity, the bar-clock lever is JUST AS GOOD

Substitution control on the rr_sf quiet lever — four variants, mc=1 spec book, matched n = 1,084:

| measure | thr | PF | p1 | years |
|---|---|---|---|---|
| rr_time (vol_60 time / tradeable-sec rate) | 1.00 | 2.099 | −16.1 | 7/7 |
| rr_barnum (vol_60 BAR / tradeable rate) | 1.16 | 2.095 | **−19.0** | 7/7 |
| rr_wall (vol_60 time / RAW wall rate) | 1.03 | **2.205** | −16.1 | 7/7 |
| rr_allbar (vol_60 BAR / per-BAR rate) | 0.87 | 2.134 | −16.8 | 7/7 |

**Verdict: the quiet-volume lever is carried by the SESSION-BASELINE CONSTRUCTION (F23), not by
the clock.** At matched selectivity the fully bar-clock variant reads within noise of the honest
one. What the clock migration actually bought, on today's evidence: (1) the honest numerator
keeps the quiet tail ~3pp thinner; (2) fixed thresholds MEAN the same thing across tape density
(the bar variants' fixed-band tables lose monotonicity at the extremes — rr_allbar's loud band
inverts to 1.844 on the pops frame); (3) the measure TAILS stop being noise buckets (the
bar-clock 20m relvol ≥8 band: n 1,400, yearly PF 0.19-178, vs the honest clock's coherent 26k);
(4) honest floors. It did NOT change mid-band rankings — S2's inversion was a BASELINE-LENGTH
artifact, not a clock artifact, and the morning's contrary hypothesis is hereby corrected.

⭐ **The unplanned finding: rr_wall > rr_time (2.205 vs 2.099; and 4.00 vs 2.31 in the tiny
fixed <0.5 cell, p1 −6.5)** — REMOVING the halt adjustment helps. Halt-discounting makes
post-halt names look quiet (their tradeable-time rate stays high), and post-halt "quiet" pops
are bad fades — the LULD-elevator class the cascade gates were designed for. ⏭ Tomorrow's
gate-by-gate pass should test: quiet defined on RAW wall time, or the halt-adjusted version
paired with an explicit post-halt veto.
