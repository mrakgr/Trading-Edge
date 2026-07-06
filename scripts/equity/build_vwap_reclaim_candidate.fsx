#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#r "nuget: Argu, 6.2.5"

// Build `vwap_reclaim_candidate` in trading.db: the VwapReclaim "in-play" universe.
//
// It is a strict SUBSET of `mr_candidate` (the shared LowFlyer/MaxFlyerV2 base table) with the
// two VwapReclaim Layer-1 prunes folded in, so the intraday engine streams FAR fewer ticker-days
// (the ADV/rvol filters were previously applied post-hoc on the trips CSV — now they prune the
// universe up front, shrinking the run and the output ~5-10x):
//
//   ADV = avgvol20 * day_close >= $1,000,000   (20-day average DOLLAR volume — a real liquidity
//                                                floor: the name is tradeable and can reach target)
//   rvol_0945 > 1                              (trading at MORE than its normal volume into the
//                                                open — genuinely "in play", not the loose >=0.1
//                                                base floor)
//
// Reuses mr_candidate wholesale (identical columns + all its liquidity/episode/CS-price logic) —
// NO duplicated build logic, stays in sync automatically. mr_candidate itself is UNTOUCHED, so
// LowFlyer/MaxFlyerV2 are unaffected. Rebuild this whenever mr_candidate is rebuilt.
//
// Run:  dotnet fsi scripts/equity/build_vwap_reclaim_candidate.fsx
//       dotnet fsi scripts/equity/build_vwap_reclaim_candidate.fsx -- --db data/trading.db

open System
open Argu
open DuckDB.NET.Data

type CliArgs =
    | [<AltCommandLine("-d")>] Db of string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Db _ -> "DuckDB database path (default: data/trading.db)."

let parser = ArgumentParser.Create<CliArgs>(programName = "build_vwap_reclaim_candidate.fsx")
let parsed =
    try parser.Parse(fsi.CommandLineArgs |> Array.skip 1, raiseOnUsage = true)
    with :? ArguParseException as ex -> eprintfn "%s" ex.Message; exit 1

let dbPath = parsed.TryGetResult Db |> Option.defaultValue "data/trading.db"

let sql = """
DROP TABLE IF EXISTS vwap_reclaim_candidate;
CREATE TABLE vwap_reclaim_candidate AS
SELECT *
FROM mr_candidate
WHERE avgvol20 * day_close >= 1000000.0   -- ADV >= $1M (20-day avg dollar volume)
  AND rvol_0945 > 1.0;                     -- genuinely in play into the open

CREATE UNIQUE INDEX vwap_reclaim_candidate_ticker_date ON vwap_reclaim_candidate (ticker, date);
"""

use conn = new DuckDBConnection($"Data Source={dbPath}")
conn.Open()
use cmd = conn.CreateCommand()
cmd.CommandText <- sql
cmd.ExecuteNonQuery() |> ignore

// report the prune ratio
use rc = conn.CreateCommand()
rc.CommandText <- "SELECT (SELECT COUNT(*) FROM mr_candidate) AS base, (SELECT COUNT(*) FROM vwap_reclaim_candidate) AS kept"
use rdr = rc.ExecuteReader()
rdr.Read() |> ignore
let baseN = rdr.GetInt64 0
let keptN = rdr.GetInt64 1
printfn "Built `vwap_reclaim_candidate`: %d / %d mr_candidate rows kept (%.1f%%) — ADV>=$1M & rvol_0945>1"
    keptN baseN (100.0 * float keptN / float baseN)
