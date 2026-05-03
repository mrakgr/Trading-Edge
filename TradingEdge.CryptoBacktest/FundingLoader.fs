module TradingEdge.CryptoBacktest.FundingLoader

open System
open System.IO
open DuckDB.NET.Data

// =============================================================================
// Funding-rate parquet loader
// =============================================================================
//
// CryptoData/download-funding writes one parquet per symbol at
// {root}/{SYMBOL}.parquet with schema:
//   calc_time_us         BIGINT  — funding settlement timestamp (UTC, microseconds)
//   funding_interval_us  BIGINT  — interval between settlements (8h × 3.6e9 us)
//   funding_rate         DOUBLE  — decimal rate per interval (0.0001 = 0.01%/8h)
//
// Funding events are sparse (3 per day) compared to bars, so loading the
// whole symbol-range in memory is cheap (≈ 2200 rows × 24 bytes per 2 years).
// The engine consumes them by stepping a pointer forward as bars advance.

[<Struct>]
type FundingEvent = {
    TimestampUs: int64
    Rate: float
}

let path (root: string) (symbol: string) : string =
    Path.Combine(root, sprintf "%s.parquet" symbol)

let exists (root: string) (symbol: string) : bool =
    File.Exists(path root symbol)

/// Load all funding events for one symbol within [startUs, endUs] inclusive,
/// sorted by timestamp. Returns an empty array if the parquet is missing.
let loadRange
    (root: string)
    (symbol: string)
    (startUs: int64)
    (endUs: int64)
    : FundingEvent[] =
    let p = path root symbol
    if not (File.Exists p) then [||]
    else
        let normalized = p.Replace('\\', '/').Replace("'", "''")
        use conn = new DuckDBConnection("Data Source=:memory:")
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            sprintf
                "SELECT calc_time_us, funding_rate
                 FROM read_parquet('%s')
                 WHERE calc_time_us BETWEEN %d AND %d
                 ORDER BY calc_time_us"
                normalized startUs endUs
        use reader = cmd.ExecuteReader()
        let result = ResizeArray<FundingEvent>(capacity = 4096)
        while reader.Read() do
            result.Add {
                TimestampUs = reader.GetInt64 0
                Rate = reader.GetDouble 1
            }
        result.ToArray()

/// Load by date range (inclusive endpoints).
let loadByDate
    (root: string)
    (symbol: string)
    (startDate: DateTime)
    (endDate: DateTime)
    : FundingEvent[] =
    let toUs (d: DateTime) =
        let dt = DateTime.SpecifyKind(d.Date, DateTimeKind.Utc)
        DateTimeOffset(dt).ToUnixTimeMilliseconds() * 1000L
    let lo = toUs startDate
    let hi = toUs (endDate.AddDays 1.0) - 1L
    loadRange root symbol lo hi
