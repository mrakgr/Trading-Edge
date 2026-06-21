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
--   NTEST.*, …): synthetic symbols with corrupt prices (0.0001 -> $200,000 = a
--   +200-billion-% one-day return) that slip into the price table with NO ref row,
--   so neither the type nor the $1M liquidity filter catches them.
--   WHAT DOESN'T WORK (verified 2026-06-20):
--     - Polygon type='OTHER': the download-tickers run returned 0 OTHER rows —
--       Polygon does NOT carry these test tickers in its reference master AT ALL
--       (a direct /v3/reference/tickers?ticker=ZXZZT query returns empty). So a
--       reference join can't tag them; that clause is inert and removed.
--     - a high-price rule (close > $50k): LEAKY both ways — catches only ~14/135 of
--       the egregious corruptions (split-adjustment rescales the spike) AND wrongly
--       hits ~53 REAL reverse-split micro-caps (HCTI, CDT, RSLS...) whose adjusted
--       history balloons past $50k. Rejected.
--   WHAT WORKS: a HARDCODED list of the published exchange test symbols (below).
--   It's a fixed, finite, published set (NASDAQ Z?ZZT/ZYxxx, NYSE NTEST.*, etc.) —
--   enumerated exhaustively from our own data via the synthetic signature — so a
--   literal list is the simplest and most robust option; it does not grow.
--   The +1000% return clip in the HEAT builder stays as the catch-all backstop for
--   residual ratio-glitches in REAL tickers (LU, EPIX — legit ADRC/CS, one corrupt
--   split-adjusted day each). Keep BOTH: blocklist removes synthetic NAMES from the
--   universes (incl. breadth, where they'd silently count as "stocks"); clip caps
--   the residual return outliers.
-- =============================================================================

-- HARDCODED test-ticker blocklist. These are the published exchange test symbols
-- (NASDAQ Z?ZZT/ZYxxx Tier-1/2/3 test series, NYSE/Arca NTEST.*, plus AAZST/CGZST/
-- YJZST/ZVV). It is the EXHAUSTIVE set found in split_adjusted_prices via the
-- synthetic signature (>1000% move AND (no CS/ADRC ref OR an impossible >$50k
-- adjusted price)). It's a fixed published set — does not grow — so a literal list
-- is simpler and more robust than any pattern/reference join (both of which proved
-- leaky: Polygon doesn't list these at all; a bare price/name rule false-positives
-- on real reverse-split micro-caps / "inTEST"/"Whitestone").
-- NOTE: the real ticker `Z` (Zillow) is DELIBERATELY EXCLUDED from this list — it
-- is a legitimate stock with one corrupt $200k row; the +1000% return clip handles
-- that single glitch, and we must not blanket-block real Zillow.
CREATE OR REPLACE TEMP MACRO is_test_ticker(tk) AS (
  tk IN (
    'ZAZZT','ZBZZT','ZCZZT','ZJZZT','ZVZZT','ZWZZT','ZXZZT','ZYZZT',  -- NASDAQ Z?ZZT test series
    'ZYYZZ','ZYSZZ','ZYSTF',                                         -- NASDAQ ZYxxx test symbols
    'NTEST.A','NTEST.B','NTEST.C','NTEST.G',                          -- NYSE/Arca test symbols
    'AAZST','CGZST','YJZST','ZVV'                                     -- other exchange test symbols
  )
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
--           lagged mean (h10). Gate: skip entries when h10 >= 0.25 (80th pctile).
--    Universe: CS/ADRC + 30-cal-day ADV >= $1M.
--    ** CHANGED 2026-06-21: heat now restricts to CS/ADRC (INNER JOIN), unified with
--       breadth + the engine. Was previously the whole liquid tape + a hardcoded
--       test-ticker blocklist. Reasons: (a) the ref-less names the whole-tape version
--       admitted are mostly closed-end funds + preferreds (NOT warrants/IPOs/foreign —
--       IPOs are CS, foreign are ADRC, and both DO get a ref row), which structurally
--       can't be top-1% gainers and only DILUTE the froth mean; (b) the CS/ADRC inner
--       join drops the test tickers for free (they have NO ticker_reference row), so
--       the is_test_ticker blocklist is no longer needed here. The two heat series are
--       0.81-correlated but the CS/ADRC version gates STRONGER: at h10<0.25 clip-post
--       1.686 vs the old 1.620; at h10<0.20 1.819 vs 1.722. New 80th pctile = 0.251
--       (~= the old 0.25, so the gate threshold is unchanged).
--    ** The +1000% per-stock return CLIP is STILL LOAD-BEARING ** — split_adjusted_prices
--       has rare corrupted rows in REAL CS/ADRC tickers (LU, EPIX: one corrupt split-
--       adjusted day each) that, as a mean of the top tail, destroy a day's value. Keep it.
-- -----------------------------------------------------------------------------
COPY (
  WITH r AS (
    SELECT p.ticker, p.date,
      p.adj_close / LAG(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date) - 1.0  AS ret,
      AVG(p.adj_close*p.adj_volume) OVER (PARTITION BY p.ticker ORDER BY p.date
                             RANGE BETWEEN INTERVAL 30 DAYS PRECEDING AND CURRENT ROW) AS adv30
    FROM split_adjusted_prices p
    JOIN ticker_reference tr ON tr.ticker = p.ticker     -- INNER join => test tickers (no ref row) dropped for free
    WHERE p.adj_close > 0 AND tr.type IN ('CS','ADRC')   -- CS/ADRC only (unified with breadth + engine)
  ),
  q AS (
    SELECT date, ret
    FROM r
    WHERE adv30 >= 1000000               -- 30-cal-day ADV >= $1M
      AND ret IS NOT NULL
      AND ret <= 10.0                    -- *** load-bearing +1000% clip (residual REAL-ticker glitches) ***
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
