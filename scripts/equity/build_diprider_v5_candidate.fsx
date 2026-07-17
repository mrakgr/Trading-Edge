#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#r "nuget: Argu, 6.2.5"

// Build `diprider_v5_candidate` — the LIVE-SAFE in-play universe for DipRiderV5.
//
// ===========================================================================
// WHY THIS TABLE EXISTS (read docs/lookahead_protocol.md first).
//
// DipRiderV4 read `vwap_reclaim_candidate`, whose universe filter was
//     avgvol20 * day_close >= $30M            -- "ADV floor". LOOKED like plumbing.
// BOTH factors are unknowable at the 10:00 entry:
//   * day_close = D's CLOSING price.
//   * avgvol20  = ROWS BETWEEN 19 PRECEDING AND CURRENT ROW — it CONTAINS D's own
//                 full-session volume. A volume spike on D inflates D's OWN 20-day
//                 average, pushing the name over the floor. The universe therefore
//                 admitted names BECAUSE OF WHAT HAPPENED THAT DAY (F14).
// It was a backdoor "today is a 12x-volume day" selector and it killed the system
// (PF 2.876 -> 1.158 once removed).
//
// THE FIX (user, 2026-07-17): select on the OPENING 15 MINUTES' DOLLAR VOLUME —
//     dv_0945 = vol_0945 * avgprice_0945 * adj_ratio
// The structural point is NOT that the threshold is different; it is that this is a
// FIXED, CLOSED window. A rolling average that ends at CURRENT ROW can always swallow
// D's own outcome; a window that ends at 09:45 cannot. It also selects for the thing
// we actually want (is this name IN PLAY this morning?) rather than a stale liquidity
// proxy that "would eliminate the biggest winners, and loosening it would let all
// kinds of trash through" (user).
//
// ⚠ KNOWABILITY CLOCK (R3): dv_0945 is fully determined at 09:45. It is legal ONLY
//   because DipRiderV5's EntryStartMin = 10:00 >= 09:45. This alignment is LOAD-BEARING:
//   lower the entry window below 09:45 and this filter SILENTLY becomes a lookahead —
//   the same trap that already makes med_bar_vol_0945 alignment-dependent.
//
// THE TWO IN-PLAY GATES (both live-safe, both complete at 09:45):
//   (1) dv_0945           >= $5M  — the opening dollar-volume floor (replaces the leaked ADV).
//   (2) rvol_0945_honest  >= 1.0  — is this name trading ABOVE its own normal pace? V4 inherited
//       `rvol_0945 > 1` from vwap_reclaim_candidate, but THAT column divides by avgvol20 (which
//       contains D's own full-session volume) ⇒ lookahead. rvol_0945_honest divides by
//       avgvol20_prior (20 sessions ending D-1, known pre-open) ⇒ legal. The GATE was always
//       legitimate; only its denominator was broken (R2: gate on the _prior twin).
//
// Run:  dotnet fsi scripts/equity/build_diprider_v5_candidate.fsx
//       dotnet fsi scripts/equity/build_diprider_v5_candidate.fsx -- --min-dv 10000000
// ===========================================================================

open System
open Argu
open DuckDB.NET.Data

type CliArgs =
    | [<AltCommandLine("-d")>] Db of string
    | [<AltCommandLine("-v")>] Min_Dv of float
    | [<AltCommandLine("-r")>] Min_Rvol of float
    | [<AltCommandLine("-t")>] Table of string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Db _ -> "DuckDB database path (default: data/trading.db)."
            | Min_Dv _ -> "Minimum live-safe 09:30-09:45 dollar volume (default: 0 = no floor; swept via the engine's --min-dv0945)."
            | Min_Rvol _ -> "Minimum LIVE-SAFE rvol_0945_honest (premkt-inclusive vol thru 09:45 / prior-20d avg). Default 1.0 = trading above its own normal pace."
            | Table _ -> "Output table name (default: diprider_v5_candidate)."

let parser = ArgumentParser.Create<CliArgs>(programName = "build_diprider_v5_candidate.fsx")
let parsed =
    try parser.Parse(fsi.CommandLineArgs |> Array.skip 1, raiseOnUsage = true)
    with :? ArguParseException as ex -> eprintfn "%s" ex.Message; exit 1

let dbPath = parsed.TryGetResult Db |> Option.defaultValue "data/trading.db"
let minDv   = parsed.TryGetResult Min_Dv |> Option.defaultValue 0.0
let minRvol = parsed.TryGetResult Min_Rvol |> Option.defaultValue 1.0
let table  =
    match parsed.TryGetResult Table |> Option.defaultValue "diprider_v5_candidate" with
    | t when t |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_') -> t
    | bad -> failwithf "Invalid table name %A (identifier chars only)" bad

// The floor is kept as a COLUMN, not baked in, so the engine can sweep it without a rebuild.
// Default 0 = pass everything through; the sweep lives in the backtest's --min-dv0945 flag.
let sql = $"""
DROP TABLE IF EXISTS {table};
CREATE TABLE {table} AS
SELECT ticker, date,
    prev_adj_close, close_3d, close_7d, day_close, adj_ratio,
    avgvol20,          -- rvol DENOMINATOR only. NEVER gate on this (F14).
    avgvol20_prior,    -- the live-safe daily-ADV twin, if a D-1 ADV gate is ever wanted.
    close_fwd_1d, close_fwd_3d, close_fwd_5d,
    day_open, med_bar_vol_0945, nbar_0945, vol_0945, avgprice_0945,
    dv_0945,           -- ⭐ THE LIVE-SAFE IN-PLAY SELECTOR (legal iff EntryStartMin >= 09:45)
    rvol_0945,         -- ⚠ contaminated denominator — carried for REPORTING/comparison only.
    rvol_0945_honest   -- ⭐ the LIVE-SAFE rvol (÷ avgvol20_prior) — THE ONE THE GATE BELOW USES.
FROM mr_candidate
WHERE dv_0945 >= {minDv}
  AND rvol_0945_honest >= {minRvol};

CREATE UNIQUE INDEX {table}_ticker_date ON {table} (ticker, date);
"""

printfn "Building `%s` — LIVE-SAFE universe: dv_0945 >= %s AND rvol_0945_honest >= %.2f" table (if minDv > 0.0 then $"$%.0f{minDv}" else "0 (no floor; sweep in-engine)") minRvol
printfn "  db: %s" (IO.Path.GetFullPath dbPath)

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
exec "CHECKPOINT"
sw.Stop()

let rows    = scalar $"SELECT COUNT(*) FROM {table}" :?> int64
let tickers = scalar $"SELECT COUNT(DISTINCT ticker) FROM {table}" :?> int64
printfn "Done in %.1fs: %d rows, %d tickers" sw.Elapsed.TotalSeconds rows tickers
conn.Dispose()
