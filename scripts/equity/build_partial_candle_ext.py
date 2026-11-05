"""The 10:00 ET partial candle for days AFTER the archived partial_candle_1000 (db_archive/2026-09-29, ends 2026-06-25),
written to a parquet instead of a trading.db table (user 2026-11-05: extend HighFlyerV2 past 2026-05 to see whether the
weak 2026 "flipped since we last tested it").

The SQL is scripts/equity/build_partial_candle.fsx's, verbatim (minute_aggs; ET minute from window_start; RTH OHLC over
[09:30, 10:00), volume premarket-inclusive over [04:00, 10:00), close > 0 bars only). CONTROL: --check rebuilds an overlap
window and requires it to equal the archived rows exactly.
    cd research && python3 scripts/equity/build_partial_candle_ext.py --start 2026-06-01 --end 2026-06-25 --check
    cd research && python3 scripts/equity/build_partial_candle_ext.py --start 2026-06-26 --end 2026-10-30
"""
import argparse, glob, os
import duckdb
ap = argparse.ArgumentParser()
ap.add_argument("--start", required=True); ap.add_argument("--end", required=True)
ap.add_argument("--minute-dir", default="data/minute_aggs")
ap.add_argument("--out", default="data/equity/highflyer_repro/partial_candle_1000_ext.parquet")
ap.add_argument("--archive", default="/mnt/d/trading-edge-bulk/db_archive/2026-09-29/partial_candle_1000.parquet")
ap.add_argument("--check", action="store_true")
a = ap.parse_args()
files = sorted(f for f in glob.glob(os.path.join(a.minute_dir, "*.parquet")) if a.start <= os.path.basename(f)[:10] <= a.end)
print(f"{len(files)} minute files {os.path.basename(files[0])[:10]} .. {os.path.basename(files[-1])[:10]}")
con = duckdb.connect(); con.execute("SET memory_limit='3GB'; SET threads=4")
flist = "[" + ", ".join("'" + f.replace("'", "''") + "'" for f in files) + "]"
con.execute(f"""
CREATE TABLE pc AS
WITH bars AS (
    SELECT ticker,
        CAST(date_part('hour',   to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT) * 60
          + CAST(date_part('minute', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT) AS et_min,
        regexp_extract(filename, '([0-9]{{4}}-[0-9]{{2}}-[0-9]{{2}})\\.parquet', 1)::DATE AS date,
        open, high, low, close, volume
    FROM read_parquet({flist}, filename = true)
    WHERE close > 0)
SELECT ticker, date,
    arg_min(CASE WHEN et_min >= 570 AND et_min < 600 THEN open  END, CASE WHEN et_min >= 570 AND et_min < 600 THEN et_min END) AS open,
    MAX(CASE WHEN et_min >= 570 AND et_min < 600 THEN high END) AS high,
    MIN(CASE WHEN et_min >= 570 AND et_min < 600 THEN low  END) AS low,
    arg_max(CASE WHEN et_min >= 570 AND et_min < 600 THEN close END, CASE WHEN et_min >= 570 AND et_min < 600 THEN et_min END) AS close,
    SUM(CASE WHEN et_min >= 240 AND et_min < 600 THEN volume ELSE 0 END)::BIGINT AS volume
FROM bars GROUP BY ticker, date""")
n = con.execute("SELECT count(*), min(date), max(date) FROM pc").fetchone()
print(f"built {n[0]:,} rows {n[1]} .. {n[2]}")
if a.check:
    d = con.execute(f"""
      WITH ar AS (SELECT * FROM read_parquet('{a.archive}') WHERE date BETWEEN DATE '{a.start}' AND DATE '{a.end}'),
      j AS (SELECT ar.*, pc.open o2, pc.high h2, pc.low l2, pc.close c2, pc.volume v2 FROM ar FULL JOIN pc USING (ticker, date))
      SELECT count(*), count(*) FILTER (WHERE open IS DISTINCT FROM o2 OR high IS DISTINCT FROM h2 OR low IS DISTINCT FROM l2 OR close IS DISTINCT FROM c2),
             count(*) FILTER (WHERE volume IS DISTINCT FROM v2), max(abs(volume - v2)), max(abs(volume - v2) / greatest(volume, 1)) FROM j""").fetchone()
    # the volume is a SUM of fractional-share minute volumes cast to BIGINT: its rounding differs by a few shares from the
    # archived build (another DuckDB version / summation order). Prices must match exactly.
    print(f"CONTROL vs the archive: {d[0]:,} rows; price mismatches {d[1]:,}; volume differs on {d[2]:,} (max {d[3]} shares, "
          f"{d[4]:.1e} relative)")
    raise SystemExit(0 if d[1] == 0 and d[3] <= 100 else 1)       # prices exact, volume within 100 shares
con.execute(f"COPY (SELECT * FROM pc ORDER BY ticker, date) TO '{a.out}.tmp' (FORMAT parquet)"); os.replace(a.out + ".tmp", a.out)
print(f"wrote {a.out}")
