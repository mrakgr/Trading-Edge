"""GapFader GF-11 (user 2026-10-16): the gap-UP continuation (GF-10: held the open at 10:00, bought then) by the first-30-min
VOLATILITY, measured as the locked FlushFader / Snoozer measure (project_surgerider_vol_bakeoff_2026-07-21; longsnoozer
§ the fixed-window analogue): slot vwaps → r = ln(vwap_i / vwap_{i−1}) → mean |r|, in bp, over 09:30–10:00, two slottings:
  volat_clk   30 s CLOCK buckets (empty buckets skipped: r between consecutive traded buckets)
  volat_pres  30 PRESENT 1s bars per slot (the engines' activity-time slots)
Knowable at 10:00. Writes data/equity/gapfader/volat30.parquet; joins tape_persistence.parquet (GF-10).
Run from research/:  python -u scripts/equity/gapfader_volat30.py > data/equity/gapfader/volat30.md 2> data/equity/gapfader/volat30.log; echo exit $?
"""
import os, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm >= 0.2) & (C.gap >= 0.04)].copy()
OUT = "data/equity/gapfader/volat30.parquet"
if os.path.exists(OUT):
    V = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []
    days = sorted(C.date.unique())
    for i, d in enumerate(days):
        tl = ",".join("'" + t.replace("'", "''") + "'" for t in C[C.date == d].ticker)
        V = con.execute(f"""
            WITH b AS (SELECT ticker, bucket, vwap::DOUBLE v, volume::DOUBLE vol,
                              (row_number() OVER (PARTITION BY ticker ORDER BY bucket) - 1) // 30 AS pslot,
                              (bucket - 34200) // 30 AS cslot
                       FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                       WHERE ticker IN ({tl}) AND bucket >= 34200 AND bucket < 36000 AND volume > 0),
            c AS (SELECT ticker, cslot, sum(v * vol) / sum(vol) sv FROM b GROUP BY ticker, cslot),
            p AS (SELECT ticker, pslot, sum(v * vol) / sum(vol) sv, count(*) nb FROM b GROUP BY ticker, pslot),
            cr AS (SELECT ticker, abs(ln(sv / lag(sv) OVER (PARTITION BY ticker ORDER BY cslot))) r FROM c),
            pr AS (SELECT ticker, abs(ln(sv / lag(sv) OVER (PARTITION BY ticker ORDER BY pslot))) r FROM p WHERE nb = 30)
            SELECT coalesce(x.ticker, y.ticker) ticker, x.volat_clk, x.n_clk, y.volat_pres, y.n_pres FROM
              (SELECT ticker, avg(r) * 1e4 volat_clk, count(r) n_clk FROM cr GROUP BY ticker) x
              FULL JOIN (SELECT ticker, avg(r) * 1e4 volat_pres, count(r) n_pres FROM pr GROUP BY ticker) y USING (ticker)""").df()
        V["date"] = d; parts.append(V)
        if i % 300 == 0: log(f"{i+1}/{len(days)} {d}")
    V = pd.concat(parts, ignore_index=True); V.to_parquet(OUT); log(f"wrote {OUT}: {len(V):,}")
C = C.merge(pd.read_parquet("data/equity/gapfader/tape_persistence.parquet"), on=["ticker", "date"]).merge(V, on=["ticker", "date"], how="left")
C["yr"] = C.date.str[:4]; C["tc30"] = C.tc_a + C.tc_b
H = C[(C.px_30 >= C.open) & C.entry_30.notna()].copy(); H["r"] = H.close / H.entry_30 - 1
def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
def stats(F):
    r = F.r[np.isfinite(F.r)]
    if len(r) < 30: return f"{len(r)} | | | | | | |"
    yb = sum(pf(g.r.values) < 1 for _, g in F.groupby("yr"))
    return (f"{len(r):,} | {r.mean()*100:+.2f} | {r.median()*100:+.2f} | {(r>0).mean()*100:.0f} | {pf(r.values):.2f} | "
            f"{pf(r.clip(upper=0.5).values):.2f} | {yb}/{F.yr.nunique()}")
HDR = "| n | mean % | median % | win % | PF raw | PF clip +50 % | years < 1 |"
print("# GF-11 — gap-UP continuation (held the open at 10:00) by the FIRST-30-MIN VOLATILITY (30 s slot vwaps, mean |r|)\n")
print(f"{len(H):,} held of {len(C):,} gap-ups ≥ 4 %, rvol_pm ≥ 0.2. Bought at the first 1s vwap after 10:00, out at the official close. No costs.\n")
for name, P in (("all rvol_pm ≥ 0.2", H), ("rvol_pm 1–5", H[(H.rvol_pm >= 1) & (H.rvol_pm < 5)])):
    for feat, lab in (("volat_clk", "clock slots"), ("volat_pres", "present-bar slots")):
        F0 = P[P[feat].notna()]
        print(f"## {name} — volatility, {lab} (`{feat}`, bp per slot): quintiles\n")
        print("| quintile (bp) " + HDR); print("|---" * 8 + "|")
        q = pd.qcut(F0[feat].rank(method="first"), 5, labels=False)
        for b in range(5):
            F = F0[q == b]
            print(f"| Q{b+1} ({F[feat].min():.0f} – {F[feat].max():.0f}) | {stats(F)} |")
        print()
    # fixed bands, comparable to FlushFader's (bp / 30 s)
    print(f"## {name} — fixed bands (`volat_clk`)\n")
    print("| volat_clk (bp) " + HDR); print("|---" * 8 + "|")
    for lo, hi in ((0, 20), (20, 40), (40, 80), (80, 150), (150, 1e9)):
        F = P[(P.volat_clk >= lo) & (P.volat_clk < hi)]
        print(f"| {lo}–{hi:g} | {stats(F)} |")
    print()
# volatility × trade count, rvol_pm 1–5
P = H[(H.rvol_pm >= 1) & (H.rvol_pm < 5) & H.volat_clk.notna()]
qv = pd.qcut(P.volat_clk.rank(method="first"), 3, labels=["low volat", "mid volat", "high volat"])
qt = pd.qcut(P.tc30.rank(method="first"), 3, labels=["few trades", "mid trades", "many trades"])
print("## rvol_pm 1–5: volatility (clock) × trade count, terciles — PF raw / clip +50 % / median % (n)\n")
print("| | few trades | mid trades | many trades |"); print("|---|---|---|---|")
for v in ["low volat", "mid volat", "high volat"]:
    cells = []
    for t in ["few trades", "mid trades", "many trades"]:
        F = P[(qv == v) & (qt == t)]; r = F.r
        cells.append(f"{pf(r.values):.2f} / {pf(r.clip(upper=0.5).values):.2f} / {r.median()*100:+.2f} ({len(F)})")
    print(f"| {v} ({P[qv==v].volat_clk.min():.0f}–{P[qv==v].volat_clk.max():.0f} bp) | " + " | ".join(cells) + " |")
print()
print(f"corr(volat_clk, log tc30) on rvol 1–5 held: {np.corrcoef(P.volat_clk, np.log(P.tc30.clip(lower=1)))[0,1]:.2f}; "
      f"corr(volat_clk, volat_pres): {np.corrcoef(P.dropna(subset=['volat_pres']).volat_clk, P.dropna(subset=['volat_pres']).volat_pres)[0,1]:.2f}")
