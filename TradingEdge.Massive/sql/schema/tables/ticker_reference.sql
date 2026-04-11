-- Reference data for ticker classification (ETF, ETN, etc.)
-- Sourced from Polygon /v3/reference/tickers
CREATE TABLE IF NOT EXISTS ticker_reference (
    ticker VARCHAR NOT NULL,
    name VARCHAR,
    type VARCHAR NOT NULL,
    PRIMARY KEY(ticker, type)
);

-- Index for type-based lookups (e.g. "all ETFs")
CREATE INDEX IF NOT EXISTS idx_ticker_reference_type ON ticker_reference(type);
