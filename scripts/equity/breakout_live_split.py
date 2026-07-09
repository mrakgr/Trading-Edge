#!/usr/bin/env python3
"""Split an UNGATED BreakoutTimer V3 book by whether a breakout timer was LIVE at entry, per year.

Tests the "sizing lever" thesis: keep every V3 entry, but do timer-live entries outperform timer-dead
ones consistently across ALL years (not just 2024)? Shows raw + clip(+50%) PF both (standing instruction).

Usage: breakout_live_split.py <ungated_book.csv>
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

def line(lbl, rows):
    s = summ(rows)
    if s: return f"{lbl:18s} n={s[0]:>4d}  win={s[1]:>4.1f}%  PFraw={s[2]:>6.2f}  PFclip={s[3]:>6.2f}  net_raw=${s[4]:>9,.0f}  net_clip=${s[5]:>9,.0f}"
    return f"{lbl:18s} n=0"

def main():
    rows = list(csv.DictReader(open(sys.argv[1])))
    live = [r for r in rows if int(r['breakout_timer']) > 0]
    dead = [r for r in rows if int(r['breakout_timer']) <= 0]
    print(f"\n=== {sys.argv[1]} — timer LIVE vs DEAD at entry (ungated V3 book) ===\n")
    print("OVERALL:")
    print("  " + line("timer LIVE (>0)", live))
    print("  " + line("timer DEAD (<=0)", dead))
    print("  " + line("ALL", rows))

    years = sorted({r['trade_date'][:4] for r in rows})
    print("\nPER YEAR:")
    print(f"  {'year':6s} | {'LIVE n':>6s} {'LIVE PFraw':>10s} {'LIVE PFclip':>11s} | {'DEAD n':>6s} {'DEAD PFraw':>10s} {'DEAD PFclip':>11s} | {'lift(clip)':>10s}")
    for y in years:
        L = [r for r in live if r['trade_date'][:4] == y]
        D = [r for r in dead if r['trade_date'][:4] == y]
        sL, sD = summ(L), summ(D)
        ln = sL[3] if sL else float('nan'); dn = sD[3] if sD else float('nan')
        lift = (ln - dn) if (sL and sD) else float('nan')
        def f(s, i): return f"{s[i]:.2f}" if s else "-"
        print(f"  {y:6s} | {(sL[0] if sL else 0):>6d} {f(sL,2):>10s} {f(sL,3):>11s} | {(sD[0] if sD else 0):>6d} {f(sD,2):>10s} {f(sD,3):>11s} | {lift:>10.2f}")

if __name__ == '__main__':
    main()
