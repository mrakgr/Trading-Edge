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

    member t.TimeStamp = if t.participant_timestamp <> 0 then t.participant_timestamp else t.sip_timestamp

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
    12  // Form T/Extended Hours
    13  // Extended Hours (Sold Out Of Sequence)
    20  // Next Day (special settlement)
    21  // Price Variation Trade
    22  // Prior Reference Price
    29  // Seller (special settlement)
    32  // Sold (Out of Sequence)
    37  // Odd Lot Trade
    41  // Extended Trading Hours (Sold Out of Sequence)
    52  // Contingent Trade
    53  // Qualified Contingent Trade (QCT)
]

let excludeForPriceDiscovery = excludeConditions - Set.ofList [37; 12] // Include odd lots and extended hour trades. Out of sequence trades always reduce the dataset quality and should be left out.

let shouldExclude (conditions: int[] option) =
    match conditions with
    | None -> false
    | Some codes -> codes |> Array.exists (fun c -> excludeForPriceDiscovery.Contains c)

let detectMarketHours (rawTrades: RawTrade[]) : (int64 * int64) option =
    let mutable openTs = None
    let mutable closeTs = None

    for trade in rawTrades do
        match trade.conditions with
        | Some conditions ->
            // 16 = Market Center Official Open
            if Array.contains 16 conditions then
                openTs <- Some (match openTs with | Some existing -> min existing trade.TimeStamp | None -> trade.TimeStamp)
            // 15 = Market Center Official Close
            if Array.contains 15 conditions then
                closeTs <- Some (match closeTs with | Some existing -> min existing trade.TimeStamp | None -> trade.TimeStamp)
        | None -> ()

    match openTs, closeTs with
    | Some o, Some c -> Some (o, c)
    | _ -> None

let classifySession (t: RawTrade) (marketHours: (int64 * int64) option) : SessionType =
    // 25 = Opening Prints, 8 = Closing Prints
    match t.conditions with
    | Some codes when Array.contains 25 codes -> OpeningPrint
    | Some codes when Array.contains 8 codes -> ClosingPrint
    | _ ->
        match marketHours with
        | Some (openTs, closeTs) ->
            if t.TimeStamp < openTs then Premarket
            elif t.TimeStamp > closeTs then Postmarket
            else RegularHours
        | None -> RegularHours

// =============================================================================
// Loading
// =============================================================================

let loadTrades (filePath: string) : Trade[] =
    let json = File.ReadAllText(filePath)
    let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
    let rawTrades = JsonSerializer.Deserialize<RawTrade[]>(json, options)

    let marketHours = detectMarketHours rawTrades

    rawTrades
    |> Array.filter (fun t -> not (shouldExclude t.conditions))
    |> Array.map (fun t ->
        let timestamp = DateTime.UnixEpoch.AddTicks(t.TimeStamp / 100L)
        { Timestamp = timestamp
          Price = t.price
          Volume = t.size
          Session = classifySession t marketHours })
    |> Array.sortBy (fun t -> t.Timestamp)

