"""S49ad — a STRUCTURED sizing model for the broad book (user, 2026-09-09): estimate the combination, don't assume it.

Every trade's edge decomposes as  E[r] = p·W − (1−p)·L  (p = win rate, W = mean win, L = mean |loss|), and the sizing
measure the user chose, PF−1 = p / ((1−p)·λ) − 1 with λ = L/W, is the edge per unit of expected loss (W cancels).
The gap × volat medians (S49 §8c–f) showed volat drives W, gap drives λ, both mildly move p. So: three small additive
models on the COMPONENTS, fitted by IRLS on all trades at once (no per-cell estimates):
    logit p  = x·β_p        (binomial)
    log  W   = x·β_W        (log-link quasi-Poisson on the winners)
    log  L   = x·β_L        (log-link quasi-Poisson on the losers)
multiplier ∝ PF−1(x) from the fitted p, W, L; normalised to mean 1 on the apply set (equal exposure), clipped [0.25, 4].

Two designs:  A = band DUMMIES (gap 4 bands, volat 5, rate600 2, tier 2 → 10 params per model, no shape assumption)
              B = one SLOPE per feature (log(1+gap), log volat, rate flag, tier flag → 5 params, smooth & monotone)
              A+I = A plus the one known interaction (premium cell gap<4 ∧ volat≥90 × rate600)
Compared with the flat book and the all-marginal PF−1 product (S49ab) on the same year-block holdouts.

Run from research/:  python -u scripts/equity/flushfader_sizing_model.py --credit 0.001 --rule140
"""
import argparse, os, time
import numpy as np, pandas as pd
from numba import njit

ap = argparse.ArgumentParser()
ap.add_argument("--slice", default="data/flushfader_review_slice.parquet")
ap.add_argument("--door", type=int, default=40)
ap.add_argument("--credit", type=float, default=0.001)
ap.add_argument("--rule140", action="store_true")
ap.add_argument("--trim", type=float, default=0.05)
ap.add_argument("--out", default="data/flushfader_gate_review/sizing_model.md")
args = ap.parse_args()
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:6.1f}s]", *a, flush=True)

# ---------------------------------------------------------------- the broad book (identical to flushfader_sizing_broad.py --halts)
D = pd.read_parquet(args.slice, columns=["tkd", "yr", "signal_sec", "ent", "ext", "ret", "volat", "px", "gap60",
                                        "l180", "l300", "eff10a", "z20", "v10r", "crf", "consol_5m_lag1m", "rate600", "ht", "ssh"])
N = len(D)
def nz(m): return np.where(np.isnan(m.astype(float)), False, m).astype(bool)
M = (nz(D.volat.values >= 40) & nz(D.signal_sec.values <= 54000) & nz(D.gap60.values < args.door) & nz(D.px.values >= 1)
     & nz(D.l300.values >= 6) & nz(D.eff10a.values >= 0.15) & nz(D.v10r.values >= 0.75) & nz(D.z20.values < -1.5)
     & nz(D.l180.values >= 3) & nz(D.crf.values <= -0.002) & nz(D.consol_5m_lag1m.values <= 0.22))
if args.rule140: M &= ~(nz(D.volat.values >= 140) & nz(D.gap60.values >= 4))
M &= ~(nz(D.ht.values >= 4) & nz(D.ssh.values < 300))
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
R = B.ret.values * 100 + 2 * args.credit / B.px.values * 100      # % per trade net of the credit
YR = B.yr.values; YEARS = sorted(set(YR)); n = len(B)
log(f"broad book {n:,} trades, credit ${args.credit}/sh/side, rule140={args.rule140}, wait ht>=4&ssh<300")

# ---------------------------------------------------------------- features
GB = [0, 1, 4, 13, 40]; GL = ["0", "1-3", "4-12", "13-39"]
VB = [40, 60, 90, 140, 250, 1e9]; VL = ["40-60", "60-90", "90-140", "140-250", "250+"]
gi = np.digitize(B.gap60.values, GB[1:-1]); vi = np.digitize(B.volat.values, VB[1:-1])
ri = (np.nan_to_num(B.rate600.values, nan=0.0) >= 0.07).astype(int)                  # 0 = slow (<.07), 1 = fast
ti = (nz(B.ht.values >= 1) & nz(B.ssh.values >= 300) & nz(B.ssh.values < 2400)).astype(int)
prem = ((B.gap60.values < 4) & (B.volat.values >= 90)).astype(int)
def X_dummies(inter=False, dense_vol=False):
    cols = [np.ones(n)]; names = ["1"]
    for k in range(1, len(GL)): cols.append((gi == k).astype(float)); names.append(f"gap {GL[k]}")
    for k in range(1, len(VL)): cols.append((vi == k).astype(float)); names.append(f"volat {VL[k]}")
    cols.append(ri.astype(float)); names.append("rate600 fast")
    cols.append(ti.astype(float)); names.append("S tier")
    if inter: cols.append((prem * ri).astype(float)); names.append("premium cell × fast")
    if dense_vol: cols.append(prem.astype(float)); names.append("dense × volatile (gap<4 ∧ volat≥90)")
    return np.column_stack(cols), names
def X_hybrid():   # C: the shapes model A revealed — gap is a STEP at 0, volat a power law, rate600 and tier flags
    cols = [np.ones(n), (gi == 0).astype(float), np.log(B.volat.values.astype(float) / 100.0), ri.astype(float), ti.astype(float)]
    return np.column_stack(cols), ["1", "gap = 0", "log(volat/100)", "rate600 fast", "S tier"]
def X_hybrid2():   # C2: step at 0 PLUS a slope over the non-zero gaps
    cols = [np.ones(n), (gi == 0).astype(float), np.log1p(B.gap60.values.astype(float)), np.log(B.volat.values.astype(float) / 100.0), ri.astype(float), ti.astype(float)]
    return np.column_stack(cols), ["1", "gap = 0", "log(1+gap)", "log(volat/100)", "rate600 fast", "S tier"]
def X_hybrid3():   # C3: step at 0 plus a second step at 13+
    cols = [np.ones(n), (gi == 0).astype(float), (gi == 3).astype(float), np.log(B.volat.values.astype(float) / 100.0), ri.astype(float), ti.astype(float)]
    return np.column_stack(cols), ["1", "gap = 0", "gap >= 13", "log(volat/100)", "rate600 fast", "S tier"]
def X_hybrid4():   # C4: gap band DUMMIES (as in A) + log volat slope + the two flags
    cols = [np.ones(n)] + [(gi == k).astype(float) for k in range(1, len(GL))] + [np.log(B.volat.values.astype(float) / 100.0), ri.astype(float), ti.astype(float)]
    return np.column_stack(cols), ["1"] + [f"gap {GL[k]}" for k in range(1, len(GL))] + ["log(volat/100)", "rate600 fast", "S tier"]
def X_slopes():
    cols = [np.ones(n), np.log1p(B.gap60.values.astype(float)), np.log(B.volat.values.astype(float)), ri.astype(float), ti.astype(float)]
    return np.column_stack(cols), ["1", "log(1+gap)", "log volat", "rate600 fast", "S tier"]

# ---------------------------------------------------------------- IRLS
def fit_logistic(X, y, w=None, it=50):
    w = np.ones(len(y)) if w is None else w; b = np.zeros(X.shape[1])
    for _ in range(it):
        eta = X @ b; mu = 1 / (1 + np.exp(-eta)); W_ = w * mu * (1 - mu) + 1e-12
        z = eta + (y - mu) / (mu * (1 - mu) + 1e-12)
        b_new = np.linalg.solve((X * W_[:, None]).T @ X, (X * W_[:, None]).T @ z)
        if np.max(np.abs(b_new - b)) < 1e-9: b = b_new; break
        b = b_new
    return b
def fit_loglink(X, y, it=50):   # quasi-Poisson: E[y] = exp(X b), y > 0 continuous
    b = np.zeros(X.shape[1]); b[0] = np.log(y.mean())
    for _ in range(it):
        eta = X @ b; mu = np.exp(eta); z = eta + (y - mu) / mu
        b_new = np.linalg.solve((X * mu[:, None]).T @ X, (X * mu[:, None]).T @ z)
        if np.max(np.abs(b_new - b)) < 1e-9: b = b_new; break
        b = b_new
    return b
def fit_model(X, fit):
    win = R > 0
    bp = fit_logistic(X[fit], win[fit].astype(float))
    bw = fit_loglink(X[fit & win], R[fit & win])
    bl = fit_loglink(X[fit & ~win], -R[fit & ~win])
    return bp, bw, bl
def predict(X, b):
    bp, bw, bl = b
    p = 1 / (1 + np.exp(-(X @ bp))); W = np.exp(X @ bw); L = np.exp(X @ bl)
    lam = L / W; pf1 = p / ((1 - p) * lam) - 1; er = p * W - (1 - p) * L
    return p, W, L, lam, pf1, er

# ---------------------------------------------------------------- stats
def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
def tpf1(r):
    if len(r) < 20: return np.nan
    return pf(r[r >= np.quantile(r, args.trim)]) - 1
def f(x, d=3): return "inf" if np.isinf(x) else ("nan" if np.isnan(x) else f"{x:.{d}f}")
def maxdd(x):
    c = np.cumsum(x); return float((np.maximum.accumulate(c) - c).max())
def sim(w, app, label):
    r = R[app]; w = w[app]; w = w / w.mean(); s = w * r
    yrs = " ".join(f"{f(pf(s[YR[app] == y]),2)}" for y in YEARS)
    return (f"| {label} | {len(r):,} | {f(pf(r))} / {f(pf(s))} | {f(tpf1(r))} / {f(tpf1(s))} | {r.mean():+.2f} / {s.mean():+.2f} | {r.sum():,.0f} / {s.sum():,.0f} | "
            f"{maxdd(r):,.0f} / {maxdd(s):,.0f} | {r.min():.1f} / {s.min():.1f} | {(w > 2).mean()*100:.1f}% / {(w < 0.5).mean()*100:.1f}% | {yrs} |")
HDR = ("| map | n | PF flat / sized | trimPF-1 flat / sized | avg% flat / sized | net flat / sized | maxDD flat / sized | worst flat / sized | share >2x / <0.5x | sized PF by year |\n"
       "|---|---|---|---|---|---|---|---|---|---|")

# the S49ab all-marginal PF-1 product, for reference
def pf1(r): return pf(r) - 1
def ladder(fit, idx, k):
    base = pf1(R[fit]); m = np.ones(k)
    for j in range(k):
        r = R[fit & (idx == j)]
        if len(r) >= 50 and np.isfinite(pf1(r)): m[j] = pf1(r) / base
    return np.clip(m, 0.25, 4.0)
def marginal_product(fit):
    return np.minimum(ladder(fit, gi, 4)[gi] * ladder(fit, vi, 5)[vi] * ladder(fit, ri, 2)[ri] * ladder(fit, ti, 2)[ti], 4.0)

out = [f"# S49ad — structured sizing model on the broad book ({n:,} trades, credit ${args.credit}/sh/side{', rule140' if args.rule140 else ''}, wait ht>=4&ssh<300)\n",
       "Components fitted by IRLS on ALL trades of the fit set: logit p (win rate), log W (mean win), log L (mean |loss|). "
       "multiplier ∝ PF−1 = p/((1−p)·L/W) − 1, mean-1 normalised on the apply set, clipped [0.25, 4].\n"]
all_m = np.ones(n, bool); early = YR <= 2023; late = YR > 2023
XA, NA = X_dummies(); XI, NI = X_dummies(inter=True); XB, NB = X_slopes(); XD, ND = X_dummies(dense_vol=True); XC, NC = X_hybrid(); XC2, NC2 = X_hybrid2(); XC3, NC3 = X_hybrid3(); XC4, NC4 = X_hybrid4()

# ---- coefficients on all years (as RATIOS: exp(b) for W/L, odds-ratio for p)
for lab, X, names in [("A: band dummies", XA, NA), ("A+I: dummies + premium×fast interaction", XI, NI), ("A+D: dummies + dense×volatile interaction", XD, ND), ("B: one slope per feature", XB, NB), ("C: hybrid (gap=0 flag, log volat, two flags)", XC, NC), ("C2: C + log(1+gap) slope", XC2, NC2), ("C3: C + gap>=13 step", XC3, NC3), ("C4: gap dummies + log volat + flags", XC4, NC4)]:
    bp, bw, bl = fit_model(X, all_m)
    out += [f"## Model {lab} — coefficients on all years (p: odds ratio · W: ×mean win · L: ×mean loss · λ ratio = L/W)\n",
            "| term | p odds ratio | W × | L × | λ × |", "|---|---|---|---|---|"]
    for j, nm in enumerate(names):
        if nm == "1": out.append(f"| intercept | p = {1/(1+np.exp(-bp[0])):.3f} | W = {np.exp(bw[0]):.2f}% | L = {np.exp(bl[0]):.2f}% | λ = {np.exp(bl[0]-bw[0]):.2f} |")
        else: out.append(f"| {nm} | {np.exp(bp[j]):.3f} | {np.exp(bw[j]):.3f} | {np.exp(bl[j]):.3f} | {np.exp(bl[j]-bw[j]):.3f} |")
    out.append("")

# ---- the fitted gap × volat grid (model A, rate600 slow, not tier) next to the S49ab ladders
bA = fit_model(XA, all_m)
out += ["## Fitted PF−1 multiplier grid, model A (rate600 slow, no tier), relative to the book's fitted PF−1; S49ab product of ladders in brackets\n",
        "| gap \\ volat | " + " | ".join(VL) + " |", "|---|" + "---|" * len(VL)]
Xg = np.zeros((len(GL) * len(VL), XA.shape[1])); Xg[:, 0] = 1
for g in range(len(GL)):
    for v in range(len(VL)):
        row = g * len(VL) + v
        if g: Xg[row, g] = 1
        if v: Xg[row, len(GL) - 1 + v] = 1
_, _, _, _, pf1g, erg = predict(Xg, bA)
book_pf1 = predict(XA, bA)[4].mean()
lg, lv = ladder(all_m, gi, 4), ladder(all_m, vi, 5)
for g, gl in enumerate(GL):
    out.append(f"| {gl} | " + " | ".join(f"{pf1g[g*len(VL)+v]/book_pf1:.2f} [{lg[g]*lv[v]:.2f}]" + (" –" if ((gi == g) & (vi == v)).sum() < 50 else "") for v in range(len(VL))) + " |")
bD = fit_model(XD, all_m); XgD = np.column_stack([Xg, np.array([1.0 if (g < 2 and v >= 2) else 0.0 for g in range(len(GL)) for v in range(len(VL))])])
pf1gD = predict(XgD, bD)[4]; book_pf1D = predict(XD, bD)[4].mean()
out += ["", "Fitted PF−1 multiplier grid, model A+D (with the dense×volatile term):", "| gap \\ volat | " + " | ".join(VL) + " |", "|---|" + "---|" * len(VL)]
for g, gl in enumerate(GL):
    out.append(f"| {gl} | " + " | ".join(f"{pf1gD[g*len(VL)+v]/book_pf1D:.2f}" for v in range(len(VL))) + " |")
out += ["", "Fitted E[r] % per trade on the same grid (model A):", "| gap \\ volat | " + " | ".join(VL) + " |", "|---|" + "---|" * len(VL)]
for g, gl in enumerate(GL):
    out.append(f"| {gl} | " + " | ".join(f"{erg[g*len(VL)+v]:+.2f}" for v in range(len(VL))) + " |")
out.append("")

# ---- sized vs flat, in-sample and holdouts
out += ["## Sized vs flat at equal exposure\n", HDR]
for lab, fit, app in [("in-sample (fit all, apply all)", all_m, all_m), ("holdout: fit 2020-23 → apply 2024-26", early, late), ("holdout: fit 2024-26 → apply 2020-23", late, early)]:
    out.append(f"| **{lab}** | | | | | | | | | |")
    out.append(sim(marginal_product(fit), app, "S49ab all-marginal PF-1 product, clip 4"))
    for mlab, X in [("model A (dummies)", XA), ("model A+I (dummies + premium×fast)", XI), ("model A+D (dummies + dense×volatile)", XD), ("model B (slopes)", XB), ("model C (hybrid)", XC), ("model C2 (step + slope)", XC2), ("model C3 (two steps)", XC3), ("model C4 (gap dummies + log volat)", XC4)]:
        b = fit_model(X, fit); p, W, L, lam, pf1x, er = predict(X, b)
        base = pf1x[fit].mean()
        out.append(sim(np.clip(pf1x / base, 0.25, 4.0), app, f"{mlab}: ∝ PF-1"))
        if mlab.startswith("model A+D"):
            for pw in (1.5, 2.0):
                out.append(sim(np.clip((pf1x / base) ** pw, 0.25, 4.0), app, f"{mlab}: ∝ PF-1 ^ {pw} (steepness control)"))
        out.append(sim(np.clip(er / er[fit].mean(), 0.25, 4.0), app, f"{mlab}: ∝ E[r] (diagnostic, NOT the rule)"))
txt = "\n".join(out); os.makedirs(os.path.dirname(args.out), exist_ok=True); open(args.out, "w").write(txt); print(txt); log(f"wrote {args.out}")
