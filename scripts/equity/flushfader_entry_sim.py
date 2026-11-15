r"""FlushFader maker ENTRY rules, the FAST sim (user 2026-11-14) — §S49ap's flushfader_fallback_sim.py on caches, in parallel.

Reads only caches: the per-trip quotes (data/equity/flushfader_cap_sim/quotes/, Massive REST) and the per-day trip prints
(data/equity/flushfader_cap_sim/prints/, flushfader_print_cache.py). Days run in parallel processes. A rule set is
picked with --set; --only filters it by regex.

The order (all rules): phase 1 = live (d2377d3): JOIN the bid, follow it both ways, never above C0 = the signal ROUNDED
UP. Phase 2 from the trigger while unfilled; never above C1; never crosses (a phase-2 order the ask falls onto fills at
the ask, counted as taken). Each re-pricing is live after LAT. Modes:
  join    keep joining the bid under the raised ceiling C1
  mid     a bid at the midpoint rounded DOWN (2+ tick spreads; strictly above the bid); a one-tick spread stays on the bid
  ratchet the user's GRADUAL RATCHETING ("lifting the order above the best bid every 10s if we aren't getting any fills"):
          every STEP s without a fill (the clock restarts on every fill), one more tick above the best bid:
          price = min(C1, ask − 1 tick, bid + k ticks), k = the steps taken
  · est   "the bid" is the best bid OTHER THAN OURS, as live must see it (user 11-14: "How are we going to deal with that?"):
          the NBBO contains our own displayed bid, so live can only trust a quote whose bid is AT OR ABOVE our price (above:
          others pay more; at ours with a larger size: others are there too); below our price the NBBO shows only us, and
          the estimate keeps the last trusted bid. Emulated exactly: the estimate updates iff the true bid >= our live price.
          Without est the sim reads the true bid (the quotes never contain us) — the §S49ap results.
  · hold  skip a ratchet step while we are estimated FIRST in line at our price (the queue model's count of the
          displayed size that was there when we arrived, less the lit prints at our price) — the user: "we might be in
          front of the queue and giving up position".
Fill models lit / queue / hidden as the cap sim. P&L at the book's exit_px, net of venue fees (add +0.20c, take −0.30c;
under \$1 no credit, take 30 bp). The control: the live rule = the cap sim's "signal +1 tick" on every off-grid trip.

Run from research/:  python -u scripts/equity/flushfader_entry_sim.py --set ratchet --out data/equity/flushfader_entry_sim_ratchet > logs/flushfader_entry_sim_ratchet.log 2>&1
"""
import argparse, datetime, math, os, re, time, zoneinfo
from multiprocessing import Pool
import numpy as np, pandas as pd, duckdb
from numba import njit

ap = argparse.ArgumentParser()
ap.add_argument("--book", default="data/flushfader_gate_review/broad_reference_book.parquet")
ap.add_argument("--quotes", default="data/equity/flushfader_cap_sim/quotes")
ap.add_argument("--prints", default="data/equity/flushfader_cap_sim/prints")
ap.add_argument("--out", required=True)
ap.add_argument("--control", default="data/equity/flushfader_cap_sim_all/sim.parquet")
ap.add_argument("--set", default="nbbo", choices=("s49ap", "ratchet", "ratchet2", "nbbo", "nbbo2"))
ap.add_argument("--only", help="keep only the rules matching this regex (the live rule always)")
ap.add_argument("--per-year", type=int, default=100000)
ap.add_argument("--seed", type=int, default=20261030)
ap.add_argument("--notional", type=float, default=1000.0)
ap.add_argument("--lat", type=float, default=0.216)
ap.add_argument("--workers", type=int, default=8)
args = ap.parse_args()
NY = zoneinfo.ZoneInfo("America/New_York")
def ns_at(d, sec):
    y, m, dd = map(int, d.split("-")); return int(datetime.datetime(y, m, dd, tzinfo=NY).timestamp() * 1e9) + int(sec * 1e9)

JOIN, MID, RATCHET, NBBO_STEP, OWN_STEP = 0, 1, 2, 3, 4

@njit(cache=True)
def run(et, ets, eb, ebs, ea, eas, ep, es, elit, i0, live_from, end, c0, c1, trig, mode, est, hold, step, kmax, eff, tick, n, fm, lat,
        bid0, bsz0, ask0, asz0):
    """(shares, cost, last fill time, shares taken). trig < 0: never (the live rule); 0: the bid rises above c0; > 0: T s"""
    INF = 1e18
    def queue_for(px, b, bs, f):
        if f == 0: return 0.0
        if px < b - 1e-9: return bs if f == 2 else 0.0
        if abs(px - b) > 1e-9: return 0.0
        return bs
    def target(ph, k, b, a):
        if ph == 1: return min(b, c0)
        if mode == 0: return min(b, c1)
        if mode == 1:
            if a - b <= tick + 1e-9: return min(b, c1)
            m = math.floor((b + a) / 2.0 / tick + 1e-6) * tick
            if m <= b + 1e-9: m = b + tick
            return min(m, c1)
        p = b + k * tick
        if p > a - tick + 1e-9: p = a - tick
        if p < b: p = b
        return min(p, c1)
    ph = 1; k = 0
    t_next = live_from - lat + trig if trig > 0 else INF
    px = min(bid0, c0); qa = queue_for(px, bid0, bsz0, fm); qe = queue_for(px, bid0, bsz0, 1)
    got = 0.0; cost = 0.0; tfill = -1.0; taken = 0.0; pend_t = -1.0; pend_px = 0.0
    lb = bid0; lbs = bsz0; la = ask0; las = asz0; bref = bid0
    for i in range(i0, len(et)):
        ts = ets[i]
        if ts > end or got >= n: break
        while ts >= t_next:                                # the time trigger / ratchet steps, AT their scheduled times
            tdue = t_next
            due = True
            if hold and mode == 2 and qe <= 1e-9 and px >= bref - 1e-9: due = False   # first in line: keep the place
            if due and mode >= 3:
                # NBBO STEP (user 2026-11-14): "moving the order every 5s after not fill above the best bid regardless of
                # whether it's ours" — live sees max(the market's bid, our own displayed bid); never down at a step
                ph = 2; cur = pend_px if pend_t >= 0 else px
                # mode 4 (`own-step`, the user's min variant): one tick above OUR price, ignoring a higher bid
                nx = min(c1, la - tick, (max(lb, cur) if mode == 3 else cur) + tick)
                if nx > cur + 1e-9:
                    if pend_t < 0: pend_t = tdue + lat
                    pend_px = nx
                due = False
            if due:
                if ph == 1:
                    ph = 2
                    if mode == 2: k = 1
                elif mode == 2 and k < kmax:
                    bb = bref if est else lb
                    # `eff` (user 2026-11-14): k grows only when the step actually RAISES the price — a step held back by
                    # the ask or the ceiling is not banked for a jump later
                    if (not eff) or target(ph, k + 1, bb, la) > target(ph, k, bb, la) + 1e-9: k += 1
                nx = target(ph, k, bref if est else lb, la); cur = pend_px if pend_t >= 0 else px
                if abs(nx - cur) > 1e-9:
                    if pend_t < 0: pend_t = tdue + lat
                    pend_px = nx
            t_next = tdue + step if mode >= 2 else INF
        if pend_t >= 0 and ts >= pend_t:
            px = pend_px; pend_t = -1.0; qa = queue_for(px, lb, lbs, fm); qe = queue_for(px, lb, lbs, 1)
            if ph == 2 and px >= la - 1e-9 and las > 0:
                g = min(n - got, las); got += g; cost += g * la; taken += g; tfill = ts; las -= g
                if mode >= 2: t_next = ts + step
                if got >= n: break
        if et[i] == 0:
            b = eb[i]; a = ea[i]
            if b <= 0 or a <= 0: continue
            lb = b; lbs = ebs[i]; la = a; las = eas[i]
            if (not est) or b >= px - 1e-9: bref = b
            if ph == 1 and trig == 0 and b > c0 + 1e-9:
                ph = 2
                if mode == 2: k = 1
            if mode >= 3 and ph == 2:
                # between steps: hold the price; with `follow` (est) join a market bid that rises ABOVE us — a bid above
                # our price cannot be ours, so this needs no identification
                cur = pend_px if pend_t >= 0 else px
                nx = min(b, c1) if (est and b > cur + 1e-9) else cur
            else:
                nx = target(ph, k, bref if est else b, a); cur = pend_px if pend_t >= 0 else px
            if abs(nx - cur) > 1e-9:
                if pend_t < 0: pend_t = ts + lat
                pend_px = nx
            elif ph == 2 and pend_t < 0 and px >= a - 1e-9 and las > 0:
                g = min(n - got, las); got += g; cost += g * a; taken += g; tfill = ts; las -= g
                if mode >= 2: t_next = ts + step
        else:
            if ts < live_from: continue
            p = ep[i]; q = es[i]; lit = elit[i]
            if lit and abs(p - px) < 1e-9: qe = max(0.0, qe - q)
            through = (lit and p < px - 1e-9) or p <= px - tick + 1e-9
            f = 0.0
            if fm == 0:
                if lit and p <= px + 1e-9: f = q
            elif through: f = q
            elif lit and abs(p - px) < 1e-9:
                eat = min(qa, q); qa -= eat; f = q - eat
            if f > 0:
                g = min(f, n - got); got += g; cost += g * px; tfill = ts
                if mode >= 2: t_next = ts + step              # "if we aren't getting any fills": the clock restarts
    return got, cost, tfill, taken

C1S = ["C0", "C0+1t", "C0+2t", "C0+3t", "s+50bp", "s+100bp", "∞"]
def rules():
    # (name, trig, C1, mode, est, hold, step)
    R = [("none (live rule)", -1.0, "C0", JOIN, False, False, 0.0)]
    if args.set == "s49ap":     # §S49ap's grid, for the equality check against flushfader_fallback_sim_all
        for tn, tv in [("1s", 1.0), ("3s", 3.0), ("10s", 10.0), ("30s", 30.0), ("run", 0.0)]:
            R += [(f"{tn} · mid · {c}", tv, c, MID, False, False, 0.0) for c in ["C0", "C0+1t", "C0+2t", "s+50bp", "s+100bp", "∞"]]
            R += [(f"{tn} · join · {c}", tv, c, JOIN, False, False, 0.0) for c in ["C0+1t", "C0+2t", "s+50bp", "s+100bp", "∞"]]
    elif args.set == "ratchet":
        for tn, tv, c in [("3s", 3.0, "C0+1t"), ("10s", 10.0, "C0+1t"), ("10s", 10.0, "C0+2t"), ("10s", 10.0, "C0+3t"), ("30s", 30.0, "C0+2t")]:
            R += [(f"{tn} · mid · {c}", tv, c, MID, False, False, 0.0), (f"{tn} · mid · {c} · est", tv, c, MID, True, False, 0.0)]
        for st in (3.0, 5.0, 10.0, 20.0):
            for c in ["C0+1t", "C0+2t", "C0+3t", "s+50bp", "s+100bp", "∞"]:
                R += [(f"ratchet {st:g}s · {c} · est", st, c, RATCHET, True, False, st),
                      (f"ratchet {st:g}s · {c} · est · hold", st, c, RATCHET, True, True, st)]
    if args.set == "ratchet2":   # 2026-11-14: k grows only when the price moves (`eff`), and k = 1 always ("penny the bid")
        R += [("ratchet 5s · s+100bp · est · hold (old: k banks)", 5.0, "s+100bp", RATCHET, True, True, 5.0, 10**6, False)]
        for st in (3.0, 5.0, 10.0):
            for c in ["C0+2t", "s+50bp", "s+100bp", "∞"]:
                for hd in (False, True):
                    h = " · hold" if hd else ""
                    R += [(f"ratchet {st:g}s · {c} · est{h}", st, c, RATCHET, True, hd, st, 10**6, True),
                          (f"k=1 {st:g}s · {c} · est{h}", st, c, RATCHET, True, hd, st, 1, True)]
    if args.set == "nbbo":   # 2026-11-14: a step every T s without fills to one tick above the bid live SEES (ours included)
        R += [("k=1 5s · s+100bp · est (reference)", 5.0, "s+100bp", RATCHET, True, False, 5.0, 1, True)]
        for st in (3.0, 5.0, 10.0):
            for c in ["C0+1t", "C0+2t", "C0+3t", "s+50bp", "s+100bp", "∞"]:
                R += [(f"nbbo-step {st:g}s · {c}", st, c, NBBO_STEP, False, False, st, 10**6, True),
                      (f"nbbo-step {st:g}s · {c} · follow-up", st, c, NBBO_STEP, True, False, st, 10**6, True)]
    if args.set == "nbbo2":  # 2026-11-14: the step from max(our price, NBBO bid) vs from our own price only (min variant)
        for st in (3.0, 5.0, 10.0):
            for c in ["s+50bp", "s+100bp"]:
                R += [(f"nbbo-step {st:g}s · {c}", st, c, NBBO_STEP, False, False, st, 10**6, True),
                      (f"own-step {st:g}s · {c}", st, c, OWN_STEP, False, False, st, 10**6, True)]
    if args.only: R = [R[0]] + [r for r in R[1:] if re.search(args.only, r[0])]
    return [r if len(r) == 9 else r + (10**6, False) for r in R]    # (…, kmax, eff): the older sets bank k (unbounded, not eff)
RULES = rules()
FM = ["lit", "queue", "hidden"]

def sim_day(job):
    d, g = job
    P = pd.read_parquet(os.path.join(args.prints, f"{d}.parquet"))
    idx = {}
    if len(P):                                   # the cache is sorted by (key, ts): each trip is one contiguous slice
        keys, starts = np.unique(P.key.values, return_index=True)
        o = np.argsort(starts); keys, starts = keys[o], starts[o]
        idx = dict(zip(keys, zip(starts, np.r_[starts[1:], len(P)])))
    ts_all, px_all, sz_all, lit_all = P.ts.values, P.price.values, P["size"].values.astype(float), P.lit.values.astype(np.bool_)
    midnight = ns_at(d, 0); out = []
    for r in g.itertuples():
        rec = dict(symbol=r.symbol, trade_date=d, year=r.year, signal_sec=r.signal_sec, exit_sec=r.exit_sec,
                   signal_vwap=r.signal_vwap, entry_px=r.entry_px, exit_px=r.exit_px)
        Q = pd.read_parquet(os.path.join(args.quotes, f"{r.key}.parquet")).dropna()
        if len(Q) == 0: rec["err"] = "no quotes"; out.append(rec); continue
        if Q.bid_size.median() < 50: Q["bid_size"] *= 100; Q["ask_size"] *= 100
        s, e = idx.get(r.key, (0, 0))
        fs = (ts_all[s:e] - midnight) / 1e9; qs = (Q.ts.values - midnight) / 1e9
        et = np.concatenate([np.zeros(len(qs), np.int8), np.ones(len(fs), np.int8)]); ets = np.concatenate([qs, fs]); o = np.lexsort((et, ets))
        et, ets = et[o], ets[o]; z = np.zeros(len(fs))
        eb = np.concatenate([Q.bid.values, z])[o]; ebs = np.concatenate([Q.bid_size.values.astype(float), z])[o]
        ea = np.concatenate([Q.ask.values, z])[o]; eas = np.concatenate([Q.ask_size.values.astype(float), z])[o]
        ep = np.concatenate([np.zeros(len(qs)), px_all[s:e]])[o]; es = np.concatenate([np.zeros(len(qs)), sz_all[s:e]])[o]
        elit = np.concatenate([np.zeros(len(qs), np.bool_), lit_all[s:e]])[o]
        s0 = r.signal_sec + 1.0
        qi = np.flatnonzero((et == 0) & (ets <= s0) & (eb > 0) & (ea > 0))
        if len(qi) == 0: rec["err"] = "no quote at the signal"; out.append(rec); continue
        j = qi[-1]; bid0, bsz0, ask0, asz0 = eb[j], ebs[j], ea[j], eas[j]
        i0 = int(np.searchsorted(ets, s0, side="right"))
        tk = 0.01 if r.signal_vwap >= 1 else 0.0001
        c0 = math.ceil(r.signal_vwap / tk - 1e-6) * tk
        C1 = {"C0": c0, "C0+1t": c0 + tk, "C0+2t": c0 + 2 * tk, "C0+3t": c0 + 3 * tk,
              "s+50bp": max(c0, math.ceil(r.signal_vwap * 1.005 / tk - 1e-6) * tk),
              "s+100bp": max(c0, math.ceil(r.signal_vwap * 1.01 / tk - 1e-6) * tk), "∞": 1e9}
        n = float(max(1, math.floor(args.notional / r.signal_vwap)))
        rec.update(n=n, c0=c0, on_grid=bool(abs(r.signal_vwap / tk - round(r.signal_vwap / tk)) < 1e-6))
        for name, trig, c, mode, est, hold, step, kmax, eff in RULES:
            for k, fm in enumerate(FM):
                got, cost, tf, tkn = run(et, ets, eb, ebs, ea, eas, ep, es, elit, i0, s0 + args.lat, float(r.exit_sec), c0, C1[c],
                                         trig, mode, est, hold, step, kmax, eff, tk, n, k, args.lat, bid0, bsz0, ask0, asz0)
                key = f"{name}|{fm}"
                rec["q_" + key] = got; rec["px_" + key] = cost / got if got else math.nan; rec["tk_" + key] = tkn
                rec["pl_" + key] = (r.exit_px - cost / got) * got if got else 0.0
                mk = got - tkn
                rec["fee_" + key] = (0.0020 * mk - 0.0030 * tkn) if r.signal_vwap >= 1 else (-0.003 * (cost / got if got else 0) * tkn)
        out.append(rec)
    return pd.DataFrame(out)

if __name__ == "__main__":
    T0 = time.time()
    def log(*a): print(f"[{time.time() - T0:7.1f}s]", *a, flush=True)
    B = duckdb.sql(f"select * from '{args.book}'").df(); B["year"] = B.trade_date.str[:4]
    S = B.groupby("year").sample(n=args.per_year, random_state=args.seed) if B.year.value_counts().min() >= args.per_year else B.copy()
    S = S.sort_values(["trade_date", "symbol", "signal_sec"]).reset_index(drop=True)
    S["key"] = S.symbol + "_" + S.trade_date + "_" + S.signal_sec.astype(str)
    os.makedirs(args.out, exist_ok=True)
    jobs = list(S.groupby("trade_date"))
    log(f"{len(S):,} trips, {len(jobs):,} days, {len(RULES)} rules × {len(FM)} fill models, {args.workers} workers")
    parts = []
    with Pool(args.workers) as p:
        for i, df in enumerate(p.imap_unordered(sim_day, jobs, chunksize=4)):
            parts.append(df)
            if (i + 1) % 200 == 0: log(f"  {i + 1:,}/{len(jobs):,} days")
    R = pd.concat(parts, ignore_index=True).sort_values(["trade_date", "symbol", "signal_sec"]).reset_index(drop=True)
    R.to_parquet(os.path.join(args.out, "sim.parquet"), index=False)
    ok = R[R["err"].isna()] if "err" in R else R
    log(f"simulated {len(ok):,} of {len(R):,}")
    if os.path.exists(args.control):
        C = pd.read_parquet(args.control, columns=["symbol", "trade_date", "signal_sec"] + [f"pl_signal +1 tick|{f}" for f in FM])
        J = ok.merge(C, on=["symbol", "trade_date", "signal_sec"]); J = J[~J.on_grid.astype(bool)]
        for f in FM:
            dif = (J[f"pl_none (live rule)|{f}"] - J[f"pl_signal +1 tick|{f}"]).abs()
            print(f"CONTROL {f}: {len(J):,} off-grid trips vs the cap sim's +1 tick — |Δ| > 1e-6 on {int((dif > 1e-6).sum())}, total Δ {dif.sum():.4f}")
    model = ((ok.exit_px - ok.entry_px) * ok.n).sum(); notional = (ok.n * ok.signal_vwap).sum()
    print(f"\nthe MODEL (every trip at entry_px): ${model:+,.0f} on ${notional:,.0f} ({model / notional * 1e4:+.1f} bp)\n")
    for fm in FM:
        base = ok[f"pl_none (live rule)|{fm}"] + ok[f"fee_none (live rule)|{fm}"]
        print(f"## fill model: {fm} — P&L net of venue fees; Δ vs the live rule\n")
        print("| rule | filled full / part / none | taken % | entry vs model bp | P&L $ | % of the model | **Δ $** | t (by day) | years Δ > 0 |")
        print("|---|---|---|---|---|---|---|---|---|")
        for name, *_ in RULES:
            q = ok[f"q_{name}|{fm}"]; px = ok[f"px_{name}|{fm}"]; f = q > 0
            net = ok[f"pl_{name}|{fm}"] + ok[f"fee_{name}|{fm}"]; dd = net - base
            bd = dd.groupby(ok.trade_date).sum(); t = bd.mean() / (bd.std(ddof=1) / math.sqrt(len(bd))) if bd.std() > 0 else 0.0
            yy = dd.groupby(ok.year).sum()
            ebp = ((ok.entry_px - px) * q)[f].sum() / (ok.entry_px * q)[f].sum() * 1e4
            print(f"| {name} | {int((q >= ok.n).sum())} / {int((f & (q < ok.n)).sum())} / {int((~f).sum())} | {100 * ok[f'tk_{name}|{fm}'].sum() / max(q.sum(), 1):.1f} | "
                  f"{ebp:+.1f} | {net.sum():+,.0f} | {100 * net.sum() / model:.0f} % | **{dd.sum():+,.0f}** | {t:.2f} | {int((yy > 0).sum())} / {len(yy)} |")
        print()
    log("done")
