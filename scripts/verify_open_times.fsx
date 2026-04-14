open System
open System.IO
open System.Text.Json

// Find the IANA or Windows zone id depending on platform.
let easternTz =
    try TimeZoneInfo.FindSystemTimeZoneById("America/New_York")
    with _ -> TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")

/// Given a yyyy-MM-dd date string, produce the UTC DateTime corresponding
/// to 09:30:00 Eastern on that date. Handles DST automatically.
let nineThirtyEtInUtc (date: string) : DateTime =
    let d = DateTime.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
    // 09:30 local Eastern, unspecified kind so ConvertTimeToUtc treats it as local-in-tz
    let local = DateTime(d.Year, d.Month, d.Day, 9, 30, 0, DateTimeKind.Unspecified)
    TimeZoneInfo.ConvertTimeToUtc(local, easternTz)

// ---------------------------------------------------------------------------
// Verify against data/market_hours.json
// ---------------------------------------------------------------------------

let mhPath = "data/market_hours.json"
use fs = File.OpenRead mhPath
use doc = JsonDocument.Parse(fs)

let mutable nChecked = 0
let mutable nWithin30 = 0
let mutable maxDeltaMin = 0.0
let worstCases = ResizeArray<string * string * float>()

for elem in doc.RootElement.EnumerateArray() do
    let ticker = elem.GetProperty("ticker").GetString()
    let date   = elem.GetProperty("date").GetString()
    let ooProp = elem.GetProperty("officialOpen")
    if ooProp.ValueKind <> JsonValueKind.Null then
        let oo = ooProp.GetString() |> DateTime.Parse
        let oo = oo.ToUniversalTime()
        let expected = nineThirtyEtInUtc date
        let deltaMin = (oo - expected).TotalMinutes
        nChecked <- nChecked + 1
        if abs deltaMin <= 30.0 then
            nWithin30 <- nWithin30 + 1
        if abs deltaMin > abs maxDeltaMin then
            maxDeltaMin <- deltaMin
        if abs deltaMin > 5.0 then
            worstCases.Add((ticker, date, deltaMin))

printfn "Checked %d entries with non-null officialOpen." nChecked
printfn "Within 30 min of 09:30 ET: %d  (%.2f%%)" nWithin30 (100.0 * float nWithin30 / float nChecked)
printfn "Largest delta: %.2f min" maxDeltaMin
printfn ""
printfn "Entries more than 5 min off from 09:30 ET (halt-opens):"
for (t, d, dm) in worstCases |> Seq.sortByDescending (fun (_, _, dm) -> abs dm) |> Seq.truncate 20 do
    printfn "  %-8s %s  %+.2f min" t d dm
