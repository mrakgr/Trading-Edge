"""S49k — sizing the BROAD book (S49d step 7 + gap < 40, no vote) on volat_20m × lagged coil (user, 2026-09-08).

Rule (feedback_trim_bottom_5pct, feedback_no_vol_scaled_sizing_in_mr): size ∝ the cell's trimmed PF−1, never Kelly,
never 1/√vol. Sized vs flat is compared at EQUAL AVERAGE EXPOSURE (multipliers normalised to mean 1 over the book).
Holdout: multipliers derived on 2020–23 applied to 2024–26, and the mirror.

Run from research/:  python -u scripts/equity/flushfader_sizing_broad.py [--door 40] [--cost 0.10]
"""
import argparse, os, sys, time
import numpy as np, pandas as pd
from numba import njit

ap = argparse.ArgumentParser()
ap.add_argument("--slice", default="data/flushfader_review_slice.parquet")
ap.add_argument("--door", type=int, default=40)
ap.add_argument("--cost", type=float, default=0.10, help="round-trip cost, % of notional per trade (fixed bp)")
ap.add_argument("--credit", type=float, default=None, help="NET credit per share per SIDE in $ (rebate minus commission); overrides --cost, scales 1/price: trade cost = -2*credit/px")
ap.add_argument("--rule140", action="store_true", help="user rule: volat >= 140 bp only if gap_60 < 4")
ap.add_argument("--trim", type=float, default=0.05)
ap.add_argument("--base", type=float, default=0.10, help="fraction of equity per trade at multiplier 1 (compounded sim)")
ap.add_argument("--out", default="data/flushfader_gate_review/sizing_broad.md")
ap.add_argument("--measure", default="tpf1", choices=["tpf1", "pf1"], help="edge measure the multipliers are derived from: trimPF-1 (bottom 5% trimmed) or raw PF-1")
ap.add_argument("--gap-bands", default="0,1,4,13,40", help="gap_60 band edges (upper edge = the door); S49k default 0,1,4,8,13,20,30,40")
ap.add_argument("--rate-bands", type=int, default=2, choices=[2, 3, 4], help="rate600 bands: 4 (S49n quartiles) or 3 (the two fast bands folded, S49v)")
ap.add_argument("--halts", action="store_true", help="S49p: WAIT (ht>=4 & ssh<300 excluded) + S TIER (ht>=1 & ssh in [300,2400)) as a sizing axis")
args = ap.parse_args()
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:6.1f}s]", *a, flush=True)

D = pd.read_parquet(args.slice, columns=["tkd", "yr", "signal_sec", "ent", "ext", "ret", "dv0945", "volat", "px", "gap60",
                                        "l180", "l300", "eff10a", "z20", "v10r", "crf", "consol_5m_lag1m", "rate600", "lows_rr1_300", "ht", "ssh"])
N = len(D); log(f"{N:,} sampler trips")
def nz(m): return np.where(np.isnan(m.astype(float)), False, m).astype(bool)
# the S49d step-7 spec: frame (S49a) + lows300 + eff10 + v10r + z20 + lows180 + crf + coil5lo, door < args.door, NO vote
M = (nz(D.volat.values >= 40) & nz(D.signal_sec.values <= 54000) & nz(D.gap60.values < args.door) & nz(D.px.values >= 1)
     & nz(D.l300.values >= 6) & nz(D.eff10a.values >= 0.15) & nz(D.v10r.values >= 0.75) & nz(D.z20.values < -1.5)
     & nz(D.l180.values >= 3) & nz(D.crf.values <= -0.002) & nz(D.consol_5m_lag1m.values <= 0.22))
if args.rule140: M &= ~(nz(D.volat.values >= 140) & nz(D.gap60.values >= 4))
if args.halts: M &= ~(nz(D.ht.values >= 4) & nz(D.ssh.values < 300))   # S49p wait: 4th+ halt of the day, first 5 min after the resume

@njit(cache=True)
def greedy_keep(tkd, ent, ext, mask, keep):
    last_tkd = -1; last_ext = -1
    for i in range(tkd.shape[0]):
        if tkd[i] != last_tkd:
            last_tkd = tkd[i]; last_ext = -1
        if mask[i] and ent[i] >= last_ext:
            keep[i] = True; last_ext = ext[i]
keep = np.zeros(N, bool); greedy_keep(D.tkd.values.astype(np.int64), D.ent.values.astype(np.int64), D.ext.values.astype(np.int64), M, keep)
B = D[keep].sort_values(["yr", "tkd", "ent"]).reset_index(drop=True)
COST = (-2 * args.credit / B.px.values * 100) if args.credit is not None else np.full(len(B), args.cost)
R = B.ret.values * 100 - COST               # % per trade, net of cost (or plus the rebate credit)
COSTLAB = f"credit ${args.credit}/sh/side (mean {COST.mean():+.3f}%/trade, median px ${np.median(B.px.values):.2f})" if args.credit is not None else f"cost {args.cost}%/trade"
YR = B.yr.values; YEARS = sorted(set(YR))
log(f"broad book: {len(B):,} trades, door < {args.door}, {COSTLAB}{' , rule140' if args.rule140 else ''}")

def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
def tpf1(r):
    if len(r) < 20: return np.nan
    return pf(r[r >= np.quantile(r, args.trim)]) - 1
def pf1(r):
    if len(r) < 20: return np.nan
    return pf(r) - 1
MEAS = tpf1 if args.measure == "tpf1" else pf1
def f(x, d=2):
    return "inf" if np.isinf(x) else ("nan" if np.isnan(x) else f"{x:.{d}f}")

VB = [40, 60, 90, 140, 250, 1e9]; VL = ["40-60", "60-90", "90-140", "140-250", "250+"]
CB = [-1, 0.05, 0.10, 0.15, 0.221]; CL = ["<.05", ".05-.10", ".10-.15", ".15-.22"]
GB = [int(x) for x in args.gap_bands.split(",")]; GL = [f"{a}" if b == a + 1 else f"{a}-{b-1}" for a, b in zip(GB[:-1], GB[1:])]
vi = np.digitize(B.volat.values, VB[1:-1]); ci = np.digitize(B.consol_5m_lag1m.values, CB[1:-1]); gi = np.digitize(B.gap60.values, GB[1:-1])
RB = [0, 0.044, 0.07, 0.125, 1.01]; RL = ["<.044", ".044-.07", ".07-.125", ".125+"]   # rate600 quartile-ish bands (S49n)
if args.rate_bands == 3: RB = [0, 0.044, 0.07, 1.01]; RL = ["<.044", ".044-.07", ".07+"]
if args.rate_bands == 2: RB = [0, 0.07, 1.01]; RL = ["<.07", ".07+"]
ri = np.digitize(np.nan_to_num(B.rate600.values, nan=0.0), RB[1:-1])
TGB = [0, 1, 4]; TGL = ["0", "1-3", "4+"]; TVB = [40, 90, 140, 250]; TVL = ["<90", "90-140", "140-250", "250+"]   # the tier's coarse grid
tgi = np.digitize(B.gap60.values, TGB[1:]); tvi = np.digitize(B.volat.values, TVB[1:]); tgv = tgi * len(TVL) + tvi
T2L = ["gap<4 & volat<90", "gap<4 & volat>=90", "gap>=4 & volat<90", "gap>=4 & volat>=90"]
t2i = (B.gap60.values >= 4).astype(int) * 2 + (B.volat.values >= 90).astype(int)   # the tier's 2x2
TL = ["book", "S tier"]; ti = (nz(B.ht.values >= 1) & nz(B.ssh.values >= 300) & nz(B.ssh.values < 2400)).astype(int)

out = [f"# S49k — sizing the broad book (multipliers from {args.measure}): step-7 spec, gap < {args.door}, no vote; {len(B):,} trades; {COSTLAB}{'; RULE volat>=140 only if gap<4' if args.rule140 else ''}\n",
       f"Book flat: PF {f(pf(R),3)}  trimPF-1 {f(tpf1(R),3)}  avg {R.mean():+.2f}%  net {R.sum():,.0f}%\n"]

def grid(name, ai, al, bi, bl, stat):
    hdr = f"| {name} | " + " | ".join(bl) + " | all |\n|---|" + "---|" * (len(bl) + 1)
    rows = []
    for a in range(len(al)):
        cells = []
        for b in range(len(bl)):
            r = R[(ai == a) & (bi == b)]; cells.append(stat(r))
        rows.append(f"| {al[a]} | " + " | ".join(cells) + f" | {stat(R[ai == a])} |")
    rows.append("| all | " + " | ".join(stat(R[bi == b]) for b in range(len(bl))) + f" | {stat(R)} |")
    return hdr + "\n" + "\n".join(rows)
st_n = lambda r: f"{len(r):,}"
st_t = lambda r: f(tpf1(r))
st_a = lambda r: f"{r.mean():+.2f}" if len(r) else "—"
st_pf = lambda r: f(pf(r)) if len(r) else "—"
out += ["## 1. volat_20m (rows) × lagged coil (cols) — n", grid("volat \\ coil", vi, VL, ci, CL, st_n), "",
        "## 2. trimPF-1 per cell (the sizing metric)", grid("volat \\ coil", vi, VL, ci, CL, st_t), "",
        "## 3. avg % per trade (net)", grid("volat \\ coil", vi, VL, ci, CL, st_a), "",
        "## 4. PF per cell", grid("volat \\ coil", vi, VL, ci, CL, st_pf), "",
        "## 5. gap_60 (rows) × volat (cols) — trimPF-1", grid("gap \\ volat", gi, GL, vi, VL, st_t), "",
        "## 5b. gap_60 × volat — n", grid("gap \\ volat", gi, GL, vi, VL, st_n), "",
        "## 5c. gap_60 × volat — avg % per trade (net)", grid("gap \\ volat", gi, GL, vi, VL, st_a), "",
        "## 5d. gap_60 × volat — PF", grid("gap \\ volat", gi, GL, vi, VL, st_pf), "",
        "## 6. gap_60 (rows) × coil (cols) — trimPF-1", grid("gap \\ coil", gi, GL, ci, CL, st_t), ""]

# ---- multipliers: m(cell) = tpf1(cell) / tpf1(book), derived on a FIT set, normalised to mean 1 on the APPLY set
def mults(fit_mask, cells_fn, ncell, base_mask=None, fallback=1.0):
    """base = the BOOK's edge on the fit years (base_mask), so sub-population maps stay on the book's scale; thin cells (<50) -> fallback."""
    m = np.full(ncell, fallback); base = MEAS(R[fit_mask if base_mask is None else base_mask])
    for k in range(ncell):
        r = R[fit_mask & (cells_fn == k)]
        m[k] = (MEAS(r) / base) if len(r) >= 50 and np.isfinite(MEAS(r)) else fallback
    return np.clip(m, 0.25, 4.0)
# 6g. is rate600 EXPLAINED by gap x volat? within-cell test: each band vs cell-weighted peers in the same gap x volat cells
def within_cell(band_mask, fit_mask, cell_idx=None):
    """band vs cell-weighted peers in the same cells of cell_idx (default: the gap x volat cells)."""
    cidx = gv if cell_idx is None else cell_idx
    w = np.zeros(len(B)); other = fit_mask & ~band_mask
    for c in np.unique(cidx[fit_mask & band_mask]):
        kb = (fit_mask & band_mask & (cidx == c)).sum(); ko = (other & (cidx == c)).sum()
        if ko: w[other & (cidx == c)] = kb / ko
    rw = R * w
    if args.measure == "tpf1":   # trim the bottom 5% of WEIGHT from the peers, like MEAS trims the band
        o = np.argsort(R); cw = np.cumsum(w[o]) / w.sum(); drop = o[cw < args.trim]; rw = rw.copy(); rw[drop] = 0.0
    g_, l_ = rw[rw > 0].sum(), -rw[rw < 0].sum()
    peers = (g_ / l_ - 1) if l_ else np.inf
    return MEAS(R[fit_mask & band_mask]), peers, ((R * w).sum() / w.sum()) if w.sum() else np.nan
def sim(w, mask, label, cap=None):
    """w = per-trade multiplier (mean-1 normalised on mask). Returns the stats line for sized vs flat."""
    r = R[mask]; w = w[mask]
    if cap is not None: w = np.minimum(w, cap)
    w = w / w.mean()
    sized = w * r
    # compounded equity at base fraction per unit multiplier, chronological
    def maxdd(x):   # drawdown of the cumulative sum, in % of ONE position's notional (position units)
        c = np.cumsum(x); return float((np.maximum.accumulate(c) - c).max())
    yrs = " ".join(f"{f(pf(sized[YR[mask] == y]),2)}" for y in YEARS)
    return (f"| {label} | {len(r):,} | {f(pf(r),3)} / {f(pf(sized),3)} | {f(tpf1(r),3)} / {f(tpf1(sized),3)} | {r.mean():+.2f} / {sized.mean():+.2f} | "
            f"{r.sum():,.0f} / {sized.sum():,.0f} | {maxdd(r):,.0f} / {maxdd(sized):,.0f} | {r.min():.1f} / {sized.min():.1f} | {yrs} |")
HDR = ("| multiplier map | n | PF flat / sized | trimPF-1 flat / sized | avg% flat / sized | net flat / sized | "
       "maxDD (position-% units) flat / sized | worst trade flat / sized | sized PF by year |\n|---|---|---|---|---|---|---|---|---|")
all_m = np.ones(len(B), bool); early = YR <= 2023; late = YR > 2023
vc = vi * len(CL) + ci                                    # joint cell index
gv = gi * len(VL) + vi
def apply(m, idx): return m[idx]
rows = ["## 7. Sized vs flat at EQUAL average exposure (multipliers = cell trimPF-1 / book trimPF-1, clipped [0.25, 4])", HDR]
for lab, fit, app in [("in-sample (fit all, apply all)", all_m, all_m), ("holdout: fit 2020-23 → apply 2024-26", early, late),
                      ("holdout: fit 2024-26 → apply 2020-23", late, early)]:
    rows.append(f"| **{lab}** | | | | | | | | |")
    rows.append(sim(apply(mults(fit, vi, len(VL)), vi), app, "volat only"))
    rows.append(sim(apply(mults(fit, ci, len(CL)), ci), app, "coil only"))
    rows.append(sim(apply(mults(fit, vc, len(VL) * len(CL)), vc), app, "volat × coil"))
    rows.append(sim(apply(mults(fit, gi, len(GL)), gi), app, "gap only"))
    wsep = apply(mults(fit, gi, len(GL)), gi) * apply(mults(fit, vi, len(VL)), vi)
    rows.append(sim(wsep, app, "SEPARABLE gap ladder × volat ladder (marginal factors)"))
    if args.halts:
        rcs = np.array([(lambda o, pe, _: o / pe)(*within_cell(ri == k, fit)) for k in range(len(RL))])
        rows.append(sim(np.minimum(wsep * rcs[ri], 4.0), app, "SEPARABLE gap × volat × rate600 conditional, clip 4"))
        rows.append(sim(np.minimum(wsep * rcs[ri] * apply(mults(fit, ti, 2), ti), 4.0), app, "SEPARABLE gap × volat × rate600 conditional × tier, clip 4"))
        # ALL-CONDITIONAL separable: gap within volat bands, volat within gap bands, rate600 within gap x volat cells
        gc = np.array([(lambda o, pe, _: o / pe)(*within_cell(gi == k, fit, vi)) for k in range(len(GL))])
        vcnd = np.array([(lambda o, pe, _: o / pe)(*within_cell(vi == k, fit, gi)) for k in range(len(VL))])
        rm = mults(fit, ri, len(RL))
        rows.append(f"| (ladders on the fit years — gap marginal {' / '.join(f'{x:.2f}' for x in mults(fit, gi, len(GL)))} vs conditional {' / '.join(f'{x:.2f}' for x in gc)}; volat marginal {' / '.join(f'{x:.2f}' for x in mults(fit, vi, len(VL)))} vs conditional {' / '.join(f'{x:.2f}' for x in vcnd)}; rate600 marginal {' / '.join(f'{x:.2f}' for x in rm)} vs conditional {' / '.join(f'{x:.2f}' for x in rcs)}) | | | | | | | | |")
        rows.append(sim(np.minimum(wsep * rm[ri] * apply(mults(fit, ti, 2), ti), 4.0), app, "SEPARABLE all-MARGINAL: gap × volat × rate600 marginal × tier, clip 4"))
        rows.append(sim(np.minimum(gc[gi] * vcnd[vi] * rcs[ri] * apply(mults(fit, ti, 2), ti), 4.0), app, "SEPARABLE all-CONDITIONAL: gap|volat × volat|gap × rate600|cell × tier, clip 4"))
        wam = wsep * rm[ri] * apply(mults(fit, ti, 2), ti)   # the all-marginal product before clip
        if lab.startswith("in-sample"):
            gm_, vm_, tm_ = mults(fit, gi, len(GL)), mults(fit, vi, len(VL)), mults(fit, ti, 2)
            rows.append(f"| (trade-weighted MEAN of each ladder over the book: gap {gm_[gi].mean():.3f} · volat {vm_[vi].mean():.3f} · rate600 {rm[ri].mean():.3f} · tier {tm_[ti].mean():.3f}; raw product: mean {wam.mean():.3f}, median {np.median(wam):.3f}, min {wam.min():.2f}, max {wam.max():.2f}, share > 4 before clip {(wam > 4).mean()*100:.2f}%; after clip 4 the mean is {np.minimum(wam, 4).mean():.3f}) | | | | | | | | |")
        for pw in (0.5, 0.75, 1.0, 1.25):
            wp = np.minimum(wam ** pw, 4.0)
            rows.append(sim(wp, app, f"ALL-MARGINAL ^ {pw}, clip 4  (max {wp.max()/ (wp[app].mean()):.2f}, share > 2x {(wp/wp[app].mean() > 2).mean()*100:.1f}%, share < 0.5x {(wp/wp[app].mean() < 0.5).mean()*100:.1f}%)"))
        wj = apply(mults(fit, gv, len(GL) * len(VL)), gv)
        for pw in (1.25, 1.5):
            rows.append(sim(np.minimum(wj ** pw * rcs[ri] * apply(mults(fit, ti, 2), ti), 4.0), app, f"CONTROL: JOINT grid ^ {pw} × rate600 conditional × tier, clip 4"))
        rows.append(f"| (mean multiplier on gap 0 & volat>=140: separable {wsep[(gi==0)&(vi>=3)].mean():.2f} · joint {wj[(gi==0)&(vi>=3)].mean():.2f}; share of book with multiplier > 2: separable {(wsep>2).mean()*100:.1f}% · joint {(wj>2).mean()*100:.1f}%) | | | | | | | | |")
    gvc = (gi * len(VL) + vi) * len(CL) + ci
    rows.append(sim(apply(mults(fit, gvc, len(GL) * len(VL) * len(CL)), gvc), app, "gap × volat × coil"))
    gv = gi * len(VL) + vi
    rows.append(sim(apply(mults(fit, gv, len(GL) * len(VL)), gv), app, "gap × volat"))
    rows.append(sim(apply(mults(fit, ri, len(RL)), ri), app, "rate600 only"))
    rows.append(sim(apply(mults(fit, gv, len(GL) * len(VL)), gv) * apply(mults(fit, ri, len(RL)), ri), app, "gap × volat × rate600 FACTOR (separable: gv cells × r bands)"))
    rc = np.array([(lambda o, pe, _: o / pe)(*within_cell(ri == k, fit)) for k in range(len(RL))])   # CONDITIONAL (within gap x volat cell) rate600 factor
    rows.append(sim(apply(mults(fit, gv, len(GL) * len(VL)), gv) * rc[ri], app, f"gap × volat × rate600 CONDITIONAL factor ({' / '.join(f'{x:.2f}' for x in rc)})"))
    if args.halts:
        rows.append(sim(np.minimum(apply(mults(fit, gv, len(GL) * len(VL)), gv) * rc[ri] * apply(mults(fit, ti, 2), ti), 4.0), app, "gap × volat × rate600 CONDITIONAL factor × tier factor, clip 4"))
    if args.halts:
        rows.append(sim(np.minimum(apply(mults(fit, gv, len(GL) * len(VL)), gv) * apply(mults(fit, ri, len(RL)), ri) * apply(mults(fit, ti, 2), ti), 4.0), app, "gap × volat × rate600 FACTOR × tier factor, clip 4"))
    gvr = gv * len(RL) + ri
    rows.append(sim(apply(mults(fit, gvr, len(GL) * len(VL) * len(RL)), gvr), app, "gap × volat × rate600"))
    if args.halts:
        rows.append(sim(apply(mults(fit, ti, 2), ti), app, "S tier only"))
        gvt = gv * 2 + ti
        rows.append(sim(apply(mults(fit, gvt, len(GL) * len(VL) * 2), gvt), app, "gap × volat × tier (joint cells)"))
        rows.append(sim(apply(mults(fit, gv, len(GL) * len(VL)), gv) * apply(mults(fit, ti, 2), ti), app, "gap × volat, × tier factor"))
        rows.append(sim(apply(mults(fit, gvr, len(GL) * len(VL) * len(RL)), gvr) * apply(mults(fit, ti, 2), ti), app, "gap × volat × rate600, × tier factor"))
        # SPLIT maps: the tier sized from ITS OWN cells (coarse grid), the rest from its own gvr / gv map; one scale (book trimPF-1), no product
        for rest_lab, rest_idx, rest_n in [("gap × volat × rate600", gvr, len(GL) * len(VL) * len(RL)), ("gap × volat", gv, len(GL) * len(VL))]:
            for tier_lab, tier_idx, tier_n in [("2x2 gap<4 × volat>=90", t2i, 4), ("gap3 × volat4", tgv, len(TGL) * len(TVL)), ("volat4", tvi, len(TVL)), ("flat 1 cell", np.zeros(len(B), int), 1)]:
                w = np.where(ti == 1, apply(mults(fit & (ti == 1), tier_idx, tier_n, fit), tier_idx), apply(mults(fit & (ti == 0), rest_idx, rest_n, fit), rest_idx))
                rows.append(sim(w, app, f"SPLIT: rest = {rest_lab} (fit on rest) · tier = {tier_lab} (fit on tier)"))
        # S49t (user): REST on gap x volat x rate600 (fit on rest), HALT trades on gap x volat (their OWN cells; thin cells -> the tier factor or 1.0)
        tf = mults(fit & (ti == 1), np.zeros(len(B), int), 1, fit)[0]
        w_rest = apply(mults(fit & (ti == 0), gvr, len(GL) * len(VL) * len(RL), fit), gvr)
        rows.append(f"| (S49t: tier factor on the fit years = {tf:.2f}; tier cells with >= 50 trades: gap × volat {sum(1 for k in range(len(GL)*len(VL)) if ((ti==1)&fit&(gv==k)).sum()>=50)} of 35, gap3 × volat4 {sum(1 for k in range(len(TGL)*len(TVL)) if ((ti==1)&fit&(tgv==k)).sum()>=50)} of 12) | | | | | | | | |")
        for tl, tidx, tn in [("own gap × volat", gv, len(GL) * len(VL)), ("own gap3 × volat4", tgv, len(TGL) * len(TVL))]:
            for fl, fb in [("thin -> tier factor", tf), ("thin -> 1.0", 1.0)]:
                w = np.where(ti == 1, apply(mults(fit & (ti == 1), tidx, tn, fit, fb), tidx), w_rest)
                rows.append(sim(w, app, f"S49t: rest = gvr · halt = {tl} ({fl})"))
        rows.append(sim(np.where(ti == 1, np.minimum(apply(mults(fit, gv, len(GL) * len(VL)), gv) * tf, 4.0), w_rest), app, f"S49t: rest = gvr · halt = BOOK gap × volat map × tier factor {tf:.2f}, clip 4"))
        rows.append(sim(np.where(ti == 1, tf, w_rest), app, f"S49t: rest = gvr · halt = flat tier factor {tf:.2f}"))
        # the S49r form: PREMIUM cell (tier & gap<4 & volat>=90) at its own flat multiplier; everything else (incl. the other 3 tier cells) on the book map
        prem = (ti == 1) & (t2i == 1)
        for rest_lab, rest_idx, rest_n in [("gap × volat × rate600", gvr, len(GL) * len(VL) * len(RL)), ("gap × volat", gv, len(GL) * len(VL))]:
            mp = mults(fit & prem, np.zeros(len(B), int), 1, fit)[0]
            w = np.where(prem, mp, apply(mults(fit & ~prem, rest_idx, rest_n, fit), rest_idx))
            rows.append(sim(w, app, f"PREMIUM CELL flat {mp:.2f} (fit) · rest (incl. other tier cells) = {rest_lab}"))
        # premium cell = book map x a factor (clip 4); factor = the cell's ratio (1.80) or the RESIDUAL after the book map's own grade of those trades
        wb = apply(mults(fit, gvr, len(GL) * len(VL) * len(RL)), gvr)
        mp = mults(fit & prem, np.zeros(len(B), int), 1, fit)[0]; mb = wb[fit & prem].mean(); resid = mp / mb
        mt = mults(fit & (ti == 1), np.zeros(len(B), int), 1, fit)[0]; mbt = wb[fit & (ti == 1)].mean()
        rows.append(f"| (book map's mean multiplier on the premium cell = {mb:.2f} vs the cell's own ratio {mp:.2f} → residual {resid:.2f}; on the whole tier {mbt:.2f} vs {mt:.2f} → residual {mt/mbt:.2f}) | | | | | | | | |")
        rows.append(sim(np.where(prem, np.minimum(wb * mp, 4.0), wb), app, f"PREMIUM CELL = book gvr map × {mp:.2f} (cell ratio), clip 4 · rest = book map"))
        rows.append(sim(np.where(prem, np.minimum(wb * resid, 4.0), wb), app, f"PREMIUM CELL = book gvr map × {resid:.2f} (residual), clip 4 · rest = book map"))
        rows.append(sim(np.where(ti == 1, np.minimum(wb * (mt / mbt), 4.0), wb), app, f"WHOLE TIER = book gvr map × {mt/mbt:.2f} (residual), clip 4 · rest = book map"))
        # CONTROL: is the product's gain tier-specific or just a STEEPER book map? (map^p, no tier information)
        # WITHIN-CELL tier factor on the fit years: trimPF-1 of premium-cell tier trades / trimPF-1 of cell-weighted non-tier trades (same gvr cells)
        gvr_idx_ = (gi * len(VL) + vi) * len(RL) + ri; wgt = np.zeros(len(B))
        for c in np.unique(gvr_idx_[fit & prem]):
            kt = (fit & prem & (gvr_idx_ == c)).sum(); kn = (fit & (ti == 0) & (gvr_idx_ == c)).sum()
            if kn: wgt[fit & (ti == 0) & (gvr_idx_ == c)] = kt / kn
        def tpf1w(r, w):   # weighted measure: trim the bottom 5% of weight (tpf1) or nothing (pf1)
            o = np.argsort(r); cw = np.cumsum(w[o]) / w.sum(); keep_ = o[cw >= (args.trim if args.measure == "tpf1" else 0.0)]; rr, ww = r[keep_] * w[keep_], w[keep_]
            g_, l_ = rr[rr > 0].sum(), -rr[rr < 0].sum(); return g_ / l_ - 1 if l_ else np.inf
        fw = MEAS(R[fit & prem]) / tpf1w(R, wgt)
        rows.append(f"| (within-cell tier factor on the fit years, {args.measure}: premium-cell {MEAS(R[fit & prem]):.2f} / cell-matched non-tier {tpf1w(R, wgt):.2f} = {fw:.2f}) | | | | | | | | |")
        for pw in (1.0, 1.25, 1.5):
            base_w = wb ** pw
            rows.append(sim(np.minimum(base_w, 4.0), app, f"book gvr map ^ {pw} (NO tier), clip 4"))
            rows.append(sim(np.minimum(np.where(prem, base_w * fw, base_w), 4.0), app, f"book gvr map ^ {pw} × WITHIN-CELL factor {fw:.2f} on the premium cell, clip 4"))
            rows.append(sim(np.where(prem, np.minimum(base_w, 4.0) * fw, np.minimum(base_w, 4.0)), app, f"book gvr map ^ {pw} (clip 4) × WITHIN-CELL factor {fw:.2f} on the premium cell, NO clip on the product"))
        wprod = apply(mults(fit, gvr, len(GL) * len(VL) * len(RL)), gvr) * apply(mults(fit, ti, 2), ti)
        rows.append(sim(wprod, app, "gap × volat × rate600, × tier factor, product CAPPED at 4", cap=4.0))
        rows.append(sim(apply(mults(fit, gv, len(GL) * len(VL)), gv) * apply(mults(fit, ti, 2), ti), app, "gap × volat, × tier factor, product CAPPED at 4", cap=4.0))
        if lab.startswith("in-sample"):
            rows.append(f"| (product multiplier before cap: max {wprod.max():.2f}, share > 4 = {(wprod > 4).mean()*100:.1f}%, share > 3 = {(wprod > 3).mean()*100:.1f}%) | | | | | | | | |")
if args.halts:
    out += ["", f"## 6b. S TIER (ht>=1 & ssh in [300,2400)) on this book, {COSTLAB}", "| set | n | PF | trimPF-1 | avg% | net | worst | " + " | ".join(str(y) for y in YEARS) + " |", "|---|---|---|---|---|---|---|" + "---|" * len(YEARS)]
    for lab, m in [("S tier", ti == 1), ("rest of book", ti == 0), ("tier & gap<4", (ti == 1) & (B.gap60.values < 4)), ("tier & gap>=4", (ti == 1) & (B.gap60.values >= 4)),
                   ("tier & volat<90", (ti == 1) & (B.volat.values < 90)), ("tier & volat>=90", (ti == 1) & (B.volat.values >= 90))]:
        r = R[m]; out.append(f"| {lab} | {len(r):,} | {f(pf(r),3)} | {f(tpf1(r),3)} | {r.mean():+.2f} | {r.sum():,.0f} | {r.min():.1f} | " + " | ".join(f"{f(pf(r[YR[m]==y]),2)} ({(YR[m]==y).sum()})" for y in YEARS) + " |")
    out += ["", "tier multiplier (fit all): " + ", ".join(f"{l} {m:.2f}" for l, m in zip(TL, mults(all_m, ti, 2))),
            "tier multiplier fit 2020-23: " + ", ".join(f"{l} {m:.2f}" for l, m in zip(TL, mults(early, ti, 2))) + " ; fit 2024-26: " + ", ".join(f"{l} {m:.2f}" for l, m in zip(TL, mults(late, ti, 2)))]
    out += ["", "## 6d. The tier's 2x2: gap<4 / >=4 × volat<90 / >=90 (net of credit)", "| tier cell | n | PF | trimPF-1 | avg% | net | worst | mult (fit all) | mult fit 20-23 | mult fit 24-26 | " + " | ".join(str(y) for y in YEARS) + " |", "|---|---|---|---|---|---|---|---|---|---|" + "---|" * len(YEARS)]
    m_all, m_e, m_l = mults(all_m & (ti == 1), t2i, 4, all_m), mults(early & (ti == 1), t2i, 4, early), mults(late & (ti == 1), t2i, 4, late)
    for k, lab in enumerate(T2L):
        m = (ti == 1) & (t2i == k); r = R[m]
        out.append(f"| {lab} | {len(r):,} | {f(pf(r),3)} | {f(tpf1(r),3)} | {r.mean():+.2f} | {r.sum():,.0f} | {r.min():.1f} | {m_all[k]:.2f} | {m_e[k]:.2f} | {m_l[k]:.2f} | " + " | ".join(f"{f(pf(r[YR[m]==y]),2)} ({(YR[m]==y).sum()})" for y in YEARS) + " |")
    # 6e. WITHIN-CELL comparison: tier trades vs non-tier trades in the SAME gap x volat x rate600 cells (premium cell gap<4 & volat>=90)
    gvr_idx = (gi * len(VL) + vi) * len(RL) + ri
    prem = (ti == 1) & (t2i == 1); nont = (ti == 0)
    out += ["", "## 6e. Halt premium WITHIN cells: tier trades (gap<4 & volat>=90) vs non-halt trades in the same gap × volat × rate600 cells",
            "| cell (gap / volat / rate600) | n tier | n non-tier | trimPF-1 tier | trimPF-1 non-tier | avg% tier | avg% non-tier | PF tier | PF non-tier |", "|---|---|---|---|---|---|---|---|---|"]
    cells = [c for c in np.unique(gvr_idx[prem]) if (prem & (gvr_idx == c)).sum() >= 30]
    for c in cells:
        a, b_ = R[prem & (gvr_idx == c)], R[nont & (gvr_idx == c)]
        g_, v_, r_ = c // (len(VL) * len(RL)), (c // len(RL)) % len(VL), c % len(RL)
        out.append(f"| {GL[g_]} / {VL[v_]} / {RL[r_]} | {len(a)} | {len(b_):,} | {f(tpf1(a))} | {f(tpf1(b_))} | {a.mean():+.2f} | {b_.mean():+.2f} | {f(pf(a))} | {f(pf(b_))} |")
    # stratified null: for each gvr cell draw as many non-tier trades as the tier has there; 2,000 draws
    rng = np.random.default_rng(7); draws = 2000
    comp = [(c, (prem & (gvr_idx == c)).sum(), np.flatnonzero(nont & (gvr_idx == c))) for c in np.unique(gvr_idx[prem])]
    comp = [(c, k, idx) for c, k, idx in comp if len(idx) >= k]
    a = R[prem]; pfs, tps, avs = np.empty(draws), np.empty(draws), np.empty(draws)
    for d in range(draws):
        rr = np.concatenate([R[rng.choice(idx, k, replace=False)] for c, k, idx in comp])
        pfs[d], tps[d], avs[d] = pf(rr), tpf1(rr), rr.mean()
    out += ["", f"Stratified null (cell-matched non-halt draws, {draws} draws, {sum(k for _,k,_ in comp)} of {len(a)} tier trades matchable):",
            "| stat | tier (premium cell) | null median | null 2.5% | null 97.5% | percentile |", "|---|---|---|---|---|---|",
            f"| PF | {f(pf(a),3)} | {f(np.median(pfs),3)} | {f(np.quantile(pfs,.025),3)} | {f(np.quantile(pfs,.975),3)} | {(pfs < pf(a)).mean()*100:.1f} |",
            f"| trimPF-1 | {f(tpf1(a),3)} | {f(np.median(tps),3)} | {f(np.quantile(tps,.025),3)} | {f(np.quantile(tps,.975),3)} | {(tps < tpf1(a)).mean()*100:.1f} |",
            f"| avg% | {a.mean():+.2f} | {np.median(avs):+.2f} | {np.quantile(avs,.025):+.2f} | {np.quantile(avs,.975):+.2f} | {(avs < a.mean()).mean()*100:.1f} |"]
    # per year: tier PF vs cell-weighted non-tier PF (each non-tier trade weighted n_tier_cell / n_nontier_cell within the year)
    out += ["", "| year | n tier | PF tier | avg% tier | PF non-tier (cell-weighted) | avg% non-tier (cell-weighted) |", "|---|---|---|---|---|---|"]
    for y in YEARS:
        my = YR == y; ta = R[prem & my]; w = np.zeros(len(B))
        for c in np.unique(gvr_idx[prem & my]):
            kt = (prem & my & (gvr_idx == c)).sum(); kn = (nont & my & (gvr_idx == c)).sum()
            if kn: w[nont & my & (gvr_idx == c)] = kt / kn
        rw = R * w; g_, l_ = rw[rw > 0].sum(), -rw[rw < 0].sum()
        out.append(f"| {y} | {len(ta)} | {f(pf(ta))} | {ta.mean():+.2f} | {f(g_/l_ if l_ else np.inf)} | {(rw.sum()/w.sum()) if w.sum() else np.nan:+.2f} |")
    for flab, fm in [("fit all", all_m), ("fit 2020-23", early), ("fit 2024-26", late)]:
        tm = mults(fm & (ti == 1), tgv, len(TGL) * len(TVL), fm)
        out += ["", f"tier's own map gap3 × volat4 ({flab}; multiplier = cell trimPF-1 / BOOK trimPF-1; 1.00 = < 50 trades): ", "| gap \\ volat | " + " | ".join(TVL) + " |", "|---|" + "---|" * len(TVL)]
        for g, gl in enumerate(TGL):
            out.append(f"| {gl} | " + " | ".join(f"{tm[g * len(TVL) + v]:.2f} ({((tgi==g)&(tvi==v)&(ti==1)).sum()})" for v in range(len(TVL))) + " |")
    gvt_m = mults(all_m, gv * 2 + ti, len(GL) * len(VL) * 2)
    out += ["", "## 6c. gap (rows) × volat (cols) multipliers, S tier cells vs book cells (fit all; 1.00 = fewer than 50 trades)"]
    for t, tl in enumerate(TL):
        out += [f"\n{tl}:", "| gap \\ volat | " + " | ".join(VL) + " |", "|---|" + "---|" * len(VL)]
        for g, gl in enumerate(GL):
            out.append(f"| {gl} | " + " | ".join(f"{gvt_m[(g * len(VL) + v) * 2 + t]:.2f} ({((gi==g)&(vi==v)&(ti==t)).sum()})" for v in range(len(VL))) + " |")
out += ["", f"## 6f. rate600 bands ({args.measure} multipliers; net of cost)", "| band | n | PF | trimPF-1 | avg% | worst | tail<-20% | mult all | mult 20-23 | mult 24-26 | " + " | ".join(str(y) for y in YEARS) + " |", "|---|---|---|---|---|---|---|---|---|---|" + "---|" * len(YEARS)]
ma, me, ml = mults(all_m, ri, len(RL)), mults(early, ri, len(RL), early), mults(late, ri, len(RL), late)
for k, lab in enumerate(RL):
    m = ri == k; r = R[m]
    out.append(f"| {lab} | {len(r):,} | {f(pf(r),3)} | {f(tpf1(r),3)} | {r.mean():+.2f} | {r.min():.1f} | {(r < -20).mean()*100:.2f}% | {ma[k]:.2f} | {me[k]:.2f} | {ml[k]:.2f} | " + " | ".join(f"{f(pf(r[YR[m]==y]),2)}" for y in YEARS) + " |")
out += ["", f"## 6g. Is rate600 explained by gap × volat? Each band vs CELL-WEIGHTED peers in the same gap × volat cells ({args.measure})",
        "| band | n | own PF-1 (all) | peers PF-1 (all) | within-cell ratio all | ratio fit 20-23 | ratio fit 24-26 | avg% own / peers (all) |", "|---|---|---|---|---|---|---|---|"]
for k, lab in enumerate(RL):
    bm = ri == k; o, pe, pa = within_cell(bm, all_m); oe, pee, _ = within_cell(bm, early); ol, pel, _ = within_cell(bm, late)
    out.append(f"| {lab} | {bm.sum():,} | {o:.3f} | {pe:.3f} | {o/pe:.2f} | {oe/pee:.2f} | {ol/pel:.2f} | {R[bm].mean():+.2f} / {pa:+.2f} |")
out += ["", "composition — share of each rate600 band by gap bucket (row %):", "| band | " + " | ".join(GL) + " |", "|---|" + "---|" * len(GL)]
for k, lab in enumerate(RL):
    bm = ri == k; out.append(f"| {lab} | " + " | ".join(f"{(bm & (gi == g)).sum() / bm.sum() * 100:.0f}" for g in range(len(GL))) + " |")
out += ["", "composition — by volat bucket (row %):", "| band | " + " | ".join(VL) + " |", "|---|" + "---|" * len(VL)]
for k, lab in enumerate(RL):
    bm = ri == k; out.append(f"| {lab} | " + " | ".join(f"{(bm & (vi == v)).sum() / bm.sum() * 100:.0f}" for v in range(len(VL))) + " |")
out += ["", "rate600 gradient INSIDE the big gap × volat cells (PF-1 of slow leg / mid / fast; n):", "| gap / volat | " + " | ".join(RL) + " |", "|---|" + "---|" * len(RL)]
for g in range(len(GL)):
    for v in range(len(VL)):
        cm = (gi == g) & (vi == v)
        if cm.sum() < 600: continue
        out.append(f"| {GL[g]} / {VL[v]} | " + " | ".join(f"{f(pf1(R[cm & (ri == k)]),2)} ({(cm & (ri == k)).sum()})" for k in range(len(RL))) + " |")
# 6h. the RAW joint: rate600 bands inside the 2x2 gap<4 / >=4 x volat<90 / >=90 (user, 2026-09-09)
out += ["", f"## 6h. rate600 bands INSIDE the 2×2 (gap<4 / ≥4 × volat<90 / ≥90) — raw data, net of credit",
        "| cell | rate600 | n | PF | PF-1 | trimPF-1 | avg% | win% | worst | tail<-20% | net | PF 20-23 | PF 24-26 | " + " | ".join(str(y) for y in YEARS) + " |", "|---|---|---|---|---|---|---|---|---|---|---|---|---|" + "---|" * len(YEARS)]
for gl, gm in [("gap<4", B.gap60.values < 4), ("gap>=4", B.gap60.values >= 4)]:
    for vl, vm in [("volat<90", B.volat.values < 90), ("volat>=90", B.volat.values >= 90)]:
        cm = gm & vm
        for k, lab in list(enumerate(RL)) + [(None, "ALL")]:
            m = cm if k is None else cm & (ri == k); r = R[m]
            out.append(f"| {gl} & {vl} | {lab} | {len(r):,} | {f(pf(r),3)} | {pf(r)-1:.3f} | {f(tpf1(r),3)} | {r.mean():+.2f} | {(r>0).mean()*100:.1f} | {r.min():.1f} | {(r<-20).mean()*100:.2f}% | {r.sum():,.0f} | {f(pf(r[YR[m]<=2023]),2)} | {f(pf(r[YR[m]>2023]),2)} | " + " | ".join(f"{f(pf(r[YR[m]==y]),2)} ({(YR[m]==y).sum()})" for y in YEARS) + " |")
out += rows + ["", "## 8. The multiplier maps (fit on all years)",
               "volat: " + ", ".join(f"{l} {m:.2f}" for l, m in zip(VL, mults(all_m, vi, len(VL)))),
               "coil: " + ", ".join(f"{l} {m:.2f}" for l, m in zip(CL, mults(all_m, ci, len(CL)))),
               "gap: " + ", ".join(f"{l} {m:.2f}" for l, m in zip(GL, mults(all_m, gi, len(GL)))),
               "rate600: " + ", ".join(f"{l} {m:.2f}" for l, m in zip(RL, mults(all_m, ri, len(RL))))]
gvm = mults(all_m, gv, len(GL) * len(VL))
out += ["", f"## 8b. THE gap × volat multiplier grid ({args.measure}, fit all, clip [0.25, 4]; cell multiplier (n); 1.00 = < 50 trades)", "| gap \\ volat | " + " | ".join(VL) + " |", "|---|" + "---|" * len(VL)]
for g, gl in enumerate(GL):
    out.append(f"| {gl} | " + " | ".join(f"{gvm[g * len(VL) + v]:.2f} ({((gi==g)&(vi==v)).sum():,})" for v in range(len(VL))) + " |")
out += ["", "## 8c. gap × volat — MEDIAN trade % (net of credit): all years · fit 20–23 · fit 24–26 (n)", "| gap \\ volat | " + " | ".join(VL) + " |", "|---|" + "---|" * len(VL)]
for g, gl in enumerate(GL):
    cells = []
    for v in range(len(VL)):
        m = (gi == g) & (vi == v)
        if m.sum() < 50: cells.append(f"– ({m.sum()})"); continue
        cells.append(f"{np.median(R[m]):+.2f} · {np.median(R[m & early]):+.2f} · {np.median(R[m & late]):+.2f} ({m.sum():,})")
    out.append(f"| {gl} | " + " | ".join(cells) + " |")
out += ["", "## 8d. gap × volat — MEAN trade % (net of credit): all · 20–23 · 24–26", "| gap \\ volat | " + " | ".join(VL) + " |", "|---|" + "---|" * len(VL)]
for g, gl in enumerate(GL):
    cells = []
    for v in range(len(VL)):
        m = (gi == g) & (vi == v)
        if m.sum() < 50: cells.append("–"); continue
        cells.append(f"{R[m].mean():+.2f} · {R[m & early].mean():+.2f} · {R[m & late].mean():+.2f}")
    out.append(f"| {gl} | " + " | ".join(cells) + " |")
out += ["", "## 8e. gap × volat — WIN % : all · 20–23 · 24–26", "| gap \\ volat | " + " | ".join(VL) + " |", "|---|" + "---|" * len(VL)]
for g, gl in enumerate(GL):
    cells = []
    for v in range(len(VL)):
        m = (gi == g) & (vi == v)
        if m.sum() < 50: cells.append("–"); continue
        cells.append(f"{(R[m]>0).mean()*100:.0f} · {(R[m & early]>0).mean()*100:.0f} · {(R[m & late]>0).mean()*100:.0f}")
    out.append(f"| {gl} | " + " | ".join(cells) + " |")
out += ["", "## 9. gap_60 (rows) × rate600 (cols) — trimPF-1", grid("gap \\ rate600", gi, GL, ri, RL, st_t),
        "", "## 9b. volat (rows) × rate600 (cols) — trimPF-1", grid("volat \\ rate600", vi, VL, ri, RL, st_t)]
txt = "\n".join(out); os.makedirs(os.path.dirname(args.out), exist_ok=True); open(args.out, "w").write(txt); print(txt); log(f"wrote {args.out}")
