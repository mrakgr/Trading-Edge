module TradingEdge.Simulation.DatasetGeneration

open System
open System.IO
open System.Threading.Tasks
open System.Threading.Channels
open Parquet
open Parquet.Schema
open Parquet.Data
open FSharp.Control
open TradingEdge.Simulation.SessionDuration
// open TradingEdge.Simulation.OrderFlowGeneration
open TradingEdge.Simulation.TradeDataTDigests

// let sessionToInt (s: DaySession) : int =
//     match s with
//     | Morning -> 0
//     | DaySession.Mid -> 1
//     | Close -> 2

type VolumeBar = {
    Vwap: float
    VwStd: float
    Duration: float
    Session: int
}

type private BarFragment = {
    Price: float
    Volume: int
    Time: float
    Session: int
}

// /// Generate all trades for a full day
// let generateDayTrades (rng: Random) (startPrice: float) (baseline: SessionBaseline) (digests: TradeDataDigests) (result: DayResult) : Trade[] =
//     let getSessionMultiplier = function
//         | DaySession.Morning -> sqrt 3.0
//         | DaySession.Mid -> 1.0
//         | DaySession.Close -> sqrt 3.0

//     let baselineSizeMean = digestMean digests.SizeDigest
//     let baselineGapMean = digestMean digests.GapDigest
//     let baselineRate = 1.0 / baselineGapMean

//     let allTrades = ResizeArray<Trade>()
//     let mutable price = startPrice
//     let mutable timeOffset = 0.0

//     for session in result.Sessions do
//         let multiplier = getSessionMultiplier session.Label
//         let tiltedDigests = createSessionTiltedDigests rng digests multiplier

//         let auctionParams = {
//             BaseVolBps = baseline.ProposalVolBps
//             MeanVolume = baselineSizeMean
//             MeanRate = baselineRate
//             SessionMultiplier = multiplier
//         }

//         let episodeDurationSeconds = session.Duration * 60.0
//         let episodeVariance = calculateVariance auctionParams episodeDurationSeconds
//         let episodeTarget = log price + sampleAuctionTarget rng episodeVariance
//         let episodeSigma = sqrt episodeVariance

//         let subepisodes = generateSubepisodes rng (fun d -> calculateVariance auctionParams d) episodeTarget episodeSigma session.Duration

//         for subepisode in subepisodes do
//             let durationSeconds = subepisode.Duration * 60.0
//             let trades, endPrice = generateSubepisodeTrades rng price baseline tiltedDigests subepisode.TargetMean subepisode.TargetSigma durationSeconds
//             for t in trades do
//                 allTrades.Add({ t with Time = t.Time + timeOffset })
//             timeOffset <- timeOffset + durationSeconds
//             price <- endPrice

//     allTrades.ToArray()

// /// Aggregate trades into fixed volume bars with trade splitting
// let aggregateToVolumeBars (trades: Trade[]) (volumeSize: int) : VolumeBar[] =
//     if trades.Length = 0 then [||]
//     else
//         let bars = ResizeArray<VolumeBar>()
//         let mutable currentVolume = 0
//         let barData = ResizeArray<BarFragment>()
//         let mutable startTime = trades.[0].Time

//         let computeBar () =
//             if barData.Count > 0 then
//                 let totalVol = barData |> Seq.sumBy (fun f -> f.Volume)
//                 let vwap = (barData |> Seq.sumBy (fun f -> f.Price * float f.Volume)) / float totalVol
//                 let variance = (barData |> Seq.sumBy (fun f -> float f.Volume * (f.Price - vwap) * (f.Price - vwap))) / float totalVol
//                 let vwstd = sqrt variance
//                 let lastFrag = barData.[barData.Count - 1]
//                 let duration = lastFrag.Time - startTime
//                 bars.Add { Vwap = vwap; VwStd = vwstd; Duration = duration; Session = lastFrag.Session }

//         for t in trades do
//             let mutable remainingSize = t.Size
//             let session = sessionToInt Morning

//             while remainingSize > 0 do
//                 let spaceLeft = volumeSize - currentVolume

//                 if remainingSize <= spaceLeft then
//                     barData.Add { Price = t.Price; Volume = remainingSize; Time = t.Time; Session = session }
//                     currentVolume <- currentVolume + remainingSize
//                     remainingSize <- 0
//                 else
//                     if spaceLeft > 0 then
//                         barData.Add { Price = t.Price; Volume = spaceLeft; Time = t.Time; Session = session }
//                         currentVolume <- currentVolume + spaceLeft
//                         remainingSize <- remainingSize - spaceLeft

//                     if currentVolume >= volumeSize then
//                         computeBar()
//                         currentVolume <- 0
//                         barData.Clear()
//                         startTime <- t.Time

//         if barData.Count > 0 then
//             computeBar()

//         bars.ToArray()

type DayData = {
    DayId: int
    Bars500: VolumeBar[]
    Bars2000: VolumeBar[]
    Bars10000: VolumeBar[]
}

// let generateSingleDay
//     (dayId: int)
//     (seed: int)
//     (mcmcConfig: MCMC.Config)
//     (sessionConfig: SessionLevel.Config)
//     (trendConfig: TrendLevel.Config)
//     (startPrice: float)
//     (digests: TradeDataDigests)
//     : DayData =

//     let rng = Random(seed)
//     let result = generateDay sessionConfig trendConfig mcmcConfig rng 390.0
//     let trades = generateDayTrades rng startPrice defaultBaseline digests result

//     { DayId = dayId
//       Bars500 = aggregateToVolumeBars trades 500
//       Bars2000 = aggregateToVolumeBars trades 2000
//       Bars10000 = aggregateToVolumeBars trades 10000 }

let datasetSchema = ParquetSchema(
    DataField<int>("day_id"),
    DataField<int>("bar_type"),
    DataField<float>("vwap"),
    DataField<float>("vwstd"),
    DataField<float>("duration"),
    DataField<int>("session")
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
                do! rowGroup.WriteColumnAsync(DataColumn(fields.[0], dayIds))
                do! rowGroup.WriteColumnAsync(DataColumn(fields.[1], barTypes))
                do! rowGroup.WriteColumnAsync(DataColumn(fields.[2], vwaps))
                do! rowGroup.WriteColumnAsync(DataColumn(fields.[3], vwstds))
                do! rowGroup.WriteColumnAsync(DataColumn(fields.[4], durations))
                do! rowGroup.WriteColumnAsync(DataColumn(fields.[5], sessions))
        daysWritten <- daysWritten + 1
        if daysWritten % 100 = 0 then
            printfn "  Written %d / %d days" daysWritten numDays
}

// let generatorTask
//     (workerId: int) (numWorkers: int) (numDays: int) (baseSeed: int)
//     (mcmcConfig: MCMC.Config) (sessionConfig: SessionLevel.Config)
//     (trendConfig: TrendLevel.Config) (startPrice: float)
//     (digests: TradeDataDigests)
//     (channel: Channel<DayData>) = task {
//     let mutable dayId = workerId
//     while dayId < numDays do
//         let data = generateSingleDay dayId (baseSeed + dayId) mcmcConfig sessionConfig trendConfig startPrice digests
//         do! channel.Writer.WriteAsync(data)
//         dayId <- dayId + numWorkers
// }

// let generateDataset
//     (baseSeed: int) (numDays: int) (outputPath: string)
//     (mcmcConfig: MCMC.Config) (sessionConfig: SessionLevel.Config)
//     (trendConfig: TrendLevel.Config) (startPrice: float)
//     (digestsPath: string) : unit =

//     printfn "Loading t-digests from %s..." digestsPath
//     let digests = loadTDigests digestsPath

//     let numWorkers = Environment.ProcessorCount
//     printfn "Generating %d days with %d workers..." numDays numWorkers

//     let dir = Path.GetDirectoryName(outputPath)
//     if not (String.IsNullOrEmpty(dir)) && not (Directory.Exists(dir)) then
//         Directory.CreateDirectory(dir) |> ignore

//     let channel = Channel.CreateBounded<DayData>(BoundedChannelOptions(numWorkers * 2))

//     let writer = writerTask outputPath numDays channel

//     let generators =
//         [| for w in 0 .. numWorkers - 1 ->
//             Task.Run(Func<Task>(fun () ->
//                 generatorTask w numWorkers numDays baseSeed mcmcConfig sessionConfig trendConfig startPrice digests channel)) |]

//     Task.WhenAll(generators).ContinueWith(fun (_: Task) -> channel.Writer.Complete()).Wait()
//     writer.Wait()

//     printfn "Done. Wrote dataset to %s" outputPath
