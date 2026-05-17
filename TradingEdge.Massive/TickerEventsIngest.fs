module TradingEdge.TickerEventsIngest

open System
open System.IO
open DuckDB.NET.Data

/// Build data/tickers/events.parquet from the per-ticker JSONs, then load it
/// into the `ticker_events` table. We do the flattening entirely in DuckDB
/// SQL: read_json -> unnest events array -> write parquet -> COPY into table.
let buildAndLoad (jsonDir: string) (parquetPath: string) (dbPath: string) =
    if not (Directory.Exists jsonDir) then
        failwithf "JSON dir not found: %s" jsonDir

    let jsonGlob = Path.Combine(jsonDir, "*.json").Replace("\\", "/")
    let parquetPathFs = parquetPath.Replace("\\", "/")
    Directory.CreateDirectory(Path.GetDirectoryName parquetPath) |> ignore

    use conn = new DuckDBConnection($"Data Source={dbPath}")
    conn.Open()

    // 1) Flatten JSONs to a parquet. The `filename` virtual column gives us
    //    the query_ticker (path stem). We extract per-event rows by unnesting
    //    results.events; OK rows have an events array, NOT_FOUND rows do not.
    let buildSql =
        $"""
        COPY (
            WITH raw AS (
                SELECT
                    regexp_extract(filename, '([^/\\]+)\.json$', 1) AS query_ticker,
                    results.composite_figi AS figi,
                    results.name AS name,
                    results.cik AS cik,
                    results.events AS events
                FROM read_json(
                    '{jsonGlob}',
                    format := 'auto',
                    filename := true,
                    union_by_name := true,
                    maximum_object_size := 16777216
                )
                WHERE results IS NOT NULL
                  AND results.events IS NOT NULL
            ),
            exploded AS (
                SELECT
                    -- sanitize() in F# replaces '.' with '_'; reverse it here so
                    -- query_ticker matches the original Polygon symbol (BRK.B etc.).
                    replace(query_ticker, '_', '.') AS query_ticker,
                    figi,
                    name,
                    cik,
                    unnest(events) AS ev
                FROM raw
            )
            SELECT
                query_ticker,
                figi,
                name,
                cik,
                CAST(ev.date AS DATE) AS event_date,
                ev.type AS event_type,
                ev.ticker_change.ticker AS event_ticker
            FROM exploded
            ORDER BY query_ticker, event_date
        ) TO '{parquetPathFs}' (FORMAT PARQUET, COMPRESSION 'zstd');
        """

    printfn "Building %s …" parquetPath
    use cmd = conn.CreateCommand()
    cmd.CommandText <- buildSql
    cmd.ExecuteNonQuery() |> ignore

    // 2) Reload into the persistent table (truncate-and-insert for idempotence).
    use cmd2 = conn.CreateCommand()
    cmd2.CommandText <- $"DELETE FROM ticker_events;"
    cmd2.ExecuteNonQuery() |> ignore

    use cmd3 = conn.CreateCommand()
    cmd3.CommandText <-
        $"INSERT INTO ticker_events SELECT * FROM read_parquet('{parquetPathFs}');"
    let inserted = cmd3.ExecuteNonQuery()

    printfn "Loaded %d rows into ticker_events" inserted
