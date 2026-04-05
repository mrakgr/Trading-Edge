module TradingEdge.Parsing.TradeLoader

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open FSharp.Control

// =============================================================================
// Types
// =============================================================================

type SessionType =
    | Premarket
    | OpeningPrint
    | RegularHours
    | ClosingPrint
    | Postmarket

/// Trimmed raw record: only fields we actually consume. STJ ignores unknown
/// JSON properties by default, so dropping `id`, `exchange`, `sequence_number`,
/// `tape` saves per-trade allocation during streaming.
type RawTrade =
    {
        conditions: int[] option
        participant_timestamp: int64
        price: float
        sip_timestamp: int64
        size: float
    }

let timestamp (t : RawTrade) = if t.participant_timestamp <> 0L then t.participant_timestamp else t.sip_timestamp
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

// =============================================================================
// Loading (streaming)
// =============================================================================

/// Compact intermediate: what we need post-filter to classify sessions and
/// emit a Trade. Using a struct record keeps the ResizeArray contiguous and
/// avoids per-trade reference-type allocations while we buffer the day.
[<Struct>]
type private TradeStaging = {
    mutable Ts: int64
    mutable Price: float
    mutable Volume: float
    mutable IsOpeningPrint: bool
    mutable IsClosingPrint: bool
}

/// Stream the JSON array element-by-element via System.Text.Json's async
/// enumerable API. This avoids the previous File.ReadAllText path which
/// doubled the file size in memory as a UTF-16 string before parsing.
///
/// Single streaming pass:
///   * detects official market open/close (condition codes 16 / 15)
///   * filters out exchange-noise conditions and zero-volume prints
///   * collects a compact struct per surviving trade
/// Followed by an in-memory classification pass and a stable sort by time.
let loadTrades (filePath: string) : Trade[] =
    let staging = ResizeArray<TradeStaging>()
    let mutable officialOpenNs = 0L
    let mutable haveOfficialOpen = false
    let mutable officialCloseNs = 0L
    let mutable haveOfficialClose = false

    let options = JsonSerializerOptions()
    // 64KB reads: the default is far smaller and JSON parsing is I/O bound
    // for gigabyte-sized inputs.
    let fsOptions = FileStreamOptions()
    fsOptions.Mode <- FileMode.Open
    fsOptions.Access <- FileAccess.Read
    fsOptions.Share <- FileShare.Read
    fsOptions.BufferSize <- 1 <<< 16
    fsOptions.Options <- FileOptions.SequentialScan

    let loadTask = task {
        use stream = new FileStream(filePath, fsOptions)
        let enumerable =
            JsonSerializer.DeserializeAsyncEnumerable<RawTrade>(stream, options)
        for raw in enumerable do
            let conds = conditions raw
            let ts = timestamp raw

            // Track official market hours (condition 16 = official open, 15 = close).
            // Pick the earliest occurrence when multiple are tagged.
            for i in 0 .. conds.Length - 1 do
                let c = conds.[i]
                if c = 16 then
                    if not haveOfficialOpen || ts < officialOpenNs then
                        officialOpenNs <- ts
                        haveOfficialOpen <- true
                elif c = 15 then
                    if not haveOfficialClose || ts < officialCloseNs then
                        officialCloseNs <- ts
                        haveOfficialClose <- true

            if not (shouldExclude conds) && raw.size > 0.0 then
                let condSet = Set.ofArray conds
                let isOpen =
                    not (Set.intersect condSet openingPrintConditions |> Set.isEmpty)
                let isClose =
                    not (Set.intersect condSet closingPrintConditions |> Set.isEmpty)
                staging.Add {
                    Ts = ts
                    Price = raw.price
                    Volume = raw.size
                    IsOpeningPrint = isOpen
                    IsClosingPrint = isClose
                }
    }
    loadTask.GetAwaiter().GetResult()

    // Second pass: classify sessions using the market hours we detected,
    // and materialize the final Trade[] in one allocation.
    let n = staging.Count
    let result = Array.zeroCreate<Trade> n
    for i in 0 .. n - 1 do
        let s = staging.[i]
        let session =
            if s.IsOpeningPrint then OpeningPrint
            elif s.IsClosingPrint then ClosingPrint
            elif haveOfficialOpen && haveOfficialClose then
                if s.Ts < officialOpenNs then Premarket
                elif s.Ts > officialCloseNs then Postmarket
                else RegularHours
            else RegularHours
        result.[i] <- {
            Timestamp = DateTime.UnixEpoch.AddTicks(s.Ts / 100L)
            Price = s.Price
            Volume = s.Volume
            Session = session
        }

    Array.sortInPlaceBy (fun (t: Trade) -> t.Timestamp) result
    result
