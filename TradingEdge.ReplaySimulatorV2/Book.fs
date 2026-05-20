module TradingEdge.ReplaySimulatorV2.Book

// V2: purely immutable L3 order book. Every Apply* returns a new L3Book that
// shares all unchanged levels with the input via F# Map's structural sharing.
//
// Layout:
//   * Bids/Asks: Map<price, Level> where Level = Map<arrivalSeq, Order>.
//     Per-level Map preserves FIFO order via a monotonically-increasing
//     arrivalSeq key. All ops on the level are O(log queue).
//   * Index: Map<order_id, (side, price, arrivalSeq)>. Lets Cancel/Modify
//     locate the order without scanning. O(log n) lookup.
//   * NextSeq: u64 counter, incremented per Add. Never recycled; arbitrarily
//     large, but ~13M events/day << 2^64 even over years.
//
// Compared to V1's MutableBook: ~2× more allocations per event, but enables
// O(1) snapshot capture (just hold the reference) with full structural sharing.

open TradingEdge.ReplaySimulatorV2.MboReader

let private SIDE_ASK : byte = byte 'A'
let private SIDE_BID : byte = byte 'B'

let private ACTION_A : byte = byte 'A'
let private ACTION_C : byte = byte 'C'
let private ACTION_M : byte = byte 'M'
let private ACTION_R : byte = byte 'R'
let private ACTION_F : byte = byte 'F'
let private ACTION_T : byte = byte 'T'

type Order = { OrderId: uint64; Size: uint32 }

/// FIFO queue at a single price level. Keys are monotonically-increasing
/// arrival sequence numbers (smallest = oldest).
type Level = Map<uint64, Order>

type L3Book = {
    Bids: Map<int64, Level>
    Asks: Map<int64, Level>
    /// Order-id → (side, price, arrivalSeq) so Cancel/Modify can locate orders.
    Index: Map<uint64, struct (byte * int64 * uint64)>
    /// Counter for the next Add's arrivalSeq. Monotonically increasing.
    NextSeq: uint64
}

let empty : L3Book = {
    Bids = Map.empty
    Asks = Map.empty
    Index = Map.empty
    NextSeq = 0UL
}

let private sideOf (b: L3Book) (side: byte) : Map<int64, Level> =
    if side = SIDE_BID then b.Bids
    elif side = SIDE_ASK then b.Asks
    else failwithf "unexpected side byte 0x%02X" side

let private withSide (b: L3Book) (side: byte) (m: Map<int64, Level>) : L3Book =
    if side = SIDE_BID then { b with Bids = m }
    elif side = SIDE_ASK then { b with Asks = m }
    else failwithf "unexpected side byte 0x%02X" side

/// Add an order at (side, price). Returns the new book.
let applyAdd (b: L3Book) (m: MboMsg) : L3Book =
    let seq = b.NextSeq
    let order = { OrderId = m.OrderId; Size = m.Size }
    let side = sideOf b m.Side
    let level =
        match Map.tryFind m.Price side with
        | Some lvl -> Map.add seq order lvl
        | None -> Map.ofList [ seq, order ]
    let side' = Map.add m.Price level side
    let b' = withSide b m.Side side'
    {
        b' with
            Index = Map.add m.OrderId (struct (m.Side, m.Price, seq)) b'.Index
            NextSeq = seq + 1UL
    }

/// Internal: remove the order identified by (side, price, arrivalSeq) from the
/// book without touching the index.
let private removeOrderFromBook (b: L3Book) (side: byte) (price: int64) (seq: uint64) : L3Book =
    let sideMap = sideOf b side
    match Map.tryFind price sideMap with
    | None -> b
    | Some lvl ->
        let lvl' = Map.remove seq lvl
        let sideMap' =
            if Map.isEmpty lvl' then Map.remove price sideMap
            else Map.add price lvl' sideMap
        withSide b side sideMap'

/// Cancel an order. No-op if the order_id isn't in the index.
let applyCancel (b: L3Book) (m: MboMsg) : L3Book =
    match Map.tryFind m.OrderId b.Index with
    | None -> b
    | Some (struct (side, price, seq)) ->
        let b' = removeOrderFromBook b side price seq
        { b' with Index = Map.remove m.OrderId b'.Index }

/// Decrement a resting order by m.Size. If the result is 0, the order is fully
/// filled and removed. No-op if the order_id isn't in the index.
let applyFill (b: L3Book) (m: MboMsg) : L3Book =
    match Map.tryFind m.OrderId b.Index with
    | None -> b
    | Some (struct (side, price, seq)) ->
        let sideMap = sideOf b side
        match Map.tryFind price sideMap with
        | None -> b
        | Some lvl ->
            match Map.tryFind seq lvl with
            | None -> b
            | Some order ->
                let remaining = if order.Size > m.Size then order.Size - m.Size else 0u
                if remaining = 0u then
                    let lvl' = Map.remove seq lvl
                    let sideMap' =
                        if Map.isEmpty lvl' then Map.remove price sideMap
                        else Map.add price lvl' sideMap
                    let b' = withSide b side sideMap'
                    { b' with Index = Map.remove m.OrderId b'.Index }
                else
                    let lvl' = Map.add seq { order with Size = remaining } lvl
                    let sideMap' = Map.add price lvl' sideMap
                    withSide b side sideMap'

/// Modify an order. Same-price + size-decrease keeps priority (update in place).
/// Anything else is cancel + re-add at the back of the new level.
let applyModify (b: L3Book) (m: MboMsg) : L3Book =
    match Map.tryFind m.OrderId b.Index with
    | None ->
        if m.Size > 0u then applyAdd b m else b
    | Some (struct (side, price, seq)) ->
        if m.Size = 0u then applyCancel b m
        elif price = m.Price then
            let sideMap = sideOf b side
            match Map.tryFind price sideMap with
            | None -> applyAdd (applyCancel b m) m
            | Some lvl ->
                match Map.tryFind seq lvl with
                | None -> applyAdd (applyCancel b m) m
                | Some order ->
                    if m.Size <= order.Size then
                        // Keep priority: update size in place.
                        let lvl' = Map.add seq { order with Size = m.Size } lvl
                        let sideMap' = Map.add price lvl' sideMap
                        withSide b side sideMap'
                    else
                        // Size increase: lose priority.
                        applyAdd (applyCancel b m) m
        else
            // Price changed: lose priority.
            applyAdd (applyCancel b m) m

/// Drop the entire book.
let applyClear (_: L3Book) : L3Book = empty

/// Dispatch by action. T is a no-op for the book; F records do the actual
/// order decrements. Returns the new book (possibly the same reference).
let applyToBook (b: L3Book) (m: MboMsg) : L3Book =
    match m.Action with
    | a when a = ACTION_A -> applyAdd b m
    | a when a = ACTION_C -> applyCancel b m
    | a when a = ACTION_M -> applyModify b m
    | a when a = ACTION_R -> applyClear b
    | a when a = ACTION_F -> applyFill b m
    | a when a = ACTION_T -> b
    | _ -> b
