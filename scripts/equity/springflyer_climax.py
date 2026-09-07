#!/usr/bin/env python3
"""SpringFlyer S6 — the FIRST RED DAY after a CLIMAX (user, 2026-09-07; the Tim Sykes
short): several consecutive green closes with a large run gain, then the first
down close that does NOT give most of the run back. SHORT at D's close (and, as
the alternative, at D+1's open). Short returns (bp, + = profit).
  prev_streak    = consecutive up closes ending D-1
  prev_run_gain  = close(D-1) / the run's base close - 1
  retrace        = (close(D-1) - close(D)) / (close(D-1) - run_base)   (1 = gave it all back)
  prev_run_maxday = biggest single up day inside the run"""
import sys, numpy as np, pandas as pd, duckdb
con = duckdb.connect(); con.execute("SET memory_limit='6GB'; SET threads=8")
def q(sql): return pd.DataFrame(con.execute(sql).fetchnumpy())
F = "read_parquet('data/springflyer_daily.parquet')"
FRAME = "dv20_prior >= 5e6 AND prev_close_raw >= 2 AND r1 IS NOT NULL AND close > 0.1 * prev_close AND atr20_prior > 0"
NY = 22
def pf(r):
    g = r[r > 0].sum(); l = -r[r < 0].sum(); return g / l if l > 0 else float('inf')
HS = ['r_open1', 'r1', 'r3', 'r5', 'r10']
def load(where):
    d = q(f"SELECT date, yr, {', '.join(HS)}, mfe5 FROM {F} WHERE {FRAME} AND ({where})")
    # entry at the NEXT OPEN instead of D's close: (1+r_k)/(1+r_open1) - 1
    for h in ['r1', 'r3', 'r5', 'r10']:
        d[h + '_o'] = ((1 + d[h] / 1e4) / (1 + d.r_open1 / 1e4) - 1) * 1e4
    return d
def row(where, label):
    d = load(where)
    if len(d) == 0: return f"| {label} | 0 |" + " | " * 12
    r5 = -d.r5.dropna(); yrs = -d.groupby('yr').r5.mean(); m = d.mfe5.dropna()
    cnt = d.groupby('date').size(); share10 = (d.date.map(cnt) >= 10).mean() * 100
    out = [f"| {label} | {len(d)/NY:,.0f}"]
    out += [f"{(-d[h].dropna()).mean():+.0f} / {pf(-d[h].dropna().values):.2f}" for h in HS]
    out += [f"{(-d[h].dropna()).mean():+.0f} / {pf(-d[h].dropna().values):.2f}" for h in ['r5_o', 'r10_o']]
    out.append(f"{(r5>0).mean()*100:.0f}% | {(yrs>0).sum()}/{yrs.size} | {np.percentile(m,95):+.0f} | {share10:.0f}%")
    return " | ".join(out) + " |"
def table(title, cells):
    print(f"\n### {title}")
    print("| cell | n/yr | " + " | ".join(f"{h}" for h in HS) + " | r5 @next open | r10 @next open | win5 | yrs5 | p95 mfe5 | trips on 10+ days |")
    print("|---|---|" + "---|" * 7 + "---|---|---|---|")
    for lab, w in cells: print(row(w, lab))
    sys.stdout.flush()
RED = "rev_prev < 0"
table("T1 — the first RED day: streak x run gain, retrace < 50% (short mean bp / PF; entries at D's close, and at D+1's open)",
      [(f"streak >= {k}, run {gl}, red, retrace < 0.5", f"prev_streak >= {k} AND prev_run_gain >= {a} AND prev_run_gain < {b} AND {RED} AND retrace < 0.5")
       for k in [2, 3, 4] for gl, a, b in [("20-50%", 0.2, 0.5), ("50-100%", 0.5, 1.0), ("100%+", 1.0, 99)]])
B = f"prev_streak >= 3 AND prev_run_gain >= 0.5 AND {RED}"
table("T2 — how much of the run did the red day give back? (streak >= 3, run >= 50%)",
      [(f"retrace {lab}", B + f" AND retrace >= {a} AND retrace < {b}") for lab, a, b in
       [("0-0.15 (barely red)", -9, 0.15), ("0.15-0.3", 0.15, 0.3), ("0.3-0.5", 0.3, 0.5), ("0.5-0.75", 0.5, 0.75), ("0.75-1", 0.75, 1.0), (">1 (closed below the base)", 1.0, 99)]])
table("T3 — CONTROLS: short the GREEN climax day itself (no red yet), and the 2nd red day",
      [("green day, streak_d >= 3, run gain (incl. today) >= 50%", "up_streak_d >= 3 AND close / run_base - 1 >= 0.5"),
       ("green day, streak_d >= 4, run gain >= 100%", "up_streak_d >= 4 AND close / run_base - 1 >= 1.0"),
       ("the FIRST red (streak>=3, run>=50%, retrace<0.5)", B + " AND retrace < 0.5"),
       ("a red day with NO climax (streak <= 1, |chg20| < 10%)", f"{RED} AND prev_streak <= 1 AND ABS(chg20) < 0.1")])
C = B + " AND retrace < 0.5"
table("T4 — inside the first-red cell (streak >= 3, run >= 50%, retrace < 0.5): D's own shape and context",
      [(f"D's range {lab}", C + f" AND rng >= {a} AND rng < {b}") for lab, a, b in [("<7%", 0, 0.07), ("7-15%", 0.07, 0.15), ("15-30%", 0.15, 0.3), ("30%+", 0.3, 9)]]
      + [(f"D closed {lab}", C + f" AND rev_open {op}") for lab, op in [("below the open (red body)", "< 0"), ("above the open (gap-down, bought)", "> 0")]]
      + [(f"clpos {lab}", C + f" AND clpos >= {a} AND clpos < {b}") for lab, a, b in [("0-0.25", -0.01, 0.25), ("0.25-0.5", 0.25, 0.5), ("0.5-0.75", 0.5, 0.75), ("0.75-1", 0.75, 1.01)]]
      + [(f"rvol on D {lab}", C + f" AND rvol >= {a} AND rvol < {b}") for lab, a, b in [("<1", 0, 1), ("1-2", 1, 2), ("2-5", 2, 5), ("5+", 5, 1e9)]]
      + [(f"biggest day in the run {lab}", C + f" AND prev_run_maxday >= {a} AND prev_run_maxday < {b}") for lab, a, b in [("<15%", 0, 0.15), ("15-30%", 0.15, 0.3), ("30-60%", 0.3, 0.6), ("60%+", 0.6, 9)]]
      + [(f"chg20 {lab}", C + f" AND chg20 >= {a} AND chg20 < {b}") for lab, a, b in [("<50%", -9, 0.5), ("50-150%", 0.5, 1.5), ("150%+", 1.5, 99)]]
      + [(f"prev close {lab}", C + f" AND prev_close_raw >= {a} AND prev_close_raw < {b}") for lab, a, b in [("$2-5", 2, 5), ("$5-15", 5, 15), ("$15+", 15, 1e9)]])
print(f"\n### T5 — year table, SHORT, first red: {C}")
d = load(C)
print("| year | n | r1 | r5 | r10 | r5 @open | win5 | p95 mfe5 |\n|---|---|---|---|---|---|---|---|")
for y, g in d.groupby('yr'):
    print(f"| {y} | {len(g)} | " + " | ".join(f"{(-g[h].dropna()).mean():+.0f} / {pf(-g[h].dropna().values):.2f}" for h in ['r1', 'r5', 'r10', 'r5_o']) + f" | {(-g.r5.dropna()>0).mean()*100:.0f}% | {np.percentile(g.mfe5.dropna(),95):+.0f} |")
