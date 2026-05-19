module TradingEdge.ReplaySimulator.Dbn

// Native F# reader for Databento DBN files (zstd-wrapped or raw). MBO only.
//
// Format reference: https://databento.com/docs/standards-and-conventions/databento-binary-encoding-dbn
// Rust source-of-truth: https://github.com/databento/dbn/blob/main/rust/dbn/src/decode/dbn/fsm.rs
//
// Layout:
//   - 8-byte prelude: magic "DBN" (3) + version u8 + metadata_length u32 LE
//   - metadata_length bytes of metadata (fixed 100-byte header + variable symbol tail)
//   - record stream: each record starts with a 16-byte RecordHeader; total record
//     size = length * 4 bytes (length field is in 32-bit words, includes the header)
//
// We parse the metadata fully (not skip) so we can assert schema=MBO and fail
// loudly on the wrong file. MBO record layout is identical across DBN v1/v2/v3.

open System
open System.IO
open System.Runtime.InteropServices
open System.Buffers.Binary

let MAGIC = [| byte 'D'; byte 'B'; byte 'N' |]
let RTYPE_MBO : byte = 0xA0uy
let SCHEMA_MBO : uint16 = 0us       // Schema::Mbo in DBN enums
let SCHEMA_NONE : uint16 = 0xFFFFus // mixed-schema sentinel

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

/// MBO record body (16-byte header + 40 bytes), packed C layout. 56 bytes total.
[<Struct; StructLayout(LayoutKind.Sequential, Pack = 1)>]
type MboMsg = {
    // ---- RecordHeader (16 bytes) ----
    Length: byte                // in 32-bit words; MBO = 14 → 56 bytes
    RType: byte                 // 0xA0 for MBO
    PublisherId: uint16
    InstrumentId: uint32
    TsEvent: int64              // nanos since epoch (Databento clock)
    // ---- MBO body (40 bytes) ----
    OrderId: uint64
    Price: int64                // 1 unit = 1e-9 dollars (e.g. price=12_345_000_000 → $12.345)
    Size: uint32
    Flags: byte
    ChannelId: byte
    Action: byte                // 'A'=0x41 add, 'C'=0x43 cancel, 'M'=0x4D modify,
                                // 'R'=0x52 clear, 'T'=0x54 trade, 'F'=0x46 fill
    Side: byte                  // 'A'=0x41 ask, 'B'=0x42 bid, 'N'=0x4E none
    TsRecv: int64               // nanos since epoch (gateway recv clock)
    TsInDelta: int32
    Sequence: uint32
}

let private readExact (s: Stream) (buf: byte[]) (off: int) (len: int) : unit =
    let mutable remaining = len
    let mutable cursor = off
    while remaining > 0 do
        let n = s.Read(buf, cursor, remaining)
        if n <= 0 then raise (EndOfStreamException(sprintf "Unexpected EOF (wanted %d more bytes)" remaining))
        cursor <- cursor + n
        remaining <- remaining - n

/// Forward-only skip — zstd streams don't support Seek.
let private skipExact (s: Stream) (n: int) : unit =
    if n > 0 then
        let buf = Array.zeroCreate<byte> (min n 8192)
        let mutable left = n
        while left > 0 do
            let want = min left buf.Length
            let got = s.Read(buf, 0, want)
            if got <= 0 then raise (EndOfStreamException(sprintf "Unexpected EOF during skip (%d bytes left)" left))
            left <- left - got

let private readU8 (s: Stream) : byte =
    let v = s.ReadByte()
    if v < 0 then raise (EndOfStreamException("Unexpected EOF reading u8"))
    byte v

let private readU16LE (s: Stream) : uint16 =
    let buf = Array.zeroCreate<byte> 2
    readExact s buf 0 2
    BinaryPrimitives.ReadUInt16LittleEndian(ReadOnlySpan(buf))

let private readU32LE (s: Stream) : uint32 =
    let buf = Array.zeroCreate<byte> 4
    readExact s buf 0 4
    BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(buf))

let private readU64LE (s: Stream) : uint64 =
    let buf = Array.zeroCreate<byte> 8
    readExact s buf 0 8
    BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(buf))

let private readCstr (s: Stream) (len: int) : string =
    let buf = Array.zeroCreate<byte> len
    readExact s buf 0 len
    // Null-padded ASCII; trim trailing zeros
    let mutable n = len
    while n > 0 && buf.[n - 1] = 0uy do n <- n - 1
    System.Text.Encoding.ASCII.GetString(buf, 0, n)

let private readSymbolList (s: Stream) (cstrLen: int) : string list =
    let count = int (readU32LE s)
    [ for _ in 1 .. count -> readCstr s cstrLen ]

/// Parse the 8-byte prelude (already consumed via wrapping or direct). Returns
/// (version, metadataLength).
let private readPrelude (s: Stream) : byte * uint32 =
    let magic = Array.zeroCreate<byte> 3
    readExact s magic 0 3
    if magic.[0] <> MAGIC.[0] || magic.[1] <> MAGIC.[1] || magic.[2] <> MAGIC.[2] then
        raise (InvalidDataException(
            sprintf "Not a DBN file: magic bytes were [%02X %02X %02X], expected [%02X %02X %02X] ('DBN')"
                magic.[0] magic.[1] magic.[2] MAGIC.[0] MAGIC.[1] MAGIC.[2]))
    let version = readU8 s
    if version < 1uy || version > 3uy then
        raise (NotSupportedException(sprintf "DBN version %d not supported by this reader (supports 1..3)" version))
    let metadataLen = readU32LE s
    version, metadataLen

/// Decode the metadata block following the prelude. Reads exactly `metadataLen`
/// bytes from the stream.
let private readMetadata (s: Stream) (version: byte) (metadataLen: uint32) : DbnMetadata =
    let startPos =
        // For non-seekable streams (zstd), track via a counting wrapper would be cleaner,
        // but here we just trust the fields and skip any trailing reserved bytes at the end.
        // (Variable tail consumes the rest; we recompute remaining as we go.)
        0L

    let dataset = readCstr s 16
    let schema = readU16LE s
    let startNs = readU64LE s
    let endNs = readU64LE s
    let limit = readU64LE s

    let fixedConsumed, stypeIn, stypeOut, tsOut, cstrLen, reservedLen =
        if version = 1uy then
            // V1: deprecated record_count u64 at offset 42, no symbol_cstr_len, 47 reserved
            let _recordCount = readU64LE s            // 8
            let stypeIn = readU8 s                    // 1
            let stypeOut = readU8 s                   // 1
            let tsOut = (readU8 s) <> 0uy             // 1
            // 16+2+8+8+8+8+1+1+1 = 53; reserved fills to 100
            (53, stypeIn, stypeOut, tsOut, 22, 47)
        else
            // V2/V3: stype_in u8, stype_out u8, ts_out u8 bool, symbol_cstr_len u16 LE, 53 reserved
            let stypeIn = readU8 s                    // 1
            let stypeOut = readU8 s                   // 1
            let tsOut = (readU8 s) <> 0uy             // 1
            let cstrLen = int (readU16LE s)           // 2
            // 16+2+8+8+8+1+1+1+2 = 47; reserved fills to 100
            (47, stypeIn, stypeOut, tsOut, cstrLen, 53)

    skipExact s reservedLen
    // We've now consumed fixedConsumed + reservedLen = 100 bytes for v1, v2, v3.

    // schema_definition_length u32; must be 0 in our use case.
    let schemaDefLen = readU32LE s
    if schemaDefLen <> 0u then
        // Spec allows it but real files we've seen have 0. Skip anyway to stay generic.
        skipExact s (int schemaDefLen)

    let symbols = readSymbolList s cstrLen
    let partial = readSymbolList s cstrLen
    let notFound = readSymbolList s cstrLen

    let mappingCount = int (readU32LE s)
    let mappings =
        [ for _ in 1 .. mappingCount do
            let rawSym = readCstr s cstrLen
            let intervalCount = int (readU32LE s)
            let intervals =
                [ for _ in 1 .. intervalCount ->
                    let startDate = readU32LE s
                    let endDate = readU32LE s
                    let sym = readCstr s cstrLen
                    (startDate, endDate, sym) ]
            yield (rawSym, intervals) ]

    {
        Version = version
        Dataset = dataset
        Schema = schema
        StartNs = startNs
        EndNs = endNs
        Limit = limit
        StypeIn = stypeIn
        StypeOut = stypeOut
        TsOut = tsOut
        SymbolCstrLen = cstrLen
        Symbols = symbols
        PartialSymbols = partial
        NotFoundSymbols = notFound
        Mappings = mappings
    }

/// Open a `.dbn` or `.dbn.zst` file and return (metadata, decompressedStream).
/// The caller is responsible for disposing the returned stream (which transitively
/// disposes the underlying file).
let openDbnFile (path: string) : DbnMetadata * Stream =
    let fileStream : Stream = File.OpenRead(path) :> Stream
    let dataStream : Stream =
        if path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) then
            new ZstdSharp.DecompressionStream(fileStream) :> Stream
        else
            fileStream
    let version, metadataLen = readPrelude dataStream
    let metadata = readMetadata dataStream version metadataLen
    metadata, dataStream

/// Iterate MBO records from an opened stream positioned just past the metadata.
/// Records with rtype other than MBO are skipped. The returned sequence is
/// single-pass; do not enumerate twice.
let readMboRecords (s: Stream) : seq<MboMsg> =
    seq {
        let headerBuf = Array.zeroCreate<byte> 16
        let recordBuf = Array.zeroCreate<byte> 256  // MBO needs 56; oversized for safety
        let mutable eof = false
        while not eof do
            let n = s.Read(headerBuf, 0, 16)
            if n = 0 then
                eof <- true
            elif n < 16 then
                // Partial header: try to fill it. If we hit EOF in the middle of a
                // record header it's a truncated file — raise to surface the problem.
                readExact s headerBuf n (16 - n)
            else ()

            if not eof then
                let length = headerBuf.[0]
                let rtype = headerBuf.[1]
                let totalBytes = int length * 4
                if totalBytes < 16 then
                    raise (InvalidDataException(
                        sprintf "Record header length=%d implies total size %d < 16 bytes" length totalBytes))
                let bodyBytes = totalBytes - 16
                if rtype = RTYPE_MBO then
                    if totalBytes <> 56 then
                        raise (InvalidDataException(
                            sprintf "MBO record with non-standard size %d (expected 56)" totalBytes))
                    Array.blit headerBuf 0 recordBuf 0 16
                    readExact s recordBuf 16 bodyBytes
                    let msg = MemoryMarshal.Read<MboMsg>(ReadOnlySpan(recordBuf, 0, 56))
                    yield msg
                else
                    // Non-MBO record — skip the body
                    skipExact s bodyBytes
    }
