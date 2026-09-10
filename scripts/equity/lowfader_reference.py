"""LowFader SPEC v4 reference artifacts for the Scanner parity harness (2026-09-10; docs/lowfader_results.md §L23,
docs/production_specs.md §5).

The ratified corpus `data/lowfader_wl_wide/` was run with --base-run, so the spec is exactly the engine's `ordPass`
(session-low signal + floors + volat >= 30 bp + the 11 Ord gates) and a trip passed it iff `spec_ord > 0`. Two parquets:

  lowfader_reference_trips.parquet   every `spec_ord > 0` trip (the Scanner LowFader engine, a sampler gated on ordPass,
                                     must emit exactly this set — keys (symbol, trade_date, signal_sec))
  lowfader_reference_book.parquet    the FIRST qualifying signal per ticker-day (mc = 1, no averaging down; user 2026-09-10:
                                     NO price floor — the 221 stand) + the A/rest grade and weight
                                     (A = volat_20m > 0.005 ∧ gap_adj_60 <= 30 ∧ (rr >= 5 ∨ lows_rr3_120 >= 20) -> 1.00, rest 0.30)

Run from research/:  python -u scripts/equity/lowfader_reference.py [--trips 'data/lowfader_wl_wide/*.parquet'] [--tag x]
"""
import argparse, os, time
import numpy as np, pandas as pd, duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--trips", default="data/lowfader_wl_wide/*.parquet")
ap.add_argument("--out-dir", default="data/flushfader_gate_review")
ap.add_argument("--tag", default="")
args = ap.parse_args()
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:6.1f}s]", *a, flush=True)
sfx = f"_{args.tag}" if args.tag else ""
con = duckdb.connect(); con.execute("SET memory_limit='4GB'")
T = con.execute(f"""
    SELECT symbol, trade_date::VARCHAR AS trade_date, signal_sec, entry_sec, exit_sec, entry_px, exit_px, exit_reason, ret_exit, signal_vwap,
           volat_20m, eff_ewma_10m, lows_since_first_low_600, bars_since_first_low_600,
           lows_since_first_low_600::DOUBLE / NULLIF(bars_since_first_low_600, 0) AS rate600,
           vol_60, vol_0945_tape, vol_60 / NULLIF(vol_0945_tape / 15.0, 0) AS rr,
           close_m1, div_m1, signal_vwap / NULLIF(close_m1 + coalesce(div_m1, 0), 0) - 1 AS chg1d,
           gap_adj_60, dollar_vol_60, lows_since_first_low_120, chg_since_run_first_low AS crf, dv_0945_tape, lows_rr3_120,
           halts_today, secs_since_halt, spec_ord
    FROM read_parquet('{args.trips}') WHERE spec_ord > 0
    ORDER BY symbol, trade_date, signal_sec""").df()
log(f"engine-level trips (spec_ord > 0): {len(T):,} on {T.groupby(['symbol','trade_date']).ngroups:,} ticker-days; exit reasons {T.exit_reason.value_counts().to_dict()}")
B = T.groupby(["symbol", "trade_date"], sort=False).head(1).copy()
strict = (B.volat_20m.values > 0.005) & (B.gap_adj_60.values <= 30)
x = (np.nan_to_num(B.rr.values, nan=-1.0) >= 5.0) | (B.lows_rr3_120.values >= 20)
B["grade_a"] = (strict & x).astype(int); B["weight"] = np.where(strict & x, 1.0, 0.3)
def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
R = B.ret_exit.values * 100; W = B.weight.values
rows = [f"# LowFader SPEC v4 reference from `{args.trips}`\n",
        "| set | n | ticker-days | PF | net % | worst % |", "|---|---|---|---|---|---|",
        f"| engine-level trips (spec_ord > 0) | {len(T):,} | {T.groupby(['symbol','trade_date']).ngroups:,} | {pf(T.ret_exit.values*100):.3f} | {(T.ret_exit.values*100).sum():,.0f} | {(T.ret_exit.values*100).min() if len(T) else float('nan'):.1f} |",
        f"| book (first per ticker-day, no floor) | {len(B):,} | {len(B):,} | {pf(R):.3f} | {R.sum():,.0f} | {R.min() if len(B) else float('nan'):.1f} |",
        f"| grade A (1.00) | {int(B.grade_a.sum()):,} | | {pf(R[B.grade_a.values==1]):.3f} | {R[B.grade_a.values==1].sum():,.0f} | |",
        f"| rest (0.30) | {int((B.grade_a==0).sum()):,} | | {pf(R[B.grade_a.values==0]):.3f} | {R[B.grade_a.values==0].sum():,.0f} | |",
        f"| book sized (A 1.00 / rest 0.30) | {len(B):,} | | {pf(R*W):.3f} | {(R*W).sum():,.0f} | {(R*W).min() if len(B) else float('nan'):.1f} |",
        "", "| year | n | PF | net % |", "|---|---|---|---|"]
yr = pd.to_datetime(B.trade_date).dt.year.values
for y in sorted(set(yr)):
    m = yr == y; rows.append(f"| {y} | {m.sum()} | {pf(R[m]):.2f} | {R[m].sum():+.0f} |")
os.makedirs(args.out_dir, exist_ok=True)
tp = os.path.join(args.out_dir, f"lowfader_reference_trips{sfx}.parquet"); bp = os.path.join(args.out_dir, f"lowfader_reference_book{sfx}.parquet")
T.to_parquet(tp, index=False); B.to_parquet(bp, index=False)
txt = "\n".join(rows) + "\n"; mp = os.path.join(args.out_dir, f"lowfader_reference{sfx}.md"); open(mp, "w").write(txt); print(txt)
log(f"wrote {tp} ({len(T):,}), {bp} ({len(B):,}), {mp}")
