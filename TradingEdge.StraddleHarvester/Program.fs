module TradingEdge.StraddleHarvester.Program

open Argu

// StraddleHarvester (user 2026-11-10): sell the ~30-day ATM straddle on an individual stock when its ATM implied
// volatility exceeds its Yang-Zhang realized volatility. A skeleton: the stages are built one at a time, see
// docs/straddleharvester_results.md.

type Args =
    | Placeholder
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Placeholder -> "Nothing yet: the first stage is still to be built."

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Args>(programName = "StraddleHarvester")
    let _ = parser.ParseCommandLine argv
    printfn "StraddleHarvester: no stages built yet."
    0
