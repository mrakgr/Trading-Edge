"""GapFader GF-54 (user 2026-10-27): THE PRODUCTION REFIT on the 60-session MEDIAN dollar volume.

User rulings, 2026-10-27:
  · the volume estimate is dv60_med (GF-52/53: the mean is biased by the spikes of illiquid names) — in the MODEL, the
    GATE and the CAP;
  · THE FLOOR: dv60_med ≥ $500k ("to trade we actually need counter parties"; the backtest's PF below it is not a
    quantity we can trade) — replaces dv20_prior ≥ $1M;
  · fitted on the trade the OMS RUNS: ONE target at 50 % of the gap (trade-through) to 10:00, then the 10:00 1s vwap
    ("free up the buying power for FlushFader as soon as possible");
  · rvol on the 60-session MEDIAN too: rvol = premarket volume / vol60_med, vol60_med = median(volume / n) over the 60
    prior sessions in the episode × n(D) (shares in D's scale, as the census's avgvol20_prior) — in the model's terms AND
    the rvol < 0.02 gate.
A listing younger than 60 sessions: the medians take all its prior sessions in the episode (n60, reported); the census
has a row only from 20 prior sessions on (its 20-session volume), as the Oms's Reference (absent below 20).
THE BOOK: GF-45's (gapfader_sizing_model.py) with the volume swapped: open ≥ $1 · no early close · gap ≤ −4 % and the
noisy auction gap ≤ −4 % · rvol60 < 0.02 · not NYSE-listed · dv60_med ≥ $500k. The census pool's pre-filter is on the
NEW rvol (premarket to 09:30 / vol60_med < 0.05; rvol60 to the cutoff can only be smaller). The trades already in
GF-45's book keep their exact auction-price noise (seed 20261020, drawn in GF-45's row order); a trade new to this book
draws its own from a seed of its (ticker, date), so it does not move when the book does. Their tape (the target's
trade-through, the 10:00 vwap) is GF-45's cache plus an EXTENSION (data/equity/gapfader/sizing_tape_cut_ext.parquet),
the same query, topped up with whatever rows the book needs and it lacks.
THE MODELS (GF-45's machinery: logit p, log W, log L by IRLS, terms standardised on the fit set, winsorised 1/99 %):
  current   ln dv20_prior (the 20-session MEAN) + ln(rvol20 + 0.001) + (rvol20 < 0.005)      rvol20 = pm / avgvol20_prior
  dv60 only ln dv60_med + the same rvol20 terms
  new       ln dv60_med + ln(rvol60 + 0.001) + (rvol60 < 0.005)                           rvol60 = pm / vol60_med
OUTPUT: data/equity/gapfader/gf_coefficients.json — PRODUCTION's (Engine/GapFader.fs's Model, pinned by the Oms's tests;
the new model fitted on 2021–26, 200 pins). GF-45's model is kept as gf_coefficients_pre_gf54.json (its scripts read it).

  cd research && python -u scripts/equity/gapfader_model_v2.py > data/equity/gapfader/model_v2.md 2> logs/gapfader_model_v2.log; echo exit $?
"""
import argparse, hashlib, json, os, sys, time
import numpy as np, pandas as pd, duckdb
ap = argparse.ArgumentParser()
# the production coefficients' path; 2026-10-31 (user: "keep the coefficients as they are") the holder's-return rebuild
# wrote its refit beside them (gf_coefficients_holder_refit.json) and production kept its own
ap.add_argument("--coefficients-out", default="data/equity/gapfader/gf_coefficients.json")
args = ap.parse_args()
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
FLOOR = 5e5

# ---------------------------------------------------------------- the pool (GF-45's, before its dv gate)
P = pd.read_parquet("data/equity/gapfader/census.parquet")
P = P[(P.open >= 1) & ~P.early_close & P.close.notna() & (P.gap <= -0.035)].copy()     # the rvol pre-filter: below, on rvol60
L = pd.read_parquet("data/equity/gapfader/listing_2026-10-17.parquet")
P = P.merge(L, on="ticker", how="left"); P = P[P.primary_exchange != "XNYS"].copy()
V = pd.read_parquet("data/equity/gapfader/sizing_tape_cut.parquet")

# ---------------------------------------------------------------- GF-45's noise, for the rows of its book (verbatim order)
G = P[(P.dv20_prior >= 1e6) & (P.rvol_pm < 0.05)].merge(V, on=["ticker", "date"], how="inner")
G = G[G.x_36000.notna()]
rng = np.random.default_rng(20261020)
eG = np.exp(rng.normal(np.log(0.001), np.log(6.5) / 1.2816, len(G))) * rng.choice([-1, 1], len(G))
NOISE = dict(zip(zip(G.ticker, G.date), eG))
log(f"pool {len(P):,}; GF-45's pre-gate rows {len(G):,}")

# ---------------------------------------------------------------- the medians (inside the episode)
con = duckdb.connect()
con.execute("set memory_limit='4GB'; set threads=6; set enable_progress_bar=false; set temp_directory='logs/duckdb_tmp_gf54'")
bk = P[["ticker", "date"]].copy(); bk["date"] = pd.to_datetime(bk.date)
con.register("bk", bk)
F = con.execute("""
    WITH e0 AS (SELECT ticker, date, volume * close AS dvol, volume / n AS vol_n, n,
                       CASE WHEN date - lag(date) OVER (PARTITION BY ticker ORDER BY date) > 45 THEN 1 ELSE 0 END AS brk
                FROM 'data/v2/daily_adjusted.parquet' WHERE ticker IN (SELECT DISTINCT ticker FROM bk)),
    ep AS (SELECT *, sum(brk) OVER (PARTITION BY ticker ORDER BY date) AS episode FROM e0),
    w AS (SELECT ticker, date, median(dvol) OVER p AS dv60_med, median(vol_n) OVER p * n AS vol60_med, count(*) OVER p AS n60
          FROM ep WINDOW p AS (PARTITION BY ticker, episode ORDER BY date ROWS BETWEEN 60 PRECEDING AND 1 PRECEDING))
    SELECT w.ticker, w.date::VARCHAR date, w.dv60_med, w.vol60_med, w.n60 FROM w JOIN bk USING (ticker, date)""").df()
P = P.merge(F, on=["ticker", "date"], how="left")
P = P[(P.dv60_med >= FLOOR) & (P.pm_vol / P.vol60_med < 0.05)].copy()
log(f"dv60_med ≥ ${FLOOR:,.0f} and premarket / vol60_med < 0.05: {len(P):,}")

# ---------------------------------------------------------------- the tape: GF-45's cache + the extension (built once)
EXT = "data/equity/gapfader/sizing_tape_cut_ext.parquet"
X0 = pd.read_parquet(EXT) if os.path.exists(EXT) else None
have = set(zip(V.ticker, V.date)) | (set(zip(X0.ticker, X0.date)) if X0 is not None else set())
need = P[[(t, d) not in have for t, d in zip(P.ticker, P.date)]]
if need.empty:
    X = X0
else:
    log(f"topping up the tape extension: {len(need):,} rows over {need.date.nunique():,} days")
    parts = []
    for i, d in enumerate(sorted(need.date.unique())):
        gd = need[need.date == d][["ticker", "open", "pc_total"]]
        con.register("gd", gd)
        X = con.execute(f"""SELECT s.ticker,
                   coalesce(sum(volume) FILTER (bucket < 34165), 0) AS pmvol_25,
                   coalesce(sum(volume) FILTER (bucket < 34190), 0) AS pmvol_50,
                   count(*) FILTER (bucket >= 30540 AND bucket < 34140) AS bars29,
                   min(bucket) FILTER (bucket >= 34200 AND v > gd.open + 0.50 * (gd.pc_total - gd.open)) AS tf_50,
                   min(bucket) FILTER (bucket >= 34200 AND v > gd.open + 0.75 * (gd.pc_total - gd.open)) AS tf_75,
                   arg_min(v, bucket) FILTER (bucket >= 36000) AS x_36000
            FROM (SELECT ticker, bucket, vwap::DOUBLE v, volume::DOUBLE volume FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                  WHERE ticker IN (SELECT ticker FROM gd) AND bucket >= 14400 AND bucket < 57600 AND volume > 0) s
            JOIN gd ON gd.ticker = s.ticker GROUP BY s.ticker""").df()
        con.unregister("gd"); X["date"] = d; parts.append(X)
        if i % 100 == 0: log(f"  {i + 1} {d}")
    X = pd.concat(([X0] if X0 is not None else []) + parts, ignore_index=True)
    X.to_parquet(EXT + ".tmp"); os.replace(EXT + ".tmp", EXT)
    log(f"wrote {EXT}: {len(X):,}")
VV = pd.concat([V, X[V.columns]], ignore_index=True)
C = P.merge(VV, on=["ticker", "date"], how="inner")
C = C[C.x_36000.notna()].copy()
C["pmvolc"] = np.where(C.primary_exchange == "XASE", C.pmvol_50, C.pmvol_25)
C["rvolc"] = C.pmvolc / C.avgvol20_prior          # rvol20: the current model's
C["rvol60"] = C.pmvolc / C.vol60_med              # rvol60: the new model's, and the gate's
C = C.sort_values(["date", "ticker"]).reset_index(drop=True)
new = np.array([(t, d) not in NOISE for t, d in zip(C.ticker, C.date)])
def own_noise(t: str, d: str) -> float:          # a new row's noise from a seed of its own (ticker, date)
    g = np.random.default_rng(int.from_bytes(hashlib.sha256(f"{t}|{d}|20261027".encode()).digest()[:8], "little"))
    return float(np.exp(g.normal(np.log(0.001), np.log(6.5) / 1.2816)) * g.choice([-1, 1]))
e = np.array([NOISE[(t, d)] if (t, d) in NOISE else own_noise(t, d) for t, d in zip(C.ticker, C.date)])
C["gap_est"] = (C.open * (1 + e) - C.div_m1) / C.close_m1 - 1      # the auction price's holder's return (user 2026-10-31)
C["in_gf45"] = ~new & (C.dv20_prior >= 1e6) & (C.rvolc < 0.02)    # GF-45's gates take it too (its noise kept)
C = C[(C.gap <= -0.04) & (C.gap_est <= -0.04) & (C.rvol60 < 0.02)].copy().reset_index(drop=True)
t50 = C.tf_50.astype("float64").fillna(np.inf)
C["r1"] = (np.where(t50 < 36000, C.open + 0.5 * (C.pc_total - C.open), C.x_36000) / C.open - 1 - 0.004 / C.open) * 100
log(f"book {len(C):,} trades ({(~C.in_gf45).sum():,} not in GF-45's)")

# ---------------------------------------------------------------- the models
R = C.r1.values; WIN = R > 0; YR = C.date.str[:4].astype(int).values
RAW = {"ldv": np.log(C.dv20_prior.values), "lm60": np.log(C.dv60_med.values),
       "lrv": np.log(C.rvolc.values + 0.001), "rv5": (C.rvolc.values < 0.005) * 1.0,
       "lrv60": np.log(C.rvol60.values + 0.001), "rv5_60": (C.rvol60.values < 0.005) * 1.0}
assert all(np.isfinite(v).all() for v in RAW.values())
FLAGS = {"rv5", "rv5_60"}
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
def score(terms, fit, app):
    m = np.full(len(C), pf(R[fit]) - 1) if not terms else model(terms, fit)[0]
    w = np.clip(m / m[fit].mean(), 0.25, 4)[app]; w = w / w.mean(); u = np.clip(m, 0.125, 2)[app]
    q3, q1 = np.quantile(m[fit], [2 / 3, 1 / 3]); a = m[app]; r = R[app]
    return (pf(r, w) - 1, pf(r, u) - 1, (r * u).sum() / 100, pf(r[a >= q3]), pf(r[a < q1]))
E, LATE, A, B = YR <= 2019, YR >= 2021, (YR >= 2021) & (YR <= 2023), YR >= 2024
MODELS = [("flat", []), ("current: ln dv20 MEAN + rvol20", ["ldv", "lrv", "rv5"]), ("dv60_med + rvol20", ["lm60", "lrv", "rv5"]),
          ("**new: ln dv60_med + rvol60**", ["lm60", "lrv60", "rv5_60"])]

print("# GF-54 — the production refit on the 60-session MEDIAN: the model, the gates (dv60_med ≥ $500k, rvol60 < 0.02), the cap; the Oms's trade\n")
print(f"Book: {len(C):,} trades {C.date.min()}..{C.date.max()} — {(~C.in_gf45).sum():,} of them not in GF-45's. "
      f"The trade: ONE target at 50 % to 10:00, else the 10:00 1s vwap, 0.40c/sh.\n")
n60 = C.n60.values
print("Sessions behind the medians (n60): " + " · ".join(f"{a}–{b}: {((n60 >= a) & (n60 <= b)).sum():,}" for a, b in [(1, 19), (20, 29), (30, 39), (40, 49), (50, 59), (60, 60)]) + "\n")
print("| book | n | flat PF all | 2016–19 | 2020 | 2021–23 | 2024–26 |\n|---|---|---|---|---|---|---|")
def brow(nm, m):
    print(f"| {nm} | {m.sum():,} | {pf(R[m]):.3f} | {pf(R[m & E]):.3f} | {pf(R[m & (YR == 2020)]):.3f} | {pf(R[m & A]):.3f} | {pf(R[m & B]):.3f} |")
brow("this book (dv60_med ≥ $500k)", np.ones(len(C), bool))
brow("… also in GF-45's (its gates pass too)", C.in_gf45.values)
brow("… not in GF-45's", ~C.in_gf45.values)
brow("… fewer than 60 sessions behind the medians", n60 < 60)
print("\nBy year, flat PF [n]: " + " · ".join(f"{y} {pf(R[YR == y]):.2f} [{(YR == y).sum():,}]" for y in np.unique(YR)) + "\n")
print("## The current regime: fit 2021–23 → apply 2024–26, and the reverse (GF-45's criterion)\n")
print("| model | sized PF−1 A→B / B→A (**mean**) | units PF−1 A→B / B→A (**mean**) | Σ units·r A→B / B→A | top third PF | bottom third PF |\n|---|---|---|---|---|---|")
for nm, terms in MODELS:
    f, h = score(terms, A, B), score(terms, B, A)
    print(f"| {nm} | {f[0]:.3f} / {h[0]:.3f} (**{(f[0] + h[0]) / 2:.3f}**) | {f[1]:.3f} / {h[1]:.3f} (**{(f[1] + h[1]) / 2:.3f}**) | "
          f"{f[2]:+.1f} / {h[2]:+.1f} | {f[3]:.2f} / {h[3]:.2f} | {f[4]:.2f} / {h[4]:.2f} |")
print("\n## Across regimes: fit 2016–19 → apply 2021–26, and the reverse\n")
print("| model | sized PF−1 fwd / mirror (**mean**) | units PF−1 fwd / mirror (**mean**) | top third PF | bottom third PF |\n|---|---|---|---|---|")
for nm, terms in MODELS:
    f, h = score(terms, E, LATE), score(terms, LATE, E)
    print(f"| {nm} | {f[0]:.3f} / {h[0]:.3f} (**{(f[0] + h[0]) / 2:.3f}**) | {f[1]:.3f} / {h[1]:.3f} (**{(f[1] + h[1]) / 2:.3f}**) | "
          f"{f[3]:.2f} / {h[3]:.2f} | {f[4]:.2f} / {h[4]:.2f} |")

# ---------------------------------------------------------------- the production fit (2021–26) -> gf_coefficients.json
TERMS, FIT = ["lm60", "lrv60", "rv5_60"], LATE
mm, (bp, bw, bl) = model(TERMS, FIT)
print("\n## The new model fitted on 2021–26 (per SD)\n")
print("| term | odds × (p) | W × | L × | λ = L/W × |\n|---|---|---|---|---|")
for j, t in enumerate(["intercept", "ln dv60_med", "ln rvol60", "rvol60 < 0.005"]):
    print(f"| {t} | {np.exp(bp[j]):.3f} | {np.exp(bw[j]):.3f} | {np.exp(bl[j]):.3f} | {np.exp(bl[j] - bw[j]):.3f} |")
s = FIT
u = np.clip(mm, 0.125, 2)
print(f"\nPredicted PF−1 on 2021–26: p10 / p50 / p90 {np.quantile(mm[s], [.1, .5, .9]).round(2)}, mean {mm[s].mean():.3f}; "
      f"at the 0.125 floor {(mm[s] <= 0.125).mean() * 100:.1f} %, at the 2-unit cap {(mm[s] >= 2).mean() * 100:.1f} %. "
      f"In-sample units PF {pf(R[s], u[s]):.3f} vs flat {pf(R[s]):.3f}.\n")
print("## Calibration (2021–26, in-sample): predicted PF−1 decile vs realised\n")
print("| decile | n | mean predicted PF−1 | realised PF−1 | mean net % | median dv60_med $M | median rvol60 |\n|---|---|---|---|---|---|---|")
idx = np.where(s)[0]; dq = pd.qcut(mm[idx], 10, labels=False)
for k in range(10):
    j = idx[dq == k]
    print(f"| {k + 1} | {len(j):,} | {mm[j].mean():.2f} | {pf(R[j]) - 1:.2f} | {R[j].mean():+.2f} | {np.median(C.dv60_med.values[j]) / 1e6:.2f} | {np.median(C.rvol60.values[j]):.4f} |")
std = []
for t in TERMS:
    x = RAW[t]
    if t in FLAGS: lo, hi = None, None
    else: lo, hi = (float(v) for v in np.quantile(x[FIT], [0.01, 0.99])); x = np.clip(x, lo, hi)
    std.append({"term": t, "winsor": [lo, hi], "mean": float(x[FIT].mean()), "sd": float(x[FIT].std())})
pins_idx = np.random.default_rng(7).choice(np.where(FIT)[0], 200, replace=False)
COEF = {"model": "GapFader (weak-premarket gap-down long) PF−1 sizing, docs/gapfader_results.md GF-54 (the 60-session median)",
        "fit": f"2021-01 .. {C.date.max()[:7]}, n={int(FIT.sum())}; trade = LOO, ONE target at 50 % of the gap to 10:00, then the 10:00 1s vwap; 0.40c/sh",
        "gates": {"gap_max": -0.04, "open_min": 1.0, "dv60_med_min": FLOOR, "rvol_max": 0.02, "no_early_close": True, "listings": ["XNAS", "XASE"]},
        "cap": "MaxNotional = 0.0025 × dv60_med",
        "indicator_cutoff_et": {"XNAS": "09:29:25", "XASE": "09:29:50"}, "premarket_from_et": "04:00:00",
        "dv60_med": "median(volume × close) over the 60 PRIOR sessions in the ticker's episode (a > 45-day gap starts one); fewer when younger",
        "vol60_med": "median(volume / n) over the same 60 sessions × n(D): shares in D's scale",
        "min_sessions": "20 prior sessions in the episode (the census's row, the Oms's Reference); fewer than 60: all of them",
        "rvol": "premarket volume 04:00 → the listing's cutoff / vol60_med",
        "terms": {"lm60": "ln(dv60_med)", "lrv60": "ln(rvol + 0.001)", "rv5_60": "1 if rvol < 0.005 else 0"},
        "standardise": std, "design": ["1"] + TERMS,
        "beta_p": [float(v) for v in bp], "beta_w": [float(v) for v in bw], "beta_l": [float(v) for v in bl],
        "pf1": "p / ((1 − p) · exp(x·β_l) / exp(x·β_w)) − 1, p = 1 / (1 + exp(−x·β_p)); x = [1, (clip(term) − mean) / sd …]",
        "fit_mean_pf1": float(mm[FIT].mean()),
        "pins": [{"ticker": C.ticker.values[i], "date": C.date.values[i], "dv60_med": float(C.dv60_med.values[i]),
                  "vol60_med": float(C.vol60_med.values[i]), "rvol": float(C.rvol60.values[i]), "pf1": float(mm[i])} for i in sorted(pins_idx)]}
OUT = args.coefficients_out
json.dump(COEF, open(OUT, "w"), indent=1)
def pf1_from_json(dv, rv, J):
    raw = {"lm60": np.log(dv), "lrv60": np.log(rv + 0.001), "rv5_60": 1.0 * (rv < 0.005)}
    x = [1.0] + [((np.clip(raw[q["term"]], *q["winsor"]) if q["winsor"][0] is not None else raw[q["term"]]) - q["mean"]) / q["sd"] for q in J["standardise"]]
    x = np.array(x); p = 1 / (1 + np.exp(-x @ J["beta_p"])); return p / ((1 - p) * np.exp(x @ J["beta_l"]) / np.exp(x @ J["beta_w"])) - 1
J = json.load(open(OUT))
err = max(abs(pf1_from_json(q["dv60_med"], q["rvol"], J) - q["pf1"]) for q in J["pins"])
assert err < 1e-12, err
print(f"\nwrote {OUT}: fit n={int(FIT.sum()):,}, fit-mean PF−1 {mm[FIT].mean():.4f}, 200 pins (max |Δ| recomputed from the JSON {err:.1e})")
C.assign(pf1_new=mm, units_new=u)[["ticker", "date", "open", "close_m1", "div_m1", "gap", "gap_est", "dv20_prior", "dv60_med", "vol60_med", "n60", "rvolc", "rvol60", "r1", "in_gf45", "pf1_new", "units_new"]] \
    .to_parquet("data/equity/gapfader/model_v2.parquet")
log("done")
