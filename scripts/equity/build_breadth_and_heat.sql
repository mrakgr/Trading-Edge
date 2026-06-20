-- =============================================================================
-- Momentum V2 regime filters — BREADTH and HEAT builders (2026-06-20)
-- =============================================================================
-- Run against the equities DB:
--   duckdb -readonly data/trading.db < scripts/equity/build_breadth_and_heat.sql
-- (uses COPY ... TO to write parquets; if data/trading.db must stay read-only,
--  ATTACH it read-only into an in-memory db instead — see note at the bottom.)
--
-- SHARED UNIVERSE CONVENTION (both filters):
--   * 30-CALENDAR-DAY average dollar volume >= $100,000
--       adv30 = AVG(adj_close*adj_volume) over a RANGE of INTERVAL 30 DAYS.
--       This is the project-standard `avg_dollar_volume_4w` liquidity bar.
--       NOTE: same-day dollar volume and a $1M bar are BOTH wrong for our
--       standard — we standardized on 30-cal-day ADV >= $100k for both filters.
--   * BREADTH additionally restricts to common stock + ADRs:
--       ticker_reference.type IN ('CS','ADRC')   (NOT ETF/ETN/funds — ~43% of
--       the raw price table is non-CS/ADRC and must be excluded from breadth).
--     (The HEAT universe does NOT apply the CS/ADRC filter — top-gainer "heat"
--      reads the whole liquid tape; CS/ADRC-only changes it negligibly.)
--
-- NOTE ON THE PRODUCTION breadth.parquet: the committed
--   data/equity/momentum_v0/breadth.parquet was built (v0, builder not kept)
--   with a ~$1M dollar-volume bar (matched its per-day `n`). The $100k build
--   below is the standardized definition going forward.
-- =============================================================================


-- -----------------------------------------------------------------------------
-- 1) BREADTH:  pct_above_20 = fraction of the daily universe with close > 20d SMA
--    Universe: CS/ADRC + 30-cal-day ADV >= $100k. Lag 1 day at USE time (the
--    consumer does LAG(pct_above_20) — the parquet itself is un-lagged).
-- -----------------------------------------------------------------------------
COPY (
  WITH base AS (
    SELECT s.ticker, s.date, s.adj_close,
      AVG(s.adj_close) OVER (PARTITION BY s.ticker ORDER BY s.date
                             ROWS BETWEEN 19 PRECEDING AND CURRENT ROW)            AS ma20,
      AVG(s.adj_close*s.adj_volume) OVER (PARTITION BY s.ticker ORDER BY s.date
                             RANGE BETWEEN INTERVAL 30 DAYS PRECEDING AND CURRENT ROW) AS adv30,
      ROW_NUMBER() OVER (PARTITION BY s.ticker ORDER BY s.date)                    AS rn,
      r.type
    FROM split_adjusted_prices s
    LEFT JOIN ticker_reference r ON r.ticker = s.ticker
    WHERE s.adj_close > 0
  ),
  univ AS (
    SELECT * FROM base
    WHERE rn >= 20                       -- need a full 20d MA window
      AND type IN ('CS','ADRC')          -- common stock + ADRs only
      AND adv30 >= 100000                -- 30-cal-day ADV >= $100k
  )
  SELECT date,
    COUNT(*)                                            AS n,
    AVG(CASE WHEN adj_close > ma20 THEN 1.0 ELSE 0.0 END) AS pct_above_20
  FROM univ
  GROUP BY date ORDER BY date
) TO 'data/equity/momentum_v0/breadth_100k.parquet' (FORMAT parquet);


-- -----------------------------------------------------------------------------
-- 2) HEAT:  daily mean return of the TOP 1% of gainers, then a trailing-10d
--           lagged mean (h10). Gate: skip entries when h10 >= 0.24 (80th pctile).
--    Universe: 30-cal-day ADV >= $100k (NO CS/ADRC restriction).
--    ** The +1000% per-stock return CLIP is LOAD-BEARING ** — split_adjusted_prices
--       has rare corrupted rows (~3e9% returns) that, as a mean of the top tail,
--       destroy a whole day's value. Do not remove it.
-- -----------------------------------------------------------------------------
COPY (
  WITH r AS (
    SELECT ticker, date,
      adj_close / LAG(adj_close) OVER (PARTITION BY ticker ORDER BY date) - 1.0    AS ret,
      AVG(adj_close*adj_volume) OVER (PARTITION BY ticker ORDER BY date
                             RANGE BETWEEN INTERVAL 30 DAYS PRECEDING AND CURRENT ROW) AS adv30
    FROM split_adjusted_prices
    WHERE adj_close > 0
  ),
  q AS (
    SELECT date, ret
    FROM r
    WHERE adv30 >= 100000                -- 30-cal-day ADV >= $100k
      AND ret IS NOT NULL
      AND ret <= 10.0                    -- *** load-bearing +1000% clip ***
  ),
  ranked AS (
    SELECT date, ret, PERCENT_RANK() OVER (PARTITION BY date ORDER BY ret) AS pr
    FROM q
  ),
  daily AS (                              -- daily heat = mean of the top 1%
    SELECT date, AVG(ret) AS heat
    FROM ranked WHERE pr >= 0.99
    GROUP BY date
  )
  SELECT date, heat,
    -- h10: trailing 10d mean, LAGGED 1 day (1 PRECEDING boundary = no lookahead).
    AVG(heat) OVER (ORDER BY date ROWS BETWEEN 10 PRECEDING AND 1 PRECEDING)       AS h10
  FROM daily ORDER BY date
) TO 'data/equity/momentum_v0/heat.parquet' (FORMAT parquet);


-- -----------------------------------------------------------------------------
-- USAGE (post-hoc, as-of the prior close — no lookahead):
--   breadth gate:  JOIN on entry_date, keep  LAG(pct_above_20) > 0.50  (peak ~0.65 on $100k)
--   heat gate:     JOIN on entry_date, keep  h10 < 0.24                 (h10 already lagged)
--
-- If data/trading.db must remain read-only (can't COPY out of an attached RO db),
-- run instead from an in-memory duckdb:
--   duckdb :memory: -c "ATTACH 'data/trading.db' AS db (READ_ONLY); <paste the two
--   COPY blocks, prefixing split_adjusted_prices/ticker_reference with db.>"
-- =============================================================================
