module TradingEdge.CryptoLake.Schema

// =============================================================================
// On-disk parquet conventions
// =============================================================================
//
// We mirror the layout the Python downloader produces under /mnt/d so the two
// codepaths can share a cache:
//   {root}/{table}/{exchange}/{symbol}/{date}.parquet
//
// `book` schema (from lakeapi.load_data, ns->us already applied):
//   timestamp_us int64, received_time_us int64, sequence_number int64,
//   bid_{0..19}_price f64, bid_{0..19}_size f64,
//   ask_{0..19}_price f64, ask_{0..19}_size f64       (83 columns)
//
// `book_delta_v2` raw schema in Crypto Lake's S3 (NS timestamps):
//   timestamp i64 ns, receipt_timestamp i64 ns, sequence_number i64,
//   side_is_bid bool, price f64, size f64
// We pass the raw file through verbatim — no schema rewrite — because the
// 458 MB / 139 M-row daily volumes make any in-process round-trip prohibitive,
// and consumers can read either schema directly with DuckDB.

let bookLevels = 20

let bookColumnNames : string[] =
    [|
        yield "timestamp_us"
        yield "received_time_us"
        yield "sequence_number"
        for i in 0 .. bookLevels - 1 do
            yield sprintf "bid_%d_price" i
            yield sprintf "bid_%d_size" i
        for i in 0 .. bookLevels - 1 do
            yield sprintf "ask_%d_price" i
            yield sprintf "ask_%d_size" i
    |]

let dataPath (root: string) (table: string) (exchange: string) (symbol: string) (date: System.DateTime) : string =
    System.IO.Path.Combine(
        root, table, exchange, symbol,
        sprintf "%s.parquet" (date.ToString("yyyy-MM-dd")))
