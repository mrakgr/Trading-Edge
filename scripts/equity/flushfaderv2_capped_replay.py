"""FlushFaderV2 — V2-8: the CAPPED replay, V1 vs V2 (2026-10-12).

The ruled replay of §S49ag (user 2026-09-09): chronological, first come first served by entry second; one CONCURRENT
position per ticker-date (mc=1 re-entries allowed); at most CAP units of size open at once (sum of multipliers); a trip
that would breach the cap is skipped, not queued. The Oms trades exactly this shape (MaxUnits 20, unit = 7.5 % of
equity ⇒ 150 % gross).

  V1 = the broad book's trips (broad_reference_trips.parquet, raw px ≥ $1), sized by A3
  V2 = the same trips − dslo ∈ (0, 2 %] (V2-6), sized by A3 + dslo ≥ 4 % + s20 (V2-7)
Sizes are fitted on each system's UNCAPPED mc=1 book (in-sample: all years; cross-fit: each year block's sizes from a
fit on the other block) and predicted for every trip. P&L in position units (100 = one flat position's notional);
"% equity" columns convert at the Oms's 7.5 % of equity per unit.

Run from research/:
    python -u scripts/equity/flushfaderv2_capped_replay.py > data/flushfaderv2/capped_replay.log 2>&1; echo exit $?
"""
import argparse, os, sys
import numpy as np, pandas as pd, duckdb
from numba import njit
sys.argv, _argv = [sys.argv[0]], sys.argv           # the imported module parses its own (default) args
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from flushfaderv2_linear import fit_logistic, fit_loglink
sys.argv = _argv

ap = argparse.ArgumentParser()
ap.add_argument("--credit", type=float, default=0.001)
ap.add_argument("--cap", type=float, default=20.0)
ap.add_argument("--unit-equity", type=float, default=0.075, help="equity fraction per unit (the Oms's UnitFraction)")
ap.add_argument("--out", default="data/flushfaderv2/capped_replay.md")
args = ap.parse_args()

con = duckdb.connect(); con.execute("SET memory_limit='8GB'"); con.execute("SET threads=6")
C = con.execute("""
    SELECT t.symbol, t.trade_date, t.signal_sec, t.entry_sec AS ent, t.exit_sec AS ext, t.entry_px, t.ret_exit,
           t.volat, t.gap_60, t.rate600, t.ht, t.ssh, s.dslo, s.s20
    FROM 'data/flushfader_gate_review/broad_reference_trips.parquet' t
    LEFT JOIN 'data/flushfader_review_slice.parquet' s
      ON s.symbol = t.symbol AND s.trade_date::VARCHAR = t.trade_date AND s.signal_sec = t.signal_sec
    WHERE t.entry_px >= 1.0""").df()
assert C.dslo.notna().all() and C.s20.notna().all()
C["date"] = pd.to_datetime(C.trade_date); C["day"] = (C.date - C.date.min()).dt.days.astype(np.int64)
C["tkd"] = C.groupby(["symbol", "trade_date"]).ngroup().astype(np.int64)
C = C.sort_values(["day", "ent", "tkd"]).reset_index(drop=True)
R = (C.ret_exit.values + 2 * args.credit / C.entry_px.values) * 100
YR = C.date.dt.year.values; N = len(C)
early, late, all_m = YR <= 2023, YR > 2023, np.ones(N, bool)
GATE = (C.dslo.values > 0) & (C.dslo.values <= 0.02)
print(f"# V2-8 — capped replay, V1 vs V2 ({N:,} trips with raw px ≥ $1; V2 gate removes {GATE.sum():,} of them)\n")

@njit(cache=True)
def mc1_keep(tkd, ent, ext, ok, keep):
    last_tkd = -1; last_ext = -1
    for i in range(tkd.shape[0]):
        if tkd[i] != last_tkd: last_tkd = tkd[i]; last_ext = -1
        if ok[i] and ent[i] >= last_ext: keep[i] = True; last_ext = ext[i]
def uncapped_book(ok):
    o = np.lexsort((C.ent.values, C.tkd.values)); k = np.zeros(N, bool)
    mc1_keep(C.tkd.values[o], C.ent.values[o].astype(np.int64), C.ext.values[o].astype(np.int64), ok[o], k)
    b = np.zeros(N, bool); b[o] = k; return b

# ---- designs
gb = np.digitize(C.gap_60.values, [1, 4]); vb = np.digitize(C.volat.values, [60, 90, 140, 250])
fast = (np.nan_to_num(C.rate600.values, nan=0.0) >= 0.07).astype(float)
tier = ((C.ht.values >= 1) & (C.ssh.values >= 300) & (C.ssh.values < 2400)).astype(float)
XA3 = np.column_stack([np.ones(N), gb == 1, gb == 2] + [vb == k for k in range(1, 5)] + [fast, tier]).astype(float)
dslo4 = (C.dslo.values >= 0.04).astype(float)
def X_v2(fit):
    """A3 + dslo ≥ 4 % + s20 standardised with the FIT set's winsorising quantiles / mean / SD (as V2-7)"""
    x = C.s20.values.astype(float); lo, hi = np.quantile(x[fit], [0.01, 0.99]); z = np.clip(x, lo, hi)
    return np.column_stack([XA3, dslo4, (z - z[fit].mean()) / z[fit].std()])
def mult(X, fit):
    win = R > 0
    bp = fit_logistic(X[fit], win[fit].astype(float)); bw = fit_loglink(X[fit & win], R[fit & win]); bl = fit_loglink(X[fit & ~win], -R[fit & ~win])
    p = 1 / (1 + np.exp(-(X @ bp))); W = np.exp(X @ bw); L = np.exp(X @ bl); pf1 = p / ((1 - p) * L / W) - 1
    return np.clip(pf1 / pf1[fit].mean(), 0.25, 4.0)

B1 = uncapped_book(np.ones(N, bool)); B2 = uncapped_book(~GATE)
print(f"uncapped books: V1 {B1.sum():,} (reference 37,254), V2 {B2.sum():,} (V2-7: 35,612)\n")
SIZES = {
    "V1 in":  mult(XA3, B1 & all_m),
    "V1 x":   np.where(late, mult(XA3, B1 & early), mult(XA3, B1 & late)),
    "V2 in":  mult(X_v2(B2), B2),
    "V2 x":   np.where(late, mult(X_v2(B2 & early), B2 & early), mult(X_v2(B2 & late), B2 & late)),
}

# ---- the replay (flushfader_capped_replay.py's, re-entries form only, no stop)
@njit(cache=True)
def capped_replay(day, tkd, ent, ext, size, cap, ok, take, reason, open_size_at_entry):
    n = day.shape[0]; i = 0
    while i < n:
        d = day[i]; j = i
        while j < n and day[j] == d: j += 1
        m = j - i
        op_ext = np.empty(m, np.int64); op_size = np.empty(m, np.float64); op_tkd = np.empty(m, np.int64); nop = 0
        for t in range(i, j):
            if not ok[t]: reason[t] = 9; continue
            now = ent[t]; w = 0
            for q in range(nop):
                if op_ext[q] > now:
                    op_ext[w] = op_ext[q]; op_size[w] = op_size[q]; op_tkd[w] = op_tkd[q]; w += 1
            nop = w
            cur = 0.0
            for q in range(nop): cur += op_size[q]
            open_size_at_entry[t] = cur
            busy = False
            for q in range(nop):
                if op_tkd[q] == tkd[t]: busy = True; break
            if busy: reason[t] = 1; continue
            if cur + size[t] > cap + 1e-9: reason[t] = 2; continue
            take[t] = 1; reason[t] = 0
            op_ext[nop] = ext[t]; op_size[nop] = size[t]; op_tkd[nop] = tkd[t]; nop += 1
        i = j
def run(size, ok, cap):
    take = np.zeros(N, np.int8); reason = np.full(N, -1, np.int8); osz = np.zeros(N)
    capped_replay(C.day.values, C.tkd.values, C.ent.values.astype(np.int64), C.ext.values.astype(np.int64), size.astype(np.float64), float(cap), ok, take, reason, osz)
    t = take == 1
    return dict(t=t, pnl=R[t] * size[t], dt=C.date.values[t], size=size[t], osz=osz[t], reason=reason)

def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
def daily(res): return pd.Series(res["pnl"]).groupby(pd.to_datetime(res["dt"]).normalize()).sum()
def stats(res, label):
    pnl = res["pnl"]; dt = pd.to_datetime(res["dt"]); d = daily(res)
    wk = pd.Series(pnl).groupby(dt.to_period("W-FRI").start_time).sum(); mo = pd.Series(pnl).groupby(dt.to_period("M").start_time).sum()
    c = np.cumsum(pnl); dd = float((np.maximum.accumulate(c) - c).max())
    gross = pd.Series(res["osz"] + res["size"]).groupby(dt.normalize()).max()
    rej = res["reason"]; cand = (rej >= 0) & (rej != 9)
    ue = args.unit_equity
    return (f"| {label} | {res['t'].sum():,} | {pf(pnl):.3f} | {pnl.sum():+,.0f} | {pnl.mean():+.2f} | {dd:,.0f} ({dd*ue:.0f} %) | "
            f"{d.min():+.0f} ({d.min()*ue:+.1f} %) | {pnl.min():+.0f} ({pnl.min()*ue:+.1f} %) | {(d>0).mean()*100:.1f} % | {(wk>0).mean()*100:.1f} % | "
            f"{(mo>0).sum()}/{len(mo)} | {gross.median():.1f} / {gross.quantile(.9):.1f} / {gross.max():.1f} | {(rej[cand]==2).mean()*100:.1f} % |")
HDR = ("| replay | trades | PF | net (units) | avg/trade | max DD units (% eq) | worst day (% eq) | worst trade (% eq) | days prof. | weeks prof. | months prof. | peak open units/day med / p90 / max | trips refused by cap |\n"
       "|---|---|---|---|---|---|---|---|---|---|---|---|---|")
out = [f"P&L in position units (100 = one flat position); % equity at {args.unit_equity*100:g} % of equity per unit (the Oms). Cap {args.cap:g} units, mc=1 re-entries allowed.\n",
       "## 1. V1 vs V2\n", HDR]
ok1 = np.ones(N, bool); ok2 = ~GATE; ones = np.ones(N)
RES = {}
for lab, size, ok in [("V1 flat", ones, ok1), ("V1 A3 in-sample", SIZES["V1 in"], ok1), ("V1 A3 cross-fit", SIZES["V1 x"], ok1),
                      ("V2 flat (gated)", ones, ok2), ("V2 in-sample", SIZES["V2 in"], ok2), ("V2 cross-fit", SIZES["V2 x"], ok2)]:
    RES[lab] = run(size, ok, args.cap); out.append(stats(RES[lab], lab))
out.append("")
for lab, size, ok in [("V1 A3 cross-fit, cap 10", SIZES["V1 x"], ok1), ("V2 cross-fit, cap 10", SIZES["V2 x"], ok2)]:
    out.append(stats(run(size, ok, 10.0), lab))
out[-2:] = ["## 2. The same at cap 10 units\n", HDR] + out[-2:]

# ---- by year, cross-fit (every year's sizes out of sample)
out += ["", "## 3. By year, cross-fit sizes (net units / PF / worst day / max DD)\n",
        "| year | V1 net | V2 net | Δ | V1 PF | V2 PF | V1 worst day | V2 worst day | V1 max DD | V2 max DD |", "|---|---|---|---|---|---|---|---|---|---|"]
a, b = RES["V1 A3 cross-fit"], RES["V2 cross-fit"]
ya, yb = pd.to_datetime(a["dt"]).year, pd.to_datetime(b["dt"]).year
for y in sorted(set(ya)):
    pa, pb = a["pnl"][ya == y], b["pnl"][yb == y]
    da = pd.Series(pa).groupby(pd.to_datetime(a["dt"][ya == y]).normalize()).sum(); db = pd.Series(pb).groupby(pd.to_datetime(b["dt"][yb == y]).normalize()).sum()
    mdd = lambda p: float((np.maximum.accumulate(np.cumsum(p)) - np.cumsum(p)).max())
    out.append(f"| {y} | {pa.sum():+,.0f} | {pb.sum():+,.0f} | {pb.sum() - pa.sum():+,.0f} | {pf(pa):.2f} | {pf(pb):.2f} | {da.min():+.0f} | {db.min():+.0f} | {mdd(pa):,.0f} | {mdd(pb):,.0f} |")

# ---- the worst days and the worst trades
da, db = daily(a), daily(b)
out += ["", "## 4. The 10 worst days of each (cross-fit), with the other system's P&L that day\n",
        "| V1 worst | V1 | V2 same day | | V2 worst | V2 | V1 same day |", "|---|---|---|---|---|---|---|"]
wa, wb = da.sort_values().head(10), db.sort_values().head(10)
for (d1, v1), (d2, v2) in zip(wa.items(), wb.items()):
    out.append(f"| {d1.date()} | {v1:+.0f} | {db.get(d1, 0):+.0f} | | {d2.date()} | {v2:+.0f} | {da.get(d2, 0):+.0f} |")
tb = b["t"]; idx = np.where(tb)[0]; order = idx[np.argsort(R[idx] * SIZES["V2 x"][idx])][:10]
out += ["", "## 5. The 10 worst V2 trades (cross-fit sizes)\n", "| date | symbol | signal | ret % | dslo | s20 | volat | gap | V2 size | V2 P&L units | V1 size (A3 x) | V1 P&L units |", "|---|---|---|---|---|---|---|---|---|---|---|---|"]
for i in order:
    hh, mm = divmod(int(C.signal_sec.values[i]) // 60, 60)
    out.append(f"| {C.trade_date.values[i]} | {C.symbol.values[i]} | {hh:02d}:{mm:02d} | {R[i]:+.1f} | {C.dslo.values[i]*100:.1f} % | {C.s20.values[i]:+.0f} | {C.volat.values[i]:.0f} | {C.gap_60.values[i]} | "
               f"{SIZES['V2 x'][i]:.2f} | {R[i]*SIZES['V2 x'][i]:+.0f} | {SIZES['V1 x'][i]:.2f} | {R[i]*SIZES['V1 x'][i]:+.0f} |")
# where the extra tail comes from: P&L by multiplier band
out += ["", "## 6. V2 cross-fit, P&L by size band\n", "| size | trades | PF | net | worst trade | share of net |", "|---|---|---|---|---|---|"]
sz = b["size"]; tot = b["pnl"].sum()
for lo, hi in [(0.25, 0.2501), (0.2501, 0.5), (0.5, 1), (1, 1.5), (1.5, 2), (2, 3), (3, 4.01)]:
    m = (sz >= lo) & (sz < hi); p = b["pnl"][m]
    if m.sum(): out.append(f"| [{lo:g}, {hi:g}) | {m.sum():,} | {pf(p):.2f} | {p.sum():+,.0f} | {p.min():+.0f} | {p.sum()/tot*100:.0f} % |")

txt = "\n".join(out); open(args.out, "w").write(txt); print(txt)
