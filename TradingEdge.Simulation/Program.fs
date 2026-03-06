open System
open Argu
open TradingEdge.Simulation.OrderBook
open TradingEdge.Simulation.EpisodeMCMC
open TradingEdge.Simulation.DatasetGeneration
open TradingEdge.Simulation.OrderFlowGeneration

type OrderBookArgs =
    | [<AltCommandLine("-s")>] Seed of int
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Seed _ -> "Random seed for generation"

type GenerateDayArgs =
    | [<AltCommandLine("-s")>] Seed of int
    | [<AltCommandLine("-n")>] Runs of int
    | [<AltCommandLine("-i")>] Iterations of int
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Seed _ -> "Random seed for generation"
            | Runs _ -> "Number of days to generate"
            | Iterations _ -> "MCMC iterations per level"

type BuildDigestsArgs =
    | [<AltCommandLine("-i")>] Input of string
    | [<AltCommandLine("-o")>] Output of string
    | [<AltCommandLine("-c")>] Compression of float
    | [<AltCommandLine("-t")>] Threshold of int64
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Input _ -> "Input JSON trade file path"
            | Output _ -> "Output t-digests file path"
            | Compression _ -> "T-digest compression factor (default: 4096)"
            | Threshold _ -> "Merge threshold in nanoseconds (default: 100000)"

type GenerateDatasetArgs =
    | [<AltCommandLine("-s")>] Seed of int
    | [<AltCommandLine("-n")>] Days of int
    | [<AltCommandLine("-o")>] Output of string
    | [<AltCommandLine("-p")>] Price of float
    | [<AltCommandLine("-i")>] Iterations of int
    | [<Mandatory; AltCommandLine("-d")>] Digests of string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Seed _ -> "Random seed for generation"
            | Days _ -> "Number of days to generate"
            | Output _ -> "Output parquet file path"
            | Price _ -> "Starting price (default: 100.0)"
            | Iterations _ -> "MCMC iterations per level"
            | Digests _ -> "T-digests file path for trade sizes and gaps"

type PreprocessArgs =
    | [<AltCommandLine("-i")>] Input of string
    | [<AltCommandLine("-o")>] Output of string
    | [<AltCommandLine("-t")>] Tdigest of string
    | [<AltCommandLine("-c")>] Compression of float
    | [<AltCommandLine("-w")>] Workers of int
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Input _ -> "Input raw parquet file path"
            | Output _ -> "Output CDF-transformed parquet file path"
            | Tdigest _ -> "Use existing t-digest file (default: build from input)"
            | Compression _ -> "T-digest compression factor (default: 4096)"
            | Workers _ -> "Number of worker threads (default: CPU count)"

type DumpTradesArgs =
    | [<AltCommandLine("-s")>] Seed of int
    | [<AltCommandLine("-o")>] Output of string
    | [<AltCommandLine("-p")>] Price of float
    | [<AltCommandLine("-i")>] Iterations of int
    | [<Mandatory; AltCommandLine("-d")>] Digests of string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Seed _ -> "Random seed for generation"
            | Output _ -> "Output CSV file path (default: stdout)"
            | Price _ -> "Starting price (default: 100.0)"
            | Iterations _ -> "MCMC iterations per level"
            | Digests _ -> "T-digests file path for trade sizes and gaps"

type Command =
    | [<CliPrefix(CliPrefix.None)>] Order_Book of ParseResults<OrderBookArgs>
    | [<CliPrefix(CliPrefix.None)>] Generate_Day of ParseResults<GenerateDayArgs>
    | [<CliPrefix(CliPrefix.None)>] Generate_Dataset of ParseResults<GenerateDatasetArgs>
    | [<CliPrefix(CliPrefix.None)>] Build_Digests of ParseResults<BuildDigestsArgs>
    | [<CliPrefix(CliPrefix.None)>] Preprocess of ParseResults<PreprocessArgs>
    | [<CliPrefix(CliPrefix.None)>] Dump_Trades of ParseResults<DumpTradesArgs>
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Order_Book _ -> "Generate and display an order book"
            | Generate_Day _ -> "Generate a full day with sessions and trends using MCMC"
            | Generate_Dataset _ -> "Generate dataset of volume bars to parquet"
            | Build_Digests _ -> "Build t-digests from JSON trade data"
            | Preprocess _ -> "Apply t-digest CDF transform to raw dataset"
            | Dump_Trades _ -> "Dump raw trade data for a single day as CSV"

let runOrderBook (args: ParseResults<OrderBookArgs>) =
    let seed = args.GetResult(OrderBookArgs.Seed, 42)
    let rng = Random(seed)

    let limitParams = { Limit = 99.95; SizeMean = 200.0; SizeStdDev = 150.0 }
    let distParams = { DistanceMean = 0.10; DistanceStdDev = 0.15 }

    let config = {
        TickSize = 0.01
        Bid = { LevelCount = 20; LevelParams = { limitParams with Limit = 99.95 }; DistanceParams = distParams }
        Ask = { LevelCount = 20; LevelParams = { limitParams with Limit = 100.00 }; DistanceParams = distParams }
    }

    let book = generate config rng
    print book

let runGenerateDay (args: ParseResults<GenerateDayArgs>) =
    let seed = args.GetResult(GenerateDayArgs.Seed, 42)
    let runs = args.GetResult(GenerateDayArgs.Runs, 1)
    let iterations = args.GetResult(GenerateDayArgs.Iterations, 10000)
    let rng = Random(seed)

    let mcmcConfig = { MCMC.Iterations = iterations }
    let sessionConfig = SessionLevel.defaultConfig
    let trendConfig = TrendLevel.defaultConfig

    printfn "Day Generation (MCMC iterations=%d)" iterations
    printfn ""

    for i in 1 .. runs do
        if runs > 1 then printfn "=== Day %d ===" i
        let result = generateDay sessionConfig trendConfig mcmcConfig rng 390.0
        printDayResult result
        if i < runs then printfn "---"
        printfn ""

let runGenerateDataset (args: ParseResults<GenerateDatasetArgs>) =
    let seed = args.GetResult(GenerateDatasetArgs.Seed, 42)
    let numDays = args.GetResult(GenerateDatasetArgs.Days, 1000)
    let output = args.GetResult(GenerateDatasetArgs.Output, "data/dataset.parquet")
    let startPrice = args.GetResult(GenerateDatasetArgs.Price, 100.0)
    let iterations = args.GetResult(GenerateDatasetArgs.Iterations, 10000)
    let digestsPath = args.GetResult(GenerateDatasetArgs.Digests)
    let mcmcConfig = { MCMC.Iterations = iterations }
    generateDataset seed numDays output mcmcConfig SessionLevel.defaultConfig TrendLevel.defaultConfig startPrice digestsPath

let runBuildDigests (args: ParseResults<BuildDigestsArgs>) =
    let input = args.GetResult(BuildDigestsArgs.Input)
    let output = args.GetResult(BuildDigestsArgs.Output)
    let compression = args.GetResult(BuildDigestsArgs.Compression, 4096.0)
    let threshold = args.GetResult(BuildDigestsArgs.Threshold, 100000L)
    let digests = TradingEdge.Simulation.TradeDataTDigests.buildTDigestsFromJson input compression threshold
    TradingEdge.Simulation.TradeDataTDigests.saveTDigests digests output

let runDumpTrades (args: ParseResults<DumpTradesArgs>) =
    let seed = args.GetResult(DumpTradesArgs.Seed, 42)
    let startPrice = args.GetResult(DumpTradesArgs.Price, 100.0)
    let iterations = args.GetResult(DumpTradesArgs.Iterations, 10000)
    let digestsPath = args.GetResult(DumpTradesArgs.Digests)
    let outputPath = args.TryGetResult(DumpTradesArgs.Output)
    let rng = Random(seed)
    let mcmcConfig = { MCMC.Iterations = iterations }
    let result = generateDay SessionLevel.defaultConfig TrendLevel.defaultConfig mcmcConfig rng 390.0
    let digests = TradingEdge.Simulation.TradeDataTDigests.loadTDigests digestsPath
    let trades = generateDayTrades rng startPrice defaultBaseline digests result

    let writer : System.IO.TextWriter =
        match outputPath with
        | Some path -> new System.IO.StreamWriter(path) :> _
        | None -> System.Console.Out

    writer.WriteLine("time,dt,price,size,trend,target_mean,target_sigma")
    let mutable prevTime = 0.0
    for t in trades do
        let dt = t.Time - prevTime
        writer.WriteLine(sprintf "%.6f,%.6f,%.6f,%d,%s,%.6f,%.6f" t.Time dt t.Price t.Size (showTrend t.Trend) t.TargetMean t.TargetSigma)
        prevTime <- t.Time

    match outputPath with
    | Some path ->
        (writer :?> System.IO.StreamWriter).Dispose()
        printfn "Wrote %d trades to %s" trades.Length path
    | None -> ()

let runPreprocess (args: ParseResults<PreprocessArgs>) =
    let input = args.GetResult(PreprocessArgs.Input)
    let output = args.GetResult(PreprocessArgs.Output)
    let tdigestPath = args.TryGetResult(PreprocessArgs.Tdigest)
    let compression = args.GetResult(PreprocessArgs.Compression, TradingEdge.Simulation.TDigestProcessing.defaultCompression)
    let numWorkers = args.GetResult(PreprocessArgs.Workers, Environment.ProcessorCount)

    let tds =
        match tdigestPath with
        | Some path ->
            printfn "Loading t-digests from %s..." path
            TradingEdge.Simulation.TDigestProcessing.loadTDigests path
        | None ->
            let path = input + ".tdigests"
            let tds = (TradingEdge.Simulation.TDigestProcessing.buildTDigestsFromParquet input compression numWorkers).Result
            TradingEdge.Simulation.TDigestProcessing.saveTDigests tds path
            tds

    (TradingEdge.Simulation.TDigestProcessing.transformParquetWithCdf input tds output numWorkers).Wait()

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Command>(programName = "TradingEdge.Simulation")

    try
        let results = parser.ParseCommandLine(inputs = argv, raiseOnUsage = true)

        match results.GetSubCommand() with
        | Order_Book args -> runOrderBook args
        | Generate_Day args -> runGenerateDay args
        | Generate_Dataset args -> runGenerateDataset args
        | Build_Digests args -> runBuildDigests args
        | Preprocess args -> runPreprocess args
        | Dump_Trades args -> runDumpTrades args

        0
    with
    | :? ArguParseException as e ->
        printfn "%s" e.Message
        1
