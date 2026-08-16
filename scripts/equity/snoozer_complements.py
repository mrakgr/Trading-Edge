"""S43cl — the 16-CELL COMPLEMENTS TABLE: every combination of the four features.

⭐ USER (2026-08-16): "All of these are worth trading, just with different sizes. Let's
make a complements table... There should be a total of 16 combinations in the end. That
should be more informative than trying to pick the thresholds. We know what our A++ book
would be, but on live trading we'd want to trade the lesser cells with smaller size to
maximize our net profits."

This is the FlushFader broker-doc lesson applied to the Snoozer (§S43cc): the trades
outside the top cell had PF ~2 and were being thrown away. A threshold answers "in or
out"; a lattice answers "how much", which is the question that actually gets traded.

## The four binary features (W = 30m throughout, §S43ck)

    V  volat_open30    <= median     LOW volatility   — buys survival
    I  dv_over_open30  >= median     HIGH intensity   — buys expectancy
    B  bar_over_open30 >= median     HIGH relative persistence
    G  gaps            <= median     LOW absolute gaps

`+` = the favourable side, `−` = its complement. The 16 cells are DISJOINT and their
union is the whole 4,164-trade population, so nothing is discarded and the n column sums
to the population.

## ⚠ THE CELLS ARE VERY UNEQUAL, BY CONSTRUCTION

The features are far from independent — Spearman(volat_open30, dv_over_open30) = −0.607,
so V+I+ holds 1,509 trades where independence predicts 1,040 (1.45×). The corner cells
are therefore thin and their PFs are correspondingly uncertain. **Read n before PF on
every row**; a 60-trade cell is a hint, not a measurement.

## ⚠⚠ SIZING — WHAT NOT TO DO HERE

The house standard is `trimmed PF − 1` on VOL-NORMALISED returns
(`feedback_trim_bottom_5pct_when_comparing`). The vol-normalisation is **omitted** in
this script and that is deliberate: the natural normaliser would be `volat_open30`,
which is one of the four cell-defining features. Dividing by it would make the V+ rows
mechanically larger and the V− rows smaller — the sizing signal would partly BE the
cell definition. So multipliers here are trimmed PF − 1 on RAW returns, scaled to the
weakest tradeable cell = 1.00.

⚠ Both raw and 5%-trimmed PF are printed (`feedback_show_raw_and_clip_pf`). Size on the
TRIMMED column; the raw column is there to expose the fat tail.

Usage:  python scripts/equity/snoozer_complements.py [--side long]
"""
import argparse
import itertools

import duckdb
import numpy as np
import pandas as pd

pd.set_option("display.width", 260)
pd.set_option("display.max_columns", 60)

ap = argparse.ArgumentParser()
ap.add_argument("--shape", default="data/equity/flushfader/snoozer_shape.parquet")
ap.add_argument("--volat", default="data/equity/flushfader/snoozer_volat.parquet")
ap.add_argument("--side", choices=["long", "short"], default="long")
ap.add_argument("--chg", type=float, default=0.06)
ap.add_argument("--w", type=int, default=30)
ap.add_argument("--trim", type=float, default=0.05)
args = ap.parse_args()

sign = "-" if args.side == "short" else ""
cond = f"chg60k59 > {args.chg}" if args.side == "short" else f"chg60k59 < {-args.chg}"
W = args.w

con = duckdb.connect(config={"memory_limit": "8GB", "threads": 8})
con.execute("SET enable_progress_bar=false")
con.execute(f"""CREATE OR REPLACE TEMP TABLE S AS
SELECT s.ticker, s.date, year(s.date) AS yr, {sign}(s.ovn_from_lim59) AS r,
       3540 - s.nb60k59 AS gaps, s.dv_over_open{W} AS inten,
       s.bar_over_open{W} AS pers, v.volat_open{W} AS volat
FROM read_parquet('{args.shape}') s
JOIN read_parquet('{args.volat}') v ON v.ticker = s.ticker AND v.date = s.date
WHERE {cond} AND s.ovn_from_lim59 IS NOT NULL AND s.dv_lh > 0
  AND s.dv_over_open15 IS NOT NULL""")
A = con.execute("SELECT * FROM S").fetchdf()
r = A.r.values
n = len(A)
yrs = sorted(A.yr.unique())

# ⭐ favourable direction per feature; every one FLIPS on the short side (§S43cb)
if args.side == "long":
    good = {"V": A.volat.values <= np.nanquantile(A.volat.values, .5),
            "I": A.inten.values >= np.nanquantile(A.inten.values, .5),
            "B": A.pers.values >= np.nanquantile(A.pers.values, .5),
            "G": A.gaps.values <= np.nanquantile(A.gaps.values, .5)}
else:
    good = {"V": A.volat.values >= np.nanquantile(A.volat.values, .5),
            "I": A.inten.values <= np.nanquantile(A.inten.values, .5),
            "B": A.pers.values <= np.nanquantile(A.pers.values, .5),
            "G": A.gaps.values >= np.nanquantile(A.gaps.values, .5)}
THR = {"V": np.nanquantile(A.volat.values, .5) * 1e4,
       "I": np.nanquantile(A.inten.values, .5),
       "B": np.nanquantile(A.pers.values, .5),
       "G": np.nanquantile(A.gaps.values, .5)}
print(f"side={args.side}   W={W}m   n = {n:,}   thresholds: "
      f"volat {THR['V']:.1f}bp · inten {THR['I']:.2f} · pers {THR['B']:.2f} · "
      f"gaps {THR['G']:.0f}s")


def pf(x):
    g, l = x[x > 0].sum(), -x[x < 0].sum()
    return float("inf") if l == 0 else g / l


def tpf(x):
    if len(x) < 20:
        return float("nan")
    return pf(x[x >= np.quantile(x, args.trim)])


rows = []
for combo in itertools.product([1, 0], repeat=4):
    m = np.ones(n, bool)
    lab = ""
    for bit, k in zip(combo, "VIBG"):
        m &= good[k] if bit else ~good[k]
        lab += f"{k}{'+' if bit else '−'}"
    k = int(m.sum())
    if k == 0:
        continue
    x = r[m]
    neg = [y for y in yrs
           if (m & (A.yr.values == y)).sum() >= 10
           and pf(r[m & (A.yr.values == y)]) < 1.0]
    rows.append({"cell": lab, "score": sum(combo), "n": k,
                 "share%": f"{k/n*100:.1f}",
                 "PF raw": round(pf(x), 3),
                 "PF trim5": (round(tpf(x), 3) if tpf(x) == tpf(x) else np.nan),
                 "mean%": round(x.mean()*100, 2),
                 "med%": round(np.median(x)*100, 2),
                 "win%": round((x > 0).mean()*100),
                 "p5%": round(np.percentile(x, 5)*100, 1),
                 "worst5%": round(np.sort(x)[:max(1, k//20)].mean()*100, 1),
                 "loss%": round((x < 0).mean()*100),
                 "yrs<1": len(neg)})
D = pd.DataFrame(rows)
assert D.n.sum() == n, f"cells sum to {D.n.sum()}, not {n}"

print(f"\n{'='*205}\n⭐⭐ §1 THE 16 CELLS — disjoint, exhaustive (n sums to "
      f"{D.n.sum():,}), sorted by trimmed PF\n{'='*205}")
print(D.sort_values("PF raw", ascending=False).to_string(index=False))
# ⚠ the legend MUST follow --side; every direction flips (§S43cb, §S43co)
if args.side == "long":
    print(f"  V+ = volat_open{W} LOW (<= {THR['V']:.1f}bp) · I+ = intensity HIGH "
          f"(>= {THR['I']:.2f}) · B+ = rel. persistence HIGH (>= {THR['B']:.2f}) · "
          f"G+ = gaps LOW (<= {THR['G']:.0f}s)")
else:
    print(f"  V+ = volat_open{W} HIGH (>= {THR['V']:.1f}bp) · I+ = intensity LOW "
          f"(<= {THR['I']:.2f}, QUIET close) · B+ = rel. persistence LOW "
          f"(<= {THR['B']:.2f}, GAPPY close) · G+ = gaps HIGH (>= {THR['G']:.0f}s, "
          f"THIN tape)")
print("  (+ is always the favourable side FOR THAT SYSTEM — every direction flips)")
print("  ⚠ READ n BEFORE PF. A thin cell's PF is a hint, not a measurement.")
print("  ⚠ sorted by RAW PF: on the short side trim lifts run 1.8-3.4, so the trimmed")
print("    column re-ranks cells on which losers get dropped rather than on edge.")

print(f"\n{'='*205}\n⭐ §2 THE SIZING LADDER — trimmed PF − 1, scaled to the BEST "
      f"cell = 1.00 (so every multiplier is a FRACTION of full size)\n{'='*205}")
S = D[D.n >= 60].copy()
S["edge"] = S["PF trim5"] - 1.0
# ⚠ scale to the BEST cell, not the weakest positive one: dividing by a near-zero
# weakest edge produced 41x multipliers, which is meaningless as a position size.
S["mult"] = (S.edge / S.edge.max()).round(3)
S.loc[S.edge <= 0, "mult"] = 0.0
# ⚠⚠ A cell with NEGATIVE RAW MEAN is not tradeable no matter what its TRIMMED PF says.
# Trimming exists to COMPARE cells whose tails are uncertain, not to decide whether an
# edge exists at all — dropping the worst 5% can flip a losing cell to PF > 1 and would
# otherwise hand it a positive size. Three cells here do exactly that.
S.loc[S["mean%"] <= 0, "mult"] = 0.0
S = S.sort_values("edge", ascending=False)
S["book"] = np.where(S.mult >= 0.70, "A++",
                     np.where(S.mult >= 0.30, "A+",
                              np.where(S.mult >= 0.10, "B++",
                                       np.where(S.mult > 0, "B+", "🛑 SKIP"))))
# ⚠ the raw/trim gap is itself a diagnostic: a cell whose PF triples under a 5% trim
# is carried by a handful of losers and its trimmed edge is the LESS reliable number.
S["trim lift"] = (S["PF trim5"] / S["PF raw"]).round(2)
print(S[["cell", "book", "n", "share%", "PF raw", "PF trim5", "trim lift", "edge",
         "mult", "mean%", "med%", "win%", "worst5%", "loss%",
         "yrs<1"]].to_string(index=False))
print(f"  cells with n < 60 omitted from the ladder "
      f"({int(D[D.n < 60].n.sum())} trades in {int((D.n < 60).sum())} cells)")
print("  ⚠ multipliers are on RAW returns — see the header for why vol-normalising "
      "would be circular here.")
print("  ⚠ 'trim lift' > ~2 means the cell's PF depends heavily on dropping its worst "
      "5%; treat its rank with suspicion.")

print(f"\n{'='*205}\n⭐ §3 NET PROFIT SHARE — does trading the lesser cells actually "
      f"pay?\n{'='*205}")
# ⭐ TWO books, because they answer different questions:
#   FLAT  = n x mean%   — the P&L if every cell were traded at the same size. This is
#           the honest "where does the profit live" measure, independent of any sizing
#           model, and it is what the A++-share claim should be judged on.
#   SIZED = n x mult x mean% — the P&L under the §2 ladder.
S = S.copy()
S["flat"] = S.n * S["mean%"]
S["sized"] = S.n * S.mult * S["mean%"]
tf, ts = S.flat.sum(), S.sized.sum()
run = []
af = asz = 0.0
for _, q in S.iterrows():
    af += q.flat
    asz += q.sized
    run.append({"cell": q.cell, "book": q.book, "n": int(q.n), "mean%": q["mean%"],
                "mult": q.mult,
                "FLAT P&L": round(q.flat),
                "flat %": f"{q.flat/tf*100:.1f}%",
                "flat cum %": f"{af/tf*100:.1f}%",
                "SIZED P&L": round(q.sized),
                "sized %": f"{q.sized/ts*100:.1f}%",
                "sized cum %": f"{asz/ts*100:.1f}%"})
print(pd.DataFrame(run).to_string(index=False))
print("  FLAT  = n x mean%  — profit if every cell were traded at EQUAL size "
      "(sizing-model-free).")
print("  SIZED = n x mult x mean% — profit under the §2 ladder.")
print("  ⚠ mean% on the SHORT side is inflated by the same fat right tail that makes "
      "its\n    worst trades ruinous; a cell can lead on FLAT P&L and still be "
      "untradeable.")

print(f"\n{'='*205}\n⭐ §4 MARGINAL VALUE OF EACH FEATURE across the lattice — "
      f"holding the other three fixed\n{'='*205}")
rows = []
for k in "VIBG":
    others = [o for o in "VIBG" if o != k]
    for combo in itertools.product([1, 0], repeat=3):
        base = np.ones(n, bool)
        lab = ""
        for bit, o in zip(combo, others):
            base &= good[o] if bit else ~good[o]
            lab += f"{o}{'+' if bit else '−'}"
        mp, mm = base & good[k], base & ~good[k]
        if mp.sum() < 40 or mm.sum() < 40:
            continue
        # ⚠ generic column names — per-feature keys ("V+ n", "I+ n", ...) make pandas
        # union them into one NaN-riddled frame.
        rows.append({"feature": k, "holding the other three": lab,
                     "n (feature +)": int(mp.sum()),
                     "PF (feature +)": round(pf(r[mp]), 3),
                     "n (feature −)": int(mm.sum()),
                     "PF (feature −)": round(pf(r[mm]), 3),
                     "ΔPF": round(pf(r[mp]) - pf(r[mm]), 3)})
M = pd.DataFrame(rows)
for k in "VIBG":
    sub = M[M.feature == k]
    if len(sub) == 0:
        continue
    print(f"\n  --- {k} ---")
    print(sub.drop(columns="feature").to_string(index=False))
    print(f"      ΔPF > 0 in {int((sub['ΔPF'] > 0).sum())}/{len(sub)} contexts, "
          f"median Δ {sub['ΔPF'].median():+.3f}")

print(f"\n{'='*205}\n⭐ §5 VOLATILITY IN TERCILES (user: \"might be worth breaking "
      f"volatility down into terciles instead of halves\")\n{'='*205}")
vq = np.nanquantile(A.volat.values, [0, 1/3, 2/3, 1.0])
tv = np.clip(np.searchsorted(vq, A.volat.values, side="right") - 1, 0, 2)
score = good["I"].astype(int) + good["B"].astype(int) + good["G"].astype(int)
rows = []
for t in range(3):
    for s in range(4):
        m = (tv == t) & (score == s)
        k = int(m.sum())
        if k < 40:
            continue
        x = r[m]
        rows.append({"volat tercile": f"T{t+1} [{vq[t]*1e4:.0f}, {vq[t+1]*1e4:.0f})bp",
                     "I+B+G score": s, "n": k, "PF raw": round(pf(x), 3),
                     "PF trim5": (round(tpf(x), 3) if tpf(x) == tpf(x) else np.nan),
                     "mean%": round(x.mean()*100, 2),
                     "med%": round(np.median(x)*100, 2),
                     "win%": round((x > 0).mean()*100),
                     "worst5%": round(np.sort(x)[:max(1, k//20)].mean()*100, 1),
                     "loss%": round((x < 0).mean()*100)})
print(pd.DataFrame(rows).to_string(index=False))
print("  T1 = quietest third. ⚠ §S43ch found the relation is an INVERTED U — the")
print("  bottom DECILE is below baseline — so watch whether T1 underperforms T2.")
