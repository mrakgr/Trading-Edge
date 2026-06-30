module TradingEdge.HighFlyerV2.Backtest

open System
open System.Globalization
open System.Collections.Generic
open DuckDB.NET.Data
open TradingEdge.HighFlyerV2.Types

/// Full system configuration. The locked production default is `defaultConfig`.
type Config =
    { StopLowWindow: int        // prior-window low (CSV snapshot reference only)
      HiCloseWindow: int
      AtrWindow: int
      TightnessWindow: int
      VolDays: int
      MaxHoldBars: int          // time-stop: exit at next open after this many Holding bars (0 = off)
      Notional: float
      Entry: EntryConfig }

/// The locked production default (mirrors the original HighFlyer defaultConfig
/// after debloat): the entry factor stack, NO price stop, a 5d time-stop, MTM.
let defaultConfig =
    { StopLowWindow = 4
      HiCloseWindow = 252
      AtrWindow = 14
      TightnessWindow = 14
      VolDays = 28
      // 5d time-stop: the high-edge breakouts carry ~61% of P&L (PF 1.83) in the
      // first 5 days; the rest is a low-edge grind. NoStop is gone — it was just a
      // time-stop with an infinite cap, so a price stop never exists here.
      MaxHoldBars = 5
      Notional = 10_000.0
      Entry =
        { UpThreshold = 0.10
          MaxUpThreshold = 0.30
          RvolMin = 5.0
          RvolMax = infinity
          MinPriorDays = 21
          MinAvgDollarVolume = 100_000.0
          Min52wPct = 0.95
          Use52wHigh = false
          MinPrice = 1.0
          MaxTightness = 4.5
          MaxAtrPct = 0.10
          MinIntradayRet = -0.07
          MinMaxAtrLog = 0.04 } }

/// A finished trip, ready for the CSV. Mirrors the original HighFlyer base
/// columns so the two outputs diff directly.
type Trip =
    { Symbol: string
      SignalDate: DateOnly
      EntryDate: DateOnly
      EntryReason: string
      ExitDate: DateOnly
      EntryPrice: float
      EntryDayStopRef: float
      StopLowAtEntry: float
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
      Pct52wAtEntry: float
      Pct52wHighAtEntry: float
      Pct52wLowCloseAtEntry: float
      Pct52wLowAtEntry: float
      ExitReason: string
      Open: bool }

/// Convert a closed Position into a Trip. `barsHeld` = exit index − entry index,
/// recovered from the per-ticker date list.
let private toTrip (symbol: string) (notional: float)
                   (barIndex: IReadOnlyDictionary<DateOnly,int>) (p: Position) : Trip =
    match p.State with
    | Exited (exitDate, exitPrice, reason) ->
        let qty = notional / p.EntryPrice
        { Symbol = symbol
          SignalDate = p.SignalDate
          EntryDate = p.EntryDate
          EntryReason = p.EntryReason
          ExitDate = exitDate
          EntryPrice = p.EntryPrice
          EntryDayStopRef = p.EntryDayStopRef
          StopLowAtEntry = p.StopLowAtEntry
          ExitPrice = exitPrice
          Qty = qty
          NetPnL = qty * (exitPrice - p.EntryPrice)   // long only
          BarsHeld = barIndex.[exitDate] - barIndex.[p.EntryDate]
          EntryVolume = p.EntryVolume
          RvolAtEntry = p.RvolAtEntry
          AvgDollarVolumeAtEntry = p.AvgDollarVolumeAtEntry
          PctUpAtEntry = p.PctUpAtEntry
          AtrPctAtEntry = p.AtrPctAtEntry
          TightnessAtEntry = p.TightnessAtEntry
          Pct52wAtEntry = p.Pct52wAtEntry
          Pct52wHighAtEntry = p.Pct52wHighAtEntry
          Pct52wLowCloseAtEntry = p.Pct52wLowCloseAtEntry
          Pct52wLowAtEntry = p.Pct52wLowAtEntry
          ExitReason = reason
          Open = (reason = "mtm") }
    | _ -> failwith "toTrip called on a non-Exited position (Finalize first)"

// ===========================================================================
// Pipeline stage 1 — DbEmitter: the ONLY database read.
//
// Streams the daily universe (split_adjusted_prices, CS/ADRC only) ordered by
// (ticker, date) and pushes each (ticker, Bar) downstream via `onNext`. It owns
// nothing but the read. Continuation-passing style isolates the DB from the rest.
// ===========================================================================
type DbEmitter(conn: DuckDBConnection, startDate: DateOnly, endDate: DateOnly) =

    // Public accessors for the ctor-captured state: `Process` is `inline`.
    member val Conn = conn
    member val StartDate = startDate
    member val EndDate = endDate

    // Universe = common stock + ADRs only. NOTE the SEMI-JOIN (EXISTS), not an
    // inner JOIN: a few tickers (e.g. ASND) have BOTH a 'CS' and an 'ADRC' row in
    // ticker_reference; an inner join would FAN OUT every price row into two
    // identical consecutive rows. EXISTS filters without multiplying.
    static member val Sql =
        "SELECT p.ticker, p.date, p.adj_open, p.adj_high, p.adj_low, p.adj_close, p.adj_volume
         FROM split_adjusted_prices p
         WHERE EXISTS (SELECT 1 FROM ticker_reference r
                       WHERE r.ticker = p.ticker AND r.type IN ('CS','ADRC'))
           AND p.date >= $start AND p.date <= $end
         ORDER BY p.ticker, p.date"

    /// Stream every daily row, pushing (ticker, bar) downstream in (ticker, date)
    /// order. `inline` so the onNext closure fuses into the read loop.
    member inline this.Process(onNext: string * Bar -> unit) =
        use cmd = this.Conn.CreateCommand()
        cmd.CommandText <- DbEmitter.Sql
        let pStart = cmd.CreateParameter() in pStart.ParameterName <- "start"; pStart.Value <- this.StartDate; cmd.Parameters.Add pStart |> ignore
        let pEnd   = cmd.CreateParameter() in pEnd.ParameterName   <- "end";   pEnd.Value   <- this.EndDate;   cmd.Parameters.Add pEnd   |> ignore
        use reader = cmd.ExecuteReader()
        while reader.Read() do
            let ticker = reader.GetString 0
            let bar : Bar =
                { date     = DateOnly.FromDateTime(reader.GetDateTime 1)
                  ``open`` = reader.GetDouble 2
                  high     = reader.GetDouble 3
                  low      = reader.GetDouble 4
                  close    = reader.GetDouble 5
                  volume   = reader.GetInt64 6 }
            onNext (ticker, bar)

/// Run the whole backtest in a single streaming pass over split_adjusted_prices
/// (ordered by ticker, date), one HighFlyer per ticker. Returns all trips.
/// breadth is applied post-hoc; the engine runs with no breadth gate here.
let run (dbPath: string) (cfg: Config) (startDate: DateOnly) (endDate: DateOnly) : Trip[] =
    let connStr = $"Data Source={dbPath};ACCESS_MODE=READ_ONLY"
    use conn = new DuckDBConnection(connStr)
    conn.Open()
    do
        use pragma = conn.CreateCommand()
        pragma.CommandText <- "PRAGMA memory_limit='6GB'"
        pragma.ExecuteNonQuery() |> ignore

    let emitter = DbEmitter(conn, startDate, endDate)
    let trips = ResizeArray<Trip>()

    // Per-ticker mutable accumulators, reset at each ticker boundary.
    let mutable curTicker : string = null
    let mutable sys = Unchecked.defaultof<HighFlyer>
    let mutable barIndex = Dictionary<DateOnly,int>()
    let mutable lastBar = Unchecked.defaultof<Bar>
    let mutable barNo = 0

    let newSystem () =
        HighFlyer(cfg.StopLowWindow, cfg.HiCloseWindow, cfg.AtrWindow,
                  cfg.TightnessWindow, cfg.VolDays, cfg.MaxHoldBars, cfg.Entry)

    // Flush the just-finished ticker: MTM-close open trips, emit all trips.
    let flush () =
        if not (isNull curTicker) then
            sys.Finalize lastBar
            for p in sys.Positions do
                match p.State with
                | Exited _ -> trips.Add(toTrip curTicker cfg.Notional barIndex p)
                | _ -> ()

    emitter.Process(fun (ticker, bar) ->
        if ticker <> curTicker then
            flush ()
            curTicker <- ticker
            sys <- newSystem ()
            barIndex <- Dictionary<DateOnly,int>()
            barNo <- 0
        barIndex.[bar.date] <- barNo
        barNo <- barNo + 1
        lastBar <- bar
        sys.Process bar)
    flush ()  // last ticker

    trips.ToArray()

// ---------------------------------------------------------------------------
// CSV emission (original-HighFlyer-compatible base columns)
// ---------------------------------------------------------------------------

let private inv = CultureInfo.InvariantCulture
let private fmt (x: float) = if Double.IsNaN x then "nan" else x.ToString("0.################", inv)

let header =
    "symbol,signal_date,entry_date,entry_reason,exit_date,side,entry_price,entry_day_stop_ref,stop_low_at_entry,exit_price,qty,net_pnl,bars_held,"
    + "entry_adj_volume,rvol_at_entry,avg_dollar_volume_4w_at_entry,pct_up_at_entry,"
    + "atr_pct_14_at_entry,range_pct_14_at_entry,tightness_14_at_entry,pct_52w_at_entry,pct_52w_high_at_entry,pct_52w_low_close_at_entry,pct_52w_low_at_entry,exit_reason,open"

let private row (t: Trip) : string =
    String.concat "," [
        t.Symbol
        t.SignalDate.ToString("yyyy-MM-dd")
        t.EntryDate.ToString("yyyy-MM-dd")
        t.EntryReason
        t.ExitDate.ToString("yyyy-MM-dd")
        "long"                      // long-only engine
        fmt t.EntryPrice
        fmt t.EntryDayStopRef
        fmt t.StopLowAtEntry
        fmt t.ExitPrice
        fmt t.Qty
        fmt t.NetPnL
        string t.BarsHeld
        string t.EntryVolume
        fmt t.RvolAtEntry
        fmt t.AvgDollarVolumeAtEntry
        fmt t.PctUpAtEntry
        fmt t.AtrPctAtEntry
        "nan"                       // range_pct_14: not carried (post-hoc only)
        fmt t.TightnessAtEntry
        fmt t.Pct52wAtEntry
        fmt t.Pct52wHighAtEntry
        fmt t.Pct52wLowCloseAtEntry
        fmt t.Pct52wLowAtEntry
        t.ExitReason
        (if t.Open then "1" else "0")
    ]

let writeCsv (path: string) (trips: Trip[]) =
    use w = new IO.StreamWriter(path)
    w.WriteLine header
    for t in trips do w.WriteLine(row t)
