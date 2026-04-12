module TradingEdge.Parsing.VolumeBars

open System
open System.Collections.Immutable
open DuckDB.NET.Data
open TradeLoader

// =============================================================================
// Database Access
// =============================================================================

let getAvgVolume (ticker: string) (date: DateTime option) (dbPath: string) : float option =
    use conn = new DuckDBConnection($"Data Source={dbPath}")
    conn.Open()

    let query, parameters =
        match date with
        | Some d ->
            let dateStr = d.ToString("yyyy-MM-dd")
            """
            SELECT AVG(adj_volume) as avg_volume
            FROM (
                SELECT adj_volume
                FROM split_adjusted_prices
                WHERE ticker = ? AND date < CAST(? AS DATE)
                ORDER BY date DESC
                LIMIT 20
            )
            """, [| box ticker; box dateStr |]
        | None ->
            """
            SELECT AVG(adj_volume) as avg_volume
            FROM (
                SELECT adj_volume
                FROM split_adjusted_prices
                WHERE ticker = ?
                ORDER BY date DESC
                LIMIT 20
            )
            """, [| box ticker |]

    use cmd = new DuckDBCommand(query, conn)
    for p in parameters do cmd.Parameters.Add(DuckDBParameter(Value = p)) |> ignore

    use reader = cmd.ExecuteReader()
    if reader.Read() && not (reader.IsDBNull(0)) then
        Some (reader.GetDouble(0))
    else
        None

// =============================================================================
// Volume Bar Types
// =============================================================================

type VolumeBar = 
    {
        CumulativeVolume: float
        VWAP: float
        StdDev: float
        Volume: float
        Trades: ImmutableArray<Trade>
    }

    member self.StartTime = self.Trades.[0].Timestamp
    member self.EndTime = self.Trades.[self.Trades.Length - 1].Timestamp
    member self.NumTrades = self.Trades.Length