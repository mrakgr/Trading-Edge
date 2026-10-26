"""GapFader GF-50 (user 2026-10-26, after NIVF: "what we need is a bar count filter over the past 20 days. The sharp uptick
in volume when it traded 26m shares on one of the previous days ended up fooling the volume filter"): an ACTIVITY floor.

THE BOOK: GF-49's (data/equity/gapfader/spiral.parquet = GF-45's gates + the Oms's exit, one target at 50 % to 10:00,
and production units clip(PF−1, 0.125, 2)).

THE FEATURES (the 20 PRIOR sessions inside the ticker's episode, knowable before D's open; data/equity/daily_bars from
scripts/equity/build_daily_bars.py):
  bars20_med   median of rth_bars (1s bars with volume in 09:30–16:00, of 23,400) — one spike day cannot lift it
  bars20_mean  mean of rth_bars, for comparison
  bars20_min   the quietest of the 20 sessions
  vol_top1     the largest session's share of the 20 sessions' volume (in D's share units) — how much ONE day carries
               dv20 / avgvol20 (NIVF: one 58M-share day)
  dv20_med     the MEDIAN of the 20 sessions' dollar volume (volume × close, raw: scale-free) — user 2026-10-26: "let's
               also introduce median 20d volume as a feature"; comparable to the census's dv20_prior (the MEAN)
  spike        dv20_prior / dv20_med: how far a few big days lift the mean over the median
A session with no 1s bars at all counts 0 bars.

CONTROLS: the disproportion test (share of the book refused vs the PF move) and an ISO-TRIP control — the same number of
trades refused AT RANDOM, 200 draws: a real filter beats the random refusal's distribution.

  cd research && python -u scripts/equity/gapfader_bars20.py > data/equity/gapfader/bars20.md 2> logs/gapfader_bars20.log; echo exit $?
"""
import sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)

B = pd.read_parquet("data/equity/gapfader/spiral.parquet")
B["date"] = pd.to_datetime(B.date)
log(f"book {len(B):,}")
con = duckdb.connect()
con.execute("set memory_limit='4GB'; set threads=6; set enable_progress_bar=false; set temp_directory='logs/duckdb_tmp_bars20'")
con.register("bk", B[["ticker", "date"]])
F = con.execute("""
    WITH e0 AS (SELECT ticker, date, volume / n AS vol_n, volume * close AS dvol,
                       CASE WHEN date - lag(date) OVER (PARTITION BY ticker ORDER BY date) > 45 THEN 1 ELSE 0 END AS brk
                FROM 'data/v2/daily_adjusted.parquet' WHERE ticker IN (SELECT DISTINCT ticker FROM bk)),
    ep AS (SELECT *, sum(brk) OVER (PARTITION BY ticker ORDER BY date) AS episode FROM e0),
    db AS (SELECT ticker, strptime(regexp_extract(filename, '(\\d{4}-\\d{2}-\\d{2})', 1), '%Y-%m-%d')::DATE AS date, rth_bars
           FROM read_parquet('data/equity/daily_bars/*.parquet', filename = true) WHERE ticker IN (SELECT DISTINCT ticker FROM bk)),
    j AS (SELECT ep.*, coalesce(db.rth_bars, 0) AS rth_bars FROM ep LEFT JOIN db USING (ticker, date)),
    w AS (SELECT ticker, date,
                 median(rth_bars) OVER p AS bars20_med, avg(rth_bars) OVER p AS bars20_mean, min(rth_bars) OVER p AS bars20_min,
                 max(vol_n) OVER p / nullif(sum(vol_n) OVER p, 0) AS vol_top1, median(dvol) OVER p AS dv20_med,
                 count(*) OVER p AS n20
          FROM j WINDOW p AS (PARTITION BY ticker, episode ORDER BY date ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING))
    SELECT w.* FROM w JOIN bk USING (ticker, date)""").df()
F["date"] = pd.to_datetime(F.date)
B = B.merge(F, on=["ticker", "date"], how="left")
C0 = pd.read_parquet("data/equity/gapfader/census.parquet", columns=["ticker", "date", "dv20_prior"])
C0["date"] = pd.to_datetime(C0.date)
B = B.merge(C0, on=["ticker", "date"], how="left")
B["spike"] = B.dv20_prior / B.dv20_med
log(f"features for {B.bars20_med.notna().sum():,}; n20 == 20 for {(B.n20 == 20).mean() * 100:.1f} %")

R, U, YR = B.r.values, B.units.values, B.date.dt.year.values
def pf(r, w=None):
    r = np.asarray(r) * (1 if w is None else np.asarray(w)); g, l = r[r > 0].sum(), -r[r < 0].sum()
    return np.inf if l == 0 else g / l
def fmt(x): return "inf" if np.isinf(x) else ("—" if np.isnan(x) else f"{x:.2f}")
def row(name, m):
    r, u, y = R[m], U[m], YR[m]
    yrs = " | ".join(fmt(pf(r[y == k])) + f" [{(y == k).sum()}]" for k in range(2016, 2027))
    return (f"| {name} | {m.sum():,} | {fmt(pf(r))} | {fmt(pf(r, u))} | {r.mean():+.2f} | {r.min():+.1f} | "
            f"{(r * u).sum() / 100:+.1f} | {yrs} |")
HDR = ("| slice | n | PF flat | PF units | mean % | worst % | Σ units·r | " + " | ".join(str(k) for k in range(2016, 2027)) + " |\n"
       + "|---|---|---|---|---|---|---|" + "---|" * 11)

print("# GF-50 — an ACTIVITY floor: the 20-session median of RTH active 1s bars (user 2026-10-26, after NIVF)\n")
print(f"Book: GF-49's {len(B):,} trades (the Oms's exit and units). bars = 1s bars with volume in 09:30–16:00 (of 23,400), "
      f"the 20 PRIOR sessions in the episode.\n")
print("## bars20_med buckets (years: PF flat [n])\n")
print(HDR)
med = B.bars20_med.values
edges = [0, 250, 500, 1000, 2000, 4000, 8000, 16000, 23401]
for lo, hi in zip(edges[:-1], edges[1:]):
    print(row(f"{lo:,} … {hi:,}", (med >= lo) & (med < hi)))
print(row("ALL", np.ones(len(B), bool)))

print("\n## vol_top1 buckets: the biggest of the 20 sessions' share of their volume\n")
print(HDR)
vt = B.vol_top1.values
for lo, hi in [(0, 0.1), (0.1, 0.2), (0.2, 0.35), (0.35, 0.5), (0.5, 0.75), (0.75, 1.01)]:
    print(row(f"{lo:.2f} … {hi:.2f}", (vt >= lo) & (vt < hi)))

print("\n## dv20_med buckets: the MEDIAN dollar volume of the 20 prior sessions (the gate today: the MEAN dv20 ≥ $1M)\n")
print(HDR)
dm = B.dv20_med.values
for lo, hi in [(0, 2.5e5), (2.5e5, 5e5), (5e5, 1e6), (1e6, 2e6), (2e6, 5e6), (5e6, 2.5e7), (2.5e7, np.inf)]:
    print(row(f"${lo / 1e6:g}M … ${hi / 1e6:g}M", (dm >= lo) & (dm < hi)))
print("\n## spike = dv20 (mean) / dv20_med (median) buckets\n")
print(HDR)
sp = B.spike.values
for lo, hi in [(0, 1.25), (1.25, 1.5), (1.5, 2), (2, 3), (3, 5), (5, np.inf)]:
    print(row(f"{lo:g} … {hi:g}", (sp >= lo) & (sp < hi)))

rng = np.random.default_rng(20261026)
base_u, base_sum = pf(R, U), (R * U).sum() / 100
def iso(n, draws=200):
    out = []
    for _ in range(draws):
        keep = np.ones(len(B), bool); keep[rng.choice(len(B), n, replace=False)] = False
        out.append(pf(R[keep], U[keep]) - base_u)
    return np.quantile(out, [0.05, 0.5, 0.95])
print("\n## THE RULES: refuse below a floor — vs the ISO-TRIP control (the same n refused at random, 200 draws)\n")
print("| rule | refused n (%) | refused PF flat / units | refused Σ | kept PF units | Δ PF units | random Δ p5 / p50 / p95 | kept Σ | Δ Σ |")
print("|---|---|---|---|---|---|---|---|---|")
for th in (100, 250, 500, 1000, 2000, 3000, 4000):
    rf = med < th; kp = ~rf
    q = iso(int(rf.sum()))
    print(f"| bars20_med < {th:,} | {rf.sum():,} ({rf.mean() * 100:.2f} %) | {fmt(pf(R[rf]))} / {fmt(pf(R[rf], U[rf]))} | "
          f"{(R[rf] * U[rf]).sum() / 100:+.1f} | {fmt(pf(R[kp], U[kp]))} | {pf(R[kp], U[kp]) - base_u:+.3f} | "
          f"{q[0]:+.3f} / {q[1]:+.3f} / {q[2]:+.3f} | {(R[kp] * U[kp]).sum() / 100:+.1f} | {(R[kp] * U[kp]).sum() / 100 - base_sum:+.1f} |")

for th in (2.5e5, 5e5, 7.5e5, 1e6, 2e6):
    rf = dm < th; kp = ~rf
    q = iso(int(rf.sum()))
    print(f"| dv20_med < ${th / 1e6:g}M | {rf.sum():,} ({rf.mean() * 100:.2f} %) | {fmt(pf(R[rf]))} / {fmt(pf(R[rf], U[rf]))} | "
          f"{(R[rf] * U[rf]).sum() / 100:+.1f} | {fmt(pf(R[kp], U[kp]))} | {pf(R[kp], U[kp]) - base_u:+.3f} | "
          f"{q[0]:+.3f} / {q[1]:+.3f} / {q[2]:+.3f} | {(R[kp] * U[kp]).sum() / 100:+.1f} | {(R[kp] * U[kp]).sum() / 100 - base_sum:+.1f} |")
for th in (2, 3, 5):
    rf = sp >= th; kp = ~rf
    q = iso(int(rf.sum()))
    print(f"| spike ≥ {th:g} | {rf.sum():,} ({rf.mean() * 100:.2f} %) | {fmt(pf(R[rf]))} / {fmt(pf(R[rf], U[rf]))} | "
          f"{(R[rf] * U[rf]).sum() / 100:+.1f} | {fmt(pf(R[kp], U[kp]))} | {pf(R[kp], U[kp]) - base_u:+.3f} | "
          f"{q[0]:+.3f} / {q[1]:+.3f} / {q[2]:+.3f} | {(R[kp] * U[kp]).sum() / 100:+.1f} | {(R[kp] * U[kp]).sum() / 100 - base_sum:+.1f} |")

print("\n## The same refusals by regime (flat PF [n]: refused vs kept)\n")
for th in (500, 1000, 2000):
    rf = med < th
    parts = []
    for nm, m in [("2016–19", YR <= 2019), ("2020", YR == 2020), ("2021–23", (YR >= 2021) & (YR <= 2023)), ("2024–26", YR >= 2024)]:
        parts.append(f"{nm} {fmt(pf(R[rf & m]))} [{(rf & m).sum()}] vs {fmt(pf(R[~rf & m]))}")
    print(f"- bars20_med < {th:,}: " + " · ".join(parts))

print("\n## Correlations with the sizing inputs (Spearman)\n")
for a, b in [("bars20_med", "pf1"), ("bars20_med", "vol_top1"), ("bars20_med", "chg_1y"), ("bars20_med", "dv20_med"),
             ("dv20_med", "dv20_prior"), ("dv20_med", "pf1"), ("spike", "pf1")]:
    print(f"- {a} ~ {b}: {B[a].rank().corr(B[b].rank()):+.3f}")

x = B[B.ticker == "NIVF"][["date", "open", "gap", "r", "pf1", "bars20_med", "bars20_mean", "bars20_min", "vol_top1", "dv20_prior", "dv20_med", "spike"]]
print("\n## NIVF in the book\n")
print(x.to_string(index=False))
B.to_parquet("data/equity/gapfader/bars20.parquet")
log("done")
