"""GapFader GF-10 (user 2026-10-16): gap-UP CONTINUATION — gap-ups ≥ 4 % on MODERATE+ premarket volume (rvol_pm ≥ 0.2) that
HELD THE OPEN at T, bought at T; does the TAPE in the first 30–60 min (1s activity and its PERSISTENCE) separate the
winners? Features from the raw 1s bars (a row = a second with ≥ 1 trade), all knowable at T:
  T = 30 (10:00):  act30  = active seconds 09:30–10:00 (of 1,800)      pers30 = act 09:45–10:00 / act 09:30–09:45
                   tc30   = trades 09:30–10:00                         tpers30 = trades 09:45–10:00 / trades 09:30–09:45
  T = 60 (10:30):  act60  = active seconds 10:00–10:30 (of 1,800)      pers60 = act 10:00–10:30 / act 09:30–10:00
                   tc60   = trades 10:00–10:30                         tpers60 = trades 10:00–10:30 / trades 09:30–10:00
Held = the last 1s vwap before T ≥ the official open. Entry = the first 1s vwap at/after T; out at the official close (and
the next open). Headline universe (open ≥ $1, dv20_prior ≥ $1M, no early close). Equal weight, no costs.
Writes data/equity/gapfader/tape_persistence.parquet.
Run from research/:  python -u scripts/equity/gapfader_tape_persistence.py > data/equity/gapfader/tape_persistence.md 2> data/equity/gapfader/tape_persistence.log; echo exit $?
"""
import os, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm >= 0.2) & (C.gap >= 0.04)].copy()
log(f"{len(C):,} gap-ups ≥ 4 % with rvol_pm ≥ 0.2 on {C.date.nunique():,} days")
OUT = "data/equity/gapfader/tape_persistence.parquet"
W = {"a": (34200, 35100), "b": (35100, 36000), "c": (36000, 37800)}     # 09:30–09:45, 09:45–10:00, 10:00–10:30
if os.path.exists(OUT):
    X = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []
    days = sorted(C.date.unique())
    for i, d in enumerate(days):
        tl = ",".join("'" + t.replace("'", "''") + "'" for t in C[C.date == d].ticker)
        cols = []
        for k, (lo, hi) in W.items():
            cols += [f"count(*) FILTER (bucket >= {lo} AND bucket < {hi}) AS act_{k}",
                     f"sum(tc) FILTER (bucket >= {lo} AND bucket < {hi}) AS tc_{k}",
                     f"sum(vol) FILTER (bucket >= {lo} AND bucket < {hi}) AS vol_{k}"]
        for T in (30, 60):
            e = 34200 + T * 60
            cols += [f"arg_max(v, bucket) FILTER (bucket < {e}) AS px_{T}", f"arg_min(v, bucket) FILTER (bucket >= {e}) AS entry_{T}"]
        X = con.execute(f"""SELECT ticker, {', '.join(cols)}
            FROM (SELECT ticker, bucket, vwap::DOUBLE v, volume::DOUBLE vol, trade_count tc
                  FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                  WHERE ticker IN ({tl}) AND bucket >= 34200 AND bucket < 57600) GROUP BY ticker""").df()
        X["date"] = d; parts.append(X)
        if i % 300 == 0: log(f"{i+1}/{len(days)} {d}")
    X = pd.concat(parts, ignore_index=True); X.to_parquet(OUT); log(f"wrote {OUT}: {len(X):,}")
C = C.merge(X, on=["ticker", "date"], how="inner")
C["yr"] = C.date.str[:4]
C["act30"] = C.act_a + C.act_b; C["pers30"] = C.act_b / C.act_a.replace(0, np.nan)
C["tc30"] = C.tc_a + C.tc_b;   C["tpers30"] = C.tc_b / C.tc_a.replace(0, np.nan)
C["act60"] = C.act_c;          C["pers60"] = C.act_c / C.act30.replace(0, np.nan)
C["tc60"] = C.tc_c;            C["tpers60"] = C.tc_c / C.tc30.replace(0, np.nan)
def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
def stats(F, T):
    r = (F.close / F[f"entry_{T}"] - 1); ok = np.isfinite(r); r, yr = r[ok], F.yr[ok]
    if len(r) < 30: return f"{len(r)} | | | | | | | |"
    trim = r[r < r.quantile(0.95)]
    top = r.sort_values(ascending=False).head(max(1, len(r) // 20)).sum() / max(r[r > 0].sum(), 1e-12)
    rn = (F.open_next_total / F[f"entry_{T}"] - 1); rn = rn[np.isfinite(rn)]
    return (f"{len(r):,} | {r.mean()*100:+.2f} | {r.median()*100:+.2f} | {(r>0).mean()*100:.0f} | {pf(r.values):.2f} | "
            f"{pf(trim.values):.2f} | {top*100:.0f} % | {pf(r[(yr <= '2020').values].values):.2f} / {pf(r[(yr >= '2021').values].values):.2f} | {pf(rn.values):.2f}")
HDR = "| n | mean % | median % | win % | PF raw | PF top 5 % trimmed | top 5 % = gains | PF 2016–20 / 2021–26 | PF → next open |"
print("# GF-10 — gap-UP continuation: held the open at T, by the 1s TAPE (activity, persistence) — rvol_pm ≥ 0.2, gap ≥ 4 %\n")
print(f"{len(C):,} gaps, {C.date.min()} … {C.date.max()}. Bought at the first 1s vwap after T, out at the official close. No costs.\n")
for T in (30, 60):
    H = C[(C[f"px_{T}"] >= C.open) & C[f"entry_{T}"].notna()]
    print(f"## T = {T} min — held the open: {len(H):,} of {C[C[f'entry_{T}'].notna()].shape[0]:,} ({len(H)/len(C)*100:.0f} %)\n")
    print("| bucket " + HDR); print("|---" * 10 + "|")
    print(f"| all held | {stats(H, T)} |")
    print(f"| not held (control) | {stats(C[(C[f'px_{T}'] < C.open) & C[f'entry_{T}'].notna()], T)} |\n")
    for feat, lab in ((f"act{T}", "active seconds"), (f"pers{T}", "activity persistence"), (f"tc{T}", "trades"), (f"tpers{T}", "trade persistence")):
        print(f"### {lab} (`{feat}`) quintiles, held only\n")
        print("| quintile (range) " + HDR); print("|---" * 10 + "|")
        q = pd.qcut(H[feat].rank(method="first"), 5, labels=False)
        for b in range(5):
            F = H[q == b]
            print(f"| Q{b+1} ({F[feat].min():.3g} – {F[feat].max():.3g}) | {stats(F, T)} |")
        print()
    # the 2-D: activity × persistence terciles
    print(f"### active seconds × persistence (terciles), held only — PF raw / trimmed (n)\n")
    qa = pd.qcut(H[f"act{T}"].rank(method="first"), 3, labels=["low act", "mid act", "high act"])
    qp = pd.qcut(H[f"pers{T}"].rank(method="first"), 3, labels=["decaying", "steady", "rising"])
    print("| | decaying | steady | rising |"); print("|---|---|---|---|")
    for a in ["low act", "mid act", "high act"]:
        cells = []
        for p in ["decaying", "steady", "rising"]:
            F = H[(qa == a) & (qp == p)]; r = (F.close / F[f"entry_{T}"] - 1); r = r[np.isfinite(r)]
            trim = r[r < r.quantile(0.95)]
            cells.append(f"{pf(r.values):.2f} / {pf(trim.values):.2f} ({len(r)})")
        print(f"| {a} | " + " | ".join(cells) + " |")
    print()
