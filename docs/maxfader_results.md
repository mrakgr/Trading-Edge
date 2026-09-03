# MaxFader — MaxFlyerV2 on the 1s tape (the hold-to-close SHORT pop-fade)

**`TradingEdge.MaxFader`** (2026-09-03). The second of the two 1m→1s conversions
(user: "the hard one first"; LowFlyer follows). Fade the **new SESSION HIGH**, short,
**hold to the close**. Sampler mc=0, record-first, every lever a recorded column.

## What is being ported, and why none of its numbers are a baseline

`TradingEdge.MaxFlyerV2` (1m): a bar CLOSES above the prior session high (close-based
ref) on a bar whose volume EXCEEDS the running session 1m-volume max; short; MOC. Its
master gate was **`brv20d ≥ 100` = 2,760 trips / PF 6.65 / 88.7% win** — and it is
**contaminated**: `brv20d = bar_vol / (avgvol20 × adj_ratio / 390)` uses `avgvol20`
(INCLUDES day D) and `adj_ratio` (folds in FUTURE splits). Both are the lookahead classes
of `docs/lookahead_protocol.md`; the doc's own tail says so. The secondary lever
`bar_rvol_15m` (vs the opening-15m mean) is causal.

**The design problem the user named:** it HOLDS TO CLOSE, so every large up day hits
it — worst trip −839%, 3% of trips below −20%. Per the SpikeFader lessons
(`project_next_session_2026-09-01`): time stops and loss stops LOSE on a short MR book;
look at **entry selection and sizing**, not exits. Every table in this doc carries the
tail block (worst / p1 / p5 / %<−20%) for that reason.

## The engine: a fork of SpikeFader, three edits

SpikeFader already carried every primitive — and said so in its comments:
`sessHigh`/`sSessHigh`/`brSess` (the session-high event); `tVol60PriorMax =
TimeLagMaxMa 60` over `tVolSum60` (*"the MaxRider F10/F12 volHIGH mirror … LowFlyer
uses the session 1m max — baked in here for the eventual port"*); `vol0945Tape`; the
halt detector; time-clock volume; calendar-aware MOC; the mc=0 sampler; the day-worker
pipeline; forward/aux marks at every minute. So:

| step | commit | what | proof |
|---|---|---|---|
| 1 | `eafeab1` | verbatim fork | zero-diff: 5,902 = 5,902 trips, 461 cols, symmetric difference 0 |
| 2 | `acfc9d5` | `EntryChannelBars = 0` → SESSION channel (`priorEntryMax` reads `sSessHigh`); `ExitChannelBars = 0` → no target, MOC only (`sExitMin` forced ValueNone); `avgvol20_prior` on the row | signal > prior session high on 2,973/2,973; exit = `moc` on all |
| 3 | `cd1220e` | prepass + harness | one-day superset check 44/44 |

The causal levers, all derivable from recorded columns:

| lever | formula | note |
|---|---|---|
| `brv15_tape` | `vol_60 / (vol_0945_tape / 15)` | MaxFlyerV2's `bar_rvol_15m`; same-day, **split-immune — PRIMARY** |
| `brv20d_prior` | `vol_60 / (avgvol20_prior / 390)` | the causal `brv20d`; `20 PRECEDING AND 1 PRECEDING`, day-D raw shares. ⚠ straddling a reverse split → near-zero denominator (S39d) — **SECONDARY, split-guarded** (`n == 1`) |
| `volhigh60` | `vol_60 > vol_60_prior_max` | MaxFlyerV2's STRICT "new session volume high" (F10/F12) |
| `chg_1d` | `entry_px / close_m1 − 1` | extension into the fade, both day-D raw |
| `ret_540` | `−(aux_lo_540_px / entry_px − 1)` | SpikeFader's 9m cover on MaxFader's entry — an exit comparison without a rerun |

`vol_60` is the 60-**tradeable-second** sum (halt-adjusted clock, §S7); the 1m system's
"bar volume" has no cleaner analog.

## ⭐⭐ The whitelist-warmth lesson (the step-1 argument was wrong, measurably)

Step 1 claimed: *every session high is a 20m high, so base_v4's trip ticker-days
(`spikefader_s44_whitelist`, 208,708) are a complete whitelist.* The price inequality is
true; the claim is false, because a channel signal also needs the channel **WARM**. On
the 5-day smoke, 650 of 2,973 MaxFader signals fired with `bars_present` 632–1,197 —
the first ~10 minutes after 09:45, where the 1200-bar channel is cold and SpikeFader
structurally cannot fire. At ticker-day level **34 of 137 (24.8%, carrying 11.0% of
trips) had NO SpikeFader trip** and would have been silently dropped: the early spike
that never re-takes its high — a real, distinct population.

*General form: a subset argument between two channel signals must account for warmth,
not just the price inequality.*

Replacement — `scripts/equity/maxfader_prepass.py`, a pure-SQL **superset of the
`--base-run` signal by construction**: `barnum ≥ 22` ∧ a bar in [09:45, 15:00] strictly
above the running prior max since 09:30 ∧ `max |30-bar-slot log return| ≥ 40bp − 1e-6`
(the S39j convexity argument rebuilt — `volat_20m` is a convex combination of that |r|
stream, so a day whose max is under the floor can never open the gate; the v2 table has
no `max_slot_absr_bp`). **No dv/tc floors**: the engine's 60-second window is
halt-adjusted tradeable time and can exceed 60 wall-clock seconds, so a wall-clock SQL
floor could reject a day the engine accepts. 0.9 s/day; ~59% of candidates flagged →
~3.3 h engine vs ~5.5 h base. Table `maxfader_whitelist` via `FF_CANDIDATE_TABLE`.

## Method

* **Record-first, `--base-run`.** SpikeFader's speed/eff stack is OFF: it was derived
  for a 20m-high / 9m-cover trade; this is a session-high / MOC trade. Ports are
  hypotheses (`feedback_side_flip_inverts_rulings`).
* **Universe gates kept identical to SpikeFader's base**: volat_20m ≥ 40bp, dv60 ≥ $100k,
  tc60 ≥ 60, barnum ≥ 22, entries 09:45–15:00 (12:00 on early closes), MOC 16:00 (13:00).
* Metric = PF−1 (`feedback: measure edge as PF−1`), raw (a short's return is +100%-bounded;
  MaxFlyerV2 doc), with the tail block on every table. Three controls before any gate is
  called a feature: year table (2026 first), same-n tightened incumbent + random floor,
  and mc=1 replay.
* ⚠ Stale defaults: pass `--db-path` and `--sec-dir` explicitly (Program.fs points at
  the pre-split path; the stale sec-dir yields "0 had a 1s tape" and 0 trips silently).

## Smoke (2021-03-01..05, 2,973 trips) — an anecdote, recorded before the run

Bare session-high short: PF−1 −0.032, worst −133.8%, 6.1% < −20%. The `brv15_tape`
ladder is monotone and **the tail vanishes up it**: ≥4 → PF−1 1.69 (n 708, worst −78%);
≥8 → **PF−1 24.6, win 86%, worst −5.6%, 0% < −20%** (n 219); ≥12 → win 94% (n 87).
`volhigh60` fires on 12.5% of session-high signals. Five days; the corpus decides.

---

## ⏭ The risk rule (user, 2026-09-03 evening) — to build after the base-run tables

MaxFlyerV2's stop-out / re-entry machinery is rejected ("ridiculous, doesn't fit my
style"). Instead: an **anchored VWAP on the entry**, and the trade runs regardless. At
**{1h, 2h, 3h} after entry**, if the AVWAP is **above the entry** (the short is losing on
a volume-weighted basis), the MOC order is **replaced with a 5m-low exit** — and that is
the entire rule. Then find the horizon at which holding to the close stops making sense.

Engine mapping: the AVWAP is the window difference of the running session-VWAP sums
(stamp `cumDv`/`cumVol` at entry; `(cumDv − dv₀)/(cumVol − vol₀)` at any later bar — no
new structure). New record-only marks: `avwap_{1h,2h,3h}` at entry+{3600,7200,10800}s
and, when that AVWAP > entry, the FIRST 300-bar-low breach after the check (px + sec).
The existing per-minute lo marks fire from entry, not from the check time, so this is a
small genuine addition; the horizon study is then post-hoc SQL on one run.
