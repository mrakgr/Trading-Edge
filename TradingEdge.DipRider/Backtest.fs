module TradingEdge.DipRider.Backtest

open System
open System.Globalization
open System.Collections.Generic
open DuckDB.NET.Data
open TradingEdge.DipRider.Intraday

// ===========================================================================
// LowFlyer backtest wiring.
//
// There is NO daily selection engine. Pipeline 1 is a pure-SQL read of the
// `mr_candidate` table (built by scripts/equity/build_mr_candidate.fsx): every
// (ticker, day) that clears the market-wide preconditions — median 09:30-09:45
// 1m-bar vol >= 10k AND >= 10 bars, CS/ADRC, price >= $1 — carrying the daily
// context (prev/3d close, day close, adj ratio, 20-bar avg vol) and the forward
// returns (D+1/3/5 close, reported for post-hoc slicing). Pipeline 2 streams
// each candidate day's minute bars into the reused IntradaySystem (Intraday.fs)
// driven downside/long to buy the high-volume flush to a new session low, held
// to MOC. Same two-pipeline shape as MaxFlyer, minus the daily engine.
// ===========================================================================

/// LowFlyer config = the intraday engine knobs + notional. No daily gates.
type Config =
    { Intraday: IntradayConfig
      Notional: float }

/// Production default: the market-wide long mean-reversion setup. Downside
/// breakout (new session low) + long P&L, no stop / no target, hold to MOC,
/// scan from 09:45, RTH warmup from 09:30. The two intraday quality gates
/// (tightness / ATR%) are OFF (+inf) — only new-session-low + volume-confirm +
/// the 09:45 floor survive.
let defaultConfig =
    { Intraday =
        { VolWindow = 20
          MaxTightness = infinity        // OFF
          MaxAtrPct = infinity           // OFF
          SessionStartMin = 9 * 60       // 09:30 ET — the SMB session VWAP anchors at the RTH OPEN (not
                                         // premarket). VWAP, the 9-EMA, the below-VWAP counter, and the
                                         // running session low all accumulate from 09:30 for the reclaim.
          EntryStartMin   = 10 * 60      // 10:00 ET — morning-window START (Finding 4: 10:00-13:30 is best;
                                         // 09:30-10:00 warms VWAP/EMA + the weakness run before any entry)
          EntryEndMin     = 13 * 60 + 30 // 13:30 ET — morning-window END (afternoon reclaims fade)
          UseStop = false
          PctStop = 0.0
          TimeStopMin = 0                // DipRider: HOLD-TO-MOC by default (Finding 3: the trade is a
                                         // momentum-CONTINUATION — letting it run beats any short time-stop;
                                         // PF climbs 20m<30m<60m<MOC). --time-stop-min N to re-impose a stop.
          Downside = true                // breakout to a new session LOW
          WickBreakout = false           // CLOSE through the prior low
          ExtGate = 0.0
          RiseEntry = 0.0
          TrailEntry = false
          Short = false                  // LONG (buy the flush)
          Target = NoTarget
          MocMin = 16 * 60               // 16:00 ET
          MaxConcurrent = 1              // ONE position per (ticker,day) (F29): the later same-day adds chase a
                                        // more-extended run and have worse EV — capping at 1 lifts cell PF
                                        // ~+0.2 & helps 2021. --max-concurrent 0 = unlimited (the old default;
                                        // pass it to reproduce V1's archived numbers, which shared this field).
          MinBarFlush = 0.0              // entry-bar flush gate OFF (--min-bar-flush -0.007 to enable)
          MinBarFlushFloor = 0.0         // entry-bar flush-depth floor OFF (--min-bar-flush-floor -0.12 to enable)
          VolHighFrac = 0.90             // volume-confirm = breakout vol >= 90% of the running vol high (PRODUCTION:
                                         // within 10% of the high; +239 trips over strict 1.0 at PF 3.38 vs 3.45 —
                                         // "near the high" carries the signal, <0.90 dilutes. --vol-high-frac to override.)
          MinCloseRef = true             // default = min-CLOSE reference (wick-immune; +~29% trips at ~same
                                         // PF — Run 12). --min-low-ref switches back to the min-LOW channel.
          // ----- SMB VWAP x 9-EMA reclaim (long-only) — OFF; DipRider is this project's engine -----
          VwapReclaim = false            // reclaim engine off; the DipRider block below is ON.
          EmaPeriod = 9                  // SMB 9-EMA.
          BelowVwapFrac = 0.0            // OFF — the consecutive-run gate (MinRunBelowVwap) is the weakness
                                         // filter now (rb>=11 consecutive is a stronger signal than a 60%
                                         // cumulative fraction, and they double-gate if both are on).
          MinRunBelowVwap = 11           // require >=11 CONSECUTIVE bars EMA<VWAP into the cross. The rb FLOOR
                                         // (no upper cap — the by-year work showed the old rb<=30 cap threw
                                         // away ~6.5x the trips for a PF premium that doesn't survive the
                                         // trip-count trade: rb>=11 no-cap = ~5.8k trips / PF 1.48 / positive
                                         // EVERY year, vs rb[11,30] = 896 trips / PF 2.07 / 35 trips in 2025).
          StopAnchorVwap = true          // stop = VWAP - d*StopDistFrac (default); false = entry-anchored.
          FixedPctStop = 0.0             // 0 = use the d-geometry stop; >0 = a fixed %-below-entry stop (Finding 17).
          StopDistFrac = 2.0 / 3.0       // stop distance = d*2/3 (Finding 14): the video's d/3 was too tight
                                         // once the target is off & winners run to MOC — a wider stop lets
                                         // the reclaim breathe (stop-rate 55%->38%). d*2/3 is the peak (PF
                                         // 1.478->1.689); d/2 and full-d are slightly worse. --stop-dist-frac.
          MinStopDistPct = 0.0           // OFF (Finding 18). It was load-bearing at d/3 (Finding 7), but at
                                         // d*2/3 even the p5 stop distance is 1.17% > 1%, so NOTHING lands
                                         // under 1% — the filter is fully inert (removing it is byte-identical:
                                         // 868 trips, PF 1.69). Dropped to simplify. --min-stop-dist-pct to re-enable.
          ClampStopDist = true           // (moot while MinStopDistPct=0) clamp-vs-skip for a too-tight stop.
          MinTightness = 3.0             // require a name with real range. Finding 6 locked 4.5 on the THIN
                                         // book; on the fat book (rb>=11 no-cap) the 3-4.5 band is +EV too —
                                         // tight>=3 gives 1.7x the trips (8.7k->14.9k) and 1.6x net ($635k->
                                         // $1.02M) at ~flat PF (1.39->1.38), positive every year. Below 3 is
                                         // nearly dead; >=3 is the fat-book floor. (--min-tightness overrides.)
          StopOnClose = true             // stop triggers only on a CLOSE below the level (ignore noise wicks).
          UseTarget = false              // NO target by default (Finding 13: winners run to MOC). --use-target re-enables VWAP+d.
          ReclaimShort = false           // LONG reclaim by default; --reclaim-short mirrors to the short side.
          // ----- DipRider (pullback-in-uptrend re-break, long-only) — ON by default for this project -----
          DipRider = true                // this project's engine: buy the re-break after a pullback in an uptrend.
          DipRebreakAtr = 0.5            // re-break close >= prevBar.high * (1 + 0.5*ATR%): a half-ATR expansion
                                         // over the prior high (a decisive resumption bar, not a one-tick poke).
          DipMinBarsBelowEma = 3         // require >= 3 consecutive bars closed below the 9-EMA before the re-break
                                         // (a genuine pullback, not a one-bar wiggle). Swept later.
          DipMaxBarsBelowEma = 8         // CAP the pullback at < 8 bars below the 9-EMA (Finding 2: monotone —
                                         // shallow dips resume, deep ones are broken trends). --dip-max-bars-below-ema.
          DipMinTrendPct = 0.02          // require the re-break close >= 2% above the session open (an established
                                         // intraday uptrend to have pulled back FROM). Swept later.
          DipExitNewHigh = false         // Finding 3: the new-high target AMPUTATES the runners (PF ~0.95 ON
                                         // vs 1.06-1.19 OFF). Continuation trade — let it run. --dip-exit-new-high
                                         // re-enables it.
          // ----- DipRider V2 (run-above-9EMA slopes, long-only) — OFF by default; --diprider-v2 turns it on -----
          DipRiderV2 = false             // V1 is the archived production system; V2 is the research mode.
          RunResetBarsBelow = 4          // F29 sweep (2-5): tol=4 is best on the cell (PF 2.30/2.50 mc0/mc1).
                                         // Excuse up to 4 below-9EMA closes within an up-run before it breaks.
          DipV2MinRunLen = 10            // require the prior above-9EMA run >= 10 bars (the U-shape's good cell;
                                         // len-1 and 2-9 are the weak/dead zone). --dip-v2-min-run-len; 0 = off.
          DipV2Reclaim = false           // re-break trigger by default; --dip-v2-reclaim = enter on the 9-EMA reclaim.
          DipV2MinBarsSinceBreak = 0     // bars-since-break gates OFF by default (recorded feature first).
          DipV2MaxBarsSinceBreak = 0     // --dip-v2-min/max-bars-since-break to enable.
          DipV2PullbackBar = 0           // 0 = use re-break/reclaim trigger; N>0 = buy the Nth below-EMA bar.
          DipV2GeomStop = false          // 2-bar-low stop by default; --dip-v2-geom-stop = run-anchored geometry stop.
          DipV2StopDistFrac = 2.0 / 3.0  // geometry-stop distance below the run floor as a fraction of the run range.
          DipV2BuyIntoRun = 20           // Finding 15: N=20 is the momentum sweet spot (PF 2.17 on the cell).
                                         // 0 = off (fall back to the re-break/reclaim/pullback trigger). --dip-v2-buy-into-run.
          DipV2ExhaustExit = false       // exhaustion exit OFF by default; --dip-v2-exhaust-exit turns it on.
          DipV2ExhaustVolMult = 10.0     // blow-off = exit bar vol >= 10× each per-minute baseline. --dip-v2-exhaust-vol-mult.
          DipV2VwapExitBars = 0          // loss-of-VWAP exit OFF by default; --dip-v2-vwap-exit-bars 10 = require the
                                         // 9-EMA >=10 bars above VWAP then exit when it crosses below.
          DipV2MaxRvol = 0.0 }           // F22 exhaustion cut (OFF by default; --dip-v2-max-rvol 75 enables): skip
                                         // entries with cumulative day vol >= N× the 20d avg daily vol (blown-out tail).
      Notional = 10_000.0 }

/// One candidate (ticker, day) from mr_candidate, with the daily context the
/// engine + the post-hoc feature slicing need. Forward closes are REPORTED only.
type Candidate =
    { Ticker: string
      Date: DateOnly
      PrevAdjClose: float       // D-1 adj close (= close_1d), the engine's prevClose
      Close3d: float            // D-3 adj close
      Close7d: float            // D-7 adj close (trailing 7-day return feature)
      DayClose: float           // D adj close (price floor + fwd-return base)
      AdjRatio: float           // adj_close / raw_close (rescale minute bars)
      AvgVol20: float           // 20-bar trailing mean daily volume (rvol denom)
      CloseFwd1d: float
      CloseFwd3d: float
      CloseFwd5d: float
      DayOpen: float            // first 09:30 RTH bar's open (== engine session open)
      MedBarVol0945: int64
      NBar0945: int
      Vol0945: int64 }

/// A finished trip. MaxFlyer's gap/premarket/daily-quality fields are gone;
/// the mean-reversion features + forward returns are added.
type Trip =
    { Symbol: string
      TradeDate: DateOnly
      PrevAdjClose: float
      AdjRatio: float
      // intraday at entry
      EntryMin: int
      EntryPrice: float          // = breakout bar close (the fill)
      EntryBarOpen: float        // the entry (breakout) 1m bar's OPEN — for the entry-bar %-change (close/open-1)
      PrevBarClose: float        // the strictly-prior 1m bar's CLOSE — for the flush = close/prevClose-1
      Chg20m: float              // 20-minute %-change into entry (engine LagMa(20), no post-hoc rescan)
      RunLowAtEntry: float       // the session low the breakout cleared (pos.BreakoutRef)
      IntradayAtrPct: float      // 1m log-ATR (VolWindow bars) snapshot at the breakout — how jumpy the name is intraday
      IntradayTightness: float   // 1m tightness snapshot at the breakout
      // RECORDED features (post-hoc slicing, NOT gates)
      Rvol: float                // cumVolToEntry / avgVol20
      BreakoutBarVol: int64      // the breakout bar's own volume
      NewVolHigh: bool           // did the breakout bar make a new session 1m-vol high? (always true at
                                 // vol-high-frac>=1; a relaxed run flags which entries clear the strict gate)
      VolVsHigh: float           // breakout-bar volume / running session 1m-vol high (continuous; >=1 = new high).
                                 // FOR THE RECLAIM: repurposed to the below-VWAP FRACTION at entry.
      RunBelowVwap: int          // consecutive bars EMA<VWAP right before the reclaim cross (0 = breakout engine)
      StopDistPct: float         // stop distance as a fraction of entry = (entry - stopLevel)/entry
      BarRvol15m: float          // breakout_bar_vol / mean(1m vol over [9:30,9:45)) — the breakout-bar
                                 // volume spike relative to the name's own opening-15m 1m tempo. Baseline
                                 // = vol_0945 / nbar_0945 (RTH 09:30-09:45 sum / bar count). Discriminates
                                 // OPPOSITELY by side: extreme spikes (>=40x) = exhaustion blow-off that
                                 // fades on the SHORT (pop-fade PF 1.0->2.0), but falling-knife on the LONG.
      Rvol20m20d: float          // trailing-20m mean 1m volume / (avgvol20/390) — the last 20 minutes' volume
                                 // vs the name's 20-DAY per-minute baseline. "Is the convergence running hot
                                 // vs normal?" (Jeff's rising-volume-into-the-reclaim cue, 20m window not 1 bar).
      Rvol20m15m: float          // trailing-20m mean 1m volume / (vol_0945/nbar_0945) — the last 20 minutes'
                                 // volume vs the OPENING-15m per-minute average. >1 = volume ACCELERATING since
                                 // the open into the reclaim (the acceleration measure, vs the 20d "hot" one).
      RunMaxDist: float          // max (VWAP-EMA)/VWAP over the pre-cross run = the run's DEPTH (how far the
                                 // 9-EMA fell below VWAP). "Are bigger %-runs better trades?"
      RunAtr: float              // mean per-bar log-TR OVER the run bars (reset at each cross) = the run's own vol
      RunDistPerAtr: float       // RunMaxDist / RunAtr = the run's depth in ATR-units (depth normalized by vol)
      RunUpVol: float            // mean 1m vol of above-9EMA bars since the last VWAP cross (accumulation)
      RunDnVol: float            // mean 1m vol of below-9EMA bars since the last VWAP cross (distribution)
      RunUpDnRatio: float        // RunUpVol / RunDnVol — >1 = volume flowing into the RISING side (convergence
                                 // back toward VWAP); <1 = volume on the FALLING side (divergence/selloff)
      // ----- DipRider features (the four handcrafted pullback-continuation features) -----
      BarsSinceHi: int           // # bars since the session PRICE high at the re-break (recency of the 1st push)
      BarsSinceVolHi: int        // # bars since the session max-1m-VOLUME high (recency of peak interest)
      BarsBelowEma: int          // # consecutive bars closed below the 9-EMA before the re-break (pullback depth)
      TrendPct: float            // re-break close / session open - 1 (how far up the session the trend had run)
      // ----- DipRider V2 features (the just-ended above-9EMA run + trailing volumes + VWAP) -----
      RunLen: int                // bars the just-ended above-9EMA run lasted
      RunSlope: float            // OLS slope of that run's log-close (per-bar log-return)
      RunR2: float               // R² of the log-close fit (trend cleanliness)
      RunVolSlope: float         // OLS slope of that run's log-volume (per-bar; is volume rising?)
      RunVolR2: float            // R² of the log-volume fit
      RunAtrV2: float            // mean per-bar log-TR over the just-ended run (its own volatility)
      RunLastClose: float        // close of the last bar of the just-ended run (its top)
      EntryVsRunTop: float       // entryPx / run_last_close - 1 (how far below the run's top we bought; <0 = below)
      RunPctGain: float          // BUY-INTO-RUN: the live run's %gain at entry (entry/floor-1); nan otherwise
      TrailSlope: float          // trailing-20 OLS log-close slope (run-independent)
      TrailVolSlope: float       // trailing-20 OLS log-volume slope (run-independent; pairs with intraday_atr_pct)
      MktChgOpen: float          // SPY %-change from session open at entry (broader-market regime)
      MktChgPrev: float          // SPY %-change from prev daily close at entry
      EntryVsSessHigh: float     // entry / running session high - 1 (<=0; how far below the session high we bought)
      BarsSinceBreak: int        // bars since the above-EMA run broke = the true pullback age (survives blips)
      Vol20: int64               // trailing raw volume over the last 20 bars (exhaustion inputs)
      Vol10: int64
      Vol5: int64
      Vol2: int64
      VwapAtEntry: float         // session VWAP at entry
      EntryVsVwap: float         // entryPx / VWAP - 1 (>0 = bought ABOVE VWAP, <0 = below)
      CumVolToEntry: int64       // cumulative day volume through the entry bar
      PctChgSinceOpen: float     // entryPx / dayOpen - 1
      Close1d: float             // close-1-day-ago (adj) = PrevAdjClose
      Close3d: float             // close-3-days-ago (adj)
      Close7d: float             // close-7-days-ago (adj)
      Chg1d: float               // entryPx / close_1d - 1 — day-scale flush DEPTH at entry (selection filter)
      Chg3d: float               // entryPx / close_3d - 1 — 3-day trend (>= -8% = not a multi-day decliner)
      Chg7d: float               // entryPx / close_7d - 1 — 7-day trend into entry (the run-up feature)
      // exit
      ExitMin: int
      ExitPrice: float           // MOC close (adj scale)
      ExitReason: string         // "moc"
      RetMoc: float              // exitPx / entryPx - 1 (intraday held return)
      // forward daily returns (base = D's daily close; recomputed in analysis)
      DayClose: float
      CloseFwd1d: float
      CloseFwd3d: float
      CloseFwd5d: float
      MedBarVol0945: int64
      Qty: float
      NetPnL: float
      BarsHeld: int }

let private toTrip (c: Candidate) (notional: float) (short: bool) (pos: IntradayPosition) : Trip =
    match pos.State with
    | ExitedAt (exitMin, exitPx, reason) ->
        let qty = notional / pos.EntryPx
        { Symbol = c.Ticker
          TradeDate = c.Date
          PrevAdjClose = c.PrevAdjClose
          AdjRatio = c.AdjRatio
          EntryMin = pos.EntryMin
          EntryPrice = pos.EntryPx
          EntryBarOpen = pos.BreakoutBarOpen
          PrevBarClose = pos.PrevBarClose
          Chg20m = pos.Chg20mAtEntry
          RunLowAtEntry = pos.BreakoutRef
          IntradayAtrPct = pos.AtrPctAtEntry
          IntradayTightness = pos.TightnessAtEntry
          Rvol = (if c.AvgVol20 > 0.0 then float pos.CumVolAtEntry / c.AvgVol20 else nan)
          BreakoutBarVol = pos.BreakoutBarVol
          NewVolHigh = pos.NewVolHigh
          VolVsHigh = pos.VolVsHigh
          RunBelowVwap = pos.RunBelowVwapAtEntry
          StopDistPct = pos.StopDistPct
          // spike vs the [9:30,9:45) 1m baseline = vol_0945/nbar_0945 (mean 1m vol over the window).
          BarRvol15m =
              (let meanBarVol15m = if c.NBar0945 > 0 then float c.Vol0945 / float c.NBar0945 else nan
               if meanBarVol15m > 0.0 then float pos.BreakoutBarVol / meanBarVol15m else nan)
          // trailing-20m mean 1m volume vs (a) the 20-day per-minute baseline and (b) the opening-15m avg.
          Rvol20m20d =
              (let perMin20d = c.AvgVol20 / 390.0   // 390 RTH minutes/day
               if perMin20d > 0.0 && not (Double.IsNaN pos.Vol20mAvgAtEntry) then pos.Vol20mAvgAtEntry / perMin20d else nan)
          Rvol20m15m =
              (let meanBarVol15m = if c.NBar0945 > 0 then float c.Vol0945 / float c.NBar0945 else nan
               if meanBarVol15m > 0.0 && not (Double.IsNaN pos.Vol20mAvgAtEntry) then pos.Vol20mAvgAtEntry / meanBarVol15m else nan)
          RunMaxDist = pos.RunMaxDistAtEntry
          RunAtr = pos.RunAtrAtEntry
          RunDistPerAtr =
              (if pos.RunAtrAtEntry > 0.0 && not (Double.IsNaN pos.RunAtrAtEntry) then pos.RunMaxDistAtEntry / pos.RunAtrAtEntry else nan)
          RunUpVol = pos.RunUpVolAtEntry
          RunDnVol = pos.RunDnVolAtEntry
          RunUpDnRatio =
              (if pos.RunDnVolAtEntry > 0.0 && not (Double.IsNaN pos.RunUpVolAtEntry) then pos.RunUpVolAtEntry / pos.RunDnVolAtEntry else nan)
          BarsSinceHi = pos.BarsSinceHiAtEntry
          BarsSinceVolHi = pos.BarsSinceVolHiAtEntry
          BarsBelowEma = pos.BarsBelowEmaAtEntry
          TrendPct = pos.TrendPctAtEntry
          RunLen = pos.RunLenAtEntry
          RunSlope = pos.RunSlopeAtEntry
          RunR2 = pos.RunR2AtEntry
          RunVolSlope = pos.RunVolSlopeAtEntry
          RunVolR2 = pos.RunVolR2AtEntry
          RunAtrV2 = pos.RunAtrV2AtEntry
          RunLastClose = pos.RunLastCloseAtEntry
          EntryVsRunTop = (if pos.RunLastCloseAtEntry > 0.0 && not (Double.IsNaN pos.RunLastCloseAtEntry) then pos.EntryPx / pos.RunLastCloseAtEntry - 1.0 else nan)
          RunPctGain = pos.RunPctGainAtEntry
          TrailSlope = pos.TrailSlopeAtEntry
          TrailVolSlope = pos.TrailVolSlopeAtEntry
          MktChgOpen = pos.MktChgOpenAtEntry
          MktChgPrev = pos.MktChgPrevAtEntry
          EntryVsSessHigh = pos.EntryVsSessHighAtEntry
          BarsSinceBreak = pos.BarsSinceBreakAtEntry
          Vol20 = pos.Vol20AtEntry
          Vol10 = pos.Vol10AtEntry
          Vol5 = pos.Vol5AtEntry
          Vol2 = pos.Vol2AtEntry
          VwapAtEntry = pos.VwapAtEntry
          EntryVsVwap = (if pos.VwapAtEntry > 0.0 && not (Double.IsNaN pos.VwapAtEntry) then pos.EntryPx / pos.VwapAtEntry - 1.0 else nan)
          CumVolToEntry = pos.CumVolAtEntry
          PctChgSinceOpen = (if c.DayOpen > 0.0 then pos.EntryPx / c.DayOpen - 1.0 else nan)
          Close1d = c.PrevAdjClose
          Close3d = c.Close3d
          Close7d = c.Close7d
          Chg1d = (if c.PrevAdjClose > 0.0 then pos.EntryPx / c.PrevAdjClose - 1.0 else nan)
          Chg3d = (if c.Close3d > 0.0 then pos.EntryPx / c.Close3d - 1.0 else nan)
          Chg7d = (if c.Close7d > 0.0 then pos.EntryPx / c.Close7d - 1.0 else nan)
          ExitMin = exitMin
          ExitPrice = exitPx
          ExitReason = reason
          // long: gain when price rises (exit/entry-1); short: gain when price falls (entry/exit-1).
          RetMoc = (if pos.EntryPx > 0.0 then (if short then pos.EntryPx / exitPx - 1.0 else exitPx / pos.EntryPx - 1.0) else nan)
          DayClose = c.DayClose
          CloseFwd1d = c.CloseFwd1d
          CloseFwd3d = c.CloseFwd3d
          CloseFwd5d = c.CloseFwd5d
          MedBarVol0945 = c.MedBarVol0945
          Qty = qty
          // long: profit when price rises (exit - entry); short: profit when price falls.
          NetPnL = (if short then qty * (pos.EntryPx - exitPx) else qty * (exitPx - pos.EntryPx))
          BarsHeld = exitMin - pos.EntryMin }
    | Holding -> failwith "toTrip called on a still-Holding position (Flatten first)"
    | Armed _ | Skipped -> failwith "toTrip called on an Armed/Skipped (never-filled) position (filter these out)"

// ===========================================================================
// Pipeline 1 — read qualifying (ticker, day) rows from vwap_reclaim_candidate:
// mr_candidate PRE-PRUNED to the in-play universe (ADV = avgvol20*day_close >= $1M AND
// rvol_0945 > 1), built by scripts/equity/build_vwap_reclaim_candidate.fsx. Folding these
// two Layer-1 filters in up front (was post-hoc on the trips CSV) streams ~5x fewer
// ticker-days (161,979 / 850,107 = 19% of mr_candidate).
// ===========================================================================
let private readCandidates (conn: DuckDBConnection) (startDate: DateOnly) (endDate: DateOnly) : Candidate[] =
    // Research override: VWR_CANDIDATE_TABLE lets a breakdown run against a WIDER universe (e.g. a
    // table with the $100M ADV floor dropped) without disturbing the production `vwap_reclaim_candidate`.
    // Value is validated against a fixed allow-list (identifier-only) to keep it injection-safe.
    let table =
        match Environment.GetEnvironmentVariable "VWR_CANDIDATE_TABLE" with
        | null | "" -> "vwap_reclaim_candidate"
        | t when t |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_') -> t
        | bad -> failwithf "Invalid VWR_CANDIDATE_TABLE %A (identifier chars only)" bad
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        $"SELECT ticker, date, prev_adj_close, close_3d, close_7d, day_close, adj_ratio, avgvol20,
                close_fwd_1d, close_fwd_3d, close_fwd_5d, day_open, med_bar_vol_0945, nbar_0945, vol_0945
         FROM {table}
         WHERE date >= $start AND date <= $end
         ORDER BY ticker, date"
    let pStart = cmd.CreateParameter() in pStart.ParameterName <- "start"; pStart.Value <- startDate; cmd.Parameters.Add pStart |> ignore
    let pEnd   = cmd.CreateParameter() in pEnd.ParameterName   <- "end";   pEnd.Value   <- endDate;   cmd.Parameters.Add pEnd   |> ignore
    let out = ResizeArray<Candidate>()
    use reader = cmd.ExecuteReader()
    // helper: a nullable DOUBLE column reads as nan when NULL (e.g. close_fwd_* at the tail).
    let dbl (i: int) = if reader.IsDBNull i then nan else reader.GetDouble i
    while reader.Read() do
        out.Add(
            { Ticker = reader.GetString 0
              Date   = DateOnly.FromDateTime(reader.GetDateTime 1)
              PrevAdjClose = dbl 2
              Close3d = dbl 3
              Close7d = dbl 4
              DayClose = dbl 5
              AdjRatio = dbl 6
              AvgVol20 = dbl 7
              CloseFwd1d = dbl 8
              CloseFwd3d = dbl 9
              CloseFwd5d = dbl 10
              DayOpen = dbl 11
              MedBarVol0945 = reader.GetInt64 12
              NBar0945 = reader.GetInt32 13
              Vol0945 = reader.GetInt64 14 })
    out.ToArray()

// ===========================================================================
// Pipeline 2 (intraday) — MinuteEmitter -> IntradaySystem -> trip array.
// Copied from MaxFlyer: one MinuteEmitter per date (parquet opened once), one
// IntradaySystem per ticker, drain-on-boundary + Flatten. Only the candidate
// source and toTrip differ.
// ===========================================================================
type MinuteEmitter
        ( conn: DuckDBConnection, path: string,
          tickers: string[], adjRatio: IDictionary<string, float>,
          sessionStartMin: int, mocMin: int ) =

    member val Conn = conn
    member val AdjRatio = adjRatio

    member val Sql =
        let tickerList = tickers |> Array.map (fun t -> "'" + t.Replace("'", "''") + "'") |> String.concat ","
        sprintf """
        WITH bars AS (
            SELECT ticker,
                CAST(date_part('hour', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT)*60
                  + CAST(date_part('minute', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT) AS et_min,
                open, high, low, close, volume
            FROM read_parquet('%s') WHERE close > 0 AND ticker IN (%s))
        SELECT ticker, et_min, open, high, low, close, volume
        FROM bars
        WHERE et_min >= %d AND et_min <= %d
        ORDER BY ticker, et_min"""
            (path.Replace("'", "''")) tickerList sessionStartMin mocMin

    /// Stream every candidate-ticker minute bar for this date, split-adjusted, in
    /// (ticker, et_min) order. `inline` so onNext fuses into the read loop.
    member inline this.Process(onNext: string * MinuteBar -> unit) =
        use cmd = this.Conn.CreateCommand()
        cmd.CommandText <- this.Sql
        use reader = cmd.ExecuteReader()
        while reader.Read() do
            let ticker = reader.GetString 0
            let r = this.AdjRatio.[ticker]
            let bar : MinuteBar =
                { etMin    = reader.GetInt32 1
                  ``open`` = reader.GetDouble 2 * r
                  high     = reader.GetDouble 3 * r
                  low      = reader.GetDouble 4 * r
                  close    = reader.GetDouble 5 * r
                  volume   = reader.GetInt64 6 }
            onNext (ticker, bar)

/// Build the per-date MARKET-CONTEXT lookup (broader-market regime, shared by all tickers). Reads the
/// reference index's (SPY) 1m RTH bars from the same date parquet, plus its prev daily close, and returns
/// et_min -> struct(chg-from-open, chg-from-prev-close) as CUMULATIVE %-changes at that minute. No-lookahead
/// by construction (the value at et_min uses only SPY bars through et_min). Returns the (nan,nan) no-op if
/// SPY is missing that day. `idx` defaults to "SPY".
let private buildMarketCtx (conn: DuckDBConnection) (path: string) (date: DateOnly)
                           (mocMin: int) : int -> struct (float * float) =
    let idx = "SPY"
    // prev daily close from the RAW daily table (avoid the split_adjusted dividend bug). ValueNone if absent.
    let prevClose =
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT close FROM daily_prices WHERE ticker = $t AND date < $d ORDER BY date DESC LIMIT 1"
        let pt = cmd.CreateParameter() in pt.ParameterName <- "t"; pt.Value <- idx; cmd.Parameters.Add pt |> ignore
        let pd = cmd.CreateParameter() in pd.ParameterName <- "d"; pd.Value <- date; cmd.Parameters.Add pd |> ignore
        match cmd.ExecuteScalar() with
        | null -> ValueNone
        | :? System.DBNull -> ValueNone
        | v -> ValueSome (System.Convert.ToDouble v)
    // SPY 1m RTH closes, in et_min order, for this date.
    let bars =
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            sprintf """
            WITH b AS (
                SELECT CAST(date_part('hour', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT)*60
                     + CAST(date_part('minute', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT) AS et_min,
                     open, close
                FROM read_parquet('%s') WHERE ticker = 'SPY' AND close > 0)
            SELECT et_min, open, close FROM b WHERE et_min >= 570 AND et_min <= %d ORDER BY et_min"""
                (path.Replace("'", "''")) mocMin
        use reader = cmd.ExecuteReader()
        let acc = ResizeArray<struct (int * float * float)>()
        while reader.Read() do acc.Add(struct (reader.GetInt32 0, reader.GetDouble 1, reader.GetDouble 2))
        acc
    if bars.Count = 0 then (fun _ -> struct (nan, nan))
    else
        // session open = the first RTH (>=09:30) bar's OPEN.
        let struct (_, sessOpen, _) = bars.[0]
        // map et_min -> cumulative (chgOpen, chgPrev) using SPY's close at/through that minute. We fill a
        // dense map by carrying the last-seen close forward across gaps (a minute with no SPY bar reads the
        // prior minute's value).
        let m = System.Collections.Generic.Dictionary<int, struct (float * float)>()
        let mutable lastClose = sessOpen   // forward-filled across minutes with no SPY bar
        let mutable bi = 0
        for etMin in 570 .. mocMin do
            while bi < bars.Count && (let struct (e,_,_) = bars.[bi] in e) <= etMin do
                let struct (_,_,c) = bars.[bi] in lastClose <- c
                bi <- bi + 1
            let chgOpen = if sessOpen > 0.0 then lastClose / sessOpen - 1.0 else nan
            let chgPrev = match prevClose with ValueSome pc when pc > 0.0 -> lastClose / pc - 1.0 | _ -> nan
            m.[etMin] <- struct (chgOpen, chgPrev)
        (fun etMin -> match m.TryGetValue etMin with | true, v -> v | _ -> struct (nan, nan))

let collectTrips (conn: DuckDBConnection) (cfg: Config) (minuteDir: string)
                 (candidates: Candidate[]) : Trip[] =
    let trips = ResizeArray<Trip>()

    let drain (c: Candidate) (sys: IntradaySystem) =
        sys.Flatten()
        for pos in sys.Positions do
            match pos.State with
            | ExitedAt _ -> trips.Add(toTrip c cfg.Notional cfg.Intraday.Short pos)
            | Armed _ | Skipped -> ()   // never filled → no trip (Armed/RiseEntry off here anyway)
            | Holding -> failwith "Flatten closes all; unreachable"

    for date, cands in candidates |> Array.groupBy (fun c -> c.Date) do
        let path = IO.Path.Combine(minuteDir, sprintf "%s.parquet" (date.ToString("yyyy-MM-dd")))
        if IO.File.Exists path then
            let byTicker = cands |> Array.map (fun c -> c.Ticker, c) |> dict
            let adjRatio = cands |> Array.map (fun c -> c.Ticker, c.AdjRatio) |> dict
            let emitter = MinuteEmitter(conn, path, Array.map (fun (c: Candidate) -> c.Ticker) cands,
                                        adjRatio, cfg.Intraday.SessionStartMin, cfg.Intraday.MocMin)
            // broader-market (SPY) context for this date — built once, shared by every ticker's engine.
            let mktCtx = buildMarketCtx conn path date cfg.Intraday.MocMin
            let mutable cur : (Candidate * IntradaySystem) option = None
            emitter.Process(fun (ticker, bar) ->
                match cur with
                | Some(c, sys) when c.Ticker = ticker -> sys.Process bar
                | _ ->
                    match cur with
                    | Some(pc, psys) -> drain pc psys
                    | None -> ()
                    let c = byTicker.[ticker]
                    let sys = IntradaySystem(cfg.Intraday, ticker, date, c.PrevAdjClose)
                    sys.SetMarketCtx mktCtx
                    // per-minute volume baselines for the exhaustion exit: 20d-avg/390 and opening-15m avg 1m vol.
                    let perMin20d = if c.AvgVol20 > 0.0 then c.AvgVol20 / 390.0 else 0.0
                    let perMin15m = if c.NBar0945 > 0 then float c.Vol0945 / float c.NBar0945 else 0.0
                    sys.SetVolBaselines(perMin20d, perMin15m)
                    sys.Process bar
                    cur <- Some(c, sys))
            match cur with
            | Some(c, sys) -> drain c sys
            | None -> ()

    trips.ToArray()

/// Run the whole LowFlyer backtest: read mr_candidate (pipeline 1), then the
/// intraday breakout engine per candidate day (pipeline 2, grouped by date so
/// each minute_aggs parquet opens at most once). Returns (trips, candidateCount).
let run (dbPath: string) (minuteDir: string) (cfg: Config)
        (startDate: DateOnly) (endDate: DateOnly) : Trip[] * int =
    let connStr = $"Data Source={dbPath};ACCESS_MODE=READ_ONLY"
    use conn = new DuckDBConnection(connStr)
    conn.Open()
    do
        use pragma = conn.CreateCommand()
        pragma.CommandText <- "PRAGMA memory_limit='6GB'"
        pragma.ExecuteNonQuery() |> ignore

    let candidates = readCandidates conn startDate endDate
    let trips = collectTrips conn cfg minuteDir candidates
    trips, candidates.Length

// ---------------------------------------------------------------------------
// CSV emission
// ---------------------------------------------------------------------------

let private inv = CultureInfo.InvariantCulture
let private fmt (x: float) = if Double.IsNaN x then "nan" else x.ToString("0.################", inv)
let private hhmm (m: int) = sprintf "%02d:%02d" (m / 60) (m % 60)

let header =
    "symbol,trade_date,prev_adj_close,adj_ratio,"
    + "entry_time,entry_price,entry_bar_open,prev_bar_close,chg_20m,run_low_at_entry,intraday_atr_pct_at_entry,intraday_tightness_at_entry,"
    + "rvol,breakout_bar_vol,new_vol_high,vol_vs_high,run_below_vwap,stop_dist_pct,bar_rvol_15m,rvol20m_20d,rvol20m_15m,run_max_dist,run_atr,run_dist_per_atr,run_up_vol,run_dn_vol,run_updn_ratio,bars_since_hi,bars_since_vol_hi,bars_below_ema,trend_pct,run_len,run_slope,run_r2,run_vol_slope,run_vol_r2,run_atr_v2,run_last_close,entry_vs_run_top,run_pct_gain,trail_slope,trail_vol_slope,mkt_chg_open,mkt_chg_prev,entry_vs_sess_high,bars_since_break,vol_20,vol_10,vol_5,vol_2,vwap_at_entry,entry_vs_vwap,cum_vol_to_entry,pct_chg_since_open,close_1d,close_3d,close_7d,chg_1d,chg_3d,chg_7d,"
    + "exit_time,exit_price,exit_reason,ret_moc,"
    + "day_close,close_fwd_1d,close_fwd_3d,close_fwd_5d,med_bar_vol_0945,"
    + "qty,net_pnl,bars_held_min"

let private row (t: Trip) : string =
    String.concat "," [
        t.Symbol
        t.TradeDate.ToString("yyyy-MM-dd")
        fmt t.PrevAdjClose
        fmt t.AdjRatio
        hhmm t.EntryMin
        fmt t.EntryPrice
        fmt t.EntryBarOpen
        fmt t.PrevBarClose
        fmt t.Chg20m
        fmt t.RunLowAtEntry
        fmt t.IntradayAtrPct
        fmt t.IntradayTightness
        fmt t.Rvol
        string t.BreakoutBarVol
        (if t.NewVolHigh then "1" else "0")
        fmt t.VolVsHigh
        string t.RunBelowVwap
        fmt t.StopDistPct
        fmt t.BarRvol15m
        fmt t.Rvol20m20d
        fmt t.Rvol20m15m
        fmt t.RunMaxDist
        fmt t.RunAtr
        fmt t.RunDistPerAtr
        fmt t.RunUpVol
        fmt t.RunDnVol
        fmt t.RunUpDnRatio
        string t.BarsSinceHi
        string t.BarsSinceVolHi
        string t.BarsBelowEma
        fmt t.TrendPct
        string t.RunLen
        fmt t.RunSlope
        fmt t.RunR2
        fmt t.RunVolSlope
        fmt t.RunVolR2
        fmt t.RunAtrV2
        fmt t.RunLastClose
        fmt t.EntryVsRunTop
        fmt t.RunPctGain
        fmt t.TrailSlope
        fmt t.TrailVolSlope
        fmt t.MktChgOpen
        fmt t.MktChgPrev
        fmt t.EntryVsSessHigh
        string t.BarsSinceBreak
        string t.Vol20
        string t.Vol10
        string t.Vol5
        string t.Vol2
        fmt t.VwapAtEntry
        fmt t.EntryVsVwap
        string t.CumVolToEntry
        fmt t.PctChgSinceOpen
        fmt t.Close1d
        fmt t.Close3d
        fmt t.Close7d
        fmt t.Chg1d
        fmt t.Chg3d
        fmt t.Chg7d
        hhmm t.ExitMin
        fmt t.ExitPrice
        t.ExitReason
        fmt t.RetMoc
        fmt t.DayClose
        fmt t.CloseFwd1d
        fmt t.CloseFwd3d
        fmt t.CloseFwd5d
        string t.MedBarVol0945
        fmt t.Qty
        fmt t.NetPnL
        string t.BarsHeld
    ]

let writeCsv (path: string) (trips: Trip[]) =
    use w = new IO.StreamWriter(path)
    w.WriteLine header
    for t in trips do w.WriteLine(row t)
