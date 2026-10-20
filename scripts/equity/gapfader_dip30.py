"""GapFader GF-37 (user 2026-10-20): a gap-up that HELD its open at 10:00 (px_30 >= open); instead of buying at 10:00, buy when it
makes a 30-MINUTE LOW, hold to the official close. Two readings of "a 30m low", both from 10:00, entries before 15:30:
  (a) ROLLING   the first 1s bar at/after 10:00 whose vwap is below the lowest vwap of the previous 1,800 s (a new trailing
                30-min low)
  (b) OR-LOW    the first 1s bar at/after 10:00 whose vwap is below the 09:30–10:00 lowest vwap (the opening range's low)
Entry = the NEXT 1s bar's vwap after the trigger (the research fill model); no trigger by 15:30 = no trade. Same sample as
GF-33..36: gap-up >= 4 %, rvol_pm >= 0.2, headline universe, net 0.7c / share; calm = volat_clk < 40 bp (09:30–10:00);
fading = GF-30. Cached in data/equity/gapfader/dip30.parquet.
Run from research/:  python -u scripts/equity/gapfader_dip30.py > data/equity/gapfader/dip30.md 2> data/equity/gapfader/dip30.log; echo exit $?
"""
import os, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm >= 0.2) & (C.gap >= 0.04)].copy()
X = pd.read_parquet("data/equity/gapfader/tape_persistence.parquet")[["ticker", "date", "act_a", "act_b", "px_30", "entry_30", "entry_60"]]
V = pd.read_parquet("data/equity/gapfader/volat30.parquet")[["ticker", "date", "volat_clk"]]
C = C.merge(X, on=["ticker", "date"]).merge(V, on=["ticker", "date"], how="left")
C = C[(C.px_30 >= C.open) & np.isfinite(C.entry_30) & C.volat_clk.notna()].copy()        # held at 10:00
OUT = "data/equity/gapfader/dip30.parquet"
if os.path.exists(OUT):
    D = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []; days = sorted(C.date.unique())
    for i, d in enumerate(days):
        tl = ",".join("'" + t.replace("'", "''") + "'" for t in C[C.date == d].ticker)
        D = con.execute(f"""
            WITH b AS (SELECT ticker, bucket, vwap::DOUBLE v FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                       WHERE ticker IN ({tl}) AND bucket >= 34200 AND bucket < 57600),
            w AS (SELECT ticker, bucket, v,
                         min(v) OVER (PARTITION BY ticker ORDER BY bucket RANGE BETWEEN 1800 PRECEDING AND 1 PRECEDING) lo_roll,
                         lead(v) OVER (PARTITION BY ticker ORDER BY bucket) v_next,
                         lead(bucket) OVER (PARTITION BY ticker ORDER BY bucket) b_next
                  FROM b),
            o AS (SELECT ticker, min(v) lo30 FROM b WHERE bucket < 36000 GROUP BY ticker)
            SELECT w.ticker, o.lo30,
                   min(bucket) FILTER (bucket >= 36000 AND bucket < 55800 AND v < lo_roll) t_roll,
                   arg_min(v_next, bucket) FILTER (bucket >= 36000 AND bucket < 55800 AND v < lo_roll) e_roll,
                   min(bucket) FILTER (bucket >= 36000 AND bucket < 55800 AND v < o.lo30) t_or,
                   arg_min(v_next, bucket) FILTER (bucket >= 36000 AND bucket < 55800 AND v < o.lo30) e_or
            FROM w JOIN o USING (ticker) GROUP BY w.ticker, o.lo30""").df()
        D["date"] = d; parts.append(D)
        if i % 300 == 0: log(f"{i+1}/{len(days)} {d}")
    D = pd.concat(parts, ignore_index=True); D.to_parquet(OUT); log(f"wrote {OUT}: {len(D):,}")
C = C.merge(D, on=["ticker", "date"], how="left")
C["yr"] = C.date.str[:4]; early = C.yr <= "2020"; NY = C.yr.nunique()
for k, e in [("10", "entry_30"), ("1030", "entry_60"), ("roll", "e_roll"), ("or", "e_or")]:
    C[f"r_{k}"] = C.close / C[e] - 1; C[f"rn_{k}"] = C[f"r_{k}"] - 0.007 / C[e]
calm = C.volat_clk < 40; fading = (C.act_b / C.act_a.where(C.act_a > 0)) < 0.82
trig_roll, trig_or = C.e_roll.notna(), C.e_or.notna()
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
hm = lambda b: f"{int(b // 3600):02d}:{int(b % 3600 // 60):02d}"
print("# GF-37 — held the open at 10:00, bought at the next 30-minute low, held to the close\n")
for sub, sm in [("all held", C.r_10.notna()), ("calm", calm), ("calm · fading", calm & fading)]:
    print(f"**{sub}** ({sm.sum():,}): a rolling 30-min low by 15:30 on {trig_roll[sm].mean()*100:.0f} % (median time "
          f"{hm(C.t_roll[sm & trig_roll].median())}); below the opening-range low on {trig_or[sm].mean()*100:.0f} % "
          f"(median {hm(C.t_or[sm & trig_or].median())})\n")
print(H)
for sub, sm in [("all held", C.r_10.notna()), ("calm", calm), ("calm · fading", calm & fading)]:
    print(row(f"{sub} · bought at 10:00 (GF-11 / GF-30)", sm, "10"))
    print(row(f"{sub} · bought at 10:30 (GF-34)", sm, "1030"))
    print(row(f"{sub} · **(a) rolling 30-min low**", sm & trig_roll, "roll"))
    print(row(f"{sub} · (a) the same days bought at 10:00", sm & trig_roll, "10"))
    print(row(f"{sub} · (a) no trigger: bought at 10:00", sm & ~trig_roll, "10"))
    print(row(f"{sub} · **(b) below the opening-range low**", sm & trig_or, "or"))
    print(row(f"{sub} · (b) the same days bought at 10:00", sm & trig_or, "10"))
    print(row(f"{sub} · (b) no trigger: bought at 10:00", sm & ~trig_or, "10"))
print()
print("## calm, (a) rolling: by the trigger's time\n"); print(H)
for a, b in [(36000, 37800), (37800, 41400), (41400, 48600), (48600, 55800)]:
    print(row(f"calm · (a) triggered {hm(a)}–{hm(b)}", calm & trig_roll & (C.t_roll >= a) & (C.t_roll < b), "roll"))
print()
print("## calm, (b) opening-range low: by the trigger's time\n"); print(H)
for a, b in [(36000, 37800), (37800, 41400), (41400, 48600), (48600, 55800)]:
    print(row(f"calm · (b) triggered {hm(a)}–{hm(b)}", calm & trig_or & (C.t_or >= a) & (C.t_or < b), "or"))
for nm, k, tm in [("(a) rolling", "roll", trig_roll), ("(b) OR-low", "or", trig_or)]:
    d = C[calm & tm]
    print(f"\ncalm · {nm} by year, net PF: " + " · ".join(f"{y} {pf(d[f'rn_{k}'][d.yr == y]):.2f} [{(d.yr == y).sum()}]" for y in sorted(C.yr.unique())))
