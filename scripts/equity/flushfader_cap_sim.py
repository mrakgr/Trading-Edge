"""FlushFader maker ENTRY CAPS on the research book (user 2026-10-30) — a quote-driven fill simulation at scale.

The live sessions' tape (the private repo's maker_sim/caps/sim7.py, 109 entries 10-14..10-29) found tight caps winning on
the real NBBO, but on too few entries for a rule. This runs the same order rule over 500 random trips per year of
data/flushfader_gate_review/broad_reference_book.parquet (2020-01 .. 2026-08):

  the order: a BUY of $1,000 / signal_vwap shares (our median live order is $813) that JOINS the NBBO bid and follows it
             both ways; after every re-pricing the OLD price stays live for LAT = 0.216 s (the measured path, 10-28).
             From the end of the signal second until the trip's exit (exit_sec); never above the cap C:
               none · signal (the signal vwap) · signal + 1 / 3 ticks · signal + 100 bp · ¼ / ½ of the way to ma30 · ma10 · ma15
             C is FIXED at the signal: from the 1s vwaps of the present seconds up to and including the signal second
             (all prints of the second — the book's own vwap, checked against the raw tape: 12 of 12 exact), floored to
             the tick. ⚠ The book's prices are on the RAW tape scale (the same check), so no adjustment enters.
  the fills: against the prints of the bulk tape (the trade_filters.py exclusions and the closing / official prints
             dropped). lit = not FINRA TRF (exchange 4). Three models (sim7's):
               lit    any lit print at or through our price fills us
               queue  the displayed bid size is ahead of us when we join the bid; a lit print at our price eats it first;
                      a print strictly through (lit), or a full tick through (any), fills; below the bid nobody is ahead
               hidden queue, and a price below the bid has the bid's displayed size ahead of it (pessimistic)
  the outcome: (exit_px − our fill) × shares filled — the book's model exit; the MODEL fills every trip at entry_px.

Quotes: Massive REST /v3/quotes per trip, from 60 s before the signal to the exit (cached: data/equity/flushfader_cap_sim/
quotes/). Sizes reported in round lots (a file whose median bid size is < 50) are × 100, counted.

Run from research/ (fetch first; both phases resume from the cache):
    python -u scripts/equity/flushfader_cap_sim.py > logs/flushfader_cap_sim.log 2>&1; echo exit $?
"""
import argparse, datetime, json, math, os, re, time, urllib.request, zoneinfo
from concurrent.futures import ThreadPoolExecutor
import numpy as np, pandas as pd, duckdb
from numba import njit

ap = argparse.ArgumentParser()
ap.add_argument("--book", default="data/flushfader_gate_review/broad_reference_book.parquet")
ap.add_argument("--trades", default="data/bulk/trades")
ap.add_argument("--out", default="data/equity/flushfader_cap_sim")
ap.add_argument("--quotes", help="the quote cache (default <out>/quotes; share it between samples: a larger sample with the same seed contains the smaller)")
ap.add_argument("--per-year", type=int, default=500)
ap.add_argument("--seed", type=int, default=20261030)
ap.add_argument("--notional", type=float, default=1000.0)
ap.add_argument("--lat", type=float, default=0.216)
ap.add_argument("--workers", type=int, default=8)
ap.add_argument("--phase", choices=("fetch", "sim", "all"), default="all")
ap.add_argument("--only", help="simulate only the caps whose name matches this regex ('none' always)")
args = ap.parse_args()
T0 = time.time()
def log(*a): print(f"[{time.time() - T0:7.1f}s]", *a, flush=True)
NY = zoneinfo.ZoneInfo("America/New_York")
def ns_at(d: str, sec: float) -> int:
    y, m, dd = map(int, d.split("-"))
    return int(datetime.datetime(y, m, dd, tzinfo=NY).timestamp() * 1e9) + int(sec * 1e9)

# ---------------------------------------------------------------- 0. the sample: per-year random, reproducible
B = duckdb.sql(f"select * from '{args.book}'").df()
B["year"] = B.trade_date.str[:4]
# every year at least --per-year: groupby().sample (one RandomState across the years, so a larger n with the same seed
# contains the smaller sample); a smaller book (LowFader, 12–49 a year) is taken whole
S = (B.groupby("year").sample(n=args.per_year, random_state=args.seed) if B.year.value_counts().min() >= args.per_year else B.copy())
S = (S
     .sort_values(["trade_date", "symbol", "signal_sec"]).reset_index(drop=True))
S["key"] = S.symbol + "_" + S.trade_date + "_" + S.signal_sec.astype(str)
qdir = args.quotes or os.path.join(args.out, "quotes"); os.makedirs(qdir, exist_ok=True); os.makedirs(args.out, exist_ok=True)
log(f"sample: {len(S):,} trips of {len(B):,} ({S.year.value_counts().sort_index().to_dict()})")

# ---------------------------------------------------------------- 1. the quotes (cached; the key never printed)
KEY = json.load(open(os.path.expanduser("~/.config/massive/credentials")))["massive_api_key"]
def fetch(r):
    path = os.path.join(qdir, f"{r.key}.parquet")
    if os.path.exists(path): return 0
    url = (f"https://api.massive.com/v3/quotes/{r.symbol}?timestamp.gte={ns_at(r.trade_date, r.signal_sec - 60)}"
           f"&timestamp.lt={ns_at(r.trade_date, r.exit_sec + 2)}&limit=50000&sort=timestamp&order=asc")
    rows, pages = [], 0
    while url:
        for attempt in range(5):
            try:
                req = urllib.request.Request(url, headers={"Authorization": f"Bearer {KEY}"})
                d = json.load(urllib.request.urlopen(req, timeout=120)); break
            except Exception as e:
                if attempt == 4: raise RuntimeError(f"{r.key}: {type(e).__name__}")
                time.sleep(2 * (attempt + 1))
        rows += [(q.get("sip_timestamp"), q.get("bid_price"), q.get("bid_size"), q.get("ask_price"), q.get("ask_size")) for q in d.get("results", [])]
        url = d.get("next_url"); pages += 1
    tmp = path + ".tmp"
    pd.DataFrame(rows, columns=["ts", "bid", "bid_size", "ask", "ask_size"]).to_parquet(tmp, index=False)
    os.replace(tmp, path)
    return pages
if args.phase in ("fetch", "all"):
    todo = [r for r in S.itertuples() if not os.path.exists(os.path.join(qdir, f"{r.key}.parquet"))]
    log(f"quotes: {len(S) - len(todo):,} cached, fetching {len(todo):,}")
    with ThreadPoolExecutor(args.workers) as ex:
        for i, _ in enumerate(ex.map(fetch, todo)):
            if (i + 1) % 100 == 0: log(f"  fetched {i + 1:,}/{len(todo):,}")
    log("quotes: done")
if args.phase == "fetch": raise SystemExit(0)

# ---------------------------------------------------------------- 2. the simulation
EXCL = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53, 8, 15, 19]::UTINYINT[]"
con = duckdb.connect(); con.execute("SET memory_limit='4GB'"); con.execute("SET threads=4")
def tape_for(d, g):
    lst = ", ".join(f"'{s}'" for s in g.symbol.unique())
    lo, hi = ns_at(d, g.signal_sec.min() - 1800), ns_at(d, g.exit_sec.max() + 2)
    return con.execute(f"""
        SELECT ticker, sip_timestamp AS ts, price, size, exchange <> 4 AS lit, NOT list_has_any(conditions, {EXCL}) AS ok
        FROM read_parquet('{os.path.join(args.trades, d + ".parquet")}')
        WHERE ticker IN ({lst}) AND size > 0 AND sip_timestamp >= {lo} AND sip_timestamp < {hi}
        ORDER BY ts""").df()

@njit(cache=True)
def run(et, ets, eb, ebs, ea, ep, es, elit, i0, live_from, end, cap, tick, n, fm, lat, bid0, bsz0):
    """sim7's current rule under a cap: (shares filled, cost, seconds to the last fill, re-pricings). et 0 = quote, 1 = print"""
    def queue_for(px, b, bs):
        if fm == 0: return 0.0
        if px < b - 1e-9: return bs if fm == 2 else 0.0
        if abs(px - b) > 1e-9: return 0.0
        return bs
    px = min(bid0, cap); qa = queue_for(px, bid0, bsz0)
    got = 0.0; cost = 0.0; tfill = -1.0; moves = 0; pend_t = -1.0; pend_px = 0.0; lb = bid0; lbs = bsz0
    for i in range(i0, len(et)):
        ts = ets[i]
        if ts > end or got >= n: break
        if pend_t >= 0 and ts >= pend_t:
            px = pend_px; pend_t = -1.0; qa = queue_for(px, lb, lbs)
        if et[i] == 0:
            b = eb[i]; a = ea[i]
            if b <= 0 or a <= 0: continue
            lb = b; lbs = ebs[i]
            nx = min(b, cap); cur = pend_px if pend_t >= 0 else px
            if abs(nx - cur) > 1e-9:
                if pend_t < 0: pend_t = ts + lat
                pend_px = nx; moves += 1
        else:
            if ts < live_from: continue
            p = ep[i]; q = es[i]; lit = elit[i]
            through = (lit and p < px - 1e-9) or p <= px - tick + 1e-9
            f = 0.0
            if fm == 0:
                if lit and p <= px + 1e-9: f = q
            elif through: f = q
            elif lit and abs(p - px) < 1e-9:
                eat = min(qa, q); qa -= eat; f = q - eat
            if f > 0:
                g = min(f, n - got); got += g; cost += g * px; tfill = ts
    return got, cost, tfill, moves

TICKS, BPS, MAS = (1, 2, 3, 4), (50, 100, 150, 200, 250, 300), range(2, 16)
# every cap is rounded DOWN to the tick (the live entry cap floors: an off-grid cap replace-stormed on 09-30). The bp caps
# are also tried rounded to the NEAREST tick and UP (user 2026-10-30): floored, + 50 bp on a $1.20 name is 0 or 1 tick of
# room depending on where the signal vwap falls in its cent. In a max() the mode applies to the bp part.
RND = ("", " (nearest)", " (ceil)")
RULES = (["none", "signal"] + [f"signal +{k} tick" for k in TICKS] + [f"signal +{b} bp{m}" for m in RND for b in BPS]
         + [f"max(+{k} tick, +{b} bp{m})" for m in RND for k in TICKS for b in BPS[:4]]
         + ["¼ of the way to ma30", "½ of the way to ma30"] + [f"ma{n}" for n in MAS])
if args.only: RULES = ["none"] + [r for r in RULES[1:] if re.search(args.only, r)]
FM = ["lit", "queue", "hidden"]
def caps_for(s, w, tk):
    fl = lambda v: math.floor(v / tk + 1e-6) * tk
    rnd = {"": fl, " (nearest)": lambda v: round(v / tk) * tk, " (ceil)": lambda v: math.ceil(v / tk - 1e-6) * tk}
    C = {"none": math.inf, "signal": fl(s),
         "¼ of the way to ma30": fl(s + 0.25 * (w[-30:].mean() - s)), "½ of the way to ma30": fl(s + 0.5 * (w[-30:].mean() - s)),
         **{f"ma{n}": fl(w[-n:].mean()) for n in MAS}}
    for k in TICKS: C[f"signal +{k} tick"] = fl(s + k * tk)
    for m, f in rnd.items():
        for b in BPS: C[f"signal +{b} bp{m}"] = f(s * (1 + b / 1e4))
        for k in TICKS:
            for b in BPS[:4]: C[f"max(+{k} tick, +{b} bp{m})"] = max(fl(s + k * tk), f(s * (1 + b / 1e4)))
    return C

rows, lots_n, empty_n = [], 0, 0
days = S.groupby("trade_date")
for di, (d, g) in enumerate(days):
    X_all = tape_for(d, g); midnight = ns_at(d, 0)
    for r in g.itertuples():
        Q = pd.read_parquet(os.path.join(qdir, f"{r.key}.parquet")).dropna()
        X = X_all[X_all.ticker == r.symbol]
        rec = dict(symbol=r.symbol, trade_date=d, year=r.year, signal_sec=r.signal_sec, exit_sec=r.exit_sec, signal_vwap=r.signal_vwap,
                   entry_px=r.entry_px, exit_px=r.exit_px, n_quotes=len(Q))
        if len(Q) == 0: empty_n += 1; rec["err"] = "no quotes"; rows.append(rec); continue
        if Q.bid_size.median() < 50: Q["bid_size"] *= 100; Q["ask_size"] *= 100; lots_n += 1; rec["lots"] = True
        # the caps: 1s vwaps of every print of the present seconds up to the signal second
        xs = (X.ts.values - midnight) / 1e9; sec = np.floor(xs).astype(np.int64)
        pre = sec <= r.signal_sec
        if pre.sum() == 0: rec["err"] = "no prints before the signal"; rows.append(rec); continue
        bars = pd.DataFrame({"s": sec[pre], "pv": X.price.values[pre] * X["size"].values[pre], "v": X["size"].values[pre]}).groupby("s").sum()
        w = (bars.pv / bars.v).values
        rec["bar_check"] = w[-1] / r.signal_vwap if bars.index[-1] == r.signal_sec else math.nan
        tk = 0.01 if r.signal_vwap >= 1 else 0.0001
        C = caps_for(r.signal_vwap, w, tk)
        # the event stream: quotes and the fill-eligible prints, a quote first at the same timestamp
        F = X[X.ok]
        qs = (Q.ts.values - midnight) / 1e9; fs = (F.ts.values - midnight) / 1e9
        et = np.concatenate([np.zeros(len(qs), np.int8), np.ones(len(fs), np.int8)])
        ets = np.concatenate([qs, fs]); o = np.lexsort((et, ets))
        et, ets = et[o], ets[o]
        eb = np.concatenate([Q.bid.values, np.zeros(len(fs))])[o]; ebs = np.concatenate([Q.bid_size.values, np.zeros(len(fs))])[o]
        ea = np.concatenate([Q.ask.values, np.zeros(len(fs))])[o]
        ep = np.concatenate([np.zeros(len(qs)), F.price.values])[o]; es = np.concatenate([np.zeros(len(qs)), F["size"].values.astype(float)])[o]
        elit = np.concatenate([np.zeros(len(qs), np.bool_), F.lit.values.astype(np.bool_)])[o]
        s0 = r.signal_sec + 1.0
        qi = np.flatnonzero((et == 0) & (ets <= s0) & (eb > 0) & (ea > 0))
        if len(qi) == 0: rec["err"] = "no quote at the signal"; rows.append(rec); continue
        bid0, bsz0 = eb[qi[-1]], ebs[qi[-1]]
        i0 = int(np.searchsorted(ets, s0, side="right"))
        n = float(max(1, math.floor(args.notional / r.signal_vwap)))
        rec.update(n=n, bid0=bid0, spread_bp=(ea[qi[-1]] - bid0) / bid0 * 1e4, caps=json.dumps(C))
        for rule in RULES:
            for k, fm in enumerate(FM):
                got, cost, tf, mv = run(et, ets, eb, ebs, ea, ep, es, elit, i0, s0 + args.lat, float(r.exit_sec), C[rule], tk, n, k, args.lat, bid0, bsz0)
                key = f"{rule}|{fm}"
                rec["q_" + key] = got; rec["px_" + key] = cost / got if got else math.nan
                rec["t_" + key] = tf - s0 if tf >= 0 else math.nan; rec["mv_" + key] = mv
                rec["pl_" + key] = (r.exit_px - cost / got) * got if got else 0.0
        rows.append(rec)
    if (di + 1) % 100 == 0: log(f"  simulated {di + 1:,}/{days.ngroups:,} days ({len(rows):,} trips)")
R = pd.DataFrame(rows); R.to_parquet(os.path.join(args.out, "sim.parquet"), index=False)
ok = R[R.get("err").isna()] if "err" in R else R
log(f"simulated {len(ok):,} of {len(R):,} (no quotes {empty_n}, sizes in lots {lots_n}, other errors {int(R.err.notna().sum()) - empty_n if 'err' in R else 0})")

# ---------------------------------------------------------------- 3. the report
bc = ok.bar_check.dropna()
print(f"\nthe cap bars vs the book's signal vwap: {len(bc):,} checked, |ratio − 1| > 1e-6 on {int((abs(bc - 1) > 1e-6).sum())}, "
      f"p99 {abs(bc - 1).quantile(.99) * 1e4:.1f} bp, max {abs(bc - 1).max() * 1e4:.1f} bp (the MA caps' bars; the book's own vwap is the signal cap)")
print(f"spread at the signal (bp): p25 {ok.spread_bp.quantile(.25):.0f} · median {ok.spread_bp.median():.0f} · p75 {ok.spread_bp.quantile(.75):.0f}")
model = ((ok.exit_px - ok.entry_px) * ok.n).sum(); notional = (ok.n * ok.signal_vwap).sum()
print(f"\nthe MODEL (every trip at entry_px): ${model:+,.0f} on ${notional:,.0f} of entries ({model / notional * 1e4:+.1f} bp)\n")
for fm in FM:
    base = ok[f"pl_none|{fm}"]
    print(f"## fill model: {fm}\n")
    print("| cap | filled full / part / none | entry vs model bp (filled) | P&L $ | Δ vs none $ | Δ bp of the notional | years Δ > 0 | unfilled: n / model P&L $ |")
    print("|---|---|---|---|---|---|---|---|")
    for rule in RULES:
        q = ok[f"q_{rule}|{fm}"]; px = ok[f"px_{rule}|{fm}"]; pl = ok[f"pl_{rule}|{fm}"]; f = q > 0
        ebp = ((ok.entry_px - px) * q)[f].sum() / (ok.entry_px * q)[f].sum() * 1e4
        dy = (pl - base).groupby(ok.year).sum()
        un = ok[~f]
        print(f"| {rule} | {int((q >= ok.n).sum())} / {int((f & (q < ok.n)).sum())} / {int((~f).sum())} | {ebp:+.1f} | {pl.sum():+,.0f} | "
              f"**{pl.sum() - base.sum():+,.0f}** | {(pl.sum() - base.sum()) / notional * 1e4:+.1f} | {int((dy > 0).sum())} / {len(dy)} | "
              f"{len(un)} / {((un.exit_px - un.entry_px) * un.n).sum():+,.0f} |")
    print()
print("## Δ vs none by year, fill model queue ($)\n")
yr = sorted(ok.year.unique())
print("| cap | " + " | ".join(yr) + " |\n|---|" + "---|" * len(yr))
for rule in RULES[1:]:
    dy = (ok[f"pl_{rule}|queue"] - ok["pl_none|queue"]).groupby(ok.year).sum()
    print(f"| {rule} | " + " | ".join(f"{dy.get(y, 0):+,.0f}" for y in yr) + " |")
print("\n## STABILITY, fill model queue: the per-trip gain clustered by day (t = mean / SE over days), the worst year, the years' SD\n")
print("| cap | Δ $ | t (by day) | worst year $ | years' SD $ | years > 0 | trips changed |\n|---|---|---|---|---|---|---|")
st = []
for rule in RULES[1:]:
    dd = ok[f"pl_{rule}|queue"] - ok["pl_none|queue"]; bd = dd.groupby(ok.trade_date).sum(); yy = dd.groupby(ok.year).sum()
    st.append((bd.mean() / (bd.std(ddof=1) / math.sqrt(len(bd))), rule, dd.sum(), yy.min(), yy.std(), int((yy > 0).sum()), len(yy), int((dd.abs() > 1e-9).sum())))
for t, rule, tot, wy, sd, yp, ny, ch in sorted(st, reverse=True):
    print(f"| {rule} | {tot:+,.0f} | {t:.2f} | {wy:+,.0f} | {sd:,.0f} | {yp} / {ny} | {ch} |")
print("\n## Δ vs none by the signal price, fill model queue ($; trips per bucket in the header)\n")
pb = pd.cut(ok.signal_vwap, [0, 2, 5, 10, 20, 1e9], labels=["< $2", "$2-5", "$5-10", "$10-20", "> $20"])
cnt = pb.value_counts().reindex(pb.cat.categories)
print("| cap | " + " | ".join(f"{c} ({n})" for c, n in cnt.items()) + " | all |\n|---|" + "---|" * (len(cnt) + 1))
for rule in RULES[1:]:
    dq = (ok[f"pl_{rule}|queue"] - ok["pl_none|queue"]).groupby(pb, observed=False).sum()
    print(f"| {rule} | " + " | ".join(f"{dq.get(c, 0):+,.0f}" for c in cnt.index) + f" | **{dq.sum():+,.0f}** |")
log("done")
