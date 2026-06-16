module TradingEdge.MomentumBacktest.Types

open System
open System.Collections.Generic

/// Momentum-structure lookback periods, in trading days. Single source of truth:
/// drives the generated SQL columns, the per-row Levels dictionary, the trip
/// EntryLevels, and the CSV header. Each period gets 6 raw levels (see levelKinds).
/// Single-day periods (1..5) produce degenerate hi=lo=close=ma; that's intentional
/// (uniform loop, no special cases). 2w=10, 4w=20, 8w=40, 13w=63, 26w=126, 52w=252.
let structurePeriods : (string * int) list =
    [ "52w", 252; "26w", 126; "13w", 63; "8w", 40; "4w", 20; "2w", 10
      "5d", 5; "4d", 4; "3d", 3; "2d", 2; "1d", 1 ]

/// The 6 raw levels stored per period. Distances/moves are derived in SQL post-hoc
/// (entry_close / level - 1), never stored. Column name = "{kind}_{periodLabel}".
///   trail   = close N bars ago (LAG, window-start anchor for the trailing move)
///   ma      = AVG(adj_close) over the window
///   hi      = MAX(adj_high)  (high-channel top)      lo = MIN(adj_low)  (bottom)
///   hiclose = MAX(adj_close) (close-channel top)  loclose = MIN(adj_close)(bottom)
let levelKinds : string list = [ "trail"; "ma"; "hi"; "lo"; "hiclose"; "loclose" ]

/// All structure column names, in a stable order (kind-major, period order from
/// structurePeriods). Used by the SQL SELECT, the reader, and the CSV.
let structureColumns : string list =
    [ for kind in levelKinds do
        for (label, _) in structurePeriods do
            yield sprintf "%s_%s" kind label ]

/// One row per (ticker, date) returned by the signal SQL — the full per-day
/// split-adjusted series plus the precomputed indicator columns. The stop-walk
/// needs every day between entry and exit, not just entries, so this carries the
/// whole series; `is_entry` marks the days that open a trip.
///
/// [<CLIMutable>] so Dapper can hydrate it; field names match the SQL column
/// aliases exactly (Dapper is case-insensitive but we keep them identical).
[<CLIMutable>]
type SignalRow = {
    ticker: string
    date: DateOnly
    adj_open: float
    adj_high: float
    adj_low: float
    adj_close: float
    adj_volume: int64
    prev_adj_close: Nullable<float>
    avg_volume_4w: Nullable<float>
    avg_dollar_volume_4w: Nullable<float>
    prior_idx: int64
    hi_252_prior: Nullable<float>
    low_15_prior: Nullable<float>
    atr_pct_14: Nullable<float>
    range_pct_14: Nullable<float>
    tightness_14: Nullable<float>
    pct_up: Nullable<float>
    rvol: Nullable<float>
    is_entry: bool
    /// Raw structure levels at this bar, keyed by structureColumns name (e.g.
    /// "hiclose_52w"). NaN where the window has insufficient history. Populated
    /// by the manual reader in Signals.loadTicker (not Dapper-mapped).
    levels: IReadOnlyDictionary<string, float>
}

/// A completed (or still-open) round trip. Long-only for v0. Open trips are
/// marked-to-market at the final available adj_close and flagged Open=true.
type Trip = {
    Symbol: string
    EntryDate: DateOnly
    ExitDate: DateOnly
    EntryPrice: float
    ExitPrice: float
    Qty: float
    NetPnL: float
    BarsHeld: int
    // entry-context captured at the entry bar T
    EntryAdjVolume: int64
    RvolAtEntry: float
    AvgDollarVolume4wAtEntry: float
    PctUpAtEntry: float
    AtrPct14AtEntry: float
    RangePct14AtEntry: float
    Tightness14AtEntry: float
    ExitReason: string        // "stop" (15-day-low), "expansion" (tightness>thr), "mtm" (open at end)
    Open: bool
    /// Raw structure levels at the entry bar (keyed by structureColumns name),
    /// carried through to the trips CSV for post-hoc distance/momentum breakdowns.
    EntryLevels: IReadOnlyDictionary<string, float>
}

/// Resolved run configuration (all Argu flags folded to concrete values).
type Config = {
    DbPath: string
    StartDate: DateOnly
    EndDate: DateOnly
    Notional: float
    UpThreshold: float
    RvolThreshold: float
    LookbackHigh: int
    StopLowWindow: int
    MinPriorDays: int
    MinAvgDollarVolume: float
    TradableOnly: bool        // true = CS/ADRC only
    /// 52-week-high proximity gate (None = no gate). Require the entry bar's close
    /// to be at least this fraction of the prior 252-day high CLOSE (hi_252_prior):
    ///   1.0  = strict new-52w-high gate (the original default),
    ///   0.85 = "within 15% of the 52w high" band (the study filter, now in-engine),
    ///   None = no gate (full breakout range; replaces the old --no-52w-high).
    /// Replaces the former binary NoFiftyTwoWeekHigh.
    MinPctOf52wHigh: float option
    /// Entry filters (None = off). Require the entry bar's tightness <= MaxTightness
    /// and ATR% <= MaxAtrPct. (The standalone analysis used 0.40 and 0.08.)
    MaxTightnessAtEntry: float option
    MaxAtrPctAtEntry: float option
    /// Volatility-expansion exit: close the trip when the held-day rolling
    /// tightness = range/(14*ATR) rises ABOVE this threshold (exit next open).
    /// None = disabled (15-day-low stop only).
    ExpansionExitThreshold: float option
    /// Time stop: if no other exit has fired within N held bars, exit at bar T+N
    /// (next open). None = disabled.
    TimeStopBars: int option
    /// Stall exit: exit if K consecutive held bars pass without a new
    /// since-entry-high CLOSE. None = disabled.
    StallBars: int option
    /// Breakeven-after-N: at bar t+N, if the trade is in profit, raise the stop
    /// floor to the entry price (stop = max(15-day-low, entry) thereafter); if it
    /// is NOT in profit at t+N, exit (laggard time-stop). None = disabled.
    BreakevenAfter: int option
    /// Disable the price stop entirely (no 15-day-low / day-low / breakeven floor);
    /// exits are then only the time stop, stall, and expansion exit. Variant 1.
    NoPriceStop: bool
    /// Floor the stop at the ENTRY-DAY low (Qullamaggie initial stop) until the
    /// trailing 15-day-low rises above it. Variant 2.
    InitialStopDayLow: bool
    /// Skip the structure_levels JOIN and the 66-column marshalling entirely. The
    /// per-ticker query then only computes the (near-free) core indicator windows,
    /// taking a full run from ~12 min to seconds. Use for backtests that don't need
    /// the structure columns (e.g. the breadth×RVOL grid, which uses only core
    /// indicators). Trips CSV structure fields are left empty. Default off.
    NoStructure: bool
    TripsCsv: string
    BreakdownLog: string
}
