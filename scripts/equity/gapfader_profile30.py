"""GapFader GF-34 (user 2026-10-20): two follow-ups to GF-33 on the gap-up intraday swing (gap-up >= 4 %, rvol_pm >= 0.2,
headline universe; out at the official close; net 0.7c / share).

(A) THE 09:30–10:00 VOLUME PROFILE. Instead of one reference (the open), where does the 10:00 price sit in the first 30
    min's traded volume? prof_pct = (volume traded below px_30 + half the volume at px_30) / all volume, 09:30–10:00, each
    1s bar's volume placed at its vwap. 0 = below everything that traded (a breakdown), 1 = above it (a breakout). Beside
    it, the plain RANGE position: rng_pct = (px_30 − lo) / (hi − lo) over the 1s vwaps. Bought at entry_30 (the first 1s
    vwap at/after 10:00). Knowable at 10:00. Cached in data/equity/gapfader/profile30.parquet.
(B) HELD AT 10:00, FAILED AT 10:30. px_60 (the last 1s vwap before 10:30) < the official open, bought at entry_60 (the first
    1s vwap at/after 10:30), from GF-10's tape_persistence.parquet. Knowable at 10:30.
calm = volat_clk < 40 bp (GF-11, 09:30–10:00).
Run from research/:  python -u scripts/equity/gapfader_profile30.py > data/equity/gapfader/profile30.md 2> data/equity/gapfader/profile30.log; echo exit $?
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
OUT = "data/equity/gapfader/profile30.parquet"
if os.path.exists(OUT):
    P = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []; days = sorted(C.date.unique())
    for i, d in enumerate(days):
        S = C.loc[C.date == d, ["ticker", "px_30"]].dropna()
        con.register("s", S)
        P = con.execute(f"""
            WITH b AS (SELECT ticker, vwap::DOUBLE v, volume::DOUBLE vol
                       FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                       WHERE ticker IN (SELECT ticker FROM s) AND bucket >= 34200 AND bucket < 36000)
            SELECT ticker,
                   (sum(vol) FILTER (v < px_30) + 0.5 * coalesce(sum(vol) FILTER (v = px_30), 0)) / sum(vol) prof_pct,
                   min(v) lo30, max(v) hi30
            FROM b JOIN s USING (ticker) GROUP BY ticker""").df()
        con.unregister("s"); P["date"] = d; parts.append(P)
        if i % 300 == 0: log(f"{i+1}/{len(days)} {d}")
    P = pd.concat(parts, ignore_index=True); P["prof_pct"] = P.prof_pct.fillna(0.0); P.to_parquet(OUT); log(f"wrote {OUT}: {len(P):,}")
C = C.merge(P, on=["ticker", "date"], how="left")
C["yr"] = C.date.str[:4]; early = C.yr <= "2020"; NY = C.yr.nunique()
C["r"] = C.close / C.entry_30 - 1; C["rn"] = C.r - 0.007 / C.entry_30
C["r60"] = C.close / C.entry_60 - 1; C["rn60"] = C.r60 - 0.007 / C.entry_60
C["rng_pct"] = ((C.px_30 - C.lo30) / (C.hi30 - C.lo30)).where(C.hi30 > C.lo30, 0.5)
calm = C.volat_clk < 40; held = C.px_30 >= C.open; held60 = C.px_60 >= C.open
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
ok = C.prof_pct.notna() & np.isfinite(C.entry_30) & C.volat_clk.notna()
print("# GF-34 — the 09:30–10:00 volume profile at 10:00, and held at 10:00 → failed at 10:30\n")
print(f"{ok.sum():,} gap-ups (≥ 4 %, rvol_pm ≥ 0.2) with a profile and a 10:00 entry. ρ(prof_pct, rng_pct) = "
      f"{C.loc[ok, ['prof_pct', 'rng_pct']].corr(method='spearman').iloc[0, 1]:.2f}; ρ(prof_pct, 10:00 vs open) = "
      f"{pd.Series((C.px_30 / C.open)[ok]).corr(C.prof_pct[ok], method='spearman'):.2f}\n")
BANDS = [(0, 0.1), (0.1, 0.25), (0.25, 0.5), (0.5, 0.75), (0.75, 0.9), (0.9, 1.01)]
for title, f in [("(A1) the VOLUME-PROFILE percentile at 10:00", "prof_pct"), ("(A2) the RANGE position at 10:00 (control)", "rng_pct")]:
    print(f"## {title}, 10:00 → close\n")
    for sub, sm in [("all", ok), ("calm", ok & calm), ("not calm", ok & ~calm)]:
        print(f"**{sub}**\n\n" + H)
        for a, b in BANDS:
            print(row(f"{sub} · {f} {a:.2f}–{min(b, 1):.2f}", sm & (C[f] >= a) & (C[f] < b)))
        print()
print("## (A3) the profile × the open's hold, calm only, 10:00 → close\n"); print(H)
for hn, hm in [("held", held), ("failed", ~held)]:
    for a, b in [(0, 0.25), (0.25, 0.5), (0.5, 0.75), (0.75, 1.01)]:
        print(row(f"calm · {hn} · prof {a:.2f}–{min(b, 1):.2f}", ok & calm & hm & (C.prof_pct >= a) & (C.prof_pct < b)))
print()
ok60 = np.isfinite(C.entry_60) & np.isfinite(C.px_60) & C.volat_clk.notna()
print("## (B) HELD at 10:00, then at 10:30: bought at 10:30 → close\n"); print(H)
for sub, sm in [("all", ok60), ("calm (09:30–10:00)", ok60 & calm)]:
    print(row(f"{sub} · held 10:00 · held 10:30", sm & held & held60, "r60", "rn60"))
    print(row(f"{sub} · held 10:00 · **FAILED 10:30**", sm & held & ~held60, "r60", "rn60"))
    print(row(f"{sub} · failed 10:00 · held 10:30 (reclaimed)", sm & ~held & held60, "r60", "rn60"))
    print(row(f"{sub} · failed 10:00 · failed 10:30", sm & ~held & ~held60, "r60", "rn60"))
print()
print("**the same days bought at 10:00 (GF-11's trade), split by 10:30 — ⚠ descriptive, the split is not knowable at 10:00**\n"); print(H)
for sub, sm in [("calm", ok60 & calm)]:
    print(row(f"{sub} · held 10:00 · held 10:30", sm & held & held60)); print(row(f"{sub} · held 10:00 · failed 10:30", sm & held & ~held60))
d = C[ok60 & calm & held & ~held60]
print(f"\ncalm · held 10:00 · failed 10:30 by year (net PF, 10:30 → close): " +
      " · ".join(f"{y} {pf(d.rn60[d.yr == y]):.2f} [{(d.yr == y).sum()}]" for y in sorted(d.yr.unique())))
