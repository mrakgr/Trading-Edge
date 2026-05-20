module TradingEdge.ReplaySimulatorV1.Book

// L3 order book per venue. Two representations:
//
//   * MutableBook — used during the eager build pass. Hot-path: O(log levels)
//     for adds, O(1) order_id lookup via a side dictionary.
//
//   * L3Book — immutable snapshot. F# Map per side, ImmutableList<orderId*size>
//     per level preserves FIFO queue order. Cheap to share across snapshots.
//
// Convert mutable -> immutable at snapshot time. The reverse direction (rehydrate
// for further mutation after a seek) is handled inside Play.fs.

open System
open System.Collections.Generic
open System.Collections.Immutable
open TradingEdge.ReplaySimulatorV1.MboReader

let private SIDE_ASK : byte = byte 'A'
let private SIDE_BID : byte = byte 'B'

let private ACTION_A : byte = byte 'A'
let private ACTION_C : byte = byte 'C'
let private ACTION_M : byte = byte 'M'
let private ACTION_R : byte = byte 'R'
let private ACTION_F : byte = byte 'F'
let private ACTION_T : byte = byte 'T'

// ---------------------------------------------------------------------------
// Immutable representation (used inside snapshots).
// ---------------------------------------------------------------------------

/// A single resting order: id + remaining size.
type Order = { OrderId: uint64; Size: uint32 }

/// Immutable L3 book. Each side is price -> FIFO queue of orders.
/// Price keys are raw DBN nano-USD (int64).
type L3Book = {
    Bids: Map<int64, ImmutableList<Order>>
    Asks: Map<int64, ImmutableList<Order>>
}

let emptyL3Book : L3Book = { Bids = Map.empty; Asks = Map.empty }

// ---------------------------------------------------------------------------
// Mutable representation (build-pass hot path).
// ---------------------------------------------------------------------------

/// Location of an order in the mutable book: (side, price). The queue itself
/// is looked up via the side dictionaries.
[<Struct>]
type OrderLocation = { Side: byte; Price: int64 }

type MutableBook() =
    // SortedDictionary so we can iterate in price order for snapshots and for
    // best-bid/best-ask queries. The per-level queue uses LinkedList for O(1)
    // append and removal-by-id (when paired with the OrderIndex below).
    let bids = SortedDictionary<int64, LinkedList<Order>>()
    let asks = SortedDictionary<int64, LinkedList<Order>>()
    // order_id -> (location, linked-list node) for O(1) cancel/modify.
    let index = Dictionary<uint64, struct (OrderLocation * LinkedListNode<Order>)>()

    let sideMap (s: byte) =
        if s = SIDE_BID then bids
        elif s = SIDE_ASK then asks
        else failwithf "unexpected side byte 0x%02X" s

    let getOrCreateLevel (side: SortedDictionary<int64, LinkedList<Order>>) (price: int64) =
        match side.TryGetValue price with
        | true, q -> q
        | false, _ ->
            let q = LinkedList<Order>()
            side.Add(price, q)
            q

    let removeLevelIfEmpty (side: SortedDictionary<int64, LinkedList<Order>>) (price: int64) =
        match side.TryGetValue price with
        | true, q when q.Count = 0 -> side.Remove(price) |> ignore
        | _ -> ()

    member _.ApplyAdd (m: MboMsg) =
        let side = sideMap m.Side
        let q = getOrCreateLevel side m.Price
        let node = q.AddLast({ OrderId = m.OrderId; Size = m.Size })
        index.[m.OrderId] <- struct ({ Side = m.Side; Price = m.Price }, node)

    member _.ApplyCancel (m: MboMsg) =
        match index.TryGetValue m.OrderId with
        | false, _ -> ()   // unknown order — venue may have sent the Add before our session start
        | true, struct (loc, node) ->
            let side = sideMap loc.Side
            match side.TryGetValue loc.Price with
            | true, q ->
                q.Remove(node)
                removeLevelIfEmpty side loc.Price
            | _ -> ()
            index.Remove(m.OrderId) |> ignore

    member this.ApplyModify (m: MboMsg) =
        match index.TryGetValue m.OrderId with
        | false, _ ->
            // Never seen this order — treat as Add (but a 0-size add is meaningless).
            if m.Size > 0u then this.ApplyAdd m
        | true, struct (loc, node) ->
            if m.Size = 0u then
                // Modify-to-zero is functionally a Cancel.
                this.ApplyCancel m
            elif loc.Price = m.Price && m.Size <= node.Value.Size then
                // Same price + size decrease (or unchanged) → keep priority.
                node.Value <- { OrderId = m.OrderId; Size = m.Size }
            else
                // Different price or size increase → lose priority.
                this.ApplyCancel m
                this.ApplyAdd m

    member _.ApplyFill (m: MboMsg) =
        // F decrements the size of the specific resting order. When size hits 0
        // the order is fully filled and removed.
        match index.TryGetValue m.OrderId with
        | false, _ -> ()
        | true, struct (loc, node) ->
            let remaining =
                if node.Value.Size > m.Size then node.Value.Size - m.Size
                else 0u
            if remaining = 0u then
                let side = sideMap loc.Side
                match side.TryGetValue loc.Price with
                | true, q ->
                    q.Remove(node)
                    removeLevelIfEmpty side loc.Price
                | _ -> ()
                index.Remove(m.OrderId) |> ignore
            else
                node.Value <- { OrderId = m.OrderId; Size = remaining }

    member _.ApplyClear () =
        bids.Clear()
        asks.Clear()
        index.Clear()

    /// Freeze the current mutable state into an immutable L3Book.
    /// O(total levels × avg-queue-depth) — call only at snapshot boundaries.
    member _.Freeze () : L3Book =
        let toImmut (side: SortedDictionary<int64, LinkedList<Order>>) =
            let b = Map.empty<int64, ImmutableList<Order>>
            (b, side) ||> Seq.fold (fun acc kv ->
                let builder = ImmutableList.CreateBuilder<Order>()
                for o in kv.Value do builder.Add o
                Map.add kv.Key (builder.ToImmutable()) acc)
        { Bids = toImmut bids; Asks = toImmut asks }

    /// Rehydrate the mutable book from an immutable snapshot. Clears existing state.
    member _.Hydrate (book: L3Book) =
        bids.Clear()
        asks.Clear()
        index.Clear()
        let load (target: SortedDictionary<int64, LinkedList<Order>>) (side: byte) (src: Map<int64, ImmutableList<Order>>) =
            for kv in src do
                let q = LinkedList<Order>()
                for o in kv.Value do
                    let node = q.AddLast(o)
                    index.[o.OrderId] <- struct ({ Side = side; Price = kv.Key }, node)
                target.Add(kv.Key, q)
        load bids SIDE_BID book.Bids
        load asks SIDE_ASK book.Asks

/// Dispatch an MBO record to the appropriate Apply method. T records are
/// ignored (they don't affect the book — F records do the actual order
/// decrements). Returns true if the book was potentially mutated.
let applyToBook (book: MutableBook) (m: MboMsg) : bool =
    match m.Action with
    | a when a = ACTION_A -> book.ApplyAdd m; true
    | a when a = ACTION_C -> book.ApplyCancel m; true
    | a when a = ACTION_M -> book.ApplyModify m; true
    | a when a = ACTION_R -> book.ApplyClear (); true
    | a when a = ACTION_F -> book.ApplyFill m; true
    | a when a = ACTION_T -> false   // trades don't touch the book; F records decrement orders
    | _ -> false
