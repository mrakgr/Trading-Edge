"""S49ag — the CAPPED replay of the broad book (user ruling, 2026-09-09):
  * max ONE position per ticker-date (the first eligible trip of the day takes the ticker),
  * max 20 UNITS of size open at any moment across all tickers (sum of multipliers of open positions),
    first come first served by entry second; a trip that would breach the cap is skipped (not queued).
Sizes = model A3 (S49ae) multipliers (fit on the uncapped mc=1 book, all years; cross-fit twin as a check); a FLAT
variant (every position 1 unit → max 20 positions) for comparison. P&L in position units.

Run from research/:  python -u scripts/equity/flushfader_capped_replay.py --credit 0.001 --rule140 --cap 20
"""
import argparse, os, time
import numpy as np, pandas as pd
from numba import njit

ap = argparse.ArgumentParser()
ap.add_argument("--slice", default="data/flushfader_review_slice.parquet")
ap.add_argument("--door", type=int, default=40)
ap.add_argument("--credit", type=float, default=0.001)
ap.add_argument("--rule140", action="store_true")
ap.add_argument("--cap", type=float, default=20.0, help="max open size units at any moment")
ap.add_argument("--out", default="data/flushfader_gate_review/capped_replay.md")
ap.add_argument("--window", default="2020-02-14:2020-03-31", help="daily table for this date range under the ruled replay")
args = ap.parse_args()
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:6.1f}s]", *a, flush=True)

D = pd.read_parquet(args.slice, columns=["tkd", "trade_date", "yr", "signal_sec", "ent", "ext", "ret", "volat", "px", "gap60",
                                        "l180", "l300", "eff10a", "z20", "v10r", "crf", "consol_5m_lag1m", "rate600", "ht", "ssh"])
def nz(m): return np.where(np.isnan(m.astype(float)), False, m).astype(bool)
M = (nz(D.volat.values >= 40) & nz(D.signal_sec.values <= 54000) & nz(D.gap60.values < args.door) & nz(D.px.values >= 1)
     & nz(D.l300.values >= 6) & nz(D.eff10a.values >= 0.15) & nz(D.v10r.values >= 0.75) & nz(D.z20.values < -1.5)
     & nz(D.l180.values >= 3) & nz(D.crf.values <= -0.002) & nz(D.consol_5m_lag1m.values <= 0.22))
if args.rule140: M &= ~(nz(D.volat.values >= 140) & nz(D.gap60.values >= 4))
M &= ~(nz(D.ht.values >= 4) & nz(D.ssh.values < 300))
C = D[M].copy(); del D
C["date"] = pd.to_datetime(C.trade_date.values); C["day"] = (C.date - C.date.min()).dt.days.astype(np.int64)
C = C.sort_values(["day", "ent", "tkd"]).reset_index(drop=True)
R = C.ret.values * 100 + 2 * args.credit / C.px.values * 100
log(f"{len(C):,} spec-passing trips on {C.day.nunique():,} days")

# ---- model A3 multipliers for every trip (fit on the UNCAPPED mc=1 book, as in S49ae)
GB = [0, 1, 4, 40]; VB = [40, 60, 90, 140, 250, 1e9]
gi = np.digitize(C.gap60.values, GB[1:-1]); vi = np.digitize(C.volat.values, VB[1:-1])
ri = (np.nan_to_num(C.rate600.values, nan=0.0) >= 0.07).astype(int)
ti = (nz(C.ht.values >= 1) & nz(C.ssh.values >= 300) & nz(C.ssh.values < 2400)).astype(int)
X = np.column_stack([np.ones(len(C)), (gi == 1), (gi == 2)] + [(vi == k) for k in range(1, 5)] + [ri, ti]).astype(float)
YR = C.yr.values
@njit(cache=True)
def mc1_keep(tkd, ent, ext, keep):
    last_tkd = -1; last_ext = -1
    for i in range(tkd.shape[0]):
        if tkd[i] != last_tkd:
            last_tkd = tkd[i]; last_ext = -1
        if ent[i] >= last_ext:
            keep[i] = True; last_ext = ext[i]
o = np.lexsort((C.ent.values, C.tkd.values)); k = np.zeros(len(C), bool); mc1_keep(C.tkd.values[o].astype(np.int64), C.ent.values[o].astype(np.int64), C.ext.values[o].astype(np.int64), k)
BOOK = np.zeros(len(C), bool); BOOK[o] = k
def fit_logistic(X, y, it=50):
    b = np.zeros(X.shape[1])
    for _ in range(it):
        eta = X @ b; mu = 1 / (1 + np.exp(-eta)); W_ = mu * (1 - mu) + 1e-12; z = eta + (y - mu) / W_
        b_new = np.linalg.solve((X * W_[:, None]).T @ X, (X * W_[:, None]).T @ z)
        if np.max(np.abs(b_new - b)) < 1e-9: b = b_new; break
        b = b_new
    return b
def fit_loglink(X, y, it=50):
    b = np.zeros(X.shape[1]); b[0] = np.log(y.mean())
    for _ in range(it):
        eta = X @ b; mu = np.exp(eta); z = eta + (y - mu) / mu
        b_new = np.linalg.solve((X * mu[:, None]).T @ X, (X * mu[:, None]).T @ z)
        if np.max(np.abs(b_new - b)) < 1e-9: b = b_new; break
        b = b_new
    return b
def a3(fit):
    win = R > 0; f = BOOK & fit
    bp = fit_logistic(X[f], win[f].astype(float)); bw = fit_loglink(X[f & win], R[f & win]); bl = fit_loglink(X[f & ~win], -R[f & ~win])
    p = 1 / (1 + np.exp(-(X @ bp))); W = np.exp(X @ bw); L = np.exp(X @ bl); pf1 = p / ((1 - p) * L / W) - 1
    return np.clip(pf1 / pf1[f].mean(), 0.25, 4.0)
all_m = np.ones(len(C), bool); early = YR <= 2023; late = YR > 2023
W_IN = a3(all_m); W_X = np.where(late, a3(early), a3(late))
log(f"A3 multipliers on the uncapped book: mean {W_IN[BOOK].mean():.3f}")

# ---- the capped replay
@njit(cache=True)
def capped_replay(day, tkd, ent, ext, size, cap, one_per_tkd, take, reason, open_size_at_entry, pnl, stop, realised_at_entry):
    """Trips sorted by (day, ent). take[i]=1 if executed. reason: 0 taken, 1 ticker busy/used, 2 cap, 3 daily stop.
    pnl = per-trip sized P&L (known at EXIT); stop = daily loss limit in units (<= 0 disables): once the day's REALISED
    P&L (closed positions) <= -stop, no new entries for the rest of the day."""
    n = day.shape[0]; i = 0
    while i < n:
        d = day[i]; j = i
        while j < n and day[j] == d: j += 1
        # per-day state
        m = j - i
        op_ext = np.empty(m, np.int64); op_size = np.empty(m, np.float64); op_tkd = np.empty(m, np.int64); op_pnl = np.empty(m, np.float64); nop = 0
        used = np.empty(m, np.int64); nused = 0; realised = 0.0; stopped = False
        for t in range(i, j):
            now = ent[t]
            # close positions whose exit <= now (exit at the exit second frees the slot for entries at >= that second)
            w = 0
            for q in range(nop):
                if op_ext[q] > now:
                    op_ext[w] = op_ext[q]; op_size[w] = op_size[q]; op_tkd[w] = op_tkd[q]; op_pnl[w] = op_pnl[q]; w += 1
                else:
                    realised += op_pnl[q]
            nop = w
            realised_at_entry[t] = realised
            if stop > 0 and realised <= -stop: stopped = True
            cur = 0.0
            for q in range(nop): cur += op_size[q]
            open_size_at_entry[t] = cur
            if one_per_tkd:
                dup = False
                for q in range(nused):
                    if used[q] == tkd[t]: dup = True; break
                if dup: reason[t] = 1; continue
            else:   # mc=1 within the ticker-day: skip if this ticker still has an OPEN position
                busy = False
                for q in range(nop):
                    if op_tkd[q] == tkd[t]: busy = True; break
                if busy: reason[t] = 1; continue
            if cur + size[t] > cap + 1e-9: reason[t] = 2; continue
            if stopped: reason[t] = 3; continue
            take[t] = 1; reason[t] = 0
            op_ext[nop] = ext[t]; op_size[nop] = size[t]; op_tkd[nop] = tkd[t]; op_pnl[nop] = pnl[t]; nop += 1
            used[nused] = tkd[t]; nused += 1
        i = j

def run(size, cap, one_per_tkd, label, stop=0.0):
    n = len(C); take = np.zeros(n, np.int8); reason = np.full(n, -1, np.int8); osz = np.zeros(n); rae = np.zeros(n)
    capped_replay(C.day.values.astype(np.int64), C.tkd.values.astype(np.int64), C.ent.values.astype(np.int64), C.ext.values.astype(np.int64), size.astype(np.float64), float(cap), one_per_tkd, take, reason, osz, (R * size).astype(np.float64), float(stop), rae)
    t = take == 1; pnl = R[t] * size[t]; dt = C.date.values[t]
    return dict(label=label, n=int(t.sum()), tkd=len(np.unique(C.tkd.values[t])), pnl=pnl, dt=dt, size=size[t], reason=reason, osz=osz[t], t=t, rae=rae)

def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
def stats(res):
    pnl, dt = res["pnl"], pd.to_datetime(res["dt"]); s = pd.Series(pnl)
    d = s.groupby(dt.normalize()).sum(); wk = s.groupby(dt.to_period("W-FRI").start_time).sum(); mo = s.groupby(dt.to_period("M").start_time).sum()
    c = np.cumsum(pnl); dd = float((np.maximum.accumulate(c) - c).max())
    gross = pd.Series(res["osz"] + res["size"]).groupby(dt.normalize()).max()   # peak open size per day (after entry)
    rej = res["reason"]; ntrips = len(rej)
    return (f"| {res['label']} | {res['n']:,} | {res['tkd']:,} | {pf(pnl):.3f} | {pnl.sum():+,.0f} | {pnl.mean():+.2f} | {dd:,.0f} | {d.min():+.0f} | "
            f"{(d>0).mean()*100:.1f}% | {(wk>0).mean()*100:.1f}% | {(mo>0).sum()}/{len(mo)} | {gross.median():.1f} / {gross.quantile(.9):.1f} / {gross.max():.1f} | "
            f"{(rej==1).sum()/ntrips*100:.1f}% / {(rej==2).sum()/ntrips*100:.1f}% |")
HDR = ("| replay | trades | tkd | PF | net (units) | avg/trade | max DD | worst day | days prof. | weeks prof. | months prof. | peak open size /day: med / p90 / max | trips skipped: ticker used / cap |\n"
       "|---|---|---|---|---|---|---|---|---|---|---|---|---|")

out = [f"# S49ag — capped replay of the broad book: cap {args.cap:g} size units open at once, one position per ticker-date; {len(C):,} spec-passing trips; net of ${args.credit}/sh/side\n",
       "P&L in position units (1 unit = 1% of one flat position's notional; 100 = one position). 'trips skipped' are candidate trips (not book trades) refused by each rule.\n", HDR]
ref_flat = dict(label="REFERENCE: uncapped mc=1 (re-entries allowed), flat", n=int(BOOK.sum()), tkd=len(np.unique(C.tkd.values[BOOK])), pnl=R[BOOK], dt=C.date.values[BOOK], size=np.ones(BOOK.sum()), reason=np.zeros(len(C), np.int8), osz=np.zeros(BOOK.sum()), t=BOOK)
ref_a3 = dict(ref_flat, label="REFERENCE: uncapped mc=1, A3 sized (in-sample)", pnl=R[BOOK] * W_IN[BOOK], size=W_IN[BOOK])
out += [stats(ref_flat), stats(ref_a3)]
ones = np.ones(len(C))
out.append(stats(run(ones, 1e9, True, "one per ticker-date, NO cap, flat")))
out.append(stats(run(W_IN, 1e9, True, "one per ticker-date, NO cap, A3 sized")))
for cap in (10.0, 20.0, 40.0):
    out.append(stats(run(ones, cap, True, f"one per ticker-date, cap {cap:g} positions, flat")))
    out.append(stats(run(W_IN, cap, True, f"one per ticker-date, cap {cap:g} units, A3 sized (in-sample)")))
    out.append(stats(run(W_X, cap, True, f"one per ticker-date, cap {cap:g} units, A3 sized (CROSS-FIT)")))
# the middle form: cap on open size, but sequential RE-ENTRIES in a ticker allowed (mc=1 within the ticker-day, as the reference)
for cap in (10.0, 20.0):
    out.append(stats(run(ones, cap, False, f"re-entries allowed (mc=1), cap {cap:g} positions, flat")))
    out.append(stats(run(W_IN, cap, False, f"re-entries allowed (mc=1), cap {cap:g} units, A3 sized (in-sample)")))
    out.append(stats(run(W_X, cap, False, f"re-entries allowed (mc=1), cap {cap:g} units, A3 sized (CROSS-FIT)")))
# ---- DAILY LOSS STOP sweep on the ruled replay (re-entries allowed, cap 20), cross-fit sizes
out += ["", "## Daily loss STOP on the ruled replay (re-entries allowed, cap 20 units, A3 cross-fit): no new entries once the day's REALISED P&L ≤ −L; open positions run to their exits\n",
        "1 unit = one flat position's notional; at 20% of equity per unit, L = 25 units ≈ 5% of equity, 50 ≈ 10%, 100 ≈ 20%.\n",
        "| L (units) | trades | PF | net | Δ net vs no stop | max DD | worst day | days prof. | months prof. | days stopped | trips refused by stop | P&L those trips would have made (naive) | worst 5 days |", "|---|---|---|---|---|---|---|---|---|---|---|---|---|"]
base = run(W_X, args.cap, False, "no stop")
for L in (0.0, 25.0, 50.0, 75.0, 100.0, 150.0, 200.0):
    r = run(W_X, args.cap, False, f"stop {L:g}", stop=L); pnl = r["pnl"]; dt = pd.to_datetime(r["dt"]); s_ = pd.Series(pnl)
    d = s_.groupby(dt.normalize()).sum(); mo = s_.groupby(dt.to_period("M").start_time).sum(); c = np.cumsum(pnl); dd = float((np.maximum.accumulate(c) - c).max())
    refused = r["reason"] == 3; forgone = (R * W_X)[refused].sum()
    stopped_days = len(np.unique(C.date.values[refused]))
    worst5 = ", ".join(f"{v:+.0f}" for v in d.sort_values().head(5).values)
    out.append(f"| {'none' if L == 0 else f'{L:g}'} | {r['n']:,} | {pf(pnl):.3f} | {pnl.sum():+,.0f} | {pnl.sum() - base['pnl'].sum():+,.0f} | {dd:,.0f} | {d.min():+.0f} | {(d>0).mean()*100:.1f}% | {(mo>0).sum()}/{len(mo)} | {stopped_days} | {refused.sum():,} | {forgone:+,.0f} | {worst5} |")
for L in (75.0, 100.0):
    r = run(W_X, args.cap, False, f"stop {L:g}", stop=L); refused = r["reason"] == 3
    days = np.unique(C.date.values[refused]); dtb = pd.to_datetime(base["dt"]); dtr = pd.to_datetime(r["dt"])
    out += ["", f"stopped days at L = {L:g} (day P&L without stop → with stop; trades without → with; time the stop fired):", "| date | no stop | with stop | trades | stop fired at |", "|---|---|---|---|---|"]
    for d in days:
        pb = base["pnl"][np.asarray(dtb == d)].sum(); pr = r["pnl"][np.asarray(dtr == d)].sum()
        nb = int(np.asarray(dtb == d).sum()); nr = int(np.asarray(dtr == d).sum())
        first = C.ent.values[refused & (C.date.values == d)].min(); hh, mm = divmod(int(first) // 60, 60)
        out.append(f"| {pd.Timestamp(d).date()} | {pb:+.0f} | {pr:+.0f} | {nb} → {nr} | {hh:02d}:{mm:02d} |")
main = run(W_X, args.cap, False, "RULED: re-entries allowed (mc=1), cap 20 units, A3 CROSS-FIT")
flat_main = run(ones, args.cap, False, "flat, same rules")
a, b = args.window.split(":"); wm = (C.date.values >= np.datetime64(a)) & (C.date.values <= np.datetime64(b))
out += ["", f"## Daily view {a} .. {b} — ruled replay (re-entries allowed, cap {args.cap:g} units), sized by the 2024-26 fit (out of sample for 2020); flat alongside\n",
        "| date | trades | tickers | sized P&L | cum sized | flat P&L | cum flat | win% | worst trade | peak open units | trips refused by cap |", "|---|---|---|---|---|---|---|---|---|---|---|"]
tm = main["t"] & wm; tf = flat_main["t"] & wm
days = np.unique(C.date.values[wm]); cs = cf = 0.0
for d in days:
    ms = tm & (C.date.values == d); mf = tf & (C.date.values == d)
    ps = (R[ms] * W_X[ms]).sum(); pfl = R[mf].sum(); cs += ps; cf += pfl
    osz = main["osz"][(C.date.values[main["t"]] == d)] + W_X[main["t"]][(C.date.values[main["t"]] == d)]
    cap_ref = int(((main["reason"] == 2) & (C.date.values == d)).sum())
    out.append(f"| {pd.Timestamp(d).date()} | {ms.sum()} | {len(np.unique(C.tkd.values[ms]))} | {ps:+.0f} | {cs:+.0f} | {pfl:+.0f} | {cf:+.0f} | {(R[ms]>0).mean()*100 if ms.sum() else 0:.0f} | {R[ms].min() if ms.sum() else 0:+.0f} | {osz.max() if len(osz) else 0:.1f} | {cap_ref} |")
out.append(f"\nwindow total: sized {cs:+.0f} · flat {cf:+.0f}; trades {tm.sum():,}; days profitable sized {sum(1 for d in days if (R[tm & (C.date.values == d)] * W_X[tm & (C.date.values == d)]).sum() > 0)} of {len(days)}")
main = run(W_IN, args.cap, False, "main")
# per-year for the ruled replay
pnl, dt, yr_ = main["pnl"], pd.to_datetime(main["dt"]), pd.to_datetime(main["dt"]).year
out += ["", f"## The ruled replay (cap {args.cap:g} units, re-entries allowed, A3 in-sample) by year\n",
        "| year | trades | net | PF | avg | days prof. | weeks prof. | months prof. | worst day | max DD | peak open size med / max |", "|---|---|---|---|---|---|---|---|---|---|---|"]
for y in sorted(set(yr_)):
    m = yr_ == y; s = pd.Series(pnl[m]); d = s.groupby(dt[m].normalize()).sum(); wk = s.groupby(dt[m].to_period("W-FRI").start_time).sum(); mo = s.groupby(dt[m].to_period("M").start_time).sum()
    c = np.cumsum(pnl[m]); dd = float((np.maximum.accumulate(c) - c).max()); g = pd.Series(main["osz"][m] + main["size"][m]).groupby(dt[m].normalize()).max()
    out.append(f"| {y} | {m.sum():,} | {pnl[m].sum():+,.0f} | {pf(pnl[m]):.2f} | {pnl[m].mean():+.2f} | {(d>0).sum()}/{len(d)} ({(d>0).mean()*100:.0f}%) | {(wk>0).sum()}/{len(wk)} ({(wk>0).mean()*100:.0f}%) | {(mo>0).sum()}/{len(mo)} | {d.min():+.0f} | {dd:,.0f} | {g.median():.1f} / {g.max():.1f} |")
# 2020-03-18 under the cap
m = dt.normalize() == pd.Timestamp("2020-03-18")
out += ["", f"2020-03-18 under the ruled replay: {m.sum()} trades, P&L {pnl[m].sum():+.0f} units (uncapped flat: 331 trades, −501); trips skipped that day: ticker busy {int(((main['reason']==1) & (C.date.values == np.datetime64('2020-03-18'))).sum())}, cap {int(((main['reason']==2) & (C.date.values == np.datetime64('2020-03-18'))).sum())}."]
txt = "\n".join(out); os.makedirs(os.path.dirname(args.out), exist_ok=True); open(args.out, "w").write(txt); print(txt); log(f"wrote {args.out}")
