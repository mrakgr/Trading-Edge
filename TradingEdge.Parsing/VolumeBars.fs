module TradingEdge.Parsing.VolumeBars

open System
open DuckDB.NET.Data
open TradeLoader

// =============================================================================
// Database Access
// =============================================================================

let getAvgVolume (ticker: string) (date: DateTime option) (dbPath: string) : float option =
    use conn = new DuckDBConnection($"Data Source={dbPath}")
    conn.Open()

    let query, parameters =
        match date with
        | Some d ->
            let dateStr = d.ToString("yyyy-MM-dd")
            """
            SELECT AVG(adj_volume) as avg_volume
            FROM (
                SELECT adj_volume
                FROM split_adjusted_prices
                WHERE ticker = ? AND date < CAST(? AS DATE)
                ORDER BY date DESC
                LIMIT 20
            )
            """, [| box ticker; box dateStr |]
        | None ->
            """
            SELECT AVG(adj_volume) as avg_volume
            FROM (
                SELECT adj_volume
                FROM split_adjusted_prices
                WHERE ticker = ?
                ORDER BY date DESC
                LIMIT 20
            )
            """, [| box ticker |]

    use cmd = new DuckDBCommand(query, conn)
    for p in parameters do cmd.Parameters.Add(DuckDBParameter(Value = p)) |> ignore

    use reader = cmd.ExecuteReader()
    if reader.Read() && not (reader.IsDBNull(0)) then
        Some (reader.GetDouble(0))
    else
        None

// =============================================================================
// Volume Bar Types
// =============================================================================

type VolumeBar = {
    CumulativeVolume: float
    VWAP: float
    StdDev: float
    Volume: float
    StartTime: DateTime
    EndTime: DateTime
    NumTrades: int
    VWMA: float  // Volume-weighted moving average
}

// =============================================================================
// Volume Bar Creation
// =============================================================================

let calculateBarSize (avgVolume: float) : float =
    avgVolume * 3.0 / 4000.0

let calculateVWMA (bars: VolumeBar[]) (currentIndex: int) (startIndex: int) : float =
    if currentIndex < startIndex then 0.0
    else
        let relevantBars = bars.[startIndex..currentIndex]
        let totalVolume = relevantBars |> Array.sumBy (fun b -> b.Volume)
        let weightedSum = relevantBars |> Array.sumBy (fun b -> b.VWAP * b.Volume)
        weightedSum / totalVolume


let computeBarStats (barData: ResizeArray<float * float * DateTime>) (prevCumulativeVolume: float) : VolumeBar =
    let prices = barData |> Seq.map (fun (p, _, _) -> p) |> Seq.toArray
    let volumes = barData |> Seq.map (fun (_, v, _) -> v) |> Seq.toArray
    let timestamps = barData |> Seq.map (fun (_, _, t) -> t) |> Seq.toArray

    let totalVolume = Array.sum volumes
    let vwap = (Array.zip prices volumes |> Array.sumBy (fun (p, v) -> p * v)) / totalVolume
    let variance = (Array.zip volumes prices |> Array.sumBy (fun (v, p) -> v * (p - vwap) ** 2.0)) / totalVolume
    let stddev = sqrt variance

    { CumulativeVolume = prevCumulativeVolume + totalVolume
      VWAP = vwap
      StdDev = stddev
      Volume = totalVolume
      StartTime = timestamps.[0]
      EndTime = timestamps.[timestamps.Length - 1]
      NumTrades = barData.Count
      VWMA = 0.0 }  // Will be calculated in second pass

let createVolumeBars (trades: Trade[]) (volumePerBar: float) : VolumeBar[] =
    let bars = ResizeArray<VolumeBar>()
    let mutable currentVolume = 0.0
    let barData = ResizeArray<float * float * DateTime>()  // price, volume, timestamp
    let mutable cumulativeVolume = 0.0

    for trade in trades do
        let mutable remainingSize = trade.Volume

        while remainingSize > 0.0 do
            let spaceLeft = volumePerBar - currentVolume

            if remainingSize <= spaceLeft then
                barData.Add((trade.Price, remainingSize, trade.Timestamp))
                currentVolume <- currentVolume + remainingSize
                remainingSize <- 0.0
            else
                if spaceLeft > 0.0 then
                    barData.Add((trade.Price, spaceLeft, trade.Timestamp))
                    currentVolume <- currentVolume + spaceLeft
                    remainingSize <- remainingSize - spaceLeft

                if currentVolume >= volumePerBar then
                    let bar = computeBarStats barData cumulativeVolume
                    bars.Add(bar)
                    cumulativeVolume <- bar.CumulativeVolume
                    currentVolume <- 0.0
                    barData.Clear()

    if barData.Count > 0 then
        let bar = computeBarStats barData cumulativeVolume
        bars.Add(bar)

    let barsArray = bars.ToArray()

    // Find opening print timestamp
    let openingPrintTime =
        trades
        |> Array.tryFind (fun t -> t.Session = OpeningPrint)
        |> Option.map (fun t -> t.Timestamp)

    // Find the first bar at or after opening print
    let openingPrintIndex =
        match openingPrintTime with
        | Some opTime ->
            barsArray
            |> Array.tryFindIndex (fun b -> b.EndTime >= opTime)
            |> Option.defaultValue 0
        | None -> 0

    // Second pass: calculate VWMA from opening print onwards
    for i in 0 .. barsArray.Length - 1 do
        barsArray.[i] <- { barsArray.[i] with VWMA = calculateVWMA barsArray i openingPrintIndex }

    barsArray



