"""Snoozer entries, CAPPED vs UNCAPPED maker held until 16:10 (user 2026-10-12) — a quote-driven fill simulation.

For each trade of data/flushfader_gate_review/snoozer_reference_book.parquet (1,587, 2016-08 .. 2026-08) the order
lives from the end of the signal second to END (16:10 default) and is worked on the NBBO (Massive REST /v3/quotes,
cached per trade) against the lit trades of the raw tape (trf_id = 0, the trade_filters.py exclusions, no closing
cross, no official-close reports):

  capped    a buy at onGrid(min(bid, cap)), a short at onGrid(max(ask, cap)); cap = the signal price (p1559) — the
            Oms's MakeSpreadRth with the entry cap (Executor.legOf), extended past 16:00
  uncapped  a buy at the bid, a short at the ask (onGrid) — MakeSpreadRth without the cap

Fill rule (conservative; queue-aware without venue depth):
  · a lit print strictly THROUGH our price fills us at our price;
  · a lit print AT our price eats the queue ahead of us; we fill when it is gone. The queue ahead is the displayed
    NBBO size at our price when we joined it (all venues — conservative: our venue's share is smaller);
  · our price moves whenever the policy's price moves (a new quote) — back of the new level's queue;
  · resting BELOW the bid (a capped buy under a higher bid): the queue ahead is unknown until the bid comes down to our
    price, then it is the bid size at that moment.
Outcome: ovn = (open_p1 + div_p1) / fill − 1 (a short negated), weighted by the cell weight as the book.

Run from research/ (the quote fetch is cached; re-runs are fast):
    python -u scripts/equity/snoozer_maker_sim.py > data/equity/snoozer_maker_sim.log 2>&1; echo exit $?
"""
import argparse, datetime, json, math, os, time, urllib.request, zoneinfo
from concurrent.futures import ThreadPoolExecutor
import numpy as np, pandas as pd, duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--book", default="data/flushfader_gate_review/snoozer_reference_book.parquet")
ap.add_argument("--trades", default="data/bulk/trades")
ap.add_argument("--cache", default="data/equity/snoozer_quotes")
ap.add_argument("--out", default="data/equity/snoozer_maker_sim.parquet")
ap.add_argument("--end", type=int, default=58200, help="the order's last second, ET (58200 = 16:10)")
ap.add_argument("--workers", type=int, default=8)
ap.add_argument("--limit", type=int, default=0)
args = ap.parse_args()
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, flush=True)
NY = zoneinfo.ZoneInfo("America/New_York")
def ns_at(d: str, sec: int) -> int:
    y, m, dd = map(int, d.split("-"))
    return int(datetime.datetime(y, m, dd, tzinfo=NY).timestamp() * 1e9) + sec * 1_000_000_000

B = pd.read_parquet(args.book).reset_index(drop=True)
B["signal_sec"] = B.signal_sec.astype(int)
if args.limit: B = B.head(args.limit)
os.makedirs(args.cache, exist_ok=True)

# ---------------------------------------------------------------- 1. the quotes (cached; the key never printed)
KEY = json.load(open(os.path.expanduser("~/.config/massive/credentials")))["massive_api_key"]
def fetch(r):
    path = os.path.join(args.cache, f"{r.symbol}_{r.trade_date}.parquet")
    if os.path.exists(path): return path, 0
    url = (f"https://api.polygon.io/v3/quotes/{r.symbol}?timestamp.gte={ns_at(r.trade_date, 57540)}"
           f"&timestamp.lt={ns_at(r.trade_date, args.end)}&limit=50000&sort=timestamp&order=asc")
    rows, pages = [], 0
    while url:
        for attempt in range(5):
            try:
                req = urllib.request.Request(url, headers={"Authorization": f"Bearer {KEY}"})
                d = json.load(urllib.request.urlopen(req, timeout=120)); break
            except Exception as e:
                if attempt == 4: raise RuntimeError(f"{r.symbol} {r.trade_date}: {type(e).__name__}")
                time.sleep(2 * (attempt + 1))
        rows += [(q.get("sip_timestamp"), q.get("bid_price"), q.get("bid_size"), q.get("ask_price"), q.get("ask_size")) for q in d.get("results", [])]
        url = d.get("next_url"); pages += 1
    pd.DataFrame(rows, columns=["ts", "bid", "bid_size", "ask", "ask_size"]).to_parquet(path, index=False)
    return path, pages
todo = [r for r in B.itertuples() if not os.path.exists(os.path.join(args.cache, f"{r.symbol}_{r.trade_date}.parquet"))]
log(f"quotes: {len(B) - len(todo):,} cached, fetching {len(todo):,}")
with ThreadPoolExecutor(args.workers) as ex:
    for i, (p, pages) in enumerate(ex.map(fetch, todo)):
        if i % 200 == 0: log(f"  fetched {i+1}/{len(todo)}")

# ---------------------------------------------------------------- 2. the lit trades per trade window
EXCL = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53]::UTINYINT[]"
con = duckdb.connect(); con.execute("SET memory_limit='6GB'"); con.execute("SET threads=6")
def tape_for(d, syms):
    path = os.path.join(args.trades, f"{d}.parquet")
    lst = ", ".join(f"'{s}'" for s in syms)
    return con.execute(f"""
        SELECT ticker, sip_timestamp AS ts, price, size FROM read_parquet('{path}')
        WHERE ticker IN ({lst}) AND size > 0 AND trf_id = 0
          AND NOT list_has_any(conditions, {EXCL}) AND NOT list_has_any(conditions, [8, 19, 15]::UTINYINT[])
          AND sip_timestamp >= {ns_at(d, 57540)} AND sip_timestamp < {ns_at(d, args.end)}
        ORDER BY ts""").df()

def on_grid(buy, px):
    tick = 0.01 if px >= 1.0 else 0.0001
    n = math.floor(px / tick + 1e-9) if buy else math.ceil(px / tick - 1e-9)
    return round(n * tick, 4)

def simulate(buy, cap, Q, X, t_start):
    """one order from t_start: returns (fill_ts, fill_px) or (None, nan). Q quotes (ts, bid, bid_size, ask, ask_size), X lit trades (ts, price, size)."""
    # merge the two streams by time; a quote at the same ns as a trade is applied first
    qi = np.searchsorted(Q.ts.values, t_start, side="right") - 1      # the quote standing at t_start
    if qi < 0: qi = 0
    qts, qb, qbs, qa, qas = Q.ts.values, Q.bid.values, Q.bid_size.values, Q.ask.values, Q.ask_size.values
    xts, xp, xs = X.ts.values, X.price.values, X["size"].values
    xi = np.searchsorted(xts, t_start, side="left")
    price = None; ahead = math.inf
    def touch(i):            # our side's NBBO price and size at quote i
        return (qb[i], qbs[i]) if buy else (qa[i], qas[i])
    def want(i):
        t_px, _ = touch(i)
        if not (t_px > 0): return None
        if cap is None: return on_grid(buy, t_px)
        return on_grid(buy, min(t_px, cap) if buy else max(t_px, cap))
    def reprice(i):
        nonlocal price, ahead
        w = want(i)
        if w is None: return
        t_px, t_sz = touch(i)
        if w != price:
            price = w
            ahead = t_sz if abs(t_px - w) < 1e-9 else math.inf
        elif math.isinf(ahead) and abs(t_px - w) < 1e-9:
            ahead = t_sz                                        # the touch came to our resting price
    reprice(qi)
    qn = qi + 1
    while True:
        tq = qts[qn] if qn < len(qts) else None
        tx = xts[xi] if xi < len(xts) else None
        if tq is None and tx is None: return None, math.nan
        if tx is None or (tq is not None and tq <= tx):
            reprice(qn); qn += 1; continue
        p, s = xp[xi], xs[xi]; xi += 1
        if price is None: continue
        through = (p < price - 1e-9) if buy else (p > price + 1e-9)
        if through: return tx, price
        if abs(p - price) < 1e-9 and not math.isinf(ahead):
            ahead -= s
            if ahead <= 0: return tx, price

# bid_size / ask_size: SHARES in the probes (AAPL 2017-03-01 200 / 1,100; HTZ 2026-08-06 286,400). A file whose median
# size is < 50 is taken to be in round lots (× 100) — counted in the report (`lots`).
rows = []
for d, bk in B.groupby("trade_date"):
    X_all = tape_for(d, bk.symbol.unique())
    for r in bk.itertuples():
        Q = pd.read_parquet(os.path.join(args.cache, f"{r.symbol}_{r.trade_date}.parquet")).dropna(subset=["ts"]).sort_values("ts")
        lots = len(Q) > 0 and Q.bid_size.median() < 50
        if lots:
            Q["bid_size"] *= 100; Q["ask_size"] *= 100
        X = X_all[X_all.ticker == r.symbol]
        buy = r.side == "long"; t0 = ns_at(d, r.signal_sec + 1)
        rec = {"symbol": r.symbol, "trade_date": d, "side": r.side, "cell": r.cell, "weight": r.weight, "p1559": r.p1559,
               "open_p1": r.open_p1, "div_p1": r.div_p1, "px_proxy": r.px_lim_1559_1600, "n_quotes": len(Q), "lots": lots}
        for lab, cap in (("capped", r.p1559), ("uncapped", None)):
            if len(Q) == 0:
                rec[f"{lab}_ts"], rec[f"{lab}_px"] = None, math.nan; continue
            ts, px = simulate(buy, cap, Q, X, t0)
            rec[f"{lab}_ts"] = ts; rec[f"{lab}_px"] = px
            rec[f"{lab}_sec"] = (ts - ns_at(d, 0)) / 1e9 if ts else math.nan
        rows.append(rec)
R = pd.DataFrame(rows); R.to_parquet(args.out, index=False); log(f"simulated {len(R):,} -> {args.out}")

# ---------------------------------------------------------------- 3. the report
def pf(x):
    x = x[~np.isnan(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
sgn = np.where(R.side == "long", 1.0, -1.0)
for lab in ("capped", "uncapped"):
    R[f"o_{lab}"] = sgn * ((R.open_p1 + R.div_p1) / R[f"{lab}_px"] - 1.0)
R["o_proxy"] = sgn * ((R.open_p1 + R.div_p1) / R.px_proxy - 1.0)
print(f"\nquote files {len(R):,}: empty {int((R.n_quotes == 0).sum())}, sizes in lots {int(R.lots.sum())}; quotes per trade p50 {R.n_quotes.median():,.0f}")
print(f"\n## Capped vs uncapped maker, held until {args.end // 3600}:{args.end % 3600 // 60:02d} (quote-driven, queue-aware)\n")
print("| side | policy | filled | by 16:00 | PF filled (weighted) | mean % filled | fill vs signal price (bp, + = better) | Σ weighted return | missed: n / mean % at the proxy |")
print("|---|---|---|---|---|---|---|---|---|")
for side in ("long", "short"):
    S = R[R.side == side]; w = S.weight.values
    for lab in ("capped", "uncapped"):
        f = S[f"{lab}_px"].notna().values; o = S[f"o_{lab}"].values
        by16 = (S[f"{lab}_sec"] < 57600).values
        slip = (S.p1559 / S[f"{lab}_px"] - 1.0) * 1e4 * (1 if side == "long" else -1)
        print(f"| {side} | {lab} | {f.mean()*100:.1f} % ({f.sum()}) | {by16.mean()*100:.1f} % | {pf(o[f]*w[f]):.2f} | {np.nanmean(o[f])*100:+.2f} | "
              f"{np.nanmedian(slip[f]):+.1f} | {np.nansum(o[f]*w[f])*100:,.0f} | {(~f).sum()} / {np.nanmean(S.o_proxy.values[~f])*100:+.2f} |")
print("\n## Where they differ\n")
print("| side | filled by | n | mean % (capped fill) | mean % (uncapped fill) |\n|---|---|---|---|---|")
for side in ("long", "short"):
    S = R[R.side == side]; c = S.capped_px.notna(); u = S.uncapped_px.notna()
    for lab, m in (("both", c & u), ("uncapped only", u & ~c), ("capped only", c & ~u), ("neither", ~c & ~u)):
        print(f"| {side} | {lab} | {int(m.sum())} | {np.nanmean(S.o_capped[m])*100 if (c & m).any() else float('nan'):+.2f} | {np.nanmean(S.o_uncapped[m])*100 if (u & m).any() else float('nan'):+.2f} |")
log("done")
