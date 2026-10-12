"""FlushFaderV2 — V2-2: the shape of every candidate feature on the broad book (2026-10-12).

Book = data/flushfaderv2/book_features.parquet (the 37,254-trade broad reference book + every slice input, joined on
symbol, trade_date, signal_sec). The book's membership is fixed (sizing never changes which trades are taken), so the
octile tables need no replay. Return = ret_exit net of the $0.001/sh/side rebate credit (the sizing measure, S49s).

Per feature: null count, octile edges, and per octile n / PF−1 / avg% / win%, plus PF−1 in 2020–23 and 2024–26 (the
two holdout blocks). Run from research/:
    python -u scripts/equity/flushfaderv2_features.py > data/flushfaderv2/features.log 2>&1; echo exit $?
"""
import argparse
import numpy as np, pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument("--book", default="data/flushfaderv2/book_features.parquet")
ap.add_argument("--credit", type=float, default=0.001)
ap.add_argument("--out", default="data/flushfaderv2/features.md")
ap.add_argument("--q", type=int, default=8)
args = ap.parse_args()

D = pd.read_parquet(args.book)
D["r"] = D.ret_exit + 2 * args.credit / D.entry_px
D["yr"] = D.trade_date.str[:4].astype(int)

FEATS = [  # (column, label) — the efficiency-variant list in curve order, then the voices
    ("s_l300", "lows300"), ("s_eff10a", "eff10"), ("s_v10r", "v10r"), ("s_z20", "z20"), ("s_l180", "lows180"),
    ("s_crf", "crf"), ("coil5", "coil5lo"), ("s20", "s20"), ("speed", "speed"), ("accel", "accel"), ("rngf", "rngf"),
    ("rflow", "rflow"), ("d1m", "d1m"), ("eff20a", "eff20"), ("s5", "s5"), ("ht", "cascade: ht"), ("ssh", "cascade: ssh"),
    ("k20", "K"), ("dlv", "dlv"), ("ssf", "ssf"), ("e9", "e9"),
    ("volat", "v20 (volat)"), ("d20a", "d20a"), ("dslo", "dslo"), ("vexp", "vexp"), ("vcrush", "vcrush"), ("ac1", "acneg (ac1)"),
    ("esf", "legage (esf)"), ("dsu", "dsu"),
    ("gap_60", "A3: gap_60"), ("rate600", "A3: rate600"),
]

def pf1(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum()
    return np.inf if l == 0 else g / l - 1

def fmt(x, d=2): return "inf" if np.isinf(x) else ("—" if np.isnan(x) else f"{x:.{d}f}")

out = [f"# V2-2 feature shapes on the broad book ({len(D):,} trades, net of the ${args.credit}/sh/side credit)\n",
       f"Book PF−1 {fmt(pf1(D.r.values))}, avg {D.r.mean()*100:.2f} %, win {(D.r > 0).mean()*100:.1f} %\n"]
summary = []
for col, lab in FEATS:
    x = D[col].astype(float)
    nn = x.isna().sum()
    v = x.dropna()
    # octiles on distinct quantile edges (counters with heavy ties collapse to fewer bands)
    edges = np.unique(np.quantile(v, np.linspace(0, 1, args.q + 1)))
    if len(edges) < 3: edges = np.unique(v)
    b = np.clip(np.searchsorted(edges, x, side="right") - 1, 0, len(edges) - 2)
    b = np.where(x.isna(), -1, b)
    rows = [f"## {lab} (`{col}`) — nulls {nn:,}\n",
            "| band | range | n | PF−1 | avg% | win% | PF−1 2020–23 | PF−1 2024–26 |", "|---|---|---|---|---|---|---|---|"]
    pfs = []
    for k in sorted(set(b)):
        m = b == k
        r = D.r.values[m]; y = D.yr.values[m]
        rng = "null" if k < 0 else f"[{edges[k]:.4g}, {edges[k+1]:.4g}{']' if k == len(edges) - 2 else ')'}"
        p = pf1(r); pfs.append(p) if k >= 0 else None
        rows.append(f"| {k} | {rng} | {m.sum():,} | {fmt(p)} | {r.mean()*100:.2f} | {(r > 0).mean()*100:.1f} | "
                    f"{fmt(pf1(r[y <= 2023]))} | {fmt(pf1(r[y >= 2024]))} |")
    # Spearman of the feature with the trade return: a one-number monotonicity read
    rho = pd.Series(v.values).rank().corr(pd.Series(D.r[x.notna()].values).rank())
    summary.append((lab, col, nn, len(edges) - 1, rho, min(pfs), max(pfs), pfs[0], pfs[-1]))
    out += rows + [""]
S = ["## Summary\n", "| feature | col | nulls | bands | Spearman ρ (ret) | min band PF−1 | max band PF−1 | low band | high band |",
     "|---|---|---|---|---|---|---|---|---|"]
S += [f"| {a} | `{c}` | {n:,} | {k} | {rho:+.3f} | {fmt(lo)} | {fmt(hi)} | {fmt(b0)} | {fmt(b1)} |" for a, c, n, k, rho, lo, hi, b0, b1 in summary]
txt = "\n".join(out[:2] + S + [""] + out[2:])
open(args.out, "w").write(txt)
print("\n".join(S))
