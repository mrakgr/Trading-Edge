-- ============================================================================
-- daily_adjusted — CAUSAL (forward) price adjustment.
--
-- ⭐ WHY THIS EXISTS. `split_adjusted_prices` (01_) BACK-adjusts, imitating the
-- charting convention: the present is the truth and the past is bent to meet it.
-- That is the wrong anchor for a backtest and it costs us twice:
--
--   1. LOOKAHEAD. `adj_ratio = adj_close/raw_close` folds in every split AFTER
--      day D, so it is FUTURE INFORMATION in any gate. This is the entire reason
--      CLAUDE.md rule 4 exists, and it is the S43v bug that made 71.4% of an
--      "S-tier" book artifact.
--   2. A DIVISION BY PRICE. 01_'s dividend factor is `1 - div/price`, so a large
--      special detonates it. VISN paid a $10.00 special on a $19.53 close =>
--      the factor went NEGATIVE, hit the 1e-6 clamp, and annihilated the whole
--      prior history (adj_close $0.000020 against a raw $19.53). 92,749 rows
--      across 72 tickers. See docs/flushfader_results.md §S43bq.
--
-- ⭐ THE SCHEME (user, 2026-08-12). Adjust the RIGHT side, not the left. Carry
-- splits forward multiplicatively and dividends forward ADDITIVELY. Per one
-- share held from the ticker's first row in this table:
--
--     P(t)  = the raw price the tape prints            (daily_prices, untouched)
--     n(t)  = Pi split_ratio over execution_date <= t  -- shares now held
--     C(t)  = Sum cash_amount_i * n(ex_i - 1)          -- cash received per orig share
--
-- Every value depends ONLY on events up to t, so the series is CAUSAL: the
-- adj_ratio lookahead class becomes structurally impossible rather than a thing
-- to re-audit. And nothing divides by price, so the VISN class cannot recur.
--
-- ⭐ NO FUSED LEVEL IS STORED. `P*n + C` is a lossy fusion that anchors every
-- ratio to a hypothetical day-one holder: it divides a trade by `P1n1 + C1`,
-- charging it for cash it never invested (a $100->$110 move on a name with $50 of
-- dividend history reads +6.7% instead of +10%). 67% of universe ticker-days are
-- on names with dividend history. Consumers combine the components instead:
--
--     absolute price floor ($1/$2/$5)  ->  P(t), raw, directly
--     price comparison across time     ->  P(t)*n(t)
--     return of a position t1 -> t2    ->  [ (P2n2 - P1n1) + (C2 - C1) ] / (P1n1)
--     any prior value in day D's scale ->  P(t) * n(t)/n(D)
--     total-return index (research)    ->  P(t)*n(t) + C(t)
--
-- With no split and no dividend inside the window this collapses to the plain
-- raw return (P2-P1)/P1 — the property back-adjustment never had.
--
-- ⭐ BOTH EVENTS LAND AT THE OPEN OF DAY t; the last pre-event trading is t-1's
-- close. Split: AAPL closed 499.23 on 2020-08-28 and opened 127.58 on 2020-08-31
-- (4:1) => n(t) INCLUDES the split dated t. Dividend: VISN closed 19.53 on
-- 2026-04-27 and opened 9.64 on 2026-04-28 => C(t) INCLUDES the dividend dated t.
--
-- ⚠ THE DIVIDEND IS SCALED BY n(ex - 1), NOT n(ex). Entitlement is fixed at the
-- last cum-dividend close, BEFORE any split dated `ex` lands, so `cash_amount` is
-- quoted per PRE-split share. splits.execution_date = dividends.ex_dividend_date
-- collides on 2,956 rows across 1,000 tickers — scrip/stock dividends paid with a
-- cash dividend, mostly foreign ADRs (ratios 1.005-1.05). Worked example:
-- SCCO 2026-08-11, ratio 1.012 + $1.10 cash — 100 shares held at the 08-10 close
-- pay $110 AND become 101.2 shares. Using n(ex) would understate the cash.
-- n(ex-1) = n(ex) whenever there is no collision, so this is ONE UNIFORM RULE.
-- The strict `>` in the dividend ASOF join below is what implements it.
--
-- ⚠ The absolute level of n is arbitrary per ticker (splits predating the ticker's
-- first price row are included). Only RATIOS of n are meaningful — which is all
-- any consumer above uses. Do not eyeball P*n as if it were a price: serial
-- reverse-splitters reach n = 3.7e-18.
--
-- ⚠ STALENESS: raw OHLCV is duplicated here so the table is usable standalone.
-- Rebuild this whenever daily_prices / splits / dividends are backfilled.
-- ============================================================================
DROP TABLE IF EXISTS daily_adjusted;
CREATE TABLE daily_adjusted AS

-- Step 0: ⚠ COLLAPSE MULTIPLE SPLITS ON ONE DATE FIRST. A ticker can have more
-- than one split per execution_date (a reverse/forward PAIR — the odd-lot
-- squeeze-out; see sql/schema/tables/splits.sql). Their combined effect is the
-- PRODUCT of the ratios, which for a pair is 1.0 — no share-count change at all.
-- Without this step the cumulative window below emits one row per LEG and the
-- ASOF join picks an arbitrary one, reinstating the naked-ratio bug.
WITH splits_by_date AS (
    SELECT ticker, execution_date, EXP(SUM(LN(split_ratio))) AS split_ratio
    FROM splits
    WHERE split_ratio > 0
    GROUP BY ticker, execution_date
),
-- Step 1: n at each split execution_date = FORWARD cumulative product of ratios.
-- (Log space for stability; ROWS ... CURRENT ROW makes the split dated t inclusive.)
split_cum AS (
    SELECT
        ticker,
        execution_date,
        EXP(SUM(LN(split_ratio)) OVER (
            PARTITION BY ticker
            ORDER BY execution_date ASC
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        )) AS n_at
    FROM splits_by_date
),
-- Step 2: scale each dividend by the share count at the LAST CUM-DIVIDEND CLOSE.
-- The ASOF predicate is STRICTLY `>` so a split dated on the ex-date itself is
-- EXCLUDED — that is the n(ex-1) rule. Multiple dividends sharing an ex-date are
-- summed.
div_scaled AS (
    SELECT
        d.ticker,
        d.ex_dividend_date,
        d.cash_amount * COALESCE(sc.n_at, 1.0) AS cash_at
    FROM dividends d
    ASOF LEFT JOIN split_cum sc
        ON d.ticker = sc.ticker
        AND d.ex_dividend_date > sc.execution_date
),
div_by_date AS (
    SELECT ticker, ex_dividend_date, SUM(cash_at) AS cash_at
    FROM div_scaled
    GROUP BY ticker, ex_dividend_date
),
-- Step 3: C at each ex-date = FORWARD cumulative SUM (additive — never a product,
-- never a division, so it cannot clamp, zero, or flip sign).
div_cum AS (
    SELECT
        ticker,
        ex_dividend_date,
        SUM(cash_at) OVER (
            PARTITION BY ticker
            ORDER BY ex_dividend_date ASC
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) AS c_at
    FROM div_by_date
),
-- Step 4: attach n to every price bar (most recent execution_date <= date).
px_n AS (
    SELECT
        dp.ticker, dp.date, dp.open, dp.high, dp.low, dp.close,
        dp.volume, dp.transactions,
        COALESCE(sc.n_at, 1.0) AS n
    FROM daily_prices dp
    ASOF LEFT JOIN split_cum sc
        ON dp.ticker = sc.ticker
        AND dp.date >= sc.execution_date
)
-- Step 5: attach C the same way (most recent ex_dividend_date <= date).
SELECT
    p.ticker,
    p.date,
    p.open,
    p.high,
    p.low,
    p.close,
    p.volume,
    p.transactions,
    p.n,
    COALESCE(dc.c_at, 0.0) AS cum_div
FROM px_n p
ASOF LEFT JOIN div_cum dc
    ON p.ticker = dc.ticker
    AND p.date >= dc.ex_dividend_date;
