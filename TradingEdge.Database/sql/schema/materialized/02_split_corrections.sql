-- ============================================================================
-- split_corrections — splits the PRICE TAPE contradicts, and what to do about them.
--
-- ⭐ WHY. Polygon's splits reference data and Polygon's own flat-file price tape
-- disagree on ~5.5% of split dates. Our data is faithful to both: `daily_prices`
-- matches the S3 CSVs exactly and `splits` matches /v3/reference/splits. The
-- vendor contradicts itself. Established 2026-08-12 (S43bs) — it is NOT symbol
-- reuse (0 of 174 flagged tickers have multiple FIGIs), NOT a low-price artifact
-- (contradicted splits have median price $16.20 while sub-10c splits are the most
-- reliable at 0.6% contradicted), and NOT a fund artifact (ETFs under-represented).
--
-- A split ratio makes a TESTABLE prediction: the day's move should be explained by
-- it. Where it is not, applying the ratio CREATES a discontinuity the market never
-- had — which is how TTSH ended up multiplied by 3000 and, under the old
-- back-adjustment, how its entire prior history was divided down to $0.002147.
--
-- ⭐ THIS TABLE IS RECOMPUTED, NEVER CURATED (user, 2026-08-12). It is derived
-- from `splits` + `daily_prices` on every materialize, so it cannot go stale
-- against its own inputs. A hand-maintained overlay would rot: if Polygon later
-- fixes a record upstream, a stale SHIFT would relocate a now-correct date and the
-- only guard would be remembering to re-run a diff. `splits` itself is never
-- edited — it stays a faithful mirror of the vendor so we can always re-derive.
--
-- ⚠ RUNS BEFORE 03_daily_adjusted.sql, which consumes it. Ordering is by RESOURCE
-- NAME (Database.getEmbeddedSqlFromFolder does Array.sort), so the 02_/03_ prefixes
-- are load-bearing. Do not renumber one without the other.
--
-- ---------------------------------------------------------------------------
-- THE THREE OUTCOMES (counts as of 2026-08-12, 343 contradicted split dates)
--
--   SHIFT  (33)  The split is REAL but the recorded date is wrong. A day within
--                +/-40 TRADING days is explained near-exactly by the ratio, so
--                execution_date moves there.
--                ⭐ EXACTNESS, NOT PROXIMITY, IS THE EVIDENCE. The far-offset
--                cases are the most exact, on large stable names: FLIC residual
--                0.0008, BMI 0.0027 ($66), HEI 0.0030 ($94), HBI 0.0103 ($114),
--                JEF 0.0121 — at offsets of 11-40 trading days. A 2:1 landing on
--                0.5014 thirteen days out is not coincidence. The mechanism shows
--                in the pattern (HEI recorded 2018-01-02, tape jump 2018-01-18;
--                CGNX 2017-11-16 -> 2017-12-04): Polygon recorded an ANNOUNCEMENT
--                / DECLARATION date instead of the ex-date.
--                Worked check: IAU's 10:1 is recorded 2010-06-17 but the tape
--                divides by exactly 10 on 2010-06-24 (121.06 -> 12.14).
--
--   REJECT (138) No day in +/-40 is explained by the ratio, or the best match is
--                only loose (residual >= 5%, user's call 2026-08-12). The split is
--                dropped. ⚠ REJECT, NOT REPAIR: across these dates the tape is
--                internally SELF-CONSISTENT (no discontinuity anywhere in the
--                window), so dropping preserves that continuity while applying
--                would manufacture a jump. Repairing the prices instead would need
--                an external source of truth we do not have.
--
--   (KEEP) (172) Ratio < 1.25x — NO ROW IS EMITTED. These are not errors: 49.4%
--                are co-dated with a dividend against a 10.7% baseline (4.6x
--                enriched) and ratios cluster at 1.01-1.05 — the signature of
--                SCRIP / STOCK DIVIDENDS, which genuinely change share count. A
--                one-day test cannot resolve a 3% ratio against daily noise, so
--                they stay applied and the residual contradiction count lands at
--                ~172 BY DESIGN, not as remaining damage.
--
-- See docs/price_adjustment.md for the full evidence.
-- ============================================================================
DROP TABLE IF EXISTS split_corrections;
CREATE TABLE split_corrections AS

-- Row number per ticker so the +/-40 window is TRADING days, not calendar days.
WITH px AS (
    SELECT ticker, date, close,
           ROW_NUMBER() OVER w AS rn,
           lag(close)   OVER w AS pc
    FROM daily_prices
    WINDOW w AS (PARTITION BY ticker ORDER BY date)
),
mv AS (
    SELECT ticker, date, rn, close / pc AS m
    FROM px WHERE pc > 0 AND close > 0
),
-- Net ratio per DATE: a reverse/forward pair on one date is the PRODUCT of its
-- legs (= 1.0, i.e. no change at all), never one leg. See splits.sql.
sbd AS (
    SELECT ticker, execution_date, EXP(SUM(LN(split_ratio))) AS r, count(*) AS n_legs
    FROM splits WHERE split_ratio > 0
    GROUP BY ticker, execution_date
),
-- CONTRADICTED = applying the ratio makes the day's move LESS plausible, not more.
-- Restricted to DIAGNOSTIC ratios: below 1.25x the implied move is inside daily
-- noise and the test cannot decide, so those emit no row (the KEEP case above).
bad AS (
    SELECT s.ticker, s.execution_date, s.r, s.n_legs, v.rn, v.m
    FROM sbd s
    JOIN mv v ON v.ticker = s.ticker AND v.date = s.execution_date
    WHERE abs(ln(v.m * s.r)) >= abs(ln(v.m))
      AND abs(ln(s.r)) >= ln(1.25)
),
-- Candidate true dates. All three clauses matter:
--   (a) a GENUINE discontinuity  — abs(ln m) >= half the ratio's own log size.
--       ⚠ THIS CLAUSE IS LOAD-BEARING. Without it any ordinary 1% day "matches" a
--       1.01 ratio, and the offset histogram goes uniform across +/-40 days — an
--       earlier pass without it reported 60.6% "found" that was mostly noise.
--   (b) the ratio EXPLAINS the move — residual under 5%.
--   (c) the window is +/-40 TRADING days via rn, so holidays and halts do not
--       silently shrink or stretch it.
--   (d) ⚠ THE CANDIDATE DAY MUST NOT ALREADY CARRY A SPLIT OF ITS OWN. If it
--       does, its move is already spoken for and a second split claiming the same
--       jump would DOUBLE-COUNT it. Caught by the "no SHIFT may create a new
--       contradiction" check: FSBK has two 3-for-2 splits, 2004-03-22 (phantom,
--       tape +1.0%) and 2004-04-26 (real, 37.01 -> 24.85 = 1/1.49). Without this
--       clause the phantom shifted onto the real one and the date's product
--       became 1.5 x 1.5 = 2.25x against a tape showing 1.5x.
cand AS (
    SELECT b.ticker, b.execution_date, b.r, b.n_legs, b.m,
           c.date AS cand_date,
           c.rn - b.rn AS off,
           abs(ln(c.m * b.r)) AS resid
    FROM bad b
    JOIN mv c ON c.ticker = b.ticker AND c.rn BETWEEN b.rn - 40 AND b.rn + 40
    WHERE abs(ln(c.m)) >= 0.5 * abs(ln(b.r))
      AND abs(ln(c.m * b.r)) < 0.05
      AND NOT EXISTS (SELECT 1 FROM sbd s2
                      WHERE s2.ticker = b.ticker AND s2.execution_date = c.date)
),
-- Nearest qualifying day wins; residual breaks ties.
pick AS (
    SELECT * FROM cand
    QUALIFY ROW_NUMBER() OVER (PARTITION BY ticker, execution_date
                               ORDER BY abs(off), resid) = 1
)
SELECT
    b.ticker,
    b.execution_date,
    b.r      AS ratio,                  -- NET ratio for the date (product of legs)
    b.n_legs,
    b.m      AS move_on_recorded_date,  -- what the tape actually did that day
    CASE WHEN p.cand_date IS NOT NULL THEN 'SHIFT' ELSE 'REJECT' END AS action,
    p.cand_date AS corrected_date,      -- NULL for REJECT
    p.off,                              -- trading days from the recorded date
    p.resid                             -- how exactly the ratio explains the jump
FROM bad b
LEFT JOIN pick p USING (ticker, execution_date);

CREATE UNIQUE INDEX split_corrections_ticker_date
    ON split_corrections (ticker, execution_date);
