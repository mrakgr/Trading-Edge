-- daily_episodes_causal — the CAUSAL twin of `daily_episodes` (S43br).
--
-- Identical episode logic (a >45-CALENDAR-day gap starts a new episode, so no
-- rolling window can span a listing gap and a recycled symbol cannot contaminate
-- its successor), but sourced from `daily_adjusted` instead of
-- `split_adjusted_prices`. It therefore carries RAW OHLCV plus the two causal
-- factors `n` and `cum_div` rather than back-adjusted prices.
--
-- ⭐ WHY A SECOND VIEW rather than changing `daily_episodes` in place: the old
-- view's consumers (build_mr_candidate.fsx, the tideflyer_*.sql family,
-- live_scan.py) all read `adj_close`/`adj_volume` and would silently change
-- meaning. Migration is staged — see docs/price_adjustment.md.
--
-- ⚠ Consumers must NOT use these columns as if they were adjusted prices. Combine
-- them per docs/price_adjustment.md §2:
--     price of day t in day D's raw scale   ->  close(t) * n(t)/n(D)
--     volume of day t in day D's share scale ->  volume(t) * n(D)/n(t)     [reciprocal —
--                                                 so price*volume is scale-invariant]
--     return t1 -> t2  ->  [ (P2*n2 - P1*n1) + (C2 - C1) ] / (P1*n1)
--
-- A VIEW, not a table: the episode assignment is one LAG plus a running SUM, so
-- it is always fresh when daily_adjusted is rebuilt and cannot go stale.
--
-- ⚠ DuckDB CACHES A VIEW'S COLUMN TYPES. If a base-table column changes type,
-- every view over it fails with "Contents of view were altered: types don't
-- match!" until re-created. Changing daily_prices.volume BIGINT -> DOUBLE broke
-- this view and its legacy twin, and the failure surfaces in the CONSUMER
-- (build_mr_candidate_1s.fsx), not here. Re-apply BOTH view files after any such
-- change: this one and build_daily_episodes_view.sql.
--
-- Apply:  via scripts/equity/data_v2.py (attach) or build_mr_candidate_1s.fsx --v2 — it needs data v2's ticker_types
--         (2026-11-28); trading.db has none (ATTACH the parquet as ticker_types first).

CREATE OR REPLACE VIEW daily_episodes_causal AS
WITH marked AS (
    SELECT
        a.ticker,
        a.date,
        a.open,
        a.high,
        a.low,
        a.close,
        a.volume,
        a.n,
        a.cum_div,
        -- a break = a >45-calendar-day gap since the prior bar for this ticker.
        -- The first bar of each ticker has a NULL LAG => is_break = 0.
        CASE WHEN a.date - LAG(a.date) OVER w > 45 THEN 1 ELSE 0 END AS is_break
    FROM daily_adjusted a
    -- ⭐ 2026-11-28: THE DATED TYPE. A ticker is in the universe on the days ticker_types (data v2) says it is a stock.
    -- Until then an undated EXISTS on ticker_reference let every once-CS symbol through on ALL its days: RAM (Aries I
    -- Acquisition, a SPAC, then since 2026-06-24 the Roundhill 2X DRAM ETF) traded in the research universe as a stock.
    -- ticker_types has one row per ticker per day (non-overlapping intervals), so the join cannot fan out.
    JOIN ticker_types t
      ON t.ticker = a.ticker AND a.date BETWEEN t.from_date AND t.to_date AND t.is_stock
    WINDOW w AS (PARTITION BY a.ticker ORDER BY a.date)
)
SELECT
    ticker,
    date,
    open,
    high,
    low,
    close,
    volume,
    n,
    cum_div,
    SUM(is_break) OVER (PARTITION BY ticker ORDER BY date) AS episode
FROM marked;
