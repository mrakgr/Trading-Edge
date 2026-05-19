module TradingEdge.ReplaySimulator.Program

// Milestone-1 smoke test: open a single DBN file or a directory of DBN files,
// count records by action type, and dump the first few MBO trades for sanity.
// Once the reader is solid, this `main` is replaced by the Avalonia app entry.

open System
open System.IO
open TradingEdge.ReplaySimulator.Dbn
open TradingEdge.ReplaySimulator.MboReader

let dumpMetadata (m: DbnMetadata) =
    printfn "  version:           %d" m.Version
    printfn "  dataset:           %s" m.Dataset
    printfn "  schema:            %d (MBO=%d)" m.Schema (int SCHEMA_MBO)
    printfn "  start ns:          %d" m.StartNs
    printfn "  end ns:            %d" m.EndNs
    printfn "  limit:             %d" m.Limit
    printfn "  stype_in:          %d  stype_out: %d  ts_out: %b" m.StypeIn m.StypeOut m.TsOut
    printfn "  symbol_cstr_len:   %d" m.SymbolCstrLen
    printfn "  symbols (%d):       %s" m.Symbols.Length (String.concat "," m.Symbols)
    printfn "  partial (%d):       %s" m.PartialSymbols.Length (String.concat "," m.PartialSymbols)
    printfn "  not_found (%d):     %s" m.NotFoundSymbols.Length (String.concat "," m.NotFoundSymbols)
    printfn "  mappings (%d):      %s"
        m.Mappings.Length
        (String.concat ";"
            [ for (s, ivs) in m.Mappings -> sprintf "%s→%d intervals" s ivs.Length ])

let scanVenue (path: string) =
    printfn "--- %s ---" (Path.GetFileName path)
    let v = openVenue path
    try
        dumpMetadata v.Metadata
        let mutable n = 0
        let mutable nTrade = 0
        let mutable nFill = 0
        let mutable nAdd = 0
        let mutable nCancel = 0
        let mutable nModify = 0
        let mutable nReset = 0
        let mutable nOther = 0
        let mutable firstFive = []
        let mutable minTs = Int64.MaxValue
        let mutable maxTs = Int64.MinValue
        for rec_ in v.Records do
            n <- n + 1
            if rec_.TsEvent < minTs then minTs <- rec_.TsEvent
            if rec_.TsEvent > maxTs then maxTs <- rec_.TsEvent
            match char rec_.Action with
            | 'T' -> nTrade <- nTrade + 1
            | 'F' -> nFill <- nFill + 1
            | 'A' -> nAdd <- nAdd + 1
            | 'C' -> nCancel <- nCancel + 1
            | 'M' -> nModify <- nModify + 1
            | 'R' -> nReset <- nReset + 1
            | _   -> nOther <- nOther + 1
            if (char rec_.Action = 'T' || char rec_.Action = 'F') && firstFive.Length < 5 then
                firstFive <- rec_ :: firstFive
        printfn ""
        printfn "  records total:     %d" n
        printfn "    Add:    %d" nAdd
        printfn "    Cancel: %d" nCancel
        printfn "    Modify: %d" nModify
        printfn "    Reset:  %d" nReset
        printfn "    Trade:  %d" nTrade
        printfn "    Fill:   %d" nFill
        printfn "    Other:  %d" nOther
        if n > 0 then
            printfn "  ts range:          %d → %d" minTs maxTs
            let dt0 = DateTimeOffset.FromUnixTimeMilliseconds(minTs / 1_000_000L).UtcDateTime
            let dt1 = DateTimeOffset.FromUnixTimeMilliseconds(maxTs / 1_000_000L).UtcDateTime
            printfn "    (%s → %s UTC)" (dt0.ToString("yyyy-MM-dd HH:mm:ss.fff")) (dt1.ToString("yyyy-MM-dd HH:mm:ss.fff"))
        printfn ""
        printfn "  first 5 T/F records:"
        for r in List.rev firstFive do
            let priceDollars = float r.Price / 1e9
            printfn "    ts=%d action=%c side=%c price=$%.4f size=%d order_id=%d"
                r.TsEvent (char r.Action) (char r.Side) priceDollars r.Size r.OrderId
    finally
        v.Disposable.Dispose()

[<EntryPoint>]
let main argv =
    if argv.Length = 0 then
        printfn "Usage: TradingEdge.ReplaySimulator <path-to-dbn-file-or-dir>"
        printfn ""
        printfn "If given a directory, scans all *.dbn.zst files within."
        1
    else
        let target = argv.[0]
        let files =
            if File.Exists target then [| target |]
            elif Directory.Exists target then
                Directory.GetFiles(target, "*.dbn.zst")
                |> Array.sort
            else
                printfn "Not found: %s" target
                exit 2
        if files.Length = 0 then
            printfn "No .dbn.zst files in %s" target
            exit 2
        for f in files do
            scanVenue f
            printfn ""
        0
