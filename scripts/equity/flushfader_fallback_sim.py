r"""FlushFader maker entry with a MIDPOINT FALLBACK (user 2026-11-14) — the cap sim (flushfader_cap_sim.py, §S49ak) extended.

Why: on the week 2026-11-09..13 the cap sim reproduced the live entries leg by leg (the private repo's fill_sim_check.py):
live ≈ sim, and the gap to the model is the RULE — the maker bid misses the trips that bounce straight off the low
(whole book, queue model: the rule keeps 76 % of the model; its 5 % unfilled trips carry a third of the model P&L).
The user: "we don't want to cross the spread ever unless it's only a tick. I've heard that real HFT shops try to get in at
the bid, and if that fails they switch to trying to get in at the midpoint."

The broker (Lightspeed Connect) has no midpoint-peg or hidden order type (OrderType MARKET / LIMIT / STOP / STOP_LIMIT /
MOC / LOC), so "the midpoint" is a DISPLAYED limit on the tick grid:
  phase 1 (as live, d2377d3): JOIN the bid and follow it both ways, never above C0 = the signal price ROUNDED UP.
  phase 2 (from the trigger on, while unfilled): on a spread of 2+ ticks, a bid at the MIDPOINT rounded DOWN to the tick
          (strictly above the bid: alone at a new price, nobody ahead); on a ONE-tick spread the midpoint is a sub-penny
          price that cannot be quoted, so either TAKE the ask (`take`) or stay on the bid (`notake`). Never above C1.
          The price follows every quote; each re-pricing is live after LAT. A buy at or above the current ask fills at the
          ask, up to the ask's displayed size (the rest rests at the limit).
  triggers: T s after the order goes live (1, 3, 10, 30), or `run` = the moment the bid rises above C0 (the market leaving).
  C1: C0 (no room past the cap) · C0 + 1 / 2 ticks · the signal + 50 / 100 bp rounded up.
  control: no trigger = the live rule. On every trip whose signal vwap is off the grid it must equal the cap sim's
           "signal +1 tick" column (floor(s + tick) = ceil(s) there) — checked against data/equity/flushfader_cap_sim_all.
⚠ The sim's NBBO does not contain our own order. Live, a bid we post at the midpoint BECOMES the bid, so a live rule must
  not re-derive the midpoint from a bid that is its own (it would step up against itself — the 10-27 "bid + tick" lesson).

Fees (venue only; the commission is the same in every rule): a resting fill earns the add credit (0.20c, ARCA's; nothing
under \$1), a taken share pays 0.30c (30 bp of value under \$1). P&L at the book's exit_px; the model fills every trip at
entry_px. Fill models lit / queue / hidden as the cap sim.

Run from research/ (quotes come from the cap sim's cache; nothing is fetched):
    python -u scripts/equity/flushfader_fallback_sim.py --per-year 500 --out data/equity/flushfader_fallback_sim > logs/flushfader_fallback_sim.log 2>&1
    python -u scripts/equity/flushfader_fallback_sim.py --per-year 100000 --out data/equity/flushfader_fallback_sim_all > logs/flushfader_fallback_sim_all.log 2>&1
"""
import argparse, datetime, json, math, os, time, zoneinfo
import numpy as np, pandas as pd, duckdb
from numba import njit

ap = argparse.ArgumentParser()
ap.add_argument("--book", default="data/flushfader_gate_review/broad_reference_book.parquet")
ap.add_argument("--trades", default="data/bulk/trades")
ap.add_argument("--quotes", default="data/equity/flushfader_cap_sim/quotes")
ap.add_argument("--out", default="data/equity/flushfader_fallback_sim")
ap.add_argument("--control", default="data/equity/flushfader_cap_sim_all/sim.parquet")
ap.add_argument("--per-year", type=int, default=500)
ap.add_argument("--seed", type=int, default=20261030)
ap.add_argument("--notional", type=float, default=1000.0)
ap.add_argument("--lat", type=float, default=0.216)
args = ap.parse_args()
T0 = time.time()
def log(*a): print(f"[{time.time() - T0:7.1f}s]", *a, flush=True)
NY = zoneinfo.ZoneInfo("America/New_York")
def ns_at(d, sec):
    y, m, dd = map(int, d.split("-")); return int(datetime.datetime(y, m, dd, tzinfo=NY).timestamp() * 1e9) + int(sec * 1e9)

B = duckdb.sql(f"select * from '{args.book}'").df(); B["year"] = B.trade_date.str[:4]
S = B.groupby("year").sample(n=args.per_year, random_state=args.seed) if B.year.value_counts().min() >= args.per_year else B.copy()
S = S.sort_values(["trade_date", "symbol", "signal_sec"]).reset_index(drop=True)
S["key"] = S.symbol + "_" + S.trade_date + "_" + S.signal_sec.astype(str)
os.makedirs(args.out, exist_ok=True)
log(f"sample: {len(S):,} trips of {len(B):,}")

@njit(cache=True)
def run(et, ets, eb, ebs, ea, eas, ep, es, elit, i0, live_from, end, c0, c1, trig, take, mid, tick, n, fm, lat, bid0, bsz0, ask0, asz0):
    """(shares, cost, last fill time, shares taken). trig: < 0 never (the live rule), 0 = the bid rises above c0, > 0 = T s"""
    INF = 1e18
    def queue_for(px, b, bs):
        if fm == 0: return 0.0
        if px < b - 1e-9: return bs if fm == 2 else 0.0
        if abs(px - b) > 1e-9: return 0.0
        return bs
    def target(ph, b, a):
        if ph == 1: return min(b, c0)
        if not mid: return min(b, c1)              # `join`: the bid, under the raised (or no) ceiling
        if a - b <= tick + 1e-9:
            if take and a <= c1 + 1e-9: return a
            return min(b, c1)
        m = math.floor((b + a) / 2.0 / tick + 1e-6) * tick
        if m <= b + 1e-9: m = b + tick
        return min(m, c1)
    ph = 1; t_switch = live_from - lat + trig if trig > 0 else INF
    px = min(bid0, c0); qa = queue_for(px, bid0, bsz0)
    got = 0.0; cost = 0.0; tfill = -1.0; taken = 0.0; pend_t = -1.0; pend_px = 0.0
    lb = bid0; lbs = bsz0; la = ask0; las = asz0
    for i in range(i0, len(et)):
        ts = ets[i]
        if ts > end or got >= n: break
        # the time trigger
        if ph == 1 and ts >= t_switch:
            ph = 2; nx = target(2, lb, la); cur = pend_px if pend_t >= 0 else px
            if abs(nx - cur) > 1e-9:
                if pend_t < 0: pend_t = t_switch + lat
                pend_px = nx
        if pend_t >= 0 and ts >= pend_t:
            px = pend_px; pend_t = -1.0; qa = queue_for(px, lb, lbs)
            if ph == 2 and px >= la - 1e-9 and las > 0:          # marketable on arrival: lift the ask (phase 2 only: phase 1 = the cap sim exactly)
                g = min(n - got, las); got += g; cost += g * la; taken += g; tfill = ts; las -= g
                if got >= n: break
        if et[i] == 0:
            b = eb[i]; a = ea[i]
            if b <= 0 or a <= 0: continue
            lb = b; lbs = ebs[i]; la = a; las = eas[i]
            if ph == 1 and trig == 0 and b > c0 + 1e-9: ph = 2
            nx = target(ph, b, a); cur = pend_px if pend_t >= 0 else px
            if abs(nx - cur) > 1e-9:
                if pend_t < 0: pend_t = ts + lat
                pend_px = nx
            elif ph == 2 and pend_t < 0 and px >= a - 1e-9 and las > 0:   # resting at/above a new ask: it trades with us
                g = min(n - got, las); got += g; cost += g * a; taken += g; tfill = ts; las -= g
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
    return got, cost, tfill, taken

TRIG = [("never", -1.0), ("1s", 1.0), ("3s", 3.0), ("10s", 10.0), ("30s", 30.0), ("run", 0.0)]
C1S = ["C0", "C0+1t", "C0+2t", "s+50bp", "s+100bp", "∞"]
# 2026-11-14 whole book (user): the `take` variants dropped (they lost everywhere on the sample); two phase-2 modes —
#   mid  · C1: a bid at the midpoint (2+ tick spreads; a one-tick spread stays on the bid), never above C1
#   join · C1: keep JOINING the bid, the cap raised to C1 (∞ = lifted: "wait a certain amount for the bid to hit
#              otherwise lift the cap completely")
RULES = ([("none (live rule)", -1.0, "C0", False, False)]
         + [(f"{tn} · mid · {c}", tv, c, False, True) for tn, tv in TRIG[1:] for c in C1S]
         + [(f"{tn} · join · {c}", tv, c, False, False) for tn, tv in TRIG[1:] for c in C1S[1:]])
FM = ["lit", "queue", "hidden"]
EXCL = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53, 8, 15, 19]::UTINYINT[]"
con = duckdb.connect(); con.execute("SET memory_limit='2GB'"); con.execute("SET threads=4")
# RESUMABLE (2026-11-14: the first whole-book run died at day ~250 when other jobs took the RAM): each day's rows go to
# <out>/days/<date>.parquet; a rerun skips the days already written.
ddir = os.path.join(args.out, "days"); os.makedirs(ddir, exist_ok=True)
days = S.groupby("trade_date")
for di, (d, g) in enumerate(days):
    dpath = os.path.join(ddir, f"{d}.parquet")
    if os.path.exists(dpath): continue
    rows = []
    # the tape per TRIP, over its own window and only the fill-eligible prints (2026-11-14: one query per day over every
    # ticker's whole span was OOM-killed at 5G on 2021-01-27, the meme-stock peak)
    tpath = os.path.join(args.trades, d + ".parquet")
    def tape(sym, lo, hi):
        return con.execute(f"""SELECT sip_timestamp AS ts, price, size, exchange <> 4 AS lit FROM read_parquet('{tpath}')
            WHERE ticker = '{sym}' AND size > 0 AND sip_timestamp >= {ns_at(d, lo)} AND sip_timestamp < {ns_at(d, hi)}
              AND NOT list_has_any(conditions, {EXCL}) ORDER BY ts""").df()
    midnight = ns_at(d, 0)
    for r in g.itertuples():
        rec = dict(symbol=r.symbol, trade_date=d, year=r.year, signal_sec=r.signal_sec, exit_sec=r.exit_sec,
                   signal_vwap=r.signal_vwap, entry_px=r.entry_px, exit_px=r.exit_px)
        qp = os.path.join(args.quotes, f"{r.key}.parquet")
        if not os.path.exists(qp): rec["err"] = "no quote file"; rows.append(rec); continue
        Q = pd.read_parquet(qp).dropna()
        if len(Q) == 0: rec["err"] = "no quotes"; rows.append(rec); continue
        if Q.bid_size.median() < 50: Q["bid_size"] *= 100; Q["ask_size"] *= 100
        X = tape(r.symbol, r.signal_sec - 5, r.exit_sec + 2)
        qs = (Q.ts.values - midnight) / 1e9; fs = (X.ts.values - midnight) / 1e9
        et = np.concatenate([np.zeros(len(qs), np.int8), np.ones(len(fs), np.int8)]); ets = np.concatenate([qs, fs]); o = np.lexsort((et, ets))
        et, ets = et[o], ets[o]
        z = np.zeros(len(fs))
        eb = np.concatenate([Q.bid.values, z])[o]; ebs = np.concatenate([Q.bid_size.values.astype(float), z])[o]
        ea = np.concatenate([Q.ask.values, z])[o]; eas = np.concatenate([Q.ask_size.values.astype(float), z])[o]
        ep = np.concatenate([np.zeros(len(qs)), X.price.values])[o]; es = np.concatenate([np.zeros(len(qs)), X["size"].values.astype(float)])[o]
        elit = np.concatenate([np.zeros(len(qs), np.bool_), X.lit.values.astype(np.bool_)])[o]
        s0 = r.signal_sec + 1.0
        qi = np.flatnonzero((et == 0) & (ets <= s0) & (eb > 0) & (ea > 0))
        if len(qi) == 0: rec["err"] = "no quote at the signal"; rows.append(rec); continue
        j = qi[-1]; bid0, bsz0, ask0, asz0 = eb[j], ebs[j], ea[j], eas[j]
        i0 = int(np.searchsorted(ets, s0, side="right"))
        tk = 0.01 if r.signal_vwap >= 1 else 0.0001
        c0 = math.ceil(r.signal_vwap / tk - 1e-6) * tk
        C1 = {"C0": c0, "C0+1t": c0 + tk, "C0+2t": c0 + 2 * tk,
              "s+50bp": max(c0, math.ceil(r.signal_vwap * 1.005 / tk - 1e-6) * tk), "s+100bp": max(c0, math.ceil(r.signal_vwap * 1.01 / tk - 1e-6) * tk),
              "∞": 1e9}
        n = float(max(1, math.floor(args.notional / r.signal_vwap)))
        rec.update(n=n, c0=c0, spread_ticks=round((ask0 - bid0) / tk), on_grid=abs(r.signal_vwap / tk - round(r.signal_vwap / tk)) < 1e-6)
        for name, trig, c, take, mid in RULES:
            for k, fm in enumerate(FM):
                got, cost, tf, tkn = run(et, ets, eb, ebs, ea, eas, ep, es, elit, i0, s0 + args.lat, float(r.exit_sec), c0, C1[c], trig, take, mid,
                                         tk, n, k, args.lat, bid0, bsz0, ask0, asz0)
                key = f"{name}|{fm}"
                rec["q_" + key] = got; rec["px_" + key] = cost / got if got else math.nan; rec["tk_" + key] = tkn
                rec["pl_" + key] = (r.exit_px - cost / got) * got if got else 0.0
                # venue fees: + add credit on the resting shares, − the take fee on the taken ones
                mk = got - tkn
                rec["fee_" + key] = (0.0020 * mk - 0.0030 * tkn) if r.signal_vwap >= 1 else (-0.003 * (cost / got if got else 0) * tkn)
        rows.append(rec)
    pd.DataFrame(rows).to_parquet(dpath + ".tmp", index=False); os.replace(dpath + ".tmp", dpath)
    if (di + 1) % 100 == 0: log(f"  {di + 1:,}/{days.ngroups:,} days")
R = pd.concat([pd.read_parquet(os.path.join(ddir, f"{d}.parquet")) for d in days.groups], ignore_index=True)
R.to_parquet(os.path.join(args.out, "sim.parquet"), index=False)
ok = R[R["err"].isna()] if "err" in R else R
log(f"simulated {len(ok):,} of {len(R):,}")

# ---- the control: the live rule = the cap sim's "signal +1 tick" on the off-grid trips ----
if os.path.exists(args.control):
    C = pd.read_parquet(args.control, columns=["symbol", "trade_date", "signal_sec"] + [f"pl_signal +1 tick|{f}" for f in FM])
    J = ok.merge(C, on=["symbol", "trade_date", "signal_sec"])
    J = J[~J.on_grid.astype(bool)]
    for f in FM:
        dif = (J[f"pl_none (live rule)|{f}"] - J[f"pl_signal +1 tick|{f}"]).abs()
        print(f"CONTROL {f}: {len(J):,} off-grid trips vs the cap sim's +1 tick — |Δ| > 1e-6 on {int((dif > 1e-6).sum())}, total Δ {dif.sum():.4f}")

model = ((ok.exit_px - ok.entry_px) * ok.n).sum(); notional = (ok.n * ok.signal_vwap).sum()
print(f"\nthe MODEL (every trip at entry_px): ${model:+,.0f} on ${notional:,.0f} ({model / notional * 1e4:+.1f} bp)")
print(f"spread at the signal, ticks: 1 = {(ok.spread_ticks <= 1).mean():.0%}, 2 = {(ok.spread_ticks == 2).mean():.0%}, 3+ = {(ok.spread_ticks >= 3).mean():.0%}\n")
for fm in FM:
    base = ok[f"pl_none (live rule)|{fm}"] + ok[f"fee_none (live rule)|{fm}"]
    print(f"## fill model: {fm} — P&L net of venue fees; Δ vs the live rule\n")
    print("| rule | filled full / part / none | taken shares % | entry vs model bp | P&L $ | % of the model | **Δ $** | t (by day) | years Δ > 0 | unfilled: model P&L $ |")
    print("|---|---|---|---|---|---|---|---|---|---|")
    for name, *_ in RULES:
        q = ok[f"q_{name}|{fm}"]; px = ok[f"px_{name}|{fm}"]; f = q > 0
        net = ok[f"pl_{name}|{fm}"] + ok[f"fee_{name}|{fm}"]; dd = net - base
        bd = dd.groupby(ok.trade_date).sum(); t = bd.mean() / (bd.std(ddof=1) / math.sqrt(len(bd))) if bd.std() > 0 else 0.0
        yy = dd.groupby(ok.year).sum(); un = ok[~f]
        ebp = ((ok.entry_px - px) * q)[f].sum() / (ok.entry_px * q)[f].sum() * 1e4
        print(f"| {name} | {int((q >= ok.n).sum())} / {int((f & (q < ok.n)).sum())} / {int((~f).sum())} | {100 * ok[f'tk_{name}|{fm}'].sum() / max(q.sum(), 1):.1f} | "
              f"{ebp:+.1f} | {net.sum():+,.0f} | {100 * net.sum() / model:.0f} % | **{dd.sum():+,.0f}** | {t:.2f} | {int((yy > 0).sum())} / {len(yy)} | "
              f"{((un.exit_px - un.entry_px) * un.n).sum():+,.0f} |")
    print()
log("done")
