#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#r "nuget: Argu, 6.2.5"

// Generate data/gap_up_universe.json: every (ticker, date) where the stock
// gapped up at the open by at least --min-gap-pct (default 5%), restricted to
//   - CS/ADRC tickers
//   - $25M+ 4-week average dollar volume (ADV-25M universe)
//   - dates covered by session_daily_totals (i.e. we have 10s bars for them)
//
// No RVOL filter — using a pre-RVOL-filtered list (e.g. breakouts_rvol3plus_gapup.json)
// would make the backtest circular: thresholds find high-RVOL days, list is
// already high-RVOL days, "success" would prove nothing.
//
// The gap-threshold filter is a material-size filter, not a selection filter
// for volume/volatility. Raising it (e.g. from 0% to 5%) drops the universe
// size dramatically (from 220k to 4.5k pairs at 262 days) and keeps only
// setups a discretionary trader would consider tradable.
//
// Output row shape: {"ticker": "AAPL", "date": "2024-06-12", "gap_pct": 0.0523,
//                    "raw_avg_4w": 12345678.9, "txn_avg_4w": 1234.5,
//                    "split_factor_today": 1.0,
//                    "session_raw_volume": 23456789, "rvol": 1.9}
//
// raw_avg_4w / txn_avg_4w / split_factor_today feed the binary header writer
// for the breakout pipeline. session_raw_volume / rvol carry the actual
// session-volume figures from session_volume_4w so downstream studies don't
// need a second DB round-trip to bucket setups by RVOL.

open System
open System.IO
open System.Text
open System.Globalization
open Argu
open DuckDB.NET.Data

type CliArgs =
    | [<AltCommandLine("-d")>] Database of string
    | [<AltCommandLine("-o")>] Output of string
    | [<AltCommandLine("-g")>] Min_Gap_Pct of float
    | [<AltCommandLine("-s")>] Start_Date of string
    | [<AltCommandLine("-e")>] End_Date of string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Database _ -> "DuckDB path. Default: data/trading.db"
            | Output _ -> "Output JSON path. Default: data/gap_up_universe.json"
            | Min_Gap_Pct _ -> "Minimum gap_pct as a decimal (e.g. 0.05 = 5%). Default: 0.05"
            | Start_Date _ -> "First date (yyyy-MM-dd, inclusive). Default: min date in session_daily_totals."
            | End_Date _ -> "Last date (yyyy-MM-dd, inclusive). Default: max date in session_daily_totals."

let parser = ArgumentParser.Create<CliArgs>(programName = "generate_gap_up_universe.fsx")
let parsed =
    try parser.Parse(fsi.CommandLineArgs |> Array.skip 1, raiseOnUsage = true)
    with :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        exit 1

let db = parsed.GetResult(Database, defaultValue = "data/trading.db")
let outPath = parsed.GetResult(Output, defaultValue = "data/gap_up_universe.json")
let minGapPct = parsed.GetResult(Min_Gap_Pct, defaultValue = 0.05)
let startDateOpt = parsed.TryGetResult Start_Date
let endDateOpt = parsed.TryGetResult End_Date

let conn = new DuckDBConnection(sprintf "Data Source=%s;ACCESS_MODE=READ_ONLY" db)
conn.Open()

let minGapStr = minGapPct.ToString("R", CultureInfo.InvariantCulture)

// Start/end bounds default to the full session_daily_totals range; when the
// caller supplies either one we override the corresponding edge. Interpolated
// as DATE literals directly into SQL — we control the input shape, and
// DuckDB's DATE parser rejects anything malformed.
let startBoundSql =
    match startDateOpt with
    | Some s -> $"DATE '{s}'"
    | None -> "(SELECT MIN(date) FROM session_daily_totals)"
let endBoundSql =
    match endDateOpt with
    | Some e -> $"DATE '{e}'"
    | None -> "(SELECT MAX(date) FROM session_daily_totals)"

// Emits the 4w metadata needed by the binary header writer
// (TradingEdge.Orb.Convert): raw_avg_4w = avg_session_adj_volume_4w /
// split_factor_today, txn_avg_4w = avg_session_transactions_4w, and
// split_factor_today = session_adj_volume / session_raw_volume. When a
// session_volume_4w row is missing (< 16 trading days of history), fields
// default to NULL and Convert writes NaN, so the gate treats the day as
// pass-through.
let sql = $"""
WITH priced AS (
    SELECT
        ticker, date, open,
        LAG(close) OVER (PARTITION BY ticker ORDER BY date) AS prior_close
    FROM daily_prices
)
SELECT
    p.ticker,
    strftime(p.date, '%%Y-%%m-%%d') AS date_str,
    (p.open / p.prior_close - 1.0)::DOUBLE AS gap_pct,
    sv.avg_session_transactions_4w                                   AS txn_avg_4w,
    sv.session_adj_volume::DOUBLE / NULLIF(sv.session_raw_volume, 0)::DOUBLE AS split_factor_today,
    sv.avg_session_adj_volume_4w
      / NULLIF(sv.session_adj_volume::DOUBLE / NULLIF(sv.session_raw_volume, 0)::DOUBLE, 0) AS raw_avg_4w,
    sv.session_raw_volume::DOUBLE                                    AS session_raw_volume,
    sv.session_volume_rvol::DOUBLE                                   AS rvol
FROM priced p
JOIN ticker_reference tr ON tr.ticker = p.ticker AND tr.type IN ('CS', 'ADRC')
JOIN stock_volume_4w sd ON sd.ticker = p.ticker AND sd.date = p.date
LEFT JOIN session_volume_4w sv ON sv.ticker = p.ticker AND sv.date = p.date
WHERE p.prior_close > 0
  AND p.open / p.prior_close - 1.0 >= {minGapStr}
  AND sd.avg_dollar_volume_4w >= 25000000.0
  AND p.date >= {startBoundSql}
  AND p.date <= {endBoundSql}
ORDER BY p.date, p.ticker
"""

printfn "Date range: %s .. %s"
    (startDateOpt |> Option.defaultValue "(min session_daily_totals)")
    (endDateOpt |> Option.defaultValue "(max session_daily_totals)")

printfn "Querying gap-up universe..."
let sw = Diagnostics.Stopwatch.StartNew()

use cmd = conn.CreateCommand()
cmd.CommandText <- sql
use reader = cmd.ExecuteReader()

Directory.CreateDirectory(Path.GetDirectoryName outPath) |> ignore
use fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536)
use w = new StreamWriter(fs)

let inv = CultureInfo.InvariantCulture
w.Write "[\n"
let mutable first = true
let mutable n = 0L
let tickerCounts = System.Collections.Generic.Dictionary<string, int>()
let dateCounts = System.Collections.Generic.Dictionary<string, int>()

let fmtOrNull (v: obj) =
    match v with
    | :? System.DBNull -> "null"
    | :? double as d when Double.IsNaN d || Double.IsInfinity d -> "null"
    | :? double as d -> d.ToString("F6", inv)
    | x -> x.ToString()

while reader.Read() do
    let ticker = reader.GetString 0
    let date = reader.GetString 1
    let gapPct = reader.GetDouble 2
    let txnAvg4w = fmtOrNull (reader.GetValue 3)
    let splitFactor = fmtOrNull (reader.GetValue 4)
    let rawAvg4w = fmtOrNull (reader.GetValue 5)
    let sessionRawVolume = fmtOrNull (reader.GetValue 6)
    let rvol = fmtOrNull (reader.GetValue 7)
    if not first then w.Write ",\n" else first <- false
    w.Write(String.Format(inv,
        "  {{\"ticker\": \"{0}\", \"date\": \"{1}\", \"gap_pct\": {2:F6}, \"raw_avg_4w\": {3}, \"txn_avg_4w\": {4}, \"split_factor_today\": {5}, \"session_raw_volume\": {6}, \"rvol\": {7}}}",
        ticker, date, gapPct, rawAvg4w, txnAvg4w, splitFactor, sessionRawVolume, rvol))
    n <- n + 1L
    tickerCounts.[ticker] <- (if tickerCounts.ContainsKey ticker then tickerCounts.[ticker] + 1 else 1)
    dateCounts.[date] <- (if dateCounts.ContainsKey date then dateCounts.[date] + 1 else 1)

w.Write "\n]\n"
w.Flush()
sw.Stop()

printfn ""
printfn "Wrote %s" outPath
printfn "  rows:         %d" n
printfn "  distinct tickers: %d" tickerCounts.Count
printfn "  distinct dates:   %d" dateCounts.Count
printfn "  elapsed:      %.1fs" sw.Elapsed.TotalSeconds
printfn ""

if n > 0L then
    let topTickers =
        tickerCounts
        |> Seq.sortByDescending (fun kv -> kv.Value)
        |> Seq.truncate 5
    printfn "top tickers by count:"
    for kv in topTickers do
        printfn "  %-8s  %d" kv.Key kv.Value
    let dateVals = dateCounts.Values |> Seq.toArray
    Array.sortInPlace dateVals
    let median = dateVals.[dateVals.Length / 2]
    printfn ""
    printfn "gap-ups per day: min=%d  median=%d  max=%d"
        (Array.min dateVals) median (Array.max dateVals)
