-- =============================================================================
-- Momentum V2 regime filters — BREADTH and HEAT builders (2026-06-20)
-- =============================================================================
-- DOCS: explained in docs/momentum_v2_results.md — sections
--   "Breadth (pct_above_20) cumulative floor ..." and
--   "Top-gainer HEAT ..." (rationale, sweeps, thresholds, era splits).
-- =============================================================================
-- Run against the equities DB:
--   duckdb -readonly data/trading.db < scripts/equity/build_breadth_and_heat.sql
-- (uses COPY ... TO to write parquets; if data/trading.db must stay read-only,
--  ATTACH it read-only into an in-memory db instead — see note at the bottom.)
--
-- SHARED UNIVERSE CONVENTION (both filters):
--   * 30-CALENDAR-DAY average dollar volume >= $1,000,000
--       adv30 = AVG(adj_close*adj_volume) over a RANGE of INTERVAL 30 DAYS.
--       30-cal-day ADV is the project-standard `avg_dollar_volume_4w` window;
--       the bar is $1M (decided 2026-06-20, applies to BOTH filters).
--       NOTE: use 30-cal-day ADV, NOT same-day dollar volume.
--   * BREADTH additionally restricts to common stock + ADRs:
--       ticker_reference.type IN ('CS','ADRC')   (NOT ETF/ETN/funds — ~43% of
--       the raw price table is non-CS/ADRC and must be excluded from breadth).
--     (The HEAT universe does NOT apply the CS/ADRC filter — top-gainer "heat"
--      reads the whole liquid tape; CS/ADRC-only changes it negligibly.)
--
-- NOTE: this $1M build REPRODUCES the committed
--   data/equity/momentum_v0/breadth.parquet (the v0 builder was not kept, but
--   its per-day `n` and the floor-sweep PFs match this $1M+CS/ADRC definition to
--   ~1-2%). So breadth_1m.parquet here == the production breadth, by construction.
--
-- TEST-TICKER BLOCKLIST (NASDAQ test securities — ZXZZT, ZWZZT, ZJZZT, AAZST,
--   NTEST.*, …): these are synthetic symbols with corrupt prices (0.0001 ->
--   $200,000 = a +200-billion-% one-day return) that slip into the price table
--   tagged CS or with no ref row, so neither the type nor the $1M liquidity
--   filter catches them. SOURCE OF TRUTH: Polygon files them under type='OTHER'
--   (and their `name` contains "test"). Fetch them into ticker_reference with
--     dotnet run --project TradingEdge.Massive -- download-tickers
--   (TickersDownload.fs now includes "OTHER" in tickerTypes). Then both builders
--   below exclude them via the `is_test_ticker` predicate. Until that download is
--   run, the predicate matches nothing (OTHER rows absent) and the +1000% return
--   clip in the HEAT builder remains the safety net. Keep BOTH: the blocklist
--   removes synthetic NAMES from the universes (incl. breadth, where they'd count
--   as "stocks"); the clip still handles residual ratio-glitches in REAL tickers
--   (e.g. LU, EPIX — legit ADRC/CS with one corrupt split-adjusted day).
-- =============================================================================

-- Reusable test-ticker predicate: TRUE if `tk` is a Polygon test security.
-- (type='OTHER' is the authoritative tag; the name LIKE '%test%' and the known
--  Z*ZZT / NTEST pattern are belt-and-suspenders for any that pre-date the OTHER
--  fetch or lack a ref row.)
CREATE OR REPLACE TEMP MACRO is_test_ticker(tk) AS (
  tk IN (SELECT ticker FROM ticker_reference
         WHERE type = 'OTHER' OR LOWER(name) LIKE '%test%')
  OR tk SIMILAR TO 'Z[A-Z]ZZT'      -- ZXZZT, ZWZZT, ZJZZT, ZVZZT, ZAZZT, ZBZZT, ZCZZT...
  OR tk LIKE 'NTEST%'               -- NTEST.A / NTEST.G ...
  OR tk IN ('AAZST','CGZST','ZYSTF','ZYYZZ','ZYSZZ','YJZST','ZVV')
);


-- -----------------------------------------------------------------------------
-- 1) BREADTH:  pct_above_20 = fraction of the daily universe with close > 20d SMA
--    Universe: CS/ADRC + 30-cal-day ADV >= $1M. Lag 1 day at USE time (the
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
      AND adv30 >= 1000000               -- 30-cal-day ADV >= $1M
      AND NOT is_test_ticker(ticker)     -- block NASDAQ test securities
  )
  SELECT date,
    COUNT(*)                                            AS n,
    AVG(CASE WHEN adj_close > ma20 THEN 1.0 ELSE 0.0 END) AS pct_above_20
  FROM univ
  GROUP BY date ORDER BY date
) TO 'data/equity/momentum_v0/breadth_1m.parquet' (FORMAT parquet);


-- -----------------------------------------------------------------------------
-- 2) HEAT:  daily mean return of the TOP 1% of gainers, then a trailing-10d
--           lagged mean (h10). Gate: skip entries when h10 >= 0.25 (80th pctile, $1M universe).
--    Universe: 30-cal-day ADV >= $1M (NO CS/ADRC restriction).
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
    WHERE adv30 >= 1000000               -- 30-cal-day ADV >= $1M
      AND NOT is_test_ticker(ticker)     -- block NASDAQ test securities
      AND ret IS NOT NULL
      AND ret <= 10.0                    -- *** load-bearing +1000% clip (still needed for
                                         --     residual ratio-glitches in REAL tickers) ***
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
--   breadth gate:  JOIN on entry_date, keep  LAG(pct_above_20) > 0.50  (peak PF at ~0.70 on $1M)
--   heat gate:     JOIN on entry_date, keep  h10 < 0.25                 (h10 already lagged)
--
-- If data/trading.db must remain read-only (can't COPY out of an attached RO db),
-- run instead from an in-memory duckdb:
--   duckdb :memory: -c "ATTACH 'data/trading.db' AS db (READ_ONLY); <paste the two
--   COPY blocks, prefixing split_adjusted_prices/ticker_reference with db.>"
-- =============================================================================
