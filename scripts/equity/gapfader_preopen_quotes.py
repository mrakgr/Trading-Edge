"""GapFader GF-19, the download (user 2026-10-17: "download just the 5 mins of data before every open for the past couple of
trading days" — can the premarket QUOTES tell where a stock will open, before the 09:25 cutoff?). For every universe
ticker (CS/ADRC, the census's exclusions) with a daily bar on the day: the NBBO STANDING at --from-et (the last quote at
or after 04:00 and before it) and every NBBO update from --from-et to --to-et, from Massive's REST v3/quotes (the key in
the Authorization header, never in a URL; never printed). One parquet a day, written whole; a day already written is
skipped (resume).
  data/equity/gapfader/preopen_quotes/<date>.parquet: ticker, ts (SIP ns, UTC), bid, bid_size, ask, ask_size,
  bid_exchange, ask_exchange, standing (true = the quote standing at --from-et)
Run from research/:
  python -u scripts/equity/gapfader_preopen_quotes.py --days 10 > data/equity/gapfader/preopen_quotes.log 2>&1; echo exit $?
"""
import argparse, asyncio, datetime as dt, json, os, re, sys, time, urllib.parse
from zoneinfo import ZoneInfo
import aiohttp, duckdb, pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument("--days", type=int, default=10, help="the last N trading days in data/v2/daily_adjusted")
ap.add_argument("--dates", default="", help="explicit yyyy-mm-dd,… (overrides --days)")
ap.add_argument("--from-et", default="09:25")
ap.add_argument("--to-et", default="09:30")
ap.add_argument("--conc", type=int, default=48)
ap.add_argument("--tickers", default="", help="only these (a test)")
ap.add_argument("--out-dir", default="data/equity/gapfader/preopen_quotes")
a = ap.parse_args()
T0 = time.time()
def log(*x): print(f"[{time.time()-T0:7.1f}s]", *x, flush=True)
ET = ZoneInfo("America/New_York")
KEY = json.load(open(os.path.expanduser("~/.config/massive/credentials")))["massive_api_key"]
BASE = "https://api.massive.com/v3/quotes/"
os.makedirs(a.out_dir, exist_ok=True)

# ---------------------------------------------------------------- the universe (gapfader_census.py, mirrored)
CEF = {"CCD", "CGO", "CHI", "CHW", "CHY", "CPZ", "CSQ", "HERZ", "SDHY", "VCX", "RVII", "BPRE", "CFND"}
RX = {k: re.compile(p, re.I) for k, p in {
    "warrant": r"\bwarrants?\b|\(adw\)|\bwts?\b", "rights": r"\brights\b", "units": r"\bunits?\b|\buts\b",
    "equity_units": r"common units|\bcom (uts|units?)\b|limited partner|ltd partner|partnership|\bpartners\b|\bl\.? ?p\.? (uts|units?)\b|units of bene|\btr (uts|units?)\b|\btrust\b.*\bunits?\b|paired share|\bads\b|american depositary|ltd liability|^unit corp",
    "preferred": r"\bpreferred (stock|shares?|units?|securities)\b|\bcumulative\b|\bperpetual\b|depositary shares.*\bpreferred\b",
    "adr": r"american depositary"}.items()}
def excluded(t, name):
    name = name or ""
    if t in CEF: return True
    if t.endswith(".WS") or RX["warrant"].search(name): return True
    if t.endswith(".RT") or RX["rights"].search(name): return True
    if RX["preferred"].search(name) and not RX["adr"].search(name): return True
    if t.endswith(".U") or (RX["units"].search(name) and not RX["equity_units"].search(name)): return True
    return False
con = duckdb.connect()
ref = con.execute("SELECT ticker, any_value(coalesce(name,'')) AS name FROM 'data/v2/ticker_reference.parquet' WHERE type IN ('CS','ADRC') GROUP BY ticker").df()
con.register("uni", pd.DataFrame({"ticker": [t for t, n in zip(ref.ticker, ref.name) if not excluded(t, n)]}))
if a.dates:
    dates = a.dates.split(",")
else:
    dates = [str(d) for (d,) in con.execute(f"SELECT DISTINCT date FROM 'data/v2/daily_adjusted.parquet' ORDER BY date DESC LIMIT {a.days}").fetchall()][::-1]
log(f"days {dates[0]} … {dates[-1]} ({len(dates)}), window {a.from_et}–{a.to_et} ET")

def ns(d: str, hhmm: str):
    h, m = map(int, hhmm.split(":"))
    return int(dt.datetime(*map(int, d.split("-")), h, m, tzinfo=ET).timestamp()) * 1_000_000_000

COLS = ["ticker", "ts", "bid", "bid_size", "ask", "ask_size", "bid_exchange", "ask_exchange", "standing"]
def rows(t, results, standing):
    return [(t, r.get("sip_timestamp"), r.get("bid_price"), r.get("bid_size"), r.get("ask_price"), r.get("ask_size"),
             r.get("bid_exchange"), r.get("ask_exchange"), standing) for r in results]

async def get(session, url, params, stats):
    for attempt in range(8):
        try:
            async with session.get(url, params=params) as r:
                if r.status == 200: return await r.json()
                if r.status == 404: return {"results": []}
                if r.status in (429, 500, 502, 503, 504):
                    stats["retry"] += 1; await asyncio.sleep(min(10.0, 0.25 * 2 ** attempt)); continue
                raise RuntimeError(f"HTTP {r.status} for {url.split('?')[0]}: {(await r.text())[:200]}")
        except (aiohttp.ClientError, asyncio.TimeoutError):
            stats["retry"] += 1; await asyncio.sleep(min(10.0, 0.25 * 2 ** attempt))
    raise RuntimeError(f"gave up on {url.split('?')[0]}")

async def ticker_day(session, d, t, w0, w1, pm0, stats):
    url = BASE + urllib.parse.quote(t, safe="")
    out = []
    j = await get(session, url, {"timestamp.gte": pm0, "timestamp.lt": w0, "order": "desc", "sort": "timestamp", "limit": 1}, stats)
    out += rows(t, j.get("results") or [], True)
    j = await get(session, url, {"timestamp.gte": w0, "timestamp.lt": w1, "order": "asc", "sort": "timestamp", "limit": 50000}, stats)
    out += rows(t, j.get("results") or [], False)
    while j.get("next_url"):                                 # the cursor carries the query; the key stays in the header
        stats["pages"] += 1
        j = await get(session, j["next_url"], None, stats)
        out += rows(t, j.get("results") or [], False)
    return out

async def main():
    hdr = {"Authorization": "Bearer " + KEY}
    timeout = aiohttp.ClientTimeout(total=120)
    async with aiohttp.ClientSession(headers=hdr, timeout=timeout, connector=aiohttp.TCPConnector(limit=a.conc)) as session:
        for d in dates:
            path = os.path.join(a.out_dir, f"{d}.parquet")
            if os.path.exists(path): log(f"{d}: exists, skipped"); continue
            tickers = [t for (t,) in con.execute(f"SELECT DISTINCT ticker FROM 'data/v2/daily_adjusted.parquet' JOIN uni USING (ticker) WHERE date = '{d}' ORDER BY 1").fetchall()]
            if a.tickers: tickers = [t for t in tickers if t in a.tickers.split(",")]
            w0, w1, pm0 = ns(d, a.from_et), ns(d, a.to_et), ns(d, "04:00")
            stats = {"retry": 0, "pages": 0}
            q = asyncio.Queue()
            for t in tickers: q.put_nowait(t)
            got, done = [], [0]
            async def worker():
                while True:
                    try: t = q.get_nowait()
                    except asyncio.QueueEmpty: return
                    got.extend(await ticker_day(session, d, t, w0, w1, pm0, stats))
                    done[0] += 1
                    if done[0] % 1000 == 0: log(f"{d}: {done[0]:,}/{len(tickers):,} tickers, {len(got):,} quotes")
            await asyncio.gather(*[worker() for _ in range(a.conc)])
            df = pd.DataFrame(got, columns=COLS)
            df.to_parquet(path + ".tmp", index=False); os.replace(path + ".tmp", path)
            log(f"{d}: {len(tickers):,} tickers, {len(df):,} quotes ({df.standing.sum():,} standing), {stats['pages']} extra pages, {stats['retry']} retries -> {path}")

asyncio.run(main())
log("done")
