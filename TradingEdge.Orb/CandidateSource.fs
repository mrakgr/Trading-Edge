module TradingEdge.Orb.CandidateSource

// =============================================================================
// `--candidates SOURCE` — the candidate universe an engine reads (user, 2026-09-29:
// a COMMAND-LINE ARGUMENT, never an environment variable; it replaced
// FF_CANDIDATE_TABLE / LH_CANDIDATE_TABLE). SOURCE is either
//   - a TABLE in the open DuckDB connection (v1 trading.db: a whitelist, a corpus —
//     mr_candidate_1s_v2 is NOT a table since 2026-11-19) — identifier characters only,
//     so injection-safe; or
//   - PARQUET (data v2): a file, a glob, or a hive-partitioned directory
//     (e.g. data/v2/mr_candidate_1s_v2 with date=YYYY-MM-DD/ partitions).
// The engines splice `fromClause` where the table name used to go.
// =============================================================================

open System
open System.IO

/// ⭐ 2026-11-19 (user): the DATA v2 HIVE, cwd-relative (research commands run from research/). The v1 table of the same
/// name in trading.db was a pre-marker-free build ($2M floor on the double-counted opening cross, through 2026-09-04) that
/// silently dropped 16,991 of the current universe's ticker-days from a parity seal; it is archived
/// (/mnt/d/trading-edge-bulk/db_archive/2026-11-19/) and DROPPED, so a stale reference to it now fails loudly.
let defaultSource = "data/v2_candidates/mr_candidate_1s_v2"

/// the SQL to put after FROM
let fromClause (source: string) : string =
    let quoted (p: string) = p.Replace("'", "''")
    if source <> "" && source |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_') then source
    elif source.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase) || source.Contains '*' then
        sprintf "read_parquet('%s', hive_partitioning = true)" (quoted source)
    elif Directory.Exists source then
        sprintf "read_parquet('%s', hive_partitioning = true)" (quoted (Path.Combine(source, "**", "*.parquet")))
    else failwithf "--candidates %A: neither a table name (letters, digits, _) nor a parquet file / glob / directory" source

/// a query returning 1 if the source has the column, else 0 (the engines' column guards)
let hasColumnSql (source: string) (column: string) : string =
    sprintf "SELECT count(*) FROM (DESCRIBE SELECT * FROM %s) WHERE column_name = '%s'" (fromClause source) (column.Replace("'", "''"))
