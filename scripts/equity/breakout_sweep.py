#!/usr/bin/env python3
"""Tabulate raw + clip(+50%) PF / win / net (overall + per-year) across a set of BreakoutTimer sweep CSVs.

Each input is a CSV produced by an in-engine run at one parameter value (drought threshold or timer length).
Pass them as  label=path  pairs so the table is labelled by the swept value.

Usage: breakout_sweep.py drought8=/tmp/d8.csv drought12=/tmp/d12.csv ...

Always prints BOTH raw and clip PF (per user standing instruction).
"""
import csv, sys

def pf(rets_nets):
    g = sum(n for r, n in rets_nets if n > 0)
    l = sum(-n for r, n in rets_nets if n < 0)
    return g / l if l > 0 else float('inf')

def clipnet(r):
    return float(r['qty']) * float(r['entry_price']) * min(float(r['ret_moc']), 0.50)

def summ(rows):
    n = len(rows)
    if n == 0: return None
    w = sum(1 for r in rows if float(r['ret_moc']) > 0)
    pfr = pf([(float(r['ret_moc']), float(r['net_pnl'])) for r in rows])
    pfc = pf([(float(r['ret_moc']), clipnet(r)) for r in rows])
    nr = sum(float(r['net_pnl']) for r in rows); nc = sum(clipnet(r) for r in rows)
    return n, 100*w/n, pfr, pfc, nr, nc

def main():
    items = []
    for a in sys.argv[1:]:
        lbl, path = a.split('=', 1)
        items.append((lbl, list(csv.DictReader(open(path)))))

    # overall table
    print(f"\n{'param':12s} {'n':>5s} {'win%':>6s} {'PFraw':>7s} {'PFclip':>7s} {'net_raw':>11s} {'net_clip':>11s}")
    for lbl, rows in items:
        s = summ(rows)
        if s: print(f"{lbl:12s} {s[0]:>5d} {s[1]:>5.1f}% {s[2]:>7.2f} {s[3]:>7.2f} {s[4]:>11,.0f} {s[5]:>11,.0f}")

    # per-year: raw then clip
    years = sorted({r['trade_date'][:4] for _, rows in items for r in rows})
    for tag, idx in [("RAW PF", 2), ("clip PF", 3)]:
        print(f"\n  --- per-year {tag} ---")
        print("  " + f"{'param':12s}" + "".join(f"{y:>8s}" for y in years))
        for lbl, rows in items:
            line = "  " + f"{lbl:12s}"
            for y in years:
                yr = [r for r in rows if r['trade_date'][:4] == y]
                s = summ(yr)
                line += (f"{s[idx]:>8.2f}" if s else f"{'-':>8s}")
            print(line)

if __name__ == '__main__':
    main()
