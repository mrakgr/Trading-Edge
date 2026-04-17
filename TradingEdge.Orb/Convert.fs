module TradingEdge.Orb.Convert

open System
open System.IO
open System.Text.Json
open TradeLoader
open TradeBinary

type PlayEntry = {
    ticker: string
    date: string
}

/// Read a plays JSON file and return the deduplicated (ticker, date) pairs.
/// Same (ticker, date) may appear multiple times across plays — dedup avoids redundant conversion work.
let loadPlays (jsonPath: string) : (string * string)[] =
    let bytes = File.ReadAllBytes jsonPath
    use doc = JsonDocument.Parse(ReadOnlyMemory bytes)
    [| for el in doc.RootElement.EnumerateArray() ->
        el.GetProperty("ticker").GetString(), el.GetProperty("date").GetString() |]
    |> Array.distinct

/// Convert a single day's parquet trades file to the project's binary format.
/// Missing parquet is logged but non-fatal so a bulk run completes instead of aborting mid-way.
let convertOne (tradesDir: string) (outDir: string) (ticker: string, date: string) =
    let parquetPath = Path.Combine(tradesDir, ticker, $"{date}.parquet")
    if not (File.Exists parquetPath) then
        printfn "  MISSING %s/%s.parquet" ticker date
    else
        let staging = loadTrades parquetPath
        let info = { Directory = outDir; Ticker = ticker; Date = date }
        writeDay info staging

/// Convert every (ticker, date) pair from a plays JSON file in parallel.
/// Uses Parallel.ForEach because conversion is CPU/IO bound per file and trivially independent across pairs.
let convertPlays (jsonPath: string) (tradesDir: string) (outDir: string) =
    let pairs = loadPlays jsonPath
    printfn "Converting %d (ticker, date) pairs from %s" pairs.Length jsonPath
    Directory.CreateDirectory outDir |> ignore
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable done_ = 0
    System.Threading.Tasks.Parallel.ForEach(pairs, fun pair ->
        convertOne tradesDir outDir pair
        let n = System.Threading.Interlocked.Increment &done_
        if n % 100 = 0 then printfn "  [%d/%d] %.1fs" n pairs.Length sw.Elapsed.TotalSeconds
    ) |> ignore
    sw.Stop()
    printfn "Done: %d pairs in %.1fs" pairs.Length sw.Elapsed.TotalSeconds
