"""V2-28 — the QUIET entry (user 2026-11-07: "What if we instead wait for 60s without a new 20m low?"). Every V2-25
production book trade, the same signal and the same size, entered instead once Q seconds pass with NO NEW 20m LOW after
the signal (a new low = a bar vwap strictly under the prior 1,200-present-bar min: the engine's entry channel, which every
signal bar is). Each new low restarts the clock; the decision bar is the first present bar >= Q s after the last new low
(itself not a new low), filled at the FOLLOWING present bar's vwap. The exit is SIMULATED from the bars (a quiet entry can
come after the reversal's own exit, a fast V): the first bar after the fill whose vwap strictly exceeds the prior
300-present-bar max, filled at the following bar's vwap; none by 16:00 = the reversal's own next-open exit when the
reversal also held overnight, else the last bar before 16:00 (counted, flagged). A quiet fill at/after 16:00 = no trade.
Controls: (1) the signal bar is a new 20m low under this definition; (2) the exit simulated from the REVERSAL's fill
reproduces the book's exit_sec / exit_px.
Run from research/:  python3 -u scripts/equity/flushfaderv2_quiet_entry.py [--book B] [--out O]   then  --report [--window W]
"""
import argparse, sys, time, duckdb, numpy as np, pandas as pd
ap = argparse.ArgumentParser()
ap.add_argument('--book', default='data/flushfaderv2/flushfaderv2_reference_book.parquet')
ap.add_argument('--bars', default='data/intraday_1s_slim')
ap.add_argument('--out', default='data/flushfaderv2/quiet_entry.parquet')
ap.add_argument('--quiet', default='30,60,120,300')
ap.add_argument('--report', action='store_true')
ap.add_argument('--window', default=None)
ap.add_argument('--rth-only', action='store_true', help='channels over bars from 09:30 only (default: every bar of the file)')
args = ap.parse_args()
QS = [int(x) for x in args.quiet.split(',')]
CLOSE = 57600

def run():
    con = duckdb.connect(); con.execute("SET memory_limit='3GB'"); con.execute("SET threads=4")
    B = con.execute(f"SELECT symbol, trade_date::VARCHAR AS td, signal_sec, entry_sec, exit_sec, entry_px, exit_px, ret_exit, pf1_fitted "
                    f"FROM '{args.book}' ORDER BY td, symbol, signal_sec").df()
    B['td'] = B.td.str[:10]
    print(f"{len(B):,} book trades, {B.td.nunique():,} days", flush=True)
    out = []; T0 = time.time(); lo_sec = 34200 if args.rth_only else 0
    for k, (day, g) in enumerate(B.groupby('td', sort=True)):
        tick = list(g.symbol.unique())
        bars = con.execute(f"SELECT ticker, bucket, vwap FROM '{args.bars}/{day}.parquet' WHERE ticker IN ({','.join('?' * len(tick))}) "
                           f"AND volume > 0 AND bucket >= {lo_sec} ORDER BY ticker, bucket", tick).df()
        for sym, gt in g.groupby('symbol'):
            bt = bars[bars.ticker == sym]; sec = bt.bucket.values.astype(np.int64); vw = bt.vwap.values.astype(np.float64)
            s = pd.Series(vw)
            pmin = s.rolling(1200, min_periods=1).min().shift(1).values      # the prior 1,200 present bars' min
            pmax = s.rolling(300, min_periods=1).max().shift(1).values       # the prior 300's max
            newlow = vw < pmin; brk = vw > pmax
            def exit_from(f):                                                # f = the fill bar index
                j = np.flatnonzero(brk[f + 1:]) + f + 1
                j = j[(j + 1 < len(sec))]
                if len(j) and sec[j[0] + 1] <= CLOSE: return int(sec[j[0] + 1]), float(vw[j[0] + 1]), False
                return -1, np.nan, True                                       # no exit break by 16:00
            for r in gt.itertuples():
                si = np.searchsorted(sec, r.signal_sec); o = dict(symbol=sym, td=day, signal_sec=r.signal_sec)
                o['sig_is_low'] = bool(si < len(sec) and sec[si] == r.signal_sec and newlow[si])
                f = si + 1
                if f < len(sec):
                    es, ep, on = exit_from(f); o['ctl_exit_sec'], o['ctl_exit_px'], o['ctl_overnight'] = es, ep, on
                lows = np.flatnonzero(newlow[si:]) + si                       # the signal bar and every later new low
                for q in QS:
                    k2 = -1
                    for a_i, a in enumerate(lows):
                        b = lows[a_i + 1] if a_i + 1 < len(lows) else len(sec)
                        c = np.searchsorted(sec, sec[a] + q)                  # the first bar >= q s after this low
                        if c < b: k2 = c; break
                    f2 = k2 + 1 if k2 >= 0 else -1
                    if f2 < 0 or f2 >= len(sec) or sec[f2] >= CLOSE:
                        o[f'q{q}_px'] = np.nan; o[f'q{q}_sec'] = -1; o[f'q{q}_xpx'] = np.nan; o[f'q{q}_flag'] = 'none'; continue
                    o[f'q{q}_px'], o[f'q{q}_sec'] = float(vw[f2]), int(sec[f2])
                    es, ep, on = exit_from(f2)
                    if not on: o[f'q{q}_xpx'], o[f'q{q}_flag'] = ep, 'target'
                    elif r.exit_sec >= CLOSE: o[f'q{q}_xpx'], o[f'q{q}_flag'] = r.exit_px, 'overnight'
                    else:
                        lb = np.searchsorted(sec, CLOSE) - 1; o[f'q{q}_xpx'], o[f'q{q}_flag'] = float(vw[lb]), 'close'
                out.append(o)
        if k % 200 == 0: print(f"  {day}  {k + 1}/{B.td.nunique()}  {time.time() - T0:.0f}s", flush=True)
    R = B.merge(pd.DataFrame(out), on=['symbol', 'td', 'signal_sec'], how='left'); R.to_parquet(args.out, index=False)
    t = R[R.exit_sec < CLOSE]
    print(f"CONTROL 1: the signal bar is a new 20m low on {R.sig_is_low.mean():.2%}")
    print(f"CONTROL 2 (same-day exits, {len(t):,}): simulated exit second = the book's on {(t.ctl_exit_sec == t.exit_sec).mean():.2%}, "
          f"price within 1e-6 on {(np.abs(t.ctl_exit_px / t.exit_px - 1) < 1e-6).mean():.2%}; book overnight {(R.exit_sec >= CLOSE).sum()}, "
          f"simulated overnight {R.ctl_overnight.sum()}")
    print(f"done {time.time() - T0:.0f}s")

def report():
    def pf(r): l = -r[r < 0].sum(); return np.inf if l == 0 else r[r > 0].sum() / l
    def prep(p):
        R = pd.read_parquet(p); R['d'] = pd.to_datetime(R.td); R['u'] = R.pf1_fitted.clip(.125, 2)
        R['r_rev'] = R.ret_exit + 2 * 0.001 / R.entry_px
        for q in QS:
            tr = R[f'q{q}_sec'] >= 0
            R[f'r_q{q}'] = np.where(tr, R[f'q{q}_xpx'] / R[f'q{q}_px'] - 1 + 2 * 0.001 / R[f'q{q}_px'], 0.0)
            R[f't_q{q}'] = tr
        return R
    sets = [('2020-01 … 2026-08', prep(args.out))]
    if args.window:
        W = prep(args.window); sets += [('the window', W), ('from 2026-10-12', W[W.d >= '2026-10-12'])]
    for lab, R in sets:
        print(f"\n**{lab}**\n\n| entry | trades | mean | PF | worst | ≤ −20 % per 1,000 | % of equity | weekly median | worst 4 weeks | later than the reversal's exit |")
        print("|---|---|---|---|---|---|---|---|---|---|")
        for name, rc, tc in [('reversal (production)', 'r_rev', None)] + [(f'quiet {q} s', f'r_q{q}', f't_q{q}') for q in QS]:
            t = R if tc is None else R[R[tc]]
            p = 15 * R.u * R[rc]; w = p.groupby(R.d.dt.to_period('W-FRI')).sum(); w4 = w.rolling(4).sum()
            late = '' if tc is None else f"{(R.loc[R[tc], f'q{rc[3:]}_sec'] > R.loc[R[tc], 'exit_sec']).mean():.0%}"
            print(f"| {name} | {len(t):,} | {100*t[rc].mean():+.2f} % | {pf(t[rc].values):.3f} | {100*t[rc].min():+.1f} % | {1000*(t[rc] <= -.2).mean():.1f} | "
                  f"{p.sum():+.1f} | {w.median():+.2f} | {w4.min():+.2f} | {late} |")
        if lab.startswith('2020'):
            print("\n| year | reversal PF · % eq | " + " | ".join(f"quiet {q} s PF · % eq" for q in QS) + " |\n|---|---|" + "---|" * len(QS))
            for y, g in R.groupby(R.d.dt.year):
                cells = [f"{pf(g.r_rev.values):.3f} · {(15*g.u*g.r_rev).sum():+.0f}"] + \
                        [f"{pf(g[g[f't_q{q}']][f'r_q{q}'].values):.3f} · {(15*g.u*g[f'r_q{q}']).sum():+.0f}" for q in QS]
                print(f"| {y} | " + " | ".join(cells) + " |")
            q = 60; tr = R[f't_q{q}']
            dl = (R.loc[tr, f'q{q}_sec'] - R.loc[tr, 'entry_sec']); dp = R.loc[tr, f'q{q}_px'] / R.loc[tr, 'entry_px'] - 1
            print(f"\nquiet 60 s: entered a median {dl.median():.0f} s after the reversal fill (p90 {dl.quantile(.9):.0f} s) at a median "
                  f"{100*dp.median():+.2f} % (p10 {100*dp.quantile(.1):+.2f}, p90 {100*dp.quantile(.9):+.2f}); lower than the reversal on {(dp < 0).mean():.0%}; "
                  f"exit flags {R.loc[tr, 'q60_flag'].value_counts().to_dict()}")
            x = R.nsmallest(30, 'r_rev')
            print("the 30 worst reversal trades: reversal mean {:+.1f} % -> ".format(100 * x.r_rev.mean()) +
                  ", ".join(f"quiet {q} s {100*x[f'r_q{q}'].mean():+.1f} %" for q in QS))
        else:
            L = R.nsmallest(8, 'r_rev')
            print("\n" + L[['symbol', 'td', 'entry_px', 'q60_px', 'q60_xpx', 'r_rev', 'r_q60']].round(3).to_string(index=False))

report() if args.report else run()
