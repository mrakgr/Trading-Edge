"""GapFader GF-45 (user 2026-10-20; the premarket cutoff moved 09:28 → 09:29 → the listing's own cutoff, 09:29:25 / 09:29:50, the same day): a PF−1 SIZING MODEL for the weak-premarket gap-down long, FlushFaderV2's machinery:
logit p, log W, log L by IRLS, PF−1 = p / ((1−p)·L/W) − 1.

THE TRADE (the system as designed, docs/gapfader_oms_plan.md): a LOO bought in the opening cross; half sold at 50 % of the gap,
half at 75 % (resting sell limits, filled on a trade-through), until 10:00; what is left is sold at the first 1s vwap at/after
10:00 (GF-21's maker exit, neutral); 0.40c a share round trip (GF-21).

THE GATES (knowable at the decision, ~09:29:25 Nasdaq / ~09:29:50 NYSE American; LOO cutoff test 2026-10-20):
  gap ≤ −4 % · open ≥ $1 · dv20_prior ≥ $1M · no early close · NOT NYSE-listed (GF-27; today's listing map, delisted = kept)
  rvolc < 0.02, rvolc = premarket volume from 04:00 to the LISTING's indicator cutoff / avgvol20_prior: 09:29:25 for Nasdaq
  (LOO cutoff 09:29:30), 09:29:50 for NYSE American (freeze 09:29:55); unmapped (delisted) names get 09:29:25. (The census's
  rvol_pm runs to 09:30 — not knowable.) Bars stay 08:29–09:29 (GF-45b: they add nothing beside rvol).
THE OPEN AS THE AUCTION PRICE + NOISE (user 2026-10-19): at the decision the gap is known only through IBKR's auction price,
which on 2026-10-19 missed the official open by a median 0.10 %, p90 0.65 %. Here gap_est = open·(1+e)/pc − 1 with |e|
lognormal (median 0.10 %, p90 0.65 %), random sign; a trade needs gap_est ≤ −4 % (the decision) AND gap ≤ −4 % (the LOO at
pc × 0.96 fills); the model's gap term uses gap_est.

TERMS (standardised on the FIT set, winsorised 1/99 %):
  ldv   ln dv20_prior          lrv   ln(rvolc + 0.001)       rv5   rvolc < 0.005 (a flag)
  lb    ln(1 + bars29)         b0    bars29 = 0 (a flag)       bars29 = active 1s bars 08:29–09:29
  b1_9 / b10_29 / b30   bar-count BUCKET dummies (0 bars = the base) — the alternative to lb + b0
  gap shapes: glin −gap_est · glog ln(−gap_est) · gsqrt sqrt(−gap_est)       lpx   ln price (the open)
FITS EXCLUDE 2020 (GF-23: March 2020 is half the deep-gap edge). Holdouts: fwd = fit 2016–19 → apply 2021–26; mirror = fit
2021–26 → apply 2016–19 (2020 reported apart). Scores on the apply set:
  sized PF−1  weights clip(PF−1 / its fit-set mean, 0.25, 4), mean 1 (V2's normalised method)
  units PF−1  weights clip(PF−1, 0.125, 2) (production's unit rule, not rescaled) — and the units' total
  top / bottom third net PF (the cuts = the FIT set's terciles)
Cached tape: data/equity/gapfader/sizing_tape_cut.parquet.
Run from research/:  python -u scripts/equity/gapfader_sizing_model.py > data/equity/gapfader/sizing_model.md 2> data/equity/gapfader/sizing_model.log; echo exit $?
"""
import os, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.gap <= -0.035) & (C.rvol_pm < 0.05)].copy()
L = pd.read_parquet("data/equity/gapfader/listing_2026-10-17.parquet")
C = C.merge(L, on="ticker", how="left"); C = C[C.primary_exchange != "XNYS"].copy()
log(f"{len(C):,} candidate gap-downs (gap ≤ −3.5 %, rvol_pm < 0.05, not NYSE-listed)")
OUT = "data/equity/gapfader/sizing_tape_cut.parquet"
if os.path.exists(OUT):
    V = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []
    for i, d in enumerate(sorted(C.date.unique())):
        gd = C[C.date == d][["ticker", "open", "pc_total"]]
        con.register("gd", gd)
        V = con.execute(f"""SELECT s.ticker,
                   coalesce(sum(volume) FILTER (bucket < 34165), 0) AS pmvol_25,
                   coalesce(sum(volume) FILTER (bucket < 34190), 0) AS pmvol_50,
                   count(*) FILTER (bucket >= 30540 AND bucket < 34140) AS bars29,
                   min(bucket) FILTER (bucket >= 34200 AND v > gd.open + 0.50 * (gd.pc_total - gd.open)) AS tf_50,
                   min(bucket) FILTER (bucket >= 34200 AND v > gd.open + 0.75 * (gd.pc_total - gd.open)) AS tf_75,
                   arg_min(v, bucket) FILTER (bucket >= 36000) AS x_36000
            FROM (SELECT ticker, bucket, vwap::DOUBLE v, volume::DOUBLE volume FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                  WHERE ticker IN (SELECT ticker FROM gd) AND bucket >= 14400 AND bucket < 57600 AND volume > 0) s
            JOIN gd ON gd.ticker = s.ticker GROUP BY s.ticker""").df()
        con.unregister("gd"); V["date"] = d; parts.append(V)
        if i % 400 == 0: log(f"{i + 1} {d}")
    V = pd.concat(parts, ignore_index=True); V.to_parquet(OUT); log(f"wrote {OUT}: {len(V):,}")
C = C.merge(V, on=["ticker", "date"], how="inner")
C = C[C.x_36000.notna()].copy()
C["pmvolc"] = np.where(C.primary_exchange == "XASE", C.pmvol_50, C.pmvol_25)          # the listing's own cutoff
C["rvolc"] = C.pmvolc / C.avgvol20_prior
rng = np.random.default_rng(20261020)
e = np.exp(rng.normal(np.log(0.001), np.log(6.5) / 1.2816, len(C))) * rng.choice([-1, 1], len(C))
C["gap_est"] = C.open * (1 + e) / C.pc_total - 1
C = C[(C.gap <= -0.04) & (C.gap_est <= -0.04) & (C.rvolc < 0.02)].copy()
for f in (50, 75):
    t = C[f"tf_{f}"].astype("float64").fillna(np.inf)
    C[f"r{f}"] = np.where(t < 36000, C.open + f / 100 * (C.pc_total - C.open), C.x_36000) / C.open - 1 - 0.004 / C.open
C["r"] = 0.5 * C.r50 + 0.5 * C.r75
C = C.sort_values(["date", "ticker"]).reset_index(drop=True)
R = C.r.values * 100; WIN = R > 0; YR = C.date.str[:4].astype(int).values
RAW = {"ldv": np.log(C.dv20_prior), "lrv": np.log(C.rvolc + 0.001), "rv5": (C.rvolc < 0.005) * 1.0,
       "lb": np.log1p(C.bars29), "b0": (C.bars29 == 0) * 1.0, "lpx": np.log(C.open),
       "b1_9": ((C.bars29 >= 1) & (C.bars29 < 10)) * 1.0, "b10_29": ((C.bars29 >= 10) & (C.bars29 < 30)) * 1.0, "b30": (C.bars29 >= 30) * 1.0,
       "glin": -C.gap_est, "glog": np.log(-C.gap_est), "gsqrt": np.sqrt(-C.gap_est)}
RAW = {k: np.asarray(v, float) for k, v in RAW.items()}
assert all(np.isfinite(v).all() for v in RAW.values())
FLAGS = {"rv5", "b0", "b1_9", "b10_29", "b30"}
def design(terms, fit):
    cols = [np.ones(len(C))]
    for t in terms:
        x = RAW[t]
        if t not in FLAGS:
            lo, hi = np.quantile(x[fit], [0.01, 0.99]); x = np.clip(x, lo, hi)
        cols.append((x - x[fit].mean()) / x[fit].std())
    return np.column_stack(cols)
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
def model(terms, fit):
    X = design(terms, fit)
    bp = irls(X[fit], WIN[fit] * 1.0, "logit"); bw = irls(X[fit & WIN], R[fit & WIN], "log"); bl = irls(X[fit & ~WIN], -R[fit & ~WIN], "log")
    p = 1 / (1 + np.exp(-(X @ bp))); return p / ((1 - p) * np.exp(X @ bl) / np.exp(X @ bw)) - 1, (bp, bw, bl)
def pf(r, w=None):
    r = np.asarray(r) * (1 if w is None else np.asarray(w)); g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
E, LATE, Y20 = YR <= 2019, YR >= 2021, YR == 2020
HOLD = [("fwd", E, LATE), ("mirror", LATE, E)]
log(f"{len(C):,} trades")
print("# GF-45 — the GapFader PF−1 sizing model (weak-premarket gap-down long, LOO → ½ at 50 % / ½ at 75 % / 10:00)\n")
print(f"{len(C):,} trades ({len(C) / YR.size * 0 + len(C) / len(np.unique(YR)):,.0f} / yr); NYSE-listed removed by today's map "
      f"(unmapped = delisted kept: {C.primary_exchange.isna().mean() * 100:.0f} %). Noise on the gap: {(np.abs(e) < 0.01).mean() * 100:.0f} % within 1 %.\n")
print(f"Flat net PF: all {pf(R):.3f} · 2016–19 {pf(R[E]):.3f} ({E.sum():,}) · 2020 {pf(R[Y20]):.3f} ({Y20.sum():,}) · 2021–26 {pf(R[LATE]):.3f} ({LATE.sum():,}); "
      f"mean {R.mean():+.2f} %\n")
print("By year, flat net PF: " + " · ".join(f"{y} {pf(R[YR == y]):.2f} [{(YR == y).sum():,}]" for y in np.unique(YR)) + "\n")
MODELS = [("flat", []), ("dv20", ["ldv"]), ("dv20 + rvol (log)", ["ldv", "lrv"]), ("dv20 + rvol (log + < 0.005 flag)", ["ldv", "lrv", "rv5"]),
          ("+ bars (ln 1+b, b = 0 flag)", ["ldv", "lrv", "rv5", "lb", "b0"]),
          ("+ bars as BUCKETS (1–9, 10–29, ≥ 30 vs 0)", ["ldv", "lrv", "rv5", "b1_9", "b10_29", "b30"]),
          ("dv20 + bars BUCKETS (no rvol)", ["ldv", "b1_9", "b10_29", "b30"]),
          ("dv20 + bars (ln 1+b, b = 0 flag; no rvol)", ["ldv", "lb", "b0"]),
          ("+ gap (log)", ["ldv", "lrv", "rv5", "lb", "b0", "glog"]),
          ("+ gap (log) + price", ["ldv", "lrv", "rv5", "lb", "b0", "glog", "lpx"]),
          ("gap shape: linear", ["ldv", "lrv", "rv5", "lb", "b0", "glin"]), ("gap shape: sqrt", ["ldv", "lrv", "rv5", "lb", "b0", "gsqrt"]),
          ("no rvol (dv20 + bars + gap)", ["ldv", "lb", "b0", "glog"]), ("no bars (dv20 + rvol + gap)", ["ldv", "lrv", "rv5", "glog"]),
          ("no dv20 (rvol + bars + gap)", ["lrv", "rv5", "lb", "b0", "glog"]), ("gap alone (log)", ["glog"])]
print("| model | sized PF−1 fwd / mirror (**mean**) | units PF−1 fwd / mirror (Σ units) | top third PF fwd / mirror | bottom third PF fwd / mirror | 2020 sized PF−1 (fit 2016–19) |")
print("|---|---|---|---|---|---|")
for nm, terms in MODELS:
    out = {}
    for h, fit, app in HOLD:
        if not terms: m = np.full(len(C), pf(R[fit]) - 1)
        else: m, _ = model(terms, fit)
        w = np.clip(m / m[fit].mean(), 0.25, 4.0)[app]; w = w / w.mean()
        u = np.clip(m, 0.125, 2.0)[app]
        q3, q1 = np.quantile(m[fit], [2 / 3, 1 / 3]); a = m[app]; r = R[app]
        out[h] = (pf(r, w) - 1, pf(r, u) - 1, u.sum(), pf(r[a >= q3]) if (a >= q3).any() else np.nan, pf(r[a < q1]) if (a < q1).any() else np.nan)
        if h == "fwd":
            w20 = np.clip(m / m[fit].mean(), 0.25, 4.0)[Y20]; out["y20"] = pf(R[Y20], w20 / w20.mean()) - 1
    f, mi = out["fwd"], out["mirror"]
    print(f"| {nm} | {f[0]:.3f} / {mi[0]:.3f} (**{(f[0] + mi[0]) / 2:.3f}**) | {f[1]:.3f} / {mi[1]:.3f} ({f[2]:,.0f} / {mi[2]:,.0f}) | "
          f"{f[3]:.2f} / {mi[3]:.2f} | {f[4]:.2f} / {mi[4]:.2f} | {out['y20']:.3f} |")
terms = ["ldv", "lrv", "rv5", "lb", "b0", "glog"]
fitall = ~Y20
m_all, (bp, bw, bl) = model(terms, fitall)
print("\n## The model fitted on all years but 2020 (per SD of each term; flags per SD too)\n")
print("| term | odds × (p) | W × | L × | λ = L/W × | PF−1 direction |\n|---|---|---|---|---|---|")
for j, t in enumerate(["intercept"] + terms):
    lam = np.exp(bl[j] - bw[j]); odds = np.exp(bp[j])
    print(f"| {t} | {odds:.3f} | {np.exp(bw[j]):.3f} | {np.exp(bl[j]):.3f} | {lam:.3f} | {'↑' if odds / lam > 1 else '↓'} |")
print(f"\nPredicted PF−1 (fit ex-2020): mean {m_all[fitall].mean():.3f}, p10 / p50 / p90 {np.quantile(m_all[fitall], [.1, .5, .9]).round(3)}; "
      f"share < 0.125 (the unit floor) {(m_all[fitall] < 0.125).mean() * 100:.0f} %, > 2 {(m_all[fitall] > 2).mean() * 100:.0f} %\n")
print("## Calibration (fit ex-2020, in-sample): predicted PF−1 decile vs realised\n")
print("| decile | n | mean predicted PF−1 | realised PF−1 | mean net % | median dv20 $M | median rvolc | median bars29 | median gap % |\n|---|---|---|---|---|---|---|---|---|")
dq = pd.qcut(m_all[fitall], 10, labels=False)
for k in range(10):
    s = np.where(fitall)[0][dq == k]
    print(f"| {k + 1} | {len(s):,} | {m_all[s].mean():.2f} | {pf(R[s]) - 1:.2f} | {R[s].mean():+.2f} | {C.dv20_prior.values[s].mean() * 0 + np.median(C.dv20_prior.values[s]) / 1e6:.1f} | "
          f"{np.median(C.rvolc.values[s]):.4f} | {np.median(C.bars29.values[s]):.0f} | {np.median(C.gap_est.values[s]) * 100:.1f} |")
print("\n## The noise check: the same model with the TRUE gap (no auction-price noise), sized PF−1 fwd / mirror\n")
RAW["glog"] = np.log(-np.asarray(C.gap, float))
for h, fit, app in HOLD:
    m, _ = model(terms, fit); w = np.clip(m / m[fit].mean(), 0.25, 4.0)[app]; w = w / w.mean(); print(f"{h}: {pf(R[app], w) - 1:.3f}")
RAW["glog"] = np.log(-np.asarray(C.gap_est, float))                     # back to the noisy gap
# ---- each grade by PERIOD (the dv20 grade flips sign between 2016–19 and 2021–26)
P = [("2016–19", YR <= 2019), ("2020", Y20), ("2021–23", (YR >= 2021) & (YR <= 2023)), ("2024–26", YR >= 2024)]
def tab(nm, bands):
    print(f"| {nm} | " + " | ".join(p for p, _ in P) + " |\n|---|" + "---|" * len(P))
    for bn, s in bands: print(f"| {bn} | " + " | ".join(f"{pf(R[s & msk]):.2f} [{(s & msk).sum():,}]" for _, msk in P) + " |")
    print()
rv, b, g, dv, px = C.rvolc.values, C.bars29.values, -C.gap_est.values, C.dv20_prior.values, C.open.values
print("\n## Each grade by period, flat net PF [n]\n")
tab("rvolc", [("< 0.001", rv < 0.001), ("0.001–0.005", (rv >= 0.001) & (rv < 0.005)), ("0.005–0.01", (rv >= 0.005) & (rv < 0.01)), ("0.01–0.02", rv >= 0.01)])
tab("bars 08:29–09:29", [("0", b == 0), ("1–9", (b >= 1) & (b < 10)), ("10–29", (b >= 10) & (b < 30)), ("≥ 30", b >= 30)])
tab("dv20", [("$1–5M", dv < 5e6), ("$5–25M", (dv >= 5e6) & (dv < 25e6)), ("$25–100M", (dv >= 25e6) & (dv < 1e8)), ("≥ $100M", dv >= 1e8)])
tab("gap (noisy)", [("4–6 %", g < 0.06), ("6–10 %", (g >= 0.06) & (g < 0.10)), ("≥ 10 %", g >= 0.10)])
tab("price", [("$1–3", px < 3), ("$3–10", (px >= 3) & (px < 10)), ("$10–30", (px >= 10) & (px < 30)), ("≥ $30", px >= 30)])
# ---- the CURRENT regime: holdouts inside 2021–26
A, B = (YR >= 2021) & (YR <= 2023), YR >= 2024
print("## Holdouts inside the current regime: fit 2021–23 → apply 2024–26, and the reverse\n")
print("| model | sized PF−1 A→B / B→A (**mean**) | units PF−1 A→B / B→A | top third PF | bottom third PF |\n|---|---|---|---|---|")
for nm, terms in MODELS:
    out = []
    for fit, app in ((A, B), (B, A)):
        mm = np.full(len(C), pf(R[fit]) - 1) if not terms else model(terms, fit)[0]
        w = np.clip(mm / mm[fit].mean(), 0.25, 4)[app]; w = w / w.mean(); u = np.clip(mm, 0.125, 2)[app]
        q3, q1 = np.quantile(mm[fit], [2 / 3, 1 / 3]); a = mm[app]; r = R[app]
        out.append((pf(r, w) - 1, pf(r, u) - 1, pf(r[a >= q3]) if (a >= q3).any() else np.nan, pf(r[a < q1]) if (a < q1).any() else np.nan))
    f, h = out
    print(f"| {nm} | {f[0]:.3f} / {h[0]:.3f} (**{(f[0] + h[0]) / 2:.3f}**) | {f[1]:.3f} / {h[1]:.3f} | {f[2]:.2f} / {h[2]:.2f} | {f[3]:.2f} / {h[3]:.2f} |")
best = ["ldv", "lrv", "rv5"]
m_r, (bp, bw, bl) = model(best, YR >= 2021)
print("\n## The candidate (dv20 + rvol log + < 0.005 flag) fitted on 2021–26 (per SD)\n")
print("| term | odds × (p) | W × | L × | λ = L/W × |\n|---|---|---|---|---|")
for j, t in enumerate(["intercept"] + best):
    print(f"| {t} | {np.exp(bp[j]):.3f} | {np.exp(bw[j]):.3f} | {np.exp(bl[j]):.3f} | {np.exp(bl[j] - bw[j]):.3f} |")
s = YR >= 2021
print(f"\nPredicted PF−1 on 2021–26: p10 / p50 / p90 {np.quantile(m_r[s], [.1, .5, .9]).round(2)}; below the 0.125 floor {(m_r[s] < 0.125).mean() * 100:.0f} %; "
      f"above 2 {(m_r[s] > 2).mean() * 100:.0f} %. Applied to 2016–19 (the other regime): sized PF−1 "
      f"{(lambda w: pf(R[E], w / w.mean()) - 1)(np.clip(m_r[E] / m_r[s].mean(), 0.25, 4)):.3f} vs flat {pf(R[E]) - 1:.3f}")
# ---- THE PRODUCTION COEFFICIENTS (user 2026-10-20: dv20 + rvolc, fitted on 2021–26) → data/equity/gapfader/gf_coefficients.json
import json
TERMS = ["ldv", "lrv", "rv5"]; FIT = YR >= 2021
mm, (bp, bw, bl) = model(TERMS, FIT)
std = []
for t in TERMS:
    x = RAW[t]
    if t in FLAGS: lo, hi = None, None
    else: lo, hi = (float(v) for v in np.quantile(x[FIT], [0.01, 0.99])); x = np.clip(x, lo, hi)
    std.append({"term": t, "winsor": [lo, hi], "mean": float(x[FIT].mean()), "sd": float(x[FIT].std())})
idx = np.random.default_rng(7).choice(np.where(FIT)[0], 200, replace=False)
COEF = {"model": "GapFader (weak-premarket gap-down long) PF−1 sizing, docs/gapfader_results.md GF-45 / 45b / 45c",
        "fit": f"2021-01 .. 2026-09, n={int(FIT.sum())}; trade = LOO, ½ at 50 % / ½ at 75 % of the gap to 10:00, then the 10:00 1s vwap; 0.40c/sh",
        "gates": {"gap_max": -0.04, "open_min": 1.0, "dv20_min": 1e6, "rvol_max": 0.02, "no_early_close": True, "listings": ["XNAS", "XASE"]},
        "indicator_cutoff_et": {"XNAS": "09:29:25", "XASE": "09:29:50"}, "premarket_from_et": "04:00:00",
        "rvol": "premarket volume 04:00 → the listing's cutoff / avgvol20_prior (the 20 PRIOR sessions, in D's share units)",
        "terms": {"ldv": "ln(dv20_prior)", "lrv": "ln(rvol + 0.001)", "rv5": "1 if rvol < 0.005 else 0"},
        "standardise": std, "design": ["1"] + TERMS,
        "beta_p": [float(v) for v in bp], "beta_w": [float(v) for v in bw], "beta_l": [float(v) for v in bl],
        "pf1": "p / ((1 − p) · exp(x·β_l) / exp(x·β_w)) − 1, p = 1 / (1 + exp(−x·β_p)); x = [1, (clip(term) − mean) / sd …]",
        "fit_mean_pf1": float(mm[FIT].mean()),
        "pins": [{"ticker": C.ticker.values[i], "date": C.date.values[i], "dv20_prior": float(C.dv20_prior.values[i]),
                  "rvol": float(C.rvolc.values[i]), "pf1": float(mm[i])} for i in sorted(idx)]}
os.makedirs("data/equity/gapfader", exist_ok=True)
json.dump(COEF, open("data/equity/gapfader/gf_coefficients.json", "w"), indent=1)
# self-check: recompute the pins from the JSON alone
def pf1_from_json(dv, rv, J):
    raw = {"ldv": np.log(dv), "lrv": np.log(rv + 0.001), "rv5": 1.0 * (rv < 0.005)}
    x = [1.0] + [((np.clip(raw[s["term"]], *s["winsor"]) if s["winsor"][0] is not None else raw[s["term"]]) - s["mean"]) / s["sd"] for s in J["standardise"]]
    x = np.array(x); p = 1 / (1 + np.exp(-x @ J["beta_p"])); return p / ((1 - p) * np.exp(x @ J["beta_l"]) / np.exp(x @ J["beta_w"])) - 1
J = json.load(open("data/equity/gapfader/gf_coefficients.json"))
err = max(abs(pf1_from_json(q["dv20_prior"], q["rvol"], J) - q["pf1"]) for q in J["pins"])
assert err < 1e-12, err
print(f"\nwrote data/equity/gapfader/gf_coefficients.json: fit n={int(FIT.sum()):,}, fit-mean PF−1 {mm[FIT].mean():.4f}, 200 pins (max |Δ| recomputed from the JSON {err:.1e})")
