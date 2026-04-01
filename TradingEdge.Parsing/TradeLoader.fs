module TradingEdge.Parsing.TradeLoader

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization

// =============================================================================
// Types
// =============================================================================

type SessionType =
    | Premarket
    | OpeningPrint
    | RegularHours
    | ClosingPrint
    | Postmarket

type RawTrade = 
    {
        conditions: int[] option
        exchange: int
        id: string
        participant_timestamp: int64
        price: float
        sequence_number: int
        sip_timestamp: int64
        size: float
        tape: int
    }

let timestamp (t : RawTrade) = if t.participant_timestamp <> 0 then t.participant_timestamp else t.sip_timestamp
let conditions (t : RawTrade) = Option.defaultValue [||] t.conditions

type Trade = {
    Timestamp: DateTime
    Price: float
    Volume: float
    Session: SessionType
}

// =============================================================================
// Filtering
// =============================================================================

// Condition codes to exclude for price discovery analysis
let excludeConditions = Set.ofList [
    2   // Average Price Trade
    7   // Cash Sale (special settlement)
    10  // Derivatively Priced
    13  // Extended Hours (Sold Out Of Sequence)
    20  // Next Day (special settlement)
    21  // Price Variation Trade
    22  // Prior Reference Price
    29  // Seller (special settlement)
    32  // Sold (Out of Sequence)
    41  // Extended Trading Hours (Sold Out of Sequence)
    52  // Contingent Trade
    53  // Qualified Contingent Trade (QCT)
]

let openingPrintConditions = Set.ofList [
    17  // Market Center Opening Trade
    25  // Opening Prints
]

let closingPrintConditions = Set.ofList [
    19  // Market Center Closing Trade
    8   // Closing Prints
]

let openingAndClosingPrintConditions = openingPrintConditions + closingPrintConditions

let shouldExclude (conditions: int[]) =
    let c = Set.ofArray conditions
    if Set.intersect c openingAndClosingPrintConditions |> Set.isEmpty |> not then false
    else Set.intersect c excludeConditions |> Set.isEmpty |> not

let detectMarketHours (rawTrades: RawTrade[]) : (int64 * int64) option =
    let mutable openTs = None
    let mutable closeTs = None

    for trade in rawTrades do
        let conditions = conditions trade
        // 16 = Market Center Official Open
        if Array.contains 16 conditions then
            openTs <- Some (match openTs with | Some existing -> min existing (timestamp trade) | None -> timestamp trade)
        // 15 = Market Center Official Close
        if Array.contains 15 conditions then
            closeTs <- Some (match closeTs with | Some existing -> min existing (timestamp trade) | None -> timestamp trade)

    match openTs, closeTs with
    | Some o, Some c -> Some (o, c)
    | _ -> None

let classifySession (t: RawTrade) (marketHours: (int64 * int64) option) : SessionType =
    let c = conditions t |> Set.ofArray
    if Set.intersect c openingPrintConditions |> Set.isEmpty |> not then OpeningPrint
    elif Set.intersect c closingPrintConditions |> Set.isEmpty |> not then ClosingPrint
    else 
        match marketHours with
        | Some (openTs, closeTs) ->
            if timestamp t < openTs then Premarket
            elif timestamp t > closeTs then Postmarket
            else RegularHours
        | None -> RegularHours

// =============================================================================
// Loading
// =============================================================================

let loadTrades (filePath: string) : Trade[] =
    let json = File.ReadAllText(filePath)
    let options = JsonSerializerOptions()     
    let rawTrades = JsonSerializer.Deserialize<RawTrade[]>(json, options)

    let marketHours = detectMarketHours rawTrades

    rawTrades
    |> Array.filter (fun t -> not (shouldExclude (conditions t)))
    |> Array.map (fun t ->
        let timestamp = DateTime.UnixEpoch.AddTicks(timestamp t / 100L)
        { Timestamp = timestamp
          Price = t.price
          Volume = t.size
          Session = classifySession t marketHours })
    |> Array.sortBy (fun t -> t.Timestamp)

