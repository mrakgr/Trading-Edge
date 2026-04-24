#r "../TradingEdge.Orb/bin/Debug/net10.0/TradingEdge.Orb.dll"

open System
open System.IO
open System.Text
open System.Text.Json
open TradingEdge.Orb.TradeBinary

let input = "data/continuation_plays_augmented.json"
let binDir = "data/trades_bin"

let thresholds = [| 4.0; 5.0; 6.0; 7.0; 8.0; 10.0 |]

let raw =
    let bytes = File.ReadAllBytes input
    use doc = JsonDocument.Parse(ReadOnlyMemory bytes)
    [| for el in doc.RootElement.EnumerateArray() ->
        {|
            ticker = el.GetProperty("ticker").GetString()
            date = el.GetProperty("date").GetString()
            daysSince = el.GetProperty("days_since_max_rvol_day").GetInt32()
            rvol = el.GetProperty("rvol").GetDouble()
        |} |]

printfn "Total entries: %d" raw.Length

let filterTradable rows =
    rows
    |> Array.filter (fun (r: {| ticker: string; date: string; daysSince: int; rvol: float |}) ->
        let path = Path.Combine(binDir, r.ticker, $"{r.date}.bin")
        File.Exists path
        &&
        let h = readHeader { Directory = binDir; Ticker = r.ticker; Date = r.date }
        h.OpeningPrintIndex.IsSome)

let writeJson (path: string) (rows: {| ticker: string; date: string; daysSince: int; rvol: float |}[]) =
    let sb = StringBuilder()
    sb.Append "[\n" |> ignore
    rows
    |> Array.iteri (fun i e ->
        let comma = if i = rows.Length - 1 then "" else ","
        sb.AppendFormat("    {{\"ticker\": \"{0}\", \"date\": \"{1}\"}}{2}\n", e.ticker, e.date, comma) |> ignore)
    sb.Append "]\n" |> ignore
    File.WriteAllText(path, sb.ToString())

for minRvol in thresholds do
    let band = raw |> Array.filter (fun r -> r.daysSince = 0 && r.rvol >= minRvol)
    let tradable = filterTradable band
    let path = sprintf "data/breakouts_rvol%dplus.json" (int minRvol)
    writeJson path tradable
    printfn "RVOL >= %4.1f: %d raw -> %d tradable -> %s" minRvol band.Length tradable.Length path
