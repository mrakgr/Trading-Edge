#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#r "nuget: Argu, 6.2.5"

// Build the `partial_candle_1000` table in trading.db: one row per (ticker, date)
// carrying the day's PARTIAL candle as it looked at the 10:00 ET checkpoint.
// HighFlyerV2's early-entry experiment reads it alongside the full daily bar:
// the entry decision (move band, rvol, the candle-value filters) and the fill
// price switch from the full daily candle to this partial one, to test whether
// entering at 10:00 ET beats entering at the daily close.
//
// The partial candle (as of 10:00 ET, NO peek past it):
//   open  = open of the FIRST RTH bar (et_min >= 570 = 09:30 ET)              [= rth_open]
//   high  = MAX(high) over RTH bars 09:30..09:59  (et_min in [570, 600))
//   low   = MIN(low)  over RTH bars 09:30..09:59
//   close = close of the LAST RTH bar <= 09:59     (arg_max(close, et_min) over [570,600))
//   vol   = SUM(volume) from PREMARKET 04:00 through 09:59 (et_min in [240, 600))
//
// Cutoff = exactly 10:00:00 ET. The 10:00 bar (et_min = 600) covers 10:00:00-
// 10:00:59, i.e. trades AFTER the checkpoint, so it is EXCLUDED from every leg
// (the windows are half-open at 600). That makes the row a true "as of 10:00:00"
// snapshot with no lookahead. Volume is premarket-inclusive (starts 04:00 ET, the
// same start the `premarket` table uses) while OHLC is RTH-only (from the 09:30
// open) -- volume measures total interest, the candle measures the RTH move.
//
// Source = data/minute_aggs/*.parquet (Massive's published 1m product; same
// provenance as build_premarket.fsx -- keeps it consistent with the daily
// aggregates the engine trades off). ET minutes-since-midnight derived here; date
// from the filename. A MaxFlyer/HighFlyer research rollup -> a script, not a
// TradingEdge.Database verb.
//
// Idempotent: DROP+CREATE. Whole minute_aggs corpus in ~well under a minute.
//
// Run:  dotnet fsi scripts/equity/build_partial_candle.fsx
//       dotnet fsi scripts/equity/build_partial_candle.fsx -- --db data/trading.db --minute-dir data/minute_aggs

open System
open Argu
open DuckDB.NET.Data

type CliArgs =
    | [<AltCommandLine("-d")>] Db of string
    | [<AltCommandLine("-m")>] Minute_Dir of string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Db _ -> "DuckDB database path (default: data/trading.db)."
            | Minute_Dir _ -> "Directory of minute_aggs parquet files (default: data/minute_aggs)."

let parser = ArgumentParser.Create<CliArgs>(programName = "build_partial_candle.fsx")
let parsed =
    try parser.Parse(fsi.CommandLineArgs |> Array.skip 1, raiseOnUsage = true)
    with :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        exit 1

let dbPath = parsed.TryGetResult Db |> Option.defaultValue "data/trading.db"
let minuteDir = parsed.TryGetResult Minute_Dir |> Option.defaultValue "data/minute_aggs"

// 10:00 ET checkpoint, minutes-since-midnight.
let rthOpenMin = 570   // 09:30 ET
let cutoffMin  = 600   // 10:00 ET (EXCLUSIVE — the 10:00 bar covers 10:00:00+)
let premktMin  = 240   // 04:00 ET (volume start)

// minute_aggs/*.parquet glob, single-quote-escaped for the SQL literal.
let glob = System.IO.Path.Combine(minuteDir, "*.parquet").Replace("'", "''")

let sql = $"""
DROP TABLE IF EXISTS partial_candle_1000;
CREATE TABLE partial_candle_1000 AS
WITH bars AS (
    SELECT
        ticker,
        CAST(date_part('hour',   to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT) * 60
          + CAST(date_part('minute', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT) AS et_min,
        regexp_extract(filename, '([0-9]{{4}}-[0-9]{{2}}-[0-9]{{2}})\.parquet', 1)::DATE AS date,
        open, high, low, close, volume
    FROM read_parquet('{glob}', filename = true)
    WHERE close > 0
)
SELECT
    ticker,
    date,
    -- OHLC from RTH bars only, 09:30 (inclusive) .. 10:00 (exclusive)
    arg_min(CASE WHEN et_min >= {rthOpenMin} AND et_min < {cutoffMin} THEN open  END,
            CASE WHEN et_min >= {rthOpenMin} AND et_min < {cutoffMin} THEN et_min END)  AS open,
    MAX(CASE WHEN et_min >= {rthOpenMin} AND et_min < {cutoffMin} THEN high END)        AS high,
    MIN(CASE WHEN et_min >= {rthOpenMin} AND et_min < {cutoffMin} THEN low  END)        AS low,
    arg_max(CASE WHEN et_min >= {rthOpenMin} AND et_min < {cutoffMin} THEN close END,
            CASE WHEN et_min >= {rthOpenMin} AND et_min < {cutoffMin} THEN et_min END)  AS close,
    -- volume premarket-inclusive: 04:00 (inclusive) .. 10:00 (exclusive)
    SUM(CASE WHEN et_min >= {premktMin} AND et_min < {cutoffMin} THEN volume ELSE 0 END)::BIGINT AS volume
FROM bars
GROUP BY ticker, date;

CREATE UNIQUE INDEX partial_candle_1000_ticker_date ON partial_candle_1000 (ticker, date);
"""

printfn "Building `partial_candle_1000` table (10:00 ET checkpoint)"
printfn "  db:         %s" (IO.Path.GetFullPath dbPath)
printfn "  minute_aggs:%s" (IO.Path.GetFullPath minuteDir)

let sw = Diagnostics.Stopwatch.StartNew()
let conn = new DuckDBConnection($"DataSource={dbPath}")
conn.Open()

let exec (q: string) =
    let cmd = conn.CreateCommand()
    cmd.CommandText <- q
    cmd.CommandTimeout <- 0
    cmd.ExecuteNonQuery() |> ignore
    cmd.Dispose()

let scalar (q: string) =
    let cmd = conn.CreateCommand()
    cmd.CommandText <- q
    let v = cmd.ExecuteScalar()
    cmd.Dispose()
    v

exec sql
sw.Stop()

let rows    = scalar "SELECT COUNT(*) FROM partial_candle_1000" :?> int64
let tickers = scalar "SELECT COUNT(DISTINCT ticker) FROM partial_candle_1000" :?> int64
let days    = scalar "SELECT COUNT(DISTINCT date) FROM partial_candle_1000" :?> int64
// rows with NO RTH bar before 10:00 (halted/illiquid) have NULL open/close — report them.
let nullOpen = scalar "SELECT COUNT(*) FROM partial_candle_1000 WHERE open IS NULL" :?> int64
printfn "Done in %.1fs: %d rows, %d tickers, %d days (%d with no RTH bar < 10:00)"
    sw.Elapsed.TotalSeconds rows tickers days nullOpen
conn.Dispose()
