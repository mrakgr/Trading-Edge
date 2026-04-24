module TradingEdge.Hmm.Program

open System
open System.IO
open Argu
open TradingEdge.Hmm.LwModel
open TradingEdge.Hmm.ForwardBackward
open TradingEdge.Orb.TradeBinary

type InferArgs =
    | [<AltCommandLine("-t")>] Ticker of string
    | [<AltCommandLine("-d")>] Date of string
    | [<AltCommandLine("-i")>] Bin_Dir of string
    | [<AltCommandLine("-o")>] Output of string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Ticker _ -> "Ticker symbol (e.g. LW)"
            | Date _ -> "Trading date (YYYY-MM-DD)"
            | Bin_Dir _ -> "Root directory of trade binaries (default: data/trades_bin)"
            | Output _ -> "Output CSV path (default: logs/hmm_<ticker>_<date>.csv)"

type TestArgs =
    | Placeholder   // Argu requires at least one case; never set from CLI.
    interface IArgParserTemplate with
        member this.Usage = "(internal)"

type Args =
    | [<CliPrefix(CliPrefix.None)>] Test of ParseResults<TestArgs>
    | [<CliPrefix(CliPrefix.None)>] Infer of ParseResults<InferArgs>
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Test _ -> "Run the synthetic-data unit test"
            | Infer _ -> "Run forward-backward on a (ticker, date) binary and dump posteriors"

let runInfer (args: ParseResults<InferArgs>) =
    let ticker = args.GetResult Ticker
    let date = args.GetResult Date
    let binDir = args.GetResult(Bin_Dir, defaultValue = "data/trades_bin")
    let outPath =
        args.GetResult(Output, defaultValue = sprintf "logs/hmm_%s_%s.csv" ticker date)

    let info = { Directory = binDir; Ticker = ticker; Date = date }
    let header, trades = loadDay info
    printfn "loaded %d trades from %s / %s" trades.Length ticker date
    let seq = buildSequence trades
    let t = seq.DtSec.Length
    printfn "sequence length: %d" t

    let sw = System.Diagnostics.Stopwatch.StartNew()
    let out = infer defaultParams seq
    sw.Stop()
    printfn "forward-backward: %d ms, loglik = %g" sw.ElapsedMilliseconds out.LogLikelihood

    Directory.CreateDirectory(Path.GetDirectoryName outPath) |> ignore
    use w = new StreamWriter(outPath)
    w.WriteLine("k,ticks_from_base,dt_sec,dlogp,volume,price,p_up,p_consol,p_down")
    // Trade index in the original array = k + 1 (we start pairwise from index 1).
    for k in 0 .. t - 1 do
        let trade = trades.[k + 1]
        let pUp = exp out.LogGamma.[0, k]
        let pCon = exp out.LogGamma.[1, k]
        let pDn = exp out.LogGamma.[2, k]
        w.WriteLine(
            sprintf "%d,%d,%.6g,%.6g,%.1f,%.6f,%.6f,%.6f,%.6f"
                k
                (int64 trade.KiloTicksFromBase * 1000L + header.BaseTicks)
                seq.DtSec.[k] seq.DlogP.[k] seq.Volume.[k] trade.Price
                pUp pCon pDn)
    printfn "wrote %s" outPath
    0

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Args>(programName = "TradingEdge.Hmm")
    try
        let parsed = parser.ParseCommandLine argv
        match parsed.GetSubCommand() with
        | Test _ ->
            Tests.runSyntheticTwoStateTest ()
            printfn "all tests passed."
            0
        | Infer args -> runInfer args
    with
    | :? ArguException as e ->
        printfn "%s" e.Message
        1
