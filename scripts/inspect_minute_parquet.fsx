#r "nuget: DuckDB.NET.Data.Full, 1.4.4"

open DuckDB.NET.Data

let path = "data/minute_aggs_smoke/2024-04-01.parquet"

use conn = new DuckDBConnection("DataSource=:memory:")
conn.Open()

let query (sql: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    use r = cmd.ExecuteReader()
    let cols = [| for i in 0 .. r.FieldCount - 1 -> r.GetName i |]
    printfn "%s" (String.concat "\t" cols)
    while r.Read() do
        [| for i in 0 .. r.FieldCount - 1 ->
            if r.IsDBNull i then "NULL" else string (r.GetValue i) |]
        |> String.concat "\t"
        |> printfn "%s"
    printfn ""

printfn "-- schema --"
query (sprintf "DESCRIBE SELECT * FROM read_parquet('%s') LIMIT 1" path)

printfn "-- row count --"
query (sprintf "SELECT COUNT(*) AS rows, COUNT(DISTINCT ticker) AS tickers FROM read_parquet('%s')" path)

printfn "-- sample AAPL bars --"
query (sprintf "SELECT * FROM read_parquet('%s') WHERE ticker='AAPL' ORDER BY window_start LIMIT 5" path)

printfn "-- AAPL bar count --"
query (sprintf "SELECT ticker, COUNT(*) AS bars, MIN(to_timestamp(window_start/1000000000)) AS first_ts, MAX(to_timestamp(window_start/1000000000)) AS last_ts FROM read_parquet('%s') WHERE ticker='AAPL' GROUP BY ticker" path)
