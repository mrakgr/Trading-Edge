open System
open System.IO
open System.Text.Json
open System.Collections.Generic

let easternTz =
    try TimeZoneInfo.FindSystemTimeZoneById("America/New_York")
    with _ -> TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")

let toEastern (utc: DateTime) : DateTime =
    TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), easternTz)

let mhPath = "data/market_hours.json"
use fs = File.OpenRead mhPath
use doc = JsonDocument.Parse(fs)

let mutable nChecked = 0
// Bucket: (hour, minute) in Eastern time
let bucket = Dictionary<struct (int * int), int>()
let earlyCloses = ResizeArray<string * string * DateTime>()

for elem in doc.RootElement.EnumerateArray() do
    let ticker = elem.GetProperty("ticker").GetString()
    let date   = elem.GetProperty("date").GetString()
    let ocProp = elem.GetProperty("officialClose")
    if ocProp.ValueKind <> JsonValueKind.Null then
        let oc = ocProp.GetString() |> DateTime.Parse |> fun d -> d.ToUniversalTime()
        let et = toEastern oc
        nChecked <- nChecked + 1
        let key = struct (et.Hour, et.Minute)
        match bucket.TryGetValue key with
        | true, v -> bucket.[key] <- v + 1
        | _ -> bucket.[key] <- 1
        // Track closes before 14:00 ET (early-close days)
        if et.Hour < 14 || (et.Hour = 14 && et.Minute = 0) then
            earlyCloses.Add((ticker, date, et))

printfn "Checked %d entries with non-null officialClose." nChecked
printfn ""
printfn "Top (hour:minute) ET buckets:"
for kv in bucket |> Seq.sortByDescending (fun kv -> kv.Value) |> Seq.truncate 15 do
    let struct (h, m) = kv.Key
    let n = kv.Value
    printfn "  %02d:%02d ET   %5d (%.2f%%)" h m n (100.0 * float n / float nChecked)
printfn ""
printfn "Closes at or before 14:00 ET (early-close days):"
// Group by date to see the distinct early-close days
let byDate =
    earlyCloses
    |> Seq.groupBy (fun (_, d, _) -> d)
    |> Seq.map (fun (d, xs) ->
        let sample = xs |> Seq.head |> (fun (_, _, et) -> et)
        d, Seq.length xs, sample)
    |> Seq.sortBy (fun (d, _, _) -> d)
    |> Seq.toList
printfn "  Distinct dates: %d  |  Total entries: %d" byDate.Length (earlyCloses |> Seq.length)
for (d, n, sample) in byDate do
    printfn "    %s  n=%d   sample close ET: %02d:%02d:%02d" d n sample.Hour sample.Minute sample.Second
