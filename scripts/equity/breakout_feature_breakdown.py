#!/usr/bin/env python3
"""Clipped(+50%) bucket breakdown of a BreakoutTimer study CSV by any numeric column.

For a chosen feature column, splits trades into quantile (or fixed-edge) buckets and reports
raw + clip PF / win% / net per bucket. Optionally adds a per-year sub-table for one bucket range
to check for 2021 inversion (the 2021-law test).

Usage:
  breakout_feature_breakdown.py <csv> <col> [nbuckets]           # quantile buckets
  breakout_feature_breakdown.py <csv> <col> --edges e1,e2,e3     # fixed edges
  breakout_feature_breakdown.py <csv> <col> --edges ... --year   # + per-year per-bucket

Clip = min(ret_moc, 0.50). Skips rows where the feature is nan/blank.
"""
import csv, sys
import math

def pf(rets_nets):
    g = sum(n for r, n in rets_nets if n > 0)
    l = sum(-n for r, n in rets_nets if n < 0)
    return g / l if l > 0 else float('inf')

def clipnet(r):
    return float(r['qty']) * float(r['entry_price']) * min(float(r['ret_moc']), 0.50)

def isnum(x):
    try:
        v = float(x); return not math.isnan(v)
    except Exception:
        return False

def stat(rows):
    n = len(rows)
    if n == 0: return "n=0"
    w = sum(1 for r in rows if float(r['ret_moc']) > 0)
    pfr = pf([(float(r['ret_moc']), float(r['net_pnl'])) for r in rows])
    pfc = pf([(float(r['ret_moc']), clipnet(r)) for r in rows])
    nr = sum(float(r['net_pnl']) for r in rows); nc = sum(clipnet(r) for r in rows)
    return f"n={n:4d}  win={100*w/n:4.1f}%  PFraw={pfr:5.2f}  PFclip={pfc:5.2f}  net_raw=${nr:>9,.0f}  net_clip=${nc:>9,.0f}"

def main():
    path, col = sys.argv[1], sys.argv[2]
    rows = [r for r in csv.DictReader(open(path)) if isnum(r.get(col, ''))]
    tot = list(csv.DictReader(open(path)))
    print(f"\n=== {path}  |  feature = {col}  ({len(rows)}/{len(tot)} rows have a numeric {col}) ===")

    edges = None; per_year = False; nb = 5
    args = sys.argv[3:]
    if '--edges' in args:
        edges = [float(x) for x in args[args.index('--edges')+1].split(',')]
    if '--year' in args:
        per_year = True
    for a in args:
        if a.isdigit(): nb = int(a)

    vals = sorted(float(r[col]) for r in rows)
    if edges is None:
        # quantile edges
        qs = [vals[int(len(vals)*i/nb)] for i in range(1, nb)]
        edges = qs
    edges = sorted(set(edges))
    labels = []
    lo = float('-inf')
    bounds = [lo] + edges + [float('inf')]
    for i in range(len(bounds)-1):
        a, b = bounds[i], bounds[i+1]
        al = '-inf' if a == float('-inf') else f"{a:.4g}"
        bl = '+inf' if b == float('inf') else f"{b:.4g}"
        labels.append((a, b, f"[{al}, {bl})"))

    print(f"{'bucket':22s} {'stats'}")
    buckets = []
    for a, b, lbl in labels:
        bk = [r for r in rows if a <= float(r[col]) < b]
        buckets.append((lbl, bk))
        print(f"{lbl:22s} {stat(bk)}")

    if per_year:
        years = sorted({r['trade_date'][:4] for r in rows})
        for tag, netfn in [("clip PF", lambda r: clipnet(r)), ("RAW PF", lambda r: float(r['net_pnl']))]:
            print(f"\n  --- per-year {tag} per bucket (2021-law check) ---")
            hdr = "  " + f"{'bucket':22s}" + "".join(f"{y:>8s}" for y in years)
            print(hdr)
            for lbl, bk in buckets:
                line = "  " + f"{lbl:22s}"
                for y in years:
                    yb = [r for r in bk if r['trade_date'][:4] == y]
                    if yb:
                        p = pf([(float(r['ret_moc']), netfn(r)) for r in yb])
                        line += f"{p:>8.2f}"
                    else:
                        line += f"{'-':>8s}"
                print(line)

if __name__ == '__main__':
    main()
