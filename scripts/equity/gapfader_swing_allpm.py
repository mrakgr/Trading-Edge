"""GapFader GF-38 (user 2026-10-20): the calm gap-up swing (GF-30 / 34 / 37) at ALL premarket volumes. Is the edge there below
rvol_pm 0.2? Sample: gap-up >= 4 %, headline universe (open >= $1, dv20_prior >= $1M, no early close), every rvol_pm.
One pass over the 1s bars (09:30–16:00) computes everything the earlier studies took from their caches:
  volat_clk (GF-11) · act_a / act_b (09:30–09:45 / 09:45–10:00 active bars) · px_30, entry_30 (10:00) · px_60, entry_60 (10:30)
  lo30 (the 09:30–10:00 low vwap) · e_or / t_or (the next bar's vwap after the first bar at/after 10:00, before 15:30, below lo30)
Cached in data/equity/gapfader/swing_allpm.parquet. ⭐ CONTROL: the rvol_pm >= 0.2 rows must reproduce GF-30 / 34 / 37.
held = px_30 >= the official open; calm = volat_clk < 40 bp; fading = act_b / act_a < 0.82. Out at the close, net 0.7c / share.
Run from research/:  python -u scripts/equity/gapfader_swing_allpm.py > data/equity/gapfader/swing_allpm.md 2> data/equity/gapfader/swing_allpm.log; echo exit $?
"""
import os, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.gap >= 0.04)].copy()
log(f"{len(C):,} gap-ups >= 4 %")
OUT = "data/equity/gapfader/swing_allpm.parquet"
if os.path.exists(OUT):
    X = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []; days = sorted(C.date.unique())
    for i, d in enumerate(days):
        tl = ",".join("'" + t.replace("'", "''") + "'" for t in C[C.date == d].ticker)
        X = con.execute(f"""
            WITH b AS (SELECT ticker, bucket, vwap::DOUBLE v, volume::DOUBLE vol FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                       WHERE ticker IN ({tl}) AND bucket >= 34200 AND bucket < 57600),
            t AS (SELECT ticker,
                    count(*) FILTER (bucket < 35100) act_a, count(*) FILTER (bucket >= 35100 AND bucket < 36000) act_b,
                    arg_max(v, bucket) FILTER (bucket < 36000) px_30, arg_min(v, bucket) FILTER (bucket >= 36000) entry_30,
                    arg_max(v, bucket) FILTER (bucket < 37800) px_60, arg_min(v, bucket) FILTER (bucket >= 37800) entry_60,
                    min(v) FILTER (bucket < 36000) lo30
                  FROM b GROUP BY ticker),
            c AS (SELECT ticker, (bucket - 34200) // 30 cslot, sum(v * vol) / sum(vol) sv FROM b
                  WHERE bucket < 36000 AND vol > 0 GROUP BY ticker, cslot),
            cr AS (SELECT ticker, abs(ln(sv / lag(sv) OVER (PARTITION BY ticker ORDER BY cslot))) r FROM c),
            vv AS (SELECT ticker, avg(r) * 1e4 volat_clk FROM cr GROUP BY ticker),
            w AS (SELECT ticker, bucket, v, lead(v) OVER (PARTITION BY ticker ORDER BY bucket) v_next FROM b),
            o AS (SELECT w.ticker, min(bucket) FILTER (bucket >= 36000 AND bucket < 55800 AND v < t.lo30) t_or,
                         arg_min(v_next, bucket) FILTER (bucket >= 36000 AND bucket < 55800 AND v < t.lo30) e_or
                  FROM w JOIN t USING (ticker) GROUP BY w.ticker)
            SELECT t.*, vv.volat_clk, o.t_or, o.e_or FROM t LEFT JOIN vv USING (ticker) LEFT JOIN o USING (ticker)""").df()
        X["date"] = d; parts.append(X)
        if i % 300 == 0: log(f"{i+1}/{len(days)} {d}")
    X = pd.concat(parts, ignore_index=True); X.to_parquet(OUT); log(f"wrote {OUT}: {len(X):,}")
C = C.merge(X, on=["ticker", "date"])
C = C[(C.px_30 >= C.open) & np.isfinite(C.entry_30) & C.volat_clk.notna() & (C.act_a > 0)].copy()     # held at 10:00
C["yr"] = C.date.str[:4]; early = C.yr <= "2020"; NY = C.yr.nunique()
for k, e in [("10", "entry_30"), ("1030", "entry_60"), ("or", "e_or")]:
    C[f"r_{k}"] = C.close / C[e] - 1; C[f"rn_{k}"] = C[f"r_{k}"] - 0.007 / C[e]
calm = C.volat_clk < 40; fading = C.act_b / C.act_a < 0.82; trig = C.e_or.notna()
def pf(x):
    x = np.asarray(x); x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
H = ("| cell | n | n / yr | median % | win % | PF | PF clip +50 % | **net PF** | net 2016–20 / 2021–26 | years < 1 (net) |\n"
     "|---|---|---|---|---|---|---|---|---|---|")
def row(nm, m, k):
    r, rn = f"r_{k}", f"rn_{k}"; m = m & np.isfinite(C[rn]); d = C[m]
    if len(d) < 25: return f"| {nm} | {len(d)} | | | | | | | | |"
    yrs = d.groupby("yr")[rn].apply(pf)
    return (f"| {nm} | {len(d):,} | {len(d)/NY:.0f} | {d[r].median()*100:+.2f} | {(d[r]>0).mean()*100:.0f} | {pf(d[r]):.2f} | "
            f"{pf(d[r].clip(upper=0.5)):.2f} | **{pf(d[rn]):.2f}** | {pf(d[rn][early[m]]):.2f} / {pf(d[rn][~early[m]]):.2f} | {int((yrs<1).sum())}/{len(yrs)} |")
BANDS = [("rvol_pm < 0.02", C.rvol_pm < 0.02), ("rvol_pm 0.02–0.2", (C.rvol_pm >= 0.02) & (C.rvol_pm < 0.2)),
         ("rvol_pm 0.02–0.05", (C.rvol_pm >= 0.02) & (C.rvol_pm < 0.05)), ("rvol_pm 0.05–0.2", (C.rvol_pm >= 0.05) & (C.rvol_pm < 0.2)),
         ("rvol_pm ≥ 0.2 (CONTROL)", C.rvol_pm >= 0.2)]
print("# GF-38 — the calm gap-up swing at every premarket volume (held the open at 10:00)\n")
print("| band | held gap-ups | calm % | calm · fading % | median dv20 $M (calm) | median price (calm) |\n|---|---|---|---|---|---|")
for nm, bm in BANDS:
    print(f"| {nm} | {bm.sum():,} | {calm[bm].mean()*100:.0f} | {(calm & fading)[bm].mean()*100:.0f} | "
          f"{C.dv20_prior[bm & calm].median()/1e6:.1f} | {C.entry_30[bm & calm].median():.2f} |")
print()
for nm, bm in BANDS:
    print(f"## {nm}\n"); print(H)
    print(row("held · bought 10:00", bm, "10")); print(row("held · not calm · bought 10:00", bm & ~calm, "10"))
    print(row("**calm** · bought 10:00", bm & calm, "10")); print(row("**calm** · bought 10:30", bm & calm, "1030"))
    print(row("**calm** · bought at the OR-low break (GF-37 b)", bm & calm & trig, "or"))
    print(row("**calm · fading** · bought 10:00", bm & calm & fading, "10")); print(row("**calm · fading** · bought 10:30", bm & calm & fading, "1030"))
    print()
for nm, bm in BANDS[:2]:
    d = C[bm & calm]
    print(f"{nm} · calm · bought 10:30, net PF by year: " + " · ".join(f"{y} {pf(d.rn_1030[d.yr == y]):.2f} [{(d.yr == y).sum()}]" for y in sorted(C.yr.unique())) + "\n")
