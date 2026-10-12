"""FlushFaderV2 — the production reference (2026-10-12): the V2 book + the V2 sizing coefficients, in FULL DOUBLE
precision from a research BASE corpus (every spec gate OFF), for the Oms port and its Parity seal.

The V2 study (docs/flushfaderv2_results.md V2-1..V2-8) read its inputs from the FLOAT32 review slice; production must
not inherit that. Here every input comes from the base parquets in double:
    dslo = signal_vwap / sess_low − 1          (sess_low = the running session minimum, the signal bar included)
    s20  = ols_slope_1200 × 6e5                (bp/min; full 1200-bar window only, NaN otherwise)

  V2 = the broad book (flushfader_broad_reference.py's mask, calendar-aware entry window) − dslo ∈ (0, 2 %]
       (a NaN dslo is NOT gated — sess_low is never NaN at a signal), then raw px ≥ $1 + mc=1 greedy per ticker-day.
  sizing = A3 design + [dslo ≥ 4 %] + standardised s20:
       z = (clip(s20, S20Lo, S20Hi) − S20Mean) / S20Sd     (the fit set's 1 % / 99 % quantiles, mean, SD)
       a NaN s20 enters as z = 0 (the mean) — never seen on the book, a live guard
       multiplier = clip(PF−1(x) / BasePf1, 0.25, 4)

Outputs (out-dir, + _TAG):
  flushfaderv2_reference_trips.parquet   every trip passing the ENGINE-level predicates, V2 gate included (the Oms's
                                         signals under its default config must equal this set; keys symbol,
                                         trade_date, signal_sec) + dslo / s20
  flushfaderv2_reference_book.parquet    + raw px ≥ $1 + mc=1 greedy + the sizing features + multiplier
  v2_coefficients.json                   (fit mode only) the β vectors, BasePf1, the s20 constants, the thresholds,
                                         and a PIN TABLE of 200 book trades' (inputs → multiplier) for the F# tests

Run from research/:
  python -u scripts/equity/flushfaderv2_reference.py                                    # fit on the full period
  python -u scripts/equity/flushfaderv2_reference.py --trips 'data/equity/flushfader/base_10d/*.parquet' --tag 10d \
      --coef data/flushfaderv2/v2_coefficients.json                                     # apply the full-period fit
"""
import argparse, json, os, time
import numpy as np, pandas as pd, duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--trips", default="data/equity/flushfader/base_v19/*.parquet")
ap.add_argument("--coef", default="", help="APPLY these coefficients instead of fitting (the 10-day seal set)")
ap.add_argument("--out-dir", default="data/flushfaderv2")
ap.add_argument("--tag", default="")
ap.add_argument("--door", type=int, default=40)
ap.add_argument("--credit", type=float, default=0.001)
ap.add_argument("--mem", default="8GB")
args = ap.parse_args()
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:6.1f}s]", *a, flush=True)
sfx = f"_{args.tag}" if args.tag else ""

# the gate and the flag (V2-6 / V2-7, user 2026-10-12)
NEAR_LOW_LO, NEAR_LOW_HI = 0.0, 0.02        # drop dslo in (LO, HI]
DSLO_FLAG = 0.04                            # sizing flag dslo >= this
S20_SCALE = 6e5                             # ols_slope_1200 (ln/bar) -> bp/min
EARLY = {"2016-11-25", "2017-07-03", "2017-11-24", "2018-07-03", "2018-11-23", "2018-12-24", "2019-07-03", "2019-11-29", "2019-12-24",
         "2020-11-27", "2020-12-24", "2021-11-26", "2022-11-25", "2023-07-03", "2023-11-24", "2024-07-03", "2024-11-29", "2024-12-24",
         "2025-07-03", "2025-11-28", "2025-12-24", "2026-11-27", "2026-12-24"}

con = duckdb.connect(); con.execute(f"SET memory_limit='{args.mem}'"); con.execute("SET threads=6")
D = con.execute(f"""
    SELECT symbol, trade_date::VARCHAR AS trade_date, signal_sec, entry_sec, exit_sec, entry_px, exit_px, ret_exit, signal_vwap,
           volat_20m * 1e4 AS volat, gap_60, halts_today AS ht, secs_since_halt AS ssh,
           chg_since_run_first_low AS crf, consol_5m_lag1m, lows_since_first_low_600 AS l600, bars_since_first_low_600 AS b600,
           lows_since_first_low_300 AS l300, lows_since_first_low_180 AS l180, abs(eff_10m) AS eff10a,
           vol_10_bar, vol_60_bar, vol_1200_bar, dlv_1200, dlv2_1200, dv_0945_tape, sess_low, ols_slope_1200
    FROM read_parquet('{args.trips}')
    WHERE gap_60 < {args.door} AND lows_since_first_low_300 >= 6 AND lows_since_first_low_180 >= 3
      AND signal_sec <= 54000
    ORDER BY symbol, trade_date, signal_sec""").df()
log(f"{len(D):,} rows after the integer pre-filters")

def nz(m): return np.where(np.isnan(m.astype(float)), False, m).astype(bool)
with np.errstate(divide="ignore", invalid="ignore"):
    v10r = (D.vol_10_bar.values / 10.0) / (D.vol_60_bar.values / 60.0)
    mean = D.dlv_1200.values / D.vol_1200_bar.values
    var = D.dlv2_1200.values / D.vol_1200_bar.values - mean * mean
    z20 = np.where(var > 0, (np.log(D.signal_vwap.values) - mean) / np.sqrt(np.where(var > 0, var, 1.0)), np.nan)
    rate600 = D.l600.values / np.where(D.b600.values > 0, D.b600.values, np.nan).astype(float)
    dslo = D.signal_vwap.values / D.sess_low.values - 1.0
s20 = D.ols_slope_1200.values * S20_SCALE
early = D.trade_date.isin(EARLY).values
window = np.where(early, D.signal_sec.values <= 43200, D.signal_sec.values <= 54000)
ret_ok = ~np.isnan(D.ret_exit.values.astype(float))
M = (ret_ok & window & nz(D.volat.values >= 40)
     & nz(D.eff10a.values >= 0.15) & nz(v10r >= 0.75) & nz(z20 < -1.5)
     & nz(D.crf.values <= -0.002) & nz(D.consol_5m_lag1m.values <= 0.22))
M &= ~(nz(D.volat.values >= 140) & nz(D.gap_60.values >= 4))            # rule140
M &= ~(nz(D.ht.values >= 4) & nz(D.ssh.values < 300))                    # the WAIT
n_broad = int(M.sum())
M &= ~(nz(dslo > NEAR_LOW_LO) & nz(dslo <= NEAR_LOW_HI))                 # ⭐ V2: the near-low gate
T = D[M].copy(); T["rate600"] = rate600[M]; T["z20"] = z20[M]; T["v10r"] = v10r[M]; T["dslo"] = dslo[M]; T["s20"] = s20[M]
log(f"engine-level trips: broad {n_broad:,} -> V2 {len(T):,} (near-low gate removes {n_broad - len(T):,}); NaN dslo {np.isnan(T.dslo.values).sum()}, NaN s20 {np.isnan(T.s20.values).sum()}")

# ---- the book: raw $1 floor + per-ticker-day mc=1 greedy in signal order
px_ok = nz(T.entry_px.values >= 1.0)
keep = np.zeros(len(T), bool)
sym = T.symbol.values; dt = T.trade_date.values; ent = T.entry_sec.values; ext = T.exit_sec.values
last_key = None; last_ext = -1
for i in range(len(T)):
    key = (sym[i], dt[i])
    if key != last_key: last_key = key; last_ext = -1
    if px_ok[i] and ent[i] >= last_ext: keep[i] = True; last_ext = ext[i]
B = T[keep].copy().reset_index(drop=True)
R = (B.ret_exit.values + 2 * args.credit / B.entry_px.values) * 100
log(f"book {len(B):,} trades" + ("" if args.tag else " (the slice study: 35,612)"))

# ---- the design
gap_band = np.digitize(B.gap_60.values, [1, 4]); volat_band = np.digitize(B.volat.values, [60, 90, 140, 250])
fast = (np.nan_to_num(B.rate600.values, nan=0.0) >= 0.07).astype(int)
tier = (nz(B.ht.values >= 1) & nz(B.ssh.values >= 300) & nz(B.ssh.values < 2400)).astype(int)
flag = nz(B.dslo.values >= DSLO_FLAG).astype(int)
def zs20(x, c):
    z = (np.clip(x, c["s20_lo"], c["s20_hi"]) - c["s20_mean"]) / c["s20_sd"]
    return np.where(np.isnan(x), 0.0, z)
def design(c):
    return np.column_stack([np.ones(len(B)), gap_band == 1, gap_band == 2] + [volat_band == k for k in range(1, 5)]
                           + [fast, tier, flag, zs20(B.s20.values, c)]).astype(float)
NAMES = ["1", "gap 1-3", "gap 4-39", "volat 60-90", "volat 90-140", "volat 140-250", "volat 250+", "rate600 fast", "S tier",
         "dslo >= 4%", "s20 (standardised)"]

def fit_logistic(X, y, it=50):
    b = np.zeros(X.shape[1])
    for _ in range(it):
        eta = X @ b; mu = 1 / (1 + np.exp(-eta)); W_ = mu * (1 - mu) + 1e-12
        z = eta + (y - mu) / (mu * (1 - mu) + 1e-12)
        b_new = np.linalg.solve((X * W_[:, None]).T @ X, (X * W_[:, None]).T @ z)
        if np.max(np.abs(b_new - b)) < 1e-9: b = b_new; break
        b = b_new
    return b
def fit_loglink(X, y, it=50):
    b = np.zeros(X.shape[1]); b[0] = np.log(y.mean())
    for _ in range(it):
        eta = X @ b; mu = np.exp(eta); z = eta + (y - mu) / mu
        b_new = np.linalg.solve((X * mu[:, None]).T @ X, (X * mu[:, None]).T @ z)
        if np.max(np.abs(b_new - b)) < 1e-9: b = b_new; break
        b = b_new
    return b
def pf1_of(X, c):
    """⚠ operation order = the F# port's (Sizing.fs): lam = L / W; pf1 = p / ((1 - p) * lam) - 1"""
    p = 1 / (1 + np.exp(-(X @ np.array(c["beta_p"])))); W = np.exp(X @ np.array(c["beta_w"])); L = np.exp(X @ np.array(c["beta_l"]))
    lam = L / W
    return p / ((1 - p) * lam) - 1

if args.coef:
    C = json.load(open(args.coef)); log(f"applying {args.coef}")
else:
    s = B.s20.values[~np.isnan(B.s20.values)]
    lo, hi = np.quantile(s, [0.01, 0.99]); zc = np.clip(s, lo, hi)
    C = {"s20_lo": float(lo), "s20_hi": float(hi), "s20_mean": float(zc.mean()), "s20_sd": float(zc.std())}
    X = design(C); win = R > 0
    C.update({"beta_p": [float(v) for v in fit_logistic(X, win.astype(float))],
              "beta_w": [float(v) for v in fit_loglink(X[win], R[win])],
              "beta_l": [float(v) for v in fit_loglink(X[~win], -R[~win])]})
    C["base_pf1"] = float(pf1_of(X, C).mean())
X = design(C); pf1 = pf1_of(X, C); mult = np.clip(pf1 / C["base_pf1"], 0.25, 4.0)
B["gap_band"] = gap_band; B["volat_band"] = volat_band; B["rate600_fast"] = fast; B["s_tier"] = tier; B["dslo_flag"] = flag
B["s20_z"] = X[:, 10]; B["pf1_fitted"] = pf1; B["multiplier"] = mult
log(f"multiplier mean {mult.mean():.4f}, range {mult.min():.3f}-{mult.max():.3f}, at the 0.25 floor {(mult <= 0.25).mean()*100:.1f}%")

def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
rows = [f"# FlushFaderV2 reference from `{args.trips}` (full double precision){' — coefficients ' + args.coef if args.coef else ''}\n",
        "| set | n | PF (net of credit) | net % |", "|---|---|---|---|",
        f"| engine-level trips (V2 gate in) | {len(T):,} | {pf((T.ret_exit.values + 2*args.credit/T.entry_px.values)*100):.3f} | |",
        f"| book (px ≥ 1, mc=1) | {len(B):,} | {pf(R):.3f} | {R.sum():,.0f} |",
        f"| book, sized ∝ V2 | {len(B):,} | {pf(R*mult):.3f} | {(R*mult).sum():,.0f} |", ""]
if not args.coef:
    bp, bw, bl = (np.array(C[k]) for k in ("beta_p", "beta_w", "beta_l"))
    rows += ["| term | β_p (odds ×) | β_W (×) | β_L (×) |", "|---|---|---|---|"]
    rows += [f"| {nm} | {bp[j]:+.6f} ({np.exp(bp[j]):.3f}) | {bw[j]:+.6f} ({np.exp(bw[j]):.3f}) | {bl[j]:+.6f} ({np.exp(bl[j]):.3f}) |" for j, nm in enumerate(NAMES)]
    rows += ["", f"s20: winsorise [{C['s20_lo']:.6f}, {C['s20_hi']:.6f}] bp/min, mean {C['s20_mean']:.6f}, SD {C['s20_sd']:.6f}; BasePf1 {C['base_pf1']:.10f}"]
    # the PIN TABLE: 200 book trades spread over the multiplier range, inputs at full precision
    idx = np.unique(np.argsort(mult, kind="stable")[np.linspace(0, len(B) - 1, 200).astype(int)])
    C.update({"model": "FlushFaderV2 (A3 + dslo >= 4% + s20), docs/flushfaderv2_results.md V2-7",
              "fit": f"all years, V2 book n={len(B):,}, credit ${args.credit}/sh/side, from {args.trips}",
              "design": NAMES, "gap_edges": [0, 1, 4, 40], "volat_edges_bp": [40, 60, 90, 140, 250], "rate600_fast_threshold": 0.07,
              "s_tier": "halts_today >= 1 and 300 <= secs_since_halt < 2400", "near_low_gate": [NEAR_LOW_LO, NEAR_LOW_HI],
              "dslo_flag": DSLO_FLAG, "s20_scale": S20_SCALE, "clip": [0.25, 4.0],
              "pins": [{"gap_60": int(B.gap_60.values[i]), "volat_bp": float(B.volat.values[i]), "l600": int(B.l600.values[i]),
                        "b600": int(B.b600.values[i]), "ht": int(B.ht.values[i]), "ssh": int(B.ssh.values[i]),
                        "signal_vwap": float(B.signal_vwap.values[i]), "sess_low": float(B.sess_low.values[i]),
                        "ols_slope_1200": float(B.ols_slope_1200.values[i]), "pf1": float(pf1[i]), "multiplier": float(mult[i])}
                       for i in idx]})
    jp = os.path.join(args.out_dir, "v2_coefficients.json"); json.dump(C, open(jp, "w"), indent=1); log(f"wrote {jp} ({len(idx)} pins)")

os.makedirs(args.out_dir, exist_ok=True)
drop = ["vol_10_bar", "vol_60_bar", "vol_1200_bar", "dlv_1200", "dlv2_1200"]
tp = os.path.join(args.out_dir, f"flushfaderv2_reference_trips{sfx}.parquet"); bkp = os.path.join(args.out_dir, f"flushfaderv2_reference_book{sfx}.parquet")
T.drop(columns=drop).to_parquet(tp, index=False); B.drop(columns=drop).to_parquet(bkp, index=False)
txt = "\n".join(rows) + "\n"; mdp = os.path.join(args.out_dir, f"flushfaderv2_reference{sfx}.md"); open(mdp, "w").write(txt); print(txt)
log(f"wrote {tp} ({len(T):,}), {bkp} ({len(B):,}), {mdp}")
