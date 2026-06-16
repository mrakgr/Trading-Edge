module TradingEdge.MomentumBacktest.Signals

open System
open System.Data
open System.Collections.Generic
open Dapper
open TradingEdge.MomentumBacktest.Types

/// Build the per-ticker signal query. The two rolling-window SIZES (lookback-high
/// and stop-low) cannot be DuckDB runtime parameters — `ROWS BETWEEN N PRECEDING`
/// requires a literal — so they are validated ints interpolated into the SQL
/// string. Everything else (thresholds, dates, liquidity floor, tradable filter)
/// is a real bound parameter. The `... AND 1 PRECEDING` upper bound on each window
/// is what EXCLUDES the current day, giving point-in-time correctness.
///
/// Scoped to a single ticker (`$ticker`) so the caller can stream ticker-by-ticker
/// and keep the .NET heap bounded. Returns the FULL per-day series for that ticker
/// over the date window (the stop-walk needs every bar between entry and exit);
/// `is_entry` marks entry days.
/// SQL expressions for the 66 structure-level columns, generated from
/// structurePeriods × levelKinds. Each is a window aggregate over the prior N
/// bars (`ROWS BETWEEN N PRECEDING AND 1 PRECEDING` — excludes the current bar).
/// Emitted in the `base` CTE (uses the WINDOW alias `w`, partitioned by ticker,
/// ordered by date). Column order matches Types.structureColumns exactly.
let private structureSql () : string =
    let exprFor kind (n: int) =
        let frame = sprintf "ROWS BETWEEN %d PRECEDING AND 1 PRECEDING" n
        match kind with
        | "trail"   -> sprintf "LAG(p.adj_close, %d) OVER w" n
        | "ma"      -> sprintf "AVG(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date %s)" frame
        | "hi"      -> sprintf "MAX(p.adj_high)  OVER (PARTITION BY p.ticker ORDER BY p.date %s)" frame
        | "lo"      -> sprintf "MIN(p.adj_low)   OVER (PARTITION BY p.ticker ORDER BY p.date %s)" frame
        | "hiclose" -> sprintf "MAX(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date %s)" frame
        | "loclose" -> sprintf "MIN(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date %s)" frame
        | other     -> failwithf "unknown level kind %s" other
    [ for kind in levelKinds do
        for (label, n) in structurePeriods do
            yield sprintf "        %s AS %s_%s" (exprFor kind n) kind label ]
    |> String.concat ",\n"

let private query (lookbackHigh: int) (stopLowWindow: int) (minPctOf52wHigh: float option) (noStructure: bool) : string =
    // Guard: only ever interpolate validated positive ints, never user strings.
    if lookbackHigh < 1 || stopLowWindow < 1 then
        invalidArg "window" "lookback/stop windows must be >= 1"
    // Structure levels are PRECOMPUTED in the `structure_levels` table (one row per
    // ticker/date, 66 columns) rather than recomputed as 66 window aggregates per run.
    // When noStructure, we skip the JOIN entirely and emit NULL placeholders so the
    // reader's ordinals still resolve — a full run then drops from ~12 min to seconds
    // (the JOIN + 66-column marshalling is the whole cost; the core windows are ~free).
    // `base` carries the levels as `s.<col>` (aliased to the bare name); the final
    // SELECT reads them by bare name. Placeholders are `NULL AS <col>` in `base`.
    let structInBase =
        (if noStructure then Types.structureColumns |> List.map (sprintf "NULL AS %s")
         else Types.structureColumns |> List.map (sprintf "s.%s"))
        |> String.concat ", "
    let structJoin =
        if noStructure then ""
        else "      JOIN structure_levels s ON s.ticker = p.ticker AND s.date = p.date\n"
    // 52-week-high proximity gate: require adj_close >= $min52wPct * hi_252_prior.
    // None => no gate (full breakout range). The threshold is a BOUND parameter
    // ($min52wPct), never interpolated. 1.0 = strict new-high; 0.85 = within-15% band.
    let fiftyTwoTerm =
        match minPctOf52wHigh with
        | Some _ -> "          AND adj_close >= $min52wPct * hi_252_prior\n"
        | None -> ""
    $"""
    WITH base AS (
      SELECT
        p.ticker, p.date,
        p.adj_open, p.adj_high, p.adj_low, p.adj_close, p.adj_volume,
        LAG(p.adj_close) OVER w                       AS prev_adj_close,
        v.avg_volume_4w, v.avg_dollar_volume_4w,
        ROW_NUMBER() OVER w                           AS prior_idx,
        MAX(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date
            ROWS BETWEEN {lookbackHigh} PRECEDING AND 1 PRECEDING) AS hi_252_prior,
        MIN(p.adj_low)   OVER (PARTITION BY p.ticker ORDER BY p.date
            ROWS BETWEEN {stopLowWindow} PRECEDING AND 1 PRECEDING) AS low_15_prior,
        -- per-bar true range (needs prev close); NULL on a ticker's first bar.
        GREATEST(
            p.adj_high - p.adj_low,
            ABS(p.adj_high - LAG(p.adj_close) OVER w),
            ABS(p.adj_low  - LAG(p.adj_close) OVER w)
        )                                             AS tr,
        {structInBase}
      FROM split_adjusted_prices p
      JOIN stock_volume_4w v ON v.ticker = p.ticker AND v.date = p.date
{structJoin}      WHERE p.ticker = $ticker
      WINDOW w AS (PARTITION BY p.ticker ORDER BY p.date)
    ),
    windowed AS (
      SELECT *,
        -- 14-day mean true range over the 14 PRIOR bars (excludes the current bar
        -- via the `AND 1 PRECEDING` bound), so all of these measure the name's
        -- state going INTO the breakout. No lookahead.
        AVG(tr) OVER w14 AS atr_abs_14,
        AVG(tr) OVER w14 / NULLIF(adj_close, 0) AS atr_pct_14,
        -- 14-day price span (consolidation width): highest high minus lowest low
        -- over the same prior window.
        ( MAX(adj_high) OVER w14 - MIN(adj_low) OVER w14 ) AS range_abs_14
      FROM base
      WINDOW w14 AS (PARTITION BY ticker ORDER BY date
                     ROWS BETWEEN 14 PRECEDING AND 1 PRECEDING)
    ),
    ratioed AS (
      SELECT *,
        range_abs_14 / NULLIF(adj_close, 0) AS range_pct_14,
        -- Consolidation tightness: 14-day span / cumulative daily travel
        -- (14 * ATR). ~1.0 = the window trended cleanly; well below 1 = price
        -- chopped in a tight band relative to how much it moved day-to-day
        -- (a "coiled spring"). Bounded, scale-free.
        range_abs_14 / NULLIF(14.0 * atr_abs_14, 0) AS tightness_14
      FROM windowed
    ),
    flagged AS (
      SELECT *,
        adj_close / prev_adj_close - 1.0       AS pct_up,
        adj_volume / NULLIF(avg_volume_4w, 0)  AS rvol,
        COALESCE(
          adj_close / prev_adj_close - 1.0 >= $upThr
          AND adj_volume / NULLIF(avg_volume_4w, 0) >= $rvolThr
{fiftyTwoTerm}          AND prior_idx > $minPriorDays
          AND avg_dollar_volume_4w >= $minAdv, FALSE) AS is_entry
      FROM ratioed
    )
    SELECT
      ticker, date, adj_open, adj_high, adj_low, adj_close, adj_volume,
      prev_adj_close, avg_volume_4w, avg_dollar_volume_4w, prior_idx,
      hi_252_prior, low_15_prior, atr_pct_14, range_pct_14, tightness_14,
      pct_up, rvol, is_entry,
      {Types.structureColumns |> String.concat ", "}
    FROM flagged
    WHERE date >= $start AND date <= $end
    ORDER BY date
    """

/// Tickers that pass the security-type filter. CS/ADRC only when tradableOnly;
/// otherwise every ticker with daily data. Done once up front so per-ticker
/// signal queries don't each re-scan ticker_reference.
let eligibleTickers (conn: IDbConnection) (tradableOnly: bool) : string[] =
    let sql =
        if tradableOnly then
            // INTERSECT with daily data presence so we don't query tickers that
            // have a reference row but no prices.
            """
            SELECT DISTINCT p.ticker
            FROM split_adjusted_prices p
            JOIN ticker_reference r ON r.ticker = p.ticker
            WHERE r.type IN ('CS','ADRC')
            ORDER BY 1
            """
        else
            "SELECT DISTINCT ticker FROM split_adjusted_prices ORDER BY 1"
    conn.Query<string>(sql) |> Seq.toArray

/// Load the full per-day signal series for one ticker, ordered by date.
/// Uses a manual reader (not Dapper) so the 66 generated structure columns can be
/// folded into each row's `levels` dictionary by name rather than 66 record fields.
let loadTicker (conn: IDbConnection) (cfg: Config) (ticker: string) : SignalRow[] =
    let sql = query cfg.LookbackHigh cfg.StopLowWindow cfg.MinPctOf52wHigh cfg.NoStructure
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    let addParam (name: string) (value: obj) =
        let p = cmd.CreateParameter()
        p.ParameterName <- name
        p.Value <- value
        cmd.Parameters.Add p |> ignore
    addParam "ticker" ticker
    addParam "upThr" cfg.UpThreshold
    addParam "rvolThr" cfg.RvolThreshold
    addParam "minPriorDays" (int64 cfg.MinPriorDays)
    addParam "minAdv" cfg.MinAvgDollarVolume
    addParam "start" (cfg.StartDate.ToString("yyyy-MM-dd"))
    addParam "end" (cfg.EndDate.ToString("yyyy-MM-dd"))
    // Only bind $min52wPct when the gate is active (it appears in the SQL only then).
    match cfg.MinPctOf52wHigh with
    | Some pct -> addParam "min52wPct" pct
    | None -> ()

    use reader = cmd.ExecuteReader()
    // Resolve column ordinals once.
    let ord (c: string) = reader.GetOrdinal c
    let oTicker = ord "ticker"
    let oDate = ord "date"
    let oOpen = ord "adj_open"
    let oHigh = ord "adj_high"
    let oLow = ord "adj_low"
    let oClose = ord "adj_close"
    let oVol = ord "adj_volume"
    let oPrev = ord "prev_adj_close"
    let oAvgVol = ord "avg_volume_4w"
    let oAvgDol = ord "avg_dollar_volume_4w"
    let oPriorIdx = ord "prior_idx"
    let oHi252 = ord "hi_252_prior"
    let oLow15 = ord "low_15_prior"
    let oAtr = ord "atr_pct_14"
    let oRange = ord "range_pct_14"
    let oTight = ord "tightness_14"
    let oPctUp = ord "pct_up"
    let oRvol = ord "rvol"
    let oEntry = ord "is_entry"
    let structOrds = structureColumns |> List.map (fun c -> c, ord c)
    // Nullable<float> from a possibly-NULL column.
    let nf (o: int) : Nullable<float> =
        if reader.IsDBNull o then Nullable() else Nullable(reader.GetDouble o)
    let rows = ResizeArray<SignalRow>()
    while reader.Read() do
        let lv = System.Collections.Generic.Dictionary<string, float>()
        for (name, o) in structOrds do
            lv.[name] <- (if reader.IsDBNull o then nan else reader.GetDouble o)
        rows.Add {
            ticker = reader.GetString oTicker
            date = DateOnly.FromDateTime(reader.GetDateTime oDate)
            adj_open = reader.GetDouble oOpen
            adj_high = reader.GetDouble oHigh
            adj_low = reader.GetDouble oLow
            adj_close = reader.GetDouble oClose
            adj_volume = reader.GetInt64 oVol
            prev_adj_close = nf oPrev
            avg_volume_4w = nf oAvgVol
            avg_dollar_volume_4w = nf oAvgDol
            prior_idx = reader.GetInt64 oPriorIdx
            hi_252_prior = nf oHi252
            low_15_prior = nf oLow15
            atr_pct_14 = nf oAtr
            range_pct_14 = nf oRange
            tightness_14 = nf oTight
            pct_up = nf oPctUp
            rvol = nf oRvol
            is_entry = (not (reader.IsDBNull oEntry)) && reader.GetBoolean oEntry
            levels = lv :> IReadOnlyDictionary<string, float>
        }
    rows.ToArray()
