module TradingEdge.Hmm.Program

open System
open System.IO
open Argu
open TradingEdge.Hmm.BinanceLoader

module Btc = TradingEdge.Hmm.BtcModel
module Hold = TradingEdge.Hmm.HoldModel
module Bars = TradingEdge.Hmm.VolumeBar

type InferArgs =
    | [<Mandatory; AltCommandLine("-i")>] Input of string
    | [<AltCommandLine("-o")>] Output of string
    | [<AltCommandLine("-l")>] Lambda of float
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Input _ -> "Path to a Binance trade file (.csv or .bin)"
            | Output _ -> "Output CSV path (default: logs/<input-base>_hmm.csv)"
            | Lambda _ -> "Per-unit-volume log-odds weight (overrides default)"

type InferHoldArgs =
    | [<Mandatory; AltCommandLine("-i")>] Input of string
    | [<AltCommandLine("-o")>] Output of string
    | [<AltCommandLine("-b")>] BarSize of float
    | [<AltCommandLine("-w")>] BaselineWindow of int
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Input _ -> "Path to a Binance trade file (.csv or .bin)"
            | Output _ -> "Output CSV path (default: logs/<input-base>_hold.csv)"
            | BarSize _ -> "Volume bar size (default: 18.0)"
            | BaselineWindow _ -> "Rolling baseline window in bars (default: 100)"

type ConvertArgs =
    | [<Mandatory; AltCommandLine("-i")>] Input of string
    | [<AltCommandLine("-o")>] Output of string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Input _ -> "Path to a Binance trade CSV"
            | Output _ -> "Output binary path (default: same dir, .bin extension)"

type TestArgs =
    | Placeholder
    interface IArgParserTemplate with
        member this.Usage = "(internal)"

type Args =
    | [<CliPrefix(CliPrefix.None)>] Test of ParseResults<TestArgs>
    | [<CliPrefix(CliPrefix.None)>] Infer of ParseResults<InferArgs>
    | [<CliPrefix(CliPrefix.None)>] InferHold of ParseResults<InferHoldArgs>
    | [<CliPrefix(CliPrefix.None)>] Convert of ParseResults<ConvertArgs>
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Test _ -> "Run synthetic-data unit tests"
            | Infer _ -> "Run forward-backward on a Binance trade file"
            | InferHold _ -> "Run the bar-level hold detector on a Binance trade file"
            | Convert _ -> "Convert a Binance trade CSV to packed binary"

let runInfer (args: ParseResults<InferArgs>) =
    let inputPath = args.GetResult <@ InferArgs.Input @>
    let baseName = Path.GetFileNameWithoutExtension(inputPath: string)
    let outPath = args.GetResult(<@ InferArgs.Output @>, defaultValue = sprintf "logs/%s_hmm.csv" baseName)

    printfn "loading %s ..." inputPath
    let swLoad = System.Diagnostics.Stopwatch.StartNew()
    let trades = load inputPath
    swLoad.Stop()
    printfn "  %d trades loaded in %d ms" trades.Length swLoad.ElapsedMilliseconds

    let seq = Btc.buildSequence trades
    let t = seq.DtSec.Length
    printfn "sequence length: %d" t

    let p = Btc.defaultParams
    let p =
        match args.TryGetResult Lambda with
        | Some l -> { p with Lambda = l }
        | None -> p
    printfn "λ = %g" p.Lambda

    let sw = System.Diagnostics.Stopwatch.StartNew()
    let out = Btc.infer p seq
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

let runInferHold (args: ParseResults<InferHoldArgs>) =
    let inputPath = args.GetResult <@ InferHoldArgs.Input @>
    let baseName = Path.GetFileNameWithoutExtension(inputPath: string)
    let outPath = args.GetResult(<@ InferHoldArgs.Output @>, defaultValue = sprintf "logs/%s_hold.csv" baseName)
    let barSize = args.GetResult(<@ InferHoldArgs.BarSize @>, defaultValue = 18.0)
    let window = args.GetResult(<@ InferHoldArgs.BaselineWindow @>, defaultValue = Hold.DefaultBaselineWindow)

    printfn "loading %s ..." inputPath
    let swLoad = System.Diagnostics.Stopwatch.StartNew()
    let trades = load inputPath
    swLoad.Stop()
    printfn "  %d trades loaded in %d ms" trades.Length swLoad.ElapsedMilliseconds

    printfn "building %g-BTC volume bars ..." barSize
    let swBars = System.Diagnostics.Stopwatch.StartNew()
    let bars = Bars.buildBars barSize trades
    swBars.Stop()
    printfn "  %d bars in %d ms" bars.Length swBars.ElapsedMilliseconds

    let seq = Hold.buildSequence window bars
    printfn "observations: %d (after %d-bar baseline)" seq.Obs.Length window

    let p = { Hold.defaultParams with BaselineWindow = window }

    let sw = System.Diagnostics.Stopwatch.StartNew()
    let out = Hold.infer p seq
    sw.Stop()
    printfn "forward-backward: %d ms, loglik = %g" sw.ElapsedMilliseconds out.LogLikelihood

    Directory.CreateDirectory(Path.GetDirectoryName outPath) |> ignore
    use w = new StreamWriter(outPath)
    // Self-contained bar+posterior CSV. Each row carries enough bar metadata
    // for the python chart to plot directly (no bar rebuild on the python side
    // → no alignment risk with the F# splitting logic). bar_index is the index
    // into the FULL VolumeBar[] (the first BaselineWindow bars are skipped).
    // Smoothed = full forward-backward; filtered = forward-only, i.e. what a
    // real-time system would have available at that bar.
    w.WriteLine(
        "bar_index,start_us,end_us,vwap,stddev,volume,high,low,n_trades,k_buys,"
        + "p_hold,p_fakeout,p_trend,p_hold_filt,p_fakeout_filt,p_trend_filt")
    for k in 0 .. seq.Obs.Length - 1 do
        let i = seq.BarIndex.[k]
        let bar = bars.[i]
        let pH = exp out.LogGamma.[Hold.HOLD, k]
        let pF = exp out.LogGamma.[Hold.FAKEOUT, k]
        let pT = exp out.LogGamma.[Hold.TREND, k]
        let pHf = exp out.LogGammaFiltered.[Hold.HOLD, k]
        let pFf = exp out.LogGammaFiltered.[Hold.FAKEOUT, k]
        let pTf = exp out.LogGammaFiltered.[Hold.TREND, k]
        w.WriteLine(
            sprintf "%d,%d,%d,%.4f,%.6f,%.4f,%.4f,%.4f,%d,%d,%.6f,%.6f,%.6f,%.6f,%.6f,%.6f"
                i bar.StartUs bar.EndUs bar.VWAP bar.StdDev bar.Volume
                bar.High bar.Low bar.TradeCount bar.BuyCount
                pH pF pT pHf pFf pTf)
    printfn "wrote %s" outPath
    0

let runConvert (args: ParseResults<ConvertArgs>) =
    let inputPath = args.GetResult <@ ConvertArgs.Input @>
    let defaultOut = Path.ChangeExtension(inputPath, ".bin")
    let outPath = args.GetResult(<@ ConvertArgs.Output @>, defaultValue = defaultOut)

    printfn "loading %s ..." inputPath
    let swLoad = System.Diagnostics.Stopwatch.StartNew()
    let trades = loadCsv inputPath
    swLoad.Stop()
    printfn "  %d trades loaded in %d ms" trades.Length swLoad.ElapsedMilliseconds

    Directory.CreateDirectory(Path.GetDirectoryName(outPath: string)) |> ignore
    let swWrite = System.Diagnostics.Stopwatch.StartNew()
    writeBinary outPath trades
    swWrite.Stop()
    let info = FileInfo outPath
    printfn "wrote %s (%.1f MB) in %d ms"
        outPath (float info.Length / 1.0e6) swWrite.ElapsedMilliseconds
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
        | InferHold args -> runInferHold args
        | Convert args -> runConvert args
    with
    | :? ArguException as e ->
        printfn "%s" e.Message
        1
