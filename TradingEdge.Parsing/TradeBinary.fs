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
let [<Literal>] HeaderSize = 48
let [<Literal>] RecordSize = 16
let [<Literal>] AbsentIndex = UInt32.MaxValue

/// 16-byte packed trade record. Naturally aligned on x86-64:
/// 4 trades per 64-byte cache line.
[<Struct; StructLayout(LayoutKind.Sequential, Pack = 1)>]
type TradeRecord =
    val Price: float
    val Size: int32
    val TimeDeci: int32
    new (price, size, timeDeci) = { Price = price; Size = size; TimeDeci = timeDeci }

type DayHeader = {
    TradeCount: int
    OpeningPrintIndex: uint32
    ClosingPrintIndex: uint32
    BaseTicks: int64
    SessionOpenTicks: int64
    SessionCloseTicks: int64
}

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

    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore

    use stream = File.Create(path)
    use w = new BinaryWriter(stream)

    // Header
    w.Write(Magic)
    w.Write(Version)
    w.Write(0us) // reserved
    w.Write(uint32 trades.Length)
    w.Write(openIdx)
    w.Write(closeIdx)
    w.Write(0u) // padding
    w.Write(baseTicks)
    w.Write(sessionOpenTicks)
    w.Write(sessionCloseTicks)

    // Body
    for t in trades do
        let deltaTicks = t.Timestamp.Ticks - baseTicks
        // Convert ticks (100ns units) to tenths of milliseconds (100μs units)
        // 1 tick = 100ns, 1 tenth-of-ms = 100μs = 1000 ticks
        let timeDeci = int (deltaTicks / 1000L)
        w.Write(t.Price)
        w.Write(int32 t.Volume)
        w.Write(timeDeci)

// =============================================================================
// Reader
// =============================================================================

/// Read the 40-byte header from a binary trade file.
let readHeader (path: string) : DayHeader =
    use stream = File.OpenRead(path)
    use r = new BinaryReader(stream)

    let magic = r.ReadUInt32()
    if magic <> Magic then
        failwithf "readHeader: bad magic 0x%08X in %s" magic path
    let version = r.ReadUInt16()
    if version <> Version then
        failwithf "readHeader: unsupported version %d in %s" version path
    let _reserved = r.ReadUInt16()
    let tradeCount = r.ReadUInt32() |> int
    let openIdx = r.ReadUInt32()
    let closeIdx = r.ReadUInt32()
    let _padding = r.ReadUInt32()
    let baseTicks = r.ReadInt64()
    let sessionOpenTicks = r.ReadInt64()
    let sessionCloseTicks = r.ReadInt64()

    { TradeCount = tradeCount
      OpeningPrintIndex = openIdx
      ClosingPrintIndex = closeIdx
      BaseTicks = baseTicks
      SessionOpenTicks = sessionOpenTicks
      SessionCloseTicks = sessionCloseTicks }

/// Load a binary trade file into a Trade[]. This materializes the full array
/// on the managed heap — it's the compatibility path that lets existing
/// VwapSystem.fs code work unchanged. A future zero-copy mmap path will
/// replace this for the hot loop.
let loadDay (path: string) : DayHeader * Trade[] =
    use stream = File.OpenRead(path)
    use r = new BinaryReader(stream)

    // Read header
    let magic = r.ReadUInt32()
    if magic <> Magic then
        failwithf "loadDay: bad magic 0x%08X in %s" magic path
    let version = r.ReadUInt16()
    if version <> Version then
        failwithf "loadDay: unsupported version %d in %s" version path
    let _reserved = r.ReadUInt16()
    let tradeCount = r.ReadUInt32() |> int
    let openIdx = r.ReadUInt32()
    let closeIdx = r.ReadUInt32()
    let _padding = r.ReadUInt32()
    let baseTicks = r.ReadInt64()
    let sessionOpenTicks = r.ReadInt64()
    let sessionCloseTicks = r.ReadInt64()

    let header =
        { TradeCount = tradeCount
          OpeningPrintIndex = openIdx
          ClosingPrintIndex = closeIdx
          BaseTicks = baseTicks
          SessionOpenTicks = sessionOpenTicks
          SessionCloseTicks = sessionCloseTicks }

    // Read body
    let trades = Array.zeroCreate<Trade> tradeCount
    for i in 0 .. tradeCount - 1 do
        let price = r.ReadDouble()
        let size = r.ReadInt32()
        let timeDeci = r.ReadInt32()
        let ticks = baseTicks + int64 timeDeci * 1000L
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
            Price = price
            Volume = float size
            Session = session
        }

    header, trades
