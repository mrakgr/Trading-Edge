"""V2-54 — the RETAIL-SHARE gate on the production book (2026-11-19, user decision 2026-11-17; docs/buildup_results.md §6/§7).

retail_share_1200 = BJZZ retail volume / all counted volume over the last 1,200 present 1 s bars, the signal bar included
(research 8f6b98e; the engine records it on every trip from the share-column corpus). The production reference book
(data/flushfaderv2/flushfaderv2_reference_book.parquet, V2-49, 35,519 trips, base_v24) is joined to base_v25 by
(symbol, trade_date, signal_sec) — base_v25 is base_v24's run on the rebuilt corpus, so ret_exit must match exactly.

Questions:
  1. the quintile table on THIS book (raw and sized PF, by era) — does the buildup finding hold on production?
  2. the threshold: a FIXED cut vs a TRAILING-RELATIVE cut (the p-th percentile of the book's rshare over the prior
     N calendar days, strictly before D — causal), since the level drifts (2026 q20 ≈ 0.10 vs 0.15 overall)
  3. controls: per year, time-of-day and price buckets, random removal of the same number of trips (p-value)

  cd research && python -u scripts/equity/flushfaderv2_retail_gate.py > logs/retail_gate.log 2>&1; echo exit $?
"""
import argparse, time
import numpy as np, pandas as pd, duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--book", default="data/flushfaderv2/pre_rshare_2026-11-19/flushfaderv2_reference_book.parquet", help="the V2-49 (ungated) production book")
ap.add_argument("--base", nargs="+", default=["data/equity/flushfader/base_v25/*.parquet"])
ap.add_argument("--until", default="9999", help="keep the book's trips with trade_date <= this (a partial base)")
ap.add_argument("--credit", type=float, default=0.001)
ap.add_argument("--out", default="data/flushfaderv2/retail_gate_book.parquet")
args = ap.parse_args()
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:6.1f}s]", *a, flush=True)

con = duckdb.connect(); con.execute("SET memory_limit='4GB'; SET threads=6")
B = con.execute(f"""
    SELECT b.*, v.retail_share_1200 AS rshare, v.mid_share_1200 AS mshare, v.ret_exit AS ret_v25
    FROM read_parquet('{args.book}') b
    LEFT JOIN (SELECT DISTINCT ON (symbol, trade_date, signal_sec) symbol, trade_date::VARCHAR AS trade_date, signal_sec,
                      retail_share_1200, mid_share_1200, ret_exit
               FROM read_parquet({args.base!r})) v
      USING (symbol, trade_date, signal_sec)
    WHERE b.trade_date <= '{args.until}'
    ORDER BY trade_date, signal_sec, symbol""").df()
log(f"book {len(B):,}; rshare missing {B.rshare.isna().sum()}; NaN {np.isnan(B.rshare.astype(float)).sum()}; "
    f"ret_exit != v25 {(B.ret_exit != B.ret_v25).sum()}")
assert B.rshare.notna().all() and (B.ret_exit == B.ret_v25).all()
B["year"] = B.trade_date.str[:4]
B["r"] = B.ret_exit + 2 * args.credit / B.entry_px      # the reference convention: \$0.001/share maker credit per side
B["sr"] = B.r * B.multiplier

def pf(x):
    x = np.asarray(x, float); w = x[x > 0].sum(); l = -x[x < 0].sum()
    return np.inf if l == 0 else w / l
def row(d): return dict(n=len(d), pf=pf(d.r), spf=pf(d.sr), eq=15 * d.sr.sum(), mean_bp=1e4 * d.r.mean())
def tab(rows, cols):
    print("| " + " | ".join(cols) + " |"); print("|" + "---|" * len(cols))
    for r in rows: print("| " + " | ".join(r) + " |")
f3 = lambda x: f"{x:.3f}"

print(f"\n## 1. Quintiles of retail_share_1200 on the production book ({len(B):,} trips)\n")
edges = np.quantile(B.rshare, [0.2, 0.4, 0.6, 0.8])
print("edges", np.round(edges, 4))
B["q"] = np.searchsorted(edges, B.rshare, side="right") + 1
rows = []
for q in range(1, 6):
    d = B[B.q == q]; a = row(d); e1 = d[d.year <= "2023"]; e2 = d[d.year >= "2024"]
    rows.append([str(q), f"[{d.rshare.min():.3f}, {d.rshare.max():.3f}]", f"{a['n']:,}", f3(a['pf']), f3(a['spf']),
                 f3(pf(e1.r)), f3(pf(e2.r)), f"{a['eq']:+,.0f}", f3(d.multiplier.mean())])
tab(rows, ["q", "range", "n", "PF", "sized PF", "PF 2020–23", "PF 2024–26", "% eq (15 %/unit)", "mean mult"])

print("\n## 1b. The yearly level of the share (book)\n")
rows = []
for y, d in B.groupby("year"):
    rows.append([y, f"{len(d):,}"] + [f3(v) for v in np.quantile(d.rshare, [0.1, 0.2, 0.4, 0.5, 0.8])])
tab(rows, ["year", "n", "p10", "p20", "p40", "p50", "p80"])

# ---- the causal trailing percentile: for day D, the p-th percentile of the book's rshare on days in [D - N, D)
days = np.array(sorted(B.trade_date.unique()))
dd = pd.to_datetime(days)
def trailing_cut(p, ndays):
    out = {}
    dates = pd.to_datetime(B.trade_date.values); rs = B.rshare.values
    order = np.argsort(dates, kind="stable"); dates_s = dates[order]; rs_s = rs[order]
    for D in dd:
        lo = np.searchsorted(dates_s, D - pd.Timedelta(days=ndays), side="left")
        hi = np.searchsorted(dates_s, D, side="left")
        out[D.strftime("%Y-%m-%d")] = np.quantile(rs_s[lo:hi], p) if hi - lo >= 200 else np.nan
    return B.trade_date.map(out).values

base = row(B)
print(f"\n## 2. Gate variants (drop rshare < cut)\n\nbase: n {base['n']:,}, PF {base['pf']:.3f}, sized {base['spf']:.3f}, % eq {base['eq']:+,.0f}\n")
variants = [("fixed 0.10", np.full(len(B), 0.10)), ("fixed 0.12", np.full(len(B), 0.12)), ("fixed 0.153", np.full(len(B), 0.153)),
            (f"fixed book q20 {edges[0]:.3f}", np.full(len(B), edges[0]))]
for p in (0.15, 0.2, 0.25):
    for nd in (90, 182, 365):
        variants.append((f"trailing p{int(p*100)} {nd}d", trailing_cut(p, nd)))
yrs = sorted(B.year.unique())
rows = []
for name, cut in variants:
    have = ~np.isnan(cut)
    drop = have & (B.rshare.values < cut)
    K = B[~drop]; a = row(K); Dp = B[drop]
    yr = [pf(K[K.year == y].r) - pf(B[B.year == y].r) for y in yrs]
    rows.append([name, f"{drop.mean()*100:.1f} %", f"{a['n']:,}", f3(a['pf']), f3(a['spf']), f"{a['eq']-base['eq']:+,.0f}",
                 f3(pf(Dp.r)), f"{sum(v > 0 for v in yr)}/{len(yr)}", f"{(drop & (B.year.values == '2026')).sum() / (B.year.values == '2026').sum() * 100:.0f} %"])
tab(rows, ["variant", "dropped", "kept", "PF", "sized PF", "Δ % eq", "dropped PF", "years better", "2026 dropped"])


print("\n## 3. Controls for the two candidate cuts\n")
rng = np.random.default_rng(7)
B["tod"] = pd.cut(B.signal_sec, [0, 37800, 41400, 48600, 99999], labels=["09:45–10:30", "10:30–11:30", "11:30–13:30", "13:30–15:00"])
B["pxb"] = pd.cut(B.entry_px, [0, 2, 5, 10, 1e9], labels=["$1–2", "$2–5", "$5–10", "$10+"])
for name, cut in [("fixed 0.153", np.full(len(B), 0.153)), ("trailing p20 365d", trailing_cut(0.2, 365))]:
    drop = ~np.isnan(cut) & (B.rshare.values < cut)
    K = B[~drop]; Dp = B[drop]
    print(f"### {name}: drops {drop.sum():,}; dropped PF {pf(Dp.r):.3f}, sized {pf(Dp.sr):.3f}, mean {1e4*Dp.r.mean():.1f} bp (kept {1e4*K.r.mean():.1f} bp)\n")
    rows = []
    for col in ("year", "tod", "pxb"):
        for k, d in B.groupby(col, observed=True):
            dk = d[~drop[d.index]] if False else K[K[col] == k]; dd_ = Dp[Dp[col] == k]
            rows.append([col, str(k), f"{len(d):,}", f3(pf(d.r)), f3(pf(dk.r)), f"{len(dd_):,}", f3(pf(dd_.r)), f3(pf(dk.sr) - pf(d.sr))])
    tab(rows, ["by", "bucket", "n", "PF all", "PF kept", "n dropped", "PF dropped", "Δ sized PF"])
    obs = pf(K.r); sims = []
    r = B.r.values
    for _ in range(1000):
        m = np.ones(len(B), bool); m[rng.choice(len(B), drop.sum(), replace=False)] = False
        sims.append(pf(r[m]))
    sims = np.array(sims)
    print(f"\nrandom removal of {drop.sum():,} trips ×1000: kept PF p50 {np.median(sims):.3f}, p99 {np.quantile(sims, .99):.3f}, max {sims.max():.3f}; observed {obs:.3f}\n")

print("\n## 4. After the gate (fixed 0.153): the kept book in quintiles of rshare\n")
K = B[B.rshare >= 0.153].copy()
e = np.quantile(K.rshare, [0.2, 0.4, 0.6, 0.8]); K["q"] = np.searchsorted(e, K.rshare, side="right") + 1
rows = []
for q, d in K.groupby("q"):
    rows.append([str(q), f"[{d.rshare.min():.3f}, {d.rshare.max():.3f}]", f"{len(d):,}", f3(pf(d.r)), f3(pf(d.sr)), f3(d.multiplier.mean()), f3(d.pf1_fitted.mean())])
tab(rows, ["q", "range", "n", "PF", "sized PF", "mean mult", "mean pf1 predicted"])

B.to_parquet(args.out, index=False)
log(f"wrote {args.out}")
