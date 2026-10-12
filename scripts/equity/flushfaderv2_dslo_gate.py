"""FlushFaderV2 — V2-6: the near-low dslo GATE (user, 2026-10-12: "gate the [0.5 %, 2 %] part away — or should the
bottom end be 0.2 or 0.1 %?").

1. Fine bands of dslo below 3 % on the current book (fixed membership), by year — exactly 0 (the signal bar IS the
   session low) split out.
2. Gate variants REPLAYED: the 209,252 engine-level trips (broad_reference_trips.parquet) + dslo from the slice; drop
   dslo ∈ [lo, hi]; then the book's own construction (raw px ≥ $1, per-ticker-day mc=1 greedy in signal order). A
   removed trade can free its slot for a later trip of the same ticker-day, so dropping book rows is NOT the same.
Return = ret_exit + the $0.001/sh/side credit, flat (equal weight). Run from research/:
    python -u scripts/equity/flushfaderv2_dslo_gate.py > data/flushfaderv2/dslo_gate.log 2>&1; echo exit $?
"""
import numpy as np, pandas as pd, duckdb

CREDIT = 0.001
con = duckdb.connect(); con.execute("SET memory_limit='8GB'"); con.execute("SET threads=6")
T = con.execute("""
    SELECT t.symbol, t.trade_date, t.signal_sec, t.entry_sec, t.exit_sec, t.entry_px, t.ret_exit, s.dslo
    FROM 'data/flushfader_gate_review/broad_reference_trips.parquet' t
    LEFT JOIN 'data/flushfader_review_slice.parquet' s
      ON s.symbol = t.symbol AND s.trade_date::VARCHAR = t.trade_date AND s.signal_sec = t.signal_sec
    ORDER BY t.symbol, t.trade_date, t.signal_sec""").df()
assert T.dslo.notna().all(), f"{T.dslo.isna().sum()} trips without dslo"
T["r"] = (T.ret_exit + 2 * CREDIT / T.entry_px) * 100
T["yr"] = T.trade_date.str[:4].astype(int)
YEARS = sorted(T.yr.unique())

def book(drop):
    keep = np.zeros(len(T), bool); ok = (T.entry_px.values >= 1.0) & ~drop
    sym, dt, ent, ext = T.symbol.values, T.trade_date.values, T.entry_sec.values, T.exit_sec.values
    last, last_ext = None, -1
    for i in range(len(T)):
        k = (sym[i], dt[i])
        if k != last: last, last_ext = k, -1
        if ok[i] and ent[i] >= last_ext: keep[i] = True; last_ext = ext[i]
    return T[keep]
def pf1(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l - 1
def row(lab, B, base=None):
    r = B.r.values
    yrs = " | ".join(f"{pf1(r[B.yr.values == y]):.2f}" for y in YEARS)
    d = "" if base is None else f" | {len(B) - len(base):+,} | {r.sum() - base.r.sum():+,.0f}"
    return f"| {lab} | {len(B):,} | {pf1(r):.3f} | {r.mean():.3f} | {r.sum():,.0f}{d} | {yrs} |"

B0 = book(np.zeros(len(T), bool))
print(f"# V2-6 — the near-low dslo gate ({len(T):,} engine-level trips; base book {len(B0):,} — the reference is 37,254)\n")

# ---- 1. fine bands on the base book
x = B0.dslo.values
print("## 1. dslo below 3 % on the base book (fixed membership), PF−1 net of the credit\n")
print("| dslo | n | PF−1 | avg % | win % | " + " | ".join(map(str, YEARS)) + " |")
print("|---|---|---|---|---|" + "---|" * len(YEARS))
bands = [("= 0 (signal bar is the session low)", x == 0)]
edges = [0, 0.001, 0.002, 0.003, 0.005, 0.0075, 0.01, 0.015, 0.02, 0.025, 0.03]
for a, b in zip(edges[:-1], edges[1:]):
    bands.append((f"({a*100:g} %, {b*100:g} %)" if a == 0 else f"[{a*100:g} %, {b*100:g} %)", (x > a if a == 0 else x >= a) & (x < b)))
bands.append(("≥ 3 %", x >= 0.03))
for lab, m in bands:
    r = B0.r.values[m]; y = B0.yr.values[m]
    yrs = " | ".join(f"{pf1(r[y == yy]):.2f} ({(y == yy).sum()})" for yy in YEARS)
    print(f"| {lab} | {m.sum():,} | {pf1(r):.3f} | {r.mean():.3f} | {(r > 0).mean()*100:.1f} | {yrs} |")

# ---- 2. gate variants, replayed
print("\n## 2. Gate dslo ∈ [lo, hi], REPLAYED (flat, net of the credit)\n")
print("| book | n | PF−1 | avg % | net % | Δ n | Δ net | " + " | ".join(map(str, YEARS)) + " |")
print("|---|---|---|---|---|---|---|" + "---|" * len(YEARS))
print(row("base (no dslo gate)", B0, B0))
d = T.dslo.values
for lo in (0.0005, 0.001, 0.002, 0.003, 0.005):
    for hi in (0.02, 0.03):
        B = book((d >= lo) & (d <= hi))
        print(row(f"drop [{lo*100:g} %, {hi*100:g} %]", B, B0))
B = book((d > 0) & (d <= 0.02)); print(row("drop (0, 2 %] (everything off the exact low)", B, B0))
