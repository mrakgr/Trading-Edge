"""V2-54c — the MID share (user 2026-11-19): is midpoint TRF volume institutional, or retail-like?

mid_share_1200 = TRF volume at a sub-tick fraction in [0.4, 0.6] (midpoint crosses) / all counted volume over the last
1,200 present 1 s bars — recorded by the engine next to retail_share_1200 (same bars, same carry). BJZZ leaves the
midpoint band out of "retail" because a midpoint cross is ambiguous: retail price improvement at exactly half a spread,
or a dark-pool / ATS midpoint match (typically institutional).

On the V2-49 production book (35,519, ungated; data/flushfaderv2/retail_gate_book.parquet = the book + rshare/mshare,
written by flushfaderv2_retail_gate.py), PF with the reference's maker credit (2 × $0.001 / entry price):
  1. mid share quintiles — raw and within the retail gate's survivors
  2. the 2-D table: retail quintile × mid tercile
  3. gates at the SAME drop fraction as retail < 0.153 (19.4 %): retail, mid, retail + mid, retail − mid ...
  4. per-year for the candidates

  cd research && python -u scripts/equity/flushfaderv2_mid_share.py > logs/mid_share.log 2>&1; echo exit $?
"""
import numpy as np, pandas as pd

B = pd.read_parquet("data/flushfaderv2/retail_gate_book.parquet")
assert len(B) == 35519
B["r"] = B.ret_exit + 2 * 0.001 / B.entry_px
B["sr"] = B.r * B.multiplier
B["year"] = B.trade_date.str[:4]

def pf(x):
    x = np.asarray(x, float); w = x[x > 0].sum(); l = -x[x < 0].sum()
    return np.inf if l == 0 else w / l
def tab(rows, cols):
    print("| " + " | ".join(cols) + " |"); print("|" + "---|" * len(cols))
    for r in rows: print("| " + " | ".join(r) + " |")
f3 = lambda x: f"{x:.3f}"
def quint(x, k=5):
    e = np.quantile(x, np.linspace(0, 1, k + 1)[1:-1]); return np.searchsorted(e, x, side="right") + 1

print(f"book {len(B):,}; PF {pf(B.r):.3f}, sized {pf(B.sr):.3f}")
print(f"mid share p10/50/90 {np.quantile(B.mshare, .1):.3f} / {B.mshare.median():.3f} / {np.quantile(B.mshare, .9):.3f}; "
      f"retail p50 {B.rshare.median():.3f}")
print("Spearman mshare ~ " + ", ".join(f"{c} {B.mshare.corr(B[c], method='spearman'):+.3f}"
      for c in ["rshare", "entry_px", "volat", "gap_60", "dslo", "vrate", "s20", "multiplier"]))

print("\n## 1. Mid-share quintiles\n")
rows = []
B["mq"] = quint(B.mshare)
for q, d in B.groupby("mq"):
    k = d[d.rshare >= 0.153]
    rows.append([str(q), f"[{d.mshare.min():.3f}, {d.mshare.max():.3f}]", f"{len(d):,}", f3(pf(d.r)), f3(pf(d.sr)),
                 f3(pf(d[d.year <= "2023"].r)), f3(pf(d[d.year >= "2024"].r)), f3(d.rshare.median()), f3(d.entry_px.median()),
                 f3(d.multiplier.mean()), f"{len(k):,}", f3(pf(k.r)), f3(pf(k.sr))])
tab(rows, ["mid q", "range", "n", "PF", "sized PF", "PF 2020–23", "PF 2024–26", "median retail", "median px", "mean mult",
           "n after retail gate", "PF after", "sized PF after"])

print("\n## 2. Retail quintile × mid tercile (PF; n)\n")
B["rq"] = quint(B.rshare); B["mt"] = quint(B.mshare, 3)
rows = []
for rq in range(1, 6):
    r = [str(rq)]
    for mt in range(1, 4):
        d = B[(B.rq == rq) & (B.mt == mt)]; r.append(f"{pf(d.r):.2f} ({len(d):,})")
    rows.append(r)
e3 = np.quantile(B.mshare, [1 / 3, 2 / 3])
tab(rows, ["retail q", f"mid low (< {e3[0]:.3f})", "mid mid", f"mid high (≥ {e3[1]:.3f})"])

print("\n## 3. Gates at the same drop fraction (iso-n with retail < 0.153)\n")
base_n = int((B.rshare < 0.153).sum()); frac = base_n / len(B)
yrs = sorted(B.year.unique())
def gate_row(name, score, low_bad=True):
    # drop the frac of the book with the worst score (lowest if low_bad); the cut is a fixed full-book quantile
    cut = np.quantile(score, frac if low_bad else 1 - frac)
    drop = (score < cut) if low_bad else (score > cut)
    K = B[~drop]; D = B[drop]
    yr = [pf(K[K.year == y].r) - pf(B[B.year == y].r) for y in yrs]
    return [name, f"{'<' if low_bad else '>'} {cut:.3f}", f"{drop.sum():,}", f3(pf(K.r)), f3(pf(K.sr)), f"{15 * (K.sr.sum() - B.sr.sum()):+,.0f}",
            f3(pf(D.r)), f"{sum(v > 0 for v in yr)}/{len(yr)}", f3(pf(K[K.year == '2026'].r))]
tot = B.rshare + B.mshare
rows = [gate_row("retail share", B.rshare.values),
        gate_row("mid share (drop LOW)", B.mshare.values),
        gate_row("mid share (drop HIGH)", B.mshare.values, low_bad=False),
        gate_row("retail + mid share", tot.values),
        gate_row("retail − mid share", (B.rshare - B.mshare).values),
        gate_row("retail + ½ mid", (B.rshare + 0.5 * B.mshare).values),
        gate_row("retail / (1 − mid) (retail of the non-mid volume)", (B.rshare / (1 - B.mshare)).values)]
tab(rows, ["gate score", "cut", "dropped", "PF kept", "sized PF kept", "Δ % eq", "dropped PF", "years better", "2026 PF kept"])
print(f"\n(no gate: PF {pf(B.r):.3f}, sized {pf(B.sr):.3f}; 2026 PF {pf(B[B.year == '2026'].r):.3f})")

print("\n## 4. On top of the retail gate: a SECOND cut on mid (drop the worst 10 % of the survivors)\n")
K0 = B[B.rshare >= 0.153]
rows = []
for name, s, low in [("mid LOW", K0.mshare.values, True), ("mid HIGH", K0.mshare.values, False)]:
    cut = np.quantile(s, 0.1 if low else 0.9); drop = (s < cut) if low else (s > cut)
    K = K0[~drop]; D = K0[drop]
    yr = [pf(K[K.year == y].r) - pf(K0[K0.year == y].r) for y in yrs]
    rows.append([name, f"{'<' if low else '>'} {cut:.3f}", f"{drop.sum():,}", f3(pf(K.r)), f3(pf(K.sr)), f3(pf(D.r)), f"{sum(v > 0 for v in yr)}/{len(yr)}"])
tab(rows, ["second cut", "cut", "dropped", "PF kept", "sized PF kept", "dropped PF", "years better"])
print(f"\n(the retail gate alone: n {len(K0):,}, PF {pf(K0.r):.3f}, sized {pf(K0.sr):.3f})")
