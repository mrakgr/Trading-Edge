#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.1.3"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TradingEdge.Parsing.dll"

open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.VwapSystem

let run ticker date =
    let path = sprintf "data/trades/%s/%s.json" ticker date
    let trades = loadTrades path
    let op = trades |> Array.tryFind (fun tr -> tr.Session = OpeningPrint)
    let cp = trades |> Array.tryFind (fun tr -> tr.Session = ClosingPrint)
    let window = { openTime = op.Value.Timestamp; closeTime = cp.Value.Timestamp }
    let basePct = 0.005
    let decay = 0.9
    let exponents = [| -13; -5; -6; -6 |]
    let pcts = exponents |> Array.map (fun i -> basePct * (decay ** float i))

    let mutable lastBar = None
    let mutable barCount = 0
    let collector (bar: VwapSystemBar option, _decision: TradingDecision option, _trade: Trade) =
        match bar with
        | Some b ->
            barCount <- barCount + 1
            lastBar <- Some b
            if barCount <= 10 || barCount % 50 = 0 then
                printfn "  Bar %3d: VWAP=%.4f StdDev=%.6f VWMA=%.4f VolFactor=%.6f" barCount b.Bar.VWAP b.Bar.StdDev b.Vwma b.VolFactor
        | None -> ()

    let track, getResult = trackDecisions ()
    let mutable chain = track collector
    let decide = vwapSystem (30000.0, Some 0.0095)
    let segregate = segregateTrades window pcts
    let addTrade = segregate (decide chain)
    for tr in trades do addTrade tr
    let r = getResult()
    match lastBar with
    | Some b ->
        let effSize = min 30000.0 (30000.0 * 0.0095 / b.VolFactor)
        printfn "%s %s: Final VolFactor=%.6f EffSize=$%.0f Price~%.2f Shares~%d Decisions=%d"
            ticker date b.VolFactor effSize b.Bar.VWAP (int (effSize / b.Bar.VWAP)) r.Decisions.Count
    | None -> printfn "%s %s: No bars" ticker date
    printfn ""

run "UGRO" "2026-03-26"
run "TPET" "2026-03-02"
run "TPET" "2026-03-03"
run "TPET" "2026-03-05"
