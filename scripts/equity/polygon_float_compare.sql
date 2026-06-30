-- Compare the float factor under THREE sources on the same trade book:
--   1. SEC free-float (dei:EntityPublicFloat, the current 81%-covered scrape)
--   2. Polygon scso x adj_close_at_entry (the new ?date= shares-out, ~95% covered)
--   3. coverage of each
--
-- The factor under test: dollar-float < $300M at entry => stronger PF (the
-- [[project_float_feature_2026-06-22]] finding, PF 2.47 vs 1.36). Question: does
-- the Polygon metric reproduce/sharpen that edge with its better coverage?
--
-- Polygon scso is ALREADY split-correct at the entry date (?date= verified), so the
-- dollar-float is just scso * adj_close_at_entry -- NO period_end re-scaling (the SEC
-- path needs it; this doesn't).
--
-- Run from repo root with the golden trips CSV path substituted:
--   duckdb -readonly data/trading.db < scripts/equity/polygon_float_compare.sql
-- (edit the read_csv_auto path below to the trips CSV under test)

ATTACH 'data/equity/float/float.db' AS f (READ_ONLY);

-- the trade book (edit path as needed)
CREATE OR REPLACE TEMP TABLE book AS
SELECT symbol, entry_date::DATE AS entry_date,
       (exit_price/entry_price - 1.0) AS ret
FROM read_csv_auto('/tmp/claude-1000/-home-mrakgr-Trading-Edge/977bd955-edd5-4bd8-a21e-cf21c38dd21a/scratchpad/golden_trips.csv');

-- ===== source 2: Polygon scso dollar-float at entry =====
CREATE OR REPLACE TEMP TABLE poly AS
SELECT b.symbol, b.entry_date, b.ret,
       ps.scso * en.adj_close AS float_usd
FROM book b
LEFT JOIN f.polygon_shares ps ON ps.ticker = b.symbol AND ps.date = b.entry_date
LEFT JOIN split_adjusted_prices en ON en.ticker = b.symbol AND en.date = b.entry_date AND en.adj_close > 0;

-- ===== source 1: SEC free-float at entry (ASOF known_date<=entry, price-rescaled) =====
CREATE OR REPLACE TEMP TABLE flt_sec AS
SELECT tc.ticker, fs.known_date, fs.period_end, fs.value AS float_usd
FROM f.float_sec fs JOIN f.ticker_cik tc ON tc.cik = fs.cik WHERE fs.value > 0;

CREATE OR REPLACE TEMP TABLE sec AS
SELECT b.symbol, b.entry_date, b.ret,
       CASE WHEN s.float_usd IS NOT NULL AND pe.adj_close > 0 AND en.adj_close > 0
            THEN s.float_usd * en.adj_close / pe.adj_close END AS float_usd
FROM book b
ASOF LEFT JOIN flt_sec s ON s.ticker = b.symbol AND s.known_date <= b.entry_date
LEFT JOIN split_adjusted_prices en ON en.ticker = b.symbol AND en.date = b.entry_date
ASOF LEFT JOIN split_adjusted_prices pe ON pe.ticker = b.symbol AND pe.date <= s.period_end;

-- ===== coverage =====
SELECT '=== coverage on the trade book ===' z;
SELECT
  (SELECT COUNT(*) FROM book) AS trips,
  (SELECT COUNT(*) FROM sec  WHERE float_usd IS NOT NULL) AS sec_covered,
  (SELECT COUNT(*) FROM poly WHERE float_usd IS NOT NULL) AS poly_covered,
  ROUND(100.0*(SELECT COUNT(*) FROM sec  WHERE float_usd IS NOT NULL)/(SELECT COUNT(*) FROM book),1) AS sec_pct,
  ROUND(100.0*(SELECT COUNT(*) FROM poly WHERE float_usd IS NOT NULL)/(SELECT COUNT(*) FROM book),1) AS poly_pct;

-- ===== the <$300M edge, each source =====
SELECT '=== SEC free-float: <300M vs >=300M (covered only) ===' z;
SELECT CASE WHEN float_usd < 300e6 THEN 'a:LOW <300M' ELSE 'b:HIGH >=300M' END grp,
  COUNT(*) n, ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_raw
FROM sec WHERE float_usd IS NOT NULL GROUP BY 1 ORDER BY 1;

SELECT '=== Polygon scso float: <300M vs >=300M (covered only) ===' z;
SELECT CASE WHEN float_usd < 300e6 THEN 'a:LOW <300M' ELSE 'b:HIGH >=300M' END grp,
  COUNT(*) n, ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_raw
FROM poly WHERE float_usd IS NOT NULL GROUP BY 1 ORDER BY 1;

-- ===== agreement where BOTH cover (does the coarser scso proxy track free-float?) =====
SELECT '=== SEC vs Polygon bucket agreement (both covered) ===' z;
SELECT
  CASE WHEN s.float_usd<300e6 THEN 'sec_LOW' ELSE 'sec_HIGH' END sec_grp,
  CASE WHEN p.float_usd<300e6 THEN 'poly_LOW' ELSE 'poly_HIGH' END poly_grp,
  COUNT(*) n
FROM sec s JOIN poly p USING(symbol, entry_date)
WHERE s.float_usd IS NOT NULL AND p.float_usd IS NOT NULL
GROUP BY 1,2 ORDER BY 1,2;
