#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#r "nuget: Argu, 6.2.5"

// Build a `partial_candle_HHMM` table in trading.db: one row per (ticker, date)
// carrying the day's PARTIAL candle as it looked at the --cutoff-min ET checkpoint
// (default 600 = 10:00 ET; 630 = 10:30, 660 = 11:00 -> partial_candle_1000/1030/1100).
// HighFlyerV2's early-entry experiment reads it alongside the full daily bar: the
// entry decision (move band, rvol, the candle-value filters) and the fill price
// switch from the full daily candle to this partial one, to test whether entering
// at the checkpoint beats entering at the daily close.
//
// The partial candle (as of HH:MM ET, NO peek past it), for cutoff C (ET minutes):
//   open  = open of the FIRST RTH bar (et_min >= 570 = 09:30 ET)              [= rth_open]
//   high  = MAX(high) over RTH bars 09:30..C-1  (et_min in [570, C))
//   low   = MIN(low)  over RTH bars 09:30..C-1
//   close = close of the LAST RTH bar < C        (arg_max(close, et_min) over [570,C))
//   vol   = SUM(volume) from PREMARKET 04:00 through C-1 (et_min in [240, C))
//
// The cutoff bar (et_min = C) covers HH:MM:00-HH:MM:59, i.e. trades AFTER the
// checkpoint, so it is EXCLUDED from every leg (windows half-open at C). That makes
// the row a true "as of HH:MM:00" snapshot with no lookahead. Volume is premarket-
// inclusive (starts 04:00 ET, same as the `premarket` table) while OHLC is RTH-only
// (from the 09:30 open) -- volume measures total interest, the candle the RTH move.
//
// Source = data/minute_aggs/*.parquet (Massive's published 1m product; same
// provenance as build_premarket.fsx -- keeps it consistent with the daily
// aggregates the engine trades off). ET minutes-since-midnight derived here; date
// from the filename. A MaxFlyer/HighFlyer research rollup -> a script, not a
// TradingEdge.Database verb.
//
// Idempotent: DROP+CREATE. Whole minute_aggs corpus in ~well under a minute.
//
// Run:  dotnet fsi scripts/equity/build_partial_candle.fsx                       (10:00)
//       dotnet fsi scripts/equity/build_partial_candle.fsx -- --cutoff-min 630   (10:30)

open System
open Argu
open DuckDB.NET.Data

type CliArgs =
    | [<AltCommandLine("-d")>] Db of string
    | [<AltCommandLine("-m")>] Minute_Dir of string
    | [<AltCommandLine("-c")>] Cutoff_Min of int

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Db _ -> "DuckDB database path (default: data/trading.db)."
            | Minute_Dir _ -> "Directory of minute_aggs parquet files (default: data/minute_aggs)."
            | Cutoff_Min _ -> "Checkpoint cutoff in ET minutes-since-midnight, EXCLUSIVE (default 600 = 10:00 ET; 630 = 10:30, 660 = 11:00). The table is named partial_candle_HHMM from it."

let parser = ArgumentParser.Create<CliArgs>(programName = "build_partial_candle.fsx")
let parsed =
    try parser.Parse(fsi.CommandLineArgs |> Array.skip 1, raiseOnUsage = true)
    with :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        exit 1

let dbPath = parsed.TryGetResult Db |> Option.defaultValue "data/trading.db"
let minuteDir = parsed.TryGetResult Minute_Dir |> Option.defaultValue "data/minute_aggs"

// Checkpoint minutes-since-midnight. cutoffMin is EXCLUSIVE (the cutoff bar covers
// HH:MM:00+, so excluding it makes the row a true "as of HH:MM:00" snapshot).
let rthOpenMin = 570   // 09:30 ET
let cutoffMin  = parsed.TryGetResult Cutoff_Min |> Option.defaultValue 600  // 10:00 ET default
let premktMin  = 240   // 04:00 ET (volume start)
// table name = partial_candle_HHMM derived from the cutoff (600 -> 1000, 630 -> 1030).
let tableName  = sprintf "partial_candle_%02d%02d" (cutoffMin / 60) (cutoffMin % 60)

// minute_aggs/*.parquet glob, single-quote-escaped for the SQL literal.
let glob = System.IO.Path.Combine(minuteDir, "*.parquet").Replace("'", "''")

let sql = $"""
DROP TABLE IF EXISTS {tableName};
CREATE TABLE {tableName} AS
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

CREATE UNIQUE INDEX {tableName}_ticker_date ON {tableName} (ticker, date);
"""

printfn "Building `%s` table (%02d:%02d ET checkpoint)" tableName (cutoffMin / 60) (cutoffMin % 60)
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

let rows    = scalar $"SELECT COUNT(*) FROM {tableName}" :?> int64
let tickers = scalar $"SELECT COUNT(DISTINCT ticker) FROM {tableName}" :?> int64
let days    = scalar $"SELECT COUNT(DISTINCT date) FROM {tableName}" :?> int64
// rows with NO RTH bar before the cutoff (halted/illiquid) have NULL open/close — report them.
let nullOpen = scalar $"SELECT COUNT(*) FROM {tableName} WHERE open IS NULL" :?> int64
printfn "Done in %.1fs: %d rows, %d tickers, %d days (%d with no RTH bar < %02d:%02d)"
    sw.Elapsed.TotalSeconds rows tickers days nullOpen (cutoffMin / 60) (cutoffMin % 60)
conn.Dispose()
