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
open System.IO
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Threading.Tasks
open System.Threading.Tasks

let MAGIC : byte[] = [| byte 'D'; byte 'B'; byte 'N' |]

// Fixed-size byte buffers, embedded inline in their parent struct (no heap
// allocation, fully unmanaged). These let MemoryMarshal.Read<T> work directly.

[<Struct; InlineArray(16)>] type Bytes16 = private { mutable _e: byte }
[<Struct; InlineArray(47)>] type Bytes47 = private { mutable _e: byte }
[<Struct; InlineArray(53)>] type Bytes53 = private { mutable _e: byte }

/// 8-byte file prelude.
[<Struct; StructLayout(LayoutKind.Sequential, Pack = 1)>]
type DbnPrelude = {
    Magic0: byte
    Magic1: byte
    Magic2: byte
    Version: byte
    MetadataLength: uint32
}

/// 100-byte v1 fixed metadata header. Symbol cstr length is implicitly 22.
[<Struct; StructLayout(LayoutKind.Sequential, Pack = 1)>]
type DbnHeaderV1 = {
    mutable Dataset: Bytes16
    mutable Schema: uint16
    mutable Start: uint64
    mutable End: uint64
    mutable Limit: uint64
    mutable RecordCount: uint64    // deprecated in v1, still present in layout
    mutable StypeIn: byte
    mutable StypeOut: byte
    mutable TsOut: byte
    mutable Reserved: Bytes47
}

/// 100-byte v2/v3 fixed metadata header.
[<Struct; StructLayout(LayoutKind.Sequential, Pack = 1)>]
type DbnHeaderV2 = {
    mutable Dataset: Bytes16
    mutable Schema: uint16
    mutable Start: uint64
    mutable End: uint64
    mutable Limit: uint64
    mutable StypeIn: byte
    mutable StypeOut: byte
    mutable TsOut: byte
    mutable SymbolCstrLen: uint16
    mutable Reserved: Bytes53
}

/// Read sizeof<'T> bytes from the stream and reinterpret as 'T. Throws
/// EndOfStreamException on a short read.
let inline readStruct<'T when 'T : (new : unit -> 'T) and 'T : struct and 'T :> ValueType> (s: Stream) : 'T Task = task {
    let arr = Array.zeroCreate<byte> sizeof<'T>
    do! s.ReadExactlyAsync(Memory(arr))
    return MemoryMarshal.Read<'T>(ReadOnlySpan(arr))
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
