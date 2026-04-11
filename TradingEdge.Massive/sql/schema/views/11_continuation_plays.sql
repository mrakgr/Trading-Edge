-- Continuation Plays (raw): for each SIP breakout, return the breakout day
-- plus all subsequent trading days within `max_horizon_days` calendar days.
-- This is a thin data-fetch macro -- ALL chain logic (rolling max, stop rule,
-- dedup) is done in F# after the round trip.
--
-- All stocks_in_play parameters are forwarded so the same filters apply to
-- the underlying breakout source. The only continuation-specific param here
-- is `max_horizon_days` (the forward window).
DROP MACRO IF EXISTS continuation_plays;
CREATE MACRO continuation_plays(
    start_date := DATE '1900-01-01',
    end_date := DATE '2999-12-31',
    min_rvol := 3,
    min_gap_pct := 0.05,
    min_avg_dollar_volume := 25000000,
    rvol_weight := 0.95,
    gap_weight := 0.05,
    exclude_etfs := true,
    pre_window_days := 20,
    post_window_days := 5,
    min_atr_ratio := 0.55,
    max_horizon_days := 15
) AS TABLE
WITH breakouts AS (
    SELECT
        ticker AS sip_ticker,
        date AS sip_breakout_date
    FROM stocks_in_play(
        start_date := start_date,
        end_date := end_date,
        min_rvol := min_rvol,
        min_gap_pct := min_gap_pct,
        min_avg_dollar_volume := min_avg_dollar_volume,
        rvol_weight := rvol_weight,
        gap_weight := gap_weight,
        exclude_etfs := exclude_etfs,
        pre_window_days := pre_window_days,
        post_window_days := post_window_days,
        min_atr_ratio := min_atr_ratio
    )
)
SELECT
    b.sip_ticker,
    b.sip_breakout_date,
    p.date AS day_date,
    p.adj_volume AS day_volume,
    v.avg_volume_4w AS day_avg_volume_4w,
    v.avg_dollar_volume_4w AS day_avg_dollar_volume_4w,
    (p.adj_close * p.adj_volume) / NULLIF(v.avg_dollar_volume_4w, 0) AS day_rvol
FROM breakouts b
JOIN split_adjusted_prices p
    ON p.ticker = b.sip_ticker
    AND p.date >= b.sip_breakout_date
    AND p.date <= b.sip_breakout_date + INTERVAL (max_horizon_days) DAY
JOIN stock_volume_4w v
    ON v.ticker = p.ticker
    AND v.date  = p.date
ORDER BY b.sip_ticker, b.sip_breakout_date, p.date;
