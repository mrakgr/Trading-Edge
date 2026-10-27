"""GapFader GF-53 (user 2026-10-27: "let's make an alternative model that uses the 60d median dollar volume and compare it
against the current one that uses the 20d mean dollar volume").

THE BOOK: GF-45's, verbatim (scripts/equity/gapfader_sizing_model.py: the census after GF-46, the tape cache, the gates, the
auction-price noise with seed 20261020), so the current model's numbers reproduce (current-regime holdout, sized PF−1 1.087).
TWO TRADES, each model fitted on the one it is scored on:
  r2  the design trade GF-45 fitted: ½ at 50 % / ½ at 75 % of the gap (trade-through) to 10:00, then the 10:00 1s vwap
  r1  the trade the Oms runs: ONE target at 50 % to 10:00, then the 10:00 1s vwap
0.40c a share round trip on both.
THE MODELS (logit p, log W, log L by IRLS; PF−1 = p / ((1−p)·L/W) − 1; terms standardised on the FIT set, winsorised 1/99 %):
  current    ln dv20_prior (the 20-session MEAN) + ln(rvol + 0.001) + (rvol < 0.005)       — production (gf_coefficients.json)
  dv60_med   ln dv60_med (the 60-session MEDIAN, GF-52) in its place
  dv20_med   ln dv20_med (the 20-session median, GF-50) in its place — the window's length vs the statistic
  both       ln dv20_prior + ln dv60_med
  dv60 + surge   ln dv60_med + ln(dv20_med / dv60_med)
The gates do not change (dv20_prior ≥ $1M): only the sizing.
HOLDOUTS (GF-45's): the current regime, fit 2021–23 → apply 2024–26 and the reverse (THE criterion GF-45 picked on);
fwd = fit 2016–19 → apply 2021–26, mirror = the reverse (2020 excluded from fits).
SCORES on the apply set:
  sized PF−1  weights clip(PF−1 / its fit-set mean, 0.25, 4), mean 1
  units PF−1  weights clip(PF−1, 0.125, 2) (production's rule) and Σ units·r (% units)
  top / bottom third flat PF (cuts = the fit set's terciles)

  cd research && python -u scripts/equity/gapfader_dv60_model.py > data/equity/gapfader/dv60_model.md 2> logs/gapfader_dv60_model.log; echo exit $?
"""
import json, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)

# ---------------------------------------------------------------- GF-45's book (gapfader_sizing_model.py, verbatim)
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.gap <= -0.035) & (C.rvol_pm < 0.05)].copy()
L = pd.read_parquet("data/equity/gapfader/listing_2026-10-17.parquet")
C = C.merge(L, on="ticker", how="left"); C = C[C.primary_exchange != "XNYS"].copy()
V = pd.read_parquet("data/equity/gapfader/sizing_tape_cut.parquet")
C = C.merge(V, on=["ticker", "date"], how="inner")
C = C[C.x_36000.notna()].copy()
C["pmvolc"] = np.where(C.primary_exchange == "XASE", C.pmvol_50, C.pmvol_25)
C["rvolc"] = C.pmvolc / C.avgvol20_prior
rng = np.random.default_rng(20261020)
e = np.exp(rng.normal(np.log(0.001), np.log(6.5) / 1.2816, len(C))) * rng.choice([-1, 1], len(C))
C["gap_est"] = C.open * (1 + e) / C.pc_total - 1
C = C[(C.gap <= -0.04) & (C.gap_est <= -0.04) & (C.rvolc < 0.02)].copy()
for f in (50, 75):
    t = C[f"tf_{f}"].astype("float64").fillna(np.inf)
    C[f"r{f}"] = np.where(t < 36000, C.open + f / 100 * (C.pc_total - C.open), C.x_36000) / C.open - 1 - 0.004 / C.open
C["r2"] = 0.5 * C.r50 + 0.5 * C.r75
C["r1"] = C.r50
C = C.sort_values(["date", "ticker"]).reset_index(drop=True)
log(f"book {len(C):,} trades")

# ---------------------------------------------------------------- the medians (GF-50 / GF-52's windows, inside the episode)
con = duckdb.connect()
con.execute("set memory_limit='4GB'; set threads=6; set enable_progress_bar=false; set temp_directory='logs/duckdb_tmp_dv60m'")
bk = C[["ticker", "date"]].copy(); bk["date"] = pd.to_datetime(bk.date)
con.register("bk", bk)
F = con.execute("""
    WITH e0 AS (SELECT ticker, date, volume * close AS dvol,
                       CASE WHEN date - lag(date) OVER (PARTITION BY ticker ORDER BY date) > 45 THEN 1 ELSE 0 END AS brk
                FROM 'data/v2/daily_adjusted.parquet' WHERE ticker IN (SELECT DISTINCT ticker FROM bk)),
    ep AS (SELECT *, sum(brk) OVER (PARTITION BY ticker ORDER BY date) AS episode FROM e0),
    w AS (SELECT ticker, date,
                 median(dvol) OVER (PARTITION BY ticker, episode ORDER BY date ROWS BETWEEN 60 PRECEDING AND 1 PRECEDING) AS dv60_med,
                 median(dvol) OVER (PARTITION BY ticker, episode ORDER BY date ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS dv20_med
          FROM ep)
    SELECT w.* FROM w JOIN bk USING (ticker, date)""").df()
F["date"] = F.date.astype(str)
C = C.merge(F, on=["ticker", "date"], how="left")
assert C.dv60_med.notna().all() and (C.dv60_med > 0).all() and (C.dv20_med > 0).all(), "a median is missing"

YR = C.date.str[:4].astype(int).values
RAW = {"ldv": np.log(C.dv20_prior), "lrv": np.log(C.rvolc + 0.001), "rv5": (C.rvolc < 0.005) * 1.0,
       "lm60": np.log(C.dv60_med), "lm20": np.log(C.dv20_med), "lsurge": np.log(C.dv20_med / C.dv60_med)}
RAW = {k: np.asarray(v, float) for k, v in RAW.items()}
assert all(np.isfinite(v).all() for v in RAW.values())
FLAGS = {"rv5"}
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
def model(terms, fit, R):
    WIN = R > 0
    X = design(terms, fit)
    bp = irls(X[fit], WIN[fit] * 1.0, "logit"); bw = irls(X[fit & WIN], R[fit & WIN], "log"); bl = irls(X[fit & ~WIN], -R[fit & ~WIN], "log")
    p = 1 / (1 + np.exp(-(X @ bp))); return p / ((1 - p) * np.exp(X @ bl) / np.exp(X @ bw)) - 1, (bp, bw, bl)
def pf(r, w=None):
    r = np.asarray(r) * (1 if w is None else np.asarray(w)); g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
def fmt(x): return "inf" if np.isinf(x) else f"{x:.3f}"

E, LATE, Y20 = YR <= 2019, YR >= 2021, YR == 2020
A, B = (YR >= 2021) & (YR <= 2023), YR >= 2024
MODELS = [("flat", []), ("current: ln dv20 MEAN + rvol", ["ldv", "lrv", "rv5"]),
          ("**dv60_med** + rvol", ["lm60", "lrv", "rv5"]), ("dv20_med + rvol", ["lm20", "lrv", "rv5"]),
          ("both: dv20 mean + dv60_med + rvol", ["ldv", "lm60", "lrv", "rv5"]),
          ("dv60_med + surge + rvol", ["lm60", "lsurge", "lrv", "rv5"]),
          ("dv60_med alone", ["lm60"]), ("dv20 mean alone", ["ldv"])]
def score(terms, fit, app, R):
    m = np.full(len(C), pf(R[fit]) - 1) if not terms else model(terms, fit, R)[0]
    w = np.clip(m / m[fit].mean(), 0.25, 4)[app]; w = w / w.mean(); u = np.clip(m, 0.125, 2)[app]
    q3, q1 = np.quantile(m[fit], [2 / 3, 1 / 3]); a = m[app]; r = R[app]
    return (pf(r, w) - 1, pf(r, u) - 1, (r * u).sum(), pf(r[a >= q3]) if (a >= q3).any() else np.nan,
            pf(r[a < q1]) if (a < q1).any() else np.nan)

print("# GF-53 — an alternative GapFader sizing model on the 60-session MEDIAN dollar volume vs the current 20-session MEAN\n")
print(f"Book: GF-45's {len(C):,} trades (after GF-46). dv20_prior / dv60_med Spearman "
      f"{pd.Series(RAW['ldv']).rank().corr(pd.Series(RAW['lm60']).rank()):+.3f}; dv20_prior / dv60_med median ratio "
      f"{np.median(C.dv20_prior / C.dv60_med):.2f} (p90 {np.quantile(C.dv20_prior / C.dv60_med, 0.9):.1f}).\n")
for tag, rc, desc in (("r2", "r2", "the DESIGN trade (½ at 50 % / ½ at 75 %; what the current model was fitted on)"),
                      ("r1", "r1", "the trade the OMS RUNS (one target at 50 %)")):
    R = C[rc].values * 100
    print(f"## Trade {tag}: {desc}\n")
    print(f"Flat net PF: all {pf(R):.3f} · 2021–23 {pf(R[A]):.3f} · 2024–26 {pf(R[B]):.3f} · 2016–19 {pf(R[E]):.3f}\n")
    print("### The current regime: fit 2021–23 → apply 2024–26, and the reverse (GF-45's criterion)\n")
    print("| model | sized PF−1 A→B / B→A (**mean**) | units PF−1 A→B / B→A (**mean**) | Σ units·r A→B / B→A | top third PF | bottom third PF |")
    print("|---|---|---|---|---|---|")
    for nm, terms in MODELS:
        f, h = score(terms, A, B, R), score(terms, B, A, R)
        print(f"| {nm} | {f[0]:.3f} / {h[0]:.3f} (**{(f[0] + h[0]) / 2:.3f}**) | {f[1]:.3f} / {h[1]:.3f} (**{(f[1] + h[1]) / 2:.3f}**) | "
              f"{f[2] / 100:+.1f} / {h[2] / 100:+.1f} | {f[3]:.2f} / {h[3]:.2f} | {f[4]:.2f} / {h[4]:.2f} |")
    print("\n### Across regimes: fwd = fit 2016–19 → apply 2021–26, mirror = the reverse\n")
    print("| model | sized PF−1 fwd / mirror (**mean**) | units PF−1 fwd / mirror (**mean**) | top third PF | bottom third PF |")
    print("|---|---|---|---|---|")
    for nm, terms in MODELS:
        f, h = score(terms, E, LATE, R), score(terms, LATE, E, R)
        print(f"| {nm} | {f[0]:.3f} / {h[0]:.3f} (**{(f[0] + h[0]) / 2:.3f}**) | {f[1]:.3f} / {h[1]:.3f} (**{(f[1] + h[1]) / 2:.3f}**) | "
              f"{f[3]:.2f} / {h[3]:.2f} | {f[4]:.2f} / {h[4]:.2f} |")
    print()

# ---------------------------------------------------------------- the two models fitted as production is (2021–26, design trade)
R = C.r2.values * 100
FIT = LATE
cur, (cp, cw, cl) = model(["ldv", "lrv", "rv5"], FIT, R)
alt, (ap, aw, al) = model(["lm60", "lrv", "rv5"], FIT, R)
J = json.load(open("data/equity/gapfader/gf_coefficients_pre_gf54.json"))
err = max(abs(a - b) for a, b in zip(list(cp) + list(cw) + list(cl), J["beta_p"] + J["beta_w"] + J["beta_l"]))
print(f"## Fitted as production (2021–26, design trade): the current model reproduces gf_coefficients.json (max |Δβ| {err:.1e})\n")
print("| term (per SD) | current: odds × / W × / L × | dv60_med: odds × / W × / L × |\n|---|---|---|")
for j, (tc, ta) in enumerate(zip(["intercept", "ln dv20 mean", "ln rvol", "rvol < 0.005"], ["intercept", "ln dv60_med", "ln rvol", "rvol < 0.005"])):
    print(f"| {tc} / {ta} | {np.exp(cp[j]):.3f} / {np.exp(cw[j]):.3f} / {np.exp(cl[j]):.3f} | {np.exp(ap[j]):.3f} / {np.exp(aw[j]):.3f} / {np.exp(al[j]):.3f} |")
s = FIT
print(f"\nPredicted PF−1 on 2021–26: current p10 / p50 / p90 {np.quantile(cur[s], [.1, .5, .9]).round(2)}, mean {cur[s].mean():.3f}; "
      f"dv60_med {np.quantile(alt[s], [.1, .5, .9]).round(2)}, mean {alt[s].mean():.3f}. Spearman between them {pd.Series(cur[s]).rank().corr(pd.Series(alt[s]).rank()):+.3f}.\n")
uc, ua = np.clip(cur, 0.125, 2), np.clip(alt, 0.125, 2)
print(f"Units (production's clip): mean current {uc[s].mean():.3f} vs dv60_med {ua[s].mean():.3f}; at the 2-unit cap {(uc[s] >= 2).mean() * 100:.1f} % vs {(ua[s] >= 2).mean() * 100:.1f} %; "
      f"at the 0.125 floor {(uc[s] <= 0.125).mean() * 100:.1f} % vs {(ua[s] <= 0.125).mean() * 100:.1f} %.\n")
print("## Where the two disagree (2021–26, in-sample): trades by the units ratio dv60_med / current\n")
print("| units ratio | n | flat PF | PF current units | PF dv60_med units | median dv20 mean $M | median dv60_med $M |\n|---|---|---|---|---|---|---|")
ratio = ua / uc
for lo, hi in [(0, 0.5), (0.5, 0.8), (0.8, 1.25), (1.25, 2), (2, np.inf)]:
    m = s & (ratio >= lo) & (ratio < hi)
    if m.sum():
        print(f"| {lo:g} … {hi:g} | {m.sum():,} | {pf(R[m]):.2f} | {pf(R[m], uc[m]):.2f} | {pf(R[m], ua[m]):.2f} | "
              f"{np.median(C.dv20_prior.values[m]) / 1e6:.2f} | {np.median(C.dv60_med.values[m]) / 1e6:.3f} |")
C.assign(pf1_cur=cur, pf1_dv60=alt).to_parquet("data/equity/gapfader/dv60_model.parquet")
log("done")

# ---------------------------------------------------------------- 10-26's live GapFader trades under both models (fitted as production)
def pf1_at(terms, coefs, raw):
    x = [1.0]
    for t in terms:
        v = raw[t]; xs = RAW[t]
        if t not in FLAGS:
            lo, hi = np.quantile(xs[FIT], [0.01, 0.99]); xs = np.clip(xs, lo, hi); v = min(max(v, lo), hi)
        x.append((v - xs[FIT].mean()) / xs[FIT].std())
    x = np.array(x); bp, bw, bl = coefs
    p = 1 / (1 + np.exp(-x @ bp)); return p / ((1 - p) * np.exp(x @ bl) / np.exp(x @ bw)) - 1
# the decision rows' dv20 / rvol (out/oms-live/2026-10-26/trade.ndjson, private) and dv60_med from daily_adjusted (60 sessions to 10-23)
LIVE = [("NIVF", 14933690.8, 0.013985, 0.371), ("GIXI", 11026182.3, 0.0032815, 0.840)]
d60 = con.execute("""WITH x AS (SELECT ticker, volume * close dv, row_number() OVER (PARTITION BY ticker ORDER BY date DESC) rn
                     FROM 'data/v2/daily_adjusted.parquet' WHERE ticker IN ('NIVF', 'GIXI') AND date < '2026-10-26')
                     SELECT ticker, median(dv) FROM x WHERE rn <= 60 GROUP BY ticker""").fetchall()
d60 = dict(d60)
print("\n## 10-26's live trades under both models (fitted as production)\n")
print("| ticker | dv20 mean | dv60_med | rvol | live pf1 | current (refit check) | dv60_med model | units current → dv60_med |\n|---|---|---|---|---|---|---|---|")
for t, dv, rv, live in LIVE:
    raw = {"ldv": np.log(dv), "lrv": np.log(rv + 0.001), "rv5": float(rv < 0.005), "lm60": np.log(d60[t])}
    a = pf1_at(["ldv", "lrv", "rv5"], (cp, cw, cl), raw); b = pf1_at(["lm60", "lrv", "rv5"], (ap, aw, al), raw)
    print(f"| {t} | ${dv / 1e6:.2f}M | ${d60[t] / 1e6:.3f}M | {rv:.4f} | {live:.3f} | {a:.3f} | {b:.3f} | {np.clip(a, .125, 2):.2f} → {np.clip(b, .125, 2):.2f} |")
