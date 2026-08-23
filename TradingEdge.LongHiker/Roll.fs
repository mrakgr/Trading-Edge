module TradingEdge.LongHiker.Roll

// The bar side of QUEUE SHARING: one ring of 32-byte RollBar feeds every
// bar-level sum and OLS in the engine, each window evicting its own tail out of
// the shared ring instead of keeping a private Queue.
//
// ⚠ This file is a deliberate TWIN of TradingEdge.Scanner/Engine/Roll.fs (the
// live fork's copy). The generic primitives — IRoll, WindowRoller, SumRoll,
// OlsRoll — live once, in TradingEdge.RollingMa; only the projections are
// per-engine, and LongHiker's set is not FlushFader's (no dv2/dlv/dlv2 z-score
// moments, which nothing here reads). Keep them separate rather than
// generalising: a shared projection list is a shared blast radius across two
// systems that have no parity obligation to each other.
//
// ⚠ max/min stay on MaxMa/MinMa: a monotonic deque cannot Remove an arbitrary
// value, so those windows are not shareable. Sums and OLS are.

open TradingEdge.RollingMa

/// The ring element — a 32-byte STRUCT, deliberately not SecBar.
///
/// ⭐ TWO REASONS it is not the bar record. (1) A ring of 1200 SecBar
/// REFERENCES keeps 1200 short-lived records alive per engine, promoting to
/// gen2 what would otherwise die in gen0; a struct ring is a flat array and
/// retains nothing. (2) `log vwap` is stored, not recomputed: the ring's
/// projections run on every Add AND every Remove, so a recomputing log
/// projection would roughly double the engine's log() count.
[<Struct>]
type RollBar =
    { Vwap: float
      LogVwap: float
      Vol: float
      Tc: float }

let inline rollBar (vwap: float) (volume: float) (tradeCount: int) =
    { Vwap = vwap; LogVwap = log vwap; Vol = volume; Tc = float tradeCount }

// --- the projections this engine folds over the 1s tape -------------------
let inline pVol   (b: RollBar) = b.Vol
let inline pTc    (b: RollBar) = b.Tc
let inline pDv    (b: RollBar) = b.Vwap * b.Vol
/// OLS is fed LOG price so the slope is %-per-bar and comparable across tickers.
let inline pLogPx (b: RollBar) = b.LogVwap
/// ⭐ The VOLUME trend's y (user, 2026-08-23). LOG volume for the same reason
/// price is logged: a raw-volume slope is shares-per-bar-per-bar and is not
/// comparable between a $2 name printing 50k lots and a $200 one printing 200s,
/// so it could never be banded across the universe. ln makes it a GROWTH RATE.
/// ⚠ Defined because the slim emitter admits only `volume > 0` bars — there is
/// no ln(0) to guard, and adding an epsilon would put a floor in the feature.
let inline pLogVol (b: RollBar) = log b.Vol

/// Group aggregates by window size so the ring is walked once per size rather
/// than once per aggregate.
let roller (items: (int * IRoll<RollBar>)[]) =
    items
    |> Array.groupBy fst
    |> Array.map (fun (w, xs) -> w, xs |> Array.map snd)
    |> WindowRoller<RollBar>

// --- the specialised windows ---------------------------------------------
//
// ⭐ ONE SEALED SUBCLASS PER PROJECTION. `SumRoll.Project` is abstract, so
// overriding it here removes the closure dereference the ring would otherwise
// pay on every Add AND every Remove — measured as the difference between queue
// sharing being a WIN and being a LOSS (docs/queue_sharing.md).
[<Sealed>] type VolSum (w) = inherit SumRoll<RollBar>(w)
                             override _.Project b = pVol b
[<Sealed>] type TcSum  (w) = inherit SumRoll<RollBar>(w)
                             override _.Project b = pTc b
[<Sealed>] type DvSum  (w) = inherit SumRoll<RollBar>(w)
                             override _.Project b = pDv b

/// ln(vwap) regressed on the bar index — the price trend.
[<Sealed>] type LogPxOls (w) = inherit OlsRoll<RollBar>(w)
                               override _.Project b = pLogPx b
/// ln(volume) regressed on the bar index — the PARTICIPATION trend, the same
/// shape of statement about the tape's activity that LogPxOls makes about its
/// price. ⚠ log is recomputed per Add/Remove here (RollBar caches only the price
/// one); that is 2 extra logs per registered volume window per bar and was the
/// cheaper trade against widening the ring element for every engine that uses it.
[<Sealed>] type LogVolOls(w) = inherit OlsRoll<RollBar>(w)
                               override _.Project b = pLogVol b
