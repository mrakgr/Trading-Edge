"""Partial candles at SEVERAL entry cutoffs in one pass over the minute aggs (user 2026-11-06: "I wonder if entering earlier,
like 9:45 or even at the open would do better?" — HighFlyerV2 Run 40).
For each cutoff c (ET minutes): RTH OHLC over [09:30, c), volume premarket-inclusive over [04:00, c), close > 0 bars only —
build_partial_candle.fsx's SQL with 10:00 replaced by c. A PREMARKET cutoff (c <= 09:30; user 2026-11-06: "an hour before the
open or half an hour", 08:30 / 09:00) has no RTH minute: its candle is the premarket's, OHLC over [04:00, c). Default cutoffs 09:31 (the first minute: "at the open"), 09:45 and
10:00 (the CONTROL: it must equal the archived partial_candle_1000 exactly on every day both hold).
Per year, one file per cutoff: data/equity/highflyer_repro/partial_candles/<HHMM>/<YYYY>.parquet (the port reads a glob).
    cd research && python3 -u scripts/equity/build_partial_candles_multi.py [--years 2005-2026] [--check]
"""
import argparse, glob, os, time
import duckdb
ap = argparse.ArgumentParser()
ap.add_argument("--years", default="2005-2026")
ap.add_argument("--cutoffs", default="0931,0945,1000")
ap.add_argument("--minute-dir", default="data/minute_aggs")
ap.add_argument("--out", default="data/equity/highflyer_repro/partial_candles")
ap.add_argument("--archive", default="/mnt/d/trading-edge-bulk/db_archive/2026-09-29/partial_candle_1000.parquet")
ap.add_argument("--check", action="store_true", help="after the build: the 10:00 candles against the archive")
a = ap.parse_args()
y0, y1 = (int(x) for x in a.years.split("-"))
CUT = {c: int(c[:2]) * 60 + int(c[2:]) for c in a.cutoffs.split(",")}
T0 = time.time()
con = duckdb.connect(); con.execute("SET memory_limit='4GB'; SET threads=6; SET preserve_insertion_order=false")
for c in CUT:
    os.makedirs(os.path.join(a.out, c), exist_ok=True)
for y in range(y0, y1 + 1):
    files = sorted(glob.glob(os.path.join(a.minute_dir, f"{y}-*.parquet")))
    if not files:
        continue
    if all(os.path.exists(os.path.join(a.out, c, f"{y}.parquet")) for c in CUT):
        print(f"{y}: done before, skipped", flush=True); continue
    flist = "[" + ", ".join("'" + f.replace("'", "''") + "'" for f in files) + "]"
    cols = []
    for c, m in CUT.items():
        r = f"et_min >= {570 if m > 570 else 240} AND et_min < {m}"      # a premarket cutoff: the premarket's own candle
        cols.append(f"""arg_min(CASE WHEN {r} THEN open END, CASE WHEN {r} THEN et_min END) AS open_{c},
            MAX(CASE WHEN {r} THEN high END) AS high_{c}, MIN(CASE WHEN {r} THEN low END) AS low_{c},
            arg_max(CASE WHEN {r} THEN close END, CASE WHEN {r} THEN et_min END) AS close_{c},
            SUM(CASE WHEN et_min >= 240 AND et_min < {m} THEN volume ELSE 0 END)::BIGINT AS volume_{c}""")
    con.execute(f"""
    CREATE OR REPLACE TABLE pc AS
    WITH bars AS (
        SELECT ticker,
            CAST(date_part('hour',   to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT) * 60
              + CAST(date_part('minute', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT) AS et_min,
            regexp_extract(filename, '([0-9]{{4}}-[0-9]{{2}}-[0-9]{{2}})\\.parquet', 1)::DATE AS date,
            open, high, low, close, volume
        FROM read_parquet({flist}, filename = true)
        WHERE close > 0)
    SELECT ticker, date, {", ".join(cols)}
    FROM bars WHERE et_min >= 240 AND et_min < {max(CUT.values())} GROUP BY ticker, date""")
    for c in CUT:
        con.execute(f"""COPY (SELECT ticker, date, open_{c} AS open, high_{c} AS high, low_{c} AS low, close_{c} AS close,
                               volume_{c} AS volume FROM pc) TO '{os.path.join(a.out, c, f"{y}.parquet")}' (FORMAT PARQUET)""")
    n = con.execute("SELECT count(*) FROM pc").fetchone()[0]
    print(f"[{time.time() - T0:7.1f}s] {y}: {len(files)} days, {n:,} ticker-days", flush=True)
if a.check and "1000" in CUT:
    new = os.path.join(a.out, "1000", "*.parquet")
    r = con.execute(f"""
    WITH n AS (SELECT * FROM read_parquet('{new}')), o AS (SELECT * FROM read_parquet('{a.archive}')),
    j AS (SELECT o.*, n.open AS n_open, n.high AS n_high, n.low AS n_low, n.close AS n_close, n.volume AS n_volume
          FROM o JOIN n USING (ticker, date))
    SELECT count(*), sum(CASE WHEN open IS NOT DISTINCT FROM n_open AND high IS NOT DISTINCT FROM n_high AND low IS NOT DISTINCT FROM n_low
                               AND close IS NOT DISTINCT FROM n_close THEN 1 ELSE 0 END),
           sum(CASE WHEN abs(volume - n_volume) <= 100 THEN 1 ELSE 0 END),
           (SELECT count(*) FROM o WHERE date BETWEEN (SELECT min(date) FROM n) AND (SELECT max(date) FROM n))
    FROM j""").fetchone()
    print(f"CHECK 10:00 vs the archive: {r[0]:,} ticker-days in both (archive rows in the window {r[3]:,}); prices identical {r[1]:,}; "
          f"volume within 100 shares {r[2]:,}")
