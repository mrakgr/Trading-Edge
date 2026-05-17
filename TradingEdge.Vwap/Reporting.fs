module TradingEdge.Vwap.Reporting

open System
open System.IO
open System.Globalization
open TradingEdge.Vwap.Backtest

let private inv = CultureInfo.InvariantCulture

let private fmt (x: float) : string =
    if Double.IsNaN x then "nan"
    elif Double.IsPositiveInfinity x then "inf"
    elif Double.IsNegativeInfinity x then "-inf"
    else x.ToString("R", inv)

let private writeAtomic (path: string) (lines: seq<string>) =
    let dir = Path.GetDirectoryName path
    if not (String.IsNullOrEmpty dir) then
        Directory.CreateDirectory dir |> ignore
    let tmp = path + ".tmp"
    File.WriteAllLines(tmp, lines)
    if File.Exists path then File.Delete path
    File.Move(tmp, path)

let tradeHeader =
    "entry_date,entry_bucket,entry_price,entry_vwap,entry_reason,\
     exit_date,exit_bucket,exit_price,exit_vwap,exit_reason,\
     side,pnl,return,bars_held"

let private sideStr = function Long -> "long" | Short -> "short"

let private tradeRow (t: Trade) : string =
    String.concat "," [
        t.EntryDate.ToString("yyyy-MM-dd")
        string t.EntryBucket
        fmt t.EntryPrice
        fmt t.EntryVwap
        t.EntryReason
        t.ExitDate.ToString("yyyy-MM-dd")
        string t.ExitBucket
        fmt t.ExitPrice
        fmt t.ExitVwap
        t.ExitReason
        sideStr t.Side
        fmt t.PnL
        fmt t.Return
        string t.BarsHeld
    ]

let writeTrades (path: string) (trades: Trade[]) =
    writeAtomic path (seq {
        yield tradeHeader
        for t in trades -> tradeRow t
    })

let printSummary (m: Metrics) =
    printfn ""
    printfn "  Trades:           %d (long %d, short %d)"
        m.Trades m.LongTrades m.ShortTrades
    printfn "  Wins:             %d  (win rate %.1f%%)"
        m.Wins (m.WinRate * 100.0)
    printfn "  Long wins:        %d / %d  (%.1f%%)"
        m.LongWins m.LongTrades
        (if m.LongTrades > 0 then 100.0 * float m.LongWins / float m.LongTrades else 0.0)
    printfn "  Short wins:       %d / %d  (%.1f%%)"
        m.ShortWins m.ShortTrades
        (if m.ShortTrades > 0 then 100.0 * float m.ShortWins / float m.ShortTrades else 0.0)
    printfn "  Net P&L:          %+.2f/share" m.NetPnL
    printfn "    Long  P&L:      %+.2f/share" m.LongNetPnL
    printfn "    Short P&L:      %+.2f/share" m.ShortNetPnL
    printfn "  Profit factor:    %.3f" m.ProfitFactor
    printfn "  Gross wins:       %+.2f/share" m.GrossWins
    printfn "  Gross losses:     %+.2f/share" m.GrossLosses
    printfn "  Max drawdown:     %+.2f/share" m.MaxDrawdown
    printfn "  Avg bars held:    %.1f" m.AvgBarsHeld
