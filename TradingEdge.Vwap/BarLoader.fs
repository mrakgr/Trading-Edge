module TradingEdge.Vwap.BarLoader

open System
open System.IO
open DuckDB.NET.Data

/// One 1m bar for a single ticker, narrowed to the fields the VWAP engine
/// needs. `Bucket` is 0..959 indexing 04:00-19:59 ET in 1m steps; RTH = 330..719.
type Bar = {
    Date: DateOnly
    Bucket: int
    Open: float
    Close: float
    High: float
    Low: float
    Volume: int64
    DollarVolume: float
    Vwap: float
}

// RTH = 09:30..15:59 ET inclusive = buckets 330..719.
let rthStartBucket = 330
let rthEndBucket = 719  // last RTH bucket (15:59 ET); 16:00 = bucket 720 is postmarket

let private defaultBarsDir = "data/minute_bars_1m"

/// Load every RTH bar for `ticker` across the dates in [start, end] inclusive.
/// Returns one flat array sorted by (date, bucket). Days whose parquet is
/// missing are silently skipped — the caller treats this as "no trading data
/// for that day" (typically a weekend or holiday).
let loadRth (barsDir: string) (ticker: string) (start: DateOnly) (endInc: DateOnly) : Bar[] =
    use conn = new DuckDBConnection("Data Source=:memory:")
    conn.Open()

    let dirAbs = Path.GetFullPath barsDir
    // List existing parquets in the window. DuckDB's glob_pattern with a
    // BETWEEN gives us this cheaply.
    let listSql =
        sprintf
            "SELECT file FROM glob('%s/*.parquet') \
             WHERE regexp_extract(file, '([0-9]{4}-[0-9]{2}-[0-9]{2})\\.parquet$', 1) \
                   BETWEEN '%s' AND '%s' \
             ORDER BY file"
            (dirAbs.Replace("\\", "/")) (start.ToString("yyyy-MM-dd")) (endInc.ToString("yyyy-MM-dd"))

    let files = ResizeArray<string>()
    use lstCmd = conn.CreateCommand()
    lstCmd.CommandText <- listSql
    use lstRdr = lstCmd.ExecuteReader()
    while lstRdr.Read() do files.Add(lstRdr.GetString(0))
    lstRdr.Close()

    if files.Count = 0 then
        [||]
    else
        // Single bulk read over the union of selected parquet files; DuckDB
        // exposes the source filename via the `filename := true` flag so we
        // can recover the trading date.
        let filesList = String.concat "','" files
        let sql =
            sprintf
                "SELECT regexp_extract(filename, '([0-9]{4}-[0-9]{2}-[0-9]{2})\\.parquet$', 1) AS date, \
                        bucket, open, close, high, low, volume, dollar_volume, vwap \
                 FROM read_parquet(['%s'], filename := true) \
                 WHERE ticker = $ticker AND bucket BETWEEN %d AND %d \
                 ORDER BY date, bucket"
                filesList rthStartBucket rthEndBucket

        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        let pTicker = cmd.CreateParameter()
        pTicker.ParameterName <- "ticker"
        pTicker.Value <- ticker
        cmd.Parameters.Add pTicker |> ignore

        let out = ResizeArray<Bar>()
        use rdr = cmd.ExecuteReader()
        while rdr.Read() do
            out.Add {
                Date = DateOnly.ParseExact(rdr.GetString(0), "yyyy-MM-dd")
                Bucket = rdr.GetInt32(1)
                Open = rdr.GetDouble(2)
                Close = rdr.GetDouble(3)
                High = rdr.GetDouble(4)
                Low = rdr.GetDouble(5)
                Volume = rdr.GetInt64(6)
                DollarVolume = rdr.GetDouble(7)
                Vwap = rdr.GetDouble(8)
            }
        out.ToArray()
