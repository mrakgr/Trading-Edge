module TradingEdge.ReplaySimulatorV2.Bars

// 1-minute OHLCV+VWAP bar aggregator. Ported from v0's
// TradingEdge.ReplaySimulator/Bars.fs:24-157. Adds freeze/hydrate so the
// aggregator's state can be embedded in a snapshot and resumed later.

open System
open TradingEdge.ReplaySimulatorV2.MboReader

let private ACTION_T : byte = byte 'T'

/// DBN MBO prices are 1e-9 USD.
let priceToUsd (p: int64) : float = float p * 1e-9

/// 1-minute bucket size in ns.
let private BAR_NS : int64 = 60L * 1_000_000_000L

type Bar = {
    BucketStartNs: int64
    Open: float
    High: float
    Low: float
    Close: float
    Volume: int64
    TradeCount: int
    BarVwap: float
    SessionVwap: float option   // None before RTH open
}

let private NY_TZ =
    try TimeZoneInfo.FindSystemTimeZoneById("America/New_York")
    with _ -> TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")

let private RTH_OPEN = TimeSpan(9, 30, 0)

let isAtOrAfterRthOpen (utcNs: int64) : bool =
    let utc = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(utcNs / 100L)
    let ny = TimeZoneInfo.ConvertTimeFromUtc(utc, NY_TZ)
    ny.TimeOfDay >= RTH_OPEN

type FeedResult =
    | NoChange
    | Forming of Bar
    | Closed of closedBar: Bar * newCurrent: Bar

/// Frozen snapshot of the aggregator's mutable state. Pure value, safe to hold
/// in an immutable snapshot.
type BarAggregatorSnapshot = {
    CurrentBucket: int64
    BOpen: float
    BHigh: float
    BLow: float
    BClose: float
    BVol: int64
    BCount: int
    BNotional: float
    SessNotional: float
    SessVol: int64
}

type BarAggregator() =
    let mutable currentBucket = Int64.MinValue
    let mutable bOpen = 0.0
    let mutable bHigh = Double.MinValue
    let mutable bLow = Double.MaxValue
    let mutable bClose = 0.0
    let mutable bVol = 0L
    let mutable bCount = 0
    let mutable bNotional = 0.0
    let mutable sessNotional = 0.0
    let mutable sessVol = 0L

    let snapshot () : Bar =
        let barVwap = if bVol > 0L then bNotional / float bVol else bClose
        let sessVwap = if sessVol > 0L then Some (sessNotional / float sessVol) else None
        {
            BucketStartNs = currentBucket
            Open = bOpen
            High = bHigh
            Low = bLow
            Close = bClose
            Volume = bVol
            TradeCount = bCount
            BarVwap = barVwap
            SessionVwap = sessVwap
        }

    member _.Current : Bar option =
        if bCount = 0 then None else Some (snapshot ())

    member _.Freeze () : BarAggregatorSnapshot =
        {
            CurrentBucket = currentBucket
            BOpen = bOpen
            BHigh = bHigh
            BLow = bLow
            BClose = bClose
            BVol = bVol
            BCount = bCount
            BNotional = bNotional
            SessNotional = sessNotional
            SessVol = sessVol
        }

    member _.Hydrate (s: BarAggregatorSnapshot) =
        currentBucket <- s.CurrentBucket
        bOpen <- s.BOpen
        bHigh <- s.BHigh
        bLow <- s.BLow
        bClose <- s.BClose
        bVol <- s.BVol
        bCount <- s.BCount
        bNotional <- s.BNotional
        sessNotional <- s.SessNotional
        sessVol <- s.SessVol

    member _.Feed (m: MboMsg) : FeedResult =
        if m.Action <> ACTION_T then NoChange
        else
            let price = priceToUsd m.Price
            let size = int64 m.Size
            let bucket = (m.TsEvent / BAR_NS) * BAR_NS
            if bucket <> currentBucket then
                let closed = if bCount > 0 then Some (snapshot ()) else None
                currentBucket <- bucket
                bOpen <- price
                bHigh <- price
                bLow <- price
                bClose <- price
                bVol <- size
                bCount <- 1
                bNotional <- price * float size
                if isAtOrAfterRthOpen m.TsEvent then
                    sessNotional <- sessNotional + bNotional
                    sessVol <- sessVol + size
                let newCur = snapshot ()
                match closed with
                | Some c -> Closed (c, newCur)
                | None -> Forming newCur
            else
                if price > bHigh then bHigh <- price
                if price < bLow then bLow <- price
                bClose <- price
                bVol <- bVol + size
                bCount <- bCount + 1
                let notional = price * float size
                bNotional <- bNotional + notional
                if isAtOrAfterRthOpen m.TsEvent then
                    sessNotional <- sessNotional + notional
                    sessVol <- sessVol + size
                Forming (snapshot ())
