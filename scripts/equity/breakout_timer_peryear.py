#!/usr/bin/env python3
"""Per-year raw + clipped(+50%) PF / win / net for a BreakoutTimer trips CSV.

Clip = min(ret_moc, 0.50) applied to each trade's return, re-derived net = qty * entry_price * clipped_ret.
Mirrors the DipRiderV3 convention (clip the per-trade PF estimate to kill lottery exposure).

Usage: breakout_timer_peryear.py <trips.csv> [<trips2.csv> ...]
"""
import csv, sys

def pf(rets_nets):
    g = sum(n for _, n in rets_nets if n > 0)
    l = sum(-n for _, n in rets_nets if n < 0)
    return g / l if l > 0 else float('inf')

def analyze(path):
    rows = list(csv.DictReader(open(path)))
    # per trade: raw net, and clipped net = qty * entry_px * min(ret_moc, 0.5)
    by_year = {}
    allr, allc = [], []
    for r in rows:
        yr = r['trade_date'][:4]
        ret = float(r['ret_moc']); net = float(r['net_pnl'])
        qty = float(r['qty']); ep = float(r['entry_price'])
        cnet = qty * ep * min(ret, 0.50)
        by_year.setdefault(yr, []).append((ret, net, cnet))
        allr.append((ret, net)); allc.append((ret, cnet))

    print(f"\n=== {path} ===")
    print(f"{'year':6s} {'n':>4s} {'win%':>6s} {'PFraw':>7s} {'PFclip':>7s} {'net_raw':>11s} {'net_clip':>11s}")
    for yr in sorted(by_year):
        ts = by_year[yr]
        n = len(ts); w = sum(1 for ret, _, _ in ts if ret > 0)
        pfr = pf([(ret, net) for ret, net, _ in ts])
        pfc = pf([(ret, cnet) for ret, _, cnet in ts])
        nr = sum(net for _, net, _ in ts); nc = sum(cnet for _, _, cnet in ts)
        print(f"{yr:6s} {n:>4d} {100*w/n:>5.1f}% {pfr:>7.2f} {pfc:>7.2f} {nr:>11,.0f} {nc:>11,.0f}")
    n = len(allr); w = sum(1 for ret, _ in allr if ret > 0)
    pfr = pf(allr)
    pfc = pf([(ret, cnet) for ret, cnet in allc])
    nr = sum(net for _, net in allr); nc = sum(cnet for _, cnet in allc)
    print(f"{'ALL':6s} {n:>4d} {100*w/n:>5.1f}% {pfr:>7.2f} {pfc:>7.2f} {nr:>11,.0f} {nc:>11,.0f}")

if __name__ == '__main__':
    for p in sys.argv[1:]:
        analyze(p)
