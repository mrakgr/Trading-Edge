module TradingEdge.ReplaySimulatorV1.MboReader

// Reader for MBO (Market-By-Order) records from a DBN stream. Assumes the
// stream is positioned just past the metadata block (i.e. the caller has
// already consumed prelude + header + tail via Dbn.readMetadata).
//
// MBO files are expected to contain MBO records only; an rtype other than
// 0xA0 surfaces as an InvalidDataException.

open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open FSharp.Control
open TradingEdge.ReplaySimulatorV1.Dbn

let private RTYPE_MBO : byte = 0xA0uy

/// 56-byte packed MBO record. Layout matches DBN v1/v2/v3 — the wire format
/// of MBO records did not change across versions.
[<Struct; StructLayout(LayoutKind.Sequential, Pack = 1)>]
type MboMsg = {
    // ---- RecordHeader (16 bytes) ----
    Length: byte                // total record size in 32-bit words; MBO = 14
    RType: byte                 // 0xA0 for MBO
    PublisherId: uint16
    InstrumentId: uint32
    TsEvent: int64              // nanos since epoch (Databento clock)
    // ---- MBO body (40 bytes) ----
    OrderId: uint64
    Price: int64                // 1e-9 USD (e.g. 12_345_000_000 → $12.345)
    Size: uint32
    Flags: byte
    ChannelId: byte
    Action: byte                // 'A' add / 'C' cancel / 'M' modify / 'R' clear / 'T' trade / 'F' fill
    Side: byte                  // 'A' ask / 'B' bid / 'N' none
    TsRecv: int64               // nanos since epoch (gateway recv clock)
    TsInDelta: int32
    Sequence: uint32
}

/// Iterate MBO records from a stream positioned just past the metadata.
/// Yields one MboMsg per record until clean EOF. A non-MBO rtype raises
/// InvalidDataException.
let readMboRecords (s: Stream) : IAsyncEnumerable<MboMsg> = taskSeq {
    let mutable keepGoing = true
    while keepGoing do
        let! maybeMsg = tryReadStruct<MboMsg> s
        match maybeMsg with
        | None -> keepGoing <- false
        | Some m ->
            if m.RType <> RTYPE_MBO then
                raise (InvalidDataException(
                    sprintf "Expected MBO record (rtype=0x%02X) but got rtype=0x%02X at sequence=%d"
                        RTYPE_MBO m.RType m.Sequence))
            yield m
}

/// K-way merge of a list of sorted MBO record sequences, ordered by ts_event.
/// Each input must already be in non-decreasing ts_event order (true for raw
/// per-venue DBN streams). Uses a min-heap keyed on ts_event.
let mergeByTsEvent (streams: IAsyncEnumerable<MboMsg> list) : IAsyncEnumerable<MboMsg> =
    taskSeq {
        let enumerators =
            streams
            |> List.map (fun s -> s.GetAsyncEnumerator())
        try
            // Priority queue keyed on (ts_event, source index) so ties are stable.
            let pq = PriorityQueue<int, struct (int64 * int)>()
            for i in 0 .. enumerators.Length - 1 do
                let e = enumerators.[i]
                let! b = e.MoveNextAsync()
                if b then
                    pq.Enqueue(i, struct (e.Current.TsEvent, i))
            while pq.Count > 0 do
                let i = pq.Dequeue()
                let e = enumerators.[i]
                yield e.Current
                let! b = e.MoveNextAsync()
                if b then
                    pq.Enqueue(i, struct (e.Current.TsEvent, i))
        finally
            for e in enumerators do 
                let! _ = e.DisposeAsync() in ()
    }