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
    cash_amount DOUBLE NOT NULL,   -- ⭐ in USD, the trading currency (converted at ingest since 2026-10-21; see below)
    declaration_date DATE,
    pay_date DATE,
    frequency INTEGER NOT NULL,
    dividend_type VARCHAR NOT NULL,
    currency VARCHAR,          -- ISO 4217 the vendor DECLARED it in; NOT always USD (see below). NULL = the vendor omitted it.
    cash_amount_orig DOUBLE,   -- the vendor's amount, in `currency`
    fx_rate DOUBLE,            -- USD per 1 unit of `currency` (1.0 for USD); cash_amount = cash_amount_orig * fx_rate
    PRIMARY KEY(id)
);

-- 2026-10-21: `currency`, `cash_amount_orig`, `fx_rate`. The vendor quotes each dividend in the currency it was
-- declared in: US listings of Canadian/European issuers (RY, TD, ENB, TTE, RACE, ...) trade in USD but declare in
-- CAD/EUR — 2,713 records on 165 tape tickers. The ingest (Database.dividendsSource) converts at the ECB reference rate
-- of the ex-date, so cash_amount is USD and daily_adjusted needs no change. A row the ECB has no rate for is kept OUT
-- of this table (it stays in dividends.csv).
-- ⭐ The ingest DROPS and recreates this table from this file every run (Database.replaceFromCsv), so a new column
-- needs no migration: change the definition here and in Database.dividendsSource.

-- The natural query path is no longer the primary key, so it needs its own index.
CREATE INDEX IF NOT EXISTS idx_dividends_ticker_date ON dividends(ticker, ex_dividend_date);

CREATE INDEX IF NOT EXISTS idx_dividends_ex_date ON dividends(ex_dividend_date);
