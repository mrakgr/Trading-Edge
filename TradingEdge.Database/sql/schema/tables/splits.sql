-- Stock split information.
--
-- ⭐ PRIMARY KEY IS POLYGON'S OWN RECORD `id`, NOT (ticker, execution_date).
-- A ticker can legitimately have MORE THAN ONE split on a single execution_date:
-- a reverse/forward PAIR is the standard odd-lot squeeze-out used to force out
-- small holders before deregistering, and it nets to NO change in share count.
-- Polygon returns both legs with distinct ids, e.g. TTSH 2025-12-16:
--     E003ef6d...  split_from=3000 split_to=1     (1-for-3000 reverse)
--     E60b443f...  split_from=1    split_to=3000  (3000-for-1 forward)
-- The old key plus `ON CONFLICT(ticker, execution_date) DO UPDATE` kept only the
-- LAST leg, leaving a naked 1:3000 that multiplied every later price by 3000.
-- Found 2026-08-12 while validating daily_adjusted: 7 such pairs, worst 5000x
-- (PMD), and the same class corrupted split_adjusted_prices in the other
-- direction (it divided TTSH's ENTIRE prior history down to $0.002147).
-- See docs/price_adjustment.md.
--
-- ⚠ Consumers must therefore aggregate by (ticker, execution_date) — take the
-- PRODUCT of split_ratio — before building any cumulative factor. A window
-- function ordered by execution_date alone yields one row per leg and picks an
-- arbitrary one. `03_daily_adjusted.sql` does this in its splits_by_date CTE.
CREATE TABLE IF NOT EXISTS splits (
    id VARCHAR NOT NULL,
    ticker VARCHAR NOT NULL,
    execution_date DATE NOT NULL,
    split_from DOUBLE NOT NULL,
    split_to DOUBLE NOT NULL,
    split_ratio DOUBLE NOT NULL,
    PRIMARY KEY(id)
);

-- The natural query path (all splits for a ticker, in date order) is no longer
-- the primary key, so it needs its own index.
CREATE INDEX IF NOT EXISTS idx_splits_ticker_date ON splits(ticker, execution_date);

-- Index for efficient queries by execution date
CREATE INDEX IF NOT EXISTS idx_splits_execution_date ON splits(execution_date);
