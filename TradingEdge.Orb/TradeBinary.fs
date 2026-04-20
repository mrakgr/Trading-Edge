module TradingEdge.Orb.TradeBinary

open System
open System.IO
open System.Runtime.InteropServices
open TradeLoader

// =============================================================================
// On-disk format
// =============================================================================

// Magic bumped when the on-disk header layout changes. Any new field here
// requires a new magic — readers refuse to open files with the wrong magic.
let [<Literal>] Magic = 0x54454444u

/// On-disk header. Layout matches the file format exactly so it can be
/// read/written via MemoryMarshal in a single call.
///
/// RawAvg4w / TxnAvg4w / SplitFactorToday are the per-day context needed by
/// the ThresholdGate at replay time. Storing them in the header lets the
/// gap-up backtest avoid a DuckDB round-trip per (ticker, date). They come
/// from session_volume_4w at convert time and reflect:
///   RawAvg4w       = avg_session_adj_volume_4w / split_factor_today
///                    (split-safe 4w avg volume in raw share units for today)
///   TxnAvg4w       = avg_session_transactions_4w
///   SplitFactorToday = session_adj_volume / session_raw_volume
///                      (kept for audit / to reconstruct the adj avg)
/// All three are NaN when session_volume_4w lacked a valid row (e.g. ticker
/// has < 16 trading days of history). Callers should skip those days.
[<Struct; StructLayout(LayoutKind.Sequential)>]
type DayHeader = {
    Magic: uint32
    TradeCount: int
    OpeningPrintIndex: int voption
    BaseTicks: int64
    RawAvg4w: double
    TxnAvg4w: double
    SplitFactorToday: double
}

let HeaderSize = Marshal.SizeOf<DayHeader>()
let RecordSize = Marshal.SizeOf<Trade>()

type TickerInfo = {
    Directory : string
    Ticker : string
    Date : string
}

// =============================================================================
// Writer
// =============================================================================

let infoPath (info: TickerInfo) =
    Path.Combine(info.Directory, info.Ticker, $"{info.Date}.bin")

let ensureInfoDir (info: TickerInfo) =
    Directory.CreateDirectory (Path.Combine(info.Directory, info.Ticker)) |> ignore

/// Day-level context stored alongside the trades. Callers that don't have
/// valid 4w data (e.g. ticker history < 16 days) should pass Double.NaN for
/// the affected fields — PassesEntryGate treats NaN as pass-through.
type DayMeta = {
    RawAvg4w: double
    TxnAvg4w: double
    SplitFactorToday: double
}

let DayMetaNaN = { RawAvg4w = nan; TxnAvg4w = nan; SplitFactorToday = nan }

/// Convert a Trade[] (from TradeLoader.loadTrades) into binary format and write
/// to disk. The input must already be sorted by Timestamp.
let writeDay (info: TickerInfo) (meta: DayMeta) (trades: TradesStaging) =
    let baseTime = Timezone.baseTimeFromDateString(info.Date)
    // Build header
    let header = {
        Magic = Magic
        TradeCount = trades.Trades.Length
        OpeningPrintIndex = trades.OpeningPrintIndex
        BaseTicks = baseTime.Ticks
        RawAvg4w = meta.RawAvg4w
        TxnAvg4w = meta.TxnAvg4w
        SplitFactorToday = meta.SplitFactorToday
    }

    ensureInfoDir info
    use stream = File.Create(infoPath info)
    // Write header as raw bytes
    let headerBytes = MemoryMarshal.AsBytes(ReadOnlySpan [| header |])
    stream.Write(headerBytes)
    // Write body as raw bytes
    let bodyBytes = MemoryMarshal.AsBytes(trades.Trades.AsSpan())
    stream.Write(bodyBytes)

// =============================================================================
// Reader
// =============================================================================

/// Read the header from a binary trade file.
let readHeader (info: TickerInfo) : DayHeader =
    let path = infoPath info
    use stream = File.OpenRead path
    let buf = Array.zeroCreate<byte> HeaderSize
    stream.ReadExactly(buf, 0, HeaderSize)
    let header = MemoryMarshal.Read<DayHeader>(ReadOnlySpan buf)
    if header.Magic <> Magic then
        failwithf "readHeader: bad magic 0x%08X in %s" header.Magic path
    header

/// Load a binary trade file into a Trade[]. This materializes the full array
/// on the managed heap — it's the compatibility path that lets existing
/// VwapSystem.fs code work unchanged. A future zero-copy mmap path will
/// replace this for the hot loop.
let loadDay (info: TickerInfo) : DayHeader * Trade[] =
    let path = infoPath info
    let bytes = File.ReadAllBytes path
    let header = MemoryMarshal.Read<DayHeader>(ReadOnlySpan(bytes, 0, HeaderSize))
    if header.Magic <> Magic then
        failwithf "loadDay: bad magic 0x%08X in %s" header.Magic path

    let tradeCount = int header.TradeCount
    let records = MemoryMarshal.Cast<byte, Trade>(ReadOnlySpan(bytes, HeaderSize, tradeCount * RecordSize))
    let trades = records.ToArray()
    header, trades
