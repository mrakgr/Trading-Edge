module TradingEdge.ReplaySimulatorV2.Trades

// Narrow projection of MboMsg for the T&S window. We don't need order_id,
// flags, ts_recv, sequence, etc. once a trade has occurred — only the fields
// the tape display cares about.

open System.Runtime.InteropServices
open TradingEdge.ReplaySimulatorV2.MboReader

[<Struct; StructLayout(LayoutKind.Sequential, Pack = 1)>]
type TradeMsg = {
    TsEvent: int64         // ns since epoch
    Price: int64           // 1e-9 USD
    Size: uint32
    Side: byte             // 'A' / 'B' / 'N'  (resting-book side; aggressor is the opposite)
    PublisherId: uint16
}

let fromMbo (m: MboMsg) : TradeMsg =
    {
        TsEvent = m.TsEvent
        Price = m.Price
        Size = m.Size
        Side = m.Side
        PublisherId = m.PublisherId
    }
