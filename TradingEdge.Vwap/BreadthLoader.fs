module TradingEdge.Vwap.BreadthLoader

open System
open System.Collections.Generic
open DuckDB.NET.Data

/// Imbalance breadth for a single RTH minute. Loaded from
/// `data/breadth/imbalance_vwma_{N}m.parquet`. `Imbalance` is in [-1, 1];
/// >0 means buy dollar volume dominated, <0 means sell dollar volume
/// dominated, across S&P 500 constituents over the trailing N minutes.
type BreadthBar = {
    Date: DateOnly
    Bucket: int
    Imbalance: float
}

/// Load imbalance breadth from a parquet across [start, endInc], one row
/// per (date, bucket). The keying matches BarLoader.Bar exactly.
let load (parquetPath: string) (start: DateOnly) (endInc: DateOnly) : BreadthBar[] =
    use conn = new DuckDBConnection("Data Source=:memory:")
    conn.Open()
    let sql =
        sprintf
            "SELECT CAST(date AS VARCHAR), bucket, imbalance \
             FROM read_parquet('%s') \
             WHERE date BETWEEN DATE '%s' AND DATE '%s' \
             ORDER BY date, bucket"
            (parquetPath.Replace("\\", "/"))
            (start.ToString("yyyy-MM-dd"))
            (endInc.ToString("yyyy-MM-dd"))
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    let out = ResizeArray<BreadthBar>()
    use rdr = cmd.ExecuteReader()
    while rdr.Read() do
        out.Add {
            Date = DateOnly.ParseExact(rdr.GetString(0), "yyyy-MM-dd")
            Bucket = rdr.GetInt32(1)
            Imbalance = rdr.GetDouble(2)
        }
    out.ToArray()

/// Indexed (date, bucket) → imbalance lookup. Used by the backtest engine
/// to query the breadth value at each SPY bar close in O(1).
type BreadthIndex(bars: BreadthBar[]) =
    let map = Dictionary<struct (DateOnly * int), float>(bars.Length)
    do
        for b in bars do
            map.[struct (b.Date, b.Bucket)] <- b.Imbalance

    /// Returns Some imbalance if present for this (date, bucket); None
    /// otherwise (e.g., breadth file doesn't cover that day, or that
    /// bucket is pre-RTH lookback territory the breadth builder dropped).
    member _.TryGet(d: DateOnly, bucket: int) : float option =
        match map.TryGetValue(struct (d, bucket)) with
        | true, v -> Some v
        | _ -> None
