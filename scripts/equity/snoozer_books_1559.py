"""The Snoozer production books RE-BASELINED on 15:59 features (2026-09-10; user rulings: truncate at 15:59, gap ceiling 3,450).

Reads `snoozer_1559.parquet` (snoozer_build_1559.py — the ms corpus, every feature with both the 15:59 and the 16:00
last-hour endpoint) and builds the ratified cells (docs/production_specs.md §3/§4) three ways:
  (a) the 16:00 definitions on the ms corpus         — the ns-era registry numbers, re-measured (the precision drift)
  (b) the 15:59 definitions, ratified thresholds     — THE LIVE-CAUSAL BOOK the Scanner reproduces
  (c) (b) + the staleness ceiling gaps <= 3,450       — THE PRODUCTION BOOK
and writes the reference artifacts for the Scanner harness from (c):
  snoozer_reference_signals.parquet   one row per ticker-day-side that passed the SIGNAL + cell (the engine's trip set)
  snoozer_reference_book.parquet      the same rows with a fill (px_lim_1559_1600 present) + weight (the book)

Cells (weights): LONG chg60k59 < -6%: A++ 1.00 = volat_open30 [30,60) bp ∧ inten >= I ∧ pers >= B ·
A+ 1.00 = [60,120) ∧ inten >= I ∧ pers >= BB · B++ 0.35 = [60,120) ∧ inten >= I ∧ pers ∈ [B, BB);
SHORT chg60k59 > +8%: S 1.00 = volat < 40 ∧ gaps >= 1500 · A 0.50 = volat < 40 ∧ gaps ∈ [500,1500) ·
B 0.35 = volat [40,100) ∧ gaps >= 2000. I, B, BB = 0.498393, 0.652937, 0.925649 (population quantiles of the ns-era 16:00
features — kept as the ratified literals). Population guards as snoozer_production_books.py: a fill exists, the last hour
traded, dv_over_open15 defined. NO price floor (both sides' $1 rulings are pending; --floor applies one on p1559).

Run from research/:  python -u scripts/equity/snoozer_books_1559.py [--floor 0] [--ceiling 3450]
"""
import argparse, os
import numpy as np, pandas as pd, duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--feat", default="data/equity/flushfader/snoozer_1559.parquet")
ap.add_argument("--out-dir", default="data/flushfader_gate_review")
ap.add_argument("--floor", type=float, default=0.0, help="p1559 >= this (0 = none)")
ap.add_argument("--ceiling", type=int, default=3450, help="gaps <= this on every cell (the staleness rule)")
ap.add_argument("--tag", default="")
args = ap.parse_args()
sfx = f"_{args.tag}" if args.tag else ""
I, B, BB = 0.498393, 0.652937, 0.925649
con = duckdb.connect()
F = con.execute(f"SELECT * FROM read_parquet('{args.feat}')").df()
F["date"] = F["date"].astype(str)
# ⚠ NYSE early-close days are EXCLUDED (2026-09-10): the research caches read (15:00, 16:00] on every day, which on a 13:00
# close is AFTER-HOURS tape — no 15:59 limit exists there. The calendar-aware Scanner never sees those bars (its day ends at
# 13:00). One reference trade in 2025-26 (NCPL 2025-07-03) was this class.
EARLY = {"2016-11-25","2017-07-03","2017-11-24","2018-07-03","2018-11-23","2018-12-24","2019-07-03","2019-11-29","2019-12-24","2020-11-27","2020-12-24",
         "2021-11-26","2022-11-25","2023-07-03","2023-11-24","2024-07-03","2024-11-29","2024-12-24","2025-07-03","2025-11-28","2025-12-24","2026-11-27","2026-12-24"}
n_early = int(F.date.isin(EARLY).sum()); F = F[~F.date.isin(EARLY)].reset_index(drop=True); print(f"early-close ticker-days excluded: {n_early:,}")
vb = F.volat_open30.values * 1e4
def nz(m): return np.where(np.isnan(np.asarray(m, dtype=float)), False, m).astype(bool)
def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
def cells(inten, pers, guard_lh):
    base = nz(F.ovn_from_lim59.values) if False else ~np.isnan(F.ovn_from_lim59.values.astype(float))
    base &= guard_lh & ~np.isnan(F.dv_over_open15.values.astype(float))
    if args.floor > 0: base &= nz(F.p1559.values >= args.floor)
    lo = base & nz(F.chg60k59.values < -0.06); sh = base & nz(F.chg60k59.values > 0.08)
    g = F.gaps.values
    return {
        ("long", "A++", 1.00): lo & nz(vb >= 30) & nz(vb < 60) & nz(inten >= I) & nz(pers >= B),
        ("long", "A+", 1.00): lo & nz(vb >= 60) & nz(vb < 120) & nz(inten >= I) & nz(pers >= BB),
        ("long", "B++", 0.35): lo & nz(vb >= 60) & nz(vb < 120) & nz(inten >= I) & nz(pers >= B) & nz(pers < BB),
        ("short", "S", 1.00): sh & nz(vb < 40) & (g >= 1500),
        ("short", "A", 0.50): sh & nz(vb < 40) & (g >= 500) & (g < 1500),
        ("short", "B", 0.35): sh & nz(vb >= 40) & nz(vb < 100) & (g >= 2000)}
r0 = F.ovn_from_lim59.values.astype(float) * 100
rows = [f"# Snoozer books re-baselined on the ms corpus with 15:59 endpoints (`{args.feat}`; floor {args.floor}; ceiling gaps <= {args.ceiling})\n",
        "| cell | w | (a) 16:00 defs, ms corpus: n @ PF | (b) 15:59 defs: n @ PF | (c) 15:59 + ceiling: n @ PF | worst % (c) | registry (ns-era) |",
        "|---|---|---|---|---|---|---|"]
REG = {"A++": "476 @ 3.452", "A+": "211 @ 4.152", "B++": "304 @ 1.590", "S": "142 @ 6.884", "A": "108 @ 3.244", "B": "378 @ 2.775"}
ca = cells(F.inten60.values, F.pers60.values, nz(F.dv_lh.values > 0))
cb = cells(F.inten59.values, F.pers59.values, nz(F.dv_lh59.values > 0))
ceil = F.gaps.values <= args.ceiling
sig = []
for k in ca:
    side, name, w = k; sg = 1.0 if side == "long" else -1.0
    ma, mb, mc = ca[k], cb[k], cb[k] & ceil
    ra, rb, rc = sg * r0[ma], sg * r0[mb], sg * r0[mc]
    rows.append(f"| {side} {name} | {w} | {ma.sum()} @ {pf(ra):.3f} | {mb.sum()} @ {pf(rb):.3f} | {mc.sum()} @ {pf(rc):.3f} | {rc.min() if len(rc) else float('nan'):.1f} | {REG[name]} |")
    d = F[mc].copy(); d["side"] = side; d["cell"] = name; d["weight"] = w; d["ret"] = sg * d.ovn_from_lim59.values
    sig.append(d)
S = pd.concat(sig, ignore_index=True)
# the ENGINE-level set = signal + cell + ceiling, fill or not (the guards on the fill / last hour are book-side):
# rebuild it without the fill/last-hour guards for the trips artifact
def sigset():
    base = ~np.isnan(F.dv_over_open15.values.astype(float)) & ceil
    if args.floor > 0: base &= nz(F.p1559.values >= args.floor)
    lo = base & nz(F.chg60k59.values < -0.06); sh = base & nz(F.chg60k59.values > 0.08)
    g = F.gaps.values; inten, pers = F.inten59.values, F.pers59.values
    out = []
    for (side, name, w), m in {
        ("long", "A++", 1.00): lo & nz(vb >= 30) & nz(vb < 60) & nz(inten >= I) & nz(pers >= B),
        ("long", "A+", 1.00): lo & nz(vb >= 60) & nz(vb < 120) & nz(inten >= I) & nz(pers >= BB),
        ("long", "B++", 0.35): lo & nz(vb >= 60) & nz(vb < 120) & nz(inten >= I) & nz(pers >= B) & nz(pers < BB),
        ("short", "S", 1.00): sh & nz(vb < 40) & (g >= 1500),
        ("short", "A", 0.50): sh & nz(vb < 40) & (g >= 500) & (g < 1500),
        ("short", "B", 0.35): sh & nz(vb >= 40) & nz(vb < 100) & (g >= 2000)}.items():
        d = F[m].copy(); d["side"] = side; d["cell"] = name; d["weight"] = w; out.append(d)
    return pd.concat(out, ignore_index=True)
T = sigset()
cols = ["ticker", "date", "side", "cell", "weight", "chg60k59", "gaps", "nb60k59", "inten59", "pers59", "volat_open30", "nsl_open30",
        "p1500", "p1559", "dv_lh59", "dvO5", "dvO15", "dvO30", "nbO5", "nbO15", "nbO30", "px_lim_1559_1600", "nb_lastmin", "open_p1", "div_p1", "ovn_from_lim59"]
T = T[cols].rename(columns={"ticker": "symbol", "date": "trade_date"}); T["signal_sec"] = 57540
S = S[cols + ["ret"]].rename(columns={"ticker": "symbol", "date": "trade_date"}); S["signal_sec"] = 57540
w = S.weight.values; R = S.ret.values * 100
rows += ["", f"engine-level signals (signal + cell + ceiling, fill or not): {len(T):,}; book (with a fill and the last hour traded): {len(S):,}",
         f"book: LONG {int((S.side=='long').sum())} @ {pf(R[S.side.values=='long']):.3f} · SHORT {int((S.side=='short').sum())} @ {pf(R[S.side.values=='short']):.3f} · sized (Σ w·r / mean w) net {(R*w).sum()/w.mean():,.0f}%",
         "", "| year | long n @ PF | short n @ PF |", "|---|---|---|"]
yr = pd.to_datetime(S.trade_date).dt.year.values
for y in sorted(set(yr)):
    ml = (yr == y) & (S.side.values == "long"); ms = (yr == y) & (S.side.values == "short")
    rows.append(f"| {y} | {ml.sum()} @ {pf(R[ml]) if ml.sum() else float('nan'):.2f} | {ms.sum()} @ {pf(R[ms]) if ms.sum() else float('nan'):.2f} |")
os.makedirs(args.out_dir, exist_ok=True)
tp = os.path.join(args.out_dir, f"snoozer_reference_signals{sfx}.parquet"); bp = os.path.join(args.out_dir, f"snoozer_reference_book{sfx}.parquet")
T.to_parquet(tp, index=False); S.to_parquet(bp, index=False)
txt = "\n".join(rows) + "\n"; open(os.path.join(args.out_dir, f"snoozer_reference{sfx}.md"), "w").write(txt); print(txt)
print(f"wrote {tp} ({len(T):,}), {bp} ({len(S):,})")
