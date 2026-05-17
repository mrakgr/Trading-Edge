module TradingEdge.Vwap.AdrGate

open System
open System.Collections.Generic
open DuckDB.NET.Data

/// Regime gate: for each calendar date D, returns true if the trailing
/// `windowDays`-day ADR-as-percent-of-mean-close (over days strictly before D)
/// is at least `minAdrPct`. Built from `split_adjusted_prices` in the daily
/// DuckDB. We compute it once for the whole date range at load time and
/// expose an O(1) lookup; this keeps the backtest loop dependency-free.
///
/// Lookahead-clean: the per-day average uses days strictly before D, so the
/// gate state for trading on D depends only on data fully known by D-1's close.

type AdrIndex(gateOn: Dictionary<DateOnly, bool>) =
    /// True if the gate is on (trading allowed) for the given date.
    /// Returns false if the date isn't in the index — conservative default
    /// is "no data → don't trade".
    member _.IsOn(d: DateOnly) : bool =
        match gateOn.TryGetValue d with
        | true, v -> v
        | _ -> false

/// Build the gate from the daily DB for the SPY symbol over [start, endInc].
/// Pulls a few extra `windowDays` of warmup history so the first business
/// day in the range has a fully populated trailing window.
let build
    (dbPath: string)
    (ticker: string)
    (start: DateOnly)
    (endInc: DateOnly)
    (windowDays: int)
    (minAdrPct: float)
    : AdrIndex =

    let warmupStart = start.AddDays(-(int windowDays + 14))  // calendar pad for weekends
    let sql =
        sprintf
            "WITH days AS (
                SELECT date,
                       adj_high - adj_low AS daily_range,
                       adj_close
                FROM split_adjusted_prices
                WHERE ticker = '%s'
                  AND date >= DATE '%s'
                  AND date <= DATE '%s'
             ),
             rolled AS (
                SELECT date,
                       100.0 * AVG(daily_range) OVER (
                           ORDER BY date
                           ROWS BETWEEN %d PRECEDING AND 1 PRECEDING
                       ) /
                       NULLIF(AVG(adj_close) OVER (
                           ORDER BY date
                           ROWS BETWEEN %d PRECEDING AND 1 PRECEDING
                       ), 0) AS adr_pct
                FROM days
             )
             SELECT CAST(date AS VARCHAR), adr_pct
             FROM rolled
             WHERE adr_pct IS NOT NULL
               AND date >= DATE '%s' AND date <= DATE '%s'
             ORDER BY date;"
            ticker
            (warmupStart.ToString("yyyy-MM-dd"))
            (endInc.ToString("yyyy-MM-dd"))
            windowDays windowDays
            (start.ToString("yyyy-MM-dd"))
            (endInc.ToString("yyyy-MM-dd"))

    use conn = new DuckDBConnection($"Data Source={dbPath}")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql

    let gateOn = Dictionary<DateOnly, bool>()
    use rdr = cmd.ExecuteReader()
    while rdr.Read() do
        let d = DateOnly.ParseExact(rdr.GetString(0), "yyyy-MM-dd")
        let adrPct = rdr.GetDouble(1)
        gateOn.[d] <- adrPct >= minAdrPct
    AdrIndex(gateOn)
