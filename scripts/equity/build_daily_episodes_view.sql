-- daily_episodes — the single source of truth for gap-episode partitioning.
--
-- A recycled ticker (same symbol reused by a NEW company after a multi-year
-- listing gap, e.g. MRX = old co. through 2012, then Marex from 2024) must not
-- let any rolling window span the gap. This view assigns a running `episode` id
-- that increments at every >45-CALENDAR-day gap between consecutive bars, so
-- consumers can `PARTITION BY ticker, episode` on their rolling windows and no
-- window can reach across a listing gap. It is the SQL equivalent of the F#
-- engine resetting its rolling state on a detected gap (TradingEdge.HighFlyerV2
-- RollingMa.Reset / HighFlyer.ResetIndicators).
--
-- This centralizes the episode logic that was duplicated in scripts/equity/
-- live_scan.py (and would otherwise be re-copied into build_mr_candidate.fsx).
-- Both now import `episode` from here instead of re-deriving it.
--
-- Scope: the CS/ADRC universe only (common stock + ADRs), the FULL history (no
-- date floor — each consumer applies its own date window). Carries the adjusted
-- OHLCV plus raw_close (from daily_prices) so consumers get adj_ratio =
-- adj_close/raw_close without re-joining. A VIEW, not a table: the episode
-- assignment is cheap (one LAG + one running SUM), always fresh when
-- split_adjusted_prices updates, and stale-table-proof.
--
-- Apply:  duckdb data/trading.db < scripts/equity/build_daily_episodes_view.sql
--   (or run the same CREATE OR REPLACE from any DuckDB connection to trading.db)

CREATE OR REPLACE VIEW daily_episodes AS
WITH marked AS (
    SELECT
        p.ticker,
        p.date,
        p.adj_open,
        p.adj_high,
        p.adj_low,
        p.adj_close,
        p.adj_volume,
        d.close AS raw_close,   -- for adj_ratio = adj_close / raw_close (rescale intraday to adjusted)
        -- a break = a >45-calendar-day gap since the prior bar for this ticker.
        -- GAP_DAYS = 45 (>1 month of no trading = a new listing). The first bar of
        -- each ticker has a NULL LAG => is_break = 0 (no gap before the first bar).
        CASE WHEN p.date - LAG(p.date) OVER w > 45 THEN 1 ELSE 0 END AS is_break
    FROM split_adjusted_prices p
    JOIN daily_prices d ON d.ticker = p.ticker AND d.date = p.date
    WHERE EXISTS (SELECT 1 FROM ticker_reference r
                  WHERE r.ticker = p.ticker AND r.type IN ('CS','ADRC'))
    WINDOW w AS (PARTITION BY p.ticker ORDER BY p.date)
)
SELECT
    ticker,
    date,
    adj_open,
    adj_high,
    adj_low,
    adj_close,
    adj_volume,
    raw_close,
    -- running episode id: cumulative count of breaks (0 for a ticker's first listing,
    -- 1 after its first >45d gap, etc). PARTITION BY (ticker, episode) downstream.
    SUM(is_break) OVER (PARTITION BY ticker ORDER BY date) AS episode
FROM marked;
