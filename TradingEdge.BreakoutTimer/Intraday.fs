module TradingEdge.BreakoutTimer.Intraday

open System
open TradingEdge.RollingMa

// =============================================================================
// DipRiderV3 INTRADAY engine — pure trailing-window momentum (long-only).
//
// A per-(ticker, day) engine that folds 1-MINUTE bars. Forked from DipRider(V2);
// the V2 run-tracking machinery (above-9EMA run reset, savedRun stats,
// buy-into-run) is DELETED. Entry rests entirely on RESET-FREE trailing-window
// features computed over a fixed 20-bar window:
//   * 20m OLS log-price slope   (trend strength)
//   * 20m OLS log-volume slope  (is volume rising into the move — the PF lever)
//   * 20m log-ATR               (volatility; high-ATR names trade momentum better)
//   * 20m tightness = (rangeHigh-rangeLow)/atrLin  (real range, not lethargic)
//   * SumMa(6/40/60) of closes-above-9EMA          (how sustained the push is)
//   * slope / log-ATR ratio, session-max log-ATR   (recorded, gated after breakdown)
// plus session extremes (min/max close, max volume) and session VWAP.
//
// TWO-ANCHOR timing (the fix for V2's accidental 09:00 VWAP anchor):
//   * SessionStartMin (08:30 ET) — the emitter delivers bars from here; the
//     session min/max close + session max volume accumulate from the first valid bar.
//   * FeatureStartMin (09:30 ET) — every OTHER feature (VWAP, both OLS slopes,
//     ATR, tightness, EMA, the SumMa above-EMA counts, 15m-init-vol) folds ONLY
//     once bar.etMin >= FeatureStartMin (i.e. from the RTH open, no premarket).
//
// UNIVERSAL VALID-BAR gate: a bar enters a rolling window ONLY if
//   bar.close > 0 && bar.volume > 0 — illiquid/halt/print-gap bars are skipped
//   for EVERY feature. (15m-init-vol counts the first 15 VALID feature-bars, not
//   the wall-clock 09:30-09:45 window — a direct consequence of this rule.)
//
// Same snapshot-before-push (no-lookahead) discipline as the daily QullaSystem.
// =============================================================================

/// One 1-minute RTH bar, already converted to ET minutes-since-midnight and
/// split-adjusted to the candidate's daily scale.
type MinuteBar =
    { etMin: int
      ``open``: float
      high: float
      low: float
      close: float
      volume: int64 }

/// Intraday position life-cycle. DipRiderV3 positions are Holding immediately
/// (no Armed/Skipped arming states — those were the breakout engine's, deleted).
type IntraPosState =
    | Holding
    | ExitedAt of exitMin: int * exitPx: float * reason: string   // "stop"|"pct_stop"|"exhaust"|"vwap_lost"|"time_stop"|"moc"

/// One intraday trip. `EntryMin`/`EntryPx` are the FILL values (the entry bar's
/// close); the *AtEntry metrics are the strictly-prior indicator snapshots at the
/// entry bar (no-lookahead).
type IntradayPosition =
    { EntryMin: int
      EntryPx: float
      StopLevel: float         // protective-stop level (geometry stop off the 20m-window low; -inf = stopless).
      StopDistPct: float       // stop DISTANCE as a fraction of entry = (entry - stopLevel)/entry. nan if stopless.
      // ----- the V3 trailing-window momentum features (all strictly-prior; nan/-1 = not-warm) -----
      PriceSlope20AtEntry: float // 20m OLS slope of log(close) — trend strength (%/bar)
      VolSlope20AtEntry: float   // 20m OLS slope of log(volume) — is volume rising into the move (the F14 lever)
      LogAtr20AtEntry: float     // 20m mean log-true-range — the volatility feature
      Tightness20AtEntry: float  // (rangeHigh-rangeLow)/atrLin over the window — real range vs lethargic
      SlopePerAtrAtEntry: float  // PriceSlope20 / LogAtr20 (trend per unit volatility)
      EmaLowAtEntry: float       // trailing-20m MIN 9-EMA (strictly-prior) — the ema_climb basis
      SumAbove6AtEntry: int      // # of the last 6 feature-bars that closed >= the 9-EMA (short push count)
      SumAbove40AtEntry: int     // # of the last 40 that closed above the EMA (trend-too-long cap input)
      SumAbove60AtEntry: int     // # of the last 60 that closed above the EMA
      EmaVwap30AtEntry: int      // # of the last 30 bars the 9-EMA was ABOVE the session VWAP (persistence of the above-VWAP uptrend)
      EmaVwap60AtEntry: int      // # of the last 60 bars the 9-EMA was above VWAP
      TrailVol20mAtEntry: int64  // trailing 20-bar volume sum at entry (the entry's 20m volume)
      SessMaxVol20AtEntry: float // session PEAK trailing-20 volume sum (are we entering at the volume climax or a lull?)
      SessMinVol20AtEntry: float // session-MIN trailing-20 volume avg (from 09:45) — the session-min vol-stop basis
      EmaAtEntry: float          // the CURRENT-bar 9-EMA at entry (strictly-prior)
      SessMaxEmaAtEntry: float   // session MAX 9-EMA (how far the 9-EMA has pulled back from its session peak)
      // ----- BreakoutTimer features (strictly-prior; the fusion layer) -----
      BreakoutTimerAtEntry: int       // the breakout timer value going INTO this bar (>0 = a live breakout window).
      BarsSinceEmaHighAtEntry: int    // # bars since the 9-EMA last made a new session high (0 on a fresh high).
      PbUpDnAtEntry: float            // pullback updn = mean above-EMA-bar vol / mean below-EMA-bar vol since the
                                      // last reset (>1 = volume flowing into the RISING side). The vol-slope rival.
      PbMaxDistAtEntry: float         // DEEPEST fractional gap (sessMaxEma/ema - 1) reached since the last reset —
                                      // how far the 9-EMA fell below its session high during the pullback (>=0).
      PbAtrPctAtEntry: float          // mean 20m log-ATR accumulated over the pullback run since the last reset.
      SessMaxLogAtrAtEntry: float // session-cumulative MAX of the 20m log-ATR so far (past vol explosions)
      SessMinCloseAtEntry: float  // session MIN close (from 08:30) — geometry-stop floor candidate / context
      SessMaxCloseAtEntry: float  // session MAX close (from 08:30)
      SessMaxVolAtEntry: int64    // session MAX single-bar volume (from 08:30)
      VwapAtEntry: float          // session VWAP at entry (from 09:30); nan if none. buy-below vs above-VWAP split
      InitVol15mAtEntry: int64    // Σ volume over the first 15 VALID feature-bars (the name's early tempo)
      TrailVol5mAtEntry: int64    // trailing 5-bar volume sum at entry (recent tempo; exhaustion-cut numerator)
      EntryVsSessHighAtEntry: float // entry px / running session HIGH (strictly-prior) - 1 (<=0; how far below high)
      Chg20mAtEntry: float        // 20-bar %-change into entry (entry close / close 20 bars ago - 1); nan if <20 bars
      CumVolAtEntry: int64        // cumulative day volume THROUGH the entry bar (rvol numerator; recorded)
      MktChgOpenAtEntry: float    // reference index (SPY) %-change from SESSION OPEN, at the entry minute
      MktChgPrevAtEntry: float    // reference index (SPY) %-change from PREV DAILY CLOSE, at the entry minute
      State: IntraPosState }   // immutable — advancing a position returns a NEW record (HighFlyer style)

/// Intraday engine config — DipRiderV3 (pure trailing-window momentum).
type IntradayConfig =
    { VolWindow: int           // trailing ATR / tightness / OLS-slope lookback in 1m BARS (default 20)
      EmaPeriod: int           // the 9-EMA period (the closes-above-EMA reference)
      SessionStartMin: int     // first ET minute fed to the engine (510 = 08:30 ET). The session extremes
                               // (min/max close, max volume) accumulate from here.
      FeatureStartMin: int     // ET minute the OTHER features start folding (570 = 09:30 ET, the RTH open).
                               // VWAP, both OLS slopes, ATR, tightness, EMA, SumMa above-EMA, 15m-init-vol
                               // all fold only once bar.etMin >= this (no premarket contamination).
      EntryStartMin: int       // earliest ET minute an entry may fire (600 = 10:00). Bars before are
                               // processed (to warm the windows) but do not TRADE.
      EntryEndMin: int         // LATEST ET minute an entry may fire (810 = 13:30). 0 / >=MocMin = no upper bound.
      MocMin: int              // MOC cutoff in ET minutes (960 = 16:00)
      MaxConcurrent: int       // cap on currently-OPEN (Holding) positions; 0 = unlimited. V3 default = 1.
      // ----- entry gates (trailing-window momentum) -----
      MinVolSlope: float       // require 20m OLS log-volume slope >= this (rising volume). Default 0.05 (V2 F14/F15).
      MaxVolSlope: float       // BLOW-OFF CEILING (F16): reject 20m OLS log-volume slope >= this — an extreme
                               // volume-slope-up = a blow-off into entry (>=0.25 clips PF 0.58/-4.94%). +inf = off.
      MinPriceSlope: float     // require 20m OLS log-price slope > this. Default 0.0 (sweep for a higher floor).
      MinTightness: float      // require tightness >= this (real range, not lethargic). Default 3.0. 0 = off.
      MaxTightness: float      // tightness CEILING (+inf disables).
      MinAtrPct: float         // log-ATR FLOOR: require the 20m log-ATR >= this. THE MAIN LEVER (F3: PF scales
                               // monotonically with ATR; sub-0.013 is flat/dead). 0 = off. Default 0.013.
      MaxAtrPct: float         // log-ATR CEILING (+inf disables).
      MinCloseAbove6: int      // require SumAbove6 >= this (>= N of the last 6 bars closed above the EMA).
                               // 0 = DISABLED (V3 default — start off, tune later).
      MinSlopePerAtr: float    // require slope/log-ATR ratio >= this. -inf / very-negative = off (default). Gated after breakdown.
      MinSessMaxLogAtr: float  // require the session-max log-ATR >= this (a name that HAS exploded). 0 = off (default).
      MaxRvol5m20d: float      // EXHAUSTION CUT (F11): reject if the trailing 5m avg volume >= this × the 20d
                               // per-minute pace ((trailVol5m/5) / (avgvol20/390)). A blow-off = a late entry.
                               // Default 100. 0 = off. Needs SetVolBaselines' perMin20d.
      MinEntryVsVwap: float    // VWAP-LOCATION FLOOR (F14): reject if entry px is < this fraction below the
                               // session VWAP (entry/vwap - 1 < this). A momentum name >3% below VWAP is a
                               // sold-off falling knife, not a continuation. Default -0.03. NaN/-inf = off.
      MinChg1d: float          // DAY-DIRECTION FLOOR (F17): reject if the entry px is < this fraction vs the
                               // PREV daily close (entry/prevClose - 1 < this). A stock RED on the day is
                               // fighting its own daily trend — buying its intraday bounce loses. Default 0.0
                               // (must be green on the day). NaN/-inf = off.
      MinChg3d: float          // 3-DAY TREND FLOOR (F28): reject if entry / close-3d-ago - 1 < this. A name
                               // FALLING over 3 days is a poor momentum buy in BOTH regimes (bad-everywhere,
                               // durable). Default 0.0 (3-day trend must be up). NaN/-inf = off. Needs close3d.
      RequireEmaAboveVwap: bool // ENTRY GATE (F21): require the 9-EMA STRICTLY ABOVE the session VWAP at entry.
                               // Superseded by MinEmaVsVwap (the knee is −2%, not 0). false (default) = off.
      MinEmaVsVwap: float      // VWAP-LOCATION FLOOR (F27, REPLACES MinEntryVsVwap): reject if the current 9-EMA
                               // is more than |this| below the session VWAP (ema/vwap − 1 < this). The smoothed
                               // TREND vs VWAP (ignores single wicks) — cleaner than the price-vs-VWAP floor.
                               // Default −0.02. NaN/-inf = off.
      MaxSumAbove40: int       // CAP: reject if SumAbove40 >= this (trend went on too long). 0 = off (default).
      MaxSumAbove60: int       // CAP: reject if SumAbove60 >= this. 0 = off (default).
      // ----- BreakoutTimer: 9-EMA-breakout permission gate (the fusion layer over the V3 momentum entry) -----
      RequireBreakoutTimer: bool // MASTER SWITCH: when true, the V3 momentum entry may fire ONLY while the
                               // breakout timer is live (>0). false (default reproduces pure DipRiderV3) = off.
      BreakoutMinBarsSinceHigh: int // the DROUGHT threshold: a new 9-EMA session high arms (or re-arms) the timer
                               // ONLY if the 9-EMA had NOT made a new session high for >= this many bars first
                               // (bars_since_ema_high, read PRE-reset). Avoids arming on trivial pullbacks.
                               // Default 8 (the VwapReclaim value). A new high below this just keeps ticking down.
      BreakoutTimerBars: int   // the COUNTDOWN length: on a qualifying breakout the timer is set to this and
                               // downticks 1/bar to a -1 floor. While >0 the momentum entry is permitted.
                               // Default 10 (a ~10-minute window). Pullback features reset on two triggers: the
                               // timer expiring to 0, or a DORMANT new-high that fails to arm; ordinary declines
                               // and the live-breakout window keep accumulating.
      MinPbUpDn: float         // pb_updn FLOOR (F8): reject if the pullback updn ratio (mean above-EMA-bar vol /
                               // mean below-EMA-bar vol since the last reset) < this. At drought=16 this is a
                               // MONOTONE, all-weather, 2021-safe lever (high updn = sustained accumulation into a
                               // legitimate breakout). 0 / -inf / NaN = off (default). Needs a warm pb_updn.
      // ----- stop / exits -----
      GeomStop: bool           // true (default) = geometry stop: d = entry - stopFloor (the 20m-window low),
                               // stop = entry - d*StopDistFrac. false = stopless (hold-to-MOC + optional pct/time).
      StopFloorSessMin: bool   // stop-floor source: false (default) = the 20m-window low (rangeLow, the trailing
                               // analogue of V2's run floor); true = the session min close (a wider floor). A/B lever.
      StopDistFrac: float      // stop distance as a fraction of d (default 2/3, VwapReclaim/V2 Finding 14).
      StopOnClose: bool        // true (default) = stop triggers on a CLOSE at/below the level (fills at close);
                               // false = wick stop (bar.low <= level, fills at the level / gap-through open).
      EmaStop: bool            // EMA-STOP MODE (momentum-appropriate): the stop level IS the trailing-20m MIN 9-EMA
                               // (direct, NO geometry), and the stop TRIGGERS when the live 9-EMA falls below it
                               // (fills at that bar's close). false (default) = close-based geometry stop.
                               // EmaStopTrail chooses FIXED (level frozen at entry) vs TRAILING (recomputed each bar).
      EmaStopTrail: bool       // EMA-STOP SUB-MODE (only when EmaStop): false (default) = FIXED — the level is the
                               // 20m-min-9EMA as-of the ENTRY bar, held for the trade's life. true = TRAILING —
                               // the level recomputes each bar to the CURRENT 20m-min-9EMA (ratchets up with trend).
      PctStop: float           // wide catastrophe %-stop: bar.low <= entry*(1-x). 0 = off.
      TimeStopMin: int         // time-stop: flatten this many minutes after entry (capped at MOC). 0 = off.
      VolStopFrac: float       // 20m-AVG-VOLUME STOP: while holding, exit when the trailing-20m volume falls to
                               // < this fraction of the ENTRY's trailing-20m volume (live_vol20 / entry_vol20 <
                               // this). Volume drying up = disinterest → exit. Fills at that bar's close.
                               // e.g. 0.667 (2/3) or 0.5 (1/2). 0 = off (default).
      VolSlopeStop: float      // VOLUME-SLOPE STOP: while holding, exit when the live 20m OLS log-volume slope
                               // falls BELOW this (the volume TREND rolling over = the move losing fuel). More
                               // selective than the avg-volume stop (needs an actual downtrend, not mere decay).
                               // e.g. 0.0 (any negative) or −0.05 / −0.10 (a steeper roll-over). Fills at close.
                               // -inf / NaN = off (default).
      VolStopSessionMin: bool  // 20m-avg-VOLUME STOP basis. false (default) = the stop threshold is
                               // VolStopFrac × the ENTRY's trailing-20m volume. true = VolStopFrac × the
                               // SESSION-MIN trailing-20m volume (the quietest 20m so far, tracked from
                               // VolStopMinStartMin). Motivation: a session-min basis places a LOWER, more
                               // absolute floor than the entry basis (which drifts up when entering at a
                               // volume climax) — testing whether the vol-stop lift is a low-threshold effect.
      VolStopMinStartMin: int  // ET minute the session-MIN 20m-volume tracker starts (585 = 09:45 ET — the
                               // trade start, NOT the 09:30 feature anchor: the volatile open would drag the
                               // min down artificially. Only used when VolStopSessionMin = true.
      // ----- exhaustion / loss-of-VWAP exits (recorded lessons from V2; off by default) -----
      ExhaustExit: bool        // MaxFlyer-style: while HOLDING, exit when a 1m bar makes a NEW SESSION HIGH on a
                               // VOLUME BLOW-OFF (>= ExhaustVolMult × BOTH per-min baselines). Fill at that close.
      ExhaustVolMult: float    // the blow-off multiplier (e.g. 5/10/20). Only used when ExhaustExit.
      VwapExitBars: int }      // LOSS-OF-VWAP exit: once the 9-EMA has been above VWAP for >= this many bars, close
                               // the long the bar the 9-EMA crosses BELOW VWAP. 0 = off.

/// Per-(ticker, day) intraday engine. Feed it the day's RTH MinuteBar[] in time
/// order via `Process`, then `Flatten` and read `Positions`.
type IntradaySystem(cfg: IntradayConfig, ticker: string, day: DateOnly, prevClose: float) =

    // MARKET CONTEXT (broader-market regime, shared across all tickers on the day). A read-only lookup
    // etMin -> struct(chg-from-open, chg-from-prev-close) for a reference index (SPY). Set per-day before
    // feeding bars; defaults to (nan, nan). NOT folded into any per-ticker state — pure context, snapshotted
    // at entry. No-lookahead: the lookup at et_min uses only the index's bars through et_min.
    let mutable marketCtx : int -> struct (float * float) = fun _ -> struct (nan, nan)
    // per-minute VOLUME baselines for the exhaustion exit (set per-day from the Candidate; 0 = unset/disabled):
    //   permin20d  = 20-day avg daily volume / 390  (the name's normal per-minute pace)
    //   permin15m  = opening-15m avg 1m volume       (the name's own early-session tempo)
    let mutable permin20d = 0.0
    let mutable permin15m = 0.0
    // multi-day daily-close context (adj), for the chg_3d/chg_7d trend floors. 0 = unset (gate disabled).
    let mutable close3d = 0.0
    let mutable close7d = 0.0

    // ----- rolling intraday structures (1m timeframe; all fed ONLY from the 09:30 feature anchor) -----
    let atrLog    = AvgMa(cfg.VolWindow)        // mean LOG true range over the last VolWindow bars (the volatility feature)
    let atrLin    = AvgMa(cfg.VolWindow)        // mean ABSOLUTE true range (linear) — the tightness DENOMINATOR
    let lag20     = LagMa<float>(20)            // 20-bar close delay line -> the 20-minute %-change into entry
    let rangeHigh = MaxMa(cfg.VolWindow)        // tightness: max high over the window
    let rangeLow  = MinMa(cfg.VolWindow)        // tightness: min low over the window (the tightness range)
    let closeLow  = MinMa(cfg.VolWindow)        // the 20m MIN CLOSE — the geometry-stop floor (StopFloorSessMin=false)
    let emaLow    = MinMa(cfg.VolWindow)        // the 20m MIN 9-EMA — the EMA-stop floor (EmaStop mode)
    let ema       = EmaMa(cfg.EmaPeriod)        // the 9-EMA (closes-above-EMA reference)
    // SumMa of the 0/1 "did this bar close >= the strictly-prior 9-EMA?" indicator, over 6/40/60 feature-bars.
    // The reset-free replacement for V2's above-EMA run length. Short window = the push; long windows = the cap.
    let above6    = SumMa(6)
    let above40   = SumMa(40)
    let above60   = SumMa(60)
    // SumMa of the 0/1 "was the 9-EMA above the session VWAP this bar?" indicator, over 30/60 feature-bars.
    // How PERSISTENTLY the fast trend has held above VWAP through the session — a genuine above-VWAP uptrend
    // signal (distinct from the VwapReclaim cross logic: this is a windowed COUNT, not a cross event).
    let emaVwap30 = SumMa(30)
    let emaVwap60 = SumMa(60)
    // trailing-window OLS slopes (fixed VolWindow bars, fed every VALID feature-bar). Log-close = trend slope,
    // log-volume = the volume-trend slope (the F14 lever). Both pushed TOGETHER only when both logs are valid,
    // so they stay on the identical push-index x-axis (directly comparable).
    let priceOls  = OlsSlopeMa(cfg.VolWindow)
    let volOls    = OlsSlopeMa(cfg.VolWindow)
    // trailing 5-bar raw VOLUME sum — the recent-tempo numerator for the exhaustion cut (5m-avg vs the 15m
    // opening-avg and the 20d per-minute avg). Fed every VALID feature-bar.
    let vol5sum   = SumMa(5)
    // trailing 20-bar raw VOLUME sum — for the session-MAX-20m-volume feature (compare the entry's 20m volume
    // to the session's peak 20m volume: are we entering at the volume climax or a lull?).
    let vol20sum  = SumMa(20)

    // session VWAP = Σ(typical_price · volume) / Σ(volume), typical = (h+l+c)/3, from the 09:30 feature anchor.
    let mutable vwapNum  = 0.0                   // Σ tp·v
    let mutable vwapDen  = 0.0                   // Σ v

    // ----- session-cumulative running extremes (NOT windowed) -----
    // session HIGH (strictly-prior) — for the entry-vs-session-high feature. Folds from the FEATURE anchor
    // (09:30) so it tracks the RTH high, matching what the momentum entry cares about.
    let mutable runHi    : float voption = ValueNone
    // session min/max CLOSE and max single-bar VOLUME — fold from the SESSION anchor (08:30), per the spec.
    let mutable sessMinClose : float voption = ValueNone
    let mutable sessMaxClose : float voption = ValueNone
    let mutable sessMaxVol   : int64 voption = ValueNone
    // session-cumulative MAX of the trailing-20-bar volume SUM (the session's peak 20m volume) and of the
    // 9-EMA (the session's highest 9-EMA). Fed the respective post-fold state each feature-bar. No reset.
    let mutable sessMaxVol20 : float voption = ValueNone
    // session-cumulative MIN of the trailing-20-bar volume SUM, tracked ONLY from VolStopMinStartMin (09:45 —
    // skips the volatile open). The session-min basis for the 20m-avg-volume stop. No reset — per-day engine.
    let mutable sessMinVol20 : float voption = ValueNone
    let mutable sessMaxEma   : float voption = ValueNone
    // session-cumulative MAX of the 20m log-ATR (past volatility explosions). Fed the atrLog.State each
    // feature-bar (like HighFlyer's maxAtrLog, but session-cumulative, no window). No reset — per-day engine.
    let mutable sessMaxLogAtr : float = nan
    // 15m INITIAL volume = Σ volume over the first 15 VALID feature-bars (NOT the clock 09:30-09:45 window).
    let mutable initVol15m   : int64 = 0L
    let mutable initVolBars  : int = 0
    // consecutive bars the 9-EMA has been ABOVE the session VWAP (for the loss-of-VWAP exit). Post-fold state.
    let mutable emaAboveVwapBars = 0

    // ----- BreakoutTimer state (the fusion layer over the V3 momentum entry) -----
    // # bars since the 9-EMA last made a NEW session high (over the sessMaxEma reference). 0 on a fresh high,
    // +1 each feature-bar that doesn't make one. Read PRE-reset for the drought threshold.
    let mutable barsSinceEmaHigh = 0
    // the breakout timer: -1 = inactive. Set to BreakoutTimerBars on a qualifying post-drought EMA-high breakout,
    // downticks 1/bar to a -1 floor. While >0 the momentum entry is permitted; the pullback features reset at 0.
    let mutable breakoutTimer = -1
    // pullback-run accumulators (VwapReclaim-style; reset when the timer expires to 0 — planned A/B against
    // reset-on-bars_since-threshold). updn = mean above-EMA vol / mean below-EMA vol; pbMaxDist = deepest
    // sessMaxEma/ema-1 gap; pbAtr = mean log-ATR over the run. Accumulated CONTINUOUSLY (pullback AND breakout).
    let mutable pbUpVolSum = 0.0
    let mutable pbUpVolN   = 0
    let mutable pbDnVolSum = 0.0
    let mutable pbDnVolN   = 0
    let mutable pbMaxDist  = 0.0
    let mutable pbAtrSum   = 0.0
    let mutable pbAtrN     = 0

    let mutable cumVol      : int64 = 0L         // cumulative day volume from the FEATURE anchor (the rvol numerator)
    let mutable sessionOpen : float voption = ValueNone   // the first FEATURE bar's open (09:30) — the session open
    let mutable lastBar  : MinuteBar voption = ValueNone  // most-recent folded bar; at ProcessBar top it's the prior bar
    let mutable barsSeen  = 0

    // ----- pre-push snapshots (state going INTO the current bar; no lookahead) -----
    let mutable sRunHi        : float voption = ValueNone
    let mutable sLastBar      : MinuteBar voption = ValueNone
    let mutable sAtrLog       : float voption = ValueNone
    let mutable sAtrLin       : float voption = ValueNone
    let mutable sRangeHigh    : float voption = ValueNone
    let mutable sRangeLow     : float voption = ValueNone
    let mutable sCloseLow     : float voption = ValueNone   // the 20m min close going INTO this bar (the geom-stop floor)
    let mutable sEmaLow       : float voption = ValueNone   // the 20m min 9-EMA going INTO this bar (the EMA-stop floor)
    let mutable sEmaPrev      : float voption = ValueNone   // the 9-EMA going INTO this bar (for the closes-above indicator)
    let mutable sPriceSlope   : float = nan
    let mutable sVolSlope     : float = nan
    let mutable sSumAbove6    : int = 0
    let mutable sSumAbove40   : int = 0
    let mutable sSumAbove60   : int = 0
    let mutable sEmaVwap30    : int = 0    // # of last 30 bars the 9-EMA was above the session VWAP (into this bar)
    let mutable sEmaVwap60    : int = 0
    let mutable sSessMaxLogAtr : float = nan
    let mutable sSessMinClose : float = nan
    let mutable sSessMaxClose : float = nan
    let mutable sSessMaxVol   : int64 = 0L
    let mutable sVwapNow      : float voption = ValueNone
    let mutable sInitVol15m   : int64 = 0L
    let mutable sTrailVol5m   : int64 = 0L      // trailing 5-bar volume sum going INTO this bar (exhaustion numerator)
    let mutable sTrailVol20m  : int64 = 0L      // trailing 20-bar volume sum going INTO this bar
    let mutable sSessMaxVol20 : float = nan     // session peak trailing-20 volume sum (strictly-prior)
    let mutable sSessMinVol20 : float = nan     // session-min trailing-20 volume avg from 09:45 (strictly-prior)
    let mutable sSessMaxEma   : float = nan     // session max 9-EMA (strictly-prior)
    let mutable sEmaAboveVwapBars : int = 0
    // BreakoutTimer strictly-prior snapshots (state going INTO this bar — what the entry gate reads).
    let mutable sBreakoutTimer     : int = -1
    let mutable sBarsSinceEmaHigh  : int = 0
    let mutable sPbUpDn            : float = nan   // pullback updn ratio (mean up-vol / mean dn-vol)
    let mutable sPbMaxDist         : float = nan   // deepest sessMaxEma/ema-1 gap this run
    let mutable sPbAtr             : float = nan   // mean log-ATR over the pullback run

    let positions = ResizeArray<IntradayPosition>()

    member _.Ticker = ticker
    member _.Day = day
    member _.BarsSeen = barsSeen
    /// Set the per-day broader-market (SPY) context lookup: etMin -> struct(chg-from-open, chg-from-prev-close).
    member _.SetMarketCtx (f: int -> struct (float * float)) = marketCtx <- f
    /// Set the per-minute volume baselines for the exhaustion exit: (20d-avg/390, opening-15m avg 1m vol).
    member _.SetVolBaselines (perMin20d: float, perMin15m: float) = permin20d <- perMin20d; permin15m <- perMin15m
    /// Set the multi-day daily-close context (adj) for the chg_3d/chg_7d trend floors. 0 disables that gate.
    member _.SetDailyContext (c3d: float, c7d: float) = close3d <- c3d; close7d <- c7d
    member private _.MktChgOpen (etMin: int) = let struct (o, _) = marketCtx etMin in o
    member private _.MktChgPrev (etMin: int) = let struct (_, p) = marketCtx etMin in p
    /// The session open = the first FEATURE bar's open (09:30). Cross-checks the SQL day_open.
    member _.SessionOpen = sessionOpen

    // ----- read members -----
    member _.AtrLog = sAtrLog
    member _.RangeAbs =
        match sRangeHigh, sRangeLow with
        | ValueSome hi, ValueSome lo -> ValueSome (hi - lo)
        | _ -> ValueNone
    /// Intraday consolidation tightness = abs range / abs (LINEAR) ATR.
    member this.Tightness =
        match this.RangeAbs, sAtrLin with
        | ValueSome r, ValueSome atr when atr <> 0.0 -> ValueSome (r / atr)
        | _ -> ValueNone
    /// The LIVE session VWAP (post-fold, incl. the current bar). ValueNone before any volume.
    member private _.LiveVwap : float voption =
        if vwapDen > 0.0 then ValueSome (vwapNum / vwapDen) else ValueNone

    /// Count of currently-open (Holding) positions — the concurrency denominator.
    member _.OpenCount =
        let mutable n = 0
        for p in positions do (match p.State with Holding -> n <- n + 1 | ExitedAt _ -> ())
        n

    /// Gate 3 — does THIS bar trigger a DipRiderV3 momentum entry? Reads pre-push snapshots (strictly-prior),
    /// so it must be evaluated AFTER ProcessBar(bar). The entry bar's close is the fill.
    member this.ShouldEnterV3 (bar: MinuteBar) : bool =
        let inline gate (v: _ voption) (test: _ -> bool) =
            match v with ValueSome x -> test x | ValueNone -> false
        // inside the wall-clock trading window [EntryStartMin, EntryEndMin].
        bar.etMin >= cfg.EntryStartMin
        && (cfg.EntryEndMin <= 0 || cfg.EntryEndMin >= cfg.MocMin || bar.etMin <= cfg.EntryEndMin)
        // rising volume into the move (the main PF lever): 20m OLS log-volume slope >= MinVolSlope.
        && (not (Double.IsNaN sVolSlope) && sVolSlope >= cfg.MinVolSlope)
        // blow-off CEILING (F16): reject an extreme volume-slope-up (a volume explosion into entry). +inf = off.
        && (Double.IsInfinity cfg.MaxVolSlope || (not (Double.IsNaN sVolSlope) && sVolSlope < cfg.MaxVolSlope))
        // positive price trend: 20m OLS log-price slope > MinPriceSlope.
        && (not (Double.IsNaN sPriceSlope) && sPriceSlope > cfg.MinPriceSlope)
        // volatility FLOOR — THE MAIN LEVER (F3): require the 20m log-ATR >= MinAtrPct. Low-ATR names don't
        // move enough for a continuation to pay (sub-0.013 is flat/dead). 0 = off.
        && (cfg.MinAtrPct <= 0.0 || gate this.AtrLog (fun a -> a >= cfg.MinAtrPct))
        // real range, not a lethargic name: tightness >= MinTightness (and optional CEILINGS).
        && (cfg.MinTightness <= 0.0 || gate this.Tightness (fun t -> t >= cfg.MinTightness))
        && (Double.IsInfinity cfg.MaxTightness || gate this.Tightness (fun t -> t < cfg.MaxTightness))
        && (Double.IsInfinity cfg.MaxAtrPct    || gate this.AtrLog    (fun a -> a < cfg.MaxAtrPct))
        // EXHAUSTION CUT (F11): reject if the trailing 5m avg volume >= MaxRvol5m20d × the 20d per-minute
        // pace ((trailVol5m/5) / (permin20d)). A blow-off = a late entry. 0 = off; needs the perMin20d baseline.
        && (cfg.MaxRvol5m20d <= 0.0 || permin20d <= 0.0
            || (float sTrailVol5m / 5.0) < cfg.MaxRvol5m20d * permin20d)
        // VWAP-LOCATION FLOOR (F14): reject if the entry px is more than |MinEntryVsVwap| below the session
        // VWAP (a sold-off falling knife, not a continuation). -inf/NaN = off. Uses the strictly-prior VWAP.
        && (Double.IsNegativeInfinity cfg.MinEntryVsVwap || Double.IsNaN cfg.MinEntryVsVwap
            || gate sVwapNow (fun v -> v > 0.0 && bar.close / v - 1.0 >= cfg.MinEntryVsVwap))
        // DAY-DIRECTION FLOOR (F17): reject if the stock is RED on the day (entry/prevClose - 1 < MinChg1d).
        // A red-on-the-day name is fighting its own daily trend. -inf/NaN = off.
        && (Double.IsNegativeInfinity cfg.MinChg1d || Double.IsNaN cfg.MinChg1d
            || (prevClose > 0.0 && bar.close / prevClose - 1.0 >= cfg.MinChg1d))
        // 3-DAY TREND FLOOR (F28): reject if the stock is falling over 3 days (entry/close3d - 1 < MinChg3d).
        // Bad-everywhere (both regimes). -inf/NaN = off; needs close3d set.
        && (Double.IsNegativeInfinity cfg.MinChg3d || Double.IsNaN cfg.MinChg3d || close3d <= 0.0
            || bar.close / close3d - 1.0 >= cfg.MinChg3d)
        // ABOVE-VWAP ENTRY GATE (F21): require the 9-EMA STRICTLY above the session VWAP. false = off.
        && (not cfg.RequireEmaAboveVwap
            || (match sEmaPrev, sVwapNow with ValueSome e, ValueSome v -> e > v | _ -> false))
        // 9-EMA-vs-VWAP FLOOR (F27, replaces the price-vs-VWAP floor): reject if the 9-EMA is more than
        // |MinEmaVsVwap| below VWAP (a sold-off trend, not a continuation). -inf/NaN = off.
        && (Double.IsNegativeInfinity cfg.MinEmaVsVwap || Double.IsNaN cfg.MinEmaVsVwap
            || (match sEmaPrev, sVwapNow with
                | ValueSome e, ValueSome v -> v > 0.0 && e / v - 1.0 >= cfg.MinEmaVsVwap
                | _ -> false))
        // the push: >= N of the last 6 bars closed above the 9-EMA. 0 = OFF (default, start disabled).
        && (cfg.MinCloseAbove6 <= 0 || sSumAbove6 >= cfg.MinCloseAbove6)
        // trend-too-long CAP: reject if too many of the last 40/60 bars were above the EMA. 0 = off.
        && (cfg.MaxSumAbove40 <= 0 || sSumAbove40 < cfg.MaxSumAbove40)
        && (cfg.MaxSumAbove60 <= 0 || sSumAbove60 < cfg.MaxSumAbove60)
        // slope-per-ATR floor (gated after breakdown; default off via a very-negative floor). Requires a
        // warm, positive log-ATR and a warm price slope.
        && (Double.IsNegativeInfinity cfg.MinSlopePerAtr
            || (match sAtrLog with
                | ValueSome a when a > 0.0 && not (Double.IsNaN sPriceSlope) -> (sPriceSlope / a) >= cfg.MinSlopePerAtr
                | _ -> false))
        // session-max log-ATR floor (a name that HAS had a volatility explosion). 0 = off.
        && (cfg.MinSessMaxLogAtr <= 0.0 || (not (Double.IsNaN sSessMaxLogAtr) && sSessMaxLogAtr >= cfg.MinSessMaxLogAtr))
        // BREAKOUT-TIMER PERMISSION GATE (the fusion layer): when RequireBreakoutTimer, the momentum entry may
        // fire ONLY while the timer is live (>0) — i.e. within BreakoutTimerBars of a post-drought EMA-high
        // breakout. false (default) reproduces pure DipRiderV3. Reads the strictly-prior timer snapshot.
        && (not cfg.RequireBreakoutTimer || sBreakoutTimer > 0)
        // pb_updn FLOOR (F8): reject if the pullback updn ratio < MinPbUpDn. At drought=16 a monotone, all-weather
        // A+ lever. 0/-inf/NaN = off; requires a warm pb_updn (both up & down bars accumulated).
        && (cfg.MinPbUpDn <= 0.0 || Double.IsNegativeInfinity cfg.MinPbUpDn || Double.IsNaN cfg.MinPbUpDn
            || (not (Double.IsNaN sPbUpDn) && sPbUpDn >= cfg.MinPbUpDn))
        // concurrency room.
        && (cfg.MaxConcurrent <= 0 || this.OpenCount < cfg.MaxConcurrent)

    /// Fold one minute bar into all rolling state (snapshot-before-push, no-lookahead).
    member this.ProcessBar (bar: MinuteBar) =
        // 1) snapshot pre-push state (strictly-prior — what the entry gate reads on THIS bar).
        sRunHi         <- runHi
        sLastBar       <- lastBar
        sAtrLog        <- atrLog.State
        sAtrLin        <- atrLin.State
        sRangeHigh     <- rangeHigh.State
        sRangeLow      <- rangeLow.State
        sCloseLow      <- closeLow.State
        sEmaLow        <- emaLow.State
        sEmaPrev       <- ema.State
        sPriceSlope    <- (match priceOls.Slope with ValueSome s -> s | ValueNone -> nan)
        sVolSlope      <- (match volOls.Slope   with ValueSome s -> s | ValueNone -> nan)
        sSumAbove6     <- (match above6.State  with ValueSome v -> int v | ValueNone -> 0)
        sSumAbove40    <- (match above40.State with ValueSome v -> int v | ValueNone -> 0)
        sSumAbove60    <- (match above60.State with ValueSome v -> int v | ValueNone -> 0)
        sEmaVwap30     <- (match emaVwap30.State with ValueSome v -> int v | ValueNone -> 0)
        sEmaVwap60     <- (match emaVwap60.State with ValueSome v -> int v | ValueNone -> 0)
        sSessMaxLogAtr <- sessMaxLogAtr
        sSessMinClose  <- (match sessMinClose with ValueSome c -> c | ValueNone -> nan)
        sSessMaxClose  <- (match sessMaxClose with ValueSome c -> c | ValueNone -> nan)
        sSessMaxVol    <- (match sessMaxVol   with ValueSome v -> v | ValueNone -> 0L)
        sVwapNow       <- this.LiveVwap
        sInitVol15m    <- initVol15m
        sTrailVol5m    <- (match vol5sum.State with ValueSome v -> int64 v | ValueNone -> 0L)
        sTrailVol20m   <- (match vol20sum.State with ValueSome v -> int64 v | ValueNone -> 0L)
        sSessMaxVol20  <- sessMaxVol20 |> ValueOption.defaultValue nan
        sSessMinVol20  <- sessMinVol20 |> ValueOption.defaultValue nan
        sSessMaxEma    <- sessMaxEma   |> ValueOption.defaultValue nan
        sEmaAboveVwapBars <- emaAboveVwapBars
        sBreakoutTimer    <- breakoutTimer
        sBarsSinceEmaHigh <- barsSinceEmaHigh
        sPbUpDn           <- (if pbUpVolN > 0 && pbDnVolN > 0 then (pbUpVolSum / float pbUpVolN) / (pbDnVolSum / float pbDnVolN) else nan)
        sPbMaxDist        <- (if pbUpVolN > 0 || pbDnVolN > 0 then pbMaxDist else nan)
        sPbAtr            <- (if pbAtrN > 0 then pbAtrSum / float pbAtrN else nan)

        // 2) the universal VALID-bar gate: a bar enters ANY rolling window only if price AND volume are positive.
        //    Illiquid/halt/print-gap bars are skipped for every feature.
        let valid = bar.close > 0.0 && bar.volume > 0L

        // 3) SESSION extremes — fold from the SESSION anchor (08:30), for every VALID bar.
        if valid then
            sessMinClose <- (match sessMinClose with ValueSome c -> ValueSome (min c bar.close) | ValueNone -> ValueSome bar.close)
            sessMaxClose <- (match sessMaxClose with ValueSome c -> ValueSome (max c bar.close) | ValueNone -> ValueSome bar.close)
            sessMaxVol   <- (match sessMaxVol   with ValueSome v -> ValueSome (max v bar.volume) | ValueNone -> ValueSome bar.volume)

        // 4) FEATURE block — everything else folds ONLY from the FEATURE anchor (09:30), for every VALID bar.
        if valid && bar.etMin >= cfg.FeatureStartMin then
            if sessionOpen.IsNone then sessionOpen <- ValueSome bar.``open``
            cumVol <- cumVol + bar.volume
            // running session HIGH (strictly-prior via the snapshot above) — the entry-vs-high reference.
            runHi <- (match runHi with ValueSome h -> ValueSome (max h bar.high) | ValueNone -> ValueSome bar.high)
            // true range vs the prior bar's close (both linear & log ATR). Needs a valid prior bar.
            let mutable barLogTr = nan
            (match lastBar with
             | ValueSome prev when bar.high > 0.0 && bar.low > 0.0 && prev.close > 0.0 ->
                 let pc = prev.close
                 (max bar.high pc - min bar.low pc) |> atrLin.Push
                 barLogTr <- log (max bar.high pc / min bar.low pc)
                 barLogTr |> atrLog.Push
             | _ -> ())
            // session-cumulative MAX of the 20m log-ATR (past volatility explosions).
            (match atrLog.State with
             | ValueSome a -> sessMaxLogAtr <- (if Double.IsNaN sessMaxLogAtr then a else max sessMaxLogAtr a)
             | ValueNone -> ())
            // tightness window (high/low), the 20m min-CLOSE (geom-stop floor), + 20m %-change delay line.
            rangeHigh.Push bar.high
            rangeLow.Push  bar.low
            closeLow.Push  bar.close
            lag20.Push     bar.close
            // closes-above-9EMA indicator, vs the STRICTLY-PRIOR EMA (sEmaPrev). Push 1/0 into the SumMa windows.
            let aboveInd = match sEmaPrev with ValueSome e -> (if bar.close >= e then 1.0 else 0.0) | ValueNone -> 0.0
            above6.Push aboveInd; above40.Push aboveInd; above60.Push aboveInd
            // "was the 9-EMA above the session VWAP this bar?" — strictly-prior EMA vs strictly-prior VWAP
            // (sEmaPrev / sVwapNow, both snapshotted at the top before this bar folds). 0 if either undefined.
            let emaVwapInd =
                match sEmaPrev, sVwapNow with
                | ValueSome e, ValueSome v -> if e > v then 1.0 else 0.0
                | _ -> 0.0
            emaVwap30.Push emaVwapInd; emaVwap60.Push emaVwapInd
            // trailing-window OLS slopes (both together, same x-axis).
            priceOls.Push (log bar.close)
            volOls.Push   (log (float bar.volume))
            vol5sum.Push  (float bar.volume)   // trailing 5-bar volume (exhaustion-cut numerator)
            vol20sum.Push (float bar.volume)   // trailing 20-bar volume (for the session-peak-20m-volume feature)
            // session-cumulative MAX of the trailing-20 volume avg (the session's peak 20m volume).
            (match vol20sum.State with
             | ValueSome v -> sessMaxVol20 <- (match sessMaxVol20 with ValueSome m -> ValueSome (max m v) | ValueNone -> ValueSome v)
             | ValueNone -> ())
            // session-cumulative MIN of the trailing-20 volume avg, tracked from VolStopMinStartMin (09:45). The
            // window has been folding since 09:30 so it holds ~15 bars at 09:45 — a partial window is fine here
            // (.State is a mean, not a sum, so an early ~15-bar avg is still a valid 20m-avg estimate).
            if bar.etMin >= cfg.VolStopMinStartMin then
                (match vol20sum.State with
                 | ValueSome v -> sessMinVol20 <- (match sessMinVol20 with ValueSome m -> ValueSome (min m v) | ValueNone -> ValueSome v)
                 | ValueNone -> ())
            // session VWAP.
            let tp = (bar.high + bar.low + bar.close) / 3.0
            vwapNum <- vwapNum + tp * float bar.volume
            vwapDen <- vwapDen + float bar.volume
            // the 9-EMA itself (fed AFTER the indicator reads the prior EMA above).
            ema.Push bar.close
            // trailing-20m MIN 9-EMA (the EMA-stop floor) — push the freshly-folded EMA into the min-window.
            (match ema.State with ValueSome e -> emaLow.Push e | ValueNone -> ())
            // ===== BreakoutTimer bookkeeping (needs the freshly-folded EMA & atrLog; runs AFTER ema.Push) =====
            (match ema.State with
             | ValueSome e ->
                 // (a) is THIS bar a new 9-EMA session high? Compare the fresh EMA to the STRICTLY-PRIOR session
                 //     max (sSessMaxEma, snapshotted before this bar folded). NaN prior = first EMA = a new high.
                 let isNewEmaHigh = Double.IsNaN sSessMaxEma || e > sSessMaxEma
                 // (b) arm + reset. The arm decision uses barsSinceEmaHigh as-of the PRIOR bar (the drought that
                 //     PRECEDED this high) — read BEFORE (c) resets it. Arms only after a >= threshold drought.
                 // PULLBACK-FEATURE RESET — two triggers, everything else keeps accumulating:
                 //   (1) the timer EXPIRES to exactly 0 (a live breakout window just closed out), OR
                 //   (2) a NEW EMA high fires but FAILS to arm (drought threshold not met) — a lethargic/false-
                 //       start breakout that invalidates the prior pullback run.
                 // Ordinary declines (no new high, timer <= 0) do NOT reset — they ARE the pullback we accumulate.
                 // A qualifying arming breakout does NOT reset — updn/etc carry the pullback into the live breakout.
                 let mutable doReset = false
                 if isNewEmaHigh && barsSinceEmaHigh >= cfg.BreakoutMinBarsSinceHigh then
                     breakoutTimer <- cfg.BreakoutTimerBars          // real breakout arms (no reset).
                 elif breakoutTimer > 0 then
                     breakoutTimer <- breakoutTimer - 1              // live breakout ticks down (no reset)...
                     if breakoutTimer = 0 then doReset <- true       // (1) ...unless it just hit 0.
                 elif isNewEmaHigh then
                     doReset <- true                                 // (2) DORMANT (timer<=0) new high that failed to arm.
                 // (no new high, timer <= 0) → dormant drought: keep accumulating, no reset.
                 if doReset then
                     pbUpVolSum <- 0.0; pbUpVolN <- 0; pbDnVolSum <- 0.0; pbDnVolN <- 0
                     pbMaxDist <- 0.0; pbAtrSum <- 0.0; pbAtrN <- 0
                 // (c) the bars-since-EMA-high counter: 0 on a fresh high, +1 otherwise (AFTER the arm check).
                 barsSinceEmaHigh <- (if isNewEmaHigh then 0 else barsSinceEmaHigh + 1)
                 // (d) session-cumulative MAX of the 9-EMA (the breakout reference). Update AFTER the high check.
                 sessMaxEma <- (match sessMaxEma with ValueSome m -> ValueSome (max m e) | ValueNone -> ValueSome e)
                 // (e) pullback-run accumulators — updn (above/below EMA volume split), deepest gap, mean log-ATR.
                 //     above/below vs the STRICTLY-PRIOR EMA. Accumulated after the reset above, so a reset bar
                 //     restarts the window WITH this bar.
                 (match sEmaPrev with
                  | ValueSome ep -> if bar.close >= ep then (pbUpVolSum <- pbUpVolSum + float bar.volume; pbUpVolN <- pbUpVolN + 1)
                                    else (pbDnVolSum <- pbDnVolSum + float bar.volume; pbDnVolN <- pbDnVolN + 1)
                  | ValueNone -> ())
                 (match sessMaxEma with
                  | ValueSome m when m > 0.0 -> let g = m / e - 1.0 in (if g > pbMaxDist then pbMaxDist <- g)
                  | _ -> ())
                 (match atrLog.State with
                  | ValueSome a -> pbAtrSum <- pbAtrSum + a; pbAtrN <- pbAtrN + 1
                  | ValueNone -> ())
             | ValueNone -> ())
            // 15m initial volume = Σ over the first 15 VALID feature-bars.
            if initVolBars < 15 then
                initVol15m  <- initVol15m + bar.volume
                initVolBars <- initVolBars + 1
            // loss-of-VWAP counter (post-fold live EMA & VWAP): extend while EMA>VWAP, reset otherwise.
            (match ema.State, this.LiveVwap with
             | ValueSome e, ValueSome v -> if e > v then emaAboveVwapBars <- emaAboveVwapBars + 1 else emaAboveVwapBars <- 0
             | _ -> ())

        // 5) advance the prior-bar pointer & bar counter (lastBar updates for EVERY bar so the ATR prior-close
        //    is the immediately-prior bar; an invalid bar still advances wall-clock time).
        lastBar   <- ValueSome bar
        barsSeen  <- barsSeen + 1

    /// Advance one open position by the current bar (immutable update). Exit precedence (first to fire wins):
    ///   stop -> pct_stop -> exhaust -> vwap_lost -> time_stop -> moc.
    member private this.Advance (bar: MinuteBar) (pos: IntradayPosition) : IntradayPosition =
        match pos.State with
        | ExitedAt _ -> pos
        | Holding ->
            // the EMA-stop LEVEL: FIXED = pos.StopLevel (frozen at entry); TRAILING = the current strictly-prior
            // 20m-min-9EMA (sEmaLow, snapshotted at ProcessBar top = the min going INTO this bar, no lookahead).
            let emaStopLevel =
                if cfg.EmaStopTrail then (match sEmaLow with ValueSome l -> l | ValueNone -> Double.NegativeInfinity)
                else pos.StopLevel
            let stopHit =
                if cfg.EmaStop then
                    // EMA-STOP: trigger when the CURRENT live 9-EMA falls below the level (fills at close).
                    emaStopLevel > Double.NegativeInfinity
                    && (match ema.State with ValueSome e -> e < emaStopLevel | ValueNone -> false)
                else
                    pos.StopLevel > Double.NegativeInfinity
                    && (if cfg.StopOnClose then bar.close <= pos.StopLevel else bar.low <= pos.StopLevel)
            let pctStopLevel = pos.EntryPx * (1.0 - cfg.PctStop)
            let pctStopHit = cfg.PctStop > 0.0 && bar.low <= pctStopLevel
            // 20m-AVG-VOLUME STOP: the live trailing-20m volume (vol20sum.State, incl. THIS bar — ProcessBar ran
            // first) has fallen below VolStopFrac × the BASIS. Volume drying up = disinterest → exit at close.
            //   ENTRY basis (default): basis = the entry's trailing-20m volume.
            //   SESSION-MIN basis (VolStopSessionMin): basis = the session-min trailing-20m volume (from 09:45) —
            //     a lower, more absolute floor than the entry basis (which drifts up entering at a volume climax).
            // For the session-min basis use the SnapshotAtEntry (fixed floor as-of entry), matching the entry-basis
            // path which is also fixed at entry — both are stable thresholds, not running floors that ratchet down.
            let volStopBasis =
                if cfg.VolStopSessionMin then pos.SessMinVol20AtEntry
                else float pos.TrailVol20mAtEntry
            let volStopHit =
                cfg.VolStopFrac > 0.0 && volStopBasis > 0.0 && not (Double.IsNaN volStopBasis)
                && (match vol20sum.State with
                    | ValueSome v -> v / volStopBasis < cfg.VolStopFrac
                    | ValueNone -> false)
            // VOLUME-SLOPE STOP: the live 20m OLS log-volume slope has rolled over below VolSlopeStop (the volume
            // TREND is negative = the move is losing fuel). More selective than the avg-volume stop. Fills at close.
            let volSlopeStopHit =
                not (Double.IsNegativeInfinity cfg.VolSlopeStop) && not (Double.IsNaN cfg.VolSlopeStop)
                && (match volOls.Slope with ValueSome s -> s < cfg.VolSlopeStop | ValueNone -> false)
            // EXHAUSTION EXIT: this bar makes a NEW SESSION HIGH (wick > strictly-prior session high) AND is a
            // VOLUME BLOW-OFF (>= mult × BOTH per-minute baselines). Fill at the bar close.
            let exhaustHit =
                cfg.ExhaustExit
                && permin20d > 0.0 && permin15m > 0.0
                && float bar.volume >= cfg.ExhaustVolMult * permin20d
                && float bar.volume >= cfg.ExhaustVolMult * permin15m
                && (match sRunHi with ValueSome h -> bar.high > h | ValueNone -> false)
            // LOSS-OF-VWAP exit: the 9-EMA had been above VWAP for >= VwapExitBars bars going into this bar
            // (strictly-prior) AND on THIS bar the live EMA is now BELOW VWAP. Fill at this bar's close.
            let vwapLostHit =
                cfg.VwapExitBars > 0
                && sEmaAboveVwapBars >= cfg.VwapExitBars
                && (match ema.State, this.LiveVwap with ValueSome e, ValueSome v -> e < v | _ -> false)
            if stopHit then
                // EMA-stop and close-based stop both fill at the bar close; wick-stop fills at the level/gap open.
                let fill = if cfg.EmaStop || cfg.StopOnClose then bar.close else min pos.StopLevel bar.``open``
                { pos with State = ExitedAt (bar.etMin, fill, "stop") }
            elif pctStopHit then
                { pos with State = ExitedAt (bar.etMin, min pctStopLevel bar.``open``, "pct_stop") }
            elif volStopHit then
                { pos with State = ExitedAt (bar.etMin, bar.close, "vol_stop") }
            elif volSlopeStopHit then
                { pos with State = ExitedAt (bar.etMin, bar.close, "vol_slope_stop") }
            elif exhaustHit then
                { pos with State = ExitedAt (bar.etMin, bar.close, "exhaust") }
            elif vwapLostHit then
                { pos with State = ExitedAt (bar.etMin, bar.close, "vwap_lost") }
            elif cfg.TimeStopMin > 0 && bar.etMin >= pos.EntryMin + cfg.TimeStopMin then
                { pos with State = ExitedAt (bar.etMin, bar.close, "time_stop") }
            elif bar.etMin >= cfg.MocMin then
                { pos with State = ExitedAt (bar.etMin, bar.close, "moc") }
            else pos

    /// Advance the whole system by one minute bar: fold the bar in, advance every open position, then
    /// (if Gate 3 fires) open a new independent position. The new position is appended AFTER existing
    /// positions advance, so an entry bar is never its own first exit-check.
    member this.Process (bar: MinuteBar) =
        this.ProcessBar bar
        for i in 0 .. positions.Count - 1 do
            positions.[i] <- this.Advance bar positions.[i]
        if this.ShouldEnterV3 bar then
            // STOP. Two modes:
            //   EMA-STOP: the stop level IS the trailing-20m MIN 9-EMA directly (NO geometry) — exit when the
            //             live 9-EMA falls below it.
            //   CLOSE geom (default, VwapReclaim/V2): d = entry - floor; stop = entry - d*StopDistFrac (2/3).
            //     floor = the 20m MIN CLOSE (closeLow, default) or the session min close (StopFloorSessMin).
            let rawStop =
                if cfg.EmaStop then
                    match sEmaLow with ValueSome l when l > 0.0 && bar.close > l -> l | _ -> Double.NegativeInfinity
                else
                    let stopFloor =
                        if cfg.StopFloorSessMin then sSessMinClose
                        else (match sCloseLow with ValueSome l -> l | ValueNone -> nan)
                    if cfg.GeomStop && not (Double.IsNaN stopFloor) && stopFloor > 0.0 && bar.close > stopFloor then
                        let d = bar.close - stopFloor
                        bar.close - d * cfg.StopDistFrac
                    else Double.NegativeInfinity
            let stop = if rawStop > 0.0 then rawStop else Double.NegativeInfinity
            let stopDistPct = if bar.close > 0.0 && stop > Double.NegativeInfinity then (bar.close - stop) / bar.close else nan
            let slopePerAtr =
                match sAtrLog with
                | ValueSome a when a > 0.0 && not (Double.IsNaN sPriceSlope) -> sPriceSlope / a
                | _ -> nan
            positions.Add
                { EntryMin = bar.etMin
                  EntryPx = bar.close
                  StopLevel = stop
                  StopDistPct = stopDistPct
                  PriceSlope20AtEntry = sPriceSlope
                  VolSlope20AtEntry = sVolSlope
                  LogAtr20AtEntry = (match sAtrLog with ValueSome a -> a | ValueNone -> nan)
                  Tightness20AtEntry = (match this.Tightness with ValueSome t -> t | ValueNone -> nan)
                  SlopePerAtrAtEntry = slopePerAtr
                  EmaLowAtEntry = (match sEmaLow with ValueSome m -> m | ValueNone -> nan)
                  SumAbove6AtEntry = sSumAbove6
                  SumAbove40AtEntry = sSumAbove40
                  SumAbove60AtEntry = sSumAbove60
                  EmaVwap30AtEntry = sEmaVwap30
                  EmaVwap60AtEntry = sEmaVwap60
                  TrailVol20mAtEntry = sTrailVol20m
                  SessMaxVol20AtEntry = sSessMaxVol20
                  SessMinVol20AtEntry = sSessMinVol20
                  EmaAtEntry = (match sEmaPrev with ValueSome e -> e | ValueNone -> nan)
                  SessMaxEmaAtEntry = sSessMaxEma
                  BreakoutTimerAtEntry = sBreakoutTimer
                  BarsSinceEmaHighAtEntry = sBarsSinceEmaHigh
                  PbUpDnAtEntry = sPbUpDn
                  PbMaxDistAtEntry = sPbMaxDist
                  PbAtrPctAtEntry = sPbAtr
                  SessMaxLogAtrAtEntry = sSessMaxLogAtr
                  SessMinCloseAtEntry = sSessMinClose
                  SessMaxCloseAtEntry = sSessMaxClose
                  SessMaxVolAtEntry = sSessMaxVol
                  VwapAtEntry = (match sVwapNow with ValueSome v -> v | ValueNone -> nan)
                  InitVol15mAtEntry = sInitVol15m
                  TrailVol5mAtEntry = sTrailVol5m
                  EntryVsSessHighAtEntry = (match sRunHi with ValueSome h when h > 0.0 -> bar.close / h - 1.0 | _ -> nan)
                  Chg20mAtEntry = (match lagPctChange lag20 with ValueSome p -> p | ValueNone -> nan)
                  CumVolAtEntry = cumVol
                  MktChgOpenAtEntry = this.MktChgOpen bar.etMin
                  MktChgPrevAtEntry = this.MktChgPrev bar.etMin
                  State = Holding }

    /// Flatten any still-open positions at the last folded bar's close (MOC / hold-to-close).
    member _.Flatten () =
        match lastBar with
        | ValueNone -> ()
        | ValueSome lb ->
            for i in 0 .. positions.Count - 1 do
                match positions.[i].State with
                | Holding -> positions.[i] <- { positions.[i] with State = ExitedAt (lb.etMin, lb.close, "moc") }
                | ExitedAt _ -> ()

    /// All positions for this (ticker, day), in entry order.
    member _.Positions = positions
