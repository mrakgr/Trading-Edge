module TradingEdge.Orb.TradeLoader

open System
open System.Collections.Generic
open System.Runtime.InteropServices
open DuckDB.NET.Data
open System.Collections.Immutable

// =============================================================================
// Types
// =============================================================================

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

    member self.Timestamp = if self.participant_timestamp <> 0L then self.participant_timestamp else self.sip_timestamp
    // Polygon stores nanoseconds since Unix epoch (1970-01-01). .NET Ticks are
    // 100-ns units since 0001-01-01 — so divide by 100 and add the epoch offset.
    static member val UnixEpochTicks = System.DateTime(1970, 1, 1, 0, 0, 0, System.DateTimeKind.Utc).Ticks
    member self.Ticks = self.Timestamp / 100L + RawTrade.UnixEpochTicks

[<Struct; StructLayout(LayoutKind.Sequential)>]
type Trade = 
    {
        Price: float
        Volume: uint
        KiloTicksFromBase: uint
    }

    member self.TicksFromBase = int64 self.KiloTicksFromBase * 1000L

// =============================================================================
// Filtering
// =============================================================================

// Condition codes to exclude for price discovery analysis.
// Codes 12 (Form T) and 37 (Odd Lot) intentionally kept in: 99%+ of premarket
// trades carry 12, and odd lots make up a large share of modern volume. Late
// prints inside those groups are caught by the SIP-delta filter below instead.
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
    52  // Contingent Trade
    53  // Qualified Contingent Trade (QCT)
]

/// Drop trades whose SIP timestamp lags the participant timestamp by more than
/// this many ticks (100-ns units). 50ms catches the late-reported Form T prints
/// that show up 10%+ off-market (e.g. MSTR 2024-11-21 08:47:45 $473.83 vs. $537
/// contemporaneous market), while the healthy tail stays well under 10ms.
let maxSipDeltaTicks = 50L * TimeSpan.TicksPerMillisecond

let openingPrintConditions = Set.ofList [
    17  // Market Center Opening Trade
    25  // Opening Prints
]

let closingPrintConditions = Set.ofList [
    19  // Market Center Closing Trade
    8   // Closing Prints
]

let openingAndClosingPrintConditions = openingPrintConditions + closingPrintConditions

let shouldExclude (conditions: int Set) =
    if Set.intersect conditions openingAndClosingPrintConditions |> Set.isEmpty |> not then false
    else Set.intersect conditions excludeConditions |> Set.isEmpty |> not

// =============================================================================
// Loading (streaming)
// =============================================================================

type TradesStaging =
    {
        Trades : Trade ImmutableArray
        OpeningPrintIndex : int voption
    }

type TradesStagingBuilder() =
    member val private Trades = ImmutableArray.CreateBuilder(1 <<< 18)
    member val private OpeningPrintIndex = ValueNone with get, set
    member val private BaseTime : DateTime voption = ValueNone with get, set

    member self.AddTrade(trade : RawTrade) =
        let conds = trade.conditions |> Set.ofArray
        if self.OpeningPrintIndex.IsNone && not (Set.intersect conds openingPrintConditions).IsEmpty then
            self.OpeningPrintIndex <- ValueSome self.Trades.Count
        // SIP timestamps measure when the SIP *reported* the trade; participant_timestamp
        // is when the venue booked it. A large positive delta means a late print — often
        // a stale extended-hours price arriving after the market has moved.
        let sipDeltaTicks =
            if trade.participant_timestamp <> 0L && trade.sip_timestamp <> 0L then
                (trade.sip_timestamp - trade.participant_timestamp) / 100L
            else 0L
        if trade.size > 0 && not (shouldExclude conds) && sipDeltaTicks <= maxSipDeltaTicks then
            let tradeTicks = trade.Ticks
            let baseTime =
                match self.BaseTime with
                | ValueSome bt -> bt
                | ValueNone ->
                    let bt = Timezone.baseTimeFromTicks tradeTicks
                    self.BaseTime <- ValueSome bt
                    bt
            let delta = tradeTicks - baseTime.Ticks
            if delta < 0L then failwithf "Trade timestamp %d precedes baseTime %d (delta=%d)" tradeTicks baseTime.Ticks delta
            self.Trades.Add {
                Price = trade.price
                KiloTicksFromBase = delta / 1000L |> uint
                Volume = uint trade.size
            }

    member self.ToTradesStaging() =
        {
            Trades = self.Trades.ToImmutableArray()
            OpeningPrintIndex = self.OpeningPrintIndex
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
             FROM (SELECT *, ROW_NUMBER() OVER () AS _row FROM read_parquet('%s')) \
             ORDER BY participant_timestamp, _row"
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

let loadTrades (filePath: string) =
    let raws = loadRawTrades filePath
    let builder = TradesStagingBuilder()
    raws |> Array.iter builder.AddTrade
    builder.ToTradesStaging()


