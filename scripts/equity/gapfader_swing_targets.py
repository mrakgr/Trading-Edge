"""GapFader GF-61 (user 2026-11-01): TARGETS on the gap-up swing (10:00 → close), after GF-60's stops all lost.
"Targets are next ... We could also make use of the slope, ROC and EF features and try using them as targets."

The book: GF-60's calm book's 10:00 unit (2,705 names; caches swing_1s_calm.parquet and er_minutes.parquet). Exits:
  fixed    a resting sell limit at entry × (1 + x), x = 1 · 2 · 3 · 5 · 8 · 10 %; filled AT the target when a later 1s vwap
           trades THROUGH it (strictly above; GapFader's convention)
  range    the 09:30–10:00 high 1s vwap (hi30), and entry + k × (hi30 − lo30), k = 0.5 · 1 · 2; filled as the fixed
  signal   sell at the NEXT minute's first 1s vwap once, at a minute's end from 10:00 on (its first full minute) — or from
           10:29, so the 30-minute window lies wholly after the entry (from 10:00 it still holds the morning run, and the
           exits fire at 10:01: a non-trade) — the stock is EXTENDED: OLS30 > x (GF-58's slope, fitted % over 30 min) · ROC30 > x (x = 2 · 3 · 5 %) · ER30 > 0.3 / 0.6
What is not exited goes at the official close; net 0.7c / share. For each: how many exit, when, and what the EXITED
trades would have made held to the close (the cost of capping the winners) vs what they realized.

  cd research && python -u scripts/equity/gapfader_swing_targets.py > data/equity/gapfader/swing_targets.md 2> logs/gapfader_swing_targets.log; echo exit $?
"""
import os, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.gap >= 0.04)].copy()
C = C.merge(pd.read_parquet("data/equity/gapfader/swing_allpm.parquet"), on=["ticker", "date"])
C = C[(C.px_30 >= C.open) & np.isfinite(C.entry_30) & C.volat_clk.notna() & (C.act_a > 0) & (C.rvol_30m >= 1)
      & (C.volat_clk < 40)].copy()
C = C.sort_values(["date", "ticker"]).reset_index(drop=True); N = len(C)

# the 09:30–10:00 high (not in swing_allpm)
HI = "data/equity/gapfader/swing_hi30_calm.parquet"
if not os.path.exists(HI):
    con = duckdb.connect(); con.execute("set memory_limit='1GB'; set threads=3; set enable_progress_bar=false"); parts = []
    for d, g in C.groupby("date"):
        con.register("tk", g[["ticker"]])
        X = con.execute(f"""SELECT ticker, max(vwap::DOUBLE) hi30 FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                            WHERE ticker IN (SELECT ticker FROM tk) AND bucket >= 34200 AND bucket < 36000 AND volume > 0
                            GROUP BY ticker""").df()
        con.unregister("tk"); X["date"] = d; parts.append(X)
    pd.concat(parts, ignore_index=True).to_parquet(HI)
C = C.merge(pd.read_parquet(HI), on=["ticker", "date"], how="left"); assert C.hi30.notna().all()
log(f"{N:,} calm names")

# the 1s path from 10:00 (GF-60's cache)
S = pd.read_parquet("data/equity/gapfader/swing_1s_calm.parquet").sort_values(["date", "ticker", "bucket"], kind="stable")
idx = {(t, d): i for i, (t, d) in enumerate(zip(C.ticker, C.date))}
row = np.array([idx[(t, d)] for t, d in zip(S.ticker, S.date)])
V = S.vwap.values.astype(np.float64); BK = S.bucket.values
start = np.searchsorted(row, np.arange(N), "left"); end = np.searchsorted(row, np.arange(N), "right")
E0 = C.entry_30.values; CL = C.close.values; LO = C.lo30.values; H30 = C.hi30.values
assert np.max(np.abs(V[start] / E0 - 1)) < 1e-6

# the minute signals (GF-58's, over the 31 points to minute m; P column 0 = the open)
M = pd.read_parquet("data/equity/gapfader/er_minutes.parquet")
M = M[[(t, d) in idx for t, d in zip(M.ticker, M.date)]]
mr = np.array([idx[(t, d)] for t, d in zip(M.ticker, M.date)])
P = np.full((N, 390), np.nan); EM = np.full((N, 390), np.nan)
P[mr, M.m.values] = M.pv.values; EM[mr, M.m.values] = M.first_px.values
P = pd.DataFrame(P).ffill(axis=1).values; P = np.where(np.isnan(P), C.open.values[:, None], P)
P = np.hstack([C.open.values[:, None], P]); EM = pd.DataFrame(EM).bfill(axis=1).values
W = 30; m = np.arange(30, 389); hi, lo = m + 1, m + 1 - W          # from the 10:00 minute's end; exit by 15:59
dP = np.abs(np.diff(P, axis=1)); cs = np.hstack([np.zeros((N, 1)), np.cumsum(dP, axis=1)])
with np.errstate(invalid="ignore", divide="ignore"):
    ER = np.where(cs[:, hi] - cs[:, lo] > 0, (P[:, hi] - P[:, lo]) / (cs[:, hi] - cs[:, lo]), np.nan)
ROC = 100 * (P[:, hi] / P[:, lo] - 1)
y = np.log(P); k = np.arange(y.shape[1], dtype=float)
c1 = np.hstack([np.zeros((N, 1)), np.cumsum(y, axis=1)]); c2 = np.hstack([np.zeros((N, 1)), np.cumsum(k * y, axis=1)])
Sy = c1[:, hi + 1] - c1[:, lo]; Sky = c2[:, hi + 1] - c2[:, lo]; x = np.arange(W + 1, dtype=float)
OLS = 100 * W * ((Sky - lo * Sy) - x.mean() * Sy) / ((x - x.mean()) ** 2).sum()

YR = C.date.str[:4].values; EARLY = YR <= "2020"
def pf(r):
    r = r[np.isfinite(r)]; g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
def net(px): return px / E0 - 1 - 0.007 / E0
base = net(CL)

def limit_exit(tgt):
    out = CL.copy(); hit = np.zeros(N, bool); tsec = np.full(N, np.nan)
    for i in range(N):
        a, b = start[i] + 1, end[i]
        if a >= b or not np.isfinite(tgt[i]): continue
        w = np.flatnonzero(V[a:b] > tgt[i])
        if len(w): out[i] = tgt[i]; hit[i] = True; tsec[i] = BK[a + w[0]]
    return out, hit, tsec
def signal_exit(Sg, thr, from_m=30):
    """from_m = the first minute evaluated: 30 = the 10:00 minute's end (the window still holds 09:30–10:00), 59 = 10:29
    (the 30-minute window lies wholly AFTER the entry)"""
    h = (Sg > thr) & (m >= from_m)[None, :]; a = h.any(axis=1); mf = np.where(a, m[np.argmax(h, axis=1)], -1)
    out = CL.copy(); hit = a.copy()
    out[hit] = EM[hit, mf[hit] + 1]; bad = hit & ~np.isfinite(out); out[bad] = CL[bad]; hit &= ~bad
    return out, hit, np.where(hit, 34200 + 60 * (mf + 1), np.nan)

H = ("| exit | exited % | median exit | mean % | win % | **net PF** | 2016–20 / 2021–26 | years < 1 | the exited: held to the close "
     "mean % | the exited: realized mean % | the rest (held) mean % |\n|" + "---|" * 11)
def line(nm, res):
    out, hit, ts = res; r = net(out); yrs = pd.Series(r).groupby(YR).apply(pf)
    med = np.nanmedian(ts) if hit.any() else np.nan
    t = f"{int(med // 3600)}:{int(med % 3600 // 60):02d}" if np.isfinite(med) else "—"
    return (f"| {nm} | {100 * hit.mean():.0f} | {t} | {100 * r.mean():+.3f} | {100 * (r > 0).mean():.0f} | **{pf(r):.3f}** | "
            f"{pf(r[EARLY]):.2f} / {pf(r[~EARLY]):.2f} | {int((yrs < 1).sum())}/{len(yrs)} | {100 * base[hit].mean():+.2f} | "
            f"{100 * r[hit].mean():+.2f} | {100 * base[~hit].mean():+.2f} |")

print("# GF-61 — targets on the gap-up swing (calm · held at 10:00 · rvol30 ≥ 1; 10:00 → close)\n")
print(f"{N:,} names, {C.date.min()}..{C.date.max()}; net 0.7c / share. Limits fill AT the target on a trade-through; "
      "signal exits at the next minute's first 1s vwap.\n")
print(H)
yb = pd.Series(base).groupby(YR).apply(pf)
print(f"| none: the close (GF-59) | 0 | 16:00 | {100 * base.mean():+.3f} | {100 * (base > 0).mean():.0f} | **{pf(base):.3f}** | "
      f"{pf(base[EARLY]):.2f} / {pf(base[~EARLY]):.2f} | {int((yb < 1).sum())}/{len(yb)} | — | — | {100 * base.mean():+.2f} |")
print("| **fixed** | | | | | | | | | | |")
for xx in (0.01, 0.02, 0.03, 0.05, 0.08, 0.10):
    print(line(f"entry + {100 * xx:g} %", limit_exit(E0 * (1 + xx))))
print("| **the morning range** | | | | | | | | | | |")
print(line("the 09:30–10:00 high", limit_exit(np.where(H30 > E0, H30, np.nan))))
for kk in (0.5, 1.0, 2.0):
    print(line(f"entry + {kk:g} × the 09:30–10:00 range", limit_exit(E0 + kk * (H30 - LO))))
print("| **signals (extended up)** | | | | | | | | | | |")
for f0, lab in ((30, "from 10:00 (the window holds the morning run)"), (59, "from 10:29 (the window wholly after the entry)")):
    print(f"| *{lab}* | | | | | | | | | | |")
    for nm, Sg, ths in (("OLS30", OLS, (2, 3, 5)), ("ROC30", ROC, (2, 3, 5)), ("ER30", ER, (0.3, 0.6))):
        for th in ths:
            print(line(f"{nm} > {th:g}{' %' if nm != 'ER30' else ''}", signal_exit(Sg, th, f0)))
rg = 1e2 * (H30 - LO) / E0; above = 1e4 * (H30 / E0 - 1)
print(f"\nThe 09:30–10:00 range, % of the entry: median {np.median(rg):.1f} (p10 {np.quantile(rg, .1):.1f} … p90 "
      f"{np.quantile(rg, .9):.1f}); the high above the entry: median {np.median(above):+.0f} bp ({(H30 <= E0).mean() * 100:.0f} % of "
      "the names entered AT or above their morning high: no range target for those)")
log("done")
