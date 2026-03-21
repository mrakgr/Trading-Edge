-- Dividend information
CREATE TABLE IF NOT EXISTS dividends (
    ticker VARCHAR NOT NULL,
    ex_dividend_date DATE NOT NULL,
    cash_amount DOUBLE NOT NULL,
    declaration_date DATE,
    pay_date DATE,
    frequency INTEGER NOT NULL,
    dividend_type VARCHAR NOT NULL,
    PRIMARY KEY(ticker, ex_dividend_date)
);

CREATE INDEX IF NOT EXISTS idx_dividends_ticker ON dividends(ticker);
CREATE INDEX IF NOT EXISTS idx_dividends_ex_date ON dividends(ex_dividend_date);
