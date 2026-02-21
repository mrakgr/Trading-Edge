open System
open Argu
open TradingEdge.Simulation.OrderBook
open TradingEdge.Simulation.EpisodeMCMC
open TradingEdge.Simulation.TDigestProcessing
open TradingEdge.Simulation.DatasetGeneration

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

type GenerateDatasetArgs =
    | [<AltCommandLine("-s")>] Seed of int
    | [<AltCommandLine("-n")>] Days of int
    | [<AltCommandLine("-o")>] Output of string
    | [<AltCommandLine("-p")>] Price of float
    | [<AltCommandLine("-i")>] Iterations of int
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Seed _ -> "Random seed for generation"
            | Days _ -> "Number of days to generate"
            | Output _ -> "Output parquet file path"
            | Price _ -> "Starting price (default: 100.0)"
            | Iterations _ -> "MCMC iterations per level"

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

type Command =
    | [<CliPrefix(CliPrefix.None)>] Order_Book of ParseResults<OrderBookArgs>
    | [<CliPrefix(CliPrefix.None)>] Generate_Day of ParseResults<GenerateDayArgs>
    | [<CliPrefix(CliPrefix.None)>] Generate_Dataset of ParseResults<GenerateDatasetArgs>
    | [<CliPrefix(CliPrefix.None)>] Preprocess of ParseResults<PreprocessArgs>
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Order_Book _ -> "Generate and display an order book"
            | Generate_Day _ -> "Generate a full day with sessions and trends using MCMC"
            | Generate_Dataset _ -> "Generate dataset of VWAP/Volume/StdDev bars to parquet"
            | Preprocess _ -> "Apply t-digest CDF transform to raw dataset"

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
    let mcmcConfig = { MCMC.Iterations = iterations }
    generateDataset seed numDays output mcmcConfig SessionLevel.defaultConfig TrendLevel.defaultConfig startPrice

let runPreprocess (args: ParseResults<PreprocessArgs>) =
    let input = args.GetResult(PreprocessArgs.Input)
    let output = args.GetResult(PreprocessArgs.Output)
    let tdigestPath = args.TryGetResult(PreprocessArgs.Tdigest)
    let compression = args.GetResult(PreprocessArgs.Compression, defaultCompression)
    let numWorkers = args.GetResult(PreprocessArgs.Workers, Environment.ProcessorCount)
    
    let tds = 
        match tdigestPath with
        | Some path ->
            printfn "Loading t-digests from %s..." path
            loadTDigests path
        | None ->
            let path = input + ".tdigests"
            let tds = (buildTDigestsFromParquet input compression numWorkers).Result
            saveTDigests tds path
            tds
    
    (transformParquetWithCdf input tds output numWorkers).Wait()

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Command>(programName = "TradingEdge.Simulation")

    try
        let results = parser.ParseCommandLine(inputs = argv, raiseOnUsage = true)

        match results.GetSubCommand() with
        | Order_Book args -> runOrderBook args
        | Generate_Day args -> runGenerateDay args
        | Generate_Dataset args -> runGenerateDataset args
        | Preprocess args -> runPreprocess args

        0
    with
    | :? ArguParseException as e ->
        printfn "%s" e.Message
        1
