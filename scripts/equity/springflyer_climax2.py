#!/usr/bin/env python3
"""SpringFlyer S7 — the user's climax spec (2026-09-07): the last 3 days up > 50%,
D-1 closed at a 52-WEEK CLOSING HIGH, extreme volume on the climax days, the 10-day
efficiency ratio at D-1 > 0.8 (0.9), then the first RED day that keeps most of the
move (retrace of the 3-day move < 0.3). SHORT at D's close. Short returns. The
green-day control (short at the climax close, same stack on D itself) beside it."""
import sys, numpy as np, pandas as pd, duckdb
con = duckdb.connect(); con.execute("SET memory_limit='6GB'; SET threads=8")
def q(sql): return pd.DataFrame(con.execute(sql).fetchnumpy())
F = "read_parquet('data/springflyer_daily.parquet')"
FRAME = "dv20_prior >= 5e6 AND prev_close_raw >= 2 AND r1 IS NOT NULL AND close > 0.1 * prev_close AND atr20_prior > 0"
NY = 22
def pf(r):
    g = r[r > 0].sum(); l = -r[r < 0].sum(); return g / l if l > 0 else float('inf')
HS = ['r_open1', 'r1', 'r3', 'r5', 'r10']
def row(where, label):
    d = q(f"SELECT date, yr, {', '.join(HS)}, mfe5 FROM {F} WHERE {FRAME} AND ({where})")
    if len(d) == 0: return f"| {label} | 0 |" + " | " * 10
    r5 = -d.r5.dropna(); yrs = -d.groupby('yr').r5.mean(); m = d.mfe5.dropna()
    cnt = d.groupby('date').size(); share10 = (d.date.map(cnt) >= 10).mean() * 100
    out = [f"| {label} | {len(d)/NY:,.0f} | {d.yr.nunique()}"]
    out += [f"{(-d[h].dropna()).mean():+.0f} / {pf(-d[h].dropna().values):.2f}" for h in HS]
    out.append(f"{(r5>0).mean()*100:.0f}% | {(yrs>0).sum()}/{yrs.size} | {np.median(r5):+.0f} | {np.percentile(m,95):+.0f} | {share10:.0f}%")
    return " | ".join(out) + " |"
def table(title, cells):
    print(f"\n### {title}")
    print("| cell | n/yr | yrs w/ trips | " + " | ".join(HS) + " | win5 | yrs5 | med r5 | p95 mfe5 | trips on 10+ days |")
    print("|---|---|---|" + "---|" * 5 + "---|---|---|---|---|")
    for lab, w in cells: print(row(w, lab))
    sys.stdout.flush()
RED = "rev_prev < 0 AND retrace3 < 0.3"
L0 = f"chg3_prev >= 0.5 AND {RED}"
table("T1 — THE LADDER (first red day, retrace of the 3-day move < 0.3): each condition added in turn",
      [("3-day move >= 50%, red, retrace3 < 0.3", L0),
       ("+ D-1 at a 52w CLOSING high", L0 + " AND at52_prev = 1"),
       ("+ loudest climax day rvol >= 5", L0 + " AND at52_prev = 1 AND run_rvol_max3 >= 5"),
       ("+ loudest climax day rvol >= 10", L0 + " AND at52_prev = 1 AND run_rvol_max3 >= 10"),
       ("+ er10 at D-1 >= 0.8 (with rvol >= 5)", L0 + " AND at52_prev = 1 AND run_rvol_max3 >= 5 AND prev_er10s >= 0.8"),
       ("+ er10 at D-1 >= 0.9 (with rvol >= 5)", L0 + " AND at52_prev = 1 AND run_rvol_max3 >= 5 AND prev_er10s >= 0.9"),
       ("er10 >= 0.9, rvol >= 10", L0 + " AND at52_prev = 1 AND run_rvol_max3 >= 10 AND prev_er10s >= 0.9"),
       ("CONTROL: same but NOT at a 52w high", L0 + " AND at52_prev = 0 AND run_rvol_max3 >= 5 AND prev_er10s >= 0.8"),
       ("CONTROL: same but er10 < 0.5", L0 + " AND at52_prev = 1 AND run_rvol_max3 >= 5 AND prev_er10s < 0.5"),
       ("CONTROL: same but quiet (rvol max < 2)", L0 + " AND at52_prev = 1 AND run_rvol_max3 < 2 AND prev_er10s >= 0.8")])
B = L0 + " AND at52_prev = 1"
table("T2 — bands inside (3-day >= 50% x 52w-high x red): efficiency at D-1, loudest rvol, the move's size",
      [(f"er10 at D-1 {lab}", B + f" AND prev_er10s >= {a} AND prev_er10s < {b}") for lab, a, b in [("<0.5", -1.01, 0.5), ("0.5-0.7", 0.5, 0.7), ("0.7-0.8", 0.7, 0.8), ("0.8-0.9", 0.8, 0.9), ("0.9+", 0.9, 1.01)]]
      + [(f"loudest rvol {lab}", B + f" AND run_rvol_max3 >= {a} AND run_rvol_max3 < {b}") for lab, a, b in [("<2", 0, 2), ("2-5", 2, 5), ("5-10", 5, 10), ("10-25", 10, 25), ("25+", 25, 1e9)]]
      + [(f"3-day move {lab}", B + f" AND chg3_prev >= {a} AND chg3_prev < {b}") for lab, a, b in [("50-80%", 0.5, 0.8), ("80-150%", 0.8, 1.5), ("150%+", 1.5, 99)]]
      + [(f"retrace3 {lab}", L0.replace(" AND retrace3 < 0.3", "") + f" AND at52_prev = 1 AND retrace3 >= {a} AND retrace3 < {b}") for lab, a, b in [("<0.1", -9, 0.1), ("0.1-0.3", 0.1, 0.3), ("0.3-0.6", 0.3, 0.6), ("0.6+", 0.6, 99)]])
G = "chg3_d >= 0.5 AND at52_d = 1 AND rev_prev > 0"
table("T3 — the GREEN-DAY CONTROL: short at the climax close itself (3-day move incl. today >= 50%, closed at a 52w closing high, green)",
      [("green climax, 52w high", G),
       ("+ rvol today >= 5", G + " AND rvol_d >= 5"),
       ("+ rvol today >= 5 AND er10 today >= 0.8", G + " AND rvol_d >= 5 AND er10_signed >= 0.8"),
       ("+ rvol today >= 5 AND er10 today >= 0.9", G + " AND rvol_d >= 5 AND er10_signed >= 0.9"),
       ("+ rvol today >= 10 AND er10 today >= 0.9", G + " AND rvol_d >= 10 AND er10_signed >= 0.9")])
HEAD = L0 + " AND at52_prev = 1 AND run_rvol_max3 >= 5 AND prev_er10s >= 0.8"
print(f"\n### T4 — year table, SHORT, the stacked first-red cell: {HEAD}")
d = q(f"SELECT yr, {', '.join(HS)}, mfe5 FROM {F} WHERE {FRAME} AND {HEAD}")
print("| year | n | r1 | r5 | r10 | win5 | p95 mfe5 |\n|---|---|---|---|---|---|---|")
for y, g in d.groupby('yr'):
    print(f"| {y} | {len(g)} | " + " | ".join(f"{(-g[h].dropna()).mean():+.0f} / {pf(-g[h].dropna().values):.2f}" for h in ['r1', 'r5', 'r10']) + f" | {(-g.r5.dropna()>0).mean()*100:.0f}% | {np.percentile(g.mfe5.dropna(),95):+.0f} |")
