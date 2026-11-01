"""GapFader GF-58 (user 2026-11-01): GF-57's dip entry with the 30-minute ROC and the 30-minute OLS SLOPE instead of ER30.

The user: "Instead of ER30, I wonder if the 30m OLS slope would be a better feature to use? Or even the 30m ROC?"
The book, the entry and the controls are GF-57's (gapfader_er_entry.py, its cache er_minutes.parquet): calm · held at
10:00 · rvol30 >= 1; the first minute from 09:59 to 15:29 whose signal is below the threshold, in at the next minute's
first 1s vwap, out at the close, net 0.7c; the SAME names bought at 10:00 and at a random minute from the cell's own
entry times. Over the same 31 points as ER30 (P = the 1-minute vwap, the open before 09:30):
  ROC30  P[m] / P[m−30] − 1
  OLS30  the least-squares slope of ln P over the 31 points × 30: the fitted change over the window (log %)
Thresholds in %: 0 · −1 · −2 · −3 · −5, and the one that triggers the SAME share of the book as ER30 < −0.6 (35 %), so
the three signals are compared at equal selectivity.

  cd research && python -u scripts/equity/gapfader_dip_signals.py > data/equity/gapfader/dip_signals.md 2> logs/gapfader_dip_signals.log; echo exit $?
"""
import hashlib, sys, time
import numpy as np, pandas as pd
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.gap >= 0.04)].copy()
C = C.merge(pd.read_parquet("data/equity/gapfader/swing_allpm.parquet"), on=["ticker", "date"])
C = C[(C.px_30 >= C.open) & np.isfinite(C.entry_30) & C.volat_clk.notna() & (C.act_a > 0) & (C.rvol_30m >= 1)
      & (C.volat_clk < 40)].copy()
C = C.sort_values(["date", "ticker"]).reset_index(drop=True)
M = pd.read_parquet("data/equity/gapfader/er_minutes.parquet")
NM, W = 390, 30
key = {(t, d): i for i, (t, d) in enumerate(zip(C.ticker, C.date))}
M = M[[(t, d) in key for t, d in zip(M.ticker, M.date)]]
row = np.array([key[(t, d)] for t, d in zip(M.ticker, M.date)])
P = np.full((len(C), NM), np.nan); E = np.full((len(C), NM), np.nan)
P[row, M.m.values] = M.pv.values; E[row, M.m.values] = M.first_px.values
P = pd.DataFrame(P).ffill(axis=1).values
P = np.where(np.isnan(P), C.open.values[:, None], P)
P = np.hstack([C.open.values[:, None], P])
E = pd.DataFrame(E).bfill(axis=1).values
assert np.nanmax(np.abs(E[:, 30] / C.entry_30.values - 1)) < 1e-9
CL = C.close.values; YR = C.date.str[:4].values; EARLY = YR <= "2020"
log(f"{len(C):,} calm names")

# the signals at every minute m in [29, 359], over P columns lo = m + 1 − W .. hi = m + 1 (31 points)
m = np.arange(29, 360); hi, lo = m + 1, m + 1 - W
dP = np.abs(np.diff(P, axis=1)); cs = np.hstack([np.zeros((len(C), 1)), np.cumsum(dP, axis=1)])
net, path = P[:, hi] - P[:, lo], cs[:, hi] - cs[:, lo]
with np.errstate(invalid="ignore", divide="ignore"):
    ER = np.where(path > 0, net / path, np.nan)
ROC = 100 * (P[:, hi] / P[:, lo] - 1)
y = np.log(P); k = np.arange(y.shape[1], dtype=float)
c1 = np.hstack([np.zeros((len(C), 1)), np.cumsum(y, axis=1)]); c2 = np.hstack([np.zeros((len(C), 1)), np.cumsum(k * y, axis=1)])
Sy = c1[:, hi + 1] - c1[:, lo]; Sky = c2[:, hi + 1] - c2[:, lo]
x = np.arange(W + 1, dtype=float); Sxx = ((x - x.mean()) ** 2).sum()
Sxy = (Sky - lo * Sy) - x.mean() * Sy                         # Σ (x − x̄) y with x = k − lo
OLS = 100 * W * Sxy / Sxx
# check one window by brute force
i0, j0 = 7, 40
assert abs(OLS[i0, j0] - 100 * W * np.polyfit(x, y[i0, lo[j0]:hi[j0] + 1], 1)[0]) < 1e-9

def first(S, thr):
    hit = S < thr; a = hit.any(axis=1)
    return np.where(a, m[np.argmax(hit, axis=1)], -1)
def pf(r):
    r = r[np.isfinite(r)]; g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
def ret(px, sel): return CL[sel] / px[sel] - 1 - 0.007 / px[sel]
def seed(t, d, cell): return int.from_bytes(hashlib.sha256(f"{t}|{d}|{cell}".encode()).digest()[:8], "little")

def cell(name, S, thr):
    mf = first(S, thr); ok = mf >= 0
    px = np.full(len(C), np.nan); px[ok] = E[ok, mf[ok] + 1]; ok &= np.isfinite(px)
    pool = mf[ok]; cr = np.full(len(C), np.nan)
    for i in np.flatnonzero(ok):
        cr[i] = E[i, int(pool[np.random.default_rng(seed(C.ticker[i], C.date[i], name)).integers(len(pool))]) + 1]
    okr = ok & np.isfinite(cr); r = ret(px, ok)
    yrs = pd.Series(r).groupby(YR[ok]).apply(pf)
    med = 30 + int(np.median(mf[ok]))
    return (f"| {name} | {ok.sum():,} ({100 * ok.mean():.0f}) | {9 + med // 60}:{med % 60:02d} | "
            f"{1e4 * np.median(px[ok] / C.entry_30.values[ok] - 1):+.0f} bp | {100 * r.mean():+.2f} | {100 * (r > 0).mean():.0f} | "
            f"**{pf(r):.2f}** | {pf(r[EARLY[ok]]):.2f} / {pf(r[~EARLY[ok]]):.2f} | {int((yrs < 1).sum())}/{len(yrs)} | "
            f"{pf(ret(C.entry_30.values, ok)):.2f} | {pf(ret(cr, okr)):.2f} |")

def matched(S, share):
    """the threshold at which the first-hit share of the book equals `share` (bisection on the running minimum)"""
    mn = np.nanmin(S, axis=1); return np.nanquantile(mn, share)

print("# GF-58 — the dip entry by ER30, ROC30 and the 30-minute OLS slope\n")
print(f"GF-57's calm book ({len(C):,} names, held at 10:00 · rvol30 ≥ 1 · volat_clk < 40 bp). Bought at 10:00: net PF "
      f"**{pf(ret(C.entry_30.values, np.ones(len(C), bool))):.2f}**. Out at the close, net 0.7c. Controls: the same names "
      f"bought at 10:00 and at a random minute from the cell's own entry times.\n")
print("| signal | n (% of book) | median entry | price vs 10:00 | mean % | win % | net PF | 2016–20 / 2021–26 | years < 1 | "
      "same names @10:00 | @ random time |\n|" + "---|" * 11)
share = (first(ER, -0.6) >= 0).mean()
for thr in (0.0, -0.3, -0.6):
    print(cell(f"ER30 < {thr:g}", ER, thr))
for nm, S in (("ROC30", ROC), ("OLS30", OLS)):
    for thr in (0.0, -1.0, -2.0, -3.0, -5.0):
        print(cell(f"{nm} < {thr:g} %", S, thr))
    t = matched(S, share)
    print(cell(f"{nm} < {t:.2f} % (the share of ER30 < −0.6)", S, t))
log("done")
