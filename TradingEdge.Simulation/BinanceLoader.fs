module TradingEdge.Simulation.BinanceLoader

open System
open System.IO
open System.Runtime.InteropServices
open Sylvan.Data.Csv

/// Per-trade record extracted from Binance public trade data
/// (https://data.binance.vision). The schema of the source CSV is:
///   trade_id, price, quantity, quote_quantity, timestamp_us,
///   isBuyerMaker, isBestMatch
///
/// We keep the price (for downstream charting), the quantity (volume), the
/// microsecond timestamp, and the sign of aggression derived from
/// isBuyerMaker. isBuyerMaker = True means the buyer was the resting order
/// (passive), so the seller was the aggressor → sign = -1. The reverse maps
/// to sign = +1.
///
/// StructLayout(Sequential) pins the in-memory layout to match the on-disk
/// binary format (see writeBinary / loadBinary), so we can ship the array
/// to/from disk via MemoryMarshal in a single call.
[<Struct; StructLayout(LayoutKind.Sequential)>]
type Trade = {
    Price: float
    Quantity: float
    TimestampUs: int64
    Sign: float    // +1.0 buyer aggressive, -1.0 seller aggressive
}

/// Stream a Binance trade CSV (no header) and return the parsed Trade[].
/// Source schema: trade_id, price, quantity, quote_quantity, timestamp_us,
/// isBuyerMaker, isBestMatch. We only need fields 1, 2, 4, 5.
///
/// Sylvan.Data.Csv keeps each row's fields in an internal buffer and returns
/// span-based field access via GetFieldSpan, so per-field parsing avoids
/// string allocations on the hot path. ~15M rows/day for a busy BTCUSDT.
let loadCsv (path: string) : Trade[] =
    let opts = CsvDataReaderOptions(HasHeaders = false)
    use reader = CsvDataReader.Create(path, opts)
    let result = ResizeArray<Trade>(capacity = 4_000_000)
    while reader.Read() do
        let bm = reader.GetFieldSpan(5)
        result.Add {
            Price = reader.GetDouble 1
            Quantity = reader.GetDouble 2
            TimestampUs = reader.GetInt64 4
            // isBuyerMaker = "True" → buyer was passive, seller aggressed → -1.
            Sign = if bm.Length > 0 && bm.[0] = 'T' then -1.0 else +1.0
        }
    result.ToArray()

// =============================================================================
// Binary IO
// =============================================================================
//
// Format:
//   header (16 bytes):
//     magic   : uint32   = 0x42494E54  ('BINT')
//     version : uint32   = 1
//     count   : int64    number of trade records
//   records (32 bytes each):
//     Trade struct, raw bytes — relies on Sequential layout above.
//
// Bumping `version` invalidates older files; readers reject mismatches.

[<Literal>]
let private Magic = 0x42494E54u
[<Literal>]
let private Version = 1u

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private Header = {
    Magic: uint32
    Version: uint32
    Count: int64
}

let private headerSize = Marshal.SizeOf<Header>()
let private recordSize = Marshal.SizeOf<Trade>()

/// Write a Trade[] to disk as a packed binary file.
let writeBinary (path: string) (trades: Trade[]) =
    use stream = File.Create path
    let header = { Magic = Magic; Version = Version; Count = int64 trades.Length }
    let headerBytes = MemoryMarshal.AsBytes(ReadOnlySpan [| header |])
    stream.Write headerBytes
    let bodyBytes = MemoryMarshal.AsBytes(ReadOnlySpan trades)
    stream.Write bodyBytes

/// Read a Trade[] from a packed binary file.
let loadBinary (path: string) : Trade[] =
    let bytes = File.ReadAllBytes path
    let header = MemoryMarshal.Read<Header>(ReadOnlySpan(bytes, 0, headerSize))
    if header.Magic <> Magic then
        failwithf "loadBinary: bad magic 0x%08X in %s" header.Magic path
    if header.Version <> Version then
        failwithf "loadBinary: unsupported version %u in %s (expected %u)"
            header.Version path Version
    let count = int header.Count
    let records =
        MemoryMarshal.Cast<byte, Trade>(
            ReadOnlySpan(bytes, headerSize, count * recordSize))
    records.ToArray()

/// Auto-dispatch on file extension: .bin → binary, anything else → CSV.
let load (path: string) : Trade[] =
    if path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) then loadBinary path
    else loadCsv path
