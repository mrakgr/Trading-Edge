# DipRider — pullback-in-uptrend re-break (LONG intraday)

**System:** `TradingEdge.DipRider` (branch `dip-rider`), forked from `TradingEdge.VwapReclaim`.
**Doc discipline:** journal each finding here AS YOU GO (see `feedback_document_findings_as_you_go`).

## The pattern

The mirror image of the reversion systems (VwapReclaim / LowFlyer buy *weakness that reverts*).
DipRider buys **strength that pauses and resumes**: after an established intraday UPTREND, price pulls
back (closes below the 9-EMA for a stretch), then a **RE-BREAK** bar closes decisively above the prior
bar's high — buy the resumption. This is Qullamaggie's / Ross Cameron's / Tim Sykes' long dip-buy.

## The engine (v0 defaults — arbitrary, pre-tuning)

- **Universe:** `vwap_reclaim_candidate` (ADV ≥ $30M & rvol_0945 > 1; 52,630 ticker-days 2003-2026).
- **Entry** (long, fill at the re-break bar's close), all gates ON by default:
  - **Re-break:** `close ≥ prevBar.high · (1 + 0.5·ATR%)` (a half-ATR expansion over the prior high).
  - **Pullback:** ≥ 3 consecutive bars closed below the 9-EMA right before the re-break.
  - **Uptrend:** the re-break close ≥ 2% above the session open.
  - tightness ≥ 3; window 10:00–13:30 ET.
- **Exit** (user's spec): **exit-to-new-session-high** (the resumption ran), else a **15m time-stop**,
  else MOC. **Stop** = the re-break bar's low (close-based).

### The four handcrafted features (recorded in the CSV, not yet gates)

1. `bars_since_hi` — # bars since the session PRICE high (recency of the 1st push).
2. `bars_since_vol_hi` — # bars since the session max-1m-VOLUME high (recency of peak interest).
3. `bars_below_ema` — # consecutive bars closed below the 9-EMA before the re-break (pullback DEPTH).
4. `trend_pct` — re-break close / session open − 1 (how far up the session the trend had run).

Feature #1 vs #4 pins how STALE the first move is on both the price and volume axes; the gap between
them (price-recency vs volume-recency) says whether the second push is on fading or re-igniting volume.

---

## Findings

### Finding 1 — v0 harness works end-to-end (2026 H1 smoke test)

Bare defaults, 2026-01-01 .. 2026-06-25: **871 trips / 459 candidate-days / PF 1.037 / 40.1% win /
+$3.7k.** Thin (as expected pre-tuning), but the engine fires and the features populate sensibly:
- avg bars-since-price-high ≈ **81 min**, avg bars-since-vol-high ≈ **126 min** → the typical entry is
  a LATE second push, well after both the price and (especially) the volume peak. Volume is staler than
  price — the re-igniting-interest case the features were built to catch.
- avg pullback depth ≈ 8 bars below the 9-EMA; avg trend ≈ 21% up on the session (tail-heavy universe).
- **Exit mix: 8% new-high, 42% time-stop, ~49% stop, 0% MOC** — most re-breaks do NOT immediately make
  a fresh high; they chop into the 15m time-stop. ⇒ the edge (if any) is in SELECTION: which re-breaks
  resume. That's what the four features are for. NEXT = bucket-breakdown each feature vs ret_moc.

### Finding 2 — feature breakdown (2020-2026, 30,828 trips, base PF 0.92)

Raw modern-era book at v0 defaults = **PF 0.921 / 39% win / −$156k** (the raw signal LOSES). So any edge
must come from feature selection, not a lucky default. Bucket PF per feature:

- **`bars_below_ema` (pullback DEPTH) — the clean lever, MONOTONE:** shallower = better.
  `3-4 bars` PF **1.02** → `5-7` 0.95 → `8-14` 0.86 → `15-29` 0.80 → `30+` 0.52. Mechanistic: a SHALLOW
  pullback in an uptrend is a healthy pause that resumes; a deep/long one means the trend already broke
  and you're catching a dead-cat re-break. **Buy shallow dips, not broken trends.**
- **`bars_since_vol_hi` (volume recency) — strong but RARE:** re-break within `1-2 bars` of a fresh
  session volume high = PF **1.54** (52 trips), `3-4` = 1.23 (69). Everything ≥5 bars stale ≈ 0.9. The
  "volume re-igniting right into the re-break" case — the best micro-cell, just tiny.
- **`bars_since_hi` (price recency) — mild, non-monotone:** only the `60-119 bars` bucket clears 1.0
  (PF 1.05); a mid-recency sweet spot. Weak lever.
- **`trend_pct` — WEAK/flat:** 0.83–0.98 across 2%→40%+ up-on-session. The uptrend GATE earns its keep
  as a precondition, but the magnitude is not a selector. (Don't stack it.)
- **Interaction:** shallow-pullback and fresh-volume are ANTI-correlated (a shallow dip with fresh vol =
  re-break right off the high, barely a pullback → noise; that cell is PF 0.77). Keep them SEPARATE:
  shallow-alone = PF 1.03 (12.5k trips); fresh-vol-alone = 1.16 (184). No free stacking here.

**Honest read:** at v0 exit settings the best broad cell is only PF ~1.03 — NOT tradable yet. 42% of
trades die on the 15m time-stop, only 8% reach a new session high. The suspect is the EXIT, not the
entry (new-high target = a FULL session high may be too far; 15m stop too short to let a resumption
develop). NEXT = sweep the exit (time-stop 5-30m, new-high-vs-hold-to-MOC) before judging the entry.

### Finding 3 — the NEW-HIGH exit was capping winners (turn it OFF, hold longer)

The exit sweep (2020-2026, full 30,828-trip raw book):
- **new-high exit ON** (default), time-stop 5/10/20/30m → PF 0.96 / 0.92 / 0.93 / 0.96. All ~break-even;
  the time-stop length barely moves it.
- **new-high exit OFF** (hold to the time-stop / MOC), time-stop 20/30/60m → PF **1.063 / 1.119 / 1.186**.

Turning the new-high target OFF and letting the trade RUN flips the whole raw book positive, and it keeps
improving as the hold lengthens (60m > 30m > 20m). **The new-high target amputated the runners** — exactly
the VwapReclaim Finding 13 lesson (a fixed target caps a momentum-CONTINUATION edge). DipRider is a
continuation trade: buy the resumption and let it run, don't book it at the prior session high.
⇒ **new default: DipExitNewHigh should be OFF; the exit is a longer time-stop / hold-to-MOC.** (The stop
= re-break bar low stays.) NEXT (before productionizing): re-run the FEATURE breakdown under the run-to-MOC
exit — the shallow-pullback lever (F2) was measured under the winner-capping exit and should be re-judged;
and sweep the time-stop / MOC choice against the shallow-pullback cell now that the entry has an edge.

### Finding 4 — added a pullback-depth CAP (`DipMaxBarsBelowEma`, default 8)

Wired `--dip-max-bars-below-ema` (gate: reject re-breaks with ≥ N consecutive bars below the 9-EMA),
default 8, off with 0. Operationalizes F2's monotone "shallow resumes, deep = broken trend." Not yet
re-measured under the F3 run-to-MOC exit — that's the next entry-vs-exit pass.

### Finding 5 — 2-bar-low stop (user request): more dollars, close-based wins

Note: DipRider ALWAYS had a protective stop (the re-break bar's low). F3 turned off the new-high TARGET
and the time-stop, not the stop. Per user request, widened the stop to the **2-bar low** = `min(re-break
bar low, prior bar low)` — a self-calibrating structural stop under the resumption base (mirrors the
breakout engine's twoBar). 2020-2026:
- **re-break-bar low** (prev): PF 1.398 / 14.7% win / +$771k.
- **2-bar low, close-based** (new default): PF 1.375 / **18.1% win / +$866k.**
- 2-bar low, WICK-based (`--wick-stop`): PF 1.393 / 14.5% win / +$738k.

The 2-bar low is slightly WIDER (must also clear the prior bar's low) → tapped less on noise → win rate
14.7%→18.1% and net +$771k→+$866k, at a hair lower PF. **Close-based beats wick** (+$866k vs +$738k):
the wick stop gets shaken out by intrabar spikes that recover by the close. New default = 2-bar low,
close-based. (Trip count is unchanged at 17,997 — the stop only changes exits, not entries.)
