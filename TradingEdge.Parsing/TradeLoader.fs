module TradingEdge.Parsing.TradeLoader

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization

// =============================================================================
// Types
// =============================================================================

type RawTrade = {
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

type Trade = {
    Timestamp: DateTime
    Price: float
    Volume: float
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

// =============================================================================
// Loading
// =============================================================================

let loadTrades (filePath: string) : Trade[] =
    let json = File.ReadAllText(filePath)
    let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
    // options.Converters.Add(JsonFSharpConverter(JsonUnionEncoding.Default))
    let rawTrades = JsonSerializer.Deserialize<RawTrade[]>(json, options)

    rawTrades
    |> Array.filter (fun t -> not (shouldExclude t.conditions))
    |> Array.map (fun t ->
        let ticks = t.sip_timestamp / 100L + 621355968000000000L
        { Timestamp = DateTime(ticks, DateTimeKind.Utc)
          Price = t.price
          Volume = t.size })
    |> Array.sortBy (fun t -> t.Timestamp)

