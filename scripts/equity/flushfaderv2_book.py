"""FlushFaderV2 — V2-7: the V2 BOOK = the broad book + the near-low gate (drop dslo ∈ (0, 2 %], user 2026-10-12),
REPLAYED from the engine-level trips, with every model input attached (the schema of book_features.parquet, so the
V2 model scripts take it via --book).

  trips  = data/flushfader_gate_review/broad_reference_trips.parquet (209,252 engine-level trips, S49aj)
  + the slice's inputs (dslo, s20, ...) joined on symbol / trade_date / signal_sec
  gate   = drop dslo > LO and dslo <= HI   (defaults 0, 0.02)
  book   = raw px >= $1, per-ticker-day mc=1 greedy in signal order (the reference construction)
  A3     = the band columns (gap 0 / 1-3 / 4-39, volat 40/60/90/140/250, rate600 >= .07 (NaN = slow), S tier)

Also a holdout check of the gate's own cutoffs: (lo, hi) picked on one year block by flat net, applied to the other.
Run from research/:
    python -u scripts/equity/flushfaderv2_book.py > data/flushfaderv2/v2_book.log 2>&1; echo exit $?
"""
import argparse
import numpy as np, pandas as pd, duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--lo", type=float, default=0.0)
ap.add_argument("--hi", type=float, default=0.02)
ap.add_argument("--credit", type=float, default=0.001)
ap.add_argument("--out", default="data/flushfaderv2/v2_book.parquet")
args = ap.parse_args()

SLICE_COLS = ["s20", "speed", "accel", "rngf", "rflow", "d1m", "eff20a", "s5", "k20", "dlv", "ssf", "e9",
              "d20a", "dslo", "vexp", "vcrush", "ac1", "esf", "dsu"]
con = duckdb.connect(); con.execute("SET memory_limit='8GB'"); con.execute("SET threads=6")
T = con.execute(f"""
    SELECT t.*, s.px AS raw_px, {", ".join(f"s.{c}" for c in SLICE_COLS)}
    FROM 'data/flushfader_gate_review/broad_reference_trips.parquet' t
    LEFT JOIN 'data/flushfader_review_slice.parquet' s
      ON s.symbol = t.symbol AND s.trade_date::VARCHAR = t.trade_date AND s.signal_sec = t.signal_sec
    ORDER BY t.symbol, t.trade_date, t.signal_sec""").df()
assert T.dslo.notna().all() and T.raw_px.notna().all()
T["r"] = (T.ret_exit + 2 * args.credit / T.entry_px) * 100
T["yr"] = T.trade_date.str[:4].astype(int)

def book_mask(drop):
    keep = np.zeros(len(T), bool); ok = (T.entry_px.values >= 1.0) & ~drop
    sym, dt, ent, ext = T.symbol.values, T.trade_date.values, T.entry_sec.values, T.exit_sec.values
    last, last_ext = None, -1
    for i in range(len(T)):
        k = (sym[i], dt[i])
        if k != last: last, last_ext = k, -1
        if ok[i] and ent[i] >= last_ext: keep[i] = True; last_ext = ext[i]
    return keep
def pf1(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l - 1

d = T.dslo.values
gate = lambda lo, hi: (d > lo) & (d <= hi)

# ---- 1. the V2 book
keep = book_mask(gate(args.lo, args.hi)); B = T[keep].copy()
B["gap_band"] = np.digitize(B.gap_60.values, [1, 4])
B["volat_band"] = np.digitize(B.volat.values, [60, 90, 140, 250])
B["rate600_fast"] = (np.nan_to_num(B.rate600.values, nan=0.0) >= 0.07).astype(int)
B["s_tier"] = ((B.ht.values >= 1) & (B.ssh.values >= 300) & (B.ssh.values < 2400)).astype(int)
B = B.drop(columns=["r", "yr"]).sort_values(["trade_date", "symbol", "signal_sec"]).reset_index(drop=True)
B.to_parquet(args.out, index=False)
r = (B.ret_exit + 2 * args.credit / B.entry_px).values * 100
print(f"# V2-7 — the V2 book: drop dslo ∈ ({args.lo*100:g} %, {args.hi*100:g} %], replayed\n")
print(f"{len(B):,} trades, flat PF−1 {pf1(r):.3f}, net {r.sum():,.0f} % -> {args.out}\n")

# ---- 2. the gate's cutoffs, held out: pick (lo, hi) on one block by flat net, apply to the other
LOS = [0.0, 0.0005, 0.001, 0.002, 0.003, 0.005, 0.0075, 0.01]
HIS = [0.01, 0.015, 0.02, 0.025, 0.03, 0.04]
base_keep = book_mask(np.zeros(len(T), bool))
Y = T.yr.values; R = T.r.values
res = {}
for lo in LOS:
    for hi in HIS:
        if hi <= lo: continue
        k = book_mask(gate(lo, hi))
        res[(lo, hi)] = {blk: (R[k & m].sum(), pf1(R[k & m])) for blk, m in (("early", Y <= 2023), ("late", Y > 2023))}
print("## The gate's cutoffs held out (flat net % / PF−1 on each block; base = no gate)\n")
print("| pick on | chosen (lo, hi] | fit-block net gain | apply block | apply net gain | apply PF−1 (base → gated) |")
print("|---|---|---|---|---|---|")
for fit, app in (("early", "late"), ("late", "early")):
    m_f = (Y <= 2023) if fit == "early" else (Y > 2023); m_a = ~m_f
    b_f, b_a = R[base_keep & m_f].sum(), R[base_keep & m_a].sum()
    (lo, hi), v = max(res.items(), key=lambda kv: kv[1][fit][0])
    print(f"| {'2020–23' if fit == 'early' else '2024–26'} | ({lo*100:g} %, {hi*100:g} %] | {v[fit][0] - b_f:+,.0f} | "
          f"{'2024–26' if app == 'late' else '2020–23'} | {v[app][0] - b_a:+,.0f} | {pf1(R[base_keep & m_a]):.3f} → {v[app][1]:.3f} |")
print("\nThe whole grid, net gain per block (early / late):\n")
print("| lo \\ hi | " + " | ".join(f"{h*100:g} %" for h in HIS) + " |\n|---|" + "---|" * len(HIS))
be, bl_ = R[base_keep & (Y <= 2023)].sum(), R[base_keep & (Y > 2023)].sum()
for lo in LOS:
    cells = []
    for hi in HIS:
        cells.append("—" if (lo, hi) not in res else f"{res[(lo, hi)]['early'][0] - be:+.0f} / {res[(lo, hi)]['late'][0] - bl_:+.0f}")
    print(f"| {lo*100:g} % | " + " | ".join(cells) + " |")
