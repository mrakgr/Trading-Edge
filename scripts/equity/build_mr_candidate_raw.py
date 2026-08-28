"""Build `mr_candidate_raw` — the RAW-price, lookahead-free rebuild of MaxRider's
candidate table (user, 2026-08-28). Changes vs mr_candidate/diprider_v6_candidate:
  - NO price floor (the adjusted $1 floor was a future-reverse-split selector)
  - warmup CAUSAL: ROW_NUMBER within episode > 21 (was full-episode nbars > 21 = future)
  - dv_0945 = Σ(close × volume) over 09:30-09:45 RTH bars — exact raw dollars (was
    vol × avgprice × adj_ratio, the S35 future-split-inflated dollars)
  - all price context RAW and unconverted (the system gates on none of it):
    prev_adj_close := LAG(raw_close), day_close := raw_close, close_fwd_* := LEAD(raw_close),
    adj_ratio := 1.0 (minute bars stay raw)
  - kept identical (causal plumbing): med 1m-bar vol 09:30-09:45 >= 10k, nbar >= 10,
    rvol_0945_honest >= 0.1 (raw premkt-incl vol / avgvol20_prior from the view)
Range: 2020-01-01 .. 2026-06-30 (the F22/F24 window).
"""
import os
import time
import duckdb

t0 = time.time()
MIN_DIR = "/home/mrakgr/Trading-Edge/research/data/minute_aggs"
files = sorted(f for f in os.listdir(MIN_DIR)
               if f.endswith(".parquet") and "2020-01-01" <= f[:10] <= "2026-06-30")
print(f"{len(files)} minute-agg files in range", flush=True)
globs = ", ".join(f"'{MIN_DIR}/{f}'" for f in files)

c = duckdb.connect("/home/mrakgr/Trading-Edge/research/data/trading.db")
c.execute("PRAGMA memory_limit='8GB'")
c.execute(f"""
CREATE OR REPLACE TABLE mr_candidate_raw AS
WITH bars AS (
    SELECT ticker,
        CAST(date_part('hour',   to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT) * 60
          + CAST(date_part('minute', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT) AS et_min,
        regexp_extract(filename, '([0-9]{{4}}-[0-9]{{2}}-[0-9]{{2}})\\.parquet', 1)::DATE AS date,
        open, close, volume
    FROM read_parquet([{globs}], filename = true)
    WHERE close > 0
),
liq AS (
    SELECT ticker, date,
        median(CASE WHEN et_min >= 570 AND et_min < 585 THEN volume END) AS med_bar_vol_0945,
        count (CASE WHEN et_min >= 570 AND et_min < 585 THEN volume END) AS nbar_0945,
        arg_min(CASE WHEN et_min >= 570 AND et_min < 585 THEN open END,
                CASE WHEN et_min >= 570 AND et_min < 585 THEN et_min END) AS day_open,
        sum   (CASE WHEN et_min >= 570 AND et_min < 585 THEN volume ELSE 0 END) AS vol_0945,
        sum   (CASE WHEN et_min >= 570 AND et_min < 585 THEN close * volume ELSE 0 END) AS dv_0945,
        sum   (CASE WHEN et_min >= 240 AND et_min < 585 THEN volume ELSE 0 END) AS vol_0945_pm
    FROM bars
    GROUP BY ticker, date
    HAVING median(CASE WHEN et_min >= 570 AND et_min < 585 THEN volume END) >= 10000
       AND count (CASE WHEN et_min >= 570 AND et_min < 585 THEN volume END) >= 10
),
ctx AS (
    SELECT ticker, date, raw_close,
        LAG(raw_close, 1) OVER e AS prev_raw_1,
        LAG(raw_close, 3) OVER e AS prev_raw_3,
        LEAD(raw_close, 1) OVER e AS fwd_raw_1,
        LEAD(raw_close, 3) OVER e AS fwd_raw_3,
        LEAD(raw_close, 5) OVER e AS fwd_raw_5,
        AVG(adj_volume) OVER (PARTITION BY ticker, episode ORDER BY date
                              ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS avgvol20_prior,
        ROW_NUMBER() OVER e AS barnum
    FROM daily_episodes
    WINDOW e AS (PARTITION BY ticker, episode ORDER BY date)
)
SELECT c.ticker, c.date,
    c.prev_raw_1::DOUBLE AS prev_adj_close,
    c.prev_raw_3::DOUBLE AS close_3d,
    c.raw_close::DOUBLE  AS day_close,
    CAST(1.0 AS DOUBLE)  AS adj_ratio,
    c.fwd_raw_1::DOUBLE  AS close_fwd_1d,
    c.fwd_raw_3::DOUBLE  AS close_fwd_3d,
    c.fwd_raw_5::DOUBLE  AS close_fwd_5d,
    l.day_open::DOUBLE   AS day_open,
    l.vol_0945::DOUBLE   AS vol_0945,
    l.nbar_0945::BIGINT  AS nbar_0945,
    l.dv_0945::DOUBLE    AS dv_0945,
    (l.vol_0945_pm::DOUBLE / NULLIF(c.avgvol20_prior, 0))::DOUBLE AS rvol_0945_honest
FROM ctx c
JOIN liq l ON l.ticker = c.ticker AND l.date = c.date
WHERE c.barnum > 21
  AND l.vol_0945_pm::DOUBLE / NULLIF(c.avgvol20_prior, 0) >= 0.1
""")
n, tk, d3 = c.execute("""SELECT count(*), count(DISTINCT ticker),
    sum(CASE WHEN dv_0945 >= 3e6 THEN 1 ELSE 0 END) FROM mr_candidate_raw""").fetchone()
print(f"mr_candidate_raw: {n:,} rows, {tk:,} tickers, {d3:,} clear the $3M honest floor "
      f"[{time.time()-t0:.0f}s]", flush=True)
c.close()
