-- Stage 2 of the intraday-entry pipeline: turn the raw checkpoint snapshots into a signal table
-- with the intraday MOVE, intraday RVOL (no-lookahead, time-of-day-normalized), the pre-open
-- daily context, and the 5-day-forward exit. One row per (ticker, date, checkpoint).
--
-- This is the LIVE-TRADEABLE signal: every field is knowable at the checkpoint moment.
--   intraday_move = px / prevclose - 1            (prevclose = RAW daily_prices.close; px is raw)
--   intraday_rvol = cum_vol / vol_baseline        where vol_baseline = trailing 20-session mean of
--                   the SAME ticker's cum_vol at the SAME checkpoint, EXCLUDING the current day
--                   (premarket-inclusive cum_vol, so gap-up volume counts — see stage 1).
--   forward exit  = buy at px (checkpoint), sell at the OPEN of the 5th trading day after `date`
--                   (mirrors HighFlyer's 5-day time-stop). ret = exit_open/px - 1, raw prices.
--
-- Pre-open-knowable daily filters (tightness, ATR%, 52w proximity, dollar float, price) are NOT
-- recomputed here yet — they get joined in the sweep (stage 3) from the daily engine + float.db.
-- This stage just produces the intraday move/rvol/forward-return spine.
--
-- Output: data/equity/intraday/signal.parquet
-- Run from an in-memory duck so we can read trading.db read-only and COPY out:
--   duckdb :memory: -c "ATTACH 'data/trading.db' AS db (READ_ONLY); .read scripts/equity/intraday_signal_build.sql"

-- Trading-day index, so 'exit = open of the 5th trading day after entry' is a clean row offset.
CREATE OR REPLACE TEMP TABLE cal AS
SELECT date, ROW_NUMBER() OVER (ORDER BY date) AS dnum
FROM (SELECT DISTINCT date FROM db.daily_prices);

-- Per-ticker daily open/close (raw) with a trading-day index, for prevclose + forward exit.
CREATE OR REPLACE TEMP TABLE px AS
SELECT p.ticker, p.date, c.dnum, p.open AS day_open, p.close AS day_close
FROM db.daily_prices p JOIN cal c ON c.date = p.date;

COPY (
    WITH cp AS (
        SELECT ticker, date, checkpoint, cum_vol, rth_vol, px, hi_sofar, lo_sofar, n_bars
        FROM read_parquet('data/equity/intraday/checkpoints.parquet')
    ),
    -- prevclose: the ticker's most recent daily close STRICTLY before `date`
    withprev AS (
        SELECT cp.*,
               (SELECT pp.day_close FROM px pp
                 WHERE pp.ticker = cp.ticker AND pp.date < cp.date
                 ORDER BY pp.date DESC LIMIT 1) AS prevclose
        FROM cp
    ),
    -- 5-trading-day-forward exit open: find this row's dnum, take the open at dnum+5
    withexit AS (
        SELECT w.*,
               cur.dnum AS entry_dnum,
               (SELECT pe.day_open FROM px pe
                 WHERE pe.ticker = w.ticker AND pe.dnum = cur.dnum + 5) AS exit_open
        FROM withprev w
        JOIN px cur ON cur.ticker = w.ticker AND cur.date = w.date
    ),
    -- no-lookahead rvol baseline: trailing 20-session mean of cum_vol at the SAME checkpoint,
    -- windowed over PRIOR rows only (ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING).
    withbase AS (
        SELECT e.*,
               AVG(e.cum_vol) OVER (
                   PARTITION BY e.ticker, e.checkpoint ORDER BY e.date
                   ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING
               ) AS vol_baseline
        FROM withexit e
    )
    SELECT
        ticker, date, checkpoint, px, cum_vol, rth_vol, hi_sofar, lo_sofar, n_bars,
        prevclose,
        CASE WHEN prevclose > 0 THEN px / prevclose - 1.0 END                      AS intraday_move,
        vol_baseline,
        CASE WHEN vol_baseline > 0 THEN cum_vol / vol_baseline END                 AS intraday_rvol,
        exit_open,
        CASE WHEN exit_open IS NOT NULL AND px > 0 THEN exit_open / px - 1.0 END    AS fwd_ret
    FROM withbase
) TO 'data/equity/intraday/signal.parquet' (FORMAT parquet);
