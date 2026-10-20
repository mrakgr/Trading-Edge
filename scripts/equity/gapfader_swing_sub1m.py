"""GapFader GF-43 (user 2026-10-20): the gap-up long swing on THIN stocks below the headline universe's dv20 floor, i.e.
dv20_prior $0.25–1M (the census itself starts at $250k). Same trade and tape as GF-38..42: gap-up >= 4 %, open >= $1, no early
close, every rvol_pm; held the open at 10:00; bought at 10:00 (entry_30) or 10:30 (entry_60), out at the official close, net
0.7c / share. The tape is GF-38's query unchanged, cached in data/equity/gapfader/swing_sub1m.parquet. For comparison, the
$1–5M band from GF-38's cache.
Run from research/:  python -u scripts/equity/gapfader_swing_sub1m.py > data/equity/gapfader/swing_sub1m.md 2> data/equity/gapfader/swing_sub1m.log; echo exit $?
"""
import os, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
CEN = pd.read_parquet("data/equity/gapfader/census.parquet")
CEN = CEN[(CEN.open >= 1) & ~CEN.early_close & CEN.close.notna() & (CEN.gap >= 0.04)]
S = CEN[CEN.dv20_prior < 1e6].copy()
log(f"{len(S):,} gap-ups with dv20 < $1M")
OUT = "data/equity/gapfader/swing_sub1m.parquet"
if os.path.exists(OUT):
    X = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []; days = sorted(S.date.unique())
    for i, d in enumerate(days):
        tl = ",".join("'" + t.replace("'", "''") + "'" for t in S[S.date == d].ticker)
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
A = S.merge(X, on=["ticker", "date"])
B = CEN[(CEN.dv20_prior >= 1e6) & (CEN.dv20_prior < 5e6)].merge(pd.read_parquet("data/equity/gapfader/swing_allpm.parquet"), on=["ticker", "date"])
C = pd.concat([A, B], ignore_index=True)
C = C[(C.px_30 >= C.open) & np.isfinite(C.entry_30) & C.volat_clk.notna() & (C.act_a > 0) & C.rvol_30m.notna()].copy()
C["yr"] = C.date.str[:4]; NY = C.yr.nunique()
for k, e in [("10", "entry_30"), ("1030", "entry_60")]:
    C[f"r_{k}"] = C.close / C[e] - 1; C[f"rn_{k}"] = C[f"r_{k}"] - 0.007 / C[e]
calm = C.volat_clk < 40; fad = C.act_b / C.act_a < 0.82
def pf(x):
    x = np.asarray(x); x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
H = ("| cell | n | n / yr | median % | win % | PF | PF clip +50 % | **net PF 10:00** | net 2016–20 / 2021–26 | years < 1 | **net PF 10:30** | years < 1 |\n"
     "|---|---|---|---|---|---|---|---|---|---|---|---|")
def row(nm, m):
    d = C[m & np.isfinite(C.rn_10) & np.isfinite(C.rn_1030)]
    if len(d) < 25: return f"| {nm} | {len(d)} | | | | | | | | | | |"
    y10 = d.groupby("yr").rn_10.apply(pf); y30 = d.groupby("yr").rn_1030.apply(pf); e = d.yr <= "2020"
    return (f"| {nm} | {len(d):,} | {len(d)/NY:.0f} | {d.r_10.median()*100:+.2f} | {(d.r_10>0).mean()*100:.0f} | {pf(d.r_10):.2f} | "
            f"{pf(d.r_10.clip(upper=0.5)):.2f} | **{pf(d.rn_10):.2f}** | {pf(d.rn_10[e]):.2f} / {pf(d.rn_10[~e]):.2f} | {int((y10<1).sum())}/{len(y10)} | "
            f"**{pf(d.rn_1030):.2f}** | {int((y30<1).sum())}/{len(y30)} |")
DV = [("dv20 $0.25–0.5M", (C.dv20_prior < 5e5)), ("dv20 $0.5–1M", (C.dv20_prior >= 5e5) & (C.dv20_prior < 1e6)),
      ("dv20 $1–5M (GF-42's best band)", C.dv20_prior >= 1e6)]
print("# GF-43 — the gap-up long swing below the $1M dv20 floor\n")
for nm, dm in DV:
    print(f"## {nm}\n"); print(H)
    print(row("held · calm", dm & calm)); print(row("held · not calm", dm & ~calm))
    print(row("**held · calm · rvol30 ≥ 1**", dm & calm & (C.rvol_30m >= 1)))
    print(row("held · calm · rvol30 ≥ 2", dm & calm & (C.rvol_30m >= 2)))
    print(row("held · calm · rvol30 ≥ 1 · fading", dm & calm & (C.rvol_30m >= 1) & fad))
    print(row("held · calm · rvol30 ≥ 1 · rvol_pm ≥ 0.2", dm & calm & (C.rvol_30m >= 1) & (C.rvol_pm >= 0.2)))
    print(row("held · calm · rvol30 ≥ 1 · rvol_pm < 0.2", dm & calm & (C.rvol_30m >= 1) & (C.rvol_pm < 0.2)))
    print(row("held · NOT calm · rvol30 ≥ 1", dm & ~calm & (C.rvol_30m >= 1)))
    print()
sub = (C.dv20_prior < 1e6) & calm & (C.rvol_30m >= 1); d = C[sub]
print("sub-$1M · calm · rvol30 ≥ 1, net PF by year (10:00): " + " · ".join(f"{y} {pf(d.rn_10[d.yr == y]):.2f} [{(d.yr == y).sum()}]" for y in sorted(C.yr.unique())))
print(f"\nits median price ${d.entry_30.median():.2f}; median 09:30–10:00 dollar volume ${(d.vol_30m * d.entry_30).median()/1e3:,.0f}k; "
      f"median dv20 ${d.dv20_prior.median()/1e3:,.0f}k")
