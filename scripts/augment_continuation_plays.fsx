#r "nuget: DuckDB.NET.Data.Full, 1.4.4"

open System
open System.IO
open System.Text
open System.Text.Json
open DuckDB.NET.Data

let input = "data/continuation_plays.json"
let output = "data/continuation_plays_augmented.json"
let db = "data/trading.db"

// 1. Load all entries, verbatim, so we can emit them back with extra fields.
let raw =
    let bytes = File.ReadAllBytes input
    use doc = JsonDocument.Parse(ReadOnlyMemory bytes)
    [| for el in doc.RootElement.EnumerateArray() ->
        // We copy into a dictionary so we can re-emit preserving original fields.
        let props = System.Collections.Generic.Dictionary<string, JsonElement>()
        for p in el.EnumerateObject() do
            props.[p.Name] <- p.Value.Clone()
        props |]

printfn "Loaded %d entries" raw.Length

// 2. Build the set of tickers we need.
let neededTickers =
    raw
    |> Array.map (fun d -> d.["ticker"].GetString())
    |> Array.distinct

printfn "Distinct tickers: %d" neededTickers.Length

// 3. Query daily_prices with LAG for prev_close.
let loadDaily () =
    let tickerList = neededTickers |> Array.map (fun t -> "'" + t + "'") |> String.concat ","
    let template = "
WITH daily AS (
    SELECT
        ticker,
        strftime(date, '%Y-%m-%d') AS day,
        open,
        high,
        low,
        close,
        LAG(close) OVER (PARTITION BY ticker ORDER BY date) AS prev_close
    FROM daily_prices
    WHERE ticker IN (__TICKERS__)
)
SELECT ticker, day, open, high, low, close, prev_close FROM daily
"
    let query = template.Replace("__TICKERS__", tickerList)
    use conn = new DuckDBConnection(sprintf "Data Source=%s;ACCESS_MODE=READ_ONLY" db)
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- query
    use reader = cmd.ExecuteReader()
    let dict = System.Collections.Generic.Dictionary<struct (string * string), struct (float * float * float * float * float voption)>()
    while reader.Read() do
        let ticker = reader.GetString 0
        let day = reader.GetString 1
        let openP = reader.GetDouble 2
        let high = reader.GetDouble 3
        let low = reader.GetDouble 4
        let closeP = reader.GetDouble 5
        let prevClose =
            if reader.IsDBNull 6 then ValueNone
            else ValueSome (reader.GetDouble 6)
        dict.[struct (ticker, day)] <- struct (openP, high, low, closeP, prevClose)
    dict

printfn "Querying daily_prices ..."
let sw = System.Diagnostics.Stopwatch.StartNew()
let dailyByKey = loadDaily ()
sw.Stop()
printfn "Loaded %d daily rows in %.1fs" dailyByKey.Count sw.Elapsed.TotalSeconds

// 4. Augment each entry. Entries without daily data (e.g. very old or missing) get
// null fields. Entries without a prior close get a null gap_pct.
let mutable nFound = 0
let mutable nNoDaily = 0
let mutable nNoPrev = 0
let mutable nFlatRange = 0

let augmented =
    raw
    |> Array.map (fun props ->
        let ticker = props.["ticker"].GetString()
        let date = props.["date"].GetString()
        let gapPct, closeOverOpen, closeInRange =
            match dailyByKey.TryGetValue (struct (ticker, date)) with
            | false, _ ->
                nNoDaily <- nNoDaily + 1
                ValueNone, ValueNone, ValueNone
            | true, struct (openP, high, low, closeP, prevClose) ->
                nFound <- nFound + 1
                let gap =
                    match prevClose with
                    | ValueSome pc when pc > 0.0 -> ValueSome (openP / pc - 1.0)
                    | _ -> nNoPrev <- nNoPrev + 1; ValueNone
                let cvo =
                    if openP > 0.0 then ValueSome (closeP / openP - 1.0)
                    else ValueNone
                let cir =
                    if high > low then ValueSome ((closeP - low) / (high - low))
                    else
                        nFlatRange <- nFlatRange + 1
                        ValueNone  // degenerate (e.g. halted all day, one trade)
                gap, cvo, cir
        props, gapPct, closeOverOpen, closeInRange)

printfn "Augmentation: %d found, %d missing daily, %d missing prev-close, %d flat-range" nFound nNoDaily nNoPrev nFlatRange

// 5. Distribution diagnostics.
let stats (name: string) (vs: float voption[]) =
    let arr = vs |> Array.choose (function ValueSome v -> Some v | _ -> None) |> Array.sort
    if arr.Length = 0 then printfn "%s: no data" name
    else
        let pct p = arr.[int (float arr.Length * p) |> min (arr.Length - 1) |> max 0]
        printfn "%-22s  n=%d  p05=%+.3f  p25=%+.3f  p50=%+.3f  p75=%+.3f  p95=%+.3f"
            name arr.Length (pct 0.05) (pct 0.25) (pct 0.5) (pct 0.75) (pct 0.95)

printfn ""
printfn "Field distributions (all continuation_plays rows, not just breakouts):"
stats "gap_pct" (augmented |> Array.map (fun (_, g, _, _) -> g))
stats "close_vs_open_pct" (augmented |> Array.map (fun (_, _, c, _) -> c))
stats "close_in_range_pct" (augmented |> Array.map (fun (_, _, _, r) -> r))

// 6. Emit as JSON. Preserve original field order via the dictionary we captured.
// For each row, we write out the original fields first (in insertion order), then
// append the three new ones.
let writeValue (sb: StringBuilder) (el: JsonElement) =
    // Delegate to System.Text.Json for correct escaping and type handling.
    sb.Append(el.GetRawText()) |> ignore

let writeDouble (sb: StringBuilder) (v: float voption) =
    match v with
    | ValueNone -> sb.Append "null" |> ignore
    | ValueSome x -> sb.Append(x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)) |> ignore

let sb = StringBuilder()
sb.Append "[\n" |> ignore
augmented
|> Array.iteri (fun i (props, gap, cvo, cir) ->
    sb.Append "  {" |> ignore
    let mutable first = true
    for kv in props do
        if first then first <- false else sb.Append ", " |> ignore
        sb.Append '"' |> ignore
        sb.Append kv.Key |> ignore
        sb.Append "\": " |> ignore
        writeValue sb kv.Value
    sb.Append ", \"gap_pct\": " |> ignore
    writeDouble sb gap
    sb.Append ", \"close_vs_open_pct\": " |> ignore
    writeDouble sb cvo
    sb.Append ", \"close_in_range_pct\": " |> ignore
    writeDouble sb cir
    sb.Append "}" |> ignore
    if i < augmented.Length - 1 then sb.Append "," |> ignore
    sb.Append "\n" |> ignore)
sb.Append "]\n" |> ignore
File.WriteAllText(output, sb.ToString())
printfn ""
printfn "Wrote %s (%d entries)" output augmented.Length
