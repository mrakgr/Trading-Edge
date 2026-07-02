#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#r "nuget: Argu, 6.2.5"

// Build the `mr_candidate` table in trading.db: one row per (ticker, date) that
// clears the LowFlyer market-wide mean-reversion PRECONDITIONS, carrying the
// daily context the intraday engine and the post-hoc feature slicing need.
//
// LowFlyer scans EVERY stock, EVERY day, for a high-volume 1m breakout to a new
// session low (a long mean-reversion entry). The daily selection engine is gone;
// the only day-level preconditions are pure SQL and live here:
//   (A) LIQUIDITY  — median of the 09:30-09:45 ET 1m-bar volumes >= 10,000 AND
//                    >= 10 of the (max 15) bars present. Computed over the whole
//                    minute_aggs corpus. This is the hard prune: only qualifying
//                    (ticker,day)s have their minute bars streamed to the engine.
//   (B) UNIVERSE/PRICE/CONTEXT — CS/ADRC, D's adj_close >= $1, warmed up (>21
//                    bars in the current episode), plus the recorded features and
//                    forward returns, from the shared `daily_episodes` view
//                    (gap-severed at >45d; see build_daily_episodes_view.sql).
//
// Columns:
//   day_open           first 09:30 RTH bar's open (== the engine's session open)
//   med_bar_vol_0945   median 1m-bar volume 09:30-09:45 (the liquidity metric)
//   nbar_0945          # of 1m bars present in 09:30-09:45 (>=10 required)
//   vol_0945           total volume 09:30-09:45
//   prev_adj_close     D-1 adj close (= close_1d), episode-partitioned
//   close_3d           D-3 adj close, episode-partitioned
//   day_close          D's adj close (the price-floor field + fwd-return base)
//   adj_ratio          adj_close/raw_close (rescale intraday bars to adjusted)
//   avgvol20           20-bar trailing mean daily volume (rvol denominator)
//   close_fwd_1d/3d/5d D+1/D+3/D+5 adj close (forward returns; REPORTED, no lookahead)
//
// The final INNER JOIN (liq x ctx) is the prune: only median>=10k days survive.
// No-lookahead: close_fwd_* are strictly future daily closes, only ever reported.
//
// Run:  dotnet fsi scripts/equity/build_mr_candidate.fsx
//       dotnet fsi scripts/equity/build_mr_candidate.fsx -- --db data/trading.db -m data/minute_aggs

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

let parser = ArgumentParser.Create<CliArgs>(programName = "build_mr_candidate.fsx")
let parsed =
    try parser.Parse(fsi.CommandLineArgs |> Array.skip 1, raiseOnUsage = true)
    with :? ArguParseException as ex -> eprintfn "%s" ex.Message; exit 1

let dbPath    = parsed.TryGetResult Db |> Option.defaultValue "data/trading.db"
let minuteDir = parsed.TryGetResult Minute_Dir |> Option.defaultValue "data/minute_aggs"
let glob = System.IO.Path.Combine(minuteDir, "*.parquet").Replace("'", "''")

// ET-minute anchors (minutes-since-ET-midnight): 09:30 = 570, 09:45 = 585.
// The liquidity window is the RTH open range [570, 585) — the 15 one-minute bars
// 09:30..09:44. It is fully known by 09:45 (no lookahead into the scan window).
let premktMin = 240   // 04:00 ET — premarket start (for the premarket-inclusive rvol_0945 volume)
let rthOpen = 570
let scanMin = 585

let sql = $"""
DROP TABLE IF EXISTS mr_candidate;
CREATE TABLE mr_candidate AS
WITH
-- (A) liquidity + session open, over the 09:30-09:45 RTH window, whole corpus.
bars AS (
    SELECT ticker,
        CAST(date_part('hour',   to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT) * 60
          + CAST(date_part('minute', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT) AS et_min,
        regexp_extract(filename, '([0-9]{{4}}-[0-9]{{2}}-[0-9]{{2}})\.parquet', 1)::DATE AS date,
        open, volume
    FROM read_parquet('{glob}', filename = true)
    WHERE close > 0
),
liq AS (
    SELECT ticker, date,
        median(CASE WHEN et_min >= {rthOpen} AND et_min < {scanMin} THEN volume END) AS med_bar_vol_0945,
        count (CASE WHEN et_min >= {rthOpen} AND et_min < {scanMin} THEN volume END) AS nbar_0945,
        arg_min(CASE WHEN et_min >= {rthOpen} AND et_min < {scanMin} THEN open   END,
                CASE WHEN et_min >= {rthOpen} AND et_min < {scanMin} THEN et_min END) AS day_open,
        sum   (CASE WHEN et_min >= {rthOpen} AND et_min < {scanMin} THEN volume ELSE 0 END) AS vol_0945,
        -- premarket-INCLUSIVE volume 04:00->09:45 (== partial_candle_0945.volume) — the
        -- rvol_0945 numerator used in all the feature analysis.
        sum   (CASE WHEN et_min >= {premktMin} AND et_min < {scanMin} THEN volume ELSE 0 END) AS vol_0945_pm
    FROM bars
    GROUP BY ticker, date
    HAVING median(CASE WHEN et_min >= {rthOpen} AND et_min < {scanMin} THEN volume END) >= 10000
       AND count (CASE WHEN et_min >= {rthOpen} AND et_min < {scanMin} THEN volume END) >= 10
),
-- (B) episode-partitioned daily context from the shared view (gap-severed).
ctx AS (
    SELECT ticker, date,
        adj_close AS day_close,
        CASE WHEN raw_close > 0 THEN adj_close / raw_close END        AS adj_ratio,
        LAG(adj_close, 1) OVER e                                      AS prev_adj_close,   -- close_1d
        LAG(adj_close, 3) OVER e                                      AS close_3d,
        AVG(adj_volume) OVER (PARTITION BY ticker, episode ORDER BY date
                              ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) AS avgvol20,
        LEAD(adj_close, 1) OVER e                                     AS close_fwd_1d,
        LEAD(adj_close, 3) OVER e                                     AS close_fwd_3d,
        LEAD(adj_close, 5) OVER e                                     AS close_fwd_5d,
        COUNT(*) OVER (PARTITION BY ticker, episode)                  AS nbars
    FROM daily_episodes
    WINDOW e AS (PARTITION BY ticker, episode ORDER BY date)
)
SELECT c.ticker, c.date,
    c.prev_adj_close, c.close_3d, c.day_close, c.adj_ratio, c.avgvol20,
    c.close_fwd_1d, c.close_fwd_3d, c.close_fwd_5d,
    l.day_open, l.med_bar_vol_0945, l.nbar_0945, l.vol_0945,
    -- rvol_0945 = premarket-inclusive vol through 09:45 / 20-bar avg daily vol. First-class
    -- (was a post-join off partial_candle_0945). The <0.1 tail (barely traded by 09:45) is
    -- dead in every slice (Run 10), so prune it here to save the intraday engine the work.
    l.vol_0945_pm::DOUBLE / NULLIF(c.avgvol20, 0) AS rvol_0945
FROM ctx c
JOIN liq l ON l.ticker = c.ticker AND l.date = c.date   -- INNER JOIN = the liquidity prune
WHERE c.day_close >= 1.0 AND c.nbars > 21               -- price>=$1 (D close), warmed up
  AND l.vol_0945_pm::DOUBLE / NULLIF(c.avgvol20, 0) >= 0.1;  -- drop the dead <0.1 rvol_0945 tail

CREATE UNIQUE INDEX mr_candidate_ticker_date ON mr_candidate (ticker, date);
"""

printfn "Building `mr_candidate` (median 1m-bar vol 09:30-09:45 >= 10k AND >=10 bars; CS/ADRC; price>=$1; rvol_0945 >= 0.1)"
printfn "  db:          %s" (IO.Path.GetFullPath dbPath)
printfn "  minute_aggs: %s" (IO.Path.GetFullPath minuteDir)

let sw = Diagnostics.Stopwatch.StartNew()
let conn = new DuckDBConnection($"DataSource={dbPath}")
conn.Open()

let exec (q: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- q
    cmd.CommandTimeout <- 0
    cmd.ExecuteNonQuery() |> ignore

let scalar (q: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- q
    cmd.ExecuteScalar()

exec "PRAGMA memory_limit='6GB'"
exec sql
sw.Stop()

let rows    = scalar "SELECT COUNT(*) FROM mr_candidate" :?> int64
let tickers = scalar "SELECT COUNT(DISTINCT ticker) FROM mr_candidate" :?> int64
let days    = scalar "SELECT COUNT(DISTINCT date) FROM mr_candidate" :?> int64
printfn "Done in %.1fs: %d candidate rows, %d tickers, %d days" sw.Elapsed.TotalSeconds rows tickers days
conn.Dispose()
