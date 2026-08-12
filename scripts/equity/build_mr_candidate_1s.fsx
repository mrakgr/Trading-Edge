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
//   (B)  UNIVERSE/CONTEXT — CS/ADRC via `daily_episodes`. ⚠ NO WARMUP (S40, user
//                     2026-08-01): the >= 21-prior-bars clause was vestigial — nothing in
//                     the spec or (A') needs ANY prior day — and it excluded IPO/early-
//                     listing days. `barnum` (ROW_NUMBER, prior-only, live-knowable) is
//                     now a recorded COLUMN so the early-episode slice can be studied
//                     and, if toxic, cut post-hoc. (The old table's COUNT(*)-over-episode
//                     variant counted FUTURE days — membership conditioned on the
//                     episode's eventual length, a lookahead.) ⚠ NO PRICE FLOOR
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
// Columns (the LEGACY engine reads prev_adj_close/close_3d/day_close/adj_ratio/
// close_fwd_*; v2 renames those — see the S43br block below):
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
//   rvol_0945          vol_0945_pm_tape / avgvol20 — report-only (avgvol20 includes D)
//   rvol_0945_honest   vol_0945_pm_tape / avgvol20_prior — LIVE-SAFE. Gate on this one.
//
// ⭐⭐ S43br (2026-08-12): THE DAILY-CONTEXT COLUMNS ARE REBUILT AND RENAMED.
// They now come from `daily_episodes_causal` (over the CAUSAL `daily_adjusted`)
// instead of `daily_episodes` (over the back-adjusted `split_adjusted_prices`),
// and every one is expressed in day D's RAW scale. Renamed deliberately so a stale
// query FAILS rather than silently reading raw prices as adjusted:
//
//     day_close       -> close_d      (RAW close on D)
//     adj_ratio       -> n            (causal cumulative split multiplier)
//     prev_adj_close  -> close_m1  + div_m1
//     close_3d/7d     -> close_m3/m7 + div_m3/m7
//     close_fwd_1/3/5 -> close_p1/p3/p5 + div_p1/p3/p5
//     (new)              open_p1     next session's OPEN, in D's raw scale
//
// Each reference day carries BOTH a price and a dividend increment because a return
// needs both endpoints' scale info:  ret = (close_x + div_x) / close_d - 1.
// ⚠ avgvol20 / avgvol20_prior are now in D's RAW SHARE scale. The old
// AVG(adj_volume) was raw x product-of-FUTURE-splits — a lookahead on 9.37% of the
// universe, 134,203 ticker-days (AAPL 2020-08-27 recorded 155.3M vs a raw 38.8M,
// x4 from a split four days later), sitting under the denominator of "LIVE-SAFE"
// rvol_0945_honest. Dormant only because the engine's MinRvol0945 defaults to 0.
// See docs/price_adjustment.md.
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
    | Min_Bars of int
    | [<AltCommandLine("-t")>] Table of string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Db _ -> "DuckDB database path (default: data/trading.db)."
            | Slim_Dir _ -> "Directory of 1s slim parquet files (default: data/intraday_1s_slim)."
            | Min_Dv _ -> "dv_0945_tape floor in raw dollars (default: 2e6)."
            | Min_Neff _ -> "n_eff_shannon floor — ⚠ RETIRED as the gate (S43u), 0 = off (default: 0)."
            | Min_Bars _ -> "n_bars_1s floor over [09:30,09:45) = 900 - gap count. THE (A') gate (default: 200)."
            | Table _ -> "Destination table (default: mr_candidate_1s_v2). ⚠ v2 is the CAUSAL rebuild (S43br) with RENAMED columns; the engine still reads the legacy `mr_candidate_1s` until it is migrated."

let parser = ArgumentParser.Create<CliArgs>(programName = "build_mr_candidate_1s.fsx")
let parsed =
    try parser.Parse(fsi.CommandLineArgs |> Array.skip 1, raiseOnUsage = true)
    with :? ArguParseException as ex -> eprintfn "%s" ex.Message; exit 1

let dbPath  = parsed.TryGetResult Db |> Option.defaultValue "data/trading.db"
let slimDir = parsed.TryGetResult Slim_Dir |> Option.defaultValue "data/intraday_1s_slim"
let minDv   = parsed.TryGetResult Min_Dv |> Option.defaultValue 2e6
// ⭐ S43u (user): n_eff_shannon RETIRED as the (A') gate, replaced by a GAP COUNT.
// Why: the 09:30 opening auction print is a median 26% (p90 58%) of ALL 09:30-09:45
// volume, so exp(Shannon H) over per-second volume shares is substantially a measure of
// HOW BIG THE OPENING PRINT WAS. Excluding that one second lifts median n_eff 27.9 ->
// 50.6, and 22% of ticker-days that FAIL n_eff>=25 would PASS without it. A gap count is
// immune: it asks only whether the second traded at all, never how much.
// Knowability is unchanged — both are folded over [09:30, 09:45) and fully determined by
// 09:45, and EntryStartMin >= 09:45. Iso-universe threshold is 210; 200 is the round pick
// (56.8% vs 54.0% kept). The two filters disagree on 20.9% of ticker-days.
let minNeff = parsed.TryGetResult Min_Neff |> Option.defaultValue 0.0
let minBars = parsed.TryGetResult Min_Bars |> Option.defaultValue 200
// ⚠ Builds ALONGSIDE the legacy table by default. The column set is deliberately
// RENAMED (day_close -> close_d, prev_adj_close -> close_m1, adj_ratio -> n, ...) so
// a stale query fails LOUDLY rather than silently reading raw prices as adjusted —
// which is exactly the failure mode CLAUDE.md rule 4 exists for. Swap the engine over
// with FF_CANDIDATE_TABLE once the control run passes.
let tbl = parsed.TryGetResult Table |> Option.defaultValue "mr_candidate_1s_v2"
let glob = IO.Path.Combine(slimDir, "*.parquet").Replace("'", "''")

// Seconds-since-ET-midnight anchors: premarket 04:00 = 14400, RTH open 09:30 = 34200,
// scan boundary 09:45 = 35100. The liquidity window is [34200, 35100) — fully known by
// 09:45, no lookahead into the entry window (EntryStartMin >= 09:45).
let premktSec = 14400
let rthOpenSec = 34200
let scanSec = 35100

let sql = $"""
DROP TABLE IF EXISTS {tbl};
CREATE TABLE {tbl} AS
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
    -- ⭐ S43u: THE (A') GATE IS NOW THE GAP COUNT. n_bars_1s = seconds in [09:30,09:45)
    -- that traded at all; gaps = 900 - n_bars_1s. Immune to the opening print (see header).
    -- n_eff_shannon is still RECORDED (both orders) but no longer gates unless --min-neff
    -- is passed explicitly.
    HAVING sum(vwap::DOUBLE * volume::DOUBLE) FILTER (bucket >= {rthOpenSec}) >= {minDv}
       AND count(*) FILTER (bucket >= {rthOpenSec}) >= {minBars}
       AND ({minNeff} <= 0.0
            OR CASE WHEN sum(volume::DOUBLE) FILTER (bucket >= {rthOpenSec} AND volume > 0) > 0 THEN
                   exp( ln(sum(volume::DOUBLE) FILTER (bucket >= {rthOpenSec} AND volume > 0))
                      - sum(volume::DOUBLE * ln(volume::DOUBLE)) FILTER (bucket >= {rthOpenSec} AND volume > 0)
                        / sum(volume::DOUBLE) FILTER (bucket >= {rthOpenSec} AND volume > 0) )
               END >= {minNeff})
),
-- (B) episode-partitioned daily context, expressed in day D's RAW scale (S43br).
-- ⭐ Sourced from `daily_episodes_causal` (over `daily_adjusted`), NOT the legacy
-- `daily_episodes` (over `split_adjusted_prices`). The old source was back-adjusted,
-- so every column carried a FUTURE-split factor. See docs/price_adjustment.md.
--
-- Conversion of any other day t into D's scale (both endpoints needed — a return
-- is not a ratio of levels when the share count changes):
--     price  :  P(t) * n(t)/n(D)          div increment : (C(t) - C(D)) / n(D)
--     ret    :  (close_x + div_x) / close_d - 1        for any stored day x
-- and between two stored days: (px2 + div2 - px1 - div1) / px1.
-- `div_m*` are NEGATIVE by construction (cash was paid between t and D) — correct.
ctx AS (
    SELECT ticker, date,
        close AS close_d,                                             -- RAW close on D
        n,
        LAG(close,1) OVER e * LAG(n,1) OVER e / n                     AS close_m1,
        LAG(close,3) OVER e * LAG(n,3) OVER e / n                     AS close_m3,
        LAG(close,7) OVER e * LAG(n,7) OVER e / n                     AS close_m7,
        (LAG(cum_div,1) OVER e - cum_div) / n                         AS div_m1,
        (LAG(cum_div,3) OVER e - cum_div) / n                         AS div_m3,
        (LAG(cum_div,7) OVER e - cum_div) / n                         AS div_m7,
        -- Forward columns + the NEXT SESSION'S OPEN (S43bq, user request): outcome
        -- measurement only. Lookahead BY DESIGN — never gate on them.
        LEAD(close,1) OVER e * LEAD(n,1) OVER e / n                   AS close_p1,
        LEAD(close,3) OVER e * LEAD(n,3) OVER e / n                   AS close_p3,
        LEAD(close,5) OVER e * LEAD(n,5) OVER e / n                   AS close_p5,
        LEAD(open,1)  OVER e * LEAD(n,1) OVER e / n                   AS open_p1,
        (LEAD(cum_div,1) OVER e - cum_div) / n                        AS div_p1,
        (LEAD(cum_div,3) OVER e - cum_div) / n                        AS div_p3,
        (LEAD(cum_div,5) OVER e - cum_div) / n                        AS div_p5,
        -- ⭐ Volume in D's SHARE scale: volume(t) * n(D)/n(t) — the RECIPROCAL of the
        -- price conversion, so price x volume (dollar volume) is scale-invariant.
        -- ⚠ THIS FIXES A LOOKAHEAD. The old `AVG(adj_volume)` used
        -- split_adjusted_prices' adj_volume = raw x product-of-FUTURE-splits, so
        -- avgvol20 was FUTURE-SCALED on 134,203 of 1,431,802 universe ticker-days
        -- (9.37 pct). AAPL 2020-08-27 recorded 155.3M against a raw 38.8M — x4 from
        -- a split four days LATER. That denominator sits under `rvol_0945_honest`,
        -- the column the old header called "LIVE-SAFE, gate on this one". It was
        -- dormant only because the engine's MinRvol0945 defaults to 0.
        -- (NB 64 pct of rows have adj_ratio != 1, but that is mostly the DIVIDEND
        -- factor; adj_volume carries the split factor only. Do not conflate them.)
        n * AVG(volume / n) OVER (PARTITION BY ticker, episode ORDER BY date
                                  ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) AS avgvol20,
        -- LIVE-SAFE twin: the 20 bars ENDING AT D-1 (gate on this one, never avgvol20 — F14).
        n * AVG(volume / n) OVER (PARTITION BY ticker, episode ORDER BY date
                                  ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS avgvol20_prior,
        -- D's position in its episode: prior-only, live-knowable. NOT COUNT(*) over the
        -- episode (that includes FUTURE days — see the header).
        ROW_NUMBER() OVER e                                           AS barnum
    FROM daily_episodes_causal
    WINDOW e AS (PARTITION BY ticker, episode ORDER BY date)
)
SELECT c.ticker, c.date, c.barnum, c.n,
    c.close_d, c.close_m1, c.close_m3, c.close_m7, c.div_m1, c.div_m3, c.div_m7,
    c.close_p1, c.close_p3, c.close_p5, c.open_p1, c.div_p1, c.div_p3, c.div_p5,
    c.avgvol20, c.avgvol20_prior,
    l.n_bars_1s, l.tc_0945_tape, l.vol_0945_tape, l.dv_0945_tape, l.open_0930_tape,
    l.n_eff_shannon, l.n_eff_hhi, l.vol_0945_pm_tape,
    -- dv_0945 = the TAPE value (raw dollars). The old adj_ratio-scaled formula was the S35
    -- contamination; the engine's --min-dv-0945 gate defaults 0, THE floor is dv_0945_tape.
    l.dv_0945_tape AS dv_0945,
    -- Now scale-consistent for the first time: a RAW tape numerator over a
    -- D-raw-scale denominator. Previously raw / future-adjusted.
    l.vol_0945_pm_tape / NULLIF(c.avgvol20, 0)       AS rvol_0945,
    l.vol_0945_pm_tape / NULLIF(c.avgvol20_prior, 0) AS rvol_0945_honest
FROM ctx c
JOIN liq l ON l.ticker = c.ticker AND l.date = c.date;  -- INNER JOIN = the (A') prune
-- (S40: the `WHERE c.barnum > 21` warmup is REMOVED — barnum is recorded instead.)

CREATE UNIQUE INDEX {tbl}_ticker_date ON {tbl} (ticker, date);
"""
// (S39j max_slot_absr_bp volat-prepass column RETIRED from the build (user): the
// provable trim only bought ~28.5% of streaming and the per-day fill cost ~7 min per
// rebuild — the engine-derived `flushfader_base_tkds` signal-day table is the real
// trim. The engine's auto-trim clause is column-existence-guarded, so tables built
// without the column just skip it.)

printfn "Building `%s` (dv_0945_tape >= $%.1fM AND n_bars_1s >= %d [gaps <= %d of 900]%s; CS/ADRC; NO warmup, NO price floor)"
    tbl (minDv / 1e6) minBars (900 - minBars)
    (if minNeff > 0.0 then sprintf " AND n_eff_shannon >= %.0f" minNeff else "; n_eff gate OFF (S43u)")
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

let rows    = scalar $"SELECT COUNT(*) FROM {tbl}" :?> int64
let tickers = scalar $"SELECT COUNT(DISTINCT ticker) FROM {tbl}" :?> int64
let days    = scalar $"SELECT COUNT(DISTINCT date) FROM {tbl}" :?> int64
printfn "Done in %.1fs: %d candidate rows, %d tickers, %d days" sw.Elapsed.TotalSeconds rows tickers days

printfn "Per-year:"
let cmd = conn.CreateCommand()
cmd.CommandText <- $"SELECT year(date), COUNT(*) FROM {tbl} GROUP BY 1 ORDER BY 1"
use reader = cmd.ExecuteReader()
while reader.Read() do
    printfn "  %d  %d" (reader.GetInt64 0) (reader.GetInt64 1)
conn.Dispose()
