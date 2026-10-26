"""Per ticker-day ACTIVITY counts from the 1s corpus (2026-10-26, user: "a bar count filter over the past 20 days" — NIVF's
one 58M-share day lifted its dv20 / avgvol20 while it traded on few seconds of most days).

For each day of data/intraday_1s_slim/ (the marker-free corpus), per ticker:
  rth_bars    1s bars with volume > 0 in [09:30, 16:00) ET   (bucket 34200 … 57599; 23,400 possible)
  pm_bars     the same in [04:00, 09:30)
  rth_trades  Σ trade_count in RTH
  rth_volume  Σ volume in RTH
→ data/equity/daily_bars/YYYY-MM-DD.parquet (one file a day, resumable: a day already written is skipped). Day D's row
uses only day D: the 20-session windows over it are taken by the reader, over the PRIOR sessions only.

  cd research && python -u scripts/equity/build_daily_bars.py > logs/daily_bars.log 2>&1; echo exit $?
"""
import glob, os, sys, time
import duckdb

SRC, OUT = "data/intraday_1s_slim", "data/equity/daily_bars"
os.makedirs(OUT, exist_ok=True)
con = duckdb.connect()
con.execute("set memory_limit='3GB'; set threads=8; set enable_progress_bar=false")
days = sorted(os.path.basename(f)[:-8] for f in glob.glob(f"{SRC}/????-??-??.parquet"))
todo = [d for d in days if not os.path.exists(f"{OUT}/{d}.parquet")]
print(f"days {len(days)}  to do {len(todo)}", flush=True)
t0 = time.time()
for i, d in enumerate(todo):
    con.execute(f"""
        copy (select ticker,
                     count(*) filter (where bucket >= 34200 and bucket < 57600)::INTEGER rth_bars,
                     count(*) filter (where bucket >= 14400 and bucket < 34200)::INTEGER pm_bars,
                     coalesce(sum(trade_count) filter (where bucket >= 34200 and bucket < 57600), 0)::BIGINT rth_trades,
                     coalesce(sum(volume) filter (where bucket >= 34200 and bucket < 57600), 0)::DOUBLE rth_volume
              from read_parquet('{SRC}/{d}.parquet') where volume > 0 group by ticker order by ticker)
        to '{OUT}/{d}.parquet.tmp' (format parquet, compression zstd)""")
    os.replace(f"{OUT}/{d}.parquet.tmp", f"{OUT}/{d}.parquet")
    if i % 100 == 0 or i == len(todo) - 1:
        el = time.time() - t0
        print(f"{i + 1:5}/{len(todo)} {d}  {el:7.0f}s  eta {el / (i + 1) * (len(todo) - i - 1) / 60:5.1f} min", flush=True)
print("done", flush=True)
