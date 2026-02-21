module TradingEdge.Simulation.DatasetGeneration

open System
open System.IO
open System.Threading.Tasks
open System.Threading.Channels
open Parquet
open Parquet.Schema
open Parquet.Data
open FSharp.Control
open TradingEdge.Simulation.EpisodeMCMC
open TradingEdge.Simulation.OrderFlowGeneration

let sessionToInt (s: DaySession) : int =
    match s with
    | Morning -> 0
    | Mid -> 1
    | Close -> 2

let trendToInt (t: Trend) : int =
    match t with
    | StrongUptrend -> 0
    | MidUptrend -> 1
    | WeakUptrend -> 2
    | Consolidation -> 3
    | WeakDowntrend -> 4
    | MidDowntrend -> 5
    | StrongDowntrend -> 6

type SecondBar = {
    Vwap: float
    Volume: int
    StdDev: float
    Session: int
    Trend: int
}

/// Generate all trades for a full day, chaining episode end prices
let generateDayTrades (rng: Random) (startPrice: float) (result: DayResult) : Trade[] =
    let allTrades = ResizeArray<Trade>()
    let mutable price = startPrice
    let mutable timeOffset = 0.0
    for sessionTrends in result.Trends do
        for episode in sessionTrends do
            let trades, endPrice = generateEpisodeTrades rng price episode
            for t in trades do
                allTrades.Add({ t with Time = t.Time + timeOffset })
            timeOffset <- timeOffset + episode.Duration * 60.0
            price <- endPrice
    allTrades.ToArray()

/// Aggregate trades into 1-second bars
let aggregateToSecondBars (trades: Trade[]) (totalSeconds: int) : SecondBar[] =
    let bars = Array.init totalSeconds (fun _ -> { Vwap = 0.0; Volume = 0; StdDev = 0.0; Session = 0; Trend = 0 })
    
    // Group trades by second
    let buckets = Array.init totalSeconds (fun _ -> ResizeArray<Trade>())
    for t in trades do
        let sec = int t.Time |> min (totalSeconds - 1) |> max 0
        buckets.[sec].Add(t)
    
    let mutable lastVwap = if trades.Length > 0 then trades.[0].Price else 0.0
    
    for sec in 0 .. totalSeconds - 1 do
        let bucket = buckets.[sec]
        if bucket.Count = 0 then
            bars.[sec] <- { Vwap = lastVwap; Volume = 0; StdDev = 0.0; Session = 0; Trend = 0 }
        else
            let totalVol = bucket |> Seq.sumBy (fun t -> t.Size)
            let vwap = (bucket |> Seq.sumBy (fun t -> float t.Size * t.Price)) / float totalVol
            let variance =
                if totalVol > 0 then
                    (bucket |> Seq.sumBy (fun t -> float t.Size * (t.Price - vwap) ** 2.0)) / float totalVol
                else 0.0
            let lastTrade = bucket.[bucket.Count - 1]
            bars.[sec] <- { Vwap = vwap; Volume = totalVol; StdDev = sqrt variance; Session = sessionToInt Morning; Trend = trendToInt lastTrade.Trend }
            lastVwap <- vwap
    bars

/// Assign session/trend labels to second bars based on episode structure
let assignLabels (bars: SecondBar[]) (result: DayResult) : unit =
    let mutable sec = 0
    for i in 0 .. result.Sessions.Length - 1 do
        let session = result.Sessions.[i]
        let sessionInt = sessionToInt session.Label
        for episode in result.Trends.[i] do
            let trendInt = trendToInt episode.Label
            let endSec = sec + int (episode.Duration * 60.0)
            for s in sec .. min (endSec - 1) (bars.Length - 1) do
                bars.[s] <- { bars.[s] with Session = sessionInt; Trend = trendInt }
            sec <- endSec

type DayData = {
    DayId: int
    DayIds: int[]
    Times: int[]
    Sessions: int[]
    Trends: int[]
    Vwap1s: float[]
    Volume1s: int[]
    StdDev1s: float[]
    Vwap1m: float[]
    Volume1m: int[]
    StdDev1m: float[]
    Vwap5m: float[]
    Volume5m: int[]
    StdDev5m: float[]
}

/// Combine 1s bars into period bars (1m, 5m) using online weighted variance (West 1979)
let combineBars (bars: SecondBar[]) (periodSeconds: int) : (float * int * float)[] =
    let n = bars.Length
    let result = Array.zeroCreate n
    let mutable mean = 0.0
    let mutable s = 0.0
    let mutable vol = 0
    
    for i in 0 .. n - 1 do
        if i % periodSeconds = 0 then
            mean <- 0.0
            s <- 0.0
            vol <- 0
        
        let b = bars.[i]
        if b.Volume > 0 then
            vol <- vol + b.Volume
            let delta = b.Vwap - mean
            let w = float b.Volume
            mean <- mean + delta * w / float vol
            s <- s + w * (b.StdDev * b.StdDev + delta * (b.Vwap - mean))
        
        let stdDev = sqrt(s / float vol)
        result.[i] <- mean, vol, stdDev
    
    result

let barsPerDay = 390 * 60

let generateSingleDay
    (dayId: int)
    (seed: int)
    (mcmcConfig: MCMC.Config)
    (sessionConfig: SessionLevel.Config)
    (trendConfig: TrendLevel.Config)
    (startPrice: float)
    : DayData =

    let rng = Random(seed)
    let result = generateDay sessionConfig trendConfig mcmcConfig rng 390.0
    let trades = generateDayTrades rng startPrice result
    let bars = aggregateToSecondBars trades barsPerDay
    assignLabels bars result

    let agg1m = combineBars bars 60
    let agg5m = combineBars bars 300

    { DayId = dayId
      DayIds = Array.create barsPerDay dayId
      Times = Array.init barsPerDay id
      Sessions = bars |> Array.map (fun b -> b.Session)
      Trends = bars |> Array.map (fun b -> b.Trend)
      Vwap1s = bars |> Array.map (fun b -> b.Vwap)
      Volume1s = bars |> Array.map (fun b -> b.Volume)
      StdDev1s = bars |> Array.map (fun b -> b.StdDev)
      Vwap1m = agg1m |> Array.map (fun (v,_,_) -> v)
      Volume1m = agg1m |> Array.map (fun (_,v,_) -> v)
      StdDev1m = agg1m |> Array.map (fun (_,_,s) -> s)
      Vwap5m = agg5m |> Array.map (fun (v,_,_) -> v)
      Volume5m = agg5m |> Array.map (fun (_,v,_) -> v)
      StdDev5m = agg5m |> Array.map (fun (_,_,s) -> s) }

let datasetSchema = ParquetSchema(
    DataField<int>("day_id"),
    DataField<int>("time"),
    DataField<int>("session"),
    DataField<int>("trend"),
    DataField<float>("vwap_1s"),
    DataField<int>("volume_1s"),
    DataField<float>("stddev_1s"),
    DataField<float>("vwap_1m"),
    DataField<int>("volume_1m"),
    DataField<float>("stddev_1m"),
    DataField<float>("vwap_5m"),
    DataField<int>("volume_5m"),
    DataField<float>("stddev_5m")
)

let writerTask (outputPath: string) (numDays: int) (channel: Channel<DayData>) = task {
    use stream = File.Create(outputPath)
    let! writer = ParquetWriter.CreateAsync(datasetSchema, stream)
    use writer = writer
    let mutable daysWritten = 0

    for data in channel.Reader.ReadAllAsync() do
        let fields = datasetSchema.DataFields
        use rowGroup = writer.CreateRowGroup()
        do! rowGroup.WriteColumnAsync(DataColumn(fields.[0], data.DayIds))
        do! rowGroup.WriteColumnAsync(DataColumn(fields.[1], data.Times))
        do! rowGroup.WriteColumnAsync(DataColumn(fields.[2], data.Sessions))
        do! rowGroup.WriteColumnAsync(DataColumn(fields.[3], data.Trends))
        do! rowGroup.WriteColumnAsync(DataColumn(fields.[4], data.Vwap1s))
        do! rowGroup.WriteColumnAsync(DataColumn(fields.[5], data.Volume1s))
        do! rowGroup.WriteColumnAsync(DataColumn(fields.[6], data.StdDev1s))
        do! rowGroup.WriteColumnAsync(DataColumn(fields.[7], data.Vwap1m))
        do! rowGroup.WriteColumnAsync(DataColumn(fields.[8], data.Volume1m))
        do! rowGroup.WriteColumnAsync(DataColumn(fields.[9], data.StdDev1m))
        do! rowGroup.WriteColumnAsync(DataColumn(fields.[10], data.Vwap5m))
        do! rowGroup.WriteColumnAsync(DataColumn(fields.[11], data.Volume5m))
        do! rowGroup.WriteColumnAsync(DataColumn(fields.[12], data.StdDev5m))
        daysWritten <- daysWritten + 1
        if daysWritten % 500 = 0 then
            printfn "  Written %d / %d days" daysWritten numDays
}

let generatorTask
    (workerId: int) (numWorkers: int) (numDays: int) (baseSeed: int)
    (mcmcConfig: MCMC.Config) (sessionConfig: SessionLevel.Config)
    (trendConfig: TrendLevel.Config) (startPrice: float)
    (channel: Channel<DayData>) = task {
    let mutable dayId = workerId
    while dayId < numDays do
        let data = generateSingleDay dayId (baseSeed + dayId) mcmcConfig sessionConfig trendConfig startPrice
        do! channel.Writer.WriteAsync(data)
        dayId <- dayId + numWorkers
}

let generateDataset
    (baseSeed: int) (numDays: int) (outputPath: string)
    (mcmcConfig: MCMC.Config) (sessionConfig: SessionLevel.Config)
    (trendConfig: TrendLevel.Config) (startPrice: float) : unit =

    let numWorkers = Environment.ProcessorCount
    printfn "Generating %d days (%d bars) with %d workers..." numDays (numDays * barsPerDay) numWorkers

    let dir = Path.GetDirectoryName(outputPath)
    if not (String.IsNullOrEmpty(dir)) && not (Directory.Exists(dir)) then
        Directory.CreateDirectory(dir) |> ignore

    let channel = Channel.CreateBounded<DayData>(BoundedChannelOptions(numWorkers * 2))

    let writer = writerTask outputPath numDays channel

    let generators =
        [| for w in 0 .. numWorkers - 1 ->
            Task.Run(Func<Task>(fun () ->
                generatorTask w numWorkers numDays baseSeed mcmcConfig sessionConfig trendConfig startPrice channel)) |]

    Task.WhenAll(generators).ContinueWith(fun (_: Task) -> channel.Writer.Complete()).Wait()
    writer.Wait()

    printfn "Done. Wrote %d bars to %s" (numDays * barsPerDay) outputPath
