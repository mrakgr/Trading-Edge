"""GapFader GF-7 (user 2026-10-16): of the heavy-premarket gap-UPs (rvol_pm > 1, gap ≥ 4 %), how many FADE within
15–60 min of the open — and is there a LONG edge in the few that do not (positive momentum after the open, then buy)?

Per gap, at each checkpoint T = 15 / 30 / 45 / 60 min after 09:30 (from the raw 1s vwap bars):
  px_T   the last 1s vwap before 09:30 + T          lo_T / hi_T   the min / max 1s vwap in [09:30, 09:30 + T)
  entry  the FIRST 1s vwap at or after 09:30 + T (the buy — after the decision; nothing after T decides)
Outcomes: entry → the official close, entry → the next open (in D's terms). Headline universe (open ≥ $1, dv20_prior ≥ $1M,
no early close). Equal weight, no costs. Writes data/equity/gapfader/hold_after_open.parquet; the report to stdout.
Run from research/:  python -u scripts/equity/gapfader_hold_after_open.py > data/equity/gapfader/hold_after_open.md 2> data/equity/gapfader/hold_after_open.log; echo exit $?
"""
import os, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm > 1) & (C.gap >= 0.04)].copy()
TS = (15, 30, 45, 60)
OUT = "data/equity/gapfader/hold_after_open.parquet"
if os.path.exists(OUT):
    X = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []
    days = sorted(C.date.unique())
    for k, d in enumerate(days):
        tick = C[C.date == d].ticker.tolist()
        tl = ",".join("'" + t.replace("'", "''") + "'" for t in tick)
        cols = []
        for T in TS:
            e = 34200 + T * 60
            cols += [f"arg_max(v, bucket) FILTER (bucket >= 34200 AND bucket < {e}) AS px_{T}",
                     f"min(v) FILTER (bucket >= 34200 AND bucket < {e}) AS lo_{T}",
                     f"max(v) FILTER (bucket >= 34200 AND bucket < {e}) AS hi_{T}",
                     f"arg_min(v, bucket) FILTER (bucket >= {e} AND bucket < 57600) AS entry_{T}"]
        X = con.execute(f"""SELECT ticker, {', '.join(cols)}
            FROM (SELECT ticker, bucket, vwap::DOUBLE v FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                  WHERE ticker IN ({tl}) AND bucket >= 34200 AND bucket < 57600) GROUP BY ticker""").df()
        X["date"] = d; parts.append(X)
        if k % 200 == 0: log(f"{k+1}/{len(days)} {d}")
    X = pd.concat(parts, ignore_index=True); X.to_parquet(OUT); log(f"wrote {OUT}: {len(X):,}")
C = C.merge(X, on=["ticker", "date"], how="inner")
C["yr"] = C.date.str[:4]
log(f"{len(C):,} gaps with 1s paths")
def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
def row(lab, F, T, tot):
    if len(F) < 15: return f"| {lab} | {len(F)} | | | | | | | |"
    r = (F.close / F[f"entry_{T}"] - 1).values; rn = (F.open_next_total / F[f"entry_{T}"] - 1).values
    h1, h2 = (F.yr <= "2020").values, (F.yr >= "2021").values
    return (f"| {lab} | {len(F):,} | {len(F)/tot*100:.0f} | {np.nanmean(r)*100:+.2f} | {np.nanmedian(r)*100:+.2f} | {(r>0).mean()*100:.0f} | "
            f"{pf(r):.2f} | {pf(r[h1]):.2f} / {pf(r[h2]):.2f} | {pf(rn):.2f} |")
print("# GF-7 — heavy-premarket gap-UPs (rvol_pm > 1, gap ≥ 4 %): who fades in the first hour, and buying the ones that hold\n")
print(f"{len(C):,} gaps, {C.date.min()} … {C.date.max()}. Entry = the first 1s vwap AFTER the checkpoint; out at the official close. No costs.\n")
for glo, ghi, gl in ((0.04, 9, "all ≥ 4 %"), (0.04, 0.20, "4–20 %"), (0.20, 9, "≥ 20 %")):
    G = C[(C.gap >= glo) & (C.gap < ghi)]
    print(f"## gap {gl} — {len(G):,} gaps\n")
    for T in TS:
        G2 = G[G[f"entry_{T}"].notna()]; tot = len(G2)
        px, lo, op = G2[f"px_{T}"], G2[f"lo_{T}"], G2.open
        print(f"### at 09:30 + {T} min — {tot:,} still trading\n")
        print("| state at the checkpoint (bought there) | n | % of gaps | mean % → close | median % | win % | PF → close | PF 2016–20 / 2021–26 | PF → next open |")
        print("|---|---|---|---|---|---|---|---|---|")
        print(row("all (buy at T regardless)", G2, T, tot))
        print(row("FADED: price below the open", G2[px < op], T, tot))
        print(row("HELD: price ≥ the open", G2[px >= op], T, tot))
        print(row("…never below the open (lo ≥ open)", G2[lo >= op], T, tot))
        for m in (0.02, 0.05, 0.10):
            print(row(f"…price ≥ open + {m*100:.0f} %", G2[px >= op * (1 + m)], T, tot))
        print(row("…never below the open AND ≥ open + 5 %", G2[(lo >= op) & (px >= op * 1.05)], T, tot))
        print(row("…at the high so far (px ≥ 99 % of hi_T) and ≥ open", G2[(px >= 0.99 * G2[f'hi_{T}']) & (px >= op)], T, tot))
        print()
