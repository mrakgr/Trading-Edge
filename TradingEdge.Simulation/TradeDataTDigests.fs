module TradingEdge.Simulation.TradeDataTDigests

open System
open System.IO
open System.Text.Json
open TDigest

type JsonTrade = {
    participant_timestamp: int64
    price: float
    size: int
}

type TradeDataDigests = {
    SizeDigest: MergingDigest
    GapDigest: MergingDigest
}

let mergeTradesWithinThreshold (trades: JsonTrade[]) (thresholdNs: int64) : JsonTrade[] =
    if trades.Length = 0 then [||]
    else
        let merged = ResizeArray<JsonTrade>()
        let mutable group = ResizeArray<JsonTrade>([trades.[0]])

        for i in 1 .. trades.Length - 1 do
            let trade = trades.[i]
            let prevTrade = group.[group.Count - 1]
            if trade.participant_timestamp - prevTrade.participant_timestamp < thresholdNs then
                group.Add(trade)
            else
                let totalSize = group |> Seq.sumBy (fun t -> t.size)
                let vwap = (group |> Seq.sumBy (fun t -> float t.size * t.price)) / float totalSize
                merged.Add({ participant_timestamp = group.[0].participant_timestamp; price = vwap; size = totalSize })
                group.Clear()
                group.Add(trade)

        let totalSize = group |> Seq.sumBy (fun t -> t.size)
        let vwap = (group |> Seq.sumBy (fun t -> float t.size * t.price)) / float totalSize
        merged.Add({ participant_timestamp = group.[0].participant_timestamp; price = vwap; size = totalSize })
        merged.ToArray()

let buildTDigestsFromJson (jsonPath: string) (compression: float) (mergeThresholdNs: int64) (marketOpen: float) (marketClose: float) : TradeDataDigests =
    printfn "Building t-digests from %s..." jsonPath
    let json = File.ReadAllText(jsonPath)
    let trades = JsonSerializer.Deserialize<JsonTrade[]>(json)
    printfn "  Loaded %d trades" trades.Length

    let filtered = trades |> Array.filter (fun t -> t.size > 0)
    printfn "  Filtered to %d trades (removed size 0)" filtered.Length

    let marketHours = filtered |> Array.filter (fun t ->
        let dt = DateTimeOffset.FromUnixTimeMilliseconds(t.participant_timestamp / 1_000_000L).UtcDateTime
        let hour = float dt.Hour + float dt.Minute / 60.0
        marketOpen <= hour && hour <= marketClose)
    printfn "  Filtered to %d trades (market hours %.1f-%.1f UTC)" marketHours.Length marketOpen marketClose

    let sorted = marketHours |> Array.sortBy (fun t -> t.participant_timestamp)
    printfn "  Sorted trades by timestamp"

    if sorted.Length > 0 then
        let firstTs = sorted.[0].participant_timestamp
        let firstDt = DateTimeOffset.FromUnixTimeMilliseconds(firstTs / 1_000_000L).UtcDateTime
        let cutoffTs = firstTs + 300_000_000_000L  // 5 minutes in nanoseconds
        let volume5min = sorted |> Array.filter (fun t -> t.participant_timestamp <= cutoffTs) |> Array.sumBy (fun t -> t.size)
        printfn "  First trade: %s UTC" (firstDt.ToString("yyyy-MM-dd HH:mm:ss"))
        printfn "  Volume in first 5 minutes: %s" (volume5min.ToString("N0"))

    let merged = mergeTradesWithinThreshold sorted mergeThresholdNs
    printfn "  Merged to %d trades (threshold: %d ns)" merged.Length mergeThresholdNs

    let sizeDigest = MergingDigest(compression)
    for t in merged do
        sizeDigest.Add(float t.size)

    let gapDigest = MergingDigest(compression)
    for i in 1 .. merged.Length - 1 do
        let gapNs = merged.[i].participant_timestamp - merged.[i-1].participant_timestamp
        let gapS = float gapNs / 1e9  // Convert nanoseconds to seconds
        gapDigest.Add(gapS)

    printfn "  Size digest: %d centroids" (sizeDigest.Centroids() |> Seq.length)
    printfn "  Gap digest: %d centroids" (gapDigest.Centroids() |> Seq.length)
    { SizeDigest = sizeDigest; GapDigest = gapDigest }

let saveTDigests (digests: TradeDataDigests) (outputPath: string) =
    use stream = File.Create(outputPath)
    use writer = new BinaryWriter(stream)
    digests.SizeDigest.AsBytes(writer)
    digests.GapDigest.AsBytes(writer)
    printfn "Saved trade data t-digests to %s" outputPath

let loadTDigests (inputPath: string) : TradeDataDigests =
    use stream = File.OpenRead(inputPath)
    use reader = new BinaryReader(stream)
    { SizeDigest = MergingDigest.FromBytes(reader)
      GapDigest = MergingDigest.FromBytes(reader) }
