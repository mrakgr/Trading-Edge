"""GapFader GF-51 (user 2026-10-26: "what if instead of looking at the bar counts for the last 20d, we looked at the previous
trading day. Let's look into previous day's volume and intraday bar count as a filter"): the PREVIOUS SESSION's activity.

THE BOOK: GF-50's (data/equity/gapfader/bars20.parquet: GF-49's trades + the 20-session features).
THE FEATURES (D−1 = the previous session in the ticker's episode; knowable before D's open):
  bars_d1    RTH active 1s bars on D−1 (data/equity/daily_bars; none = 0)
  dv_d1      D−1's dollar volume (volume × close, raw)
  rel_d1     dv_d1 / dv20_med: D−1 against the 20-session median (GF-50)
CONTROLS: the iso-trip control (the same n refused at random, 200 draws) and the regime split.

  cd research && python -u scripts/equity/gapfader_prevday.py > data/equity/gapfader/prevday.md 2> logs/gapfader_prevday.log; echo exit $?
"""
import sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)

B = pd.read_parquet("data/equity/gapfader/bars20.parquet")
B["date"] = pd.to_datetime(B.date)
con = duckdb.connect()
con.execute("set memory_limit='4GB'; set threads=6; set enable_progress_bar=false")
con.register("bk", B[["ticker", "date"]])
F = con.execute("""
    WITH d AS (SELECT ticker, date, lag(date) OVER w AS d1, lag(volume * close) OVER w AS dv_d1
               FROM 'data/v2/daily_adjusted.parquet' WHERE ticker IN (SELECT DISTINCT ticker FROM bk)
               WINDOW w AS (PARTITION BY ticker ORDER BY date)),
    db AS (SELECT ticker, strptime(regexp_extract(filename, '(\\d{4}-\\d{2}-\\d{2})', 1), '%Y-%m-%d')::DATE AS date, rth_bars
           FROM read_parquet('data/equity/daily_bars/*.parquet', filename = true) WHERE ticker IN (SELECT DISTINCT ticker FROM bk))
    SELECT d.ticker, d.date, d.dv_d1, coalesce(db.rth_bars, 0) AS bars_d1
    FROM d JOIN bk USING (ticker, date) LEFT JOIN db ON db.ticker = d.ticker AND db.date = d.d1""").df()
F["date"] = pd.to_datetime(F.date)
B = B.merge(F, on=["ticker", "date"], how="left")
B["rel_d1"] = B.dv_d1 / B.dv20_med
log(f"book {len(B):,}; dv_d1 known {B.dv_d1.notna().mean() * 100:.1f} %")

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
print("# GF-51 — the PREVIOUS SESSION's activity: bar count and dollar volume on D−1 (user 2026-10-26, after NIVF)\n")
print(f"Book: GF-49/50's {len(B):,} trades (the Oms's exit and units).\n")
print("## bars_d1: RTH active 1s bars on the previous session (of 23,400)\n")
print(HDR)
b1 = B.bars_d1.values
for lo, hi in zip([0, 250, 500, 1000, 2000, 4000, 8000, 16000], [250, 500, 1000, 2000, 4000, 8000, 16000, 23401]):
    print(row(f"{lo:,} … {hi:,}", (b1 >= lo) & (b1 < hi)))
print("\n## dv_d1: the previous session's dollar volume\n")
print(HDR)
d1 = B.dv_d1.values
for lo, hi in [(0, 2.5e5), (2.5e5, 5e5), (5e5, 1e6), (1e6, 2e6), (2e6, 5e6), (5e6, 2.5e7), (2.5e7, np.inf)]:
    print(row(f"${lo / 1e6:g}M … ${hi / 1e6:g}M", (d1 >= lo) & (d1 < hi)))
print("\n## rel_d1 = dv_d1 / dv20_med: the previous session against the 20-session median\n")
print(HDR)
rl = B.rel_d1.values
for lo, hi in [(0, 0.25), (0.25, 0.5), (0.5, 1), (1, 2), (2, 5), (5, np.inf)]:
    print(row(f"{lo:g} … {hi:g}", (rl >= lo) & (rl < hi)))

rng = np.random.default_rng(20261026)
base_u, base_sum = pf(R, U), (R * U).sum() / 100
def iso(n, draws=200):
    out = []
    for _ in range(draws):
        keep = np.ones(len(B), bool); keep[rng.choice(len(B), n, replace=False)] = False
        out.append(pf(R[keep], U[keep]) - base_u)
    return np.quantile(out, [0.05, 0.5, 0.95])
def rule(nm, rf):
    kp = ~rf; q = iso(int(rf.sum()))
    print(f"| {nm} | {rf.sum():,} ({rf.mean() * 100:.2f} %) | {fmt(pf(R[rf]))} / {fmt(pf(R[rf], U[rf]))} | {(R[rf] * U[rf]).sum() / 100:+.1f} | "
          f"{fmt(pf(R[kp], U[kp]))} | {pf(R[kp], U[kp]) - base_u:+.3f} | {q[0]:+.3f} / {q[1]:+.3f} / {q[2]:+.3f} | "
          f"{(R[kp] * U[kp]).sum() / 100 - base_sum:+.1f} |")
print("\n## THE RULES vs the ISO-TRIP control (the same n refused at random, 200 draws)\n")
print("| rule | refused n (%) | refused PF flat / units | refused Σ | kept PF units | Δ PF units | random Δ p5 / p50 / p95 | Δ Σ |")
print("|---|---|---|---|---|---|---|---|")
for th in (250, 500, 1000, 2000): rule(f"bars_d1 < {th:,}", b1 < th)
for th in (2.5e5, 5e5, 1e6, 2e6): rule(f"dv_d1 < ${th / 1e6:g}M", d1 < th)
for th in (2, 5): rule(f"rel_d1 ≥ {th:g} (yesterday busy)", rl >= th)
for th in (0.5, 0.25): rule(f"rel_d1 < {th:g} (yesterday quiet)", rl < th)

print("\n## By regime (flat PF [n]: refused vs kept)\n")
for nm0, rf in [("bars_d1 < 500", b1 < 500), ("dv_d1 < $0.5M", d1 < 5e5), ("rel_d1 ≥ 5", rl >= 5)]:
    parts = []
    for nm, m in [("2016–19", YR <= 2019), ("2020", YR == 2020), ("2021–23", (YR >= 2021) & (YR <= 2023)), ("2024–26", YR >= 2024)]:
        parts.append(f"{nm} {fmt(pf(R[rf & m]))} [{(rf & m).sum()}] vs {fmt(pf(R[~rf & m]))}")
    print(f"- {nm0}: " + " · ".join(parts))
print("\n## Correlations (Spearman)\n")
for a, b in [("bars_d1", "bars20_med"), ("dv_d1", "dv20_med"), ("bars_d1", "pf1"), ("rel_d1", "pf1")]:
    print(f"- {a} ~ {b}: {B[a].rank().corr(B[b].rank()):+.3f}")
B.to_parquet("data/equity/gapfader/prevday.parquet")
log("done")
