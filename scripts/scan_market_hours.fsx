#r "nuget: DuckDB.NET.Data.Full, 1.4.4"

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open DuckDB.NET.Data

// Polygon SIP condition codes we care about
let [<Literal>] CondOfficialClose = 15
let [<Literal>] CondOfficialOpen  = 16
// Opening print: 17 (Market Center Opening Trade) or 25 (Opening Prints)
// Closing print: 19 (Market Center Closing Trade) or 8 (Closing Prints)
let openingPrintConds = Set.ofList [17; 25]
let closingPrintConds = Set.ofList [19; 8]

// Polygon timestamps are ns since Unix epoch. DateTime ticks are 100ns.
let tsToUtc (ns: int64) = DateTime.UnixEpoch.AddTicks(ns / 100L).ToUniversalTime()

type MarkerTimes = {
    OfficialOpen:  DateTime voption
    OfficialClose: DateTime voption
    OpeningPrint:  DateTime voption
    ClosingPrint:  DateTime voption
}

// Output record. Field declaration order is the JSON serialization order
// for F# records under System.Text.Json.
type OutputEntry = {
    ticker:        string
    date:          string
    officialOpen:  string
    openingPrint:  string
    officialClose: string
    closingPrint:  string
}

let emptyMarkers = {
    OfficialOpen = ValueNone; OfficialClose = ValueNone
    OpeningPrint = ValueNone; ClosingPrint = ValueNone
}

let scanParquet (path: string) : MarkerTimes =
    // Filter rows in SQL so only trades with one of the four marker
    // conditions are returned. Keeps the .NET reader loop tiny.
    let escaped = path.Replace("'", "''")
    use conn = new DuckDBConnection("Data Source=:memory:")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        sprintf
            "SELECT participant_timestamp, sip_timestamp, conditions \
             FROM read_parquet('%s') \
             WHERE list_has_any(conditions, [8, 15, 16, 17, 19, 25])"
            escaped

    let updateEarliest (slot: DateTime voption) (ts: DateTime) =
        match slot with
        | ValueNone -> ValueSome ts
        | ValueSome prev when ts < prev -> ValueSome ts
        | _ -> slot

    let mutable officialOpen  = ValueNone
    let mutable officialClose = ValueNone
    let mutable openingPrint  = ValueNone
    let mutable closingPrint  = ValueNone

    use reader = cmd.ExecuteReader()
    while reader.Read() do
        let partTs = if reader.IsDBNull 0 then 0L else reader.GetInt64 0
        let sipTs  = if reader.IsDBNull 1 then 0L else reader.GetInt64 1
        let ns     = if partTs <> 0L then partTs else sipTs
        let ts     = tsToUtc ns
        let raw    = reader.GetValue(2) :?> IList<int>
        for i in 0 .. raw.Count - 1 do
            let c = raw.[i]
            if c = CondOfficialOpen then
                officialOpen <- updateEarliest officialOpen ts
            elif c = CondOfficialClose then
                officialClose <- updateEarliest officialClose ts
            elif openingPrintConds.Contains c then
                openingPrint <- updateEarliest openingPrint ts
            elif closingPrintConds.Contains c then
                closingPrint <- updateEarliest closingPrint ts

    { OfficialOpen = officialOpen; OfficialClose = officialClose
      OpeningPrint = openingPrint; ClosingPrint = closingPrint }

// ---------------------------------------------------------------------------
// Load (ticker, date) pairs from continuation_plays.json
// ---------------------------------------------------------------------------

let inputPath  = "data/continuation_plays.json"
let outputPath = "data/market_hours.json"
let tradesRoot = "data/trades"

printfn "Loading %s ..." inputPath
let pairs =
    use fs = File.OpenRead inputPath
    use doc = JsonDocument.Parse(fs)
    let seen = HashSet<string * string>()
    let result = ResizeArray<string * string>()
    for elem in doc.RootElement.EnumerateArray() do
        let t = elem.GetProperty("ticker").GetString()
        let d = elem.GetProperty("date").GetString()
        if seen.Add((t, d)) then result.Add((t, d))
    result.ToArray()

printfn "Found %d unique (ticker, date) pairs." pairs.Length

// ---------------------------------------------------------------------------
// Parallel scan
// ---------------------------------------------------------------------------

let mutable processed = 0
let totalCount = pairs.Length
let progressLock = obj()

let entries =
    pairs
    |> Array.Parallel.map (fun (ticker, date) ->
        let path = Path.Combine(tradesRoot, ticker, sprintf "%s.parquet" date)
        let markers =
            if File.Exists path then
                try scanParquet path
                with ex ->
                    lock progressLock (fun () ->
                        eprintfn "  [ERR]  %s/%s  %s" ticker date ex.Message)
                    emptyMarkers
            else
                lock progressLock (fun () ->
                    eprintfn "  [MISS] %s/%s  (no parquet)" ticker date)
                emptyMarkers

        let n = System.Threading.Interlocked.Increment(&processed)
        if n % 50 = 0 || n = totalCount then
            lock progressLock (fun () ->
                printfn "  [%d/%d] scanned" n totalCount)

        let fmt (dt: DateTime voption) =
            match dt with
            | ValueSome ts -> ts.ToString("o")
            | ValueNone    -> null
        { ticker        = ticker
          date          = date
          officialOpen  = fmt markers.OfficialOpen
          openingPrint  = fmt markers.OpeningPrint
          officialClose = fmt markers.OfficialClose
          closingPrint  = fmt markers.ClosingPrint })

// ---------------------------------------------------------------------------
// Serialize
// ---------------------------------------------------------------------------

printfn "Writing %s ..." outputPath
let opts = JsonSerializerOptions(WriteIndented = true)
let json = JsonSerializer.Serialize(entries, opts)
File.WriteAllText(outputPath, json)

// Summary
let countSome (f: OutputEntry -> string) =
    entries |> Array.sumBy (fun e -> if isNull (f e) then 0 else 1)
let nOpen  = countSome (fun e -> e.officialOpen)
let nClose = countSome (fun e -> e.officialClose)
let nOP    = countSome (fun e -> e.openingPrint)
let nCP    = countSome (fun e -> e.closingPrint)
printfn ""
printfn "Done. %d entries written." entries.Length
printfn "  officialOpen  present: %d / %d" nOpen  entries.Length
printfn "  officialClose present: %d / %d" nClose entries.Length
printfn "  openingPrint  present: %d / %d" nOP    entries.Length
printfn "  closingPrint  present: %d / %d" nCP    entries.Length
