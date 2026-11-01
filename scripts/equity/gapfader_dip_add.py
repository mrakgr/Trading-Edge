"""GapFader GF-59 (user 2026-11-01): the gap-up swing at the POSITION level — one unit at 10:00 on every name, and a SECOND
unit when the 30-minute OLS slope (GF-58) first drops below a threshold. Against 10:00 alone.

GF-58's book, signals and entry (gapfader_dip_signals.py; the cache er_minutes.parquet): calm · held at 10:00 · rvol30
>= 1; the add at the next minute's first 1s vwap after the first minute (09:59..15:29) with OLS30 < thr; both units out
at the official close; equal notional per unit, net 0.7c / share on each. A position's return is the SUM of its units'
returns (in units of one unit's notional). Controls: (a) 10:00 alone; (b) two units at 10:00 on every name (plain
doubling: the same PF, twice the capital); (c) the same names' add at a RANDOM minute from the adds' own times.

  cd research && python -u scripts/equity/gapfader_dip_add.py > data/equity/gapfader/dip_add.md 2> logs/gapfader_dip_add.log; echo exit $?
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
CL = C.close.values; YR = C.date.str[:4].values; EARLY = YR <= "2020"; N = len(C)
m = np.arange(29, 360); hi, lo = m + 1, m + 1 - W
y = np.log(P); k = np.arange(y.shape[1], dtype=float)
c1 = np.hstack([np.zeros((N, 1)), np.cumsum(y, axis=1)]); c2 = np.hstack([np.zeros((N, 1)), np.cumsum(k * y, axis=1)])
Sy = c1[:, hi + 1] - c1[:, lo]; Sky = c2[:, hi + 1] - c2[:, lo]
x = np.arange(W + 1, dtype=float)
OLS = 100 * W * ((Sky - lo * Sy) - x.mean() * Sy) / ((x - x.mean()) ** 2).sum()

def pf(r):
    r = r[np.isfinite(r)]; g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
def u(px): return CL / px - 1 - 0.007 / px                   # one unit's net return (px full-length)
def seed(t, d, c): return int.from_bytes(hashlib.sha256(f"{t}|{d}|{c}".encode()).digest()[:8], "little")
r10 = u(C.entry_30.values)

H = ("| book | positions with an add | units | total (unit-%) | per unit % | position PF | 2016–20 / 2021–26 | "
     "years < 1 | worst position (units) | p1 position | days with a loss > 10 units-% |\n|" + "---|" * 11)
DAY = C.date.values
def line(nm, pos, units, adds):
    yrs = pd.Series(pos).groupby(YR).apply(pf)
    day = pd.Series(pos).groupby(DAY).sum()
    return (f"| {nm} | {adds:,} | {units:,.0f} | {100 * pos.sum():+,.0f} | {100 * pos.sum() / units:+.3f} | **{pf(pos):.3f}** | "
            f"{pf(pos[EARLY]):.2f} / {pf(pos[~EARLY]):.2f} | {int((yrs < 1).sum())}/{len(yrs)} | {100 * pos.min():+.1f} | "
            f"{100 * np.quantile(pos, 0.01):+.1f} | {int((day < -0.10).sum())} |")

print("# GF-59 — the gap-up swing at the position level: 10:00 plus an add on the OLS30 dip\n")
print(f"GF-58's calm book: {N:,} names (held at 10:00 · rvol30 ≥ 1 · volat_clk < 40 bp), {C.date.min()}..{C.date.max()}. "
      "One unit at 10:00; the add = a second unit at the first OLS30 < threshold; both out at the close, net 0.7c / share "
      "each. Totals in % of ONE unit's notional.\n")
print(H)
print(line("10:00 alone", r10, N, 0))
print(line("2 units at 10:00 (plain doubling)", 2 * r10, 2 * N, N))
for thr in (-2.0, -3.0, -4.0, -5.0):
    hit = OLS < thr; a = hit.any(axis=1); mf = np.where(a, m[np.argmax(hit, axis=1)], -1)
    ok = mf >= 0; px = np.full(N, np.nan); px[ok] = E[ok, mf[ok] + 1]; ok &= np.isfinite(px)
    ra = np.zeros(N); ra[ok] = CL[ok] / px[ok] - 1 - 0.007 / px[ok]
    pool = mf[ok]; rr = np.zeros(N)
    for i in np.flatnonzero(ok):
        j = int(pool[np.random.default_rng(seed(C.ticker[i], C.date[i], f"add{thr}")).integers(len(pool))]) + 1
        if np.isfinite(E[i, j]): rr[i] = CL[i] / E[i, j] - 1 - 0.007 / E[i, j]
    print(line(f"10:00 + add at OLS30 < {thr:g} %", r10 + ra, N + ok.sum(), ok.sum()))
    print(line(f"   control: the add at a random time", r10 + rr, N + ok.sum(), ok.sum()))
log("done")
