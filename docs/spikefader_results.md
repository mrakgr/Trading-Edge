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

# S12 (2026-08-28) — GATE-BY-GATE REBUILD, feature 1: volat_20m (the FlushFader 40bp floor)

**Frame change (user): start from scratch — ALL spec gates OFF, the bare base_v2 corpus,
one feature at a time.** mc=1 replayed greedily inside the bare frame (107,770 trips from
2,088,578 mc=0).

⚠ **The 40bp floor cannot be tested from this corpus: it is baked into the base run itself**
(`volat band = volat_20m ∈ [40, inf) bp/30s` in the run log — `MinVolat20m = 0.004` is an
ENTRY gate AND feeds the candidate reader). Bands 00-03 are empty; everything below is about
whether the floor should be HIGHER. Testing below 40bp needs a base re-run with
`MinVolat20m = 0`.

## mc=1 fine bands (bare frame)

| band | n | avg% | med% | p1 | win% | pf | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| <50bp | 40,034 | −0.049 | 0.551 | −9.5 | 61.5 | 0.945 | 0.79 | 0.96 | 0.96 | 1.05 | 1.02 | 0.99 | 1.02 |
| <60bp | 20,743 | 0.031 | 0.782 | −11.6 | 63.1 | 1.029 | 0.90 | 1.12 | 1.03 | 1.02 | 1.07 | 1.12 | 0.91 |
| <80bp | 23,265 | 0.230 | 1.183 | −15.5 | 65.5 | 1.178 | 1.04 | 1.17 | 1.18 | 1.23 | 1.30 | 1.22 | 1.14 |
| <100bp | 11,309 | 0.467 | 1.746 | −20.1 | 66.8 | 1.291 | 1.07 | 1.15 | 1.34 | 1.47 | 1.34 | 1.40 | 1.30 |
| <150bp | 9,270 | 0.593 | 2.582 | −31.5 | 69.1 | 1.255 | 1.19 | 1.37 | 1.18 | 1.15 | 1.29 | 1.30 | 1.26 |
| <200bp | 2,226 | 0.697 | 3.844 | −44.2 | 68.9 | 1.197 | 0.88 | 1.08 | 0.97 | 1.33 | 1.20 | 1.25 | 1.55 |
| <300bp | 812 | 1.090 | 4.646 | −51.2 | 67.0 | 1.259 | 1.34 | 0.61 | 2.60 | 1.30 | 1.28 | 1.60 | 0.98 |
| ≥300bp | 111 | 0.193 | 4.098 | −73.0 | 64.9 | 1.028 | 3.48 | 3.23 | NaN | 0.71 | 0.73 | 1.53 | 0.56 |

(mc=0 table in session scratch `sf_s12.out`; same shape, PFs uniformly higher — the ≥300bp
band reads 2.14 at mc=0 and collapses to 1.03 at mc=1, the usual multi-signal inflation.
pf_clip = pf everywhere: no win exceeds +50% on this book.)

## mc=1 floor sweep (kept book: volat_20m ≥ F) — AMENDED same day (S12d)

⚠ The original sweep filtered the bare-frame mc=1 book (replay-then-filter — the weaker
order). Amended per user: filter FIRST, replay inside each floor's frame (a gate frees the
ticker-day slot for later passing signals). Levels rise slightly; shape unchanged; the
80bp peak sharpens.

| floor | n | avg% | p1 | win% | pf | eqw bp/d | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 40bp | 107,770 | 0.160 | −16.4 | 64.1 | 1.124 | 30.1 | 0.96 | 1.10 | 1.10 | 1.19 | 1.22 | 1.21 | 1.13 |
| 50bp | 71,352 | 0.282 | −19.8 | 65.5 | 1.186 | 47.7 | 1.04 | 1.18 | 1.17 | 1.21 | 1.26 | 1.27 | 1.16 |
| 60bp | 50,211 | 0.393 | −23.0 | 66.7 | 1.229 | 66.4 | 1.09 | 1.21 | 1.22 | 1.25 | 1.29 | 1.31 | 1.18 |
| 70bp | 36,038 | 0.486 | −26.2 | 67.5 | 1.252 | 85.8 | 1.12 | 1.24 | 1.27 | 1.26 | 1.29 | 1.33 | 1.21 |
| 80bp | 26,120 | 0.596 | −29.5 | 68.2 | **1.278** | 103.9 | 1.14 | 1.26 | 1.29 | 1.30 | 1.30 | 1.37 | 1.24 |
| 100bp | 14,092 | 0.715 | −37.1 | 69.4 | 1.269 | 128.9 | 1.22 | 1.22 | 1.26 | 1.21 | 1.27 | 1.37 | 1.26 |
| 120bp | 8,066 | 0.778 | −43.9 | 69.4 | 1.245 | 147.0 | 1.15 | 1.26 | 1.24 | 1.25 | 1.26 | 1.33 | 1.18 |

A 300bp ceiling is cosmetic at any floor (n ≥300bp = 111; PF moves in the 3rd decimal).

## Reads

1. **The [40, 50)bp band — 37% of the mc=1 book — is under water standalone** (PF 0.945,
   avg −5bp/trip), and [50, 60) is barely break-even. On the bare frame the floor earns its
   keep only from ~60bp.
2. **Monotone rise to a peak at 80bp** (PF 1.278, every year ≥ 1.14; ≥ 1.09 from floor 60
   up). Per-trip avg and eqw/day keep rising past that (0.78% / 147bp at floor 120) but buy
   it with tail (p1 −16% → −44%) and book size (108k → 8k): higher floors are a
   sizing/stop question, not a free PF gain.
3. 2020 is the weak year at every cut (1.04-1.15) — the short fader's worst regime, worth
   remembering as gates stack.
4. ⚠ Interaction note from the aborted spec-frame run (before the from-scratch pivot, in
   `sf_s12.out` history): INSIDE the full 15-gate spec the high bands invert hard — volat
   ≥150bp reads PF 0.96/0.95/0.20 with p1 −41..−56%. Standalone, high volat is fine to
   300bp; inside the spec it is ruinous. A ceiling decision belongs to the stacked context,
   not to feature 1.

**Verdict on "is 40bp the right floor": 40bp is a base-corpus recording floor, not an edge
floor — standalone the edge starts at ~60bp.** Whether to raise it in the rebuilt spec is
deferred until the stack exists (the later gates may be doing the same work — inside the old
spec the sub-60bp population was already down to 28% of the mc=1 book vs 56% here).

# S13 (2026-08-28) — feature 2: the SPEED pair (FlushFader's two legs, flipped short)

`speed_1m = signal_vwap/vwap_60_prev − 1` (vwap_60_prev = (dv120b−dv60b)/(vol120b−vol60b), S8)
and `dist_lo = signal_vwap/lo_60 − 1` (60-bar post-push low). Bare base_v2 frame; every book
mc=1-replayed INSIDE its own gate frame. Full band tables in scratch `sf_s13.out`.

## 1D bands (mc=1 inside bare frame) — the shape

- **speed_1m**: monotone but shallow — 0.90 below 0.5%, ~1.02-1.10 in 0.5-2%, 1.14-1.23
  above 2%. The corpus has NO speed < 0 trips: the base trigger is isNewHigh, which
  mechanically floors speed at ~0.
- **dist_lo**: the sharper leg — sub-1% is outright NEGATIVE (0.79-0.88, every year ≤ 1.1),
  peak 1.24 at 2.5-3%, plateau ~1.18-1.20 above.
- **2D joint**: the diagonal dominates (fresh pop ⇒ speed ≈ dist). Off-diagonal curiosity:
  LOW speed + HIGH dist (pop happened minutes ago, still stretched, no longer moving) reads
  PF 2.4-3.3 on ~100 trips — a "stalled pop" cell worth a look once the stack exists.

## Joint-threshold sweep (speed > T AND dist > T, mc=1 inside each frame)

| T | n | avg% | p1 | win% | pf | eqw bp/day |
|---|---|---|---|---|---|---|
| 0% | 107,770 | 0.160 | −16.4 | 64.1 | 1.124 | 30.1 |
| 1.5% | 89,271 | 0.216 | −18.0 | 64.5 | 1.154 | 40.3 |
| 2% | 72,682 | 0.282 | −19.6 | 65.0 | 1.186 | 50.2 |
| 2.5% | 57,490 | 0.342 | −21.8 | 65.7 | 1.207 | 62.4 |
| 3% | 45,512 | 0.414 | −23.8 | 66.4 | 1.231 | 72.9 |
| 4% | 29,006 | 0.513 | −28.6 | 67.2 | 1.244 | 95.4 |

**Monotone through 4% — no knee at 2%.** Deeper is better per-trip and per-day, paid for in
n (107k → 29k) and tail (p1 −16 → −29).

## Leg attribution at 2%

speed-only 1.172 (n 78,397) · dist-only 1.174 (n 79,900) · both 1.186 (n 72,682) — the two
legs are near-duplicates at matched threshold (the isNewHigh trigger makes a fresh high both
fast AND stretched); the AND buys +0.013 PF for −7% n. Keep both for robustness, but they
are one lever, not two.

## Both >2% book, headline year table (mc=1)

| yr | days | eqw bp | med bp | up% | tkd | trim bp |
|---|---|---|---|---|---|---|
| 2020 | 252 | 46.7 | 53.3 | 73.0 | 5,726 | 45.5 |
| 2021 | 252 | 40.9 | 46.8 | 75.8 | 6,324 | 40.2 |
| 2022 | 251 | 50.7 | 54.4 | 71.7 | 3,374 | 49.2 |
| 2023 | 250 | 55.1 | 70.0 | 75.6 | 2,663 | 53.7 |
| 2024 | 252 | 67.7 | 74.4 | 79.8 | 3,973 | 66.5 |
| 2025 | 250 | 51.1 | 55.9 | 80.4 | 5,807 | 50.3 |
| 2026 | 160 | 33.1 | 41.4 | 71.3 | 3,428 | 31.6 |

Positive and trim-positive every year with volat + speed alone (+~50bp/day eqw blended);
2026 is the weak year (33bp). The remaining 13 old-spec gates took the July book from here
(PF 1.186 @ 72,682) to PF 1.563 @ 4,997 — that spread is what the rest of the gate-by-gate
pass has to re-earn honestly.

**Verdict: the speed pair is real and monotone; 2% is defensible but not special — the
threshold choice is a book-size decision, deferred until the stack is assembled.**

# S14 (2026-08-28) — feature 3: EFFICIENCY, SMA vs EWMA forms (user question: which is better NOW that both are recorded)

Frame: the speed stack (speed_1m > 2% AND dist_lo > 2%), mc=1 replayed inside it
(72,682 trips), banded by each measure. corr(SMA, EWMA) on the book: 20m 0.644, 10m 0.510 —
genuinely different measures. Full tables in scratch `sf_s14.out` / `sf_s14b.out`.

## ⭐ THE SHAPE: the two SMA horizons point in OPPOSITE directions

**SIGNED eff_20m (SMA)** — monotone DECREASING (backdrop should be inefficient/declining):

| band | n | pf |
|---|---|---|
| [−.3,−.15) | 569 | 1.677 |
| [−.15,0) | 12,547 | 1.283 |
| [0,.15) | 27,024 | 1.202 |
| [.15,.3) | 20,133 | 1.122 |
| [.3,.5) | 9,397 | 1.104 |
| [.5,.75) | 1,185 | 1.133 |

**SIGNED eff_10m (SMA)** — monotone INCREASING (the spike itself should be efficient):

| band | n | pf |
|---|---|---|
| [−.15,0) | 5,853 | 1.018 |
| [0,.15) | 16,972 | 1.120 |
| [.15,.3) | 20,802 | 1.200 |
| [.3,.5) | 19,555 | 1.286 |
| [.5,.75) | 7,642 | 1.225 |
| ≥.75 | 981 | 1.371 |

Archetype: a CLEAN VERTICAL SPIKE (efficient 10m) out of a CHOPPY/DECLINING backdrop
(inefficient 20m) is the fade; an efficient 20m uptrend that keeps popping is the danger.

**The EWMA forms are NON-MONOTONE** — both horizons hump then dip at [.15,.3)
(eff_ewma_20m: 0.980 there vs 1.483 at [−.15,0); eff_ewma_10m: 1.060 vs 1.277) and flip
sign again above. Mechanism: EWMA recency-weighting leaks the spike itself into the "20m"
measure, blending the two opposing horizons into one number. **Verdict: SMA wins — clean
monotone structure at both horizons, opposite signs, interpretable. EWMA eff is a mixed-
horizon measure and should not gate.**

## 2D joint (mc=1 inside speed stack) — pf (n)

| eff_20m \ eff_10m | <0 | 0-.15 | .15-.3 | .3-.5 | ≥.5 |
|---|---|---|---|---|---|
| <−.15 | 1.81 (264) | 1.87 (144) | 1.74 (119) | 1.02 (71) | 3.73 (12) |
| −.15-0 | 1.01 (1,523) | 1.30 (3,093) | 1.26 (3,803) | 1.44 (3,231) | 1.36 (897) |
| 0-.15 | 1.01 (2,532) | 1.13 (6,684) | 1.24 (8,197) | 1.29 (7,287) | 1.24 (2,324) |
| .15-.3 | 1.01 (1,909) | 1.03 (5,195) | 1.14 (5,922) | 1.24 (5,168) | 1.21 (1,939) |
| ≥.3 | 0.90 (502) | 1.03 (1,856) | 1.10 (2,761) | 1.21 (3,798) | 1.22 (3,451) |

## Candidate gates on top of speed>2% (mc=1 INSIDE each frame)

| gate | n | avg% | p1 | pf | eqw bp/d | worst yr |
|---|---|---|---|---|---|---|
| (speed stack alone, S13) | 72,682 | 0.28 | −19.6 | 1.186 | 50.2 | — |
| old transplant \|e20\|∈[.3,.5) & \|e10\|≥.15 | 25,463 | 0.50 | −22.1 | 1.313 | 73.6 | 1.19 (2020) |
| A: e20<.15 & e10>.15 | 32,681 | 0.44 | −19.6 | 1.300 | 59.3 | 1.20 (2026) |
| B: e20<0 & e10>.15 | 8,545 | 0.52 | −19.5 | 1.347 | 69.7 | 1.15 (2021) |
| C: e20<0 & e10>.3 | 4,520 | 0.59 | −19.0 | 1.395 | 72.6 | 1.11 (2021) |
| D: e20<.15 & e10>.3 | 21,108 | 0.53 | −20.4 | 1.366 | 68.2 | 1.24 (2021) |

⚠ **The three-mc lesson recurred within this session**: the old transplant band reads PF
~1.10-1.14 in the band tables (bare-replay banding) but 1.313 replayed inside its own frame
— gating frees ticker-day slots for later qualifying signals. Band tables rank shapes;
only inside-frame replays rank gates.

**Verdict: efficiency is a real second lever on top of speed. SMA forms only. D
(e20<.15 & e10>.3) is the leading candidate — PF 1.366 on a 21k book, every year ≥1.24,
better p1 than the transplant — but the transplant band is NOT dead (best eqw/day at 73.6);
final threshold choice deferred to stack assembly.**

## S14c addendum — mc=0 tables (user request: more samples) ⚠ eff_20m INVERTS at mc=0

mc=0 on the speed-stacked frame (1,010,722 trips). Full tables in `sf_s14c.out`.

**SIGNED eff_20m (SMA), mc=0** — monotone INCREASING, the OPPOSITE of mc=1:

| band | n | pf_mc0 | pf_mc1 (S14) |
|---|---|---|---|
| [−.15,0) | 48,220 | 1.260 | 1.283 |
| [0,.15) | 224,898 | 1.299 | 1.202 |
| [.15,.3) | 323,132 | 1.350 | 1.122 |
| [.3,.5) | 315,818 | 1.504 | 1.104 |
| [.5,.75) | 86,217 | 1.746 | 1.133 |
| ≥.75 | 3,226 | 4.171 | — |

**SIGNED eff_10m (SMA), mc=0** — increasing, SAME direction as mc=1 (robust lever):

| band | n | pf_mc0 | pf_mc1 (S14) |
|---|---|---|---|
| [−.15,0) | 21,870 | 1.026 | 1.018 |
| [0,.15) | 102,153 | 1.169 | 1.120 |
| [.15,.3) | 203,926 | 1.230 | 1.200 |
| [.3,.5) | 350,421 | 1.416 | 1.286 |
| [.5,.75) | 279,059 | 1.628 | 1.225 |
| ≥.75 | 50,766 | 1.885 | 1.371 |

**The diagnostic that explains the inversion — signals per ticker-day by eff_20m band:**

| band | n | tkd | trips/tkd |
|---|---|---|---|
| <−.15 | 1,599 | 598 | 2.7 |
| [−.15,0) | 48,220 | 9,658 | 5.0 |
| [0,.15) | 224,898 | 19,490 | 11.5 |
| [.15,.3) | 323,132 | 20,348 | 15.9 |
| [.3,.5) | 315,818 | 16,574 | 19.1 |
| [.5,.75) | 86,217 | 5,420 | 15.9 |

Two mechanisms, both favoring high-eff bands at mc=0 only:
1. **Density weighting** — high-eff_20m (trend) days fire 4-7× more signals per ticker-day,
   so mc=0 overweights exactly those days.
2. **Within-day timing selection** — eff_20m BUILDS over a run, so a runaway day's trips
   stamped ≥.3 are disproportionately its LATE signals, the ones near the eventual top.
   mc=0 credits fades you could only take by skipping the earlier losing signals; mc=1
   shows you'd have been stuck in the first trade (the user's cancellation point,
   S13 preamble).

**Rules of thumb going forward:** mc=0's extra samples are only trustworthy for a feature
UNCORRELATED with signal density. eff_10m passes (same direction both views — lean on its
mc=0 n's). eff_20m fails (density-confounded at mc=0 — the mc=1 shape governs). Practical
consequence: the e20 < 0 gate variants (B/C) rest on the thin negative-backdrop population
(~13k mc=1 trips); the milder D (e20 < .15 & e10 > .3) rests on broad n in BOTH views and
is the safer construction.

# S15 (2026-08-28) — stack += eff_10m ≥ 0.3 (user); sharpening the eff_10m TOP (mc=0, the ruled view)

**User rulings recorded**: (a) mc=0 is the proper view for feature breakdowns while the
stack is being assembled — the S14c density confound is a property of the unfinished stack;
mc=1 is for scoring assembled candidates. (b) SMA eff confirmed over EWMA as GATES, but the
EWMA versions' A-tier tail past 0.5 motivates using them as SHARPENERS. (c) Stack is now:
speed_1m > 2% AND dist_lo > 2% AND **eff_10m ≥ 0.3**. eff_20m set aside for now.
(Exit reminder: these trips are the engine default — cover on vwap < prior 300-bar min,
else MOC. NOT the 7m fixed cover.)

**New stack, mc=0**: 680,246 trips · avg 0.952% · p1 −25.9 · win 70.1% · **PF 1.540**.

## Fine eff_10m bands on the new stack

| band | n | avg% | p1 | win% | pf | weak yrs |
|---|---|---|---|---|---|---|
| [.3,.4) | 176,342 | 0.65 | −24.3 | 68.4 | 1.375 | — |
| [.4,.5) | 174,079 | 0.81 | −25.9 | 69.8 | 1.457 | — |
| [.5,.6) | 147,222 | 0.99 | −25.2 | 70.6 | 1.564 | — |
| [.6,.7) | 100,357 | 1.17 | −27.1 | 71.1 | 1.655 | — |
| [.7,.75) | 31,480 | 1.51 | −31.3 | 72.6 | 1.836 | 2026 1.42 |
| [.75,.8) | 22,329 | 1.57 | −29.5 | 71.0 | 1.856 | — |
| [.8,.85) | 14,191 | 1.57 | −29.7 | 72.9 | 1.850 | 2023 0.67 |
| [.85,.9) | 8,268 | 1.37 | −27.6 | 69.1 | 1.634 | 2023 1.07 |
| [.9,.95) | 4,217 | 2.31 | −18.9 | 74.5 | **2.569** | 2023 1.04 |
| ≥.95 | 1,761 | 2.58 | −23.2 | 78.0 | **2.785** | 2023 0.46, 2024 0.23 |

## ⭐ eff_10m ≥ .75 (n 14,246) cross-cut BY eff_ewma_10m — monotone sharpener

| eff_ewma_10m | n | avg% | p1 | win% | pf |
|---|---|---|---|---|---|
| [.3,.5) | 19,037* | 0.88 | −25.6 | 68.5 | 1.491 |
| [.5,.65) | 19,300* | 1.69 | −29.5 | 71.8 | 1.848 |
| [.65,.75) | 5,996 | 3.15 | −41.1 | 79.1 | **2.507** |
| [.75,.85) | 1,585 | 5.25 | −35.1 | 84.9 | **3.850** |
| ≥.85 | 410 | 3.49 | −9.3 | 68.8 | **4.244** |

(*bands span the full ≥.75 population; counts include sub-bands of it.) BY eff_ewma_20m:
[.5,.65) → 3.235 (n 7,437).

## ⭐ eff_10m ≥ .75 cross-cut BY eff_20m (SMA backdrop) — full table (keep: the deepest cell)

| eff_20m | n | avg% | med% | p1 | win% | pf | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| <.15 | 2,541 | 0.74 | 1.54 | −15.0 | 68.1 | 1.522 | 1.49 | 1.16 | 9.10 | 1.19 | 2.24 | 1.42 | 1.59 |
| [.15,.3) | 5,803 | 0.78 | 1.94 | −29.5 | 69.8 | 1.432 | 4.38 | 2.25 | 1.04 | 0.91 | 1.29 | 0.79 | 1.71 |
| [.3,.5) | 16,544 | 1.32 | 2.20 | −24.3 | 69.7 | 1.783 | 1.70 | 1.81 | 1.60 | 1.55 | 1.53 | 2.05 | 2.40 |
| [.5,.65) | 15,946 | 1.87 | 2.81 | −33.6 | 73.2 | 1.936 | 3.36 | 2.23 | 1.68 | 0.84 | 1.58 | 2.90 | 1.43 |
| [.65,.75) | 6,535 | 2.00 | 2.87 | −34.9 | 73.6 | 1.835 | 2.21 | 2.20 | 3.10 | 0.93 | 2.14 | 2.02 | 1.09 |
| ≥.75 | 2,810 | 4.11 | 3.82 | −19.9 | 80.3 | **4.712** | 44.97 | 3.75 | 2.40 | 1.95 | 2.50 | 8.99 | 2.66 |

**⭐ The deepest cell: eff_10m ≥ .75 AND eff_20m ≥ .75 → PF 4.712, n 2,810, win 80.3%,
positive EVERY year (min 1.95 in 2023) — the "everything vertical at every horizon" trip.**
Do not forget this cell (user).

## Reads

1. **The A-tier exists and is where the user pointed**: within eff_10m ≥ .75, the EWMA
   10m measure is a clean monotone sharpener — agreement of the equal-weight and
   recency-weighted windows (both ≥ .65-.75) marks the pure parabolic ramp still at full
   efficiency at signal. PF 2.5 → 4.2 with win% up to 85%.
2. ⚠ **2023 is the weak year in every A-tier cell** (0.46-1.27; also 2024 0.23 at
   eff_10m ≥ .95) — the blowouts are 2020/2021-heavy. Any gate built here needs the year
   table shown before adoption. Extreme-cell year PFs (219, 211, 181) are tiny-denominator
   artifacts, not signal.
3. The [.85,.9) dip in the fine table repeats the S14 pattern of non-monotone pockets —
   the top region is not one smooth gradient; the ewma cross-cut is smoother than the raw
   fine bands.

# S16 (2026-08-28) — feature 4: the K band (highs_since_first_high). The [26,50] transplant DOES NOT TRANSFER

Frame: current stack (speed pair > 2% + eff_10m ≥ 0.3), mc=0. 680,246 trips.

## highs_since_first_high (K) fine bands

| band | n | avg% | p1 | win% | pf | weak yr |
|---|---|---|---|---|---|---|
| <5 | 30,787 | 0.70 | −22.5 | 69.5 | 1.450 | 2021 1.19 |
| [5,10) | 40,109 | 0.75 | −22.8 | 70.0 | 1.480 | 2026 1.30 |
| [10,15) | 43,433 | 0.78 | −23.6 | 70.1 | 1.491 | 2020 1.40 |
| [15,20) | 43,476 | 0.81 | −23.9 | 70.5 | 1.505 | 2026 1.33 |
| [20,26) | 48,819 | 0.82 | −24.0 | 70.9 | 1.495 | 2020 1.29 |
| [26,35) | 66,244 | 0.80 | −25.6 | 70.5 | 1.454 | 2020 1.31 |
| [35,50) | 89,007 | 0.83 | −26.2 | 69.8 | 1.461 | 2022 1.21 |
| [50,75) | 108,885 | 0.81 | −28.8 | 69.5 | 1.421 | 2023 1.30 |
| [75,100) | 76,115 | 0.92 | −29.8 | 69.5 | 1.467 | 2022 1.20 |
| [100,150) | 81,753 | 1.16 | −26.9 | 69.3 | 1.619 | 2021 1.30 |
| ≥150 | 51,618 | 2.10 | −22.2 | 72.5 | **2.315** | 2022 1.25 |

Region contrast: below [26,50] → 1.487 · **in [26,50] → 1.458 (the WORST region)** ·
above → 1.611. The FlushFader 2022 fix was a LONG-side calibration; flipped short there is
no band — the signal is monotone INCREASING: the more highs the up-move has already made,
the better it fades. Maturity/exhaustion, the mirror of the long side's "not too extended".

## highs_since_first_high_300 (5m twin) — same story, smoother

| band | n | pf |
|---|---|---|
| <3 | 29,363 | 1.298 |
| [3,6) | 36,286 | 1.324 |
| [6,10) | 54,537 | 1.344 |
| [10,15) | 70,550 | 1.344 |
| [15,25) | 129,497 | 1.371 |
| [25,40) | 140,795 | 1.479 |
| ≥40 | 219,218 | **1.866** |

k300 ≥ 40 is a THIRD of the stack at PF 1.866 with every year ≥ 1.41 — the broadest
candidate gate found so far. K ≥ 150: PF 2.315, all years ≥ 1.25 (2022 the weakest —
the year the long side needed the band for).

**Verdict: drop the [26,50] transplant; the short-side K lever is a FLOOR, not a band.**
Candidates for the stack: k300 ≥ 40 (broad, smooth) and/or K ≥ 100-150 (sharper, smaller).
⚠ Both correlate with move maturity/time-of-day and trend-day signal density — mc=0
caveats apply; year columns clean throughout though.

## S16b — the rest of the K family (user request before adopting k300 ≥ 40)

Recorded family: all three counters count the SAME event (new high of the ~20m entry
channel) with different LEG RESETS — main = 20m channel leg, `_600` = 10m-low breach,
`_300` = 5m-low breach. **No 1m variant recorded** (would need an additive engine change +
corpus rerun). Twins `bars_since_first_high{,_300,_600}` + `highs_since_downtick` also on
disk, unexamined.

**highs_since_first_high_600 (10m reset), mc=0 on the current stack:**

| band | n | avg% | p1 | win% | pf | worst yr |
|---|---|---|---|---|---|---|
| <3 | 21,810 | 0.58 | −24.3 | 68.4 | 1.342 | 2021 1.15 |
| [3,6) | 28,008 | 0.65 | −23.4 | 68.9 | 1.399 | 2021 1.18 |
| [6,10) | 43,042 | 0.65 | −24.6 | 69.2 | 1.381 | 2026 1.22 |
| [10,15) | 56,928 | 0.64 | −24.6 | 69.1 | 1.368 | 2024 1.28 |
| [15,25) | 107,748 | 0.69 | −25.5 | 69.6 | 1.393 | 2026 1.19 |
| [25,40) | 125,304 | 0.77 | −27.6 | 70.1 | 1.426 | 2020 1.34 |
| [40,60) | 106,081 | 0.85 | −28.1 | 69.6 | 1.455 | 2022 1.17 |
| ≥60 | 191,325 | 1.52 | −25.1 | 71.4 | **1.883** | 2023 1.40 |

Same monotone-floor shape as k300. Head-to-head of the two broad floors:
k300 ≥ 40 → 1.866 (n 219,218, worst yr 1.41) vs k600 ≥ 60 → 1.883 (n 191,325, worst yr
1.40, 2026 2.54 strongest). Statistically twins; k300 slightly broader, k600 slightly
better 2026. The whole family says one thing: **fade maturity — the leg must have made
MANY highs without breaching its low.**

⏭ Parked (user): consider replacing FlushFader's K band [26,50] with a
`highs_since_first_high_300`-style counter on the LONG side too — much later, after this
system is assembled.

## S16c — K combo iso-trip test (user): BOTH floors beat each alone, and not by thinning

Frame: current stack, mc=0. Jaccard(A, B) = 0.544 — half-overlapping levers.

| gate | n | avg% | p1 | win% | pf | worst yr |
|---|---|---|---|---|---|---|
| A: k300 ≥ 40 | 219,218 | 1.50 | −26.5 | 72.0 | 1.866 | 1.41 (2022) |
| B: k600 ≥ 60 | 191,325 | 1.52 | −25.1 | 71.4 | 1.883 | 1.40 (2023) |
| **C: BOTH** | 144,724 | 1.78 | −25.3 | 72.8 | **2.061** | **1.49 (2022)** |

Iso-trip controls at n ≈ n_C = 144,724:
- **Tightened singles**: A' k300 ≥ 54 → 2.028 (worst yr 1.41) · B' k600 ≥ 73 → 2.033
  (worst yr 1.37). C beats both by ~+0.03 PF AND has the best worst-year — the AND is a
  mild but real diversification, not threshold depth in disguise.
- **Random-subsample null** (500× at n_C): from A → 1.866 ± 0.011 (max 1.894); from B →
  1.883 ± 0.009 (max 1.913). P(null ≥ 2.061) = 0.000 on both. The combo is decisively not
  thinning noise.

**Adopted (user): stack += k300 ≥ 40 AND k600 ≥ 60.**
**Stack now: speed_1m > 2% ∧ dist_lo > 2% ∧ eff_10m ≥ 0.3 ∧ k300 ≥ 40 ∧ k600 ≥ 60 —
mc=0: 144,724 trips · avg +1.78% · win 72.8% · PF 2.061 · every year ≥ 1.49.**

## S16d — 2D k300 × k600 (user: "one strong, one weak?"). The mixed cell is the WORST on the board

Mechanical fact first: both counters count the same event and differ only in reset
frequency, so **k300 ≤ k600 always** (verified: 0 violations) — the grid is triangular and
"k300 strong × k600 weak" cannot exist. The only mixed population is LOW k300 inside HIGH
k600: a recent 5m-low breach (dip-and-recover) inside a mature 10m leg.

pf (n), mc=0, frame = speed pair + eff_10m ≥ 0.3:

| k300 \ k600 | <10 | 10-25 | 25-40 | 40-60 | 60-90 | ≥90 |
|---|---|---|---|---|---|---|
| <6 | 1.36 (51,291) | 1.35 (4,654) | **1.04** (3,218) | **1.06** (2,655) | 1.13 (2,245) | 1.20 (1,586) |
| 6-15 | 1.40 (41,569) | 1.36 (61,784) | 1.32 (7,196) | 1.13 (6,215) | 1.17 (4,723) | 1.28 (3,600) |
| 15-25 | — | 1.40 (98,238) | 1.28 (10,008) | 1.17 (9,042) | 1.29 (6,975) | 1.48 (5,234) |
| 25-40 | — | — | 1.46 (104,882) | 1.55 (13,675) | 1.44 (12,864) | 1.64 (9,374) |
| 40-60 | — | — | — | 1.52 (74,494) | 1.67 (14,188) | 2.03 (13,339) |
| ≥60 | — | — | — | — | 1.74 (52,776) | **2.45 (64,421)** |

Reads:
1. **A fresh 5m leg inside a mature 10m leg is the worst fade on the board** (1.04-1.17):
   the up-move already took a dip and RECOVERED — a move that survives its dips is
   dangerous to fade. This is FlushFader's "5m trims the bottom" logic reappearing
   short-side with a mechanism.
2. The k600 dimension still pays WITHIN high k300: at k300 ≥ 60, k600 60-90 → 1.74 vs
   ≥90 → **2.45 on n 64,421 (avg +2.32%)** — the deepest-maturity corner is the best
   large cell found so far.
3. This explains S16c: since high k300 implies high k600, a tightened k300-only floor is
   nearly the combo already (A' 2.028 ≈ C 2.061); the combo's job is excluding the
   dip-and-recover cells.
⏭ Candidate tier-2 upgrade: k300 ≥ 60 ∧ k600 ≥ 90 (PF 2.45 @ 64,421) vs adopted 40/60
   (2.061 @ 144,724) — book-size trade-off, user's call at assembly.

# S17 (2026-08-28) — ENGINE: spec transplant DELETED, stack baked as defaults, short-reset K ladder added, whitelist rerun

**User directives (breakfast batch):** (1) add new-highs-since-last-{30s,1m,2m,3m}-low
counters; (2) delete the FlushFader spec gates; (3) bake the ratified stack as the new
default; (4) rerun on only the stack's ticker-days.

## Engine changes (Intraday.fs / Backtest.fs / Program.fs)

- **The 15-gate FlushFader spec transplant is DELETED** — config fields, gate expressions,
  CLI flags, --base-run sentinels, banner. Gone: K band [26,50], |eff20| band, eff9ema,
  ssf/dlv leg pair, rflow, z20, cascade/reopen, vol10rate, highs300 floor, rngfront,
  accel1020, slope20/slope5. (All remain RECORDED columns — only the gates died.)
- **Default gate set = the ratified stack**: speed_1m > 2% ∧ dist_lo > 2% ∧ eff_10m ≥ 0.3
  (SIGNED SMA form — `MinEff10m` replaces `MinAbsEff10m`), + the signal definition
  (volat ≥ 40bp, 20m channel, dv/tc 60s floors, barnum ≥ 22). `MinDv0945Tape` default
  3e6 → 0 (the stack was derived with it OFF; it is a post-hoc lever again).
  `--base-run` now zeroes just the four stack sentinels.
- **New counters** `highs_since_first_high_{30,60,120,180}`: same new-high event as the
  whole K family, legs reset by the existing brLo{30,60,120,180} breach trackers —
  4 new INTEGER columns after the _600 pair. Bars twins not recorded (highs only).

## The whitelist rerun (spikefader_kstudy)

`spikefader_stk_cand` = mr_candidate_1s_v2 ∩ stack ticker-days = **25,736 tkds (2.2% of
the 1.16M corpus)**; selected via `FF_CANDIDATE_TABLE`. Output:
`data/spikefader_kstudy/` (+ .log). ⚠ Schema ≠ base_v2 (4 new columns) — do not mix dirs.

**Full run: COMPLETE + VALIDATED** — 680,246 trips = the S15 stack exactly (13.1 min,
exit 0, 7 parts); full-corpus trip keys ≡ base_v2 post-hoc stack (0 diffs both ways);
counter ladder triangle invariant 0 violations, 0 disarmed at signal.

**Smoke (July 2026): VALIDATED** — engine-gated trips = post-hoc SQL stack trips on
base_v2, 8,088 = 8,088, zero key diffs both directions (the S19 bit-match discipline
holds for the rewritten gate block). Counter ladder: k30 ≤ k60 ≤ k120 ≤ k180 ≤ k300,
0 violations; none disarmed at signal; July PF 2.530 mc=0 on the whitelist (77.0% win).

# S18 (2026-08-28) — the K ladder: adjacent pairwise 2D grids (30s→10m). Where the gradient dies

Frame: kstudy corpus (= the stack), mc=0, 680,246 trips. Bands per rung ≈ its
p10/p25/p50/p75/p90 (distributions nearly parallel: medians 10/13/17/21/26/34 for
30s/1m/2m/3m/5m/10m). pf (n) grids; avg% grids in scratch `sf_s18.out`.

## k30 × k60 — pf (n)

| k30 \ k60 | <2 | 2-6 | 6-13 | 13-23 | 23-36 | ≥36 |
|---|---|---|---|---|---|---|
| <2 | 1.46 (43,403) | 1.57 (3,849) | 1.66 (6,878) | 1.68 (5,758) | 1.74 (3,270) | 1.87 (1,837) |
| 2-4 | — | 1.47 (51,575) | 1.69 (7,163) | 1.59 (6,530) | 1.69 (3,617) | 1.93 (2,072) |
| 4-10 | — | 1.45 (50,948) | 1.47 (109,236) | 1.55 (22,389) | 1.61 (12,782) | 1.91 (7,016) |
| 10-17 | — | — | 1.42 (58,292) | 1.44 (77,735) | 1.62 (16,296) | 1.89 (9,293) |
| 17-27 | — | — | — | 1.43 (58,674) | 1.51 (38,440) | 1.89 (12,336) |
| ≥27 | — | — | — | — | 1.55 (30,737) | 2.08 (40,120) |

avg% per trip:

| k30 \ k60 | <2 | 2-6 | 6-13 | 13-23 | 23-36 | ≥36 |
|---|---|---|---|---|---|---|
| <2 | 0.88 | 1.10 | 1.22 | 1.28 | 1.48 | 1.90 |
| 2-4 | — | 0.84 | 1.21 | 1.12 | 1.36 | 1.91 |
| 4-10 | — | 0.78 | 0.79 | 0.98 | 1.15 | 1.70 |
| 10-17 | — | — | 0.71 | 0.76 | 1.07 | 1.53 |
| 17-27 | — | — | — | 0.76 | 0.91 | 1.53 |
| ≥27 | — | — | — | — | 1.03 | 1.88 |

## k60 × k120 — pf (n)

| k60 \ k120 | <3 | 3-8 | 8-17 | 17-31 | 31-50 | ≥50 |
|---|---|---|---|---|---|---|
| <2 | 1.39 (33,112) | 1.41 (1,620) | 1.75 (2,703) | 1.60 (3,002) | 1.79 (1,915) | 1.84 (1,051) |
| 2-6 | 1.41 (18,507) | 1.39 (62,128) | 1.63 (8,481) | 1.63 (9,066) | 1.64 (5,362) | 1.88 (2,828) |
| 6-13 | — | 1.41 (39,891) | 1.40 (102,097) | 1.68 (20,685) | 1.69 (12,322) | 1.95 (6,574) |
| 13-23 | — | — | 1.36 (58,762) | 1.42 (85,420) | 1.83 (17,257) | 1.96 (9,647) |
| 23-36 | — | — | — | 1.39 (57,126) | 1.63 (36,974) | **2.47 (11,042)** |
| ≥36 | — | — | — | — | 1.60 (34,721) | 2.42 (37,953) |

avg% per trip:

| k60 \ k120 | <3 | 3-8 | 8-17 | 17-31 | 31-50 | ≥50 |
|---|---|---|---|---|---|---|
| <2 | 0.70 | 0.95 | 1.44 | 1.34 | 1.73 | 1.95 |
| 2-6 | 0.71 | 0.67 | 1.12 | 1.20 | 1.33 | 1.74 |
| 6-13 | — | 0.68 | 0.68 | 1.09 | 1.24 | 1.66 |
| 13-23 | — | — | 0.64 | 0.76 | 1.32 | 1.63 |
| 23-36 | — | — | — | 0.75 | 1.14 | 2.16 |
| ≥36 | — | — | — | — | 1.15 | 2.32 |

## k120 × k180 — pf (n)

| k120 \ k180 | <4 | 4-10 | 10-21 | 21-38 | 38-61 | ≥61 |
|---|---|---|---|---|---|---|
| <3 | 1.35 (41,691) | 1.88 (1,463) | 1.66 (2,753) | 1.36 (2,993) | 1.55 (1,750) | 1.68 (969) |
| 3-8 | 1.37 (15,625) | 1.37 (68,672) | 1.72 (6,589) | 1.41 (6,897) | 1.39 (3,796) | 1.68 (2,060) |
| 8-17 | — | 1.39 (33,229) | 1.38 (113,057) | 1.59 (13,676) | 1.33 (7,911) | 1.70 (4,170) |
| 17-31 | — | — | 1.38 (50,624) | 1.47 (106,444) | 1.50 (11,313) | 1.66 (6,918) |
| 31-50 | — | — | — | 1.56 (44,367) | 1.71 (55,241) | 1.82 (8,943) |
| ≥50 | — | — | — | — | 1.85 (23,770) | **2.52 (45,325)** |

avg% per trip:

| k120 \ k180 | <4 | 4-10 | 10-21 | 21-38 | 38-61 | ≥61 |
|---|---|---|---|---|---|---|
| <3 | 0.62 | 1.33 | 1.17 | 0.75 | 1.12 | 1.37 |
| 3-8 | 0.63 | 0.63 | 1.04 | 0.76 | 0.77 | 1.32 |
| 8-17 | — | 0.66 | 0.66 | 0.93 | 0.67 | 1.29 |
| 17-31 | — | — | 0.69 | 0.84 | 0.99 | 1.35 |
| 31-50 | — | — | — | 1.03 | 1.26 | 1.68 |
| ≥50 | — | — | — | — | 1.48 | 2.43 |

## k180 × k300 — pf (n)

| k180 \ k300 | <6 | 6-13 | 13-26 | 26-47 | 47-78 | ≥78 |
|---|---|---|---|---|---|---|
| <4 | 1.30 (41,463) | 1.70 (2,741) | 1.45 (4,847) | 1.55 (4,404) | 1.49 (2,567) | **1.25 (1,294)** |
| 4-10 | 1.33 (24,186) | 1.34 (55,154) | 1.52 (8,704) | 1.75 (8,441) | 1.36 (4,391) | 1.38 (2,488) |
| 10-21 | — | 1.33 (39,156) | 1.36 (105,394) | 1.70 (15,286) | 1.64 (8,360) | 1.68 (4,827) |
| 21-38 | — | — | 1.38 (50,077) | 1.46 (103,206) | 2.02 (13,449) | 2.12 (7,645) |
| 38-61 | — | — | — | 1.51 (42,277) | 1.64 (51,383) | **2.75 (10,121)** |
| ≥61 | — | — | — | — | 1.69 (26,532) | 2.59 (41,853) |

avg% per trip:

| k180 \ k300 | <6 | 6-13 | 13-26 | 26-47 | 47-78 | ≥78 |
|---|---|---|---|---|---|---|
| <4 | 0.53 | 0.99 | 0.79 | 0.89 | 0.93 | 0.55 |
| 4-10 | 0.56 | 0.59 | 0.81 | 1.04 | 0.68 | 0.71 |
| 10-21 | — | 0.59 | 0.64 | 1.06 | 1.02 | 1.13 |
| 21-38 | — | — | 0.69 | 0.85 | 1.50 | 1.71 |
| 38-61 | — | — | — | 0.97 | 1.19 | 2.37 |
| ≥61 | — | — | — | — | 1.31 | 2.61 |

## k300 × k600 (kstudy replication of S16d) — pf (n)

| k300 \ k600 | <7 | 7-16 | 16-34 | 34-65 | 65-105 | ≥105 |
|---|---|---|---|---|---|---|
| <6 | 1.37 (50,192) | 1.19 (3,113) | 1.18 (4,758) | **1.07 (4,253)** | 1.14 (2,343) | 1.29 (990) |
| 6-13 | 1.39 (10,032) | 1.38 (66,353) | 1.33 (7,773) | 1.12 (7,488) | 1.10 (3,758) | 1.57 (1,647) |
| 13-26 | — | 1.38 (31,453) | 1.40 (105,890) | 1.19 (18,030) | 1.36 (9,307) | 1.51 (4,342) |
| 26-47 | — | — | 1.44 (58,904) | 1.51 (87,128) | 1.56 (18,841) | 1.71 (8,741) |
| 47-78 | — | — | — | 1.54 (53,187) | 1.65 (40,297) | **2.57 (13,198)** |
| ≥78 | — | — | — | — | 2.06 (28,621) | 2.67 (39,607) |

avg% per trip:

| k300 \ k600 | <7 | 7-16 | 16-34 | 34-65 | 65-105 | ≥105 |
|---|---|---|---|---|---|---|
| <6 | 0.62 | 0.36 | 0.33 | 0.16 | 0.28 | 0.62 |
| 6-13 | 0.65 | 0.65 | 0.55 | 0.25 | 0.21 | 0.99 |
| 13-26 | — | 0.66 | 0.70 | 0.39 | 0.66 | 0.93 |
| 26-47 | — | — | 0.80 | 0.92 | 0.99 | 1.25 |
| 47-78 | — | — | — | 1.00 | 1.20 | 2.16 |
| ≥78 | — | — | — | — | 1.76 | 2.62 |

## Reads — where the gradient dies

1. **The dip-and-recover penalty is a SLOW-horizon phenomenon.** At 30s and 1m the
   low-short × high-long cells are FINE (1.74-1.93 — even good): a 30s/1m pullback is
   noise, not resilience. The penalty first appears at 3m×5m (k180<4 × k300≥78 → 1.25,
   avg 0.55%) and is fully formed at 5m×10m (k300<6 × k600 34-105 → **1.07-1.14, avg
   0.16-0.28%** — the worst cells on the whole ladder). A dip only certifies the
   up-move's resilience when it is deep enough to matter — ≥3m scale.
2. **Below ~2m the short counter is uninformative given the longer one**: within any
   fixed k60/k120 column, the k30/k60 gradient is flat-to-slightly-negative. The fast
   rungs add nothing the 2m+ rungs don't know — EXCEPT their deep diagonal corner.
3. **Within-row (longer counter) gradient stays informative at every pair** — the right
   edge of every row is its best cell, and the deep corners strengthen with horizon:
   2.08 (30/60) → 2.47 (1m/2m) → 2.52 (2m/3m) → 2.75 (3m/5m) → 2.67 (5m/10m).
4. Off-diagonal "long much deeper than short, short still mature" cells (k180 38-61 ×
   k300 ≥78 → 2.75; k60 23-36 × k120 ≥50 → 2.47) rival or beat the pure diagonal — the
   best trips have a mature long leg with the short leg one notch behind.

**Verdict: the informative range of the ladder is 2m→10m; 30s/1m are redundant.
The 5m/10m pair carries the sharpest dip-and-recover discrimination (which the adopted
k300+k600 combo already exploits); 3m adds the same signal in weaker form.**

## S18b — the fast rungs INSIDE the adopted K gate (k300 ≥ 40 ∧ k600 ≥ 60): prediction WRONG, they still pay

Book: 144,724 trips, PF 2.061. The ungated read ("fast rungs redundant") does NOT carry
into the gated book — conditioning on mature 5m/10m legs changes what a fast dip means.

**1D marginals inside the gate** (bands ≈ in-gate quantiles):

| band | k30 pf | k60 pf | k120 pf | k180 pf |
|---|---|---|---|---|
| lowest | 1.943 | 1.721 | **1.535** | **1.371** |
| 2nd | 1.888 | 1.770 | 1.605 | 1.809 |
| 3rd | 1.894 | 1.891 | 1.793 | 2.321 |
| 4th | 2.003 | 2.110 | 2.110 | 2.045 |
| 5th | 2.252 | 2.211 | 2.186 | 1.844 |
| top | **2.599** | **2.587** | **2.672** | 2.601 |

**Pairwise grids inside the gate** (full tables in `sf_s18b.out`): the deep-diagonal
corners are the headline —

- k30 ≥ 36 × k60 ≥ 46 → **2.82 (n 14,176)**
- k60 ≥ 46 × k120 ≥ 64 → **3.03 (n 16,703)**
- k120 ≥ 64 × k180 ≥ 78 → **3.14 (n 21,127)**

and the low-fast cells are the anti-signal: k120 < 10 → 1.53 (avg 1.01%), k180 < 14 →
1.37 (avg 0.71%), with pockets like k120 10-20 × k180 41-58 → 1.25.

**Read: once the slow legs are mature, a recent SHALLOW dip becomes a warning too** —
in the ungated frame a 30s-2m pullback was noise, but within a mature 5m/10m leg it
marks the same dip-and-recover resilience, fractally. The completely unbroken climb —
no dip at ANY horizon, every rung deep — is the A-tier: **PF ~3.0-3.14 on ~15-21k trips
vs the gated book's 2.061.** k120 (2m) is the cleanest monotone fast lever.

⏭ Candidate tier-2 gates from this: k120 ≥ 32 (or ≥ 47 / ≥ 64 by appetite) on top of the
K combo — needs the year table + iso-trip controls before adoption (this was a survey
pass, no year columns yet).

# S19 (2026-08-28) — STACK LOCKED (+ k180 ≥ 15); k120/k60 trim sweeps

⏭ **Noted for a future VOICE FAMILY, not adopted (user)**: the S18b deep-diagonal cell
`k120 ≥ 64 ∧ k180 ≥ 78` (PF 3.14 @ 21,127). ⚠ By the triangle it implies k300/k600 ≥ 64,
i.e. as a GATE it would swallow the slow floors — that is why it stays a voice candidate.

## The lock

**STACK (locked, user): speed_1m > 2% ∧ dist_lo > 2% ∧ eff_10m ≥ 0.3 ∧ k300 ≥ 40 ∧
k600 ≥ 60 ∧ k180 ≥ 15.**

| book | n | avg% | p1 | win% | pf | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| K combo (S16c) | 144,724 | 1.78 | −25.3 | 72.8 | 2.061 | 2.60 | 2.01 | 1.49 | 1.61 | 1.86 | 2.17 | 2.58 |
| **+ k180 ≥ 15 (LOCKED)** | 131,165 | 1.89 | −25.4 | 73.3 | **2.140** | 2.65 | 2.02 | 1.49 | 1.63 | 1.99 | 2.28 | **2.95** |

The trim costs 9.4% of trips for +0.08 PF and a notably better 2026 (2.58 → 2.95).

## k120 floor sweep (on the locked stack) — monotone, no knee

| floor | n | avg% | pf | 2023 | 2026 |
|---|---|---|---|---|---|
| off | 131,165 | 1.89 | 2.140 | 1.63 | 2.95 |
| ≥8 | 125,011 | 1.91 | 2.157 | 1.58 | 3.01 |
| ≥12 | 121,462 | 1.93 | 2.175 | 1.57 | 3.06 |
| ≥16 | 116,796 | 1.96 | 2.200 | 1.56 | 3.10 |
| ≥20 | 109,549 | 2.01 | 2.238 | 1.53 | 3.16 |
| ≥25 | 100,862 | 2.07 | 2.287 | 1.51 | 3.22 |
| ≥32 | 89,351 | 2.15 | 2.349 | 1.50 | 3.37 |

Every year improves EXCEPT 2023, which bleeds slowly (1.63 → 1.50). A book-size dial,
not a threshold with structure.

## k60 floor sweep — weaker, and pays with the recent years

≥13 → 2.215 @ 101,847 but 2026 2.95 → 2.81 and 2024 1.99 → 1.94 (helping 2020/2021).
The 2D floor combo confirms redundancy: at k120 ≥ 20, adding k60 ≥ 13 buys +0.05 PF for
−17k trips.

**Verdict: k120 trimming helps modestly and monotonically (2023 the lone cost); k60 adds
nothing beyond k120 and trades the recent years for the COVID years — skip it.** Whether
to take a k120 floor (≥12-16 looks sensible) is a book-size call at assembly.

# S20 (2026-08-28) — the volume rate ratio, 1m vs FIRST-15m anchor (user: the cum baseline drifts intraday; anchor at the open)

`rr_15m = vol_60 / (vol_0945_tape · 60/900)` — last-minute volume rate vs the day's
opening-15m rate, both tape-native time-clock columns. Frame: LOCKED stack, mc=0,
131,165 trips (rr never null). Median rr 2.23 (p10 0.77, p90 7.5).

## Fine bands: the shape is a U, not the monotone quiet story

| band | n | avg% | p1 | win% | pf | trips/tkd |
|---|---|---|---|---|---|---|
| <0.3 | 1,085 | 1.50 | — | 75.8 | 2.52* | 4.5 |
| [0.3,0.5) | 3,842 | 1.53 | −13.5 | 72.4 | 2.517 | 5.4 |
| [0.5,0.75) | 7,580 | 1.80 | −14.3 | 73.9 | 2.661 | 5.8 |
| [0.75,1) | 9,452 | 1.34 | −19.4 | 70.7 | 1.948 | 5.9 |
| [1,1.5) | 19,101 | 1.19 | −25.6 | 71.6 | 1.716 | 7.8 |
| [1.5,2) | 17,490 | 1.17 | −33.3 | 72.6 | **1.599** | 7.6 |
| [2,3) | 23,925 | 1.88 | −24.6 | 72.9 | 2.112 | 9.5 |
| [3,5) | 23,406 | 2.10 | −23.7 | 73.4 | 2.222 | 11.2 |
| ≥5 | 25,284 | 3.01 | −29.9 | 76.0 | 2.739 | **17.4** |

(*bands <0.3 merged for the density line.) ⚠ **The loud arm carries the eff_20m density
signature**: ≥5 fires 17.4 trips/tkd vs 4.5-5.9 quiet — mc=0 overweights blowout days
exactly there. The QUIET arm is density-clean (at/below the frame's average).

## Candidate gates (year columns)

| gate | n | avg% | p1 | pf | worst yr | 2026 |
|---|---|---|---|---|---|---|
| locked stack | 131,165 | 1.89 | −25.4 | 2.140 | 1.49 | 2.95 |
| **quiet rr < 0.75** | 12,507 | 1.69 | **−14.3** | **2.607** | 1.65 (2021) | 3.80 |
| quiet rr < 1 | 21,959 | 1.54 | −16.8 | 2.275 | 1.66 | 3.60 |
| trough [1,3) | 60,516 | 1.46 | −27.5 | 1.829 | 1.25 (2023) | 2.26 |
| loud ≥ 3 ⚠ | 48,690 | 2.57 | −26.9 | 2.492 | 1.26 (2022) | 3.34 |
| U: <1 or ≥3 | 70,649 | 2.25 | −23.5 | 2.440 | 1.34 (2022) | 3.38 |

## Reads

1. **The quiet lever survives the locked stack with the 15m anchor** (S11b/F23b
   consistent): rr < 0.75 → PF 2.607 with the tail HALVED (p1 −14.3 vs −25.4), every
   year ≥ 1.65, recent years the strongest (2025 3.18, 2026 3.80). Density-clean.
   Mechanism reads true: a pop on thin volume has no fuel to squeeze.
2. **The loud arm (≥3-5×) looks strong but is density-suspect** — 2-3× the signal rate
   per ticker-day, 2022 its worst year (1.26). Needs a replay/tkd-level verification
   before it is believed; do NOT adopt from this table.
3. **The trough [1,3) is 46% of the book at PF 1.83** — the ordinary-rate pop is the
   weakest thing the locked stack admits (2023 1.25, p1 −27.5 to −33.3).

## S20d — rr_15m at mc=1 (user): BOTH arms survive the inside-frame replay; loud ≥5 is the eqw engine

("Inside-frame replay" = the three-mc-questions third form: the gate is live DURING the
greedy replay, so failing signals never occupy the ticker-day slot.)

**Milestone in passing**: the locked 6-gate stack at mc=1 = PF 1.530 @ 7,945 trips,
eqw 100.9 bp/day — already matching the old 15-gate spec book (1.563 @ 4,997, 93.8
bp/day) on 1.6× the trips, with every gate honestly derived.

| gate (mc=1 inside frame) | n | avg% | p1 | pf | eqw bp/d | worst yr | 2026 |
|---|---|---|---|---|---|---|---|
| locked stack (no rr) | 7,945 | 0.83 | −21.8 | 1.530 | 100.9 | 1.15 (2022) | 1.96 |
| quiet rr < 0.75 | 1,739 | 0.85 | **−15.9** | 1.698 | 98.2 | 1.44 (2020) | 2.48 |
| quiet rr < 1 | 2,602 | 0.76 | −18.8 | 1.585 | 95.5 | 1.26 (2020) | 2.94 |
| loud rr ≥ 3 | 3,070 | 1.20 | −25.0 | 1.672 | 151.2 | 1.05 (2022) | 1.63 |
| **loud rr ≥ 5** | 1,668 | 1.67 | −26.6 | **1.962** | **179.0** | 1.42 (2022) | 1.78 |
| U: <1 or ≥3 | 5,549 | 1.00 | −21.1 | 1.647 | 124.9 | 1.18 (2022) | 1.94 |

Bare-replay band shape (view (a), scratch `sf_s20d.out`): the U flattens at mc=1 but
persists — <0.3 → 3.05 (n 162, every year ≥ 2.36), trough 1-2 → 1.33-1.40, ≥5 → 1.78.

**Reads:**
1. **The loud arm survives the replay control** — the S20 density worry was about
   within-day multi-signal inflation, and mc=1 removes exactly that: rr ≥ 5 still reads
   1.962 with eqw 179 bp/day (baseline 101). What remains unresolved is regime tilt:
   2022 = 1.42 (its worst), vs the quiet arm's 2022 = 1.59.
2. **The quiet arm holds with the better TAIL** (p1 −15.9 vs −26.6) and the better
   recent years (2026: 2.48/2.94 vs 1.78/1.63) — quiet and loud are different trades:
   thin-fuel fades (safe, recent-regime-loving) vs blowout exhaustion (bigger per-trip,
   2020/2024-loving, worse tails).
3. The trough [1,3) exclusion nets 1.647 @ 5,549 — the honest aggregate if both arms
   are kept as one gate; as VOICES they are better kept separate (opposite regime tilts).

# S21 (2026-08-28) — gap_60 vs the rate ratio (user: "does it beat the rates?"). NO — it points the OTHER WAY, and refines them instead

Locked stack; gap_60 = missing non-halt seconds in the trailing minute. Distribution:
62% of the stack has gap_60 = 0; p90 = 21. **corr(gap_60, rr) = −0.06 (−0.18 in log) —
gap and rate are nearly INDEPENDENT thinness measures.**

## Bands (condensed; full tables in `sf_s21.out`)

mc=0: gap=0 → 2.257 (n 81,099, avg 2.23%) declining to 1.51 at 11-20 gaps; the >40 band
(2.54) is artifact-shaped (p1 −48.5, wild years). mc=1 bare-replay: same decline, =0 →
1.62, 21-40 → 1.03. **mc=1 inside-frame floors all HURT**: gap ≥ 3 → 1.303, ≥ 6 →
1.309, ≥ 11 → 1.247 (baseline 1.530). On the SHORT fader, a holey tape is bad for the
fade — the OPPOSITE of FlushFader's long-side gap levers. Mechanism: thin-presence pops
carry wide spreads/erratic marks; the fade wants a continuously printing tape.

## ⭐ The refinement: CONTINUITY × QUIET — the best mc=1 cell of the rebuild

2D (mc=0): rr < 0.75 × gap=0 → 4.42 (n 4,438); quiet-with-holes → 1.19-1.32. At mc=1
INSIDE the frame:

| gate | n | avg% | p1 | win% | pf | eqw bp/d | worst yr |
|---|---|---|---|---|---|---|---|
| rr < 0.75 (S20d) | 1,739 | 0.85 | −15.9 | 69.0 | 1.698 | 98.2 | 1.44 |
| **rr < 0.75 ∧ gap = 0** | 530 | 1.93 | −17.6 | 77.5 | **2.861** | **202.4** | 1.58 (2020) |
| rr < 0.75 ∧ gap ≤ 2 | 655 | 1.66 | −16.9 | 75.7 | 2.501 | 164.6 | 1.59 |
| gap = 0 alone | 4,661 | 1.25 | −25.7 | 72.5 | 1.721 | 139.6 | 1.20 (2022) |

(2025 PF 13.8 in the gap=0 cell = tiny-loss-denominator; flag, not signal.)

**Reads:** (1) gap_60 does NOT replace the rate ratio — as a floor it subtracts value
everywhere. (2) Its real role is a CONTINUITY requirement: "volume running below the
morning rate while the tape prints every second" = sellers absent but market present —
PF 2.861 / +202 bp/day / every year ≥ 1.58 on ~80 trips/yr. The strongest voice
candidate found today (join the voice family list: k120×k180 diagonal, rr quiet, rr
loud, now quiet×continuous). (3) gap = 0 alone is a mild honest lever (1.721).

## S21b — the 2D at TRUE mc=1 (user ruling: filters ARE gates in the replay, slot per ticker-day, one replay per cell)

⚠ Convention note: `gap_60` (used throughout S21) is the RAW counter — classified-halt
seconds INCLUDED; the halt-excluded twin is `gap_adj_60`. gap = 0 is identical under
both. **Method ruling recorded (also in memory): from now on "mc=1" always means
gates-in-replay per (ticker, date); no more bare-replay banding.**

| gap_60 \ rr | <0.75 | 0.75-1.5 | 1.5-3 | 3-5 | ≥5 |
|---|---|---|---|---|---|
| =0 | **2.86 (530)** | 1.65 (1,471) | 1.67 (2,163) | 1.72 (1,465) | 2.02 (1,246) |
| 1-2 | 2.46 (233) | 2.00 (509) | 1.84 (759) | 1.92 (523) | 1.96 (362) |
| 3-5 | 2.00 (189) | 1.41 (428) | 1.68 (539) | 1.70 (351) | 3.04 (234) |
| 6-10 | 1.54 (204) | 1.90 (458) | 1.63 (537) | 1.42 (282) | 1.76 (192) |
| 11-20 | 1.72 (307) | 1.18 (565) | 1.37 (553) | 1.44 (265) | 2.81 (195) |
| 21-40 | 1.11 (599) | 1.07 (709) | 1.03 (525) | 1.33 (186) | **3.75 (192)** |
| >40 | 1.02 (201) | 1.20 (190) | 1.25 (160) | 1.05 (114) | 3.00 (157) |

Reads:
1. **The quiet column decays monotonically with gaps** (2.86 → 1.02): continuity is not
   a bonus on the quiet cell, it is a REQUIREMENT — quiet-with-holes is dead weight.
2. **The gap=0 row is a U in rr** (2.86 / ~1.7 / 2.02) — both S20 arms visible on a
   continuous tape.
3. **Loud × gappy survives true mc=1** (3.04 / 2.81 / 3.75 / 3.00 on n 157-234). ⚠
   HALT-ENTANGLED: raw gap_60 counts LULD pauses, so these cells are largely post-halt
   blowoffs — the retired cascade-gate lore lives here. Re-examine with gap_adj_60 /
   halts_today before believing; small n, and S11d's rr_wall finding sits adjacent.

# S22 (2026-08-28) — gap_adj_60: the halt-excluded twin. True mc=1 throughout

`gap_adj_60` = missing seconds in the trailing minute EXCLUDING classified halts (the
S40x detector); `gap_60` is the raw counter, halts included. On the locked stack only
**3.2%** of trips (4,159 of 131,165) have halt seconds in the trailing minute — but S21b's
most glamorous cells live exactly there. Every number below is true mc=1: filters as
gates inside the greedy replay, slot per (ticker, date), one replay per cell.

## 1D: gap_adj_60 bands (one replay per band)

| gap_adj_60 | n | pf |
|---|---|---|
| =0 | 4,712 | 1.71 |
| 1-2 | 1,785 | 2.06 |
| 3-5 | 1,315 | 1.58 |
| 6-10 | 1,196 | 1.62 |
| 11-20 | 1,249 | 1.21 |
| 21-40 | 1,321 | 1.09 |
| >40 | 339 | 1.25 |

Same monotone decay as the raw counter (S21): GENUINE sparsity — not halts — is what
kills the fade.

## 2D: gap_adj_60 × rr_15m — pf (n)

| gap_adj \ rr | <0.75 | 0.75-1.5 | 1.5-3 | 3-5 | ≥5 |
|---|---|---|---|---|---|
| =0 | **2.87 (531)** | 1.65 (1,480) | 1.61 (2,185) | 1.70 (1,502) | 2.07 (1,309) |
| 1-2 | 2.46 (233) | 1.98 (507) | 1.87 (748) | 1.93 (514) | 1.84 (338) |
| 3-5 | 2.00 (189) | 1.38 (425) | 1.55 (527) | 1.64 (339) | 1.95 (202) |
| 6-10 | 1.54 (204) | 1.75 (453) | 1.67 (519) | 1.18 (265) | 1.25 (146) |
| 11-20 | 1.59 (306) | 1.13 (561) | 1.16 (524) | 1.23 (241) | 1.11 (114) |
| 21-40 | 1.07 (597) | 1.02 (699) | 0.97 (475) | 0.90 (144) | 2.84 (64) |
| >40 | 1.05 (199) | 1.06 (158) | 0.92 (67) | 1.95 (17) | 4.70 (10) |

Compare S21b (raw gap_60 rows): the bottom-right corner has EVAPORATED — rr≥5 ×
gap 21-40 was 3.75 on n 192 raw, and is 2.84 on n **64** adjusted; >40 keeps n **10**.
The trips didn't disappear — they migrated up to the =0/1-2 rows once their gaps were
recognized as halts (loud × adj=0 ticks up 2.02 → 2.07). Meanwhile the sparse loud rows
that remain (6-20 adjusted gaps × ≥3) drop to 1.11-1.25: with halts removed, "loud on a
thin tape" has no edge at all.

## The direct split: loud (rr ≥ 3) × raw-gappy (gap_60 ≥ 11)

| subset | n | pf |
|---|---|---|
| halt-driven (gap_adj ≤ 2) | 249 | **1.86** |
| gap_adj 3-10 | 0 | — (structurally empty: a classified halt owns its WHOLE gap run) |
| genuinely sparse (gap_adj ≥ 11) | 416 | **1.18** |

## The quiet×continuous voice is convention-proof

| gate | n | pf |
|---|---|---|
| rr < 0.75 ∧ raw gap_60 = 0 (S21) | 530 | 2.86 |
| rr < 0.75 ∧ gap_adj_60 = 0 | 531 | 2.87 |
| the difference (halt-forgiven trips) | 3 | — |

**Reads:** (1) S21b's "loud on a holey tape" cells were halt-resumption fades in
disguise — a real but modest 1.86 pocket on ~250 trips, cascade-lore territory,
deferred to the halts_today study; genuinely sparse loud tape is DEAD (1.18).
(2) All S21 conclusions stand with gap_adj as the interpretive counter; the
quiet×continuous voice is unchanged. (3) `gap_adj_60` is the right counter for
TAPE-TEXTURE questions; `gap_60` − `gap_adj_60` > 0 is a free halt-in-last-minute flag.

# S23 (2026-08-28) — halts_today × secs_since_halt on the locked stack. True mc=1 throughout

Context for a first read: `halts_today` = running count of classified volatility halts
(S40x detector) on the ticker-day up to the signal; `secs_since_halt` = seconds since the
last resume (only meaningful when halts_today ≥ 1). On the SHORT fader these are
limit-UP-flavored halts — the mirror of the long side's cascade-gate lore (S42n/S42t),
which was retired with the spec transplant in S17. Frame: locked stack, 131,165 mc=0
trips; 14.4% have halts_today ≥ 1. All numbers gates-in-replay, one replay per cell.

## 1D: halts_today

| halts_today | n (mc=1) | pf | win% |
|---|---|---|---|
| 0 | 7,161 | 1.52 | 69.0 |
| 1 | 549 | 1.85 | 71.2 |
| 2 | 214 | 1.88 | 73.4 |
| 3 | 139 | 1.69 | 69.8 |
| ≥4 | 197 | 1.56 | 74.1 |

## 2D: halts_today × secs_since_halt — pf (n)

| ht \ since resume | <2m | 2-5m | 5-20m | 20-80m | >80m |
|---|---|---|---|---|---|
| 1 | 1.89 (207) | **3.32 (86)** | 1.73 (70) | 1.80 (102) | 1.63 (180) |
| 2 | 2.33 (98) | 2.99 (29) | 1.99 (38) | 1.17 (38) | 3.60 (49) |
| 3 | 1.95 (65) | **4.76 (27)** | 3.27 (19) | 1.24 (22) | 1.37 (38) |
| ≥4 | 1.45 (101) | 1.43 (43) | 1.70 (36) | **0.97 (35)** | 1.25 (62) |

## The S22 halt-driven loud pocket, resolved

| gate | n | pf | win% |
|---|---|---|---|
| ht=1 ∧ rr≥3 ∧ since-resume <5m | 181 | **2.42** | 74.6 |
| ht≥2 ∧ rr≥3 ∧ since-resume <5m | 167 | 1.91 | 74.3 |
| ht≥1 ∧ rr≥3 ∧ since-resume 5-20m | 83 | 1.42 | 67.5 |
| ht≥1 ∧ rr≥3 ∧ since-resume ≥20m | 151 | **1.00** | 61.6 |

## Reads

1. **A halted name fades mildly better, peaking at 1-2 halts** (1.85-1.88 vs 1.52
   unhalted) — and the edge lives in the FIRST MINUTES after the resume: the [2,5m)
   post-resume window is the best column (3.32 / 2.99 / 4.76, small n). Short-side
   mirror of S42p: on the long side [2,5m) was where the next limit-DOWN decided itself
   (dangerous); here a pop that fires 2-5m after a limit-UP resume is a failed
   re-launch — and it fades hard.
2. **Serial breakers (ht ≥ 4) have no edge anywhere** (0.97-1.70) — "don't fade the
   LULD elevator" survives the direction flip.
3. The S22 pocket is now fully explained: it was FRESH-resume blowoff fading, best on
   the first halt (2.42 @ 181), decaying with both halt count and staleness to exactly
   1.00 at ≥20m. ⏭ Voice candidate (small): ht 1-3 ∧ since-resume <5m ∧ rr ≥ 3 —
   ~350 trips at ~2.1-2.4. Not adopted; n small and 2022-style cascade regimes untested
   year-by-year.

# S24 (2026-08-28) — why rr is less decisive here than in MaxRiderV1 (user question)

Benchmark being compared against: F22/F23 on MaxRiderV1 — quiet book PF 3.62, rate-ratio
fine ladder rr < 0.2 → **5.90**, monotone quiet-dominant, on its 1m corpus.

## Control: rr_15m amplitude with NO gates (bare base_v2, 2.089M trips, mc=0)

| band | bare n | bare pf | speed-stack pf | locked-stack pf (S20) |
|---|---|---|---|---|
| <0.2 | 121,970 | 1.902 | 2.200 | ~2.5* |
| [0.2,0.3) | 145,995 | 1.677 | 2.079 | 2.13* |
| [0.3,0.5) | 315,123 | 1.478 | 1.778 | 2.52 |
| [0.5,0.75) | 338,909 | 1.283 | 1.430 | 2.66 |
| [0.75,1) | 249,208 | 1.223 | 1.286 | 1.95 |
| [1,1.5) | 314,237 | 1.182 | 1.197 | 1.72 |
| [1.5,2) | 182,459 | 1.221 | 1.216 | 1.60 |
| [2,3) | 186,257 | 1.376 | 1.399 | 2.11 |
| [3,5) | 133,262 | 1.387 | 1.398 | 2.22 |
| ≥5 | 101,158 | 1.700 | 1.739 | 2.74 |

(*S20 used coarser low bands.) The U exists at EVERY gate level, and the quiet arm's
amplitude RELATIVE to its local baseline is roughly constant (~1.2-1.45×) — so **stack
absorption is real but is NOT the main story: even the bare 1s corpus never shows
MaxRider's 2-3× quiet dominance.**

## The structural differences (in decreasing order of suspicion)

1. 🛑 **The benchmark itself is suspect.** MaxRiderV1 runs on `diprider_v6_candidate`,
   which carries the S39d lookahead pair (day-D ADJUSTED $1 floor = future-reverse-split
   detector + episode-length warmup = outcome selection); the system is stamped INVALID
   pending a clean-table rerun, and F22/F23 were deliberately run on the dirty table
   (user: replicate first, clean later). The inflation concentrates in exactly the thin,
   quiet, split-prone slices where the 5.90 cell lives. The honest MaxRider quiet number
   is UNKNOWN and likely smaller.
2. **Different universe.** diprider_v6_candidate = 1m-gated, rvol/dollar-floored,
   IN-PLAY names. mr_candidate_1s_v2 = every tape with $2M by 09:45, no price floor, no
   warmup — full of microcaps where "quiet" is the resting state, not a signal.
3. **Different signal event.** MaxRider fires on 1m-bar SESSION highs (rare, a few per
   day); the SpikeFader base samples every 1s 20m-channel high (dense). A dense
   sampler's marginal signal is weaker, compressing all band contrasts.
4. **Different exits.** MaxRider's 1m exit vs the 5m-low trail + MOC — PF scales are not
   comparable across the two books even for identical selections.

**Verdict: rr is a genuine but SECOND-ORDER lever on the 1s fader (best expressed as the
quiet×continuous voice, S21-S22); MaxRider's decisive version was measured on an
invalid universe with a rarer event and a different exit.**

**✅ RESOLVED same day (MaxRider F24): the clean-table rerun happened.** Honest deep-quiet
= rr<0.2 → **2.816** (entry ≥ $1: 3.163) vs dirty 5.902 — the contamination was ~half the
lever, concentrated exactly in the quiet band (5,631 dirty trips → 1,124 clean). The two
systems AGREE once both are honest: quiet ≈ 2.5-3.2 on both.

# S26 (2026-08-28) — STACK += gap_adj_60 < 10 (user: "it earned its place")

**STACK (locked, user): speed_1m > 2% ∧ dist_lo > 2% ∧ eff_10m ≥ 0.3 ∧ k300 ≥ 40 ∧
k600 ≥ 60 ∧ k180 ≥ 15 ∧ gap_adj_60 < 10** — six levers, all re-derived on honest
corpora: speed (S13), verticality (S14), maturity ×3 (S16/S19), tape continuity (S21/S22).

> ⚠⚠ **SUPERSEDED 2026-08-31 (S37f-s): `k600 ≥ 60` → `k600 ≥ 90`, and the EXIT
> channel 300 → 540 bars.** k600 is the load-bearing maturity floor (k300 stays at
> 40 — still load-bearing as a FLOOR, but useless as a lever). PF−1 0.727 → 1.098
> (+51%) for −25.5% net, all seven years up, all four controls passed. The tables
> in THIS section predate both changes; read S37f-s for the current spec.

| view | book | n | avg% | p1 | win% | pf | eqw bp/d |
|---|---|---|---|---|---|---|---|
| mc=0 | S19 stack | 131,165 | 1.89 | −25.4 | 73.3 | 2.140 | — |
| mc=0 | **+ gap_adj<10** | 111,460 | 2.17 | −26.3 | 74.9 | **2.288** | — |
| mc=1 | S19 stack | 7,945 | 0.83 | −21.8 | 69.3 | 1.530 | 100.9 |
| mc=1 | **+ gap_adj<10** | 6,295 | 1.05 | −24.1 | 71.3 | **1.642** | **120.2** |

mc=1 years (pf): 1.65 / 1.72 / 1.23 / 1.42 / 1.68 / 1.65 / 2.10 — every year up or flat
vs S19; 2022 stays the floor.

## mc=1 headline year table (the tradeable read)

| yr | days | eqw bp | med bp | up% | tkd | trim bp |
|---|---|---|---|---|---|---|
| 2020 | 226 | 125.7 | 150.3 | 75.2 | 929 | 119.9 |
| 2021 | 238 | 108.7 | 151.7 | 73.5 | 1,154 | 105.2 |
| 2022 | 208 | 82.8 | 131.6 | 65.4 | 554 | 75.7 |
| 2023 | 215 | 80.7 | 220.2 | 71.6 | 450 | 72.0 |
| 2024 | 224 | 131.4 | 186.5 | 76.3 | 679 | 125.7 |
| 2025 | 232 | 117.6 | 151.9 | 69.0 | 893 | 114.0 |
| 2026 | 149 | 227.3 | 246.8 | 83.9 | 516 | 207.0 |

**+80 to +227 bp/day eqw, positive and trim-positive every year, 2026 the best year on
the book.** For calibration: the old 15-gate transplant read 93.8 bp/day blended; the
six-gate honest stack now clears it in every year but 2023's 80.7 ≈ its 64.6, with
2026 at 227 vs 86.5. Costs/borrow/stops still unmodeled (standing caveat).

# S27 (2026-08-28) — rr on the NEW stack (+ the trip-count question vs MaxRider)

Frame: the S26 locked stack (111,460 mc=0 / 6,295 mc=1). User: MaxRider's pops frame had
~17.8k quiet (<1) and only 4.4k loud (≥5) — how does SpikeFader split?

## The count answer: the two systems are volume-arm MIRRORS

| arm | MaxRider mc=0 (pops>2%) | SpikeFader mc=0 (new stack) | SpikeFader mc=1 |
|---|---|---|---|
| quiet (rr < 1) | ~17,800 | 13,990 | 1,506 |
| loud (rr ≥ 5) | 4,417 | **24,405** | 1,566 |

MaxRider is quiet-heavy (rare 1m session-high events on in-play names); SpikeFader is
LOUD-heavy — the 1s sampler fires repeatedly on blowout days and the maturity stack
selects into them. At true mc=1 the arms balance (~1.5k each).

## The U on the new stack — the gap gate PURIFIED the quiet arm

| rr band | mc=0 n | mc=0 pf | mc=1 n | mc=1 pf |
|---|---|---|---|---|
| <0.3 | 554 | 3.156 | 92 | **3.888** |
| 0.3-0.5 | 2,110 | **4.350** | 326 | **3.255** |
| 0.5-0.75 | 4,667 | 3.712 | 720 | 2.100 |
| 0.75-1 | 6,659 | 2.285 | 1,062 | 1.837 |
| 1-1.5 | 15,142 | 1.881 | 1,871 | 1.535 |
| 1.5-2 | 14,896 | 1.654 | 1,946 | 1.608 |
| 2-3 | 21,159 | 2.287 | 2,306 | 1.719 |
| 3-5 | 21,868 | 2.275 | 2,044 | 1.644 |
| ≥5 | 24,405 | 2.759 | 1,566 | 1.982 |

Arm gates at true mc=1: **rr < 0.75 → 2.170 @ 879** (was 1.698 pre-gap-gate — the
continuity requirement is now baked into the stack, so the naked quiet arm carries most
of the old quiet×continuous cell); rr < 1 → 2.022 @ 1,506; rr ≥ 5 → 1.982 @ 1,566
(unchanged from S20d's 1.962); baseline 1.642.

**Reads:** (1) deep quiet (<0.5) is now A-tier on the stack itself — 3.3-3.9 at mc=1 on
418 trips. (2) The quiet arm beats the loud arm in PF again (2.17 vs 1.98) now that its
holes are gone; loud still owns the volume of opportunities at mc=0. (3) The voice
family updates: quiet-rr (2.17 @ 879) and loud-rr (1.98 @ 1,566) on the NEW stack.

## S27b — the A+ sizing cell + why rr's discrimination collapsed (user post-mortem)

**rr < 0.5, true mc=1 on the S26 stack** (the user's proposed sizing tier, not a gate):

| n | avg% | med% | p1 | win% | pf | trimmed pf (drop worst 5%) |
|---|---|---|---|---|---|---|
| 364 | 1.89 | 2.30 | −12.3 | 75.8 | **3.451** | 8.73 |

| yr | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|
| pf | 8.17 | 2.05 | 5.34 | 2.07 | 3.00 | 3.30 | 4.97 |
| n | 56 | 55 | 40 | 27 | 70 | 78 | 38 |

~55 trips/yr, every year ≥ 2.05, losses concentrated in the bottom 5% (trimmed PF 8.7 —
the PF−1 sizing rule would say big, but n=364 and no stops modeled: treat as a TIER, not
a Kelly input yet).

**Why the discrimination collapsed — three compounding reasons:**
1. **It was never as big at true mc=1 as the mc=0 tables suggested.** S20's dramatic U
   (1.6 trough vs 2.7 loud) was the mc=0 view — density-weighted toward blowout days.
   The honest mc=1 U was always shallow in the middle; the loud arm's mc=0 counts
   compress 16:1 into mc=1 (24,405 → 1,566) vs quiet's 9:1.
2. **The stack absorbed rr's correlates**: K maturity selects sustained-volume days,
   gap_adj eats sparsity, eff_10m eats dead tape. Marginal edge vs baseline moved:
   quiet +0.17 → +0.53 (purified), loud +0.43 → +0.34 (partially absorbed),
   middle ≈ 0 → ≈ 0.
3. **This is what a maturing stack looks like** — the S12 lesson (the volat floor inert
   inside the old spec) recurring as a success instead of a bug: features stop
   discriminating when the stack already knows what they knew. The residual information
   lives in the extremes → consume rr as TIERED SIZING (rr < 0.5 = A+), not as a gate.

**User verdict adopted: NO loud-end trim; rr enters the system as a sizing tier only.**

# S28 (2026-08-28) — time-of-day buckets: the FlushFader last-hour rule INVERTS

⚠ Frame limitation: the corpus inherits FlushFader's hour-before-close entry cap
(EntryEndSec = 54000, the S31b/c calibration) — the true last hour (15:00-16:00) is NOT
in the data. What is observable: the shape across 09:45→15:00, S26 stack.

## mc=0 by signal time

| bucket | n | pf | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|
| 09:45-10:00 | 10,060 | 2.449 | 3.25 | 2.04 | 1.65 | 3.50 | 6.49 | 2.23 | 1.13 |
| 10:00-10:30 | 27,447 | 2.258 | 3.68 | 1.92 | 0.92 | 1.96 | 2.49 | 2.65 | 4.31 |
| 10:30-11:00 | 16,794 | 1.823 | 2.81 | 1.37 | 2.24 | 1.06 | 1.15 | 3.54 | 4.06 |
| 11:00-12:00 | 22,126 | 2.049 | 2.78 | 1.59 | 2.61 | 1.69 | 1.49 | 2.40 | 2.79 |
| 12:00-13:00 | 15,372 | 2.543 | 1.83 | 7.23 | 2.89 | 5.51 | 1.49 | 1.88 | 5.35 |
| 13:00-14:00 | 10,719 | **3.680** | 4.39 | 4.83 | 2.09 | 2.79 | 4.07 | 2.67 | 7.24 |
| 14:00-15:00 | 8,939 | 2.443 | 8.15 | 1.97 | 2.41 | 0.78 | 12.79 | 2.36 | 1.64 |

## true mc=1 per bucket

| bucket | n | pf | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|
| 09:45-10:00 | 738 | 1.630 | 1.97 | 1.59 | 0.87 | 2.82 | 2.95 | 1.32 | 1.29 |
| 10:00-10:30 | 1,709 | 1.531 | 1.52 | 1.63 | 0.92 | 1.27 | 1.83 | 1.48 | 2.29 |
| 10:30-11:00 | 1,015 | 1.632 | 1.96 | 1.36 | 1.03 | 1.45 | 1.69 | 1.82 | 2.49 |
| 11:00-12:00 | 1,219 | 1.501 | 1.77 | 1.32 | 1.79 | 1.16 | 1.15 | 1.95 | 1.63 |
| 12:00-13:00 | 870 | 1.682 | 1.58 | 2.93 | 1.39 | 1.85 | 1.05 | 1.53 | 2.59 |
| 13:00-14:00 | 684 | **2.448** | 2.12 | 2.74 | 1.58 | 1.48 | 2.78 | 2.76 | 4.04 |
| 14:00-15:00 | 571 | 2.091 | 4.32 | 1.99 | 1.71 | 0.92 | 6.17 | 1.49 | 1.30 |

**Reads:** (1) PF RISES into the afternoon — 13:00-14:00 is the best bucket in BOTH
views (mc=1 2.448, every year ≥ 1.48), and 14:00-15:00 stays above every morning bucket
(2.091; 2023 its one sub-1.0 year). The long flusher decayed into the close; the short
pop-fader IMPROVES. (2) Mechanism consistent with the stack: the K maturity floors need
hours to build, so afternoon signals are maximal-exhaustion moves — a late pop on a name
that has climbed all day is the most tired thing the system sees. (3) ⏭ The inherited
15:00 cutoff may be cutting GOOD trades: testing 15:00-16:00 needs a kstudy rerun with
the entry window extended (cheap, ~13 min) — user's call; completion-room vs MOC
interaction is the thing to watch (S31b/c logic may still bind at the very end).

# S29 (2026-08-28) — the FlushFader base-spec campaign: test the old gates one by one on the S26 stack

⏭ Deferred first (user): full-corpus rerun to test 15:00-16:00 entries; volat floor 20bp
experiment (can high speed/dist compensate for low vol?).

**The campaign list** (all recorded columns; already resolved: volat floor S12, speed
pair S13, eff family S14, K family S16-S19, halt gates S23):
1. rngfront (rng_300/rng_20m < 0.8) — TESTED BELOW
2. z20 (20m vw-sigma z > 1.5) — TESTED BELOW
3. ssf pair (slope_since_flow ∈ [25,375) bp/min) · 4. dlv (dist above leg vwap > 3%) ·
5. rflow (r_since_flow ≤ 0.95) · 6. accel1020 ≤ 80 · 7. slope20 > 10 · 8. slope5 ≤ 400 ·
9. eff_9ema_10m ≥ −0.10 · 10. vol10rate ≥ 0.75 · 11. dv_0945_tape ≥ $3M

## Campaign 1: rngfront — DEAD, and the cliff cell INVERTED

(rngfront = rng_300 / ln(signal_vwap/chan_lo), the S10-validated post-push form.)
mc=0 bands: 1.97-2.38 flat below 0.8; **0.9-1.0 → 4.179 (n 375)** — the "pure cliff"
(whole 20m range in the last 5m) that the long side REJECTED is the short side's best
band. True mc=1: bands flat 1.73-2.00; the old <0.8 gate removes 84 trips for +0.011 PF
(noise); the cliff ≥0.8 reads 1.916 @ 283. **Verdict: no gate; the cliff-rejection logic
was long-side-specific (verticality is the short fade's FRIEND — consistent with S14).**

## Campaign 2: z20 — DEAD; the stack already implies it

The stack's speed+dist gates mechanically force z20 > 1 (the <1 band is EMPTY, n=0).
True mc=1 bands: 1-1.5 → 2.320 (n 773, the LOW rim — mild inversion of the old gate's
direction), 1.5-2.5 → 1.72-1.75, 2.5-3 → 2.10, ≥3 → 1.81. The old z > 1.5 gate removes
93 trips and LOWERS PF (1.642 → 1.624); z > 2 is flat (1.656 @ 4,529, no year
improved meaningfully). **Verdict: no gate — fully absorbed by speed/dist; the weak-pop
trim has nothing left to trim.**

## Campaign 3-5: the leg-native trio (ssf / dlv / rflow) — mostly absorbed; ONE inversion, ONE survivor

Frame: S26 stack, all three columns null-free. Old-spec gates at true mc=1:

| gate | n | pf | Δ vs baseline 1.642 |
|---|---|---|---|
| ssf ∈ [25,375) bp/min | 5,492 | 1.657 | +0.015 (−803 trips) |
| dlv > 3% | 6,275 | 1.644 | +0.002 (−20 trips) |
| rflow ≤ 0.95 | 5,515 | **1.680** | +0.038 (−780 trips) |
| all three (old spec) | 4,739 | 1.698 | +0.056 |

**ssf (leg slope)** — the old band is inert, but its CEILING is INVERTED: the "vertical
melt-up" the long spec rejected (≥ 375 bp/min) is the best cell — mc=0 375-600 → 5.825
(n 988), ≥600 → 6.615 (n 227); true mc=1 3.190 (n 66) / 2.534 (n 22). Tiny n — the
FOURTH verticality inversion (eff_10m, rngfront cliff, S18b diagonal, now ssf). ⏭ voice
shortlist, too small alone.

**dlv (leg stretch)** — absorbed: the stack's speed+dist already exclude the shallow leg
(0-3% band = 65 mc=1 trips at 0.69); everything above is flat 1.64-1.90. No gate.

**rflow (perfect-line rejector)** — THE ONE SURVIVOR of the trio: the ≥0.95 band is the
worst rflow cell (1.533 mc=1, n 1,311) and the ≤0.95 gate improves every year or holds
it (2021 1.72→1.82, 2022 1.23→1.30, 2025 1.65→1.72, 2026 2.10→2.15). A pop that is one
clean regression line since the leg's first high is a DRIFT, not an exhaustion spike —
the one piece of long-side logic that transfers unchanged. Modest (+0.04 PF for −12%
trips); adoption = user's call.

Full band tables in `sf_s30.out`.

## S30b — STACK += dlv > 3% (user: safe minor trim)

**STACK (locked): speed_1m > 2% ∧ dist_lo > 2% ∧ eff_10m ≥ 0.3 ∧ k300 ≥ 40 ∧ k600 ≥ 60 ∧
k180 ≥ 15 ∧ gap_adj_60 < 10 ∧ dlv > 3%** — mc=0 111,219 · mc=1 6,275 @ 1.644.
⏭ Parked (user): the rngfront TOP DECILE deserves its own study (FlushFader trimmed the
top 2 deciles with a big mc=0/mc=1 discrepancy; here the front band hides good trades).
Also noted (user recollection): FlushFader's rflow ≤ 0.95 cell was outright negative —
here it is merely the worst band (1.53), another magnitude-shift across the flip.

## Campaign 6-8: the slope trio — ALL DEAD, every ceiling INVERTED (verticality #5, #6, #7)

Frame: S30b stack. Old gates at true mc=1: accel ≤ 80 → 1.662 (+0.018); slope20 > 10 →
1.645 (removes 2 trips — the 0..10 band is n=8); slope5 ≤ 400 → **1.629 (LOWERS PF)**;
all three → 1.640. Nothing to adopt.

The structure is all at the steep end (mc0 / mc1 pf):

| band | mc=0 n | mc=0 pf | mc=1 n | mc=1 pf |
|---|---|---|---|---|
| accel ≥ 300 bp/min | 474 | 16.73 | 46 | 3.41 |
| slope20 ≥ 250 | 2,789 | 5.33 | 150 | 3.31 |
| slope5 400-700 | 6,142 | 2.93 | 493 | 2.25 |
| slope5 700-1200 | 1,211 | 8.13 | 115 | 3.69 |
| slope5 ≥ 1200 | 142 | 20.65 | 13 | 9.56 |

The old spec REJECTED every one of these cells ("no vertical melt-up", "no late
acceleration"). Short-side they are the best cells on their axes — monotone rising in
all three tables. **The verticality inversion is now SEVEN-fold aligned** (eff_10m,
eff diagonal S18b, rngfront cliff, ssf ceiling, accel, slope20, slope5): one coherent
A-tier axis — MAXIMUM STEEPNESS of the pop, at every horizon, on every measure. ⏭ At
assembly these should consolidate into ONE verticality voice (they overlap heavily),
not seven tiny cells.

## S31b — slope_10m shown; STACK += slope_5m ≥ 0 ∧ slope_20m ≥ 30 (user: trim the rare marginal trades)

slope_10m (ols_slope_600 × 6e5) on the S30b stack — the family shape again, monotone up:

| band bp/min | mc=0 n | mc=0 pf | mc=1 n | mc=1 pf |
|---|---|---|---|---|
| <0 | 3 | 0.00 | 1 | 0.00 |
| 0..30 | 1,651 | 1.386 | 322 | 1.532 |
| 30..60 | 15,175 | 1.619 | 1,990 | 1.544 |
| 60..120 | 47,383 | 2.076 | 3,731 | 1.790 |
| 120..250 | 37,106 | 2.255 | 2,280 | 1.832 |
| 250..500 | 8,932 | 2.787 | 544 | 1.949 |
| ≥500 | 969 | 16.72 | 70 | 3.982 |

Floors at true mc=1: s5 ≥ 0 alone −4 trips; s20 ≥ 30 → 1.663 @ 5,931; **BOTH → 1.665 @
5,927 (+0.021 for −348 trips; every year flat-to-up except 2024 −0.03)**. Adding
s10 ≥ 30 on top: +0.004 (−83 trips) — skipped.

**STACK (locked): speed_1m > 2% ∧ dist_lo > 2% ∧ eff_10m ≥ 0.3 ∧ k300 ≥ 40 ∧ k600 ≥ 60 ∧
k180 ≥ 15 ∧ gap_adj_60 < 10 ∧ dlv > 3% ∧ slope_5m ≥ 0 ∧ slope_20m ≥ 30** —
mc=1 baseline: **1.665 @ 5,927**.

## Campaign 9-11: eff_9ema / vol10rate / dv_0945_tape — all three inert. THE CAMPAIGN IS COMPLETE

Frame: S31b stack (106,390 mc=0 / 5,927 mc=1 @ 1.665).

**eff_9ema_10m** — old ≥ −0.10 knife removes 15 mc=1 trips (inert). Bands monotone up to
1.844 at ≥0.5 (the efficiency family again); the −0.10..0 sliver reads 1.910 @ 164 —
the same adjacent-band curio FlushFader saw (PF 8.06 there). No gate.

**vol10rate** — old ≥ 0.75 floor: +0.006 for −150 trips (inert). The <0.4 dying-tape
band is weak (1.399 @ 391) but tiny; 0.4-4 is flat 1.73-1.82 at mc=1 (the mc=0 hump at
0.4-0.75 → 2.43 flattens under replay). No gate.

**dv_0945_tape** — the $3M floor LOWERS PF (1.655, −716 trips, 2023 1.46 → 1.34); $1M is
a no-op (universe floor $2M binds first). Bands flat 1.64-1.74 to $100M, then **≥$100M →
1.480 @ 404** — the most-liquid tail is the one weak cell (heavily-traded in-play names
mean-revert less). ⏭ a dv CEILING is the only idea here; not adopted.

| band tables | mc0/mc1 in `sf_s32.out` |
|---|---|

## 🏁 CAMPAIGN SUMMARY — the old 15-gate FlushFader spec, fully adjudicated short-side

| old gate | verdict |
|---|---|
| volat ≥ 40bp | recording floor; edge starts ~60bp (S12); 20bp test deferred |
| speed > 2% + d1m > 2% | ✅ ADOPTED (S13, re-derived) |
| \|eff_10m\| ≥ 0.15 | ✅ superseded → SIGNED eff_10m ≥ 0.3 (S14) |
| \|eff_20m\| ∈ [0.3,0.5) | set aside (opposite-sign horizons, S14) |
| K band [26,50] | ❌ dead → K FLOORS k300/k600/k180 (S16-S19) |
| highs300 ≥ 6 | superseded by k300 ≥ 40 |
| cascade/reopen | ❌ inverts (fresh resumes fade WELL, S23) |
| rngfront < 0.8 | ❌ dead; cliff cell INVERTED (S29) |
| z20 > 1.5 | ❌ absorbed by speed/dist (S29) |
| ssf band | ❌ inert; ceiling INVERTED (S30) |
| dlv > 3% | ✅ ADOPTED (S30b, user) |
| rflow ≤ 0.95 | survivor, borderline (+0.04); unadopted |
| accel/slope20/slope5 ceilings | ❌ ALL INVERTED (S31) → floors s5 ≥ 0, s20 ≥ 30 ADOPTED (S31b) |
| eff_9ema ≥ −0.10 | ❌ inert (S32) |
| vol10rate ≥ 0.75 | ❌ inert (S32) |
| dv_0945_tape ≥ $3M | ❌ mildly negative; ceiling idea parked (S32) |

**FINAL STACK (10 gates): speed_1m > 2% ∧ dist_lo > 2% ∧ eff_10m ≥ 0.3 ∧ k300 ≥ 40 ∧
k600 ≥ 60 ∧ k180 ≥ 15 ∧ gap_adj_60 < 10 ∧ dlv > 3% ∧ slope_5m ≥ 0 ∧ slope_20m ≥ 30
— true mc=1: 5,927 trips @ PF 1.665.** Sizing tiers/voices parked for assembly:
rr < 0.5 (A+, 3.45), quiet-rr < 0.75 (2.17), loud-rr ≥ 5 (1.98), the consolidated
VERTICALITY axis (7 aligned inversions), k120×k180 diagonal (3.14), fresh-resume pocket
(2.42), rflow ≤ 0.95 (borderline gate).

# S33 (2026-08-28) — base_v3: the two deferred questions answered (15-16 ET; volat 20bp + compensation)

**Corpus**: `spikefader_base_v3` — BASE RUN (gates off), volat floor 20bp, entries
09:45-16:00 (13:00 early closes), full universe, 2020-01-02..2026-08-27.
**8,876,434 trips / 1,164,334 tkd / 3.7h / 89 parts / exit 0.** Integrity: restricted to
base_v2 semantics it reproduces the v2 corpus at 100.13% (2,690 extras = the widened
early-close window; 63 v2-only = 0.003% gate-boundary noise). Analyses below: the S31b
10-gate stack applied post-hoc (129,238 mc=0 rows at volat ≥ 20bp); true mc=1 throughout.

## A. The 15:00-16:00 window: the hour SPLITS

(volat ≥ 40bp for S28 comparability)

| bucket | mc=0 n | mc=0 pf | mc=1 n | mc=1 pf | mc=1 years |
|---|---|---|---|---|---|
| 13:00-14:00 | 10,295 | 3.635 | 648 | 2.439 | 2.15 2.78 1.52 1.44 2.68 2.78 4.03 |
| 14:00-15:00 | 8,601 | 2.492 | 545 | 2.087 | 4.40 2.04 1.73 0.94 5.90 1.44 1.25 |
| **15:00-15:30** | 4,587 | 2.612 | 327 | **2.513** | 1.39 1.62 3.00 2.18 9.69 5.30 2.20 |
| **15:30-16:00** | 5,212 | 1.366 | 341 | **1.107** | 1.23 1.39 1.40 0.98 1.87 0.46 0.99 |

**The FlushFader hour-before-close rule was HALF right**: 15:30-16:00 is dead (three
years ≤ 1.0 — completion room: a 15:30+ entry has < 30 min for the 5m-low cover to
develop before MOC eats it). But **15:00-15:30 is the best bucket on the whole clock**
(2.513). ⏭ Candidate: move the entry cutoff 15:00 → 15:30 (+327 mc=1 trips at 2.5).

## B1. volat fine bands on the stack (whole day)

| bp band | mc=0 n | mc=0 pf | mc=1 n | mc=1 pf |
|---|---|---|---|---|
| 20-25 | 777 | 0.957 | 121 | **0.757** |
| 25-30 | 2,407 | 1.177 | 353 | 1.267 |
| 30-35 | 3,933 | 1.499 | 535 | 1.348 |
| 35-40 | 5,667 | 1.682 | 678 | 1.504 |
| 40-50 | 14,138 | 1.688 | 1,346 | 1.553 |
| 50-60 | 15,961 | 2.093 | 1,422 | 1.676 |
| 60-80 | 28,282 | 2.313 | 2,002 | 1.831 |
| 80-100 | 19,956 | 2.309 | 1,274 | **1.905** |
| ≥100 | 38,117 | 2.381 | 1,836 | 1.697 |

The S12 extrapolation confirmed with real sub-40 data: 20-25 is outright NEGATIVE, the
edge builds continuously, 35-40 (1.504) ≈ 40-50 (1.553) — the 40bp floor is roughly
right, maybe 5bp generous.

## B2. Compensation: within LOW volat, can speed/dist rescue it? YES — at tiny n

mc=1 pf (n), volat rows × speed cols [2-3 / 3-4 / 4-6 / ≥6%]:

| volat | 2-3% | 3-4% | 4-6% | ≥6% |
|---|---|---|---|---|
| 20-30bp | 1.14 (362) | 0.93 (150) | 2.35 (61) | 4.17 (11) |
| 30-40bp | 1.36 (866) | 1.49 (463) | 1.85 (186) | 2.22 (43) |
| 40-60bp | 1.66 (1,958) | 1.62 (1,395) | 1.54 (769) | 2.08 (220) |

× dist cols: same shape, more extreme (20-30bp × dist ≥6% → 10.17 on n=8).

**Read: the compensation is REAL and monotone — a ≥4% pop on a 20-40bp name fades at
1.85-4.2 — but the population is ~300 mc=1 trips total.** ⏭ Candidate OR-extension of
the volat floor: volat ≥ 40bp OR (volat ≥ 25bp ∧ speed ≥ 4%) — a small honest add;
user's call at assembly.

# S34 (2026-08-28) — STACK += entry cutoff 15:30 (user); the VR/autocorr family tested

**STACK (locked): the S31b 10 gates ∧ signal < 15:30** — frame moves to base_v3
(volat ≥ 40bp post-hoc). ⏭ FlushFader port of the 15:30 cutoff parked (user).

**New baseline: mc=1 6,229 @ PF 1.699** (was 5,927 @ 1.665) — years
1.71 / 1.76 / 1.26 / 1.50 / 1.73 / 1.74 / 2.12, every year ≥ the old stack.

## The VR family (F1-F8 lock lineage: EWMA variance ratios + autocorrs on 30s slot returns)

**vr2_ewma** — no structure: 93% of the book sits in 1.0-1.5 at mc=1 1.77; the 1.5-2.0
tail reads 2.161 @ 164.

**vr4_ewma** — flat 1.63-1.92 through the mass; the ≥2.0 tail (hyper-trending at the
4-slot horizon) reads **2.940 @ 165** — verticality-adjacent, joins that cluster.

**ac1_ewma** — the one directional read: NEGATIVE lag-1 autocorr is bad
(<−0.2 → 0.898 @ 16; −0.2..−0.1 → 0.977 @ 60; −0.1..0 → 1.560 @ 304) — a tape already
whipsawing has done its own reverting. The ≥0 mass is flat 1.76-1.93.

| band tables | `sf_s34.out` |
|---|---|

**Verdict: no gates. The family is absorbed like the rest of the campaign; two cells for
the ledger — vr4 ≥ 2 (2.94 @ 165, verticality cluster) and ac1 < −0.1 (a 76-trip @ ~0.94
sliver, too small to bother trimming).**

## S34b — the volat OR-EXTENSION adopted too (user: that was the intended suggestion)

**STACK (locked, 12 conditions): (volat_20m ≥ 40bp ∨ (volat_20m ≥ 25bp ∧ speed ≥ 4%)) ∧
speed_1m > 2% ∧ dist_lo > 2% ∧ eff_10m ≥ 0.3 ∧ k300 ≥ 40 ∧ k600 ≥ 60 ∧ k180 ≥ 15 ∧
gap_adj_60 < 10 ∧ dlv > 3% ∧ slope_5m ≥ 0 ∧ slope_20m ≥ 30 ∧ signal < 15:30.**
⏭ Both the OR-extension and the 15:30 cutoff are FlushFader port candidates (user).

| book | mc=1 n | pf | years |
|---|---|---|---|
| volat ≥ 40 only (S34) | 6,229 | 1.699 | 1.71 1.76 1.26 1.50 1.73 1.74 2.12 |
| **+ OR-extension** | 6,347 | 1.697 | 1.73 1.74 1.28 1.50 1.72 1.73 2.12 |
| the added slice alone | 200 | 1.802 | 2.07 1.65 3.35 1.15 1.39 1.50 9.32 |

The extension adds 118 net mc=1 trips (the 200-trip slice frees/steals some slots) at
slice PF 1.802 — baseline-accretive in 2020/2022, neutral elsewhere, 2026's 9.32 on a
tiny denominator. A small honest widening, adopted.

## S34c — STACK += ac1_ewma ≥ −0.1 (user); the ≥0.2 region broken down

**STACK (locked, 13 conditions): S34b ∧ ac1_ewma ≥ −0.1** — mc=1 **6,322 @ 1.709**
(−25 trips, every year flat-to-up: 2021 1.76, 2023 1.52, 2026 2.15).

**ac1 ≥ 0.2 fine bands** (on the gated stack):

| band | mc=0 n | mc=0 pf | mc=1 n | mc=1 pf | mc=1 years |
|---|---|---|---|---|---|
| 0.2-0.3 | 36,690 | 2.103 | 2,961 | 1.843 | 2.28 1.76 1.54 1.49 1.88 1.70 2.18 |
| 0.3-0.4 | 22,544 | 2.787 | 1,903 | 1.964 | 2.37 1.79 1.77 1.64 1.75 2.37 2.07 |
| 0.4-0.5 | 8,035 | 2.161 | 720 | 2.011 | 3.11 2.14 1.82 **0.72** 2.80 2.21 1.60 |
| 0.5-0.6 | 2,036 | 4.084 | 205 | 2.106 | 2.71 4.71 3.76 **0.53** 2.23 2.16 1.02 |
| 0.6-0.7 | 485 | 5.450 | 40 | 2.794 | (sparse-year cells) |
| ≥0.7 | 98 | 497* | 6 | 96.65* | (*near-zero losers — artifact) |

Monotone rising 1.84 → 2.79: persistent positive slot-autocorr (the trend that never
paused) is ANOTHER verticality-cluster member — but ⚠ 2023 INVERTS above 0.4 (0.72/0.53),
the first year-instability inside the cluster. The 0.2-0.4 mass (77% of the book) is
solid everywhere. Ledger: ac1 ≥ 0.4 is a tier candidate with a 2023 asterisk; ≥0.6 cells
are too thin to read.

**S34d — ac1 IS the better whipsaw knife (user hypothesis, verified)**: on the stack
frame, corr(ac1_ewma, eff_9ema_10m) = 0.266 and their reject sets are DISJOINT (621 vs
362 mc=0 rejects, overlap 0). What each cuts: ac1 < −0.1 → PF 0.709 (real whipsaw junk);
eff_9ema < −0.10 → PF 1.052 (roughly baseline noise — its knife never found anything
short-side, S32). The autocorrelation measures the fighting tape DIRECTLY; the 9-EMA
agreement was a proxy that doesn't transfer. ⏭ FlushFader port candidate #3: replace
eff_9ema's knife with ac1_ewma ≥ −0.1 (alongside the 15:30 cutoff and the volat
OR-extension).

## S35 (2026-08-29) — ONE window-difference speed vs the speed PAIR: the pair is replaceable

**The question (user)**: replace the two speed-pair features (`speed_1m = signal_vwap/
vwap_60_prev − 1` over the plain 120b/60b window difference, plus `dist_lo =
signal_vwap/lo_60 − 1`) with a SINGLE decayed-sum window-difference speed. Grid:
half-life pairs (120,60)/(120,30)/(60,30) × {bar-clock, time-clock} × {equal-weighted,
volume-weighted}, plus a time-clock zero-fill EQ family — 15 cells.

**The machinery (engine, S35/S35b)**: `DecaySumMa` grew a `GapValue` mode DU declaring
what value the stream held during the gapCount missing seconds — `Zero` (gaps are real
zero observations: volume/tc; the frozen original semantics), `Empty` (pushes only,
gaps decay weightlessly: bar-clock prices), `Locf` (gaps hold the last value: the
decayed TWAP of the LOCF-filled path; time-clock prices). Numerator and `Weight` update
consistently per mode — the broken object is MIXING them (a zero-filled numerator over
an every-second weight reads a "mean price" below every print; a 10, 5s-gap, 10 stream
"averages" 3.4). Oracle: `TradingEdge.RollingMa/DecaySumWeight_Test.fsx` (per-second
brute force, all modes ≤ 1.4e-15; on gap-0 streams all three modes coincide EXACTLY;
sparse Zero/Empty weights diverge 16×; `Locf.Weight ≡ Zero.Weight`). Recorded columns
`vwap_ewp_{12060,12030,6030}_{tv,te,tz,bv,be}` (t/b = time/bar clock, v = dv/vol,
e = px/Weight LOCF-or-Empty, z = px/Weight Zero); `vwap_ew_60_prev` (S8) IS the
12060_tv cell. Feature = `signal_vwap/col − 1`.

**Corpus**: `data/spikefader_base_v4` — the v3 base config (volat ≥ 20bp, entries →
16:00) rerun on the 212,822 tkds that produced ≥ 1 v3 trip (`spikefader_v3tkd_cand`;
18% of the universe, 2h17m vs 3h41m). 8,867,876 trips. ⚠ 8,558 trips (0.10%) fewer
than v3: v3 was launched with `--entry-end-sec-short 46800` (13:00 on NYSE early-close
days) and v4 defaulted to 12:00 — verified the ONLY delta (254 tkds, all early-close
dates; restricted to ≤ 12:00 the two corpora agree exactly). Carry the flag forward.

**Frame**: background-11 = the S34c stack minus the speed pair and the volat condition
(eff/k×3/gap_adj/dlv/slopes×2/15:30/ac1 on; volat ≥ 20bp floor) → 225,747 mc=0 rows.
Reference S34c on v4: **6,313 @ 1.709**, years 1.73/1.76/1.29/1.52/1.72/1.74/2.15
(v3 gave 6,322 @ 1.709 — the early-close delta, PF identical).

**A1 — iso-trip, clean `volat ≥ 40bp` frame** (pair → single `wd_speed > t`, t bisected
to the pair's mc=1 n; olap = share of the pair book's (tkd, signal) keys retained):

| variant | thr | n | pf | olap | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| PAIR 2%+2% | - | 6,196 | 1.710 | - | 1.71 | 1.77 | 1.27 | 1.53 | 1.73 | 1.75 | 2.15 |
| tv12060 | 3.31% | 6,196 | 1.716 | 71.4% | 1.70 | 1.80 | 1.29 | 1.52 | 1.71 | 1.76 | 2.18 |
| tv12030 | 2.93% | 6,196 | 1.720 | 72.4% | 1.70 | 1.80 | 1.29 | 1.53 | 1.71 | 1.78 | 2.19 |
| tv6030 | 2.29% | 6,196 | 1.718 | 74.5% | 1.70 | 1.77 | 1.30 | 1.52 | 1.72 | 1.79 | 2.18 |
| te12060 | 3.78% | 6,196 | 1.716 | 70.9% | 1.70 | 1.79 | 1.33 | 1.53 | 1.70 | 1.76 | 2.16 |
| te12030 | 3.38% | 6,197 | 1.722 | 71.9% | 1.70 | 1.80 | 1.34 | 1.52 | 1.70 | 1.78 | 2.16 |
| te6030 | 2.58% | 6,197 | 1.726 | 74.3% | 1.72 | 1.78 | 1.32 | 1.52 | 1.71 | 1.79 | 2.19 |
| tz12060 | 7.49% | 6,197 | 1.698 | 62.0% | 1.74 | 1.77 | 1.43 | 1.46 | 1.68 | 1.70 | 2.03 |
| tz12030 | 6.58% | 6,197 | 1.687 | 61.9% | 1.72 | 1.78 | 1.41 | 1.45 | 1.67 | 1.70 | 2.01 |
| tz6030 | 4.81% | 6,196 | 1.673 | 60.9% | 1.70 | 1.74 | 1.39 | 1.43 | 1.67 | 1.68 | 2.03 |
| bv12060 | 3.58% | 6,197 | 1.720 | 71.4% | 1.70 | 1.81 | 1.30 | 1.51 | 1.72 | 1.77 | 2.17 |
| bv12030 | 3.17% | 6,196 | 1.720 | 72.4% | 1.71 | 1.79 | 1.32 | 1.52 | 1.72 | 1.77 | 2.17 |
| bv6030 | 2.42% | 6,196 | 1.725 | 74.5% | 1.70 | 1.80 | 1.30 | 1.52 | 1.73 | 1.79 | 2.18 |
| be12060 | 4.01% | 6,197 | 1.727 | 70.9% | 1.70 | 1.82 | 1.33 | 1.52 | 1.71 | 1.79 | 2.17 |
| be12030 | 3.57% | 6,197 | 1.727 | 71.8% | 1.71 | 1.81 | 1.32 | 1.52 | 1.72 | 1.78 | 2.17 |
| be6030 | 2.67% | 6,196 | 1.723 | 74.4% | 1.72 | 1.76 | 1.31 | 1.53 | 1.73 | 1.78 | 2.19 |

**A2 — mc=0 PF by band** (same frame; n in thousands):

| variant | 0-1% | 1-2% | 2-3% | 3-5% | 5-8% | ≥8% |
|---|---|---|---|---|---|---|
| tv12060 | 1.45 (0k) | 1.52 (4k) | 1.51 (23k) | 2.03 (54k) | 2.22 (41k) | 2.57 (38k) |
| tv12030 | 2.34 (0k) | 1.37 (9k) | 1.68 (30k) | 2.15 (54k) | 2.23 (36k) | 2.61 (30k) |
| tv6030 | 1.28 (1k) | 1.60 (28k) | 2.07 (38k) | 2.14 (46k) | 2.14 (27k) | 2.97 (18k) |
| te12060 | 0.00 (0k) | 1.31 (2k) | 1.50 (15k) | 1.89 (46k) | 2.13 (44k) | 2.54 (52k) |
| te12030 | 2.57 (0k) | 1.45 (5k) | 1.51 (21k) | 2.08 (50k) | 2.10 (41k) | 2.58 (43k) |
| te6030 | 1.12 (1k) | 1.58 (21k) | 1.99 (34k) | 2.05 (47k) | 2.16 (31k) | 2.81 (25k) |
| tz12060 | inf (0k) | 1.16 (0k) | 2.27 (3k) | 2.02 (17k) | 2.55 (29k) | 2.22 (111k) |
| tz12030 | inf (0k) | 1.77 (1k) | 1.93 (6k) | 2.39 (21k) | 2.34 (32k) | 2.22 (101k) |
| tz6030 | 0.93 (0k) | 1.86 (6k) | 2.38 (14k) | 2.34 (32k) | 2.04 (35k) | 2.32 (73k) |
| bv12060 | inf (0k) | 1.40 (3k) | 1.59 (19k) | 2.00 (54k) | 2.14 (43k) | 2.57 (41k) |
| bv12030 | 1.63 (0k) | 1.43 (7k) | 1.64 (28k) | 2.10 (56k) | 2.19 (38k) | 2.63 (32k) |
| bv6030 | 1.36 (1k) | 1.61 (25k) | 2.06 (38k) | 2.10 (48k) | 2.12 (28k) | 2.98 (19k) |
| be12060 | inf (0k) | 1.31 (1k) | 1.60 (13k) | 1.86 (46k) | 2.08 (46k) | 2.54 (53k) |
| be12030 | 4.35 (0k) | 1.47 (4k) | 1.55 (20k) | 2.06 (50k) | 2.05 (42k) | 2.59 (44k) |
| be6030 | 1.18 (0k) | 1.61 (19k) | 1.98 (33k) | 2.03 (49k) | 2.13 (32k) | 2.83 (26k) |

**B — the FULL S34c frame, pair AND the volat OR arm swapped** (gate `s > t`, arm
`s ≥ 2t` mirroring the reference's 4% = 2 × 2%; t bisected to the reference n):

| variant | thr | n | pf | olap | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| REF pair | - | 6,313 | 1.709 | - | 1.73 | 1.76 | 1.29 | 1.52 | 1.72 | 1.74 | 2.15 |
| tv12060 | 3.24% | 6,313 | 1.718 | 70.1% | 1.71 | 1.78 | 1.33 | 1.53 | 1.69 | 1.76 | 2.19 |
| tv12030 | 2.88% | 6,314 | 1.718 | 70.8% | 1.70 | 1.77 | 1.32 | 1.53 | 1.70 | 1.78 | 2.18 |
| tv6030 | 2.24% | 6,313 | 1.713 | 72.7% | 1.71 | 1.75 | 1.32 | 1.53 | 1.71 | 1.78 | 2.17 |
| te12060 | 3.72% | 6,313 | 1.713 | 69.5% | 1.72 | 1.77 | 1.35 | 1.54 | 1.68 | 1.75 | 2.16 |
| te12030 | 3.31% | 6,313 | 1.713 | 70.7% | 1.71 | 1.76 | 1.36 | 1.53 | 1.68 | 1.76 | 2.17 |
| te6030 | 2.54% | 6,314 | 1.718 | 73.3% | 1.74 | 1.74 | 1.33 | 1.51 | 1.69 | 1.79 | 2.20 |
| tz12060 | 10.05% | 6,314 | 1.590 | 50.4% | 1.75 | 1.57 | 1.39 | 1.34 | 1.61 | 1.57 | 1.82 |
| tz12030 | 8.91% | 6,313 | 1.592 | 49.8% | 1.76 | 1.57 | 1.38 | 1.34 | 1.61 | 1.57 | 1.84 |
| tz6030 | 6.66% | 6,313 | 1.609 | 47.3% | 1.74 | 1.63 | 1.43 | 1.39 | 1.66 | 1.55 | 1.78 |
| bv12060 | 3.50% | 6,314 | 1.711 | 70.0% | 1.71 | 1.77 | 1.32 | 1.50 | 1.70 | 1.76 | 2.16 |
| bv12030 | 3.10% | 6,313 | 1.716 | 71.1% | 1.71 | 1.78 | 1.32 | 1.51 | 1.70 | 1.76 | 2.17 |
| bv6030 | 2.38% | 6,313 | 1.723 | 73.4% | 1.73 | 1.77 | 1.32 | 1.52 | 1.72 | 1.78 | 2.18 |
| be12060 | 3.92% | 6,314 | 1.711 | 69.6% | 1.70 | 1.78 | 1.33 | 1.53 | 1.69 | 1.76 | 2.15 |
| be12030 | 3.50% | 6,313 | 1.716 | 70.5% | 1.71 | 1.78 | 1.33 | 1.53 | 1.69 | 1.78 | 2.16 |
| be6030 | 2.63% | 6,313 | 1.721 | 73.0% | 1.74 | 1.75 | 1.32 | 1.53 | 1.70 | 1.78 | 2.19 |

**Verdicts**:
1. **The pair is replaceable.** Every non-tz single variant matches or beats the pair
   at identical n in BOTH frames (A1: 1.716-1.727 vs 1.710; B: 1.711-1.723 vs 1.709),
   with the 2022 worst-year IMPROVING (1.29 → 1.32-1.36). `dist_lo` adds nothing the
   smooth kernel doesn't already carry. One feature, one threshold, two conditions
   retired.
2. **Clock and weighting are nearly irrelevant** (be ≈ te, bv ≈ tv within ~0.005;
   pairs within ~0.01 of each other) — at signal time the tape is dense (tc60 ≥ 60
   entry floor), so bar-clock ≈ time-clock and EQ ≈ VW. The kernel SHAPE did the work.
   Best cells: A1 be12060/be12030 1.727, B bv6030 1.723 / be6030 1.721 — (60,30)
   slightly ahead in the full frame, retaining the most of the pair book (73%).
3. **tz (zero-fill) is the one real loser** — A1 1.67-1.70, and its OR arm is TOXIC
   (B: 1.59, overlap 50%): at 2t = 13-20% the arm admits volat-25-40bp trips selected
   mostly by prior-window SPARSITY, not speed (A2: its ≥8% band holds 111k rows at
   PF 2.22 vs the others' 2.5-3.0 tails — the density blend piles thin-tape rows into
   the top band). Its 2022 = 1.38-1.43 (best of all variants) hints the sparsity blend
   carries some 2022-specific information — a sizing-tier curiosity at most, not a gate.
4. **Book overlap is only ~70-74%** at matched n: the single feature selects a
   genuinely different book at equal-or-better PF — a real simplification, not a
   re-labeling.

⏭ Adoption decision (user): which cell replaces the pair — bv6030 (VW, honest vwap
semantics, B-best 1.723) or be6030/be12060 (EQ, simplest machinery)? Threshold lands
at ~2.4% (6030) / ~3.9-4.0% (12060) with the OR arm at 2×.

**S35c — the UNION of the 12 non-tz variant books (user)**: replayed per the mc=1
ruling as ONE gate stack whose speed condition is the OR of all 12 calibrated gates
(each with its own 2× volat arm; thresholds from the B table, count-calibrated only):

| book | n | pf | avg | win | ref-olap | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| REF pair | 6,313 | 1.709 | 1.16% | 72.0% | 100% | 1.73 | 1.76 | 1.29 | 1.52 | 1.72 | 1.74 | 2.15 |
| UNION 12 | 6,704 | 1.707 | 1.13% | 72.4% | 73.7% | 1.71 | 1.75 | 1.37 | 1.52 | 1.68 | 1.75 | 2.14 |
| UNION 13 (+pair) | 6,891 | 1.710 | 1.12% | 72.4% | 82.6% | 1.71 | 1.77 | 1.37 | 1.53 | 1.68 | 1.73 | 2.14 |
| INTER 12 | 5,914 | 1.725 | 1.22% | 72.3% | 67.7% | 1.73 | 1.79 | 1.29 | 1.54 | 1.70 | 1.79 | 2.18 |

+6.2% trips at −0.002 PF (union-12), +9.2% at ±0.000 (union-13 with the pair), and
2022 — the worst year — improves 1.29 → 1.37 in both; 2024 gives back 0.04. Coverage:
every individual gate passes 79-82% of the union book — a consensus core plus
complementary ~20% fringes that carry their weight. The 12-way INTERSECTION is a mild
premium tier (1.725, avg 1.22%), consensus-as-sizing at best. ⚠ The union carries 12
count-calibrated thresholds (12 dials vs bv6030's one at 1.723) — the PF was never
tuned, but remember the dial count when comparing. ⏭ If pursued: prune to the 3-4
maximally complementary kernels rather than all 12.

**S35d — the REF/UNION symmetric difference decomposed (user)**: shared core 4,655 @
1.717 (avg 1.31%). Slot-displaced trips (shared tkd, different second): ref-side 1,503
@ 1.491, union-side 1,613 @ 1.242 — the non-consensus slots are worse entries. The
tkd-exclusive fringes are near-perfect ON BOTH SIDES (ref 155 @ 19.45, 91.6% win;
union 436 @ 19.39, 91.7% win) — NOT a single-signal-day effect (control: 1-trip tkds
in the ref book run 1.82, not 19): DISAGREEMENT STRUCTURALLY EXCLUDES RUNNERS — a pop
strong enough to run trips every construction, so a tkd only lands exclusive when the
pop stayed borderline all day, and borderline pops are capped-loss fades (typical
winner ~2.5%, loser ~1.4%, zero catastrophes). ⚠ "Exclusive day" is OUTCOME SELECTION
as a label (whether other gates fire later is future information — the bounce-door
class); the union banks those trips CAUSALLY, which is why its book PF holds.
Net (eqw Σret): REF 7,345% · UNION 12 7,555% (+2.9%) · UNION 13 7,690% (+4.7%, 2022
net 328 → 432).

**S35e — 3-voice compression (user: "pick one (120,60)/(120,30)/(60,30) triple")**:
within-family triples are a DEAD HEAT (tv-3 7,465% @ 1.714 · te-3 7,461 @ 1.712 ·
be-3 7,458 @ 1.712 · bv-3 7,447 @ 1.711; n ≈ 6,460) — family is irrelevant, per-cell
thresholds from the B table. The 64 mixed one-cell-per-horizon combos spread 7,417-
7,566%: every top-5 combo CROSSES families (diversity across horizons is what pays;
the bottom combos repeat similar constructions) and every top-5 includes tv12060 =
the S8 `vwap_ew_60_prev` already in production. Best: `tv12060 + te12030 + be6030`
(n 6,569 @ 1.719, 7,566% — all of union-12's net on 3 voices; top-5 within 11 net
points = tie cluster, exact winner is noise).

| book | n | pf | net | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|
| REF pair | 6,313 | 1.709 | 7,345% | 1,269 | 1,475 | 328 | 558 | 1,182 | 1,319 | 1,214 |
| TRI mixed | 6,569 | 1.719 | 7,566% | 1,285 | 1,498 | 408 | 570 | 1,160 | 1,405 | 1,241 |
| TRI + pair | 6,803 | 1.717 | 7,702% | 1,315 | 1,564 | 418 | 588 | 1,171 | 1,392 | 1,253 |
| UNION 13 | 6,891 | 1.710 | 7,690% | 1,305 | 1,575 | 432 | 587 | 1,160 | 1,385 | 1,245 |

**⏭ ADOPTION CANDIDATE — TRI + pair (4 voices, replaces the 13-voice union)**: the
pair (speed > 2% ∧ dist > 2%, arm ≥ 4%) ∨ tv12060 > 3.24% ∨ te12030 > 3.31% ∨
be6030 > 2.63% (each kernel with its own 2× volat arm). Beats UNION 13 on BOTH net
(7,702% vs 7,690%) and PF (1.717 vs 1.710) at 6,803 trips; +4.9% net over the pair
alone; 2022 net +27%.

## S35f (2026-08-29) — ⭐ ADOPTED: the speed PAIR → single `be6030 > 2%` (flat threshold)

**The flat-threshold probe (user: "the calibrated thresholds would be hard to deal
with")** — every construction at the pair's own 2% gate / 4% arm:

| book | n | pf | net | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|
| REF pair | 6,313 | 1.709 | 7,345% | 1.73 | 1.76 | 1.29 | 1.52 | 1.72 | 1.74 | 2.15 |
| tv-3 @2% | 7,871 | 1.683 | 7,923% | 1.71 | 1.75 | 1.34 | 1.47 | 1.68 | 1.68 | 2.09 |
| te-3 @2% | 8,183 | 1.675 | 7,995% | 1.70 | 1.75 | 1.35 | 1.46 | 1.68 | 1.67 | 2.07 |
| bv-3 @2% | 8,044 | 1.674 | 7,915% | 1.69 | 1.75 | 1.34 | 1.45 | 1.68 | 1.67 | 2.09 |
| be-3 @2% | 8,327 | 1.672 | 8,030% | 1.69 | 1.74 | 1.36 | 1.45 | 1.68 | 1.66 | 2.08 |
| TRI @2% | 7,980 | 1.680 | 7,947% | 1.70 | 1.75 | 1.34 | 1.46 | 1.68 | 1.68 | 2.09 |
| TRI+pair @2% | 7,980 | 1.680 | 7,944% | (pair fully absorbed) | | | | | | |
| **be6030 @2%** | 7,243 | **1.704** | **7,817%** | 1.72 | 1.77 | 1.35 | 1.48 | 1.70 | 1.74 | 2.13 |

**⭐ THE COLLAPSE FINDING**: at a COMMON threshold the union degenerates — tv-3 @2% ≡
tv12060 @2% alone (identical books): the slow kernel's speed always reads larger than
its faster siblings', so at equal thresholds the (120,60) cell is a SUPERSET of the
rest, and the pair vanishes inside TRI+pair (7,947 → 7,944). Per-horizon calibration
wasn't a nuisance — it was what made multiple voices real. Flat thresholds delete the
ensemble. The one cell where a round 2% is NOT a giveaway is the fast (60,30) kernel
(its calibrated value was 2.63%) — and be6030 @2% alone nets MORE than the calibrated
4-voice TRI+pair (7,817% vs 7,702%) at near-reference PF on ONE dial.

**ADOPTED (user)**: `speed_1m > 2% ∧ dist_lo > 2%` (two conditions) → **`be6030 > 2%`**
(one), and the volat OR arm's `speed ≥ 4%` → **`be6030 ≥ 4%`**. Two features never
measured two things. The stack is now 12 conditions:

`(volat_20m ≥ 40bp ∨ (volat ≥ 25bp ∧ be6030 ≥ 4%)) ∧ be6030 > 2% ∧ eff_10m ≥ 0.3 ∧
k300 ≥ 40 ∧ k600 ≥ 60 ∧ k180 ≥ 15 ∧ gap_adj_60 < 10 ∧ dlv > 3% ∧ slope_5m ≥ 0 ∧
slope_20m ≥ 30 ∧ signal < 15:30 ∧ ac1_ewma ≥ −0.1`
where `be6030 = signal_vwap/vwap_ewp_6030_be − 1`.

**The adopted book (v4, true mc=1)**: **7,243 @ PF 1.704, avg 1.08%, win 72.2%,
net 7,817%** — +14.7% trips and +6.4% net vs the pair stack at −0.005 PF, worst year
2022 UP 1.29 → 1.35 (net 328 → 427).

| year | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|
| pf | 1.72 | 1.77 | 1.35 | 1.48 | 1.70 | 1.74 | 2.13 |
| net | 1,341 | 1,616 | 427 | 555 | 1,201 | 1,421 | 1,255 |
| n | 1,320 | 1,642 | 770 | 626 | 933 | 1,273 | 679 |

**Engine**: `MinSpeed1m`/`MinDist1mLo` gates DELETED; `MinSpeedBe6030` (default 0.02)
gates `vwap/vwap_ewp_6030_be − 1`, mirroring the recorded column (banner:
`STACK (S35f)= speed_be6030 > +2% | eff10 >= 0.30 | dv0945tape >= off`). The volat OR
arm stays post-hoc. ⏭ The union/TRI machinery stays on the shelf (S35c-e): calibrated
TRI+pair (1.717 / 7,702%) remains the quality-tilted alternative if capacity ever
matters more than dial count.

# 📌 S36 — CURRENT STATE RECAP (2026-08-30, written at the FlushFader-weekend close)

**THE 12-CONDITION STACK (S35f, adopted 2026-08-29)** — post-hoc over `base_v4`
(the canonical corpus: volat ≥ 20bp, entries → 16:00, 15-cell wd-speed grid,
whitelist `spikefader_v3tkd_cand`); engine stack gates = MinSpeedBe6030/MinEff10m/
MinDv0945Tape:

`(volat_20m ≥ 40bp ∨ (volat ≥ 25bp ∧ be6030 ≥ 4%)) ∧ be6030 > 2% ∧ eff_10m ≥ 0.3 ∧
k300 ≥ 40 ∧ k600 ≥ 60 ∧ k180 ≥ 15 ∧ gap_adj_60 < 10 ∧ dlv > 3% ∧ slope_5m ≥ 0 ∧
slope_20m ≥ 30 bp/min ∧ signal < 15:30 ∧ ac1_ewma ≥ −0.1`,
where `be6030 = signal_vwap/vwap_ewp_6030_be − 1` (bar-clock EQ (60,30) decayed
window-difference; ONE feature replaced the S13 speed_1m+dist_lo pair).
**True mc=1: 7,243 @ PF 1.704 · avg 1.08% · win 72.2% · net 7,817% eqw** ·
years pf 1.72 / 1.77 / 1.35 / 1.48 / 1.70 / 1.74 / 2.13.

**THE VOICE/TIER FAMILY (queued for breakdowns — the next work)**:
- `rr_15m < 0.5` A+ SIZING tier (mc=1 3.45 @ 364; trimmed 8.7; losses all bottom-5%)
- quiet rr < 0.75 arm (2.17 @ 879) · loud rr ≥ 5 arm (1.98 @ 1,566) — MaxRider's
  mirrored U; consume as TIERS, not gates (S27b ruling)
- CONSOLIDATED VERTICALITY axis — the 8 aligned inverted-guard cells (rngfront cliff,
  ssf/accel/slope5/slope20 ceilings, eff_10m, vr4 ≥ 2) merge into ONE voice
- k120×k180 diagonal (3.14 @ 21k mc=0; k120 ≥ 64 would swallow k300/k600 — voice only)
- fresh-resume pocket (ht 1-3 ∧ ssh < 5m ∧ rr ≥ 3 → 2.42)
- rflow ≤ 0.95 (borderline +0.04) · ac1 ≥ 0.4 tier (2.0-2.8, ⚠ 2023 inverts)
- TODO (user): `gap_adj_1200` feature test
- ⚠ from the FlushFader weekend (docs/flushfader_results.md S43cf-cp): FIVE side
  inversions measured — NOTHING ports across the side flip without re-derivation;
  SpikeFader-specific rulings (this stack, the 15:30 cutoff, the volat OR-arm, the
  ac1 ≥ −0.1 KNIFE) all remain SHORT-SIDE-ONLY facts. Blockers before any tradable
  claim: stops, cover-vs-costs, spreads, borrow/SSR.

# S37 (2026-08-30) — the FlushFader-voice PORT CAMPAIGN opens: frame validated, v20 INVERTS (#7)

The user's directive: try FlushFader's ROSTER v3.3 voices one by one in SpikeFader —
each a HYPOTHESIS under the side-flip law, re-derived from scratch. (An rr detour ran
first on the FlushFader side: no seat there in five forms, but a real monotone
time-clock-only gradient — flushfader_results.md S43cq-b..d.)

## The port list (mirrors; ingredients in base_v4 unless noted)

1. v20 `volat_20m ≥ 140bp` (same side) · 2. d20a → arming POP ≥ +28%
`(signal_vwap/first_high_vwap)·(1+d_lo_flow) − 1` · 3. dslo → dist off session HIGH
≤ −8% (+ unflipped twin) · 4. vexp `(s10−s20)·2e4 > 12` · 5. vcrush `s5·2e4 ≤ −24`
(❌ needs volat_slope_5m — engine add + whitelist rerun; defer pending vexp's verdict)
· 6. legage → `secs_since_first_high ≤ 450` (⚠ K floors already gate maturity) ·
7. dsu → `upticks_since_downtick ≥ 8` · 8. haltband (⚠ S23 already inverted the
fresh-resume side — re-derive the band) · 9. acneg (already adjudicated: the stack
GATES ac1 ≥ −0.1; acneg's tail IS the junk the knife cuts — skip).

## Frame validation (match the book)

The 12-condition S35f stack rebuilt as post-hoc SQL on base_v4 + true per-tkd mc=1
replay reproduces the adopted book EXACTLY: **7,243 @ 1.704, avg +1.08%, win 72.2%,
net 7,817%, years 1.72/1.77/1.35/1.48/1.70/1.74/2.13** (scratch sf20_stack.py — the
reusable frame for every port study). ⚠ base_v4's `ret_exit` is ALREADY the short
return — do NOT negate (a sign flip reproduces the exact mirror book, 0.587 = 1/1.704,
which is itself a useful checksum).

## Port #1 — v20: FlushFader's strongest voice is BELOW-BOOK short-side (inversion #7)

volat_20m bands on the stack (S33-B1 redone on the CURRENT stack, top end resolved):

| bp band | mc0 n | mc0 pf | mc1 n | mc1 pf | avg% | mc1 years |
|---|---|---|---|---|---|---|
| [40,60) | 47,754 | 1.961 | 2,757 | 1.599 | +0.60 | 1.83 2.02 0.92 1.23 1.60 1.52 1.75 |
| [60,80) | 34,820 | 2.391 | 1,677 | 1.849 | +1.04 | 2.18 1.89 1.56 1.94 2.00 1.66 1.73 |
| [80,100) | 21,270 | 2.326 | 947 | 1.834 | +1.36 | 1.72 1.73 2.61 2.00 1.56 1.56 2.87 |
| [100,120) | 12,691 | 2.414 | 585 | 1.950 | +1.78 | 2.11 1.07 2.62 1.44 1.51 2.56 5.11 |
| [120,140) | 7,607 | 2.576 | 322 | 1.945 | +2.08 | 1.32 1.85 1.23 0.96 2.68 4.28 3.34 |
| [140,180) | 9,263 | 1.670 | 365 | **1.104** | +0.45 | 1.43 1.17 0.71 1.52 1.10 0.98 1.11 |
| [180,250) | 5,874 | 2.519 | 219 | 2.084 | +3.24 | 1.18 7.19 7.40 5.16 1.56 3.50 1.87 |
| ≥250 | 2,316 | 6.784 | 81 | 1.805 | +3.81 | 0.63 0.70 1.06 1.26 6.41 1.20 3.30 |

The port cell `≥140bp` = **1.451 @ 665 vs the 1.704 book** (2022 0.84); `≥100bp` =
1.654, also below. The short-side gradient rises to a 100-140bp plateau (~1.95) then
[140,180) craters to 1.104 INSIDE the passing book, with a partial noisy recovery
above 180. **FlushFader's #1 voice subtracts short-side — the cleanest side-flip yet,
on the systems' single most shared feature.**

**⏭ THE USER'S HYPOTHESIS (2026-08-30 close, tomorrow's program)**: the ≥140 cells
fail because the stack is TOO LOOSE for them — entering too early into pops that keep
squeezing. "In contrast to FlushFader which worked best in the [26,50] K band,
SpikeFader only gets better as we raise the floors... getting the ≥140 cells to work
will most likely be just that — the system has to be MORE SELECTIVE." Tomorrow: study
K-floor (k300/k600/k180) and eff tightening WITHIN the ≥140bp cells, then continue
the port list (#2 d20a next). Candidate exclusion band [140,180) @ 1.104 × 365 waits
on the same study (if tightening repairs it, no knife needed).

## S37b — SCOUT (autonomous, evening 2026-08-30): the looseness hypothesis probed. DIRECTIONALLY CONFIRMED

Run while the user was out, as PREP for tomorrow — scout tables only, nothing adopted.
Replay-inside-gate per variant (scripts sf22/sf23).

**K-floor ladder INSIDE volat ≥ 140bp** (eff ≥ 0.3 fixed; floors k300/k600/k180):

| floors | n | pf | avg% | years |
|---|---|---|---|---|
| BASE 40/60/15 | 792 | 1.535 | +2.03 | 1.24 1.58 1.01 1.34 1.65 1.74 1.85 |
| ×1.5 60/90/22 | 367 | 2.109 | +3.62 | 2.52 1.35 1.21 0.83 2.63 3.42 3.49 |
| ×2 80/120/30 | 171 | **3.262** | +5.88 | 36.02 1.36 0.85 0.52 4.99 4.60 8.42 |
| ×3 120/180/45 | 28 | 3.272 | +7.91 | (n-starved) |

**eff is NOT the lever** (flat 1.52-1.56 through 0.3-0.5; mild 1.74 @ 0.6). K is.

**⭐ THE ISO-CONTROL (the part that disciplines it)** — the same ladder everywhere:

| frame | BASE | ×2 | lift |
|---|---|---|---|
| FULL stack | 1.704 @ 7,243 | 2.546 @ 1,433 | ×1.49 |
| v < 140bp | 1.780 @ 6,588 | 2.338 @ 1,281 | ×1.31 |
| v ≥ 140bp | 1.535 @ 792 | 3.262 @ 171 | **×2.12** |

Two findings: (1) **higher K floors help EVERYWHERE** — the monotone-floor grammar is
general (full-stack ×2 = 2.546 with every year ≥ 1.49 except 2023) — so "K ×2" is
really a whole-system selectivity/size trade (7,243 → 1,433 trips), not a cell patch;
(2) **the lift is genuinely LARGEST inside ≥ 140bp** (×2.12 vs ×1.31), and at K×2 the
hot cell flips to ABOVE the rest (3.26 vs 2.34) — the user's squeeze mechanism is
real: looseness hurts hyper-volatile pops disproportionately. ⚠ The repaired ≥140
cell's middle years stay broken at every rung (2021-2023: 1.36/0.85/0.52 at ×2) —
the repair is 2020 + 2024-26; a fair-weather check must gate any adoption. ⏭
Tomorrow's decisions: where on the K ladder to sit (capacity vs quality), whether
≥140 gets its own tier/arm at a higher rung, and the rest of the port list.

---

# S37c (2026-08-31) — the 2023 holdout: five levers, one CLASS of failure; rf EXPOSED as a fit

The user's program for the day: make the weak years (2022, 2023) stronger. Every
lever below was measured on the **sf20 replay frame** (base_v4, mc=1 greedy
replay INSIDE the gate), control `7,243 @ 1.704` re-verified at the top of every
script.

## The eff floors (user: "eff 20m and 10m to >= 0.75")

| gate | n | PF | 2022 | 2023 |
|---|---|---|---|---|
| baseline (eff10 ≥ 0.3) | 7,243 | 1.704 | 1.354 | 1.479 |
| eff10 ≥ 0.75 | 2,079 | 2.015 | **1.787** | 1.365 |
| eff20 ≥ 0.75 | 248 | 2.460 | — | — |
| both ≥ 0.75 | 226 | 2.740 | — | — |

`eff_10m` has a genuine **interior peak at 0.75** (0.7 → 1.963, 0.75 → 2.015,
0.8 → 1.820, 0.9 → 1.696) — a peak, not a plateau, which is the shape that
overfits most easily. `eff_20m` is monotone but starves (248 trips = 3.4%);
inside ≥140bp it leaves **21 trips**. ⚠ This CORRECTS S37b's "eff is not the
lever" — that scout swept only 0.3-0.6, where the curve genuinely is flat. The
action is all above 0.6.

**eff20 at 0.25/0.5 (user):** 0.25 is INERT (touches 47 of 7,155 — note 88 trips
have null/negative eff_20m, so every eff20 row is against 7,155 not 7,243). 0.5
pays (1.833 @ 3,838) but **buys its lift from 2020/2024/2025 and leaves 2022-23
flat-to-worse** — the signature of a gate that concentrates the good regime
rather than repairing the bad one.

## ⭐ The 2023 ANATOMY — it is not a selection failure

| yr | n | PF | win% | avgW% | **avgL%** | gW/trip | gL/trip |
|---|---|---|---|---|---|---|---|
| 2020 | 1,320 | 1.717 | 72.2 | 3.37 | −5.10 | 2.43 | 1.42 |
| 2022 | 770 | 1.354 | **68.4** | 3.10 | −4.96 | 2.12 | 1.57 |
| **2023** | 626 | 1.479 | **71.6** | 3.82 | **−6.51** | 2.74 | **1.85** |
| 2024 | 933 | 1.698 | 73.1 | 4.28 | −6.86 | 3.13 | 1.84 |

**2022 and 2023 are broken in DIFFERENT ways.** 2022 stops winning (win rate
68.4%); 2023's win rate is normal (71.6%, = 2020/2025) and its gross win/trip is
above 2020's — it pays too much on the losers (avg loss −6.51%). That is why one
selectivity lever repairs 2022 and cannot touch 2023: **tightening entry quality
raises win rate, and 2023 does not have a win-rate problem.**

Concentration: 9 of 12 months profitable; damage is Jan (0.852), Jun (0.816),
Jul (0.777). Dropping the 5 worst trips: **1.479 → 1.942**, net 555% → 831%.
Worst trip MSGM 2023-01-31 **−93.9%** (rr 1.59 — quiet by any band).

## rr vs the big losers (user: "rr affects the worst-case loss")

Confirmed on the TAIL, monotone — and it does NOT gate the catastrophes.

| rr band | n | PF | p1 | min | avgL% | win% |
|---|---|---|---|---|---|---|
| <0.75 | 947 | **2.280** | −16.1 | **−44.1** | **−4.28** | 75.4 |
| 0.75-1.5 | 1,847 | 1.655 | −23.2 | −75.7 | −5.24 | 72.8 |
| 1.5-3 | 2,244 | 1.591 | −21.9 | −93.9 | −5.52 | 71.5 |
| 3-5 | 1,142 | 1.615 | −25.6 | −85.5 | −6.11 | 71.2 |
| ≥5 | 1,063 | 1.773 | −25.7 | −98.6 | −6.29 | 71.1 |

avgL, p1, p5 and the worst-case bound ALL degrade monotonically with rr. But
trips ≤ −20% have median rr **2.08** vs winners' 1.88 — barely separated, and 5
of the 12 worst trips in the book had rr < 2. **rr shifts the distribution of
losses; it does not remove the blow-ups.** By year: rr<0.75 lifts six of seven
years, 2023 the sole exception (1.48 → 1.17).

Also killed: the **$1 price floor** — 1.704 → 1.715 while dropping 1,195 trips
and 1,464% of net; the sub-$1 slice runs PF 1.658 and is BETTER than book in
2022/2023/2025. Disproportion test in reverse. Stacked on rr it subtracts
(1.827 → 1.826) and hurts both weak years. Keep it for borrow/fee realism if at
all, not for edge.

## ⭐⭐ be12060 — the pop-HEIGHT gate (user: "short at higher levels")

`be120 = signal_vwap / vwap_ewp_12060_be − 1`.

| be120 | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | ALL | n | net |
|---|---|---|---|---|---|---|---|---|---|---|
| BASE | 1.72 | 1.77 | 1.35 | 1.48 | 1.70 | 1.74 | 2.13 | 1.704 | 7,243 | 7,817% |
| ≥10% | 1.91 | 1.78 | 1.82 | 1.34 | 1.75 | 1.99 | 1.95 | 1.802 | 2,108 | — |
| **≥20%** | 2.27 | 2.94 | **2.41** | **1.11** | 2.59 | 2.47 | 1.87 | **2.150** | 557 | 2,232% |

**Six of seven years up; 2023 monotone in the WRONG direction** (1.48 → 1.34 →
1.11 → 0.93 at 30%). Inside ≥140bp: 1.535 → **1.955 @ 403, net 1,556% vs 1,609%**
— i.e. **~the same net at half the trips**, the user's "vastly better PF at the
same net" observation.

⚠ **The tail anatomy REFUTES the parabolic-protection story**: selling higher
does not shrink losses — avgL grows monotonically −13.31 → −16.13 → −18.00 →
−19.46 (BASE → 20 → 30 → 40%). What improves is win rate (71.5 → 76.8) and
avgWin (8.16 → 15.75). Shorting higher does not stop you being run over; it makes
you right more often and paid more when right.

⚠ **≥30% / ≥40% are NOT measurable**: 156 / 69 trips, 2022 has 8 / 2. The ≥40%
headline 2.681 rests on 2024's 21 trips printing PF **282** (near-zero
denominator). ≥20% is the deepest rung with a real year table.

## 5m range, rng_front, and the SLOPES — height beats steepness

`rng_300` IS the 5m range (rng_front = rng_300/rng_20m, FlushFader's numerator).
Head-to-head inside ≥140bp, each gate ALONE:

| gate | 2022 | 2023 | ALL | n |
|---|---|---|---|---|
| BASE | 1.01 | 1.34 | 1.535 | 792 |
| be120 ≥ 20% | 1.35 | 1.22 | **1.955** | 403 |
| rng_300 ≥ 25% | 1.35 | 1.18 | 2.012 | 365 |
| s300 ≥ 300 bp/min | **0.80** | 1.14 | 1.752 | 551 |
| s180 ≥ 400 bp/min | **0.73** | 1.23 | 1.644 | 540 |

**The slopes LOSE and break 2022.** Steepness of ascent does not predict the
blow-up; HEIGHT does. (`rng_300` ≈ be120 restated — both are "how big was the
move".) Range FLOORS inside the be120 cell are inert because **be120 ≥ 20%
already implies a large range** (cell min rng_120 ≈ 9.5%, rng_300 ≈ 17%).

## 💀 rng_front ≥ 0.55 — ADOPTED FOR 20 MINUTES, THEN EXPOSED (user called it)

It looked like the find of the day: inside be120 ≥ 20%, 2.284 @ 286 with
**2022 2.80 and 2023 1.91** — the only arm that lifted both weak years.
Composition looked right too (corr 0.487, conjunction 1.890 > either parent).

The user: *"feels like overfit — it managed to avoid one of the big losers and
that flipped the year massively."* Correct:

| year | cell n | cell PF | rf n | rf PF | dropped worst | UNGATED PF minus its 1 worst trip |
|---|---|---|---|---|---|---|
| 2022 | **20** | 1.349 | 12 | **14.392** | NRSN −69% | **4.003** |
| 2023 | **46** | 1.217 | 27 | 1.582 | TRVN −53% | 1.734 |

2022's "repair" is **excluding one ticker**; the gated 2022 book has no
meaningful loser at all (PF 14.4 on 12 trips). The year cells behind the
headline were 12 and 27 trips. **RULE REINFORCED: a year column under ~40 trips
is an anecdote — check the leave-one-out before reading a year table as a
repair.** rf DROPPED.

## The other partners (all fail the same way)

Inside be120 ≥ 20%, vs rf's 2022 2.80 / 2023 1.91:

| partner | 2022 | 2023 | ALL | n |
|---|---|---|---|---|
| s1200 ≥ 150 | 1.44 | 1.59 | 2.287 | 318 |
| vratio (volat_10m/volat_20m) ≥ 1.0 | 1.35 | 1.16 | 2.157 | 363 |
| s600 − s1200 ≥ 100 | 1.43 | 1.61 | 2.163 | 283 |
| s300 − s1200 ≥ 200 | 1.38 | 1.08 | 2.066 | 339 |

⚠ **The s1200 CEILING hypothesis is REFUTED** (user: "low s1200 + high be120"):
every ceiling underperforms the unpartnered cell (`<200` 1.711, `<150` 1.606 vs
1.955), and the band table inverts it — `[150,400)` **2.257** vs `[0,150)` 1.606.
Despite corr(s1200, rf) = **−0.608**, suppressing the 20m slope does not
reproduce rf; RAISING it does better.

**Volatility slopes** (OLS on |r| over 40/20 slots — `vs20`, `vs10`, `vexp = vs10−vs20`,
×2e4): `vs10 < 0` is the best STANDALONE gate found today — **1.704 → 1.855 @
3,219**, six of seven years up including BOTH weak ones, cut at zero (no fitting).
⚠ And `vexp` **splits the two weak years in OPPOSITE directions**: `vexp ≥ 12`
gives 2022 2.79 / 2023 0.96; `vexp < 0` gives 2022 1.28 / 2023 1.55. The same
feature, opposite signs, one per year — another face of the inversion law.

## ⭐⭐ COUNTERFACTUAL RISK ANALYSIS (user) — the stop question is CLOSED

For every book trade, price at entry+60…600 bars vs what it actually made.

**(A) trades still OPEN at N — exit now vs run to target:**

| N | n open | cf PF | actual PF | cf avg | actual avg |
|---|---|---|---|---|---|
| 60 | 6,798 | 1.148 | **1.761** | +0.12% | **+1.11%** |
| 180 | 5,850 | 0.948 | **1.489** | −0.07% | +0.74% |
| 300 | 4,117 | 0.392 | **0.751** | −1.23% | −0.51% |
| 600 | 1,622 | 0.065 | **0.126** | −4.60% | −4.27% |

**(B) the real policy min(target, N):** monotone — every time stop costs PF AND
net (target 1.704 / 7,817% → 600-bar 1.651 / 7,276% → 60-bar 1.166 / 1,111%). It
does cut avg loss (−5.52 → −2.02) and destroys far more on the winner side. Even
a 600-bar cap does not reach MSGM's −93.9%.

**Verdict: the mean reversion NEEDS TIME. Cutting losers earlier is wrong at
every horizon.** Being open a long time is itself the bad signal — but realising
it early is worse than waiting. This closes the stop hypothesis raised from the
2023 anatomy.

## ⭐⭐ EXIT CHANNEL SWEEP — 9m beats the production 5m (user's 7-9m prior)

⚠ **A simulated sweep FAILED its control first** (chan=300 gave 2.102 vs the
book's 1.704) even though all 7,243 exit *prices* matched exactly. Cause: the
**return convention**. Verified empirically on the book: stored
`ret_exit = −(exit_px/entry_px − 1)` — the SHORT sign is already applied.
`entry/exit − 1` (the naive short form) inflates every return by r²/(1+r).
⭐ Then the user pointed out the engine ALREADY RECORDS the marks
(`aux_lo_{N}_px/_sec`) — no simulation needed. Control passes exactly at
**7,243 @ 1.7038**.

| chan | n | PF | net% | win% | med hold | unres% |
|---|---|---|---|---|---|---|
| 300* | 7,243 | 1.704 | 7,817 | 72.2 | 386 | 0.0 |
| 420 (7m) | 7,064 | 1.734 | 9,042 | 72.0 | 562 | 0.1 |
| 480 (8m) | 7,007 | 1.777 | 9,785 | 72.5 | 647 | 0.2 |
| **540 (9m)** | 6,947 | **1.802** | **10,332** | 72.6 | 746 | 0.4 |
| 600 | 6,894 | 1.791 | 10,598 | 72.6 | 843 | 0.6 |
| 1200 | 6,714 | 1.847 | 13,038 | 73.0 | 1,492 | **16.9** |

**540 gives PF +0.098 AND net +32% simultaneously.** In the be120 cell:
1.955 → **2.488**; with s1200 ≥ 150: 2.287 → **2.999**.

**The "unresolved" marks are NOT moc exits** (user asked): at chan 1200, 1,136 of
1,137 are recorded `target` — the trade covered on its ORIGINAL 5m channel and
the day ended before price printed a new 20m low. They carry PF **2.044**, ABOVE
the resolved 1.820. On a like-for-like resolved-only basis 540/600/1200 are
within 0.016 (1.816 / 1.805 / 1.820) — **the gain is in leaving 300, and it
SATURATES at 480-540.** ⚠ 420/540 are not in the engine's `exitChanSet`
{30,60,120,300,600,1200}; adopting needs a one-line Program.fs change.

## be120 ∧ s1200 vs s1200 alone — the answer DIFFERS by frame

| frame | arm | n | PF | 2022 | 2023 |
|---|---|---|---|---|---|
| v≥140bp | BOTH (be120≥20 ∧ s1200≥150) | 313 | 2.999 | 1.47 | 2.11 |
| v≥140bp | s1200 ≥ 200 (same-n) | 294 | **3.839** | **0.97** | 2.38 |
| FULL | BOTH | 358 | **2.363** | **1.78** | 1.49 |
| FULL | s1200 ≥ 200 (same-n) | 317 | 2.547 | **1.02** | 1.44 |
| FULL | be120 ≥ 25% (same-n) | 318 | 2.271 | 1.49 | 1.55 |

Inside the hot cell s1200 alone can replace the pair at matched n; **on the full
book it CANNOT** — its edge concentrates in years that were already fine (2022
→ 1.02), and its ladder runs into empty cells fast (≥250: 2022 unprintable, 2024
11.36; ≥300: 2026 **479.77**). corr(be120, s1200) = 0.633 full-book / 0.358 in
the cell, yet the conjunction beats both parents in both frames. At the 9m exit
the pair is **2.925 @ 353, net 2,375%, every year ≥ 1.66** — the most robust arm
of the session.

## Charts

`spikefader_loser_charts.py` (scratchpad) — the FlushFader loser-chart twin,
direction-flipped: ENTRY = new 20m HIGH, EXIT = 5m MIN cover target, and the
point is the mirror image — **on a squeeze the cover target RATCHETS UP behind
price**, so the short is carried the whole way before it may cover (MSGM: entered
$11.47, covered $22.25, 48 min). ⚠ base_v4 has NO adj_ratio column (retired
scheme); verified on MSGM that trip prices sit inside the raw bar range, so bars
and trip prices share one axis with no rescaling. Also: `scrollZoom` is a plotly
CONFIG flag, NOT part of `chart_controls.js` (which only does middle-click +
a/s/d) — `flushfader_loser_charts.py` was missing it and has been fixed; all 29
chart scripts now pass it.

---

# S37f-s (2026-08-31) — ⭐⭐ SPEC CHANGES ADOPTED: exit 5m → 9m, and k600 60 → 90

Two adoptions today, both user-decided, both engine-verified. Everything below is
on the **mc=1 replay INSIDE the gate**, control-exact (`chan 300` reproduces
7,243 @ 1.7038 net 7,817% at the head of every script).

## ⭐ THE PF−1 REFRAME (user, and it changes how every table below reads)

> "the actual profit itself isn't the PF, but PF−1. So going from 1.832 to 2.308
> isn't a 26% increase in profitability, but a 57.2% one."

Correct, and it is the convention already recorded for sizing
(`feedback_trim_bottom_5pct_when_comparing`: sizing = PF−1 on the trimmed book).
**Every ladder in this section reports PF−1 as the edge term.** Under it, a gate
that costs 30% of net to buy 55% more edge is a good trade, which is what made
the k600 decision go the way it did. ⚠ Months of prior tables quote raw PF deltas
and therefore *understate* the value of selectivity — re-read old ladders with
this in mind.

## ADOPTION 1 — the exit channel: 300 → 540 bars (5m → 9m)

Engine-verified end to end (`ExitChannelBars = 540`): **6,945 @ PF 1.756, net
9,969%** vs 7,243 @ 1.704 / 7,817%. Six of seven years up; 2022 the lone loser
(1.35 → 1.22).

| chan | n | PF | PF−1 | net% | win% | med hold | unres% |
|---|---|---|---|---|---|---|---|
| 300* | 7,243 | 1.704 | 0.704 | 7,817 | 72.2 | 386 | 0.0 |
| 420 (7m) | 7,064 | 1.734 | 0.734 | 9,042 | 72.0 | 562 | 0.1 |
| 480 (8m) | 7,007 | 1.777 | 0.777 | 9,785 | 72.5 | 647 | 0.2 |
| **540 (9m)** | 6,947 | **1.802** | **0.802** | 10,332 | 72.6 | 746 | 0.4 |
| 720 (12m) | 6,829 | 1.832 | 0.832 | 11,655 | 72.7 | 1,035 | 1.2 |
| 1200 (20m) | 6,714 | 1.847 | 0.847 | 13,041 | 73.0 | 1,492 | 16.9 |

**Not tail-driven**: trim-5% PF 3.297 → 3.491 and the **MEDIAN** trip 2.016% →
2.638%; paired per-trip, 540 beats 300 on **76.5% of the SAME trips** (Wilcoxon
p ≈ 6e-279). PF plateaus from 9m out (540/720/1200 within 0.05) while **net rises
monotonically to 20m** — so 12m is the natural next step if more net is wanted;
9m is the conservative rung. ⚠ avg LOSS widens with the hold (−5.52% → −6.77% →
−7.52%): more net, fatter left tail.

⚠ **Three latent bugs surfaced adopting this**: `exitChanSet` did not contain
420/480/540 (the first 540 run ABORTED: "no 540-bar channel"); `chanMin` reached
only the six legacy windows; the banner hardcoded "~5m". All fixed.

## ADOPTION 2 — k600: 60 → 90 (k300 STAYS at 40)

`k300 ≥ 60 ∧ k600 ≥ 90` was the user's proposal; the sweep says **k600 is the
load-bearing floor and k300 is not a lever** — but k300 ≥ 40 is still a
load-bearing FLOOR. Both facts matter and they are not the same claim.

**k600 swept, k300 fixed at 60** (steep, monotone) vs **k300 swept, k600 fixed at
90** (flat):

| k600 (k300=60) | PF−1 | n | | k300 (k600=90) | PF−1 | n |
|---|---|---|---|---|---|---|
| ≥60 | 0.793 | 5,738 | | ≥50 | 1.100 | 3,512 |
| ≥80 | 0.987 | 3,962 | | ≥60 | 1.155 | 3,281 |
| **≥90** | **1.155** | 3,281 | | ≥70 | 1.159 | 3,007 |
| ≥120 | 1.376 | 1,719 | | ≥90 | 1.194 | 2,484 |

**THE ADOPTED ARM — `k300 ≥ 40 ∧ k600 ≥ 90`, one parameter changed:**

| arm | n | PF | **PF−1** | net | avg/trade | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BASE (k600≥60) | 6,824 | 1.727 | 0.727 | 10,742% | 1.574% | 1.80 | 1.72 | 1.18 | 1.56 | 1.69 | 2.06 | 1.95 |
| **k600 ≥ 90** | 3,777 | 2.098 | **1.098** | 8,001% | 2.118% | 2.39 | 2.02 | 1.40 | 1.93 | 2.09 | 2.65 | 2.10 |
| (k300≥60 too) | 3,281 | 2.155 | 1.155 | 7,240% | 2.207% | 2.65 | 1.96 | 1.54 | 1.83 | 2.07 | 3.00 | 1.90 |

**+51.0% edge for −25.5% net** (the pair: +58.9% for −32.6% — worse ratio, two
parameters, and only 2022 prefers it). **Every year improves.**

**k300 ≥ 40 must STAY**: dropping it costs −21% edge (1.098 → 0.863) *and* net
(8,001% → 7,025%) for +158 trips — strictly dominated. What it removes is 687
trips (17.5%) at PF 1.298, avg +0.81%/trade vs the kept book's 2.12%. And 40 is a
real knee (0.863 → 0.968 → **1.098** → 1.100), not an inherited number.

**All four controls pass** for k600 ≥ 90:
* **same-n** (~3,800): k600 **1.098** vs volat≥65bp 0.811, k120≥50 0.744, k180≥60 0.759.
* **ticker-day resample**: observed 2.098, null max 1.948 over 2,000 draws → **p = 0.0000**.
* **leave-one-year-out**: lift **1.463–1.571×**, no year carries it.
* **year table**: 7 of 7 up.

## What did NOT survive (the same day's rejects)

| candidate | verdict |
|---|---|
| k180 ≥ 15 → higher | flat: 15 is a local peak; 70.7% of the SLOW book is already k180 ≥ 45 (the triangle) |
| k60 / k120 / k30 trims | best rung buys +8.4% edge for −52% trips; ladders non-monotone |
| k120 ≥ 64 ∧ k180 ≥ 78 (the S18b "A-cell") | **~30 trips** inside ≥140bp; "DIAG only" PF 0.730 on 16. The 3.14 @ 21,127 headline was an **mc=0 SURVEY** number with no year columns |
| be300180 (5m,3m) | does NOT extend the be60→be120 ladder: same-n 2.412 vs be120's 2.445, non-monotone, **corr(be120,be300)=0.968** — a re-parameterisation. **be120 is the optimum window (~2 min)** |
| be6030 raised | be120's trips are a strict SUBSET of be60's; the non-overlap runs 1.415 vs 2.778. Wider window = height, narrow = speed, and speed keeps losing |
| UNION (SLOW ∨ be120) | 2.239 @ 3,523 — **below** plain SLOW (2.308 @ 3,282). be120's 544 trips carry avg loss −18.3% and widen the tail |

⭐ **The fast-counter INVERSION**: inside SLOW, *low* fast counters are the BEST
band, monotone decreasing — k60 [0,5) → PF−1 **1.632**, [5,15) 1.452, [15,30)
1.233. S18b called low-fast the anti-signal on the **ungated mc=0** frame. Once
the slow legs are mature, a quiet recent 1-2m stretch means the pop has STALLED.
Not adopted (post-hoc band, no controls) but it is the opposite of a floor.

---

# S37t-v (2026-08-31) — volat buckets under the NEW spec; ports #2 (d20a) + the ROSTER opens

## ⭐⭐ k600 ≥ 90 REPAIRED THE ≥140bp CELL (the thing that resisted all morning)

The hot cell went from the WORST part of the book to the BEST, for free, off a
maturity floor — no be120/rng_front/slope machinery needed:

| cell | PF−1 | n | net | 2022 | 2023 |
|---|---|---|---|---|---|
| v ≥ 140bp, OLD spec (k600≥60) | 0.900 | 758 | 2,955% | 1.42 | 1.83 |
| **v ≥ 140bp, NEW spec (k600≥90)** | **1.715** | 455 | 2,616% | 1.68 | 1.73 |
| v < 140bp, NEW spec | 0.993 | 3,395 | 5,890% | 1.43 | 1.95 |

Mechanism (user's squeeze thesis, confirmed): demanding a mature 10m leg stops us
shorting hyper-volatile pops EARLY, and early is precisely where a hyper-volatile
pop is most expensive. ⚠ The band dip MOVED under the new spec — it was
`[140,200)` and is now `[80,100)` (PF−1 0.648). **A dip that relocates when an
unrelated gate changes is noise in ~600-trip bands, not a feature of the
volatility axis.** Do not build an exclusion band on it (this is what the S37
"candidate exclusion band [140,180)" idea was, and it is hereby dropped).

Volat bands, NEW spec, 12m exit: [40,60) 0.673 · [60,80) 0.910 · [80,100) 0.648 ·
**[100,140) 2.118** · [140,200) 1.312 · [200,300) 3.350 · ≥300 5.932 (n 34).

## 🛑 THE THREE mc VIEWS — a discrepancy the user caught, and the rule that fixes it

The user spotted that dec9+10 of the d20a decile table (~1.68) contradicted the
`d20a ≥ 45%` ladder row (1.434). Both numbers were right; they answer different
questions, and mixing them in one comparison is the error.

| view | how | d20a ≥ 0.419 |
|---|---|---|
| **SLICE** — build the mc=1 book ONCE, then slice it | voice / SIZING | **1.664** @ 755 |
| **REPLAY-INSIDE** — filter signals, THEN greedy-replay | gate / ENTRY | 1.492 @ 1,143 |

Reconciliation: the slice is a strict SUBSET (663 of 663 shared, 0 exclusive); the
replay admits **342 extra trips at PF−1 1.089** which drag the mean down. Those are
ticker-days whose FIRST qualifying signal had low d20a — the book took it and the
slot was gone; filter first and a later, higher-d20a signal wins the slot instead.
**Later entries into an already-run pop are worse.**

**Rule:** a VOICE used for SIZING reads on the SLICE; a voice used as an OR-GATE
(FlushFader's roster is a gate) reads REPLAY-INSIDE. State which one before
quoting a number. See [[feedback_three_mc_questions]].

## PORT #2 — d20a: PORTS SUCCESSFULLY (the leg's total pop height from its birth low)

`d20a = signal_vwap / first_high_vwap · (1 + d_lo_flow) − 1` (mirror of
FlushFader's arming-high depth). Deciles inside the new-spec book are monotone
across the top half — dec6 1.009 → dec7 1.179 → dec8 1.121 → **dec9 1.734** →
dec10 1.629, win rate 75.3 → 79.1.

## THE ROSTER SO FAR — three voices, slice basis, vs the book's PF−1 1.098

| voice | PF−1 | n | share | net | note |
|---|---|---|---|---|---|
| **volat ≥ 100bp** | **2.099** | 804 | 21.3% | 4,191% | best; most uniform year row (2022 = 2.54) |
| **d20a ≥ 28%** | 1.460 | 1,495 | 39.6% | 5,476% | the CAPACITY voice |
| d20a ≥ 42% | 1.661 | 750 | 19.9% | 3,567% | |
| be120 ≥ 20% | 2.779 | 124 | 3.3% | 1,113% | ⚠ 3 of 7 years have < 10 trips |

⚠⚠ **THEY ARE NEARLY ONE VOICE.** Of be120's 124 trips, **122 are also d20a and
122 are also volat**; 594 of d20a's 750 are also volat. corr(d20a, volat) = 0.798,
corr(d20a, be120) = 0.714. `d20a ONLY` = **0.486 — BELOW book**; `volat ONLY` =
2.372. **Volat carries the family; d20a adds nothing independently.**

**Vote-count tiers (monotone — the usable sizing ladder):**

| votes | PF−1 | n | share | net |
|---|---|---|---|---|
| 0 | 0.742 | 2,816 | 74.6% | 3,539% |
| 1 | 1.278 | 365 | 9.7% | 1,133% |
| 2 | 1.831 | 475 | 12.6% | 2,250% |
| 3 | 2.695 | 121 | 3.2% | 1,080% |

⚠ With 122/124 nesting, tier 3 IS "be120 fired" under another name. Recommendation:
`volat ≥ 100bp` primary + `d20a ≥ 28%` secondary (capacity), be120 DROPPED as
redundant; the remaining ports must supply genuinely different families.

**User framing (2026-08-31):** the roster is an OR-GATE in FlushFader, but voices
may also be used for SIZING here — keep both views. And on capacity: *"It's not
worth sacrificing the net directly anymore... trying to be too consistent isn't
good either. It's good enough to find a sweet spot."* — hence volat ≥ 100bp enters
as a VOICE, not as a raised floor.

---

# S37w-x (2026-08-31) — ports #3/#6/#7/#8; ⭐ dslo is the FIND; legage CLOSED

Slice basis (voice/sizing view) under the new spec, 12m exit, book PF−1 **1.098**.

## ⭐⭐ PORT #3 — dslo (dist off the SESSION HIGH): the strongest voice yet, and INDEPENDENT

| arm | PF−1 | n | share | net | win% |
|---|---|---|---|---|---|
| BOOK | 1.098 | 3,777 | 100% | 8,001% | 74.3 |
| dslo ≤ −2% | 3.320 | 288 | 7.6% | 1,135% | 84.7 |
| **dslo ≤ −5%** | **5.034** | 245 | 6.5% | 1,084% | **86.1** |
| dslo ≤ −8% (the FF mirror) | 4.876 | 201 | 5.3% | 942% | 86.6 |
| dslo ≤ −12% | 5.943 | 154 | 4.1% | 807% | 89.0 |

Every rung ≥ 3.3, every year ≥ 3.36 at −8%. **91.3% of the book shorts within 0.1%
of the session high** (median dslo = 0.0000) — so this fires on a small, genuinely
distinct minority: *don't short AT the high, short the FAILED RETEST.*

**⭐ It is the first roster voice that is actually INDEPENDENT**: corr(dslo, volat)
= **−0.043**, corr(dslo, d20a) = **−0.010**; only 48 of 245 overlap volat, 100
overlap d20a. And `dslo ONLY` (no other voice fires) = **PF−1 5.332 @ 144, win
86.8%** — compare `d20a ONLY` = **0.486, BELOW book**. The height family is one
voice measured three ways; dslo is a second family.

## PORT #7 dsu / PORT #8 haltband — weak and partial

* **dsu** `upticks_since_downtick ≥ 8` = **1.005, BELOW book**. The FF threshold
  does not transfer (distribution here is tight: p50 3, p90 6, max 24). Only ≥3
  does anything (1.397 @ 54.7%) and that is a tilt, not a voice.
* **haltband**: the SIMPLE binary ports — `halts_today ≥ 1` = **1.609 @ 10.4%**
  (vs 1.008 unhalted). The since-resume BANDS do not (all ~1.0-2.2 on thin cells,
  no band shape) — consistent with S23 already inverting the fresh-resume side.
  Take the binary, not FlushFader's windows.

## 💀 PORT #6 legage — CLOSED on this side (and the first test was WRONG)

⚠ The first attempt used `secs_since_first_high`, which ages the **MAIN (20m
channel)** leg only and says nothing about the 5m/10m legs. Redone per-leg (the
engine already records `bars_since_first_high_300/_600`, each reset on its own
channel breach via `brLo300/brLo600 → countersN.Reset()`).

All three leg ages are NON-MONOTONE and noisy at ~378 trips/decile:

| leg | dec1 → dec10 (PF−1) |
|---|---|
| 10m (`_600`, the primary) | 0.671 · 1.739 · 1.090 · **0.490** · 0.750 · 1.498 · 1.301 · **2.037** · 1.008 · 1.289 |
| 5m (`_300`) | 1.089 · **0.361** · 0.850 · 0.788 · 1.697 · 1.766 · 1.250 · 1.518 · 1.157 · 1.233 |
| 20m (main) | 1.045 · 0.786 · **0.381** · 1.469 · 1.070 · **2.167** · 1.647 · 0.815 · 1.825 · 0.866 |

Adjacent deciles swing by 4×. There IS a weak old-is-better tilt (and the FF
direction — favour YOUNG legs — is definitely inverted: `secs_since_first_high ≤
450` = **PF−1 −0.353**), but it is ~0.3-0.5 buried in ±0.8 of noise.

**⭐ THE REASON (user, and it generalises):** *"FlushFader is a different system.
The long fade uses a K band around [26,50], but the short side is unbounded, so
`legage` doesn't apply."* A BOUNDED K band leaves age genuinely free to vary
within it, so age carries information the counter does not. A MONOTONE K FLOOR
(this side) pre-selects age directly — `corr(age10m, k600) = +0.437` — so leg age
is a noisier restatement of the gate. **Same subsumption that killed the
k180/k60/k120 trims.** Port closed; re-test only if k600 is ever lowered.

## ROSTER STATE (voices, slice basis)

| voice | PF−1 | n | share | family |
|---|---|---|---|---|
| **dslo ≤ −5%** | **5.034** | 245 | 6.5% | position vs session high — INDEPENDENT |
| **volat ≥ 100bp** | 2.099 | 804 | 21.3% | height/energy — carries this family |
| **d20a ≥ 42%** (user raised from 28%) | 1.661 | 750 | 19.9% | height — 28% included ~40% of the book, too coarse |
| halts_today ≥ 1 | 1.609 | 394 | 10.4% | event |
| ~~be120 ≥ 20%~~ | 2.779 | 124 | 3.3% | DROPPED — 122 of 124 nested inside d20a AND volat |

⚠ **Vote-counting is NOT monotone** on the 4-voice set (0.579 / 0.858 / 2.089 /
1.915 / 3.103) — the 1-vote tier is BELOW book. The voices differ too much in
strength to count; weight them or split 0-1 vs 2+. As an OR-GATE (FlushFader
semantics, replay-inside): **PF−1 1.378 @ 2,189, net 6,870%** vs book 1.098 /
8,001% — +25% edge for −14% net.

⏭ `d20a ≥ 28%` is a future SPEC candidate (user: "not right now").
⏭ Remaining unported: **vcrush** (#5) — blocked on `volat_slope_5m`, needs an
engine add + rerun.

---

# S37y-z (2026-08-31) — deep-K voices CLOSED, k120 ceiling FAILS controls, vcrush ✗ — the port list is DONE

## Deep-K cells as voices: all absorbed by k600 ≥ 90

S18b's deep cells were flagged as voice candidates on the **mc=0 survey** frame.
Re-tested as voices (slice basis) inside the new spec, they are gone:

| candidate | PF−1 | n | "ONLY" cell (no other voice) |
|---|---|---|---|
| k600 ≥ 150 | 1.284 | 290 | **1.082** (book = 1.098) |
| k300 ≥ 110 | 1.346 | 469 | **1.156** |
| k20m ≥ 220 | 0.572 | 156 | **0.514** |
| k120≥64 ∧ k180≥78 (the S18b diagonal) | **0.893** | 647 | — |
| k60≥46 ∧ k120≥64 | **0.499** | 413 | — |

Both S18b diagonals are now **BELOW book**. After k600 ≥ 90 the counter is squeezed
into a narrow band (median k600 = 94, p75 = 114), so "deeper" is the top slice of an
already-selected population. **The information was in the FLOOR and we already took
it** — same subsumption that closed legage and the k180/k60/k120 trims.

## The k120 CEILING (the S37p inversion) — real observation, FAILS as a voice

Individual ladders confirm the inversion: k60's floor ladder DECAYS monotonically
(1.066 → 1.022 → 0.874 → 0.747 → **0.431** at ≥60), and k120's octiles fall
1.923 (oct1 [0,15]) → **0.708** (oct8 [85,218]). High fast counters are genuinely
bad. But `k120 < 30` (PF−1 1.351 @ 1,340) does not survive:

* **ticker-day resample p = 0.0925** — null p95 = 1.422 is ABOVE the observed 1.351.
* **same-n controls BEAT it**: volat ≥ 85bp **1.601**, d20a ≥ 33% **1.567**; and the
  sibling counters land in the same place (k60 < 16 = 1.296, k180 < 42 = 1.281) —
  the mark of generic selectivity, not a k120 property.
* **no knee**: the ladder is monotone from `<15` (1.953) to `<60` (1.245). A band
  with no interior optimum is just "fewer trips".
* (leave-one-year-out passes, 1.14-1.37× — but that only rules out one-year deps.)

⚠ The band was chosen by INSPECTING the octile table, which is exactly when the
controls matter most. Not adopted.

## PORT #5 — vcrush ✗ (and the volatility-slope family lives at 10m, not 5m/3m)

Engine add: `volat_slope_5m` + `volat_slope_3m` (+ Pearson-r twins), mirroring
`TradingEdge.FlushFader/Intraday.fs` S43cg exactly — `FloatOls 10` / `FloatOls 6`
on the shared 30s-slot |r| ring, record-only.

| arm | PF−1 | n | share |
|---|---|---|---|
| BOOK | 1.098 | 3,777 | 100% |
| **vs5 ≤ −24 (the FF mirror)** | **1.123** | 160 | 4.2% |
| vs5 ≤ −12 | 1.353 | 401 | 10.6% |
| vs5 ≥ 24 | 0.854 | 523 | 13.8% |
| vs3 ≤ −24 | 1.068 | 469 | 12.4% |
| **vs10 ≤ 0** | **1.410** | 1,131 | 29.9% |

**No lift at the ported threshold**, octiles flat and non-monotone (1.376 · 0.834 ·
0.828 · 0.923 · 1.568 · 1.255 · 1.310 · 0.927). Independence kills it: of the 160
trips, **111 also fire volat and 96 also fire d20a**, and the isolated cell is
**PF−1 −0.462 on 40 trips** (losing). The vs3 twin is uniformly worse, so it is not
a calibration problem. ⭐ Only the WIDEST window shows anything (`vs10 ≤ 0` = 1.410),
consistent with S37c's finding that `volat_slope_10m < 0` was the best standalone
gate on the old spec. **The volatility-slope signal lives at 10m.**

## 🏁 THE FLUSHFADER PORT LIST IS COMPLETE — 4 of 9 ported

| # | voice | verdict | PF−1 | n | share |
|---|---|---|---|---|---|
| 3 | **dslo ≤ −5%** | ⭐ THE FIND — independent (corr −0.04 / −0.01) | **5.034** | 245 | 6.5% |
| 1 | **volat ≥ 100bp** (v20 re-derived) | ports | 2.099 | 804 | 21.3% |
| 2 | **d20a ≥ 42%** | ports; height-family redundant with volat | 1.661 | 750 | 19.9% |
| 8 | **halts_today ≥ 1** | binary only — NOT the since-resume bands (S23) | 1.609 | 394 | 10.4% |
| 4 | vexp | ✗ splits 2022/2023 in OPPOSITE directions | — | — | — |
| 5 | vcrush | ✗ 1.123; isolated cell −0.462 | — | — | — |
| 6 | legage | ✗ bounded-band vs monotone-floor (user) | — | — | — |
| 7 | dsu | ✗ 1.005, below book | — | — | — |
| 9 | acneg | skipped — already the stack's knife | — | — | — |

⏭ Open: the roster's aggregation (vote-counting is NOT monotone — weight, or split
0-1 vs 2+); `d20a ≥ 28%` as a future SPEC candidate; 12m exit as the next rung.

---

# S38 (2026-09-01) — rr JOINS THE ROSTER (S-tier); the eff > 0.75 finding INVERTS under the new spec

Slice basis, new spec (k600 ≥ 90), 12m exit. Book PF−1 **1.098**.

## ⚠ Provenance correction first

The "`rr < 0.5` is A-tier" recollection is **MaxRiderV1's**, not SpikeFader's — S24
records `rr < 0.2 → 5.90` on MaxRider's *1m* corpus, and flags that benchmark as
**suspect** (diprider_v6_candidate carries the S39d lookahead pair; the system is
stamped INVALID). SpikeFader's own rr (S20/S20d) was second-order: `rr < 0.75` =
PF 2.607 at mc=0 but **1.698 at mc=1**. So this is a fresh measurement, not a
verification — and it lands well anyway.

## ⭐⭐ rr < 0.5 — VOICE #5, and it is INDEPENDENT

`rr_15m = vol_60 / (vol_0945_tape · 60/900)`.

| arm | PF−1 | n | share | net | win% |
|---|---|---|---|---|---|
| BOOK | 1.098 | 3,777 | 100% | 8,001% | 74.3 |
| **rr < 0.5** | **5.584** | 116 | 3.1% | 411% | **87.1** |
| rr < 0.75 | 1.798 | 327 | 8.7% | 757% | 78.3 |
| rr < 1.0 | 1.493 | 603 | 16.0% | 1,290% | 77.1 |
| rr ≥ 3 (the loud arm) | 1.360 | 1,296 | 34.3% | 3,500% | 74.8 |

**The U survives** in octiles: oct1 1.614, belly oct3-5 0.435-0.889, oct7-8
1.476/1.250. Both tails beat the middle.

**Independence**: corr(rr, volat) ≈ 0, corr(rr, d20a) ≈ 0, corr(rr, dslo) ≈ 0. Of
116 trips only 34 fire NO other voice — and that isolated cell is **PF−1 4.642**
(vs `d20a ONLY` = 0.486). A second genuinely orthogonal family alongside dslo.

**Controls:** ticker-day resample **p = 0.0020** (null p99 = 4.075); LOYO lift
**3.88-11.90×, every year**; and NOT one-trip-driven — 15 losers of 116, worst
−11.7%, PF−1 without the three best trips still **4.979** (the test rng_front
failed). Same-n: `dslo ≤ −12%` reaches 5.943 @ 154, but the two barely overlap, so
they are complements not rivals.

⚠ **Discount the magnitude, not the effect.** 116 trips (3.1%); 2022 has **zero
losers** (PF = nan) and 2026 prints 119.71. The 5.584 will regress; the controls say
the direction is real.

## 💀 eff > 0.75 — THE OLD FINDING INVERTS UNDER k600 ≥ 90

S37c (old spec): `eff_10m ≥ 0.75` → PF 2.015 vs a 1.704 book (**+18%**).
Today (new spec): **0.718 vs 1.098 (−35%)**. A clean sign flip.

| eff_10m | PF−1 | n | share | win% |
|---|---|---|---|---|
| BOOK | **1.098** | 3,777 | 100% | 74.3 |
| ≥ 0.5 | 0.953 | 2,660 | 70.4% | 73.6 |
| ≥ 0.7 | 0.704 | 977 | 25.9% | 72.7 |
| ≥ 0.75 | 0.718 | 665 | 17.6% | 71.9 |
| ≥ 0.9 | **0.367** | 110 | 2.9% | **60.0** |

**Every rung is BELOW the book, monotonically**, win rate decaying 74.3 → 60.0.
`eff_20m` matches until ≥0.7 where it turns up (1.359) — but that is 177 trips with
2025 at 15.78; not readable.

**Mechanism:** efficiency measures how directionally CLEAN the run is, and k600 ≥ 90
already demands many highs in the 10m leg — i.e. a persistent, clean push. Stacking
eff over-selects for *still running smoothly*, which is exactly what a fader must
not buy. Same subsumption family as legage / deep-K, but here it goes PAST neutral
into actively harmful. ⭐ **Third instance of the law: a monotone floor does not
just absorb a correlated feature, it can INVERT it.** eff is not a voice; the
CEILING side is the direction worth testing.

## THE 5-VOICE ROSTER — adding rr made the aggregation MONOTONE

| votes | PF−1 | n | share | net | win% |
|---|---|---|---|---|---|
| 0 | 0.610 | 2,520 | 66.7% | 2,693% | 71.9 |
| 1 | 1.326 | 498 | 13.2% | 1,378% | 79.5 |
| 2 | 2.125 | 486 | 12.9% | 2,319% | 79.2 |
| 3 | 2.124 | 253 | 6.7% | 1,456% | 77.1 |
| 4 | 2.534 | 20 | 0.5% | 156% | 90.0 |

Monotone through tier 3 (the 4-voice version had a 1-vote tier BELOW book at 0.858),
and the 0-vote tier fell 0.742 → 0.610. **OR-gate (replay-inside): PF−1 1.623 @
1,568, net 6,067%** — up from 1.378 @ 2,189 with four voices.

**ROSTER:** `rr < 0.5` (5.584, independent) · `dslo ≤ −5%` (5.034, independent) ·
`volat ≥ 100bp` (2.099) · `d20a ≥ 42%` (1.661) · `halts_today ≥ 1` (1.609).
