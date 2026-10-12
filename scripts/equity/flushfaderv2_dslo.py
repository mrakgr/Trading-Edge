"""FlushFaderV2 — V2-4: the dslo step (signal_vwap / running session low − 1) — fine bands by year, and the flag's
threshold swept on the year-block holdouts (A3 + one flag). Run from research/:
    python -u scripts/equity/flushfaderv2_dslo.py > data/flushfaderv2/dslo.log 2>&1; echo exit $?
"""
import sys, os
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import flushfaderv2_linear as L        # the book, R, YR, the A3 design and the scorer

x = L.B.dslo.values.astype(float)
edges = [0, 0.005, 0.01, 0.02, 0.03, 0.04, 0.05, 0.06, 0.08, 0.10, 0.15, 0.20, 0.30, 1e9]
print("## Fine dslo bands — PF−1 net of the credit, by year (n in brackets)\n")
print("| dslo | n | PF−1 | avg% | win% | " + " | ".join(map(str, L.YEARS)) + " |")
print("|---|---|---|---|---|" + "---|" * len(L.YEARS))
for a, b in zip(edges[:-1], edges[1:]):
    m = (x >= a) & (x < b); r = L.R[m]
    yrs = " | ".join(f"{L.f(L.pf1(r[L.YR[m] == y]), 2)} ({(L.YR[m] == y).sum()})" for y in L.YEARS)
    print(f"| [{a*100:g}%, {'∞' if b > 1 else f'{b*100:g}%'}) | {m.sum():,} | {L.f(L.pf1(r))} | {r.mean():.2f} | {(r > 0).mean()*100:.1f} | {yrs} |")

print("\n## The flag's threshold swept: A3 + (dslo ≥ t), sized PF−1 on the holdouts\n")
print("| t | share flagged | fwd | mirror | mean | in-sample |\n|---|---|---|---|---|---|")
for t in [0.005, 0.01, 0.02, 0.03, 0.04, 0.046, 0.05, 0.06, 0.08, 0.10, 0.15]:
    L.CAND["flag"] = ("dslo", ("ge", t)); L.RAW["flag"] = x
    sf, sm, si, _ = L.score(["flag"])
    print(f"| {t*100:g}% | {(x >= t).mean()*100:.1f}% | {L.f(sf)} | {L.f(sm)} | {L.f((sf + sm) / 2)} | {L.f(si)} |")
L.CAND["logdslo"] = ("dslo", "lin"); L.RAW["logdslo"] = np.log(x + 0.01)
sf, sm, si, _ = L.score(["logdslo"])
print(f"| log(dslo + 1%) linear | — | {L.f(sf)} | {L.f(sm)} | {L.f((sf + sm) / 2)} | {L.f(si)} |")
