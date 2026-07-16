module TradingEdge.MaxFlyerV3.Intraday

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
      EmaStopBase: float       // MAX-EMA STOP base, FROZEN at entry: the session-max 9-EMA as-of the ENTRY bar.
                               // The stop level = EmaStopBase × (1 + EmaMaxStopBuffer); cover when the live 9-EMA
                               // CLOSES above it. Frozen (not trailing) so the buffer is meaningfully spaced — a
                               // trailing base rises with the EMA and the buffer saturates. nan when EmaMaxStop off.
      EmaAtEntry: float        // the live 9-EMA on the ENTRY (fill) bar. The 9-EMA %-STOP anchor: level =
                               // EmaAtEntry × (1 + EmaPctStop). nan when EmaPctStop off.
      StopArmPending: bool     // ShortHighEntry only: this leg entered at the HIGH with a DORMANT ema-max stop.
                               // While true the stop is off; on the first 9-EMA down-tick the stop arms (EmaStopBase
                               // set from the roll30-max at that bar) and this flips false. false for normal legs.
      ArmMin: int              // the ET minute the ema-max stop ARMED (the first 9-EMA down-tick bar) for a
                               // ShortHighEntry leg-0. -1 if never armed / not a short-high leg. Records the
                               // right-side-of-the-V point for the entry→arm displacement analysis.
      ArmClose: float          // the CLOSE of the down-tick (arm) bar. nan if never armed. Displacement from entry
                               // = ArmClose / EntryPx − 1 (short: >0 = underwater at the arm; <0 = already in profit).
      CloseStopBase: float     // MAX-CLOSE STOP base, FROZEN at entry: the rolling-N-bar max raw close as-of the
                               // ENTRY bar. Stop level = CloseStopBase × (1 + MaxCloseStopBuffer); cover when the
                               // live bar close rises above it. nan when MaxCloseStop off.
      StopLevel: float         // protective-stop level. Direct entry: the 2-bar local extreme at the
                               // breakout (upside = 2-bar low, downside = 2-bar high); fires when price
                               // reaches it (low<=level / high>=level). Under --trail-entry this is the
                               // SESSION extreme at fill instead (the high for a short, the low for a long):
                               // we're stopped out if price runs back to a fresh session extreme.
      BreakoutRef: float       // the running session extreme (strictly-prior bars) the breakout cleared —
                               // the session high for an upside breakout, the session low for a downside one
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
      // ----- SIGNAL (arm) bar record — for the EMA-entry model, the breakout HIGH is the trade signal but the
      //       FILL is deferred to the later 9-EMA cross-under. These capture the signal bar so post-hoc we can
      //       measure how much the entry differs from the signal. For direct entries signal == entry bar. -----
      SignalMin: int           // the arm (signal/breakout) bar's ET minute. Entry lag = EntryMin - SignalMin.
      SignalHigh: float        // the breakout bar's HIGH — the trade SIGNAL price (the pop we commit to fade).
      SignalOpen: float        // the signal bar's OPEN.
      SignalLow: float         // the signal bar's LOW.
      SignalClose: float       // the signal bar's CLOSE (vs EntryPx = how far the fade moved before we filled).
      SignalVolume: int64      // the signal (arm/breakout) bar's own VOLUME.
      SessVolHighAtSignal: int64  // the STRICTLY-PRIOR session 1m-vol high at the signal bar (vol-confirm post-hoc).
      Reentries: int           // RE-ENTRIES this position still permits after a stop-out (down-tick + --ema-reentries).
                               // On an ema-max-stop with Reentries>0, Advance spawns a fresh re-arm pending carrying
                               // Reentries-1, which re-shorts on the next down-tick. 0 = no re-entry (chain ends).
      ReIdx: int               // RE-ENTRY leg index (0 = original, 1 = 1st re-entry, …) — recorded for a POST-HOC cap.
      State: IntraPosState }   // immutable — advancing a position returns a NEW record (HighFlyer style)

/// One PENDING signal in the EMA-entry model: a new-session-high breakout that has NOT yet been faded. It fires
/// (becomes a Holding position) when its 9-EMA closes below EmaAtArm within its Bars countdown, else it expires.
/// Each new high arms an independent PendingSignal; multiple coexist. Carries the SIGNAL (arm) bar's features so
/// the resulting trip describes the pop being faded, not the later cross-under fill bar. `Bars` is mutable (aged).
type PendingSignal =
    { EmaAtArm: float
      mutable Bars: int
      // the bar this pending was (re)armed on — the fire-loop skip guard ("just armed this bar → don't fire yet").
      // Kept SEPARATE from SignalMin so a re-arm can carry the ORIGINAL signal's time in SignalMin (data preserved)
      // while ArmMin = the re-arm (stop) bar.
      ArmMin: int
      // RE-ENTRY budget this pending will hand to the position it spawns. The ORIGINAL breakout pending starts at
      // EmaReentries; a re-arm pending (spawned by a stopped position in Advance) carries the DECREMENTED budget.
      Reentries: int
      // RE-ENTRY leg index: 0 for the original entry, 1 for the 1st re-entry, ... Recorded on the trip so a
      // re-entry CAP can be applied POST-HOC (filter re_index <= N) — run once with unlimited re-entries, slice.
      ReIdx: int
      BreakoutRef: float; AtrPct: float; Tightness: float; CumVol: int64; BoVol: int64; NewVolHigh: bool
      VolVsHigh: float; BoOpen: float; PrevClose: float; Chg20m: float; TwoBar: float
      SignalMin: int; SignalHigh: float; SignalOpen: float; SignalLow: float; SignalClose: float
      SessVolHigh: int64 }

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
      // ----- A-BOOK GATES (baked into the engine so it emits ONLY A-book trips — tiny CSVs, no post-hoc bloat) -----
      MinAtrPct: float         // A-book FLOOR: reject the ARM bar unless its intraday log-ATR >= this. 0 = off.
                               // The V2/V3 A-book value is 0.03. (Complements MaxAtrPct, which is the ceiling.)
      MinBrv15m: float         // LOOKAHEAD-FREE twin of MinBrv20d: reject the ARM bar unless
                               // bar_vol / (D's own 09:30-09:45 mean 1m vol) >= this. 0 = off (default).
                               // brv20d's baseline needs D's CLOSE; this one is complete at 09:45 =
                               // EntryStartMin, so it is legal. See F14g/F14h.
      MinBrv20d: float         // A-book MAIN LEVER: reject the ARM bar unless its brv20d >= this. 0 = off. brv20d =
                               // breakout_bar_vol / (AvgVol20 * AdjRatio / 390) — the breakout bar's volume vs the
                               // 20d ADJUSTED daily-avg per-minute. A-book value is 100. Needs AvgVol20/AdjRatio
                               // (passed to the engine ctor). Under EmaEntry this gates at the SIGNAL (arm) bar.
      SessionStartMin: int     // first ET minute fed to the engine (510 = 08:30 ET — the SMB "1h
                               // opening range"; the running high/low/volume accumulate from here).
      EntryStartMin: int       // earliest ET minute an entry may fire (575 = 09:35). The engine
                               // PROCESSES premarket bars (to warm the running extremes) but does not
                               // TRADE before this wall-clock floor.
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
      VolHighFrac: float       // VOLUME-CONFIRMATION gate as a FRACTION of the running session max 1m-bar
                               // volume: require bar.volume >= VolHighFrac * runVolHi. 1.0 (default) = the
                               // original "must EXCEED the vol high" gate (a new-vol-high bar). 0.95 / 0.90
                               // relax it to "within 5% / 10% of the high" — probes whether the edge is in
                               // MAKING a new vol high or merely being NEAR elevated volume, recovering trips.
                               // 0.0 = OFF (fire on the first new-session-extreme bar regardless of volume).
                               // NOTE: below 1.0 the `new_vol_high` column still records the STRICT test
                               // (bar > runVolHi), so a relaxed run can still be split on true-new-high vs not.
      // ----- 9-EMA arm-timer ENTRY + session-max-EMA STOP (the V3 addition; all OFF by default so the
      //       engine reproduces V2 byte-for-byte until --ema-entry / --ema-max-stop flip them on) -----
      EmaEntry: bool           // ENTRY MODEL. false (default) = short DIRECTLY on the breakout (V2 behavior).
                               // true = the breakout ARMS a countdown instead: record the 9-EMA at the breakout
                               // bar (emaAtArm), start an EmaArmBars countdown, and SHORT (at close) on the first
                               // bar within the window whose 9-EMA closes BELOW emaAtArm (the pop rolled over —
                               // a confirmed fade). Timer expires with no cross => no trade. Re-arms on each new
                               // session high. Mirrors BreakoutTimer's arm-timer, flipped for the short.
      EmaPeriod: int           // the EMA period for the arm-timer entry + max-EMA stop (default 9).
      EmaArmBars: int          // the countdown length (bars) after a breakout during which the 9-EMA-cross-under
                               // short may fire (default 10).
      // ----- alt ENTRY TRIGGER: bars-since-9EMA-high (instead of the 9-EMA cross-under) -----
      EmaBarsSinceHighEntry: bool // when true (with EmaEntry), FIRE the short as soon as barsSinceEmaHigh reaches
                               // EmaBarsSinceHigh — i.e. on the FIRST small weakness (the 9-EMA hasn't made a new
                               // session high for N bars), NOT when the 9-EMA crosses back under a stale armed
                               // level (which can lag ~1h). Mirrors BreakoutTimer's barsSinceEmaHigh. The pending
                               // arms on each new 9-EMA session high (resetting the counter). Once per episode.
      EmaBarsSinceHigh: int    // the bars-since-9EMA-high threshold to enter (default 2 = the 9-EMA has stalled/
                               // ticked down for 2 bars). Only used when EmaBarsSinceHighEntry is on.
      EmaDownTickEntry: bool   // alt ENTRY TRIGGER (with EmaEntry): fire the short when the live 9-EMA TICKS DOWN
                               // vs the PRIOR bar's 9-EMA (ema < prevEma) — a pure "EMA turned down" weakness,
                               // with NO requirement that the 9-EMA ever made a session high (unlike bars-since-
                               // high). Catches weakness on names whose EMA stalls without a clean session high.
                               // Takes precedence over the bars-since-high and cross-under triggers when on.
      EmaReentries: int        // RE-ENTRY budget per pop (down-tick mode only): after a position is stopped out by
                               // the ema-max-stop, re-short on the NEXT 9-EMA down-tick — up to this many times
                               // (0 = off / give up after the first stop, default). Each re-entry gets a FRESH stop
                               // (its own 30m-max-9EMA × (1+buffer) frozen at the re-entry bar). Chain ends at the
                               // cap or MOC. Pairs with a TIGHT buffer (e.g. 0.05) — tight stop + several re-probes.
      EmaMaxStop: bool         // EXIT MODEL. false (default) = the existing exits (V2). true = add a session-max-
                               // 9EMA stop: while short, COVER (at close) when the live 9-EMA rises ABOVE the
                               // running session-max 9-EMA × (1 + EmaMaxStopBuffer). The max is LIVE/trailing
                               // (keeps updating during the hold), so a new EMA session high = "we're wrong" = exit.
      EmaMaxStopWindow: int    // MAX-EMA STOP anchor window (bars). 0 (default) = the SESSION-cumulative max
                               // 9-EMA. N > 0 = a ROLLING N-bar max 9-EMA (e.g. 30 = a 30m local EMA high).
                               // The session anchor stales badly: if a stock popped EARLY (pushing the session
                               // max up) then we short a LATER, LOWER pop, the session-anchored stop sits far
                               // above where we shorted → it lets the trade run 100%+ before firing (the −153%
                               // MGIH loser). A rolling window re-anchors to the RECENT local high near the fill.
      EmaMaxStopBuffer: float  // a fractional buffer RAISING the max-EMA stop above the session max (0.0 = stop
                               // exactly at the session-max 9-EMA; 0.05 = 5% above it, tolerating a small poke
                               // over the high before covering). Only used when EmaMaxStop is on.
      // ----- alternative EXIT: a pure 9-EMA %-stop anchored to the ENTRY 9-EMA (not the session max) -----
      EmaPctStop: float        // 9-EMA %-STOP: while short, cover (at close) when the live 9-EMA rises to
                               // ema_at_entry × (1 + EmaPctStop) — a UNIFORM per-trade cap measured from the
                               // 9-EMA at fill, NOT the session max. Tighter/more uniform than the max-EMA stop
                               // (which lets a session with an already-high max drift far). 0 = OFF (default).
                               // Mutually usable with EmaMaxStop (both checked; first to fire wins). e.g. 0.60.
      // ----- alternative EXIT: a PRICE stop over the rolling-window max RAW CLOSE (not the 9-EMA) -----
      MaxCloseStop: bool       // MAX-CLOSE STOP. false (default) = off. true = while short, COVER (at close)
                               // when the raw bar CLOSE rises above the rolling-N-bar max close × (1+buffer),
                               // frozen at entry (same freeze discipline as EmaStopBase). Faster/tighter than
                               // the EMA anchor — raw close reacts a bar sooner than the 9-EMA, so it cuts the
                               // worst trades quicker. Re-entries chain off it exactly like the EMA stop.
      MaxCloseStopWindow: int  // MAX-CLOSE STOP anchor window (bars). Rolling N-bar max of the raw close.
                               // Default 20 (a 20m local price high). Re-anchors to the recent high near the fill.
      MaxCloseStopBuffer: float // buffer RAISING the max-close stop above the rolling max close (0.10 = 10%
                               // above the local high before covering). Only used when MaxCloseStop is on.
      // ----- APPLES-TO-APPLES entry-timing test: short the HIGH, but arm the stop on the first down-tick -----
      ShortHighEntry: bool     // ENTRY TIMING (down-tick mode, ORIGINAL leg only): instead of waiting for the
                               // 9-EMA down-tick to ENTER, short the SIGNAL/breakout bar IMMEDIATELY (the high),
                               // and leave the ema-max stop DORMANT until the first 9-EMA down-tick — at which
                               // point the stop arms with its roll30-max base frozen at THAT down-tick bar (exactly
                               // where the down-tick-ENTRY book freezes it). Isolates the entry-price question:
                               // short-the-high vs short-the-down-tick, same stop/re-entry mechanics keyed to the
                               // same down-tick bar. Re-entries STILL enter at the next down-tick (only leg 0 differs).
                               // Pre-arm: NO exits except MOC (fully unprotected until the down-tick). Off (default).
      // ----- the LONG BREAKOUT book: BUY the new-session-high pop, SELL on the first 9-EMA down-tick -----
      EmaDownTickExit: bool }  // EXIT TRIGGER: while HOLDING, close the position when the live 9-EMA TICKS DOWN
                               // vs the prior bar (ema < prevEma). The inversion of EmaDownTickEntry: instead of
                               // SHORTING on 9-EMA weakness we EXIT a long on it. Pair with --long-breakout
                               // (Short=false, direct entry on the breakout bar) to test the long side of the pop.
                               // Off (default). No re-entries / no other stops needed — the down-tick IS the exit.

/// Per-(ticker, day) intraday engine. Feed it the day's RTH MinuteBar[] in time
/// order via `Process`, then `Finalize` and read `Trips()`.
type IntradaySystem(cfg: IntradayConfig, ticker: string, day: DateOnly, prevClose: float,
                    avgVol20: float, adjRatio: float, meanBarVol15m: float) =

    // day extension at a bar = close vs the prior daily close (how far up/down on the day).
    // Used by --ext-gate to route direct-vs-rollover entry and gate the rollover fill.
    let extension (px: float) = if prevClose > 0.0 then px / prevClose - 1.0 else 0.0

    // brv20d for a breakout bar's volume = vol / (avgVol20 * adjRatio / 390) — the A-book main lever. The daily
    // per-minute baseline; matches the post-hoc SQL exactly. 0/nan when the daily context is unusable (gate off).
    let brv20dBaseline = if avgVol20 > 0.0 && adjRatio > 0.0 then avgVol20 * adjRatio / 390.0 else nan
    let brv20dOf (vol: int64) = if Double.IsNaN brv20dBaseline || brv20dBaseline <= 0.0 then nan else float vol / brv20dBaseline

    // brv15m = bar volume / the name's OWN 09:30-09:45 mean 1m volume (vol_0945/nbar_0945).
    // The LOOKAHEAD-FREE twin of brv20d: the baseline is D's own opening-15m tempo, complete at 09:45,
    // and EntryStartMin is 09:45 — so it is knowable exactly when the first entry may fire. brv20d's
    // baseline (avgVol20) instead contains D's OWN full-session volume = not knowable until D's close.
    // Same quantity conceptually (a bar's volume spike vs a baseline), but honestly measurable. F14h.
    let brv15mOf (vol: int64) =
        if Double.IsNaN meanBarVol15m || meanBarVol15m <= 0.0 then nan else float vol / meanBarVol15m

    // ----- rolling intraday structures (1m timeframe) -----
    let atrLog    = AvgMa(cfg.VolWindow)        // mean LOG true range over the last VolWindow 1m bars
    let atrLin    = AvgMa(cfg.VolWindow)        // mean ABSOLUTE true range (linear)
    let lag20     = LagMa<float>(20)            // 20-bar close delay line -> the 20-minute %-change into entry
    let rangeHigh = MaxMa(cfg.VolWindow)        // intraday tightness: max high over the window
    let rangeLow  = MinMa(cfg.VolWindow)        // intraday tightness: min low over the window
    let ema       = EmaMa(cfg.EmaPeriod)        // the 9-EMA — the arm-timer entry ref + the max-EMA stop ref
    // rolling N-bar MAX of the 9-EMA (the LOCAL EMA high) — the alt max-EMA-stop anchor when EmaMaxStopWindow>0.
    // Sized only when the rolling anchor is used (window>0); fed the folded EMA each bar.
    let emaRollMax = if cfg.EmaMaxStopWindow > 0 then MaxMa(cfg.EmaMaxStopWindow) else MaxMa(1)
    // rolling N-bar MAX of the raw CLOSE — the max-close-stop anchor when MaxCloseStop is on. Sized only when used.
    let closeRollMax = if cfg.MaxCloseStop then MaxMa(cfg.MaxCloseStopWindow) else MaxMa(1)

    // ----- mean-reversion TARGET anchors (only the one named by cfg.Target is fed) -----
    // session VWAP = Σ(typical_price · volume) / Σ(volume), typical = (h+l+c)/3, from
    // SessionStartMin. Bar-based (the 1m parquet carries no trade-exact VWAP) — adequate
    // for a 1m mean-reversion anchor.
    let mutable vwapNum  = 0.0                   // Σ tp·v
    let mutable vwapDen  = 0.0                   // Σ v
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

    // ----- 9-EMA arm-timer ENTRY + session-max-EMA STOP state (only live when the flags are on) -----
    // session-cumulative MAX of the folded 9-EMA (LIVE/trailing — keeps updating during a hold; the max-EMA
    // stop reference). No reset — per-(ticker,day) engine.
    let mutable sessMaxEma : float voption = ValueNone
    // bars since the 9-EMA last made a NEW session high (0 on a fresh EMA-high, +1 otherwise). The bars-since-high
    // entry trigger (EmaBarsSinceHighEntry): fire when this reaches EmaBarsSinceHigh = the first small weakness.
    let mutable barsSinceEmaHigh = 0
    // the 9-EMA of the PRIOR bar (snapshotted before the current push) — the EmaDownTickEntry reference.
    let mutable prevEma : float voption = ValueNone
    // PER-SIGNAL PENDING TRADES (replaces the old global timer): each new-session-high breakout arms its OWN
    // independent pending — its armed 9-EMA level (emaAtArm), its own remaining-bars countdown, and the breakout
    // (signal) bar's features captured at arm time (so the trip describes the pop being faded, not the later
    // cross-under fill). Each pending fires INDEPENDENTLY when ITS 9-EMA closes below ITS emaAtArm before ITS
    // countdown expires. Multiple pendings coexist; all qualifying ones fire on the same bar (user's call).
    // `bars` is decremented each bar; a pending is dropped when it fires or when bars reaches 0 (expired).
    let pending = ResizeArray<PendingSignal>()

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
    member this.ShouldEnter (bar: MinuteBar) : bool =
        let inline gate (v: _ voption) (test: _ -> bool) =
            match v with ValueSome x -> test x | ValueNone -> false
        // EMA-entry (cfg.EmaEntry) never enters via ShouldEnter — its entries are per-signal PENDINGS fired in
        // FirePendings. So under EmaEntry the direct breakout entry is fully OFF here.
        if cfg.EmaEntry then false
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
        // A-BOOK GATES (baked in) — same as the EmaEntry arm gates, on the direct-entry breakout bar.
        && (cfg.MinAtrPct <= 0.0 || (match this.AtrPct with ValueSome a -> a >= cfg.MinAtrPct | ValueNone -> false))
        && (cfg.MinBrv20d <= 0.0 || (let b = brv20dOf bar.volume in not (Double.IsNaN b) && b >= cfg.MinBrv20d))
        && (cfg.MinBrv15m <= 0.0 || (let b = brv15mOf bar.volume in not (Double.IsNaN b) && b >= cfg.MinBrv15m))
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

        // 2) fold the bar in. True range vs the prior 1m close.
        match lastBar with
        | ValueSome prev when bar.high > 0.0 && bar.low > 0.0 && prev.close > 0.0 ->
            let pc = prev.close
            (max bar.high pc - min bar.low pc) |> atrLin.Push
            log (max bar.high pc / min bar.low pc) |> atrLog.Push
        | _ -> ()

        rangeHigh.Push bar.high
        rangeLow.Push  bar.low
        runHi    <- match runHi with ValueSome h -> ValueSome (max h bar.high) | ValueNone -> ValueSome bar.high
        runLo    <- match runLo with ValueSome l -> ValueSome (min l bar.low)  | ValueNone -> ValueSome bar.low
        runHiClose <- match runHiClose with ValueSome h -> ValueSome (max h bar.close) | ValueNone -> ValueSome bar.close
        runLoClose <- match runLoClose with ValueSome l -> ValueSome (min l bar.close) | ValueNone -> ValueSome bar.close
        runVolHi <- match runVolHi with ValueSome v -> ValueSome (max v bar.volume) | ValueNone -> ValueSome bar.volume
        cumVol   <- cumVol + bar.volume
        lag20.Push bar.close      // 20-bar delay line; read post-fold at entry = the 20m %-change into it
        if sessionOpen.IsNone then sessionOpen <- ValueSome bar.``open``

        // fold the chosen mean-reversion anchor (only the active one accumulates).
        match cfg.Target with
        | NoTarget -> ()
        | Vwap ->
            let tp = (bar.high + bar.low + bar.close) / 3.0
            vwapNum <- vwapNum + tp * float bar.volume
            vwapDen <- vwapDen + float bar.volume
        | Ma _ -> maTgt.Push bar.close
        | Channel _ -> chanTgt.Push bar.low

        // 9-EMA fold + arm-timer bookkeeping (only meaningful when EmaEntry/EmaMaxStop are on; cheap otherwise).
        // Snapshot the PRIOR-bar EMA (the down-tick reference), then fold the EMA on the current close, then update
        // the LIVE session-max EMA (the trailing max-EMA stop ref).
        prevEma <- ema.State
        ema.Push bar.close
        // is THIS bar a new 9-EMA session high? (compare the fresh EMA to the STRICTLY-PRIOR session max, i.e.
        // sessMaxEma BEFORE this bar's update below). NaN/None prior = the first EMA = a new high.
        let isNewEmaHigh =
            match ema.State with
            | ValueSome e -> (match sessMaxEma with ValueSome m -> e > m | ValueNone -> true)
            | ValueNone -> false
        (match ema.State with
         | ValueSome e ->
             sessMaxEma <- (match sessMaxEma with ValueSome m -> ValueSome (max m e) | ValueNone -> ValueSome e)
             if cfg.EmaMaxStopWindow > 0 then emaRollMax.Push e   // rolling local EMA-high anchor
         | ValueNone -> ())
        if cfg.MaxCloseStop then closeRollMax.Push bar.close   // rolling local raw-close-high anchor (max-close stop)
        // bars-since-9EMA-high counter (BreakoutTimer idiom): 0 on a fresh EMA high, +1 otherwise.
        barsSinceEmaHigh <- (if isNewEmaHigh then 0 else barsSinceEmaHigh + 1)
        // ARM a NEW PENDING on a new-session-HIGH PRICE breakout — SAME in both entry models (short into every
        // high, as before). Only the FIRE condition differs (cross-under vs bars-since-high), applied in Process.
        // Each new high arms its OWN pending (features captured at the arm bar). Pendings fire below.
        if cfg.EmaEntry then
            let priorHigh =
                if cfg.MinCloseRef then (if cfg.Downside then sRunLoClose else sRunHiClose)
                else (if cfg.Downside then sRunLo else sRunHi)
            let isArm =
                bar.etMin >= cfg.EntryStartMin
                && (match priorHigh with
                    | ValueSome ext -> (if cfg.Downside then bar.close < ext else bar.close > ext)
                    | ValueNone -> false)
                // A-BOOK GATES (baked in): the ARM bar must clear the ATR% floor and the brv20d main lever, so the
                // engine emits only A-book trips. ATR% uses this.AtrPct (= sAtrLog, the value captured into the
                // pending); brv20d uses THIS bar's breakout volume. 0 = gate off.
                && (cfg.MinAtrPct <= 0.0 || (match this.AtrPct with ValueSome a -> a >= cfg.MinAtrPct | ValueNone -> false))
                && (cfg.MinBrv20d <= 0.0 || (let b = brv20dOf bar.volume in not (Double.IsNaN b) && b >= cfg.MinBrv20d))
                && (cfg.MinBrv15m <= 0.0 || (let b = brv15mOf bar.volume in not (Double.IsNaN b) && b >= cfg.MinBrv15m))
                // ROOM at ARM time: don't QUEUE a pending when we're already at capacity. Cap TOTAL committed
                // exposure (currently-open Holding + already-queued pendings) at MaxConcurrent. This filters
                // PENDINGS (not fills) so the queue never backlogs while a position is held — the source of the
                // stack (a pile of stale pendings all firing when the open position finally exits). 0 = unlimited.
                && (cfg.MaxConcurrent <= 0 || this.OpenCount + pending.Count < cfg.MaxConcurrent)
            if isArm then
                let twoBar =
                    match sLastBar with
                    | ValueSome prev -> if cfg.Downside then max prev.high bar.high else min prev.low bar.low
                    | ValueNone      -> if cfg.Downside then bar.high else bar.low
                pending.Add
                    { EmaAtArm  = (match ema.State with ValueSome e -> e | ValueNone -> nan)
                      Bars      = cfg.EmaArmBars
                      ArmMin    = bar.etMin
                      Reentries = cfg.EmaReentries
                      ReIdx     = 0
                      BreakoutRef = (match this.BreakoutRef with ValueSome x -> x | ValueNone -> nan)
                      AtrPct      = (match this.AtrPct with ValueSome x -> x | ValueNone -> nan)
                      Tightness   = (match this.Tightness with ValueSome x -> x | ValueNone -> nan)
                      CumVol      = cumVol
                      BoVol       = bar.volume
                      NewVolHigh  = (match sRunVolHi with ValueSome vh -> bar.volume > vh | ValueNone -> true)
                      VolVsHigh   = (match sRunVolHi with ValueSome vh when vh > 0L -> float bar.volume / float vh | _ -> nan)
                      BoOpen      = bar.``open``
                      PrevClose   = (match sLastBar with ValueSome p -> p.close | ValueNone -> nan)
                      Chg20m      = (match lagPctChange lag20 with ValueSome p -> p | ValueNone -> nan)
                      TwoBar      = twoBar
                      SignalMin   = bar.etMin
                      SignalHigh  = bar.high
                      SignalOpen  = bar.``open``
                      SignalLow   = bar.low
                      SignalClose = bar.close
                      SessVolHigh = (match sRunVolHi with ValueSome vh -> vh | ValueNone -> 0L) }

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
    member _.Advance (bar: MinuteBar) (pos: IntradayPosition) : IntradayPosition =
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
            // SHORT-THE-HIGH stop-arming (ShortHighEntry): this leg entered at the high with a DORMANT ema-max stop.
            // On the FIRST 9-EMA down-tick (ema < prevEma for a short), arm the stop — freeze EmaStopBase from the
            // roll30-max 9-EMA at THIS bar (exactly where the down-tick-ENTRY book freezes it) and clear the flag.
            // Until then the position is unprotected (MOC-only). Rebind `pos` so the checks below see the armed base.
            let pos =
                if pos.StopArmPending && cfg.EmaMaxStop && cfg.Short then
                    let down = match ema.State, prevEma with ValueSome e, ValueSome p -> e < p | _ -> false
                    if down then
                        let baseV =
                            if cfg.EmaMaxStopWindow > 0 then (match emaRollMax.State with ValueSome m -> m | ValueNone -> nan)
                            else (match sessMaxEma with ValueSome m -> m | ValueNone -> nan)
                        { pos with EmaStopBase = baseV; StopArmPending = false
                                   ArmMin = bar.etMin; ArmClose = bar.close }   // record the right-side-of-the-V point
                    else pos
                else pos
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
            // MAX-EMA STOP (cfg.EmaMaxStop): while short, cover (at close) when the live 9-EMA CLOSES above the
            // stop level = EmaStopBase × (1 + buffer). EmaStopBase is the session-max 9-EMA FROZEN at the entry
            // bar (per-position), NOT the live trailing max — freezing makes the buffer meaningfully spaced (a
            // trailing base rises with the EMA, so any buffer ≥~10% saturates and the stop never fires). The
            // 9-EMA rising buffer% above where it was at entry = the pop we faded is re-taking the high → exit.
            let emaMaxStopHit =
                cfg.EmaMaxStop && cfg.Short && not (Double.IsNaN pos.EmaStopBase)
                && (match ema.State with
                    | ValueSome e -> e > pos.EmaStopBase * (1.0 + cfg.EmaMaxStopBuffer)
                    | ValueNone -> false)
            // 9-EMA %-STOP (cfg.EmaPctStop): cover when the live 9-EMA CLOSES above ema_at_entry × (1+EmaPctStop).
            // A UNIFORM per-trade cap off the entry 9-EMA (unlike the session-max anchor, which lets a session
            // with an already-high max drift far — the source of the −153% MGIH loser). First stop to fire wins.
            let emaPctStopHit =
                cfg.EmaPctStop > 0.0 && cfg.Short && not (Double.IsNaN pos.EmaAtEntry)
                && (match ema.State with
                    | ValueSome e -> e > pos.EmaAtEntry * (1.0 + cfg.EmaPctStop)
                    | ValueNone -> false)
            // MAX-CLOSE STOP (cfg.MaxCloseStop): while short, cover (at close) when the RAW bar close rises above
            // CloseStopBase × (1+buffer). CloseStopBase = the rolling-N-bar max close FROZEN at entry. Raw close
            // reacts a bar sooner than the 9-EMA → cuts the worst runners faster. Shares the re-entry chain path.
            let maxCloseStopHit =
                cfg.MaxCloseStop && cfg.Short && not (Double.IsNaN pos.CloseStopBase)
                && bar.close > pos.CloseStopBase * (1.0 + cfg.MaxCloseStopBuffer)
            // LONG-BREAKOUT EXIT (cfg.EmaDownTickExit): while holding a LONG, sell (at close) on the first 9-EMA
            // down-tick vs the prior bar — the inversion of the short's down-tick ENTRY. This IS the exit; no stops.
            let emaDownTickExitHit =
                cfg.EmaDownTickExit && not cfg.Short
                && (match ema.State, prevEma with
                    | ValueSome e, ValueSome pe -> e < pe
                    | _ -> false)
            if emaDownTickExitHit then
                { pos with State = ExitedAt (bar.etMin, bar.close, "ema_down_tick") }
            elif emaMaxStopHit || emaPctStopHit || maxCloseStopHit then
                let reason =
                    if maxCloseStopHit && not emaMaxStopHit && not emaPctStopHit then "max_close_stop"
                    elif emaPctStopHit && not emaMaxStopHit then "ema_pct_stop" else "ema_max_stop"
                // RE-ENTRY: stopped by an EMA stop with budget left and before MOC → spawn a fresh re-arm pending
                // (same pop's signal features, Reentries-1). It re-shorts on the NEXT down-tick (the EMA is rising
                // at the stop, so the down-tick is false THIS bar; SignalMin=this bar makes the fire loop skip it
                // this bar). Each re-entry gets its OWN fresh 30m-max×(1+buffer) stop (captured at its entry bar).
                // NO room gate here: THIS position is exiting (vacating its slot), so its re-arm is a 1-for-1
                // replacement of the SAME chain's slot — not new exposure — so it never grows the stack.
                if pos.Reentries > 0 && bar.etMin < cfg.MocMin then
                    pending.Add
                        { EmaAtArm = nan; Bars = cfg.EmaArmBars; ArmMin = bar.etMin; Reentries = pos.Reentries - 1
                          ReIdx = pos.ReIdx + 1
                          BreakoutRef = pos.BreakoutRef; AtrPct = pos.AtrPctAtEntry; Tightness = pos.TightnessAtEntry
                          CumVol = pos.CumVolAtEntry; BoVol = pos.SignalVolume; NewVolHigh = pos.NewVolHigh
                          VolVsHigh = pos.VolVsHigh; BoOpen = pos.BreakoutBarOpen; PrevClose = pos.PrevBarClose
                          Chg20m = pos.Chg20mAtEntry; TwoBar = pos.StopLevel
                          // SignalMin = the ORIGINAL pop's signal time (data preserved); ArmMin = the stop bar (skip).
                          SignalMin = pos.SignalMin; SignalHigh = pos.SignalHigh; SignalOpen = pos.SignalOpen
                          SignalLow = pos.SignalLow; SignalClose = pos.SignalClose; SessVolHigh = pos.SessVolHighAtSignal }
                { pos with State = ExitedAt (bar.etMin, bar.close, reason) }
            elif cfg.UseStop && stopHit then
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

        // Build a Holding/Armed position from a PendingSignal `af` filled at `entryPx` this bar.
        // stopArmPending=true (ShortHighEntry leg-0): enter with a DORMANT ema-max stop (base nan) that arms on the
        // first 9-EMA down-tick in Advance. Normal fills pass false → the stop base is frozen here at entry as usual.
        let addPositionArm (af: PendingSignal) (entryPx: float) (state: IntraPosState) (stopArmPending: bool) =
            positions.Add
                { EntryMin = bar.etMin
                  EntryPx = entryPx
                  // freeze the max-EMA-stop base at entry (structures are live here — ProcessBar folded this bar).
                  // Window 0 = the session-cumulative max 9-EMA; window N = the rolling N-bar local max 9-EMA
                  // (re-anchors to the recent high near the fill). Stop = base × (1+buffer); nan when the stop is off.
                  // stopArmPending → DORMANT: leave the base nan now; Advance sets it on the first down-tick.
                  StopArmPending = stopArmPending
                  EmaStopBase =
                    (if stopArmPending || not cfg.EmaMaxStop then nan
                     elif cfg.EmaMaxStopWindow > 0 then (match emaRollMax.State with ValueSome m -> m | ValueNone -> nan)
                     else (match sessMaxEma with ValueSome m -> m | ValueNone -> nan))
                  // the live 9-EMA at fill — the anchor for the EmaPctStop (level = EmaAtEntry × (1+EmaPctStop)).
                  EmaAtEntry = (if cfg.EmaPctStop > 0.0 then (match ema.State with ValueSome e -> e | ValueNone -> nan) else nan)
                  // freeze the max-CLOSE-stop base at entry: the rolling-N-bar max raw close as-of this bar.
                  CloseStopBase = (if cfg.MaxCloseStop then (match closeRollMax.State with ValueSome m -> m | ValueNone -> nan) else nan)
                  ArmMin = -1                          // set on the first down-tick when the dormant stop arms
                  ArmClose = nan
                  StopLevel = af.TwoBar
                  BreakoutRef = af.BreakoutRef
                  AtrPctAtEntry = af.AtrPct
                  TightnessAtEntry = af.Tightness
                  CumVolAtEntry = af.CumVol
                  BreakoutBarVol = af.BoVol           // = the SIGNAL (arm) bar's volume under EmaEntry
                  NewVolHigh = af.NewVolHigh
                  VolVsHigh = af.VolVsHigh
                  BreakoutBarOpen = af.BoOpen
                  PrevBarClose = af.PrevClose
                  Chg20mAtEntry = af.Chg20m
                  SignalMin = af.SignalMin
                  SignalHigh = af.SignalHigh
                  SignalOpen = af.SignalOpen
                  SignalLow = af.SignalLow
                  SignalClose = af.SignalClose
                  SignalVolume = af.BoVol
                  SessVolHighAtSignal = af.SessVolHigh
                  Reentries = af.Reentries
                  ReIdx = af.ReIdx
                  State = state }
        // normal fill: stop armed at entry (stopArmPending=false). ShortHighEntry leg-0 uses addPositionArm ... true.
        let addPosition (af: PendingSignal) (entryPx: float) (state: IntraPosState) =
            addPositionArm af entryPx state false

        if cfg.EmaEntry && cfg.EmaDownTickEntry then
            // ===== EMA-DOWN-TICK TRIGGER: a pending fires when the live 9-EMA ticked DOWN vs the prior bar's 9-EMA
            //       (ema < prevEma) — a pure "EMA turned down" weakness, NO session-high requirement. A pending
            //       armed THIS bar is skipped (its breakout bar is an EMA up-tick anyway; earliest fire = next bar). =====
            let emaDown =
                match ema.State, prevEma with
                | ValueSome e, ValueSome p -> (if cfg.Downside then e > p else e < p)
                | _ -> false
            let toRemove = ResizeArray<int>()
            for i in 0 .. pending.Count - 1 do
                let p = pending.[i]
                let room = cfg.MaxConcurrent <= 0 || this.OpenCount < cfg.MaxConcurrent
                // SHORT-THE-HIGH (ShortHighEntry, ORIGINAL leg only, ReIdx=0): fire IMMEDIATELY on the arm/breakout
                // bar (short the high) with a DORMANT stop — do NOT wait for the down-tick. The stop arms later, on
                // the first 9-EMA down-tick (handled in Advance). Re-entries (ReIdx>0) keep the normal down-tick entry.
                let shortHighNow = cfg.ShortHighEntry && p.ReIdx = 0 && p.ArmMin = bar.etMin
                if shortHighNow && room then
                    addPositionArm p bar.close Holding true       // short the high, stop dormant
                    toRemove.Add i
                elif p.ArmMin = bar.etMin then ()                 // just (re)armed this bar — earliest fire is next bar
                elif emaDown && room then
                    addPosition p bar.close Holding               // 9-EMA ticked down → fire
                    toRemove.Add i
            for k in toRemove.Count - 1 .. -1 .. 0 do pending.RemoveAt toRemove.[k]
        elif cfg.EmaEntry && cfg.EmaBarsSinceHighEntry then
            // ===== BARS-SINCE-9EMA-HIGH TRIGGER: a pending fires once the SESSION-level barsSinceEmaHigh (bars
            //       since the last new 9-EMA session high) is >= threshold. threshold 0 = short directly into the
            //       high (fires the bar it arms). All currently-live pendings that clear the threshold fire; each is
            //       removed on firing. (Pendings are only added on price-breakout arms, same as cross-under.) =====
            let toRemove = ResizeArray<int>()
            for i in 0 .. pending.Count - 1 do
                // recompute room EACH iteration — OpenCount rises as pendings fire below, so the concurrency cap
                // must be re-checked per add (else all qualifying pendings fire on one bar, ignoring MaxConcurrent).
                let room = cfg.MaxConcurrent <= 0 || this.OpenCount < cfg.MaxConcurrent
                if barsSinceEmaHigh = cfg.EmaBarsSinceHigh && room then
                    addPosition pending.[i] bar.close Holding    // EXACTLY N bars since the EMA high → fire
                    toRemove.Add i
            for k in toRemove.Count - 1 .. -1 .. 0 do pending.RemoveAt toRemove.[k]
        elif cfg.EmaEntry then
            // ===== CROSS-UNDER TRIGGER (default): fire every pending whose 9-EMA has now closed below ITS armed
            //       level; age the rest and drop the expired. A pending armed THIS bar (signalMin = bar.etMin) is
            //       not aged/fired yet. All qualifying pendings fire this bar; MaxConcurrent (if set) caps how many.
            let live = ema.State
            let toRemove = ResizeArray<int>()
            for i in 0 .. pending.Count - 1 do
                let p = pending.[i]
                if p.ArmMin = bar.etMin then ()                  // just (re)armed this bar — leave it for next bar
                else
                    let crossed =
                        match live with
                        | ValueSome e -> (if cfg.Downside then e > p.EmaAtArm else e < p.EmaAtArm)
                        | ValueNone -> false
                    let room = cfg.MaxConcurrent <= 0 || this.OpenCount < cfg.MaxConcurrent
                    if crossed && room then
                        addPosition p bar.close Holding          // fill at this bar's close
                        toRemove.Add i
                    else
                        p.Bars <- p.Bars - 1
                        if p.Bars <= 0 then toRemove.Add i       // expired without a cross → drop, no trip
            // remove fired/expired pendings back-to-front (stable indices)
            for k in toRemove.Count - 1 .. -1 .. 0 do pending.RemoveAt toRemove.[k]
        elif this.ShouldEnter bar then
            // DIRECT entry (V2): the signal bar IS this bar, so signal* == entry-bar values. sLastBar/Value reads
            // are safe (entries can't fire before 09:45, a prior bar always exists).
            let twoBarDirect =
                if cfg.Downside then max sLastBar.Value.high bar.high
                else min sLastBar.Value.low bar.low
            let af : PendingSignal =
                { EmaAtArm = nan; Bars = 0; ArmMin = bar.etMin; Reentries = 0; ReIdx = 0
                  BreakoutRef = this.BreakoutRef.Value; AtrPct = this.AtrPct.Value; Tightness = this.Tightness.Value
                  CumVol = cumVol; BoVol = bar.volume
                  NewVolHigh = (match sRunVolHi with ValueSome vh -> bar.volume > vh | ValueNone -> true)
                  VolVsHigh = (match sRunVolHi with ValueSome vh when vh > 0L -> float bar.volume / float vh | _ -> nan)
                  BoOpen = bar.``open``; PrevClose = sLastBar.Value.close
                  Chg20m = (match lagPctChange lag20 with ValueSome p -> p | ValueNone -> nan)
                  TwoBar = twoBarDirect
                  SignalMin = bar.etMin; SignalHigh = bar.high; SignalOpen = bar.``open``
                  SignalLow = bar.low; SignalClose = bar.close
                  SessVolHigh = (match sRunVolHi with ValueSome vh -> vh | ValueNone -> 0L) }
            let state =
                if cfg.ExtGate > 0.0 then
                    if extension bar.close >= cfg.ExtGate then Holding
                    else Armed (bar.etMin, af.TwoBar, bar.close, false)
                elif cfg.TrailEntry || cfg.RiseEntry > 0.0 then Armed (bar.etMin, af.TwoBar, bar.close, false)
                else Holding
            addPosition af bar.close state

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
