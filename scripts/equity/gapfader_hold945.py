"""GapFader GF-36 (user 2026-10-20): does a gap-up that held its open at BOTH 09:45 and 10:00 beat one that held at 10:00 alone?
Same sample as GF-33..35: gap-up >= 4 %, rvol_pm >= 0.2, headline universe, out at the official close, net 0.7c / share.
px_15 = the last 1s vwap before 09:45 (bucket < 35100), cached in data/equity/gapfader/px15.parquet; px_30 / entry_30 /
entry_60 from GF-10's tape_persistence.parquet. held = px >= the official open. calm = volat_clk < 40 bp (09:30–10:00);
activity fading = act 09:45–10:00 / act 09:30–09:45 < 0.82 (GF-30). Bought at 10:00 (entry_30), and at 10:30 (entry_60) as in GF-34.
Run from research/:  python -u scripts/equity/gapfader_hold945.py > data/equity/gapfader/hold945.md 2> data/equity/gapfader/hold945.log; echo exit $?
"""
import os, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm >= 0.2) & (C.gap >= 0.04)].copy()
X = pd.read_parquet("data/equity/gapfader/tape_persistence.parquet")[["ticker", "date", "act_a", "act_b", "px_30", "entry_30", "px_60", "entry_60"]]
V = pd.read_parquet("data/equity/gapfader/volat30.parquet")[["ticker", "date", "volat_clk"]]
C = C.merge(X, on=["ticker", "date"]).merge(V, on=["ticker", "date"], how="left")
OUT = "data/equity/gapfader/px15.parquet"
if os.path.exists(OUT):
    P = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []; days = sorted(C.date.unique())
    for i, d in enumerate(days):
        tl = ",".join("'" + t.replace("'", "''") + "'" for t in C[C.date == d].ticker)
        P = con.execute(f"""SELECT ticker, arg_max(vwap::DOUBLE, bucket) px_15 FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                            WHERE ticker IN ({tl}) AND bucket >= 34200 AND bucket < 35100 GROUP BY ticker""").df()
        P["date"] = d; parts.append(P)
        if i % 300 == 0: log(f"{i+1}/{len(days)} {d}")
    P = pd.concat(parts, ignore_index=True); P.to_parquet(OUT); log(f"wrote {OUT}: {len(P):,}")
C = C.merge(P, on=["ticker", "date"], how="left")
C = C[np.isfinite(C.entry_30) & C.volat_clk.notna() & C.px_15.notna()].copy()
C["yr"] = C.date.str[:4]; early = C.yr <= "2020"; NY = C.yr.nunique()
C["r"] = C.close / C.entry_30 - 1; C["rn"] = C.r - 0.007 / C.entry_30
C["r60"] = C.close / C.entry_60 - 1; C["rn60"] = C.r60 - 0.007 / C.entry_60
calm = C.volat_clk < 40; fading = (C.act_b / C.act_a.where(C.act_a > 0)) < 0.82
h15, h30 = C.px_15 >= C.open, C.px_30 >= C.open
def pf(x):
    x = np.asarray(x); x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
H = ("| cell | n | n / yr | median % | win % | PF | PF clip +50 % | **net PF** | net 2016–20 / 2021–26 | years < 1 (net) |\n"
     "|---|---|---|---|---|---|---|---|---|---|")
def row(nm, m, r="r", rn="rn"):
    m = m & np.isfinite(C[rn]); d = C[m]
    if len(d) < 25: return f"| {nm} | {len(d)} | | | | | | | | |"
    yrs = d.groupby("yr")[rn].apply(pf)
    return (f"| {nm} | {len(d):,} | {len(d)/NY:.0f} | {d[r].median()*100:+.2f} | {(d[r]>0).mean()*100:.0f} | {pf(d[r]):.2f} | "
            f"{pf(d[r].clip(upper=0.5)):.2f} | **{pf(d[rn]):.2f}** | {pf(d[rn][early[m]]):.2f} / {pf(d[rn][~early[m]]):.2f} | {int((yrs<1).sum())}/{len(yrs)} |")
print("# GF-36 — held the open at 09:45 AND 10:00 vs at 10:00 alone\n")
print(f"{len(C):,} gap-ups. Held at 09:45 {h15.mean()*100:.0f} %, at 10:00 {h30.mean()*100:.0f} %; of the 10:00 holders, "
      f"{h15[h30].mean()*100:.0f} % also held at 09:45 (calm: {h15[h30 & calm].mean()*100:.0f} %)\n")
for title, r, rn in [("bought at 10:00 → close", "r", "rn"), ("bought at 10:30 → close (GF-34's delay)", "r60", "rn60")]:
    print(f"## {title}\n"); print(H)
    for sub, sm in [("all", C.r.notna()), ("calm", calm), ("calm · fading", calm & fading)]:
        print(row(f"{sub} · held 10:00 (any 09:45)", sm & h30, r, rn))
        print(row(f"{sub} · **held 09:45 · held 10:00**", sm & h15 & h30, r, rn))
        print(row(f"{sub} · failed 09:45 · held 10:00 (reclaimed)", sm & ~h15 & h30, r, rn))
        print(row(f"{sub} · held 09:45 · failed 10:00", sm & h15 & ~h30, r, rn))
    print()
d = C[calm & h15 & h30]; e = C[calm & ~h15 & h30]
print("calm by year, net PF, bought at 10:00 — held both: " + " · ".join(f"{y} {pf(d.rn[d.yr == y]):.2f}" for y in sorted(C.yr.unique())))
print("\ncalm by year, net PF, bought at 10:00 — reclaimed:  " + " · ".join(f"{y} {pf(e.rn[e.yr == y]):.2f} [{(e.yr == y).sum()}]" for y in sorted(C.yr.unique())))
