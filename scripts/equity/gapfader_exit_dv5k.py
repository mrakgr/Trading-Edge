"""GapFader GF-69 (user 2026-11-21): the 10:00 time-stop exit at the $5k VWAP instead of the 10:00:00 1s vwap.

Why: the live review (private exec_day.py) moved its model fill to the $5k VWAP — the 1s bars after the signal accumulated
until their dollar volume reaches $5k — after ZSQR 2026-11-20's 10:00 exit was modelled at 1.71, one 100-share print at
10:02:09, while ZSQR traded 1.63-1.68 (live sold 1.66). The user: compare every GapFader signal under the new exit — "how
the pf1 and net profit figures change".

THE BOOK: GF-67's (qqq_sizing.parquet = GF-54's 38,052 + QQQ's premarket gap). The target trades (tf_50 < 10:00) keep
their r1. The 20,081 time-stop trades: the prints of GF-68's cache (exit_sim/prints/<date>.parquet, 09:59:50-10:30, the
1s-bar corpus rule bar_ok — the same prints the 1s bars, so x_36000, are built from) from 10:00:00 on, accumulated in
time order to price × size >= $5k; the exit = their vwap. Short of $5k by 10:30: the vwap of what traded. No print by
10:30: x_36000 kept (reported). r1 = 100 (exit / open − 1) − 100 · 0.004 / open (0.40c/sh), as the book's.

THE SIZING: production's model v3 (gapfader_model_v3.py, GF-67): logit p, log W, log L by IRLS on 2021+, PF−1 =
p / ((1 − p) L / W) − 1, units = clip(PF−1, 0.125, 2). Reported (a) production's coefficients as deployed, applied to the new
outcomes, and (b) the model REFIT on the new outcomes (pf1 itself moves).
    cd research && python3 scripts/equity/gapfader_exit_dv5k.py > data/equity/gapfader/exit_dv5k.md 2> logs/gapfader_exit_dv5k.log; echo exit $?
"""
import json, os, sys, time
import numpy as np, pandas as pd

T0 = time.time()
def log(*a): print(f"[{time.time() - T0:6.1f}s]", *a, file=sys.stderr, flush=True)
MIN_DV, PR = 5000.0, "data/equity/gapfader/exit_sim/prints"

C = pd.read_parquet("data/equity/gapfader/qqq_sizing.parquet")
V = pd.concat([pd.read_parquet("data/equity/gapfader/sizing_tape_cut.parquet"),
               pd.read_parquet("data/equity/gapfader/sizing_tape_cut_ext.parquet")]).drop_duplicates(["ticker", "date"])
C = C.merge(V[["ticker", "date", "tf_50", "x_36000"]], on=["ticker", "date"], how="left")
ts = C.tf_50.fillna(1e9).values >= 36000
log(f"book {len(C):,}; time-stop exits {ts.sum():,}")

# ---------------------------------------------------------------- the $5k VWAP from 10:00:00
ny = __import__("zoneinfo").ZoneInfo("America/New_York")
def ns_ten(d):
    y, m, dd = map(int, d.split("-"))
    return int(pd.Timestamp(year=y, month=m, day=dd, hour=10, tz=ny).value)
x5, how, secs = {}, {}, {}
need = C[ts]
for d, g in need.groupby("date"):
    p = os.path.join(PR, f"{d}.parquet")
    if not os.path.exists(p): continue
    P = pd.read_parquet(p, columns=["key", "ts", "price", "size", "bar_ok"])
    P = P[P.bar_ok & (P.ts >= ns_ten(d))]
    t10 = ns_ten(d)
    for k, q in P.groupby("key", sort=False):
        dv = (q.price * q["size"]).cumsum().values
        i = int(np.searchsorted(dv, MIN_DV))           # first index where dv >= MIN_DV
        j = min(i, len(q) - 1)
        x5[k] = dv[j] / q["size"].values[: j + 1].sum()
        how[k] = "5k" if i < len(q) else "short"
        secs[k] = (q.ts.values[j] - t10) / 1e9
log(f"priced {len(x5):,}")
key = C.ticker + "_" + C.date
C["x_new"] = np.where(ts, key.map(x5).fillna(C.x_36000), np.nan)
C["how"] = np.where(ts, key.map(how).fillna("none"), "target")
C["secs"] = key.map(secs)
C["r_new"] = np.where(ts, 100 * (C.x_new / C.open - 1) - 100 * 0.004 / C.open, C.r1)

# ---------------------------------------------------------------- the model (production's v3 machinery)
COEF = json.load(open("models/gapfader/gf_coefficients.json"))
YR = C.date.str[:4].astype(int).values; FIT = YR >= 2021
deep = (C.gap_est.values <= -0.10) * 1.0; qd = (C.qqq_pm_gap.values <= -0.01) * 1.0
RAW = {"lm60": np.log(C.dv60_med.values), "lrv60": np.log(C.rvol60.values + 0.001), "rv5_60": (C.rvol60.values < 0.005) * 1.0,
       "deep10": deep, "d10xq1": deep * qd}
FLAGS = {"rv5_60", "deep10", "d10xq1"}; TERMS = ["lm60", "lrv60", "rv5_60", "deep10", "d10xq1"]
def design(std=None):
    cols, out = [np.ones(len(C))], []
    for i, t in enumerate(TERMS):
        x = RAW[t]
        if std is None:
            lo, hi = (None, None) if t in FLAGS else tuple(float(v) for v in np.quantile(x[FIT], [0.01, 0.99]))
        else: lo, hi = std[i]["winsor"]
        if lo is not None: x = np.clip(x, lo, hi)
        m, s = (float(x[FIT].mean()), float(x[FIT].std())) if std is None else (std[i]["mean"], std[i]["sd"])
        out.append({"term": t, "winsor": [lo, hi], "mean": m, "sd": s}); cols.append((x - m) / s)
    return np.column_stack(cols), out
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
def fit(R):
    X, std = design(); W = R > 0
    return X, (irls(X[FIT], W[FIT] * 1.0, "logit"), irls(X[FIT & W], R[FIT & W], "log"), irls(X[FIT & ~W], -R[FIT & ~W], "log"))
def pf1(X, b):
    bp, bw, bl = b; p = 1 / (1 + np.exp(-(X @ bp))); return p / ((1 - p) * np.exp(X @ bl) / np.exp(X @ bw)) - 1

X_old, b_old = fit(C.r1.values)         # = production's fit (checked against the deployed file below)
_, b_new = fit(C.r_new.values)
P_old, P_new = pf1(X_old, b_old), pf1(X_old, b_new)
# the refit machinery on the old exits must reproduce the deployed file
dev = max(float(np.max(np.abs(np.array(COEF[k]) - b))) for k, b in zip(("beta_p", "beta_w", "beta_l"), b_old))
log(f"refit on the old exits vs the deployed coefficients: max |Δβ| = {dev:.2e}"); assert dev < 1e-6, dev

# ---------------------------------------------------------------- the tables
def pf(r): w, l = r[r > 0].sum(), -r[r < 0].sum(); return w / l if l > 0 else float("inf")
PER = [("all", np.ones(len(C), bool)), ("2016–19", YR <= 2019), ("2020", YR == 2020), ("2021–23", (YR >= 2021) & (YR <= 2023)),
       ("2024–26", YR >= 2024)]
print("# GF-69 — the 10:00 time-stop exit at the $5k VWAP (user 2026-11-21)\n")
c = pd.Series(C.how[ts]).value_counts()
print(f"Time-stop exits {ts.sum():,}: $5k reached by 10:30 {c.get('5k', 0):,} · short of $5k by 10:30 {c.get('short', 0):,} · "
      f"no print 10:00-10:30 (x_36000 kept) {c.get('none', 0):,}. Seconds from 10:00 to the $5k: "
      f"p50 {np.nanmedian(C.secs[ts & (C.how == '5k')]):.0f} · p90 {np.nanquantile(C.secs[ts & (C.how == '5k')], 0.9):.0f} · "
      f"p99 {np.nanquantile(C.secs[ts & (C.how == '5k')], 0.99):.0f}.\n")
d = (C.r_new - C.r1)[ts] * 100
print(f"The exit's change (new − old, bp of the entry, time-stop trades): mean {d.mean():+.1f} · median {d.median():+.1f} · "
      f"p5 {d.quantile(0.05):+.0f} · p95 {d.quantile(0.95):+.0f} · |Δ| > 100 bp {(d.abs() > 100).mean():.1%}\n")

print("## Flat (one unit per trade)\n")
print("| period | n | PF old | PF new | Σ r old % | Σ r new % | mean old bp | mean new bp |")
print("|---|---|---|---|---|---|---|---|")
for lab, m in PER:
    a, b = C.r1.values[m], C.r_new.values[m]
    print(f"| {lab} | {m.sum():,} | {pf(a):.3f} | {pf(b):.3f} | {a.sum():+,.0f} | {b.sum():+,.0f} | {a.mean() * 100:+.1f} | {b.mean() * 100:+.1f} |")
print("\nTime-stop trades alone:\n")
print("| period | n | PF old | PF new | Σ r old % | Σ r new % |\n|---|---|---|---|---|---|")
for lab, m in PER:
    m = m & ts; a, b = C.r1.values[m], C.r_new.values[m]
    print(f"| {lab} | {m.sum():,} | {pf(a):.3f} | {pf(b):.3f} | {a.sum():+,.0f} | {b.sum():+,.0f} |")

print("\n## pf1 (the model's predicted PF−1) and the sized book\n")
print("Units = clip(pf1, 0.125, 2). 'deployed' = production's v3 fit (on the old exits) applied to the outcome; 'refit' = the "
      "same model fitted on the new exits (2021+). In-sample on 2021+.\n")
U_old, U_new = np.clip(P_old, 0.125, 2), np.clip(P_new, 0.125, 2)
print("| | pf1 p10 | p50 | p90 | mean | at the 0.125 floor |\n|---|---|---|---|---|---|")
for lab, P in [("deployed (old exits)", P_old), ("refit (new exits)", P_new)]:
    q = P[FIT]
    print(f"| {lab} | {np.quantile(q, .1):.3f} | {np.quantile(q, .5):.3f} | {np.quantile(q, .9):.3f} | {q.mean():.3f} | {(q <= 0.125).mean():.1%} |")
print(f"\nρ(pf1 deployed, pf1 refit) on 2021+ = {np.corrcoef(P_old[FIT], P_new[FIT])[0, 1]:.4f}\n")
print("| period | units PF: deployed on old exits | deployed on new exits | refit on new exits | Σ units·r old | deployed, new | refit, new |")
print("|---|---|---|---|---|---|---|")
for lab, m in PER:
    a, b = C.r1.values[m], C.r_new.values[m]
    print(f"| {lab} | {pf(U_old[m] * a):.3f} | {pf(U_old[m] * b):.3f} | {pf(U_new[m] * b):.3f} | {(U_old[m] * a).sum():+,.0f} | "
          f"{(U_old[m] * b).sum():+,.0f} | {(U_new[m] * b).sum():+,.0f} |")
print("\n## The coefficients (per SD; odds / W / L multipliers)\n")
print("| term | p old | p new | W old | W new | L old | L new |\n|---|---|---|---|---|---|---|")
for i, t in enumerate(["intercept"] + TERMS):
    print(f"| {t} | {np.exp(b_old[0][i]):.3f} | {np.exp(b_new[0][i]):.3f} | {np.exp(b_old[1][i]):.3f} | {np.exp(b_new[1][i]):.3f} | "
          f"{np.exp(b_old[2][i]):.3f} | {np.exp(b_new[2][i]):.3f} |")
C[["ticker", "date", "how", "secs", "x_36000", "x_new", "r1", "r_new"]].assign(pf1_old=P_old, pf1_new=P_new).to_parquet(
    "data/equity/gapfader/exit_dv5k.parquet")
log("done")
