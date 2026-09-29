module TradingEdge.Orb.CandidateSource

// =============================================================================
// `--candidates SOURCE` — the candidate universe an engine reads (user, 2026-09-29:
// a COMMAND-LINE ARGUMENT, never an environment variable; it replaced
// FF_CANDIDATE_TABLE / LH_CANDIDATE_TABLE). SOURCE is either
//   - a TABLE in the open DuckDB connection (v1 trading.db: mr_candidate_1s_v2, a
//     whitelist, a corpus) — identifier characters only, so injection-safe; or
//   - PARQUET (data v2): a file, a glob, or a hive-partitioned directory
//     (e.g. data/v2/mr_candidate_1s_v2 with date=YYYY-MM-DD/ partitions).
// The engines splice `fromClause` where the table name used to go.
// =============================================================================

open System
open System.IO

let defaultSource = "mr_candidate_1s_v2"

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
