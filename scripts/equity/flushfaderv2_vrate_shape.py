"""V2-52: is the volume RATE's effect captured by its LINEAR term? (user 2026-11-09: "Given the large difference between
exhaustion and acceleration, I feel that the improvement in the overall profit factor of the system should have been more
significant than the 10% we're getting currently. Is the current profit factor right, or is the linear model feature not
doing a good enough job of predicting the profit factor?")
On the production reference book (V2-49, 35,519):
  A. by vrate DECILE: the realized equal-size PF (net of the maker credit) vs the OOS-predicted mean PF−1 of the model
     WITHOUT a volume term and of B (V2-42's harness: fwd 2020–23 -> 2024–26, mirror) — calibration of the linear term
  B. the vrate SHAPE out of sample: B's linear z vs + z², decile dummies, hinges at p10 / p50 / p90, a tighter / looser
     winsorisation — the paired day bootstrap against B
  C. the CEILING: sizing by the REALIZED decile PF−1 alone (in-sample, the best any function of vrate alone can do) vs
     equal size, and the in-sample sized PF of B
Run from research/:  python3 -u scripts/equity/flushfaderv2_vrate_shape.py > logs/vrate_shape.log 2>&1
"""
import argparse, json, numpy as np, pandas as pd
ap = argparse.ArgumentParser()
ap.add_argument('--book', default='data/flushfaderv2/flushfaderv2_reference_book.parquet')
ap.add_argument('--coef', default='models/flushfaderv2/v2_coefficients.json')
args = ap.parse_args()
CREDIT, YOUNG_SCALE = 0.001, 0.8
def fit_logistic(X, y, it=60):
    b = np.zeros(X.shape[1])
    for _ in range(it):
        eta = X @ b; mu = 1 / (1 + np.exp(-eta)); w = mu * (1 - mu) + 1e-12
        bn = np.linalg.solve((X * w[:, None]).T @ X, (X * w[:, None]).T @ (eta + (y - mu) / w))
        if np.max(np.abs(bn - b)) < 1e-10: return bn
        b = bn
    return b
def fit_loglink(X, y, it=60):
    b = np.zeros(X.shape[1]); b[0] = np.log(y.mean())
    for _ in range(it):
        eta = X @ b; mu = np.exp(eta)
        bn = np.linalg.solve((X * mu[:, None]).T @ X, (X * mu[:, None]).T @ (eta + (y - mu) / mu))
        if np.max(np.abs(bn - b)) < 1e-10: return bn
        b = bn
    return b
def pf1(r): g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l - 1
def zs(x):
    lo, hi = np.nanquantile(x, [0.01, 0.99]); c = np.clip(x, lo, hi); return np.nan_to_num((c - np.nanmean(c)) / np.nanstd(c))


def pf(r): l = -r[r < 0].sum(); return np.inf if l == 0 else r[r > 0].sum() / l
def zq(x, lo, hi):
    a, b = np.nanquantile(x, [lo, hi]); c = np.clip(x, a, b); return np.nan_to_num((c - np.nanmean(c)) / np.nanstd(c))

B = pd.read_parquet(args.book).sort_values(['trade_date', 'symbol', 'signal_sec']).reset_index(drop=True)
C = json.load(open(args.coef))
R = (B.ret_exit.values + 2 * CREDIT / B.entry_px.values) * 100; WIN = R > 0
YR = B.trade_date.astype(str).str[:4].astype(int).values; young = B.young.values.astype(bool)
v = B.vrate.values.astype(float)
gb = B.gap_band.values
common = [np.ones(len(B)), gb == 1, gb == 2, np.log(np.clip(B.volat.values, *C['volat_clip_bp'])), B.rate600_fast.values, B.s_tier.values, B.dslo_flag.values,
          zq(B.s20.values, .01, .99), zq(B.cusr_dn_k025.values.astype(float), .01, .99)]
dec = np.digitize(v, np.nanquantile(v, np.linspace(.1, .9, 9)))
def predict(Xm, f):
    bp = fit_logistic(Xm[f], WIN[f] * 1.0); bw = fit_loglink(Xm[f & WIN], R[f & WIN]); bl = fit_loglink(Xm[f & ~WIN], -R[f & ~WIN])
    p = 1 / (1 + np.exp(-(Xm @ bp))); return p * np.exp(Xm @ bw) / ((1 - p) * np.exp(Xm @ bl)) - 1
def run(extra):
    Xm = np.column_stack(common + extra).astype(float); x = np.zeros(len(B)); m = np.zeros(len(B))
    for fm in (YR <= 2023, YR > 2023):
        app = ~fm; mm = predict(Xm, fm & ~young); mm = np.where(young, YOUNG_SCALE * mm, mm)
        x[np.flatnonzero(app)] = (np.clip(mm, .125, 2.0) * R)[app]; m[np.flatnonzero(app)] = mm[app]
    return x, m
z = zq(v, .01, .99)
x0, m0 = run([]); xB, mB = run([z])
print(f"{len(B):,} trades. vrate (per-second ln-volume slope × 1,200) deciles: edges " + ", ".join(f"{e:+.2f}" for e in np.nanquantile(v, np.linspace(.1, .9, 9))))
print(f"\n**A. by vrate decile** — realized at EQUAL size vs the OOS-predicted PF−1 (no volume term / B)\n")
print("| decile | vrate range | trades | win % | mean win % | mean loss % | ≤ −20 % per 1,000 | realized PF | realized PF−1 | predicted PF−1: no volume | B | B sized net / trade |\n|---|---|---|---|---|---|---|---|---|---|---|---|")
for d in range(10):
    s = dec == d; r = R[s]
    print(f"| {d + 1} | {v[s].min():+.2f} … {v[s].max():+.2f} | {s.sum():,} | {100 * WIN[s].mean():.1f} | {r[r > 0].mean():.2f} | {-r[r < 0].mean():.2f} | {1000 * (r <= -20).mean():.1f} | "
          f"{pf(r):.3f} | {pf(r) - 1:.3f} | {m0[s].mean():.3f} | {mB[s].mean():.3f} | {xB[s].mean():+.3f} |")
print(f"| all | | {len(B):,} | {100 * WIN.mean():.1f} | | | {1000 * (R <= -20).mean():.1f} | {pf(R):.3f} | {pf(R) - 1:.3f} | {m0.mean():.3f} | {mB.mean():.3f} | {xB.mean():+.3f} |")
# realized vs predicted, decile 1 vs 10
print("\n(realized PF−1 is POOLED — Σwins / Σlosses — while the predicted is the MEAN of per-trade PF−1; compare their SHAPE across deciles)")

ud, inv = np.unique(B.trade_date.values, return_inverse=True); rng = np.random.default_rng(20261109)
picks = np.stack([np.bincount(rng.integers(0, len(ud), len(ud)), minlength=len(ud))[inv] for _ in range(2000)]).astype(float)
def pfw(x): return (picks @ np.where(x > 0, x, 0)) / -(picks @ np.where(x < 0, x, 0)) - 1
pB = pfw(xB)
q = np.nanquantile(v, [.1, .5, .9])
SHAPES = {'no volume term (V2-25 design)': [], 'B: linear z (winsor 1 / 99 %)': [z], '+ z²': [z, z * z],
          'decile dummies (9)': [(dec == d).astype(float) for d in range(1, 10)],
          'hinges at p10 / p50 / p90': [z] + [np.maximum(0, np.clip(v, *np.nanquantile(v, [.01, .99])) - k) / np.nanstd(v) for k in q],
          'linear, winsor 5 / 95 %': [zq(v, .05, .95)], 'linear, winsor 0.1 / 99.9 %': [zq(v, .001, .999)],
          'z × ln volat (interaction)': [z, z * (common[3] - common[3].mean())]}
print("\n**B. the vrate SHAPE out of sample** (sized PF−1, Δ vs B, paired day bootstrap)\n")
print("| shape | OOS sized PF−1 | Δ vs B [90 %] · share > 0 | net units·% |\n|---|---|---|---|")
for n, extra in SHAPES.items():
    x, _ = run(extra); d = pfw(x) - pB
    print(f"| {n} | {pf(x) - 1:.4f} | {pf(x) - pf(xB):+.4f} [{np.quantile(d, .05):+.4f}, {np.quantile(d, .95):+.4f}] · {100 * (d > 0).mean():.0f} % | {x.sum():+,.0f} |", flush=True)

print("\n**C. the ceiling** (IN-SAMPLE; units clip(PF−1, 0.125, 2) on the trade's return)\n")
eq = R
dpf1 = np.array([pf(R[dec == d]) - 1 for d in range(10)])[dec]
oracle = np.clip(dpf1, .125, 2) * R
print("| sizing | sized PF | sized PF−1 | net units·% | vs equal size |\n|---|---|---|---|---|")
for n, x in [('equal size', R), ('vrate decile ORACLE (each decile at its own realized PF−1)', oracle),
             ('B, OOS', xB), ('no volume term, OOS', x0)]:
    print(f"| {n} | {pf(x):.3f} | {pf(x) - 1:.3f} | {x.sum():+,.0f} | PF−1 × {(pf(x) - 1) / (pf(R) - 1):.2f} |")
