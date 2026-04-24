#r "../TradingEdge.Orb/bin/Debug/net10.0/TradingEdge.Orb.dll"
#r "nuget: DuckDB.NET.Data.Full, 1.4.4"

open System
open System.IO
open System.Text
open System.Text.Json
open DuckDB.NET.Data
open TradingEdge.Orb.TradeBinary

let binDir = "data/trades_bin"
let input = "data/continuation_plays.json"
let db = "data/trading.db"

let outGapUp = "data/breakouts_rvol3plus_gapup.json"
let outGapDown = "data/breakouts_rvol3plus_gapdown.json"
let minRvol = 3.0

// 1. Load RVOL>=3 breakouts from continuation_plays.
let entries =
    let bytes = File.ReadAllBytes input
    use doc = JsonDocument.Parse(ReadOnlyMemory bytes)
    [| for el in doc.RootElement.EnumerateArray() ->
        {|
            ticker = el.GetProperty("ticker").GetString()
            date = el.GetProperty("date").GetString()
            daysSince = el.GetProperty("days_since_max_rvol_day").GetInt32()
            rvol = el.GetProperty("rvol").GetDouble()
        |} |]
    |> Array.filter (fun e -> e.daysSince = 0 && e.rvol >= minRvol)

printfn "RVOL>=%.1f breakouts in continuation_plays: %d" minRvol entries.Length

// 2. Filter by bin + opening-print.
let withBin =
    entries
    |> Array.filter (fun e ->
        File.Exists (Path.Combine(binDir, e.ticker, $"{e.date}.bin")))
let withOpening =
    withBin
    |> Array.filter (fun e ->
        let h = readHeader { Directory = binDir; Ticker = e.ticker; Date = e.date }
        h.OpeningPrintIndex.IsSome)
printfn "After bin + opening-print filter:          %d" withOpening.Length

// 3. One DuckDB query: compute gap for every (ticker, date) we need.
// Using LAG over the ticker's own daily history, so "prior close" is the most recent
// daily_prices row before `date` — whatever the calendar gap (weekends, holidays).
let computeGaps () =
    let neededTickers =
        withOpening |> Array.map (fun e -> e.ticker) |> Array.distinct
    let tickerList = neededTickers |> Array.map (fun t -> "'" + t + "'") |> String.concat ","
    let template = "
WITH daily AS (
    SELECT
        ticker,
        strftime(date, '%Y-%m-%d') AS day,
        open,
        LAG(close) OVER (PARTITION BY ticker ORDER BY date) AS prev_close
    FROM daily_prices
    WHERE ticker IN (__TICKERS__)
)
SELECT ticker, day, open, prev_close
FROM daily
WHERE prev_close IS NOT NULL AND prev_close > 0
"
    let query = template.Replace("__TICKERS__", tickerList)
    use conn = new DuckDBConnection(sprintf "Data Source=%s;ACCESS_MODE=READ_ONLY" db)
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- query
    use reader = cmd.ExecuteReader()
    let dict = System.Collections.Generic.Dictionary<struct (string * string), float>()
    while reader.Read() do
        let ticker = reader.GetString 0
        let day = reader.GetString 1
        let openP = reader.GetDouble 2
        let prevClose = reader.GetDouble 3
        let gap = openP / prevClose - 1.0
        dict.[struct (ticker, day)] <- gap
    dict

printfn "Querying gaps from trading.db ..."
let sw = System.Diagnostics.Stopwatch.StartNew()
let gapByKey = computeGaps ()
sw.Stop()
printfn "Gap lookup populated: %d rows in %.1fs" gapByKey.Count sw.Elapsed.TotalSeconds

// 4. Attach gap to each entry, drop those without.
let withGap =
    withOpening
    |> Array.choose (fun e ->
        match gapByKey.TryGetValue (struct (e.ticker, e.date)) with
        | true, g -> Some {| ticker = e.ticker; date = e.date; gap = g |}
        | false, _ -> None)
printfn "With gap data:                             %d" withGap.Length

// 5. Split.
let gapUp = withGap |> Array.filter (fun e -> e.gap > 0.0)
let gapDown = withGap |> Array.filter (fun e -> e.gap < 0.0)
printfn "Gap up (>0%%):                              %d" gapUp.Length
printfn "Gap down (<0%%):                            %d" gapDown.Length

// Distribution.
let gapPcts = withGap |> Array.map (fun e -> e.gap * 100.0) |> Array.sort
let pct p (arr: float[]) =
    if arr.Length = 0 then 0.0
    else arr.[int (float arr.Length * p) |> min (arr.Length - 1) |> max 0]
printfn ""
printfn "Gap %% distribution:"
for p in [| 0.01; 0.05; 0.25; 0.50; 0.75; 0.95; 0.99 |] do
    printfn "  p%-3.0f:  %+7.2f%%" (p * 100.0) (pct p gapPcts)

// 6. Write.
let writeJson (path: string) (rows: {| ticker: string; date: string; gap: float |}[]) =
    let sb = StringBuilder()
    sb.Append "[\n" |> ignore
    rows
    |> Array.iteri (fun i e ->
        let comma = if i = rows.Length - 1 then "" else ","
        sb.AppendFormat("    {{\"ticker\": \"{0}\", \"date\": \"{1}\"}}{2}\n", e.ticker, e.date, comma) |> ignore)
    sb.Append "]\n" |> ignore
    File.WriteAllText(path, sb.ToString())
    printfn "Wrote %s (%d entries)" path rows.Length

writeJson outGapUp gapUp
writeJson outGapDown gapDown
