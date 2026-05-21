module TradingEdge.ReplaySimulatorV3.Bars

// 1-minute OHLCV+VWAP bars. Pure functional: feed takes a bar list (head =
// current/most-recent bar) and an MboMsg, returns a new bar list. A trade in
// the same bucket replaces the head; a trade in a new bucket prepends a new
// head; a non-trade record is a no-op (returns the input list unchanged).
//
// Session-VWAP state (running notional / volume since RTH open) lives on the
// head bar so the list is self-contained — no auxiliary aggregator needed.

open System
open TradingEdge.ReplaySimulatorV3.MboReader

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
    BarNotional: float          // running notional within this bar (price * size sum)
    SessionNotional: float      // running notional since RTH open (cumulative across bars)
    SessionVolume: int64        // running volume since RTH open (cumulative across bars)
}

let barVwap (b: Bar) : float =
    if b.Volume > 0L then b.BarNotional / float b.Volume else b.Close

let sessionVwap (b: Bar) : float option =
    if b.SessionVolume > 0L then Some (b.SessionNotional / float b.SessionVolume) else None

let private NY_TZ =
    try TimeZoneInfo.FindSystemTimeZoneById("America/New_York")
    with _ -> TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")

let private RTH_OPEN = TimeSpan(9, 30, 0)

let isAtOrAfterRthOpen (utcNs: int64) : bool =
    let utc = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(utcNs / 100L)
    let ny = TimeZoneInfo.ConvertTimeFromUtc(utc, NY_TZ)
    ny.TimeOfDay >= RTH_OPEN

/// Fold an MBO record into a bar list. Head = current (most-recent) bar.
/// Non-trade records return `bars` unchanged. A trade in the head's bucket
/// updates the head in place (cons new head, drop old head). A trade in a
/// later bucket prepends a fresh head, leaving the prior head closed at the
/// front of the tail.
let feed (bars: Bar list) (m: MboMsg) : Bar list =
    if m.Action <> ACTION_T then bars
    else
        let price = priceToUsd m.Price
        let size = int64 m.Size
        let notional = price * float size
        let bucketStartNs = (m.TsEvent / BAR_NS) * BAR_NS
        let rthAdd = isAtOrAfterRthOpen m.TsEvent
        match bars with
        | head :: tail when head.BucketStartNs = bucketStartNs ->
            let updated = {
                head with
                    High = if price > head.High then price else head.High
                    Low = if price < head.Low then price else head.Low
                    Close = price
                    Volume = head.Volume + size
                    TradeCount = head.TradeCount + 1
                    BarNotional = head.BarNotional + notional
                    SessionNotional = if rthAdd then head.SessionNotional + notional else head.SessionNotional
                    SessionVolume = if rthAdd then head.SessionVolume + size else head.SessionVolume
            }
            updated :: tail
        | _ ->
            // New bucket (or first bar). Carry session totals forward from the
            // prior head, if any.
            let priorSessNotional, priorSessVol =
                match bars with
                | h :: _ -> h.SessionNotional, h.SessionVolume
                | [] -> 0.0, 0L
            let newHead = {
                BucketStartNs = bucketStartNs
                Open = price
                High = price
                Low = price
                Close = price
                Volume = size
                TradeCount = 1
                BarNotional = notional
                SessionNotional = if rthAdd then priorSessNotional + notional else priorSessNotional
                SessionVolume = if rthAdd then priorSessVol + size else priorSessVol
            }
            newHead :: bars
