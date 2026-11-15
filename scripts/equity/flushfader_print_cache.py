"""The PRINT CACHE for the entry fill sims (user 2026-11-14: "Could you find a way to speed up these evals?").

Profiled on 100 book trips: reading a trip's prints from the day's 2.5 GB bulk trades file takes ~226 ms, its cached
quotes 2 ms, the simulation itself a few ms — the whole-book sims (flushfader_cap_sim.py, flushfader_fallback_sim.py)
spent ~2 h re-reading the tape. This writes, ONCE, every book trip's fill-eligible prints (the same exclusions as the
sims; the window from 5 s before the signal to 2 s after the exit) to one parquet per day:

    data/equity/flushfader_cap_sim/prints/<date>.parquet   (key, ts, price, size, lit)   key = symbol_date_signalsec

One DuckDB scan per day file (a range join of the day's trip windows), days in parallel processes. Resumable (a day's
file is written last, via .tmp). Run from research/:
    python -u scripts/equity/flushfader_print_cache.py > logs/flushfader_print_cache.log 2>&1; echo exit $?
"""
import argparse, datetime, os, time, zoneinfo
from multiprocessing import Pool
import pandas as pd, duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--book", default="data/flushfader_gate_review/broad_reference_book.parquet")
ap.add_argument("--trades", default="data/bulk/trades")
ap.add_argument("--out", default="data/equity/flushfader_cap_sim/prints")
ap.add_argument("--workers", type=int, default=4)
ap.add_argument("--mem", default="1500MB", help="DuckDB memory_limit per worker")
args = ap.parse_args()
NY = zoneinfo.ZoneInfo("America/New_York")
EXCL = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53, 8, 15, 19]::UTINYINT[]"
def ns_at(d, sec):
    y, m, dd = map(int, d.split("-")); return int(datetime.datetime(y, m, dd, tzinfo=NY).timestamp() * 1e9) + int(sec * 1e9)

def day(job):
    d, g = job
    path = os.path.join(args.out, f"{d}.parquet")
    if os.path.exists(path): return d, -1
    W = pd.DataFrame({"key": g.symbol + "_" + d + "_" + g.signal_sec.astype(str), "ticker": g.symbol,
                      "lo": [ns_at(d, s - 5) for s in g.signal_sec], "hi": [ns_at(d, s + 2) for s in g.exit_sec]})
    con = duckdb.connect(); con.execute(f"SET memory_limit='{args.mem}'"); con.execute("SET threads=2")
    con.register("w", W)
    lst = ", ".join(f"'{s}'" for s in W.ticker.unique())
    con.execute(f"""COPY (
        SELECT w.key, t.sip_timestamp AS ts, t.price, t.size::INTEGER AS size, t.exchange <> 4 AS lit
        FROM read_parquet('{os.path.join(args.trades, d + ".parquet")}') t
        JOIN w ON t.ticker = w.ticker AND t.sip_timestamp >= w.lo AND t.sip_timestamp < w.hi
        WHERE t.ticker IN ({lst}) AND t.size > 0 AND NOT list_has_any(t.conditions, {EXCL})
        ORDER BY w.key, t.sip_timestamp) TO '{path}.tmp' (FORMAT parquet)""")
    n = con.execute(f"SELECT count(*) FROM read_parquet('{path}.tmp')").fetchone()[0]
    con.close(); os.replace(path + ".tmp", path)
    return d, n

if __name__ == "__main__":
    os.makedirs(args.out, exist_ok=True)
    B = duckdb.sql(f"select symbol, trade_date, signal_sec, exit_sec from '{args.book}'").df()
    jobs = [(d, g) for d, g in B.groupby("trade_date")]
    T0 = time.time(); done = 0
    with Pool(args.workers) as p:
        for d, n in p.imap_unordered(day, jobs):
            done += 1
            if done % 100 == 0 or done == len(jobs): print(f"[{time.time() - T0:7.1f}s] {done:,}/{len(jobs):,} days (last {d}: {n:,} prints)", flush=True)
    print("done", flush=True)
