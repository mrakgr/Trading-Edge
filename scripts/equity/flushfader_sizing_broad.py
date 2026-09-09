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
def f(x, d=2):
    return "inf" if np.isinf(x) else ("nan" if np.isnan(x) else f"{x:.{d}f}")

VB = [40, 60, 90, 140, 250, 1e9]; VL = ["40-60", "60-90", "90-140", "140-250", "250+"]
CB = [-1, 0.05, 0.10, 0.15, 0.221]; CL = ["<.05", ".05-.10", ".10-.15", ".15-.22"]
GB = [0, 1, 4, 8, 13, 20, 30, 40]; GL = ["0", "1-3", "4-7", "8-12", "13-19", "20-29", "30-39"]
vi = np.digitize(B.volat.values, VB[1:-1]); ci = np.digitize(B.consol_5m_lag1m.values, CB[1:-1]); gi = np.digitize(B.gap60.values, GB[1:-1])
RB = [0, 0.044, 0.07, 0.125, 1.01]; RL = ["<.044", ".044-.07", ".07-.125", ".125+"]   # rate600 quartile-ish bands (S49n)
ri = np.digitize(np.nan_to_num(B.rate600.values, nan=0.0), RB[1:-1])
TGB = [0, 1, 4]; TGL = ["0", "1-3", "4+"]; TVB = [40, 90, 140, 250]; TVL = ["<90", "90-140", "140-250", "250+"]   # the tier's coarse grid
tgi = np.digitize(B.gap60.values, TGB[1:]); tvi = np.digitize(B.volat.values, TVB[1:]); tgv = tgi * len(TVL) + tvi
TL = ["book", "S tier"]; ti = (nz(B.ht.values >= 1) & nz(B.ssh.values >= 300) & nz(B.ssh.values < 2400)).astype(int)

out = [f"# S49k — sizing the broad book: step-7 spec, gap < {args.door}, no vote; {len(B):,} trades; {COSTLAB}{'; RULE volat>=140 only if gap<4' if args.rule140 else ''}\n",
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
def mults(fit_mask, cells_fn, ncell, base_mask=None):
    """base = the BOOK's trimPF-1 on the fit years (base_mask), so sub-population maps stay on the book's scale."""
    m = np.ones(ncell); base = tpf1(R[fit_mask if base_mask is None else base_mask])
    for k in range(ncell):
        r = R[fit_mask & (cells_fn == k)]
        m[k] = (tpf1(r) / base) if len(r) >= 50 and np.isfinite(tpf1(r)) else 1.0
    return np.clip(m, 0.25, 4.0)
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
def apply(m, idx): return m[idx]
rows = ["## 7. Sized vs flat at EQUAL average exposure (multipliers = cell trimPF-1 / book trimPF-1, clipped [0.25, 4])", HDR]
for lab, fit, app in [("in-sample (fit all, apply all)", all_m, all_m), ("holdout: fit 2020-23 → apply 2024-26", early, late),
                      ("holdout: fit 2024-26 → apply 2020-23", late, early)]:
    rows.append(f"| **{lab}** | | | | | | | | |")
    rows.append(sim(apply(mults(fit, vi, len(VL)), vi), app, "volat only"))
    rows.append(sim(apply(mults(fit, ci, len(CL)), ci), app, "coil only"))
    rows.append(sim(apply(mults(fit, vc, len(VL) * len(CL)), vc), app, "volat × coil"))
    rows.append(sim(apply(mults(fit, gi, len(GL)), gi), app, "gap only"))
    gvc = (gi * len(VL) + vi) * len(CL) + ci
    rows.append(sim(apply(mults(fit, gvc, len(GL) * len(VL) * len(CL)), gvc), app, "gap × volat × coil"))
    gv = gi * len(VL) + vi
    rows.append(sim(apply(mults(fit, gv, len(GL) * len(VL)), gv), app, "gap × volat"))
    rows.append(sim(apply(mults(fit, ri, len(RL)), ri), app, "rate600 only"))
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
            for tier_lab, tier_idx, tier_n in [("gap3 × volat4", tgv, len(TGL) * len(TVL)), ("volat4", tvi, len(TVL)), ("flat 1 cell", np.zeros(len(B), int), 1)]:
                w = np.where(ti == 1, apply(mults(fit & (ti == 1), tier_idx, tier_n, fit), tier_idx), apply(mults(fit & (ti == 0), rest_idx, rest_n, fit), rest_idx))
                rows.append(sim(w, app, f"SPLIT: rest = {rest_lab} (fit on rest) · tier = {tier_lab} (fit on tier)"))
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
out += rows + ["", "## 8. The multiplier maps (fit on all years)",
               "volat: " + ", ".join(f"{l} {m:.2f}" for l, m in zip(VL, mults(all_m, vi, len(VL)))),
               "coil: " + ", ".join(f"{l} {m:.2f}" for l, m in zip(CL, mults(all_m, ci, len(CL)))),
               "gap: " + ", ".join(f"{l} {m:.2f}" for l, m in zip(GL, mults(all_m, gi, len(GL)))),
               "rate600: " + ", ".join(f"{l} {m:.2f}" for l, m in zip(RL, mults(all_m, ri, len(RL))))]
out += ["", "## 9. gap_60 (rows) × rate600 (cols) — trimPF-1", grid("gap \\ rate600", gi, GL, ri, RL, st_t),
        "", "## 9b. volat (rows) × rate600 (cols) — trimPF-1", grid("volat \\ rate600", vi, VL, ri, RL, st_t)]
txt = "\n".join(out); os.makedirs(os.path.dirname(args.out), exist_ok=True); open(args.out, "w").write(txt); print(txt); log(f"wrote {args.out}")
