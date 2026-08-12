-- Daily OHLCV price data
CREATE TABLE IF NOT EXISTS daily_prices (
    ticker VARCHAR NOT NULL,
    date DATE NOT NULL,
    open DOUBLE NOT NULL,
    high DOUBLE NOT NULL,
    low DOUBLE NOT NULL,
    close DOUBLE NOT NULL,
    -- ⚠ DOUBLE, not BIGINT. Polygon's flat files began reporting FRACTIONAL
    -- volume on 2026-02-23 (~85% of rows from that day on; 0% before), because
    -- some TRF prints carry fractional share counts. A BIGINT column silently
    -- FLOORED them — e.g. 2026-08-07 ticker A: 1,525,223.372108 stored as
    -- 1,525,223. Small in itself, but it is the same class of defect that made
    -- split_adjusted_prices.adj_volume truncate to 0 and silently delete 436
    -- universe ticker-days (see docs/price_adjustment.md §8).
    volume DOUBLE NOT NULL,
    transactions BIGINT NOT NULL,
    PRIMARY KEY(ticker, date)
);

-- Index for efficient queries by ticker
CREATE INDEX IF NOT EXISTS idx_daily_prices_ticker ON daily_prices(ticker);

-- Index for efficient queries by date
CREATE INDEX IF NOT EXISTS idx_daily_prices_date ON daily_prices(date);

-- Index for efficient queries by ticker and date range
CREATE INDEX IF NOT EXISTS idx_daily_prices_ticker_date ON daily_prices(ticker, date);
