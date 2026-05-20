module TradingEdge.ReplaySimulatorV1.Dbn

// Databento Binary Encoding (DBN) — header structs only.
//
// File layout:
//   [Prelude        8 bytes]   magic "DBN" + version u8 + metadata_length u32 LE
//   [Fixed header 100 bytes]   layout depends on version
//   [variable tail]            symbol arrays + mappings (not parsed here)
//   [record stream]            records (not parsed here)
//
// Spec: https://databento.com/docs/standards-and-conventions/databento-binary-encoding-dbn

open System
open System.Buffers
open System.Buffers.Binary
open System.IO
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Text
open System.Threading.Tasks

let MAGIC : byte[] = [| byte 'D'; byte 'B'; byte 'N' |]

/// 8-byte file prelude.
[<Struct; StructLayout(LayoutKind.Sequential, Pack = 1)>]
type DbnPrelude = {
    Magic0: byte
    Magic1: byte
    Magic2: byte
    Version: byte
    MetadataLength: uint32
}

/// Read sizeof<'T> bytes from the stream and reinterpret as 'T. Throws
/// EndOfStreamException on a short read.
let inline readStruct<'T when 'T : (new : unit -> 'T) and 'T : struct and 'T :> ValueType> (s: Stream) : 'T Task = task {
    let size = sizeof<'T>
    let buf = ArrayPool<byte>.Shared.Rent(size)
    try
        do! s.ReadExactlyAsync(Memory(buf, 0, size))
        return MemoryMarshal.Read<'T>(ReadOnlySpan(buf, 0, size))
    finally
        ArrayPool<byte>.Shared.Return(buf)
}

/// Read the 8-byte prelude. Throws if magic is wrong or version isn't in [1..3].
let readPrelude (s: Stream) : DbnPrelude Task = task {
    let! p = readStruct<DbnPrelude> s
    if p.Magic0 <> MAGIC.[0] || p.Magic1 <> MAGIC.[1] || p.Magic2 <> MAGIC.[2] then
        raise (InvalidDataException(
            sprintf "Not a DBN file: magic was [%02X %02X %02X], expected [44 42 4E] ('DBN')"
                p.Magic0 p.Magic1 p.Magic2))
    if p.Version < 1uy || p.Version > 3uy then
        raise (NotSupportedException(sprintf "DBN version %d not supported (expected 1..3)" p.Version))
    return p
}

/// Forward-only skip — zstd streams don't support Seek. Reads into a pooled
/// 512-byte discard buffer until n bytes have been consumed.
let private skipExact (s: Stream) (n: int) : Task = task {
    if n > 0 then
        let buf = ArrayPool<byte>.Shared.Rent(512)
        try
            let cap = min 512 n
            let mutable left = n
            while left > 0 do
                let want = min left cap
                let! got = s.ReadAsync(Memory(buf, 0, want))
                if got <= 0 then raise (EndOfStreamException(sprintf "Unexpected EOF during skip (%d bytes left)" left))
                left <- left - got
        finally
            ArrayPool<byte>.Shared.Return(buf)
}

let private readU8 (s: Stream) : byte Task = task {
    let v = s.ReadByte()
    if v < 0 then raise (EndOfStreamException("Unexpected EOF reading u8"))
    return byte v
}

let private readU16LE (s: Stream) : uint16 Task = task {
    let buf = ArrayPool<byte>.Shared.Rent(2)
    try
        do! s.ReadExactlyAsync(Memory(buf, 0, 2))
        return BinaryPrimitives.ReadUInt16LittleEndian(ReadOnlySpan(buf, 0, 2))
    finally
        ArrayPool<byte>.Shared.Return(buf)
}

let private readU32LE (s: Stream) : uint32 Task = task {
    let buf = ArrayPool<byte>.Shared.Rent(4)
    try
        do! s.ReadExactlyAsync(Memory(buf, 0, 4))
        return BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(buf, 0, 4))
    finally
        ArrayPool<byte>.Shared.Return(buf)
}

let private readU64LE (s: Stream) : uint64 Task = task {
    let buf = ArrayPool<byte>.Shared.Rent(8)
    try
        do! s.ReadExactlyAsync(Memory(buf, 0, 8))
        return BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(buf, 0, 8))
    finally
        ArrayPool<byte>.Shared.Return(buf)
}

/// Read `len` bytes of null-padded ASCII; trim trailing zeros and decode.
let private readCstr (s: Stream) (len: int) : string Task = task {
    let buf = ArrayPool<byte>.Shared.Rent(len)
    try
        do! s.ReadExactlyAsync(Memory(buf, 0, len))
        let mutable n = len
        while n > 0 && buf.[n - 1] = 0uy do n <- n - 1
        return Encoding.ASCII.GetString(buf, 0, n)
    finally
        ArrayPool<byte>.Shared.Return(buf)
}

/// Read a u32 count followed by that many cstrs of length `cstrLen`.
let private readSymbolList (s: Stream) (cstrLen: int) : string list Task = task {
    let! count = readU32LE s
    let result = ResizeArray<string>(int count)
    for _ in 1u .. count do
        let! sym = readCstr s cstrLen
        result.Add sym
    return List.ofSeq result
}

type DbnMetadata = {
    Version: byte
    Dataset: string
    Schema: uint16              // 0 = MBO; 0xFFFF = none/mixed
    StartNs: uint64
    EndNs: uint64               // 0xFFFFFFFFFFFFFFFFUL = undef
    Limit: uint64               // 0 = no limit
    StypeIn: byte               // 0xFF = none
    StypeOut: byte
    TsOut: bool
    SymbolCstrLen: int          // implicit 22 for v1; explicit (default 71) for v2/v3
    Symbols: string list
    PartialSymbols: string list
    NotFoundSymbols: string list
    Mappings: (string * (uint32 * uint32 * string) list) list
}

/// Decode the metadata block following the prelude. Reads exactly `metadataLen`
/// bytes from the stream.
let readMetadata (s: Stream) : DbnMetadata Task = task {
    let! prelude = readPrelude s

    let! dataset = readCstr s 16
    let! schema  = readU16LE s
    let! startNs = readU64LE s
    let! endNs   = readU64LE s
    let! limit   = readU64LE s

    // Version-specific tail of the 100-byte fixed header.
    let mutable stypeIn    = 0uy
    let mutable stypeOut   = 0uy
    let mutable tsOutByte  = 0uy
    let mutable cstrLen    = 0
    let mutable reservedLen = 0
    if prelude.Version = 1uy then
        // V1: deprecated record_count u64, stype_in u8, stype_out u8, ts_out u8, 47 reserved.
        let! _recordCount = readU64LE s
        let! si = readU8 s
        let! so = readU8 s
        let! ts = readU8 s
        stypeIn     <- si
        stypeOut    <- so
        tsOutByte   <- ts
        cstrLen     <- 22
        reservedLen <- 47
    else
        // V2/V3: stype_in u8, stype_out u8, ts_out u8, symbol_cstr_len u16 LE, 53 reserved.
        let! si = readU8 s
        let! so = readU8 s
        let! ts = readU8 s
        let! cl = readU16LE s
        stypeIn     <- si
        stypeOut    <- so
        tsOutByte   <- ts
        cstrLen     <- int cl
        reservedLen <- 53

    do! skipExact s reservedLen
    // Fixed-100 bytes of metadata header now consumed.

    // schema_definition_length u32 — every real file we've seen has it 0.
    // We assert rather than try to interpret an embedded schema definition.
    let! schemaDefLen = readU32LE s
    if schemaDefLen <> 0u then
        raise (NotSupportedException(
            sprintf "DBN metadata has schema_definition_length=%d; this reader expects 0." schemaDefLen))

    let! symbols  = readSymbolList s cstrLen
    let! partial  = readSymbolList s cstrLen
    let! notFound = readSymbolList s cstrLen

    let! mappingCount = readU32LE s
    let mappings = ResizeArray<string * (uint32 * uint32 * string) list>(int mappingCount)
    for _ in 1u .. mappingCount do
        let! rawSym = readCstr s cstrLen
        let! intervalCount = readU32LE s
        let intervals = ResizeArray<uint32 * uint32 * string>(int intervalCount)
        for _ in 1u .. intervalCount do
            let! startDate = readU32LE s
            let! endDate   = readU32LE s
            let! sym       = readCstr s cstrLen
            intervals.Add (startDate, endDate, sym)
        mappings.Add (rawSym, List.ofSeq intervals)

    return {
        Version         = prelude.Version
        Dataset         = dataset
        Schema          = schema
        StartNs         = startNs
        EndNs           = endNs
        Limit           = limit
        StypeIn         = stypeIn
        StypeOut        = stypeOut
        TsOut           = tsOutByte <> 0uy
        SymbolCstrLen   = cstrLen
        Symbols         = symbols
        PartialSymbols  = partial
        NotFoundSymbols = notFound
        Mappings        = List.ofSeq mappings
    }
}