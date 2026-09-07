#!/usr/bin/env python3
"""SpringFlyer S4 — BIG-RANGE DAYS as a SHORT (user, 2026-09-07): the common factor
of S2 (fell, reclaimed) and S3 (spiked, failed) is the RANGE. Short at the close of
a big-range day, cover k days later. Short returns (bp, + = profit). `mfe5` = the
5-day high vs entry (adverse); `tail>50%` = share of trades whose 5-day high is
more than 50% above the entry. Signal-day guard close > 0.1 * prev_close."""
import sys, numpy as np, pandas as pd, duckdb
con = duckdb.connect(); con.execute("SET memory_limit='6GB'; SET threads=8")
def q(sql):  # .df() on a streaming result hits a DuckDB 1.5.0 internal cast error; materialize via arrow
    return pd.DataFrame(con.execute(sql).fetchnumpy())
F = "read_parquet('data/springflyer_daily.parquet')"
FRAME = "dv20_prior >= 5e6 AND prev_close_raw >= 2 AND r1 IS NOT NULL AND close > 0.1 * prev_close AND atr20_prior > 0"
NY = 22
def pf(r):
    g = r[r > 0].sum(); l = -r[r < 0].sum(); return g / l if l > 0 else float('inf')
HS = ['r_open1', 'r1', 'r3', 'r5', 'r10']
def row(where, label, era=""):
    d = q(f"SELECT yr, {', '.join(HS)}, mfe5 FROM {F} WHERE {FRAME} AND ({where}){era}")
    ny = 11 if era else NY
    if len(d) == 0: return f"| {label} | 0 |" + " | " * 9
    out = [f"| {label} | {len(d)/ny:,.0f}"]
    for h in HS:
        r = -d[h].dropna().values
        out.append(f"{r.mean():+.0f} / {pf(r):.2f}")
    r5 = -d['r5'].dropna(); yrs = -d.groupby('yr')['r5'].mean(); m = d.mfe5.dropna()
    out.append(f"{(r5>0).mean()*100:.0f}% | {(yrs>0).sum()}/{yrs.size} | {np.median(r5):+.0f} | {np.percentile(m,95):+.0f} | {(m>5000).mean()*100:.1f}%")
    return " | ".join(out) + " |"
def table(title, cells, era=""):
    print(f"\n### {title}")
    print("| cell | n/yr | " + " | ".join(f"{h}: short mean / PF" for h in HS) + " | win5 | yrs5 | med r5 | p95 mfe5 | tail>50% |")
    print("|---|---|" + "---|" * len(HS) + "---|---|---|---|---|")
    for lab, w in cells: print(row(w, lab, era))
    sys.stdout.flush()
RB = [("5-7%", 0.05, 0.07), ("7-10%", 0.07, 0.10), ("10-15%", 0.10, 0.15), ("15-25%", 0.15, 0.25), ("25-40%", 0.25, 0.40), ("40%+", 0.40, 99)]
table("T1 — the day's RANGE (high/low − 1), short at the close, no other condition",
      [(f"rng {lab}", f"rng >= {a} AND rng < {b}") for lab, a, b in RB])
table("T1b — the same, 2016+ only", [(f"rng {lab}", f"rng >= {a} AND rng < {b}") for lab, a, b in RB], era=" AND yr >= 2016")
AB = [("2-3x", 2, 3), ("3-4x", 3, 4), ("4-6x", 4, 6), ("6-10x", 6, 10), ("10x+", 10, 999)]
table("T2 — the range in units of the stock's OWN prior 20-day average range (rng_atr)",
      [(f"rng_atr {lab}", f"rng_atr >= {a} AND rng_atr < {b}") for lab, a, b in AB])
print("\n### T2b — rng x rng_atr cross, r5 short mean / PF (n/yr)")
print("| rng \\ rng_atr | <3x | 3-6x | 6-10x | 10x+ |\n|---|---|---|---|---|")
for lab, a, b in RB[2:]:
    cells = []
    for la, aa, bb in [("<3x", 0, 3), ("3-6x", 3, 6), ("6-10x", 6, 10), ("10x+", 10, 999)]:
        d = q(f"SELECT r5 FROM {F} WHERE {FRAME} AND rng >= {a} AND rng < {b} AND rng_atr >= {aa} AND rng_atr < {bb}")
        r = -d.r5.dropna().values
        cells.append(f"{r.mean():+.0f} / {pf(r):.2f} ({len(d)/NY:,.0f})" if len(r) > 50 else "—")
    print(f"| {lab} | " + " | ".join(cells) + " |")
sys.stdout.flush()
B = "rng >= 0.15"
table("T3 — inside the big-range days (rng >= 15%): where did it close, and which way?",
      [(f"clpos {lab}", B + f" AND clpos >= {a} AND clpos < {b}") for lab, a, b in
       [("0-0.2 (near the low)", -0.01, 0.2), ("0.2-0.4", 0.2, 0.4), ("0.4-0.6", 0.4, 0.6), ("0.6-0.8", 0.6, 0.8), ("0.8-1 (near the high)", 0.8, 1.01)]]
      + [("close < open AND < prev (down day)", B + " AND rev_open < 0 AND rev_prev < 0"),
         ("close > open AND > prev (up day)", B + " AND rev_open > 0 AND rev_prev > 0"),
         ("S2 shape: low far below open, closed above open", B + " AND decl_open < -0.07 AND rev_open > 0"),
         ("S3 shape: high far above open, closed below open", B + " AND (high/open-1) >= 0.07 AND rev_open < 0"),
         ("closed above the prev close, below the open (gap-up faded)", B + " AND rev_prev > 0 AND rev_open < 0"),
         ("closed below the prev close, above the open (gap-down bought)", B + " AND rev_prev < 0 AND rev_open > 0")])
table("T4 — context on big-range days (rng >= 15%): the runner, the gap, volume, price, liquidity",
      [(f"chg20 {lab}", B + f" AND chg20 >= {a} AND chg20 < {b}") for lab, a, b in [("<-30%", -9, -0.3), ("-30..-10%", -0.3, -0.1), ("-10..+10%", -0.1, 0.1), ("+10..+50%", 0.1, 0.5), ("+50..+150%", 0.5, 1.5), ("+150%+", 1.5, 99)]]
      + [(f"gap {lab}", B + f" AND gap >= {a} AND gap < {b}") for lab, a, b in [("<-10%", -9, -0.1), ("-10..0%", -0.1, 0), ("0..+10%", 0, 0.1), ("+10..+30%", 0.1, 0.3), ("+30%+", 0.3, 99)]]
      + [(f"rvol {lab}", B + f" AND rvol >= {a} AND rvol < {b}") for lab, a, b in [("<2", 0, 2), ("2-5", 2, 5), ("5-10", 5, 10), ("10-25", 10, 25), ("25+", 25, 1e9)]]
      + [(f"prev close {lab}", B + f" AND prev_close_raw >= {a} AND prev_close_raw < {b}") for lab, a, b in [("$2-5", 2, 5), ("$5-10", 5, 10), ("$10-25", 10, 25), ("$25+", 25, 1e9)]]
      + [(f"dv20 {lab}", B + f" AND dv20_prior >= {a} AND dv20_prior < {b}") for lab, a, b in [("$5-20M", 5e6, 2e7), ("$20-100M", 2e7, 1e8), ("$100M-1B", 1e8, 1e9), ("$1B+", 1e9, 1e13)]])
HEAD = "rng >= 0.15"
print(f"\n### T5 — year table, SHORT, rng >= 15%\n")
d = q(f"SELECT yr, {', '.join(HS)}, mfe5 FROM {F} WHERE {FRAME} AND {HEAD}")
print("| year | n | " + " | ".join(HS) + " | win5 | p95 mfe5 |\n|---|---|---|---|---|---|---|---|---|")
for y, g in d.groupby('yr'):
    print(f"| {y} | {len(g):,} | " + " | ".join(f"{(-g[h].dropna()).mean():+.0f} / {pf(-g[h].dropna().values):.2f}" for h in HS) + f" | {(-g.r5.dropna()>0).mean()*100:.0f}% | {np.percentile(g.mfe5.dropna(),95):+.0f} |")

EB = [("<0.2", 0, 0.2), ("0.2-0.4", 0.2, 0.4), ("0.4-0.6", 0.4, 0.6), ("0.6-0.8", 0.6, 0.8), ("0.8+", 0.8, 1.01)]
table("T6 — EFFICIENCY RATIO on closes, 10-day, inside big-range days (rng >= 15%): |net| / path",
      [(f"er10 {lab}", B + f" AND er10 >= {a} AND er10 < {b}") for lab, a, b in EB])
table("T6b — er10 SIGNED (direction of the 10-day net move), rng >= 15%",
      [(f"er10_signed {lab}", B + f" AND er10_signed >= {a} AND er10_signed < {b}") for lab, a, b in
       [("<-0.6 (efficient DOWN)", -1.01, -0.6), ("-0.6..-0.3", -0.6, -0.3), ("-0.3..0", -0.3, 0), ("0..0.3", 0, 0.3), ("0.3..0.6", 0.3, 0.6), ("0.6+ (efficient UP)", 0.6, 1.01)]])
table("T6c — er20 SIGNED, rng >= 15%",
      [(f"er20_signed {lab}", B + f" AND er20_signed >= {a} AND er20_signed < {b}") for lab, a, b in
       [("<-0.6", -1.01, -0.6), ("-0.6..-0.3", -0.6, -0.3), ("-0.3..0", -0.3, 0), ("0..0.3", 0, 0.3), ("0.3..0.6", 0.3, 0.6), ("0.6+", 0.6, 1.01)]])
table("T6d — er10 x the day's direction (rng >= 15%)",
      [("efficient UP (er10_s >= 0.5) AND closed DOWN on the day", B + " AND er10_signed >= 0.5 AND rev_open < 0"),
       ("efficient UP (er10_s >= 0.5) AND closed UP on the day", B + " AND er10_signed >= 0.5 AND rev_open > 0"),
       ("efficient DOWN (er10_s <= -0.5) AND closed UP on the day", B + " AND er10_signed <= -0.5 AND rev_open > 0"),
       ("efficient DOWN (er10_s <= -0.5) AND closed DOWN on the day", B + " AND er10_signed <= -0.5 AND rev_open < 0"),
       ("choppy (|er10_s| < 0.3)", B + " AND ABS(er10_signed) < 0.3")])
