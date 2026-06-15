module TradingEdge.MomentumBacktest.Reporting

open System
open System.IO
open System.Globalization
open System.Text
open TradingEdge.MomentumBacktest.Types

let private inv = CultureInfo.InvariantCulture

let private fmt (x: float) : string =
    if Double.IsNaN x then "nan"
    elif Double.IsPositiveInfinity x then "inf"
    elif Double.IsNegativeInfinity x then "-inf"
    else x.ToString("R", inv)

/// tmp + move so a half-written file never replaces a good one (mirrors the
/// crypto backtest's writeAtomic).
let private writeAtomic (path: string) (lines: seq<string>) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    let tmp = path + ".tmp"
    File.WriteAllLines(tmp, lines)
    if File.Exists path then File.Delete path
    File.Move(tmp, path)

// ---------------------------------------------------------------------------
// Trips CSV
// ---------------------------------------------------------------------------

let tripsHeader =
    "symbol,entry_date,exit_date,side,entry_price,exit_price,qty,net_pnl,bars_held,entry_adj_volume,rvol_at_entry,avg_dollar_volume_4w_at_entry,pct_up_at_entry,atr_pct_14_at_entry,range_pct_14_at_entry,tightness_14_at_entry,exit_reason,open"

let private tripRow (t: Trip) : string =
    String.concat "," [
        t.Symbol
        t.EntryDate.ToString("yyyy-MM-dd")
        t.ExitDate.ToString("yyyy-MM-dd")
        "long"
        fmt t.EntryPrice
        fmt t.ExitPrice
        fmt t.Qty
        fmt t.NetPnL
        string t.BarsHeld
        string t.EntryAdjVolume
        fmt t.RvolAtEntry
        fmt t.AvgDollarVolume4wAtEntry
        fmt t.PctUpAtEntry
        fmt t.AtrPct14AtEntry
        fmt t.RangePct14AtEntry
        fmt t.Tightness14AtEntry
        t.ExitReason
        (if t.Open then "1" else "0")
    ]

let writeTrips (path: string) (trips: Trip[]) =
    let lines = seq {
        yield tripsHeader
        for t in trips -> tripRow t
    }
    writeAtomic path lines

// ---------------------------------------------------------------------------
// Monthly / yearly breakdown
// ---------------------------------------------------------------------------

/// One aggregated bucket: net P&L, count, win rate, profit factor.
type private Bucket = {
    Label: string
    Trades: int
    Wins: int
    WinRate: float
    NetPnL: float
    ProfitFactor: float
}

let private aggregate (label: string) (trips: Trip[]) : Bucket =
    let trades = trips.Length
    let wins = trips |> Array.filter (fun t -> t.NetPnL > 0.0) |> Array.length
    let grossWins = trips |> Array.sumBy (fun t -> if t.NetPnL > 0.0 then t.NetPnL else 0.0)
    let grossLosses = trips |> Array.sumBy (fun t -> if t.NetPnL < 0.0 then -t.NetPnL else 0.0)
    let pf =
        if grossLosses > 0.0 then grossWins / grossLosses
        elif grossWins > 0.0 then Double.PositiveInfinity
        else 0.0
    {
        Label = label
        Trades = trades
        Wins = wins
        WinRate = (if trades > 0 then float wins / float trades else 0.0)
        NetPnL = trips |> Array.sumBy (fun t -> t.NetPnL)
        ProfitFactor = pf
    }

/// Bucketed by ENTRY date — "when the system fired" — which is the natural view
/// for "which periods does this system shine". (Exit-date bucketing would smear a
/// multi-month winner across its holding period.)
let private byYear (trips: Trip[]) : Bucket[] =
    trips
    |> Array.groupBy (fun t -> t.EntryDate.Year)
    |> Array.sortBy fst
    |> Array.map (fun (y, g) -> aggregate (string y) g)

let private byMonth (trips: Trip[]) : Bucket[] =
    trips
    |> Array.groupBy (fun t -> (t.EntryDate.Year, t.EntryDate.Month))
    |> Array.sortBy fst
    |> Array.map (fun ((y, m), g) -> aggregate (sprintf "%04d-%02d" y m) g)

/// Average-dollar-volume tiers (at entry), used as a proxy for market cap since
/// no shares-outstanding data exists yet. Boundaries in dollars; (rank, label)
/// keep the buckets in ascending liquidity order in the output. A trip's tier is
/// the first bound it falls under.
let private advTiers : (float * string)[] =
    [| 300e3, "$100-300k"
       1e6,   "$300k-1M"
       3e6,   "$1-3M"
       10e6,  "$3-10M"
       30e6,  "$10-30M"
       100e6, "$30-100M"
       1e9,   "$100M-1B"
       infinity, "$1B+" |]

let private advTierOf (adv: float) : int * string =
    let i = advTiers |> Array.findIndex (fun (hi, _) -> adv < hi)
    i, snd advTiers.[i]

let private byDollarVolume (trips: Trip[]) : Bucket[] =
    trips
    |> Array.groupBy (fun t -> advTierOf t.AvgDollarVolume4wAtEntry)
    |> Array.sortBy (fun ((rank, _), _) -> rank)
    |> Array.map (fun ((_, label), g) -> aggregate label g)

/// 14-day ATR% (at entry) tiers — the entry name's baseline daily volatility
/// going into the breakout, as a fraction of price. Upper bound per tier; a NaN
/// ATR (insufficient prior history) lands in its own bucket so it never silently
/// skews a real tier.
let private atrTiers : (float * string)[] =
    [| 0.03, "<3%"
       0.05, "3-5%"
       0.08, "5-8%"
       0.12, "8-12%"
       0.20, "12-20%"
       infinity, "20%+" |]

let private atrTierOf (atr: float) : int * string =
    if Double.IsNaN atr then atrTiers.Length, "n/a"
    else
        let i = atrTiers |> Array.findIndex (fun (hi, _) -> atr < hi)
        i, snd atrTiers.[i]

let private byAtr (trips: Trip[]) : Bucket[] =
    trips
    |> Array.groupBy (fun t -> atrTierOf t.AtrPct14AtEntry)
    |> Array.sortBy (fun ((rank, _), _) -> rank)
    |> Array.map (fun ((_, label), g) -> aggregate label g)

/// Consolidation-tightness tiers: 14-day span / (14 * 14-day ATR). Near 1.0 = the
/// prior window trended cleanly; well below 1 = price chopped in a tight band
/// relative to its daily travel (a "coiled spring"). Low tiers = tighter.
let private tightTiers : (float * string)[] =
    [| 0.40, "<0.40 (tight)"
       0.55, "0.40-0.55"
       0.70, "0.55-0.70"
       0.85, "0.70-0.85"
       infinity, "0.85+ (trend)" |]

let private tightTierOf (x: float) : int * string =
    if Double.IsNaN x then tightTiers.Length, "n/a"
    else
        let i = tightTiers |> Array.findIndex (fun (hi, _) -> x < hi)
        i, snd tightTiers.[i]

/// Which exit closed each trip — stop (15-day-low), expansion (tightness>thr),
/// or mtm (still open at end). Shows what share the expansion exit catches and
/// how the two exit populations differ in quality.
let private byExitReason (trips: Trip[]) : Bucket[] =
    trips
    |> Array.groupBy (fun t -> t.ExitReason)
    |> Array.sortBy fst
    |> Array.map (fun (r, g) -> aggregate r g)

let private byTightness (trips: Trip[]) : Bucket[] =
    trips
    |> Array.groupBy (fun t -> tightTierOf t.Tightness14AtEntry)
    |> Array.sortBy (fun ((rank, _), _) -> rank)
    |> Array.map (fun ((_, label), g) -> aggregate label g)

let private formatTable (title: string) (buckets: Bucket[]) : string =
    let sb = StringBuilder()
    sb.AppendLine(title) |> ignore
    sb.AppendLine(sprintf "%-14s %8s %7s %8s %15s" "period" "trades" "win%" "pf" "net_pnl") |> ignore
    sb.AppendLine(String.replicate 56 "-") |> ignore
    for b in buckets do
        let pfStr = if Double.IsInfinity b.ProfitFactor then "inf" else sprintf "%.2f" b.ProfitFactor
        // Bake the sign in so we can right-pad as a plain string (%+s is invalid in F#).
        let pnlStr =
            let s = b.NetPnL.ToString("N0", inv)
            if b.NetPnL >= 0.0 then "+" + s else s
        sb.AppendLine(
            sprintf "%-14s %8d %6.1f%% %8s %15s"
                b.Label b.Trades (b.WinRate * 100.0) pfStr pnlStr) |> ignore
    sb.ToString()

/// Print the by-year and by-month breakdowns to the console and append them
/// (with a header recording the run + the same-day-close caveat) to the log file.
let writeBreakdown (logPath: string) (cfg: Config) (trips: Trip[]) =
    let openTrips = trips |> Array.filter (fun t -> t.Open) |> Array.length
    let overall = aggregate "ALL" trips
    let header =
        let sb = StringBuilder()
        sb.AppendLine("=== momentum_v0 backtest breakdown ===") |> ignore
        sb.AppendLine(sprintf "generated:   %s" (DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))) |> ignore
        sb.AppendLine(sprintf "window:      %s .. %s"
            (cfg.StartDate.ToString("yyyy-MM-dd")) (cfg.EndDate.ToString("yyyy-MM-dd"))) |> ignore
        sb.AppendLine(sprintf "entry:       up>=%.0f%%, rvol>=%.1f, new %d-day high; tradable_only=%b; min_adv=%s"
            (cfg.UpThreshold * 100.0) cfg.RvolThreshold cfg.LookbackHigh cfg.TradableOnly
            (cfg.MinAvgDollarVolume.ToString("N0", inv))) |> ignore
        sb.AppendLine(sprintf "stop:        %d-day-low trailing, exit next open; notional=$%s/trade, uncapped, no compounding"
            cfg.StopLowWindow (cfg.Notional.ToString("N0", inv))) |> ignore
        sb.AppendLine(sprintf "exp.exit:    %s"
            (match cfg.ExpansionExitThreshold with
             | Some thr -> sprintf "tightness > %.2f -> exit next open (volatility-expansion exit ON)" thr
             | None -> "off (15-day-low stop only)")) |> ignore
        sb.AppendLine("caveat:      entry fills at the SAME-day close that defines the signal (mildly optimistic, by design).") |> ignore
        sb.AppendLine("             buckets are by ENTRY date. open trips are MTM'd at the final close.") |> ignore
        sb.AppendLine(sprintf "trips:       %d total (%d still open / MTM'd), net P&L $%s"
            overall.Trades openTrips (overall.NetPnL.ToString("N0", inv))) |> ignore
        sb.AppendLine() |> ignore
        sb.ToString()

    let yearTable = formatTable "--- by year (entry) ---" (byYear trips)
    let monthTable = formatTable "--- by month (entry) ---" (byMonth trips)
    let advTable = formatTable "--- by avg dollar volume at entry (market-cap proxy) ---" (byDollarVolume trips)
    let atrTable = formatTable "--- by 14-day ATR% at entry (name volatility) ---" (byAtr trips)
    let tightTable = formatTable "--- by 14-day tightness = range / (14 x ATR) at entry ---" (byTightness trips)
    let exitTable = formatTable "--- by exit reason ---" (byExitReason trips)

    let full = header + exitTable + "\n" + advTable + "\n" + atrTable + "\n" + tightTable + "\n" + yearTable + "\n" + monthTable

    // Console
    printfn "%s" full
    // Log (append; create dir)
    Directory.CreateDirectory(Path.GetDirectoryName logPath) |> ignore
    File.AppendAllText(logPath, full + "\n")
