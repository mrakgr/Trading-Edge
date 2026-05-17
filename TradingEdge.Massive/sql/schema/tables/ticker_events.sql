-- Ticker rename / corporate-event history sourced from Polygon's
-- /vX/reference/tickers/{ticker}/events endpoint.
--
-- One row per event. `query_ticker` is the ticker the API was queried under
-- (i.e., the JSON filename stem); `figi`/`name`/`cik` are the security-level
-- fields that span the whole chain; `event_ticker` is the ticker assigned by
-- that event (`ticker_change` events are the only type Polygon currently emits).
--
-- Loaded from data/tickers/events.parquet, which is itself rebuilt from the
-- per-ticker JSONs at data/tickers/events/{ticker}.json. The DB is transient;
-- the JSONs are the durable copy.
CREATE TABLE IF NOT EXISTS ticker_events (
    query_ticker  VARCHAR NOT NULL,
    figi          VARCHAR,
    name          VARCHAR,
    cik           VARCHAR,
    event_date    DATE NOT NULL,
    event_type    VARCHAR NOT NULL,
    event_ticker  VARCHAR
);

CREATE INDEX IF NOT EXISTS idx_ticker_events_query   ON ticker_events(query_ticker);
CREATE INDEX IF NOT EXISTS idx_ticker_events_figi    ON ticker_events(figi);
CREATE INDEX IF NOT EXISTS idx_ticker_events_ticker  ON ticker_events(event_ticker);
