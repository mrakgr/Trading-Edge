# MaxRider → 1s bars — the port plan (prep, 2026-08-26)

⏭ Working title from the 2026-08-12 plan: **SpikeFader** (the 1s SHORT fader). Final name = user's call.

## What is being ported

`TradingEdge.MaxRiderV1` (1m bars, 1,026 lines, fork of DipRiderV6): intraday MR SHORT — fade the
pop. Production core **quiet-vol × ADX≥40 × not-session-high = PF 2.599**, all-weather (worst year
2.208), tail-safe (p1 −4.12), ~11k trips/yr. 🛑 Its universe (`diprider_v6_candidate` =
`mr_candidate` copy) carries the S39d lookaheads — **every number is INVALID pending a clean
rerun. The 1s port on `mr_candidate_1s_v2` IS that rerun.** Entry was close ≥ prior 20m max of 1m
closes; exit 7m-low cover or MOC; $10k/trip; dv_0945 ≥ $3M; ATR floor 0.004.

## ⭐⭐ The fork decision: start from the LongHiker v7 engine, not from MaxRiderV1

The v7 engine (TradingEdge.LongHiker) already IS most of this system:

| MaxRiderV1 lever | v7 engine equivalent | status |
|---|---|---|
| entry: close ≥ prior 20m max | the side-+1 sampler (new 1200-bar high, strictly prior) | ✅ built |
| quiet volume (F11/F17) | `dv_60/dv_1200` ratio + EWMA twins | ✅ recorded |
| not-session-high (F18) | `sess_hi` at signal | ✅ recorded |
| ADX (smoothness) | no ADX on 1s — analogs: `ols_r_*` (signed R), eff family, `volat_*` | ✅ recorded, mapping to VERIFY |
| pop size (F22) | pinned speed `signal_vwap/vwap_60_prev − 1` | ✅ recorded |
| counters (arm/reset) | `highs_20m_since_lo_*` reseat family | ✅ recorded |
| VWAP side (V6 F6) | `sess_vwap`, `vwap_{60,300,1200}` | ✅ recorded |
| exit: 7m-low cover | `LoBreak` marks exist at {1,2,5}m — add 420 to CHANS or re-sweep | small edit |
| MOC backstop | Flatten | ✅ built |
| universe + causal prices | `mr_candidate_1s_v2`, raw tape, barnum recorded | ✅ built |
| mc=1 | MaxConcurrent flag + the S33 replay discipline | ✅ built |

The 1m engine's only unique asset is its ADX computation; everything else is a strict subset.
**Recommended change list on the v7 engine:** (1) sampler already fires both sides — analysis
takes side +1 events SHORT (returns negated; the v7 convention anticipated this); (2) add a 7m
channel (CHANS +420) or sweep the existing lo marks per V6-F16; (3) optionally an ADX-on-slots
indicator if the OLS-R analog fails to reproduce F18's ordering.

## The carry-ins (all measured, none optional)

1. ⭐⭐ **The speed boundary (S35/S36b): >1-2%/min belongs to the FADER.** LongHiker owns below it;
   this system gates ABOVE it. The two systems partition the axis — measure the fade's edge as a
   function of speed FIRST, expect the mirror of the S36b table.
2. ⭐⭐ **Quiet volume is confirmed 3× on both sides** — expect it to carry here (it was already
   F17's core on 1m).
3. ⭐⭐ **Fixed timestops beat trails/stops** — and S36c showed every price-stop was DEAD on the
   short side specifically. Baseline exits: ts30/60/90/120 + the lo/hi marks for measurement.
   ⚠ But a short book still NEEDS a risk stop for the unbounded tail — design it and report the
   PF sacrifice honestly (V1 doc's own directive).
4. ⚠⚠ **Check 2026 FIRST** — five LongHiker feature sets were flat-to-negative in 2026; if the
   short fade is too, it is regime, not competition.
5. ⚠⚠ **Measure SPREADS early** (never done in the whole program; the reason S31's verdict was
   unarguable-but-unquantified). The passive-entry question (fade fills passively ≈ free,
   FlushFader result) decides tradability at these magnitudes.
6. ⭐ **Volume MAGNITUDE, not persistence** (2026-08-12 plan): the 1m fader loved LOW-volume
   spikes; gap counts were FlushFader's lever, not this family's. Test magnitude early.
7. Borrow / SSR / locate unmodelled (V1 doc's standing banner) — quiet-vol names are the
   borrowable end, which is why F11/F17 matter doubly.
8. V1's five open questions (counters asymmetry, VWAP side, chg_1d, ATR ceiling, exit window)
   re-tested from scratch — **do NOT assume the 1m findings mirror onto 1s.**

## Session-1 protocol

1. Verify the v7 engine still builds; add CHANS 420 + any missing config.
2. Smoke one month; check side +1 event counts vs the 1m system's ~11k trips/yr scale.
3. Full pass → study slice (10GB memory limit — the box has 15GB, one OOM already).
4. First tables: speed bands (expect the S36b mirror), quiet-vol × smoothness-analog ×
   not-sess-high (the F18 reproduction attempt), year table with 2026 first, mc=1 + eqw + trim
   throughout.
