"""GF-67 — GapFader's sizing with the MARKET's gap: QQQ's premarket gap × the stock's gap depth (user 2026-11-05, after a
Nasdaq sell-off morning: "Let's make a sizing model based on the QQQ when the gaps are large. We don't have to freeze the
model completely, we can make minor adjustments to improve our PF. What we must not do is abandon our approach completely.")

GF-66 found depth is flat on average but splits by the market: deep gaps pay on QQQ gap-down opens and are the worst bucket on
normal ones. Here that becomes TERMS added to production's model (GF-54: logit p, log W, log L by IRLS; terms standardised on
the fit set, winsorised 1/99 %; PF−1 = p / ((1 − p) · L / W) − 1; units = clip(PF−1, 0.125, 2)) — the approach unchanged.

Knowable at the LOO deadline (09:28), both:
  dep      ln(−gap_est): the stock's gap from the noisy auction estimate (GF-54's simulation of the imbalance price)
  qqq      QQQ's PREMARKET gap at 09:29:25 (the indicator cutoff): its last 1s vwap before 09:29:25 vs the previous close,
           the holder's return (daily_adjusted n / cum_div); capped at 0 above (an up-gap = 0: GF-66's effect is one-sided)
  dxq      dep × qqq (the interaction)
Scored as GF-54 (the same functions): fit 2021–23 → apply 2024–26 and the reverse; fit 2016–19 → apply 2021–26 and the reverse;
and the production fit (2021–26) applied out of sample to 2016–20.
    cd research && python3 -u scripts/equity/gapfader_qqq_sizing.py > data/equity/gapfader/qqq_sizing.md 2> logs/gapfader_qqq_sizing.log
"""
import os, sys, time
import duckdb, numpy as np, pandas as pd
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
C = pd.read_parquet("data/equity/gapfader/model_v2.parquet").sort_values(["date", "ticker"]).reset_index(drop=True)

# ---------------------------------------------------------------- QQQ's premarket gap at 09:29:25, per date (cached)
QF = "data/equity/gapfader/qqq_premarket_gap.parquet"
con = duckdb.connect(); con.execute("SET memory_limit='2GB'; SET threads=4")
Q0 = pd.read_parquet(QF) if os.path.exists(QF) else pd.DataFrame(columns=["date", "qqq_pm_px"])
need = sorted(set(C.date) - set(Q0.date))
if need:
    rows = []
    for i, d in enumerate(need):
        try:
            v = con.execute(f"""SELECT arg_max(vwap, bucket) FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                                WHERE ticker = 'QQQ' AND bucket >= 14400 AND bucket < 34165 AND volume > 0""").fetchone()[0]
        except duckdb.IOException: v = None
        rows.append((d, v))
        if i % 500 == 0: log(f"  QQQ premarket {i + 1}/{len(need)} {d}")
    Q0 = pd.concat([Q0, pd.DataFrame(rows, columns=["date", "qqq_pm_px"])], ignore_index=True)
    Q0.to_parquet(QF + ".tmp"); os.replace(QF + ".tmp", QF)
D = con.execute("""SELECT strftime(date, '%Y-%m-%d') date, close c, n, cum_div cd, open o,
                          lag(close) OVER w c1, lag(n) OVER w n1, lag(cum_div) OVER w cd1
                   FROM 'data/v2/daily_adjusted.parquet' WHERE ticker = 'QQQ' WINDOW w AS (ORDER BY date)""").df()
Q = Q0.merge(D, on="date", how="left")
Q["qqq_pm_gap"] = ((Q.qqq_pm_px * Q.n - Q.c1 * Q.n1) + (Q.cd - Q.cd1)) / (Q.c1 * Q.n1)
Q["qqq_open_gap"] = ((Q.o * Q.n - Q.c1 * Q.n1) + (Q.cd - Q.cd1)) / (Q.c1 * Q.n1)
C = C.merge(Q[["date", "qqq_pm_gap", "qqq_open_gap"]], on="date", how="left")
log(f"book {len(C):,}; QQQ premarket gap missing on {C.qqq_pm_gap.isna().sum():,} rows")
C = C[C.qqq_pm_gap.notna()].reset_index(drop=True)

# ---------------------------------------------------------------- GF-54's machinery, verbatim (gapfader_model_v2.py)
R = C.r1.values; WIN = R > 0; YR = C.date.str[:4].astype(int).values
mq = np.minimum(C.qqq_pm_gap.values, 0.0)
RAW = {"lm60": np.log(C.dv60_med.values), "lrv60": np.log(C.rvol60.values + 0.001), "rv5_60": (C.rvol60.values < 0.005) * 1.0,
       "dep": np.log(-C.gap_est.values), "qqq": mq, "dxq": np.log(-C.gap_est.values) * mq,
       "deep10": (C.gap_est.values <= -0.10) * 1.0, "qd1": (C.qqq_pm_gap.values <= -0.01) * 1.0}
RAW["d10xq1"] = RAW["deep10"] * RAW["qd1"]
FLAGS = {"rv5_60", "deep10", "qd1", "d10xq1"}
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
    m = model(terms, fit)[0]
    w = np.clip(m / m[fit].mean(), 0.25, 4)[app]; w = w / w.mean(); u = np.clip(m, 0.125, 2)[app]
    q3, q1 = np.quantile(m[fit], [2 / 3, 1 / 3]); a = m[app]; r = R[app]
    return (pf(r, w) - 1, pf(r, u) - 1, (r * u).sum() / 100, pf(r[a >= q3]), pf(r[a < q1]))
E, LATE, A, B, EARLY = YR <= 2019, YR >= 2021, (YR >= 2021) & (YR <= 2023), YR >= 2024, YR <= 2020
BASE = ["lm60", "lrv60", "rv5_60"]
MODELS = [("**production (GF-54)**", BASE), ("+ depth", BASE + ["dep"]), ("+ QQQ", BASE + ["qqq"]), ("+ depth + QQQ", BASE + ["dep", "qqq"]),
          ("**+ depth + QQQ + depth × QQQ**", BASE + ["dep", "qqq", "dxq"]),
          ("+ deep10 + QQQ ≤ −1 % + both (flags)", BASE + ["deep10", "qd1", "d10xq1"]),
          ("**+ depth + depth × QQQ** (no market main effect)", BASE + ["dep", "dxq"]),
          ("**+ deep10 + deep10 × QQQ ≤ −1 %** (flags, no market main effect)", BASE + ["deep10", "d10xq1"])]

qd = C.drop_duplicates("date")
print("# GF-67 — GapFader's sizing with QQQ's premarket gap × the stock's depth (scripts/equity/gapfader_qqq_sizing.py)\n")
print(f"Book: production's GF-54 book, {len(C):,} trades {C.date.min()} .. {C.date.max()} (all of it, PF−1 ≤ 0 included, as GF-54 fits).\n")
print(f"QQQ's premarket gap at 09:29:25 vs its official-open gap, over the book's {len(qd):,} days: ρ {qd.qqq_pm_gap.corr(qd.qqq_open_gap):.3f}, "
      f"median |difference| {100 * (qd.qqq_pm_gap - qd.qqq_open_gap).abs().median():.3f} pp; days ≤ −1 % premarket {int((qd.qqq_pm_gap <= -0.01).sum())}, "
      f"at the open {int((qd.qqq_open_gap <= -0.01).sum())}.\n")
print("## 1. The current regime: fit 2021–23 → apply 2024–26, and the reverse (GF-45's criterion)\n")
print("| model | sized PF−1 A→B / B→A (**mean**) | units PF−1 A→B / B→A (**mean**) | Σ units·r A→B / B→A | top third PF | bottom third PF |\n|---|---|---|---|---|---|")
for nm, terms in MODELS:
    f, h = score(terms, A, B), score(terms, B, A)
    print(f"| {nm} | {f[0]:.3f} / {h[0]:.3f} (**{(f[0] + h[0]) / 2:.3f}**) | {f[1]:.3f} / {h[1]:.3f} (**{(f[1] + h[1]) / 2:.3f}**) | "
          f"{f[2]:+.1f} / {h[2]:+.1f} | {f[3]:.2f} / {h[3]:.2f} | {f[4]:.2f} / {h[4]:.2f} |")
print("\n## 2. Across regimes: fit 2016–19 → apply 2021–26, and the reverse\n")
print("| model | sized PF−1 fwd / mirror (**mean**) | units PF−1 fwd / mirror (**mean**) | Σ units·r fwd / mirror | top third PF | bottom third PF |\n|---|---|---|---|---|---|")
for nm, terms in MODELS:
    f, h = score(terms, E, LATE), score(terms, LATE, E)
    print(f"| {nm} | {f[0]:.3f} / {h[0]:.3f} (**{(f[0] + h[0]) / 2:.3f}**) | {f[1]:.3f} / {h[1]:.3f} (**{(f[1] + h[1]) / 2:.3f}**) | "
          f"{f[2]:+.1f} / {h[2]:+.1f} | {f[3]:.2f} / {h[3]:.2f} | {f[4]:.2f} / {h[4]:.2f} |")
print("\n## 3. Production's fit window (2021–26) applied OUT OF SAMPLE to 2016–20, by the market's premarket gap\n")
print("Units = clip(PF−1, 0.125, 2) as live; a non-positive PF−1 is REFUSED live (NoEdge, since 11-04) — counted as 0 units here.\n")
print("| model | units PF 2016–20 | Σ units·r 2016–20 | QQQ ≤ −1 %: units PF · Σ | QQQ −1..−0.3 %: units PF · Σ | QQQ > −0.3 %: units PF · Σ | refused 2016–20 |\n|---|---|---|---|---|---|---|")
for nm, terms in MODELS:
    m = model(terms, LATE)[0]; u = np.where(m > 0, np.clip(m, 0.125, 2), 0.0)
    cells = []
    for lo, hi in ((-9, -0.01), (-0.01, -0.003), (-0.003, 9)):
        s = EARLY & (C.qqq_pm_gap.values > lo) & (C.qqq_pm_gap.values <= hi)
        cells.append(f"{pf(R[s], u[s]):.2f} · {(R[s] * u[s]).sum() / 100:+.1f}")
    print(f"| {nm} | {pf(R[EARLY], u[EARLY]):.3f} | {(R[EARLY] * u[EARLY]).sum() / 100:+.1f} | " + " | ".join(cells) + f" | {100 * (m[EARLY] <= 0).mean():.1f} % |")
print("\n## 4. The interaction model fitted on 2021–26 (per SD)\n")
TERMS = BASE + ["dep", "qqq", "dxq"]
mm, (bp, bw, bl) = model(TERMS, LATE)
print("| term | odds × (p) | W × | L × | λ = L/W × |\n|---|---|---|---|---|")
for j, t in enumerate(["intercept", "ln dv60_med", "ln rvol60", "rvol60 < 0.005", "ln −gap_est", "QQQ pm gap (≤ 0)", "ln −gap × QQQ"]):
    print(f"| {t} | {np.exp(bp[j]):.3f} | {np.exp(bw[j]):.3f} | {np.exp(bl[j]):.3f} | {np.exp(bl[j] - bw[j]):.3f} |")
m0 = model(BASE, LATE)[0]
print("\nPredicted PF−1 (2021–26 fit) by cell, production vs the interaction model — mean predicted · realised PF−1:\n")
print("| QQQ premarket gap | depth | trades | production predicted | interaction predicted | realised PF−1 |\n|---|---|---|---|---|---|")
for ql, qlo, qhi in (("≤ −1 %", -9, -0.01), ("−1..−0.3 %", -0.01, -0.003), ("> −0.3 %", -0.003, 9)):
    for dl, dlo, dhi in (("−4..−6 %", -0.06, -0.04), ("−6..−10 %", -0.10, -0.06), ("≤ −10 %", -9, -0.10)):
        s = (C.qqq_pm_gap.values > qlo) & (C.qqq_pm_gap.values <= qhi) & (C.gap_est.values > dlo) & (C.gap_est.values <= dhi)
        print(f"| {ql} | {dl} | {s.sum():,} | {m0[s].mean():.2f} | {mm[s].mean():.2f} | {pf(R[s]) - 1:.2f} |")
C.assign(pf1_prod=m0, pf1_qqq=mm).to_parquet("data/equity/gapfader/qqq_sizing.parquet")

print("\n## 5. Calibration of every model (fit 2021–26), mean predicted PF−1 vs realised; March 2020 excluded from the realised (and the n)\n")
P = {nm: model(terms, LATE)[0] for nm, terms in MODELS}
nofeb = ~C.date.str.startswith("2020-03").values
print("| QQQ pm gap | depth | n (ex 2020-03) | realised PF−1 | " + " | ".join(nm.replace("**", "") for nm, _ in MODELS) + " |\n|---|---|---|---|" + "---|" * len(MODELS))
for ql, qlo, qhi in (("≤ −1 %", -9, -0.01), ("−1..−0.3 %", -0.01, -0.003), ("> −0.3 %", -0.003, 9)):
    for dl, dlo, dhi in (("−4..−6 %", -0.06, -0.04), ("−6..−10 %", -0.10, -0.06), ("≤ −10 %", -9, -0.10)):
        c = nofeb & (C.qqq_pm_gap.values > qlo) & (C.qqq_pm_gap.values <= qhi) & (C.gap_est.values > dlo) & (C.gap_est.values <= dhi)
        print(f"| {ql} | {dl} | {c.sum():,} | **{pf(R[c]) - 1:.2f}** | " + " | ".join(f"{P[nm][c].mean():.2f}" for nm, _ in MODELS) + " |")
print("\n## 6. Day-level risk (fit 2021–26; units as live: clip(PF−1, 0.125, 2), refused at PF−1 ≤ 0): the DAY's Σ units·r\n")
print("| model | all days: PF of the days · Σ | QQQ ≤ −1 % days: PF · Σ | ... ex 2020-03 | worst day | 5 worst days Σ | units on QQQ ≤ −1 % days / other days (mean per trade) |\n|---|---|---|---|---|---|---|")
for nm, _ in MODELS:
    u = np.where(P[nm] > 0, np.clip(P[nm], 0.125, 2), 0.0)
    d = pd.DataFrame({"date": C.date, "x": R * u / 100, "q": C.qqq_pm_gap.values <= -0.01, "u": u}).groupby("date").agg(x=("x", "sum"), q=("q", "first"))
    dd = d[d.q]; de = dd[~dd.index.str.startswith("2020-03")]
    qm = C.qqq_pm_gap.values <= -0.01
    print(f"| {nm} | {pf(d.x.values):.2f} · {d.x.sum():+.1f} | {pf(dd.x.values):.2f} · {dd.x.sum():+.1f} | {pf(de.x.values):.2f} · {de.x.sum():+.1f} | "
          f"{d.x.min():+.2f} ({d.x.idxmin()}) | {d.x.nsmallest(5).sum():+.2f} | {u[qm].mean():.2f} / {u[~qm].mean():.2f} |")
