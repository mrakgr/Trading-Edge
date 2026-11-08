"""FlushFaderV2 — the production reference (2026-10-12): the V2 book + the V2 sizing coefficients, in FULL DOUBLE
precision from a research BASE corpus (every spec gate OFF), for the Oms port and its Parity seal.

The V2 study (docs/flushfaderv2_results.md V2-1..V2-8) read its inputs from the FLOAT32 review slice; production must
not inherit that. Here every input comes from the base parquets in double:
    dslo = signal_vwap / sess_low − 1          (sess_low = the running session minimum, the signal bar included)
    s20  = ols_slope_1200 × 6e5                (bp/min; full 1200-bar window only, NaN otherwise)

  V2 = the broad book (flushfader_broad_reference.py's mask, calendar-aware entry window) − dslo ∈ (0, 3 %] (2 % until 2026-10-19)
       (a NaN dslo is NOT gated — sess_low is never NaN at a signal), then raw px ≥ $1 + mc=1 greedy per ticker-day.
  sizing = A3 design + [dslo ≥ 4 %] + standardised s20:
       ⭐ since 2026-10-19 (V2-16) the A3 volatility BANDS are ONE column ln(clip(volat, 40, 250 bp)) (--volat log;
       --volat bands = V2-7's 4 dummies; apply mode follows the coefficient file's volat_shape)
       z = (clip(s20, S20Lo, S20Hi) − S20Mean) / S20Sd     (the fit set's 1 % / 99 % quantiles, mean, SD)
       a NaN s20 enters as z = 0 (the mean) — never seen on the book, a live guard
       multiplier = clip(PF−1(x) / BasePf1, 0.25, 4)
  ⭐ LISTING AGE (user 2026-10-18, private docs/listing_age.md): the ticker-day's barnum (its session number in the episode,
       mr_candidate_1s_v2) must be >= MIN_BARNUM (11; base_v19 itself is barnum >= 22, the sessions 11..21 come from the
       splice scripts/equity/flushfader_run_base_v19_young.sh); a YOUNG trade (barnum < YOUNG_BELOW = 22) is sized at
       YOUNG_SCALE (0.8) × the model's PF−1 (the young sized PF−1 0.59 vs 0.73). The model is FIT on the barnum >= 22 book
       only (the fit, BasePf1 and the pins are V2's as they were); pf1_model = the model, pf1_fitted = what production
       emits (× YOUNG_SCALE when young).
  ⭐ V2-43 (2026-11-08): a TENTH sizing term, vlog (the 20m ln-volume slope, --vlog-trips; scripts/equity/flushfaderv2_vlog.py),
       fit on the ATR5-off book. ⭐ V2-44 (2026-11-08): an ENGINE gate, bars_since_high >= 300 (--min-bars-since-high); the
       production reference APPLIES the V2-43 coefficients (fit ungated) to the gated book:
           ... --coef models/flushfaderv2/v2_coefficients.json   (--min-bars-since-high 0 = the V2-43 reference)
  ⭐ V2-46 (2026-11-08): s20 is the TIME-indexed slope (--s20 time, the default; --tslope-trips, scripts/equity/flushfaderv2_tslope.py:
       ln vwap on the bar's second over the same 1,200 present bars, V2-45 `span 1`), refit on the V2-44 (gated) book; the
       coefficients carry s20_x = "time". Applying a file without s20_x (every fit before) uses the bar-indexed ols_slope_1200.
  ⭐ V2-49 (2026-11-08, user: "We definitely want this"): s20 = the 60s GRID time price slope (--s20 grid60, the default; pgrid60
       × 6e5) and the volume term = the per-second volume RATE (--vol vrate, the default; vrate × 1,200) replacing vlog — both from
       --pgrid-trips (scripts/equity/flushfaderv2_pgrid.py), refit on the V2-44 book; the coefficients carry s20_x = "grid60"
       and vrate_* (vlog stays recorded).
  ⭐ ATR5 GATE — REMOVED 2026-11-07 (user, V2-34..V2-38: --max-atr5 defaults to inf; the production fit's coefficients
       are unchanged). Was (user 2026-10-18, V2-12/V2-13, after WFF 10-14): no trade on a ticker-day whose 5-day ATR % (the 5 daily
       rows before D, scripts/equity/atr5_candidates.py) is >= MAX_ATR5 (4.0 = 400 %); a NaN ATR5 is not gated.

Outputs (out-dir, + _TAG):
  flushfaderv2_reference_trips.parquet   every trip passing the ENGINE-level predicates, V2 gate included (the Oms's
                                         signals under its default config must equal this set; keys symbol,
                                         trade_date, signal_sec) + dslo / s20
  flushfaderv2_reference_book.parquet    + raw px ≥ $1 + mc=1 greedy + the sizing features + multiplier
  v2_coefficients.json                   (fit mode only; a CANDIDATE — production's is models/flushfaderv2/, promoted by a
                                         copy with the Oms's port: models/README.md) the β vectors, BasePf1, the s20 constants, the thresholds,
                                         and a PIN TABLE of 200 book trades' (inputs → multiplier) for the F# tests

Run from research/:
  python -u scripts/equity/flushfaderv2_reference.py                                    # ⭐ THE PRODUCTION REFERENCE since
      2026-10-19 (V2-16): the fit on the full period (ln volat, gate 3 %, ATR5-gated, barnum >= 22 fit set);
      --coef models/flushfaderv2/v2_coefficients.json reproduces it exactly (the 2026-10-18 V2-7 file:
      data/flushfaderv2/pre_refit_2026-10-19/, with --near-low-hi 0.02)
  python -u scripts/equity/flushfaderv2_reference.py --trips 'data/equity/flushfader/base_10d/*.parquet' --tag 10d \
      --coef models/flushfaderv2/v2_coefficients.json                                     # apply the full-period fit
"""
import argparse, json, os, time
import numpy as np, pandas as pd, duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--trips", nargs="+", default=["data/equity/flushfader/base_v24/*.parquet"],
                help="V2-25 (2026-11-05): base_v24, the marker-free base with the young trades in the same run (was base_v19 + its young)")
ap.add_argument("--cands", default="data/v2_candidates/mr_candidate_1s_v2/*/*.parquet", help="the barnum source (hive)")
ap.add_argument("--min-barnum", type=int, default=11)
ap.add_argument("--young-below", type=int, default=22)
ap.add_argument("--young-scale", type=float, default=0.8)
ap.add_argument("--atr5", default="data/equity/atr5/candidates_atr5.parquet", help="per-ticker-day ATR5 (atr5_candidates.py)")
ap.add_argument("--max-atr5", type=float, default=float("inf"), help="drop ticker-days with ATR5 >= this (inf = off: the DEFAULT since 2026-11-07, V2-38; was 4.0)")
ap.add_argument("--coef", default="", help="APPLY these coefficients instead of fitting (the 10-day seal set)")
ap.add_argument("--out-dir", default="data/flushfaderv2")
ap.add_argument("--tag", default="")
ap.add_argument("--volat", choices=["bands", "log"], default="log", help="fit mode: the volatility term (V2-14/V2-16; log = production since 2026-10-19); apply mode reads it from --coef")
ap.add_argument("--near-low-hi", type=float, default=0.03, help="the near-low gate drops dslo in (0, this] (0.03 = production since 2026-10-19, V2-15/V2-16; was 0.02)")
ap.add_argument("--door", type=int, default=40)
ap.add_argument("--credit", type=float, default=0.001)
ap.add_argument("--cus-trips", nargs="+", default=["data/equity/flushfader/base_v24/*.parquet"],
                help="V2-24: the corpora carrying cusr_dn_k025 (the raw downside CUSUM at k = 0.25 bp/bar, research "
                     "flushfader_results.md §S49ap), joined on symbol / trade_date / signal_sec")
ap.add_argument("--barnum-fallback", default="",
                help="V2-24: a trip with no barnum in --cands takes the one this production-reference trips file recorded. "
                     "The 2026-10-25 marker-free rebuild of the 1s bars and the candidates ($1.6M dv_0945 floor) dropped 47 "
                     "ticker-days the 10-19 production fit had (630 base_v19 trips, 7 book trades); '' = no fallback (fails) — the "
                     "default since V2-25 (base_v24 is built on the rebuilt candidates: nothing to fill)")
ap.add_argument("--no-cusum", action="store_true", help="fit mode: the model WITHOUT the CUSUM term (the pre-V2-24 production fit)")
ap.add_argument("--vlog-trips", default="data/flushfaderv2/vlog/vlog_trips.parquet",
                help="V2-43 (user 2026-11-08): the canonical vlog per reference trip (scripts/equity/flushfaderv2_vlog.py: the OLS slope of "
                     "ln(bar volume) over the last 1,200 present bars × 1,200), joined on symbol / trade_date / signal_sec")
ap.add_argument("--no-vlog", action="store_true", help="fit mode: the model WITHOUT the vlog term (the V2-25 design)")
ap.add_argument("--min-bars-since-high", type=int, default=300,
                help="V2-44 (user 2026-11-08): an ENGINE gate, bars_since_high >= this (present bars since the bar after the last new "
                     "20m high, the signal bar included). 300 = production since 2026-11-08; 0 = off (the V2-43 reference)")
ap.add_argument("--tslope-trips", default="data/flushfaderv2/tslope/tslope_trips.parquet",
                help="V2-46 (user 2026-11-08): the canonical TIME-indexed 20m price slope per reference trip (scripts/equity/flushfaderv2_tslope.py: "
                     "the OLS slope of ln(vwap) on the bar's second over the last 1,200 present bars), joined as tslope_1200")
ap.add_argument("--pgrid-trips", default="data/flushfaderv2/pgrid/pgrid_trips.parquet",
                help="V2-49 (user 2026-11-08): the canonical 60s GRID price slope (pgrid60) and the volume RATE (vrate) per reference trip "
                     "(scripts/equity/flushfaderv2_pgrid.py), joined on symbol / trade_date / signal_sec")
ap.add_argument("--s20", choices=["grid60", "time", "index"], default="grid60",
                help="fit mode: which slope s20 is — grid60 (V2-49, production since 2026-11-08: pgrid60 x 6e5, 60s candles), time "
                     "(V2-46: the 1s time slope tslope_1200 x 6e5) or index (the bar-indexed ols_slope_1200 x 6e5, every fit before). "
                     "Apply mode reads it from --coef's s20_x (absent = index)")
ap.add_argument("--vol", choices=["vrate", "vlog"], default="vrate",
                help="fit mode: the volume term — vrate (V2-49, production since 2026-11-08: the per-second ln-volume slope x 1,200) or "
                     "vlog (V2-43: the bar-indexed one). Apply mode: vrate_lo in --coef -> vrate, vlog_lo -> vlog")
ap.add_argument("--mem", default="8GB")
args = ap.parse_args()
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:6.1f}s]", *a, flush=True)
sfx = f"_{args.tag}" if args.tag else ""
os.makedirs(args.out_dir, exist_ok=True)

# the gate and the flag (V2-6 / V2-7, user 2026-10-12)
NEAR_LOW_LO, NEAR_LOW_HI = 0.0, args.near_low_hi   # drop dslo in (LO, HI] (0.03 = production since 2026-10-19)
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
           lows_since_first_low_300 AS l300, lows_since_first_low_180 AS l180, abs(eff_10m) AS eff10a, bars_since_high,
           vol_10_bar, vol_60_bar, vol_1200_bar, dlv_1200, dlv2_1200, dv_0945_tape, sess_low, ols_slope_1200
           , c.barnum, x.atr5
    FROM read_parquet({args.trips}) t
    LEFT JOIN (SELECT ticker, date, barnum FROM read_parquet('{args.cands}', hive_partitioning = true)) c
           ON c.ticker = t.symbol AND c.date = t.trade_date
    LEFT JOIN read_parquet('{args.atr5}') x ON x.ticker = t.symbol AND x.date = t.trade_date
    WHERE gap_60 < {args.door} AND lows_since_first_low_300 >= 6 AND lows_since_first_low_180 >= 3
      AND signal_sec <= 54000
    ORDER BY symbol, trade_date, signal_sec""").df()
cus_src = " UNION ALL ".join(f"SELECT symbol, trade_date::VARCHAR AS trade_date, signal_sec, cusr_dn_k025 FROM read_parquet('{g}')" for g in args.cus_trips)
CU = con.execute(f"SELECT * FROM ({cus_src})").df()
D = D.merge(CU, on=["symbol", "trade_date", "signal_sec"], how="left")
S20_X = json.load(open(args.coef)).get("s20_x", "index") if args.coef else args.s20
PG = con.execute(f"SELECT symbol, trade_date::VARCHAR AS trade_date, signal_sec, pgrid60, vrate AS vrate_1200 FROM read_parquet('{args.pgrid_trips}')").df() \
    if os.path.exists(args.pgrid_trips) else None
_C = json.load(open(args.coef)) if args.coef else {}
assert PG is not None or (S20_X != "grid60" and "vrate_lo" not in _C and (args.coef or args.vol != "vrate" or args.no_vlog)), \
    f"no {args.pgrid_trips} (scripts/equity/flushfaderv2_pgrid.py writes it)"
if PG is not None: D = D.merge(PG, on=["symbol", "trade_date", "signal_sec"], how="left")
else: D["pgrid60"] = np.nan; D["vrate_1200"] = np.nan
D["vrate"] = D.vrate_1200.values * 1200.0          # the sizing term: per second × 1,200 (the Oms: VRate1200 × VRateScale)
if os.path.exists(args.tslope_trips):
    TS = con.execute(f"SELECT symbol, trade_date::VARCHAR AS trade_date, signal_sec, tslope AS tslope_1200 FROM read_parquet('{args.tslope_trips}')").df()
    D = D.merge(TS, on=["symbol", "trade_date", "signal_sec"], how="left")
else:  # a missing file under s20_x = time would silently score every trip at z = 0
    assert S20_X != "time", f"no {args.tslope_trips} (scripts/equity/flushfaderv2_tslope.py writes it); --s20 index for the bar-indexed slope"
    D["tslope_1200"] = np.nan
log(f"s20 = the {S20_X}-indexed 20m slope; tslope_1200 joined on {D.tslope_1200.notna().sum():,} rows")
if os.path.exists(args.vlog_trips):
    VL = con.execute(f"SELECT symbol, trade_date::VARCHAR AS trade_date, signal_sec, vlog FROM read_parquet('{args.vlog_trips}')").df()
    D = D.merge(VL, on=["symbol", "trade_date", "signal_sec"], how="left")
else:  # vlog is a production term since V2-43: a missing file would silently score every trip at z = 0
    assert args.no_vlog, f"no {args.vlog_trips} (scripts/equity/flushfaderv2_vlog.py writes it); --no-vlog for the V2-25 design"
    D["vlog"] = np.nan; log(f"no {args.vlog_trips}: vlog NaN everywhere (--no-vlog)")
log(f"{len(D):,} rows after the integer pre-filters; cusr_dn_k025 joined on {D.cusr_dn_k025.notna().sum():,} (NaN {D.cusr_dn_k025.isna().sum():,})")
if args.barnum_fallback and D.barnum.isna().any():
    # the ATR5 table was rebuilt from the same candidates (2026-10-25): those ticker-days' ATR5 comes from the file too
    FB = pd.read_parquet(args.barnum_fallback, columns=["symbol", "trade_date", "signal_sec", "barnum", "atr5"]).rename(
        columns={"barnum": "barnum_fb", "atr5": "atr5_fb"})
    D = D.merge(FB, on=["symbol", "trade_date", "signal_sec"], how="left")
    miss = D.barnum.isna(); filled = miss & D.barnum_fb.notna()
    D.loc[filled, "barnum"] = D.loc[filled, "barnum_fb"]
    D.loc[filled, "atr5"] = D.loc[filled, "atr5_fb"]
    D = D.drop(columns=["barnum_fb", "atr5_fb"])
    log(f"barnum fallback ({args.barnum_fallback}): {int(miss.sum())} trips had none in --cands, {int(filled.sum())} filled; "
        f"the {int((miss & ~filled).sum())} left were never engine-level trips (dropped)")
    D = D[D.barnum.notna()].reset_index(drop=True)
    D["barnum"] = D.barnum.astype("int64")            # the join made it nullable; the book stores int64
assert D.barnum.notna().all(), f"{D.barnum.isna().sum()} trips without a barnum"
D = D[D.barnum >= args.min_barnum].reset_index(drop=True)
log(f"{len(D):,} rows at barnum >= {args.min_barnum} ({(D.barnum < args.young_below).sum():,} young: barnum < {args.young_below})")
hot = D.atr5.values >= args.max_atr5                                  # NaN compares False: not gated
log(f"ATR5 gate (>= {args.max_atr5:g}): drops {hot.sum():,} rows on {D[hot].groupby(['symbol', 'trade_date']).ngroups} ticker-days; NaN ATR5 {D.atr5.isna().sum()}")
D = D[~hot].reset_index(drop=True)

def nz(m): return np.where(np.isnan(m.astype(float)), False, m).astype(bool)
with np.errstate(divide="ignore", invalid="ignore"):
    v10r = (D.vol_10_bar.values / 10.0) / (D.vol_60_bar.values / 60.0)
    mean = D.dlv_1200.values / D.vol_1200_bar.values
    var = D.dlv2_1200.values / D.vol_1200_bar.values - mean * mean
    z20 = np.where(var > 0, (np.log(D.signal_vwap.values) - mean) / np.sqrt(np.where(var > 0, var, 1.0)), np.nan)
    rate600 = D.l600.values / np.where(D.b600.values > 0, D.b600.values, np.nan).astype(float)
    dslo = D.signal_vwap.values / D.sess_low.values - 1.0
s20 = {"grid60": D.pgrid60.values, "time": D.tslope_1200.values, "index": D.ols_slope_1200.values}[S20_X] * S20_SCALE   # ⭐ V2-46/V2-49
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
n_v2 = int(M.sum())
if args.min_bars_since_high > 0:
    M &= nz(D.bars_since_high.values >= args.min_bars_since_high)        # ⭐ V2-44: the decline has lasted (a NaN fails)
T = D[M].copy().reset_index(drop=True); T["rate600"] = rate600[M]; T["z20"] = z20[M]; T["v10r"] = v10r[M]; T["dslo"] = dslo[M]; T["s20"] = s20[M]
log(f"engine-level trips: broad {n_broad:,} -> V2 {n_v2:,} (near-low gate removes {n_broad - n_v2:,}) -> {len(T):,} (bars_since_high >= {args.min_bars_since_high} removes {n_v2 - len(T):,}); NaN dslo {np.isnan(T.dslo.values).sum()}, NaN s20 {np.isnan(T.s20.values).sum()}")

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
young = (B.barnum.values < args.young_below)
OLD = ~young                                  # the fit set: the V2 book as it was (barnum >= 22)
log(f"book {len(B):,} trades" + ("" if args.tag else " (the slice study: 35,612)"))

# ---- the design
gap_band = np.digitize(B.gap_60.values, [1, 4]); volat_band = np.digitize(B.volat.values, [60, 90, 140, 250])
fast = (np.nan_to_num(B.rate600.values, nan=0.0) >= 0.07).astype(int)
tier = (nz(B.ht.values >= 1) & nz(B.ssh.values >= 300) & nz(B.ssh.values < 2400)).astype(int)
flag = nz(B.dslo.values >= DSLO_FLAG).astype(int)
def zs20(x, c):
    z = (np.clip(x, c["s20_lo"], c["s20_hi"]) - c["s20_mean"]) / c["s20_sd"]
    return np.where(np.isnan(x), 0.0, z)
VOLAT_CLIP = [40.0, 250.0]                  # V2-14: ln(clip(volat, 40, 250 bp)) — the engine gate is volat >= 40
def volat_cols(c):
    if c.get("volat_shape", "bands") == "log":
        return [np.log(np.clip(B.volat.values, *c["volat_clip_bp"]))]
    return [volat_band == k for k in range(1, 5)]
def zcus(x, c):
    """V2-24: the CUSUM level (bp), winsorised at the fit set's 1 % / 99 % and standardised; a NaN enters as z = 0 (the mean)"""
    z = (np.clip(x, c["cus_lo"], c["cus_hi"]) - c["cus_mean"]) / c["cus_sd"]
    return np.where(np.isnan(x), 0.0, z)
def zvlog(x, c):
    """V2-43: the 20m ln-volume slope, winsorised at the fit set's 1 % / 99 % and standardised; a NaN enters as z = 0 (the mean)"""
    z = (np.clip(x, c["vlog_lo"], c["vlog_hi"]) - c["vlog_mean"]) / c["vlog_sd"]
    return np.where(np.isnan(x), 0.0, z)
def zvrate(x, c):
    """V2-49: the volume RATE (per-second ln-volume slope × 1,200), winsorised at the fit set's 1 % / 99 % and standardised; NaN -> z = 0"""
    z = (np.clip(x, c["vrate_lo"], c["vrate_hi"]) - c["vrate_mean"]) / c["vrate_sd"]
    return np.where(np.isnan(x), 0.0, z)
def design(c):
    # ⭐ V2-24: the CUSUM term only when the coefficient file has it (an older file gives exactly the older model)
    return np.column_stack([np.ones(len(B)), gap_band == 1, gap_band == 2] + volat_cols(c)
                           + [fast, tier, flag, zs20(B.s20.values, c)]
                           + ([zcus(B.cusr_dn_k025.values.astype(float), c)] if "cus_lo" in c else [])
                           + ([zvlog(B.vlog.values.astype(float), c)] if "vlog_lo" in c else [])
                           + ([zvrate(B.vrate.values.astype(float), c)] if "vrate_lo" in c else [])).astype(float)
def names(c):
    v = ["ln volat (clip 40-250 bp)"] if c.get("volat_shape", "bands") == "log" else ["volat 60-90", "volat 90-140", "volat 140-250", "volat 250+"]
    return (["1", "gap 1-3", "gap 4-39"] + v + ["rate600 fast", "S tier", "dslo >= 4%", "s20 (standardised)"]
            + (["CUSUM dn k0.25 (standardised)"] if "cus_lo" in c else [])
            + (["vlog (standardised)"] if "vlog_lo" in c else [])
            + (["vrate (standardised)"] if "vrate_lo" in c else []))

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
    s = B.s20.values[OLD & ~np.isnan(B.s20.values)]
    lo, hi = np.quantile(s, [0.01, 0.99]); zc = np.clip(s, lo, hi)
    C = {"s20_x": S20_X, "s20_lo": float(lo), "s20_hi": float(hi), "s20_mean": float(zc.mean()), "s20_sd": float(zc.std()), "volat_shape": args.volat}
    if args.volat == "log": C["volat_clip_bp"] = VOLAT_CLIP
    if not args.no_cusum:
        cu = B.cusr_dn_k025.values.astype(float)[OLD]; cu = cu[~np.isnan(cu)]
        clo, chi = np.quantile(cu, [0.01, 0.99]); cc = np.clip(cu, clo, chi)
        C.update({"cus_k_bp": 0.25, "cus_lo": float(clo), "cus_hi": float(chi), "cus_mean": float(cc.mean()), "cus_sd": float(cc.std())})
    if not args.no_vlog and args.vol == "vrate":
        vl = B.vrate.values.astype(float)[OLD]; vl = vl[~np.isnan(vl)]
        vlo, vhi = np.quantile(vl, [0.01, 0.99]); vc = np.clip(vl, vlo, vhi)
        C.update({"vrate_window": 1200, "vrate_scale": 1200.0, "vrate_lo": float(vlo), "vrate_hi": float(vhi), "vrate_mean": float(vc.mean()), "vrate_sd": float(vc.std())})
    if not args.no_vlog and args.vol == "vlog":
        vl = B.vlog.values.astype(float)[OLD]; vl = vl[~np.isnan(vl)]
        vlo, vhi = np.quantile(vl, [0.01, 0.99]); vc = np.clip(vl, vlo, vhi)
        C.update({"vlog_window": 1200, "vlog_lo": float(vlo), "vlog_hi": float(vhi), "vlog_mean": float(vc.mean()), "vlog_sd": float(vc.std())})
    X = design(C)[OLD]; RO = R[OLD]; win = RO > 0
    C.update({"beta_p": [float(v) for v in fit_logistic(X, win.astype(float))],
              "beta_w": [float(v) for v in fit_loglink(X[win], RO[win])],
              "beta_l": [float(v) for v in fit_loglink(X[~win], -RO[~win])]})
    C["base_pf1"] = float(pf1_of(X, C).mean())
X = design(C); pf1_model = pf1_of(X, C)
pf1 = np.where(young, args.young_scale * pf1_model, pf1_model)        # ⭐ the young trades at YOUNG_SCALE
mult = np.clip(pf1 / C["base_pf1"], 0.25, 4.0)
B["gap_band"] = gap_band; B["volat_band"] = volat_band; B["rate600_fast"] = fast; B["s_tier"] = tier; B["dslo_flag"] = flag
B["s20_z"] = X[:, names(C).index("s20 (standardised)")]; B["cus_z"] = X[:, names(C).index("CUSUM dn k0.25 (standardised)")] if "cus_lo" in C else np.nan
B["vlog_z"] = X[:, names(C).index("vlog (standardised)")] if "vlog_lo" in C else np.nan
B["vrate_z"] = X[:, names(C).index("vrate (standardised)")] if "vrate_lo" in C else np.nan; B["young"] = young; B["pf1_model"] = pf1_model; B["pf1_fitted"] = pf1; B["multiplier"] = mult
log(f"multiplier mean {mult.mean():.4f}, range {mult.min():.3f}-{mult.max():.3f}, at the 0.25 floor {(mult <= 0.25).mean()*100:.1f}%")

def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
rows = [f"# FlushFaderV2 reference from `{' + '.join(args.trips)}` (barnum >= {args.min_barnum}, young < {args.young_below} × {args.young_scale}, ATR5 < {args.max_atr5:g}) (full double precision){' — coefficients ' + args.coef if args.coef else ''}\n",
        "| set | n | PF (net of credit) | net % |", "|---|---|---|---|",
        f"| engine-level trips (V2 gate in) | {len(T):,} | {pf((T.ret_exit.values + 2*args.credit/T.entry_px.values)*100):.3f} | |",
        f"| book (px ≥ 1, mc=1) | {len(B):,} | {pf(R):.3f} | {R.sum():,.0f} |",
        f"| book, sized ∝ V2 | {len(B):,} | {pf(R*mult):.3f} | {(R*mult).sum():,.0f} |",
        f"| · barnum ≥ {args.young_below} | {OLD.sum():,} | {pf(R[OLD]):.3f} (sized {pf(R[OLD]*mult[OLD]):.3f}) | {R[OLD].sum():,.0f} |",
        f"| · young: barnum {args.min_barnum}–{args.young_below - 1}, × {args.young_scale} | {young.sum():,} | {pf(R[young]):.3f} (sized {pf(R[young]*mult[young]):.3f}) | {R[young].sum():,.0f} |", ""]
if not args.coef:
    bp, bw, bl = (np.array(C[k]) for k in ("beta_p", "beta_w", "beta_l"))
    rows += ["| term | β_p (odds ×) | β_W (×) | β_L (×) |", "|---|---|---|---|"]
    rows += [f"| {nm} | {bp[j]:+.6f} ({np.exp(bp[j]):.3f}) | {bw[j]:+.6f} ({np.exp(bw[j]):.3f}) | {bl[j]:+.6f} ({np.exp(bl[j]):.3f}) |" for j, nm in enumerate(names(C))]
    rows += ["", f"s20 ({S20_X}-indexed): winsorise [{C['s20_lo']:.6f}, {C['s20_hi']:.6f}] bp/min, mean {C['s20_mean']:.6f}, SD {C['s20_sd']:.6f}; BasePf1 {C['base_pf1']:.10f}"]
    if "cus_lo" in C:
        rows += [f"CUSUM dn k0.25: winsorise [{C['cus_lo']:.6f}, {C['cus_hi']:.6f}] bp, mean {C['cus_mean']:.6f}, SD {C['cus_sd']:.6f}; "
                 f"NaN in the book {int(np.isnan(B.cusr_dn_k025.values.astype(float)).sum())}"]
    if "vrate_lo" in C:
        rows += [f"vrate (1200 bars, per second × 1200): winsorise [{C['vrate_lo']:.6f}, {C['vrate_hi']:.6f}], mean {C['vrate_mean']:.6f}, SD {C['vrate_sd']:.6f}; "
                 f"NaN in the book {int(np.isnan(B.vrate.values.astype(float)).sum())}"]
    if "vlog_lo" in C:
        rows += [f"vlog (1200 bars): winsorise [{C['vlog_lo']:.6f}, {C['vlog_hi']:.6f}], mean {C['vlog_mean']:.6f}, SD {C['vlog_sd']:.6f}; "
                 f"NaN in the book {int(np.isnan(B.vlog.values.astype(float)).sum())}"]
    # the PIN TABLE: 200 book trades spread over the multiplier range, inputs at full precision
    # (from the barnum >= 22 book only: the pins carry no barnum, so a young pin would not reproduce in the F# test)
    oi = np.flatnonzero(OLD)
    idx = oi[np.unique(np.argsort(mult[oi], kind="stable")[np.linspace(0, len(oi) - 1, 200).astype(int)])]
    C.update({"model": "FlushFaderV2 (A3 + dslo >= 4% + s20), docs/flushfaderv2_results.md V2-7" + (", ln volat V2-14/V2-16" if args.volat == "log" else "")
                       + ("" if args.no_cusum else ", + the CUSUM level dn k0.25 V2-21/V2-24")
                       + ("" if args.no_vlog else ", + the 20m ln-volume slope vlog V2-42/V2-43" if args.vol == "vlog" else ", + the volume RATE vrate V2-47/V2-49")
                       + (", s20 TIME-indexed V2-45/V2-46" if S20_X == "time" else "")
                       + (", s20 = the 60s grid time slope + the volume RATE V2-47..V2-49" if S20_X == "grid60" else ""),
              "fit": f"all years, V2 book n={int(OLD.sum()):,}, credit ${args.credit}/sh/side, from {args.trips[0]}",
              "design": names(C), "gap_edges": [0, 1, 4, 40], **({} if args.volat == "log" else {"volat_edges_bp": [40, 60, 90, 140, 250]}), "rate600_fast_threshold": 0.07,
              "s_tier": "halts_today >= 1 and 300 <= secs_since_halt < 2400", "near_low_gate": [NEAR_LOW_LO, NEAR_LOW_HI],
              "dslo_flag": DSLO_FLAG, "s20_scale": S20_SCALE, "clip": [0.25, 4.0],
              "pins": [{"gap_60": int(B.gap_60.values[i]), "volat_bp": float(B.volat.values[i]), "l600": int(B.l600.values[i]),
                        "b600": int(B.b600.values[i]), "ht": int(B.ht.values[i]), "ssh": int(B.ssh.values[i]),
                        "signal_vwap": float(B.signal_vwap.values[i]), "sess_low": float(B.sess_low.values[i]),
                        "ols_slope_1200": float(B.ols_slope_1200.values[i]),
                        **({"tslope_1200": float(B.tslope_1200.values[i])} if S20_X == "time" else {}),
                        **({"pgrid60": float(B.pgrid60.values[i])} if S20_X == "grid60" else {}),
                        **({"vrate_1200": float(B.vrate_1200.values[i])} if "vrate_lo" in C else {}),
                        **({"cusum_dn_k025": float(B.cusr_dn_k025.values[i])} if "cus_lo" in C else {}),
                        **({"vlog": float(B.vlog.values[i])} if "vlog_lo" in C else {}),
                        "pf1": float(pf1[i]), "multiplier": float(mult[i])}
                       for i in idx]})
    jp = os.path.join(args.out_dir, "v2_coefficients.json"); json.dump(C, open(jp, "w"), indent=1); log(f"wrote {jp} ({len(idx)} pins)")

os.makedirs(args.out_dir, exist_ok=True)
drop = ["vol_10_bar", "vol_60_bar", "vol_1200_bar", "dlv_1200", "dlv2_1200"]
tp = os.path.join(args.out_dir, f"flushfaderv2_reference_trips{sfx}.parquet"); bkp = os.path.join(args.out_dir, f"flushfaderv2_reference_book{sfx}.parquet")
T.drop(columns=drop).to_parquet(tp, index=False); B.drop(columns=drop).to_parquet(bkp, index=False)
txt = "\n".join(rows) + "\n"; mdp = os.path.join(args.out_dir, f"flushfaderv2_reference{sfx}.md"); open(mdp, "w").write(txt); print(txt)
log(f"wrote {tp} ({len(T):,}), {bkp} ({len(B):,}), {mdp}")
