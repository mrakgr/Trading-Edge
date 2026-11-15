r"""GapFader's 10:00 EXIT: today's maker vs the NBBO step (user 2026-11-14) — on gapfader_exit_cache.py's caches.

The 20,081 GF-54 book trades whose target did not fill before 10:00 sell at the 10:00 time stop. Live that is the maker
exit (`MakeSpreadRthOrMoo`: a sell AT THE ASK, following it both ways, uncapped). The user's step rule, adopted for
entries (§S49ap-2), as the exit, with NO floor ("The exit shouldn't have a floor"):
  step T   join the ask; every T s without a fill, one tick BELOW min(our price, the visible ask) — never up at a step,
           never at the bid, the price held between steps; the clock restarts on fills.
The sell is simulated as a BUY in NEGATED prices (bid' = −ask, ask' = −bid, print' = −price), so the order logic is
flushfader_entry_sim.py's tested `run`, unchanged (read from that file at start): today's maker = the live entry rule
with no cap; the step = `nbbo-step` with no ceiling. Fill models lit / queue / hidden mirror for the sell side.

Per trade: $1,000 / the cross (the book's open) shares, the order live from 10:00:00 + LAT until 10:30:00; shares still
unsold then are valued at the last fill-eligible print before 10:30 (and counted). The MODEL exit = the book's own
x_36000 (GF-54's 10:00 price). Venue fees: a resting fill earns the add credit (0.20c; none under \$1), a taken share
(the bid rose onto our price) pays 0.30c (30 bp under \$1).
Run from research/:  python -u scripts/equity/gapfader_exit_sim.py > logs/gapfader_exit_sim.log 2>&1; echo exit $?
"""
import argparse, datetime, math, os, re, time, zoneinfo
from multiprocessing import Pool
import numpy as np, pandas as pd, duckdb
from numba import njit

ap = argparse.ArgumentParser()
ap.add_argument("--cache", default="data/equity/gapfader/exit_sim")
ap.add_argument("--out", default="data/equity/gapfader/exit_sim/sim.parquet")
ap.add_argument("--notional", type=float, default=1000.0)
ap.add_argument("--lat", type=float, default=0.216)
ap.add_argument("--workers", type=int, default=8)
args = ap.parse_args()
NY = zoneinfo.ZoneInfo("America/New_York")
def ns_at(d, sec):
    y, m, dd = map(int, d.split("-")); return int(datetime.datetime(y, m, dd, tzinfo=NY).timestamp() * 1e9) + int(sec * 1e9)

# the entry sim's `run`, verbatim (one source of truth for the order logic)
_src = open("scripts/equity/flushfader_entry_sim.py").read()
_a = _src.index("@njit(cache=True)\ndef run("); _b = _src.index("\nC1S = ")
exec(_src[_a:_b].replace("@njit(cache=True)", "@njit", 1))   # an exec'd function has no file for numba's cache

T10, T1030 = 36000.0, 37800.0
INF = 1e9
# (name, trig, mode, step): mode 0 with no cap = today's maker; mode 3 = the NBBO step
RULES = [("maker (today)", -1.0, 0, 0.0), ("step 3s", 3.0, 3, 3.0), ("step 5s", 5.0, 3, 5.0), ("step 10s", 10.0, 3, 10.0),
         ("step 30s", 30.0, 3, 30.0)]
FM = ["lit", "queue", "hidden"]

def sim_day(job):
    d, g = job
    P = pd.read_parquet(os.path.join(args.cache, "prints", f"{d}.parquet"))
    P = P[P.fill_ok]
    idx = {}
    if len(P):
        keys, starts = np.unique(P.key.values, return_index=True); o = np.argsort(starts); keys, starts = keys[o], starts[o]
        idx = dict(zip(keys, zip(starts, np.r_[starts[1:], len(P)])))
    ts_all, px_all, sz_all, lit_all = P.ts.values, P.price.values, P["size"].values.astype(float), P.lit.values.astype(np.bool_)
    midnight = ns_at(d, 0); out = []
    for r in g.itertuples():
        key = f"{r.ticker}_{d}"
        rec = dict(ticker=r.ticker, date=d, year=d[:4], cross=r.open, x10=r.x_36000)
        qp = os.path.join(args.cache, "quotes", f"{key}.parquet")
        if not os.path.exists(qp): rec["err"] = "no quote file"; out.append(rec); continue
        Q = pd.read_parquet(qp)
        pp = os.path.join(args.cache, "pre", f"{key}.parquet")            # the standing quote at 10:00 (gapfader_exit_cache.py --phase pre)
        if os.path.exists(pp):
            Pq = pd.read_parquet(pp)
            if len(Pq): Q = pd.concat([Pq, Q], ignore_index=True)       # an empty file = no quote since 09:30
        Q = Q.dropna().astype({"ts": "int64", "bid": "float64", "bid_size": "float64", "ask": "float64", "ask_size": "float64"})
        Q = Q[(Q.bid > 0) & (Q.ask > 0) & (Q.ask >= Q.bid)]
        if len(Q) == 0: rec["err"] = "no quotes"; out.append(rec); continue
        if Q.bid_size.median() < 50: Q["bid_size"] *= 100; Q["ask_size"] *= 100
        s, e = idx.get(key, (0, 0))
        fs = (ts_all[s:e] - midnight) / 1e9; qs = (Q.ts.values - midnight) / 1e9
        if not (fs < T1030).any(): rec["err"] = "no prints"; out.append(rec); continue
        last1030 = px_all[s:e][fs < T1030][-1]
        # NEGATED: the sell becomes a buy. bid' = −ask (with the ask's size), ask' = −bid, print' = −price
        et = np.concatenate([np.zeros(len(qs), np.int8), np.ones(len(fs), np.int8)]); ets = np.concatenate([qs, fs]); o = np.lexsort((et, ets))
        et, ets = et[o], ets[o]; z = np.zeros(len(fs))
        eb = np.concatenate([-Q.ask.values, z])[o]; ebs = np.concatenate([Q.ask_size.values.astype(float), z])[o]
        ea = np.concatenate([-Q.bid.values, z])[o]; eas = np.concatenate([Q.bid_size.values.astype(float), z])[o]
        ep = np.concatenate([np.zeros(len(qs)), -px_all[s:e]])[o]; es = np.concatenate([np.zeros(len(qs)), sz_all[s:e]])[o]
        elit = np.concatenate([np.zeros(len(qs), np.bool_), lit_all[s:e]])[o]
        qi = np.flatnonzero((et == 0) & (ets <= T10))
        if len(qi) == 0: rec["err"] = "no quote at 10:00"; out.append(rec); continue
        j = qi[-1]; bid0, bsz0, ask0, asz0 = eb[j], ebs[j], ea[j], eas[j]
        i0 = int(np.searchsorted(ets, T10, side="right"))
        tk = 0.01 if r.open >= 1 else 0.0001
        n = float(max(1, math.floor(args.notional / r.open)))
        rec.update(n=n, spread_ticks=round((ea[j] - eb[j]) / tk), last1030=last1030)   # ask − bid = (−eb) − (−ea)
        for name, trig, mode, step in RULES:
            for k, fm in enumerate(FM):
                got, cost, tf, tkn = run(et, ets, eb, ebs, ea, eas, ep, es, elit, i0, T10 + args.lat, T1030, INF, INF, trig, mode, False, False,
                                         step, 10**6, True, tk, n, k, args.lat, bid0, bsz0, ask0, asz0)
                sold = got; px = -cost / got if got else math.nan          # back to positive prices
                key2 = f"{name}|{fm}"
                proceeds = (px * sold if sold else 0.0) + (n - sold) * last1030
                rec["q_" + key2] = sold; rec["px_" + key2] = px; rec["tk_" + key2] = tkn
                rec["t_" + key2] = (tf - T10) if tf >= 0 else math.nan
                rec["vs_" + key2] = proceeds - n * r.x_36000                # $ vs the model's 10:00 exit (+ = better)
                rec["fee_" + key2] = (0.0020 * (sold - tkn) - 0.0030 * tkn) if r.open >= 1 else (-0.003 * (px if sold else 0) * tkn)
        out.append(rec)
    return pd.DataFrame(out)

if __name__ == "__main__":
    T0 = time.time()
    def log(*a): print(f"[{time.time() - T0:7.1f}s]", *a, flush=True)
    B = duckdb.sql("""
        WITH V AS (SELECT ticker, date, tf_50, x_36000 FROM 'data/equity/gapfader/sizing_tape_cut.parquet'
                   UNION ALL BY NAME SELECT ticker, date, tf_50, x_36000 FROM 'data/equity/gapfader/sizing_tape_cut_ext.parquet')
        SELECT m.ticker, m.date, m.open, V.x_36000 FROM 'data/equity/gapfader/model_v2.parquet' m JOIN V USING (ticker, date)
        WHERE coalesce(V.tf_50, 1e9) >= 36000 ORDER BY m.date, m.ticker""").df()
    jobs = list(B.groupby("date"))
    log(f"{len(B):,} trades, {len(jobs):,} days, {len(RULES)} rules × {len(FM)} fill models")
    with Pool(args.workers) as p: parts = list(p.imap_unordered(sim_day, jobs, chunksize=4))
    R = pd.concat(parts, ignore_index=True).sort_values(["date", "ticker"]).reset_index(drop=True); R.to_parquet(args.out, index=False)
    ok = R[R["err"].isna()] if "err" in R else R
    log(f"simulated {len(ok):,} of {len(R):,}" + (f" ({R.err.value_counts().to_dict()})" if "err" in R else ""))
    notional = (ok.n * ok.x10).sum()
    print(f"\n{len(ok):,} 10:00 exits, ${notional:,.0f} at the model price; unsold shares at 10:30 valued at the last print\n")
    for fm in FM:
        base = ok[f"vs_maker (today)|{fm}"] + ok[f"fee_maker (today)|{fm}"]
        print(f"## fill model: {fm} — exit vs the model's 10:00 price, net of venue fees\n")
        print("| rule | sold by 10:30 (full / part / none) | taken % | median s to the last fill | p90 s | vs model bp | per-trade p1 / p5 bp | **Δ vs today $** | t (by day) | years Δ > 0 |")
        print("|---|---|---|---|---|---|---|---|---|---|")
        for name, *_ in RULES:
            q = ok[f"q_{name}|{fm}"]; net = ok[f"vs_{name}|{fm}"] + ok[f"fee_{name}|{fm}"]; dd = net - base
            bp = net / (ok.n * ok.x10) * 1e4
            bd = dd.groupby(ok.date).sum(); t = bd.mean() / (bd.std(ddof=1) / math.sqrt(len(bd))) if bd.std() > 0 else 0.0
            yy = dd.groupby(ok.year).sum(); tt = ok[f"t_{name}|{fm}"]
            print(f"| {name} | {int((q >= ok.n).sum())} / {int(((q > 0) & (q < ok.n)).sum())} / {int((q == 0).sum())} | "
                  f"{100 * ok[f'tk_{name}|{fm}'].sum() / max(q.sum(), 1):.1f} | {tt.median():.0f} | {tt.quantile(.9):.0f} | "
                  f"{net.sum() / notional * 1e4:+.1f} | {bp.quantile(.01):+.0f} / {bp.quantile(.05):+.0f} | **{dd.sum():+,.0f}** | {t:.2f} | "
                  f"{int((yy > 0).sum())} / {len(yy)} |")
        print()
    log("done")
