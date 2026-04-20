module TradingEdge.Orb.Convert

open System
open System.IO
open System.Text.Json
open TradeLoader
open TradeBinary

/// One row in the plays JSON. ticker + date are required. The 4w fields
/// are optional — when absent, the written header carries NaNs and the
/// ThresholdGate treats them as pass-through (so the day is effectively
/// ungated). Setup generators that care about the threshold gate must emit
/// raw_avg_4w / txn_avg_4w / split_factor_today.
type PlayEntry = {
    Ticker: string
    Date: string
    RawAvg4w: double
    TxnAvg4w: double
    SplitFactorToday: double
}

let private readDoubleOrNaN (el: JsonElement) (name: string) =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.Number -> v.GetDouble()
    | _ -> nan

/// Read a plays JSON file and return the deduplicated entries. Dedup is
/// keyed on (ticker, date); the 4w fields are carried through from the first
/// occurrence.
let loadPlays (jsonPath: string) : PlayEntry[] =
    let bytes = File.ReadAllBytes jsonPath
    use doc = JsonDocument.Parse(ReadOnlyMemory bytes)
    let seen = System.Collections.Generic.HashSet<struct (string * string)>()
    [|
        for el in doc.RootElement.EnumerateArray() do
            let ticker = el.GetProperty("ticker").GetString()
            let date = el.GetProperty("date").GetString()
            if seen.Add (struct (ticker, date)) then
                yield {
                    Ticker = ticker
                    Date = date
                    RawAvg4w = readDoubleOrNaN el "raw_avg_4w"
                    TxnAvg4w = readDoubleOrNaN el "txn_avg_4w"
                    SplitFactorToday = readDoubleOrNaN el "split_factor_today"
                }
    |]

/// Convert a single day's parquet trades file to the project's binary format.
/// Missing parquet is logged but non-fatal so a bulk run completes instead of aborting mid-way.
let convertOne (tradesDir: string) (outDir: string) (entry: PlayEntry) =
    let parquetPath = Path.Combine(tradesDir, entry.Ticker, $"{entry.Date}.parquet")
    if not (File.Exists parquetPath) then
        printfn "  MISSING %s/%s.parquet" entry.Ticker entry.Date
    else
        let staging = loadTrades parquetPath
        let info = { Directory = outDir; Ticker = entry.Ticker; Date = entry.Date }
        let meta : DayMeta = {
            RawAvg4w = entry.RawAvg4w
            TxnAvg4w = entry.TxnAvg4w
            SplitFactorToday = entry.SplitFactorToday
        }
        writeDay info meta staging

/// Convert every entry from a plays JSON file in parallel.
/// Uses Parallel.ForEach because conversion is CPU/IO bound per file and trivially independent across pairs.
let convertPlays (jsonPath: string) (tradesDir: string) (outDir: string) =
    let entries = loadPlays jsonPath
    printfn "Converting %d (ticker, date) pairs from %s" entries.Length jsonPath
    Directory.CreateDirectory outDir |> ignore
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable done_ = 0
    System.Threading.Tasks.Parallel.ForEach(entries, fun entry ->
        convertOne tradesDir outDir entry
        let n = System.Threading.Interlocked.Increment &done_
        if n % 100 = 0 then printfn "  [%d/%d] %.1fs" n entries.Length sw.Elapsed.TotalSeconds
    ) |> ignore
    sw.Stop()
    printfn "Done: %d pairs in %.1fs" entries.Length sw.Elapsed.TotalSeconds
