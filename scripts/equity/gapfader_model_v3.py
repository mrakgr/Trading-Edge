"""GF-67 → PRODUCTION: GapFader's sizing model v3 = GF-54's + two flags (user 2026-11-05: "let's go for the narrow flags model.
We'll deploy it tomorrow").
  deep10   the stock's gap at the LOO decision <= −10 % (research: gap_est, the noisy auction estimate; live: the auction
           price's holder's return, GapDecision.Gap)
  d10xq1   deep10 AND QQQ's premarket gap at 09:29:25 <= −1 % (the holder's return from QQQ's previous close to its last 1s
           vwap before 09:29:25; live: L1's cutter, Engine/GapFader.fs MarketGap; no QQQ price = 0)
Everything else is GF-54's (gapfader_model_v2.py): logit p, log W, log L by IRLS on 2021-01 .. the book's end; lm60, lrv60
winsorised 1/99 % on the fit; every term standardised (the flags too, as rv5_60); PF−1 = p / ((1 − p) · L / W) − 1. The fit
is GF-67's (gapfader_qqq_sizing.py, model "+ deep10 + deep10 × QQQ ≤ −1 %"); this script writes it out.
OUTPUT: models/gapfader/gf_coefficients.json — production's (Engine/GapFader.fs's Model, pinned by the Oms's tests).
GF-54's production file is kept as gf_coefficients_pre_gf67.json (gapfader_model_v2.py now writes gf_coefficients_gf54.json).
200 pins, stratified: 50 from each (deep10, QQQ ≤ −1 %) cell of the fit set.
    cd research && python3 scripts/equity/gapfader_model_v3.py > data/equity/gapfader/model_v3.md
"""
import json, os, shutil
import numpy as np, pandas as pd
C = pd.read_parquet("data/equity/gapfader/qqq_sizing.parquet")      # GF-67's: GF-54's book + qqq_pm_gap
R = C.r1.values; WIN = R > 0; YR = C.date.str[:4].astype(int).values; FIT = YR >= 2021
deep = (C.gap_est.values <= -0.10) * 1.0; qd = (C.qqq_pm_gap.values <= -0.01) * 1.0
RAW = {"lm60": np.log(C.dv60_med.values), "lrv60": np.log(C.rvol60.values + 0.001), "rv5_60": (C.rvol60.values < 0.005) * 1.0,
       "deep10": deep, "d10xq1": deep * qd}
FLAGS = {"rv5_60", "deep10", "d10xq1"}
TERMS = ["lm60", "lrv60", "rv5_60", "deep10", "d10xq1"]
std, cols = [], [np.ones(len(C))]
for t in TERMS:
    x = RAW[t]
    if t in FLAGS: lo = hi = None
    else: lo, hi = (float(v) for v in np.quantile(x[FIT], [0.01, 0.99])); x = np.clip(x, lo, hi)
    std.append({"term": t, "winsor": [lo, hi], "mean": float(x[FIT].mean()), "sd": float(x[FIT].std())})
    cols.append((x - x[FIT].mean()) / x[FIT].std())
X = np.column_stack(cols)
def irls(X, y, link, it=80):
    b = np.zeros(X.shape[1])
    if link == "log": b[0] = np.log(y.mean())
    for _ in range(it):
        eta = X @ b
        if link == "logit": mu = 1 / (1 + np.exp(-eta)); w = mu * (1 - mu) + 1e-12; z = eta + (y - mu) / w
        else: mu = np.exp(eta); w = mu; z = eta + (y - mu) / mu
        bn = np.linalg.solve((X * w[:, None]).T @ X + 1e-9 * np.eye(X.shape[1]), (X * w[:, None]).T @ z)
        if np.max(np.abs(bn - b)) < 1e-10: return bn
        b = bn
    return b
bp = irls(X[FIT], WIN[FIT] * 1.0, "logit"); bw = irls(X[FIT & WIN], R[FIT & WIN], "log"); bl = irls(X[FIT & ~WIN], -R[FIT & ~WIN], "log")
p = 1 / (1 + np.exp(-(X @ bp))); mm = p / ((1 - p) * np.exp(X @ bl) / np.exp(X @ bw)) - 1
rng = np.random.default_rng(67)
pins_idx = np.concatenate([rng.choice(np.where(FIT & (deep == a) & (qd == b))[0], 50, replace=False) for a in (0, 1) for b in (0, 1)])
COEF = {"model": "GapFader (weak-premarket gap-down long) PF−1 sizing, docs/gapfader_results.md GF-67: GF-54's + deep10 + deep10 × QQQ ≤ −1 %",
        "fit": f"2021-01 .. {C.date.max()[:7]}, n={int(FIT.sum())}; trade = LOO, ONE target at 50 % of the gap to 10:00, then the 10:00 1s vwap; 0.40c/sh",
        "gates": {"gap_max": -0.04, "open_min": 1.0, "dv60_med_min": 5e5, "rvol_max": 0.02, "no_early_close": True, "listings": ["XNAS", "XASE"]},
        "cap": "MaxNotional = 0.0025 × dv60_med",
        "indicator_cutoff_et": {"XNAS": "09:29:25", "XASE": "09:29:50"}, "premarket_from_et": "04:00:00",
        "dv60_med": "median(volume × close) over the 60 PRIOR sessions in the ticker's episode (a > 45-day gap starts one); fewer when younger",
        "vol60_med": "median(volume / n) over the same 60 sessions × n(D): shares in D's scale",
        "rvol": "premarket volume 04:00 → the listing's cutoff / vol60_med",
        "gap": "the holder's return close(D−1) → the auction price at the decision (research: gap_est)",
        "qqq_gap": "QQQ's holder's return close(D−1) → its last 1s vwap before 09:29:25 (both listings); none = not ≤ −1 %",
        "terms": {"lm60": "ln(dv60_med)", "lrv60": "ln(rvol + 0.001)", "rv5_60": "1 if rvol < 0.005 else 0",
                  "deep10": "1 if gap <= -0.10 else 0", "d10xq1": "1 if gap <= -0.10 and qqq_gap <= -0.01 else 0"},
        "standardise": std, "design": ["1"] + TERMS,
        "beta_p": [float(v) for v in bp], "beta_w": [float(v) for v in bw], "beta_l": [float(v) for v in bl],
        "pf1": "p / ((1 − p) · exp(x·β_l) / exp(x·β_w)) − 1, p = 1 / (1 + exp(−x·β_p)); x = [1, (clip(term) − mean) / sd …]",
        "fit_mean_pf1": float(mm[FIT].mean()),
        "pins": [{"ticker": C.ticker.values[i], "date": C.date.values[i], "dv60_med": float(C.dv60_med.values[i]), "rvol": float(C.rvol60.values[i]),
                  "gap": float(C.gap_est.values[i]), "qqq_gap": float(C.qqq_pm_gap.values[i]), "pf1": float(mm[i])} for i in sorted(pins_idx)]}
OUT, OLD = "models/gapfader/gf_coefficients.json", "models/gapfader/gf_coefficients_pre_gf67.json"
if not os.path.exists(OLD): shutil.copyfile(OUT, OLD)       # GF-54's, kept once
json.dump(COEF, open(OUT, "w"), indent=1)
def pf1_from_json(q, J):
    raw = {"lm60": np.log(q["dv60_med"]), "lrv60": np.log(q["rvol"] + 0.001), "rv5_60": 1.0 * (q["rvol"] < 0.005),
           "deep10": 1.0 * (q["gap"] <= -0.10), "d10xq1": 1.0 * (q["gap"] <= -0.10 and q["qqq_gap"] <= -0.01)}
    x = np.array([1.0] + [((np.clip(raw[s["term"]], *s["winsor"]) if s["winsor"][0] is not None else raw[s["term"]]) - s["mean"]) / s["sd"] for s in J["standardise"]])
    pp = 1 / (1 + np.exp(-x @ J["beta_p"])); return pp / ((1 - pp) * np.exp(x @ J["beta_l"]) / np.exp(x @ J["beta_w"])) - 1
J = json.load(open(OUT))
err = max(abs(pf1_from_json(q, J) - q["pf1"]) for q in J["pins"])
assert err < 1e-12, err
# the control: GF-67's own fit of this model (its parquet has no per-model column; refit there = the same arithmetic) — compare the cells
print("# GapFader model v3 (GF-67, the narrow flags) — production coefficients\n")
print(f"fit n = {int(FIT.sum()):,} (2021-01 .. {C.date.max()}); fit-mean PF−1 {mm[FIT].mean():.4f}; wrote {OUT} (GF-54's kept as {OLD}); "
      f"200 stratified pins, recomputed from the JSON to {err:.1e}\n")
print("| term | odds × (p) | W × | L × |\n|---|---|---|---|")
for j, t in enumerate(["intercept"] + TERMS):
    print(f"| {t} | {np.exp(bp[j]):.3f} | {np.exp(bw[j]):.3f} | {np.exp(bl[j]):.3f} |")
print("\n| cell (fit set) | n | mean predicted PF−1 | realised PF−1 |\n|---|---|---|---|")
for a in (0, 1):
    for b in (0, 1):
        s = FIT & (deep == a) & (qd == b); g, l = R[s][R[s] > 0].sum(), -R[s][R[s] < 0].sum()
        print(f"| deep10 {a} · QQQ ≤ −1 % {b} | {s.sum():,} | {mm[s].mean():.3f} | {g / l - 1:.3f} |")
