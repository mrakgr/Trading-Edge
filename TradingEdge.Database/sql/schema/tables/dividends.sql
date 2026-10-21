-- Dividend information.
--
-- ⭐ PRIMARY KEY IS POLYGON'S OWN RECORD `id`, NOT (ticker, ex_dividend_date).
-- One ex-date routinely carries SEVERAL distinct payments:
--   * a regular dividend plus a special (very common in Dec 2012 ahead of the
--     2013 tax change — e.g. AAON 2012-11-29 paid two $0.12 distributions);
--   * ADRs paying multiple components, e.g. ABEV 2025-12-22 = [0.0145, 0.0833]
--     (interest-on-capital alongside the ordinary dividend).
-- The old key plus `ON CONFLICT(ticker, ex_dividend_date) DO UPDATE` kept only
-- the LAST record. Measured 2026-08-12 on a 250-ticker sample of the FlushFader
-- universe: 155 of 10,154 records lost (1.5%) across 114 ex-dates, dropping at
-- least $22.83/share of cash. Every one of those understates a dividend-adjusted
-- return. See docs/price_adjustment.md.
--
-- ⚠ Consumers must aggregate by (ticker, ex_dividend_date) — take the SUM of
-- cash_amount — before building any cumulative series.
CREATE TABLE IF NOT EXISTS dividends (
    id VARCHAR NOT NULL,
    ticker VARCHAR NOT NULL,
    ex_dividend_date DATE NOT NULL,
    cash_amount DOUBLE NOT NULL,
    declaration_date DATE,
    pay_date DATE,
    frequency INTEGER NOT NULL,
    dividend_type VARCHAR NOT NULL,
    currency VARCHAR,          -- ISO 4217 of cash_amount; NOT always USD (see below). NULL = the vendor omitted it.
    PRIMARY KEY(id)
);

-- 2026-10-21: `currency` added. A database created before then gets the column here (CREATE TABLE IF NOT EXISTS
-- leaves an existing table alone); the next ingest fills it. cash_amount is in THIS currency: US listings of
-- Canadian/European issuers (RY, TD, ENB, TTE, RACE, ...) trade in USD but declare in CAD/EUR — 278 of 70,324
-- tape-ticker records in 2025-26. Consumers that sum cash into a USD series must convert or exclude non-USD rows.
ALTER TABLE dividends ADD COLUMN IF NOT EXISTS currency VARCHAR;

-- The natural query path is no longer the primary key, so it needs its own index.
CREATE INDEX IF NOT EXISTS idx_dividends_ticker_date ON dividends(ticker, ex_dividend_date);

CREATE INDEX IF NOT EXISTS idx_dividends_ex_date ON dividends(ex_dividend_date);
