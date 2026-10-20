"""GapFader GF-35 (user 2026-10-20): GF-33/34's held / failed tests with the session VWAP as the reference instead of the open,
and a VOLUME-WEIGHTED Z-SCORE of the price. Same sample: gap-up >= 4 %, rvol_pm >= 0.2, headline universe, out at the official
close, net 0.7c / share.

Per window W in {09:30–10:00, 09:30–10:30}, each 1s bar's volume placed at its vwap:
  vwap_W = Σ v·vol / Σ vol      sd_W = sqrt(Σ vol·(v − vwap_W)² / Σ vol)  (the volume is the count)
  z_W    = (px − vwap_W) / sd_W, px = the last 1s vwap before the window's end (px_30 at 10:00, px_60 at 10:30)
held_v = px >= vwap. Entries: entry_30 (the first 1s vwap at/after 10:00) or entry_60 (at/after 10:30). calm = volat_clk < 40 bp
(09:30–10:00). Cached in data/equity/gapfader/vwap_z.parquet.
Run from research/:  python -u scripts/equity/gapfader_vwap_z.py > data/equity/gapfader/vwap_z.md 2> data/equity/gapfader/vwap_z.log; echo exit $?
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
OUT = "data/equity/gapfader/vwap_z.parquet"
if os.path.exists(OUT):
    Z = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []; days = sorted(C.date.unique())
    for i, d in enumerate(days):
        tl = ",".join("'" + t.replace("'", "''") + "'" for t in C[C.date == d].ticker)
        Z = con.execute(f"""
            SELECT ticker,
                   sum(vol) FILTER (bucket < 36000) w30, sum(v * vol) FILTER (bucket < 36000) s30, sum(v * v * vol) FILTER (bucket < 36000) q30,
                   sum(vol) w60, sum(v * vol) s60, sum(v * v * vol) q60
            FROM (SELECT ticker, bucket, vwap::DOUBLE v, volume::DOUBLE vol FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                  WHERE ticker IN ({tl}) AND bucket >= 34200 AND bucket < 37800)
            GROUP BY ticker""").df()
        Z["date"] = d; parts.append(Z)
        if i % 300 == 0: log(f"{i+1}/{len(days)} {d}")
    Z = pd.concat(parts, ignore_index=True)
    for w in ("30", "60"):
        Z[f"vwap{w}"] = Z[f"s{w}"] / Z[f"w{w}"]
        Z[f"sd{w}"] = np.sqrt(np.maximum(Z[f"q{w}"] / Z[f"w{w}"] - Z[f"vwap{w}"] ** 2, 0.0))   # E[v²] − E[v]², volume-weighted
    Z = Z[["ticker", "date", "vwap30", "sd30", "vwap60", "sd60"]]; Z.to_parquet(OUT); log(f"wrote {OUT}: {len(Z):,}")
C = C.merge(Z, on=["ticker", "date"], how="left")
C["z30"] = (C.px_30 - C.vwap30) / C.sd30.where(C.sd30 > 0); C["z60"] = (C.px_60 - C.vwap60) / C.sd60.where(C.sd60 > 0)
C["yr"] = C.date.str[:4]; early = C.yr <= "2020"; NY = C.yr.nunique()
C["r"] = C.close / C.entry_30 - 1; C["rn"] = C.r - 0.007 / C.entry_30
C["r60"] = C.close / C.entry_60 - 1; C["rn60"] = C.r60 - 0.007 / C.entry_60
calm = C.volat_clk < 40
held_o, held_v = C.px_30 >= C.open, C.px_30 >= C.vwap30
held_o60, held_v60 = C.px_60 >= C.open, C.px_60 >= C.vwap60
fading = (C.act_b / C.act_a.where(C.act_a > 0)) < 0.82
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
ok = np.isfinite(C.entry_30) & C.volat_clk.notna() & C.z30.notna()
print("# GF-35 — the VWAP instead of the open, and the volume-weighted z-score\n")
print(f"{ok.sum():,} gap-ups with a 10:00 entry. Above the VWAP at 10:00: {held_v[ok].mean()*100:.0f} %; above the open: "
      f"{held_o[ok].mean()*100:.0f} %; agree {(held_v == held_o)[ok].mean()*100:.0f} %. Median z30 {C.z30[ok].median():+.2f}, "
      f"p10 / p90 {C.z30[ok].quantile(.1):+.2f} / {C.z30[ok].quantile(.9):+.2f}\n")

print("## 1. held vs failed, OPEN vs VWAP, 10:00 → close\n"); print(H)
for sub, sm in [("all", ok), ("calm", ok & calm), ("calm · fading", ok & calm & fading)]:
    print(row(f"{sub} · above the open", sm & held_o)); print(row(f"{sub} · below the open", sm & ~held_o))
    print(row(f"{sub} · **above the VWAP**", sm & held_v)); print(row(f"{sub} · **below the VWAP**", sm & ~held_v))
print()
print("## 2. the two references crossed, calm, 10:00 → close\n"); print(H)
for on, om in [("above open", held_o), ("below open", ~held_o)]:
    for vn, vm in [("above VWAP", held_v), ("below VWAP", ~held_v)]:
        print(row(f"calm · {on} · {vn}", ok & calm & om & vm))
print()

BANDS = [(-99, -2), (-2, -1), (-1, -0.5), (-0.5, 0), (0, 0.5), (0.5, 1), (1, 2), (2, 99)]
lab = lambda a, b: f"z < {b:g}" if a == -99 else (f"z ≥ {a:g}" if b == 99 else f"z {a:g} … {b:g}")
print("## 3. the volume-weighted z-score at 10:00, 10:00 → close\n")
for sub, sm in [("all", ok), ("calm", ok & calm), ("not calm", ok & ~calm)]:
    print(f"**{sub}**\n\n" + H)
    for a, b in BANDS: print(row(f"{sub} · {lab(a, b)}", sm & (C.z30 >= a) & (C.z30 < b)))
    print()

ok60 = np.isfinite(C.entry_60) & C.volat_clk.notna() & C.z60.notna() & C.z30.notna()
print("## 4. held at 10:00 → checked at 10:30, bought at 10:30 → close: OPEN vs VWAP, calm\n"); print(H)
for nm, h10, h30 in [("open", held_o, held_o60), ("VWAP", held_v, held_v60)]:
    print(row(f"calm · above the {nm} 10:00 · above 10:30", ok60 & calm & h10 & h30, "r60", "rn60"))
    print(row(f"calm · above the {nm} 10:00 · **below 10:30**", ok60 & calm & h10 & ~h30, "r60", "rn60"))
    print(row(f"calm · below the {nm} 10:00 · above 10:30", ok60 & calm & ~h10 & h30, "r60", "rn60"))
    print(row(f"calm · below the {nm} 10:00 · below 10:30", ok60 & calm & ~h10 & ~h30, "r60", "rn60"))
print()
print("## 5. the z-score at 10:30 (window 09:30–10:30), bought at 10:30 → close\n")
for sub, sm in [("calm", ok60 & calm), ("calm · above the open at 10:00", ok60 & calm & held_o)]:
    print(f"**{sub}**\n\n" + H)
    for a, b in BANDS: print(row(f"{sub} · {lab(a, b)}", sm & (C.z60 >= a) & (C.z60 < b), "r60", "rn60"))
    print()
