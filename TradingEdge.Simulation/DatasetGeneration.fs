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
open TradingEdge.Simulation.TradeDataTDigests

let sessionToInt (s: DaySession) : int =
    match s with
    | Morning -> 0
    | DaySession.Mid -> 1
    | Close -> 2

let trendToInt (t: Trend) : int =
    match t with
    | Move (Up, Strong) -> 0
    | Move (Up, Mid) -> 1
    | Move (Up, Weak) -> 2
    | Consolidation -> 3
    | Move (Down, Weak) -> 4
    | Move (Down, Mid) -> 5
    | Move (Down, Strong) -> 6
    | Hold (Bid, Strong, Short) -> 7
    | Hold (Bid, Strong, Medium) -> 8
    | Hold (Bid, Strong, Long) -> 9
    | Hold (Bid, Mid, Short) -> 10
    | Hold (Bid, Mid, Medium) -> 11
    | Hold (Bid, Mid, Long) -> 12
    | Hold (Bid, Weak, Short) -> 13
    | Hold (Bid, Weak, Medium) -> 14
    | Hold (Bid, Weak, Long) -> 15
    | Hold (Ask, Strong, Short) -> 16
    | Hold (Ask, Strong, Medium) -> 17
    | Hold (Ask, Strong, Long) -> 18
    | Hold (Ask, Mid, Short) -> 19
    | Hold (Ask, Mid, Medium) -> 20
    | Hold (Ask, Mid, Long) -> 21
    | Hold (Ask, Weak, Short) -> 22
    | Hold (Ask, Weak, Medium) -> 23
    | Hold (Ask, Weak, Long) -> 24
    | Hold (Neutral, Strong, Short) -> 25
    | Hold (Neutral, Strong, Medium) -> 26
    | Hold (Neutral, Strong, Long) -> 27
    | Hold (Neutral, Mid, Short) -> 28
    | Hold (Neutral, Mid, Medium) -> 29
    | Hold (Neutral, Mid, Long) -> 30
    | Hold (Neutral, Weak, Short) -> 31
    | Hold (Neutral, Weak, Medium) -> 32
    | Hold (Neutral, Weak, Long) -> 33

type VolumeBar = {
    Vwap: float
    VwStd: float
    Duration: float
    Session: int
    Trend: int
}

type private BarFragment = {
    Price: float
    Volume: int
    Time: float
    Session: int
    Trend: int
}

/// Generate all trades for a full day
let generateDayTrades (rng: Random) (startPrice: float) (baseline: SessionBaseline) (digests: TradeDataDigests) (result: DayResult) : Trade[] =
    let centroids = extractCentroids digests.SizeDigest
    let totalWeight = centroids |> Array.sumBy snd
    let baselineSize = centroids |> Array.map (fun (v, w) -> v * w) |> Array.sum |> fun s -> s / totalWeight

    let allTrades = ResizeArray<Trade>()
    let mutable price = startPrice
    let mutable targetMean = log startPrice
    let mutable timeOffset = 0.0
    for sessionTrends in result.Trends do
        for episode in sessionTrends do
            let trades, endPrice, newTargetMean = generateEpisodeTrades rng price targetMean baselineSize baseline digests episode
            for t in trades do
                allTrades.Add({ t with Time = t.Time + timeOffset })
            timeOffset <- timeOffset + episode.Duration * 60.0
            price <- endPrice
            targetMean <- newTargetMean
    allTrades.ToArray()

/// Aggregate trades into fixed volume bars with trade splitting
let aggregateToVolumeBars (trades: Trade[]) (volumeSize: int) : VolumeBar[] =
    if trades.Length = 0 then [||]
    else
        let bars = ResizeArray<VolumeBar>()
        let mutable currentVolume = 0
        let barData = ResizeArray<BarFragment>()
        let mutable startTime = trades.[0].Time

        let computeBar () =
            if barData.Count > 0 then
                let totalVol = barData |> Seq.sumBy (fun f -> f.Volume)
                let vwap = (barData |> Seq.sumBy (fun f -> f.Price * float f.Volume)) / float totalVol
                let variance = (barData |> Seq.sumBy (fun f -> float f.Volume * (f.Price - vwap) * (f.Price - vwap))) / float totalVol
                let vwstd = sqrt variance
                let lastFrag = barData.[barData.Count - 1]
                let duration = lastFrag.Time - startTime
                bars.Add { Vwap = vwap; VwStd = vwstd; Duration = duration; Session = lastFrag.Session; Trend = lastFrag.Trend }

        for t in trades do
            let mutable remainingSize = t.Size
            let session = sessionToInt Morning
            let trend = trendToInt t.Trend

            while remainingSize > 0 do
                let spaceLeft = volumeSize - currentVolume

                if remainingSize <= spaceLeft then
                    barData.Add { Price = t.Price; Volume = remainingSize; Time = t.Time; Session = session; Trend = trend }
                    currentVolume <- currentVolume + remainingSize
                    remainingSize <- 0
                else
                    if spaceLeft > 0 then
                        barData.Add { Price = t.Price; Volume = spaceLeft; Time = t.Time; Session = session; Trend = trend }
                        currentVolume <- currentVolume + spaceLeft
                        remainingSize <- remainingSize - spaceLeft

                    if currentVolume >= volumeSize then
                        computeBar()
                        currentVolume <- 0
                        barData.Clear()
                        startTime <- t.Time

        if barData.Count > 0 then
            computeBar()

        bars.ToArray()

type DayData = {
    DayId: int
    Bars500: VolumeBar[]
    Bars2000: VolumeBar[]
    Bars10000: VolumeBar[]
}

let generateSingleDay
    (dayId: int)
    (seed: int)
    (mcmcConfig: MCMC.Config)
    (sessionConfig: SessionLevel.Config)
    (trendConfig: TrendLevel.Config)
    (startPrice: float)
    (digests: TradeDataDigests)
    : DayData =

    let rng = Random(seed)
    let result = generateDay sessionConfig trendConfig mcmcConfig rng 390.0
    let trades = generateDayTrades rng startPrice defaultBaseline digests result

    { DayId = dayId
      Bars500 = aggregateToVolumeBars trades 500
      Bars2000 = aggregateToVolumeBars trades 2000
      Bars10000 = aggregateToVolumeBars trades 10000 }

let datasetSchema = ParquetSchema(
    DataField<int>("day_id"),
    DataField<int>("bar_type"),
    DataField<float>("vwap"),
    DataField<float>("vwstd"),
    DataField<float>("duration"),
    DataField<int>("session"),
    DataField<int>("trend")
)

let writerTask (outputPath: string) (numDays: int) (channel: Channel<DayData>) = task {
    use stream = File.Create(outputPath)
    let! writer = ParquetWriter.CreateAsync(datasetSchema, stream)
    use writer = writer
    let mutable daysWritten = 0

    for data in channel.Reader.ReadAllAsync() do
        let fields = datasetSchema.DataFields
        for barType, bars in [(500, data.Bars500); (2000, data.Bars2000); (10000, data.Bars10000)] do
            if bars.Length > 0 then
                use rowGroup = writer.CreateRowGroup()
                let dayIds = Array.create bars.Length data.DayId
                let barTypes = Array.create bars.Length barType
                let vwaps = bars |> Array.map (fun b -> b.Vwap)
                let vwstds = bars |> Array.map (fun b -> b.VwStd)
                let durations = bars |> Array.map (fun b -> b.Duration)
                let sessions = bars |> Array.map (fun b -> b.Session)
                let trends = bars |> Array.map (fun b -> b.Trend)
                do! rowGroup.WriteColumnAsync(DataColumn(fields.[0], dayIds))
                do! rowGroup.WriteColumnAsync(DataColumn(fields.[1], barTypes))
                do! rowGroup.WriteColumnAsync(DataColumn(fields.[2], vwaps))
                do! rowGroup.WriteColumnAsync(DataColumn(fields.[3], vwstds))
                do! rowGroup.WriteColumnAsync(DataColumn(fields.[4], durations))
                do! rowGroup.WriteColumnAsync(DataColumn(fields.[5], sessions))
                do! rowGroup.WriteColumnAsync(DataColumn(fields.[6], trends))
        daysWritten <- daysWritten + 1
        if daysWritten % 100 = 0 then
            printfn "  Written %d / %d days" daysWritten numDays
}

let generatorTask
    (workerId: int) (numWorkers: int) (numDays: int) (baseSeed: int)
    (mcmcConfig: MCMC.Config) (sessionConfig: SessionLevel.Config)
    (trendConfig: TrendLevel.Config) (startPrice: float)
    (digests: TradeDataDigests)
    (channel: Channel<DayData>) = task {
    let mutable dayId = workerId
    while dayId < numDays do
        let data = generateSingleDay dayId (baseSeed + dayId) mcmcConfig sessionConfig trendConfig startPrice digests
        do! channel.Writer.WriteAsync(data)
        dayId <- dayId + numWorkers
}

let generateDataset
    (baseSeed: int) (numDays: int) (outputPath: string)
    (mcmcConfig: MCMC.Config) (sessionConfig: SessionLevel.Config)
    (trendConfig: TrendLevel.Config) (startPrice: float)
    (digestsPath: string) : unit =

    printfn "Loading t-digests from %s..." digestsPath
    let digests = loadTDigests digestsPath

    let numWorkers = Environment.ProcessorCount
    printfn "Generating %d days with %d workers..." numDays numWorkers

    let dir = Path.GetDirectoryName(outputPath)
    if not (String.IsNullOrEmpty(dir)) && not (Directory.Exists(dir)) then
        Directory.CreateDirectory(dir) |> ignore

    let channel = Channel.CreateBounded<DayData>(BoundedChannelOptions(numWorkers * 2))

    let writer = writerTask outputPath numDays channel

    let generators =
        [| for w in 0 .. numWorkers - 1 ->
            Task.Run(Func<Task>(fun () ->
                generatorTask w numWorkers numDays baseSeed mcmcConfig sessionConfig trendConfig startPrice digests channel)) |]

    Task.WhenAll(generators).ContinueWith(fun (_: Task) -> channel.Writer.Complete()).Wait()
    writer.Wait()

    printfn "Done. Wrote dataset to %s" outputPath
