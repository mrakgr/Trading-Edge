module TradingEdge.ReplaySimulatorV3.Time

// Shared NY-time helpers. All modules in V3 work in UTC nanoseconds since
// epoch internally; this module is the single place where we convert into
// America/New_York wall-clock.

open System

let NY_TZ =
    try TimeZoneInfo.FindSystemTimeZoneById("America/New_York")
    with _ -> TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")

/// Convert UTC ns since epoch into a NY-local DateTime.
let toNy (utcNs: int64) : DateTime =
    let utc = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(utcNs / 100L)
    TimeZoneInfo.ConvertTimeFromUtc(utc, NY_TZ)
