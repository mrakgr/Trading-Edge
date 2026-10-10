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
    
do if DateOnly.FromDateTime DateTime.Today > DateOnly(2028,11,24) then failwith "Early closes must be updated."

let holidays : DateOnly HashSet =
    // NYSE/Nasdaq equity FULL CLOSURES on weekdays (user 2026-10-10: the live Oms must not run on one, and the day after
    // one must not wait for its daily data). 2016 – 2026-09-28: the weekdays with no daily data (data/v2 daily_prices),
    // which are exactly the NYSE holidays plus the two national days of mourning (2018-12-05 Bush, 2025-01-09 Carter);
    // Juneteenth from 2022; no holiday is observed when New Year's Day falls on a Saturday (2022, 2028). 2026-11-26 on:
    // the NYSE Group 2026-2028 Holiday and Early Closings Calendar (Business Wire, 2025-12-23), checked against Massive's
    // /v1/marketstatus/upcoming (through 2027-09-06) on 2026-10-10.
    [|
        DateOnly(2016,01,01); DateOnly(2016,01,18); DateOnly(2016,02,15); DateOnly(2016,03,25); DateOnly(2016,05,30); DateOnly(2016,07,04); DateOnly(2016,09,05); DateOnly(2016,11,24); DateOnly(2016,12,26)
        DateOnly(2017,01,02); DateOnly(2017,01,16); DateOnly(2017,02,20); DateOnly(2017,04,14); DateOnly(2017,05,29); DateOnly(2017,07,04); DateOnly(2017,09,04); DateOnly(2017,11,23); DateOnly(2017,12,25)
        DateOnly(2018,01,01); DateOnly(2018,01,15); DateOnly(2018,02,19); DateOnly(2018,03,30); DateOnly(2018,05,28); DateOnly(2018,07,04); DateOnly(2018,09,03); DateOnly(2018,11,22); DateOnly(2018,12,05); DateOnly(2018,12,25)
        DateOnly(2019,01,01); DateOnly(2019,01,21); DateOnly(2019,02,18); DateOnly(2019,04,19); DateOnly(2019,05,27); DateOnly(2019,07,04); DateOnly(2019,09,02); DateOnly(2019,11,28); DateOnly(2019,12,25)
        DateOnly(2020,01,01); DateOnly(2020,01,20); DateOnly(2020,02,17); DateOnly(2020,04,10); DateOnly(2020,05,25); DateOnly(2020,07,03); DateOnly(2020,09,07); DateOnly(2020,11,26); DateOnly(2020,12,25)
        DateOnly(2021,01,01); DateOnly(2021,01,18); DateOnly(2021,02,15); DateOnly(2021,04,02); DateOnly(2021,05,31); DateOnly(2021,07,05); DateOnly(2021,09,06); DateOnly(2021,11,25); DateOnly(2021,12,24)
        DateOnly(2022,01,17); DateOnly(2022,02,21); DateOnly(2022,04,15); DateOnly(2022,05,30); DateOnly(2022,06,20); DateOnly(2022,07,04); DateOnly(2022,09,05); DateOnly(2022,11,24); DateOnly(2022,12,26)
        DateOnly(2023,01,02); DateOnly(2023,01,16); DateOnly(2023,02,20); DateOnly(2023,04,07); DateOnly(2023,05,29); DateOnly(2023,06,19); DateOnly(2023,07,04); DateOnly(2023,09,04); DateOnly(2023,11,23); DateOnly(2023,12,25)
        DateOnly(2024,01,01); DateOnly(2024,01,15); DateOnly(2024,02,19); DateOnly(2024,03,29); DateOnly(2024,05,27); DateOnly(2024,06,19); DateOnly(2024,07,04); DateOnly(2024,09,02); DateOnly(2024,11,28); DateOnly(2024,12,25)
        DateOnly(2025,01,01); DateOnly(2025,01,09); DateOnly(2025,01,20); DateOnly(2025,02,17); DateOnly(2025,04,18); DateOnly(2025,05,26); DateOnly(2025,06,19); DateOnly(2025,07,04); DateOnly(2025,09,01); DateOnly(2025,11,27); DateOnly(2025,12,25)
        DateOnly(2026,01,01); DateOnly(2026,01,19); DateOnly(2026,02,16); DateOnly(2026,04,03); DateOnly(2026,05,25); DateOnly(2026,06,19); DateOnly(2026,07,03); DateOnly(2026,09,07); DateOnly(2026,11,26); DateOnly(2026,12,25)
        DateOnly(2027,01,01); DateOnly(2027,01,18); DateOnly(2027,02,15); DateOnly(2027,03,26); DateOnly(2027,05,31); DateOnly(2027,06,18); DateOnly(2027,07,05); DateOnly(2027,09,06); DateOnly(2027,11,25); DateOnly(2027,12,24)
        DateOnly(2028,01,17); DateOnly(2028,02,21); DateOnly(2028,04,14); DateOnly(2028,05,29); DateOnly(2028,06,19); DateOnly(2028,07,04); DateOnly(2028,09,04); DateOnly(2028,11,23); DateOnly(2028,12,25)
    |] |> HashSet

do if DateOnly.FromDateTime DateTime.Today > DateOnly(2028,12,25) then failwith "Holidays must be updated."

/// A day the equity market trades: a weekday, not a holiday (an early close is a trading day)
let isTradingDay (d: DateOnly) =
    d.DayOfWeek <> DayOfWeek.Saturday && d.DayOfWeek <> DayOfWeek.Sunday && not (holidays.Contains d)

/// The last trading day before `d`
let previousTradingDay (d: DateOnly) =
    let mutable p = d.AddDays -1
    while not (isTradingDay p) do p <- p.AddDays -1
    p