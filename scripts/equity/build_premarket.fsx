#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#r "nuget: Argu, 6.2.5"

// Build the `premarket` table in trading.db: one row per (ticker, date) carrying
// the day's premarket volume and the RTH opening price. MaxFlyer's Gate 2 reads it
// as a plain in-DB JOIN against the daily selection stream.
//
//   premkt_vol = SUM(volume) over premarket bars 04:00-09:29 ET (et_min in [240,570))
//   rth_open   = open of the FIRST bar at/after 09:30 ET (et_min >= 570)
//
// Source is data/minute_aggs/*.parquet -- Massive's published 1m product (their
// {15,16,38,22}+odd-lot rules), NOT a locally rebuilt lit-only product. That keeps
// premkt_vol/gap% consistent with the daily aggregates MaxFlyer trades off (see
// docs/massive_aggregates_provenance.md). minute_aggs has window_start (epoch-ns
// UTC) and no bucket column, so we derive ET minutes-since-midnight here (the same
// conversion as scripts/equity/intraday_checkpoints.py). Date comes from the
// filename to avoid a second per-row timezone conversion.
//
// This is a MaxFlyer research rollup, deliberately a script (not a TradingEdge.Database
// CLI verb) -- the warehouse project owns only canonical Massive data.
//
// Idempotent: DROP+CREATE. Whole minute_aggs corpus in ~well under a minute.
//
// Run:  dotnet fsi scripts/equity/build_premarket.fsx
//       dotnet fsi scripts/equity/build_premarket.fsx -- --db data/trading.db --minute-dir data/minute_aggs

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

let parser = ArgumentParser.Create<CliArgs>(programName = "build_premarket.fsx")
let parsed =
    try parser.Parse(fsi.CommandLineArgs |> Array.skip 1, raiseOnUsage = true)
    with :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        exit 1

let dbPath = parsed.TryGetResult Db |> Option.defaultValue "data/trading.db"
let minuteDir = parsed.TryGetResult Minute_Dir |> Option.defaultValue "data/minute_aggs"

// minute_aggs/*.parquet glob, single-quote-escaped for the SQL literal.
let glob = System.IO.Path.Combine(minuteDir, "*.parquet").Replace("'", "''")

let sql = $"""
DROP TABLE IF EXISTS premarket;
CREATE TABLE premarket AS
WITH bars AS (
    SELECT
        ticker,
        CAST(date_part('hour',   to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT) * 60
          + CAST(date_part('minute', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT) AS et_min,
        regexp_extract(filename, '([0-9]{{4}}-[0-9]{{2}}-[0-9]{{2}})\.parquet', 1)::DATE AS date,
        open, volume
    FROM read_parquet('{glob}', filename = true)
    WHERE close > 0
)
SELECT
    ticker,
    date,
    SUM(CASE WHEN et_min >= 240 AND et_min < 570 THEN volume ELSE 0 END)::BIGINT AS premkt_vol,
    arg_min(CASE WHEN et_min >= 570 THEN open   END,
            CASE WHEN et_min >= 570 THEN et_min END)                             AS rth_open
FROM bars
GROUP BY ticker, date;

CREATE UNIQUE INDEX premarket_ticker_date ON premarket (ticker, date);
"""

printfn "Building `premarket` table"
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

let rows    = scalar "SELECT COUNT(*) FROM premarket" :?> int64
let tickers = scalar "SELECT COUNT(DISTINCT ticker) FROM premarket" :?> int64
let days    = scalar "SELECT COUNT(DISTINCT date) FROM premarket" :?> int64
printfn "Done in %.1fs: %d rows, %d tickers, %d days" sw.Elapsed.TotalSeconds rows tickers days
conn.Dispose()
