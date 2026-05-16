module TradingEdge.Orb.TradeLoader

open System
open System.Collections.Generic
open System.Runtime.InteropServices
open DuckDB.NET.Data
open System.Collections.Immutable
open TradingEdge.Orb.TradeFilters

// =============================================================================
// Types
// =============================================================================

/// Trimmed raw record: the fields persisted to the per-ticker Parquet plus
/// trf_id (added when the bulk-trades schema gained it). Populated by the
/// DuckDB reader below, then filtered via TradeFilters.shouldKeepTrade.
type RawTrade =
    {
        conditions: int[]
        participant_timestamp: int64
        price: float
        sip_timestamp: int64
        size: float
        trf_id: int64
    }

    // Direct-broker semantics: participant_timestamp is the venue's booking
    // clock — what a live system on a direct exchange feed would see. SIP
    // timestamps are out of scope (the SIP path is a consolidated reporter, not
    // the venue clock, and our forward path is direct-feed). sip_timestamp
    // remains on the record for diagnostics but is not used for ordering.
    member self.Timestamp = self.participant_timestamp
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
        if shouldKeepTrade trade.trf_id trade.size conds then
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

/// Read all rows from a trades Parquet file into a flat RawTrade[]. Requires
/// `trf_id` to be present in the parquet schema — older per-ticker parquets
/// that predate the trf_id extension will fail loudly at read time. That's
/// intentional: rebuild the missing file from bulk rather than silently
/// treating its trades as lit. Rows are ordered by participant_timestamp.
let loadRawTrades (filePath: string) : RawTrade[] =
    // Double up any single-quotes in the path so the SQL literal stays safe.
    let escaped = filePath.Replace("'", "''")

    use connection = new DuckDBConnection("Data Source=:memory:")
    connection.Open()

    use cmd = connection.CreateCommand()
    cmd.CommandText <-
        sprintf
            "SELECT participant_timestamp, sip_timestamp, price, size, trf_id, conditions \
             FROM read_parquet('%s') \
             ORDER BY participant_timestamp"
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
        let trfId =
            if reader.IsDBNull(4) then 0L else reader.GetInt64(4)
        let conds =
            if reader.IsDBNull(5) then
                Array.empty
            else
                // DuckDB returns list columns as a generic List; materialize
                // once to avoid per-row LINQ allocation.
                let raw = reader.GetValue(5) :?> IList<int>
                let arr = Array.zeroCreate<int> raw.Count
                for i in 0 .. raw.Count - 1 do arr.[i] <- raw.[i]
                arr
        result.Add {
            conditions = conds
            participant_timestamp = partTs
            price = price
            sip_timestamp = sipTs
            size = size
            trf_id = trfId
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
