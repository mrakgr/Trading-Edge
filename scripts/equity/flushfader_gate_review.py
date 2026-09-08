"""FlushFader gate review (§S49, 2026-09-08) — every gate of SPEC v3.1 × ROSTER v3.3 against
chance and against rival features at the same trip loss, on a GATES-OFF base corpus.

Run from research/:
    python -u scripts/equity/flushfader_gate_review.py --build            # slice from --trips
    python -u scripts/equity/flushfader_gate_review.py --validate         # predicate dict vs the engine's own spec run
    python -u scripts/equity/flushfader_gate_review.py --gate speed       # one gate's report (see GATES)
    python -u scripts/equity/flushfader_gate_review.py --gate all

Every book number is CONSTRUCTION 3: greedy mc=1 per ticker-day replayed INSIDE the filtered
set (the 2026-08-28 ruling). The null resamples TICKER-DAYS, never trips.

Per AND-gate G:   full book S  vs  LOO book S∖G;  CUT = LOO-book trades failing G;
                  null = 2,000 tkd-resamples of the LOO book at the full book's tkd count;
                  substitution = every other continuous input, thresholded on S∖G to the full
                  book's n (both directions), replayed; time control; band table (one replay per band).
Per OR-voice V:   full book vs the book WITHOUT V (the vote shrinks); ADDED = full-book trades
                  that V alone admits; null = random tkds from the UNVOTED pool at the added set's size.
"""
import argparse, os, sys, time, warnings
import numpy as np, pandas as pd, duckdb
from numba import njit
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from flushfader_common import raw_px_expr
warnings.filterwarnings("ignore")

ap = argparse.ArgumentParser()
ap.add_argument("--trips", default="data/equity/flushfader/base_v19/trips_p*.parquet")
ap.add_argument("--slice", default="data/flushfader_review_slice.parquet")
ap.add_argument("--build", action="store_true")
ap.add_argument("--validate", default=None, help="engine spec run to reproduce, e.g. data/equity/flushfader/v49_spec20/trips_p*.parquet")
ap.add_argument("--gate", default=None)
ap.add_argument("--out", default="data/flushfader_gate_review")
ap.add_argument("--draws", type=int, default=2000)
ap.add_argument("--trim", type=float, default=0.05)
ap.add_argument("--seed", type=int, default=7)
ap.add_argument("--mem", default="4GB")
ap.add_argument("--summary", default=None, help="write the one-row-per-gate overview here")
ap.add_argument("--drop", default="eff10,s20,s5", help="gates RULED OUT of the spec (S49b, user 2026-09-08); they leave S and are not reported")
args = ap.parse_args()
os.makedirs(args.out, exist_ok=True)
con = duckdb.connect(); con.execute(f"SET memory_limit='{args.mem}'"); con.execute("SET threads=6")
T0 = time.time()
def log(*a):
    print(f"[{time.time()-T0:7.1f}s]", *a, flush=True)

# ---------------------------------------------------------------- 1. the slice
# One row per sampler trip; every gate INPUT as a float32 column; keys as ints.
def build():
    cols = {r[0] for r in con.execute(f"DESCRIBE SELECT * FROM read_parquet('{args.trips}')").fetchall()}
    rawpx, _ = raw_px_expr(con, args.trips)
    opt = []   # substitute candidates that exist only on newer corpora
    for c in ["consol_3m", "consol_5m", "consol_10m", "consol_20m", "consol_5m_lag1m",
              "lows_rr1_180", "lows_rr2_180", "lows_rr3_180", "lows_rr1_300", "lows_rr2_300", "lows_rr3_300",
              "lows_rr3_120", "vol_ew_60", "dv_ew_60", "halt_secs_cum"]:
        if c in cols: opt.append(f"{c}::FLOAT AS {c}")
    optsql = (",\n      " + ",\n      ".join(opt)) if opt else ""
    # ⚠ clock: on the S43cr engine the canonical vol_* are TIME-clock; the per-bar moment sums (dlv*) and the
    # bar-clock vol10rate gate need the `_bar` twins. Pre-clock-fix corpora (base_v18) only have the bar columns.
    vb = (lambda w: f"vol_{w}_bar") if "vol_60_bar" in cols else (lambda w: f"vol_{w}")
    vb10, vb60, vb1200 = vb(10), vb(60), vb(1200)
    q = f"""
    COPY (
      SELECT dense_rank() OVER (ORDER BY symbol, trade_date)::INTEGER AS tkd,
             symbol, trade_date, year(trade_date::DATE)::SMALLINT AS yr,
             signal_sec::INTEGER AS signal_sec, entry_sec::INTEGER AS ent, exit_sec::INTEGER AS ext,
             ret_exit::DOUBLE AS ret,
             -- layer 1
             dv_0945_tape::DOUBLE AS dv0945, (volat_20m*1e4)::DOUBLE AS volat, ({rawpx})::DOUBLE AS px,
             gap_60::SMALLINT AS gap60, lows_since_first_low_180::SMALLINT AS l180,
             -- layer 2 inputs (the spec card's post-hoc formulas)
             (signal_vwap/vwap_60_prev - 1)::DOUBLE AS speed,
             (signal_vwap/hi_60 - 1)::DOUBLE AS d1m,
             (ols_slope_since_flow*6e5)::DOUBLE AS ssf,
             (signal_vwap/(dv_leg/vol_leg) - 1)::DOUBLE AS dlv,
             ols_r_since_flow::DOUBLE AS rflow,
             (CASE WHEN dlv2_1200/{vb1200} - (dlv_1200/{vb1200})^2 > 0 THEN (ln(signal_vwap) - dlv_1200/{vb1200}) / sqrt(dlv2_1200/{vb1200} - (dlv_1200/{vb1200})^2) END)::DOUBLE AS z20,
             halts_today::SMALLINT AS ht, secs_since_halt::INTEGER AS ssh,
             lows_since_first_low::SMALLINT AS k20,
             abs(eff_20m)::DOUBLE AS eff20a, abs(eff_10m)::DOUBLE AS eff10a, eff_9ema_10m::DOUBLE AS e9,
             (({vb10}/10.0)/({vb60}/60.0))::DOUBLE AS v10r,
             lows_since_first_low_300::SMALLINT AS l300,
             (rng_300/rng_20m)::DOUBLE AS rngf,
             ((ols_slope_600 - ols_slope_1200)*6e5)::DOUBLE AS accel,
             (ols_slope_1200*6e5)::DOUBLE AS s20, (ols_slope_300*6e5)::DOUBLE AS s5,
             -- layer 3 voice inputs
             ((signal_vwap/first_low_vwap)*(1+d_hi_flow) - 1)::DOUBLE AS d20a,
             (signal_vwap/sess_low - 1)::DOUBLE AS dslo,
             ((volat_slope_10m - volat_slope_20m)*2e4)::DOUBLE AS vexp,
             (volat_slope_5m*2e4)::DOUBLE AS vcrush,
             ac1_ewma::DOUBLE AS ac1, secs_since_first_low::INTEGER AS esf,
             downticks_since_uptick::SMALLINT AS dsu,
             -- layer 4 + controls + extra substitute candidates
             gap_adj_1200::FLOAT AS gadj1200, (ols_slope_60*6e5)::FLOAT AS s1,
             bars_present::INTEGER AS bars_present,
             eff_20m::FLOAT AS eff20s, eff_9ema_20m::FLOAT AS e9_20, lows_since_first_low_120::SMALLINT AS l120,
             lows_since_first_low_600::SMALLINT AS l600, rng_20m::FLOAT AS rng20, (rng_600/rng_20m)::FLOAT AS rngf600,
             (volat_10m/volat_20m)::FLOAT AS vratio, volat_eslope_10m::FLOAT AS ves10, volat_eslope_5m::FLOAT AS ves5,
             n_eff_shannon_600::FLOAT AS shan600, eff_since_flow::FLOAT AS effflow, z_since_flow::FLOAT AS zflow,
             (vol_60/vol_0945_tape)::FLOAT AS rr60, tc_60::FLOAT AS tc60, (vol_60*signal_vwap)::FLOAT AS dv60,
             bars_since_high::INTEGER AS bsh, secs_since_last_uptick::INTEGER AS sslu, lows_since_uptick::SMALLINT AS lsu
             {optsql}
      FROM read_parquet('{args.trips}')
      WHERE ret_exit IS NOT NULL AND NOT isnan(ret_exit)
      ORDER BY tkd, signal_sec
    ) TO '{args.slice}' (FORMAT PARQUET, COMPRESSION ZSTD)"""
    con.execute(q)
    n = con.execute(f"SELECT count(*), count(DISTINCT tkd) FROM read_parquet('{args.slice}')").fetchone()
    log(f"slice {args.slice}: {n[0]:,} trips / {n[1]:,} tkd  (px expr `{rawpx}`; optional cols {len(opt)})")

if args.build:
    build()
    if not args.gate and not args.validate: sys.exit(0)

log("loading slice"); D = pd.read_parquet(args.slice)
D = D.drop(columns=["symbol", "trade_date"]) if args.gate else D
TKD = D.tkd.values.astype(np.int64); ENT = D.ent.values.astype(np.int64); EXT = D.ext.values.astype(np.int64)
RET = D.ret.values.astype(np.float64); YR = D.yr.values.astype(np.int64)
YEARS = list(range(int(YR.min()), int(YR.max()) + 1)); N = len(D)
log(f"{N:,} trips, {len(np.unique(TKD)):,} tkd, years {YEARS[0]}-{YEARS[-1]}")
# NaN-safe accessors: a NaN input FAILS every gate (the engine's cold-fails convention)
def col(c): return D[c].values.astype(np.float64)

# ---------------------------------------------------------------- 2. the gates
# (name, layer, kind, predicate-fn, input col or None, human threshold)
G = {}
def gate(name, layer, kind, fn, inp, thr):
    G[name] = dict(layer=layer, kind=kind, fn=fn, inp=inp, thr=thr)
c = col
gate("dv0945",  1, "and", lambda: c("dv0945") >= 2e6,                    "dv0945", ">= $2M = the corpus floor (S49a: $3M RETIRED, user 2026-09-08)")
gate("volat40", 1, "and", lambda: c("volat") >= 40,                       "volat",  ">= 40 bp (engine band floor 20)")
gate("win1500", 1, "and", lambda: c("signal_sec") <= 54000,               "signal_sec", "<= 15:00")
gate("g60",     1, "and", lambda: c("gap60") < 4,                         "gap60",  "< 4")
gate("px1",     1, "and", lambda: c("px") >= 1,                           "px",     ">= $1 raw")
gate("lows180", 2, "and", lambda: c("l180") >= 3,                         "l180",   ">= 3 (SPEC v3.0; a spec gate, layer 2 — user)")
gate("speed",   2, "and", lambda: c("speed") < -0.02,                     "speed",  "< -2%/1m")
gate("d1m",     2, "and", lambda: c("d1m") < -0.02,                       "d1m",    "< -2%")
gate("ssf",     2, "and", lambda: (c("ssf") >= -375) & (c("ssf") < -25),  "ssf",    "in [-375,-25) bp/m")
gate("dlv",     2, "and", lambda: c("dlv") < -0.03,                       "dlv",    "< -3%")
gate("rflow",   2, "and", lambda: c("rflow") >= -0.95,                    "rflow",  ">= -0.95")
gate("z20",     2, "and", lambda: c("z20") < -1.5,                        "z20",    "< -1.5 sigma")
def _cascade():
    ht, ssh = c("ht"), c("ssh")
    return (ht == 0) | ((ht >= 1) & (ht < 3) & (ssh >= 120)) | ((ht >= 3) & (ssh >= 1200))
gate("cascade", 2, "and", _cascade,                                       "ssh",    "ht>=1 wait 120s, ht>=3 wait 1200s")
gate("K",       2, "and", lambda: (c("k20") >= 26) & (c("k20") <= 50),    "k20",    "in [26,50]")
gate("eff20",   2, "and", lambda: (c("eff20a") >= 0.30) & (c("eff20a") < 0.50), "eff20a", "|eff20| in [.30,.50)")
gate("eff10",   2, "and", lambda: c("eff10a") >= 0.15,                    "eff10a", "|eff10| >= .15")
gate("e9",      2, "and", lambda: c("e9") >= -0.10,                       "e9",     "eff9ema10 >= -.10")
gate("v10r",    2, "and", lambda: c("v10r") >= 0.75,                      "v10r",   ">= .75x (bar-clock)")
gate("lows300", 2, "and", lambda: c("l300") >= 6,                         "l300",   ">= 6")
gate("rngf",    2, "and", lambda: c("rngf") < 0.80,                       "rngf",   "< .80")
gate("accel",   2, "and", lambda: c("accel") >= -80,                      "accel",  ">= -80 bp/m")
gate("s20",     2, "and", lambda: c("s20") < -10,                         "s20",    "< -10 bp/m")
gate("s5",      2, "and", lambda: c("s5") >= -400,                        "s5",     ">= -400 bp/m")
gate("v20",     3, "or",  lambda: c("volat") >= 140,                      "volat",  ">= 140 bp")
gate("d20a",    3, "or",  lambda: c("d20a") < -0.28,                      "d20a",   "< -28%")
gate("dslo",    3, "or",  lambda: c("dslo") >= 0.08,                      "dslo",   ">= +8%")
gate("vexp",    3, "or",  lambda: c("vexp") > 12,                         "vexp",   "> 12")
gate("vcrush",  3, "or",  lambda: c("vcrush") <= -24,                     "vcrush", "<= -24")
gate("acneg",   3, "or",  lambda: c("ac1") < -0.1,                        "ac1",    "< -0.1")
gate("legage",  3, "or",  lambda: (c("esf") >= 0) & (c("esf") <= 450),    "esf",    "in [0,450] s")
gate("dsu",     3, "or",  lambda: c("dsu") >= 8,                          "dsu",    ">= 8")
gate("haltband",3, "or",  lambda: (c("ssh") >= 1200) & (c("ssh") < 4800), "ssh",    "in [1200,4800)")
gate("stier",   3, "or",  lambda: (c("ht") >= 1) & (c("ssh") >= 120) & (c("ssh") < 1200), "ssh", "ht>=1 & ssh in [120,1200)")
for k, v in G.items():   # NaN -> False, materialised once
    m = v["fn"](); v["mask"] = np.where(np.isnan(m.astype(float)), False, m).astype(bool) if m.dtype != bool else m
DROPPED = [x for x in args.drop.split(",") if x]
AND = [k for k, v in G.items() if v["kind"] == "and" and k not in DROPPED]; OR = [k for k, v in G.items() if v["kind"] == "or" and k not in DROPPED]
log(f"spec = {len(AND)} AND gates + {len(OR)} voices; DROPPED by ruling: {DROPPED}")

# The substitute candidate inputs (continuous columns tried in BOTH directions).
SUBS = ["dv0945", "volat", "signal_sec", "gap60", "px", "l180", "speed", "d1m", "ssf", "dlv", "rflow", "z20", "ssh",
        "k20", "eff20a", "eff10a", "e9", "v10r", "l300", "rngf", "accel", "s20", "s5", "d20a", "dslo", "vexp",
        "vcrush", "ac1", "esf", "dsu", "gadj1200", "s1", "eff20s", "e9_20", "l120", "l600", "rng20", "rngf600",
        "vratio", "ves10", "ves5", "shan600", "effflow", "zflow", "rr60", "tc60", "dv60", "bsh", "sslu", "lsu"]
SUBS += [x for x in ["consol_3m", "consol_5m", "consol_10m", "consol_20m", "consol_5m_lag1m", "lows_rr1_180",
                     "lows_rr2_180", "lows_rr3_180", "lows_rr1_300", "lows_rr2_300", "lows_rr3_300", "lows_rr3_120",
                     "vol_ew_60", "dv_ew_60"] if x in D.columns]

def spec_mask(drop=None, no_vote=False):
    m = np.ones(N, bool)
    for k in AND:
        if k != drop: m &= G[k]["mask"]
    if not no_vote:
        v = np.zeros(N, bool)
        for k in OR:
            if k != drop: v |= G[k]["mask"]
        m &= v
    return m

# ---------------------------------------------------------------- 3. replay + stats
@njit(cache=True)
def greedy_keep(tkd, ent, ext, mask, keep):
    last_tkd = -1; last_ext = -1
    for i in range(tkd.shape[0]):
        if tkd[i] != last_tkd:
            last_tkd = tkd[i]; last_ext = -1
        if mask[i] and ent[i] >= last_ext:
            keep[i] = True; last_ext = ext[i]

def book(mask):
    keep = np.zeros(N, bool); greedy_keep(TKD, ENT, EXT, mask, keep); return keep

def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum()
    return float("inf") if l == 0 else g / l
def tpf1(r):
    if len(r) < 20: return float("nan")
    return pf(r[r >= np.quantile(r, args.trim)]) - 1
def S(keep):
    r = RET[keep]; n = len(r)
    if n == 0: return dict(n=0, tkd=0, pf=np.nan, tpf1=np.nan, win=np.nan, avg=np.nan, worst=np.nan)
    return dict(n=n, tkd=len(np.unique(TKD[keep])), pf=pf(r), tpf1=tpf1(r), win=(r > 0).mean() * 100,
                avg=r.mean() * 100, worst=r.min() * 100)
def yrow(keep):
    out = []
    for y in YEARS:
        r = RET[keep & (YR == y)]
        out.append((len(r), pf(r) if len(r) else np.nan))
    return out
def worst_removed(keep):
    out = []
    for y in YEARS:
        r = RET[keep & (YR == y)]
        out.append(pf(np.sort(r)[1:]) if len(r) > 1 else np.nan)
    return out
def f(x, d=3):
    if x is None or (isinstance(x, float) and np.isnan(x)): return "nan"
    if isinstance(x, float) and np.isinf(x): return "inf"
    return f"{x:.{d}f}" if isinstance(x, float) else str(x)
def line(label, keep):
    s = S(keep); ys = yrow(keep)
    return (f"| {label} | {s['n']:,} | {s['tkd']:,} | {f(s['pf'])} | {f(s['tpf1'])} | {f(s['win'],1)} | {f(s['avg'],2)} | {f(s['worst'],1)} | "
            + " | ".join(f"{f(p,2)} ({n})" for n, p in ys) + " |")
HDR = ("| set | n | tkd | PF | trimPF-1 | win% | avg% | worst% | " + " | ".join(str(y) for y in YEARS) + " |\n"
       "|---|---|---|---|---|---|---|---|" + "---|" * len(YEARS))

def null_draws(keep_pool, n_tkd, draws, rng):
    """PF / trimPF-1 / avg distribution of random TICKER-DAY subsets of a book (keep_pool) at n_tkd tkds."""
    idx = np.flatnonzero(keep_pool); t = TKD[idx]; r = RET[idx]
    u, start = np.unique(t, return_index=True); end = np.append(start[1:], len(t))
    if n_tkd >= len(u) or n_tkd <= 0: return None
    pfs, tps, avs = np.empty(draws), np.empty(draws), np.empty(draws)
    for d in range(draws):
        pick = rng.choice(len(u), n_tkd, replace=False)
        rr = np.concatenate([r[start[p]:end[p]] for p in pick])
        pfs[d] = pf(rr); tps[d] = tpf1(rr); avs[d] = rr.mean() * 100
    return pfs, tps, avs
def pct(dist, x):
    return float((dist < x).mean() * 100)

def match_threshold(base_mask, vals, target_n, keep_high):
    """Threshold on `vals` (over base_mask rows) so the REPLAYED book has ~target_n trades. Returns (thr, keep)."""
    v = vals[base_mask]; v = v[~np.isnan(v)]
    if len(v) == 0: return None, None
    lo, hi = 0.0, 1.0; best = None
    for _ in range(14):
        q = (lo + hi) / 2
        thr = np.quantile(v, 1 - q if keep_high else q)
        m = base_mask & (~np.isnan(vals)) & ((vals >= thr) if keep_high else (vals <= thr))
        k = book(m); n = int(k.sum())
        if best is None or abs(n - target_n) < abs(best[2] - target_n): best = (thr, k, n)
        if abs(n - target_n) <= max(3, 0.01 * target_n): break
        if n < target_n: lo = q
        else: hi = q
    return best[0], best[1]

# ---------------------------------------------------------------- 4. validation
if args.validate:
    full = spec_mask(no_vote=True) & G["dv0945"]["mask"]
    # the engine's spec run has NO book frame (volat 20bp, window 16:00, lows180 off) — compare on the engine gates only
    eng = np.ones(N, bool)
    for k in AND:
        if G[k]["layer"] == 2: eng &= G[k]["mask"]
    eng &= G["dv0945"]["mask"]
    mine = set(zip(D.symbol.values[eng], D.trade_date.values[eng], D.signal_sec.values[eng]))
    ref = con.execute(f"SELECT symbol, trade_date, signal_sec FROM read_parquet('{args.validate}')").fetchall()
    ref = set(ref)
    log(f"VALIDATE predicate dict vs {args.validate}: mine {len(mine):,}  ref {len(ref):,}  "
        f"both {len(mine & ref):,}  mine-only {len(mine - ref):,}  ref-only {len(ref - mine):,}")
    for lab, sset in [("mine-only", mine - ref), ("ref-only", ref - mine)]:
        for x in sorted(sset)[:8]: print("   ", lab, x)
    keep = book(spec_mask())
    log("BOOK (full spec + frame + vote, mc=1 inside):"); print(HDR); print(line("S", keep))
    if not args.gate: sys.exit(0)

# ---------------------------------------------------------------- 5. one gate's report
rng = np.random.default_rng(args.seed)
FULL = spec_mask(); KFULL = book(FULL); SF = S(KFULL)
log(f"full spec book: {SF['n']:,} @ {f(SF['pf'])} trimPF-1 {f(SF['tpf1'])}")

def hour_table(keeps, labels):
    edges = [(35100, 37800, "09:45-10:30"), (37800, 43200, "10:30-12:00"), (43200, 50400, "12:00-14:00"),
             (50400, 54000, "14:00-15:00"), (54000, 57601, "15:00-16:00")]
    ss = col("signal_sec"); out = ["| hour | " + " | ".join(f"{l} n | {l} PF" for l in labels) + " |",
                                   "|---|" + "---|---|" * len(labels)]
    for a, b, lab in edges:
        cells = []
        for k in keeps:
            r = RET[k & (ss >= a) & (ss < b)]; cells.append(f"{len(r)} | {f(pf(r) if len(r) else np.nan,2)}")
        out.append(f"| {lab} | " + " | ".join(cells) + " |")
    return "\n".join(out)

def band_table(base_mask, vals, nb=8):
    v = vals[base_mask]; v = v[~np.isnan(v)]
    if len(v) < 100: return "(too few values)"
    qs = np.unique(np.quantile(v, np.linspace(0, 1, nb + 1)))
    out = ["| band (replay inside) | n | tkd | PF | trimPF-1 | avg% | " + " | ".join(str(y) for y in YEARS) + " |",
           "|---|---|---|---|---|---|" + "---|" * len(YEARS)]
    for i in range(len(qs) - 1):
        lo, hi = qs[i], qs[i + 1]
        m = base_mask & (vals >= lo) & ((vals < hi) if i < len(qs) - 2 else (vals <= hi))
        k = book(m); s = S(k); ys = yrow(k)
        out.append(f"| [{lo:.4g}, {hi:.4g}{')' if i < len(qs)-2 else ']'} | {s['n']:,} | {s['tkd']:,} | {f(s['pf'])} | {f(s['tpf1'])} | {f(s['avg'],2)} | "
                   + " | ".join(f"{f(p,2)} ({n})" for n, p in ys) + " |")
    return "\n".join(out)

SUMMARY = []
def report(g):
    v = G[g]; out = [f"# Gate `{g}` (layer {v['layer']}, {v['kind'].upper()}): {v['thr']}\n",
                     f"Corpus `{args.trips}` — {N:,} sampler trips, {len(np.unique(TKD)):,} tkd. All books = mc=1 replay INSIDE the set.\n"]
    if v["kind"] == "and":
        loo = spec_mask(drop=g); kloo = book(loo)
        cut = kloo & ~v["mask"]                      # LOO-book trades the gate would remove
        out += ["## 1. Leave-one-out\n", HDR, line("S (full spec)", KFULL), line(f"S \\ {g}", kloo),
                line(f"CUT: S\\{g} book trades failing {g}", cut), line(f"S\\{g} book trades passing {g} (construction 2)", kloo & v["mask"]),
                "", "worst-trip-removed year PF (thin-cell control): S = " + ", ".join(f"{y} {f(p,2)}" for y, p in zip(YEARS, worst_removed(KFULL)))
                + " ; S\\g = " + ", ".join(f"{y} {f(p,2)}" for y, p in zip(YEARS, worst_removed(kloo))), ""]
        nd = null_draws(kloo, SF["tkd"], args.draws, rng)
        if nd:
            pfs, tps, avs = nd
            out += ["## 2. Null — random ticker-day subsets of S\\g at the full book's tkd count\n",
                    f"| stat | actual (S) | null median | null 2.5% | null 97.5% | percentile of actual |", "|---|---|---|---|---|---|",
                    f"| PF | {f(SF['pf'])} | {f(np.nanmedian(pfs))} | {f(np.nanquantile(pfs,.025))} | {f(np.nanquantile(pfs,.975))} | {pct(pfs, SF['pf']):.1f} |",
                    f"| trimPF-1 | {f(SF['tpf1'])} | {f(np.nanmedian(tps))} | {f(np.nanquantile(tps,.025))} | {f(np.nanquantile(tps,.975))} | {pct(tps, SF['tpf1']):.1f} |",
                    f"| avg% | {f(SF['avg'],2)} | {f(np.nanmedian(avs),2)} | {f(np.nanquantile(avs,.025),2)} | {f(np.nanquantile(avs,.975),2)} | {pct(avs, SF['avg']):.1f} |", ""]
        else:
            out += ["## 2. Null — (S\\g has no more tkds than S: the gate cuts nothing at book level)\n"]
        # substitution
        rows = []
        for h in SUBS:
            if h == v["inp"]: continue
            vals = col(h)
            for keep_high in (True, False):
                thr, k = match_threshold(loo, vals, SF["n"], keep_high)
                if k is None: continue
                s = S(k); ys = yrow(k)
                rows.append((s["tpf1"], f"| {h} {'>=' if keep_high else '<='} {thr:.4g} | {s['n']:,} | {f(s['pf'])} | {f(s['tpf1'])} | {f(s['avg'],2)} | "
                             + " | ".join(f"{f(p,2)}" for n, p in ys) + " |"))
        rows.sort(key=lambda x: (-(x[0] if not np.isnan(x[0]) else -9)))
        sl, sc = S(kloo), S(cut)
        SUMMARY.append(f"| {g} | {v['thr']} | {sl['n']:,} | {f(sl['pf'])} | {f(sl['tpf1'])} | {sc['n']} | {f(sc['pf'],2)} | {f(sc['avg'],2)} | "
                       f"{pct(nd[0], SF['pf']):.0f} / {pct(nd[1], SF['tpf1']):.0f} | {sum(1 for r in rows if r[0] > SF['tpf1'])} / {len(rows)} | "
                       f"{rows[0][1].split('|')[1].strip()} {rows[0][1].split('|')[3].strip()} / {rows[0][1].split('|')[4].strip()} |" if nd and rows else f"| {g} | {v['thr']} | (no cut) |")
        out += ["## 3. Substitution — every other input thresholded on S\\g to the SAME trip count (replayed), top 12 by trimPF-1\n",
                "| substitute | n | PF | trimPF-1 | avg% | " + " | ".join(str(y) for y in YEARS) + " |", "|---|---|---|---|---|" + "---|" * len(YEARS),
                f"| **{g} itself (S)** | {SF['n']:,} | {f(SF['pf'])} | {f(SF['tpf1'])} | {f(SF['avg'],2)} | " + " | ".join(f"{f(p,2)}" for n, p in yrow(KFULL)) + " |"]
        out += [r[1] for r in rows[:12]]
        out += [f"\n(all {len(rows)} substitutes: better trimPF-1 than {g} = {sum(1 for r in rows if r[0] > SF['tpf1'])})", ""]
        # time control
        x = col(v["inp"]); ok = loo & ~np.isnan(x)
        cs = np.corrcoef(x[ok], col("signal_sec")[ok])[0, 1]; cb = np.corrcoef(x[ok], col("bars_present")[ok])[0, 1]
        out += ["## 4. Time control\n", f"corr({v['inp']}, signal_sec) = {cs:+.3f}   corr({v['inp']}, bars_present) = {cb:+.3f}  (on S\\g rows; >0.3 is suspect)\n",
                hour_table([KFULL, kloo, cut], ["S", "S\\g", "cut"]), ""]
        # band table
        out += [f"## 5. Band table of `{v['inp']}` on S\\g (octiles, one replay per band)\n", band_table(loo, x), ""]
    else:
        wo = spec_mask(drop=g); kwo = book(wo)                # vote without this voice
        others = np.zeros(N, bool)
        for k in OR:
            if k != g: others |= G[k]["mask"]
        solo = KFULL & v["mask"] & ~others                    # full-book trades only this voice admits
        pool = spec_mask(no_vote=True) & ~others & ~v["mask"] # unvoted pool: passes frame+engine, fails every voice
        kpool = book(pool)
        out += ["## 1. Drop the voice\n", HDR, line("S (full roster)", KFULL), line(f"S without {g}", kwo),
                line(f"ADDED: S-book trades that only {g} admits", solo), line("UNVOTED pool (frame+engine, no voice), replayed", kpool), ""]
        ns = int(len(np.unique(TKD[solo])))
        nd = null_draws(kpool, ns, args.draws, rng) if ns else None
        s = S(solo)
        if nd:
            pfs, tps, avs = nd
            out += [f"## 2. Null — random ticker-day subsets of the UNVOTED pool at the added set's tkd count ({ns})\n",
                    f"| stat | added ({g} alone) | null median | null 2.5% | null 97.5% | percentile |", "|---|---|---|---|---|---|",
                    f"| PF | {f(s['pf'])} | {f(np.nanmedian(pfs))} | {f(np.nanquantile(pfs,.025))} | {f(np.nanquantile(pfs,.975))} | {pct(pfs, s['pf']):.1f} |",
                    f"| trimPF-1 | {f(s['tpf1'])} | {f(np.nanmedian(tps))} | {f(np.nanquantile(tps,.025))} | {f(np.nanquantile(tps,.975))} | {pct(tps, s['tpf1']):.1f} |",
                    f"| avg% | {f(s['avg'],2)} | {f(np.nanmedian(avs),2)} | {f(np.nanquantile(avs,.025),2)} | {f(np.nanquantile(avs,.975),2)} | {pct(avs, s['avg']):.1f} |", ""]
        rows = []
        for h in SUBS:
            if h == v["inp"]: continue
            vals = col(h)
            for keep_high in (True, False):
                thr, k = match_threshold(pool, vals, s["n"], keep_high)
                if k is None: continue
                ss = S(k)
                rows.append((ss["tpf1"], f"| {h} {'>=' if keep_high else '<='} {thr:.4g} | {ss['n']:,} | {f(ss['pf'])} | {f(ss['tpf1'])} | {f(ss['avg'],2)} | "
                             + " | ".join(f"{f(p,2)}" for n, p in yrow(k)) + " |"))
        rows.sort(key=lambda x: (-(x[0] if not np.isnan(x[0]) else -9)))
        out += [f"## 3. Substitution — other inputs carving the SAME number of trades out of the unvoted pool, top 12\n",
                "| substitute | n | PF | trimPF-1 | avg% | " + " | ".join(str(y) for y in YEARS) + " |", "|---|---|---|---|---|" + "---|" * len(YEARS),
                f"| **{g} alone (added)** | {s['n']:,} | {f(s['pf'])} | {f(s['tpf1'])} | {f(s['avg'],2)} | " + " | ".join(f"{f(p,2)}" for n, p in yrow(solo)) + " |"]
        out += [r[1] for r in rows[:12]]
        x = col(v["inp"]); ok = pool & ~np.isnan(x)
        cs = np.corrcoef(x[ok], col("signal_sec")[ok])[0, 1]; cb = np.corrcoef(x[ok], col("bars_present")[ok])[0, 1]
        out += ["", "## 4. Time control\n", f"corr({v['inp']}, signal_sec) = {cs:+.3f}   corr({v['inp']}, bars_present) = {cb:+.3f}\n",
                hour_table([KFULL, kwo, solo], ["S", "S-voice", "added"]), ""]
        out += [f"## 5. Band table of `{v['inp']}` on the unvoted frame (frame+engine, no vote), octiles\n",
                band_table(spec_mask(no_vote=True), x), ""]
    txt = "\n".join(out); path = os.path.join(args.out, f"{g}.md")
    open(path, "w").write(txt); print(txt); log(f"wrote {path}")

if args.gate:
    sel = list(G) if args.gate == "all" else [k for k in G if G[k]["layer"] == int(args.gate[5:])] if args.gate.startswith("layer") else args.gate.split(",")
    for g in sel:
        if g in DROPPED: continue
        report(g)
    if args.summary:
        hdr = (f"S (full spec) = {SF['n']:,} @ {f(SF['pf'])} trimPF-1 {f(SF['tpf1'])}\n\n"
               "| gate | threshold | S\\g n | S\\g PF | S\\g trimPF-1 | cut n | cut PF | cut avg% | S pct of null (PF / trimPF-1) | rivals better | best rival PF / trimPF-1 |\n"
               "|---|---|---|---|---|---|---|---|---|---|---|\n")
        open(args.summary, "w").write(hdr + "\n".join(SUMMARY) + "\n"); print(hdr + "\n".join(SUMMARY))
