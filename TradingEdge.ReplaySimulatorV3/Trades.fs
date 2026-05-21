module TradingEdge.ReplaySimulatorV3.Trades

// Narrow projection of MboMsg for the T&S window. We don't need order_id,
// flags, ts_recv, sequence, etc. once a trade has occurred — only the fields
// the tape display cares about.

open System
open System.Runtime.InteropServices
open TradingEdge.ReplaySimulatorV3.MboReader

[<Struct; StructLayout(LayoutKind.Sequential, Pack = 1)>]
type TradeMsg = {
    TsEvent: int64         // ns since epoch
    Price: int64           // 1e-9 USD
    Size: uint32
    Side: byte             // 'A' / 'B' / 'N'  (aggressor side; 'A' = ask-seller hit a bid, 'B' = bid-buyer hit an ask, 'N' = off-book/auction/TRF)
    PublisherId: uint16
}

let private ACTION_TRADE : byte = byte 'T'

let fromMbo (m: MboMsg) : TradeMsg =
    if m.Action <> ACTION_TRADE then
        invalidArg "m"
            (sprintf "Trades.fromMbo expects a trade record (action='T') but got action='%c' at sequence=%d"
                (char m.Action) m.Sequence)
    {
        TsEvent = m.TsEvent
        Price = m.Price
        Size = m.Size
        Side = m.Side
        PublisherId = m.PublisherId
    }
