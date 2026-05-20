module TradingEdge.ReplaySimulator.Program

// Entry point.
//   chart <symbol> <date>     -> load data/databento/mbo/<SYM>/<date>/, build bars, open Avalonia window
//   chart <day-dir>           -> same, treating arg as the directory directly
//   scan  <file-or-dir>       -> dump DBN metadata + per-action record counts (smoke test)
//   (no args)                 -> defaults to chart EOSE 2026-05-13

open System
open System.IO
open Avalonia
open TradingEdge.ReplaySimulator
open TradingEdge.ReplaySimulator.Dbn
open TradingEdge.ReplaySimulator.MboReader

let private defaultDataRoot =
    Path.Combine(__SOURCE_DIRECTORY__, "..", "data", "databento", "mbo")
    |> Path.GetFullPath

let private resolveDayDir (a: string) (b: string option) : string * string * string =
    match b with
    | Some date ->
        let dir = Path.Combine(defaultDataRoot, a.ToUpperInvariant(), date)
        if not (Directory.Exists dir) then
            failwithf "Day directory not found: %s" dir
        dir, a.ToUpperInvariant(), date
    | None ->
        if not (Directory.Exists a) then failwithf "Directory not found: %s" a
        // Infer symbol/date from path tail: .../<SYM>/<date>
        let parts = a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |])
        let n = parts.Length
        let date = if n >= 1 then parts.[n - 1] else ""
        let sym  = if n >= 2 then parts.[n - 2] else "?"
        a, sym, date

let private dumpMetadata (m: DbnMetadata) =
    printfn "  version:           %d" m.Version
    printfn "  dataset:           %s" m.Dataset
    printfn "  schema:            %d (MBO=%d)" m.Schema (int SCHEMA_MBO)
    printfn "  symbols (%d):       %s" m.Symbols.Length (String.concat "," m.Symbols)

let private scanVenue (path: string) =
    printfn "--- %s ---" (Path.GetFileName path)
    let v = openVenue path
    try
        dumpMetadata v.Metadata
        let mutable n = 0
        let mutable nT = 0
        let mutable nF = 0
        for rec_ in v.Records do
            n <- n + 1
            match char rec_.Action with
            | 'T' -> nT <- nT + 1
            | 'F' -> nF <- nF + 1
            | _ -> ()
        printfn "  records: %d  trades: %d  fills: %d" n nT nF
    finally
        v.Disposable.Dispose()

let private runScan (target: string) =
    let files =
        if File.Exists target then [| target |]
        elif Directory.Exists target then
            Directory.GetFiles(target, "*.dbn.zst") |> Array.sort
        else
            failwithf "Not found: %s" target
    if files.Length = 0 then failwithf "No .dbn.zst files in %s" target
    for f in files do
        scanVenue f
        printfn ""

let private runChart (dayDir: string) (symbol: string) (date: string) =
    printfn "Opening %s %s from %s ..." symbol date dayDir
    let files =
        Directory.GetFiles(dayDir, "*.dbn.zst")
        |> Array.sort
        |> Array.toList
    if files.IsEmpty then
        printfn "No .dbn.zst files in %s — nothing to replay." dayDir
    else
        let venues = files |> List.map openVenue
        let merged = Bars.mergeByTsEvent (venues |> List.map (fun v -> v.Records))
        printfn "  %d venues opened; replay engine ready." venues.Length
        try
            let app =
                AppBuilder
                    .Configure<App.App>(fun () -> App.App(symbol, date, merged, venues.Length))
                    .UsePlatformDetect()
                    .LogToTrace()
            app.StartWithClassicDesktopLifetime([||]) |> ignore
        finally
            venues |> List.iter (fun v -> v.Disposable.Dispose())

[<EntryPoint>]
let main argv =
    try
        match argv with
        | [||] ->
            let dir, sym, date = resolveDayDir "EOSE" (Some "2026-05-13")
            runChart dir sym date
            0
        | [| "scan"; target |] ->
            runScan target
            0
        | [| "chart"; arg |] ->
            let dir, sym, date = resolveDayDir arg None
            runChart dir sym date
            0
        | [| "chart"; sym; date |] ->
            let dir, sym, date = resolveDayDir sym (Some date)
            runChart dir sym date
            0
        | _ ->
            printfn "Usage:"
            printfn "  TradingEdge.ReplaySimulator                          -> chart EOSE 2026-05-13"
            printfn "  TradingEdge.ReplaySimulator chart <SYM> <yyyy-MM-dd> -> chart that day"
            printfn "  TradingEdge.ReplaySimulator chart <day-dir>          -> chart that directory"
            printfn "  TradingEdge.ReplaySimulator scan  <file-or-dir>      -> dump metadata + counts"
            1
    with ex ->
        eprintfn "ERROR: %s" ex.Message
        eprintfn "%s" ex.StackTrace
        2
