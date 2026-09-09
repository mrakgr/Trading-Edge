"""S49aj — THE BROAD BOOK's reference artifacts for the Scanner parity harness (2026-09-09).

Rebuilds the production book from a research BASE corpus (every spec gate OFF) in FULL DOUBLE precision — the review
slice stores consol_5m_lag1m / rate600 as FLOAT32, which is a boundary hazard the Scanner must not inherit — and sizes
it with the A3 coefficients the Scanner embeds (`a3_coefficients.json`, written by flushfader_sizing_model.py
--dump-coefficients). Two parquets:

  broad_reference_trips.parquet   every trip passing the ENGINE-level predicates (the Scanner's signals.parquet under
                                  defaultConfig must equal this set exactly — keys (symbol, trade_date, signal_sec))
  broad_reference_book.parquet    + raw entry_px >= $1 + per-ticker-day mc=1 greedy (signal order, entry_sec >= last
                                  kept exit_sec) + the A3 multiplier and its four sizing features

The mask is the one in flushfader_sizing_model.py (S49ae) with ONE deliberate difference: the entry window is
CALENDAR-AWARE (signal_sec <= 43200 on NYSE early-close days, 54000 otherwise) because that is what the engine does
(S43bc/S43cr-b: the flat 54000 in the scripts admits a 3-trip half-day class the engine refuses).

Run from research/:
  python -u scripts/equity/flushfader_broad_reference.py                       # base_v19, full period
  python -u scripts/equity/flushfader_broad_reference.py --trips 'data/equity/flushfader/base_10d/*.parquet' --tag 10d
"""
import argparse, json, os, time
import numpy as np, pandas as pd, duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--trips", default="data/equity/flushfader/base_v19/*.parquet")
ap.add_argument("--coef", default="data/flushfader_gate_review/a3_coefficients.json")
ap.add_argument("--out-dir", default="data/flushfader_gate_review")
ap.add_argument("--tag", default="", help="suffix for the output names (e.g. 10d)")
ap.add_argument("--door", type=int, default=40)
ap.add_argument("--slice", default="data/flushfader_review_slice.parquet", help="cross-check against the FLOAT32 slice book (S49 scripts); '' to skip")
ap.add_argument("--mem", default="6GB")
args = ap.parse_args()
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:6.1f}s]", *a, flush=True)
sfx = f"_{args.tag}" if args.tag else ""

# NYSE early closes (research/TradingEdge.Orb/Timezone.fs `early_closes`) — the engine's 12:00 entry cutoff applies on these
EARLY = {"2016-11-25", "2017-07-03", "2017-11-24", "2018-07-03", "2018-11-23", "2018-12-24", "2019-07-03", "2019-11-29", "2019-12-24",
         "2020-11-27", "2020-12-24", "2021-11-26", "2022-11-25", "2023-07-03", "2023-11-24", "2024-07-03", "2024-11-29", "2024-12-24",
         "2025-07-03", "2025-11-28", "2025-12-24", "2026-11-27", "2026-12-24"}

con = duckdb.connect(); con.execute(f"SET memory_limit='{args.mem}'"); con.execute("SET threads=6")
# integer pre-filters only (no NaN semantics in SQL); every float predicate is applied in numpy with the nz() convention
D = con.execute(f"""
    SELECT symbol, trade_date::VARCHAR AS trade_date, signal_sec, entry_sec, exit_sec, entry_px, exit_px, ret_exit, signal_vwap,
           volat_20m * 1e4 AS volat, gap_60, halts_today AS ht, secs_since_halt AS ssh,
           chg_since_run_first_low AS crf, consol_5m_lag1m, lows_since_first_low_600 AS l600, bars_since_first_low_600 AS b600,
           lows_since_first_low_300 AS l300, lows_since_first_low_180 AS l180, abs(eff_10m) AS eff10a,
           vol_10_bar, vol_60_bar, vol_1200_bar, dlv_1200, dlv2_1200, dv_0945_tape
    FROM read_parquet('{args.trips}')
    WHERE gap_60 < {args.door} AND lows_since_first_low_300 >= 6 AND lows_since_first_low_180 >= 3
      AND signal_sec <= 54000
    ORDER BY symbol, trade_date, signal_sec""").df()
log(f"{len(D):,} rows after the integer pre-filters (gap < {args.door}, l300 >= 6, l180 >= 3, signal <= 15:00)")

def nz(m): return np.where(np.isnan(m.astype(float)), False, m).astype(bool)
with np.errstate(divide="ignore", invalid="ignore"):
    v10r = (D.vol_10_bar.values / 10.0) / (D.vol_60_bar.values / 60.0)
    mean = D.dlv_1200.values / D.vol_1200_bar.values
    var = D.dlv2_1200.values / D.vol_1200_bar.values - mean * mean
    z20 = np.where(var > 0, (np.log(D.signal_vwap.values) - mean) / np.sqrt(np.where(var > 0, var, 1.0)), np.nan)
    rate600 = D.l600.values / np.where(D.b600.values > 0, D.b600.values, np.nan).astype(float)
early = D.trade_date.isin(EARLY).values
window = np.where(early, D.signal_sec.values <= 43200, D.signal_sec.values <= 54000)
ret_ok = ~np.isnan(D.ret_exit.values.astype(float))
M = (ret_ok & window & nz(D.volat.values >= 40)
     & nz(D.eff10a.values >= 0.15) & nz(v10r >= 0.75) & nz(z20 < -1.5)
     & nz(D.crf.values <= -0.002) & nz(D.consol_5m_lag1m.values <= 0.22))
M &= ~(nz(D.volat.values >= 140) & nz(D.gap_60.values >= 4))            # rule140
M &= ~(nz(D.ht.values >= 4) & nz(D.ssh.values < 300))                    # the WAIT (S49p)
T = D[M].copy(); T["rate600"] = rate600[M]; T["z20"] = z20[M]; T["v10r"] = v10r[M]
log(f"engine-level trips: {len(T):,} on {T.groupby(['symbol','trade_date']).ngroups:,} ticker-days ({early[M].sum():,} on early-close days)")

# ---- the book: raw $1 floor + per-ticker-day mc=1 greedy in SIGNAL order
px_ok = nz(T.entry_px.values >= 1.0)
keep = np.zeros(len(T), bool)
sym = T.symbol.values; dt = T.trade_date.values; ent = T.entry_sec.values; ext = T.exit_sec.values
last_key = None; last_ext = -1
for i in range(len(T)):
    key = (sym[i], dt[i])
    if key != last_key: last_key = key; last_ext = -1
    if px_ok[i] and ent[i] >= last_ext:
        keep[i] = True; last_ext = ext[i]
B = T[keep].copy()

# ---- A3 sizing from the embedded coefficients (the exact pipeline the Scanner's Sizing.fs reproduces)
C = json.load(open(args.coef))
bp, bw, bl = np.array(C["beta_p"]), np.array(C["beta_w"]), np.array(C["beta_l"]); base = C["base_pf1"]; lo, hi = C["clip"]
gap_band = np.digitize(B.gap_60.values, [1, 4])                       # 0: gap 0 | 1: 1-3 | 2: 4-39
volat_band = np.digitize(B.volat.values, [60, 90, 140, 250])         # 0..4
fast = (np.nan_to_num(B.rate600.values, nan=0.0) >= C["rate600_fast_threshold"]).astype(int)
tier = (nz(B.ht.values >= 1) & nz(B.ssh.values >= 300) & nz(B.ssh.values < 2400)).astype(int)
X = np.column_stack([np.ones(len(B)), gap_band == 1, gap_band == 2] + [volat_band == k for k in range(1, 5)] + [fast, tier]).astype(float)
p = 1 / (1 + np.exp(-(X @ bp))); W = np.exp(X @ bw); L = np.exp(X @ bl); pf1 = p / ((1 - p) * L / W) - 1
mult = np.clip(pf1 / base, lo, hi)
B["gap_band"] = gap_band; B["volat_band"] = volat_band; B["rate600_fast"] = fast; B["s_tier"] = tier
B["pf1_fitted"] = pf1; B["multiplier"] = mult
# every book trade must land on a table cell with the same multiplier
cell = {(c["gap_band"], c["volat_band"], c["rate600_fast"], c["s_tier"]): c["multiplier"] for c in C["cells"]}
tab = np.array([cell[(g, v, r, t)] for g, v, r, t in zip(gap_band, volat_band, fast, tier)])
log(f"book: {len(B):,} trades; multiplier mean {mult.mean():.4f} range {mult.min():.3f}–{mult.max():.3f}; max |mult − table cell| = {np.abs(mult - tab).max():.2e}")

def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
R = B.ret_exit.values * 100
rows = [f"# S49aj — broad-book reference from `{args.trips}` (full double precision), A3 from `{args.coef}`\n",
        "| set | n | ticker-days | PF gross | net % | mean mult |", "|---|---|---|---|---|---|",
        f"| engine-level trips (no $1 floor, no replay) | {len(T):,} | {T.groupby(['symbol','trade_date']).ngroups:,} | {pf(T.ret_exit.values*100):.3f} | {(T.ret_exit.values*100).sum():,.0f} | |",
        f"| book (px >= 1, mc=1 per ticker-day) | {len(B):,} | {B.groupby(['symbol','trade_date']).ngroups:,} | {pf(R):.3f} | {R.sum():,.0f} | {mult.mean():.4f} |",
        f"| book, sized ∝ A3 | {len(B):,} | | {pf(R*mult):.3f} | {(R*mult).sum():,.0f} | |", ""]
rows.append("| multiplier cells populated | " + ", ".join(f"{k}" for k in sorted(set(zip(gap_band, volat_band, fast, tier)))) + " |")

# ---- cross-check vs the FLOAT32 slice (what the S49 scripts sized): the boundary class
if args.slice and os.path.exists(args.slice) and not args.tag:
    S = pd.read_parquet(args.slice, columns=["symbol", "trade_date", "signal_sec", "ent", "ext", "volat", "px", "gap60", "l180", "l300", "eff10a", "z20", "v10r", "crf", "consol_5m_lag1m", "rate600", "ht", "ssh"])
    Ms = (nz(S.volat.values >= 40) & nz(S.signal_sec.values <= 54000) & nz(S.gap60.values < args.door) & nz(S.l300.values >= 6) & nz(S.eff10a.values >= 0.15)
          & nz(S.v10r.values >= 0.75) & nz(S.z20.values < -1.5) & nz(S.l180.values >= 3) & nz(S.crf.values <= -0.002) & nz(S.consol_5m_lag1m.values <= 0.22))
    Ms &= ~(nz(S.volat.values >= 140) & nz(S.gap60.values >= 4)); Ms &= ~(nz(S.ht.values >= 4) & nz(S.ssh.values < 300))
    ks = set(zip(S.symbol.values[Ms], S.trade_date.values[Ms], S.signal_sec.values[Ms])); kt = set(zip(T.symbol.values, T.trade_date.values, T.signal_sec.values))
    only_s = sorted(ks - kt); only_t = sorted(kt - ks)
    rows += ["", f"cross-check vs the FLOAT32 slice mask (flat 15:00 cutoff): slice {len(ks):,} vs double {len(kt):,}; slice-only {len(only_s)}, double-only {len(only_t)}"]
    Sk = S.set_index(["symbol", "trade_date", "signal_sec"])
    for lab, keys in [("slice-only", only_s), ("double-only", only_t)]:
        for k in keys[:40]:
            r = Sk.loc[k] if k in Sk.index else None
            why = "early-close 12:00 cutoff" if (k[1] in EARLY and k[2] > 43200) else "float32 boundary"
            rows.append(f"- {lab} {k[0]} {k[1]} {k[2]}: {why}" + (f" (slice consol {r.consol_5m_lag1m:.9f}, rate600 {r.rate600}, volat {r.volat:.6f})" if r is not None else ""))
    log(f"slice cross-check: slice-only {len(only_s)}, double-only {len(only_t)}")

os.makedirs(args.out_dir, exist_ok=True)
tp = os.path.join(args.out_dir, f"broad_reference_trips{sfx}.parquet"); bkp = os.path.join(args.out_dir, f"broad_reference_book{sfx}.parquet")
T.drop(columns=["vol_10_bar", "vol_60_bar", "vol_1200_bar", "dlv_1200", "dlv2_1200"]).to_parquet(tp, index=False)
B.drop(columns=["vol_10_bar", "vol_60_bar", "vol_1200_bar", "dlv_1200", "dlv2_1200"]).to_parquet(bkp, index=False)
txt = "\n".join(rows) + "\n"; mdp = os.path.join(args.out_dir, f"broad_reference{sfx}.md"); open(mdp, "w").write(txt); print(txt)
log(f"wrote {tp} ({len(T):,}), {bkp} ({len(B):,}), {mdp}")
