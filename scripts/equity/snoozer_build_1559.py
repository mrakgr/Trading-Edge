"""Snoozer features REBUILT with 15:59 endpoints, one pass over the ms-era 1s corpus (2026-09-10, user ruling: "truncate at
15:59 and re-baseline"). The ratified books (longsnoozer §4, shortsnoozer §S43cw) read two features that run to 16:00 —
dv_over_open30 and bar_over_open30 — one minute past the 15:59 decision; the caches were also ns-era. This builder computes
EVERY feature both books read, with the last-hour numerators cut at 15:59 (the `59` twins) AND at 16:00 (for the drift
report), from the current corpus, so the Scanner's live-causal definitions have a reference built the same way.

Definitions (snoozer_build_cache.py / _shape.py / _volat.py, verbatim except the endpoint):
  p1500 / p1559 / p1600      last present bar's vwap at/before 15:00 / 15:59 / 16:00 (max_by, no recency bound)
  chg60k59                   p1559 / p1500 - 1                                   (the SIGNAL)
  nb60k59                    present seconds in (15:00, 15:59];  gaps = 3540 - nb60k59
  dvO5/O15/O30, nbO5/15/30   dollars / present seconds in [09:30,09:35) [09:35,09:45) [09:45,10:00)
  dv_lh59, nb_lh59           dollars / present seconds in (15:00, 15:59]   (16:00 twins: dv_lh, nb_lh)
  inten59 = dv_lh59 / (dvO5+dvO15+dvO30)         (raw dollar ratio — NOT rate-normalised, as research)
  pers59  = (nb_lh59/3540) / ((nbO5+nbO15+nbO30)/1800)   (rate-normalised, as research; 3540 replaces 3600)
  volat_open30               mean |ln(sv_i/sv_{i-1})| over 30 WALL-CLOCK-second slot vwaps, slots [1140,1200), consecutive
                             PRESENT slots, both endpoints in the window (volume>0 AND vwap>0 bars)
  px_lim_1559_1600           vwap of the bars in (15:59, 16:00] — the limit-fill proxy; nb_lastmin its second count
  ovn_from_lim59             (open_p1 + div_p1) / px_lim_1559_1600 - 1    (the outcome; SHORT = its negative)
Universe: db.mr_candidate_1s_v2 (dv_0945_tape >= $2M ∧ n_bars_1s >= 200), open_p1 IS NOT NULL, close_d > 0, p1500 and p1600 present.

Run from research/:  python -u scripts/equity/snoozer_build_1559.py [--start 2016-08-08] > data/equity/flushfader/snoozer_1559.log 2>&1
"""
import argparse, glob, os, time
import duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--bars1s", default="data/intraday_1s_slim")
ap.add_argument("--db", default="data/trading.db")
ap.add_argument("--out", default="data/equity/flushfader/snoozer_1559.parquet")
ap.add_argument("--start", default="2016-08-08")
ap.add_argument("--end", default="2026-12-31")
ap.add_argument("--batch", type=int, default=20)
ap.add_argument("--mem", default="6GB")
ap.add_argument("--force", action="store_true")
args = ap.parse_args()
if os.path.exists(args.out) and not args.force:
    raise SystemExit(f"{args.out} exists — pass --force to rebuild")
files = sorted(f for f in glob.glob(os.path.join(args.bars1s, "*.parquet")) if args.start <= os.path.basename(f)[:10] <= args.end)
print(f"{len(files):,} day files {os.path.basename(files[0])[:10]}..{os.path.basename(files[-1])[:10]}, batch {args.batch}", flush=True)
con = duckdb.connect(config={"memory_limit": args.mem, "threads": 6})
con.execute("SET enable_progress_bar=false"); con.execute("SET preserve_insertion_order=false")
con.execute(f"ATTACH '{args.db}' AS db (READ_ONLY)")
con.execute("""CREATE OR REPLACE TABLE feat (date DATE, ticker VARCHAR,
    p1500 DOUBLE, p1559 DOUBLE, p1600 DOUBLE, nb60k59 BIGINT, nb_lh BIGINT, dv_lh59 DOUBLE, dv_lh DOUBLE,
    dvO5 DOUBLE, dvO15 DOUBLE, dvO30 DOUBLE, nbO5 BIGINT, nbO15 BIGINT, nbO30 BIGINT,
    px_lim_1559_1600 DOUBLE, nb_lastmin BIGINT, vol_lastmin DOUBLE, volat_open30 DOUBLE, nsl_open30 BIGINT)""")
t0 = time.time()
for i in range(0, len(files), args.batch):
    chunk = files[i:i + args.batch]
    lst = "[" + ",".join(f"'{f}'" for f in chunk) + "]"
    con.execute(f"""
INSERT INTO feat
WITH b AS (
  SELECT CAST(regexp_extract(filename, '(\\d{{4}}-\\d{{2}}-\\d{{2}})', 1) AS DATE) AS date, ticker, bucket, vwap::DOUBLE AS vwap, volume::DOUBLE AS volume
  FROM read_parquet({lst}, filename = true) WHERE bucket >= 34200 AND bucket <= 57600),
f AS (
  SELECT date, ticker,
         max_by(vwap, bucket) FILTER (bucket <= 54000) AS p1500,
         max_by(vwap, bucket) FILTER (bucket <= 57540) AS p1559,
         max_by(vwap, bucket) FILTER (bucket <= 57600) AS p1600,
         count(*) FILTER (bucket > 54000 AND bucket <= 57540) AS nb60k59,
         count(*) FILTER (bucket > 54000 AND bucket <= 57600) AS nb_lh,
         sum(vwap*volume) FILTER (bucket > 54000 AND bucket <= 57540) AS dv_lh59,
         sum(vwap*volume) FILTER (bucket > 54000 AND bucket <= 57600) AS dv_lh,
         sum(vwap*volume) FILTER (bucket >= 34200 AND bucket < 34500) AS dvO5,
         sum(vwap*volume) FILTER (bucket >= 34500 AND bucket < 35100) AS dvO15,
         sum(vwap*volume) FILTER (bucket >= 35100 AND bucket < 36000) AS dvO30,
         count(*) FILTER (bucket >= 34200 AND bucket < 34500) AS nbO5,
         count(*) FILTER (bucket >= 34500 AND bucket < 35100) AS nbO15,
         count(*) FILTER (bucket >= 35100 AND bucket < 36000) AS nbO30,
         sum(vwap*volume) FILTER (bucket > 57540 AND bucket <= 57600) / nullif(sum(volume) FILTER (bucket > 57540 AND bucket <= 57600), 0) AS px_lim_1559_1600,
         count(*) FILTER (bucket > 57540 AND bucket <= 57600) AS nb_lastmin,
         sum(volume) FILTER (bucket > 57540 AND bucket <= 57600) AS vol_lastmin
  FROM b GROUP BY 1, 2),
s AS (
  SELECT date, ticker, bucket//30 AS slot, sum(vwap*volume)/nullif(sum(volume), 0) AS sv
  FROM b WHERE bucket < 57540 AND volume > 0 AND vwap > 0 GROUP BY 1, 2, 3),
r AS (
  SELECT date, ticker, slot, lag(slot) OVER w AS pslot, ln(sv / lag(sv) OVER w) AS lr
  FROM s WINDOW w AS (PARTITION BY date, ticker ORDER BY slot)),
v AS (
  SELECT date, ticker,
         avg(abs(lr)) FILTER (slot >= 1140 AND slot < 1200 AND pslot >= 1140) AS volat_open30,
         count(lr)    FILTER (slot >= 1140 AND slot < 1200 AND pslot >= 1140) AS nsl_open30
  FROM r GROUP BY 1, 2)
SELECT f.date, f.ticker, f.p1500, f.p1559, f.p1600, f.nb60k59, f.nb_lh, f.dv_lh59, f.dv_lh,
       f.dvO5, f.dvO15, f.dvO30, f.nbO5, f.nbO15, f.nbO30, f.px_lim_1559_1600, f.nb_lastmin, f.vol_lastmin,
       v.volat_open30, v.nsl_open30
FROM f LEFT JOIN v ON v.date = f.date AND v.ticker = f.ticker""")
    if (i // args.batch) % 10 == 0:
        n = con.execute("SELECT count(*) FROM feat").fetchone()[0]; el = time.time() - t0; done = i + len(chunk)
        print(f"  {done:>5}/{len(files)} files  {n:>12,} rows  {el:>6.0f}s  eta {el/done*(len(files)-done):.0f}s", flush=True)
print(f"scan done in {time.time()-t0:.0f}s", flush=True)
con.execute(f"""
COPY (
  SELECT u.ticker, u.date, u.dv_0945_tape, u.n_bars_1s, u.close_d, u.open_p1, u.div_p1,
         f.p1500, f.p1559, f.p1600, f.nb60k59, f.nb_lh, f.dv_lh59, f.dv_lh, f.dvO5, f.dvO15, f.dvO30, f.nbO5, f.nbO15, f.nbO30,
         f.px_lim_1559_1600, f.nb_lastmin, f.vol_lastmin, f.volat_open30, f.nsl_open30,
         f.p1559 / nullif(f.p1500, 0) - 1 AS chg60k59,
         3540 - f.nb60k59 AS gaps,
         f.dv_lh59 / nullif(f.dvO5 + f.dvO15 + f.dvO30, 0) AS inten59,
         f.dv_lh   / nullif(f.dvO5 + f.dvO15 + f.dvO30, 0) AS inten60,
         (f.nb60k59 / 3540.0) / nullif((f.nbO5 + f.nbO15 + f.nbO30) / 1800.0, 0) AS pers59,
         (f.nb_lh / 3600.0)   / nullif((f.nbO5 + f.nbO15 + f.nbO30) / 1800.0, 0) AS pers60,
         f.dv_lh / nullif(f.dvO5 + f.dvO15, 0) AS dv_over_open15,
         (u.open_p1 + u.div_p1) / nullif(f.px_lim_1559_1600, 0) - 1 AS ovn_from_lim59
  FROM db.mr_candidate_1s_v2 u
  JOIN feat f ON f.ticker = u.ticker AND f.date = u.date
  WHERE u.open_p1 IS NOT NULL AND u.close_d > 0 AND f.p1500 IS NOT NULL AND f.p1600 IS NOT NULL
) TO '{args.out}' (FORMAT PARQUET, COMPRESSION ZSTD)""")
print(con.execute(f"SELECT count(*), min(date), max(date), count(DISTINCT ticker) FROM read_parquet('{args.out}')").fetchall(), flush=True)
print(f"wrote {args.out} in {time.time()-t0:.0f}s", flush=True)
