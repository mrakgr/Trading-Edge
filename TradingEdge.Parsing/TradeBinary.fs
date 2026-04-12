module TradingEdge.Parsing.TradeBinary

open System
open System.IO
open System.Runtime.InteropServices
open TradeLoader

// =============================================================================
// On-disk format
// =============================================================================

// Per-day file: data/trades_bin/{ticker}/{yyyy-MM-dd}.bin
//
// Header (48 bytes, 8-byte aligned):
//   u32  magic              = 0x54454442  ("TEDB")
//   u16  version            = 1
//   u16  reserved           = 0
//   u32  tradeCount
//   u32  openingPrintIndex  (UInt32.MaxValue if absent)
//   u32  closingPrintIndex  (UInt32.MaxValue if absent)
//   u32  padding
//   i64  baseTicks          (DateTime.Ticks of trades[0])
//   i64  sessionOpenTicks   (DateTime.Ticks of the opening-print trade)
//   i64  sessionCloseTicks  (DateTime.Ticks of the closing-print trade)
//
// Body (tradeCount * 16 bytes):
//   repeated TradeRecord { price: f64; size: i32; timeDeci: i32 }

let [<Literal>] Magic = 0x54454442u
let [<Literal>] Version = 1us
let [<Literal>] AbsentIndex = UInt32.MaxValue

/// 48-byte on-disk header. Layout matches the file format exactly so it can
/// be read/written via MemoryMarshal in a single call.
[<Struct; StructLayout(LayoutKind.Sequential, Pack = 1)>]
type DayHeader =
    val mutable Magic: uint32
    val mutable Version: uint16
    val mutable Reserved: uint16
    val mutable TradeCount: uint32
    val mutable OpeningPrintIndex: uint32
    val mutable ClosingPrintIndex: uint32
    val mutable Padding: uint32
    val mutable BaseTicks: int64
    val mutable SessionOpenTicks: int64
    val mutable SessionCloseTicks: int64

let HeaderSize = Marshal.SizeOf<DayHeader>()

/// 16-byte packed trade record. Naturally aligned on x86-64:
/// 4 trades per 64-byte cache line.
[<Struct; StructLayout(LayoutKind.Sequential, Pack = 1)>]
type TradeRecord =
    val mutable Price: float
    val mutable Size: int32
    val mutable TimeDeci: int32

let RecordSize = Marshal.SizeOf<TradeRecord>()

// =============================================================================
// Writer
// =============================================================================

/// Convert a Trade[] (from TradeLoader.loadTrades) into binary format and write
/// to disk. The input must already be sorted by Timestamp.
let writeDay (path: string) (trades: Trade[]) =
    if trades.Length = 0 then
        failwith "writeDay: empty trade array"

    let baseTicks = trades.[0].Timestamp.Ticks

    // Find opening/closing print indices
    let mutable openIdx = AbsentIndex
    let mutable closeIdx = AbsentIndex
    for i in 0 .. trades.Length - 1 do
        if trades.[i].Session = OpeningPrint && openIdx = AbsentIndex then
            openIdx <- uint32 i
        if trades.[i].Session = ClosingPrint then
            closeIdx <- uint32 i

    let sessionOpenTicks =
        if openIdx <> AbsentIndex then trades.[int openIdx].Timestamp.Ticks else 0L
    let sessionCloseTicks =
        if closeIdx <> AbsentIndex then trades.[int closeIdx].Timestamp.Ticks else 0L

    // Build header
    let mutable header = DayHeader()
    header.Magic <- Magic
    header.Version <- Version
    header.Reserved <- 0us
    header.TradeCount <- uint32 trades.Length
    header.OpeningPrintIndex <- openIdx
    header.ClosingPrintIndex <- closeIdx
    header.Padding <- 0u
    header.BaseTicks <- baseTicks
    header.SessionOpenTicks <- sessionOpenTicks
    header.SessionCloseTicks <- sessionCloseTicks

    // Build record array
    let records = Array.zeroCreate<TradeRecord> trades.Length
    for i in 0 .. trades.Length - 1 do
        let t = trades.[i]
        let deltaTicks = t.Timestamp.Ticks - baseTicks
        let mutable r = TradeRecord()
        r.Price <- t.Price
        r.Size <- int32 t.Volume
        r.TimeDeci <- int (deltaTicks / 1000L)
        records.[i] <- r

    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore

    use stream = File.Create(path)
    // Write header as raw bytes
    let headerBytes = MemoryMarshal.AsBytes(ReadOnlySpan [| header |])
    stream.Write(headerBytes)
    // Write body as raw bytes
    let bodyBytes = MemoryMarshal.AsBytes(ReadOnlySpan records)
    stream.Write(bodyBytes)

// =============================================================================
// Reader
// =============================================================================

/// Read the header from a binary trade file.
let readHeader (path: string) : DayHeader =
    use stream = File.OpenRead(path)
    let buf = Array.zeroCreate<byte> HeaderSize
    stream.ReadExactly(buf, 0, HeaderSize)
    let header = MemoryMarshal.Read<DayHeader>(ReadOnlySpan buf)
    if header.Magic <> Magic then
        failwithf "readHeader: bad magic 0x%08X in %s" header.Magic path
    if header.Version <> Version then
        failwithf "readHeader: unsupported version %d in %s" header.Version path
    header

/// Load a binary trade file into a Trade[]. This materializes the full array
/// on the managed heap — it's the compatibility path that lets existing
/// VwapSystem.fs code work unchanged. A future zero-copy mmap path will
/// replace this for the hot loop.
let loadDay (path: string) : DayHeader * Trade[] =
    let bytes = File.ReadAllBytes(path)
    let header = MemoryMarshal.Read<DayHeader>(ReadOnlySpan(bytes, 0, HeaderSize))
    if header.Magic <> Magic then
        failwithf "loadDay: bad magic 0x%08X in %s" header.Magic path
    if header.Version <> Version then
        failwithf "loadDay: unsupported version %d in %s" header.Version path

    let tradeCount = int header.TradeCount
    let records = MemoryMarshal.Cast<byte, TradeRecord>(ReadOnlySpan(bytes, HeaderSize, tradeCount * RecordSize))

    let openIdx = header.OpeningPrintIndex
    let closeIdx = header.ClosingPrintIndex
    let baseTicks = header.BaseTicks
    let sessionOpenTicks = header.SessionOpenTicks
    let sessionCloseTicks = header.SessionCloseTicks

    let trades = Array.zeroCreate<Trade> tradeCount
    for i in 0 .. tradeCount - 1 do
        let r = records.[i]
        let ticks = baseTicks + int64 r.TimeDeci * 1000L
        let session =
            let idx = uint32 i
            if idx = openIdx then OpeningPrint
            elif idx = closeIdx then ClosingPrint
            elif sessionOpenTicks > 0L && sessionCloseTicks > 0L then
                if ticks < sessionOpenTicks then Premarket
                elif ticks > sessionCloseTicks then Postmarket
                else RegularHours
            else RegularHours
        trades.[i] <- {
            Timestamp = DateTime(ticks)
            Price = r.Price
            Volume = float r.Size
            Session = session
        }

    header, trades
