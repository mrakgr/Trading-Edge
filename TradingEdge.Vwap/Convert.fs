module TradingEdge.Vwap.Convert

open System
open System.IO
open System.Text.Json
open TradeLoader
open TradeBinary

type PlayEntry = {
    ticker: string
    date: string
}

let loadPlays (jsonPath: string) : (string * string)[] =
    let bytes = File.ReadAllBytes jsonPath
    use doc = JsonDocument.Parse(ReadOnlyMemory bytes)
    [| for el in doc.RootElement.EnumerateArray() ->
        el.GetProperty("ticker").GetString(), el.GetProperty("date").GetString() |]
    |> Array.distinct

let convertOne (tradesDir: string) (outDir: string) (ticker: string, date: string) =
    let parquetPath = Path.Combine(tradesDir, ticker, $"{date}.parquet")
    if not (File.Exists parquetPath) then
        printfn "  MISSING %s/%s.parquet" ticker date
    else
        let staging = loadTrades parquetPath
        let info = { Directory = outDir; Ticker = ticker; Date = date }
        writeDay info staging

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
