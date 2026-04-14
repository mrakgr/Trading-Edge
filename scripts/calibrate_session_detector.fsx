#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#load "SessionDetector.fsx"

open System
open System.IO
open System.Text.Json
open DuckDB.NET.Data
open SessionDetector

// Polygon ns-since-epoch → DateTime (UTC). Ticks are 100ns.
let tsToUtc (ns: int64) = DateTime.UnixEpoch.AddTicks(ns / 100L).ToUniversalTime()

/// Stream every trade from a Parquet file through a SessionStartDetector
/// and return the trigger timestamp if any. We include all trades (extended
/// hours, odd lots, etc.) so the long window reflects the true ambient
/// volume leading into 09:30.
let runDetector (tradingDate: DateOnly) (path: string) : DateTime voption =
    let escaped = path.Replace("'", "''")
    use conn = new DuckDBConnection("Data Source=:memory:")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        sprintf
            "SELECT participant_timestamp, sip_timestamp, size \
             FROM read_parquet('%s') \
             ORDER BY coalesce(participant_timestamp, sip_timestamp) ASC"
            escaped
    use reader = cmd.ExecuteReader()
    let detector = SessionStartDetector(tradingDate)
    let mutable result = ValueNone
    let onTrigger (ts: DateTime) =
        if result.IsNone then result <- ValueSome ts
    while reader.Read() && result.IsNone do
        let partTs = if reader.IsDBNull 0 then 0L else reader.GetInt64 0
        let sipTs  = if reader.IsDBNull 1 then 0L else reader.GetInt64 1
        let size   = reader.GetDouble 2
        let ns = if partTs <> 0L then partTs else sipTs
        let ts = tsToUtc ns
        detector.Process(onTrigger, ts, size)
    result

// ---------------------------------------------------------------------------
// Calibrate against data/market_hours.json
// ---------------------------------------------------------------------------

let inputPath = "data/market_hours.json"
let tradesRoot = "data/trades"

printfn "Loading %s ..." inputPath
let entries =
    use fs = File.OpenRead inputPath
    use doc = JsonDocument.Parse(fs)
    [| for elem in doc.RootElement.EnumerateArray() ->
        let t = elem.GetProperty("ticker").GetString()
        let d = elem.GetProperty("date").GetString()
        // We calibrate against openingPrint (first auction trade), not
        // officialOpen (exchange marker). The opening print is when real
        // volume starts flowing and is what the detector should land on.
        let opProp = elem.GetProperty("openingPrint")
        let op =
            if opProp.ValueKind = JsonValueKind.Null then ValueNone
            else ValueSome (DateTime.Parse(opProp.GetString()).ToUniversalTime())
        t, d, op |]

let labeled = entries |> Array.filter (fun (_, _, op) -> op.IsSome)
printfn "Labeled entries (openingPrint present): %d" labeled.Length

// Parallelize — each worker owns its own DuckDB connection.
let mutable processed = 0
let total = labeled.Length
let progressLock = obj()

type Result = { Ticker: string; Date: string; OpeningPrint: DateTime; Trigger: DateTime voption }

let results =
    labeled
    |> Array.Parallel.map (fun (ticker, dateStr, opOpt) ->
        let op = opOpt.Value
        let date = DateOnly.ParseExact(dateStr, "yyyy-MM-dd")
        let path = Path.Combine(tradesRoot, ticker, sprintf "%s.parquet" dateStr)
        let trigger =
            if File.Exists path then
                try runDetector date path
                with ex ->
                    lock progressLock (fun () ->
                        eprintfn "  [ERR] %s/%s  %s" ticker dateStr ex.Message)
                    ValueNone
            else ValueNone
        let n = System.Threading.Interlocked.Increment(&processed)
        if n % 200 = 0 || n = total then
            lock progressLock (fun () -> printfn "  [%d/%d]" n total)
        { Ticker = ticker; Date = dateStr; OpeningPrint = op; Trigger = trigger })

// ---------------------------------------------------------------------------
// Report
// ---------------------------------------------------------------------------

let fired = results |> Array.filter (fun r -> r.Trigger.IsSome)
let missed = results |> Array.filter (fun r -> r.Trigger.IsNone)

printfn ""
printfn "=== Calibration results ==="
printfn "Total labeled days:   %d" results.Length
printfn "Detector fired:       %d (%.2f%%)" fired.Length (100.0 * float fired.Length / float results.Length)
printfn "Detector never fired: %d (%.2f%%)" missed.Length (100.0 * float missed.Length / float results.Length)

let deltas =
    fired
    |> Array.map (fun r -> (r.Trigger.Value - r.OpeningPrint).TotalSeconds)
    |> Array.sort
if deltas.Length > 0 then
    let pct p = deltas.[min (deltas.Length - 1) (int (float deltas.Length * p))]
    printfn ""
    printfn "Delta vs openingPrint (trigger - openingPrint, in seconds):"
    printfn "  min:    %+9.2f" deltas.[0]
    printfn "  p10:    %+9.2f" (pct 0.10)
    printfn "  p50:    %+9.2f" (pct 0.50)
    printfn "  p90:    %+9.2f" (pct 0.90)
    printfn "  p99:    %+9.2f" (pct 0.99)
    printfn "  max:    %+9.2f" deltas.[deltas.Length - 1]
    let within30 = deltas |> Array.filter (fun d -> abs d <= 30.0) |> Array.length
    let within60 = deltas |> Array.filter (fun d -> abs d <= 60.0) |> Array.length
    printfn "  within 30s: %d (%.2f%%)" within30 (100.0 * float within30 / float fired.Length)
    printfn "  within 60s: %d (%.2f%%)" within60 (100.0 * float within60 / float fired.Length)

printfn ""
printfn "=== Worst 20 outliers (|delta| largest) ==="
let worst =
    fired
    |> Array.map (fun r -> r, (r.Trigger.Value - r.OpeningPrint).TotalSeconds)

    |> Array.sortByDescending (fun (_, d) -> abs d)
    |> Array.truncate 20
for (r, d) in worst do
    printfn "  %-8s %s  delta=%+9.2fs" r.Ticker r.Date d

if missed.Length > 0 then
    printfn ""
    printfn "=== No-fire days (detector never triggered) ==="
    printfn "  Showing first 20:"
    for r in missed |> Array.truncate 20 do
        printfn "  %-8s %s" r.Ticker r.Date
