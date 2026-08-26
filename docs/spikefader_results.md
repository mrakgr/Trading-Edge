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
