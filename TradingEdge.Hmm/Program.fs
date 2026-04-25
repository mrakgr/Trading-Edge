module TradingEdge.Hmm.Program

open System
open System.IO
open Argu
open TradingEdge.Hmm.BinanceLoader
open TradingEdge.Hmm.BtcModel

type InferArgs =
    | [<Mandatory; AltCommandLine("-i")>] Input of string
    | [<AltCommandLine("-o")>] Output of string
    | [<AltCommandLine("-l")>] Lambda of float
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Input _ -> "Path to a Binance trade CSV (e.g. BTCUSDT-trades-2026-02-05.csv)"
            | Output _ -> "Output CSV path (default: logs/<input-base>_hmm.csv)"
            | Lambda _ -> "Per-unit-volume log-odds weight (overrides default)"

type TestArgs =
    | Placeholder
    interface IArgParserTemplate with
        member this.Usage = "(internal)"

type Args =
    | [<CliPrefix(CliPrefix.None)>] Test of ParseResults<TestArgs>
    | [<CliPrefix(CliPrefix.None)>] Infer of ParseResults<InferArgs>
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Test _ -> "Run synthetic-data unit tests"
            | Infer _ -> "Run forward-backward on a Binance trade CSV"

let runInfer (args: ParseResults<InferArgs>) =
    let inputPath = args.GetResult Input
    let baseName = Path.GetFileNameWithoutExtension inputPath
    let outPath = args.GetResult(Output, defaultValue = sprintf "logs/%s_hmm.csv" baseName)

    printfn "loading %s ..." inputPath
    let swLoad = System.Diagnostics.Stopwatch.StartNew()
    let trades = load inputPath
    swLoad.Stop()
    printfn "  %d trades loaded in %d ms" trades.Length swLoad.ElapsedMilliseconds

    let seq = buildSequence trades
    let t = seq.DtSec.Length
    printfn "sequence length: %d" t

    let p = defaultParams
    let p =
        match args.TryGetResult Lambda with
        | Some l -> { p with Lambda = l }
        | None -> p
    printfn "λ = %g" p.Lambda

    let sw = System.Diagnostics.Stopwatch.StartNew()
    let out = infer p seq
    sw.Stop()
    printfn "forward-backward: %d ms, loglik = %g" sw.ElapsedMilliseconds out.LogLikelihood

    Directory.CreateDirectory(Path.GetDirectoryName outPath) |> ignore
    use w = new StreamWriter(outPath)
    // Output is aligned 1:1 with the input trades, starting from index 1
    // (the first trade has no predecessor, so no Δt or sign was computed).
    // Each row carries both the smoothed posterior P(s | x_{1:T}) and the
    // filtered posterior P(s | x_{1:t}). Filtered is what a real-time system
    // would see; smoothed cheats with future data and is for analysis only.
    w.WriteLine("p_up,p_consol,p_down,p_up_filt,p_consol_filt,p_down_filt")
    for k in 0 .. t - 1 do
        let pUp = exp out.LogGamma.[0, k]
        let pCon = exp out.LogGamma.[1, k]
        let pDn = exp out.LogGamma.[2, k]
        let pUpF = exp out.LogGammaFiltered.[0, k]
        let pConF = exp out.LogGammaFiltered.[1, k]
        let pDnF = exp out.LogGammaFiltered.[2, k]
        w.WriteLine(sprintf "%.6f,%.6f,%.6f,%.6f,%.6f,%.6f" pUp pCon pDn pUpF pConF pDnF)
    printfn "wrote %s" outPath
    0

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Args>(programName = "TradingEdge.Hmm")
    try
        let parsed = parser.ParseCommandLine argv
        match parsed.GetSubCommand() with
        | Test _ ->
            Tests.runSyntheticBernoulliTest ()
            printfn "all tests passed."
            0
        | Infer args -> runInfer args
    with
    | :? ArguException as e ->
        printfn "%s" e.Message
        1
