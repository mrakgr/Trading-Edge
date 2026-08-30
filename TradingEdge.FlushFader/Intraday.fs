module TradingEdge.FlushFader.Intraday

open System
open TradingEdge.RollingMa

// ===========================================================================
// FlushFader — 1-SECOND-bar intraday MEAN REVERSION (long-only): the
// production-tier successor to DipRiderV6 (1m), running on the SurgeRider
// engine. First of the Fader family (FlushFader long, SpikeFader short,
// MinFader/MaxFader hold-to-close). Design: docs/flushfader_results.md.
//
// THE SYSTEM (sampler form — record, don't gate):
//   ENTRY SIGNAL: the vwap prints a NEW ~20m LOW — strictly under the
//         STRICTLY-PRIOR EntryChannelBars-bar MIN (strict `<`: V6's F21
//         phantom-tie fix — `<=` re-fired on round-number pinning ties).
//         Hard floors: 60-bar dollar-volume AND trade count, inside the
//         entry window. mc = 0 averages down; every further new low is
//         another trip.
//   FILL: the NEXT present bar's vwap (the 1s dataset has no close column, and
//         the next-bar vwap is the honest "you traded into the following
//         second" fill). Exits fill the same way.
//   EXIT SIGNAL: the vwap strictly EXCEEDS the strictly-prior
//         ExitChannelBars-bar MAX (the ~5m-high reversion target; strict `>`
//         everywhere is the house rule after F21 — deliberate deviation from
//         V6's inclusive `>=`), or a PRICE-ACCEPTANCE STOP, or MOC.
//   ⭐ PRICE-ACCEPTANCE STOPS (user, 2026-07-28; Lance's framing): while
//         holding, a NEW entry-channel LOW made (a) on >= VolStopRatio 1m/20m
//         volume, (b) on >= TcStopRatio 1m/20m trade count, or (c) at a 1m
//         pace under SpeedStopPct — the market ACCEPTING the lower price;
//         the flush is continuing, not snapping. These are NOT price-level
//         stops: V6 F6 measured a level stop as DESTRUCTIVE for this shape
//         (PF 1.429 -> 1.164) — it fires exactly where MR buys. Acceptance
//         stops fire only on qualified FRESH lows. The S2 tables motivate
//         them: >=8x participation new lows run PF 0.59-0.87 at entry, and
//         sustained <-1% pace is the continuing-flush signature.
//   THE LEG MACHINE (LegCounters, RollingMa — DipRiderV6 lineage):
//         armed by the leg's FIRST new low; BarsSinceFirstLow counts every
//         bar, LowsSinceFirstLow counts further new lows (averaging-down
//         depth); ⭐ RESET on a new ENTRY-channel HIGH (the user's "reset at
//         the 20m high" — V6 reset on its exit window, which at 20/20
//         coincided; with a 5m exit that would end legs mid-flush, so the
//         reset is welded to the ENTRY channel here). Books are built
//         POST-HOC by greedy mc-replay (S38) — the old V6 F3 one-trip-per-leg
//         K-gate latch (MinLowsIntoLeg + legConsumed) is deleted.
//
// ⭐ SAMPLER, NOT A BOOK (mc = 0 default): every qualifying bar becomes an
// independent trip with its full feature vector + forward marks; PF on the raw
// output is ATTRIBUTION, not portfolio. Re-run chosen cells at mc=1.
//
// ⭐ PRESENT-BAR SEMANTICS (D1): the engine steps ONLY on bars that exist in
// data/intraday_1s_slim/ (seconds with >= 1 kept trade). Every window is a
// PRESENT-BAR-COUNT window — "60 bars" spans 60+ wall-clock seconds on any
// name with gaps. The GapCounter features (gap60/30/15 = missing seconds in
// the trailing wall-clock window) measure exactly what this convention hides;
// gap60 = 0 certifies present-bar ~= wall-clock locally.
//
// ⭐ THE REGIME BLOCK replaces V6's log_atr_20/adx_14 with the F1-F8
// bake-off lock (docs/surgerider_results.md):
//   30-present-bar slot vwaps (SlotVwapMa) -> r = ln(V/V_prev) -> EmaHlMa of
//   |r| at hl = 40 slots (volat_20m) + hl = 20 slots (volat_10m); rng_20m =
//   ln(high/low) of the 1200-bar vwap channel; eff_20m = ln(V/V_40ago) /
//   Sum40|r| ∈ [-1,1] (SIGNED drift t-stat; |eff| = trendiness — the ADX
//   replacement) + the 20-slot eff_10m twin.
//   ⚠ NAMING (user, 2026-07-28): volat_* = VOLATILITY; vol_* = VOLUME. The
//   old engines called both "vol".
//
// Live-safe: every feature folds from D's own realized bars; the entry window
// floor is 09:45 (etSec 35100), the knowability floor for the 09:45 universe
// context fields (docs/lookahead_protocol.md). Default start 10:00 (V6's
// research window).
// ===========================================================================

/// One 1-second present bar from data/intraday_1s_slim/, exactly as the tape
/// printed it. `etSec` = the `bucket` column: seconds since 00:00 ET (RTH open =
/// 34200, 09:45 = 35100, 16:00 = 57600). volume is FLOAT — the tape carries
/// genuine fractional shares.
///
/// ⭐ S43br: THESE ARE RAW. They used to arrive split-adjusted (`vwap = raw ×
/// adj_ratio`, `volume = raw / adj_ratio`), which meant every intraday price
/// carried a factor derived from FUTURE splits, and price and volume had to be
/// held on one scale by hand so that vwap·volume stayed honest dollars (the S29
/// fix, 2026-07-29 — before it, the DvFloor60 gate was future-split-dependent and
/// admitted 808 trips on inflation alone).
///
/// Raw needs none of that: same-day ratios cancel any common factor, and raw
/// price × raw volume is already real dollars. Cross-day context arrives
/// pre-converted into day D's raw scale on the Candidate. See
/// docs/price_adjustment.md.
type SecBar =
    { etSec: int
      vwap: float
      volume: float
      tradeCount: int }

/// Trip life-cycle. Exit SIGNALS are detected at a bar's close but FILL at the
/// next present bar's vwap — PendingExit carries the reason across that bar.
type IntraPosState =
    | Holding
    | PendingExit of reason: string
    | ExitedAt of exitSec: int * exitPx: float * reason: string
      // "target" | "vol_stop" | "tc_stop" | "speed_stop" | "moc"

/// One sampler trip. Features are the state at the SIGNAL bar's close
/// (inclusive of the signal bar — it has closed; not lookahead). The fill is
/// the NEXT present bar's vwap. ⭐ NOTHING here gates (beyond the hard entry
/// gates); it is all recorded for post-hoc SQL.
type FlushPosition =
    { SignalSec: int             // the gate bar (features captured here)
      SignalVwap: float          // its vwap — entry slippage = EntryPx/SignalVwap
      EntrySec: int              // the fill bar
      EntryPx: float             // the fill: next present bar's vwap
      // ----- the regime block (replaces V6's log_atr_20 / adx_14) -----
      Volat20m: float            // EmaHlMa hl=40 slots of |slot return| — THE volatility driver
      Volat10m: float            // hl=20 twin (trajectory: Volat10m << Volat20m = vol collapsing)
      Rng20m: float              // ln(high/low) of the 1200-bar vwap channel (F8 complement)
      Eff20m: float              // SIGNED ln(V/V_40slots_ago) / Sum40|r| ∈ [-1,1]. |eff| =
                                 // trendiness (drift t-stat — the ADX replacement); sign = the
                                 // 20m net direction. At a flush entry expect eff < 0.
                                 // ⭐ S38n: warms at 41 slots ≈ 1,230 bars — one slot AFTER the
                                 // entry channel; cold-at-signal now FAILS the spec (v1.6).
      Eff10m: float              // 20-slot twin (the 10m drift t-stat)
      SlotCount: int             // slot returns folded so far (volat/eff warmth)
      // ----- S40 (user 2026-08-01): slot-vwap RANGE twins of the eff pair.
      // Numerator = ln(hi/lo) over the SAME 41/21-slot-vwap span the eff returns
      // cover; eff_rng = that range over the SAME Σ|r| denominator. Direction-
      // blind; range >= |net| so eff_rng_* >= |eff_*|. nan until the span is
      // full (same warmth as eff_20m/eff_10m). Record-only. -----
      RngSlots20m: float         // ln(hi/lo) of the last 41 slot vwaps
      RngSlots10m: float         // 21-slot-vwap twin
      EffRng20m: float           // rng_slots_20m / Sum40|r| ∈ (0,1]
      EffRng10m: float           // rng_slots_10m / Sum20|r|
      // ----- ⭐ S43bd (user 2026-08-07): the LINEAR twins — both legs in PRICE
      // space: (H − L) / Σ|V_i − V_{i−1}| over the same slot vwaps. Triangle
      // inequality pins (0,1]; no log distortion on crash-sized moves; the
      // honest analog of HighFlyer's tightness. Record-only. -----
      EffRngLin20m: float        // (max41 − min41) / Sum40|ΔV|
      EffRngLin10m: float        // (max21 − min21) / Sum20|ΔV|
      // ----- ⭐ S43al (user 2026-08-05): the eff_9ema family. Same denominator as
      // eff_20m/eff_10m, but the NUMERATOR is a TREND-SIGNED sum of the very same
      // slot returns instead of the endpoint displacement ln(V_last/V_first):
      //   eff_9ema = Σ s_t·r_t / Σ|r_t|,  s_t = +1 if V_{t-1} > EMA9(V)_{t-1} else -1
      // Kaufman's eff sees only the two ends, so a V-shaped window and a straight
      // slide that finish in the same place read alike; this credits every leg that
      // agreed with the prevailing trend and debits every leg that fought it. Both
      // live in [-1,1] and share the warmth convention (40 / 20 returns). -----
      Eff9Ema20m: float
      Eff9Ema10m: float
      // ----- channel widths, ln(high/low) per present-bar window -----
      RngSess: float
      Rng600: float
      Rng300: float
      Rng120: float
      Rng60: float
      Rng30: float
      Hi60: float                // S40: the raw 60-bar vwap MAX (rng_60 can't recover it) —
                                 // dist-from-1m-high = signal_vwap/hi_60 - 1 (flush-speed study)
      // ----- bars since each channel's HIGH was last breached (-1 = never) -----
      BreachSess: int
      Breach1200: int
      Breach600: int
      Breach300: int
      Breach120: int
      Breach60: int
      Breach30: int
      // ----- bars since each channel's LOW was last breached (-1 = never).
      // breach_lo_sess = 0 reproduces V6's is_new_sess_low free-fall flag. -----
      BreachLoSess: int
      BreachLo1200: int
      BreachLo600: int
      BreachLo300: int
      BreachLo120: int
      BreachLo60: int
      BreachLo30: int
      // ----- ⭐ the down-leg counters (the point of V6), read at the signal
      // bar AFTER its OnEvent — the entry bar IS a low, so both >= 0. -----
      BarsSinceFirstLow: int     // the leg's age in present bars
      SecsSinceFirstLow: int     // ⭐ S43be: the SAME leg age in WALL-CLOCK seconds
                                 // (era-invariant; the bar count is not — see S43be)
      LowsSinceFirstLow: int     // averaging-down depth (0 = the leg's first low)
      // ⭐ S38e: same events, TIGHTER resets — leg disarmed by a new 5m/10m
      // high instead of the 20m one (record-only)
      BarsSinceFirstLow300: int
      LowsSinceFirstLow300: int
      BarsSinceFirstLow600: int
      LowsSinceFirstLow600: int
      // ⭐ S43cf (user, 2026-08-29): the SpikeFader S17 K-ladder mirrored back —
      // 2m/3m rungs (counts only) + the volat EWMA shorts for the ratio study +
      // the be window-difference speed cells (bar-clock EQ; long-side speed =
      // signal_vwap/x − 1 in SQL) + the S34c autocorr port. All record-only.
      LowsSinceFirstLow120: int
      LowsSinceFirstLow180: int
      Volat5m: float
      Volat3m: float
      VwapEwp12060Be: float
      VwapEwp12030Be: float
      VwapEwp6030Be: float
      Ac1Ewma: float
      Ac2Ewma: float
      Ac3Ewma: float
      // ⭐ S43cg (user, 2026-08-29): the vol-slope ladder extended — 5m/3m SMA
      // rungs (FloatOls 10/6, same shared |r| ring) + EWMA slope twins at all
      // four horizons (EwmaOlsMa, no hard warmup). SQL scale: ×2e4 = bp/min,
      // same as volat_slope_20m. Record-only.
      VolatSlope5m: float
      VolatR5m: float
      VolatSlope3m: float
      VolatR3m: float
      VolatEslope20m: float
      VolatEr20m: float
      VolatEslope10m: float
      VolatEr10m: float
      VolatEslope5m: float
      VolatEr5m: float
      VolatEslope3m: float
      VolatEr3m: float
      // ----- ⭐ S43bk (user, 2026-08-11): 1s TICK DIRECTION. A "tick" is the
      // sign of this present bar's vwap vs the PREVIOUS present bar's — the 1s
      // slim dataset has no close, so bar-vwap-to-bar-vwap IS the tick. Three
      // shapes, all record-only:
      //   (a) run counters — how long the tape has gone without an uptick;
      //   (b) windowed counts over {15,30,60,120} PRESENT bars — these are
      //       era-invariant BY CONSTRUCTION (denominator = the window size, so
      //       the count IS the fraction × N), unlike a leg-age bar count;
      //   (c) up counts alongside down, so `unchanged = N − up − dn` is
      //       recoverable — vwap ties are not rare on round-number pinning
      //       (the F21 phantom-tie lesson).
      DownticksSinceUptick: int  // down bars since the last UP bar; ties do NOT reset
      RunDownticks: int          // strict consecutive down run; ANY non-down bar resets
      SecsSinceLastUptick: int   // the era-invariant twin of DownticksSinceUptick.
                                 // -1 = no uptick yet this session.
      Dn15: int
      Dn30: int
      Dn60: int
      Dn120: int
      Up15: int
      Up30: int
      Up60: int
      Up120: int
      Gap120: int                // density companion for the 120 window (gap_15/30/60
                                 // already exist; 120 did not)
      // ⭐ S43bn: the tick counted over the LEG, anchored on the leg's first 20m
      // low (the same event as SecsSinceFirstLow) instead of on the last uptick.
      // -1 = no leg armed. Density = DownticksSinceFlow / (BarsSinceFirstLow+1).
      DownticksSinceFlow: int
      UpticksSinceFlow: int
      // ⭐ S43bo: NEW 20m LOWS since the last uptick (the signal bar is itself a
      // low, so this is >= 1 on every trip). Same events as LowsSinceFirstLow,
      // reset by a BOUNCE instead of by a new 20m high.
      LowsSinceUptick: int
      // ⭐ S43bp: the run's MAGNITUDE — vwap / (last uptick bar's vwap) - 1.
      // Negative during a down run. Pairs with BOTH DownticksSinceUptick and
      // LowsSinceUptick (same anchor); the per-tick and per-low rates are
      // this / the respective counter. nan = no uptick yet this session.
      ChgSinceLastUptick: float
      // ⭐ S43bq: the same measure anchored on the bar BEFORE the run's first
      // 20m low — the low sequence itself, inclusive of the drop that opened it.
      // Never identically 0 (unlike a first-low anchor). Together with
      // ChgSinceLastUptick this decomposes the run into "pre-sequence drift"
      // and "the sequence".
      ChgSinceRunPreLow: float
      /// ⭐ S43bq-b: the twin anchored on the first low ITSELF. 0 when the signal
      /// bar is that low; nan when no low has printed since the last uptick.
      ChgSinceRunFirstLow: float
      /// ⭐ S43bq-c: the downtick run's magnitude anchored on its FIRST DOWNTICK
      /// bar (the twin of ChgSinceLastUptick, which anchors on the uptick before
      /// it). 0 when the signal bar is that first downtick.
      ChgSinceRunFirstDn: float
      TradeIdx: int              // ⭐ index of this SIGNAL within the down-leg (0 = the leg's
                                 // first trade); reset by the new-high leg reset. Diverges from
                                 // LowsSinceFirstLow wherever a low fired no trade (outside the
                                 // window, floors failed, slots full, K-gate).
      OpenAtSignal: int          // trades already OPEN in the engine when this signal fired
                                 // (shared exits => concurrent trips exit together; the
                                 // mechanically honest add-index at mc=0)
      // ----- location levels at the signal -----
      Vwap1200: float            // the 20m ROLLING VWAP (dv_1200/vol_1200) — "the 20m MA"
      ChanHi: float              // the STRICTLY-PRIOR ENTRY-channel max at the signal:
                                 // depth-into-leg = ln(signal_vwap/chan_hi) <= 0
      ChanLo: float              // the STRICTLY-PRIOR ENTRY-channel min — the level this entry
                                 // broke: depth-below-break = ln(signal_vwap/chan_lo) < 0
      ExitChanHi: float          // the STRICTLY-PRIOR EXIT-channel max — the reversion target:
                                 // distance-to-target = ln(exit_chan_hi/signal_vwap) > 0
      // ----- the gap counts (what present-bar windows hide). S40l: also the
      // rank-0 Rényi member per window — bars + gaps = the window's calendar
      // span, support share = window/(window+gap). -----
      Gap60: int
      Gap30: int
      Gap15: int
      Gap300: int
      Gap600: int
      Gap1200: int
      // S40v: halt-vs-sparsity decomposition of the gap totals.
      MaxGapRun1200: float       // longest contiguous tradeless run (s) in the 20m window
      MaxGapRun300: float        // 5m twin
      BigGapRuns1200: float      // count of >= 60s runs in the 20m window
      // ⭐ S40y (user): the leg-anchored OLS pair — slope/r of ln(vwap) since the
      // last 20m high and since the leg's FIRST low. Growing windows (the leg's
      // own shape, not a fixed clock); nan below 3 points.
      OlsSlopeSinceHigh: float
      OlsRSinceHigh: float
      BarsSinceHigh: int         // the sinceHigh window's length (bars_since_first_low
                                 // is the sinceFlow length, already recorded)
      OlsSlopeSinceFlow: float
      OlsRSinceFlow: float
      // S40z (user): the anchored Kaufman eff twins (30-bar vwap slots built
      // internally, boundaries aligned to the anchor — sub-30s returns are
      // microstructure noise, F7).
      EffSinceHigh: float
      EffSinceFlow: float
      // ⭐ S43am (user): the eff_9ema twins of the two anchored segments —
      // high -> now and first-low -> now. Same slots as eff_since_high/flow.
      // SPANS RECORDED alongside (the S43aj/ak lesson), though this ratio is
      // structurally span-normalised where Kaufman's is not.
      Eff9SinceHigh: float
      Eff9SinceFlow: float
      // ⭐ S43ao (user): volume-weighted z of the signal price against each anchored
      // segment's own log-price distribution, and the entry-channel min as of 1m ago.
      ZSinceHigh: float
      ZSinceFlow: float
      ChanLoPrev: float
      // ⭐ S43ap (user): the 2m flush pair — the 20m low as of 120 bars ago, and
      // the 2m high. Twins of chan_lo_prev / hi_60 at double the horizon.
      ChanLoPrev120: float
      Hi120: float
      ChanLoPrev180: float
      Hi180: float
      SlotsSinceHigh: int
      SlotsSinceFlow: int
      // S41e (user): the arming drop — first leg low / 20m high at arming - 1
      // (the flush magnitude that started the leg) + the drop segment's OLS
      // slope/r (high -> first low). nan = channel-cold / short arming.
      DHiFlow: float
      OlsSlopeHiFlow: float
      OlsRHiFlow: float
      // ⭐ S43ai (user): the EFF twin of the drop segment. olsSinceHigh at the
      // arming bar spans exactly high -> first low, and effSinceHigh shares that
      // anchor — so this was a free snapshot that was simply never taken. It
      // completes the family: the drop was the only segment measured by OLS alone.
      EffHiFlow: float
      // ⭐ S43ai (user): eff_20m / eff_10m AS OF THE ARMING HIGH — the smoothness
      // of the trend INTO the high, which nothing else captured (olsSinceHigh /
      // effSinceHigh measure the drop OUT of it). Carried as MaxMaMeta payload
      // on the entry channel, snapshotted at the arming bar. nan = cold eff or
      // no leg.
      ArmHiEff20m: float
      ArmHiEff10m: float
      ArmHiSlots20m: int         // S43aj: slot SPAN each ratio was computed from
      ArmHiSlots10m: int         //   (40 / 20 = fully warm; less = partial; 0 = none)
      FirstLowVwap: float        // S41k: the leg's first-low anchor price (raw)
      BarsAboveSvwap: int        // S41o: present bars spent ABOVE the running session vwap
      BarsPresent: int           // its denominator (present bars this session)
      SessLow: float             // S41p: the raw session extremes (dist-from-session-low =
      SessHigh: float            // signal_vwap/sess_low - 1 = the GRADED above-low measure)
      // S41q: z-score raw moments (session + 5m/10m/20m; normal + log space).
      SessDv2: float
      SessDlv: float
      SessDlv2: float
      Dv2300: float
      Dlv300: float
      Dlv2300: float
      Dv2600: float
      Dlv600: float
      Dlv2600: float
      Dv21200: float
      Dlv1200: float
      Dlv21200: float
      // S40x: the halt-ADJUSTED gap family (halt intervals excluded) + detector state.
      GapAdj60: int
      GapAdj300: int
      GapAdj600: int
      GapAdj1200: int
      HaltsToday: int            // volatility halts classified so far this session
      Halts1200: int             // ⭐ S42r: classified halts inside the last 1200 PRESENT BARS
      Halts600: int              //    ... and the last 600 (bar-indexed twins of HaltsToday —
                                 //    the entry channel's own window, so the lookback stretches
                                 //    back THROUGH halt holes; the COUNT, not the seconds)
      SecsSinceHalt: int         // signal_sec − last reopen sec; −1 = no halt today
      // ----- location -----
      SessVwap: float
      DistSessVwap: float        // ln(vwap / session vwap)
      PctChgOpen: float          // vwap / first-RTH-bar vwap - 1
      // ----- raw activity levels (log twins = ln() in SQL) -----
      BarVol: float
      BarTc: int
      Vol5: float                // 5s/10s tails (exhaustion-fade contrast vs the 1m rate)
      Vol10: float
      Vol15: float
      Vol30: float
      Vol60: float
      Vol300: float              // S40l: the 5m window joins the vol/tc/dv family
      Vol600: float              // 10m volume sum (mid-horizon participation ratios)
      Vol1200: float             // 20m sums — the absolute 1m-vs-10m-vs-20m comparisons
      Tc5: float                 // 5s/10s tails, tc twins
      Tc10: float
      Tc15: float
      Tc30: float
      Tc60: float
      Tc300: float
      Tc600: float
      Tc1200: float
      Vol60Prev: float           // the PREVIOUS non-overlapping minute's sums (60-bar lag of the
      Tc60Prev: float            // 60-sums) — minute-over-minute activity SLOPE
      Vwap60: float              // ⭐ the CURRENT rolling 60-bar vwap (dv_60/vol_60)
      Vwap5Prev: float           // S42h: 5-bar vwap lagged 5 bars (5s flush speed denominator)
      Vwap10Prev: float          // S42h: 10-bar twin
      // ⭐ S43at (user): the 30s twins. `speed_30 = signal_vwap/vwap_30_prev - 1`
      // and `d_30s_hi = signal_vwap/hi_30 - 1`. Record-only.
      Vwap30Prev: float
      Hi30: float
      // ⭐ S43au: the CURRENT rolling vwaps at 30s/2m/3m (vwap_60 and vwap_1200
      // already existed). `d_vwap_N = signal_vwap/vwap_N - 1`.
      Vwap30: float
      Vwap120: float
      Vwap180: float
      Vwap60Prev: float          // ⭐ the rolling 60-bar vwap 60 bars ago (previous non-
                                 // overlapping minute) — flush speed = signal_vwap/vwap_60_prev-1
                                 // (user, 2026-07-28; replaces the noisy two-point vwap_60_ago)
      DollarVol60: float         // Sum60 of vwap*volume — the liquidity-floor value
      DollarVol300: float        // S40l: dv at every study window (the torrent axis
      DollarVol600: float        //   lived on dv/20m — now recorded at 5m/10m/20m
      DollarVol1200: float       //   for the dv-vs-N_eff disentangling)
      CumVol: float
      CumTc: float
      CumDv: float               // S40l: session Σ vwap·vol — pre-leg dv = cum_dv − dv_leg
      // ----- ⭐ S40l tier-2: leg-scoped participation (Σ since the 20m leg's
      // FIRST low, that bar inclusive; nan = no leg open — never at a signal,
      // the signal bar is a low) + the day-scoped virgin clock + the lagged
      // volat normalizer (S39q). -----
      VolLeg: float
      TcLeg: float
      DvLeg: float
      TargetsToday: int          // target exits FILLED before this bar (0 = virgin day)
      Volat20mPrev: float        // volat_20m as of 1200 present bars ago (nan while cold)
      // ----- S42k (user, 2026-08-03): OLS of |30s slot return| vs slot index
      // over the LAST 40 / 20 completed slot returns — the SELF-CONTAINED
      // vol-trend (expansion > 0, contraction < 0). Unlike vchg =
      // volat_20m/volat_20m_prev this needs no prior 20m window. Slope =
      // Δ|r| per slot (×2e4 ≈ bp of |r| per minute in SQL); r = Pearson
      // trend quality. nan below 3 returns in the window; partial windows
      // allowed (filter post-hoc on slot_count). Record-only. -----
      VolatSlope20m: float       // OLS slope over last min(slot_count,40) |r|
      VolatR20m: float           // its Pearson r
      VolatSlope10m: float       // 20-return twin
      VolatR10m: float           // its Pearson r
      Vol0945Tape: float         // S41g: Σ volume < 09:45 (dv_0945_tape / vol_0945_tape =
                                 // the first-15m tape vwap — the day-anchor AVWAP)
      Dv0945Tape: float          // ⭐ Σ vwap·volume over OUR 1s bars strictly before
                                 // 09:45 (honest dollars, v7 scale) — the live-scanner-
                                 // consistent dv_0945; compare vs the candidate column
      // ----- ⭐ S38q rolling OLS of ln(vwap) vs time, 600/1200 present bars.
      // slope = ln per present bar toward the present (×60 ≈ ln/min; ×6e5 ≈
      // bp/min); r = Pearson (r < 0 at a flush). nan on degenerate windows. -----
      // ⭐ S43ar (user): 1m / 2m / 3m OLS slopes on ln(vwap), same convention as
      // ols_slope_300 (multiply by 6e5 for bp/min). Record-only.
      OlsSlope60: float
      OlsR60: float
      OlsSlope120: float
      OlsR120: float
      OlsSlope180: float
      OlsR180: float
      OlsSlope300: float           // ⭐ S39p: the 5m twin (300 present bars)
      OlsR300: float
      OlsSlope600: float
      OlsR600: float
      OlsSlope1200: float
      OlsR1200: float
      // ----- ⭐ S39c rolling N_eff (record-only): Shannon perplexity + HHI of the
      // volume distribution over the last 600/1200 PRESENT bars — "how many
      // effective seconds carry the recent volume" (RollingMa NEff monoids in a
      // SlidingAgg; inverse-free). nan until the window is full. -----
      NEffShannon600: float
      NEffHhi600: float
      NEffShannon1200: float
      NEffHhi1200: float
      // S40l: the 1m/5m members (same present-bar convention; gap_60/gap_300
      // carry the rank-0 side of the family).
      NEffShannon60: float
      NEffHhi60: float
      NEffShannon300: float
      NEffHhi300: float
      // ----- ⭐ S39i (user): N_eff (Shannon) of |30s-slot log returns| — the SAME
      // stream the eff ratios consume — 40 slot-returns (20m) / 20 (10m): trend
      // SMOOTHNESS, the candidate replacement for the Kaufman eff ratios. High
      // (near window size) = movement spread evenly; low = gap-and-chop.
      // nan until the window is full (same warmth as eff_20m/eff_10m). -----
      NEffRet20m: float
      NEffRet10m: float
      // ----- forward marks (vwap at the first present bar >= entry + horizon; nan if the day ends first) -----
      FwdVwap60: float
      FwdVwap300: float
      FwdVwap600: float
      FwdVwap1200: float
      // ----- ⭐ AUX-HIGH marks, retargeted for MR: the post-hoc EXIT-WINDOW SWEEP.
      // The first NEW {120,300,600,1200}-present-bar HIGH made STRICTLY AFTER the entry
      // fill bar, MARKED AT THE FOLLOWING BAR's vwap (the fill discipline). Detection is
      // the counter formulation: at the current bar, look up the PREVIOUS bar's
      // breach-counter snapshot — if the mark is still nan and that snapshot's
      // bars-since-high is 0 (the previous bar printed the high), the mark fills at THIS
      // bar's vwap. px = nan / sec = -1 until hit. Prototype "exit at the N-bar high"
      // books in SQL without re-running:
      //   ret = if aux_sec <= exit_sec then aux_px/entry-1 else ret_exit.
      // aux_hi_300 ~= the real exit at the defaults (both strict `>`; aux additionally
      // requires the high strictly after the fill bar) — near-agreement is a smoke test.
      // Tracked until retirement (>= +20m after entry), like the forward marks. -----
      // ⭐ S43bw (user 2026-08-14): the 60-bar (~1m) rung, added so TIGHTENING the
      // exit target is answerable post-hoc instead of by re-running. The sweep
      // previously bottomed out at 120 (~2m), which is the wrong side of the
      // production 300 (~5m) to test "take the reversion sooner".
      AuxHi60: float
      AuxSec60: int
      AuxHi120: float
      AuxSec120: int
      AuxHi300: float
      AuxSec300: int
      AuxHi600: float
      AuxSec600: int
      AuxHi1200: float
      AuxSec1200: int
      // ⭐ MA-EXIT MARKS (user, 2026-07-29): first STRICT cross of vwap above the
      // strictly-prior {10,20,30,40,50,60}m mean after the fill bar, filled at the
      // NEXT present bar (aux discipline) — the counterfactual "exit at reversion
      // to the mean" sweep. ma_* = simple mean of bar vwaps; vwma_* = Σ(vwap·vol)/
      // Σvol. Both PARTIAL-TOLERANT (early-session window = session-so-far mean).
      // Unresolved marks fill at the MOC bar / day-end (sec >= MocSec = the moc
      // fallback, distinguishable post-hoc).
      Ma10Px: float
      Ma10Sec: int
      Ma20Px: float
      Ma20Sec: int
      Ma30Px: float
      Ma30Sec: int
      Ma40Px: float
      Ma40Sec: int
      Ma50Px: float
      Ma50Sec: int
      Ma60Px: float
      Ma60Sec: int
      Vwma10Px: float
      Vwma10Sec: int
      Vwma20Px: float
      Vwma20Sec: int
      Vwma30Px: float
      Vwma30Sec: int
      Vwma40Px: float
      Vwma40Sec: int
      Vwma50Px: float
      Vwma50Sec: int
      Vwma60Px: float
      Vwma60Sec: int
      // ----- exit -----
      BarsHeld: int              // present bars from the fill bar to the exit-fill bar
      State: IntraPosState }

/// ⭐ RIGHT-SIDE-OF-V CONTINUATION (user, 2026-07-29 — Lance Breitstein's concept).
/// Parent = a SPEC v1.2 reversal trip. After the parent's ENTRY FILL, the first
/// STRICT break above the prior {60,120,300}-bar MAX — the breach printing strictly
/// after the fill bar (aux-mark discipline) — is the continuation signal for that
/// window; it fills at the NEXT present bar's vwap. One continuation per
/// (parent, window). Each row carries THREE independent counterfactual trailing
/// stops: exit when vwap breaks STRICTLY below the prior {60,120,300}-bar rolling
/// MIN (ratchets up as the low rises; fill next bar), MOC backstop, NO profit
/// target — the right side runs. 3 entries x 3 stops = 9 counterfactuals/parent.
// (right-side-of-V ContPosition machinery DELETED — S39g: the continuation study
// closed at S26 (taker-fee-dead, left side wins) and the 9-counterfactual
// tracking was pure per-bar drag on the sampler.)

/// FlushFader config. Hard gates only — every other lever is a recorded column.
type IntradayConfig =
    { EntryChannelBars: int      // ⭐ ENTRY: vwap < the prior N-present-bar MIN of vwaps (strict).
                                 // Default 1200 (~20m on a fully-active name). The channel must be
                                 // WARM (N bars folded) — a partial-window "low" is not a flush.
                                 // Also the leg-reset channel: a new N-bar HIGH ends the down-leg.
      ExitChannelBars: int       // ⭐ EXIT: vwap > the prior N-bar MAX (strict) -> target. Default
                                 // 300 (~5m) — V6 F16's direction. NO other exit before MOC.
      // ⭐ S43bw (user 2026-08-14): a SECOND, TIGHTER exit channel that engages only
      // after AfterHoursSec. The motive is that every channel here counts PRESENT
      // BARS, not wall-clock: post-market tape is sparse, so a 300-bar "5m high"
      // can span hours after 16:00 (measured: 55 of 181 unresolved trips never made
      // one even by 20:00). A 60-bar rung is reachable on thin tape.
      // 0 = OFF (use ExitChannelBars all session) — the default, so this is inert
      // until asked for. Must be one of {30,60,120,300,600,1200}.
      // ⚠ Only reachable when MocSec > AfterHoursSec; at the production MocSec
      // (57600 = AfterHoursSec) there are no bars past the boundary to act on.
      ExitChannelBarsAfterHours: int
      AfterHoursSec: int         // 57600 = 16:00. Where the tighter channel takes over.
      DvFloor60: float           // hard gate: Sum60(vwap*volume) >= this at the signal bar. $ terms.
      TcFloor60: float           // hard gate: Sum60(tradeCount) >= this.
      // ⭐ VOLATILITY BAND — RECORD-FIRST for MR: the breakout F10 calibration does NOT
      // transfer (THE INVERSION: every momentum gate flipped in MR territory). Both
      // default OFF; volat_20m is a recorded column for post-hoc banding. A signal with
      // the volat feature still cold FAILS a positive floor, like V6's atrOk.
      MinVolat20m: float
      MaxVolat20m: float
      // ⭐ |eff_20m| floor — same record-first stance (V6's adx analog; keep 0). A signal
      // with eff still cold FAILS a positive floor.
      // (MinAbsEff20m DELETED, S40i: fully superseded — AbsEff20Lo IS the abs floor.)
      // ⭐ SPEC v1.2 GATES (baked 2026-07-29) — the S18 production stack as entry
      // conditions, formulas IDENTICAL to the recorded columns. Each individually
      // disable-able; a cold feature FAILS an armed gate (volatOk stance).
      MaxSpeed1m: float          // vwap/vwap_60_prev - 1 < this (flush speed). Default -0.02. 0 = off.
      KBandLo: int               // lows_since_first_low >= this. Default 26. <= 0 = off.
      KBandHi: int               // lows_since_first_low <= this. Default 50. <= 0 = off.
      AbsEff20Lo: float          // ⭐ SPEC v2.0 REDESIGN (user, S40i): |eff_20m| >= this. Default 0.3.
      AbsEff20Hi: float          // |eff_20m| < this. Default 0.5. lo <= 0 / hi = +Infinity = off.
                                 // One |·| convention for both eff measures (mirrors |eff10|);
                                 // ≡ the old signed band [-0.5,-0.3) to within ~30 trips — 99.9%
                                 // of the residual universe has eff_20m < 0 (S40e). Unwarm eff
                                 // fails the band like any other gate (S38n).
      MinAbsEff10m: float        // |eff_10m| >= this. Default 0.15. 0 = off.
      // ⭐ SPEC v2.7 (user, S43al): eff_9ema_10m >= this. Default -0.10 — the
      // WHIPSAW knife. Below -0.10 the 20 slot-returns fought the 9-slot EMA
      // more than they agreed with it: 211 trips of 7,789 at PF 1.23 / +0.35%,
      // with a NEGATIVE expectancy ratio in 2020/2024/2025. The adjacent
      // [-0.10, 0) band is the OPPOSITE — PF 8.06 — which is why the knife sits
      // at -0.10 and NOT at 0 (a >= 0 cut LOWERS mc=3 PF, 3.899 -> 3.846).
      // ⚠ THE THRESHOLD IS NEGATIVE, so 0 is a LIVE bound, not a sentinel.
      // Off = NEGATIVE INFINITY only — the S43aj `--abs-eff20-hi 0` trap in
      // mirror image. Verify from the BANNER, never from the flag.
      MinEff9Ema10m: float
      // ⭐ SPEC v2.2 (user, S41c/d): THE LEG-NATIVE PAIR replaces {dvw < -5%,
      // d20m-high < -10%} — those two were ONE feature counted twice (corr
      // 0.946); ssf (leg SPEED) x dlv (leg STRETCH) are corr 0.075 = two real
      // dimensions. Beats the old pair at both mc levels (+0.066 mc=0 /
      // +0.051 mc=1 for -8.6% trips). Unwarm slope / leg vwap FAILS an armed
      // gate (at signal a leg always exists; defense only).
      SsfLoBpm: float            // slope_since_flow x 6e5 >= this bp/min (no vertical crash;
                                 // < -400 = 0.58, [-400,-375) = 0.63). Default -375 (user
                                 // tightened from -400). -Infinity = off.
      SsfHiBpm: float            // slope_since_flow x 6e5 < this bp/min (no shallow drift;
                                 // [-25,0) = 1.96, >= 0 = 0.97). Default -25. >= 0 = off.
      MaxDistLegVwap: float      // vwap / (dv_leg/vol_leg) - 1 < this — stretched below the
                                 // leg's OWN vwap (the [-3,0) shallow slice = 1.6). Default
                                 // -0.03. >= 0 = off.
      MinVol10Rate: float        // (vol_10/10)/(vol_60/60) >= this (S17/S18 last-10s floor). Default 0.75. 0 = off.
      MinLows180: int            // ⭐ SPEC v3.0 (S43cl, user 2026-08-30): lows_since_first_low_180
                                 // >= this — the 3m freshness floor (net-FREE at 3: the mc=1 slots
                                 // recycle; "never buy the first dip"). Default 3. 0 = off.
      MinLows300: int            // ⭐ SPEC v1.4 (S38h): lows_since_first_low_300 >= this — kills the
                                 // FAST-CHASE re-entry (5m bounce without a 20m leg reset leaves the
                                 // K-band satisfied, re-signaling in seconds; that slice = PF 0.11 on
                                 // the A++ cell). Default 6. 0 = off.
      MaxRngFront: float         // ⭐ SPEC v1.5 (S38k): rng_300/rng_20m < this — reject the PURE
                                 // CLIFF (the whole 20m range printed in the last 5m; monotone-worst
                                 // at mc=1, 1.51/64.7). Degenerate/cold ranges FAIL (mirrors SQL
                                 // nullif). Default 0.8. Infinity = off.
      MinAccel1020Bpm: float     // ⭐ SPEC v1.7 (S39o/r): (slope_10m − slope_20m)×6e5 >= this bp/min —
                                 // reject the LATE-ACCELERATING decline (the [−150,−80) bleed band:
                                 // 1.09/1.32/1.28 across three buckets; the RAW axis carries the
                                 // signal, vol-normalizing washes it out, S39q). Default −80.
                                 // −Infinity = off. Cold OLS FAILS an armed gate.
      MaxSlope20Bpm: float       // ⭐ SPEC v1.7 (user, S39m): slope_20m×6e5 < this bp/min — the
                                 // L-SHAPE INSURANCE (flat 20m slope + dist ≤ −10% ⇒ tail-compressed
                                 // late cliff; the 64-trip 0.438/43.8% sliver). Default −10.
                                 // >= 0 = off. Cold OLS FAILS an armed gate.
      MinSlope5Bpm: float        // ⭐ SPEC v1.7 (user, S39s): slope_5m×6e5 >= this bp/min — reject the
                                 // VERTICAL last-5m collapse (under the other v1.7 gates the < −400
                                 // slice = 66 trips @ 0.706/36.4%, four years with ZERO winners).
                                 // Default −400. −Infinity = off. Cold OLS FAILS an armed gate.
      MinDv0945Tape: float       // ⭐ tape-native dv_0945 floor (Σ vwap·vol, 1s bars < 09:45).
                                 // Default 0 = RECORD-FIRST; the live-scanner-consistent
                                 // replacement for the candidate-table dv_0945 gate.
      MaxDist1mHi: float         // ⭐ SPEC v1.9 (user, S40g): vwap/hi_60 - 1 < this — the 1m-leg
                                 // CONJUNCTION with the speed gate (fast last minute AND a real
                                 // leg below the 1m high; the shallow slice above −2% = 2,261
                                 // trips @ ~1.6, slot thieves — first both-mc-levels winner).
                                 // Default −0.02. >= 0 = off. Same post-push max60 as hi_60.
      // ⭐ S40x HALT DETECTOR (user design, 2026-08-01): a tradeless run counts
      // as a HALT — and is EXCLUDED from the gap_adj_* counters — iff, at the
      // last bar BEFORE the hole: run >= HaltMinRunSec, 5m range (pre-hole,
      // ln hi/lo) >= HaltMinRng300, and the ADJUSTED 1m gap < HaltMaxPreGap60
      // (tape continuous right up to the stop; the adjusted counter makes
      // back-to-back halt chains classify by recursion). Causal/live-buildable.
      // Detects VOLATILITY halts only — news-pending (T1) halts have a quiet
      // pre-range and stay in the counters as ordinary gaps (we don't fade
      // news; sentiment-on-no-news is the MR edge — user).
      HaltMinRunSec: int         // default 58 (LULD pause = 5min nominal; jitter tolerance)
      HaltMinRng300: float       // default 0.04 (the LULD trigger state, pre-hole)
      HaltMaxPreGap60: int       // default 2 (< 2 missing seconds in the pre-halt minute)
      MaxZ20m: float             // ⭐ SPEC v2.3 (user, S41r): the 20m vw-sigma z (LOG space) < this —
                                 // z = (ln vwap - L)/sigma_L on the 1200-bar window's own volume-
                                 // weighted ln-price moments. Trims the WEAK DIP ([-1.5,-1) = 1.67
                                 // full / 1.80 g60 — the flush that barely dents its own 20m
                                 // dispersion; monotone on clean tape). Default -1.5. >= 0 = off.
                                 // Cold/degenerate sigma FAILS an armed gate. (log vs normal =
                                 // 0.997 — convention only.)
      MinRSinceFlow: float       // ⭐ SPEC v2.1 (user, S40y): r_since_flow >= this — reject the
                                 // PERFECT-LINE flush (the leg one clean regression line since
                                 // its first low = a drift, not a capitulation; the falling-knife
                                 // quantifier: < -0.95 = 1.22 on 406). Default -0.95. <= -1 = off.
                                 // Unwarm (leg < 3 bars) fails an armed gate.
      CascadeHaltCount: int      // ⭐ SPEC v2.4 (user, S42n): the CASCADE-KNIFE gate — reject a
                                 // signal iff haltsToday >= this AND secs since last resume <
                                 // CascadeWindowSec. The halt NUMBER matters ONLY in the
                                 // immediate aftermath (<20m: ht=3 = 1.25 / ht>=4 = 2.09 w/
                                 // 2022 0.08; by [20,80m) every tier reverts fine). Both
                                 // counters are causal running state. Default 3. 0 = off.
      CascadeWindowSec: int      // the post-resume window (default 1200 = 20m)
      ReopenBlockSec: int        // ⭐ SPEC v2.5 (user, S42t): no fade within this many
                                 // seconds of ANY resume, the first 1-2 halts INCLUDED.
                                 // Rationale (S42p): [2,5m) post-resume is where the next
                                 // LULD trigger decides itself — a first-halt name still
                                 // printing new lows there RE-HALTS (that state is
                                 // structurally EMPTY for ht=1), so a sub-2m entry is
                                 // taken blind to a cascade test the tape is about to
                                 // run. Default 120. 0 = off. Subsumes the ht>=3 case.
      // ⭐ PRICE-ACCEPTANCE STOPS (user, 2026-07-28): while holding, exit if a NEW
      // entry-channel low prints on (vol_60/60)/(vol_1200/1200) >= VolStopRatio, or
      // (tc_60/60)/(tc_1200/1200) >= TcStopRatio, or at vwap/vwap_60_prev - 1 <
      // SpeedStopPct. Ratios: +infinity = off. Speed: 0 = off (a 0% speed stop would
      // fire on every low). Both windows must be warm. Fill: next present bar.
      VolStopRatio: float
      TcStopRatio: float
      SpeedStopPct: float
      MaxConcurrent: int         // 0 = unlimited (THE SAMPLER DEFAULT — averages down). 1 = a real book.
      SlotBars: int              // the slot clock: 30 present bars (F5c: 30-40s flat, 30 stands).
      SessionStartSec: int       // 34200 = 09:30 — features fold from RTH open.
      EntryStartSec: int         // 36000 = 10:00 (V6's research window). ⚠ KNOWABILITY FLOOR 35100
                                 // (09:45) — the universe context (dv_0945 / rvol_0945_honest)
                                 // rides along in the output; entries before 09:45 would silently
                                 // make those columns lookahead (R4).
      EntryEndSec: int           // 54000 = 15:00 — one hour before the regular close.
      EntryEndSecShort: int      // ⭐ S43bc: entry cutoff on NYSE early-close days (13:00 ET
                                 // close). 43200 = 12:00 — the hour-before-close rule mirrored.
                                 // Without it EntryEndSec never engages on short days and the
                                 // engine enters minutes before the short close and force-MOCs
                                 // immediately (RGC 2025-07-03: entry 12:53, MOC 12:58).
      MocSec: int                // 57600 = 16:00. Positions stop trading at the first bar >= this
                                 // and exit at the NEXT SESSION'S OPEN (S43bw), and Flatten catches
                                 // days whose tape ends earlier.
      // ⭐⭐ S43bx (user 2026-08-14): THE CLOSE IS CALENDAR-AWARE, exactly as the entry
      // cutoff already was. 46800 = 13:00 on NYSE early-close days.
      //
      // This became load-bearing when the 1s corpus was extended to midnight: a flat
      // 57600 made the engine read ~3 HOURS OF POST-CLOSE TAPE as if it were RTH on
      // half-days. Measured over 9,759 early-close candidate ticker-days vs matched
      // neighbouring regular days:
      //     traded seconds in [13:00,16:00)   median   40 of 10,800  (0.4%)
      //     same window on a REGULAR day      median 3,326 of 10,800 (30.8%)
      //     per-second rate vs that day's own morning:  0.011  vs  1.009
      // ⭐ A 5m (300-bar) high CANNOT FORM on 88.2% of those ticker-days — there are
      // not 300 traded seconds in the whole window — so the exit rule is not merely
      // impaired there, it is undefined. Dollar volume looks respectable (24% of the
      // morning's) only because 62% of it lands in the FIRST 60 SECONDS: the closing
      // auction and its late-disseminating prints, which a continuous-tape rule
      // cannot participate in.
      // ⚠ Drifting: 2016-19 saw 0-6% of ticker-days reach 300 bars, 2024-25 sees
      // 15-18%. Still far too thin to trade, but re-measure rather than assume.
      MocSecShort: int }

/// The FlushFader engine. One instance per (ticker, day).
type IntradaySystem(cfg: IntradayConfig, ticker: string, day: DateOnly) =
    // S43bc: short days use their own entry cutoff (see EntryEndSecShort doc).
    let isEarlyClose = TradingEdge.Orb.Timezone.early_closes.Contains day
    let entryEndSec = if isEarlyClose then cfg.EntryEndSecShort else cfg.EntryEndSec
    // S43bx: ...and their own CLOSE (see MocSecShort doc).
    let mocSec = if isEarlyClose then cfg.MocSecShort else cfg.MocSec
    // ----- the entry/exit channels + the recorded channel set -----
    // MaxMa/MinMa pairs over vwap at six present-bar windows; session extremes
    // via RunMaxMa/RunMinMa. Entry/exit windows are validated in Program to
    // alias these.
    let max30 = MaxMa 30
    let max60 = MaxMa 60
    let max120 = MaxMa 120
    // ⭐ S43ap (user): the 3m high — "as far as we'd want to go for quick flushes".
    // Record-only; no channel/gate reads it, so no snapshot pair is needed.
    let max180 = MaxMa 180
    let max300 = MaxMa 300
    let max600 = MaxMa 600
    let max1200 = MaxMa 1200
    let min30 = MinMa 30
    let min60 = MinMa 60
    let min120 = MinMa 120
    let min300 = MinMa 300
    let min600 = MinMa 600
    let min1200 = MinMa 1200
    let sessHigh = RunMaxMa<float>()
    let sessLow = RunMinMa<float>()
    let chanMax n : MaxMa =
        match n with
        | 30 -> max30 | 60 -> max60 | 120 -> max120 | 300 -> max300 | 600 -> max600 | 1200 -> max1200
        | _ -> invalidArg "n" $"no {n}-bar channel"
    let chanMin n : MinMa =
        match n with
        | 30 -> min30 | 60 -> min60 | 120 -> min120 | 300 -> min300 | 600 -> min600 | 1200 -> min1200
        | _ -> invalidArg "n" $"no {n}-bar channel"
    let entryMin = chanMin cfg.EntryChannelBars     // ⭐ the flush trigger side
    let entryMax = chanMax cfg.EntryChannelBars     // ⭐ the leg-reset side (+ chan_hi)
    // ⭐ S43ai: a metadata-carrying TWIN of entryMax, same window, fed the same
    // vwap once per present bar. Runs PARALLEL to entryMax rather than replacing
    // it so the signal path is provably untouched (parity by construction); the
    // extra deque is amortized O(1). Payload = (eff_20m, eff_10m) at that bar.
    let entryMaxMeta = MaxMaMeta<struct (float * float * int * int)> cfg.EntryChannelBars
    let exitMax = chanMax cfg.ExitChannelBars       // ⭐ the reversion target
    // ----- breach counters, both sides per window -----
    let brSess = BreachCounter()
    let br30 = BreachCounter()
    let br60 = BreachCounter()
    let br120 = BreachCounter()
    let br180 = BreachCounter()                  // S43cf: 3m rung for the K-ladder resets
    let br300 = BreachCounter()
    let br600 = BreachCounter()
    let br1200 = BreachCounter()
    let brLoSess = BreachCounter()
    let brLo30 = BreachCounter()
    let brLo60 = BreachCounter()
    let brLo120 = BreachCounter()
    let brLo300 = BreachCounter()
    let brLo600 = BreachCounter()
    let brLo1200 = BreachCounter()
    // ⭐ the leg machine + the per-leg trade counter (index of each SIGNAL
    // within the down-leg; reset together with the counters on the new
    // entry-channel high — NOT on new lows, unlike the momentum engines).
    let counters = LegCounters()
    // ⭐ S38e (user): the SAME new-low event counted under TIGHTER leg resets —
    // disarmed by a new 5m/10m high instead of the 20m one. A 20m-high breach
    // implies a 5m/10m breach (nested windows), so these never outlive `counters`.
    // RECORD-ONLY: no gate reads them.
    let counters300 = LegCounters()
    let counters600 = LegCounters()
    // ⭐ S43cf (user): the SpikeFader S17 K-ladder mirrored back — the same
    // new-low event under 2m/3m resets (br120 exists; br180 added for this).
    // RECORD-ONLY, counts only (lows_since_first_low_{120,180}).
    let counters120 = LegCounters()
    let counters180 = LegCounters()
    // ⭐ S38q OLS trend features (record-only): RollingMa's OlsSlopeMa on
    // ln(vwap) per present bar; signed r = sign(slope)·√R²
    let ols300 = OlsSlopeMa 300                  // ⭐ S39p (user): the 5m slope — the missing
                                                 // scale for DIRECT recent-acceleration
                                                 // (slope_5m − slope_10m), replacing the
                                                 // rngfront proxy for "late cliff"
    // ⭐ S43ar (user): the SHORT-HORIZON slope family. The existing OLS ladder
    // starts at 5m (ols300); nothing measured the 1-3m trend directly, which is
    // exactly the horizon the deep-flush measures live on.
    let ols60 = OlsSlopeMa 60
    let ols120 = OlsSlopeMa 120
    let ols180 = OlsSlopeMa 180
    let ols600 = OlsSlopeMa 600
    let ols1200 = OlsSlopeMa 1200
    // ⭐ S39c rolling N_eff over 1s-bar volume, 600/1200 present bars (record-only):
    // one SlidingAgg per window carrying the (HHI, Shannon) accumulators as a
    // product monoid. SlidingAgg is size-agnostic — the counts live here and the
    // caller pops once the window is full (push-then-pop keeps it exactly at size).
    let neffCombine (struct (h1: NEffHhi, s1: NEffShannon)) (struct (h2: NEffHhi, s2: NEffShannon)) =
        struct (h1.Merge h2, s1.Merge s2)
    let neffZero = struct (NEffHhi.Zero, NEffShannon.Zero)
    let neffLift (v: float) = struct (NEffHhi.Zero.Add v, NEffShannon.Zero.Add v)
    let neff600 = SlidingAgg<struct (NEffHhi * NEffShannon)>(neffZero, neffCombine)
    let neff1200 = SlidingAgg<struct (NEffHhi * NEffShannon)>(neffZero, neffCombine)
    let mutable neff600Count = 0
    let mutable neff1200Count = 0
    // ⭐ S40l (user): the 1m/5m members of the Rényi family — gap counters carry
    // rank 0 (support/time), these carry ranks 1-2 on the SAME present-bar
    // window convention as neff600/1200 (bars + gaps = calendar time post-hoc).
    let neff60 = SlidingAgg<struct (NEffHhi * NEffShannon)>(neffZero, neffCombine)
    let neff300 = SlidingAgg<struct (NEffHhi * NEffShannon)>(neffZero, neffCombine)
    let mutable neff60Count = 0
    let mutable neff300Count = 0
    // ⭐ S39i (user): N_eff (Shannon) of |30s-slot log returns| — trend SMOOTHNESS
    // on the SAME stream as the eff ratios (the |r| pushed into slotAbsSum/
    // slotAbsSum20): 40 slot-returns = 20m, 20 = 10m. A smooth slide spreads its
    // movement over many effective slots (high N_eff, near the window size); a
    // gap-and-chop tape concentrates it (low). Scale-invariant (entropy of
    // shares), direction-blind — the candidate replacement for the Kaufman eff
    // ratios. Shannon only (HHI = 0.965-correlated duplicate, S39f). Zero-|r|
    // slots occupy a window slot but add no mass (Add skips v <= 0), exactly
    // mirroring slotAbsSum's SumMa semantics.
    let neffRet40 = SlidingAgg<NEffShannon>(NEffShannon.Zero, fun a b -> a.Merge b)
    let neffRet20 = SlidingAgg<NEffShannon>(NEffShannon.Zero, fun a b -> a.Merge b)
    let mutable neffRet40Count = 0
    let mutable neffRet20Count = 0
    let mutable tradeIdx = 0
    // ----- activity sums + lags -----
    let volSum300 = SumMa 300                    // S40l: the 5m window joins the family
    let tcSum300 = SumMa 300
    let dvSum300 = SumMa 300
    let volSum5 = SumMa 5                        // 5s/10s tails (user, 2026-07-29)
    let volSum10 = SumMa 10
    // ⭐ S42h (user): 5s/10s flush speeds — dv twins + non-overlapping lags,
    // mirroring the vwap_60_prev construction. Record-only.
    let dvSum5 = SumMa 5
    let dvSum10 = SumMa 10
    let vwap5Lag = LagMa<float> 5
    let vwap10Lag = LagMa<float> 10
    let volSum15 = SumMa 15
    let volSum30 = SumMa 30
    let volSum60 = SumMa 60
    let volSum600 = SumMa 600                    // 10m volume (user, 2026-07-28)
    let volSum1200 = SumMa 1200
    let tcSum5 = SumMa 5
    let tcSum10 = SumMa 10
    let tcSum15 = SumMa 15
    let tcSum30 = SumMa 30
    let tcSum60 = SumMa 60
    let tcSum600 = SumMa 600
    let tcSum1200 = SumMa 1200
    let vol60Lag = LagMa<float> 60               // the previous minute's 60-sum (slope features)
    let tc60Lag = LagMa<float> 60
    let vwap60Lag = LagMa<float> 60              // ⭐ the rolling 60-bar vwap, lagged one minute
    // ⭐ S43at (user): the 30-SECOND twins of `speed` and `d_1m_hi`. The 1m family
    // saturates at corr ~0.91 with each other; the question is whether HALVING the
    // horizon separates the way TRIPLING it did (s1m won as a sizing lever at the
    // 1m scale, and the z geometry showed the discriminating action is at 1-2 min).
    // ⭐ S43au (user): "we really should have tested distance from the 1m/2m/3m
    // vwap". DISTINCT from `speed`, which divides by the LAGGED vwap: dividing by
    // the CURRENT rolling vwap asks "how far below the recent average is this print
    // right now" — the numerator of a z-score rather than a displacement.
    let dvSum120 = SumMa 120
    let volSum120 = SumMa 120
    let dvSum180 = SumMa 180
    let volSum180 = SumMa 180
    let dvSum30 = SumMa 30
    let vwap30Lag = LagMa<float> 30              // the rolling 30-bar vwap, lagged 30s
    let dvSum60 = SumMa 60                       // Σ vwap·volume — the liquidity floor + vwap_60
    // ⭐ MA-exit machinery (user, 2026-07-29): rolling sums for the {10..60}m means.
    // Simple price MA = pxSumN/count; VWMA = dvSumN/volSumN. All partial-tolerant.
    let pxSum600 = SumMa 600
    let pxSum1200 = SumMa 1200
    let pxSum1800 = SumMa 1800
    let pxSum2400 = SumMa 2400
    let pxSum3000 = SumMa 3000
    let pxSum3600 = SumMa 3600
    let dvSum600 = SumMa 600
    // (no dvSum1200 here — the engine already maintains one below for vwap_1200;
    // a duplicate binding SHADOWED it and its double-push corrupted both. ⚠)
    let dvSum1800 = SumMa 1800
    let dvSum2400 = SumMa 2400
    let dvSum3000 = SumMa 3000
    let dvSum3600 = SumMa 3600
    let volSum1800 = SumMa 1800
    let volSum2400 = SumMa 2400
    let volSum3000 = SumMa 3000
    let volSum3600 = SumMa 3600
    let dvSum1200 = SumMa 1200                   // Σ vwap·volume over 20m — the 20m rolling VWAP
    // ⭐ S41q (user): z-score raw moments — Σv·p², Σv·ln p, Σv·(ln p)² per window
    // (both NORMAL- and LOG-space vw-σ derivable post-hoc; ln(vwap) != vw-mean of
    // ln p so log space needs its own first moment). Record-only.
    let dv2Sum300 = SumMa 300
    let dlvSum300 = SumMa 300
    let dlv2Sum300 = SumMa 300
    let dv2Sum600 = SumMa 600
    let dlvSum600 = SumMa 600
    let dlv2Sum600 = SumMa 600
    let dv2Sum1200 = SumMa 1200
    let dlvSum1200 = SumMa 1200
    let dlv2Sum1200 = SumMa 1200
    // ⭐ S43cf (user): the be window-difference speed cells (SpikeFader S35 port) —
    // bar-clock equal-weighted decayed sums of vwap, hl in BARS (pushed gap 0,
    // GapValue.Empty: pushes only). wd vwap = (SumS−SumF)/(WeightS−WeightF);
    // long-side speed = signal_vwap/wd − 1 (negative in a flush), post-hoc.
    let pxDecayB30 = DecaySumMa(30.0, GapValue.Empty)
    let pxDecayB60 = DecaySumMa(60.0, GapValue.Empty)
    let pxDecayB120 = DecaySumMa(120.0, GapValue.Empty)
    // ----- the locked volatility block -----
    let slots = SlotVwapMa cfg.SlotBars
    let ew40 = EmaHlMa 40.0                      // volat_20m — THE driver (F7 lock)
    let ew20 = EmaHlMa 20.0                      // volat_10m — the trajectory twin
    // ⭐ S43cf (user): the shorter volat twins for the EWMA ratio study (10 slots
    // = 5m, 6 slots = 3m) + the SpikeFader S34c/S34d autocorr port (whipsaw
    // knife candidate ac1_ewma ≥ −0.1). All RECORD-ONLY.
    let ew10 = EmaHlMa 10.0                      // volat_5m
    let ew6 = EmaHlMa 6.0                        // volat_3m
    let acEwma = EwmaAutoCorrMa(40.0, 3)
    // ⭐ S38n: the 40-INTERVAL horizon STAYS (a 39-interval alignment variant was
    // built and rejected — it churned ~11% of the book through the eff band and
    // lost 5 of 7 years). eff_20m therefore warms at 41 slots ≈ 1,230 present
    // bars, ONE SLOT after the 1200-bar entry channel; cold eff now FAILS (the
    // v1.2 cold-passes special case is retired — it only ever admitted the
    // 30-bar warm-up-gap fringe, 471 trips @ 1.78).
    let slotLag = LagMa<float> 40                // slot vwap 40 emissions ago (eff numerator)
    let slotAbsSum = SumMa 40                    // Σ|r| over the same 40 returns (eff denominator)
    // S43bd: linear twins — Σ|ΔV| in price space over the same 40/20 slot steps.
    let slotAbsSumLin = SumMa 40
    let slotAbsSumLin20 = SumMa 20
    let slotLag20 = LagMa<float> 20              // eff10m pair — same stream, half the horizon
    let slotAbsSum20 = SumMa 20
    // ⭐ S43al (user): the eff_9ema numerators. Σ s·r over the SAME 40 / 20 returns
    // slotAbsSum / slotAbsSum20 absolute, with s fixed by the PREVIOUS slot's
    // position relative to its own 9-slot EMA (strictly-prior information — the
    // sign is knowable before the return happens). This is the P&L of a
    // trend-following overlay normalised by total path length, and it replaces
    // Kaufman's two-endpoint numerator with one that reads the whole path.
    // ⚠ Cold-start convention: EmaMa seeds on the first value, so the day's FIRST
    // slot return reads s = -1 (pv > pv is false). One term in forty, and long
    // gone by the time the eff window warms at 41 slots (~1,230 present bars).
    let slotEma9 = EmaMa 9                       // 9-slot EMA of the slot vwaps
    let slotSgnSum = SumMa 40                    // Σ s·r over the eff_20m window
    let slotSgnSum20 = SumMa 20                  // ... and over the eff_10m window
    // S40: slot-vwap extremes over the SAME 41/21-vwap spans the eff returns
    // cover — the range-eff numerators. Warmth aligns with the eff pair: the
    // 41st slot emission fills slotMax41 AND completes slotAbsSum's 40 returns.
    let slotMax41 = MaxMa 41
    let slotMin41 = MinMa 41
    let slotMax21 = MaxMa 21
    let slotMin21 = MinMa 21
    let mutable prevSlotVwap : float voption = ValueNone
    let mutable prevEtSec = -1                   // the PREVIOUS present bar's etSec (aux-mark lookback)
    let mutable prevVwap = nan                   // ... and its vwap (continuation signal price)
    // did the PREVIOUS bar close strictly above its prior {10..60}m mean? (the
    // MA-exit marks fill on the bar AFTER the cross — aux discipline)
    let mutable prevXMa10 = false
    let mutable prevXMa20 = false
    let mutable prevXMa30 = false
    let mutable prevXMa40 = false
    let mutable prevXMa50 = false
    let mutable prevXMa60 = false
    let mutable prevXVw10 = false
    let mutable prevXVw20 = false
    let mutable prevXVw30 = false
    let mutable prevXVw40 = false
    let mutable prevXVw50 = false
    let mutable prevXVw60 = false
    let mutable slotReturns = 0
    // S42k (user, 2026-08-03): the last 40 |slot returns| regressed against
    // their order — the self-contained vol-trend (vchg needs a FULL prior 20m
    // window; an OLS of |r| WITHIN the current window doesn't). Two windows
    // share ONE queue, the 20 being a suffix of the 40: 40 -> volat_slope_20m,
    // 20 -> _10m.
    //
    // ⭐⭐ 2026-08-26 (user): PRODUCTION IMPLEMENTATION IMPORTED — WindowRoller +
    // OlsRoll, the same primitives the bar-level sums use, O(1) per push. The
    // previous inlined `volatOls` recomputed the normal equations O(n) at every
    // signal bar AND answered on PARTIAL windows. The partial window was dead
    // code at SPEC signals, provably: the armed eff-20m band gate reads
    // ValueNone below 40 slot returns and a ValueNone FAILS an armed gate — but
    // in a --base-run partial-window rows DID reach the parquet; those now read
    // NULL, the engine's standard `.Count = .WindowSize` convention. Full-window
    // values agree with the O(n) recompute to FP accumulation order (~1e-16;
    // FloatOls's own doc note measured 8.8e-18 worst at window 40). Verified
    // zero-diff against the live scanner on the one-year audit.
    let volatOls20m = FloatOls 40
    let volatOls10m = FloatOls 20
    // ⭐ S43cg (user): the 5m/3m rungs of the same ladder — same shared ring.
    let volatOls5m = FloatOls 10
    let volatOls3m = FloatOls 6
    let volatRoller =
        WindowRoller<float>(
            [| 40, [| volatOls20m :> IRoll<float> |]
               20, [| volatOls10m :> IRoll<float> |]
               10, [| volatOls5m :> IRoll<float> |]
               6, [| volatOls3m :> IRoll<float> |] |])
    // ⭐ S43cg (user): the EWMA slope twins — EwmaOlsMa on the SAME |r| stream,
    // hl in slots matching each SMA window; no hard warmup (established ~3
    // pushes), the ratio-study motivation made structural. Record-only.
    let volatEols20m = EwmaOlsMa 40.0
    let volatEols10m = EwmaOlsMa 20.0
    let volatEols5m = EwmaOlsMa 10.0
    let volatEols3m = EwmaOlsMa 6.0
    /// slope and SIGNED Pearson r (sign(slope)·√R²), FULL WINDOW ONLY — nan
    /// otherwise. Matches the live scanner's `fullSlope` read discipline.
    let volatOlsRead (o: FloatOls) =
        if o.Count = o.WindowSize then
            match o.Slope, o.R2 with
            | ValueSome sl, ValueSome r2 ->
                struct (sl, (if sl < 0.0 then -1.0 else 1.0) * sqrt r2)
            | ValueSome sl, ValueNone -> struct (sl, nan)
            | _ -> struct (nan, nan)
        else struct (nan, nan)
    // ----- gaps / location / session -----
    let gap60 = GapCounter(60, cfg.SessionStartSec)
    let gap30 = GapCounter(30, cfg.SessionStartSec)
    let gap15 = GapCounter(15, cfg.SessionStartSec)
    let gap120 = GapCounter(120, cfg.SessionStartSec)   // S43bk: the 120-tick window's twin
    // S40l: the slow-window gap counters (rank-0 Rényi at 5m/10m/20m; bars +
    // gaps = the window's calendar span).
    let gap300 = GapCounter(300, cfg.SessionStartSec)
    let gap600 = GapCounter(600, cfg.SessionStartSec)
    let gap1200 = GapCounter(1200, cfg.SessionStartSec)
    // ⭐ S40v (user): HALT vs SPARSITY — total gap seconds can't tell one long
    // contiguous hole (LULD halt ~300s, reopen pause) from many short ones
    // (thin tape). Per present bar, the gap RUN ending at that bar =
    // etSec − prevEtSec − 1 (first bar: seconds since the open). Windowed over
    // the last 1200/300 PRESENT bars: the max run + the count of >= 60s runs.
    let gapRunMax1200 = MaxMa 1200
    let gapRunMax300 = MaxMa 300
    let bigGapRuns1200 = SumMa 1200
    // S40x halt state: classified halt intervals [a,b] (missing seconds), the
    // day's halt count, the last reopen second, and the PRE-hole snapshots
    // (as of the previous present bar) the classifier reads.
    // S40y: the leg-anchored OLS pair. sinceHigh accumulates every bar and
    // resets on a new 20m high (anchor = the bar AFTER the high); sinceFlow
    // starts at the leg's first low (that bar inclusive — pushed at the event,
    // then per-bar while the leg is armed) and clears with the leg.
    let olsSinceHigh = AnchoredOls()
    let olsSinceFlow = AnchoredOls()
    // S40z: the anchored eff twins, same anchors/lifecycle as the OLS pair,
    // slot cadence = cfg.SlotBars (the eff_20m convention).
    let effSinceHigh = AnchoredEff cfg.SlotBars
    let effSinceFlow = AnchoredEff cfg.SlotBars
    let haltIvals = ResizeArray<struct (int * int)>()
    let mutable haltsToday = 0
    let mutable lastHaltEnd = -1
    let mutable prevAdjGap60 = 0
    let mutable prevRng300 = nan
    // ⭐ S43bk (user): the tick-direction block. `prevVwap` (set at the end of
    // OnBar) is the previous PRESENT bar's vwap, so the sign of
    // `bar.vwap - prevVwap` is this bar's tick. Windowed counts ride the same
    // present-bar SumMa convention as every other window in the engine.
    let dnSum15 = SumMa 15
    let dnSum30 = SumMa 30
    let dnSum60 = SumMa 60
    let dnSum120 = SumMa 120
    let upSum15 = SumMa 15
    let upSum30 = SumMa 30
    let upSum60 = SumMa 60
    let upSum120 = SumMa 120
    let mutable dnSinceUp = 0                    // down bars since the last up bar
    let mutable dnRun = 0                        // strict consecutive down run
    let mutable lastUptickSec = -1               // -1 = no uptick yet this session
    // ⭐ S43bn (user, 2026-08-11): the SAME tick counted over the LEG instead of
    // over the run — downticks since the leg's first 20m low, i.e. anchored on
    // the same event as `secs_since_first_low`/`bars_since_first_low` rather
    // than on the last uptick. `dsu` asks "how clean is the tape RIGHT NOW";
    // this asks "how one-sided has the WHOLE leg been". The uptick twin makes
    // the balance and the density (vs bars_since_first_low) recoverable.
    // -1 = disarmed, mirroring the LegCounters convention.
    let mutable dnFlow = -1
    let mutable upFlow = -1
    // ⭐ S43bo (user, 2026-08-11): count NEW 20m LOWS — not downtick bars — and
    // reset on every uptick. `dsu` counts every down BAR; this counts only the
    // bars that actually took out the 20m channel, so it is the averaging-down
    // depth of ONE uninterrupted down sequence. It sits between `dsu` (bar-level,
    // uptick-reset) and `lows_since_first_low` (low-level, LEG-reset): same
    // events as K, but reset by a bounce instead of by a new 20m high.
    // ⚠ A new 20m low is ALWAYS a downtick (the strictly-prior min includes the
    // previous bar, so vwap < prior min => vwap < prev vwap), hence a bar can
    // never both reset and increment this counter.
    let mutable lowsSinceUp = 0
    // ⭐ S43bp (user, 2026-08-11): the MAGNITUDE of the run, not just its length.
    // Anchor = the LAST UPTICK BAR's vwap (user's choice) — the same anchor both
    // `dsu` and `lows_since_uptick` reset on, so ONE column serves both counters.
    // `chg_since_last_uptick = vwap/anchor - 1` is negative during a down run;
    // dividing it by either counter gives the per-downtick / per-low rate
    // post-hoc, so no rate column is recorded. nan = no uptick yet this session.
    let mutable lastUptickVwap = nan
    // ⭐ S43bq (user, 2026-08-11): the SECOND anchor — the bar IMMEDIATELY
    // BEFORE the first 20m low after the last uptick, i.e. the last print above
    // the sequence. `chg_since_last_uptick` spans bounce -> now and can include
    // flat/down bars that never took out the channel; this spans
    // pre-first-low -> now, so it measures the low sequence ITSELF, inclusive of
    // the drop that opened it.
    // ⚠ Anchoring on the PRE-low bar rather than the first low itself is
    // deliberate (user): anchoring on the low would read exactly 0 whenever the
    // signal bar IS that first low (~half of all trips), collapsing the feature
    // on the most common case. nan = no low since the last uptick (or day's
    // first bar, which has no predecessor).
    let mutable runPreLowVwap = nan
    // ⭐ S43bq-b (user): BOTH anchors for the low run, studied side by side.
    // pre-low  -> the sequence INCLUSIVE of the drop that opened it
    // first-low -> the EXTENSION the sequence added beyond its own first low
    // (identically 0 when the signal bar IS that first low — ~half of trips —
    // which is exactly why it cannot be the only anchor). Their difference is
    // the opening drop, so the pair decomposes the run.
    let mutable runFirstLowVwap = nan
    // ⭐ S43bq-c (user): the SAME two-anchor treatment for the DOWNTICK run, so
    // both runs are measured the same way. Full 2x2:
    //            run        | anchored BEFORE the run | anchored on its FIRST bar
    //   downtick (dsu)      | last uptick bar         | first downtick bar
    //   20m low (lows_su)   | bar before first low    | the first low itself
    // The "before" anchors include the move that opened the run and are never
    // identically 0; the "first bar" anchors read 0 when the signal bar IS that
    // first bar, and measure only what the run added afterwards.
    let mutable runFirstDnVwap = nan
    let sessVwap = RatioMa()
    let mutable openVwap : float voption = ValueNone
    let mutable cumVol = 0.0
    let mutable dv0945Tape = 0.0                 // frozen once etSec >= 35100 (09:45)
    let mutable vol0945Tape = 0.0                // S41g (user): its volume twin — the
                                                 // first-15m tape vwap = dv/vol (record-only)
    let mutable cumTc = 0.0
    let mutable cumDv = 0.0                      // S40l: session Σ vwap·vol (leg-scoped dv)
    // ⭐ S40l tier-2 (user): leg-scoped participation — session-cum anchors
    // snapshotted at the leg's FIRST low (the first-low bar's own prints count
    // INTO the leg: anchor = cum minus the current bar). nan = no leg open.
    let mutable legVolAnchor = nan
    let mutable legTcAnchor = nan
    let mutable legDvAnchor = nan
    // ⭐ S41e (user): the arming drop — first leg low / 20m channel high at the
    // arming bar - 1 (the flush magnitude that STARTED the leg; chan_hi decays
    // as bars roll out, so it must be captured at arming, not read at signal)
    // + the drop segment's OLS slope/r (olsSinceHigh at the arming bar spans
    // exactly high -> first low — a free snapshot). Record-only. nan = no leg.
    let mutable dropToFlow = nan
    let mutable dropSlopeHiFlow = nan
    let mutable dropRHiFlow = nan
    let mutable dropEffHiFlow = nan
    // ⭐ S43ai (user): eff_20m / eff_10m as of the bar that SET the arming high.
    // Carried as MaxMaMeta metadata on the entry channel (same window as
    // entryMax) and snapshotted at the arming bar. RECORD-ONLY — no gate reads
    // these, so the signal path is byte-identical to v27.
    let mutable armHiEff20 = nan
    let mutable armHiEff10 = nan
    let mutable armHiSlots20 = 0
    let mutable armHiSlots10 = 0
    // S41k (user): the first low's own vwap — signal_vwap/first_low_vwap - 1 =
    // the leg's EXTENSION below its first low (distance twin of ssf). nan = no leg.
    let mutable firstLowVwap = nan
    // S41o (user): the TIME-INTEGRAL of day strength — present bars spent above
    // the (running) session vwap + the present-bar denominator. Session-scoped.
    let mutable barsAboveSvwap = 0
    let mutable barsPresent = 0
    // S41q session second moments (see the SumMa block for the window twins)
    let mutable cumDv2 = 0.0
    let mutable cumDlv = 0.0
    let mutable cumDlv2 = 0.0
    // ⭐ S40l tier-2 (S38i): target exits FILLED before this bar — the day-scoped
    // virgin flag becomes queryable (targets_today = 0 <=> virgin).
    let mutable targetsToday = 0
    // ⭐ S40l tier-2 (S39q): volat_20m as of 1200 present bars ago — the LAGGED
    // (pre-window) normalizer the slope study lacked. nan while cold.
    // ⭐ S43ao (user): the ENTRY-CHANNEL MIN as of 60 present bars ago. `chan_lo`
    // is the CURRENT strictly-prior 1200-bar min, which the signal has just broken
    // by a median of only 0.21% — a gap-through-the-level measure, not flush depth.
    // Against the min AS IT STOOD A MINUTE AGO, `signal_vwap/chan_lo_prev - 1` is a
    // genuine depth-over-a-minute measure: the same shape as `speed` but anchored on
    // a CHANNEL FLOOR instead of a rolling vwap (speed and d_1m_hi are corr 0.911 —
    // one feature twice — so a floor-anchored twin is the missing dimension).
    let entryMinLag = LagMa<float> 60
    // ⭐ S43ap (user): the 2m twins. The 1m family (speed, d_1m_hi, dlo1m) is one
    // axis three times over (pairwise corr 0.83-0.91); the question is whether
    // doubling the horizon separates from it or just smooths it.
    let entryMinLag120 = LagMa<float> 120
    let entryMinLag180 = LagMa<float> 180
    let volatLag = LagMa<float> 1200
    // ⭐ S42r (user, 2026-08-03): halts classified inside the last N PRESENT
    // BARS — one indicator per bar into a rolling sum (the bigGapRuns1200
    // pattern). Bar-indexed, NOT wall-clock: a halt REMOVES seconds from the
    // tape, so a wall-clock window shrinks exactly when a cascade is running
    // (a 300s pause eats 25% of a 1200s window; 2+ halts essentially cannot
    // fit and the count degenerates to 0/1 — measured, v24_hcount). The bar
    // window stretches back THROUGH the holes, where the cascade lives.
    let haltSum1200 = SumMa 1200
    let haltSum600 = SumMa 600

    // ⭐ ACTIVE/RETIRED SPLIT (user, 2026-07-23). At mc=0 a busy day opens hundreds
    // of trips per ticker; looping ALL of them every bar made runtime scale
    // super-linearly with trip count. A trip is INERT once it has exited AND its
    // last forward mark (+1200s) has filled — nothing in the per-bar loop can
    // touch it again — so it retires to `retired` and the hot loop only walks
    // `active`. `.Positions` = retired @ active (⚠ NOT chronological order —
    // sort by signal_sec in SQL if order matters).
    let active = ResizeArray<FlushPosition>()
    let retired = ResizeArray<FlushPosition>()
    let mutable pendingEntry : FlushPosition voption = ValueNone
    // ⭐ right-side-of-V continuations: watchers arm at the parent fill (one per
    // window, removed when fired), positions retire once all three stops resolve.
    // STRICTLY-PRIOR snapshots, captured BEFORE this bar's vwap folds in. ⚠ If
    // the current vwap were inside its own window, "vwap < channel min" would be
    // trivially false on every bar (a value can't undercut a min that contains
    // it) — and the exit target would trivially fire on every leading bar.
    let mutable sMax30 : float voption = ValueNone
    let mutable sMax60 : float voption = ValueNone
    let mutable sMax120 : float voption = ValueNone
    let mutable sMax180 : float voption = ValueNone   // S43cf: br180's strictly-prior high
    let mutable sMax300 : float voption = ValueNone
    let mutable sMax600 : float voption = ValueNone
    let mutable sMax1200 : float voption = ValueNone
    // S43ai: prior-bar metadata of the entry-channel high (eff_20m, eff_10m)
    let mutable sMaxMeta : struct (float * float * int * int) voption = ValueNone
    let mutable sMin30 : float voption = ValueNone
    let mutable sMin60 : float voption = ValueNone
    let mutable sMin120 : float voption = ValueNone
    let mutable sMin300 : float voption = ValueNone
    let mutable sMin600 : float voption = ValueNone
    let mutable sMin1200 : float voption = ValueNone
    let mutable sExitMax : float voption = ValueNone
    let mutable sSessHigh : float voption = ValueNone
    let mutable sSessLow : float voption = ValueNone

    let vv (v: float voption) = match v with ValueSome x -> x | ValueNone -> nan
    /// S43cf: prior-window wd vwap from two decayed sums (SpikeFader S35 form —
    /// the den difference must be strictly positive).
    let wdVwap (ns: float voption) (ds: float voption) (nf: float voption) (df: float voption) =
        match ns, ds, nf, df with
        | ValueSome ns, ValueSome ds, ValueSome nf, ValueSome df when ds - df > 0.0 ->
            (ns - nf) / (ds - df)
        | _ -> nan
    /// ln(high/low) of a channel pair, nan until both sides carry a value.
    let chanRng (hi: MaxMa) (lo: MinMa) =
        match hi.State, lo.State with
        | ValueSome h, ValueSome l when l > 0.0 -> log (h / l)
        | _ -> nan

    member _.Ticker = ticker
    member _.Day = day
    member _.Positions = Seq.append retired active
    member _.OpenCount =
        let mutable k = 0
        for p in active do
            (match p.State with Holding | PendingExit _ -> k <- k + 1 | ExitedAt _ -> ())
        k
    member this.HasSlot =
        cfg.MaxConcurrent <= 0
        || this.OpenCount + (if pendingEntry.IsSome then 1 else 0) < cfg.MaxConcurrent

    /// Advance the whole system by one PRESENT 1s bar. Bars arrive in etSec
    /// order, RTH only (the emitter filters to [SessionStartSec, MocSec]).
    member this.Process (bar: SecBar) =
        if bar.etSec < cfg.SessionStartSec then () else

        // ===== 1. capture the STRICTLY-PRIOR channel states =====
        sMax30 <- max30.State
        sMax60 <- max60.State
        sMax120 <- max120.State
        sMax180 <- max180.State
        sMax300 <- max300.State
        sMax600 <- max600.State
        sMax1200 <- max1200.State
        // S43ai: the STRICTLY-PRIOR metadata, snapshotted at the same instant as
        // sMax1200 so it describes the same high that priorEntryMax reports.
        sMaxMeta <- entryMaxMeta.StateMeta
        sMin30 <- min30.State
        sMin60 <- min60.State
        sMin120 <- min120.State
        sMin300 <- min300.State
        sMin600 <- min600.State
        sMin1200 <- min1200.State
        sExitMax <- exitMax.State
        sSessHigh <- sessHigh.State
        sSessLow <- sessLow.State
        // strictly-prior {10..60}m means for the MA-exit marks (partial-tolerant:
        // an early-session window degrades to the session-so-far mean)
        let inline sumMean (s: SumMa) = match s.State with ValueSome v when s.Count > 0 -> v / float s.Count | _ -> nan
        let inline sumRatio (a: SumMa) (b: SumMa) = match a.State, b.State with ValueSome x, ValueSome y when y > 0.0 -> x / y | _ -> nan
        let ma10 = sumMean pxSum600
        let ma20 = sumMean pxSum1200
        let ma30 = sumMean pxSum1800
        let ma40 = sumMean pxSum2400
        let ma50 = sumMean pxSum3000
        let ma60 = sumMean pxSum3600
        let vwma10 = sumRatio dvSum600 volSum600
        let vwma20 = sumRatio dvSum1200 volSum1200
        let vwma30 = sumRatio dvSum1800 volSum1800
        let vwma40 = sumRatio dvSum2400 volSum2400
        let vwma50 = sumRatio dvSum3000 volSum3000
        let vwma60 = sumRatio dvSum3600 volSum3600
        let priorEntryMin =
            match cfg.EntryChannelBars with
            | 30 -> sMin30 | 60 -> sMin60 | 120 -> sMin120 | 300 -> sMin300 | 600 -> sMin600 | 1200 -> sMin1200
            | _ -> ValueNone
        let priorEntryMax =
            match cfg.EntryChannelBars with
            | 30 -> sMax30 | 60 -> sMax60 | 120 -> sMax120 | 300 -> sMax300 | 600 -> sMax600 | 1200 -> sMax1200
            | _ -> ValueNone

        // ===== 2. fold this bar into every structure =====
        if openVwap.IsNone then openVwap <- ValueSome bar.vwap
        cumVol <- cumVol + bar.volume
        barsPresent <- barsPresent + 1
        // tape-native 09:30-09:45 dollar volume (35100 = THE knowability floor, R4)
        if bar.etSec < 35100 then
            dv0945Tape <- dv0945Tape + bar.vwap * bar.volume
            vol0945Tape <- vol0945Tape + bar.volume
        cumTc <- cumTc + float bar.tradeCount
        cumDv <- cumDv + bar.vwap * bar.volume
        let lnp = log bar.vwap
        cumDv2 <- cumDv2 + bar.vwap * bar.vwap * bar.volume
        cumDlv <- cumDlv + lnp * bar.volume
        cumDlv2 <- cumDlv2 + lnp * lnp * bar.volume
        if cumVol > 0.0 && bar.vwap > cumDv / cumVol then barsAboveSvwap <- barsAboveSvwap + 1
        gap60.Push bar.etSec
        gap30.Push bar.etSec
        gap15.Push bar.etSec
        gap120.Push bar.etSec
        gap300.Push bar.etSec
        gap600.Push bar.etSec
        gap1200.Push bar.etSec
        // S40v: the tradeless run ending at THIS bar (prevEtSec still holds the
        // previous present bar; -1 on the day's first bar -> run from the open).
        let gapRun =
            let prev = if prevEtSec < cfg.SessionStartSec then cfg.SessionStartSec - 1 else prevEtSec
            float (max 0 (bar.etSec - prev - 1))
        gapRunMax1200.Push gapRun
        gapRunMax300.Push gapRun
        bigGapRuns1200.Push (if gapRun >= 60.0 then 1.0 else 0.0)
        // ⭐ S40x: classify the run just ended against the PRE-hole snapshots.
        // First bar of the day never classifies (prevRng300 = nan fails >=).
        let haltClassified =
            if gapRun >= float cfg.HaltMinRunSec
               && prevRng300 >= cfg.HaltMinRng300
               && prevAdjGap60 < cfg.HaltMaxPreGap60
               && prevEtSec >= cfg.SessionStartSec then
                haltIvals.Add(struct (prevEtSec + 1, bar.etSec - 1))
                haltsToday <- haltsToday + 1
                lastHaltEnd <- bar.etSec
                true
            else false
        // S42r: 1 on a bar that classified a halt, 0 otherwise -> the rolling
        // sums ARE the windowed halt counts (auto-evicting at 1200/600 bars).
        haltSum1200.Push (if haltClassified then 1.0 else 0.0)
        haltSum600.Push (if haltClassified then 1.0 else 0.0)
        // adjusted gaps = raw window gaps minus halt-interval overlap with the
        // SAME [max(sessionStart, now-W+1), now] window the GapCounter uses.
        let haltOverlap (w: int) =
            let lo = max cfg.SessionStartSec (bar.etSec - w + 1)
            let mutable s = 0
            for struct (a, b) in haltIvals do
                let hiO = min b bar.etSec
                let loO = max a lo
                if hiO >= loO then s <- s + (hiO - loO + 1)
            s
        let adjGap60 = max 0 (gap60.Gaps - haltOverlap 60)
        let adjGap300 = max 0 (gap300.Gaps - haltOverlap 300)
        let adjGap600 = max 0 (gap600.Gaps - haltOverlap 600)
        let adjGap1200 = max 0 (gap1200.Gaps - haltOverlap 1200)
        // ⭐ S43bk tick direction. The day's FIRST present bar has no
        // predecessor (prevVwap = nan) — both comparisons are false, so it
        // counts as an UNCHANGED tick and touches no counter. Ties (vwap
        // exactly equal, common on round-number pinning) likewise count as
        // neither up nor down: they do not reset `dnSinceUp`, but they DO reset
        // `dnRun`, which is the whole difference between the two run measures.
        let isUptick = bar.vwap > prevVwap
        let isDntick = bar.vwap < prevVwap
        dnSum15.Push (if isDntick then 1.0 else 0.0)
        dnSum30.Push (if isDntick then 1.0 else 0.0)
        dnSum60.Push (if isDntick then 1.0 else 0.0)
        dnSum120.Push (if isDntick then 1.0 else 0.0)
        upSum15.Push (if isUptick then 1.0 else 0.0)
        upSum30.Push (if isUptick then 1.0 else 0.0)
        upSum60.Push (if isUptick then 1.0 else 0.0)
        upSum120.Push (if isUptick then 1.0 else 0.0)
        if isUptick then
            dnSinceUp <- 0
            dnRun <- 0
            lowsSinceUp <- 0          // S43bo: the bounce ends the low sequence
            lastUptickSec <- bar.etSec
            lastUptickVwap <- bar.vwap   // S43bp: re-anchor the run's magnitude
            runPreLowVwap <- nan         // S43bq: the next low starts a new sequence
            runFirstLowVwap <- nan
            runFirstDnVwap <- nan        // S43bq-c: the downtick run restarts too
        elif isDntick then
            // S43bq-c: the FIRST downtick of this run anchors its magnitude
            // (test BEFORE the increment).
            if dnSinceUp = 0 then runFirstDnVwap <- bar.vwap
            dnSinceUp <- dnSinceUp + 1
            dnRun <- dnRun + 1
        else
            dnRun <- 0
        volSum5.Push bar.volume
        dvSum5.Push (bar.vwap * bar.volume)
        volSum10.Push bar.volume
        dvSum10.Push (bar.vwap * bar.volume)
        volSum15.Push bar.volume
        volSum30.Push bar.volume
        volSum60.Push bar.volume
        volSum300.Push bar.volume
        volSum600.Push bar.volume
        volSum1200.Push bar.volume
        tcSum5.Push (float bar.tradeCount)
        tcSum10.Push (float bar.tradeCount)
        tcSum15.Push (float bar.tradeCount)
        tcSum30.Push (float bar.tradeCount)
        tcSum60.Push (float bar.tradeCount)
        tcSum300.Push (float bar.tradeCount)
        tcSum600.Push (float bar.tradeCount)
        tcSum1200.Push (float bar.tradeCount)
        dvSum30.Push (bar.vwap * bar.volume)   // S43at
        dvSum120.Push (bar.vwap * bar.volume)  // S43au
        volSum120.Push bar.volume
        dvSum180.Push (bar.vwap * bar.volume)
        volSum180.Push bar.volume
        dvSum60.Push (bar.vwap * bar.volume)
        dvSum300.Push (bar.vwap * bar.volume)
        dv2Sum300.Push (bar.vwap * bar.vwap * bar.volume)
        dlvSum300.Push (log bar.vwap * bar.volume)
        dlv2Sum300.Push (log bar.vwap * log bar.vwap * bar.volume)
        pxSum600.Push bar.vwap
        pxSum1200.Push bar.vwap
        pxSum1800.Push bar.vwap
        pxSum2400.Push bar.vwap
        pxSum3000.Push bar.vwap
        pxSum3600.Push bar.vwap
        dvSum600.Push (bar.vwap * bar.volume)
        dv2Sum600.Push (bar.vwap * bar.vwap * bar.volume)
        dlvSum600.Push (log bar.vwap * bar.volume)
        dlv2Sum600.Push (log bar.vwap * log bar.vwap * bar.volume)
        dvSum1800.Push (bar.vwap * bar.volume)
        dvSum2400.Push (bar.vwap * bar.volume)
        dvSum3000.Push (bar.vwap * bar.volume)
        dvSum3600.Push (bar.vwap * bar.volume)
        volSum1800.Push bar.volume
        volSum2400.Push bar.volume
        volSum3000.Push bar.volume
        volSum3600.Push bar.volume
        dvSum1200.Push (bar.vwap * bar.volume)
        dv2Sum1200.Push (bar.vwap * bar.vwap * bar.volume)
        dlvSum1200.Push (log bar.vwap * bar.volume)
        dlv2Sum1200.Push (log bar.vwap * log bar.vwap * bar.volume)
        // the minute-lag chains feed only WARM 60-sums (1 push per bar after
        // warmup keeps .Lagged = the value ending exactly 60 bars ago)
        if volSum60.Count = 60 then
            (match volSum60.State with ValueSome s -> vol60Lag.Push s | ValueNone -> ())
        if tcSum60.Count = 60 then
            (match tcSum60.State with ValueSome s -> tc60Lag.Push s | ValueNone -> ())
        // ⭐ the rolling 60-bar vwap + its one-minute lag (user, 2026-07-28):
        // dv_60/vol_60, pushed into the lag once per warm bar so .Lagged = the
        // previous NON-OVERLAPPING minute's vwap — the flush-speed denominator.
        let vwap60Now =
            if volSum60.Count = volSum60.WindowSize then
                match dvSum60.State, volSum60.State with
                | ValueSome dv, ValueSome v when v > 0.0 -> ValueSome (dv / v)
                | _ -> ValueNone
            else ValueNone
        (match vwap60Now with ValueSome v -> vwap60Lag.Push v | ValueNone -> ())
        // S43at: same construction one horizon down — 30-bar rolling vwap, lagged 30 bars.
        (if volSum30.Count = volSum30.WindowSize then
            match dvSum30.State, volSum30.State with
            | ValueSome dv, ValueSome v when v > 0.0 -> vwap30Lag.Push (dv / v)
            | _ -> ())
        (if volSum5.Count = volSum5.WindowSize then
            match dvSum5.State, volSum5.State with
            | ValueSome dv, ValueSome v when v > 0.0 -> vwap5Lag.Push (dv / v)
            | _ -> ())
        (if volSum10.Count = volSum10.WindowSize then
            match dvSum10.State, volSum10.State with
            | ValueSome dv, ValueSome v when v > 0.0 -> vwap10Lag.Push (dv / v)
            | _ -> ())
        sessVwap.Push(bar.vwap * bar.volume, bar.volume)
        max30.Push bar.vwap
        max60.Push bar.vwap
        max120.Push bar.vwap
        max180.Push bar.vwap
        // S43cf: the be speed cells — bar-clock, gap 0 by definition
        pxDecayB30.Push(bar.vwap, 0)
        pxDecayB60.Push(bar.vwap, 0)
        pxDecayB120.Push(bar.vwap, 0)
        max300.Push bar.vwap
        max600.Push bar.vwap
        max1200.Push bar.vwap
        min30.Push bar.vwap
        min60.Push bar.vwap
        min120.Push bar.vwap
        min300.Push bar.vwap
        min600.Push bar.vwap
        min1200.Push bar.vwap
        sessHigh.Push bar.vwap
        sessLow.Push bar.vwap
        ols60.Push (log bar.vwap)
        ols120.Push (log bar.vwap)
        ols180.Push (log bar.vwap)
        ols300.Push (log bar.vwap)
        olsSinceHigh.Push (log bar.vwap)
        effSinceHigh.Push(bar.vwap, bar.volume)
        // the first-low bar itself is pushed at the arming event (step 4) —
        // Armed is still false here on that bar, so no double-push.
        if counters.Armed then
            olsSinceFlow.Push (log bar.vwap)
            effSinceFlow.Push(bar.vwap, bar.volume)
        ols600.Push (log bar.vwap)
        ols1200.Push (log bar.vwap)
        neff600.Push (neffLift bar.volume)
        if neff600Count = 600 then neff600.Pop() else neff600Count <- neff600Count + 1
        neff1200.Push (neffLift bar.volume)
        if neff1200Count = 1200 then neff1200.Pop() else neff1200Count <- neff1200Count + 1
        neff60.Push (neffLift bar.volume)
        if neff60Count = 60 then neff60.Pop() else neff60Count <- neff60Count + 1
        neff300.Push (neffLift bar.volume)
        if neff300Count = 300 then neff300.Pop() else neff300Count <- neff300Count + 1
        // S40l: lagged volat — push the CURRENT ew40 state each present bar so
        // .Lagged = volat_20m as of 1200 present bars ago (nan while EMA cold).
        volatLag.Push (match ew40.State with ValueSome v -> v | ValueNone -> nan)
        // S43ao: push THIS bar's strictly-prior channel min; `.Lagged` then reads
        // the value from 60 bars ago (same semantics as vwap60Lag -> vwap_60_prev).
        entryMinLag.Push (match priorEntryMin with ValueSome v -> v | ValueNone -> nan)
        entryMinLag120.Push (match priorEntryMin with ValueSome v -> v | ValueNone -> nan)
        entryMinLag180.Push (match priorEntryMin with ValueSome v -> v | ValueNone -> nan)
        // the slot chain: one |r| into the volat EWMAs per completed slot
        match slots.Push(bar.vwap, bar.volume) with
        | ValueSome v ->
            (match prevSlotVwap with
             | ValueSome pv when pv > 0.0 && v > 0.0 ->
                 let r = log (v / pv)
                 let ar = abs r
                 ew40.Push ar
                 ew20.Push ar
                 ew10.Push ar                     // S43cf: volat_5m
                 ew6.Push ar                      // S43cf: volat_3m
                 acEwma.Push r                    // S43cf: SIGNED r (SpikeFader S34c port)
                 slotAbsSum.Push ar
                 slotAbsSum20.Push ar
                 // S43bd: the same slot step in PRICE space for the linear twins
                 slotAbsSumLin.Push (abs (v - pv))
                 slotAbsSumLin20.Push (abs (v - pv))
                 // S43al: the trend-signed twin of this same return. slotEma9 has
                 // absorbed pv but NOT v (it is pushed below, with slotLag), so the
                 // sign is fixed strictly before r is realised — no lookahead.
                 let s =
                     match slotEma9.State with
                     | ValueSome e when pv > e -> 1.0
                     | _ -> -1.0
                 slotSgnSum.Push (s * r)
                 slotSgnSum20.Push (s * r)
                 // S39i: the same |r| into the smoothness windows
                 neffRet40.Push (NEffShannon.Zero.Add ar)
                 if neffRet40Count = 40 then neffRet40.Pop() else neffRet40Count <- neffRet40Count + 1
                 neffRet20.Push (NEffShannon.Zero.Add ar)
                 if neffRet20Count = 20 then neffRet20.Pop() else neffRet20Count <- neffRet20Count + 1
                 volatRoller.Push ar                   // S42k: one queue, all four windows
                 volatEols20m.Push ar                  // S43cg: the EWMA slope twins
                 volatEols10m.Push ar
                 volatEols5m.Push ar
                 volatEols3m.Push ar
                 slotReturns <- slotReturns + 1
             | _ -> ())
            slotLag.Push v
            slotLag20.Push v
            slotEma9.Push v                      // S43al: AFTER the sign read above

            slotMax41.Push v
            slotMin41.Push v
            slotMax21.Push v
            slotMin21.Push v
            prevSlotVwap <- ValueSome v
        | ValueNone -> ()

        // ⭐ S43ai: feed the metadata twin ONCE per present bar, HERE — after the
        // slot chain, so the payload is the eff pair in exactly the state the
        // signal-time eff_20m / eff_10m would read on this bar. (Position in the
        // bar body is otherwise irrelevant: one push per bar in bar order gives
        // a deque identical to entryMax's.) Same formulas as the eff20Signed /
        // eff10Ok gates; nan when the window is cold.
        // ⭐ S43aj (user): take the ratio AS IT IS even when the window is short —
        // `Oldest` spans whatever has accumulated — and RECORD THE SPAN alongside,
        // so a partial reading is identifiable rather than silently mixed in with
        // full ones. ⚠ The span matters: a shorter window gives chop less time to
        // accumulate in the denominator, so eff drifts UP as the span shrinks
        // (S43ai measured median 0.337 at 20 slots vs 0.191 at 40). Below 20 slots
        // the two measures read IDENTICALLY (same queue contents), which the
        // recorded spans make visible.
        let effPairNow =
            let partialEff (lagM: LagMa<float>) (absSum: SumMa) =
                match lagM.Last, lagM.Oldest, absSum.State with
                | ValueSome cur, ValueSome old, ValueSome sm
                    when lagM.Count >= 2 && old > 0.0 && cur > 0.0 && sm > 0.0 ->
                    struct (log (cur / old) / sm, lagM.Count - 1)
                | _ -> struct (nan, 0)
            let struct (e20, n20) = partialEff slotLag slotAbsSum
            let struct (e10, n10) = partialEff slotLag20 slotAbsSum20
            struct (e20, e10, n20, n10)
        entryMaxMeta.Push(bar.vwap, effPairNow)

        // ===== 3. fill pendings at THIS bar's vwap (signals from the prior bar) =====
        match pendingEntry with
        | ValueSome p ->
            let filled = { p with EntrySec = bar.etSec; EntryPx = bar.vwap }
            active.Add filled
            pendingEntry <- ValueNone
        | ValueNone -> ()
        for i in 0 .. active.Count - 1 do
            match active.[i].State with
            | PendingExit reason ->
                active.[i] <- { active.[i] with State = ExitedAt (bar.etSec, bar.vwap, reason) }
                // S40l (S38i): the day-scoped virgin clock — count target exits
                // at their FILL (the bounce is real once the exit prints).
                if reason = "target" then targetsToday <- targetsToday + 1
            | _ -> ()

        // ===== 4. breach counters: step, then mark this bar's breaches =====
        // The aux-mark logic (step 5) reads the counters AS OF THE PREVIOUS BAR
        // ("previous snapshot's bars-since-high = 0 -> mark on the current
        // bar") — snapshot them before this bar's update.
        let prevBr60 = br60.BarsSinceBreach
        let prevBr120 = br120.BarsSinceBreach
        let prevBr300 = br300.BarsSinceBreach
        let prevBr600 = br600.BarsSinceBreach
        let prevBr1200 = br1200.BarsSinceBreach
        let breached (prior: float voption) = match prior with ValueSome hi -> bar.vwap > hi | ValueNone -> false
        brSess.Step(); br30.Step(); br60.Step(); br120.Step(); br180.Step(); br300.Step(); br600.Step(); br1200.Step()
        if breached sSessHigh then brSess.OnBreach()
        if breached sMax30 then br30.OnBreach()
        if breached sMax60 then br60.OnBreach()
        if breached sMax120 then br120.OnBreach()
        if breached sMax180 then br180.OnBreach()
        if breached sMax300 then br300.OnBreach()
        if breached sMax600 then br600.OnBreach()
        if breached sMax1200 then br1200.OnBreach()
        let breachedLo (prior: float voption) = match prior with ValueSome lo -> bar.vwap < lo | ValueNone -> false
        brLoSess.Step(); brLo30.Step(); brLo60.Step(); brLo120.Step(); brLo300.Step(); brLo600.Step(); brLo1200.Step()
        if breachedLo sSessLow then brLoSess.OnBreach()
        if breachedLo sMin30 then brLo30.OnBreach()
        if breachedLo sMin60 then brLo60.OnBreach()
        if breachedLo sMin120 then brLo120.OnBreach()
        if breachedLo sMin300 then brLo300.OnBreach()
        if breachedLo sMin600 then brLo600.OnBreach()
        if breachedLo sMin1200 then brLo1200.OnBreach()
        // ⭐ the leg machine. Step FIRST so BarsSinceFirstLow counts bars
        // ELAPSED since the leg's first low. STRICT inequalities on both
        // events (V6 F21: `<=` re-fired on round-number pinning ties — two
        // consecutive identical prints are NOT a new low; same rule for the
        // reset high). The RESET itself fires LAST (step 7) — an entry on
        // this bar must read the pre-reset counters.
        counters.Step()
        counters300.Step()
        counters600.Step()
        counters120.Step()
        counters180.Step()
        // ⭐ S43bn: fold this bar's tick into the LEG totals. Runs with Step (so
        // it counts bars ELAPSED since the first low) and BEFORE the arming
        // branch below, which seeds the counters from the first-low bar itself.
        if counters.Armed then
            if isDntick then dnFlow <- dnFlow + 1
            elif isUptick then upFlow <- upFlow + 1
        let isNewLow = match priorEntryMin with ValueSome lo -> bar.vwap < lo | ValueNone -> false
        let isNewHigh = match priorEntryMax with ValueSome hi -> bar.vwap > hi | ValueNone -> false
        if isNewLow then
            // S40l: the 20m leg's FIRST low anchors the leg-participation cums.
            // Anchor BEFORE this bar's prints (already folded above) so the
            // first-low bar itself counts INTO the leg.
            if not counters.Armed then
                legVolAnchor <- cumVol - bar.volume
                legTcAnchor <- cumTc - float bar.tradeCount
                legDvAnchor <- cumDv - bar.vwap * bar.volume
                // S43bn: seed the leg tick totals from THIS bar (same "first-low
                // bar counts INTO the leg" rule as the participation anchors).
                dnFlow <- (if isDntick then 1 else 0)
                upFlow <- (if isUptick then 1 else 0)
                // S40y/z: the first-low OLS + eff start HERE, this bar inclusive.
                olsSinceFlow.Reset()
                olsSinceFlow.Push (log bar.vwap)
                effSinceFlow.Reset()
                effSinceFlow.Push(bar.vwap, bar.volume)
                // S41e: the arming drop, captured against THIS bar's prior 20m high
                // + the drop segment's OLS (sinceHigh spans high -> this first low).
                dropToFlow <-
                    match priorEntryMax with
                    | ValueSome hi when hi > 0.0 -> bar.vwap / hi - 1.0
                    | _ -> nan
                dropSlopeHiFlow <- olsSinceHigh.Slope
                dropRHiFlow <- olsSinceHigh.R
                dropEffHiFlow <- effSinceHigh.Eff   // S43ai: same anchor, same bar
                // ⭐ S43ai: the eff pair as of the bar that SET that same high —
                // sMaxMeta is snapshotted alongside sMax1200, so it describes
                // exactly the `priorEntryMax` used for dropToFlow above.
                (match sMaxMeta with
                 | ValueSome (struct (e20, e10, n20, n10)) ->
                     armHiEff20 <- e20; armHiEff10 <- e10
                     armHiSlots20 <- n20; armHiSlots10 <- n10
                 | ValueNone ->
                     armHiEff20 <- nan; armHiEff10 <- nan
                     armHiSlots20 <- 0; armHiSlots10 <- 0)
                firstLowVwap <- bar.vwap
            // S43bo: another 20m low inside the current (uptick-delimited)
            // sequence. Safe to place here — a new low can never be an uptick.
            // S43bq: the FIRST low of the sequence anchors the extension measure
            // (test BEFORE the increment).
            if lowsSinceUp = 0 then
                runPreLowVwap <- prevVwap
                runFirstLowVwap <- bar.vwap
            lowsSinceUp <- lowsSinceUp + 1
            counters.OnEvent bar.etSec
            counters300.OnEvent bar.etSec
            counters600.OnEvent bar.etSec
            counters120.OnEvent bar.etSec
            counters180.OnEvent bar.etSec

        // ===== 5. advance open positions: forward marks, hold clock, exit signals =====
        // Exit precedence: moc > acceptance stops > target. Stops and target are
        // mutually exclusive on one bar (a new entry-channel LOW cannot also be a
        // new exit-channel HIGH: prior max >= prior min).
        // ⭐ S43bw: past AfterHoursSec the target may switch to a tighter channel
        // (see ExitChannelBarsAfterHours). All six snapshots are already maintained
        // strictly-prior, so this is a selection, not a new structure. Inert at the
        // default 0.
        let sTargetMax =
            if cfg.ExitChannelBarsAfterHours > 0 && bar.etSec >= cfg.AfterHoursSec then
                match cfg.ExitChannelBarsAfterHours with
                | 30 -> sMax30 | 60 -> sMax60 | 120 -> sMax120
                | 300 -> sMax300 | 600 -> sMax600 | 1200 -> sMax1200
                | _ -> sExitMax
            else sExitMax
        let targetHit = match sTargetMax with ValueSome hi -> bar.vwap > hi | ValueNone -> false
        // ⭐ the price-acceptance stops: qualified FRESH lows only (see config)
        let rate60vs1200 (s60: SumMa) (s1200: SumMa) =
            if s60.Count = s60.WindowSize && s1200.Count = s1200.WindowSize then
                match s60.State, s1200.State with
                | ValueSome a, ValueSome b when b > 0.0 -> ValueSome ((a / 60.0) / (b / 1200.0))
                | _ -> ValueNone
            else ValueNone
        let volStopHit =
            isNewLow
            && not (Double.IsPositiveInfinity cfg.VolStopRatio)
            && (match rate60vs1200 volSum60 volSum1200 with
                | ValueSome r -> r >= cfg.VolStopRatio
                | ValueNone -> false)
        let tcStopHit =
            isNewLow
            && not (Double.IsPositiveInfinity cfg.TcStopRatio)
            && (match rate60vs1200 tcSum60 tcSum1200 with
                | ValueSome r -> r >= cfg.TcStopRatio
                | ValueNone -> false)
        let speedStopHit =
            isNewLow
            && cfg.SpeedStopPct < 0.0
            && (match vwap60Lag.Lagged with
                | ValueSome pv when pv > 0.0 -> bar.vwap / pv - 1.0 < cfg.SpeedStopPct
                | _ -> false)
        // compacting walk: survivors overwrite in place, inert trips retire
        let mutable w = 0
        for i in 0 .. active.Count - 1 do
            let p = active.[i]
            // forward marks fill for EVERY trip (exited included — the sampler
            // wants the counterfactual path), first present bar past each horizon
            let p =
                { p with
                    FwdVwap60 = if Double.IsNaN p.FwdVwap60 && bar.etSec >= p.EntrySec + 60 then bar.vwap else p.FwdVwap60
                    FwdVwap300 = if Double.IsNaN p.FwdVwap300 && bar.etSec >= p.EntrySec + 300 then bar.vwap else p.FwdVwap300
                    FwdVwap600 = if Double.IsNaN p.FwdVwap600 && bar.etSec >= p.EntrySec + 600 then bar.vwap else p.FwdVwap600
                    FwdVwap1200 = if Double.IsNaN p.FwdVwap1200 && bar.etSec >= p.EntrySec + 1200 then bar.vwap else p.FwdVwap1200 }
            // aux-high marks: the PREVIOUS bar's breach-counter snapshot reads
            // 0 -> the previous bar printed the new N-bar high -> the mark
            // fills at THIS bar's vwap. Only highs printed STRICTLY AFTER the
            // entry fill bar count (prevEtSec > EntrySec), and only the FIRST
            // one per window per trip (mark still nan).
            let inline auxStep px sec prevBr =
                if Double.IsNaN px && prevBr = 0 && prevEtSec > p.EntrySec
                then struct (bar.vwap, bar.etSec)
                else struct (px, sec)
            let struct (hi60, sc60) = auxStep p.AuxHi60 p.AuxSec60 prevBr60
            let struct (hi120, sc120) = auxStep p.AuxHi120 p.AuxSec120 prevBr120
            let struct (hi300, sc300) = auxStep p.AuxHi300 p.AuxSec300 prevBr300
            let struct (hi600, sc600) = auxStep p.AuxHi600 p.AuxSec600 prevBr600
            let struct (hi1200, sc1200) = auxStep p.AuxHi1200 p.AuxSec1200 prevBr1200
            let p =
                { p with
                    AuxHi60 = hi60; AuxSec60 = sc60
                    AuxHi120 = hi120; AuxSec120 = sc120
                    AuxHi300 = hi300; AuxSec300 = sc300
                    AuxHi600 = hi600; AuxSec600 = sc600
                    AuxHi1200 = hi1200; AuxSec1200 = sc1200 }
            // MA-exit marks: the PREVIOUS bar crossed strictly above its prior
            // mean (strictly after the fill bar) -> fill at THIS bar's vwap; any
            // mark still unresolved at/past MocSec resolves at this bar (the moc
            // fallback — sec >= MocSec distinguishes it post-hoc).
            let inline maStep px sec prevX =
                if Double.IsNaN px && (bar.etSec >= mocSec || (prevX && prevEtSec > p.EntrySec))
                then struct (bar.vwap, bar.etSec)
                else struct (px, sec)
            let struct (m10p, m10s) = maStep p.Ma10Px p.Ma10Sec prevXMa10
            let struct (m20p, m20s) = maStep p.Ma20Px p.Ma20Sec prevXMa20
            let struct (m30p, m30s) = maStep p.Ma30Px p.Ma30Sec prevXMa30
            let struct (m40p, m40s) = maStep p.Ma40Px p.Ma40Sec prevXMa40
            let struct (m50p, m50s) = maStep p.Ma50Px p.Ma50Sec prevXMa50
            let struct (m60p, m60s) = maStep p.Ma60Px p.Ma60Sec prevXMa60
            let struct (v10p, v10s) = maStep p.Vwma10Px p.Vwma10Sec prevXVw10
            let struct (v20p, v20s) = maStep p.Vwma20Px p.Vwma20Sec prevXVw20
            let struct (v30p, v30s) = maStep p.Vwma30Px p.Vwma30Sec prevXVw30
            let struct (v40p, v40s) = maStep p.Vwma40Px p.Vwma40Sec prevXVw40
            let struct (v50p, v50s) = maStep p.Vwma50Px p.Vwma50Sec prevXVw50
            let struct (v60p, v60s) = maStep p.Vwma60Px p.Vwma60Sec prevXVw60
            let p =
                { p with
                    Ma10Px = m10p; Ma10Sec = m10s
                    Ma20Px = m20p; Ma20Sec = m20s
                    Ma30Px = m30p; Ma30Sec = m30s
                    Ma40Px = m40p; Ma40Sec = m40s
                    Ma50Px = m50p; Ma50Sec = m50s
                    Ma60Px = m60p; Ma60Sec = m60s
                    Vwma10Px = v10p; Vwma10Sec = v10s
                    Vwma20Px = v20p; Vwma20Sec = v20s
                    Vwma30Px = v30p; Vwma30Sec = v30s
                    Vwma40Px = v40p; Vwma40Sec = v40s
                    Vwma50Px = v50p; Vwma50Sec = v50s
                    Vwma60Px = v60p; Vwma60Sec = v60s }
            let p =
                match p.State with
                | Holding | PendingExit _ -> { p with BarsHeld = p.BarsHeld + 1 }
                | ExitedAt _ -> p
            let p =
                match p.State with
                | Holding ->
                    if bar.etSec >= mocSec then
                        // the 16:00 bar IS the auction-proximate print — fill here, not next bar
                        { p with State = ExitedAt (bar.etSec, bar.vwap, "moc") }
                    elif volStopHit then { p with State = PendingExit "vol_stop" }
                    elif tcStopHit then { p with State = PendingExit "tc_stop" }
                    elif speedStopHit then { p with State = PendingExit "speed_stop" }
                    elif targetHit then { p with State = PendingExit "target" }
                    else p
                | _ -> p
            // retire when exited AND the last (+1200s) mark has filled — a bar
            // that fills the 1200s mark also fills the 60/300/600 ones — AND no
            // aux mark is about to fill off THIS bar's high (an unset mark whose
            // counter just hit 0 fills next bar; retiring now would lose it)
            match p.State with
            | ExitedAt _ when not (Double.IsNaN p.FwdVwap1200)
                              && not (Double.IsNaN p.AuxHi60 && br60.BarsSinceBreach = 0)
                              && not (Double.IsNaN p.AuxHi120 && br120.BarsSinceBreach = 0)
                              && not (Double.IsNaN p.AuxHi300 && br300.BarsSinceBreach = 0)
                              && not (Double.IsNaN p.AuxHi600 && br600.BarsSinceBreach = 0)
                              && not (Double.IsNaN p.AuxHi1200 && br1200.BarsSinceBreach = 0)
                              // MA-exit marks: retire only fully resolved (they
                              // resolve by the MOC bar at the latest)
                              && not (Double.IsNaN p.Ma10Px) && not (Double.IsNaN p.Ma20Px)
                              && not (Double.IsNaN p.Ma30Px) && not (Double.IsNaN p.Ma40Px)
                              && not (Double.IsNaN p.Ma50Px) && not (Double.IsNaN p.Ma60Px)
                              && not (Double.IsNaN p.Vwma10Px) && not (Double.IsNaN p.Vwma20Px)
                              && not (Double.IsNaN p.Vwma30Px) && not (Double.IsNaN p.Vwma40Px)
                              && not (Double.IsNaN p.Vwma50Px) && not (Double.IsNaN p.Vwma60Px) ->
                retired.Add p
            | _ ->
                active.[w] <- p
                w <- w + 1
        if w < active.Count then active.RemoveRange(w, active.Count - w)

        // ===== 6. entry signal (fills next bar) =====
        let inWindow = bar.etSec >= cfg.EntryStartSec && bar.etSec <= entryEndSec
        let channelWarm = entryMin.Count = entryMin.WindowSize
        let floorsOk =
            (match dvSum60.State with ValueSome dv -> dv >= cfg.DvFloor60 | ValueNone -> false)
            && (match tcSum60.State with ValueSome tc -> tc >= cfg.TcFloor60 | ValueNone -> false)
        // the volatility band: floor AND ceiling (record-first — see the config comment)
        let volatOk =
            (cfg.MinVolat20m <= 0.0
             || (match ew40.State with ValueSome v -> v >= cfg.MinVolat20m | ValueNone -> false))
            && (Double.IsPositiveInfinity cfg.MaxVolat20m
                || (match ew40.State with ValueSome v -> v < cfg.MaxVolat20m | ValueNone -> true))
        // ⭐ SPEC v1.2 GATES — expressions mirror the recorded columns exactly, so an
        // engine-gated run must bit-match the post-hoc SQL on the same trips (S19).
        let speedOk =
            cfg.MaxSpeed1m >= 0.0
            || (match vwap60Lag.Lagged with
                | ValueSome pv when pv > 0.0 -> bar.vwap / pv - 1.0 < cfg.MaxSpeed1m
                | _ -> false)
        // ⭐ SPEC v1.9 (S40g): post-push max60, identical to the recorded hi_60
        // (no fullness requirement — the 1200-bar signal channel guarantees warmth).
        let d1mOk =
            cfg.MaxDist1mHi >= 0.0
            || (match max60.State with
                | ValueSome h when h > 0.0 -> bar.vwap / h - 1.0 < cfg.MaxDist1mHi
                | _ -> false)
        // ⭐ SPEC v2.1 (S40y): reject the perfect-line flush. Unwarm r fails.
        let rsfOk =
            cfg.MinRSinceFlow <= -1.0
            || (let r = olsSinceFlow.R
                not (Double.IsNaN r) && r >= cfg.MinRSinceFlow)
        // ⭐ SPEC v2.3 (S41r): same ln-moment sums as the recorded dlv_1200/dlv2_1200.
        let z20Ok =
            cfg.MaxZ20m >= 0.0
            || (match dlvSum1200.State, dlv2Sum1200.State, volSum1200.State with
                | ValueSome dl, ValueSome dl2, ValueSome v
                    when volSum1200.Count = volSum1200.WindowSize && v > 0.0 ->
                    let mean = dl / v
                    let var = dl2 / v - mean * mean
                    var > 0.0 && (log bar.vwap - mean) / sqrt var < cfg.MaxZ20m
                | _ -> false)
        // ⭐ SPEC v2.4/v2.5: the cascade-knife gate — no fading the LULD
        // elevator. Mirrors the recorded halts_today / secs_since_halt exactly.
        //
        // Stated as the rule reads: HOW LONG must the tape run after a resume
        // before we are willing to fade it? A case analysis on the halt count,
        // whose three cases PARTITION ht (so they cannot overlap):
        //     ht = 0        never halted today  ->  no wait
        //     1 <= ht < N   ordinary halt       ->  ReopenBlockSec    (2m)
        //     ht >= N       serial breaker      ->  CascadeWindowSec  (20m)
        // ⚠ The middle case's UPPER bound is load-bearing. Written without it,
        // as `(ht>=N & ssh>=20m) || (ht>=1 & ssh>=2m) || ht=0`, the weaker
        // clause is satisfied by ht>=N names too and re-admits every serial
        // breaker at ssh in [2m,20m) — 568 trips, the PF~1.5 core of exactly
        // what this gate exists to remove.
        // Each knob is independently switchable (0 = that rule off) and
        // --base-run zeroes both, which zeroes every wait.
        let cascadeOk =
            let requiredWait =
                if haltsToday = 0 then 0
                elif cfg.CascadeHaltCount > 0 && haltsToday >= cfg.CascadeHaltCount then cfg.CascadeWindowSec
                else cfg.ReopenBlockSec
            requiredWait <= 0 || bar.etSec - lastHaltEnd >= requiredWait
        // ⭐ SPEC v2.2 (S41c/d): the leg-native pair. Same slope/leg-cum sources
        // as the recorded ols_slope_since_flow and dv_leg/vol_leg (SQL twins).
        let ssfOk =
            let s = olsSinceFlow.Slope * 6e5
            (Double.IsNegativeInfinity cfg.SsfLoBpm
             || (not (Double.IsNaN s) && s >= cfg.SsfLoBpm))
            && (cfg.SsfHiBpm >= 0.0
                || (not (Double.IsNaN s) && s < cfg.SsfHiBpm))
        let dlvOk =
            cfg.MaxDistLegVwap >= 0.0
            || (let v = cumVol - legVolAnchor
                let dv = cumDv - legDvAnchor
                not (Double.IsNaN v) && v > 0.0 && dv > 0.0
                && bar.vwap / (dv / v) - 1.0 < cfg.MaxDistLegVwap)
        let kBandOk =
            (cfg.KBandLo <= 0 || counters.EventsSinceFirst >= cfg.KBandLo)
            && (cfg.KBandHi <= 0 || counters.EventsSinceFirst <= cfg.KBandHi)
        let eff20Signed =
            match slotLag.Last, slotLag.Lagged, slotAbsSum.State with
            | ValueSome cur, ValueSome old, ValueSome s
                when slotAbsSum.Count = slotAbsSum.WindowSize && old > 0.0 && s > 0.0 ->
                ValueSome (log (cur / old) / s)
            | _ -> ValueNone
        let eff20BandOk =
            // ⭐ S38n (SPEC v1.6): COLD eff_20m FAILS an armed edge — standard stance.
            // Cold-at-signal = the one-slot warm-up gap (channel warm at 1,200 bars,
            // eff at ~1,230) — a weak early-morning thin-tape fringe (471 trips @
            // 1.78, hurting 2023) — or a degenerate dead tape (Σ|r| = 0). S40i
            // redesign: ABSOLUTE band |eff_20m| ∈ [lo, hi) — SQL twin:
            // abs(eff_20m) >= 0.3 AND abs(eff_20m) < 0.5.
            (cfg.AbsEff20Lo <= 0.0
             || (match eff20Signed with ValueSome e -> abs e >= cfg.AbsEff20Lo | ValueNone -> false))
            && (Double.IsPositiveInfinity cfg.AbsEff20Hi
                || (match eff20Signed with ValueSome e -> abs e < cfg.AbsEff20Hi | ValueNone -> false))
        let eff10Ok =
            cfg.MinAbsEff10m <= 0.0
            || (match slotLag20.Last, slotLag20.Lagged, slotAbsSum20.State with
                | ValueSome cur, ValueSome old, ValueSome s
                    when slotAbsSum20.Count = slotAbsSum20.WindowSize && old > 0.0 && s > 0.0 ->
                    abs (log (cur / old) / s) >= cfg.MinAbsEff10m
                | _ -> false)
        // ⭐ S43al: the eff_9ema_10m whipsaw knife. Same window and warmth guard
        // as eff10Ok (slotSgnSum20 and slotAbsSum20 are pushed on identical
        // slots, so one Count check covers both); cold FAILS, the standard
        // armed-gate stance. SQL twin: eff_9ema_10m >= -0.10.
        let eff9Ok =
            Double.IsNegativeInfinity cfg.MinEff9Ema10m
            || (match slotSgnSum20.State, slotAbsSum20.State with
                | ValueSome num, ValueSome den
                    when slotAbsSum20.Count = slotAbsSum20.WindowSize && den > 0.0 ->
                    num / den >= cfg.MinEff9Ema10m
                | _ -> false)
        let vol10Ok =
            cfg.MinVol10Rate <= 0.0
            || (match volSum10.State, volSum60.State with
                | ValueSome v10, ValueSome v60 when v60 > 0.0 ->
                    (v10 / 10.0) / (v60 / 60.0) >= cfg.MinVol10Rate
                | _ -> false)
        let dv0945TapeOk = cfg.MinDv0945Tape <= 0.0 || dv0945Tape >= cfg.MinDv0945Tape
        let lows300Ok = cfg.MinLows300 <= 0 || counters300.EventsSinceFirst >= cfg.MinLows300
        let lows180Ok = cfg.MinLows180 <= 0 || counters180.EventsSinceFirst >= cfg.MinLows180
        let frontOk =
            Double.IsPositiveInfinity cfg.MaxRngFront
            || chanRng max300 min300 / chanRng max1200 min1200 < cfg.MaxRngFront
        // ⭐ SPEC v1.7 (S39o/r): OLS gates. ols1200 warms exactly with the entry
        // channel (both need 1200 present bars) so at any signal both slopes are
        // warm; the ValueNone arms are pure defense.
        let accelOk =
            Double.IsNegativeInfinity cfg.MinAccel1020Bpm
            || (match ols600.State, ols1200.State with
                | ValueSome m6, ValueSome m12
                    when ols600.Count = ols600.WindowSize && ols1200.Count = ols1200.WindowSize ->
                    (m6 - m12) * 6e5 >= cfg.MinAccel1020Bpm
                | _ -> false)
        let slope20Ok =
            cfg.MaxSlope20Bpm >= 0.0
            || (match ols1200.State with
                | ValueSome m when ols1200.Count = ols1200.WindowSize -> m * 6e5 < cfg.MaxSlope20Bpm
                | _ -> false)
        let slope5Ok =
            Double.IsNegativeInfinity cfg.MinSlope5Bpm
            || (match ols300.State with
                | ValueSome m when ols300.Count = ols300.WindowSize -> m * 6e5 >= cfg.MinSlope5Bpm
                | _ -> false)
        let specOk = speedOk && d1mOk && ssfOk && dlvOk && rsfOk && z20Ok && cascadeOk && kBandOk && eff20BandOk && eff10Ok && eff9Ok && vol10Ok && dv0945TapeOk && lows300Ok && lows180Ok && frontOk && accelOk && slope20Ok && slope5Ok
        if inWindow && channelWarm && isNewLow && floorsOk && volatOk && specOk && this.HasSlot then
            let struct (vs20m, vr20m) = volatOlsRead volatOls20m
            let struct (vs10m, vr10m) = volatOlsRead volatOls10m
            let struct (vs5m, vr5m) = volatOlsRead volatOls5m
            let struct (vs3m, vr3m) = volatOlsRead volatOls3m
            let eolsRead (o: EwmaOlsMa) =
                match o.State with
                | ValueSome (struct (sl, r)) -> struct (sl, r)
                | ValueNone -> struct (nan, nan)
            let struct (evs20m, evr20m) = eolsRead volatEols20m
            let struct (evs10m, evr10m) = eolsRead volatEols10m
            let struct (evs5m, evr5m) = eolsRead volatEols5m
            let struct (evs3m, evr3m) = eolsRead volatEols3m
            pendingEntry <-
                ValueSome
                    { SignalSec = bar.etSec
                      SignalVwap = bar.vwap
                      EntrySec = -1                  // filled next bar (step 3)
                      EntryPx = nan
                      Volat20m = vv ew40.State
                      Volat10m = vv ew20.State
                      Rng20m = chanRng max1200 min1200
                      Eff20m =
                        (match slotLag.Last, slotLag.Lagged, slotAbsSum.State with
                         | ValueSome cur, ValueSome old, ValueSome s
                             when slotAbsSum.Count = slotAbsSum.WindowSize && old > 0.0 && s > 0.0 ->
                             log (cur / old) / s
                         | _ -> nan)
                      Eff10m =
                        (match slotLag20.Last, slotLag20.Lagged, slotAbsSum20.State with
                         | ValueSome cur, ValueSome old, ValueSome s
                             when slotAbsSum20.Count = slotAbsSum20.WindowSize && old > 0.0 && s > 0.0 ->
                             log (cur / old) / s
                         | _ -> nan)
                      SlotCount = slotReturns
                      RngSlots20m =
                        (match slotMax41.State, slotMin41.State with
                         | ValueSome h, ValueSome l
                             when slotMax41.Count = slotMax41.WindowSize && l > 0.0 -> log (h / l)
                         | _ -> nan)
                      RngSlots10m =
                        (match slotMax21.State, slotMin21.State with
                         | ValueSome h, ValueSome l
                             when slotMax21.Count = slotMax21.WindowSize && l > 0.0 -> log (h / l)
                         | _ -> nan)
                      EffRng20m =
                        (match slotMax41.State, slotMin41.State, slotAbsSum.State with
                         | ValueSome h, ValueSome l, ValueSome s
                             when slotMax41.Count = slotMax41.WindowSize
                                  && slotAbsSum.Count = slotAbsSum.WindowSize
                                  && l > 0.0 && s > 0.0 ->
                             log (h / l) / s
                         | _ -> nan)
                      EffRng10m =
                        (match slotMax21.State, slotMin21.State, slotAbsSum20.State with
                         | ValueSome h, ValueSome l, ValueSome s
                             when slotMax21.Count = slotMax21.WindowSize
                                  && slotAbsSum20.Count = slotAbsSum20.WindowSize
                                  && l > 0.0 && s > 0.0 ->
                             log (h / l) / s
                         | _ -> nan)
                      EffRngLin20m =
                        (match slotMax41.State, slotMin41.State, slotAbsSumLin.State with
                         | ValueSome h, ValueSome l, ValueSome s
                             when slotMax41.Count = slotMax41.WindowSize
                                  && slotAbsSumLin.Count = slotAbsSumLin.WindowSize
                                  && s > 0.0 ->
                             (h - l) / s
                         | _ -> nan)
                      EffRngLin10m =
                        (match slotMax21.State, slotMin21.State, slotAbsSumLin20.State with
                         | ValueSome h, ValueSome l, ValueSome s
                             when slotMax21.Count = slotMax21.WindowSize
                                  && slotAbsSumLin20.Count = slotAbsSumLin20.WindowSize
                                  && s > 0.0 ->
                             (h - l) / s
                         | _ -> nan)
                      // S43al: slotSgnSum and slotAbsSum are pushed on exactly the
                      // same slots, so their Counts are identical — one warmth
                      // guard covers both, and it is the SAME guard eff_20m uses.
                      Eff9Ema20m =
                        (match slotSgnSum.State, slotAbsSum.State with
                         | ValueSome n, ValueSome d
                             when slotAbsSum.Count = slotAbsSum.WindowSize && d > 0.0 -> n / d
                         | _ -> nan)
                      Eff9Ema10m =
                        (match slotSgnSum20.State, slotAbsSum20.State with
                         | ValueSome n, ValueSome d
                             when slotAbsSum20.Count = slotAbsSum20.WindowSize && d > 0.0 -> n / d
                         | _ -> nan)
                      RngSess =
                        (match sessHigh.State, sessLow.State with
                         | ValueSome h, ValueSome l when l > 0.0 -> log (h / l)
                         | _ -> nan)
                      Rng600 = chanRng max600 min600
                      Rng300 = chanRng max300 min300
                      Rng120 = chanRng max120 min120
                      Rng60 = chanRng max60 min60
                      Rng30 = chanRng max30 min30
                      Hi60 = vv max60.State
                      BreachSess = brSess.BarsSinceBreach
                      Breach1200 = br1200.BarsSinceBreach
                      Breach600 = br600.BarsSinceBreach
                      Breach300 = br300.BarsSinceBreach
                      Breach120 = br120.BarsSinceBreach
                      Breach60 = br60.BarsSinceBreach
                      Breach30 = br30.BarsSinceBreach
                      BreachLoSess = brLoSess.BarsSinceBreach
                      BreachLo1200 = brLo1200.BarsSinceBreach
                      BreachLo600 = brLo600.BarsSinceBreach
                      BreachLo300 = brLo300.BarsSinceBreach
                      BreachLo120 = brLo120.BarsSinceBreach
                      BreachLo60 = brLo60.BarsSinceBreach
                      BreachLo30 = brLo30.BarsSinceBreach
                      // ⭐ read the counters NOW — the reset in step 7 must not
                      // affect this trip. The entry bar IS a low, so both >= 0.
                      BarsSinceFirstLow = counters.BarsSinceFirst
                      SecsSinceFirstLow = counters.SecsSinceFirst bar.etSec
                      LowsSinceFirstLow = counters.EventsSinceFirst
                      BarsSinceFirstLow300 = counters300.BarsSinceFirst
                      LowsSinceFirstLow300 = counters300.EventsSinceFirst
                      BarsSinceFirstLow600 = counters600.BarsSinceFirst
                      LowsSinceFirstLow600 = counters600.EventsSinceFirst
                      LowsSinceFirstLow120 = counters120.EventsSinceFirst
                      LowsSinceFirstLow180 = counters180.EventsSinceFirst
                      Volat5m = vv ew10.State
                      Volat3m = vv ew6.State
                      VwapEwp12060Be = wdVwap pxDecayB120.Sum pxDecayB120.Weight pxDecayB60.Sum pxDecayB60.Weight
                      VwapEwp12030Be = wdVwap pxDecayB120.Sum pxDecayB120.Weight pxDecayB30.Sum pxDecayB30.Weight
                      VwapEwp6030Be = wdVwap pxDecayB60.Sum pxDecayB60.Weight pxDecayB30.Sum pxDecayB30.Weight
                      Ac1Ewma = acEwma.Rho 1
                      Ac2Ewma = acEwma.Rho 2
                      Ac3Ewma = acEwma.Rho 3
                      VolatSlope5m = vs5m
                      VolatR5m = vr5m
                      VolatSlope3m = vs3m
                      VolatR3m = vr3m
                      VolatEslope20m = evs20m
                      VolatEr20m = evr20m
                      VolatEslope10m = evs10m
                      VolatEr10m = evr10m
                      VolatEslope5m = evs5m
                      VolatEr5m = evr5m
                      VolatEslope3m = evs3m
                      VolatEr3m = evr3m
                      TradeIdx = tradeIdx
                      OpenAtSignal = this.OpenCount
                      Vwap1200 =
                        (if volSum1200.Count = volSum1200.WindowSize then
                            match dvSum1200.State, volSum1200.State with
                            | ValueSome dv, ValueSome v when v > 0.0 -> dv / v
                            | _ -> nan
                         else nan)
                      ChanHi = vv priorEntryMax
                      ChanLo = vv priorEntryMin
                      ExitChanHi = vv sExitMax
                      Gap60 = gap60.Gaps
                      Gap30 = gap30.Gaps
                      Gap15 = gap15.Gaps
                      Gap300 = gap300.Gaps
                      Gap600 = gap600.Gaps
                      Gap1200 = gap1200.Gaps
                      MaxGapRun1200 = vv gapRunMax1200.State
                      MaxGapRun300 = vv gapRunMax300.State
                      BigGapRuns1200 = vv bigGapRuns1200.State
                      OlsSlopeSinceHigh = olsSinceHigh.Slope
                      OlsRSinceHigh = olsSinceHigh.R
                      BarsSinceHigh = olsSinceHigh.Count
                      OlsSlopeSinceFlow = olsSinceFlow.Slope
                      OlsRSinceFlow = olsSinceFlow.R
                      EffSinceHigh = effSinceHigh.Eff
                      EffSinceFlow = effSinceFlow.Eff
                      Eff9SinceHigh = effSinceHigh.Eff9Ema
                      Eff9SinceFlow = effSinceFlow.Eff9Ema
                      ZSinceHigh = effSinceHigh.Z bar.vwap
                      ZSinceFlow = effSinceFlow.Z bar.vwap
                      ChanLoPrev = (match entryMinLag.Lagged with ValueSome v -> v | ValueNone -> nan)
                      ChanLoPrev120 = (match entryMinLag120.Lagged with ValueSome v -> v | ValueNone -> nan)
                      Hi120 = vv max120.State
                      ChanLoPrev180 = (match entryMinLag180.Lagged with ValueSome v -> v | ValueNone -> nan)
                      Hi180 = vv max180.State
                      SlotsSinceHigh = effSinceHigh.Slots
                      SlotsSinceFlow = effSinceFlow.Slots
                      DHiFlow = dropToFlow
                      OlsSlopeHiFlow = dropSlopeHiFlow
                      OlsRHiFlow = dropRHiFlow
                      EffHiFlow = dropEffHiFlow
                      ArmHiEff20m = armHiEff20
                      ArmHiEff10m = armHiEff10
                      ArmHiSlots20m = armHiSlots20
                      ArmHiSlots10m = armHiSlots10
                      FirstLowVwap = firstLowVwap
                      BarsAboveSvwap = barsAboveSvwap
                      BarsPresent = barsPresent
                      SessLow = (match sessLow.State with ValueSome l -> l | ValueNone -> nan)
                      SessHigh = (match sessHigh.State with ValueSome h -> h | ValueNone -> nan)
                      SessDv2 = cumDv2
                      SessDlv = cumDlv
                      SessDlv2 = cumDlv2
                      Dv2300 = vv dv2Sum300.State
                      Dlv300 = vv dlvSum300.State
                      Dlv2300 = vv dlv2Sum300.State
                      Dv2600 = vv dv2Sum600.State
                      Dlv600 = vv dlvSum600.State
                      Dlv2600 = vv dlv2Sum600.State
                      Dv21200 = vv dv2Sum1200.State
                      Dlv1200 = vv dlvSum1200.State
                      Dlv21200 = vv dlv2Sum1200.State
                      GapAdj60 = adjGap60
                      GapAdj300 = adjGap300
                      GapAdj600 = adjGap600
                      GapAdj1200 = adjGap1200
                      // ⭐ S43bk: the signal bar's OWN tick is already folded in
                      // (step 2 runs before the signal test), so a signal bar
                      // that ticked down reads DownticksSinceUptick >= 1.
                      DownticksSinceUptick = dnSinceUp
                      RunDownticks = dnRun
                      SecsSinceLastUptick = (if lastUptickSec < 0 then -1 else bar.etSec - lastUptickSec)
                      Dn15 = (match dnSum15.State with ValueSome v -> int v | ValueNone -> 0)
                      Dn30 = (match dnSum30.State with ValueSome v -> int v | ValueNone -> 0)
                      Dn60 = (match dnSum60.State with ValueSome v -> int v | ValueNone -> 0)
                      Dn120 = (match dnSum120.State with ValueSome v -> int v | ValueNone -> 0)
                      Up15 = (match upSum15.State with ValueSome v -> int v | ValueNone -> 0)
                      Up30 = (match upSum30.State with ValueSome v -> int v | ValueNone -> 0)
                      Up60 = (match upSum60.State with ValueSome v -> int v | ValueNone -> 0)
                      Up120 = (match upSum120.State with ValueSome v -> int v | ValueNone -> 0)
                      Gap120 = gap120.Gaps
                      DownticksSinceFlow = dnFlow
                      UpticksSinceFlow = upFlow
                      LowsSinceUptick = lowsSinceUp
                      ChgSinceLastUptick =
                        (if Double.IsNaN lastUptickVwap || lastUptickVwap <= 0.0 then nan
                         else bar.vwap / lastUptickVwap - 1.0)
                      ChgSinceRunPreLow =
                        (if Double.IsNaN runPreLowVwap || runPreLowVwap <= 0.0 then nan
                         else bar.vwap / runPreLowVwap - 1.0)
                      ChgSinceRunFirstLow =
                        (if Double.IsNaN runFirstLowVwap || runFirstLowVwap <= 0.0 then nan
                         else bar.vwap / runFirstLowVwap - 1.0)
                      ChgSinceRunFirstDn =
                        (if Double.IsNaN runFirstDnVwap || runFirstDnVwap <= 0.0 then nan
                         else bar.vwap / runFirstDnVwap - 1.0)
                      HaltsToday = haltsToday
                      Halts1200 = (match haltSum1200.State with ValueSome v -> int v | ValueNone -> 0)
                      Halts600 = (match haltSum600.State with ValueSome v -> int v | ValueNone -> 0)
                      SecsSinceHalt = (if lastHaltEnd < 0 then -1 else bar.etSec - lastHaltEnd)
                      SessVwap = vv sessVwap.State
                      DistSessVwap =
                        (match sessVwap.State with
                         | ValueSome sv when sv > 0.0 -> log (bar.vwap / sv)
                         | _ -> nan)
                      PctChgOpen =
                        (match openVwap with
                         | ValueSome o when o > 0.0 -> bar.vwap / o - 1.0
                         | _ -> nan)
                      BarVol = bar.volume
                      BarTc = bar.tradeCount
                      Vol5 = vv volSum5.State
                      Vol10 = vv volSum10.State
                      Vol15 = vv volSum15.State
                      Vol30 = vv volSum30.State
                      Vol60 = vv volSum60.State
                      Vol300 = vv volSum300.State
                      Vol600 = vv volSum600.State
                      Vol1200 = vv volSum1200.State
                      Tc5 = vv tcSum5.State
                      Tc10 = vv tcSum10.State
                      Tc15 = vv tcSum15.State
                      Tc30 = vv tcSum30.State
                      Tc60 = vv tcSum60.State
                      Tc300 = vv tcSum300.State
                      Tc600 = vv tcSum600.State
                      Tc1200 = vv tcSum1200.State
                      Vol60Prev = vv vol60Lag.Lagged
                      Tc60Prev = vv tc60Lag.Lagged
                      Vwap60 = vv vwap60Now
                      Vwap5Prev = vv vwap5Lag.Lagged
                      Vwap10Prev = vv vwap10Lag.Lagged
                      Vwap30Prev = vv vwap30Lag.Lagged
                      Vwap30 = sumRatio dvSum30 volSum30
                      Vwap120 = sumRatio dvSum120 volSum120
                      Vwap180 = sumRatio dvSum180 volSum180
                      Hi30 = vv max30.State
                      Vwap60Prev = vv vwap60Lag.Lagged
                      DollarVol60 = vv dvSum60.State
                      DollarVol300 = vv dvSum300.State
                      DollarVol600 = vv dvSum600.State
                      DollarVol1200 = vv dvSum1200.State
                      CumVol = cumVol
                      CumDv = cumDv
                      Vol0945Tape = vol0945Tape
                      Dv0945Tape = dv0945Tape
                      // S40l tier-2: the signal bar IS a leg low, so the anchors
                      // are always set here (nan can only appear on non-signal
                      // reads, which never reach the parquet).
                      VolLeg = cumVol - legVolAnchor
                      TcLeg = cumTc - legTcAnchor
                      DvLeg = cumDv - legDvAnchor
                      TargetsToday = targetsToday
                      Volat20mPrev = vv volatLag.Lagged
                      VolatSlope20m = vs20m
                      VolatR20m = vr20m
                      VolatSlope10m = vs10m
                      VolatR10m = vr10m
                      OlsSlope60 =
                        (match ols60.State with
                         | ValueSome m when ols60.Count = ols60.WindowSize -> m | _ -> nan)
                      OlsR60 =
                        (match ols60.State, ols60.R2 with
                         | ValueSome m, ValueSome r2 when ols60.Count = ols60.WindowSize ->
                             (if m < 0.0 then -sqrt r2 else sqrt r2)
                         | _ -> nan)
                      OlsSlope120 =
                        (match ols120.State with
                         | ValueSome m when ols120.Count = ols120.WindowSize -> m | _ -> nan)
                      OlsR120 =
                        (match ols120.State, ols120.R2 with
                         | ValueSome m, ValueSome r2 when ols120.Count = ols120.WindowSize ->
                             (if m < 0.0 then -sqrt r2 else sqrt r2)
                         | _ -> nan)
                      OlsSlope180 =
                        (match ols180.State with
                         | ValueSome m when ols180.Count = ols180.WindowSize -> m | _ -> nan)
                      OlsR180 =
                        (match ols180.State, ols180.R2 with
                         | ValueSome m, ValueSome r2 when ols180.Count = ols180.WindowSize ->
                             (if m < 0.0 then -sqrt r2 else sqrt r2)
                         | _ -> nan)
                      OlsSlope300 =
                        (match ols300.State with
                         | ValueSome m when ols300.Count = ols300.WindowSize -> m
                         | _ -> nan)
                      OlsR300 =
                        (match ols300.State, ols300.R2 with
                         | ValueSome m, ValueSome r2 when ols300.Count = ols300.WindowSize ->
                             (if m < 0.0 then -sqrt r2 else sqrt r2)
                         | _ -> nan)
                      OlsSlope600 =
                        (match ols600.State with
                         | ValueSome m when ols600.Count = ols600.WindowSize -> m
                         | _ -> nan)
                      OlsR600 =
                        (match ols600.State, ols600.R2 with
                         | ValueSome m, ValueSome r2 when ols600.Count = ols600.WindowSize ->
                             (if m < 0.0 then -sqrt r2 else sqrt r2)
                         | _ -> nan)
                      OlsSlope1200 =
                        (match ols1200.State with
                         | ValueSome m when ols1200.Count = ols1200.WindowSize -> m
                         | _ -> nan)
                      OlsR1200 =
                        (match ols1200.State, ols1200.R2 with
                         | ValueSome m, ValueSome r2 when ols1200.Count = ols1200.WindowSize ->
                             (if m < 0.0 then -sqrt r2 else sqrt r2)
                         | _ -> nan)
                      NEffShannon600 =
                        (if neff600Count = 600 then let struct (_, s) = neff600.Query in s.Value else nan)
                      NEffHhi600 =
                        (if neff600Count = 600 then let struct (h, _) = neff600.Query in h.Value else nan)
                      NEffShannon1200 =
                        (if neff1200Count = 1200 then let struct (_, s) = neff1200.Query in s.Value else nan)
                      NEffHhi1200 =
                        (if neff1200Count = 1200 then let struct (h, _) = neff1200.Query in h.Value else nan)
                      NEffShannon60 =
                        (if neff60Count = 60 then let struct (_, s) = neff60.Query in s.Value else nan)
                      NEffHhi60 =
                        (if neff60Count = 60 then let struct (h, _) = neff60.Query in h.Value else nan)
                      NEffShannon300 =
                        (if neff300Count = 300 then let struct (_, s) = neff300.Query in s.Value else nan)
                      NEffHhi300 =
                        (if neff300Count = 300 then let struct (h, _) = neff300.Query in h.Value else nan)
                      NEffRet20m = (if neffRet40Count = 40 then neffRet40.Query.Value else nan)
                      NEffRet10m = (if neffRet20Count = 20 then neffRet20.Query.Value else nan)
                      CumTc = cumTc
                      FwdVwap60 = nan
                      FwdVwap300 = nan
                      FwdVwap600 = nan
                      FwdVwap1200 = nan
                      AuxHi60 = nan
                      AuxSec60 = -1
                      AuxHi120 = nan
                      AuxSec120 = -1
                      AuxHi300 = nan
                      AuxSec300 = -1
                      AuxHi600 = nan
                      AuxSec600 = -1
                      AuxHi1200 = nan
                      AuxSec1200 = -1
                      Ma10Px = nan
                      Ma10Sec = -1
                      Ma20Px = nan
                      Ma20Sec = -1
                      Ma30Px = nan
                      Ma30Sec = -1
                      Ma40Px = nan
                      Ma40Sec = -1
                      Ma50Px = nan
                      Ma50Sec = -1
                      Ma60Px = nan
                      Ma60Sec = -1
                      Vwma10Px = nan
                      Vwma10Sec = -1
                      Vwma20Px = nan
                      Vwma20Sec = -1
                      Vwma30Px = nan
                      Vwma30Sec = -1
                      Vwma40Px = nan
                      Vwma40Sec = -1
                      Vwma50Px = nan
                      Vwma50Sec = -1
                      Vwma60Px = nan
                      Vwma60Sec = -1
                      BarsHeld = 0
                      State = Holding }
            // ⭐ the trade counter advances on INITIATION (the signal), whether or
            // not the fill materializes — the (rare) end-of-tape dropped pending
            // entry still consumed its place in the leg's sequence.
            tradeIdx <- tradeIdx + 1

        // ===== 7. the leg reset fires LAST: a new entry-channel high ends the
        // down-leg. AFTER the entry block — an entry on this bar reads the
        // pre-reset counters (V6's ordering). isNewHigh and isNewLow are
        // mutually exclusive (prior max >= prior min), so the reset can never
        // clobber the very leg an entry just joined. =====
        if isNewHigh then
            counters.Reset()
            tradeIdx <- 0
            // S40l: the leg is over — clear the leg-participation anchors.
            legVolAnchor <- nan
            legTcAnchor <- nan
            legDvAnchor <- nan
            dropToFlow <- nan
            dropSlopeHiFlow <- nan
            dropRHiFlow <- nan
            firstLowVwap <- nan
            // S43bn: the leg is over — disarm the leg tick totals.
            dnFlow <- -1
            upFlow <- -1
            // S40y/z: the anchored OLS + eff reset with the leg (sinceHigh
            // re-anchors at the bar after this high; sinceFlow waits for the
            // next first low).
            olsSinceHigh.Reset()
            olsSinceFlow.Reset()
            effSinceHigh.Reset()
            effSinceFlow.Reset()
        // ⭐ S38e tighter resets: the breach bar reads 0 (OnBreach fired in
        // step 4). A new 5m/10m high can't share a bar with an entry either
        // (vwap < prior min1200 <= prior max300/600), so ordering is safe; a
        // 20m-high bar implies both, keeping all three counters in sync.
        if br300.BarsSinceBreach = 0 then counters300.Reset()
        if br600.BarsSinceBreach = 0 then counters600.Reset()
        // S43cf: the 2m/3m rungs of the same ladder (a 20m-high bar implies all)
        if br120.BarsSinceBreach = 0 then counters120.Reset()
        if br180.BarsSinceBreach = 0 then counters180.Reset()
        // the aux-mark lookback: remember this bar as "the previous bar"
        // (+ S40x pre-hole snapshots: this bar's adjusted 1m gap and post-push
        // 5m range become the classifier inputs if the NEXT bar reveals a hole)
        prevAdjGap60 <- adjGap60
        prevRng300 <- chanRng max300 min300
        prevEtSec <- bar.etSec
        prevVwap <- bar.vwap
        prevXMa10 <- not (Double.IsNaN ma10) && bar.vwap > ma10
        prevXMa20 <- not (Double.IsNaN ma20) && bar.vwap > ma20
        prevXMa30 <- not (Double.IsNaN ma30) && bar.vwap > ma30
        prevXMa40 <- not (Double.IsNaN ma40) && bar.vwap > ma40
        prevXMa50 <- not (Double.IsNaN ma50) && bar.vwap > ma50
        prevXMa60 <- not (Double.IsNaN ma60) && bar.vwap > ma60
        prevXVw10 <- not (Double.IsNaN vwma10) && bar.vwap > vwma10
        prevXVw20 <- not (Double.IsNaN vwma20) && bar.vwap > vwma20
        prevXVw30 <- not (Double.IsNaN vwma30) && bar.vwap > vwma30
        prevXVw40 <- not (Double.IsNaN vwma40) && bar.vwap > vwma40
        prevXVw50 <- not (Double.IsNaN vwma50) && bar.vwap > vwma50
        prevXVw60 <- not (Double.IsNaN vwma60) && bar.vwap > vwma60

    /// Flatten at the tape's last bar: fill any pending exit and force-exit any
    /// holder at the last vwap ("moc" — covers early closes and thin tapes whose
    /// last print lands before MocSec). A pending ENTRY that never filled is
    /// dropped — there was no bar to trade into.
    member _.Flatten (lastBar: SecBar) =
        pendingEntry <- ValueNone
        for i in 0 .. active.Count - 1 do
            let p = active.[i]
            let p =
                match p.State with
                | Holding | PendingExit _ -> { p with State = ExitedAt (lastBar.etSec, lastBar.vwap, "moc") }
                | ExitedAt _ -> p
            // MA-exit marks: resolve stragglers at the day's last bar (early
            // closes / thin tapes whose final print lands before MocSec)
            let inline fin px = if Double.IsNaN px then lastBar.vwap else px
            let inline finSec px sec = if Double.IsNaN px then lastBar.etSec else sec
            active.[i] <-
                { p with
                    Ma10Sec = finSec p.Ma10Px p.Ma10Sec; Ma10Px = fin p.Ma10Px
                    Ma20Sec = finSec p.Ma20Px p.Ma20Sec; Ma20Px = fin p.Ma20Px
                    Ma30Sec = finSec p.Ma30Px p.Ma30Sec; Ma30Px = fin p.Ma30Px
                    Ma40Sec = finSec p.Ma40Px p.Ma40Sec; Ma40Px = fin p.Ma40Px
                    Ma50Sec = finSec p.Ma50Px p.Ma50Sec; Ma50Px = fin p.Ma50Px
                    Ma60Sec = finSec p.Ma60Px p.Ma60Sec; Ma60Px = fin p.Ma60Px
                    Vwma10Sec = finSec p.Vwma10Px p.Vwma10Sec; Vwma10Px = fin p.Vwma10Px
                    Vwma20Sec = finSec p.Vwma20Px p.Vwma20Sec; Vwma20Px = fin p.Vwma20Px
                    Vwma30Sec = finSec p.Vwma30Px p.Vwma30Sec; Vwma30Px = fin p.Vwma30Px
                    Vwma40Sec = finSec p.Vwma40Px p.Vwma40Sec; Vwma40Px = fin p.Vwma40Px
                    Vwma50Sec = finSec p.Vwma50Px p.Vwma50Sec; Vwma50Px = fin p.Vwma50Px
                    Vwma60Sec = finSec p.Vwma60Px p.Vwma60Sec; Vwma60Px = fin p.Vwma60Px }
