"""FlushFader EXIT fill simulation (user 2026-11-03) — the resting 5m-high sell vs the live maker-at-the-ask exit.

Why: live, the target exit is a maker SELL sent at the trigger: a DAY limit at the NBBO ask, re-priced to follow the ask
on every quote move, uncapped. On reversals it chases the ask down (2026-11-02: CHOW −262 bp vs the model exit, AIOK −261
bp, filled 813 s after the signal). The user ruled a new exit (being built in the Oms):
  1. while the position is held, a SELL LIMIT RESTS at the current 5m-high level ROUNDED UP to the tick (0.01 at >= \\$1,
     0.0001 below), placed once the entry has filled, replaced whenever the rounded level changes. The level = the max of
     the 1s vwaps over the last 300 PRESENT bars INCLUDING the current one (the level the next bar must exceed).
  2. at the trigger (the research exit signal) the resting sell stays at the level it had (the prior max, rounded up) 60 s;
  3. if still not (fully) filled 60 s after the trigger, it switches to the maker exit (the ask, following it).
This compares that rule with (a) the research model exit (the book's exit_px) and (b) today's live maker.

The book: data/flushfader_gate_review/broad_reference_book.parquet (37,254 trips 2020-01 .. 2026-08, RAW tape prices).
The 54 'next_open' trips (the target never hit; exit_reason from base_v19) are left out. The engine's 1s bars are rebuilt
from the bulk tape exactly as build_all_1s_bars.fsx --ms-precision does (all venues; the 15/16 markers dropped; auction
prints {17,25,19,8} kept unconditionally; else sip − participant <= 50 ms at ms precision AND not in the exclude set; the
bucket = floor((sip or participant − 00:00 ET) / 1 s)); RTH only (09:30 .. the close, the engine's SessionStartSec/MocSec).
The trigger bar T = the first bar at or after entry_sec whose FLOAT32 vwap (the corpus stores FLOAT) is strictly above
the max of the prior 300 present bars; the book's exit = the next present bar (exit_sec, exit_px = its vwap). A trip
whose rebuilt (T+1, vwap) differs from the book's (exit_sec, exit_px) is left out (counted). The ORDER levels use the
double-precision vwaps (what a live system computes), rounded up to the tick with a 1e-6-tick tolerance.

The order (\\$1,000 / entry_px shares; the entry assumed filled at entry_px at entry_sec — exits only). Clock: a bar closes
at its second + 1; every placement / re-pricing becomes live LAT = 0.216 s later (the old price stays live until then;
requests in flight collapse onto the newest price, as in flushfader_cap_sim.py). Variants:
  MAKER    today's live: from T+1, sell at the ask, following it.
  REST0    resting at ceil(level) from entry_sec+1, the maker at once at T+1.
  REST60   the new rule: resting, frozen at its level for 60 s after T+1, then the maker.
  RESTinf  resting, frozen at the trigger, never switching (until filled or the window ends).
  REST60 −1 tick / +1 tick   CONTROLS: REST60 with every resting price shifted one tick down / up.
Fills (mirroring flushfader_cap_sim.py's three models for a SELL), against the tape's prints (the cap sim's exclusions:
conditions {2,7,10,13,20,21,22,29,32,52,53} and the closing / official prints {8,15,16,19} dropped; lit = not FINRA TRF,
exchange 4):
  lit     any lit print at or through (>=) our price fills us
  queue   when we join the ask (our price == the ask) its displayed size is ahead; a lit print AT our price eats it first;
          a lit print strictly through, or any print a full tick through, fills; above the ask nobody is ahead
  hidden  queue, and a price above the ask has the ask's displayed size ahead (pessimistic)
  back    (added here, the most pessimistic) hidden, and whenever the NBBO ask ARRIVES at our resting price from below,
          its displayed size there is put ahead of us (as if every order at the 5m-high level had been there first)
  all     only prints within the 50 ms sip − participant cap fill (a late-reported print is a past price; the cap sim had
          no cap); an (re)opening AUCTION print (17/18/25) at or through our price fills us at the AUCTION price, nobody
          ahead; quotes with a zero size are dropped (halt indications);
          a new price at or below the bid is marketable: filled at once at the bid (a TAKER; the take fee is not modelled,
          counted); a bid strictly above our resting price fills us at our price (counted as 'crossed').
The window: quotes from data/equity/flushfader_cap_sim/quotes (signal − 60 s .. exit_sec + 2) plus an extension fetched
here (exit_sec + 2 .. min(exit_sec + 1800, the close); data/equity/flushfader_exit_sim/quotes/). The DAY order does not
work past the close; an order unfilled at the window end is MARKED at the last 1s vwap in the window (flagged).
Outcome = proceeds + unfilled × mark − n × entry_px. MODEL = (exit_px − entry_px) × n.

Run from research/ (each phase resumes; --sample N for a test on N random trips; --trace KEY prints one trip by hand):
    python -u scripts/equity/flushfader_exit_sim.py --phase fetch > logs/flushfader_exit_sim_fetch.log 2>&1; echo exit $?
    /usr/bin/time -v python -u scripts/equity/flushfader_exit_sim.py --phase sim > logs/flushfader_exit_sim.log 2>&1; echo exit $?
    python -u scripts/equity/flushfader_exit_sim.py --phase report      # data/equity/flushfader_exit_sim/report.md
"""
import argparse, datetime, json, math, os, time, urllib.request, zoneinfo
from concurrent.futures import ThreadPoolExecutor
import numpy as np, pandas as pd, duckdb
from numba import njit

ap = argparse.ArgumentParser()
ap.add_argument("--book", default="data/flushfader_gate_review/broad_reference_book.parquet")
ap.add_argument("--base", default="data/equity/flushfader/base_v19/*.parquet", help="the base corpus (exit_reason)")
ap.add_argument("--trades", default="data/bulk/trades")
ap.add_argument("--quotes0", default="data/equity/flushfader_cap_sim/quotes", help="the cap sim's cache (signal−60 .. exit+2)")
ap.add_argument("--out", default="data/equity/flushfader_exit_sim")
ap.add_argument("--window", type=int, default=1800, help="seconds after exit_sec the order may work (capped at the close)")
ap.add_argument("--hold", type=float, default=60.0, help="REST60's freeze after the trigger")
ap.add_argument("--notional", type=float, default=1000.0)
ap.add_argument("--lat", type=float, default=0.216)
ap.add_argument("--workers", type=int, default=8)
ap.add_argument("--sample", type=int, default=0, help="random trips for a test run (0 = the whole book)")
ap.add_argument("--seed", type=int, default=20261103)
ap.add_argument("--trace", help="KEY (symbol_date_signalsec): print one trip's level path, prints and fills")
ap.add_argument("--phase", choices=("fetch", "sim", "report", "all"), default="all")
args = ap.parse_args()
T0 = time.time()
def log(*a): print(f"[{time.time() - T0:7.1f}s]", *a, flush=True)
NY = zoneinfo.ZoneInfo("America/New_York")
def base_ns(d: str) -> int:
    y, m, dd = map(int, d.split("-")); return int(datetime.datetime(y, m, dd, tzinfo=NY).timestamp() * 1e9)
def ns_at(d, sec): return base_ns(d) + int(round(sec * 1e9))
EARLY = {"2016-11-25", "2017-07-03", "2017-11-24", "2018-07-03", "2018-11-23", "2018-12-24", "2019-07-03", "2019-11-29", "2019-12-24",
         "2020-11-27", "2020-12-24", "2021-11-26", "2022-11-25", "2023-07-03", "2023-11-24", "2024-07-03", "2024-11-29", "2024-12-24",
         "2025-07-03", "2025-11-28", "2025-12-24", "2026-11-27", "2026-12-24"}
def close_sec(d): return 46800 if d in EARLY else 57600
sfx = f"_s{args.sample}" if args.sample else ""
os.makedirs(args.out, exist_ok=True); qdir = os.path.join(args.out, "quotes"); os.makedirs(qdir, exist_ok=True)

con = duckdb.connect(); con.execute("SET memory_limit='2GB'"); con.execute("SET threads=4")
B = con.execute(f"""
    WITH b AS (SELECT * FROM '{args.book}'),
         r AS (SELECT symbol, trade_date::VARCHAR AS trade_date, signal_sec, exit_reason FROM read_parquet('{args.base}'))
    SELECT b.*, r.exit_reason FROM b LEFT JOIN r USING (symbol, trade_date, signal_sec)
    ORDER BY trade_date, symbol, signal_sec""").df()                           # a join's row order is not deterministic
assert len(B) == 37254 or args.book != ap.get_default("book"), len(B)
B["year"] = B.trade_date.str[:4]
B["key"] = B.symbol + "_" + B.trade_date + "_" + B.signal_sec.astype(str)
B["t_end"] = np.minimum(B.exit_sec + args.window, B.trade_date.map(close_sec))
n_next_open = int((B.exit_reason != "target").sum())
S = B[B.exit_reason == "target"]
if args.trace: S = S[S.key == args.trace]
elif args.sample: S = S.sample(args.sample, random_state=args.seed)
S = S.sort_values(["trade_date", "symbol", "signal_sec"]).reset_index(drop=True)
log(f"book {len(B):,}; exit_reason {B.exit_reason.value_counts().to_dict()}; simulating {len(S):,}")

# ---------------------------------------------------------------- 1. the quote extension (cached; the key never printed)
def fetch(r):
    path = os.path.join(qdir, f"{r.key}.parquet")
    if os.path.exists(path): return 0
    rows, pages = [], 0
    if r.t_end > r.exit_sec + 2:
        url = (f"https://api.massive.com/v3/quotes/{r.symbol}?timestamp.gte={ns_at(r.trade_date, r.exit_sec + 2)}"
               f"&timestamp.lt={ns_at(r.trade_date, r.t_end)}&limit=50000&sort=timestamp&order=asc")
        while url:
            for attempt in range(6):
                try:
                    req = urllib.request.Request(url, headers={"Authorization": f"Bearer {KEY}"})
                    d = json.load(urllib.request.urlopen(req, timeout=120)); break
                except Exception as e:
                    if attempt == 5: raise RuntimeError(f"{r.key}: {type(e).__name__}")
                    time.sleep(2 * (attempt + 1))
            rows += [(q.get("sip_timestamp"), q.get("bid_price"), q.get("bid_size"), q.get("ask_price"), q.get("ask_size")) for q in d.get("results", [])]
            url = d.get("next_url"); pages += 1
    tmp = path + ".tmp"
    pd.DataFrame(rows, columns=["ts", "bid", "bid_size", "ask", "ask_size"]).astype(
        {"ts": "int64", "bid": "float64", "bid_size": "float64", "ask": "float64", "ask_size": "float64"}).to_parquet(tmp, index=False)
    os.replace(tmp, path)
    return pages
if args.phase in ("fetch", "all"):
    KEY = json.load(open(os.path.expanduser("~/.config/massive/credentials")))["massive_api_key"]
    todo = [r for r in S.itertuples() if not os.path.exists(os.path.join(qdir, f"{r.key}.parquet"))]
    log(f"quote extension: {len(S) - len(todo):,} cached, fetching {len(todo):,}")
    pg = 0
    with ThreadPoolExecutor(args.workers) as ex:
        for i, p in enumerate(ex.map(fetch, todo)):
            pg += p
            if (i + 1) % 500 == 0: log(f"  fetched {i + 1:,}/{len(todo):,} ({pg:,} pages)")
    log(f"quote extension: done ({pg:,} pages)")

# ---------------------------------------------------------------- 2. the simulation
FILL_EXCL = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53, 8, 15, 16, 19]::UTINYINT[]"
BAR_EXCL = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53]::UTINYINT[]"
def tape_for(d, sym, t_end_max):
    """One ticker-day's prints, 09:30 .. the last trip's window end (per ticker: a whole-day IN-list query OOM-killed the
    first full run at 6 GB on a 2021 meme day). Plain numpy columns — no per-row ticker strings."""
    b0 = base_ns(d)
    lo, hi = b0 + 34200 * 10**9, b0 + int(t_end_max + 1) * 10**9
    sip_ms, par_ms = "((sip_timestamp // 1000000) * 1000000)", "((participant_timestamp // 1000000) * 1000000)"
    q = f"""
        WITH x AS (SELECT COALESCE(NULLIF(sip_timestamp, 0), participant_timestamp) AS ts, price, size::DOUBLE AS size,
                          exchange <> 4 AS lit, COALESCE(conditions, []::UTINYINT[]) AS conditions, sip_timestamp, participant_timestamp, sequence_number
                   FROM read_parquet('{os.path.join(args.trades, d + ".parquet")}')
                   WHERE ticker = '{sym}' AND size > 0 AND price > 0)
        SELECT ts, price, size, lit,
               list_has_any(conditions, [17, 18, 25]::UTINYINT[]) AS auction,
               (NOT list_has_any(conditions, {FILL_EXCL})
                AND (list_has_any(conditions, [17, 18, 25]::UTINYINT[])
                     OR sip_timestamp = 0 OR participant_timestamp = 0 OR ({sip_ms} - {par_ms}) <= 50000000)) AS fill_ok,
               (NOT list_has_any(conditions, [15, 16]::UTINYINT[])
                AND (list_has_any(conditions, [17, 25, 19, 8]::UTINYINT[])
                     OR ((sip_timestamp = 0 OR participant_timestamp = 0 OR ({sip_ms} - {par_ms}) <= 50000000)
                         AND NOT list_has_any(conditions, {BAR_EXCL})))) AS bar_ok
        FROM x WHERE ts >= {lo} AND ts < {hi} ORDER BY ts, sequence_number"""
    global con
    for attempt in range(3):
        try: return pd.DataFrame(con.execute(q).fetchnumpy())
        except duckdb.Error as e:
            # 2026-11-03: one DuckDB InternalException ("Information loss on integer cast") on a 2025-10 day that did not
            # reproduce in isolation: retry on a fresh connection, then give up on the ticker-day (its trips flagged)
            log(f"  tape read failed {d} {sym} (attempt {attempt + 1}): {type(e).__name__}: {str(e).splitlines()[0][:160]}")
            con = duckdb.connect(); con.execute("SET memory_limit='2GB'"); con.execute("SET threads=4")
    return None

@njit(cache=True)
def run(et, ets, eb, ea, eas, ep, es, elit, eauc, sched_t, sched_px, t_maker, t_end, n, tick, fm, lat, out_t, out_px, out_q):
    """One sell order's life. et 0 = quote, 1 = print. Returns (filled, proceeds, n_fills, taker shares, crossed shares, re-pricings)."""
    px = np.nan; pend_t = -1.0; pend_px = np.nan
    lb = 0.0; la = 0.0; las = 0.0; qa = 0.0
    si = 0; maker = False
    got = 0.0; proc = 0.0; k = 0; taker = 0.0; crossed = 0.0; moves = 0
    for i in range(len(et)):
        ts = ets[i]
        if ts > t_end or got >= n: break
        # resting-level requests due by now (only before the maker switch)
        while si < len(sched_t) and sched_t[si] <= ts:
            if sched_t[si] < t_maker:
                cur = pend_px if pend_t >= 0 else px
                if not (abs(sched_px[si] - cur) < 1e-9):
                    if pend_t < 0: pend_t = sched_t[si] + lat
                    pend_px = sched_px[si]
            si += 1
        if (not maker) and ts >= t_maker:
            maker = True
            if la > 0:
                cur = pend_px if pend_t >= 0 else px
                if not (abs(la - cur) < 1e-9):
                    if pend_t < 0: pend_t = t_maker + lat
                    pend_px = la
        # a pending price goes live
        if pend_t >= 0 and ts >= pend_t:
            act_t = pend_t; px = pend_px; pend_t = -1.0; moves += 1
            if lb > 0 and px <= lb + 1e-9:                    # marketable: taken at once at the bid
                g = n - got; got += g; proc += g * lb; taker += g
                if k < len(out_t): out_t[k] = act_t; out_px[k] = lb; out_q[k] = g
                k += 1
                break
            if fm == 0: qa = 0.0
            elif px > la + 1e-9: qa = las if fm >= 2 else 0.0
            elif abs(px - la) < 1e-9: qa = las
            else: qa = 0.0
        if et[i] == 0:
            b = eb[i]; a = ea[i]
            if b <= 0 or a <= 0: continue
            # 'back' (fm 3): when the ask arrives at our resting price from below, its displayed size is ahead of us
            if fm == 3 and px == px and abs(a - px) < 1e-9 and la < px - 1e-9: qa = max(qa, eas[i])
            lb = b; la = a; las = eas[i]
            if px == px and lb > px + 1e-9:                    # the bid moved through our resting price
                g = n - got; got += g; proc += g * px; crossed += g
                if k < len(out_t): out_t[k] = ts; out_px[k] = px; out_q[k] = g
                k += 1
                break
            if maker:
                cur = pend_px if pend_t >= 0 else px
                if not (abs(la - cur) < 1e-9):
                    if pend_t < 0: pend_t = ts + lat
                    pend_px = la
        else:
            if not (px == px): continue
            p = ep[i]; q = es[i]; lit = elit[i]
            through = (lit and p > px + 1e-9) or p >= px + tick - 1e-9
            f = 0.0
            if fm == 0:
                if lit and p >= px - 1e-9: f = q
            elif through: f = q
            elif lit and abs(p - px) < 1e-9:
                eat = min(qa, q); qa -= eat; f = q - eat
            fpx = px
            if eauc[i] and p >= px - 1e-9:                  # an (re)opening auction crosses us at ITS price, nobody ahead
                f = q; fpx = max(px, p)
            if f > 0:
                g = min(f, n - got); got += g; proc += g * fpx
                if k < len(out_t): out_t[k] = ts; out_px[k] = fpx; out_q[k] = g
                k += 1
    return got, proc, k, taker, crossed, moves

def tick_of(p): return 0.01 if p >= 1.0 else 0.0001
def ceil_tick(v):
    tk = tick_of(v); return round(math.ceil(v / tk - 1e-6) * tk, 4)
FM = ["lit", "queue", "hidden", "back"]
VAR = ["MAKER", "REST0", "REST60", "RESTinf", "REST60_m1", "REST60_p1"]

def sim_trip(r, X, Q, rec, trace=False):
    midnight = base_ns(r.trade_date)
    xs = (X.ts.values - midnight) / 1e9
    # ---- the 1s bars (the engine's rule), RTH up to the close, then the trigger
    xb = X[X.bar_ok.values]; sb = np.floor((xb.ts.values - midnight) / 1e9).astype(np.int64)
    bars = pd.DataFrame({"s": sb, "pv": xb.price.values * xb["size"].values, "v": xb["size"].values}).groupby("s").sum()
    bars = bars[(bars.index >= 34200)]
    s_all = bars.index.values; wd_all = (bars.pv / bars.v).values
    rth = s_all <= close_sec(r.trade_date)
    s = s_all[rth]; wd = wd_all[rth]; w32 = wd.astype(np.float32).astype(np.float64)
    ie = int(np.searchsorted(s, r.entry_sec))
    if ie >= len(s) or s[ie] != r.entry_sec: rec["err"] = "entry bar missing"; return rec
    rec["entry_match"] = abs(w32[ie] / r.entry_px - 1) < 1e-6
    pm = pd.Series(w32).rolling(300, min_periods=1).max().shift(1).values      # the strictly-prior 300-present-bar max
    hit = np.flatnonzero(w32[ie:] > pm[ie:])                                     # NaN (bar 0) compares False
    T = int(ie + hit[0]) if len(hit) else -1
    if T < 0 or T + 1 >= len(s): rec["err"] = "no trigger"; return rec
    rec["trig_sec"] = int(s[T]); rec["rb_exit_sec"] = int(s[T + 1]); rec["rb_exit_px"] = w32[T + 1]
    rec["trig_match"] = bool(s[T + 1] == r.exit_sec and abs(w32[T + 1] / r.exit_px - 1) < 1e-6)
    if not rec["trig_match"]: rec["err"] = "trigger mismatch"; return rec
    # ---- the resting level path: after bar i (i = ie .. T−1) closes, ceil(max of the 300 bars ending at i)
    lvl = pd.Series(wd).rolling(300, min_periods=1).max().values
    st, sp = [], []
    for i in range(ie, T):
        c = ceil_tick(lvl[i])
        if not sp or abs(c - sp[-1]) > 1e-9: st.append(float(s[i] + 1)); sp.append(c)
    st = np.array(st, float); sp = np.array(sp, float)
    rec["level_prev"] = lvl[T - 1] if T > ie else np.nan; rec["rest_px"] = sp[-1] if len(sp) else np.nan
    rec["n_level_changes"] = len(sp); rec["trig_vwap"] = wd[T]
    t1 = float(s[T] + 1); te = float(r.t_end)
    in_win = s_all <= te
    rec["mark"] = wd_all[in_win][-1]
    # ---- the event stream: quotes first at a shared timestamp
    F = X[X.fill_ok.values]
    fs = (F.ts.values - midnight) / 1e9
    qs = (Q.ts.values - midnight) / 1e9
    m = fs >= r.entry_sec; fs = fs[m]; F = F[m]
    et = np.concatenate([np.zeros(len(qs), np.int8), np.ones(len(fs), np.int8)])
    ets = np.concatenate([qs, fs]); o = np.lexsort((et, ets)); et, ets = et[o], ets[o]
    nq, nf = len(qs), len(fs)
    eb = np.concatenate([Q.bid.values, np.zeros(nf)])[o]; ea = np.concatenate([Q.ask.values, np.zeros(nf)])[o]
    eas = np.concatenate([Q.ask_size.values, np.zeros(nf)])[o]
    ep = np.concatenate([np.zeros(nq), F.price.values])[o]; es = np.concatenate([np.zeros(nq), F["size"].values.astype(float)])[o]
    elit = np.concatenate([np.zeros(nq, np.bool_), F.lit.values.astype(np.bool_)])[o]
    eauc = np.concatenate([np.zeros(nq, np.bool_), F.auction.values.astype(np.bool_)])[o]
    qi = np.flatnonzero((et == 0) & (ets <= t1) & (eb > 0) & (ea > 0))
    if len(qi) == 0: rec["err"] = "no quote at the trigger"; return rec
    rec["bid_t1"] = eb[qi[-1]]; rec["ask_t1"] = ea[qi[-1]]
    n = float(max(1, math.floor(args.notional / r.entry_px))); rec["n"] = n
    tk = tick_of(r.entry_px)
    empty = np.zeros(0)
    plan = {"MAKER": (empty, empty, t1), "REST0": (st, sp, t1), "REST60": (st, sp, t1 + args.hold), "RESTinf": (st, sp, np.inf),
            "REST60_m1": (st, np.array([round(p - tick_of(p), 4) for p in sp]), t1 + args.hold),
            "REST60_p1": (st, np.array([round(p + tick_of(p), 4) for p in sp]), t1 + args.hold)}
    out_t = np.zeros(20000); out_px = np.zeros(20000); out_q = np.zeros(20000)
    for v, (a_t, a_px, tm) in plan.items():
        for k, fm in enumerate(FM):
            got, proc, nk, taker, crossed, mv = run(et, ets, eb, ea, eas, ep, es, elit, eauc, a_t, a_px, tm, te, n, tick_of(r.exit_px), k, args.lat, out_t, out_px, out_q)
            key = f"{v}|{fm}"
            rec["q_" + key] = got; rec["px_" + key] = proc / got if got else np.nan
            rec["t0_" + key] = out_t[0] - t1 if nk else np.nan
            rec["t1_" + key] = out_t[min(nk, len(out_t)) - 1] - t1 if nk else np.nan
            rec["tk_" + key] = taker; rec["cx_" + key] = crossed; rec["mv_" + key] = mv
            rec["pl_" + key] = proc + (n - got) * rec["mark"] - n * r.entry_px
            if trace:
                print(f"  {key:18s} filled {got:.0f}/{n:.0f} avg {rec['px_' + key]:.4f} first {rec['t0_' + key]:+.3f}s last {rec['t1_' + key]:+.3f}s "
                      f"taker {taker:.0f} crossed {crossed:.0f} repricings {mv} P&L {rec['pl_' + key]:+.2f}")
                for j in range(min(nk, 6)): print(f"      fill {out_t[j] - t1:+9.3f}s  {out_q[j]:7.0f} @ {out_px[j]:.4f}")
    rec["pl_MODEL"] = (r.exit_px - r.entry_px) * n
    if trace:
        print(f"\n{r.key}: entry {r.entry_sec} @ {r.entry_px:.4f}; trigger bar {s[T]} vwap {wd[T]:.4f} > prior max {lvl[T - 1]:.4f}; "
              f"book exit {r.exit_sec} @ {r.exit_px:.4f}; n {n:.0f}; window end {te:.0f}; mark {rec['mark']:.4f}")
        print("  level path (time = bar close, the resting price):")
        for a, b in list(zip(st, sp))[:3] + [("...", "")] + list(zip(st, sp))[-8:]: print(f"      {a}  {b}")
        print("  bars T−5 .. T+5 (sec, vwap, rolling max incl.):")
        for i in range(max(ie, T - 5), min(len(s), T + 6)): print(f"      {s[i]}  {wd[i]:.4f}  {lvl[i]:.4f}{'   <- T' if i == T else ''}")
        w = (fs >= s[T] - 3) & (fs < s[T] + 4)
        print("  fill-eligible prints T−3 .. T+4 (t − T−1, px, size, lit):")
        for a, b, c, d_ in zip(fs[w][:60], F.price.values[w][:60], F["size"].values[w][:60], F.lit.values[w][:60]): print(f"      {a - t1:+8.3f}  {b:.4f}  {c:7.0f}  {'lit' if d_ else 'TRF'}")
        wq = (qs >= s[T] - 2) & (qs < s[T] + 3)
        print("  quotes T−2 .. T+3 (t − T−1, bid, ask, ask size):")
        for a, b, c, d_ in list(zip(qs[wq], Q.bid.values[wq], Q.ask.values[wq], Q.ask_size.values[wq]))[:40]: print(f"      {a - t1:+8.3f}  {b:.4f}  {c:.4f}  {d_:.0f}")
    return rec

simp = os.path.join(args.out, f"sim{sfx}.parquet")
if args.phase in ("sim", "all") or args.trace:
    # resumable: one parquet per CHUNK days under parts<sfx>/ (a partial run is kept; a rerun skips finished chunks)
    pdir = os.path.join(args.out, f"parts{sfx}"); os.makedirs(pdir, exist_ok=True)
    CHUNK = 25
    lots_n = 0
    dlist = sorted(S.trade_date.unique()); nch = (len(dlist) + CHUNK - 1) // CHUNK
    for ci in range(nch):
        cp = os.path.join(pdir, f"chunk_{ci:04d}.parquet")
        if os.path.exists(cp) and not args.trace: continue
        rows = []
        for d in dlist[ci * CHUNK:(ci + 1) * CHUNK]:
            g = S[S.trade_date == d]
            for sym, gs in g.groupby("symbol"):
                X = tape_for(d, sym, gs.t_end.max())
                for r in gs.itertuples():
                    rec = dict(key=r.key, symbol=r.symbol, trade_date=d, year=r.year, signal_sec=r.signal_sec, entry_sec=r.entry_sec,
                               exit_sec=r.exit_sec, entry_px=r.entry_px, exit_px=r.exit_px, t_end=r.t_end)
                    if X is None: rec["err"] = "tape read error"; rows.append(rec); continue
                    Q = pd.concat([pd.read_parquet(os.path.join(args.quotes0, f"{r.key}.parquet")), pd.read_parquet(os.path.join(qdir, f"{r.key}.parquet"))]).dropna()
                    # zero-size quotes are not quotes (halt indications: NYCB 2024-03-06 showed 4.00 x 4.50, sizes 0, mid-halt)
                    Q = Q[(Q.bid > 0) & (Q.ask > 0) & (Q.bid_size > 0) & (Q.ask_size > 0)].sort_values("ts", kind="stable")
                    if len(Q) and Q.bid_size.median() < 50: Q["bid_size"] *= 100; Q["ask_size"] *= 100; lots_n += 1; rec["lots"] = True
                    rows.append(sim_trip(r, X, Q, rec, trace=bool(args.trace)))
                del X
        if args.trace: raise SystemExit(0)
        pd.DataFrame(rows).to_parquet(cp + ".tmp", index=False); os.replace(cp + ".tmp", cp)
        log(f"  chunk {ci + 1}/{nch} done ({len(rows):,} trips; days {dlist[ci * CHUNK]} .. {dlist[min(len(dlist), (ci + 1) * CHUNK) - 1]})")
    R = pd.concat([pd.read_parquet(os.path.join(pdir, f"chunk_{ci:04d}.parquet")) for ci in range(nch)], ignore_index=True)
    R.to_parquet(simp, index=False)
    log(f"simulated {len(R):,} trips (sizes in lots this pass {lots_n}); errors {R.err.value_counts().to_dict() if 'err' in R else {}}")

# ---------------------------------------------------------------- 3. the report
if args.phase in ("report", "all"):
    R = pd.read_parquet(simp)
    out = []
    def P(s=""): out.append(s)
    nb = len(R)
    err = R.err.value_counts() if "err" in R else pd.Series(dtype=int)
    ok = R[R.err.isna()].copy() if "err" in R else R.copy()
    P(f"# FlushFader EXIT fill simulation — the resting 5m-high sell (REST60) vs the live maker (2026-11-03)\n")
    P(f"Script `scripts/equity/flushfader_exit_sim.py`; book `{args.book}`; LAT {args.lat} s; window = min(exit_sec + {args.window} s, "
      f"the close); \\$1,000 / entry_px shares; the entry assumed filled at entry_px.\n")
    P("## The sample and the rebuild\n")
    P("| | trips |\n|---|---|")
    P(f"| book | {len(B):,} |\n| not a target exit (next_open, left out) | {n_next_open} |\n| simulated (target exits{', random sample' if args.sample else ''}) | {nb:,} |")
    for e, c in err.items(): P(f"| left out: {e} | {c} |")
    P(f"| **compared** | **{len(ok):,}** |")
    tm = R.trig_match.dropna() if "trig_match" in R else pd.Series(dtype=bool)
    P(f"\nRebuilt trigger (T+1 = exit_sec and its vwap = exit_px within 1e-6): **{int(tm.sum()):,} of {len(tm):,} ({tm.mean() * 100:.2f} %)**; "
      f"entry bar vwap = entry_px on {int(R.entry_match.sum()):,} of {int(R.entry_match.notna().sum()):,}.\n")
    notional = (ok.n * ok.entry_px).sum(); yrs = sorted(ok.year.unique())
    def tstat(dd):
        bd = dd.groupby(ok.trade_date).sum(); return bd.mean() / (bd.std(ddof=1) / math.sqrt(len(bd))) if len(bd) > 2 else np.nan
    def yrpos(dd): yy = dd.groupby(ok.year).sum(); return f"{int((yy > 0).sum())} / {len(yy)}"
    model = ok.pl_MODEL
    for fm in FM:
        P(f"## Totals — fill model **{fm}** ({len(ok):,} trips, \\${notional:,.0f} of entries)\n")
        P("| exit | P&L \\$ | bp of notional | Δ vs MODEL \\$ | bp | t (by day) | years > 0 | Δ vs MAKER \\$ | bp | t (by day) | years > 0 | full / partial / none | marked P&L \\$ | taker / crossed fills |")
        P("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")
        P(f"| MODEL | {model.sum():+,.0f} | {model.sum() / notional * 1e4:+.1f} | | | | | | | | | | | |")
        mk = ok[f"pl_MAKER|{fm}"]
        for v in VAR:
            pl = ok[f"pl_{v}|{fm}"]; q = ok[f"q_{v}|{fm}"]; full = q >= ok.n - 1e-9; none = q <= 0
            d1 = pl - model; d2 = pl - mk
            unf = ~full
            P(f"| {v} | {pl.sum():+,.0f} | {pl.sum() / notional * 1e4:+.1f} | {d1.sum():+,.0f} | {d1.sum() / notional * 1e4:+.1f} | {tstat(d1):.2f} | {yrpos(d1)} | "
              + (f"{d2.sum():+,.0f} | {d2.sum() / notional * 1e4:+.1f} | {tstat(d2):.2f} | {yrpos(d2)} | " if v != "MAKER" else "| | | | ")
              + f"{int(full.sum())} / {int((~full & ~none).sum())} / {int(none.sum())} | {((ok.n - q) * (ok.mark) - (ok.n - q) * ok.entry_px)[unf].sum():+,.0f} | "
              f"{int((ok[f'tk_{v}|{fm}'] > 0).sum())} / {int((ok[f'cx_{v}|{fm}'] > 0).sum())} |")
        P()
    # ---- by year and by price, queue
    P("## Δ by year, fill model queue (\\$)\n")
    P("| exit | " + " | ".join(f"{y} ({int((ok.year == y).sum())})" for y in yrs) + " |\n|---|" + "---|" * len(yrs))
    for v in VAR:
        dm = (ok[f"pl_{v}|queue"] - model).groupby(ok.year).sum()
        P(f"| {v} − MODEL | " + " | ".join(f"{dm.get(y, 0):+,.0f}" for y in yrs) + " |")
    for v in VAR[1:]:
        dk = (ok[f"pl_{v}|queue"] - ok["pl_MAKER|queue"]).groupby(ok.year).sum()
        P(f"| {v} − MAKER | " + " | ".join(f"{dk.get(y, 0):+,.0f}" for y in yrs) + " |")
    pb = pd.cut(ok.entry_px, [0, 2, 5, 10, 20, 1e9], labels=["< \\$2", "\\$2-5", "\\$5-10", "\\$10-20", "> \\$20"])
    cnt = pb.value_counts().reindex(pb.cat.categories)
    for fm in ("queue", "hidden", "back"):
        P(f"\n## Δ by entry price, fill model {fm} (bp of the bucket's notional; trips in the header)\n")
        P("| exit | " + " | ".join(f"{c} ({n})" for c, n in cnt.items()) + " |\n|---|" + "---|" * len(cnt))
        nt = (ok.n * ok.entry_px).groupby(pb, observed=False).sum()
        for v in VAR:
            dm = (ok[f"pl_{v}|{fm}"] - model).groupby(pb, observed=False).sum() / nt * 1e4
            P(f"| {v} − MODEL | " + " | ".join(f"{dm.get(c, 0):+.1f}" for c in cnt.index) + " |")
        for v in VAR[1:]:
            dk = (ok[f"pl_{v}|{fm}"] - ok[f"pl_MAKER|{fm}"]).groupby(pb, observed=False).sum() / nt * 1e4
            P(f"| {v} − MAKER | " + " | ".join(f"{dk.get(c, 0):+.1f}" for c in cnt.index) + " |")
    # ---- fill timing
    P("\n## When the order completes (seconds vs T+1, the close of the trigger bar = the research signal)\n")
    P("| exit · fill model | before the trigger bar | in the trigger bar | T+1 .. T+61 | after T+61 | never (marked) | median delay s | p90 delay s | p99 delay s |")
    P("|---|---|---|---|---|---|---|---|---|")
    for v in VAR:
        for fm in FM:
            q = ok[f"q_{v}|{fm}"]; full = q >= ok.n - 1e-9; t = ok[f"t1_{v}|{fm}"].where(full)
            c = [(t < -1).sum(), ((t >= -1) & (t < 0)).sum(), ((t >= 0) & (t < args.hold + args.lat)).sum(), (t >= args.hold + args.lat).sum(), (~full).sum()]
            P(f"| {v} · {fm} | " + " | ".join(f"{x / len(ok) * 100:.1f} %" for x in c) +
              f" | {t.median():.1f} | {t.quantile(.9):.1f} | {t.quantile(.99):.1f} |")
    # ---- the tail
    P("\n## The tail: per-trip (exit − MODEL) in bp of the entry notional\n")
    P("| exit · fill model | p1 | p5 | p10 | p25 | median | p75 | p90 | mean | share < −100 bp | share < −250 bp |")
    P("|---|---|---|---|---|---|---|---|---|---|---|")
    for v in VAR:
        for fm in FM:
            x = (ok[f"pl_{v}|{fm}"] - model) / (ok.n * ok.entry_px) * 1e4
            P(f"| {v} · {fm} | " + " | ".join(f"{x.quantile(p):+.0f}" for p in (.01, .05, .1, .25, .5, .75, .9)) +
              f" | {x.mean():+.1f} | {(x < -100).mean() * 100:.2f} % | {(x < -250).mean() * 100:.2f} % |")
    bp = lambda v, fm: (ok[f"pl_{v}|{fm}"] - model) / (ok.n * ok.entry_px) * 1e4
    for lead, other in (("MAKER", "REST60"), ("REST60", "MAKER")):
        P(f"\n### The worst 20 trips by {lead} (queue), {other} beside (bp vs MODEL; delays s vs T+1)\n")
        P(f"| trip | entry \\$ | model exit \\$ | {lead} bp | {lead} fill \\$ | {lead} delay | {other} bp | {other} fill \\$ | {other} delay |")
        P("|---|---|---|---|---|---|---|---|---|")
        x = bp(lead, "queue")
        for i in x.nsmallest(20).index:
            r = ok.loc[i]
            P(f"| {r.symbol} {r.trade_date} {int(r.signal_sec)} | {r.entry_px:.4f} | {r.exit_px:.4f} | {x[i]:+.0f} | {r[f'px_{lead}|queue']:.4f} | {r[f't1_{lead}|queue']:.0f} | "
              f"{bp(other, 'queue')[i]:+.0f} | {r[f'px_{other}|queue']:.4f} | {r[f't1_{other}|queue']:.0f} |")
    P("\n### Does REST60 cut the MAKER tail? Trips by MAKER − MODEL (bp), the mean of each exit on the same trips\n")
    P("| MAKER − MODEL bucket · fill model | trips | MAKER mean bp | REST60 mean bp | REST0 mean bp | RESTinf mean bp | REST60 better (share) |")
    P("|---|---|---|---|---|---|---|")
    for fm in ("queue", "hidden", "back"):
        xm = bp("MAKER", fm)
        for lab, m in (("< −250", xm < -250), ("−250 .. −100", (xm >= -250) & (xm < -100)), ("−100 .. −25", (xm >= -100) & (xm < -25)),
                       ("−25 .. +25", (xm >= -25) & (xm <= 25)), ("> +25", xm > 25)):
            P(f"| {lab} · {fm} | {int(m.sum())} | {xm[m].mean():+.0f} | {bp('REST60', fm)[m].mean():+.0f} | {bp('REST0', fm)[m].mean():+.0f} | "
              f"{bp('RESTinf', fm)[m].mean():+.0f} | {(bp('REST60', fm)[m] > xm[m]).mean() * 100:.0f} % |")
    # ---- the cost of the rule
    P("\n## The cost of the rule: REST60 vs the MODEL exit, per trip (queue)\n")
    P("| REST60 completed | trips | REST60 < model: n / Σ \\$ | REST60 > model: n / Σ \\$ | equal | net Δ \\$ |")
    P("|---|---|---|---|---|---|")
    q = ok["q_REST60|queue"]; full = q >= ok.n - 1e-9; t = ok["t1_REST60|queue"]; d = ok["pl_REST60|queue"] - model
    for lab, m in (("before the trigger bar", full & (t < -1)), ("in the trigger bar", full & (t >= -1) & (t < 0)),
                   ("T+1 .. T+61", full & (t >= 0) & (t < args.hold + args.lat)), ("after T+61 (maker)", full & (t >= args.hold + args.lat)), ("never (marked)", ~full)):
        dm = d[m]
        P(f"| {lab} | {int(m.sum())} | {int((dm < -1e-6).sum())} / {dm[dm < -1e-6].sum():+,.0f} | {int((dm > 1e-6).sum())} / {dm[dm > 1e-6].sum():+,.0f} | "
          f"{int((dm.abs() <= 1e-6).sum())} | {dm.sum():+,.0f} |")
    P(f"| all | {len(ok)} | {int((d < -1e-6).sum())} / {d[d < -1e-6].sum():+,.0f} | {int((d > 1e-6).sum())} / {d[d > 1e-6].sum():+,.0f} | {int((d.abs() <= 1e-6).sum())} | {d.sum():+,.0f} |")
    P("\nThe level vs the model exit: the REST60 price (ceil of the prior max) vs exit_px (the next bar's vwap), bp of exit_px:\n")
    lv = (ok.rest_px / ok.exit_px - 1) * 1e4
    P("| p5 | p25 | median | p75 | p95 | share rest_px < exit_px |\n|---|---|---|---|---|---|")
    P(f"| {lv.quantile(.05):+.0f} | {lv.quantile(.25):+.0f} | {lv.median():+.0f} | {lv.quantile(.75):+.0f} | {lv.quantile(.95):+.0f} | {(lv < 0).mean() * 100:.1f} % |")
    # ---- the control
    P("\n## CONTROL: REST60 with the resting price one tick down / up (the harness must move: lower = earlier, more fills, worse price)\n")
    P("| variant · fill model | full fills by T+61 | completed before the trigger bar | mean fill − model bp (filled) | Δ vs MODEL \\$ | Δ vs REST60 \\$ | t vs REST60 |")
    P("|---|---|---|---|---|---|---|")
    for fm in FM:
        for v in ("REST60_m1", "REST60", "REST60_p1"):
            q = ok[f"q_{v}|{fm}"]; full = q >= ok.n - 1e-9; t = ok[f"t1_{v}|{fm}"]
            fp = ((ok[f"px_{v}|{fm}"] - ok.exit_px) / ok.exit_px * 1e4)[full]
            dd = ok[f"pl_{v}|{fm}"] - ok[f"pl_REST60|{fm}"]
            P(f"| {v} · {fm} | {(full & (t < args.hold + args.lat)).mean() * 100:.1f} % | {(full & (t < -1)).mean() * 100:.1f} % | {fp.mean():+.1f} | "
              f"{(ok[f'pl_{v}|{fm}'] - model).sum():+,.0f} | {dd.sum():+,.0f} | {tstat(dd) if v != 'REST60' else float('nan'):.2f} |")
    P(f"\nResting re-pricings per trip (REST60, level changes entry → trigger): median {ok.n_level_changes.median():.0f}, p90 {ok.n_level_changes.quantile(.9):.0f}; "
      f"quote files with sizes in round lots (×100): {int(ok.get('lots', pd.Series(dtype=bool)).fillna(False).sum())}.")
    P("\n## Caveats\n")
    P("- Exits only: every trip's entry is assumed filled at entry_px at entry_sec, and the book's mc=1 sequence is kept as is "
      "(an earlier REST exit could admit a later trip on the same ticker-day that the book skipped; not modelled).")
    P("- The queue models credit ANY lit print at our price against the displayed queue, whatever its venue, odd lots included "
      "(e.g. RWT 2020-04-02: a burst of odd-lot prints at the level filled the resting order 146 s before the trigger). "
      "`back` is the pessimistic bracket for a resting order at a level other sellers also watch.")
    P("- The resting price is the level rounded UP; a level that sits exactly on a tick (a prior bar's vwap = 5.00) is a TIE "
      "the engine's strict `>` ignores but a limit at 5.00 fills on (RWT, NXTP in the hand checks); that share of the "
      "before-the-trigger fills was not measured.")
    P("- Take fees (marketable placements, the `taker` column) and maker rebates are not modelled; prices only.")
    P("- The order works only to min(exit_sec + 1,800 s, the close) — a DAY order; unfilled shares are marked at the last 1s vwap.")
    txt = "\n".join(out) + "\n"
    rp = os.path.join(args.out, f"report{sfx}.md"); open(rp, "w").write(txt); print(txt)
    log(f"wrote {rp}")
