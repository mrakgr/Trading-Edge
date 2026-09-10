"""SpikeFader S47 reference artifacts for the Scanner parity harness (2026-09-10; docs/spikefader_results.md §S47/§S49,
docs/production_specs.md §2).

The corpus `data/spikefader_s47/` carries only three spec predicates in the engine (volat >= 40 bp, be6030 speed > 2%,
eff_10m >= 0.3); the rest is the `SPEC` SQL of scripts/analysis/spikefader_zq.py applied post-hoc WITH the two rewrites
scripts/analysis/spikefader_s47_exits.py makes (`dlv` -> signal_vwap / sess_low - 1; ols_slope_1200 >= 5.0e-05, the
per-bar unit of the doc's 30 bp/min). Two parquets:

  spikefader_reference_trips.parquet   every trip passing the full SPEC (the Scanner SpikeFader engine, gated on all of it,
                                       must emit exactly this set — keys (symbol, trade_date, signal_sec))
  spikefader_reference_book.parquet    + the per-ticker-day mc=1 greedy replay in signal order (signal_sec >= the last kept
                                       exit_sec) + the ROSTER v3.4 weight (overwrite cascade E->D->C->X->B->A, the LAST
                                       firing voice wins — NOT max weight: C 0.75 loses to X 0.74)

ret_exit in the corpus already carries the SHORT sign (-(exit/entry - 1)); the book's 540-bar exit IS the engine's exit.
Run from research/:  python -u scripts/equity/spikefader_reference.py [--trips 'data/spikefader_s47/*.parquet'] [--tag x]
"""
import argparse, os, time
import numpy as np, pandas as pd, duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--trips", default="data/spikefader_s47/*.parquet")
ap.add_argument("--out-dir", default="data/flushfader_gate_review")
ap.add_argument("--tag", default="")
ap.add_argument("--mem", default="6GB")
args = ap.parse_args()
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:6.1f}s]", *a, flush=True)
sfx = f"_{args.tag}" if args.tag else ""
con = duckdb.connect(); con.execute(f"SET memory_limit='{args.mem}'"); con.execute("SET threads=6")
# integer / non-NaN predicates in SQL; the float ones in numpy with the nz() convention (DuckDB orders NaN as +inf)
D = con.execute(f"""
    SELECT symbol, trade_date::VARCHAR AS trade_date, signal_sec, entry_sec, exit_sec, entry_px, exit_px, exit_reason, ret_exit, signal_vwap,
           volat_20m, vwap_ewp_6030_be, eff_10m, highs_since_first_high_300, highs_since_first_high_600, highs_since_first_high_180,
           gap_adj_60, sess_low, sess_high, ols_slope_300, ols_slope_1200, ac1_ewma, vol_60, vol_0945_tape, halts_today, secs_since_halt
    FROM read_parquet('{args.trips}')
    WHERE highs_since_first_high_300 >= 40 AND highs_since_first_high_600 >= 90 AND highs_since_first_high_180 >= 15
      AND gap_adj_60 < 10 AND signal_sec < 55800
    ORDER BY symbol, trade_date, signal_sec""").df()
log(f"{len(D):,} rows after the integer pre-filters")
def nz(m): return np.where(np.isnan(m.astype(float)), False, m).astype(bool)
with np.errstate(divide="ignore", invalid="ignore"):
    be6030 = D.signal_vwap.values / D.vwap_ewp_6030_be.values - 1
    dlv = D.signal_vwap.values / np.where(D.sess_low.values != 0, D.sess_low.values, np.nan) - 1
    dslo = D.signal_vwap.values / np.where(D.sess_high.values != 0, D.sess_high.values, np.nan) - 1
    rr = D.vol_60.values / np.where(D.vol_0945_tape.values * 60.0 / 900.0 != 0, D.vol_0945_tape.values * 60.0 / 900.0, np.nan)
M = (nz(D.volat_20m.values >= 0.0040) & nz(be6030 > 0.02) & nz(D.eff_10m.values >= 0.3) & nz(dlv > 0.03)
     & nz(D.ols_slope_300.values >= 0) & nz(D.ols_slope_1200.values >= 5.0e-05) & nz(D.ac1_ewma.values >= -0.1)
     & nz(D.signal_vwap.values >= 1.0) & ~np.isnan(D.ret_exit.values.astype(float)))
T = D[M].copy(); T["be6030"] = be6030[M]; T["dlv"] = dlv[M]; T["dslo"] = dslo[M]; T["rr"] = rr[M]
log(f"SPEC trips: {len(T):,} on {T.groupby(['symbol','trade_date']).ngroups:,} ticker-days; exits {T.exit_reason.value_counts().to_dict()}")
# mc=1 per ticker-day, signal order, signal_sec >= busy
keep = np.zeros(len(T), bool); sym = T.symbol.values; dt = T.trade_date.values; ss = T.signal_sec.values; es = T.exit_sec.values
prev = None; busy = -1
for i in range(len(T)):
    k = (sym[i], dt[i])
    if k != prev: busy = -1; prev = k
    if ss[i] >= busy: keep[i] = True; busy = es[i]
B = T[keep].copy()
A = B.rr.values < 0.5; Bv = B.dslo.values <= -0.05; X = B.rr.values >= 12
C = (B.halts_today.values >= 2) & (B.secs_since_halt.values >= 60) & (B.secs_since_halt.values < 300)
Dv = B.volat_20m.values >= 0.01
w = np.full(len(B), 0.19); w = np.where(Dv, 0.48, w); w = np.where(C, 0.75, w); w = np.where(X, 0.74, w); w = np.where(Bv, 0.99, w); w = np.where(A, 1.00, w)
grade = np.full(len(B), "E", dtype=object); grade[Dv] = "D"; grade[C] = "C"; grade[X] = "X"; grade[Bv] = "B"; grade[A] = "A"
B["grade"] = grade; B["weight"] = w
def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
R = B.ret_exit.values * 100
rows = [f"# SpikeFader S47 reference from `{args.trips}` (SPEC with the s47 rewrites; returns SHORT-signed)\n",
        "| set | n | ticker-days | PF | net % | worst % |", "|---|---|---|---|---|---|",
        f"| SPEC trips (no replay) | {len(T):,} | {T.groupby(['symbol','trade_date']).ngroups:,} | {pf(T.ret_exit.values*100):.3f} | {(T.ret_exit.values*100).sum():,.0f} | {(T.ret_exit.values*100).min():.1f} |",
        f"| book (mc=1 per ticker-day) | {len(B):,} | {B.groupby(['symbol','trade_date']).ngroups:,} | {pf(R):.3f} | {R.sum():,.0f} | {R.min():.1f} |",
        f"| book sized (roster v3.4) | {len(B):,} | | {pf(R*w):.3f} | {(R*w).sum():,.0f} | {(R*w).min():.1f} |",
        "", "| grade | weight | n | PF |", "|---|---|---|---|"]
for g_, wt in [("A", 1.0), ("B", 0.99), ("X", 0.74), ("C", 0.75), ("D", 0.48), ("E", 0.19)]:
    m = grade == g_; rows.append(f"| {g_} | {wt} | {m.sum():,} | {pf(R[m]) if m.sum() else float('nan'):.3f} |")
rows += ["", "| year | n | PF | net % |", "|---|---|---|---|"]
yr = pd.to_datetime(B.trade_date).dt.year.values
for y in sorted(set(yr)):
    m = yr == y; rows.append(f"| {y} | {m.sum()} | {pf(R[m]):.2f} | {R[m].sum():+.0f} |")
os.makedirs(args.out_dir, exist_ok=True)
tp = os.path.join(args.out_dir, f"spikefader_reference_trips{sfx}.parquet"); bp = os.path.join(args.out_dir, f"spikefader_reference_book{sfx}.parquet")
T.to_parquet(tp, index=False); B.to_parquet(bp, index=False)
txt = "\n".join(rows) + "\n"; mp = os.path.join(args.out_dir, f"spikefader_reference{sfx}.md"); open(mp, "w").write(txt); print(txt)
log(f"wrote {tp} ({len(T):,}), {bp} ({len(B):,}), {mp}")
