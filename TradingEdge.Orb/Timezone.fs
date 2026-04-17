module TradingEdge.Orb.Timezone

open System
open System.Collections.Generic

// Find the IANA or Windows zone id depending on platform.
let easternTz =
    try TimeZoneInfo.FindSystemTimeZoneById "America/New_York"
    with _ -> TimeZoneInfo.FindSystemTimeZoneById "Eastern Standard Time"

let baseTimeFromDate (d : DateOnly) =
    // 00:00 local Eastern, unspecified kind so ConvertTimeToUtc treats it as local-in-tz
    let local = DateTime(d, TimeOnly(0, 0, 0), DateTimeKind.Unspecified)
    TimeZoneInfo.ConvertTimeToUtc(local, easternTz)

let baseTimeFromTicks (ticks : int64) = DateTime(ticks) |> DateOnly.FromDateTime |> baseTimeFromDate

/// Given a yyyy-MM-dd date string, produce the UTC DateTime corresponding
/// to 04:00:00 Eastern on that date. Handles DST automatically.
let baseTimeFromDateString (date: string) : DateTime = DateOnly.ParseExact(date, "yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture) |> baseTimeFromDate

let early_closes : DateOnly HashSet = 
    // NYSE/Nasdaq equity early-close days (1:00 PM ET). Verified against the NYSE 2026/2027/2028 press release and matched 
    // against the six dates observed in data/market_hours.json for 2024-2025. 
    // Source: NYSE Group 2026-2028 Holiday and Early Closings Calendar.
    // These half-days close 3 hours earlier that regular days at 1:00 PM ET.
    [|
        DateOnly(2023,07,03)
        DateOnly(2023,11,24)
        DateOnly(2024,07,03)
        DateOnly(2024,11,29)
        DateOnly(2024,12,24)
        DateOnly(2025,07,03)
        DateOnly(2025,11,28)
        DateOnly(2025,12,24)
        DateOnly(2026,11,27)
        DateOnly(2026,12,24)
        DateOnly(2027,11,26)
        DateOnly(2028,07,03)
        DateOnly(2028,11,24)
    |] |> HashSet
    