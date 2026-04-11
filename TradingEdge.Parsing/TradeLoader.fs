module TradingEdge.Parsing.TradeLoader

open System
open System.Collections.Generic
open DuckDB.NET.Data

// =============================================================================
// Types
// =============================================================================

type SessionType =
    | Premarket
    | OpeningPrint
    | RegularHours
    | ClosingPrint
    | Postmarket

/// Trimmed raw record: the five fields persisted to Parquet. Populated by the
/// DuckDB reader below, then filtered/classified in-process.
type RawTrade =
    {
        conditions: int[]
        participant_timestamp: int64
        price: float
        sip_timestamp: int64
        size: float
    }

let timestamp (t : RawTrade) = if t.participant_timestamp <> 0L then t.participant_timestamp else t.sip_timestamp
let conditions (t : RawTrade) = t.conditions

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

// =============================================================================
// Parquet reader (low-level)
// =============================================================================

/// Read all rows from a trades Parquet file into a flat RawTrade[]. The
/// Parquet schema drops `id`, `exchange`, `sequence_number`, `tape` — those
/// are Polygon wire fields that no downstream consumer reads. Rows are
/// returned in file order (DuckDB does not guarantee ordering; the caller
/// sorts by timestamp afterwards).
let loadRawTrades (filePath: string) : RawTrade[] =
    // Double up any single-quotes in the path so the SQL literal stays safe.
    let escaped = filePath.Replace("'", "''")

    use connection = new DuckDBConnection("Data Source=:memory:")
    connection.Open()

    use cmd = connection.CreateCommand()
    cmd.CommandText <-
        sprintf
            "SELECT participant_timestamp, sip_timestamp, price, size, conditions \
             FROM read_parquet('%s')"
            escaped

    use reader = cmd.ExecuteReader()
    let result = ResizeArray<RawTrade>()
    while reader.Read() do
        let partTs =
            if reader.IsDBNull(0) then 0L else reader.GetInt64(0)
        let sipTs =
            if reader.IsDBNull(1) then 0L else reader.GetInt64(1)
        let price = reader.GetDouble(2)
        let size = reader.GetDouble(3)
        let conds =
            if reader.IsDBNull(4) then
                Array.empty
            else
                // DuckDB returns list columns as a generic List; materialize
                // once to avoid per-row LINQ allocation.
                let raw = reader.GetValue(4) :?> IList<int>
                let arr = Array.zeroCreate<int> raw.Count
                for i in 0 .. raw.Count - 1 do arr.[i] <- raw.[i]
                arr
        result.Add {
            conditions = conds
            participant_timestamp = partTs
            price = price
            sip_timestamp = sipTs
            size = size
        }
    result.ToArray()

// =============================================================================
// Loading (public entry point)
// =============================================================================

/// Load and classify trades from a per-ticker-day Parquet file. Two passes:
///   * first pass detects official market open/close (condition codes 16/15)
///     and filters out exchange-noise conditions + zero-volume prints;
///   * second pass assigns a SessionType to each surviving row using the
///     detected market hours and materializes the final Trade[].
/// Output is sorted by timestamp (stable).
let loadTrades (filePath: string) : Trade[] =
    let raws = loadRawTrades filePath

    let staging = ResizeArray<TradeStaging>(raws.Length)
    let mutable officialOpenNs = 0L
    let mutable haveOfficialOpen = false
    let mutable officialCloseNs = 0L
    let mutable haveOfficialClose = false

    for raw in raws do
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
