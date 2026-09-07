#!/usr/bin/env python3
"""SpringFlyer S3 — the MIRROR of the S2 short (user, 2026-09-07): a stock that
RISES a lot from the open during the day and then closes BELOW the open (and/or
the previous close). LONG at the close, hold k days. Returns are LONG returns.
`mae5` = the lowest low over the next 5 days (the long's adverse excursion).
Signal-day guard `close > 0.1 * prev_close` (the WB $0.01 class) — stated, minimal."""
import sys, numpy as np, pandas as pd, duckdb
con = duckdb.connect(); con.execute("SET memory_limit='6GB'; SET threads=8")
F = "read_parquet('data/springflyer_daily.parquet')"
FRAME = "dv20_prior >= 5e6 AND prev_close_raw >= 2 AND r1 IS NOT NULL AND close > 0.1 * prev_close"
NY = 22
def pf(r):
    g = r[r > 0].sum(); l = -r[r < 0].sum(); return g / l if l > 0 else float('inf')
HS = ['r_open1', 'r1', 'r3', 'r5', 'r10']
def row(where, label):
    d = con.execute(f"SELECT yr, {', '.join(HS)}, mae5 FROM {F} WHERE {FRAME} AND ({where})").df()
    if len(d) == 0: return f"| {label} | 0 |" + " | " * 8
    out = [f"| {label} | {len(d)/NY:,.0f}"]
    for h in HS:
        r = d[h].dropna().values
        out.append(f"{r.mean():+.0f} / {pf(r):.2f}")
    r5 = d['r5'].dropna(); yrs = d.groupby('yr')['r5'].mean()
    out.append(f"{(r5>0).mean()*100:.0f}% | {(yrs>0).sum()}/{yrs.size} | {np.median(r5):+.0f} | {np.median(d.mae5.dropna()):+.0f}")
    return " | ".join(out) + " |"
def table(title, cells):
    print(f"\n### {title}")
    print("| cell | n/yr | " + " | ".join(f"{h}: long mean bp / PF" for h in HS) + " | win5 | yrs5 | med r5 | med mae5 |")
    print("|---|---|" + "---|" * len(HS) + "---|---|---|---|")
    for lab, w in cells: print(row(w, lab))
    sys.stdout.flush()
RISE = [("+3..+5%", 0.03, 0.05), ("+5..+7%", 0.05, 0.07), ("+7..+10%", 0.07, 0.10), ("+10..+15%", 0.10, 0.15), ("+15..+25%", 0.15, 0.25), ("+25%+", 0.25, 9)]
RO = "(high/open - 1)"; RP = "(high/prev_close - 1)"
table("T1 — LONG the failed rally: RISE from the OPEN (high vs open) x closed BELOW the open; prev-close reference; BOTH; and the S2 short's own cells for reference (negated = what the mirror must beat)",
      [(f"open ref: rise_open {lab}, close < open", f"{RO} >= {a} AND {RO} < {b} AND rev_open < 0") for lab, a, b in RISE]
      + [(f"prev ref: rise_prev {lab}, close < prev", f"{RP} >= {a} AND {RP} < {b} AND rev_prev < 0") for lab, a, b in RISE]
      + [(f"BOTH: rise_open {lab}, close < open AND < prev", f"{RO} >= {a} AND {RO} < {b} AND rev_open < 0 AND rev_prev < 0") for lab, a, b in RISE]
      + [(f"CONTROL: rise_open {lab}, close >= open (rally held)", f"{RO} >= {a} AND {RO} < {b} AND rev_open >= 0") for lab, a, b in RISE])
B = f"{RO} >= 0.07 AND rev_open < 0"
table("T2 — how far BELOW the open did it close? (rise from open >= +7%)",
      [(f"rev_open {lab}", B + f" AND rev_open <= {a} AND rev_open > {b}") for lab, a, b in
       [("0..-1%", 0, -0.01), ("-1..-3%", -0.01, -0.03), ("-3..-6%", -0.03, -0.06), ("-6..-10%", -0.06, -0.10), ("-10%+", -0.10, -9)]])
table("T3 — the close's position in the range (rise from open >= +7%, close < open)",
      [(f"clpos {lab}", B + f" AND clpos >= {a} AND clpos < {b}") for lab, a, b in
       [("0-0.05 (closed at the low)", -0.01, 0.05), ("0.05-0.15", 0.05, 0.15), ("0.15-0.3", 0.15, 0.3), ("0.3-0.5", 0.3, 0.5)]])
table("T4 — the gap that set it up (rise from open >= +7%, close < open)",
      [(f"gap {lab}", B + f" AND gap >= {a} AND gap < {b}") for lab, a, b in
       [("<-15%", -9, -0.15), ("-15..-5%", -0.15, -0.05), ("-5..-1%", -0.05, -0.01), ("-1..+1%", -0.01, 0.01), ("+1..+5%", 0.01, 0.05), ("+5%+", 0.05, 9)]])
table("T5 — 20-day context, 20d-high break, rvol, liquidity (rise from open >= +7%, close < open)",
      [("broke the 20d HIGH intraday", B + " AND high > high20_prior"), ("did NOT break the 20d high", B + " AND high <= high20_prior"),
       ("chg20 < -30%", B + " AND chg20 < -0.3"), ("chg20 -30..-10%", B + " AND chg20 >= -0.3 AND chg20 < -0.1"),
       ("chg20 -10..+10%", B + " AND chg20 >= -0.1 AND chg20 < 0.1"), ("chg20 +10..+50%", B + " AND chg20 >= 0.1 AND chg20 < 0.5"), ("chg20 +50%+", B + " AND chg20 >= 0.5")]
      + [(f"rvol {lab}", B + f" AND rvol >= {a} AND rvol < {b}") for lab, a, b in [("<1", 0, 1), ("1-2", 1, 2), ("2-4", 2, 4), ("4+", 4, 1e9)]]
      + [(f"dv20 {lab}", B + f" AND dv20_prior >= {a} AND dv20_prior < {b}") for lab, a, b in [("$5-20M", 5e6, 2e7), ("$20-100M", 2e7, 1e8), ("$100M+", 1e8, 1e13)]])
HEAD = f"{RO} >= 0.10 AND rev_open < 0"
print(f"\n### T6 — year table, LONG, cell = rise from open >= +10% AND close < open\n{HEAD}")
d = con.execute(f"SELECT yr, {', '.join(HS)}, mae5 FROM {F} WHERE {FRAME} AND {HEAD}").df()
print("| year | n | " + " | ".join(HS) + " | med mae5 |\n|---|---|---|---|---|---|---|---|")
for y, g in d.groupby('yr'):
    print(f"| {y} | {len(g):,} | " + " | ".join(f"{g[h].dropna().mean():+.0f} / {pf(g[h].dropna().values):.2f}" for h in HS) + f" | {g.mae5.median():+.0f} |")
