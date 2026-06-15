module TradingEdge.MomentumBacktest.Types

open System

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
    /// Volatility-expansion exit: close the trip when the held-day rolling
    /// tightness = range/(14*ATR) rises ABOVE this threshold (exit next open).
    /// None = disabled (15-day-low stop only).
    ExpansionExitThreshold: float option
    TripsCsv: string
    BreakdownLog: string
}
