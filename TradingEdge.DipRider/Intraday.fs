module TradingEdge.DipRider.Intraday

open System
open TradingEdge.RollingMa

// =============================================================================
// MaxFlyer INTRADAY layer — Gate 3 (the trading engine).
//
// A separate per-(ticker, day) engine that mirrors the daily QullaSystem but
// folds 1-MINUTE bars instead of daily bars. The daily system SELECTS the day;
// this system TRADES it independently. Same snapshot-before-push (no-lookahead)
// discipline, same RollingMa structures, but with MULTIPLE concurrent open
// positions: every fresh new-session-high breakout (with the intraday gates
// satisfied) opens its own independent position, all held to MOC (or an
// optional intraday-low stop).
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

/// Intraday position life-cycle. The breakout may first ARM a position (no fill); it
/// transitions to Holding once its entry condition is met:
///   --trail-entry — a bar closes back through the ratcheting trail (rollover off the top).
///   --rise-entry  — price first runs a further `RiseEntry` fraction from the arm price
///                   (short into a now-parabolic move), then either fills immediately or,
///                   if --trail-entry is also on, waits for the rollover after that.
/// With neither flag the breakout creates a Holding position directly (Armed is unused).
type IntraPosState =
    // armMin = arming bar; trailLevel = ratcheting 2-bar extreme; armPrice = breakout-bar
    // price (the --rise-entry anchor); riseCleared = has the +RiseEntry move happened yet.
    | Armed of armMin: int * trailLevel: float * armPrice: float * riseCleared: bool
    | Skipped                                                     // armed but a gate refused the fill → no trip
    | Holding
    | ExitedAt of exitMin: int * exitPx: float * reason: string   // "moc"|"intraday_stop"|"session_stop"|"pct_stop"|"target"|"time_stop"

/// One intraday trip. `EntryMin`/`EntryPx` are the FILL values (the breakout bar, or
/// the rollover bar under trail-entry); the *_at_entry metrics are the intraday
/// indicator snapshots at the ARMING (breakout) bar.
type IntradayPosition =
    { EntryMin: int
      EntryPx: float
      StopLevel: float         // protective-stop level. Direct entry: the 2-bar local extreme at the
                               // breakout (upside = 2-bar low, downside = 2-bar high); fires when price
                               // reaches it (low<=level / high>=level). Under --trail-entry this is the
                               // SESSION extreme at fill instead (the high for a short, the low for a long):
                               // we're stopped out if price runs back to a fresh session extreme.
      BreakoutRef: float       // the running session extreme (strictly-prior bars) the breakout cleared —
                               // the session high for an upside breakout, the session low for a downside one
      TargetLevel: float       // VWAP-reclaim profit target = VWAP + (VWAP - sessionLow), snapshotted at
                               // entry (nan for the breakout engine). Long-only; a resting limit above.
      RunBelowVwapAtEntry: int // consecutive bars EMA<VWAP right before the cross (0 for the breakout engine)
      StopDistPct: float       // stop DISTANCE as a fraction of entry price = (entry - stopLevel)/entry.
                               // The recorded var for the "do too-tight stops get chopped?" breakdown. nan for breakout.
      AtrPctAtEntry: float     // intraday log-ATR snapshot at the arming bar
      TightnessAtEntry: float  // intraday tightness snapshot at the arming bar
      CumVolAtEntry: int64     // cumulative day volume THROUGH the entry bar (rvol numerator; recorded feature)
      BreakoutBarVol: int64    // the entry (breakout) bar's own volume (recorded feature)
      NewVolHigh: bool         // did the entry bar make a NEW session 1m-volume high (vs strictly-prior bars)?
                               // Always true at VolHighFrac>=1.0 (the gate enforces it); the informative case
                               // is a relaxed/off gate, where this flags which entries would pass the strict gate.
      VolVsHigh: float         // breakout-bar volume / running session 1m-vol high (pre-push). The CONTINUOUS
                               // version of NewVolHigh: >=1.0 = a new high; 0.8 = 80% of the high. Lets a
                               // relaxed run (--vol-high-frac 0.8) be sliced post-hoc to find the knee. (runVolHi
                               // is not in the CSV otherwise, so this must be recorded here.)
      BreakoutBarOpen: float   // the entry (breakout) bar's OPEN — for the 1m entry-bar %-change (close/open-1)
      PrevBarClose: float      // the strictly-prior 1m bar's CLOSE — for the 1m flush = close/prevClose-1
      Chg20mAtEntry: float     // 20-bar (20-minute) %-change into entry (entry close / close 20 bars ago - 1); nan if <20 bars
      Vol20mAvgAtEntry: float   // trailing 20-bar MEAN 1m volume at entry (incl. the cross bar); nan if <20 bars.
                               // "volume during the convergence" — compared to the 20d/min & 15m/min baselines
                               // in toTrip to get rvol20m_20d and rvol20m_15m (Jeff's rising-volume cue).
      RunMaxDistAtEntry: float  // max (VWAP-EMA)/VWAP over the pre-cross run — how DEEP the 9-EMA fell below
                               // VWAP (the run's depth as a fraction). "Are bigger %-runs better trades?"
      RunAtrAtEntry: float      // mean per-bar log true range OVER THE RUN bars (reset at each cross) — the
                               // run's own volatility, NOT the trailing-window ATR. distance/this = depth in
                               // ATR-units (RunMaxDist/RunAtr) is derived in toTrip.
      RunUpVolAtEntry: float    // mean 1m volume of ABOVE-9EMA bars since the last VWAP cross (accumulation /
                               // rising-side volume). nan if no above-EMA bars this run.
      RunDnVolAtEntry: float    // mean 1m volume of BELOW-9EMA bars since the last VWAP cross (distribution /
                               // falling-side volume). Up/Dn ratio (in toTrip) = the volume-conviction signal.
      // ----- DipRider features (the four handcrafted pullback-continuation features; -1/nan = n/a) -----
      BarsSinceHiAtEntry: int    // # bars since the session PRICE high, at the re-break (recency of push #1)
      BarsSinceVolHiAtEntry: int // # bars since the session max-1m-VOLUME high (recency of peak interest)
      BarsBelowEmaAtEntry: int   // # consecutive bars closed below the 9-EMA right before the re-break (pullback depth)
      TrendPctAtEntry: float     // re-break close / session open - 1 (how far up the session the trend had run)
      State: IntraPosState }   // immutable — advancing a position returns a NEW record (HighFlyer style)

/// Mean-reversion TARGET — where a (short) position covers when price reverts back
/// to the anchor. For a short, "reverted" = price falls to/through the anchor, so
/// the target doubles as the loss-cut: if price never comes back, the trade rides to
/// MOC (the loss case). This retires the protective stop for the mean-reversion book.
type Target =
    | NoTarget                 // hold to MOC / time-stop only
    | Vwap                     // session VWAP (running, bar-typical-price weighted, from SessionStartMin)
    | Ma of window: int        // fast simple moving average of closes over `window` 1m bars
    | Channel of window: int   // Donchian low: min low over the prior `window` 1m bars (pre-breakout range floor)

/// Intraday Gate-3 + engine config.
type IntradayConfig =
    { VolWindow: int           // intraday ATR/tightness lookback in 1m BARS (swept; default ~20)
      MaxTightness: float      // intraday tightness gate (per-bar)
      MaxAtrPct: float         // intraday ATR% gate (log-space, per-bar)
      SessionStartMin: int     // first ET minute fed to the engine (510 = 08:30 ET — the SMB "1h
                               // opening range"; the running high/low/volume accumulate from here).
      EntryStartMin: int       // earliest ET minute an entry may fire (575 = 09:35). The engine
                               // PROCESSES premarket bars (to warm the running extremes) but does not
                               // TRADE before this wall-clock floor.
      EntryEndMin: int         // LATEST ET minute an entry may fire (VwapReclaim: 810 = 13:30, the morning
                               // window per the video — afternoon reclaims fade; Finding 4). Positions
                               // opened before this still hold to MOC. 0 / >=MocMin = no upper bound.
      UseStop: bool            // arm the intraday-low protective stop
      PctStop: float           // wide catastrophe %-stop: a fixed adverse excursion from entry (0 = off;
                               // e.g. 0.5 = stop if price moves 50% AGAINST the position). Independent of
                               // UseStop — meant to clip the run-over tail (a short that doubles on you)
                               // while leaving normal noise untouched. Short: stop on bar.high >= entry*(1+x);
                               // long: bar.low <= entry*(1-x). Fills at the level, gap-through at the open.
      TimeStopMin: int         // time-stop: flatten this many minutes after entry (capped at MOC);
                               // 0 = off. Side-independent. Fires before the protective stop / MOC
                               // only if it lands earlier.
      Downside: bool           // breakout DIRECTION. false (default) = upside breakout to a new session
                               // HIGH; true = downside breakout to a new session LOW. Independent of
                               // Short: Downside chooses the signal, Short chooses the P&L sign. The
                               // protective stop flips with it (2-bar low for upside, 2-bar high for down).
      WickBreakout: bool       // breakout TRIGGER style. false (default) = CLOSE through the prior
                               // session extreme; true = the bar's HIGH/LOW WICK merely pierces it (even
                               // if it closes back inside). Wick fires more/earlier; admits weaker pierces.
      ExtGate: float           // CONDITIONAL day-extension entry gate (0 = off; 0.5 = 50% vs prev close).
                               // At the breakout: if day-extension >= ExtGate already → enter DIRECT (the
                               // name is parabolic now). If < ExtGate → arm a ROLLOVER (trail-entry style)
                               // and take it ONLY if day-extension >= ExtGate by the time it triggers;
                               // otherwise skip (no trade). Short side; uses prevClose. Distinct from
                               // RiseEntry (which measures % from the breakout PRICE, not the day).
      RiseEntry: float         // entry GATE: require price to first run this fraction past the arm
                               // (breakout) price before a short may enter — i.e. wait for a further
                               // parabolic move and short THAT, not the breakout (0 = off; 0.5 = +50%).
                               // Short anchors above (armPrice*(1+x)), long below (armPrice*(1-x)). Arms
                               // a position like trail-entry; with --trail-entry it ALSO waits for the
                               // rollover after the rise, otherwise it fills immediately at the level.
      TrailEntry: bool         // entry MODEL. false (default) = enter immediately on the breakout bar.
                               // true = the breakout only ARMS; track a trailing 2-bar extreme that
                               // ratchets WITH the move (up for a short / down for a long), and enter only
                               // when a bar CLOSES back through it (the rollover off the top/bottom). The
                               // protective stop then sits at the SESSION extreme at fill. Opt-in.
      Short: bool              // trade the breakout SHORT (fade) instead of long. Flips the P&L sign
                               // only; the breakout entry signal is unchanged. The intraday-low
                               // protective stop is wrong-sided for a short, so leave UseStop off when
                               // Short (the first short experiments run stopless + time-stopped).
      Target: Target           // mean-reversion cover target (NoTarget by default). For a short, the
                               // position covers when the bar's low reaches the (snapshotted, strictly-
                               // prior) anchor; doubles as the loss-cut (no separate stop needed).
      MocMin: int              // MOC cutoff in ET minutes (960 = 16:00)
      MaxConcurrent: int       // cap on currently-OPEN (Holding) positions; 0 = unlimited
      MinBarFlush: float       // entry-bar FLUSH gate: require close/prevClose-1 <= this (a real flush
                               // candle, not a one-tick poke below the reference). 0.0 (default) = OFF.
                               // e.g. -0.007 rejects bars whose 1m move is softer than -0.7%. Uses the
                               // strictly-prior bar's close (sLastBar); ValueNone prior bar => not gated.
      MinBarFlushFloor: float  // entry-bar flush DEPTH floor (the falling-knife cut, Run 26): reject the
                               // breakout bar if its 1m move is DEEPER than this. 0.0 (default) = OFF.
                               // Long/Downside: require close/prevClose-1 >= MinBarFlushFloor (e.g. >= -0.12
                               // rejects flushes deeper than -12%). Upside/short: mirror (<= -MinBarFlushFloor).
                               // Pairs with MinBarFlush to make an entry-bar move BAND [floor, ceiling].
      MinCloseRef: bool        // breakout REFERENCE level. false (default) = the running min-LOW
                               // (max-HIGH upside) of strictly-prior bars. true = the running min-
                               // CLOSE (max-CLOSE upside) — closes-only, so a long lower/upper WICK
                               // can't push the channel boundary. The TRIGGER stays close-based;
                               // this only changes what level the close must clear.
      // ----- SMB VWAP x 9-EMA reclaim (long-only) -----
      VwapReclaim: bool        // MASTER SWITCH. true = ignore the breakout gates above and use the
                               // VWAP-reclaim entry: go LONG when the EMA (EmaPeriod) crosses ABOVE the
                               // session VWAP, having spent > BelowVwapFrac of the pre-cross session BELOW
                               // it, with rvol_0945 in play. false (default) = the original breakout engine
                               // (so the fork stays byte-parity with LowFlyer when off).
      EmaPeriod: int           // the fast EMA period for the reclaim (SMB default 9).
      BelowVwapFrac: float     // require the EMA to have been below VWAP for > this FRACTION of the
                               // pre-cross session bars (a genuine reclaim of sustained weakness, not chop
                               // across VWAP). Swept (0.5/0.6/0.75/0.9). 0 = off.
      MinRunBelowVwap: int     // require >= this many CONSECUTIVE bars EMA<VWAP immediately before the
                               // cross (the IMMEDIACY of the weakness — a sustained downtrend into the
                               // reclaim, not a chop-across). 0 = off. An alternative to BelowVwapFrac.
      StopAnchorVwap: bool     // stop anchor: true (default) = VWAP - d*StopDistFrac (a fixed level off
                               // VWAP); false = entry - d*StopDistFrac (floats with the fill).
                               // Target is VWAP + (VWAP-sessionLow) either way.
      StopDistFrac: float      // stop DISTANCE as a fraction of d = (VWAP-sessionLow). Default 2/3.
                               // larger = WIDER stop. The GEOMETRIC stop (ignored if FixedPctStop > 0).
      FixedPctStop: float      // if > 0, use a FIXED %-stop = entry*(1-this) INSTEAD of the d-geometry stop
                               // (Finding 17: does the stop's edge come from the VWAP-low geometry or just
                               // from having a cut at a sensible distance?). 0 (default) = use the geometry.
      MinStopDistPct: float    // MIN stop distance as a fraction of entry (Finding 7). When the geometric
                               // stop is tighter than this, either SKIP the trade (ClampStopDist=false) or
                               // CLAMP the stop to this distance (true). Default 0.01 (1%). 0 = off.
      ClampStopDist: bool      // true = CLAMP a too-tight stop to MinStopDistPct (keep the trade, widen the
                               // stop); false (default) = SKIP the trade entirely (Finding 7's original).
      MinTightness: float      // MIN intraday tightness at entry (Finding 6): require a name with real range
                               // (tight >= this), not a dead-flat chop. Default 4.5. 0 = off.
      StopOnClose: bool        // true (default) = the stop triggers only when a bar CLOSES at/below the stop
                               // level (ignores random low wicks that immediately recover), filling at that
                               // close. false = the old wick stop (bar.low <= level, fills at the level).
      UseTarget: bool          // true (default) = exit at the VWAP+d profit target. false = NO target — let
                               // winners run to the time-stop / MOC (does the target cut winners short?).
      ReclaimShort: bool       // MIRROR the reclaim to the SHORT side: enter when the 9-EMA crosses BELOW
                               // VWAP after sustained STRENGTH (EMA above VWAP for the run). Geometry flips:
                               // d = sessionHIGH - VWAP; target = VWAP - d (below); stop = VWAP + d*frac
                               // (above); P&L is short. The weakness run/frac gates read the ABOVE-VWAP
                               // counters. false (default) = the long reclaim. Tightness/ADV/rvol gates
                               // and StopDistFrac/UseTarget/StopOnClose all apply symmetrically.
      VolHighFrac: float       // VOLUME-CONFIRMATION gate as a FRACTION of the running session max 1m-bar
                               // volume: require bar.volume >= VolHighFrac * runVolHi. 1.0 (default) = the
                               // original "must EXCEED the vol high" gate (a new-vol-high bar). 0.95 / 0.90
                               // relax it to "within 5% / 10% of the high" — probes whether the edge is in
                               // MAKING a new vol high or merely being NEAR elevated volume, recovering trips.
                               // 0.0 = OFF (fire on the first new-session-extreme bar regardless of volume).
                               // NOTE: below 1.0 the `new_vol_high` column still records the STRICT test
                               // (bar > runVolHi), so a relaxed run can still be split on true-new-high vs not.
      // ----- DipRider: pullback-in-uptrend re-break (long-only) -----
      DipRider: bool           // MASTER SWITCH (3rd; exclusive with VwapReclaim / the breakout engine).
                               // true = the pullback-continuation entry: after an established intraday
                               // UPTREND, price pulls back (closes below the 9-EMA for a stretch), then a
                               // RE-BREAK bar closes >= DipRebreakAtr*ATR% above the PRIOR bar's HIGH — buy
                               // the resumption. false (default) = off (keeps the other two engines intact).
      DipRebreakAtr: float     // re-break trigger SIZE: require bar.close >= prevBar.high * (1 + k*ATR%),
                               // ATR% = intraday per-bar log-ATR. k (default 0.5) = how decisive the
                               // expansion bar must be. 0 = any close above the prior bar's high.
      DipMinBarsBelowEma: int  // require >= this many CONSECUTIVE bars CLOSED below the 9-EMA immediately
                               // before the re-break (the pullback DEPTH — a real dip, not a one-bar wiggle).
                               // 0 = off. The EMA-vs-close mirror of MinRunBelowVwap.
      DipMinTrendPct: float     // UPTREND precondition: require the re-break close to be >= this fraction
                               // above the session OPEN (an established up-move to pull back FROM). 0 = off.
                               // e.g. 0.02 = must be >= 2% up on the session at the re-break.
      DipExitNewHigh: bool }   // EXIT to NEW HIGHS: cover when a bar's HIGH exceeds the session high that
                               // stood at entry (TargetLevel) — the resumption completed the move, book it.
                               // Combined with TimeStopMin (5-30m) as the fallback if it never re-breaks:
                               // whichever fires first. false = hold-to-MOC (+ stop/time-stop). Default true.

/// Per-(ticker, day) intraday engine. Feed it the day's RTH MinuteBar[] in time
/// order via `Process`, then `Finalize` and read `Trips()`.
type IntradaySystem(cfg: IntradayConfig, ticker: string, day: DateOnly, prevClose: float) =

    // day extension at a bar = close vs the prior daily close (how far up/down on the day).
    // Used by --ext-gate to route direct-vs-rollover entry and gate the rollover fill.
    let extension (px: float) = if prevClose > 0.0 then px / prevClose - 1.0 else 0.0

    // ----- rolling intraday structures (1m timeframe) -----
    let atrLog    = AvgMa(cfg.VolWindow)        // mean LOG true range over the last VolWindow 1m bars
    let atrLin    = AvgMa(cfg.VolWindow)        // mean ABSOLUTE true range (linear)
    let lag20     = LagMa<float>(20)            // 20-bar close delay line -> the 20-minute %-change into entry
    let vol20     = AvgMa(20)                    // mean 1m VOLUME over the last 20 bars -> "volume during the
                                                // convergence" (Jeff's rising-volume-into-the-reclaim cue).
                                                // Compared at entry vs the 20d/min and 15m/min baselines.
    let rangeHigh = MaxMa(cfg.VolWindow)        // intraday tightness: max high over the window
    let rangeLow  = MinMa(cfg.VolWindow)        // intraday tightness: min low over the window

    // ----- mean-reversion TARGET anchors (only the one named by cfg.Target is fed) -----
    // session VWAP = Σ(typical_price · volume) / Σ(volume), typical = (h+l+c)/3, from
    // SessionStartMin. Bar-based (the 1m parquet carries no trade-exact VWAP) — adequate
    // for a 1m mean-reversion anchor.
    let mutable vwapNum  = 0.0                   // Σ tp·v
    let mutable vwapDen  = 0.0                   // Σ v
    // ----- SMB VWAP-reclaim state (only used when cfg.VwapReclaim) -----
    let ema        = EmaMa(cfg.EmaPeriod)        // the fast reclaim EMA (9)
    let mutable belowVwapBars = 0                // # bars where EMA < VWAP, from session start (strictly-prior)
    let mutable emaVwapBars   = 0                // # bars where both EMA & VWAP were defined (the counter denom)
    let mutable runBelowVwap  = 0                // CONSECUTIVE bars EMA<VWAP up to now (resets to 0 on EMA>=VWAP)
    let mutable aboveVwapBars = 0                // # bars where EMA > VWAP (the short mirror of belowVwapBars)
    let mutable runAboveVwap  = 0                // CONSECUTIVE bars EMA>VWAP up to now (resets on EMA<=VWAP)
    // depth-of-the-run features (measured OVER the current consecutive below-VWAP streak; reset when it
    // breaks). runMaxDist = max (VWAP-EMA)/VWAP during the run (how far the 9-EMA fell below VWAP);
    // runAtrSum/runAtrN = mean per-bar log true range over the run bars (the run's volatility). Short
    // mirror uses (EMA-VWAP)/VWAP over the ABOVE streak.
    let mutable runMaxDist = 0.0                 // max fractional VWAP<->EMA gap seen this run
    let mutable runAtrSum  = 0.0                 // Σ per-bar log-TR over this run's bars
    let mutable runAtrN    = 0                   // # bars contributing to runAtrSum
    // Up/Down VOLUME split since the last VWAP cross (reset at each cross, like the run accumulators).
    // A bar whose CLOSE is above the (strictly-prior) 9-EMA feeds the Up bucket; below feeds Down. The
    // Up/Down MEAN-volume RATIO measures whether volume is flowing into the RISING side (accumulation /
    // convergence back toward VWAP) or the FALLING side (distribution / divergence) of the run.
    let mutable runUpVolSum = 0.0                // Σ volume of above-EMA (rising) bars this run
    let mutable runUpVolN   = 0                  // # above-EMA bars
    let mutable runDnVolSum = 0.0                // Σ volume of below-EMA (falling) bars this run
    let mutable runDnVolN   = 0                  // # below-EMA bars
    // ----- DipRider state (only used when cfg.DipRider) -----
    // bar index (0-based, over folded bars) of the LAST time the running session HIGH / max-1m-VOLUME
    // updated. barsSince* at a bar = barsSeen - hiBarIdx (how many bars ago the last high/vol-high was).
    // Recency of the first push (price) and of peak interest (volume) — the "how stale is the move" pair.
    let mutable hiBarIdx    = -1                 // barsSeen index at the last runHi update (-1 = none yet)
    let mutable volHiBarIdx = -1                 // barsSeen index at the last runVolHi update
    let mutable barsBelowEma = 0                 // CONSECUTIVE bars whose CLOSE was below the 9-EMA (resets
                                                 // to 0 when a bar closes at/above the EMA) — the pullback depth
    let maTgt      = match cfg.Target with Ma w -> AvgMa(w)      | _ -> AvgMa(1)   // fast SMA of closes
    let chanTgt    = match cfg.Target with Channel w -> MinMa(w) | _ -> MinMa(1)   // Donchian low (pre-breakout floor)

    // session-cumulative running extremes over ALL bars seen so far (NOT windowed),
    // accumulating from SessionStartMin (08:30 ET). The breakout reference is the high
    // of all strictly-prior bars; runVolHi is the max single-bar VOLUME so far — the
    // volume-confirmation reference (a real breakout re-takes the session volume high,
    // which normally only happens at the open/close).
    let mutable runHi    : float voption = ValueNone
    let mutable runLo    : float voption = ValueNone
    // parallel CLOSE-based running extremes (min/max over strictly-prior CLOSES), used
    // as the breakout reference when cfg.MinCloseRef is on — closes-only, wick-immune.
    let mutable runHiClose : float voption = ValueNone
    let mutable runLoClose : float voption = ValueNone
    let mutable runVolHi : int64 voption = ValueNone
    // cumulative day volume from SessionStartMin (09:30). Read (post-fold) when a
    // position is created it is the cumulative volume THROUGH the entry bar (incl. it)
    // — the rvol numerator (a recorded feature, not a gate).
    let mutable cumVol      : int64 = 0L
    // the day's session open = the FIRST folded bar's open (09:30). Cross-checks the
    // SQL day_open (they must match); exposed for an optional engine-side %-since-open.
    let mutable sessionOpen : float voption = ValueNone
    // the most recent bar folded. Read at the TOP of ProcessBar (before the current
    // bar is pushed) it IS the immediately-prior bar — the source of both the true-range
    // prior close and the 2-bar-stop prior low; read by Flatten it's the day's last bar.
    let mutable lastBar  : MinuteBar voption = ValueNone
    let mutable barsSeen  = 0

    // ----- pre-push snapshots (state going INTO the current bar; no lookahead) -----
    let mutable sRunHi      : float voption = ValueNone
    let mutable sRunLo      : float voption = ValueNone
    let mutable sRunHiClose : float voption = ValueNone
    let mutable sRunLoClose : float voption = ValueNone
    let mutable sRunVolHi   : int64 voption = ValueNone
    let mutable sLastBar   : MinuteBar voption = ValueNone   // the prior bar, as-of going into this bar (its low feeds the 2-bar stop)
    let mutable sAtrLog    : float voption = ValueNone
    let mutable sAtrLin    : float voption = ValueNone
    let mutable sRangeHigh : float voption = ValueNone
    let mutable sRangeLow  : float voption = ValueNone
    // the mean-reversion cover anchor (cfg.Target), as-of going INTO this bar (strictly-
    // prior). ValueNone until the chosen anchor is warm / when Target = NoTarget.
    let mutable sTargetAnchor : float voption = ValueNone
    // ----- VWAP-reclaim snapshots: the PRIOR bar's EMA & VWAP (for cross detection) -----
    let mutable sEmaPrev  : float voption = ValueNone   // EMA going INTO this bar (strictly-prior)
    let mutable sVwapPrev : float voption = ValueNone   // session VWAP going INTO this bar (strictly-prior)
    let mutable sBelowVwapFrac : float voption = ValueNone   // frac of pre-cross bars with EMA<VWAP (strictly-prior)
    let mutable sRunBelowVwap  : int = 0                     // CONSECUTIVE bars EMA<VWAP going INTO this bar (strictly-prior)
    let mutable sAboveVwapFrac : float voption = ValueNone   // short mirror: frac of pre-cross bars with EMA>VWAP
    let mutable sRunAboveVwap  : int = 0                     // short mirror: CONSECUTIVE bars EMA>VWAP into this bar
    // ----- DipRider snapshots (strictly-prior state going INTO this bar; no lookahead) -----
    let mutable sBarsSinceHi     : int = -1                  // barsSeen - hiBarIdx as-of this bar's start (-1 = no high yet)
    let mutable sBarsSinceVolHi  : int = -1                  // barsSeen - volHiBarIdx as-of this bar's start
    let mutable sBarsBelowEma    : int = 0                   // consecutive bars closed below the 9-EMA into this bar (the pullback)
    let mutable sEmaPrevDip      : float voption = ValueNone // the prior 9-EMA (for the trend/pullback context; = sEmaPrev)

    let positions = ResizeArray<IntradayPosition>()

    member _.Ticker = ticker
    member _.Day = day
    member _.BarsSeen = barsSeen
    /// The session open = the first folded bar's open (09:30). Cross-checks the SQL day_open.
    member _.SessionOpen = sessionOpen

    // ----- read members (mirror the daily QullaSystem) -----
    /// The strictly-prior session extreme the breakout must clear — the running high for
    /// an upside breakout, the running low for a downside one. cfg.MinCloseRef selects
    /// the CLOSE-based extreme (wick-immune) instead of the low/high (wick) extreme.
    member _.BreakoutRef =
        match cfg.Downside, cfg.MinCloseRef with
        | true,  false -> sRunLo
        | true,  true  -> sRunLoClose
        | false, false -> sRunHi
        | false, true  -> sRunHiClose
    /// Running max single-bar volume over strictly-prior bars (the volume-confirmation reference).
    member _.RunVolHigh = sRunVolHi
    member _.AtrLog = sAtrLog
    /// ATR% stand-in = the intraday log-ATR directly.
    member _.AtrPct = sAtrLog
    member _.RangeAbs =
        match sRangeHigh, sRangeLow with
        | ValueSome hi, ValueSome lo -> ValueSome (hi - lo)
        | _ -> ValueNone
    /// Intraday consolidation tightness = abs range / abs ATR (LINEAR; log mode removed).
    member this.Tightness =
        match this.RangeAbs, sAtrLin with
        | ValueSome r, ValueSome atr when atr <> 0.0 -> ValueSome (r / atr)
        | _ -> ValueNone

    // ----- VWAP-reclaim accessors -----
    /// The LIVE session VWAP (post-fold, incl. the current bar) — always accumulates for
    /// this engine, independent of cfg.Target. ValueNone before any volume.
    member private _.LiveVwap : float voption =
        if vwapDen > 0.0 then ValueSome (vwapNum / vwapDen) else ValueNone
    /// The running SESSION LOW (post-fold, incl. the current bar) — the sessionLow used in
    /// the stop/target geometry, read at the entry (cross) bar.
    member _.SessionLow = runLo
    /// The fraction of pre-cross session bars the EMA spent BELOW VWAP (strictly-prior snapshot).
    member _.BelowVwapFrac = sBelowVwapFrac
    /// CONSECUTIVE bars the EMA was below VWAP immediately before this bar (strictly-prior).
    /// Measures the IMMEDIACY/strength of the weakness being reclaimed (vs the whole-session frac).
    member _.RunBelowVwap = sRunBelowVwap
    /// The mean-reversion cover anchor (cfg.Target), as-of going INTO the current bar.
    member _.TargetAnchor = sTargetAnchor

    /// The LIVE target anchor (post-fold) for the chosen cfg.Target — used by ProcessBar
    /// to snapshot the strictly-prior value. ValueNone for NoTarget / before warm.
    member private _.LiveTargetAnchor : float voption =
        match cfg.Target with
        | NoTarget -> ValueNone
        | Vwap -> if vwapDen > 0.0 then ValueSome (vwapNum / vwapDen) else ValueNone
        | Ma _ -> maTgt.State
        | Channel _ -> chanTgt.State

    /// Count of currently-open positions — Holding, plus Armed (a committed pending
    /// trail-entry signal counts against concurrency too).
    member _.OpenCount =
        let mutable n = 0
        for p in positions do (match p.State with Holding | Armed _ -> n <- n + 1 | ExitedAt _ | Skipped -> ())
        n

    /// Gate 3 — does THIS bar trigger a new-high breakout entry? Reads pre-push
    /// snapshots, so it must be evaluated AFTER ProcessBar(bar). A new SESSION high
    /// (close above the running high of strictly-prior bars from 08:30) that ALSO
    /// re-takes the session 1m-volume high, with the intraday tightness/ATR% gates
    /// satisfied, past the 09:35 trading floor, and concurrency room.
    /// SMB VWAP-reclaim entry (long-only). Fires when the fast EMA crosses ABOVE the
    /// session VWAP on THIS bar (prior EMA <= prior VWAP; this-bar EMA > this-bar VWAP —
    /// both this-bar values incorporate this bar's own close, which is the fill point, so
    /// no lookahead), having spent > BelowVwapFrac of the pre-cross session below VWAP,
    /// past the trading floor, in play (rvol gate applied upstream), tightness/ATR% ok,
    /// concurrency room. Fills at the cross-bar close.
    member this.ShouldEnterReclaim (bar: MinuteBar) : bool =
        let inline gate (v: _ voption) (test: _ -> bool) =
            match v with ValueSome x -> test x | ValueNone -> false
        // inside the wall-clock trading window [EntryStartMin, EntryEndMin]. EntryEndMin<=0 or
        // >=MocMin = no upper bound (all-day). Default 13:30 morning window (Finding 4).
        bar.etMin >= cfg.EntryStartMin
        && (cfg.EntryEndMin <= 0 || cfg.EntryEndMin >= cfg.MocMin || bar.etMin <= cfg.EntryEndMin)
        // the CROSS: long = EMA at/below VWAP going in, now ABOVE (reclaim). short (ReclaimShort) = EMA
        // at/above VWAP going in, now BELOW (loss-of-VWAP rejection).
        && (match sEmaPrev, sVwapPrev, ema.State, this.LiveVwap with
            | ValueSome ep, ValueSome vp, ValueSome ec, ValueSome vc ->
                if cfg.ReclaimShort then ep >= vp && ec < vc else ep <= vp && ec > vc
            | _ -> false)
        // sustained trend into the cross: long needs > BelowVwapFrac of pre-cross bars BELOW VWAP;
        // short needs > BelowVwapFrac ABOVE VWAP (same knob, mirrored counter).
        && (cfg.BelowVwapFrac <= 0.0
            || gate (if cfg.ReclaimShort then sAboveVwapFrac else sBelowVwapFrac) (fun f -> f > cfg.BelowVwapFrac))
        // IMMEDIATE trend: >= MinRunBelowVwap CONSECUTIVE bars on the trend side right before the cross
        // (a strong regime-flip, vs a chop-across). 0 = off. Long reads run-below, short reads run-above.
        && (cfg.MinRunBelowVwap <= 0
            || (if cfg.ReclaimShort then sRunAboveVwap else sRunBelowVwap) >= cfg.MinRunBelowVwap)
        // intraday tightness FLOOR (Finding 6): require a name with real range, not a dead chop.
        && (cfg.MinTightness <= 0.0 || gate this.Tightness (fun t -> t >= cfg.MinTightness))
        // intraday tightness / ATR% CEILINGS (optional; +inf disables — same as the breakout path).
        && (Double.IsInfinity cfg.MaxTightness || gate this.Tightness (fun t -> t < cfg.MaxTightness))
        && (Double.IsInfinity cfg.MaxAtrPct   || gate this.AtrPct    (fun a -> a < cfg.MaxAtrPct))
        // concurrency room.
        && (cfg.MaxConcurrent <= 0 || this.OpenCount < cfg.MaxConcurrent)

    /// DipRider entry (long-only). Fires when, after an established intraday UPTREND and a PULLBACK
    /// (>= DipMinBarsBelowEma consecutive bars closed below the 9-EMA), THIS bar RE-BREAKS: its close
    /// clears the STRICTLY-PRIOR bar's high by >= DipRebreakAtr * ATR% (the intraday per-bar log-ATR).
    /// The re-break bar's own close is the fill (its close is used in the trigger, so no lookahead).
    member this.ShouldEnterDip (bar: MinuteBar) : bool =
        let inline gate (v: _ voption) (test: _ -> bool) =
            match v with ValueSome x -> test x | ValueNone -> false
        // trading window [EntryStartMin, EntryEndMin] (EntryEndMin<=0 / >=MOC = all-day).
        bar.etMin >= cfg.EntryStartMin
        && (cfg.EntryEndMin <= 0 || cfg.EntryEndMin >= cfg.MocMin || bar.etMin <= cfg.EntryEndMin)
        // the RE-BREAK: close clears the strictly-prior bar's HIGH by >= k*ATR% (an expansion bar). ATR%
        // = the intraday per-bar log-ATR snapshot (sAtrLog). Require both a prior bar and a warm ATR.
        && (match sLastBar, sAtrLog with
            | ValueSome prev, ValueSome atr when prev.high > 0.0 ->
                bar.close >= prev.high * (1.0 + cfg.DipRebreakAtr * atr)
            | _ -> false)
        // the PULLBACK: >= DipMinBarsBelowEma consecutive bars closed below the 9-EMA right before this
        // bar (a genuine dip to buy the resumption of, not a one-bar wiggle). 0 = off.
        && (cfg.DipMinBarsBelowEma <= 0 || sBarsBelowEma >= cfg.DipMinBarsBelowEma)
        // the UPTREND precondition: the re-break close is >= DipMinTrendPct above the session open (an
        // established up-move to have pulled back FROM). 0 = off.
        && (cfg.DipMinTrendPct <= 0.0
            || gate sessionOpen (fun o -> o > 0.0 && bar.close / o - 1.0 >= cfg.DipMinTrendPct))
        // intraday tightness floor / ceilings + ATR% ceiling (same optional gates as the reclaim).
        && (cfg.MinTightness <= 0.0 || gate this.Tightness (fun t -> t >= cfg.MinTightness))
        && (Double.IsInfinity cfg.MaxTightness || gate this.Tightness (fun t -> t < cfg.MaxTightness))
        && (Double.IsInfinity cfg.MaxAtrPct   || gate this.AtrPct    (fun a -> a < cfg.MaxAtrPct))
        // concurrency room.
        && (cfg.MaxConcurrent <= 0 || this.OpenCount < cfg.MaxConcurrent)

    member this.ShouldEnter (bar: MinuteBar) : bool =
        let inline gate (v: _ voption) (test: _ -> bool) =
            match v with ValueSome x -> test x | ValueNone -> false
        if cfg.DipRider then this.ShouldEnterDip bar
        elif cfg.VwapReclaim then this.ShouldEnterReclaim bar
        else
        // past the wall-clock trading floor (09:35). The engine processes premarket
        // bars from 08:30 to warm the running extremes, but does not TRADE before this.
        bar.etMin >= cfg.EntryStartMin
        // new session extreme vs strictly-prior bars (08:30-onward running high/low).
        // Two trigger styles (cfg.WickBreakout):
        //   false (default) — CLOSE breakout: the bar must CLOSE through the prior extreme
        //                     (close > runHi upside / close < runLo downside).
        //   true            — WICK breakout: the bar's HIGH (upside) / LOW (downside) merely
        //                     PIERCES the prior extreme, even if it closes back inside.
        // The wick trigger fires more often (and earlier) and admits the weaker pierce-but-
        // close-inside breakouts.
        && gate this.BreakoutRef (fun ext ->
            match cfg.Downside, cfg.WickBreakout with
            | false, false -> bar.close > ext
            | false, true  -> bar.high  > ext
            | true,  false -> bar.close < ext
            | true,  true  -> bar.low   < ext)
        // VOLUME CONFIRMATION (cfg.VolHighFrac): the breakout bar's volume vs the strictly-prior
        // session 1m-volume high. 1.0 (default) = must EXCEED it (a new-vol-high bar) — most
        // breakouts never re-take the vol high, so a morning bar that does is a significant event.
        // <1.0 relaxes to "within (1-frac) of the high" (0.95/0.90); 0.0 = OFF (--no-vol-high).
        && (cfg.VolHighFrac <= 0.0
            || gate sRunVolHi (fun vh ->
                   if cfg.VolHighFrac >= 1.0 then float bar.volume > float vh          // strict >, original behavior
                   else float bar.volume >= cfg.VolHighFrac * float vh))
        // ENTRY-BAR FLUSH gate (cfg.MinBarFlush, 0 = off): the breakout bar's own 1m move
        // = close/prevClose-1 must be a real flush, not a one-tick poke below the reference.
        // Downside/long: require the move <= MinBarFlush (e.g. <= -0.7%). Upside: mirror
        // (>= -MinBarFlush). ValueNone prior close (only the session-open bar) => not gated,
        // but entries can't fire that early anyway.
        && (cfg.MinBarFlush = 0.0
            || gate (sLastBar |> ValueOption.map (fun p -> p.close)) (fun pc ->
                   pc > 0.0 &&
                   let mv = bar.close / pc - 1.0
                   if cfg.Downside then mv <= cfg.MinBarFlush else mv >= -cfg.MinBarFlush))
        // ENTRY-BAR FLUSH-DEPTH floor (cfg.MinBarFlushFloor, 0 = off): reject a flush DEEPER than
        // the floor (the Run 26 falling-knife cut). Long/Downside: mv >= floor (e.g. >= -0.12).
        // Upside/short: mirror (mv <= -floor). Together with MinBarFlush this bands the entry move.
        && (cfg.MinBarFlushFloor = 0.0
            || gate (sLastBar |> ValueOption.map (fun p -> p.close)) (fun pc ->
                   pc > 0.0 &&
                   let mv = bar.close / pc - 1.0
                   if cfg.Downside then mv >= cfg.MinBarFlushFloor else mv <= -cfg.MinBarFlushFloor))
        // intraday tightness gate
        && gate this.Tightness (fun t -> t < cfg.MaxTightness)
        // intraday ATR% gate (log-space)
        && gate this.AtrPct (fun a -> a < cfg.MaxAtrPct)
        // concurrency room (count only currently-OPEN positions)
        && (cfg.MaxConcurrent <= 0 || this.OpenCount < cfg.MaxConcurrent)

    /// Fold the current bar into the intraday indicators. Snapshots taken BEFORE
    /// the push (prior-bars / no-lookahead), then the bar is folded in.
    member this.ProcessBar (bar: MinuteBar) =
        // `lastBar` still holds the immediately-prior bar here (it's updated at the
        // very end), so it's the source of both the prior low and the prior close.
        // 1) snapshot pre-bar state
        sRunHi        <- runHi
        sRunLo        <- runLo
        sRunHiClose   <- runHiClose
        sRunLoClose   <- runLoClose
        sRunVolHi     <- runVolHi
        sLastBar      <- lastBar
        sAtrLog       <- atrLog.State
        sAtrLin       <- atrLin.State
        sRangeHigh    <- rangeHigh.State
        sRangeLow     <- rangeLow.State
        sTargetAnchor <- this.LiveTargetAnchor
        // VWAP-reclaim: snapshot the PRIOR EMA & VWAP (strictly-prior — for cross detection),
        // and the below-VWAP fraction accumulated over strictly-prior bars.
        sEmaPrev  <- ema.State
        sVwapPrev <- this.LiveVwap
        sBelowVwapFrac <- if emaVwapBars > 0 then ValueSome (float belowVwapBars / float emaVwapBars) else ValueNone
        sRunBelowVwap  <- runBelowVwap
        sAboveVwapFrac <- if emaVwapBars > 0 then ValueSome (float aboveVwapBars / float emaVwapBars) else ValueNone
        sRunAboveVwap  <- runAboveVwap
        // DipRider: bars-since-high / bars-since-vol-high (recency), pullback depth, prior EMA — all
        // strictly-prior (barsSeen has not yet counted this bar; hi*BarIdx point at prior updates).
        sBarsSinceHi    <- if hiBarIdx    >= 0 then barsSeen - hiBarIdx    else -1
        sBarsSinceVolHi <- if volHiBarIdx >= 0 then barsSeen - volHiBarIdx else -1
        sBarsBelowEma   <- barsBelowEma
        sEmaPrevDip     <- ema.State

        // 2) fold the bar in. True range vs the prior 1m close.
        let mutable barLogTr = nan               // this bar's log true range (for the run-ATR accumulator below)
        match lastBar with
        | ValueSome prev when bar.high > 0.0 && bar.low > 0.0 && prev.close > 0.0 ->
            let pc = prev.close
            (max bar.high pc - min bar.low pc) |> atrLin.Push
            barLogTr <- log (max bar.high pc / min bar.low pc)
            barLogTr |> atrLog.Push
        | _ -> ()

        rangeHigh.Push bar.high
        rangeLow.Push  bar.low
        // DipRider recency: record the bar index whenever a NEW session high / vol-high is made (>=, so
        // the FIRST bar and ties re-stamp — the most recent touch of the extreme). barsSeen is this bar's
        // own 0-based index (it's incremented at the very end of ProcessBar).
        (match runHi with ValueSome h when bar.high <= h -> () | _ -> hiBarIdx <- barsSeen)
        (match runVolHi with ValueSome v when bar.volume <= v -> () | _ -> volHiBarIdx <- barsSeen)
        runHi    <- match runHi with ValueSome h -> ValueSome (max h bar.high) | ValueNone -> ValueSome bar.high
        runLo    <- match runLo with ValueSome l -> ValueSome (min l bar.low)  | ValueNone -> ValueSome bar.low
        runHiClose <- match runHiClose with ValueSome h -> ValueSome (max h bar.close) | ValueNone -> ValueSome bar.close
        runLoClose <- match runLoClose with ValueSome l -> ValueSome (min l bar.close) | ValueNone -> ValueSome bar.close
        runVolHi <- match runVolHi with ValueSome v -> ValueSome (max v bar.volume) | ValueNone -> ValueSome bar.volume
        cumVol   <- cumVol + bar.volume
        lag20.Push bar.close      // 20-bar delay line; read post-fold at entry = the 20m %-change into it
        vol20.Push (float bar.volume)  // trailing 20-bar mean 1m volume; read post-fold at entry (incl. the cross bar)
        if sessionOpen.IsNone then sessionOpen <- ValueSome bar.``open``

        // VWAP-reclaim: tally the strictly-prior below-VWAP state (prior EMA vs prior VWAP,
        // both snapshotted above BEFORE this bar folds in), THEN fold this bar's VWAP + EMA.
        if cfg.VwapReclaim then
            match sEmaPrev, sVwapPrev with
            | ValueSome e, ValueSome v ->
                emaVwapBars <- emaVwapBars + 1
                // the ACTIVE run's side: long tracks the below-run (VWAP-EMA), short the above-run (EMA-VWAP).
                let onRun = if cfg.ReclaimShort then e > v else e < v
                if onRun then
                    let dist = if v > 0.0 then abs (v - e) / v else 0.0    // fractional VWAP<->EMA gap this bar
                    if dist > runMaxDist then runMaxDist <- dist          // deepen the run's max distance
                    if not (Double.IsNaN barLogTr) then                  // accumulate the run's ATR (log-TR mean)
                        runAtrSum <- runAtrSum + barLogTr
                        runAtrN   <- runAtrN + 1
                // Up/Down volume split over the WHOLE cross-to-cross regime (NOT gated by onRun — bars
                // oscillate around the declining/converging EMA within the run). CLOSE vs the strictly-prior
                // 9-EMA `e`: above -> Up (rising/accumulation), below -> Down (falling/distribution).
                if bar.close > e then
                    runUpVolSum <- runUpVolSum + float bar.volume
                    runUpVolN   <- runUpVolN + 1
                elif bar.close < e then
                    runDnVolSum <- runDnVolSum + float bar.volume
                    runDnVolN   <- runDnVolN + 1
                let resetRun () =
                    runMaxDist <- 0.0; runAtrSum <- 0.0; runAtrN <- 0
                    runUpVolSum <- 0.0; runUpVolN <- 0; runDnVolSum <- 0.0; runDnVolN <- 0
                if e < v then
                    belowVwapBars <- belowVwapBars + 1
                    runBelowVwap  <- runBelowVwap + 1     // extend the consecutive-below streak
                    runAboveVwap  <- 0                    // EMA below VWAP -> above-streak breaks
                    if cfg.ReclaimShort then resetRun ()  // the short's above-run just broke
                elif e > v then
                    aboveVwapBars <- aboveVwapBars + 1
                    runAboveVwap  <- runAboveVwap + 1     // extend the consecutive-above streak (short mirror)
                    runBelowVwap  <- 0                    // EMA reclaimed/above VWAP -> below-streak breaks
                    if not cfg.ReclaimShort then resetRun ()  // the long's below-run just broke
                else
                    runBelowVwap  <- 0                    // EMA exactly at VWAP -> both streaks break
                    runAboveVwap  <- 0
                    resetRun ()                           // either way the run broke
            | _ -> ()

        // session VWAP always accumulates for this engine (independent of cfg.Target), so the
        // reclaim can read it; cfg.Target still selects a separate MR cover anchor if set.
        let tp = (bar.high + bar.low + bar.close) / 3.0
        vwapNum <- vwapNum + tp * float bar.volume
        vwapDen <- vwapDen + float bar.volume
        // DipRider: extend/reset the consecutive-bars-closed-below-9EMA counter (the pullback depth),
        // measured vs the STRICTLY-PRIOR EMA (sEmaPrevDip, snapshotted above). A close below the prior
        // EMA extends the pullback; a close at/above it ends the pullback (the re-break resets it to 0).
        if cfg.DipRider then
            match sEmaPrevDip with
            | ValueSome e -> if bar.close < e then barsBelowEma <- barsBelowEma + 1 else barsBelowEma <- 0
            | ValueNone -> ()
        if cfg.VwapReclaim || cfg.DipRider then ema.Push bar.close

        // fold the chosen mean-reversion COVER anchor (only the active one accumulates).
        match cfg.Target with
        | NoTarget | Vwap -> ()      // Vwap handled by the always-on accumulator above
        | Ma _ -> maTgt.Push bar.close
        | Channel _ -> chanTgt.Push bar.low

        lastBar   <- ValueSome bar
        barsSeen  <- barsSeen + 1

    /// Advance one open position by the current bar, returning the (possibly
    /// state-changed) position — an immutable update, mirroring HighFlyer's
    /// `Update`. Exit precedence (first to fire wins):
    ///   1. protective stop (if armed) — the 2-bar local low at entry; fires when
    ///      bar.low reaches it, fills at the bar's CLOSE (filling exactly at the stop
    ///      price would assume we caught the precise low — too optimistic; the close
    ///      is still optimistic but far more defensible).
    ///   2. mean-reversion TARGET (short, if armed) — cover when the bar's low reaches
    ///      the snapshotted (strictly-prior) anchor; fills at the anchor as a resting
    ///      limit, but no better than the bar OPEN if price gapped through it. Doubles
    ///      as the loss-cut (price that never reverts rides to the time-stop / MOC).
    ///   3. time-stop (if armed) — `TimeStopMin` minutes after entry, fills at close.
    ///   4. MOC — at/after the cutoff, flatten at close.
    /// An already-exited position is returned unchanged.
    /// VWAP-reclaim exit (long-only): fixed stop/target levels snapshotted at entry.
    /// Precedence: protective stop → profit target → time-stop → MOC.
    ///   stop   — CLOSE-based (StopOnClose, default): the bar must CLOSE at/below StopLevel (ignores
    ///            random low wicks that recover); fills at that close. Wick mode (StopOnClose=false):
    ///            bar.low <= StopLevel, fills at the level (gap-through at the open).
    ///   target — bar.high >= TargetLevel; a resting SELL limit, fills at the level, gap-up fills
    ///            better at the open (max). If both the stop and target trigger in the same bar
    ///            we CONSERVATIVELY take the STOP (can't know intrabar order; assume the adverse fill).
    member private _.AdvanceReclaim (bar: MinuteBar) (pos: IntradayPosition) : IntradayPosition =
        match pos.State with
        | ExitedAt _ | Skipped | Armed _ -> pos      // reclaim positions are Holding immediately; no Armed
        | Holding ->
            // long: stop BELOW (close/low <= level), target ABOVE (high >= level).
            // short (ReclaimShort): fully mirrored — stop ABOVE (close/high >= level), target BELOW (low <= level).
            let stopHit =
                if cfg.ReclaimShort then (if cfg.StopOnClose then bar.close >= pos.StopLevel else bar.high >= pos.StopLevel)
                else (if cfg.StopOnClose then bar.close <= pos.StopLevel else bar.low <= pos.StopLevel)
            let targetHit =
                cfg.UseTarget && (if cfg.ReclaimShort then bar.low <= pos.TargetLevel else bar.high >= pos.TargetLevel)
            if stopHit then
                // close-based: fill at the close (the bar closed through the stop). wick mode: fill at the
                // level, but no better than the open if the bar gapped clean through it (worse side per direction).
                let fill =
                    if cfg.StopOnClose then bar.close
                    elif cfg.ReclaimShort then max pos.StopLevel bar.``open``
                    else min pos.StopLevel bar.``open``
                { pos with State = ExitedAt (bar.etMin, fill, "stop") }
            elif targetHit then
                let fill =
                    if cfg.ReclaimShort then min pos.TargetLevel bar.``open`` // gap-down through fills at the open
                    else max pos.TargetLevel bar.``open`` // gap-up through the target fills at the open
                { pos with State = ExitedAt (bar.etMin, fill, "target") }
            elif cfg.TimeStopMin > 0 && bar.etMin >= pos.EntryMin + cfg.TimeStopMin then
                { pos with State = ExitedAt (bar.etMin, bar.close, "time_stop") }
            elif bar.etMin >= cfg.MocMin then
                { pos with State = ExitedAt (bar.etMin, bar.close, "moc") }
            else pos

    /// DipRider exit (long-only, v0). Precedence: protective stop (StopLevel, close-based when
    /// StopOnClose) → optional %-catastrophe stop → time-stop → MOC. The stop is a structural level
    /// snapshotted at entry (the re-break bar's low, or a %-below-entry floor); NoTarget by default
    /// (momentum-continuation, run to MOC — same lesson as the reclaim). StopLevel = -inf disables it.
    member private _.AdvanceDip (bar: MinuteBar) (pos: IntradayPosition) : IntradayPosition =
        match pos.State with
        | ExitedAt _ | Skipped | Armed _ -> pos      // DipRider positions are Holding immediately
        | Holding ->
            let stopHit =
                pos.StopLevel > Double.NegativeInfinity
                && (if cfg.StopOnClose then bar.close <= pos.StopLevel else bar.low <= pos.StopLevel)
            let pctStopLevel = pos.EntryPx * (1.0 - cfg.PctStop)
            let pctStopHit = cfg.PctStop > 0.0 && bar.low <= pctStopLevel
            // EXIT-TO-NEW-HIGH: a bar's high exceeds the session high that stood at entry (TargetLevel) →
            // the resumption made a fresh high; book it as a resting SELL limit at that level (gap-up fills
            // better at the open). Guarded by a valid (non-nan) target.
            let newHighHit =
                cfg.DipExitNewHigh && not (Double.IsNaN pos.TargetLevel) && bar.high > pos.TargetLevel
            if stopHit then
                let fill = if cfg.StopOnClose then bar.close else min pos.StopLevel bar.``open``
                { pos with State = ExitedAt (bar.etMin, fill, "stop") }
            elif pctStopHit then
                { pos with State = ExitedAt (bar.etMin, min pctStopLevel bar.``open``, "pct_stop") }
            elif newHighHit then
                // fill at the target (the prior session high +ε ≈ the level); gap-up fills better at the open.
                { pos with State = ExitedAt (bar.etMin, max pos.TargetLevel bar.``open``, "new_high") }
            elif cfg.TimeStopMin > 0 && bar.etMin >= pos.EntryMin + cfg.TimeStopMin then
                { pos with State = ExitedAt (bar.etMin, bar.close, "time_stop") }
            elif bar.etMin >= cfg.MocMin then
                { pos with State = ExitedAt (bar.etMin, bar.close, "moc") }
            else pos

    member this.Advance (bar: MinuteBar) (pos: IntradayPosition) : IntradayPosition =
        if cfg.DipRider then this.AdvanceDip bar pos
        elif cfg.VwapReclaim then this.AdvanceReclaim bar pos else
        // the 2-bar local extreme AT this bar (min/max of this bar + the prior bar).
        // sLastBar is the strictly-prior bar (ValueNone only on the 08:30 open bar, well
        // before any entry can fire). For a SHORT (upside breakout) the trail is a 2-bar
        // LOW; for a LONG (downside breakout) a 2-bar HIGH.
        let twoBarExtreme =
            match sLastBar with
            | ValueSome prev -> if cfg.Downside then max prev.high bar.high else min prev.low bar.low
            | ValueNone      -> if cfg.Downside then bar.high else bar.low
        match pos.State with
        | ExitedAt _ | Skipped -> pos
        | Armed (armMin, trail, armPrice, riseCleared) ->
            // ratchet the trailing 2-bar extreme WITH the move (a short's trail only rises;
            // a long's only falls).
            let trail' = if cfg.Downside then min trail twoBarExtreme else max trail twoBarExtreme
            // session extreme at fill — the --trail-entry protective stop reference.
            let sessionStop () =
                match (if cfg.Downside then sRunLo else sRunHi) with
                | ValueSome x -> x
                | ValueNone   -> if cfg.Downside then bar.low else bar.high
            if cfg.ExtGate > 0.0 then
                // --ext-gate (<ExtGate-at-breakout) branch: this arm was created because
                // day-extension was BELOW ExtGate at the breakout. Two confirmations race:
                //   (1) if RiseEntry>0 and price runs +RiseEntry past the breakout price FIRST
                //       → take it (short into the parabolic move at the rise level).
                //   (2) else if a ROLLOVER fires (close back through the trail) → take it ONLY
                //       if day-extension has reached ExtGate by then; otherwise SKIP (no trade).
                let riseLevel = if cfg.Downside then armPrice * (1.0 - cfg.RiseEntry) else armPrice * (1.0 + cfg.RiseEntry)
                let riseHit = cfg.RiseEntry > 0.0 && (if cfg.Downside then bar.low <= riseLevel else bar.high >= riseLevel)
                let rolled  = if cfg.Downside then bar.close > trail' else bar.close < trail'
                let extOk   = (if cfg.Downside then -(extension bar.close) else extension bar.close) >= cfg.ExtGate
                if riseHit then
                    let fill = if cfg.Downside then min riseLevel bar.``open`` else max riseLevel bar.``open``
                    { pos with EntryMin = bar.etMin; EntryPx = fill; StopLevel = sessionStop (); State = Holding }
                elif rolled && extOk then
                    { pos with EntryMin = bar.etMin; EntryPx = bar.close; StopLevel = sessionStop (); State = Holding }
                elif rolled then
                    // rolled over but not extended enough → no trade. Terminal Skipped state
                    // (never re-triggers, never becomes a trip).
                    { pos with State = Skipped }
                elif bar.etMin >= cfg.MocMin then pos   // neither fired by MOC → dropped (no trip)
                else { pos with State = Armed (armMin, trail', armPrice, riseCleared) }
            else
            // --rise-entry GATE: require a further RiseEntry move past the arm price first
            // (short above armPrice*(1+x), long below armPrice*(1-x)). Once a bar's extreme
            // reaches that level the gate is cleared (latched). With RiseEntry=0 it's open.
            let riseLevel = if cfg.Downside then armPrice * (1.0 - cfg.RiseEntry) else armPrice * (1.0 + cfg.RiseEntry)
            let riseCleared' =
                riseCleared
                || cfg.RiseEntry <= 0.0
                || (if cfg.Downside then bar.low <= riseLevel else bar.high >= riseLevel)
            if not riseCleared' then
                // rise gate still pending → can't enter this bar; just ratchet & wait.
                if bar.etMin >= cfg.MocMin then pos
                else { pos with State = Armed (armMin, trail', armPrice, false) }
            elif cfg.TrailEntry then
                // wait for the rollover: enter on the first close back through the trail.
                let triggered = if cfg.Downside then bar.close > trail' else bar.close < trail'
                if triggered then
                    { pos with EntryMin = bar.etMin; EntryPx = bar.close; StopLevel = sessionStop (); State = Holding }
                elif bar.etMin >= cfg.MocMin then pos
                else { pos with State = Armed (armMin, trail', armPrice, true) }
            else
                // immediate fill the moment the rise gate clears: short INTO the parabolic
                // move at the rise level (no better than the bar OPEN if it gapped through).
                // (Reached only with RiseEntry>0 & not trail — RiseEntry=0 direct entry never arms.)
                let fill = if cfg.Downside then min riseLevel bar.``open`` else max riseLevel bar.``open``
                { pos with EntryMin = bar.etMin; EntryPx = fill; StopLevel = sessionStop (); State = Holding }
        | Holding ->
            // a short cover-target: bar.low reaching the strictly-prior anchor.
            let targetHit =
                cfg.Short &&
                match sTargetAnchor with ValueSome a -> bar.low <= a | ValueNone -> false
            // protective stop: directional, fires when price reaches pos.StopLevel. Opt-in
            // via UseStop in BOTH entry models (so --trail-entry alone = backside entry,
            // stopless, hold-to-MOC; add --intraday-stop for the session stop).
            //   direct entry  — the 2-bar local extreme at the breakout (short stops on
            //                   bar.low <= level; long on bar.high >= level).
            //   --trail-entry / --rise-entry — the SESSION extreme at fill (short stops on
            //                   bar.high >= the session high → ran back over the top; long on
            //                   bar.low <= the session low).
            // Both Armed paths (trail, rise) set StopLevel to the session extreme, so the
            // session-stop direction applies whenever either is on.
            let sessionStopMode = cfg.TrailEntry || cfg.RiseEntry > 0.0
            let stopHit =
                if sessionStopMode then
                    if cfg.Downside then bar.low <= pos.StopLevel else bar.high >= pos.StopLevel
                elif cfg.Downside then bar.high >= pos.StopLevel
                else bar.low <= pos.StopLevel
            // wide %-stop: a fixed adverse excursion from the entry (e.g. 0.5 = 50% against
            // us). Independent of UseStop — a catastrophe stop meant to clip the run-over
            // tail while leaving normal noise untouched. Short stops if price rises PctStop
            // above entry; long if it falls PctStop below. Fills at the level, but no better
            // than the bar OPEN if the bar gapped clean through it.
            let pctStopLevel =
                if cfg.Short then pos.EntryPx * (1.0 + cfg.PctStop)
                else pos.EntryPx * (1.0 - cfg.PctStop)
            let pctStopHit =
                cfg.PctStop > 0.0 &&
                (if cfg.Short then bar.high >= pctStopLevel else bar.low <= pctStopLevel)
            if cfg.UseStop && stopHit then
                let reason = if cfg.TrailEntry then "session_stop" else "intraday_stop"
                { pos with State = ExitedAt (bar.etMin, bar.close, reason) }
            elif pctStopHit then
                let fill = if cfg.Short then max pctStopLevel bar.``open`` else min pctStopLevel bar.``open``
                { pos with State = ExitedAt (bar.etMin, fill, "pct_stop") }
            elif targetHit then
                let anchor = sTargetAnchor.Value
                let fill = min anchor bar.``open``   // limit fill at anchor; gap-through fills at the open
                { pos with State = ExitedAt (bar.etMin, fill, "target") }
            elif cfg.TimeStopMin > 0 && bar.etMin >= pos.EntryMin + cfg.TimeStopMin then
                { pos with State = ExitedAt (bar.etMin, bar.close, "time_stop") }
            elif bar.etMin >= cfg.MocMin then
                { pos with State = ExitedAt (bar.etMin, bar.close, "moc") }
            else pos

    /// Advance the whole system by one minute bar: fold the bar in, advance every
    /// open position, then (if Gate 3 fires) open a new independent position.
    /// Ordering mirrors the daily engine — the new position is appended AFTER the
    /// existing positions advance, so an entry bar is never its own first exit-check.
    member this.Process (bar: MinuteBar) =
        this.ProcessBar bar
        for i in 0 .. positions.Count - 1 do
            positions.[i] <- this.Advance bar positions.[i]
        if cfg.DipRider then
            if this.ShouldEnterDip bar then
                // fill at the re-break bar's close. StopLevel = the re-break bar's LOW (a tight structural
                // stop under the resumption bar); -inf if that would be non-positive (stopless fallback).
                let stop = if bar.low > 0.0 then bar.low else Double.NegativeInfinity
                let stopDistPct = if bar.close > 0.0 && stop > Double.NegativeInfinity then (bar.close - stop) / bar.close else nan
                positions.Add
                    { EntryMin = bar.etMin
                      EntryPx = bar.close
                      StopLevel = stop
                      BreakoutRef = (match sLastBar with ValueSome p -> p.high | ValueNone -> nan)  // the prior high re-broken
                      // TargetLevel = the session HIGH standing at entry (post-fold incl. this re-break bar).
                      // The DipExitNewHigh target: exit when a later bar's high EXCEEDS this level (a fresh
                      // session high = the resumption ran). nan-guarded → no target if no high yet.
                      TargetLevel = (match runHi with ValueSome h -> h | ValueNone -> nan)
                      RunBelowVwapAtEntry = 0
                      StopDistPct = stopDistPct
                      AtrPctAtEntry = (match this.AtrPct with ValueSome a -> a | ValueNone -> nan)
                      TightnessAtEntry = (match this.Tightness with ValueSome t -> t | ValueNone -> nan)
                      CumVolAtEntry = cumVol
                      BreakoutBarVol = bar.volume
                      NewVolHigh = (match sRunVolHi with ValueSome vh -> bar.volume > vh | ValueNone -> true)
                      VolVsHigh = (match sRunVolHi with ValueSome vh when vh > 0L -> float bar.volume / float vh | _ -> nan)
                      BreakoutBarOpen = bar.``open``
                      PrevBarClose = (match sLastBar with ValueSome p -> p.close | ValueNone -> nan)
                      Chg20mAtEntry = (match lagPctChange lag20 with ValueSome p -> p | ValueNone -> nan)
                      Vol20mAvgAtEntry = (if vol20.Count >= 20 then (match vol20.State with ValueSome v -> v | ValueNone -> nan) else nan)
                      RunMaxDistAtEntry = nan
                      RunAtrAtEntry = nan
                      RunUpVolAtEntry = nan
                      RunDnVolAtEntry = nan
                      BarsSinceHiAtEntry = sBarsSinceHi
                      BarsSinceVolHiAtEntry = sBarsSinceVolHi
                      BarsBelowEmaAtEntry = sBarsBelowEma
                      TrendPctAtEntry = (match sessionOpen with ValueSome o when o > 0.0 -> bar.close / o - 1.0 | _ -> nan)
                      State = Holding }
        elif cfg.VwapReclaim then
            if this.ShouldEnterReclaim bar then
                // stop/target geometry off the LIVE VWAP + running session low at the cross bar.
                // d = VWAP - sessionLow (the VWAP-to-low distance). target = VWAP + d.
                // stop = VWAP - d*StopDistFrac (StopAnchorVwap, default) OR entry - d*StopDistFrac.
                // StopDistFrac scales the stop distance: 1/3 default (tight), larger = WIDER stop.
                let vwap = this.LiveVwap.Value          // ShouldEnterReclaim required the cross => LiveVwap is ValueSome
                // d = VWAP-to-extreme distance. Long: VWAP - sessionLow, target ABOVE, stop BELOW.
                // Short (ReclaimShort): sessionHigh - VWAP, target BELOW, stop ABOVE (fully mirrored).
                let sExt =
                    if cfg.ReclaimShort then (match runHi with ValueSome h -> h | ValueNone -> bar.high)
                    else (match runLo with ValueSome l -> l | ValueNone -> bar.low)
                let d = if cfg.ReclaimShort then sExt - vwap else vwap - sExt
                let target = if cfg.ReclaimShort then vwap - d else vwap + d
                let stopDist = d * cfg.StopDistFrac
                // stop level: a FIXED % adverse (FixedPctStop>0) OR the d-geometry (VWAP- or entry-anchored).
                // Short flips the sign: stop ABOVE entry/VWAP.
                let rawStop =
                    if cfg.FixedPctStop > 0.0 then
                        if cfg.ReclaimShort then bar.close * (1.0 + cfg.FixedPctStop) else bar.close * (1.0 - cfg.FixedPctStop)
                    elif cfg.StopAnchorVwap then
                        if cfg.ReclaimShort then vwap + stopDist else vwap - stopDist
                    else
                        if cfg.ReclaimShort then bar.close + stopDist else bar.close - stopDist
                // stop DISTANCE as a positive fraction of entry (adverse excursion), sign-agnostic.
                let rawStopDistPct = if bar.close > 0.0 then abs (rawStop - bar.close) / bar.close else nan
                // MIN stop-distance handling (Finding 7 / 15). Two modes when the geometric stop is
                // TIGHTER than MinStopDistPct:
                //   CLAMP (ClampStopDist=true): keep the trade, but WIDEN the stop so its distance = the
                //     minimum (place the stop at exactly MinStopDistPct below entry).
                //   SKIP (false, original): reject the trade entirely.
                let tooTight = cfg.MinStopDistPct > 0.0 && rawStopDistPct < cfg.MinStopDistPct
                let stop =
                    if tooTight && cfg.ClampStopDist then
                        if cfg.ReclaimShort then bar.close * (1.0 + cfg.MinStopDistPct) else bar.close * (1.0 - cfg.MinStopDistPct)
                    else rawStop
                let stopDistPct = if bar.close > 0.0 then abs (stop - bar.close) / bar.close else nan
                let admit = (not tooTight) || cfg.ClampStopDist
                if admit then
                 positions.Add
                    { EntryMin = bar.etMin
                      EntryPx = bar.close
                      StopLevel = stop
                      BreakoutRef = vwap
                      TargetLevel = target
                      // record the TREND run into the cross (below-run for long, above-run for short) so the
                      // rb-band gate/breakdown reads the right counter for either direction.
                      RunBelowVwapAtEntry = (if cfg.ReclaimShort then sRunAboveVwap else sRunBelowVwap)
                      StopDistPct = stopDistPct
                      AtrPctAtEntry = (match this.AtrPct with ValueSome a -> a | ValueNone -> nan)
                      TightnessAtEntry = (match this.Tightness with ValueSome t -> t | ValueNone -> nan)
                      CumVolAtEntry = cumVol
                      BreakoutBarVol = bar.volume
                      NewVolHigh = false
                      VolVsHigh = (match sBelowVwapFrac with ValueSome f -> f | ValueNone -> nan)  // reuse col: below-VWAP frac
                      BreakoutBarOpen = bar.``open``
                      PrevBarClose = (match sLastBar with ValueSome p -> p.close | ValueNone -> nan)
                      Chg20mAtEntry = (match lagPctChange lag20 with ValueSome p -> p | ValueNone -> nan)
                      Vol20mAvgAtEntry = (if vol20.Count >= 20 then (match vol20.State with ValueSome v -> v | ValueNone -> nan) else nan)
                      RunMaxDistAtEntry = runMaxDist
                      RunAtrAtEntry = (if runAtrN > 0 then runAtrSum / float runAtrN else nan)
                      RunUpVolAtEntry = (if runUpVolN > 0 then runUpVolSum / float runUpVolN else nan)
                      RunDnVolAtEntry = (if runDnVolN > 0 then runDnVolSum / float runDnVolN else nan)
                      BarsSinceHiAtEntry = -1
                      BarsSinceVolHiAtEntry = -1
                      BarsBelowEmaAtEntry = 0
                      TrendPctAtEntry = nan
                      State = Holding }
        elif this.ShouldEnter bar then
            // ShouldEnter passed => sRunHi / this.Tightness / this.AtrPct are all
            // ValueSome (each is gated). sLastBar is ValueSome too: it's ValueNone only
            // on the 08:30 session-open bar, but entries can't fire before 09:35
            // (EntryStartMin), by which point a prior bar always exists. The .Value
            // reads are therefore safe — no sentinel needed.
            // StopLevel = the 2-bar local extreme at entry. Upside: min of the breakout
            // bar's low and the prior bar's low (a tight stop just under the breakout base).
            // Downside: max of the two highs (just above the breakdown base). Self-
            // calibrating; replaces the wide 08:30-session extreme.
            // the 2-bar local extreme at the breakout bar (upside = 2-bar low, downside =
            // 2-bar high): the direct-entry protective stop, AND the initial trail level
            // for --trail-entry.
            let twoBar =
                if cfg.Downside then max sLastBar.Value.high bar.high
                else min sLastBar.Value.low bar.low
            positions.Add
                { EntryMin = bar.etMin
                  EntryPx = bar.close
                  StopLevel = twoBar
                  BreakoutRef = this.BreakoutRef.Value
                  TargetLevel = nan
                  RunBelowVwapAtEntry = 0
                  StopDistPct = nan
                  AtrPctAtEntry = this.AtrPct.Value
                  TightnessAtEntry = this.Tightness.Value
                  // cumVol was folded by ProcessBar (called before this append), so it
                  // already includes THIS breakout bar → cumulative volume through entry.
                  CumVolAtEntry = cumVol
                  BreakoutBarVol = bar.volume
                  // sRunVolHi is the pre-push snapshot (excludes this bar), so this is a true
                  // "new session vol high" test. ValueNone (first bar) counts as a new high.
                  NewVolHigh = (match sRunVolHi with ValueSome vh -> bar.volume > vh | ValueNone -> true)
                  VolVsHigh = (match sRunVolHi with ValueSome vh when vh > 0L -> float bar.volume / float vh | _ -> nan)
                  BreakoutBarOpen = bar.``open``
                  PrevBarClose = sLastBar.Value.close
                  Chg20mAtEntry = (match lagPctChange lag20 with ValueSome p -> p | ValueNone -> nan)
                  Vol20mAvgAtEntry = (if vol20.Count >= 20 then (match vol20.State with ValueSome v -> v | ValueNone -> nan) else nan)
                  RunMaxDistAtEntry = runMaxDist
                  RunAtrAtEntry = (if runAtrN > 0 then runAtrSum / float runAtrN else nan)
                  RunUpVolAtEntry = (if runUpVolN > 0 then runUpVolSum / float runUpVolN else nan)
                  RunDnVolAtEntry = (if runDnVolN > 0 then runDnVolSum / float runDnVolN else nan)
                  BarsSinceHiAtEntry = -1
                  BarsSinceVolHiAtEntry = -1
                  BarsBelowEmaAtEntry = 0
                  TrendPctAtEntry = nan
                  // entry routing:
                  //   --ext-gate — if day-extension already >= ExtGate at the breakout, enter
                  //                DIRECT (parabolic now); else arm a ROLLOVER gated on reaching
                  //                ExtGate by fill time.
                  //   --trail-entry / --rise-entry — arm (wait for the rollover and/or rise gate).
                  //   otherwise — Holding now (fill at the breakout close).
                  State =
                    if cfg.ExtGate > 0.0 then
                        if extension bar.close >= cfg.ExtGate then Holding
                        else Armed (bar.etMin, twoBar, bar.close, false)
                    elif cfg.TrailEntry || cfg.RiseEntry > 0.0 then Armed (bar.etMin, twoBar, bar.close, false)
                    else Holding }

    /// Flatten any still-open positions at the last folded bar's close (MOC /
    /// hold-to-close). The last bar is held in state (set by ProcessBar), so no
    /// argument is needed. A no-op if no bar was ever processed. (Named `Flatten`,
    /// not `Finalize`, to avoid colliding with Object.Finalize, the GC finalizer.)
    member _.Flatten () =
        match lastBar with
        | ValueNone -> ()
        | ValueSome lb ->
            for i in 0 .. positions.Count - 1 do
                match positions.[i].State with
                | Holding -> positions.[i] <- { positions.[i] with State = ExitedAt (lb.etMin, lb.close, "moc") }
                | Armed _ | Skipped | ExitedAt _ -> ()   // never-filled / skipped → no trip (dropped below)

    /// All positions for this (ticker, day), in entry order.
    member _.Positions = positions
