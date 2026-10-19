"""GapFader GF-32 (user 2026-10-19): the GF-30 SIMPLE RULE on GAP-DOWNS — a gap-down >= 4 % (headline universe: open >= $1,
dv20_prior >= $1M, no early close; ALL premarket volumes) that HELD ITS OPEN at 10:00 (the last 1s vwap before 10:00 >= the
open), bought at the first 1s vwap at/after 10:00, out at the official close; net 0.7c / share.
The 09:30–10:00 tape exactly as GF-10 / GF-11 (one pass over the 1s bars, cached in data/equity/gapfader/gapdown_tape.parquet):
volat_clk (30 s clock-slot vwaps, mean |ln r| in bp), trades / active bars / volume per window 09:30–09:45 and 09:45–10:00.
The gap-up rule's cuts unchanged: calm = volat_clk < 40 bp; activity fading = active bars 09:45–10:00 / 09:30–09:45 < 0.82.
Run from research/:  python -u scripts/equity/gapfader_gapdown_swing.py > data/equity/gapfader/gapdown_swing.md 2> data/equity/gapfader/gapdown_swing.log; echo exit $?
"""
import os, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.gap <= -0.04)].copy()
log(f"{len(C):,} gap-downs ≤ −4 %")
OUT = "data/equity/gapfader/gapdown_tape.parquet"
if os.path.exists(OUT):
    X = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []; days = sorted(C.date.unique())
    for i, d in enumerate(days):
        tl = ",".join("'" + t.replace("'", "''") + "'" for t in C[C.date == d].ticker)
        X = con.execute(f"""
            WITH b AS (SELECT ticker, bucket, vwap::DOUBLE v, volume::DOUBLE vol, trade_count tc
                       FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                       WHERE ticker IN ({tl}) AND bucket >= 34200 AND bucket < 37800),
            t AS (SELECT ticker,
                    count(*) FILTER (bucket < 35100) act_a, count(*) FILTER (bucket >= 35100 AND bucket < 36000) act_b,
                    coalesce(sum(tc) FILTER (bucket < 35100), 0) tc_a, coalesce(sum(tc) FILTER (bucket >= 35100 AND bucket < 36000), 0) tc_b,
                    coalesce(sum(vol) FILTER (bucket < 35100), 0) vol_a, coalesce(sum(vol) FILTER (bucket >= 35100 AND bucket < 36000), 0) vol_b,
                    arg_max(v, bucket) FILTER (bucket < 36000) px_30, arg_min(v, bucket) FILTER (bucket >= 36000) entry_30
                  FROM b GROUP BY ticker),
            c AS (SELECT ticker, (bucket - 34200) // 30 cslot, sum(v * vol) / sum(vol) sv FROM b
                  WHERE bucket < 36000 AND vol > 0 GROUP BY ticker, cslot),
            cr AS (SELECT ticker, abs(ln(sv / lag(sv) OVER (PARTITION BY ticker ORDER BY cslot))) r FROM c),
            vv AS (SELECT ticker, avg(r) * 1e4 volat_clk FROM cr GROUP BY ticker)
            SELECT t.*, vv.volat_clk FROM t LEFT JOIN vv USING (ticker)""").df()
        X["date"] = d; parts.append(X)
        if i % 300 == 0: log(f"{i+1}/{len(days)} {d}")
    X = pd.concat(parts, ignore_index=True); X.to_parquet(OUT); log(f"wrote {OUT}: {len(X):,}")
C = C.merge(X, on=["ticker", "date"])
C = C[(C.px_30 >= C.open) & np.isfinite(C.entry_30) & C.volat_clk.notna() & (C.act_a > 0)].copy()
C["r"] = C.close / C.entry_30 - 1; C["rn"] = C.r - 0.007 / C.entry_30
C["apers"] = C.act_b / C.act_a; C["yr"] = C.date.str[:4]; early = C.yr <= "2020"
calm, fading = C.volat_clk < 40, C.apers < 0.82
NY = C.yr.nunique()
def pf(x):
    x = np.asarray(x); x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
H = "| cell | n | n / yr | median % | win % | PF | PF clip +50 % | **net PF** | net 2016–20 / 2021–26 | years < 1 (net) |\n|---|---|---|---|---|---|---|---|---|---|"
def row(nm, m):
    d = C[m]
    if len(d) < 25: return f"| {nm} | {len(d)} | | | | | | | | |"
    yrs = d.groupby("yr").rn.apply(pf)
    return (f"| {nm} | {len(d):,} | {len(d)/NY:.0f} | {d.r.median()*100:+.2f} | {(d.r>0).mean()*100:.0f} | {pf(d.r):.2f} | "
            f"{pf(d.r.clip(upper=0.5)):.2f} | **{pf(d.rn):.2f}** | {pf(d.rn[early[m]]):.2f} / {pf(d.rn[~early[m]]):.2f} | {int((yrs<1).sum())}/{len(yrs)} |")
print(f"# GF-32 — the gap-DOWN that held its open at 10:00, 10:00 → close, by the gap-up swing's rule\n")
print(f"{len(C):,} held gap-downs (of the gap-downs ≤ −4 % with a 10:00 entry). calm = volat_clk < 40 bp ({calm.mean()*100:.0f} %); "
      f"activity fading = active bars 09:45–10:00 / 09:30–09:45 < 0.82 ({fading.mean()*100:.0f} %).\n")
print("## 1. The rule\n"); print(H)
print(row("all held gap-downs", C.r.notna()))
print(row("calm", calm)); print(row("not calm", ~calm)); print(row("wild (≥ 80 bp)", C.volat_clk >= 80))
print(row("**calm · activity fading** (the gap-up rule)", calm & fading))
print(row("calm · activity not fading", calm & ~fading))
print()
print("## 2. By premarket volume (rvol_pm)\n"); print(H)
for a, b in [(0, 0.02), (0.02, 0.2), (0.2, 1), (1, 1e9)]:
    m = (C.rvol_pm >= a) & (C.rvol_pm < b); nm = f"{a:g}–{b:g}" if b < 1e8 else f"≥ {a:g}"
    print(row(f"rvol_pm {nm} · all held", m)); print(row(f"rvol_pm {nm} · calm · fading", m & calm & fading))
print()
print("## 3. By gap size, calm · fading\n"); print(H)
for a, b in [(0.04, 0.06), (0.06, 0.10), (0.10, 0.20), (0.20, 9)]:
    print(row(f"gap −{a*100:g} … −{b*100:g} %" if b < 9 else f"gap ≤ −{a*100:g} %", calm & fading & (-C.gap >= a) & (-C.gap < b)))
print()
d = C[calm & fading]
print("## 4. calm · fading by year, net PF\n\n| year | " + " | ".join(sorted(d.yr.unique())) + " |\n|---|" + "---|" * d.yr.nunique())
print("| n | " + " | ".join(str((d.yr == y).sum()) for y in sorted(d.yr.unique())) + " |")
print("| net PF | " + " | ".join(f"{pf(d.rn[d.yr == y]):.2f}" for y in sorted(d.yr.unique())) + " |")
