"""GH-21 — WHEN does the "still above the 10 ET high" short play out, and when does being above it turn neutral? (user
2026-11-23: "For gap downs, maybe the stop simply needs to be a bit further out in time ... This is actually a pretty strong short
setup now, but how long does it take for it to play out. Maybe if the stock is still above the 30m high by 11 ET or 11:30 ET the
expectancy becomes neutral and that is the point to stop out?")

The universe: GH-20's eligible 10:00 stock-days (z > −2, the 09:30 stepped stop not fired before 10:00). H30 = the 09:30–10:00
1s-vwap high (gaphv_break30). Prices: the half-hour marks' last price p<hhmm> and $5k fill x<hhmm> (gaphv_vwap30 / path).
  1. the STILL-ABOVE group (above H30 during 10:00–10:30 and at 10:30): the short from the 10:30 fill, its mean return to each
     mark (no stop) — how fast it plays out;
  2. every eligible trade, at each mark: above H30 at the mark (p > H30) or not → the forward short return from the mark's fill
     to the 15:30 fill. Where "above" turns neutral is where a stop above H30 should start.
No stops, no exit rules: forward returns only. No new tape pass.
    cd research && python3 scripts/equity/gaphv_still_above.py > data/equity/gaphv/still_above.md
"""
import numpy as np, pandas as pd, sys, io, contextlib
sys.path.insert(0, 'scripts/equity')
with contextlib.redirect_stdout(io.StringIO()):
    from gaphv_exits import X, pf, f2, CHK
X = (X.merge(pd.read_parquet('data/equity/gaphv/break30.parquet')[['ticker', 'd', 'h30', 'grp']], on=['ticker', 'd'])
      .merge(pd.read_parquet('data/equity/gaphv/entry10.parquet')[['ticker', 'd', 'z']], on=['ticker', 'd'], how='left')
      .merge(pd.read_parquet('data/equity/gaphv/volstop6.parquet')[['ticker', 'd', 'pinf_a700_b500_t']], on=['ticker', 'd'], how='left'))
MARKS = CHK + ['1530']
def lab(m): return f'{m[:2]}:{m[2:]}'
print('# GH-21 — when the "still above the 10 ET high" short plays out, and when being above H30 turns neutral\n')
print('Eligible 10:00 stock-days (z > −2, the 09:30 stepped stop not fired). Forward SHORT returns between the marks\' $5k fills, '
      'no stop, no exit rule. Σ = Σ %; halves = the mean in 2016–2020 / 2021–2026.\n')
for name, flag in (('D1000', 'd1000'), ('U1000', 'u1000')):
    A = X[X[flag] & ~(X.z < -2.0) & ~((X.pinf_a700_b500_t > 0) & (X.pinf_a700_b500_t < 36000))].copy()
    h1 = (A.yr <= 2020).values
    S = A[(A.grp == 'still above') & np.isfinite(A.x1030) & (A.x1030 > 0)]
    sh1 = (S.yr <= 2020).values
    print(f'## {name}\n')
    print(f'**1. The still-above group ({len(S):,}): the short from the 10:30 fill to each mark**\n')
    print('| to | mean | PF | win | halves |')
    print('|---|---|---|---|---|')
    for m in MARKS[1:]:
        r = 100 * (1 - S[f'x{m}'].values / S.x1030.values); ok = np.isfinite(r)
        print(f'| {lab(m)} | {np.nanmean(r):+.2f} % | {f2(pf(r[ok]))} | {(r[ok] > 0).mean():.0%} | {np.nanmean(r[sh1]):+.2f} / {np.nanmean(r[~sh1]):+.2f} % |')
    print()
    print('**2. Every eligible trade, at each mark: above H30 or not → the short from the mark to 15:30**\n')
    print('| mark | above H30: n · mean · PF · halves | not above: n · mean · PF |')
    print('|---|---|---|')
    for m in CHK:
        x0 = A[f'x{m}'].values; x1 = A.x1530.values; p = A[f'p_{m}'].values
        ok = np.isfinite(x0) & np.isfinite(x1) & (x0 > 0) & np.isfinite(p)
        r = 100 * (1 - x1 / x0)
        ab = ok & (p > A.h30.values); bl = ok & ~(p > A.h30.values)
        print(f'| {lab(m)} | {ab.sum():,} · {r[ab].mean():+.2f} % · {f2(pf(r[ab]))} · {r[ab & h1].mean():+.2f} / {r[ab & ~h1].mean():+.2f} % | '
              f'{bl.sum():,} · {r[bl].mean():+.2f} % · {f2(pf(r[bl]))} |')
    print()
