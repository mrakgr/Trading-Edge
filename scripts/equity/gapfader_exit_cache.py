"""GapFader's 10:00 EXIT — the data for the exit fill sim (user 2026-11-14).

Why: every system's maker exit is `MakeSpreadRthOrMoo` (a sell at the ask, following it both ways, uncapped); GapFader's
10:00 time stop uses it, and live those exits filled late (10:01–10:20; the private repo's docs/execution_reviews.md).
The user's NBBO-step rule (research §S49ap-2, adopted for entries 11-14: every 5 s without a fill one tick past the
visible quote) is to be tested as the exit too, with NO floor. This collects, ONCE, what that sim reads:

  the trades: GF-54's production book (data/equity/gapfader/model_v2.parquet, 38,052) whose target did NOT fill before
              10:00 (tf_50 >= 36000 in the sizing tape, the model's own rule) — 20,081 exits at 10:00.
  --phase quotes  Massive REST /v3/quotes per trade, 09:59:50 .. 10:30:00 ET → data/equity/gapfader/exit_sim/quotes/
                  <ticker>_<date>.parquet (ts, bid, bid_size, ask, ask_size). The key is read, never printed.
  --phase prints  the bulk tape over the same window, one DuckDB scan per day → data/equity/gapfader/exit_sim/prints/
                  <date>.parquet (key, ts, price, size, lit, fill_ok, bar_ok), sorted by (key, ts):
                    fill_ok = the fill sims' rule (conditions {2,7,10,13,20,21,22,29,32,52,53} and the closing /
                              official prints {8,15,19} excluded; size > 0)
                    bar_ok  = the 1s-bar corpus rule (build_all_1s_bars.fsx): the markers {15,16} dropped; the
                              auction prints {17,25,19,8} kept; else |sip − participant| <= 50 ms and not in the
                              exclude set — for the model's exit price, the 10:00:00 1s vwap (GF-54's x_36000)
Both phases resume (a file is written last, via .tmp). Run from research/, the phases in parallel:
    python -u scripts/equity/gapfader_exit_cache.py --phase quotes > logs/gapfader_exit_quotes.log 2>&1; echo exit $?
    python -u scripts/equity/gapfader_exit_cache.py --phase prints > logs/gapfader_exit_prints.log 2>&1; echo exit $?
"""
import argparse, datetime, json, os, time, urllib.request, zoneinfo
from concurrent.futures import ThreadPoolExecutor
from multiprocessing import Pool
import pandas as pd, duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--phase", required=True, choices=("quotes", "prints", "pre"))
ap.add_argument("--out", default="data/equity/gapfader/exit_sim")
ap.add_argument("--trades", default="data/bulk/trades")
ap.add_argument("--workers", type=int, default=8)
ap.add_argument("--mem", default="1500MB")
args = ap.parse_args()
NY = zoneinfo.ZoneInfo("America/New_York")
T0 = time.time()
def log(*a): print(f"[{time.time() - T0:7.1f}s]", *a, flush=True)
def ns_at(d, sec):
    y, m, dd = map(int, d.split("-")); return int(datetime.datetime(y, m, dd, tzinfo=NY).timestamp() * 1e9) + int(sec * 1e9)
LO, HI = 10 * 3600 - 10, 10 * 3600 + 1800          # 09:59:50 .. 10:30:00

def book():
    return duckdb.sql("""
        WITH V AS (SELECT ticker, date, tf_50 FROM 'data/equity/gapfader/sizing_tape_cut.parquet'
                   UNION ALL BY NAME SELECT ticker, date, tf_50 FROM 'data/equity/gapfader/sizing_tape_cut_ext.parquet')
        SELECT m.ticker, m.date FROM 'data/equity/gapfader/model_v2.parquet' m JOIN V USING (ticker, date)
        WHERE coalesce(V.tf_50, 1e9) >= 36000 ORDER BY m.date, m.ticker""").df()

# ---------------------------------------------------------------- quotes
KEY = None
def fetch(r):
    try: return _fetch(r)
    except Exception as e: print(f"  FAILED {e}", flush=True); return -1      # logged and left uncached: a rerun retries it
def _fetch(r):
    path = os.path.join(args.out, "quotes", f"{r.ticker}_{r.date}.parquet")
    if os.path.exists(path): return 0
    url = (f"https://api.massive.com/v3/quotes/{r.ticker}?timestamp.gte={ns_at(r.date, LO)}&timestamp.lt={ns_at(r.date, HI)}"
           f"&limit=50000&sort=timestamp&order=asc")
    rows, pages = [], 0
    while url:
        for attempt in range(5):
            try:
                d = json.load(urllib.request.urlopen(urllib.request.Request(url, headers={"Authorization": f"Bearer {KEY}"}), timeout=120)); break
            except Exception as e:
                if attempt == 4: raise RuntimeError(f"{r.ticker} {r.date}: {type(e).__name__}")
                time.sleep(2 * (attempt + 1))
        rows += [(q.get("sip_timestamp"), q.get("bid_price"), q.get("bid_size"), q.get("ask_price"), q.get("ask_size")) for q in d.get("results", [])]
        url = d.get("next_url"); pages += 1
    pd.DataFrame(rows, columns=["ts", "bid", "bid_size", "ask", "ask_size"]).to_parquet(path + ".tmp", index=False)
    os.replace(path + ".tmp", path)
    return pages

# ---------------------------------------------------------------- pre: the NBBO standing at 10:00
# (2026-11-14: 4,012 of the first 9,608 trades had no quote update in 09:59:50..10:00 — in thin names the standing quote
# can be minutes old.) For every trade whose window file has no quote at or before 10:00, the LAST quote at or before
# 10:00:00 (one request, sort desc, limit 1) → <out>/pre/<ticker>_<date>.parquet (the same columns, 0 or 1 row).
def fetch_pre(r):
    path = os.path.join(args.out, "pre", f"{r.ticker}_{r.date}.parquet")
    if os.path.exists(path): return 0
    url = (f"https://api.massive.com/v3/quotes/{r.ticker}?timestamp.lte={ns_at(r.date, 10 * 3600)}"
           f"&timestamp.gte={ns_at(r.date, 9 * 3600 + 30 * 60)}&limit=1&sort=timestamp&order=desc")
    try:
        for attempt in range(5):
            try:
                d = json.load(urllib.request.urlopen(urllib.request.Request(url, headers={"Authorization": f"Bearer {KEY}"}), timeout=60)); break
            except Exception as e:
                if attempt == 4: raise RuntimeError(f"{r.ticker} {r.date}: {type(e).__name__}")
                time.sleep(2 * (attempt + 1))
    except Exception as e:
        print(f"  FAILED {e}", flush=True); return -1
    rows = [(q.get("sip_timestamp"), q.get("bid_price"), q.get("bid_size"), q.get("ask_price"), q.get("ask_size")) for q in d.get("results", [])]
    pd.DataFrame(rows, columns=["ts", "bid", "bid_size", "ask", "ask_size"]).to_parquet(path + ".tmp", index=False)
    os.replace(path + ".tmp", path)
    return 1

# ---------------------------------------------------------------- prints
EXCL_FILL = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53, 8, 15, 19]::UTINYINT[]"
EXCL_BAR = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53]::UTINYINT[]"
def day(job):
    d, g = job
    path = os.path.join(args.out, "prints", f"{d}.parquet")
    if os.path.exists(path): return d, -1
    W = pd.DataFrame({"key": g.ticker + "_" + d, "ticker": g.ticker})
    con = duckdb.connect(); con.execute(f"SET memory_limit='{args.mem}'"); con.execute("SET threads=2")
    con.register("w", W)
    lst = ", ".join(f"'{s}'" for s in W.ticker.unique())
    con.execute(f"""COPY (
        SELECT key, ts, price, size, lit, fill_ok, bar_ok FROM (
          SELECT w.key, t.sip_timestamp AS ts, t.price, t.size::INTEGER AS size, t.exchange <> 4 AS lit,
                 (t.size > 0 AND NOT list_has_any(coalesce(t.conditions, []::UTINYINT[]), {EXCL_FILL})) AS fill_ok,
                 (t.size > 0 AND NOT list_has_any(coalesce(t.conditions, []::UTINYINT[]), [15, 16]::UTINYINT[]) AND
                  (list_has_any(coalesce(t.conditions, []::UTINYINT[]), [17, 25, 19, 8]::UTINYINT[]) OR
                   (abs(t.sip_timestamp // 1000000 - t.participant_timestamp // 1000000) <= 50
                    AND NOT list_has_any(coalesce(t.conditions, []::UTINYINT[]), {EXCL_BAR})))) AS bar_ok
          FROM read_parquet('{os.path.join(args.trades, d + ".parquet")}') t JOIN w ON t.ticker = w.ticker
          WHERE t.ticker IN ({lst}) AND t.sip_timestamp >= {ns_at(d, LO)} AND t.sip_timestamp < {ns_at(d, HI)})
        WHERE fill_ok OR bar_ok ORDER BY key, ts) TO '{path}.tmp' (FORMAT parquet)""")
    n = con.execute(f"SELECT count(*) FROM read_parquet('{path}.tmp')").fetchone()[0]
    con.close(); os.replace(path + ".tmp", path)
    return d, n

if __name__ == "__main__":
    B = book()
    log(f"{len(B):,} GapFader trades exit at 10:00 ({B.date.min()} .. {B.date.max()}); phase {args.phase}")
    if args.phase == "quotes":
        os.makedirs(os.path.join(args.out, "quotes"), exist_ok=True)
        KEY = json.load(open(os.path.expanduser("~/.config/massive/credentials")))["massive_api_key"]
        todo = [r for r in B.itertuples() if not os.path.exists(os.path.join(args.out, "quotes", f"{r.ticker}_{r.date}.parquet"))]
        log(f"quotes: {len(B) - len(todo):,} cached, fetching {len(todo):,}")
        with ThreadPoolExecutor(args.workers) as ex:
            for i, _ in enumerate(ex.map(fetch, todo)):
                if (i + 1) % 500 == 0: log(f"  fetched {i + 1:,}/{len(todo):,}")
    elif args.phase == "pre":
        os.makedirs(os.path.join(args.out, "pre"), exist_ok=True)
        KEY = json.load(open(os.path.expanduser("~/.config/massive/credentials")))["massive_api_key"]
        def needs(r):
            qp = os.path.join(args.out, "quotes", f"{r.ticker}_{r.date}.parquet")
            if not os.path.exists(qp): return False
            Q = pd.read_parquet(qp, columns=["ts"])
            return not (Q.ts <= ns_at(r.date, 10 * 3600)).any()
        todo = [r for r in B.itertuples() if needs(r) and not os.path.exists(os.path.join(args.out, "pre", f"{r.ticker}_{r.date}.parquet"))]
        log(f"pre: fetching the 10:00 standing quote for {len(todo):,} trades")
        with ThreadPoolExecutor(args.workers) as ex:
            for i, _ in enumerate(ex.map(fetch_pre, todo)):
                if (i + 1) % 1000 == 0: log(f"  fetched {i + 1:,}/{len(todo):,}")
    else:
        os.makedirs(os.path.join(args.out, "prints"), exist_ok=True)
        jobs = list(B.groupby("date"))
        with Pool(4) as p:
            for i, (d, n) in enumerate(p.imap_unordered(day, jobs)):
                if (i + 1) % 200 == 0 or i + 1 == len(jobs): log(f"  {i + 1:,}/{len(jobs):,} days (last {d}: {n:,} prints)")
    log("done")
