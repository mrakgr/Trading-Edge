"""GapFader GF-60 (user 2026-11-01): STOPS on the gap-up swing (10:00 → close). "The net and the PF is too low to really
consider trading it seriously. I wonder if we could improve it by using stops and targets? Let's start with stops."

The book: GF-57/58's calm book (gap-up >= 4 % · open >= $1 · dv20_prior >= $1M · no early close · held the open at
10:00 · rvol30 >= 1 · volat_clk < 40 bp): one unit bought at 10:00 (entry_30), out at the official close, net 0.7c / share.
A STOP triggers on the first 1s vwap AT OR BELOW the stop price after the entry second, and fills at the NEXT second's
vwap (a stop-market a second late; the close if none is left). Stops:
  fixed   entry × (1 − x), x = 1 · 2 · 3 · 5 · 8 %
  open    the official open × (1 − x), x = 0 · 1 · 2 %   (the "held the open" premise breaking)
  lo30    the 09:30–10:00 low 1s vwap (GF-38's lo30), and lo30 × 0.99
For each: how many stop out, their mean fill vs the stop (slippage), and what the STOPPED trades would have made held
to the close (a stop that cuts losers stops names that keep falling; one that whipsaws stops names that recover).
Cache: data/equity/gapfader/swing_1s_calm.parquet (the book's 1s bars 10:00–16:00).

  cd research && python -u scripts/equity/gapfader_swing_stops.py > data/equity/gapfader/swing_stops.md 2> logs/gapfader_swing_stops.log; echo exit $?
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
C = C.sort_values(["date", "ticker"]).reset_index(drop=True)
log(f"{len(C):,} calm names")

CACHE = "data/equity/gapfader/swing_1s_calm.parquet"
if os.path.exists(CACHE):
    S = pd.read_parquet(CACHE)
else:
    con = duckdb.connect()
    con.execute("set memory_limit='1GB'; set threads=3; set enable_progress_bar=false")
    parts = []
    for i, (d, g) in enumerate(C.groupby("date")):
        con.register("tk", g[["ticker"]])
        X = con.execute(f"""SELECT ticker, bucket::INTEGER bucket, vwap::FLOAT vwap
            FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
            WHERE ticker IN (SELECT ticker FROM tk) AND bucket >= 36000 AND bucket < 57600 AND volume > 0
            ORDER BY ticker, bucket""").df()
        con.unregister("tk"); X["date"] = d; parts.append(X)
        if i % 300 == 0: log(f"  {i + 1} {d}")
    S = pd.concat(parts, ignore_index=True)
    S.to_parquet(CACHE + ".tmp"); os.replace(CACHE + ".tmp", CACHE)
    log(f"wrote {CACHE}: {len(S):,} 1s bars")

# per name: its 1s vwaps from 10:00, as one flat array with offsets
S = S.sort_values(["date", "ticker", "bucket"], kind="stable")
idx = {(t, d): i for i, (t, d) in enumerate(zip(C.ticker, C.date))}
row = np.array([idx[(t, d)] for t, d in zip(S.ticker, S.date)])
V = S.vwap.values.astype(np.float64)
start = np.searchsorted(row, np.arange(len(C)), "left"); end = np.searchsorted(row, np.arange(len(C)), "right")
assert (row[:-1] <= row[1:]).all()
E0 = C.entry_30.values; CL = C.close.values; OP = C.open.values; LO = C.lo30.values
err = np.max(np.abs(V[start] / E0 - 1)); assert err < 1e-6, err          # the cache's first 1s vwap = entry_30 (float32)
YR = C.date.str[:4].values; EARLY = YR <= "2020"; N = len(C)

def run(stop_px):
    """per name: the exit price (the stop's fill or the close) and whether it stopped"""
    out = CL.copy(); hit = np.zeros(N, bool)
    for i in range(N):
        a, b = start[i] + 1, end[i]                           # from the second after the entry
        if a >= b or not np.isfinite(stop_px[i]): continue
        w = np.flatnonzero(V[a:b] <= stop_px[i])
        if len(w):
            j = a + w[0] + 1
            out[i] = V[j] if j < b else CL[i]; hit[i] = True
    return out, hit
def pf(r):
    r = r[np.isfinite(r)]; g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
def net(px_out): return px_out / E0 - 1 - 0.007 / E0
base = net(CL)

H = ("| stop | stopped % | fill vs stop (median) | mean % | win % | **net PF** | 2016–20 / 2021–26 | years < 1 | p1 % | "
     "the stopped: held to the close, mean % (PF) | the stopped: realized mean % |\n|" + "---|" * 11)
def line(nm, sp):
    xo, hit = run(sp); r = net(xo)
    yrs = pd.Series(r).groupby(YR).apply(pf)
    slip = np.median(xo[hit] / sp[hit] - 1) * 1e4 if hit.any() else np.nan
    return (f"| {nm} | {100 * hit.mean():.0f} | {slip:+.0f} bp | {100 * r.mean():+.3f} | {100 * (r > 0).mean():.0f} | **{pf(r):.3f}** | "
            f"{pf(r[EARLY]):.2f} / {pf(r[~EARLY]):.2f} | {int((yrs < 1).sum())}/{len(yrs)} | {100 * np.quantile(r, 0.01):+.1f} | "
            f"{100 * base[hit].mean():+.2f} ({pf(base[hit]):.2f}) | {100 * r[hit].mean():+.2f} |")

print("# GF-60 — stops on the gap-up swing (calm · held at 10:00 · rvol30 ≥ 1; 10:00 → close)\n")
print(f"{N:,} names, {C.date.min()}..{C.date.max()}. A stop triggers on the first 1s vwap at or below it after the entry "
      "and fills at the next second's vwap; net 0.7c / share.\n")
print(H)
yb = pd.Series(base).groupby(YR).apply(pf)
print(f"| none (GF-59's 10:00 alone) | 0 | — | {100 * base.mean():+.3f} | {100 * (base > 0).mean():.0f} | **{pf(base):.3f}** | "
      f"{pf(base[EARLY]):.2f} / {pf(base[~EARLY]):.2f} | {int((yb < 1).sum())}/{len(yb)} | {100 * np.quantile(base, 0.01):+.1f} | — | — |")
for x in (0.01, 0.02, 0.03, 0.05, 0.08):
    print(line(f"entry − {100 * x:g} %", E0 * (1 - x)))
for x in (0.0, 0.01, 0.02):
    print(line(f"the open − {100 * x:g} %", OP * (1 - x)))
print(line("the 09:30–10:00 low", LO))
print(line("the 09:30–10:00 low − 1 %", LO * 0.99))
d = pd.DataFrame({"stop_vs_entry_open": 1e4 * (OP / E0 - 1), "stop_vs_entry_lo30": 1e4 * (LO / E0 - 1)})
print(f"\nThe structural stops' distance below the entry (bp), median (p10–p90): the open "
      f"{d.stop_vs_entry_open.median():+.0f} ({d.stop_vs_entry_open.quantile(.1):+.0f} … {d.stop_vs_entry_open.quantile(.9):+.0f}); "
      f"the 09:30–10:00 low {d.stop_vs_entry_lo30.median():+.0f} ({d.stop_vs_entry_lo30.quantile(.1):+.0f} … {d.stop_vs_entry_lo30.quantile(.9):+.0f})")
log("done")
