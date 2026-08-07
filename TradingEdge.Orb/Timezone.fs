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

/// Offset in hours from baseTimeFromDate (midnight ET) to the start of the
/// premarket session window (08:30 ET). Used by the 10s bar builder and the
/// exporter to compute the UTC ns of bucket 0.
let startHoursFromBase = 8.5

/// Given a yyyy-MM-dd date string, produce the UTC DateTime corresponding
/// to 00:00:00 Eastern on that date. Handles DST automatically.
let baseTimeFromDateString (date: string) : DateTime = DateOnly.ParseExact(date, "yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture) |> baseTimeFromDate

let early_closes : DateOnly HashSet =
    // NYSE/Nasdaq equity early-close days (1:00 PM ET). Verified against the NYSE 2026/2027/2028 press release and matched
    // against the six dates observed in data/market_hours.json for 2024-2025.
    // Source: NYSE Group 2026-2028 Holiday and Early Closings Calendar.
    // These half-days close 3 hours earlier that regular days at 1:00 PM ET.
    // 2016-2022 backfilled 2026-08-07 (user catch — the list started at 2023-07-03,
    // so FOUR in-sample days were built with a 15:59 cutoff and carried after-hours
    // prints as RTH bars: 2020-11-27, 2020-12-24, 2021-11-26, 2022-11-25; rebuilt).
    // Rules: Jul 3 only when Jul 4 falls Tue-Fri; the Friday after Thanksgiving
    // always; Dec 24 only when it is a weekday and not itself the observed holiday.
    [|
        DateOnly(2016,11,25)
        DateOnly(2017,07,03)
        DateOnly(2017,11,24)
        DateOnly(2018,07,03)
        DateOnly(2018,11,23)
        DateOnly(2018,12,24)
        DateOnly(2019,07,03)
        DateOnly(2019,11,29)
        DateOnly(2019,12,24)
        DateOnly(2020,11,27)
        DateOnly(2020,12,24)
        DateOnly(2021,11,26)
        DateOnly(2022,11,25)
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
    