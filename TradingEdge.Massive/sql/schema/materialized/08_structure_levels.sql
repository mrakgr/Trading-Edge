-- 08_structure_levels.sql — momentum-structure price levels, one row per (ticker,date).
--
-- 66 columns = 11 lookback periods (52w/26w/13w/8w/4w/2w + 5/4/3/2/1 day)
--              × 6 level kinds (trail close, MA, hi/lo channel, hi/lo CLOSE channel).
-- Every window EXCLUDES the current bar via `... AND 1 PRECEDING` (point-in-time, no lookahead).
-- Column names/order match TradingEdge.MomentumBacktest Types.structureColumns exactly
-- (kind-major). The momentum backtest JOINs this table instead of recomputing 66 window
-- aggregates per run (Signals.fs). ~48.7M rows, ~8 GB.
--
-- REBUILD: this file is an embedded resource (TradingEdge.Massive.fsproj embeds
-- sql/schema/materialized/*.sql) and runs automatically as part of `ingest-data`
-- (Database.materializeTables), in filename order — so it rebuilds after
-- 01_split_adjusted_prices whenever new data is ingested. Build time ~40s. To run
-- it standalone: `duckdb data/trading.db < <this file>`.
--
-- COLUMN LIST is the cross-product structurePeriods × levelKinds defined in
-- TradingEdge.MomentumBacktest/Types.fs (the single source of truth — column
-- names/order must match Types.structureColumns). If those defs change, regenerate
-- the SELECT body below from the same lists.

CREATE OR REPLACE TABLE structure_levels AS
SELECT p.ticker, p.date,
    LAG(p.adj_close, 252) OVER w AS trail_52w,
    LAG(p.adj_close, 126) OVER w AS trail_26w,
    LAG(p.adj_close, 63) OVER w AS trail_13w,
    LAG(p.adj_close, 40) OVER w AS trail_8w,
    LAG(p.adj_close, 20) OVER w AS trail_4w,
    LAG(p.adj_close, 10) OVER w AS trail_2w,
    LAG(p.adj_close, 5) OVER w AS trail_5d,
    LAG(p.adj_close, 4) OVER w AS trail_4d,
    LAG(p.adj_close, 3) OVER w AS trail_3d,
    LAG(p.adj_close, 2) OVER w AS trail_2d,
    LAG(p.adj_close, 1) OVER w AS trail_1d,
    AVG(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 252 PRECEDING AND 1 PRECEDING) AS ma_52w,
    AVG(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 126 PRECEDING AND 1 PRECEDING) AS ma_26w,
    AVG(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 63 PRECEDING AND 1 PRECEDING) AS ma_13w,
    AVG(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 40 PRECEDING AND 1 PRECEDING) AS ma_8w,
    AVG(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS ma_4w,
    AVG(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 10 PRECEDING AND 1 PRECEDING) AS ma_2w,
    AVG(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 5 PRECEDING AND 1 PRECEDING) AS ma_5d,
    AVG(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 4 PRECEDING AND 1 PRECEDING) AS ma_4d,
    AVG(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 3 PRECEDING AND 1 PRECEDING) AS ma_3d,
    AVG(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 2 PRECEDING AND 1 PRECEDING) AS ma_2d,
    AVG(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 1 PRECEDING AND 1 PRECEDING) AS ma_1d,
    MAX(p.adj_high)  OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 252 PRECEDING AND 1 PRECEDING) AS hi_52w,
    MAX(p.adj_high)  OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 126 PRECEDING AND 1 PRECEDING) AS hi_26w,
    MAX(p.adj_high)  OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 63 PRECEDING AND 1 PRECEDING) AS hi_13w,
    MAX(p.adj_high)  OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 40 PRECEDING AND 1 PRECEDING) AS hi_8w,
    MAX(p.adj_high)  OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS hi_4w,
    MAX(p.adj_high)  OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 10 PRECEDING AND 1 PRECEDING) AS hi_2w,
    MAX(p.adj_high)  OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 5 PRECEDING AND 1 PRECEDING) AS hi_5d,
    MAX(p.adj_high)  OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 4 PRECEDING AND 1 PRECEDING) AS hi_4d,
    MAX(p.adj_high)  OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 3 PRECEDING AND 1 PRECEDING) AS hi_3d,
    MAX(p.adj_high)  OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 2 PRECEDING AND 1 PRECEDING) AS hi_2d,
    MAX(p.adj_high)  OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 1 PRECEDING AND 1 PRECEDING) AS hi_1d,
    MIN(p.adj_low)   OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 252 PRECEDING AND 1 PRECEDING) AS lo_52w,
    MIN(p.adj_low)   OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 126 PRECEDING AND 1 PRECEDING) AS lo_26w,
    MIN(p.adj_low)   OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 63 PRECEDING AND 1 PRECEDING) AS lo_13w,
    MIN(p.adj_low)   OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 40 PRECEDING AND 1 PRECEDING) AS lo_8w,
    MIN(p.adj_low)   OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS lo_4w,
    MIN(p.adj_low)   OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 10 PRECEDING AND 1 PRECEDING) AS lo_2w,
    MIN(p.adj_low)   OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 5 PRECEDING AND 1 PRECEDING) AS lo_5d,
    MIN(p.adj_low)   OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 4 PRECEDING AND 1 PRECEDING) AS lo_4d,
    MIN(p.adj_low)   OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 3 PRECEDING AND 1 PRECEDING) AS lo_3d,
    MIN(p.adj_low)   OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 2 PRECEDING AND 1 PRECEDING) AS lo_2d,
    MIN(p.adj_low)   OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 1 PRECEDING AND 1 PRECEDING) AS lo_1d,
    MAX(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 252 PRECEDING AND 1 PRECEDING) AS hiclose_52w,
    MAX(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 126 PRECEDING AND 1 PRECEDING) AS hiclose_26w,
    MAX(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 63 PRECEDING AND 1 PRECEDING) AS hiclose_13w,
    MAX(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 40 PRECEDING AND 1 PRECEDING) AS hiclose_8w,
    MAX(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS hiclose_4w,
    MAX(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 10 PRECEDING AND 1 PRECEDING) AS hiclose_2w,
    MAX(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 5 PRECEDING AND 1 PRECEDING) AS hiclose_5d,
    MAX(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 4 PRECEDING AND 1 PRECEDING) AS hiclose_4d,
    MAX(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 3 PRECEDING AND 1 PRECEDING) AS hiclose_3d,
    MAX(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 2 PRECEDING AND 1 PRECEDING) AS hiclose_2d,
    MAX(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 1 PRECEDING AND 1 PRECEDING) AS hiclose_1d,
    MIN(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 252 PRECEDING AND 1 PRECEDING) AS loclose_52w,
    MIN(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 126 PRECEDING AND 1 PRECEDING) AS loclose_26w,
    MIN(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 63 PRECEDING AND 1 PRECEDING) AS loclose_13w,
    MIN(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 40 PRECEDING AND 1 PRECEDING) AS loclose_8w,
    MIN(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS loclose_4w,
    MIN(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 10 PRECEDING AND 1 PRECEDING) AS loclose_2w,
    MIN(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 5 PRECEDING AND 1 PRECEDING) AS loclose_5d,
    MIN(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 4 PRECEDING AND 1 PRECEDING) AS loclose_4d,
    MIN(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 3 PRECEDING AND 1 PRECEDING) AS loclose_3d,
    MIN(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 2 PRECEDING AND 1 PRECEDING) AS loclose_2d,
    MIN(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date ROWS BETWEEN 1 PRECEDING AND 1 PRECEDING) AS loclose_1d
FROM split_adjusted_prices p
WINDOW w AS (PARTITION BY p.ticker ORDER BY p.date);
