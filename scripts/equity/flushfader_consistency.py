"""S49af — CONSISTENCY of the broad book (user, 2026-09-09): how many days / weeks / months are profitable?

Book = the broad book (S49 step 7, gap < 40, rule140, WAIT ht>=4 & ssh<300), returns net of the $0.001/sh/side credit.
Sizing = model A3 (S49ae), fitted on ALL years (in-sample multipliers — noted) and, as the honest twin, fitted on the
OTHER half (2020-23 multipliers applied to 2024-26 and vice versa). P&L is in POSITION UNITS: % per trade × multiplier,
i.e. 1 unit = 1% of one flat position's notional; a day's P&L is the sum over its trades.

Run from research/:  python -u scripts/equity/flushfader_consistency.py --credit 0.001 --rule140
"""
import argparse, os, time
import numpy as np, pandas as pd
from numba import njit

ap = argparse.ArgumentParser()
ap.add_argument("--slice", default="data/flushfader_review_slice.parquet")
ap.add_argument("--door", type=int, default=40)
ap.add_argument("--credit", type=float, default=0.001)
ap.add_argument("--rule140", action="store_true")
ap.add_argument("--out", default="data/flushfader_gate_review/consistency.md")
args = ap.parse_args()
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:6.1f}s]", *a, flush=True)

D = pd.read_parquet(args.slice, columns=["tkd", "trade_date", "yr", "signal_sec", "ent", "ext", "ret", "volat", "px", "gap60",
                                        "l180", "l300", "eff10a", "z20", "v10r", "crf", "consol_5m_lag1m", "rate600", "ht", "ssh"])
N = len(D)
def nz(m): return np.where(np.isnan(m.astype(float)), False, m).astype(bool)
M = (nz(D.volat.values >= 40) & nz(D.signal_sec.values <= 54000) & nz(D.gap60.values < args.door) & nz(D.px.values >= 1)
     & nz(D.l300.values >= 6) & nz(D.eff10a.values >= 0.15) & nz(D.v10r.values >= 0.75) & nz(D.z20.values < -1.5)
     & nz(D.l180.values >= 3) & nz(D.crf.values <= -0.002) & nz(D.consol_5m_lag1m.values <= 0.22))
if args.rule140: M &= ~(nz(D.volat.values >= 140) & nz(D.gap60.values >= 4))
M &= ~(nz(D.ht.values >= 4) & nz(D.ssh.values < 300))
@njit(cache=True)
def greedy_keep(tkd, ent, ext, mask, keep):
    last_tkd = -1; last_ext = -1
    for i in range(tkd.shape[0]):
        if tkd[i] != last_tkd:
            last_tkd = tkd[i]; last_ext = -1
        if mask[i] and ent[i] >= last_ext:
            keep[i] = True; last_ext = ext[i]
keep = np.zeros(N, bool); greedy_keep(D.tkd.values.astype(np.int64), D.ent.values.astype(np.int64), D.ext.values.astype(np.int64), M, keep)
B = D[keep].sort_values(["trade_date", "tkd", "ent"]).reset_index(drop=True)
R = B.ret.values * 100 + 2 * args.credit / B.px.values * 100
YR = B.yr.values; n = len(B); DT = pd.to_datetime(B.trade_date.values)
log(f"broad book {n:,} trades, {DT.nunique():,} trading days, {DT.min().date()}..{DT.max().date()}")

# ---- model A3 (S49ae)
GB = [0, 1, 4, 40]; VB = [40, 60, 90, 140, 250, 1e9]; VL = ["40-60", "60-90", "90-140", "140-250", "250+"]
gi = np.digitize(B.gap60.values, GB[1:-1]); vi = np.digitize(B.volat.values, VB[1:-1])
ri = (np.nan_to_num(B.rate600.values, nan=0.0) >= 0.07).astype(int)
ti = (nz(B.ht.values >= 1) & nz(B.ssh.values >= 300) & nz(B.ssh.values < 2400)).astype(int)
X = np.column_stack([np.ones(n), (gi == 1), (gi == 2)] + [(vi == k) for k in range(1, 5)] + [ri, ti]).astype(float)
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
def a3_mult(fit):
    win = R > 0
    bp = fit_logistic(X[fit], win[fit].astype(float)); bw = fit_loglink(X[fit & win], R[fit & win]); bl = fit_loglink(X[fit & ~win], -R[fit & ~win])
    p = 1 / (1 + np.exp(-(X @ bp))); W = np.exp(X @ bw); L = np.exp(X @ bl); pf1 = p / ((1 - p) * L / W) - 1
    return np.clip(pf1 / pf1[fit].mean(), 0.25, 4.0)
all_m = np.ones(n, bool); early = YR <= 2023; late = YR > 2023
w_in = a3_mult(all_m)
w_ho = np.where(late, a3_mult(early), a3_mult(late))       # each half sized by the OTHER half's fit
log(f"A3 multipliers: in-sample mean {w_in.mean():.3f}, cross-fit mean {w_ho.mean():.3f}")

# ---- aggregation
def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
def agg(pnl, key):
    s = pd.Series(pnl).groupby(key).sum(); c = pd.Series(pnl).groupby(key).size(); return s, c
def block(pnl, label):
    out = [f"### {label}\n"]
    keys = {"day": DT.normalize(), "week": DT.to_period("W-FRI").start_time, "month": DT.to_period("M").start_time, "quarter": DT.to_period("Q").start_time, "year": DT.to_period("Y").start_time}
    out += ["| period | n periods | profitable | share | mean P&L | median | p10 | worst | best | longest losing streak | trades/period |", "|---|---|---|---|---|---|---|---|---|---|---|"]
    for k, key in keys.items():
        s, c = agg(pnl, key); pos = (s > 0).sum()
        neg = (s <= 0).values; streak = 0; best = 0
        for v in neg:
            streak = streak + 1 if v else 0; best = max(best, streak)
        out.append(f"| {k} | {len(s):,} | {pos:,} | {pos/len(s)*100:.1f}% | {s.mean():+.1f} | {s.median():+.1f} | {s.quantile(.10):+.1f} | {s.min():+.1f} | {s.max():+.1f} | {best} | {c.mean():.1f} |")
    # by year: days / weeks / months profitable
    out += ["", "| year | trades | net | PF | days prof. | weeks prof. | months prof. | worst day | worst month | max DD (position units) |", "|---|---|---|---|---|---|---|---|---|---|"]
    for y in sorted(set(YR)):
        m = YR == y; d, _ = agg(pnl[m], DT[m].normalize()); wk, _ = agg(pnl[m], DT[m].to_period("W-FRI").start_time); mo, _ = agg(pnl[m], DT[m].to_period("M").start_time)
        cum = np.cumsum(pnl[m]); dd = float((np.maximum.accumulate(cum) - cum).max())
        out.append(f"| {y} | {m.sum():,} | {pnl[m].sum():+,.0f} | {pf(pnl[m]):.2f} | {(d>0).sum()}/{len(d)} ({(d>0).mean()*100:.0f}%) | {(wk>0).sum()}/{len(wk)} ({(wk>0).mean()*100:.0f}%) | {(mo>0).sum()}/{len(mo)} ({(mo>0).mean()*100:.0f}%) | {d.min():+.0f} | {mo.min():+.0f} | {dd:,.0f} |")
    # the monthly series
    mo, mc = agg(pnl, DT.to_period("M").start_time)
    out += ["", "monthly P&L (position units), losing months in **bold**:", "| year | " + " | ".join(f"{m:02d}" for m in range(1, 13)) + " |", "|---|" + "---|" * 12]
    for y in sorted(set(YR)):
        cells = []
        for m in range(1, 13):
            k = pd.Timestamp(year=y, month=m, day=1)
            if k in mo.index: v = mo[k]; cells.append(f"**{v:+.0f}**" if v <= 0 else f"{v:+.0f}")
            else: cells.append("")
        out.append(f"| {y} | " + " | ".join(cells) + " |")
    return "\n".join(out) + "\n"

out = [f"# S49af — consistency of the broad book: {n:,} trades, {DT.nunique():,} days, net of ${args.credit}/sh/side; P&L in position units (1 unit = 1% of one flat position's notional; 100 = one position)\n"]
out.append(block(R, "FLAT (every trade one unit)"))
out.append(block(R * w_in, "SIZED — model A3 fitted on all years (in-sample multipliers)"))
out.append(block(R * w_ho, "SIZED — model A3 CROSS-FIT (2024-26 sized by the 2020-23 fit and vice versa)"))
txt = "\n".join(out); os.makedirs(os.path.dirname(args.out), exist_ok=True); open(args.out, "w").write(txt); print(txt); log(f"wrote {args.out}")
