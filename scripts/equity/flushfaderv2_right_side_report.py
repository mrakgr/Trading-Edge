"""V2-27 report (exec'd by flushfaderv2_right_side.py --report)."""
import numpy as np, pandas as pd
SP = '/tmp/claude-1000/-home-mrakgr-Trading-Edge/48da2bbb-3acf-462f-b6d5-74fb8b86dee0/scratchpad'
g60 = pd.read_parquet('data/flushfaderv2/flushfaderv2_reference_book.parquet', columns=['symbol', 'trade_date', 'signal_sec', 'gap_60'])
g60['td'] = g60.trade_date.astype(str).str[:10]
def prep(s):
    R = pd.read_parquet(s); R['d'] = pd.to_datetime(R.td); R['u'] = R.pf1_fitted.clip(.125, 2)
    R['traded'] = R.brk_sec >= 0
    R['r0'] = R.ret_exit + 2 * 0.001 / R.entry_px                                   # the reversal entry (V2-26: maker both sides)
    cr = R.exit_px / R.brk_px - 1
    R['r1'] = np.where(R.traded, cr + 2 * 0.001 / R.brk_px, 0.0)                    # confirmation entry, still maker (optimistic)
    R['r2'] = np.where(R.traded, cr - 0.003 / R.brk_px + 0.001 / R.brk_px, 0.0)     # confirmation entry as a TAKER (0.3c), exit maker
    for c in ('r0', 'r1', 'r2'): R['p' + c[1]] = 15 * R.u * R[c]
    R['delay'] = np.where(R.traded, R.brk_sec - R.entry_sec, np.nan); R['dpx'] = np.where(R.traded, R.brk_px / R.entry_px - 1, np.nan)
    return R
H = prep(args.out)
W = prep(args.window) if getattr(args, 'window', None) else None
H = H.merge(g60[['symbol', 'td', 'signal_sec', 'gap_60']], on=['symbol', 'td', 'signal_sec'], how='left')
def pf(r): l = -r[r < 0].sum(); return np.inf if l == 0 else r[r > 0].sum() / l
def line(lab, R, rc, pc):
    t = R[R.traded] if rc != 'r0' else R
    w = R.set_index('d')[pc].resample('W-FRI').sum(); w4 = w.rolling(4).sum()
    return (f"| {lab} | {len(t):,} | {100*t[rc].mean():+.2f} % | {pf(t[rc].values):.3f} | {100*t[rc].min():+.1f} % | {1000*(t[rc] <= -.2).mean():.1f} | "
            f"{R[pc].sum():+.1f} | {w.median():+.2f} | {w4.min():+.2f} |")
HDR = "| entry | trades | mean | PF | worst | ≤ −20 % per 1,000 | % of equity | weekly median | worst 4 weeks |\n|---|---|---|---|---|---|---|---|---|"
SETS = [('2020-01 … 2026-08', H)] + ([] if W is None else [('the window', W), ('from 2026-10-12', W[W.d >= '2026-10-12'])])
for lab, R in SETS:
    print(f"\n**{lab}**\n\n{HDR}")
    for n, rc, pc in (('reversal (production)', 'r0', 'p0'), ('1m-high break, maker', 'r1', 'p1'), ('1m-high break, taker', 'r2', 'p2')):
        print(line(n, R, rc, pc))
t = H[H.traded]
print(f"\nhistory: skipped (no 1m high before the exit) {(~H.traded).sum()} trades (their reversal PF {pf(H[~H.traded].r0.values):.2f}, {H[~H.traded].p0.sum():+.1f} % eq); "
      f"the confirmation came a median {np.nanmedian(H.delay):.0f} s later (p90 {np.nanpercentile(H.delay, 90):.0f} s) at a median {100*np.nanmedian(H.dpx):+.2f} % vs the reversal fill "
      f"(p10 {100*np.nanpercentile(H.dpx, 10):+.2f} %, p90 {100*np.nanpercentile(H.dpx, 90):+.2f} %); entered LOWER on {(H.dpx < 0).mean():.0%}")
print("\n| year | reversal PF · % eq | 1m break (maker) PF · % eq | (taker) PF · % eq |\n|---|---|---|---|")
H['yr'] = H.d.dt.year
for y, g in H.groupby('yr'):
    tt = g[g.traded]
    print(f"| {y} | {pf(g.r0.values):.3f} · {g.p0.sum():+.1f} | {pf(tt.r1.values):.3f} · {g.p1.sum():+.1f} | {pf(tt.r2.values):.3f} · {g.p2.sum():+.1f} |")
print("\n| gap_60 | reversal PF · mean · % eq | 1m break (maker) PF · mean · % eq |\n|---|---|---|")
for lab, m in (('≤ 4 (liquid)', H.gap_60 <= 4), ('> 4', H.gap_60 > 4)):
    g = H[m]; tt = g[g.traded]
    print(f"| {lab} | {pf(g.r0.values):.3f} · {100*g.r0.mean():+.2f} % · {g.p0.sum():+.1f} | {pf(tt.r1.values):.3f} · {100*tt.r1.mean():+.2f} % · {g.p1.sum():+.1f} |")
print("\nhistory: the 30 worst reversal trades, what the confirmation entry made of them:")
x = H.nsmallest(30, 'r0')
print(f"  reversal Σ {100*x.r0.sum():+.0f} % (mean {100*x.r0.mean():+.1f}) -> confirmation Σ {100*x.r1.sum():+.0f} % (mean {100*x.r1.mean():+.1f}); skipped {(~x.traded).sum()}")
if W is not None:
  print("\nthe window's tail losers:")
  L = W.nsmallest(8, 'r0')[['symbol', 'td', 'entry_px', 'brk_px', 'exit_px', 'r0', 'r1', 'delay']].copy()
  print(L.round(3).to_string(index=False))
