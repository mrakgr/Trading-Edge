#!/usr/bin/env python3
"""SpringFlyer S2 — the SHORT side, OPEN as the reference (user, 2026-09-07).
Short AT THE CLOSE of a day that fell hard from its open and closed back ABOVE the
open; cover at the close k days later. Returns are SHORT returns (bp, positive =
profit). `mfe5` on the feature table is the highest high over the next 5 days —
the short's adverse excursion; `hi1` = tomorrow's high."""
import sys, numpy as np, pandas as pd, duckdb
con = duckdb.connect(); con.execute("SET memory_limit='6GB'; SET threads=8")
F = "read_parquet('data/springflyer_daily.parquet')"
FRAME = "dv20_prior >= 5e6 AND prev_close_raw >= 2 AND r1 IS NOT NULL"
NY = 22
def pf(r):
    g = r[r > 0].sum(); l = -r[r < 0].sum(); return g / l if l > 0 else float('inf')
HS = ['r_open1', 'r1', 'r3', 'r5', 'r10']
def row(where, label):
    d = con.execute(f"SELECT yr, {', '.join(HS)}, mfe5, hi1 FROM {F} WHERE {FRAME} AND ({where})").df()
    if len(d) == 0: return f"| {label} | 0 |" + " | " * 8
    out = [f"| {label} | {len(d)/NY:,.0f}"]
    for h in HS:
        r = -d[h].dropna().values          # SHORT
        out.append(f"{r.mean():+.0f} / {pf(r):.2f}")
    r5 = -d['r5'].dropna(); yrs = (-d.groupby('yr')['r5'].mean())
    out.append(f"{(r5>0).mean()*100:.0f}% | {(yrs>0).sum()}/{yrs.size} | {np.median(d.mfe5.dropna()):+.0f} | {np.percentile(d.mfe5.dropna(),95):+.0f}")
    return " | ".join(out) + " |"
def table(title, cells):
    print(f"\n### {title}")
    print("| cell | n/yr | " + " | ".join(f"{h}: short mean bp / PF" for h in HS) + " | win5 | yrs5 | med mfe5 | p95 mfe5 |")
    print("|---|---|" + "---|" * len(HS) + "---|---|---|---|")
    for lab, w in cells: print(row(w, lab))
    sys.stdout.flush()

DECL = [("-3..-5%", -0.05, -0.03), ("-5..-7%", -0.07, -0.05), ("-7..-10%", -0.10, -0.07),
        ("-10..-15%", -0.15, -0.10), ("-15..-25%", -0.25, -0.15), ("<-25%", -9, -0.25)]
table("T1 — SHORT the reversal: decline from the OPEN (low vs open) x closed ABOVE the open; and the prev-close reference beside it",
      [(f"open ref: decl_open {lab}, close > open", f"decl_open >= {a} AND decl_open < {b} AND rev_open > 0") for lab, a, b in DECL]
      + [(f"prev ref: decl_prev {lab}, close > prev", f"decl_prev >= {a} AND decl_prev < {b} AND rev_prev > 0") for lab, a, b in DECL]
      + [(f"BOTH: decl_open {lab}, close > open AND > prev", f"decl_open >= {a} AND decl_open < {b} AND rev_open > 0 AND rev_prev > 0") for lab, a, b in DECL])

B = "decl_open < -0.07 AND rev_open > 0"
table("T2 — how far ABOVE the open did it close? (decline from open <= -7%)",
      [(f"rev_open {lab}", B + f" AND rev_open >= {a} AND rev_open < {b}") for lab, a, b in
       [("0..+1%", 0, 0.01), ("+1..+3%", 0.01, 0.03), ("+3..+6%", 0.03, 0.06), ("+6..+10%", 0.06, 0.10), ("+10%+", 0.10, 9)]])
table("T3 — the close's position in the range (decline from open <= -7%, close > open)",
      [(f"clpos {lab}", B + f" AND clpos >= {a} AND clpos < {b}") for lab, a, b in
       [("0.5-0.7", 0.5, 0.7), ("0.7-0.85", 0.7, 0.85), ("0.85-0.95", 0.85, 0.95), ("0.95-1", 0.95, 1.01)]])
table("T4 — the gap that set it up (decline from open <= -7%, close > open)",
      [(f"gap {lab}", B + f" AND gap >= {a} AND gap < {b}") for lab, a, b in
       [("<-5%", -9, -0.05), ("-5..-1%", -0.05, -0.01), ("-1..+1%", -0.01, 0.01), ("+1..+5%", 0.01, 0.05), ("+5..+15%", 0.05, 0.15), ("+15%+", 0.15, 9)]])
table("T5 — 7d-low break and the 20-day context (decline from open <= -7%, close > open)",
      [("broke the 7d low", B + " AND below7 < 0"), ("did NOT break the 7d low", B + " AND below7 >= 0"),
       ("chg20 < -15%", B + " AND chg20 < -0.15"), ("chg20 -15..+15%", B + " AND chg20 >= -0.15 AND chg20 < 0.15"),
       ("chg20 +15..+50%", B + " AND chg20 >= 0.15 AND chg20 < 0.5"), ("chg20 +50%+", B + " AND chg20 >= 0.5")])
table("T6 — relative volume and liquidity (decline from open <= -7%, close > open)",
      [(f"rvol {lab}", B + f" AND rvol >= {a} AND rvol < {b}") for lab, a, b in [("<1", 0, 1), ("1-2", 1, 2), ("2-4", 2, 4), ("4+", 4, 1e9)]]
      + [(f"dv20 {lab}", B + f" AND dv20_prior >= {a} AND dv20_prior < {b}") for lab, a, b in [("$5-20M", 5e6, 2e7), ("$20-100M", 2e7, 1e8), ("$100M+", 1e8, 1e13)]])
HEAD = "decl_open < -0.10 AND rev_open > 0"
print(f"\n### T7 — year table, SHORT, cell = decline from open <= -10% AND close > open\n{HEAD}")
d = con.execute(f"SELECT yr, {', '.join(HS)}, mfe5 FROM {F} WHERE {FRAME} AND {HEAD}").df()
print("| year | n | " + " | ".join(HS) + " | p95 mfe5 |\n|---|---|---|---|---|---|---|---|")
for y, g in d.groupby('yr'):
    print(f"| {y} | {len(g):,} | " + " | ".join(f"{(-g[h].dropna()).mean():+.0f} / {pf(-g[h].dropna().values):.2f}" for h in HS) + f" | {np.percentile(g.mfe5.dropna(),95):+.0f} |")
