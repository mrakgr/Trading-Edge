module TradingEdge.MomentumV1.Backtest

open System
open System.Globalization
open System.Collections.Generic
open DuckDB.NET.Data
open TradingEdge.MomentumV1.Types

/// Full system configuration. The locked v0 default is `defaultConfig`.
type Config =
    { StopLowWindow: int
      TrailWindow: int
      HiCloseWindow: int
      AtrWindow: int
      TightnessWindow: int
      VolDays: int
      ExpansionThr: float
      ExitTimeCap: int          // bars the sell limit may rest; 0 = exit next open (N ignored)
      Notional: float
      Entry: EntryConfig }

/// The locked production default (Qulla day-low stop + N=1 trailing limit +
/// 0.70 expansion exit, NO time stop, stop-window 4; production entry filter).
let defaultConfig =
    { StopLowWindow = 4
      TrailWindow = 1
      HiCloseWindow = 252
      AtrWindow = 14
      TightnessWindow = 14
      VolDays = 28
      ExpansionThr = 0.70
      ExitTimeCap = 5
      Notional = 10_000.0
      Entry =
        { UpThreshold = 0.05
          RvolMin = 6.0
          RvolMax = 20.0
          MinPriorDays = 21
          MinAvgDollarVolume = 100_000.0
          Min52wPct = 0.95
          MinPrice = 5.0
          MaxTightness = 0.30
          MaxAtrPct = 0.08 } }

/// A finished trip, ready for the CSV. Mirrors v0's base trip columns so the
/// two outputs diff directly.
type Trip =
    { Symbol: string
      EntryDate: DateOnly
      ExitDate: DateOnly
      EntryPrice: float
      ExitPrice: float
      Qty: float
      NetPnL: float
      BarsHeld: int
      EntryVolume: int64
      RvolAtEntry: float
      AvgDollarVolumeAtEntry: float
      PctUpAtEntry: float
      AtrPctAtEntry: float
      TightnessAtEntry: float
      ExitReason: string
      Open: bool }

/// Convert a closed Position into a Trip. `barsHeld` is the number of trading
/// bars the ticker saw between entry and exit (exit index − entry index),
/// recovered from the per-ticker date list.
let private toTrip (symbol: string) (notional: float)
                   (barIndex: IReadOnlyDictionary<DateOnly,int>) (p: Position) : Trip =
    match p.State with
    | Exited (exitDate, exitPrice, reason) ->
        let qty = notional / p.EntryPrice
        { Symbol = symbol
          EntryDate = p.EntryDate
          ExitDate = exitDate
          EntryPrice = p.EntryPrice
          ExitPrice = exitPrice
          Qty = qty
          NetPnL = qty * (exitPrice - p.EntryPrice)
          BarsHeld = barIndex.[exitDate] - barIndex.[p.EntryDate]
          EntryVolume = p.EntryVolume
          RvolAtEntry = p.RvolAtEntry
          AvgDollarVolumeAtEntry = p.AvgDollarVolumeAtEntry
          PctUpAtEntry = p.PctUpAtEntry
          AtrPctAtEntry = p.AtrPctAtEntry
          TightnessAtEntry = p.TightnessAtEntry
          ExitReason = reason
          Open = (reason = "mtm") }
    | _ -> failwith "toTrip called on a non-Exited position (Finalize first)"

/// Run the whole backtest in a single streaming pass over split_adjusted_prices
/// (ordered by ticker, then date), one QullaSystem per ticker. Returns all
/// trips across all tickers. breadth is applied later (post-hoc); the engine
/// runs with no breadth gate here.
let run (dbPath: string) (cfg: Config) (startDate: DateOnly) (endDate: DateOnly) : Trip[] =
    let connStr = $"Data Source={dbPath};ACCESS_MODE=READ_ONLY"
    use conn = new DuckDBConnection(connStr)
    conn.Open()
    (use pragma = conn.CreateCommand()
     pragma.CommandText <- "PRAGMA memory_limit='6GB'"
     pragma.ExecuteNonQuery() |> ignore)

    use cmd = conn.CreateCommand()
    // Universe = common stock + ADRs only (v0's tradableOnly default:
    // ticker_reference.type IN ('CS','ADRC')). Excludes ETFs, units, warrants,
    // preferreds, etc. — without this v1 trades ~3x more tickers than v0.
    cmd.CommandText <-
        "SELECT p.ticker, p.date, p.adj_open, p.adj_high, p.adj_low, p.adj_close, p.adj_volume
         FROM split_adjusted_prices p
         JOIN ticker_reference r ON r.ticker = p.ticker
         WHERE r.type IN ('CS','ADRC')
           AND p.date >= $start AND p.date <= $end
         ORDER BY p.ticker, p.date"
    let pStart = cmd.CreateParameter() in pStart.ParameterName <- "start"; pStart.Value <- startDate; cmd.Parameters.Add pStart |> ignore
    let pEnd   = cmd.CreateParameter() in pEnd.ParameterName   <- "end";   pEnd.Value   <- endDate;   cmd.Parameters.Add pEnd   |> ignore

    let trips = ResizeArray<Trip>()

    // Per-ticker mutable accumulators, reset at each ticker boundary.
    let mutable curTicker : string = null
    let mutable sys = Unchecked.defaultof<QullaSystem>
    let mutable barIndex = Dictionary<DateOnly,int>()
    let mutable lastBar = Unchecked.defaultof<Bar>
    let mutable barNo = 0

    let newSystem () =
        QullaSystem(cfg.StopLowWindow, cfg.TrailWindow, cfg.HiCloseWindow,
                    cfg.AtrWindow, cfg.TightnessWindow, cfg.VolDays,
                    cfg.ExpansionThr, cfg.ExitTimeCap, cfg.Entry)

    // Flush the just-finished ticker: MTM-close open trips, emit all trips.
    let flush () =
        if not (isNull curTicker) then
            sys.Finalize lastBar
            for p in sys.Positions do
                trips.Add(toTrip curTicker cfg.Notional barIndex p)

    use reader = cmd.ExecuteReader()
    while reader.Read() do
        let ticker = reader.GetString 0
        if ticker <> curTicker then
            flush ()
            curTicker <- ticker
            sys <- newSystem ()
            barIndex <- Dictionary<DateOnly,int>()
            barNo <- 0
        let bar =
            { date   = DateOnly.FromDateTime(reader.GetDateTime 1)
              ``open`` = reader.GetDouble 2
              high   = reader.GetDouble 3
              low    = reader.GetDouble 4
              close  = reader.GetDouble 5
              volume = reader.GetInt64 6 }
        barIndex.[bar.date] <- barNo
        barNo <- barNo + 1
        lastBar <- bar
        sys.Process bar
    flush ()  // last ticker

    trips.ToArray()

// ---------------------------------------------------------------------------
// CSV emission (v0-compatible base columns)
// ---------------------------------------------------------------------------

let private inv = CultureInfo.InvariantCulture
let private fmt (x: float) = if Double.IsNaN x then "nan" else x.ToString("0.################", inv)

let header =
    "symbol,entry_date,exit_date,side,entry_price,exit_price,qty,net_pnl,bars_held,"
    + "entry_adj_volume,rvol_at_entry,avg_dollar_volume_4w_at_entry,pct_up_at_entry,"
    + "atr_pct_14_at_entry,range_pct_14_at_entry,tightness_14_at_entry,exit_reason,open"

let private row (t: Trip) : string =
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
        string t.EntryVolume
        fmt t.RvolAtEntry
        fmt t.AvgDollarVolumeAtEntry
        fmt t.PctUpAtEntry
        fmt t.AtrPctAtEntry
        "nan"                       // range_pct_14: not carried by v1 (post-hoc only)
        fmt t.TightnessAtEntry
        t.ExitReason
        (if t.Open then "1" else "0")
    ]

let writeCsv (path: string) (trips: Trip[]) =
    use w = new IO.StreamWriter(path)
    w.WriteLine header
    for t in trips do w.WriteLine(row t)
