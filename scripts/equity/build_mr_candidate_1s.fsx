#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#r "nuget: Argu, 6.2.5"

// Build the `mr_candidate_1s` table in trading.db: the 1s-TAPE-NATIVE successor to
// `mr_candidate` (see build_mr_candidate.fsx). One row per (ticker, date) clearing the
// mean-reversion preconditions, with liquidity measured on OUR 1s bars — the same feed
// the live scanner will actually build — instead of Polygon minute_aggs.
//
// WHY (S38p/S39): the old precondition (A) — median 09:30-09:45 1m-bar volume >= 10k AND
// >= 10/15 bars — was the last 1m-bar dependency in the FlushFader path, and it blind-spots
// the thinnest tapes (60% of dv-qualified tkds fail it). It is replaced by two tape floors:
//
//   (A') LIQUIDITY  — dv_0945_tape >= $2M   (Σ vwap·volume, 09:30-09:45, raw dollars)
//                     AND n_eff_shannon >= 25 (see below).
//   (B)  UNIVERSE/CONTEXT — CS/ADRC via `daily_episodes`, episode warmed up:
//                     D has >= 21 PRIOR bars (ROW_NUMBER, prior-only — the old table's
//                     COUNT(*)-over-episode counted FUTURE days: membership conditioned
//                     on the episode's eventual length, a lookahead). ⚠ NO PRICE FLOOR
//                     (S39d): the old `day_close >= $1` gate was D's OWN close — unknowable
//                     at 09:45 — and ADJUSTED, i.e. future-split-dependent (S35 disease:
//                     penny names with future reverse splits admitted, crash-through-$1
//                     days excluded = survivorship flattering a long-MR book). The $1
//                     price cut stays POST-HOC on raw entry_px, where it has always been.
//
// `n_eff_shannon` = exp(H) of the volume distribution over the window's 1s bars
// (Shannon perplexity, Rényi order 1): the effective number of equally-weighted seconds
// carrying the window's volume, in [1, 900]. The principled replacement for the median/
// bar-count pair: a tape where 25+ effective seconds carry the volume is genuinely
// tradable; a $2M window owned by a handful of prints (quad-witching megacap auction
// bursts, one-block pump tapes) is not. Computed with the same monoid identities as
// RollingMa's NEffShannon/NEffHhi accumulators (no shares, no window functions):
//     H = ln(Σv) − (Σ v·ln v)/Σv          n_eff_hhi = (Σv)² / Σv²      (v > 0 bars)
// Calibration (S39): old-pass days had n_eff_shannon q10 ≈ 25, so the floor matches the
// old gate's tail on the distribution axis while opening the dollar axis. It keeps 89%
// of the old-FAIL × dv>=$3M × sub-$10 blind spot and drops only 14/4,138 v1.6 book tkds
// (70 trips, +30.6 pts net, incl. a −38% COVID bomb). Universe ≈ 115-190k tkd/yr
// (~2.5-3× the old streaming load).
//
// The rvol_0945 >= 0.1 prune of the old table is DROPPED: the $2M absolute dollar floor
// subsumes "barely traded by 09:45", and rvol stays a recorded column (honest twin only;
// gate with the engine's --min-rvol-0945 if ever needed).
//
// ⚠ KNOWABILITY: every (A') field is fully determined at 09:45 — legal ONLY for engines
// with EntryStartMin >= 09:45 (FlushFader enters 09:45+). Same alignment trap as before.
// ⚠ SPAN: the 1s slim corpus starts 2020 — this table spans 2020+, not 2003+ like
// `mr_candidate`. Point the engine at it with FF_CANDIDATE_TABLE=mr_candidate_1s.
//
// Columns (engine reads: ticker, date, prev_adj_close, close_3d, day_close, adj_ratio,
// close_fwd_1d/3d/5d, dv_0945, rvol_0945_honest — all present):
//   n_bars_1s          # of PRESENT 1s bars 09:30-09:45 (max 900; absent seconds = no trades)
//   tc_0945_tape       Σ trade_count 09:30-09:45
//   vol_0945_tape      Σ volume 09:30-09:45
//   dv_0945_tape       Σ vwap·volume 09:30-09:45 — RAW dollars, matches the engine's
//                      in-stream dv_0945_tape exactly (0 mismatches on the v1.6 book)
//   dv_0945            = dv_0945_tape. ⚠ NOT the old `vol·avgprice·adj_ratio` (that was
//                      future-split-contaminated — S35). Kept so the engine's candidate
//                      SELECT resolves; its --min-dv-0945 gate defaults 0 (off).
//   n_eff_shannon      exp(Shannon H) of the 1s volume distribution (the (A') floor)
//   n_eff_hhi          (Σv)²/Σv² — the order-2 twin (RECORDED; heavy-print alarm)
//   open_0930_tape     first present 1s bar's vwap at/after 09:30 (session-open analog)
//   vol_0945_pm_tape   premarket-inclusive Σ volume 04:00-09:45 (rvol numerator)
//   rvol_0945          vol_0945_pm_tape / avgvol20 — ⚠ CONTAMINATED DENOMINATOR, report-only
//   rvol_0945_honest   vol_0945_pm_tape / avgvol20_prior — LIVE-SAFE. Gate on this one.
//   (+ the same daily-context columns as mr_candidate: prev_adj_close, close_3d, close_7d,
//    day_close, adj_ratio, avgvol20, avgvol20_prior, close_fwd_1d/3d/5d)
//
// Run:  dotnet fsi scripts/equity/build_mr_candidate_1s.fsx
//       dotnet fsi scripts/equity/build_mr_candidate_1s.fsx -- --min-neff 25 --min-dv 2000000

open System
open Argu
open DuckDB.NET.Data

type CliArgs =
    | [<AltCommandLine("-d")>] Db of string
    | [<AltCommandLine("-s")>] Slim_Dir of string
    | Min_Dv of float
    | Min_Neff of float

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Db _ -> "DuckDB database path (default: data/trading.db)."
            | Slim_Dir _ -> "Directory of 1s slim parquet files (default: data/intraday_1s_slim)."
            | Min_Dv _ -> "dv_0945_tape floor in raw dollars (default: 2e6)."
            | Min_Neff _ -> "n_eff_shannon floor (default: 25)."

let parser = ArgumentParser.Create<CliArgs>(programName = "build_mr_candidate_1s.fsx")
let parsed =
    try parser.Parse(fsi.CommandLineArgs |> Array.skip 1, raiseOnUsage = true)
    with :? ArguParseException as ex -> eprintfn "%s" ex.Message; exit 1

let dbPath  = parsed.TryGetResult Db |> Option.defaultValue "data/trading.db"
let slimDir = parsed.TryGetResult Slim_Dir |> Option.defaultValue "data/intraday_1s_slim"
let minDv   = parsed.TryGetResult Min_Dv |> Option.defaultValue 2e6
let minNeff = parsed.TryGetResult Min_Neff |> Option.defaultValue 25.0
let glob = IO.Path.Combine(slimDir, "*.parquet").Replace("'", "''")

// Seconds-since-ET-midnight anchors: premarket 04:00 = 14400, RTH open 09:30 = 34200,
// scan boundary 09:45 = 35100. The liquidity window is [34200, 35100) — fully known by
// 09:45, no lookahead into the entry window (EntryStartMin >= 09:45).
let premktSec = 14400
let rthOpenSec = 34200
let scanSec = 35100

let sql = $"""
DROP TABLE IF EXISTS mr_candidate_1s;
CREATE TABLE mr_candidate_1s AS
WITH
-- (A') tape liquidity over OUR 1s bars. One aggregation pass: the Shannon/HHI effective
-- counts use the monoid identities (H = ln Σv − Σ v·ln v / Σv), so no per-row volume
-- shares and no window functions are needed. rth = the 09:30-09:45 RTH open window;
-- the premarket-inclusive volume rides along from the same scan.
liq AS (
    SELECT ticker,
        regexp_extract(filename, '([0-9]{{4}}-[0-9]{{2}}-[0-9]{{2}})\.parquet', 1)::DATE AS date,
        count(*)               FILTER (bucket >= {rthOpenSec})                  AS n_bars_1s,
        sum(trade_count)       FILTER (bucket >= {rthOpenSec})                  AS tc_0945_tape,
        sum(volume::DOUBLE)    FILTER (bucket >= {rthOpenSec})                  AS vol_0945_tape,
        sum(vwap::DOUBLE * volume::DOUBLE) FILTER (bucket >= {rthOpenSec})      AS dv_0945_tape,
        arg_min(vwap::DOUBLE, bucket) FILTER (bucket >= {rthOpenSec})           AS open_0930_tape,
        sum(volume::DOUBLE)                                                     AS vol_0945_pm_tape,
        -- effective counts over v > 0 RTH bars (volume can be 0 on odd-lot-only seconds;
        -- ln(0) guard). Σv over v>0 == vol_0945_tape (zero bars add nothing to the sum).
        CASE WHEN sum(volume::DOUBLE) FILTER (bucket >= {rthOpenSec} AND volume > 0) > 0 THEN
            exp( ln(sum(volume::DOUBLE) FILTER (bucket >= {rthOpenSec} AND volume > 0))
               - sum(volume::DOUBLE * ln(volume::DOUBLE)) FILTER (bucket >= {rthOpenSec} AND volume > 0)
                 / sum(volume::DOUBLE) FILTER (bucket >= {rthOpenSec} AND volume > 0) )
        END AS n_eff_shannon,
        CASE WHEN sum(volume::DOUBLE) FILTER (bucket >= {rthOpenSec} AND volume > 0) > 0 THEN
            pow(sum(volume::DOUBLE) FILTER (bucket >= {rthOpenSec} AND volume > 0), 2)
              / sum(volume::DOUBLE * volume::DOUBLE) FILTER (bucket >= {rthOpenSec} AND volume > 0)
        END AS n_eff_hhi
    FROM read_parquet('{glob}', filename = true)
    WHERE bucket >= {premktSec} AND bucket < {scanSec}
    GROUP BY 1, 2
    HAVING sum(vwap::DOUBLE * volume::DOUBLE) FILTER (bucket >= {rthOpenSec}) >= {minDv}
       AND CASE WHEN sum(volume::DOUBLE) FILTER (bucket >= {rthOpenSec} AND volume > 0) > 0 THEN
               exp( ln(sum(volume::DOUBLE) FILTER (bucket >= {rthOpenSec} AND volume > 0))
                  - sum(volume::DOUBLE * ln(volume::DOUBLE)) FILTER (bucket >= {rthOpenSec} AND volume > 0)
                    / sum(volume::DOUBLE) FILTER (bucket >= {rthOpenSec} AND volume > 0) )
           END >= {minNeff}
),
-- (B) episode-partitioned daily context — IDENTICAL to build_mr_candidate.fsx.
ctx AS (
    SELECT ticker, date,
        adj_close AS day_close,
        CASE WHEN raw_close > 0 THEN adj_close / raw_close END        AS adj_ratio,
        LAG(adj_close, 1) OVER e                                      AS prev_adj_close,
        LAG(adj_close, 3) OVER e                                      AS close_3d,
        LAG(adj_close, 7) OVER e                                      AS close_7d,
        AVG(adj_volume) OVER (PARTITION BY ticker, episode ORDER BY date
                              ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) AS avgvol20,
        -- LIVE-SAFE twin: the 20 bars ENDING AT D-1 (gate on this one, never avgvol20 — F14).
        AVG(adj_volume) OVER (PARTITION BY ticker, episode ORDER BY date
                              ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS avgvol20_prior,
        LEAD(adj_close, 1) OVER e                                     AS close_fwd_1d,
        LEAD(adj_close, 3) OVER e                                     AS close_fwd_3d,
        LEAD(adj_close, 5) OVER e                                     AS close_fwd_5d,
        -- D's position in its episode: prior-only, live-knowable. NOT COUNT(*) over the
        -- episode (that includes FUTURE days — see the header).
        ROW_NUMBER() OVER e                                           AS barnum
    FROM daily_episodes
    WINDOW e AS (PARTITION BY ticker, episode ORDER BY date)
)
SELECT c.ticker, c.date,
    c.prev_adj_close, c.close_3d, c.close_7d, c.day_close, c.adj_ratio, c.avgvol20, c.avgvol20_prior,
    c.close_fwd_1d, c.close_fwd_3d, c.close_fwd_5d,
    l.n_bars_1s, l.tc_0945_tape, l.vol_0945_tape, l.dv_0945_tape, l.open_0930_tape,
    l.n_eff_shannon, l.n_eff_hhi, l.vol_0945_pm_tape,
    -- dv_0945 = the TAPE value (raw dollars). The old adj_ratio-scaled formula was the S35
    -- contamination; the engine's --min-dv-0945 gate defaults 0, THE floor is dv_0945_tape.
    l.dv_0945_tape AS dv_0945,
    l.vol_0945_pm_tape / NULLIF(c.avgvol20, 0)       AS rvol_0945,
    l.vol_0945_pm_tape / NULLIF(c.avgvol20_prior, 0) AS rvol_0945_honest
FROM ctx c
JOIN liq l ON l.ticker = c.ticker AND l.date = c.date   -- INNER JOIN = the (A') prune
WHERE c.barnum > 21;                                    -- (B) >= 21 PRIOR bars, prior-only

CREATE UNIQUE INDEX mr_candidate_1s_ticker_date ON mr_candidate_1s (ticker, date);
"""

printfn "Building `mr_candidate_1s` (dv_0945_tape >= $%.1fM AND n_eff_shannon >= %.0f; CS/ADRC; >=21 prior bars; NO price floor)" (minDv / 1e6) minNeff
printfn "  db:      %s" (IO.Path.GetFullPath dbPath)
printfn "  1s slim: %s" (IO.Path.GetFullPath slimDir)

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

exec "PRAGMA memory_limit='8GB'"
exec sql
sw.Stop()

let rows    = scalar "SELECT COUNT(*) FROM mr_candidate_1s" :?> int64
let tickers = scalar "SELECT COUNT(DISTINCT ticker) FROM mr_candidate_1s" :?> int64
let days    = scalar "SELECT COUNT(DISTINCT date) FROM mr_candidate_1s" :?> int64
printfn "Done in %.1fs: %d candidate rows, %d tickers, %d days" sw.Elapsed.TotalSeconds rows tickers days

printfn "Per-year:"
let cmd = conn.CreateCommand()
cmd.CommandText <- "SELECT year(date), COUNT(*) FROM mr_candidate_1s GROUP BY 1 ORDER BY 1"
use reader = cmd.ExecuteReader()
while reader.Read() do
    printfn "  %d  %d" (reader.GetInt64 0) (reader.GetInt64 1)
conn.Dispose()
