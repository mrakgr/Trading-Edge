"""GapFader GF-52 (user 2026-10-27, after NIVF and GIXI: "how would our system look when we rank the signals on the 60-day
median dollar volume?"): the MEDIAN dollar volume over the 60 prior sessions.

NIVF 10-26 passed GF-50's 20-session median ($2.32M): its 20 sessions were the death-spiral frenzy. Over 60 sessions its
median is $224k (Aug–Sep ~$150k a day). GIXI (37 sessions since its 09-02 listing): $99k over 20, $166k over all 37.

THE BOOK: GF-51's (data/equity/gapfader/prevday.parquet: GF-49's trades + GF-50's and GF-51's features).
THE FEATURES (the prior sessions inside the ticker's episode, knowable before D's open):
  dv60_med   median of volume × close (raw: scale-free) over the 60 PRIOR sessions; fewer when the episode is younger
             (n60 = how many) — a 37-session listing takes all 37
  surge      dv20_med / dv60_med: the last 20 sessions against the last 60 (NIVF ≈ 10)
CONTROLS: the iso-trip control (the same n refused at random, 200 draws) and the regime split.

  cd research && python -u scripts/equity/gapfader_dv60.py > data/equity/gapfader/dv60.md 2> logs/gapfader_dv60.log; echo exit $?
"""
import sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)

B = pd.read_parquet("data/equity/gapfader/prevday.parquet")
B["date"] = pd.to_datetime(B.date)
con = duckdb.connect()
con.execute("set memory_limit='4GB'; set threads=6; set enable_progress_bar=false; set temp_directory='logs/duckdb_tmp_dv60'")
con.register("bk", B[["ticker", "date"]])
F = con.execute("""
    WITH e0 AS (SELECT ticker, date, volume * close AS dvol,
                       CASE WHEN date - lag(date) OVER (PARTITION BY ticker ORDER BY date) > 45 THEN 1 ELSE 0 END AS brk
                FROM 'data/v2/daily_adjusted.parquet' WHERE ticker IN (SELECT DISTINCT ticker FROM bk)),
    ep AS (SELECT *, sum(brk) OVER (PARTITION BY ticker ORDER BY date) AS episode FROM e0),
    w AS (SELECT ticker, date, median(dvol) OVER p AS dv60_med, count(*) OVER p AS n60
          FROM ep WINDOW p AS (PARTITION BY ticker, episode ORDER BY date ROWS BETWEEN 60 PRECEDING AND 1 PRECEDING))
    SELECT w.* FROM w JOIN bk USING (ticker, date)""").df()
F["date"] = pd.to_datetime(F.date)
B = B.merge(F, on=["ticker", "date"], how="left")
B["surge"] = B.dv20_med / B.dv60_med
log(f"book {len(B):,}; dv60_med known {B.dv60_med.notna().mean() * 100:.1f} %; n60 == 60 for {(B.n60 == 60).mean() * 100:.1f} %")

R, U, YR = B.r.values, B.units.values, B.date.dt.year.values
def pf(r, w=None):
    r = np.asarray(r) * (1 if w is None else np.asarray(w)); g, l = r[r > 0].sum(), -r[r < 0].sum()
    return np.inf if l == 0 else g / l
def fmt(x): return "inf" if np.isinf(x) else ("—" if np.isnan(x) else f"{x:.2f}")
def row(name, m):
    r, u, y = R[m], U[m], YR[m]
    if m.sum() == 0: return f"| {name} | 0 |" + " |" * 16
    yrs = " | ".join(fmt(pf(r[y == k])) + f" [{(y == k).sum()}]" for k in range(2016, 2027))
    return (f"| {name} | {m.sum():,} | {fmt(pf(r))} | {fmt(pf(r, u))} | {r.mean():+.2f} | {r.min():+.1f} | "
            f"{(r * u).sum() / 100:+.1f} | {yrs} |")
HDR = ("| slice | n | PF flat | PF units | mean % | worst % | Σ units·r | " + " | ".join(str(k) for k in range(2016, 2027)) + " |\n"
       + "|---|---|---|---|---|---|---|" + "---|" * 11)
print("# GF-52 — the 60-session MEDIAN dollar volume (user 2026-10-27, after NIVF and GIXI)\n")
print(f"Book: GF-49..51's {len(B):,} trades (the Oms's exit and units). dv60_med over the 60 prior sessions in the episode "
      f"(fewer for a younger listing: n60 < 60 for {(B.n60 < 60).sum():,} trades).\n")
print("## dv60_med buckets (years: PF flat [n])\n")
print(HDR)
d60 = B.dv60_med.values
bins = [(0, 1e5), (1e5, 2.5e5), (2.5e5, 5e5), (5e5, 1e6), (1e6, 2e6), (2e6, 5e6), (5e6, 2.5e7), (2.5e7, np.inf)]
for lo, hi in bins:
    print(row(f"${lo / 1e6:g}M … ${hi / 1e6:g}M", (d60 >= lo) & (d60 < hi)))
print(row("ALL", np.ones(len(B), bool)))
print("\n## dv60_med deciles\n")
print(HDR)
q = np.nanquantile(d60, np.linspace(0, 1, 11))
for i in range(10):
    m = (d60 >= q[i]) & ((d60 < q[i + 1]) if i < 9 else (d60 <= q[i + 1]))
    print(row(f"D{i + 1} ${q[i] / 1e6:.3g}M … ${q[i + 1] / 1e6:.3g}M", m))
print("\n## surge = dv20_med / dv60_med: the last 20 sessions against the last 60\n")
print(HDR)
sg = B.surge.values
for lo, hi in [(0, 0.5), (0.5, 0.8), (0.8, 1.25), (1.25, 2), (2, 4), (4, 8), (8, np.inf)]:
    print(row(f"{lo:g} … {hi:g}", (sg >= lo) & (sg < hi)))
print("\n## dv60_med × surge (PF flat [n])\n")
cols = [(0, 1.25, "surge < 1.25"), (1.25, 4, "1.25 … 4"), (4, np.inf, "≥ 4")]
print("| dv60_med | " + " | ".join(c[2] for c in cols) + " |\n|---|---|---|---|")
for lo, hi in [(0, 2.5e5), (2.5e5, 1e6), (1e6, 5e6), (5e6, np.inf)]:
    cells = []
    for a, b, _ in cols:
        m = (d60 >= lo) & (d60 < hi) & (sg >= a) & (sg < b)
        cells.append(f"{fmt(pf(R[m]))} / {fmt(pf(R[m], U[m]))} [{m.sum():,}]" if m.sum() else "—")
    print(f"| ${lo / 1e6:g}M … ${hi / 1e6:g}M | " + " | ".join(cells) + " |")

rng = np.random.default_rng(20261027)
base_u, base_sum = pf(R, U), (R * U).sum() / 100
def iso(n, draws=200):
    out = []
    for _ in range(draws):
        keep = np.ones(len(B), bool); keep[rng.choice(len(B), n, replace=False)] = False
        out.append(pf(R[keep], U[keep]) - base_u)
    return np.quantile(out, [0.05, 0.5, 0.95])
def rule(nm, rf):
    kp = ~rf; qq = iso(int(rf.sum()))
    print(f"| {nm} | {rf.sum():,} ({rf.mean() * 100:.2f} %) | {fmt(pf(R[rf]))} / {fmt(pf(R[rf], U[rf]))} | {(R[rf] * U[rf]).sum() / 100:+.1f} | "
          f"{fmt(pf(R[kp], U[kp]))} | {pf(R[kp], U[kp]) - base_u:+.3f} | {qq[0]:+.3f} / {qq[1]:+.3f} / {qq[2]:+.3f} | "
          f"{(R[kp] * U[kp]).sum() / 100 - base_sum:+.1f} |")
print("\n## THE RULES vs the ISO-TRIP control (the same n refused at random, 200 draws)\n")
print("| rule | refused n (%) | refused PF flat / units | refused Σ | kept PF units | Δ PF units | random Δ p5 / p50 / p95 | Δ Σ |")
print("|---|---|---|---|---|---|---|---|")
for th in (1e5, 2.5e5, 5e5, 1e6): rule(f"dv60_med < ${th / 1e6:g}M", d60 < th)
for th in (4, 8): rule(f"surge ≥ {th:g}", sg >= th)
rule("dv60_med < $0.25M and surge ≥ 4 (NIVF's shape)", (d60 < 2.5e5) & (sg >= 4))

print("\n## By regime (flat PF [n]: refused vs kept)\n")
for nm0, rf in [("dv60_med < $0.25M", d60 < 2.5e5), ("surge ≥ 4", sg >= 4)]:
    parts = []
    for nm, m in [("2016–19", YR <= 2019), ("2020", YR == 2020), ("2021–23", (YR >= 2021) & (YR <= 2023)), ("2024–26", YR >= 2024)]:
        parts.append(f"{nm} {fmt(pf(R[rf & m]))} [{(rf & m).sum()}] vs {fmt(pf(R[~rf & m]))}")
    print(f"- {nm0}: " + " · ".join(parts))
print("\n## Correlations (Spearman)\n")
for a, b in [("dv60_med", "dv20_med"), ("dv60_med", "dv20_prior"), ("dv60_med", "pf1"), ("surge", "pf1"), ("surge", "spike")]:
    print(f"- {a} ~ {b}: {B[a].rank().corr(B[b].rank()):+.3f}")
B.to_parquet("data/equity/gapfader/dv60.parquet")
log("done")
